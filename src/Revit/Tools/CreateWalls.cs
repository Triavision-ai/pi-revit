using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitBridge.Tools;

/// <summary>Bounded creation of native wall paths and vertical profiles.</summary>
internal sealed class CreateWalls : ITool
{
    public string Name => "create_walls";
    public IReadOnlyList<string> DocumentKinds => DocumentKind.ProjectOnly;
    public IReadOnlyList<string> Keywords => new[] { "create walls", "draw walls", "batch walls", "model walls", "wall lines" };
    public IReadOnlyList<ToolLimit> Limits => new[]
    {
        new ToolLimit("Wall attachments and editing an existing wall profile", "api", "Wall.AddAttachment (Revit 2025.2+); Wall.CreateProfileSketch and SketchEditScope"),
        new ToolLimit("Wall type or level creation and wall type layers, width or materials", "api", "ElementType.Duplicate; Level.Create; HostObjAttributes.SetCompoundStructure"),
        new ToolLimit("Wall openings and non-end geometry joins", "api", "Document.NewOpening; JoinGeometryUtils.JoinGeometry"),
    };
    public string? Verification => "reread";
    public string Label => "Create Walls";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Create 1–100 Basic, Curtain or Stacked walls using existing wall types and a base level. Path shapes: straight line, circular arc, elliptical arc (absolute document-internal XY); or a nonrectangular vertical profile from XYZ vertices. Path walls use height or top level, offsets, and optional vertical/slanted/tapered cross-section (type-dependent). Basic path walls can align six location-line planes. Optional flip, structural use, room bounding and end joins. Native Revit validation decides type/shape compatibility. Default partial success, atomic rollback, preview rollback, and retained actual properties. No model save.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            unit = ModelEditInputs.LengthUnitSchema,
            walls = new { type = "array", minItems = 1, maxItems = 100, items = new
            {
                type = "object", properties = new
                {
                    shape = new { type = "string", @enum = new[] { "line", "arc", "ellipse", "profile" }, description = "Default line. Arc/ellipse use center, radii and angles; profile uses profile_vertices." },
                    start = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number" }, description = "Absolute document-internal XY start for a line; selected location-line plane lies on this path." },
                    end = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number" }, description = "Absolute document-internal XY end for a line." },
                    center = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number" }, description = "Absolute document-internal XY center for arc or ellipse." },
                    radius = new { type = "number", exclusiveMinimum = 0, description = "Circular arc radius in unit." },
                    x_radius = new { type = "number", exclusiveMinimum = 0, description = "Elliptical arc radius along +X in unit." },
                    y_radius = new { type = "number", exclusiveMinimum = 0, description = "Elliptical arc radius along +Y in unit." },
                    start_angle_deg = new { type = "number", description = "Arc/ellipse start angle in degrees from +X." },
                    end_angle_deg = new { type = "number", description = "Arc/ellipse end angle; must exceed start by less than 360 degrees." },
                    profile_vertices = new { type = "array", minItems = 3, maxItems = 100, items = new { type = "array", minItems = 3, maxItems = 3, items = new { type = "number" } }, description = "Ordered closed boundary vertices [x,y,z] in absolute document-internal coordinates. Closing edge is implicit. Must be planar and vertical. No height/top/base-offset fields." },
                    wall_type_id = new { type = "integer", minimum = 1 }, level_id = new { type = "integer", minimum = 1 },
                    height = new { type = "number", exclusiveMinimum = 0, description = "Unconnected height in unit. Provide exactly one of height or top_level_id." },
                    top_level_id = new { type = "integer", minimum = 1, description = "Existing top level for a constrained wall. Provide exactly one of height or top_level_id." },
                    top_offset = new { type = "number", description = "Offset above/below top_level_id in unit; only with top_level_id." },
                    base_offset = new { type = "number", description = "Offset above/below level_id in unit." },
                    cross_section = new { type = "string", @enum = new[] { "Vertical", "SingleSlanted", "Tapered" }, description = "Default Vertical. Native support depends on wall type and path." },
                    slant_angle_deg = new { type = "number", description = "Angle from vertical in degrees; requires SingleSlanted." },
                    exterior_angle_deg = new { type = "number", description = "Exterior tapered angle in degrees; requires Tapered and a compatible variable-layer wall type." },
                    interior_angle_deg = new { type = "number", description = "Interior tapered angle in degrees; requires Tapered and a compatible variable-layer wall type." },
                    location_line = new { type = "string", @enum = new[] { "WallCenterline", "CoreCenterline", "FinishFaceExterior", "FinishFaceInterior", "CoreExterior", "CoreInterior" }, description = "Selected wall plane follows the requested line or arc path; default WallCenterline. Ellipse and profile require WallCenterline." },
                    flip = new { type = "boolean", description = "Reverse the default exterior side. Default false." },
                    structural = new { type = "boolean", description = "Legacy structural flag. Use this or structural_usage, not both; default false." },
                    structural_usage = new { type = "string", @enum = new[] { "NonBearing", "Bearing", "Shear", "Combined" }, description = "StructuralWallUsage enum; use this or structural, not both." },
                    room_bounding = new { type = "boolean" },
                    allow_join_start = new { type = "boolean", description = "Allow/disallow joins at start of wall curve; default Revit behavior." },
                    allow_join_end = new { type = "boolean", description = "Allow/disallow joins at end of wall curve; default Revit behavior." },
                }, required = new[] { "wall_type_id", "level_id" },
            } },
            preview = ModelEditInputs.PreviewSchema,
            atomic = new { type = "boolean", description = "Roll back all walls if any row fails. Default false." },
        }, required = new[] { "unit", "walls" },
    };

    private sealed record Input(long TypeId, long LevelId, long? TopLevelId, string Shape, XYZ? Start, XYZ? End,
        XYZ? Center, double Radius, double YRadius, double StartAngle, double EndAngle, XYZ[]? Profile,
        double? Height, double BaseOffset, double TopOffset, WallLocationLine LocationLine, bool Flip,
        WallCrossSection CrossSection, double? SlantAngle, double? ExteriorAngle, double? InteriorAngle,
        bool Structural, StructuralWallUsage? StructuralUsage, bool? RoomBounding, bool? AllowJoinStart, bool? AllowJoinEnd);

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        double scale = ModelEditInputs.LengthScale(args);
        if (!args.TryGetProperty("walls", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 100)
            throw new ArgumentException("walls must contain 1–100 rows.");
        var inputs = rows.EnumerateArray().Select(row => Read(row, scale, doc.Application.ShortCurveTolerance)).ToArray();
        var steps = inputs.Select(input => new ModelEditBatch.Step(
            new() { ["wall_type_id"] = input.TypeId, ["level_id"] = input.LevelId, ["requested_shape"] = input.Shape },
            () =>
            {
                var type = doc.GetElement(new ElementId(input.TypeId)) as WallType ?? throw new ArgumentException($"Wall type {input.TypeId} not found.");
                var level = doc.GetElement(new ElementId(input.LevelId)) as Level
                    ?? throw new ArgumentException($"Base level {input.LevelId} not found.");
                Level? topLevel = null;
                if (input.TopLevelId is long topId)
                    topLevel = doc.GetElement(new ElementId(topId)) as Level
                        ?? throw new ArgumentException($"Top level {topId} not found.");
                Wall wall;
                if (input.Shape == "profile")
                {
                    var vertices = input.Profile!;
                    var edges = new List<Curve>(vertices.Length);
                    for (int i = 0; i < vertices.Length; i++)
                        edges.Add(Line.CreateBound(vertices[i], vertices[(i + 1) % vertices.Length]));
                    var firstHorizontal = edges.Select(e => e.GetEndPoint(1) - e.GetEndPoint(0))
                        .First(delta => Math.Abs(delta.X) + Math.Abs(delta.Y) > doc.Application.ShortCurveTolerance);
                    var normal = XYZ.BasisZ.CrossProduct(new XYZ(firstHorizontal.X, firstHorizontal.Y, 0)).Normalize();
                    if (input.Flip) normal = normal.Negate();
                    wall = Wall.Create(doc, edges, type.Id, level.Id, input.Structural, normal);
                }
                else
                {
                    double height = input.Height
                        ?? topLevel!.ProjectElevation + input.TopOffset - level.ProjectElevation - input.BaseOffset;
                    if (!double.IsFinite(height) || height <= doc.Application.ShortCurveTolerance || height > 30000)
                        throw new ArgumentException("Top level and offsets must give a positive height no greater than 30000 feet.");
                    // ProjectElevation uses the document internal Z frame; Elevation may use a shared datum.
                    Curve baseline = BuildPath(input, level.ProjectElevation);
                    wall = Wall.Create(doc, baseline, type.Id, level.Id, height, input.BaseOffset, input.Flip, input.Structural);
                }
                if (wall == null) throw new InvalidOperationException("Revit did not create a wall.");
                if (input.LocationLine != WallLocationLine.WallCenterline)
                {
                    if (input.Shape == "ellipse")
                        throw new ArgumentException("Elliptical wall paths require WallCenterline; Revit rejects an offset ellipse as a wall location curve.");
                    var compound = type.GetCompoundStructure()
                        ?? throw new ArgumentException($"Wall type {input.TypeId} has no compound structure for location-line alignment.");
                    double offset = compound.GetOffsetForLocationLine(input.LocationLine);
                    // Signed offset is from wall center toward exterior. Orientation includes flip and
                    // line direction. Setting the parameter alone does not move the wall geometry.
                    doc.Regenerate();
                    if (input.Shape == "line")
                    {
                        XYZ translation = wall.Orientation.Negate().Multiply(offset);
                        if (translation.GetLength() > 1e-9)
                            ElementTransformUtils.MoveElement(doc, wall.Id, translation);
                    }
                    else if (Math.Abs(offset) > 1e-9)
                    {
                        var location = wall.Location as LocationCurve
                            ?? throw new InvalidOperationException("Wall has no location curve for curved alignment.");
                        var original = location.Curve;
                        var shifted = original.CreateOffset(offset, XYZ.BasisZ);
                        var desired = wall.Orientation.Negate().Multiply(offset);
                        var movement = shifted.GetEndPoint(0) - original.GetEndPoint(0);
                        if (movement.DotProduct(desired) < 0)
                            shifted = original.CreateOffset(-offset, XYZ.BasisZ);
                        location.Curve = shifted;
                    }
                    SetInteger(wall, BuiltInParameter.WALL_KEY_REF_PARAM, (int)input.LocationLine);
                }
                if (topLevel != null)
                {
                    SetElementId(wall, BuiltInParameter.WALL_HEIGHT_TYPE, topLevel.Id);
                    SetDouble(wall, BuiltInParameter.WALL_TOP_OFFSET, input.TopOffset);
                }
                if (input.StructuralUsage is { } usage)
                    wall.StructuralUsage = usage;
                if (input.CrossSection != WallCrossSection.Vertical)
                {
                    wall.CrossSection = input.CrossSection;
                    if (input.SlantAngle is { } slant)
                        SetDouble(wall, BuiltInParameter.WALL_SINGLE_SLANT_ANGLE_FROM_VERTICAL, slant);
                    if (input.CrossSection == WallCrossSection.Tapered &&
                        (input.ExteriorAngle != null || input.InteriorAngle != null))
                    {
                        SetInteger(wall, BuiltInParameter.WALL_TAPERED_USE_INSTANCE_ANGLES, 1);
                        if (input.ExteriorAngle is { } exterior)
                            SetDouble(wall, BuiltInParameter.WALL_TAPERED_EXTERIOR_INWARD_ANGLE, exterior);
                        if (input.InteriorAngle is { } interior)
                            SetDouble(wall, BuiltInParameter.WALL_TAPERED_INTERIOR_INWARD_ANGLE, interior);
                    }
                }
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
        string shape = JsonArgs.GetString(row, "shape") ?? "line";
        if (shape is not ("line" or "arc" or "ellipse" or "profile"))
            throw new ArgumentException("shape must be line, arc, ellipse or profile.");
        XYZ? start = null, end = null, center = null;
        XYZ[]? profile = null;
        double radius = 0, yRadius = 0, startAngle = 0, endAngle = 0;
        if (shape == "line")
        {
            start = Point(row, "start", scale);
            end = Point(row, "end", scale);
            if (start.DistanceTo(end) <= tolerance)
                throw new ArgumentException("Wall endpoints must be farther apart than Revit's short-curve tolerance.");
        }
        else if (shape is "arc" or "ellipse")
        {
            center = Point(row, "center", scale);
            radius = ModelEditInputs.Number(row, shape == "arc" ? "radius" : "x_radius") * scale;
            yRadius = shape == "arc" ? radius : ModelEditInputs.Number(row, "y_radius") * scale;
            if (!double.IsFinite(radius) || !double.IsFinite(yRadius) ||
                radius <= tolerance || yRadius <= tolerance || radius > 30000 || yRadius > 30000)
                throw new ArgumentException("Arc and ellipse radii must exceed short-curve tolerance and be <=30000 feet.");
            startAngle = ModelEditInputs.Number(row, "start_angle_deg") * Math.PI / 180;
            endAngle = ModelEditInputs.Number(row, "end_angle_deg") * Math.PI / 180;
            double sweep = endAngle - startAngle;
            if (!double.IsFinite(startAngle) || !double.IsFinite(endAngle) ||
                sweep <= 0 || sweep >= 2 * Math.PI || Math.Min(radius, yRadius) * sweep <= tolerance)
                throw new ArgumentException("Arc/ellipse angle sweep must be >0 and <360 degrees, and the curve must exceed short-curve tolerance.");
        }
        else
        {
            if (!row.TryGetProperty("profile_vertices", out var vertices) || vertices.ValueKind != JsonValueKind.Array ||
                vertices.GetArrayLength() is < 3 or > 100)
                throw new ArgumentException("profile_vertices must contain 3–100 XYZ points.");
            profile = vertices.EnumerateArray().Select(value => Point3(value, scale)).ToArray();
            XYZ? horizontal = null;
            for (int i = 0; i < profile.Length; i++)
            {
                XYZ delta = profile[(i + 1) % profile.Length] - profile[i];
                if (delta.GetLength() <= tolerance)
                    throw new ArgumentException("Every profile edge must exceed Revit's short-curve tolerance.");
                if (horizontal == null && Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) > tolerance)
                    horizontal = new XYZ(delta.X, delta.Y, 0).Normalize();
            }
            if (horizontal == null || profile.Max(p => p.Z) - profile.Min(p => p.Z) <= tolerance)
                throw new ArgumentException("Profile must have horizontal width and vertical height.");
            foreach (var vertex in profile)
            {
                XYZ delta = vertex - profile[0];
                if (Math.Abs(delta.X * horizontal.Y - delta.Y * horizontal.X) > tolerance)
                    throw new ArgumentException("Profile vertices must lie in one vertical plane.");
            }
        }
        long? topLevelId = JsonArgs.GetLong(row, "top_level_id");
        bool hasHeight = row.TryGetProperty("height", out var heightValue) && heightValue.ValueKind != JsonValueKind.Null;
        if (shape != "profile" && hasHeight == (topLevelId != null))
            throw new ArgumentException("Provide exactly one of height or top_level_id.");
        if (shape == "profile" && (hasHeight || topLevelId != null))
            throw new ArgumentException("Profile vertices define the wall height; omit height and top_level_id.");
        if (topLevelId is <= 0) throw new ArgumentException("top_level_id must be positive.");
        double? height = hasHeight ? ModelEditInputs.Number(row, "height") * scale : null;
        double baseOffset = OptionalLength(row, "base_offset", scale);
        double topOffset = OptionalLength(row, "top_offset", scale);
        if (topLevelId == null && row.TryGetProperty("top_offset", out var topOffsetValue) && topOffsetValue.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("top_offset requires top_level_id.");
        if (shape == "profile" && row.TryGetProperty("base_offset", out var profileOffset) && profileOffset.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("Profile vertices use absolute Z; omit base_offset.");
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
        if (shape == "profile" && locationLine != WallLocationLine.WallCenterline)
            throw new ArgumentException("Profile walls require WallCenterline; their XYZ vertices define the profile plane.");
        string? crossSectionName = JsonArgs.GetString(row, "cross_section");
        WallCrossSection crossSection = crossSectionName switch
        {
            null or "Vertical" => WallCrossSection.Vertical,
            "SingleSlanted" => WallCrossSection.SingleSlanted,
            "Tapered" => WallCrossSection.Tapered,
            _ => throw new ArgumentException("cross_section must be a WallCrossSection enum name."),
        };
        if (shape == "profile" && crossSection != WallCrossSection.Vertical)
            throw new ArgumentException("Profile walls require a vertical cross-section.");
        double? slantAngle = OptionalAngle(row, "slant_angle_deg");
        double? exteriorAngle = OptionalAngle(row, "exterior_angle_deg");
        double? interiorAngle = OptionalAngle(row, "interior_angle_deg");
        if (slantAngle != null && crossSection != WallCrossSection.SingleSlanted)
            throw new ArgumentException("slant_angle_deg requires SingleSlanted cross_section.");
        if ((exteriorAngle != null || interiorAngle != null) && crossSection != WallCrossSection.Tapered)
            throw new ArgumentException("Tapered angles require Tapered cross_section.");
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
        return new Input(typeId, levelId, topLevelId, shape, start, end, center, radius, yRadius, startAngle, endAngle, profile,
            height, baseOffset, topOffset, locationLine, OptionalBool(row, "flip") ?? false,
            crossSection, slantAngle, exteriorAngle, interiorAngle, structuralAtCreation, usage,
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

    private static XYZ Point3(JsonElement value, double scale)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw new ArgumentException("Each profile vertex must be an XYZ array.");
        var coordinates = value.EnumerateArray().ToArray();
        if (coordinates.Any(v => v.ValueKind != JsonValueKind.Number ||
            !v.TryGetDouble(out double n) || !double.IsFinite(n)))
            throw new ArgumentException("Profile vertices must contain finite numbers.");
        var point = new XYZ(coordinates[0].GetDouble() * scale,
            coordinates[1].GetDouble() * scale, coordinates[2].GetDouble() * scale);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            throw new ArgumentException("Profile vertex is outside the representable range.");
        return point;
    }

    private static double? OptionalAngle(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        double degrees = ModelEditInputs.Number(row, key);
        if (!double.IsFinite(degrees) || degrees <= -89 || degrees >= 89)
            throw new ArgumentException($"{key} must be between -89 and 89 degrees.");
        return degrees * Math.PI / 180;
    }

    private static Curve BuildPath(Input input, double z)
    {
        static XYZ AtZ(XYZ point, double elevation) => new(point.X, point.Y, elevation);
        return input.Shape switch
        {
            "line" => Line.CreateBound(AtZ(input.Start!, z), AtZ(input.End!, z)),
            "arc" => Arc.Create(AtZ(input.Center!, z), input.Radius, input.StartAngle,
                input.EndAngle, XYZ.BasisX, XYZ.BasisY),
            "ellipse" => Ellipse.CreateCurve(AtZ(input.Center!, z), input.Radius, input.YRadius,
                XYZ.BasisX, XYZ.BasisY, input.StartAngle, input.EndAngle),
            _ => throw new ArgumentException("Unsupported path shape."),
        };
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
            wall_kind = wall.WallType.Kind.ToString(),
            location_curve_type = curve?.GetType().Name,
            profile_sketch_id = wall.SketchId == ElementId.InvalidElementId ? (long?)null : wall.SketchId.Value,
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
            slant_angle_rad = wall.CrossSection == WallCrossSection.SingleSlanted
                ? wall.get_Parameter(BuiltInParameter.WALL_SINGLE_SLANT_ANGLE_FROM_VERTICAL)?.AsDouble() : null,
            tapered_exterior_angle_rad = wall.CrossSection == WallCrossSection.Tapered
                ? wall.get_Parameter(BuiltInParameter.WALL_TAPERED_EXTERIOR_INWARD_ANGLE)?.AsDouble() : null,
            tapered_interior_angle_rad = wall.CrossSection == WallCrossSection.Tapered
                ? wall.get_Parameter(BuiltInParameter.WALL_TAPERED_INTERIOR_INWARD_ANGLE)?.AsDouble() : null,
            structural_usage = wall.StructuralUsage.ToString(),
            room_bounding = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.AsInteger() == 1,
            allow_join_start = WallUtils.IsWallJoinAllowedAtEnd(wall, 0),
            allow_join_end = WallUtils.IsWallJoinAllowedAtEnd(wall, 1),
        };
    }
}
