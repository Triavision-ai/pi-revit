// Reference data for a model inventory (read-only): non-type elements per category, with the
// category's fixed identity and its name in the running Revit's language.
var rows = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements()
    .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && e.ViewSpecific == false)
    .GroupBy(e => e.Category.Id.Value)
    .Select(g => new { built_in = g.First().Category.BuiltInCategory.ToString(), name = g.First().Category.Name, count = g.Count() })
    .OrderByDescending(r => r.count).ToList();
return Emit(new { categories = rows.Count, elements = rows.Sum(r => r.count), rows });
