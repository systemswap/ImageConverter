using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageConverter.Models;

namespace ImageConverter.ViewModels;

// Rückgängig für Einstellungen.
//
// Jede Einstellung, die man in der linken Spalte (oder per Auswahlrahmen) ändert,
// lässt sich zurücknehmen. Ein Schritt entsteht pro Bedienvorgang, nicht nach Zeit:
// – Alles, was ein Vorgang ändert, gehört zusammen (z. B. Breite samt verknüpfter Höhe,
//   DPI samt neu berechneter Pixel).
// – Regler und Eingabefelder: aufeinanderfolgende Vorgänge am selben Element werden ein
//   Schritt — Hin- und Herschieben, Pausen und erneutes Anfassen ergeben nur einen.
//   Abgeschlossen wird er, sobald etwas anderes bedient wird, bei Rückgängig,
//   Bildwechsel, Bearbeiten oder Export.
// – Auswahlrahmen: jedes Loslassen der Maus ist ein Schritt (CommitSettingsStep).
// – Haken und Auswahllisten: jeder Vorgang ist ein eigener Schritt.
// Ein Schritt speichert den kompletten Stand der Einstellungen davor.
public partial class MainViewModel
{
    /// <summary>Vollständiger Stand aller Einstellungen, die Rückgängig zurückholen kann.</summary>
    internal sealed record SettingsState(
        string SelectedFormat, double Quality, bool Lossless,
        string SelectedSizeUnit, bool KeepAspectRatio, bool WidthIsLeading,
        int TargetWidth, int TargetHeight, double PrintWidthIn, double PrintHeightIn,
        ResizeCalculator Calc,
        FilterOption SelectedFilter, bool DoNotEnlarge, bool GammaCorrect,
        int Dpi, bool DpiMetadataOnly,
        string SelectedAspectPreset, Rect? CropSelection, double CornerRadius,
        double SharpenAmount, double SharpenRadius, double NoiseReduction,
        double Dehaze, double Brightness, double Contrast, double Shadows, double Highlights, double Vibrance, double Saturation,
        string SelectedTint, double TintHue, double MatchLuminance, double Neutralize, double Warmth,
        string SelectedMetadataMode, string SelectedColorMode,
        string OutputFolder, string NamePattern,
        int SourceWidth, int SourceHeight,   // Bildgröße, für die dieser Stand gilt
        double DehazePasses, double DehazeClarity, double DehazeContrast, double DehazeVibrance,
        double DehazeSharpen, double DehazeEdge, double DehazeBrightness, double DehazeDeband, double DehazeCast,
        bool HighBitDepth,
        EditSteps Hidden = EditSteps.None,    // ausgeschaltete Funktionen (Liste „Bearbeitungen“)
        double ColorNoiseReduction = 0,
        double RotationAngle = 0,             // noch nicht übernommene freie Drehung — gehört zum Rahmen
        WbSample? WbNeutral = null,           // Weißabgleich: gemittelte Pipetten-Messungen
        WbSample? WbBlack = null,
        WbSample? WbWhite = null,
        double MatchLimit = 100,            // Luminanz: Schwellwert der Bereiche, hellere bleiben
        double MatchTarget = 0,             // Luminanz: Zielwert
        double MatchGlobal = 0,             // Luminanz: Gesamt
        double MaskAmount = 0,              // Luminanzmaske: Stärke, Auslaufen ab, Verlauf
        double MaskEnd = 75,
        double MaskCurve = 30);

