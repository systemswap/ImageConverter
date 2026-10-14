using System;
using System.Collections.Generic;
using System.Threading;
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
// Skalieren → Entrauschen → Tools → Schärfen — auf einer Kopie mit höchstens
// PreviewMaxEdge Pixeln Kantenlänge. Die Stufen werden einzeln
// zwischengespeichert: Ziehen am Schärfe-Regler rechnet nur die letzte,
// schnelle Stufe neu.
public partial class MainViewModel
{
    private const int PreviewMaxEdge = 2048;

    /// <summary>Das aktuelle Bild in voller Auflösung, unbearbeitet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(HasImage), nameof(CompareBitmap))]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand), nameof(RotateRightCommand), nameof(FlipHorizontalCommand), nameof(ExportCommand), nameof(PrintCommand))]
    public partial Bitmap? FullBitmap { get; set; }

    /// <summary>Ergebnis der Vorschau-Kette; null = nichts zu tun, Original zeigen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(HasAdjustments), nameof(CanCompare), nameof(CompareBitmap))]
    public partial Bitmap? AdjustedBitmap { get; set; }

    /// <summary>
    /// Vorher/Nachher-Schalter: true = Vergleich zeigen — links der verschiebbaren Linie das
    /// Bild vor den Bildschritten gleicher Form (KI …), ohne Einstellungen wie Größe,
    /// Entrauschen, Tools, Schärfe; rechts das Ergebnis.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareBitmap))]
    public partial bool ShowOriginal { get; set; }

    partial void OnShowOriginalChanged(bool value)
    {
        if (value) _ = LoadBeforeAsync();
    }

    /// <summary>Anzeige für „Vorher“, wenn es einen früheren Bildstand gibt; sonst gilt <see cref="FullBitmap"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareBitmap))]
    public partial Bitmap? BeforeBitmap { get; set; }

    /// <summary>
    /// <see cref="FullBitmap"/> verkleinert auf Vorschaugröße — für die Vorher-Seite, solange
    /// die Nachher-Seite ohnehin eine Vorschau ist. Ein Panorama in voller Größe bei jeder
    /// Bewegung der Trennlinie neu zu zeichnen, wäre zäh.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareBitmap))]
    public partial Bitmap? FullPreviewBitmap { get; set; }

    private Bitmap? _fullPreviewOf;

    /// <summary>Vorher-Seite des Vergleichs; null = kein Vergleich (oder Vorher wird noch geladen).</summary>
    public Bitmap? CompareBitmap
    {
        get
        {
            if (!ShowOriginal || FullBitmap is null) return null;
            if (OverrideBitmap is not null) return OverrideBeforeBitmap;   // offenes Ergebnis (KI …)
            if (FindBeforeImage() is not null) return BeforeBitmap;   // früherer Bildstand (KI …)
            return AdjustedBitmap is not null && FullPreviewBitmap is { } small && ReferenceEquals(_fullPreviewOf, FullBitmap)
                ? small
                : FullBitmap;
        }
    }

