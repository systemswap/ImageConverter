using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageConverter.Models;

namespace ImageConverter.ViewModels;

// Undo for settings.
//
// Every setting changed in the left-hand column (or with the selection frame)
// can be undone. One step is created per user action, not by time:
// – Everything one action changes belongs together (for example the width with the linked
//   height, the DPI with the recalculated pixels).
// – Sliders and input fields: consecutive actions on the same control become one
//   step; moving back and forth, pauses and grabbing it again produce only one.
//   The step is completed as soon as something else is operated, on undo,
//   image switch, editing or export.
// – Selection frame: each release of the mouse button is a step (CommitSettingsStep).
// – Check boxes and drop-down lists: each action is a separate step.
// A step stores the complete settings state before it.
public partial class MainViewModel
{
    /// <summary>Complete state of all settings that undo can restore.</summary>
    internal sealed record SettingsState(
        string SelectedFormat, double Quality, bool Lossless,
        string SelectedSizeUnit, bool KeepAspectRatio, bool WidthIsLeading,
        int TargetWidth, int TargetHeight, double PrintWidthIn, double PrintHeightIn,
        ResizeCalculator Calc,
        FilterOption SelectedFilter, bool GammaCorrect,
        int Dpi,
        string SelectedAspectPreset, Rect? CropSelection, double CornerRadius,
        double SharpenAmount, double SharpenRadius, double NoiseReduction,
        double Dehaze, double Brightness, double Contrast, double Shadows, double Highlights, double Vibrance, double Saturation,
        string SelectedTint, double TintHue, double MatchLuminance, double Neutralize, double Warmth,
        string SelectedMetadataMode, string SelectedColorMode,
        string OutputFolder, string NamePattern,
        int SourceWidth, int SourceHeight,   // image size this state applies to
        double DehazePasses, double DehazeClarity, double DehazeContrast, double DehazeVibrance,
        double DehazeSharpen, double DehazeEdge, double DehazeBrightness, double DehazeDeband, double DehazeCast,
        bool HighBitDepth,
        EditSteps Hidden = EditSteps.None,    // switched-off functions ("Edits" list)
        double ColorNoiseReduction = 0,
        double RotationAngle = 0,             // free rotation not yet applied; belongs to the frame
        WbSample? WbNeutral = null,           // white balance: averaged eyedropper measurements
        WbSample? WbBlack = null,
        WbSample? WbWhite = null,
        double MatchLimit = 100,            // luminance: threshold of the areas; brighter ones stay unchanged
        double MatchTarget = 0,             // luminance: target value
        double MatchGlobal = 0,             // luminance: global
        double MaskAmount = 0,              // luminosity mask: strength, fade-out start, gradient
        double MaskEnd = 75,
        double MaskCurve = 30,
        double GrainAmount = 0,             // grain: strength, grain size, roughness, colour share, hardness
        double GrainSize = 40,
        double GrainRoughness = 30,
        double GrainColor = 0,
        double GrainHardness = 0,
        UpscaleMethod Upscaler = UpscaleMethod.Filter,   // upscaling: method, fidelity, details
        double UpscaleFidelity = 1,
        double UpscaleDetails = 1,
        bool UpscaleTwice = true);                       // above 4×: two AI passes

