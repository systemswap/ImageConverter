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

/// <summary>Eine Zeile im Export-Fenster.</summary>
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
}

/// <summary>
/// Export mehrerer Bilder: Liste mit Haken, Vorschaubild, Auflösung und Ziel.
/// Jedes Bild wird mit seinem eigenen Stand exportiert (Einzelmodus) bzw. mit dem
/// gemeinsamen, auf das Bild umgerechneten Stand.
/// </summary>
public partial class ExportDialogViewModel : ViewModelBase
{
    private const int ThumbSize = 72;

    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _closing = new();
    private bool _cancelRequested;

    public ExportDialogViewModel(MainViewModel main)
    {
        _main = main;
        Items = new ObservableCollection<ExportItem>(main.OpenFiles.Select(p => new ExportItem(p)));
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
        ? "Gleiche Einstellungen für alle — Größe passend zu jedem Bild umgerechnet."
        : "Jedes Bild mit seinen eigenen Einstellungen.";

    public int SelectedCount => Items.Count(i => i.Include);
    public string ExportButtonText => SelectedCount == 1 ? "1 Bild exportieren" : $"{SelectedCount} Bilder exportieren";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsExporting { get; set; }

    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
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

    /// <summary>Vorschaubilder und Auflösungen nacheinander im Hintergrund füllen.</summary>
    public async Task LoadAsync()
    {
        foreach (var item in Items)
        {
            if (_closing.IsCancellationRequested) return;
            try
            {
                var (image, owned, _) = await _main.GetImageAsync(item.Path);
                try
                {
                    var w = (int)image.Width;
                    var h = (int)image.Height;
                    var state = _main.SettingsFor(item.Path, w, h);
                    var options = MainViewModel.OptionsFor(state);
                    var (_, fileName) = _main.PlanOutput(item.Path, state, options);

                    item.Resolution = $"{options.Width} × {options.Height} px";
                    item.Details = fileName;

                    var (thumb, tw, th) = await Task.Run(() =>
                    {
                        using var t = ImageOps.CloneOf(image);
                        t.Thumbnail((uint)ThumbSize, (uint)ThumbSize);
                        return (ImageOps.ToDisplayBgra(t), (int)t.Width, (int)t.Height);
                    });
                    item.Thumbnail = BitmapOps.FromBgra(thumb, tw, th);
                }
                finally
                {
                    if (owned) image.Dispose();
                }
            }
            catch (Exception ex)
            {
                item.Resolution = "—";
                item.Status = "nicht lesbar";
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
        _cancelRequested = false;

        var selected = Items.Where(i => i.Include).ToList();
        var ok = 0;
        long total = 0;
        string? lastFolder = null;

        for (var n = 0; n < selected.Count; n++)
        {
            if (_cancelRequested || _closing.IsCancellationRequested) break;

            var item = selected[n];
            item.Status = "wird exportiert …";
            item.Failed = false;
            try
            {
                var (path, bytes) = await _main.ExportFileAsync(item.Path);
                item.Status = $"✓  {FormatBytes(bytes)}";
                item.Details = Path.GetFileName(path);   // tatsächlicher Name, ggf. mit „ (2)"
                item.Done = true;
                lastFolder = Path.GetDirectoryName(path);
                total += bytes;
                ok++;
            }
            catch (Exception ex)
            {
                item.Status = $"Fehler: {ex.Message}";
                item.Failed = true;
            }

            Progress = (n + 1) * 100.0 / selected.Count;
        }

        var failed = selected.Count(i => i.Failed);
        Summary = _cancelRequested
            ? $"Abgebrochen — {ok} von {selected.Count} exportiert."
            : failed > 0
                ? $"{ok} exportiert, {failed} fehlgeschlagen."
                : $"Fertig — {ok} {(ok == 1 ? "Bild" : "Bilder")} exportiert ({FormatBytes(total)}).";

        if (lastFolder is not null)
            _main.ReportExport(lastFolder, Summary);

        IsExporting = false;
        IsDone = true;
    }

    /// <summary>Beendet nach dem Bild, das gerade läuft.</summary>
    [RelayCommand]
    private void CancelExport() => _cancelRequested = true;

    /// <summary>Fenster wird geschlossen: Laden der Vorschaubilder beenden.</summary>
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
