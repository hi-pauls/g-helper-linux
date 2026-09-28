namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Intel counterpart to <see cref="RyzenPower.TrySetPpt"/>. Some ASUS Intel
/// models (Flow Z13 GZ301V) accept the PL1/PL2 PPT writes, legacy and
/// firmware-attributes alike, but never apply them on Linux: on Windows Intel
/// DTT moves them into RAPL. So after the firmware write the effective RAPL
/// limit is compared, and when the firmware did not apply it, gpu-helper
/// writes RAPL directly. Machines whose firmware applies the limit are left
/// alone, since RAPL then already matches.
/// </summary>
public static class IntelRapl
{
    private static readonly string[] Zones =
    {
        "/sys/class/powercap/intel-rapl-mmio:0",
        "/sys/class/powercap/intel-rapl:0",
    };

    private static readonly Lazy<bool> _available = new(() =>
        IntelUndervolt.IsGenuineIntel() && Zones.Any(Directory.Exists));

    public static bool Available => _available.Value;

    /// <summary>RAPL constraint for a PPT attribute: pl1 = long_term, pl2 = short_term.</summary>
    internal static (string limit, string constraint)? ConstraintFor(string attribute) => attribute switch
    {
        "ppt_pl1_spl" => ("pl1", "long_term"),
        "ppt_pl2_sppt" => ("pl2", "short_term"),
        _ => null,
    };

    /// <summary>The limit the CPU enforces for a constraint: the lower of the
    /// MMIO and MSR copies. Null when no zone has it.</summary>
    public static int? EffectiveWatts(string constraint)
    {
        int? lowest = null;
        foreach (var zone in Zones)
            for (int i = 0; i < 4; i++)
                if (SysfsHelper.ReadAttributeSilent(Path.Combine(zone, $"constraint_{i}_name")) == constraint
                    && long.TryParse(SysfsHelper.ReadAttributeSilent(Path.Combine(zone, $"constraint_{i}_power_limit_uw")), out long uw))
                {
                    int watts = (int)(uw / 1_000_000);
                    lowest = lowest is int l ? Math.Min(l, watts) : watts;
                }
        return lowest;
    }

    /// <summary>
    /// Make RAPL enforce <paramref name="watts"/> for a PPT attribute when the
    /// firmware did not. True when RAPL matches afterwards; false for attributes
    /// RAPL has no equivalent for, or when RAPL is unavailable.
    /// </summary>
    public static bool Enforce(string attribute, int watts)
    {
        if (!Available || ConstraintFor(attribute) is not var (limit, constraint))
            return false;
        if (EffectiveWatts(constraint) == watts)
            return true;
        if (!Gpu.NVidia.NvidiaProcessScanner.EnsureHelper())
            return false;

        var (_, stderr, exitCode) = SysfsHelper.RunSudoOrPkexecEx(
            SysfsHelper.GpuHelperPath, ["rapl-limit", limit, watts.ToString()], allowPkexec: false);
        int? effective = EffectiveWatts(constraint);
        Helpers.Logger.WriteLine(effective == watts
            ? $"IntelRapl: firmware did not apply {attribute}={watts}; set {constraint} through RAPL"
            : $"IntelRapl: {attribute}={watts} not applied (RAPL {constraint}={effective}, exit {exitCode}): {stderr}");
        return effective == watts;
    }
}
