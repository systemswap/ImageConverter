using System.Numerics;

namespace ImageConverter.Viewer3D.Editing;

/// <summary>Shape of a body to add or remove.</summary>
public enum BodyShape
{
    Box,
    Cylinder,
}

/// <summary>
/// A body in model coordinates: a box from <paramref name="Min"/> to <paramref name="Max"/>, or a cylinder that fills that
/// box along <paramref name="Axis"/> (diameter: the smaller of the two other sizes).
/// </summary>
/// <param name="Shape">Box or cylinder.</param>
/// <param name="Min">Lower corner.</param>
/// <param name="Max">Upper corner.</param>
/// <param name="Axis">Cylinder axis: 0 X, 1 Y, 2 Z.</param>
public sealed record Body(BodyShape Shape, Vector3 Min, Vector3 Max, int Axis = 2);
