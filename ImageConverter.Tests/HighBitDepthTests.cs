using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// 16 Bit je Kanal: bei kräftiger Tonwert-Bearbeitung empfohlen und nach jeder solchen Änderung von der App gesetzt (auch
/// wenn er von Hand heraus war); einen selbst gesetzten nimmt sie unter der Schwelle wieder heraus, einen von Hand gesetzten
/// nicht; Rückgängig des auslösenden Reglers nimmt den Haken mit.
/// </summary>
public class HighBitDepthTests
{
    private static MainViewModel Create() => new() { DeferHistoryTurns = false };

    [Fact]
    public void SpreadsTones_FromStrongTonalEdits_Only()
    {
        Assert.False(Adjustments.None.SpreadsTones);
        Assert.False(new Adjustments(Dehaze: 10, Contrast: 15, Saturation: 80, Vibrance: 60).SpreadsTones);   // leicht, Farbe zählt nicht
        Assert.True(new Adjustments(Dehaze: 20).SpreadsTones);
        Assert.True(new Adjustments(Contrast: -25).SpreadsTones);
        Assert.True(new Adjustments(Shadows: 30).SpreadsTones);
        Assert.True(new Adjustments(MaskAmount: 40).SpreadsTones);
    }

    [Fact]
    public void Recommended_TicksItself_AndUntiksWhenItWasTheApp()
    {
        var vm = Create();
        Assert.Equal("PNG", vm.SelectedFormat);
        Assert.False(vm.HighBitDepth);

        vm.Dehaze = 40;
        Assert.True(vm.HighBitDepthRecommended);
        Assert.True(vm.HighBitDepth);              // selbst gesetzt

        vm.Dehaze = 0;
        Assert.False(vm.HighBitDepthRecommended);
        Assert.False(vm.HighBitDepth);             // selbst gesetzt → selbst wieder heraus

        vm.SelectedFormat = "JPEG";                // kein 16 Bit: keine Empfehlung
        vm.Dehaze = 40;
        Assert.False(vm.HighBitDepthRecommended);
    }

    [Fact]
    public void TicksAgain_AfterEveryStrongChange_ManualTickStays()
    {
        var vm = Create();
        vm.Dehaze = 40;
        Assert.True(vm.HighBitDepth);
        vm.HighBitDepth = false;                   // von Hand heraus …
        Assert.False(vm.HighBitDepth);             // … bleibt aus, bis wieder etwas Kräftiges geändert wird
        vm.Dehaze = 60;
        Assert.True(vm.HighBitDepth);              // lieber wieder an als vergessen

        var hand = Create();
        hand.HighBitDepth = true;                  // von Hand gesetzt
        hand.Dehaze = 40;
        hand.Dehaze = 0;
        Assert.True(hand.HighBitDepth);            // die App nimmt ihn nicht heraus
    }

    [Fact]
    public async Task Undo_OfTheSlider_TakesTheTickAlong()
    {
        var vm = Create();
        vm.Dehaze = 40;
        vm.CommitPendingSettings();
        Assert.True(vm.HighBitDepth);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.Dehaze);
        Assert.False(vm.HighBitDepth);
        Assert.False(vm.CanUndo);                  // kein eigener Schritt für den Haken

        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Equal(40, vm.Dehaze);
        Assert.True(vm.HighBitDepth);
    }
}
