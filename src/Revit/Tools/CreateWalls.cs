using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitBridge.Tools;

/// <summary>Bounded creation of straight, vertical basic walls with explicit placement semantics.</summary>
internal sealed class CreateWalls : ITool
{
    public string Name => "create_walls";
    public IReadOnlyList<string> DocumentKinds => DocumentKind.ProjectOnly;
    public IReadOnlyList<string> Keywords => new[] { "create walls", "draw walls", "batch walls", "model walls", "wall lines" };
    public IReadOnlyList<ToolLimit> Limits => new[]
    {
        new ToolLimit("Curved, slanted, tapered, stacked, curtain, profile-edited and attached walls", "api", "Wall.Create; Wall.CrossSection; Wall.AddAttachment (Revit 2025.2+)"),
        new ToolLimit("Wall type or level creation and wall type layers, width or materials", "api", "ElementType.Duplicate; Level.Create; HostObjAttributes.SetCompoundStructure"),
        new ToolLimit("Wall openings and non-end geometry joins", "api", "Document.NewOpening; JoinGeometryUtils.JoinGeometry"),
    };
    public string? Verification => "reread";
    public string Label => "Create Walls";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Create 1–100 straight, vertical basic walls from document-internal XY endpoints and explicit length units. Each row uses an existing wall type and base level, with exactly one top mode: positive unconnected height or top_level_id with optional top_offset. Optional location_line places the selected center/core/finish plane on the requested XY line; flip controls exterior orientation. Optional base_offset, structural or structural_usage, room_bounding and per-end join allowance. Default partial success; atomic rolls back all rows if any fails. preview commit-validates and rolls back. Retained rows include regenerated actual properties after commit, or actual_read_error if reread fails. Proposed IDs are temporary. No model save.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            unit = ModelEditInputs.LengthUnitSchema,
            walls = new { type = "array", minItems = 1, maxItems = 100, items = new
            {
                type = "object", properties = new
                {
                    start = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number" }, description = "Absolute document-internal XY coordinates in unit; selected location_line lies on this line." },
                    end = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number" }, description = "Absolute document-internal XY coordinates in unit; order determines the default exterior side." },
                    wall_type_id = new { type = "integer", minimum = 1 }, level_id = new { type = "integer", minimum = 1 },
                    height = new { type = "number", exclusiveMinimum = 0, description = "Unconnected height in unit. Provide exactly one of height or top_level_id." },
                    top_level_id = new { type = "integer", minimum = 1, description = "Existing top level for a constrained wall. Provide exactly one of height or top_level_id." },
                    top_offset = new { type = "number", description = "Offset above/below top_level_id in unit; only with top_level_id." },
                    base_offset = new { type = "number", description = "Offset above/below level_id in unit." },
                    location_line = new { type = "string", @enum = new[] { "WallCenterline", "CoreCenterline", "FinishFaceExterior", "FinishFaceInterior", "CoreExterior", "CoreInterior" }, description = "Wall plane placed on start/end line; default WallCenterline." },
                    flip = new { type = "boolean", description = "Reverse the default exterior side. Default false." },
                    structural = new { type = "boolean", description = "Legacy structural flag. Use this or structural_usage, not both; default false." },
                    structural_usage = new { type = "string", @enum = new[] { "NonBearing", "Bearing", "Shear", "Combined" }, description = "StructuralWallUsage enum; use this or structural, not both." },
                    room_bounding = new { type = "boolean" },
                    allow_join_start = new { type = "boolean", description = "Allow/disallow joins at start of wall curve; default Revit behavior." },
                    allow_join_end = new { type = "boolean", description = "Allow/disallow joins at end of wall curve; default Revit behavior." },
                }, required = new[] { "start", "end", "wall_type_id", "level_id" },
            } },
            preview = ModelEditInputs.PreviewSchema,
            atomic = new { type = "boolean", description = "Roll back all walls if any row fails. Default false." },
        }, required = new[] { "unit", "walls" },
    };

    private sealed record Input(long TypeId, long LevelId, long? TopLevelId, XYZ Start, XYZ End,
        double? Height, double BaseOffset, double TopOffset, WallLocationLine LocationLine, bool Flip,
        bool Structural, StructuralWallUsage? StructuralUsage, bool? RoomBounding, bool? AllowJoinStart, bool? AllowJoinEnd);

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
                var level = doc.GetElement(new ElementId(input.LevelId)) as Level
                    ?? throw new ArgumentException($"Base level {input.LevelId} not found.");
                Level? topLevel = null;
                if (input.TopLevelId is long topId)
                    topLevel = doc.GetElement(new ElementId(topId)) as Level
                        ?? throw new ArgumentException($"Top level {topId} not found.");
                double height = input.Height
                    ?? topLevel!.ProjectElevation + input.TopOffset - level.ProjectElevation - input.BaseOffset;
                if (!double.IsFinite(height) || height <= doc.Application.ShortCurveTolerance || height > 30000)
                    throw new ArgumentException("Top level and offsets must give a positive height no greater than 30000 feet.");
                // ProjectElevation uses the document internal Z frame; Elevation may use a shared datum.
                var baseline = Line.CreateBound(new XYZ(input.Start.X, input.Start.Y, level.ProjectElevation), new XYZ(input.End.X, input.End.Y, level.ProjectElevation));
                var wall = Wall.Create(doc, baseline, type.Id, level.Id, height, input.BaseOffset, input.Flip, input.Structural);
                if (wall == null) throw new InvalidOperationException("Revit did not create a wall.");
                if (input.LocationLine != WallLocationLine.WallCenterline)
                {
                    var compound = type.GetCompoundStructure()
                        ?? throw new ArgumentException($"Wall type {input.TypeId} has no compound structure for location-line alignment.");
                    double offset = compound.GetOffsetForLocationLine(input.LocationLine);
                    // Signed offset is from wall center toward exterior. Orientation includes flip and
                    // line direction. Setting the parameter alone does not move the wall geometry.
                    doc.Regenerate();
                    XYZ translation = wall.Orientation.Negate().Multiply(offset);
                    if (translation.GetLength() > 1e-9)
                        ElementTransformUtils.MoveElement(doc, wall.Id, translation);
                    SetInteger(wall, BuiltInParameter.WALL_KEY_REF_PARAM, (int)input.LocationLine);
                }
                if (topLevel != null)
                {
                    SetElementId(wall, BuiltInParameter.WALL_HEIGHT_TYPE, topLevel.Id);
                    SetDouble(wall, BuiltInParameter.WALL_TOP_OFFSET, input.TopOffset);
                }
                if (input.StructuralUsage is { } usage)
                    wall.StructuralUsage = usage;
                if (input.RoomBounding is { } roomBounding)
                    SetInteger(wall, BuiltInParameter.WALL_ATTR_ROOM_BOUNDING, roomBounding ? 1 : 0);
                if (input.AllowJoinStart is { } joinStart)
                    SetJoin(wall, 0, joinStart);
                if (input.AllowJoinEnd is { } joinEnd)
                    SetJoin(wall, 1, joinEnd);
                return new() { ["created_id"] = wall.Id.Value, ["unique_id"] = wall.UniqueId,
                    ["created_id_is_temporary"] = JsonArgs.GetBool(args, "preview", false),
                    ["coordinate_system"] = "document_internal", ["unit"] = "feet" };
            }, row => new[] { (long)row["created_id"]! })).ToArray();
        var batch = ModelEditBatch.Run(doc, Name, args, steps);
        foreach (var row in batch.Proposed) row["created_id_is_temporary"] = true;
        if (batch.Committed)
            foreach (var row in batch.Succeeded)
            {
                try
                {
                    var wall = doc.GetElement(new ElementId((long)row["created_id"]!)) as Wall;
                    if (wall == null) row["actual_read_error"] = "Committed wall could not be reread; inspect created_id.";
                    else row["actual"] = Describe(wall);
                }
                catch (Exception error)
                {
                    // The transaction already committed. Do not discard retained IDs and commit status
                    // merely because this optional, post-commit description could not be projected.
                    row["actual_read_error"] = error.Message.Length > 320 ? error.Message[..320] : error.Message;
                }
            }
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
        long? topLevelId = JsonArgs.GetLong(row, "top_level_id");
        bool hasHeight = row.TryGetProperty("height", out var heightValue) && heightValue.ValueKind != JsonValueKind.Null;
        if (hasHeight == (topLevelId != null))
            throw new ArgumentException("Provide exactly one of height or top_level_id.");
        if (topLevelId is <= 0) throw new ArgumentException("top_level_id must be positive.");
        double? height = hasHeight ? ModelEditInputs.Number(row, "height") * scale : null;
        double baseOffset = OptionalLength(row, "base_offset", scale);
        double topOffset = OptionalLength(row, "top_offset", scale);
        if (topLevelId == null && row.TryGetProperty("top_offset", out var topOffsetValue) && topOffsetValue.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("top_offset requires top_level_id.");
        if (height != null && (!double.IsFinite(height.Value) || height <= 0 || height > 30000))
            throw new ArgumentException("Wall height must be >0 and <=30000 feet.");
        if (Math.Abs(baseOffset) > 30000 || Math.Abs(topOffset) > 30000)
            throw new ArgumentException("Absolute base_offset and top_offset must be <=30000 feet.");
        string? lineName = JsonArgs.GetString(row, "location_line");
        WallLocationLine locationLine = lineName switch
        {
            null or "WallCenterline" => WallLocationLine.WallCenterline,
            "CoreCenterline" => WallLocationLine.CoreCenterline,
            "FinishFaceExterior" => WallLocationLine.FinishFaceExterior,
            "FinishFaceInterior" => WallLocationLine.FinishFaceInterior,
            "CoreExterior" => WallLocationLine.CoreExterior,
            "CoreInterior" => WallLocationLine.CoreInterior,
            _ => throw new ArgumentException("location_line must be a WallLocationLine enum name."),
        };
        bool? structural = OptionalBool(row, "structural");
        string? usageName = JsonArgs.GetString(row, "structural_usage");
        if (structural != null && usageName != null)
            throw new ArgumentException("Use structural or structural_usage, not both.");
        StructuralWallUsage? usage = usageName switch
        {
            null => null,
            "NonBearing" => StructuralWallUsage.NonBearing,
            "Bearing" => StructuralWallUsage.Bearing,
            "Shear" => StructuralWallUsage.Shear,
            "Combined" => StructuralWallUsage.Combined,
            _ => throw new ArgumentException("structural_usage must be a StructuralWallUsage enum name."),
        };
        bool structuralAtCreation = usage is { } selected ? selected != StructuralWallUsage.NonBearing : structural ?? false;
        return new Input(typeId, levelId, topLevelId, start, end, height, baseOffset, topOffset,
            locationLine, OptionalBool(row, "flip") ?? false, structuralAtCreation, usage,
            OptionalBool(row, "room_bounding"), OptionalBool(row, "allow_join_start"), OptionalBool(row, "allow_join_end"));
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

    private static double OptionalLength(JsonElement row, string key, double scale)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        double converted = ModelEditInputs.Number(row, key) * scale;
        if (!double.IsFinite(converted)) throw new ArgumentException($"{key} is outside the representable range.");
        return converted;
    }

    private static bool? OptionalBool(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"{key} must be a boolean.");
        return value.GetBoolean();
    }

    private static void SetInteger(Wall wall, BuiltInParameter name, int value)
    {
        var parameter = wall.get_Parameter(name) ?? throw new InvalidOperationException($"Missing {name}.");
        if (parameter.IsReadOnly) throw new InvalidOperationException($"{name} is read-only on this wall.");
        if (parameter.AsInteger() != value && !parameter.Set(value))
            throw new InvalidOperationException($"Revit refused {name}.");
    }

    private static void SetDouble(Wall wall, BuiltInParameter name, double value)
    {
        var parameter = wall.get_Parameter(name) ?? throw new InvalidOperationException($"Missing {name}.");
        if (parameter.IsReadOnly) throw new InvalidOperationException($"{name} is read-only on this wall.");
        if (Math.Abs(parameter.AsDouble() - value) > 1e-9 && !parameter.Set(value))
            throw new InvalidOperationException($"Revit refused {name}.");
    }

    private static void SetElementId(Wall wall, BuiltInParameter name, ElementId value)
    {
        var parameter = wall.get_Parameter(name) ?? throw new InvalidOperationException($"Missing {name}.");
        if (parameter.IsReadOnly) throw new InvalidOperationException($"{name} is read-only on this wall.");
        if (parameter.AsElementId() != value && !parameter.Set(value))
            throw new InvalidOperationException($"Revit refused {name}.");
    }

    private static void SetJoin(Wall wall, int end, bool allow)
    {
        if (WallUtils.IsWallJoinAllowedAtEnd(wall, end) == allow) return;
        if (allow) WallUtils.AllowWallJoinAtEnd(wall, end);
        else WallUtils.DisallowWallJoinAtEnd(wall, end);
    }

    private static object Describe(Wall wall)
    {
        var curve = (wall.Location as LocationCurve)?.Curve;
        var top = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
        var baseLevel = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
        static double[] Point(XYZ xyz) => new[] { xyz.X, xyz.Y, xyz.Z };
        return new
        {
            type_id = wall.GetTypeId().Value,
            base_level_id = baseLevel?.Value,
            top_level_id = top != null && top != ElementId.InvalidElementId ? top.Value : (long?)null,
            unconnected_height_ft = top == null || top == ElementId.InvalidElementId
                ? wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() : null,
            base_offset_ft = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble(),
            top_offset_ft = top != null && top != ElementId.InvalidElementId
                ? wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() : null,
            length_ft = curve?.Length,
            width_ft = wall.Width,
            location_line = ((WallLocationLine)(wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0)).ToString(),
            location_start_ft = curve == null ? null : Point(curve.GetEndPoint(0)),
            location_end_ft = curve == null ? null : Point(curve.GetEndPoint(1)),
            flipped = wall.Flipped,
            exterior_normal = Point(wall.Orientation),
            cross_section = wall.CrossSection.ToString(),
            structural_usage = wall.StructuralUsage.ToString(),
            room_bounding = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.AsInteger() == 1,
            allow_join_start = WallUtils.IsWallJoinAllowedAtEnd(wall, 0),
            allow_join_end = WallUtils.IsWallJoinAllowedAtEnd(wall, 1),
        };
    }
}
