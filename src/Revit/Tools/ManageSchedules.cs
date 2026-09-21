using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class ManageSchedules : ITool
{
    public string Name => "manage_schedules";
    public string Label => "Manage Schedules";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Create or configure a regular schedule in one atomic step. Create requires category and name; optional area_scheme_id supports area schedules. Configure requires schedule_id. Discover add_fields using get_schedule_fields (parameter_id + field_type), or add field_type Count without a parameter_id. update_fields, sort_fields and filters use schedule-local field_id from get_schedules. Supplied sort_fields/filters replace their entire lists; empty arrays clear them. Numeric measured filter values require a compatible unit; returned numeric filter values use internal units. Column widths require explicit length units and apply to both grid and sheet. preview=true commit-validates then rolls back; created schedule/field IDs in previews are temporary. Revision schedules, templates, embedded schedules and calculated/combined-field authoring are outside this tool.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            action = new { type = "string", @enum = new[] { "create", "configure" } },
            schedule_id = new { type = "integer", minimum = 1 }, category = new { type = "string" }, name = new { type = "string", minLength = 1 }, area_scheme_id = new { type = "integer", minimum = 1 },
            is_itemized = new { type = "boolean" }, preview = ModelEditInputs.PreviewSchema,
            add_fields = new { type = "array", maxItems = 50, items = new { type = "object", properties = new
            {
                parameter_id = new { type = "integer", description = "May be a negative built-in ID; omit for Count." }, field_type = new { type = "string" },
                heading = new { type = "string" }, hidden = new { type = "boolean" }, width = new { type = "number", exclusiveMinimum = 0 }, unit = ModelEditInputs.LengthUnitSchema,
            }, required = new[] { "field_type" } } },
            update_fields = new { type = "array", maxItems = 50, items = new { type = "object", properties = new
            {
                field_id = new { type = "integer", minimum = 0 }, heading = new { type = "string" }, hidden = new { type = "boolean" }, width = new { type = "number", exclusiveMinimum = 0 }, unit = ModelEditInputs.LengthUnitSchema,
            }, required = new[] { "field_id" } } },
            sort_fields = new { type = "array", maxItems = 4, items = new { type = "object", properties = new { field_id = new { type = "integer", minimum = 0 }, descending = new { type = "boolean" }, show_header = new { type = "boolean" } }, required = new[] { "field_id" } } },
            filters = new { type = "array", maxItems = 8, items = new { type = "object", properties = new
            {
                field_id = new { type = "integer", minimum = 0 }, comparison = new { type = "string", @enum = new[] { "equals", "not_equals", "contains", "greater_than", "less_than" } },
                value_type = new { type = "string", @enum = new[] { "string", "number", "integer", "element_id" } }, value = new { description = "Filter value matching value_type." }, unit = new { type = "string", description = "Required for measured numbers; compatible forge unit name, e.g. meters, squareMeters, degrees." },
            }, required = new[] { "field_id", "comparison", "value_type", "value" } } },
        }, required = new[] { "action" },
    };

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        string action = JsonArgs.GetString(args, "action") ?? "";
        if (action is not ("create" or "configure")) throw new ArgumentException("action must be create or configure.");
        var additions = Array(args, "add_fields", 50); var updates = Array(args, "update_fields", 50);
        var sorts = Array(args, "sort_fields", 4); var filters = Array(args, "filters", 8);
        return ModelEditBatch.Run(doc, Name, args, new[] { new ModelEditBatch.Step(new() { ["action"] = action }, () =>
        {
            ViewSchedule schedule;
            if (action == "create")
            {
                string category = JsonArgs.GetString(args, "category") ?? throw new ArgumentException("category is required for creation.");
                var categoryId = CategoryResolver.Resolve(doc, category);
                schedule = JsonArgs.GetLong(args, "area_scheme_id") is long area ? ViewSchedule.CreateSchedule(doc, categoryId, new ElementId(area)) : ViewSchedule.CreateSchedule(doc, categoryId);
                if (!args.TryGetProperty("name", out _)) throw new ArgumentException("name is required for creation.");
            }
            else
            {
                if (args.TryGetProperty("category", out _) || args.TryGetProperty("area_scheme_id", out _)) throw new ArgumentException("category and area_scheme_id are creation-only.");
                schedule = doc.GetElement(new ElementId(JsonArgs.GetLong(args, "schedule_id") ?? 0)) as ViewSchedule ?? throw new ArgumentException("schedule_id is not a schedule.");
            }
            if (schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule || schedule.Definition.IsEmbedded) throw new ArgumentException("Use a regular, non-template schedule.");
            if (args.TryGetProperty("name", out var name))
            {
                if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) throw new ArgumentException("name must be nonempty.");
                schedule.Name = name.GetString()!;
            }
            var definition = schedule.Definition;
            var added = new List<int>();
            foreach (var input in additions)
            {
                string typeName = JsonArgs.GetString(input, "field_type") ?? "";
                if (typeName.Length == 0 || !char.IsLetter(typeName[0]) || !Enum.TryParse<ScheduleFieldType>(typeName, false, out var type) || !Enum.IsDefined(type)) throw new ArgumentException("Use a field_type from get_schedule_fields, or Count.");
                ScheduleField field;
                if (type == ScheduleFieldType.Count)
                {
                    if (input.TryGetProperty("parameter_id", out _)) throw new ArgumentException("Count has no parameter_id.");
                    field = definition.AddField(type);
                }
                else
                {
                    long parameterId = JsonArgs.GetLong(input, "parameter_id") ?? throw new ArgumentException("parameter_id is required for this field.");
                    var eligible = definition.GetSchedulableFields().FirstOrDefault(f => f.ParameterId.Value == parameterId && f.FieldType == type) ?? throw new ArgumentException("That parameter_id/field_type pair is not eligible for this schedule.");
                    field = definition.AddField(eligible);
                }
                ConfigureField(field, input); added.Add(field.FieldId.IntegerValue);
            }
            var seen = new HashSet<int>();
            foreach (var input in updates)
            {
                var id = FieldId(input);
                if (!seen.Add(id.IntegerValue)) throw new ArgumentException("update_fields contains a duplicate field_id.");
                ConfigureField(definition.GetField(id), input);
            }
            if (args.TryGetProperty("is_itemized", out _)) definition.IsItemized = Bool(args, "is_itemized");
            if (args.TryGetProperty("sort_fields", out _))
                definition.SetSortGroupFields(sorts.Select(input => new ScheduleSortGroupField(FieldId(input), Bool(input, "descending") ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending) { ShowHeader = Bool(input, "show_header") }).ToList());
            if (args.TryGetProperty("filters", out _)) definition.SetFilters(filters.Select(input => Filter(definition, input)).ToList());
            doc.Regenerate();
            return new() { ["schedule_id"] = schedule.Id.Value, ["unique_id"] = schedule.UniqueId, ["name"] = schedule.Name,
                ["created"] = action == "create", ["id_is_temporary"] = action == "create" && JsonArgs.GetBool(args, "preview", false),
                ["added_field_ids"] = added, ["added_field_ids_are_temporary"] = JsonArgs.GetBool(args, "preview", false),
                ["is_itemized"] = definition.IsItemized, ["field_count"] = definition.GetFieldCount(), ["filter_count"] = definition.GetFilterCount(), ["sort_count"] = definition.GetSortGroupFieldCount() };
        }) }).Payload;
    }

    private static List<JsonElement> Array(JsonElement args, string name, int max)
    {
        if (!args.TryGetProperty(name, out var value)) return new();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > max) throw new ArgumentException($"{name} must be an array of at most {max} entries.");
        var items = value.EnumerateArray().ToList();
        if (items.Any(item => item.ValueKind != JsonValueKind.Object)) throw new ArgumentException($"{name} entries must be objects.");
        return items;
    }
    private static bool Bool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"{name} must be boolean.");
        return value.GetBoolean();
    }
    private static ScheduleFieldId FieldId(JsonElement input)
    {
        long id = JsonArgs.GetLong(input, "field_id") ?? -1;
        return id is >= 0 and <= int.MaxValue ? new ScheduleFieldId((int)id) : throw new ArgumentException("field_id must be a nonnegative schedule-local ID.");
    }
    private static void ConfigureField(ScheduleField field, JsonElement input)
    {
        if (input.TryGetProperty("heading", out var heading)) field.ColumnHeading = heading.ValueKind == JsonValueKind.String ? heading.GetString()! : throw new ArgumentException("heading must be a string.");
        if (input.TryGetProperty("hidden", out _)) field.IsHidden = Bool(input, "hidden");
        if (input.TryGetProperty("width", out _))
        {
            double width = ModelEditInputs.Number(input, "width") * ModelEditInputs.LengthScale(input);
            if (width <= 0) throw new ArgumentException("Column width must be positive.");
            field.GridColumnWidth = width; field.SheetColumnWidth = width;
        }
    }
    private static ScheduleFilter Filter(ScheduleDefinition definition, JsonElement input)
    {
        var id = FieldId(input); var field = definition.GetField(id);
        var comparison = JsonArgs.GetString(input, "comparison") switch
        {
            "equals" => ScheduleFilterType.Equal, "not_equals" => ScheduleFilterType.NotEqual, "contains" => ScheduleFilterType.Contains,
            "greater_than" => ScheduleFilterType.GreaterThan, "less_than" => ScheduleFilterType.LessThan, _ => throw new ArgumentException("Unknown comparison."),
        };
        if (comparison == ScheduleFilterType.Contains ? !definition.CanFilterBySubstring(id) : !definition.CanFilterByValue(id)) throw new ArgumentException("This field does not support that filter operation.");
        if (!input.TryGetProperty("value", out var value)) throw new ArgumentException("Filter value is required.");
        string? type = JsonArgs.GetString(input, "value_type");
        if (type == "string" && value.ValueKind == JsonValueKind.String) return new ScheduleFilter(id, comparison, value.GetString()!);
        if (type == "integer" && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int integer)) return new ScheduleFilter(id, comparison, integer);
        if (type == "element_id" && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long elementId)) return new ScheduleFilter(id, comparison, new ElementId(elementId));
        if (type == "number")
        {
            double number = ModelEditInputs.Number(input, "value");
            var spec = field.GetSpecTypeId(); string? unit = JsonArgs.GetString(input, "unit");
            if (UnitUtils.IsMeasurableSpec(spec))
            {
                if (string.IsNullOrWhiteSpace(unit)) throw new ArgumentException("Measured numeric filters require an explicit unit.");
                var unitId = UnitResolver.Resolve(unit);
                if (!UnitUtils.IsValidUnit(spec, unitId)) throw new ArgumentException("Filter unit does not match the field's specification.");
                number = UnitUtils.ConvertToInternalUnits(number, unitId);
            }
            else if (unit != null) throw new ArgumentException("This numeric field has no measurable unit.");
            return new ScheduleFilter(id, comparison, number);
        }
        throw new ArgumentException("Filter value does not match value_type.");
    }
}
