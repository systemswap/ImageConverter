using System;
using System.Collections.Generic;
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
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(HasAdjustments), nameof(CanCompare))]
    public partial Bitmap? AdjustedBitmap { get; set; }

    /// <summary>
    /// Vorher/Nachher-Schalter: true = „Vorher“ zeigen — das Bild vor den Bildschritten
    /// gleicher Form (KI …), ohne Einstellungen wie Größe, Entrauschen, Schärfe.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(ViewOutputSize))]
    public partial bool ShowOriginal { get; set; }

    partial void OnShowOriginalChanged(bool value)
    {
        if (value) _ = LoadBeforeAsync();
    }

    /// <summary>Anzeige für „Vorher“, wenn es einen früheren Bildstand gibt; sonst gilt <see cref="FullBitmap"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap))]
    public partial Bitmap? BeforeBitmap { get; set; }

    /// <summary>Gibt es etwas zu vergleichen: Einstellungen oder ein früherer Bildstand (KI …)?</summary>
    public bool CanCompare => HasAdjustments || FindBeforeImage() is not null;

    private void NotifyCompare()
    {
        OnPropertyChanged(nameof(CanCompare));
        if (ShowOriginal && !CanCompare) ShowOriginal = false;
    }

    /// <summary>„Vorher“-Bild holen (aus dem Speicher oder einmal umgerechnet).</summary>
    private async Task LoadBeforeAsync()
    {
        if (FindBeforeImage() is not { } before)
        {
            BeforeBitmap = null;
            return;
        }

        var bitmap = await DisplayBitmapForAsync(before);
        if (ShowOriginal && ReferenceEquals(FindBeforeImage(), before))
            BeforeBitmap = bitmap;
        TrimDisplayCache();
    }

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

    /// <summary>
    /// Vorübergehende Anzeige statt der Vorschau, z. B. der Zwischenstand einer langen
    /// Berechnung. Muss dasselbe Seitenverhältnis wie das Bild haben.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap))]
    public partial Bitmap? OverrideBitmap { get; set; }

    /// <summary>Hinweis über dem Bild bei langen Arbeiten (z. B. „KI · 42 % · noch 20 s“); null = keiner.</summary>
    [ObservableProperty] public partial string? ActivityText { get; set; }

    public Bitmap? DisplayBitmap => OverrideBitmap
        ?? (ShowOriginal ? BeforeBitmap ?? FullBitmap : AdjustedBitmap ?? FullBitmap);
    public bool HasImage => FullBitmap is not null;
    public bool HasAdjustments => AdjustedBitmap is not null;

    // ------------------------------------------------------------ Regler

    [ObservableProperty] public partial double SharpenAmount { get; set; }
    [ObservableProperty] public partial double SharpenRadius { get; set; } = 1.0;
    [ObservableProperty] public partial double NoiseReduction { get; set; }

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

    // Anzeige-Bitmaps der letzten Bildstände (volle Auflösung). Rückgängig/Wiederholen
    // eines Zuschnitts oder KI-Schritts tauscht dann nur noch aus, statt neu umzurechnen.
    private const long DisplayCacheBytes = 400L * 1024 * 1024;
    private readonly List<(MagickImage Doc, Bitmap Bitmap, long Bytes)> _displayCache = [];
    private string? _shownPath;

    /// <summary>Neuer Bildstand: volle Auflösung für die Anzeige aufbereiten (oder aus dem Speicher).</summary>
    private async Task SetDocumentAsync(MagickImage doc)
    {
        var w = (int)doc.Width;
        var h = (int)doc.Height;

        var bitmap = await DisplayBitmapForAsync(doc);

        // Gleiches Bild, gleiche Größe (Rückgängig eines KI-Schritts …): Zoom und Ausschnitt
        // bleiben — so lässt sich vorher/nachher an derselben Stelle vergleichen
        var keepView = _shownPath == CurrentPath && DocumentSize == new PixelSize(w, h);
        _shownPath = CurrentPath;

        // Anderes Bild oder andere Größe: Die Vorschau des vorigen Stands passt nicht mehr —
        // sofort das neue Bild zeigen, die Vorschau-Kette rechnet es gleich nach. Sonst stand
        // beim Blättern das vorige Bild noch so lange da, wie die neue Vorschau brauchte.
        if (!keepView) SetAdjusted(null);

        FullBitmap = bitmap;
        TrimDisplayCache();
        DocumentSize = new PixelSize(w, h);
        if (!keepView) ViewResetToken = new object();

        // „Vorher“ gehört zum Bildstand — neu bestimmen
        BeforeBitmap = null;
        NotifyCompare();
        if (ShowOriginal) _ = LoadBeforeAsync();
    }

    /// <summary>Anzeige-Bitmap eines Bildstands — aus dem Speicher oder neu umgerechnet.</summary>
    private async Task<Bitmap> DisplayBitmapForAsync(MagickImage doc)
    {
        var i = _displayCache.FindIndex(e => ReferenceEquals(e.Doc, doc));
        if (i >= 0)
        {
            var hit = _displayCache[i];
            _displayCache.RemoveAt(i);
            _displayCache.Insert(0, hit);
            return hit.Bitmap;
        }

        int w = (int)doc.Width, h = (int)doc.Height;
        var bgra = await Task.Run(() => ImageOps.ToDisplayBgra(doc));
        var bitmap = BitmapOps.FromBgra(bgra, w, h);
        _displayCache.Insert(0, (doc, bitmap, (long)w * h * 4));
        return bitmap;
    }

    /// <summary>Älteste Einträge freigeben, sobald zu viel Speicher belegt ist — nie das angezeigte Bild.</summary>
    private void TrimDisplayCache()
    {
        long total = 0;
        for (var i = 0; i < _displayCache.Count; i++)
        {
            var (_, bitmap, bytes) = _displayCache[i];
            total += bytes;
            if (i == 0 || total <= DisplayCacheBytes
                || ReferenceEquals(bitmap, FullBitmap) || ReferenceEquals(bitmap, BeforeBitmap)) continue;

            bitmap.Dispose();
            _displayCache.RemoveAt(i--);
            total -= bytes;
        }
    }

    private void ClearDisplay()
    {
        var adjusted = AdjustedBitmap;
        FullBitmap = null;
        AdjustedBitmap = null;
        BeforeBitmap = null;
        adjusted?.Dispose();

        foreach (var (_, bitmap, _) in _displayCache) bitmap.Dispose();
        _displayCache.Clear();
        _shownPath = null;
        ClearPreviewCaches();
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

    // Zwischenspeicher der Stufen — nur im Render-Task benutzt, nie gleichzeitig.
    // Je ein paar Ergebnisse, damit Rückgängig/Wiederholen zwischen Ständen nicht neu rechnet.
    private const int StageCacheSize = 4;
    private readonly List<(ResizeKey Key, MagickImage Image)> _resizedCache = [];
    private readonly List<(ResizeKey Key, double Noise, byte[] Bgra)> _denoisedCache = [];

    private readonly record struct ResizeKey(MagickImage Doc, int W, int H, ResampleFilter F, bool G);

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
                // Inzwischen ein anderes Bild gewählt: Ergebnis verwerfen, die Schleife
                // rechnet ohnehin neu (RequestPreview hat _renderDirty gesetzt)
                if (!ReferenceEquals(job.Doc, _history?.Current)) continue;
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

    }

    /// <summary>Zwischenspeicher leeren (Bilder geschlossen).</summary>
    private void ClearPreviewCaches()
    {
        foreach (var (_, image) in _resizedCache) image.Dispose();
        _resizedCache.Clear();
        _denoisedCache.Clear();
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
        var key = new ResizeKey(job.Doc, job.Width, job.Height, job.Filter, job.Gamma);
        var resized = Cached(_resizedCache, e => e.Key == key)?.Image;
        if (resized is null)
        {
            resized = ImageOps.Resize(job.Doc, job.Width, job.Height, job.Filter, job.Gamma);
            AddCached(_resizedCache, (Key: key, Image: resized), e => e.Image.Dispose());
        }

        // 2) Entrauschen — mittel, nur bei geändertem Regler neu
        var denoised = Cached(_denoisedCache, e => e.Key == key && e.Noise == job.Noise)?.Bgra;
        if (denoised is null)
        {
            if (job.Noise > 0)
            {
                using var d = ImageOps.Denoise(resized, job.Noise);
                denoised = ImageOps.ToDisplayBgra(d);
            }
            else
            {
                denoised = ImageOps.ToDisplayBgra(resized);
            }
            AddCached(_denoisedCache, (Key: key, Noise: job.Noise, Bgra: denoised), null);
        }

        // 3) Schärfen — schnell, bei jeder Reglerbewegung
        var final = job.SharpenAmount > 0
            ? Sharpen.UnsharpMask(denoised, job.Width, job.Height, job.SharpenAmount, job.SharpenSigma)
            : denoised;

        return new PreviewResult(final, job.Width, job.Height);
    }

    /// <summary>Treffer nach vorn holen (zuletzt benutzt bleibt am längsten).</summary>
    private static T? Cached<T>(List<T> cache, Func<T, bool> match) where T : struct
    {
        var i = cache.FindIndex(e => match(e));
        if (i < 0) return null;
        var hit = cache[i];
        cache.RemoveAt(i);
        cache.Insert(0, hit);
        return hit;
    }

    private static void AddCached<T>(List<T> cache, T entry, Action<T>? evicted)
    {
        cache.Insert(0, entry);
        while (cache.Count > StageCacheSize)
        {
            evicted?.Invoke(cache[^1]);
            cache.RemoveAt(cache.Count - 1);
        }
    }
}
