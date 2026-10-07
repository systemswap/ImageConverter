using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageConverter.Core.Imaging;
using ImageConverter.Models;

namespace ImageConverter.ViewModels;

// Pixelmaße, Seitenverhältnis-Kette, DPI und Druckgröße.
public partial class MainViewModel
{
    // Verhindert, dass das Nachrechnen einer Größe das Nachrechnen der anderen
    // erneut anstößt (Breite → Höhe → Breite …).
    private bool _isSyncingSize;

    private ResizeCalculator _calc = new(4000, 3000);

    // Druckgröße in Zoll als Kommazahl. Beim Ziehen am DPI-Regler werden die
    // Pixel daraus neu gerechnet — nie aus den gerundeten Pixelwerten, sonst
    // wandern sie bei langem Ziehen weg.
    private double _printWidthIn = 4000 / 72.0;
    private double _printHeightIn = 3000 / 72.0;

    [ObservableProperty] public partial int SourceWidth { get; set; }
    [ObservableProperty] public partial int SourceHeight { get; set; }

    [ObservableProperty] public partial FilterOption SelectedFilter { get; set; }

    [ObservableProperty] public partial bool KeepAspectRatio { get; set; } = true;
    [ObservableProperty] public partial bool DoNotEnlarge { get; set; } = true;
    [ObservableProperty] public partial bool GammaCorrect { get; set; } = true;

    [ObservableProperty] public partial int TargetWidth { get; set; }
    [ObservableProperty] public partial int TargetHeight { get; set; }

    /// <summary>Welche Kante wurde zuletzt getippt — die andere ist die abgeleitete.</summary>
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
        // Kette nachträglich geschlossen: Verhältnis der aktuellen Werte
        // übernehmen, damit nichts springt.
        _calc = value
            ? new ResizeCalculator(Math.Max(1, TargetWidth), Math.Max(1, TargetHeight))
            : new ResizeCalculator(SourceWidth, SourceHeight);

        NotifySizeDerived();
    }

    partial void OnDoNotEnlargeChanged(bool value) => NotifySizeDerived();
    partial void OnGammaCorrectChanged(bool value) => RequestPreview();
    partial void OnSelectedFilterChanged(FilterOption value) => RequestPreview();

    /// <summary>Neues Bild bzw. neuer Stand nach Zuschnitt/Drehen: Ziel = Original.</summary>
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
        CropSelection = null;
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
        OnPropertyChanged(nameof(UpscaleHint));
        OnPropertyChanged(nameof(HasUpscaleHint));
        RequestPreview();
    }

    public string SizeSummary =>
        $"{SourceWidth} × {SourceHeight} → {TargetWidth} × {TargetHeight} px";

    /// <summary>
    /// Zielgröße, die tatsächlich gerechnet wird: mit „Nicht vergrößern" höchstens
    /// so groß wie das Original (Seitenverhältnis bleibt).
    /// </summary>
    private (int Width, int Height) EffectiveTarget()
    {
        var w = Math.Max(1, TargetWidth);
        var h = Math.Max(1, TargetHeight);
        if (!DoNotEnlarge || (w <= SourceWidth && h <= SourceHeight)) return (w, h);

        var f = Math.Min((double)SourceWidth / w, (double)SourceHeight / h);
        return (Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)));
    }

    public bool HasUpscaleHint => UpscaleHint is not null;

    public string? UpscaleHint
    {
        get
        {
            if (TargetWidth <= SourceWidth && TargetHeight <= SourceHeight) return null;
            if (!DoNotEnlarge) return "Wird hochgerechnet: mehr Pixel, aber keine zusätzlichen Details.";
            var (w, h) = EffectiveTarget();
            return $"Größer als das Original — wegen „Nicht vergrößern“ bleibt es bei {w} × {h} px.";
        }
    }

    // ---------------------------------------------------------- DPI / Druck

    /// <summary>Obergrenze des Reglers. Im Zahlenfeld geht mehr (bis 2400), etwa für Scans.</summary>
    public const int DpiSliderMax = 1200;

    [ObservableProperty] public partial int Dpi { get; set; } = (int)ImageLoader.DefaultDpi;

    /// <summary>Standard: Pixel neu berechnen — die Druckgröße bleibt, die Pixel folgen der DPI.</summary>
    [ObservableProperty] public partial bool DpiMetadataOnly { get; set; }

    partial void OnDpiChanged(int value)
    {
        OnPropertyChanged(nameof(DpiPurpose));
        if (_isSyncingSize || value <= 0) return;

        if (DpiMetadataOnly)
        {
            // Pixel bleiben, nur die Druckgröße ändert sich
            UpdatePrintSizeFromPixels();
        }
        else
        {
            // Druckgröße bleibt, Pixel werden neu berechnet
            _isSyncingSize = true;
            TargetWidth = Math.Max(1, (int)Math.Round(_printWidthIn * value));
            TargetHeight = Math.Max(1, (int)Math.Round(_printHeightIn * value));
            _isSyncingSize = false;
        }

        NotifySizeDerived();
    }

    /// <summary>DPI setzen, ohne dass Pixel neu gerechnet werden (z. B. Wert aus der Datei).</summary>
    private void SetDpiSilently(int dpi)
    {
        _isSyncingSize = true;
        Dpi = Math.Max(1, dpi);
        _isSyncingSize = false;
        UpdatePrintSizeFromPixels();
        NotifySizeDerived();
    }

    public string PrintSize =>
        $"Druckgröße {_printWidthIn * 2.54:F1} × {_printHeightIn * 2.54:F1} cm";

    /// <summary>Wofür sich die eingestellte Auflösung eignet — als Orientierung unter dem Regler.</summary>
    public string DpiPurpose => Dpi switch
    {
        <= 100 => "Bildschirm und Web",
        <= 200 => "einfacher Druck, Zeitung",
        <= 360 => "Fotodruck (300 ist der Standard)",
        <= 720 => "hochwertiger Kunstdruck",
        _ => "Strichzeichnungen und Scans",
    };
}
