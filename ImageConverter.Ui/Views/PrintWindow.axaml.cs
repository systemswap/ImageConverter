using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

public partial class PrintWindow : Window
{
    // Für den XAML-Designer
    public PrintWindow()
    {
        InitializeComponent();
    }

    public PrintWindow(PrintDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.PickPdfPath = PickPdfPathAsync;
        vm.Close = Close;
        Opened += async (_, _) => await vm.LoadAsync();
    }

    private async Task<string?> PickPdfPathAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Als PDF speichern"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
