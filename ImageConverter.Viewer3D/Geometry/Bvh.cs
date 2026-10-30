using System.Numerics;
using System.Runtime.CompilerServices;

namespace ImageConverter.Viewer3D.Geometry;

/// <summary>Where a ray meets the mesh.</summary>
/// <param name="Triangle">Index of the triangle in the mesh.</param>
/// <param name="Distance">Distance along the ray (in units of the ray direction's length).</param>
public readonly record struct RayHit(int Triangle, float Distance);

/// <summary>
/// Bounding volume hierarchy over the triangles of a mesh (or a subset of them): finds the triangle under the mouse,
/// tests rays for ambient occlusion and the shortest distance between two sets of triangles, each in about
/// logarithmic time.
/// </summary>
/// <remarks>
/// Built top-down by binning the triangle centres (16 bins along the longest axis, surface area heuristic). A split
/// that would leave less than an eighth on one side is replaced by the split at the median bin, and a range whose
/// centres cannot be told apart is halved by position in the list – so the depth stays logarithmic even for crafted
/// meshes (at most <see cref="MaxDepth"/>), and building and traversing never recurse.
/// </remarks>
public sealed class Bvh
{
    /// <summary>Deepest level; ranges still larger there become one leaf. Never reached by the split rules above for 2³¹ triangles.</summary>
    public const int MaxDepth = 180;

    private const int LeafSize = 4;

    /// <summary>Tolerance of the ray test at triangle edges, in barycentric units.</summary>
    private const float EdgeSlack = 1e-4f;
    private const int Bins = 16;

    private readonly Mesh _mesh;
    private float[] _box = [];      // 6 per node: min x y z, max x y z
    private int[] _first = [];      // leaf: first entry in _tris; inner: index of the left child (right = left + 1)
    private int[] _count = [];      // leaf: number of triangles; inner: 0
    private readonly int[] _tris;

    private Bvh(Mesh mesh, int[] tris)
    {
        _mesh = mesh;
        _tris = tris;
    }

    /// <summary>Number of triangles in the hierarchy.</summary>
    public int TriangleCount => _tris.Length;

    /// <summary>Approximate number of bytes the hierarchy occupies (without the mesh).</summary>
    public long Bytes => (long)_box.Length * 4 + (long)_first.Length * 8 + (long)_tris.Length * 4;

    /// <summary>Builds the hierarchy over all triangles or over the given ones.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="triangles">Triangle indices to include; null for all. The array is used as is (reordered), not copied.</param>
    /// <param name="ct">Cancels building.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Bvh Build(Mesh mesh, int[]? triangles = null, CancellationToken ct = default)
    {
        var tris = triangles ?? Enumerable.Range(0, mesh.TriangleCount).ToArray();
        var bvh = new Bvh(mesh, tris);
        bvh.BuildNodes(ct);
        return bvh;
    }

    /// <summary>A range of <see cref="_tris"/> still to be split, with its bounds (empty if not yet known).</summary>
    private readonly record struct Range(int Node, int Start, int End, int Depth, Box3 Box, Box3 Centres);

    /// <remarks>
    /// The upper levels are split one after the other; the ranges below a share of the triangles per processor are
    /// then built in parallel, each into its own node store, and spliced in afterwards. They cover disjoint parts of
    /// the triangle list, so they never touch the same entries.
    /// </remarks>
    private void BuildNodes(CancellationToken ct)
    {
        var n = _tris.Length;
        var centres = new float[3 * n];
        var root = Box3.Empty;
        var rootCentres = Box3.Empty;
        for (var i = 0; i < n; i++)
        {
            var (a, b, c) = _mesh.Corners(_tris[i]);
            var m = (a + b + c) / 3;
            centres[3 * i] = m.X;
            centres[3 * i + 1] = m.Y;
            centres[3 * i + 2] = m.Z;
            root = root.Including(a).Including(b).Including(c);
            rootCentres = rootCentres.Including(m);
        }

        var store = new NodeStore(Math.Max(1, 2 * (n / LeafSize) + 1));
        var deferred = new List<Range>();
        var deferBelow = n >= 1 << 18 ? n / (2 * Environment.ProcessorCount) : 0;
        Split(store, new Range(0, 0, n, 0, root, rootCentres), centres, deferBelow, deferred, parallelBins: true, ct);

        if (deferred.Count > 0)
        {
            var parts = new NodeStore[deferred.Count];
            try
            {
                Parallel.For(0, deferred.Count, new ParallelOptions { CancellationToken = ct }, i =>
                {
                    var r = deferred[i];
                    var local = new NodeStore(2 * ((r.End - r.Start) / LeafSize) + 1);
                    Split(local, r with { Node = 0 }, centres, 0, null, parallelBins: false, ct);
                    parts[i] = local;
                });
            }
            catch (AggregateException e) when (e.InnerExceptions.All(x => x is OperationCanceledException))
            {
                throw new OperationCanceledException(ct);
            }
            for (var i = 0; i < parts.Length; i++) store.Splice(deferred[i].Node, parts[i]);
        }

        store.Trim();
        _box = store.Box;
        _first = store.First;
        _count = store.Count;
    }

