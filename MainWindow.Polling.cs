using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RescueWhale;

// Live polling rate: counts the movement reports the mouse actually sends, using Windows' raw input.
// A mouse only sends a report when it has moved, so the rate reaches the polling rate only while it's moving
// steadily. Nothing is sent to the mouse; this only listens.
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input, uint command, out RAWINPUTHEADER data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, char[]? data, ref uint size);

    const int WM_INPUT = 0x00FF;
    const uint RID_HEADER = 0x10000005, RIDI_DEVICENAME = 0x20000007;

    readonly Dictionary<IntPtr, bool> _rawIsOurs = [];   // raw input device handle -> is it a Compx mouse (vendor 3554)
    readonly Stopwatch _rateClock = new();
    readonly DispatcherTimer _rateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Queue<(double Time, int Count)> _rateWindows = new();
    long _rateCount;
    double _ratePeak, _rateLastMove;

    void InitPollingMeter()
    {
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // listen to mice while this window is in front; other programs are unaffected
            var mouse = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = 0, Target = hwnd };
            if (!RegisterRawInputDevices([mouse], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) return;
            HwndSource.FromHwnd(hwnd)?.AddHook(RawInputHook);
            _rateClock.Start();
            _rateTimer.Tick += (_, _) => UpdatePollingMeter();
            _rateTimer.Start();
        };
        ShowPollingMeter(-1);
    }

    IntPtr RawInputHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        uint size = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RID_HEADER, out var header, ref size, size) == uint.MaxValue || header.Type != 0) return IntPtr.Zero;
        if (!_rawIsOurs.TryGetValue(header.Device, out bool ours))
        {
            ours = DeviceName(header.Device).Contains("VID_3554", StringComparison.OrdinalIgnoreCase);
            _rawIsOurs[header.Device] = ours;
        }
        if (ours) _rateCount++;
        return IntPtr.Zero;
    }

    static string DeviceName(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref chars);
        if (chars == 0) return "";
        var name = new char[chars];
        return GetRawInputDeviceInfo(device, RIDI_DEVICENAME, name, ref chars) == uint.MaxValue ? "" : new string(name).TrimEnd('\0');
    }

    /// <summary>Every quarter second: the report rate over the last half second, and the best seen recently.</summary>
    void UpdatePollingMeter()
    {
        double now = _rateClock.Elapsed.TotalSeconds;
        int count = (int)Interlocked.Exchange(ref _rateCount, 0);
        _rateWindows.Enqueue((now, count));
        while (_rateWindows.Count > 2) _rateWindows.Dequeue();
        if (_page != 0) return;

        double span = _rateWindows.Count > 1 ? now - _rateWindows.Peek().Time + 0.25 : 0.25;
        double hz = _rateWindows.Sum(w => w.Count) / span;
        if (count > 0) _rateLastMove = now;
        if (now - _rateLastMove > 2) _ratePeak = 0;          // forget the peak after two seconds without movement
        else _ratePeak = Math.Max(_ratePeak, hz);
        ShowPollingMeter(count > 0 ? hz : -1);
    }

    void ShowPollingMeter(double hz)
    {
        int target = _m is { Loaded: true } m && m.PollingHz > 0 ? m.PollingHz : 1000;
        if (hz < 0)
        {
            RateLive.Text = _ratePeak > 0 ? $"[{new string('.', 16)}]  --   peak {_ratePeak:N0} HZ" : "move the mouse quickly to measure";
            RateLive.Foreground = (Brush)FindResource("Muted");
            return;
        }
        int cells = (int)Math.Round(16 * Math.Min(1, hz / target));
        RateLive.Text = $"[{new string('#', cells)}{new string('.', 16 - cells)}]  {hz,6:N0} HZ   peak {_ratePeak:N0} HZ";
        // close to the setting is good; well under it usually just means the mouse isn't moving fast enough
        RateLive.Foreground = (Brush)FindResource(_ratePeak >= target * 0.9 ? "Accent" : "Text");
    }
}
