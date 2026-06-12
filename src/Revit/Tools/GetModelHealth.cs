using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Model audit. Document.GetWarnings() returns FailureMessage API objects that
    /// do not survive plain JSON serialization, so they are flattened here into
    /// clean DTOs grouped by description, plus basic structure counts: worksharing
    /// status and user worksets, phases, design options, in-place families, and
    /// the total element count.
    /// </summary>
    internal sealed class GetModelHealth : ITool
    {
        private const int MaxGroups = 100;
        private const int MaxElementsPerGroup = 20;

        public string Name => "get_model_health";
        public string Label => "Get Model Health";
        public string Description => "Audit the open Revit model: every document warning grouped by description (description, severity, occurrence count, failing element ids+names — the top 100 groups ordered by count, up to 20 elements each), plus structure counts: worksharing status with user worksets, phases, design options, in-place families, and the total element count. Read-only; use it to review model quality, e.g. before and after bulk edits.";
        public string Tier => "advanced";

        public object ParametersSchema => new { type = "object", properties = new { }, required = Array.Empty<string>() };

        public string? PromptSnippet => "Audit the model: warnings grouped by description plus workset/phase/design-option counts.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Run get_model_health to review Revit warnings (grouped, with failing element ids) before and after bulk modifications.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();

            var warnings = doc.GetWarnings();
            var groups = warnings
                .GroupBy(SafeDescription, StringComparer.Ordinal)
                .Select(group =>
                {
                    var elementIds = group
                        .SelectMany(SafeFailingElements)
                        .Distinct()
                        .ToList();
                    return new Dictionary<string, object?>
                    {
                        ["description"] = group.Key,
                        ["severity"] = SafeSeverity(group.First()),
                        ["count"] = group.Count(),
                        ["elementCount"] = elementIds.Count,
                        ["elements"] = elementIds
                            .Take(MaxElementsPerGroup)
                            .Select(id => new Dictionary<string, object?>
                            {
                                ["id"] = id.Value,
                                ["name"] = SafeName(doc, id),
                            })
                            .ToList(),
                        ["elementsTruncated"] = elementIds.Count > MaxElementsPerGroup,
                    };
                })
                .OrderByDescending(group => (int)group["count"]!)
                .ToList();
            bool groupsTruncated = groups.Count > MaxGroups;
            var topGroups = groups.Take(MaxGroups).ToList();

            bool workshared = doc.IsWorkshared;
            List<string>? worksetNames = null;
            if (workshared)
            {
                worksetNames = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .Select(workset => workset.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var phaseNames = new List<string>();
            foreach (Phase phase in doc.Phases)
                phaseNames.Add(phase.Name);

            var designOptionNames = new FilteredElementCollector(doc)
                .OfClass(typeof(DesignOption))
                .Select(option => option.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int inPlaceFamilies = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Count(family => family.IsInPlace);

            int totalElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementCount();

            var payload = new Dictionary<string, object?>
            {
                ["warnings"] = new Dictionary<string, object?>
                {
                    ["total"] = warnings.Count,
                    ["groupCount"] = groups.Count,
                    ["groups"] = topGroups,
                    ["groupsTruncated"] = groupsTruncated,
                },
                ["workshared"] = workshared,
                ["worksets"] = worksetNames is null ? null : new Dictionary<string, object?>
                {
                    ["count"] = worksetNames.Count,
                    ["names"] = worksetNames,
                },
                ["phases"] = new Dictionary<string, object?>
                {
                    ["count"] = phaseNames.Count,
                    ["names"] = phaseNames,
                },
                ["designOptions"] = new Dictionary<string, object?>
                {
                    ["count"] = designOptionNames.Count,
                    ["names"] = designOptionNames,
                },
                ["inPlaceFamilies"] = inPlaceFamilies,
                ["totalElements"] = totalElements,
            };

            string topWarning = topGroups.Count > 0
                ? $" Top: '{Shorten((string)topGroups[0]["description"]!, 80)}' x{topGroups[0]["count"]}."
                : string.Empty;
            string compact = $"{warnings.Count} warning(s) in {groups.Count} group(s); "
                + (workshared ? $"workshared ({worksetNames!.Count} workset(s)); " : "not workshared; ")
                + $"{phaseNames.Count} phase(s); {designOptionNames.Count} design option(s); {inPlaceFamilies} in-place families; {totalElements} elements."
                + topWarning;

            return new ToolOutput(payload, compact);
        }

        // ------------------------------------------------- FailureMessage flattening

        private static string SafeDescription(FailureMessage warning)
        {
            try
            {
                string text = warning.GetDescriptionText();
                return string.IsNullOrWhiteSpace(text) ? "(no description)" : text.Trim();
            }
            catch
            {
                return "(no description)";
            }
        }

        private static string? SafeSeverity(FailureMessage warning)
        {
            try
            {
                return warning.GetSeverity().ToString();
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<ElementId> SafeFailingElements(FailureMessage warning)
        {
            try
            {
                return warning.GetFailingElements();
            }
            catch
            {
                return Enumerable.Empty<ElementId>();
            }
        }

        private static string? SafeName(Document doc, ElementId id)
        {
            try
            {
                return doc.GetElement(id)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string Shorten(string text, int max)
            => text.Length <= max ? text : text[..(max - 3)] + "...";
    }
}
