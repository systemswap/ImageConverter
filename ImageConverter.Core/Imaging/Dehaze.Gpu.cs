using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

namespace ImageConverter.Core.Imaging;

// Dehaze on the GPU: the two loops over the whole image (the model per pass, LookTone), with the same formulas as in
// Dehaze.Apply and DehazeLook.LookTone, one thread per pixel. The estimates on the small copy (air light, transmission,
// guided filter) stay on the CPU; their maps are uploaded. Tables (sRGB ↔ linear) as on the CPU; there is no cube root
// on the GPU, so power ⅓ with one Newton step is used. The results differ from the CPU only in rounding (see
// docs/TEMPO.md and the test DehazeTests.Dehaze_GpuAndCpu_Agree).
internal static partial class Dehaze
{
    /// <summary>Constant parameters of the model kernel.</summary>
    internal readonly record struct ModelParams(int Width, int Height, int F, int Sw, int Sh, int HasStrong,
        float Mix, float K, float CastRemoval, float Strength);

    /// <summary>Constant parameters of the LookTone kernel.</summary>
    internal readonly record struct ToneParams(int Width, int Height, int F, int Sw, int Sh, int HasScene, int SceneW, int SceneH,
        int SceneFactor, float Clarity, float Curve, float Vibrance, float VibranceSky);

    // Tables one after another in a single buffer: Srgb.Decode (4097), Oklab ToLinear (4097), Oklab ToSrgb (4097); the
    // sizes must match the CPU tables (BuildGpuTables checks this).
    private const int DecodeSize = 4096;
    private const int DecodeAt = 0, OkLinearAt = DecodeSize + 1, OkSrgbAt = OkLinearAt + TableSizeOk + 1;

    private static float[]? _gpuTables;

    private static float[] GpuTables => _gpuTables ??= BuildGpuTables();

    private static float[] BuildGpuTables()
    {
        if (Srgb.DecodeTableForGpu.Length != DecodeSize + 1 || Oklab.LinearTableForGpu.Length != TableSizeOk + 1
            || Oklab.SrgbTableForGpu.Length != TableSizeOk + 1)
            throw new InvalidOperationException("Tabellengrößen für die Grafikkarte passen nicht.");
        return [.. Srgb.DecodeTableForGpu, .. Oklab.LinearTableForGpu, .. Oklab.SrgbTableForGpu];
    }

    private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ModelParams>? _modelKernel;

    private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ToneParams>? _toneKernel;

    /// <summary>Loaded kernels belong to the GPU accelerator; after it is closed they must be loaded again.</summary>
    internal static void ForgetGpuKernels() => (_modelKernel, _toneKernel) = (null, null);

    /// <summary>Runs the pixel loop of the model on the GPU (physics only: without clarity/sharpening/curve/vibrance).
    /// Returns a buffer from the <see cref="PixelPool"/>, or null if the CPU has to compute it.</summary>
    private static float[]? ModelOnGpu(float[] bgra, int width, int height, int f, int sw, int sh, float[] air, float[] sky,
        (float[] A, float[] B) plain, (float[] A, float[] B) strong, (float[] A, float[] B) gain, float[] airLab, float mix, float k,
        float castRemoval, float strength)
    {
        float[]? result = null;
        var ok = GpuFilters.TryRun(acc =>
        {
            _modelKernel ??= acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ModelParams>(ModelKernel);
            var hasStrong = strong.A is not null;
            using var dIn = acc.Allocate1D(bgra);
            using var dOut = acc.Allocate1D<float>(bgra.Length);
            using var dAir = acc.Allocate1D(air);
            using var dSky = acc.Allocate1D(sky);
            using var dPa = acc.Allocate1D(plain.A);
            using var dPb = acc.Allocate1D(plain.B);
            using var dSa = acc.Allocate1D(hasStrong ? strong.A : plain.A);
            using var dSb = acc.Allocate1D(hasStrong ? strong.B : plain.B);
            using var dGa = acc.Allocate1D(gain.A);
            using var dGb = acc.Allocate1D(gain.B);
            using var dLab = acc.Allocate1D(airLab);
            using var dT = acc.Allocate1D(GpuTables);
            _modelKernel(width * height, dIn.View, dOut.View, dAir.View, dSky.View, dPa.View, dPb.View, dSa.View, dSb.View, dGa.View,
                dGb.View, dLab.View, dT.View,
                new ModelParams(width, height, f, sw, sh, hasStrong ? 1 : 0, mix, k, castRemoval, strength));
            result = PixelPool.Rent(bgra.Length);
            dOut.CopyToCPU(result);
        });
        if (ok) return result;
        PixelPool.Return(result);
        return null;
    }

