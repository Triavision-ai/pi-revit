using System.Text.Json;
using Autodesk.Revit.DB;
using RevitBridge.Tools;

int passed = 0, failed = 0;
void Test(string name, Action run)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
ModelEditBatch.Result Run(Document doc, object args, params ModelEditBatch.Step[] steps) => ModelEditBatch.Run(doc, "Fixture edit", Args(args), steps);
ModelEditBatch.Step Edit(Document doc, string key, int value, bool fail = false) => new(new() { ["target"] = key }, () =>
{
    doc.Events.Add("apply." + key);
    doc.Values[key] = value;
    if (fail) throw new ArgumentException("Rejected " + key);
    return new() { ["after"] = value };
});
void Baseline(Document doc) => Check(doc.Values.Count == 1 && doc.Values["baseline"] == 100, "model snapshot was not fully restored");
InvalidOperationException Reject(Action run, string expected)
{
    try { run(); }
    catch (InvalidOperationException error)
    {
        Check(error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), $"missing error detail '{expected}': {error.Message}");
        return error;
    }
    throw new Exception("operation should have failed instead of reporting success");
}

foreach (string flag in new[] { "preview", "atomic" })
foreach (var invalid in new (string Label, object? Value)[]
{
    ("string", "true"), ("null", null), ("number", 1), ("object", new { enabled = true }),
})
Test($"{flag} rejects {invalid.Label} before opening transactions or invoking edits", () =>
{
    var doc = new Document();
    var args = new Dictionary<string, object?> { ["preview"] = true, ["atomic"] = true, [flag] = invalid.Value };
    bool rejected = false;
    try { Run(doc, args, Edit(doc, "mustNotRun", 1)); }
    catch (ArgumentException error)
    {
        rejected = true;
        Check(error.Message.Contains($"{flag} must be a boolean", StringComparison.Ordinal), "validation error must identify the invalid flag");
    }
    Check(rejected, "nonboolean flag must not be accepted or coerced");
    Check(doc.Events.Count == 0, "invalid flags must fail before any transaction or edit action");
    Baseline(doc);
});

Test("default partial batch commits valid steps and rolls back failed step mutations", () =>
{
    var doc = new Document();
    doc.Warnings.Add("Fixture warning");
    var result = Run(doc, new { }, Edit(doc, "first", 1), Edit(doc, "rejected", 2, true), Edit(doc, "last", 3));
    Check(result.Committed && !result.Atomic && !result.Preview, "default behavior must commit partial successes");
    Check(result.CommitValidationPerformed, "committed edit must report completed commit validation");
    Check(doc.Values.Count == 3 && doc.Values["first"] == 1 && doc.Values["last"] == 3 && !doc.Values.ContainsKey("rejected"), "failed step leaked changes or valid steps were lost");
    Check(result.Succeeded.Count == 2 && result.Proposed.Count == 0 && result.Failed.Count == 1, "incorrect outcome partition");
    Check((int)result.Succeeded[1]["index"]! == 2 && (int)result.Succeeded[1]["after"]! == 3, "successful step index or output missing");
    Check((string)result.Failed[0]["target"]! == "rejected" && (int)result.Failed[0]["index"]! == 1 && ((string)result.Failed[0]["reason"]!).Contains("Rejected rejected"), "failure must retain target, index and reason");
    Check(result.Warnings.SequenceEqual(new[] { "Fixture warning" }), "commit warning missing");
    Check(JsonSerializer.SerializeToElement(result.Payload).GetProperty("updated").GetInt32() == 2, "updated count must contain committed successes only");
    Check(doc.Events.IndexOf("transaction.start") < doc.Events.IndexOf("failures.attach"), "failure handling must attach after start");
});

