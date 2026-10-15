using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>
/// Rauschen getrennt nach Helligkeit und Farbe: Rauschen weg, Details (feine Streifen, Kanten) bleiben, die Farb-Regler
/// lassen die Helligkeit in Ruhe.
/// </summary>
public class DenoiserTests
{
    private const int W = 256, H = 192;

    /// <summary>
    /// Grafikkarte (wenn vorhanden) und CPU rechnen dasselbe Non-Local Means: Abweichung nur in der Rundung (unter
    /// 1/1000 Stufe). Ohne Grafikkarte prüft der Test nur, dass die CPU rechnet.
    /// </summary>
    [Fact]
    public void LumaDenoise_GpuAndCpu_Agree()
    {
        var (noisy, _) = Scene(0.06f, 0.02f);
        float[] cpu, gpu;
        using (GpuFilters.CpuOnly()) cpu = Denoiser.Apply(noisy, W, H, 0.6, 0.4);
        gpu = Denoiser.Apply(noisy, W, H, 0.6, 0.4);
        var max = cpu.Zip(gpu).Max(p => Math.Abs(p.First - p.Second)) * 255;
        Assert.True(max < 1e-3, $"Grafikkarte ({GpuFilters.DeviceName ?? "keine"}) weicht um {max:0.0000} Stufen ab");
    }

    /// <summary>Links eben, rechts feine senkrechte Streifen (Periode 6 px) — dazu Rauschen in Helligkeit und/oder Farbe.</summary>
    private static (float[] Noisy, float[] Clean) Scene(float lumaNoise, float chromaNoise)
    {
        var n1 = FaceDetails.GrainField(0, 0, W, H, 0, seed: 1);
        var n2 = FaceDetails.GrainField(0, 0, W, H, 0, seed: 2);
        var n3 = FaceDetails.GrainField(0, 0, W, H, 0, seed: 3);
        float[] noisy = new float[W * H * 4], clean = new float[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = y * W + x;
            var v = x < W / 2 ? 0.5f : 0.5f + 0.15f * MathF.Sin(x * MathF.PI / 3);
            var l = lumaNoise * n1[i];
            float cb = chromaNoise * n2[i], cr = chromaNoise * n3[i];
            // Helligkeit v + l, Farbabstände cb, cr → RGB (wie im Entrauscher)
            var yy = v + l;
            float r = yy + cr, b = yy + cb, g = (yy - 0.2126f * r - 0.0722f * b) / 0.7152f;
            (noisy[i * 4], noisy[i * 4 + 1], noisy[i * 4 + 2], noisy[i * 4 + 3]) = (b, g, r, 1);
            (clean[i * 4], clean[i * 4 + 1], clean[i * 4 + 2], clean[i * 4 + 3]) = (v, v, v, 1);
        }
        return (noisy, clean);
    }

    private static float Luma(float[] px, int i) => 0.2126f * px[i * 4 + 2] + 0.7152f * px[i * 4 + 1] + 0.0722f * px[i * 4];

    /// <summary>Mittlere quadratische Abweichung vom sauberen Bild in einem Bereich (Helligkeit oder Farbe Cr).</summary>
    private static double Error(float[] a, float[] clean, int x0, int x1, bool chroma)
    {
        double sum = 0;
        var n = 0;
        for (var y = 8; y < H - 8; y++)
        for (var x = x0; x < x1; x++, n++)
        {
            var i = y * W + x;
            var d = chroma ? (a[i * 4 + 2] - Luma(a, i)) - (clean[i * 4 + 2] - Luma(clean, i)) : Luma(a, i) - Luma(clean, i);
            sum += d * d;
        }
        return Math.Sqrt(sum / n);
    }

    [Fact]
    public void Luma_RemovesNoise_KeepsFineStripes()
    {
        var (noisy, clean) = Scene(lumaNoise: 0.03f, chromaNoise: 0);
        var result = Denoiser.Apply(noisy, W, H, luma: 0.7, chroma: 0);

        // eben: Rauschen deutlich kleiner
        var (before, after) = (Error(noisy, clean, 8, W / 2 - 8, false), Error(result, clean, 8, W / 2 - 8, false));
        Assert.True(after < before * 0.5, $"eben: vorher {before:0.0000}, nachher {after:0.0000}");

        // Streifen: näher am sauberen Bild als vorher — sie werden nicht weggebügelt
        var (sb, sa) = (Error(noisy, clean, W / 2 + 8, W - 8, false), Error(result, clean, W / 2 + 8, W - 8, false));
        Assert.True(sa < sb, $"Streifen: vorher {sb:0.0000}, nachher {sa:0.0000}");
    }

    [Fact]
    public void Chroma_RemovesColourNoise_LeavesBrightness()
    {
        var (noisy, clean) = Scene(lumaNoise: 0, chromaNoise: 0.04f);
        var result = Denoiser.Apply(noisy, W, H, luma: 0, chroma: 0.8);

        var (before, after) = (Error(noisy, clean, 8, W - 8, true), Error(result, clean, 8, W - 8, true));
        Assert.True(after < before * 0.3, $"Farbe: vorher {before:0.0000}, nachher {after:0.0000}");

        // Helligkeit unverändert
        double maxLuma = 0;
        for (var i = 0; i < W * H; i++) maxLuma = Math.Max(maxLuma, Math.Abs(Luma(result, i) - Luma(noisy, i)));
        Assert.True(maxLuma < 1e-4, $"Helligkeit verändert um {maxLuma}");
    }

    [Fact]
    public void Zero_ChangesNothing()
    {
        var (noisy, _) = Scene(0.03f, 0.04f);
        var result = Denoiser.Apply(noisy, W, H, 0, 0);
        for (var i = 0; i < noisy.Length; i++) Assert.Equal(noisy[i], result[i], 5);
    }
}
