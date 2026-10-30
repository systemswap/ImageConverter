namespace ImageConverter.Viewer3D.Geometry;

/// <summary>
/// Neighbourhood of the triangles and the figures derived from it: open and non-manifold edges, connected parts
/// (shells), surface area and volume.
/// </summary>
/// <remarks>
/// Edges are matched by sorting all 3 × T half-edges by their vertex pair, which takes O(T log T) whatever the
/// mesh looks like; matching through the triangles around a vertex would be faster on clean meshes, but a crafted
/// file with a few vertices shared by millions of triangles would make it quadratic.
/// </remarks>
public sealed class MeshTopology
{
    /// <summary>Edge without a neighbour (open mesh).</summary>
    public const int Open = -1;

    /// <summary>Edge shared by more than two triangles; treated as a border when finding faces.</summary>
    public const int NonManifold = -2;

    private MeshTopology(int[] neighbours, int openEdges, int nonManifoldEdges, int[] shellOf, int shellCount, double area, double volume)
    {
        Neighbours = neighbours;
        OpenEdges = openEdges;
        NonManifoldEdges = nonManifoldEdges;
        ShellOf = shellOf;
        ShellCount = shellCount;
        Area = area;
        Volume = volume;
    }

    /// <summary>
    /// For each triangle t and edge k (from corner k to corner k + 1), the triangle on the other side at
    /// <c>3 t + k</c>, or <see cref="Open"/> / <see cref="NonManifold"/>.
    /// </summary>
    public int[] Neighbours { get; }

    /// <summary>Edges that belong to one triangle only.</summary>
    public int OpenEdges { get; }

    /// <summary>Edges shared by three or more triangles.</summary>
    public int NonManifoldEdges { get; }

    /// <summary>The model is closed: every edge has exactly two triangles, so the volume is meaningful.</summary>
    public bool IsClosed => OpenEdges == 0 && NonManifoldEdges == 0;

    /// <summary>Connected part of each triangle (0 … <see cref="ShellCount"/> − 1), parts touching at a corner count as one.</summary>
    public int[] ShellOf { get; }

    public int ShellCount { get; }

    /// <summary>Surface area in square file units.</summary>
    public double Area { get; }

    /// <summary>Enclosed volume in cubic file units (absolute value of the signed volume); only meaningful when closed.</summary>
    public double Volume { get; }

    /// <summary>Computes the neighbourhood and the figures.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static MeshTopology Build(Mesh mesh, CancellationToken ct = default)
    {
        var t = mesh.TriangleCount;
        var idx = mesh.Indices;
        var halfEdges = 3 * t;

        // Key: smaller vertex in the high half, larger in the low half – equal keys are the same edge.
        var keys = new long[halfEdges];
        var ids = new int[halfEdges];
        for (var h = 0; h < halfEdges; h++)
        {
            var a = idx[h];
            var b = idx[h % 3 == 2 ? h - 2 : h + 1];
            keys[h] = a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a;
            ids[h] = h;
        }
        ct.ThrowIfCancellationRequested();
        Array.Sort(keys, ids);
        ct.ThrowIfCancellationRequested();

        var neighbours = new int[halfEdges];
        int open = 0, nonManifold = 0;
        for (var i = 0; i < halfEdges;)
        {
            var j = i + 1;
            while (j < halfEdges && keys[j] == keys[i]) j++;
            switch (j - i)
            {
                case 1:
                    neighbours[ids[i]] = Open;
                    open++;
                    break;
                case 2:
                    neighbours[ids[i]] = ids[i + 1] / 3;
                    neighbours[ids[i + 1]] = ids[i] / 3;
                    break;
                default:
                    for (var k = i; k < j; k++) neighbours[ids[k]] = NonManifold;
                    nonManifold++;
                    break;
            }
            i = j;
        }
        keys = null!;
        ids = null!;
        ct.ThrowIfCancellationRequested();

        var (shellOf, shells) = Shells(mesh);
        ct.ThrowIfCancellationRequested();

        double area = 0, volume = 0;
        for (var i = 0; i < t; i++)
        {
            var (a, b, c) = mesh.CornersD(i);
            area += D3.Cross(b - a, c - a).Length * 0.5;
            volume += D3.Dot(a, D3.Cross(b, c)) / 6;   // signed tetrahedron to the origin
        }
        return new MeshTopology(neighbours, open, nonManifold, shellOf, shells, area, Math.Abs(volume));
    }

    /// <summary>Connected parts via union–find over the vertices (corners shared count as connected).</summary>
    private static (int[] ShellOf, int Count) Shells(Mesh mesh)
    {
        var parent = new int[mesh.VertexCount];
        for (var v = 0; v < parent.Length; v++) parent[v] = v;
        var idx = mesh.Indices;
        for (var i = 0; i < idx.Length; i += 3)
        {
            Union(parent, idx[i], idx[i + 1]);
            Union(parent, idx[i], idx[i + 2]);
        }

        // Compact numbering in the order the parts first appear
        var number = new int[mesh.VertexCount];
        Array.Fill(number, -1);
        var shellOf = new int[mesh.TriangleCount];
        var count = 0;
        for (var t = 0; t < shellOf.Length; t++)
        {
            var root = Find(parent, idx[3 * t]);
            if (number[root] < 0) number[root] = count++;
            shellOf[t] = number[root];
        }
        return (shellOf, count);
    }

    private static int Find(int[] parent, int v)
    {
        // Path halving: keeps the trees flat without recursion
        while (parent[v] != v)
        {
            parent[v] = parent[parent[v]];
            v = parent[v];
        }
        return v;
    }

    private static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a == b) return;
        // Smaller root below the larger: deterministic, and with path halving the depth stays small
        if (a < b) parent[b] = a;
        else parent[a] = b;
    }
}
