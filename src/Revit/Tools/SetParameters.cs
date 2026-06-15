using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Generic bulk parameter write — and the home for rename (parameter 'Name',
    /// falling back to the Element.Name property when the parameter is read-only,
    /// unit handling). Owns one Transaction("set_parameters") per call: partial success
    /// commits and reports the failed updates; total failure rolls back. Values
    /// are storage-type validated; numeric values convert from the given forge
    /// unit (or the document's display units for the parameter's spec) to
    /// internal units.
    /// </summary>
    internal sealed class SetParameters : ITool
    {
        private const int MaxUpdates = 200;

        public string Name => "set_parameters";
        public string Label => "Set Parameters";
        public string Description => "Write parameter values on elements — also the home for rename: parameter 'Name' covers levels, views, sheets, types, etc. (falls back to the element's Name property when the Name parameter is read-only). updates apply in ONE transaction: partial success commits and lists the failures; if every update fails the transaction is rolled back and nothing changes. parameter accepts a display name (Comments, Mark, Name), a BuiltInParameter enum name (e.g. ALL_MODEL_MARK), or guid:<GUID> for a shared parameter; type parameters live on the element type, so pass the type's id. Values are validated against the parameter's storage type; numeric values are interpreted in the document's display units for that parameter unless 'unit' (e.g. millimeters, feet, squareMeters) is given.";
        public bool Write => true;

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                updates = new
                {
                    type = "array",
                    description = "Parameter writes to apply in one transaction (1-200 per call).",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            element_id = new { type = "integer", description = "Target element id (use the type id for type parameters)." },
                            parameter = new { type = "string", description = "Parameter display name (e.g. Comments, Mark, Name), BuiltInParameter enum name (e.g. ALL_MODEL_MARK), or guid:<GUID> for a shared parameter." },
                            value = new { description = "New value: string, number, or boolean (yes/no parameters). Empty string clears a text parameter." },
                            unit = new { type = "string", description = "Unit of a numeric value, e.g. millimeters, meters, feet, inches, squareMeters, degrees. Default: the document's display units for that parameter." },
                        },
                        required = new[] { "element_id", "parameter", "value" },
                    },
                },
            },
            required = new[] { "updates" },
        };

        public string? PromptSnippet => "Write element parameter values in bulk, including renames via the 'Name' parameter.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use set_parameters for parameter writes and renames (parameter 'Name'); pass 'unit' with numeric values unless the document's display units are intended.",
            "set_parameters commits partial successes — always check the failed list in its result instead of assuming every update applied.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();
            var updates = ParseUpdates(args);

            var succeeded = new List<Dictionary<string, object?>>();
            var failed = new List<Dictionary<string, object?>>();

            using var transaction = new Transaction(doc, "set_parameters");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Unable to start the set_parameters transaction.");

            try
            {
                foreach (var update in updates)
                {
                    try
                    {
                        string? newDisplayValue = Apply(doc, update);
                        succeeded.Add(new Dictionary<string, object?>
                        {
                            ["id"] = update.ElementId,
                            ["parameter"] = update.ParameterInput,
                            ["newDisplayValue"] = newDisplayValue,
                        });
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new Dictionary<string, object?>
                        {
                            ["id"] = update.ElementId,
                            ["parameter"] = update.ParameterInput,
                            ["reason"] = ex.Message,
                        });
                    }
                }

                if (succeeded.Count > 0)
                {
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("The set_parameters transaction failed to commit; no changes were saved.");
                }
                else
                {
                    transaction.RollBack();
                }
            }
            catch
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
                throw;
            }

            int elementCount = succeeded.Select(row => row["id"]).Distinct().Count();
            string failureSample = failed.Count > 0
                ? $" First failure: element {failed[0]["id"]} '{failed[0]["parameter"]}': {failed[0]["reason"]}"
                : string.Empty;
            string compact = failed.Count == 0
                ? $"Updated {succeeded.Count} parameter value(s) on {elementCount} element(s)."
                : succeeded.Count == 0
                    ? $"No updates applied; all {failed.Count} failed — transaction rolled back.{failureSample}"
                    : $"Updated {succeeded.Count} parameter value(s) on {elementCount} element(s); {failed.Count} failed.{failureSample}";

            return new ToolOutput(new
            {
                updated = succeeded.Count,
                committed = succeeded.Count > 0,
                succeeded,
                failed,
            }, compact);
        }

        // ------------------------------------------------------------- application

        /// <summary>Applies one update; returns the new display value or throws with the per-update reason.</summary>
        private static string? Apply(Document doc, Update update)
        {
            var element = doc.GetElement(new ElementId(update.ElementId))
                ?? throw new InvalidOperationException($"Element {update.ElementId} not found.");

            var parameter = FindParameter(element, update);
            bool nameTarget = string.Equals(update.ParameterInput, "Name", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parameter?.Definition?.Name, "Name", StringComparison.OrdinalIgnoreCase);

            if (parameter is null)
            {
                if (nameTarget)
                    return SetElementName(element, update);
                throw new InvalidOperationException($"Parameter '{update.ParameterInput}' not found on element {update.ElementId} ('{element.Name}'). Type parameters live on the element type — pass its id instead.");
            }

            if (parameter.IsReadOnly)
            {
                if (nameTarget)
                    return SetElementName(element, update);
                throw new InvalidOperationException($"Parameter '{parameter.Definition?.Name}' is read-only on element {update.ElementId}.");
            }

            bool accepted = parameter.StorageType switch
            {
                StorageType.String => parameter.Set(ValueAsString(update.Value)),
                StorageType.Double => parameter.Set(ToInternalUnits(doc, parameter, RequireDouble(parameter, update.Value), update.Unit)),
                StorageType.Integer => parameter.Set(RequireInt(parameter, update.Value)),
                StorageType.ElementId => parameter.Set(new ElementId(RequireLong(parameter, update.Value))),
                _ => throw new InvalidOperationException($"Parameter '{parameter.Definition?.Name}' has storage type None and cannot be set."),
            };

            if (!accepted)
            {
                if (nameTarget)
                    return SetElementName(element, update);
                throw new InvalidOperationException($"Revit rejected the value for '{parameter.Definition?.Name}' on element {update.ElementId} (Parameter.Set returned false).");
            }
            return DescribeNewValue(parameter) ?? ValueAsString(update.Value);
        }

        /// <summary>Rename fallback: the Element.Name property setter.</summary>
        private static string SetElementName(Element element, Update update)
        {
            string name = ValueAsString(update.Value);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Renaming needs a non-empty string value.");
            element.Name = name;
            return name;
        }

        private static Parameter? FindParameter(Element element, Update update)
        {
            if (update.BuiltIn is { } builtIn)
                return element.get_Parameter(builtIn);
            if (update.SharedGuid is { } guid)
                return element.get_Parameter(guid);

            var direct = element.LookupParameter(update.ParameterInput);
            if (direct != null)
                return direct;
            foreach (Parameter parameter in element.Parameters)
            {
                if (string.Equals(parameter.Definition?.Name, update.ParameterInput, StringComparison.OrdinalIgnoreCase))
                    return parameter;
            }
            return null;
        }

        private static string? DescribeNewValue(Parameter parameter)
        {
            try
            {
                return parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Converts a user-supplied numeric value to internal units using the
        /// explicit unit, or the document's display units for the parameter's spec.</summary>
        private static double ToInternalUnits(Document doc, Parameter parameter, double value, string? unitInput)
        {
            ForgeTypeId? spec = null;
            try
            {
                spec = parameter.Definition?.GetDataType();
            }
            catch
            {
                // Treat as unitless below.
            }

            bool measurable = false;
            try
            {
                measurable = spec != null && UnitUtils.IsMeasurableSpec(spec);
            }
            catch
            {
                // Treat as unitless below.
            }

            if (!measurable || spec is null)
            {
                if (!string.IsNullOrWhiteSpace(unitInput))
                    throw new InvalidOperationException($"Parameter '{parameter.Definition?.Name}' is unitless; remove unit '{unitInput}'.");
                return value;
            }

            ForgeTypeId unit;
            if (!string.IsNullOrWhiteSpace(unitInput))
            {
                unit = UnitResolver.Resolve(unitInput);
                if (!UnitUtils.IsValidUnit(spec, unit))
                    throw new InvalidOperationException($"Unit '{unitInput}' does not fit parameter '{parameter.Definition?.Name}' (spec {UnitResolver.ShortName(spec)}); the document displays it in {UnitResolver.ShortName(doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId())}.");
            }
            else
            {
                unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
            }
            return UnitUtils.ConvertToInternalUnits(value, unit);
        }

        // ------------------------------------------------------------ value typing

        private static double RequireDouble(Parameter parameter, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double fromNumber))
                return fromNumber;
            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fromString))
                return fromString;
            throw new InvalidOperationException($"Parameter '{parameter.Definition?.Name}' stores a number; got {value.GetRawText()}.");
        }

        private static int RequireInt(Parameter parameter, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int fromNumber))
                return fromNumber;
            if (value.ValueKind == JsonValueKind.True)
                return 1;
            if (value.ValueKind == JsonValueKind.False)
                return 0;
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()?.Trim() ?? string.Empty;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromString))
                    return fromString;
                if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                    return 1;
                if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                    return 0;
            }
            throw new InvalidOperationException($"Parameter '{parameter.Definition?.Name}' stores an integer (yes/no parameters take true/false); got {value.GetRawText()}.");
        }

        private static long RequireLong(Parameter parameter, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long fromNumber))
                return fromNumber;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long fromString))
                return fromString;
            throw new InvalidOperationException($"Parameter '{parameter.Definition?.Name}' stores an element id; pass the id as an integer. Got {value.GetRawText()}.");
        }

        private static string ValueAsString(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText(),
        };

        // ---------------------------------------------------------------- parsing

        private sealed class Update
        {
            public required long ElementId { get; init; }
            public required string ParameterInput { get; init; }
            public JsonElement Value { get; init; }
            public string? Unit { get; init; }
            public BuiltInParameter? BuiltIn { get; init; }
            public Guid? SharedGuid { get; init; }
        }

        private static List<Update> ParseUpdates(JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("updates", out var updatesElement) || updatesElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Missing or invalid required field: updates must be an array of { element_id, parameter, value, unit? } objects.");

            var updates = new List<Update>(updatesElement.GetArrayLength());
            foreach (var item in updatesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Each update must be an object with element_id, parameter, and value.");

                long elementId = JsonArgs.GetLong(item, "element_id")
                    ?? throw new ArgumentException("Each update needs an integer element_id.");
                string parameterInput = (JsonArgs.GetString(item, "parameter") ?? string.Empty).Trim();
                if (parameterInput.Length == 0)
                    throw new ArgumentException($"The update for element {elementId} needs a parameter name.");
                if (!item.TryGetProperty("value", out var value))
                    throw new ArgumentException($"The update for element {elementId} ('{parameterInput}') needs a value.");

                BuiltInParameter? builtIn = null;
                Guid? sharedGuid = null;
                if (parameterInput.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                {
                    sharedGuid = Guid.TryParse(parameterInput["guid:".Length..], out var guid)
                        ? guid
                        : throw new ArgumentException($"Invalid shared parameter guid: {parameterInput}");
                }
                else
                {
                    string enumName = parameterInput.StartsWith("BuiltInParameter.", StringComparison.OrdinalIgnoreCase)
                        ? parameterInput["BuiltInParameter.".Length..]
                        : parameterInput;
                    // BuiltInParameter names are SHOUTY_SNAKE_CASE; require an underscore or
                    // all-caps so plain display names like "Comments" never collide.
                    bool looksLikeEnumName = enumName.Length > 0 && char.IsLetter(enumName[0]) && !enumName.Contains(' ')
                        && (enumName.Contains('_') || enumName.All(c => !char.IsLetter(c) || char.IsUpper(c)));
                    if (looksLikeEnumName && Enum.TryParse<BuiltInParameter>(enumName, true, out var parsed) && parsed != BuiltInParameter.INVALID)
                        builtIn = parsed;
                }

                updates.Add(new Update
                {
                    ElementId = elementId,
                    ParameterInput = parameterInput,
                    Value = value,
                    Unit = JsonArgs.GetString(item, "unit"),
                    BuiltIn = builtIn,
                    SharedGuid = sharedGuid,
                });
            }

            if (updates.Count == 0)
                throw new ArgumentException("updates must contain at least one update.");
            if (updates.Count > MaxUpdates)
                throw new ArgumentException($"Too many updates ({updates.Count}); max {MaxUpdates} per call. Apply them in batches.");
            return updates;
        }
    }
}
