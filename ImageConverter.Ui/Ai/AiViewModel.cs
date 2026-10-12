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
using ImageConverter.Core.Imaging;
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
        ModelGroups =
        [
            Group("BILDER VERBESSERN", "Rauschen, JPEG-Artefakte, Unschärfe, Vergrößern – in der Rubrik „KI“.", AiModelKind.Enhance),
            Group("GESICHTER", "Unscharfe oder beschädigte Gesichter wiederherstellen – in der Rubrik „KI“.", AiModelKind.Face),
            Group("GESICHTSERKENNUNG", "Hilfsmodelle für die Gesichtsmodelle: finden Gesichter und ihre Bereiche. Werden mit jedem Gesichtsmodell automatisch geladen.", AiModelKind.Helper),
            Group("ENTFERNEN", "Wasserzeichen, Schrift oder Störendes übermalen und füllen lassen – in der Rubrik „Entfernen“.", AiModelKind.Inpaint),
        ];

        SizeOptions = ["Größe behalten", "Doppelte Größe (2×)", "Vierfache Größe (4×)"];
        SelectedSize = SizeOptions[0];

        Rescan();
        WatchFolders();
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.FilesReplaced += (_, _) => ResetToStart();
    }

    internal AiModelStore Store { get; }

    /// <summary>Geöffnete Modelle — mit dem Entfernen geteilt, damit nur ein großes Modell im Speicher liegt.</summary>
    internal OnnxSessions Sessions => _sessions;

    /// <summary>Nach jedem Durchsehen des Modellordners (Download, Löschen, von Hand kopiert).</summary>
    internal event EventHandler? ModelsChanged;

    /// <summary>Fortschritt in der einen Anzeige unten mitte — auch für das Entfernen.</summary>
    internal void ShowActivity(string text, double progress, System.Windows.Input.ICommand? cancel)
    {
        _main.ActivityText = text;
        _main.ActivityProgress = progress;
        _main.ActivityCancel = cancel;
    }

    internal void HideActivity() => EndActivity();

    /// <summary>Alle Modelle für die Verwaltung (Einstellungen).</summary>
    public IReadOnlyList<AiModelItem> Models { get; }

    /// <summary>Die Modelle in Gruppen für die Verwaltung — je Gruppe ein Kasten.</summary>
    public IReadOnlyList<AiModelGroup> ModelGroups { get; }

    private AiModelGroup Group(string title, string hint, AiModelKind kind) =>
        new(title, hint, Models.Where(m => m.Model.Kind == kind).ToList());

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
    [NotifyPropertyChangedFor(nameof(IsEnhanceModel), nameof(ModelHint), nameof(IsFaceModel), nameof(HasFidelity), nameof(CanChooseSize), nameof(CanShowMap))]
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
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(CancelCommand), nameof(CommitCommand), nameof(DiscardCommand))]
    [NotifyPropertyChangedFor(nameof(CanChooseSize))]
    public partial bool IsRunning { get; set; }

    /// <summary>Fortschritt 0…100.</summary>
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }

    /// <summary>
    /// Reihenfolge von KI und Einstellungen (Dunst, Licht, Farbe, Entrauschen, Schärfe). Nie doppelt:
    /// Entweder die KI rechnet auf dem Bild ohne sie und die Einstellungen wirken danach wie gewohnt auch auf
    /// die Gesichter — oder sie werden vorher ins Bild gerechnet und stehen danach auf null.
    /// </summary>
    public IReadOnlyList<string> OrderOptions { get; } = ["Erst KI, dann Einstellungen", "Erst Einstellungen, dann KI"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrderHint))]
    public partial string SelectedOrder { get; set; } = "Erst KI, dann Einstellungen";

    private bool LookFirst => SelectedOrder == OrderOptions[1];

    public string OrderHint => LookFirst
        ? Tr.T("Die KI bekommt das fertig eingestellte Bild, Gesichter kommen zuletzt. Danach stehen die Regler auf null.")
        : Tr.T("Die Einstellungen wirken danach auch auf die Gesichter und bleiben verstellbar.");

    partial void OnSelectedOrderChanged(string value)
    {
        // das offene Ergebnis gehört zur Reihenfolge, mit der es gerechnet wurde
        if (HasPending) Discard();
    }

    /// <summary>Wie ein Lauf aufgesetzt ist: woher er kommt, was das Modell bekommt und welcher Look danach gilt.</summary>
    /// <param name="Base">Bildstand, den das Ergebnis ersetzt (beim Übernehmen muss er noch gelten).</param>
    /// <param name="Input">Was das Modell bekommt: <paramref name="Base"/> oder Base mit eingerechnetem Look.</param>
    /// <param name="Baked">Look, der in <paramref name="Input"/> steckt; null = keiner.</param>
    /// <param name="LookAfter">Reglerstand nach dem Übernehmen; null = Regler bleiben.</param>
    /// <param name="LookOnTop">Die Einstellungen wirken danach auf das Ergebnis — die Vorschau zeigt sie mit.</param>
    /// <param name="Current">Das aktuelle Bild beim Rechnen — „Übernehmen“ nur, solange es noch das aktuelle ist.</param>
    /// <param name="Turns">Vierteldrehungen, mit denen <paramref name="Input"/> gedreht ist (Gesichter aufrecht, wie angezeigt).</param>
    /// <param name="Flip">… und ob gespiegelt. Das Ergebnis wird damit vor dem Übernehmen zurückgedreht.</param>
    private sealed record RunSetup(MagickImage Base, MagickImage Input, MainViewModel.Look? Baked,
        MainViewModel.Look? LookAfter, bool LookOnTop, MagickImage Current, int Turns = 0, bool Flip = false);

    /// <summary>
    /// Ergebnis des Modells, noch nicht übernommen: Stärke und Hautstruktur mischen es live neu
    /// (<see cref="IAiRun"/>), erst „Übernehmen“ macht daraus einen Bearbeitungsschritt.
    /// </summary>
    private IAiRun? _run;
    private RunSetup? _setup;
    private AiModel? _runModel;   // womit gerechnet wurde — Name in der Liste „Bearbeitungen“

    /// <summary>Gefundene Gesichter des offenen Ergebnisses — für ein neues Rechnen mit anderer Natürlichkeit.</summary>
    private IReadOnlyList<DetectedFace>? _runFaces;

    /// <summary>
    /// Letzter übernommener KI-Schritt: Ergebnis, Bild davor und der dabei eingerechnete Look — ein neuer Lauf
    /// beginnt wieder vor der KI, statt auf ihrem Ergebnis aufzubauen.
    /// </summary>
    private (MagickImage Result, MagickImage Base, MainViewModel.Look? Baked)? _lastAi;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand))]
    [NotifyPropertyChangedFor(nameof(CanShowMap))]
    public partial bool HasPending { get; set; }

    /// <summary>Die Einstellungen (Dunst …) werden gerade über das Ergebnis gelegt — Fortschritt wie beim Rechnen zeigen.</summary>
    [ObservableProperty]
    public partial bool IsRenderingLook { get; set; }

    public bool CanChooseSize => IsEnhanceModel && !IsRunning;
    public bool CanShowMap => IsFaceModel && HasPending;

    // Vorgaben der Haut-Regler: vom Benutzer am Foto 172251 (2012) als passend befunden (0.5.20)
    private const double DefaultTexture = 69, DefaultFolds = 48, DefaultGrain = 31;
    private const double DefaultGrainSize = 22;   // ×0,46: 2^((22 − 50) / 25)

    /// <summary>Hautstruktur 0…100 %: feine Struktur aus dem Bild davor statt der glatten Modellhaut (nur Gesichter).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextureLabel))]
    public partial double Texture { get; set; } = DefaultTexture;

    public string TextureLabel => $"{Texture:0} %";

    /// <summary>Falten &amp; Züge 0…100 %: was das Modell an Falten geglättet hat, aus dem Original zurück (nur Haut).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldsLabel))]
    public partial double Folds { get; set; } = DefaultFolds;

    public string FoldsLabel => $"{Folds:0} %";

    /// <summary>Körnung 0…200 %: 100 % = so viel Korn wie im übrigen Foto gemessen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GrainLabel))]
    public partial double Grain { get; set; } = DefaultGrain;

    public string GrainLabel => $"{Grain:0} %";

    /// <summary>Korngröße als Regler 0…100: Mitte = wie im Foto gemessen, je Viertel doppelt so grob bzw. fein.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GrainSizeLabel))]
    public partial double GrainSize { get; set; } = DefaultGrainSize;

    private double GrainFactor => Math.Pow(2, (GrainSize - 50) / 25);

    public string GrainSizeLabel => $"×{GrainFactor:0.0#}";

    /// <summary>Karte statt Bild: rot, wo Falten zurückkommen; blau, was vom Modell bleibt.</summary>
    [ObservableProperty] public partial bool ShowMap { get; set; }

    private AiBlend CurrentBlend => new(Strength / 100, Texture / 100, Folds / 100, Grain / 100, GrainFactor);

    partial void OnFoldsChanged(double value) => RequestPreview();
    partial void OnGrainChanged(double value) => RequestPreview();
    partial void OnGrainSizeChanged(double value) => RequestPreview();
    partial void OnShowMapChanged(bool value) => RequestPreview();

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

    /// <summary>
    /// Ausgangsbild und Look für einen neuen Lauf. Immer vom Stand vor der KI: Ist das Bild das Ergebnis des
    /// letzten KI-Schritts, rechnet der Lauf auf dem Bild davor — KI auf KI würde Gesichter immer glatter machen.
    /// Der Look kommt nie doppelt ins Bild.
    /// </summary>
    private (MagickImage Base, MainViewModel.Look Apply, MainViewModel.Look? Restore) ChooseStart(MagickImage current)
    {
        var look = _main.CurrentLook();
        if (_lastAi is { } last && ReferenceEquals(current, last.Result))
        {
            // letzter Schritt ohne eingerechneten Look: Regler gelten wie eingestellt
            if (last.Baked is null) return (last.Base, look, null);
            // mit eingerechnetem Look und Reglern noch auf null: derselbe Look, vom Bild davor aus
            if (look.IsNone) return (last.Base, last.Baked, last.Baked);
        }
        // Nach der KI gedreht oder zugeschnitten: vom Bild ohne KI aus (spätere Schritte noch einmal angewendet)
        if (_main.CurrentWithoutAi is { } clean && !ReferenceEquals(clean, current)) return (clean, look, null);
        return (current, look, null);   // neu, oder Regler seither verstellt: vom aktuellen Bild aus
    }

    private async Task ComputeAsync(bool rerun)
    {
        if (SelectedModel is not { } model || _main.CurrentImage is not { } current) return;
        if (Store.PathOf(model) is not { } modelPath)
        {
            Status = Tr.T("Modell fehlt — bitte in den Einstellungen herunterladen.");
            return;
        }

        var lookFirst = LookFirst;
        if (!rerun && lookFirst && _main.SettingsShared)
        {
            Status = Tr.T("„Erst Einstellungen“ geht nur bei Einstellungen je Bild — dazu „Gleiche Einstellungen für alle Bilder“ ausschalten.");
            return;
        }

        RunSetup? setup = rerun ? _setup : null;
        var start = setup is null ? ChooseStart(current) : default;
        var image = setup?.Input ?? start.Base;

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
        var clock = Stopwatch.StartNew();
        Status = Tr.T("KI rechnet — Fortschritt unten in der Vorschau.");
        Activity(Tr.T("Modell wird geladen …"), 0, cancel: true);

        var progress = new Progress<double>(f =>
        {
            Progress = f * 100;
            if (f > 0) Activity($"{f * 100:0} %{Remaining(clock.Elapsed, f)}", f * 100, cancel: true);
        });

        // Zwischenstände sind im gedrehten Pixelbild — für die Anzeige zurückdrehen und mit dem Rezept zeigen
        var geometry = _main.ShownGeometry;
        var (runTurns, runFlip) = setup is { } open ? (open.Turns, open.Flip) : _main.AiOrientation;
        void Preview(AiPreviewFrame frame)
        {
            var shownFrame = MainViewModel.ShowWithGeometry(frame, runTurns, runFlip, geometry.Recipe, geometry.Stages);
            Dispatcher.UIThread.Post(() =>
            {
                // nur zeigen, solange noch dasselbe Bild angezeigt wird
                if (IsRunning && _main.CurrentImagePath == path)
                    _main.OverrideBitmap = BitmapOps.FromBgra(shownFrame.Bgra, shownFrame.Width, shownFrame.Height);
            });
        }

        MagickImage? rendered = null;   // eigens gerechnetes Eingangsbild — gehört uns, bis es ein Lauf übernimmt
        try
        {
            if (setup is null)
            {
                if (lookFirst && !start.Apply.IsNone)
                {
                    // Erst die Einstellungen in voller Größe ins Bild, dann bekommt es das Modell
                    Activity(Tr.T("Einstellungen werden ins Bild gerechnet …"), 0, cancel: true);
                    var (from, apply) = (start.Base, start.Apply);
                    var plan = apply.Plan((int)from.Width, (int)from.Height, new Progress<WorkProgress>(p =>
                    {
                        Progress = p.Fraction * 100;
                        Activity(p.Step, p.Fraction * 100, cancel: true);
                    }), cts.Token);
                    rendered = await Task.Run(() =>
                    {
                        var looked = MainViewModel.RenderLook(from, apply, plan);
                        if (MainViewModel.Orient(looked, runTurns, runFlip) is not { } turned) return looked;
                        looked.Dispose();
                        return turned;
                    }, cts.Token);
                    setup = new RunSetup(start.Base, rendered, apply, apply.Neutral(), LookOnTop: false, current, runTurns, runFlip);
                }
                else
                {
                    // KI zuerst; ein eingerechneter Look des letzten Schritts kommt beim Übernehmen in die Regler zurück.
                    // Das Modell bekommt das Bild so gedreht, wie es angezeigt wird (eigenes Bild, gehört dem Lauf).
                    var from = start.Base;
                    rendered = await Task.Run(() => MainViewModel.Orient(from, runTurns, runFlip), cts.Token);
                    setup = new RunSetup(start.Base, rendered ?? start.Base, null, start.Restore, LookOnTop: !lookFirst, current, runTurns, runFlip);
                }
                image = setup.Input;
            }

            Activity(Tr.T("Modell wird geladen …"), Progress, cancel: true);
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
                    Activity(Tr.T("Gesichter werden gesucht …"), Progress, cancel: true);
                    var input = image;
                    faces = await Task.Run(() =>
                        AiFaces.Detect(input, _sessions.Get(AiModelCatalog.FaceDetector, detectorPath)), cts.Token);
                }

                if (faces.Count == 0)
                {
                    Status = Tr.T("Kein Gesicht gefunden.");
                    return;
                }

                Activity(faces.Count == 1 ? Tr.T("1 Gesicht gefunden …") : Tr.T("{0} Gesichter gefunden …", faces.Count), Progress, cancel: true);
                var (found, input2, fidelity) = (faces, image, Fidelity / 100);
                var parserPath = Store.PathOf(AiModelCatalog.FaceParser);   // ohne: Falten ersatzweise ohne Augen und Mund
                run = await Task.Run(() =>
                {
                    var face = new OnnxFaceModel(_sessions.Get(model, modelPath), model) { Fidelity = fidelity };
                    var parser = parserPath is null ? null : new OnnxFaceParser(_sessions.Get(AiModelCatalog.FaceParser, parserPath));
                    return (IAiRun)AiFaces.Run(input2, found, face, parser, progress, cts.Token, Preview);
                }, cts.Token);
            }
            else
            {
                var input = image;
                run = await Task.Run(() =>
                {
                    var tiles = new OnnxTileModel(_sessions.Get(model, modelPath), model);
                    return (IAiRun)AiEnhancer.Run(input, tiles, factor, progress, cts.Token, Preview);
                }, cts.Token);
            }

            if (_main.CurrentImagePath != path) return;   // inzwischen ein anderes Bild gewählt
            if (!ReferenceEquals(_main.CurrentImage, setup.Current))
            {
                Status = Tr.T("Das Bild wurde inzwischen geändert — bitte neu berechnen.");
                return;
            }

            if (!ReferenceEquals(setup, _setup)) DisposeInput(_setup);
            (_run, _setup, _runFaces, _runModel) = (run, setup, faces, model);
            rendered = null;   // gehört jetzt zu _setup
            HasPending = true;
            Status = Tr.T("Fertig nach {0} — die Regler wirken jetzt live, „Übernehmen“ schreibt es ins Bild.",
                FormatDuration(clock.Elapsed));
            if (model.Kind == AiModelKind.Face && !Store.IsInstalled(AiModelCatalog.FaceParser))
                Status += " " + Tr.T("Tipp: „Gesichtsbereiche“ in den Einstellungen laden — dann wirken Falten genauer.");
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
            rendered?.Dispose();
            _runCts = null;
            IsRunning = false;
            EndActivity();
            if (HasPending) RequestPreview();   // fertiges Ergebnis (oder nach Abbruch das alte) mit den Reglern zeigen
            else _main.OverrideBitmap = null;
        }
    }

    /// <summary>Das eigens gerechnete Eingangsbild eines Laufs freigeben (nie das Bild aus dem Verlauf).</summary>
    private static void DisposeInput(RunSetup? setup)
    {
        if (setup is not null && !ReferenceEquals(setup.Input, setup.Base)) setup.Input.Dispose();
    }

    // Vorschau beim Ziehen: die letzte Anforderung gewinnt, gerechnet wird nie zweimal gleichzeitig
    private bool _previewRunning, _previewDirty;
    private CancellationTokenSource? _previewCts;
    private (IAiRun Run, AiPreviewSize Size)? _beforeOf;

    private void RequestPreview()
    {
        if (!HasPending || IsRunning) return;
        if (_previewRunning)
        {
            _previewDirty = true;
            _previewCts?.Cancel();   // veraltete Rechnung (Dunst …) zwischen zwei Schritten abbrechen
            return;
        }

        _ = ShowPreviewAsync();
    }

    /// <summary>Welcher Look über dem Ergebnis liegt: bei „erst KI“ die Regler (bzw. der Look, der zurückkommt).</summary>
    private MainViewModel.Look? PreviewLook(RunSetup setup) =>
        setup.LookOnTop ? setup.LookAfter ?? _main.CurrentLook() : null;

    private async Task ShowPreviewAsync()
    {
        _previewRunning = true;
        try
        {
            do
            {
                _previewDirty = false;
                if (_run is not { } run || _setup is not { } setup) return;
                var (blend, look, map) = (CurrentBlend, PreviewLook(setup), ShowMap ? run as FaceRun : null);
                var geometry = _main.ShownGeometry;
                var shaped = (geometry.Recipe.Present & geometry.Stages) != 0 || setup.Turns != 0 || setup.Flip;
                // wie die normale Vorschau: ohne Look und Größenänderung das Bild selbst, sonst verkleinert; mit Ausrichten
                // verkleinert im Format des Pixelbilds (gedreht/zugeschnitten wird danach)
                var (grain, targetWidth) = (_main.CurrentGrain(), _main.CurrentLook().TargetWidth);
                var size = !shaped && look is not { IsNone: false } && grain is null && _main.TargetIsImageSize
                    ? new AiPreviewSize(run.Width, run.Height)
                    : shaped
                        ? AiPreviewSize.Fit(run.Width, run.Height, Math.Max(_main.PreviewSize().Width, _main.PreviewSize().Height))
                        : _main.PreviewSize();

                // Einstellungen über dem Ergebnis (Dunst in mehreren Durchläufen …): Schritt im Label zeigen
                using var cts = _previewCts = new CancellationTokenSource();
                var plan = map is null && look is { IsNone: false }
                    ? look.Plan(size.Width, size.Height, new Progress<WorkProgress>(p =>
                    {
                        if (cts.IsCancellationRequested) return;
                        Activity(p.Step, p.Fraction * 100, cancel: false);
                    }), cts.Token)
                    : null;

                AiPreviewFrame shown;
                try
                {
                    if (plan is not null)
                    {
                        Progress = 0;
                        IsRenderingLook = true;
                    }
                    var (turns, flip) = (setup.Turns, setup.Flip);
                    // Körnung immer obenauf — gleich, ob der Look davor ins Bild kam oder darüber liegt (Ausgabegröße: targetWidth)
                    shown = await Task.Run(() => map is not null
                        ? Render(MainViewModel.ShowWithGeometry(map.Map(blend, size), turns, flip, geometry.Recipe, geometry.Stages), null, null)
                        : Render(MainViewModel.ShowWithGeometry(run.Preview(blend, size), turns, flip, geometry.Recipe, geometry.Stages), look, plan,
                            grain, targetWidth), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    continue;   // neuere Reglerstellung wartet schon
                }
                finally
                {
                    _previewCts = null;
                    IsRenderingLook = false;
                    if (plan is not null && !IsRunning) EndActivity();
                }

                if (ReferenceEquals(run, _run) && !IsRunning)
                {
                    _main.OverrideBitmap = BitmapOps.FromBgra(shown.Bgra, shown.Width, shown.Height);
                }

                // Vorher-Seite für „Vorher / Nachher“: das Bild vor der KI, ohne Einstellungen — so zeigt der Vergleich
                // links das Original und rechts KI samt allen Einstellungen
                if (_beforeOf is not { } b || !ReferenceEquals(b.Run, run) || b.Size != size)
                {
                    var origin = setup.Base;
                    // Vorher ist das ungedrehte Pixelbild: Größe passend zur gedrehten Vorschau, dann mit Rezept zeigen
                    var baseSize = setup.Turns % 2 == 1 ? size with { Width = size.Height, Height = size.Width } : size;
                    var before = await Task.Run(() =>
                        MainViewModel.ShowWithGeometry(Original(origin, baseSize), 0, false, geometry.Recipe, geometry.Stages));
                    if (ReferenceEquals(run, _run))
                    {
                        _beforeOf = (run, size);
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

    private static AiPreviewFrame Render(AiPixels frame, MainViewModel.Look? look, WorkPlan? plan,
        GrainSettings? grain = null, int targetWidth = 0) =>
        new(MainViewModel.RenderLookPreview(frame.Bgra, frame.Width, frame.Height, look, plan,
            grain, targetWidth > 0 ? (double)frame.Width / targetWidth : 1), frame.Width, frame.Height);

    /// <summary>Das Bild vor der KI in Vorschaugröße, ohne Einstellungen (für die Vorher-Seite).</summary>
    private static AiPreviewFrame Original(MagickImage image, AiPreviewSize size)
    {
        using var small = ImageOps.Resize(image, size.Width, size.Height, size.Filter, size.GammaCorrect);
        var pixels = ImageOps.ToDisplayPixels(small);
        var bgra = Pixels.ToBytes(pixels, dither: true);
        PixelPool.Return(pixels);
        return new AiPreviewFrame(bgra, size.Width, size.Height);
    }

    private bool CanCommit => HasPending && !IsRunning && !_main.IsEditing;

    /// <summary>Ergebnis mit den aktuellen Reglern in voller Größe ins Bild — ein Schritt, rückgängig mit Strg+Z.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task Commit()
    {
        if (_run is not { } run || _setup is not { } setup) return;
        var blend = CurrentBlend;
        _fidelityCts?.Cancel();

        // Inzwischen anders bearbeitet (Zuschnitt, Rückgängig …)? Dann passt das Ergebnis nicht mehr.
        if (!ReferenceEquals(_main.CurrentImage, setup.Current))
        {
            ClearRun();
            Status = Tr.T("Das Bild wurde inzwischen geändert — bitte neu berechnen.");
            return;
        }

        Status = Tr.T("Wird übernommen …");

        // Gerechnet wurde auf dem Stand vor der KI — das Ergebnis ersetzt das aktuelle Bild, statt darauf aufzubauen.
        // Steckt der Look im Ergebnis, gehen die Regler im selben Schritt auf null (nichts doppelt).
        // Mit Haut & Details auch die Fassung ohne sie behalten — so lassen sie sich in der Liste „Bearbeitungen“ ausschalten
        var details = run.SupportsDetails && (blend.Texture > 0 || blend.Folds > 0 || blend.Grain > 0);
        var plainBlend = blend with { Texture = 0, Folds = 0, Grain = 0 };
        bool ok;
        _committing = true;
        try
        {
            // Zurückgedreht ins Pixelbild (das Modell bekam es gedreht, wie es angezeigt wurde)
            MagickImage Upright(MagickImage img)
            {
                if (MainViewModel.Unorient(img, setup.Turns, setup.Flip) is not { } back) return img;
                img.Dispose();
                return back;
            }
            ok = await _main.ApplyImageEditAsync(_ => (Upright(run.Compose(blend)), details ? Upright(run.Compose(plainBlend)) : null),
                StepLabel(), StepDetail(blend), details ? SkinDetail(blend) : null, setup.LookAfter);
        }
        finally
        {
            _committing = false;
        }
        if (ok && _main.CurrentImage is { } result) _lastAi = (result, setup.Base, setup.Baked);
        ClearRun();
        Status = ok ? Tr.T("Übernommen — rückgängig mit Strg+Z.") : Tr.T("Fehlgeschlagen — siehe Meldung oben.");
    }


    /// <summary>Neue Bilder: alles wie nach dem Programmstart (erstes vorhandenes Modell, Vorgaben der Regler).</summary>
    private void ResetToStart()
    {
        if (HasPending) Discard();
        _lastAi = null;
        Status = null;
        SelectedModel = Available.FirstOrDefault();
        SelectedSize = SizeOptions[0];
        SelectedOrder = OrderOptions[0];
        Strength = 100;
        Texture = DefaultTexture;
        Folds = DefaultFolds;
        Grain = DefaultGrain;
        GrainSize = DefaultGrainSize;
        ShowMap = false;
        Fidelity = 60;
    }


    /// <summary>Name des KI-Schritts in der Liste „Bearbeitungen“.</summary>
    private string StepLabel() => Tr.T("KI: {0}", _runModel is { } m ? Tr.T(m.Name) : "");

    /// <summary>Kurzinfo zum KI-Schritt (Zeile „KI“).</summary>
    private static string StepDetail(AiBlend blend) => Tr.T("Stärke {0:0} %", blend.Strength * 100);

    /// <summary>Kurzinfo zur Zeile „Haut &amp; Details“.</summary>
    private static string SkinDetail(AiBlend blend)
    {
        var parts = new List<string>();
        if (blend.Texture > 0) parts.Add(Tr.T("Haut {0:0} %", blend.Texture * 100));
        if (blend.Folds > 0) parts.Add(Tr.T("Falten {0:0} %", blend.Folds * 100));
        if (blend.Grain > 0) parts.Add(Tr.T("Korn {0:0} %", blend.Grain * 100));
        return string.Join(" · ", parts);
    }

    private bool _committing;   // eigenes Übernehmen ändert das Bild — kein Grund zum Verwerfen
    private bool CanDiscard => HasPending && !IsRunning;

    /// <summary>Ergebnis verwerfen: das Bild bleibt, wie es war.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        ClearRun();
        Status = null;
    }

    private void ClearRun()
    {
        _fidelityCts?.Cancel();
        if (IsRunning) _runCts?.Cancel();   // ein Neurechnen benutzt das Eingangsbild noch — dann gibt es der GC frei
        else DisposeInput(_setup);
        _previewCts?.Cancel();
        (_run, _setup, _runFaces, _beforeOf, _runModel) = (null, null, null, null, null);
        HasPending = false;
        ShowMap = false;
        _main.OverrideBitmap = null;
        _main.OverrideBeforeBitmap = null;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>Öffnet die Einstellungen (Tab „KI-Modelle“).</summary>
    [RelayCommand]
    private Task OpenModels() => _main.OpenSettingsCommand.ExecuteAsync(null);


    /// <summary>Fortschritt in der einen Anzeige unten mitte in der Vorschau (für alles, was rechnet).</summary>
    private void Activity(string text, double progress, bool cancel)
    {
        _main.ActivityText = Tr.T("KI · {0}", text);
        _main.ActivityProgress = progress;
        _main.ActivityCancel = cancel ? CancelCommand : null;
    }

    private void EndActivity()
    {
        _main.ActivityText = null;
        _main.ActivityProgress = -1;
        _main.ActivityCancel = null;
    }
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

        // Bild selbst geändert (Drehen, Zuschnitt, Rückgängig), während ein Ergebnis offen ist: Es passt nicht mehr —
        // sonst läge es im alten Format über dem neuen Bild (schmal, Gesichter an falscher Stelle)
        if (e.PropertyName == nameof(MainViewModel.FullBitmap) && HasPending && !_committing
            && _setup is { } open && !ReferenceEquals(_main.CurrentImage, open.Current))
        {
            ClearRun();
            Status = Tr.T("Das Bild wurde geändert — offenes Ergebnis verworfen, bitte neu berechnen.");
            return;
        }

        // Regler oder Zielgröße verstellt, während ein Ergebnis offen ist: Vorschau mitziehen (bei „erst KI“ liegt der Look darüber)
        if (e.PropertyName is nameof(MainViewModel.AdjustedBitmap) or nameof(MainViewModel.FullBitmap) && _setup is not null)
            RequestPreview();

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
            .Where(m => m.Kind is AiModelKind.Enhance or AiModelKind.Face && Store.IsInstalled(m)
                        && (m.Kind != AiModelKind.Face || detector))
            .ToList();

        var selected = SelectedModel;
        Available.Clear();
        foreach (var m in usable) Available.Add(m);
        SelectedModel = usable.Contains(selected!) ? selected : usable.FirstOrDefault();
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(FolderText));
        ModelsChanged?.Invoke(this, EventArgs.Empty);
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

    internal bool IsBusy => IsRunning || BusyElsewhere?.Invoke() == true;


    /// <summary>Rechnet gerade das Entfernen? (Modelle dann nicht löschen.)</summary>
    internal Func<bool>? BusyElsewhere { get; set; }

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

/// <summary>Eine Gruppe in der Verwaltung (Kasten mit Überschrift und kurzem Hinweis).</summary>
public sealed record AiModelGroup(string Title, string Hint, IReadOnlyList<AiModelItem> Items);

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
