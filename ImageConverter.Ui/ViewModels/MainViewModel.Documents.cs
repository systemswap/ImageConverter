using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageConverter.Documents;

namespace ImageConverter.ViewModels;

/// <summary>
/// Files that are not images (3D models …): a registered <see cref="IDocumentViewer"/> shows them in place of the picture
/// and the settings column. While one is shown there is no current image, so every image function is off by itself –
/// export, print, tools and AI all depend on <see cref="HasImage"/>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The document shown instead of an image, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDocument), nameof(ShowsEmptyState), nameof(ShowsImageSettings), nameof(ShowsDocumentPanel))]
    public partial IOpenDocument? OpenDocument { get; set; }

    public bool ShowsDocument => OpenDocument is not null;

    /// <summary>The large drop zone: nothing open at all.</summary>
    public bool ShowsEmptyState => !HasImage && OpenDocument is null;

    /// <summary>Rubrics and settings column: for images, not in full screen.</summary>
    public bool ShowsImageSettings => !IsFullScreen && OpenDocument is null;

    /// <summary>The document's own column (instead of the settings), not in full screen.</summary>
    public bool ShowsDocumentPanel => !IsFullScreen && OpenDocument is not null;

    partial void OnIsFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsImageSettings));
        OnPropertyChanged(nameof(ShowsDocumentPanel));
    }

    /// <summary>Settings of the last image before a document was shown: the template when an image follows again.</summary>
    private (SettingsState Settings, double FileDpi)? _beforeDocument;

    private async Task ShowDocumentFileAsync(int index, IDocumentViewer viewer)
    {
        var path = _files[index];
        CommitPendingSettings();
        if (_history is not null)
        {
            var outgoing = CaptureSettings();
            if (!ApplyToAll && CurrentPath is { } leaving) _fileSettings[leaving] = outgoing;
            _beforeDocument = (outgoing, _history.Dpi);
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var document = await viewer.OpenAsync(path, cts.Token);
            if (cts.IsCancellationRequested)
            {
                document.Dispose();
                return;
            }

            var previous = _history;
            var previousPath = previous is not null ? CurrentPath : null;
            await StopPreviewAsync();
            ClearDisplay();
            _history = null;
            _fileIndex = index;
            var old = OpenDocument;
            OpenDocument = document;
            old?.Dispose();
            CurrentFileName = Path.GetFileName(path);
            FileDetails = document.Details;
            OnPropertyChanged(nameof(FilePosition));
            OnPropertyChanged(nameof(ShowsEmptyState));
            NotifyUndoRedo();

            if (previous is not null && previousPath is not null)
                await ReleaseIfUnusedAsync(previousPath, previous);
        }
        catch (OperationCanceledException)
        {
            // Another file has been chosen meanwhile.
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

    /// <summary>Closes the shown document (an image is shown next, or new files are opened).</summary>
    private void CloseDocument()
    {
        var old = OpenDocument;
        if (old is null) return;
        OpenDocument = null;
        old.Dispose();
        OnPropertyChanged(nameof(ShowsEmptyState));
    }
}
