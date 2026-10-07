using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageConverter.Core.Imaging;
using ImageConverter.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// Anzeige und Live-Vorschau.
//
// Die Vorschau rechnet dieselbe Kette wie später der Export —
// Skalieren → Entrauschen → Schärfen — auf einer Kopie mit höchstens
// PreviewMaxEdge Pixeln Kantenlänge. Die drei Stufen werden einzeln
// zwischengespeichert: Ziehen am Schärfe-Regler rechnet nur die letzte,
// schnelle Stufe neu.
public partial class MainViewModel
{
    private const int PreviewMaxEdge = 2048;

    /// <summary>Das aktuelle Bild in voller Auflösung, unbearbeitet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(HasImage))]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand), nameof(RotateRightCommand), nameof(FlipHorizontalCommand), nameof(ExportCommand), nameof(PrintCommand))]
    public partial Bitmap? FullBitmap { get; set; }

    /// <summary>Ergebnis der Vorschau-Kette; null = nichts zu tun, Original zeigen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(HasAdjustments))]
    public partial Bitmap? AdjustedBitmap { get; set; }

    /// <summary>Vorher/Nachher-Schalter: true = Original zeigen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(ViewOutputSize))]
    public partial bool ShowOriginal { get; set; }

    /// <summary>Maße des Bildes in Pixeln — die Auswahl rechnet immer damit,
    /// auch wenn gerade eine kleinere Vorschau angezeigt wird.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewOutputSize))]
    public partial PixelSize DocumentSize { get; set; }

    /// <summary>
    /// Größe des angezeigten Ergebnisses — darauf bezieht sich die Zoom-Anzeige:
    /// „100 %" heißt ein Ergebnis-Pixel pro Bildschirmpixel. Bei „Vorher" das Original.
    /// </summary>
    public PixelSize ViewOutputSize
    {
        get
        {
            if (ShowOriginal || DocumentSize.Width <= 0) return DocumentSize;
            var (w, h) = EffectiveTarget();
            return new PixelSize(w, h);
        }
    }

    /// <summary>Wechselt bei jedem neuen Bildstand; die Ansicht passt dann neu ein.</summary>
    [ObservableProperty] public partial object? ViewResetToken { get; set; }

    /// <summary>Nur sichtbar, wenn eine Berechnung länger dauert — kein Flackern bei schnellen.</summary>
    [ObservableProperty] public partial bool ShowRendering { get; set; }

    public Bitmap? DisplayBitmap => ShowOriginal || AdjustedBitmap is null ? FullBitmap : AdjustedBitmap;
    public bool HasImage => FullBitmap is not null;
    public bool HasAdjustments => AdjustedBitmap is not null;

    // ------------------------------------------------------------ Regler

    [ObservableProperty] public partial double SharpenAmount { get; set; }
    [ObservableProperty] public partial double SharpenRadius { get; set; } = 1.0;
    [ObservableProperty] public partial double NoiseReduction { get; set; }
    [ObservableProperty] public partial bool UseAiDenoise { get; set; }

    public string SharpenLabel => SharpenAmount <= 0.01 ? "aus" : $"{SharpenAmount:F2}";
    public string NoiseLabel => NoiseReduction <= 0.01 ? "aus" : $"{NoiseReduction:F2}";

    partial void OnSharpenAmountChanged(double value)
    {
        OnPropertyChanged(nameof(SharpenLabel));
        RequestPreview();
    }

    partial void OnSharpenRadiusChanged(double value) => RequestPreview();

    partial void OnNoiseReductionChanged(double value)
    {
        OnPropertyChanged(nameof(NoiseLabel));
        RequestPreview();
    }

    // ------------------------------------------------------------ Anzeige

    /// <summary>Neuer Bildstand: volle Auflösung für die Anzeige aufbereiten.</summary>
    private async Task SetDocumentAsync(MagickImage doc)
    {
        var w = (int)doc.Width;
        var h = (int)doc.Height;
        var bgra = await Task.Run(() => ImageOps.ToDisplayBgra(doc));

        var old = FullBitmap;
        FullBitmap = BitmapOps.FromBgra(bgra, w, h);
        old?.Dispose();

        DocumentSize = new PixelSize(w, h);
        ViewResetToken = new object();
    }

    private void ClearDisplay()
    {
        var full = FullBitmap;
        var adjusted = AdjustedBitmap;
        FullBitmap = null;
        AdjustedBitmap = null;
        full?.Dispose();
        adjusted?.Dispose();
    }

    private void SetAdjusted(Bitmap? bitmap)
    {
        var old = AdjustedBitmap;
        AdjustedBitmap = bitmap;
        old?.Dispose();
    }

    // ------------------------------------------------------------ Rechnen

    private sealed record PreviewJob(
        MagickImage Doc, int Width, int Height,
        ResampleFilter Filter, bool Gamma,
        double Noise, double SharpenAmount, double SharpenSigma);

    private sealed record PreviewResult(byte[] Bgra, int Width, int Height);

