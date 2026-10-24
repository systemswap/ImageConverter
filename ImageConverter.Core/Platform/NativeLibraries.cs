using System.Runtime.InteropServices;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Loads the third-party native libraries from their own folders next to the program.
/// </summary>
/// <remarks>
/// The libraries are not part of the program file; they are stored in Tools/ImageMagick and
/// Tools/OnnxRuntime (see ImageConverter.Desktop.csproj). <see cref="Configure"/> must run before the
/// first use of Magick.NET or the ONNX Runtime. If a folder is missing (start from the IDE, tests), the
/// normal search in the output folder (runtimes/&lt;rid&gt;/native) applies.
/// </remarks>
public static class NativeLibraries
{
    /// <summary>Folder name of ImageMagick below the tools folder (<see cref="AppLocations.ToolFolder"/>).</summary>
    public const string ImageMagickFolder = "ImageMagick";

    /// <summary>Folder name of the ONNX Runtime below the tools folder (<see cref="AppLocations.ToolFolder"/>).</summary>
    public const string OnnxRuntimeFolder = "OnnxRuntime";

    /// <summary>Environment variable that disables the telemetry of the ONNX Runtime (see <see cref="DisableOnnxTelemetry"/>).</summary>
    public const string OnnxTelemetryVariable = "ORT_DISABLE_TELEMETRY";

    private static readonly Lock Gate = new();
    private static bool _configured;

    /// <summary>
    /// Disables the ONNX Runtime telemetry, registers the library resolvers and applies the ImageMagick
    /// policy (<see cref="ImageMagickPolicy.Apply"/>).
    /// </summary>
    /// <remarks>Thread-safe; only the first call has an effect.</remarks>
    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured) return;
            _configured = true;

            DisableOnnxTelemetry();

            // MagickNET.SetNativeLibraryDirectory has no effect on .NET (Core), hence a custom resolver.
            // Depending on the system, the library is named Magick.Native-Q16-x64.dll (.so/.dylib).
            var magick = AppLocations.ToolFolder(ImageMagickFolder);
            if (Directory.Exists(magick))
            {
                NativeLibrary.SetDllImportResolver(typeof(MagickImage).Assembly, (name, _, _) =>
                {
                    if (!name.StartsWith("Magick.Native-", StringComparison.Ordinal)) return IntPtr.Zero;
                    LoadOpenMp(magick);
                    var file = new[] { name, name + ".so", name + ".dylib" }
                        .Select(n => Path.Combine(magick, n))
                        .FirstOrDefault(File.Exists);
                    return file is null ? IntPtr.Zero : NativeLibrary.Load(file);
                });
            }

            var onnx = Path.Combine(AppLocations.ToolFolder(OnnxRuntimeFolder), OnnxLibraryFileName);
            if (File.Exists(onnx))
            {
                // Windows: load DirectML.dll (graphics card) from the same folder first; otherwise the runtime would use
                // the older one from System32.
                var directMl = Path.Combine(Path.GetDirectoryName(onnx)!, "DirectML.dll");
                if (OperatingSystem.IsWindows() && File.Exists(directMl)) NativeLibrary.Load(directMl);

                // Custom resolver instead of the built-in one of the ONNX Runtime; the built-in one must be
                // disabled, otherwise it reports that a resolver is already registered.
                OrtEnv.DisableDllImportResolver = true;
                NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
                    name is "onnxruntime" or "libonnxruntime" ? NativeLibrary.Load(onnx) : IntPtr.Zero);
            }

            // After the resolver (this loads ImageMagick), before any other use of it.
            ImageMagickPolicy.Apply();
        }
    }

    /// <summary>
    /// Loads the OpenMP runtime that the multi-core build of ImageMagick requires: on Windows vcomp140.dll from
    /// the same folder (loaded in advance, otherwise Windows searches only next to the program and in the system
    /// folders), on Linux libgomp.so.1, preferring the system copy over the bundled one.
    /// </summary>
    private static void LoadOpenMp(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var vcomp = Path.Combine(folder, "vcomp140.dll");
                if (File.Exists(vcomp)) NativeLibrary.Load(vcomp);
            }
            else if (OperatingSystem.IsLinux() && !NativeLibrary.TryLoad("libgomp.so.1", out _))
            {
                var gomp = Path.Combine(folder, "libgomp.so.1");
                if (File.Exists(gomp)) NativeLibrary.Load(gomp);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            // ImageMagick then reports itself what is missing.
        }
    }

    internal static string OnnxLibraryFileName =>
        OperatingSystem.IsWindows() ? "onnxruntime.dll"
        : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib"
        : "libonnxruntime.so";

    /// <summary>
    /// Disables the telemetry of the ONNX Runtime.
    /// </summary>
    /// <remarks>
    /// The official builds of the ONNX Runtime send usage data to Microsoft on Linux and macOS (1DS, with a
    /// persistent device identifier) and via ETW on Windows. This is disabled with the environment variable
    /// ORT_DISABLE_TELEMETRY=1 before the runtime starts, and additionally through the API
    /// (<see cref="OnnxSessions"/>).
    /// <para>
    /// On Linux, Environment.SetEnvironmentVariable changes only the copy held by .NET, not the process
    /// environment that the native library reads with getenv; hence setenv. The AppImage also sets the
    /// variable in AppRun.
    /// </para>
    /// </remarks>
    internal static void DisableOnnxTelemetry()
    {
        Environment.SetEnvironmentVariable(OnnxTelemetryVariable, "1");
        if (!OperatingSystem.IsWindows())
            _ = setenv(OnnxTelemetryVariable, "1", 1);
    }

    /// <summary>Value of a variable in the process environment (not the copy held by .NET).</summary>
    internal static string? NativeEnvironmentVariable(string name) =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name)
            : Marshal.PtrToStringUTF8(getenv(name));

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern IntPtr getenv(string name);
}
