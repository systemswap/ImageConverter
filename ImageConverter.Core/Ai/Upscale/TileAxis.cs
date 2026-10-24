namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// Division of one axis into tiles, with blending weights.
/// </summary>
/// <remarks>
/// The tiles are spread evenly and overlap by at least <c>overlap</c> (with <c>align</c> = 1). The weights sum to exactly 1
/// at every position (soft ramps in the overlaps, none at the image border), so a result can be added up tile by tile
/// without full-size sum and weight buffers. In two dimensions the product of the weights of both axes is used; it also
/// sums to 1 everywhere. See docs/FEHLERGEDAECHTNIS.md, No. 61.
/// </remarks>
public sealed class TileAxis
{
    /// <summary>Divides an axis into tiles.</summary>
    /// <param name="length">Length of the axis.</param>
    /// <param name="tile">Tile size (if the axis is shorter: one tile covering everything).</param>
    /// <param name="overlap">Minimum overlap; the ramps have this width.</param>
    /// <param name="align">Starts are rounded down to multiples of this value (VAE: 1 latent pixel = 8 image pixels); the last tile always ends at the axis end.</param>
    public TileAxis(int length, int tile, int overlap, int align = 1)
    {
        Length = length;
        Size = Math.Min(tile, length);
        var n = length <= tile ? 1 : (int)Math.Ceiling((length - overlap) / (double)(tile - overlap));
        Starts = new int[n];
        for (var k = 0; k < n; k++)
        {
            var s = n == 1 ? 0 : (int)Math.Round(k * (length - Size) / (double)(n - 1));
            Starts[k] = Math.Min(length - Size, s / align * align);
        }
        Starts[^1] = length - Size;

        // Raw weights: ramp from the tile edges inwards (not at the image border), then normalise to sum 1 per position
        var ramp = Math.Max(1, overlap);
        var raw = new float[n][];
        var sum = new double[length];
        for (var k = 0; k < n; k++)
        {
            raw[k] = new float[Size];
            for (var i = 0; i < Size; i++)
            {
                var p = Starts[k] + i;
                double r = 1;
                if (Starts[k] > 0) r = Math.Min(r, (i + 0.5) / ramp);
                if (Starts[k] + Size < length) r = Math.Min(r, (Size - i - 0.5) / ramp);
                raw[k][i] = (float)r;
                sum[p] += r;
            }
        }
        Weights = new float[n][];
        for (var k = 0; k < n; k++)
        {
            Weights[k] = new float[Size];
            for (var i = 0; i < Size; i++) Weights[k][i] = (float)(raw[k][i] / sum[Starts[k] + i]);
        }

        // For the intermediate results: last tile that touches a position
        LastCovering = new int[length];
        for (var k = 0; k < n; k++)
        for (var i = 0; i < Size; i++) LastCovering[Starts[k] + i] = k;
    }

    /// <summary>Gets the length of the axis.</summary>
    public int Length { get; }

    /// <summary>Gets the size of each tile (at most the length of the axis).</summary>
    public int Size { get; }

    /// <summary>Gets the start position of each tile, in ascending order.</summary>
    public int[] Starts { get; }

    /// <summary>Gets the number of tiles.</summary>
    public int Count => Starts.Length;

    /// <summary>Gets the weight per tile and position within the tile.</summary>
    public float[][] Weights { get; }

    /// <summary>Gets, per position, the last (highest-numbered) tile that touches it.</summary>
    public int[] LastCovering { get; }

    /// <summary>Returns the same division at <paramref name="factor"/> times the resolution (latent → image), ramps scaled as well.</summary>
    /// <param name="factor">Resolution factor.</param>
    /// <param name="overlap">Ramp width at the coarse resolution; it is multiplied by <paramref name="factor"/>.</param>
    /// <returns>The scaled division.</returns>
    public TileAxis Scaled(int factor, int overlap) => new(this, factor, overlap);

    private TileAxis(TileAxis coarse, int factor, int overlap)
    {
        Length = coarse.Length * factor;
        Size = coarse.Size * factor;
        Starts = coarse.Starts.Select(s => s * factor).ToArray();
        var n = Starts.Length;
        var ramp = Math.Max(1, overlap * factor);
        var raw = new float[n][];
        var sum = new double[Length];
        for (var k = 0; k < n; k++)
        {
            raw[k] = new float[Size];
            for (var i = 0; i < Size; i++)
            {
                double r = 1;
                if (Starts[k] > 0) r = Math.Min(r, (i + 0.5) / ramp);
                if (Starts[k] + Size < Length) r = Math.Min(r, (Size - i - 0.5) / ramp);
                raw[k][i] = (float)r;
                sum[Starts[k] + i] += r;
            }
        }
        Weights = new float[n][];
        for (var k = 0; k < n; k++)
        {
            Weights[k] = new float[Size];
            for (var i = 0; i < Size; i++) Weights[k][i] = (float)(raw[k][i] / sum[Starts[k] + i]);
        }
        LastCovering = new int[Length];
        for (var k = 0; k < n; k++)
        for (var i = 0; i < Size; i++) LastCovering[Starts[k] + i] = k;
    }
}
