using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitBridge.Tools;

internal sealed class SummarizeElements : ITool
{
    private const int MaxElements = 10000;
    public string Name => "summarize_elements";
    public string Label => "Summarize Elements";
    public string Tier => "advanced";
    public string Description => "Count matching host-model elements grouped by category, type, level or one parameter value. Accepts get_elements category/class/level/type/filter scopes. At most 10,000 matches; narrow larger queries. Parameter grouping uses exact raw values, with a distinct missing-value group. Ambiguous display-name parameters are rejected. Counts cover the entire matching scope, not just the returned groups page.";
    public object ParametersSchema
    {
        get
        {
            var query = ElementQueryScope.Schema();
            return new
            {
                type = "object",
                properties = new
                {
                    query,
                    group_by = new { type = "string", @enum = new[] { "category", "typeName", "levelId", "parameter" } },
                    parameter = new { type = "string", description = "Required for group_by=parameter. Name, built-in identity or guid:<GUID>." },
                    type_parameter = new { type = "boolean", description = "Group by the parameter on the element type instead of the instance." },
                    offset = new { type = "integer", minimum = 0 },
                    limit = new { type = "integer", minimum = 1, maximum = 500 },
                },
                required = new[] { "group_by" },
            };
        }
    }

    public object Execute(JsonElement args, ToolContext context)
    {
        string groupBy = JsonArgs.GetString(args, "group_by") ?? "";
        if (groupBy is not ("category" or "typeName" or "levelId" or "parameter")) throw new ArgumentException("Unknown group_by value.");
        string? parameter = JsonArgs.GetString(args, "parameter");
        if (groupBy == "parameter" && string.IsNullOrWhiteSpace(parameter)) throw new ArgumentException("parameter is required for parameter grouping.");
        bool typeParameter = JsonArgs.GetBool(args, "type_parameter", false);
        var query = ElementQueryScope.Parse(args);
        JsonElement RunQuery() => JsonSerializer.SerializeToElement(((ToolOutput)new GetElements().Execute(JsonSerializer.SerializeToElement(query), context)!).Payload);
        query["count_only"] = true;
        var countResult = RunQuery();
        int total = countResult.GetProperty("total_count").GetInt32();
        object? warnings = countResult.TryGetProperty("warnings", out var queryWarnings) ? queryWarnings.Clone() : null;
        if (total > MaxElements) throw new ArgumentException($"Query matches {total} elements; narrow the scope to at most {MaxElements}.");
        query["count_only"] = false;
        query["limit"] = 1000;
        if (groupBy == "parameter")
        {
            query["parameter_names"] = new JsonArray(parameter);
            query["include_type_parameters"] = typeParameter;
        }
        var counts = new Dictionary<string, (object? Value, bool Missing, int Count)>();
        for (int position = 0; position < total; position += 1000)
        {
            query["offset"] = position;
            foreach (var row in RunQuery().GetProperty("elements").EnumerateArray())
            {
                object? value = null;
                bool missing = false;
                if (groupBy == "parameter")
                {
                    var projection = row.GetProperty("parameters").EnumerateArray().FirstOrDefault(p => p.GetProperty("isType").GetBoolean() == typeParameter);
                    missing = projection.ValueKind == JsonValueKind.Undefined || !projection.GetProperty("found").GetBoolean();
                    if (!missing)
                    {
                        if (projection.GetProperty("ambiguous").GetBoolean()) throw new ArgumentException($"Parameter '{parameter}' is ambiguous on element {row.GetProperty("id")}; use a built-in identity or shared GUID.");
                        value = projection.GetProperty("matches")[0].GetProperty("value").Clone();
                    }
                }
                else if (row.TryGetProperty(groupBy, out var field)) value = field.Clone();
                string key = (missing ? "missing:" : "value:") + JsonSerializer.Serialize(value);
                counts[key] = counts.TryGetValue(key, out var previous) ? (value, missing, previous.Count + 1) : (value, missing, 1);
            }
        }
        int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0));
        int limit = Math.Clamp(JsonArgs.GetInt(args, "limit", 100), 1, 500);
        var groups = counts.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.Ordinal).Skip(offset).Take(limit)
            .Select(pair => new { value = pair.Value.Value, missing = pair.Value.Missing, count = pair.Value.Count }).ToArray();
        return new { total_elements = total, total_groups = counts.Count, group_by = groupBy, parameter, type_parameter = typeParameter,
            numeric_values = "Revit internal units", warnings, offset, returned_count = groups.Length,
            next_offset = offset + groups.Length < counts.Count ? (int?)(offset + groups.Length) : null, groups };
    }
}
