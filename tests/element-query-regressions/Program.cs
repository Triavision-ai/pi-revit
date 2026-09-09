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
Console.WriteLine($"RESULT {passed} passed; {failed} failed. Offline controlled API substitutes; live Revit verification required.");
return failed == 0 ? 0 : 1;
