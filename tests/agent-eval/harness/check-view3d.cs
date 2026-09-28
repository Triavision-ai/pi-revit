// Ground truth for "a perspective 3D view of the whole building from the south-east named X".
string name = inputs.GetProperty("name").GetString();
long maxId = inputs.GetProperty("max_id").GetInt64();
var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && v.Name == name).ToList();
if (views.Count != 1) return Emit(new { pass = false, reason = $"{views.Count} views named '{name}'" });
var view = views[0];
var forward = view.GetOrientation().ForwardDirection;
var main = new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Floors });
int hidden = new FilteredElementCollector(doc).WherePasses(main).WhereElementIsNotElementType().ToElements().Count(e => { try { return e.IsHidden(view); } catch { return false; } });
bool fromSouthEast = forward.X < -0.1 && forward.Y > 0.1;
bool roofs = RoofsVisible(view);
return Emit(new { pass = view.IsPerspective && roofs && fromSouthEast && hidden == 0, id = view.Id.Value, created_in_round = view.Id.Value > maxId,
    perspective = view.IsPerspective, roofs_visible = roofs, forward = R(forward), from_south_east = fromSouthEast, hidden_walls_roofs_floors = hidden,
    hidden_categories = HiddenCategories(view).Count });
