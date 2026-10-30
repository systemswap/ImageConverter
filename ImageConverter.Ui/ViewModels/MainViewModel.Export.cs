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

// Format, metadata, colour, output, and the export itself including the size estimate.
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
    [NotifyPropertyChangedFor(nameof(ShowTransparencyWarning), nameof(QualityApplies), nameof(LosslessAvailable), nameof(HighBitDepthAvailable), nameof(HighBitDepthRecommended))]
    public partial string SelectedFormat { get; set; }

    [ObservableProperty] public partial double Quality { get; set; } = 82;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityApplies))]
    public partial bool Lossless { get; set; }

    /// <summary>The quality slider applies: not for PNG, TIFF, BMP and ICO, which always save losslessly, and not when lossless saving is chosen.</summary>
    public bool QualityApplies => !ImageExporter.IsAlwaysLossless(CurrentFormat) && !(Lossless && LosslessAvailable);

    /// <summary>Only WebP, AVIF and JPEG XL can optionally save losslessly.</summary>
    public bool LosslessAvailable => CurrentFormat is OutputFormat.WebP or OutputFormat.Avif or OutputFormat.JpegXl;

    partial void OnSelectedFormatChanged(string value)
    {
        RequestEstimate();
        UpdateHighBitDepthRecommendation();
    }
    partial void OnQualityChanged(double value) => RequestEstimate();
    partial void OnLosslessChanged(bool value) => RequestEstimate();

    /// <summary>PNG and TIFF optionally with 16 bits per channel (sources with more than 8 bits keep 16 bits anyway).</summary>
    [ObservableProperty] public partial bool HighBitDepth { get; set; }

    public bool HighBitDepthAvailable => ImageExporter.SupportsHighBitDepth(CurrentFormat);

    /// <summary>
    /// "recommended" next to the check box: strong tonal editing (<see cref="Adjustments.SpreadsTones"/>); 16 bits keep
    /// gradients free of banding, the file becomes about twice as large. Updated on every change (<see cref="RequestPreview"/>).
    /// </summary>
    public bool HighBitDepthRecommended => HighBitDepthAvailable && CurrentAdjustments().SpreadsTones;

    // Automatic mode: whether the app set the tick itself (then it also removes it again itself), and the last recommendation state
    private bool _highBitDepthByApp;
    private bool _highBitDepthWasRecommended;
    private bool _settingHighBitDepth;

    /// <summary>
    /// Updates the recommendation (on every preview). If it lapses, the app removes a tick it set itself, because
    /// otherwise the file would become twice as large for no benefit; a tick set by hand stays. When a state is
    /// restored (undo, another image) the saved tick applies and nothing is changed.
    /// </summary>
    private void UpdateHighBitDepthRecommendation()
    {
        OnPropertyChanged(nameof(HighBitDepthRecommended));
        var now = HighBitDepthRecommended;
        var was = _highBitDepthWasRecommended;
        _highBitDepthWasRecommended = now;
        if (_suppressHistory > 0) return;
        if (was && !now && HighBitDepth && _highBitDepthByApp) SetHighBitDepthByApp(false);
    }

    /// <summary>
    /// After every settings change made in the UI: if the edit is above the threshold, the app sets the tick, even if it
    /// had been removed by hand before, because a tick set once too often is preferable to a forgotten one. It can be
    /// removed at any time. Silent, without its own undo step: undoing the triggering slider also undoes the tick.
    /// </summary>
    /// <param name="changed">Name of the property that changed.
    /// </summary>
    private void TickHighBitDepthIfRecommended(string changed)
    {
        if (changed == nameof(HighBitDepth) || HighBitDepth || !HighBitDepthRecommended) return;
        SetHighBitDepthByApp(true);
    }

    private void SetHighBitDepthByApp(bool value)
    {
        _settingHighBitDepth = true;
        try
        {
            using (SuppressHistory()) HighBitDepth = value;
        }
        finally
        {
            _settingHighBitDepth = false;
        }
        _highBitDepthByApp = value;
    }

    partial void OnHighBitDepthChanged(bool value)
    {
        if (!_settingHighBitDepth && _suppressHistory == 0) _highBitDepthByApp = false;   // set by hand: stays as it is
        RequestEstimate();
    }

    // ------------------------------------------------- Metadata and colour

    [ObservableProperty] public partial string SelectedMetadataMode { get; set; }
    [ObservableProperty] public partial string SelectedColorMode { get; set; }

    partial void OnSelectedColorModeChanged(string value) => RequestEstimate();

    // ------------------------------------------------------------- Output

    /// <summary>Output folder; empty means the default subfolder (<see cref="OutputNaming.DefaultSubfolder"/>) next to each original.</summary>
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

    /// <summary>Progress of saving across all steps, 0–100.</summary>
    [ObservableProperty] public partial double ExportProgress { get; set; }

    /// <summary>What is happening during saving right now (shown below the progress bar).</summary>
    [ObservableProperty] public partial string ExportStep { get; set; } = "";

    /// <summary>Folder of the most recently exported image, for "Open folder".</summary>
    [ObservableProperty] public partial string? LastExportFolder { get; set; }

    // Not while an image step computes: the file would mix the state before and after it (No. 98)
    private bool CanExport => HasImage && !IsExporting && !IsUpscaling && !IsEditing;

    /// <summary>The view opens the export window (modal) and waits until it is closed.</summary>
    public event Func<ExportDialogViewModel, Task>? MultiExportRequested;

    /// <summary>The view opens the print dialog (modal).</summary>
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

    /// <summary>Shows the result of the print dialog in the status bar.</summary>
    internal void ReportPrint(string message, string? pdfPath)
    {
        LastExportFolder = pdfPath is not null ? Path.GetDirectoryName(pdfPath) : null;
        ErrorMessage = null;
        StatusMessage = message;
    }

    /// <summary>Shows the result of the export window in the status bar.</summary>
    internal void ReportExport(string folder, string message)
    {
        LastExportFolder = folder;
        ErrorMessage = null;
        StatusMessage = message;
    }

    /// <summary>The current settings as an export job at full resolution.</summary>
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
        if (_fileIndex < 0 || await Task.Run(() => ResultDocument) is not { } doc) return;   // without switched-off functions (AI …), with geometry

        CommitPendingSettings();

        // Several images: export window with a list, opened by the view
        if (OpenFiles.Count > 1 && MultiExportRequested is { } openDialog)
        {
            if (!ApplyToAll && CurrentPath is { } current)
                _fileSettings[current] = CaptureSettings();
            await openDialog(new ExportDialogViewModel(this));
            return;
        }
        var sourcePath = _files[_fileIndex];
        var (w, h) = EffectiveTarget();
        var options = BuildExportOptions(w, h);

        // Upscaler selected: the computed ×4 image is the basis (otherwise ask)
        var (cancel, upscaled) = await UpscaleBeforeExportAsync();
        if (cancel) return;
        if (upscaled is not null) options = options with { Upscaled = upscaled };

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

            // Progress reports come from the worker thread and arrive here via the UI context.
            var progress = new Progress<WorkProgress>(p =>
            {
                ExportProgress = p.Fraction * 100;
                ExportStep = p.Step;
            });
            var (path, bytes) = await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var target = OutputNaming.MakeUnique(folder, fileName);
                var written = ImageExporter.Export(doc, options, target, progress);
                PixelPool.Trim(release: true);   // only now are all export buffers free: return memory
                return (target, written);
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


    // ------------------------------------------------- Size estimate

    // Sample for the estimate: as small as possible, so that AVIF is fast too.
    // If the target itself is small enough, it is encoded exactly at the target size.
    private const int EstimateMaxEdge = SizeEstimator.DefaultSampleEdge;

    // Reduced base image for the samples; saves the expensive scaling from
    // the full image on every slider movement.
    private const int EstimateBaseEdge = 2048;
    private MagickImage? _estimateBase;
    private (MagickImage Doc, ResampleFilter Filter, bool Gamma)? _estimateBaseKey;
    // Intermediate results of the samples (dehaze, shadows/highlights, light and colour): otherwise sharpening, grain,
    // quality and format would recompute the whole dehaze twice on every change. Valid for _estimateBase and
    // discarded with it.
    private readonly LookCache _estimateCache = new();

    [ObservableProperty] public partial string EstimatedSize { get; set; } = "—";
    [ObservableProperty] public partial string SavingsText { get; set; } = "Größe nach dem Export";
    [ObservableProperty] public partial bool IsEstimating { get; set; }

    private CancellationTokenSource? _estimateCts;
    private Task _estimateTask = Task.CompletedTask;

    /// <summary>Requests a new estimate, computed only after a short pause; older ones are cancelled.</summary>
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
            // One after another, never concurrently: all share the reduced base image.
            try { await previous; } catch { /* previous estimate cancelled */ }
            await Task.Delay(350, ct);
            // Not while dragging, because it would take computing time from the preview (it is requested again on release).
            if (Interactive) return;
            if (_history is not { } h || await Task.Run(() => ResultDocument) is not { } doc) return;

            var (tw, th) = EffectiveTarget();
            var options = BuildExportOptions(tw, th);
            var originalBytes = h.FileBytes;
            var baseKey = (doc, options.Filter, options.GammaCorrect);

            IsEstimating = true;
            var estimate = await Task.Run(() =>
            {
                // Compute the samples from a reduced version instead of the full image;
                // it is recreated only when the image, filter or gamma changes.
                if (_estimateBase is null || _estimateBaseKey != baseKey)
                {
                    _estimateCache.Clear();
                    _estimateBase?.Dispose();
                    var s = Math.Min(1.0, (double)EstimateBaseEdge / Math.Max((int)doc.Width, (int)doc.Height));
                    _estimateBase = ImageOps.Resize(doc,
                        Math.Max(1, (int)Math.Round(doc.Width * s)), Math.Max(1, (int)Math.Round(doc.Height * s)),
                        options.Filter, options.GammaCorrect);
                    _estimateBaseKey = baseKey;
                }

                ct.ThrowIfCancellationRequested();
                return SizeEstimator.EstimateSize(_estimateBase, options, EstimateMaxEdge, ct, _estimateCache);
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
            // A new setting arrived in the meantime.
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
