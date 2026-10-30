using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// Cropping, rotating, flipping and the undo history for them.
public partial class MainViewModel
{
    /// <summary>
    /// One step in the history: the state <b>before</b> the change. For image changes
    /// (AI, removal) <see cref="Image"/> is the image before and
    /// <see cref="FilePath"/> the file; for geometry changes (rotation, crop …) <see cref="Recipe"/> is the recipe
    /// before; for pure settings changes <see cref="Image"/> and <see cref="Recipe"/> are null.
    /// </summary>
    private sealed record Snapshot(string? FilePath, MagickImage? Image, SettingsState Settings,
        bool? ApplyToAll = null, IReadOnlyDictionary<string, SettingsState>? FileSettings = null,
        SettingsState? Template = null, MainViewModel.ImageStep? Step = null, GeometryRecipe? Recipe = null);

    /// <summary>The image of one file: original, current pixel image and the geometry as a recipe.</summary>
    private sealed class EditHistory(MagickImage original)
    {
        public MagickImage Original { get; } = original;

        /// <summary>Pixel image: the original plus AI and removal edits, not rotated and not cropped.</summary>
        public MagickImage Current { get; set; } = original;

        /// <summary>Geometry (perspective, levelling, crop, rotate/flip); applied only when displaying and saving.</summary>
        public GeometryRecipe Recipe { get; set; } = GeometryRecipe.None;

        public long FileBytes { get; init; }
        public string FormatName { get; init; } = "";
        public double Dpi { get; init; } = ImageLoader.DefaultDpi;

        /// <summary>Differs from the loaded image (cropped, rotated, AI …).</summary>
        public bool IsEdited => !ReferenceEquals(Current, Original) || !Recipe.IsNone;
    }

    // One shared history for everything: settings steps and image changes both remember
    // their file. "Undo" always reverts the latest step, switching to another
    // image if necessary.
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    private readonly Dictionary<string, EditHistory> _histories = new(StringComparer.Ordinal);
    private EditHistory? _history;

    /// <summary>Rotating and flipping: only with an image and not while upscaling is running.</summary>
    public bool CanTransform => HasImage && !IsUpscaling;

    // Not during an editing step (for example AI): its result belongs to the image before.
    public bool CanUndo => !IsEditing && !IsUpscaling && (_undo.Count > 0 || HasPendingSettings);
    public bool CanRedo => !IsEditing && !IsUpscaling && _redo.Count > 0 && !HasPendingSettings;

    [ObservableProperty] public partial bool IsEditing { get; set; }

    /// <summary>
    /// The history an editing step is computing on. The step reads its images on a worker thread while the user may
    /// switch to another image or open new ones; those images must stay alive until it ends (No. 85).
    /// </summary>
    private EditHistory? _editingHistory;

    /// <summary>Images released while an editing step was running; disposed when it ends (No. 85).</summary>
    private readonly List<MagickImage> _disposeAfterEdit = [];

    partial void OnIsEditingChanged(bool value)
    {
        NotifyUndoRedo();
        // Upscaling, saving and AI straightening read the image a running step is about to replace (No. 98)
        UpscaleCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        PrintCommand.NotifyCanExecuteChanged();
        StraightenWithAiCommand.NotifyCanExecuteChanged();
    }

