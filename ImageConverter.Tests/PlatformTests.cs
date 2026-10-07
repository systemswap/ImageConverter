using ImageConverter.Core.Platform;

namespace ImageConverter.Tests;

/// <summary>
/// Programmordner (AppImage oder .exe) und Telemetrie-Schalter der ONNX Runtime.
/// </summary>
public class PlatformTests
{
    [Fact]
    public void ProgramFolder_AppImage_IsFolderOfTheAppImageFile()
    {
        var folder = AppLocations.Resolve("/home/user/Programme/ImageConverter-x86_64.AppImage", "/tmp/.mount_abc/usr/bin/");
        Assert.Equal("/home/user/Programme", folder);
    }

    [Fact]
    public void ProgramFolder_WithoutAppImage_IsProgramDirectory()
    {
        Assert.Equal("/opt/ic/", AppLocations.Resolve(null, "/opt/ic/"));
        Assert.Equal("/opt/ic/", AppLocations.Resolve("", "/opt/ic/"));
    }

    [Fact]
    public void ToolFolder_LiesBesideTheProgram()
    {
        var folder = AppLocations.ToolFolder(NativeLibraries.ImageMagickFolder);
        Assert.Equal(Path.Combine(AppLocations.ProgramFolder, "Tools", "ImageMagick"), folder);
    }

    /// <summary>
    /// Die ONNX Runtime liest den Schalter mit getenv aus der Umgebung des Prozesses —
    /// Environment.SetEnvironmentVariable allein erreicht die unter Linux nicht.
    /// </summary>
    [Fact]
    public void OnnxTelemetry_SwitchReachesTheNativeEnvironment()
    {
        NativeLibraries.DisableOnnxTelemetry();
        Assert.Equal("1", NativeLibraries.NativeEnvironmentVariable(NativeLibraries.OnnxTelemetryVariable));
    }
}
