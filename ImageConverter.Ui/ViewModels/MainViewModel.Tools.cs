using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;

namespace ImageConverter.ViewModels;

// Kachel „Tools“: Licht und Farbe, je Funktion ein Regler von −100 bis +100
// (Farbton beim Färben 0–360°).
// Wirkt in der Kette nach dem Entrauschen und vor dem Schärfen (Core: Adjust).
public partial class MainViewModel
{
    [ObservableProperty] public partial double Dehaze { get; set; }

    // Dunst, Feineinstellung — Vorgaben siehe Adjustments
    [ObservableProperty] public partial double DehazePasses { get; set; } = Adjustments.None.DehazePasses;
    [ObservableProperty] public partial double DehazeClarity { get; set; } = Adjustments.None.DehazeClarity;
    [ObservableProperty] public partial double DehazeContrast { get; set; } = Adjustments.None.DehazeContrast;
    [ObservableProperty] public partial double DehazeVibrance { get; set; } = Adjustments.None.DehazeVibrance;
    [ObservableProperty] public partial double DehazeSharpen { get; set; } = Adjustments.None.DehazeSharpen;
    [ObservableProperty] public partial double DehazeEdge { get; set; }
    [ObservableProperty] public partial double DehazeBrightness { get; set; } = Adjustments.None.DehazeBrightness;
    [ObservableProperty] public partial double DehazeDeband { get; set; } = Adjustments.None.DehazeDeband;
    [ObservableProperty] public partial double DehazeCast { get; set; } = Adjustments.None.DehazeCast;

    /// <summary>Die Feineinstellung erscheint nur, wenn Dunst entfernt wird.</summary>
    public bool HasDehaze => Math.Round(Dehaze) > 0;

    [ObservableProperty] public partial double Brightness { get; set; }
    [ObservableProperty] public partial double Contrast { get; set; }
    [ObservableProperty] public partial double Shadows { get; set; }
    [ObservableProperty] public partial double Highlights { get; set; }
    /// <summary>„Gleiche Farbe“ wie Referenzwerkzeug CS5: Luminanz −100 … +100 (Referenzwerkzeug 1–200 %).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLimitLuminance))]
    public partial double MatchLuminance { get; set; }

    /// <summary>„Gleiche Farbe“: Schwellwert für Luminanz — bis zu dieser Helligkeit (%) wird aufgehellt, Helleres bleibt; 100 = keine Grenze.</summary>
    [ObservableProperty] public partial double MatchLimit { get; set; } = 100;


    /// <summary>„Gleiche Farbe“: Zielwert 0–90 % — dunkle Bereiche nur bis zu dieser Helligkeit anheben; 0 = aus.</summary>
    [ObservableProperty] public partial double MatchTarget { get; set; }

    /// <summary>„Gleiche Farbe“: Luminanz für das ganze Bild, nach der Bereichs-Luminanz — −100 … +100 (0–200 %).</summary>
    [ObservableProperty] public partial double MatchGlobal { get; set; }

    // ------------------------------------------------------------ Luminanzmaske (eigene Gruppe)

    /// <summary>Luminanzmaske: wie viel mehr Licht das Dunkelste bekommt, 0–200 %.</summary>
    [ObservableProperty] public partial double MaskAmount { get; set; }

    /// <summary>Luminanzmaske: ab welcher Helligkeit (%) nichts mehr passiert.</summary>
    [ObservableProperty] public partial double MaskEnd { get; set; } = 75;

    /// <summary>Luminanzmaske: Verlauf 0 (weich, Mitteltöne bekommen mit) … 100 (steil, nur das Dunkelste).</summary>
    [ObservableProperty] public partial double MaskCurve { get; set; } = 30;

    /// <summary>Maske als Graustufen in der Vorschau zeigen (nur Ansicht: kein Rückgängig-Schritt, nie im Export).</summary>
    [ObservableProperty] public partial bool ShowMask { get; set; }

    public string MaskAmountLabel => MaskAmount < 0.5 ? Tr.T("aus") : $"+{Math.Round(MaskAmount):0} %";
    public string MaskEndLabel => $"{Math.Round(MaskEnd):0} %";
    public string MaskCurveLabel => MaskCurve < 20 ? Tr.T("weich") : MaskCurve > 70 ? Tr.T("steil") : $"{Math.Round(MaskCurve):0}";

    partial void OnMaskAmountChanged(double value) => ToolChanged(nameof(MaskAmountLabel));
    partial void OnMaskEndChanged(double value) => ToolChanged(nameof(MaskEndLabel));
    partial void OnMaskCurveChanged(double value) => ToolChanged(nameof(MaskCurveLabel));
    partial void OnShowMaskChanged(bool value) => RequestPreview();

