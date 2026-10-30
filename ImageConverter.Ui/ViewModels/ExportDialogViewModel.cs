using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageConverter.Imaging;

namespace ImageConverter.ViewModels;

/// <summary>One row in the export window.</summary>
public partial class ExportItem(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);

    [ObservableProperty] public partial bool Include { get; set; } = true;
    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }
    [ObservableProperty] public partial string Resolution { get; set; } = "…";
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool Failed { get; set; }
    [ObservableProperty] public partial bool Done { get; set; }

    /// <summary>Estimated duration if upscaling still has to be computed during the export, otherwise null.</summary>
    internal TimeSpan? UpscaleEstimate { get; set; }
}

/// <summary>
/// Export of several images: a list with check boxes, thumbnail, resolution and destination.
/// Each image is exported with its own settings (per-image mode) or with the shared settings
/// converted to fit that image.
/// </summary>
public partial class ExportDialogViewModel : ViewModelBase
{
    private const int ThumbSize = 72;

    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _closing = new();
    private bool _cancelRequested;
    private CancellationTokenSource? _exportCts;   // also cancels a running upscaling

    /// <summary>Asks a question (modal over this window); result: index of the answer, −1 if the dialog was closed.</summary>
    public event Func<ChoiceRequest, Task<int>>? ChoiceRequested;

