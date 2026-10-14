using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

public partial class ExportWindow : Window
{
    // Für den XAML-Designer
    public ExportWindow()
    {
        InitializeComponent();
    }

    public ExportWindow(ExportDialogViewModel vm) : this()
    {
        DataContext = vm;
        Opened += async (_, _) => await vm.LoadAsync();
        vm.ChoiceRequested += async request =>
        {
            var window = new ChoiceWindow(request);
            await window.ShowDialog(this);
            return window.Result;
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        (DataContext as ExportDialogViewModel)?.OnClosing();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
