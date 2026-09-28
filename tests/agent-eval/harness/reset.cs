// Reset to the round baseline: delete every element created after it, restore pinned state.
// Runs only on the disposable fixture; never saves.
long maxId = inputs.GetProperty("max_id").GetInt64();
var pinned = new HashSet<long>(inputs.GetProperty("pinned").EnumerateArray().Select(e => e.GetInt64()));
var created = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds()
    .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds())
    .Where(id => id.Value > maxId).OrderByDescending(id => id.Value).ToList();
int deleted = 0, dependent = 0; var failures = new List<object>();
foreach (var id in created)
{
    if (doc.GetElement(id) == null) continue;
    // Some elements (a view's work-plane grid) go only with their owner; the fingerprint check catches leftovers.
    if (!DocumentValidation.CanDeleteElement(doc, id)) { dependent++; continue; }
    try { deleted += doc.Delete(id).Count; }
    catch (Exception ex) { failures.Add(new { id = id.Value, name = Safe(() => doc.GetElement(id)?.Name), error = ex.Message }); }
}
int restored = 0;
foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
{
    bool should = pinned.Contains(e.Id.Value);
    try { if (e.Pinned != should) { e.Pinned = should; restored++; } }
    catch (Exception ex) { failures.Add(new { id = e.Id.Value, name = Safe(() => e.Name), error = "pin: " + ex.Message }); }
}
// A family's types and parameters are not elements: remove those added after the baseline and
// restore the current type. Changed values of baseline types are detected by the fingerprint, not undone.
int typesRemoved = 0, parametersRemoved = 0;
if (doc.IsFamilyDocument && inputs.TryGetProperty("family_types", out var baseTypes))
{
    var fm = doc.FamilyManager;
    var keepTypes = new HashSet<string>(baseTypes.EnumerateArray().Select(e => e.GetString()));
    foreach (var t in fm.Types.Cast<FamilyType>().Where(t => !keepTypes.Contains(t.Name)).ToList())
    {
        try { fm.CurrentType = t; fm.DeleteCurrentType(); typesRemoved++; }
        catch (Exception ex) { failures.Add(new { family_type = t.Name, error = ex.Message }); }
    }
    var keepParameters = new HashSet<string>(inputs.GetProperty("family_parameters").EnumerateArray().Select(e => e.GetString()));
    foreach (var p in fm.Parameters.Cast<FamilyParameter>().Where(p => !keepParameters.Contains(p.Definition.Name)).ToList())
    {
        try { fm.RemoveParameter(p); parametersRemoved++; }
        catch (Exception ex) { failures.Add(new { family_parameter = p.Definition.Name, error = ex.Message }); }
    }
    string current = inputs.GetProperty("family_current").GetString();
    var currentType = fm.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == current);
    if (currentType != null && fm.CurrentType?.Name != current) fm.CurrentType = currentType;
}
return Emit(new { created_found = created.Count, deleted, deleted_with_owner = dependent, remaining = created.Count(id => doc.GetElement(id) != null), pinned_restored = restored,
    family_types_removed = typesRemoved, family_parameters_removed = parametersRemoved, failures });
