namespace GHelper.Linux.Install;

/// <summary>
/// Udev rules for the privilege-separated install (see
/// Installer.PrivilegeHelperMode), derived from the 90-ghelper.rules template
/// so hardware upstream adds is covered without a second rules file. Kept free
/// of UI types so the scenario tests can compile it.
/// </summary>
public static class HelperRules
{
    public const string Account = "ghelper";
    public const string ClientGroup = "ghelperctl";
    public const string Grant = "/opt/ghelper/ghelperd --grant";

    /// <summary>Sections the helper cannot serve: uinput and numberpad (the
    /// FN-lock remapper, NumberPad and OSK inject input or grab the whole
    /// keyboard) and the world-writable compat block.</summary>
    public static readonly string[] DroppedSections = ["uinput", "numberpad", "compat0666"];

    /// <summary>Rules only the helper needs. A remapper such as keyd grabs the
    /// ASUS keyboard, so its hotkeys arrive on keyd's virtual device instead;
    /// ghelperd reads that one too and forwards only the hotkeys.</summary>
    public static readonly string[] HelperOnlyRules =
    [
        "",
        "# Hotkeys re-emitted by keyd (ghelperd filters out typing)",
        $"SUBSYSTEM==\"input\", KERNEL==\"event*\", ATTRS{{name}}==\"keyd virtual keyboard\", GROUP=\"{Account}\", MODE=\"0660\"",
    ];

    /// <summary>
    /// The template's rules granted to the ghelperd account only. RUN chmod
    /// 0666 becomes "ghelperd --grant" (group read-write, others keep read);
    /// MODE 0666 becomes GROUP ghelper, 0660.
    /// </summary>
    public static string Transform(string template)
    {
        var kept = new List<string>
        {
            "# G-Helper privilege-separated install: hardware access for the",
            $"# {Account} account (ghelperd) only, generated from 90-ghelper.rules.",
        };
        string section = "universal";
        foreach (var line in template.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#@section ", StringComparison.Ordinal))
            {
                section = trimmed["#@section ".Length..].Trim();
                continue;
            }
            if (DroppedSections.Contains(section))
                continue;
            kept.Add(line
                .Replace("/bin/chmod 0666", Grant, StringComparison.Ordinal)
                .Replace("chmod 0666", Grant, StringComparison.Ordinal)
                .Replace("MODE=\"0666\"", $"GROUP=\"{Account}\", MODE=\"0660\"", StringComparison.Ordinal));
        }
        kept.AddRange(HelperOnlyRules);
        return string.Join("\n", kept);
    }
}
