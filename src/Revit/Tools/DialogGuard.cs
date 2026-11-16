using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Auto-answers any modal Revit dialog raised while a tool runs, so a popup cannot hang the
    /// Revit thread until someone clicks. The bridge wraps every document tool in one guard
    /// (BridgeServer.RunToolAsync); the answers follow DialogPolicy. Dismissed dialog ids and
    /// the answer given are reported in the result's suppressedDialogs, or in the error message
    /// when the tool fails.
    /// </summary>
    internal sealed class DialogGuard : IDisposable
    {
        private readonly UIApplication _uiapp;
        private readonly List<string> _suppressed = new();

        /// <summary>One line per dialog answered so far, e.g. "TaskDialog_X (answered Cancel)".</summary>
        public IReadOnlyList<string> Suppressed => _suppressed;

        public DialogGuard(UIApplication uiapp)
        {
            _uiapp = uiapp;
            _uiapp.DialogBoxShowing += OnDialogBoxShowing;
        }

        private void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
        {
            string id = string.IsNullOrEmpty(e.DialogId) ? e.GetType().Name : e.DialogId;
            _suppressed.Add(DialogPolicy.Answer(id, e.OverrideResult));
        }

        /// <summary>
        /// Merges suppressedDialogs into an object payload when any dialog was answered; other
        /// payload shapes, and results without dialogs, are returned unchanged.
        /// </summary>
        public static object? Attach(object? output, IReadOnlyList<string> suppressed)
        {
            if (suppressed.Count == 0) return output;
            try
            {
                object? payload = output is ToolOutput tool ? tool.Payload : output;
                if (JsonSerializer.SerializeToNode(payload) is not JsonObject json) return output;
                json["suppressedDialogs"] = JsonSerializer.SerializeToNode(suppressed);
                return output is ToolOutput original ? original with { Payload = json } : json;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return output;
            }
        }

        public void Dispose()
        {
            try
            {
                _uiapp.DialogBoxShowing -= OnDialogBoxShowing;
            }
            catch
            {
                // Unsubscribe must never mask the tool result.
            }
        }
    }
}