    /// <summary>
    /// Properties whose change creates an undo step. The value is the
    /// group for continuous controls (sliders, input fields); consecutive actions in the same group
    /// are merged. null: each action is a separate step.
    /// </summary>
    private static readonly Dictionary<string, string?> TrackedSettings = new()
    {
        [nameof(SelectedFormat)] = null,
        [nameof(Quality)] = "Qualität",
        [nameof(Lossless)] = null,
        [nameof(HighBitDepth)] = null,
        [nameof(SelectedSizeUnit)] = null,
        [nameof(KeepAspectRatio)] = null,
        [nameof(TargetWidth)] = "Größe",
        [nameof(TargetHeight)] = "Größe",
        [nameof(SelectedFilter)] = null,
        [nameof(GammaCorrect)] = null,
        [nameof(Dpi)] = "DPI",
        [nameof(SelectedAspectPreset)] = null,
        [nameof(CropSelection)] = "Auswahl",
        [nameof(RotationAngle)] = "Auswahl",   // rotates the frame as well: one step
        [nameof(WbNeutral)] = null,            // each eyedropper click is one step
        [nameof(WbBlack)] = null,
        [nameof(WbWhite)] = null,
        [nameof(CornerRadius)] = "Ecken",
        [nameof(SharpenAmount)] = "Schärfen",
        [nameof(SharpenRadius)] = "Radius",
        [nameof(NoiseReduction)] = "Rauschen",
        [nameof(ColorNoiseReduction)] = "Farbrauschen",
        [nameof(Dehaze)] = "Dunst",
        [nameof(DehazePasses)] = "Durchläufe",
        [nameof(DehazeClarity)] = "Klarheit",
        [nameof(DehazeContrast)] = "Dunst-Kontrast",
        [nameof(DehazeVibrance)] = "Dunst-Dynamik",
        [nameof(DehazeSharpen)] = "Schärfe fein",
        [nameof(DehazeEdge)] = "Kanten",
        [nameof(DehazeBrightness)] = "Helligkeit halten",
        [nameof(DehazeDeband)] = "Entstufen",
        [nameof(DehazeCast)] = "Dunstfarbe",
        [nameof(Brightness)] = "Helligkeit",
        [nameof(Contrast)] = "Kontrast",
        [nameof(Shadows)] = "Tiefen",
        [nameof(Highlights)] = "Lichter",
        [nameof(Vibrance)] = "Dynamik",
        [nameof(Saturation)] = "Sättigung",
        [nameof(SelectedTint)] = null,
        [nameof(TintHue)] = "Farbton",
        [nameof(MatchLuminance)] = "Luminanz",
        [nameof(MatchLimit)] = "Schwellwert",
        [nameof(MatchTarget)] = "Zielwert",
        [nameof(MatchGlobal)] = "Gesamt",
        [nameof(MaskAmount)] = "Maske Stärke",
        [nameof(MaskEnd)] = "Maske Auslaufen",
        [nameof(MaskCurve)] = "Maske Verlauf",
        [nameof(GrainAmount)] = "Körnung",
        [nameof(GrainSize)] = "Korngröße",
        [nameof(GrainRoughness)] = "Rauheit",
        [nameof(GrainColor)] = "Farbanteil",
        [nameof(GrainHardness)] = "Härte",
        [nameof(Neutralize)] = "Ausgleichen",
        [nameof(Warmth)] = "Farbe",
        [nameof(SelectedMetadataMode)] = null,
        [nameof(SelectedColorMode)] = null,
        [nameof(OutputFolder)] = "Ordner",
        [nameof(NamePattern)] = "Dateiname",
        [nameof(HiddenSteps)] = null,
        [nameof(SelectedUpscaler)] = null,
        [nameof(UpscaleFidelity)] = "Treue",
        [nameof(UpscaleDetails)] = "Details",
        [nameof(UpscaleTwice)] = null,
    };

    /// <summary>
    /// true (app): changes within one user action are collected and evaluated only
    /// afterwards. Tests set false, so that every change counts immediately.
    /// </summary>
    internal bool DeferHistoryTurns { get; init; } = true;

    private SettingsState? _committedSettings;   // state after the last step
    private SettingsState? _settled;             // state after the last user action
    private SettingsState? _pendingBaseline;     // state before the step that is still open
    private string? _pendingGroup;               // group of the open step (null: completed on the next action)

    // Current user action: the state before it and what it changed
    private SettingsState? _turnBaseline;
    private readonly HashSet<string> _turnProperties = [];
    private int _suppressHistory;                // > 0: changes come from the program, not from user input

