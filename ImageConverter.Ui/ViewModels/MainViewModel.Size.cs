using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageConverter.Models;

namespace ImageConverter.ViewModels;

// Pixel dimensions, aspect ratio chain, DPI and print size.
public partial class MainViewModel
{
    // Prevents the recalculation of one dimension from triggering the recalculation of the other
    // again (width → height → width …).
    private bool _isSyncingSize;

    private ResizeCalculator _calc = new(4000, 3000);

    // Print size in inches as a floating-point number. While the DPI slider is dragged, the
    // pixels are recalculated from it, never from the rounded pixel values; otherwise
    // they drift away during long dragging.
    private double _printWidthIn = 4000 / 72.0;
    private double _printHeightIn = 3000 / 72.0;

    [ObservableProperty] public partial int SourceWidth { get; set; }
    [ObservableProperty] public partial int SourceHeight { get; set; }

    [ObservableProperty] public partial FilterOption SelectedFilter { get; set; }

    [ObservableProperty] public partial bool KeepAspectRatio { get; set; } = true;
    [ObservableProperty] public partial bool GammaCorrect { get; set; } = true;

    [ObservableProperty] public partial int TargetWidth { get; set; }
    [ObservableProperty] public partial int TargetHeight { get; set; }

    /// <summary>Which edge was typed last; the other one is derived.</summary>
    [ObservableProperty] public partial bool WidthIsLeading { get; set; } = true;

    partial void OnTargetWidthChanged(int value)
    {
        if (_isSyncingSize) return;

        if (KeepAspectRatio)
        {
            _isSyncingSize = true;
            WidthIsLeading = true;
            TargetHeight = _calc.HeightFor(value);
            _isSyncingSize = false;
        }

        UpdatePrintSizeFromPixels();
        NotifySizeDerived();
    }

    partial void OnTargetHeightChanged(int value)
    {
        if (_isSyncingSize) return;

        if (KeepAspectRatio)
        {
            _isSyncingSize = true;
            WidthIsLeading = false;
            TargetWidth = _calc.WidthFor(value);
            _isSyncingSize = false;
        }

        UpdatePrintSizeFromPixels();
        NotifySizeDerived();
    }

    partial void OnKeepAspectRatioChanged(bool value)
    {
        // Chain closed afterwards: take over the ratio of the current values
        // so that nothing jumps.
        _calc = value
            ? new ResizeCalculator(Math.Max(1, TargetWidth), Math.Max(1, TargetHeight))
            : new ResizeCalculator(SourceWidth, SourceHeight);

        NotifySizeDerived();
    }

    partial void OnGammaCorrectChanged(bool value)
    {
        RequestPreview();
        UpdateUpscaleStatus();   // with two passes the first one keeps the gamma it was computed with
    }

    /// <summary>New image or new state after crop/rotation: target = original.</summary>
    private void ApplySourceSize(int width, int height)
    {
        _isSyncingSize = true;
        SourceWidth = width;
        SourceHeight = height;
        TargetWidth = width;
        TargetHeight = height;
        WidthIsLeading = true;
        _isSyncingSize = false;

        _calc = new ResizeCalculator(width, height);
        UpdatePrintSizeFromPixels();
        RotationAngle = 0;        // a rotation not yet applied belongs only to the previous state
        PerspectiveTool = false;  // as do the perspective corners
        if (!_keepDewarpTool) DewarpTool = false;   // and the curved-page lines (not during the tool's own "Straighten out", which shows the result)
        ResetWbForNewImage();     // measuring points belong to the previous image (the measured values stay: same light)
        CropSelection = null;
        OnPropertyChanged(nameof(AspectRatio));
        NotifySizeDerived();
    }

    /// <summary>
    /// Dimensions of the finished image changed without a new image state (a geometry stage switched on/off): the source
    /// follows, the target only if it still matched the image, so a set size stays. Tools stay open.
    /// </summary>
    private void FollowSourceSize(int width, int height)
    {
        if (width == SourceWidth && height == SourceHeight) return;
        var untouched = TargetWidth == SourceWidth && TargetHeight == SourceHeight;
        _isSyncingSize = true;
        SourceWidth = width;
        SourceHeight = height;
        if (untouched)
        {
            TargetWidth = width;
            TargetHeight = height;
            WidthIsLeading = true;
        }
        _isSyncingSize = false;
        _calc = new ResizeCalculator(width, height);
        if (untouched) UpdatePrintSizeFromPixels();
        OnPropertyChanged(nameof(AspectRatio));
        NotifySizeDerived();
    }

    private void UpdatePrintSizeFromPixels()
    {
        if (Dpi <= 0) return;
        _printWidthIn = TargetWidth / (double)Dpi;
        _printHeightIn = TargetHeight / (double)Dpi;
    }

