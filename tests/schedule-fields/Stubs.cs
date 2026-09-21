using System.Text.Json;

// The fixture supplies Revit's observed field identities. Production tools handle
// discovery, validation, addition and response construction without replacement.
namespace Autodesk.Revit.DB
{
    internal sealed record ElementId(long Value);
    internal sealed record ScheduleFieldId(int IntegerValue);
    internal enum ScheduleFieldType { Instance, ElementType, Count }
    internal sealed class Document(ViewSchedule schedule)
    {
        public ViewSchedule Schedule { get; } = schedule;
        public object? GetElement(ElementId id) => id.Value == Schedule.Id.Value ? Schedule : null;
        public void Regenerate() { }
    }
    internal sealed class ViewSchedule(long id, IEnumerable<SchedulableField> fields)
    {
        public ElementId Id { get; } = new(id);
        public string UniqueId => "test-schedule";
        public string Name { get; set; } = "Schedule";
        public bool IsTemplate => false;
        public bool IsTitleblockRevisionSchedule => false;
        public ScheduleDefinition Definition { get; } = new(fields);
        public static ViewSchedule CreateSchedule(Document doc, ElementId category) => throw new NotSupportedException();
        public static ViewSchedule CreateSchedule(Document doc, ElementId category, ElementId area) => throw new NotSupportedException();
    }
    internal sealed class SchedulableField(ScheduleFieldType type, long parameterId, string name)
    {
        public ElementId ParameterId { get; } = new(parameterId);
        public ScheduleFieldType FieldType { get; } = type;
        public string GetName(Document doc) => name;
    }
    internal sealed class ScheduleDefinition(IEnumerable<SchedulableField> eligible)
    {
        private readonly List<SchedulableField> available = eligible.ToList();
        private readonly List<ScheduleField> included = new();
        public bool IsEmbedded => false;
        public bool IsItemized { get; set; } = true;
        public IList<SchedulableField> GetSchedulableFields() => available.ToArray();
        public IList<ScheduleFieldId> GetFieldOrder() => included.Select(f => f.FieldId).ToArray();
        public ScheduleField GetField(ScheduleFieldId id) => included.Single(f => f.FieldId == id);
        public ScheduleField AddField(SchedulableField field)
        {
            if (!available.Contains(field)) throw new ArgumentException("Field is not schedulable.");
            return Append(field.FieldType, field.ParameterId.Value);
        }
        public ScheduleField AddField(ScheduleFieldType type)
        {
            if (type != ScheduleFieldType.Count) throw new NotSupportedException();
            return Append(type, -1152353);
        }
        private ScheduleField Append(ScheduleFieldType type, long parameterId)
        {
            var field = new ScheduleField(new(included.Count), new(parameterId), type);
            included.Add(field);
            return field;
        }
        public int GetFieldCount() => included.Count;
        public int GetFilterCount() => 0;
        public int GetSortGroupFieldCount() => 0;
        public void SetSortGroupFields(List<ScheduleSortGroupField> fields) => throw new NotSupportedException();
        public void SetFilters(List<ScheduleFilter> filters) => throw new NotSupportedException();
        public bool CanFilterBySubstring(ScheduleFieldId id) => throw new NotSupportedException();
        public bool CanFilterByValue(ScheduleFieldId id) => throw new NotSupportedException();
    }
    internal sealed class ScheduleField(ScheduleFieldId id, ElementId parameterId, ScheduleFieldType type)
    {
        public ScheduleFieldId FieldId { get; } = id;
        public ElementId ParameterId { get; } = parameterId;
        public ScheduleFieldType FieldType { get; } = type;
        public string ColumnHeading { get; set; } = "";
        public bool IsHidden { get; set; }
        public double GridColumnWidth { get; set; }
        public double SheetColumnWidth { get; set; }
        public ForgeTypeId GetSpecTypeId() => throw new NotSupportedException();
    }
    internal sealed class ForgeTypeId { }
    internal enum ScheduleSortOrder { Ascending, Descending }
    internal enum ScheduleFilterType { Equal, NotEqual, Contains, GreaterThan, LessThan }
    internal sealed class ScheduleSortGroupField
    {
        public ScheduleSortGroupField(ScheduleFieldId id, ScheduleSortOrder order) => throw new NotSupportedException();
        public bool ShowHeader { get; set; }
    }
    internal sealed class ScheduleFilter
    {
        public ScheduleFilter(ScheduleFieldId id, ScheduleFilterType type, object value) => throw new NotSupportedException();
    }
    internal static class UnitUtils
    {
        public static bool IsMeasurableSpec(ForgeTypeId id) => throw new NotSupportedException();
        public static bool IsValidUnit(ForgeTypeId spec, ForgeTypeId unit) => throw new NotSupportedException();
        public static double ConvertToInternalUnits(double value, ForgeTypeId unit) => throw new NotSupportedException();
    }
}
namespace RevitBridge.Tools
{
    internal interface ITool { }
    internal sealed record ToolContext(Autodesk.Revit.DB.Document? Document);
    internal sealed class NoActiveDocumentException : Exception { }
    internal static class ModelEditBatch
    {
        // Execute the production step directly. Atomic rollback and commit handling
        // belong to tests/model-edit-batch and require live Revit verification too.
        internal sealed record Step(Dictionary<string, object?> Input, Func<Dictionary<string, object?>> Execute);
        internal sealed record Result(object Payload);
        public static Result Run(Autodesk.Revit.DB.Document doc, string name, JsonElement args, IEnumerable<Step> steps)
            => new(steps.Select(step => step.Execute()).ToArray());
    }
    internal static class JsonArgs
    {
        public static string? GetString(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        public static bool GetBool(JsonElement args, string name, bool fallback) => args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
        public static int GetInt(JsonElement args, string name, int fallback) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed) ? parsed : fallback;
        public static long? GetLong(JsonElement args, string name)
        {
            if (!args.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
            return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long text) ? text : null;
        }
    }
    internal static class ModelEditInputs
    {
        public static object PreviewSchema => new { type = "boolean" };
        public static object LengthUnitSchema => new { type = "string" };
        public static double Number(JsonElement input, string name) => throw new NotSupportedException();
        public static double LengthScale(JsonElement input) => throw new NotSupportedException();
    }
    internal static class CategoryResolver
    {
        public static Autodesk.Revit.DB.ElementId Resolve(Autodesk.Revit.DB.Document doc, string category) => throw new NotSupportedException();
    }
    internal static class UnitResolver
    {
        public static Autodesk.Revit.DB.ForgeTypeId Resolve(string unit) => throw new NotSupportedException();
    }
}
