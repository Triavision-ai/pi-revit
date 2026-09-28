using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    internal sealed class GetModelOverview : ITool
    {
        public string Name => "get_model_overview";
        public IReadOnlyList<string> DocumentKinds => DocumentKind.Both;
        public IReadOnlyList<string> Keywords => new[] { "project info", "model info", "units", "levels", "grids", "category counts", "summary", "document id", "identity", "document kind", "project or family", "family document" };
        public IReadOnlyList<ToolLimit> Limits => new[]
        {
            new ToolLimit("Switching the active document or Revit session", "tool", "manage_revit_instances"),
        };
        public string Label => "Get Model Overview";
        public string Description => "One-call orientation for the open Revit model: project metadata (title, name, number, client, address, file path, Revit version, display units), levels with elevations, grids, element counts for the major categories, and totals (elements, views, sheets). Use this first when starting work on a model.";
        public object ParametersSchema => new { type = "object", properties = new { }, required = Array.Empty<string>() };
        public string? PromptSnippet => "Get project info, units, levels, grids, and category counts for the open Revit model.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Call get_model_overview first when starting work on a Revit model; it covers project info, units, levels, grids, and per-category counts in one call.",
        };

        private static readonly IReadOnlyDictionary<string, BuiltInCategory> MajorCategories = new Dictionary<string, BuiltInCategory>
        {
            ["Walls"] = BuiltInCategory.OST_Walls,
            ["Doors"] = BuiltInCategory.OST_Doors,
            ["Windows"] = BuiltInCategory.OST_Windows,
            ["Floors"] = BuiltInCategory.OST_Floors,
            ["Rooms"] = BuiltInCategory.OST_Rooms,
            ["Areas"] = BuiltInCategory.OST_Areas,
            ["Ceilings"] = BuiltInCategory.OST_Ceilings,
            ["Roofs"] = BuiltInCategory.OST_Roofs,
            ["Columns"] = BuiltInCategory.OST_Columns,
            ["Structural Columns"] = BuiltInCategory.OST_StructuralColumns,
            ["Structural Framing"] = BuiltInCategory.OST_StructuralFraming,
            ["Stairs"] = BuiltInCategory.OST_Stairs,
            ["Furniture"] = BuiltInCategory.OST_Furniture,
            ["Generic Models"] = BuiltInCategory.OST_GenericModel,
            ["Pipes"] = BuiltInCategory.OST_PipeCurves,
            ["Ducts"] = BuiltInCategory.OST_DuctCurves,
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();
            var info = doc.ProjectInformation;
            var units = doc.GetUnits();

            string system = doc.DisplayUnitSystem == DisplayUnit.IMPERIAL ? "imperial" : "metric";
            string? lengthUnit = DisplayUnitName(units, SpecTypeId.Length);
            var project = new Dictionary<string, object?>
            {
                ["title"] = doc.Title,
                ["documentId"] = DocumentGuard.GetIdentity(doc),
                ["name"] = info?.Name,
                ["number"] = info?.Number,
                ["clientName"] = info?.ClientName,
                ["address"] = info?.Address,
                ["buildingName"] = info?.BuildingName,
                ["author"] = info?.Author,
                ["filePath"] = doc.PathName,
                // Project or family: tools declare which kinds they work in, and the bridge refuses others.
                ["documentKind"] = DocumentGuard.KindOf(doc),
                ["revitVersion"] = doc.Application.VersionNumber,
                ["units"] = new Dictionary<string, object?>
                {
                    ["system"] = system,
                    ["length"] = lengthUnit,
                    ["area"] = DisplayUnitName(units, SpecTypeId.Area),
                    ["volume"] = DisplayUnitName(units, SpecTypeId.Volume),
                },
            };

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .Select(level => new Dictionary<string, object?>
                {
                    ["id"] = level.Id.Value,
                    ["name"] = level.Name,
                    ["elevation_ft"] = level.Elevation,
                    ["elevation_display"] = FormatLength(units, level.Elevation),
                })
                .ToList();

            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .OrderBy(grid => grid.Name, StringComparer.OrdinalIgnoreCase)
                .Select(grid => new Dictionary<string, object?>
                {
                    ["id"] = grid.Id.Value,
                    ["name"] = grid.Name,
                })
                .ToList();

            var counts = new Dictionary<string, int>();
            foreach (var (label, category) in MajorCategories)
            {
                try
                {
                    counts[label] = new FilteredElementCollector(doc)
                        .OfCategory(category)
                        .WhereElementIsNotElementType()
                        .GetElementCount();
                }
                catch
                {
                    counts[label] = 0;
                }
            }

            int totalElements = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
            int sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount();
            int views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Count(view => !view.IsTemplate && view is not ViewSheet);

            var totals = new Dictionary<string, int>
            {
                ["elements"] = totalElements,
                ["views"] = views,
                ["sheets"] = sheets,
                ["levels"] = levels.Count,
                ["grids"] = grids.Count,
            };

            var topCategories = counts
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .Take(5)
                .Select(pair => $"{pair.Key} {pair.Value}")
                .ToList();
            string numberPart = string.IsNullOrWhiteSpace(info?.Number) ? string.Empty : $" (project {info!.Number})";
            string compact = $"'{doc.Title}'{numberPart}: {totalElements} elements, {levels.Count} levels, {grids.Count} grids, {sheets} sheets, {views} views; units {system} ({lengthUnit})."
                + (topCategories.Count > 0 ? $" Top categories: {string.Join(", ", topCategories)}." : string.Empty);

            if (doc.IsFamilyDocument) project["family"] = DescribeFamily(doc);
            return new ToolOutput(new { project, levels, grids, counts, totals }, compact);
        }

        /// <summary>What a family document holds: its category, types and parameters (names only).</summary>
        private static Dictionary<string, object?> DescribeFamily(Document doc)
        {
            var manager = doc.FamilyManager;
            var category = doc.OwnerFamily?.FamilyCategory;
            return new Dictionary<string, object?>
            {
                ["category"] = category?.Name,
                ["builtInCategory"] = category?.BuiltInCategory.ToString(),
                ["currentType"] = manager.CurrentType?.Name,
                ["types"] = manager.Types.Cast<FamilyType>().Select(type => type.Name).OrderBy(name => name).ToList(),
                ["parameters"] = manager.Parameters.Cast<FamilyParameter>()
                    .Select(parameter => new { name = parameter.Definition.Name, instance = parameter.IsInstance, formula = parameter.Formula })
                    .OrderBy(parameter => parameter.name).ToList(),
                ["api"] = "Family types, parameters and formulas are edited through FamilyManager (" + DocumentKind.FamilyApi + ") with execute_csharp; project-only tools refuse this document.",
            };
        }

        private static string? DisplayUnitName(Units units, ForgeTypeId spec)
        {
            try
            {
                return UnitResolver.ShortName(units.GetFormatOptions(spec).GetUnitTypeId());
            }
            catch
            {
                return null;
            }
        }

        private static string? FormatLength(Units units, double value)
        {
            try
            {
                return UnitFormatUtils.Format(units, SpecTypeId.Length, value, false);
            }
            catch
            {
                return null;
            }
        }
    }
}
