// Ground truth for the name collision: the pre-existing view keeps its name and state.
var id = new ElementId(inputs.GetProperty("collision_view_id").GetInt64());
string expected = inputs.GetProperty("collision_signature").GetString();
long maxId = inputs.GetProperty("max_id").GetInt64();
var view = doc.GetElement(id) as View;
var created = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && v.Id.Value > maxId && v.Id != id)
    .Select(v => new { id = v.Id.Value, name = v.Name, roofs_visible = RoofsVisible(v), section_box = v.IsSectionBoxActive }).ToList();
bool unchanged = view != null && Signature(view) == expected;
return Emit(new { pass = unchanged, existing_view_exists = view != null, existing_view_unchanged = unchanged, existing_view_name = view?.Name, new_3d_views = created });
