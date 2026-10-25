using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Metadata;

namespace ImageConverter.ViewModels;

/// <summary>A summary section as shown, with the note whether saving will remove it.</summary>
/// <param name="Title">Section title.</param>
/// <param name="RemovedNote">Shown when the current EXIF setting removes this section on export; otherwise null.</param>
/// <param name="Fields">Labelled values.</param>
public sealed record MetadataGroupView(string Title, string? RemovedNote, IReadOnlyList<MetadataField> Fields);

/// <summary>
/// Group „Bildinformationen“ (tab Output): what the original file says about itself — camera, exposure, location,
/// author, colour profile — and the complete list of entries. Read in the background when the image changes; display
/// only, nothing is changed or sent.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Recently read files (path → result); small, the results hold only text.</summary>
    private readonly Dictionary<string, ImageMetadata> _metadataCache = new(StringComparer.Ordinal);
    private const int MetadataCacheSize = 64;

    private CancellationTokenSource? _metadataCts;
    private ImageMetadata _metadata = ImageMetadata.Empty;

    /// <summary>Summary sections of the current image.</summary>
    [ObservableProperty] public partial IReadOnlyList<MetadataGroupView> MetadataGroups { get; set; } = [];

    /// <summary>All entries of the current image (collapsed list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetadataTagsHeader), nameof(HasMetadataTags))]
    public partial IReadOnlyList<MetadataTag> MetadataTags { get; set; } = [];

    /// <summary>Note under the heading: still reading, nothing found, or why reading was incomplete.</summary>
    [ObservableProperty] public partial string? MetadataNote { get; set; }

    public bool HasMetadataTags => MetadataTags.Count > 0;

    public string MetadataTagsHeader => _metadata.Truncated
        ? Tr.T("Alle Einträge (die ersten {0})", MetadataTags.Count)
        : Tr.T("Alle Einträge ({0})", MetadataTags.Count);

    /// <summary>Asks the view to put text on the clipboard (the view model has no access to it).</summary>
    public event Func<string, Task>? CopyTextRequested;

    partial void OnCurrentFileNameChanged(string value) => _ = LoadMetadataAsync(CurrentPath);

    partial void OnSelectedMetadataModeChanged(string value) => ShowMetadata();

    private async Task LoadMetadataAsync(string? path)
    {
        _metadataCts?.Cancel();
        if (path is null)
        {
            _metadata = ImageMetadata.Empty;
            MetadataNote = null;
            ShowMetadata();
            return;
        }
        if (_metadataCache.TryGetValue(path, out var known))
        {
            _metadata = known;
            ShowMetadata();
            return;
        }

        var cts = _metadataCts = new CancellationTokenSource();
        MetadataNote = Tr.T("Wird gelesen …");
        var read = await Task.Run(() => ImageMetadataReader.Read(path));
        if (cts.IsCancellationRequested) return;   // another image was selected meanwhile

        if (_metadataCache.Count >= MetadataCacheSize) _metadataCache.Clear();
        _metadataCache[path] = read;
        _metadata = read;
        ShowMetadata();
    }

    /// <summary>Labels come from the reader in the current language — read again after switching languages.</summary>
    private void RefreshMetadataTexts()
    {
        _metadataCache.Clear();
        _ = LoadMetadataAsync(CurrentPath);
    }

    /// <summary>Puts the current result on display, marking what the EXIF setting removes when saving.</summary>
    private void ShowMetadata()
    {
        var mode = MetadataByLabel.GetValueOrDefault(SelectedMetadataMode, MetadataMode.KeepAll);
        MetadataGroups = _metadata.Groups
            .Select(g => new MetadataGroupView(g.Title, RemovedOnExport(g.Section, mode), g.Fields))
            .ToList();
        MetadataTags = _metadata.Tags;
        MetadataNote = _metadata.Problem is { } problem
            ? Tr.T("Nicht alles lesbar: {0}", problem)
            : _metadata.Groups.All(g => g.Section == MetadataSection.File) && CurrentPath is not null
                ? Tr.T("Keine Kamera-, Orts- oder Urheberangaben im Bild.")
                : null;
    }

    /// <summary>What the export does with a section under the given EXIF setting (the colour profile always stays).</summary>
    private static string? RemovedOnExport(MetadataSection section, MetadataMode mode) => (section, mode) switch
    {
        (MetadataSection.Location, MetadataMode.RemoveGps) => Tr.T("wird beim Speichern entfernt"),
        (MetadataSection.Camera or MetadataSection.Capture or MetadataSection.Location or MetadataSection.Description,
            MetadataMode.RemoveAll) => Tr.T("wird beim Speichern entfernt"),
        _ => null,
    };

    /// <summary>Summary and complete list as plain text on the clipboard.</summary>
    [RelayCommand]
    private Task CopyMetadata()
    {
        var text = new StringBuilder();
        if (CurrentFileName is { } name) text.AppendLine(name).AppendLine();
        foreach (var group in _metadata.Groups)
        {
            text.AppendLine(group.Title);
            foreach (var field in group.Fields) text.Append("  ").Append(field.Label).Append(": ").AppendLine(field.Value);
            text.AppendLine();
        }
        if (_metadata.Tags.Count > 0)
        {
            text.AppendLine(Tr.T("Alle Einträge"));
            foreach (var tag in _metadata.Tags) text.Append("  [").Append(tag.Directory).Append("] ").Append(tag.Name).Append(": ").AppendLine(tag.Value);
        }
        return CopyTextRequested?.Invoke(text.ToString()) ?? Task.CompletedTask;
    }
}
