using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class GetLinkedModels : ITool
{
    public string Name => "get_linked_models";
    public string Label => "Get Linked Models";
    public string Tier => "advanced";
    public string Description => "List placed Revit links, including unloaded instances, exact linked document identities and their transforms into host coordinates. Nested links are not traversed. Coordinates and transform origins use internal feet.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            offset = new { type = "integer", minimum = 0 },
            limit = new { type = "integer", minimum = 1, maximum = 1000 },
        },
        required = Array.Empty<string>(),
    };

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0));
        int limit = Math.Clamp(JsonArgs.GetInt(args, "limit", 100), 1, 1000);
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>().OrderBy(x => x.Id.Value).ToList();
        var rows = links.Skip(offset).Take(limit).Select(link =>
        {
            var linked = link.GetLinkDocument();
            var transform = link.GetTotalTransform();
            return new
            {
                link_instance_id = link.Id.Value,
                unique_id = link.UniqueId,
                name = link.Name,
                type_id = link.GetTypeId().Value,
                loaded = linked != null,
                linked_document_id = linked == null ? null : DocumentGuard.GetIdentity(linked),
                document_title = linked?.Title,
                transform = new
                {
                    origin = Coordinates(transform.Origin),
                    basis_x = Coordinates(transform.BasisX),
                    basis_y = Coordinates(transform.BasisY),
                    basis_z = Coordinates(transform.BasisZ),
                },
            };
        }).ToArray();
        return new
        {
            document_id = DocumentGuard.GetIdentity(doc),
            coordinate_unit = "ft",
            total_count = links.Count,
            returned_count = rows.Length,
            offset,
            has_more = offset + rows.Length < links.Count,
            next_offset = offset + rows.Length < links.Count ? (int?)(offset + rows.Length) : null,
            links = rows,
        };
    }

    internal static double[] Coordinates(XYZ point) => new[] { point.X, point.Y, point.Z };
}
