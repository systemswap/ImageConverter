using System.Numerics;

namespace ImageConverter.Viewer3D.Geometry;

/// <summary>
/// The drawn section plane: a rectangle on the plane that covers the model, and where a ray meets it. The plane is
/// (normal, distance): points with <c>dot(p, normal) &lt; distance</c> are cut away.
/// </summary>
public static class SectionGeometry
{
    /// <summary>The rectangle on the plane over the model's box, a little larger.</summary>
    /// <param name="box">Box of the model.</param>
    /// <param name="plane">The section plane (unit normal, distance).</param>
    /// <param name="margin">Margin as a share of the rectangle's size, for example 0.06.</param>
    /// <returns>Four corners, going round.</returns>
    public static Vector3[] Quad(Box3 box, Vector4 plane, float margin = 0.06f)
    {
        var (origin, u, v, uMin, uMax, vMin, vMax) = Frame(box, plane, margin);
        return
        [
            origin + u * uMin + v * vMin,
            origin + u * uMax + v * vMin,
            origin + u * uMax + v * vMax,
            origin + u * uMin + v * vMax,
        ];
    }

    /// <summary>Where a ray meets the rectangle, or null if it misses it (or runs along the plane).</summary>
    /// <returns>Distance along the ray (in units of the direction's length).</returns>
    public static float? Hit(Box3 box, Vector4 plane, Vector3 origin, Vector3 direction, float margin = 0.06f)
    {
        var n = new Vector3(plane.X, plane.Y, plane.Z);
        var along = Vector3.Dot(direction, n);
        if (Math.Abs(along) < 1e-9f) return null;
        var t = (plane.W - Vector3.Dot(origin, n)) / along;
        if (t < 0) return null;
        var p = origin + direction * t;
        var (o, u, v, uMin, uMax, vMin, vMax) = Frame(box, plane, margin);
        var pu = Vector3.Dot(p - o, u);
        var pv = Vector3.Dot(p - o, v);
        return pu >= uMin && pu <= uMax && pv >= vMin && pv <= vMax ? t : null;
    }

    /// <summary>
    /// Plane for a new section on a face: lying on the clicked point, its normal pointing into the material (opposite
    /// to the face's outward normal), so that moving it deeper cuts the material in front away.
    /// </summary>
    public static Vector4 OnFace(Vector3 point, Vector3 outwardNormal)
    {
        var n = -Vector3.Normalize(outwardNormal);
        return new Vector4(n, Vector3.Dot(point, n));
    }

    private static (Vector3 Origin, Vector3 U, Vector3 V, float UMin, float UMax, float VMin, float VMax) Frame(Box3 box, Vector4 plane, float margin)
    {
        var n = new Vector3(plane.X, plane.Y, plane.Z);
        var u = Vector3.Normalize(MathF.Abs(n.X) < 0.9f ? Vector3.Cross(n, Vector3.UnitX) : Vector3.Cross(n, Vector3.UnitY));
        var v = Vector3.Cross(n, u);
        // Origin: the box centre moved onto the plane
        var c = box.Center;
        var origin = c + n * (plane.W - Vector3.Dot(c, n));
        float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
            var du = Vector3.Dot(corner - origin, u);
            var dv = Vector3.Dot(corner - origin, v);
            uMin = Math.Min(uMin, du);
            uMax = Math.Max(uMax, du);
            vMin = Math.Min(vMin, dv);
            vMax = Math.Max(vMax, dv);
        }
        var mu = (uMax - uMin) * margin;
        var mv = (vMax - vMin) * margin;
        return (origin, u, v, uMin - mu, uMax + mu, vMin - mv, vMax + mv);
    }
}