    /// <summary>Runs LookTone on the GPU, modifying the pixels in place. Returns false if the CPU has to compute
    /// it.</summary>
    private static bool ToneOnGpu(float[] px, int width, int height, int f, int sw, int sh, (float[] A, float[] B) baseL,
        SceneMap? scene, LookSettings s) =>
        GpuFilters.TryRun(acc =>
        {
            _toneKernel ??= acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ToneParams>(ToneKernel);
            float[] one = [0f];
            using var dPx = acc.Allocate1D(px);
            using var dSky = acc.Allocate1D(scene?.Sky ?? one);
            using var dA = acc.Allocate1D(baseL.A ?? one);
            using var dB = acc.Allocate1D(baseL.B ?? one);
            using var dT = acc.Allocate1D(GpuTables);
            _toneKernel(width * height, dPx.View, dSky.View, dA.View, dB.View, dT.View,
                new ToneParams(width, height, f, sw, sh, scene is null ? 0 : 1, scene?.W ?? 1, scene?.H ?? 1, scene?.Factor ?? 1,
                    s.Clarity, s.Curve, s.Vibrance, s.VibranceSky));
            dPx.CopyToCPU(px);
        });

    // ------------------------------------------------------------ Kernels

    private static void ModelKernel(Index1D index, ArrayView<float> bgra, ArrayView<float> result, ArrayView<float> air,
        ArrayView<float> sky, ArrayView<float> plainA, ArrayView<float> plainB, ArrayView<float> strongA, ArrayView<float> strongB,
        ArrayView<float> gainA, ArrayView<float> gainB, ArrayView<float> airLab, ArrayView<float> tables, ModelParams p)
    {
        int pix = index;
        var y = pix / p.Width;
        var x = pix - y * p.Width;
        var i = pix * 4;
        var sy = (y + 0.5f) / p.F - 0.5f;
        var sx = (x + 0.5f) / p.F - 0.5f;
        GpuCell(p.Sw, p.Sh, sx, sy, out var i00, out var i01, out var i10, out var i11, out var fx, out var fy);

        var a0 = Of(air, i00, i01, i10, i11, fx, fy, 0, 3);
        var a1 = Of(air, i00, i01, i10, i11, fx, fy, 1, 3);
        var a2 = Of(air, i00, i01, i10, i11, fx, fy, 2, 3);
        float gB = bgra[i], gG = bgra[i + 1], gR = bgra[i + 2];
        var l0 = Lookup(tables, DecodeAt, DecodeSize, bgra[i]);
        var l1 = Lookup(tables, DecodeAt, DecodeSize, bgra[i + 1]);
        var l2 = Lookup(tables, DecodeAt, DecodeSize, bgra[i + 2]);

        var skyHere = Of(sky, i00, i01, i10, i11, fx, fy, 0, 1);
        // TransmitAt
        var landHere = 1 - XMath.Clamp(skyHere, 0f, 1f);
        var haloHere = XMath.Clamp(skyHere - 1, 0f, 1f);
        var tt = GpuTolerate(XMath.Clamp(Sample(plainA, plainB, i00, i01, i10, i11, fx, fy, gB, gG, gR), 0f, 1f), l0, l1, l2, a0, a1, a2,
            landHere);
        if (p.HasStrong != 0)
            tt = XMath.Min(tt, GpuTolerate(XMath.Clamp(Sample(strongA, strongB, i00, i01, i10, i11, fx, fy, gB, gG, gR), 0f, 1f),
                l0, l1, l2, a0, a1, a2, landHere));
        tt = tt + (1 - tt) * haloHere;
        var land = 1 - XMath.Clamp(skyHere, 0f, 1f);

        var oB = Recover(l0, a0, tt, p.Mix);
        var oG = Recover(l1, a1, tt, p.Mix);
        var oR = Recover(l2, a2, tt, p.Mix);
        var g = XMath.Exp(XMath.Clamp(Sample(gainA, gainB, i00, i01, i10, i11, fx, fy, gB, gG, gR), -0.7f, 1.1f) * p.K);
        if (g > 1)
        {
            var before = Luma(l0, l1, l2);
            g = XMath.Min(g, XMath.Max(1f, before / XMath.Max(Luma(oB, oG, oR), 1e-4f)));
        }
        g = 1 + (g - 1) * land;
        oB *= g; oG *= g; oR *= g;

        GpuFromLinear(l2, l1, l0, out _, out var wasA, out var wasB);
        GpuFromLinear(XMath.Max(0f, oR), XMath.Max(0f, oG), XMath.Max(0f, oB), out var nowL, out var nowA, out var nowB);
        var castShare = XMath.Clamp((1 - tt) * Of(airLab, i00, i01, i10, i11, fx, fy, 0, 3) / XMath.Max(Luma(l0, l1, l2), 1e-4f), 0f,
            MaxCastShare);
        var cast = p.CastRemoval * HueRecovery * p.Mix * land * castShare;
        var baseA = wasA - cast * Of(airLab, i00, i01, i10, i11, fx, fy, 1, 3);
        var baseB = wasB - cast * Of(airLab, i00, i01, i10, i11, fx, fy, 2, 3);
        var c0 = XMath.Sqrt(baseA * baseA + baseB * baseB);
        var c1 = XMath.Sqrt(nowA * nowA + nowB * nowB);
        var chromaCap = 1 + land * (p.Mix * (1 / tt - 1) + p.Strength * ChromaPerUnit);
        var ratio = c0 > 1e-3f ? XMath.Min(c1 / c0, chromaCap) : 1f;
        var L = XMath.Clamp(nowL, 0f, 1f);
        GpuToSrgb(tables, L, baseA * ratio, baseB * ratio, out var sR, out var sG, out var sB);
        result[i] = sB;
        result[i + 1] = sG;
        result[i + 2] = sR;
        result[i + 3] = bgra[i + 3];
    }

