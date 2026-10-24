using Avalonia;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// Undo for settings. In these tests every change immediately counts as a separate user action
/// (<c>DeferHistoryTurns = false</c>); <see cref="MainViewModel.CommitPendingSettings"/> closes the open step.
/// </summary>
public class SettingsUndoTests
{
    private static MainViewModel Create() => new() { DeferHistoryTurns = false };

    [Fact]
    public async Task SingleChange_CanBeUndone_AndRedone()
    {
        var vm = Create();
        vm.Quality = 50;
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(82, vm.Quality);

        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Equal(50, vm.Quality);
    }

    [Fact]
    public async Task Upscaler_AndItsSliders_CanBeUndone()
    {
        var vm = Create();
        vm.SelectedUpscaler = ImageConverter.Models.UpscaleOption.Of(ImageConverter.Core.Ai.Upscale.UpscaleMethod.Generative);
        vm.CommitPendingSettings();
        for (var v = 1.0; v >= 0.5; v -= 0.05) vm.UpscaleFidelity = v;
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.UpscaleFidelity);
        Assert.Equal(ImageConverter.Core.Ai.Upscale.UpscaleMethod.Generative, vm.SelectedUpscaler.Method);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(ImageConverter.Core.Ai.Upscale.UpscaleMethod.Filter, vm.SelectedUpscaler.Method);
        Assert.False(vm.UsesAiUpscaler);
    }

    [Fact]
    public async Task DraggingASlider_IsOneStep()
    {
        var vm = Create();
        for (var v = 0.0; v <= 2.0; v += 0.05)
            vm.SharpenAmount = v;     // many intermediate values, as when dragging
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.SharpenAmount);
        Assert.False(vm.CanUndo);     // exactly one step
    }

    [Fact]
    public async Task UncommittedChange_IsUndoneDirectly()
    {
        // Ctrl+Z while the time window is still open: the pending change counts immediately
        var vm = Create();
        vm.SelectedFormat = "JPEG";
        Assert.True(vm.CanUndo);

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Equal("PNG", vm.SelectedFormat);
    }

    [Fact]
    public async Task SizeChange_RestoresPixelsAndMillimetersExactly()
    {
        var vm = Create();
        vm.Dpi = 300;
        vm.CommitPendingSettings();
        vm.SelectedSizeUnit = "mm";
        vm.FieldWidth = 100m;
        vm.CommitPendingSettings();

        vm.FieldWidth = 250m;
        vm.CommitPendingSettings();
        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Equal(100m, vm.FieldWidth);
        Assert.Equal(75m, vm.FieldHeight);
        Assert.Equal(1181, vm.TargetWidth);
    }

    [Fact]
    public async Task UndoSteps_GoBackInOrder()
    {
        var vm = Create();
        vm.SelectedFormat = "JPEG";
        vm.CommitPendingSettings();
        vm.Dpi = 300;
        vm.CommitPendingSettings();
        vm.SelectedMetadataMode = "Alles entfernen";
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal("Alles behalten", vm.SelectedMetadataMode);
        Assert.Equal(300, vm.Dpi);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(72, vm.Dpi);
        Assert.Equal("JPEG", vm.SelectedFormat);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal("PNG", vm.SelectedFormat);
    }

    [Fact]
    public async Task NewChange_AfterUndo_ClearsRedo()
    {
        var vm = Create();
        vm.Quality = 50;
        vm.CommitPendingSettings();
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.True(vm.CanRedo);

        vm.Quality = 60;
        vm.CommitPendingSettings();

        Assert.False(vm.CanRedo);
    }

    [Fact]
    public void ChangingBackAndForth_ToSameValue_CreatesNoStep()
    {
        var vm = Create();
        vm.Quality = 50;
        vm.Quality = 82;
        vm.CommitPendingSettings();

        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task UndoItself_DoesNotCreateNewSteps()
    {
        var vm = Create();
        vm.Quality = 50;
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);
        vm.CommitPendingSettings();   // must not record anything new

        Assert.False(vm.CanUndo);
        Assert.True(vm.CanRedo);
    }

    [Fact]
    public async Task SelectionAndCornerRadius_AreUndoable()
    {
        var vm = Create();
        vm.CropSelection = new Rect(10, 10, 400, 400);
        vm.CommitPendingSettings();
        vm.CornerRadius = 120;
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.CornerRadius);
        Assert.NotNull(vm.CropSelection);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Null(vm.CropSelection);
    }

    [Fact]
    public async Task SameSlider_SeveralDrags_StayOneStep()
    {
        // Drag, release, pause, drag again — one step
        var vm = Create();
        vm.SharpenAmount = 1;
        vm.SharpenAmount = 1.5;
        vm.SharpenAmount = 0.8;
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.SharpenAmount);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task OtherControl_ClosesTheOpenStep()
    {
        var vm = Create();
        vm.Quality = 50;
        vm.SharpenAmount = 1;      // without a pause — still two steps
        vm.Quality = 60;           // quality again: a new step, not the first one
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(50, vm.Quality);
        Assert.Equal(1, vm.SharpenAmount);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.SharpenAmount);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(82, vm.Quality);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task Choices_AreEachTheirOwnStep()
    {
        var vm = Create();
        vm.SelectedFormat = "JPEG";
        vm.SelectedFormat = "TIFF";
        vm.CommitPendingSettings();

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal("JPEG", vm.SelectedFormat);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal("PNG", vm.SelectedFormat);
    }

    [Fact]
    public async Task Selection_EachMouseUpIsAStep()
    {
        var vm = Create();
        vm.CropSelection = new Rect(10, 10, 100, 100);
        vm.CropSelection = new Rect(10, 10, 200, 150);   // dragging
        vm.CommitSettingsStepCommand.Execute(null);        // mouse released
        vm.CropSelection = new Rect(50, 40, 200, 150);   // moving
        vm.CommitSettingsStepCommand.Execute(null);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(new Rect(10, 10, 200, 150), vm.CropSelection);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Null(vm.CropSelection);
    }
}
