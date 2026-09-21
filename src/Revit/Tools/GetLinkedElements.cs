using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class GetLinkedElements : ITool
{
    public string Name => "get_linked_elements";
    public string Label => "Get Linked Elements";
    public string Tier => "advanced";
    public string Description => "Query one loaded Revit link with get_elements filters and pagination. Use link_instance_id and expected_linked_document_id from get_linked_models. Returned element IDs belong to the linked document and cannot be passed to host write tools. Optional bounds are axis-aligned in host coordinates, in internal feet. Only direct links are supported; active-view scoping is unavailable.";
    public object ParametersSchema
    {
        get
        {
            var schema = JsonSerializer.SerializeToNode(new GetElements().ParametersSchema)!.AsObject();
            var properties = schema["properties"]!.AsObject();
            properties.Remove("in_active_view");
            properties["link_instance_id"] = new JsonObject { ["type"] = "integer" };
            properties["expected_linked_document_id"] = new JsonObject { ["type"] = "string" };
            properties["include_bounds"] = new JsonObject { ["type"] = "boolean" };
            schema["required"] = new JsonArray("link_instance_id", "expected_linked_document_id");
            return schema;
        }
    }

    public object Execute(JsonElement args, ToolContext context)
    {
        var host = context.Document ?? throw new NoActiveDocumentException();
        var id = JsonArgs.GetLong(args, "link_instance_id") ?? throw new ArgumentException("link_instance_id is required.");
        var link = host.GetElement(new ElementId(id)) as RevitLinkInstance
            ?? throw new ArgumentException("link_instance_id is not a Revit link instance in the active host document.");
        var linked = link.GetLinkDocument() ?? throw new ArgumentException("The link is unloaded. Load it in Revit before querying it.");
        string identity = DocumentGuard.GetIdentity(linked);
        if (!string.Equals(JsonArgs.GetString(args, "expected_linked_document_id"), identity, StringComparison.Ordinal))
            throw new ArgumentException("The linked document identity is missing or stale. Read get_linked_models again.");
        if (JsonArgs.GetBool(args, "in_active_view", false))
            throw new ArgumentException("in_active_view is not supported for linked queries.");

        var result = (ToolOutput)new GetElements().Execute(args, new ToolContext(linked, null))!;
        var payload = JsonSerializer.SerializeToNode(result.Payload)!.AsObject();
        payload["host_document_id"] = DocumentGuard.GetIdentity(host);
        payload["linked_document_id"] = identity;
        payload["link_instance_id"] = id;
        payload["coordinate_unit"] = "ft";
        if (payload["elements"] is JsonArray elements)
        {
            var transform = link.GetTotalTransform();
            foreach (var row in elements)
            {
                var element = linked.GetElement(new ElementId(row!["id"]!.GetValue<long>()));
                row["unique_id"] = element.UniqueId;
                row["reference"] = new JsonObject
                {
                    ["host_document_id"] = DocumentGuard.GetIdentity(host),
                    ["link_instance_id"] = id,
                    ["linked_document_id"] = identity,
                    ["element_id"] = element.Id.Value,
                };
                if (JsonArgs.GetBool(args, "include_bounds", false))
                    row["host_bounds"] = JsonSerializer.SerializeToNode(HostBounds(element.get_BoundingBox(null), transform));
            }
        }
        return new ToolOutput(payload, result.CompactText);
    }

    internal static object? HostBounds(BoundingBoxXYZ? box, Transform placement)
    {
        if (box == null) return null;
        var corners = new List<XYZ>(8);
        foreach (double x in new[] { box.Min.X, box.Max.X })
        foreach (double y in new[] { box.Min.Y, box.Max.Y })
        foreach (double z in new[] { box.Min.Z, box.Max.Z })
            corners.Add(placement.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z))));
        return new
        {
            min = new[] { corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z) },
            max = new[] { corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z) },
        };
    }
}