Test("atomic failure restores all steps without committing outer transaction", () =>
{
    var doc = new Document();
    var result = Run(doc, new { atomic = true }, Edit(doc, "first", 1), Edit(doc, "bad", 2, true), Edit(doc, "last", 3));
    Baseline(doc);
    Check(!result.Committed && result.Atomic && result.Succeeded.Count == 0 && result.Proposed.Count == 2 && result.Failed.Count == 1, "atomic result must distinguish proposed from committed changes");
    Check(!result.CommitValidationPerformed, "rejected atomic batch cannot report commit validation");
    Check(!doc.Events.Contains("transaction.commit") && doc.Events.Contains("transaction.rollback"), "atomic rejected batch must roll back before outer commit");
    Check(JsonSerializer.SerializeToElement(result.Payload).GetProperty("updated").GetInt32() == 0, "atomic rollback cannot report updated rows");
});

Test("preview validates via transaction commit then restores the group snapshot", () =>
{
    var doc = new Document();
    var result = Run(doc, new { preview = true }, Edit(doc, "first", 1), Edit(doc, "bad", 2, true));
    Baseline(doc);
    Check(result.Preview && !result.Committed && result.Succeeded.Count == 0 && result.Proposed.Count == 1 && result.Failed.Count == 1, "preview cannot report persisted successes");
    Check(doc.Events.IndexOf("group.start") < doc.Events.IndexOf("transaction.start"), "group must enclose transaction");
    Check(doc.Events.IndexOf("transaction.commit") >= 0 && doc.Events.IndexOf("transaction.commit") < doc.Events.IndexOf("group.rollback"), "successful preview must validate before group rollback");
    Check(!doc.Events.Contains("group.commit"), "preview group must never commit");
    var payload = JsonSerializer.SerializeToElement(result.Payload);
    Check(result.CommitValidationPerformed && payload.GetProperty("commit_validation_performed").GetBoolean(), "validated preview must report completed commit validation");
    Check(payload.GetProperty("validation").GetString()!.Contains("validation completed"), "preview validation explanation must match performed validation");
});

Test("atomic failed preview rolls back before validation commit", () =>
{
    var doc = new Document();
    var result = Run(doc, new { preview = true, atomic = true }, Edit(doc, "first", 1), Edit(doc, "bad", 2, true));
    Baseline(doc);
    Check(!result.Committed && result.Proposed.Count == 1 && result.Failed.Count == 1, "atomic preview result incorrect");
    Check(!doc.Events.Contains("transaction.commit") && doc.Events.Contains("transaction.rollback") && doc.Events.Contains("group.rollback"), "failed atomic preview cannot claim commit validation");
    var payload = JsonSerializer.SerializeToElement(result.Payload);
    Check(!result.CommitValidationPerformed && !payload.GetProperty("commit_validation_performed").GetBoolean(), "failed atomic preview must report skipped validation");
    Check(payload.GetProperty("validation").GetString()!.Contains("was not performed"), "preview explanation must disclose skipped commit validation");
});

Test("empty and entirely rejected batches roll back without a commit", () =>
{
    foreach (bool withFailure in new[] { false, true })
    {
        var doc = new Document();
        var steps = withFailure ? new[] { Edit(doc, "bad", 2, true) } : Array.Empty<ModelEditBatch.Step>();
        var result = Run(doc, new { }, steps);
        Baseline(doc);
        Check(!result.Committed && result.Succeeded.Count == 0 && result.Proposed.Count == 0 && result.Failed.Count == (withFailure ? 1 : 0), "no accepted work must remain uncommitted");
        Check(!doc.Events.Contains("transaction.commit"), "empty accepted batch cannot commit");
        Check(!result.CommitValidationPerformed, "empty accepted batch must report no commit validation");
    }
});

Test("subtransaction commit rollback rejects its changes and allows the next step", () =>
{
    var doc = new Document();
    doc.SubtransactionPlan.Commits.Enqueue(Outcome.As(TransactionStatus.RolledBack));
    var result = Run(doc, new { }, Edit(doc, "rejected", 1), Edit(doc, "accepted", 2));
    Check(result.Committed && result.Failed.Count == 1 && result.Succeeded.Count == 1, "failed subcommit must become one rejected step");
    Check(!doc.Values.ContainsKey("rejected") && doc.Values["accepted"] == 2, "subcommit rollback must isolate changes");
});

