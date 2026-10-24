using System.Runtime.CompilerServices;
using ImageConverter.Core.Platform;

namespace ImageConverter.Tests;

/// <summary>
/// Applies the ImageMagick policy before the first access to ImageMagick, as the program does
/// (Program.Main → NativeLibraries.Configure). Applied later, the policy takes effect only partially.
/// </summary>
internal static class TestSetup
{
    // CA2255 is suppressed because a module initialiser is intended in the test project.
#pragma warning disable CA2255 // Modul-Initialisierer im Testprojekt ist hier gewollt
    [ModuleInitializer]
    internal static void Init() => ImageMagickPolicy.Apply();
#pragma warning restore CA2255
}
