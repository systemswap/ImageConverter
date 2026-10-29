using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
            vm.Ai.PickFile = PickFileAsync;
        };
        Closed += (_, _) =>
        {
            if (vm.Ai.PickFile == PickFileAsync) vm.Ai.PickFile = null;
        };
    }

    /// <summary>File dialog for a model downloaded by hand (RMBG-2.0); starts in the downloads folder if there is one.</summary>
    private async Task<string?> PickFileAsync(string title)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var start = Directory.Exists(downloads) ? await StorageProvider.TryGetFolderFromPathAsync(downloads) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType(Tr.T("KI-Modell")) { Patterns = ["*.onnx", "*.ONNX"] }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
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
            SettingsPage.Background => NavBackground,
            SettingsPage.Dewarp => NavDewarp,
            SettingsPage.Upscale => NavUpscale,
            _ => NavPerformance,
        };
        button.IsChecked = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
