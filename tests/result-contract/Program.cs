using System.Reflection;
using System.Text.Json;
using RevitBridge;

var method = typeof(BridgeServer).GetMethod("BuildToolResponse", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Production response builder not found.");
int failures = 0;
void Check(string name, Action action)
{
    try { action(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
JsonElement Response(object? value) => JsonSerializer.SerializeToElement(method.Invoke(null, new[] { "fixture_tool", value }));
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

Check("requested payload replaces the compact summary in model content", () =>
{
    var payload = new { elements = Enumerable.Range(101, 7).Select(id => new { id, value = $"fixture-{id}" }).ToArray() };
    var result = Response(new ToolOutput(payload, "7 elements. Sample: 101, 102, 103."));
    string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
    Require(text == JsonSerializer.Serialize(payload), "model content omitted complete element rows/values");
    Require(result.GetProperty("details").GetProperty("payload").GetRawText() == text, "structured payload changed");
});
Check("large content stays bounded while full payload is retained for the extension", () =>
{
    var payload = new { value = new string('x', 15000), tail = "last-value" };
    var result = Response(new ToolOutput(payload, "Tiny summary."));
    string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
    Require(text.Length <= 12000, "inline content limit exceeded");
    Require(result.GetProperty("details").GetProperty("contentTruncated").GetBoolean(), "oversized result not marked");
    Require(result.GetProperty("details").GetProperty("payload").GetRawText() == JsonSerializer.Serialize(payload), "full payload lost");
});
Check("null values preserve their meaning", () =>
{
    var result = Response(new ToolOutput(null, "Summary."));
    Require(result.GetProperty("content")[0].GetProperty("text").GetString() == "null", "null payload changed");
});
Console.WriteLine($"{3 - failures}/3 response checks passed; no Revit API or network executed.");
return failures == 0 ? 0 : 1;
