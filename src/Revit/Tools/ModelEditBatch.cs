using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

/// <summary>Runs model-only edits. Files, UI actions and arbitrary scripts must not use this preview contract.</summary>
internal static class ModelEditBatch
{
    // AffectedIds is evaluated for each attempt, after Apply returns. Creation and replacement
    // steps declare their newly produced IDs explicitly so native commit failures can isolate rows.
    internal sealed record Step(Dictionary<string, object?> Target, Func<Dictionary<string, object?>> Apply,
        Func<Dictionary<string, object?>, IReadOnlyCollection<long>>? AffectedIds = null);

    internal sealed record Result(bool Committed, bool Preview, bool Atomic, bool CommitValidationPerformed,
        IReadOnlyList<Dictionary<string, object?>> Succeeded, IReadOnlyList<Dictionary<string, object?>> Proposed,
        IReadOnlyList<Dictionary<string, object?>> Failed, IReadOnlyList<string> Warnings,
        IReadOnlyList<object>? CommitDiagnostics = null)
    {
        public object Payload => new
        {
            updated = Committed ? Succeeded.Count : 0, committed = Committed, preview = Preview, atomic = Atomic,
            succeeded = Succeeded, proposed = Proposed, failed = Failed, commitWarnings = Warnings,
            commitDiagnostics = CommitDiagnostics,
            commit_validation_performed = CommitValidationPerformed,
            validation = Preview ? (CommitValidationPerformed ? "Revit commit validation completed; preview group rollback confirmed." : "Preview group rollback confirmed. Commit-time validation was not performed because the batch was rejected before commit.") : null,
        };
    }

