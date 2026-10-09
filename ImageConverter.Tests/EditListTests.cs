using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// Liste „Bearbeitungen“: ausgeblendete Bearbeitungen fehlen in Vorschau und Export, die Regler behalten ihren
/// Wert; „Neu anwenden“ setzt genau die ausgeblendeten auf null.
/// </summary>
public class EditListTests
{
    private static MainViewModel Edited()
    {
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.Dehaze = 47;
        vm.DehazePasses = 6;
        vm.Brightness = 12;
        vm.Saturation = -20;
        vm.Warmth = 15;
        vm.SharpenAmount = 0.8;
        vm.NoiseReduction = 0.3;
        vm.TargetWidth = 2000;
        vm.CommitPendingSettings();
        return vm;
    }

    [Fact]
    public void HiddenEdits_AreMissingFromTheExport_ButKeepTheirSliders()
    {
        var vm = Edited();
        vm.HiddenSteps = EditSteps.Dehaze | EditSteps.Sharpen | EditSteps.Resize;

        var s = vm.CaptureSettings();
        var o = MainViewModel.OptionsFor(s);

        Assert.Equal(0, o.Adjustments!.Dehaze);
        Assert.Equal(12, o.Adjustments.Brightness);          // Licht nicht ausgeblendet
        Assert.Equal(0, o.SharpenAmount);
        Assert.Equal(0.3, o.Denoise, 6);
        Assert.Equal((s.SourceWidth, s.SourceHeight), (o.Width, o.Height));   // Größe ausgeblendet: Originalgröße

        Assert.Equal(47, vm.Dehaze);   // der Regler behält seinen Wert
        Assert.Equal(0.8, vm.SharpenAmount, 6);
    }

    [Fact]
    public void ColourGroups_HideSeparately()
    {
        var a = new Adjustments(Brightness: 12, Saturation: -20, Warmth: 15, Dehaze: 47);

        var noColour = MainViewModel.Mask(a, EditSteps.Color);
        Assert.Equal(0, noColour.Saturation);
        Assert.Equal(15, noColour.Warmth);        // „Gleiche Farbe“ bleibt
        Assert.Equal(12, noColour.Brightness);

        var noMatch = MainViewModel.Mask(a, EditSteps.MatchColor | EditSteps.Light);
        Assert.Equal(0, noMatch.Warmth);
        Assert.Equal(0, noMatch.Brightness);
        Assert.Equal(-20, noMatch.Saturation);
        Assert.Equal(47, noMatch.Dehaze);
    }

    [Fact]
    public void ApplySelection_ResetsExactlyTheHiddenEdits()
    {
        var vm = Edited();
        var s = vm.CaptureSettings() with { Hidden = EditSteps.Dehaze | EditSteps.Denoise | EditSteps.Resize };

        var applied = MainViewModel.WithoutHidden(s, s.Hidden);

        Assert.Equal(EditSteps.None, applied.Hidden);
        Assert.Equal(0, applied.Dehaze);
        Assert.Equal(6, applied.DehazePasses);    // Feineinstellung bleibt
        Assert.Equal(0, applied.NoiseReduction);
        Assert.Equal(applied.SourceWidth, applied.TargetWidth);
        Assert.Equal(12, applied.Brightness);     // nicht ausgeblendet: bleibt
        Assert.Equal(0.8, applied.SharpenAmount, 6);
    }

    [Fact]
    public void HidingAnEdit_IsOneUndoStep()
    {
        var vm = Edited();
        vm.HiddenSteps = EditSteps.Dehaze;
        vm.CommitPendingSettings();

        vm.UndoCommand.Execute(null);

        Assert.Equal(EditSteps.None, vm.HiddenSteps);
        Assert.Equal(47, vm.Dehaze);
    }
}