    private void Split(NodeStore store, Range top, float[] centres, int deferBelow, List<Range>? deferred, bool parallelBins, CancellationToken ct)
    {
        var stack = new Stack<Range>();
        stack.Push(top);
        var bins = new BinSet();
        var rightArea = new float[Bins];
        var work = 0L;

        while (stack.Count > 0)
        {
            var (node, start, end, depth, box, cbox) = stack.Pop();
            var count = end - start;
            if ((work += count) > 1 << 22)
            {
                ct.ThrowIfCancellationRequested();
                work = 0;
            }
            if (box.IsEmpty && count > 0) (box, cbox) = Bounds(centres, start, end);

            if (deferred is not null && count <= deferBelow && count > LeafSize)
            {
                deferred.Add(new Range(node, start, end, depth, box, cbox));
                continue;
            }
            store.SetBox(node, box);

            if (count <= LeafSize || depth >= MaxDepth)
            {
                store.First[node] = start;
                store.Count[node] = count;
                continue;
            }

            var extent = cbox.Size;
            var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            var lo = axis == 0 ? cbox.Min.X : axis == 1 ? cbox.Min.Y : cbox.Min.Z;
            var span = axis == 0 ? extent.X : axis == 1 ? extent.Y : extent.Z;

            int mid;
            Box3 leftBox = Box3.Empty, leftCentres = Box3.Empty, rightBox = Box3.Empty, rightCentres = Box3.Empty;
            if (!(span > 0))
            {
                mid = start + count / 2;   // all centres at one spot: halve by position
            }
            else
            {
                var scale = Bins / span;
                FillBins(bins, centres, start, end, axis, lo, scale, parallelBins);

                // Surface area heuristic over the 15 boundaries; areas from the right accumulated first
                var acc = Box3.Empty;
                for (var b = Bins - 1; b > 0; b--)
                {
                    acc = acc.Including(bins.Box[b]);
                    rightArea[b] = Area(acc);
                }
                var best = -1;
                var bestCost = float.MaxValue;
                var left = Box3.Empty;
                var leftCount = 0;
                var medianBin = -1;
                for (var b = 0; b < Bins - 1; b++)
                {
                    left = left.Including(bins.Box[b]);
                    leftCount += bins.Count[b];
                    if (medianBin < 0 && leftCount >= count / 2) medianBin = b;
                    if (leftCount == 0 || leftCount == count) continue;
                    var cost = Area(left) * leftCount + rightArea[b + 1] * (count - leftCount);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = b;
                    }
                }

                // Too lopsided: take the median bin instead, so that the depth stays logarithmic
                if (best >= 0)
                {
                    var bestLeft = 0;
                    for (var b = 0; b <= best; b++) bestLeft += bins.Count[b];
                    if (Math.Min(bestLeft, count - bestLeft) < count / 8) best = medianBin;
                }

                mid = best >= 0 ? Partition(centres, start, end, axis, lo, scale, best) : start;
                if (mid - start < Math.Max(1, count / 8) || end - mid < Math.Max(1, count / 8))
                {
                    mid = start + count / 2;   // centres too close to separate: halve by position, bounds from a scan
                }
                else
                {
                    for (var b = 0; b < Bins; b++)
                    {
                        if (b <= best)
                        {
                            leftBox = leftBox.Including(bins.Box[b]);
                            leftCentres = leftCentres.Including(bins.Centres[b]);
                        }
                        else
                        {
                            rightBox = rightBox.Including(bins.Box[b]);
                            rightCentres = rightCentres.Including(bins.Centres[b]);
                        }
                    }
                }
            }

            var child = store.Allocate();
            store.First[node] = child;
            store.Count[node] = 0;
            stack.Push(new Range(child + 1, mid, end, depth + 1, rightBox, rightCentres));
            stack.Push(new Range(child, start, mid, depth + 1, leftBox, leftCentres));
        }
    }

    /// <summary>Growable node arrays; one per thread while building.</summary>
    private sealed class NodeStore
    {
        public float[] Box;
        public int[] First;
        public int[] Count;
        public int Nodes = 1;   // the root exists from the start

        public NodeStore(int capacity)
        {
            capacity = Math.Max(1, capacity);
            Box = new float[6 * capacity];
            First = new int[capacity];
            Count = new int[capacity];
        }

        public int Allocate()
        {
            Reserve(Nodes + 2);
            var index = Nodes;
            Nodes += 2;
            return index;
        }

        public void SetBox(int node, Box3 b)
        {
            var o = 6 * node;
            Box[o] = b.Min.X;
            Box[o + 1] = b.Min.Y;
            Box[o + 2] = b.Min.Z;
            Box[o + 3] = b.Max.X;
            Box[o + 4] = b.Max.Y;
            Box[o + 5] = b.Max.Z;
        }

        /// <summary>Puts the root of <paramref name="part"/> at <paramref name="node"/> and appends its other nodes.</summary>
        public void Splice(int node, NodeStore part)
        {
            var offset = Nodes - 1;   // part node k ≥ 1 becomes node offset + k
            Reserve(Nodes + part.Nodes - 1);
            for (var k = 0; k < part.Nodes; k++)
            {
                var target = k == 0 ? node : offset + k;
                Array.Copy(part.Box, 6 * k, Box, 6 * target, 6);
                Count[target] = part.Count[k];
                First[target] = part.Count[k] > 0 ? part.First[k] : part.First[k] + offset;
            }
            Nodes += part.Nodes - 1;
        }

        public void Trim()
        {
            Array.Resize(ref Box, 6 * Nodes);
            Array.Resize(ref First, Nodes);
            Array.Resize(ref Count, Nodes);
        }

        private void Reserve(int nodes)
        {
            if (nodes <= First.Length) return;
            var capacity = Math.Max(nodes, First.Length * 3 / 2 + 2);
            Array.Resize(ref Box, 6 * capacity);
            Array.Resize(ref First, capacity);
            Array.Resize(ref Count, capacity);
        }
    }

    /// <summary>Counts, triangle bounds and centre bounds per bin.</summary>
    private sealed class BinSet
    {
        public readonly int[] Count = new int[Bins];
        public readonly Box3[] Box = new Box3[Bins];
        public readonly Box3[] Centres = new Box3[Bins];

        public void Clear()
        {
            Array.Clear(Count);
            Array.Fill(Box, Box3.Empty);
            Array.Fill(Centres, Box3.Empty);
        }

        public void Merge(BinSet other)
        {
            for (var b = 0; b < Bins; b++)
            {
                Count[b] += other.Count[b];
                Box[b] = Box[b].Including(other.Box[b]);
                Centres[b] = Centres[b].Including(other.Centres[b]);
            }
        }
    }

    private const int ParallelBinning = 1 << 16;

    /// <summary>Bins a range; large ranges of the upper levels in parallel chunks.</summary>
    private void FillBins(BinSet bins, float[] centres, int start, int end, int axis, float lo, float scale, bool parallel)
    {
        bins.Clear();
        var count = end - start;
        if (!parallel || count < ParallelBinning)
        {
            Bin(bins, centres, start, end, axis, lo, scale);
            return;
        }
        var chunks = Math.Min(Environment.ProcessorCount, count / (ParallelBinning / 4));
        var parts = new BinSet[chunks];
        Parallel.For(0, chunks, c =>
        {
            var part = parts[c] = new BinSet();
            part.Clear();
            Bin(part, centres, start + (int)((long)count * c / chunks), start + (int)((long)count * (c + 1) / chunks), axis, lo, scale);
        });
        foreach (var part in parts) bins.Merge(part);
    }

    private void Bin(BinSet bins, float[] centres, int start, int end, int axis, float lo, float scale)
    {
        for (var i = start; i < end; i++)
        {
            var bin = Math.Clamp((int)((centres[3 * i + axis] - lo) * scale), 0, Bins - 1);
            bins.Count[bin]++;
            var (a, b, c) = _mesh.Corners(_tris[i]);
            bins.Box[bin] = bins.Box[bin].Including(a).Including(b).Including(c);
            bins.Centres[bin] = bins.Centres[bin].Including(new Vector3(centres[3 * i], centres[3 * i + 1], centres[3 * i + 2]));
        }
    }

    private (Box3 Box, Box3 Centres) Bounds(float[] centres, int start, int end)
    {
        var box = Box3.Empty;
        var cbox = Box3.Empty;
        for (var i = start; i < end; i++)
        {
            var (a, b, c) = _mesh.Corners(_tris[i]);
            box = box.Including(a).Including(b).Including(c);
            cbox = cbox.Including(new Vector3(centres[3 * i], centres[3 * i + 1], centres[3 * i + 2]));
        }
        return (box, cbox);
    }

    private int Partition(float[] centres, int start, int end, int axis, float lo, float scale, int lastLeftBin)
    {
        int i = start, j = end - 1;
        while (i <= j)
        {
            var bin = Math.Clamp((int)((centres[3 * i + axis] - lo) * scale), 0, Bins - 1);
            if (bin <= lastLeftBin)
            {
                i++;
                continue;
            }
            (_tris[i], _tris[j]) = (_tris[j], _tris[i]);
            for (var k = 0; k < 3; k++) (centres[3 * i + k], centres[3 * j + k]) = (centres[3 * j + k], centres[3 * i + k]);
            j--;
        }
        return i;
    }

    private static float Area(Box3 b)
    {
        if (b.IsEmpty) return 0;
        var s = b.Size;
        return s.X * s.Y + s.Y * s.Z + s.Z * s.X;
    }

    private Box3 NodeBox(int node)
    {
        var o = 6 * node;
        return new Box3(new Vector3(_box[o], _box[o + 1], _box[o + 2]), new Vector3(_box[o + 3], _box[o + 4], _box[o + 5]));
    }

    // ------------------------------------------------------------------ rays

    /// <summary>Nearest triangle along a ray (both sides of a triangle count).</summary>
    /// <param name="origin">Start of the ray.</param>
    /// <param name="direction">Direction (need not be unit length).</param>
    /// <param name="maxDistance">Ignore hits farther than this (in units of <paramref name="direction"/>).</param>
    /// <param name="hit">The nearest hit.</param>
    /// <returns>Whether a triangle was hit.</returns>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit) =>
        Trace(origin, direction, maxDistance, anyHit: false, out hit);

    /// <summary>Whether anything lies on the ray within the distance; stops at the first triangle found.</summary>
    public bool Occluded(Vector3 origin, Vector3 direction, float maxDistance) =>
        Trace(origin, direction, maxDistance, anyHit: true, out _);

    private bool Trace(Vector3 origin, Vector3 direction, float maxDistance, bool anyHit, out RayHit hit)
    {
        hit = default;
        if (_tris.Length == 0) return false;
        var inv = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);

        // Start the ray where it enters the model's box: a start far away (orthographic camera) would make the
        // triangle test lose so many digits that a ray along a shared edge slips between both triangles
        if (!RayBox(0, origin, inv, maxDistance, out var enter)) return false;
        var size = Math.Max(_box[3] - _box[0], Math.Max(_box[4] - _box[1], _box[5] - _box[2]));
        var skip = Math.Max(0, enter - 1e-3f * size * direction.Length());
        if (skip <= 0) return TraceFrom(origin, direction, maxDistance, anyHit, out hit);
        var found = TraceFrom(origin + direction * skip, direction, maxDistance - skip, anyHit, out var near);
        hit = found ? near with { Distance = near.Distance + skip } : default;
        return found;
    }

    private bool TraceFrom(Vector3 origin, Vector3 direction, float maxDistance, bool anyHit, out RayHit hit)
    {
        hit = default;
        var inv = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        var best = maxDistance;
        var bestTri = -1;
        Span<int> stack = stackalloc int[MaxDepth + 4];
        var sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            var node = stack[--sp];
            if (!RayBox(node, origin, inv, best, out _)) continue;
            var count = _count[node];
            if (count > 0)
            {
                var first = _first[node];
                for (var i = first; i < first + count; i++)
                {
                    var t = _tris[i];
                    var (a, b, c) = _mesh.Corners(t);
                    if (RayTriangle(origin, direction, a, b, c, out var d) && d < best)
                    {
                        best = d;
                        bestTri = t;
                        if (anyHit)
                        {
                            hit = new RayHit(t, d);
                            return true;
                        }
                    }
                }
                continue;
            }

            // Nearer child last, so that it is taken first
            var l = _first[node];
            var r = l + 1;
            var hl = RayBox(l, origin, inv, best, out var dl);
            var hr = RayBox(r, origin, inv, best, out var dr);
            if (hl && hr)
            {
                if (dl <= dr)
                {
                    stack[sp++] = r;
                    stack[sp++] = l;
                }
                else
                {
                    stack[sp++] = l;
                    stack[sp++] = r;
                }
            }
            else if (hl) stack[sp++] = l;
            else if (hr) stack[sp++] = r;
        }
        if (bestTri < 0) return false;
        hit = new RayHit(bestTri, best);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RayBox(int node, Vector3 o, Vector3 inv, float maxT, out float tNear)
    {
        var k = 6 * node;
        var t1 = (_box[k] - o.X) * inv.X;
        var t2 = (_box[k + 3] - o.X) * inv.X;
        var tmin = Math.Min(t1, t2);
        var tmax = Math.Max(t1, t2);
        t1 = (_box[k + 1] - o.Y) * inv.Y;
        t2 = (_box[k + 4] - o.Y) * inv.Y;
        tmin = Math.Max(tmin, Math.Min(t1, t2));
        tmax = Math.Min(tmax, Math.Max(t1, t2));
        t1 = (_box[k + 2] - o.Z) * inv.Z;
        t2 = (_box[k + 5] - o.Z) * inv.Z;
        tmin = Math.Max(tmin, Math.Min(t1, t2));
        tmax = Math.Min(tmax, Math.Max(t1, t2));
        tNear = tmin;
        // NaN (ray parallel to a slab and starting on it) compares false and lets the node through: safe side
        return !(tmax < Math.Max(tmin, 0)) && !(tmin > maxT);
    }

    /// <summary>Möller–Trumbore ray/triangle test, both sides.</summary>
    /// <remarks>
    /// T. Möller, B. Trumbore: Fast, Minimum Storage Ray/Triangle Intersection. Journal of Graphics Tools 2(1), 1997.
    /// The barycentric limits are widened by <see cref="EdgeSlack"/>: a ray exactly along the shared edge of two
    /// triangles could otherwise miss both through rounding and hit whatever lies behind.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (Math.Abs(det) < 1e-20f) return false;
        var invDet = 1 / det;
        var s = o - a;
        var u = Vector3.Dot(s, p) * invDet;
        if (u < -EdgeSlack || u > 1 + EdgeSlack) return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(d, q) * invDet;
        if (v < -EdgeSlack || u + v > 1 + EdgeSlack) return false;
        t = Vector3.Dot(e2, q) * invDet;
        return t > 0;
    }

    // ------------------------------------------------------------------ distances

    /// <summary>The point of the triangles nearest to a point.</summary>
    /// <param name="p">The point.</param>
    /// <returns>Nearest point, its triangle and the distance; triangle −1 if the hierarchy is empty.</returns>
    public (D3 Point, int Triangle, double Distance) Nearest(D3 p)
    {
        var best = double.MaxValue;
        var bestPoint = D3.Zero;
        var bestTri = -1;
        if (_tris.Length == 0) return (bestPoint, -1, best);
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (BoxDistanceSquared(NodeBox(node), p) >= best * best) continue;
            var count = _count[node];
            if (count > 0)
            {
                for (var i = _first[node]; i < _first[node] + count; i++)
                {
                    var (a, b, c) = _mesh.CornersD(_tris[i]);
                    var q = TriangleDistance.ClosestPoint(p, a, b, c);
                    var d = (q - p).Length;
                    if (d < best)
                    {
                        best = d;
                        bestPoint = q;
                        bestTri = _tris[i];
                    }
                }
                continue;
            }
            var l = _first[node];
            var dl = BoxDistanceSquared(NodeBox(l), p);
            var dr = BoxDistanceSquared(NodeBox(l + 1), p);
            if (dl <= dr)
            {
                stack.Push(l + 1);
                stack.Push(l);
            }
            else
            {
                stack.Push(l);
                stack.Push(l + 1);
            }
        }
        return (bestPoint, bestTri, best);
    }

    /// <summary>Shortest distance between the triangles of two hierarchies over the same mesh.</summary>
    /// <param name="a">First set.</param>
    /// <param name="b">Second set.</param>
    /// <param name="ct">Cancels the search.</param>
    /// <returns>The two nearest points (on <paramref name="a"/> and on <paramref name="b"/>) and their distance; 0 where they touch or cross.</returns>
    public static (D3 OnA, D3 OnB, double Distance) Nearest(Bvh a, Bvh b, CancellationToken ct = default)
    {
        var best = double.MaxValue;
        D3 pa = D3.Zero, pb = D3.Zero;
        if (a._tris.Length == 0 || b._tris.Length == 0) return (pa, pb, best);

        var stack = new Stack<(int A, int B)>();
        stack.Push((0, 0));
        var steps = 0;
        while (stack.Count > 0)
        {
            if ((++steps & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            var (na, nb) = stack.Pop();
            var ba = a.NodeBox(na);
            var bb = b.NodeBox(nb);
            if (BoxBoxDistanceSquared(ba, bb) >= best * best) continue;
            var leafA = a._count[na] > 0;
            var leafB = b._count[nb] > 0;
            if (leafA && leafB)
            {
                for (var i = a._first[na]; i < a._first[na] + a._count[na]; i++)
                for (var j = b._first[nb]; j < b._first[nb] + b._count[nb]; j++)
                {
                    var (a0, a1, a2) = a._mesh.CornersD(a._tris[i]);
                    var (b0, b1, b2) = b._mesh.CornersD(b._tris[j]);
                    var (qa, qb, d) = TriangleDistance.Between(a0, a1, a2, b0, b1, b2);
                    if (d < best)
                    {
                        best = d;
                        pa = qa;
                        pb = qb;
                        if (best == 0) return (pa, pb, 0);
                    }
                }
                continue;
            }
            // Open the larger box (or the one that is not a leaf)
            var splitA = !leafA && (leafB || Area(ba) >= Area(bb));
            if (splitA)
            {
                var l = a._first[na];
                stack.Push((l + 1, nb));
                stack.Push((l, nb));
            }
            else
            {
                var l = b._first[nb];
                stack.Push((na, l + 1));
                stack.Push((na, l));
            }
        }
        return (pa, pb, best);
    }

    private static double BoxDistanceSquared(Box3 b, D3 p)
    {
        var dx = Math.Max(0, Math.Max(b.Min.X - p.X, p.X - b.Max.X));
        var dy = Math.Max(0, Math.Max(b.Min.Y - p.Y, p.Y - b.Max.Y));
        var dz = Math.Max(0, Math.Max(b.Min.Z - p.Z, p.Z - b.Max.Z));
        return dx * dx + dy * dy + dz * dz;
    }

    private static double BoxBoxDistanceSquared(Box3 a, Box3 b)
    {
        var dx = Math.Max(0, Math.Max((double)a.Min.X - b.Max.X, (double)b.Min.X - a.Max.X));
        var dy = Math.Max(0, Math.Max((double)a.Min.Y - b.Max.Y, (double)b.Min.Y - a.Max.Y));
        var dz = Math.Max(0, Math.Max((double)a.Min.Z - b.Max.Z, (double)b.Min.Z - a.Max.Z));
        return dx * dx + dy * dy + dz * dz;
    }
}
