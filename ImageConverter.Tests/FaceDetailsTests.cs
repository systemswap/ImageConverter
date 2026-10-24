using ImageConverter.Core.Ai;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>
/// Tests the detail sliders applied after the AI face model: „Falten &amp; Züge“ brings smoothed structure back from the
/// original (skin only), „Körnung“ measures the grain of the photo and adds grain of the same strength to the face.
/// </summary>
public class FaceDetailsTests
{
    /// <summary>Face model that smooths (9 × 9 box mean) and thereby removes wrinkles.</summary>
    private sealed class SmoothFace : IFaceModel
    {
        public int Size => 128;

        public float[] Run(float[] input, CancellationToken ct)
        {
            var n = Size * Size;
            var output = new float[input.Length];
            for (var c = 0; c < 3; c++)
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                float sum = 0;
                for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                    sum += input[c * n + Math.Clamp(y + dy, 0, Size - 1) * Size + Math.Clamp(x + dx, 0, Size - 1)];
                output[c * n + y * Size + x] = sum / 81;
            }
            return output;
        }
    }

    /// <summary>Face model that changes nothing.</summary>
    private sealed class IdentityFace : IFaceModel
    {
        public int Size => 128;
        public float[] Run(float[] input, CancellationToken ct) => input;
    }

    private static readonly DetectedFace Face =
        new(100, 100, 200, 200, 0.9, [(130, 130), (170, 130), (150, 150), (135, 172), (165, 172)]);

    /// <summary>Builds a grey image from values 0…1 (one plane, copied to R, G and B).</summary>
    private static MagickImage Gray(int w, int h, Func<int, int, float> value)
    {
        var data = new ushort[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var v = (ushort)(Math.Clamp(value(x, y), 0f, 1f) * 65535 + 0.5f);
            data[(y * w + x) * 3] = data[(y * w + x) * 3 + 1] = data[(y * w + x) * 3 + 2] = v;
        }
        var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings((uint)w, (uint)h, StorageType.Quantum, "RGB"));
        return image;
    }

    private static double Distance(MagickImage a, MagickImage b, int x0, int y0, int x1, int y1)
    {
        var pa = AiEnhancer.ReadPixels(a, "RGB");
        var pb = AiEnhancer.ReadPixels(b, "RGB");
        double sum = 0;
        var n = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++, n++)
        {
            var i = (y * (int)a.Width + x) * 3;
            sum += Math.Abs(pa[i] - pb[i]) / 65535.0;
        }
        return sum / n;
    }

    [Fact]
    public void Folds_BringBackSmoothedLines_OnSkin_ButNotAtTheEyes()
    {
        // Wrinkles: horizontal lines every 8 pixels
        using var src = Gray(300, 300, (x, y) => 0.55f + 0.08f * MathF.Sin(y * MathF.PI / 4));
        var run = AiFaces.Run(src, [Face], new SmoothFace(), null, null, default);

        using var smooth = run.Compose(new AiBlend());
        using var folds = run.Compose(new AiBlend(Folds: 1));

        // Skin between eyes and mouth: clearly closer to the original
        var (before, after) = (Distance(src, smooth, 145, 138, 156, 148), Distance(src, folds, 145, 138, 156, 148));
        Assert.True(after < before * 0.7, $"ohne {before:0.0000}, mit Falten {after:0.0000}");

        // at the eye: the model output stays
        Assert.True(Distance(smooth, folds, 127, 127, 134, 134) < 0.002);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.5)]    // white noise: small grain size
    [InlineData(1.5, 1.0, 2.0)]    // coarse grain
    public void MeasureGrain_FindsStrengthAndSize(double sigma, double minSize, double maxSize)
    {
        const int w = 512, h = 384;
        var noise = FaceDetails.GrainField(0, 0, w, h, sigma, seed: 7);
        var image = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var v = 0.1f + 0.8f * x / w + 0.02f * noise[y * w + x];   // gradient + grain with standard deviation 0.02
            image[(y * w + x) * 3] = image[(y * w + x) * 3 + 1] = image[(y * w + x) * 3 + 2] = v;
        }

        var profile = FaceDetails.MeasureGrain(image, w, h);

        Assert.InRange(profile.SigmaAt(0.5f), 0.016, 0.024);
        Assert.InRange(profile.GrainSigma, minSize, maxSize);
    }

    [Fact]
    public void Grain_FillsUpToThePhoto_InTheFace()
    {
        var noise = FaceDetails.GrainField(0, 0, 300, 300, 0, seed: 3);
        using var src = Gray(300, 300, (x, y) => 0.5f + 0.02f * noise[y * 300 + x]);
        var run = AiFaces.Run(src, [Face], new SmoothFace(), null, null, default);

        using var clean = run.Compose(new AiBlend());
        using var grainy = run.Compose(new AiBlend(Grain: 1));

        // Face centre: the model has smoothed it, „Körnung“ at 100 % brings it back to the grain of the photo
        var (photo, smooth, filled) = (Noise(src), Noise(clean), Noise(grainy));
        Assert.True(smooth < photo * 0.5, $"geglättet {smooth:0.0000}, Foto {photo:0.0000}");
        Assert.InRange(filled / photo, 0.8, 1.25);

        // outside the faces: no change
        Assert.True(Distance(clean, grainy, 0, 0, 40, 40) == 0);
    }

    /// <summary>Face model that invents fine structure itself (strong noise).</summary>
    private sealed class NoisyFace : IFaceModel
    {
        public int Size => 128;

        public float[] Run(float[] input, CancellationToken ct)
        {
            var noise = FaceDetails.GrainField(0, 0, Size, Size * 3, 0, seed: 9);
            return input.Select((v, i) => v + 0.15f * noise[i]).ToArray();
        }
    }

    [Fact]
    public void Grain_AddsNothing_WhenTheFaceIsAlreadyGrainy()
    {
        var noise = FaceDetails.GrainField(0, 0, 300, 300, 0, seed: 3);
        using var src = Gray(300, 300, (x, y) => 0.5f + 0.02f * noise[y * 300 + x]);
        var run = AiFaces.Run(src, [Face], new NoisyFace(), null, null, default);

        using var clean = run.Compose(new AiBlend());
        using var grainy = run.Compose(new AiBlend(Grain: 1));

        Assert.True(Distance(clean, grainy, 140, 140, 160, 160) < 0.002);
    }

    /// <summary>Fine structure in the face centre: standard deviation (0…1) of each pixel minus the mean of its 8 neighbours.</summary>
    private static double Noise(MagickImage image)
    {
        var p = AiEnhancer.ReadPixels(image, "RGB");
        var w = (int)image.Width;
        double V(int x, int y) => p[(y * w + x) * 3] / 65535.0;
        double sum = 0, sum2 = 0;
        var n = 0;
        for (var y = 140; y < 160; y++)
        for (var x = 140; x < 160; x++, n++)
        {
            var r = V(x, y) - (V(x - 1, y - 1) + V(x, y - 1) + V(x + 1, y - 1) + V(x - 1, y) + V(x + 1, y)
                               + V(x - 1, y + 1) + V(x, y + 1) + V(x + 1, y + 1)) / 8;
            sum += r;
            sum2 += r * r;
        }
        return Math.Sqrt(sum2 / n - sum / n * (sum / n));
    }
}
