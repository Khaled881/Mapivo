
using System;
using System.Collections.Generic;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Mapivo.Core;

/// <summary>
/// Virtual controllers injected into Windows through ViGEmBus. A slot is only
/// created after the user presses Start on the driver page; nothing is
/// registered for any game behind the user's back.
/// </summary>
public static class VirtualPadManager
{
    public const int MaxSlots = 8;

    // Wire layout shared with the driver loop. Everything is 0..65535;
    // sticks rest at 32768.
    //   0..9    a b x y lb rb back start l3 r3
    //   10..13  up down left right
    //   14,15   lt, rt
    //   16..31  raw HID buttons b0..b15
    //   32..35  lx ly rx ry
    public const int SourceCount = 36;
    public const int IxLX = 32, IxLY = 33, IxRX = 34, IxRY = 35;

    public sealed class VirtualPad
    {
        public int Index;
        public string SourceToken = "";
        public bool Enabled;
        public bool Attached;
        public string Problem = "";
        public byte VibrateLeft, VibrateRight;

        internal IXbox360Controller? Target;
        internal ushort Buttons;
        internal short LX, LY, RX, RY;
        internal byte LT, RT;
        internal string Sent = "";
    }

    static ViGEmClient? _client;
    static readonly object Lock = new();
    static readonly VirtualPad[] Slots = BuildSlots();

    static VirtualPad[] BuildSlots()
    {
        var a = new VirtualPad[MaxSlots];
        for (int i = 0; i < MaxSlots; i++) a[i] = new VirtualPad { Index = i };
        return a;
    }

    public static IReadOnlyList<VirtualPad> List() => Slots;

    public static VirtualPad? Get(int index) =>
        index >= 0 && index < MaxSlots ? Slots[index] : null;

    /// <summary>"" when the ViGEmBus driver is present, else a reason.</summary>
    public static string DriverProblem()
    {
        if (DriverPath().Length == 0) return "ViGEmBus is not installed";
        try
        {
            lock (Lock) _client ??= new ViGEmClient();
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Driver service path ("" when not installed) — for the UI status row.</summary>
    public static string DriverPath()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\ViGEmBus");
            return k?.GetValue("ImagePath")?.ToString() ?? "";
        }
        catch { return ""; }
    }

    public static void SetSource(int index, string token)
    {
        var s = Get(index);
        if (s != null) s.SourceToken = token ?? "";
    }

