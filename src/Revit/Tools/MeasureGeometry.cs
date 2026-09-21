using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class MeasureGeometry : ITool
{
    public string Name => "measure_geometry";
    public string Label => "Measure Geometry";
    public string Tier => "advanced";
    public string Description => "Measure exact Euclidean distance between two explicit points, or approximate separation between two host elements' model axis-aligned bounding boxes. Required unit applies to point inputs, optional clearance threshold, and all output coordinates/distances. Points use document internal axes. Box gap is a lower bound on geometry separation: zero means enclosing boxes touch/overlap, not a confirmed clash. Bounds can include nonphysical geometry. An optional clearance threshold classifies only box proximity, never a physical clearance failure. Does not traverse linked contents.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            mode = new { type = "string", @enum = new[] { "point_distance", "bounding_box_gap" } }, unit = ModelEditInputs.LengthUnitSchema,
            point_a = ModelEditInputs.VectorSchema("First point in document internal axes, in unit."), point_b = ModelEditInputs.VectorSchema("Second point in document internal axes, in unit."),
            element_a_id = new { type = "integer", minimum = 1 }, element_b_id = new { type = "integer", minimum = 1 },
            clearance = new { type = "number", minimum = 0, description = "Optional box-proximity threshold in unit, only for bounding_box_gap." },
        }, required = new[] { "mode", "unit" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        double scale = ModelEditInputs.LengthScale(args);
        string? mode = JsonArgs.GetString(args, "mode"), unit = JsonArgs.GetString(args, "unit");
        if (mode == "point_distance")
        {
            if (args.TryGetProperty("clearance", out _) || args.TryGetProperty("element_a_id", out _) || args.TryGetProperty("element_b_id", out _))
                throw new ArgumentException("point_distance accepts points, not element IDs or clearance.");
            var a = ModelEditInputs.Vector(args, "point_a").Multiply(scale); var b = ModelEditInputs.Vector(args, "point_b").Multiply(scale);
            return new { method = mode, approximate = false, coordinate_system = "document_internal", unit, distance = a.DistanceTo(b) / scale,
                delta = SpatialBounds.Coordinates(b - a, scale), point_a = SpatialBounds.Coordinates(a, scale), point_b = SpatialBounds.Coordinates(b, scale) };
        }
        if (mode != "bounding_box_gap") throw new ArgumentException("Unknown measurement mode.");
        if (args.TryGetProperty("point_a", out _) || args.TryGetProperty("point_b", out _)) throw new ArgumentException("bounding_box_gap accepts element IDs, not points.");
        Element Resolve(string key)
        {
            long id = JsonArgs.GetLong(args, key) ?? 0;
            return id > 0 ? doc.GetElement(new ElementId(id)) ?? throw new ArgumentException($"{key} {id} was not found.") : throw new ArgumentException($"{key} must be positive.");
        }
        var elementA = Resolve("element_a_id"); var elementB = Resolve("element_b_id");
        var boundsA = SpatialBounds.Of(elementA) ?? throw new ArgumentException($"Element {elementA.Id.Value} has no model bounding box.");
        var boundsB = SpatialBounds.Of(elementB) ?? throw new ArgumentException($"Element {elementB.Id.Value} has no model bounding box.");
        var gap = boundsA.Gap(boundsB); double distance = gap.GetLength() / scale;
        double? clearance = args.TryGetProperty("clearance", out _) ? ModelEditInputs.Number(args, "clearance") : null;
        if (clearance < 0) throw new ArgumentException("clearance cannot be negative.");
        return new { method = mode, approximate = true, coordinate_system = "document_internal", unit, distance, axis_gaps = SpatialBounds.Coordinates(gap, scale),
            boxes_overlap_or_touch = boundsA.Intersects(boundsB), clearance, box_gap_below_clearance = clearance.HasValue ? (bool?)(distance < clearance.Value) : null,
            interpretation = "Bounding-box gap is a lower bound on geometry separation. Overlap or a gap below clearance identifies candidates for geometry review, not a confirmed clash or clearance failure.",
            element_a = new { id = elementA.Id.Value, unique_id = elementA.UniqueId, bounds = boundsA.Describe(scale) },
            element_b = new { id = elementB.Id.Value, unique_id = elementB.UniqueId, bounds = boundsB.Describe(scale) } };
    }
}
