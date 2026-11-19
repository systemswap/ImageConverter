using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.ViewModels;
using ImageMagick;

namespace ImageConverter.Ai;

/// <summary>What the mouse does in the "Hintergrund" tab.</summary>
public enum BackgroundTool
{
    /// <summary>Nothing: the model finds the subject by itself; the mouse works as usual (before/after divider, crop).</summary>
    Auto,

    /// <summary>Red brush: what it touches goes.</summary>
    Remove,

    /// <summary>Green brush: what it touches stays.</summary>
    Keep,
}

/// <summary>
/// "Hintergrund" tab: the model finds the subject automatically; the background becomes transparent or blurred. Rough red
/// and green strokes correct the result: red removes whole objects, green keeps them (and adds what a zoomed run finds).
/// See docs/KI-HINTERGRUND.md.
/// </summary>
/// <remarks>
/// The model works on the displayed area of the image (after crop). Its mask is kept: changing strokes, mode or blur
/// computes again in a few seconds without the model. As with "Entfernen", a new attempt starts from the image before the
/// last one instead of building on its result.
/// </remarks>
public partial class BackgroundViewModel : ViewModelBase, IBrushTool
{
    private readonly MainViewModel _main;
    private readonly AiViewModel _ai;
    private CancellationTokenSource? _runCts;

