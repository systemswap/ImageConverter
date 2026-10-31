using System.Numerics;

namespace ImageConverter.Viewer3D.Geometry;

/// <summary>Finds where a ray leaves the next wall of a closed model, for zooming through it wall by wall.</summary>
public static class Walls
{
    /// <summary>
    /// The point where a ray leaves the first piece of material it meets: it enters at the first surface (outside of a
    /// face) and leaves at the next one (inside of a face). Starts inside material (first face seen from behind): that
    /// face is the exit.
    /// </summary>
    /// <param name="mesh">The model.</param>
    /// <param name="bvh">Hierarchy over all its triangles.</param>
    /// <param name="origin">Start of the ray (in front of the model or on a section plane).</param>
    /// <param name="direction">Unit direction.</param>
    /// <returns>The exit point, or null if the ray meets nothing.</returns>
    /// <remarks>
    /// Relies on outward-facing triangles (counter-clockwise seen from outside), as STL prescribes. In a file with some
    /// triangles the wrong way round, a wall may need two steps.
    /// </remarks>
    public static Vector3? ExitOfNextWall(Mesh mesh, Bvh bvh, Vector3 origin, Vector3 direction)
    {
        if (!bvh.Raycast(origin, direction, float.MaxValue, out var entry)) return null;
        var entryPoint = origin + direction * entry.Distance;
        if (Vector3.Dot(mesh.AreaNormal(entry.Triangle).ToVector3(), direction) > 0) return entryPoint;   // already inside

        // Step a little past the entry and look for the way out
        var nudge = Math.Max(1e-5f * mesh.Bounds.Diagonal, 1e-6f);
        var from = entryPoint + direction * nudge;
        for (var guard = 0; guard < 64 && bvh.Raycast(from, direction, float.MaxValue, out var next); guard++)
        {
            var p = from + direction * next.Distance;
            if (Vector3.Dot(mesh.AreaNormal(next.Triangle).ToVector3(), direction) > 0) return p;
            from = p + direction * nudge;   // another outside face (overlapping parts): keep going
        }
        return entryPoint;   // open model: no way out found
    }
}
