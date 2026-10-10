using System;
using System.Linq;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// Der Bildlook als Ganzes — für Erweiterungen (KI), die ihn vorher ins Bild rechnen oder
// über ein eigenes Zwischenergebnis legen wollen.
public partial class MainViewModel
{
    /// <summary>
    /// Was die Kette außer Größe und Format aufs Bild legt: Entrauschen → Tools (Licht, Farbe, Dunst) → Schärfen.
    /// </summary>
    /// <param name="TargetWidth">Breite des Exports — der Schärferadius gilt relativ dazu.</param>
    internal sealed record Look(double Noise, Adjustments Adjust, double SharpenAmount, double SharpenRadius, int TargetWidth,
        double ColorNoise = 0)
    {
        public bool IsNone => Noise <= 0 && ColorNoise <= 0 && SharpenAmount <= 0 && Adjust.IsNone;

        /// <summary>Schärferadius für ein Bild dieser Breite (so, dass er im Export gleich wirkt).</summary>
        public double SharpenSigma(int width) => SharpenRadius * width / Math.Max(1, TargetWidth);

        /// <summary>Schritte für die Fortschrittsanzeige (Entrauschen, Dunst je Durchlauf, Licht und Farbe, Schärfen).</summary>
        public WorkPlan Plan(int width, int height, IProgress<WorkProgress>? progress, System.Threading.CancellationToken cancel) =>
            ImageExporter.PlanLook((double)width * height / 1e6, Noise, ColorNoise, Adjust, SharpenAmount, progress, cancel);

        /// <summary>Derselbe Look ohne Wirkung: Regler, die etwas tun, auf null; Feineinstellungen bleiben.</summary>
        public Look Neutral() => this with
        {
            Noise = 0,
            ColorNoise = 0,
            SharpenAmount = 0,
            Adjust = Adjust with
            {
                Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0, Saturation = 0, Tint = Tint.None,
                Vibrance = 0, Dehaze = 0, MatchLuminance = 0, Neutralize = 0, Warmth = 0,
                WbNeutral = null, WbBlack = null, WbWhite = null,
            },
        };
    }

    /// <summary>Größe und Filter der normalen Vorschau — für Zwischenergebnisse, die an ihrer Stelle gezeigt werden.</summary>
    internal AiPreviewSize PreviewSize()
    {
        var (tw, th) = EffectiveTarget();
        return AiPreviewSize.Fit(tw, th, PreviewMaxEdge) with { Filter = SelectedFilter.Filter, GammaCorrect = GammaCorrect };
    }

    /// <summary>
    /// Ziel des Exports so groß wie das Bild? Dann zeigt die normale Vorschau ohne Look das Bild selbst, in voller
    /// Größe — ein Zwischenergebnis sollte dann auch in voller Größe erscheinen, sonst springt es beim Übernehmen.
    /// </summary>
    internal bool TargetIsImageSize
    {
        get
        {
            var (tw, th) = EffectiveTarget();
            return _history?.Current is { } doc && tw == (int)doc.Width && th == (int)doc.Height;
        }
    }

    internal Look CurrentLook() => new(
        EffectiveNoise,
        CurrentAdjustments(),
        EffectiveSharpen,
        SharpenRadius,
        EffectiveTarget().Width,
        EffectiveColorNoise);

    /// <summary>
    /// Teilen sich mehrere Bilder die Einstellungen („für alle“)? Dann lässt sich der Look nicht für
    /// ein einzelnes Bild ins Bild rechnen und zurücksetzen.
    /// </summary>
    internal bool SettingsShared => ApplyToAll && _files.Count > 1;

    /// <summary>Look in voller Größe ins Bild rechnen (Hintergrund). Ohne Wirkung: das Bild selbst.</summary>
    internal static MagickImage RenderLook(MagickImage image, Look look, WorkPlan? plan = null)
    {
        if (look.IsNone) return image;
        int width;
        lock (image) width = (int)image.Width;
        return ImageExporter.RenderLook(image, look.Noise, look.ColorNoise, look.Adjust, look.SharpenAmount, look.SharpenSigma(width), plan);
    }

    /// <summary>Look auf eine Vorschau (Pixelpuffer der Kette) rechnen und für die Anzeige runden (Hintergrund).</summary>
    internal static byte[] RenderLookPreview(float[] bgra, int width, int height, Look? look, WorkPlan? plan = null)
    {
        if (look is null || look.IsNone) return Pixels.ToBytes(bgra, dither: true);

        var pixels = bgra;
        if (look.Noise > 0 || look.ColorNoise > 0)
            pixels = Denoiser.Apply(pixels, width, height, look.Noise, look.ColorNoise, (double)width / Math.Max(1, look.TargetWidth), plan);
        if (!look.Adjust.IsNone) pixels = Adjust.Apply(pixels, width, height, look.Adjust, plan);
        if (look.SharpenAmount > 0)
        {
            plan?.Next();
            pixels = Sharpen.UnsharpMask(pixels, width, height, look.SharpenAmount, look.SharpenSigma(width));
        }

        var result = Pixels.ToBytes(pixels, dither: true);
        PixelPool.Trim();
        return result;
    }

    /// <summary>Die Einstellungen mit einem anderen Look (alles Übrige bleibt).</summary>
    private static SettingsState WithLook(SettingsState s, Look look)
    {
        var a = look.Adjust;
        return s with
        {
            NoiseReduction = look.Noise,
            ColorNoiseReduction = look.ColorNoise,
            SharpenAmount = look.SharpenAmount,
            SharpenRadius = look.SharpenRadius,
            Brightness = a.Brightness, Contrast = a.Contrast, Shadows = a.Shadows, Highlights = a.Highlights,
            Saturation = a.Saturation, Vibrance = a.Vibrance, Dehaze = a.Dehaze,
            SelectedTint = TintLabels.First(t => t.Tint == a.Tint).Label, TintHue = a.TintHue,
            MatchLuminance = a.MatchLuminance, Neutralize = a.Neutralize, Warmth = a.Warmth,
            DehazePasses = a.DehazePasses, DehazeClarity = a.DehazeClarity, DehazeContrast = a.DehazeContrast,
            DehazeVibrance = a.DehazeVibrance, DehazeSharpen = a.DehazeSharpen, DehazeEdge = a.DehazeEdge,
            DehazeBrightness = a.DehazeBrightness, DehazeDeband = a.DehazeDeband, DehazeCast = a.DehazeCast,
            WbNeutral = a.WbNeutral, WbBlack = a.WbBlack, WbWhite = a.WbWhite,
        };
    }

}