    /// <summary>
    /// Eigenschaften, deren Änderung einen Rückgängig-Schritt erzeugt. Der Wert ist die
    /// Gruppe für stufenlose Elemente (Regler, Eingabefelder) — Vorgänge derselben Gruppe
    /// hintereinander werden zusammengefasst. null: jeder Vorgang ist ein eigener Schritt.
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
        [nameof(DoNotEnlarge)] = null,
        [nameof(GammaCorrect)] = null,
        [nameof(Dpi)] = "DPI",
        [nameof(DpiMetadataOnly)] = null,
        [nameof(SelectedAspectPreset)] = null,
        [nameof(CropSelection)] = "Auswahl",
        [nameof(RotationAngle)] = "Auswahl",   // dreht den Rahmen mit — ein Schritt
        [nameof(WbNeutral)] = null,            // jeder Pipetten-Klick ein Schritt
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
        [nameof(Neutralize)] = "Ausgleichen",
        [nameof(Warmth)] = "Farbe",
        [nameof(SelectedMetadataMode)] = null,
        [nameof(SelectedColorMode)] = null,
        [nameof(OutputFolder)] = "Ordner",
        [nameof(NamePattern)] = "Dateiname",
        [nameof(HiddenSteps)] = null,
    };

    /// <summary>
    /// true (App): Änderungen innerhalb eines Bedienvorgangs werden gesammelt und erst
    /// danach ausgewertet. Tests setzen false — dann zählt jede Änderung sofort.
    /// </summary>
    internal bool DeferHistoryTurns { get; init; } = true;

    private SettingsState? _committedSettings;   // Stand nach dem letzten Schritt
    private SettingsState? _settled;             // Stand nach dem letzten Bedienvorgang
    private SettingsState? _pendingBaseline;     // Stand vor dem noch offenen Schritt
    private string? _pendingGroup;               // Gruppe des offenen Schritts (null: abgeschlossen bei nächster Aktion)

    // Laufender Bedienvorgang: Stand davor und was er geändert hat
    private SettingsState? _turnBaseline;
    private readonly HashSet<string> _turnProperties = [];
    private int _suppressHistory;                // > 0: Änderungen kommen vom Programm, nicht vom Nutzer

    internal bool HasPendingSettings => _pendingBaseline is not null || _turnBaseline is not null;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && TrackedSettings.ContainsKey(name))
        {
            OnSettingChanged(name);
            RequestEditRows();   // Liste „Bearbeitungen“ folgt den Reglern
        }
    }

    private void OnSettingChanged(string name)
    {
        if (_suppressHistory > 0 || _settled is null) return;

        var first = _turnBaseline is null;
        _turnBaseline ??= _settled;
        _turnProperties.Add(name);

        if (!DeferHistoryTurns)
        {
            EndTurn();
        }
        else if (first)
        {
            // Folgeänderungen (verknüpfte Höhe …) laufen noch im selben Aufruf;
            // ausgewertet wird, wenn der Vorgang durch ist — vor der nächsten Eingabe.
            Dispatcher.UIThread.Post(EndTurn, DispatcherPriority.Normal);
            NotifyUndoRedo();
        }
    }

    /// <summary>Ein Bedienvorgang ist durch: zum offenen Schritt dazu oder neuer Schritt.</summary>
    private void EndTurn()
    {
        if (_turnBaseline is not { } before) return;

        var groups = _turnProperties.Select(p => TrackedSettings[p]).ToList();
        var group = groups.Any(g => g is null) ? null : string.Join("+", groups.Distinct().Order());
        _turnBaseline = null;
        _turnProperties.Clear();

        // Anderes Element (oder Haken/Liste) bedient: offenen Schritt abschließen
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

    /// <summary>Legt den offenen Schritt ab; <paramref name="after"/> ist der Stand danach.</summary>
    private void FinishPending(SettingsState after)
    {
        if (_pendingBaseline is not { } before) return;
        _pendingBaseline = null;
        _pendingGroup = null;
        _committedSettings = after;

        // Hin und zurück geschoben, ohne dass sich unterm Strich etwas geändert hat
        if (after == before) return;

        // Im Einzelmodus gehört der Schritt zum aktuellen Bild — Rückgängig springt dorthin
        _undo.Push(new Snapshot(ApplyToAll ? null : CurrentPath, null, before));
        ClearRedo();
    }

    /// <summary>Offene Einstellungsänderungen jetzt als Schritt ablegen.</summary>
    internal void CommitPendingSettings()
    {
        EndTurn();
        FinishPending(CaptureSettings());
        _settled = CaptureSettings();
        NotifyUndoRedo();
    }

    /// <summary>Ende einer Geste (z. B. Maus losgelassen am Auswahlrahmen): eigener Schritt.</summary>
    [RelayCommand]
    private void CommitSettingsStep() => CommitPendingSettings();

    /// <summary>Ab hier zählen Änderungen neu — nach Laden, Rückgängig, Bildwechsel.</summary>
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
        SelectedFilter, DoNotEnlarge, GammaCorrect,
        Dpi, DpiMetadataOnly,
        SelectedAspectPreset, CropSelection, CornerRadius,
        SharpenAmount, SharpenRadius, NoiseReduction,
        Dehaze, Brightness, Contrast, Shadows, Highlights, Vibrance, Saturation, SelectedTint, TintHue,
        MatchLuminance, Neutralize, Warmth,
        SelectedMetadataMode, SelectedColorMode,
        OutputFolder, NamePattern,
        SourceWidth, SourceHeight,
        DehazePasses, DehazeClarity, DehazeContrast, DehazeVibrance, DehazeSharpen, DehazeEdge, DehazeBrightness, DehazeDeband, DehazeCast,
        HighBitDepth, HiddenSteps, ColorNoiseReduction, RotationAngle, WbNeutral, WbBlack, WbWhite, MatchLimit, MatchTarget, MatchGlobal, MaskAmount, MaskEnd, MaskCurve);

    /// <summary>Stellt einen gespeicherten Stand wieder her, ohne neue Schritte zu erzeugen.</summary>
    private void ApplySettings(SettingsState s)
    {
        // Stand für ein anders großes Bild (z. B. „für alle" nach Bildwechsel):
        // Größe passend umrechnen, statt fremde Pixelmaße zu übernehmen
        if (s.SourceWidth != SourceWidth || s.SourceHeight != SourceHeight)
            s = AdaptToImage(s, SourceWidth, SourceHeight);

        using (SuppressHistory())
        {
            _isSyncingSize = true;   // nichts nachrechnen — der Stand ist vollständig
            try
            {
                SelectedFormat = s.SelectedFormat;
                Quality = s.Quality;
                Lossless = s.Lossless;
                HighBitDepth = s.HighBitDepth;
                SelectedSizeUnit = s.SelectedSizeUnit;
                KeepAspectRatio = s.KeepAspectRatio;
                Dpi = s.Dpi;
                DpiMetadataOnly = s.DpiMetadataOnly;
                TargetWidth = s.TargetWidth;
                TargetHeight = s.TargetHeight;
                WidthIsLeading = s.WidthIsLeading;
                _printWidthIn = s.PrintWidthIn;
                _printHeightIn = s.PrintHeightIn;
                _calc = s.Calc;
                SelectedFilter = s.SelectedFilter;
                DoNotEnlarge = s.DoNotEnlarge;
                GammaCorrect = s.GammaCorrect;
                SelectedAspectPreset = s.SelectedAspectPreset;
                RotationAngle = s.RotationAngle;   // vor der Auswahl: der Winkel setzt sonst den automatischen Rahmen
                WbNeutral = s.WbNeutral;
                WbBlack = s.WbBlack;
                WbWhite = s.WbWhite;
                CropSelection = s.CropSelection;
                CornerRadius = s.CornerRadius;   // nach der Auswahl, die den Radius sonst begrenzt
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
                Neutralize = s.Neutralize;
                Warmth = s.Warmth;
                SelectedMetadataMode = s.SelectedMetadataMode;
                SelectedColorMode = s.SelectedColorMode;
                OutputFolder = s.OutputFolder;
                NamePattern = s.NamePattern;
                HiddenSteps = s.Hidden;
            }
            finally
            {
                _isSyncingSize = false;
            }
        }

        OnPropertyChanged(nameof(DpiPurpose));
        NotifySizeDerived();
        ResetHistoryBaseline();
    }

    /// <summary>Für Änderungen durch das Programm (Laden, Zuschnitt, Rückgängig selbst).</summary>
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
