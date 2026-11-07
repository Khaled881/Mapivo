
using Mapivo.Core;

namespace Mapivo.App;

/// <summary>
/// Feeds the virtual controllers from physical pads while the user keeps the
/// driver switched on. Reads pads only through XInput / DirectInput; it never
/// touches the keyboard, so a game keeps receiving its keys normally.
/// </summary>
sealed class DriverEngine
{
    public sealed class Route
    {
        public int Index;
        public string Token = "";
        public Dictionary<string, string> Map = new();
    }

    public const int TickMs = 10;

    static readonly string[] Btn =
    {
        "a", "b", "x", "y", "lb", "rb", "back", "start", "l3", "r3",
        "up", "down", "left", "right"
    };

    static readonly string[] DirDefault =
    {
        "ly+", "ly-", "lx-", "lx+", "ry+", "ry-", "rx-", "rx+"
    };

    static readonly string[] Dir = { "lu", "ld", "ll", "lr", "ru", "rd", "rl", "rr" };

    readonly List<Route> _routes = new();
    readonly int[][] _src = new int[VirtualPadManager.MaxSlots][];
    Thread? _thread;
    volatile bool _running;

    public bool Running => _running;
    public string Error { get; private set; } = "";

    public bool Start(List<Route> routes)
    {
        Stop();
        if (routes.Count == 0) { Error = "No controller turned on"; return false; }

        string problem = VirtualPadManager.DriverProblem();
        if (problem.Length > 0) { Error = problem; return false; }

        for (int i = 0; i < VirtualPadManager.MaxSlots; i++)
            _src[i] = new int[VirtualPadManager.SourceCount];

        foreach (var r in routes)
        {
            string err = VirtualPadManager.Attach(r.Index);
            if (err.Length > 0) { Error = err; Stop(); return false; }
            VirtualPadManager.SetSource(r.Index, r.Token);
        }

        _routes.Clear();
        _routes.AddRange(routes);
        Error = "";
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Mapivo virtual pads" };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        _running = false;
        var t = _thread;
        _thread = null;
        if (t != null)
        {
            try { t.Join(400); } catch { }
        }
        for (int i = 0; i < VirtualPadManager.MaxSlots; i++)
        {
            Zero(i);
            VirtualPadManager.Detach(i);
        }
        _routes.Clear();
    }

    void Zero(int index)
    {
        var s = _src[index];
        if (s == null) return;
        for (int i = 0; i < s.Length; i++) s[i] = 0;
        Apply(index);
    }

    void Apply(int index)
    {
        var f = _src[index];
        if (f == null) return;
        for (int i = VirtualPadManager.IxLX; i <= VirtualPadManager.IxRY; i++)
            if (f[i] == 0) f[i] = 32768;
        VirtualPadManager.Feed(index, f);
    }

    void Loop()
    {
        while (_running)
        {
            long at = Environment.TickCount64;
            foreach (var r in _routes)
            {
                if (!_running) break;
                try { Pump(r); } catch { }
            }
            long spent = Environment.TickCount64 - at;
            int wait = TickMs - (int)spent;
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    void Pump(Route r)
    {
        var s = _src[r.Index];
        var frame = PadReader.Read(r.Token, r.Map);
        if (!frame.Connected)
        {
            Array.Clear(s, 0, s.Length);
            Apply(r.Index);
            return;
        }

        for (int i = 0; i < Btn.Length; i++)
        {
            // On a pad with a real POV hat the D-pad comes from the hat, never
            // from a raw-button entry in the device database: those indices are
            // Start/Back on many pads.
            if (frame.HasPov && i >= 10) s[i] = frame.Buttons.Contains(i) ? 65535 : 0;
            else s[i] = Val(frame, r.Map.GetValueOrDefault(Btn[i], Btn[i])) >= 32768 ? 65535 : 0;
        }

        s[14] = Val(frame, r.Map.GetValueOrDefault("lt", "lt")) >= 16000 ? 65535 : 0;
        s[15] = Val(frame, r.Map.GetValueOrDefault("rt", "rt")) >= 16000 ? 65535 : 0;

        foreach (var b in frame.RawButtons)
            if (b >= 0 && b < 16) s[16 + b] = 65535;

        int lr = Val(frame, r.Map.GetValueOrDefault("lr", DirDefault[3]));
        int ll = Val(frame, r.Map.GetValueOrDefault("ll", DirDefault[2]));
        int lu = Val(frame, r.Map.GetValueOrDefault("lu", DirDefault[0]));
        int ld = Val(frame, r.Map.GetValueOrDefault("ld", DirDefault[1]));
        int rr = Val(frame, r.Map.GetValueOrDefault("rr", DirDefault[7]));
        int rl = Val(frame, r.Map.GetValueOrDefault("rl", DirDefault[6]));
        int ru = Val(frame, r.Map.GetValueOrDefault("ru", DirDefault[4]));
        int rd = Val(frame, r.Map.GetValueOrDefault("rd", DirDefault[5]));

        s[VirtualPadManager.IxLX] = 32768 + (lr - ll) / 2;
        s[VirtualPadManager.IxLY] = 32768 + (lu - ld) / 2;
        s[VirtualPadManager.IxRX] = 32768 + (rr - rl) / 2;
        s[VirtualPadManager.IxRY] = 32768 + (ru - rd) / 2;

        Apply(r.Index);
        VirtualPadManager.FlushRumble(r.Index, r.Token, frame.LT, frame.RT);
    }

    /// <summary>Resolve one source name to 0..65535, analog where it makes sense.</summary>
    static int Val(PadFrame f, string src)
    {
        if (string.IsNullOrEmpty(src)) return 0;
        switch (src)
        {
            case "lx": return f.LX;
            case "ly": return f.LY;
            case "rx": return f.RX;
            case "ry": return f.RY;
            case "lx+": return Up(f.LX - 32768);
            case "lx-": return Up(32768 - f.LX);
            case "ly+": return Up(f.LY - 32768);
            case "ly-": return Up(32768 - f.LY);
            case "rx+": return Up(f.RX - 32768);
            case "rx-": return Up(32768 - f.RX);
            case "ry+": return Up(f.RY - 32768);
            case "ry-": return Up(32768 - f.RY);
        }
        if (src == "lt") return f.LT;
        if (src == "rt") return f.RT;

        int bi = Array.IndexOf(Btn, src);
        if (bi >= 0) return f.Buttons.Contains(bi) ? 65535 : 0;

        if (src.Length > 1 && src[0] == 'b' && char.IsDigit(src[1]))
            return int.TryParse(src.AsSpan(1), out var n) && f.RawButtons.Contains(n) ? 65535 : 0;

        return 0;
    }

    // The hook and the UI both treat a stick pushed past ~40% as full.
    static int Up(int v) => v >= 13000 ? 65535 : 0;
}
