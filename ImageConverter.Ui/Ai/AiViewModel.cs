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
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Platform;
using ImageConverter.Imaging;
using ImageConverter.ViewModels;
using ImageMagick;

namespace ImageConverter.Ai;

/// <summary>
/// AI enhancement and model management: the group box in the main window (<see cref="AiPanel"/>) and the
/// "Leistung" (performance) and AI model pages in the settings (<see cref="AiModelPageView"/>,
/// <see cref="PerformanceView"/>, <see cref="ModelFolderView"/>).
/// </summary>
/// <remarks>
/// The AI code is not self-contained: <c>MainViewModel</c> (upscaling, dewarping, history), <c>CropView</c> and parts of
/// the Core imaging code use <c>ImageConverter.Core.Ai</c> as well.
/// </remarks>
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
        _sessions.Measured = AppSettings.Load().AiSpeed;   // last speed test: GPU or CPU
        Upscalers.OnGpu = _sessions.OnGpu;
        MainViewModel.RestoreUpscaleTimes();   // times measured on this computer instead of the defaults measured on a laptop
        Models = AiModelCatalog.All.Select(m => new AiModelItem(this, m)).ToList();
        ModelGroups =
        [
            Group("BILDER VERBESSERN", "Rauschen, JPEG-Artefakte, Unschärfe, Vergrößern – in der Rubrik „KI“.", AiModelKind.Enhance),
            Group("GESICHTER", "Unscharfe oder beschädigte Gesichter wiederherstellen – in der Rubrik „KI“.", AiModelKind.Face),
            Group("GESICHTSERKENNUNG", "Hilfsmodelle für die Gesichtsmodelle: finden Gesichter und ihre Bereiche. Werden mit jedem Gesichtsmodell automatisch geladen.", AiModelKind.Helper),
            Group("ENTFERNEN", "Wasserzeichen, Schrift oder Störendes übermalen und füllen lassen – in der Rubrik „Entfernen“.", AiModelKind.Inpaint),
            Group("BEGRADIGEN", "Fotografierte, gewölbte oder zerknitterte Blätter mit einem Klick gerade ziehen – Symbol „KI begradigen“ in der Vorschau.", AiModelKind.Dewarp),
            Group("UPSCALING", "Vergrößern mit KI – unter „Größe“, Liste „Verfahren“ (KI-Scaler). Real-ESRGAN x4v3 ist dieselbe Datei wie „Allround“ oben.", AiModelKind.Upscale, AiModelCatalog.Allround),
            Group("STABLE DIFFUSION 2.1", "Teile für PiSA-SR. Werden mit PiSA-SR automatisch geladen; die App baut daraus beim ersten Gebrauch ihr Modell.", AiModelKind.Part),
            Group("HINTERGRUND", "Motiv freistellen, Hintergrund transparent oder unscharf – in der Rubrik „Hintergrund“. Ein Modell reicht; BiRefNet ist der Standard.", AiModelKind.Background),
        ];
        // One settings page per use case; helper models and parts appear with the use case that needs them.
        ImprovePage = new([ModelGroups[0]]);
        FacesPage = new([ModelGroups[1], ModelGroups[2]]);
        RemovePage = new([ModelGroups[3]]);
        DewarpPage = new([ModelGroups[4]]);
        UpscalePage = new([ModelGroups[5], ModelGroups[6]]);
        BackgroundPage = new([ModelGroups[7]]);


        Rescan();
        WatchFolders();
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.FilesReplaced += (_, _) => ResetToStart();
    }

    internal AiModelStore Store { get; }

    /// <summary>Opened models; shared with the removal tab so that only one large model is held in memory.</summary>
    internal OnnxSessions Sessions => _sessions;

    // ------------------------------------------------------------ GPU

    // If a GPU is available it is used, unless the speed test measured the CPU as faster on this computer (test first,
    // then use the faster one); the properties here only report what currently applies.

    /// <summary>What the AI currently computes on, or why the GPU is not used (Settings › Performance).</summary>
    public string GpuStatus =>
        OnnxSessions.GpuName is not { } name ? Tr.T("Diese Laufzeit kann keine Grafikkarte ansprechen — die KI rechnet auf der CPU.")
        : _sessions.CpuFasterHere ? Tr.T("Die KI rechnet auf der CPU — laut Geschwindigkeitstest ist sie hier schneller als die Grafikkarte ({0}).", name)
        : _sessions.GpuProblem is { } problem ? Tr.T("Die Grafikkarte ({0}) ließ sich nicht nutzen — die KI rechnet auf der CPU. Grund: {1}", name, problem)
        : Tr.T("Die KI-Scaler (Größe › Verfahren) rechnen auf der Grafikkarte ({0}), die übrigen Modelle (Gesichter, Entfernen, Begradigen) auf der CPU.", name);

    /// <summary>What denoising and dehazing compute on (ILGPU: OpenCL/CUDA, independent of the AI runtime).</summary>
    public string FilterGpuStatus =>
        GpuFilters.DeviceName is { } device
            ? GpuFilters.UsesGpu
                ? Tr.T("Entrauschen und Dunst rechnen auf der Grafikkarte ({0}).", device)
                : Tr.T("Entrauschen und Dunst rechnen auf der CPU — laut Geschwindigkeitstest ist sie hier schneller als die Grafikkarte ({0}).", device)
            : GpuFilters.Problem is { } problem
                ? Tr.T("Entrauschen und Dunst rechnen auf der CPU — {0}", problem)
                : Tr.T("Entrauschen und Dunst nutzen die Grafikkarte, sobald eine mit OpenCL oder CUDA gefunden ist.");

    // ------------------------------------------------------------ Keeping the computer responsive

    /// <summary>Maximum CPU share in percent (slider 10 … 100, applied in steps of 5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuPercentLabel), nameof(CpuRestartHint))]
    public partial double CpuPercent { get; set; } = ComputeLimits.Percent;

    /// <summary>Hint shown because ImageMagick (loading, aligning, converting) applies a changed limit only after the next start.</summary>
    public string? CpuRestartHint => ComputeLimits.MagickNeedsRestart
        ? Tr.T("Für Laden, Ausrichten und Umwandeln gilt die neue Grenze ab dem nächsten Start; KI und Bildfilter sofort.")
        : null;

    partial void OnCpuPercentChanged(double value)
    {
        var percent = (int)Math.Round(value / 5) * 5;
        if (percent == ComputeLimits.Percent) return;
        ComputeLimits.SetPercent(percent);
        var clamped = ComputeLimits.Percent;   // after clamping to 10 … 100
        AppSettings.Update(s => s with { CpuPercent = clamped });
        // the measurements were made with the old limit
        OnPropertyChanged(nameof(SpeedTestText));
        OnPropertyChanged(nameof(FilterGpuStatus));
        RefreshGpuStatus();
    }

    public string CpuPercentLabel => ComputeLimits.Percent >= 100
        ? Tr.T("100 % · alle {0} Kerne", ComputeLimits.PhysicalCores)
        : Tr.T("{0} % · {1} von {2} Kernen", ComputeLimits.Percent, ComputeLimits.AllowedCores, ComputeLimits.PhysicalCores);

    /// <summary>Low priority: other programs take precedence; otherwise full speed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityHint))]
    public partial bool LowPriority { get; set; } = AppSettings.Load().LowPriority;

    partial void OnLowPriorityChanged(bool value)
    {
        AppSettings.Update(s => s with { LowPriority = value });
        if (value) ComputeLimits.LowerPriority();
        else ComputeLimits.TryNormalPriority();
    }

    /// <summary>Hint when switching low priority off only takes effect after a restart (Linux).</summary>
    public string? PriorityHint => !LowPriority && ComputeLimits.LowPriority
        ? Tr.T("Wird beim nächsten Start des Programms wirksam.")
        : null;

    // ------------------------------------------------------------ Speed test

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSpeedCommand))]
    public partial bool IsSpeedTesting { get; set; }

    [ObservableProperty] public partial string? SpeedTestProgress { get; set; }

    /// <summary>Result of the last test per area, or a hint that no test has been run yet.</summary>
    public string SpeedTestText
    {
        get
        {
            var settings = AppSettings.Load();
            if (settings.FilterSpeed is null && settings.AiSpeed is null && Upscalers.Learned().Count == 0)
                return Tr.T("Noch nicht getestet — die Grafikkarte wird genutzt, wenn eine da ist.");
            var lines = new List<string>();
            if (settings.FilterSpeed is { } f)
                lines.Add(Describe(Tr.T("Entrauschen und Dunst"), f, SpeedTest.FilterMachine(GpuFilters.DeviceName)));
            if (settings.AiSpeed is { } a)
                lines.Add(Describe(Tr.T("KI (Real-ESRGAN x4v3)"), a, SpeedTest.AiMachine(OnnxSessions.GpuProvider)));
            // Upscalers: estimated time for a typical photo (12 MP, ×4), once measured on this computer
            var learned = Upscalers.Learned();
            var device = Upscalers.OnGpu ? "gpu" : "cpu";
            var times = new[] { UpscaleMethod.Fast, UpscaleMethod.Medium, UpscaleMethod.Generative }
                .Where(m => learned.ContainsKey($"{m}|{device}"))
                .Select(m => $"{ImageConverter.Models.UpscaleOption.Of(m).Name}: {Upscalers.Format(Upscalers.Estimate(m, 4000, 3000))}")
                .ToList();
            if (times.Count > 0) lines.Add(Tr.T("Upscaler, geschätzt für ein Foto mit 12 MP: {0}", string.Join(" · ", times)));
            return string.Join("\n", lines);
        }
    }

    private static string Describe(string area, SpeedMeasure m, string machine)
    {
        var result = m.GpuSeconds is not { } gpu
            ? Tr.T("{0}: CPU {1:0.00} s, keine Grafikkarte nutzbar → CPU", area, m.CpuSeconds)
            : Tr.T("{0}: Grafikkarte {1:0.00} s · CPU {2:0.00} s → {3}", area, gpu, m.CpuSeconds,
                m.CpuFaster ? Tr.T("CPU") : Tr.T("Grafikkarte"));
        var when = Tr.T("gemessen {0:g}", m.When);
        return m.Machine == machine
            ? $"{result} ({when})"
            : $"{result} ({when}, {Tr.T("gilt nicht mehr: anderer Rechner — bitte neu testen")})";
    }

    private bool CanTestSpeed => !IsSpeedTesting;

    /// <summary>
    /// Runs the image filters and the AI once each on the GPU and the CPU, stores the faster choice and uses it from now
    /// on; then measures the upscalers. The AI test needs the small x4v3 model ("Allround").
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestSpeed))]
    private async Task TestSpeed()
    {
        IsSpeedTesting = true;
        try
        {
            SpeedTestProgress = Tr.T("Entrauschen und Dunst werden gemessen …");
            var filters = await Task.Run(() => SpeedTest.Filters());
            GpuFilters.Measured = filters;

            SpeedMeasure? ai = null;
            if (OnnxSessions.GpuProvider is null)
            {
                SpeedTestProgress = Tr.T("KI: Diese Laufzeit kann keine Grafikkarte ansprechen — nichts zu vergleichen.");
            }
            else if (Store.PathOf(AiModelCatalog.Allround) is not { } path)
            {
                SpeedTestProgress = Tr.T("Für den Test der KI bitte erst „Allround (klein)“ herunterladen (5 MB).");
            }
            else
            {
                SpeedTestProgress = Tr.T("KI wird gemessen …");
                ai = await Task.Run(() => SpeedTest.Ai(path, AiModelCatalog.Allround));
                _sessions.Measured = ai;
                SpeedTestProgress = null;
            }
            RefreshGpuStatus();   // set Upscalers.OnGpu according to the choice before the upscalers are measured

            // Measure the upscalers with the chosen device and CPU limit so that the time estimate is right from the first image.
            foreach (var method in new[] { UpscaleMethod.Fast, UpscaleMethod.Medium, UpscaleMethod.Generative })
            {
                SpeedTestProgress = Tr.T("Upscaler „{0}“ wird gemessen …", ImageConverter.Models.UpscaleOption.Of(method).Name);
                await Task.Run(() => _main.MeasureUpscaler(method, CancellationToken.None));
            }
            SpeedTestProgress = null;

            AppSettings.Update(s => s with { FilterSpeed = filters, AiSpeed = ai ?? s.AiSpeed });
        }
        catch (Exception e)
        {
            SpeedTestProgress = Tr.T("Test fehlgeschlagen: {0}", e.Message);
            AppLog.Write("Geschwindigkeitstest fehlgeschlagen", e.ToString());
        }
        finally
        {
            IsSpeedTesting = false;
            OnPropertyChanged(nameof(SpeedTestText));
            OnPropertyChanged(nameof(FilterGpuStatus));
            RefreshGpuStatus();
        }
    }

    /// <summary>Called after a model is opened: updates whether the GPU is used or, after an error, the CPU.</summary>
    internal void RefreshGpuStatus()
    {
        if (Upscalers.OnGpu != _sessions.OnGpu)
        {
            Upscalers.OnGpu = _sessions.OnGpu;
            _main.GpuChanged();
        }
        OnPropertyChanged(nameof(GpuStatus));
    }

    /// <summary>Raised after every rescan of the model folder (download, deletion, files copied by hand).</summary>
    internal event EventHandler? ModelsChanged;

    /// <summary>Shows progress in the single activity display at the bottom centre; also used by the removal tab.</summary>
    /// <param name="text">Text of the display.</param>
    /// <param name="progress">Progress 0…100.</param>
    /// <param name="cancel">Command of the cancel button; <c>null</c> hides it.</param>
    internal void ShowActivity(string text, double progress, System.Windows.Input.ICommand? cancel)
    {
        _main.ActivityText = text;
        _main.ActivityProgress = progress;
        _main.ActivityCancel = cancel;
    }

    internal void HideActivity() => EndActivity();

    /// <summary>All models for the model management (settings).</summary>
    public IReadOnlyList<AiModelItem> Models { get; }

    /// <summary>The models in groups for the model management.</summary>
    public IReadOnlyList<AiModelGroup> ModelGroups { get; }

    /// <summary>Settings pages (AI models section on the left), one per use case.</summary>
    public AiModelPage ImprovePage { get; }
    public AiModelPage FacesPage { get; }
    public AiModelPage RemovePage { get; }
    public AiModelPage DewarpPage { get; }
    public AiModelPage UpscalePage { get; }
    public AiModelPage BackgroundPage { get; }

    /// <summary>Asks the user for a file (set by the settings window); null when no window can show a dialog.</summary>
    internal Func<string, Task<string?>>? PickFile { get; set; }

    /// <param name="also">Models of other groups that are needed here as well (one file, two entries).</param>
    private AiModelGroup Group(string title, string hint, AiModelKind kind, params AiModel[] also) =>
        new(title, hint, Models.Where(m => m.Model.Kind == kind || also.Contains(m.Model)).OrderBy(m => m.Model.Kind == kind).ToList());   // shared models first (x4v3 = "KI Real-ESRGAN x4v3 · schnell")

    /// <summary>Installed, selectable models (without helper models).</summary>
    public ObservableCollection<AiModel> Available { get; } = [];

    /// <summary>Where the models are (or will be) stored.</summary>
    public string FolderText => Store.Folders.FirstOrDefault(Directory.Exists) ?? Store.Folders[0];

    /// <summary>Opens the model folder in the file manager, e.g. to place models there by hand.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Store.DownloadFolder;   // the folder is created below if needed
        Directory.CreateDirectory(folder);
        WatchFolders();
        if (!ImageConverter.Services.FolderOpener.TryOpen(folder))
            Status = Tr.T("Kein Dateimanager gefunden. Der Ordner ist: {0}", folder);
        OnPropertyChanged(nameof(FolderText));
    }

    // ------------------------------------------------------------ Selection

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnhanceModel), nameof(ModelHint), nameof(IsFaceModel), nameof(HasFidelity), nameof(CanShowMap))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial AiModel? SelectedModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthLabel))]
    public partial double Strength { get; set; } = 100;

    public string StrengthLabel => $"{Strength:0} %";

    public bool IsEnhanceModel => SelectedModel?.Kind == AiModelKind.Enhance;
    public bool HasModels => Available.Count > 0;

    public string? ModelHint => SelectedModel is { } m
        ? Tr.T(m.Description) + (m.NonCommercial ? "\n\n" + Tr.T("Lizenz: nur für nicht-kommerzielle Nutzung.") : "")
        : null;

    // ------------------------------------------------------------ Run

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(CancelCommand), nameof(CommitCommand), nameof(DiscardCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>Progress 0…100.</summary>
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }

    /// <summary>
    /// Order of AI and adjustments (haze, light, colour, denoising, sharpness). The adjustments are never applied twice:
    /// either the AI works on the image without them and the adjustments then also affect the faces as usual, or they
    /// are rendered into the image beforehand and reset to zero afterwards.
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
        // the pending result belongs to the order it was computed with
        if (HasPending) Discard();
    }

    /// <summary>
    /// Keeps only faces whose centre lies in the displayed image (crop, perspective correction …). Face detection gets
    /// the pixel image (only rotated/mirrored); without this filter, cropped-away faces would be processed and counted too.
    /// </summary>
    private static IReadOnlyList<DetectedFace> VisibleFaces(IReadOnlyList<DetectedFace> faces, RunSetup setup, GeometryRecipe recipe,
        GeometryStages stages)
    {
        int w, h;
        lock (setup.Base) (w, h) = ((int)setup.Base.Width, (int)setup.Base.Height);
        var (sw, sh) = recipe.SizeFor(w, h, stages);
        var orient = new GeometryRecipe(Turns: setup.Turns, Flip: setup.Flip);
        return faces.Where(f =>
        {
            var (px, py) = orient.ToSource((f.X1 + f.X2) / 2, (f.Y1 + f.Y2) / 2, w, h, GeometryStages.Orient);
            var (x, y) = recipe.ToResult(px, py, w, h, stages);
            return x >= 0 && y >= 0 && x < sw && y < sh;
        }).ToList();
    }

    /// <summary>How a run is set up: where it starts, what the model gets and which look applies afterwards.</summary>
    /// <param name="Base">Image state that the result replaces (it must still be current when committing).</param>
    /// <param name="Input">What the model gets: <paramref name="Base"/>, or Base with the look rendered in, oriented as displayed.</param>
    /// <param name="Baked">Look contained in <paramref name="Input"/>; null = none.</param>
    /// <param name="LookAfter">Slider values after committing; null = sliders stay as they are.</param>
    /// <param name="LookOnTop">The adjustments apply to the result afterwards, so the preview shows them on top.</param>
    /// <param name="Current">The current image at computation time; "Übernehmen" (commit) only works while it is still current.</param>
    /// <param name="Turns">Quarter turns by which <paramref name="Input"/> is rotated (faces upright, as displayed).</param>
    /// <param name="Flip">Whether <paramref name="Input"/> is also mirrored. The result is turned back with these values before committing.</param>
    private sealed record RunSetup(MagickImage Base, MagickImage Input, MainViewModel.Look? Baked,
        MainViewModel.Look? LookAfter, bool LookOnTop, MagickImage Current, int Turns = 0, bool Flip = false);

    /// <summary>
    /// Result of the model, not yet committed: strength and skin texture re-blend it live (<see cref="IAiRun"/>);
    /// only "Übernehmen" (commit) turns it into an editing step.
    /// </summary>
    private IAiRun? _run;
    private RunSetup? _setup;
    private AiModel? _runModel;   // model used for the computation; its name appears in the "Bearbeitungen" (edits) list

    /// <summary>Faces found for the pending result, reused when recomputing with a different fidelity.</summary>
    private IReadOnlyList<DetectedFace>? _runFaces;

    /// <summary>
    /// Last committed AI step: result, image before it and the look rendered in, so that a new run starts again
    /// before the AI instead of building on its result.
    /// </summary>
    private (MagickImage Result, MagickImage Base, MainViewModel.Look? Baked)? _lastAi;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand))]
    [NotifyPropertyChangedFor(nameof(CanShowMap))]
    public partial bool HasPending { get; set; }

    /// <summary>The adjustments (haze …) are being rendered over the result; progress is shown as during computation.</summary>
    [ObservableProperty]
    public partial bool IsRenderingLook { get; set; }

    public bool CanShowMap => IsFaceModel && HasPending;

    // Defaults of the skin sliders, chosen by visual comparison on a test portrait.
    private const double DefaultTexture = 69, DefaultFolds = 48, DefaultGrain = 31;
    private const double DefaultGrainSize = 22;   // ×0.46: 2^((22 − 50) / 25)

    /// <summary>Skin texture 0…100 %: fine structure from the image before instead of the smooth model skin (faces only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextureLabel))]
    public partial double Texture { get; set; } = DefaultTexture;

    public string TextureLabel => $"{Texture:0} %";

    /// <summary>Wrinkles and features 0…100 %: restores from the original the wrinkles the model smoothed out (skin only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldsLabel))]
    public partial double Folds { get; set; } = DefaultFolds;

    public string FoldsLabel => $"{Folds:0} %";

    /// <summary>Grain 0…200 %: 100 % = as much grain as measured in the rest of the photo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GrainLabel))]
    public partial double Grain { get; set; } = DefaultGrain;

    public string GrainLabel => $"{Grain:0} %";

    /// <summary>Grain size as a slider 0…100: the middle = as measured in the photo; each quarter doubles or halves the size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GrainSizeLabel))]
    public partial double GrainSize { get; set; } = DefaultGrainSize;

    private double GrainFactor => Math.Pow(2, (GrainSize - 50) / 25);

    public string GrainSizeLabel => $"×{GrainFactor:0.0#}";

    /// <summary>Shows a map instead of the image: red where wrinkles are restored, blue where the model result remains.</summary>
    [ObservableProperty] public partial bool ShowMap { get; set; }

    private AiBlend CurrentBlend => new(Strength / 100, Texture / 100, Folds / 100, Grain / 100, GrainFactor);

    partial void OnFoldsChanged(double value) => RequestPreview();
    partial void OnGrainChanged(double value) => RequestPreview();
    partial void OnGrainSizeChanged(double value) => RequestPreview();
    partial void OnShowMapChanged(bool value) => RequestPreview();

    /// <summary>CodeFormer fidelity: 0 = strongest enhancement, 100 = as close to the original as possible. It is a model input, so a change requires recomputing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FidelityLabel))]
    public partial double Fidelity { get; set; } = 60;

    public string FidelityLabel => $"{Fidelity:0} %";

    public bool IsFaceModel => SelectedModel?.Kind == AiModelKind.Face;
    public bool HasFidelity => SelectedModel?.Id == AiModelCatalog.CodeFormer.Id;

    partial void OnSelectedModelChanged(AiModel? value)
    {
        // a pending result belongs to the model it was computed with
        if (HasPending) Discard();
    }

    partial void OnStrengthChanged(double value) => RequestPreview();
    partial void OnTextureChanged(double value) => RequestPreview();

    private double _runFidelity;   // fidelity the pending result was computed with

    /// <summary>Fidelity changed since the computation; "KI berechnen" (compute) recomputes with the new value.</summary>
    private bool FidelityChangedSinceRun => HasPending && HasFidelity && Math.Abs(Fidelity - _runFidelity) > 0.01;

    partial void OnFidelityChanged(double value)
    {
        // Fidelity is a model input, but a change does not recompute automatically; only "KI berechnen" does.
        if (FidelityChangedSinceRun && !IsRunning)
            Status = Tr.T("Natürlichkeit geändert — „KI berechnen“ rechnet mit dem neuen Wert neu.");
    }

    private bool CanApply => !IsRunning && SelectedModel is not null && _main.HasImage && !_main.IsEditing;

    /// <summary>
    /// Runs the model and shows the result; it enters the image only with "Übernehmen" (commit). If only the fidelity was
    /// changed, the same input image and faces are reused and only the model is run again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task Apply() => ComputeAsync(rerun: FidelityChangedSinceRun && _runModel == SelectedModel);

    /// <summary>
    /// Chooses the start image and look for a new run, always from the state before the AI: if the image is the result
    /// of the last AI step, the run works on the image before it, because AI on AI would make faces smoother and
    /// smoother. The look never enters the image twice.
    /// </summary>
    private (MagickImage Base, MainViewModel.Look Apply, MainViewModel.Look? Restore) ChooseStart(MagickImage current)
    {
        var look = _main.CurrentLook();
        if (_lastAi is { } last && ReferenceEquals(current, last.Result))
        {
            // last step without a rendered-in look: the sliders apply as set
            if (last.Baked is null) return (last.Base, look, null);
            // with a rendered-in look and sliders still at zero: the same look, starting from the image before
            if (look.IsNone) return (last.Base, last.Baked, last.Baked);
        }
        // Rotated or cropped after the AI: start from the image without AI (later steps applied again)
        if (_main.CurrentWithoutAi is { } clean && !ReferenceEquals(clean, current)) return (clean, look, null);
        return (current, look, null);   // new, or sliders changed since: start from the current image
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

        // Enlarging belongs to "Größe" (upscaling); here always at original size (computed at ×4, averaged back down)
        const int factor = 1;
        if (model.Kind == AiModelKind.Enhance
            && !AiEnhancer.PossibleFactors(model.Scale, (int)image.Width, (int)image.Height).Contains(factor))
        {
            var mp = image.Width * factor * image.Height * factor / 1e6;
            Status = Tr.T("Zu groß: das Ergebnis hätte {0:0} Megapixel (höchstens {1}).", mp, AiEnhancer.MaxOutputPixels / 1_000_000);
            return;
        }

        var path = _main.CurrentImagePath;
        var fidelityUsed = Fidelity;
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        AppLog.Activity($"KI-Tab: {model.Name} ({(_sessions.OnGpu && model.Gpu ? "Grafikkarte" : "CPU")}) auf {System.IO.Path.GetFileName(path)}");
        Progress = 0;
        var clock = Stopwatch.StartNew();
        Status = Tr.T("KI rechnet — Fortschritt unten in der Vorschau.");
        Activity(Tr.T("Modell wird geladen …"), 0, cancel: true);

        var progress = new Progress<double>(f =>
        {
            Progress = f * 100;
            if (f > 0) Activity($"{f * 100:0} %{Remaining(clock.Elapsed, f)}", f * 100, cancel: true);
        });

        // Intermediate frames are in the rotated pixel image; turn them back for display and show them with the geometry recipe.
        var geometry = _main.ShownGeometry;
        var (runTurns, runFlip) = setup is { } open ? (open.Turns, open.Flip) : _main.AiOrientation;
        void Preview(AiPreviewFrame frame)
        {
            var shownFrame = MainViewModel.ShowWithGeometry(frame, runTurns, runFlip, geometry.Recipe, geometry.Stages);
            Dispatcher.UIThread.Post(() =>
            {
                // show only while the same image is still displayed
                if (IsRunning && _main.CurrentImagePath == path)
                    _main.OverrideBitmap = BitmapOps.FromBgra(shownFrame.Bgra, shownFrame.Width, shownFrame.Height);
            });
        }

        MagickImage? rendered = null;   // input image rendered for this run; owned here until a run setup takes it over
        try
        {
            if (setup is null)
            {
                if (lookFirst && !start.Apply.IsNone)
                {
                    // Render the adjustments into the image at full size first, then pass it to the model.
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
                    // AI first; a look rendered into the last step returns to the sliders on commit.
                    // The model gets the image rotated as displayed (a separate image owned by the run).
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
                    // The AI sees the whole pixel image; faces outside the crop (and other geometry) stay as they are.
                    faces = VisibleFaces(faces, setup!, geometry.Recipe, geometry.Stages);
                }

                if (faces.Count == 0)
                {
                    Status = Tr.T("Kein Gesicht gefunden.");
                    return;
                }

                Activity(faces.Count == 1 ? Tr.T("1 Gesicht gefunden …") : Tr.T("{0} Gesichter gefunden …", faces.Count), Progress, cancel: true);
                var (found, input2, fidelity) = (faces, image, fidelityUsed / 100);
                var parserPath = Store.PathOf(AiModelCatalog.FaceParser);   // without it, wrinkles fall back to a mask without eyes and mouth
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

            if (_main.CurrentImagePath != path) return;   // a different image was selected meanwhile
            if (!ReferenceEquals(_main.CurrentImage, setup.Current))
            {
                Status = Tr.T("Das Bild wurde inzwischen geändert — bitte neu berechnen.");
                return;
            }

            if (!ReferenceEquals(setup, _setup)) DisposeInput(_setup);
            (_run, _setup, _runFaces, _runModel, _runFidelity) = (run, setup, faces, model, fidelityUsed);
            rendered = null;   // now owned by _setup
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
            AppLog.Activity(null);
            if (HasPending) RequestPreview();   // show the finished result (or, after cancelling, the previous one) with the sliders
            else _main.OverrideBitmap = null;
        }
    }

    /// <summary>Disposes the input image rendered for a run (never the image from the history).</summary>
    private static void DisposeInput(RunSetup? setup)
    {
        if (setup is not null && !ReferenceEquals(setup.Input, setup.Base)) setup.Input.Dispose();
    }

    // Preview while dragging: the latest request wins, and two previews are never computed at the same time.
    private bool _previewRunning, _previewDirty;
    private CancellationTokenSource? _previewCts;
    private (IAiRun Run, AiPreviewSize Size)? _beforeOf;

    private void RequestPreview()
    {
        if (!HasPending || IsRunning) return;
        if (_previewRunning)
        {
            _previewDirty = true;
            _previewCts?.Cancel();   // cancel the outdated computation (haze …) between two steps
            return;
        }

        _ = ShowPreviewAsync();
    }

    /// <summary>Look rendered over the result: with "AI first" the sliders (or the look that is restored); otherwise none.</summary>
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
                // Like the normal preview: without look and size change the image itself, otherwise downscaled; with
                // geometry, downscaled in the format of the pixel image (rotation/cropping is applied afterwards).
                var (grain, targetWidth) = (_main.CurrentGrain(), _main.CurrentLook().TargetWidth);
                var size = !shaped && look is not { IsNone: false } && grain is null && _main.TargetIsImageSize
                    ? new AiPreviewSize(run.Width, run.Height)
                    : shaped
                        ? AiPreviewSize.Fit(run.Width, run.Height, Math.Max(_main.PreviewSize().Width, _main.PreviewSize().Height))
                        : _main.PreviewSize();

                // Adjustments over the result (haze in several passes …): show the current step in the label.
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
                    // Grain always on top, whether the look was rendered in before or lies on top (output size: targetWidth)
                    shown = await Task.Run(() => map is not null
                        ? Render(MainViewModel.ShowWithGeometry(map.Map(blend, size), turns, flip, geometry.Recipe, geometry.Stages), null, null)
                        : Render(MainViewModel.ShowWithGeometry(run.Preview(blend, size), turns, flip, geometry.Recipe, geometry.Stages), look, plan,
                            grain, targetWidth), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    continue;   // a newer slider position is already waiting
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

                // "Before" side of the before/after comparison: the image before the AI, without adjustments, so that the
                // comparison shows the original on the left and the AI result with all adjustments on the right.
                if (_beforeOf is not { } b || !ReferenceEquals(b.Run, run) || b.Size != size)
                {
                    var origin = setup.Base;
                    // "Before" is the unrotated pixel image: size matching the rotated preview, then shown with the recipe
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

    /// <summary>The image before the AI at preview size, without adjustments (for the "before" side; "Hintergrund" while it recomputes).</summary>
    internal static AiPreviewFrame Original(MagickImage image, AiPreviewSize size)
    {
        using var small = ImageOps.Resize(image, size.Width, size.Height, size.Filter, size.GammaCorrect);
        var pixels = ImageOps.ToDisplayPixels(small);
        var bgra = Pixels.ToBytes(pixels, dither: true);
        PixelPool.Return(pixels);
        return new AiPreviewFrame(bgra, size.Width, size.Height);
    }

    private bool CanCommit => HasPending && !IsRunning && !_main.IsEditing;

    /// <summary>Writes the result with the current sliders into the image at full size, as one step that Ctrl+Z undoes.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task Commit()
    {
        if (_run is not { } run || _setup is not { } setup) return;
        var blend = CurrentBlend;

        // Edited differently meanwhile (crop, undo …)? Then the result no longer fits.
        if (!ReferenceEquals(_main.CurrentImage, setup.Current))
        {
            ClearRun();
            Status = Tr.T("Das Bild wurde inzwischen geändert — bitte neu berechnen.");
            return;
        }

        Status = Tr.T("Wird übernommen …");

        // The computation used the state before the AI; the result replaces the current image instead of building on it.
        // If the look is contained in the result, the sliders go to zero in the same step (nothing applied twice).
        // With skin and details, the version without them is kept as well so they can be switched off in the
        // "Bearbeitungen" (edits) list.
        var details = run.SupportsDetails && (blend.Texture > 0 || blend.Folds > 0 || blend.Grain > 0);
        var plainBlend = blend with { Texture = 0, Folds = 0, Grain = 0 };
        bool ok;
        _committing = true;
        try
        {
            // Turned back into the pixel image (the model got it rotated as displayed).
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


    /// <summary>New images: resets everything as after program start (first installed model, default slider values).</summary>
    private void ResetToStart()
    {
        if (HasPending) Discard();
        _lastAi = null;
        Status = null;
        SelectedModel = Available.FirstOrDefault();
        SelectedOrder = OrderOptions[0];
        Strength = 100;
        Texture = DefaultTexture;
        Folds = DefaultFolds;
        Grain = DefaultGrain;
        GrainSize = DefaultGrainSize;
        ShowMap = false;
        Fidelity = 60;
    }


    /// <summary>Name of the AI step in the "Bearbeitungen" (edits) list.</summary>
    private string StepLabel() => Tr.T("KI: {0}", _runModel is { } m ? Tr.T(m.Name) : "");

    /// <summary>Short info for the AI step ("KI" row).</summary>
    private static string StepDetail(AiBlend blend) => Tr.T("Stärke {0:0} %", blend.Strength * 100);

    /// <summary>Short info for the "Haut &amp; Details" (skin and details) row.</summary>
    private static string SkinDetail(AiBlend blend)
    {
        var parts = new List<string>();
        if (blend.Texture > 0) parts.Add(Tr.T("Haut {0:0} %", blend.Texture * 100));
        if (blend.Folds > 0) parts.Add(Tr.T("Falten {0:0} %", blend.Folds * 100));
        if (blend.Grain > 0) parts.Add(Tr.T("Korn {0:0} %", blend.Grain * 100));
        return string.Join(" · ", parts);
    }

    private bool _committing;   // committing changes the image itself; that is no reason to discard
    private bool CanDiscard => HasPending && !IsRunning;

    /// <summary>Discards the result; the image stays as it was.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        ClearRun();
        Status = null;
    }

    private void ClearRun()
    {
        if (IsRunning) _runCts?.Cancel();   // a recomputation still uses the input image; the GC releases it in that case
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

    /// <summary>Opens the settings on the "Bilder verbessern" (enhance images) page with the models of the AI tab.</summary>
    [RelayCommand]
    private Task OpenModels() => _main.OpenSettingsCommand.ExecuteAsync(SettingsPage.Improve);


    /// <summary>Shows progress in the single activity display at the bottom centre of the preview (used for all computations).</summary>
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

        // Image itself changed (rotation, crop, undo) while a result is pending: the result no longer fits; otherwise it
        // would lie over the new image in the old format (narrow, faces in the wrong place).
        if (e.PropertyName == nameof(MainViewModel.FullBitmap) && HasPending && !_committing
            && _setup is { } open && !ReferenceEquals(_main.CurrentImage, open.Current))
        {
            ClearRun();
            Status = Tr.T("Das Bild wurde geändert — offenes Ergebnis verworfen, bitte neu berechnen.");
            return;
        }

        // Sliders or target size changed while a result is pending: update the preview (with "AI first" the look lies on top).
        if (e.PropertyName is nameof(MainViewModel.AdjustedBitmap) or nameof(MainViewModel.FullBitmap) && _setup is not null)
            RequestPreview();

        // different image selected: intermediate frame and pending result do not belong to it
        if (e.PropertyName == nameof(MainViewModel.CurrentFileName))
        {
            if (HasPending) Discard();
            else if (_main.OverrideBitmap is not null) _main.OverrideBitmap = null;
        }
    }

    // ------------------------------------------------------------ Model management

    /// <summary>Rescans which models are installed; models copied into the folder by hand are detected as well.</summary>
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
        // Face models need face detection (and other required models); download them along with it.
        foreach (var id in item.Model.Requires)
            if (Models.FirstOrDefault(m => m.Model.Id == id) is { IsInstalled: false, IsDownloading: false } required)
                _ = required.DownloadCommand.ExecuteAsync(null);

        await Store.DownloadAsync(item.Model, Http, item.ProgressReporter, item.Token);
        Rescan();
        WatchFolders();   // the folder may only exist now
    }

    /// <summary>Takes over a file downloaded by hand (RMBG-2.0) after checking size and checksum.</summary>
    internal async Task ImportAsync(AiModelItem item, string file)
    {
        await Store.ImportAsync(item.Model, file, item.ProgressReporter, item.Token);
        Rescan();
        WatchFolders();
    }

    internal void Delete(AiModelItem item)
    {
        _sessions.Dispose();   // release opened models; otherwise the file is locked
        Store.Delete(item.Model);
        Rescan();
    }

    internal bool IsBusy => IsRunning || _main.IsUpscaling || BusyElsewhere?.Invoke() == true;


    /// <summary>Reports whether the removal or background tab is computing (models must not be deleted then).</summary>
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
                // without a watcher, changes are detected the next time the settings are opened
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

/// <summary>A group in the model management (box with heading and short hint).</summary>
public sealed record AiModelGroup(string Title, string Hint, IReadOnlyList<AiModelItem> Items)
{
    /// <summary>Whether the heading is shown: only when the page shows several groups (otherwise it already appears on the left).</summary>
    public bool ShowTitle { get; init; }
}

/// <summary>A settings page with one or more groups of models.</summary>
public sealed record AiModelPage
{
    public AiModelPage(IReadOnlyList<AiModelGroup> groups) =>
        Groups = groups.Select(g => g with { ShowTitle = groups.Count > 1 }).ToList();

    public IReadOnlyList<AiModelGroup> Groups { get; }
}

/// <summary>A model in the model management: installed state, download with progress, cancel, delete.</summary>
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
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ShowDownload), nameof(ShowManual))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand), nameof(ImportCommand))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ShowDownload), nameof(ShowManual))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand), nameof(CancelCommand), nameof(ImportCommand))]
    public partial bool IsDownloading { get; set; }

    /// <summary>Whether to offer the hand download (page of the provider, choose the file); only for models without a URL.</summary>
    public bool ShowManual => !IsInstalled && !IsDownloading && Model.ManualPage is not null;

    /// <summary>Opens the provider's page, where the user agrees to the terms and downloads the file.</summary>
    [RelayCommand]
    private void OpenPage()
    {
        if (Model.ManualPage is { } page && !ImageConverter.Services.FolderOpener.TryOpen(page))
            Problem = Tr.T("Kein Browser gefunden. Die Seite ist: {0}", page);
    }

    private bool CanImport => !IsInstalled && !IsDownloading && Model.ManualPage is not null;

    /// <summary>Takes over the downloaded file: asks for it, checks size and checksum and copies it into the model folder.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        if (_owner.PickFile is not { } pick || await pick(Tr.T("Heruntergeladene Datei „{0}“ wählen", Model.MainFile)) is not { } file) return;
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Progress = 0;
        Problem = null;
        try
        {
            await _owner.ImportAsync(this, file);
        }
        catch (OperationCanceledException)
        {
            Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _cts.Dispose();
            _cts = null;
            Refresh();
        }
    }

    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }

    /// <summary>Whether to offer a download; not for models without a URL (their file is placed by hand).</summary>
    public bool ShowDownload => !IsInstalled && !IsDownloading && Model.Url.Length > 0;

    public string StateText => IsDownloading ? Tr.T("wird geladen …") : IsInstalled ? Tr.T("✓ vorhanden") : Tr.T("nicht geladen");

    internal void Refresh() => IsInstalled = _owner.Store.IsInstalled(Model);

    private bool CanDownload => !IsInstalled && !IsDownloading && Model.Url.Length > 0;

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
