// Someone's working view that already has the name the request asks for: a 3D view with a
// section box around the lower half of the building. The modify-name-collision scenario checks
// that it is neither edited, renamed, replaced nor deleted.
var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
var view = View3D.CreateIsometric(doc, vft.Id);
view.Name = inputs.GetProperty("name").GetString();
var boxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType()
    .Select(w => w.get_BoundingBox(null)).Where(b => b != null).ToList();
var min = new XYZ(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y), boxes.Min(b => b.Min.Z));
var max = new XYZ(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y), boxes.Max(b => b.Max.Z));
view.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = new XYZ(max.X, max.Y, (min.Z + max.Z) / 2) });
doc.Regenerate();
return Emit(new { collision_view = view.Name, collision_view_id = view.Id.Value, collision_signature = Signature(view) });
