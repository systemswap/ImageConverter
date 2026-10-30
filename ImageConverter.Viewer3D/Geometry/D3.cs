using System.Numerics;
using System.Runtime.CompilerServices;

namespace ImageConverter.Viewer3D.Geometry;

/// <summary>Point or direction in double precision; used wherever sums or measurements must not lose digits.</summary>
/// <remarks>The mesh itself is stored in single precision as read from the file (STL stores 32-bit floats).</remarks>
public readonly record struct D3(double X, double Y, double Z)
{
    public static readonly D3 Zero = new(0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static D3 From(Vector3 v) => new(v.X, v.Y, v.Z);

    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

    public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static D3 operator -(D3 a) => new(-a.X, -a.Y, -a.Z);
    public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static D3 operator *(double s, D3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static D3 operator /(D3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static double Dot(D3 a, D3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static D3 Cross(D3 a, D3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double LengthSquared => X * X + Y * Y + Z * Z;

    /// <summary>The vector scaled to length 1, or <see cref="Zero"/> if it has (almost) no length.</summary>
    public D3 Normalized()
    {
        var l = Length;
        return l > 1e-300 ? this / l : Zero;
    }

    /// <summary>Any unit vector perpendicular to this (unit) vector.</summary>
    public D3 AnyPerpendicular()
    {
        var other = Math.Abs(X) < 0.9 ? new D3(1, 0, 0) : new D3(0, 1, 0);
        return Cross(this, other).Normalized();
    }

    /// <summary>Angle between two directions in degrees (0 … 180).</summary>
    public static double AngleDegrees(D3 a, D3 b)
    {
        var la = a.Length;
        var lb = b.Length;
        if (la <= 0 || lb <= 0) return 0;
        // atan2 of |a×b| and a·b stays accurate near 0° and 180°, where acos loses digits
        return Math.Atan2(Cross(a, b).Length, Dot(a, b)) * 180 / Math.PI;
    }
}
