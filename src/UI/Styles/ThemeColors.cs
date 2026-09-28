using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace GHelper.Linux.UI.Styles;

/// <summary>
/// The theme palette (GHelperColors.axaml) for controls built or drawn in code.
/// Resolved on every call against the current theme, so code that reads it when
/// it builds or renders follows a theme switch; XAML uses DynamicResource.
/// </summary>
public static class ThemeColors
{
    public const string ConfigKey = "ui_mode";

    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        Helpers.Logger.WriteLine($"ThemeColors: no brush '{key}' in GHelperColors.axaml");
        return Brushes.Magenta;
    }

    public static Color Color(string key) => Brush(key) is ISolidColorBrush solid ? solid.Color : Colors.Magenta;

    public static bool IsLight => Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    /// <summary>
    /// ui_mode as in the Windows original: "dark", "light", or "windows" (follow
    /// the system, also the default). On Linux the system preference is the
    /// freedesktop colour scheme, which Avalonia follows for ThemeVariant.Default.
    /// </summary>
    public static ThemeVariant FromConfig(string? mode) => mode switch
    {
        "dark" => ThemeVariant.Dark,
        "light" => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };
}