    public BackgroundViewModel(MainViewModel main, AiViewModel ai)
    {
        _main = main;
        _ai = ai;
        _ai.ModelsChanged += (_, _) => Rescan();
        _main.PropertyChanged += OnMainPropertyChanged;
        // Every image step, undo, redo and image switch rebuilds the "Edits" list: the shown image may have changed
        _main.EditRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AgainHint));
            OnPropertyChanged(nameof(OrderHint));
        };
        _main.FilesReplaced += (_, _) =>
        {
            ClearAll();
            _mask = null;   // the images are gone; the mask held one of them
        };
        Rescan();
    }

    // ------------------------------------------------------------ Models

    /// <summary>Installed background models (BiRefNet also when only its graphics-card add-on is there).</summary>
    public ObservableCollection<AiModel> Available { get; } = [];

    public bool HasModels => Available.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelHint), nameof(AgainHint))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial AiModel? SelectedModel { get; set; }

    public string? ModelHint => SelectedModel is { } m ? Tr.T(m.Description) : null;

    private void Rescan()
    {
        var store = _ai.Store;
        var usable = AiModelCatalog.All
            .Where(m => m.Kind == AiModelKind.Background && m != AiModelCatalog.BiRefNetGeneralGpu
                        && (store.IsInstalled(m) || m == AiModelCatalog.BiRefNetGeneral && store.IsInstalled(AiModelCatalog.BiRefNetGeneralGpu)))
            .ToList();
        var selected = SelectedModel;
        Available.Clear();
        foreach (var m in usable) Available.Add(m);
        SelectedModel = selected is not null && usable.Contains(selected) ? selected : usable.FirstOrDefault();
        OnPropertyChanged(nameof(HasModels));
    }

    /// <summary>
    /// The file that actually runs: BiRefNet in half precision when the graphics card computes (or when only that file is
    /// there), otherwise the selected model itself.
    /// </summary>
    private (AiModel Model, string Path)? Resolve(AiModel model)
    {
        var store = _ai.Store;
        if (model == AiModelCatalog.BiRefNetGeneral && store.PathOf(AiModelCatalog.BiRefNetGeneralGpu) is { } half
            && (_ai.Sessions.OnGpu || !store.IsInstalled(model)))
            return (AiModelCatalog.BiRefNetGeneralGpu, half);
        return store.PathOf(model) is { } path ? (model, path) : null;
    }

    /// <summary>Opens the settings on the "Hintergrund" page for downloading models.</summary>
    [RelayCommand]
    private Task OpenModels() => _main.OpenSettingsCommand.ExecuteAsync(SettingsPage.Background);

    // ------------------------------------------------------------ Tab and tool

    /// <summary>Whether the "Hintergrund" tab is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushing))]
    public partial bool IsTabActive { get; set; }

    partial void OnIsTabActiveChanged(bool value)
    {
        if (!value) return;
        // Painting happens on the unrotated image: close the rotation, perspective and dewarp tools and the eyedropper.
        _main.RotationTool = false;
        _main.PerspectiveTool = false;
        _main.DewarpTool = false;
        _main.IsPicking = false;
    }

    /// <summary>Automatic, red or green brush.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushing), nameof(BrushKeep), nameof(IsAuto), nameof(IsRemoveBrush), nameof(IsKeepBrush), nameof(ToolHint), nameof(StrokeStrength))]
    public partial BackgroundTool Tool { get; set; }

    public bool IsAuto
    {
        get => Tool == BackgroundTool.Auto;
        set { if (value) Tool = BackgroundTool.Auto; }
    }

    public bool IsRemoveBrush
    {
        get => Tool == BackgroundTool.Remove;
        set { if (value) Tool = BackgroundTool.Remove; }
    }

    public bool IsKeepBrush
    {
        get => Tool == BackgroundTool.Keep;
        set { if (value) Tool = BackgroundTool.Keep; }
    }

    public string ToolHint => Tool switch
    {
        BackgroundTool.Remove => Tr.T("Rot: grob über etwas malen, das weg soll – das ganze Objekt, das der Strich berührt, verschwindet."),
        BackgroundTool.Keep => Tr.T("Grün: über etwas malen, das bleiben soll – was grün übermalt ist, bleibt in jedem Fall (scharf bzw. sichtbar). Berührt der Strich ein Objekt, bleibt es ganz; fehlt ein Teil des Motivs, wird um den Strich genauer nachgerechnet."),
        _ => Tr.T("Das Motiv wird ohne Markierung erkannt. Passt etwas nicht: mit Rot oder Grün grob nachbessern und noch einmal anwenden."),
    };

    /// <summary>Whether the brush paints: a brush chosen, tab selected, an image loaded, no computation running.</summary>
    public bool IsBrushing
    {
        get => IsTabActive && Tool != BackgroundTool.Auto && _main.HasImage && !IsRunning;
        set { }   // Esc in the preview does not end the brush; the tab does that
    }

    /// <summary>Green brush: new strokes keep.</summary>
    public bool BrushKeep => Tool == BackgroundTool.Keep;

    /// <summary>Brush size as a diameter in screen pixels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushSizeLabel))]
    public partial double BrushSize { get; set; } = 40;

    public string BrushSizeLabel => $"{BrushSize:0} px";

    /// <summary>
    /// Strength of new green strokes in percent: how much of what they cover stays (a railing at 40 %: 40 % sharp in the
    /// blur mode, 40 % visible in the transparent mode). It belongs to the selected brush layer: changing it changes all strokes
    /// of that layer (docs/KI-HINTERGRUND.md, „Bedienung“).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepStrengthLabel), nameof(StrokeStrength))]
    public partial double KeepStrength { get; set; } = 100;

    partial void OnKeepStrengthChanged(double value)
    {
        if (_syncing || _layer is not { Keep: true } layer) return;
        layer.Strength = value;
        RestyleLayer(layer);
    }

    public string KeepStrengthLabel => $"{KeepStrength:0} %";

    /// <summary>
    /// Hardness in percent (red and green): 100 % a hard edge; less lets the effect fall off radially from the middle of the
    /// brush to its edge. It belongs to the selected brush layer (docs/KI-HINTERGRUND.md, „Bedienung“).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushHardnessLabel), nameof(StrokeHardness))]
    public partial double BrushHardness { get; set; } = 100;

    partial void OnBrushHardnessChanged(double value)
    {
        if (_syncing || _layer is not { } layer) return;
        layer.Hardness = value;
        RestyleLayer(layer);
    }

    // ------------------------------------------------------------ Brush layers

    /// <summary>
    /// Brush layers: strokes of one layer share colour, strength and hardness and form one mask; green layers add up (two at
    /// 30 % keep 60 %), red ones combine. Selecting a layer continues it with its values (docs/KI-HINTERGRUND.md, „Bedienung“).
    /// </summary>
    public ObservableCollection<BrushLayer> Layers { get; } = [];

    public bool HasLayers => Layers.Count > 0;

    private BrushLayer? _layer;   // the layer new strokes go into
    private int _nextLayer = 1;
    private bool _syncing;        // the sliders follow a selected layer, not the other way round

    /// <summary>Starts a new layer with the current colour, strength and hardness; the next strokes go into it.</summary>
    [RelayCommand]
    private void NewLayer()
    {
        if (Tool == BackgroundTool.Auto) return;
        Select(AddLayer(Tool == BackgroundTool.Keep));
    }

    private BrushLayer AddLayer(bool keep)
    {
        var layer = new BrushLayer(this, _nextLayer++, keep, keep ? KeepStrength : 100, BrushHardness);
        Layers.Add(layer);
        OnPropertyChanged(nameof(HasLayers));
        return layer;
    }

    /// <summary>Continues a layer: colour, strength and hardness follow it, new strokes go into it.</summary>
    internal void Select(BrushLayer layer)
    {
        _layer = layer;
        _syncing = true;
        try
        {
            Tool = layer.Keep ? BackgroundTool.Keep : BackgroundTool.Remove;
            BrushHardness = layer.Hardness;
            if (layer.Keep) KeepStrength = layer.Strength;
        }
        finally
        {
            _syncing = false;
        }
        foreach (var l in Layers) l.IsSelected = ReferenceEquals(l, layer);
        DropEmptyLayers();
    }

    /// <summary>Removes a layer with its strokes.</summary>
    internal void Remove(BrushLayer layer)
    {
        Strokes = Strokes.Where(s => s.Layer != layer.Id).ToArray();
        Layers.Remove(layer);
        if (ReferenceEquals(_layer, layer)) _layer = null;
        StrokesApplied = false;
        OnPropertyChanged(nameof(HasLayers));
    }

    /// <summary>Layers without strokes that are not being painted go away.</summary>
    private void DropEmptyLayers()
    {
        foreach (var empty in Layers.Where(l => l.Count == 0 && !ReferenceEquals(l, _layer)).ToList()) Layers.Remove(empty);
        OnPropertyChanged(nameof(HasLayers));
    }

    /// <summary>A layer's strength or hardness changed: all its strokes take it (shown again until applied).</summary>
    private void RestyleLayer(BrushLayer layer)
    {
        Strokes = Strokes.Select(s => s.Layer == layer.Id ? s with { Strength = layer.Keep ? layer.Strength / 100 : 1, Hardness = layer.Hardness / 100 } : s).ToArray();
        if (layer.Count > 0) StrokesApplied = false;
    }

    private void ResetLayers()
    {
        Layers.Clear();
        _layer = null;
        _nextLayer = 1;
        OnPropertyChanged(nameof(HasLayers));
    }

    partial void OnToolChanged(BackgroundTool value)
    {
        // Other colour: continue its last layer, or start one with the first stroke
        if (_syncing || value == BackgroundTool.Auto || _layer is { } l && l.Keep == (value == BackgroundTool.Keep)) return;
        if (Layers.LastOrDefault(x => x.Keep == (value == BackgroundTool.Keep)) is { } last) Select(last);
        else
        {
            _layer = null;
            foreach (var x in Layers) x.IsSelected = false;
            DropEmptyLayers();
        }
    }

    public string BrushHardnessLabel => $"{BrushHardness:0} %";

    /// <inheritdoc />
    public double StrokeHardness => BrushHardness / 100;

    /// <inheritdoc />
    public double StrokeStrength => BrushKeep ? KeepStrength / 100 : 1;

    /// <summary>All strokes, oldest first, in the displayed image (a new list on every change so that the preview updates).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStrokes))]
    [NotifyCanExecuteChangedFor(nameof(UndoStrokeCommand), nameof(ClearStrokesCommand))]
    public partial IReadOnlyList<BrushStroke> Strokes { get; set; } = [];

    public bool HasStrokes => Strokes.Count > 0;

    /// <summary>The strokes have been applied; hidden in the preview until something is added.</summary>
    [ObservableProperty] public partial bool StrokesApplied { get; set; }

    System.Windows.Input.ICommand IBrushTool.AddStrokeCommand => AddStrokeCommand;

    [RelayCommand]
    private void AddStroke(BrushStroke stroke)
    {
        if (Strokes.Count == 0) _paintedOn = _main.ShownGeometry;
        // Into the selected layer of this colour, or a new one with the current values
        if (_layer is null || _layer.Keep != stroke.Keep) Select(AddLayer(stroke.Keep));
        var layer = _layer!;
        stroke = stroke with { Layer = layer.Id, Hardness = layer.Hardness / 100, Strength = layer.Keep ? layer.Strength / 100 : 1 };
        layer.Count++;
        Strokes = [.. Strokes, stroke];
        StrokesApplied = false;
        Status = null;
    }

    private bool CanEditStrokes => HasStrokes && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanEditStrokes))]
    private void UndoStroke()
    {
        var last = Strokes[^1];
        Strokes = Strokes.Take(Strokes.Count - 1).ToArray();
        if (Layers.FirstOrDefault(l => l.Id == last.Layer) is { } layer)
        {
            layer.Count--;
            if (layer.Count == 0 && !ReferenceEquals(layer, _layer)) DropEmptyLayers();
        }
        StrokesApplied = false;
    }

    [RelayCommand(CanExecute = nameof(CanEditStrokes))]
    private void ClearStrokes()
    {
        Strokes = [];
        ResetLayers();
        StrokesApplied = false;
        Status = null;
    }

    // ------------------------------------------------------------ Mode

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransparent), nameof(IsBlur), nameof(ApplyText), nameof(ShowFormatHint))]
    public partial BackgroundMode Mode { get; set; }

    public bool IsTransparent
    {
        get => Mode == BackgroundMode.Transparent;
        set { if (value) Mode = BackgroundMode.Transparent; }
    }

    public bool IsBlur
    {
        get => Mode == BackgroundMode.Blur;
        set { if (value) Mode = BackgroundMode.Blur; }
    }

    /// <summary>Strength of the blur in percent; computed only on "Anwenden" (not while dragging).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlurLabel))]
    public partial double BlurStrength { get; set; } = 50;

    public string BlurLabel => $"{BlurStrength:0} %";

    public string ApplyText => Mode == BackgroundMode.Blur ? Tr.T("Hintergrund unscharf machen") : Tr.T("Hintergrund entfernen");

    /// <summary>Transparent, but the output format has no transparency (JPEG, BMP): the background becomes white there.</summary>
    public bool ShowFormatHint => Mode == BackgroundMode.Transparent && _main.SelectedFormat is "JPEG" or "BMP";

    /// <summary>Switches the output format to PNG, which keeps the transparency.</summary>
    [RelayCommand]
    private void UsePng() => _main.SelectedFormat = "PNG";

    // ------------------------------------------------------------ Computation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushing))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(CancelCommand), nameof(UndoStrokeCommand), nameof(ClearStrokesCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial string? Status { get; set; }

    /// <summary>
    /// After a background step: what the next run starts from. It always starts from the image before that step, never from
    /// the result, even with other image steps (AI, removal) in between; those are then not part of the new result.
    /// </summary>
    /// <summary>
    /// Recommended order of the image steps: background, removal, AI enhancement. Shown when removal or AI came first; the
    /// order stays the user's choice (docs/KI-HINTERGRUND.md, "Reihenfolge in der Kette").
    /// </summary>
    public string? OrderHint =>
        _main.LastBackground() is null && (_main.LastStep(ImageStepKind.Ai) is not null || _main.LastStep(ImageStepKind.Retouch) is not null)
            ? Tr.T("Tipp: Den Hintergrund am besten zuerst – das Modell erkennt das Motiv am unbearbeiteten Foto am sichersten, und die Regler rechnen danach nur im sichtbaren Bereich.")
            : null;

    public string? AgainHint => _main.LastBackground() switch
    {
        null => null,
        { Later: 0 } last => Tr.T("Zu sehen: Ergebnis von {0}. Anwenden rechnet neu vom Bild vor dem Hintergrund-Schritt.", last.Detail),
        { } last => Tr.T("Zu sehen: Ergebnis von {0}, danach weitere Bildschritte ({1}). Anwenden rechnet neu vom Bild vor dem Hintergrund-Schritt, die späteren Schritte sind dann nicht enthalten (Strg+Z holt sie zurück).", last.Detail, last.Later),
    };

    /// <summary>Mask of the last run (per image, model and area): new strokes, mode or blur need no new model run.</summary>
    private (MagickImage Image, string ModelId, BackgroundMask Mask)? _mask;

    /// <summary>Seconds the model took last time, per model file (for the time shown while it runs).</summary>
    private readonly Dictionary<string, double> _seconds = [];

    private bool CanApply => SelectedModel is not null && !IsRunning && _main.HasImage && !_main.IsEditing;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        if (SelectedModel is not { } chosen || _main.CurrentImage is not { } current) return;
        if (Resolve(chosen) is not var (model, path))
        {
            Status = Tr.T("Modell fehlt — bitte in den Einstellungen herunterladen.");
            return;
        }
        // Image steps switched off: the step works on what is displayed (No. 100)
        if (!await _main.TakeOverHiddenStepsAsync()) return;
        current = _main.CurrentImage ?? current;

        // A new attempt starts from the image before the last background step, not from its result
        var earlier = _main.LastBackground();
        var start = earlier?.Base ?? current;
        var discards = earlier?.Later ?? 0;
        int pw, ph;
        lock (start) (pw, ph) = ((int)start.Width, (int)start.Height);
        int nowW, nowH;
        lock (current) (nowW, nowH) = ((int)current.Width, (int)current.Height);
        if ((pw, ph) != (nowW, nowH))
        {
            // Enlarged by the AI after the background step: crop and strokes refer to the other size
            Status = Tr.T("Das Bild wurde nach dem Hintergrund-Schritt in der Größe geändert. Bitte erst mit Strg+Z bis dorthin zurückgehen.");
            return;
        }
        var region = ShownRegion(pw, ph);
        var strokes = Strokes.Select(ToPixels).ToArray();
        var cached = _mask is { } m && ReferenceEquals(m.Image, start) && m.ModelId == model.Id && m.Mask.Region == region ? m.Mask : null;
        var (mode, blur) = (Mode, BlurStrength / 100);

        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        AppLog.Activity($"Hintergrund: {model.Name}");
        var clock = Stopwatch.StartNew();
        Status = earlier is not null ? Tr.T("Rechnet mit {0} neu vom Bild vor dem Hintergrund-Schritt …", Tr.T(chosen.Name)) : Tr.T("Wird berechnet …");
        // Show the image the new attempt starts from right away instead of the previous result
        var shownOriginal = earlier is not null ? await ShowOriginalAsync(start) : null;
        var expected = _seconds.TryGetValue(model.Id, out var s) ? s : Expected(model);
        var phase = cached is null ? Tr.T("Hintergrund · Motiv wird erkannt (etwa {0:0} s) …", expected) : Tr.T("Hintergrund · Kanten …");
        _ai.ShowActivity(phase, 0, CancelCommand);
        // The model reports no progress: show the expected time running down
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (IsRunning)
                _ai.ShowActivity(phase, Math.Min(95, clock.Elapsed.TotalSeconds / Math.Max(1, expected) * 100), CancelCommand);
        };
        if (cached is null) timer.Start();
        try
        {
            BackgroundMask? mask = cached;
            // The same background step on another image (an earlier step switched off): the remembered mask with the
            // strokes applied, the edges and colours from the new image; no model run (No. 102)
            (BackgroundMask Mask, float[] Alpha, float[]? Kept)? used = null;
            MainViewModel.StepReplay replay = (input, _, ct) => used is var (m, a, k)
                ? BackgroundRemover.Compose(input, BackgroundRemover.Rebase(m, input), a, mode, blur, ct, k)
                : throw new InvalidOperationException(Tr.T("Hintergrund-Schritt ohne Maske."));
            var result = await _main.ApplyBackgroundAsync(_ =>
            {
                // Opened only when needed: with the remembered mask, red strokes, mode and strength need no model
                OnnxSegmentModel Segment() => new(_ai.Sessions.Get(model, path), model);
                if (mask is null)
                {
                    var t = Stopwatch.StartNew();
                    mask = BackgroundRemover.Detect(start, region, Segment(), cts.Token);
                    _seconds[model.Id] = t.Elapsed.TotalSeconds;
                }
                Dispatcher.UIThread.Post(() => phase = Tr.T("Hintergrund · Kanten …"));
                var alpha = BackgroundRemover.Edit(mask, strokes, r => BackgroundRemover.Detect(start, r, Segment(), cts.Token), out var kept);
                used = (mask with { Guide = [] }, alpha, kept);   // the guide comes from the image a replay works on
                return BackgroundRemover.Compose(start, mask, alpha, mode, blur, cts.Token, kept);
            }, mode == BackgroundMode.Blur ? Tr.T("Hintergrund unscharf") : Tr.T("Hintergrund entfernt"),
               mode == BackgroundMode.Blur ? $"{Tr.T(chosen.Name)} · {BlurStrength:0} %" : Tr.T(chosen.Name), start, earlier is { Later: 0 }, discards, replay, current);

            if (result is not null)
            {
                if (mask is not null) _mask = (start, model.Id, mask);
                StrokesApplied = strokes.Length > 0;
                Status = earlier is not null
                    ? Tr.T("Mit {0} neu vom Bild vor dem Hintergrund-Schritt gerechnet ({1:0.0} s). Rückgängig mit Strg+Z.",
                        Tr.T(chosen.Name), clock.Elapsed.TotalSeconds)
                    : Tr.T("Fertig nach {0:0.0} s — rückgängig mit Strg+Z. Nachbessern mit Rot oder Grün, dann noch einmal anwenden: rechnet vom Bild davor und ohne neues Erkennen.",
                        clock.Elapsed.TotalSeconds);
                OnPropertyChanged(nameof(AgainHint));
            }
            else
            {
                Status = cts.IsCancellationRequested ? Tr.T("Abgebrochen.") : Tr.T("Fehlgeschlagen — siehe Meldung oben.");
            }
        }
        catch (Exception ex)
        {
            Status = Tr.T("Fehler: {0}", ex.Message);
        }
        finally
        {
            timer.Stop();
            // On the graphics card the runtime keeps all its memory until the session closes (up to 15.6 GB, No. 92)
            if (_ai.Sessions.OnGpu && model.Gpu) _ai.Sessions.Release(model.Id);
            if (shownOriginal is not null && ReferenceEquals(_main.OverrideBitmap, shownOriginal))
            {
                _main.OverrideBitmap = null;   // the new result (or, after cancelling, the previous one) is shown again
                _main.OverrideBeforeBitmap = null;
            }
            _runCts = null;
            IsRunning = false;
            AppLog.Activity(null);
            _ai.HideActivity();
        }
    }

    /// <summary>
    /// Shows the image a new attempt starts from in the preview while it computes, displayed with the current geometry,
    /// without adjustments.
    /// </summary>
    /// <returns>The bitmap shown, or null if the image could not be prepared.</returns>
    private async Task<Avalonia.Media.Imaging.Bitmap?> ShowOriginalAsync(MagickImage start)
    {
        var geometry = _main.ShownGeometry;
        var preview = _main.PreviewSize();
        int pw, ph;
        lock (start) (pw, ph) = ((int)start.Width, (int)start.Height);
        var size = AiPreviewSize.Fit(pw, ph, Math.Max(preview.Width, preview.Height)) with { Filter = preview.Filter, GammaCorrect = preview.GammaCorrect };
        try
        {
            var shown = await Task.Run(() =>
            {
                // No lock around it: Resize locks the image itself and works in parallel from 1 MP on (No. 91)
                return MainViewModel.ShowWithGeometry(AiViewModel.Original(start, size), 0, false, geometry.Recipe, geometry.Stages);
            });
            var bitmap = ImageConverter.Imaging.BitmapOps.FromBgra(shown.Bgra, shown.Width, shown.Height);
            _main.OverrideBitmap = bitmap;
            _main.OverrideBeforeBitmap = bitmap;
            return bitmap;
        }
        catch (Exception ex) when (ex is MagickException or InvalidOperationException)
        {
            return null;   // only a display aid: the computation itself goes on
        }
    }

    /// <summary>Time of one model run measured on a notebook with integrated graphics (docs/KI-HINTERGRUND.md).</summary>
    private double Expected(AiModel model) =>
        (model.Id, _ai.Sessions.OnGpu && model.Gpu) switch
        {
            ("birefnet-lite", _) => 11,
            ("ben2", _) => 17,
            ("birefnet-general-gpu", true) => 12,
            (_, _) => 24,
        };

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>The displayed area (after crop, perspective …) as a rectangle of the pixel image: the model looks only there.</summary>
    private SourceRect ShownRegion(int pw, int ph)
    {
        var (w, h) = (_main.DocumentSize.Width, _main.DocumentSize.Height);
        if (w <= 0 || h <= 0) return new SourceRect(0, 0, pw, ph);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        // Points along the border: with perspective or a dewarped page the edges are curved in the pixel image
        for (var i = 0; i <= 16; i++)
        {
            var f = i / 16.0;
            foreach (var (x, y) in new[] { (f * w, 0.0), (f * w, (double)h), (0.0, f * h), ((double)w, f * h) })
            {
                var (px, py) = _main.ShownToPixels(x, y);
                (x0, y0, x1, y1) = (Math.Min(x0, px), Math.Min(y0, py), Math.Max(x1, px), Math.Max(y1, py));
            }
        }
        var left = Math.Clamp((int)Math.Floor(x0), 0, pw - 1);
        var top = Math.Clamp((int)Math.Floor(y0), 0, ph - 1);
        var right = Math.Clamp((int)Math.Ceiling(x1), left + 1, pw);
        var bottom = Math.Clamp((int)Math.Ceiling(y1), top + 1, ph);
        return new SourceRect(left, top, right - left, bottom - top);
    }

    /// <summary>Maps a stroke from the displayed image to the pixel image (see <see cref="RetouchViewModel"/>).</summary>
    private BrushStroke ToPixels(BrushStroke s)
    {
        var points = s.Points.Select(p => _main.ShownToPixels(p.X, p.Y)).ToArray();
        var radius = s.Points.Count == 0 ? s.Radius : s.Points.Average(p => _main.ShownLengthToPixels(p.X, p.Y, s.Radius));
        return s with { Points = points, Radius = radius };
    }

    // ------------------------------------------------------------ Image change

    /// <summary>Another image: its strokes and status go; what a new attempt starts from stays in the history of the image.</summary>
    private void ClearAll()
    {
        Strokes = [];
        ResetLayers();
        StrokesApplied = false;
        Status = null;
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HasImage):
                OnPropertyChanged(nameof(IsBrushing));
                ApplyCommand.NotifyCanExecuteChanged();
                break;
            case nameof(MainViewModel.IsEditing):
                ApplyCommand.NotifyCanExecuteChanged();
                break;
            case nameof(MainViewModel.SelectedFormat):
                OnPropertyChanged(nameof(ShowFormatHint));
                break;
            case nameof(MainViewModel.CurrentFileName):
                // Also while computing: the run took its strokes along, these belong to the previous image (No. 98)
                ClearAll();
                break;
            case nameof(MainViewModel.DocumentSize):
                // Displayed dimensions changed: strokes lie in the displayed image and no longer fit
                var now = (_main.DocumentSize.Width, _main.DocumentSize.Height);
                if (_size is { } size && size != now)
                {
                    Strokes = [];
                    ResetLayers();
                    StrokesApplied = false;
                }
                _size = now;
                break;
            case nameof(MainViewModel.ShownGeometry):
                // Flipped, rotated by 180°, a geometry eye or its undo: same dimensions, but the strokes would hit other
                // places (No. 98)
                if (Strokes.Count > 0 && _paintedOn is { } painted && painted != _main.ShownGeometry)
                {
                    Strokes = [];
                    ResetLayers();
                    StrokesApplied = false;
                    Status = Tr.T("Die Markierung passte nach Drehen, Spiegeln oder Zuschneiden nicht mehr und wurde entfernt.");
                }
                break;
        }
    }

    /// <summary>Geometry of the displayed image when the first of the current strokes was painted.</summary>
    private (GeometryRecipe Recipe, GeometryStages Stages)? _paintedOn;

    private (int W, int H)? _size;
}

