using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    internal sealed class GetElementDetails : ITool
    {
        private const int MaxIds = 50;

        public string Name => "get_element_details";
        public string Label => "Get Element Details";
        public string Description => "Inspect one or more elements by id: parameter VALUES (name, internal value, formatted displayValue, storage type, display unit where applicable, read-only/shared flags), plus optional type parameters, location (point/curve), bounding box, and materials. Internal numeric values are Revit internal units (feet-based); displayValue is formatted in the document's display units and 'unit' names that display unit. The single home for reading parameter values — get_elements only returns identity fields.";

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                element_ids = new
                {
                    type = "array",
                    items = new { type = "integer" },
                    description = "Element ids to inspect (1-50 per call).",
                },
                parameter_names = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Only return these parameters (case-insensitive exact names). Default: all.",
                },
                include = new
                {
                    type = "object",
                    description = "Optional sections per element.",
                    properties = new
                    {
                        parameters = new { type = "boolean", description = "Instance parameter values. Default true." },
                        type_parameters = new { type = "boolean", description = "Also include the element type's parameters, marked isType=true. Default false." },
                        location = new { type = "boolean", description = "Location point or curve (coordinates in internal feet). Default false." },
                        bounding_box = new { type = "boolean", description = "Model bounding box min/max (internal feet). Default false." },
                        materials = new { type = "boolean", description = "Material ids/names with area/volume (internal units). Default false." },
                    },
                },
            },
            required = new[] { "element_ids" },
        };

        public string? PromptSnippet => "Read parameter values, location, bounding box, and materials for specific Revit elements.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use get_element_details (with ids from get_elements or the user's selection) whenever parameter values are needed; numeric values carry both internal value and display value with its unit.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();

            var ids = JsonArgs.GetLongArray(args, "element_ids");
            if (ids.Count == 0)
                throw new ArgumentException("element_ids must contain at least one element id.");
            if (ids.Count > MaxIds)
                throw new ArgumentException($"Too many element_ids ({ids.Count}); max {MaxIds} per call. Inspect ids in batches.");

            var parameterNames = JsonArgs.GetStringArray(args, "parameter_names");
            HashSet<string>? nameFilter = parameterNames is { Count: > 0 }
                ? new HashSet<string>(parameterNames, StringComparer.OrdinalIgnoreCase)
                : null;

            JsonElement include = args.TryGetProperty("include", out var includeElement) && includeElement.ValueKind == JsonValueKind.Object
                ? includeElement
                : default;
            bool withParameters = JsonArgs.GetBool(include, "parameters", true);
            bool withTypeParameters = JsonArgs.GetBool(include, "type_parameters", false);
            bool withLocation = JsonArgs.GetBool(include, "location", false);
            bool withBoundingBox = JsonArgs.GetBool(include, "bounding_box", false);
            bool withMaterials = JsonArgs.GetBool(include, "materials", false);

            var elements = new List<Dictionary<string, object?>>(ids.Count);
            var notFound = new List<long>();
            var compactParts = new List<string>();

            foreach (long id in ids)
            {
                var element = doc.GetElement(new ElementId(id));
                if (element is null)
                {
                    notFound.Add(id);
                    continue;
                }

                var elementType = doc.GetElement(element.GetTypeId());
                var dto = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = element.Name,
                    ["category"] = element.Category?.Name,
                    ["typeName"] = elementType?.Name,
                    ["typeId"] = elementType?.Id.Value,
                    ["levelId"] = element.LevelId is { } levelId && levelId != ElementId.InvalidElementId ? levelId.Value : (long?)null,
                };

                int parameterCount = 0;
                if (withParameters)
                {
                    var parameters = new List<Dictionary<string, object?>>();
                    AppendParameters(doc, element, nameFilter, isType: false, parameters);
                    if (withTypeParameters && elementType != null)
                        AppendParameters(doc, elementType, nameFilter, isType: true, parameters);
                    dto["parameters"] = parameters;
                    parameterCount = parameters.Count;
                }

                if (withLocation)
                    dto["location"] = DescribeLocation(element);
                if (withBoundingBox)
                    dto["boundingBox"] = DescribeBoundingBox(element);
                if (withMaterials)
                    dto["materials"] = DescribeMaterials(doc, element);

                elements.Add(dto);
                compactParts.Add($"'{element.Name}' (id {id}, {element.Category?.Name ?? "no category"}{(withParameters ? $", {parameterCount} params" : string.Empty)})");
            }

            string compact = $"{elements.Count} element(s)"
                + (compactParts.Count > 0 ? $": {string.Join("; ", compactParts.Take(5))}" : string.Empty)
                + (compactParts.Count > 5 ? $" (+{compactParts.Count - 5} more)" : string.Empty)
                + (notFound.Count > 0 ? $". Not found: {string.Join(", ", notFound)}" : string.Empty)
                + ".";

            return new ToolOutput(new { count = elements.Count, elements, not_found = notFound }, compact);
        }

        private static void AppendParameters(Document doc, Element element, HashSet<string>? nameFilter, bool isType, List<Dictionary<string, object?>> target)
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (Parameter parameter in element.Parameters)
            {
                string name = parameter.Definition?.Name ?? string.Empty;
                if (nameFilter != null && !nameFilter.Contains(name))
                    continue;
                rows.Add(DescribeParameter(doc, parameter, name, isType));
            }
            rows.Sort((a, b) => string.Compare(a["name"] as string, b["name"] as string, StringComparison.OrdinalIgnoreCase));
            target.AddRange(rows);
        }

        private static Dictionary<string, object?> DescribeParameter(Document doc, Parameter parameter, string name, bool isType)
        {
            bool hasValue = parameter.HasValue;
            var row = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["storageType"] = parameter.StorageType.ToString(),
                ["isType"] = isType,
                ["isReadOnly"] = parameter.IsReadOnly,
            };

            switch (parameter.StorageType)
            {
                case StorageType.String:
                {
                    string? text = hasValue ? parameter.AsString() : null;
                    row["value"] = text;
                    row["displayValue"] = text;
                    break;
                }
                case StorageType.Integer:
                    row["value"] = hasValue ? parameter.AsInteger() : (int?)null;
                    row["displayValue"] = hasValue
                        ? SafeValueString(parameter) ?? parameter.AsInteger().ToString(CultureInfo.InvariantCulture)
                        : null;
                    break;
                case StorageType.Double:
                {
                    row["value"] = hasValue ? parameter.AsDouble() : (double?)null;
                    row["displayValue"] = hasValue ? SafeValueString(parameter) : null;
                    try
                    {
                        var spec = parameter.Definition?.GetDataType();
                        if (spec != null && UnitUtils.IsMeasurableSpec(spec))
                            row["unit"] = UnitResolver.ShortName(doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId());
                    }
                    catch
                    {
                        // Leave unit out when the spec or format options are unavailable.
                    }
                    break;
                }
                case StorageType.ElementId:
                {
                    var referenced = hasValue ? parameter.AsElementId() : null;
                    row["value"] = referenced != null && referenced != ElementId.InvalidElementId ? referenced.Value : (long?)null;
                    row["displayValue"] = hasValue
                        ? SafeValueString(parameter) ?? (referenced != null ? doc.GetElement(referenced)?.Name : null)
                        : null;
                    break;
                }
                default:
                    row["value"] = null;
                    row["displayValue"] = hasValue ? SafeValueString(parameter) : null;
                    break;
            }

            if (parameter.IsShared)
            {
                row["isShared"] = true;
                try
                {
                    row["guid"] = parameter.GUID.ToString();
                }
                catch
                {
                    // GUID can throw for malformed shared parameters; skip it.
                }
            }
            if (parameter.Definition is InternalDefinition internalDefinition && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
                row["builtInParameter"] = internalDefinition.BuiltInParameter.ToString();

            return row;
        }

        private static string? SafeValueString(Parameter parameter)
        {
            try
            {
                return parameter.AsValueString();
            }
            catch
            {
                return null;
            }
        }

        private static object? DescribeLocation(Element element)
        {
            switch (element.Location)
            {
                case LocationPoint point:
                {
                    var dto = new Dictionary<string, object?> { ["kind"] = "point", ["point"] = Point(point.Point) };
                    try
                    {
                        dto["rotation"] = point.Rotation;
                    }
                    catch
                    {
                        // Not all point-located elements expose a rotation.
                    }
                    return dto;
                }
                case LocationCurve curve:
                {
                    var c = curve.Curve;
                    var dto = new Dictionary<string, object?> { ["kind"] = "curve", ["isLine"] = c is Line };
                    if (c.IsBound)
                    {
                        dto["start"] = Point(c.GetEndPoint(0));
                        dto["end"] = Point(c.GetEndPoint(1));
                        dto["length"] = c.Length;
                    }
                    return dto;
                }
                default:
                    return null;
            }
        }

        private static object? DescribeBoundingBox(Element element)
        {
            var box = element.get_BoundingBox(null);
            return box is null ? null : new { min = Point(box.Min), max = Point(box.Max) };
        }

        private static List<Dictionary<string, object?>> DescribeMaterials(Document doc, Element element)
        {
            var result = new List<Dictionary<string, object?>>();
            ICollection<ElementId> materialIds;
            try
            {
                materialIds = element.GetMaterialIds(false);
            }
            catch
            {
                return result;
            }

            foreach (var materialId in materialIds)
            {
                var row = new Dictionary<string, object?>
                {
                    ["id"] = materialId.Value,
                    ["name"] = doc.GetElement(materialId)?.Name,
                };
                try
                {
                    row["area"] = element.GetMaterialArea(materialId, false);
                }
                catch
                {
                    // Area is undefined for some element kinds.
                }
                try
                {
                    row["volume"] = element.GetMaterialVolume(materialId);
                }
                catch
                {
                    // Volume is undefined for some element kinds.
                }
                result.Add(row);
            }
            return result;
        }

        private static object Point(XYZ point) => new { x = point.X, y = point.Y, z = point.Z };
    }
}
