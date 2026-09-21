using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class QuerySpatialElements : ITool
{
    public string Name => "query_spatial_elements";
    public string Label => "Query Spatial Elements";
    public string Tier => "advanced";
    public string Description => "Find host elements whose model axis-aligned bounding boxes intersect or lie inside an explicit region. Coordinates use document internal axes and the required length unit. query uses get_elements filters, covering the whole scope (maximum 10,000 candidates). Includes touching boundaries; elements without bounds are counted separately. Bounds may include nonphysical geometry, so these are approximate candidates, not solid intersections or clashes. Linked contents are not traversed. Results are ordered by element ID with paging.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            query = ElementQueryScope.Schema(),
            min = ModelEditInputs.VectorSchema("Region minimum in document internal axes, in unit."),
            max = ModelEditInputs.VectorSchema("Region maximum in document internal axes, in unit."),
            unit = ModelEditInputs.LengthUnitSchema,
            relation = new { type = "string", @enum = new[] { "intersects", "inside" } },
            offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 200 },
        }, required = new[] { "min", "max", "unit" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        double scale = ModelEditInputs.LengthScale(args);
        var region = new SpatialBounds(ModelEditInputs.Vector(args, "min").Multiply(scale), ModelEditInputs.Vector(args, "max").Multiply(scale));
        if (region.Min.X > region.Max.X || region.Min.Y > region.Max.Y || region.Min.Z > region.Max.Z)
            throw new ArgumentException("Each min coordinate must be less than or equal to max.");
        string relation = JsonArgs.GetString(args, "relation") ?? "intersects";
        if (relation is not ("intersects" or "inside")) throw new ArgumentException("relation must be intersects or inside.");
        var query = ElementQueryScope.Parse(args);
        JsonElement RunQuery() => JsonSerializer.SerializeToElement(((ToolOutput)new GetElements().Execute(JsonSerializer.SerializeToElement(query), context)!).Payload);
        query["count_only"] = true;
        var counted = RunQuery();
        int count = counted.GetProperty("total_count").GetInt32();
        if (count > 10000) throw new ArgumentException($"Query has {count} candidates; narrow query to at most 10,000.");
        object? warnings = counted.TryGetProperty("warnings", out var warning) ? warning.Clone() : null;
        query["count_only"] = false; query["limit"] = 1000;
        var matches = new List<(Element Element, SpatialBounds Bounds)>();
        int withoutBounds = 0;
        for (int start = 0; start < count; start += 1000)
        {
            query["offset"] = start;
            foreach (var row in RunQuery().GetProperty("elements").EnumerateArray())
            {
                var element = doc.GetElement(new ElementId(row.GetProperty("id").GetInt64()));
                var bounds = SpatialBounds.Of(element);
                if (bounds == null) { withoutBounds++; continue; }
                if (relation == "inside" ? region.Contains(bounds) : region.Intersects(bounds)) matches.Add((element, bounds));
            }
        }
        int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0)), limit = Math.Clamp(JsonArgs.GetInt(args, "limit", 100), 1, 200);
        var elements = matches.OrderBy(x => x.Element.Id.Value).Skip(offset).Take(limit).Select(x => new
        {
            id = x.Element.Id.Value, unique_id = x.Element.UniqueId, name = x.Element.Name, category = x.Element.Category?.Name, bounds = x.Bounds.Describe(scale),
        }).ToArray();
        return new { method = "axis_aligned_bounding_boxes", approximate = true, coordinate_system = "document_internal", unit = JsonArgs.GetString(args, "unit"),
            relation, region = region.Describe(scale), candidate_count = count, without_bounds_count = withoutBounds, warnings,
            total_count = matches.Count, offset, returned_count = elements.Length, next_offset = offset + elements.Length < matches.Count ? (int?)(offset + elements.Length) : null, elements };
    }
}
