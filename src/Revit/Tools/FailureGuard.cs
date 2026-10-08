using Autodesk.Revit.DB;

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
        /// <summary>Elements the error-severity failures name, widened to their groups and group
        /// members, so a batch can tell which of its updates Revit rejected at commit.</summary>
        public HashSet<long> ErrorElementIds { get; } = new();

        public static FailureGuard Attach(Transaction transaction)
        {
            if (transaction.GetStatus() != TransactionStatus.Started)
                throw new InvalidOperationException("FailureGuard must be attached after the transaction starts; Start resets failure handling options.");
            var guard = new FailureGuard();
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
                try
                {
                    description = failure.GetDescriptionText();
                }
                catch
                {
                    description = "(failure without description)";
                }

                if (failure.GetSeverity() == FailureSeverity.Warning)
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
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
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
        public string DescribeErrors()
            => Errors.Count > 0 ? $" Revit failure(s): {string.Join("; ", Errors)}." : string.Empty;
    }
}
