using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Mapivo.Core;

/// <summary>One live pad frame, normalized: sticks/triggers 0..65535, buttons as indices.</summary>
public sealed class PadFrame
{
    public bool Connected { get; set; }
    public string? Error { get; set; }
    public int LX, LY, RX, RY, LT, RT;
    public List<int> Buttons { get; } = new();
    public List<int> RawButtons { get; } = new();
    public Dictionary<string, string> Map { get; set; } = new();
}

public static class PadReader
{
    /// <summary>Window for DirectInput cooperative level; set once the app window exists.</summary>
    public static IntPtr Window;

    /// <summary>True when the pad has a rumble actuator the app can drive.</summary>
    public static bool CanVibrate(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token[0] == 'x' || token[0] == 'X')
            return int.TryParse(token.AsSpan(1), out var x) && XInputApi.IsConnected(x);
        var dev = Cached(token);
        return (dev != null && dev.CanVibrate) || HidRumble.CanVibrate(token);
    }

    static readonly object VibLock = new();
    static string? _buzzToken;
    static long _buzzUntil;
    static string? _holdToken;               // pad held by the "infinite" toggle
    static int _holdLevel = 3;
    static CancellationTokenSource? _holdCts;

    /// <summary>Whether the pad is buzzing right now (from a test pulse).</summary>
    public static bool IsBuzzing(string token) =>
        _buzzToken == token && Environment.TickCount64 < _buzzUntil;

    /// <summary>Strength of a test buzz: 1 light, 2 medium, 3 strong.</summary>
    static int LevelStrength(int level) => level <= 1 ? 11000 : level == 2 ? 20000 : 32767;

    /// <summary>A test buzz at a given level, or an infinite hold when ms is 0.</summary>
    public static bool TestVibrateLevel(string token, int level, int ms) =>
        ms <= 0 ? HoldVibrate(token, level) : TestVibrate(token, ms, level);

    /// <summary>Toggle an endless buzz. Second call with the same pad stops it.</summary>
    static bool HoldVibrate(string token, int level)
    {
        lock (VibLock)
        {
            if (_holdToken == token) { StopHoldLocked(); return true; }
            StopHoldLocked();
            if (!CanVibrate(token)) return false;

            _holdToken = token;
            _holdLevel = level;
            _holdCts = new CancellationTokenSource();
            var ct = _holdCts.Token;
            var tok = token;
            var lvl = level;
            Task.Run(() =>
            {
                // Same ceiling as the one-shot test: a weak buzz is below the
                // big motor's start threshold on many pads and only the small
                // motor turns, which looks like "endless shakes one side".
                int[] at = { 22000, 40000, 65535 };
                int[] hid = { 130, 200, 255 };
                int hidMode = -1;   // -1 undecided, 0 HID only, 1 DirectInput only
                bool kick = true;
                while (!ct.IsCancellationRequested)
                {
                    lock (VibLock)
                    {
                        _buzzToken = tok;
                        _buzzUntil = Environment.TickCount64 + 120;
                    }
                    if (tok[0] == 'x' || tok[0] == 'X')
                    {
                        if (!int.TryParse(tok.AsSpan(1), out var x) || !XInputApi.IsConnected(x)) break;
                        int v = Math.Min(at[Math.Clamp(lvl, 1, 3) - 1], 65535);
                        if (kick) { XInputApi.SetVibration(x, 65535, 65535); Thread.Sleep(90); kick = false; }
                        XInputApi.SetVibration(x, (ushort)v, (ushort)v);
                    }
                    else
                    {
                        var dev = Cached(tok);
                        int hv = hid[Math.Clamp(lvl, 1, 3) - 1];
                        bool hasHid = HidRumble.CanVibrate(tok);
                        // Never drive the HID report and the DirectInput effect
                        // at once: on pads that expose both they fight over the
                        // same motors and one side drops out, which is why the
                        // first endless buzz after a restart only shook one
                        // side while a later one shook both. Use the HID report
                        // when it exists (each motor gets its own value), and
                        // fall back to DirectInput only when it does not.
                        if (hidMode < 0) hidMode = hasHid ? 0 : 1;
                        if (hidMode == 0)
                        {
                            bool ok = HidRumble.Set(tok, (byte)hv, (byte)hv);
                            if (ok && kick)
                            {
                                // Start the lazy motor with a full-power pulse,
                                // then settle on the requested level.
                                HidRumble.Set(tok, 255, 255);
                                Thread.Sleep(90);
                                HidRumble.Set(tok, (byte)hv, (byte)hv);
                                kick = false;
                            }
                            if (!ok && dev != null && dev.CanVibrate)
                            {
                                hidMode = 1;
                                dev.StartRumble(hv * 257);
                            }
                        }
                        else if (dev != null && dev.CanVibrate)
                        {
                            // Keep the effect running instead of resting
                            // between ticks: stopping and restarting it every
                            // frame made the second press behave differently.
                            dev.StartRumble(hv * 257);
                        }
                    }
                    ct.WaitHandle.WaitOne(50);
                }
                lock (VibLock)
                {
                    if (tok[0] == 'x' || tok[0] == 'X')
                    {
                        if (int.TryParse(tok.AsSpan(1), out var x)) XInputApi.SetVibration(x, 0, 0);
                    }
                    else
                    {
                        HidRumble.Set(tok, 0, 0);
                        try { Cached(tok)?.StopRumble(); } catch { }
                    }
                }
            }, ct);
            return true;
        }
    }

    /// <summary>Stop an endless buzz, if one is running.</summary>
    public static void StopHold()
    {
        lock (VibLock) StopHoldLocked();
    }

    static void StopHoldLocked()
    {
        _holdCts?.Cancel();
        _holdCts = null;
        _holdToken = null;
        _buzzUntil = 0;
    }

    public static bool IsHolding(string token) => _holdToken == token;

    /// <summary>
    /// Drive the pad's DirectInput force-feedback axis, for pads that expose no
    /// usable HID output report. magnitude 0 stops it.
    /// </summary>
    public static bool RumbleFallback(string token, int magnitude)
    {
        if (string.IsNullOrEmpty(token) || token[0] == 'x' || token[0] == 'X') return false;
        try
        {
            var dev = Cached(token);
            if (dev == null || !dev.CanVibrate) return false;
            return magnitude <= 0 ? StopFallback(dev) : dev.StartRumble(magnitude);
        }
        catch { return false; }
    }

    /// <summary>
    /// Brief full-power pulse before a weaker command. Many pads' large motor
    /// has a high stiction threshold, so Low/Medium only turned the small one
    /// while High (near the ceiling) turned both. The kick gets the lazy motor
    /// spinning and it keeps going once the level drops back down.
    /// </summary>
    static void KickHid(string token, byte level)
    {
        HidRumble.Set(token, 255, 255);
        Thread.Sleep(90);
        HidRumble.Set(token, level, level);
    }

    static bool StopFallback(DiDevice dev)
    {
        dev.StopRumble();
        return true;
    }

    /// <summary>Buzz the pad once for a moment, as a wiring test.</summary>
    public static bool TestVibrate(string token, int ms = 500) => TestVibrate(token, ms, 3);

    public static bool TestVibrate(string token, int ms, int level)
    {
        if (string.IsNullOrEmpty(token)) return false;
        ms = Math.Clamp(ms, 50, 6000);
        int strength = LevelStrength(level);
        int hid = Math.Clamp(strength / 129, 0, 255);
        lock (VibLock)
        {
            _buzzToken = token;
            _buzzUntil = Environment.TickCount64 + ms;
            if (token[0] == 'x' || token[0] == 'X')
            {
                if (!int.TryParse(token.AsSpan(1), out var x) || !XInputApi.IsConnected(x)) return false;
                // 65535 is the hardware ceiling, but a one-shot command only
                // starts spinning the motor. Re-sending it every 50 ms keeps
                // the driver from resetting the effect, so the motor reaches
                // full speed and the test feels much stronger.
                var until = Environment.TickCount64 + ms;
                ushort v = (ushort)(strength * 2);
                // Same stiction trick as the HID path: a short burst at the
                // ceiling gets both motors spinning, then settle on the level.
                XInputApi.SetVibration(x, 65535, 65535);
                Thread.Sleep(90);
                while (Environment.TickCount64 < until)
                {
                    XInputApi.SetVibration(x, v, v);
                    _buzzUntil = Environment.TickCount64 + 60;
                    Thread.Sleep(50);
                }
                XInputApi.SetVibration(x, 0, 0);
                return true;
            }
            var dev = Cached(token);
            // HID first: it takes an independent value for each motor, so a
            // test buzz shakes both sides. The DirectInput effect is a single
            // force-feedback axis and only ever spins one motor, so it is only
            // a fallback for pads that expose no usable output report.
            // Drive both motors, and re-send while the test runs: the pads
            // that need this path keep the last command until told to stop.
            if (HidRumble.Set(token, (byte)hid, (byte)hid))
            {
                KickHid(token, (byte)hid);
                var until = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < until)
                {
                    Thread.Sleep(60);
                    _buzzUntil = Environment.TickCount64 + 60;
                    HidRumble.Set(token, (byte)hid, (byte)hid);
                }
                HidRumble.Set(token, 0, 0);
                return true;
            }
            if (dev != null && dev.CanVibrate && dev.TestVibrate(ms, strength)) return true;
            return false;
        }
    }

    public static readonly string[] ButtonNames =
    {
        "a", "b", "x", "y", "lb", "rb", "back", "start", "l3", "r3",
        "up", "down", "left", "right"
    };

    /// <summary>Read one frame. token "x0".."x3" = XInput, "jVVVV_PPPP" = DirectInput.</summary>
    public static PadFrame Read(string token, Dictionary<string, string>? map = null)
    {
        if (string.IsNullOrEmpty(token)) return new PadFrame { Error = "no pad selected" };
        if (token[0] == 'x' || token[0] == 'X')
            return int.TryParse(token.AsSpan(1), out var idx)
                ? ReadXIndex(idx)
                : new PadFrame { Error = "bad token" };

        var sony = SonyReader.Read(token);
        if (sony != null) return sony;
        return ReadDi(token, map);
    }

    public static PadFrame ReadXIndex(int i)
    {
        if (!XInputApi.TryGetState(i, out var s))
            return new PadFrame { Error = "slot " + (i + 1) + " not connected" };

        var f = new PadFrame { Connected = true };
        var g = s.Gamepad;
        f.LX = g.ThumbLX + 32768;
        f.LY = g.ThumbLY + 32768;
        f.RX = g.ThumbRX + 32768;
        f.RY = g.ThumbRY + 32768;
        f.LT = g.LeftTrigger * 257;
        f.RT = g.RightTrigger * 257;

        (ushort mask, int index)[] map =
        {
            (0x1000, 0), (0x2000, 1), (0x4000, 2), (0x8000, 3),
            (0x0100, 4), (0x0200, 5), (0x0020, 6), (0x0010, 7),
            (0x0040, 8), (0x0080, 9),
            (0x0001, 10), (0x0002, 11), (0x0004, 12), (0x0008, 13)
        };
        foreach (var (mask, index) in map)
            if ((g.Buttons & mask) != 0) f.Buttons.Add(index);
        return f;
    }

    /* ---------------- DirectInput ----------------
       One device per token, cached: enumerating objects and building a data
       format costs a few ms and the UI polls at ~8 Hz. */

    static readonly Dictionary<string, DiDevice> Cache = new();
    static readonly object CacheLock = new();

    static DiDevice? Cached(string token)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(token, out var dev) || dev.Dead)
            {
                dev = DiDevice.Open(token);
                Cache[token] = dev;
            }
            return dev;
        }
    }

    static PadFrame ReadDi(string token, Dictionary<string, string>? map)
    {
        try
        {
            var dev = Cached(token)!;
            if (dev.Error != null) return new PadFrame { Error = dev.Error };
            return dev.Read(map);
        }
        catch (Exception ex) { return new PadFrame { Error = ex.Message }; }
    }

    static string TokenOf(ref DeviceInstance inst)
    {
        // guidProduct.Data1 packs the vendor in the low word, same order the
        // raw-input scanner uses for "VID_PID".
        var bytes = inst.Product.ToByteArray();
        uint data1 = BitConverter.ToUInt32(bytes, 0);
        return "j" + (data1 & 0xFFFF).ToString("X4") + "_"
                   + ((data1 >> 16) & 0xFFFF).ToString("X4");
    }

    sealed class DiDevice
    {
        public string? Error;
        public bool Dead;

        IntPtr _vt;
        uint _size;
        bool _ff;
        IntPtr _eff;
        readonly Dictionary<Guid, int> _axes = new();
        readonly List<int> _buttons = new();
        int _pov = -1;
        bool _zStick;

        PtrArgFn _setFormat = null!;
        ObjCbFn _enumObjects = null!;
        SclFn _setCoop = null!;
        GpFn _getProp = null!;
        NoArgFn _acquire = null!;
        NoArgFn _poll = null!;
        GdsFn _getState = null!;
        GcFn _getCaps = null!;
        CeFn _createEffect = null!;
        SpFn _setParams = null!;
        EffFn _start = null!;
        NoArgEffFn _stop = null!;

        public static DiDevice Open(string token)
        {
            var d = new DiDevice();
            var di = Di.Instance;
            if (di == null) { d.Error = "DirectInput unavailable"; return d; }

            Guid found = Guid.Empty;
            EnumCallback cb = (ref DeviceInstance inst, IntPtr p) =>
            {
                if (TokenOf(ref inst) == token) { found = inst.Instance; return 0; }
                return 1;
            };
            IntPtr cbPtr = Marshal.GetFunctionPointerForDelegate(cb);
            di.EnumDevices(0x4, cbPtr, IntPtr.Zero, 0x1);
            GC.KeepAlive(cb);

            if (found == Guid.Empty) { d.Error = "pad not found"; return d; }
            return d.Attach(di, found);
        }

        DiDevice Attach(IDirectInput8W di, Guid instance)
        {
            var g = instance;
            if (di.CreateDevice(ref g, out var raw, IntPtr.Zero) != 0 || raw == IntPtr.Zero)
            { Error = "CreateDevice failed"; return this; }

            _vt = raw;
            _eff = IntPtr.Zero;
            _setFormat = Fn<PtrArgFn>(_vt, 11);
            _enumObjects = Fn<ObjCbFn>(_vt, 4);
            _getState = Fn<GdsFn>(_vt, 9);
            _setCoop = Fn<SclFn>(_vt, 13);
            _getProp = Fn<GpFn>(_vt, 5);
            _acquire = Fn<NoArgFn>(_vt, 7);
            _poll = Fn<NoArgFn>(_vt, 25);
            _getCaps = Fn<GcFn>(_vt, 10);
            _createEffect = Fn<CeFn>(_vt, 12);
            _start = Fn<EffFn>(_vt, 17);
            _stop = Fn<NoArgEffFn>(_vt, 19);
            _setParams = Fn<SpFn>(_vt, 20);

            var objs = new List<(Guid g, int ofs, uint type)>();
            ObjFn ocb = (IntPtr oi, IntPtr ctx) =>
            {
                Guid og = Marshal.PtrToStructure<Guid>(IntPtr.Add(oi, 4));
                int ofs = Marshal.ReadInt32(oi, 20);
                uint type = (uint)Marshal.ReadInt32(oi, 24);
                uint kind = type & 0xFF;
                // Collections carry no data and DirectInput rejects them in a format.
                if ((type & 0x80) != 0 || kind == 0x40) return 1;
                if ((type & 0x03) == 0 && (type & 0x04) == 0 && (type & 0x10) == 0) return 1;
                objs.Add((og, ofs, type));
                return 1;
            };
            IntPtr op = Marshal.GetFunctionPointerForDelegate(ocb);
            _enumObjects(_vt, op, IntPtr.Zero, 0);
            GC.KeepAlive(ocb);

            int dataSize = 0;
            foreach (var o in objs)
            {
                if ((o.type & 0x10) != 0) { _pov = o.ofs; dataSize = Math.Max(dataSize, o.ofs + 4); }
                else if ((o.type & 0x03) != 0)
                {
                    if (!_axes.ContainsKey(o.g)) _axes[o.g] = o.ofs;
                    dataSize = Math.Max(dataSize, o.ofs + 4);
                }
                else { _buttons.Add(o.ofs); dataSize = Math.Max(dataSize, o.ofs + 1); }
            }
            // dxDataSize is a DWORD; an unaligned size is rejected with E_INVALIDARG.
            _size = (uint)((dataSize + 3) & ~3);

            IntPtr fmt = BuildFormat(objs, _size);
            int hr = _setFormat(_vt, fmt);
            Marshal.FreeHGlobal(fmt);
            if (hr != 0) { Error = "SetDataFormat 0x" + hr.ToString("X8"); Dead = true; return this; }

            // A pad with only X/Y/Z/Rz (many PS3 adapters) puts the right
            // stick on Z/Rz and its triggers on buttons, so Z/Rz must not be
            // read as triggers -- that made the right stick look half-pressed.
            _zStick = !_axes.ContainsKey(GuidRx) && !_axes.ContainsKey(GuidRy)
                      && _axes.ContainsKey(GuidZ) && _axes.ContainsKey(GuidRz);
            _ranges[GuidZ] = AxisRange(_axes.TryGetValue(GuidZ, out var zofs) ? zofs : -1);
            _ranges[GuidRz] = AxisRange(_axes.TryGetValue(GuidRz, out var rzofs) ? rzofs : -1);

            IntPtr hwnd = Window;
            _setCoop(_vt, hwnd, 0x2 | 0x4);   // DISCL_BACKGROUND | DISCL_NONEXCLUSIVE
            if (_acquire(_vt) != 0) { Error = "pad busy (not acquired)"; Dead = true; return this; }

            var caps = Marshal.AllocHGlobal(44);
            try
            {
                Marshal.WriteInt32(caps, 0, 44);                      // dwSize
                if (_getCaps(_vt, caps) == 0)
                    _ff = (Marshal.ReadInt32(caps, 4) & 0x00000100) != 0;   // DIDC_FORCEFEEDBACK
            }
            finally { Marshal.FreeHGlobal(caps); }
            return this;
        }

        public bool CanVibrate => _ff;

        /// <summary>
        /// Play one constant rumble effect on the pad. The effect is created
        /// once and reused; each call only rewrites its magnitude.
        /// </summary>
        public bool TestVibrate(int ms) => TestVibrate(ms, 10000);

        public bool TestVibrate(int ms, int magnitude)
        {
            if (!_ff) return false;
            if (!EnsureEffect()) return false;

            IntPtr cfp = IntPtr.Zero, hdr = IntPtr.Zero;
            try
            {
                cfp = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(cfp, 0, Math.Clamp(magnitude, 0, 10000)); // DICONSTANTFORCE.lMagnitude
                hdr = Marshal.AllocHGlobal(32);
                Marshal.WriteInt32(hdr, 0, 32);               // dwSize
                Marshal.WriteInt32(hdr, 4, 4);                // cbTypeSpecificParams
                Marshal.WriteIntPtr(hdr, 8, cfp);             // lpvTypeSpecificParams
                if (_setParams(_eff, hdr, 0x00000080) != 0) return false;   // DIEP_TYPESPECIFICPARAMS
                if (_start(_eff, 1, 0) != 0) return false;
                Thread.Sleep(ms);
                _stop(_eff);
                return true;
            }
            finally
            {
                if (cfp != IntPtr.Zero) Marshal.FreeHGlobal(cfp);
                if (hdr != IntPtr.Zero) Marshal.FreeHGlobal(hdr);
            }
        }

        /// <summary>Start a constant effect that keeps running until StopRumble.</summary>
        public bool StartRumble(int magnitude)
        {
            if (!_ff) return false;
            if (!EnsureEffect()) return false;
            IntPtr cfp = IntPtr.Zero, hdr = IntPtr.Zero;
            try
            {
                cfp = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(cfp, 0, Math.Clamp(magnitude, 0, 10000));
                hdr = Marshal.AllocHGlobal(32);
                Marshal.WriteInt32(hdr, 0, 32);
                Marshal.WriteInt32(hdr, 4, 4);
                Marshal.WriteIntPtr(hdr, 8, cfp);
                if (_setParams(_eff, hdr, 0x00000080) != 0) return false;
                return _start(_eff, 1, 0) == 0;
            }
            finally
            {
                if (cfp != IntPtr.Zero) Marshal.FreeHGlobal(cfp);
                if (hdr != IntPtr.Zero) Marshal.FreeHGlobal(hdr);
            }
        }

        public void StopRumble()
        {
            if (_ff && _eff != IntPtr.Zero) { try { _stop(_eff); } catch { } }
        }

        bool EnsureEffect()
        {
            if (_eff != IntPtr.Zero) return true;
            {
                IntPtr eff = IntPtr.Zero, axes = IntPtr.Zero, dir = IntPtr.Zero, cf = IntPtr.Zero;
                try
                {
                    eff = Marshal.AllocHGlobal(DIEFFECT_SIZE);
                    axes = Marshal.AllocHGlobal(4);
                    dir = Marshal.AllocHGlobal(4);
                    cf = Marshal.AllocHGlobal(4);
                    Marshal.WriteInt32(axes, 0, 0);                       // DIJOFS_X
                    Marshal.WriteInt32(dir, 0, 0);                        // direction 0
                    Marshal.WriteInt32(cf, 0, 0);                         // initial magnitude
                    Marshal.WriteInt32(eff, 0, DIEFFECT_SIZE);            // dwSize
                    Marshal.WriteInt32(eff, 4, 0x00000101);               // CARTESIAN | OBJECTOFFSETS
                    Marshal.WriteInt32(eff, 8, unchecked((int)0x7FFFFFFF)); // dwDuration INFINITE
                    Marshal.WriteInt32(eff, 12, 0);                       // dwSamplePeriod
                    Marshal.WriteInt32(eff, 16, 10000);                   // dwGain
                    Marshal.WriteInt32(eff, 20, 0x00FFFFFF);              // DIEB_NOTRIGGER
                    Marshal.WriteInt32(eff, 24, 1);                       // cAxes
                    Marshal.WriteIntPtr(eff, 28, axes);                   // rgdwAxes
                    Marshal.WriteIntPtr(eff, 36, dir);                    // rglDirection
                    Marshal.WriteInt32(eff, 44, 0x00000080);              // dwFlags DIEFF_OBJECTIDS
                    Marshal.WriteInt32(eff, 48, 4);                       // cbTypeSpecificParams
                    Marshal.WriteIntPtr(eff, 56, cf);                     // lpvTypeSpecificParams
                    var kind = GuidConstantForce;
                    if (_createEffect(_vt, ref kind, eff, out _eff, IntPtr.Zero) != 0 || _eff == IntPtr.Zero)
                    { _eff = IntPtr.Zero; return false; }
                }
                finally
                {
                    if (eff != IntPtr.Zero) Marshal.FreeHGlobal(eff);
                    if (axes != IntPtr.Zero) Marshal.FreeHGlobal(axes);
                    if (dir != IntPtr.Zero) Marshal.FreeHGlobal(dir);
                    if (cf != IntPtr.Zero) Marshal.FreeHGlobal(cf);
                }
            }
            return _eff != IntPtr.Zero;
        }

        public PadFrame Read(Dictionary<string, string>? map)
        {
            _poll(_vt);
            IntPtr buf = Marshal.AllocHGlobal((int)_size);
            try
            {
                if (_getState(_vt, _size, buf) != 0)
                {
                    Dead = true;
                    return new PadFrame { Error = "lost pad, rescan" };
                }

                var f = new PadFrame { Connected = true };
                if (map != null) f.Map = map;
                f.LX = Axis(buf, GuidX, 32768);
                f.LY = 65535 - Axis(buf, GuidY, 32768);
                f.RX = _zStick ? Axis(buf, GuidZ, 32768) : Axis(buf, GuidRx, 32768);
                f.RY = 65535 - (_zStick ? Axis(buf, GuidRz, 32768) : Axis(buf, GuidRy, 32768));
                f.LT = _zStick ? 0 : Trigger(buf, GuidZ);
                f.RT = _zStick ? 0 : Trigger(buf, GuidRz);

                // Triggers are buttons on many pads (SDL's device database maps
                // them to bN). Report the mapped button as a full press so the
                // live test matches what the hook sends to the game.
                Func<string, int, int> trigBtn = (key, axis) =>
                {
                    if (!f.Map.TryGetValue(key, out var src) || string.IsNullOrEmpty(src)
                        || src[0] != 'b' || src.Length < 2 || src[1] < '0' || src[1] > '9') return axis;
                    int bi = int.Parse(src.Substring(1));
                    return bi >= 0 && bi < _buttons.Count && Marshal.ReadByte(buf, _buttons[bi]) != 0 ? 65535 : 0;
                };
                f.LT = trigBtn("lt", f.LT);
                f.RT = trigBtn("rt", f.RT);


                // Raw HID order maps 1:1 onto canonical buttons so the live test
                // and the hook agree. A pad whose printed labels differ is fixed
                // by learning the physical button (stored as "bN").
                for (int i = 0; i < _buttons.Count; i++)
                {
                    if (Marshal.ReadByte(buf, _buttons[i]) == 0) continue;
                    f.RawButtons.Add(i);
                    if (i < 10) f.Buttons.Add(i);
                }

                if (_pov >= 0)
                {
                    uint pov = (uint)Marshal.ReadInt32(buf, _pov);
                    if (pov != 0xFFFFFFFF)
                    {
                        int deg = (int)(pov / 100);
                        if (deg >= 315 || deg < 45) f.Buttons.Add(10);
                        if (deg >= 45 && deg < 135) f.Buttons.Add(13);
                        if (deg >= 135 && deg < 225) f.Buttons.Add(11);
                        if (deg >= 225 && deg < 315) f.Buttons.Add(12);
                    }
                }
                return f;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        readonly Dictionary<Guid, (int mn, int mx)> _ranges = new();

        // DIPROP_RANGE tells where the axis rests: 0 on a one-sided trigger,
        // the middle on a centred one. Without it an idle trigger reads full.
        (int mn, int mx) AxisRange(int ofs)
        {
            if (ofs < 0) return (0, 32767);
            IntPtr ph = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(ph, 0, 24);
                Marshal.WriteInt32(ph, 4, 16);
                Marshal.WriteInt32(ph, 8, ofs);
                Marshal.WriteInt32(ph, 12, 1);   // DIPH_BYOFFSET
                Marshal.WriteInt32(ph, 16, 0);
                Marshal.WriteInt32(ph, 20, 32767);
                // DIPROP_RANGE is a pseudo-GUID whose value is the small int 4.
                if (_getProp(_vt, (IntPtr)4, ph) != 0) return (0, 32767);
                return (Marshal.ReadInt32(ph, 16), Marshal.ReadInt32(ph, 20));
            }
            finally { Marshal.FreeHGlobal(ph); }
        }

        int Axis(IntPtr buf, Guid g, int fallback)
            => _axes.TryGetValue(g, out var ofs) ? Marshal.ReadInt32(buf, ofs) : fallback;

        // Triggers sit either on a 0..65535 rail or centre on 32767 depending on
        // the HID report, and DIPROP_RANGE is not exposed. Distance from centre
        // reads both as 0 at rest and 65535 fully pressed.
        int Trigger(IntPtr buf, Guid g)
        {
            if (!_axes.TryGetValue(g, out var ofs)) return 0;
            var (mn, mx) = _ranges.TryGetValue(g, out var r) ? r : (0, 32767);
            int rest = mn < 0 && mx > 0 ? 0 : mn;
            int span = Math.Max(rest - mn, mx - rest);
            if (span <= 0) return 0;
            int d = Math.Abs(Marshal.ReadInt32(buf, ofs) - rest);
            return Math.Min(65535, (int)((long)Math.Min(d, span) * 65535 / span));
        }
    }

    const int DIEFFECT_SIZE = 64;
    static readonly Guid GuidConstantForce = new("13541C20-8E33-11D0-9AD0-00A0C9A06E35");

    static readonly Guid GuidX = new("A36D02E0-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GuidY = new("A36D02E1-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GuidZ = new("A36D02E2-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GuidRz = new("A36D02E3-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GuidRx = new("A36D02F4-C9F3-11CF-BFC7-444553540000");
    static readonly Guid GuidRy = new("A36D02F5-C9F3-11CF-BFC7-444553540000");

    /// <summary>
    /// DIDATAFORMAT on x64 is 32 bytes: dwSize, dwObjSize, dwFlags, dwDataSize,
    /// dwNumObjs, then 4 bytes of padding, then the object array pointer at 24.
    /// </summary>
    static IntPtr BuildFormat(List<(Guid g, int ofs, uint type)> objs, uint dataSize)
    {
        var arr = Marshal.AllocHGlobal(24 * objs.Count);
        for (int i = 0; i < objs.Count; i++)
        {
            var gp = Marshal.AllocHGlobal(16);
            Marshal.StructureToPtr(objs[i].g, gp, false);
            Marshal.WriteIntPtr(arr, i * 24, gp);
            Marshal.WriteInt32(arr, i * 24 + 8, objs[i].ofs);
            Marshal.WriteInt32(arr, i * 24 + 12, (int)objs[i].type);
            Marshal.WriteInt32(arr, i * 24 + 16, 0);
        }
        var fp = Marshal.AllocHGlobal(32);
        Marshal.WriteInt32(fp, 0, 32);
        Marshal.WriteInt32(fp, 4, 24);
        Marshal.WriteInt32(fp, 8, 1);                       // DIDF_ABSAXIS
        Marshal.WriteInt32(fp, 12, (int)dataSize);
        Marshal.WriteInt32(fp, 16, objs.Count);
        Marshal.WriteIntPtr(fp, 24, arr);
        return fp;
    }

    /* ---------------- COM interop ---------------- */

    static T Fn<T>(IntPtr com, int slot)
        => Marshal.GetDelegateForFunctionPointer<T>(
               Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DeviceInstance
    {
        public uint Size;
        public Guid Instance;
        public Guid Product;
        public uint DevType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string InstanceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ProductName;
        public Guid FFDriver;
        public ushort UsagePage, Usage;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int EnumCallback(ref DeviceInstance inst, IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ObjFn(IntPtr obj, IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PtrArgFn(IntPtr self, IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ObjCbFn(IntPtr self, IntPtr cb, IntPtr r, uint f);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SclFn(IntPtr self, IntPtr h, uint f);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GpFn(IntPtr self, IntPtr prop, IntPtr ph);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GdsFn(IntPtr self, uint size, IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GcFn(IntPtr self, IntPtr caps);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CeFn(IntPtr self, ref Guid rguid, IntPtr eff, out IntPtr effect, IntPtr outer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SpFn(IntPtr self, IntPtr eff, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EffFn(IntPtr self, uint iterations, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgEffFn(IntPtr self);

    [ComImport, Guid("BF798031-483A-4DA2-AA99-5D64ED369700"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirectInput8W
    {
        [PreserveSig] int CreateDevice(ref Guid rguid, out IntPtr device, IntPtr outer);
        [PreserveSig] int EnumDevices(uint devType, IntPtr callback, IntPtr ref_, uint flags);
    }

    sealed class Di
    {
        public static IDirectInput8W? Instance
        {
            get
            {
                if (_inst != null) return _inst;
                var iid = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
                if (DirectInput8Create(GetModuleHandleW(null), 0x0800, ref iid, out var p, IntPtr.Zero) != 0)
                    return null;
                _inst = (IDirectInput8W)Marshal.GetObjectForIUnknown(p);
                Marshal.Release(p);
                return _inst;
            }
        }
        static IDirectInput8W? _inst;
    }

    [DllImport("dinput8.dll")]
    static extern int DirectInput8Create(IntPtr hinst, uint version, ref Guid iid, out IntPtr dev, IntPtr outer);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleW(string? name);

    /// <summary>
    /// PlayStation pads are read straight from their HID input reports. DirectInput
    /// sees the same pad, but its button numbering shifts between drivers and the
    /// triggers are often reported as buttons, so the report is the stable source.
    /// Returns null for tokens that are not known PlayStation layouts.
    /// </summary>
    static class SonyReader
    {
        sealed class Pad
        {
            public SonyHid.Device Device = new();
            public SonyHid.Kind Kind;
            public PadFrame Last = new() { Connected = false };
        }

        static readonly Dictionary<string, Pad> Cache = new();
        static readonly object Lock = new();

        public static PadFrame? Read(string token)
        {
            if (!TryParse(token, out var vid, out var pid)) return null;
            var kind = SonyHid.Classify(vid, pid);
            if (kind == SonyHid.Kind.None) return null;

            lock (Lock)
            {
                if (!Cache.TryGetValue(token, out var p))
                {
                    p = new Pad { Kind = kind };
                    Cache[token] = p;
                }
                if (!p.Device.Ready)
                {
                    var path = FindPath(vid, pid);
                    if (path == null) return new PadFrame { Error = "PlayStation pad not found" };
                    if (!p.Device.Open(path, kind, 0))
                    {
                        p.Device.Dispose();
                        p.Device = new SonyHid.Device();
                        return new PadFrame { Error = "PlayStation pad is busy" };
                    }
                }

                var rep = p.Device.Read(0);
                if (rep != null && rep.Length > 2)
                {
                    var f = SonyHid.Parse(p.Kind, rep, rep.Length);
                    if (f.Connected) p.Last = f;
                }
                if (!p.Device.Ready) p.Device.Dispose();
                return p.Last;
            }
        }

        static string? FindPath(ushort vid, ushort pid)
        {
            foreach (var path in HidPaths.Candidates(vid, pid))
                if (path.IndexOf("col", StringComparison.OrdinalIgnoreCase) < 0
                    || path.IndexOf("col01", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("col02", StringComparison.OrdinalIgnoreCase) >= 0)
                    return path;
            return null;
        }

        static bool TryParse(string token, out ushort vid, out ushort pid)
        {
            vid = pid = 0;
            if (string.IsNullOrEmpty(token) || (token[0] != 'j' && token[0] != 'J')) return false;
            var parts = token.Substring(1).Split('_');
            if (parts.Length < 2) return false;
            return ushort.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out vid)
                && ushort.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out pid);
        }

        public static void Forget(string token)
        {
            lock (Lock)
            {
                if (Cache.Remove(token, out var p)) p.Device.Dispose();
            }
        }
    }
}
