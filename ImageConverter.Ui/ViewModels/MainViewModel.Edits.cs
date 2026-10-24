using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

/// <summary>Functions that can be switched off (eye icon in the "Edits" list, as in Referenzwerkzeug).</summary>
[Flags]
public enum EditSteps
{
    None = 0,
    Ai = 1,
    Resize = 2,
    Denoise = 4,
    Dehaze = 8,
    Light = 16,
    Color = 32,
    MatchColor = 64,
    Sharpen = 128,
    SkinDetails = 256,
    ColorNoise = 512,
    WhiteBalance = 1024,
    Retouch = 2048,
    // Geometry (recipe, GeometryRecipe): one row per stage
    Straighten = 4096,
    Level = 8192,
    CropStep = 16384,
    Orient = 32768,
    LumMask = 65536,
    Grain = 131072,
    Dewarp = 262144,
}

/// <summary>Kind of an image step in the history (changes the image or its geometry, not only settings).</summary>
public enum ImageStepKind { Crop, Rotate, Flip, Ai, Applied, Retouch }

/// <summary>One row of the "Edits" list: a function that acts on the image.</summary>
public partial class EditRow(MainViewModel owner, EditSteps step, string name, string detail, bool canHide, string? note)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Detail { get; } = detail;

    /// <summary>The function can be switched off (not, for example, if the AI has enlarged the image).</summary>
    public bool CanHide { get; } = canHide;

    public string? Note { get; } = note;

    /// <summary>Pencil next to the row: reopens the edit (Curved page: adjust the lines in use).</summary>
    public CommunityToolkit.Mvvm.Input.IRelayCommand? Edit { get; init; }

    public bool CanEdit => Edit is not null;

    /// <summary>Eye open: the function acts in "After" and in the export.</summary>
    public bool Shown
    {
        get => (owner.HiddenSteps & step) == 0;
        set
        {
            if (value == Shown) return;
            owner.HiddenSteps = value ? owner.HiddenSteps & ~step : owner.HiddenSteps | step;
        }
    }
}

// "Edits" list: one row per function (AI, Skin & details, removal, each geometry stage, size, noise, white balance,
// dehaze, match colour, light, colour, sharpness, grain …), not one per slider movement. The eye switches a function
// off for "After" and the export; "Apply selection" makes the selection permanent.
public partial class MainViewModel
{
    /// <summary>An image step in the history: what it was and how it can be repeated on another image.</summary>
    /// <param name="Kind">Kind of the step.</param>
    /// <param name="Label">Name in the "Edits" list.</param>
    /// <param name="Detail">Short info for the "Edits" list; null for none.</param>
    /// <param name="Replay">The same step applied to another pixel image; null if the step cannot be repeated. All steps
    /// created at present pass null, because rotation and crop are kept in the recipe instead.</param>
    /// <param name="SkinDetail">AI with "Skin &amp; details": short info on it; null for none.</param>
    internal sealed record ImageStep(ImageStepKind Kind, string Label, string? Detail,
        Func<MagickImage, MagickImage>? Replay, string? SkinDetail = null)
    {
        /// <summary>The same AI result without "Skin &amp; details" (only if there were any), used to switch them off.</summary>
        public MagickImage? Plain { get; set; }

        /// <summary>Removal: a new attempt from the image before the last removal; replaces that step in the list.</summary>
        public bool Replaces { get; init; }
    }

    /// <summary>Switched-off functions; part of the settings state (per image, undo, export).</summary>
    [ObservableProperty] public partial EditSteps HiddenSteps { get; set; }

    /// <summary>Shows the "Edits" list above the preview.</summary>
    [ObservableProperty] public partial bool ShowEditList { get; set; }

    public ObservableCollection<EditRow> EditRows { get; } = [];

    public bool HasEditRows => EditRows.Count > 0;

    private bool Shows(EditSteps step) => (HiddenSteps & step) == 0;

    private const EditSteps ImageVariants = EditSteps.Ai | EditSteps.SkinDetails;

    /// <summary>The geometry stages in the list.</summary>
    internal const EditSteps GeometrySteps = EditSteps.Dewarp | EditSteps.Straighten | EditSteps.Level | EditSteps.CropStep | EditSteps.Orient;

    partial void OnHiddenStepsChanged(EditSteps oldValue, EditSteps newValue)
    {
        ApplySelectionCommand.NotifyCanExecuteChanged();
        RefreshEditRows();
        var changed = oldValue ^ newValue;
        if ((changed & GeometrySteps) != 0)
            _ = ShowDocumentAsync(sizeChanges: true);   // geometry on/off: different dimensions
        else if ((changed & ImageVariants) != 0)
            _ = ShowDocumentAsync();   // AI or Skin & details on/off: show a different image
        else
            RequestPreview();
        NotifySizeDerived();
    }

