using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitBridge.Tools;

/// <summary>Places bounded point instances of already loaded, unhosted level-based symbols.</summary>
internal sealed class PlaceFamilyInstances : ITool
{
    public string Name => "place_family_instances";
    public IReadOnlyList<string> DocumentKinds => DocumentKind.ProjectOnly;
    public IReadOnlyList<string> Keywords => new[] { "place family", "create component", "batch family instances", "place objects", "model components" };
    public IReadOnlyList<ToolLimit> Limits => new[]
    {
        new ToolLimit("Hosted, face-based, work-plane, adaptive, curve-driven and structural placement", "api", "Family.FamilyPlacementType; Document.Create.NewFamilyInstance"),
        new ToolLimit("Loading missing families or creating types", "api", "Document.LoadFamily; FamilySymbol.Duplicate"),
        new ToolLimit("Setting instance parameters after creation", "tool", "set_parameters"),
    };
    public string? Verification => "reread";
    public string Label => "Place Family Instances";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Place 1–100 unhosted, OneLevelBased point family instances with StructuralType.NonStructural using loaded family symbol and level IDs and explicit document internal XYZ coordinates and units. Other placement kinds are refused. Inactive symbols are activated within the transaction. Input shapes, units and finite values are checked before editing; symbol and level compatibility is checked per row. Default partial success; atomic rolls back all rows if any fails. preview commit-validates and rolls back. Created IDs in proposed preview or atomic rollback rows are temporary. Reread committed IDs; no model save is performed.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            unit = ModelEditInputs.LengthUnitSchema,
            instances = new { type = "array", minItems = 1, maxItems = 100, items = new
            {
                type = "object", properties = new
                {
                    family_symbol_id = new { type = "integer", minimum = 1 }, level_id = new { type = "integer", minimum = 1 },
                    point = ModelEditInputs.VectorSchema("Absolute XYZ in document internal coordinates, in unit."),
                }, required = new[] { "family_symbol_id", "level_id", "point" },
            } },
            preview = ModelEditInputs.PreviewSchema,
            atomic = new { type = "boolean", description = "Roll back all placements if any row fails. Default false." },
        }, required = new[] { "unit", "instances" },
    };

    private sealed record Input(long SymbolId, long LevelId, XYZ Point);

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        double scale = ModelEditInputs.LengthScale(args);
        if (!args.TryGetProperty("instances", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 100)
            throw new ArgumentException("instances must contain 1–100 rows.");
        var inputs = rows.EnumerateArray().Select(row => Read(row, scale)).ToArray();
        var steps = inputs.Select(input => new ModelEditBatch.Step(
            new() { ["family_symbol_id"] = input.SymbolId, ["level_id"] = input.LevelId },
            () =>
            {
                var symbol = doc.GetElement(new ElementId(input.SymbolId)) as FamilySymbol ?? throw new ArgumentException($"Family symbol {input.SymbolId} not found.");
                var level = doc.GetElement(new ElementId(input.LevelId)) as Level ?? throw new ArgumentException($"Level {input.LevelId} not found.");
                var kind = symbol.Family.FamilyPlacementType;
                if (kind != FamilyPlacementType.OneLevelBased)
                    throw new ArgumentException($"Family symbol {input.SymbolId} uses {kind} placement; only unhosted OneLevelBased symbols are supported.");
                bool activated = !symbol.IsActive;
                if (activated) { symbol.Activate(); doc.Regenerate(); }
                var instance = doc.Create.NewFamilyInstance(input.Point, symbol, level, StructuralType.NonStructural)
                    ?? throw new InvalidOperationException("Revit did not create a family instance.");
                return new() { ["created_id"] = instance.Id.Value, ["unique_id"] = instance.UniqueId,
                    ["activated_symbol"] = activated,
                    ["created_id_is_temporary"] = JsonArgs.GetBool(args, "preview", false),
                    ["coordinate_system"] = "document_internal", ["unit"] = "feet" };
            }, row => new[] { (long)row["created_id"]! })).ToArray();
        var batch = ModelEditBatch.Run(doc, Name, args, steps);
        foreach (var row in batch.Proposed) row["created_id_is_temporary"] = true;
        return new { created = batch.Committed ? batch.Succeeded.Count : 0, committed = batch.Committed, preview = batch.Preview, atomic = batch.Atomic,
            succeeded = batch.Succeeded, proposed = batch.Proposed, failed = batch.Failed,
            commitWarnings = batch.Warnings, commitDiagnostics = batch.CommitDiagnostics,
            commit_validation_performed = batch.CommitValidationPerformed };
    }

    private static Input Read(JsonElement row, double scale)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each instance must be an object.");
        long symbolId = JsonArgs.GetLong(row, "family_symbol_id") ?? 0, levelId = JsonArgs.GetLong(row, "level_id") ?? 0;
        if (symbolId <= 0 || levelId <= 0) throw new ArgumentException("Each instance needs positive family_symbol_id and level_id.");
        var point = ModelEditInputs.Vector(row, "point").Multiply(scale);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            throw new ArgumentException("point coordinates are outside the representable range.");
        return new Input(symbolId, levelId, point);
    }
}
