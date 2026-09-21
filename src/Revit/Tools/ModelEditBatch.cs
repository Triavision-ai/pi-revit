using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

/// <summary>Runs model-only edits. Files, UI actions and arbitrary scripts must not use this preview contract.</summary>
internal static class ModelEditBatch
{
    internal sealed record Step(Dictionary<string, object?> Target, Func<Dictionary<string, object?>> Apply);

    internal sealed record Result(bool Committed, bool Preview, bool Atomic, bool CommitValidationPerformed,
        IReadOnlyList<Dictionary<string, object?>> Succeeded, IReadOnlyList<Dictionary<string, object?>> Proposed,
        IReadOnlyList<Dictionary<string, object?>> Failed, IReadOnlyList<string> Warnings)
    {
        public object Payload => new
        {
            updated = Committed ? Succeeded.Count : 0, committed = Committed, preview = Preview, atomic = Atomic,
            succeeded = Succeeded, proposed = Proposed, failed = Failed, commitWarnings = Warnings,
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
        var accepted = new List<Dictionary<string, object?>>();
        var failed = new List<Dictionary<string, object?>>();
        using var group = preview ? new TransactionGroup(doc, name + " preview") : null;
        using var transaction = new Transaction(doc, name);
        try
        {
            if (group != null && group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start preview transaction group.");
            if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start model transaction.");
            var failures = FailureGuard.Attach(transaction);
            for (int index = 0; index < steps.Count; index++)
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
                    var rejection = new Dictionary<string, object?>(step.Target) { ["index"] = index, ["reason"] = error.Message };
                    failed.Add(rejection);
                    continue;
                }
                var row = new Dictionary<string, object?>(step.Target) { ["index"] = index };
                foreach (var field in change) row[field.Key] = field.Value;
                accepted.Add(row);
            }
            bool commit = accepted.Count > 0 && !(atomic && failed.Count > 0);
            if (commit)
            {
                var status = transaction.Commit();
                if (status != TransactionStatus.Committed || transaction.GetStatus() != TransactionStatus.Committed)
                    throw new InvalidOperationException($"Commit returned {status}; final status {transaction.GetStatus()}." + failures.DescribeErrors());
            }
            else
            {
                var status = transaction.RollBack();
                if (status != TransactionStatus.RolledBack || transaction.GetStatus() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("Batch rollback could not be confirmed.");
            }
            if (group != null) RollBackGroup(group);
            bool committed = commit && !preview;
            return new Result(committed, preview, atomic, commit,
                committed ? accepted : new List<Dictionary<string, object?>>(),
                committed ? new List<Dictionary<string, object?>>() : accepted, failed, failures.Warnings);
        }
        catch (Exception error)
        {
            string cleanup = FailureGuard.RollBackAndDescribe(transaction);
            if (group != null)
            {
                try { RollBackGroup(group); cleanup += " Preview group rollback confirmed."; }
                catch (Exception rollback) { cleanup += " Preview group rollback not confirmed: " + rollback.Message; }
            }
            throw new InvalidOperationException(error.Message + " " + cleanup, error);
        }
    }

    private static void RollBackGroup(TransactionGroup group)
    {
        if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
        if (group.GetStatus() != TransactionStatus.RolledBack) throw new InvalidOperationException($"Preview group status is {group.GetStatus()}.");
    }
}
