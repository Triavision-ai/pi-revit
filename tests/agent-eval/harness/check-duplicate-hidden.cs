// Ground truth for "duplicate the source view; in the copy the whole building must be visible":
// the copy shows the roof and the walls hidden in the source, and the source is unchanged.
string copyName = inputs.GetProperty("copy_name").GetString();
var source = doc.GetElement(new ElementId(inputs.GetProperty("source_view_id").GetInt64())) as View3D;
var hiddenIds = inputs.GetProperty("hidden_element_ids").EnumerateArray().Select(e => new ElementId(e.GetInt64())).ToList();
var copies = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && v.Name == copyName).ToList();
bool sourceUnchanged = source != null && !RoofsVisible(source) && hiddenIds.All(id => doc.GetElement(id)?.IsHidden(source) == true);
if (copies.Count != 1) return Emit(new { pass = false, reason = $"{copies.Count} views named '{copyName}'", source_unchanged = sourceUnchanged });
var copy = copies[0];
int stillHidden = hiddenIds.Count(id => doc.GetElement(id)?.IsHidden(copy) == true);
bool roofs = RoofsVisible(copy);
return Emit(new { pass = roofs && stillHidden == 0 && sourceUnchanged, copy_id = copy.Id.Value, roofs_visible = roofs, source_walls_still_hidden = stillHidden,
    hidden_categories = HiddenCategories(copy).Count, source_unchanged = sourceUnchanged, perspective = copy.IsPerspective });