    // ------------------------------------------------------------ Körnung (eigene Gruppe, immer der letzte Schritt)

    /// <summary>Körnung: Stärke 0–100 % (Streuung im Mittelton).</summary>
    [ObservableProperty] public partial double GrainAmount { get; set; }

    /// <summary>Körnung: Korngröße 0 (fein, Pixel für Pixel) … 100 (grob), in Pixeln der Ausgabe.</summary>
    [ObservableProperty] public partial double GrainSize { get; set; } = GrainSettings.None.Size;

    /// <summary>Körnung: Rauheit 0 (gleichmäßig) … 100 (klumpig wie Film).</summary>
    [ObservableProperty] public partial double GrainRoughness { get; set; } = GrainSettings.None.Roughness;

    /// <summary>Körnung: Farbanteil 0 (nur Helligkeit) … 100 %.</summary>
    [ObservableProperty] public partial double GrainColor { get; set; }

    /// <summary>„Aus dem Foto ermitteln“ läuft.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureGrainCommand))]
    public partial bool IsMeasuringGrain { get; set; }

    public string GrainAmountLabel => GrainAmount < 0.5 ? Tr.T("aus") : $"{Math.Round(GrainAmount):0} %";
    public string GrainSizeLabel => $"{GrainSettings.SigmaFor(Math.Round(GrainSize)):0.0} px";
    public string GrainRoughnessLabel => GrainRoughness < 0.5 ? Tr.T("gleichmäßig") : $"{Math.Round(GrainRoughness):0}";
    public string GrainColorLabel => GrainColor < 0.5 ? Tr.T("aus") : $"{Math.Round(GrainColor):0} %";

    partial void OnGrainAmountChanged(double value) => ToolChanged(nameof(GrainAmountLabel));
    partial void OnGrainSizeChanged(double value) => ToolChanged(nameof(GrainSizeLabel));
    partial void OnGrainRoughnessChanged(double value) => ToolChanged(nameof(GrainRoughnessLabel));
    partial void OnGrainColorChanged(double value) => ToolChanged(nameof(GrainColorLabel));

    /// <summary>Körnung für Vorschau und Export; null = aus (oder in der Liste ausgeschaltet).</summary>
    private GrainSettings? EffectiveGrain => Shows(EditSteps.Grain) && Round(GrainAmount) >= 1
        ? new GrainSettings(Round(GrainAmount), Round(GrainSize), Round(GrainRoughness), Round(GrainColor))
        : null;

    private bool CanMeasureGrain => !IsMeasuringGrain;

    /// <summary>
    /// Korn des Fotos messen und die Regler danach stellen — gemessen am Bild vor der KI und ohne Einstellungen (also
    /// vor dem Entrauschen), umgerechnet auf die Ausgabegröße. Ein Rückgängig-Schritt; danach von Hand nachbessern.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMeasureGrain))]
    private async Task MeasureGrain()
    {
        if (_history is not { } h || IsEditing) return;
        var photo = CurrentWithoutAi ?? h.Current;
        var (tw, th) = EffectiveTarget();
        var scale = Math.Sqrt((double)tw * th / Math.Max(1.0, (double)SourceWidth * SourceHeight));

        IsMeasuringGrain = true;
        ActivityText = Tr.T("Korn wird gemessen …");
        ActivityProgress = -1;
        try
        {
            var measured = await Task.Run(() =>
            {
                int w, ht;
                lock (photo) (w, ht) = ((int)photo.Width, (int)photo.Height);
                var px = Pixels.FromMagick(photo);
                try { return Grain.Measure(px, w, ht, scale); }
                finally { PixelPool.Return(px); }
            });
            if (measured is null || measured.IsNone)
            {
                StatusMessage = Tr.T("Im Foto ist kein Korn messbar – es ist schon ganz glatt.");
                return;
            }

            CommitPendingSettings();   // ein eigener Rückgängig-Schritt
            GrainAmount = measured.Amount;
            GrainSize = measured.Size;
            GrainRoughness = measured.Roughness;
            GrainColor = measured.Color;
            CommitPendingSettings();
            StatusMessage = Tr.T("Korn aus dem Foto übernommen – Regler lassen sich nachbessern.");
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Korn messen fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsMeasuringGrain = false;
            ActivityText = null;
        }
    }


    /// <summary>Der Schwellwert wirkt nur beim Aufhellen.</summary>
    public bool CanLimitLuminance => Math.Round(MatchLuminance) > 0;

