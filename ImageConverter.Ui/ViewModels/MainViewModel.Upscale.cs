using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Ai;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Platform;
using ImageConverter.Imaging;
using ImageConverter.Models;
using ImageMagick;

namespace ImageConverter.ViewModels;

/// <summary>A question for the view: title, text, answers (listed one below the other) and the highlighted answer.</summary>
/// <param name="Title">Window title.</param>
/// <param name="Message">Question text.</param>
/// <param name="Choices">Answers in display order.</param>
/// <param name="Default">Index of the highlighted answer.</param>
public sealed record ChoiceRequest(string Title, string Message, IReadOnlyList<string> Choices, int Default = 0);

// Upscaling (group under "Size"): when enlarging, the pixels come not only from the filter but from an AI.
// It is computed only on demand (button, or after a question when saving); the result at four times the size stays in
// memory and serves preview and export as the basis for scaling. Fidelity and details are mixed afterwards without a new
// computation. The inspection view shows the result at full resolution (zoom to 100 % and beyond), before/after compares
// it with the filter; during the computation finished tiles appear immediately.
public partial class MainViewModel
{
    public IReadOnlyList<UpscaleOption> UpscaleOptions { get; } = UpscaleOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesAiUpscaler), nameof(UpscaleModelMissing), nameof(UpscaleDownloadText))]
    public partial UpscaleOption SelectedUpscaler { get; set; } = UpscaleOption.All[0];

    /// <summary>0 … 1: fidelity safeguard; scaled back down, the result yields the original again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpscaleFidelityLabel))]
    public partial double UpscaleFidelity { get; set; } = 1;

    /// <summary>0 … 1: share of the AI relative to the filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpscaleDetailsLabel))]
    public partial double UpscaleDetails { get; set; } = 1;

    /// <summary>
    /// Above 4×: two AI passes (the fine details also come from the AI) instead of one followed by the filter enlarging
    /// the rest. Chosen in the question before computing.
    /// </summary>
    [ObservableProperty] public partial bool UpscaleTwice { get; set; } = true;

    /// <summary>
    /// During upscaling everything else is locked (settings, tools, undo, image switch, saving), because the result belongs
    /// to exactly this state. Zoom, panning, before/after and cancel remain available.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpscaleCommand), nameof(CancelUpscaleCommand), nameof(UndoCommand), nameof(RedoCommand),
        nameof(ExportCommand), nameof(PrintCommand), nameof(RotateLeftCommand), nameof(RotateRightCommand),
        nameof(FlipHorizontalCommand), nameof(CropToSelectionCommand), nameof(PreviousFileCommand), nameof(NextFileCommand), nameof(StraightenWithAiCommand))]
    [NotifyPropertyChangedFor(nameof(CanUndo), nameof(CanRedo), nameof(CanTransform), nameof(CanCropToSelection), nameof(IsIdle), nameof(SelectionActive))]
    public partial bool IsUpscaling { get; set; }

    /// <summary>No upscaling is running; settings and tools are available.</summary>
    public bool IsIdle => !IsUpscaling;

    /// <summary>Browsing between images is possible (not during upscaling).</summary>
    public bool CanBrowse => HasMultipleFiles && !IsUpscaling;

    /// <summary>Inspection view: the result at full resolution instead of the preview (without light/colour/sharpness).</summary>
    [ObservableProperty] public partial bool UpscaleInspect { get; set; }

    [ObservableProperty] public partial string? UpscaleStatus { get; set; }

    /// <summary>Changes when new tiles are in the displayed bitmap (the view then redraws).</summary>
    [ObservableProperty] public partial object? RedrawToken { get; set; }

    /// <summary>Visible part of the image in image pixels; set by the view.</summary>
    public Func<Rect>? VisibleImageArea { get; set; }

    /// <summary>Asks a question (modal); result: index of the answer, −1 = window closed.</summary>
    public event Func<ChoiceRequest, Task<int>>? ChoiceRequested;

    public bool UsesAiUpscaler => SelectedUpscaler.Method != UpscaleMethod.Filter;

    /// <summary>An AI scaler is selected and the image is being enlarged; only then does the AI compute (fidelity, details, time).</summary>
    public bool AiScalingActive => UsesAiUpscaler && UpscaleEnlarges();

    /// <summary>The target is larger than the image (in one of the edges).</summary>
    public bool IsEnlarging => UpscaleEnlarges();

    /// <summary>The target differs from the image; otherwise there is nothing to scale.</summary>
    public bool HasSizeChange
    {
        get
        {
            var (w, h) = EffectiveTarget();
            return w != SourceWidth || h != SourceHeight;
        }
    }

    /// <summary>Entries of the "Method" list; fixed, with <see cref="SyncScaleChoices"/> hiding entries depending on the direction.</summary>
    public IReadOnlyList<ScaleChoice> ScaleChoices => _scaleChoices ??= CreateScaleChoices();

    private IReadOnlyList<ScaleChoice>? _scaleChoices;

    private IReadOnlyList<ScaleChoice> CreateScaleChoices()
    {
        var list = ScaleChoice.CreateList();
        foreach (var c in list) c.IsOffered = c.OfferedFor(_direction);
        return list;
    }

    /// <summary>Selected entry; filter and AI scaler are stored separately (the AI choice is kept while reducing).</summary>
    public ScaleChoice? SelectedScaleChoice
    {
        get
        {
            var list = ScaleChoices;
            if (Direction != ScaleDirection.Down && UsesAiUpscaler && list.FirstOrDefault(c => c.Ai == SelectedUpscaler) is { } ai) return ai;
            return list.FirstOrDefault(c => c.Filter == SelectedFilter && c.IsOffered) ?? list.First(c => c.Filter == FilterOption.Lanczos);
        }
        set
        {
            if (value is null || value.IsHeader)
            {
                Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedScaleChoice)));   // heading: not selectable
                return;
            }
            if (value.Ai is { } ai) SelectedUpscaler = ai;
            else
            {
                SelectedFilter = value.Filter!;
                if (Direction != ScaleDirection.Down) SelectedUpscaler = UpscaleOption.Of(UpscaleMethod.Filter);
            }
        }
    }

    private ScaleDirection _direction = ScaleDirection.Same;

    /// <summary>Smaller than, equal to or larger than the image.</summary>
    public ScaleDirection Direction => IsEnlarging ? ScaleDirection.Up : HasSizeChange ? ScaleDirection.Down : ScaleDirection.Same;

    /// <summary>An AI scaler is selected and the image is not being reduced; fidelity and details can be set even before the dimensions.</summary>
    public bool AiScalerChosen => UsesAiUpscaler && Direction != ScaleDirection.Down;

    /// <summary>
    /// After every size change: if the direction changed, a different list is offered; a filter that is not offered
    /// in the new direction (Box when enlarging …) becomes Lanczos.
    /// </summary>
    private void SyncScaleChoices()
    {
        var direction = Direction;
        if (direction == ScaleDirection.Up && !SelectedFilter.Up || direction == ScaleDirection.Down && !SelectedFilter.Down)
            SelectedFilter = FilterOption.Lanczos;
        if (direction != _direction)
        {
            _direction = direction;
            foreach (var c in ScaleChoices) c.IsOffered = c.OfferedFor(direction);
        }
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(AiScalerChosen));
        OnPropertyChanged(nameof(SelectedScaleChoice));
        OnPropertyChanged(nameof(IsEnlarging));
        OnPropertyChanged(nameof(HasSizeChange));
        OnPropertyChanged(nameof(AiScalingActive));
        OnPropertyChanged(nameof(ScaleButtonText));
        UpscaleCommand.NotifyCanExecuteChanged();
        ResetScaleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The device the AI scalers compute on, shown below fidelity/details and in the progress.</summary>
    public string ComputeDeviceText => Ai.Sessions.OnGpu && OnnxSessions.GpuName is { } gpu
        ? Tr.T("KI rechnet auf der Grafikkarte ({0}).", gpu)
        : Tr.T("KI rechnet auf der CPU.");

    /// <summary>"Scale", with the estimated time when an AI computes.</summary>
    public string ScaleButtonText => AiScalingActive && MatchingUpscale() is null
        ? Tr.T("Skalieren (≈ {0})", Upscalers.Format(CurrentEstimate()))
        : Tr.T("Skalieren");

    /// <summary>Plan for the current state: one or two passes and the base of the last one.</summary>
    private UpscalePlan CurrentPlan()
    {
        var (tw, th) = EffectiveTarget();
        return Upscalers.Plan(SourceWidth, SourceHeight, tw, th, UpscaleTwice);
    }

    private TimeSpan CurrentEstimate() => Upscalers.Estimate(SelectedUpscaler.Method, SourceWidth, SourceHeight, CurrentPlan());

    /// <summary>The AI computes in two passes ("Size" row of the "Edits" list).</summary>
    public bool UpscaleTwoPasses => AiScalingActive && CurrentPlan().Passes == 2;
    public string UpscaleFidelityLabel => $"{UpscaleFidelity * 100:0} %";
    public string UpscaleDetailsLabel => $"{UpscaleDetails * 100:0} %";

    // ------------------------------------------------------------ Models

    /// <summary>The selected model (with all its parts) is not installed.</summary>
    public bool UpscaleModelMissing => SelectedUpscaler.Model is { } m && !UpscaleModelInstalled(m);

    /// <summary>Whether everything is installed. PiSA-SR needs the text encoder only as long as the empty prompt embedding is missing.</summary>
    private bool UpscaleModelInstalled(AiModel m) =>
        m == AiModelCatalog.PiSaSr
            ? PiSaSrModel.Find(Ai.Store) is not null
            : Ai.Store.IsInstalled(m) && m.Requires.All(id => AiModelCatalog.Find(id) is { } r && Ai.Store.IsInstalled(r));

    private IEnumerable<AiModelItem> UpscaleItems() =>
        SelectedUpscaler.Model is { } m
            ? Ai.Models.Where(i => i.Model == m || m.Requires.Contains(i.Model.Id))
            : [];

    public bool IsDownloadingUpscaler => UpscaleItems().Any(i => i.IsDownloading);

    /// <summary>"Download (2.6 GB)" or the progress across all parts.</summary>
    public string UpscaleDownloadText
    {
        get
        {
            var items = UpscaleItems().Where(i => !i.IsInstalled || i.IsDownloading).ToList();
            var total = items.Sum(i => i.Model.DownloadSize);
            var size = total >= 1L << 30 ? $"{total / (double)(1L << 30):0.0} GB" : $"{total / (double)(1 << 20):0} MB";
            if (!items.Any(i => i.IsDownloading)) return Tr.T("Herunterladen ({0})", size);
            var done = items.Sum(i => i.Model.DownloadSize * (i.IsInstalled ? 100 : i.Progress)) / Math.Max(1, total);
            return Tr.T("Wird geladen … {0:0} % von {1}", done, size);
        }
    }

    [RelayCommand]
    private async Task DownloadUpscaler()
    {
        // The main model downloads its parts along with it (Requires).
        if (SelectedUpscaler.Model is not { } m || Ai.Models.FirstOrDefault(i => i.Model == m) is not { } item) return;
        WatchUpscaleItems();
        if (!item.IsInstalled && !item.IsDownloading) await item.DownloadCommand.ExecuteAsync(null);
        else
            foreach (var part in UpscaleItems().Where(i => !i.IsInstalled && !i.IsDownloading).ToList())
                _ = part.DownloadCommand.ExecuteAsync(null);
    }

    private bool _watchingUpscaleItems;

    private void WatchUpscaleItems()
    {
        if (_watchingUpscaleItems) return;
        _watchingUpscaleItems = true;
        foreach (var i in Ai.Models.Where(i => i.Model.Kind is AiModelKind.Upscale or AiModelKind.Part || i.Model == AiModelCatalog.Allround))
            i.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AiModelItem.Progress) or nameof(AiModelItem.IsDownloading) or nameof(AiModelItem.IsInstalled))
                    NotifyUpscaleModels();
            };
        Ai.ModelsChanged += (_, _) => NotifyUpscaleModels();
    }

    private void NotifyUpscaleModels()
    {
        OnPropertyChanged(nameof(UpscaleModelMissing));
        OnPropertyChanged(nameof(IsDownloadingUpscaler));
        OnPropertyChanged(nameof(UpscaleDownloadText));
        UpdateUpscaleStatus();
        UpscaleCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------ Result

    /// <summary>What a result applies to: pixel image (with/without switched-off functions), geometry, method, passes.</summary>
    private sealed record UpscaleKey(string Path, MagickImage Pixels, GeometryRecipe Recipe, GeometryStages Stages, UpscaleMethod Method,
        UpscalePlan Plan)
    {
        public bool Equals(UpscaleKey? o) => o is not null && Path == o.Path && ReferenceEquals(Pixels, o.Pixels) && Recipe == o.Recipe
                                             && Stages == o.Stages && Method == o.Method && Plan == o.Plan;

        public override int GetHashCode() => HashCode.Combine(Path, RuntimeHelpers.GetHashCode(Pixels), Stages, Method);
    }

    /// <summary>
    /// The latest result: the whole image or only a region (Region in image pixels, BaseRegion in pixels of the base of
    /// the last pass; identical for a single pass). <c>FirstPass</c>: with two passes, the fidelity, details and gamma the
    /// first one was blended and reduced with; the sliders afterwards only reach the last pass (No. 101).
    /// </summary>
    private sealed record UpscaleEntry(UpscaleKey Key, SourceRect? Region, SourceRect? BaseRegion, UpscaleResult Result, TimeSpan Took,
        (double Fidelity, double Details, bool Gamma)? FirstPass = null);

    private UpscaleEntry? _upscale;
    private (UpscaleResult Result, double Fidelity, double Details, MagickImage Image)? _upscaledImage;
    private Task? _composeTask;
    private CancellationTokenSource? _upscaleCts;

    private UpscaleKey? CurrentUpscaleKey()
    {
        if (_history is not { } h || CurrentPath is not { } path) return null;
        return new UpscaleKey(path, PixelsFor(path, h, HiddenSteps), h.Recipe, StagesShown(HiddenSteps), SelectedUpscaler.Method, CurrentPlan());
    }

    /// <summary>Whether the image is being enlarged (target size larger than the image).</summary>
    private bool UpscaleEnlarges()
    {
        var (w, h) = EffectiveTarget();
        return w > SourceWidth || h > SourceHeight;
    }

    /// <summary>The result for the current state (whole image only, unless <paramref name="wholeOnly"/> is false), otherwise null.</summary>
    private UpscaleEntry? MatchingUpscale(bool wholeOnly = true) =>
        UsesAiUpscaler && _upscale is { } e && e.Key.Equals(CurrentUpscaleKey()) && (!wholeOnly || e.Region is null) ? e : null;

    /// <summary>
    /// For the preview: the composed ×4 image if a matching result exists and the image is being enlarged.
    /// If the mix for the current sliders is missing, it is computed in the background (followed by a new preview); until
    /// then the previous mix applies.
    /// </summary>
    private MagickImage? UpscaledForPreview()
    {
        if (!UpscaleEnlarges() || MatchingUpscale() is not { } e) return null;
        var (f, d) = (UpscaleFidelity, UpscaleDetails);
        if (_upscaledImage is { } c && ReferenceEquals(c.Result, e.Result))
        {
            if (c.Fidelity != f || c.Details != d) RecomposeLater();
            return c.Image;
        }
        RecomposeLater();
        return null;
    }

    /// <summary>For the export: the composed image with the current sliders (waits, computes it if necessary).</summary>
    private async Task<MagickImage?> UpscaledForExportAsync()
    {
        if (!UpscaleEnlarges() || MatchingUpscale() is not { } e) return null;
        var (f, d) = (UpscaleFidelity, UpscaleDetails);
        if (_upscaledImage is { } c && ReferenceEquals(c.Result, e.Result) && c.Fidelity == f && c.Details == d) return c.Image;
        var image = await Task.Run(() => e.Result.Compose(f, d));
        SetUpscaledImage(e.Result, f, d, image);
        return image;
    }

    private void SetUpscaledImage(UpscaleResult result, double f, double d, MagickImage image)
    {
        var old = _upscaledImage?.Image;
        _upscaledImage = (result, f, d, image);
        // The old image may still be in use by a running preview; EmptyUpscaleTrash releases it after the next run.
        if (old is not null && !ReferenceEquals(old, image)) _upscaleTrash.Add(old);
    }

    private readonly List<MagickImage> _upscaleTrash = [];

    /// <summary>Releases evicted ×4 images; call only when no preview or export is computing on them any more.</summary>
    private void EmptyUpscaleTrash()
    {
        if (IsExporting) return;
        foreach (var image in _upscaleTrash.Where(i => !ReferenceEquals(i, _upscaledImage?.Image))) image.Dispose();
        _upscaleTrash.Clear();
    }

    /// <summary>Keeps the upscaling status and view in step with the image: switching, editing, size.</summary>
    private void OnUpscaleRelevantChange(object? sender, PropertyChangedEventArgs e)
    {
        // Any other setting (light, colour, size, method …): back to the normal preview with everything applied
        if (UpscaleInspect && !IsUpscaling && e.PropertyName is { } name && TrackedSettings.ContainsKey(name)
            && name is not (nameof(UpscaleFidelity) or nameof(UpscaleDetails)))
            UpscaleInspect = false;

        switch (e.PropertyName)
        {
            case nameof(CurrentFileName):
                UpscaleImageChanged();
                break;
            case nameof(FullBitmap):
                if (UpscaleInspect && !IsUpscaling && MatchingUpscale(wholeOnly: false) is null) UpscaleInspect = false;
                UpdateUpscaleStatus();
                break;
            case nameof(HasImage):
                UpscaleCommand.NotifyCanExecuteChanged();
                break;
            case nameof(TargetWidth) or nameof(TargetHeight) or nameof(HiddenSteps) or nameof(SourceWidth) or nameof(SourceHeight):
                SyncScaleChoices();
                UpdateUpscaleStatus();
                break;
        }
    }

    private void RecomposeLater()
    {
        if (_composeTask is { IsCompleted: false }) { _recomposeAgain = true; return; }
        _composeTask = RecomposeAsync();
    }

    private bool _recomposeAgain;

    private async Task RecomposeAsync()
    {
        await Task.Delay(250);   // slider still moving
        do
        {
            _recomposeAgain = false;
            if (MatchingUpscale() is not { } e) return;
            var (f, d) = (UpscaleFidelity, UpscaleDetails);
            if (_upscaledImage is { } c && ReferenceEquals(c.Result, e.Result) && c.Fidelity == f && c.Details == d) break;
            var image = await Task.Run(() => e.Result.Compose(f, d));
            SetUpscaledImage(e.Result, f, d, image);
        } while (_recomposeAgain);
        RequestPreview();
    }

    partial void OnSelectedUpscalerChanged(UpscaleOption value)
    {
        WatchUpscaleItems();
        UpdateUpscaleStatus();
        OnPropertyChanged(nameof(SelectedScaleChoice));
        OnPropertyChanged(nameof(AiScalingActive));
        OnPropertyChanged(nameof(AiScalerChosen));
        OnPropertyChanged(nameof(ScaleButtonText));
        UpscaleCommand.NotifyCanExecuteChanged();
        RequestPreview();
    }

    partial void OnSelectedFilterChanged(FilterOption value)
    {
        OnPropertyChanged(nameof(SelectedScaleChoice));
        RequestPreview();
    }

    partial void OnUpscaleTwiceChanged(bool value)
    {
        OnPropertyChanged(nameof(UpscaleTwoPasses));
        UpdateUpscaleStatus();
        RequestPreview();
    }

    partial void OnUpscaleFidelityChanged(double value) => UpscaleSlidersChanged();
    partial void OnUpscaleDetailsChanged(double value) => UpscaleSlidersChanged();

    private void UpscaleSlidersChanged()
    {
        if (_upscale is null || _inspect is { Factor: 0 }) return;
        if (UpscaleInspect && !IsUpscaling) RedrawInspectLater();
        RequestPreview();
    }

    partial void OnUpscaleInspectChanged(bool value)
    {
        if (!value && !IsUpscaling) ClearInspect();
        UpdateUpscaleStatus();
    }

    /// <summary>Updates the status line of the group.</summary>
    private void UpdateUpscaleStatus()
    {
        OnPropertyChanged(nameof(ScaleButtonText));
        if (IsUpscaling) return;   // the computation writes the status itself
        var inspect = UpscaleInspect
            ? Tr.T("Die Vorschau zeigt das Ergebnis in voller Größe, nur skaliert — Licht, Farbe und Schärfe kommen zurück, sobald du etwas anderes einstellst.")
            : null;
        if (!HasSizeChange)
        {
            UpscaleStatus = UsesAiUpscaler
                ? Tr.T("Keine Größenänderung — der KI-Scaler wirkt, sobald die Maße größer sind als das Bild (z. B. 4× im Menü der Formate).")
                : Tr.T("Keine Größenänderung.");
            return;
        }
        if (!AiScalingActive) { UpscaleStatus = inspect; return; }
        if (UpscaleModelMissing) { UpscaleStatus = Tr.T("Modell nicht geladen."); return; }
        var estimate = Upscalers.Format(CurrentEstimate());
        if (MatchingUpscale() is { } e)
        {
            // Two passes: the first was blended with the values at the time; a full result needs a new computation, which
            // takes long, so the result stays and the status says so instead of dropping it
            var partly = e.FirstPass is { } first && first != (UpscaleFidelity, UpscaleDetails, GammaCorrect)
                ? "\n" + Tr.T("Treue, Details oder Gamma geändert: Bei über 4× wirkt das jetzt nur auf den zweiten Durchgang. Für das volle Ergebnis neu berechnen (≈ {0}).", estimate)
                : "";
            UpscaleStatus = Tr.T("Berechnet ({0}) — Vorschau und Speichern verwenden es.", Upscalers.Format(e.Took))
                            + partly + (inspect is null ? "" : "\n" + inspect);
            return;
        }
        if (MatchingUpscale(wholeOnly: false) is not null)
        {
            UpscaleStatus = Tr.T("Nur ein Ausschnitt ist berechnet. Fürs Speichern wird das ganze Bild gerechnet (≈ {0}).", estimate);
            return;
        }
        UpscaleStatus = _upscale is { } old && old.Key.Path == CurrentPath && old.Key.Method == SelectedUpscaler.Method
            ? Tr.T("Das Bild hat sich geändert — neu berechnen (≈ {0}).", estimate)
            : Tr.T("Noch nicht berechnet (≈ {0}). Beim Speichern wird gefragt.", estimate);
    }

    // ------------------------------------------------------------ Computation

    // Not while an image step computes: the result would be stored for the new image but computed from the old (No. 98)
    private bool CanUpscale => HasImage && !IsUpscaling && !IsEditing && HasSizeChange && (!AiScalingActive || !UpscaleModelMissing);

    /// <summary>
    /// "Scale": with a filter in seconds, with an AI scaler in minutes; above 4× or for a long duration after a
    /// question. Afterwards the preview shows the result at full size, for zooming in and for before/after.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpscale))]
    private async Task Upscale()
    {
        if (!AiScalingActive)
        {
            await ScaleWithFilterAsync();
            return;
        }
        if (await Task.Run(() => ResultDocument) is not { } doc) return;
        int w, h;
        lock (doc) (w, h) = ((int)doc.Width, (int)doc.Height);
        if (await ShowReadyUpscaleAsync(doc)) return;

        if (await AskUpscaleAsync(w, h) is not { } choice) return;
        UpscaleTwice = choice.Twice;
        if (await ShowReadyUpscaleAsync(doc)) return;   // already computed with the other choice
        if (await Task.Run(CurrentUpscaleKey) is not { } key) return;
        await RunUpscaleAsync(doc, key, choice.Region);
    }

    /// <summary>Already computed (whole image): only shows it. Returns true if it was shown.</summary>
    private async Task<bool> ShowReadyUpscaleAsync(MagickImage doc)
    {
        if (MatchingUpscale() is not { } ready) return false;
        await PrepareInspectAsync(doc, ready.Key.Plan, null);
        UpscaleInspect = true;
        ShowInspectBitmaps();
        await RedrawInspectAsync();
        return true;
    }

    /// <summary>From this duration on, a question is asked first (whole image or only the visible region).</summary>
    private static readonly TimeSpan LongUpscale = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Question before computing, each option with duration and end time: above 4× one or two passes, for a long duration
    /// the whole image or only the visible region. Short and at most 4×: no question, the current choice applies.
    /// null = cancelled.
    /// </summary>
    private async Task<(bool Twice, SourceRect? Region)?> AskUpscaleAsync(int w, int h)
    {
        var method = SelectedUpscaler.Method;
        var (tw, th) = EffectiveTarget();
        var beyond = Upscalers.BeyondOnePass(w, h, tw, th);
        var visible = VisibleRegion(w, h);
        var options = new List<(string Text, bool Twice, SourceRect? Region)>();
        var isLong = false;
        foreach (var twice in beyond ? [true, false] : new[] { UpscaleTwice })
        {
            var plan = Upscalers.Plan(w, h, tw, th, twice);
            var whole = Upscalers.Estimate(method, w, h, plan);
            var wholeText = Upscalers.FormatWithEnd(whole, DateTime.Now);
            var label = twice ? Tr.T("Zwei KI-Durchgänge") : Tr.T("Ein KI-Durchgang, den Rest macht der Filter");
            options.Add((beyond ? Tr.T("{0} — ganzes Bild (≈ {1})", label, wholeText) : Tr.T("Ganzes Bild berechnen (≈ {0})", wholeText),
                twice, null));
            if (whole <= LongUpscale) continue;
            isLong = true;
            if (visible is not { } v) continue;
            var part = Upscalers.FormatWithEnd(Upscalers.Estimate(method, w, h, plan, v.Width, v.Height), DateTime.Now);
            options.Add((beyond ? Tr.T("{0} — nur der sichtbare Ausschnitt ({1} × {2} px, ≈ {3})", label, v.Width, v.Height, part)
                : Tr.T("Nur den sichtbaren Ausschnitt ({0} × {1} px, ≈ {2})", v.Width, v.Height, part), twice, v));
        }
        if ((!beyond && !isLong) || ChoiceRequested is not { } ask) return (UpscaleTwice, null);

        // Preselected: the previous choice, and the region for a long duration
        var preferred = options.FindIndex(o => o.Twice == UpscaleTwice && o.Region is not null);
        if (preferred < 0) preferred = options.FindIndex(o => o.Twice == UpscaleTwice);
        var trial = options.Any(o => o.Region is not null)
            ? "\n\n" + Tr.T("Zum Ausprobieren reicht der Ausschnitt, den du gerade siehst — zoome dazu vorher hinein.")
            : "";
        var choices = options.Select(o => o.Text).Append(Tr.T("Abbrechen")).ToList();
        var request = beyond
            ? new ChoiceRequest(Tr.T("Mehr als 4× vergrößern"), BeyondText(w, h, tw, th) + trial, choices, preferred)
            : new ChoiceRequest(Tr.T("Upscaling dauert länger"),
                Tr.T("„{0}“ braucht für dieses Bild ({1} × {2} px) auf diesem Rechner etwa {3}.", SelectedUpscaler.Name, w, h,
                    Upscalers.Format(Upscalers.Estimate(method, w, h, Upscalers.Plan(w, h, tw, th, UpscaleTwice)))) + trial,
                choices, preferred);
        var answer = await ask(request);
        if (answer < 0 || answer >= options.Count) return null;
        return (options[answer].Twice, options[answer].Region);
    }

    /// <summary>Above 4×: what one and two passes mean.</summary>
    private string BeyondText(int w, int h, int tw, int th) =>
        Tr.T("Das Ziel ({0} × {1} px) ist {2:0.#}× so groß wie das Bild ({3} × {4} px); „{5}“ vergrößert je Durchgang 4×. Zwei Durchgänge: auch die feinen Details kommen von der KI — deutlich schärfer, sie erfindet aber mehr dazu. Ein Durchgang: den Rest vergrößert der Filter — weicher.",
            tw, th, Math.Max(tw / (double)w, th / (double)h), w, h, SelectedUpscaler.Name);

    [RelayCommand(CanExecute = nameof(IsUpscaling))]
    private void CancelUpscale() => _upscaleCts?.Cancel();

    /// <summary>Visible region rounded to whole pixels; null if (almost) the whole image is visible anyway (more than 80 %).</summary>
    private SourceRect? VisibleRegion(int w, int h)
    {
        if (VisibleImageArea?.Invoke() is not { Width: > 0 } r || DocumentSize.Width <= 0) return null;
        var k = w / (double)DocumentSize.Width;   // the view works in image pixels of the document
        int x0 = Math.Clamp((int)Math.Floor(r.X * k), 0, w - 1), y0 = Math.Clamp((int)Math.Floor(r.Y * k), 0, h - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(r.Right * k), x0 + 1, w), y1 = Math.Clamp((int)Math.Ceiling(r.Bottom * k), y0 + 1, h);
        if ((x1 - x0) * (double)(y1 - y0) > 0.8 * w * h) return null;
        return new SourceRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Computes the upscaling (whole image or region) and shows it live in the inspection view. true = finished.</summary>
    private async Task<bool> RunUpscaleAsync(MagickImage doc, UpscaleKey key, SourceRect? region)
    {
        if (SelectedUpscaler.Model is null) return false;
        var (method, plan) = (key.Method, key.Plan);
        var cts = _upscaleCts = new CancellationTokenSource();
        IsUpscaling = true;
        AppLog.Activity($"Upscaling: {SelectedUpscaler.Name}, {plan.Passes} Durchgang/Durchgänge ({(Ai.Sessions.OnGpu ? "Grafikkarte" : "CPU")}) auf {CurrentFileName}");
        ErrorMessage = null;
        var clock = Stopwatch.StartNew();
        var preparing = false;
        int dw, dh;
        lock (doc) (dw, dh) = ((int)doc.Width, (int)doc.Height);
        var (pw, ph) = region is { } part ? (part.Width, part.Height) : (dw, dh);
        var expected = Upscalers.Estimate(method, dw, dh, plan, pw, ph);
        // Progress spans both passes: share of the first pass in the total time
        var share = plan.Passes == 2 && expected > TimeSpan.Zero ? Upscalers.Estimate(method, pw, ph) / expected : 1;
        var pass = 1;
        void Report(double f)
        {
            var remaining = RemainingText(clock.Elapsed, f, expected);
            ActivityText = preparing
                ? Tr.T("Upscaling · KI-Modell wird vorbereitet (einmalig) … {0:0} %", f * 100)
                : plan.Passes == 2
                    ? Ai.Sessions.OnGpu
                        ? Tr.T("Upscaling auf der Grafikkarte · Durchgang {0} von 2 · {1:0} %{2}", pass, f * 100, remaining)
                        : Tr.T("Upscaling auf der CPU · Durchgang {0} von 2 · {1:0} %{2}", pass, f * 100, remaining)
                    : Ai.Sessions.OnGpu
                        ? Tr.T("Upscaling auf der Grafikkarte · {0:0} %{1}", f * 100, remaining)
                        : Tr.T("Upscaling auf der CPU · {0:0} %{1}", f * 100, remaining);
            ActivityProgress = f * 100;
            if (!preparing) UpscaleStatus = Tr.T("Wird berechnet …") + remaining;
        }
        var firstProgress = new Progress<double>(f => Report(preparing ? f : f * share));
        var secondProgress = new Progress<double>(f =>
        {
            pass = 2;
            Report(share + f * (1 - share));
        });
        ActivityCancel = CancelUpscaleCommand;
        Report(0);

        // Prepare the inspection view: Before = filter, After fills with finished tiles (of the last pass).
        var baseRegion = region is { } rg ? ToBase(rg, dw, dh, plan) : (SourceRect?)null;
        var (f0, d0, gamma) = (UpscaleFidelity, UpscaleDetails, GammaCorrect);
        await PrepareInspectAsync(doc, plan, baseRegion);
        UpscaleInspect = true;
        var view = _inspect;

        try
        {
            var result = await Task.Run(() =>
            {
                using var input = region is { } r ? CropOf(doc, r) : null;
                var source = input ?? doc;
                void Done(UpscaleResult res, SourceRect rect)
                {
                    if (view is null) return;
                    var (ox, oy) = baseRegion is { } br ? (br.X, br.Y) : (0, 0);
                    BlitTile(view, res, rect, ox, oy, f0, d0, live: true);
                }

                return ComputePlanned(source, method, plan.Passes, baseRegion?.Width ?? plan.BaseWidth, baseRegion?.Height ?? plan.BaseHeight,
                    f0, d0, gamma, firstProgress, secondProgress, prepare =>
                    {
                        preparing = prepare;
                        if (prepare) Dispatcher.UIThread.Post(() => Report(0));
                        else clock.Restart();
                    }, cts.Token, Done);
            }, cts.Token);

            var took = clock.Elapsed;
            Ai.RefreshGpuStatus();
            // Release the model before the result is composed; fidelity and details no longer need it.
            if (Upscalers.ModelOf(method) is { } usedModel) Ai.Sessions.Release(usedModel.Id);
            _upscale = new UpscaleEntry(key, region, baseRegion, result, took, plan.Passes > 1 ? (f0, d0, gamma) : null);
            _upscaledImage = null;
            if (region is null)
            {
                var image = await Task.Run(() => result.Compose(UpscaleFidelity, UpscaleDetails));
                SetUpscaledImage(result, UpscaleFidelity, UpscaleDetails, image);
            }
            // Redraw the whole view once: during the computation the neighbours were missing at the tile edges (fidelity),
            // and the sliders may have changed in the meantime.
            RedrawInspectLater();
            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
            StatusMessage = Tr.T("Upscaling abgebrochen.");
            if (_upscale is null || !_upscale.Key.Equals(key)) ClearInspect();
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Upscaling fehlgeschlagen: {0}", ex.Message);
            ClearInspect();
            return false;
        }
        finally
        {
            IsUpscaling = false;
            AppLog.Activity(null);
            ActivityText = null;
            ActivityProgress = -1;
            ActivityCancel = null;
            UpdateUpscaleStatus();
            RequestPreview();
        }
    }

    /// <summary>Converts a region (image pixels) to pixels of the base of the last pass.</summary>
    private static SourceRect ToBase(SourceRect r, int w, int h, UpscalePlan plan)
    {
        if (plan.Passes == 1) return r;
        double sx = plan.BaseWidth / (double)w, sy = plan.BaseHeight / (double)h;
        int x0 = (int)Math.Round(r.X * sx), y0 = (int)Math.Round(r.Y * sy);
        int x1 = Math.Clamp((int)Math.Round((r.X + r.Width) * sx), x0 + 1, plan.BaseWidth);
        int y1 = Math.Clamp((int)Math.Round((r.Y + r.Height) * sy), y0 + 1, plan.BaseHeight);
        return new SourceRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Computes according to the plan (on the worker thread). One pass: like <see cref="ComputeUpscale"/>. Two: composes the
    /// result of the first with fidelity and details, scales it to the base (<paramref name="baseWidth"/> ×
    /// <paramref name="baseHeight"/>) and computes ×4 once more. Only the last pass reports finished tiles; the time per
    /// pass refines the estimate.
    /// </summary>
    private UpscaleResult ComputePlanned(MagickImage source, UpscaleMethod method, int passes, int baseWidth, int baseHeight,
        double fidelity, double details, bool gamma, IProgress<double> first, IProgress<double> second, Action<bool>? preparing,
        CancellationToken ct, Action<UpscaleResult, SourceRect>? done = null)
    {
        var clock = Stopwatch.StartNew();
        void Prepare(bool start)
        {
            preparing?.Invoke(start);
            if (!start) clock.Restart();
        }
        int w, h;
        lock (source) (w, h) = ((int)source.Width, (int)source.Height);
        var result = ComputeUpscale(source, method, first, Prepare, ct, passes == 1 ? done : null);
        Upscalers.Measured(method, w, h, clock.Elapsed);
        SaveUpscaleTimes();
        if (passes == 1) return result;

        MagickImage middle;
        using (var composed = result.Compose(fidelity, details, ct))
            middle = ImageOps.Resize(composed, baseWidth, baseHeight, FilterOption.Lanczos.Filter, gamma);
        using (middle)
        {
            clock.Restart();
            var last = ComputeUpscale(middle, method, second, Prepare, ct, done);
            Upscalers.Measured(method, baseWidth, baseHeight, clock.Elapsed);
            SaveUpscaleTimes();
            return last;
        }
    }

    /// <summary>
    /// Speed test: runs an upscaler on a 256 × 256 test image (several tiles per part, as with large images), preceded
    /// by a small run that loads the model. Replaces the estimate for this machine. null: the model is missing (or
    /// PiSA-SR has never run; the conversion takes long and is left to the first real run).
    /// </summary>
    /// <param name="method">The upscaler to measure.</param>
    /// <param name="ct">Cancels the measurement.</param>
    /// <returns>Duration of the 256 × 256 run, or null.</returns>
    internal TimeSpan? MeasureUpscaler(UpscaleMethod method, CancellationToken ct)
    {
        if (Upscalers.ModelOf(method) is not { } model || !UpscaleModelInstalled(model)) return null;
        if (method == UpscaleMethod.Generative && PiSaSrModel.Find(Ai.Store) is { } sources
            && PiSaSrModel.NeedsBuild(sources, Ai.Sessions.OnGpu)) return null;
        static MagickImage Test(int size)
        {
            var image = new MagickImage(new MagickColor(30000, 32000, 28000), (uint)size, (uint)size);
            image.AddNoise(NoiseType.Gaussian, 1.5);
            image.Alpha(AlphaOption.Off);
            return image;
        }
        var none = new Progress<double>();
        using (var warm = Test(64)) ComputeUpscale(warm, method, none, null, ct);
        using var test = Test(256);
        var clock = Stopwatch.StartNew();
        ComputeUpscale(test, method, none, null, ct);
        clock.Stop();
        Upscalers.Measured(method, 256, 256, clock.Elapsed, replace: true);
        SaveUpscaleTimes();
        return clock.Elapsed;
    }

    /// <summary>Saves the measured upscaler times for this machine, so that the estimate is also correct after a restart.</summary>
    private static void SaveUpscaleTimes()
    {
        var times = new UpscaleTimes(SpeedTest.AiMachine(OnnxSessions.GpuProvider), Upscalers.Learned());
        AppSettings.Update(s => s with { UpscaleWork = times });
    }

    /// <summary>At startup: takes over saved upscaler times if they were measured on this machine.</summary>
    internal static void RestoreUpscaleTimes()
    {
        if (AppSettings.Load().UpscaleWork is { } times && times.Machine == SpeedTest.AiMachine(OnnxSessions.GpuProvider))
            Upscalers.Restore(times.SecondsPerWork);
    }

    /// <summary>
    /// Runs the model (on the worker thread). The first time, PiSA-SR builds its ONNX files;
    /// <paramref name="preparing"/> reports the start (true) and end (false) of that, and progress starts afresh afterwards.
    /// </summary>
    private UpscaleResult ComputeUpscale(MagickImage source, UpscaleMethod method, IProgress<double> progress, Action<bool>? preparing,
        CancellationToken ct, Action<UpscaleResult, SourceRect>? done = null)
    {
        var model = Upscalers.ModelOf(method) ?? throw new ArgumentException("Filter rechnet nicht.", nameof(method));
        if (method == UpscaleMethod.Generative)
        {
            var sources = PiSaSrModel.Find(Ai.Store) ?? throw new InvalidOperationException(Tr.T("Modell nicht geladen."));
            // GPU: half precision (half the memory, faster, same result); CPU: full precision, because it does not compute fp16 faster
            var half = Ai.Sessions.OnGpu;
            var build = PiSaSrModel.NeedsBuild(sources, half);
            if (build) preparing?.Invoke(true);
            var (enc, unet, dec) = PiSaSrModel.OnnxFor(sources, progress, ct, half);
            if (build) preparing?.Invoke(false);
            var sessions = (Ai.Sessions.Get(model, enc), Ai.Sessions.Get(model, unet), Ai.Sessions.Get(model, dec));
            if (half && !Ai.Sessions.OnGpu)
            {
                // The GPU could not be opened after all: use the full-precision files for the CPU.
                if (PiSaSrModel.NeedsBuild(sources)) preparing?.Invoke(true);
                (enc, unet, dec) = PiSaSrModel.OnnxFor(sources, progress, ct);
                preparing?.Invoke(false);
                sessions = (Ai.Sessions.Get(model, enc), Ai.Sessions.Get(model, unet), Ai.Sessions.Get(model, dec));
            }
            return PiSaSrRun.Run(source, sessions.Item1, sessions.Item2, sessions.Item3, progress, ct, done);
        }
        var path = Ai.Store.PathOf(model) ?? throw new InvalidOperationException(Tr.T("Modell nicht geladen."));
        return UpscaleEngine.RunTiles(source, new OnnxTileModel(Ai.Sessions.Get(model, path), model), progress, ct, done);
    }

    private static MagickImage CropOf(MagickImage doc, SourceRect r)
    {
        MagickImage clone;
        lock (doc) clone = (MagickImage)doc.Clone();
        clone.Crop(new MagickGeometry(r.X, r.Y, (uint)r.Width, (uint)r.Height));
        clone.ResetPage();
        return clone;
    }

    /// <summary>
    /// "  ·  about 4 min left, done around 15:42": from the progress, or at the very beginning (too little computed yet)
    /// from the estimate <paramref name="expected"/>.
    /// </summary>
    private static string RemainingText(TimeSpan elapsed, double fraction, TimeSpan? expected = null)
    {
        TimeSpan left;
        if (fraction >= 0.03 && elapsed.TotalSeconds >= 3)
            left = TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction * (1 - fraction));
        else if (expected is { } e && e > elapsed)
            left = e - elapsed;
        else
            return "";
        return Tr.T("  ·  noch ca. {0}", Upscalers.FormatWithEnd(left, DateTime.Now));
    }

    // ------------------------------------------------------------ Inspection view

    /// <summary>Display at <c>Factor</c> times the image size (1, 2 or 4, as much as the target size needs).</summary>
    /// <param name="After">Bitmap of the result, filled tile by tile.</param>
    /// <param name="Before">Bitmap of the filter result.</param>
    /// <param name="Factor">Display pixels per image pixel (AI: 1, 2 or 4); 0 for the filter (bitmap at target size).</param>
    /// <param name="Width">Width of the base in px.</param>
    /// <param name="Height">Height of the base in px.</param>
    /// <param name="OwnsBefore">false: Before belongs to the display (cache) and must not be disposed.</param>
    private sealed record InspectView(WriteableBitmap After, Bitmap Before, int Factor, int Width, int Height, bool OwnsBefore = true);

    private InspectView? _inspect;

    /// <summary>Size factor of the inspection view: as fine as the target, at most ×4 and about 64 megapixels.</summary>
    private int InspectFactor(int w, int h)
    {
        var (tw, th) = EffectiveTarget();
        var need = Math.Max(tw / (double)w, th / (double)h);
        var m = need <= 1 ? 1 : need <= 2 ? 2 : 4;
        while (m > 1 && (long)w * m * h * m > 64_000_000) m /= 2;
        return m;
    }

    /// <summary>
    /// Creates Before (filter) and After (initially the filter, faded) at display size, relative to the base of the last
    /// pass (for a single pass the image itself); <paramref name="region"/> in pixels of this base.
    /// </summary>
    private async Task PrepareInspectAsync(MagickImage doc, UpscalePlan plan, SourceRect? region)
    {
        var (w, h) = (plan.BaseWidth, plan.BaseHeight);
        var m = InspectFactor(w, h);
        var (filter, gamma) = (SelectedFilter.Filter, GammaCorrect);
        var before = await Task.Run(() =>
        {
            using var big = ImageOps.Resize(doc, w * m, h * m, filter, gamma);
            return ImageOps.ToDisplayBgra(big);
        });
        // After: faded until the tiles arrive; for a region only there, the rest stays like Before
        var after = (byte[])before.Clone();
        var (rx0, ry0, rx1, ry1) = region is { } r ? (r.X * m, r.Y * m, (r.X + r.Width) * m, (r.Y + r.Height) * m) : (0, 0, w * m, h * m);
        Parallel.For(ry0, ry1, ComputeLimits.Options, y =>
        {
            for (var x = rx0; x < rx1; x++)
            {
                var i = ((long)y * w * m + x) * 4;
                for (var c = 0; c < 3; c++) after[i + c] = (byte)(after[i + c] * 0.6 + 102);
            }
        });
        ClearInspect();
        _inspect = new InspectView((WriteableBitmap)BitmapOps.FromBgra(after, w * m, h * m), BitmapOps.FromBgra(before, w * m, h * m), m, w, h);
        if (UpscaleInspect || IsUpscaling) ShowInspectBitmaps();
    }

    private void ShowInspectBitmaps()
    {
        if (_inspect is not { } v) return;
        OverrideBitmap = v.After;
        OverrideBeforeBitmap = v.Before;
        RedrawToken = new object();
    }

    private void ClearInspect()
    {
        if (_inspect is { } v)
        {
            if (ReferenceEquals(OverrideBitmap, v.After)) OverrideBitmap = null;
            if (ReferenceEquals(OverrideBeforeBitmap, v.Before)) OverrideBeforeBitmap = null;
            v.After.Dispose();
            if (v.OwnsBefore) v.Before.Dispose();
        }
        _inspect = null;
    }

    /// <summary>
    /// Scales with the selected filter at full target size (at most about 64 megapixels in the display). Before is the
    /// image itself; the view places both on the same area.
    /// </summary>
    private async Task ScaleWithFilterAsync()
    {
        if (await Task.Run(() => ResultDocument) is not { } doc) return;
        var (tw, th) = EffectiveTarget();
        var k = Math.Min(1.0, Math.Sqrt(64_000_000.0 / ((long)tw * th)));
        int dw = Math.Max(1, (int)Math.Round(tw * k)), dh = Math.Max(1, (int)Math.Round(th * k));
        var (filter, gamma) = (SelectedFilter.Filter, GammaCorrect);
        ActivityText = Tr.T("Skalieren …");
        ActivityProgress = -1;
        try
        {
            var bgra = await Task.Run(() =>
            {
                using var scaled = ImageOps.Resize(doc, dw, dh, filter, gamma);
                return ImageOps.ToDisplayBgra(scaled);
            });
            var before = await DisplayBitmapForAsync(doc);
            ClearInspect();
            _inspect = new InspectView((WriteableBitmap)BitmapOps.FromBgra(bgra, dw, dh), before, 0, (int)doc.Width, (int)doc.Height, OwnsBefore: false);
            UpscaleInspect = true;
            ShowInspectBitmaps();
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Skalieren fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            ActivityText = null;
            ActivityProgress = -1;
        }
    }

    private CancellationTokenSource? _redrawCts;

    private void RedrawInspectLater() => _ = RedrawInspectAsync(delay: 200);

    /// <summary>Recomposes the whole inspection view with the current sliders (tile by tile, cancellable).</summary>
    private async Task RedrawInspectAsync(int delay = 0)
    {
        _redrawCts?.Cancel();
        var cts = _redrawCts = new CancellationTokenSource();
        if (delay > 0)
        {
            try { await Task.Delay(delay, cts.Token); } catch (OperationCanceledException) { return; }
        }
        if (_inspect is not { Factor: > 0 } view || MatchingUpscale(wholeOnly: false) is not { } e) return;
        var (f, d) = (UpscaleFidelity, UpscaleDetails);
        var (ox, oy) = e.BaseRegion is { } r ? (r.X, r.Y) : (0, 0);
        try
        {
            await Task.Run(() =>
            {
                const int step = 256;
                for (var y = 0; y < e.Result.SourceHeight; y += step)
                for (var x = 0; x < e.Result.SourceWidth; x += step)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    BlitTile(view, e.Result, new SourceRect(x, y, Math.Min(step, e.Result.SourceWidth - x), Math.Min(step, e.Result.SourceHeight - y)),
                        ox, oy, f, d);
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // newer values are already being computed
        }
    }

    /// <summary>
    /// Composes a finished area (fidelity, details), averages it down to the display size and writes it into the inspection
    /// view. Runs on the worker thread; only the copy into the bitmap happens on the UI thread.
    /// </summary>
    /// <param name="live">During the computation the neighbours are still empty, so fidelity is applied only within the finished area.</param>
    private void BlitTile(InspectView view, UpscaleResult result, SourceRect rect, int ox, int oy, double f, double d, bool live = false)
    {
        var px = result.ComposeRegion(rect, f, d, known: live ? rect : null);
        var ch = result.HasAlpha ? 4 : 3;
        var m = view.Factor;
        var k = UpscaleResult.Factor / m;
        int bw = rect.Width * m, bh = rect.Height * m, rw = rect.Width * UpscaleResult.Factor;
        var bgra = new byte[bw * bh * 4];
        var norm = 1f / (k * k * 257f);
        Parallel.For(0, bh, ComputeLimits.Options, y =>
        {
            Span<float> sum = stackalloc float[4];
            for (var x = 0; x < bw; x++)
            {
                sum.Clear();
                for (var by = 0; by < k; by++)
                for (var bx = 0; bx < k; bx++)
                {
                    var i = ((long)(y * k + by) * rw + x * k + bx) * ch;
                    for (var c = 0; c < ch; c++) sum[c] += px[i + c];
                }
                var o = (y * bw + x) * 4;
                bgra[o] = (byte)Math.Min(255, sum[2] * norm + 0.5f);
                bgra[o + 1] = (byte)Math.Min(255, sum[1] * norm + 0.5f);
                bgra[o + 2] = (byte)Math.Min(255, sum[0] * norm + 0.5f);
                bgra[o + 3] = ch == 4 ? (byte)Math.Min(255, sum[3] * norm + 0.5f) : (byte)255;
            }
        });
        int dx = (ox + rect.X) * m, dy = (oy + rect.Y) * m;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_inspect, view)) return;
            using (var fb = view.After.Lock())
            {
                for (var y = 0; y < bh; y++)
                    Marshal.Copy(bgra, y * bw * 4, fb.Address + (dy + y) * fb.RowBytes + dx * 4, bw * 4);
            }
            RedrawToken = new object();
        });
    }

    // ------------------------------------------------------------ Saving

    /// <summary>
    /// Before saving: if an upscaler is selected and the image is enlarged but not (completely) computed, asks first.
    /// Returns the ×4 image for the export, null = filter only; <c>Cancel</c> = do not save.
    /// </summary>
    private async Task<(bool Cancel, MagickImage? Upscaled)> UpscaleBeforeExportAsync()
    {
        if (!UsesAiUpscaler || !UpscaleEnlarges()) return (false, null);
        if (MatchingUpscale() is not null) return (false, await UpscaledForExportAsync());
        if (UpscaleModelMissing || ChoiceRequested is not { } ask) return (false, null);

        // Above 4×: one or two passes to choose from, each with its duration
        var (w, h) = (SourceWidth, SourceHeight);
        var (tw, th) = EffectiveTarget();
        var beyond = Upscalers.BeyondOnePass(w, h, tw, th);
        string Estimate(bool twice) =>
            Upscalers.FormatWithEnd(Upscalers.Estimate(SelectedUpscaler.Method, w, h, Upscalers.Plan(w, h, tw, th, twice)), DateTime.Now);
        var choices = beyond
            ? new List<string>
            {
                Tr.T("Zwei KI-Durchgänge berechnen und speichern (≈ {0})", Estimate(true)),
                Tr.T("Ein KI-Durchgang berechnen und speichern (≈ {0})", Estimate(false)),
            }
            : [Tr.T("Jetzt berechnen und speichern (≈ {0})", Estimate(UpscaleTwice))];
        var filterOnly = choices.Count;
        choices.Add(Tr.T("Ohne KI speichern (nur Filter)"));
        choices.Add(Tr.T("Abbrechen"));
        var message = beyond
            ? Tr.T("Für dieses Bild ist „{0}“ gewählt, aber noch nicht berechnet.", SelectedUpscaler.Name) + "\n\n" + BeyondText(w, h, tw, th)
            : Tr.T("Für dieses Bild ist „{0}“ gewählt, aber noch nicht berechnet. Das dauert etwa {1}.", SelectedUpscaler.Name, Estimate(UpscaleTwice));
        var answer = await ask(new ChoiceRequest(Tr.T("Upscaling noch nicht berechnet"), message, choices, beyond && !UpscaleTwice ? 1 : 0));
        if (answer == filterOnly) return (false, null);
        if (answer < 0 || answer > filterOnly) return (true, null);
        if (beyond) UpscaleTwice = answer == 0;
        if (await Task.Run(() => (ResultDocument, CurrentUpscaleKey())) is not ({ } doc, { } key)) return (true, null);
        if (!await RunUpscaleAsync(doc, key, null)) return (true, null);
        return (false, await UpscaledForExportAsync());
    }

    // ------------------------------------------------------------ Export of several images

    /// <summary>Determines whether upscaling still has to be computed for this file with its state.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="width">Width of the image in px.</param>
    /// <param name="height">Height of the image in px.</param>
    /// <param name="s">Settings state of the file.</param>
    /// <param name="o">Export options for that state.</param>
    /// <returns>The approximate duration, or null if nothing has to be computed.</returns>
    internal TimeSpan? UpscaleNeededFor(string path, int width, int height, SettingsState s, ExportOptions o)
    {
        if (s.Upscaler == UpscaleMethod.Filter || (o.Width <= width && o.Height <= height)) return null;
        if (Upscalers.ModelOf(s.Upscaler) is not { } m || !UpscaleModelInstalled(m)) return null;   // without a model: filter
        if (path == CurrentPath && SelectedUpscaler.Method == s.Upscaler && UpscaleTwice == s.UpscaleTwice && MatchingUpscale() is not null)
            return null;
        return Upscalers.Estimate(s.Upscaler, width, height, Upscalers.Plan(width, height, o.Width, o.Height, s.UpscaleTwice));
    }

    /// <summary>
    /// When exporting a file: the ×4 image for its state, either the computed one of the open image or newly computed
    /// (without a view). <c>Owned</c> = the caller disposes it. null = filter only.
    /// </summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="image">The image to upscale.</param>
    /// <param name="s">Settings state of the file.</param>
    /// <param name="o">Export options for that state.</param>
    /// <param name="progress">Progress reports; null for none.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <returns>The upscaled image and whether the caller owns it, or null.</returns>
    internal async Task<(MagickImage Image, bool Owned)?> UpscaleForBatchAsync(string path, MagickImage image, SettingsState s, ExportOptions o,
        IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        int w, h;
        lock (image) (w, h) = ((int)image.Width, (int)image.Height);
        if (s.Upscaler == UpscaleMethod.Filter || (o.Width <= w && o.Height <= h)) return null;
        // An own copy (it shares the pixels until written): a slider move during the export composes a new ×4 image and
        // puts this one into the trash, which the preview empties – it must not be released while being saved (No. 101)
        if (path == CurrentPath && SelectedUpscaler.Method == s.Upscaler && UpscaleTwice == s.UpscaleTwice && MatchingUpscale() is not null
            && await UpscaledForExportAsync() is { } ready)
        {
            return (ImageOps.CloneOf(ready), true);
        }
        if (Upscalers.ModelOf(s.Upscaler) is not { } m || !UpscaleModelInstalled(m)) return null;

        var plan = Upscalers.Plan(w, h, o.Width, o.Height, s.UpscaleTwice);
        var expected = Upscalers.Estimate(s.Upscaler, w, h, plan);
        var share = plan.Passes == 2 && expected > TimeSpan.Zero ? Upscalers.Estimate(s.Upscaler, w, h) / expected : 1;
        var clock = Stopwatch.StartNew();
        Progress<double> Step(double from, double span) => new(f =>
        {
            var all = from + f * span;
            progress?.Report(new WorkProgress(all, Tr.T("Upscaling (KI) · {0:0} %{1}", all * 100, RemainingText(clock.Elapsed, all, expected))));
        });
        var (first, second) = (Step(0, share), Step(share, 1 - share));
        var composed = await Task.Run(() =>
        {
            var result = ComputePlanned(image, s.Upscaler, plan.Passes, plan.BaseWidth, plan.BaseHeight, s.UpscaleFidelity, s.UpscaleDetails,
                s.GammaCorrect, first, second, prepare => { if (!prepare) clock.Restart(); }, ct);
            return result.Compose(s.UpscaleFidelity, s.UpscaleDetails, ct);
        }, ct);
        return (composed, true);
    }

    /// <summary>GPU switched on/off: a different time estimate; an existing result stays valid.</summary>
    internal void GpuChanged()
    {
        OnPropertyChanged(nameof(ComputeDeviceText));
        OnPropertyChanged(nameof(ScaleButtonText));
        UpdateUpscaleStatus();
    }

    /// <summary>Image switched or closed: cancels a running upscaling and closes the inspection view; the result stays (it applies only to its own image).</summary>
    private void UpscaleImageChanged()
    {
        if (IsUpscaling) _upscaleCts?.Cancel();
        UpscaleInspect = false;
        ClearInspect();
        UpdateUpscaleStatus();
    }
}
