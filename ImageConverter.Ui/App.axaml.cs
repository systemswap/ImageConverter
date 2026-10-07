using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ImageConverter.ViewModels;
using ImageConverter.Views;

namespace ImageConverter;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Desktop-only: die Single-View-Lifetimes (Android/iOS/Browser) sind entfallen.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Dateien aus "Öffnen mit" bzw. der .desktop-Zeile (Exec=… %F) direkt laden
            if (desktop.Args is { Length: > 0 } args)
                _ = vm.OpenAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
