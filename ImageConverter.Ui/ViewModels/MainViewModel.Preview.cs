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

// Display and live preview.
//
// The preview computes the same chain as the export later does
// (scale → denoise → adjustments → sharpen → grain) on a copy with at most
// PreviewMaxEdge pixels edge length. The stages are cached
// individually: dragging the sharpness slider recomputes only the late,
// fast stages.
public partial class MainViewModel
{
    private const int PreviewMaxEdge = 2048;

    /// <summary>
    /// While a slider is dragged (mouse button pressed, or changes in quick succession: mouse wheel, arrow keys) the
    /// preview is computed with at most this edge length: a quarter of the pixels, about four times as fast. After
    /// release or <see cref="RapidMs"/> of quiet the fine preview follows.
    /// </summary>
    private const int PreviewDragEdge = 1024;

    /// <summary>Edge length on which dehaze is estimated in the coarse preview (otherwise 1024).</summary>
    private const int CoarseDehazeEdge = 512;

    /// <summary>Two changes within this interval (ms) count as dragging; below <see cref="SameActionMs"/> they count as one action.</summary>
    private const int RapidMs = 300, SameActionMs = 10;

    /// <summary>Whether to compute coarsely while dragging; only in the app, because tests and tools always need the fine preview.</summary>
    internal bool CoarseWhileDragging { get; init; }

    private bool _pointerDown;
    private bool _rapidChanges;
    private long _lastRequestTicks = long.MinValue / 2;
    private bool _shownCoarse;   // the displayed preview is the coarse one; the fine one follows

    /// <summary>The displayed image state in full resolution, without the settings chain (size, denoise, adjustments, sharpening).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(HasImage), nameof(CompareBitmap), nameof(ShowsEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand), nameof(RotateRightCommand), nameof(FlipHorizontalCommand), nameof(ExportCommand), nameof(PrintCommand))]
    public partial Bitmap? FullBitmap { get; set; }

    /// <summary>Result of the preview chain; null = nothing to do, show <see cref="FullBitmap"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(HasAdjustments), nameof(CanCompare), nameof(CompareBitmap))]
    public partial Bitmap? AdjustedBitmap { get; set; }

    /// <summary>
    /// Before/after switch: true = show the comparison. Left of the movable line is the
    /// image before the image steps of the same shape (AI …), without settings such as size,
    /// denoise, adjustments, sharpness; on the right the result.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareBitmap))]
    public partial bool ShowOriginal { get; set; }

    partial void OnShowOriginalChanged(bool value)
    {
        if (value) _ = LoadBeforeAsync();
    }

    /// <summary>Display for "Before" if there is an earlier image state; otherwise <see cref="FullBitmap"/> is used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareBitmap))]
    public partial Bitmap? BeforeBitmap { get; set; }

    /// <summary>
    /// <see cref="FullBitmap"/> reduced to preview size, for the "Before" side while
    /// the "After" side is a preview anyway. Redrawing a full-size panorama on every
    /// movement of the dividing line would be sluggish.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareBitmap))]
    public partial Bitmap? FullPreviewBitmap { get; set; }

    private Bitmap? _fullPreviewOf;

    /// <summary>"Before" side of the comparison; null = no comparison (or "Before" is still loading).</summary>
    public Bitmap? CompareBitmap
    {
        get
        {
            if (!ShowOriginal || FullBitmap is null) return null;
            if (OverrideBitmap is not null) return OverrideBeforeBitmap;   // pending result (AI …)
            if (FindBeforeImage() is not null) return BeforeBitmap;   // earlier image state (AI …)
            return AdjustedBitmap is not null && FullPreviewBitmap is { } small && ReferenceEquals(_fullPreviewOf, FullBitmap)
                ? small
                : FullBitmap;
        }
    }

