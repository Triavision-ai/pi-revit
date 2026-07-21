using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Activates a view or sheet in the Revit UI — the API equivalent of double-clicking
    /// it in the Project Browser. Uses UIDocument.RequestViewChange, which is explicitly
    /// permitted from an ExternalEvent callback (where all bridge tools run) as long as no
    /// transaction is open; the tool opens none. The activation is asynchronous by design:
    /// Revit performs it the moment control returns from the bridge call.
    /// </summary>
    internal sealed class OpenView : ITool
    {
        public string Name => "open_view";
        public string Label => "Open View";
        public string Description => "Activate a view or sheet in the Revit UI, like double-clicking it in the Project Browser. Identify the target by view_id (an id from get_elements category Views/Sheets or from view/sheet creation) or by name (exact view name, sheet number like 'A-101', or sheet number - name; case-insensitive). Activation is queued and completes the instant this call returns control to Revit — a capture_view immediately after may still show the previous active view. View templates and internal views cannot be opened.";
        public bool Write => false;

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                view_id = new { type = "integer", description = "Id of the view or sheet to activate." },
                name = new { type = "string", description = "Exact view name, sheet number, or sheet 'number - name' (case-insensitive). Used when view_id is absent." },
            },
        };

        public string? PromptSnippet => "Activate a Revit view or sheet in the UI by id or name (like double-clicking it in the Project Browser).";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use open_view to put a view or sheet on the user's screen (e.g. after creating a sheet); activation completes right after the call, so capture_view in the SAME call batch may still show the old view.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();
            var uiapp = context.UIApplication ?? throw new NoActiveDocumentException();
            var uidoc = uiapp.ActiveUIDocument ?? throw new NoActiveDocumentException();

            long? viewId = JsonArgs.GetLong(args, "view_id");
            string? name = JsonArgs.GetString(args, "name");
            if (viewId is null && string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Pass view_id or name to identify the view or sheet to open.");

            View view = viewId is { } id ? ResolveById(doc, id) : ResolveByName(doc, name!.Trim());

            try
            {
                uidoc.RequestViewChange(view);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ArgumentException($"View '{view.Name}' (id {view.Id.Value}) cannot be activated: {ex.Message}");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Revit refused to queue the view change to '{view.Name}': {ex.Message} "
                    + "The document may be mid-edit; retry after the current operation finishes.");
            }

            string kind = view is ViewSheet sheet ? $"sheet {sheet.SheetNumber}" : view.ViewType.ToString();
            return new ToolOutput(new
            {
                requestedViewId = view.Id.Value,
                viewName = view.Name,
                viewType = view.ViewType.ToString(),
            }, $"Queued activation of {kind} '{view.Name}' (id {view.Id.Value}); it opens as soon as Revit regains control.");
        }

        private static View ResolveById(Document doc, long id)
        {
            var view = doc.GetElement(new ElementId(id)) as View
                ?? throw new ArgumentException($"Element {id} is not a view or sheet (or does not exist). Find view ids with get_elements, category 'Views' or 'Sheets'.");
            if (view.IsTemplate)
                throw new ArgumentException($"View '{view.Name}' (id {id}) is a view template; templates cannot be opened.");
            return view;
        }

        private static View ResolveByName(Document doc, string name)
        {
            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate)
                .Where(view =>
                    string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase)
                    || (view is ViewSheet sheet
                        && (string.Equals(sheet.SheetNumber, name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals($"{sheet.SheetNumber} - {sheet.Name}", name, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (candidates.Count == 1)
                return candidates[0];
            if (candidates.Count == 0)
                throw new ArgumentException($"No view or sheet named '{name}'. Find the exact name or sheet number with get_elements, category 'Views' or 'Sheets'.");
            string list = string.Join("; ", candidates.Take(8).Select(view => $"'{view.Name}' (id {view.Id.Value}, {view.ViewType})"));
            throw new ArgumentException($"'{name}' matches {candidates.Count} views: {list}. Pass view_id to disambiguate.");
        }
    }
}
