// Controlled dependencies for offline regression tests. This is not a Revit API
// emulator: only the fixtures' collector, parameter, and identity behavior is used.
using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Autodesk.Revit.DB
{
    public enum BuiltInParameter { INVALID = -1, ALL_MODEL_INSTANCE_COMMENTS = -100, ELEM_TYPE_PARAM = -101 }
    public enum StorageType { None, String, Double, Integer, ElementId }
    public sealed record ElementId(long Value)
    {
        public ElementId(BuiltInParameter value) : this((long)value) { }
        public static ElementId InvalidElementId { get; } = new(-1);
    }
    public sealed record ForgeTypeId(string TypeId);
    public class Definition
    {
        public string Name { get; init; } = "";
        public ForgeTypeId Spec { get; init; } = new("unitless");
        public ForgeTypeId GetDataType() => Spec;
    }
    public sealed class InternalDefinition : Definition
    {
        public BuiltInParameter BuiltInParameter { get; init; }
    }
    public sealed class Parameter
    {
        public required ElementId Id { get; init; }
        public required Definition Definition { get; init; }
        public StorageType StorageType { get; init; } = StorageType.String;
        public object? Value { get; init; }
        public string? DisplayValue { get; init; }
        public bool HasValue => Value != null;
        public bool IsReadOnly { get; init; }
        public bool IsShared { get; init; }
        public Guid GUID { get; init; }
        public string? AsString() => Value as string;
        public double AsDouble() => Convert.ToDouble(Value, CultureInfo.InvariantCulture);
        public int AsInteger() => Convert.ToInt32(Value, CultureInfo.InvariantCulture);
        public ElementId AsElementId() => (ElementId)Value!;
        public string? AsValueString() => DisplayValue ?? Value?.ToString();
    }
    public sealed class Category { public string Name { get; init; } = "Fixture"; public ElementId Id { get; init; } = new(10); }
    public class Element
    {
        public required ElementId Id { get; init; }
        public string Name { get; init; } = "fixture";
        public Category? Category { get; init; } = new();
        public ElementId TypeId { get; init; } = ElementId.InvalidElementId;
        public ElementId LevelId { get; init; } = ElementId.InvalidElementId;
        public List<Parameter> Parameters { get; } = new();
        public object? Location => null;
        public ElementId GetTypeId() => TypeId;
        public Parameter? LookupParameter(string name) => Parameters.FirstOrDefault(p => p.Definition.Name == name);
        public Parameter? get_Parameter(BuiltInParameter id) => Parameters.FirstOrDefault(p => p.Id.Value == (long)id);
        public Parameter? get_Parameter(Guid guid) => Parameters.FirstOrDefault(p => p.IsShared && p.GUID == guid);
        public BoundingBoxXYZ? get_BoundingBox(object? view) => null;
        public ICollection<ElementId> GetMaterialIds(bool paint) => Array.Empty<ElementId>();
        public double GetMaterialArea(ElementId id, bool paint) => throw new NotSupportedException();
        public double GetMaterialVolume(ElementId id) => throw new NotSupportedException();
    }
    public sealed class FamilyInstance : Element { }
    public class ElementType : Element { }
    public sealed class Level : Element { public double Elevation { get; init; } }
    public sealed class View : Element { }
    public sealed class XYZ { public double X { get; init; } public double Y { get; init; } public double Z { get; init; } }
    public sealed class BoundingBoxXYZ { public XYZ Min { get; } = new(); public XYZ Max { get; } = new(); }
    public sealed class LocationPoint { public XYZ Point { get; } = new(); public double Rotation => 0; }
    public sealed class LocationCurve { public Curve Curve { get; } = new(); }
    public class Curve { public bool IsBound => false; public double Length => 0; public XYZ GetEndPoint(int index) => new(); }
    public sealed class Line : Curve { }
    public sealed class Document
    {
        public List<Element> Elements { get; } = new();
        public View? ActiveView { get; init; }
        public Element? GetElement(ElementId id) => Elements.FirstOrDefault(e => e.Id == id);
        public Units GetUnits() => new();
    }
    public sealed class Units { public FormatOptions GetFormatOptions(ForgeTypeId spec) => new(); }
    public sealed class FormatOptions { public ForgeTypeId GetUnitTypeId() => new("feet"); }
    public static class UnitUtils
    {
        public static bool IsMeasurableSpec(ForgeTypeId spec) => spec.TypeId == "length";
        public static bool IsValidUnit(ForgeTypeId spec, ForgeTypeId unit) => unit.TypeId is "feet" or "millimeters";
        public static double ConvertToInternalUnits(double value, ForgeTypeId unit) => unit.TypeId == "millimeters" ? value / 304.8 : value;
    }
    public abstract class ElementFilter { public abstract bool Matches(Element element); }
    public sealed class ElementLevelFilter(ElementId id) : ElementFilter
    {
        public override bool Matches(Element element) => element.LevelId == id;
    }
    public sealed class FilterRule(ElementId id, Func<Parameter, bool> predicate)
    {
        public bool Matches(Element element)
        {
            var parameter = element.Parameters.FirstOrDefault(p => p.Id == id);
            return parameter != null && predicate(parameter);
        }
    }
    public sealed class ElementParameterFilter : ElementFilter
    {
        private readonly IReadOnlyList<FilterRule> rules;
        public ElementParameterFilter(FilterRule rule) : this(new[] { rule }) { }
        public ElementParameterFilter(IReadOnlyList<FilterRule> rules) { this.rules = rules; }
        public override bool Matches(Element element) => rules.All(r => r.Matches(element));
    }
    public sealed class LogicalOrFilter(IReadOnlyList<ElementFilter> filters) : ElementFilter
    {
        public override bool Matches(Element element) => filters.Any(f => f.Matches(element));
    }
    public static class ParameterFilterRuleFactory
    {
        // The fake quick path resolves exclusively by ID. Its purpose is to expose
        // a production optimization pinning a display-name rule to the wrong ID.
        private static FilterRule Rule(ElementId id, object target, Func<int, bool> compare, double epsilon = 0)
            => new(id, p =>
            {
                int result;
                if (target is string text)
                    result = string.Compare(p.AsString(), text, StringComparison.OrdinalIgnoreCase);
                else if (target is ElementId elementId)
                    result = p.AsElementId().Value.CompareTo(elementId.Value);
                else
                {
                    double delta = Convert.ToDouble(p.Value, CultureInfo.InvariantCulture) - Convert.ToDouble(target, CultureInfo.InvariantCulture);
                    result = Math.Abs(delta) <= epsilon ? 0 : Math.Sign(delta);
                }
                return compare(result);
            });
        public static FilterRule CreateEqualsRule(ElementId id, object value, double epsilon = 0) => Rule(id, value, c => c == 0, epsilon);
        public static FilterRule CreateNotEqualsRule(ElementId id, object value, double epsilon = 0) => Rule(id, value, c => c != 0, epsilon);
        public static FilterRule CreateGreaterRule(ElementId id, object value, double epsilon = 0) => Rule(id, value, c => c > 0, epsilon);
        public static FilterRule CreateGreaterOrEqualRule(ElementId id, object value, double epsilon = 0) => Rule(id, value, c => c >= 0, epsilon);
        public static FilterRule CreateLessRule(ElementId id, object value, double epsilon = 0) => Rule(id, value, c => c < 0, epsilon);
        public static FilterRule CreateLessOrEqualRule(ElementId id, object value, double epsilon = 0) => Rule(id, value, c => c <= 0, epsilon);
        public static FilterRule CreateContainsRule(ElementId id, string value) => new(id, p => (p.AsString() ?? "").Contains(value, StringComparison.OrdinalIgnoreCase));
    }
    public sealed class FilteredElementCollector : IEnumerable<Element>
    {
        private IEnumerable<Element> elements;
        public static int ParameterFilterApplications { get; set; }
        public FilteredElementCollector(Document document) { elements = document.Elements; }
        public FilteredElementCollector(Document document, ElementId view) : this(document) { }
        public FilteredElementCollector OfCategoryId(ElementId id) { elements = elements.Where(e => e.Category?.Id == id); return this; }
        public FilteredElementCollector OfClass(Type type) { elements = elements.Where(type.IsInstanceOfType); return this; }
        public FilteredElementCollector WhereElementIsNotElementType() { elements = elements.Where(e => e is not ElementType); return this; }
        public FilteredElementCollector WherePasses(ElementFilter filter)
        {
            if (filter is ElementParameterFilter) ParameterFilterApplications++;
            elements = elements.Where(filter.Matches);
            return this;
        }
        public int GetElementCount() => elements.Count();
        public IEnumerator<Element> GetEnumerator() => elements.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace RevitBridge
{
    using Autodesk.Revit.DB;
    internal interface ITool { }
    internal sealed record ToolContext(Document? Document);
    internal sealed record ToolOutput(object? Payload, string? CompactText = null);
    internal sealed class NoActiveDocumentException : Exception { }
}

namespace RevitBridge.Tools
{
    using Autodesk.Revit.DB;
    internal static class JsonArgs
    {
        private static JsonElement Get(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) ? value : default;
        public static string? GetString(JsonElement args, string name) => Get(args, name) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        public static bool GetBool(JsonElement args, string name, bool fallback) => Get(args, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
        public static int GetInt(JsonElement args, string name, int fallback) => Get(args, name) is var value && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : fallback;
        public static long? GetLong(JsonElement args, string name) => Get(args, name) is var value && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
        public static List<long> GetLongArray(JsonElement args, string name) => Get(args, name).EnumerateArray().Select(v => v.GetInt64()).ToList();
        public static List<string>? GetStringArray(JsonElement args, string name) => Get(args, name) is var value && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetString()!).ToList() : null;
    }
    internal static class CategoryResolver { public static ElementId Resolve(Document doc, string name) => new(10); }
    internal static class ElementClassResolver { public static Type Resolve(string name) => name == "FamilyInstance" ? typeof(FamilyInstance) : throw new ArgumentException(name); }
    internal static class UnitResolver
    {
        public static ForgeTypeId Resolve(string name) => new(name);
        public static string ShortName(ForgeTypeId id) => id.TypeId;
    }
    internal static class ElementIdentity
    {
        public static IReadOnlyList<string> Fields { get; } = new[] { "id", "name", "category", "typeName", "levelId" };
        public static Dictionary<string, object?> Build(Document doc, Element element, IReadOnlyList<string> fields)
        {
            var all = new Dictionary<string, object?> { ["id"] = element.Id.Value, ["name"] = element.Name, ["category"] = element.Category?.Name, ["typeName"] = doc.GetElement(element.GetTypeId())?.Name, ["levelId"] = element.LevelId.Value };
            return all.Where(kv => fields.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }
}