    /// <summary>Verkleinerte Fassung von <see cref="FullBitmap"/> anlegen, falls größer als die Vorschau.</summary>
    private async Task PrepareFullPreviewAsync()
    {
        if (FullBitmap is not { } full || ReferenceEquals(_fullPreviewOf, full)) return;
        var old = FullPreviewBitmap;
        _fullPreviewOf = full;
        FullPreviewBitmap = null;
        old?.Dispose();

        var size = full.PixelSize;
        var scale = (double)PreviewMaxEdge / Math.Max(size.Width, size.Height);
        if (scale >= 1) return;   // klein genug: das Bild selbst

        var target = new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
        var small = await Task.Run(() => full.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality));
        if (ReferenceEquals(_fullPreviewOf, full) && ReferenceEquals(FullBitmap, full)) FullPreviewBitmap = small;
        else small.Dispose();
    }

    /// <summary>Gibt es etwas zu vergleichen: Einstellungen oder ein früherer Bildstand (KI …)?</summary>
    public bool CanCompare => OverrideBeforeBitmap is not null || HasAdjustments || FindBeforeImage() is not null;

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
            await PrepareFullPreviewAsync();
            return;
        }

        // Vorher ist ein früheres Pixelbild (vor der KI …) — angezeigt mit demselben Ausrichten wie jetzt
        MagickImage shownBefore = before;
        if (_history is { } h && CurrentPath is { } path)
        {
            var (recipe, stages) = (h.Recipe, StagesShown(HiddenSteps) & ToolView);
            shownBefore = await Task.Run(() => WithGeometry(path, recipe, before, stages));
        }
        var bitmap = await DisplayBitmapForAsync(shownBefore);
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
    /// „100 %" heißt ein Ergebnis-Pixel pro Bildschirmpixel.
    /// </summary>
    public PixelSize ViewOutputSize
    {
        get
        {
            if (DocumentSize.Width <= 0) return DocumentSize;
            var (w, h) = EffectiveTarget();
            return new PixelSize(w, h);
        }
    }

    /// <summary>Wechselt bei jedem neuen Bildstand; die Ansicht passt dann neu ein.</summary>
    [ObservableProperty] public partial object? ViewResetToken { get; set; }

    /// <summary>Nur sichtbar, wenn eine Berechnung länger dauert — kein Flackern bei schnellen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial bool ShowRendering { get; set; }

    /// <summary>Auswahlwerkzeug in der Vorschau: an = Ziehen zieht einen Ausschnitt auf, aus = Ziehen verschiebt
    /// das Bild. Eine Ansichtseinstellung, kein Bearbeitungsschritt (nicht im Rückgängig).</summary>
    [ObservableProperty] public partial bool SelectionTool { get; set; } = true;

    /// <summary>Was die Vorschau gerade rechnet (z. B. „Dunst entfernen – Durchlauf 2 von 3“).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial string PreviewStep { get; set; } = "";

    /// <summary>Wie weit die Vorschau ist (0…1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial double PreviewFraction { get; set; }

    /// <summary>
    /// Vorübergehende Anzeige statt der Vorschau, z. B. der Zwischenstand einer langen
    /// Berechnung. Muss dasselbe Seitenverhältnis wie das Bild haben.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(CompareBitmap))]
    public partial Bitmap? OverrideBitmap { get; set; }

    /// <summary>
    /// Vorher-Seite zu <see cref="OverrideBitmap"/>, z. B. das Bild vor einem noch nicht übernommenen
    /// KI-Ergebnis — so lässt sich auch ein offenes Ergebnis mit „Vorher / Nachher“ vergleichen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareBitmap))]
    public partial Bitmap? OverrideBeforeBitmap { get; set; }

    partial void OnOverrideBeforeBitmapChanged(Bitmap? value) => NotifyCompare();

    /// <summary>Hinweis über dem Bild bei langen Arbeiten (z. B. „KI · 42 % · noch 20 s“); null = keiner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial string? ActivityText { get; set; }

    /// <summary>Fortschritt der langen Arbeit 0…100; −1 = ohne Balken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial double ActivityProgress { get; set; } = -1;

    /// <summary>Abbrechen der langen Arbeit (z. B. KI); null = nicht abbrechbar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial System.Windows.Input.ICommand? ActivityCancel { get; set; }

    // Eine Anzeige für alles, was rechnet — unten mitte in der Vorschau: lange Arbeiten (KI, Einstellungen ins Bild)
    // haben Vorrang, sonst die Vorschau-Berechnung, sobald sie spürbar dauert.
    public bool ShowOverlay => ActivityText is not null || ShowRendering;
    public string? OverlayText => ActivityText ?? PreviewStep;
    public bool OverlayHasProgress => ActivityText is not null ? ActivityProgress >= 0 : ShowRendering;
    public double OverlayProgress => ActivityText is not null ? ActivityProgress : PreviewFraction * 100;
    public bool OverlayCanCancel => ActivityText is not null && ActivityCancel is not null;

    /// <summary>Angezeigtes Ergebnis (beim Vergleich die Nachher-Seite).</summary>
    public Bitmap? DisplayBitmap => OverrideBitmap ?? AdjustedBitmap ?? FullBitmap;
    public bool HasImage => FullBitmap is not null;
    public bool HasAdjustments => AdjustedBitmap is not null;

    // ------------------------------------------------------------ Regler

    [ObservableProperty] public partial double SharpenAmount { get; set; }
    [ObservableProperty] public partial double SharpenRadius { get; set; } = 1.0;
    /// <summary>Helligkeitsrauschen 0…1 (Non-Local Means, Stärke relativ zum gemessenen Rauschen).</summary>
    [ObservableProperty] public partial double NoiseReduction { get; set; }

    /// <summary>Farbrauschen 0…1 (helligkeitsgeführter Filter auf den Farbkanälen).</summary>
    [ObservableProperty] public partial double ColorNoiseReduction { get; set; }

    public string SharpenLabel => SharpenAmount <= 0.01 ? Tr.T("aus") : $"{SharpenAmount:F2}";
    public string NoiseLabel => NoiseReduction <= 0.005 ? Tr.T("aus") : $"{NoiseReduction * 100:0} %";
    public string ColorNoiseLabel => ColorNoiseReduction <= 0.005 ? Tr.T("aus") : $"{ColorNoiseReduction * 100:0} %";

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

    partial void OnColorNoiseReductionChanged(double value)
    {
        OnPropertyChanged(nameof(ColorNoiseLabel));
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
        var fullPreview = FullPreviewBitmap;
        FullBitmap = null;
        AdjustedBitmap = null;
        BeforeBitmap = null;
        FullPreviewBitmap = null;
        _fullPreviewOf = null;
        adjusted?.Dispose();
        fullPreview?.Dispose();

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
        (double Luma, double Chroma) Noise, Adjustments Adjust, double SharpenAmount, double SharpenSigma, double Scale,
        GrainSettings? Grain, MagickImage? Upscaled = null);   // Upscaling: Bild ×4 als Grundlage fürs Vergrößern

    private sealed record PreviewResult(byte[] Bgra, int Width, int Height);

    private Task _renderTask = Task.CompletedTask;
    private bool _renderRunning;
    private bool _renderDirty;
    private CancellationTokenSource? _renderCts;

    // Zwischenspeicher der Stufen — nur im Render-Task benutzt, nie gleichzeitig.
    // Je ein paar Ergebnisse, damit Rückgängig/Wiederholen zwischen Ständen nicht neu rechnet.
    // Bildstufen in 32 Bit sind viermal so groß wie früher in 8 Bit (bei 2048 px ~30 MB je Stufe):
    // skaliert vier, die Pixelstufen je zwei (Dunst + Tiefen/Lichter als teuerste drei).
    private const int StageCacheSize = 4;
    private const int PixelCacheSize = 2;
    private const int PreparedCacheSize = 3;
    private readonly List<(ResizeKey Key, MagickImage Image)> _resizedCache = [];
    private readonly List<(ResizeKey Key, (double Luma, double Chroma) Noise, float[] Pixels)> _denoisedCache = [];
    // Weißabgleich + Dunst gesondert: der teuerste Teil, hängt nur von DehazeKey ab — Tiefen/Lichter und die
    // Bereichskarten kommen danach und rechnen ihn nicht neu
    private readonly List<(ResizeKey Key, (double Luma, double Chroma) Noise, Adjustments DehazeKey, float[] Pixels)> _dehazedCache = [];
    // Dunst + Tiefen/Lichter (+ Bereichskarten): hängt nur von PrepareKey ab, die übrigen Regler bleiben flüssig
    private readonly List<(ResizeKey Key, (double Luma, double Chroma) Noise, Adjustments PrepareKey, Adjust.Prepared Prepared)> _tonedCache = [];
    private readonly List<(ResizeKey Key, (double Luma, double Chroma) Noise, Adjustments Adjust, float[] Pixels)> _adjustedCache = [];

    private readonly record struct ResizeKey(MagickImage Doc, int W, int H, ResampleFilter F, bool G, MagickImage? Up = null);

    /// <summary>
    /// Fordert eine neue Vorschau an. Läuft schon eine Berechnung, wird danach genau
    /// einmal mit den dann aktuellen Werten nachgerechnet („die letzte gewinnt").
    /// </summary>
    private void RequestPreview()
    {
        RequestEstimate();   // Größe, Schärfe, Rauschen … ändern auch die Dateigröße
        UpdateHighBitDepthRecommendation();   // Empfehlung 16 Bit folgt den Reglern (und setzt den Haken selbst)
        if (_renderRunning)
        {
            _renderDirty = true;
            _renderCts?.Cancel();   // laufende Rechnung zwischen zwei Schritten abbrechen — der Regler läuft weiter
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

                var cts = _renderCts = new CancellationTokenSource();   // ohne Zeitgeber, Dispose nicht nötig
                PreviewStep = Tr.T("Vorschau wird berechnet …");
                PreviewFraction = 0;
                var plan = new WorkPlan(new Progress<WorkProgress>(p =>
                {
                    PreviewStep = p.Step;
                    PreviewFraction = p.Fraction;
                }), cts.Token);
                PreviewResult result;
                try
                {
                    result = await Task.Run(() => Render(job, plan), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    continue;   // neuere Einstellungen warten schon (_renderDirty)
                }
                // Inzwischen ein anderes Bild gewählt: Ergebnis verwerfen, die Schleife
                // rechnet ohnehin neu (RequestPreview hat _renderDirty gesetzt)
                if (!ReferenceEquals(job.Doc, ShownDocument)) continue;
                SetAdjusted(BitmapOps.FromBgra(result.Bgra, result.Width, result.Height));
            }
            while (_renderDirty);
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Vorschau fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            _renderRunning = false;
            ShowRendering = false;
            EmptyUpscaleTrash();
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
        _renderCts?.Cancel();
        try { await _renderTask; } catch { /* Fehler zeigt die Schleife selbst an */ }

        // Auch eine laufende Größenschätzung liest das Bild noch
        _estimateCts?.Cancel();
        try { await _estimateTask; } catch { /* abgebrochen */ }
        _estimateCache.Clear();
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
        _adjustedCache.Clear();
        _tonedCache.Clear();
        _dehazedCache.Clear();
    }

    /// <summary>Momentaufnahme der Einstellungen (UI-Thread). null = Original zeigen.</summary>
    private PreviewJob? CreateJob()
    {
        if (ShownDocument is not { } doc) return null;

        var (tw, th) = EffectiveTarget();
        var resize = tw != (int)doc.Width || th != (int)doc.Height;
        var noise = (EffectiveNoise, EffectiveColorNoise);
        var sharpen = EffectiveSharpen;
        // „Maske zeigen“ gilt nur hier in der Vorschau (nicht im Export, nicht in der Größenschätzung)
        var adjust = CurrentAdjustments() is var a && ShowMask && Shows(EditSteps.LumMask) ? a with { MaskPreview = true } : CurrentAdjustments();
        var grain = EffectiveGrain;
        if (!resize && noise == (0, 0) && sharpen == 0 && adjust.IsNone && grain is null) return null;
        var upscaled = ToolView == GeometryStages.All ? UpscaledForPreview() : null;   // nicht in der Ansicht eines Werkzeugs

        // Vorschau höchstens PreviewMaxEdge groß; der Schärferadius wird
        // mitskaliert, damit er relativ zum Ergebnis gleich wirkt.
        var scale = Math.Min(1.0, (double)PreviewMaxEdge / Math.Max(tw, th));
        var pw = Math.Max(1, (int)Math.Round(tw * scale));
        var ph = Math.Max(1, (int)Math.Round(th * scale));

        return new PreviewJob(doc, pw, ph, SelectedFilter.Filter, GammaCorrect,
            noise, adjust, sharpen, SharpenRadius * scale, scale, grain, upscaled);
    }

    /// <summary>Hintergrund: die vier Stufen, jede nur wenn nötig neu.</summary>
    private PreviewResult Render(PreviewJob job, WorkPlan plan)
    {
        // 1) Skalieren — teuer, nur bei Größe/Filter/Gamma neu
        var key = new ResizeKey(job.Doc, job.Width, job.Height, job.Filter, job.Gamma, job.Upscaled);
        var resized = Cached(_resizedCache, e => e.Key == key)?.Image;
        if (resized is null)
        {
            resized = ImageOps.Resize(job.Upscaled ?? job.Doc, job.Width, job.Height, job.Filter, job.Gamma);
            AddCached(_resizedCache, (Key: key, Image: resized), e => e.Image.Dispose());
        }

        // 2) Entrauschen — mittel, nur bei geändertem Regler neu
        var denoised = Cached(_denoisedCache, e => e.Key == key && e.Noise == job.Noise)?.Pixels;
        if (denoised is null)
        {
            int docW, docH;
            lock (job.Doc) (docW, docH) = ((int)job.Doc.Width, (int)job.Doc.Height);
            if (job.Noise != (0, 0) && job.Upscaled is null && (long)job.Width * job.Height > (long)docW * docH)
            {
                // Vergrößern: erst entrauschen, dann vergrößern — wie im Export (ImageExporter.NoiseFirst)
                plan.Add(Tr.T("Rauschen entfernen"), WorkPlan.DenoiseCost * docW * docH / 1e6);
                var source = ImageOps.ToDisplayPixels(job.Doc);
                var clean = Denoiser.Apply(source, docW, docH, job.Noise.Luma, job.Noise.Chroma, 1, plan);
                PixelPool.Return(source);
                using var cleanImage = Pixels.ToMagick16(clean, docW, docH, alpha: false);
                PixelPool.Return(clean);
                using var enlarged = ImageOps.Resize(cleanImage, job.Width, job.Height, job.Filter, job.Gamma);
                denoised = ImageOps.ToDisplayPixels(enlarged);
            }
            else
            {
                denoised = ImageOps.ToDisplayPixels(resized);
            }
            if (job.Noise != (0, 0) && (job.Upscaled is not null || (long)job.Width * job.Height <= (long)docW * docH))
            {
                // Helligkeit und Farbe getrennt; Radien mit dem Vorschau-Maßstab wie beim Export
                plan.Add(Tr.T("Rauschen entfernen"), WorkPlan.DenoiseCost * job.Width * job.Height / 1e6);
                var clean = Denoiser.Apply(denoised, job.Width, job.Height, job.Noise.Luma, job.Noise.Chroma, job.Scale, plan);
                PixelPool.Return(denoised);
                denoised = clean;
            }
            AddCached(_denoisedCache, (Key: key, Noise: job.Noise, Pixels: denoised), null, PixelCacheSize);
        }

        // 3) Tools (Licht, Farbe) — schnell, nur bei geänderten Reglern neu
        var adjusted = denoised;
        if (!job.Adjust.IsNone)
        {
            adjusted = Cached(_adjustedCache, e => e.Key == key && e.Noise == job.Noise && e.Adjust == job.Adjust)?.Pixels;
            if (adjusted is null)
            {
                var j = job.Adjust;
                var prepareKey = j.PrepareKey;
                var prepared = Cached(_tonedCache, e => e.Key == key && e.Noise == job.Noise && e.PrepareKey == prepareKey)?.Prepared;
                if (prepared is null)
                {
                    var dehazeKey = j.DehazeKey;
                    var dehazed = Cached(_dehazedCache, e => e.Key == key && e.Noise == job.Noise && e.DehazeKey == dehazeKey)?.Pixels;
                    // Dunst je Durchlauf (nur wenn nicht schon gerechnet) … Tiefen/Lichter … Licht und Farbe
                    Adjust.PlanSteps(j, plan, (double)job.Width * job.Height / 1e6, withDehaze: dehazed is null);
                    if (dehazed is null)
                    {
                        dehazed = Adjust.Dehazed(denoised, job.Width, job.Height, j, plan);
                        AddCached(_dehazedCache, (key, job.Noise, dehazeKey, dehazed), null, PixelCacheSize);
                    }
                    prepared = Adjust.Finish(dehazed, job.Width, job.Height, j, plan);
                    AddCached(_tonedCache, (key, job.Noise, prepareKey, prepared), null, PreparedCacheSize);
                }
                else
                {
                    plan.Add(Tr.T("Licht und Farbe"), 1);
                }
                adjusted = Adjust.Apply(prepared, job.Width, job.Height, j, plan);
                AddCached(_adjustedCache, (Key: key, Noise: job.Noise, Adjust: job.Adjust, Pixels: adjusted), null, PixelCacheSize);
            }
        }

        // 4) Schärfen — schnell, bei jeder Reglerbewegung
        var final = job.SharpenAmount > 0
            ? Sharpen.UnsharpMask(adjusted, job.Width, job.Height, job.SharpenAmount, job.SharpenSigma)
            : adjusted;

        // 5) Körnung — immer zuletzt; in der verkleinerten Vorschau so, wie sie im verkleinerten Export aussähe
        if (job.Grain is { } grain)
        {
            plan.Add(Tr.T("Körnung"), WorkPlan.GrainCost * job.Width * job.Height / 1e6);
            plan.Next();
            var grainy = Grain.Apply(final, job.Width, job.Height, grain, job.Scale);
            if (!ReferenceEquals(final, adjusted)) PixelPool.Return(final);
            final = grainy;
        }

        // Einmal runden, mit Dither — erst hier wird aus 32 Bit die Anzeige in 8 Bit
        var bgra = Pixels.ToBytes(final, dither: true);
        if (job.Grain is not null) PixelPool.Return(final);
        // Die Zwischenstände dieser Rechnung (Dunst-Durchläufe …) sollen nicht belegt bleiben
        PixelPool.Trim();
        return new PreviewResult(bgra, job.Width, job.Height);
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

    private static void AddCached<T>(List<T> cache, T entry, Action<T>? evicted, int size = StageCacheSize)
    {
        cache.Insert(0, entry);
        while (cache.Count > size)
        {
            evicted?.Invoke(cache[^1]);
            cache.RemoveAt(cache.Count - 1);
        }
    }
}
