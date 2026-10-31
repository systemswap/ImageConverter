using System.Globalization;
using ImageConverter.Viewer3D.Geometry;

namespace ImageConverter.Viewer3D;

/// <summary>Texts for measured values: lengths in millimetres (or inches), angles in degrees, in the current culture.</summary>
public static class MeasureText
{
    public const double MillimetresPerInch = 25.4;

    /// <summary>A length in file units (taken as millimetres).</summary>
    public static string Length(double mm, bool inches) =>
        inches
            ? (mm / MillimetresPerInch).ToString("N3", CultureInfo.CurrentCulture) + " in"
            : mm.ToString("N2", CultureInfo.CurrentCulture) + " mm";

    public static string Angle(double degrees) => degrees.ToString("N2", CultureInfo.CurrentCulture) + "°";

    /// <summary>An area in square file units: mm² below 1 cm², otherwise cm² (or in²).</summary>
    public static string Area(double mm2, bool inches) =>
        inches ? (mm2 / (MillimetresPerInch * MillimetresPerInch)).ToString("N3", CultureInfo.CurrentCulture) + " in²"
        : mm2 >= 100 ? (mm2 / 100).ToString("N2", CultureInfo.CurrentCulture) + " cm²"
        : mm2.ToString("N2", CultureInfo.CurrentCulture) + " mm²";

    /// <summary>A volume in cubic file units: mm³ below 1 cm³, otherwise cm³ (or in³).</summary>
    public static string Volume(double mm3, bool inches) =>
        inches ? (mm3 / Math.Pow(MillimetresPerInch, 3)).ToString("N3", CultureInfo.CurrentCulture) + " in³"
        : mm3 >= 1000 ? (mm3 / 1000).ToString("N2", CultureInfo.CurrentCulture) + " cm³"
        : mm3.ToString("N1", CultureInfo.CurrentCulture) + " mm³";

    /// <summary>Name and value, for example "Abstand 12,50 mm" or "Ø A 6,00 mm".</summary>
    public static string Value(MeasureValue v, bool inches)
    {
        var number = v.Quantity == Quantity.Angle ? Angle(v.Value) : Length(v.Value, inches);
        return Name(v) + " " + number;
    }

    /// <summary>The value alone, with a short sign for diameters, radii and angles.</summary>
    public static string Short(MeasureValue v, bool inches) => v.Quantity switch
    {
        Quantity.Angle => "∠ " + Angle(v.Value),
        Quantity.Diameter => "Ø " + Length(v.Value, inches),
        Quantity.Radius => "R " + Length(v.Value, inches),
        _ => Length(v.Value, inches),
    };

    public static string Name(MeasureValue v)
    {
        var of = v.Of switch { 1 => " A", 2 => " B", _ => "" };
        return v.Quantity switch
        {
            Quantity.Distance => Tr.T("Abstand"),
            Quantity.Angle => Tr.T("Winkel"),
            Quantity.AxisDistance => Tr.T("Achsabstand"),
            Quantity.MinDistance => Tr.T("Kürzester Abstand"),
            Quantity.DistanceAtClick => Tr.T("Abstand am Klickpunkt"),
            Quantity.MaxDistance => Tr.T("Größter Abstand"),
            Quantity.Diameter => "Ø" + of,
            Quantity.Radius => "R" + of,
            Quantity.DeltaX => "ΔX",
            Quantity.DeltaY => "ΔY",
            Quantity.DeltaZ => "ΔZ",
            _ => "",
        };
    }

    /// <summary>What a pick is: Ebene, Zylinder, Fläche, Punkt.</summary>
    public static string Kind(Pick p, FaceSet? faces)
    {
        if (p.Kind == PickKind.Point) return Tr.T("Punkt");
        return faces?.Faces[p.Face].Kind switch
        {
            FaceKind.Plane => Tr.T("Ebene"),
            FaceKind.Cylinder => Tr.T("Zylinder"),
            _ => Tr.T("Fläche"),
        };
    }
}
