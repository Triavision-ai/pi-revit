// Ground truth for family type scenarios: every baseline type still exists with unchanged values,
// and (when new_type is given) a new type with that name exists and copies base_type's values.
var state = FamilyState();
var types = (Dictionary<string, string>)state["types"];
var baseline = inputs.GetProperty("baseline_types").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
var changed = baseline.Where(b => !types.TryGetValue(b.Key, out var now) || now != b.Value).Select(b => b.Key).ToList();
var added = types.Keys.Where(name => !baseline.ContainsKey(name)).ToList();
string newType = inputs.TryGetProperty("new_type", out var n) ? n.GetString() : null;
string baseType = inputs.TryGetProperty("base_type", out var b2) ? b2.GetString() : null;
bool newOk = true;
string newTypeDiff = null;
if (newType != null)
{
    newOk = types.TryGetValue(newType, out var values) && baseType != null && baseline.TryGetValue(baseType, out var baseValues) && values == baseValues;
    if (!newOk && types.TryGetValue(newType, out var got) && baseType != null && baseline.TryGetValue(baseType, out var want))
        newTypeDiff = string.Join("; ", got.Split('|').Zip(want.Split('|')).Where(pair => pair.First != pair.Second).Select(pair => pair.First + " (base: " + pair.Second + ")").Take(10));
}
return Emit(new { pass = changed.Count == 0 && newOk, baseline_types_changed = changed, types_added = added, new_type_exists = newType != null && types.ContainsKey(newType),
    new_type_matches_base = newOk, new_type_differences = newTypeDiff, current_type = state["current_type"] });
