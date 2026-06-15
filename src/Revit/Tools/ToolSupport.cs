using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Resolves a user-facing category string (friendly alias, category display name,
    /// or BuiltInCategory enum name like OST_Walls) to a category ElementId usable
    /// with FilteredElementCollector.OfCategoryId. Shared by the query tools.
    /// </summary>
    internal static class CategoryResolver
    {
        private static readonly IReadOnlyDictionary<string, BuiltInCategory> Aliases = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["wall"] = BuiltInCategory.OST_Walls,
            ["walls"] = BuiltInCategory.OST_Walls,
            ["door"] = BuiltInCategory.OST_Doors,
            ["doors"] = BuiltInCategory.OST_Doors,
            ["window"] = BuiltInCategory.OST_Windows,
            ["windows"] = BuiltInCategory.OST_Windows,
            ["floor"] = BuiltInCategory.OST_Floors,
            ["floors"] = BuiltInCategory.OST_Floors,
            ["room"] = BuiltInCategory.OST_Rooms,
            ["rooms"] = BuiltInCategory.OST_Rooms,
            ["area"] = BuiltInCategory.OST_Areas,
            ["areas"] = BuiltInCategory.OST_Areas,
            ["column"] = BuiltInCategory.OST_Columns,
            ["columns"] = BuiltInCategory.OST_Columns,
            ["roof"] = BuiltInCategory.OST_Roofs,
            ["roofs"] = BuiltInCategory.OST_Roofs,
            ["stair"] = BuiltInCategory.OST_Stairs,
            ["stairs"] = BuiltInCategory.OST_Stairs,
            ["ceiling"] = BuiltInCategory.OST_Ceilings,
            ["ceilings"] = BuiltInCategory.OST_Ceilings,
            ["furniture"] = BuiltInCategory.OST_Furniture,
            ["pipe"] = BuiltInCategory.OST_PipeCurves,
            ["pipes"] = BuiltInCategory.OST_PipeCurves,
            ["duct"] = BuiltInCategory.OST_DuctCurves,
            ["ducts"] = BuiltInCategory.OST_DuctCurves,
            ["beam"] = BuiltInCategory.OST_StructuralFraming,
            ["beams"] = BuiltInCategory.OST_StructuralFraming,
            ["structural framing"] = BuiltInCategory.OST_StructuralFraming,
            ["generic model"] = BuiltInCategory.OST_GenericModel,
            ["generic models"] = BuiltInCategory.OST_GenericModel,
            ["level"] = BuiltInCategory.OST_Levels,
            ["levels"] = BuiltInCategory.OST_Levels,
            ["grid"] = BuiltInCategory.OST_Grids,
            ["grids"] = BuiltInCategory.OST_Grids,
            ["sheet"] = BuiltInCategory.OST_Sheets,
            ["sheets"] = BuiltInCategory.OST_Sheets,
            ["view"] = BuiltInCategory.OST_Views,
            ["views"] = BuiltInCategory.OST_Views,
        };

        public static ElementId Resolve(Document doc, string input)
        {
            string trimmed = input.Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException("category must be a non-empty string.");

            if (Aliases.TryGetValue(trimmed, out var alias))
                return new ElementId(alias);
            if (TryParseBuiltInCategory(trimmed, out var builtIn))
                return new ElementId(builtIn);
            if (TryParseBuiltInCategory("OST_" + trimmed.Replace(" ", string.Empty), out builtIn))
                return new ElementId(builtIn);

            foreach (Category category in doc.Settings.Categories)
            {
                if (string.Equals(category.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    return category.Id;
            }

            throw new ArgumentException($"Unknown category: {input}. Use a BuiltInCategory enum name (e.g. OST_Walls) or a category display name as shown in Revit (e.g. Walls, Doors, Sheets, Views).");
        }

        private static bool TryParseBuiltInCategory(string text, out BuiltInCategory category)
        {
            category = BuiltInCategory.INVALID;
            return text.Length > 0
                && char.IsLetter(text[0])
                && Enum.TryParse(text, true, out category)
                && category != BuiltInCategory.INVALID;
        }
    }

    /// <summary>
    /// Resolves a Revit API class name ("Wall", "ViewSheet", "WallType", or a full
    /// name like "Autodesk.Revit.DB.Mechanical.Duct") to its Type for OfClass filters.
    /// </summary>
    internal static class ElementClassResolver
    {
        private static readonly Lazy<IReadOnlyDictionary<string, Type>> BySimpleName = new(BuildSimpleNameMap);

        public static Type Resolve(string input)
        {
            string trimmed = input.Trim();
            var assembly = typeof(Element).Assembly;
            Type? match = trimmed.Contains('.') ? assembly.GetType(trimmed, false, true) : null;
            match ??= assembly.GetType("Autodesk.Revit.DB." + trimmed, false, true);
            if (match is null)
                BySimpleName.Value.TryGetValue(trimmed, out match);

            if (match is null || !typeof(Element).IsAssignableFrom(match))
                throw new ArgumentException($"Unknown Revit element class: {input}. Use an Element subclass from the Revit API (e.g. Wall, FamilyInstance, ViewSheet, Level, WallType) or a full name like Autodesk.Revit.DB.Mechanical.Duct.");
            if (match == typeof(Element))
                throw new ArgumentException("of_class must be a concrete Element subclass, not Element itself.");
            return match;
        }

        private static IReadOnlyDictionary<string, Type> BuildSimpleNameMap()
        {
            Type[] types;
            try
            {
                types = typeof(Element).Assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }

            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in types)
            {
                if (!type.IsPublic || !typeof(Element).IsAssignableFrom(type))
                    continue;
                // Prefer the root Autodesk.Revit.DB namespace when simple names collide.
                if (!map.TryGetValue(type.Name, out var existing) || (type.Namespace == "Autodesk.Revit.DB" && existing.Namespace != "Autodesk.Revit.DB"))
                    map[type.Name] = type;
            }
            return map;
        }
    }

    /// <summary>
    /// Resolves friendly forge unit names ("millimeters", "squareMeters") or full forge
    /// ids ("autodesk.unit.unit:millimeters-1.0.1") to UnitTypeId values, and back.
    /// </summary>
    internal static class UnitResolver
    {
        private static readonly Lazy<IReadOnlyDictionary<string, ForgeTypeId>> ByName = new(Build);

        public static ForgeTypeId Resolve(string unit)
        {
            string trimmed = unit.Trim();
            if (ByName.Value.TryGetValue(trimmed, out var match))
                return match;
            throw new ArgumentException($"Unknown unit: {unit}. Use a forge unit name like millimeters, meters, feet, inches, squareMeters, cubicFeet, degrees.");
        }

        /// <summary>"autodesk.unit.unit:millimeters-1.0.1" -> "millimeters".</summary>
        public static string ShortName(ForgeTypeId unit)
        {
            string id = unit.TypeId ?? string.Empty;
            int colon = id.IndexOf(':');
            string tail = colon >= 0 ? id[(colon + 1)..] : id;
            int dash = tail.IndexOf('-');
            return dash >= 0 ? tail[..dash] : tail;
        }

        private static IReadOnlyDictionary<string, ForgeTypeId> Build()
        {
            var map = new Dictionary<string, ForgeTypeId>(StringComparer.OrdinalIgnoreCase);
            foreach (ForgeTypeId unit in UnitUtils.GetAllUnits())
            {
                map[ShortName(unit)] = unit;
                map[unit.TypeId] = unit;
            }
            return map;
        }
    }

    /// <summary>Identity-only element row shared by the query tools.</summary>
    internal static class ElementIdentity
    {
        public static readonly IReadOnlyList<string> Fields = new[] { "id", "name", "category", "typeName", "levelId" };

        public static Dictionary<string, object?> Build(Document doc, Element element, IReadOnlyList<string> fields)
        {
            var row = new Dictionary<string, object?>(fields.Count);
            foreach (string field in fields)
            {
                row[field] = field switch
                {
                    "id" => (object?)element.Id.Value,
                    "name" => element.Name,
                    "category" => element.Category?.Name,
                    "typeName" => doc.GetElement(element.GetTypeId())?.Name,
                    "levelId" => element.LevelId is { } levelId && levelId != ElementId.InvalidElementId ? levelId.Value : (long?)null,
                    _ => null,
                };
            }
            return row;
        }
    }

    /// <summary>Small JsonElement argument readers shared by the tools.</summary>
    internal static class JsonArgs
    {
        public static string? GetString(JsonElement args, string name)
            => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public static bool GetBool(JsonElement args, string name, bool fallback)
            => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

        public static int GetInt(JsonElement args, string name, int fallback)
            => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed)
                ? parsed
                : fallback;

        public static long? GetLong(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long fromNumber))
                return fromNumber;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long fromString))
                return fromString;
            return null;
        }

        public static List<long> GetLongArray(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"Missing or invalid required field: {name} must be an array of element ids.");

            var result = new List<long>(value.GetArrayLength());
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long fromNumber))
                    result.Add(fromNumber);
                else if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out long fromString))
                    result.Add(fromString);
                else
                    throw new ArgumentException($"{name} must contain only integer element ids.");
            }
            return result;
        }

        public static List<string>? GetStringArray(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<string>(value.GetArrayLength());
            foreach (var item in value.EnumerateArray())
            {
                string? text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                    throw new ArgumentException($"{name} must contain only non-empty strings.");
                result.Add(text.Trim());
            }
            return result;
        }
    }
}
