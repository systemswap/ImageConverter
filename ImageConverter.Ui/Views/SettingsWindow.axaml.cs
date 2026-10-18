using Avalonia.Controls;
using Avalonia.Interactivity;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

public partial class SettingsWindow : Window
{
    // Für den XAML-Designer
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(MainViewModel vm) : this()
    {
        DataContext = vm;
        // Von Hand in den Modellordner kopierte Dateien gleich erkennen
        Opened += (_, _) =>
        {
            vm.Ai.Rescan();
            vm.Language.Rescan();
        };
    }

    /// <summary>Die genannte Seite links auswählen (z. B. „Entfernen“ aus dem Knopf „Modelle laden …“).</summary>
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
