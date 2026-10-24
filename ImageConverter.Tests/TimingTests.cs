using System.Diagnostics;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>
/// Timing measurements need an idle machine: this collection never runs in
/// parallel with other tests (the estimation tests would otherwise load all cores).
/// </summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingCollection;

[Collection("Timing")]
public class TimingTests
{
    [Fact]
    public void IsFastEnough_ForLivePreview()
    {
        // 2048 × 1365 ≈ preview size. Target: well below 100 ms; the 250 ms limit is generous for CI and Debug builds.
        const int w = 2048, h = 1365;
        var src = new byte[w * h * 4];
        new Random(1).NextBytes(src);
        Sharpen.UnsharpMask(src, w, h, 1, 2); // warm-up

        // Best of three runs: a single measurement fluctuates when the machine is busy
        // with something else (e.g. directly after the build).
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