    public ExportDialogViewModel(MainViewModel main)
    {
        _main = main;
        Items = new ObservableCollection<ExportItem>(main.OpenFiles.Select(p => new ExportItem(p) { Include = main.PreselectForExport(p) }));
        foreach (var item in Items)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExportItem.Include)) NotifySelection();
            };
        Destination = main.OutputFolderDisplay;
    }

    public ObservableCollection<ExportItem> Items { get; }
    public string Destination { get; }
    public string ModeHint => _main.ApplyToAll
        ? Tr.T("Gleiche Einstellungen für alle — Größe passend zu jedem Bild umgerechnet.")
        : Tr.T("Jedes Bild mit seinen eigenen Einstellungen.");

    public int SelectedCount => Items.Count(i => i.Include);
    public string ExportButtonText => SelectedCount == 1 ? Tr.T("1 Bild exportieren") : Tr.T("{0} Bilder exportieren", SelectedCount);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsExporting { get; set; }

    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }

    /// <summary>What is happening right now: image n of m and the current step within it.</summary>
    [ObservableProperty] public partial string Step { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ExportButtonText));
        ExportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll() { foreach (var i in Items) i.Include = true; }

    [RelayCommand]
    private void SelectNone() { foreach (var i in Items) i.Include = false; }

    /// <summary>Fills in thumbnails and resolutions one image after another in the background.</summary>
    public async Task LoadAsync()
    {
        foreach (var item in Items)
        {
            if (_closing.IsCancellationRequested) return;
            try
            {
                // Header data and a small thumbnail only; the full image is loaded by the export itself.
                var (w, h, dpi, thumb, tw, th) = await _main.GetListEntryAsync(item.Path, ThumbSize);
                var state = _main.SettingsFor(item.Path, w, h, dpi);
                var options = MainViewModel.OptionsFor(state);
                var (_, fileName) = _main.PlanOutput(item.Path, state, options);

                item.Resolution = $"{options.Width} × {options.Height} px";
                item.UpscaleEstimate = _main.UpscaleNeededFor(item.Path, w, h, state, options);
                item.Details = fileName;
                item.Thumbnail = BitmapOps.FromBgra(thumb, tw, th);
            }
            catch (Exception ex)
            {
                item.Resolution = "—";
                item.Status = Tr.T("nicht lesbar");
                item.Failed = true;
                item.Include = false;
                item.Details = ex.Message;
            }
        }
    }

    private bool CanExport => !IsExporting && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        IsExporting = true;
        IsDone = false;
        Progress = 0;
        _cancelRequested = false;

        var selected = Items.Where(i => i.Include).ToList();

        // AI upscaling still to be computed: ask once for all images.
        var upscale = true;
        var needs = selected.Where(i => i.UpscaleEstimate is not null).ToList();
        if (needs.Count > 0 && ChoiceRequested is { } ask)
        {
            var duration = Core.Ai.Upscale.Upscalers.FormatWithEnd(TimeSpan.FromTicks(needs.Sum(i => i.UpscaleEstimate!.Value.Ticks)), DateTime.Now);
            var answer = await ask(new ChoiceRequest(Tr.T("Upscaling noch nicht berechnet"),
                needs.Count == 1
                    ? Tr.T("Für 1 Bild ist ein KI-Upscaler gewählt, aber noch nicht berechnet. Das dauert etwa {0}.", duration)
                    : Tr.T("Für {0} Bilder ist ein KI-Upscaler gewählt, aber noch nicht berechnet. Das dauert zusammen etwa {1}.", needs.Count, duration),
                [Tr.T("Jetzt berechnen und speichern (≈ {0})", duration), Tr.T("Ohne KI speichern (nur Filter)"), Tr.T("Abbrechen")]));
            if (answer is < 0 or 2)
            {
                IsExporting = false;
                return;
            }
            upscale = answer == 0;
        }
        _exportCts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var ok = 0;
        long total = 0;
        string? lastFolder = null;

        for (var n = 0; n < selected.Count; n++)
        {
            if (_cancelRequested || _closing.IsCancellationRequested) break;

            var item = selected[n];
            item.Status = Tr.T("wird exportiert …");
            item.Failed = false;
            // One progress bar for all images: each image gets an equal share, subdivided by its steps.
            var index = n;
            Step = Tr.T("Bild {0} von {1} · {2}", index + 1, selected.Count, Tr.T("Laden …"));
            var progress = new Progress<WorkProgress>(p =>
            {
                Progress = (index + p.Fraction) * 100 / selected.Count;
                Step = Tr.T("Bild {0} von {1} · {2}", index + 1, selected.Count, p.Step);
                item.Status = p.Step;
            });
            try
            {
                var (path, bytes) = await _main.ExportFileAsync(item.Path, progress, upscale, _exportCts.Token);
                item.Status = $"✓  {FormatBytes(bytes)}";
                item.Details = Path.GetFileName(path);   // actual name, possibly with a " (2)" suffix
                item.Done = true;
                lastFolder = Path.GetDirectoryName(path);
                total += bytes;
                ok++;
            }
            catch (OperationCanceledException)
            {
                item.Status = Tr.T("abgebrochen");
                break;
            }
            catch (Exception ex)
            {
                item.Status = Tr.T("Fehler: {0}", ex.Message);
                item.Failed = true;
            }

            Progress = (n + 1) * 100.0 / selected.Count;
        }

        var failed = selected.Count(i => i.Failed);
        Summary = _cancelRequested
            ? Tr.T("Abgebrochen — {0} von {1} exportiert.", ok, selected.Count)
            : failed > 0
                ? Tr.T("{0} exportiert, {1} fehlgeschlagen.", ok, failed)
                : ok == 1 ? Tr.T("Fertig — 1 Bild exportiert ({0}).", FormatBytes(total)) : Tr.T("Fertig — {0} Bilder exportiert ({1}).", ok, FormatBytes(total));

        if (lastFolder is not null)
            _main.ReportExport(lastFolder, Summary);

        IsExporting = false;
        IsDone = true;
    }

    /// <summary>Stops the export after the current image; a running upscaling is cancelled immediately.</summary>
    [RelayCommand]
    private void CancelExport()
    {
        _cancelRequested = true;
        _exportCts?.Cancel();
    }

    /// <summary>Called when the window closes: stops loading thumbnails and cancels a running export.</summary>
    public void OnClosing()
    {
        _cancelRequested = true;
        _closing.Cancel();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
