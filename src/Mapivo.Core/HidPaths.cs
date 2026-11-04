using System.Runtime.InteropServices;

namespace Mapivo.Core;

/// <summary>
/// Device-interface enumeration for HID pads. RawInput silently drops devices
/// hidden by a filter driver (HidHide and friends), so the cfgmgr32 interface
/// list is used as a second way to reach a pad path. Both paths are exposed in
/// the shape of the game-control collections only, which keeps audio and touch
/// interfaces of a Sony pad out of the list.
/// </summary>
public static class HidPaths
{
    const uint DIGCF_PRESENT = 0x02;
    const int CR_SUCCESS = 0;
    const uint RIM_TYPEHID = 2;
    const uint RIDI_DEVICENAME = 0x20000007;
    const uint RIDI_DEVICEINFO = 0x2000000b;

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

    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceList(IntPtr pList, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid guid, string? devId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string? devId, [Out] char[] list, uint len, uint flags);

    static readonly Guid HidClass = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    /// <summary>Every interface path Windows reports for the HID class.</summary>
    public static List<string> Query()
    {
        var paths = new List<string>();
        uint len = 0;
        var g = HidClass;
        if (CM_Get_Device_Interface_List_SizeW(out len, ref g, null, DIGCF_PRESENT) != CR_SUCCESS || len == 0)
            return paths;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, null, buf, len, DIGCF_PRESENT) != CR_SUCCESS)
            return paths;

        int i = 0;
        while (i < buf.Length && buf[i] != '\0')
        {
            int j = i;
            while (j < buf.Length && buf[j] != '\0') j++;
            var p = new string(buf, i, j - i);
            i = j + 1;
            if (p.Length > 0) paths.Add(p);
        }
        return paths;
    }

    /// <summary>Vendor/product carried in a HID interface path, or zeros.</summary>
    public static (ushort vid, ushort pid) Parse(string path)
    {
        int v = path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
        int p = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (v < 0 || p < 0) return (0, 0);
        return (HexAt(path, v + 4), HexAt(path, p + 4));
    }

    static ushort HexAt(string s, int at)
    {
        if (at + 4 > s.Length) return 0;
        return ushort.TryParse(s.AsSpan(at, 4), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (ushort)0;
    }

    /// <summary>
    /// Candidate paths for one pad, the way RawInput sees it. Falls back to the
    /// cfgmgr32 interface list when RawInput hides the device (HidHide).
    /// </summary>
    public static List<string> Candidates(ushort vid, ushort pid)
    {
        var list = new List<string>();
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(IntPtr.Zero, ref count, size);
        if (count > 0)
        {
            var buf = Marshal.AllocHGlobal((int)(count * size));
            try
            {
                if (GetRawInputDeviceList(buf, ref count, size) != uint.MaxValue)
                {
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
                        if (info.hid.dwVendorId != vid || info.hid.dwProductId != pid) continue;
                        if (info.hid.usUsagePage != 0x01 || (info.hid.usUsage != 0x04 && info.hid.usUsage != 0x05)) continue;
                        var path = DeviceName(dev.hDevice);
                        if (path.Length > 0 && !list.Contains(path)) list.Add(path);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        if (list.Count == 0)
        {
            foreach (var p in Query())
            {
                var (v, d) = Parse(p);
                if (v == vid && d == pid) list.Add(p);
            }
        }
        return list;
    }

    static string DeviceName(IntPtr hDevice)
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
}
