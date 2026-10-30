using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImageConverter.Viewer3D.Geometry;

namespace ImageConverter.Tests;

/// <summary>Builds small, exactly known models as triangle lists and writes them as binary or text STL.</summary>
internal static class StlTestModels
{
    public readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C);

    /// <summary>Box with outward normals, 12 triangles.</summary>
    public static List<Tri> Box(Vector3 min, Vector3 max)
    {
        Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        var list = new List<Tri>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            list.Add(new Tri(a, b, c));
            list.Add(new Tri(a, c, d));
        }
        Quad(P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0));   // bottom (−z)
        Quad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1));   // top (+z)
        Quad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));   // front (−y)
        Quad(P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0));   // back (+y)
        Quad(P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0));   // left (−x)
        Quad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));   // right (+x)
        return list;
    }

    /// <summary>
    /// Prism from a convex profile in the xy plane (counter-clockwise), from z = 0 to <paramref name="height"/>,
    /// then transformed: side strips, caps as fans from the profile centre.
    /// </summary>
    public static List<Tri> Extrude(IReadOnlyList<Vector2> profile, float height, Matrix4x4? transform = null)
    {
        var m = transform ?? Matrix4x4.Identity;
        Vector3 T(Vector2 p, float z) => Vector3.Transform(new Vector3(p, z), m);
        var centre = profile.Aggregate(Vector2.Zero, (s, p) => s + p) / profile.Count;
        var list = new List<Tri>();
        for (var i = 0; i < profile.Count; i++)
        {
            var a = profile[i];
            var b = profile[(i + 1) % profile.Count];
            list.Add(new Tri(T(a, 0), T(b, 0), T(b, height)));
            list.Add(new Tri(T(a, 0), T(b, height), T(a, height)));
            list.Add(new Tri(T(centre, height), T(a, height), T(b, height)));   // top
            list.Add(new Tri(T(centre, 0), T(b, 0), T(a, 0)));                  // bottom
        }
        return list;
    }

    /// <summary>Circle profile with <paramref name="segments"/> corners.</summary>
    public static List<Vector2> Circle(Vector2 centre, float radius, int segments) =>
        Enumerable.Range(0, segments)
            .Select(i => centre + radius * new Vector2(MathF.Cos(2 * MathF.PI * i / segments), MathF.Sin(2 * MathF.PI * i / segments)))
            .ToList();

    /// <summary>Rectangle w × h at the origin whose corner at (w, h) is rounded with radius r in <paramref name="segments"/> steps.</summary>
    public static List<Vector2> RoundedCorner(float w, float h, float r, int segments)
    {
        var list = new List<Vector2> { new(0, 0), new(w, 0) };
        for (var i = 0; i <= segments; i++)
        {
            var a = MathF.PI / 2 * i / segments;
            list.Add(new Vector2(w - r + r * MathF.Cos(a), h - r + r * MathF.Sin(a)));
        }
        list.Add(new Vector2(0, h));
        return list;
    }

    /// <summary>Tube along z: outer and inner wall, rings at both ends.</summary>
    public static List<Tri> Tube(float inner, float outer, float height, int segments)
    {
        var list = new List<Tri>();
        // i % segments: the last corner is the very first one again (cos 2π is not exactly cos 0 in float)
        Vector3 P(float r, int i, float z) => new(r * MathF.Cos(2 * MathF.PI * (i % segments) / segments), r * MathF.Sin(2 * MathF.PI * (i % segments) / segments), z);
        for (var i = 0; i < segments; i++)
        {
            var j = i + 1;
            list.Add(new Tri(P(outer, i, 0), P(outer, j, 0), P(outer, j, height)));
            list.Add(new Tri(P(outer, i, 0), P(outer, j, height), P(outer, i, height)));
            list.Add(new Tri(P(inner, i, 0), P(inner, j, height), P(inner, j, 0)));
            list.Add(new Tri(P(inner, i, 0), P(inner, i, height), P(inner, j, height)));
            list.Add(new Tri(P(inner, i, height), P(outer, i, height), P(outer, j, height)));
            list.Add(new Tri(P(inner, i, height), P(outer, j, height), P(inner, j, height)));
            list.Add(new Tri(P(inner, i, 0), P(outer, j, 0), P(outer, i, 0)));
            list.Add(new Tri(P(inner, i, 0), P(inner, j, 0), P(outer, j, 0)));
        }
        return list;
    }

    /// <summary>UV sphere: a freeform surface.</summary>
    public static List<Tri> Sphere(Vector3 centre, float radius, int slices, int stacks)
    {
        Vector3 P(int i, int j)
        {
            var theta = MathF.PI * j / stacks;
            var phi = 2 * MathF.PI * (i % slices) / slices;
            if (j == 0) return centre + new Vector3(0, 0, radius);
            if (j == stacks) return centre - new Vector3(0, 0, radius);
            return centre + radius * new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta));
        }
        var list = new List<Tri>();
        for (var j = 0; j < stacks; j++)
        for (var i = 0; i < slices; i++)
        {
            if (j > 0) list.Add(new Tri(P(i, j), P(i, j + 1), P(i + 1, j)));
            if (j < stacks - 1) list.Add(new Tri(P(i + 1, j), P(i, j + 1), P(i + 1, j + 1)));
        }
        return list;
    }

    public static byte[] Binary(IReadOnlyList<Tri> tris, string header = "test model")
    {
        var bytes = new byte[84 + 50 * tris.Count];
        Encoding.ASCII.GetBytes(header).AsSpan(0, Math.Min(80, header.Length)).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), (uint)tris.Count);
        for (var i = 0; i < tris.Count; i++)
        {
            var o = 84 + 50 * i;
            var t = tris[i];
            var n = Vector3.Normalize(Vector3.Cross(t.B - t.A, t.C - t.A));
            Write(bytes, o, n);
            Write(bytes, o + 12, t.A);
            Write(bytes, o + 24, t.B);
            Write(bytes, o + 36, t.C);
        }
        return bytes;

        static void Write(byte[] b, int o, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 4), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 8), v.Z);
        }
    }

    public static byte[] Ascii(IReadOnlyList<Tri> tris, string name = "test")
    {
        var sb = new StringBuilder();
        sb.Append("solid ").Append(name).Append('\n');
        foreach (var t in tris)
        {
            sb.Append("  facet normal 0 0 0\n    outer loop\n");
            foreach (var v in new[] { t.A, t.B, t.C })
                sb.Append("      vertex ").Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z)).Append('\n');
            sb.Append("    endloop\n  endfacet\n");
        }
        sb.Append("endsolid ").Append(name).Append('\n');
        return Encoding.ASCII.GetBytes(sb.ToString());

        // "R" keeps every bit, so text and binary give the same vertices
        static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
    }

    public static StlFile Read(byte[] bytes) => StlReader.Read(new MemoryStream(bytes));

    public static Mesh MeshOf(IReadOnlyList<Tri> tris) => Read(Binary(tris)).Mesh;

    /// <summary>Mesh, topology and faces in one go.</summary>
    public static (Mesh Mesh, MeshTopology Topology, FaceSet Faces) Analyse(IReadOnlyList<Tri> tris)
    {
        var mesh = MeshOf(tris);
        var topology = MeshTopology.Build(mesh);
        return (mesh, topology, FaceFinder.Find(mesh, topology));
    }

    /// <summary>Face under a point that lies on the surface (nearest triangle centre).</summary>
    public static int FaceAt(Mesh mesh, FaceSet faces, Vector3 p)
    {
        var best = -1;
        var bestD = float.MaxValue;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Corners(t);
            var d = (D3.From(p) - TriangleDistance.ClosestPoint(D3.From(p), D3.From(a), D3.From(b), D3.From(c))).Length;
            if (d < bestD)
            {
                bestD = (float)d;
                best = t;
            }
        }
        return faces.FaceOf[best];
    }
}
