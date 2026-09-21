using System.Text.Json;

namespace Autodesk.Revit.DB
{
    public enum TransactionStatus { Uninitialized, Started, Committed, RolledBack, Pending, Error }
    public sealed record Outcome(TransactionStatus Returned, TransactionStatus Final)
    {
        public static Outcome As(TransactionStatus status) => new(status, status);
    }
    public sealed class TransactionPlan
    {
        public Queue<Outcome> Starts { get; } = new();
        public Queue<Outcome> Commits { get; } = new();
        public Queue<Outcome> Rollbacks { get; } = new();
    }
    public sealed class Document
    {
        public Dictionary<string, int> Values { get; } = new() { ["baseline"] = 100 };
        public List<string> Events { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();
        public TransactionPlan TransactionPlan { get; } = new();
        public TransactionPlan SubtransactionPlan { get; } = new();
        public TransactionPlan GroupPlan { get; } = new();
    }

    // Each scope owns a snapshot. Status queues let tests distinguish API return values
    // from final status, and simulate failed cleanup without silently fixing it in Dispose.
    public abstract class Scope(Document doc, string kind, TransactionPlan plan) : IDisposable
    {
        private Dictionary<string, int>? snapshot;
        private TransactionStatus status = TransactionStatus.Uninitialized;
        public Document Document => doc;
        public TransactionStatus Start()
        {
            doc.Events.Add(kind + ".start");
            snapshot = new(doc.Values);
            return Apply(plan.Starts, TransactionStatus.Started);
        }
        public TransactionStatus Commit()
        {
            doc.Events.Add(kind + ".commit");
            return Apply(plan.Commits, TransactionStatus.Committed);
        }
        public TransactionStatus RollBack()
        {
            doc.Events.Add(kind + ".rollback");
            return Apply(plan.Rollbacks, TransactionStatus.RolledBack);
        }
        public TransactionStatus GetStatus() => status;
        public void Dispose() => doc.Events.Add(kind + ".dispose");
        private TransactionStatus Apply(Queue<Outcome> planned, TransactionStatus normal)
        {
            var outcome = planned.Count > 0 ? planned.Dequeue() : Outcome.As(normal);
            status = outcome.Final;
            if (status == TransactionStatus.RolledBack && snapshot != null)
            {
                doc.Values.Clear();
                foreach (var item in snapshot) doc.Values[item.Key] = item.Value;
            }
            return outcome.Returned;
        }
    }
    public sealed class Transaction(Document doc, string name) : Scope(doc, "transaction", doc.TransactionPlan)
    {
        public string Name { get; } = name;
    }
    public sealed class SubTransaction(Document doc) : Scope(doc, "step", doc.SubtransactionPlan);
    public sealed class TransactionGroup(Document doc, string name) : Scope(doc, "group", doc.GroupPlan)
    {
        public string Name { get; } = name;
    }
}

namespace Autodesk.Revit.Exceptions
{
    public sealed class RegenerationFailedException(string message) : Exception(message);
}

namespace RevitBridge.Tools
{
    using Autodesk.Revit.DB;
    internal static class JsonArgs
    {
        public static bool GetBool(JsonElement args, string name, bool fallback) =>
            args.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
    }
    internal sealed class FailureGuard(Document document)
    {
        public IReadOnlyList<string> Warnings => document.Warnings;
        public static FailureGuard Attach(Transaction transaction)
        {
            if (transaction.GetStatus() != TransactionStatus.Started)
                throw new InvalidOperationException("Failure handling attached before transaction start.");
            transaction.Document.Events.Add("failures.attach");
            return new(transaction.Document);
        }
        public string DescribeErrors() => document.Errors.Count == 0 ? "" : " Errors: " + string.Join("; ", document.Errors);
        public static string RollBackAndDescribe(Transaction transaction)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            return transaction.GetStatus() == TransactionStatus.RolledBack
                ? "Transaction rollback confirmed."
                : $"Transaction status is {transaction.GetStatus()}; rollback is not confirmed.";
        }
    }
}
