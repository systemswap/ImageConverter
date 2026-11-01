using System.Numerics;
using ImageConverter.Viewer3D.Geometry;
using ManifoldSharp;
using ManifoldSharp.Linalg;

namespace ImageConverter.Viewer3D.Editing;

/// <summary>An edit could not be carried out; the message is meant for the user.</summary>
public sealed class EditException(string message) : Exception(message);

/// <summary>
/// Changes to a closed model: lengthen or shorten it at a dividing plane, add or remove a body, move a flat face. The
/// only place that knows the geometry kernel (Boolean operations on closed triangle meshes whose results are closed
/// again); the rest of the program hands in a <see cref="Mesh"/> and gets one back.
/// </summary>
/// <remarks>
/// <para>Kernel: ManifoldSharp 0.6.0 (https://github.com/larsbrubaker/manifold-sharp, Apache-2.0), a C# port of Manifold
/// (https://github.com/elalish/manifold) via manifold-rust; it brings Clipper2 1.5.4 (https://github.com/AngusJohnson/Clipper2,
/// Boost Software License 1.0) for the outlines. See THIRD-PARTY-NOTICES.md.</para>
/// <para>
/// The kernel takes and returns positions in single precision – no loss, a binary STL file stores no more – and
/// computes in double precision in between. Positions that are not touched keep their exact values.
/// </para>
/// </remarks>
public static partial class SolidEditor
{
    private static Manifold Import(Mesh mesh)
    {
        var gl = new MeshGL
        {
            NumProp = 3,
            VertProperties = [.. mesh.Positions],
            TriVerts = [.. mesh.Indices.Select(i => (uint)i)],
        };
        var solid = Manifold.FromMeshGL(gl);
        if (solid.Status() != Error.NoError)
            throw new EditException(Tr.T("Das Modell ist nicht geschlossen oder hat sich überschneidende Flächen; bearbeiten lassen sich nur geschlossene Modelle."));
        return solid;
    }

    private static Mesh Export(Manifold solid)
    {
        if (solid.Status() != Error.NoError || solid.IsEmpty())
            throw new EditException(Tr.T("Die Änderung ergab kein gültiges Modell."));
        var gl = solid.GetMeshGL(-1);
        var stride = (int)gl.NumProp;
        var count = gl.NumVert();
        var positions = new float[count * 3];
        var props = gl.VertProperties;
        for (var i = 0; i < count; i++)
        {
            positions[3 * i] = props[i * stride];
            positions[3 * i + 1] = props[i * stride + 1];
            positions[3 * i + 2] = props[i * stride + 2];
        }
        return new Mesh(positions, [.. gl.TriVerts.Select(i => (int)i)]);
    }

    /// <summary>Rotation that turns <paramref name="n"/> into +Z (the kernel slices along Z), and back.</summary>
    private static (Mat3x4 ToZ, Mat3x4 Back) Frame(Vector3 n)
    {
        // Exactly for the axes (no rounding of untouched positions), otherwise an orthonormal frame
        Vector3 u, v;
        if (n == Vector3.UnitZ) (u, v) = (Vector3.UnitX, Vector3.UnitY);
        else if (n == -Vector3.UnitZ) (u, v) = (Vector3.UnitX, -Vector3.UnitY);
        else if (n == Vector3.UnitX) (u, v) = (Vector3.UnitY, Vector3.UnitZ);
        else if (n == -Vector3.UnitX) (u, v) = (-Vector3.UnitY, Vector3.UnitZ);
        else if (n == Vector3.UnitY) (u, v) = (Vector3.UnitZ, Vector3.UnitX);
        else if (n == -Vector3.UnitY) (u, v) = (-Vector3.UnitZ, Vector3.UnitX);
        else
        {
            var helper = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
            u = Vector3.Normalize(Vector3.Cross(helper, n));
            v = Vector3.Cross(n, u);
        }
        // rows u, v, n: p' = (u·p, v·p, n·p); the columns of that matrix, then the transpose for the way back
        var toZ = Mat3x4.FromCols(new Vec3(u.X, v.X, n.X), new Vec3(u.Y, v.Y, n.Y), new Vec3(u.Z, v.Z, n.Z), new Vec3(0, 0, 0));
        var back = Mat3x4.FromCols(V(u), V(v), V(n), new Vec3(0, 0, 0));
        return (toZ, back);
    }

    private static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 Unit(Vector3 v)
    {
        var len = v.Length();
        if (!(len > 0) || !float.IsFinite(len)) throw new ArgumentException("No direction.", nameof(v));
        var u = v / len;
        // snap a direction that is an axis up to rounding onto the axis (exact frame above)
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            if (Vector3.Dot(u, axis) > 1 - 1e-7f) return axis;
            if (Vector3.Dot(u, axis) < -1 + 1e-7f) return -axis;
        }
        return u;
    }

    private static double Diagonal(Mesh mesh) => Math.Max(mesh.Bounds.Diagonal, 1e-6);
}
