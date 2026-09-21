using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class ManageElementSets : ITool
{
    private sealed record Member(long Id, string UniqueId);
    private sealed record Snapshot(string Id, string DocumentId, string Label, DateTimeOffset Expires, IReadOnlyList<Member> Members);
    private static readonly Dictionary<string, Snapshot> Sets = new(StringComparer.Ordinal);
    private const int MaxElements = 10000;
    private const int MaxSets = 32;

    public string Name => "manage_element_sets";
    public string Label => "Manage Element Sets";
    public string Tier => "advanced";
    public IReadOnlyList<string> Effects => new[] { "session" };
    public string Description => "Create, list, read or forget reusable element sets without changing the model or selection. A set captures matching host-element identities (up to 10,000), expires after 30 minutes, and is valid only for the exact open document and bridge session. Read pages report live values, deleted members and identity mismatches; membership is a snapshot and filters are not re-evaluated. At most 32 sets are retained. Use read IDs with ordinary tools after checking missing members.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            action = new { type = "string", @enum = new[] { "create", "list", "read", "forget" } },
            query = ElementQueryScope.Schema(),
            label = new { type = "string", maxLength = 160 },
            set_id = new { type = "string" },
            offset = new { type = "integer", minimum = 0 },
            limit = new { type = "integer", minimum = 1, maximum = 1000 },
            parameter_names = new { type = "array", maxItems = 20, items = new { type = "string" } },
            include_type_parameters = new { type = "boolean" },
        },
        required = new[] { "action" },
    };

    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        string documentId = DocumentGuard.GetIdentity(doc);
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in Sets.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) Sets.Remove(expired);
        string action = JsonArgs.GetString(args, "action") ?? "";
        if (action == "list") return new
        {
            sets = Sets.Values.Where(set => set.DocumentId == documentId).OrderBy(set => set.Expires)
                .Select(set => new { set_id = set.Id, label = set.Label, total_count = set.Members.Count, expires_at = set.Expires }).ToArray(),
        };
        if (action == "create")
        {
            if (Sets.Count >= MaxSets) throw new ArgumentException("Element-set capacity reached. Forget an existing set or wait for expiry.");
            string label = JsonArgs.GetString(args, "label") ?? "Element set";
            if (label.Length > 160) throw new ArgumentException("label must be at most 160 characters.");
            var query = ElementQueryScope.Parse(args);
            query["fields"] = new JsonArray("id"); query["count_only"] = true;
            JsonElement Query() => JsonSerializer.SerializeToElement(((ToolOutput)new GetElements().Execute(JsonSerializer.SerializeToElement(query), context)!).Payload);
            var count = Query();
            int total = count.GetProperty("total_count").GetInt32();
            if (total > MaxElements) throw new ArgumentException($"Query matches {total} elements; narrow the scope to at most {MaxElements}.");
            query["count_only"] = false; query["limit"] = 1000;
            var members = new List<Member>(total);
            for (int offset = 0; offset < total; offset += 1000)
            {
                query["offset"] = offset;
                foreach (var row in Query().GetProperty("elements").EnumerateArray())
                {
                    var element = doc.GetElement(new ElementId(row.GetProperty("id").GetInt64()));
                    members.Add(new Member(element.Id.Value, element.UniqueId));
                }
            }
            var snapshot = new Snapshot(Guid.NewGuid().ToString("N"), documentId, label, now.AddMinutes(30), members);
            Sets.Add(snapshot.Id, snapshot);
            return new { set_id = snapshot.Id, document_id = documentId, label, total_count = total, expires_at = snapshot.Expires,
                warnings = count.TryGetProperty("warnings", out var warnings) ? (object)warnings.Clone() : null };
        }
        if (action is not ("read" or "forget")) throw new ArgumentException("Unknown action; use create, list, read or forget.");
        string setId = JsonArgs.GetString(args, "set_id") ?? "";
        if (!Sets.TryGetValue(setId, out var saved)) throw new ArgumentException("Unknown or expired set_id. Create a fresh set; IDs do not survive a bridge restart.");
        if (saved.DocumentId != documentId) throw new ArgumentException("This element set belongs to a different open document. Activate its original document or create a fresh set.");
        if (action == "forget") { Sets.Remove(setId); return new { set_id = setId, forgotten = true }; }
        int start = Math.Max(0, JsonArgs.GetInt(args, "offset", 0));
        int limit = Math.Clamp(JsonArgs.GetInt(args, "limit", 100), 1, 1000);
        var names = JsonArgs.GetStringArray(args, "parameter_names");
        if (names?.Count > 20) throw new ArgumentException("parameter_names allows at most 20 identities.");
        bool includeType = JsonArgs.GetBool(args, "include_type_parameters", false);
        var page = saved.Members.Skip(start).Take(limit).ToArray();
        var rows = new List<object>();
        var missing = new List<object>();
        foreach (var member in page)
        {
            var element = doc.GetElement(new ElementId(member.Id));
            if (element == null || element.UniqueId != member.UniqueId)
            {
                missing.Add(new { id = member.Id, unique_id = member.UniqueId, reason = element == null ? "deleted" : "identity_changed" });
                continue;
            }
            var row = ElementIdentity.Build(doc, element, ElementIdentity.Fields);
            row["unique_id"] = member.UniqueId;
            if (names is { Count: > 0 }) row["parameters"] = GetElementDetails.ProjectParameters(doc, element, names, includeType);
            rows.Add(row);
        }
        return new { set_id = setId, document_id = documentId, snapshot_count = saved.Members.Count, expires_at = saved.Expires,
            offset = start, visited_count = page.Length, returned_count = rows.Count,
            next_offset = start + page.Length < saved.Members.Count ? (int?)(start + page.Length) : null, elements = rows, missing };
    }
}
