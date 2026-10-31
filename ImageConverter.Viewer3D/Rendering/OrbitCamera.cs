using System.Numerics;

namespace ImageConverter.Viewer3D.Rendering;

/// <summary>Fixed directions to look from.</summary>
public enum StandardView
{
    Iso,
    Front,
    Back,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// Camera that orbits like a turntable: the model's up axis (+Z, as in 3D printing) always stays vertical on screen;
/// dragging sideways turns around it, dragging up and down tilts at most to the view from straight above or below.
/// </summary>
/// <remarks>
/// <para>State: the point looked at (<see cref="Target"/>), the turn around Z (<see cref="Yaw"/>), the tilt
/// (<see cref="Pitch"/>, positive = from above) and the distance. The screen axes follow from yaw and pitch directly,
/// so the views from straight above or below need no special case.</para>
/// <para>Orthographic by default – sizes on screen do not depend on depth, which suits measuring. The visible height
/// of the orthographic view and the distance of the perspective view are kept in step, so switching keeps the size of
/// what is looked at.</para>
/// <para>Matrices follow System.Numerics (row vectors); <see cref="Gl.UniformMatrix"/> hands them to GL unchanged.</para>
/// </remarks>
public sealed class OrbitCamera
{
    /// <summary>Vertical field of view of the perspective view, in radians.</summary>
    public const float FieldOfView = 30 * MathF.PI / 180;

    private static readonly float TanHalf = MathF.Tan(FieldOfView / 2);

    /// <summary>The point looked at; zoom and turns keep it in the middle unless they say otherwise.</summary>
    public Vector3 Target { get; set; }

    /// <summary>Turn around +Z in radians; 0 = looking along +Y (from the front).</summary>
    public float Yaw { get; set; }

    /// <summary>Tilt in radians, −π/2 … π/2; positive = looking down from above.</summary>
    public float Pitch { get; set; }

    /// <summary>Distance of the eye from <see cref="Target"/> (perspective view).</summary>
    public float Distance { get; set; } = 100;

    public bool Orthographic { get; set; } = true;

    /// <summary>Viewport in pixels.</summary>
    public int Width { get; set; } = 1;

    public int Height { get; set; } = 1;

    /// <summary>Centre and radius of everything drawn (model and ground), for the clip planes.</summary>
    public Vector3 SceneCentre { get; set; }

    public float SceneRadius { get; set; } = 1;

    /// <summary>Visible height in world units at the target (orthographic view: everywhere).</summary>
    public float ViewHeight => 2 * Distance * TanHalf;

