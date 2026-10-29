using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

/// <summary>Bounded, model-only creation of straight walls with explicit type, level and units.</summary>
internal sealed class CreateWalls : ITool
{
    public string Name => "create_walls";
    public IReadOnlyList<string> DocumentKinds => DocumentKind.ProjectOnly;
    public IReadOnlyList<string> Keywords => new[] { "create walls", "draw walls", "batch walls", "model walls", "wall lines" };
    public IReadOnlyList<ToolLimit> Limits => new[]
    {
        new ToolLimit("Curved, slanted, stacked, profile-edited and attached walls", "api", "Wall.Create; Wall.CrossSection; Wall.AddAttachment"),
        new ToolLimit("Wall type or level creation", "api", "ElementType.Duplicate; Level.Create"),
        new ToolLimit("Joining and opening placement", "api", "JoinGeometryUtils.JoinGeometry; Document.NewOpening"),
    };
    public string? Verification => "reread";
    public string Label => "Create Walls";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Create 1–100 straight, vertical basic walls from XY endpoints using explicit units, existing wall type and level IDs, positive unconnected height, optional base offset, flip and structural flags. Endpoints use document internal XY coordinates; Revit locates the base at the selected level plus base offset. Input shapes, units and finite values are checked before editing; model type and level compatibility is checked per row. Default partial success; atomic rolls back all rows if any fails. preview commit-validates and rolls back. Created IDs in proposed preview or atomic rollback rows are temporary. Reread committed IDs before further edits; no model save is performed.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            unit = ModelEditInputs.LengthUnitSchema,
            walls = new { type = "array", minItems = 1, maxItems = 100, items = new
            {
                type = "object", properties = new
                {
                    start = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number" }, description = "Document internal XY coordinates, in unit." },
                    end = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number" }, description = "Document internal XY coordinates, in unit." },
                    wall_type_id = new { type = "integer", minimum = 1 }, level_id = new { type = "integer", minimum = 1 },
                    height = new { type = "number", exclusiveMinimum = 0 }, base_offset = new { type = "number" },
                    flip = new { type = "boolean" }, structural = new { type = "boolean" },
                }, required = new[] { "start", "end", "wall_type_id", "level_id", "height" },
            } },
            preview = ModelEditInputs.PreviewSchema,
            atomic = new { type = "boolean", description = "Roll back all walls if any row fails. Default false." },
        }, required = new[] { "unit", "walls" },
    };

    private sealed record Input(long TypeId, long LevelId, XYZ Start, XYZ End, double Height, double Offset, bool Flip, bool Structural);

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        double scale = ModelEditInputs.LengthScale(args);
        if (!args.TryGetProperty("walls", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 100)
            throw new ArgumentException("walls must contain 1–100 rows.");
        var inputs = rows.EnumerateArray().Select(row => Read(row, scale, doc.Application.ShortCurveTolerance)).ToArray();
        var steps = inputs.Select(input => new ModelEditBatch.Step(
            new() { ["wall_type_id"] = input.TypeId, ["level_id"] = input.LevelId },
            () =>
            {
                var type = doc.GetElement(new ElementId(input.TypeId)) as WallType ?? throw new ArgumentException($"Wall type {input.TypeId} not found.");
                if (type.Kind != WallKind.Basic) throw new ArgumentException($"Wall type {input.TypeId} is {type.Kind}; only basic walls are supported.");
                var level = doc.GetElement(new ElementId(input.LevelId)) as Level ?? throw new ArgumentException($"Level {input.LevelId} not found.");
                // Elevation may use the shared datum; geometry is always in document internal coordinates.
                var baseline = Line.CreateBound(new XYZ(input.Start.X, input.Start.Y, level.ProjectElevation), new XYZ(input.End.X, input.End.Y, level.ProjectElevation));
                var wall = Wall.Create(doc, baseline, type.Id, level.Id, input.Height, input.Offset, input.Flip, input.Structural);
                if (wall == null) throw new InvalidOperationException("Revit did not create a wall.");
                return new() { ["created_id"] = wall.Id.Value, ["unique_id"] = wall.UniqueId,
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

    private static Input Read(JsonElement row, double scale, double tolerance)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each wall must be an object.");
        long typeId = JsonArgs.GetLong(row, "wall_type_id") ?? 0, levelId = JsonArgs.GetLong(row, "level_id") ?? 0;
        if (typeId <= 0 || levelId <= 0) throw new ArgumentException("Each wall needs positive wall_type_id and level_id.");
        XYZ start = Point(row, "start", scale), end = Point(row, "end", scale);
        if (start.DistanceTo(end) <= tolerance) throw new ArgumentException("Wall endpoints must be farther apart than Revit's short-curve tolerance.");
        double height = ModelEditInputs.Number(row, "height") * scale;
        double offset = row.TryGetProperty("base_offset", out _) ? ModelEditInputs.Number(row, "base_offset") * scale : 0;
        if (!double.IsFinite(height) || height <= 0 || height > 30000 || !double.IsFinite(offset) || Math.Abs(offset) > 30000)
            throw new ArgumentException("Wall height must be >0 and <=30000 feet; absolute base offset must be <=30000 feet.");
        return new Input(typeId, levelId, start, end, height, offset, Bool(row, "flip"), Bool(row, "structural"));
    }

    private static XYZ Point(JsonElement row, string key, double scale)
    {
        if (!row.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 2)
            throw new ArgumentException($"{key} must contain two finite XY numbers.");
        var pair = values.EnumerateArray().ToArray();
        if (pair.Any(value => value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n)))
            throw new ArgumentException($"{key} must contain finite XY numbers.");
        double x = pair[0].GetDouble() * scale, y = pair[1].GetDouble() * scale;
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException($"{key} coordinates are outside the representable range.");
        return new XYZ(x, y, 0);
    }

    private static bool Bool(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"{key} must be a boolean.");
        return value.GetBoolean();
    }
}
