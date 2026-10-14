using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Ai;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageConverter.Imaging;
using ImageConverter.Models;
using ImageMagick;

namespace ImageConverter.ViewModels;

/// <summary>Rückfrage an die Ansicht: Titel, Text, Antworten (untereinander), hervorgehobene Antwort.</summary>
public sealed record ChoiceRequest(string Title, string Message, IReadOnlyList<string> Choices, int Default = 0);

// Upscaling (Gruppe unter „Größe“): beim Vergrößern entstehen die Pixel nicht nur aus dem Filter, sondern aus einer KI.
// Gerechnet wird nur auf Knopfdruck (oder nach Rückfrage beim Speichern) — das Ergebnis in vierfacher Größe bleibt im
// Speicher und dient Vorschau und Export als Grundlage fürs Skalieren. Treue und Details mischen danach ohne neue Rechnung.
// Die Prüfansicht zeigt das Ergebnis in voller Auflösung (zoomen bis 100 % und mehr), Vorher/Nachher vergleicht mit dem
// Filter; während der Rechnung kommen fertige Kacheln sofort hinein.
public partial class MainViewModel
{
    public IReadOnlyList<UpscaleOption> UpscaleOptions { get; } = UpscaleOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesAiUpscaler), nameof(UpscaleModelMissing), nameof(UpscaleDownloadText))]
    public partial UpscaleOption SelectedUpscaler { get; set; } = UpscaleOption.All[0];

    /// <summary>0 … 1: Treue-Sicherung — verkleinert ergibt das Ergebnis wieder das Original.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpscaleFidelityLabel))]
    public partial double UpscaleFidelity { get; set; } = 1;

    /// <summary>0 … 1: Anteil der KI gegenüber dem Filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpscaleDetailsLabel))]
    public partial double UpscaleDetails { get; set; } = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpscaleCommand), nameof(CancelUpscaleCommand))]
    public partial bool IsUpscaling { get; set; }

    /// <summary>Prüfansicht: Ergebnis in voller Auflösung statt der Vorschau (ohne Licht/Farbe/Schärfe).</summary>
    [ObservableProperty] public partial bool UpscaleInspect { get; set; }

    [ObservableProperty] public partial string? UpscaleStatus { get; set; }

    /// <summary>Wechselt, wenn neue Kacheln in der angezeigten Bitmap stehen (die Ansicht zeichnet dann neu).</summary>
    [ObservableProperty] public partial object? RedrawToken { get; set; }

    /// <summary>Sichtbarer Teil des Bildes in Bildpixeln — setzt die Ansicht.</summary>
    public Func<Rect>? VisibleImageArea { get; set; }

    /// <summary>Rückfrage (modal); Ergebnis: Nummer der Antwort, −1 = Fenster geschlossen.</summary>
    public event Func<ChoiceRequest, Task<int>>? ChoiceRequested;

    public bool UsesAiUpscaler => SelectedUpscaler.Method != UpscaleMethod.Filter;

    /// <summary>KI-Scaler gewählt und es wird vergrößert — nur dann rechnet die KI (Treue, Details, Zeit).</summary>
    public bool AiScalingActive => UsesAiUpscaler && UpscaleEnlarges();

    /// <summary>Ziel größer als das Bild (in einer der Kanten).</summary>
    public bool IsEnlarging => UpscaleEnlarges();

    /// <summary>Ziel anders als das Bild — sonst gibt es nichts zu skalieren.</summary>
    public bool HasSizeChange
    {
        get
        {
            var (w, h) = EffectiveTarget();
            return w != SourceWidth || h != SourceHeight;
        }
    }

    /// <summary>Einträge der Liste „Verfahren“ — fest; je nach Richtung blendet <see cref="SyncScaleChoices"/> aus.</summary>
    public IReadOnlyList<ScaleChoice> ScaleChoices => _scaleChoices ??= CreateScaleChoices();

    private IReadOnlyList<ScaleChoice>? _scaleChoices;

    private IReadOnlyList<ScaleChoice> CreateScaleChoices()
    {
        var list = ScaleChoice.CreateList();
        foreach (var c in list) c.IsOffered = c.OfferedFor(_direction);
        return list;
    }

    /// <summary>Gewählter Eintrag — Filter und KI-Scaler sind getrennt gespeichert (die KI-Wahl bleibt beim Verkleinern erhalten).</summary>
    public ScaleChoice? SelectedScaleChoice
    {
        get
        {
            var list = ScaleChoices;
            if (Direction != ScaleDirection.Down && UsesAiUpscaler && list.FirstOrDefault(c => c.Ai == SelectedUpscaler) is { } ai) return ai;
            return list.FirstOrDefault(c => c.Filter == SelectedFilter && c.IsOffered) ?? list.First(c => c.Filter == FilterOption.Lanczos);
        }
        set
        {
            if (value is null || value.IsHeader)
            {
                Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedScaleChoice)));   // Überschrift: nicht wählbar
                return;
            }
            if (value.Ai is { } ai) SelectedUpscaler = ai;
            else
            {
                SelectedFilter = value.Filter!;
                if (Direction != ScaleDirection.Down) SelectedUpscaler = UpscaleOption.Of(UpscaleMethod.Filter);
            }
        }
    }

    private ScaleDirection _direction = ScaleDirection.Same;

    /// <summary>Kleiner, gleich oder größer als das Bild.</summary>
    public ScaleDirection Direction => IsEnlarging ? ScaleDirection.Up : HasSizeChange ? ScaleDirection.Down : ScaleDirection.Same;

    /// <summary>KI-Scaler gewählt und nicht beim Verkleinern — Treue und Details lassen sich schon vor den Maßen einstellen.</summary>
    public bool AiScalerChosen => UsesAiUpscaler && Direction != ScaleDirection.Down;

    /// <summary>
    /// Nach jeder Größenänderung: Richtung gewechselt → andere Liste; ein Filter, der in der neuen Richtung nicht
    /// angeboten wird (Box beim Vergrößern …), wird zu Lanczos.
    /// </summary>
    private void SyncScaleChoices()
    {
        var direction = Direction;
        if (direction == ScaleDirection.Up && !SelectedFilter.Up || direction == ScaleDirection.Down && !SelectedFilter.Down)
            SelectedFilter = FilterOption.Lanczos;
        if (direction != _direction)
        {
            _direction = direction;
            foreach (var c in ScaleChoices) c.IsOffered = c.OfferedFor(direction);
        }
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(AiScalerChosen));
        OnPropertyChanged(nameof(SelectedScaleChoice));
        OnPropertyChanged(nameof(IsEnlarging));
        OnPropertyChanged(nameof(HasSizeChange));
        OnPropertyChanged(nameof(AiScalingActive));
        OnPropertyChanged(nameof(ScaleButtonText));
        UpscaleCommand.NotifyCanExecuteChanged();
        ResetScaleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>„Skalieren“, bei KI mit der geschätzten Zeit.</summary>
    public string ScaleButtonText => AiScalingActive && MatchingUpscale() is null
        ? Tr.T("Skalieren (≈ {0})", Upscalers.Format(Upscalers.Estimate(SelectedUpscaler.Method, SourceWidth, SourceHeight)))
        : Tr.T("Skalieren");
    public string UpscaleFidelityLabel => $"{UpscaleFidelity * 100:0} %";
    public string UpscaleDetailsLabel => $"{UpscaleDetails * 100:0} %";

    // ------------------------------------------------------------ Modelle

    /// <summary>Das gewählte Modell (mit allen Teilen) liegt nicht vor.</summary>
    public bool UpscaleModelMissing => SelectedUpscaler.Model is { } m && !UpscaleModelInstalled(m);

    /// <summary>Alles da? PiSA-SR braucht den Text-Encoder nur, solange die leere Beschreibung fehlt.</summary>
    private bool UpscaleModelInstalled(AiModel m) =>
        m == AiModelCatalog.PiSaSr
            ? PiSaSrModel.Find(Ai.Store) is not null
            : Ai.Store.IsInstalled(m) && m.Requires.All(id => AiModelCatalog.Find(id) is { } r && Ai.Store.IsInstalled(r));

    private IEnumerable<AiModelItem> UpscaleItems() =>
        SelectedUpscaler.Model is { } m
            ? Ai.Models.Where(i => i.Model == m || m.Requires.Contains(i.Model.Id))
            : [];

    public bool IsDownloadingUpscaler => UpscaleItems().Any(i => i.IsDownloading);

    /// <summary>„Herunterladen (2,6 GB)“ bzw. Fortschritt über alle Teile.</summary>
    public string UpscaleDownloadText
    {
        get
        {
            var items = UpscaleItems().Where(i => !i.IsInstalled || i.IsDownloading).ToList();
            var total = items.Sum(i => i.Model.DownloadSize);
            var size = total >= 1L << 30 ? $"{total / (double)(1L << 30):0.0} GB" : $"{total / (double)(1 << 20):0} MB";
            if (!items.Any(i => i.IsDownloading)) return Tr.T("Herunterladen ({0})", size);
            var done = items.Sum(i => i.Model.DownloadSize * (i.IsInstalled ? 100 : i.Progress)) / Math.Max(1, total);
            return Tr.T("Wird geladen … {0:0} % von {1}", done, size);
        }
    }

    [RelayCommand]
    private async Task DownloadUpscaler()
    {
        // Das Hauptmodell lädt seine Teile mit (Requires)
        if (SelectedUpscaler.Model is not { } m || Ai.Models.FirstOrDefault(i => i.Model == m) is not { } item) return;
        WatchUpscaleItems();
        if (!item.IsInstalled && !item.IsDownloading) await item.DownloadCommand.ExecuteAsync(null);
        else
            foreach (var part in UpscaleItems().Where(i => !i.IsInstalled && !i.IsDownloading).ToList())
                _ = part.DownloadCommand.ExecuteAsync(null);
    }

    private bool _watchingUpscaleItems;

    private void WatchUpscaleItems()
    {
        if (_watchingUpscaleItems) return;
        _watchingUpscaleItems = true;
        foreach (var i in Ai.Models.Where(i => i.Model.Kind is AiModelKind.Upscale or AiModelKind.Part || i.Model == AiModelCatalog.Allround))
            i.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AiModelItem.Progress) or nameof(AiModelItem.IsDownloading) or nameof(AiModelItem.IsInstalled))
                    NotifyUpscaleModels();
            };
        Ai.ModelsChanged += (_, _) => NotifyUpscaleModels();
    }

    private void NotifyUpscaleModels()
    {
        OnPropertyChanged(nameof(UpscaleModelMissing));
        OnPropertyChanged(nameof(IsDownloadingUpscaler));
        OnPropertyChanged(nameof(UpscaleDownloadText));
        UpdateUpscaleStatus();
        UpscaleCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------ Ergebnis

    /// <summary>Wofür ein Ergebnis gilt: Pixelbild (mit/ohne Ausgeblendetes), Ausrichten, Verfahren.</summary>
    private sealed record UpscaleKey(string Path, MagickImage Pixels, GeometryRecipe Recipe, GeometryStages Stages, UpscaleMethod Method)
    {
        public bool Equals(UpscaleKey? o) => o is not null && Path == o.Path && ReferenceEquals(Pixels, o.Pixels) && Recipe == o.Recipe
                                             && Stages == o.Stages && Method == o.Method;

        public override int GetHashCode() => HashCode.Combine(Path, RuntimeHelpers.GetHashCode(Pixels), Stages, Method);
    }

    /// <summary>Das letzte Ergebnis: ganzes Bild oder nur ein Ausschnitt (Region in Bildpixeln).</summary>
    private sealed record UpscaleEntry(UpscaleKey Key, SourceRect? Region, UpscaleResult Result, TimeSpan Took);

    private UpscaleEntry? _upscale;
    private (UpscaleResult Result, double Fidelity, double Details, MagickImage Image)? _upscaledImage;
    private Task? _composeTask;
    private CancellationTokenSource? _upscaleCts;

    private UpscaleKey? CurrentUpscaleKey()
    {
        if (_history is not { } h || CurrentPath is not { } path) return null;
        return new UpscaleKey(path, PixelsFor(path, h, HiddenSteps), h.Recipe, StagesShown(HiddenSteps), SelectedUpscaler.Method);
    }

    /// <summary>Wird gerade vergrößert (Zielgröße größer als das Bild)?</summary>
    private bool UpscaleEnlarges()
    {
        var (w, h) = EffectiveTarget();
        return w > SourceWidth || h > SourceHeight;
    }

    /// <summary>Ergebnis zum jetzigen Stand (ganzes Bild), sonst null.</summary>
    private UpscaleEntry? MatchingUpscale(bool wholeOnly = true) =>
        UsesAiUpscaler && _upscale is { } e && e.Key.Equals(CurrentUpscaleKey()) && (!wholeOnly || e.Region is null) ? e : null;

    /// <summary>
    /// Für Vorschau und Export: das zusammengesetzte Bild ×4, wenn ein passendes Ergebnis da ist und vergrößert wird.
    /// Fehlt die Mischung mit den aktuellen Reglern, wird sie im Hintergrund gerechnet (danach neue Vorschau) — bis dahin
    /// gilt die vorige.
    /// </summary>
    private MagickImage? UpscaledForPreview()
    {
        if (!UpscaleEnlarges() || MatchingUpscale() is not { } e) return null;
        var (f, d) = (UpscaleFidelity, UpscaleDetails);
        if (_upscaledImage is { } c && ReferenceEquals(c.Result, e.Result))
        {
            if (c.Fidelity != f || c.Details != d) RecomposeLater();
            return c.Image;
        }
        RecomposeLater();
        return null;
    }

    /// <summary>Zusammengesetztes Bild mit den aktuellen Reglern (wartet, rechnet notfalls).</summary>
    private async Task<MagickImage?> UpscaledForExportAsync()
    {
        if (!UpscaleEnlarges() || MatchingUpscale() is not { } e) return null;
        var (f, d) = (UpscaleFidelity, UpscaleDetails);
        if (_upscaledImage is { } c && ReferenceEquals(c.Result, e.Result) && c.Fidelity == f && c.Details == d) return c.Image;
        var image = await Task.Run(() => e.Result.Compose(f, d));
        SetUpscaledImage(e.Result, f, d, image);
        return image;
    }

    private void SetUpscaledImage(UpscaleResult result, double f, double d, MagickImage image)
    {
        var old = _upscaledImage?.Image;
        _upscaledImage = (result, f, d, image);
        // Das alte Bild kann noch in einer laufenden Vorschau stecken — der Speicher räumt es mit dem nächsten Lauf weg
        if (old is not null && !ReferenceEquals(old, image)) _upscaleTrash.Add(old);
    }

    private readonly List<MagickImage> _upscaleTrash = [];

    /// <summary>Verdrängte Bilder ×4 freigeben — nur aufrufen, wenn keine Vorschau und kein Export mehr darauf rechnet.</summary>
    private void EmptyUpscaleTrash()
    {
        if (IsExporting) return;
        foreach (var image in _upscaleTrash.Where(i => !ReferenceEquals(i, _upscaledImage?.Image))) image.Dispose();
        _upscaleTrash.Clear();
    }

    /// <summary>Hält Upscaling-Status und -Ansicht beim Bild: Wechsel, Bearbeiten, Größe.</summary>
    private void OnUpscaleRelevantChange(object? sender, PropertyChangedEventArgs e)
    {
        // Jede andere Einstellung (Licht, Farbe, Größe, Verfahren …): wieder die normale Vorschau mit allem
        if (UpscaleInspect && !IsUpscaling && e.PropertyName is { } name && TrackedSettings.ContainsKey(name)
            && name is not (nameof(UpscaleFidelity) or nameof(UpscaleDetails)))
            UpscaleInspect = false;

        switch (e.PropertyName)
        {
            case nameof(CurrentFileName):
                UpscaleImageChanged();
                break;
            case nameof(FullBitmap):
                if (UpscaleInspect && !IsUpscaling && MatchingUpscale(wholeOnly: false) is null) UpscaleInspect = false;
                UpdateUpscaleStatus();
                break;
            case nameof(HasImage):
                UpscaleCommand.NotifyCanExecuteChanged();
                break;
            case nameof(TargetWidth) or nameof(TargetHeight) or nameof(HiddenSteps) or nameof(SourceWidth) or nameof(SourceHeight):
                SyncScaleChoices();
                UpdateUpscaleStatus();
                break;
        }
    }

    private void RecomposeLater()
    {
        if (_composeTask is { IsCompleted: false }) { _recomposeAgain = true; return; }
        _composeTask = RecomposeAsync();
    }

    private bool _recomposeAgain;

    private async Task RecomposeAsync()
    {
        await Task.Delay(250);   // Regler noch in Bewegung
        do
        {
            _recomposeAgain = false;
            if (MatchingUpscale() is not { } e) return;
            var (f, d) = (UpscaleFidelity, UpscaleDetails);
            if (_upscaledImage is { } c && ReferenceEquals(c.Result, e.Result) && c.Fidelity == f && c.Details == d) break;
            var image = await Task.Run(() => e.Result.Compose(f, d));
            SetUpscaledImage(e.Result, f, d, image);
        } while (_recomposeAgain);
        RequestPreview();
    }

    partial void OnSelectedUpscalerChanged(UpscaleOption value)
    {
        WatchUpscaleItems();
        UpdateUpscaleStatus();
        OnPropertyChanged(nameof(SelectedScaleChoice));
        OnPropertyChanged(nameof(AiScalingActive));
        OnPropertyChanged(nameof(AiScalerChosen));
        OnPropertyChanged(nameof(ScaleButtonText));
        UpscaleCommand.NotifyCanExecuteChanged();
        RequestPreview();
    }

    partial void OnSelectedFilterChanged(FilterOption value)
    {
        OnPropertyChanged(nameof(SelectedScaleChoice));
        RequestPreview();
    }

    partial void OnUpscaleFidelityChanged(double value) => UpscaleSlidersChanged();
    partial void OnUpscaleDetailsChanged(double value) => UpscaleSlidersChanged();

    private void UpscaleSlidersChanged()
    {
        if (_upscale is null || _inspect is { Factor: 0 }) return;
        if (UpscaleInspect && !IsUpscaling) RedrawInspectLater();
        RequestPreview();
    }

    partial void OnUpscaleInspectChanged(bool value)
    {
        if (!value && !IsUpscaling) ClearInspect();
        UpdateUpscaleStatus();
    }

    /// <summary>Statuszeile der Gruppe.</summary>
    private void UpdateUpscaleStatus()
    {
        OnPropertyChanged(nameof(ScaleButtonText));
        if (IsUpscaling) return;   // die Rechnung schreibt selbst
        var inspect = UpscaleInspect
            ? Tr.T("Die Vorschau zeigt das Ergebnis in voller Größe, nur skaliert — Licht, Farbe und Schärfe kommen zurück, sobald du etwas anderes einstellst.")
            : null;
        if (!HasSizeChange)
        {
            UpscaleStatus = UsesAiUpscaler
                ? Tr.T("Keine Größenänderung — der KI-Scaler wirkt, sobald die Maße größer sind als das Bild (z. B. 4× im Menü der Formate).")
                : Tr.T("Keine Größenänderung.");
            return;
        }
        if (!AiScalingActive) { UpscaleStatus = inspect; return; }
        if (UpscaleModelMissing) { UpscaleStatus = Tr.T("Modell nicht geladen."); return; }
        var estimate = Upscalers.Format(Upscalers.Estimate(SelectedUpscaler.Method, SourceWidth, SourceHeight));
        if (MatchingUpscale() is { } e)
        {
            UpscaleStatus = Tr.T("Berechnet ({0}) — Vorschau und Speichern verwenden es.", Upscalers.Format(e.Took))
                            + (inspect is null ? "" : "\n" + inspect);
            return;
        }
        if (MatchingUpscale(wholeOnly: false) is not null)
        {
            UpscaleStatus = Tr.T("Nur ein Ausschnitt ist berechnet. Fürs Speichern wird das ganze Bild gerechnet (≈ {0}).", estimate);
            return;
        }
        UpscaleStatus = _upscale is { } old && old.Key.Path == CurrentPath && old.Key.Method == SelectedUpscaler.Method
            ? Tr.T("Das Bild hat sich geändert — neu berechnen (≈ {0}).", estimate)
            : Tr.T("Noch nicht berechnet (≈ {0}). Beim Speichern wird gefragt.", estimate);
    }

    // ------------------------------------------------------------ Rechnen

    private bool CanUpscale => HasImage && !IsUpscaling && HasSizeChange && (!AiScalingActive || !UpscaleModelMissing);

    /// <summary>
    /// „Skalieren“: mit einem Filter in Sekunden, mit einem KI-Scaler (nach Rückfrage bei langer Dauer) in Minuten. Danach
    /// zeigt die Vorschau das Ergebnis in voller Größe — zum Hineinzoomen und für Vorher/Nachher.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpscale))]
    private async Task Upscale()
    {
        if (!AiScalingActive)
        {
            await ScaleWithFilterAsync();
            return;
        }
        if (await Task.Run(() => (ResultDocument, CurrentUpscaleKey())) is not ({ } doc, { } key)) return;
        int w, h;
        lock (doc) (w, h) = ((int)doc.Width, (int)doc.Height);

        // Schon gerechnet (ganzes Bild): nur zeigen
        if (MatchingUpscale() is not null)
        {
            await PrepareInspectAsync(doc, null);
            UpscaleInspect = true;
            ShowInspectBitmaps();
            await RedrawInspectAsync();
            return;
        }

        // Lange Rechnung: fragen — ganzes Bild oder nur der sichtbare Ausschnitt
        SourceRect? region = null;
        var whole = Upscalers.Estimate(key.Method, w, h);
        if (whole > TimeSpan.FromSeconds(45) && ChoiceRequested is { } ask)
        {
            var visible = VisibleRegion(w, h);
            var choices = new List<string> { Tr.T("Ganzes Bild berechnen (≈ {0})", Upscalers.FormatWithEnd(whole, DateTime.Now)) };
            if (visible is { } v)
                choices.Add(Tr.T("Nur den sichtbaren Ausschnitt ({0} × {1} px, ≈ {2})", v.Width, v.Height,
                    Upscalers.FormatWithEnd(Upscalers.Estimate(key.Method, v.Width, v.Height), DateTime.Now)));
            choices.Add(Tr.T("Abbrechen"));
            var answer = await ask(new ChoiceRequest(Tr.T("Upscaling dauert länger"),
                Tr.T("„{0}“ braucht für dieses Bild ({1} × {2} px) auf diesem Rechner etwa {3}. Zum Ausprobieren reicht der Ausschnitt, den du gerade siehst — zoome dazu vorher hinein.",
                    SelectedUpscaler.Name, w, h, Upscalers.Format(whole)),
                choices, visible is null ? 0 : 1));
            if (answer < 0 || answer == choices.Count - 1) return;
            if (answer == 1 && visible is { } r) region = r;
        }
        await RunUpscaleAsync(doc, key, region);
    }

    [RelayCommand(CanExecute = nameof(IsUpscaling))]
    private void CancelUpscale() => _upscaleCts?.Cancel();

    /// <summary>Sichtbarer Ausschnitt, auf ganze Pixel gerundet — null, wenn ohnehin (fast) das ganze Bild zu sehen ist.</summary>
    private SourceRect? VisibleRegion(int w, int h)
    {
        if (VisibleImageArea?.Invoke() is not { Width: > 0 } r || DocumentSize.Width <= 0) return null;
        var k = w / (double)DocumentSize.Width;   // Ansicht rechnet in Bildpixeln des Dokuments
        int x0 = Math.Clamp((int)Math.Floor(r.X * k), 0, w - 1), y0 = Math.Clamp((int)Math.Floor(r.Y * k), 0, h - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(r.Right * k), x0 + 1, w), y1 = Math.Clamp((int)Math.Ceiling(r.Bottom * k), y0 + 1, h);
        if ((x1 - x0) * (double)(y1 - y0) > 0.8 * w * h) return null;
        return new SourceRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Rechnet das Upscaling (ganzes Bild oder Ausschnitt), zeigt es live in der Prüfansicht. true = fertig.</summary>
    private async Task<bool> RunUpscaleAsync(MagickImage doc, UpscaleKey key, SourceRect? region)
    {
        if (SelectedUpscaler.Model is null) return false;
        var method = key.Method;
        var cts = _upscaleCts = new CancellationTokenSource();
        IsUpscaling = true;
        ErrorMessage = null;
        var clock = Stopwatch.StartNew();
        var preparing = false;
        int dw, dh;
        lock (doc) (dw, dh) = ((int)doc.Width, (int)doc.Height);
        var expected = region is { } part ? Upscalers.Estimate(key.Method, part.Width, part.Height) : Upscalers.Estimate(key.Method, dw, dh);
        void Report(double f)
        {
            var remaining = RemainingText(clock.Elapsed, f, expected);
            ActivityText = preparing
                ? Tr.T("Upscaling · KI-Modell wird vorbereitet (einmalig) … {0:0} %", f * 100)
                : Tr.T("Upscaling · {0:0} %{1}", f * 100, remaining);
            ActivityProgress = f * 100;
            if (!preparing) UpscaleStatus = Tr.T("Wird berechnet …") + remaining;
        }
        var progress = new Progress<double>(Report);
        ActivityCancel = CancelUpscaleCommand;
        Report(0);

        // Prüfansicht vorbereiten: Vorher = Filter, Nachher füllt sich mit fertigen Kacheln
        int w, h;
        lock (doc) (w, h) = ((int)doc.Width, (int)doc.Height);
        var (f0, d0) = (UpscaleFidelity, UpscaleDetails);
        await PrepareInspectAsync(doc, region);
        UpscaleInspect = true;
        var view = _inspect;

        try
        {
            var result = await Task.Run(() =>
            {
                using var input = region is { } r ? CropOf(doc, r) : null;
                var source = input ?? doc;
                void Done(UpscaleResult res, SourceRect rect)
                {
                    if (view is null) return;
                    var (ox, oy) = region is { } rr ? (rr.X, rr.Y) : (0, 0);
                    BlitTile(view, res, rect, ox, oy, f0, d0, live: true);
                }

                return ComputeUpscale(source, method, progress, prepare =>
                {
                    preparing = prepare;
                    if (prepare) Dispatcher.UIThread.Post(() => Report(0));
                    else clock.Restart();
                }, cts.Token, Done);
            }, cts.Token);

            var took = clock.Elapsed;
            Ai.RefreshGpuStatus();
            Upscalers.Measured(method, result.SourceWidth, result.SourceHeight, took);
            _upscale = new UpscaleEntry(key, region, result, took);
            _upscaledImage = null;
            if (region is null)
            {
                var image = await Task.Run(() => result.Compose(UpscaleFidelity, UpscaleDetails));
                SetUpscaledImage(result, UpscaleFidelity, UpscaleDetails, image);
            }
            // Ansicht einmal ganz neu: an den Kachelrändern fehlten beim Rechnen die Nachbarn (Treue), und die Regler können
            // inzwischen anders stehen
            RedrawInspectLater();
            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
            StatusMessage = Tr.T("Upscaling abgebrochen.");
            if (_upscale is null || !_upscale.Key.Equals(key)) ClearInspect();
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Upscaling fehlgeschlagen: {0}", ex.Message);
            ClearInspect();
            return false;
        }
        finally
        {
            IsUpscaling = false;
            ActivityText = null;
            ActivityProgress = -1;
            ActivityCancel = null;
            UpdateUpscaleStatus();
            RequestPreview();
        }
    }

    /// <summary>
    /// Das Modell rechnen lassen (im Rechen-Thread). PiSA-SR baut beim ersten Mal seine ONNX-Dateien —
    /// <paramref name="preparing"/> meldet Anfang (true) und Ende (false) davon; der Fortschritt beginnt danach neu.
    /// </summary>
    private UpscaleResult ComputeUpscale(MagickImage source, UpscaleMethod method, IProgress<double> progress, Action<bool>? preparing,
        CancellationToken ct, Action<UpscaleResult, SourceRect>? done = null)
    {
        var model = Upscalers.ModelOf(method) ?? throw new ArgumentException("Filter rechnet nicht.", nameof(method));
        if (method == UpscaleMethod.Generative)
        {
            var sources = PiSaSrModel.Find(Ai.Store) ?? throw new InvalidOperationException(Tr.T("Modell nicht geladen."));
            // Grafikkarte: halbe Genauigkeit (halber Speicher, schneller, Ergebnis gleich); CPU: volle — sie rechnet fp16 nicht schneller
            var half = Ai.Sessions.OnGpu;
            var build = PiSaSrModel.NeedsBuild(sources, half);
            if (build) preparing?.Invoke(true);
            var (enc, unet, dec) = PiSaSrModel.OnnxFor(sources, progress, ct, half);
            if (build) preparing?.Invoke(false);
            var sessions = (Ai.Sessions.Get(model, enc), Ai.Sessions.Get(model, unet), Ai.Sessions.Get(model, dec));
            if (half && !Ai.Sessions.OnGpu)
            {
                // Grafikkarte ließ sich doch nicht öffnen: die Dateien in voller Genauigkeit für die CPU
                if (PiSaSrModel.NeedsBuild(sources)) preparing?.Invoke(true);
                (enc, unet, dec) = PiSaSrModel.OnnxFor(sources, progress, ct);
                preparing?.Invoke(false);
                sessions = (Ai.Sessions.Get(model, enc), Ai.Sessions.Get(model, unet), Ai.Sessions.Get(model, dec));
            }
            return PiSaSrRun.Run(source, sessions.Item1, sessions.Item2, sessions.Item3, progress, ct, done);
        }
        var path = Ai.Store.PathOf(model) ?? throw new InvalidOperationException(Tr.T("Modell nicht geladen."));
        return UpscaleEngine.RunTiles(source, new OnnxTileModel(Ai.Sessions.Get(model, path), model), progress, ct, done);
    }

    private static MagickImage CropOf(MagickImage doc, SourceRect r)
    {
        MagickImage clone;
        lock (doc) clone = (MagickImage)doc.Clone();
        clone.Crop(new MagickGeometry(r.X, r.Y, (uint)r.Width, (uint)r.Height));
        clone.ResetPage();
        return clone;
    }

    /// <summary>
    /// „  ·  noch ca. 4 min, fertig gegen 15:42“: aus dem Fortschritt, ganz am Anfang (noch zu wenig gerechnet) aus der
    /// Schätzung <paramref name="expected"/>.
    /// </summary>
    private static string RemainingText(TimeSpan elapsed, double fraction, TimeSpan? expected = null)
    {
        TimeSpan left;
        if (fraction >= 0.03 && elapsed.TotalSeconds >= 3)
            left = TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction * (1 - fraction));
        else if (expected is { } e && e > elapsed)
            left = e - elapsed;
        else
            return "";
        return Tr.T("  ·  noch ca. {0}", Upscalers.FormatWithEnd(left, DateTime.Now));
    }

    // ------------------------------------------------------------ Prüfansicht

    /// <summary>Anzeige in <c>Factor</c>-facher Größe des Bildes (1, 2 oder 4 — so viel, wie die Zielgröße braucht).</summary>
    /// <param name="Factor">Anzeige je Bildpixel (KI: 1, 2 oder 4); 0 beim Filter (Bitmap in Zielgröße).</param>
    /// <param name="OwnsBefore">false: Vorher gehört der Anzeige (Zwischenspeicher), nicht freigeben.</param>
    private sealed record InspectView(WriteableBitmap After, Bitmap Before, int Factor, int Width, int Height, bool OwnsBefore = true);

    private InspectView? _inspect;

    /// <summary>Wie groß die Prüfansicht wird: so fein wie das Ziel, höchstens ×4 und rund 64 Megapixel.</summary>
    private int InspectFactor(int w, int h)
    {
        var (tw, th) = EffectiveTarget();
        var need = Math.Max(tw / (double)w, th / (double)h);
        var m = need <= 1 ? 1 : need <= 2 ? 2 : 4;
        while (m > 1 && (long)w * m * h * m > 64_000_000) m /= 2;
        return m;
    }

    /// <summary>Vorher (Filter) und Nachher (zunächst blass der Filter) in Anzeigegröße anlegen.</summary>
    private async Task PrepareInspectAsync(MagickImage doc, SourceRect? region)
    {
        int w, h;
        lock (doc) (w, h) = ((int)doc.Width, (int)doc.Height);
        var m = InspectFactor(w, h);
        var (filter, gamma) = (SelectedFilter.Filter, GammaCorrect);
        var before = await Task.Run(() =>
        {
            using var big = ImageOps.Resize(doc, w * m, h * m, filter, gamma);
            return ImageOps.ToDisplayBgra(big);
        });
        // Nachher: blass, bis die Kacheln kommen — beim Ausschnitt nur dort, der Rest bleibt wie Vorher
        var after = (byte[])before.Clone();
        var (rx0, ry0, rx1, ry1) = region is { } r ? (r.X * m, r.Y * m, (r.X + r.Width) * m, (r.Y + r.Height) * m) : (0, 0, w * m, h * m);
        Parallel.For(ry0, ry1, y =>
        {
            for (var x = rx0; x < rx1; x++)
            {
                var i = ((long)y * w * m + x) * 4;
                for (var c = 0; c < 3; c++) after[i + c] = (byte)(after[i + c] * 0.6 + 102);
            }
        });
        ClearInspect();
        _inspect = new InspectView((WriteableBitmap)BitmapOps.FromBgra(after, w * m, h * m), BitmapOps.FromBgra(before, w * m, h * m), m, w, h);
        if (UpscaleInspect || IsUpscaling) ShowInspectBitmaps();
    }

    private void ShowInspectBitmaps()
    {
        if (_inspect is not { } v) return;
        OverrideBitmap = v.After;
        OverrideBeforeBitmap = v.Before;
        RedrawToken = new object();
    }

    private void ClearInspect()
    {
        if (_inspect is { } v)
        {
            if (ReferenceEquals(OverrideBitmap, v.After)) OverrideBitmap = null;
            if (ReferenceEquals(OverrideBeforeBitmap, v.Before)) OverrideBeforeBitmap = null;
            v.After.Dispose();
            if (v.OwnsBefore) v.Before.Dispose();
        }
        _inspect = null;
    }

    /// <summary>
    /// Skalieren mit dem gewählten Filter, in voller Zielgröße (höchstens rund 64 Megapixel in der Anzeige). Vorher ist das
    /// Bild selbst — die Ansicht legt beide auf dieselbe Fläche.
    /// </summary>
    private async Task ScaleWithFilterAsync()
    {
        if (await Task.Run(() => ResultDocument) is not { } doc) return;
        var (tw, th) = EffectiveTarget();
        var k = Math.Min(1.0, Math.Sqrt(64_000_000.0 / ((long)tw * th)));
        int dw = Math.Max(1, (int)Math.Round(tw * k)), dh = Math.Max(1, (int)Math.Round(th * k));
        var (filter, gamma) = (SelectedFilter.Filter, GammaCorrect);
        ActivityText = Tr.T("Skalieren …");
        ActivityProgress = -1;
        try
        {
            var bgra = await Task.Run(() =>
            {
                using var scaled = ImageOps.Resize(doc, dw, dh, filter, gamma);
                return ImageOps.ToDisplayBgra(scaled);
            });
            var before = await DisplayBitmapForAsync(doc);
            ClearInspect();
            _inspect = new InspectView((WriteableBitmap)BitmapOps.FromBgra(bgra, dw, dh), before, 0, (int)doc.Width, (int)doc.Height, OwnsBefore: false);
            UpscaleInspect = true;
            ShowInspectBitmaps();
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Skalieren fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            ActivityText = null;
            ActivityProgress = -1;
        }
    }

    private CancellationTokenSource? _redrawCts;

    private void RedrawInspectLater() => _ = RedrawInspectAsync(delay: 200);

    /// <summary>Ganze Prüfansicht mit den aktuellen Reglern neu zusammensetzen (Kachel für Kachel, abbrechbar).</summary>
    private async Task RedrawInspectAsync(int delay = 0)
    {
        _redrawCts?.Cancel();
        var cts = _redrawCts = new CancellationTokenSource();
        if (delay > 0)
        {
            try { await Task.Delay(delay, cts.Token); } catch (OperationCanceledException) { return; }
        }
        if (_inspect is not { Factor: > 0 } view || MatchingUpscale(wholeOnly: false) is not { } e) return;
        var (f, d) = (UpscaleFidelity, UpscaleDetails);
        var (ox, oy) = e.Region is { } r ? (r.X, r.Y) : (0, 0);
        try
        {
            await Task.Run(() =>
            {
                const int step = 256;
                for (var y = 0; y < e.Result.SourceHeight; y += step)
                for (var x = 0; x < e.Result.SourceWidth; x += step)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    BlitTile(view, e.Result, new SourceRect(x, y, Math.Min(step, e.Result.SourceWidth - x), Math.Min(step, e.Result.SourceHeight - y)),
                        ox, oy, f, d);
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // neuere Werte rechnen schon
        }
    }

    /// <summary>
    /// Eine fertige Fläche zusammensetzen (Treue, Details), auf die Anzeigegröße mitteln und in die Prüfansicht schreiben.
    /// Läuft im Rechen-Thread; nur das Kopieren in die Bitmap geschieht im UI-Thread.
    /// </summary>
    /// <param name="live">Während der Rechnung: Nachbarn sind noch leer — Treue nur innerhalb der fertigen Fläche.</param>
    private void BlitTile(InspectView view, UpscaleResult result, SourceRect rect, int ox, int oy, double f, double d, bool live = false)
    {
        var px = result.ComposeRegion(rect, f, d, known: live ? rect : null);
        var ch = result.HasAlpha ? 4 : 3;
        var m = view.Factor;
        var k = UpscaleResult.Factor / m;
        int bw = rect.Width * m, bh = rect.Height * m, rw = rect.Width * UpscaleResult.Factor;
        var bgra = new byte[bw * bh * 4];
        var norm = 1f / (k * k * 257f);
        Parallel.For(0, bh, y =>
        {
            Span<float> sum = stackalloc float[4];
            for (var x = 0; x < bw; x++)
            {
                sum.Clear();
                for (var by = 0; by < k; by++)
                for (var bx = 0; bx < k; bx++)
                {
                    var i = ((long)(y * k + by) * rw + x * k + bx) * ch;
                    for (var c = 0; c < ch; c++) sum[c] += px[i + c];
                }
                var o = (y * bw + x) * 4;
                bgra[o] = (byte)Math.Min(255, sum[2] * norm + 0.5f);
                bgra[o + 1] = (byte)Math.Min(255, sum[1] * norm + 0.5f);
                bgra[o + 2] = (byte)Math.Min(255, sum[0] * norm + 0.5f);
                bgra[o + 3] = ch == 4 ? (byte)Math.Min(255, sum[3] * norm + 0.5f) : (byte)255;
            }
        });
        int dx = (ox + rect.X) * m, dy = (oy + rect.Y) * m;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_inspect, view)) return;
            using (var fb = view.After.Lock())
            {
                for (var y = 0; y < bh; y++)
                    Marshal.Copy(bgra, y * bw * 4, fb.Address + (dy + y) * fb.RowBytes + dx * 4, bw * 4);
            }
            RedrawToken = new object();
        });
    }

    // ------------------------------------------------------------ Speichern

    /// <summary>
    /// Vor dem Speichern: Upscaler gewählt und vergrößert, aber nicht (ganz) berechnet → fragen. Liefert das Bild ×4 für
    /// den Export, null = nur Filter; <c>Cancel</c> = nicht speichern.
    /// </summary>
    private async Task<(bool Cancel, MagickImage? Upscaled)> UpscaleBeforeExportAsync()
    {
        if (!UsesAiUpscaler || !UpscaleEnlarges()) return (false, null);
        if (MatchingUpscale() is not null) return (false, await UpscaledForExportAsync());
        if (UpscaleModelMissing || ChoiceRequested is not { } ask) return (false, null);

        var estimate = Upscalers.FormatWithEnd(Upscalers.Estimate(SelectedUpscaler.Method, SourceWidth, SourceHeight), DateTime.Now);
        var answer = await ask(new ChoiceRequest(Tr.T("Upscaling noch nicht berechnet"),
            Tr.T("Für dieses Bild ist „{0}“ gewählt, aber noch nicht berechnet. Das dauert etwa {1}.", SelectedUpscaler.Name, estimate),
            [Tr.T("Jetzt berechnen und speichern (≈ {0})", estimate), Tr.T("Ohne KI speichern (nur Filter)"), Tr.T("Abbrechen")]));
        if (answer == 1) return (false, null);
        if (answer != 0) return (true, null);
        if (await Task.Run(() => (ResultDocument, CurrentUpscaleKey())) is not ({ } doc, { } key)) return (true, null);
        if (!await RunUpscaleAsync(doc, key, null)) return (true, null);
        return (false, await UpscaledForExportAsync());
    }

    // ------------------------------------------------------------ Export mehrerer Bilder

    /// <summary>Muss für diese Datei mit ihrem Stand noch gerechnet werden? Dann wie lange ungefähr (sonst null).</summary>
    internal TimeSpan? UpscaleNeededFor(string path, int width, int height, SettingsState s, ExportOptions o)
    {
        if (s.Upscaler == UpscaleMethod.Filter || (o.Width <= width && o.Height <= height)) return null;
        if (Upscalers.ModelOf(s.Upscaler) is not { } m || !UpscaleModelInstalled(m)) return null;   // ohne Modell: Filter
        if (path == CurrentPath && SelectedUpscaler.Method == s.Upscaler && MatchingUpscale() is not null) return null;
        return Upscalers.Estimate(s.Upscaler, width, height);
    }

    /// <summary>
    /// Beim Export einer Datei: Bild ×4 mit ihrem Stand — das berechnete des offenen Bildes oder neu gerechnet (ohne Ansicht).
    /// <c>Owned</c> = der Aufrufer gibt es frei. null = nur Filter.
    /// </summary>
    internal async Task<(MagickImage Image, bool Owned)?> UpscaleForBatchAsync(string path, MagickImage image, SettingsState s, ExportOptions o,
        IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        int w, h;
        lock (image) (w, h) = ((int)image.Width, (int)image.Height);
        if (s.Upscaler == UpscaleMethod.Filter || (o.Width <= w && o.Height <= h)) return null;
        if (path == CurrentPath && SelectedUpscaler.Method == s.Upscaler && MatchingUpscale() is not null
            && await UpscaledForExportAsync() is { } ready)
            return (ready, false);
        if (Upscalers.ModelOf(s.Upscaler) is not { } m || !UpscaleModelInstalled(m)) return null;

        var clock = Stopwatch.StartNew();
        var step = new Progress<double>(f => progress?.Report(new WorkProgress(f,
            Tr.T("Upscaling (KI) · {0:0} %{1}", f * 100, RemainingText(clock.Elapsed, f, Upscalers.Estimate(s.Upscaler, w, h))))));
        var composed = await Task.Run(() =>
        {
            var result = ComputeUpscale(image, s.Upscaler, step, prepare => { if (!prepare) clock.Restart(); }, ct);
            Upscalers.Measured(s.Upscaler, w, h, clock.Elapsed);
            return result.Compose(s.UpscaleFidelity, s.UpscaleDetails, ct);
        }, ct);
        return (composed, true);
    }

    /// <summary>Grafikkarte an/aus: andere Zeitschätzung, Ergebnis bleibt gültig.</summary>
    internal void GpuChanged()
    {
        OnPropertyChanged(nameof(ScaleButtonText));
        UpdateUpscaleStatus();
    }

    /// <summary>Bild gewechselt oder geschlossen: Ansicht weg; das Ergebnis bleibt (gilt nur für sein Bild).</summary>
    private void UpscaleImageChanged()
    {
        if (IsUpscaling) _upscaleCts?.Cancel();
        UpscaleInspect = false;
        ClearInspect();
        UpdateUpscaleStatus();
    }
}
