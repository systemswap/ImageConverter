using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>
/// „Ferne schonen“ über Dunst und über die Tiefenkarte. Die Tiefenkarte ist global angemeldet
/// (<see cref="LuminanceRegions.Depth"/>) — deshalb laufen diese Tests für sich allein.
/// </summary>
[Collection(nameof(LuminanceDistanceTests))]
[CollectionDefinition(nameof(LuminanceDistanceTests), DisableParallelization = true)]
public sealed class LuminanceDistanceTests : IDisposable
{
    public LuminanceDistanceTests() => LuminanceRegions.Depth = null;
    public void Dispose() => LuminanceRegions.Depth = null;

    private static float[] Halves(int w, int h, (float, float, float) l, (float, float, float) r) => AdjustTests.Halves(w, h, l, r);
    private static float At(float[] px, int w, int x, int y) => AdjustTests.At(px, w, x, y);

    /// <summary>
    /// Ferne schonen ohne Tiefenkarte: Gleich dunkle Bereiche — satter Vordergrund wird aufgehellt, dunstige Ferne (keine wirklich
    /// dunklen Farbanteile) nicht. Ohne Ferne schonen werden beide gleich behandelt.
    /// </summary>
    [Fact]
    public void MatchDistance_SparesHazyRegions_ButLiftsSaturatedForeground()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.02f, 0.3f, 0.02f), (0.2f, 0.21f, 0.23f));   // satter dunkler Wald | grauer Dunst
        var plain = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100));
        var spared = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchDistance: 100));

        Assert.True(At(plain, w, 300, 100) > At(src, w, 300, 100) + 0.05f);                      // ohne: Dunst heller
        Assert.InRange(At(spared, w, 300, 100), At(src, w, 300, 100) - 0.005f, At(src, w, 300, 100) + 0.005f);
        Assert.Equal(At(plain, w, 50, 100), At(spared, w, 50, 100), 3);                          // Wald wie ohne
        Assert.True(new Adjustments(MatchLuminance: -20, MatchDistance: 80).PrepareKey == Adjustments.None.PrepareKey with { },
            "Abdunkeln braucht keine Karten");
        Assert.True(new Adjustments(MatchDistance: 50).IsNone);
    }

    /// <summary>Ersatz-Tiefenkarte: linke Hälfte nah, rechte fern.</summary>
    private sealed class LeftNear : IDepthEstimator
    {
        public int Calls;
        public float[]? Estimate(float[] bgr, int width, int height)
        {
            Calls++;
            var near = new float[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width / 2; x++) near[y * width + x] = 1;
            return near;
        }
    }

    /// <summary>
    /// Mit Tiefenkarte zählt die Entfernung, nicht der Dunst: gleiche Farbe links (nah) und rechts (fern) — nur links
    /// wird aufgehellt, rechts bleibt es bis an die Kante (die Tiefenkarte gehört zur Obergrenze, kein Hof).
    /// </summary>
    [Fact]
    public void WithDepthMap_DistanceFollowsDepth_AndKeepsFarSideUpToTheEdge()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.15f, 0.25f, 0.12f), (0.15f, 0.25f, 0.12f));   // überall derselbe dunkle Wald
        var depth = new LeftNear();
        LuminanceRegions.Depth = depth;
        var a = new Adjustments(MatchLuminance: 100, MatchDistance: 100);
        var result = Adjust.Apply(src, w, h, a);

        Assert.True(depth.Calls >= 1, "Tiefenkarte nicht benutzt");
        Assert.True(At(result, w, 50, 100) > At(src, w, 50, 100) + 0.1f, "nah nicht aufgehellt");
        for (var x = w / 2 + 2; x < w; x++)
            Assert.InRange(At(result, w, x, 100), At(src, w, x, 100) - 0.004f, At(src, w, x, 100) + 0.004f);

        // Der teure Teil hängt davon ab, ob eine Tiefenkarte da ist (Modell geladen → neu rechnen)
        var withDepth = a.PrepareKey;
        LuminanceRegions.Depth = null;
        Assert.NotEqual(withDepth, a.PrepareKey);
    }
}
