using System;
using Avalonia;
using ImageConverter.Core.Platform;

namespace ImageConverter.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Vor allem anderen: ImageMagick und ONNX Runtime liegen in Tools/ neben dem Programm,
        // und die ONNX Runtime darf keine Nutzungsdaten senden.
        NativeLibraries.Configure();
        // Eigenes Log neben dem Programm (Abstürze, auch die vom letzten Mal)
        AppLog.Start();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLog.Stop();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Linux: Die Leiste (GNOME, KDE …) ordnet ein Fenster über WM_CLASS seiner
            // .desktop-Datei zu und zeigt deren Icon. Muss zu StartupWMClass in
            // packaging/linux/ImageConverter.desktop passen.
            .With(new X11PlatformOptions { WmClass = "ImageConverter" })
            .LogToTrace();
}