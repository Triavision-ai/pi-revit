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
        public IReadOnlyList<string> DocumentKinds => DocumentKind.Both;
        public IReadOnlyList<string> Keywords => new[] { "parameter", "set value", "edit", "change value", "rename", "mark", "comments", "bulk edit", "update" };
        public IReadOnlyList<ToolLimit> Limits => new[]
        {
            new ToolLimit("Changing an element's type", "tool", "change_element_types"),
            new ToolLimit("Element properties that are not parameters, such as pinned state", "api", "Element.Pinned"),
            new ToolLimit("Moving or rotating elements", "tool", "transform_elements"),
            new ToolLimit("Family parameters, family types and formulas in a family document", "api", DocumentKind.FamilyApi),
        };
        public string? Verification => "reread";
        public string Label => "Set Parameters";
        public string Description => "Write parameter values on elements — also the home for rename: parameter 'Name' covers levels, views, sheets, types, etc. (falls back to the element's Name property when the Name parameter is read-only). Each update has its own subtransaction inside one model transaction. Default partial success commits valid updates; atomic=true rolls everything back if any update fails. preview=true validates commit inside a transaction group, then rolls the group back. Results include observed per-step before/after values and distinguish succeeded from proposed updates. parameter accepts a display name (Comments, Mark, Name), a BuiltInParameter enum name (e.g. ALL_MODEL_MARK), or guid:<GUID> for a shared parameter; type parameters live on the element type, so pass the type's id. Values are validated against the parameter's storage type; numeric values are interpreted in the document's display units for that parameter unless 'unit' (e.g. millimeters, feet, squareMeters) is given. Revit warnings raised at commit (e.g. duplicate Mark values) are auto-dismissed and listed in commitWarnings — mention them to the user; error-severity failures roll the whole transaction back.";
        public bool Write => true;

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                preview = new { type = "boolean", description = "Validate edits through Revit commit, then roll back the enclosing transaction group. Returns proposed before/after values; no model changes remain. Default false." },
                atomic = new { type = "boolean", description = "Roll back all updates if any fails. Default false preserves successful updates. Each update is isolated in a subtransaction." },
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
                expected_document = new
                {
                    type = "string",
                    description = "Optional safety check: title of the document these writes are meant for (as reported by get_model_overview). If the active document differs (user switched models), the call fails without changing anything.",
                },
            },
            required = new[] { "updates" },
        };

        public string? PromptSnippet => "Write element parameter values in bulk, including renames via the 'Name' parameter.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use set_parameters for parameter writes and renames (parameter 'Name'); pass 'unit' with numeric values unless the document's display units are intended.",
            "set_parameters defaults to partial success; use atomic=true for all-or-nothing batches and preview=true for commit-validated model rollback. Always inspect committed, proposed and failed.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();
            DocumentGuard.CheckExpectedDocument(args, doc);
            var updates = ParseUpdates(args);
            var steps = updates.Select(update => new ModelEditBatch.Step(
                new Dictionary<string, object?> { ["id"] = update.ElementId, ["parameter"] = update.ParameterInput },
                () =>
                {
                    var change = Apply(doc, update);
                    return new Dictionary<string, object?>
                    {
                        ["before"] = change.Before, ["after"] = change.After,
                        ["newDisplayValue"] = change.Display,
                    };
                })).ToArray();
            var result = ModelEditBatch.Run(doc, Name, args, steps);
            string summary = result.Preview
                ? $"Preview rolled back: {result.Proposed.Count} proposed update(s), {result.Failed.Count} failure(s)."
                : result.Committed
                    ? $"Applied {result.Succeeded.Count} parameter update(s); {result.Failed.Count} failed."
                    : $"No updates applied; {result.Failed.Count} failed. Batch rolled back.";
            return new ToolOutput(result.Payload, summary);
        }

        // ------------------------------------------------------------- application

        private sealed record ValueChange(string? Display, object? Before, object? After);

        /// <summary>Snapshots the concrete parameter or rename target actually written by this step.</summary>
        private static ValueChange Apply(Document doc, Update update)
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
                throw new InvalidOperationException($"Parameter '{update.ParameterInput}' not found on element {update.ElementId} ('{element.Name}'). Display names are localized in non-English Revit UIs — prefer the language-independent BuiltInParameter enum name (e.g. ALL_MODEL_INSTANCE_COMMENTS; get_element_details reports each parameter's builtInParameter). Type parameters live on the element type — pass the type's id instead.");
            }

            if (parameter.IsReadOnly)
            {
                if (nameTarget)
                    return SetElementName(element, update);
                throw new InvalidOperationException($"Parameter '{parameter.Definition?.Name}' is read-only on element {update.ElementId}.");
            }

            var before = GetElementDetails.DescribeParameter(doc, parameter, parameter.Definition?.Name ?? "", false);
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
            return new ValueChange(DescribeNewValue(parameter) ?? ValueAsString(update.Value), before,
                GetElementDetails.DescribeParameter(doc, parameter, parameter.Definition?.Name ?? "", false));
        }

        /// <summary>Rename fallback: the Element.Name property setter.</summary>
        private static ValueChange SetElementName(Element element, Update update)
        {
            string name = ValueAsString(update.Value);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Renaming needs a non-empty string value.");
            var before = new { value = element.Name, displayValue = element.Name, storageType = "String" };
            ElementNames.Assign(element, name);
            return new ValueChange(name, before, new { value = element.Name, displayValue = element.Name, storageType = "String" });
        }

        /// <summary>
        /// Shared resolution policy (inv:parameter-ambiguity): a display name matching several
        /// parameters on the element fails this update with the candidates' exact identities
        /// instead of writing to an arbitrary one. Missing stays null.
        /// </summary>
        private static Parameter? FindParameter(Element element, Update update)
            => ParameterResolver.FindSingle(element, new ParameterReference(update.ParameterInput, update.BuiltIn, update.SharedGuid));

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

                var reference = ParameterReference.Parse(parameterInput);

                updates.Add(new Update
                {
                    ElementId = elementId,
                    ParameterInput = parameterInput,
                    Value = value,
                    Unit = JsonArgs.GetString(item, "unit"),
                    BuiltIn = reference.BuiltIn,
                    SharedGuid = reference.SharedGuid,
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
