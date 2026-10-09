using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Ai;
using ImageConverter.Imaging;
using ImageConverter.ViewModels;
using ImageMagick;

namespace ImageConverter.Ai;

/// <summary>
/// KI-Verbesserung als eigenständiges Modul: GroupBox im Hauptfenster (<see cref="AiPanel"/>)
/// und Tab „KI-Modelle“ in den Einstellungen (<see cref="AiModelsView"/>).
/// Entfernen: Ordner Ai/ (Ui und Core), die zwei Einbindungen in MainView/SettingsWindow,
/// die Eigenschaft <c>MainViewModel.Ai</c> und das Paket Microsoft.ML.OnnxRuntime.
/// </summary>
public partial class AiViewModel : ViewModelBase
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly MainViewModel _main;
    private readonly OnnxSessions _sessions = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _runCts;

    public AiViewModel(MainViewModel main)
    {
        _main = main;
        Store = AiModelStore.CreateDefault();
        Models = AiModelCatalog.All.Select(m => new AiModelItem(this, m)).ToList();

        SizeOptions = ["Größe behalten", "Doppelte Größe (2×)", "Vierfache Größe (4×)"];
        SelectedSize = SizeOptions[0];

        Rescan();
        WatchFolders();
        _main.PropertyChanged += OnMainPropertyChanged;
    }

    internal AiModelStore Store { get; }

    /// <summary>Alle Modelle für die Verwaltung (Einstellungen).</summary>
    public IReadOnlyList<AiModelItem> Models { get; }

    /// <summary>Vorhandene, wählbare Modelle (ohne Hilfsmodelle).</summary>
    public ObservableCollection<AiModel> Available { get; } = [];

    /// <summary>Wo die Modelle liegen (bzw. hinkommen).</summary>
    public string FolderText => Store.Folders.FirstOrDefault(Directory.Exists) ?? Store.Folders[0];

    /// <summary>Modellordner im Dateimanager öffnen — z. B. um Modelle von Hand hineinzulegen.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Store.DownloadFolder;   // legt ihn bei Bedarf an
        Directory.CreateDirectory(folder);
        WatchFolders();
        if (!ImageConverter.Services.FolderOpener.TryOpen(folder))
            Status = Tr.T("Kein Dateimanager gefunden. Der Ordner ist: {0}", folder);
        OnPropertyChanged(nameof(FolderText));
    }

    // ------------------------------------------------------------ Auswahl

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnhanceModel), nameof(ModelHint), nameof(IsFaceModel), nameof(HasFidelity))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial AiModel? SelectedModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthLabel))]
    public partial double Strength { get; set; } = 100;

    public string StrengthLabel => $"{Strength:0} %";

    public IReadOnlyList<string> SizeOptions { get; }
    [ObservableProperty] public partial string SelectedSize { get; set; }

    public bool IsEnhanceModel => SelectedModel?.Kind == AiModelKind.Enhance;
    public bool HasModels => Available.Count > 0;

    public string? ModelHint => SelectedModel is { } m
        ? Tr.T(m.Description) + (m.NonCommercial ? "\n\n" + Tr.T("Lizenz: nur für nicht-kommerzielle Nutzung.") : "")
        : null;

    // ------------------------------------------------------------ Lauf

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(CancelCommand), nameof(CommitCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>Fortschritt 0…100.</summary>
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }

    /// <summary>
    /// Ergebnis des Modells, noch nicht übernommen: Stärke und Hautstruktur mischen es live neu
    /// (<see cref="IAiRun"/>), erst „Übernehmen“ macht daraus einen Bearbeitungsschritt.
    /// </summary>
    private IAiRun? _run;

    /// <summary>Bildstand, auf dem <see cref="_run"/> gerechnet wurde.</summary>
    private MagickImage? _runBase;

    /// <summary>Gefundene Gesichter des offenen Ergebnisses — für ein neues Rechnen mit anderer Natürlichkeit.</summary>
    private IReadOnlyList<DetectedFace>? _runFaces;

    /// <summary>Letzter übernommener KI-Schritt: Ergebnis und Bild davor — ein neuer Lauf beginnt wieder vor der KI.</summary>
    private (MagickImage Result, MagickImage Base)? _lastAi;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand))]
    public partial bool HasPending { get; set; }

    /// <summary>Hautstruktur 0…100 %: feine Struktur aus dem Bild davor statt der glatten Modellhaut (nur Gesichter).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextureLabel))]
    public partial double Texture { get; set; } = 30;

    public string TextureLabel => $"{Texture:0} %";

    /// <summary>CodeFormer: 0 = stärkste Verbesserung, 100 = möglichst nah am Original. Geht ins Modell — rechnet neu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FidelityLabel))]
    public partial double Fidelity { get; set; } = 60;

    public string FidelityLabel => $"{Fidelity:0} %";

    public bool IsFaceModel => SelectedModel?.Kind == AiModelKind.Face;
    public bool HasFidelity => SelectedModel?.Id == AiModelCatalog.CodeFormer.Id;

    partial void OnSelectedModelChanged(AiModel? value)
    {
        // ein offenes Ergebnis gehört zum Modell, mit dem es gerechnet wurde
        if (HasPending) Discard();
    }

    partial void OnStrengthChanged(double value) => RequestPreview();
    partial void OnTextureChanged(double value) => RequestPreview();

    private CancellationTokenSource? _fidelityCts;

    partial void OnFidelityChanged(double value)
    {
        // Natürlichkeit geht ins Modell: nach kurzer Ruhe am Regler neu rechnen, wenn schon ein Ergebnis da ist
        if (!HasPending || !HasFidelity) return;
        _fidelityCts?.Cancel();
        var cts = _fidelityCts = new CancellationTokenSource();
        _ = RerunAfterAsync(cts.Token);
    }

    private async Task RerunAfterAsync(CancellationToken ct)
    {
        try { await Task.Delay(600, ct); }
        catch (OperationCanceledException) { return; }

        if (IsRunning) _runCts?.Cancel();   // der laufende Lauf hat den alten Wert
        while (IsRunning && !ct.IsCancellationRequested) await Task.Delay(50, CancellationToken.None);
        if (!ct.IsCancellationRequested && HasPending) await ComputeAsync(rerun: true);
    }

    private bool CanApply => !IsRunning && SelectedModel is not null && _main.HasImage && !_main.IsEditing;

    /// <summary>Rechnet das Modell und zeigt das Ergebnis — ins Bild kommt es erst mit „Übernehmen“.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task Apply() => ComputeAsync(rerun: false);

    private async Task ComputeAsync(bool rerun)
    {
        if (SelectedModel is not { } model || _main.CurrentImage is not { } current) return;
        if (Store.PathOf(model) is not { } modelPath)
        {
            Status = Tr.T("Modell fehlt — bitte in den Einstellungen herunterladen.");
            return;
        }

        // Immer vom Stand vor der KI: Ist das Bild das Ergebnis des letzten KI-Schritts, rechnet der Lauf
        // auf dem Bild davor — KI auf KI würde Gesichter immer glatter machen.
        var image = rerun && _runBase is not null ? _runBase
            : _lastAi is { } last && ReferenceEquals(current, last.Result) ? last.Base : current;

        var factor = SelectedSize == SizeOptions[2] ? 4 : SelectedSize == SizeOptions[1] ? 2 : 1;
        if (model.Kind == AiModelKind.Enhance
            && !AiEnhancer.PossibleFactors(model.Scale, (int)image.Width, (int)image.Height).Contains(factor))
        {
            var mp = image.Width * factor * image.Height * factor / 1e6;
            Status = Tr.T("Zu groß: das Ergebnis hätte {0:0} Megapixel (höchstens {1}).", mp, AiEnhancer.MaxOutputPixels / 1_000_000);
            return;
        }

        var path = _main.CurrentImagePath;
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        Progress = 0;
        Status = Tr.T("Modell wird geladen …");
        var clock = Stopwatch.StartNew();

        var progress = new Progress<double>(f =>
        {
            Progress = f * 100;
            Status = f <= 0 ? Status : $"{f * 100:0} %{Remaining(clock.Elapsed, f)}";
            _main.ActivityText = Tr.T("KI · {0}", Status);
        });

        void Preview(AiPreviewFrame frame) => Dispatcher.UIThread.Post(() =>
        {
            // nur zeigen, solange noch dasselbe Bild angezeigt wird
            if (IsRunning && _main.CurrentImagePath == path)
                _main.OverrideBitmap = BitmapOps.FromBgra(frame.Bgra, frame.Width, frame.Height);
        });

        try
        {
            IAiRun run;
            IReadOnlyList<DetectedFace>? faces = null;
            if (model.Kind == AiModelKind.Face)
            {
                if (Store.PathOf(AiModelCatalog.FaceDetector) is not { } detectorPath)
                {
                    Status = Tr.T("Die Gesichtserkennung fehlt — bitte in den Einstellungen herunterladen.");
                    return;
                }

                faces = rerun ? _runFaces : null;
                if (faces is null)
                {
                    _main.ActivityText = Tr.T("KI · Gesichter werden gesucht …");
                    faces = await Task.Run(() =>
                        AiFaces.Detect(image, _sessions.Get(AiModelCatalog.FaceDetector, detectorPath)), cts.Token);
                }

                if (faces.Count == 0)
                {
                    Status = Tr.T("Kein Gesicht gefunden.");
                    return;
                }

                Status = faces.Count == 1 ? Tr.T("1 Gesicht gefunden …") : Tr.T("{0} Gesichter gefunden …", faces.Count);
                var found = faces;
                var fidelity = Fidelity / 100;
                run = await Task.Run(() =>
                {
                    var face = new OnnxFaceModel(_sessions.Get(model, modelPath), model) { Fidelity = fidelity };
                    return (IAiRun)AiFaces.Run(image, found, face, progress, cts.Token, Preview);
                }, cts.Token);
            }
            else
            {
                run = await Task.Run(() =>
                {
                    var tiles = new OnnxTileModel(_sessions.Get(model, modelPath), model);
                    return (IAiRun)AiEnhancer.Run(image, tiles, factor, progress, cts.Token, Preview);
                }, cts.Token);
            }

            if (_main.CurrentImagePath != path) return;   // inzwischen ein anderes Bild gewählt

            (_run, _runBase, _runFaces) = (run, image, faces);
            HasPending = true;
            Status = Tr.T("Fertig nach {0} — die Regler wirken jetzt live, „Übernehmen“ schreibt es ins Bild.",
                FormatDuration(clock.Elapsed));
        }
        catch (OperationCanceledException)
        {
            Status = Tr.T("Abgebrochen.");
        }
        catch (Exception ex)
        {
            Status = Tr.T("Fehler: {0}", ex.Message);
        }
        finally
        {
            _runCts = null;
            IsRunning = false;
            _main.ActivityText = null;
            if (HasPending) RequestPreview();   // fertiges Ergebnis (oder nach Abbruch das alte) mit den Reglern zeigen
            else _main.OverrideBitmap = null;
        }
    }

    // Vorschau beim Ziehen: die letzte Anforderung gewinnt, gerechnet wird nie zweimal gleichzeitig
    private bool _previewRunning, _previewDirty;
    private IAiRun? _beforeOf;

    private void RequestPreview()
    {
        if (!HasPending || IsRunning) return;
        if (_previewRunning)
        {
            _previewDirty = true;
            return;
        }

        _ = ShowPreviewAsync();
    }

    private async Task ShowPreviewAsync()
    {
        _previewRunning = true;
        try
        {
            do
            {
                _previewDirty = false;
                if (_run is not { } run) return;
                var (strength, texture) = (Strength / 100, Texture / 100);
                var frame = await Task.Run(() => run.Preview(strength, texture));
                if (ReferenceEquals(run, _run) && !IsRunning)
                    _main.OverrideBitmap = BitmapOps.FromBgra(frame.Bgra, frame.Width, frame.Height);

                // Vorher-Seite für „Vorher / Nachher“: dasselbe Ergebnis mit Stärke 0 = Bild vor der KI
                if (!ReferenceEquals(_beforeOf, run))
                {
                    var before = await Task.Run(() => run.Preview(0, 0));
                    if (ReferenceEquals(run, _run))
                    {
                        _beforeOf = run;
                        _main.OverrideBeforeBitmap = BitmapOps.FromBgra(before.Bgra, before.Width, before.Height);
                    }
                }
            }
            while (_previewDirty);
        }
        catch (Exception ex)
        {
            Status = Tr.T("Fehler: {0}", ex.Message);
        }
        finally
        {
            _previewRunning = false;
        }
    }

    private bool CanCommit => HasPending && !IsRunning && !_main.IsEditing;

    /// <summary>Ergebnis mit den aktuellen Reglern in voller Größe ins Bild — ein Schritt, rückgängig mit Strg+Z.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task Commit()
    {
        if (_run is not { } run || _runBase is not { } runBase) return;
        var (strength, texture) = (Strength / 100, Texture / 100);
        _fidelityCts?.Cancel();

        // Inzwischen anders bearbeitet (Zuschnitt, Rückgängig …)? Dann passt das Ergebnis nicht mehr.
        var current = _main.CurrentImage;
        if (!ReferenceEquals(current, runBase)
            && !(_lastAi is { } last && ReferenceEquals(current, last.Result) && ReferenceEquals(last.Base, runBase)))
        {
            ClearRun();
            Status = Tr.T("Das Bild wurde inzwischen geändert — bitte neu berechnen.");
            return;
        }

        Status = Tr.T("Wird übernommen …");

        // Gerechnet wurde auf dem Stand vor der KI — das Ergebnis ersetzt das aktuelle Bild, statt darauf aufzubauen
        var ok = await _main.ApplyImageEditAsync(_ => run.Compose(strength, texture));
        if (ok && _main.CurrentImage is { } result) _lastAi = (result, runBase);
        ClearRun();
        Status = ok ? Tr.T("Übernommen — rückgängig mit Strg+Z.") : Tr.T("Fehlgeschlagen — siehe Meldung oben.");
    }

    /// <summary>Ergebnis verwerfen: das Bild bleibt, wie es war.</summary>
    [RelayCommand(CanExecute = nameof(HasPending))]
    private void Discard()
    {
        ClearRun();
        Status = null;
    }

    private void ClearRun()
    {
        _fidelityCts?.Cancel();
        (_run, _runBase, _runFaces, _beforeOf) = (null, null, null, null);
        HasPending = false;
        _main.OverrideBitmap = null;
        _main.OverrideBeforeBitmap = null;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>Öffnet die Einstellungen (Tab „KI-Modelle“).</summary>
    [RelayCommand]
    private Task OpenModels() => _main.OpenSettingsCommand.ExecuteAsync(null);

    private static string Remaining(TimeSpan elapsed, double fraction)
    {
        if (fraction < 0.03 || elapsed.TotalSeconds < 2) return "";
        var left = TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction * (1 - fraction));
        return Tr.T("  ·  noch ca. {0}", FormatDuration(left));
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, Math.Round(t.TotalSeconds)):0} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasImage) or nameof(MainViewModel.IsEditing))
        {
            ApplyCommand.NotifyCanExecuteChanged();
            CommitCommand.NotifyCanExecuteChanged();
        }

        // anderes Bild gewählt: Zwischenstand und offenes Ergebnis gehören nicht dazu
        if (e.PropertyName == nameof(MainViewModel.CurrentFileName))
        {
            if (HasPending) Discard();
            else if (_main.OverrideBitmap is not null) _main.OverrideBitmap = null;
        }
    }

    // ------------------------------------------------------------ Modelle verwalten

    /// <summary>Welche Modelle liegen da? Auch von Hand in den Ordner kopierte werden erkannt.</summary>
    public void Rescan()
    {
        foreach (var item in Models) item.Refresh();

        var detector = Store.IsInstalled(AiModelCatalog.FaceDetector);
        var usable = AiModelCatalog.All
            .Where(m => m.Kind != AiModelKind.Helper && Store.IsInstalled(m)
                        && (m.Kind != AiModelKind.Face || detector))
            .ToList();

        var selected = SelectedModel;
        Available.Clear();
        foreach (var m in usable) Available.Add(m);
        SelectedModel = usable.Contains(selected!) ? selected : usable.FirstOrDefault();
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(FolderText));
    }

    internal async Task DownloadAsync(AiModelItem item)
    {
        // Gesichtsmodelle brauchen die Gesichtserkennung — gleich mitladen
        foreach (var id in item.Model.Requires)
            if (Models.FirstOrDefault(m => m.Model.Id == id) is { IsInstalled: false, IsDownloading: false } required)
                _ = required.DownloadCommand.ExecuteAsync(null);

        await Store.DownloadAsync(item.Model, Http, item.ProgressReporter, item.Token);
        Rescan();
        WatchFolders();   // Ordner gibt es evtl. erst jetzt
    }

    internal void Delete(AiModelItem item)
    {
        _sessions.Dispose();   // geöffnete Modelle freigeben, sonst ist die Datei gesperrt
        Store.Delete(item.Model);
        Rescan();
    }

    internal bool IsBusy => IsRunning;

    private void WatchFolders()
    {
        foreach (var folder in Store.Folders.Where(f => Directory.Exists(f) && _watchers.All(w => w.Path != f)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                watcher.Created += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                _watchers.Add(watcher);
            }
            catch
            {
                // ohne Überwachung: erkannt wird dann beim nächsten Öffnen der Einstellungen
            }
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(Rescan, DispatcherPriority.Background);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ImageConverter");
        return http;
    }
}

