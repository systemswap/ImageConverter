using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;

namespace ImageConverter.ViewModels;

// "Tools" panel: light and colour, one slider per function from −100 to +100
// (hue for toning 0–360°), plus luminosity mask, grain and noise reduction.
// The adjustments act in the chain after denoising and before sharpening (Core: Adjust); grain comes last.
public partial class MainViewModel
{
    [ObservableProperty] public partial double Dehaze { get; set; }

    // Dehaze fine settings; for the defaults see Adjustments
    [ObservableProperty] public partial double DehazePasses { get; set; } = Adjustments.None.DehazePasses;
    [ObservableProperty] public partial double DehazeClarity { get; set; } = Adjustments.None.DehazeClarity;
    [ObservableProperty] public partial double DehazeContrast { get; set; } = Adjustments.None.DehazeContrast;
    [ObservableProperty] public partial double DehazeVibrance { get; set; } = Adjustments.None.DehazeVibrance;
    [ObservableProperty] public partial double DehazeSharpen { get; set; } = Adjustments.None.DehazeSharpen;
    [ObservableProperty] public partial double DehazeEdge { get; set; }
    [ObservableProperty] public partial double DehazeBrightness { get; set; } = Adjustments.None.DehazeBrightness;
    [ObservableProperty] public partial double DehazeDeband { get; set; } = Adjustments.None.DehazeDeband;
    [ObservableProperty] public partial double DehazeCast { get; set; } = Adjustments.None.DehazeCast;

    /// <summary>The fine settings appear only when haze is being removed.</summary>
    public bool HasDehaze => Math.Round(Dehaze) > 0;

    [ObservableProperty] public partial double Brightness { get; set; }
    [ObservableProperty] public partial double Contrast { get; set; }
    [ObservableProperty] public partial double Shadows { get; set; }
    [ObservableProperty] public partial double Highlights { get; set; }
    /// <summary>"Match colour" (without source image): luminance −100 … +100 (factor 1–200 %).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLimitLuminance))]
    public partial double MatchLuminance { get; set; }

    /// <summary>"Match colour": luminance threshold; brightening applies up to this brightness (%), brighter areas stay unchanged; 100 = no limit.</summary>
    [ObservableProperty] public partial double MatchLimit { get; set; } = 100;


    /// <summary>"Match colour": target value 0–90 %; raises dark areas only up to this brightness; 0 = off.</summary>
    [ObservableProperty] public partial double MatchTarget { get; set; }

    /// <summary>"Match colour": luminance for the whole image, applied after the regional luminance; −100 … +100 (0–200 %).</summary>
    [ObservableProperty] public partial double MatchGlobal { get; set; }

    // ------------------------------------------------------------ Luminosity mask (separate group)

    /// <summary>Luminosity mask: how much more light the darkest areas receive, 0–200 %.</summary>
    [ObservableProperty] public partial double MaskAmount { get; set; }

    /// <summary>Luminosity mask: the brightness (%) from which the mask has no effect.</summary>
    [ObservableProperty] public partial double MaskEnd { get; set; } = 75;

    /// <summary>Luminosity mask: gradient from 0 (soft, midtones are included) to 100 (steep, only the darkest areas).</summary>
    [ObservableProperty] public partial double MaskCurve { get; set; } = 30;

    /// <summary>Shows the mask in greyscale in the preview (view only: no undo step, never in the export).</summary>
    [ObservableProperty] public partial bool ShowMask { get; set; }

    public string MaskAmountLabel => MaskAmount < 0.5 ? Tr.T("aus") : $"+{Math.Round(MaskAmount):0} %";
    public string MaskEndLabel => $"{Math.Round(MaskEnd):0} %";
    public string MaskCurveLabel => MaskCurve < 20 ? Tr.T("weich") : MaskCurve > 70 ? Tr.T("steil") : $"{Math.Round(MaskCurve):0}";

    partial void OnMaskAmountChanged(double value) => ToolChanged(nameof(MaskAmountLabel));
    partial void OnMaskEndChanged(double value) => ToolChanged(nameof(MaskEndLabel));
    partial void OnMaskCurveChanged(double value) => ToolChanged(nameof(MaskCurveLabel));
    partial void OnShowMaskChanged(bool value) => RequestPreview();

    // ------------------------------------------------------------ Grain (separate group, always the last step)

    /// <summary>Grain: strength 0–100 % (spread in the midtones).</summary>
    [ObservableProperty] public partial double GrainAmount { get; set; }

    /// <summary>Grain: grain size from −27 (0.1 px, finer than a pixel) through 0 (pixel by pixel) to 100 (coarse), in output pixels.</summary>
    [ObservableProperty] public partial double GrainSize { get; set; } = GrainSettings.None.Size;

    /// <summary>Grain: roughness from 0 (even) to 100 (clumpy like film).</summary>
    [ObservableProperty] public partial double GrainRoughness { get; set; } = GrainSettings.None.Roughness;

    /// <summary>Grain: colour share from 0 (brightness only) to 100 %.</summary>
    [ObservableProperty] public partial double GrainColor { get; set; }

    /// <summary>Grain: hardness from 0 (soft dots) to 100 (sharply bounded grains, grainier).</summary>
    [ObservableProperty] public partial double GrainHardness { get; set; }

    /// <summary>"Measure from the photo" is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureGrainCommand))]
    public partial bool IsMeasuringGrain { get; set; }