    public static Result Run(Document doc, string name, JsonElement args, IReadOnlyList<Step> steps)
    {
        foreach (string flag in new[] { "preview", "atomic" })
            if (args.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException($"{flag} must be a boolean; no edits were attempted.");
        bool preview = JsonArgs.GetBool(args, "preview", false);
        bool atomic = JsonArgs.GetBool(args, "atomic", false);
        var failed = new List<Dictionary<string, object?>>();
        var commitDiagnostics = new List<object>();
        using var group = preview ? new TransactionGroup(doc, name + " preview") : null;
        Transaction? transaction = null;
        FailureGuard? currentFailures = null;
        try
        {
            if (group != null && group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start preview transaction group.");
            var work = Enumerable.Range(0, steps.Count).ToList();
            for (int attempt = 0; ; attempt++)
            {
                currentFailures = null;
                transaction?.Dispose();
                transaction = new Transaction(doc, name);
                if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start model transaction.");
                var failures = FailureGuard.Attach(transaction);
                currentFailures = failures;
                var accepted = new List<(int Index, Dictionary<string, object?> Row, IReadOnlyCollection<long> AffectedIds)>();
                foreach (int index in work)
                {
                    var step = steps[index];
                    using var part = new SubTransaction(doc);
                    if (part.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start edit subtransaction.");
                    Dictionary<string, object?> change;
                    try
                    {
                        change = step.Apply();
                        if (part.Commit() != TransactionStatus.Committed || part.GetStatus() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Edit subtransaction did not commit.");
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                    catch (Exception error)
                    {
                        if (part.GetStatus() == TransactionStatus.Started) part.RollBack();
                        if (part.GetStatus() != TransactionStatus.RolledBack)
                            throw new InvalidOperationException("Edit rollback could not be confirmed; stopping the batch.", error);
                        var rejection = Rejection(step, index, error.Message);
                        // Structured facts a shared helper attached (for example ElementNames' name_collision).
                        foreach (System.Collections.DictionaryEntry entry in error.Data)
                            if (entry.Key is string key && !rejection.ContainsKey(key)) rejection[key] = entry.Value;
                        failed.Add(rejection);
                        continue;
                    }
                    var row = new Dictionary<string, object?>(step.Target) { ["index"] = index };
                    foreach (var field in change) row[field.Key] = field.Value;
                    accepted.Add((index, row, step.AffectedIds?.Invoke(row) ?? Array.Empty<long>()));
                }
                bool commit = accepted.Count > 0 && !(atomic && failed.Count > 0);
                if (!commit)
                {
                    var status = transaction.RollBack();
                    if (status != TransactionStatus.RolledBack || transaction.GetStatus() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Batch rollback could not be confirmed.");
                    if (group != null) RollBackGroup(group);
                    return new Result(false, preview, atomic, attempt > 0, new List<Dictionary<string, object?>>(),
                        accepted.Select(a => a.Row).ToList(), Ordered(failed), failures.Warnings, commitDiagnostics.Count > 0 ? commitDiagnostics : null);
                }

                var committedStatus = transaction.Commit();
                if (failures.HasDiagnostics)
                    commitDiagnostics.Add(new { attempt = attempt + 1, diagnostics = failures.Diagnostics(transaction.GetStatus().ToString()) });
                if (committedStatus == TransactionStatus.Committed && transaction.GetStatus() == TransactionStatus.Committed)
                {
                    if (group != null) RollBackGroup(group);
                    bool committed = !preview;
                    var rows = accepted.Select(a => a.Row).ToList();
                    return new Result(committed, preview, atomic, true,
                        committed ? rows : new List<Dictionary<string, object?>>(),
                        committed ? new List<Dictionary<string, object?>>() : rows, Ordered(failed), failures.Warnings, commitDiagnostics.Count > 0 ? commitDiagnostics : null);
                }
                // Revit rejected the batch while committing (a group edited outside group edit mode,
                // dimensions losing their references, ...). Its failure handling rolled the whole
                // transaction back; only an unconfirmed rollback still stops the call.
                if (transaction.GetStatus() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException($"Commit returned {committedStatus}; final status {transaction.GetStatus()}.");
                string reason = "Revit rejected the batch when committing" + (failures.Errors.Count > 0 ? ": " + failures.ErrorSummary() : ".");
                var implicated = accepted.Where(a => Implicated(steps[a.Index], a.AffectedIds, failures.ErrorElementIds)).Select(a => a.Index).ToHashSet();
                bool isolate = !atomic && attempt == 0 && implicated.Count > 0 && implicated.Count < accepted.Count;
                if (!isolate)
                {
                    // Nothing more can be told apart: every update of this commit is reported with Revit's reason.
                    foreach (var (index, _, _) in accepted)
                        failed.Add(Rejection(steps[index], index, implicated.Count == 0 || implicated.Contains(index) ? reason : "Rolled back with the batch. " + reason,
                            implicated.Contains(index)));
                    if (group != null) RollBackGroup(group);
                    return new Result(false, preview, atomic, true, new List<Dictionary<string, object?>>(),
                        new List<Dictionary<string, object?>>(), Ordered(failed), failures.Warnings, commitDiagnostics.Count > 0 ? commitDiagnostics : null);
                }
                // Non-atomic: drop the updates on the elements Revit named and commit the rest once more.
                foreach (int index in implicated) failed.Add(Rejection(steps[index], index, reason, true));
                work = accepted.Select(a => a.Index).Where(index => !implicated.Contains(index)).ToList();
            }
        }
        catch (Exception error)
        {
            string cleanup = transaction is null ? string.Empty : FailureGuard.RollBackAndDescribe(transaction);
            string diagnostics = transaction is null ? string.Empty : currentFailures?.DescribeErrors(FailureGuard.TransactionStatusText(transaction)) ?? string.Empty;
            if (group != null)
            {
                try { RollBackGroup(group); cleanup += " Preview group rollback confirmed."; }
                catch (Exception rollback) { cleanup += " Preview group rollback not confirmed: " + rollback.Message; }
            }
            throw new InvalidOperationException(error.Message + " " + cleanup + diagnostics, error);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static Dictionary<string, object?> Rejection(Step step, int index, string reason, bool? named = null)
    {
        var rejection = new Dictionary<string, object?>(step.Target) { ["index"] = index, ["reason"] = reason };
        if (named != null) rejection["named_by_revit"] = named;
        return rejection;
    }

    private static List<Dictionary<string, object?>> Ordered(List<Dictionary<string, object?>> rows)
        => rows.OrderBy(row => row.TryGetValue("index", out var index) && index is int value ? value : int.MaxValue).ToList();

    /// <summary>Whether a step targets an element Revit's commit failure named (id, element_id or element_ids).</summary>
    private static bool Implicated(Step step, IReadOnlyCollection<long> affected, IReadOnlyCollection<long> failing)
    {
        if (failing.Count == 0) return false;
        if (affected.Any(failing.Contains)) return true;
        foreach (string key in new[] { "id", "element_id" })
            if (step.Target.TryGetValue(key, out var single) && single is long id && failing.Contains(id)) return true;
        return step.Target.TryGetValue("element_ids", out var many) && many is IEnumerable<long> ids && ids.Any(failing.Contains);
    }

    private static void RollBackGroup(TransactionGroup group)
    {
        if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
        if (group.GetStatus() != TransactionStatus.RolledBack) throw new InvalidOperationException($"Preview group status is {group.GetStatus()}.");
    }
}
