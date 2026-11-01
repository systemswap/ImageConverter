using System.Numerics;
using ImageConverter.Viewer3D.Geometry;
using ManifoldSharp;
using ManifoldSharp.Linalg;

namespace ImageConverter.Viewer3D.Editing;

// Flat faces (or a masked part of one) moved along their normal.
public static partial class SolidEditor
{
    /// <summary>
    /// The outline of a flat face in its own plane, worked out once when the face is picked: moving it then only draws
    /// this outline out.
    /// </summary>
    public sealed class FaceOutline
    {
        internal FaceOutline(List<List<Vec2>> polygons, Mat3x4 back, double plane, Vector3 normal)
        {
            Polygons = polygons;
            Back = back;
            Plane = plane;
            Normal = normal;
        }

        internal List<List<Vec2>> Polygons { get; }

        internal Mat3x4 Back { get; }

        /// <summary>Position of the face along its normal.</summary>
        public double Plane { get; }

        /// <summary>Unit normal out of the material.</summary>
        public Vector3 Normal { get; }
    }

    /// <summary>
    /// The extent of a face outline in its own plane: <c>U</c> and <c>V</c> run along the axes of the frame (for a face
    /// across X: Y and Z; across Y: Z and X; across Z: X and Y).
    /// </summary>
    public static (double MinU, double MaxU, double MinV, double MaxV) Extent(FaceOutline face)
    {
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var ring in face.Polygons)
            foreach (var p in ring)
            {
                minU = Math.Min(minU, p.X); maxU = Math.Max(maxU, p.X);
                minV = Math.Min(minV, p.Y); maxV = Math.Max(maxV, p.Y);
            }
        return (minU, maxU, minV, maxV);
    }

    /// <summary>
    /// The part of a face inside a rectangle of its plane (the mask): only that part is then moved.
    /// </summary>
    /// <exception cref="EditException">The mask does not cover any of the face.</exception>
    public static FaceOutline Mask(FaceOutline face, double minU, double maxU, double minV, double maxV)
    {
        var rect = new CrossSection([[new Vec2(minU, minV), new Vec2(maxU, minV), new Vec2(maxU, maxV), new Vec2(minU, maxV)]]);
        var part = new CrossSection(face.Polygons).Intersection(rect);
        if (part.IsEmpty() || part.Area() <= 0) throw new EditException(Tr.T("Die Maske deckt nichts von der Fläche ab."));
        return new FaceOutline(part.ToPolygons(), face.Back, face.Plane, face.Normal);
    }

    /// <summary>A thin plate in the plane of the face over a rectangle (the mask frame, for the preview).</summary>
    public static Mesh MaskFrameMesh(FaceOutline face, double minU, double maxU, double minV, double maxV, double thickness)
    {
        var rect = new List<List<Vec2>> { new() { new(minU, minV), new(maxU, minV), new(maxU, maxV), new(minU, maxV) } };
        if (!(maxU > minU) || !(maxV > minV)) throw new EditException(Tr.T("Die Maske braucht eine Größe."));
        return Export(Manifold.Extrude(rect, thickness, 0, 0, new Vec2(1, 1)).Translate(new Vec3(0, 0, face.Plane - thickness / 2)).Transform(face.Back));
    }

    /// <summary>The outline of a flat face (its triangles merged in the plane of the face).</summary>
    /// <param name="mesh">The model.</param>
    /// <param name="triangles">Triangles of the face.</param>
    /// <param name="outward">Normal of the face out of the material.</param>
    /// <exception cref="EditException">The triangles have no area in the plane.</exception>
    public static FaceOutline Outline(Mesh mesh, ReadOnlySpan<int> triangles, Vector3 outward)
    {
        var n = Unit(outward);
        var (toZ, back) = Frame(n);
        var pieces = new List<CrossSection>(triangles.Length);
        double plane = 0;
        foreach (var t in triangles)
        {
            var (a, b, c) = mesh.Corners(t);
            var pa = ToFrame(toZ, a); var pb = ToFrame(toZ, b); var pc = ToFrame(toZ, c);
            plane += (pa.Z + pb.Z + pc.Z) / 3;
            var ring = new List<Vec2> { new(pa.X, pa.Y), new(pb.X, pb.Y), new(pc.X, pc.Y) };
            var area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pc.X - pa.X) * (pb.Y - pa.Y);
            if (Math.Abs(area) < 1e-18) continue;
            if (area < 0) ring.Reverse();
            pieces.Add(new CrossSection([ring]));
        }
        if (pieces.Count == 0) throw new EditException(Tr.T("Die Fläche hat keine Ausdehnung."));
        var outline = CrossSection.BatchBoolean(pieces, OpType.Add).ToPolygons();
        return new FaceOutline(outline, back, plane / triangles.Length, n);
    }

    private static Vector3D ToFrame(Mat3x4 m, Vector3 p)
    {
        // rows of the rotation are the columns given to FromCols: apply as p' = R p
        var r = m * new Vec4(p.X, p.Y, p.Z, 1);
        return new Vector3D(r.X, r.Y, r.Z);
    }

    private readonly record struct Vector3D(double X, double Y, double Z);

    /// <summary>
    /// The face drawn out by <paramref name="distance"/> along its normal: outwards (positive) the piece that moving the
    /// face adds, inwards (negative) the piece it removes; reaching 1 µm across the face so that it joins or leaves no skin.
    /// </summary>
    private static Manifold FaceTool(FaceOutline face, double distance)
    {
        var lo = distance >= 0 ? face.Plane - Overlap : face.Plane + distance;
        var hi = distance >= 0 ? face.Plane + distance : face.Plane + Overlap;
        return Manifold.Extrude(face.Polygons, hi - lo, 0, 0, new Vec2(1, 1)).Translate(new Vec3(0, 0, lo)).Transform(face.Back);
    }

    /// <summary>1 µm: how far a tool reaches across the face it starts from.</summary>
    private const double Overlap = 0.001;

    /// <summary>The piece moving the face would add or remove, as a mesh (preview).</summary>
    public static Mesh FaceToolMesh(FaceOutline face, double distance) => Export(FaceTool(face, distance));

    /// <summary>
    /// Moves a flat face along its normal: the body grows by the face drawn out (positive) or loses that piece
    /// (negative). Side faces run on straight, as when a profile is drawn out further.
    /// </summary>
    /// <exception cref="EditException">The model is not closed, or nothing changes.</exception>
    public static Mesh MoveFace(Mesh mesh, FaceOutline face, double distance, CancellationToken ct = default)
    {
        if (Math.Abs(distance) < 1e-9) throw new EditException(Tr.T("Die Fläche wurde nicht verschoben."));
        var solid = Import(mesh);
        ct.ThrowIfCancellationRequested();
        var tool = FaceTool(face, distance);
        var result = distance > 0 ? solid.Union(tool) : solid.Difference(tool);
        if (Math.Abs(result.Volume() - solid.Volume()) <= Math.Max(1e-9, 1e-9 * solid.Volume()))
            throw new EditException(Tr.T("Das Verschieben ändert hier nichts."));
        return Export(result);
    }
}
