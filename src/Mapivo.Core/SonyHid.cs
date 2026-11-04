using System.Runtime.InteropServices;
using System.Threading;

namespace Mapivo.Core;

/// <summary>
/// Native PlayStation pad support over HID output/input reports.
///
/// Rumble is an output report the pad firmware understands, so it works even
/// when DirectInput exposes no force-feedback axis and even when another driver
/// hides the pad from RawInput. DualSense adaptive triggers ride the same report
/// as a pair of 11-byte effect blocks; no PC game supplies them, so they are
/// synthesized from the game's trigger depth and rumble.
///
/// Protocol reference: SDL_hidapi_ps3/ps4/ps5.c.
/// </summary>
public static class SonyHid
{
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ_WRITE = 0x00000003;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint WAIT_OBJECT_0 = 0;
    const uint ERROR_IO_PENDING = 997;
    const int ERROR_INVALID_HANDLE = 6;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, uint toRead, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(IntPtr h, IntPtr overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIo(IntPtr h);
    [DllImport("kernel32.dll")]
    static extern IntPtr CreateEventW(IntPtr attr, bool manual, bool initial, IntPtr name);
    [DllImport("kernel32.dll")]
    static extern bool ResetEvent(IntPtr e);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr e, uint ms);
    [DllImport("hid.dll")]
    static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr pp, ref HIDP_CAPS caps);
    [DllImport("hid.dll")]
    static extern bool HidD_SetOutputReport(IntPtr h, byte[] report, uint len);
    [DllImport("hid.dll")]
    static extern bool HidD_GetFeature(IntPtr h, byte[] buffer, uint len);
    [DllImport("hid.dll")]
    static extern bool HidD_SetFeature(IntPtr h, byte[] buffer, uint len);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps,
            NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps,
            NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    public enum Kind { None, Ps3, Ps4, Ps5 }

    /// <summary>Known DualSense / DualSense Edge / DualShock 4 / DualShock 3 ids.</summary>
    public static Kind Classify(ushort vid, ushort pid)
    {
        if (vid == 0x054C) return pid switch
        {
            0x0CE6 or 0x0DF2 or 0x0E5F or 0x0BA0 => Kind.Ps5,
            0x05C4 or 0x09CC or 0x05C5 or 0x0CDA => Kind.Ps4,
            0x0268 or 0x0269 => Kind.Ps3,
            _ => Kind.None,
        };
        // Third-party pads that speak the Sony layouts.
        return (vid, pid) switch
        {
            (0x054C, _) => Kind.None,
            (0x0F0D, 0x00C1) => Kind.Ps3,
            (0x0F0D, 0x005D) => Kind.Ps3,
            (0x0F0D, 0x0066) => Kind.Ps3,
            (0x0F0D, 0x00F0) => Kind.Ps4,
            (0x0F0D, 0x00F1) => Kind.Ps4,
            (0x0F0D, 0x00F2) => Kind.Ps4,
            (0x0F0D, 0x00F3) => Kind.Ps4,
            (0x0F0D, 0x00F4) => Kind.Ps4,
            (0x0F0D, 0x00F5) => Kind.Ps4,
            (0x0F0D, 0x00F6) => Kind.Ps4,
            (0x0F0D, 0x00F7) => Kind.Ps4,
            (0x0F0D, 0x00F8) => Kind.Ps4,
            (0x0F0D, 0x00F9) => Kind.Ps4,
            (0x0F0D, 0x00FA) => Kind.Ps4,
            _ => Kind.None,
        };
    }

    /* ---------------- CRC32 ----------------
       The HIDP Bluetooth output header 0xA2 is part of the checksum, so the
       same routine serves every report. IEEE polynomial, no final xor. */

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    static uint Crc32(uint crc, byte[] data, int offset, int count)
    {
        for (int i = 0; i < count; i++)
            crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Append the Bluetooth packet CRC at the end of a 78-byte report.</summary>
    static void SealBluetooth(byte[] rep, int size)
    {
        uint crc = Crc32(0, new[] { (byte)0xA2 }, 0, 1);
        crc = Crc32(crc, rep, 0, size - 4);
        rep[size - 4] = (byte)(crc & 0xFF);
        rep[size - 3] = (byte)((crc >> 8) & 0xFF);
        rep[size - 2] = (byte)((crc >> 16) & 0xFF);
        rep[size - 1] = (byte)((crc >> 24) & 0xFF);
    }

    /* ---------------- trigger effects ----------------
       An effect block is 11 bytes: a mode byte then ten parameters. The zones
       are ten 1/10 segments of trigger travel; each zone carries a 3-bit
       strength. Nothing on PC sends these, so they are built here. */

    public static void EffectOff(byte[] dst, int at)
    {
        Array.Clear(dst, at, 11);
        dst[at] = 0x05;
    }

    /// <summary>
    /// Constant resistance over the part of the travel the player pushed past:
    /// the harder the trigger is pulled the further the wall follows it, which
    /// feels like a real trigger stop. strength 1..8.
    /// </summary>
    public static void EffectFeedback(byte[] dst, int at, int position, int strength)
    {
        if (strength <= 0 || position >= 9) { EffectOff(dst, at); return; }
        position = Math.Clamp(position, 0, 9);
        strength = Math.Clamp(strength, 1, 8);
        Array.Clear(dst, at, 11);
        uint active = 0, force = 0;
        for (int i = position; i < 10; i++)
        {
            active |= 1u << i;
            force |= (uint)((strength - 1) & 7) << (3 * i);
        }
        dst[at] = 0x21;
        dst[at + 1] = (byte)(active & 0xFF);
        dst[at + 2] = (byte)((active >> 8) & 0xFF);
        dst[at + 3] = (byte)(force & 0xFF);
        dst[at + 4] = (byte)((force >> 8) & 0xFF);
        dst[at + 5] = (byte)((force >> 16) & 0xFF);
        dst[at + 6] = (byte)((force >> 24) & 0xFF);
    }

    /// <summary>Vibration over a slice of the travel: simulates a gun kick. freq 1..255 Hz.</summary>
    public static void EffectVibration(byte[] dst, int at, int position, int strength, int frequency)
    {
        if (strength <= 0 || position >= 10) { EffectOff(dst, at); return; }
        position = Math.Clamp(position, 0, 9);
        strength = Math.Clamp(strength, 1, 8);
        frequency = Math.Clamp(frequency, 1, 255);
        Array.Clear(dst, at, 11);
        uint active = 0, force = 0;
        for (int i = position; i < 10; i++)
        {
            active |= 1u << i;
            force |= (uint)((strength - 1) & 7) << (3 * i);
        }
        dst[at] = 0x26;
        dst[at + 1] = (byte)(active & 0xFF);
        dst[at + 2] = (byte)((active >> 8) & 0xFF);
        dst[at + 3] = (byte)(force & 0xFF);
        dst[at + 4] = (byte)((force >> 8) & 0xFF);
        dst[at + 5] = (byte)((force >> 16) & 0xFF);
        dst[at + 6] = (byte)((force >> 24) & 0xFF);
        dst[at + 7] = (byte)frequency;
    }

    /// <summary>How deep the trigger is, 0..9, from a 0..65535 axis.</summary>
    public static int ZoneOf(int axis) => Math.Clamp(axis * 10 / 65536, 0, 9);

    /// <summary>Effect strength from rumble magnitude, 0..8.</summary>
    public static int StrengthOf(byte motor) => motor == 0 ? 0 : Math.Clamp(1 + motor * 7 / 255, 1, 8);

    /* ---------------- report build ----------------
       Returns the exact bytes and length to hand HidD_SetOutputReport, or null
       when the pad is too new to know. */

    public sealed class Plan
    {
        public byte[] Report = Array.Empty<byte>();
        public int Length;
    }

    /// <summary>
    /// True when this pad is connected over Bluetooth. Windows exposes BT HID
    /// interfaces under the Bluetooth HID service GUID, while USB pads hang off
    /// the plain HID class and carry a usb vid/pid in the instance id.
    /// </summary>
    public static bool IsBluetooth(string path) =>
        path.IndexOf("00001124", StringComparison.OrdinalIgnoreCase) >= 0
        || path.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0
        || path.IndexOf("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Rumble report for a DS3/DS4/DualSense. left is the large motor (the one
    /// games call low frequency), right the small one. hs is DualSense
    /// headphone volume, ss speaker, ms mic, all 0..127 (128 = untouched).
    /// </summary>
    public static Plan? Rumble(Kind kind, bool bt, byte left, byte right, int player,
        byte[]? rightTrigger = null, byte[]? leftTrigger = null)
    {
        switch (kind)
        {
            case Kind.Ps3:
            {
                var rep = new byte[49];
                rep[0] = 0x01;
                rep[1] = 0xFF;
                rep[2] = (byte)(right != 0 ? 1 : 0);
                rep[3] = 0xFF;
                rep[4] = left;
                rep[9] = (byte)(0x01 << (1 + (Math.Clamp(player, 0, 3) % 4)));
                return new Plan { Report = rep, Length = 49 };
            }
            case Kind.Ps4:
            {
                int size = bt ? 78 : 32;
                var rep = new byte[size];
                int mask = 0x01 | 0x02;      // rumble + LED
                int off;
                if (bt)
                {
                    rep[0] = 0x11;
                    rep[1] = 0xC0;           // HID + CRC, update interval 0
                    rep[3] = (byte)mask;
                    off = 6;
                    SealBluetooth(rep, size);
                }
                else
                {
                    rep[0] = 0x05;
                    rep[1] = (byte)mask;
                    off = 4;
                }
                // DS4EffectsState_t: right motor, left motor, then LED RGB.
                rep[off + 0] = right;
                rep[off + 1] = left;
                rep[off + 2] = 0x00;
                rep[off + 3] = 0x00;
                rep[off + 4] = (byte)(0x10 + (Math.Clamp(player, 0, 3) * 0x16));
                return new Plan { Report = rep, Length = size };
            }
            case Kind.Ps5:
            {
                int size = bt ? 78 : 48;
                var rep = new byte[size];
                int off = bt ? 3 : 1;
                if (bt)
                {
                    rep[0] = 0x31;
                    rep[1] = 0x00;           // tag + sequence
                    rep[2] = 0x10;           // magic
                }
                else rep[0] = 0x02;

                // DS5EffectsState_t at off.
                if (left != 0 || right != 0)
                {
                    rep[off + 0] |= 0x01;    // rumble emulation
                    rep[off + 1] |= 0x02;    // disable audio haptics while rumbling
                    rep[off + 2] = (byte)(right >> 1);
                    rep[off + 3] = (byte)(left >> 1);
                }
                // Lightbar so a player can tell the pads apart.
                rep[off + 1] |= 0x04;
                SetLedForPlayer(rep, off, player);

                if (rightTrigger != null) Array.Copy(rightTrigger, 0, rep, off + 10, 11);
                else EffectOff(rep, off + 10);
                if (leftTrigger != null) Array.Copy(leftTrigger, 0, rep, off + 21, 11);
                else EffectOff(rep, off + 21);

                if (bt) SealBluetooth(rep, size);
                return new Plan { Report = rep, Length = size };
            }
        }
        return null;
    }

    static void SetLedForPlayer(byte[] rep, int off, int player)
    {
        // Values below 0x40 keep the lightbar from washing out.
        (byte r, byte g, byte b) c = Math.Clamp(player, 0, 6) switch
        {
            0 => (0x00, 0x00, 0x40),   // blue
            1 => (0x40, 0x00, 0x00),   // red
            2 => (0x00, 0x40, 0x00),   // green
            3 => (0x20, 0x00, 0x20),   // pink
            4 => (0x20, 0x10, 0x00),   // orange
            5 => (0x00, 0x10, 0x10),   // teal
            _ => (0x10, 0x10, 0x10),   // white
        };
        rep[off + 44] = c.r;
        rep[off + 45] = c.g;
        rep[off + 46] = c.b;
    }

    /* ---------------- device ----------------
       One open pad. USB opens as a blocking handle and is read with an OVERLAPPED
       structure plus event, so a pad that has not reported yet does not hang the
       UI thread that holds the cache lock. */

    public sealed class Device : IDisposable
    {
        public IntPtr Handle = new(-1);
        public Kind Kind;
        public bool Bluetooth;
        public int InputLength, OutputLength;
        public string Path = "";
        public int Player;
        IntPtr _ev;
        IntPtr _ov;
        readonly object _readLock = new();
        bool _pending;

        public bool Ready => Handle != new IntPtr(-1);
        public bool CanMap => InputLength > 2;

        public bool Open(string path, Kind kind, int player)
        {
            // Overlapped so a pad that has not reported yet is a timeout, not a hang.
            var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == new IntPtr(-1))
                h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1))
                h = CreateFileW(path, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == new IntPtr(-1)) return false;

            if (!HidD_GetPreparsedData(h, out var pp)) { CloseHandle(h); return false; }
            var caps = new HIDP_CAPS();
            HidP_GetCaps(pp, ref caps);
            HidD_FreePreparsedData(pp);
            if (caps.OutputReportByteLength < 2) { CloseHandle(h); return false; }

            Handle = h;
            Kind = kind;
            Path = path;
            Player = player;
            Bluetooth = IsBluetooth(path);
            InputLength = caps.InputReportByteLength;
            OutputLength = caps.OutputReportByteLength;
            _ev = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            _ov = Marshal.AllocHGlobal(32);
            Marshal.WriteInt64(_ov, 0, 0);
            Marshal.WriteInt64(_ov, 8, 0);
            Marshal.WriteIntPtr(_ov, 16, _ev);
            EnterEnhancedMode();
            return true;
        }

        /// <summary>
        /// DualSense/DS4 ignore the effects report until they see one, and the
        /// DualSense needs a bare effect packet to leave its default reporting
        /// mode. Send a harmless zeroed one right after opening.
        /// </summary>
        void EnterEnhancedMode()
        {
            if (Kind == Kind.Ps5)
            {
                var rep = new byte[Bluetooth ? 78 : 48];
                if (Bluetooth)
                {
                    rep[0] = 0x31;
                    rep[2] = 0x10;
                    SealBluetooth(rep, 78);
                }
                else rep[0] = 0x02;
                HidD_SetOutputReport(Handle, rep, (uint)rep.Length);
            }
            else if (Kind == Kind.Ps4 && Bluetooth)
            {
                var rep = new byte[78];
                rep[0] = 0x11;
                rep[1] = 0xC0;
                SealBluetooth(rep, 78);
                HidD_SetOutputReport(Handle, rep, 78);
            }
        }

        public bool Send(Plan plan)
        {
            if (!Ready) return false;
            lock (_readLock)
                return HidD_SetOutputReport(Handle, plan.Report, (uint)plan.Length);
        }

        public bool Rumble(byte left, byte right, byte[]? rt = null, byte[]? lt = null)
        {
            var plan = SonyHid.Rumble(Kind, Bluetooth, left, right, Player, rt, lt);
            return plan != null && Send(plan);
        }

        /// <summary>
        /// Latest input report, or null when nothing new arrived in waitMs. One
        /// read stays pending between calls, so a 10 ms poll costs a wait and a
        /// copy when new data exists, and nothing at all when it does not.
        /// </summary>
        byte[]? _buf;
        uint _bufGot;

        public byte[]? Read(int waitMs)
        {
            if (!Ready || InputLength < 3) return null;
            lock (_readLock)
            {
                if (!_pending)
                {
                    if (_buf == null || _buf.Length != InputLength) _buf = new byte[InputLength];
                    ResetEvent(_ev);
                    bool ok = ReadFile(Handle, _buf, (uint)_buf.Length, out var got, _ov);
                    int err = ok ? 0 : Marshal.GetLastWin32Error();
                    if (ok)
                    {
                        _pending = false;
                        _bufGot = got;
                    }
                    else if (err == ERROR_IO_PENDING)
                    {
                        _pending = true;
                        _bufGot = 0;
                    }
                    else
                    {
                        _pending = false;
                        return null;
                    }
                }
                if (_pending)
                {
                    uint w = WaitForSingleObject(_ev, (uint)Math.Max(0, waitMs));
                    if (w != WAIT_OBJECT_0) return null;
                    if (!GetOverlappedResult(Handle, _ov, out var transferred, false) || transferred == 0)
                    {
                        _pending = false;
                        return null;
                    }
                    _pending = false;
                    _bufGot = transferred;
                }
                var outBuf = new byte[_bufGot];
                Array.Copy(_buf!, outBuf, (int)_bufGot);
                return outBuf;
            }
        }

        public void Dispose()
        {
            if (Ready)
            {
                try { CancelIo(Handle); } catch { }
                CloseHandle(Handle);
            }
            Handle = new IntPtr(-1);
            if (_ov != IntPtr.Zero) { Marshal.FreeHGlobal(_ov); _ov = IntPtr.Zero; }
            if (_ev != IntPtr.Zero) { CloseHandle(_ev); _ev = IntPtr.Zero; }
        }
    }

    /* ---------------- input parsing ---------------- */

    /// <summary>Turn a raw input report into a normalized PadFrame.</summary>
    public static PadFrame Parse(Kind kind, byte[] rep, int len)
    {
        var f = new PadFrame { Connected = true };
        switch (kind)
        {
            case Kind.Ps3: ParsePs3(f, rep, len); break;
            case Kind.Ps4: ParsePs4(f, rep, len); break;
            case Kind.Ps5: ParsePs5(f, rep, len); break;
            default: return new PadFrame { Error = "not a PlayStation pad" };
        }
        return f;
    }

    static int B2A(int b) => b * 257;

    static void ParsePs3(PadFrame f, byte[] d, int len)
    {
        if (len < 7) { f.Connected = false; return; }
        if (d[0] != 0x01 || d[1] == 0xFF) { f.Connected = false; return; }

        // Mini packet (ShanWan PS2 -> PS3): different layout, tiny size.
        if (len <= 9)
        {
            f.RX = B2A(d[0]); f.RY = B2A(d[1]);
            f.LX = B2A(d[2]); f.LY = B2A(d[3]);
            int face = d[4], sh = d[5];
            AddHat(f, face & 0x0F);
            // Same nibble order as the full PS3 packet.
            AddFacePs3(f, face >> 4);
            if ((sh & 0x01) != 0) { f.Buttons.Add(4); f.RawButtons.Add(6); }
            if ((sh & 0x02) != 0) { f.Buttons.Add(5); f.RawButtons.Add(7); }
            if ((sh & 0x10) != 0) { f.Buttons.Add(6); f.RawButtons.Add(9); }
            if ((sh & 0x20) != 0) { f.Buttons.Add(7); f.RawButtons.Add(8); }
            if ((sh & 0x40) != 0) { f.Buttons.Add(8); f.RawButtons.Add(10); }
            if ((sh & 0x80) != 0) { f.Buttons.Add(9); f.RawButtons.Add(11); }
            f.LT = (sh & 0x04) != 0 ? 65535 : 0;
            f.RT = (sh & 0x08) != 0 ? 65535 : 0;
            return;
        }

        if (len < 20) { f.Connected = false; return; }
        int b2 = d[2], b3 = d[3];
        if ((b2 & 0x01) != 0) f.Buttons.Add(6);   // Back / Select
        if ((b2 & 0x02) != 0) f.Buttons.Add(8);   // L3
        if ((b2 & 0x04) != 0) f.Buttons.Add(9);   // R3
        if ((b2 & 0x08) != 0) f.Buttons.Add(7);   // Start
        if ((b2 & 0x10) != 0) f.Buttons.Add(10);
        if ((b2 & 0x20) != 0) f.Buttons.Add(13);
        if ((b2 & 0x40) != 0) f.Buttons.Add(11);
        if ((b2 & 0x80) != 0) f.Buttons.Add(12);
        if ((b3 & 0x04) != 0) { f.Buttons.Add(4); f.RawButtons.Add(6); }   // L1
        if ((b3 & 0x08) != 0) { f.Buttons.Add(5); f.RawButtons.Add(7); }   // R1
        if ((b2 & 0x01) != 0) f.RawButtons.Add(9);   // Back on SDL's numbering
        if ((b2 & 0x08) != 0) f.RawButtons.Add(8);   // Start
        if ((b2 & 0x02) != 0) f.RawButtons.Add(10);  // L3
        if ((b2 & 0x04) != 0) f.RawButtons.Add(11);  // R3
        if ((d[4] & 0x01) != 0) f.RawButtons.Add(12); // Guide / PS
        AddFacePs3(f, b3 >> 4);

        f.LX = B2A(d[6]); f.LY = B2A(d[7]);
        f.RX = B2A(d[8]); f.RY = B2A(d[9]);
        f.LT = B2A(d[18]);
        f.RT = B2A(d[19]);
    }

    static void ParsePs4(PadFrame f, byte[] d, int len)
    {
        int start;
        if (d[0] == 0x01) start = 1;
        else if (d[0] >= 0x11 && d[0] <= 0x19)
        {
            if (len < 10 || (d[1] & 0x80) == 0) { f.Connected = false; return; }
            start = 3;
        }
        else { f.Connected = false; return; }

        if (len < start + 9) { f.Connected = false; return; }
        int p = start;
        f.LX = B2A(d[p]); f.LY = B2A(d[p + 1]);
        f.RX = B2A(d[p + 2]); f.RY = B2A(d[p + 3]);
        int b0 = d[p + 4], b1 = d[p + 5], b2 = d[p + 6];
        AddFaceRaw(f, b0 >> 4, false);
        AddHat(f, b0 & 0x0F);
        if ((b1 & 0x01) != 0) { f.Buttons.Add(4); f.RawButtons.Add(4); }
        if ((b1 & 0x02) != 0) { f.Buttons.Add(5); f.RawButtons.Add(5); }
        if ((b1 & 0x10) != 0) { f.Buttons.Add(6); f.RawButtons.Add(8); }
        if ((b1 & 0x20) != 0) { f.Buttons.Add(7); f.RawButtons.Add(9); }
        if ((b1 & 0x40) != 0) { f.Buttons.Add(8); f.RawButtons.Add(10); }
        if ((b1 & 0x80) != 0) { f.Buttons.Add(9); f.RawButtons.Add(11); }
        if ((b2 & 0x01) != 0) f.RawButtons.Add(12);   // PS
        if ((b2 & 0x02) != 0) f.RawButtons.Add(13);   // touchpad
        int lt = d[p + 7], rt = d[p + 8];
        f.LT = (lt == 0 && (b1 & 0x04) != 0) ? 65535 : B2A(lt);
        f.RT = (rt == 0 && (b1 & 0x08) != 0) ? 65535 : B2A(rt);
    }

    static void ParsePs5(PadFrame f, byte[] d, int len)
    {
        int start;
        if (d[0] == 0x01)
        {
            if (len == 10 || len == 78)
            {
                // Simple packet: sticks, three button bytes, two triggers.
                if (len < 9) { f.Connected = false; return; }
                int q = 1;
                f.LX = B2A(d[q]); f.LY = B2A(d[q + 1]);
                f.RX = B2A(d[q + 2]); f.RY = B2A(d[q + 3]);
                // SDL's simple packet: sticks, one buttons+hat byte, then the
                // two trigger depths. Shoulders are not part of it.
                int c0 = d[q + 4];
                AddFaceRaw(f, c0 >> 4, false);
                AddHat(f, c0 & 0x0F);
                f.LT = B2A(d[q + 5]);
                f.RT = B2A(d[q + 6]);
                return;
            }
            start = 1;
        }
        else if (d[0] == 0x31)
        {
            if (len < 12) { f.Connected = false; return; }
            start = 2;
        }
        else { f.Connected = false; return; }

        if (len < start + 10) { f.Connected = false; return; }
        int p = start;
        f.LX = B2A(d[p]); f.LY = B2A(d[p + 1]);
        f.RX = B2A(d[p + 2]); f.RY = B2A(d[p + 3]);
        int lt = d[p + 4], rt = d[p + 5];
        int a = d[p + 7], b = d[p + 8], c = d[p + 9];
        AddFaceRaw(f, a >> 4, false);
        AddHat(f, a & 0x0F);
        if ((b & 0x01) != 0) { f.Buttons.Add(4); f.RawButtons.Add(4); }
        if ((b & 0x02) != 0) { f.Buttons.Add(5); f.RawButtons.Add(5); }
        if ((b & 0x10) != 0) { f.Buttons.Add(6); f.RawButtons.Add(8); }
        if ((b & 0x20) != 0) { f.Buttons.Add(7); f.RawButtons.Add(9); }
        if ((b & 0x40) != 0) { f.Buttons.Add(8); f.RawButtons.Add(10); }
        if ((b & 0x80) != 0) { f.Buttons.Add(9); f.RawButtons.Add(11); }
        if ((c & 0x01) != 0) f.RawButtons.Add(12);
        if ((c & 0x02) != 0) f.RawButtons.Add(13);
        if ((c & 0x04) != 0) f.RawButtons.Add(14);
        if ((c & 0x40) != 0) f.RawButtons.Add(15);
        if ((c & 0x80) != 0) f.RawButtons.Add(16);
        f.LT = (lt == 0 && (b & 0x04) != 0) ? 65535 : B2A(lt);
        f.RT = (rt == 0 && (b & 0x08) != 0) ? 65535 : B2A(rt);
    }

    /// <summary>
    /// PS3 reports the shape buttons in a different nibble order than PS4/PS5:
    /// 0x10 triangle, 0x20 circle, 0x40 cross, 0x80 square.
    /// </summary>
    static void AddFacePs3(PadFrame f, int nibble)
    {
        if ((nibble & 0x01) != 0) { f.Buttons.Add(3); f.RawButtons.Add(0); } // triangle
        if ((nibble & 0x02) != 0) { f.Buttons.Add(1); f.RawButtons.Add(1); } // circle
        if ((nibble & 0x04) != 0) { f.Buttons.Add(0); f.RawButtons.Add(2); } // cross
        if ((nibble & 0x08) != 0) { f.Buttons.Add(2); f.RawButtons.Add(3); } // square
    }

    /// <summary>
    /// Face buttons in the Xbox layout, plus the raw button index the pad's own
    /// SDL controller mapping uses. The bundled per-device maps are written in
    /// that numbering, so emitting it lets them work unchanged.
    /// </summary>
    static void AddFaceRaw(PadFrame f, int nibble, bool ps3)
    {
        if ((nibble & 0x01) != 0) { f.Buttons.Add(2); f.RawButtons.Add(ps3 ? 3 : 0); } // square
        if ((nibble & 0x02) != 0) { f.Buttons.Add(0); f.RawButtons.Add(ps3 ? 2 : 1); } // cross
        if ((nibble & 0x04) != 0) { f.Buttons.Add(1); f.RawButtons.Add(ps3 ? 1 : 2); } // circle
        if ((nibble & 0x08) != 0) { f.Buttons.Add(3); f.RawButtons.Add(ps3 ? 0 : 3); } // triangle
    }

    /// <summary>Face buttons cross and circle are swapped for the Xbox layout.</summary>
    static void AddFace(PadFrame f, int nibble)
    {
        if ((nibble & 0x01) != 0) f.Buttons.Add(2);   // square -> X
        if ((nibble & 0x02) != 0) f.Buttons.Add(0);   // cross  -> A
        if ((nibble & 0x04) != 0) f.Buttons.Add(1);   // circle -> B
        if ((nibble & 0x08) != 0) f.Buttons.Add(3);   // triangle -> Y
    }

    static void AddHat(PadFrame f, int hat)
    {
        switch (hat)
        {
            case 0: f.Buttons.Add(10); break;
            case 1: f.Buttons.Add(10); f.Buttons.Add(13); break;
            case 2: f.Buttons.Add(13); break;
            case 3: f.Buttons.Add(13); f.Buttons.Add(11); break;
            case 4: f.Buttons.Add(11); break;
            case 5: f.Buttons.Add(11); f.Buttons.Add(12); break;
            case 6: f.Buttons.Add(12); break;
            case 7: f.Buttons.Add(12); f.Buttons.Add(10); break;
        }
    }

}
