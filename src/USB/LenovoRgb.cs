using System.Runtime.InteropServices;

namespace GHelper.Linux.USB;

/// <summary>
/// Lenovo RGB keyboard kind detected on the ITE HID controller (VID 0x048D).
/// </summary>
public enum LenovoRgbKind
{
    None,
    /// <summary>Legion/LOQ 4-zone backlight: 33-byte feature report 0xCC.</summary>
    FourZone,
    /// <summary>Legion Spectrum per-key backlight: 960-byte feature report 0x07.</summary>
    Spectrum,
}

/// <summary>4-zone hardware effects (byte 2 of the 0xCC report).</summary>
public enum LenovoRgbMode
{
    Static = 1,
    Breath = 3,
    Wave = 4,
    Smooth = 6,
}

/// <summary>
/// Lenovo Legion/LOQ RGB keyboard driver over raw hidraw (no kernel driver
/// exists; protocol from LenovoLegionToolkit / L5P-Keyboard-RGB).
///
/// 4-zone (ITE VID 0x048D, PIDs 0xC9xx with 33-byte feature reports):
///   [0xCC, 0x16, effect, speed 1-4, brightness 0-2, zone1 RGB, zone2 RGB,
///    zone3 RGB, zone4 RGB, 0, waveLTR, waveRTL, 13 zero bytes]
///   effect: 1 static, 3 breath, 4 wave, 6 smooth. All-zero packet = off.
///
/// Spectrum per-key (PIDs 0xC1xx/0xC6xx/0xC9xx with 960-byte feature reports):
///   report ID 0x07, header [07, op, size, 03]. Implemented subset:
///   brightness get/set (ops 0xCD/0xCE, 0-9), profile set (0xC8, 1-6),
///   whole-keyboard static fill (EffectChange 0xCB, effect type 0x0B Always).
///   Aurora streaming / per-key editing are not implemented.
/// </summary>
public static class LenovoRgb
{
    private const ushort IteVendorId = 0x048D;

    // Known 4-zone PIDs (2020-2024 Legion/LOQ generations). Other 0xC1/C6/C9xx
    // PIDs are probed as Spectrum.
    private static readonly HashSet<ushort> FourZonePids = new()
    {
        0xC955,                  // Legion 2020
        0xC963, 0xC965,          // 2021
        0xC973, 0xC975,          // 2022
        0xC983, 0xC984, 0xC985,  // 2023 (LOQ / standard / Pro)
        0xC993, 0xC994, 0xC995,  // 2024 (LOQ / standard / Pro)
    };

