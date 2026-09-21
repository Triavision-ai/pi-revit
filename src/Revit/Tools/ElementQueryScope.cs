using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitBridge.Tools;

internal static class ElementQueryScope
{
    public static JsonObject Schema()
    {
        var schema = JsonSerializer.SerializeToNode(new GetElements().ParametersSchema)!.AsObject();
        foreach (string key in new[] { "count_only", "fields", "offset", "limit", "parameter_names", "include_type_parameters" })
            schema["properties"]!.AsObject().Remove(key);
        schema["description"] = "Filter the whole matching scope. Query paging and projections are not supported here.";
        schema["additionalProperties"] = false;
        return schema;
    }

    public static JsonObject Parse(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var input) ? JsonNode.Parse(input.GetRawText()) as JsonObject : new JsonObject();
        if (query == null) throw new ArgumentException("query must be an object.");
        var allowed = Schema()["properties"]!.AsObject();
        foreach (var property in query)
            if (!allowed.ContainsKey(property.Key)) throw new ArgumentException($"query does not support '{property.Key}'. It always uses the whole matching scope.");
        return query;
    }
}
