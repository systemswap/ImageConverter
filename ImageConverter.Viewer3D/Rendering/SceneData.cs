using System.Numerics;
using ImageConverter.Viewer3D.Geometry;

namespace ImageConverter.Viewer3D.Rendering;

/// <summary>Ground rectangle under the model with its shadow (one byte per cell, 0 = no shadow).</summary>
/// <param name="Min">Corner with the smallest x and y.</param>
/// <param name="Max">Corner with the largest x and y.</param>
/// <param name="Z">Height of the ground (just below the model).</param>
public sealed record GroundShadow(Vector2 Min, Vector2 Max, float Z, int Size, byte[] Cells);

/// <summary>
/// Prepares what the GPU draws, on the CPU and off the UI thread: edge lines, triangles sorted by face, the soft shadow
/// under the model and the part colours.
/// </summary>
public static class SceneData
{
    /// <summary>Edges drawn as lines in the solid view: where neighbouring triangles meet at more than this angle (degrees).</summary>
    public const double SharpEdgeAngle = 40;

    /// <summary>Vertex pairs of the sharp edges and of open or non-manifold edges.</summary>
    public static int[] SharpEdges(Mesh mesh, MeshTopology topology, CancellationToken ct = default)
    {
        var cos = Math.Cos(SharpEdgeAngle * Math.PI / 180);
        var idx = mesh.Indices;
        var nbr = topology.Neighbours;
        var lines = new List<int>();
        for (var h = 0; h < idx.Length; h++)
        {
            if ((h & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
            var t = h / 3;
            var q = nbr[h];
            if (q >= 0)
            {
                if (q < t) continue;   // each inner edge once
                var a = mesh.AreaNormal(t).Normalized();
                var b = mesh.AreaNormal(q).Normalized();
                if (D3.Dot(a, b) >= cos) continue;
            }
            else if (q == MeshTopology.NonManifold && !FirstOfEdge(mesh, h))
            {
                continue;
            }
            lines.Add(idx[h]);
            lines.Add(idx[h % 3 == 2 ? h - 2 : h + 1]);
        }
        return lines.ToArray();
    }

    /// <summary>Vertex pairs of all edges, each once (wireframe).</summary>
    /// <remarks>
    /// Works from the triangles alone (all edges sorted, duplicates dropped), so that the neighbourhood need not be kept
    /// for a view that is seldom chosen; O(T log T), computed when the wireframe is first shown.
    /// </remarks>
    public static int[] AllEdges(Mesh mesh, CancellationToken ct = default)
    {
        var idx = mesh.Indices;
        var keys = new long[idx.Length];
        for (var h = 0; h < idx.Length; h++)
        {
            var a = idx[h];
            var b = idx[h % 3 == 2 ? h - 2 : h + 1];
            keys[h] = a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a;
        }
        ct.ThrowIfCancellationRequested();
        Array.Sort(keys);
        ct.ThrowIfCancellationRequested();
        var unique = 0;
        for (var i = 0; i < keys.Length; i++)
            if (i == 0 || keys[i] != keys[i - 1]) unique++;
        var lines = new int[2 * unique];
        var w = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0 && keys[i] == keys[i - 1]) continue;
            lines[w++] = (int)(keys[i] >> 32);
            lines[w++] = (int)keys[i];
        }
        return lines;
    }

    /// <summary>Non-manifold edges appear in several triangles; only the copy whose start vertex is the smaller one is drawn.</summary>
    private static bool FirstOfEdge(Mesh mesh, int h)
    {
        var a = mesh.Indices[h];
        var b = mesh.Indices[h % 3 == 2 ? h - 2 : h + 1];
        return a < b;
    }

    /// <summary>
    /// Triangle indices reordered so that each face is one contiguous run: highlighting a face then draws just that run
    /// again, without a buffer per face.
    /// </summary>
    /// <returns>The reordered indices and, per face, the first triangle of its run.</returns>
    public static (int[] Indices, int[] FaceStart) SortedByFace(Mesh mesh, FaceSet faces)
    {
        var idx = mesh.Indices;
        var sorted = new int[idx.Length];
        var start = new int[faces.Faces.Length + 1];
        var w = 0;
        for (var f = 0; f < faces.Faces.Length; f++)
        {
            start[f] = w / 3;
            foreach (var t in faces.TrianglesOf(f))
            {
                sorted[w++] = idx[3 * t];
                sorted[w++] = idx[3 * t + 1];
                sorted[w++] = idx[3 * t + 2];
            }
        }
        start[^1] = w / 3;
        return (sorted, start);
    }