    internal bool HasPendingSettings => _pendingBaseline is not null || _turnBaseline is not null;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && TrackedSettings.ContainsKey(name))
        {
            OnSettingChanged(name);
            RequestEditRows();   // the "Edits" list follows the sliders
        }
    }

    private void OnSettingChanged(string name)
    {
        if (_suppressHistory > 0 || _settled is null) return;
        TickHighBitDepthIfRecommended(name);   // 16 bits for strong tonal editing (silent, belongs to this step)

        var first = _turnBaseline is null;
        _turnBaseline ??= _settled;
        _turnProperties.Add(name);

        if (!DeferHistoryTurns)
        {
            EndTurn();
        }
        else if (first)
        {
            // Follow-up changes (linked height …) still run within the same call;
            // evaluation happens once the action is complete, before the next input.
            Dispatcher.UIThread.Post(EndTurn, DispatcherPriority.Normal);
            NotifyUndoRedo();
        }
    }

    /// <summary>A user action is complete: adds it to the open step or starts a new step.</summary>
    private void EndTurn()
    {
        if (_turnBaseline is not { } before) return;

        var groups = _turnProperties.Select(p => TrackedSettings[p]).ToList();
        var group = groups.Any(g => g is null) ? null : string.Join("+", groups.Distinct().Order());
        _turnBaseline = null;
        _turnProperties.Clear();

        // Another control (or a check box/list) was operated: complete the open step.
        if (_pendingBaseline is not null && (group is null || group != _pendingGroup))
            FinishPending(before);

        if (_pendingBaseline is null)
        {
            _pendingBaseline = before;
            _pendingGroup = group;
        }

        _settled = CaptureSettings();
        NotifyUndoRedo();
    }

    /// <summary>Stores the open step; <paramref name="after"/> is the state after it.</summary>
    private void FinishPending(SettingsState after)
    {
        if (_pendingBaseline is not { } before) return;
        _pendingBaseline = null;
        _pendingGroup = null;
        _committedSettings = after;

        // moved back and forth without any net change
        if (after == before) return;

        // In per-image mode the step belongs to the current image; undo jumps to it.
        _undo.Push(new Snapshot(ApplyToAll ? null : CurrentPath, null, before));
        ClearRedo();
    }

    /// <summary>Stores pending settings changes as a step now.</summary>
    internal void CommitPendingSettings()
    {
        EndTurn();
        FinishPending(CaptureSettings());
        _settled = CaptureSettings();
        NotifyUndoRedo();
    }

    /// <summary>End of a gesture (for example the mouse button released on the selection frame): a separate step.</summary>
    [RelayCommand]
    private void CommitSettingsStep() => CommitPendingSettings();

    /// <summary>Changes count afresh from here on: after loading, undo, image switch.</summary>
    private void ResetHistoryBaseline()
    {
        _turnBaseline = null;
        _turnProperties.Clear();
        _pendingBaseline = null;
        _pendingGroup = null;
        _committedSettings = _settled = CaptureSettings();
    }

    internal SettingsState CaptureSettings() => new(
        SelectedFormat, Quality, Lossless,
        SelectedSizeUnit, KeepAspectRatio, WidthIsLeading,
        TargetWidth, TargetHeight, _printWidthIn, _printHeightIn,
        _calc,
        SelectedFilter, GammaCorrect,
        Dpi,
        SelectedAspectPreset, CropSelection, CornerRadius,
        SharpenAmount, SharpenRadius, NoiseReduction,
        Dehaze, Brightness, Contrast, Shadows, Highlights, Vibrance, Saturation, SelectedTint, TintHue,
        MatchLuminance, Neutralize, Warmth,
        SelectedMetadataMode, SelectedColorMode,
        OutputFolder, NamePattern,
        SourceWidth, SourceHeight,
        DehazePasses, DehazeClarity, DehazeContrast, DehazeVibrance, DehazeSharpen, DehazeEdge, DehazeBrightness, DehazeDeband, DehazeCast,
        HighBitDepth, HiddenSteps, ColorNoiseReduction, RotationAngle, WbNeutral, WbBlack, WbWhite, MatchLimit, MatchTarget, MatchGlobal, MaskAmount, MaskEnd, MaskCurve,
        GrainAmount, GrainSize, GrainRoughness, GrainColor, GrainHardness,
        SelectedUpscaler.Method, UpscaleFidelity, UpscaleDetails, UpscaleTwice);

    /// <summary>Restores a saved state without creating new steps.</summary>
    private void ApplySettings(SettingsState s)
    {
        // State for an image of a different size (for example "for all" after an image switch):
        // convert the size to fit instead of taking over pixel dimensions of another image
        if (s.SourceWidth != SourceWidth || s.SourceHeight != SourceHeight)
            s = AdaptToImage(s, SourceWidth, SourceHeight);

        using (SuppressHistory())
        {
            _isSyncingSize = true;   // do not recalculate anything: the state is complete
            try
            {
                SelectedFormat = s.SelectedFormat;
                Quality = s.Quality;
                Lossless = s.Lossless;
                HighBitDepth = s.HighBitDepth;
                SelectedSizeUnit = s.SelectedSizeUnit;
                KeepAspectRatio = s.KeepAspectRatio;
                Dpi = s.Dpi;
                TargetWidth = s.TargetWidth;
                TargetHeight = s.TargetHeight;
                WidthIsLeading = s.WidthIsLeading;
                _printWidthIn = s.PrintWidthIn;
                _printHeightIn = s.PrintHeightIn;
                _calc = s.Calc;
                SelectedFilter = s.SelectedFilter;
                GammaCorrect = s.GammaCorrect;
                SelectedAspectPreset = s.SelectedAspectPreset;
                RotationAngle = s.RotationAngle;   // before the selection; otherwise the angle sets the automatic frame
                WbNeutral = s.WbNeutral;
                WbBlack = s.WbBlack;
                WbWhite = s.WbWhite;
                CropSelection = s.CropSelection;
                CornerRadius = s.CornerRadius;   // after the selection, which would otherwise limit the radius
                SharpenAmount = s.SharpenAmount;
                SharpenRadius = s.SharpenRadius;
                NoiseReduction = s.NoiseReduction;
                ColorNoiseReduction = s.ColorNoiseReduction;
                Dehaze = s.Dehaze;
                DehazePasses = s.DehazePasses;
                DehazeClarity = s.DehazeClarity;
                DehazeContrast = s.DehazeContrast;
                DehazeVibrance = s.DehazeVibrance;
                DehazeSharpen = s.DehazeSharpen;
                DehazeEdge = s.DehazeEdge;
                DehazeBrightness = s.DehazeBrightness;
                DehazeDeband = s.DehazeDeband;
                DehazeCast = s.DehazeCast;
                Brightness = s.Brightness;
                Contrast = s.Contrast;
                Shadows = s.Shadows;
                Highlights = s.Highlights;
                Vibrance = s.Vibrance;
                Saturation = s.Saturation;
                SelectedTint = s.SelectedTint;
                TintHue = s.TintHue;
                MatchLuminance = s.MatchLuminance;
                MatchLimit = s.MatchLimit;
                MatchTarget = s.MatchTarget;
                MatchGlobal = s.MatchGlobal;
                MaskAmount = s.MaskAmount;
                MaskEnd = s.MaskEnd;
                MaskCurve = s.MaskCurve;
                GrainAmount = s.GrainAmount;
                GrainSize = s.GrainSize;
                GrainRoughness = s.GrainRoughness;
                GrainColor = s.GrainColor;
                GrainHardness = s.GrainHardness;
                Neutralize = s.Neutralize;
                Warmth = s.Warmth;
                SelectedMetadataMode = s.SelectedMetadataMode;
                SelectedColorMode = s.SelectedColorMode;
                OutputFolder = s.OutputFolder;
                NamePattern = s.NamePattern;
                HiddenSteps = s.Hidden;
                SelectedUpscaler = UpscaleOption.Of(s.Upscaler);
                UpscaleFidelity = s.UpscaleFidelity;
                UpscaleDetails = s.UpscaleDetails;
                UpscaleTwice = s.UpscaleTwice;
            }
            finally
            {
                _isSyncingSize = false;
            }
        }

        OnPropertyChanged(nameof(DpiPurpose));
        OnPropertyChanged(nameof(DpiTip));
        NotifySizeDerived();
        ResetHistoryBaseline();
    }

    /// <summary>For changes made by the program (loading, cropping, undo itself), which must not create undo steps.</summary>
    private HistorySuppression SuppressHistory() => new(this);

    private readonly struct HistorySuppression : IDisposable
    {
        private readonly MainViewModel _vm;

        public HistorySuppression(MainViewModel vm)
        {
            _vm = vm;
            _vm._suppressHistory++;
        }

        public void Dispose() => _vm._suppressHistory--;
    }
}
