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
    readonly string? _shot;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _dpiSave = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly DispatcherTimer _debounceSave = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly List<TextBlock> _stageNumbers = [];
    readonly List<ComboBox> _keyBoxes = [];

    ILink? _link;
    Mouse? _m;
    bool _loading, _ticking;
    int _sel;

    public MainWindow()
    {
        var args = Environment.GetCommandLineArgs();
        _demo = args.Contains("--demo");
        int at = Array.IndexOf(args, "--shot");
        if (at >= 0 && at + 1 < args.Length) _shot = args[at + 1];

        InitializeComponent();
        Whale.Text = """
                     . .
                      Y
                 .-~~~~~~-.      __
               .'  o       `-.__/ /
               `.___________,---'
            ~~~~~~~~~~~~~~~~~~~~~~~~~~
            """;
        BuildStaticControls();

        _poll.Tick += async (_, _) => await Tick();
        _dpiSave.Tick += (_, _) => { _dpiSave.Stop(); int dpi = CurrentSliderDpi; Apply(m => m.SetDpi(_sel, dpi), "DPI"); };
        _debounceSave.Tick += (_, _) => { _debounceSave.Stop(); int ms = (int)DebounceSlider.Value; Apply(m => m.SetDebounce(ms), "Click response time"); };
        SourceInitialized += (_, _) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); };
        Loaded += async (_, _) =>
        {
            await Tick();
            _poll.Start();
            if (_shot != null) { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); SaveScreenshot(_shot); Close(); }
        };
        Closed += (_, _) => _link?.Dispose();
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
            if (_link is not { Alive: true } || _m is not { Online: true })
            {
                _link?.Dispose();
                _link = null; _m = null;
                List<ILink> found = _demo ? [new DemoLink()] : [.. HidLink.OpenAll()];
                foreach (var l in found)
                {
                    if (_m is { Online: true }) { l.Dispose(); continue; }
                    var candidate = new Mouse(l);
                    bool online = await candidate.CheckOnline();
                    if (online || _link == null) { _link?.Dispose(); _link = l; _m = candidate; }
                    else l.Dispose();
                }
                if (_link != null) _link.StatusChanged += () => Dispatcher.BeginInvoke(async () => { if (_m is { Online: true } m && await m.Load()) Populate(); });
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
        Pages.IsEnabled = ready;
        Pages.Opacity = ready ? 1 : 0.18;
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
        else if (!m!.Verified)
        {
            SetState("Warn", "ONLINE, READ-ONLY");
            Status("this mouse stores its settings in a way Rescue Whale doesn't recognize, so nothing will be written to it.", true);
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
        FirmwareText.Text = !ready ? "--"
            : (m!.MouseFirmware != "" ? "mouse " + m.MouseFirmware : "mouse ?") + (m.ReceiverFirmware != "" ? "  receiver " + m.ReceiverFirmware : "");
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
        bool ok = m.Verified && await change(m);
        if (ok) Status(what.ToLowerInvariant() + " saved to the mouse");
        else if (m.Verified) Status($"couldn't save {what.ToLowerInvariant()}: the mouse didn't confirm it. showing what's on the mouse now", true);
        if (!ok || refresh) Populate();
    }

    // ---------- building controls ----------
    void BuildStaticControls()
    {
        BuildSeg(RateSeg, "rate", Mouse.Rates.Select(r => (r.Hz >= 1000 ? r.Hz / 1000 + "K" : r.Hz.ToString(), r.Hz)), (m, v) => m.SetPolling(v), "Polling rate");
        BuildSeg(LodSeg, "lod", [("0.7 MM", 3), ("1 MM", 1), ("2 MM", 2)], (m, v) => m.SetLod(v), "Lift-off distance");
        BuildSeg(ModeSeg, "mode", [("LOW POWER", 0), ("HIGH PERF", 1)], (m, v) => m.SetSensorMode(v), "Sensor mode");
        BuildSeg(SleepSeg, "sleep", [("10S", 1), ("30S", 3), ("1M", 6), ("2M", 12), ("3M", 18), ("5M", 30), ("10M", 60), ("15M", 90)], (m, v) => m.SetSleep(v), "Sleep time");

        foreach (int p in new[] { 400, 800, 1600, 3200 })
        {
            var b = new Button { Style = (Style)FindResource("Ghost"), Content = p.ToString(), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => CommitDpi(p);
            Presets.Children.Add(b);
        }
        foreach (var c in Palette)
        {
            var dot = new Rectangle { Width = 16, Height = 16, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand };
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
