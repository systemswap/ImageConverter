using System.Text.Json;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Abnahme;

/// <summary>abnahme.json im Arbeitsordner: Testbilder mit ihren Messbereichen (Anteile 0–1: x0, y0, x1, y1).</summary>
public sealed class Config
{
    /// <summary>Name des Referenzlaufs (Ordner im Arbeitsordner).</summary>
    public string Referenz { get; set; } = "0.4.45";
    /// <summary>Längste Kante, auf die gerechnet wird (kleinere Bilder bleiben, wie sie sind).</summary>
    public int Kante { get; set; } = 4000;
    /// <summary>Bild für die Leistungsmessung (am besten das größte).</summary>
    public string Leistungsbild { get; set; } = "";
    public List<Picture> Bilder { get; set; } = [];
    public List<SynthPicture> Synthetisch { get; set; } = [];

    public static Config Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!
            : throw new FileNotFoundException($"abnahme.json fehlt: {path}");
}

public sealed class Picture
{
    public string Name { get; set; } = "";
    public string Datei { get; set; } = "";
    /// <summary>Ferne Landschaft (Wiese, Hügel): Farbton, Buntheit, Detail.</summary>
    public double[]? Ferne { get; set; }
    /// <summary>Glatter Himmel: Treppen.</summary>
    public double[]? Himmel { get; set; }
    /// <summary>Sonne im Bild: Strahlen messen.</summary>
    public bool Sonne { get; set; }
}

public sealed class SynthPicture
{
    public string Name { get; set; } = "";
    /// <summary>Klares Bild; der Dunst wird künstlich darübergelegt.</summary>
    public string Datei { get; set; } = "";
}

public sealed record Case(string Name, Adjustments? Adjust, double Denoise = 0, double Sharpen = 0, double SharpenSigma = 1);

/// <summary>Die Fälle der Abnahme — Teil der Definition, darum hier im Code und nicht in der Konfiguration.</summary>
public static class Cases
{
    public const string Original = "Original";

    public static readonly Case[] All =
    [
        new(Original, null),
        new("Dunst100x3", new Adjustments(Dehaze: 100)),
        new("Dunst60x4", new Adjustments(Dehaze: 60, DehazePasses: 4)),
        new("Dunst200x3", new Adjustments(Dehaze: 200)),
        new("Dunst100x1", new Adjustments(Dehaze: 100, DehazePasses: 1)),
        new("DunstMinus50", new Adjustments(Dehaze: -50)),
        // Einstellungen des Benutzers (Turmlandschaft und Panorama, beispielhaft)
        new("KombiTurm", new Adjustments(Brightness: 4, Contrast: 24, Shadows: 63, Highlights: -59, Dehaze: 59, Vibrance: 20, Saturation: -17)),
        new("KombiPanorama", new Adjustments(Shadows: 27, Highlights: -71, Dehaze: 211), Sharpen: 1.4, SharpenSigma: 1.3),
        new("ToolsOhneDunst", new Adjustments(Brightness: 10, Contrast: 15, Shadows: 30, Highlights: -30, Vibrance: 15, Warmth: 10)),
        new("RauschenSchaerfen", null, Denoise: 0.4, Sharpen: 0.8, SharpenSigma: 1.0),
    ];

    public static readonly Case[] Synthetic =
    [
        new("Dunstig", null),
        new("Dunst100x1", new Adjustments(Dehaze: 100, DehazePasses: 1)),
        new("Dunst100x3", new Adjustments(Dehaze: 100)),
        new("Dunst60x4", new Adjustments(Dehaze: 60, DehazePasses: 4)),
    ];

    public static readonly string[] Performance = ["Dunst100x3", "Dunst100x1", "KombiPanorama", "ToolsOhneDunst"];
}

