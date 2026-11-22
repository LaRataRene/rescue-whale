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

    // Tray icons are drawn in the app theme's text color, adjusted just enough to stay readable on the taskbar:
    // darkened on a light taskbar, lightened on a dark one, keeping the hue.
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
    static SD.Color TrayInk = SD.Color.FromArgb(0xE8, 0xE8, 0xE8);   // set from the theme before icons are drawn

    /// <summary>The theme's text color, darkened or lightened until it stands out from the taskbar.</summary>
    static SD.Color ThemedInk(string hex, bool lightTaskbar)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        double r = c.R, g = c.G, b = c.B;
        static double Lum(double r, double g, double b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        for (int i = 0; i < 20; i++)
        {
            double lum = Lum(r, g, b);
            if (lightTaskbar ? lum <= 0.30 : lum >= 0.50) break;
            if (lightTaskbar) { r *= 0.85; g *= 0.85; b *= 0.85; }                                         // darker, same hue
            else { r += (255 - r) * 0.15; g += (255 - g) * 0.15; b += (255 - b) * 0.15; }                   // lighter, same hue
        }
        return SD.Color.FromArgb((int)r, (int)g, (int)b);
    }

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
        bool lightBar = LightTaskbar;
        TrayInk = ThemedInk(_theme.Text, lightBar);
        string look = _theme.Name + lightBar;   // part of each icon's key, so a theme change redraws them

        var m = _m;
        bool mouseReady = m is { Online: true, Loaded: true } && m.Battery >= 0;
        if (mouseReady)
        {
            string rate = m!.PollingHz > 0 ? $"{m.PollingHz:N0} Hz" : "unknown";
            string name = m.Model != "" ? m.Model : "Mouse";
            string tip = $"{name}\nBattery {m.Battery}%{(m.Charging ? ", charging" : "")}\nPolling rate {rate}";
            string info = $"{name}:  {m.Battery}%  ·  {rate}";
            int battery = m.Battery;

            if (_opt.BatteryIcon) SetTray(_trayMouse, $"b{battery}{look}", () => TextIcon(battery.ToString()), tip, info);
            else _trayMouse.Icon.Visible = false;

            // 8000 -> 8K, 1000 -> 1K, 500 -> 500
            string shortRate = m.PollingHz >= 1000 ? m.PollingHz / 1000 + "K" : m.PollingHz > 0 ? m.PollingHz.ToString() : "?";
            if (_opt.PollingIcon) SetTray(_trayRate, "r" + shortRate + look, () => TextIcon(shortRate), tip, info);
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
            if (wired || !known) SetTray(_trayPad, "p" + level + look, () => TextIcon(wired ? "USB" : "?"), tip, "Xbox controller:  " + level);
            else SetTray(_trayPad, "p" + level + look, () => TrayBatteryIcon(padLevel), tip, "Xbox controller:  " + level + " battery");
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
    /// Draws text as large as fits the tray icon, in Bahnschrift SemiBold Condensed (part of Windows; narrow, made for small sizes) with the glyphs snapped to whole pixels so it stays
    /// crisp on a small taskbar. An optional suffix (the battery's %) is drawn smaller, raised beside the main text.
    /// </summary>
    static IntPtr TextIcon(string text, string suffix = "")
    {
        int s = TraySize;
        using var bmp = new SD.Bitmap(s, s);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.Transparent);
        g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;   // hinted: sharp edges at tiny sizes

        // a condensed face fits more height into the square; Segoe UI bold if it is missing
        SD.FontFamily family;
        SD.FontStyle style = SD.FontStyle.Regular;
        try { family = new SD.FontFamily("Bahnschrift SemiBold Condensed"); }
        catch { family = new SD.FontFamily("Segoe UI"); style = SD.FontStyle.Bold; }
        var format = SD.StringFormat.GenericTypographic;

        // the exact ink bounds of a string at a given size, so it can be placed without the font's built-in spacing
        static SD.RectangleF Ink(string str, SD.FontFamily f, SD.FontStyle st, float em, SD.StringFormat fmt)
        {
            if (str.Length == 0) return SD.RectangleF.Empty;
            using var p = new SD.Drawing2D.GraphicsPath();
            p.AddString(str, f, (int)st, em, SD.PointF.Empty, fmt);
            return p.GetBounds();
        }

        // largest size at which the text (plus the smaller suffix) still fits the icon
        float em = s * 1.3f, sufEm = 0, gap = 0;
        SD.RectangleF main = default, suf = default;
        for (; em > 4; em -= 0.25f)
        {
            sufEm = em * 0.55f;
            gap = suffix.Length > 0 ? Math.Max(0.5f, em * 0.04f) : 0;
            main = Ink(text, family, style, em, format);
            suf = Ink(suffix, family, style, sufEm, format);
            if (main.Width + gap + suf.Width <= s && main.Height <= s) break;
        }

        float total = main.Width + gap + suf.Width;
        float x = (float)Math.Round((s - total) / 2f), top = (float)Math.Round((s - main.Height) / 2f);
        using var ink = new SD.SolidBrush(TrayInk);
        using (var font = new SD.Font(family, em, style, SD.GraphicsUnit.Pixel))
            g.DrawString(text, font, ink, x - main.X, top - main.Y, format);
        if (suffix.Length > 0)
        {
            using var sfont = new SD.Font(family, sufEm, style, SD.GraphicsUnit.Pixel);
            // raised: its top lines up with the top of the main text
            g.DrawString(suffix, sfont, ink, x + main.Width + gap - suf.X, top - suf.Y, format);
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