    /// <summary>Returns the geometry stages that are switched on.</summary>
    internal static GeometryStages StagesShown(EditSteps hidden) =>
        GeometryStages.All
        & ~((hidden & EditSteps.Dewarp) != 0 ? GeometryStages.Dewarp : 0)
        & ~((hidden & EditSteps.Straighten) != 0 ? GeometryStages.Straighten : 0)
        & ~((hidden & EditSteps.Level) != 0 ? GeometryStages.Level : 0)
        & ~((hidden & EditSteps.CropStep) != 0 ? GeometryStages.Crop : 0)
        & ~((hidden & EditSteps.Orient) != 0 ? GeometryStages.Orient : 0);

    /// <summary>Returns the adjustments without the switched-off functions.</summary>
    internal static Adjustments Mask(Adjustments a, EditSteps hidden)
    {
        if ((hidden & EditSteps.Dehaze) != 0) a = a with { Dehaze = 0 };
        if ((hidden & EditSteps.Light) != 0) a = a with { Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0 };
        if ((hidden & EditSteps.Color) != 0) a = a with { Saturation = 0, Vibrance = 0, Tint = Tint.None };
        if ((hidden & EditSteps.MatchColor) != 0) a = a with { MatchLuminance = 0, MatchLimit = 100, MatchTarget = 0, MatchGlobal = 0, Neutralize = 0, Warmth = 0 };
        if ((hidden & EditSteps.WhiteBalance) != 0) a = a with { WbNeutral = null, WbBlack = null, WbWhite = null };
        if ((hidden & EditSteps.LumMask) != 0) a = a with { MaskAmount = 0, MaskPreview = false };
        return a;
    }

    private double EffectiveNoise => Shows(EditSteps.Denoise) && NoiseReduction > 0.005 ? NoiseReduction : 0;
    private double EffectiveColorNoise => Shows(EditSteps.ColorNoise) && ColorNoiseReduction > 0.005 ? ColorNoiseReduction : 0;
    private double EffectiveSharpen => Shows(EditSteps.Sharpen) && SharpenAmount > 0.01 ? SharpenAmount : 0;

    // ------------------------------------------------------------ Image steps

    /// <summary>The image steps of a file that led to the current state (oldest first).</summary>
    private List<Snapshot> ImageStepsOf(string path) =>
        _undo.Where(s => s.FilePath == path && s.Image is not null && s.Step is not null).Reverse().ToList();

    /// <summary>
    /// Index of the first AI step that still counts: after the last "Selection applied" step (which has already taken an
    /// AI out of the image); −1 if there is none.
    /// </summary>
    private static int FirstLiveAi(List<Snapshot> steps)
    {
        var applied = steps.FindLastIndex(s => s.Step!.Kind == ImageStepKind.Applied);
        return steps.FindIndex(applied + 1, s => s.Step!.Kind == ImageStepKind.Ai);
    }

    /// <summary>The image after step <paramref name="i"/> (the image before the next step, or the current one).</summary>
    private static MagickImage After(List<Snapshot> steps, int i, EditHistory h) =>
        i + 1 < steps.Count ? steps[i + 1].Image! : h.Current;

    /// <summary>Determines whether every step from <paramref name="from"/> on is an AI step or can be repeated.</summary>
    private static bool Replayable(List<Snapshot> steps, int from) =>
        steps.Skip(from).All(s => s.Step!.Kind == ImageStepKind.Ai || s.Step.Replay is not null);

    /// <summary>
    /// Determines whether the AI can be switched off: only if it kept the image size and every later image step can be
    /// repeated (see <see cref="Replayable"/>); the image without AI is then built from the state before plus the later
    /// steps. Geometry is not affected, because it is kept in the recipe.
    /// </summary>
    private bool CanHideAi(string path, EditHistory h, out string? reason)
    {
        reason = null;
        var steps = ImageStepsOf(path);
        var first = FirstLiveAi(steps);
        if (first < 0) return false;

        for (var i = first; i < steps.Count; i++)
        {
            if (steps[i].Step!.Kind == ImageStepKind.Ai && !SameSize(steps[i].Image!, After(steps, i, h)))
            {
                reason = Tr.T("Vergrößert — lässt sich nicht ausschalten, nur mit Rückgängig zurücknehmen.");
                return false;
            }
        }
        if (!Replayable(steps, first))
        {
            reason = Tr.T("Danach wurde das Bild festgeschrieben — nur mit Rückgängig zurückzunehmen.");
            return false;
        }
        return true;
    }

    /// <summary>Determines whether the last AI step has "Skin &amp; details" that can be switched off.</summary>
    private bool CanHideSkin(string path, EditHistory h)
    {
        var steps = ImageStepsOf(path);
        if (FirstLiveAi(steps) < 0) return false;
        var last = steps.FindLastIndex(s => s.Step!.Kind == ImageStepKind.Ai);
        return steps[last].Step!.Plain is { } plain && SameSize(plain, After(steps, last, h)) && Replayable(steps, last + 1);
    }

    private static bool SameSize(MagickImage a, MagickImage b)
    {
        lock (a) lock (b) return a.Width == b.Width && a.Height == b.Height;
    }

