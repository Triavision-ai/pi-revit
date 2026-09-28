// Fixture fingerprint: element identity range, pinned state and every view's signature.
var instances = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements();
var typeIds = new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds();
long maxId = Math.Max(instances.Count == 0 ? 0 : instances.Max(e => e.Id.Value), typeIds.Count == 0 ? 0 : typeIds.Max(id => id.Value));
var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate)
    .ToDictionary(v => v.Id.Value.ToString(), v => Signature(v));
return Emit(new {
    max_id = maxId, element_count = instances.Count, type_count = typeIds.Count,
    pinned = instances.Where(e => { try { return e.Pinned; } catch { return false; } }).Select(e => e.Id.Value).OrderBy(x => x).ToList(),
    views, active_view_id = uidoc.ActiveView?.Id.Value, is_modified = doc.IsModified, title = doc.Title, path = doc.PathName,
    family = FamilyState(),
});
