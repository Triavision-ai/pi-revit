using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    internal sealed class GetElementTypes : ITool
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;

        public string Name => "get_element_types";
        public string Label => "Get Element Types";
        public string Description => "List element types / family symbols (wall types, door types, ...) for a category or class: id, name, familyName, category, isFamilySymbol, optional placed-instance count per type (answers 'used vs merely loaded'). Use the ids with get_elements type_id or for type assignment.";

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                category = new
                {
                    type = "string",
                    description = "Category: BuiltInCategory enum name (e.g. OST_Walls) or display name (e.g. Walls, Doors).",
                },
                of_class = new
                {
                    type = "string",
                    description = "Element type class, e.g. WallType, FloorType, FamilySymbol.",
                },
                name_filter = new
                {
                    type = "string",
                    description = "Case-insensitive substring matched against type name and family name.",
                },
                include_instance_count = new
                {
                    type = "boolean",
                    description = "Add the number of placed instances per type. Counts the scoped category (or the whole model without a category), so it costs one extra pass. Default false.",
                },
                offset = new { type = "integer", description = "Pagination offset. Default 0." },
                limit = new { type = "integer", description = "Max types to return (1-1000). Default 200." },
            },
            required = Array.Empty<string>(),
        };

        public string? PromptSnippet => "List Revit element types / family symbols with optional placed-instance counts.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use get_element_types to discover the available types of a category; set include_instance_count to see which types are actually placed.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();

            string? categoryInput = JsonArgs.GetString(args, "category");
            string? classInput = JsonArgs.GetString(args, "of_class");
            string needle = JsonArgs.GetString(args, "name_filter")?.Trim() ?? string.Empty;
            bool includeInstanceCount = JsonArgs.GetBool(args, "include_instance_count", false);
            int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0));
            int limit = Math.Clamp(JsonArgs.GetInt(args, "limit", DefaultLimit), 1, MaxLimit);

            ElementId? categoryId = string.IsNullOrWhiteSpace(categoryInput) ? null : CategoryResolver.Resolve(doc, categoryInput);
            Type? elementClass = string.IsNullOrWhiteSpace(classInput) ? null : ElementClassResolver.Resolve(classInput);

            var collector = new FilteredElementCollector(doc);
            if (categoryId != null)
                collector = collector.OfCategoryId(categoryId);
            if (elementClass != null)
                collector = collector.OfClass(elementClass);
            collector = collector.WhereElementIsElementType();

            var types = collector
                .OfType<ElementType>()
                .Select(type => new { Type = type, Name = type.Name ?? string.Empty, FamilyName = SafeFamilyName(type) })
                .Where(entry => needle.Length == 0
                    || entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || entry.FamilyName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Dictionary<long, int>? instanceCounts = null;
            if (includeInstanceCount)
            {
                instanceCounts = new Dictionary<long, int>();
                var instances = new FilteredElementCollector(doc);
                if (categoryId != null)
                    instances = instances.OfCategoryId(categoryId);
                foreach (Element element in instances.WhereElementIsNotElementType())
                {
                    long typeIdValue = element.GetTypeId().Value;
                    if (typeIdValue < 0)
                        continue;
                    instanceCounts[typeIdValue] = instanceCounts.TryGetValue(typeIdValue, out int count) ? count + 1 : 1;
                }
            }

            int total = types.Count;
            var window = types.Skip(offset).Take(limit).Select(entry =>
            {
                var row = new Dictionary<string, object?>
                {
                    ["id"] = entry.Type.Id.Value,
                    ["name"] = entry.Name,
                    ["familyName"] = entry.FamilyName,
                    ["category"] = entry.Type.Category?.Name,
                    ["isFamilySymbol"] = entry.Type is FamilySymbol,
                };
                if (instanceCounts != null)
                    row["instanceCount"] = instanceCounts.TryGetValue(entry.Type.Id.Value, out int count) ? count : 0;
                return row;
            }).ToList();

            bool hasMore = offset + window.Count < total;
            int? nextOffset = hasMore ? offset + window.Count : null;

            string scope = !string.IsNullOrWhiteSpace(categoryInput) ? categoryInput.Trim()
                : !string.IsNullOrWhiteSpace(classInput) ? classInput.Trim()
                : "all categories";
            string sampleText = window.Count > 0
                ? $" Sample: {string.Join(", ", window.Take(3).Select(row => $"'{row["familyName"]}: {row["name"]}' ({row["id"]})"))}."
                : string.Empty;
            string compact = hasMore
                ? $"element types ({scope}): {total} total, returned {window.Count} at offset {offset}, has_more (next_offset {nextOffset}).{sampleText}"
                : $"element types ({scope}): {total} total, returned {window.Count} at offset {offset}.{sampleText}";

            return new ToolOutput(new
            {
                total_count = total,
                returned_count = window.Count,
                offset,
                has_more = hasMore,
                next_offset = nextOffset,
                types = window,
            }, compact);
        }

        private static string SafeFamilyName(ElementType type)
        {
            try
            {
                return type.FamilyName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
