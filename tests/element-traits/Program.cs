using Autodesk.Revit.DB;
using RevitBridge.Tools;

// Offline checks of the production ElementTraits classifier (inv:special-objects-flagged).
int passed = 0, failed = 0;
void Test(string name, Action run)
{
    try { run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); failed++; }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

Test("titleblock revision schedules are flagged in sheet placement listings", () =>
{
    var revision = new ScheduleSheetInstance { IsTitleblockRevisionSchedule = true };
    var placed = new ScheduleSheetInstance();
    Check(ElementTraits.PlacementKind(revision) == "titleblock_revision_schedule", "revision schedule must have its own kind");
    Check(ElementTraits.PlacementKind(placed) == "schedule", "an ordinary schedule instance stays a schedule");
    Check(ElementTraits.PlacementKind(new Viewport()) == "viewport", "viewports keep their kind");
    Check(ElementTraits.For(revision)?["titleblock_revision_schedule"] is true, "the trait must be emitted");
});
Test("ordinary elements add no traits", () => Check(ElementTraits.For(new Element()) is null, "no traits expected"));
Test("sheets, views and schedules report their special states", () =>
{
    Check(ElementTraits.For(new ViewSheet { IsPlaceholder = true })?["placeholder_sheet"] is true, "placeholder sheet");
    Check(ElementTraits.For(new View { IsTemplate = true })?["view_template"] is true, "view template");
    Check(ElementTraits.For(new View { PrimaryViewId = new(42) })?["dependent_view_of"] is 42L, "dependent view");
    Check(ElementTraits.For(new ViewSchedule { IsTitleblockRevisionSchedule = true })?["titleblock_revision_schedule"] is true, "revision schedule view");
});
Test("group, design option and pinned membership are flagged", () =>
{
    var traits = ElementTraits.For(new Element { GroupId = new(7), DesignOption = new DesignOption { Id = new(8) }, Pinned = true })!;
    Check(traits["group_id"] is 7L && traits["design_option_id"] is 8L && traits["pinned"] is true, "membership traits");
});
Console.WriteLine($"{passed} passed, {failed} failed; production ElementTraits with minimal substitutes, no Revit or model operations.");
return failed == 0 ? 0 : 1;
