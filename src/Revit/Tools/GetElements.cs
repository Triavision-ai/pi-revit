using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// The generic FilteredElementCollector primitive: category/class/level/type/view
    /// scoping plus parameter filter rules. Rules run inside Revit's collector via
    /// ElementParameterFilter where the API supports it; regex rules (and rules the
    /// collector cannot quick-filter) are evaluated as a post-collector scan over the
    /// already-narrowed set.
    /// </summary>
    internal sealed class GetElements : ITool
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;
        private const int ProbeSize = 50;
        private const double Epsilon = 1e-6;

        public string Name => "get_elements";
        public IReadOnlyList<string> DocumentKinds => DocumentKind.Both;
        public IReadOnlyList<string> Keywords => new[] { "list", "find", "query", "count", "filter", "search elements", "how many" };
        public IReadOnlyList<ToolLimit> Limits => new[]
        {
            new ToolLimit("Elements inside linked models", "tool", "get_linked_elements"),
            new ToolLimit("Grouped counts or statistics over a whole scope", "tool", "summarize_elements"),
            new ToolLimit("Spatial containment or intersection", "tool", "query_spatial_elements"),
        };
        public string Label => "Get Elements";
        public string Description => "Query Revit elements: scope by category (display name like 'Walls' or enum name like 'OST_Walls'), element class, level, type id, or active view; filter by parameter rules; paginate with offset/limit or just count with count_only. Returns identity fields and optional parameter_names projections; include_type_parameters also reads the requested values from each type. Rules with an explicit BuiltInParameter or shared GUID can evaluate inside Revit's collector; display-name rules, regex rules, and unsupported quick filters run as a per-element scan. A rule reads the element's own parameter, or its type's when the instance has none. A display name can identify different parameters even within one category or class. Scope queries to reduce the scan, or use an explicit parameter identity when appropriate. Numeric rule values are interpreted in the document's display units for that parameter unless 'unit' is given.";

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                category = new
                {
                    type = "string",
                    description = "Category: BuiltInCategory enum name (e.g. OST_Walls) or display name (e.g. Walls, Doors, Rooms, Sheets, Views).",
                },
                of_class = new
                {
                    type = "string",
                    description = "Revit API element class, e.g. Wall, FamilyInstance, ViewSheet, Level, or a full name like Autodesk.Revit.DB.Mechanical.Duct.",
                },
                filter = new
                {
                    type = "object",
                    description = "Parameter filter. Numeric rule values are read in the document's display units for that parameter unless 'unit' is set.",
                    properties = new
                    {
                        match = new
                        {
                            type = "string",
                            @enum = new[] { "all", "any" },
                            description = "Combine rules with AND (all, default) or OR (any).",
                        },
                        rules = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    param = new { type = "string", description = "Parameter display name, BuiltInParameter enum name (e.g. WALL_BASE_OFFSET), or guid:<GUID> for a shared parameter." },
                                    op = new { type = "string", @enum = new[] { "equals", "not_equals", "greater", "greater_or_equal", "less", "less_or_equal", "contains", "is_empty", "is_not_empty", "regex" } },
                                    value = new { description = "Comparison value (string, number, or bool). Regex pattern for op=regex. Unused for is_empty/is_not_empty." },
                                    unit = new { type = "string", description = "Unit of a numeric value, e.g. millimeters, meters, feet, squareMeters. Default: the document's display units." },
                                },
                                required = new[] { "param", "op" },
                            },
                        },
                    },
                    required = new[] { "rules" },
                },
                level = new { type = "string", description = "Only elements hosted on this level (level name or id)." },
                type_id = new { type = "integer", description = "Only instances of this element type id (see get_element_types)." },
                in_active_view = new { type = "boolean", description = "Only elements visible in the active view. Default false." },
                count_only = new { type = "boolean", description = "Return only total_count, no element rows. Default false." },
                parameter_names = new
                {
                    type = "array", maxItems = 20, items = new { type = "string" },
                    description = "Optional parameter projections for each returned element: display names, BuiltInParameter names or guid:<GUID>. Every matching parameter is reported, including missing/ambiguous matches. Numeric values use internal units with separate display values. Ignored by count_only.",
                },
                include_type_parameters = new { type = "boolean", description = "Also project requested parameters from each element's type. Default false." },
                fields = new
                {
                    type = "array",
                    items = new { type = "string", @enum = new[] { "id", "name", "category", "typeName", "levelId" } },
                    description = "Identity fields per row; default all five. id is always included. Use parameter_names for optional values alongside these fields.",
                },
                offset = new { type = "integer", description = "Pagination offset. Default 0." },
                limit = new { type = "integer", description = "Max rows to return (1-1000). Default 200." },
            },
            required = Array.Empty<string>(),
        };

        public string? PromptSnippet => "Query or count Revit elements of any category with parameter filters and pagination.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use get_elements to list or count elements of any category or class (walls, doors, rooms, sheets, views, ...); use the returned ids with the other Revit tools.",
            "get_elements can project up to 20 parameter_names per row; use get_element_details for full element inspection.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();

            string? categoryInput = JsonArgs.GetString(args, "category");
            string? classInput = JsonArgs.GetString(args, "of_class");
            bool inActiveView = JsonArgs.GetBool(args, "in_active_view", false);
            bool countOnly = JsonArgs.GetBool(args, "count_only", false)
                || string.Equals(JsonArgs.GetString(args, "mode"), "count", StringComparison.OrdinalIgnoreCase);
            int offset = Math.Max(0, JsonArgs.GetInt(args, "offset", 0));
            int limit = Math.Clamp(JsonArgs.GetInt(args, "limit", DefaultLimit), 1, MaxLimit);
            var fields = ResolveFields(args);
            var parameterNames = JsonArgs.GetStringArray(args, "parameter_names");
            if (parameterNames?.Count > 20) throw new ArgumentException("parameter_names allows at most 20 parameter identities.");
            bool includeTypeParameters = JsonArgs.GetBool(args, "include_type_parameters", false);

            ElementId? categoryId = string.IsNullOrWhiteSpace(categoryInput) ? null : CategoryResolver.Resolve(doc, categoryInput);
            Type? elementClass = string.IsNullOrWhiteSpace(classInput) ? null : ElementClassResolver.Resolve(classInput);
            if (elementClass != null && typeof(ElementType).IsAssignableFrom(elementClass))
                throw new ArgumentException($"of_class {classInput!.Trim()} is an element type class, and get_elements lists placed elements only (types are excluded, so this would always be 0). Use get_element_types with of_class {classInput.Trim()} (or a category) to list types, then pass a type id as type_id here.");
            ElementId? levelId = ResolveLevel(doc, args);
            long? typeId = JsonArgs.GetLong(args, "type_id");
            // Type ids belong to one model: a host type id against a linked model (or any id that is
            // not a type here) used to match nothing, silently.
            if (typeId is { } requestedType && doc.GetElement(new ElementId(requestedType)) is not ElementType)
                throw new ArgumentException($"type_id {requestedType} is not an element type in {(doc.IsLinked ? "this linked model" : "this document")}. Type ids differ between models: "
                    + (doc.IsLinked ? "use a type id taken from this linked model (for example a linked element's typeId)." : "find type ids with get_element_types."));
            ElementId? activeViewId = null;
            if (inActiveView)
            {
                activeViewId = doc.ActiveView?.Id
                    ?? throw new ArgumentException("in_active_view requires an active graphical view in Revit.");
            }

            FilteredElementCollector CreateBaseCollector()
            {
                var collector = activeViewId != null
                    ? new FilteredElementCollector(doc, activeViewId)
                    : new FilteredElementCollector(doc);
                if (categoryId != null)
                    collector = collector.OfCategoryId(categoryId);
                if (elementClass != null)
                    collector = ElementClassResolver.OfClass(collector, elementClass);
                collector = collector.WhereElementIsNotElementType();
                if (levelId != null)
                    collector = collector.WherePasses(new ElementLevelFilter(levelId));
                if (typeId is { } type)
                {
                    collector = collector.WherePasses(new ElementParameterFilter(
                        ParameterFilterRuleFactory.CreateEqualsRule(new ElementId(BuiltInParameter.ELEM_TYPE_PARAM), new ElementId(type))));
                }
                return collector;
            }

            var (quickFilter, postPredicate, filterWarnings, stickyWarnings) = BuildParameterFilter(doc, args, CreateBaseCollector);

            FilteredElementCollector CreateCollector()
            {
                var collector = CreateBaseCollector();
                return quickFilter != null ? collector.WherePasses(quickFilter) : collector;
            }

            string scope = DescribeScope(categoryInput, classInput, inActiveView);

            // View templates are views to the collector but not to get_model_overview, so a view
            // scope reports how many of its matches are templates (counts themselves stay as is).
            bool viewScope = categoryId?.Value == (long)BuiltInCategory.OST_Views
                || (elementClass != null && typeof(View).IsAssignableFrom(elementClass));
            int templates = 0;

            if (countOnly && postPredicate is null)
            {
                int quickCount = CreateCollector().GetElementCount();
                if (viewScope && quickCount > 0)
                    templates = CreateCollector().OfType<View>().Count(view => view.IsTemplate);
                return CountResult(scope, quickCount, WithViewTemplateNote(quickCount > 0 ? stickyWarnings : filterWarnings, templates));
            }

            var rows = countOnly ? null : new List<Dictionary<string, object?>>(Math.Min(limit, 256));
            int total = 0;
            foreach (Element element in CreateCollector())
            {
                if (postPredicate != null && !postPredicate(element))
                    continue;
                if (rows != null && total >= offset && rows.Count < limit)
                {
                    var row = ElementIdentity.Build(doc, element, fields);
                    if (parameterNames is { Count: > 0 })
                        row["parameters"] = GetElementDetails.ProjectParameters(doc, element, parameterNames, includeTypeParameters);
                    rows.Add(row);
                }
                total++;
                if (viewScope && element is View { IsTemplate: true })
                    templates++;
            }

            // A found match proves most rules worked: a not-found-on-probes warning is
            // load-bearing only next to a zero, where it distinguishes 'unknown parameter
            // name' from 'no matching elements'. is_empty warnings stay, because a missing
            // parameter can itself produce the matches (inv:missing-not-silent).
            if (total > 0)
                filterWarnings = stickyWarnings;
            filterWarnings = WithViewTemplateNote(filterWarnings, templates);

            if (rows is null)
                return CountResult(scope, total, filterWarnings);

            bool hasMore = offset + rows.Count < total;
            int? nextOffset = hasMore ? offset + rows.Count : null;

            string sampleText = string.Empty;
            if (rows.Count > 0 && fields.Contains("name"))
            {
                var sample = rows.Take(3).Select(row => $"'{row["name"]}' ({row["id"]})");
                sampleText = $" Sample: {string.Join(", ", sample)}.";
            }
            string compact = (hasMore
                ? $"{scope}: {total} total, returned {rows.Count} at offset {offset}, has_more (next_offset {nextOffset}).{sampleText}"
                : $"{scope}: {total} total, returned {rows.Count} at offset {offset}.{sampleText}")
                + WarningSuffix(filterWarnings);

            return new ToolOutput(new
            {
                total_count = total,
                returned_count = rows.Count,
                offset,
                has_more = hasMore,
                next_offset = nextOffset,
                elements = rows,
                warnings = filterWarnings.Count > 0 ? filterWarnings : null,
            }, compact);
        }

        private static ToolOutput CountResult(string scope, int count, IReadOnlyList<string> warnings)
            => new(new { total_count = count, count_only = true, warnings = warnings.Count > 0 ? warnings : null },
                $"{scope}: {count} elements match." + WarningSuffix(warnings));

        private static IReadOnlyList<string> WithViewTemplateNote(IReadOnlyList<string> warnings, int templates)
            => templates == 0
                ? warnings
                : warnings.Append($"Includes {templates} view template(s) (listed rows carry traits.view_template). get_model_overview counts views without templates and sheets, so its view count is lower.").ToList();

        private static string WarningSuffix(IReadOnlyList<string> warnings)
            => warnings.Count > 0 ? " WARNING: " + string.Join(" ", warnings) : string.Empty;

        private static string DescribeScope(string? category, string? className, bool inActiveView)
        {
            string scope = !string.IsNullOrWhiteSpace(category) ? category.Trim()
                : !string.IsNullOrWhiteSpace(className) ? className.Trim()
                : "elements";
            return inActiveView ? scope + " in active view" : scope;
        }

        private static IReadOnlyList<string> ResolveFields(JsonElement args)
        {
            var requested = JsonArgs.GetStringArray(args, "fields");
            if (requested is null || requested.Count == 0)
                return ElementIdentity.Fields;

            foreach (string field in requested)
            {
                if (!ElementIdentity.Fields.Any(known => string.Equals(known, field, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Unknown field: {field}. Identity fields only: {string.Join(", ", ElementIdentity.Fields)}. Read parameter values with get_element_details.");
            }

            var canonical = new List<string> { "id" };
            foreach (string field in ElementIdentity.Fields)
            {
                if (field != "id" && requested.Any(r => string.Equals(r, field, StringComparison.OrdinalIgnoreCase)))
                    canonical.Add(field);
            }
            return canonical;
        }

        private static ElementId? ResolveLevel(Document doc, JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("level", out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long fromNumber))
                return EnsureLevel(doc, new ElementId(fromNumber));
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()?.Trim() ?? string.Empty;
                if (text.Length == 0)
                    return null;

                // A name match wins over an id reading: a level may be named "100".
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                var match = levels.FirstOrDefault(level => string.Equals(level.Name, text, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match.Id;
                if (long.TryParse(text, out long fromString))
                    return EnsureLevel(doc, new ElementId(fromString));
                throw new ArgumentException($"Unknown level: {text}. Available levels: {string.Join(", ", levels.OrderBy(l => l.Elevation).Select(l => l.Name))}");
            }
            throw new ArgumentException("level must be a level name or a level id.");
        }

        private static ElementId EnsureLevel(Document doc, ElementId id)
            => doc.GetElement(id) is Level
                ? id
                : throw new ArgumentException($"Element {id.Value} is not a Level.");

        // ---------------------------------------------------------------- filters

        private enum RuleOp { Equals, NotEquals, Greater, GreaterOrEqual, Less, LessOrEqual, Contains, IsEmpty, IsNotEmpty, Regex }

        private sealed class Rule
        {
            public required string ParamInput { get; init; }
            public required RuleOp Op { get; init; }
            public JsonElement Value { get; init; }
            public string? Unit { get; init; }
            public required ParameterReference Reference { get; init; }
            public BuiltInParameter? BuiltIn { get; init; }
            public Guid? SharedGuid { get; init; }
            public Regex? CompiledRegex { get; init; }
            public FilterRule? QuickRule { get; set; }
            /// <summary>Type-level parameter per type id, for this query only: many elements share a type, and a
            /// post-scan looked the same type parameter up again for every element.</summary>
            public Dictionary<long, Parameter?> TypeParameters { get; } = new();
        }

        private static (ElementFilter? Quick, Func<Element, bool>? Post, IReadOnlyList<string> Warnings, IReadOnlyList<string> StickyWarnings) BuildParameterFilter(
            Document doc, JsonElement args, Func<FilteredElementCollector> createBaseCollector)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("filter", out var filterElement) || filterElement.ValueKind == JsonValueKind.Null)
                return (null, null, Array.Empty<string>(), Array.Empty<string>());
            if (filterElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("filter must be an object: { match?: 'all'|'any', rules: [{ param, op, value?, unit? }] }.");

            bool matchAny = string.Equals(JsonArgs.GetString(filterElement, "match"), "any", StringComparison.OrdinalIgnoreCase);
            if (!filterElement.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("filter.rules must be an array of { param, op, value?, unit? } rules.");

            var rules = rulesElement.EnumerateArray().Select(ParseRule).ToList();
            if (rules.Count == 0)
                return (null, null, Array.Empty<string>(), Array.Empty<string>());

            // Probe a few in-scope elements for missing-name diagnostics and to find
            // storage/spec exemplars for explicit parameter identities. A sample must
            // never turn a display name into an assumed globally uniform identity.
            var probes = new List<Element>(ProbeSize);
            foreach (Element element in createBaseCollector())
            {
                probes.Add(element);
                if (probes.Count >= ProbeSize)
                    break;
            }

            // A rule whose parameter no probed element (nor its type) carries is the localized-UI
            // trap: the post-scan then matches nothing and the result reads "0 total" -- which the
            // caller cannot tell apart from a genuine no-match. Typical case: an English display
            // name queried against a non-English UI, where the same parameter carries a translated
            // name. An explicit identity can miss too: it may simply not exist on this kind of element.
            // A warning (not an error: the parameter may exist past the probe window,
            // and is_empty legitimately matches missing parameters) makes the zero honest.
            var warnings = new List<string>();
            var stickyWarnings = new List<string>();
            if (probes.Count > 0)
            {
                foreach (var rule in rules)
                {
                    // Sticky: the matches that do exist are the incomplete ones.
                    if (HiddenBuiltInWarning(probes, rule) is { } hidden)
                    {
                        warnings.Add(hidden);
                        stickyWarnings.Add(hidden);
                    }
                    if (!probes.All(probe => FindEffectiveParameter(probe, rule) is null))
                        continue;
                    string searched = $"on any of the {probes.Count} probed element(s) in scope or their types";
                    if (rule.Op == RuleOp.IsEmpty)
                    {
                        // inv:missing-not-silent: is_empty matches elements that lack the parameter,
                        // so a misspelled or localized name matches the whole scope. Keep this warning.
                        string sticky = $"Filter parameter '{rule.ParamInput}' was not found {searched}. "
                            + "is_empty also matches elements that do not have the parameter at all, so these results may be elements without it "
                            + "rather than elements with an empty value. "
                            + (rule.Reference.IsDisplayName
                                ? "Check the name, or use the language-independent BuiltInParameter name or guid:<GUID> "
                                    + "(get_element_details reports builtInParameter per parameter)."
                                : "Check that the parameter applies to these elements (get_element_details reports builtInParameter and guid per parameter).");
                        warnings.Add(sticky);
                        stickyWarnings.Add(sticky);
                    }
                    else
                        warnings.Add(
                            $"Filter parameter '{rule.ParamInput}' was not found {searched}, "
                            + (rule.Reference.IsDisplayName
                                ? "so a 0-match result may mean 'unknown parameter name', not 'no matching elements'. Display names are "
                                    + "localized in non-English Revit UIs -- prefer the language-independent BuiltInParameter enum name "
                                    + "(get_element_details reports it as builtInParameter per parameter)."
                                : "so a 0-match result may mean 'the parameter does not apply to these elements', not 'no matching elements' "
                                    + "(get_element_details reports builtInParameter and guid per parameter)."));
                }
            }

            foreach (var rule in rules)
            {
                if (rule.Op is RuleOp.Regex or RuleOp.IsEmpty or RuleOp.IsNotEmpty)
                    continue; // always post-scan (missing-parameter semantics)
                // Category/class scope and unanimous samples do not establish a
                // uniform parameter identity across later families. Resolve display
                // names per element; only explicit identities may use a quick rule.
                if (rule.BuiltIn is null && rule.SharedGuid is null)
                    continue;
                // Instance lookup on purpose: when no probe instance carries the parameter it
                // lives on the types only, and the type-aware post-scan must run (no quick rule).
                var found = probes.Select(probe => FindParameter(probe, rule)).Where(parameter => parameter != null).ToList();
                if (found.Count == 0)
                    continue;
                rule.QuickRule = TryBuildQuickRule(doc, rule, found[0]!);
            }

            var quickRules = rules.Where(rule => rule.QuickRule != null).ToList();
            var postRules = rules.Where(rule => rule.QuickRule is null).ToList();

            if (matchAny)
            {
                // OR with any post-scan rule means everything must be post-scanned: a
                // collector-level OR filter would wrongly exclude post-rule-only matches.
                // Every warning is sticky here: under OR a match proves only that some
                // rule worked, so it cannot clear another rule's not-found-on-probes warning.
                if (postRules.Count > 0)
                    return (null, element => rules.Any(rule => EvaluatePost(doc, element, rule)), warnings, warnings);

                var filters = quickRules.Select(rule => (ElementFilter)new ElementParameterFilter(rule.QuickRule!)).ToList();
                return (filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters), null, warnings, stickyWarnings);
            }

            ElementFilter? quick = quickRules.Count > 0
                ? new ElementParameterFilter(quickRules.Select(rule => rule.QuickRule!).ToList())
                : null;
            Func<Element, bool>? post = postRules.Count > 0
                ? element => postRules.All(rule => EvaluatePost(doc, element, rule))
                : null;
            return (quick, post, warnings, stickyWarnings);
        }

        private static Rule ParseRule(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Each filter rule must be an object with param and op.");

            string param = JsonArgs.GetString(element, "param")?.Trim()
                ?? throw new ArgumentException("Each filter rule needs a param name.");
            string opText = JsonArgs.GetString(element, "op")
                ?? throw new ArgumentException($"Filter rule on '{param}' needs an op.");

            RuleOp op = opText.Trim().ToLowerInvariant() switch
            {
                "equals" or "eq" => RuleOp.Equals,
                "not_equals" or "ne" or "neq" => RuleOp.NotEquals,
                "greater" or "gt" => RuleOp.Greater,
                "greater_or_equal" or "gte" or "ge" => RuleOp.GreaterOrEqual,
                "less" or "lt" => RuleOp.Less,
                "less_or_equal" or "lte" or "le" => RuleOp.LessOrEqual,
                "contains" => RuleOp.Contains,
                "is_empty" => RuleOp.IsEmpty,
                "is_not_empty" => RuleOp.IsNotEmpty,
                "regex" => RuleOp.Regex,
                _ => throw new ArgumentException($"Unknown filter op: {opText}. Supported: equals, not_equals, greater, greater_or_equal, less, less_or_equal, contains, is_empty, is_not_empty, regex."),
            };

            bool hasValue = element.TryGetProperty("value", out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (!hasValue && op is not (RuleOp.IsEmpty or RuleOp.IsNotEmpty))
                throw new ArgumentException($"Filter rule on '{param}' with op {opText} needs a value.");

            var reference = ParameterReference.Parse(param);

            Regex? regex = null;
            if (op == RuleOp.Regex)
            {
                string pattern = ValueAsString(value);
                try
                {
                    regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"Invalid regex for '{param}': {ex.Message}");
                }
            }

            return new Rule
            {
                ParamInput = param,
                Op = op,
                Value = hasValue ? value : default,
                Unit = JsonArgs.GetString(element, "unit"),
                Reference = reference,
                BuiltIn = reference.BuiltIn,
                SharedGuid = reference.SharedGuid,
                CompiledRegex = regex,
            };
        }

        /// <summary>Shared resolution policy: missing is null; an ambiguous display name throws with candidates.</summary>
        private static Parameter? FindParameter(Element element, Rule rule) => ParameterResolver.FindSingle(element, rule.Reference);

        /// <summary>The parameter whose value a rule sees: the instance parameter when it carries a
        /// value, otherwise the same reference on the element's type, read from the element's own
        /// document (a linked element's type lives in the linked model, not the host). When neither
        /// carries a value the instance parameter wins; null means the parameter exists on neither.</summary>
        private static Parameter? FindEffectiveParameter(Element element, Rule rule)
        {
            var own = FindParameter(element, rule);
            if (own != null && !HasNoValue(own))
                return own;
            var typeId = element.GetTypeId();
            if (typeId == ElementId.InvalidElementId)
                return own;
            if (!rule.TypeParameters.TryGetValue(typeId.Value, out var fromType))
            {
                var type = element.Document.GetElement(typeId);
                fromType = type is null ? null : FindParameter(type, rule);
                rule.TypeParameters[typeId.Value] = fromType;
            }
            return fromType != null && !HasNoValue(fromType) ? fromType : (own ?? fromType);
        }

        // Display label -> built-in parameters carrying it, built once per session (the UI language
        // is fixed while Revit runs) and only when a display-name rule misses a probed element.
        private static readonly Lazy<IReadOnlyDictionary<string, List<BuiltInParameter>>> BuiltInByLabel = new(() =>
        {
            var map = new Dictionary<string, List<BuiltInParameter>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in Enum.GetValues<BuiltInParameter>().Distinct())
            {
                if (id == BuiltInParameter.INVALID)
                    continue;
                string label;
                try
                {
                    label = LabelUtils.GetLabelFor(id);
                }
                catch
                {
                    continue; // not every built-in parameter has a label
                }
                if (string.IsNullOrWhiteSpace(label))
                    continue;
                if (!map.TryGetValue(label, out var ids))
                    map[label] = ids = new List<BuiltInParameter>();
                ids.Add(id);
            }
            return map;
        });

        /// <summary>Revit lists some built-in parameters in Element.Parameters on only some elements
        /// (e.g. a window's sill height is missing on curtain-wall, roof-hosted and work-plane windows)
        /// while get_Parameter(builtIn) still returns them, so a display-name rule silently skips
        /// those elements. Warns, from the probes only, when a display name is a built-in label and
        /// that happens; which elements match is not changed.</summary>
        private static string? HiddenBuiltInWarning(IReadOnlyList<Element> probes, Rule rule)
        {
            if (!rule.Reference.IsDisplayName)
                return null;
            var missing = probes.Where(probe => FindEffectiveParameter(probe, rule) is null).ToList();
            if (missing.Count == 0 || !BuiltInByLabel.Value.TryGetValue(rule.Reference.Input, out var candidates))
                return null;
            var hidden = candidates
                .Select(id => (Id: id, Count: missing.Count(probe => probe.get_Parameter(id) is { } parameter && !HasNoValue(parameter))))
                .Where(candidate => candidate.Count > 0)
                .OrderByDescending(candidate => candidate.Count)
                .FirstOrDefault();
            return hidden.Count == 0
                ? null
                : $"Revit does not list '{rule.ParamInput}' by name on {hidden.Count} of the {probes.Count} probed element(s), although {hidden.Id} returns a value for them, "
                    + $"so this display-name rule skips those elements and its matches are incomplete. Use the BuiltInParameter name {hidden.Id} instead to include them.";
        }

        /// <summary>No value set, or an empty string: what is_empty reports as empty.</summary>
        private static bool HasNoValue(Parameter parameter)
            => !parameter.HasValue || (parameter.StorageType == StorageType.String && string.IsNullOrEmpty(parameter.AsString()));

        /// <summary>Builds a collector-level FilterRule, or null when the op/storage pair must post-scan.</summary>
        private static FilterRule? TryBuildQuickRule(Document doc, Rule rule, Parameter exemplar)
        {
            ElementId paramId = exemplar.Id;
            switch (exemplar.StorageType)
            {
                case StorageType.String:
                {
                    string target = ValueAsString(rule.Value);
                    return rule.Op switch
                    {
                        RuleOp.Equals => ParameterFilterRuleFactory.CreateEqualsRule(paramId, target),
                        RuleOp.NotEquals => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, target),
                        RuleOp.Contains => ParameterFilterRuleFactory.CreateContainsRule(paramId, target),
                        RuleOp.Greater => ParameterFilterRuleFactory.CreateGreaterRule(paramId, target),
                        RuleOp.GreaterOrEqual => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(paramId, target),
                        RuleOp.Less => ParameterFilterRuleFactory.CreateLessRule(paramId, target),
                        RuleOp.LessOrEqual => ParameterFilterRuleFactory.CreateLessOrEqualRule(paramId, target),
                        _ => null,
                    };
                }
                case StorageType.Double:
                {
                    if (rule.Op == RuleOp.Contains)
                        return null; // post-scan over the display string
                    if (!TryValueAsDouble(rule.Value, out double raw))
                        throw new ArgumentException($"Filter rule on numeric parameter '{exemplar.Definition?.Name}' needs a numeric value (got {rule.Value.GetRawText()}).");
                    double target = ToInternalUnits(doc, rule, exemplar, raw);
                    return rule.Op switch
                    {
                        RuleOp.Equals => ParameterFilterRuleFactory.CreateEqualsRule(paramId, target, Epsilon),
                        RuleOp.NotEquals => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, target, Epsilon),
                        RuleOp.Greater => ParameterFilterRuleFactory.CreateGreaterRule(paramId, target, Epsilon),
                        RuleOp.GreaterOrEqual => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(paramId, target, Epsilon),
                        RuleOp.Less => ParameterFilterRuleFactory.CreateLessRule(paramId, target, Epsilon),
                        RuleOp.LessOrEqual => ParameterFilterRuleFactory.CreateLessOrEqualRule(paramId, target, Epsilon),
                        _ => null,
                    };
                }
                case StorageType.Integer:
                {
                    if (rule.Op == RuleOp.Contains)
                        return null;
                    if (!TryValueAsInt(rule.Value, out int target))
                        throw new ArgumentException($"Filter rule on integer parameter '{exemplar.Definition?.Name}' needs an integer or boolean value.");
                    return rule.Op switch
                    {
                        RuleOp.Equals => ParameterFilterRuleFactory.CreateEqualsRule(paramId, target),
                        RuleOp.NotEquals => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, target),
                        RuleOp.Greater => ParameterFilterRuleFactory.CreateGreaterRule(paramId, target),
                        RuleOp.GreaterOrEqual => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(paramId, target),
                        RuleOp.Less => ParameterFilterRuleFactory.CreateLessRule(paramId, target),
                        RuleOp.LessOrEqual => ParameterFilterRuleFactory.CreateLessOrEqualRule(paramId, target),
                        _ => null,
                    };
                }
                case StorageType.ElementId:
                {
                    if (rule.Op is RuleOp.Equals or RuleOp.NotEquals && TryValueAsLong(rule.Value, out long id))
                    {
                        return rule.Op == RuleOp.Equals
                            ? ParameterFilterRuleFactory.CreateEqualsRule(paramId, new ElementId(id))
                            : ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, new ElementId(id));
                    }
                    return null; // non-numeric values compare against the display string post-scan
                }
                default:
                    return null;
            }
        }

        /// <summary>Per-element evaluation for rules the collector cannot quick-filter.
        /// A value that does not fit the parameter's storage type is a caller mistake, and
        /// must fail the same way it does on the quick path (TryBuildQuickRule) — otherwise
        /// the identical query reports a clear error when scoped and a silent zero matches
        /// when unscoped.</summary>
        private static bool EvaluatePost(Document doc, Element element, Rule rule)
        {
            var parameter = FindEffectiveParameter(element, rule);
            bool empty = parameter is null || HasNoValue(parameter);
            if (rule.Op == RuleOp.IsEmpty)
                return empty;
            if (rule.Op == RuleOp.IsNotEmpty)
                return !empty;
            if (parameter is null)
                return false;

            if (rule.Op == RuleOp.Regex)
            {
                try
                {
                    return rule.CompiledRegex!.IsMatch(ComparableText(doc, parameter));
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }

            switch (parameter.StorageType)
            {
                case StorageType.String:
                {
                    // Case-insensitive to match Revit's own collector: ParameterFilterRuleFactory
                    // string rules ignore case (verified on Revit 2025 -- a promoted equals rule
                    // matched 'CaseProbe' for the query 'caseprobe'). The same rule must not turn
                    // case-sensitive merely because it fell back to the post-scan path.
                    string actual = parameter.AsString() ?? string.Empty;
                    string target = ValueAsString(rule.Value);
                    return rule.Op switch
                    {
                        RuleOp.Equals => string.Equals(actual, target, StringComparison.OrdinalIgnoreCase),
                        RuleOp.NotEquals => !string.Equals(actual, target, StringComparison.OrdinalIgnoreCase),
                        RuleOp.Contains => actual.Contains(target, StringComparison.OrdinalIgnoreCase),
                        _ => Compare(string.Compare(actual, target, StringComparison.OrdinalIgnoreCase), rule.Op),
                    };
                }
                case StorageType.Double:
                {
                    if (rule.Op == RuleOp.Contains)
                        return ComparableText(doc, parameter).Contains(ValueAsString(rule.Value), StringComparison.Ordinal);
                    if (!TryValueAsDouble(rule.Value, out double raw))
                        throw new ArgumentException($"Filter rule on numeric parameter '{parameter.Definition?.Name}' needs a numeric value (got {rule.Value.GetRawText()}).");
                    double target = ToInternalUnits(doc, rule, parameter, raw);
                    // Same tolerance semantics as the collector's CreateXxxRule(id, value, Epsilon)
                    // (RevitAPI.xml: a value within epsilon of the target counts as equal, so it
                    // is not greater/less but does satisfy greater_or_equal/less_or_equal); exact
                    // CompareTo here would make one rule count differently scoped vs unscoped.
                    double delta = parameter.AsDouble() - target;
                    return rule.Op switch
                    {
                        RuleOp.Equals => Math.Abs(delta) <= Epsilon,
                        RuleOp.NotEquals => Math.Abs(delta) > Epsilon,
                        RuleOp.Greater => delta > Epsilon,
                        RuleOp.GreaterOrEqual => delta >= -Epsilon,
                        RuleOp.Less => delta < -Epsilon,
                        RuleOp.LessOrEqual => delta <= Epsilon,
                        _ => false,
                    };
                }
                case StorageType.Integer:
                {
                    if (rule.Op == RuleOp.Contains)
                        return ComparableText(doc, parameter).Contains(ValueAsString(rule.Value), StringComparison.Ordinal);
                    if (!TryValueAsInt(rule.Value, out int target))
                        throw new ArgumentException($"Filter rule on integer parameter '{parameter.Definition?.Name}' needs an integer or boolean value.");
                    return Compare(parameter.AsInteger().CompareTo(target), rule.Op);
                }
                case StorageType.ElementId:
                {
                    // contains is always a text op (a digit value like "1" is a substring of
                    // the element name, not an id); the other ops read a numeric value as an id.
                    long actualId = parameter.AsElementId()?.Value ?? -1;
                    if (rule.Op != RuleOp.Contains && TryValueAsLong(rule.Value, out long targetId))
                        return Compare(actualId.CompareTo(targetId), rule.Op);

                    // Name text is case-insensitive like the String branch and the collector.
                    string actualText = ComparableText(doc, parameter);
                    string target = ValueAsString(rule.Value);
                    return rule.Op switch
                    {
                        RuleOp.Equals => string.Equals(actualText, target, StringComparison.OrdinalIgnoreCase),
                        RuleOp.NotEquals => !string.Equals(actualText, target, StringComparison.OrdinalIgnoreCase),
                        RuleOp.Contains => actualText.Contains(target, StringComparison.OrdinalIgnoreCase),
                        _ => false,
                    };
                }
                default:
                    return false;
            }
        }

        private static bool Compare(int comparison, RuleOp op) => op switch
        {
            RuleOp.Equals => comparison == 0,
            RuleOp.NotEquals => comparison != 0,
            RuleOp.Greater => comparison > 0,
            RuleOp.GreaterOrEqual => comparison >= 0,
            RuleOp.Less => comparison < 0,
            RuleOp.LessOrEqual => comparison <= 0,
            _ => false,
        };

        /// <summary>Converts a user-supplied numeric value to internal units using the
        /// explicit unit, or the document's display units for the parameter's spec.</summary>
        private static double ToInternalUnits(Document doc, Rule rule, Parameter parameter, double value)
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
                return value;

            ForgeTypeId unit;
            if (!string.IsNullOrWhiteSpace(rule.Unit))
            {
                unit = UnitResolver.Resolve(rule.Unit);
                if (!UnitUtils.IsValidUnit(spec, unit))
                    throw new ArgumentException($"Unit '{rule.Unit}' does not fit parameter '{parameter.Definition?.Name}' (spec {UnitResolver.ShortName(spec)}); the document displays it in {UnitResolver.ShortName(doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId())}.");
            }
            else
            {
                unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
            }
            return UnitUtils.ConvertToInternalUnits(value, unit);
        }

        /// <summary>String the user-facing ops compare/regex against: raw string for text
        /// parameters, otherwise the formatted display value.</summary>
        private static string ComparableText(Document doc, Parameter parameter)
        {
            if (parameter.StorageType == StorageType.String)
                return parameter.AsString() ?? string.Empty;

            string? display = null;
            try
            {
                display = parameter.AsValueString();
            }
            catch
            {
                // Fall through to the raw value below.
            }
            if (!string.IsNullOrEmpty(display))
                return display;

            return parameter.StorageType switch
            {
                StorageType.Double => parameter.AsDouble().ToString(CultureInfo.InvariantCulture),
                StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.ElementId => doc.GetElement(parameter.AsElementId())?.Name
                    ?? parameter.AsElementId()?.Value.ToString(CultureInfo.InvariantCulture)
                    ?? string.Empty,
                _ => string.Empty,
            };
        }

        private static string ValueAsString(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            _ => value.GetRawText(),
        };

        private static bool TryValueAsDouble(JsonElement value, out double result)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result))
                return true;
            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return true;
            result = 0;
            return false;
        }

        private static bool TryValueAsInt(JsonElement value, out int result)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result))
                return true;
            if (value.ValueKind == JsonValueKind.True)
            {
                result = 1;
                return true;
            }
            if (value.ValueKind == JsonValueKind.False)
            {
                result = 0;
                return true;
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()?.Trim() ?? string.Empty;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                    return true;
                if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    result = 1;
                    return true;
                }
                if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    result = 0;
                    return true;
                }
            }
            result = 0;
            return false;
        }

        private static bool TryValueAsLong(JsonElement value, out long result)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
                return true;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return true;
            result = 0;
            return false;
        }
    }
}
