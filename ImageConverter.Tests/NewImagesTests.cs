using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>Neu geladene Bilder beginnen wie nach dem Programmstart — alle Einstellungen auf den Vorgaben.</summary>
public class NewImagesTests
{
    [Fact]
    public void NewImages_StartLikeAFreshProgram()
    {
        var fresh = new MainViewModel { DeferHistoryTurns = false };
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.Dehaze = 54;
        vm.DehazePasses = 7;
        vm.DehazeCast = 48;
        vm.Brightness = 20;
        vm.SelectedTint = "Farbe";
        vm.SharpenAmount = 1.2;
        vm.SharpenRadius = 2.5;
        vm.NoiseReduction = 0.5;
        vm.CornerRadius = 12;
        vm.SelectedFormat = "PNG";
        vm.Quality = 50;
        vm.Dpi = 300;
        vm.TargetWidth = 1200;
        vm.OutputFolder = "irgendwo";
        vm.ApplyToAll = false;
        vm.SelectionTool = false;
        vm.Ai.Strength = 40;
        vm.Ai.Texture = 80;
        vm.Ai.Fidelity = 90;
        vm.Ai.SelectedOrder = vm.Ai.OrderOptions[1];
        vm.CommitPendingSettings();

        vm.ResetToStart();

        // Calc ist ein unveränderliches Objekt — nach Werten vergleichen
        var (expected, actual) = (fresh.CaptureSettings(), vm.CaptureSettings());
        Assert.Equal((expected.Calc.SourceWidth, expected.Calc.SourceHeight), (actual.Calc.SourceWidth, actual.Calc.SourceHeight));
        Assert.Equal(expected with { Calc = null! }, actual with { Calc = null! });
        Assert.Equal(0, vm.Dehaze);
        Assert.Equal(Adjustments.None.DehazePasses, vm.DehazePasses);
        Assert.Equal("WebP", vm.SelectedFormat);
        Assert.Equal(82, vm.Quality);
        Assert.True(vm.ApplyToAll);
        Assert.True(vm.SelectionTool);
        Assert.False(vm.HasPendingSettings);   // kein eigener Rückgängig-Schritt (den Verlauf leert das Öffnen)

        Assert.Equal(100, vm.Ai.Strength);
        Assert.Equal(30, vm.Ai.Texture);
        Assert.Equal(60, vm.Ai.Fidelity);
        Assert.Equal(vm.Ai.OrderOptions[0], vm.Ai.SelectedOrder);
    }
}
