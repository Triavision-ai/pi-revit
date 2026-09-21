using System.Text.Json;

namespace Autodesk.Revit.DB
{
    public sealed record XYZ(double X, double Y, double Z);

    // Affine point mapping only; these fixtures do not load the Revit API.
    public sealed class Transform(XYZ basisX, XYZ basisY, XYZ basisZ, XYZ origin)
    {
        public static Transform Identity => Translation(0, 0, 0);
        public static Transform Translation(double x, double y, double z) =>
            new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(x, y, z));
        public static Transform RotationZ(double angle, XYZ? origin = null) =>
            new(new(Math.Cos(angle), Math.Sin(angle), 0),
                new(-Math.Sin(angle), Math.Cos(angle), 0), new(0, 0, 1), origin ?? new(0, 0, 0));
        public XYZ OfPoint(XYZ point) => new(
            origin.X + basisX.X * point.X + basisY.X * point.Y + basisZ.X * point.Z,
            origin.Y + basisX.Y * point.X + basisY.Y * point.Y + basisZ.Y * point.Z,
            origin.Z + basisX.Z * point.X + basisY.Z * point.Y + basisZ.Z * point.Z);
    }

    public sealed class BoundingBoxXYZ
    {
        public XYZ Min { get; init; } = new(0, 0, 0);
        public XYZ Max { get; init; } = new(0, 0, 0);
        public Transform Transform { get; init; } = Transform.Identity;
    }

    // Nongeometry members exist solely to compile the complete production file.
    // Throwing prevents an accidental query call from passing with fabricated data.
    public sealed record ElementId(long Value);
    public class Element
    {
        public string UniqueId => throw new NotSupportedException();
        public ElementId Id => throw new NotSupportedException();
        public BoundingBoxXYZ? Bounds { get; init; }
        public BoundingBoxXYZ? get_BoundingBox(object? view) => view == null
            ? Bounds : throw new NotSupportedException("Fixtures provide model bounds only.");
    }
    public sealed class Document
    {
        public Element GetElement(ElementId id) => throw new NotSupportedException();
    }
    public sealed class RevitLinkInstance : Element
    {
        public Document? GetLinkDocument() => throw new NotSupportedException();
        public Transform GetTotalTransform() => throw new NotSupportedException();
    }
}

namespace RevitBridge
{
    internal interface ITool { }
    internal sealed record ToolContext(Autodesk.Revit.DB.Document? Document, object? UIApplication);
    internal sealed record ToolOutput(object? Payload, string? CompactText = null);
    internal sealed class NoActiveDocumentException : Exception { }
}

namespace RevitBridge.Tools
{
    internal sealed class GetElements
    {
        public object ParametersSchema => throw new NotSupportedException();
        public object Execute(JsonElement args, ToolContext context) => throw new NotSupportedException();
    }
    internal static class DocumentGuard
    {
        public static string GetIdentity(Autodesk.Revit.DB.Document document) => throw new NotSupportedException();
    }
    internal static class JsonArgs
    {
        public static long? GetLong(JsonElement args, string name) => throw new NotSupportedException();
        public static string? GetString(JsonElement args, string name) => throw new NotSupportedException();
        public static bool GetBool(JsonElement args, string name, bool defaultValue) => throw new NotSupportedException();
    }
}
