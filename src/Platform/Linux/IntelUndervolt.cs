namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Intel CPU undervolting via the OC voltage-offset mailbox (MSR 0x150),
/// applied to the core (plane 0) and cache (plane 2) planes through the root
/// gpu-helper "msr-uv" subcommand.
///
/// Analogous to RyzenSmu for AMD: the public surface is IsAvailable + Apply /
/// Reset, but the offset is expressed in millivolts (negative = undervolt)
/// rather than Curve Optimizer steps. Non-persistent: the MSR resets on reboot,
/// so g-helper re-applies the saved per-mode offset on each mode change (the
/// shared "auto_uv" flag).
///
/// Availability is CPU-vendor based (the helper loads the msr module on demand).
/// Whether the firmware actually honours the write - some ASUS BIOSes lock the
/// mailbox - is only known after an apply: the helper reports the decoded
/// readback and <see cref="Apply"/> verifies the offset took effect.
/// </summary>
public sealed class IntelUndervolt
{
    // Bounded undervolt-only range. -150 mV is a conservative ceiling for core
    // offsets on mobile Raptor Lake; the helper enforces the same clamp.
    public const int MinOffsetMv = -150;
    public const int MaxOffsetMv = 0;

    public bool IsAvailable { get; }
    public string UnavailableReason { get; } = "";
    public int LastReadbackMv { get; private set; }

    public IntelUndervolt()
    {
        if (!IsGenuineIntel())
        {
            UnavailableReason = "not an Intel CPU";
            return;
        }
        IsAvailable = true;
    }

    internal static bool IsGenuineIntel()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("vendor_id", StringComparison.Ordinal))
                    return line.Contains("GenuineIntel", StringComparison.Ordinal);
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Apply a core+cache voltage offset in mV (clamped to [-150, 0]). Returns
    /// true only when the helper readback confirms the offset took effect; a
    /// firmware-locked mailbox reads back 0 and is reported as a failure.
    /// </summary>
    public bool Apply(int mv)
    {
        if (!IsAvailable)
            return false;

        mv = Math.Clamp(mv, MinOffsetMv, MaxOffsetMv);
        var (stdout, stderr, exit) = SysfsHelper.RunSudoOrPkexecEx(
            SysfsHelper.GpuHelperPath, new[] { "msr-uv", mv.ToString() },
            sudoTimeoutMs: 5000, pkexecTimeoutMs: 15000);

        if (exit != 0 || stdout == null)
        {
            Helpers.Logger.WriteLine($"IntelUndervolt: msr-uv {mv} failed (exit {exit}): {stderr.Trim()}");
            return false;
        }

        int core = ParseReadback(stdout, "core");
        LastReadbackMv = core;

        // Encoding rounds at ~1 mV granularity; allow a small tolerance.
        if (core == int.MinValue || Math.Abs(core - mv) > 3)
        {
            Helpers.Logger.WriteLine(
                $"IntelUndervolt: requested {mv} mV but readback core={core} mV - mailbox may be firmware-locked");
            return false;
        }

        Helpers.Logger.WriteLine($"IntelUndervolt: applied core+cache {mv} mV (readback {core})");
        return true;
    }

    /// <summary>Restore stock voltage (offset 0).</summary>
    public bool Reset() => Apply(0);

    // Parse "core=<mv> cache=<mv>" from the helper output.
    private static int ParseReadback(string output, string key)
    {
        foreach (var tok in output.Split(
                     new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = tok.IndexOf('=');
            if (eq > 0 && tok.AsSpan(0, eq).SequenceEqual(key)
                && int.TryParse(tok.AsSpan(eq + 1), out int v))
                return v;
        }
        return int.MinValue;
    }
}
