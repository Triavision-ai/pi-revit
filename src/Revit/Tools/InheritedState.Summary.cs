namespace RevitBridge.Tools
{
    /// <summary>The Revit-free part of InheritedState: limits, the facts record and the summary. Tested offline.</summary>
    internal static partial class InheritedState
    {
        public const int ListCap = 30;
        public const int SampleCap = 10;
        public static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(4);

        public const string ViewCheck = "Inherited from the source view. Compare this state with the request: show anything the request asks to show, or disclose what stays hidden, and report what was inherited.";
        public const string NewViewCheck = "A new view starts with this visibility state; a duplicate inherits its source's state. Compare it with the request before reporting the view as complete.";
        public const string ElementCheck = "Carried over from the source object. Check these values against the request; a copied Mark or Comment usually needs a new value.";

        /// <summary>Facts read from one view. Lists hold category identities as "OST_Name|Localized name".</summary>
        internal sealed record ViewFacts(
            long? SourceId, long? TemplateId, string? TemplateName,
            IReadOnlyList<string> HiddenCategories, int HiddenElementCount, IReadOnlyList<long> HiddenElementSample,
            int ElementOverrideCount, bool ElementScanComplete, int CategoryOverrideCount,
            IReadOnlyList<(long Id, string Name, bool Visible)> Filters, bool TemporaryHideIsolate,
            string? DisplayStyle, string? DetailLevel, bool CropActive, bool? SectionBoxActive, bool? Perspective);

        /// <summary>Pure summary of view facts: only non-empty sections, capped lists, and the check the caller must make.</summary>
        public static Dictionary<string, object?> Summarize(ViewFacts facts, string check)
        {
            var state = new Dictionary<string, object?>();
            if (facts.SourceId is long source) state["derived_from"] = source;
            if (facts.TemplateId is long template && template > 0) state["template"] = new Dictionary<string, object?> { ["id"] = template, ["name"] = facts.TemplateName };
            if (facts.HiddenCategories.Count > 0)
                state["hidden_categories"] = new Dictionary<string, object?>
                {
                    ["count"] = facts.HiddenCategories.Count,
                    ["categories"] = facts.HiddenCategories.Take(ListCap).ToList(),
                    ["truncated"] = facts.HiddenCategories.Count > ListCap,
                };
            if (facts.HiddenElementCount > 0 || !facts.ElementScanComplete)
                state["hidden_elements"] = new Dictionary<string, object?>
                {
                    ["count"] = facts.HiddenElementCount,
                    ["sample_ids"] = facts.HiddenElementSample.Take(SampleCap).ToList(),
                    ["complete"] = facts.ElementScanComplete,
                };
            if (facts.ElementOverrideCount > 0) state["element_overrides"] = facts.ElementOverrideCount;
            if (facts.CategoryOverrideCount > 0) state["category_overrides"] = facts.CategoryOverrideCount;
            if (facts.Filters.Count > 0)
                state["filters"] = facts.Filters.Take(ListCap).Select(f => new Dictionary<string, object?> { ["id"] = f.Id, ["name"] = f.Name, ["visible"] = f.Visible }).ToList();
            if (facts.TemporaryHideIsolate) state["temporary_hide_isolate"] = true;
            var display = new Dictionary<string, object?> { ["display_style"] = facts.DisplayStyle, ["detail_level"] = facts.DetailLevel, ["crop_box_active"] = facts.CropActive };
            if (facts.SectionBoxActive is bool box) display["section_box_active"] = box;
            if (facts.Perspective is bool perspective) display["perspective"] = perspective;
            state["display"] = display;
            if (!facts.ElementScanComplete)
                state["scan_note"] = $"The hidden-element scan stopped after {ScanBudget.TotalSeconds:0} s; counts are lower bounds.";
            state["check"] = check;
            return state;
        }
    }
}