    /// <summary>Create the virtual controller for a slot.</summary>
    public static string Attach(int index)
    {
        var s = Get(index);
        if (s == null) return "Bad slot";
        string problem = DriverProblem();
        if (problem.Length > 0) return problem;
        lock (Lock)
        {
            if (s.Attached) return "";
            try
            {
                var t = _client!.CreateXbox360Controller();
                t.AutoSubmitReport = false;
                t.FeedbackReceived += (_, e) =>
                {
                    s.VibrateLeft = e.LargeMotor;
                    s.VibrateRight = e.SmallMotor;
                };
                t.Connect();
                s.Target = t;
                s.Sent = "";
                s.Attached = true;
                s.Problem = "";
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    public static void Detach(int index)
    {
        var s = Get(index);
        if (s == null) return;
        lock (Lock)
        {
            try { s.Target?.Disconnect(); } catch { }
            s.Target = null;
            s.Attached = false;
            s.VibrateLeft = s.VibrateRight = 0;
        }
    }

    public static void DetachAll()
    {
        for (int i = 0; i < MaxSlots; i++) Detach(i);
    }

    static readonly Xbox360Button[] BtnOrder =
    {
        Xbox360Button.A, Xbox360Button.B, Xbox360Button.X, Xbox360Button.Y,
        Xbox360Button.LeftShoulder, Xbox360Button.RightShoulder,
        Xbox360Button.Back, Xbox360Button.Start,
        Xbox360Button.LeftThumb, Xbox360Button.RightThumb,
        Xbox360Button.Up, Xbox360Button.Down, Xbox360Button.Left, Xbox360Button.Right,
    };

    static readonly ushort[] BtnMask = BuildMask();

    static ushort[] BuildMask()
    {
        var m = new ushort[BtnOrder.Length];
        for (int i = 0; i < BtnOrder.Length; i++) m[i] = BtnOrder[i].Value;
        return m;
    }

    /// <summary>Push one frame into a slot. Cheap when nothing changed.</summary>
    public static void Feed(int index, int[] src)
    {
        var s = Get(index);
        if (s == null) return;
        var t = s.Target;
        if (t == null || !s.Attached) return;

        ushort buttons = 0;
        for (int i = 0; i < BtnMask.Length; i++)
            if (src[i] > 20000) buttons |= BtnMask[i];

        byte lt = (byte)(Math.Clamp(src[14], 0, 65535) / 257);
        byte rt = (byte)(Math.Clamp(src[15], 0, 65535) / 257);
        short lx = Axis(src[IxLX]), ly = Axis(src[IxLY]);
        short rx = Axis(src[IxRX]), ry = Axis(src[IxRY]);

        if (buttons == s.Buttons && lt == s.LT && rt == s.RT
            && lx == s.LX && ly == s.LY && rx == s.RX && ry == s.RY) return;

        s.Buttons = buttons; s.LT = lt; s.RT = rt;
        s.LX = lx; s.LY = ly; s.RX = rx; s.RY = ry;
        try
        {
            t.SetButtonsFull(buttons);
            t.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
            t.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
            t.SetAxisValue(Xbox360Axis.RightThumbX, rx);
            t.SetAxisValue(Xbox360Axis.RightThumbY, ry);
            t.SetSliderValue(Xbox360Slider.LeftTrigger, lt);
            t.SetSliderValue(Xbox360Slider.RightTrigger, rt);
            t.SubmitReport();
        }
        catch { }
    }

    static short Axis(int v) => (short)Math.Clamp(v - 32768, -32768, 32767);

    /// <summary>
    /// Send the game's rumble back to the physical pad. Only fires when the
    /// value changed, so an idle game does not hammer the pad every frame.
    /// </summary>
    public static void FlushRumble(int index, string token, int srcLT = 0, int srcRT = 0)
    {
        var s = Get(index);
        if (s == null || token.Length == 0) return;
        byte l = s.VibrateLeft, r = s.VibrateRight;

        // DualSense halts inside its travel that follow the finger, scaled by the
        // game's rumble. No PC game asks for this, so it is derived here; a pad
        // that cannot do it simply ignores the blocks.
        var rtEffect = new byte[11];
        var ltEffect = new byte[11];
        int bump = SonyHid.StrengthOf(Math.Max(l, r));
        TriggerEffect(rtEffect, srcRT, bump);
        TriggerEffect(ltEffect, srcLT, bump);

        string key = l + ":" + r + ":" + srcLT + ":" + srcRT;
        if (key == s.Sent) return;
        s.Sent = key;
        try
        {
            if (token[0] == 'x' || token[0] == 'X')
            {
                if (int.TryParse(token.AsSpan(1), out var x) && XInputApi.IsConnected(x))
                    XInputApi.SetVibration(x, (ushort)(l * 257), (ushort)(r * 257));
            }
            else if (!HidRumble.Set(token, l, r, rtEffect, ltEffect))
            {
                // No HID output report: fall back to the DirectInput effect so
                // the game's rumble still reaches the pad. One axis only, so
                // the stronger motor wins.
                int mag = Math.Max(l, r) * 39;
                PadReader.RumbleFallback(token, mag);
            }
        }
        catch { }
    }

    static void TriggerEffect(byte[] block, int depth, int rumble)
    {
        int zone = SonyHid.ZoneOf(depth);
        if (zone <= 0) { SonyHid.EffectOff(block, 0); return; }
        int strength = Math.Clamp(2 + rumble, 1, 8);
        SonyHid.EffectFeedback(block, 0, zone, strength);
    }
}