    private static void ToneKernel(Index1D index, ArrayView<float> px, ArrayView<float> sky, ArrayView<float> baseA,
        ArrayView<float> baseB, ArrayView<float> tables, ToneParams p)
    {
        int pix = index;
        var y = pix / p.Width;
        var x = pix - y * p.Width;
        var i = pix * 4;
        float b = px[i], g = px[i + 1], r = px[i + 2];
        var skyHere = 0f;
        if (p.HasScene != 0)
        {
            // SceneMap.SkyAt
            var ssx = XMath.Clamp((x + 0.5f) / p.SceneFactor - 0.5f, 0f, (float)(p.SceneW - 1));
            var ssy = XMath.Clamp((y + 0.5f) / p.SceneFactor - 0.5f, 0f, (float)(p.SceneH - 1));
            int x0 = (int)ssx, y0 = (int)ssy;
            int x1 = XMath.Min(x0 + 1, p.SceneW - 1), y1 = XMath.Min(y0 + 1, p.SceneH - 1);
            float sfx = ssx - x0, sfy = ssy - y0;
            var top = sky[y0 * p.SceneW + x0] * (1 - sfx) + sky[y0 * p.SceneW + x1] * sfx;
            var bottom = sky[y1 * p.SceneW + x0] * (1 - sfx) + sky[y1 * p.SceneW + x1] * sfx;
            skyHere = top * (1 - sfy) + bottom * sfy;
        }
        var land = 1 - XMath.Clamp(skyHere, 0f, 1f);
        var halo = XMath.Clamp(skyHere - 1, 0f, 1f);

        GpuFromLinear(Lookup(tables, OkLinearAt, TableSizeOk, r), Lookup(tables, OkLinearAt, TableSizeOk, g),
            Lookup(tables, OkLinearAt, TableSizeOk, b), out var L, out var oa, out var ob);
        if (p.Clarity > 0)
        {
            var sy = (y + 0.5f) / p.F - 0.5f;
            GpuCell(p.Sw, p.Sh, (x + 0.5f) / p.F - 0.5f, sy, out var i00, out var i01, out var i10, out var i11, out var fx, out var fy);
            L += p.Clarity * land * (L - Sample(baseA, baseB, i00, i01, i10, i11, fx, fy, b, g, r));
        }
        L = XMath.Clamp(L, 0f, 1f);
        L = GpuCurve(L, p.Curve * (1 - HaloCurveShare * halo));

        var chroma = XMath.Sqrt(oa * oa + ob * ob);
        var vibrance = p.Vibrance * land + p.VibranceSky * XMath.Min(1f, skyHere) * (1 - halo);
        var factor = (1 + vibrance * (1 - XMath.Min(1f, chroma / VibranceFull)))
                     * (1 + CurveChroma * p.Curve * (1 - halo));
        GpuToSrgb(tables, L, oa * factor, ob * factor, out r, out g, out b);
        px[i] = b; px[i + 1] = g; px[i + 2] = r;
    }

