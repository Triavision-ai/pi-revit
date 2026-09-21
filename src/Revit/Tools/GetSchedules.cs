using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class GetSchedules : ITool
{
    public string Name => "get_schedules";
    public string Label => "Get Schedules";
    public string Tier => "advanced";
    public string Description => "List schedules, or read one schedule's field definitions and displayed body cells by schedule_id. Cell text uses Revit formatting and may contain group headings/totals; rows are not guaranteed to correspond to individual elements. Row and column pagination are independent. Templates and revision schedules are excluded from the list.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            schedule_id = new { type = "integer" },
            name_filter = new { type = "string" },
            offset = new { type = "integer", minimum = 0, description = "List offset or body row offset." },
            limit = new { type = "integer", minimum = 1, maximum = 200 },
            column_offset = new { type = "integer", minimum = 0 },
            column_limit = new { type = "integer", minimum = 1, maximum = 50 },
        },
        required = Array.Empty<string>(),
    };

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0));
        int limit = Math.Clamp(JsonArgs.GetInt(args, "limit", 50), 1, 200);
        if (JsonArgs.GetLong(args, "schedule_id") is not { } id)
        {
            string needle = JsonArgs.GetString(args, "name_filter") ?? "";
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Id.Value).ToList();
            var rows = schedules.Skip(offset).Take(limit).Select(s => new
            {
                id = s.Id.Value, name = s.Name, unique_id = s.UniqueId,
                category_id = s.Definition.CategoryId.Value,
                is_itemized = s.Definition.IsItemized,
                field_count = s.Definition.GetFieldCount(),
            }).ToArray();
            return new { total_count = schedules.Count, returned_count = rows.Length, offset,
                has_more = offset + rows.Length < schedules.Count,
                next_offset = offset + rows.Length < schedules.Count ? (int?)(offset + rows.Length) : null, schedules = rows };
        }

        var schedule = doc.GetElement(new ElementId(id)) as ViewSchedule
            ?? throw new ArgumentException("schedule_id is not a schedule in the active document.");
        if (schedule.IsTemplate) throw new ArgumentException("Schedule templates do not contain displayed body rows.");
        using var table = schedule.GetTableData();
        using var body = table.GetSectionData(SectionType.Body);
        int columnOffset = Math.Max(0, JsonArgs.GetInt(args, "column_offset", 0));
        int columnLimit = Math.Clamp(JsonArgs.GetInt(args, "column_limit", 50), 1, 50);
        int columnCount = Math.Min(columnLimit, Math.Max(0, body.NumberOfColumns - columnOffset));
        int rowCount = Math.Min(limit, Math.Max(0, body.NumberOfRows - offset));
        var cells = Enumerable.Range(0, rowCount).Select(row => new
        {
            row_index = body.FirstRowNumber + offset + row,
            cells = Enumerable.Range(0, columnCount).Select(col => schedule.GetCellText(SectionType.Body,
                body.FirstRowNumber + offset + row, body.FirstColumnNumber + columnOffset + col)).ToArray(),
        }).ToArray();
        var fields = schedule.Definition.GetFieldOrder().Select(fieldId =>
        {
            var field = schedule.Definition.GetField(fieldId);
            return new { field_id = fieldId.IntegerValue, parameter_id = field.ParameterId.Value,
                name = field.GetName(), heading = field.ColumnHeading, hidden = field.IsHidden, field_type = field.FieldType.ToString(),
                spec_type_id = field.GetSpecTypeId().TypeId, grid_width_feet = field.GridColumnWidth, sheet_width_feet = field.SheetColumnWidth,
                can_filter_value = schedule.Definition.CanFilterByValue(fieldId), can_filter_substring = schedule.Definition.CanFilterBySubstring(fieldId) };
        }).ToArray();
        return new
        {
            schedule_id = id, name = schedule.Name, is_itemized = schedule.Definition.IsItemized, fields,
            sort_fields = schedule.Definition.GetSortGroupFields().Select(sort => new { field_id = sort.FieldId.IntegerValue, order = sort.SortOrder.ToString(), show_header = sort.ShowHeader }).ToArray(),
            filters = schedule.Definition.GetFilters().Select(filter => new { field_id = filter.FieldId.IntegerValue, comparison = filter.FilterType.ToString(),
                value = filter.IsStringValue ? (object)filter.GetStringValue() : filter.IsDoubleValue ? filter.GetDoubleValue() : filter.IsIntegerValue ? filter.GetIntegerValue() : filter.IsElementIdValue ? filter.GetElementIdValue().Value : null,
                value_unit = filter.IsDoubleValue ? "internal" : null }).ToArray(),
            total_rows = body.NumberOfRows, total_columns = body.NumberOfColumns,
            offset, returned_rows = rowCount,
            next_offset = offset + rowCount < body.NumberOfRows ? (int?)(offset + rowCount) : null,
            column_offset = columnOffset, returned_columns = columnCount,
            first_column_index = body.FirstColumnNumber + columnOffset,
            next_column_offset = columnOffset + columnCount < body.NumberOfColumns ? (int?)(columnOffset + columnCount) : null,
            rows = cells,
        };
    }
}
