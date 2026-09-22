using System.Text.Json;
using Autodesk.Revit.DB;
using RevitBridge;
using RevitBridge.Tools;

int passed = 0, failed = 0;
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Require(bool value, string message) { if (!value) throw new Exception(message); }
void Reject(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception(message);
}
JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
Document New(string title) => new(new NativeDocument(title));
var original = New("AuditFixture");

if (args.Contains("--baseline"))
{
    Check("different files with the same title must not satisfy an intended-document guard", () =>
        Reject(() => BaselineDocumentGuard.CheckExpectedDocument(J(new { expected_document = "AuditFixture" }), New("AuditFixture")), "original title guard accepted an independent same-title document"));
    Check("original and detached titles must not be treated as the same document", () =>
        Reject(() => BaselineDocumentGuard.CheckExpectedDocument(J(new { expected_document = "AuditFixture.rvt" }), New("AuditFixture_detached")), "original title guard accepted detached document"));
    Check("an exact identity mismatch must not be ignored", () =>
        Reject(() => BaselineDocumentGuard.CheckExpectedDocument(J(new { expected_document_id = "different-open-document" }), original), "original guard ignored supplied exact identity"));
    Console.WriteLine($"{passed} passed, {failed} failed; original guard source, simulated documents, no Revit/API/network calls.");
    return failed == 0 ? 0 : 1;
}

