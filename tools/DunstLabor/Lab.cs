using System.Globalization;
using System.Text;
using ImageConverter.Core.Imaging;

// Synthetic haze lab, second version: a generated landscape with hill ridges (depth jumps), trees against the sky, a white
// wall and a bright path, a physically hazed sky with a gradient, a sun with glow and rays, optional noise. The truth is
// known, so the result of the dehaze filter is measured against it: per depth band (ΔE in Oklab, split into ΔL/ΔC/ΔH,
// lightness ratio), sky drift and the fringe next to land, noise in flat areas, and the rays around the sun.
// Usage: lab [csv <file>] – prints a summary (median and worst case per metric) and optionally writes every run.
static class Lab
{
    public const int W = 1600, H = 900, Horizon = 300;

    // ------------------------------------------------------------------ colour helpers

    public static double Enc(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
    public static double Dec(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    public static (double L, double A, double B) Oklab(double r, double g, double b)
    {
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    // ------------------------------------------------------------------ scene

    /// <summary>What a pixel is: sky, land at a depth (1 = horizon) with a material, or a tree.</summary>
    public enum Kind { Sky, Land, Tree, Wall, Path }

    public sealed class Scene
    {
        public double[] Truth = new double[W * H * 3];   // linear RGB without haze (without the sun)
        public double[] Depth = new double[W * H];       // 0 … 1, land only
        public Kind[] Kinds = new Kind[W * H];
        public (int X, int Y) Sun = (-1, -1);
    }

    static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }

    /// <summary>Value noise in 0 … 1 over several octaves.</summary>
    static double Noise(double x, double y, int seed, int octaves = 4, double scale = 64)
    {
        double sum = 0, amp = 1, norm = 0;
        for (var o = 0; o < octaves; o++)
        {
            double fx = x / scale, fy = y / scale;
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            double tx = fx - ix, ty = fy - iy;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            double a = Hash(ix, iy, seed + o), b = Hash(ix + 1, iy, seed + o), c = Hash(ix, iy + 1, seed + o), d = Hash(ix + 1, iy + 1, seed + o);
            sum += amp * ((a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty);
            norm += amp; amp *= 0.5; scale *= 0.5;
        }
        return sum / norm;
    }

    public static Scene Make(int seed, bool withSun)
    {
        var s = new Scene();
        // Ridges: four hill chains at increasing depth; a pixel belongs to the nearest chain whose silhouette covers it
        var ridgeDepth = new[] { 0.95, 0.75, 0.5, 0.25 };
        var ridgeBase = new[] { Horizon + 20, Horizon + 90, Horizon + 200, Horizon + 380 };
        var ridgeHeight = new[] { 60, 90, 120, 160 };
        var skyline = new int[W];
        var ridgeTop = new int[4, W];
        for (var x = 0; x < W; x++)
        {
            skyline[x] = H;
            for (var r = 0; r < 4; r++)
            {
                var top = ridgeBase[r] - (int)(ridgeHeight[r] * Noise(x + 1000 * r, 0, seed * 7 + r, 3, 220 - 40 * r));
                ridgeTop[r, x] = top;
                skyline[x] = Math.Min(skyline[x], top);
            }
        }
        // Trees on the nearest chain, partly above the skyline (gaps of sky between the crowns)
        var treeMask = new bool[W * H];
        var rnd = new Random(seed);
        for (var k = 0; k < 9; k++)
        {
            var cx = 80 + rnd.Next(W - 160);
            var baseY = ridgeTop[3, cx] + 10;
            var crown = 50 + rnd.Next(60);
            for (var b = 0; b < 14; b++)
            {
                double bx = cx + (rnd.NextDouble() - 0.5) * crown * 1.3, by = baseY - crown * 0.3 - rnd.NextDouble() * crown * 1.2;
                var rad = crown * (0.25 + rnd.NextDouble() * 0.3);
                for (var y = (int)(by - rad); y <= by + rad; y++)
                for (var x = (int)(bx - rad); x <= bx + rad; x++)
                {
                    if (x < 0 || x >= W || y < 0 || y >= H) continue;
                    var dd = Math.Sqrt((x - bx) * (x - bx) + (y - by) * (y - by)) / rad;
                    if (dd < 1 && Noise(x, y, seed + 99, 2, 6) > 0.25 * dd) treeMask[y * W + x] = true;
                }
            }
            for (var y = Math.Max(0, baseY - crown / 2); y < Math.Min(H, baseY + 30); y++)
            for (var x = cx - 3; x <= cx + 3; x++) if (x >= 0 && x < W) treeMask[y * W + x] = true;
        }
        // one tall tree at the left edge reaching high into the clear sky (the fringe shows there, not at the horizon)
        for (var b = 0; b < 40; b++)
        {
            double bx = 60 + rnd.NextDouble() * 120, by = 20 + rnd.NextDouble() * 420;
            var rad = 25 + rnd.NextDouble() * 40;
            for (var y = (int)(by - rad); y <= by + rad; y++)
            for (var x = (int)(bx - rad); x <= bx + rad; x++)
            {
                if (x < 0 || x >= W || y < 0 || y >= H) continue;
                var dd = Math.Sqrt((x - bx) * (x - bx) + (y - by) * (y - by)) / rad;
                if (dd < 1 && Noise(x, y, seed + 98, 2, 5) > 0.2 * dd) treeMask[y * W + x] = true;
            }
        }
        var wall = (X0: 900 + rnd.Next(200), Y0: ridgeTop[2, 1000] + 30, Wd: 160, Ht: 45);
        if (withSun) s.Sun = (1180 + rnd.Next(120), 90 + rnd.Next(60));

        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var p = y * W + x;
            var fine = 0.8 + 0.4 * Noise(x, y, seed + 5, 3, 9);
            if (treeMask[p])
            {
                s.Kinds[p] = Kind.Tree; s.Depth[p] = 0.22;
                var g = 0.05 + 0.12 * Noise(x, y, seed + 6, 2, 5);
                (s.Truth[p * 3], s.Truth[p * 3 + 1], s.Truth[p * 3 + 2]) = (g * 0.5, g, g * 0.35);
                continue;
            }
            if (y < skyline[x])
            {
                s.Kinds[p] = Kind.Sky; s.Depth[p] = 1;
                // sky without haze: deep blue at the top, paler towards the horizon
                var u = (double)y / Math.Max(1, skyline[x]);
                (s.Truth[p * 3], s.Truth[p * 3 + 1], s.Truth[p * 3 + 2]) = (0.10 + 0.25 * u, 0.22 + 0.33 * u, 0.55 + 0.25 * u);
                continue;
            }
            var r = 0;
            for (var k = 0; k < 4; k++) if (y >= ridgeTop[k, x]) r = k;
            var foot = r < 3 ? Math.Min(H, ridgeTop[r + 1, x] + 400) : H;
            var v = Math.Clamp((y - ridgeTop[r, x]) / (double)Math.Max(1, foot - ridgeTop[r, x]), 0, 1);
            var near = r < 3 ? ridgeDepth[r + 1] : 0.0;
            s.Depth[p] = ridgeDepth[r] + (near - ridgeDepth[r]) * v;
            s.Kinds[p] = Kind.Land;
            var field = Noise(x, y + 2000 * r, seed + 11 + r, 2, 90);
            (double rr, double gg, double bb) = field < 0.35 ? (0.08, 0.20, 0.05) : field < 0.6 ? (0.18, 0.30, 0.06) : field < 0.8 ? (0.30, 0.22, 0.10) : (0.42, 0.36, 0.12);
            if (r == 3 && y > ridgeTop[3, x] + 200 && Math.Abs(x - (W / 2 + 180 * Math.Sin(y / 120.0))) < 14 + (y - ridgeTop[3, x]) / 40.0)
            {
                s.Kinds[p] = Kind.Path; (rr, gg, bb) = (0.55, 0.50, 0.42); fine = 0.95 + 0.1 * Noise(x, y, seed + 5, 2, 9);
            }
            if (r == 2 && x >= wall.X0 && x < wall.X0 + wall.Wd && y >= wall.Y0 && y < wall.Y0 + wall.Ht)
            {
                s.Kinds[p] = Kind.Wall; (rr, gg, bb) = (0.85, 0.84, 0.80); fine = 0.97 + 0.06 * Noise(x, y, seed + 5, 2, 9);
            }
            (s.Truth[p * 3], s.Truth[p * 3 + 1], s.Truth[p * 3 + 2]) = (rr * fine, gg * fine, bb * fine);
        }
        return s;
    }

    /// <summary>Air light (linear RGB) at a pixel: bluish left, warm right, a little brighter towards the sun.</summary>
    public static (double R, double G, double B) Air(int x, int y, Scene s)
    {
        var u = x / (double)(W - 1);
        double r = 0.55 + 0.40 * u, g = 0.62 + 0.23 * u, b = 0.78 - 0.08 * u;
        if (s.Sun.X >= 0)
        {
            var d = Math.Sqrt((x - s.Sun.X) * (x - s.Sun.X) + (y - s.Sun.Y) * (y - s.Sun.Y));
            var glow = 0.25 * Math.Exp(-d / 350);
            r += glow; g += glow * 0.9; b += glow * 0.7;
        }
        return (Math.Min(1, r), Math.Min(1, g), Math.Min(1, b));
    }

    /// <summary>The sun as light on top of a pixel: clipped core, glow and twelve rays.</summary>
    static double SunLight(Scene s, int x, int y)
    {
        if (s.Sun.X < 0) return 0;
        var d = Math.Sqrt((x - s.Sun.X) * (x - s.Sun.X) + (y - s.Sun.Y) * (y - s.Sun.Y));
        var ang = Math.Atan2(y - s.Sun.Y, x - s.Sun.X);
        var rays = Math.Pow(Math.Abs(Math.Cos(6 * ang)), 8) * 0.35 * Math.Exp(-d / 220);
        return d < 28 ? 2.0 : 1.2 * Math.Exp(-(d - 28) / 60) + rays;
    }

    /// <summary>Hazes the scene: land by its depth, the sky by its own path length; the sun on top; noise after.</summary>
    public static byte[] Hazy(Scene s, double beta, double noiseSigma8, int seed, out double[] transmission)
    {
        var bgra = new byte[W * H * 4];
        transmission = new double[W * H];
        var rnd = new Random(seed * 31 + 7);
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var p = y * W + x;
            var (ar, ag, ab) = Air(x, y, s);
            double t = s.Kinds[p] == Kind.Sky
                ? Math.Exp(-beta * (0.35 + 0.65 * (1 - Math.Min(1, (double)y / Horizon))))   // thin at the top, dense at the horizon
                : Math.Exp(-beta * s.Depth[p]);
            transmission[p] = t;
            double r = s.Truth[p * 3] * t + ar * (1 - t), g = s.Truth[p * 3 + 1] * t + ag * (1 - t), b = s.Truth[p * 3 + 2] * t + ab * (1 - t);
            var sun = SunLight(s, x, y);
            r += sun; g += sun * 0.95; b += sun * 0.8;
            r = Math.Min(1, r); g = Math.Min(1, g); b = Math.Min(1, b);
            if (noiseSigma8 > 0)
            {
                double n1 = Gauss(rnd) * noiseSigma8 / 255, n2 = Gauss(rnd) * noiseSigma8 / 255, n3 = Gauss(rnd) * noiseSigma8 / 255;
                r = Dec(Math.Clamp(Enc(r) + n1, 0, 1)); g = Dec(Math.Clamp(Enc(g) + n2, 0, 1)); b = Dec(Math.Clamp(Enc(b) + n3, 0, 1));
            }
            bgra[p * 4] = (byte)Math.Round(Enc(b) * 255); bgra[p * 4 + 1] = (byte)Math.Round(Enc(g) * 255);
            bgra[p * 4 + 2] = (byte)Math.Round(Enc(r) * 255); bgra[p * 4 + 3] = 255;
        }
        return bgra;
    }

    static double Gauss(Random rnd)
    {
        double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>The truth with the sun added, as the dehazed picture should look (linear RGB, clipped at 1).</summary>
    static (double R, double G, double B) TruthWithSun(Scene s, int x, int y)
    {
        var p = y * W + x;
        var sun = SunLight(s, x, y);
        return (Math.Min(1, s.Truth[p * 3] + sun), Math.Min(1, s.Truth[p * 3 + 1] + sun * 0.95), Math.Min(1, s.Truth[p * 3 + 2] + sun * 0.8));
    }

    // ------------------------------------------------------------------ metrics

    public sealed class Result
    {
        public Dictionary<string, double> Values = new();
    }

    static (double R, double G, double B) Px(byte[] bgra, int p) => (Dec(bgra[p * 4 + 2] / 255.0), Dec(bgra[p * 4 + 1] / 255.0), Dec(bgra[p * 4] / 255.0));

    /// <summary>Distance of every sky pixel to the nearest land or tree pixel (chamfer 3-4, in pixels).</summary>
    static int[] SkyDistance(Scene s)
    {
        const int inf = 1 << 20;
        var d = new int[W * H];
        for (var p = 0; p < d.Length; p++) d[p] = s.Kinds[p] == Kind.Sky ? inf : 0;
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var p = y * W + x; if (d[p] == 0) continue;
            var best = d[p];
            if (x > 0) best = Math.Min(best, d[p - 1] + 3);
            if (y > 0) { best = Math.Min(best, d[p - W] + 3); if (x > 0) best = Math.Min(best, d[p - W - 1] + 4); if (x < W - 1) best = Math.Min(best, d[p - W + 1] + 4); }
            d[p] = best;
        }
        for (var y = H - 1; y >= 0; y--)
        for (var x = W - 1; x >= 0; x--)
        {
            var p = y * W + x; if (d[p] == 0) continue;
            var best = d[p];
            if (x < W - 1) best = Math.Min(best, d[p + 1] + 3);
            if (y < H - 1) { best = Math.Min(best, d[p + W] + 3); if (x < W - 1) best = Math.Min(best, d[p + W + 1] + 4); if (x > 0) best = Math.Min(best, d[p + W - 1] + 4); }
            d[p] = best;
        }
        for (var p = 0; p < d.Length; p++) d[p] /= 3;
        return d;
    }

    /// <summary>Noise: median absolute deviation of the luminance high-pass (3 × 3 mean) over a set of pixels, ×1000.</summary>
    static double FlatNoise(byte[] bgra, List<int> pixels)
    {
        var list = new List<double>(pixels.Count);
        foreach (var p in pixels)
        {
            int x = p % W, y = p / W; if (x < 1 || y < 1 || x >= W - 1 || y >= H - 1) continue;
            double c = 0; var n = 0;
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) { var (r, g, b) = Px(bgra, p + dy * W + dx); c += Luma(r, g, b); n++; }
            var (r0, g0, b0) = Px(bgra, p);
            list.Add(Math.Abs(Luma(r0, g0, b0) - c / n));
        }
        if (list.Count == 0) return 0;
        list.Sort();
        return list[list.Count / 2] * 1000;
    }

