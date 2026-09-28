// Shared helpers prepended to every harness script. Harness scripts run through the bridge's
// execute_csharp on the disposable fixture only (the runner checks identity and path first).
// Results go to inputs.out_file as complete JSON: execute_csharp caps returned lists at 100 items.
object Emit(object value) { System.IO.File.WriteAllText(inputs.GetProperty("out_file").GetString(), System.Text.Json.JsonSerializer.Serialize(value)); return "written"; }
string R(XYZ p) => $"{p.X:0.###},{p.Y:0.###},{p.Z:0.###}";
string Safe(Func<string> read) { try { return read(); } catch { return "?"; } }
// Model categories only: they decide what of the building a view shows. Sheets and schedules show none.
List<Category> modelCategories = null;
List<long> HiddenCategories(View v)
{
    var hidden = new List<long>();
    if (v is ViewSheet || v is ViewSchedule) return hidden;
    modelCategories ??= doc.Settings.Categories.Cast<Category>().Where(c => c.CategoryType == CategoryType.Model).ToList();
    foreach (var c in modelCategories)
        try { if (v.CanCategoryBeHidden(c.Id) && v.GetCategoryHidden(c.Id)) hidden.Add(c.Id.Value); } catch { }
    return hidden;
}
string Signature(View v)
{
    var parts = new List<string> { v.Name, v.ViewTemplateId.Value.ToString(), Safe(() => v.DetailLevel.ToString()), Safe(() => v.DisplayStyle.ToString()),
        Safe(() => v.CropBoxActive ? R(v.CropBox.Min) + ";" + R(v.CropBox.Max) : "nocrop") };
    if (v is View3D v3)
    {
        var o = v3.GetOrientation();
        parts.Add($"{v3.IsPerspective}|{R(o.EyePosition)}|{R(o.ForwardDirection)}|{v3.IsSectionBoxActive}");
        if (v3.IsSectionBoxActive) { var b = v3.GetSectionBox(); parts.Add(R(b.Min) + ";" + R(b.Max)); }
    }
    parts.Add(string.Join(",", HiddenCategories(v)));
    return string.Join("|", parts);
}
// A family document's state that elements do not show: its types (with every parameter value),
// its parameters and the current type. Null in a project document.
Dictionary<string, object> FamilyState()
{
    if (!doc.IsFamilyDocument) return null;
    var fm = doc.FamilyManager;
    var parameters = fm.Parameters.Cast<FamilyParameter>().OrderBy(p => p.Definition.Name).ToList();
    string Value(FamilyType t, FamilyParameter p) => Safe(() => t.HasValue(p) ? (t.AsValueString(p) ?? t.AsString(p) ?? t.AsDouble(p)?.ToString() ?? t.AsInteger(p)?.ToString() ?? "") : "(none)");
    return new Dictionary<string, object>
    {
        ["types"] = fm.Types.Cast<FamilyType>().ToDictionary(t => t.Name, t => string.Join("|", parameters.Select(p => p.Definition.Name + "=" + Value(t, p)))),
        ["parameters"] = parameters.Select(p => p.Definition.Name + (p.IsInstance ? " (instance)" : " (type)") + (string.IsNullOrEmpty(p.Formula) ? "" : " = " + p.Formula)).ToList(),
        ["parameter_names"] = parameters.Select(p => p.Definition.Name).ToList(),
        ["current_type"] = fm.CurrentType?.Name ?? "",
        ["category"] = doc.OwnerFamily?.FamilyCategory?.Name ?? "",
    };
}
bool RoofsVisible(View v) { var roofs = new ElementId(BuiltInCategory.OST_Roofs); return !(v.CanCategoryBeHidden(roofs) && v.GetCategoryHidden(roofs)); }
