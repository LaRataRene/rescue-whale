using System.IO;
using System.Windows;
using System.Windows.Media;

namespace RescueWhale;

/// <summary>
/// A color scheme for the whole window. The interface refers to nine named colors; applying a theme swaps them.
/// The OLED themes use pure black for every background, so those pixels stay switched off on OLED screens.
/// </summary>
public sealed record Theme(string Name, string Bg, string Panel2, string Line, string Text, string Muted, string Accent, string OnAccent, string Warn, string Bad)
{
    public static readonly Theme[] All =
    [
        //   name            background  fields     borders    text       dim text   highlight  on-highlight  titles     errors
        new("PHOSPHOR",     "#030604", "#08130B", "#1C5C34", "#56F58E", "#2F9E5B", "#8DFFB8", "#021006", "#FFB627", "#FF5555"),
        new("OLED GREEN",   "#000000", "#000000", "#17502C", "#56F58E", "#2F9E5B", "#8DFFB8", "#000000", "#FFB627", "#FF5555"),
        new("OLED AMBER",   "#000000", "#000000", "#5A3C00", "#FFB000", "#A87400", "#FFD98A", "#000000", "#6FE3FF", "#FF5555"),
        new("OLED ICE",     "#000000", "#000000", "#164B5F", "#5FD7FF", "#2F8AAB", "#C2F0FF", "#000000", "#FFB627", "#FF5555"),
        new("OLED MONO",    "#000000", "#000000", "#3A3A3A", "#E4E4E4", "#8C8C8C", "#FFFFFF", "#000000", "#FFB627", "#FF5555"),
        new("OLED RED",     "#000000", "#000000", "#5A1414", "#FF5A5A", "#A83A3A", "#FFB0B0", "#000000", "#FFB627", "#FFE066"),
        new("AMBER",        "#0A0600", "#160E00", "#5A3C00", "#FFB000", "#A87400", "#FFD98A", "#0A0600", "#6FE3FF", "#FF5555"),
        new("PAPER",        "#F1EEE3", "#E5E1D3", "#A8A390", "#1F2A20", "#636B5C", "#000000", "#F1EEE3", "#9A4D00", "#B00020"),
    ];

    /// <summary>True for themes with a light background, so the window's title bar can match.</summary>
    public bool IsLight => ((Color)ColorConverter.ConvertFromString(Bg)) is var c && c.R + c.G + c.B > 384;

    public void Apply()
    {
        var res = Application.Current.Resources;
        void Set(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            res[key] = brush;
        }
        Set("Bg", Bg); Set("Panel2", Panel2); Set("Line", Line); Set("Text", Text); Set("Muted", Muted);
        Set("Accent", Accent); Set("OnAccent", OnAccent); Set("Warn", Warn); Set("Bad", Bad);
    }

    // The theme choice is the only thing Rescue Whale keeps on the PC.
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RescueWhale", "theme.txt");

    public static Theme Find(string? name) => All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static Theme LoadSaved()
    {
        try { return Find(File.ReadAllText(SettingsPath)); } catch { return All[0]; }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, Name); } catch { }
    }
}
