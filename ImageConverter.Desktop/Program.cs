using System;
using Avalonia;
using ImageConverter.Core.Platform;

namespace ImageConverter.Desktop;

sealed class Program
{
    // Entry point. Do not use Avalonia, third-party APIs or SynchronizationContext-dependent
    // code before AppMain is called: they are not initialised yet.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before anything else: ImageMagick and ONNX Runtime are loaded from Tools/ next to
        // the program, and ONNX Runtime telemetry is switched off before the runtime is first used.
        NativeLibraries.Configure();
        // Own log file next to the program; also records crashes of the previous run.
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

    // Avalonia configuration; do not remove, the visual designer uses it as well.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Linux: the panel (GNOME, KDE …) matches a window to its .desktop file via WM_CLASS and
            // shows that file's icon. Must match StartupWMClass in
            // packaging/linux/ImageConverter.desktop.
            .With(new X11PlatformOptions { WmClass = "ImageConverter" })
            .LogToTrace();
}