    /// <summary>Creates a reduced version of <see cref="FullBitmap"/> if it is larger than the preview.</summary>
    private async Task PrepareFullPreviewAsync()
    {
        if (FullBitmap is not { } full || ReferenceEquals(_fullPreviewOf, full)) return;
        var old = FullPreviewBitmap;
        _fullPreviewOf = full;
        FullPreviewBitmap = null;
        old?.Dispose();

        var size = full.PixelSize;
        var scale = (double)PreviewMaxEdge / Math.Max(size.Width, size.Height);
        if (scale >= 1) return;   // small enough: use the image itself

        var target = new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
        var small = await Task.Run(() => BitmapOps.Shrink(full, target));   // CreateScaledBitmap rejects WriteableBitmap (No. 106)
        if (ReferenceEquals(_fullPreviewOf, full) && ReferenceEquals(FullBitmap, full)) FullPreviewBitmap = small;
        else small.Dispose();
    }

    /// <summary>Whether there is something to compare: settings, a pending result or an earlier image state (AI …).</summary>
    public bool CanCompare => OverrideBeforeBitmap is not null || HasAdjustments || FindBeforeImage() is not null;

    private void NotifyCompare()
    {
        OnPropertyChanged(nameof(CanCompare));
        if (ShowOriginal && !CanCompare) ShowOriginal = false;
    }

