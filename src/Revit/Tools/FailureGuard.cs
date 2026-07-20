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

        public static FailureGuard Attach(Transaction transaction)
        {
            var guard = new FailureGuard();
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(guard);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            return guard;
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
                }
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }

        /// <summary>Formats the recorded errors for a commit-failure message.</summary>
        public string DescribeErrors()
            => Errors.Count > 0 ? $" Revit failure(s): {string.Join("; ", Errors)}." : string.Empty;
    }
}