    private void NotifyUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RequestEditRows();
        RedoCommand.NotifyCanExecuteChanged();
        NotifyCompare();
    }

    /// <summary>
    /// Image for "Before": goes back through the history of this file as long as the shape stays the same,
    /// that is before AI steps but not before a crop or rotation (different shape).
    /// An AI enlargement (same aspect ratio) is included. Returns null if there is no earlier state.
    /// </summary>
    private MagickImage? FindBeforeImage()
    {
        if (_history is not { } h || CurrentPath is not { } path) return null;

        var before = h.Current;
        foreach (var step in _undo)   // newest first
        {
            if (step.FilePath != path || step.Image is not { } image) continue;
            if (!SameShape(image, before)) break;
            before = image;
        }

        return ReferenceEquals(before, h.Current) ? null : before;
    }

    /// <summary>Same aspect ratio, to within one pixel.</summary>
    private static bool SameShape(MagickImage a, MagickImage b) =>
        Math.Abs((double)a.Width * b.Height - (double)b.Width * a.Height) <= Math.Max(a.Width, b.Width);

    private void ClearRedo()
    {
        foreach (var s in _redo)
        {
            s.Image?.Dispose();
            // AI result without "Skin & details": belongs only to this step, which is now discarded
            if (s.Step?.Plain is { } plain && !_undo.Any(u => ReferenceEquals(u.Step, s.Step))) plain.Dispose();
        }
        _redo.Clear();
    }

    /// <summary>Determines whether the file must stay in memory because the edit state or the history still refers to it.</summary>
    /// <remarks>
    /// Only image and geometry steps need the image in memory (they swap the pixel image or the recipe). Slider steps
    /// do not: "Undo" switches to the file and reloads it, and the slider values are stored per file in the settings.
    /// If every slider step kept its image, memory grew from 1.4 to 4.4 GB when browsing through 20 photos of 12 MP.
    /// </remarks>
    private bool IsReferencedByHistory(string path, EditHistory h) =>
        h.IsEdited
        || NeedsImage(_undo, path)
        || NeedsImage(_redo, path);

    private static bool NeedsImage(Stack<Snapshot> stack, string path)
    {
        foreach (var s in stack)
            if (s.FilePath == path && (s.Image is not null || s.Recipe is not null)) return true;
        return false;
    }

    /// <summary>
    /// Cropping is possible with a selection, with a valid quadrilateral in the perspective tool, or in the "Curved page"
    /// tool with lines or an applied straightening.
    /// </summary>
    public bool CanCropToSelection => !IsUpscaling && HasCropSelection || (PerspectiveTool && IsPerspectiveValid)
        || (DewarpTool && (HasDewarpLines || CurrentRecipe.HasDewarp));

    /// <summary>
    /// Applies the open tool as one recipe step: the lines of "Curved page", the perspective quadrilateral, the free
    /// rotation with its frame, or otherwise the selection as a crop. With rounded corners everything outside the
    /// rounding becomes transparent; the image stays rectangular.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCropToSelection))]
    private async Task CropToSelection()
    {
        if (_history is not { } h) return;
        var (pw, ph) = PixelSize();

        // Curved page: write the lines into the recipe
        if (DewarpTool && DewarpLines is { } lines)
        {
            await ApplyDewarpAsync(lines);
            return;
        }

        // Perspective: write the quadrilateral of the tool view (image after the page flattening, only rotated/flipped) into the recipe
        if (PerspectiveTool && PerspectiveQuad is { } quad)
        {
            var pts = AsTuples(quad);
            if (!PerspectiveGeometry.IsValid(pts)) return;
            var next = h.Recipe.StraightenedInView(pts, pw, ph, StagesShown(HiddenSteps));
            var (tw, th) = next.SizeFor(pw, ph, GeometryStages.Straighten | (StagesShown(HiddenSteps) & GeometryStages.Dewarp));
            CloseToolsQuietly();
            await CommitRecipeAsync(next, new ImageStep(ImageStepKind.Crop, Tr.T("Entzerrt"), $"{tw} × {th} px", null),
                s => s with { Hidden = s.Hidden & ~EditSteps.Straighten });
            return;
        }

        if (CropSelection is not { } sel) return;
        var radius = CornerRadius;

        // Free rotation: angle and frame of the view (without rotation and crop) replace the previous ones
        if (RotationTool)
        {
            var next = h.Recipe.LevelledInView(RotationAngle, sel.X, sel.Y, sel.Width, sel.Height, radius, pw, ph);
            var detail = $"{next.Angle.ToString("+0.0;-0.0", System.Globalization.CultureInfo.CurrentCulture)}° · {(int)Math.Round(sel.Width)} × {(int)Math.Round(sel.Height)} px";
            CloseToolsQuietly();
            await CommitRecipeAsync(next, new ImageStep(ImageStepKind.Rotate, Tr.T("Gerade gerichtet"), detail, null),
                s => s with { Hidden = s.Hidden & ~(EditSteps.Level | EditSteps.CropStep) });
            return;
        }

        // Crop in the displayed image: crop further, or replace the crop if it is switched off
        var cropped = h.Recipe.CroppedInResult(sel.X, sel.Y, sel.Width, sel.Height, radius, pw, ph, StagesShown(HiddenSteps));
        await CommitRecipeAsync(cropped,
            new ImageStep(ImageStepKind.Crop, Tr.T("Zuschnitt"), $"{(int)Math.Round(sel.Width)} × {(int)Math.Round(sel.Height)} px", null),
            s => s with { Hidden = s.Hidden & ~(EditSteps.CropStep | ((s.Hidden & EditSteps.Level) != 0 ? EditSteps.Level : 0)) });
    }

    /// <summary>
    /// A geometry step: sets the new recipe and pushes the old one onto the undo stack (with the settings before).
    /// The pixel image stays; it is displayed with the new recipe.
    /// </summary>
    /// <param name="next">The new recipe.</param>
    /// <param name="step">Description of the step for the "Edits" list.</param>
    /// <param name="settingsAfter">Settings change belonging to the same step; null leaves the settings unchanged.</param>
    /// <returns>false if there is no image or an editing step is running.</returns>
    private async Task<bool> CommitRecipeAsync(GeometryRecipe next, ImageStep step, Func<SettingsState, SettingsState>? settingsAfter = null)
    {
        if (_history is not { } h || IsEditing || _fileIndex < 0) return false;
        CommitPendingSettings();
        var path = _files[_fileIndex];
        var before = CaptureSettings();
        _undo.Push(new Snapshot(path, null, before, Step: step, Recipe: h.Recipe));
        ClearRedo();
        h.Recipe = next;
        if (settingsAfter is not null) ChangeSettings(path, settingsAfter);
        await ShowCurrentAsync();
        NotifyUndoRedo();
        return true;
    }

    // Closes the tool without recomputing its view, because the new recipe follows right away.
    private bool _quietTools;

    private void CloseToolsQuietly()
    {
        _quietTools = true;
        try
        {
            RotationTool = false;
            PerspectiveTool = false;
            DewarpTool = false;
        }
        finally
        {
            _quietTools = false;
        }
    }

    // ------------------------------------------------------------ Free rotation

    /// <summary>"Free rotation" tool: a slider at the bottom of the preview; the selection frame belongs to it.</summary>
    [ObservableProperty] public partial bool RotationTool { get; set; }

    /// <summary>
    /// Free rotation not yet applied, in degrees (−180 … +180, positive = clockwise). The preview shows
    /// it; it is applied together with the frame when cropping. The frame is defined in the unrotated
    /// coordinate system around the image centre (<see cref="RotationGeometry"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRotationPending))]
    public partial double RotationAngle { get; set; }

    public bool IsRotationPending => Math.Abs(RotationAngle) > 1e-9;

    partial void OnRotationToolChanged(bool value)
    {
        if (value)
        {
            PerspectiveTool = false;              // only one tool at a time
            DewarpTool = false;
            SelectionTool = true;                 // without a frame there is no crop after rotating
            if (!_quietTools) _ = EnterRotationViewAsync();
        }
        else
        {
            if (IsRotationPending) DiscardRotation();
            CropSelection = null;
            if (!_quietTools) _ = ShowDocumentAsync();   // back to the finished image
        }
    }

    /// <summary>
    /// View for "Free rotation": the image without free rotation and crop, pre-filled with the angle and frame of the
    /// recipe, so that a crop can also be widened again.
    /// </summary>
    private async Task EnterRotationViewAsync()
    {
        if (_history is not { } h) return;
        await ShowDocumentAsync();
        if (!RotationTool) return;   // closed again in the meantime
        var (pw, ph) = PixelSize();
        var (angle, frame) = h.Recipe.LevelInView(pw, ph);
        if (Math.Abs(angle) > 1e-9) RotationAngle = Math.Round(angle, 1);
        if (frame is { } f) CropSelection = new Rect(Math.Round(f.X), Math.Round(f.Y), Math.Round(f.W), Math.Round(f.H));
    }

    // ------------------------------------------------------------ Perspective

    /// <summary>"Straighten" (perspective) tool: place a quadrilateral with freely movable corners on something rectangular.</summary>
    [ObservableProperty] public partial bool PerspectiveTool { get; set; }

    /// <summary>The four corners (top left, top right, bottom right, bottom left) in image pixels; null if the tool is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPerspectiveValid), nameof(PerspectiveInfo), nameof(CanCropToSelection))]
    public partial IReadOnlyList<Point>? PerspectiveQuad { get; set; }

    public bool IsPerspectiveValid => PerspectiveQuad is { Count: 4 } q && PerspectiveGeometry.IsValid(AsTuples(q));

    /// <summary>Shown at the bottom of the panel: size of the result or what is wrong with the quadrilateral.</summary>
    public string PerspectiveInfo
    {
        get
        {
            if (PerspectiveQuad is not { Count: 4 } q) return "";
            if (!IsPerspectiveValid) return Tr.T("Die Ecken überkreuzen sich – bitte so legen, dass ein Viereck entsteht.");
            var (w, h) = PerspectiveGeometry.TargetSize(AsTuples(q));
            return Tr.T("Ergebnis: {0} × {1} px", w, h);
        }
    }

    private static IReadOnlyList<(double X, double Y)> AsTuples(IReadOnlyList<Point> q) => q.Select(p => (p.X, p.Y)).ToArray();

    partial void OnPerspectiveToolChanged(bool value)
    {
        if (value)
        {
            RotationTool = false;
            DewarpTool = false;
            CropSelection = null;
            if (!_quietTools) _ = EnterPerspectiveViewAsync();
        }
        else
        {
            PerspectiveQuad = null;
            if (!_quietTools) _ = ShowDocumentAsync();   // back to the finished image
        }
        OnPropertyChanged(nameof(CanCropToSelection));
        CropToSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>View for "Straighten": the pixel image, only rotated/flipped, pre-filled with the quadrilateral of the recipe.</summary>
    private async Task EnterPerspectiveViewAsync()
    {
        if (_history is not { } h) return;
        await ShowDocumentAsync();
        if (!PerspectiveTool) return;
        var (pw, ph) = PixelSize();
        if (h.Recipe.QuadInView(pw, ph, StagesShown(HiddenSteps)) is { } q) PerspectiveQuad = q.Select(p => new Point(p.X, p.Y)).ToArray();
        else ResetPerspectiveQuad();
    }

    partial void OnPerspectiveQuadChanged(IReadOnlyList<Point>? value)
    {
        if (value is null && PerspectiveTool) PerspectiveTool = false;   // Esc in the preview
        CropToSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Resets the corners to their initial position, one tenth of the image size inside the edges.</summary>
    [RelayCommand]
    private void ResetPerspectiveQuad()
    {
        // Size of the displayed view (for perspective correction the pixel image, only rotated/flipped)
        double w = DocumentSize.Width > 0 ? DocumentSize.Width : SourceWidth, h = DocumentSize.Height > 0 ? DocumentSize.Height : SourceHeight;
        double ix = w * 0.1, iy = h * 0.1;
        PerspectiveQuad = [new Point(ix, iy), new Point(w - ix, iy), new Point(w - ix, h - iy), new Point(ix, h - iy)];
    }

    partial void OnRotationAngleChanged(double value)
    {
        // Normalise to −180 … +180 and tenths of a degree so that slider and number field show the same value.
        var tidy = double.IsFinite(value) ? Math.Round(RotationGeometry.Normalize(value), 1) : 0;
        if (tidy != value) { RotationAngle = tidy; return; }

        // If a rotation is pending (for example after undo), the slider must be visible with it.
        if (IsRotationPending && !RotationTool) RotationTool = true;
    }

    /// <summary>Discards the rotation: angle 0, frame removed (Esc in the preview does the same).</summary>
    [RelayCommand]
    private void DiscardRotation()
    {
        RotationAngle = 0;
        CropSelection = null;
    }

    // Rotating and flipping change only the recipe (net quarter turns), one undo step per click.
    [RelayCommand(CanExecute = nameof(CanTransform))]
    private Task RotateLeft() => Orient(r => r.Rotated(false), new ImageStep(ImageStepKind.Rotate, Tr.T("Links gedreht"), "90°", null));

    [RelayCommand(CanExecute = nameof(CanTransform))]
    private Task RotateRight() => Orient(r => r.Rotated(true), new ImageStep(ImageStepKind.Rotate, Tr.T("Rechts gedreht"), "90°", null));

    [RelayCommand(CanExecute = nameof(CanTransform))]
    private Task FlipHorizontal() => Orient(r => r.Flipped(), new ImageStep(ImageStepKind.Flip, Tr.T("Gespiegelt"), Tr.T("links ↔ rechts"), null));

    private async Task Orient(Func<GeometryRecipe, GeometryRecipe> change, ImageStep step)
    {
        if (_history is not { } h) return;
        if (RotationTool || PerspectiveTool || DewarpTool) CloseToolsQuietly();   // discard an open tool
        if (IsRotationPending) DiscardRotation();
        // If rotate/flip is switched off, only the new click applies; in any case the step becomes visible again.
        var basis = (HiddenSteps & EditSteps.Orient) != 0 ? h.Recipe with { Turns = 0, Flip = false } : h.Recipe;
        await CommitRecipeAsync(change(basis), step, s => s with { Hidden = s.Hidden & ~EditSteps.Orient });
    }

    /// <summary>
    /// An editing step on the actual pixel image (ImageMagick, in the background).
    /// The result becomes the new image; the image and settings before it are pushed
    /// onto the undo stack as one step.
    /// </summary>
    /// <param name="operation">Computes the new image from the current one.</param>
    /// <param name="settingsAfter">Settings afterwards (for example a neutral look), in the same step; null leaves them unchanged.</param>
    /// <param name="step">What the step was, for the "Edits" list (and for repeating it on another image).</param>
    /// <param name="recipeAfter">Geometry afterwards (in the same step, for example "Apply selection"); null leaves it unchanged.</param>
    /// <param name="expected">The pixel image the caller prepared the step for (start image, strokes, crop); if another image
    /// or state is shown by now, nothing is done. null = no check.</param>
    /// <returns>true on success; false if nothing was done, the step was cancelled or failed.</returns>
    private async Task<bool> ApplyEditAsync(Func<MagickImage, MagickImage> operation,
        Func<SettingsState, SettingsState>? settingsAfter, ImageStep step, GeometryRecipe? recipeAfter = null,
        MagickImage? expected = null)
    {
        if (_history is not { } h || IsEditing || _fileIndex < 0) return false;
        // Prepared for another state: between preparing and starting the user switched images or pressed Ctrl+Z (No. 98)
        if (expected is not null && !ReferenceEquals(h.Current, expected))
        {
            ErrorMessage = Tr.T("Das Bild wurde inzwischen gewechselt oder geändert – bitte noch einmal anwenden.");
            return false;
        }

        CommitPendingSettings();   // pending slider movements become a separate step before this one
        IsEditing = true;
        _editingHistory = h;
        // Long steps (AI): another image may be displayed in the meantime.
        var path = _files[_fileIndex];
        try
        {
            var source = h.Current;
            var before = CaptureSettings();
            var result = await Task.Run(() => operation(source));

            // New images were opened meanwhile: the history is gone, its result must not enter the new one
            if (!_histories.TryGetValue(path, out var still) || still != h)
            {
                if (!ReferenceEquals(result, source)) result.Dispose();
                step.Plain?.Dispose();
                return false;
            }

            await StopPreviewAsync();   // redo images might be being read right now
            // A new attempt right after the previous one of the same kind (AI, removal, background) takes its place: undo
            // leads to the state before that step and the previous result is released (otherwise every attempt stays in
            // memory). One rule for all three (No. 99).
            MagickImage? replaced = null;
            MagickImage? replacedPlain = null;
            if (step.Replaces
                && _undo.TryPeek(out var top) && top.FilePath == path && top.Step?.Kind == step.Kind && top.Image is { } earlier)
            {
                _undo.Pop();
                (replaced, source, before) = (source, earlier, top.Settings);
                replacedPlain = top.Step.Plain;
                step = step with { Replaces = false, Discards = top.Step.Discards };
            }
            _undo.Push(new Snapshot(path, source, before, Step: step, Recipe: recipeAfter is null ? null : h.Recipe));
            ClearRedo();
            h.Current = result;
            if (replaced is not null) ReleaseReplaced(replaced);
            if (replacedPlain is not null) ReleaseReplaced(replacedPlain);
            if (recipeAfter is not null) h.Recipe = recipeAfter;
            if (settingsAfter is not null) ChangeSettings(path, settingsAfter);   // belongs to this step (the snapshot holds the state before)
            if (h == _history) await ShowCurrentAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            // cancelled: the image stays as it was
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Bearbeiten fehlgeschlagen: {0}", ex.Message);
            return false;
        }
        finally
        {
            _editingHistory = null;
            foreach (var img in _disposeAfterEdit) img.Dispose();
            _disposeAfterEdit.Clear();
            IsEditing = false;
            NotifyUndoRedo();
            // Another image is shown by now: release this one if the step left it unedited (failed or cancelled)
            if (h != _history) await ReleaseIfUnusedAsync(path, h);
        }
    }

    /// <summary>
    /// Releases a file that is no longer displayed if nothing refers to it: unedited images are not kept in memory, edited
    /// ones are (otherwise a crop would be lost when moving on to another image). An image an editing step is still
    /// computing on stays until the step ends (No. 85).
    /// </summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="history">Its history.</param>
    /// <returns>A task that completes when the image has been released or kept.</returns>
    private async Task ReleaseIfUnusedAsync(string path, EditHistory history)
    {
        if (history == _history || history == _editingHistory || IsReferencedByHistory(path, history)
            || !_histories.TryGetValue(path, out var known) || known != history)
            return;
        await StopPreviewAsync();
        _histories.Remove(path);
        history.Current.Dispose();   // unedited: Current is the original
        RequestPreview();
        ReleaseMemorySoon();
    }

    /// <summary>
    /// Changes the settings of a file without a separate undo step; the change belongs to the preceding image step.
    /// </summary>
    private void ChangeSettings(string path, Func<SettingsState, SettingsState> change)
    {
        if (path == CurrentPath)
        {
            ApplySettings(change(CaptureSettings()));
            if (!ApplyToAll) _fileSettings[path] = CaptureSettings();
        }
        else if (!ApplyToAll && _fileSettings.TryGetValue(path, out var saved))
        {
            _fileSettings[path] = change(saved);
        }
    }

    /// <summary>
    /// For extensions (for example AI): an editing step on the current image,
    /// with undo/redo like cropping and rotating. Cancelling via
    /// <see cref="OperationCanceledException"/> leaves the image unchanged.
    /// </summary>
    /// <param name="operation">Returns the result and, if there is a "Skin &amp; details" part, the same AI result without it.</param>
    /// <param name="label">Name in the "Edits" list (for example "AI: Faces – CodeFormer").</param>
    /// <param name="detail">Short info on it (strength).</param>
    /// <param name="skinDetail">Short info for the "Skin &amp; details" row; null for none.</param>
    /// <param name="lookAfter">Look that applies afterwards (for example neutral, if it was baked into the image), in the same
    /// step, so that undo restores image and sliders together. null leaves the sliders unchanged.</param>
    /// <param name="start">The image the result was computed from; a new attempt starts from it again.</param>
    /// <param name="replaces">New attempt directly on the previous AI step: takes its place, the previous result is released.</param>
    /// <param name="discards">Number of later image steps the result no longer contains (it started from before them).</param>
    /// <param name="baked">Look rendered into the result ("Erst Einstellungen"); null if none.</param>
    /// <param name="expected">The pixel image the result was computed for; nothing is done if another one is shown by now.</param>
    /// <returns>The new image on success (also when another image is shown by now), otherwise null.</returns>
    internal async Task<MagickImage?> ApplyImageEditAsync(Func<MagickImage, (MagickImage Result, MagickImage? Plain)> operation,
        string label, string? detail, string? skinDetail, Look? lookAfter, MagickImage start, bool replaces, int discards,
        Look? baked, MagickImage expected)
    {
        var step = new ImageStep(ImageStepKind.Ai, label, detail, null, skinDetail)
        {
            Base = start, Replaces = replaces, Discards = discards, Baked = baked,
        };
        MagickImage? produced = null;
        var ok = await ApplyEditAsync(img =>
            {
                var (result, plain) = operation(img);
                step.Plain = plain;
                return produced = result;
            },
            lookAfter is null ? null : s => KeepHidden(WithLook(s, lookAfter), s), step, expected: expected);
        return ok ? produced : null;
    }

    /// <summary>
    /// For removal ("Remove" tab): an image step with undo like cropping and rotating. The operation
    /// receives the current image but may compute from an earlier state (new attempt with another model);
    /// undo then returns to the previous result.
    /// </summary>
    /// <param name="operation">Computes the new image from the current one.</param>
    /// <param name="detail">Short info for the "Edits" list.</param>
    /// <param name="start">The image <paramref name="operation"/> computes from; a new attempt starts from it again.</param>
    /// <param name="replaces">New attempt directly on the previous removal: takes its place, the previous result is released.</param>
    /// <param name="strokes">The marking removed (in the displayed image).</param>
    /// <param name="expected">The pixel image shown when the step was prepared; nothing is done if it changed meanwhile.</param>
    /// <returns>The new image on success (also when another image is shown by now), otherwise null.</returns>
    internal Task<MagickImage?> ApplyRetouchAsync(Func<MagickImage, MagickImage> operation, string detail, MagickImage start,
        bool replaces, IReadOnlyList<Core.Ai.BrushStroke> strokes, MagickImage expected) =>
        ApplyProducingAsync(operation, new ImageStep(ImageStepKind.Retouch, Tr.T("Entfernt"), detail, null)
        {
            Base = start, Replaces = replaces, Strokes = strokes,
        }, expected);

    /// <summary>
    /// Replaces or blurs the background ("Hintergrund" tab): an image step in the pixel image like a removal; undo returns
    /// to the image before.
    /// </summary>
    /// <param name="operation">Computes the new image from the current one.</param>
    /// <param name="label">Name in the "Edits" list ("Hintergrund entfernt" / "Hintergrund unscharf").</param>
    /// <param name="detail">Short info for the "Edits" list (model, strength).</param>
    /// <param name="start">The image <paramref name="operation"/> computes from; later attempts start from it again.</param>
    /// <param name="replaces">New attempt directly on the previous one: takes its place, the previous result is released.</param>
    /// <param name="discards">Number of later image steps the new attempt no longer contains (it starts from before them).</param>
    /// <param name="expected">The pixel image shown when the step was prepared; nothing is done if it changed meanwhile.</param>
    /// <returns>The new image on success (also when another image is shown by now), otherwise null.</returns>
    internal Task<MagickImage?> ApplyBackgroundAsync(Func<MagickImage, MagickImage> operation, string label, string detail,
        MagickImage start, bool replaces, int discards, MagickImage expected) =>
        ApplyProducingAsync(operation, new ImageStep(ImageStepKind.Background, label, detail, null) { Replaces = replaces, Base = start, Discards = discards }, expected);

    /// <summary>
    /// An image step that reports the image it produced. The caller must not take <see cref="CurrentImage"/> instead:
    /// while the step computed, the user may have switched to another image (No. 86).
    /// </summary>
    private async Task<MagickImage?> ApplyProducingAsync(Func<MagickImage, MagickImage> operation, ImageStep step, MagickImage expected)
    {
        MagickImage? produced = null;
        var ok = await ApplyEditAsync(img => produced = operation(img), null, step, expected: expected);
        return ok ? produced : null;
    }

    /// <summary>The current pixel image in full resolution (with AI and removal edits, without geometry); read only.</summary>
    internal MagickImage? CurrentImage => _history?.Current;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task Undo()
    {
        CommitPendingSettings();
        if (!_undo.TryPop(out var previous)) return;

        _redo.Push(await RestoreAsync(previous));
        NotifyUndoRedo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task Redo()
    {
        if (!_redo.TryPop(out var next)) return;

        _undo.Push(await RestoreAsync(next));
        NotifyUndoRedo();
    }

    /// <summary>
    /// Restores a step and returns its counterpart for the other
    /// stack (undo ↔ redo): of the same kind, holding the current state.
    /// </summary>
    private async Task<Snapshot> RestoreAsync(Snapshot step)
    {
        // If the step belongs to another image, switch to it first.
        if (step.FilePath is { } path && path != CurrentPath)
        {
            var index = _files.IndexOf(path);
            if (index >= 0) await ShowFileAsync(index);
        }

        var settingsNow = CaptureSettings();

        // Image step: swap the image; geometry step: swap the recipe
        MagickImage? imageNow = null;
        GeometryRecipe? recipeNow = null;
        if ((step.Image is not null || step.Recipe is not null) && step.FilePath is not null && _histories.TryGetValue(step.FilePath, out var h))
        {
            await StopPreviewAsync();
            if (step.Image is not null)
            {
                imageNow = h.Current;
                h.Current = step.Image;
            }
            if (step.Recipe is not null)
            {
                recipeNow = h.Recipe;
                h.Recipe = step.Recipe;
            }
            if (h == _history)
            {
                CloseToolsQuietly();
                await ShowCurrentAsync();
            }
        }

        // Switching between "for all" and "per image": restore the mode and all per-image states
        bool? modeNow = null;
        SettingsState? templateNow = null;
        Dictionary<string, SettingsState>? fileSettingsNow = null;
        if (step.ApplyToAll is { } mode)
        {
            modeNow = ApplyToAll;
            templateNow = _individualTemplate;
            fileSettingsNow = new Dictionary<string, SettingsState>(_fileSettings);
            using (SuppressHistory())
                ApplyToAll = mode;
            _fileSettings = step.FileSettings is { } saved
                ? new Dictionary<string, SettingsState>(saved)
                : new Dictionary<string, SettingsState>(StringComparer.Ordinal);
            _individualTemplate = step.Template;
        }

        ApplySettings(step.Settings);
        if (!ApplyToAll && CurrentPath is { } current)
            _fileSettings[current] = CaptureSettings();

        return new Snapshot(step.FilePath, imageNow, settingsNow, modeNow, fileSettingsNow, templateNow, step.Step, recipeNow);
    }

    /// <summary>Shows the current image of the file; the pixel dimensions are reset to those of the image.</summary>
    private async Task ShowCurrentAsync()
    {
        if (_history is not { } h) return;
        CommitPendingSettings();   // for example a slider was moved while the AI was computing

        // Pixel image + recipe in the background (perspective correction of a large image takes time)
        DropGeometryDocsExcept(CurrentPath);
        var shown = await Task.Run(() => ShownDocument) ?? h.Current;
        var result = await Task.Run(() => ResultDocument) ?? h.Current;
        EmptyGeometryTrash();
        await SetDocumentAsync(shown);   // with AI hidden, the image without it

        int w, hgt;
        lock (result) (w, hgt) = ((int)result.Width, (int)result.Height);
        UpdateFileDetails();

        using (SuppressHistory())
            ApplySourceSize(w, hgt);
        ResetHistoryBaseline();

        NotifyUndoRedo();
        OnPropertyChanged(nameof(ShownGeometry));   // brush strokes lie in the displayed image (No. 98)
        RequestPreview();
    }

    /// <summary>Line below the file name: dimensions, file size, format.</summary>
    private void UpdateFileDetails()
    {
        if (_history is not { } h) return;
        var (pw, ph) = PixelSize();
        var (dw, dh) = h.Recipe.SizeFor(pw, ph, StagesShown(HiddenSteps));
        var doc = (Width: dw, Height: dh);
        FileDetails = h.IsEdited
            ? Tr.T("{0} × {1} px  ·  {2}  ·  bearbeitet", doc.Width, doc.Height, h.FormatName)
            : $"{doc.Width} × {doc.Height} px  ·  {FormatBytes(h.FileBytes)}  ·  {h.FormatName}";
    }

    /// <summary>Releases everything, disposing each image exactly once even if it is referenced several times.</summary>
    private void DisposeAllHistories()
    {
        ClearWithoutAi();   // images without AI, before the history that still refers to them
        ClearGeometryDocs();
        var images = new HashSet<MagickImage>(ReferenceEqualityComparer.Instance);
        foreach (var h in _histories.Values) { images.Add(h.Original); images.Add(h.Current); }
        foreach (var s in _undo) if (s.Image is not null) images.Add(s.Image);
        foreach (var s in _redo) if (s.Image is not null) images.Add(s.Image);
        foreach (var s in _undo.Concat(_redo)) if (s.Step?.Plain is { } plain) images.Add(plain);
        // A running editing step may still read any of them (also an earlier state): dispose them when it ends (No. 85)
        if (_editingHistory is not null) _disposeAfterEdit.AddRange(images);
        else foreach (var img in images) img.Dispose();

        _histories.Clear();
        _undo.Clear();
        _redo.Clear();
        _history = null;
        _fileSettings.Clear();
        _individualTemplate = null;
        _pendingBaseline = null;
        NotifyUndoRedo();
    }

    // ----------------------------------------------------------- Crop

    /// <summary>Selection in image pixels; null if there is no selection. Set by the CropView.</summary>
    [ObservableProperty] public partial Rect? CropSelection { get; set; }

    [ObservableProperty] public partial string SelectedAspectPreset { get; set; }
    [ObservableProperty] public partial int CropX { get; set; }
    [ObservableProperty] public partial int CropY { get; set; }
    [ObservableProperty] public partial int CropWidth { get; set; }
    [ObservableProperty] public partial int CropHeight { get; set; }

    // Prevents ping-pong between the selection frame and the number fields.
    private bool _isSyncingCrop;

    /// <summary>Corner radius in image pixels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CornerRadiusLabel), nameof(ShowTransparencyWarning))]
    public partial double CornerRadius { get; set; }

    public bool HasCropSelection => CropSelection is not null;

    /// <summary>
    /// Maximum = half the longest side. At the maximum the selection becomes an ellipse,
    /// or a circle for a square selection.
    /// </summary>
    public double MaxCornerRadius =>
        CropSelection is { } s ? Math.Max(1, Math.Max(s.Width, s.Height) / 2) : 1;

    public string CornerRadiusLabel
    {
        get
        {
            if (CornerRadius < 0.5) return Tr.T("eckig");
            if (CornerRadius >= MaxCornerRadius - 0.5)
                return CropWidth == CropHeight ? Tr.T("Kreis") : Tr.T("Ellipse");
            return $"{CornerRadius:F0} px";
        }
    }

    /// <summary>Rounded corners become transparent, which JPEG and BMP cannot store.</summary>
    public bool ShowTransparencyWarning =>
        CornerRadius >= 0.5 && SelectedFormat is "JPEG" or "BMP";

    public double AspectRatio => SelectedAspectPreset switch
    {
        "Original" => SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 0,
        "1:1" => 1,
        "4:3" => 4.0 / 3,
        "3:2" => 3.0 / 2,
        "16:9" => 16.0 / 9,
        _ => 0,
    };

    partial void OnSelectedAspectPresetChanged(string value) => OnPropertyChanged(nameof(AspectRatio));

    partial void OnCropSelectionChanged(Rect? value)
    {
        OnPropertyChanged(nameof(HasCropSelection));
        OnPropertyChanged(nameof(MaxCornerRadius));
        CropToSelectionCommand.NotifyCanExecuteChanged();

        if (value is null)
            CornerRadius = 0;
        else if (CornerRadius > MaxCornerRadius)
            CornerRadius = MaxCornerRadius;

        if (_isSyncingCrop) return;
        _isSyncingCrop = true;
        if (value is { } r)
        {
            CropX = (int)r.X;
            CropY = (int)r.Y;
            CropWidth = (int)r.Width;
            CropHeight = (int)r.Height;
        }
        _isSyncingCrop = false;
        OnPropertyChanged(nameof(CornerRadiusLabel));
    }

    partial void OnCropXChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropYChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropWidthChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropHeightChanged(int value) => PushCropFieldsToSelection();

    private void PushCropFieldsToSelection()
    {
        if (_isSyncingCrop || CropSelection is null) return;

        // Size of the displayed view (for "Free rotation" the image without rotation and crop)
        int vw = DocumentSize.Width > 0 ? DocumentSize.Width : SourceWidth, vh = DocumentSize.Height > 0 ? DocumentSize.Height : SourceHeight;
        var x = Math.Clamp(CropX, 0, vw - 1);
        var y = Math.Clamp(CropY, 0, vh - 1);
        var w = Math.Clamp(CropWidth, 1, vw - x);
        var h = Math.Clamp(CropHeight, 1, vh - y);
        var rect = new Rect(x, y, w, h);

        // When rotating: not beyond the slanted edges; shrink around the centre
        if (IsRotationPending)
        {
            var (fx, fy, fw, fh) = RotationGeometry.ShrinkInside(vw, vh, RotationAngle, x, y, w, h);
            var x0 = Math.Ceiling(fx - 1e-6);
            var y0 = Math.Ceiling(fy - 1e-6);
            rect = new Rect(x0, y0, Math.Max(1, Math.Floor(fx + fw + 1e-6) - x0), Math.Max(1, Math.Floor(fy + fh + 1e-6) - y0));
        }

        _isSyncingCrop = true;
        CropSelection = rect;
        _isSyncingCrop = false;
        if (rect != new Rect(x, y, w, h)) OnCropSelectionChanged(rect);   // update the number fields to the shrunk frame
    }

    /// <summary>Clears the selection; while rotating also the rotation, because the frame belongs to it.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        if (IsRotationPending) RotationAngle = 0;
        CropSelection = null;
    }
}
