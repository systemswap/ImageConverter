using System.Diagnostics;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai;

/// <summary>Zwischenstand während einer KI-Berechnung: kleines Bild (8-Bit-BGRA) zum Anzeigen.</summary>
public sealed record AiPreviewFrame(byte[] Bgra, int Width, int Height);

/// <summary>
/// Liefert höchstens alle <see cref="Interval"/> einen Zwischenstand — das Zusammensetzen
/// kostet Zeit, die sonst der Berechnung fehlt.
/// </summary>
internal sealed class AiPreviewSink(Action<AiPreviewFrame>? target, int maxEdge)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(400);
    private readonly Stopwatch _since = Stopwatch.StartNew();

    public bool IsDue => target is not null && _since.Elapsed >= Interval;

    /// <summary>
    /// Baut das Vorschaubild aus einem Bild der Größe <paramref name="width"/> × <paramref name="height"/>;
    /// <paramref name="pixel"/> liefert RGBA 0…1 für eine Bildkoordinate.
    /// </summary>
    public void Emit(int width, int height, PixelReader pixel)
    {
        if (target is null) return;
        var k = Math.Min(1.0, maxEdge / (double)Math.Max(width, height));
        int pw = Math.Max(1, (int)Math.Round(width * k)), ph = Math.Max(1, (int)Math.Round(height * k));
        var bgra = new byte[pw * ph * 4];

        Parallel.For(0, ph, ComputeLimits.Options, py =>
        {
            Span<float> rgba = stackalloc float[4];
            var y = Math.Min(height - 1, (int)((py + 0.5) / k));
            for (var px = 0; px < pw; px++)
            {
                var x = Math.Min(width - 1, (int)((px + 0.5) / k));
                pixel(x, y, rgba);
                var i = (py * pw + px) * 4;
                bgra[i] = ToByte(rgba[2]);
                bgra[i + 1] = ToByte(rgba[1]);
                bgra[i + 2] = ToByte(rgba[0]);
                bgra[i + 3] = ToByte(rgba[3]);
            }
        });

        target(new AiPreviewFrame(bgra, pw, ph));
        _since.Restart();
    }

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    internal delegate void PixelReader(int x, int y, Span<float> rgba);
}
