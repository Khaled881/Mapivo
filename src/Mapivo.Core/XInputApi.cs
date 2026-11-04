using System.Runtime.InteropServices;

namespace Mapivo.Core;

[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLX;
    public short ThumbLY;
    public short ThumbRX;
    public short ThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputVibration
{
    public ushort LeftMotorSpeed;
    public ushort RightMotorSpeed;
}

public static class XInputApi
{
    const string Dll = "xinput1_4.dll";
    public const uint ErrorSuccess = 0;
    public const uint ErrorDeviceNotConnected = 1167;

    [DllImport(Dll)]
    static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

    [DllImport(Dll)]
    static extern uint XInputSetState(uint dwUserIndex, ref XInputVibration pVibration);

    [DllImport(Dll)]
    static extern uint XInputGetCapabilities(uint dwUserIndex, uint dwFlags, out XInputCapabilities pCapabilities);

    public static bool IsConnected(int index) =>
        XInputGetState((uint)index, out _) == ErrorSuccess;

    public static bool TryGetState(int index, out XInputState state) =>
        XInputGetState((uint)index, out state) == ErrorSuccess;

    public static void SetVibration(int index, ushort left, ushort right)
    {
        var v = new XInputVibration { LeftMotorSpeed = left, RightMotorSpeed = right };
        XInputSetState((uint)index, ref v);
    }

    public static string GetName(int index)
    {
        if (XInputGetCapabilities((uint)index, 0, out var caps) != ErrorSuccess)
            return $"Gamepad {index + 1}";
        return caps.SubType switch
        {
            1 => $"Gamepad {index + 1}",
            2 => $"Gamepad {index + 1} (Wheel)",
            3 => $"Gamepad {index + 1} (Arcade Stick)",
            4 => $"Gamepad {index + 1} (Flight Stick)",
            5 => $"Gamepad {index + 1} (Dance Pad)",
            6 => $"Gamepad {index + 1} (Guitar)",
            8 => $"Gamepad {index + 1} (Drum Kit)",
            _ => $"Gamepad {index + 1}"
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputCapabilities
{
    public byte Type;
    public byte SubType;
    public ushort Flags;
    public XInputGamepad Gamepad;
    public XInputVibration Vibration;
}
