using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace ImageConverter.Viewer3D.Views;

/// <summary>
/// The EDIT group of the column: undo and redo, changing a length, adding or removing a body, moving a face, drawn faces
/// and saving the edited model. Its logic lives in <see cref="ModelViewModel"/> (files ModelViewModel.Editing and
/// neighbours); only the file dialog is here.
/// </summary>
public partial class EditPanel : UserControl
{
    public EditPanel() => AvaloniaXamlLoader.Load(this);

    /// <summary>"Save as": asks for a file name, the original file stays as it is.</summary>
    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModelViewModel model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folder = await storage.TryGetFolderFromPathAsync(System.IO.Path.GetDirectoryName(model.Path) ?? "");
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Modell speichern unter"),
            SuggestedFileName = model.SuggestedSaveName,
            SuggestedStartLocation = folder,
            DefaultExtension = "stl",
            FileTypeChoices = [new FilePickerFileType("STL") { Patterns = ["*.stl"], MimeTypes = ["model/stl"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is { } path) await model.SaveAsAsync(path);
    }
}
