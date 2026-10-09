using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>Neue Bilder beginnen ohne die Bildregler der vorigen; Ausgabe-Einstellungen bleiben.</summary>
public class NewImagesTests
{
    [Fact]
    public void NewImages_ResetImageAndAiSliders_ButKeepOutputSettings()
    {
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.Dehaze = 54;
        vm.DehazePasses = 7;
        vm.DehazeCast = 48;
        vm.Brightness = 20;
        vm.Warmth = -15;
        vm.SelectedTint = "Farbe";
        vm.TintHue = 200;
        vm.SharpenAmount = 1.2;
        vm.SharpenRadius = 2.5;
        vm.NoiseReduction = 0.5;
        vm.CornerRadius = 12;
        vm.SelectedFormat = "PNG";
        vm.Quality = 50;
        vm.Dpi = 300;
        vm.Ai.Strength = 40;
        vm.Ai.Texture = 80;
        vm.Ai.Fidelity = 90;
        vm.CommitPendingSettings();

        vm.ResetImageSliders();

        Assert.Equal(0, vm.Dehaze);
        Assert.Equal(Adjustments.None.DehazePasses, vm.DehazePasses);
        Assert.Equal(Adjustments.None.DehazeCast, vm.DehazeCast);
        Assert.Equal(0, vm.Brightness);
        Assert.Equal(0, vm.Warmth);
        Assert.Equal("Aus", vm.SelectedTint);
        Assert.Equal(Adjustments.SepiaHue, vm.TintHue);
        Assert.Equal(0, vm.SharpenAmount);
        Assert.Equal(1.0, vm.SharpenRadius);
        Assert.Equal(0, vm.NoiseReduction);
        Assert.Equal(0, vm.CornerRadius);

        Assert.Equal("PNG", vm.SelectedFormat);
        Assert.Equal(50, vm.Quality);
        Assert.Equal(300, vm.Dpi);

        Assert.Equal(100, vm.Ai.Strength);
        Assert.Equal(30, vm.Ai.Texture);
        Assert.Equal(60, vm.Ai.Fidelity);
    }
}