    // ------------------------------------------------------------ Helpers (as on the CPU)

    private const int TableSizeOk = 4096;

    private static void GpuCell(int w, int h, float x, float y, out int i00, out int i01, out int i10, out int i11, out float fx,
        out float fy)
    {
        x = XMath.Clamp(x, 0f, (float)(w - 1));
        y = XMath.Clamp(y, 0f, (float)(h - 1));
        int x0 = (int)x, y0 = (int)y;
        int x1 = XMath.Min(x0 + 1, w - 1), y1 = XMath.Min(y0 + 1, h - 1);
        fx = x - x0;
        fy = y - y0;
        i00 = y0 * w + x0; i01 = y0 * w + x1; i10 = y1 * w + x0; i11 = y1 * w + x1;
    }

    private static float Of(ArrayView<float> d, int i00, int i01, int i10, int i11, float fx, float fy, int c, int ch)
    {
        var top = d[i00 * ch + c] * (1 - fx) + d[i01 * ch + c] * fx;
        var bottom = d[i10 * ch + c] * (1 - fx) + d[i11 * ch + c] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static float Sample(ArrayView<float> a, ArrayView<float> b, int i00, int i01, int i10, int i11, float fx, float fy,
        float gB, float gG, float gR) =>
        Of(a, i00, i01, i10, i11, fx, fy, 0, 3) * gB + Of(a, i00, i01, i10, i11, fx, fy, 1, 3) * gG
        + Of(a, i00, i01, i10, i11, fx, fy, 2, 3) * gR + Of(b, i00, i01, i10, i11, fx, fy, 0, 1);

    private static float Lookup(ArrayView<float> tables, int at, int size, float v)
    {
        var x = XMath.Clamp(v, 0f, 1f) * size;
        var i = (int)x;
        if (i >= size) return tables[at + size];
        return tables[at + i] + (tables[at + i + 1] - tables[at + i]) * (x - i);
    }

    private static float GpuKeep(float v, float air) => v < air
        ? (air - v) / XMath.Max(air - 0 * v, 1e-4f)
        : air < 1 ? (v - air) / (1 - air) : 0;

    private static float GpuSmoothStep(float edge0, float edge1, float x)
    {
        var t = XMath.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    private static float GpuTolerate(float t, float b, float g, float r, float aB, float aG, float aR, float land)
    {
        var smooth = XMath.Max(t, XMath.Max(GpuKeep(b, aB), XMath.Max(GpuKeep(g, aG), GpuKeep(r, aR))));
        if (land <= 0) return XMath.Clamp(smooth, MinTransmission, 1f);

        var dist = XMath.Max(XMath.Abs(b - aB), XMath.Max(XMath.Abs(g - aG), XMath.Abs(r - aR)));
        if (dist < Tolerance) t = XMath.Min(1f, t * Tolerance / XMath.Max(dist, 1e-4f));
        t = XMath.Max(t, XMath.Max(GpuKeep(b, aB), XMath.Max(GpuKeep(g, aG), GpuKeep(r, aR))));
        var over = XMath.Max(b / aB, XMath.Max(g / aG, r / aR));
        t += (1 - t) * GpuSmoothStep(1.05f, 1.2f, over);
        t += (1 - t) * GpuSmoothStep(0.85f, 0.97f, Luma(b, g, r));
        return XMath.Clamp(smooth + (t - smooth) * land, MinTransmission, 1f);
    }

    /// <summary>Cube root: power ⅓ followed by one Newton step (the GPU has no Cbrt).</summary>
    private static float GpuCbrt(float v)
    {
        if (v == 0) return 0;
        var a = XMath.Abs(v);
        var c = XMath.Pow(a, 1f / 3f);
        c -= (c * c * c - a) / (3 * c * c);
        return v < 0 ? -c : c;
    }

    private static void GpuFromLinear(float r, float g, float b, out float L, out float A, out float B)
    {
        var l = GpuCbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = GpuCbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = GpuCbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        L = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
        A = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
        B = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;
    }

    private static void GpuToLinear(float L, float a, float b, out float R, out float G, out float B)
    {
        var l = L + 0.3963377774f * a + 0.2158037573f * b;
        var m = L - 0.1055613458f * a - 0.0638541728f * b;
        var s = L - 0.0894841775f * a - 1.2914855480f * b;
        l = l * l * l; m = m * m * m; s = s * s * s;
        R = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        G = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        B = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
    }

    private static bool GpuInGamut(float r, float g, float b) =>
        r >= -1e-4f && r <= 1.0001f && g >= -1e-4f && g <= 1.0001f && b >= -1e-4f && b <= 1.0001f;

    private static void GpuToSrgb(ArrayView<float> tables, float L, float a, float b, out float sR, out float sG, out float sB)
    {
        L = XMath.Clamp(L, 0f, 1f);
        GpuToLinear(L, a, b, out var r, out var g, out var bl);
        if (!GpuInGamut(r, g, bl))
        {
            float lo = 0, hi = 1;
            for (var k = 0; k < 10; k++)
            {
                var mid = (lo + hi) / 2;
                GpuToLinear(L, a * mid, b * mid, out var mr, out var mg, out var mb);
                if (GpuInGamut(mr, mg, mb)) lo = mid; else hi = mid;
            }
            GpuToLinear(L, a * lo, b * lo, out r, out g, out bl);
        }
        sR = Lookup(tables, OkSrgbAt, TableSizeOk, r);
        sG = Lookup(tables, OkSrgbAt, TableSizeOk, g);
        sB = Lookup(tables, OkSrgbAt, TableSizeOk, bl);
    }

    private static float GpuCurve(float L, float k)
    {
        if (k == 0) return L;
        var x = L < CurvePivot ? 0.5f * L / CurvePivot : 0.5f + 0.5f * (L - CurvePivot) / (1 - CurvePivot);
        x -= k * XMath.Sin(2 * XMath.PI * x) / (2 * XMath.PI);
        return x < 0.5f ? 2 * x * CurvePivot : CurvePivot + (2 * x - 1) * (1 - CurvePivot);
    }
}
