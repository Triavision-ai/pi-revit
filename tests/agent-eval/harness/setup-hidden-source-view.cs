// A presentation 3D view with hidden content: the roof category and 40 walls are hidden.
// A duplicate of it inherits both, which the modify-duplicate-hidden-view scenario measures.
var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
var view = View3D.CreateIsometric(doc, vft.Id);
view.Name = inputs.GetProperty("name").GetString();
view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_Roofs), true);
var walls = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType()
    .Where(w => w.CanBeHidden(view)).Take(40).Select(w => w.Id).ToList();
view.HideElements(walls);
doc.Regenerate();
return Emit(new { source_view = view.Name, source_view_id = view.Id.Value, hidden_element_ids = walls.Select(i => i.Value).ToList(), hidden_category = "OST_Roofs" });
