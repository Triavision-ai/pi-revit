// Ground truth for "the layer build-up of every wall type placed in this model" (read-only).
var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>().ToList();
var types = walls.GroupBy(w => w.GetTypeId().Value).Select(g => doc.GetElement(new ElementId(g.Key)) as WallType).Where(t => t != null).ToList();
var rows = types.Select(t =>
{
    var structure = t.GetCompoundStructure();
    var layers = structure?.GetLayers().Select(l => new {
        function = l.Function.ToString(),
        material = doc.GetElement(l.MaterialId)?.Name,
        mm = Math.Round(UnitUtils.ConvertFromInternalUnits(l.Width, UnitTypeId.Millimeters), 1) }).ToList();
    return new { id = t.Id.Value, name = t.Name, kind = t.Kind.ToString(), instances = walls.Count(w => w.GetTypeId() == t.Id),
        layers, vertically_compound = structure != null && !structure.IsVerticallyHomogeneous() };
}).OrderBy(r => r.name).ToList();
return Emit(new { placed_types = rows.Count, instances = walls.Count, basic = rows.Count(r => r.kind == "Basic"), curtain = rows.Count(r => r.kind == "Curtain"),
    stacked = rows.Count(r => r.kind == "Stacked"), total_layers = rows.Sum(r => r.layers?.Count ?? 0), rows });
