using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RescueWhale;

// App options: what the tray icons show, a battery alert, what the close button does, and starting with Windows.
// They're saved to options.txt in %AppData%\RescueWhale. Starting with Windows adds an entry to the current user's
// startup list in the registry, and removes it again when switched off.
public partial class MainWindow
{
    sealed class AppOptions
    {
        public string MouseIcon = "battery";   // battery, polling, off
        public bool PadIcon = true;
        public int Alert;                      // battery level in percent for a notification, 0 for off
        public bool HideOnClose = true;
    }

    readonly AppOptions _opt = new();
    bool _startHidden;
    DateTime _optionsClosedAt;

    static string OptionsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RescueWhale", "options.txt");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "RescueWhale";

    void InitOptions(string[] args)
    {
        LoadOptions();
        // --tray starts hidden in the tray; it's what the Windows startup entry uses
        _startHidden = args.Contains("--tray") && _shot == null;
        if (_startHidden)
        {
            Opacity = 0;
            _trayHintShown = true;
            Loaded += (_, _) => { Hide(); Opacity = 1; };
        }

        _loading = true;
        OptionRow(OptMouseIcon, "mouseicon", [("BATTERY", "battery"), ("POLLING", "polling"), ("OFF", "off")], _opt.MouseIcon, v => _opt.MouseIcon = v);
        OptionRow(OptPadIcon, "padicon", [("ON", "on"), ("OFF", "off")], _opt.PadIcon ? "on" : "off", v => _opt.PadIcon = v == "on");
        OptionRow(OptAlert, "alert", [("OFF", "0"), ("15%", "15"), ("20%", "20"), ("30%", "30"), ("50%", "50")], _opt.Alert.ToString(), v => _opt.Alert = int.Parse(v));
        OptionRow(OptClose, "close", [("HIDE TO TRAY", "hide"), ("QUIT", "quit")], _opt.HideOnClose ? "hide" : "quit", v => _opt.HideOnClose = v == "hide");
        OptStartup.IsChecked = StartsWithWindows();
        _loading = false;
    }

    void OptionRow(Panel host, string group, (string Label, string Value)[] choices, string current, Action<string> set)
    {
        foreach (var (label, value) in choices)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Seg"), Content = label, GroupName = "opt-" + group, IsChecked = value == current };
            rb.Checked += (_, _) =>
            {
                if (_loading) return;
                set(value);
                SaveOptions();
                if (group == "alert") _alertArmed = true;
                UpdateTray();
            };
            host.Children.Add(rb);
        }
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        // a click on the button while the panel is open first closes it (it counts as a click outside); don't reopen it straight away
        if ((DateTime.UtcNow - _optionsClosedAt).TotalMilliseconds < 300) return;
        OptStartup.IsChecked = StartsWithWindows();
        OptionsPopup.IsOpen = true;
    }

    void OptionsPopup_Closed(object? sender, EventArgs e) => _optionsClosedAt = DateTime.UtcNow;

    void OptStartup_Click(object sender, RoutedEventArgs e)
    {
        bool on = OptStartup.IsChecked == true;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(RunName, false);
            Status(on ? "rescue whale will start in the tray when you sign in" : "rescue whale won't start with windows");
        }
        catch
        {
            Status("couldn't change the windows startup setting", true);
        }
        OptStartup.IsChecked = StartsWithWindows();
    }

    static bool StartsWithWindows()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(RunName) is string; }
        catch { return false; }
    }

    void LoadOptions()
    {
        try
        {
            foreach (var line in File.ReadAllLines(OptionsPath))
            {
                var p = line.Split('=', 2);
                if (p.Length != 2) continue;
                switch (p[0])
                {
                    case "mouseicon" when p[1] is "battery" or "polling" or "off": _opt.MouseIcon = p[1]; break;
                    case "padicon": _opt.PadIcon = p[1] != "0"; break;
                    case "alert" when int.TryParse(p[1], out int a) && a is >= 0 and <= 100: _opt.Alert = a; break;
                    case "hideonclose": _opt.HideOnClose = p[1] != "0"; break;
                }
            }
        }
        catch { }
    }

    void SaveOptions()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OptionsPath)!);
            File.WriteAllLines(OptionsPath,
            [
                "mouseicon=" + _opt.MouseIcon,
                "padicon=" + (_opt.PadIcon ? 1 : 0),
                "alert=" + _opt.Alert,
                "hideonclose=" + (_opt.HideOnClose ? 1 : 0),
            ]);
        }
        catch { }
    }
}