/// <summary>Messwerte eines Falls; nicht gemessene bleiben null.</summary>
public sealed class Metrics
{
    public double Kontrast { get; set; }
    public double Helligkeit { get; set; }
    public double? FerneFarbton { get; set; }
    public double? FerneBuntheit { get; set; }
    public double? FerneDetail { get; set; }
    public double? Treppen { get; set; }
    /// <summary>Stufenhöhe im Himmel in Tonstufen: Sägezahn des quer gemittelten Profils um seinen geglätteten Verlauf (Effektivwert). Glatter Verlauf mit Korn ≈ 0,1; Treppe aus 1-Stufen-Schritten ≈ 0,3.</summary>
    public double? Stufen { get; set; }
    /// <summary>Schwankung um die Sonne (Strahlen) im Verhältnis zum Original, 1 = wie das Original.</summary>
    public double? Strahlen { get; set; }
    /// <summary>Abweichung zur Referenz: mittleres ΔE (Oklab × 100) und 99,5-%-Wert, nach 3×3-Mittel (Dither zählt nicht).</summary>
    public double? AbweichungMittel { get; set; }
    public double? AbweichungSpitze { get; set; }
    // Künstlicher Dunst: Abstand zur Wahrheit
    public double? WahrheitAlle { get; set; }
    public double? WahrheitOben { get; set; }
    public double? WahrheitGruenFarbton { get; set; }
}

public static class Measure
{
    public static double Luma(byte[] px, int p) => 0.2126 * px[p * 4 + 2] + 0.7152 * px[p * 4 + 1] + 0.0722 * px[p * 4];

