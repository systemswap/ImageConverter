using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>Rechteck in Pixeln des Originals (das Ergebnis ist <see cref="UpscaleResult.Factor"/>-mal so groß).</summary>
public readonly record struct SourceRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Ergebnis eines Upscalers in vierfacher Größe, roh (so wie das Modell es liefert), dazu das Original. Treue und Details
/// werden erst beim Zusammensetzen eingerechnet (<see cref="Compose"/>, <see cref="ComposeRegion"/>) — die Regler lassen
/// sich ändern, ohne das Modell neu zu rechnen. Ein Alphakanal kommt nie aus dem Modell, sondern mit Lanczos aus dem
/// Original. Während der Rechnung ist das Ergebnis schon da und füllt sich; fertige Flächen meldet der Lauf einzeln.
/// </summary>
public sealed class UpscaleResult
{
    public const int Factor = 4;

    private readonly ushort[] _source;   // Original, verschachtelt (RGB oder RGBA)
    private readonly int _channels;
    private readonly MagickImage _meta;  // Profile und Auflösung (1 × 1 Pixel)

    // Farbangleich je Kanal (PiSA-SR: AdaIN), beim Lesen des Roh-Ergebnisses angewandt: v · Gain + Offset
    private readonly float[] _gain = [1, 1, 1];
    private readonly float[] _offset = [0, 0, 0];

    internal UpscaleResult(MagickImage source, ushort[] sourcePixels, int channels)
    {
        (_source, _channels) = (sourcePixels, channels);
        SourceWidth = (int)source.Width;
        SourceHeight = (int)source.Height;
        Raw = new ushort[(long)Width * Height * 3];
        _meta = new MagickImage(MagickColors.Black, 1, 1);
        lock (source)
        {
            _meta.Density = source.Density;
            ImageExporter.CopyProfiles(source, _meta);
        }
    }

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int Width => SourceWidth * Factor;
    public int Height => SourceHeight * Factor;
    public bool HasAlpha => _channels == 4;

    /// <summary>Roh-Ergebnis des Modells (RGB, ×4, 16 Bit), wird vom Lauf gefüllt.</summary>
    internal ushort[] Raw { get; }

    internal void SetColorFix(int channel, float gain, float offset) => (_gain[channel], _offset[channel]) = (gain, offset);

    /// <summary>
    /// Speicherbedarf grob in Bytes für ein Original dieser Größe: Roh-Ergebnis (RGB, 16 Bit), zusammengesetztes Ergebnis
    /// (bis RGBA) und das Bild daraus (ImageMagick, 16 Bit) — je in vierfacher Kantenlänge.
    /// </summary>
    public static long MemoryNeed(int sourceWidth, int sourceHeight) => (long)sourceWidth * sourceHeight * Factor * Factor * (6 + 8 + 8);

    /// <summary>Fertiges Bild in vierfacher Größe.</summary>
    /// <param name="fidelity">0 … 1: Treue-Sicherung (1 = verkleinert ergibt das Ergebnis wieder das Original).</param>
    /// <param name="details">0 … 1: Anteil der KI gegenüber einfachem Vergrößern (Lanczos); 1 = nur KI.</param>
    public MagickImage Compose(double fidelity, double details, CancellationToken ct = default)
    {
        var pixels = ComposeRegion(new SourceRect(0, 0, SourceWidth, SourceHeight), fidelity, details, ct);
        var image = AiEnhancer.WritePixels(pixels, Width, Height, HasAlpha ? "RGBA" : "RGB");
        image.Density = new Density(_meta.Density.X * Factor, _meta.Density.Y * Factor, _meta.Density.Units);
        ImageExporter.CopyProfiles(_meta, image);
        return image;
    }

