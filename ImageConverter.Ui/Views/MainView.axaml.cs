using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _vm;
    private bool _hadSelection;

    public MainView()
    {
        InitializeComponent();

        // Das ganze Fenster nimmt Dateien an, nicht nur die Bildfläche.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
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
        }

        _vm = DataContext as MainViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.MultiExportRequested += OnMultiExportRequested;
            _vm.PrintRequested += OnPrintRequested;
            _vm.SettingsRequested += OnSettingsRequested;
        }
    }

    private SettingsWindow? _settings;

    /// <summary>Einstellungen: nicht modal — Downloads laufen weiter, während man arbeitet.</summary>
    private Task OnSettingsRequested()
    {
        if (_settings is not null) { _settings.Activate(); return Task.CompletedTask; }
        if (TopLevel.GetTopLevel(this) is not Window owner || _vm is null) return Task.CompletedTask;

        _settings = new SettingsWindow(_vm);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show(owner);
        return Task.CompletedTask;
    }

    private async Task OnPrintRequested(PrintDialogViewModel dialogVm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new PrintWindow(dialogVm).ShowDialog(owner);
    }

    /// <summary>Export mehrerer Bilder: eigenes Fenster mit Liste, modal über dem Hauptfenster.</summary>
    private async Task OnMultiExportRequested(ExportDialogViewModel dialogVm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new ExportWindow(dialogVm).ShowDialog(owner);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Sobald ein Rahmen aufgezogen wird, die Zuschnitt-Gruppe links ins
        // Bild holen — sonst liegt sie bei kleineren Fenstern unter dem Rand.
        // Nur beim Übergang "keine Auswahl -> Auswahl", nicht bei jedem Mausschritt.
        if (e.PropertyName != nameof(MainViewModel.HasCropSelection) || _vm is null) return;

        var has = _vm.HasCropSelection;
        if (has && !_hadSelection)
            Dispatcher.UIThread.Post(() => CropGroup.BringIntoView());
        _hadSelection = has;
    }

    // ------------------------------------------------------- Drag & Drop

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
            Title = "Zielordner für den Export",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _vm.OutputFolder = path;
    }

    private void OnOpenExportFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.LastExportFolder is not { } folder || !System.IO.Directory.Exists(folder)) return;

        // Dateimanager des Systems: Explorer, Nautilus, Dolphin … über die Standard-Zuordnung
        if (!ImageConverter.Services.FolderOpener.TryOpen(folder))
            _vm.ErrorMessage = $"Kein Dateimanager gefunden. Der Ordner ist: {folder}";
    }

    // ------------------------------------------------------- Dateidialog

    private async void OnOpenFilesClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Bilder öffnen",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Bilder")
                {
                    // Linux-Dialoge unterscheiden Groß-/Kleinschreibung (Kameras schreiben .JPG)
                    Patterns = ImageFormats.ReadableExtensions
                        .SelectMany(x => new[] { "*" + x, "*" + x.ToUpperInvariant() })
                        .ToList(),
                    MimeTypes = ["image/*"],
                },
                FilePickerFileTypes.All,
            ],
        });

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await _vm.OpenAsync(paths);
    }
}