    /// <summary>„Gleiche Farbe“: Ausgleichen — wie viel vom erkannten Farbstich entfernt wird, 0–100 %.</summary>
    [ObservableProperty] public partial double Neutralize { get; set; }

    /// <summary>„Gleiche Farbe“: Farbe — kühl (−) … neutral (0) … warm (+).</summary>
    [ObservableProperty] public partial double Warmth { get; set; }

    [ObservableProperty] public partial double Vibrance { get; set; }
    [ObservableProperty] public partial double Saturation { get; set; }
    [ObservableProperty] public partial string SelectedTint { get; set; } = TintLabels[0].Label;

    /// <summary>Farbton beim Färben „Farbe“, 0–360°.</summary>
    [ObservableProperty] public partial double TintHue { get; set; } = Adjustments.SepiaHue;

    /// <summary>Der Farbton-Regler erscheint nur, wenn farbig getönt wird.</summary>
    public bool IsTintColor => TintOf(SelectedTint) == Tint.Color;

    internal static readonly (string Label, Tint Tint)[] TintLabels =
    [
        ("Aus", Tint.None),
        ("Schwarzweiß", Tint.BlackWhite),
        ("Farbe", Tint.Color),
    ];

    public IReadOnlyList<string> Tints { get; } = TintLabels.Select(t => t.Label).ToList();

    public string DehazeLabel => Signed(Dehaze);
    public string DehazePassesLabel => $"{Math.Round(DehazePasses):0}×";
    public string DehazeClarityLabel => Percent(DehazeClarity);
    public string DehazeContrastLabel => Percent(DehazeContrast);
    public string DehazeVibranceLabel => Percent(DehazeVibrance);
    public string DehazeSharpenLabel => Percent(DehazeSharpen);
    public string DehazeEdgeLabel => Percent(DehazeEdge);
    public string DehazeBrightnessLabel => Percent(DehazeBrightness);
    public string DehazeDebandLabel => Percent(DehazeDeband);
    public string DehazeCastLabel => Percent(DehazeCast);
    public string BrightnessLabel => Signed(Brightness);

    private static string Percent(double v) => v < 0.5 ? Tr.T("aus") : $"{Math.Round(v):0} %";
    public string ContrastLabel => Signed(Contrast);
    public string ShadowsLabel => Signed(Shadows);
    public string HighlightsLabel => Signed(Highlights);
    public string MatchLuminanceLabel => $"{Math.Round(100 + MatchLuminance):0} %";
    public string MatchLimitLabel => MatchLimit >= 99.5 ? Tr.T("aus") : $"{Math.Round(MatchLimit):0} %";
    public string MatchTargetLabel => Percent(MatchTarget);
    public string MatchGlobalLabel => $"{Math.Round(100 + MatchGlobal):0} %";
    public string NeutralizeLabel => Neutralize < 0.5 ? Tr.T("aus") : $"{Math.Round(Neutralize):0} %";
    public string WarmthLabel => Signed(Warmth);
    public string VibranceLabel => Signed(Vibrance);
    public string SaturationLabel => Signed(Saturation);
    public string TintHueLabel => $"{Math.Round(TintHue):0}°";

    private static string Signed(double v) => (int)Math.Round(v) switch
    {
        0 => "0",
        > 0 and var n => $"+{n}",
        var n => $"{n}",
    };

    partial void OnDehazeChanged(double value)
    {
        OnPropertyChanged(nameof(HasDehaze));
        ToolChanged(nameof(DehazeLabel));
    }

