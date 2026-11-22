using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RescueWhale;

// The CONTROLLER page: a live tester, battery, a vibration test, and button-to-keyboard/mouse mapping for an Xbox
// controller. Everything goes through XInput, Windows' official controller interface. Nothing is written to the
// controller itself; the mapping works by sending key presses from this app, so it only applies while the app is open.
public partial class MainWindow
{
    // ---- XInput ----
    [StructLayout(LayoutKind.Sequential)] struct XGamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] struct XState { public uint Packet; public XGamepad Pad; }
    [StructLayout(LayoutKind.Sequential)] struct XBattery { public byte Type, Level; }
    [StructLayout(LayoutKind.Sequential)] struct XVibration { public ushort Left, Right; }
    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint slot, out XState state);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint slot, byte device, out XBattery battery);
    [DllImport("xinput1_4.dll")] static extern uint XInputSetState(uint slot, ref XVibration vibration);

    // ---- sending keys and mouse actions ----
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>A controller input that can be mapped: a button (Flag) or a trigger (1 = left, 2 = right).</summary>
    record PadButton(string Name, ushort Flag, int Trigger = 0);
    static readonly PadButton[] PadButtons =
    [
        new("A", 0x1000), new("B", 0x2000), new("X", 0x4000), new("Y", 0x8000),
        new("LB", 0x0100), new("RB", 0x0200), new("LT", 0, 1), new("RT", 0, 2),
        new("VIEW", 0x0020), new("MENU", 0x0010), new("LS", 0x0040), new("RS", 0x0080),
        new("UP", 0x0001), new("DOWN", 0x0002), new("LEFT", 0x0004), new("RIGHT", 0x0008),
    ];

    enum PadKind { None, Key, MouseButton, Scroll }
    record PadAction(string Label, PadKind Kind, int Code);
    static readonly PadAction[] PadActions = BuildPadActions();

    static PadAction[] BuildPadActions()
    {
        var list = new List<PadAction> { new("None", PadKind.None, 0) };
        void Key(string name, int vk) => list.Add(new("Key: " + name, PadKind.Key, vk));
        Key("Space", 0x20); Key("Enter", 0x0D); Key("Esc", 0x1B); Key("Tab", 0x09); Key("Backspace", 0x08);
        Key("Shift", 0x10); Key("Ctrl", 0x11); Key("Alt", 0x12);
        Key("Up", 0x26); Key("Down", 0x28); Key("Left", 0x25); Key("Right", 0x27);
        for (char c = 'A'; c <= 'Z'; c++) Key(c.ToString(), c);
        for (char c = '0'; c <= '9'; c++) Key(c.ToString(), c);
        for (int f = 1; f <= 12; f++) Key("F" + f, 0x6F + f);
        list.Add(new("Mouse: left click", PadKind.MouseButton, 0));
        list.Add(new("Mouse: right click", PadKind.MouseButton, 1));
        list.Add(new("Mouse: middle click", PadKind.MouseButton, 2));
        list.Add(new("Mouse: scroll up", PadKind.Scroll, 120));
        list.Add(new("Mouse: scroll down", PadKind.Scroll, -120));
        Key("Volume up", 0xAF); Key("Volume down", 0xAE); Key("Mute", 0xAD);
        Key("Play / pause", 0xB3); Key("Next track", 0xB0); Key("Previous track", 0xB1);
        return [.. list];
    }

    const int TriggerPressed = 30;   // XInput's own threshold for treating a trigger as pressed

    readonly DispatcherTimer _padTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    readonly int[] _padMap = new int[PadButtons.Length];          // index into PadActions for each input
    readonly bool[] _padDown = new bool[PadButtons.Length];
    readonly DateTime[] _padRepeat = new DateTime[PadButtons.Length];
    readonly Dictionary<string, Border> _padChips = [];
    readonly List<ComboBox> _padBoxes = [];
    Rectangle _ltBar = new(), _rtBar = new(), _lsDot = new(), _rsDot = new();
    int _page;            // 0 = mouse, 1 = controller
    int _padSlot = -1;    // which of XInput's four slots the controller is on, or -1 for none
    int _padTicks;
    bool _padMapping;

    static string PadSettingsPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RescueWhale", "controller.txt");

    void InitController(string[] args)
    {
        BuildPadTester();

        // mapping rows: two columns of "button name + what it sends"
        for (int i = 0; i < PadButtons.Length; i++)
        {
            int index = i;
            var row = new DockPanel { Margin = new Thickness(0, 0, i % 2 == 0 ? 10 : 0, 7) };
            var box = new ComboBox();
            foreach (var a in PadActions) box.Items.Add(a.Label);
            box.SelectionChanged += (_, _) => { if (!_loading && box.SelectedIndex >= 0) { ReleasePadKeys(); _padMap[index] = box.SelectedIndex; SavePadSettings(); } };
            row.Children.Add(new TextBlock { Text = PadButtons[i].Name, Width = 58, Style = (Style)FindResource("Label") });
            row.Children.Add(box);
            PadMapRows.Children.Add(row);
            _padBoxes.Add(box);
        }
        LoadPadSettings();
        _loading = true;
        for (int i = 0; i < _padBoxes.Count; i++) _padBoxes[i].SelectedIndex = _padMap[i];
        PadMapSwitch.IsChecked = _padMapping;
        PageMouse.IsChecked = true;
        _loading = false;

        _padTimer.Tick += (_, _) => PadTick();
        Closed += (_, _) => { ReleasePadKeys(); StopVibration(); };
        if (_padMapping) _padTimer.Start();
        // --page controller opens straight on the controller page
        int pageAt = Array.IndexOf(args, "--page");
        if (pageAt >= 0 && pageAt + 1 < args.Length && args[pageAt + 1].StartsWith("c", StringComparison.OrdinalIgnoreCase)) PageController.IsChecked = true;
    }

    // ---------- page switching ----------
    void Page_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || PadPage == null) return;
        _page = sender == PageController ? 1 : 0;
        Pages.Visibility = _page == 0 ? Visibility.Visible : Visibility.Collapsed;
        PadPage.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_page == 1 || _padMapping) _padTimer.Start(); else { _padTimer.Stop(); StopVibration(); }
        if (_page == 1) { _padTicks = 0; PadTick(); }
        UpdateChrome();
        Relayout();
    }

    // ---------- tester ----------
    void BuildPadTester()
    {
        var c = PadCanvas;
        Border Chip(string name, double x, double y, double w = 34, string? text = null)
        {
            var label = new TextBlock { Text = text ?? name, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var b = new Border { Width = w, Height = 24, BorderThickness = new Thickness(1), Child = label };
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            Canvas.SetLeft(b, x); Canvas.SetTop(b, y);
            c.Children.Add(b);
            _padChips[name] = b;
            return b;
        }
        Rectangle Bar(double x, double y)
        {
            var frame = new Border { Width = 92, Height = 12, BorderThickness = new Thickness(1) };
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            Canvas.SetLeft(frame, x); Canvas.SetTop(frame, y);
            c.Children.Add(frame);
            var fill = new Rectangle { Width = 0, Height = 10 };
            fill.SetResourceReference(Shape.FillProperty, "Text");
            Canvas.SetLeft(fill, x + 1); Canvas.SetTop(fill, y + 1);
            c.Children.Add(fill);
            return fill;
        }
        Rectangle Stick(string name, double x, double y)
        {
            var box = Chip(name, x, y, 78);
            box.Height = 78;
            ((TextBlock)box.Child).VerticalAlignment = VerticalAlignment.Bottom;
            ((TextBlock)box.Child).FontSize = 10;
            var dot = new Rectangle { Width = 8, Height = 8, IsHitTestVisible = false };
            dot.SetResourceReference(Shape.FillProperty, "Accent");
            Canvas.SetLeft(dot, x + 35); Canvas.SetTop(dot, y + 35);
            c.Children.Add(dot);
            return dot;
        }

        // laid out roughly like the controller: triggers and bumpers on top, left stick and D-pad left, face buttons and right stick right
        Chip("LT", 10, 4, 34); _ltBar = Bar(50, 10);
        Chip("RT", 476, 4, 34); _rtBar = Bar(378, 10);
        Chip("LB", 10, 34, 60);
        Chip("RB", 450, 34, 60);
        _lsDot = Stick("LS", 30, 70);
        Chip("UP", 160, 122, 30, "^"); Chip("DOWN", 160, 174, 30, "v"); Chip("LEFT", 128, 148, 30, "<"); Chip("RIGHT", 192, 148, 30, ">");
        Chip("VIEW", 200, 62, 50); Chip("MENU", 270, 62, 50);
        Chip("Y", 428, 68, 30); Chip("A", 428, 120, 30); Chip("X", 396, 94, 30); Chip("B", 460, 94, 30);
        _rsDot = Stick("RS", 300, 118);
    }

    void SetChip(string name, bool pressed)
    {
        var b = _padChips[name];
        if (pressed) b.SetResourceReference(Border.BackgroundProperty, "Text"); else b.Background = Brushes.Transparent;
        ((TextBlock)b.Child).SetResourceReference(TextBlock.ForegroundProperty, pressed ? "OnAccent" : "Text");
    }

    // ---------- polling ----------
    void PadTick()
    {
        _padTicks++;
        if (_padSlot < 0)
        {
            // look for a controller about once a second; asking about empty slots is slow
            if (_padTicks % 60 != 1) return;
            for (uint s = 0; s < 4 && _padSlot < 0; s++)
                if (XInputGetState(s, out _) == 0) _padSlot = (int)s;
            if (_padSlot < 0) { ShowPadStatus(false); return; }
            _padTicks = 1;
        }
        if (XInputGetState((uint)_padSlot, out var st) != 0)
        {
            _padSlot = -1;
            ReleasePadKeys();
            ShowPadStatus(false);
            return;
        }
        var pad = st.Pad;
        if (_padTicks % 120 == 1) ShowPadStatus(true);

        if (_page == 1)
        {
            foreach (var b in PadButtons)
                if (b.Trigger == 0 && b.Name is not ("LS" or "RS")) SetChip(b.Name, (pad.Buttons & b.Flag) != 0);
            SetChip("LT", pad.LT > TriggerPressed); SetChip("RT", pad.RT > TriggerPressed);
            _ltBar.Width = 90 * pad.LT / 255.0; _rtBar.Width = 90 * pad.RT / 255.0;
            MoveStick(_lsDot, "LS", 30, 70, pad.LX, pad.LY, (pad.Buttons & 0x0040) != 0);
            MoveStick(_rsDot, "RS", 300, 118, pad.RX, pad.RY, (pad.Buttons & 0x0080) != 0);
            PadReadout.Text = $"LS {pad.LX,6} {pad.LY,6}   RS {pad.RX,6} {pad.RY,6}   LT {pad.LT,3}   RT {pad.RT,3}";
        }

        if (_padMapping)
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < PadButtons.Length; i++)
            {
                var b = PadButtons[i];
                bool pressed = b.Trigger == 1 ? pad.LT > TriggerPressed : b.Trigger == 2 ? pad.RT > TriggerPressed : (pad.Buttons & b.Flag) != 0;
                var action = PadActions[_padMap[i]];
                if (pressed != _padDown[i])
                {
                    _padDown[i] = pressed;
                    SendPadAction(action, pressed);
                    _padRepeat[i] = now.AddMilliseconds(350);
                }
                else if (pressed && action.Kind == PadKind.Scroll && now >= _padRepeat[i])
                {
                    SendPadAction(action, true);          // holding a scroll button keeps scrolling
                    _padRepeat[i] = now.AddMilliseconds(90);
                }
            }
        }
    }

    void MoveStick(Rectangle dot, string name, double x, double y, short sx, short sy, bool clicked)
    {
        Canvas.SetLeft(dot, x + 35 + 31 * sx / 32768.0);
        Canvas.SetTop(dot, y + 35 - 31 * sy / 32768.0);
        _padChips[name].SetResourceReference(Border.BorderBrushProperty, clicked ? "Accent" : "Line");
        _padChips[name].BorderThickness = new Thickness(clicked ? 2 : 1);
    }

    int _padBatteryType = -1, _padBatteryLevel;   // as XInput reports them: type 1 is wired; level runs 0 (empty) to 3 (full)
    bool _padAdapter;                             // an Xbox Wireless Adapter is plugged in and working

    // ---- finding the Xbox Wireless Adapter among the PC's USB devices ----
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, System.Text.StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property, out int type, byte[] buffer, int size, out int required);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList")] static extern bool SetupDiDestroyList(IntPtr set);

    /// <summary>
    /// True when an Xbox Wireless Adapter (either the original or the slim model) is plugged in and has a driver.
    /// Without its driver the adapter is listed but has no service, and no controller can connect through it.
    /// </summary>
    static bool FindXboxAdapter()
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 0x02 | 0x04);   // present devices, all classes
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return false;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var id = new System.Text.StringBuilder(260);
            var buffer = new byte[256];
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDeviceInstanceId(set, ref data, id, id.Capacity, out _)) continue;
                string s = id.ToString().ToUpperInvariant();
                if (!s.Contains("VID_045E&PID_02FE") && !s.Contains("VID_045E&PID_02E6")) continue;
                return SetupDiGetDeviceRegistryProperty(set, ref data, 4, out _, buffer, buffer.Length, out int n) && n > 2;   // property 4 is the driver's service name
            }
            return false;
        }
        finally { SetupDiDestroyList(set); }
    }

    void ShowPadStatus(bool connected)
    {
        if (connected && XInputGetBatteryInformation((uint)_padSlot, 0, out var bat) == 0) { _padBatteryType = bat.Type; _padBatteryLevel = bat.Level; }
        else _padBatteryType = -1;
        _padAdapter = FindXboxAdapter();
        if (!connected)
            PadReadout.Text = _padAdapter
                ? "wireless adapter ready. turn the controller on. to pair a new one, hold the pair button on the adapter, then on the controller"
                : "connect an xbox controller by cable, bluetooth or the wireless adapter";
        if (_page == 1) ShowPadHeader();
    }

    /// <summary>On the controller page the header's status box and battery gauge describe the controller instead of the mouse.</summary>
    void ShowPadHeader()
    {
        FirmwareLabel.Text = "POWER ..... ";
        if (_padSlot < 0)
        {
            SetState("Warn", _padAdapter ? "ADAPTER READY, NO CONTROLLER" : "NO CONTROLLER");
            ModelText.Text = "--"; IdText.Text = _padAdapter ? "xbox wireless adapter" : "--"; FirmwareText.Text = "--";
            BatteryIcon.Text = BatteryArt(0);
            BatteryIcon.Foreground = BatteryPct.Foreground = (Brush)FindResource("Muted");
            BatteryPct.Text = "--";
            BatteryNote.Text = "CONTROLLER";
            return;
        }
        bool wired = _padBatteryType == 1, known = _padBatteryType > 0;
        // Windows doesn't say whether a wireless controller came in through the adapter or Bluetooth, so the adapter is
        // only named when it's plugged in, as the likely route.
        SetState("Accent", !known ? "CONTROLLER ONLINE" : wired ? "CONTROLLER / CABLE" : _padAdapter ? "CONTROLLER / ADAPTER" : "CONTROLLER / WIRELESS");
        ModelText.Text = "XBOX CONTROLLER";
        IdText.Text = "xinput slot " + (_padSlot + 1) + (_padAdapter ? "  adapter ready" : "");

        // Windows only reports four rough battery levels for a controller, and none at all while it's on a cable
        string word = _padBatteryLevel switch { 0 => "EMPTY", 1 => "LOW", 2 => "MED", _ => "FULL" };
        int percent = _padBatteryLevel switch { 0 => 5, 1 => 30, 2 => 60, _ => 100 };
        FirmwareText.Text = !known ? "unknown" : wired ? "on cable" : word.ToLowerInvariant() + " battery";
        if (known && !wired)
        {
            BatteryIcon.Text = BatteryArt(percent);
            BatteryIcon.Foreground = BatteryPct.Foreground = BatteryColor(percent);
            BatteryPct.Text = word;
        }
        else
        {
            BatteryIcon.Text = BatteryArt(wired ? 100 : 0);
            BatteryIcon.Foreground = BatteryPct.Foreground = (Brush)FindResource(wired ? "Text" : "Muted");
            BatteryPct.Text = wired ? "USB" : "--";
        }
        BatteryNote.Text = "CONTROLLER";
    }

    // ---------- mapping ----------
    void SendPadAction(PadAction a, bool down)
    {
        var input = new INPUT();
        switch (a.Kind)
        {
            case PadKind.Key:
                input.type = 1;
                input.ki.wVk = (ushort)a.Code;
                input.ki.wScan = (ushort)MapVirtualKey((uint)a.Code, 0);
                bool extended = a.Code is 0x25 or 0x26 or 0x27 or 0x28;   // the arrow keys
                input.ki.dwFlags = (down ? 0u : 2u) | (extended ? 1u : 0u);
                break;
            case PadKind.MouseButton:
                input.type = 0;
                input.mi.dwFlags = a.Code switch { 0 => down ? 0x0002u : 0x0004u, 1 => down ? 0x0008u : 0x0010u, _ => down ? 0x0020u : 0x0040u };
                break;
            case PadKind.Scroll:
                if (!down) return;
                input.type = 0;
                input.mi.dwFlags = 0x0800;
                input.mi.mouseData = unchecked((uint)a.Code);
                break;
            default:
                return;
        }
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Lets go of anything the mapping is holding down, so a key can't stay stuck when mapping stops.</summary>
    void ReleasePadKeys()
    {
        for (int i = 0; i < _padDown.Length; i++)
            if (_padDown[i]) { _padDown[i] = false; SendPadAction(PadActions[_padMap[i]], false); }
    }

    void PadMap_Click(object sender, RoutedEventArgs e)
    {
        _padMapping = PadMapSwitch.IsChecked == true;
        if (!_padMapping) ReleasePadKeys();
        if (_padMapping || _page == 1) _padTimer.Start(); else _padTimer.Stop();
        SavePadSettings();
        Status(_padMapping ? "controller mapping on: mapped buttons now send keys to whatever window is in front" : "controller mapping off");
    }

    void PadClear_Click(object sender, RoutedEventArgs e)
    {
        ReleasePadKeys();
        _loading = true;
        for (int i = 0; i < _padBoxes.Count; i++) { _padMap[i] = 0; _padBoxes[i].SelectedIndex = 0; }
        _loading = false;
        SavePadSettings();
        Status("controller mapping cleared");
    }

    void LoadPadSettings()
    {
        try
        {
            foreach (var line in File.ReadAllLines(PadSettingsPath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                if (parts[0] == "mapping") { _padMapping = parts[1] == "1"; continue; }
                int button = Array.FindIndex(PadButtons, b => b.Name == parts[0]);
                int action = Array.FindIndex(PadActions, a => a.Label == parts[1]);
                if (button >= 0 && action >= 0) _padMap[button] = action;
            }
        }
        catch { }
    }

    void SavePadSettings()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PadSettingsPath)!);
            var lines = new List<string> { "mapping=" + (_padMapping ? 1 : 0) };
            for (int i = 0; i < PadButtons.Length; i++)
                if (_padMap[i] != 0) lines.Add(PadButtons[i].Name + "=" + PadActions[_padMap[i]].Label);
            File.WriteAllLines(PadSettingsPath, lines);
        }
        catch { }
    }

    // ---------- vibration ----------
    void PadVibe_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PadVibeLeftText == null || PadVibeRightText == null) return;
        PadVibeLeftText.Text = (int)PadVibeLeft.Value + "%";
        PadVibeRightText.Text = (int)PadVibeRight.Value + "%";
    }

    async void PadVibeTest_Click(object sender, RoutedEventArgs e)
    {
        if (_padSlot < 0) { Status("no controller connected", true); return; }
        var v = new XVibration { Left = (ushort)(655.35 * PadVibeLeft.Value), Right = (ushort)(655.35 * PadVibeRight.Value) };
        XInputSetState((uint)_padSlot, ref v);
        await Task.Delay(1000);
        StopVibration();
    }

    void StopVibration()
    {
        if (_padSlot < 0) return;
        var off = new XVibration();
        XInputSetState((uint)_padSlot, ref off);
    }
}
