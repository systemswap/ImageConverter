using ImageConverter.Core.Platform;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>
/// Programmordner (AppImage oder .exe) und Telemetrie-Schalter der ONNX Runtime.
/// </summary>
public class PlatformTests
{
    [Fact]
    public void ProgramFolder_AppImage_IsFolderOfTheAppImageFile()
    {
        if (OperatingSystem.IsWindows()) return;   // AppImage gibt es nur unter Linux; Windows macht aus / ein \

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

    /// <summary>
    /// Ohne Richtlinie löst ein Pfad wie „https://…“ eine DNS-Anfrage aus und ruft curl auf.
    /// Mit Richtlinie lehnt ImageMagick ab, bevor irgendetwas ins Netz geht.
    /// </summary>
    [Fact]
    public void ImageMagick_RefusesNetworkAddresses()
    {
        ImageMagickPolicy.Apply();
        Assert.Throws<MagickPolicyErrorException>(() => new MagickImage("https://example.invalid/bild.png"));
    }

    [Fact]
    public void ImageMagick_PolicyBlocksExternalPrograms()
    {
        ImageMagickPolicy.Apply();
        var policy = File.ReadAllText(Path.Combine(ImageMagickPolicy.Folder(), "policy.xml"));
        Assert.Contains("domain=\"delegate\" rights=\"none\" pattern=\"*\"", policy);
        Assert.Contains("{HTTP,HTTPS,FTP,URL}", policy);
    }
    /// <summary>
    /// Eigenes Log: Bleibt die Lauf-Datei eines beendeten Prozesses liegen (Absturz), kommt beim nächsten Start ein Eintrag
    /// mit der letzten Tätigkeit ins Log und die Datei verschwindet; die eigene Lauf-Datei hält die Tätigkeit und geht beim
    /// sauberen Ende weg.
    /// </summary>
    [Fact]
    public void AppLog_LeftoverMarker_BecomesCrashEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ic-applog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dead = Path.Combine(dir, $"ImageConverter.{int.MaxValue}.laeuft");
            File.WriteAllLines(dead, ["2026-10-15T09:01:13.0000000+02:00", "2026-10-15T09:07:58.0000000+02:00", "KI-Tab: Testmodell"]);

            AppLog.Start(dir);
            AppLog.Activity("Upscaling: Test");
            var log = Path.Combine(dir, AppLog.FileName);
            for (var i = 0; i < 200 && File.Exists(dead); i++) Thread.Sleep(50);

            var text = File.ReadAllText(log);
            Assert.Contains("Absturz", text);
            Assert.Contains("Zuletzt: KI-Tab: Testmodell (seit 09:07:58)", text);
            Assert.False(File.Exists(dead));
            var own = Path.Combine(dir, $"ImageConverter.{Environment.ProcessId}.laeuft");
            Assert.Contains("Upscaling: Test", File.ReadAllText(own));
            AppLog.Stop();
            Assert.False(File.Exists(own));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