    partial void OnDehazePassesChanged(double value) => ToolChanged(nameof(DehazePassesLabel));
    partial void OnDehazeClarityChanged(double value) => ToolChanged(nameof(DehazeClarityLabel));
    partial void OnDehazeContrastChanged(double value) => ToolChanged(nameof(DehazeContrastLabel));
    partial void OnDehazeVibranceChanged(double value) => ToolChanged(nameof(DehazeVibranceLabel));
    partial void OnDehazeSharpenChanged(double value) => ToolChanged(nameof(DehazeSharpenLabel));
    partial void OnDehazeEdgeChanged(double value) => ToolChanged(nameof(DehazeEdgeLabel));
    partial void OnDehazeBrightnessChanged(double value) => ToolChanged(nameof(DehazeBrightnessLabel));
    partial void OnDehazeDebandChanged(double value) => ToolChanged(nameof(DehazeDebandLabel));
    partial void OnDehazeCastChanged(double value) => ToolChanged(nameof(DehazeCastLabel));
    partial void OnBrightnessChanged(double value) => ToolChanged(nameof(BrightnessLabel));
    partial void OnContrastChanged(double value) => ToolChanged(nameof(ContrastLabel));
    partial void OnShadowsChanged(double value) => ToolChanged(nameof(ShadowsLabel));
    partial void OnHighlightsChanged(double value) => ToolChanged(nameof(HighlightsLabel));
    partial void OnMatchLuminanceChanged(double value) => ToolChanged(nameof(MatchLuminanceLabel));
    partial void OnMatchLimitChanged(double value) => ToolChanged(nameof(MatchLimitLabel));
    partial void OnMatchTargetChanged(double value) => ToolChanged(nameof(MatchTargetLabel));
    partial void OnMatchGlobalChanged(double value) => ToolChanged(nameof(MatchGlobalLabel));
    partial void OnNeutralizeChanged(double value) => ToolChanged(nameof(NeutralizeLabel));
    partial void OnWarmthChanged(double value) => ToolChanged(nameof(WarmthLabel));
    partial void OnVibranceChanged(double value) => ToolChanged(nameof(VibranceLabel));
    partial void OnSaturationChanged(double value) => ToolChanged(nameof(SaturationLabel));
    partial void OnTintHueChanged(double value) => ToolChanged(nameof(TintHueLabel));

    partial void OnSelectedTintChanged(string value)
    {
        OnPropertyChanged(nameof(IsTintColor));
        ToolChanged(null);
    }

    private void ToolChanged(string? label)
    {
        if (label is not null) OnPropertyChanged(label);
        RequestPreview();
    }

    /// <summary>Alles in dieser Kachel zurück — ein Rückgängig-Schritt.</summary>
    [RelayCommand]
    private void ResetTools()
    {
        CommitPendingSettings();   // vorige Reglerbewegung bleibt ein eigener Schritt
        Dehaze = Brightness = Contrast = Shadows = Highlights = MatchLuminance = Neutralize = Warmth = Vibrance = Saturation = 0;
        MatchLimit = 100;
        MatchTarget = 0;
        MatchGlobal = 0;
        MaskAmount = 0;
        MaskEnd = 75;
        MaskCurve = 30;
        GrainAmount = GrainColor = 0;
        GrainSize = GrainSettings.None.Size;
        GrainRoughness = GrainSettings.None.Roughness;
        NoiseReduction = ColorNoiseReduction = 0;   // Rauschen steht seit 0.5.26 auch in den Tools
        SelectedTint = TintLabels[0].Label;
        TintHue = Adjustments.SepiaHue;
        ResetDehazeDetails();
    }

    /// <summary>Nur die Dunst-Feineinstellung auf die Vorgaben — ein Rückgängig-Schritt.</summary>
    [RelayCommand]
    private void ResetDehazeDetails()
    {
        CommitPendingSettings();
        DehazePasses = Adjustments.None.DehazePasses;
        DehazeClarity = Adjustments.None.DehazeClarity;
        DehazeContrast = Adjustments.None.DehazeContrast;
        DehazeVibrance = Adjustments.None.DehazeVibrance;
        DehazeSharpen = Adjustments.None.DehazeSharpen;
        DehazeEdge = 0;
        DehazeBrightness = Adjustments.None.DehazeBrightness;
        DehazeDeband = Adjustments.None.DehazeDeband;
        DehazeCast = Adjustments.None.DehazeCast;
    }

    /// <summary>Die Reglerstände als Auftrag für Vorschau und Export (ohne Ausgeblendetes).</summary>
    private Adjustments CurrentAdjustments() => Mask(new(
        Round(Brightness), Round(Contrast), Round(Shadows), Round(Highlights), Round(Saturation),
        TintOf(SelectedTint), Round(Vibrance), Round(TintHue), Round(Dehaze), Round(MatchLuminance),
        Round(Neutralize), Round(Warmth),
        (int)Round(DehazePasses), Round(DehazeClarity), Round(DehazeContrast), Round(DehazeVibrance),
        Round(DehazeSharpen), Round(DehazeEdge), Round(DehazeBrightness), Round(DehazeDeband), Round(DehazeCast),
        WbNeutral, WbBlack, WbWhite, Round(MatchLimit), Round(MatchTarget), Round(MatchGlobal),
        Round(MaskAmount), Round(MaskEnd), Round(MaskCurve)),
        HiddenSteps);   // ausgeblendete Bearbeitungen wirken nicht

    internal static Tint TintOf(string? label) =>
        TintLabels.FirstOrDefault(t => t.Label == label).Tint;

    // Ganze Schritte: Kleinstbewegungen beim Ziehen rechnen nicht neu
    private static double Round(double v) => Math.Round(v);
}
