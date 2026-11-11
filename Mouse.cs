namespace RescueWhale;

public readonly record struct KeyFn(byte Type, byte P1, byte P2);

/// <summary>
/// The mouse's settings, held as a mirror of its settings memory. Every value in that memory is stored
/// with a checksum (value + check = 0x55), which is how <see cref="Verified"/> knows the layout matches.
/// </summary>
public sealed class Mouse(ILink link)
{
    // commands
    const byte CmdOnline = 0x03, CmdBattery = 0x04, CmdWrite = 0x07, CmdRead = 0x08, CmdVersion = 0x12, CmdDongleVersion = 0x1D;
    // settings memory addresses
    const int APolling = 0x00, ADpiCount = 0x02, ADpiIndex = 0x04, ALod = 0x0A, ADpi = 0x0C, ADpiColor = 0x2C, AKeys = 0x60,
              ADebounce = 0xA9, AMotionSync = 0xAB, ASleep = 0xAD, AAngle = 0xAF, ARipple = 0xB1, ASensorMode = 0xB9, Size = 0xC0;

    public const int MaxStages = 6, KeyCount = 6, MinDpi = 50, MaxDpi = 42000;
    public static readonly (int Hz, byte Code)[] Rates = [(125, 8), (250, 4), (500, 2), (1000, 1), (2000, 0x10), (4000, 0x20), (8000, 0x40)];

    readonly byte[] _f = new byte[Size];

    public ILink Link => link;
    public bool Online { get; private set; }
    public bool Loaded { get; private set; }
    /// <summary>True when the checksums of what we read line up. Writes are refused otherwise.</summary>
    public bool Verified { get; private set; }
    public int Battery { get; private set; } = -1;
    public bool Charging { get; private set; }
    public string MouseFirmware { get; private set; } = "";
    public string ReceiverFirmware { get; private set; } = "";

    // ---- connection ----
    public async Task<bool> CheckOnline()
    {
        var r = await link.Xfer(CmdOnline);
        Online = r != null ? r[0] == 1 : await ReadBattery();   // a wired mouse may not answer the online query
        if (!Online) Loaded = false;
        return Online;
    }

    public async Task<bool> ReadBattery()
    {
        var r = await link.Xfer(CmdBattery);
        if (r == null) return false;
        Battery = Math.Clamp((int)r[0], 0, 100); Charging = r[1] == 1;
        return true;
    }

    public async Task<bool> Load()
    {
        for (int a = 0; a < Size; a += 10)
        {
            int n = Math.Min(10, Size - a);
            var r = await link.Xfer(CmdRead, a, null, n);
            if (r == null) { Loaded = false; return false; }
            Array.Copy(r, 0, _f, a, n);
        }
        Verified = Pair(APolling) >= 0 && Pair(ADpiCount) is >= 1 and <= 8 && Pair(ADpiIndex) >= 0
                   && Enumerable.Range(0, DpiCount).All(i => Quad(ADpi + 4 * i))
                   && Enumerable.Range(0, KeyCount).All(i => Quad(AKeys + 4 * i));
        if (await link.Xfer(CmdVersion) is { } v) MouseFirmware = $"{v[0]:X}.{v[1]:X2}";
        if (await link.Xfer(CmdDongleVersion) is { } d && (d[0] | d[1]) != 0) ReceiverFirmware /* a cabled mouse answers with zeros */ = $"{d[0]:X}.{d[1]:X2}";
        await ReadBattery();
        return Loaded = true;
    }

    int Pair(int a) => ((_f[a] + _f[a + 1]) & 0xFF) == 0x55 ? _f[a] : -1;
    bool Quad(int a) => ((_f[a] + _f[a + 1] + _f[a + 2] + _f[a + 3]) & 0xFF) == 0x55;

    // ---- reading ----
    public int PollingHz => Rates.FirstOrDefault(r => r.Code == _f[APolling]).Hz;
    public int DpiCount => Math.Clamp(Pair(ADpiCount), 1, MaxStages);
    public int DpiIndex => Math.Clamp(Pair(ADpiIndex), 0, DpiCount - 1);
    public int Lod => Pair(ALod);
    public int Debounce => Pair(ADebounce);
    public int SleepCode => Pair(ASleep);
    public int MotionSync => Pair(AMotionSync);
    public int AngleSnap => Pair(AAngle);
    public int Ripple => Pair(ARipple);
    public int SensorMode => Pair(ASensorMode);
    public int Dpi(int i) => DecodeDpi(_f, ADpi + 4 * i);
    public (byte R, byte G, byte B) DpiColor(int i) => (_f[ADpiColor + 4 * i], _f[ADpiColor + 4 * i + 1], _f[ADpiColor + 4 * i + 2]);
    public KeyFn Key(int i) => new(_f[AKeys + 4 * i], _f[AKeys + 4 * i + 1], _f[AKeys + 4 * i + 2]);

    // ---- writing ----
    async Task<bool> Write(int addr, params byte[] data)
    {
        if (!Verified || !Online) return false;
        if (await link.Xfer(CmdWrite, addr, data) == null) return false;
        data.CopyTo(_f, addr);
        return true;
    }
    Task<bool> WritePair(int addr, int v) => Write(addr, (byte)v, (byte)(0x55 - v));
    static byte[] Quad(byte a, byte b, byte c) => [a, b, c, (byte)(0x55 - a - b - c)];

