// Elements created after the round baseline: what a run added (ground truth, not the agent's report).
long maxId = inputs.GetProperty("max_id").GetInt64();
var created = new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => e.Id.Value > maxId)
    .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().Where(e => e.Id.Value > maxId)).ToList();
return Emit(new { count = created.Count, items = created.Take(200).Select(e => new {
    id = e.Id.Value, @class = e.GetType().Name, name = Safe(() => e.Name), category = e.Category?.Name,
    owner_view = e.OwnerViewId.Value > 0 ? e.OwnerViewId.Value : (long?)null,
    text = e is TextNote t ? t.Text : null }).ToList() });
