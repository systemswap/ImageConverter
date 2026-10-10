using System;
using System.Collections.Generic;
using System.Linq;
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
        WbNeutral, WbBlack, WbWhite, Round(MatchLimit)),
        HiddenSteps);   // ausgeblendete Bearbeitungen wirken nicht

    internal static Tint TintOf(string? label) =>
        TintLabels.FirstOrDefault(t => t.Label == label).Tint;

    // Ganze Schritte: Kleinstbewegungen beim Ziehen rechnen nicht neu
    private static double Round(double v) => Math.Round(v);
}
