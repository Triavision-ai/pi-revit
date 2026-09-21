using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class GetElementRelationships : ITool
{
    public string Name => "get_element_relationships";
    public string Label => "Get Element Relationships";
    public string Tier => "advanced";
    public string Description => "Inspect host-document element relationships: type, level, owning view, host, family parent/subcomponents, group or assembly membership, joined geometry and logical dependents. Dependents are not a complete deletion-impact prediction. Pagination applies independently to each relationship; links are not traversed.";
    private static readonly string[] Kinds = { "type", "level", "owner_view", "host", "parent", "subcomponents", "group", "assembly", "members", "joined", "dependents" };
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            element_id = new { type = "integer" },
            relationships = new { type = "array", items = new { type = "string", @enum = Kinds } },
            offset = new { type = "integer", minimum = 0 },
            limit = new { type = "integer", minimum = 1, maximum = 200 },
        },
        required = new[] { "element_id" },
    };

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        var id = JsonArgs.GetLong(args, "element_id") ?? throw new ArgumentException("element_id is required.");
        var element = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException("element_id does not exist in the active document.");
        var kinds = (IReadOnlyList<string>?)JsonArgs.GetStringArray(args, "relationships") ?? Kinds;
        if (kinds.Any(kind => !Kinds.Contains(kind))) throw new ArgumentException("Unknown relationship kind.");
        int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0));
        int limit = Math.Clamp(JsonArgs.GetInt(args, "limit", 100), 1, 200);
        var result = new Dictionary<string, object?>();
        foreach (var kind in kinds.Distinct())
        {
            IEnumerable<ElementId> ids = kind switch
            {
                "type" => One(element.GetTypeId()),
                "level" => One(element.LevelId),
                "owner_view" => One(element.OwnerViewId),
                "host" => One((element as FamilyInstance)?.Host?.Id),
                "parent" => One((element as FamilyInstance)?.SuperComponent?.Id),
                "subcomponents" => element is FamilyInstance family ? family.GetSubComponentIds() : Array.Empty<ElementId>(),
                "group" => One(element.GroupId),
                "assembly" => One(element.AssemblyInstanceId),
                "members" => element is Group group ? group.GetMemberIds()
                    : element is AssemblyInstance assembly ? assembly.GetMemberIds() : Array.Empty<ElementId>(),
                "joined" => Joined(doc, element),
                "dependents" => element.GetDependentElements(null),
                _ => Array.Empty<ElementId>(),
            };
            var all = ids.Where(x => x != ElementId.InvalidElementId).DistinctBy(x => x.Value).OrderBy(x => x.Value).ToList();
            var rows = all.Skip(offset).Take(limit).Select(x =>
            {
                var related = doc.GetElement(x);
                return new { id = x.Value, name = related?.Name, category = related?.Category?.Name, unique_id = related?.UniqueId };
            }).ToArray();
            result[kind] = new { total_count = all.Count, returned_count = rows.Length, offset,
                has_more = offset + rows.Length < all.Count,
                next_offset = offset + rows.Length < all.Count ? (int?)(offset + rows.Length) : null, elements = rows };
        }
        return new { document_id = DocumentGuard.GetIdentity(doc), element_id = id, relationships = result };
    }

    private static IEnumerable<ElementId> One(ElementId? id)
        => id == null || id == ElementId.InvalidElementId ? Array.Empty<ElementId>() : new[] { id };

    private static IEnumerable<ElementId> Joined(Document doc, Element element)
    {
        // JoinGeometryUtils rejects family documents; surface that restriction explicitly.
        if (doc.IsFamilyDocument) throw new ArgumentException("joined relationships require a project document. Request other relationship kinds for families.");
        return JoinGeometryUtils.GetJoinedElements(doc, element);
    }
}
