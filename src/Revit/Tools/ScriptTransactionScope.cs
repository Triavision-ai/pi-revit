using System.Text.Json;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace RevitBridge.Tools;

/// <summary>Owns the script's transaction boundary, never its script-owned child scopes.</summary>
internal sealed class ScriptTransactionScope : IDisposable
{
    private readonly Document _document;
    private readonly RevitApplication _application;
    private readonly Transaction? _transaction;
    private readonly TransactionGroup? _group;
    private bool _rollbackObserved;
    public FailureGuard Failures { get; } = new();
    public string Mode { get; }

    internal static string ReadMode(JsonElement args)
    {
        if (!args.TryGetProperty("transaction_mode", out var mode)) return "automatic";
        if (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("automatic" or "group"))
            throw new ArgumentException("transaction_mode must be 'automatic' or 'group'.");
        return mode.GetString()!;
    }

    public ScriptTransactionScope(Document document, RevitApplication application, string mode)
    {
        _document = document;
        _application = application;
        Mode = mode;
        try
        {
            if (mode == "group")
            {
                _group = new TransactionGroup(document, "execute_csharp");
                if (_group.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Unable to start the execute_csharp transaction group.");
                _application.FailuresProcessing += OnFailures;
                _application.DocumentChanged += OnChanged;
            }
            else
            {
                _transaction = new Transaction(document, "execute_csharp");
                if (_transaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Unable to start the execute_csharp transaction.");
                FailureGuard.Attach(_transaction, Failures);
            }
        }
        catch { Dispose(); throw; }
    }

    private void OnFailures(object? sender, FailuresProcessingEventArgs args)
    {
        var accessor = args.GetFailuresAccessor();
        if (!accessor.GetDocument().Equals(_document)) return;
        var result = Failures.PreprocessFailures(accessor);
        // Never downgrade a rollback requested by another handler to Continue.
        if (result != FailureProcessingResult.Continue) args.SetProcessingResult(result);
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs args)
    {
        if (args.GetDocument().Equals(_document) && args.Operation.ToString() != "TransactionCommitted")
            _rollbackObserved = true;
    }

    public string Status
    {
        get
        {
            try { return (_group?.GetStatus() ?? _transaction!.GetStatus()).ToString(); }
            catch { return "unknown"; }
        }
    }

    public void Commit()
    {
        if (_group != null)
        {
            if (_document.IsModifiable)
                throw new InvalidOperationException("The script left a transaction open. Close every transaction and edit scope in using blocks before returning.");
            if (Failures.Errors.Count > 0 || _rollbackObserved)
                throw new InvalidOperationException("A child transaction or edit scope failed or rolled back. Group mode rejects partial rollback; the entire group will be rolled back.");
        }
        var status = _group != null ? _group.Assimilate() : _transaction!.Commit();
        if (status != TransactionStatus.Committed || Status != "Committed")
            throw new InvalidOperationException($"The execute_csharp {Mode} commit returned {status}; current status is {Status}.");
    }

    public string RollBackAndDescribe()
    {
        if (_transaction != null) return FailureGuard.RollBackAndDescribe(_transaction);
        try
        {
            if (_group!.GetStatus() == TransactionStatus.Started) _group.RollBack();
            return _group.GetStatus() == TransactionStatus.RolledBack
                ? "The transaction group was rolled back; no model changes from this group were retained in the target document."
                : $"Transaction group status is {Status}; rollback is not confirmed. Inspect Revit before retrying.";
        }
        catch (Exception ex)
        {
            return $"Transaction group rollback could not be confirmed ({ex.GetType().Name}: {ex.Message}). Close leaked script scopes and inspect Revit before retrying.";
        }
    }

    public void Dispose()
    {
        try { _application.FailuresProcessing -= OnFailures; } catch { }
        try { _application.DocumentChanged -= OnChanged; } catch { }
        // Explicit rollback above supplies the cleanup evidence. Dispose must not replace
        // the original exception or upgrade an unconfirmed rollback to a confirmed one.
        try { _transaction?.Dispose(); } catch { }
        try { _group?.Dispose(); } catch { }
    }
}
