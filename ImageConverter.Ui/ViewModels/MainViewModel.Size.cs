using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    partial void OnGammaCorrectChanged(bool value) => RequestPreview();

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
        RotationAngle = 0;        // eine noch nicht übernommene Drehung gilt nur für den Stand davor
        PerspectiveTool = false;  // die Ecken zum Entzerren ebenso
        if (!_keepDewarpTool) DewarpTool = false;   // und die Linien der Wölbung (nicht beim eigenen „Gerade ziehen“: Ergebnis zeigen)
        ResetWbForNewImage();     // Messstellen gehören zum Bild davor (die Messwerte bleiben, gleiches Licht)
        CropSelection = null;
        OnPropertyChanged(nameof(AspectRatio));
        NotifySizeDerived();
    }

    /// <summary>
    /// Format des fertigen Bildes geändert, ohne neuen Bildstand (eine Stufe des Ausrichtens ein/aus): Die Quelle folgt,
    /// das Ziel nur, wenn es noch dem Bild entsprach — eine eingestellte Größe bleibt. Werkzeuge bleiben offen.
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
        SyncScaleChoices();   // Richtung: andere Einträge in „Verfahren“
        RequestPreview();
    }

    // ------------------------------------------------- Einheit Pixel / mm

    private const double MmPerInch = 25.4;

    public IReadOnlyList<string> SizeUnits { get; } = ["Pixel", "mm"];

    /// <summary>
    /// „Pixel": die Pixelzahl ist maßgeblich.
    /// „mm": die Druckgröße ist maßgeblich — eingegebene Millimeter bleiben exakt
    /// stehen; Pixel (bzw. bei „Nur Metadaten" die DPI) werden daraus abgeleitet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMillimeter), nameof(SizeFormat), nameof(SizeMinimum),
        nameof(FieldWidth), nameof(FieldHeight), nameof(PixelEquivalent), nameof(HasPixelEquivalent))]
    public partial string SelectedSizeUnit { get; set; } = "Pixel";

    public bool IsMillimeter => SelectedSizeUnit == "mm";
    public string SizeFormat => IsMillimeter ? "0.#" : "0";
    public decimal SizeMinimum => IsMillimeter ? 0.1m : 1m;

    /// <summary>Breite im Eingabefeld — in der gewählten Einheit.</summary>
    public decimal FieldWidth
    {
        get => IsMillimeter ? ToMm(_printWidthIn) : TargetWidth;
        set => SetFieldSize(value, isWidth: true);
    }

    /// <summary>Höhe im Eingabefeld — in der gewählten Einheit.</summary>
    public decimal FieldHeight
    {
        get => IsMillimeter ? ToMm(_printHeightIn) : TargetHeight;
        set => SetFieldSize(value, isWidth: false);
    }

    /// <summary>Im mm-Modus: welche Pixelmaße daraus entstehen.</summary>
    public string? PixelEquivalent => IsMillimeter ? Tr.T("= {0} × {1} px bei {2} DPI", TargetWidth, TargetHeight, Dpi) : null;
    public bool HasPixelEquivalent => IsMillimeter;

    private static decimal ToMm(double inches) => Math.Round((decimal)(inches * MmPerInch), 1);

    private void SetFieldSize(decimal value, bool isWidth)
    {
        if (value <= 0) return;

        if (!IsMillimeter)
        {
            // Pixel: bestehende Logik inkl. Kette
            if (isWidth) TargetWidth = (int)Math.Round(value);
            else TargetHeight = (int)Math.Round(value);
            return;
        }

        // Millimeter: Druckgröße ist maßgeblich und bleibt exakt wie eingegeben
        var inches = (double)value / MmPerInch;
        _isSyncingSize = true;
        WidthIsLeading = isWidth;

        // Pixel = Druckgröße × DPI
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
    /// Standardformat wählen (A4, Fotoabzug …): Einheit auf mm, Hoch- oder Querformat wie das Bild, bei
    /// geschlossener Kette ganz hinein (eine Kante füllt das Format), sonst genau das Format. Die DPI bleibt, die Pixel
    /// folgen (Pixel = Druckgröße × DPI).
    /// </summary>
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

    /// <summary>Maße auf das <paramref name="factor"/>-Fache des Bildes (Menü der Formate: 2×, 4×, 16×).</summary>
    [RelayCommand]
    private void ApplyScaleFactor(string factor)
    {
        if (!int.TryParse(factor, out var f) || f < 1) return;
        SetTargetPixels(SourceWidth * f, SourceHeight * f);
    }

    /// <summary>Skalierung zurücksetzen: Maße wieder wie das Bild.</summary>
    [RelayCommand(CanExecute = nameof(HasSizeChange))]
    private void ResetScale() => SetTargetPixels(SourceWidth, SourceHeight);

    /// <summary>Beide Kanten setzen (ein Rückgängig-Schritt), Seitenverhältnis des Bildes, Druckgröße folgt.</summary>
    private void SetTargetPixels(int width, int height)
    {
        CommitPendingSettings();   // eigener Rückgängig-Schritt, nicht mit Tippen in die Maße zusammengefasst
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

    /// <summary>Einträge im Menü der Formate: „2×  ·  1000 × 600 px“.</summary>
    public string ScaleTwiceText => Tr.T("{0}×  ·  {1} × {2} px", 2, SourceWidth * 2, SourceHeight * 2);
    public string ScaleFourText => Tr.T("{0}×  ·  {1} × {2} px", 4, SourceWidth * 4, SourceHeight * 4);
    public string ScaleSixteenText => Tr.T("{0}×  ·  {1} × {2} px", 16, SourceWidth * 16, SourceHeight * 16);   // KI: zwei Durchgänge

    /// <summary>Tooltip von „Zurücksetzen“ mit der Originalgröße.</summary>
    public string ResetScaleTip => Tr.T("Skalierung zurücksetzen: wieder die Größe des Bildes ({0} × {1} px).", SourceWidth, SourceHeight);

    public string SizeSummary =>
        $"{SourceWidth} × {SourceHeight} → {TargetWidth} × {TargetHeight} px";

    /// <summary>Zielgröße, die gerechnet wird (bei ausgeblendeter Größe das Bild selbst).</summary>
    private (int Width, int Height) EffectiveTarget()
    {
        if (!Shows(EditSteps.Resize)) return (Math.Max(1, SourceWidth), Math.Max(1, SourceHeight));   // Größe ausgeblendet
        return EffectiveResize();
    }

    private (int Width, int Height) EffectiveResize() => (Math.Max(1, TargetWidth), Math.Max(1, TargetHeight));

    // ---------------------------------------------------------- DPI / Druck

    [ObservableProperty] public partial int Dpi { get; set; } = (int)ImageLoader.DefaultDpi;

    /// <summary>
    /// Die Einheit entscheidet, was die DPI ändert: bei Pixel bleiben die Pixel (nur die Druckgröße folgt, verlustfrei),
    /// bei mm bleibt die Druckgröße und die Pixel werden neu berechnet.
    /// </summary>
    private bool DpiMetadataOnly => !IsMillimeter;

    partial void OnDpiChanged(int value)
    {
        OnPropertyChanged(nameof(DpiPurpose));
        OnPropertyChanged(nameof(DpiTip));
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
        Tr.T("Druckgröße {0:F1} × {1:F1} cm", _printWidthIn * 2.54, _printHeightIn * 2.54);

    /// <summary>Tooltip des DPI-Felds: wofür der Wert taugt und was die DPI je nach Einheit ändert.</summary>
    public string DpiTip => Tr.T("{0}. Bei der Einheit Pixel ändert die DPI nur die Druckgröße — die Pixel bleiben. Bei mm bleibt die Druckgröße, die Pixel werden neu berechnet. Üblich: 72–96 Bildschirm · 150 einfacher Druck · 300 Fotodruck · 600 Kunstdruck · 1200 Strichzeichnungen; bis 2400 möglich.", DpiPurpose);

    /// <summary>Wofür sich die eingestellte Auflösung eignet — als Orientierung unter dem Regler.</summary>
    public string DpiPurpose => Dpi switch
    {
        <= 100 => Tr.T("Bildschirm und Web"),
        <= 200 => Tr.T("einfacher Druck, Zeitung"),
        <= 360 => Tr.T("Fotodruck (300 ist der Standard)"),
        <= 720 => Tr.T("hochwertiger Kunstdruck"),
        _ => Tr.T("Strichzeichnungen und Scans"),
    };
}