    /// <summary>Part number (0 … 255, repeating) of each vertex, for colouring connected parts.</summary>
    public static byte[] PartOfVertex(Mesh mesh, MeshTopology topology)
    {
        var parts = new byte[mesh.VertexCount];
        var idx = mesh.Indices;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var p = (byte)(topology.ShellOf[t] % 256);
            parts[idx[3 * t]] = parts[idx[3 * t + 1]] = parts[idx[3 * t + 2]] = p;
        }
        return parts;
    }

    /// <summary>Most cell tests for the shadow (about 0.2 s); see <see cref="Shadow"/>.</summary>
    public const long ShadowBudget = 1L << 26;

    /// <summary>
    /// Soft shadow under the model as seen from straight above: each triangle darkens the cells it covers, the more the
    /// closer it is to the ground; then the map is blurred. Linear in the number of triangles (a small triangle touches
    /// one cell).
    /// </summary>
    /// <remarks>
    /// Large triangles lying on top of each other would each test hundreds of cells – a crafted file with millions of
    /// them took over a minute. The work is capped at <see cref="ShadowBudget"/> cell tests; the triangles are visited in a
    /// scattered order, so that a capped shadow still covers the whole model.
    /// </remarks>
    public static GroundShadow Shadow(Mesh mesh, int size = 256, CancellationToken ct = default)
    {
        var box = mesh.Bounds;
        var extent = box.Size;
        var height = Math.Max(extent.Z, 1e-6f);
        var margin = 0.3f * Math.Max(Math.Max(extent.X, extent.Y), height);
        var min = new Vector2(box.Min.X - margin, box.Min.Y - margin);
        var max = new Vector2(box.Max.X + margin, box.Max.Y + margin);
        var span = Math.Max(max.X - min.X, max.Y - min.Y);
        max = min + new Vector2(span);   // square cells
        var cell = span / size;
        var grid = new float[size * size];

        var n = mesh.TriangleCount;
        var step = ScatterStep(n);
        long work = 0;
        for (long k = 0, t = 0; k < n; k++, t = (t + step) % n)
        {
            if ((k & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
            var (a, b, c) = mesh.Corners((int)t);
            var low = Math.Min(a.Z, Math.Min(b.Z, c.Z)) - box.Min.Z;
            var weight = MathF.Exp(-3.5f * low / height);
            if (weight < 0.02f) continue;
            float ax = (a.X - min.X) / cell, ay = (a.Y - min.Y) / cell;
            float bx = (b.X - min.X) / cell, by = (b.Y - min.Y) / cell;
            float cx = (c.X - min.X) / cell, cy = (c.Y - min.Y) / cell;
            var x0 = Math.Clamp((int)MathF.Floor(Math.Min(ax, Math.Min(bx, cx))), 0, size - 1);
            var x1 = Math.Clamp((int)MathF.Floor(Math.Max(ax, Math.Max(bx, cx))), 0, size - 1);
            var y0 = Math.Clamp((int)MathF.Floor(Math.Min(ay, Math.Min(by, cy))), 0, size - 1);
            var y1 = Math.Clamp((int)MathF.Floor(Math.Max(ay, Math.Max(by, cy))), 0, size - 1);
            if (x0 == x1 && y0 == y1)
            {
                Mark(x0, y0);
                continue;
            }
            if ((work += (long)(x1 - x0 + 1) * (y1 - y0 + 1)) > ShadowBudget) break;
            var area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (MathF.Abs(area) < 1e-12f) continue;   // seen edge-on from above: no footprint
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                var w0 = ((bx - px) * (cy - py) - (cx - px) * (by - py)) / area;
                var w1 = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) / area;
                if (w0 >= 0 && w1 >= 0 && w0 + w1 <= 1) Mark(x, y);
            }

            void Mark(int x, int y)
            {
                ref var g = ref grid[y * size + x];
                if (weight > g) g = weight;
            }
        }

        // Three box blurs ≈ a Gaussian; radius a few percent of the ground
        var radius = Math.Max(1, size / 28);
        var tmp = new float[grid.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxBlur(grid, tmp, size, radius, horizontal: true);
            BoxBlur(tmp, grid, size, radius, horizontal: false);
        }
        var cells = new byte[grid.Length];
        for (var i = 0; i < grid.Length; i++) cells[i] = (byte)Math.Clamp(grid[i] * 255 + 0.5f, 0, 255);
        return new GroundShadow(min, max, box.Min.Z - 1e-4f * Math.Max(height, box.Diagonal), size, cells);
    }

    /// <summary>A step coprime to <paramref name="n"/> near the golden ratio of it: t → t + step (mod n) visits all, scattered.</summary>
    private static long ScatterStep(int n)
    {
        if (n <= 2) return 1;
        var step = (long)(n * 0.6180339887) | 1;
        while (Gcd(step, n) != 1) step += 2;
        return step;

        static long Gcd(long a, long b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }
    }

    private static void BoxBlur(float[] src, float[] dst, int size, int r, bool horizontal)
    {
        var norm = 1f / (2 * r + 1);
        for (var line = 0; line < size; line++)
        {
            float sum = 0;
            for (var k = -r; k <= r; k++) sum += Sample(line, Math.Clamp(k, 0, size - 1));
            for (var i = 0; i < size; i++)
            {
                var o = horizontal ? line * size + i : i * size + line;
                dst[o] = sum * norm;
                sum += Sample(line, Math.Min(size - 1, i + r + 1)) - Sample(line, Math.Max(0, i - r));
            }
        }

        float Sample(int line, int i) => horizontal ? src[line * size + i] : src[i * size + line];
    }
}
