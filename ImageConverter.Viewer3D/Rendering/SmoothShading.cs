using System.Numerics;
using ImageConverter.Viewer3D.Geometry;

namespace ImageConverter.Viewer3D.Rendering;

/// <summary>
/// Normals per vertex for smooth shading of finely divided curved faces. A curved surface arrives as a strip of flat
/// facets, and shading each facet flat shows it as a staircase of light steps. Only what the face finder recognised as a
/// cylinder or a freeform patch is smoothed, and only where its facets are fine; planes stay exactly flat, and edges
/// between faces stay sharp, so that nothing looks rounder or softer than the file describes. The geometry itself
/// (outline, edge lines, measuring) is unchanged – only the light across a curved face.
/// </summary>
/// <param name="ExtraSources">For each added vertex (appended after the mesh's own), the mesh vertex it copies.</param>
/// <param name="Normals">
/// Normal of every vertex: zero for the mesh's own vertices (flat shading), a unit normal for the added ones (3 floats
/// each).
/// </param>
/// <param name="Corners">Vertex of each triangle corner in the extended list (3 per triangle, in mesh order).</param>
internal sealed record SmoothShading(int[] ExtraSources, float[] Normals, int[] Corners)
{
    /// <summary>
    /// A facet may differ by at most this from the normal at its corners; a cylinder divided into facets of up to
    /// twice this angle (36 or more per turn) is shaded smoothly, a coarser one stays faceted as in the file.
    /// </summary>
    public const float MaxDeviationDegrees = 5;

    /// <summary>
    /// Builds the smooth normals: on a cylinder the exact normal of the fitted cylinder (radial from its axis), on a
    /// freeform patch the average of the facets around the vertex weighted by their angle there. A triangle with a
    /// corner whose facets disagree by more than <see cref="MaxDeviationDegrees"/> stays flat. Linear in the number of
    /// triangles.
    /// </summary>
    public static SmoothShading Build(Mesh mesh, FaceSet faces, CancellationToken ct = default)
    {
        var idx = mesh.Indices;
        var pos = mesh.Positions;
        var v = mesh.VertexCount;
        var cos = MathF.Cos(MaxDeviationDegrees * MathF.PI / 180);
        var corners = (int[])idx.Clone();
        var extras = new List<int>();
        var sums = new List<Vector3>();
        // Which face last claimed a vertex, and the added vertex it got there
        var stampFace = new int[v];
        Array.Fill(stampFace, -1);
        var stampVertex = new int[v];

        foreach (var face in faces.Faces)
        {
            ct.ThrowIfCancellationRequested();
            if (face.Kind == FaceKind.Plane) continue;
            var tris = faces.TrianglesOf(face.Id);
            var firstExtra = extras.Count;
            foreach (var t in tris)
                for (var k = 0; k < 3; k++)
                {
                    var vertex = idx[3 * t + k];
                    if (stampFace[vertex] != face.Id)
                    {
                        stampFace[vertex] = face.Id;
                        stampVertex[vertex] = v + extras.Count;
                        extras.Add(vertex);
                        sums.Add(Vector3.Zero);
                    }
                    var (n, angle) = FacetAt(pos, idx, t, k);
                    sums[stampVertex[vertex] - v] += n * angle;
                    corners[3 * t + k] = stampVertex[vertex];
                }

            // Normals of this face's vertices
            var axis = face.Axis.ToVector3();
            var axisPoint = face.AxisPoint.ToVector3();
            var cylinder = face.Kind == FaceKind.Cylinder && axis.LengthSquared() > 0.5f;
            for (var e = firstExtra; e < extras.Count; e++)
            {
                var s = sums[e];
                if (s.LengthSquared() == 0) continue;
                if (cylinder)
                {
                    var p = Position(pos, extras[e]);
                    var r = p - axisPoint;
                    r -= axis * Vector3.Dot(r, axis);
                    var len = r.Length();
                    // a vertex on the axis has no radial direction: left zero, its triangles stay flat
                    sums[e] = len > 0 ? r / len * MathF.Sign(Vector3.Dot(r, s)) : Vector3.Zero;
                }
                else sums[e] = Vector3.Normalize(s);
            }

            // Coarse facets or a crease inside the face: those triangles stay flat
            foreach (var t in tris)
            {
                var flat = false;
                for (var k = 0; k < 3 && !flat; k++)
                {
                    var n = sums[corners[3 * t + k] - v];
                    var (f, _) = FacetAt(pos, idx, t, k);
                    flat = n == Vector3.Zero || f == Vector3.Zero || Vector3.Dot(n, f) < cos;
                }
                if (flat)
                    for (var k = 0; k < 3; k++) corners[3 * t + k] = idx[3 * t + k];
            }
        }

        var normals = new float[(v + extras.Count) * 3];
        for (var e = 0; e < extras.Count; e++)
        {
            var o = (v + e) * 3;
            normals[o] = sums[e].X;
            normals[o + 1] = sums[e].Y;
            normals[o + 2] = sums[e].Z;
        }
        return new SmoothShading(extras.ToArray(), normals, corners);
    }

    private static Vector3 Position(float[] pos, int i) => new(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);

    /// <summary>Unit normal of a triangle (by its winding) and its angle at one corner; zero for a degenerate one.</summary>
    private static (Vector3 Normal, float Angle) FacetAt(float[] pos, int[] idx, int t, int corner)
    {
        var a = Position(pos, idx[3 * t + corner]);
        var b = Position(pos, idx[3 * t + (corner + 1) % 3]);
        var c = Position(pos, idx[3 * t + (corner + 2) % 3]);
        var n = Vector3.Cross(b - a, c - a);
        var len = n.Length();
        if (len < 1e-30f) return (Vector3.Zero, 0);
        return (n / len, MathF.Atan2(len, Vector3.Dot(b - a, c - a)));
    }

    /// <summary>The mesh's positions followed by those of the added vertices.</summary>
    public float[] ExtendedPositions(float[] positions)
    {
        var all = new float[positions.Length + ExtraSources.Length * 3];
        positions.CopyTo(all, 0);
        for (var e = 0; e < ExtraSources.Length; e++)
            Array.Copy(positions, ExtraSources[e] * 3, all, positions.Length + e * 3, 3);
        return all;
    }

    /// <summary>Per-vertex bytes (part numbers) extended to the added vertices.</summary>
    public byte[] Extended(byte[] perVertex)
    {
        var all = new byte[perVertex.Length + ExtraSources.Length];
        perVertex.CopyTo(all, 0);
        for (var e = 0; e < ExtraSources.Length; e++) all[perVertex.Length + e] = perVertex[ExtraSources[e]];
        return all;
    }

    /// <summary>Corner indices in face order (as <see cref="SceneData.SortedByFace"/>), for drawing faces by range.</summary>
    public int[] SortedByFace(FaceSet faces)
    {
        var sorted = new int[Corners.Length];
        var w = 0;
        for (var f = 0; f < faces.Faces.Length; f++)
            foreach (var t in faces.TrianglesOf(f))
            {
                sorted[w++] = Corners[3 * t];
                sorted[w++] = Corners[3 * t + 1];
                sorted[w++] = Corners[3 * t + 2];
            }
        return sorted;
    }
}
