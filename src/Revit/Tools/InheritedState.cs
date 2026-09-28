using System.Diagnostics;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Shared report of the state an object carries when it is derived from an existing one
    /// (inv:derived-state-reported). A duplicated view keeps its source's hidden categories and
    /// elements, filters, overrides and template; a copied element keeps its instance values; a
    /// retyped element keeps its per-view graphics. Any tool that duplicates, copies, mirrors or
    /// retypes reports this in its result, so the caller can compare it with the request instead
    /// of relying on an image. The gate in scripts/check-tool-documentation.mjs makes every tool
    /// that creates from an existing object use this class. Reading and summarizing are separate:
    /// Summarize is pure and tested offline; the Read* methods only collect facts from Revit.
    /// </summary>
    internal static partial class InheritedState
    {
        /// <summary>Inherited state of a view derived from source (null when unknown), for a result.</summary>
        public static Dictionary<string, object?> OfView(View view, long? sourceId, string check = ViewCheck)
            => Summarize(ReadView(view, sourceId), check);

        /// <summary>Collect facts from Revit. The element scan is bounded by ScanBudget.</summary>
        public static ViewFacts ReadView(View view, long? sourceId)
        {
            var doc = view.Document;
            var hiddenCategories = new List<string>();
            int categoryOverrides = 0;
            foreach (Category category in doc.Settings.Categories)
            {
                try
                {
                    if (view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id))
                        hiddenCategories.Add($"{category.BuiltInCategory}|{category.Name}");
                    if (view.AreGraphicsOverridesAllowed() && view.IsCategoryOverridable(category.Id) && IsNonDefault(view.GetCategoryOverrides(category.Id)))
                        categoryOverrides++;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            }

            int hidden = 0, overridden = 0;
            var sample = new List<long>();
            bool complete = true;
            var clock = Stopwatch.StartNew();
            var candidates = new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent()
                .Concat(new FilteredElementCollector(doc).OwnedByView(view.Id));
            foreach (var element in candidates)
            {
                if (clock.Elapsed > ScanBudget) { complete = false; break; }
                try
                {
                    if (element.CanBeHidden(view) && element.IsHidden(view))
                    {
                        hidden++;
                        if (sample.Count < SampleCap) sample.Add(element.Id.Value);
                    }
                    if (view.AreGraphicsOverridesAllowed() && IsNonDefault(view.GetElementOverrides(element.Id))) overridden++;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }

            var filters = new List<(long, string, bool)>();
            try
            {
                foreach (var id in view.GetFilters())
                    filters.Add((id.Value, doc.GetElement(id)?.Name ?? "", view.GetFilterVisibility(id)));
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }

            var template = view.ViewTemplateId;
            var view3D = view as View3D;
            return new ViewFacts(sourceId, template.Value > 0 ? template.Value : null, template.Value > 0 ? doc.GetElement(template)?.Name : null,
                hiddenCategories, hidden, sample, overridden, complete, categoryOverrides, filters, Safe(() => view.IsTemporaryHideIsolateActive()),
                Safe(() => view.DisplayStyle.ToString()), Safe(() => view.DetailLevel.ToString()), Safe(() => view.CropBoxActive),
                view3D == null ? null : Safe(() => view3D.IsSectionBoxActive), view3D?.IsPerspective);
        }

        /// <summary>Some view kinds (schedules, sheets) reject graphics properties; report them as unset.</summary>
        private static T? Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return default; }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { return default; }
        }

        /// <summary>True when an override changes anything from the view's normal graphics.</summary>
        public static bool IsNonDefault(OverrideGraphicSettings o) =>
            o.Halftone || o.Transparency != 0 || o.DetailLevel != ViewDetailLevel.Undefined
            || o.ProjectionLineColor.IsValid || o.ProjectionLineWeight != OverrideGraphicSettings.InvalidPenNumber
            || o.CutLineColor.IsValid || o.CutLineWeight != OverrideGraphicSettings.InvalidPenNumber
            || o.SurfaceForegroundPatternId != ElementId.InvalidElementId || o.CutForegroundPatternId != ElementId.InvalidElementId
            || !o.IsSurfaceForegroundPatternVisible || !o.IsCutForegroundPatternVisible;

        /// <summary>
        /// Inherited state of an element derived from source: its special traits, identity-like
        /// values that were copied (Mark, Comments), and views that hide or override it (bounded).
        /// Per-view hiding and overrides are keyed by element ID, so a copy (new ID) never inherits
        /// them; pass viewGraphics only when the element keeps its ID, as a retyped element does.
        /// Views get the view report instead. Returns null when nothing was carried over.
        /// </summary>
        public static Dictionary<string, object?>? OfElement(Element derived, long? sourceId, bool viewGraphics = false)
        {
            if (derived is View view) return OfView(view, sourceId);
            var state = new Dictionary<string, object?>();
            if (sourceId is long source && source != derived.Id.Value) state["derived_from"] = source;
            if (ElementTraits.For(derived) is { } traits) state["traits"] = traits;
            var carried = new Dictionary<string, object?>();
            foreach (var (key, parameter) in new[] { ("mark", BuiltInParameter.ALL_MODEL_MARK), ("comments", BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) })
                if (derived.get_Parameter(parameter)?.AsString() is { Length: > 0 } value) carried[key] = value;
            if (carried.Count > 0) state["carried_values"] = carried;
            if (viewGraphics && ViewGraphicsOf(derived) is { } graphics) state["view_graphics"] = graphics;
            if (state.Count == 0 || (state.Count == 1 && state.ContainsKey("derived_from"))) return null;
            state["check"] = ElementCheck;
            return state;
        }

        /// <summary>Views that hide or override this element, within ScanBudget.</summary>
        private static Dictionary<string, object?>? ViewGraphicsOf(Element element)
        {
            var clock = Stopwatch.StartNew();
            var hiddenIn = new List<long>();
            int overriddenIn = 0;
            bool complete = true;
            foreach (var view in new FilteredElementCollector(element.Document).OfClass(typeof(View)).Cast<View>())
            {
                if (clock.Elapsed > ScanBudget) { complete = false; break; }
                if (view.IsTemplate) continue;
                try
                {
                    if (element.CanBeHidden(view) && element.IsHidden(view)) hiddenIn.Add(view.Id.Value);
                    if (view.AreGraphicsOverridesAllowed() && IsNonDefault(view.GetElementOverrides(element.Id))) overriddenIn++;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
            if (hiddenIn.Count == 0 && overriddenIn == 0 && complete) return null;
            return new Dictionary<string, object?>
            {
                ["hidden_in_views"] = hiddenIn.Count, ["hidden_in_view_ids"] = hiddenIn.Take(SampleCap).ToList(),
                ["overridden_in_views"] = overriddenIn, ["complete"] = complete,
            };
        }
    }
}
