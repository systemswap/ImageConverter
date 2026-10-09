using Avalonia;
using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// „Gleiche Einstellungen für alle": wie ein Stand auf ein anders großes Bild
/// übertragen wird. Ausgangslage: Querformat 4000 × 3000 bei 300 DPI.
/// </summary>
public class PerImageSettingsTests
{
    private static MainViewModel.SettingsState Landscape(int tw = 4000, int th = 3000,
        bool keep = true, bool widthLeading = true)
    {
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.Dpi = 300;
        var s = vm.SettingsFor("—", 4000, 3000, ImageLoader.DefaultDpi);   // Vorlage mit allen Standardwerten
        return s with
        {
            SourceWidth = 4000, SourceHeight = 3000,
            TargetWidth = tw, TargetHeight = th,
            KeepAspectRatio = keep, WidthIsLeading = widthLeading,
            Dpi = 300,
            CropSelection = new Rect(10, 10, 100, 100), CornerRadius = 20,
        };
    }

    [Fact]
    public void UntouchedSize_OtherImageKeepsItsOwnFullSize()
    {
        var adapted = MainViewModel.AdaptToImage(Landscape(), 1080, 1920);

        Assert.Equal((1080, 1920), (adapted.TargetWidth, adapted.TargetHeight));
    }

    [Fact]
    public void ChainClosed_WidthTyped_PortraitGetsSameWidth_OwnAspect()
    {
        var adapted = MainViewModel.AdaptToImage(Landscape(1920, 1440), 1080, 1920);

        Assert.Equal(1920, adapted.TargetWidth);
        Assert.Equal(3413, adapted.TargetHeight);   // 1920 × 1920/1080 — nicht verzerrt
    }

    [Fact]
    public void ChainClosed_HeightTyped_HeightCarriesOver()
    {
        var adapted = MainViewModel.AdaptToImage(Landscape(1067, 800, widthLeading: false), 1080, 1920);

        Assert.Equal(800, adapted.TargetHeight);
        Assert.Equal(450, adapted.TargetWidth);
    }

    [Fact]
    public void ChainOpen_ExactSizeForAll()
    {
        var adapted = MainViewModel.AdaptToImage(Landscape(500, 500, keep: false), 1080, 1920);

        Assert.Equal((500, 500), (adapted.TargetWidth, adapted.TargetHeight));
    }

    [Fact]
    public void SelectionAndRadius_BelongToTheOldImage()
    {
        var adapted = MainViewModel.AdaptToImage(Landscape(), 1080, 1920);

        Assert.Null(adapted.CropSelection);
        Assert.Equal(0, adapted.CornerRadius);
        Assert.Equal((1080, 1920), (adapted.SourceWidth, adapted.SourceHeight));
    }

    [Fact]
    public void PrintSize_FollowsNewPixels()
    {
        var adapted = MainViewModel.AdaptToImage(Landscape(1920, 1440), 1080, 1920);

        Assert.Equal(1920 / 300.0, adapted.PrintWidthIn, 6);
    }

    [Fact]
    public void OptionsFor_UsesStateValues_AndDoNotEnlarge()
    {
        var s = Landscape(8000, 6000) with { DoNotEnlarge = true, SelectedFormat = "JPEG", Quality = 70 };

        var o = MainViewModel.OptionsFor(s);

        Assert.Equal(OutputFormat.Jpeg, o.Format);
        Assert.Equal(70, o.Quality);
        Assert.Equal((4000, 3000), (o.Width, o.Height));   // nicht größer als das Original
    }

    [Fact]
    public async Task ToggleApplyToAll_IsUndoable()
    {
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.ApplyToAll = false;
        Assert.True(vm.CanUndo);

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.True(vm.ApplyToAll);
    }
}
