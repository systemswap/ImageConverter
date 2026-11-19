using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>
/// The scene map of the dehaze filter (what is sky), the noise estimate and the noise-aware sharpening, and the share of
/// the brightness compensation in dense haze.
/// </summary>
public class DehazeSceneTests
{
    private const int W = 400, H = 240;

    private static float Hash(int x, int y)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    /// <summary>A picture in linear light, B, G, R per pixel.</summary>
    private static float[] Linear(Func<int, int, (float R, float G, float B)> colour)
    {
        var lin = new float[W * H * 3];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var (r, g, b) = colour(x, y);
            var i = (y * W + x) * 3;
            (lin[i], lin[i + 1], lin[i + 2]) = (b, g, r);
        }
        return lin;
    }

    private static (float R, float G, float B) Sky(int y) => (0.50f + 0.1f * y / H, 0.62f + 0.1f * y / H, 0.85f);

    /// <summary>A brown field with texture (structure from pixel to pixel, as soil and crops have).</summary>
    private static (float R, float G, float B) Field(int x, int y)
    {
        var f = 0.3f + 1.4f * Hash(x, y);
        return (0.30f * f, 0.22f * f, 0.10f * f);
    }

    private static float At(float[] map, int x, int y) => map[y * W + x];

    /// <summary>
    /// A smooth, bright patch in a textured field is bright and without structure like sky; as sky it kept its haze
    /// while the field around it was cleared – a darker spot once the brightness compensation acted (Fehlergedächtnis
    /// #128). An isolated region that neither touches the top edge nor is large does not count as sky.
    /// </summary>
    [Fact]
    public void SkyMap_DropsAnIsolatedSmoothSpotInAField()
    {
        var lin = Linear((x, y) => y < 80 ? Sky(y)
            : x is >= 180 and < 220 && y is >= 150 and < 174 ? (0.55f, 0.50f, 0.42f)   // smooth beige spot
            : Field(x, y));
        var sky = Dehaze.SkyMap(lin, W, H);
        Assert.True(At(sky, 200, 162) < 0.1f, $"Fleck im Feld gilt als Himmel: {At(sky, 200, 162):0.00}");
        Assert.True(At(sky, 200, 30) > 0.9f, $"Himmel nicht erkannt: {At(sky, 200, 30):0.00}");
    }

    /// <summary>A smooth green meadow, large and bright, is never sky: a sky is blue, grey, white or warm, never green.</summary>
    [Fact]
    public void SkyMap_AGreenMeadowIsNeverSky()
    {
        var lin = Linear((x, y) => y < 80 ? Sky(y)
            : x is >= 40 and < 240 && y is >= 130 and < 220 ? (0.15f, 0.45f, 0.10f)   // smooth green meadow, 9 % of the picture
            : Field(x, y));
        var sky = Dehaze.SkyMap(lin, W, H);
        Assert.True(At(sky, 140, 175) < 0.1f, $"Wiese gilt als Himmel: {At(sky, 140, 175):0.00}");
    }

    /// <summary>
    /// After dehazing, the sky right next to a silhouette (a tree reaching into the sky) is not more saturated than the sky
    /// further away. The structure of the edge used to be smeared isotropically into the sky next to it, which then lost
    /// its protection and was dehazed and saturated in full – a blue band along every silhouette (Fehlergedächtnis #129).
    /// </summary>
    [Fact]
    public void Dehaze_SkyNextToASilhouette_IsNotMoreSaturated()
    {
        // Larger than the estimation copy (1024 px), as a real photo: the band came from smoothing on that copy
        const int w = 2048, h = 1024;
        const float air = 0.80f;
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = y * w + x;
            (float R, float G, float B) c;
            float t;
            if (x is >= 700 and < 860 && y >= 120) { c = (0.04f + 0.05f * Hash(x, y), 0.07f + 0.08f * Hash(y, x), 0.03f); t = 0.8f; }
            else if (y < 700) { c = (0.25f, 0.45f, 0.85f); t = 0.45f; }   // blue sky behind a veil
            else { c = Field(x, y); t = 0.6f; }
            float Mix(float v) => v * t + air * (1 - t);
            bgra[p * 4] = Srgb.ToByte(Mix(c.B)); bgra[p * 4 + 1] = Srgb.ToByte(Mix(c.G)); bgra[p * 4 + 2] = Srgb.ToByte(Mix(c.R)); bgra[p * 4 + 3] = 255;
        }
        var px = Pixels.FromBytes(bgra);
        // the settings of a real picture: gentle dehaze in eight passes with clarity, contrast, vibrance and sharpening
        var result = Adjust.Dehazed(px, w, h, Adjustments.None with
        {
            Dehaze = 36, DehazePasses = 8, DehazeClarity = 42, DehazeContrast = 22, DehazeVibrance = 25, DehazeSharpen = 33,
            DehazeBrightness = 76, DehazeDeband = 55, DehazeCast = 51,
        });
        double ChromaGain(int x0, int x1)
        {
            double before = 0, after = 0;
            for (var y = 250; y < 600; y += 2)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * w + x) * 4;
                var o = Oklab.FromSrgb(px[i + 2], px[i + 1], px[i]);
                var n = Oklab.FromSrgb(result[i + 2], result[i + 1], result[i]);
                before += MathF.Sqrt(o.A * o.A + o.B * o.B);
                after += MathF.Sqrt(n.A * n.A + n.B * n.B);
            }
            return after / before;
        }
        var edge = ChromaGain(866, 960);    // 6–100 px next to the tree
        var inner = ChromaGain(1500, 1900);
        // Measured: 1.038 × the inner gain (before: 1.055); the band in a real photo is stronger, where a warm gradient of the
        // sky meets the silhouette – this synthetic sky shows the mechanism, not its full size
        Assert.True(edge < 1.045 * inner, $"Himmel am Baum ×{edge:0.000}, innen ×{inner:0.000}");
    }

    /// <summary>The noise estimate (Immerkær) finds the standard deviation of known noise and ignores gradients.</summary>
    [Fact]
    public void EstimateNoise_FindsTheNoiseOfAPicture()
    {
        var rnd = new Random(7);
        float Gauss() => (float)(Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble()));
        var noisy = new float[W * H * 4];
        var clean = new float[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 4;
            var v = 0.2f + 0.6f * x / W;   // gradient
            var n = 0.01f * Gauss();
            for (var c = 0; c < 3; c++) { clean[i + c] = v; noisy[i + c] = v + n; }
            noisy[i + 3] = clean[i + 3] = 1;
        }
        Assert.InRange(Dehaze.EstimateNoise(noisy, W, H), 0.0085f, 0.0115f);
        Assert.True(Dehaze.EstimateNoise(clean, W, H) < 0.001f);
    }

    /// <summary>
    /// The fine sharpening of the dehaze filter sharpens edges, but not the noise: below the measured noise (times the
    /// amplification of the passes) a detail is left alone (coring).
    /// </summary>
    [Fact]
    public void Coring_SharpensEdges_ButNotTheNoise()
    {
        var rnd = new Random(11);
        float Gauss() => (float)(Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble()));
        var px = new float[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 4;
            var v = (x / 40) % 2 == 0 ? 0.30f : 0.60f;   // stripes with hard edges
            var n = 0.008f * Gauss();
            for (var c = 0; c < 3; c++) px[i + c] = v + n;
            px[i + 3] = 1;
        }
        var small = new float[W * H * 3];
        for (var p = 0; p < W * H; p++)
            for (var c = 0; c < 3; c++) small[p * 3 + c] = Srgb.Decode(px[p * 4 + c]);
        var look = new Dehaze.LookSettings(Clarity: 0, Curve: 0, Vibrance: 0, VibranceSky: 0, Sharpen: 0.8f, Edge: 0);

        float[] Sharpened(bool coring)
        {
            var scene = SceneMap.Of(small, W, H, 1);
            scene.Noise = coring ? Dehaze.EstimateNoise(px, W, H) : 0;
            var copy = (float[])px.Clone();
            Dehaze.LookDetail(copy, W, H, scene, look);
            return copy;
        }

        // noise: mean absolute deviation from the stripe value inside the stripes; edge: overshoot next to the edges
        static double Noise(float[] img) =>
            Enumerable.Range(0, W * H).Where(p => p % W % 40 is > 6 and < 34).Average(p => Math.Abs(img[p * 4 + 1] - ((p % W / 40) % 2 == 0 ? 0.30 : 0.60)));
        static double Edge(float[] img) =>
            Enumerable.Range(10, H - 20).Average(y => img[(y * W + 40) * 4 + 1] - img[(y * W + 39) * 4 + 1]);
        var with = Sharpened(true);
        var without = Sharpened(false);
        Assert.True(Noise(without) > 1.15 * Noise(px), $"Testaufbau: die Schärfung verstärkt das Rauschen nicht ({Noise(without) / Noise(px):0.00})");
        Assert.True(Noise(with) < 1.08 * Noise(px), $"Rauschen mit Coring ×{Noise(with) / Noise(px):0.00}");
        Assert.True(Edge(with) > 0.9 * Edge(without), $"Kante mit Coring {Edge(with):0.000}, ohne {Edge(without):0.000}");
    }

    /// <summary>
    /// The brightness compensation gives dense haze only a share: matching its brightness to the hazy original would give
    /// the milky distance straight back, while near the camera, where the haze is thin, the exposure is kept.
    /// </summary>
    [Fact]
    public void Compensation_GivesDenseHazeOnlyAShare()
    {
        const float air = 0.85f;
        var bgra = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var p = y * W + x;
            var f = Field(x, y);
            float t = y < 30 ? 0 : y < 120 ? 0.5f : 0.95f;   // sky, denser haze in the distance, thin haze near
            var (r, g, b) = y < 30 ? (air, air, air) : (f.R * t + air * (1 - t), f.G * t + air * (1 - t), f.B * t + air * (1 - t));
            bgra[p * 4] = Srgb.ToByte(b); bgra[p * 4 + 1] = Srgb.ToByte(g); bgra[p * 4 + 2] = Srgb.ToByte(r); bgra[p * 4 + 3] = 255;
        }
        var px = Pixels.FromBytes(bgra);
        var result = Adjust.Dehazed(px, W, H, Adjustments.None with
        {
            Dehaze = 300, DehazePasses = 1, DehazeClarity = 0, DehazeContrast = 0, DehazeVibrance = 0, DehazeSharpen = 0,
            DehazeBrightness = 100, DehazeDeband = 0,
        });
        double Ratio(int y0, int y1)
        {
            double before = 0, after = 0;
            for (var y = y0; y < y1; y++)
            for (var x = 20; x < W - 20; x++)
            {
                var i = (y * W + x) * 4;
                before += Oklab.FromSrgb(px[i + 2], px[i + 1], px[i]).L;
                after += Oklab.FromSrgb(result[i + 2], result[i + 1], result[i]).L;
            }
            return after / before;
        }
        var near = Ratio(170, 230);
        var far = Ratio(50, 100);
        Assert.InRange(near, 0.9, 1.05);                                   // near: exposure kept
        Assert.True(far < near - 0.07, $"Ferne {far:0.000} gegen Nähe {near:0.000}: dichter Dunst wird voll ausgeglichen");
    }
}
