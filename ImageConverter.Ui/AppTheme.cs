using Avalonia;
using Avalonia.Styling;

namespace ImageConverter;

/// <summary>Light, dark or following the system; the colours of both are in Styles/Tokens.axaml.</summary>
public static class AppTheme
{
    /// <summary>Stored values (<see cref="Core.Platform.AppSettings.Theme"/>).</summary>
    public const string System = "System", Light = "Light", Dark = "Dark";

    /// <summary>Switches the whole application; every colour refers to the theme tokens, so it changes at once.</summary>
    /// <param name="mode"><see cref="Light"/>, <see cref="Dark"/> or anything else for following the system.</param>
    public static void Apply(string? mode)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = mode switch
        {
            Light => ThemeVariant.Light,
            Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>Whether the dark colours are shown right now (also when following a dark system setting).</summary>
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
}
