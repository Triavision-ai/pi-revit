using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class ChangeElementTypes : ITool
{
    /// <summary>Updates whose per-view graphics are scanned; each scan visits every view.</summary>
    private const int ViewGraphicsLimit = 20;
    public string Name => "change_element_types";
    public IReadOnlyList<string> DocumentKinds => DocumentKind.Both;
    public IReadOnlyList<string> Keywords => new[] { "swap type", "replace type", "change family type", "retype", "switch type" };
    public IReadOnlyList<ToolLimit> Limits => new[]
    {
        new ToolLimit("Editing a type definition's parameters", "tool", "set_parameters"),
        new ToolLimit("Creating a new type", "api", "ElementType.Duplicate"),
        new ToolLimit("Loading a family", "api", "Document.LoadFamily"),
    };
    public string? Verification => "reread";
    public string Label => "Change Element Types";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Change the type of 1–200 elements using explicit element_id/type_id pairs. Revit validates each type against the target; invalid or pinned targets are reported per update. Some type changes replace the original element: always use resulting_id/unique_id afterward. Each update reports inherited_state: instance values, traits and, for the first 20 updates, views that hide or override the retyped element. Default partial success; atomic=true rolls back all if any update fails. preview=true commit-validates then rolls back, and replacement IDs from previews are temporary. Revit constraints can affect connected/hosted elements; results describe requested targets, not every dependent effect.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            updates = new { type = "array", minItems = 1, maxItems = 200, items = new
            {
                type = "object", properties = new { element_id = new { type = "integer", minimum = 1 }, type_id = new { type = "integer", minimum = 1 } },
                required = new[] { "element_id", "type_id" },
            } },
            preview = ModelEditInputs.PreviewSchema,
            atomic = new { type = "boolean", description = "Roll back the complete batch if any update fails. Default false." },
        }, required = new[] { "updates" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        if (!args.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array || updates.GetArrayLength() is < 1 or > 200)
            throw new ArgumentException("updates must contain 1–200 element_id/type_id pairs.");
        var seen = new HashSet<long>();
        var steps = new List<ModelEditBatch.Step>();
        foreach (var update in updates.EnumerateArray())
        {
            long elementId = JsonArgs.GetLong(update, "element_id") ?? 0;
            long typeId = JsonArgs.GetLong(update, "type_id") ?? 0;
            if (elementId <= 0 || typeId <= 0 || !seen.Add(elementId)) throw new ArgumentException("Each update needs positive IDs, and element_id must be unique within the batch.");
            int index = steps.Count;
            steps.Add(new(new() { ["element_id"] = elementId, ["type_id"] = typeId }, () =>
            {
                var element = doc.GetElement(new ElementId(elementId)) ?? throw new ArgumentException($"Element {elementId} not found.");
                if (element.Pinned) throw new ArgumentException("Target is pinned; it was not unpinned.");
                var type = doc.GetElement(new ElementId(typeId)) as ElementType ?? throw new ArgumentException($"Type {typeId} not found.");
                if (!element.IsValidType(type.Id)) throw new ArgumentException($"Type {typeId} is not valid for element {elementId}.");
                var before = ModelEditInputs.Snapshot(element);
                var replacement = element.ChangeTypeId(type.Id);
                doc.Regenerate();
                var resultId = replacement == ElementId.InvalidElementId ? new ElementId(elementId) : replacement;
                var result = doc.GetElement(resultId) ?? throw new InvalidOperationException("Changed element could not be resolved.");
                return new() { ["before"] = before, ["after"] = ModelEditInputs.Snapshot(result),
                    ["resulting_id"] = result.Id.Value, ["unique_id"] = result.UniqueId,
                    ["replaced"] = result.Id.Value != elementId,
                    ["resulting_id_is_temporary"] = result.Id.Value != elementId && JsonArgs.GetBool(args, "preview", false),
                    // What the retyped element keeps: instance values, traits and per-view graphics (inv:derived-state-reported).
                    ["inherited_state"] = InheritedState.OfElement(result, result.Id.Value != elementId ? elementId : null, viewGraphics: index < ViewGraphicsLimit) };
            }));
        }
        var batch = ModelEditBatch.Run(doc, Name, args, steps);
        foreach (var row in batch.Proposed)
            if (row["replaced"] is true) row["resulting_id_is_temporary"] = true;
        return batch.Payload;
    }
}
