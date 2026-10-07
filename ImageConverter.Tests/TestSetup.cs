using System.Runtime.CompilerServices;
using ImageConverter.Core.Platform;

namespace ImageConverter.Tests;

/// <summary>
/// Wie im Programm (Program.Main → NativeLibraries.Configure): die ImageMagick-Richtlinie vor
/// dem ersten Zugriff auf ImageMagick. Später gesetzt, greift sie nur teilweise.
/// </summary>
internal static class TestSetup
{
#pragma warning disable CA2255 // Modul-Initialisierer im Testprojekt ist hier gewollt
    [ModuleInitializer]
    internal static void Init() => ImageMagickPolicy.Apply();
#pragma warning restore CA2255
}