    /// <summary>
    /// Ein Ausschnitt fertig zusammengesetzt: Pixel (verschachtelt RGB bzw. RGBA, 16 Bit) für das Rechteck ×4.
    /// Für die Treue-Sicherung wird ein Rand um den Ausschnitt mitgelesen — höchstens bis <paramref name="known"/>: während
    /// der Rechnung ist das Roh-Ergebnis daneben noch leer (sonst helle Linien an den Kachelrändern).
    /// </summary>
    public ushort[] ComposeRegion(SourceRect region, double fidelity, double details, CancellationToken ct = default,
        SourceRect? known = null)
    {
        var k = known ?? new SourceRect(0, 0, SourceWidth, SourceHeight);
        int sw = SourceWidth, sh = SourceHeight, ow = Width, ch = _channels;
        var f = (float)Math.Clamp(fidelity, 0, 1);
        var d = (float)Math.Clamp(details, 0, 1);
        int regionW = region.Width * Factor, regionH = region.Height * Factor;
        var output = new ushort[(long)regionW * regionH * ch];
        const int core = 128, margin = 16;   // in Quellpixeln; Rückprojektion (3 Runden) reicht ca. 15 Pixel weit
        var tilesX = (region.Width + core - 1) / core;
        var tilesY = (region.Height + core - 1) / core;
        float Src(int c, int x, int y) =>
            _source[((long)Math.Clamp(y, 0, sh - 1) * sw + Math.Clamp(x, 0, sw - 1)) * ch + c] / 65535f;

        Parallel.For(0, tilesX * tilesY, new ParallelOptions { CancellationToken = ct }, t =>
        {
            int cx = region.X + t % tilesX * core, cy = region.Y + t / tilesX * core;
            int cw = Math.Min(core, region.X + region.Width - cx), chh = Math.Min(core, region.Y + region.Height - cy);
            int rx = Math.Max(k.X, cx - margin), ry = Math.Max(k.Y, cy - margin);
            int rw = Math.Min(k.X + k.Width, cx + cw + margin) - rx, rh = Math.Min(k.Y + k.Height, cy + chh + margin) - ry;
            int bw = rw * Factor, bh = rh * Factor;

            var raw = new float[3 * bw * bh];
            for (var c = 0; c < 3; c++)
            {
                var (g, o) = (_gain[c] / 65535f, _offset[c]);
                for (var y = 0; y < bh; y++)
                {
                    var row = ((long)(ry * Factor + y) * ow + rx * Factor) * 3 + c;
                    var dst = c * bw * bh + y * bw;
                    for (var x = 0; x < bw; x++) raw[dst + x] = Raw[row + x * 3] * g + o;
                }
            }

            var e = raw;
            if (f > 0)
            {
                var s = new float[3 * rw * rh];
                for (var c = 0; c < 3; c++)
                for (var y = 0; y < rh; y++)
                for (var x = 0; x < rw; x++) s[c * rw * rh + y * rw + x] = Src(c, rx + x, ry + y);
                var bp = (float[])raw.Clone();
                Resample4.BackProject(bp, s, rw, rh);
                if (f >= 1) e = bp;
                else for (var i = 0; i < raw.Length; i++) raw[i] += f * (bp[i] - raw[i]);
            }
            float[]? plain = d < 1 || ch == 4
                ? Resample4.Up(Src, ch, cx * Factor, cy * Factor, cw * Factor, chh * Factor, Resample4.Kernel.Lanczos)
                : null;

            int ox = (cx - rx) * Factor, oy = (cy - ry) * Factor, pw = cw * Factor, ph = chh * Factor;
            int dx0 = (cx - region.X) * Factor, dy0 = (cy - region.Y) * Factor;
            for (var y = 0; y < ph; y++)
            for (var x = 0; x < pw; x++)
            {
                var di = ((long)(dy0 + y) * regionW + dx0 + x) * ch;
                var si = (oy + y) * bw + ox + x;
                var pi = y * pw + x;
                for (var c = 0; c < 3; c++)
                {
                    var v = e[c * bw * bh + si];
                    if (d < 1) v = plain![c * pw * ph + pi] + d * (v - plain[c * pw * ph + pi]);
                    output[di + c] = (ushort)(Math.Clamp(v, 0f, 1f) * 65535f + 0.5f);
                }
                if (ch == 4) output[di + 3] = (ushort)(Math.Clamp(plain![3 * pw * ph + pi], 0f, 1f) * 65535f + 0.5f);
            }
        });
        return output;
    }
}
