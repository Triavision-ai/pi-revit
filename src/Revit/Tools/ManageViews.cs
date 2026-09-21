using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class ManageViews : ITool
{
    public string Name => "manage_views";
    public string Label => "Manage Views";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Create a plan, isometric 3D view or section; duplicate or update an existing view. Resolve view-family-type IDs with get_element_types (of_class ViewFamilyType), and level IDs with get_elements. A section uses document internal axes and explicit length units, orthogonal viewing_direction/up vectors, origin, width/height/depth. Duplication options are duplicate, with_detailing or dependent. Optional name, scale and view_template_id apply in the same transaction; -1 removes a template. Incompatible templates or template-controlled scale changes fail without removing the template. preview=true commit-validates then rolls back; created preview IDs are temporary. Use delete_elements to remove views.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            action = new { type = "string", @enum = new[] { "create_plan", "create_3d", "create_section", "duplicate", "update" } },
            view_id = new { type = "integer", description = "Existing view for duplicate/update." },
            view_family_type_id = new { type = "integer", description = "Compatible ViewFamilyType for creation." },
            level_id = new { type = "integer", description = "Level for create_plan." },
            duplicate_option = new { type = "string", @enum = new[] { "duplicate", "with_detailing", "dependent" } },
            name = new { type = "string", minLength = 1 }, scale = new { type = "integer", minimum = 1, maximum = 24000 },
            view_template_id = new { type = "integer", description = "Compatible template ID, or -1 to remove." },
            unit = ModelEditInputs.LengthUnitSchema,
            origin = ModelEditInputs.VectorSchema("Section origin in document internal coordinates, in unit."),
            viewing_direction = ModelEditInputs.VectorSchema("Section viewing direction, dimensionless nonzero vector."),
            up = ModelEditInputs.VectorSchema("Section up vector, orthogonal to viewing_direction."),
            width = new { type = "number", exclusiveMinimum = 0 }, height = new { type = "number", exclusiveMinimum = 0 }, depth = new { type = "number", exclusiveMinimum = 0 },
            preview = ModelEditInputs.PreviewSchema,
        }, required = new[] { "action" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        string action = JsonArgs.GetString(args, "action") ?? "";
        if (action is not ("create_plan" or "create_3d" or "create_section" or "duplicate" or "update")) throw new ArgumentException("Unknown view action.");
        return ModelEditBatch.Run(doc, Name, args, new[] { new ModelEditBatch.Step(new() { ["action"] = action }, () =>
        {
            ElementId Id(string key) => new(JsonArgs.GetLong(args, key) is long id && id > 0 ? id : throw new ArgumentException($"{key} must be a positive ID."));
            View view;
            object? before = null;
            if (action is "duplicate" or "update")
            {
                view = doc.GetElement(Id("view_id")) as View ?? throw new ArgumentException("view_id is not a view.");
                before = Describe(view);
                if (action == "duplicate")
                {
                    var option = (JsonArgs.GetString(args, "duplicate_option") ?? "duplicate") switch
                    {
                        "duplicate" => ViewDuplicateOption.Duplicate, "with_detailing" => ViewDuplicateOption.WithDetailing,
                        "dependent" => ViewDuplicateOption.AsDependent, _ => throw new ArgumentException("Invalid duplicate_option."),
                    };
                    if (!view.CanViewBeDuplicated(option)) throw new ArgumentException("This view cannot be duplicated with that option.");
                    view = (View)doc.GetElement(view.Duplicate(option));
                }
            }
            else if (action == "create_plan") view = ViewPlan.Create(doc, Id("view_family_type_id"), Id("level_id"));
            else if (action == "create_3d") view = View3D.CreateIsometric(doc, Id("view_family_type_id"));
            else
            {
                double factor = ModelEditInputs.LengthScale(args);
                var origin = ModelEditInputs.Vector(args, "origin").Multiply(factor);
                var direction = ModelEditInputs.Vector(args, "viewing_direction");
                var up = ModelEditInputs.Vector(args, "up");
                if (direction.GetLength() < 1e-12 || up.GetLength() < 1e-12) throw new ArgumentException("Section directions must be nonzero.");
                direction = direction.Normalize(); up = up.Normalize();
                if (Math.Abs(direction.DotProduct(up)) > 1e-8) throw new ArgumentException("Section up and viewing_direction must be orthogonal.");
                double width = ModelEditInputs.Number(args, "width") * factor, height = ModelEditInputs.Number(args, "height") * factor, depth = ModelEditInputs.Number(args, "depth") * factor;
                if (width <= 1e-6 || height <= 1e-6 || depth <= 1e-6) throw new ArgumentException("Section dimensions must exceed 0.000001 feet.");
                var transform = Transform.Identity;
                transform.Origin = origin; transform.BasisZ = direction; transform.BasisY = up; transform.BasisX = up.CrossProduct(direction).Normalize();
                using var box = new BoundingBoxXYZ { Transform = transform, Min = new XYZ(-width / 2, -height / 2, 0), Max = new XYZ(width / 2, height / 2, depth), Enabled = true };
                view = ViewSection.CreateSection(doc, Id("view_family_type_id"), box);
            }
            if (args.TryGetProperty("name", out var name))
            {
                if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) throw new ArgumentException("name must be nonempty.");
                view.Name = name.GetString()!;
            }
            if (args.TryGetProperty("view_template_id", out _))
            {
                long template = JsonArgs.GetLong(args, "view_template_id") ?? 0;
                if (template != -1 && (template <= 0 || !view.IsValidViewTemplate(new ElementId(template)))) throw new ArgumentException("view_template_id is not compatible with this view.");
                view.ViewTemplateId = new ElementId(template);
            }
            if (args.TryGetProperty("scale", out var scale))
            {
                if (!scale.TryGetInt32(out int value) || value is < 1 or > 24000) throw new ArgumentException("scale must be an integer from 1 to 24000.");
                var parameter = view.get_Parameter(BuiltInParameter.VIEW_SCALE);
                if (parameter == null) throw new ArgumentException("This view's scale is unavailable.");
                if (doc.GetElement(view.ViewTemplateId) is View template && template.GetTemplateParameterIds().Except(template.GetNonControlledTemplateParameterIds()).Contains(parameter.Id))
                    throw new ArgumentException("This view's scale is controlled by its template.");
                view.Scale = value;
            }
            doc.Regenerate();
            return new() { ["before"] = before, ["view"] = Describe(view), ["created"] = action != "update", ["id_is_temporary"] = action != "update" && JsonArgs.GetBool(args, "preview", false) };
        }) }).Payload;
    }
    private static object Describe(View view) => new { id = view.Id.Value, unique_id = view.UniqueId, name = view.Name, view_type = view.ViewType.ToString(), template_id = view.ViewTemplateId.Value, scale = view.Scale };
}
