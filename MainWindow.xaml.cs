using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RescueWhale;

public partial class MainWindow : Window
{
    static readonly (string Label, KeyFn Fn)[] Actions =
    [
        ("Left click", new(1, 1, 0)), ("Right click", new(1, 2, 0)), ("Middle click", new(1, 4, 0)),
        ("Back", new(1, 8, 0)), ("Forward", new(1, 16, 0)),
        ("DPI: next stage (loops)", new(2, 1, 0)), ("DPI: stage up", new(2, 2, 0)), ("DPI: stage down", new(2, 3, 0)),
        ("Scroll up", new(0x0B, 1, 0)), ("Scroll down", new(0x0B, 2, 0)), ("Scroll left", new(3, 1, 0)), ("Scroll right", new(3, 2, 0)),
        ("Polling rate: next", new(7, 0, 0)), ("Disabled", new(0, 0, 0)),
    ];
    static readonly string[] KeyNames = ["LEFT", "RIGHT", "WHEEL CLICK", "SIDE REAR", "SIDE FRONT", "DPI BUTTON"];
    static readonly Color[] Palette =
    [
        Color.FromRgb(255, 59, 48), Color.FromRgb(255, 149, 0), Color.FromRgb(255, 214, 10), Color.FromRgb(48, 209, 88),
        Color.FromRgb(100, 210, 255), Color.FromRgb(10, 132, 255), Color.FromRgb(191, 90, 242), Color.FromRgb(255, 255, 255),
    ];
    const string Custom = "CUSTOM (set in the original app)";

    readonly bool _demo;
    readonly bool _unlock;   // --unlock: allow writing to a model that isn't in the tested list
    readonly string? _shot;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _dpiSave = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly DispatcherTimer _debounceSave = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly List<TextBlock> _stageNumbers = [];
    readonly List<ComboBox> _keyBoxes = [];

    // receiver LED: a mode byte followed by three RGB colors, read with one command and written back whole with another.
    // On the 8K receiver (firmware 2.02) the colors had no visible effect in any mode (tested by eye in polling and
    // battery modes: the light kept its built-in colors), and mode 4 (DPI level in the manufacturer's list) stayed dark.
    // So only the four modes below are offered and the colors are passed through untouched.
    const byte CmdReceiverVersion = 0x1D, CmdSetReceiverLed = 0x14, CmdGetReceiverLed = 0x15;
    static readonly (string Label, int Mode)[] ReceiverModes = [("OFF", 0), ("POLLING", 1), ("BATTERY", 2), ("LOW BATT ONLY", 3)];
    static readonly string[] TestedReceivers = ["3554:FB44"];

    Theme _theme = Theme.All[0];
    ILink? _link;
    ILink? _rx;         // the wireless receiver; the same object as _link when the mouse is talking through it
    byte[]? _rxLed;
    int _rxSearch;
    Mouse? _m;
    bool _loading, _ticking;
    int _sel;

