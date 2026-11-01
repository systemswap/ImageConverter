using System.Numerics;
using ImageConverter.Viewer3D.Geometry;
using ManifoldSharp;
using ManifoldSharp.Linalg;

namespace ImageConverter.Viewer3D.Editing;

// Boxes and cylinders added to the model or cut out of it.
public static partial class SolidEditor
{
    /// <summary>
    /// Adds a body to the model or removes it from the material (Boolean union or difference).
    /// </summary>
    /// <param name="mesh">A closed model.</param>
    /// <param name="body">The body, in model coordinates.</param>
    /// <param name="add">True: union; false: the body is cut out of the model.</param>
    /// <param name="mirrorAtX">Also the body mirrored at the plane <c>x = value</c> (both sides of a symmetric part); null: only the body.</param>
    /// <param name="ct">Cancels between the steps.</param>
    /// <returns>The changed model.</returns>
    /// <exception cref="EditException">The model is not closed, the body is empty, or nothing changes (it does not touch the material).</exception>
    public static Mesh Combine(Mesh mesh, Body body, bool add, float? mirrorAtX = null, CancellationToken ct = default)
    {
        var solid = Import(mesh);
        var tool = Tool(body, mirrorAtX);
        ct.ThrowIfCancellationRequested();
        var result = add ? solid.Union(tool) : solid.Difference(tool);
        var before = solid.Volume();
        var after = result.Volume();
        if (Math.Abs(after - before) <= Math.Max(1e-9, 1e-9 * before))
            throw new EditException(add
                ? Tr.T("Der Körper liegt ganz im Material: hinzufügen ändert nichts.")
                : Tr.T("Der Körper berührt das Material nicht: wegnehmen ändert nichts."));
        ct.ThrowIfCancellationRequested();
        return Export(result);
    }

    /// <summary>The body as a mesh (preview), exactly as it would be added or removed.</summary>
    public static Mesh BodyMesh(Body body, float? mirrorAtX = null) => Export(Tool(body, mirrorAtX));

    /// <summary>
    /// Segments around a cylinder: the polygon stays within 1 µm of the circle (at least 24, at most 512) – a bore of
    /// 3 mm gets 86 corners and loses 0.09 % of its cross-section to the flats.
    /// </summary>
    private static int Segments(double radius)
    {
        var step = 2 * Math.Acos(Math.Clamp(1 - 0.001 / Math.Max(radius, 1e-6), -1, 1));
        return Math.Clamp((int)Math.Ceiling(2 * Math.PI / Math.Max(step, 1e-6)), 24, 512);
    }

    private static Manifold Tool(Body body, float? mirrorAtX)
    {
        var size = body.Max - body.Min;
        if (!(size.X > 0) || !(size.Y > 0) || !(size.Z > 0))
            throw new EditException(Tr.T("Der Körper braucht in jeder Richtung eine Größe über null."));
        Manifold tool;
        if (body.Shape == BodyShape.Box)
            tool = Manifold.Cube(new Vec3(size.X, size.Y, size.Z), false).Translate(V(body.Min));
        else
        {
            // Along Z from 0 to the length, centred on the axis; then turned onto the chosen axis
            var length = body.Axis switch { 0 => size.X, 1 => size.Y, _ => size.Z };
            var diameter = body.Axis switch { 0 => Math.Min(size.Y, size.Z), 1 => Math.Min(size.X, size.Z), _ => Math.Min(size.X, size.Y) };
            tool = Manifold.Cylinder(length, diameter / 2, diameter / 2, Segments(diameter / 2));
            if (body.Axis == 0) tool = tool.Rotate(0, 90, 0);
            else if (body.Axis == 1) tool = tool.Rotate(-90, 0, 0);
            var c = (body.Min + body.Max) / 2;
            var start = body.Axis switch
            {
                0 => new Vec3(body.Min.X, c.Y, c.Z),
                1 => new Vec3(c.X, body.Min.Y, c.Z),
                _ => new Vec3(c.X, c.Y, body.Min.Z),
            };
            tool = tool.Translate(start);
        }
        if (mirrorAtX is { } x)
            tool = tool.Union(tool.Translate(new Vec3(-x, 0, 0)).Mirror(new Vec3(1, 0, 0)).Translate(new Vec3(x, 0, 0)));
        return tool;
    }
}