    public static Result Measure(Scene s, byte[] hazy, byte[] result, double[] t)
    {
        var res = new Result();
        string[] band = ["fern", "mitte", "nah"];   // by transmission of the land: < 0,35, 0,35–0,7, > 0,7
        var sumE = new double[3]; var sumL = new double[3]; var sumC = new double[3]; var sumH = new double[3]; var ratio = new double[3]; var n = new long[3];
        var dist = SkyDistance(s);
        double skyDrift = 0, skyDriftN = 0;
        var fringeRes = new double[6]; var fringeOrig = new double[6]; var fringeN = new long[6];   // 0–25, 25–75, 75–150, 150–300, 300–600, > 600 px
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var p = y * W + x;
            var (rr, rg, rb) = Px(result, p);
            var o = Oklab(rr, rg, rb);
            if (s.Kinds[p] == Kind.Sky)
            {
                if (s.Sun.X >= 0 && Math.Sqrt((x - s.Sun.X) * (x - s.Sun.X) + (y - s.Sun.Y) * (y - s.Sun.Y)) < 420) continue;
                var (hr, hg, hb) = Px(hazy, p);
                var h = Oklab(hr, hg, hb);
                skyDrift += Math.Sqrt((o.L - h.L) * (o.L - h.L) + (o.A - h.A) * (o.A - h.A) + (o.B - h.B) * (o.B - h.B)) * 100; skyDriftN++;
                var k = dist[p] < 25 ? 0 : dist[p] < 75 ? 1 : dist[p] < 150 ? 2 : dist[p] < 300 ? 3 : dist[p] < 600 ? 4 : 5;
                fringeRes[k] += Math.Sqrt(o.A * o.A + o.B * o.B) * 100; fringeOrig[k] += Math.Sqrt(h.A * h.A + h.B * h.B) * 100; fringeN[k]++;
                continue;
            }
            if (s.Kinds[p] == Kind.Tree) continue;
            if (s.Sun.X >= 0 && Math.Sqrt((x - s.Sun.X) * (x - s.Sun.X) + (y - s.Sun.Y) * (y - s.Sun.Y)) < 420) continue;
            var (tr, tg, tb) = TruthWithSun(s, x, y);
            var tt = Oklab(tr, tg, tb);
            var bnd = t[p] < 0.35 ? 0 : t[p] < 0.7 ? 1 : 2;
            double dL = o.L - tt.L, dA = o.A - tt.A, dB = o.B - tt.B;
            var cO = Math.Sqrt(o.A * o.A + o.B * o.B); var cT = Math.Sqrt(tt.A * tt.A + tt.B * tt.B);
            var dC = cO - cT;
            var dH2 = Math.Max(0, dA * dA + dB * dB - dC * dC);
            sumE[bnd] += Math.Sqrt(dL * dL + dA * dA + dB * dB) * 100; sumL[bnd] += dL * 100; sumC[bnd] += dC * 100; sumH[bnd] += Math.Sqrt(dH2) * 100;
            ratio[bnd] += o.L / Math.Max(tt.L, 1e-3); n[bnd]++;
        }
        for (var b = 0; b < 3; b++)
        {
            if (n[b] == 0) continue;
            res.Values[$"ΔE {band[b]}"] = sumE[b] / n[b];
            res.Values[$"ΔL {band[b]}"] = sumL[b] / n[b];
            res.Values[$"ΔC {band[b]}"] = sumC[b] / n[b];
            res.Values[$"ΔH {band[b]}"] = sumH[b] / n[b];
            res.Values[$"L/L {band[b]}"] = ratio[b] / n[b];
        }
        if (skyDriftN > 0) res.Values["Himmel ΔE"] = skyDrift / skyDriftN;
        double GainAt(int k) => fringeN[k] > 0 && fringeOrig[k] > 0 ? fringeRes[k] / fringeOrig[k] : 1;
        var inner = fringeN[5] > 0 ? GainAt(5) : fringeN[4] > 0 ? GainAt(4) : GainAt(3);
        res.Values["Rand 0-25/innen"] = GainAt(0) / inner;
        res.Values["Rand 25-75/innen"] = GainAt(1) / inner;
        res.Values["Rand 75-150/innen"] = GainAt(2) / inner;
        foreach (var (name, pick) in new (string, Func<int, bool>)[]
        {
            ("Rauschen Weg", p => s.Kinds[p] == Kind.Path), ("Rauschen Wand", p => s.Kinds[p] == Kind.Wall),
            ("Rauschen Himmel", p => s.Kinds[p] == Kind.Sky && dist[p] > 150 && (s.Sun.X < 0 || Math.Abs(p % W - s.Sun.X) > 500)),
            ("Rauschen fern", p => s.Kinds[p] == Kind.Land && t[p] < 0.35),
        })
        {
            var pixels = new List<int>();
            for (var p = 0; p < W * H; p += 3) if (pick(p)) pixels.Add(p);
            var before = FlatNoise(hazy, pixels); var after = FlatNoise(result, pixels);
            if (before >= 0.5) res.Values[name] = after / before;   // only with noise in the input (×1000 units)
        }
        if (s.Sun.X >= 0)
        {
            double sum = 0; var cnt = 0;
            foreach (var ring in new[] { 60, 110, 170, 240 })
            {
                var profR = new double[72]; var profT = new double[72]; var pn = new int[72];
                for (var r = ring - 6; r <= ring + 6; r++)
                for (var a = 0; a < 360; a += 2)
                {
                    int x = (int)Math.Round(s.Sun.X + r * Math.Cos(a * Math.PI / 180)), y = (int)Math.Round(s.Sun.Y + r * Math.Sin(a * Math.PI / 180));
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    var (rr, rg, rb) = Px(result, y * W + x); var (tr, tg, tb) = Px(hazy, y * W + x);
                    var i = a / 5 % 72;
                    profR[i] += Luma(rr, rg, rb); profT[i] += Luma(tr, tg, tb); pn[i]++;
                }
                double mR = 0, mT = 0; var k = 0;
                for (var i = 0; i < 72; i++) if (pn[i] > 0) { profR[i] /= pn[i]; profT[i] /= pn[i]; mR += profR[i]; mT += profT[i]; k++; }
                if (k == 0) continue;
                mR /= k; mT /= k;
                double sR = 0, sT = 0;
                for (var i = 0; i < 72; i++) if (pn[i] > 0) { sR += (profR[i] - mR) * (profR[i] - mR); sT += (profT[i] - mT) * (profT[i] - mT); }
                if (sT > 1e-9) { sum += Math.Sqrt(sR / sT); cnt++; }
            }
            if (cnt > 0) res.Values["Strahlen erhalten"] = sum / cnt;
        }
        return res;
    }

    // ------------------------------------------------------------------ runs

    public sealed record Setting(string Name, Adjustments Adjust);

    public static Setting[] Settings =>
    [
        new("Schätzer 100/3", new Adjustments(Dehaze: 100, DehazePasses: 3, DehazeClarity: 0, DehazeContrast: 0, DehazeVibrance: 0, DehazeSharpen: 0, DehazeBrightness: 0, DehazeDeband: 0)),
        new("Beispiel 36/8", new Adjustments(Dehaze: 36, DehazePasses: 8, DehazeClarity: 42, DehazeContrast: 22, DehazeVibrance: 25, DehazeSharpen: 33, DehazeEdge: 0, DehazeBrightness: 76, DehazeDeband: 55, DehazeCast: 51)),
        new("Beispiel 36/8 H27", new Adjustments(Dehaze: 36, DehazePasses: 8, DehazeClarity: 42, DehazeContrast: 22, DehazeVibrance: 25, DehazeSharpen: 33, DehazeEdge: 0, DehazeBrightness: 27, DehazeDeband: 55, DehazeCast: 51)),
    ];

    public static void Run(string[] args)
    {
        var csv = args.Length > 1 && args[0] == "csv" ? args[1] : null;
        var sb = csv is null ? null : new StringBuilder();
        var all = new Dictionary<string, List<double>>();
        var betas = new[] { 0.0, 0.6, 1.2, 2.0, 3.0, 4.0 };
        var noises = new[] { 0.0, 2.0 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var seed in new[] { 1, 2, 3 })
        foreach (var sun in new[] { false, true })
        {
            var scene = Make(seed, sun);
            foreach (var beta in betas)
            foreach (var noise in noises)
            {
                if (beta == 0 && noise > 0) continue;   // control: no haze, no noise
                var hazy = Hazy(scene, beta, noise, seed, out var t);
                foreach (var setting in Settings)
                {
                    var result = Adjust.Apply(hazy, W, H, setting.Adjust);
                    Result r;
                    if (beta == 0)
                    {
                        // control picture: a haze-free picture must stay (almost) as it is – ΔE against the input
                        double d = 0; long n = 0;
                        for (var p = 0; p < W * H; p += 7)
                        {
                            var (a1, a2, a3) = Px(hazy, p); var (b1, b2, b3) = Px(result, p);
                            var oa = Oklab(a1, a2, a3); var ob = Oklab(b1, b2, b3);
                            d += Math.Sqrt((oa.L - ob.L) * (oa.L - ob.L) + (oa.A - ob.A) * (oa.A - ob.A) + (oa.B - ob.B) * (oa.B - ob.B)) * 100; n++;
                        }
                        r = new Result(); r.Values["Kontrolle ΔE"] = d / n;
                    }
                    else r = Measure(scene, hazy, result, t);
                    foreach (var (k, v) in r.Values)
                    {
                        var key = $"{setting.Name}|{k}";
                        if (!all.TryGetValue(key, out var list)) all[key] = list = [];
                        list.Add(v);
                        sb?.AppendLine(string.Join(';', seed, sun ? 1 : 0, beta.ToString(CultureInfo.InvariantCulture), noise.ToString(CultureInfo.InvariantCulture), setting.Name, k, v.ToString("0.000", CultureInfo.InvariantCulture)));
                    }
                }
            }
        }
        Console.WriteLine($"{all.Values.Sum(l => l.Count)} Messwerte in {sw.Elapsed.TotalSeconds:0} s");
        foreach (var setting in Settings)
        {
            Console.WriteLine($"== {setting.Name}");
            foreach (var (key, list) in all.Where(kv => kv.Key.StartsWith(setting.Name + "|")).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var sorted = list.OrderBy(v => v).ToList();
                var median = sorted[sorted.Count / 2];
                var metric = key[(setting.Name.Length + 1)..];
                var worst = metric.StartsWith("Strahlen") ? sorted[0] : sorted[^1];
                Console.WriteLine($"   {metric,-20} Median {median,7:0.00}   schlechtester {worst,7:0.00}   (n = {list.Count})");
            }
        }
        if (csv is not null) File.WriteAllText(csv, sb!.ToString());
    }
}
