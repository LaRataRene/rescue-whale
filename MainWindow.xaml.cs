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
        // The five colors the X8 Ultra's DPI light can actually show, found by eye: red, green, light blue, blue, magenta.
        // The light drops its red whenever green is also on, so orange and yellow come out green and white comes out
        // light blue. Softer in-between shades came out wrong too, so every channel is either full on or off.
        Color.FromRgb(255, 0, 0), Color.FromRgb(0, 255, 0), Color.FromRgb(0, 255, 255), Color.FromRgb(0, 0, 255), Color.FromRgb(255, 0, 255),
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

    static readonly (string Label, int Hours)[] BatteryRanges = [("24H", 24), ("7D", 168), ("30D", 720)];
    BatteryLog _battery = new(false);
    int _batteryHours = 24;
    DateTime _batteryClosedAt;

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
        // A whale's tail mid-swing, flinging drops off the high fluke, with a life ring around it at the waterline.
        // Two layers with identical spacing: the tail and water, then the ring on top in its own color.
        Logo.Text = """
                                        _.-.
              ,.                    _.-:::/   '
             /::\.              _.-:::::.'  .
             |::::`._       _.-::::::.-'
             `.::::::`-.._.-:::::::.-'     '
               `-.:::::::::::::::.-'
                  `-.:::::::::.-'
                     `.::::::.'
                       \:::::\
                        \:::::\
            ~~-~~~~      \:::::\    ~~~-~~~~~~~
            """;
        LogoBuoy.Text = """









                     .=#       #=.
                   (#=           =#)
                    `=#=#=#=#=#=#='
            """;
        // --size WIDTHxHEIGHT opens the window at a given size, for checking how it scales
        int sizeAt = Array.IndexOf(args, "--size");
        if (sizeAt >= 0 && sizeAt + 1 < args.Length && args[sizeAt + 1].Split('x') is [var sw, var sh] && double.TryParse(sw, out double width) && double.TryParse(sh, out double height))
        {
            Width = width;
            Height = height;
        }
        BuildStaticControls();
        StripText.Text = "rescue whale v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "?");

        // battery history: kept on disk for a real mouse, made up in memory for demo mode
        _battery = new BatteryLog(persist: !_demo);
        if (_demo) _battery.FillDemo();
        foreach (var (label, hours) in BatteryRanges)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Seg"), Content = label, GroupName = "battrange", IsChecked = hours == _batteryHours };
            rb.Checked += (_, _) => { _batteryHours = hours; if (BatteryPopup.IsOpen) DrawBatteryGraph(); };
            BatteryRange.Children.Add(rb);
        }

        _poll.Tick += async (_, _) => await Tick();
        _dpiSave.Tick += (_, _) => { _dpiSave.Stop(); int dpi = CurrentSliderDpi; Apply(m => m.SetDpi(_sel, dpi), "DPI"); };
        _debounceSave.Tick += (_, _) => { _debounceSave.Stop(); int ms = (int)DebounceSlider.Value; Apply(m => m.SetDebounce(ms), "Click response time"); };
        SourceInitialized += (_, _) => ApplyTitleBar();
        Loaded += async (_, _) =>
        {
            await Tick();
            _poll.Start();
            if (_shot != null)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(Root, _shot);
                // --battery also captures the history dropdown, which lives in its own pop-up window
                if (args.Contains("--battery"))
                {
                    int rangeAt = Array.IndexOf(args, "--battery");
                    if (rangeAt + 1 < args.Length && int.TryParse(args[rangeAt + 1], out int hours))
                        for (int i = 0; i < BatteryRanges.Length; i++)
                            if (BatteryRanges[i].Hours == hours) ((RadioButton)BatteryRange.Children[i]).IsChecked = true;
                    DrawBatteryGraph();
                    BatteryPopup.IsOpen = true;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    SaveScreenshot(BatteryPanel, System.IO.Path.ChangeExtension(_shot, null) + "-battery.png");
                }
                Close();
            }
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
                    if (!_demo && mouse.Loaded && mouse.Battery >= 0) _battery.Add(mouse.Battery, mouse.Charging);
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

        // the battery gauge next to the status box
        if (ready && m!.Battery >= 0)
        {
            var color = BatteryColor(m.Battery);
            BatteryIcon.Text = BatteryArt(m.Battery);
            BatteryIcon.Foreground = color;
            BatteryPct.Text = m.Battery + "%";
            BatteryPct.Foreground = color;
            BatteryNote.Text = m.Charging ? "CHARGING" : m.Battery < 20 ? "LOW BATTERY" : "BATTERY";
        }
        else
        {
            BatteryIcon.Text = BatteryArt(0);
            BatteryIcon.Foreground = (Brush)FindResource("Muted");
            BatteryPct.Text = "--";
            BatteryPct.Foreground = (Brush)FindResource("Muted");
            BatteryNote.Text = "BATTERY";
        }
        if (BatteryPopup.IsOpen) DrawBatteryGraph();

        ModelText.Text = ready && m!.Model != "" ? m.Model.ToUpperInvariant() : "--";
        IdText.Text = ready ? $"usb {m!.Link.Title}  model {(m.ModelId != "" ? m.ModelId : "?")}" : m != null ? "usb " + m.Link.Title : "--";
        FirmwareText.Text = !ready ? "--"
            : (m!.MouseFirmware != "" ? "mouse " + m.MouseFirmware : "mouse ?") + (m.ReceiverFirmware != "" ? "  receiver " + m.ReceiverFirmware : "");
    }

    // ---------- battery ----------
    /// <summary>Green from half full, amber below that, red under 20%. Darker shades on a light theme so they stay readable.</summary>
    Brush BatteryColor(int percent)
    {
        string hex = percent >= 50 ? (_theme.IsLight ? "#1E8E3E" : "#30D158")
                   : percent >= 20 ? (_theme.IsLight ? "#B26A00" : "#FFB627")
                   : (_theme.IsLight ? "#B00020" : "#FF5555");
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    /// <summary>
    /// The battery as text: five rows, each worth 20%. A row is solid (#) when full and dotted (:) when at least half full,
    /// so the picture moves in steps of 10%. Any charge at all shows at least a dotted row.
    /// </summary>
    static string BatteryArt(int percent)
    {
        int full = percent / 20;
        bool half = full < 5 && (percent % 20 >= 10 || (percent > 0 && full == 0));
        var rows = new List<string> { " .--. " };
        for (int row = 5; row >= 1; row--)
            rows.Add(row <= full ? "|####|" : row == full + 1 && half ? "|::::|" : "|    |");
        rows.Add("'----'");
        return string.Join("\n", rows);
    }

    void Battery_Click(object sender, MouseButtonEventArgs e)
    {
        // a click on the gauge while the graph is open first closes it (it counts as a click outside); don't reopen it straight away
        if ((DateTime.UtcNow - _batteryClosedAt).TotalMilliseconds < 300) return;
        DrawBatteryGraph();
        BatteryPopup.IsOpen = true;
    }

    void BatteryPopup_Closed(object? sender, EventArgs e) => _batteryClosedAt = DateTime.UtcNow;

    /// <summary>Draws battery level against time for the chosen range: solid while on battery, highlighted while charging, dashed across gaps.</summary>
    void DrawBatteryGraph()
    {
        var g = BatteryGraph;
        g.Children.Clear();
        double left = 46, right = 10, top = 8, bottom = 24;
        double plotW = g.Width - left - right, plotH = g.Height - top - bottom;
        var now = DateTime.Now;
        var from = now.AddHours(-_batteryHours);
        Brush line = (Brush)FindResource("Line"), muted = (Brush)FindResource("Muted"), text = (Brush)FindResource("Text"), warn = (Brush)FindResource("Warn");
        double X(DateTime t) => left + plotW * (t - from).TotalSeconds / (now - from).TotalSeconds;
        double Y(double percent) => top + plotH * (1 - percent / 100);

        void Label(string s, double x, double y, bool rightAlign = false, bool center = false)
        {
            var tb = new TextBlock { Text = s, FontSize = 11, Foreground = muted };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, rightAlign ? x - tb.DesiredSize.Width : center ? x - tb.DesiredSize.Width / 2 : x);
            Canvas.SetTop(tb, y);
            g.Children.Add(tb);
        }

        // level grid
        foreach (int p in new[] { 0, 25, 50, 75, 100 })
        {
            g.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = Y(p), Y2 = Y(p), Stroke = line, StrokeThickness = 1, StrokeDashArray = p is 0 or 100 ? null : [2, 4] });
            Label(p + "%", left - 6, Y(p) - 7, rightAlign: true);
        }

        // time grid: every 6 hours, every day, or every 5 days
        var step = _batteryHours <= 24 ? TimeSpan.FromHours(6) : _batteryHours <= 168 ? TimeSpan.FromDays(1) : TimeSpan.FromDays(5);
        string format = _batteryHours <= 24 ? "h tt" : _batteryHours <= 168 ? "ddd" : "M/d";
        var tick = from.Date;
        while (tick <= from) tick += step;
        for (; tick < now; tick += step)
        {
            g.Children.Add(new Line { X1 = X(tick), X2 = X(tick), Y1 = top, Y2 = top + plotH, Stroke = line, StrokeThickness = 1, StrokeDashArray = [2, 4] });
            Label(tick.ToString(format), X(tick), top + plotH + 5, center: true);
        }

        // readings in range, plus the one just before it so the line starts at the left edge
        var all = _battery.Samples;
        int first = 0;
        while (first < all.Count && all[first].Time < from) first++;
        var pts = all.Skip(Math.Max(0, first - 1)).ToList();
        int inRange = all.Count - first;

        if (inRange < 2)
        {
            var none = new TextBlock { Text = "not enough history in this range yet.\nit builds up while rescue whale is open and the mouse is awake.", Foreground = muted, TextAlignment = TextAlignment.Center };
            none.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(none, left + (plotW - none.DesiredSize.Width) / 2);
            Canvas.SetTop(none, top + (plotH - none.DesiredSize.Height) / 2);
            g.Children.Add(none);
        }
        else
        {
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                double aPercent = a.Percent;
                var aTime = a.Time;
                if (aTime < from)   // starts off the left edge: begin where the line crosses it
                {
                    aPercent += (b.Percent - a.Percent) * (from - a.Time).TotalSeconds / (b.Time - a.Time).TotalSeconds;
                    aTime = from;
                }
                bool gap = b.Time - a.Time > TimeSpan.FromMinutes(30);
                bool charging = b.Charging || b.Percent > a.Percent;
                g.Children.Add(new Line
                {
                    X1 = X(aTime), Y1 = Y(aPercent), X2 = X(b.Time), Y2 = Y(b.Percent),
                    Stroke = gap ? muted : charging ? warn : text, StrokeThickness = gap ? 1 : 2, StrokeDashArray = gap ? [3, 3] : null,
                });
            }
            var last = pts[^1];
            var dot = new Rectangle { Width = 7, Height = 7, Fill = last.Charging ? warn : text };
            Canvas.SetLeft(dot, X(last.Time) - 3.5);
            Canvas.SetTop(dot, Y(last.Percent) - 3.5);
            g.Children.Add(dot);
        }

        // summary and legend
        var shown = all.Skip(first).ToList();
        var lastCharge = all.LastOrDefault(s => s.Charging);
        BatteryStats.Text = shown.Count == 0 ? "no readings in this range"
            : $"NOW {shown[^1].Percent}%   LOW {shown.Min(s => s.Percent)}%   HIGH {shown.Max(s => s.Percent)}%"
              + (lastCharge.Time != default ? $"   LAST ON CHARGE {lastCharge.Time:MMM d, h:mm tt}".ToUpperInvariant() : "");
        BatteryLegend.Inlines.Clear();
        BatteryLegend.Inlines.Add(new System.Windows.Documents.Run("■ on battery   ") { Foreground = text });
        BatteryLegend.Inlines.Add(new System.Windows.Documents.Run("■ charging   ") { Foreground = warn });
        BatteryLegend.Inlines.Add(new System.Windows.Documents.Run("- - no readings (app closed or mouse asleep)") { Foreground = muted });
    }

    // ---------- receiver LED ----------
    void PopulateReceiver()
    {
        bool show = _rxLed != null;
        if ((RxBox.Visibility == Visibility.Visible) != show)
        {
            RxBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Relayout();   // the box appearing or going changes how tall each arrangement is
        }
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
        string text = $"LINK ...... {StateText.Text}\nMODEL ..... {ModelText.Text}\nDEVICE ID . {IdText.Text}\nFIRMWARE .. {FirmwareText.Text}";
        try { Clipboard.SetText(text); Status("device details copied to the clipboard"); }
        catch { Status("couldn't reach the clipboard, try again", true); }
    }

    // ---------- window: no title bar, own buttons, contents shrink with the window ----------
    // Two arrangements of the same boxes: wide (two columns side by side) and tall (one column, header stacked).
    const double WideWidth = 1150, TallWidth = 612, StripHeight = 30;
    bool _tall;

    void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    /// <summary>
    /// Picks the arrangement that suits the window's shape, then scales it down if the window is smaller than it needs.
    /// A window bigger than the arrangement needs just gets more room.
    /// </summary>
    void Relayout()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double wideHeight = NaturalHeight(false), tallHeight = NaturalHeight(true);
        double wideScale = Math.Min(1, Math.Min(w / WideWidth, h / wideHeight));
        double tallScale = Math.Min(1, Math.Min(w / TallWidth, h / tallHeight));
        // whichever shows the interface larger wins; when both fit at full size, a window taller than it is wide goes tall
        bool tall = tallScale > wideScale + 0.001 || (Math.Abs(tallScale - wideScale) <= 0.001 && h >= w);
        double k = tall ? tallScale : wideScale;

        SetArrangement(tall);
        Scale.ScaleX = Scale.ScaleY = k;
        ScaleHost.Width = w / k;
        ScaleHost.Height = h / k;
        // keep the drag area matched to the strip, which shrinks with everything else
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = StripHeight * k;
    }

    /// <summary>How tall an arrangement is at its own width and full size.</summary>
    double NaturalHeight(bool tall)
    {
        SetArrangement(tall);
        Scale.ScaleX = Scale.ScaleY = 1;
        double width = tall ? TallWidth : WideWidth;
        ScaleHost.Width = width;
        ScaleHost.Height = double.NaN;
        ScaleHost.Measure(new Size(width, double.PositiveInfinity));
        return ScaleHost.DesiredSize.Height;
    }

    void SetArrangement(bool tall)
    {
        _tall = tall;
        // settings: the right-hand column drops below the left-hand one
        Grid.SetColumnSpan(ColLeft, tall ? 3 : 1);
        Grid.SetRow(ColRight, tall ? 1 : 0);
        Grid.SetColumn(ColRight, tall ? 0 : 2);
        Grid.SetColumnSpan(ColRight, tall ? 3 : 1);
        // header: the battery and status box drop below the logo and title
        Grid.SetColumnSpan(TitlePanel, tall ? 3 : 1);
        TitleText.FontSize = tall ? 20 : 26;
        Grid.SetRow(BatteryWidget, tall ? 1 : 0);
        Grid.SetColumn(BatteryWidget, tall ? 0 : 2);
        BatteryWidget.HorizontalAlignment = tall ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        BatteryWidget.Margin = tall ? new Thickness(0, 10, 0, 0) : new Thickness(0, 0, 22, 0);
        BatteryPopup.HorizontalOffset = tall ? 0 : -250;
        Grid.SetRow(StatusBox, tall ? 1 : 0);
        Grid.SetColumn(StatusBox, tall ? 1 : 3);
        Grid.SetColumnSpan(StatusBox, tall ? 3 : 1);
        StatusBox.HorizontalAlignment = tall ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        StatusBox.Margin = tall ? new Thickness(0, 10, 0, 0) : new Thickness(0);
    }

    void Window_StateChanged(object? sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        // a maximized window without a title bar hangs over the screen edges by the width of its resize border
        var edge = SystemParameters.WindowResizeBorderThickness;
        const double pad = 4;   // Windows adds this much padding around the resize border
        Frame.Margin = max ? new Thickness(edge.Left + pad, edge.Top + pad, edge.Right + pad, edge.Bottom + pad) : new Thickness(0);
        Frame.BorderThickness = new Thickness(max ? 0 : 1);
        MaxButton.Content = max ? "[v]" : "[^]";
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Dragging the logo or the title moves the window; double-clicking them maximizes or restores it.</summary>
    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } }
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
        Relayout();   // the DPI stage cards add height once they're filled in, which changes how much each arrangement needs
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

    static void SaveScreenshot(FrameworkElement element, string path)
    {
        var bmp = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(element);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
