using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ImageConverter.Core.Imaging;
using ImageConverter.Models;

namespace ImageConverter.ViewModels;

// Rückgängig für Einstellungen.
//
// Jede Einstellung, die man in der linken Spalte (oder per Auswahlrahmen) ändert,
// lässt sich zurücknehmen. Damit Ziehen an einem Regler nicht hunderte Schritte
// erzeugt, werden Änderungen zu einem Schritt zusammengefasst, sobald man kurz
// innehält (SettleDelayMs). Ein Schritt speichert den kompletten Stand der
// Einstellungen davor — kein Unterschied, sondern ein vollständiges Abbild.
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
        double SharpenAmount, double SharpenRadius, double NoiseReduction, bool UseAiDenoise,
        string SelectedMetadataMode, string SelectedColorMode,
        string OutputFolder, string NamePattern,
        int SourceWidth, int SourceHeight);   // Bildgröße, für die dieser Stand gilt

    /// <summary>Eigenschaften, deren Änderung einen Rückgängig-Schritt erzeugt.</summary>
    private static readonly HashSet<string> TrackedSettings =
    [
        nameof(SelectedFormat), nameof(Quality), nameof(Lossless),
        nameof(SelectedSizeUnit), nameof(KeepAspectRatio), nameof(TargetWidth), nameof(TargetHeight),
        nameof(SelectedFilter), nameof(DoNotEnlarge), nameof(GammaCorrect),
        nameof(Dpi), nameof(DpiMetadataOnly),
        nameof(SelectedAspectPreset), nameof(CropSelection), nameof(CornerRadius),
        nameof(SharpenAmount), nameof(SharpenRadius), nameof(NoiseReduction), nameof(UseAiDenoise),
        nameof(SelectedMetadataMode), nameof(SelectedColorMode),
        nameof(OutputFolder), nameof(NamePattern),
    ];

    /// <summary>Wartezeit nach der letzten Änderung, bis sie ein eigener Schritt wird.
    /// Tests setzen -1 (nie automatisch) und rufen <see cref="CommitPendingSettings"/> selbst.</summary>
    internal int SettleDelayMs { get; set; } = 700;

    private SettingsState? _committedSettings;   // Stand nach dem letzten Schritt
    private SettingsState? _pendingBaseline;     // Stand vor den noch offenen Änderungen
    private CancellationTokenSource? _settleCts;
    private int _suppressHistory;                // > 0: Änderungen kommen vom Programm, nicht vom Nutzer

    private bool HasPendingSettings => _pendingBaseline is not null;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && TrackedSettings.Contains(name))
            OnSettingChanged();
    }

    private void OnSettingChanged()
    {
        if (_suppressHistory > 0 || _committedSettings is null) return;

        if (_pendingBaseline is null)
        {
            _pendingBaseline = _committedSettings;
            NotifyUndoRedo();
        }

        // Zeitfenster neu starten: erst wenn eine Weile Ruhe ist, wird es ein Schritt
        _settleCts?.Cancel();
        if (SettleDelayMs < 0) return;
        var cts = _settleCts = new CancellationTokenSource();
        _ = CommitAfterDelayAsync(cts.Token);
    }

    private async Task CommitAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SettleDelayMs, ct);
            CommitPendingSettings();
        }
        catch (OperationCanceledException)
        {
            // Weitere Änderung kam dazwischen — das neue Zeitfenster übernimmt
        }
    }

    /// <summary>Offene Einstellungsänderungen jetzt als einen Schritt ablegen.</summary>
    internal void CommitPendingSettings()
    {
        _settleCts?.Cancel();
        if (_pendingBaseline is not { } before) return;

        var now = CaptureSettings();
        _pendingBaseline = null;
        _committedSettings = now;

        // Hin und zurück geschoben, ohne dass sich unterm Strich etwas geändert hat
        if (now == before)
        {
            NotifyUndoRedo();
            return;
        }

        // Im Einzelmodus gehört der Schritt zum aktuellen Bild — Rückgängig springt dorthin
        _undo.Push(new Snapshot(ApplyToAll ? null : CurrentPath, null, before));
        ClearRedo();
        NotifyUndoRedo();
    }

    private SettingsState CaptureSettings() => new(
        SelectedFormat, Quality, Lossless,
        SelectedSizeUnit, KeepAspectRatio, WidthIsLeading,
        TargetWidth, TargetHeight, _printWidthIn, _printHeightIn,
        _calc,
        SelectedFilter, DoNotEnlarge, GammaCorrect,
        Dpi, DpiMetadataOnly,
        SelectedAspectPreset, CropSelection, CornerRadius,
        SharpenAmount, SharpenRadius, NoiseReduction, UseAiDenoise,
        SelectedMetadataMode, SelectedColorMode,
        OutputFolder, NamePattern,
        SourceWidth, SourceHeight);

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
                CropSelection = s.CropSelection;
                CornerRadius = s.CornerRadius;   // nach der Auswahl, die den Radius sonst begrenzt
                SharpenAmount = s.SharpenAmount;
                SharpenRadius = s.SharpenRadius;
                NoiseReduction = s.NoiseReduction;
                UseAiDenoise = s.UseAiDenoise;
                SelectedMetadataMode = s.SelectedMetadataMode;
                SelectedColorMode = s.SelectedColorMode;
                OutputFolder = s.OutputFolder;
                NamePattern = s.NamePattern;
            }
            finally
            {
                _isSyncingSize = false;
            }
        }

        OnPropertyChanged(nameof(DpiPurpose));
        NotifySizeDerived();
        _committedSettings = CaptureSettings();
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
