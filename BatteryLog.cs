using System.Globalization;
using System.IO;

namespace RescueWhale;

/// <summary>
/// A running record of the mouse's battery level, so the app can draw how it drained and recharged.
/// Readings only exist for times the app was open and the mouse was awake; everything else is a gap.
/// </summary>
public sealed class BatteryLog
{
    public readonly record struct Sample(DateTime Time, int Percent, bool Charging);

    const string TimeFormat = "yyyy-MM-ddTHH:mm:ss";
    static readonly TimeSpan Keep = TimeSpan.FromDays(45);

    readonly List<Sample> _samples = [];
    readonly bool _persist;

    public IReadOnlyList<Sample> Samples => _samples;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RescueWhale", "battery.csv");

    /// <param name="persist">False keeps the history in memory only, as demo mode does.</param>
    public BatteryLog(bool persist)
    {
        _persist = persist;
        if (!persist) return;
        try
        {
            if (!File.Exists(FilePath)) return;
            var cutoff = DateTime.Now - Keep;
            int total = 0;
            foreach (var line in File.ReadLines(FilePath))
            {
                total++;
                var p = line.Split(',');
                if (p.Length == 3
                    && DateTime.TryParseExact(p[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    && int.TryParse(p[1], out int percent) && percent is >= 0 and <= 100
                    && time >= cutoff)
                    _samples.Add(new Sample(time, percent, p[2] == "1"));
            }
            // drop old or unreadable lines from the file so it can't grow without limit
            if (_samples.Count < total) File.WriteAllLines(FilePath, _samples.Select(Format));
        }
        catch { }
    }

    static string Format(Sample s) => $"{s.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)},{s.Percent},{(s.Charging ? 1 : 0)}";

    /// <summary>Records a reading if it says something new, or if enough time has passed to be worth a point.</summary>
    public void Add(int percent, bool charging)
    {
        var now = DateTime.Now;
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            var since = now - last.Time;
            if (last.Charging == charging)
            {
                if (last.Percent == percent && since < TimeSpan.FromMinutes(5)) return;
                // the reading can wobble by a point from one moment to the next; don't log every wobble
                if (Math.Abs(last.Percent - percent) <= 1 && since < TimeSpan.FromMinutes(2)) return;
            }
        }
        var sample = new Sample(now, percent, charging);
        _samples.Add(sample);
        if (!_persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllLines(FilePath, [Format(sample)]);
        }
        catch { }
    }

    /// <summary>Fills the log with a made-up month of use and recharging, for demo mode.</summary>
    public void FillDemo()
    {
        _samples.Clear();
        // Built backwards from now, so the history ends on the 82% the demo mouse reports.
        double level = 82;
        bool charging = false;
        var end = DateTime.Now;
        for (var t = end; t >= end.AddDays(-30); t = t.AddMinutes(-30))
        {
            if (charging)
            {
                _samples.Add(new Sample(t, (int)Math.Round(level), true));
                level -= 7;                                   // going back in time, a charge runs downwards
                if (level <= 18) { level = 18; charging = false; }
            }
            else if (t.Hour is >= 9 and < 23)
            {
                _samples.Add(new Sample(t, (int)Math.Round(level), false));
                level += 0.9;
                if (level >= 100) { level = 100; charging = true; }
            }
            // otherwise it's overnight: the mouse is asleep, so there are no readings, which shows as a gap
        }
        _samples.Reverse();
    }
}
