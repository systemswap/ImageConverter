using Avalonia.Controls;
using Avalonia.Interactivity;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

public partial class SettingsWindow : Window
{
    // For the XAML designer.
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(MainViewModel vm) : this()
    {
        DataContext = vm;
        // Detect files copied into the model folder by hand right away.
        Opened += (_, _) =>
        {
            vm.Ai.Rescan();
            vm.Language.Rescan();
        };
    }

    /// <summary>Selects the given page in the navigation on the left (e.g. "Entfernen" from the "Modelle laden …" button).</summary>
    /// <param name="page">Page to show; unknown values select the performance page.</param>
    public void ShowPage(SettingsPage page)
    {
        var button = page switch
        {
            SettingsPage.Performance => NavPerformance,
            SettingsPage.General => NavGeneral,
            SettingsPage.Ai => NavAi,
            SettingsPage.Improve => NavImprove,
            SettingsPage.Faces => NavFaces,
            SettingsPage.Remove => NavRemove,
            SettingsPage.Dewarp => NavDewarp,
            SettingsPage.Upscale => NavUpscale,
            _ => NavPerformance,
        };
        button.IsChecked = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
