using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class TransformElements : ITool
{
    public string Name => "transform_elements";
    public string Label => "Transform Elements";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Move, copy or rotate 1–200 elements together in the active document. Coordinates use the document internal origin and axes; unit is required. Rotation uses a right-handed axis and angle_degrees. The whole selection succeeds or rolls back as one step; pinned elements are never automatically unpinned. Revit may move constrained/hosted dependents too. preview=true validates commit then rolls back; any created IDs in a preview are temporary and must not be reused. Snapshots describe requested elements, not a complete dependent-change audit.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            action = new { type = "string", @enum = new[] { "move", "copy", "rotate" } },
            element_ids = ModelEditInputs.IdsSchema,
            unit = ModelEditInputs.LengthUnitSchema,
            translation = ModelEditInputs.VectorSchema("Move/copy displacement [x,y,z] in unit."),
            axis_origin = ModelEditInputs.VectorSchema("Rotation axis origin [x,y,z] in unit, relative to internal origin."),
            axis_direction = ModelEditInputs.VectorSchema("Nonzero rotation axis direction [x,y,z], dimensionless."),
            angle_degrees = new { type = "number", description = "Signed right-hand rotation angle in degrees." },
            preview = ModelEditInputs.PreviewSchema,
        }, required = new[] { "action", "element_ids", "unit" },
    };

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        var ids = ModelEditInputs.Ids(args);
        string action = JsonArgs.GetString(args, "action") ?? "";
        if (action is not ("move" or "copy" or "rotate")) throw new ArgumentException("action must be move, copy or rotate.");
        double scale = ModelEditInputs.LengthScale(args);
        XYZ? translation = action != "rotate" ? ModelEditInputs.Vector(args, "translation").Multiply(scale) : null;
        XYZ? origin = action == "rotate" ? ModelEditInputs.Vector(args, "axis_origin").Multiply(scale) : null;
        XYZ? direction = action == "rotate" ? ModelEditInputs.Vector(args, "axis_direction") : null;
        double angle = action == "rotate" ? ModelEditInputs.Number(args, "angle_degrees") * Math.PI / 180 : 0;
        if (direction != null && direction.GetLength() < 1e-12) throw new ArgumentException("axis_direction must be nonzero.");
        var result = ModelEditBatch.Run(doc, Name, args, new[] { new ModelEditBatch.Step(
            new() { ["action"] = action, ["element_ids"] = ids.Select(x => x.Value).ToArray() }, () =>
            {
                var elements = ids.Select(id => doc.GetElement(id) ?? throw new ArgumentException($"Element {id.Value} not found.")).ToArray();
                if (action != "copy" && elements.Any(e => e.Pinned)) throw new ArgumentException("Selection contains pinned elements; no elements were unpinned.");
                var before = elements.Select(ModelEditInputs.Snapshot).ToArray();
                ICollection<ElementId> outputIds = ids;
                if (action == "move") ElementTransformUtils.MoveElements(doc, ids, translation!);
                else if (action == "copy") outputIds = ElementTransformUtils.CopyElements(doc, ids, translation!);
                else ElementTransformUtils.RotateElements(doc, ids, Line.CreateUnbound(origin!, direction!.Normalize()), angle);
                doc.Regenerate();
                return new() { ["before"] = before, ["after"] = outputIds.Select(doc.GetElement).Where(e => e != null).Select(ModelEditInputs.Snapshot).ToArray(),
                    ["created_ids"] = action == "copy" ? outputIds.Select(id => id.Value).ToArray() : Array.Empty<long>(),
                    ["coordinate_system"] = "document_internal", ["snapshot_unit"] = "feet",
                    ["created_ids_are_temporary"] = action == "copy" && JsonArgs.GetBool(args, "preview", false) };
            }) });
        return result.Payload;
    }
}
