using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class GetMepConnections : ITool
{
    public string Name => "get_mep_connections";
    public string Label => "Get MEP Connections";
    public string Tier => "advanced";
    public string Description => "Inspect one host element's MEP connectors, system identity, physical connection status and referenced connectors. Supports MEPCurve (pipes, ducts, conduit, cable trays), MEP family instances and wires. Required unit applies to connector positions and sizes; directions are unit vectors. API AllRefs includes physical and logical references, so reference count is not a physical connection count. Unsupported properties are null with explicit unavailable reasons, never false/zero substitutes. Connector and reference pages have independent offsets. No network traversal or linked contents.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            element_id = new { type = "integer", minimum = 1 }, unit = ModelEditInputs.LengthUnitSchema,
            offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 100 },
            reference_offset = new { type = "integer", minimum = 0 }, reference_limit = new { type = "integer", minimum = 1, maximum = 100 },
        }, required = new[] { "element_id", "unit" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        long id = JsonArgs.GetLong(args, "element_id") ?? 0;
        var element = id > 0 ? doc.GetElement(new ElementId(id)) : null;
        if (element == null) throw new ArgumentException("element_id was not found.");
        double scale = ModelEditInputs.LengthScale(args);
        ConnectorManager? manager = element switch { MEPCurve curve => curve.ConnectorManager, FamilyInstance family => family.MEPModel?.ConnectorManager, _ => null };
        if (manager == null) throw new ArgumentException("This element has no supported MEP connector manager.");
        int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0)), limit = Math.Clamp(JsonArgs.GetInt(args, "limit", 50), 1, 100);
        int referenceOffset = Math.Max(0, JsonArgs.GetInt(args, "reference_offset", 0)), referenceLimit = Math.Clamp(JsonArgs.GetInt(args, "reference_limit", 50), 1, 100);
        var connectors = manager.Connectors.Cast<Connector>().OrderBy(x => x.Id).ToList();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var connector in connectors.Skip(offset).Take(limit))
        {
            var unavailable = new Dictionary<string, string>();
            object? Read(string field, Func<object?> getter)
            {
                try { return getter(); }
                catch (Exception error) when (error is Autodesk.Revit.Exceptions.InvalidOperationException or Autodesk.Revit.Exceptions.ArgumentException or InvalidOperationException or ArgumentException)
                { unavailable[field] = error.Message; return null; }
            }
            var row = new Dictionary<string, object?>
            {
                ["connector_id"] = connector.Id, ["connector_type"] = connector.ConnectorType.ToString(), ["domain"] = connector.Domain.ToString(),
                ["origin"] = Read("origin", () => SpatialBounds.Coordinates(connector.Origin, scale)),
                ["normal"] = Read("normal", () => SpatialBounds.Coordinates(connector.CoordinateSystem.BasisZ, 1)),
                ["physically_connected"] = Read("physically_connected", () => connector.IsConnected),
                ["flow_direction"] = Read("flow_direction", () => connector.Direction.ToString()),
                ["system"] = Read("system", () => connector.MEPSystem is { } system ? new { id = system.Id.Value, name = system.Name, type_id = system.GetTypeId().Value } : null),
                ["shape"] = Read("shape", () => connector.Shape.ToString()),
            };
            row["size"] = Read("size", () => connector.Shape switch
            {
                ConnectorProfileType.Round => new { diameter = 2 * connector.Radius / scale } as object,
                ConnectorProfileType.Rectangular or ConnectorProfileType.Oval => new { width = connector.Width / scale, height = connector.Height / scale },
                _ => null,
            });
            row["references"] = Read("references", () =>
            {
                var references = connector.AllRefs.Cast<Connector>().OrderBy(x => x.Owner.Id.Value).ThenBy(x => x.Id).ToList();
                var page = references.Skip(referenceOffset).Take(referenceLimit).Select(x => new { element_id = x.Owner.Id.Value, element_unique_id = x.Owner.UniqueId, connector_id = x.Id, connector_type = x.ConnectorType.ToString(), domain = x.Domain.ToString() }).ToArray();
                return new { total_count = references.Count, offset = referenceOffset, returned_count = page.Length,
                    next_offset = referenceOffset + page.Length < references.Count ? (int?)(referenceOffset + page.Length) : null, entries = page };
            });
            row["unavailable"] = unavailable;
            rows.Add(row);
        }
        return new { element_id = element.Id.Value, unique_id = element.UniqueId, name = element.Name, unit = JsonArgs.GetString(args, "unit"), coordinate_system = "document_internal",
            total_count = connectors.Count, offset, returned_count = rows.Count, next_offset = offset + rows.Count < connectors.Count ? (int?)(offset + rows.Count) : null, connectors = rows };
    }
}
