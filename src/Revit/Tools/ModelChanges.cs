using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Records what one model-changing tool call did to the document (inv:model-changes-reported)
    /// and merges a bounded report into its result as model_changes. The bridge dispatcher wraps
    /// every tool that can write or has model effects, including execute_csharp, so present and
    /// future tools report created, modified and deleted objects without tool-specific code. New
    /// views are reported with their visibility state through InheritedState, which covers views
    /// duplicated in custom code. The Pi extension builds its per-request ledger of objects created
    /// by the agent from this report.
    /// </summary>
    internal sealed class ModelChangeRecorder : IDisposable
    {
        public const int ItemCap = 20;
        public const int NewViewStateCap = 3;
        private readonly Autodesk.Revit.ApplicationServices.Application _application;
        private readonly Document _document;
        private readonly ChangeSet _changes = new();
        // Family types and parameters are not elements, so DocumentChanged cannot name them;
        // a family document is compared before and after the call instead.
        private readonly FamilySnapshot? _familyBefore;

        private ModelChangeRecorder(Autodesk.Revit.ApplicationServices.Application application, Document document)
        {
            _application = application; _document = document;
            _familyBefore = FamilySnapshot.Of(document);
            _application.DocumentChanged += OnChanged;
        }

        /// <summary>A recorder for tools that can change the model; null for everything else.</summary>
        public static ModelChangeRecorder? For(ITool tool, Autodesk.Revit.ApplicationServices.Application application, Document document)
            => tool.Write || tool.Effects.Contains("model") ? new ModelChangeRecorder(application, document) : null;

        private void OnChanged(object? sender, DocumentChangedEventArgs e)
        {
            if (!e.GetDocument().Equals(_document)) return;
            _changes.Record(e.Operation.ToString(), Ids(e.GetAddedElementIds()), Ids(e.GetModifiedElementIds()), Ids(e.GetDeletedElementIds()));
        }

        private static IEnumerable<long> Ids(ICollection<ElementId> ids) => ids.Select(id => id.Value).ToList();

        public void Dispose() => _application.DocumentChanged -= OnChanged;

        /// <summary>
        /// Merge model_changes into an object payload; other payload shapes are returned unchanged.
        /// The tool has already run: a reporting failure must never turn its result into an error.
        /// </summary>
        public object? Attach(object? output)
        {
            try
            {
                object? payload = output is ToolOutput tool ? tool.Payload : output;
                if (JsonSerializer.SerializeToNode(payload) is not JsonObject json) return output;
                bool describesState = json.ToJsonString().Contains("\"inherited_state\"", StringComparison.Ordinal);
                json["model_changes"] = JsonSerializer.SerializeToNode(Report(!describesState));
                return output is ToolOutput original ? original with { Payload = json } : json;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return output;
            }
        }

        public Dictionary<string, object?> Report(bool includeNewViewState)
        {
            // A read through a write-capable tool (a placement listing, a read-only script) changed
            // nothing: say so in one field instead of empty lists on every call.
            if (!_changes.Observed) return new() { ["observed"] = false };
            var added = _changes.Added.Where(id => _document.GetElement(new ElementId(id)) != null).OrderBy(id => id).ToList();
            var modified = _changes.Modified.Where(id => _document.GetElement(new ElementId(id)) != null).OrderBy(id => id).ToList();
            var deleted = _changes.Deleted.Where(id => _document.GetElement(new ElementId(id)) == null).OrderBy(id => id).ToList();
            var report = new Dictionary<string, object?>
            {
                ["observed"] = _changes.Observed,
                ["added"] = Describe(added),
                ["modified"] = Describe(modified),
                ["deleted"] = new Dictionary<string, object?> { ["count"] = deleted.Count, ["ids"] = deleted.Take(ItemCap).ToList() },
            };
            if (_changes.RolledBack) report["rolled_back"] = true;
            if (_familyBefore != null && FamilySnapshot.Of(_document) is { } familyAfter && FamilySnapshot.Diff(_familyBefore, familyAfter) is { } family)
                report["family"] = family;
            // Revit reports elements it only regenerated (analytical nodes, imports, the project's
            // internal elements) as modified; round-3 agents relayed those as edits.
            if (modified.Count > 0)
                report["modified_note"] = "Modified includes elements Revit updated as a side effect of regeneration. Report as changed only what your edit targeted; reread anything else before claiming it changed.";
            if (includeNewViewState)
            {
                var views = added.Select(id => _document.GetElement(new ElementId(id))).OfType<View>()
                    .Where(view => !view.IsTemplate && view is not ViewSheet && view is not ViewSchedule).Take(NewViewStateCap)
                    .Select(view => new Dictionary<string, object?> { ["id"] = view.Id.Value, ["name"] = view.Name, ["view_type"] = view.ViewType.ToString(),
                        ["state"] = InheritedState.Summarize(InheritedState.ReadView(view, null), InheritedState.NewViewCheck) }).ToList();
                if (views.Count > 0) report["new_views"] = views;
            }
            return report;
        }

        /// <summary>A family document's types (with every value) and parameter names; null in a project.</summary>
        internal sealed record FamilySnapshot(Dictionary<string, string> Types, HashSet<string> Parameters)
        {
            public static FamilySnapshot? Of(Document doc)
            {
                if (!doc.IsFamilyDocument) return null;
                var manager = doc.FamilyManager;
                var parameters = manager.Parameters.Cast<FamilyParameter>().OrderBy(p => p.Definition.Name).ToList();
                string Value(FamilyType type, FamilyParameter parameter)
                {
                    try { return type.HasValue(parameter) ? type.AsValueString(parameter) ?? type.AsString(parameter) ?? "" : ""; }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { return "?"; }
                }
                var types = new Dictionary<string, string>();
                foreach (FamilyType type in manager.Types)
                    types[type.Name] = string.Join("|", parameters.Select(p => p.Definition.Name + "=" + Value(type, p)));
                return new FamilySnapshot(types, parameters.Select(p => p.Definition.Name).ToHashSet());
            }

            /// <summary>Family types and parameters added, removed or changed; null when none changed.</summary>
            public static Dictionary<string, object?>? Diff(FamilySnapshot before, FamilySnapshot after)
            {
                var added = after.Types.Keys.Except(before.Types.Keys).OrderBy(n => n).ToList();
                var removed = before.Types.Keys.Except(after.Types.Keys).OrderBy(n => n).ToList();
                var changed = before.Types.Keys.Where(n => after.Types.TryGetValue(n, out var v) && v != before.Types[n]).OrderBy(n => n).ToList();
                var parametersAdded = after.Parameters.Except(before.Parameters).OrderBy(n => n).ToList();
                var parametersRemoved = before.Parameters.Except(after.Parameters).OrderBy(n => n).ToList();
                if (added.Count + removed.Count + changed.Count + parametersAdded.Count + parametersRemoved.Count == 0) return null;
                return new Dictionary<string, object?>
                {
                    ["types_added"] = added, ["types_removed"] = removed, ["types_changed"] = changed,
                    ["parameters_added"] = parametersAdded, ["parameters_removed"] = parametersRemoved,
                };
            }
        }

        private Dictionary<string, object?> Describe(IReadOnlyList<long> ids) => new()
        {
            ["count"] = ids.Count,
            ["items"] = ids.Take(ItemCap).Select(id =>
            {
                var element = _document.GetElement(new ElementId(id));
                return new Dictionary<string, object?> { ["id"] = id, ["name"] = SafeName(element), ["category"] = element?.Category?.Name, ["class"] = element?.GetType().Name };
            }).ToList(),
        };

        private static string? SafeName(Element? element)
        {
            try { return element?.Name; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
        }
    }
}