Check("multiple wrappers of one native document receive one stable identity", () =>
{
    var native = new NativeDocument("WrapperFixture");
    var first = new Document(native);
    var second = new Document(native);
    Require(!ReferenceEquals(first, second) && first.Equals(second), "fixture must represent distinct managed wrappers");
    string id = DocumentGuard.GetIdentity(first);
    Require(DocumentGuard.GetIdentity(second) == id, "same native document received different identities");
    DocumentGuard.CheckForTool(J(new { expected_document_id = id }), second, "set_parameters");
});
Check("independent native documents with identical titles reject each other's identity", () =>
{
    var other = New("AuditFixture");
    string id = DocumentGuard.GetIdentity(original);
    Require(DocumentGuard.GetIdentity(other) != id, "same-title documents received the same identity");
    Reject(() => DocumentGuard.CheckForTool(J(new { expected_document_id = id }), other, "execute_csharp"), "wrong same-title document accepted");
});
Check("detached documents reject the original document's exact identity and legacy title", () =>
{
    var detached = New("AuditFixture_detached");
    Reject(() => DocumentGuard.CheckForTool(J(new { expected_document_id = DocumentGuard.GetIdentity(original) }), detached, "set_parameters"), "detached document accepted original identity");
    Reject(() => DocumentGuard.CheckExpectedDocument(J(new { expected_document = "AuditFixture.rvt" }), detached), "detached suffix was still normalized away");
});
Check("empty, malformed and differently cased supplied identities fail", () =>
{
    foreach (object? bad in new object?[] { null, "", " ", 123, false, new { value = "id" }, new[] { "id" }, "prior-session:unknown", " arbitrary " })
        Reject(() => DocumentGuard.CheckExpectedDocument(J(new { expected_document_id = bad }), original), $"invalid identity accepted: {bad}");
    string id = DocumentGuard.GetIdentity(original);
    Reject(() => DocumentGuard.CheckExpectedDocument(J(new { expected_document_id = id.ToUpperInvariant() }), original), "case-changed identity accepted");
});
Check("writes, exports and view activation require exact identity, including legacy-title requests", () =>
{
    foreach (string tool in new[] { "set_parameters", "execute_csharp", "export_documents", "open_view" })
    {
        Reject(() => DocumentGuard.CheckForTool(J(new { }), original, tool), $"{tool} accepted missing identity");
        Reject(() => DocumentGuard.CheckForTool(J(new { expected_document = "AuditFixture" }), original, tool), $"{tool} accepted title-only request");
        DocumentGuard.CheckForTool(J(new { expected_document_id = DocumentGuard.GetIdentity(original) }), original, tool);
    }
});
Check("selection and zoom changes require identity", () =>
{
    foreach (string action in new[] { "set", "add", "remove", "clear", "zoom", " SET " })
    {
        Reject(() => DocumentGuard.CheckForTool(J(new { action }), original, "manage_selection"), $"selection {action} accepted missing identity");
        DocumentGuard.CheckForTool(J(new { action, expected_document_id = DocumentGuard.GetIdentity(original) }), original, "manage_selection");
    }
});
Check("ordinary reads and selection-get allow omitted identity but honor a supplied mismatch", () =>
{
    foreach (string tool in new[] { "get_model_overview", "get_elements", "get_element_details", "get_element_types", "get_model_health", "capture_view", "manage_selection" })
    {
        DocumentGuard.CheckForTool(J(new { }), original, tool);
        Reject(() => DocumentGuard.CheckForTool(J(new { expected_document_id = "wrong" }), original, tool), $"{tool} ignored a supplied mismatch");
    }
    foreach (string action in new[] { "get", "GET", " get " })
        DocumentGuard.CheckForTool(J(new { action }), original, "manage_selection");
});
Check("selection-get with isolation is a mutation and must require identity", () =>
{
    Reject(() => DocumentGuard.CheckForTool(J(new { action = "get", isolate_in_view = true }), original, "manage_selection"), "explicit get+isolate bypassed exact identity");
    Reject(() => DocumentGuard.CheckForTool(J(new { isolate_in_view = true }), original, "manage_selection"), "default get+isolate bypassed exact identity");
    DocumentGuard.CheckForTool(J(new { action = "get", isolate_in_view = true, expected_document_id = DocumentGuard.GetIdentity(original) }), original, "manage_selection");
});
Check("closing invalidates an identity and reopening the same filename gets a new one", () =>
{
    var native = new NativeDocument("ReopenFixture");
    var old = new Document(native);
    string stale = DocumentGuard.GetIdentity(old);
    native.IsOpen = false;
    Reject(() => DocumentGuard.GetIdentity(old), "closed document produced an identity");
    var reopened = New("ReopenFixture");
    Require(DocumentGuard.GetIdentity(reopened) != stale, "reopened document inherited an open-session identity");
    Reject(() => DocumentGuard.CheckForTool(J(new { expected_document_id = stale }), reopened, "set_parameters"), "stale identity accepted after reopen");
});
Check("title remains an additional sanity check alongside a matching exact identity", () =>
{
    string id = DocumentGuard.GetIdentity(original);
    DocumentGuard.CheckExpectedDocument(J(new { expected_document_id = id, expected_document = " auditfixture.RVT " }), original);
    Reject(() => DocumentGuard.CheckExpectedDocument(J(new { expected_document_id = id, expected_document = "OtherProject" }), original), "wrong title accepted despite requested sanity check");
});
Check("new write tool names inherit exact identity enforcement from metadata", () =>
{
    Reject(() => DocumentGuard.CheckForTool(J(new { }), original, "new_write_tool", writes: true), "new write accepted missing identity");
    Reject(() => DocumentGuard.CheckForTool(J(new { expected_document_id = "wrong" }), original, "new_write_tool", writes: true), "new write accepted wrong identity");
    DocumentGuard.CheckForTool(J(new { expected_document_id = DocumentGuard.GetIdentity(original) }), original, "new_write_tool", writes: true);
});
var registry = ToolRegistry.CreateDefault();
registry.Add(new FutureWriteFixture());
foreach (string name in new[] { "transform_elements", "delete_elements", "change_element_types", "manage_views", "manage_sheets", "manage_sheet_placements", "manage_schedules", "create_tags", "future_write_fixture" })
Check($"registry requires exact identity and declares model effects for {name}", () =>
{
    var tool = registry.Get(name) ?? throw new Exception("expected write tool missing from registry");
    var descriptor = J(registry.Describe(tool));
    Require(descriptor.GetProperty("write").GetBoolean() && descriptor.GetProperty("category").GetString() == "write", "write classification was lost");
    Require(descriptor.GetProperty("effects").EnumerateArray().Select(e => e.GetString()).SequenceEqual(new[] { "model" }), "generic write must inherit model effects");
    var schema = descriptor.GetProperty("parameters");
    Require(schema.GetProperty("properties").GetProperty("expected_document_id").GetProperty("type").GetString() == "string", "exact identity schema missing");
    var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
    Require(required.Contains("fixture") && required.Count(x => x == "expected_document_id") == 1, "registry must preserve existing requirements and add identity exactly once");
    Require(!J(tool.ParametersSchema).GetProperty("properties").TryGetProperty("expected_document_id", out _), "registry schema projection must not mutate the tool's input schema");
    Reject(() => DocumentGuard.CheckForTool(J(new { }), original, name, tool.Write), "write metadata did not reject missing identity");
    Reject(() => DocumentGuard.CheckForTool(J(new { expected_document_id = "wrong" }), original, name, tool.Write), "write metadata accepted wrong identity");
    DocumentGuard.CheckForTool(J(new { expected_document_id = DocumentGuard.GetIdentity(original) }), original, name, tool.Write);
});
foreach (string name in new[] { "get_elements", "get_schedule_fields", "query_spatial_elements", "measure_geometry", "get_model_coordinates" })
Check($"registry keeps {name} identity optional and model effects empty", () =>
{
    var tool = registry.Get(name) ?? throw new Exception("expected read tool missing from registry");
    var descriptor = J(registry.Describe(tool));
    Require(descriptor.GetProperty("effects").GetArrayLength() == 0 && !descriptor.GetProperty("write").GetBoolean(), "ordinary reads cannot inherit model effects");
    Require(!descriptor.GetProperty("parameters").GetProperty("required").EnumerateArray().Any(e => e.GetString() == "expected_document_id"), "ordinary read must not require identity");
    DocumentGuard.CheckForTool(J(new { }), original, name, tool.Write);
    Reject(() => DocumentGuard.CheckForTool(J(new { expected_document_id = "wrong" }), original, name, tool.Write), "ordinary read ignored supplied document mismatch");
});
Console.WriteLine($"{passed} passed, {failed} failed; production DocumentGuard and ToolRegistry with simulated documents and tool metadata; no Revit/API/network calls.");
return failed == 0 ? 0 : 1;