    // Image without AI or without Skin & details, computed once per file, variant and current image
    private readonly Dictionary<(string Path, EditSteps Variant), (MagickImage Current, MagickImage Image)> _variants = [];

    /// <summary>
    /// The pixel image that is displayed and exported: the current one, or with AI switched off the image before the
    /// first AI step, or with "Skin &amp; details" switched off the last AI result without them; later repeatable steps
    /// are applied again in each case. Geometry is applied afterwards from the recipe.
    /// </summary>
    private MagickImage PixelsFor(string path, EditHistory h, EditSteps hidden)
    {
        var variant = (hidden & EditSteps.Ai) != 0 && CanHideAi(path, h, out _) ? EditSteps.Ai
            : (hidden & EditSteps.SkinDetails) != 0 && CanHideSkin(path, h) ? EditSteps.SkinDetails
            : EditSteps.None;
        if (variant == EditSteps.None) return h.Current;
        if (_variants.TryGetValue((path, variant), out var cached) && ReferenceEquals(cached.Current, h.Current))
            return cached.Image;

        var steps = ImageStepsOf(path);
        int from;
        MagickImage doc;
        if (variant == EditSteps.Ai)
        {
            var first = FirstLiveAi(steps);
            (doc, from) = (steps[first].Image!, first + 1);
        }
        else
        {
            var last = steps.FindLastIndex(s => s.Step!.Kind == ImageStepKind.Ai);
            (doc, from) = (steps[last].Step!.Plain!, last + 1);
        }

        var owned = false;
        foreach (var step in steps.Skip(from).Select(s => s.Step!).Where(s => s.Kind != ImageStepKind.Ai))
        {
            var next = step.Replay!(doc);
            if (owned) doc.Dispose();
            (doc, owned) = (next, true);
        }

        // The previous variant is no longer needed (the image has changed and the preview is stopped).
        if (_variants.TryGetValue((path, variant), out var old) && !ReferenceEquals(old.Image, doc) && !IsHeldByHistory(old.Image))
            old.Image.Dispose();
        _variants[(path, variant)] = (h.Current, doc);
        return doc;
    }

    // Pixel image + recipe: the latest variants per file (tool views, eyes on/off)
    private readonly Dictionary<string, List<(MagickImage Pixels, GeometryRecipe Recipe, GeometryStages Stages, MagickImage Image)>> _geometryDocs = [];
    private readonly List<MagickImage> _geometryTrash = [];
    // Up to ~230 MB per variant at 28 MP: at most three per image; those of other images are released on switching.
    private const int GeometryDocsPerFile = 3;

    /// <summary>
    /// The image as it is displayed and saved: the pixel image (with or without AI …, <see cref="PixelsFor"/>) plus
    /// geometry (<see cref="GeometryRecipe"/>) with the stages switched on. <paramref name="view"/> restricts the
    /// stages further for the view of a tool (free rotation, perspective, curved page).
    /// </summary>
    private MagickImage DocumentFor(string path, EditHistory h, EditSteps hidden, GeometryStages view = GeometryStages.All) =>
        WithGeometry(path, h.Recipe, PixelsFor(path, h, hidden), StagesShown(hidden) & view);

    /// <summary>A pixel image with the recipe applied (cached per file); also used for the "Before" side of the comparison.</summary>
    private MagickImage WithGeometry(string path, GeometryRecipe recipe, MagickImage pixels, GeometryStages stages)
    {
        if ((recipe.Present & stages) == 0) return pixels;

        lock (_geometryDocs)
        {
            if (_geometryDocs.TryGetValue(path, out var list)
                && list.FindIndex(e => ReferenceEquals(e.Pixels, pixels) && e.Recipe == recipe && e.Stages == stages) is var i and >= 0)
                return list[i].Image;
        }

        var doc = recipe.Apply(pixels, stages) ?? pixels;
        lock (_geometryDocs)
        {
            if (!_geometryDocs.TryGetValue(path, out var list)) _geometryDocs[path] = list = [];
            if (list.FindIndex(e => ReferenceEquals(e.Pixels, pixels) && e.Recipe == recipe && e.Stages == stages) is var i and >= 0)
            {
                // already computed concurrently (preview and display): keep one variant
                if (!ReferenceEquals(doc, pixels)) _geometryTrash.Add(doc);
                return list[i].Image;
            }
            list.Insert(0, (pixels, recipe, stages, doc));
            while (list.Count > GeometryDocsPerFile)
            {
                // Released only once no preview is computing on it any more (EmptyGeometryTrash).
                if (!ReferenceEquals(list[^1].Image, list[^1].Pixels)) _geometryTrash.Add(list[^1].Image);
                list.RemoveAt(list.Count - 1);
            }
        }
        return doc;
    }

