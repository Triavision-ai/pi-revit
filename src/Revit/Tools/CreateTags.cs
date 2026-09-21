using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;

namespace RevitBridge.Tools;

internal sealed class CreateTags : ITool
{
    public string Name => "create_tags";
    public string Label => "Create Tags";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Create up to 100 tags for host-document elements in one explicit view using a loaded tag FamilySymbol. kind=element uses IndependentTag; room, space and area use their corresponding spatial tag APIs. Targets contain element_id and head_position [x,y,z] in document internal coordinates with explicit length unit. head_position always means the tag head, including with leader=true. Spatial tags require a compatible plan view and positions at that spatial element's level; orientation applies only to element tags. Templates, perspective views and unlocked 3D views cannot host independent tags. Default partial success; atomic=true rolls back all on any failure. preview=true commit-validates then rolls back; all proposed tag IDs are temporary. Linked targets and face/subelement references are outside this tool.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            kind = new { type = "string", @enum = new[] { "element", "room", "space", "area" } },
            view_id = new { type = "integer", minimum = 1 }, tag_type_id = new { type = "integer", minimum = 1 }, unit = ModelEditInputs.LengthUnitSchema,
            targets = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object", properties = new { element_id = new { type = "integer", minimum = 1 }, head_position = ModelEditInputs.VectorSchema("Tag head in document internal coordinates, in unit.") }, required = new[] { "element_id", "head_position" } } },
            leader = new { type = "boolean" }, orientation = new { type = "string", @enum = new[] { "horizontal", "vertical" } },
            preview = ModelEditInputs.PreviewSchema, atomic = new { type = "boolean" },
        }, required = new[] { "kind", "view_id", "tag_type_id", "unit", "targets" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        string kind = JsonArgs.GetString(args, "kind") ?? "";
        if (kind is not ("element" or "room" or "space" or "area")) throw new ArgumentException("Unknown tag kind.");
        var view = doc.GetElement(new ElementId(JsonArgs.GetLong(args, "view_id") ?? 0)) as View ?? throw new ArgumentException("view_id is not a view.");
        if (view.IsTemplate || (view is View3D three && (three.IsPerspective || !three.IsLocked))) throw new ArgumentException("Tags require a non-template view; 3D views must be orthographic and locked.");
        if (kind != "element" && view is not ViewPlan) throw new ArgumentException("Spatial tags require a plan view.");
        var type = doc.GetElement(new ElementId(JsonArgs.GetLong(args, "tag_type_id") ?? 0)) as FamilySymbol ?? throw new ArgumentException("tag_type_id must identify a loaded tag FamilySymbol.");
        bool leader = false;
        if (args.TryGetProperty("leader", out var leaderValue))
        {
            if (leaderValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("leader must be boolean.");
            leader = leaderValue.GetBoolean();
        }
        if (kind != "element" && args.TryGetProperty("orientation", out _)) throw new ArgumentException("orientation applies only to element tags.");
        var orientation = (JsonArgs.GetString(args, "orientation") ?? "horizontal") switch
        {
            "horizontal" => TagOrientation.Horizontal, "vertical" => TagOrientation.Vertical, _ => throw new ArgumentException("Invalid orientation."),
        };
        if (!args.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() is < 1 or > 100) throw new ArgumentException("targets must contain 1–100 tag requests.");
        double scale = ModelEditInputs.LengthScale(args);
        var steps = new List<ModelEditBatch.Step>();
        foreach (var input in targets.EnumerateArray())
        {
            if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each target must be an object.");
            long id = JsonArgs.GetLong(input, "element_id") ?? 0;
            if (id <= 0) throw new ArgumentException("Target element_id must be positive.");
            var point = ModelEditInputs.Vector(input, "head_position").Multiply(scale);
            steps.Add(new(new() { ["element_id"] = id }, () =>
            {
                var element = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException($"Element {id} not found.");
                if (!type.IsActive) { type.Activate(); doc.Regenerate(); }
                Element tag;
                if (kind == "element")
                {
                    var independent = IndependentTag.Create(doc, type.Id, view.Id, new Reference(element), false, orientation, point);
                    independent.HasLeader = leader;
                    independent.TagHeadPosition = point;
                    tag = independent;
                }
                else
                {
                    if (element.Location is not LocationPoint location || view.GenLevel == null || element.LevelId != view.GenLevel.Id)
                        throw new ArgumentException("A spatial tag target must be placed on the plan view's level.");
                    var anchor = new UV(location.Point.X, location.Point.Y);
                    SpatialElementTag spatial = kind switch
                    {
                        "room" when element is Room => doc.Create.NewRoomTag(new LinkElementId(element.Id), anchor, view.Id),
                        "space" when element is Space space => doc.Create.NewSpaceTag(space, anchor, view),
                        "area" when element is Area area => doc.Create.NewAreaTag((ViewPlan)view, area, anchor),
                        _ => throw new ArgumentException($"Element {id} does not match kind {kind}."),
                    };
                    if (!spatial.IsValidType(type.Id)) throw new ArgumentException("Tag type does not match the spatial tag category.");
                    var replacement = spatial.ChangeTypeId(type.Id);
                    if (replacement != ElementId.InvalidElementId) spatial = (SpatialElementTag)doc.GetElement(replacement);
                    spatial.HasLeader = leader; spatial.TagHeadPosition = point;
                    tag = spatial;
                }
                doc.Regenerate();
                var actual = tag is IndependentTag independentResult ? independentResult.TagHeadPosition : ((SpatialElementTag)tag).TagHeadPosition;
                return new() { ["tag_id"] = tag.Id.Value, ["unique_id"] = tag.UniqueId, ["tag_type_id"] = tag.GetTypeId().Value, ["view_id"] = tag.OwnerViewId.Value,
                    ["kind"] = kind, ["head_position"] = new[] { actual.X, actual.Y, actual.Z }, ["unit"] = "feet", ["leader"] = leader, ["id_is_temporary"] = false };
            }));
        }
        var batch = ModelEditBatch.Run(doc, Name, args, steps);
        foreach (var row in batch.Proposed) row["id_is_temporary"] = true;
        return batch.Payload;
    }
}