    /// <summary>Fetches the "Before" image (from the cache or converted once).</summary>
    private async Task LoadBeforeAsync()
    {
        if (FindBeforeImage() is not { } before)
        {
            BeforeBitmap = null;
            await PrepareFullPreviewAsync();
            return;
        }

        // "Before" is an earlier pixel image (before the AI …), displayed with the same geometry as now.
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

    /// <summary>Dimensions of the displayed image in pixels; the selection always uses them,
    /// even while a smaller preview is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewOutputSize))]
    public partial PixelSize DocumentSize { get; set; }

    /// <summary>
    /// Size of the displayed result, to which the zoom display refers:
    /// "100 %" means one result pixel per screen pixel.
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

    /// <summary>Changes with every new image (or new image size); the view then fits the image again.</summary>
    [ObservableProperty] public partial object? ViewResetToken { get; set; }

    /// <summary>Visible only when a computation takes longer, so that fast ones do not flicker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial bool ShowRendering { get; set; }

    /// <summary>Selection tool in the preview: on = dragging draws a selection, off = dragging pans
    /// the image. A view setting, not an editing step (not part of undo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionActive))]
    public partial bool SelectionTool { get; set; } = true;

    /// <summary>Drawing a frame is possible; not during upscaling (dragging then pans the image).</summary>
    public bool SelectionActive => SelectionTool && !IsUpscaling;

    /// <summary>What the preview is computing right now (for example "Dehaze – pass 2 of 3").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial string PreviewStep { get; set; } = "";

    /// <summary>Progress of the preview (0…1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial double PreviewFraction { get; set; }

    /// <summary>
    /// Temporary display instead of the preview, for example the intermediate result of a long
    /// computation. Must have the same aspect ratio as the image.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBitmap), nameof(CompareBitmap))]
    public partial Bitmap? OverrideBitmap { get; set; }

    /// <summary>
    /// "Before" side for <see cref="OverrideBitmap"/>, for example the image before an AI result that has not yet been
    /// applied, so that a pending result can also be compared with "Before / After".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareBitmap))]
    public partial Bitmap? OverrideBeforeBitmap { get; set; }

    partial void OnOverrideBeforeBitmapChanged(Bitmap? value) => NotifyCompare();

    /// <summary>Notice over the image during long operations (for example "AI · 42 % · 20 s left"); null = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial string? ActivityText { get; set; }

    /// <summary>Progress of the long operation, 0…100; −1 = no progress bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial double ActivityProgress { get; set; } = -1;

    /// <summary>Cancels the long operation (for example AI); null = cannot be cancelled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay), nameof(OverlayText), nameof(OverlayHasProgress), nameof(OverlayProgress), nameof(OverlayCanCancel))]
    public partial System.Windows.Input.ICommand? ActivityCancel { get; set; }

    // One indicator for everything that computes, at the bottom centre of the preview: long operations (AI, baking
    // settings into the image) take precedence, otherwise the preview computation as soon as it takes noticeable time.
    public bool ShowOverlay => ActivityText is not null || ShowRendering;
    public string? OverlayText => ActivityText ?? PreviewStep;
    public bool OverlayHasProgress => ActivityText is not null ? ActivityProgress >= 0 : ShowRendering;
    public double OverlayProgress => ActivityText is not null ? ActivityProgress : PreviewFraction * 100;
    public bool OverlayCanCancel => ActivityText is not null && ActivityCancel is not null;

    /// <summary>Displayed result (the "After" side when comparing).</summary>
    public Bitmap? DisplayBitmap => OverrideBitmap ?? AdjustedBitmap ?? FullBitmap;
    public bool HasImage => FullBitmap is not null;
    public bool HasAdjustments => AdjustedBitmap is not null;

    // ------------------------------------------------------------ Sliders

    [ObservableProperty] public partial double SharpenAmount { get; set; }
    [ObservableProperty] public partial double SharpenRadius { get; set; } = 1.0;
    /// <summary>Luminance noise reduction 0…1 (non-local means, strength relative to the measured noise).</summary>
    [ObservableProperty] public partial double NoiseReduction { get; set; }

    /// <summary>Colour noise reduction 0…1 (luminance-guided filter on the colour channels).</summary>
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

    // ------------------------------------------------------------ Display

    // Display bitmaps of the latest image states (full resolution), so that undo/redo
    // of a crop or AI step only swaps bitmaps instead of converting again.
    private const long DisplayCacheBytes = 400L * 1024 * 1024;
    private readonly List<(MagickImage Doc, Bitmap Bitmap, long Bytes)> _displayCache = [];
    private string? _shownPath;

    /// <summary>New image state: prepares the full resolution for display (or takes it from the cache).</summary>
    private async Task SetDocumentAsync(MagickImage doc)
    {
        var w = (int)doc.Width;
        var h = (int)doc.Height;

        var bitmap = await DisplayBitmapForAsync(doc);

        // Same image, same size (undo of an AI step …): zoom and visible area
        // stay, so that before/after can be compared at the same spot.
        var keepView = _shownPath == CurrentPath && DocumentSize == new PixelSize(w, h);
        _shownPath = CurrentPath;

        // Other image or other size: the preview of the previous state no longer fits.
        // Show the new image immediately; the preview chain recomputes it right away. Otherwise
        // the previous image would stay visible while browsing for as long as the new preview takes.
        if (!keepView) SetAdjusted(null);

        FullBitmap = bitmap;
        TrimDisplayCache();
        DocumentSize = new PixelSize(w, h);
        if (!keepView) ViewResetToken = new object();

        // "Before" belongs to the image state: determine it again
        BeforeBitmap = null;
        NotifyCompare();
        if (ShowOriginal) _ = LoadBeforeAsync();
    }

    /// <summary>Display bitmap of an image state, from the cache or newly converted.</summary>
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

    /// <summary>Releases the oldest entries once too much memory is in use; never the displayed or the "Before" image.</summary>
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

    // ------------------------------------------------------------ Computation

    private sealed record PreviewJob(
        MagickImage Doc, int Width, int Height,
        ResampleFilter Filter, bool Gamma,
        (double Luma, double Chroma) Noise, Adjustments Adjust, double SharpenAmount, double SharpenSigma, double Scale,
        GrainSettings? Grain, MagickImage? Upscaled = null,   // upscaling: the ×4 image as the basis for enlarging
        bool Coarse = false);                                // coarse while dragging; fine follows

    private sealed record PreviewResult(byte[] Bgra, int Width, int Height);

    private Task _renderTask = Task.CompletedTask;
    private bool _renderRunning;
    private bool _renderDirty;
    private CancellationTokenSource? _renderCts;

    // Stage caches, used only in the render task, never concurrently.
    // A few results each, so that undo/redo between states does not recompute.
    // Image stages in 32-bit float are four times as large as in 8 bits (~30 MB per stage at 2048 px):
    // four scaled images, two of each pixel stage, three of the prepared stage (dehaze + shadows/highlights, the most expensive).
    private const int StageCacheSize = 4;
    private const int PixelCacheSize = 2;
    private const int PreparedCacheSize = 3;
    private readonly List<(ResizeKey Key, MagickImage Image)> _resizedCache = [];
    private readonly List<(ResizeKey Key, (double Luma, double Chroma) Noise, VisibleArea? Area, float[] Pixels)> _denoisedCache = [];
    // White balance + dehaze separately: the most expensive part, depends only on DehazeKey; shadows/highlights and
    // the region maps come afterwards and do not recompute it.
    private readonly List<(ResizeKey Key, (double Luma, double Chroma) Noise, Adjustments DehazeKey, float[] Pixels)> _dehazedCache = [];
    // Dehaze + shadows/highlights (+ region maps): depends only on PrepareKey, so the other sliders stay fluid.
    private readonly List<(ResizeKey Key, (double Luma, double Chroma) Noise, Adjustments PrepareKey, Adjust.Prepared Prepared)> _tonedCache = [];
    private readonly List<(ResizeKey Key, (double Luma, double Chroma) Noise, Adjustments Adjust, float[] Pixels)> _adjustedCache = [];

    private readonly record struct ResizeKey(MagickImage Doc, int W, int H, ResampleFilter F, bool G, MagickImage? Up = null);

    /// <summary>
    /// Requests a new preview. If a computation is already running, it is followed by exactly
    /// one recomputation with the values current at that time ("the last one wins").
    /// </summary>
    private void RequestPreview()
    {
        // Changes in quick succession (not the same action): the user is dragging.
        var now = Environment.TickCount64;
        if (now - _lastRequestTicks >= SameActionMs) _rapidChanges = now - _lastRequestTicks < RapidMs;
        _lastRequestTicks = now;
        RequestPreviewNow();
    }

    /// <summary>Mouse button pressed/released in the UI (set by the view). On release the fine preview follows.</summary>
    /// <param name="down">true when pressed, false when released.</param>
    public void SetPointerDown(bool down)
    {
        if (_pointerDown == down) return;
        _pointerDown = down;
        if (down) return;
        _rapidChanges = false;
        if (_shownCoarse || _renderRunning) RequestPreviewNow();
        else RequestEstimate();   // the estimate has been waiting for the release
    }

    /// <summary>Whether to compute coarsely now (slider dragged, rapid changes).</summary>
    private bool Interactive => CoarseWhileDragging && (_pointerDown || _rapidChanges);

    /// <summary>After a coarse preview without a pressed button: the fine one after a short quiet period.</summary>
    private async Task FineAfterQuietAsync(long requestTicks)
    {
        await Task.Delay(RapidMs + 20);
        if (_lastRequestTicks != requestTicks || _pointerDown || !_shownCoarse) return;
        _rapidChanges = false;
        RequestPreviewNow();
    }

    private void RequestPreviewNow()
    {
        RequestEstimate();   // size, sharpness, noise … also change the file size
        UpdateHighBitDepthRecommendation();   // the 16-bit recommendation follows the sliders (and removes a tick the app set itself when it lapses)
        if (_renderRunning)
        {
            _renderDirty = true;
            // Cancel the running computation between two steps, because the slider keeps moving. Not while dragging:
            // the coarse preview finishes (otherwise slow stages such as dehaze would show no preview at all until the
            // movement pauses), then the latest state follows.
            if (!Interactive) _renderCts?.Cancel();
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
                    _shownCoarse = false;
                    continue;
                }

                var cts = _renderCts = new CancellationTokenSource();   // without a timer, Dispose is not needed
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
                    continue;   // newer settings are already waiting (_renderDirty)
                }
                // Another image was selected in the meantime: discard the result; the loop
                // recomputes anyway (RequestPreview has set _renderDirty).
                if (!ReferenceEquals(job.Doc, ShownDocument)) continue;
                SetAdjusted(BitmapOps.FromBgra(result.Bgra, result.Width, result.Height));
                _shownCoarse = job.Coarse;
                if (job.Coarse && !_pointerDown && !_renderDirty) _ = FineAfterQuietAsync(_lastRequestTicks);
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

    /// <summary>Cancels and awaits a running preview computation and size estimate, and clears the estimate caches.</summary>
    private async Task StopPreviewAsync()
    {
        _renderDirty = false;
        _renderCts?.Cancel();
        try { await _renderTask; } catch { /* the loop reports errors itself */ }

        // A running size estimate also still reads the image.
        _estimateCts?.Cancel();
        try { await _estimateTask; } catch { /* cancelled */ }
        _estimateCache.Clear();
        _estimateBase?.Dispose();
        _estimateBase = null;
        _estimateBaseKey = null;

    }

    /// <summary>Clears the stage caches (images closed).</summary>
    private void ClearPreviewCaches()
    {
        foreach (var (_, image) in _resizedCache) image.Dispose();
        _resizedCache.Clear();
        _denoisedCache.Clear();
        _adjustedCache.Clear();
        _tonedCache.Clear();
        _dehazedCache.Clear();
    }

    /// <summary>Snapshot of the settings (UI thread). null = show the image without the chain.</summary>
    private PreviewJob? CreateJob()
    {
        if (ShownDocument is not { } doc) return null;

        var (tw, th) = EffectiveTarget();
        var resize = tw != (int)doc.Width || th != (int)doc.Height;
        var noise = (EffectiveNoise, EffectiveColorNoise);
        var sharpen = EffectiveSharpen;
        // "Show mask" applies only here in the preview (not in the export, not in the size estimate).
        var adjust = CurrentAdjustments() is var a && ShowMask && Shows(EditSteps.LumMask) ? a with { MaskPreview = true } : CurrentAdjustments();
        var grain = EffectiveGrain;
        if (!resize && noise == (0, 0) && sharpen == 0 && adjust.IsNone && grain is null) return null;
        var upscaled = ToolView == GeometryStages.All ? UpscaledForPreview() : null;   // not in the view of a tool

        // Preview at most PreviewMaxEdge in size (PreviewDragEdge while dragging); the sharpening radius is
        // scaled along so that its effect relative to the result stays the same.
        var coarse = Interactive && Math.Max(tw, th) > PreviewDragEdge;
        var scale = Math.Min(1.0, (double)(coarse ? PreviewDragEdge : PreviewMaxEdge) / Math.Max(tw, th));
        var pw = Math.Max(1, (int)Math.Round(tw * scale));
        var ph = Math.Max(1, (int)Math.Round(th * scale));

        return new PreviewJob(doc, pw, ph, SelectedFilter.Filter, GammaCorrect,
            noise, adjust, sharpen, SharpenRadius * scale, scale, grain, upscaled, coarse);
    }

    /// <summary>Background thread: the chain stages, each recomputed only when necessary.</summary>
    private PreviewResult Render(PreviewJob job, WorkPlan plan)
    {
        // 1) Scale: expensive, recomputed only on a change of size/filter/gamma
        var key = new ResizeKey(job.Doc, job.Width, job.Height, job.Filter, job.Gamma, job.Upscaled);
        var resized = Cached(_resizedCache, e => e.Key == key)?.Image;
        if (resized is null)
        {
            resized = ImageOps.Resize(job.Upscaled ?? job.Doc, job.Width, job.Height, job.Filter, job.Gamma);
            AddCached(_resizedCache, (Key: key, Image: resized), e => e.Image.Dispose());
        }

        // 2) Denoise: medium cost, recomputed only when its slider changes
        // A transparent image: only the rectangle around its visible pixels, as in the export (VisibleArea)
        var stored = Cached(_denoisedCache, e => e.Key == key && e.Noise == job.Noise);
        var denoised = stored?.Pixels;
        var area = stored?.Area;
        if (denoised is null)
        {
            int docW, docH;
            bool docAlpha;
            lock (job.Doc) (docW, docH, docAlpha) = ((int)job.Doc.Width, (int)job.Doc.Height, job.Doc.HasAlpha);
            if (job.Noise != (0, 0) && job.Upscaled is null && (long)job.Width * job.Height > (long)docW * docH)
            {
                // Enlarging: denoise first, then enlarge, as in the export (ImageExporter.NoiseFirst)
                plan.Add(Tr.T("Rauschen entfernen"), WorkPlan.DenoiseCost * docW * docH / 1e6);
                var source = ImageOps.ToDisplayPixels(job.Doc);
                var clean = VisibleArea.Apply(source, docW, docH, (px, w, h) => Denoiser.Apply(px, w, h, job.Noise.Luma, job.Noise.Chroma, 1, plan));
                PixelPool.Return(source);
                using var cleanImage = Pixels.ToMagick16(clean, docW, docH, docAlpha);
                PixelPool.Return(clean);
                using var enlarged = ImageOps.Resize(cleanImage, job.Width, job.Height, job.Filter, job.Gamma);
                denoised = ImageOps.ToDisplayPixels(enlarged);
            }
            else
            {
                denoised = ImageOps.ToDisplayPixels(resized);
            }
            area = VisibleArea.Of(denoised, job.Width, job.Height);
            if (area is { } found)
            {
                var cut = found.Cut(denoised, job.Width);
                PixelPool.Return(denoised);
                denoised = cut;
            }
            if (job.Noise != (0, 0) && (job.Upscaled is not null || (long)job.Width * job.Height <= (long)docW * docH))
            {
                // Luminance and colour separately; radii scaled to the preview as in the export
                var (nw, nh) = area is { } a ? (a.Width, a.Height) : (job.Width, job.Height);
                plan.Add(Tr.T("Rauschen entfernen"), WorkPlan.DenoiseCost * nw * nh / 1e6);
                var clean = Denoiser.Apply(denoised, nw, nh, job.Noise.Luma, job.Noise.Chroma, job.Scale, plan);
                PixelPool.Return(denoised);
                denoised = clean;
            }
            AddCached(_denoisedCache, (Key: key, Noise: job.Noise, Area: area, Pixels: denoised), null, PixelCacheSize);
        }
        var (cw, ch) = area is { } rect ? (rect.Width, rect.Height) : (job.Width, job.Height);

        // 3) Adjustments (light, colour, dehaze): recomputed only when their sliders change
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
                    // Dehaze per pass (only if not computed already) … shadows/highlights … light and colour
                    Adjust.PlanSteps(j, plan, (double)cw * ch / 1e6, withDehaze: dehazed is null);
                    if (dehazed is null)
                    {
                        // While dragging, dehaze estimates on a smaller version (a quarter of the work); otherwise as usual
                        dehazed = Adjust.Dehazed(denoised, cw, ch, j, plan, job.Coarse ? CoarseDehazeEdge : 0);
                        AddCached(_dehazedCache, (key, job.Noise, dehazeKey, dehazed), null, PixelCacheSize);
                    }
                    prepared = Adjust.Finish(dehazed, cw, ch, j, plan);
                    AddCached(_tonedCache, (key, job.Noise, prepareKey, prepared), null, PreparedCacheSize);
                }
                else
                {
                    plan.Add(Tr.T("Licht und Farbe"), 1);
                }
                adjusted = Adjust.Apply(prepared, cw, ch, j, plan);
                AddCached(_adjustedCache, (Key: key, Noise: job.Noise, Adjust: job.Adjust, Pixels: adjusted), null, PixelCacheSize);
            }
        }

        // 4) Sharpen: fast, on every slider movement
        var final = job.SharpenAmount > 0
            ? Sharpen.UnsharpMask(adjusted, cw, ch, job.SharpenAmount, job.SharpenSigma)
            : adjusted;

        // 5) Grain: always last; in the reduced preview as it would look in a reduced export
        if (job.Grain is { } grain)
        {
            plan.Add(Tr.T("Körnung"), WorkPlan.GrainCost * cw * ch / 1e6);
            plan.Next();
            var grainy = Grain.Apply(final, cw, ch, grain, job.Scale);
            if (!ReferenceEquals(final, adjusted)) PixelPool.Return(final);
            final = grainy;
        }

        // Round once, with dithering: only here does the 32-bit data become the 8-bit display
        var shown = final;
        if (area is { } visible)
        {
            // Nothing is visible around the rectangle
            shown = PixelPool.Rent(job.Width * job.Height * 4);
            Array.Clear(shown);
            visible.Paste(final, shown, job.Width);
        }
        var bgra = Pixels.ToBytes(shown, dither: true);
        if (!ReferenceEquals(shown, final)) PixelPool.Return(shown);
        if (job.Grain is not null) PixelPool.Return(final);
        // The intermediate buffers of this computation (dehaze passes …) must not stay allocated.
        PixelPool.Trim();
        return new PreviewResult(bgra, job.Width, job.Height);
    }

    /// <summary>Moves a hit to the front (the most recently used entry stays longest).</summary>
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
