using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Mapivo.Core;

public enum PadSource { XInput, RawInput }

public sealed record PhysicalPad(int Index, string Name, PadSource Source, string Token)
{
    public string SourceLabel => Source == PadSource.XInput ? "XInput" : "DirectInput";
    public override string ToString() => $"{Name} [{SourceLabel}]";
}

public static class PadScanner
{
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

    // Must be exactly 32 bytes; Windows rejects any other cbSize.
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

    public static List<PhysicalPad> Scan()
    {
        var list = new List<PhysicalPad>();
        for (int i = 0; i < 4; i++)
            if (XInputApi.IsConnected(i))
                list.Add(new PhysicalPad(list.Count, XInputApi.GetName(i), PadSource.XInput, $"x{i}"));

        var seen = new HashSet<string>();
        foreach (var (vid, pid, name) in RawJoysticks())
        {
            string token = $"j{vid:X4}_{pid:X4}";
            if (!seen.Add(token)) continue;
            list.Add(new PhysicalPad(list.Count, name, PadSource.RawInput, token));
        }

        // A filter driver (HidHide & co.) drops the pad from RawInput, which would
        // hide a PlayStation pad from the whole app. The HID class interface list
        // still reports it, so add anything Sony-shaped that is missing.
        foreach (var path in HidPaths.Query())
        {
            var (vid, pid) = HidPaths.Parse(path);
            if (SonyHid.Classify(vid, pid) == SonyHid.Kind.None) continue;
            string token = $"j{vid:X4}_{pid:X4}";
            if (!seen.Add(token)) continue;
            list.Add(new PhysicalPad(list.Count, OemName(vid, pid), PadSource.RawInput, token));
        }

        return list;
    }

    static IEnumerable<(ushort vid, ushort pid, string name)> RawJoysticks()
    {
        uint count = 0;
        GetRawInputDeviceList(IntPtr.Zero, ref count, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>());
        if (count == 0) yield break;

        var buf = Marshal.AllocHGlobal((int)(count * Marshal.SizeOf<RAWINPUTDEVICELIST>()));
        try
        {
            if (GetRawInputDeviceList(buf, ref count, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>()) == uint.MaxValue)
                yield break;

            for (int i = 0; i < count; i++)
            {
                var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + i * Marshal.SizeOf<RAWINPUTDEVICELIST>());
                if (dev.dwType != RIM_TYPEHID) continue;

                var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
                uint size = info.cbSize;
                var infoPtr = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (GetRawInputDeviceInfoW(dev.hDevice, RIDI_DEVICEINFO, infoPtr, ref size) == uint.MaxValue)
                        continue;
                    info = Marshal.PtrToStructure<RID_DEVICE_INFO>(infoPtr);
                }
                finally { Marshal.FreeHGlobal(infoPtr); }

                bool isJoystick = info.hid.usUsagePage == 0x01 &&
                                  (info.hid.usUsage == 0x04 || info.hid.usUsage == 0x05);
                if (!isJoystick) continue;

                // An XInput pad also enumerates as a HID joystick. Its path carries
                // "&IG_" (the XInput-compatible interface), so skip it here to avoid
                // listing the same physical pad twice.
                if (DeviceName(dev.hDevice).Contains("&IG_", StringComparison.OrdinalIgnoreCase))
                    continue;

                ushort vid = (ushort)info.hid.dwVendorId, pid = (ushort)info.hid.dwProductId;
                yield return (vid, pid, OemName(vid, pid));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
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

    static string OemName(ushort vid, ushort pid)
    {
        string key = $"VID_{vid:X4}&PID_{pid:X4}";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view)
                        .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM");
                    var name = baseKey?.OpenSubKey(key)?.GetValue("OEMName") as string;
                    if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                }
                catch { }
            }
        }
        return $"DirectInput pad ({key})";
    }
}
