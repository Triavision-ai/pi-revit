using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed record SpatialBounds(XYZ Min, XYZ Max)
{
    public static SpatialBounds? Of(Element element)
    {
        var box = element.get_BoundingBox(null);
        if (box == null) return null;
        var corners = new List<XYZ>(8);
        foreach (double x in new[] { box.Min.X, box.Max.X })
        foreach (double y in new[] { box.Min.Y, box.Max.Y })
        foreach (double z in new[] { box.Min.Z, box.Max.Z })
            corners.Add(box.Transform.OfPoint(new XYZ(x, y, z)));
        return new(new(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
            new(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)));
    }

    public bool Intersects(SpatialBounds other) => Min.X <= other.Max.X && Max.X >= other.Min.X
        && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
    public bool Contains(SpatialBounds other) => Min.X <= other.Min.X && Max.X >= other.Max.X
        && Min.Y <= other.Min.Y && Max.Y >= other.Max.Y && Min.Z <= other.Min.Z && Max.Z >= other.Max.Z;
    public XYZ Gap(SpatialBounds other) => new(
        Math.Max(0, Math.Max(Min.X - other.Max.X, other.Min.X - Max.X)),
        Math.Max(0, Math.Max(Min.Y - other.Max.Y, other.Min.Y - Max.Y)),
        Math.Max(0, Math.Max(Min.Z - other.Max.Z, other.Min.Z - Max.Z)));
    public static double[] Coordinates(XYZ p, double scale) => new[] { p.X / scale, p.Y / scale, p.Z / scale };
    public object Describe(double scale) => new { min = Coordinates(Min, scale), max = Coordinates(Max, scale) };
}
