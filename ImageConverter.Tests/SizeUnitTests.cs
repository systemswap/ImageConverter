using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// Größe in Pixel oder Millimeter. Ausgangslage im ViewModel ohne Bild:
/// 4000 × 3000 px bei 72 DPI.
/// </summary>
public class SizeUnitTests
{
    private static MainViewModel Create(int dpi = 300)
    {
        var vm = new MainViewModel();
        vm.Dpi = dpi;
        return vm;
    }

    [Fact]
    public void Pixel_FieldsShowPixels()
    {
        var vm = new MainViewModel();
        Assert.Equal(4000m, vm.FieldWidth);
        Assert.Equal(3000m, vm.FieldHeight);
    }

    [Fact]
    public void SwitchingToMm_ShowsPrintSize()
    {
        var vm = new MainViewModel();      // 4000 px bei 72 DPI = 1411,1 mm
        vm.SelectedSizeUnit = "mm";
        Assert.Equal(1411.1m, vm.FieldWidth);
        Assert.Equal(1058.3m, vm.FieldHeight);
    }

    [Fact]
    public void Mm_EnteredValueStaysExact_AndPixelsFollow()
    {
        var vm = Create(dpi: 300);
        vm.SelectedSizeUnit = "mm";

        vm.FieldWidth = 100m;

        Assert.Equal(100m, vm.FieldWidth);           // nicht 99,9 durch Rundung
        Assert.Equal(75m, vm.FieldHeight);           // Kette: 4:3
        Assert.Equal(1181, vm.TargetWidth);          // 100 mm / 25,4 × 300
        Assert.Equal(886, vm.TargetHeight);
    }

    [Fact]
    public void Mm_TypingHeight_WidthFollows()
    {
        var vm = Create(dpi: 300);
        vm.SelectedSizeUnit = "mm";

        vm.FieldHeight = 150m;

        Assert.Equal(150m, vm.FieldHeight);
        Assert.Equal(200m, vm.FieldWidth);
        Assert.False(vm.WidthIsLeading);
    }

    [Fact]
    public void Mm_NoDrift_WhenSwitchingUnitsBackAndForth()
    {
        var vm = Create(dpi: 300);
        vm.SelectedSizeUnit = "mm";
        vm.FieldWidth = 127m;

        for (var i = 0; i < 20; i++)
        {
            vm.SelectedSizeUnit = "Pixel";
            vm.SelectedSizeUnit = "mm";
        }

        Assert.Equal(127m, vm.FieldWidth);
    }

    [Fact]
    public void Mm_ThenChangingDpi_KeepsPrintSize_ChangesPixels()
    {
        var vm = Create(dpi: 300);
        vm.SelectedSizeUnit = "mm";
        vm.FieldWidth = 100m;

        vm.Dpi = 600;

        Assert.Equal(100m, vm.FieldWidth);
        Assert.Equal(2362, vm.TargetWidth);
    }

    [Fact]
    public void Mm_WithMetadataOnly_PixelsStay_DpiFollows()
    {
        var vm = new MainViewModel { DpiMetadataOnly = true };
        vm.SelectedSizeUnit = "mm";

        vm.FieldWidth = 200m;                        // 4000 px auf 200 mm

        Assert.Equal(4000, vm.TargetWidth);          // Pixel unverändert
        Assert.Equal(508, vm.Dpi);                   // 4000 / (200 / 25,4)
        Assert.Equal(200m, vm.FieldWidth);
        Assert.Equal(150m, vm.FieldHeight);          // folgt den Pixeln (4:3)
    }

    [Fact]
    public void Mm_ChainOpen_OnlyOneEdgeChanges()
    {
        var vm = Create(dpi: 300);
        vm.SelectedSizeUnit = "mm";
        vm.FieldWidth = 100m;
        vm.KeepAspectRatio = false;

        vm.FieldWidth = 50m;

        Assert.Equal(50m, vm.FieldWidth);
        Assert.Equal(75m, vm.FieldHeight);
    }

    [Fact]
    public void PixelEquivalent_OnlyInMm()
    {
        var vm = Create(dpi: 300);
        Assert.Null(vm.PixelEquivalent);

        vm.SelectedSizeUnit = "mm";
        vm.FieldWidth = 100m;

        Assert.Equal("= 1181 × 886 px bei 300 DPI", vm.PixelEquivalent);
    }
}