    /// <summary>Unit vector the eye looks along.</summary>
    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch), -MathF.Sin(Pitch));

    /// <summary>Screen right in world space (always horizontal).</summary>
    public Vector3 Right => new(MathF.Cos(Yaw), -MathF.Sin(Yaw), 0);

    /// <summary>Screen up in world space.</summary>
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public Vector3 Eye => Target - Forward * Distance;

    public Matrix4x4 View
    {
        get
        {
            // Rows of the rotation: right, up, back (−forward)
            Vector3 r = Right, u = Up, f = Forward, e = Eye;
            return new Matrix4x4(
                r.X, u.X, -f.X, 0,
                r.Y, u.Y, -f.Y, 0,
                r.Z, u.Z, -f.Z, 0,
                -Vector3.Dot(r, e), -Vector3.Dot(u, e), Vector3.Dot(f, e), 1);
        }
    }

    public Matrix4x4 Projection
    {
        get
        {
            var aspect = (float)Width / Math.Max(1, Height);
            // Clip planes tight around the scene for depth precision
            var depth = Vector3.Dot(SceneCentre - Eye, Forward);
            var radius = SceneRadius * 1.05f + 1e-6f;
            var far = depth + radius;
            if (Orthographic)
            {
                var h = ViewHeight;
                return Matrix4x4.CreateOrthographic(h * aspect, h, depth - radius, far);
            }
            var near = Math.Max(depth - radius, radius * 1e-3f);
            return Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, near, Math.Max(far, near * 2));
        }
    }

    public Matrix4x4 ViewProjection => View * Projection;

    /// <summary>World units per pixel at the depth of <paramref name="p"/>.</summary>
    public float UnitsPerPixel(Vector3 p)
    {
        if (Orthographic) return ViewHeight / Math.Max(1, Height);
        var depth = Math.Max(Vector3.Dot(p - Eye, Forward), Distance * 1e-3f);
        return 2 * depth * TanHalf / Math.Max(1, Height);
    }

    /// <summary>Ray through a pixel (origin on the near side, unit direction).</summary>
    public (Vector3 Origin, Vector3 Direction) Ray(float px, float py)
    {
        var x = 2 * px / Math.Max(1, Width) - 1;
        var y = 1 - 2 * py / Math.Max(1, Height);
        if (Orthographic)
        {
            var h = ViewHeight;
            var w = h * Width / Math.Max(1, Height);
            var origin = Target + Right * (x * w / 2) + Up * (y * h / 2) - Forward * (SceneRadius * 4 + Distance);
            return (origin, Forward);
        }
        var aspect = (float)Width / Math.Max(1, Height);
        var dir = Vector3.Normalize(Forward + Right * (x * TanHalf * aspect) + Up * (y * TanHalf));
        return (Eye, dir);
    }

    /// <summary>Pixel position of a world point, and whether it lies in front of the eye.</summary>
    public (float X, float Y, bool Visible) Project(Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), ViewProjection);
        if (c.W <= 1e-12f) return (0, 0, false);
        var nx = c.X / c.W;
        var ny = c.Y / c.W;
        return ((nx + 1) / 2 * Width, (1 - ny) / 2 * Height, true);
    }

    /// <summary>Turns around a pivot: <paramref name="dYaw"/> around +Z, <paramref name="dPitch"/> tilt (clamped to straight above/below).</summary>
    public void Orbit(Vector3 pivot, float dYaw, float dPitch)
    {
        var oldBasis = Basis();
        Yaw = NormaliseAngle(Yaw + dYaw);
        Pitch = Math.Clamp(Pitch + dPitch, -MathF.PI / 2, MathF.PI / 2);
        // Move the target with the same rotation, so that the pivot stays where it is on screen
        var rotation = Matrix4x4.Transpose(oldBasis) * Basis();
        Target = pivot + Vector3.Transform(Target - pivot, rotation);
    }

    /// <summary>Moves the view by pixels (positive dx: content moves right).</summary>
    public void Pan(float dx, float dy, Vector3 atDepthOf)
    {
        var k = UnitsPerPixel(atDepthOf);
        Target -= Right * (dx * k) - Up * (dy * k);
    }

    /// <summary>Zooms by a factor (&lt; 1 = closer) keeping <paramref name="anchor"/> at its place on screen.</summary>
    /// <param name="factor">Below 1 closer, above 1 farther.</param>
    /// <param name="anchor">Point under the mouse (on the surface or on the plane through the centre).</param>
    /// <param name="minStep">
    /// Perspective only: the eye moves at least this far towards or away from the anchor per step, so that it goes through
    /// the surface instead of only creeping up to it, and back out the same way; 0 = never through.
    /// </param>
    /// <remarks>
    /// Scaling the eye and the target around a point on the ray under the mouse keeps that ray – and so the point under
    /// the mouse – where it is, in both projections. Close to a surface in the perspective view the eye moves on along
    /// the ray by <paramref name="minStep"/> instead: through the wall, and what lies behind it comes into view (the near
    /// clip plane cuts away what is in front of the eye). Zooming out mirrors this. In both projections the target keeps
    /// its depth: after flying through, the surface under the mouse is often right in front of the eye, and scaling the
    /// target around it would barely move the eye while the target ran off until the distance limit stopped everything
    /// (error memory #120); orthographic, anchors behind cut-away walls would push it out of the model.
    /// </remarks>
    /// <param name="minViewHeight">Orthographic only: the view never shows less than this height.</param>
    /// <returns>False if the orthographic view is already at <paramref name="minViewHeight"/> and could not zoom in further.</returns>
    public bool Zoom(float factor, Vector3 anchor, float minStep = 0, float minViewHeight = 0)
    {
        if (!Orthographic) return ZoomPerspective(factor, anchor, minStep);
        var floor = Math.Max(SceneRadius * 1e-3f, minViewHeight / (2 * TanHalf));
        var newDistance = Math.Clamp(Distance * factor, floor, SceneRadius * 200);
        if (factor < 1 && newDistance >= Distance * 0.9999f) return false;
        factor = newDistance / Distance;
        Distance = newDistance;
        var target = anchor + (Target - anchor) * factor;
        Target = target + Forward * Vector3.Dot(Target - target, Forward);
        return true;
    }

    /// <summary>
    /// Perspective zoom: the eye moves along the ray to the anchor by the share 1 − <paramref name="factor"/> of the way,
    /// at least <paramref name="minStep"/>. The target keeps its depth – sideways it moves with the eye, the distance takes
    /// up the rest – and is pushed ahead once the eye comes close to it; zooming out brings it back to the depth of the
    /// scene centre.
    /// </summary>
    private bool ZoomPerspective(float factor, Vector3 anchor, float minStep)
    {
        var toAnchor = anchor - Eye;
        var length = toAnchor.Length();
        // Along the ray to the point under the mouse; once past it (or right on it), straight on by the smallest step –
        // a share of the way to a point behind the eye would grow with every step
        var ahead = length > 1e-9f && Vector3.Dot(toAnchor, Forward) > 0;
        var dir = ahead ? toAnchor / length : Forward;
        if (!ahead) length = minStep > 0 ? 0 : Distance;
        var step = length * (1 - factor);   // positive towards the anchor
        if (minStep > 0 && factor != 1 && Math.Abs(step) < minStep) step = factor < 1 ? minStep : -minStep;
        var move = dir * step;
        var depth = Vector3.Dot(move, Forward);
        var max = SceneRadius * 200;
        if (Distance - depth > max)
        {
            if (depth >= 0 || Distance >= max) return false;
            move *= (Distance - max) / depth;
            depth = Vector3.Dot(move, Forward);
        }
        if (move.LengthSquared() == 0) return false;
        var target = Target + move - Forward * depth;
        var distance = Distance - depth;
        // Pushed ahead, the target stays a few steps in front of the eye: panning and the section steps scale with its
        // distance and would come to a standstill right at the eye
        var floor = Math.Max(SceneRadius * 1e-3f, 8 * minStep);
        if (distance < floor)
        {
            target += Forward * (floor - distance);
            distance = floor;
        }
        // Zooming out, a target pushed beyond the model comes back to the depth of its centre (the view stays the same)
        var centreDepth = Vector3.Dot(SceneCentre - (target - Forward * distance), Forward);
        if (step < 0 && distance > centreDepth && centreDepth > floor)
        {
            target -= Forward * (distance - centreDepth);
            distance = centreDepth;
        }
        Target = target;
        Distance = distance;
        return true;
    }

    /// <summary>Puts a sphere into view with a small margin.</summary>
    /// <param name="centre">Centre of the model.</param>
    /// <param name="radius">Radius of a sphere around it.</param>
    public void Fit(Vector3 centre, float radius)
    {
        Target = centre;
        var aspect = (float)Width / Math.Max(1, Height);
        var fill = 1.1f * Math.Max(1, 1 / aspect);   // narrow windows need the width
        Distance = Orthographic ? radius * fill / TanHalf : radius * fill / MathF.Sin(FieldOfView / 2);
    }

    /// <summary>Yaw and pitch of a standard view.</summary>
    public static (float Yaw, float Pitch) Angles(StandardView view) => view switch
    {
        StandardView.Front => (0, 0),
        StandardView.Back => (MathF.PI, 0),
        StandardView.Left => (MathF.PI / 2, 0),
        StandardView.Right => (-MathF.PI / 2, 0),
        StandardView.Top => (0, MathF.PI / 2),
        StandardView.Bottom => (0, -MathF.PI / 2),
        _ => (-MathF.PI / 4, MathF.Atan(1 / MathF.Sqrt(2))),
    };

    /// <summary>State for animating between views.</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance) State
    {
        get => (Target, Yaw, Pitch, Distance);
        set => (Target, Yaw, Pitch, Distance) = value;
    }

    /// <summary>Blend of two states: shortest way around for the yaw, distances geometrically.</summary>
    public static (Vector3 Target, float Yaw, float Pitch, float Distance) Blend(
        (Vector3 Target, float Yaw, float Pitch, float Distance) a, (Vector3 Target, float Yaw, float Pitch, float Distance) b, float t)
    {
        var s = t * t * (3 - 2 * t);   // ease in and out
        var dYaw = NormaliseAngle(b.Yaw - a.Yaw);
        return (Vector3.Lerp(a.Target, b.Target, s), NormaliseAngle(a.Yaw + dYaw * s), a.Pitch + (b.Pitch - a.Pitch) * s,
            a.Distance * MathF.Pow(b.Distance / a.Distance, s));
    }

    private static float NormaliseAngle(float a)
    {
        a %= 2 * MathF.PI;
        if (a > MathF.PI) a -= 2 * MathF.PI;
        if (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }

    /// <summary>Rotation whose rows are right, up and forward: v · B maps camera axes to world, v · Bᵀ world to camera axes.</summary>
    private Matrix4x4 Basis()
    {
        Vector3 r = Right, u = Up, f = Forward;
        return new Matrix4x4(
            r.X, r.Y, r.Z, 0,
            u.X, u.Y, u.Z, 0,
            f.X, f.Y, f.Z, 0,
            0, 0, 0, 1);
    }
}
