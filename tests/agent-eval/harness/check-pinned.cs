// Ground truth for "pin all grids and levels": every grid and level pinned, nothing else newly pinned.
var baseline = new HashSet<long>(inputs.GetProperty("pinned").EnumerateArray().Select(e => e.GetInt64()));
var targets = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Grids).WhereElementIsNotElementType().ToElements()
    .Concat(new FilteredElementCollector(doc).OfClass(typeof(Level)).ToElements()).ToList();
var targetIds = new HashSet<long>(targets.Select(e => e.Id.Value));
int unpinned = targets.Count(e => !e.Pinned);
var others = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Where(e => { try { return e.Pinned; } catch { return false; } })
    .Where(e => !baseline.Contains(e.Id.Value) && !targetIds.Contains(e.Id.Value)).Select(e => new { id = e.Id.Value, name = Safe(() => e.Name), category = e.Category?.Name }).Take(20).ToList();
return Emit(new { pass = unpinned == 0 && others.Count == 0, grids_and_levels = targets.Count, unpinned, other_newly_pinned = others });