/// <summary>A brush layer of the "Hintergrund" tab: its strokes share colour, strength and hardness and form one mask.</summary>
public sealed partial class BrushLayer : ObservableObject
{
    private readonly BackgroundViewModel _owner;

    internal BrushLayer(BackgroundViewModel owner, int id, bool keep, double strength, double hardness)
    {
        _owner = owner;
        (Id, Keep, Strength, Hardness) = (id, keep, strength, hardness);
    }

    /// <summary>Number of the layer, also stored in its strokes.</summary>
    public int Id { get; }

    /// <summary>Green (stays) instead of red (goes).</summary>
    public bool Keep { get; }

    /// <summary>Strength in percent (green only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial double Strength { get; set; }

    /// <summary>Hardness in percent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial double Hardness { get; set; }

    /// <summary>Number of strokes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial int Count { get; set; }

    /// <summary>New strokes go into this layer.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public string Name => Tr.T("Ebene {0}", Id);

    /// <summary>Colour of the name: green or red like the brush.</summary>
    public Avalonia.Media.IBrush Swatch => Keep ? GreenText : RedText;

    private static readonly Avalonia.Media.IBrush GreenText = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0x2F, 0xA8, 0x4F));
    private static readonly Avalonia.Media.IBrush RedText = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0x40, 0x40));

    public string Detail
    {
        get
        {
            var strokes = Count == 1 ? Tr.T("1 Strich") : Tr.T("{0} Striche", Count);
            return Keep
                ? Tr.T("Grün · {0:0} % · Härte {1:0} % · {2}", Strength, Hardness, strokes)
                : Tr.T("Rot · Härte {0:0} % · {1}", Hardness, strokes);
        }
    }

    /// <summary>Continue this layer.</summary>
    [RelayCommand]
    private void Choose() => _owner.Select(this);

    /// <summary>Remove this layer with its strokes.</summary>
    [RelayCommand]
    private void Delete() => _owner.Remove(this);
}
