using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>Neu geladene Bilder beginnen wie nach dem Programmstart — alle Einstellungen auf den Vorgaben, aber je Bild.</summary>
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
        vm.SelectedFormat = "JPEG";
        vm.Quality = 50;
        vm.Dpi = 300;
        vm.TargetWidth = 1200;
        vm.OutputFolder = "irgendwo";
        vm.ApplyToAll = true;
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
        Assert.Equal("PNG", vm.SelectedFormat);
        Assert.Equal(82, vm.Quality);
        Assert.False(vm.ApplyToAll);   // neue Bilder: Einstellungen je Bild — eine Änderung überschreibt nie unbemerkt die anderen
        Assert.True(vm.SelectionTool);
        Assert.False(vm.HasPendingSettings);   // kein eigener Rückgängig-Schritt (den Verlauf leert das Öffnen)

        Assert.Equal(100, vm.Ai.Strength);
        Assert.Equal(69, vm.Ai.Texture);
        Assert.Equal(48, vm.Ai.Folds);
        Assert.Equal(31, vm.Ai.Grain);
        Assert.Equal(22, vm.Ai.GrainSize);
        Assert.Equal(60, vm.Ai.Fidelity);
        Assert.Equal(vm.Ai.OrderOptions[0], vm.Ai.SelectedOrder);
    }

    /// <summary>
    /// Ein Bild, das nie angezeigt wurde (Export aller Bilder, Druck), bekommt die DPI aus seiner Datei —
    /// wie beim ersten Anzeigen. Selbst eingestellte DPI bleiben.
    /// </summary>
    [Fact]
    public void UnseenImage_GetsTheDpiOfItsFile()
    {
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.ResetToStart();   // wie nach dem Laden: je Bild, Vorlage = Startstand

        var unseen = vm.SettingsFor("nie-angezeigt.jpg", 2592, 1944, fileDpi: 300);

        Assert.Equal(300, unseen.Dpi);
        Assert.Equal(2592, unseen.TargetWidth);   // Originalgröße
        Assert.Equal(2592 / 300.0, unseen.PrintWidthIn, 6);

        vm.ApplyToAll = true;
        vm.Dpi = 150;   // selbst eingestellt, gilt für alle
        Assert.Equal(150, vm.SettingsFor("nie-angezeigt.jpg", 2592, 1944, fileDpi: 300).Dpi);
    }
}