/// <summary>Ein Modell in der Verwaltung: vorhanden?, Download mit Fortschritt, abbrechen, löschen.</summary>
public partial class AiModelItem : ObservableObject
{
    private readonly AiViewModel _owner;
    private CancellationTokenSource? _cts;

    public AiModelItem(AiViewModel owner, AiModel model)
    {
        _owner = owner;
        Model = model;
        ProgressReporter = new Progress<double>(f => Progress = f * 100);
    }

    public AiModel Model { get; }
    internal IProgress<double> ProgressReporter { get; }
    internal CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    public string Info =>
        Tr.T("{0}  ·  Lizenz {1}", Model.SizeText, Model.License) + (Model.NonCommercial ? " " + Tr.T("(nur nicht-kommerziell)") : "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ShowDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ShowDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand), nameof(CancelCommand))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }

    public bool ShowDownload => !IsInstalled && !IsDownloading;

    public string StateText => IsDownloading ? Tr.T("wird geladen …") : IsInstalled ? Tr.T("✓ vorhanden") : Tr.T("nicht geladen");

    internal void Refresh() => IsInstalled = _owner.Store.IsInstalled(Model);

    private bool CanDownload => !IsInstalled && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Progress = 0;
        Problem = null;
        try
        {
            await _owner.DownloadAsync(this);
        }
        catch (OperationCanceledException)
        {
            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = ex is HttpRequestException ? Tr.T("Download fehlgeschlagen: {0}", ex.Message) : ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _cts.Dispose();
            _cts = null;
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void Cancel() => _cts?.Cancel();

    private bool CanDelete => IsInstalled && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (_owner.IsBusy)
        {
            Problem = Tr.T("Gerade läuft eine KI-Berechnung — danach löschen.");
            return;
        }

        try { _owner.Delete(this); }
        catch (Exception ex) { Problem = Tr.T("Löschen fehlgeschlagen: {0}", ex.Message); }
    }
}
