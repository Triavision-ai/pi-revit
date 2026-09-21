using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class GetModelCoordinates : ITool
{
    public string Name => "get_model_coordinates";
    public string Label => "Get Model Coordinates";
    public string Tier => "advanced";
    public string Description => "Read project/survey base points, active project location and site coordinates. Optionally report shared coordinates for up to 100 points expressed along document internal axes. Required unit applies to input points and every returned length; angles and latitude/longitude are degrees. The active location's Revit GetProjectPosition defines the shared mapping. List project locations with paging. Does not infer a GIS coordinate reference system or modify/acquire coordinates.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            unit = ModelEditInputs.LengthUnitSchema,
            points = new { type = "array", maxItems = 100, items = ModelEditInputs.VectorSchema("Point along document internal axes, in unit.") },
            offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 100 },
        }, required = new[] { "unit" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        if (doc.IsFamilyDocument) throw new ArgumentException("Project coordinates require a project document.");
        double scale = ModelEditInputs.LengthScale(args);
        var active = doc.ActiveProjectLocation;
        object Position(ProjectPosition position) => new { east_west = position.EastWest / scale, north_south = position.NorthSouth / scale, elevation = position.Elevation / scale, angle_degrees = position.Angle * 180 / Math.PI };
        object? Base(BasePoint? point) => point == null ? null : new { id = point.Id.Value, internal_position = SpatialBounds.Coordinates(point.Position, scale), shared_position = SpatialBounds.Coordinates(point.SharedPosition, scale) };
        var points = new List<object>();
        if (args.TryGetProperty("points", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 100) throw new ArgumentException("points must be an array of at most 100 XYZ points.");
            foreach (var value in values.EnumerateArray())
            {
                var wrapper = JsonSerializer.SerializeToElement(new { point = value });
                var point = ModelEditInputs.Vector(wrapper, "point").Multiply(scale);
                points.Add(new { internal_position = SpatialBounds.Coordinates(point, scale), shared_position = Position(active.GetProjectPosition(point)) });
            }
        }
        var locations = doc.ProjectLocations.Cast<ProjectLocation>().OrderBy(x => x.Id.Value).ToList();
        int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0)), limit = Math.Clamp(JsonArgs.GetInt(args, "limit", 50), 1, 100);
        var page = locations.Skip(offset).Take(limit).Select(x => new { id = x.Id.Value, name = x.Name, active = x.Id == active.Id, internal_origin_in_shared_coordinates = Position(x.GetProjectPosition(XYZ.Zero)) }).ToArray();
        var site = doc.SiteLocation;
        return new { unit = JsonArgs.GetString(args, "unit"), angle_unit = "degrees", point_input_coordinates = "document_internal", active_project_location = new { id = active.Id.Value, name = active.Name },
            project_base_point = Base(BasePoint.GetProjectBasePoint(doc)), survey_point = Base(BasePoint.GetSurveyPoint(doc)),
            site = new { name = site.Name, latitude_degrees = site.Latitude * 180 / Math.PI, longitude_degrees = site.Longitude * 180 / Math.PI, time_zone = site.TimeZone },
            total_locations = locations.Count, offset, next_offset = offset + page.Length < locations.Count ? (int?)(offset + page.Length) : null, locations = page, points };
    }
}