Test("full commit rejection restores the transaction and exposes failure details", () =>
{
    var doc = new Document();
    doc.TransactionPlan.Commits.Enqueue(Outcome.As(TransactionStatus.RolledBack));
    doc.Errors.Add("Fixture validation rejected");
    var error = Reject(() => Run(doc, new { }, Edit(doc, "first", 1), Edit(doc, "last", 2)), "Commit returned RolledBack");
    Baseline(doc);
    Check(error.Message.Contains("Fixture validation rejected") && error.Message.Contains("rollback confirmed"), "commit errors and confirmed cleanup must reach caller");
});

Test("commit return value cannot hide a noncommitted final status", () =>
{
    var doc = new Document();
    doc.TransactionPlan.Commits.Enqueue(new(TransactionStatus.Committed, TransactionStatus.Pending));
    var error = Reject(() => Run(doc, new { }, Edit(doc, "first", 1)), "final status Pending");
    Check(error.Message.Contains("rollback is not confirmed"), "uncertain final status must remain explicit");
});

Test("unconfirmed step rollback stops later edits even when outer cleanup succeeds", () =>
{
    var doc = new Document();
    doc.SubtransactionPlan.Rollbacks.Enqueue(Outcome.As(TransactionStatus.Pending));
    Reject(() => Run(doc, new { }, Edit(doc, "first", 1), Edit(doc, "bad", 2, true), Edit(doc, "mustNotRun", 3)), "Edit rollback could not be confirmed; stopping the batch");
    Check(!doc.Events.Contains("apply.mustNotRun") && !doc.Events.Contains("transaction.commit"), "uncertain step rollback must stop the batch immediately");
    Baseline(doc);
});

Test("unconfirmed batch rollback throws instead of returning an atomic result", () =>
{
    var doc = new Document();
    doc.TransactionPlan.Rollbacks.Enqueue(Outcome.As(TransactionStatus.Pending));
    var error = Reject(() => Run(doc, new { atomic = true }, Edit(doc, "first", 1), Edit(doc, "bad", 2, true)), "Batch rollback could not be confirmed");
    Check(error.Message.Contains("rollback is not confirmed"), "uncertain transaction rollback must be reported");
});

Test("unconfirmed preview group rollback cannot return a successful preview", () =>
{
    var doc = new Document();
    doc.GroupPlan.Rollbacks.Enqueue(Outcome.As(TransactionStatus.Pending));
    var error = Reject(() => Run(doc, new { preview = true }, Edit(doc, "first", 1)), "Preview group status is Pending");
    Check(error.Message.Contains("Preview group rollback not confirmed"), "group uncertainty must be preserved in cleanup report");
    Check(doc.Events.Contains("transaction.commit"), "fixture must reach committed preview before group rollback failure");
});

Test("regeneration failure aborts immediately and restores earlier changes", () =>
{
    var doc = new Document();
    var regeneration = new ModelEditBatch.Step(new() { ["target"] = "regeneration" }, () =>
    {
        doc.Values["regeneration"] = 2;
        throw new Autodesk.Revit.Exceptions.RegenerationFailedException("Fixture regeneration failure");
    });
    Reject(() => Run(doc, new { }, Edit(doc, "first", 1), regeneration, Edit(doc, "mustNotRun", 3)), "Fixture regeneration failure");
    Baseline(doc);
    Check(!doc.Events.Contains("apply.mustNotRun") && !doc.Events.Contains("transaction.commit"), "regeneration failure must not be treated as a recoverable per-step error");
});

Console.WriteLine($"RESULT {passed} passed; {failed} failed. Production ModelEditBatch with controlled transaction snapshots; no native Revit or model operations.");
return failed == 0 ? 0 : 1;
