namespace ImageConverter.Core.Imaging;

/// <summary>
/// Look je Durchlauf (Phase 4 des Neubaus): Klarheit, Kontrast, Dynamik, feine Schärfe und Kanten —
/// je Wirkung ein Mechanismus, an einer Stelle, nach der Physik (<see cref="Apply(float[], int, int, float, Pass?, Settings, float)"/>
/// mit ausgeschalteter Wahrnehmung). Bis 0.4.51 wirkte jeder Feinregler doppelt: im Dunstfilter
/// (Sinus-Kurve, Dynamik, 3×3-Schärfe) und danach über die Tools-Kette (Kontrast je Kanal, Dynamik,
/// Unscharfmaskierung) — die Dynamik war so zu stark, und die Tools-Schritte trafen auch den Himmel.
/// </summary>
/// <remarks>
/// Die Szenenkarte bestimmt, wo was wirkt: Klarheit und Dynamik in der Landschaft (Dynamik im Himmel
/// nur ein kleiner Anteil, wie bisher über die Tools), die Kurve überall außer im Lichthof (ihre
/// Schulter staucht die Strahlen), Schärfe in Landschaft und Lichthof (die Strahlen sind Struktur),
/// nicht im glatten Himmel.
/// </remarks>
internal static partial class Dehaze
{
    /// <summary>Stärken eines Durchlaufs (schon mit Regler und Dunststärke verrechnet).</summary>
    internal sealed record LookSettings(float Clarity, float Curve, float Vibrance, float VibranceSky, float Sharpen, float Edge)
    {
        public bool IsNone => Clarity <= 0 && Curve == 0 && Vibrance <= 0 && VibranceSky <= 0 && Sharpen <= 0 && Edge <= 0;
    }