    public static Metrics All(byte[] px, int w, int h, Picture image, (int X, int Y)? sun, double? raysOriginal)
    {
        var m = new Metrics();
        double s = 0, s2 = 0; var n = 0;
        for (var p = 0; p < w * h; p += 3) { var v = Luma(px, p); s += v; s2 += v * v; n++; }
        m.Helligkeit = s / n;
        m.Kontrast = Math.Sqrt(Math.Max(0, s2 / n - m.Helligkeit * m.Helligkeit));

        if (image.Ferne is { } f)
        {
            var (x0, y0, x1, y1) = Box(f, w, h);
            double sa = 0, sb = 0, c = 0, det = 0; int k = 0, kd = 0;
            for (var y = Math.Max(1, y0); y < Math.Min(h - 1, y1); y++)
            for (var x = Math.Max(1, x0); x < Math.Min(w - 1, x1); x++)
            {
                var p = y * w + x;
                det += Math.Abs(Luma(px, p) - (Luma(px, p - 1) + Luma(px, p + 1) + Luma(px, p - w) + Luma(px, p + w)) / 4);
                kd++;
                var lab = Ok.FromBytes(px, p);
                var ch = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
                var hue = Ok.Hue(lab.A, lab.B);
                if (hue is < 80 or > 175 || ch < 0.02) continue;   // grün bis gelbgrün
                sa += lab.A; sb += lab.B; c += ch; k++;
            }
            m.FerneDetail = det / Math.Max(1, kd);
            if (k > 50) { m.FerneFarbton = Ok.Hue((float)(sa / k), (float)(sb / k)); m.FerneBuntheit = c / k; }
        }

        if (image.Himmel is { } sky)
        {
            var (x0, y0, x1, y1) = Box(sky, w, h);
            double gaps = 0; var cols = 0;
            for (var x = x0; x < x1; x += Math.Max(1, (x1 - x0) / 60))
            for (var ch = 0; ch < 3; ch++)
            {
                var seen = new bool[256]; int lo = 255, hi = 0;
                for (var y = y0; y < y1; y++) { var v = px[(y * w + x) * 4 + ch]; seen[v] = true; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                if (hi - lo < 3) continue;
                var used = 0; for (var v = lo; v <= hi; v++) if (seen[v]) used++;
                gaps += 1 - (double)used / (hi - lo + 1); cols++;
            }
            m.Treppen = cols > 0 ? 100 * gaps / cols : 0;
            m.Stufen = Steps(px, w, x0, y0, x1, y1);
        }

        if (sun is { } sn && raysOriginal is > 0)
            m.Strahlen = Rays(px, w, h, sn) / raysOriginal.Value;
        return m;
    }

    /// <summary>
    /// Stufenhöhe: Je Streifen von 32 Pixeln Breite das senkrechte Helligkeitsprofil (über die Breite
    /// gemittelt — Korn mittelt sich weg, Bänder im Himmel liegen quer dazu), davon der Effektivwert
    /// der Abweichung zum über ±1 % der Höhe geglätteten Profil. Eine Treppe hinterlässt einen
    /// Sägezahn (Stufe/√12), ein weicher Verlauf fast nichts.
    /// </summary>
    static double Steps(byte[] px, int w, int x0, int y0, int x1, int y1)
    {
        const int band = 32;
        var rows = y1 - y0;
        var r = Math.Max(4, rows / 100);
        if (rows < 4 * r || x1 - x0 < band) return 0;
        double total = 0; var strips = 0;
        for (var xs = x0; xs + band <= x1; xs += band)
        {
            var profile = new double[rows];
            for (var y = 0; y < rows; y++)
            {
                double s = 0;
                for (var x = xs; x < xs + band; x++) s += Luma(px, (y0 + y) * w + x);
                profile[y] = s / band;
            }
            double sq = 0; var n = 0;
            for (var y = r; y < rows - r; y++)
            {
                double s = 0;
                for (var k = -r; k <= r; k++) s += profile[y + k];
                var d = profile[y] - s / (2 * r + 1);
                sq += d * d; n++;
            }
            total += Math.Sqrt(sq / n); strips++;
        }
        return strips > 0 ? total / strips : 0;
    }

    static (int, int, int, int) Box(double[] b, int w, int h) =>
        ((int)(b[0] * w), (int)(b[1] * h), (int)(b[2] * w), (int)(b[3] * h));

    /// <summary>Hellste Stelle der oberen Bildhälfte (Mittel über 33 × 33 Pixel).</summary>
    public static (int X, int Y) FindSun(byte[] px, int w, int h)
    {
        int bx = 0, by = 0; double best = -1;
        for (var y = 16; y < h / 2; y += 8)
        for (var x = 16; x < w - 16; x += 8)
        {
            double s = 0;
            for (var dy = -16; dy <= 16; dy += 8) for (var dx = -16; dx <= 16; dx += 8) s += Luma(px, (y + dy) * w + x + dx);
            if (s > best) { best = s; bx = x; by = y; }
        }
        return (bx, by);
    }

    /// <summary>Strahlen: Schwankung der Helligkeit über 96 Winkel auf Ringen um die Sonne (3, 5 und 8 % der Kante), gemittelt.</summary>
    public static double Rays(byte[] px, int w, int h, (int X, int Y) sun)
    {
        var edge = Math.Max(w, h);
        var total = 0.0; var rings = 0;
        foreach (var share in new[] { 0.03, 0.05, 0.08 })
        {
            var r = share * edge;
            var v = new List<double>();
            for (var k = 0; k < 96; k++)
            {
                var a = k * Math.PI / 48;
                int x = sun.X + (int)(r * Math.Cos(a)), y = sun.Y + (int)(r * Math.Sin(a));
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                v.Add(Luma(px, y * w + x));
            }
            if (v.Count < 20) continue;
            var mean = v.Average();
            total += Math.Sqrt(v.Average(q => (q - mean) * (q - mean)));
            rings++;
        }
        return rings > 0 ? total / rings : 0;
    }

    /// <summary>Abweichung zweier gleich großer Bilder nach 3 × 3-Mittel: mittleres ΔE und 99,5-%-Wert (Oklab × 100).</summary>
    public static (double Mean, double Peak) Difference(byte[] a, byte[] b, int w, int h)
    {
        var d = new List<double>(w * h / 4);
        for (var y = 1; y < h - 1; y += 2)
        for (var x = 1; x < w - 1; x += 2)
        {
            var la = Ok.Blurred(a, w, x, y);
            var lb = Ok.Blurred(b, w, x, y);
            d.Add(100 * Math.Sqrt((la.L - lb.L) * (la.L - lb.L) + (la.A - lb.A) * (la.A - lb.A) + (la.B - lb.B) * (la.B - lb.B)));
        }
        d.Sort();
        return (d.Average(), d[(int)(d.Count * 0.995)]);
    }
}

/// <summary>Künstlicher Dunst über einem klaren Bild (bekannte Wahrheit) — wie die Laborumgebung bis 0.4.39.</summary>
public static class Synthetic
{
    static float Lin(byte v) { var x = v / 255f; return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f); }
    static byte Enc(float l) { l = Math.Clamp(l, 0, 1); var x = l <= 0.0031308f ? 12.92f * l : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f; return (byte)(x * 255 + 0.5f); }

