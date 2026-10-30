using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Platform;

namespace ImageConverter.ViewModels;

/// <summary>
/// Using the app as an image viewer: light or dark appearance, browsing with the keys (the window handles them), full screen
/// with only the picture.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Appearance: 0 = as the system, 1 = light, 2 = dark (Settings › General); stored for the next start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIcon), nameof(ThemeTip))]
    public partial int ThemeIndex { get; set; } = AppSettings.Load().Theme switch
    {
        AppTheme.Light => 1,
        AppTheme.Dark => 2,
        _ => 0,
    };

    partial void OnThemeIndexChanged(int value)
    {
        var mode = value switch { 1 => AppTheme.Light, 2 => AppTheme.Dark, _ => AppTheme.System };
        AppTheme.Apply(mode);
        AppSettings.Update(s => s with { Theme = mode });
    }

    /// <summary>Switches between light and dark (button next to the menu); from "as the system" to the other one.</summary>
    [RelayCommand]
    private void ToggleTheme() => ThemeIndex = AppTheme.IsDark ? 1 : 2;

    /// <summary>Symbol of the switch: the moon leads into the dark appearance, the sun back into the light one.</summary>
    public string ThemeIcon => AppTheme.IsDark ? "☀" : "☾";

    public string ThemeTip => AppTheme.IsDark ? Tr.T("Helle Darstellung") : Tr.T("Dunkle Darstellung (angenehm für die Augen)");

    /// <summary>The system switched between light and dark while following it: the symbol follows.</summary>
    internal void SystemThemeChanged()
    {
        OnPropertyChanged(nameof(ThemeIcon));
        OnPropertyChanged(nameof(ThemeTip));
    }

    /// <summary>Full screen: only the picture and the bar below it (F11, Esc leaves it); the window sets it.</summary>
    [ObservableProperty] public partial bool IsFullScreen { get; set; }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private Task FirstFile() => ShowFileAsync(0);

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private Task LastFile() => ShowFileAsync(_files.Count - 1);
}