    /// <summary>Releases evicted variants; call only while the preview is stopped.</summary>
    private void EmptyGeometryTrash()
    {
        lock (_geometryDocs)
        {
            var inUse = _geometryDocs.Values.SelectMany(l => l.Select(e => e.Image)).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var img in _geometryTrash.Where(i => !inUse.Contains(i) && !IsHeldByHistory(i))) img.Dispose();
            _geometryTrash.Clear();
        }
    }

    /// <summary>Discards the variants of other images (on switching images); an export computes them again.</summary>
    private void DropGeometryDocsExcept(string? path)
    {
        lock (_geometryDocs)
        {
            foreach (var other in _geometryDocs.Keys.Where(k => k != path).ToList())
            {
                _geometryTrash.AddRange(_geometryDocs[other].Where(e => !ReferenceEquals(e.Image, e.Pixels)).Select(e => e.Image));
                _geometryDocs.Remove(other);
            }
        }
    }

    /// <summary>Releases all variants with geometry (images closed).</summary>
    private void ClearGeometryDocs()
    {
        lock (_geometryDocs)
        {
            foreach (var e in _geometryDocs.Values.SelectMany(l => l))
                if (!ReferenceEquals(e.Image, e.Pixels) && !IsHeldByHistory(e.Image)) e.Image.Dispose();
            _geometryDocs.Clear();
            foreach (var img in _geometryTrash.Where(i => !IsHeldByHistory(i))) img.Dispose();
            _geometryTrash.Clear();
        }
    }

    /// <summary>Stages shown by an open tool: free rotation without level/crop, perspective with only page flattening and rotate/flip, curved page with only rotate/flip.</summary>
    private GeometryStages ToolView =>
        RotationTool ? GeometryRecipe.LevelView : PerspectiveTool ? GeometryRecipe.StraightenView
        : DewarpTool && !DewarpShowsResult ? GeometryRecipe.DewarpView : GeometryStages.All;

    /// <summary>The image currently displayed: with or without AI …, with the stages switched on, in the tool's view while a tool is open.</summary>
    private MagickImage? ShownDocument =>
        _history is { } h && CurrentPath is { } path ? DocumentFor(path, h, HiddenSteps, ToolView) : null;

    /// <summary>The finished image (for export, size, printing); never the view of a tool.</summary>
    private MagickImage? ResultDocument =>
        _history is { } h && CurrentPath is { } path ? DocumentFor(path, h, HiddenSteps) : null;

    /// <summary>Size of the pixel image (before geometry).</summary>
    private (int Width, int Height) PixelSize()
    {
        if (_history is not { } h) return (0, 0);
        lock (h.Current) return ((int)h.Current.Width, (int)h.Current.Height);
    }

    /// <summary>Maps a point in the displayed image to the pixel image (eyedropper, removal brush).</summary>
    internal (double X, double Y) ShownToPixels(double x, double y)
    {
        if (_history is not { } h) return (x, y);
        var (w, ht) = PixelSize();
        return h.Recipe.ToSource(x, y, w, ht, StagesShown(HiddenSteps) & ToolView);
    }

    /// <summary>Length of a distance <paramref name="r"/> around (x, y) in the pixel image (the brush radius is distorted by perspective correction).</summary>
    internal double ShownLengthToPixels(double x, double y, double r)
    {
        var (cx, cy) = ShownToPixels(x, y);
        var (ax, ay) = ShownToPixels(x + r, y);
        var (bx, by) = ShownToPixels(x, y + r);
        return (Math.Sqrt((ax - cx) * (ax - cx) + (ay - cy) * (ay - cy)) + Math.Sqrt((bx - cx) * (bx - cx) + (by - cy) * (by - cy))) / 2;
    }

    // ------------------------------------------------------------ AI and geometry
    //
    // The AI receives the pixel image only rotated/flipped, so that faces are upright as they are seen (face detection
    // finds faces lying on their side poorly). The result is rotated back exactly before it enters the pixel image;
    // intermediate results and the preview are rotated back for display and shown with the recipe.

    /// <summary>Quarter turns and flip that the AI applies to the pixel image (only if rotate/flip is switched on).</summary>
    internal (int Turns, bool Flip) AiOrientation =>
        (CurrentStages & GeometryStages.Orient) != 0 ? (CurrentRecipe.NormalTurns, CurrentRecipe.Flip) : (0, false);

    /// <summary>Returns the image rotated/flipped; null if there is nothing to do.</summary>
    internal static MagickImage? Orient(MagickImage image, int turns, bool flip) =>
        new GeometryRecipe(Turns: turns, Flip: flip).Apply(image, GeometryStages.Orient);

    /// <summary>Inverse of <see cref="Orient"/> (a flip combined with a rotation is its own inverse).</summary>
    internal static MagickImage? Unorient(MagickImage image, int turns, bool flip) =>
        flip ? Orient(image, turns, true) : Orient(image, (4 - turns) % 4, false);

    /// <summary>Recipe and stages as the image is currently displayed (read on the UI thread, then use in the background).</summary>
    internal (GeometryRecipe Recipe, GeometryStages Stages) ShownGeometry => (CurrentRecipe, CurrentStages & ToolView);

    /// <summary>Converts AI preview pixels (rotated pixel image, reduced) to the way the image is displayed.</summary>
    internal static Core.Ai.AiPixels ShowWithGeometry(Core.Ai.AiPixels frame, int turns, bool flip, GeometryRecipe recipe, GeometryStages stages)
    {
        if ((recipe.Present & stages) == 0 && turns == 0 && !flip) return frame;
        using var image = Pixels.ToMagick16(frame.Bgra, frame.Width, frame.Height, alpha: true);
        using var upright = Unorient(image, turns, flip);
        var source = upright ?? image;
        using var shaped = recipe.Apply(source, stages);
        var result = shaped ?? source;
        return new Core.Ai.AiPixels(Pixels.FromMagick(result), (int)result.Width, (int)result.Height);
    }

    /// <summary>Like <see cref="ShowWithGeometry(Core.Ai.AiPixels, int, bool, GeometryRecipe, GeometryStages)"/>, for 8-bit intermediate results.</summary>
    internal static Core.Ai.AiPreviewFrame ShowWithGeometry(Core.Ai.AiPreviewFrame frame, int turns, bool flip, GeometryRecipe recipe, GeometryStages stages)
    {
        if ((recipe.Present & stages) == 0 && turns == 0 && !flip) return frame;
        var shown = ShowWithGeometry(new Core.Ai.AiPixels(Pixels.FromBytes(frame.Bgra), frame.Width, frame.Height), turns, flip, recipe, stages);
        return new Core.Ai.AiPreviewFrame(Pixels.ToBytes(shown.Bgra, dither: true), shown.Width, shown.Height);
    }

    /// <summary>The geometry of the current image (the AI uses only rotate/flip from it, so that faces are upright).</summary>
    internal GeometryRecipe CurrentRecipe => _history?.Recipe ?? GeometryRecipe.None;

    /// <summary>Stages of the current image that are switched on (without a tool view).</summary>
    internal GeometryStages CurrentStages => StagesShown(HiddenSteps);

    /// <summary>
    /// The current pixel image without AI (later repeatable steps applied again); the source for a new AI run, so that
    /// AI is not stacked on AI. null if there is no AI in the image or it cannot be separated.
    /// </summary>
    internal MagickImage? CurrentWithoutAi =>
        _history is { } h && CurrentPath is { } path && CanHideAi(path, h, out _) ? PixelsFor(path, h, EditSteps.Ai) : null;

    /// <summary>What is switched off for a file: its own state, otherwise the shared state or the template.</summary>
    internal EditSteps HiddenFor(string path)
    {
        if (path == CurrentPath || ApplyToAll) return HiddenSteps;
        if (_fileSettings.TryGetValue(path, out var own)) return own.Hidden;
        return _individualTemplate?.Hidden ?? EditSteps.None;
    }

    /// <summary>Releases all variants without AI or Skin &amp; details (images closed).</summary>
    private void ClearWithoutAi()
    {
        foreach (var (_, entry) in _variants)
            if (!IsHeldByHistory(entry.Image)) entry.Image.Dispose();
        _variants.Clear();
    }

    private bool IsHeldByHistory(MagickImage image) =>
        _histories.Values.Any(h => ReferenceEquals(h.Current, image) || ReferenceEquals(h.Original, image))
        || _undo.Concat(_redo).Any(s => ReferenceEquals(s.Image, image) || ReferenceEquals(s.Step?.Plain, image));

    /// <summary>Sets the displayed image again (AI, Skin &amp; details or a geometry stage on/off, tool opened/closed).</summary>
    /// <param name="sizeChanges">The finished image has different dimensions; size, printing and export follow.</param>
    private async Task ShowDocumentAsync(bool sizeChanges = false)
    {
        if (_history is null) return;
        // In the background: perspective correction of a large image takes seconds.
        var doc = await Task.Run(() => ShownDocument);
        if (doc is null) return;
        await StopPreviewAsync();
        EmptyGeometryTrash();
        await SetDocumentAsync(doc);
        if (sizeChanges && await Task.Run(() => ResultDocument) is { } result)
        {
            int w, ht;
            lock (result) (w, ht) = ((int)result.Width, (int)result.Height);
            using (SuppressHistory()) FollowSourceSize(w, ht);
            UpdateFileDetails();
        }
        RequestPreview();
    }

    // ------------------------------------------------------------ List

    private bool _editRowsRequested;

    /// <summary>Rebuilds the list once per user action (not separately for every intermediate value of a slider).</summary>
    private void RequestEditRows()
    {
        if (_editRowsRequested) return;
        _editRowsRequested = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _editRowsRequested = false;
            RefreshEditRows();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Rebuilds the rows after every setting, every image step and every image switch.</summary>
    private void RefreshEditRows()
    {
        var rows = new List<EditRow>();
        if (_history is { } h && CurrentPath is { } path)
        {
            // AI as one row (later runs replace earlier ones), Skin & details of the last AI as a separate row
            var steps = ImageStepsOf(path);
            if (FirstLiveAi(steps) >= 0)
            {
                var last = steps.Last(s => s.Step!.Kind == ImageStepKind.Ai).Step!;
                var canHideAi = CanHideAi(path, h, out var aiReason);
                rows.Add(new EditRow(this, EditSteps.Ai, last.Label, last.Detail ?? "", canHideAi, aiReason));
                if (last.SkinDetail is { } skin)
                {
                    var canHideSkin = CanHideSkin(path, h);
                    rows.Add(new EditRow(this, EditSteps.SkinDetails, Tr.T("Haut & Details"), skin, canHideSkin,
                        canHideSkin ? null : Tr.T("Lässt sich nicht ausschalten — nur mit Rückgängig zurücknehmen.")));
                }
            }

            // Removal: one row for all spots since the last "Apply selection"
            var applied = steps.FindLastIndex(s => s.Step!.Kind == ImageStepKind.Applied);
            var retouch = new List<ImageStep>();
            foreach (var s in steps.Skip(applied + 1).Select(s => s.Step!).Where(s => s.Kind == ImageStepKind.Retouch))
            {
                if (s.Replaces && retouch.Count > 0) retouch.RemoveAt(retouch.Count - 1);
                retouch.Add(s);
            }
            if (retouch.Count > 0)
                rows.Add(new EditRow(this, EditSteps.Retouch, Tr.T("Entfernt"),
                    retouch.Count == 1 ? retouch[0].Detail ?? "" : Tr.T("{0}× · zuletzt {1}", retouch.Count, retouch[^1].Detail ?? ""),
                    false, Tr.T("Lässt sich nicht ausschalten — nur mit Rückgängig zurücknehmen.")));
            // Geometry: one row per stage, in the fixed order of the recipe (always computed from the pixel image)
            var recipe = h.Recipe;
            var (pw, ph) = PixelSize();
            if (recipe.HasDewarp && recipe.Dewarp is { } spec)
            {
                var map = recipe.DewarpMapFor(pw, ph);
                var (fw, fh) = recipe.FlatSize(pw, ph);
                var lines = spec.UsableLines.Count();
                var parts = new List<string>
                {
                    spec.Grid is not null ? Tr.T("KI (SyntheticDoc)") : lines == 1 ? Tr.T("1 Linie") : Tr.T("{0} Linien", lines),
                    $"{fw} × {fh} px",
                };
                if (map?.Din == true) parts.Add("DIN");
                if (spec.Grid?.Shading is { On: true }) parts.Add(Tr.T("Schatten entfernt"));
                rows.Add(new EditRow(this, EditSteps.Dewarp, spec.Grid is not null ? Tr.T("KI begradigt") : Tr.T("Gerade gezogen"),
                    string.Join(" · ", parts), true, null) { Edit = spec.Grid is null ? EditDewarpLinesCommand : null });
            }
            if (recipe.HasStraighten)
            {
                var (sw, sh) = recipe.SizeFor(pw, ph, GeometryStages.Dewarp | GeometryStages.Straighten);
                rows.Add(Row(EditSteps.Straighten, Tr.T("Entzerrt"), $"{sw} × {sh} px"));
            }
            if (recipe.HasLevel)
                rows.Add(Row(EditSteps.Level, Tr.T("Gerade gerichtet"),
                    $"{recipe.Angle.ToString("+0.0;−0.0", System.Globalization.CultureInfo.CurrentCulture)}°"));
            if (recipe.HasCrop)
            {
                var (cw, ch) = recipe.SizeFor(pw, ph, GeometryStages.Dewarp | GeometryStages.Straighten | GeometryStages.Level | GeometryStages.Crop);
                rows.Add(Row(EditSteps.CropStep, Tr.T("Zuschnitt"),
                    recipe.CornerRadius > 0 ? Tr.T("{0} × {1} px · runde Ecken", cw, ch) : $"{cw} × {ch} px"));
            }
            if (recipe.HasOrient)
            {
                // net quarter turns: three times right = 270°, one eye for all
                var parts = new List<string>();
                if (recipe.NormalTurns != 0) parts.Add($"{recipe.NormalTurns * 90}°");
                if (recipe.Flip) parts.Add(Tr.T("gespiegelt"));
                rows.Add(Row(EditSteps.Orient, recipe.NormalTurns != 0 ? Tr.T("Gedreht") : Tr.T("Gespiegelt"), string.Join(" · ", parts)));
            }
            if (TargetWidth != SourceWidth || TargetHeight != SourceHeight)
                rows.Add(Row(EditSteps.Resize, Tr.T("Größe"),     // with the method, so that it is visible what computed the result
                    $"{TargetWidth} × {TargetHeight} px · {(AiScalingActive ? SelectedUpscaler.Name : SelectedFilter.Name)}"
                    + (UpscaleTwoPasses ? " · " + Tr.T("2 Durchgänge") : "")));
            if (NoiseReduction > 0.005)
                rows.Add(Row(EditSteps.Denoise, Tr.T("Rauschen"), $"{NoiseReduction * 100:0} %"));
            if (ColorNoiseReduction > 0.005)
                rows.Add(Row(EditSteps.ColorNoise, Tr.T("Farbrauschen"), $"{ColorNoiseReduction * 100:0} %"));
            // White balance is computed first in the chain (before dehaze, light and colour).
            if (HasWhiteBalance)
                rows.Add(Row(EditSteps.WhiteBalance, Tr.T("Weißabgleich"), string.Join(" · ",
                    new[] { WbNeutral is null ? null : WbNeutralText, WbBlack is null ? null : WbBlackText, WbWhite is null ? null : WbWhiteText }
                        .Where(t => t is not null))));
            if (Math.Round(Dehaze) != 0)
                rows.Add(Row(EditSteps.Dehaze, Tr.T("Dunst"), $"{Math.Round(Dehaze):+0;−0} · {Math.Round(DehazePasses)}×"));
            // Order of the chain: match colour (white balance) before light and colour
            AddIfAny(rows, EditSteps.MatchColor, Tr.T("Gleiche Farbe"),
                CanLimitLuminance ? string.Join(" · ", new[]
                {
                    MatchTarget >= 0.5 ? $"{Tr.T("Zielwert")} {Math.Round(MatchTarget):0} %" : null,
                    MatchLimit < 99.5 ? $"{Tr.T("Schwellwert")} {Math.Round(MatchLimit):0} %" : null,
                }.Where(t => t is not null)) is { Length: > 0 } extra ? extra : null : null,
                (Tr.T("Luminanz"), MatchLuminance), (Tr.T("Gesamt"), MatchGlobal), (Tr.T("Ausgleichen"), Neutralize), (Tr.T("Farbe"), Warmth));
            if (MaskAmount >= 0.5)
                rows.Add(Row(EditSteps.LumMask, Tr.T("Luminanzmaske"),
                    Tr.T("+{0:0} % · bis {1:0} % · Verlauf {2:0}", MaskAmount, MaskEnd, MaskCurve)));
            AddIfAny(rows, EditSteps.Light, Tr.T("Licht"),
                (Tr.T("Helligkeit"), Brightness), (Tr.T("Kontrast"), Contrast), (Tr.T("Tiefen"), Shadows), (Tr.T("Lichter"), Highlights));
            var tint = TintOf(SelectedTint) switch
            {
                Tint.BlackWhite => Tr.T("Schwarzweiß"),
                Tint.Color => Tr.T("Färben {0:0}°", TintHue),
                _ => null,
            };
            AddIfAny(rows, EditSteps.Color, Tr.T("Farbe"), tint, (Tr.T("Dynamik"), Vibrance), (Tr.T("Sättigung"), Saturation));
            if (SharpenAmount > 0.01)
                rows.Add(Row(EditSteps.Sharpen, Tr.T("Schärfe"), Tr.T("{0:0.00} · Radius {1:0.0}", SharpenAmount, SharpenRadius)));
            // grain last, as in the chain
            if (Math.Round(GrainAmount) >= 1)
                rows.Add(Row(EditSteps.Grain, Tr.T("Körnung"), string.Join(" · ", new[]
                {
                    $"{Math.Round(GrainAmount):0} %",
                    GrainSizeText(GrainSize),
                    Math.Round(GrainRoughness) >= 1 ? $"{Tr.T("Rauheit")} {Math.Round(GrainRoughness):0}" : null,
                    Math.Round(GrainColor) >= 1 ? $"{Tr.T("Farbe")} {Math.Round(GrainColor):0} %" : null,
                    Math.Round(GrainHardness) >= 1 ? $"{Tr.T("Härte")} {Math.Round(GrainHardness):0}" : null,
                }.Where(t => t is not null))));
        }

        EditRows.Clear();
        foreach (var row in rows) EditRows.Add(row);
        OnPropertyChanged(nameof(HasEditRows));
        OnPropertyChanged(nameof(AiStraightenActive));   // the "Straighten with AI" icon follows the recipe (also after undo)
        OnPropertyChanged(nameof(HasAppliedDewarpLines));   // as does the dot on the "Curved page" icon
        ApplySelectionCommand.NotifyCanExecuteChanged();
    }

    private EditRow Row(EditSteps step, string name, string detail) => new(this, step, name, detail, true, null);

    private void AddIfAny(List<EditRow> rows, EditSteps step, string name, params (string Label, double Value)[] values) =>
        AddIfAny(rows, step, name, null, values);

    private void AddIfAny(List<EditRow> rows, EditSteps step, string name, string? extra, params (string Label, double Value)[] values)
    {
        var parts = values.Where(v => Math.Round(v.Value) != 0)
            .Select(v => $"{v.Label} {Math.Round(v.Value):+0;−0}").ToList();
        if (extra is not null) parts.Insert(0, extra);
        if (parts.Count > 0) rows.Add(Row(step, name, string.Join(" · ", parts)));
    }

    // ------------------------------------------------------------ Apply selection

    private bool CanApplySelection => HiddenSteps != EditSteps.None && EditRows.Any(r => !r.Shown) && !IsEditing;

    /// <summary>
    /// Makes only the switched-on functions permanent: switched-off sliders go to zero, a switched-off AI or
    /// "Skin &amp; details" is taken out of the image, switched-off geometry stages leave the recipe. One step, undoable
    /// with Ctrl+Z.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplySelection))]
    private async Task ApplySelection()
    {
        CommitPendingSettings();
        var hidden = HiddenSteps;
        SettingsState Cleared(SettingsState s) => WithoutHidden(s, hidden);

        // Geometry: switched-off stages are removed from the recipe (in the same step).
        var recipeAfter = _history is { } hr ? WithoutHiddenStages(hr.Recipe, hidden) : null;
        if (recipeAfter is not null && recipeAfter == _history!.Recipe) recipeAfter = null;

        if (_history is { } h && CurrentPath is { } path && PixelsFor(path, h, hidden) is var shown && !ReferenceEquals(shown, h.Current))
        {
            var copy = ImageOps.CloneOf(shown);
            var aiOut = (hidden & EditSteps.Ai) != 0;
            var steps = ImageStepsOf(path);
            var last = steps.Last(s => s.Step!.Kind == ImageStepKind.Ai).Step!;
            // AI removed completely: from here on no AI counts. Only Skin & details removed: the AI stays, without them.
            var step = aiOut
                ? new ImageStep(ImageStepKind.Applied, Tr.T("Auswahl angewendet"), Tr.T("ohne KI"), null)
                : new ImageStep(ImageStepKind.Ai, last.Label, last.Detail, null);
            await ApplyEditAsync(_ => copy, Cleared, step, recipeAfter);
            return;
        }

        if (recipeAfter is not null && _history is { } hg && CurrentPath is { } gpath)
        {
            await CommitRecipeAsync(recipeAfter, new ImageStep(ImageStepKind.Applied, Tr.T("Auswahl angewendet"), null, null), Cleared);
            return;
        }

        var before = CaptureSettings();
        ApplySettings(Cleared(before));
        _undo.Push(new Snapshot(ApplyToAll ? null : CurrentPath, null, before));
        ClearRedo();
        if (!ApplyToAll && CurrentPath is { } current) _fileSettings[current] = CaptureSettings();
        NotifyUndoRedo();
    }

    /// <summary>Returns the recipe without the switched-off geometry stages.</summary>
    private static GeometryRecipe WithoutHiddenStages(GeometryRecipe r, EditSteps hidden)
    {
        if ((hidden & EditSteps.Dewarp) != 0) r = r with { Dewarp = null };
        if ((hidden & EditSteps.Straighten) != 0) r = r with { Quad = null };
        if ((hidden & EditSteps.Level) != 0) r = r with { Angle = 0 };
        if ((hidden & EditSteps.CropStep) != 0) r = r with { Crop = null, CornerRadius = 0 };
        if ((hidden & EditSteps.Orient) != 0) r = r with { Turns = 0, Flip = false };
        return r;
    }

    /// <summary>Returns the state without the switched-off functions: their sliders at zero, nothing switched off any more.</summary>
    internal static SettingsState WithoutHidden(SettingsState s, EditSteps hidden)
    {
        if ((hidden & EditSteps.Resize) != 0) s = s with { TargetWidth = s.SourceWidth, TargetHeight = s.SourceHeight,
            PrintWidthIn = s.SourceWidth / (double)Math.Max(1, s.Dpi), PrintHeightIn = s.SourceHeight / (double)Math.Max(1, s.Dpi) };
        if ((hidden & EditSteps.Denoise) != 0) s = s with { NoiseReduction = 0 };
        if ((hidden & EditSteps.ColorNoise) != 0) s = s with { ColorNoiseReduction = 0 };
        if ((hidden & EditSteps.Dehaze) != 0) s = s with { Dehaze = 0 };
        if ((hidden & EditSteps.Light) != 0) s = s with { Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0 };
        if ((hidden & EditSteps.Color) != 0) s = s with { Saturation = 0, Vibrance = 0, SelectedTint = TintLabels[0].Label };
        if ((hidden & EditSteps.MatchColor) != 0) s = s with { MatchLuminance = 0, MatchLimit = 100, MatchTarget = 0, MatchGlobal = 0, Neutralize = 0, Warmth = 0 };
        if ((hidden & EditSteps.WhiteBalance) != 0) s = s with { WbNeutral = null, WbBlack = null, WbWhite = null };
        if ((hidden & EditSteps.LumMask) != 0) s = s with { MaskAmount = 0 };
        if ((hidden & EditSteps.Sharpen) != 0) s = s with { SharpenAmount = 0 };
        if ((hidden & EditSteps.Grain) != 0) s = s with { GrainAmount = 0 };
        return s with { Hidden = EditSteps.None };
    }
}
