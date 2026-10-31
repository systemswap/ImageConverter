using System.Numerics;
using System.Runtime.CompilerServices;

namespace ImageConverter.Viewer3D.Geometry;

/// <summary>Axis-aligned box.</summary>
public readonly record struct Box3(Vector3 Min, Vector3 Max)
{
    /// <summary>A box that contains nothing; growing it by a point gives that point.</summary>
    public static readonly Box3 Empty = new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

    public bool IsEmpty => Min.X > Max.X;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>Length of the diagonal; 0 for an empty box.</summary>
    public float Diagonal => Size.Length();

    public Box3 Including(Vector3 p) => new(Vector3.Min(Min, p), Vector3.Max(Max, p));

    /// <summary>Smallest and largest dot product of the corners with a direction: the box's extent along it.</summary>
    public (float Front, float Back) Extent(Vector3 direction)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? Min.X : Max.X, (i & 2) == 0 ? Min.Y : Max.Y, (i & 4) == 0 ? Min.Z : Max.Z);
            var t = Vector3.Dot(corner, direction);
            lo = Math.Min(lo, t);
            hi = Math.Max(hi, t);
        }
        return (lo, hi);
    }

    public Box3 Including(Box3 b) => new(Vector3.Min(Min, b.Min), Vector3.Max(Max, b.Max));
}

/// <summary>
/// Triangle mesh with shared vertices: each corner of a triangle refers to a vertex by index, so neighbouring triangles
/// share their corners. That halves the memory of a plain triangle list and gives the neighbourhood that faces are
/// found with.
/// </summary>
public sealed class Mesh
{
    /// <summary>Creates a mesh from arrays of exactly the right length.</summary>
    /// <param name="positions">x, y, z of each vertex (length 3 × vertex count).</param>
    /// <param name="indices">Three vertex indices per triangle (length 3 × triangle count), each below the vertex count.</param>
    /// <exception cref="ArgumentException">The lengths are no multiple of three or an index is out of range.</exception>
    public Mesh(float[] positions, int[] indices)
    {
        if (positions.Length % 3 != 0 || indices.Length % 3 != 0)
            throw new ArgumentException("Lengths must be multiples of three.");
        var vertexCount = positions.Length / 3;
        foreach (var i in indices)
            if ((uint)i >= (uint)vertexCount) throw new ArgumentException("Vertex index out of range.");

        Positions = positions;
        Indices = indices;
        VertexCount = vertexCount;
        TriangleCount = indices.Length / 3;

        var box = Box3.Empty;
        for (var v = 0; v < vertexCount; v++) box = box.Including(Vertex(v));
        Bounds = box;
    }

    /// <summary>x, y, z of each vertex.</summary>
    public float[] Positions { get; }

    /// <summary>Three vertex indices per triangle.</summary>
    public int[] Indices { get; }

    public int VertexCount { get; }

    public int TriangleCount { get; }

    /// <summary>Box around all vertices; empty for a mesh without vertices.</summary>
    public Box3 Bounds { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 Vertex(int v) => new(Positions[3 * v], Positions[3 * v + 1], Positions[3 * v + 2]);

    /// <summary>Corners of a triangle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector3 A, Vector3 B, Vector3 C) Corners(int t) =>
        (Vertex(Indices[3 * t]), Vertex(Indices[3 * t + 1]), Vertex(Indices[3 * t + 2]));

    /// <summary>Corners of a triangle in double precision.</summary>
    public (D3 A, D3 B, D3 C) CornersD(int t)
    {
        var (a, b, c) = Corners(t);
        return (D3.From(a), D3.From(b), D3.From(c));
    }

    /// <summary>Unnormalised normal of a triangle (length = twice its area), in double precision.</summary>
    public D3 AreaNormal(int t)
    {
        var (a, b, c) = CornersD(t);
        return D3.Cross(b - a, c - a);
    }

    /// <summary>Approximate number of bytes the arrays occupy.</summary>
    public long Bytes => (long)Positions.Length * 4 + (long)Indices.Length * 4;
}
