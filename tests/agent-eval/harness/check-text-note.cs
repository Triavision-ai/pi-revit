// Ground truth for "a text note with this text near the top-left corner of sheet N".
string number = inputs.GetProperty("sheet_number").GetString();
long maxId = inputs.GetProperty("max_id").GetInt64();
var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => s.SheetNumber == number);
if (sheet == null) return Emit(new { pass = false, reason = $"no sheet {number}" });
var notes = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>().Where(t => t.Id.Value > maxId).ToList();
var onSheet = notes.Where(t => t.OwnerViewId == sheet.Id).ToList();
var matching = onSheet.Where(t => t.Text.Contains("AGENT TEST") && t.Text.Contains("NOT FOR ISSUE")).ToList();
var outline = sheet.Outline;
double width = outline.Max.U - outline.Min.U, height = outline.Max.V - outline.Min.V;
bool topLeft = matching.Count == 1 && matching[0].Coord.X < outline.Min.U + 0.4 * width && matching[0].Coord.Y > outline.Max.V - 0.4 * height;
return Emit(new { pass = matching.Count == 1 && notes.Count == 1 && topLeft, new_text_notes = notes.Count, on_sheet = onSheet.Count, matching = matching.Count, top_left = topLeft,
    position = matching.Count == 1 ? R(matching[0].Coord) : null, sheet_outline = $"{outline.Min.U:0.###},{outline.Min.V:0.###} to {outline.Max.U:0.###},{outline.Max.V:0.###}" });
