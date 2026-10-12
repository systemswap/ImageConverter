using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// Format, Metadaten, Farbe, Ausgabe — und der Export selbst samt Größenschätzung.
public partial class MainViewModel
{
    private static readonly Dictionary<string, OutputFormat> FormatByLabel = new()
    {
        ["WebP"] = OutputFormat.WebP, ["PNG"] = OutputFormat.Png, ["JPEG"] = OutputFormat.Jpeg,
        ["AVIF"] = OutputFormat.Avif, ["JPEG XL"] = OutputFormat.JpegXl, ["TIFF"] = OutputFormat.Tiff,
        ["BMP"] = OutputFormat.Bmp, ["ICO"] = OutputFormat.Ico,
    };

    private static readonly Dictionary<string, MetadataMode> MetadataByLabel = new()
    {
        ["Alles behalten"] = MetadataMode.KeepAll,
        ["Nur GPS entfernen"] = MetadataMode.RemoveGps,
        ["Alles entfernen"] = MetadataMode.RemoveAll,
    };

    private static readonly Dictionary<string, ColorMode> ColorByLabel = new()
    {
        ["Profil behalten"] = ColorMode.KeepProfile,
        ["Nach sRGB wandeln"] = ColorMode.ConvertToSrgb,
        ["Profil verwerfen"] = ColorMode.DiscardProfile,
    };

    private OutputFormat CurrentFormat =>
        FormatByLabel.TryGetValue(SelectedFormat ?? "", out var f) ? f : OutputFormat.WebP;