    private void NotifySizeDerived()
    {
        OnPropertyChanged(nameof(SizeSummary));
        OnPropertyChanged(nameof(PrintSize));
        OnPropertyChanged(nameof(DpiTip));
        OnPropertyChanged(nameof(ScaleTwiceText));
        OnPropertyChanged(nameof(ScaleFourText));
        OnPropertyChanged(nameof(ScaleSixteenText));
        OnPropertyChanged(nameof(ResetScaleTip));
        OnPropertyChanged(nameof(FieldWidth));
        OnPropertyChanged(nameof(FieldHeight));
        OnPropertyChanged(nameof(PixelEquivalent));
        OnPropertyChanged(nameof(ViewOutputSize));
        SyncScaleChoices();   // direction: different entries in "Method"
        RequestPreview();
    }

    // ------------------------------------------------- Unit pixels / mm

    private const double MmPerInch = 25.4;

    public IReadOnlyList<string> SizeUnits { get; } = ["Pixel", "mm"];

    /// <summary>
    /// "Pixel": the pixel count is decisive.
    /// "mm": the print size is decisive; entered millimetres stay exactly
    /// as entered, and the pixels are derived from them and the DPI.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMillimeter), nameof(SizeFormat), nameof(SizeMinimum),
        nameof(FieldWidth), nameof(FieldHeight), nameof(PixelEquivalent), nameof(HasPixelEquivalent))]
    public partial string SelectedSizeUnit { get; set; } = "Pixel";

    public bool IsMillimeter => SelectedSizeUnit == "mm";
    public string SizeFormat => IsMillimeter ? "0.#" : "0";
    public decimal SizeMinimum => IsMillimeter ? 0.1m : 1m;

    /// <summary>Width in the input field, in the selected unit.</summary>
    public decimal FieldWidth
    {
        get => IsMillimeter ? ToMm(_printWidthIn) : TargetWidth;
        set => SetFieldSize(value, isWidth: true);
    }

    /// <summary>Height in the input field, in the selected unit.</summary>
    public decimal FieldHeight
    {
        get => IsMillimeter ? ToMm(_printHeightIn) : TargetHeight;
        set => SetFieldSize(value, isWidth: false);
    }

    /// <summary>In mm mode: the pixel dimensions that result.</summary>
    public string? PixelEquivalent => IsMillimeter ? Tr.T("= {0} × {1} px bei {2} DPI", TargetWidth, TargetHeight, Dpi) : null;
    public bool HasPixelEquivalent => IsMillimeter;

    private static decimal ToMm(double inches) => Math.Round((decimal)(inches * MmPerInch), 1);

    private void SetFieldSize(decimal value, bool isWidth)
    {
        if (value <= 0) return;

        if (!IsMillimeter)
        {
            // Pixels: the usual logic including the chain
            if (isWidth) TargetWidth = (int)Math.Round(value);
            else TargetHeight = (int)Math.Round(value);
            return;
        }

        // Millimetres: the print size is decisive and stays exactly as entered
        var inches = (double)value / MmPerInch;
        _isSyncingSize = true;
        WidthIsLeading = isWidth;

        // pixels = print size × DPI
        if (isWidth)
        {
            _printWidthIn = inches;
            if (KeepAspectRatio) _printHeightIn = inches / _calc.AspectRatio;
        }
        else
        {
            _printHeightIn = inches;
            if (KeepAspectRatio) _printWidthIn = inches * _calc.AspectRatio;
        }
        TargetWidth = Math.Max(1, (int)Math.Round(_printWidthIn * Dpi));
        TargetHeight = Math.Max(1, (int)Math.Round(_printHeightIn * Dpi));

        _isSyncingSize = false;
        OnPropertyChanged(nameof(DpiPurpose));
        OnPropertyChanged(nameof(DpiTip));
        NotifySizeDerived();
    }

    /// <summary>
    /// Selects a standard format (A4, photo print …): unit set to mm, portrait or landscape like the image; with the
    /// chain closed the image fits inside (one edge fills the format), otherwise exactly the format. The DPI stays, the
    /// pixels follow (pixels = print size × DPI).
    /// </summary>
    /// <param name="key">Key of the format in <see cref="PaperFormats"/>.</param>
    [RelayCommand]
    private void ApplyPaperFormat(string key)
    {
        if (PaperFormats.Find(key) is not { } format) return;

        var (wMm, hMm, widthLeads) = PaperFormats.Fit(_calc.AspectRatio, format, KeepAspectRatio);
        SelectedSizeUnit = "mm";
        if (KeepAspectRatio)
        {
            if (widthLeads) FieldWidth = (decimal)wMm;
            else FieldHeight = (decimal)hMm;
        }
        else
        {
            FieldWidth = (decimal)wMm;
            FieldHeight = (decimal)hMm;
        }
    }

    /// <summary>Sets the dimensions to <paramref name="factor"/> times those of the image (format menu: 2×, 4×, 16×).</summary>
    /// <param name="factor">Integer factor as text; invalid values or values below 1 are ignored.</param>
    [RelayCommand]
    private void ApplyScaleFactor(string factor)
    {
        if (!int.TryParse(factor, out var f) || f < 1) return;
        SetTargetPixels(SourceWidth * f, SourceHeight * f);
    }

    /// <summary>Resets the scaling: dimensions as those of the image again.</summary>
    [RelayCommand(CanExecute = nameof(HasSizeChange))]
    private void ResetScale() => SetTargetPixels(SourceWidth, SourceHeight);

    /// <summary>Sets both edges (one undo step) with the aspect ratio of the image; the print size follows.</summary>
    private void SetTargetPixels(int width, int height)
    {
        CommitPendingSettings();   // a separate undo step, not merged with typing into the dimension fields
        _isSyncingSize = true;
        TargetWidth = Math.Max(1, width);
        TargetHeight = Math.Max(1, height);
        WidthIsLeading = true;
        _isSyncingSize = false;
        if (KeepAspectRatio) _calc = new ResizeCalculator(Math.Max(1, SourceWidth), Math.Max(1, SourceHeight));
        UpdatePrintSizeFromPixels();
        NotifySizeDerived();
        CommitPendingSettings();
    }

    /// <summary>Entries in the format menu: "2×  ·  1000 × 600 px".</summary>
    public string ScaleTwiceText => Tr.T("{0}×  ·  {1} × {2} px", 2, SourceWidth * 2, SourceHeight * 2);
    public string ScaleFourText => Tr.T("{0}×  ·  {1} × {2} px", 4, SourceWidth * 4, SourceHeight * 4);
    public string ScaleSixteenText => Tr.T("{0}×  ·  {1} × {2} px", 16, SourceWidth * 16, SourceHeight * 16);   // AI: two passes

    /// <summary>Tooltip of "Reset" with the original size.</summary>
    public string ResetScaleTip => Tr.T("Skalierung zurücksetzen: wieder die Größe des Bildes ({0} × {1} px).", SourceWidth, SourceHeight);

    public string SizeSummary =>
        $"{SourceWidth} × {SourceHeight} → {TargetWidth} × {TargetHeight} px";

    /// <summary>Target size that is computed (the image size itself if the size step is switched off).</summary>
    private (int Width, int Height) EffectiveTarget()
    {
        if (!Shows(EditSteps.Resize)) return (Math.Max(1, SourceWidth), Math.Max(1, SourceHeight));   // size switched off
        return EffectiveResize();
    }

    private (int Width, int Height) EffectiveResize() => (Math.Max(1, TargetWidth), Math.Max(1, TargetHeight));

    // ---------------------------------------------------------- DPI / print

    [ObservableProperty] public partial int Dpi { get; set; } = (int)ImageLoader.DefaultDpi;

    /// <summary>
    /// The unit decides what the DPI changes: with pixels the pixels stay (only the print size follows, losslessly);
    /// with mm the print size stays and the pixels are recalculated.
    /// </summary>
    private bool DpiMetadataOnly => !IsMillimeter;

    partial void OnDpiChanged(int value)
    {
        OnPropertyChanged(nameof(DpiPurpose));
        OnPropertyChanged(nameof(DpiTip));
        if (_isSyncingSize || value <= 0) return;

        if (DpiMetadataOnly)
        {
            // pixels stay, only the print size changes
            UpdatePrintSizeFromPixels();
        }
        else
        {
            // print size stays, pixels are recalculated
            _isSyncingSize = true;
            TargetWidth = Math.Max(1, (int)Math.Round(_printWidthIn * value));
            TargetHeight = Math.Max(1, (int)Math.Round(_printHeightIn * value));
            _isSyncingSize = false;
        }

        NotifySizeDerived();
    }

    /// <summary>Sets the DPI without recalculating the pixels (for example the value from the file).</summary>
    private void SetDpiSilently(int dpi)
    {
        _isSyncingSize = true;
        Dpi = Math.Max(1, dpi);
        _isSyncingSize = false;
        UpdatePrintSizeFromPixels();
        NotifySizeDerived();
    }

    public string PrintSize =>
        Tr.T("Druckgröße {0:F1} × {1:F1} cm", _printWidthIn * 2.54, _printHeightIn * 2.54);

    /// <summary>Tooltip of the DPI field: what the value is suitable for and what the DPI changes depending on the unit.</summary>
    public string DpiTip => Tr.T("{0}. Bei der Einheit Pixel ändert die DPI nur die Druckgröße — die Pixel bleiben. Bei mm bleibt die Druckgröße, die Pixel werden neu berechnet. Üblich: 72–96 Bildschirm · 150 einfacher Druck · 300 Fotodruck · 600 Kunstdruck · 1200 Strichzeichnungen; bis 2400 möglich.", DpiPurpose);

    /// <summary>What the set resolution is suitable for, as guidance below the slider.</summary>
    public string DpiPurpose => Dpi switch
    {
        <= 100 => Tr.T("Bildschirm und Web"),
        <= 200 => Tr.T("einfacher Druck, Zeitung"),
        <= 360 => Tr.T("Fotodruck (300 ist der Standard)"),
        <= 720 => Tr.T("hochwertiger Kunstdruck"),
        _ => Tr.T("Strichzeichnungen und Scans"),
    };
}
