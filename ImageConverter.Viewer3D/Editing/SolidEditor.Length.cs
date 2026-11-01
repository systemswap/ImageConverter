using System.Numerics;
using ImageConverter.Viewer3D.Geometry;
using ManifoldSharp;
using ManifoldSharp.Linalg;

namespace ImageConverter.Viewer3D.Editing;

// Lengthening and shortening at a dividing plane, and where a model is uniform enough for it.
public static partial class SolidEditor
{
    /// <summary>
    /// Lengthens (<paramref name="delta"/> &gt; 0) or shortens (&lt; 0) the model along <paramref name="normal"/>: the
    /// part beyond the dividing plane <c>dot(n, p) = offset</c> moves by <paramref name="delta"/>, the rest stays.
    /// Lengthening fills the gap with the cross-section at the plane drawn out; shortening removes the band
    /// <c>offset … offset + |delta|</c> and closes up.
    /// </summary>
    /// <param name="mesh">A closed model.</param>
    /// <param name="normal">Direction of the length to change (need not be unit length).</param>
    /// <param name="offset">Position of the dividing plane along the unit normal.</param>
    /// <param name="delta">Change of the length in model units.</param>
    /// <param name="ct">Cancels between the steps.</param>
    /// <returns>The changed model.</returns>
    /// <exception cref="EditException">
    /// The model is not closed; shortening would remove more than a uniform band (a rib, a hole or a sloping wall lies in
    /// it, so the parts would not fit together any more); or the result is empty.
    /// </exception>
    public static Mesh ChangeLength(Mesh mesh, Vector3 normal, double offset, double delta, CancellationToken ct = default)
    {
        var n = Unit(normal);
        var (toZ, back) = Frame(n);
        var solid = Import(mesh).Transform(toZ);
        ct.ThrowIfCancellationRequested();
        var (above, below) = solid.SplitByPlane(new Vec3(0, 0, 1), offset);
        Manifold result;
        if (delta >= 0)
        {
            // The cross-section at the plane, drawn out over the gap; a plane through a gap between parts has none
            var section = solid.Slice(offset).ToPolygons();
            result = above.Translate(new Vec3(0, 0, delta));
            if (section.Count > 0)
                result = result.Union(Manifold.Extrude(section, delta, 0, 0, new Vec2(1, 1)).Translate(new Vec3(0, 0, offset)));
            result = below.Union(result);
        }
        else
        {
            var cut = -delta;
            var (far, band) = above.SplitByPlane(new Vec3(0, 0, 1), offset + cut);
            CheckUniformBand(solid, band, offset, cut, Diagonal(mesh));
            ct.ThrowIfCancellationRequested();
            result = below.Union(far.Translate(new Vec3(0, 0, -cut)));
        }
        ct.ThrowIfCancellationRequested();
        return Export(result.Transform(back));
    }

    /// <summary>
    /// Stretches between two positions along <paramref name="normal"/> where the model is the same in every cross-section
    /// (no corner inside and equal sections at both ends): a dividing plane there lengthens or shortens the model without
    /// changing any feature – slots, bores and ribs stay as they are. Widest first.
    /// </summary>
    /// <param name="mesh">A closed model.</param>
    /// <param name="normal">Direction of the length.</param>
    /// <param name="from">Start along the unit normal (the first face).</param>
    /// <param name="to">End along the unit normal (the second face).</param>
    /// <param name="ct">Cancels the search.</param>
    /// <returns>Up to eight stretches, as positions along the unit normal.</returns>
    public static IReadOnlyList<(double From, double To)> UniformStretches(Mesh mesh, Vector3 normal, double from, double to, CancellationToken ct = default)
    {
        var n = Unit(normal);
        var size = Diagonal(mesh);
        var edge = 1e-5 * size;
        // Positions of all corners between the faces: a stretch without any is drawn out of triangles that run through it
        var p = mesh.Positions;
        var at = new List<double>(mesh.VertexCount) { from, to };
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var d = (double)n.X * p[3 * i] + (double)n.Y * p[3 * i + 1] + (double)n.Z * p[3 * i + 2];
            if (d > from + edge && d < to - edge) at.Add(d);
        }
        at.Sort();
        var gaps = new List<(double From, double To)>();
        for (var i = 1; i < at.Count; i++)
            if (at[i] - at[i - 1] > Math.Max(20 * edge, 1e-3 * (to - from))) gaps.Add((at[i - 1], at[i]));
        if (gaps.Count == 0) return [];

        // A sloping wall also has no corner inside: the sections at both ends must match as well. Neighbouring stretches
        // that meet at a row of corners with the same section on both sides (the seam of an earlier edit) are one.
        var (toZ, _) = Frame(n);
        var solid = Import(mesh).Transform(toZ);
        var tolerance = Math.Max(1e-9, 1e-6 * size * size);
        CrossSection Section(double z) => new(solid.Slice(z).ToPolygons());
        bool Same(CrossSection x, CrossSection y) => x.Difference(y).Area() + y.Difference(x).Area() <= tolerance;
        var candidates = gaps.OrderByDescending(g => g.To - g.From).Take(64).OrderBy(g => g.From).ToList();
        var uniform = new List<(double From, double To, CrossSection Start, CrossSection End)>();
        foreach (var g in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var a = Section(g.From + edge);
            var b = Section(g.To - edge);
            if (a.Area() <= 0 || !Same(a, b)) continue;
            if (uniform.Count > 0 && Math.Abs(uniform[^1].To - g.From) <= edge && Same(uniform[^1].End, a))
                uniform[^1] = (uniform[^1].From, g.To, uniform[^1].Start, b);
            else uniform.Add((g.From, g.To, a, b));
        }
        var result = uniform.Select(u => (u.From, u.To)).OrderByDescending(u => u.To - u.From).Take(8).ToList();
        return result;
    }

    /// <summary>
    /// The band to remove must be the cross-section drawn out unchanged: equal sections at both ends and the volume of
    /// that prism – otherwise closing up would leave steps or cut through a feature.
    /// </summary>
    private static void CheckUniformBand(Manifold solid, Manifold band, double offset, double cut, double size)
    {
        var start = new CrossSection(solid.Slice(offset).ToPolygons());
        var end = new CrossSection(solid.Slice(offset + cut).ToPolygons());
        var area = start.Area();
        var mismatch = start.Difference(end).Area() + end.Difference(start).Area();
        var tolerance = Math.Max(1e-9, 1e-6 * size * size);
        var volumeTolerance = Math.Max(1e-9, 1e-6 * size * size * size) + area * 1e-9;
        if (mismatch > tolerance || Math.Abs(band.Volume() - area * cut) > Math.Max(volumeTolerance, 1e-6 * area * cut))
            throw new EditException(Tr.T("Kürzen geht hier nicht: Im wegfallenden Bereich ist das Teil nicht überall gleich (Bohrung, Steg, Rippe oder schräge Wand). Die Trennebene an eine Stelle mit gleichem Querschnitt legen."));
    }
}
