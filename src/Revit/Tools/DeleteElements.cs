using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class DeleteElements : ITool
{
    public string Name => "delete_elements";
    public string Label => "Delete Elements";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Delete 1–200 explicitly selected elements in one atomic step, returning the full set of IDs returned by Revit's deletion API, including dependents. Use preview=true to inspect that set with commit validation and confirmed rollback. Optional expected_deleted_ids rejects a changed deletion set before commit; pass the full preview set to bind a later deletion to that set. Pinned requested elements are rejected. At most 10,000 deleted IDs are permitted; a larger cascade rolls back. Returned IDs include dependencies removed by Document.Delete, not a complete audit of surviving elements modified by Revit.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            element_ids = ModelEditInputs.IdsSchema,
            preview = ModelEditInputs.PreviewSchema,
            expected_deleted_ids = new { type = "array", minItems = 1, maxItems = 10000, uniqueItems = true, items = new { type = "integer", minimum = 1 }, description = "Exact full deleted_ids set from a previous preview; mismatches roll back." },
        }, required = new[] { "element_ids" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        var ids = ModelEditInputs.Ids(args);
        List<long>? expected = args.TryGetProperty("expected_deleted_ids", out _) ? JsonArgs.GetLongArray(args, "expected_deleted_ids") : null;
        if (expected != null && (expected.Count is < 1 or > 10000 || expected.Any(x => x <= 0) || expected.Distinct().Count() != expected.Count))
            throw new ArgumentException("expected_deleted_ids must contain 1–10,000 distinct positive IDs.");
        return ModelEditBatch.Run(doc, Name, args, new[] { new ModelEditBatch.Step(
            new() { ["element_ids"] = ids.Select(x => x.Value).ToArray() }, () =>
            {
                foreach (var id in ids)
                {
                    var element = doc.GetElement(id) ?? throw new ArgumentException($"Element {id.Value} not found.");
                    if (element.Pinned) throw new ArgumentException($"Element {id.Value} is pinned; no elements were unpinned.");
                }
                var deleted = doc.Delete(ids).Select(id => id.Value).OrderBy(id => id).ToArray();
                if (deleted.Length > 10000) throw new ArgumentException("Deletion exceeds the 10,000-ID cascade limit.");
                if (expected != null && !deleted.ToHashSet().SetEquals(expected)) throw new ArgumentException("The deletion set changed since the preview; deletion was rolled back. Run a fresh preview.");
                return new() { ["deleted_ids"] = deleted, ["deleted_count"] = deleted.Length,
                    ["dependent_ids"] = deleted.Except(ids.Select(x => x.Value)).ToArray() };
            }) }).Payload;
    }
}
