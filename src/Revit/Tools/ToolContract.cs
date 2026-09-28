namespace RevitBridge
{
    /// <summary>
    /// A declared capability boundary: something the tool deliberately does not cover and
    /// the route to use instead. Alternative kinds: "tool" (Ref names another public tool),
    /// "api" (Ref names Revit API members to verify with search_api_docs before custom
    /// execution), "user" (needs a user action or decision), or "revit_unsupported" (the
    /// Revit API itself does not offer it; Ref states the evidence). A limit never ends at
    /// "unsupported" without a route, so agents do not report a false impossibility.
    /// Kept in its own file so every project that compiles tool sources shares one type.
    /// </summary>
    internal sealed record ToolLimit(string What, string Alternative, string? Ref = null)
    {
        public static readonly IReadOnlyList<string> AlternativeKinds = new[] { "tool", "api", "user", "revit_unsupported" };
    }

    /// <summary>Allowed ITool.Verification values; see ITool.Verification.</summary>
    internal static class ToolVerification
    {
        public static readonly IReadOnlyList<string> Kinds = new[] { "reread", "capture", "inspect_output", "none" };
    }

    /// <summary>
    /// Document kinds a tool works in (inv:document-kind-declared). Every bridge tool declares
    /// ITool.DocumentKinds; the dispatcher refuses any other kind before the tool runs, with the
    /// route to use instead. Revit-free, so the refusal is tested offline.
    /// </summary>
    internal static class DocumentKind
    {
        public const string Project = "project";
        public const string Family = "family";
        public static readonly IReadOnlyList<string> Both = new[] { Project, Family };
        public static readonly IReadOnlyList<string> ProjectOnly = new[] { Project };

        /// <summary>Revit API route for family types, parameters and formulas; also a declared limit alternative.</summary>
        public const string FamilyApi = "FamilyManager.Types; FamilyManager.NewType; FamilyManager.Set; FamilyManager.AddParameter; FamilyManager.SetFormula";

        /// <summary>Why a tool cannot run in the active document, with the alternative; null when it can.</summary>
        public static string? Refusal(string tool, IReadOnlyList<string> supported, string active, string title)
        {
            if (supported.Count == 0 || supported.Contains(active)) return null;
            string route = active == Family
                ? $"A family document has no sheets, schedules, rooms, links or project coordinates. For family types, parameters and formulas use the Revit API: verify '{FamilyApi}' with search_api_docs, then use execute_csharp."
                : "Open the family for editing (Document.EditFamily) or ask the user to open the family document.";
            return $"{tool} works in {string.Join(" and ", supported)} documents; the active document '{title}' is a {active} document. Nothing was run. {route}";
        }
    }
}
