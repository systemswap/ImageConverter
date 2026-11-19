using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageConverter.Core.Imaging;
using ImageConverter.Documents;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _vm;
    private bool _hadSelection;

    public MainView()
    {
        InitializeComponent();

        // The whole window accepts dropped files, not only the image area.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // While a mouse button is pressed (dragging a slider …) the preview is computed coarser; full quality after release.
        AddHandler(PointerPressedEvent, (_, _) => _vm?.SetPointerDown(true), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _vm?.SetPointerDown(false), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.MultiExportRequested -= OnMultiExportRequested;
            _vm.PrintRequested -= OnPrintRequested;
            _vm.SettingsRequested -= OnSettingsRequested;
            _vm.CopyTextRequested -= OnCopyTextRequested;
            _vm.ChoiceRequested -= OnChoiceRequested;
            _vm.VisibleImageArea = null;
        }

        _vm = DataContext as MainViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.MultiExportRequested += OnMultiExportRequested;
            _vm.PrintRequested += OnPrintRequested;
            _vm.SettingsRequested += OnSettingsRequested;
            _vm.CopyTextRequested += OnCopyTextRequested;
            _vm.ChoiceRequested += OnChoiceRequested;
            _vm.VisibleImageArea = () => Crop.VisibleImageRect();
        }
    }

    /// <summary>Text from the view model onto the system clipboard.</summary>
    private async Task OnCopyTextRequested(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(clipboard, Avalonia.Input.DataFormat.Text, text);
    }

    private SettingsWindow? _settings;

    /// <summary>Shows the settings window non-modally so that downloads continue while working in the main window.</summary>
    private Task OnSettingsRequested(SettingsPage? page)
    {
        if (_settings is not null)
        {
            if (page is { } p) _settings.ShowPage(p);
            _settings.Activate();
            return Task.CompletedTask;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner || _vm is null) return Task.CompletedTask;

        _settings = new SettingsWindow(_vm);
        _settings.ShowPage(page ?? SettingsPage.General);   // language first (wish of the user)
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show(owner);
        return Task.CompletedTask;
    }

    /// <summary>Shows a modal prompt with several answers and returns the chosen index, −1 if none was chosen.</summary>
    private async Task<int> OnChoiceRequested(ChoiceRequest request)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return -1;
        var window = new ChoiceWindow(request);
        await window.ShowDialog(owner);
        return window.Result;
    }

    private async Task OnPrintRequested(PrintDialogViewModel dialogVm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new PrintWindow(dialogVm).ShowDialog(owner);
    }

    /// <summary>Export of several images: a separate window with a list, modal over the main window.</summary>
    private async Task OnMultiExportRequested(ExportDialogViewModel dialogVm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new ExportWindow(dialogVm).ShowDialog(owner);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // As soon as a crop frame is drawn, scroll the crop group on the left into view; otherwise
        // it lies below the edge in smaller windows. Only on the transition "no selection ->
        // selection", not on every pointer move.
        if (e.PropertyName != nameof(MainViewModel.HasCropSelection) || _vm is null) return;

        var has = _vm.HasCropSelection;
        if (has && !_hadSelection)
        {
            TabStandard.IsChecked = true;   // cropping is on the Standard tab
            Dispatcher.UIThread.Post(() => CropGroup.BringIntoView());
        }
        _hadSelection = has;
    }

    // ------------------------------------------------------- Drag and drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = hasFiles;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (_vm is null) return;

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .OfType<string>()
            .ToList();

        if (paths is { Count: > 0 })
            await _vm.OpenAsync(paths);
    }

    private void OnFitToViewClick(object? sender, RoutedEventArgs e) => Crop.FitToView();

    // ------------------------------------------------------- Export

    private async void OnChooseOutputFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Tr.T("Zielordner für den Export"),
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _vm.OutputFolder = path;
    }

    private void OnOpenExportFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.LastExportFolder is not { } folder || !System.IO.Directory.Exists(folder)) return;

        // System file manager (Explorer, Nautilus, Dolphin …) via the default association.
        if (!ImageConverter.Services.FolderOpener.TryOpen(folder))
            _vm.ErrorMessage = Tr.T("Kein Dateimanager gefunden. Der Ordner ist: {0}", folder);
    }

    // ------------------------------------------------------- File dialog

    private async void OnOpenFilesClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        // Linux dialogs are case-sensitive (cameras write .JPG).
        static List<string> Patterns(IEnumerable<string> extensions) =>
            extensions.SelectMany(x => new[] { "*" + x, "*" + x.ToUpperInvariant() }).ToList();
        var documents = DocumentViewers.All.SelectMany(v => v.Extensions).ToList();
        var types = new List<FilePickerFileType>();
        if (documents.Count > 0)
            types.Add(new FilePickerFileType(Tr.T("Bilder und Modelle")) { Patterns = Patterns(ImageFormats.ReadableExtensions.Concat(documents)) });
        types.Add(new FilePickerFileType(Tr.T("Bilder")) { Patterns = Patterns(ImageFormats.ReadableExtensions), MimeTypes = ["image/*"] });
        foreach (var viewer in DocumentViewers.All)
            types.Add(new FilePickerFileType(viewer.FileTypeName) { Patterns = Patterns(viewer.Extensions) });
        types.Add(FilePickerFileTypes.All);

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Bilder öffnen"),
            AllowMultiple = true,
            FileTypeFilter = types,
        });

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await _vm.OpenAsync(paths);
    }
}
