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
    [ObservableProperty] public partial double Brightness { get; set; }
    [ObservableProperty] public partial double Contrast { get; set; }
    [ObservableProperty] public partial double Shadows { get; set; }
    [ObservableProperty] public partial double Highlights { get; set; }
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

    public string BrightnessLabel => Signed(Brightness);
    public string ContrastLabel => Signed(Contrast);
    public string ShadowsLabel => Signed(Shadows);
    public string HighlightsLabel => Signed(Highlights);
    public string VibranceLabel => Signed(Vibrance);
    public string SaturationLabel => Signed(Saturation);
    public string TintHueLabel => $"{Math.Round(TintHue):0}°";

    private static string Signed(double v) => (int)Math.Round(v) switch
    {
        0 => "0",
        > 0 and var n => $"+{n}",
        var n => $"{n}",
    };

    partial void OnBrightnessChanged(double value) => ToolChanged(nameof(BrightnessLabel));
    partial void OnContrastChanged(double value) => ToolChanged(nameof(ContrastLabel));
    partial void OnShadowsChanged(double value) => ToolChanged(nameof(ShadowsLabel));
    partial void OnHighlightsChanged(double value) => ToolChanged(nameof(HighlightsLabel));
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
        Brightness = Contrast = Shadows = Highlights = Vibrance = Saturation = 0;
        SelectedTint = TintLabels[0].Label;
        TintHue = Adjustments.SepiaHue;
    }

    /// <summary>Die Reglerstände als Auftrag für Vorschau und Export.</summary>
    private Adjustments CurrentAdjustments() => new(
        Round(Brightness), Round(Contrast), Round(Shadows), Round(Highlights), Round(Saturation),
        TintOf(SelectedTint), Round(Vibrance), Round(TintHue));

    internal static Tint TintOf(string? label) =>
        TintLabels.FirstOrDefault(t => t.Label == label).Tint;

    // Ganze Schritte: Kleinstbewegungen beim Ziehen rechnen nicht neu
    private static double Round(double v) => Math.Round(v);
}