    /// <summary>Durchlässigkeit von 1 (unten) bis 0,35 (oben), Luftlicht links bläulich, rechts warm.</summary>
    public static (byte[] Truth, byte[] Hazy, int W, int H) Make(MagickImage clear)
    {
        var truth = ImageOps.ToBgra(clear);
        var (w, h) = ((int)clear.Width, (int)clear.Height);
        var hazy = new byte[truth.Length];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = y * w + x;
            var depth = 1 - (float)y / (h - 1);
            var t = 1 - 0.65f * depth * depth;
            var fx = (float)x / (w - 1);
            float aB = 0.45f + 0.35f * fx, aG = 0.40f + 0.45f * fx, aR = 0.38f + 0.57f * fx;
            hazy[p * 4] = Enc(Lin(truth[p * 4]) * t + aB * (1 - t));
            hazy[p * 4 + 1] = Enc(Lin(truth[p * 4 + 1]) * t + aG * (1 - t));
            hazy[p * 4 + 2] = Enc(Lin(truth[p * 4 + 2]) * t + aR * (1 - t));
            hazy[p * 4 + 3] = 255;
        }
        return (truth, hazy, w, h);
    }

    public static Metrics Measure(byte[] truth, byte[] px, int w, int h)
    {
        double all = 0, top = 0, hue = 0; int nAll = 0, nTop = 0, nGreen = 0;
        for (var p = 0; p < w * h; p += 3)
        {
            var a = Ok.FromBytes(truth, p);
            var b = Ok.FromBytes(px, p);
            var d = 100 * Math.Sqrt((a.L - b.L) * (a.L - b.L) + (a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B));
            all += d; nAll++;
            if (p / w < h / 3) { top += d; nTop++; }
            var hueT = Ok.Hue(a.A, a.B);
            if (hueT is > 95 and < 175 && Math.Sqrt(a.A * a.A + a.B * a.B) > 0.03 && p / w < h / 2)
            {
                var dh = Ok.Hue(b.A, b.B) - hueT;
                if (dh > 180) dh -= 360; if (dh < -180) dh += 360;
                hue += dh; nGreen++;
            }
        }
        return new Metrics
        {
            WahrheitAlle = all / nAll, WahrheitOben = top / Math.Max(1, nTop),
            WahrheitGruenFarbton = nGreen > 0 ? hue / nGreen : null,
        };
    }
}

/// <summary>Bericht auf der Konsole: Werte, Änderung zur Referenz, ✗ wo es schlechter wurde.</summary>
public static class Report
{
    public static int Failures;

    public static void Header() =>
        Console.WriteLine($"{"Fall",-18} {"Kontrast",9} {"Hell",7} {"Ferne h°",9} {"Bunt",7} {"Detail",7} {"Treppen",8} {"Stufen",7} {"Strahlen",9} {"ΔE ref",7} {"ΔE99",6}");

    public static void Line(string name, Metrics m, Metrics? r)
    {
        var cells = new List<string>
        {
            Cell(m.Kontrast, r?.Kontrast, "F1", 9, rel: 0.05),
            Cell(m.Helligkeit, r?.Helligkeit, "F1", 7, rel: 0.05),
            Cell(m.FerneFarbton, r?.FerneFarbton, "F1", 9, abs: 3, worse: d => Math.Abs(d) > 5),
            Cell(m.FerneBuntheit, r?.FerneBuntheit, "F3", 7, rel: 0.08),
            Cell(m.FerneDetail, r?.FerneDetail, "F2", 7, rel: 0.10),
            Cell(m.Treppen, r?.Treppen, "F1", 8, abs: 0.5, worse: d => d > 1.0),
            Cell(m.Stufen, r?.Stufen, "F2", 7, abs: 0.03, worse: d => d > 0.06),
            Cell(m.Strahlen, r?.Strahlen, "F2", 9, abs: 0.05, worse: d => d < -0.08),
            Cell(m.AbweichungMittel, null, "F2", 7),
            Cell(m.AbweichungSpitze, null, "F1", 6),
        };
        Console.WriteLine($"{name,-18} {string.Join(" ", cells)}");
    }

