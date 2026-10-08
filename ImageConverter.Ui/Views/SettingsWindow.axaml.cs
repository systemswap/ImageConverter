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

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
