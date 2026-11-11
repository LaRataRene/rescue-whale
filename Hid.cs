using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RescueWhale;

/// <summary>A transport that exchanges 17-byte command packets with the mouse (or its receiver).</summary>
public interface ILink : IDisposable
{
    string Title { get; }
    /// <summary>The product name the device reports over USB, or "" if it reports none.</summary>
    string ProductName { get; }
    bool Alive { get; }
    /// <summary>Raised when the mouse reports a change made on the device itself (e.g. DPI button pressed).</summary>
    event Action? StatusChanged;
    /// <summary>Sends one command and returns the 10 payload bytes of the reply, or null if nothing came back.</summary>
    Task<byte[]?> Xfer(byte cmd, int addr = 0, byte[]? data = null, int len = -1);
}

/// <summary>Windows HID transport for Compx-based mice (USB vendor 3554), vendor collection FF02.</summary>
public sealed class HidLink : ILink
{
    const int Vid = 0x3554, ReportLen = 17;
    const byte ReportId = 0x08, CmdStatusChanged = 0x0A;

    readonly FileStream _fs;
    readonly SemaphoreSlim _gate = new(1, 1);
    TaskCompletionSource<byte[]>? _pending;
    byte _pendingCmd;

    public string Title { get; }
    public string ProductName { get; }
    public bool Alive { get; private set; } = true;
    public event Action? StatusChanged;

    HidLink(FileStream fs, string title, string productName) { _fs = fs; Title = title; ProductName = productName; _ = ReadLoop(); }

    /// <summary>Opens every matching device: a cabled mouse and its wireless receiver each show up as their own.</summary>
    public static List<HidLink> OpenAll()
    {
        var links = new List<HidLink>();
        foreach (var path in DevicePaths())
        {
            if (!path.Contains($"vid_{Vid:x4}", StringComparison.OrdinalIgnoreCase)) continue;
            using (var probe = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (probe.IsInvalid || !HidD_GetPreparsedData(probe, out var pp)) continue;
                HidP_GetCaps(pp, out var caps);
                HidD_FreePreparsedData(pp);
                if (caps.UsagePage != 0xFF02 || caps.OutputReportByteLength != ReportLen || caps.InputReportByteLength != ReportLen) continue;
            }
            var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var pidAt = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            var pid = pidAt >= 0 ? path.Substring(pidAt + 4, 4).ToUpperInvariant() : "????";
            var name = new byte[256];
            string product = HidD_GetProductString(h, name, name.Length) ? System.Text.Encoding.Unicode.GetString(name).Split('\0')[0].Trim() : "";
            links.Add(new HidLink(new FileStream(h, FileAccess.ReadWrite, 0, true), $"3554:{pid}", product));
        }
        return links;
    }

    async Task ReadLoop()
    {
        var buf = new byte[ReportLen];
        try
        {
            while (true)
            {
                int n = await _fs.ReadAsync(buf);
                if (n < ReportLen || buf[0] != ReportId) continue;
                var p = _pending;
                if (p != null && buf[1] == _pendingCmd) p.TrySetResult((byte[])buf.Clone());
                else if (buf[1] == CmdStatusChanged) StatusChanged?.Invoke();
            }
        }
        catch { Alive = false; _pending?.TrySetCanceled(); }
    }

    public async Task<byte[]?> Xfer(byte cmd, int addr = 0, byte[]? data = null, int len = -1)
    {
        var pkt = new byte[ReportLen];
        pkt[0] = ReportId; pkt[1] = cmd; pkt[3] = (byte)(addr >> 8); pkt[4] = (byte)addr;
        pkt[5] = (byte)(len >= 0 ? len : data?.Length ?? 0);
        data?.CopyTo(pkt, 6);
        int sum = 0; for (int i = 0; i < 16; i++) sum += pkt[i];
        pkt[16] = (byte)(0x55 - sum);

        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 3 && Alive; attempt++)
            {
                var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingCmd = cmd; _pending = tcs;
                try
                {
                    await _fs.WriteAsync(pkt);
                    if (await Task.WhenAny(tcs.Task, Task.Delay(250)) == tcs.Task && tcs.Task.IsCompletedSuccessfully)
                    {
                        var r = tcs.Task.Result;
                        int s = 0; for (int i = 0; i < 16; i++) s += r[i];
                        if ((byte)(0x55 - s) == r[16] && r[3] == pkt[3] && r[4] == pkt[4]) return r[6..16];
                    }
                }
                catch { Alive = false; }
                finally { _pending = null; }
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() { Alive = false; try { _fs.Dispose(); } catch { } }

    // ---- Win32 ----
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr r; }
    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort a, b, c, d, e, f, g, h, i, j;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint acc, int share, IntPtr sec, int disp, int flags, IntPtr t);

    static List<string> DevicePaths()
    {
        var res = new List<string>();
        HidD_GetHidGuid(out var g);
        var set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
        for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
        {
            SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
            var buf = Marshal.AllocHGlobal(req);
            Marshal.WriteInt32(buf, 8);
            if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out _, IntPtr.Zero))
                res.Add(Marshal.PtrToStringUni(buf + 4)!);
            Marshal.FreeHGlobal(buf);
        }
        SetupDiDestroyDeviceInfoList(set);
        return res;
    }
}
