using System.Diagnostics;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>
/// Zeitmessungen brauchen einen ruhigen Rechner: diese Sammlung läuft nie
/// parallel zu anderen Tests (die Schätzungstests belasten sonst alle Kerne).
/// </summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingCollection;

[Collection("Timing")]
public class TimingTests
{
    [Fact]
    public void IsFastEnough_ForLivePreview()
    {
        // 2048 × 1365 ≈ Vorschaugröße. Ziel: deutlich unter 100 ms (großzügig wegen CI/Debug).
        const int w = 2048, h = 1365;
        var src = new byte[w * h * 4];
        new Random(1).NextBytes(src);
        Sharpen.UnsharpMask(src, w, h, 1, 2); // aufwärmen

        // Bester von drei Läufen: eine einzelne Messung schwankt, wenn der Rechner
        // gerade anderweitig beschäftigt ist (z. B. direkt nach dem Build).
        var best = long.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            Sharpen.UnsharpMask(src, w, h, 1, 2);
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }

        Assert.True(best < 250, $"{best} ms");
    }
}
