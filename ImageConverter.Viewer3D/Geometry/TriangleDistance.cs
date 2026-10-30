namespace ImageConverter.Viewer3D.Geometry;

/// <summary>Closest points between points, segments and triangles, in double precision.</summary>
/// <remarks>Procedures after C. Ericson: Real-Time Collision Detection, Morgan Kaufmann 2005, sections 5.1.5 and 5.1.9.</remarks>
public static class TriangleDistance
{
    /// <summary>Point of triangle abc nearest to p (section 5.1.5, Voronoi regions of the triangle).</summary>
    public static D3 ClosestPoint(D3 p, D3 a, D3 b, D3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = D3.Dot(ab, ap);
        var d2 = D3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;

        var bp = p - b;
        var d3 = D3.Dot(ab, bp);
        var d4 = D3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;

        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            var v = d1 / (d1 - d3);
            return a + ab * v;
        }

        var cp = p - c;
        var d5 = D3.Dot(ab, cp);
        var d6 = D3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;

        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            var w = d2 / (d2 - d6);
            return a + ac * w;
        }

        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            var w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return b + (c - b) * w;
        }

        var sum = va + vb + vc;
        if (!(Math.Abs(sum) > 0)) return a;   // degenerate triangle
        var denom = 1 / sum;
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    /// <summary>Closest points of segments p1q1 and p2q2 (section 5.1.9).</summary>
    public static (D3 On1, D3 On2) ClosestPoints(D3 p1, D3 q1, D3 p2, D3 q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = D3.Dot(d1, d1);
        var e = D3.Dot(d2, d2);
        var f = D3.Dot(d2, r);
        const double eps = 1e-24;
        double s, t;
        if (a <= eps && e <= eps) return (p1, p2);
        if (a <= eps)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            var c = D3.Dot(d1, r);
            if (e <= eps)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                var b = D3.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom > eps * a * e ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((b - c) / a, 0, 1);
                }
            }
        }
        return (p1 + d1 * s, p2 + d2 * t);
    }

    /// <summary>Closest points of two triangles and their distance; 0 where they touch or cross.</summary>
    public static (D3 OnA, D3 OnB, double Distance) Between(D3 a0, D3 a1, D3 a2, D3 b0, D3 b1, D3 b2)
    {
        // Crossing: an edge of one passes through the other
        if (SegmentHits(a0, a1, b0, b1, b2, out var x) || SegmentHits(a1, a2, b0, b1, b2, out x) || SegmentHits(a2, a0, b0, b1, b2, out x)
            || SegmentHits(b0, b1, a0, a1, a2, out x) || SegmentHits(b1, b2, a0, a1, a2, out x) || SegmentHits(b2, b0, a0, a1, a2, out x))
            return (x, x, 0);

        var best = double.MaxValue;
        D3 pa = a0, pb = b0;
        Span<D3> ta = [a0, a1, a2];
        Span<D3> tb = [b0, b1, b2];
        for (var i = 0; i < 3; i++)
        {
            var q = ClosestPoint(ta[i], b0, b1, b2);
            Consider(ta[i], q);
            q = ClosestPoint(tb[i], a0, a1, a2);
            Consider(q, tb[i]);
        }
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            var (s, t) = ClosestPoints(ta[i], ta[(i + 1) % 3], tb[j], tb[(j + 1) % 3]);
            Consider(s, t);
        }
        return (pa, pb, best);

        void Consider(D3 onA, D3 onB)
        {
            var d = (onA - onB).Length;
            if (d < best)
            {
                best = d;
                pa = onA;
                pb = onB;
            }
        }
    }

    /// <summary>Whether segment pq passes through triangle abc (Möller–Trumbore with the parameter limited to the segment).</summary>
    private static bool SegmentHits(D3 p, D3 q, D3 a, D3 b, D3 c, out D3 point)
    {
        point = D3.Zero;
        var d = q - p;
        var e1 = b - a;
        var e2 = c - a;
        var h = D3.Cross(d, e2);
        var det = D3.Dot(e1, h);
        if (Math.Abs(det) < 1e-30) return false;
        var inv = 1 / det;
        var s = p - a;
        var u = D3.Dot(s, h) * inv;
        if (u < 0 || u > 1) return false;
        var qv = D3.Cross(s, e1);
        var v = D3.Dot(d, qv) * inv;
        if (v < 0 || u + v > 1) return false;
        var t = D3.Dot(e2, qv) * inv;
        if (t < 0 || t > 1) return false;
        point = p + d * t;
        return true;
    }
}
