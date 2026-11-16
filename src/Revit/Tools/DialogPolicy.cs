namespace RevitBridge.Tools
{
    /// <summary>
    /// How a modal Revit dialog raised while a tool runs is answered. Unrecognized dialogs get
    /// the dismissive answer (Cancel, then Close, then No) — on several Revit dialogs OK is the
    /// destructive choice (e.g. "Delete Element(s)"), so confirming blind can silently damage
    /// the model. OK leads only for dialogs known to be safe to confirm, and is otherwise the
    /// last resort: every override attempt failing would leave the dialog up and hang the
    /// Revit thread, which is the one outcome the guard exists to prevent.
    ///
    /// The policy has no Revit dependency, so it is tested without Revit; DialogGuard connects
    /// it to Revit's DialogBoxShowing event.
    /// </summary>
    internal static class DialogPolicy
    {
        internal const int IDOK = 1;
        internal const int IDCANCEL = 2;
        internal const int IDNO = 7;
        internal const int IDCLOSE = 8;

        /// <summary>Dialogs where confirming is the benign answer and cancelling would
        /// abort the operation the tool deliberately started.</summary>
        private static readonly HashSet<string> ConfirmSafeDialogIds = new(StringComparer.OrdinalIgnoreCase)
        {
            // "Export with temporary hide/isolate": OK exports the view as displayed.
            "TaskDialog_Really_Print_Or_Export_Temp_View_Modes",
        };

        private static readonly int[] ConfirmFirst = { IDOK, IDCANCEL, IDCLOSE };
        private static readonly int[] DismissFirst = { IDCANCEL, IDCLOSE, IDNO, IDOK };

        /// <summary>The answers to try for a dialog, in order.</summary>
        internal static IReadOnlyList<int> AnswersFor(string dialogId)
            => ConfirmSafeDialogIds.Contains(dialogId) ? ConfirmFirst : DismissFirst;

        internal static string AnswerName(int answer) => answer switch
        {
            IDOK => "OK",
            IDCANCEL => "Cancel",
            IDNO => "No",
            IDCLOSE => "Close",
            _ => answer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Answers one dialog: offers each answer to <paramref name="tryOverride"/> in order until
        /// one is accepted, and returns the line reported in suppressedDialogs. Some dialogs
        /// reject specific overrides (by returning false or by throwing); the next answer is tried.
        /// </summary>
        internal static string Answer(string dialogId, Func<int, bool> tryOverride)
        {
            foreach (int answer in AnswersFor(dialogId))
            {
                try
                {
                    if (tryOverride(answer))
                        return $"{dialogId} (answered {AnswerName(answer)})";
                }
                catch
                {
                    // This dialog rejects this override; try the next answer.
                }
            }
            return $"{dialogId} (override rejected — dialog may need manual dismissal)";
        }

        /// <summary>Sentence appended to an error message; empty when nothing was dismissed.</summary>
        internal static string Describe(IReadOnlyList<string>? suppressed)
            => suppressed is { Count: > 0 } ? $" Auto-dismissed dialog(s) during the run: {string.Join(", ", suppressed)}." : string.Empty;
    }
}