    // -------------------------------------------------------------- Format

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTransparencyWarning), nameof(QualityApplies), nameof(LosslessAvailable), nameof(HighBitDepthAvailable))]
    public partial string SelectedFormat { get; set; }

    [ObservableProperty] public partial double Quality { get; set; } = 82;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityApplies))]
    public partial bool Lossless { get; set; }

    /// <summary>PNG, TIFF, BMP, ICO speichern immer verlustfrei — dort gibt es keinen Qualitätsregler.</summary>
    public bool QualityApplies => !ImageExporter.IsAlwaysLossless(CurrentFormat) && !(Lossless && LosslessAvailable);

    /// <summary>Nur WebP, AVIF und JPEG XL können wahlweise verlustfrei.</summary>
    public bool LosslessAvailable => CurrentFormat is OutputFormat.WebP or OutputFormat.Avif or OutputFormat.JpegXl;

    partial void OnSelectedFormatChanged(string value) => RequestEstimate();
    partial void OnQualityChanged(double value) => RequestEstimate();
    partial void OnLosslessChanged(bool value) => RequestEstimate();

    /// <summary>PNG und TIFF wahlweise mit 16 Bit je Kanal (Quellen mit 16 Bit bleiben es ohnehin).</summary>
    [ObservableProperty] public partial bool HighBitDepth { get; set; }

    public bool HighBitDepthAvailable => ImageExporter.SupportsHighBitDepth(CurrentFormat);

    partial void OnHighBitDepthChanged(bool value) => RequestEstimate();

    // ------------------------------------------------- Metadaten und Farbe

    [ObservableProperty] public partial string SelectedMetadataMode { get; set; }
    [ObservableProperty] public partial string SelectedColorMode { get; set; }

    partial void OnSelectedColorModeChanged(string value) => RequestEstimate();

    // ------------------------------------------------------------- Ausgabe

    /// <summary>Leer = Unterordner „konvertiert" neben dem jeweiligen Original.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputFolderDisplay), nameof(HasCustomOutputFolder))]
    public partial string OutputFolder { get; set; } = "";

    [ObservableProperty] public partial string NamePattern { get; set; } = OutputNaming.DefaultPattern;

    public bool HasCustomOutputFolder => !string.IsNullOrWhiteSpace(OutputFolder);

    public string OutputFolderDisplay => HasCustomOutputFolder
        ? OutputFolder
        : Tr.T("neben dem Original › {0}", OutputNaming.DefaultSubfolder);

    [RelayCommand]
    private void ResetOutputFolder() => OutputFolder = "";

    // ------------------------------------------------------------- Export

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(PrintCommand))]
    public partial bool IsExporting { get; set; }

    [ObservableProperty] public partial string? StatusMessage { get; set; }

    /// <summary>Fortschritt des Speicherns über alle Schritte, 0–100.</summary>
    [ObservableProperty] public partial double ExportProgress { get; set; }

    /// <summary>Was beim Speichern gerade passiert (unter dem Balken).</summary>
    [ObservableProperty] public partial string ExportStep { get; set; } = "";

    /// <summary>Ordner des zuletzt exportierten Bildes — für „Ordner öffnen".</summary>
    [ObservableProperty] public partial string? LastExportFolder { get; set; }

    private bool CanExport => HasImage && !IsExporting;

    /// <summary>Die Ansicht öffnet das Export-Fenster (modal) und wartet, bis es zu ist.</summary>
    public event Func<ExportDialogViewModel, Task>? MultiExportRequested;

    /// <summary>Die Ansicht öffnet den Druckdialog (modal).</summary>
    public event Func<PrintDialogViewModel, Task>? PrintRequested;

    internal string? CurrentImagePath => CurrentPath;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Print()
    {
        CommitPendingSettings();
        if (!ApplyToAll && CurrentPath is { } current)
            _fileSettings[current] = CaptureSettings();
        if (PrintRequested is { } open)
            await open(new PrintDialogViewModel(this));
    }

    /// <summary>Ergebnis aus dem Druckdialog in der Fußleiste anzeigen.</summary>
    internal void ReportPrint(string message, string? pdfPath)
    {
        LastExportFolder = pdfPath is not null ? Path.GetDirectoryName(pdfPath) : null;
        ErrorMessage = null;
        StatusMessage = message;
    }

    /// <summary>Ergebnis aus dem Export-Fenster in der Fußleiste anzeigen.</summary>
    internal void ReportExport(string folder, string message)
    {
        LastExportFolder = folder;
        ErrorMessage = null;
        StatusMessage = message;
    }

    /// <summary>Die aktuellen Einstellungen als Export-Auftrag in voller Auflösung.</summary>
    private ExportOptions BuildExportOptions(int width, int height, double sharpenScale = 1) => new(
        CurrentFormat,
        (int)Math.Round(Quality),
        Lossless && LosslessAvailable,
        width, height,
        SelectedFilter.Filter, GammaCorrect,
        EffectiveNoise,
        EffectiveSharpen,
        SharpenRadius * sharpenScale,
        Dpi,
        MetadataByLabel.TryGetValue(SelectedMetadataMode ?? "", out var m) ? m : MetadataMode.KeepAll,
        ColorByLabel.TryGetValue(SelectedColorMode ?? "", out var c) ? c : ColorMode.ConvertToSrgb,
        CurrentAdjustments(),
        HighBitDepth && HighBitDepthAvailable,
        EffectiveColorNoise,
        EffectiveGrain);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (_fileIndex < 0 || await Task.Run(() => ResultDocument) is not { } doc) return;   // ohne Ausgeblendetes (KI …), mit Ausrichten

        CommitPendingSettings();

        // Mehrere Bilder: Export-Fenster mit Liste — öffnet die Ansicht
        if (_files.Count > 1 && MultiExportRequested is { } openDialog)
        {
            if (!ApplyToAll && CurrentPath is { } current)
                _fileSettings[current] = CaptureSettings();
            await openDialog(new ExportDialogViewModel(this));
            return;
        }
        var sourcePath = _files[_fileIndex];
        var (w, h) = EffectiveTarget();
        var options = BuildExportOptions(w, h);

        ExportProgress = 0;
        ExportStep = Tr.T("Vorbereiten …");
        IsExporting = true;
        StatusMessage = null;
        ErrorMessage = null;
        try
        {
            var folder = OutputNaming.ResolveFolder(OutputFolder, sourcePath);
            var fileName = OutputNaming.BuildFileName(NamePattern, sourcePath, w, h,
                _fileIndex + 1, DateTime.Now, ImageExporter.Extension(options.Format));

            // Meldungen kommen aus dem Rechen-Thread und landen über den UI-Kontext hier
            var progress = new Progress<WorkProgress>(p =>
            {
                ExportProgress = p.Fraction * 100;
                ExportStep = p.Step;
            });
            var (path, bytes) = await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var target = OutputNaming.MakeUnique(folder, fileName);
                return (target, ImageExporter.Export(doc, options, target, progress));
            });

            LastExportFolder = folder;
            StatusMessage = Tr.T("Gespeichert: {0}  ·  {1}", Path.GetFileName(path), FormatBytes(bytes));
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Export fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }


    // ------------------------------------------------- Größenschätzung

    // Probe für die Schätzung: so klein wie möglich, damit auch AVIF schnell geht.
    // Ist das Ziel selbst klein genug, wird exakt in Zielgröße kodiert.
    private const int EstimateMaxEdge = SizeEstimator.DefaultSampleEdge;

    // Verkleinerte Ausgangsfassung für die Proben — spart das teure Skalieren aus
    // dem vollen Bild bei jeder Reglerbewegung.
    private const int EstimateBaseEdge = 2048;
    private MagickImage? _estimateBase;
    private (MagickImage Doc, ResampleFilter Filter, bool Gamma)? _estimateBaseKey;

    [ObservableProperty] public partial string EstimatedSize { get; set; } = "—";
    [ObservableProperty] public partial string SavingsText { get; set; } = "Größe nach dem Export";
    [ObservableProperty] public partial bool IsEstimating { get; set; }

    private CancellationTokenSource? _estimateCts;
    private Task _estimateTask = Task.CompletedTask;

    /// <summary>Neue Schätzung anfordern — erst nach kurzer Ruhe, ältere werden abgebrochen.</summary>
    private void RequestEstimate()
    {
        _estimateCts?.Cancel();
        if (_history?.Current is null) return;

        var cts = _estimateCts = new CancellationTokenSource();
        _estimateTask = EstimateAsync(_estimateTask, cts.Token);
    }

    private async Task EstimateAsync(Task previous, CancellationToken ct)
    {
        try
        {
            // Nacheinander, nie gleichzeitig: alle teilen sich die verkleinerte Fassung
            try { await previous; } catch { /* vorige Schätzung abgebrochen */ }
            await Task.Delay(350, ct);
            if (_history is not { } h || await Task.Run(() => ResultDocument) is not { } doc) return;

            var (tw, th) = EffectiveTarget();
            var options = BuildExportOptions(tw, th);
            var originalBytes = h.FileBytes;
            var baseKey = (doc, options.Filter, options.GammaCorrect);

            IsEstimating = true;
            var estimate = await Task.Run(() =>
            {
                // Proben aus einer verkleinerten Fassung rechnen statt aus dem vollen Bild —
                // die wird nur neu erstellt, wenn sich Bild, Filter oder Gamma ändern.
                if (_estimateBase is null || _estimateBaseKey != baseKey)
                {
                    _estimateBase?.Dispose();
                    var s = Math.Min(1.0, (double)EstimateBaseEdge / Math.Max((int)doc.Width, (int)doc.Height));
                    _estimateBase = ImageOps.Resize(doc,
                        Math.Max(1, (int)Math.Round(doc.Width * s)), Math.Max(1, (int)Math.Round(doc.Height * s)),
                        options.Filter, options.GammaCorrect);
                    _estimateBaseKey = baseKey;
                }

                ct.ThrowIfCancellationRequested();
                return SizeEstimator.EstimateSize(_estimateBase, options, EstimateMaxEdge, ct);
            }, ct);

            var bytes = estimate.Bytes;
            EstimatedSize = (estimate.IsExact ? "" : "≈ ") + FormatBytes(bytes);

            if (originalBytes > 0)
            {
                var ratio = (double)bytes / originalBytes;
                SavingsText = ratio <= 1
                    ? Tr.T("{0} % kleiner als das Original ({1})", Math.Round((1 - ratio) * 100), FormatBytes(originalBytes))
                    : Tr.T("{0} % größer als das Original ({1})", Math.Round((ratio - 1) * 100), FormatBytes(originalBytes));
            }
        }
        catch (OperationCanceledException)
        {
            // Neue Einstellung kam dazwischen
        }
        catch (Exception ex)
        {
            EstimatedSize = "—";
            SavingsText = Tr.T("Schätzung nicht möglich: {0}", ex.Message);
        }
        finally
        {
            if (_estimateCts?.Token == ct) IsEstimating = false;
        }
    }
}