    public Task<bool> SetPolling(int hz) => WritePair(APolling, Rates.First(r => r.Hz == hz).Code);
    public Task<bool> SetDpiCount(int n) => WritePair(ADpiCount, n);
    public Task<bool> SetDpiIndex(int i) => WritePair(ADpiIndex, i);
    public Task<bool> SetLod(int v) => WritePair(ALod, v);
    public Task<bool> SetDebounce(int ms) => WritePair(ADebounce, ms);
    public Task<bool> SetSleep(int code) => WritePair(ASleep, code);
    public Task<bool> SetMotionSync(bool on) => WritePair(AMotionSync, on ? 1 : 0);
    public Task<bool> SetAngleSnap(bool on) => WritePair(AAngle, on ? 1 : 0);
    public Task<bool> SetRipple(bool on) => WritePair(ARipple, on ? 1 : 0);
    public Task<bool> SetSensorMode(int v) => WritePair(ASensorMode, v);
    public Task<bool> SetDpi(int i, int dpi) => Write(ADpi + 4 * i, EncodeDpi(dpi));
    public Task<bool> SetDpiColor(int i, byte r, byte g, byte b) => Write(ADpiColor + 4 * i, Quad(r, g, b));
    public Task<bool> SetKey(int i, KeyFn k) => Write(AKeys + 4 * i, Quad(k.Type, k.P1, k.P2));

    public async Task<bool> RemoveStage(int i)
    {
        int n = DpiCount;
        if (n <= 1) return false;
        for (int j = i; j < n - 1; j++)
        {
            var (r, g, b) = DpiColor(j + 1);
            if (!await SetDpi(j, Dpi(j + 1)) || !await SetDpiColor(j, r, g, b)) return false;
        }
        int idx = DpiIndex > i || DpiIndex == n - 1 ? Math.Max(0, DpiIndex - 1) : DpiIndex;
        return await SetDpiCount(n - 1) && await SetDpiIndex(idx);
    }

    // ---- DPI encoding: steps of 50 up to 26,000, then steps of 100 (flag bits 0x11) up to 42,000 ----
    public static int SnapDpi(int dpi)
    {
        dpi = Math.Clamp(dpi, MinDpi, MaxDpi);
        int step = dpi > 26000 ? 100 : 50;
        return Math.Clamp((int)Math.Round(dpi / (double)step) * step, MinDpi, MaxDpi);
    }
    public static byte[] EncodeDpi(int dpi)
    {
        dpi = SnapDpi(dpi);
        bool coarse = dpi > 26000;
        int v = dpi / (coarse ? 100 : 50) - 1, hi = (v >> 8) & 3;
        return Quad((byte)v, (byte)v, (byte)((hi << 2) | (hi << 6) | (coarse ? 0x11 : 0)));
    }
    public static int DecodeDpi(byte[] f, int a) => ((f[a] | (((f[a + 2] >> 2) & 3) << 8)) + 1) * ((f[a + 2] & 1) != 0 ? 100 : 50);
}

/// <summary>An in-memory stand-in for the mouse, so the interface can be explored with nothing plugged in.</summary>
public sealed class DemoLink : ILink
{
    readonly byte[] _f = new byte[0x100];
    public string Title => "Demo";
    public bool Alive => true;
    public event Action? StatusChanged { add { } remove { } }

    public DemoLink()
    {
        void P(int a, int v) { _f[a] = (byte)v; _f[a + 1] = (byte)(0x55 - v); }
        void Q(int a, byte x, byte y, byte z) { _f[a] = x; _f[a + 1] = y; _f[a + 2] = z; _f[a + 3] = (byte)(0x55 - x - y - z); }
        P(0x00, 1); P(0x02, 4); P(0x04, 1); P(0x0A, 1); P(0xA9, 4); P(0xAB, 1); P(0xAD, 6); P(0xAF, 0); P(0xB1, 0); P(0xB9, 1);
        int[] dpi = [800, 1600, 3200, 6400, 12000, 26000];
        (byte, byte, byte)[] col = [(255, 59, 48), (48, 209, 88), (10, 132, 255), (255, 214, 10), (100, 210, 255), (191, 90, 242)];
        for (int i = 0; i < 6; i++) { Mouse.EncodeDpi(dpi[i]).CopyTo(_f, 0x0C + 4 * i); Q(0x2C + 4 * i, col[i].Item1, col[i].Item2, col[i].Item3); }
        (byte, byte)[] keys = [(1, 1), (1, 2), (1, 4), (1, 8), (1, 16), (2, 1)];
        for (int i = 0; i < 6; i++) Q(0x60 + 4 * i, keys[i].Item1, keys[i].Item2, 0);
    }

    public Task<byte[]?> Xfer(byte cmd, int addr = 0, byte[]? data = null, int len = -1)
    {
        var r = new byte[10];
        switch (cmd)
        {
            case 0x03: r[0] = 1; break;
            case 0x04: r[0] = 82; break;
            case 0x07: data!.CopyTo(_f, addr); break;
            case 0x08: Array.Copy(_f, addr, r, 0, len); break;
            case 0x12: r[0] = 1; r[1] = 0x14; break;
            case 0x1D: r[0] = 2; r[1] = 2; break;
        }
        return Task.FromResult<byte[]?>(r);
    }
    public void Dispose() { }
}
