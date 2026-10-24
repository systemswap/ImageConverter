using System.Runtime.CompilerServices;
using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Intermediate results of <see cref="ImageExporter.Render(MagickImage, ExportOptions, WorkPlan?, LookCache?)"/> for runs
/// that render the same image again and again with slightly different settings, such as the size estimate after every
/// slider movement.
/// </summary>
/// <remarks>
/// The stages match the preview: scaled + denoised → white balance + dehaze → shadows/highlights → light and colour;
/// sharpening and grain are applied every time afterwards (fast). A slider further down the pipeline therefore recomputes
/// only from its own stage onwards; the result is the same as without the cache. Not thread-safe: not for concurrent
/// runs. The stored buffers are owned by the cache. <see cref="Dispose"/> (or
/// <see cref="Clear"/>) releases the stored scaled images.
/// </remarks>
public sealed class LookCache : IDisposable
{
    // A few entries per stage, most recently used first. The estimate renders two samples per state (two sizes): two
    // entries hold the current state, four for dehaze, so that the previous dehaze state is still available.
    private const int InputSize = 2, DehazedSize = 4, PreparedSize = 2, AdjustedSize = 2;

    private readonly List<(InputKey Key, Input Value)> _input = [];
    private readonly List<((InputKey, Adjustments), float[])> _dehazed = [];
    private readonly List<((InputKey, Adjustments), Adjust.Prepared)> _prepared = [];
    private readonly List<((InputKey, Adjustments), float[])> _adjusted = [];

    /// <summary>The source image, compared by identity (MagickImage equality would otherwise compare the pixels).</summary>
    internal readonly struct SourceRef(MagickImage image) : IEquatable<SourceRef>
    {
        public MagickImage Image { get; } = image;
        public bool Equals(SourceRef other) => ReferenceEquals(Image, other.Image);
        public override bool Equals(object? obj) => obj is SourceRef other && Equals(other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(Image);
    }

    /// <summary>Everything the scaled and denoised image depends on.</summary>
    internal readonly record struct InputKey(SourceRef Source, int Width, int Height, ResampleFilter Filter, bool Gamma,
        double Denoise, double ColorDenoise);

    /// <summary>Scaled image (for profiles, alpha, density) and its pixels after denoising.</summary>
    internal sealed record Input(MagickImage Resized, float[] Pixels, bool Processed);

    /// <summary>Number of stages that were recomputed in the last run (for tests).</summary>
    internal int Computed { get; private set; }

    internal void BeginRun() => Computed = 0;

    internal Input GetInput(InputKey key, Func<Input> make) =>
        Get(_input, key, make, InputSize, e => e.Resized.Dispose());

    internal float[] GetDehazed(InputKey key, Adjustments dehazeKey, Func<float[]> make) =>
        Get(_dehazed, (key, dehazeKey), make, DehazedSize, null);

    internal Adjust.Prepared GetPrepared(InputKey key, Adjustments prepareKey, Func<Adjust.Prepared> make) =>
        Get(_prepared, (key, prepareKey), make, PreparedSize, null);

    internal float[] GetAdjusted(InputKey key, Adjustments adjust, Func<float[]> make) =>
        Get(_adjusted, (key, adjust), make, AdjustedSize, null);

    private T Get<TKey, T>(List<(TKey Key, T Value)> list, TKey key, Func<T> make, int size, Action<T>? evicted)
        where TKey : IEquatable<TKey>
    {
        var i = list.FindIndex(e => e.Key.Equals(key));
        if (i >= 0)
        {
            var hit = list[i];
            list.RemoveAt(i);
            list.Insert(0, hit);
            return hit.Value;
        }
        var value = make();
        Computed++;
        list.Insert(0, (key, value));
        while (list.Count > size)
        {
            evicted?.Invoke(list[^1].Value);
            list.RemoveAt(list.Count - 1);
        }
        return value;
    }

    /// <summary>Discards all entries and disposes the stored scaled images; call this when the source image changes.</summary>
    public void Clear()
    {
        foreach (var (_, input) in _input) input.Resized.Dispose();
        _input.Clear();
        _dehazed.Clear();
        _prepared.Clear();
        _adjusted.Clear();
    }

    /// <summary>Releases all entries, see <see cref="Clear"/>.</summary>
    public void Dispose() => Clear();
}