    public string GrainAmountLabel => GrainAmount < 0.5 ? Tr.T("aus") : $"{Math.Round(GrainAmount):0} %";
    public string GrainSizeLabel => GrainSizeText(GrainSize);

    /// <summary>Grain size in pixels, below 1 px with two decimals (0.25 px is pixel by pixel, 0.1 px is finer still).</summary>
    internal static string GrainSizeText(double size) =>
        GrainSettings.SigmaFor(Math.Round(size)) is var s && s < 1 ? $"{s:0.0#} px" : $"{s:0.0} px";
    public string GrainRoughnessLabel => GrainRoughness < 0.5 ? Tr.T("gleichmäßig") : $"{Math.Round(GrainRoughness):0}";
    public string GrainColorLabel => GrainColor < 0.5 ? Tr.T("aus") : $"{Math.Round(GrainColor):0} %";
    public string GrainHardnessLabel => GrainHardness < 0.5 ? Tr.T("weich") : $"{Math.Round(GrainHardness):0}";

    partial void OnGrainAmountChanged(double value) => ToolChanged(nameof(GrainAmountLabel));
    partial void OnGrainSizeChanged(double value) => ToolChanged(nameof(GrainSizeLabel));
    partial void OnGrainRoughnessChanged(double value) => ToolChanged(nameof(GrainRoughnessLabel));
    partial void OnGrainColorChanged(double value) => ToolChanged(nameof(GrainColorLabel));
    partial void OnGrainHardnessChanged(double value) => ToolChanged(nameof(GrainHardnessLabel));

    /// <summary>Grain for preview and export; null = off (or switched off in the list).</summary>
    private GrainSettings? EffectiveGrain => Shows(EditSteps.Grain) && Round(GrainAmount) >= 1
        ? new GrainSettings(Round(GrainAmount), Round(GrainSize), Round(GrainRoughness), Round(GrainColor), Round(GrainHardness))
        : null;

    private bool CanMeasureGrain => !IsMeasuringGrain;

    /// <summary>
    /// Measures the grain of the photo and sets the sliders accordingly; measured on the image before the AI and without
    /// settings (that is, before denoising), converted to the output size. One undo step; can be refined by hand afterwards.
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

            CommitPendingSettings();   // a separate undo step
            GrainAmount = measured.Amount;
            GrainSize = measured.Size;
            GrainRoughness = measured.Roughness;
            GrainColor = measured.Color;
            GrainHardness = measured.Hardness;
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


    /// <summary>The threshold only has an effect when brightening.</summary>
    public bool CanLimitLuminance => Math.Round(MatchLuminance) > 0;

    /// <summary>"Match colour": neutralise; how much of the detected colour cast is removed, 0–100 %.</summary>
    [ObservableProperty] public partial double Neutralize { get; set; }

    /// <summary>"Match colour": colour; cool (−) … neutral (0) … warm (+).</summary>
    [ObservableProperty] public partial double Warmth { get; set; }

    [ObservableProperty] public partial double Vibrance { get; set; }
    [ObservableProperty] public partial double Saturation { get; set; }
    [ObservableProperty] public partial string SelectedTint { get; set; } = TintLabels[0].Label;

    /// <summary>Hue for the "Colour" toning, 0–360°.</summary>
    [ObservableProperty] public partial double TintHue { get; set; } = Adjustments.SepiaHue;

    /// <summary>The hue slider appears only when toning in colour.</summary>
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

    /// <summary>
    /// Resets everything in this panel. The dehaze fine settings are reset by <see cref="ResetDehazeDetails"/>, which
    /// commits the preceding changes first, so they become a separate undo step if they differed from the defaults.
    /// </summary>
    [RelayCommand]
    private void ResetTools()
    {
        CommitPendingSettings();   // the previous slider movement stays a separate step
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
        GrainHardness = 0;
        NoiseReduction = ColorNoiseReduction = 0;   // noise reduction is part of this panel as well
        SelectedTint = TintLabels[0].Label;
        TintHue = Adjustments.SepiaHue;
        ResetDehazeDetails();
    }

    /// <summary>Resets only the dehaze fine settings to their defaults, as one undo step.</summary>
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

    /// <summary>The slider values as a job for preview and export (without switched-off functions).</summary>
    private Adjustments CurrentAdjustments() => Mask(new(
        Round(Brightness), Round(Contrast), Round(Shadows), Round(Highlights), Round(Saturation),
        TintOf(SelectedTint), Round(Vibrance), Round(TintHue), Round(Dehaze), Round(MatchLuminance),
        Round(Neutralize), Round(Warmth),
        (int)Round(DehazePasses), Round(DehazeClarity), Round(DehazeContrast), Round(DehazeVibrance),
        Round(DehazeSharpen), Round(DehazeEdge), Round(DehazeBrightness), Round(DehazeDeband), Round(DehazeCast),
        WbNeutral, WbBlack, WbWhite, Round(MatchLimit), Round(MatchTarget), Round(MatchGlobal),
        Round(MaskAmount), Round(MaskEnd), Round(MaskCurve)),
        HiddenSteps);   // switched-off edits have no effect

    internal static Tint TintOf(string? label) =>
        TintLabels.FirstOrDefault(t => t.Label == label).Tint;

    // Whole steps: tiny movements while dragging do not trigger a recomputation.
    private static double Round(double v) => Math.Round(v);
}