    public MainWindow()
    {
        var args = Environment.GetCommandLineArgs();
        bool demoUntested = args.Contains("--demo-untested");   // demo of what an untested model looks like
        _demo = args.Contains("--demo") || demoUntested;
        _unlock = args.Contains("--unlock") || (_demo && !demoUntested);
        int at = Array.IndexOf(args, "--shot");
        if (at >= 0 && at + 1 < args.Length) _shot = args[at + 1];

        // --theme NAME tries a theme for this run only; otherwise use the one saved last time
        int themeAt = Array.IndexOf(args, "--theme");
        _theme = themeAt >= 0 && themeAt + 1 < args.Length ? Theme.Find(args[themeAt + 1]) : Theme.LoadSaved();
        _theme.Apply();

        InitializeComponent();
        _loading = true;
        foreach (var t in Theme.All) ThemeBox.Items.Add(t.Name);
        ThemeBox.SelectedIndex = Array.IndexOf(Theme.All, _theme);
        _loading = false;
        Logo.Text = """
              ,.                              _,
             /::\.                       _.-:::/
             |::::`.                 _.-:::::/'
             `.:::::`-.          _.-::::::.-'
               `.:::::::`-.__.-::::::::.-'
                 `-.::::::::::::::::.-'
                    `-.:::::::::.-'
                       `.:::::.'
                         |::::|
                         |::::|
            ~~-~~~~~~~~~~|::::|~~~~~-~~~~~~~-~~~
            """;
        BuildStaticControls();

        _poll.Tick += async (_, _) => await Tick();
        _dpiSave.Tick += (_, _) => { _dpiSave.Stop(); int dpi = CurrentSliderDpi; Apply(m => m.SetDpi(_sel, dpi), "DPI"); };
        _debounceSave.Tick += (_, _) => { _debounceSave.Stop(); int ms = (int)DebounceSlider.Value; Apply(m => m.SetDebounce(ms), "Click response time"); };
        SourceInitialized += (_, _) => ApplyTitleBar();
        Loaded += async (_, _) =>
        {
            await Tick();
            _poll.Start();
            if (_shot != null) { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); SaveScreenshot(_shot); Close(); }
        };
        Closed += (_, _) => { _link?.Dispose(); _rx?.Dispose(); };
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---------- connection ----------
    async Task Tick()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            // Until a mouse is answering, keep rescanning: it may be on the cable, on the receiver, or switch between them.
            bool rescan = _link is not { Alive: true } || _m is not { Online: true };
            // With the mouse on its cable the receiver is a separate device; look for it now and then in case it gets plugged in later.
            if (!rescan && _rx is not { Alive: true } && ++_rxSearch % 5 == 0) rescan = true;
            if (rescan)
            {
                bool keepMouse = _link is { Alive: true } && _m is { Online: true };
                if (!keepMouse) { if (_rx != _link) _link?.Dispose(); _link = null; _m = null; }
                if (_rx != _link) _rx?.Dispose();
                _rx = null;
                List<ILink> found = _demo ? [keepMouse ? _link! : new DemoLink()] : [.. HidLink.OpenAll()];
                foreach (var l in found)
                {
                    bool used = false;
                    if (await l.Xfer(CmdReceiverVersion) is { } v && (v[0] | v[1]) != 0 && _rx == null)
                    {
                        // if the mouse is already talking through this receiver, reuse that connection
                        if (keepMouse && _m!.ReceiverFirmware != "") { _rx = _link; }
                        else { _rx = l; used = true; }
                    }
                    if (!keepMouse && _m is not { Online: true })
                    {
                        var candidate = new Mouse(l) { Unlocked = _unlock };
                        bool online = await candidate.CheckOnline();
                        if (online || _link == null)
                        {
                            if (_link != null && _link != _rx) _link.Dispose();
                            _link = l; _m = candidate; used = true;
                        }
                    }
                    if (!used && l != _link) l.Dispose();
                }
                if (!keepMouse && _link != null) _link.StatusChanged += () => Dispatcher.BeginInvoke(async () => { if (_m is { Online: true } m && await m.Load()) Populate(); });
                _rxLed = _rx == null ? null : await _rx.Xfer(CmdGetReceiverLed);
                PopulateReceiver();
            }
            if (_m is { } mouse)
            {
                bool wasLoaded = mouse.Loaded;
                if (await mouse.CheckOnline())
                {
                    if (!wasLoaded) { if (await mouse.Load()) { _sel = mouse.DpiIndex; Populate(); } }
                    else await mouse.ReadBattery();
                }
            }
            UpdateChrome();
        }
        finally { _ticking = false; }
    }

    void UpdateChrome()
    {
        var m = _m;
        bool ready = m is { Online: true, Loaded: true };
        // everything except the receiver's own box needs a mouse to talk to
        foreach (var column in new[] { ColLeft, ColRight })
            foreach (UIElement box in column.Children)
                if (box != RxBox) { box.IsEnabled = ready; box.Opacity = ready ? 1 : 0.18; }
        Offline.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;

        if (m == null)
        {
            SetState("Bad", "NO DEVICE");
            OfflineTitle.Text = "!! NO MOUSE FOUND";
            OfflineBody.Text = "Plug in the mouse's wireless receiver or its USB cable. Rescue Whale connects on its own as soon as it appears.";
        }
        else if (!ready)
        {
            SetState("Warn", "NO SIGNAL");
            OfflineTitle.Text = ".. WAITING FOR THE MOUSE";
            OfflineBody.Text = "The receiver is connected, but the mouse isn't answering. Move it, switch it on, or plug in its cable. Rescue Whale picks it up within a couple of seconds.";
        }
        else if (!m!.CanWrite)
        {
            SetState("Warn", "ONLINE, READ-ONLY");
            Status(m.Verified
                ? "read-only: this model hasn't been tested yet, so nothing will be written to it. click the status box to copy its details for a report"
                : "read-only: this mouse stores its settings in a way Rescue Whale doesn't recognize, so nothing will be written to it", true);
        }
        else SetState("Accent", _demo ? "DEMO MODE" : m.ReceiverFirmware == "" ? "ONLINE / CABLE" : "ONLINE / WIRELESS");

        if (ready && m!.Battery >= 0)
        {
            int cells = (int)Math.Round(m.Battery / 10.0);
            BatteryText.Text = $"[{new string('#', cells)}{new string('.', 10 - cells)}] {m.Battery}%{(m.Charging ? " CHG" : "")}";
            BatteryText.Foreground = (Brush)FindResource(m.Battery <= 15 && !m.Charging ? "Bad" : "Text");
        }
        else { BatteryText.Text = "--"; BatteryText.Foreground = (Brush)FindResource("Muted"); }

        ModelText.Text = ready && m!.Model != "" ? m.Model.ToUpperInvariant() : "--";
        IdText.Text = ready ? $"usb {m!.Link.Title}  model {(m.ModelId != "" ? m.ModelId : "?")}" : m != null ? "usb " + m.Link.Title : "--";
        FirmwareText.Text = !ready ? "--"
            : (m!.MouseFirmware != "" ? "mouse " + m.MouseFirmware : "mouse ?") + (m.ReceiverFirmware != "" ? "  receiver " + m.ReceiverFirmware : "");
    }

    // ---------- receiver LED ----------
    void PopulateReceiver()
    {
        RxBox.Visibility = _rxLed != null ? Visibility.Visible : Visibility.Collapsed;
        if (_rxLed == null) return;
        bool was = _loading;
        _loading = true;
        PickSeg(RxSeg, _rxLed[0]);
        _loading = was;
    }

    void SetReceiverMode(int mode)
    {
        if (!_loading) WriteReceiver(d => d[0] = (byte)mode, "receiver led");
    }

    /// <summary>Changes the receiver's LED setting. It is one 10-byte block, so every change sends the whole block back.</summary>
    async void WriteReceiver(Action<byte[]> change, string what)
    {
        if (_rx is not { Alive: true } rx || _rxLed is not { } current) return;
        if (!_unlock && !TestedReceivers.Contains(rx.Title))
        {
            Status("read-only: this receiver hasn't been tested yet, so nothing will be written to it", true);
            PopulateReceiver();
            return;
        }
        var data = (byte[])current.Clone();
        change(data);
        if (await rx.Xfer(CmdSetReceiverLed, 0, data) != null) { _rxLed = data; Status(what + " saved to the receiver"); }
        else Status($"couldn't save the {what}: the receiver didn't confirm it", true);
        PopulateReceiver();
    }

    /// <summary>Copies the status box as text, so it can be pasted into a bug report or a new-mouse report.</summary>
    void StatusBox_Click(object sender, MouseButtonEventArgs e)
    {
        string text = $"LINK ...... {StateText.Text}\nBATTERY ... {BatteryText.Text}\nMODEL ..... {ModelText.Text}\nDEVICE ID . {IdText.Text}\nFIRMWARE .. {FirmwareText.Text}";
        try { Clipboard.SetText(text); Status("device details copied to the clipboard"); }
        catch { Status("couldn't reach the clipboard, try again", true); }
    }

    // ---------- theme ----------
    void ApplyTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = _theme.IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
    }

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedIndex < 0) return;
        _theme = Theme.All[ThemeBox.SelectedIndex];
        _theme.Apply();
        _theme.Save();
        ApplyTitleBar();
        Populate();          // the DPI stage cards are built in code, so rebuild them in the new colors
        UpdateChrome();
        Status("theme set to " + _theme.Name.ToLowerInvariant());
    }

    void SetState(string brush, string text) { StateText.Foreground = (Brush)FindResource(brush); StateText.Text = text; }

    void Status(string text, bool bad = false)
    {
        StatusText.Text = "> " + text + "_";
        StatusText.Foreground = (Brush)FindResource(bad ? "Bad" : "Muted");
    }

    /// <summary>Sends one change to the mouse and reports the outcome in the status line.</summary>
    async void Apply(Func<Mouse, Task<bool>> change, string what, bool refresh = false)
    {
        if (_loading || _m is not { } m) return;
        bool ok = m.CanWrite && await change(m);
        if (ok) Status(what.ToLowerInvariant() + " saved to the mouse");
        else if (m.CanWrite) Status($"couldn't save {what.ToLowerInvariant()}: the mouse didn't confirm it. showing what's on the mouse now", true);
        if (!ok || refresh) Populate();
    }

    // ---------- building controls ----------
    void BuildStaticControls()
    {
        BuildSeg(RateSeg, "rate", Mouse.Rates.Select(r => (r.Hz >= 1000 ? r.Hz / 1000 + "K" : r.Hz.ToString(), r.Hz)), (m, v) => m.SetPolling(v), "Polling rate");
        BuildSeg(LodSeg, "lod", [("0.7 MM", 3), ("1 MM", 1), ("2 MM", 2)], (m, v) => m.SetLod(v), "Lift-off distance");
        BuildSeg(ModeSeg, "mode", [("LOW POWER", 0), ("HIGH PERF", 1)], (m, v) => m.SetSensorMode(v), "Sensor mode");
        BuildSeg(SleepSeg, "sleep", [("10S", 1), ("30S", 3), ("1M", 6), ("2M", 12), ("3M", 18), ("5M", 30), ("10M", 60), ("15M", 90)], (m, v) => m.SetSleep(v), "Sleep time");

        foreach (var (label, mode) in ReceiverModes)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Seg"), Content = label, Tag = mode, GroupName = "rxled" };
            rb.Checked += (_, _) => SetReceiverMode(mode);
            RxSeg.Children.Add(rb);
        }

        foreach (int p in new[] { 400, 800, 1600, 3200 })
        {
            var b = new Button { Style = (Style)FindResource("Ghost"), Content = p.ToString(), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => CommitDpi(p);
            Presets.Children.Add(b);
        }
        foreach (var c in Palette)
        {
            var dot = new Rectangle { Width = 16, Height = 16, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand, StrokeThickness = 1 };
            dot.SetResourceReference(Shape.StrokeProperty, "Line");   // an outline, so white stays visible on a light theme
            dot.MouseLeftButtonUp += (_, _) => Apply(m => m.SetDpiColor(_sel, c.R, c.G, c.B), "Indicator color", true);
            Swatches.Children.Add(dot);
        }
        for (int i = 0; i < Mouse.KeyCount; i++)
        {
            int key = i;
            var row = new DockPanel { Margin = new Thickness(0, i == 0 ? 0 : 7, 0, 0) };
            var box = new ComboBox();
            box.SelectionChanged += (_, _) => KeyChanged(key, box);
            row.Children.Add(new TextBlock { Text = KeyNames[i], Width = 150, Style = (Style)FindResource("Label") });
            row.Children.Add(box);
            KeyRows.Children.Add(row);
            _keyBoxes.Add(box);
        }
    }

    void BuildSeg(Panel host, string group, IEnumerable<(string Label, int Value)> options, Func<Mouse, int, Task<bool>> set, string what)
    {
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Seg"), Content = label, Tag = value, GroupName = group };
            rb.Checked += (_, _) => Apply(m => set(m, value), what);
            host.Children.Add(rb);
        }
    }

    static void PickSeg(Panel host, int value)
    {
        foreach (RadioButton rb in host.Children) rb.IsChecked = (int)rb.Tag == value;
    }

    // ---------- showing the mouse's settings ----------
    void Populate()
    {
        if (_m is not { Loaded: true } m) return;
        _loading = true;
        try
        {
            _sel = Math.Clamp(_sel, 0, m.DpiCount - 1);
            PickSeg(RateSeg, m.PollingHz);
            PickSeg(LodSeg, m.Lod);
            PickSeg(ModeSeg, m.SensorMode);
            PickSeg(SleepSeg, m.SleepCode);
            SwMotion.IsChecked = m.MotionSync == 1;
            SwAngle.IsChecked = m.AngleSnap == 1;
            SwRipple.IsChecked = m.Ripple == 1;
            DebounceSlider.Value = Math.Max(0, m.Debounce);
            DebounceText.Text = Math.Max(0, m.Debounce) + " MS";

            BuildStages(m);
            DpiBox.Text = m.Dpi(_sel).ToString();
            DpiSlider.Value = PosFromDpi(m.Dpi(_sel));
            RemoveStage.IsEnabled = m.DpiCount > 1;

            for (int i = 0; i < Mouse.KeyCount; i++)
            {
                var box = _keyBoxes[i];
                int idx = Array.FindIndex(Actions, a => a.Fn == m.Key(i));
                box.Items.Clear();
                foreach (var a in Actions) box.Items.Add(a.Label);
                if (idx < 0) { box.Items.Add(Custom); idx = Actions.Length; }
                box.SelectedIndex = idx;
            }
        }
        finally { _loading = false; }
        UpdateChrome();
    }

    void BuildStages(Mouse m)
    {
        Stages.Children.Clear();
        _stageNumbers.Clear();
        for (int i = 0; i < m.DpiCount; i++)
        {
            int stage = i;
            var (r, g, b) = m.DpiColor(i);
            bool selected = i == _sel;
            var number = new TextBlock { Text = m.Dpi(i).ToString("N0"), FontSize = 20, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(0, 4, 0, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Rectangle { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(r, g, b)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            head.Children.Add(new TextBlock { Text = "STAGE " + (i + 1), Style = (Style)FindResource("Hint") });
            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(number);
            body.Children.Add(new TextBlock
            {
                Text = i == m.DpiIndex ? "* ACTIVE" : " ", FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("Warn"), Margin = new Thickness(0, 2, 0, 0),
            });
            var card = new Border
            {
                Background = (Brush)FindResource("Panel2"), Padding = new Thickness(10, 8, 8, 6), Margin = new Thickness(3, 0, 3, 0),
                BorderThickness = new Thickness(selected ? 2 : 1), BorderBrush = (Brush)FindResource(selected ? "Accent" : "Line"), Cursor = Cursors.Hand, Child = body,
            };
            card.MouseLeftButtonUp += (_, _) => { _sel = stage; Apply(mm => mm.SetDpiIndex(stage), "Active stage", true); };
            Stages.Children.Add(card);
            _stageNumbers.Add(number);
        }
        if (m.DpiCount < Mouse.MaxStages)
        {
            var add = new Border
            {
                Margin = new Thickness(3, 0, 3, 0), BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("Line"),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "[+] ADD", Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            add.MouseLeftButtonUp += (_, _) => { int n = m.DpiCount; _sel = n; Apply(mm => mm.SetDpiCount(n + 1), "New stage", true); };
            Stages.Children.Add(add);
        }
    }

    // ---------- DPI editing ----------
    // The slider is curved so everyday values (400–3200) aren't crammed into the first few pixels of a 50–42,000 range.
    const int Steps = 679;
    static double PosFromDpi(int dpi) => 1000 * Math.Sqrt((dpi <= 26000 ? dpi / 50 - 1 : 519 + (dpi - 26000) / 100) / (double)Steps);
    static int DpiFromPos(double pos)
    {
        int i = (int)Math.Round(Steps * Math.Pow(pos / 1000, 2));
        return i < 520 ? (i + 1) * 50 : 26000 + (i - 519) * 100;
    }
    int CurrentSliderDpi => DpiFromPos(DpiSlider.Value);

    void DpiSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        int dpi = CurrentSliderDpi;
        DpiBox.Text = dpi.ToString();
        if (_sel < _stageNumbers.Count) _stageNumbers[_sel].Text = dpi.ToString("N0");
        _dpiSave.Stop(); _dpiSave.Start();
    }

    void CommitDpi(int dpi)
    {
        dpi = Mouse.SnapDpi(dpi);
        _dpiSave.Stop();
        _loading = true;
        DpiSlider.Value = PosFromDpi(dpi);
        DpiBox.Text = dpi.ToString();
        if (_sel < _stageNumbers.Count) _stageNumbers[_sel].Text = dpi.ToString("N0");
        _loading = false;
        Apply(m => m.SetDpi(_sel, dpi), "DPI");
    }

    void CommitDpiBox()
    {
        if (_loading || _m is not { Loaded: true } m) return;
        if (int.TryParse(DpiBox.Text.Replace(",", "").Trim(), out int dpi)) { if (Mouse.SnapDpi(dpi) != m.Dpi(_sel)) CommitDpi(dpi); else DpiBox.Text = m.Dpi(_sel).ToString(); }
        else DpiBox.Text = m.Dpi(_sel).ToString();
    }
    void DpiBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitDpiBox(); DpiBox.SelectAll(); } }
    void DpiBox_LostFocus(object sender, RoutedEventArgs e) => CommitDpiBox();

    void RemoveStage_Click(object sender, RoutedEventArgs e)
    {
        int i = _sel;
        _sel = Math.Max(0, _sel - 1);
        Apply(m => m.RemoveStage(i), "Stage removal", true);
    }

    // ---------- other handlers ----------
    void KeyChanged(int key, ComboBox box)
    {
        if (_loading || _m is not { } m || box.SelectedIndex < 0 || box.SelectedIndex >= Actions.Length) return;
        var fn = Actions[box.SelectedIndex].Fn;
        var leftClick = Actions[0].Fn;
        bool leftRemains = Enumerable.Range(0, Mouse.KeyCount).Any(i => (i == key ? fn : m.Key(i)) == leftClick);
        if (!leftRemains)
        {
            Status("one button has to stay as left click, or you'd have no way to click", true);
            Populate();
            return;
        }
        Apply(mm => mm.SetKey(key, fn), KeyNames[key], true);
    }

    void Switch_Click(object sender, RoutedEventArgs e)
    {
        var sw = (CheckBox)sender;
        bool on = sw.IsChecked == true;
        if (sw == SwMotion) Apply(m => m.SetMotionSync(on), "Motion sync");
        else if (sw == SwAngle) Apply(m => m.SetAngleSnap(on), "Angle snapping");
        else Apply(m => m.SetRipple(on), "Ripple control");
    }

    void DebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DebounceText != null) DebounceText.Text = (int)e.NewValue + " MS";
        if (_loading) return;
        _debounceSave.Stop(); _debounceSave.Start();
    }

    void SaveScreenshot(string path)
    {
        var bmp = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(Root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
