using System.Text.Json;
using Autodesk.Revit.DB;
using RevitBridge.Tools;

int passed = 0, failed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
ToolContext Fixture(bool countAvailable = true)
{
    var fields = new List<SchedulableField>
    {
        new(ScheduleFieldType.Instance, -1002001, "Mark"),
        new(ScheduleFieldType.ElementType, -1002001, "Type Mark"),
    };
    if (countAvailable) fields.Add(new(ScheduleFieldType.Count, -1152353, "Anzahl"));
    return new(new Document(new ViewSchedule(71, fields)));
}
JsonElement Discover(ToolContext context, string nameFilter = "") => JsonSerializer.SerializeToElement(
    new GetScheduleFields().Execute(JsonSerializer.SerializeToElement(new { schedule_id = 71, name_filter = nameFilter }), context));
JsonElement[] Fields(ToolContext context, string nameFilter = "") => Discover(context, nameFilter).GetProperty("fields").EnumerateArray().ToArray();
JsonElement Pair(JsonElement discovered) => JsonSerializer.SerializeToElement(new
{
    parameter_id = discovered.GetProperty("parameter_id").GetInt64(),
    field_type = discovered.GetProperty("field_type").GetString(),
});
void Add(ToolContext context, JsonElement field) => new ManageSchedules().Execute(
    JsonSerializer.SerializeToElement(new { action = "configure", schedule_id = 71, add_fields = new[] { field } }), context);
void Reject(ToolContext context, string json)
{
    try { Add(context, JsonSerializer.Deserialize<JsonElement>(json)); }
    catch (ArgumentException)
    {
        Require(context.Document!.Schedule.Definition.GetFieldCount() == 0, "Rejected input added a field.");
        return;
    }
    throw new Exception("Invalid field identity was accepted.");
}

Check("discovered Count pair can be added unchanged and is then included", () =>
{
    var context = Fixture();
    var before = Fields(context).Single(f => f.GetProperty("field_type").GetString() == "Count");
    Require(before.GetProperty("parameter_id").GetInt64() == -1152353, "Discovery lost Count's returned parameter ID.");
    Require(!before.GetProperty("included").GetBoolean(), "Count was included before addition.");
    Add(context, Pair(before));
    var after = Fields(context).Single(f => f.GetProperty("field_type").GetString() == "Count");
    Require(after.GetProperty("included").GetBoolean(), "Count is not marked included after addition.");
    var added = context.Document!.Schedule.Definition.GetField(new ScheduleFieldId(0));
    Require(added.FieldType == ScheduleFieldType.Count && added.ParameterId.Value == -1152353, "Added field does not match discovered identity.");
});
Check("Count without parameter_id remains supported", () =>
{
    var context = Fixture();
    Add(context, JsonSerializer.Deserialize<JsonElement>("{\"field_type\":\"Count\"}"));
    Require(context.Document!.Schedule.Definition.GetFieldCount() == 1, "Count was not added.");
    Require(Fields(context).Single(f => f.GetProperty("field_type").GetString() == "Count").GetProperty("included").GetBoolean(), "Explicit Count is not recognized as included.");
});
Check("Count without parameter_id works when discovery does not advertise Count", () =>
{
    var context = Fixture(false);
    Require(Fields(context).All(f => f.GetProperty("field_type").GetString() != "Count"), "Discovery invented an unavailable field.");
    Add(context, JsonSerializer.Deserialize<JsonElement>("{\"field_type\":\"Count\"}"));
    Require(context.Document!.Schedule.Definition.GetFieldCount() == 1, "Explicit Count was not added.");
    Require(Fields(context).Length == 2, "Discovery invented a schedulable field after adding Count.");
});
Check("ordinary discovered negative-ID pairs preserve field type and included identity", () =>
{
    var context = Fixture();
    var original = Fields(context).Single(f => f.GetProperty("field_type").GetString() == "Instance");
    Add(context, Pair(original));
    var after = Fields(context);
    Require(after.Single(f => f.GetProperty("field_type").GetString() == "Instance").GetProperty("included").GetBoolean(), "Added instance field not marked included.");
    Require(!after.Single(f => f.GetProperty("field_type").GetString() == "ElementType").GetProperty("included").GetBoolean(), "Same parameter ID with different type incorrectly marked included.");
    Add(context, Pair(after.Single(f => f.GetProperty("field_type").GetString() == "ElementType")));
    Require(context.Document!.Schedule.Definition.GetFieldCount() == 2, "Second field type was not added.");
});
Check("localized Count discovery remains filterable", () =>
{
    var fields = Fields(Fixture(), "ANZAHL");
    Require(fields.Length == 1 && fields[0].GetProperty("field_type").GetString() == "Count", "Localized Count filtering failed.");
});
Check("unknown Count parameter ID is rejected", () => Reject(Fixture(), "{\"field_type\":\"Count\",\"parameter_id\":-999}"));
Check("another field's parameter ID paired with Count is rejected", () => Reject(Fixture(), "{\"field_type\":\"Count\",\"parameter_id\":-1002001}"));
Check("Count parameter ID paired with another type is rejected", () => Reject(Fixture(), "{\"field_type\":\"Instance\",\"parameter_id\":-1152353}"));
Check("Count ID from another schedule is not accepted without discovery eligibility", () => Reject(Fixture(false), "{\"field_type\":\"Count\",\"parameter_id\":-1152353}"));
Check("null Count parameter ID is invalid rather than omitted", () => Reject(Fixture(), "{\"field_type\":\"Count\",\"parameter_id\":null}"));
Check("nonnumeric Count parameter ID is invalid rather than omitted", () => Reject(Fixture(), "{\"field_type\":\"Count\",\"parameter_id\":\"bad\"}"));
Check("fractional Count parameter ID is invalid rather than omitted", () => Reject(Fixture(), "{\"field_type\":\"Count\",\"parameter_id\":-1152353.5}"));
Check("ordinary field still requires parameter_id", () => Reject(Fixture(), "{\"field_type\":\"Instance\"}"));

Console.WriteLine($"{passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;
