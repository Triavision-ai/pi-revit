using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Selection and view-focus management. Selection changes and zoom are pure UI
    /// state. Temporary isolate is view state (not model data), but the Revit API
    /// only toggles it inside an open transaction, so exactly that branch wraps a
    /// small internal one; the tool itself stays a read tool (Write = false).
    /// </summary>
    internal sealed class ManageSelection : ITool
    {
        public string Name => "manage_selection";
        public string Label => "Manage Selection";
        public string Description => "Manage the Revit selection and view focus. action 'get' returns the currently selected elements (id, name, category, typeName, levelId); 'set'/'add'/'remove' change the selection with element_ids; 'clear' empties it; 'zoom' zooms open views to element_ids (default: the current selection). isolate_in_view=true additionally applies Revit's Temporary Hide/Isolate to the affected elements in the active view — temporary view state, not a model change (Revit needs a brief internal transaction to toggle it); with an empty target set (action 'clear', or 'get' with nothing selected) it resets the temporary isolate instead. Selection and zoom never modify model data.";

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                action = new
                {
                    type = "string",
                    @enum = new[] { "get", "set", "add", "remove", "clear", "zoom" },
                    description = "What to do with the selection. Default get.",
                },
                element_ids = new
                {
                    type = "array",
                    items = new { type = "integer" },
                    description = "Element ids for set/add/remove/zoom. zoom falls back to the current selection when omitted.",
                },
                isolate_in_view = new
                {
                    type = "boolean",
                    description = "Temporarily isolate the affected elements in the active view (temporary view state only; resets the temporary isolate when the target set is empty). Default false.",
                },
            },
            required = Array.Empty<string>(),
        };

        public string? PromptSnippet => "Read or change the Revit selection; zoom to or temporarily isolate elements.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use manage_selection (action 'get') when the user refers to elements they selected in Revit ('what is selected', 'these elements').",
            "To show results in Revit, pass ids from get_elements to manage_selection action 'set' or 'zoom', adding isolate_in_view to focus a crowded view.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var uiDocument = context.UIApplication?.ActiveUIDocument ?? throw new NoActiveDocumentException();
            var doc = uiDocument.Document;

            string action = (JsonArgs.GetString(args, "action") ?? "get").Trim().ToLowerInvariant();
            bool isolateInView = JsonArgs.GetBool(args, "isolate_in_view", false);
            var (requestedCount, validIds, notFound) = ResolveElementIds(doc, args);

            if (action is "set" or "add" or "remove" && requestedCount == 0)
                throw new ArgumentException($"action '{action}' needs element_ids. Use action 'clear' to empty the selection.");
            if (requestedCount > 0 && validIds.Count == 0)
                throw new ArgumentException($"None of the element_ids exist in the document: {string.Join(", ", notFound)}.");

            var payload = new Dictionary<string, object?> { ["action"] = action };
            var compactParts = new List<string>();
            List<ElementId> isolateTargets;

            switch (action)
            {
                case "get":
                {
                    var selectedIds = uiDocument.Selection.GetElementIds().ToList();
                    var rows = selectedIds
                        .Select(id => doc.GetElement(id))
                        .Where(element => element != null)
                        .Select(element => ElementIdentity.Build(doc, element!, ElementIdentity.Fields))
                        .ToList();
                    payload["count"] = rows.Count;
                    payload["elements"] = rows;
                    string sample = rows.Count > 0
                        ? $": {string.Join(", ", rows.Take(5).Select(row => $"'{row["name"]}' ({row["id"]})"))}{(rows.Count > 5 ? $" (+{rows.Count - 5} more)" : string.Empty)}"
                        : string.Empty;
                    compactParts.Add($"{rows.Count} element(s) selected{sample}");
                    isolateTargets = selectedIds;
                    break;
                }
                case "set":
                case "add":
                case "remove":
                {
                    var current = uiDocument.Selection.GetElementIds();
                    List<ElementId> next = action switch
                    {
                        "set" => validIds,
                        "add" => current.Union(validIds).ToList(),
                        _ => current.Except(validIds).ToList(),
                    };
                    uiDocument.Selection.SetElementIds(next);
                    payload["selectedCount"] = next.Count;
                    compactParts.Add(action switch
                    {
                        "set" => $"Selection set to {next.Count} element(s)",
                        "add" => $"Added {next.Count - current.Count} element(s); {next.Count} now selected",
                        _ => $"Removed {current.Count - next.Count} element(s); {next.Count} still selected",
                    });
                    isolateTargets = next;
                    break;
                }
                case "clear":
                {
                    uiDocument.Selection.SetElementIds(new List<ElementId>());
                    payload["selectedCount"] = 0;
                    compactParts.Add("Selection cleared");
                    isolateTargets = new List<ElementId>();
                    break;
                }
                case "zoom":
                {
                    var targets = validIds.Count > 0 ? validIds : uiDocument.Selection.GetElementIds().ToList();
                    if (targets.Count == 0)
                        throw new ArgumentException("action 'zoom' needs element_ids, or a non-empty current selection to zoom to.");
                    uiDocument.ShowElements(targets);
                    payload["shownCount"] = targets.Count;
                    compactParts.Add($"Zoomed open views to {targets.Count} element(s)");
                    isolateTargets = targets;
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown action: {action}. Supported: get, set, add, remove, clear, zoom.");
            }

            if (notFound.Count > 0)
            {
                payload["not_found"] = notFound;
                compactParts.Add($"ids not found: {string.Join(", ", notFound)}");
            }

            if (isolateInView)
            {
                var view = uiDocument.ActiveGraphicalView
                    ?? throw new ArgumentException("isolate_in_view requires an active graphical view in Revit.");
                ApplyTemporaryIsolate(doc, view, isolateTargets);
                payload["viewId"] = view.Id.Value;
                payload["viewName"] = view.Name;
                if (isolateTargets.Count == 0)
                {
                    payload["temporaryIsolateReset"] = true;
                    compactParts.Add($"temporary isolate reset in view '{view.Name}'");
                }
                else
                {
                    payload["isolatedCount"] = isolateTargets.Count;
                    compactParts.Add($"{isolateTargets.Count} element(s) temporarily isolated in view '{view.Name}'");
                }
            }
            else
            {
                payload["activeViewId"] = doc.ActiveView?.Id.Value;
            }

            return new ToolOutput(payload, string.Join("; ", compactParts) + ".");
        }

        private static (int RequestedCount, List<ElementId> Valid, List<long> NotFound) ResolveElementIds(Document doc, JsonElement args)
        {
            bool present = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("element_ids", out var idsElement)
                && idsElement.ValueKind != JsonValueKind.Null;
            if (!present)
                return (0, new List<ElementId>(), new List<long>());

            var requested = JsonArgs.GetLongArray(args, "element_ids");
            var valid = new List<ElementId>(requested.Count);
            var notFound = new List<long>();
            foreach (long id in requested.Distinct())
            {
                var elementId = new ElementId(id);
                if (doc.GetElement(elementId) != null)
                    valid.Add(elementId);
                else
                    notFound.Add(id);
            }
            return (requested.Count, valid, notFound);
        }

        /// <summary>
        /// Temporary hide/isolate is view state, not model data, but the Revit API
        /// only changes it inside an open transaction — so exactly this branch wraps
        /// a small one while the tool stays Write = false.
        /// </summary>
        private static void ApplyTemporaryIsolate(Document doc, View view, ICollection<ElementId> elementIds)
        {
            using var transaction = new Transaction(doc, "manage_selection: temporary isolate");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Unable to start the temporary-isolate transaction.");
            try
            {
                if (elementIds.Count == 0)
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                else
                    view.IsolateElementsTemporary(elementIds);
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("The temporary-isolate transaction failed to commit.");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
                throw new ArgumentException($"The active view '{view.Name}' does not support temporary isolate: {ex.Message}");
            }
            catch
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
                throw;
            }
        }
    }
}
