using System.Runtime.InteropServices;

namespace Mapivo.Core;

/// <summary>
/// Rumble for pads whose force feedback is not exposed over DirectInput but is
/// reachable as a HID output report (many PS3/PS4/Android-mode pads). The
/// report layout is selected by vendor id.
/// </summary>
public static class HidRumble
{
    const uint RIM_TYPEHID = 2;
    const uint RIDI_DEVICENAME = 0x20000007;
    const uint RIDI_DEVICEINFO = 0x2000000b;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ_WRITE = 0x00000003;
    const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId, dwProductId, dwVersionNumber;
        public ushort usUsagePage, usUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RID_DEVICE_INFO
    {
        public uint cbSize, dwType;
        public RID_DEVICE_INFO_HID hid;
        public uint pad1, pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps,
            NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps,
            NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceList(IntPtr pList, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll")]
    static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr pp, ref HIDP_CAPS caps);
    [DllImport("hid.dll")]
    static extern bool HidD_SetOutputReport(IntPtr h, byte[] report, uint len);

    sealed class Target
    {
        public IntPtr Handle = new(-1);
        public int ReportLength;
        public bool Ready;
        public SonyHid.Kind Kind;
        public bool Bluetooth;
        public string Path = "";
    }

    static readonly object Lock = new();
    static readonly Dictionary<string, Target> Cache = new();

    /// <summary>True when this pad has a HID output report we know how to drive.</summary>
    public static bool CanVibrate(string token)
    {
        var t = Resolve(token);
        return t != null && t.Ready;
    }

    /// <summary>
    /// Set both motors, 0..255 each. rt/lt are optional DualSense adaptive-trigger
    /// effect blocks; pads without them ignore the extra bytes.
    /// </summary>
    public static bool Set(string token, byte left, byte right, byte[]? rt = null, byte[]? lt = null)
    {
        var t = Resolve(token);
        if (t == null || !t.Ready) return false;
        if (!TryParse(token, out var vid, out var pid)) return false;

        if (t.Kind != SonyHid.Kind.None)
        {
            var plan = SonyHid.Rumble(t.Kind, t.Bluetooth, left, right, 0, rt, lt);
            if (plan == null) return false;
            lock (Lock)
                return HidD_SetOutputReport(t.Handle, plan.Report, (uint)plan.Length);
        }

        byte[] rep;
        if (vid == 0x2563 && pid == 0x0575)
        {
            // ShanWan protocol (hid-shanwan): effect 2, reserved 8,
            // weak motor, strong motor, duration 0xFF = until told otherwise.
            // The caller re-sends while the rumble lasts and ends with 0,0.
            rep = new byte[Math.Max(t.ReportLength, 9)];
            rep[0] = 0x02;
            rep[1] = 0x08;
            rep[2] = right;   // weak / small motor
            rep[3] = left;    // strong / big motor
            // 0xff = run until told otherwise; the stop report has to use
            // 0x00, otherwise the motors stay on at the last strength.
            rep[4] = (left > 0 || right > 0) ? (byte)0xFF : (byte)0x00;
        }
        else
        {
            rep = new byte[Math.Max(t.ReportLength, 9)];
            rep[3] = right;
            rep[4] = left;
        }
        lock (Lock)
            return HidD_SetOutputReport(t.Handle, rep, (uint)rep.Length);
    }

    static Target? Resolve(string token)
    {
        if (!TryParse(token, out var vid, out var pid)) return null;
        lock (Lock)
        {
            if (Cache.TryGetValue(token, out var t)) return t;
            t = Open(vid, pid);
            Cache[token] = t;
            return t;
        }
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

    static IEnumerable<(IntPtr h, ushort vid, ushort pid, ushort usage)> Devices()
    {
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(IntPtr.Zero, ref count, size);
        if (count == 0) yield break;
        var buf = Marshal.AllocHGlobal((int)(count * size));
        try
        {
            if (GetRawInputDeviceList(buf, ref count, size) == uint.MaxValue) yield break;
            for (int i = 0; i < count; i++)
            {
                var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + i * (int)size);
                if (dev.dwType != RIM_TYPEHID) continue;
                var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
                uint isz = info.cbSize;
                var p = Marshal.AllocHGlobal((int)isz);
                try
                {
                    Marshal.StructureToPtr(info, p, false);
                    if (GetRawInputDeviceInfoW(dev.hDevice, RIDI_DEVICEINFO, p, ref isz) == uint.MaxValue) continue;
                    info = Marshal.PtrToStructure<RID_DEVICE_INFO>(p);
                }
                finally { Marshal.FreeHGlobal(p); }
                yield return (dev.hDevice,
                    (ushort)info.hid.dwVendorId,
                    (ushort)info.hid.dwProductId,
                    info.hid.usUsage);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string DevicePath(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0 || size == uint.MaxValue) return "";
        var buf = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref size) == uint.MaxValue) return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static Target Open(ushort vid, ushort pid)
    {
        var t = new Target();
        var paths = new List<string>();
        foreach (var d in Devices())
        {
            if (d.vid != vid || d.pid != pid) continue;
            if (d.usage != 0x05 && d.usage != 0x04) continue;
            var path = DevicePath(d.h);
            if (path.Length > 0 && !paths.Contains(path)) paths.Add(path);
        }
        if (paths.Count == 0)
            foreach (var p in HidPaths.Candidates(vid, pid))
                if (!paths.Contains(p)) paths.Add(p);

        var kind = SonyHid.Classify(vid, pid);
        foreach (var path in paths)
        {
            var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1))
                h = CreateFileW(path, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) continue;
            if (!HidD_GetPreparsedData(h, out var pp)) { CloseHandle(h); continue; }
            var caps = new HIDP_CAPS();
            HidP_GetCaps(pp, ref caps);
            HidD_FreePreparsedData(pp);
            if (caps.OutputReportByteLength < 2) { CloseHandle(h); continue; }

            t.Handle = h;
            t.ReportLength = caps.OutputReportByteLength;
            t.Kind = kind;
            t.Path = path;
            t.Bluetooth = kind == SonyHid.Kind.Ps3 ? false : SonyHid.IsBluetooth(path);
            t.Ready = true;
            return t;
        }
        return t;
    }

    public static void Forget(string token)
    {
        lock (Lock)
        {
            if (Cache.Remove(token, out var t) && t.Handle != new IntPtr(-1)) CloseHandle(t.Handle);
        }
    }
}
