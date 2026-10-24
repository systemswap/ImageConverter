using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// Tests „16 Bit je Kanal“: recommended for strong tonal edits and ticked by the app after every such change (even if it
/// was unticked by hand); the app unticks a tick it set itself once the edits fall below the threshold, but not a tick set
/// by hand; undoing the slider that triggered it also undoes the tick.
/// </summary>
public class HighBitDepthTests
{
    private static MainViewModel Create() => new() { DeferHistoryTurns = false };

    [Fact]
    public void SpreadsTones_FromStrongTonalEdits_Only()
    {
        Assert.False(Adjustments.None.SpreadsTones);
        Assert.False(new Adjustments(Dehaze: 10, Contrast: 15, Saturation: 80, Vibrance: 60).SpreadsTones);   // light edits; colour does not count
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
        Assert.True(vm.HighBitDepth);              // set by the app

        vm.Dehaze = 0;
        Assert.False(vm.HighBitDepthRecommended);
        Assert.False(vm.HighBitDepth);             // set by the app → removed by the app

        vm.SelectedFormat = "JPEG";                // no 16 bits: no recommendation
        vm.Dehaze = 40;
        Assert.False(vm.HighBitDepthRecommended);
    }

    [Fact]
    public void TicksAgain_AfterEveryStrongChange_ManualTickStays()
    {
        var vm = Create();
        vm.Dehaze = 40;
        Assert.True(vm.HighBitDepth);
        vm.HighBitDepth = false;                   // unticked by hand …
        Assert.False(vm.HighBitDepth);             // … stays off until another strong change is made
        vm.Dehaze = 60;
        Assert.True(vm.HighBitDepth);              // ticked again rather than forgotten

        var hand = Create();
        hand.HighBitDepth = true;                  // ticked by hand
        hand.Dehaze = 40;
        hand.Dehaze = 0;
        Assert.True(hand.HighBitDepth);            // the app does not untick it
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
        Assert.False(vm.CanUndo);                  // no separate undo step for the tick

        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Equal(40, vm.Dehaze);
        Assert.True(vm.HighBitDepth);
    }
}
