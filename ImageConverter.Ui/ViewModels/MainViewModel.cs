using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Ai;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Localization;
using ImageConverter.Models;

namespace ImageConverter.ViewModels;

/// <summary>
/// View model of the main window. Split across several files:
/// <list type="bullet">
/// <item>MainViewModel.cs: opening and browsing files</item>
/// <item>MainViewModel.Export.cs: format, metadata, colour, output, export and size estimate</item>
/// <item>MainViewModel.Editing.cs: crop, rotation, flip, perspective, image steps and undo/redo</item>
/// <item>MainViewModel.History.cs: undo steps for settings</item>
/// <item>MainViewModel.Edits.cs: the "Edits" list, switching functions off, geometry views</item>
/// <item>MainViewModel.PerImage.cs: shared or per-image settings, data for the export window</item>
/// <item>MainViewModel.Size.cs: pixel dimensions, aspect ratio chain, DPI, print size</item>
/// <item>MainViewModel.Preview.cs: display and live preview of the settings chain</item>
/// <item>MainViewModel.Tools.cs: light, colour, dehaze, luminosity mask, grain</item>
/// <item>MainViewModel.WhiteBalance.cs: white balance with eyedroppers</item>
/// <item>MainViewModel.Look.cs: the look as a whole, for extensions (AI)</item>
/// <item>MainViewModel.Dewarp.cs: curved-page tool and straightening with AI</item>
/// <item>MainViewModel.Upscale.cs: AI upscaling and inspection view</item>
/// </list>
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        Formats = ["WebP", "PNG", "JPEG", "AVIF", "JPEG XL", "TIFF", "BMP", "ICO"];
        SelectedFormat = "PNG";   // default output format

        Filters = FilterOption.All;
        SelectedFilter = Filters[0];

        MetadataModes = ["Alles behalten", "Nur GPS entfernen", "Alles entfernen"];
        SelectedMetadataMode = "Alles behalten";

        ColorModes = ["Profil behalten", "Nach sRGB wandeln", "Profil verwerfen"];
        SelectedColorMode = "Nach sRGB wandeln";

        AspectPresets = ["Frei", "Original", "1:1", "4:3", "3:2", "16:9"];
        SelectedAspectPreset = "Frei";

        // Dimensions of the placeholder image while nothing is loaded
        ApplySourceSize(4000, 3000);

        // Initial state for newly opened images (see ResetToStart)
        _startSettings = CaptureSettings();

        // From here on, changes count as undo steps.
        ResetHistoryBaseline();

        // AI module (separable, see the Ai/ folder)
        Ai = new AiViewModel(this);
        Retouch = new RetouchViewModel(this, Ai);
        Background = new BackgroundViewModel(this, Ai);
        Ai.BusyElsewhere = () => Retouch.IsRunning || Background.IsRunning;
        Background.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BackgroundViewModel.IsTabActive)) OnPropertyChanged(nameof(ActiveBrush));
        };
        PropertyChanged += OnUpscaleRelevantChange;   // upscaling follows the image and the size

        // Language change: have computed texts (hints, print size …) queried again.
        Tr.Instance.PropertyChanged += (_, _) => RefreshTexts();
    }

    /// <summary>AI enhancement: a separate module with its own group box and settings tab.</summary>
    public AiViewModel Ai { get; }

    /// <summary>Removal (watermarks, text …) with a brush and an inpainting model; part of the AI module.</summary>
    public RetouchViewModel Retouch { get; }

    /// <summary>Background removal or blur ("Hintergrund" tab); part of the AI module.</summary>
    public BackgroundViewModel Background { get; }

    /// <summary>The tab whose brush paints in the preview: "Hintergrund" when it is selected, otherwise "Entfernen".</summary>
    public IBrushTool ActiveBrush => Background.IsTabActive ? Background : Retouch;

    /// <summary>"Language" tab of the settings.</summary>
    public LanguageViewModel Language { get; } = new();

    /// <summary>After a language change: rereads all bindings and rebuilds cached texts.</summary>
    private void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        if (_history is null)
        {
            CurrentFileName = Tr.T("Kein Bild geladen");
            FileDetails = Tr.T("Bild hierher ziehen oder „Bilder“ wählen");
        }
        UpdateFileDetails();
        RequestEstimate();
        RefreshMetadataTexts();
    }

    private CancellationTokenSource? _releaseCts;

    /// <summary>
    /// After an image has been released, returns memory to the operating system, but only once no further image has
    /// been selected for two seconds, and in the background, because the thorough clean-up pauses the program briefly
    /// (about 0.1 s). When browsing through 20 photos of 12 MP, memory stayed at 2–2.5 GB instead of 3.3 GB.
    /// </summary>
    private void ReleaseMemorySoon()
    {
        _releaseCts?.Cancel();
        var cts = _releaseCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                PixelPool.Trim(release: true);
            }
            catch (OperationCanceledException)
            {
                // another image was selected; the next switch cleans up
            }
        });
    }

    /// <summary>Subscribed by the view: opens the settings window on the given page, otherwise on the last page shown.</summary>
    public event Func<SettingsPage?, Task>? SettingsRequested;

    [RelayCommand]
    private Task OpenSettings(SettingsPage? page) => SettingsRequested?.Invoke(page) ?? Task.CompletedTask;

    public ObservableCollection<string> Formats { get; }
    public IReadOnlyList<FilterOption> Filters { get; }
    public ObservableCollection<string> MetadataModes { get; }
    public ObservableCollection<string> ColorModes { get; }
    public ObservableCollection<string> AspectPresets { get; }

    // ------------------------------------------------------------- Files

    private readonly List<string> _files = [];
    private int _fileIndex = -1;

    // Cancels a load still in progress when the next image has already been requested.
    private CancellationTokenSource? _loadCts;

    /// <summary>Short list of formats for the empty state; contains only formats that ImageMagick can actually read on this system.</summary>
    public string SupportedFormatsSummary
    {
        get
        {
            (string Label, string Ext)[] headline =
            [
                ("JPEG", ".jpg"), ("PNG", ".png"), ("WebP", ".webp"), ("AVIF", ".avif"),
                ("HEIC", ".heic"), ("JPEG XL", ".jxl"), ("TIFF", ".tif"), ("GIF", ".gif"), ("RAW", ".dng"),
            ];
            return string.Join(" · ", headline
                .Where(h => ImageFormats.ReadableExtensions.Contains(h.Ext))
                .Select(h => h.Label)) + " …";
        }
    }

    [ObservableProperty] public partial string CurrentFileName { get; set; } = Tr.T("Kein Bild geladen");
    [ObservableProperty] public partial string FileDetails { get; set; } = Tr.T("Bild hierher ziehen oder „Bilder“ wählen");
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }

    public int FileCount => _files.Count;
    public bool HasMultipleFiles => _files.Count > 1;
    public string FilePosition => _files.Count > 1 ? $"{_fileIndex + 1} / {_files.Count}" : "";

    /// <summary>All settings as they are at program start. Editing settings are not saved between sessions.</summary>
    private readonly SettingsState _startSettings;

    /// <summary>Raised when newly opened images replace the previous ones, so that extensions (AI) start afresh as well.</summary>
    internal event EventHandler? FilesReplaced;

    /// <summary>
    /// Newly opened images start as after program start: all settings (format, quality, size = original resolution,
    /// DPI from the file, light, haze, colour, sharpness …) are at their defaults. Settings are also kept per image
    /// ("Same settings for all images" off), so that a change to one image never silently overwrites the others;
    /// each image starts with the initial state when it is first shown.
    /// This is not an undo step; the history starts afresh with the new images.
    /// Browsing between images that are already open does not call this method.
    /// </summary>
    internal void ResetToStart()
    {
        using (SuppressHistory())
        {
            ApplyToAll = false;
            ApplySettings(_startSettings);
        }
        _individualTemplate = _startSettings;
        SelectionTool = true;
        ResetHistoryBaseline();
        FilesReplaced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes over files and folders (from drag &amp; drop, the file dialog or the
    /// command line) and shows the first image. Folders are searched non-recursively.
    /// </summary>
    /// <param name="paths">Paths of image files and folders.
    /// </summary>
    public async Task OpenAsync(IEnumerable<string> paths)
    {
        var found = new List<string>();
        var skipped = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                found.AddRange(Directory.EnumerateFiles(path)
                    .Where(ImageFormats.CanRead)
                    .Order(StringComparer.CurrentCultureIgnoreCase));
            }
            else if (File.Exists(path))
            {
                if (ImageFormats.CanRead(path)) found.Add(path);
                else skipped.Add(Path.GetFileName(path));
            }
        }

        if (found.Count == 0)
        {
            ErrorMessage = skipped.Count > 0
                ? Tr.T("„{0}“ ist kein unterstütztes Bildformat.", skipped[0])
                : Tr.T("Keine Bilder gefunden.");
            return;
        }

        // The new selection replaces the old one including its history. Wait for the preview computation
        // first, because it may still be reading an image that is about to be released.
        await StopPreviewAsync();
        ClearDisplay();
        DisposeAllHistories();
        ResetToStart();   // new images start as after program start
        _fileIndex = -1;

        _files.Clear();
        _files.AddRange(found);
        NotifyFileList();

        ErrorMessage = skipped.Count > 0
            ? Tr.T("{0} Datei(en) übersprungen — Format wird nicht unterstützt.", skipped.Count)
            : null;

        await ShowFileAsync(0);
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private Task PreviousFile() => ShowFileAsync((_fileIndex - 1 + _files.Count) % _files.Count);

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private Task NextFile() => ShowFileAsync((_fileIndex + 1) % _files.Count);

    private async Task ShowFileAsync(int index)
    {
        var path = _files[index];
        CommitPendingSettings();   // pending slider movements still belong to the previous image

        // State of the image being left. In per-image mode it is remembered;
        // in shared mode it is the template for the next image.
        var outgoing = _history is not null ? CaptureSettings() : null;
        var outgoingFileDpi = _history?.Dpi;
        if (!ApplyToAll && CurrentPath is { } leaving && outgoing is not null)
            _fileSettings[leaving] = outgoing;

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        IsLoading = true;
        try
        {
            var isNew = false;
            if (!_histories.TryGetValue(path, out var history))
            {
                // ImageMagick: all formats, EXIF orientation, colour profile → sRGB.
                // Runs in the background so that the UI stays responsive.
                var loaded = await ImageLoader.LoadAsync(path, cts.Token);
                history = new EditHistory(loaded.Image)
                {
                    FileBytes = loaded.FileBytes,
                    FormatName = loaded.FormatName,
                    Dpi = loaded.Dpi,
                };
                _histories[path] = history;
                isNew = true;
            }

            var previous = _history;
            var previousPath = _fileIndex >= 0 ? _files[_fileIndex] : null;

            _fileIndex = index;
            _history = history;
            CurrentFileName = Path.GetFileName(path);
            OnPropertyChanged(nameof(FilePosition));

            await ShowCurrentAsync();

            // Settings for this image: its own state (per-image mode) or the
            // previous state converted to fit this image.
            if (!ApplyToAll && _fileSettings.TryGetValue(path, out var own))
            {
                ApplySettings(own);
            }
            else if (outgoing is not null)
            {
                // DPI follows the file unless it was changed manually on the previous image;
                // in that case the manual setting applies to all.
                // Per-image mode without an own state: use the template from switching modes, not the previous image.
                var template = !ApplyToAll && _individualTemplate is { } t ? t : outgoing;
                if (isNew) template = WithFileDpi(template, history.Dpi, outgoingFileDpi);   // as in SettingsFor
                ApplySettings(AdaptToImage(template, SourceWidth, SourceHeight));
            }
            else if (isNew)
            {
                // Very first image: DPI from the file
                using (SuppressHistory())
                    SetDpiSilently((int)Math.Round(history.Dpi));
                ResetHistoryBaseline();
            }

            if (previous is not null && previous != history && previousPath is not null)
                await ReleaseIfUnusedAsync(previousPath, previous);
        }
        catch (OperationCanceledException)
        {
            // Another image has been selected in the meantime; not an error.
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("„{0}“ ließ sich nicht öffnen: {1}", Path.GetFileName(path), ex.Message);
        }
        finally
        {
            if (_loadCts == cts) IsLoading = false;
        }
    }

    private void NotifyFileList()
    {
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(HasMultipleFiles));
        OnPropertyChanged(nameof(FilePosition));
        PreviousFileCommand.NotifyCanExecuteChanged();
        NextFileCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
