using System.Runtime.InteropServices;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Fremde native Bibliotheken stecken nicht in der Programmdatei, sondern liegen daneben in
/// eigenen Ordnern: Tools/ImageMagick und Tools/OnnxRuntime (siehe ImageConverter.Desktop.csproj).
/// <see cref="Configure"/> muss vor dem ersten Zugriff auf Magick.NET oder die ONNX Runtime
/// laufen. Fehlt ein Ordner (Start aus der IDE, Tests), gilt die normale Suche im
/// Ausgabeordner (runtimes/&lt;rid&gt;/native).
/// </summary>
public static class NativeLibraries
{
    public const string ImageMagickFolder = "ImageMagick";
    public const string OnnxRuntimeFolder = "OnnxRuntime";

    /// <summary>Schalter der ONNX Runtime gegen ihre Telemetrie (siehe <see cref="DisableOnnxTelemetry"/>).</summary>
    public const string OnnxTelemetryVariable = "ORT_DISABLE_TELEMETRY";

    private static readonly Lock Gate = new();
    private static bool _configured;

    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured) return;
            _configured = true;

            DisableOnnxTelemetry();

            // MagickNET.SetNativeLibraryDirectory wirkt unter .NET (Core) nicht — eigener Resolver.
            // Die Bibliothek heißt je nach System Magick.Native-Q16-x64.dll(.so/.dylib).
            var magick = AppLocations.ToolFolder(ImageMagickFolder);
            if (Directory.Exists(magick))
            {
                NativeLibrary.SetDllImportResolver(typeof(MagickImage).Assembly, (name, _, _) =>
                {
                    if (!name.StartsWith("Magick.Native-", StringComparison.Ordinal)) return IntPtr.Zero;
                    var file = new[] { name, name + ".so", name + ".dylib" }
                        .Select(n => Path.Combine(magick, n))
                        .FirstOrDefault(File.Exists);
                    return file is null ? IntPtr.Zero : NativeLibrary.Load(file);
                });
            }

            var onnx = Path.Combine(AppLocations.ToolFolder(OnnxRuntimeFolder), OnnxLibraryFileName);
            if (File.Exists(onnx))
            {
                // Eigener Resolver statt dem eingebauten der ONNX Runtime; den abschalten,
                // sonst meldet er, dass schon einer registriert ist.
                OrtEnv.DisableDllImportResolver = true;
                NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
                    name is "onnxruntime" or "libonnxruntime" ? NativeLibrary.Load(onnx) : IntPtr.Zero);
            }
        }
    }

    internal static string OnnxLibraryFileName =>
        OperatingSystem.IsWindows() ? "onnxruntime.dll"
        : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib"
        : "libonnxruntime.so";

    /// <summary>
    /// Die offiziellen Builds der ONNX Runtime senden unter Linux und macOS Nutzungsdaten an
    /// Microsoft (1DS, mit dauerhafter Geräte-Kennung), unter Windows über ETW. Abgeschaltet
    /// wird das über die Umgebungsvariable ORT_DISABLE_TELEMETRY=1, bevor die Laufzeit startet,
    /// und zusätzlich über die API (<see cref="OnnxSessions"/>).
    /// <para>
    /// Environment.SetEnvironmentVariable ändert unter Linux nur die Kopie von .NET, nicht die
    /// Umgebung des Prozesses, die die native Bibliothek mit getenv liest — deshalb setenv.
    /// Die AppImage setzt die Variable zusätzlich schon in AppRun.
    /// </para>
    /// </summary>
    internal static void DisableOnnxTelemetry()
    {
        Environment.SetEnvironmentVariable(OnnxTelemetryVariable, "1");
        if (!OperatingSystem.IsWindows())
            _ = setenv(OnnxTelemetryVariable, "1", 1);
    }

    /// <summary>Wert einer Variablen in der Umgebung des Prozesses (nicht der Kopie von .NET).</summary>
    internal static string? NativeEnvironmentVariable(string name) =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name)
            : Marshal.PtrToStringUTF8(getenv(name));

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern IntPtr getenv(string name);
}
