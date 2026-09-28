// Privilege separation (ghelperd) and the Intel RAPL PPT fallback.
// Pure functions against the real rules template, no sandbox state.

using GHelper.Linux.Install;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Tests;

public static class PrivilegeSeparationTests
{
    private static string Template()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "install", "90-ghelper.rules");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        throw new AssertException("install/90-ghelper.rules not found above " + AppContext.BaseDirectory);
    }

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- Privilege separation ---");

        Harness.Scenario("HelperRules_NoWorldWritableGrantSurvives", _ =>
        {
            var rules = HelperRules.Transform(Template());
            foreach (var line in rules.Split('\n').Where(l => !l.TrimStart().StartsWith('#')))
                Harness.Assert(!line.Contains("0666"), $"world-writable grant left: {line.Trim()}");
        });

        Harness.Scenario("HelperRules_DropsInjectionAndWholeKeyboardSections", _ =>
        {
            var rules = HelperRules.Transform(Template());
            Harness.Assert(!rules.Contains("KERNEL==\"uinput\""), "uinput rule kept");
            Harness.Assert(!rules.Contains("SUBSYSTEM==\"i2c-dev\""), "i2c-dev rule kept");
            Harness.Assert(!rules.Contains("Touchpad\""), "touchpad grab rule kept");
        });

        Harness.Scenario("HelperRules_GrantsHardwareToHelperGroup", _ =>
        {
            var rules = HelperRules.Transform(Template());
            Harness.Assert(rules.Contains(HelperRules.Grant), "no ghelperd --grant");
            Harness.Assert(rules.Contains($"GROUP=\"{HelperRules.Account}\", MODE=\"0660\""), "no group device grant");
            foreach (var needed in new[] { "asus_custom_fan_curve", "throttle_thermal_policy", "platform_profile",
                         "energy_performance_preference", "charge_control_end_threshold", "Asus WMI hotkeys" })
                Harness.Assert(rules.Contains(needed), $"hardware rule missing: {needed}");
        });

        Harness.Scenario("EscalationRefused_SudoAndHelperRefusals", _ =>
        {
            Harness.Assert(SysfsHelper.EscalationRefused("sudo: a password is required"), "sudo refusal");
            Harness.Assert(SysfsHelper.EscalationRefused("ghelper-run: refused by ghelperd: helper not allowed"), "helper refusal");
            Harness.Assert(!SysfsHelper.EscalationRefused("rapl-limit: write failed: Permission denied"), "command error treated as refusal");
        });

        Harness.Scenario("IntelRapl_ConstraintForPpt", _ =>
        {
            Harness.AssertEqual(("pl1", "long_term"), IntelRapl.ConstraintFor("ppt_pl1_spl")!.Value, "PL1");
            Harness.AssertEqual(("pl2", "short_term"), IntelRapl.ConstraintFor("ppt_pl2_sppt")!.Value, "PL2");
            Harness.Assert(IntelRapl.ConstraintFor("ppt_fppt") == null, "fPPT has no RAPL constraint");
            Harness.Assert(IntelRapl.ConstraintFor("nv_dynamic_boost") == null, "NVIDIA boost is not RAPL");
        });

        Harness.Scenario("Escalate_WithoutHelper_IsSudoN", _ =>
        {
            if (PrivilegeHelper.Available)
                return; // this machine runs ghelperd; the direct form is not what it uses
            var (file, args) = PrivilegeHelper.Escalate("/opt/ghelper/gpu-helper", ["list", "1"]);
            Harness.AssertEqual(SysfsHelper.SudoPath, file, "escalation binary");
            Harness.AssertEqual("-n /opt/ghelper/gpu-helper list 1", string.Join(' ', args), "escalation args");
        });
    }
}