    // P/Invoke (raw hidraw feature-report ioctls)

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref HidrawDevinfo data);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, byte[] data);

    private const int O_RDWR = 0x02;
    private const int O_NONBLOCK = 0x800;
    private const uint HIDIOCGRAWINFO = 0x80084803;

    [StructLayout(LayoutKind.Sequential)]
    private struct HidrawDevinfo
    {
        public uint bustype;
        public short vendor;
        public short product;
    }

    // HIDIOCSFEATURE(len) = _IOC(READ|WRITE, 'H', 0x06, len)
    private static uint Sfeature(int len) => 0xC0000000u | ((uint)len << 16) | (0x48u << 8) | 0x06;
    // HIDIOCGFEATURE(len) = _IOC(READ|WRITE, 'H', 0x07, len)
    private static uint Gfeature(int len) => 0xC0000000u | ((uint)len << 16) | (0x48u << 8) | 0x07;

    // Detection state

    private static bool _scanned;
    private static LenovoRgbKind _kind = LenovoRgbKind.None;
    private static readonly List<string> _candidatePaths = new();
    private static string? _workingPath;

    private static bool _applyTested;
    private static bool _applyOk;

    // User state (persisted by the UI through AppConfig)

    public static LenovoRgbMode Mode = LenovoRgbMode.Static;
    public static int Speed = 2;          // 1-4 (4-zone) / 1-3 (Spectrum)
    public static int Brightness = 2;     // 4-zone: 0-2; Spectrum: 0-9
    public static byte ColorR = 255, ColorG = 255, ColorB = 255;
    public static byte Color2R, Color2G, Color2B;

    public static LenovoRgbKind Kind
    {
        get
        {
            Scan();
            return _kind;
        }
    }

    public static bool IsAvailable()
    {
        if (!Helpers.AppConfig.IsLenovoDevice())
            return false;
        Scan();
        if (_kind == LenovoRgbKind.None)
            return false;
        return !_applyTested || _applyOk;
    }

    /// <summary>Modes for the UI combo (int id -> label).</summary>
    public static Dictionary<int, string> GetModes()
    {
        if (Kind == LenovoRgbKind.Spectrum)
        {
            return new Dictionary<int, string>
            {
                { (int)LenovoRgbMode.Static, "Static" },
                { (int)LenovoRgbMode.Breath, "Color Pulse" },
                { (int)LenovoRgbMode.Wave, "Rainbow Wave" },
                { (int)LenovoRgbMode.Smooth, "Smooth" },
            };
        }
        return new Dictionary<int, string>
        {
            { (int)LenovoRgbMode.Static, "Static" },
            { (int)LenovoRgbMode.Breath, "Breath" },
            { (int)LenovoRgbMode.Wave, "Wave" },
            { (int)LenovoRgbMode.Smooth, "Smooth" },
        };
    }

    public static Dictionary<int, string> GetSpeeds() => new()
    {
        { 1, "Slow" },
        { 2, "Normal" },
        { 3, "Fast" },
        { 4, "Fastest" },
    };

    public static bool UsesColor() => Mode is LenovoRgbMode.Static or LenovoRgbMode.Breath;

    public static bool HasSecondColor() => Kind == LenovoRgbKind.FourZone && UsesColor();

    public static bool UsesSpeed() => Mode != LenovoRgbMode.Static;

    public static int GetColorArgb() => unchecked((int)0xFF000000 | (ColorR << 16) | (ColorG << 8) | ColorB);
    public static int GetColor2Argb() => unchecked((int)0xFF000000 | (Color2R << 16) | (Color2G << 8) | Color2B);

    public static void SetColor(int argb)
    {
        ColorR = (byte)(argb >> 16);
        ColorG = (byte)(argb >> 8);
        ColorB = (byte)argb;
    }

    public static void SetColor2(int argb)
    {
        Color2R = (byte)(argb >> 16);
        Color2G = (byte)(argb >> 8);
        Color2B = (byte)argb;
    }

    //  detection 

    private static void Scan()
    {
        if (_scanned)
            return;
        _scanned = true;

        try
        {
            foreach (var dev in Directory.GetFiles("/dev", "hidraw*"))
            {
                int fd = HidrawHelper.OpenDevice(dev, O_RDWR | O_NONBLOCK);
                if (fd < 0)
                    continue;
                try
                {
                    var info = new HidrawDevinfo();
                    if (ioctl(fd, HIDIOCGRAWINFO, ref info) != 0)
                        continue;
                    if ((ushort)info.vendor != IteVendorId)
                        continue;

                    ushort pid = (ushort)info.product;
                    ushort family = (ushort)(pid & 0xFF00);
                    if (FourZonePids.Contains(pid))
                    {
                        _kind = LenovoRgbKind.FourZone;
                        _candidatePaths.Add(dev);
                        Helpers.Logger.WriteLine($"LenovoRgb: 4-zone ITE keyboard at {dev} (PID 0x{pid:X4})");
                    }
                    else if (family is 0xC100 or 0xC600 or 0xC900)
                    {
                        // Unknown 0xCxxx PID: probe as Spectrum (960-byte GetFeature
                        // on report 7 succeeds only on Spectrum endpoints).
                        var buf = new byte[960];
                        buf[0] = 0x07;
                        if (ioctl(fd, Gfeature(buf.Length), buf) >= 0)
                        {
                            _kind = LenovoRgbKind.Spectrum;
                            _candidatePaths.Add(dev);
                            Helpers.Logger.WriteLine($"LenovoRgb: Spectrum keyboard at {dev} (PID 0x{pid:X4})");
                        }
                    }
                }
                finally
                {
                    close(fd);
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"LenovoRgb: scan failed: {ex.Message}");
        }
    }

    //  transport 

    private static bool SetFeature(byte[] report)
    {
        // Try the cached working node first, then every candidate.
        var paths = new List<string>();
        if (_workingPath != null)
            paths.Add(_workingPath);
        foreach (var p in _candidatePaths)
            if (!paths.Contains(p))
                paths.Add(p);

        foreach (var path in paths)
        {
            int fd = HidrawHelper.OpenDevice(path, O_RDWR | O_NONBLOCK);
            if (fd < 0)
                continue;
            try
            {
                if (ioctl(fd, Sfeature(report.Length), report) >= 0)
                {
                    _workingPath = path;
                    return true;
                }
            }
            finally
            {
                close(fd);
            }
        }
        return false;
    }

    private static byte[]? GetFeature(byte reportId, int length)
    {
        var paths = _workingPath != null ? new[] { _workingPath } : _candidatePaths.ToArray();
        foreach (var path in paths)
        {
            int fd = HidrawHelper.OpenDevice(path, O_RDWR | O_NONBLOCK);
            if (fd < 0)
                continue;
            try
            {
                var buf = new byte[length];
                buf[0] = reportId;
                if (ioctl(fd, Gfeature(length), buf) >= 0)
                    return buf;
            }
            finally
            {
                close(fd);
            }
        }
        return null;
    }

    //  apply 

    /// <summary>Apply the current Mode/Speed/Brightness/colors to the keyboard.</summary>
    public static bool Apply()
    {
        bool ok = Kind switch
        {
            LenovoRgbKind.FourZone => ApplyFourZone(),
            LenovoRgbKind.Spectrum => ApplySpectrum(),
            _ => false,
        };
        _applyTested = true;
        _applyOk = ok;
        return ok;
    }

    /// <summary>Turn the backlight off (all-zero 4-zone packet / brightness 0).</summary>
    public static bool Off()
    {
        if (Kind == LenovoRgbKind.FourZone)
        {
            var report = new byte[33];
            report[0] = 0xCC;
            report[1] = 0x16;
            return SetFeature(report);
        }
        return SpectrumSetBrightness(0);
    }

    private static bool ApplyFourZone()
    {
        var report = new byte[33];
        report[0] = 0xCC;
        report[1] = 0x16;
        report[2] = (byte)Mode;
        report[3] = (byte)(UsesSpeed() ? Math.Clamp(Speed, 1, 4) : 0);
        report[4] = (byte)Math.Clamp(Brightness, 0, 2);

        // Static/breath honor zone colors; wave/smooth want 0xFFFFFF (LLT).
        byte r1 = ColorR, g1 = ColorG, b1 = ColorB;
        byte r2 = Color2R, g2 = Color2G, b2 = Color2B;
        if (!UsesColor())
        {
            r1 = g1 = b1 = 0xFF;
            r2 = g2 = b2 = 0xFF;
        }
        else if (r2 == 0 && g2 == 0 && b2 == 0)
        {
            // No second color picked: paint all zones with the first.
            r2 = r1;
            g2 = g1;
            b2 = b1;
        }

        // zone1+2 = color1, zone3+4 = color2
        report[5] = r1;
        report[6] = g1;
        report[7] = b1;
        report[8] = r1;
        report[9] = g1;
        report[10] = b1;
        report[11] = r2;
        report[12] = g2;
        report[13] = b2;
        report[14] = r2;
        report[15] = g2;
        report[16] = b2;

        if (Mode == LenovoRgbMode.Wave)
            report[18] = 1; // wave LTR

        bool ok = SetFeature(report);
        Helpers.Logger.WriteLine($"LenovoRgb: 4-zone apply mode={Mode} speed={Speed} brightness={Brightness} ({(ok ? "OK" : "FAILED")})");
        return ok;
    }

    //  Spectrum subset 

    private static bool ApplySpectrum()
    {
        // Map the 4-zone mode ids onto Spectrum effect types.
        byte effectType = Mode switch
        {
            LenovoRgbMode.Static => 0x0B, // Always
            LenovoRgbMode.Breath => 0x04, // ColorPulse
            LenovoRgbMode.Wave => 0x02,   // RainbowWave
            LenovoRgbMode.Smooth => 0x06, // Smooth
            _ => 0x0B,
        };
        byte colorMode = (byte)(UsesColor() ? 2 : 1); // 2 = color list, 1 = random
        byte speed = (byte)Math.Clamp(Speed, 1, 3);

        // EffectChange (0xCB): header, profile 1, one effect covering the
        // whole keyboard via the "all keys" group keycode 0x65.
        var payload = new List<byte>
        {
            0x07, 0xCB, 0x00, 0x03,
            0x01,             // profile
            0x01, 0x01,       // unknown / count
            0x01,             // effect number
            0x06,             // param block tag
            0x01, effectType,
            0x02, speed,
            0x03, 0x00,       // clockwise
            0x04, 0x01,       // direction LTR
            0x05, colorMode,
            0x06, 0x00,
        };
        if (colorMode == 2)
        {
            payload.Add(0x01);            // one color
            payload.Add(ColorR);
            payload.Add(ColorG);
            payload.Add(ColorB);
        }
        else
        {
            payload.Add(0x00);            // no color list
        }
        // Key list: the 0x65 group code = every key (keyboard-only devices).
        payload.Add(0x01);
        payload.Add(0x65);
        payload.Add(0x00);

        var report = new byte[960];
        payload.CopyTo(report);
        report[2] = (byte)(payload.Count % 255); // size byte

        bool ok = SetFeature(report);
        if (ok)
            SpectrumSetBrightness(Math.Clamp(Brightness, 0, 9));
        Helpers.Logger.WriteLine($"LenovoRgb: Spectrum apply effect=0x{effectType:X2} ({(ok ? "OK" : "FAILED")})");
        return ok;
    }

    /// <summary>Spectrum brightness 0-9 (op 0xCE).</summary>
    public static bool SpectrumSetBrightness(int level)
    {
        var report = new byte[960];
        report[0] = 0x07;
        report[1] = 0xCE;
        report[2] = 0xC0;
        report[3] = 0x03;
        report[4] = (byte)Math.Clamp(level, 0, 9);
        return SetFeature(report);
    }

    /// <summary>Spectrum brightness 0-9 (op 0xCD), -1 on failure.</summary>
    public static int SpectrumGetBrightness()
    {
        var request = new byte[960];
        request[0] = 0x07;
        request[1] = 0xCD;
        request[2] = 0xC0;
        request[3] = 0x03;
        if (!SetFeature(request))
            return -1;
        var resp = GetFeature(0x07, 960);
        return resp != null ? resp[4] : -1;
    }

    /// <summary>Select a Spectrum hardware profile (1-6, op 0xC8).</summary>
    public static bool SpectrumSetProfile(int profile)
    {
        var report = new byte[960];
        report[0] = 0x07;
        report[1] = 0xC8;
        report[2] = 0xC0;
        report[3] = 0x03;
        report[4] = (byte)Math.Clamp(profile, 1, 6);
        return SetFeature(report);
    }

    /// <summary>Load persisted state and apply on startup.</summary>
    public static void Init()
    {
        if (!IsAvailable())
            return;

        Mode = (LenovoRgbMode)Helpers.AppConfig.Get("lenovo_rgb_mode", (int)LenovoRgbMode.Static);
        Speed = Helpers.AppConfig.Get("lenovo_rgb_speed", 2);
        Brightness = Helpers.AppConfig.Get("lenovo_rgb_brightness",
            Kind == LenovoRgbKind.Spectrum ? 9 : 2);
        SetColor(Helpers.AppConfig.Get("lenovo_rgb_color", unchecked((int)0xFFFFFFFF)));
        SetColor2(Helpers.AppConfig.Get("lenovo_rgb_color2", 0));

        Apply();
    }
}
