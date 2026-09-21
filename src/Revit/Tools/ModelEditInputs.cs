using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal static class ModelEditInputs
{
    public static object IdsSchema => new { type = "array", minItems = 1, maxItems = 200, uniqueItems = true, items = new { type = "integer", minimum = 1 } };
    public static object PreviewSchema => new { type = "boolean", description = "Commit-validate then roll back all model edits. Default false." };
    public static object LengthUnitSchema => new { type = "string", @enum = new[] { "millimeters", "centimeters", "meters", "feet", "inches" } };
    public static object VectorSchema(string description) => new { type = "array", minItems = 3, maxItems = 3, items = new { type = "number" }, description };
    public static List<ElementId> Ids(JsonElement args)
    {
        var ids = JsonArgs.GetLongArray(args, "element_ids");
        if (ids.Count is < 1 or > 200 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("element_ids must contain 1–200 distinct positive IDs.");
        return ids.Select(id => new ElementId(id)).ToList();
    }
    public static double LengthScale(JsonElement args) => JsonArgs.GetString(args, "unit") switch
    {
        "millimeters" => 1 / 304.8, "centimeters" => 1 / 30.48, "meters" => 1 / 0.3048,
        "feet" => 1, "inches" => 1 / 12.0,
        _ => throw new ArgumentException("unit must be millimeters, centimeters, meters, feet or inches."),
    };
    public static double Number(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            throw new ArgumentException($"{name} must be a finite number.");
        return number;
    }
    public static XYZ Vector(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 3)
            throw new ArgumentException($"{name} must contain exactly three finite numbers.");
        var numbers = values.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && double.IsFinite(n) ? n : throw new ArgumentException($"{name} must contain finite numbers.")).ToArray();
        return new XYZ(numbers[0], numbers[1], numbers[2]);
    }
    public static object Snapshot(Element element)
    {
        static double[] Point(XYZ p) => new[] { p.X, p.Y, p.Z };
        object? location = element.Location switch
        {
            LocationPoint p => new { kind = "point", point = Point(p.Point) },
            LocationCurve c when c.Curve.IsBound => new { kind = "curve_endpoints", start = Point(c.Curve.GetEndPoint(0)), end = Point(c.Curve.GetEndPoint(1)) } as object,
            _ => null,
        };
        return new { id = element.Id.Value, unique_id = element.UniqueId, name = element.Name, type_id = element.GetTypeId().Value, location };
    }
}
