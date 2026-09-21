using System.Text.Json;
using Autodesk.Revit.DB;
using RevitBridge;
using RevitBridge.Tools;

// Links both complete production tool files. Only their external API/transport
// dependencies are substituted; assertions use fixed fixture expectations.
int passed = 0, failed = 0;
void Test(string name, Action run)
{
    try { run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); failed++; }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
JsonElement Query(Document doc, object args) => JsonSerializer.SerializeToElement(((ToolOutput)new GetElements().Execute(Args(args), new ToolContext(doc))!).Payload);
ToolOutput Details(Document doc, object args) => (ToolOutput)new GetElementDetails().Execute(Args(args), new ToolContext(doc))!;
long[] Ids(JsonElement result) => result.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ToArray();
Parameter Text(long id, string name, string? value, Guid? guid = null) => new() { Id = new(id), Definition = new() { Name = name }, Value = value, IsShared = guid != null, GUID = guid ?? Guid.Empty };
var sharedGuid = Guid.Parse("fbef7860-bbe2-4cda-a986-e7c9738587f9");
var doc = new Document();
for (int i = 1; i <= 52; i++)
{
    var element = new FamilyInstance { Id = new(i), Name = $"Fixture {i}", TypeId = new(1000) };
    if (i <= 51) element.Parameters.Add(Text(i <= 50 ? 2000 : 2001, "Audit Label", "Alpha"));
    element.Parameters.Add(Text((long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Comments", i == 52 ? "other" : "chosen"));
    element.Parameters.Add(Text(3000, "Shared Label", "shared", sharedGuid));
    element.Parameters.Add(new Parameter { Id = new(4000), Definition = new() { Name = "Audit Height", Spec = new("length") }, StorageType = StorageType.Double, Value = 1.0 });
    doc.Elements.Add(element);
}
var type = new ElementType { Id = new(1000), Name = "Fixture type" };
type.Parameters.Add(Text(5000, "Type Label", "Type answer"));
doc.Elements.Add(type);

object Rule(string param, string op, object value) => new { param, op, value };
Test("F004 category scoped display name includes the 51st different parameter ID", () =>
{
    var result = Query(doc, new { category = "Fixture", filter = new { rules = new[] { Rule("Audit Label", "equals", "alpha") } } });
    Check(result.GetProperty("total_count").GetInt32() == 51 && Ids(result).SequenceEqual(Enumerable.Range(1, 51).Select(i => (long)i)), $"expected IDs 1..51; got {result.GetProperty("total_count")}");
});
Test("F004 class scoped count preserves cross-family display name", () =>
{
    var result = Query(doc, new { of_class = "FamilyInstance", count_only = true, filter = new { rules = new[] { Rule("Audit Label", "contains", "LP") } } });
    Check(result.GetProperty("total_count").GetInt32() == 51, $"expected 51; got {result.GetProperty("total_count")}");
});
Test("F004 pagination includes the different-ID element past the probe boundary", () =>
{
    var result = Query(doc, new { category = "Fixture", offset = 50, limit = 1, fields = new[] { "id" }, filter = new { rules = new[] { Rule("Audit Label", "equals", "Alpha") } } });
    Check(Ids(result).SequenceEqual(new long[] { 51 }), "expected final page ID 51");
    Check(!result.GetProperty("has_more").GetBoolean(), "final page cannot have more rows");
});
Test("explicit built-in identity retains collector optimization", () =>
{
    FilteredElementCollector.ParameterFilterApplications = 0;
    var result = Query(doc, new { category = "Fixture", filter = new { rules = new[] { Rule("ALL_MODEL_INSTANCE_COMMENTS", "equals", "chosen") } } });
    Check(result.GetProperty("total_count").GetInt32() == 51, "expected 51 explicit-ID matches");
    Check(FilteredElementCollector.ParameterFilterApplications > 0, "expected collector parameter filter");
});
Test("explicit shared GUID retains collector optimization", () =>
{
    FilteredElementCollector.ParameterFilterApplications = 0;
    var result = Query(doc, new { filter = new { rules = new[] { Rule("guid:" + sharedGuid, "equals", "shared") } } });
    Check(result.GetProperty("total_count").GetInt32() == 52, "expected 52 shared-GUID matches");
    Check(FilteredElementCollector.ParameterFilterApplications > 0, "expected collector parameter filter");
});
Test("AND mixes explicit collector filtering with display-name evaluation", () =>
{
    var result = Query(doc, new { category = "Fixture", filter = new { match = "all", rules = new[] { Rule("Audit Label", "equals", "alpha"), Rule("ALL_MODEL_INSTANCE_COMMENTS", "equals", "chosen") } } });
    Check(result.GetProperty("total_count").GetInt32() == 51, "expected 51 AND matches");
});
Test("OR preserves the branch matching an element without the display-name parameter", () =>
{
    var result = Query(doc, new { category = "Fixture", filter = new { match = "any", rules = new[] { Rule("Audit Label", "equals", "alpha"), Rule("ALL_MODEL_INSTANCE_COMMENTS", "equals", "other") } } });
    Check(result.GetProperty("total_count").GetInt32() == 52, "expected all 52 OR matches");
});
Test("missing parameter remains empty", () =>
{
    var result = Query(doc, new { category = "Fixture", filter = new { rules = new[] { new { param = "Audit Label", op = "is_empty" } } } });
    Check(Ids(result).SequenceEqual(new long[] { 52 }), "expected only missing-parameter element 52");
});
Test("display-name numeric comparison uses explicit units", () =>
{
    var result = Query(doc, new { category = "Fixture", filter = new { rules = new[] { new { param = "Audit Height", op = "equals", value = 304.8, unit = "millimeters" } } } });
    Check(result.GetProperty("total_count").GetInt32() == 52, "expected 52 one-foot elements");
});
Test("display-name invalid numeric value still errors", () =>
{
    try { Query(doc, new { category = "Fixture", filter = new { rules = new[] { Rule("Audit Height", "greater", "not a number") } } }); }
    catch (ArgumentException) { return; }
    throw new Exception("expected numeric validation error");
});

foreach (bool instance in new[] { false, true })
foreach (bool typeParams in new[] { false, true })
{
    Test($"F006 independent include flags instance={instance} type={typeParams}", () =>
    {
        var output = Details(doc, new { element_ids = new[] { 1 }, include = new { parameters = instance, type_parameters = typeParams } });
        var row = JsonSerializer.SerializeToElement(output.Payload).GetProperty("elements")[0];
        if (!instance && !typeParams) { Check(!row.TryGetProperty("parameters", out _), "parameters should be omitted"); return; }
        Check(row.TryGetProperty("parameters", out var parameters), "requested parameter section missing");
        var rows = parameters.EnumerateArray().ToArray();
        Check(rows.Count(p => !p.GetProperty("isType").GetBoolean()) == (instance ? 4 : 0), "wrong instance parameter count");
        Check(rows.Count(p => p.GetProperty("isType").GetBoolean()) == (typeParams ? 1 : 0), "wrong type parameter count");
        if (typeParams) Check(rows.Single(p => p.GetProperty("isType").GetBoolean()).GetProperty("value").GetString() == "Type answer", "type value missing");
    });
}
Test("F006 type-only filtering and compact count match selected parameter scope", () =>
{
    var output = Details(doc, new { element_ids = new[] { 1 }, parameter_names = new[] { "Type Label" }, include = new { parameters = false, type_parameters = true } });
    var row = JsonSerializer.SerializeToElement(output.Payload).GetProperty("elements")[0];
    Check(row.TryGetProperty("parameters", out var parameters) && parameters.GetArrayLength() == 1, "expected selected type parameter");
    Check(output.CompactText!.Contains("1 of 1 params matched"), "summary must count type parameters independently");
});
Test("F006 type-only on an element without a type returns an empty section", () =>
{
    var output = Details(doc, new { element_ids = new[] { 1000 }, include = new { parameters = false, type_parameters = true } });
    var row = JsonSerializer.SerializeToElement(output.Payload).GetProperty("elements")[0];
    Check(row.TryGetProperty("parameters", out var parameters) && parameters.GetArrayLength() == 0, "expected empty requested section");
});
var projectionDoc = new Document();
var projectionElement = new FamilyInstance { Id = new(71), Name = "Projection fixture", TypeId = new(1700) };
projectionElement.Parameters.Add(new Parameter
{
    Id = new((long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS),
    Definition = new InternalDefinition { Name = "Kommentare", BuiltInParameter = BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS },
    Value = "Instance comment",
});
projectionElement.Parameters.Add(Text(7100, "Shared projection", "Shared answer", sharedGuid));
projectionElement.Parameters.Add(Text(7101, "Duplicate label", "First distinct parameter"));
projectionElement.Parameters.Add(Text(7102, "Duplicate label", "Second distinct parameter"));
projectionElement.Parameters.Add(new Parameter
{
    Id = new(7103), Definition = new() { Name = "Height projection", Spec = new("length") },
    StorageType = StorageType.Double, Value = 1.0, DisplayValue = "1' - 0\"",
});
projectionElement.Parameters.Add(Text(7104, "Unset label", null));
projectionElement.Parameters.Add(Text(7105, "Shared scope", "Instance scope value"));
var projectionType = new ElementType { Id = new(1700), Name = "Projection type" };
projectionType.Parameters.Add(Text(7200, "Type only", "Type-only answer"));
projectionType.Parameters.Add(Text(7201, "Shared scope", "Type scope value"));
projectionDoc.Elements.Add(projectionElement);
projectionDoc.Elements.Add(projectionType);

JsonElement[] Projections(object args) => Query(projectionDoc, args).GetProperty("elements")[0]
    .GetProperty("parameters").EnumerateArray().ToArray();
JsonElement Projection(string selector) => Projections(new { parameter_names = new[] { selector } }).Single();

Test("projection resolves a built-in identity despite localized display name", () =>
{
    var projected = Projection("all_model_instance_comments");
    Check(projected.GetProperty("found").GetBoolean() && !projected.GetProperty("ambiguous").GetBoolean(), "expected one built-in match");
    var match = projected.GetProperty("matches").EnumerateArray().Single();
    Check(match.GetProperty("name").GetString() == "Kommentare", "must preserve localized display name");
    Check(match.GetProperty("builtInParameter").GetString() == "ALL_MODEL_INSTANCE_COMMENTS", "must preserve stable built-in identity");
    Check(match.GetProperty("value").GetString() == "Instance comment", "wrong built-in value");
});
Test("projection resolves shared GUID and preserves its identity", () =>
{
    var projected = Projection("GUID:" + sharedGuid.ToString().ToUpperInvariant());
    var match = projected.GetProperty("matches").EnumerateArray().Single();
    Check(projected.GetProperty("found").GetBoolean() && !projected.GetProperty("ambiguous").GetBoolean(), "expected one shared match");
    Check(match.GetProperty("isShared").GetBoolean() && Guid.Parse(match.GetProperty("guid").GetString()!) == sharedGuid, "shared identity changed");
    Check(match.GetProperty("value").GetString() == "Shared answer", "wrong GUID value");
});
Test("projection display selectors are case-insensitive and deduplicated", () =>
{
    var projected = Projections(new { parameter_names = new[] { "sHaReD pRoJeCtIoN", "SHARED PROJECTION" } });
    Check(projected.Length == 1, "case variants should produce one requested projection");
    Check(projected[0].GetProperty("requested").GetString() == "sHaReD pRoJeCtIoN", "first selector spelling should be retained");
    Check(projected[0].GetProperty("matches")[0].GetProperty("value").GetString() == "Shared answer", "display selector did not resolve");
});
Test("missing projection is explicit and differs from an unset existing parameter", () =>
{
    var projected = Projections(new { parameter_names = new[] { "Absent label", "Unset label" } });
    var missing = projected.Single(p => p.GetProperty("requested").GetString() == "Absent label");
    Check(!missing.GetProperty("found").GetBoolean() && !missing.GetProperty("ambiguous").GetBoolean(), "missing flags incorrect");
    Check(missing.GetProperty("matches").GetArrayLength() == 0, "missing parameter cannot have values");
    var unset = projected.Single(p => p.GetProperty("requested").GetString() == "Unset label");
    Check(unset.GetProperty("found").GetBoolean(), "existing unset parameter must remain found");
    var value = unset.GetProperty("matches").EnumerateArray().Single();
    Check(value.GetProperty("value").ValueKind == JsonValueKind.Null && value.GetProperty("displayValue").ValueKind == JsonValueKind.Null, "unset parameter must retain null values");
});
Test("duplicate display names report ambiguity and retain both distinct values", () =>
{
    var projected = Projection("Duplicate label");
    Check(projected.GetProperty("found").GetBoolean() && projected.GetProperty("ambiguous").GetBoolean(), "duplicate names must be explicitly ambiguous");
    var values = projected.GetProperty("matches").EnumerateArray().Select(p => p.GetProperty("value").GetString()).Order().ToArray();
    Check(values.SequenceEqual(new[] { "First distinct parameter", "Second distinct parameter" }), "duplicate values were dropped or combined");
});
Test("type projection is opt-in and distinguishes instance/type values", () =>
{
    var withoutType = Projections(new { parameter_names = new[] { "Type only", "Shared scope" } });
    Check(withoutType.Length == 2 && withoutType.All(p => !p.GetProperty("isType").GetBoolean()), "type values must be absent by default");
    Check(!withoutType[0].GetProperty("found").GetBoolean(), "type-only selector cannot match instance by default");
    var withType = Projections(new { parameter_names = new[] { "Type only", "Shared scope" }, include_type_parameters = true });
    Check(withType.Length == 4, "expected separate instance and type projections");
    var typeOnly = withType.Single(p => p.GetProperty("isType").GetBoolean() && p.GetProperty("requested").GetString() == "Type only");
    Check(typeOnly.GetProperty("matches")[0].GetProperty("value").GetString() == "Type-only answer", "type-only value missing");
    foreach (bool isType in new[] { false, true })
    {
        var projected = withType.Single(p => p.GetProperty("isType").GetBoolean() == isType && p.GetProperty("requested").GetString() == "Shared scope");
        var match = projected.GetProperty("matches").EnumerateArray().Single();
        Check(match.GetProperty("isType").GetBoolean() == isType, "nested value scope differs from projection scope");
        Check(match.GetProperty("value").GetString() == (isType ? "Type scope value" : "Instance scope value"), "instance and type values were conflated");
    }
});
Test("numeric projection preserves internal value and distinct formatted display", () =>
{
    var match = Projection("Height projection").GetProperty("matches").EnumerateArray().Single();
    Check(match.GetProperty("storageType").GetString() == "Double", "numeric storage type missing");
    Check(match.GetProperty("value").GetDouble() == 1.0, "internal value must remain one foot");
    Check(match.GetProperty("displayValue").GetString() == "1' - 0\"" && match.GetProperty("unit").GetString() == "feet", "display value or display unit lost");
    var detail = JsonSerializer.SerializeToElement(Details(projectionDoc, new { element_ids = new[] { 71 }, parameter_names = new[] { "Height projection" } }).Payload)
        .GetProperty("elements")[0].GetProperty("parameters").EnumerateArray().Single();
    Check(match.GetRawText() == detail.GetRawText(), "projection and detailed inspection must share numeric semantics");
});
Test("projection paging includes only the requested page and leaves totals unchanged", () =>
{
    var result = Query(doc, new { offset = 50, limit = 1, fields = new[] { "id" }, parameter_names = new[] { "Audit Label" } });
    Check(result.GetProperty("total_count").GetInt32() == 52 && result.GetProperty("returned_count").GetInt32() == 1, "projection altered query counts");
    Check(Ids(result).SequenceEqual(new long[] { 51 }) && result.GetProperty("next_offset").GetInt32() == 51 && result.GetProperty("has_more").GetBoolean(), "projection altered paging");
    var row = result.GetProperty("elements").EnumerateArray().Single();
    Check(!row.TryGetProperty("name", out _), "projection must respect identity field selection");
    Check(row.GetProperty("parameters")[0].GetProperty("matches")[0].GetProperty("value").GetString() == "Alpha", "wrong page projection");
    var final = Query(doc, new { offset = 51, limit = 1, parameter_names = new[] { "Audit Label" } });
    Check(!final.GetProperty("has_more").GetBoolean() && !final.GetProperty("elements")[0].GetProperty("parameters")[0].GetProperty("found").GetBoolean(), "final page must retain explicit missing parameter");
});
Test("projection evaluates only paged rows", () =>
{
    var pagedDoc = new Document();
    var skipped = new FamilyInstance { Id = new(81) };
    skipped.Parameters.Add(new Parameter { Id = new(8100), Definition = new() { Name = "Number" }, StorageType = StorageType.Double, Value = "invalid double outside returned page" });
    var returned = new FamilyInstance { Id = new(82) };
    returned.Parameters.Add(new Parameter { Id = new(8200), Definition = new() { Name = "Number" }, StorageType = StorageType.Double, Value = 2.5 });
    pagedDoc.Elements.Add(skipped);
    pagedDoc.Elements.Add(returned);
    var result = Query(pagedDoc, new { offset = 1, limit = 1, parameter_names = new[] { "Number" } });
    Check(Ids(result).SequenceEqual(new long[] { 82 }), "wrong selected page");
    Check(result.GetProperty("elements")[0].GetProperty("parameters")[0].GetProperty("matches")[0].GetProperty("value").GetDouble() == 2.5, "paged numeric value missing");
});
foreach (bool postFilter in new[] { false, true })
Test($"count-only skips projection with post-filter={postFilter}", () =>
{
    // The malformed GUID throws if projection is evaluated, exposing accidental work in either count path.
    object args = postFilter
        ? new { count_only = true, parameter_names = new[] { "guid:invalid" }, filter = new { rules = new[] { Rule("Audit Label", "equals", "Alpha") } } }
        : new { count_only = true, parameter_names = new[] { "guid:invalid" } };
    var result = Query(doc, args);
    Check(result.GetProperty("total_count").GetInt32() == (postFilter ? 51 : 52), "wrong count-only total");
    Check(!result.TryGetProperty("elements", out _), "count-only must omit projected rows");
});
Test("projection rejects malformed shared GUID during a normal query", () =>
{
    try { Projection("guid:invalid"); }
    catch (ArgumentException) { return; }
    throw new Exception("expected GUID validation error");
});
Test("projection permits exactly 20 identities and rejects 21", () =>
{
    var names = Enumerable.Range(1, 20).Select(i => $"Absent {i}").ToArray();
    Check(Projections(new { parameter_names = names }).Length == 20, "twenty identities must be supported");
    try { Projections(new { parameter_names = names.Append("Absent 21").ToArray() }); }
    catch (ArgumentException ex)
    {
        Check(ex.Message.Contains("20", StringComparison.Ordinal), "limit error must explain the cap");
        return;
    }
    throw new Exception("expected 21-identity validation error");
});
Test("queries without requested projections retain their existing response shape", () =>
{
    foreach (var queryArgs in new object[] { new { limit = 1 }, new { limit = 1, parameter_names = Array.Empty<string>(), include_type_parameters = true } })
        Check(!Query(projectionDoc, queryArgs).GetProperty("elements")[0].TryGetProperty("parameters", out _), "unrequested parameter section must be omitted");
});
Console.WriteLine($"RESULT {passed} passed; {failed} failed. Offline controlled API substitutes; live Revit verification required.");
return failed == 0 ? 0 : 1;