    /// <summary>Klarheit, Kurve und Dynamik (vor dem Entstufen), an Ort und Stelle.</summary>
    internal static void LookTone(float[] px, int width, int height, SceneMap? scene, LookSettings s)
    {
        if (s.Clarity <= 0 && s.Curve == 0 && s.Vibrance <= 0 && s.VibranceSky <= 0) return;

        // Grundhelligkeit für die Klarheit: Oklab-L, kantentreu geglättet (Guided Filter auf der kleinen
        // Fassung, Führungsbild das Bild selbst) — ein einfacher Weichzeichner gäbe Lichthöfe an Kanten
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)WorkEdge));
        (float[] A, float[] B) baseL = default;
        int sw = 0, sh = 0;
        if (s.Clarity > 0)
        {
            var (small, w2, h2) = DownscaleLinear(px, width, height, f);
            (sw, sh) = (w2, h2);
            var l = new float[sw * sh];
            Parallel.For(0, sh, row =>
            {
                for (int x = 0, p = row * sw; x < sw; x++, p++) l[p] = Oklab.FromLinear(small[p * 3 + 2], small[p * 3 + 1], small[p * 3]).L;
            });
            baseL = GuidedColor.Coefficients(Encode(small), l, sw, sh, Math.Max(1.0, ClarityPerEdge * Math.Max(sw, sh)), eps: 1e-3f);
        }

        Parallel.For(0, height, y =>
        {
            var sy = (y + 0.5f) / f - 0.5f;
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var i = p * 4;
                float b = px[i], g = px[i + 1], r = px[i + 2];
                var sky = scene?.SkyAt(x, y) ?? 0;
                var land = SceneMap.Land(sky);
                var halo = SceneMap.Halo(sky);

                var (L, oa, ob) = Oklab.FromSrgb(r, g, b);
                if (s.Clarity > 0)
                {
                    var cell = new Cell(sw, sh, (x + 0.5f) / f - 0.5f, sy);
                    L += s.Clarity * land * (L - cell.Sample(baseL, b, g, r));
                }
                L = Math.Clamp(L, 0, 1);
                // Im Lichthof halbe Kurve: ganz ohne fehlte den Strahlen der Kontrast, den bisher die Tools-Kurve
                // gab; voll staucht ihre Schulter die Strahlen
                L = Curve(L, s.Curve * (1 - HaloCurveShare * halo));

                var chroma = MathF.Sqrt(oa * oa + ob * ob);
                var vibrance = s.Vibrance * land + s.VibranceSky * Math.Min(1, sky) * (1 - halo);
                var factor = (1 + vibrance * (1 - Math.Min(1, chroma / VibranceFull)))
                             * (1 + CurveChroma * s.Curve * (1 - halo));   // wie die Kurve je Kanal bis 0.4.51
                (r, g, b) = Oklab.ToSrgb(L, oa * factor, ob * factor);
                px[i] = b; px[i + 1] = g; px[i + 2] = r;
            }
        });
    }

    /// <summary>
    /// Drehpunkt der S-Kurve in Oklab-L. Bis 0.4.51 wirkten zwei Kurven: die Sinus-Kurve um L 0,5 und die
    /// Tools-Kurve je Kanal um sRGB 0,5 (L ≈ 0,6) — zusammen etwa hier. Mit 0,5 allein wurden Bilder mit
    /// dunkler Mitte gut eine Stufe heller als bisher.
    /// </summary>
    private const float CurvePivot = 0.56f;

    /// <summary>Sanfte S-Kurve um <see cref="CurvePivot"/>: beide Hälften auf 0 … ½ bzw. ½ … 1 gestreckt, darauf
    /// die Sinus-Kurve (Steigung im Drehpunkt 1 + <paramref name="k"/>, Enden fest).</summary>
    private static float Curve(float L, float k)
    {
        if (k == 0) return L;
        var x = L < CurvePivot ? 0.5f * L / CurvePivot : 0.5f + 0.5f * (L - CurvePivot) / (1 - CurvePivot);
        x -= k * MathF.Sin(2 * MathF.PI * x) / (2 * MathF.PI);
        return x < 0.5f ? 2 * x * CurvePivot : CurvePivot + (2 * x - 1) * (1 - CurvePivot);
    }

    /// <summary>
    /// Buntheit, die mit der Kurve steigt: Die Tools-Kurve je Kanal (bis 0.4.51) spreizte die Kanäle und hob
    /// so nebenbei die Buntheit; die Kurve auf der Helligkeit tut das nicht — ohne Ausgleich fehlten im
    /// Smog (Photo175) 10 % Farbe. Über mehr Dynamik im Himmel ausgeglichen, kippten blasse Hügel ins Türkis.
    /// </summary>
    private const float CurveChroma = 0.3f;

    /// <summary>Anteil der Schärfe im glatten Himmel.</summary>
    private const float SkySharpenShare = 0.4f;

    /// <summary>Um diesen Anteil wird die Kurve im Lichthof zurückgenommen.</summary>
    private const float HaloCurveShare = 0.25f;

    /// <summary>Feine Schärfe und Kanten (nach dem Entstufen), an Ort und Stelle — im glatten Himmel nur ein Anteil.</summary>
    internal static void LookDetail(float[] px, int width, int height, SceneMap? scene, LookSettings s)
    {
        if (s.Sharpen > 0) WeightedSharpen(px, width, height, scene, s.Sharpen, FineSigma);
        if (s.Edge > 0) WeightedSharpen(px, width, height, scene, s.Edge, EdgeSigma);
    }

    private static void WeightedSharpen(float[] px, int width, int height, SceneMap? scene, float amount, double sigma)
    {
        var sharp = Sharpen.UnsharpMask(px, width, height, amount, sigma);
        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var sky = scene?.SkyAt(x, y) ?? 0;
                // Landschaft und Lichthof voll (die Strahlen sind Struktur), im Himmel ein Anteil: voll betonte
                // er JPEG-Blöcke und Rauschen (Stufen im Himmel 0,79 statt 0,59), ganz ohne fehlten die Strahlen
                var land = SceneMap.Land(sky);
                var halo = SceneMap.Halo(sky);
                var w = land + (1 - land) * SkySharpenShare + halo * (1 - SkySharpenShare);
                if (w <= 0) continue;
                var i = p * 4;
                for (var c = 0; c < 3; c++) px[i + c] += (sharp[i + c] - px[i + c]) * w;
            }
        });
        PixelPool.Return(sharp);
    }
}