    private Task _renderTask = Task.CompletedTask;
    private bool _renderRunning;
    private bool _renderDirty;

    // Zwischenspeicher der Stufen — nur im Render-Task benutzt, nie gleichzeitig
    private MagickImage? _resized;
    private (MagickImage Doc, int W, int H, ResampleFilter F, bool G)? _resizedKey;
    private byte[]? _denoisedBgra;
    private double _denoisedNoise = double.NaN;

    /// <summary>
    /// Fordert eine neue Vorschau an. Läuft schon eine Berechnung, wird danach genau
    /// einmal mit den dann aktuellen Werten nachgerechnet („die letzte gewinnt").
    /// </summary>
    private void RequestPreview()
    {
        RequestEstimate();   // Größe, Schärfe, Rauschen … ändern auch die Dateigröße
        if (_renderRunning)
        {
            _renderDirty = true;
            return;
        }
        _renderTask = RenderLoopAsync();
    }

    private async Task RenderLoopAsync()
    {
        _renderRunning = true;
        _ = ShowRenderingIfSlowAsync();
        try
        {
            do
            {
                _renderDirty = false;
                var job = CreateJob();
                if (job is null)
                {
                    SetAdjusted(null);
                    continue;
                }

                var result = await Task.Run(() => Render(job));
                SetAdjusted(BitmapOps.FromBgra(result.Bgra, result.Width, result.Height));
            }
            while (_renderDirty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Vorschau fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            _renderRunning = false;
            ShowRendering = false;
        }

    }

    private async Task ShowRenderingIfSlowAsync()
    {
        await Task.Delay(180);
        if (_renderRunning) ShowRendering = true;
    }

    /// <summary>Wartet eine laufende Berechnung ab und leert die Zwischenspeicher.</summary>
    private async Task StopPreviewAsync()
    {
        _renderDirty = false;
        try { await _renderTask; } catch { /* Fehler zeigt die Schleife selbst an */ }

        // Auch eine laufende Größenschätzung liest das Bild noch
        _estimateCts?.Cancel();
        try { await _estimateTask; } catch { /* abgebrochen */ }
        _estimateBase?.Dispose();
        _estimateBase = null;
        _estimateBaseKey = null;

        _resized?.Dispose();
        _resized = null;
        _resizedKey = null;
        _denoisedBgra = null;
        _denoisedNoise = double.NaN;
    }

    /// <summary>Momentaufnahme der Einstellungen (UI-Thread). null = Original zeigen.</summary>
    private PreviewJob? CreateJob()
    {
        if (_history?.Current is not { } doc) return null;

        var (tw, th) = EffectiveTarget();
        var resize = tw != (int)doc.Width || th != (int)doc.Height;
        var noise = NoiseReduction > 0.01 ? NoiseReduction : 0;
        var sharpen = SharpenAmount > 0.01 ? SharpenAmount : 0;
        if (!resize && noise == 0 && sharpen == 0) return null;

        // Vorschau höchstens PreviewMaxEdge groß; der Schärferadius wird
        // mitskaliert, damit er relativ zum Ergebnis gleich wirkt.
        var scale = Math.Min(1.0, (double)PreviewMaxEdge / Math.Max(tw, th));
        var pw = Math.Max(1, (int)Math.Round(tw * scale));
        var ph = Math.Max(1, (int)Math.Round(th * scale));

        return new PreviewJob(doc, pw, ph, SelectedFilter.Filter, GammaCorrect,
            noise, sharpen, SharpenRadius * scale);
    }

    /// <summary>Hintergrund: die drei Stufen, jede nur wenn nötig neu.</summary>
    private PreviewResult Render(PreviewJob job)
    {
        // 1) Skalieren — teuer, nur bei Größe/Filter/Gamma neu
        var key = (job.Doc, job.Width, job.Height, job.Filter, job.Gamma);
        if (_resized is null || _resizedKey != key)
        {
            _resized?.Dispose();
            _resized = ImageOps.Resize(job.Doc, job.Width, job.Height, job.Filter, job.Gamma);
            _resizedKey = key;
            _denoisedBgra = null;
        }

        // 2) Entrauschen — mittel, nur bei geändertem Regler neu
        if (_denoisedBgra is null || _denoisedNoise != job.Noise)
        {
            if (job.Noise > 0)
            {
                using var denoised = ImageOps.Denoise(_resized, job.Noise);
                _denoisedBgra = ImageOps.ToDisplayBgra(denoised);
            }
            else
            {
                _denoisedBgra = ImageOps.ToDisplayBgra(_resized);
            }
            _denoisedNoise = job.Noise;
        }

        // 3) Schärfen — schnell, bei jeder Reglerbewegung
        var final = job.SharpenAmount > 0
            ? Sharpen.UnsharpMask(_denoisedBgra, job.Width, job.Height, job.SharpenAmount, job.SharpenSigma)
            : _denoisedBgra;

        return new PreviewResult(final, job.Width, job.Height);
    }
}
