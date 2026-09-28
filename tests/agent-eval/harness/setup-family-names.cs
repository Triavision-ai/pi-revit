// Names a family scenario refers to (read-only): the alphabetically first type, which the
// collision scenario asks to create again and the copy scenario uses as its base.
var names = doc.FamilyManager.Types.Cast<FamilyType>().Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).OrderBy(n => n).ToList();
return Emit(new { existing_type = names.First(), type_count = names.Count });
