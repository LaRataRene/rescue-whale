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

    TrayItem? _trayMouse, _trayPad;

    static readonly SD.Color TrayGreen = SD.Color.FromArgb(0x30, 0xD1, 0x58), TrayAmber = SD.Color.FromArgb(0xFF, 0xB6, 0x27),
                             TrayRed = SD.Color.FromArgb(0xFF, 0x55, 0x55), TrayLight = SD.Color.FromArgb(0xE4, 0xE4, 0xE4);

    void InitTray()
    {
        if (_shot != null) return;   // screenshot runs open and close in a moment; no point flashing tray icons
        _trayMouse = MakeTray();
        _trayPad = MakeTray();
        Closed += (_, _) => { foreach (var t in new[] { _trayMouse, _trayPad }) if (t != null) { t.Icon.Visible = false; t.Icon.Dispose(); FreeTrayImage(t); } };
    }

    TrayItem MakeTray()
    {
        var item = new TrayItem { Icon = new WF.NotifyIcon { Visible = false } };
        var menu = new WF.ContextMenuStrip();
        item.Info = new WF.ToolStripMenuItem { Enabled = false };
        menu.Items.Add(item.Info);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Open Rescue Whale", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
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
        if (_trayMouse == null || _trayPad == null) return;

        var m = _m;
        if (m is { Online: true, Loaded: true } && m.Battery >= 0)
        {
            var color = m.Charging ? TrayLight : m.Battery >= 50 ? TrayGreen : m.Battery >= 20 ? TrayAmber : TrayRed;
            string rate = m.PollingHz > 0 ? $"{m.PollingHz:N0} Hz" : "unknown";
            string name = m.Model != "" ? m.Model : "Mouse";
            string tip = $"{name}\nBattery {m.Battery}%{(m.Charging ? ", charging" : "")}\nPolling rate {rate}";
            SetTray(_trayMouse, $"m{m.Battery}{m.Charging}", () => TextIcon(m.Battery.ToString(), color), tip,
                    $"{name}:  {m.Battery}%  ·  {rate}");
        }
        else _trayMouse.Icon.Visible = false;

        if (_padSlot >= 0)
        {
            bool wired = _padBatteryType == 1, known = _padBatteryType > 0;
            string level = !known ? "unknown" : wired ? "on cable" : _padBatteryLevel switch { 0 => "empty", 1 => "low", 2 => "medium", _ => "full" };
            string tip = $"Xbox controller\nPower: {level}";
            if (wired || !known) SetTray(_trayPad, "p" + level, () => TextIcon(wired ? "USB" : "?", TrayLight), tip, "Xbox controller:  " + level);
            else
            {
                var color = _padBatteryLevel >= 2 ? TrayGreen : _padBatteryLevel == 1 ? TrayAmber : TrayRed;
                SetTray(_trayPad, "p" + level, () => TrayBatteryIcon(_padBatteryLevel, color), tip, "Xbox controller:  " + level + " battery");
            }
        }
        else _trayPad.Icon.Visible = false;
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

    /// <summary>Draws text as large as fits the tray icon, with a dark outline so it reads on light and dark taskbars.</summary>
    static IntPtr TextIcon(string text, SD.Color color)
    {
        int s = TraySize;
        using var bmp = new SD.Bitmap(s, s);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(SD.Color.Transparent);

        SD.FontFamily family;
        try { family = new SD.FontFamily("Cascadia Mono"); } catch { family = SD.FontFamily.GenericMonospace; }
        using var path = new SD.Drawing2D.GraphicsPath();
        path.AddString(text, family, (int)SD.FontStyle.Bold, 100, SD.Point.Empty, SD.StringFormat.GenericTypographic);
        var b = path.GetBounds();
        float room = s - Math.Max(2f, s / 8f);
        float k = Math.Min(room / b.Width, room / b.Height);
        using (var fit = new SD.Drawing2D.Matrix())
        {
            fit.Translate(s / 2f, s / 2f);
            fit.Scale(k, k);
            fit.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
            path.Transform(fit);
        }
        using var outline = new SD.Pen(SD.Color.FromArgb(220, 0, 0, 0), Math.Max(2f, s / 9f)) { LineJoin = SD.Drawing2D.LineJoin.Round };
        g.DrawPath(outline, path);
        using var fill = new SD.SolidBrush(color);
        g.FillPath(fill, path);
        return bmp.GetHicon();
    }

    /// <summary>A small upright battery filled to one of Windows' four controller levels (0 empty to 3 full).</summary>
    static IntPtr TrayBatteryIcon(int level, SD.Color color)
    {
        int s = TraySize;
        using var bmp = new SD.Bitmap(s, s);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.Transparent);
        float w = s * 0.5f, h = s * 0.82f, x = (s - w) / 2, y = s - h - 1, line = Math.Max(1.5f, s / 12f);
        using var dark = new SD.SolidBrush(SD.Color.FromArgb(220, 0, 0, 0));
        g.FillRectangle(dark, x - 1, y - 1, w + 2, h + 2);
        g.FillRectangle(dark, x + w * 0.3f - 1, y - s * 0.12f - 1, w * 0.4f + 2, s * 0.12f + 2);
        using var pen = new SD.Pen(TrayLight, line);
        g.DrawRectangle(pen, x, y, w, h);
        using var cap = new SD.SolidBrush(TrayLight);
        g.FillRectangle(cap, x + w * 0.3f, y - s * 0.12f, w * 0.4f, s * 0.12f);
        float inner = h - 2 * line - 2, fillH = inner * (level + 1) / 4f;
        using var fill = new SD.SolidBrush(color);
        g.FillRectangle(fill, x + line + 1, y + h - line - 1 - fillH, w - 2 * line - 2, fillH);
        return bmp.GetHicon();
    }
}
