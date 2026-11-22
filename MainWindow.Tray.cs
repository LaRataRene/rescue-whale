using System.Runtime.InteropServices;
using System.Windows;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace RescueWhale;

// Tray icons: one for the mouse and one for the controller, each shown only while that device is connected.
// The mouse icon shows its battery percentage; hovering shows the battery and the polling rate that is set.
// The controller icon shows its battery level (Windows reports only four steps) or USB on a cable.
public partial class MainWindow
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    sealed class TrayItem
    {
        public required WF.NotifyIcon Icon;
        public IntPtr Handle;      // the icon image currently shown, so it can be freed when replaced
        public string Key = "";    // what the image shows, so it's only redrawn when that changes
        public WF.ToolStripMenuItem? Info;
    }

    TrayItem? _trayMouse, _trayRate, _trayPad, _trayApp;   // mouse battery, mouse polling rate, controller, and the whale
    bool _exiting, _trayHintShown;

    // All tray icons are plain grayscale, and follow the taskbar's own light or dark setting:
    // light gray ink on a dark taskbar, dark gray ink on a light one, with a dimmer gray for the empty part of a level bar.
    static bool LightTaskbar
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
            }
            catch { return false; }
        }
    }
    static SD.Color TrayInk => LightTaskbar ? SD.Color.FromArgb(0x1C, 0x1C, 0x1C) : SD.Color.FromArgb(0xE8, 0xE8, 0xE8);
    static SD.Color TrayDim => LightTaskbar ? SD.Color.FromArgb(0xB4, 0xB4, 0xB4) : SD.Color.FromArgb(0x70, 0x70, 0x70);
    static SD.Color TrayEdge => LightTaskbar ? SD.Color.FromArgb(110, 255, 255, 255) : SD.Color.FromArgb(150, 0, 0, 0);

    void InitTray()
    {
        if (_shot != null) return;   // screenshot runs open and close in a moment; no point flashing tray icons
        _trayMouse = MakeTray();
        _trayRate = MakeTray();
        _trayPad = MakeTray();
        // the whale icon, shown only while the window is hidden and no device is connected, so there's always a way back
        _trayApp = MakeTray();
        _trayApp.Info!.Text = "Rescue Whale: no device connected";
        _trayApp.Icon.Text = "Rescue Whale\nNo device connected";
        try
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico")).Stream;
            _trayApp.Icon.Icon = new SD.Icon(stream, WF.SystemInformation.SmallIconSize);
        }
        catch { _trayApp.Icon.Icon = SD.SystemIcons.Application; }

        // closing the window hides it to the tray; Exit in the tray menu, or Windows shutting down, really quits
        Closing += (_, e) =>
        {
            if (_exiting || !_opt.HideOnClose) return;   // the close button can be set to quit instead
            e.Cancel = true;
            HideToTray();
        };
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        IsVisibleChanged += (_, _) => UpdateTray();
        Closed += (_, _) => { foreach (var t in new[] { _trayMouse, _trayRate, _trayPad, _trayApp }) if (t != null) { t.Icon.Visible = false; t.Icon.Dispose(); FreeTrayImage(t); } };
    }

    void HideToTray()
    {
        Hide();
        UpdateTray();
        if (_trayHintShown) return;
        _trayHintShown = true;
        var shown = new[] { _trayMouse, _trayRate, _trayPad, _trayApp }.FirstOrDefault(t => t is { Icon.Visible: true });
        shown?.Icon.ShowBalloonTip(4000, "Rescue Whale is still running",
            "It's in the tray. Click its icon to open it again, or right-click and choose Exit to quit.", WF.ToolTipIcon.None);
    }

    void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    TrayItem MakeTray()
    {
        var item = new TrayItem { Icon = new WF.NotifyIcon { Visible = false } };
        var menu = new WF.ContextMenuStrip();
        item.Info = new WF.ToolStripMenuItem { Enabled = false };
        menu.Items.Add(item.Info);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Open Rescue Whale", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        item.Icon.ContextMenuStrip = menu;
        item.Icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
        return item;
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Brings both tray icons up to date with what the app knows about each device.</summary>
    void UpdateTray()
    {
        if (_trayMouse == null || _trayRate == null || _trayPad == null) return;

        var m = _m;
        bool mouseReady = m is { Online: true, Loaded: true } && m.Battery >= 0;
        if (mouseReady)
        {
            string rate = m!.PollingHz > 0 ? $"{m.PollingHz:N0} Hz" : "unknown";
            string name = m.Model != "" ? m.Model : "Mouse";
            string tip = $"{name}\nBattery {m.Battery}%{(m.Charging ? ", charging" : "")}\nPolling rate {rate}";
            string info = $"{name}:  {m.Battery}%  ·  {rate}";
            int battery = m.Battery;

            if (_opt.BatteryIcon) SetTray(_trayMouse, $"b{battery}{LightTaskbar}", () => TextIcon(battery.ToString(), battery), tip, info);
            else _trayMouse.Icon.Visible = false;

            // 8000 -> 8K, 1000 -> 1K, 500 -> 500
            string shortRate = m.PollingHz >= 1000 ? m.PollingHz / 1000 + "K" : m.PollingHz > 0 ? m.PollingHz.ToString() : "?";
            if (_opt.PollingIcon) SetTray(_trayRate, "r" + shortRate + LightTaskbar, () => TextIcon(shortRate, null), tip, info);
            else _trayRate.Icon.Visible = false;

            CheckBatteryAlert(m);
        }
        else { _trayMouse.Icon.Visible = false; _trayRate.Icon.Visible = false; }

        if (_padSlot >= 0)
        {
            bool wired = _padBatteryType == 1, known = _padBatteryType > 0;
            string level = !known ? "unknown" : wired ? "on cable" : _padBatteryLevel switch { 0 => "empty", 1 => "low", 2 => "medium", _ => "full" };
            string tip = $"Xbox controller\nPower: {level}";
            int padLevel = _padBatteryLevel;
            if (wired || !known) SetTray(_trayPad, "p" + level + LightTaskbar, () => TextIcon(wired ? "USB" : "?", null), tip, "Xbox controller:  " + level);
            else SetTray(_trayPad, "p" + level + LightTaskbar, () => TrayBatteryIcon(padLevel), tip, "Xbox controller:  " + level + " battery");
        }
        else _trayPad.Icon.Visible = false;

        // the whale icon: the way back when the window is hidden and no device icon is showing, or to carry an alert
        if (_trayApp != null)
            _trayApp.Icon.Visible = (!IsVisible && !_trayMouse.Icon.Visible && !_trayRate.Icon.Visible && !_trayPad.Icon.Visible)
                                    || DateTime.UtcNow < _alertIconUntil;
    }

    bool _alertArmed = true;
    DateTime _alertIconUntil;

    /// <summary>
    /// Shows a Windows notification once when the mouse drops to the chosen level, and gets ready to warn again after it's
    /// been charged back above that level.
    /// </summary>
    void CheckBatteryAlert(Mouse m)
    {
        if (_opt.Alert <= 0 || _trayMouse == null || _trayRate == null || _trayApp == null) return;
        if (m.Charging || m.Battery > _opt.Alert + 2) { _alertArmed = true; return; }   // a little margin so a wobbling reading can't re-trigger it
        if (!_alertArmed || m.Battery > _opt.Alert) return;
        _alertArmed = false;

        var carrier = _trayMouse.Icon.Visible ? _trayMouse : _trayRate.Icon.Visible ? _trayRate : _trayApp;
        if (!carrier.Icon.Visible) { _alertIconUntil = DateTime.UtcNow.AddSeconds(30); carrier.Icon.Visible = true; }
        string name = m.Model != "" ? m.Model : "Mouse";
        carrier.Icon.ShowBalloonTip(8000, $"Mouse battery at {m.Battery}%", $"{name} has dropped to your {_opt.Alert}% alert level. Time to charge it soon.", WF.ToolTipIcon.Warning);
    }

    void SetTray(TrayItem t, string key, Func<IntPtr> draw, string tip, string info)
    {
        if (t.Key != key)
        {
            var handle = draw();
            var old = t.Icon.Icon;
            t.Icon.Icon = SD.Icon.FromHandle(handle);
            old?.Dispose();
            FreeTrayImage(t);
            t.Handle = handle;
            t.Key = key;
        }
        t.Icon.Text = tip.Length > 127 ? tip[..127] : tip;
        if (t.Info != null) t.Info.Text = info;
        t.Icon.Visible = true;
    }

    static void FreeTrayImage(TrayItem t)
    {
        if (t.Handle != IntPtr.Zero) DestroyIcon(t.Handle);
        t.Handle = IntPtr.Zero;
    }

    static int TraySize => Math.Max(16, WF.SystemInformation.SmallIconSize.Width);

    /// <summary>
    /// Draws text as large as fits the tray icon in light gray. With a level (0-100) it also draws a thin bar along the
    /// bottom, filled to that level, which is what tells the battery icon apart from the polling rate icon at a glance.
    /// </summary>
    static IntPtr TextIcon(string text, int? level)
    {
        int s = TraySize;
        using var bmp = new SD.Bitmap(s, s);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(SD.Color.Transparent);

        float barH = level.HasValue ? Math.Max(2f, s * 0.14f) : 0, gap = level.HasValue ? Math.Max(1f, s * 0.08f) : 0;
        float textH = s - barH - gap;

        SD.FontFamily family;
        try { family = new SD.FontFamily("Cascadia Mono"); } catch { family = SD.FontFamily.GenericMonospace; }
        using var path = new SD.Drawing2D.GraphicsPath();
        path.AddString(text, family, (int)SD.FontStyle.Bold, 100, SD.Point.Empty, SD.StringFormat.GenericTypographic);
        var b = path.GetBounds();
        float k = Math.Min((s - 1) / b.Width, (textH - 1) / b.Height);
        using (var fit = new SD.Drawing2D.Matrix())
        {
            fit.Translate(s / 2f, textH / 2f);
            fit.Scale(k, k);
            fit.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
            path.Transform(fit);
        }
        using var edge = new SD.Pen(TrayEdge, Math.Max(1f, s / 16f)) { LineJoin = SD.Drawing2D.LineJoin.Round };
        g.DrawPath(edge, path);
        using var ink = new SD.SolidBrush(TrayInk);
        g.FillPath(ink, path);

        if (level is int percent)
        {
            float y = s - barH, filled = s * Math.Clamp(percent, 0, 100) / 100f;
            using var dim = new SD.SolidBrush(TrayDim);
            g.FillRectangle(dim, 0, y, s, barH);
            g.FillRectangle(ink, 0, y, filled, barH);
        }
        return bmp.GetHicon();
    }

    /// <summary>A plain gray battery outline filled to one of Windows' four controller levels (0 empty to 3 full).</summary>
    static IntPtr TrayBatteryIcon(int level)
    {
        int s = TraySize;
        using var bmp = new SD.Bitmap(s, s);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.Transparent);
        float line = Math.Max(1f, s / 14f);
        float w = s * 0.46f, h = s * 0.80f, x = (s - w) / 2, y = s - h - line, capH = s * 0.10f;
        using var pen = new SD.Pen(TrayInk, line);
        g.DrawRectangle(pen, x, y, w, h);
        using var ink = new SD.SolidBrush(TrayInk);
        g.FillRectangle(ink, x + w * 0.3f, y - capH, w * 0.4f, capH);
        float pad = line + Math.Max(1f, s / 20f), inner = h - 2 * pad, fillH = inner * (Math.Clamp(level, 0, 3) + 1) / 4f;
        g.FillRectangle(ink, x + pad, y + h - pad - fillH, w - 2 * pad, fillH);
        return bmp.GetHicon();
    }
}
