// Ground truth for "a 3D view named X showing the whole site, terrain included, from the north-east".
string name = inputs.GetProperty("name").GetString();
var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && v.Name == name).ToList();
if (views.Count != 1) return Emit(new { pass = false, reason = $"{views.Count} views named '{name}'" });
var view = views[0];
var forward = view.GetOrientation().ForwardDirection;
bool fromNorthEast = forward.X < -0.1 && forward.Y < -0.1;
var terrainCategories = new[] { BuiltInCategory.OST_Toposolid, BuiltInCategory.OST_Topography };
var terrainHidden = terrainCategories.Where(c => { var id = new ElementId(c); try { return view.CanCategoryBeHidden(id) && view.GetCategoryHidden(id); } catch { return false; } }).Select(c => c.ToString()).ToList();
var terrain = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(terrainCategories.ToList())).WhereElementIsNotElementType().ToElements();
int terrainElementsHidden = terrain.Count(e => { try { return e.IsHidden(view); } catch { return false; } });
return Emit(new { pass = fromNorthEast && terrainHidden.Count == 0 && terrainElementsHidden == 0 && terrain.Count > 0, id = view.Id.Value, perspective = view.IsPerspective,
    forward = R(forward), from_north_east = fromNorthEast, terrain_elements = terrain.Count, terrain_categories_hidden = terrainHidden, terrain_elements_hidden = terrainElementsHidden,
    section_box = view.IsSectionBoxActive, hidden_model_categories = HiddenCategories(view).Count });
