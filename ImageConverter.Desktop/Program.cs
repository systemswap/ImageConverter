using System;
using Avalonia;

namespace ImageConverter.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Linux: Die Leiste (GNOME, KDE …) ordnet ein Fenster über WM_CLASS seiner
            // .desktop-Datei zu und zeigt deren Icon. Muss zu StartupWMClass in
            // packaging/linux/einrichten.sh und publish.sh passen.
            .With(new X11PlatformOptions { WmClass = "ImageConverter" })
            .LogToTrace();
}