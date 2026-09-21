using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class GetScheduleFields : ITool
{
    public string Name => "get_schedule_fields";
    public string Label => "Get Schedule Fields";
    public string Tier => "advanced";
    public string Description => "List fields eligible to be added to one existing schedule, with paging and localized-name filtering. A field is identified by the pair parameter_id + field_type; negative built-in parameter IDs are valid. These identities differ from schedule-local field_id values returned by get_schedules. Existing fields are marked included. Calculated/combined fields and the special Count field are not part of GetSchedulableFields; manage_schedules can add Count explicitly.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            schedule_id = new { type = "integer", minimum = 1 }, name_filter = new { type = "string" },
            offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 200 },
        }, required = new[] { "schedule_id" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        var schedule = doc.GetElement(new ElementId(JsonArgs.GetLong(args, "schedule_id") ?? 0)) as ViewSchedule ?? throw new ArgumentException("schedule_id is not a schedule.");
        if (schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule) throw new ArgumentException("Use a regular schedule, not a template or titleblock revision schedule.");
        int offset = JsonArgs.GetInt(args, "offset", 0), limit = JsonArgs.GetInt(args, "limit", 100);
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Invalid paging range.");
        string needle = JsonArgs.GetString(args, "name_filter") ?? "";
        var definition = schedule.Definition;
        var included = definition.GetFieldOrder().Select(id => definition.GetField(id)).Select(f => (f.ParameterId.Value, f.FieldType)).ToHashSet();
        var fields = definition.GetSchedulableFields().Select(f => new
        {
            parameter_id = f.ParameterId.Value, field_type = f.FieldType.ToString(), name = f.GetName(doc), included = included.Contains((f.ParameterId.Value, f.FieldType)),
        }).Where(f => f.name.Contains(needle, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.parameter_id).ThenBy(f => f.field_type, StringComparer.Ordinal).ToArray();
        var page = fields.Skip(offset).Take(limit).ToArray();
        return new { schedule_id = schedule.Id.Value, total_count = fields.Length, offset, returned_count = page.Length, next_offset = offset + page.Length < fields.Length ? (int?)(offset + page.Length) : null, fields = page };
    }
}