    public static void SynthHeader() =>
        Console.WriteLine($"{"Fall",-18} {"ΔE alle",9} {"ΔE oben",9} {"Grün Δh°",9}");

    public static void SynthLine(string name, Metrics m, Metrics? r) =>
        Console.WriteLine($"{name,-18} {Cell(m.WahrheitAlle, r?.WahrheitAlle, "F2", 9, abs: 0.3, worse: d => d > 0.8)} "
                          + $"{Cell(m.WahrheitOben, r?.WahrheitOben, "F2", 9, abs: 0.3, worse: d => d > 1.0)} "
                          + $"{Cell(m.WahrheitGruenFarbton, r?.WahrheitGruenFarbton, "F1", 9, abs: 2, worse: d => Math.Abs(d) > 4)}");

    /// <summary>Wert, dahinter ≈ (wie die Referenz), ↑/↓ (geändert) oder ✗ (schlechter).</summary>
    static string Cell(double? v, double? r, string fmt, int width, double rel = 0, double abs = 0, Func<double, bool>? worse = null)
    {
        if (v is null) return new string(' ', width - 1) + "–";
        var text = v.Value.ToString(fmt);
        if (r is null) return text.PadLeft(width);
        var d = v.Value - r.Value;
        var limit = Math.Max(abs, rel * Math.Abs(r.Value));
        string mark;
        if (worse?.Invoke(d) == true) { mark = "✗"; Failures++; }
        else if (Math.Abs(d) <= limit) mark = "≈";
        else mark = d > 0 ? "↑" : "↓";
        return (text + mark).PadLeft(width);
    }
}

/// <summary>Vergleichsbilder: Referenz über Aktuell, darunter die Abweichung achtfach verstärkt.</summary>
public static class Compare
{
    public static void Pictures(string root, string refName, string name, Config config)
    {
        var dir = Path.Combine(root, name, "vergleich");
        Directory.CreateDirectory(dir);
        foreach (var image in config.Bilder)
        foreach (var c in new[] { "Dunst100x3", "KombiPanorama" })
        {
            var a = Path.Combine(root, refName, $"{image.Name}_{c}.png");
            var b = Path.Combine(root, name, $"{image.Name}_{c}.png");
            if (!File.Exists(a) || !File.Exists(b)) continue;
            using var ra = new MagickImage(a);
            using var rb = new MagickImage(b);
            if (ra.Width != rb.Width || ra.Height != rb.Height) continue;
            using var diff = (MagickImage)ra.Clone();
            diff.Composite(rb, CompositeOperator.Difference);
            diff.Evaluate(Channels.RGB, EvaluateOperator.Multiply, 8);
            using var col = new MagickImageCollection { ra.Clone(), rb.Clone(), diff.Clone() };
            using var stack = col.AppendVertically();
            stack.Resize(Math.Min(2000u, stack.Width), 0);
            stack.Quality = 92;
            stack.Write(Path.Combine(dir, $"{image.Name}_{c}_Referenz-Aktuell-Abweichung.jpg"));
        }
    }
}

/// <summary>Oklab (Ottosson) für die Messung — eigenständig, damit sie nicht von der geprüften Fassung abhängt.</summary>
public static class Ok
{
    static readonly float[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var v = i / 255.0;
        return (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
    }).ToArray();

    static float Lin(float v255)
    {
        var v = Math.Clamp(v255, 0, 255);
        var i = (int)v;
        return i >= 255 ? Table[255] : Table[i] + (Table[i + 1] - Table[i]) * (v - i);
    }

    public static (float L, float A, float B) FromBytes(byte[] px, int p) =>
        FromLinear(Table[px[p * 4 + 2]], Table[px[p * 4 + 1]], Table[px[p * 4]]);

    public static (float L, float A, float B) Blurred(byte[] px, int w, int x, int y)
    {
        float r = 0, g = 0, b = 0;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var p = ((y + dy) * w + x + dx) * 4;
            b += px[p]; g += px[p + 1]; r += px[p + 2];
        }
        return FromLinear(Lin(r / 9), Lin(g / 9), Lin(b / 9));
    }

    public static (float L, float A, float B) FromLinear(float r, float g, float b)
    {
        var l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    public static double Hue(float a, float b)
    {
        var h = Math.Atan2(b, a) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }
}
