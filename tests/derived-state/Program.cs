using RevitBridge.Tools;

// Offline checks of the Revit-free parts of the derived-object mechanisms: the net change
// accounting behind model_changes (inv:model-changes-reported) and the inherited-state
// summary (inv:derived-state-reported). Revit reading is verified live.
int passed = 0, failed = 0;
void Test(string name, Action run)
{
    try { run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); failed++; }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
long[] None = Array.Empty<long>();

Test("committed changes are reported as added, modified and deleted", () =>
{
    var changes = new ChangeSet();
    changes.Record("TransactionCommitted", new long[] { 10, 11 }, new long[] { 5, 10 }, new long[] { 7 });
    Check(changes.Observed && !changes.RolledBack, "observed, not rolled back");
    Check(changes.Added.OrderBy(x => x).SequenceEqual(new long[] { 10, 11 }), "added");
    Check(changes.Modified.SequenceEqual(new long[] { 5 }), "an object created in this call counts as added only");
    Check(changes.Deleted.SequenceEqual(new long[] { 7 }), "deleted");
});
Test("created and deleted within one call leaves nothing", () =>
{
    var changes = new ChangeSet();
    changes.Record("TransactionCommitted", new long[] { 20 }, None, None);
    changes.Record("TransactionCommitted", None, None, new long[] { 20 });
    Check(changes.IsEmpty, "no net change");
});
Test("preview rollback leaves no net change", () =>
{
    var changes = new ChangeSet();
    changes.Record("TransactionCommitted", new long[] { 30 }, new long[] { 3 }, None);
    changes.Record("TransactionGroupRolledBack", None, new long[] { 3 }, new long[] { 30 });
    Check(changes.IsEmpty && changes.RolledBack, "a rolled-back group discards what it contained");
});
Test("a call without document events is not observed", () =>
{
    var changes = new ChangeSet();
    Check(!changes.Observed && changes.IsEmpty, "unknown, not 'no change'");
});
Test("inherited view state lists hidden content and the check", () =>
{
    var facts = new InheritedState.ViewFacts(4101, 77, "Presentation", new[] { "OST_Roofs|Roofs" }, 25, new long[] { 1, 2, 3 },
        2, true, 1, new[] { (99L, "Hide roofs", false) }, false, "HLR", "Medium", true, false, true);
    var state = InheritedState.Summarize(facts, InheritedState.ViewCheck);
    Check(state["derived_from"] is 4101L, "source");
    Check(state["hidden_categories"] is Dictionary<string, object?> categories && categories["count"] is 1, "hidden categories");
    Check(state["hidden_elements"] is Dictionary<string, object?> elements && elements["count"] is 25 && elements["complete"] is true, "hidden elements");
    Check(state.ContainsKey("template") && state.ContainsKey("filters") && state["element_overrides"] is 2 && state["category_overrides"] is 1, "template, filters, overrides");
    Check((string)state["check"]! == InheritedState.ViewCheck, "the caller is told to compare with the request");
});
Test("a clean view reports only its display and the check", () =>
{
    var facts = new InheritedState.ViewFacts(null, null, null, Array.Empty<string>(), 0, None, 0, true, 0,
        Array.Empty<(long, string, bool)>(), false, "Shading", "Fine", false, null, null);
    var state = InheritedState.Summarize(facts, InheritedState.NewViewCheck);
    Check(state.Keys.OrderBy(k => k).SequenceEqual(new[] { "check", "display" }), string.Join(",", state.Keys));
});
Test("an interrupted element scan is reported as a lower bound", () =>
{
    var facts = new InheritedState.ViewFacts(null, null, null, Enumerable.Range(0, 45).Select(i => $"OST_{i}|c{i}").ToArray(), 3, None, 0, false, 0,
        Array.Empty<(long, string, bool)>(), false, null, null, false, null, null);
    var state = InheritedState.Summarize(facts, InheritedState.NewViewCheck);
    Check(state["hidden_elements"] is Dictionary<string, object?> elements && elements["complete"] is false && state.ContainsKey("scan_note"), "lower bound");
    Check(state["hidden_categories"] is Dictionary<string, object?> categories && ((List<string>)categories["categories"]!).Count == InheritedState.ListCap
        && categories["truncated"] is true && categories["count"] is 45, "capped list keeps the full count");
});
Console.WriteLine($"{passed} passed, {failed} failed; production ChangeSet and InheritedState summary, no Revit or model operations.");
return failed == 0 ? 0 : 1;
