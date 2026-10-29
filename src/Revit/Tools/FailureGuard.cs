using Autodesk.Revit.DB;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Deterministic commit-time failure handling for tool-owned transactions. Without a
    /// preprocessor, Revit resolves commit failures interactively: warnings pop the
    /// transient toast (and spam the journal), and error-severity failures block the
    /// Revit API thread behind the modal resolution dialog until a human clicks — past
    /// the bridge's call budget. Attach() deletes warnings (recorded for the tool
    /// result) and turns errors into a rollback, so a headless caller always gets an
    /// answer instead of a hang.
    /// </summary>
    internal sealed class FailureGuard : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();
        private const int MaxDiagnosticFailures = 8;
        private const int MaxDiagnosticIds = 12;
        // Diagnostic JSON is text, never embedded into HTML. Keep localized
        // descriptions readable rather than expanding every character to an escape.
        private static readonly JsonSerializerOptions DiagnosticJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        internal sealed record FailureDiagnostic(string? failure_definition_id, string severity, string description,
            IReadOnlyList<long> failing_element_ids, IReadOnlyList<long> additional_element_ids,
            bool element_ids_truncated, bool metadata_incomplete);
        private readonly List<FailureDiagnostic> _diagnostics = new();
        public bool DiagnosticsTruncated { get; private set; }

        /// <summary>Bounded raw failure attribution, separate from widened IDs used for batch isolation.</summary>
        public object Diagnostics(string transactionStatus) => new
        {
            transaction_status = transactionStatus,
            failures = _diagnostics.ToArray(),
            failures_truncated = DiagnosticsTruncated,
        };
        public bool HasDiagnostics => _diagnostics.Count > 0 || DiagnosticsTruncated;

        public static string TransactionStatusText(Transaction transaction)
        {
            try { return transaction.GetStatus().ToString(); }
            catch { return "unknown"; }
        }
        /// <summary>Elements the error-severity failures name, widened to their groups and group
        /// members, so a batch can tell which of its updates Revit rejected at commit.</summary>
        public HashSet<long> ErrorElementIds { get; } = new();

        public static FailureGuard Attach(Transaction transaction, FailureGuard? sharedGuard = null)
        {
            if (transaction.GetStatus() != TransactionStatus.Started)
                throw new InvalidOperationException("FailureGuard must be attached after the transaction starts; Start resets failure handling options.");
            var guard = sharedGuard ?? new FailureGuard();
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(guard);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            return guard;
        }

        /// <summary>Best-effort cleanup that preserves the original failure and never
        /// claims a rollback unless Revit confirms its final transaction status.</summary>
        public static string RollBackAndDescribe(Transaction transaction)
        {
            try
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
                var status = transaction.GetStatus();
                return status == TransactionStatus.RolledBack
                    ? "The transaction was rolled back; no changes from this transaction were saved."
                    : $"Transaction status is {status}; rollback is not confirmed. Inspect Revit before retrying.";
            }
            catch (Exception ex)
            {
                return $"Rollback could not be confirmed ({ex.GetType().Name}: {ex.Message}). Inspect Revit before retrying.";
            }
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool hasError = false;
            foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
            {
                string description;
                bool descriptionUnavailable = false;
                try
                {
                    description = failure.GetDescriptionText();
                }
                catch
                {
                    description = "(failure without description)";
                    descriptionUnavailable = true;
                }

                var severity = failure.GetSeverity();
                RecordDiagnostic(failure, severity, description, descriptionUnavailable);
                if (severity == FailureSeverity.Warning)
                {
                    Warnings.Add(description);
                    accessor.DeleteWarning(failure);
                }
                else
                {
                    hasError = true;
                    Errors.Add(description);
                    RecordFailingElements(accessor.GetDocument(), failure);
                }
            }
            if (hasError)
            {
                // EditScope.Commit accepts a preprocessor without exposing its transaction.
                // Set this here as well as Attach so scope/event failures roll back silently.
                var options = accessor.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                accessor.SetFailureHandlingOptions(options);
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }

        private void RecordDiagnostic(FailureMessageAccessor failure, FailureSeverity severity, string description, bool descriptionUnavailable)
        {
            if (_diagnostics.Count >= MaxDiagnosticFailures) { DiagnosticsTruncated = true; return; }
            bool incomplete = descriptionUnavailable, truncated = false;
            string? definition = null;
            try { definition = failure.GetFailureDefinitionId().Guid.ToString("D"); }
            catch { incomplete = true; }
            long[] ReadIds(Func<ICollection<ElementId>> read)
            {
                try
                {
                    var ids = read().Select(id => id.Value).Distinct().OrderBy(id => id).Take(MaxDiagnosticIds + 1).ToArray();
                    if (ids.Length > MaxDiagnosticIds) truncated = true;
                    return ids.Take(MaxDiagnosticIds).ToArray();
                }
                catch { incomplete = true; return Array.Empty<long>(); }
            }
            var failing = ReadIds(failure.GetFailingElementIds);
            var additional = ReadIds(failure.GetAdditionalElementIds);
            string boundedDescription = description.Length <= 320 ? description : description[..320] + "…";
            if (description.Length > 320) incomplete = true;
            _diagnostics.Add(new(definition, severity.ToString(), boundedDescription, failing, additional, truncated, incomplete));
        }

        private void RecordFailingElements(Document document, FailureMessageAccessor failure)
        {
            try
            {
                foreach (var id in failure.GetFailingElementIds().Concat(failure.GetAdditionalElementIds()))
                {
                    ErrorElementIds.Add(id.Value);
                    var element = document.GetElement(id);
                    // Group errors may name the group instance or its edited member: widen to both.
                    if (element is Group group)
                        foreach (var member in group.GetMemberIds()) ErrorElementIds.Add(member.Value);
                    if (element != null && element.GroupId != ElementId.InvalidElementId)
                        ErrorElementIds.Add(element.GroupId.Value);
                }
            }
            catch
            {
                // Attribution is best effort; the failure text is still reported.
            }
        }

        /// <summary>Formats the recorded errors for a commit-failure message.</summary>
        public string ErrorSummary()
        {
            string summary = string.Join("; ", Errors.Take(5).Select(text => text.Length <= 320 ? text : text[..320] + "…"));
            return summary.Length <= 640 ? summary : summary[..640] + "…";
        }

        public string DescribeErrors(string transactionStatus = "unknown")
            => HasDiagnostics
                ? (Errors.Count > 0 ? $" Revit failure(s): {ErrorSummary()}." : string.Empty)
                    + $" Failure diagnostics: {JsonSerializer.Serialize(Diagnostics(transactionStatus), DiagnosticJson)}"
                : string.Empty;
    }
}
