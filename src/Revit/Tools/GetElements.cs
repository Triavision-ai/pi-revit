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
        public string Label => "Get Elements";
        public string Description => "Query Revit elements: scope by category (display name like 'Walls' or enum name like 'OST_Walls'), element class, level, type id, or active view; filter by parameter rules; paginate with offset/limit or just count with count_only. Returns identity fields only (id, name, category, typeName, levelId) — read parameter values with get_element_details. Most filter rules evaluate inside Revit's collector; 'regex' rules (and rules on parameters that cannot be quick-filtered) run as a slower post-collector scan. Prefer combining display-name filter rules with a category or of_class scope: the same display name (e.g. 'Width') can resolve to different parameters per category, which forces the slower per-element scan in unscoped queries. Numeric rule values are interpreted in the document's display units for that parameter unless 'unit' is given.";

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
                fields = new
                {
                    type = "array",
                    items = new { type = "string", @enum = new[] { "id", "name", "category", "typeName", "levelId" } },
                    description = "Identity fields per row; default all five. id is always included. Parameter values live in get_element_details.",
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
            "get_elements returns identity fields only; read parameter values with get_element_details.",
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

            ElementId? categoryId = string.IsNullOrWhiteSpace(categoryInput) ? null : CategoryResolver.Resolve(doc, categoryInput);
            Type? elementClass = string.IsNullOrWhiteSpace(classInput) ? null : ElementClassResolver.Resolve(classInput);
            ElementId? levelId = ResolveLevel(doc, args);
            long? typeId = JsonArgs.GetLong(args, "type_id");
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
                    collector = collector.OfClass(elementClass);
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

            var (quickFilter, postPredicate) = BuildParameterFilter(doc, args, CreateBaseCollector);

            FilteredElementCollector CreateCollector()
            {
                var collector = CreateBaseCollector();
                return quickFilter != null ? collector.WherePasses(quickFilter) : collector;
            }

            string scope = DescribeScope(categoryInput, classInput, inActiveView);

            if (countOnly && postPredicate is null)
                return CountResult(scope, CreateCollector().GetElementCount());

            var rows = countOnly ? null : new List<Dictionary<string, object?>>(Math.Min(limit, 256));
            int total = 0;
            foreach (Element element in CreateCollector())
            {
                if (postPredicate != null && !postPredicate(element))
                    continue;
                if (rows != null && total >= offset && rows.Count < limit)
                    rows.Add(ElementIdentity.Build(doc, element, fields));
                total++;
            }

            if (rows is null)
                return CountResult(scope, total);

            bool hasMore = offset + rows.Count < total;
            int? nextOffset = hasMore ? offset + rows.Count : null;

            string sampleText = string.Empty;
            if (rows.Count > 0 && fields.Contains("name"))
            {
                var sample = rows.Take(3).Select(row => $"'{row["name"]}' ({row["id"]})");
                sampleText = $" Sample: {string.Join(", ", sample)}.";
            }
            string compact = hasMore
                ? $"{scope}: {total} total, returned {rows.Count} at offset {offset}, has_more (next_offset {nextOffset}).{sampleText}"
                : $"{scope}: {total} total, returned {rows.Count} at offset {offset}.{sampleText}";

            return new ToolOutput(new
            {
                total_count = total,
                returned_count = rows.Count,
                offset,
                has_more = hasMore,
                next_offset = nextOffset,
                elements = rows,
            }, compact);
        }

        private static ToolOutput CountResult(string scope, int count)
            => new(new { total_count = count, count_only = true }, $"{scope}: {count} elements match.");

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
                if (long.TryParse(text, out long fromString))
                    return EnsureLevel(doc, new ElementId(fromString));

                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                var match = levels.FirstOrDefault(level => string.Equals(level.Name, text, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match.Id;
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
            public BuiltInParameter? BuiltIn { get; init; }
            public Guid? SharedGuid { get; init; }
            public Regex? CompiledRegex { get; init; }
            public FilterRule? QuickRule { get; set; }
        }

        private static (ElementFilter? Quick, Func<Element, bool>? Post) BuildParameterFilter(
            Document doc, JsonElement args, Func<FilteredElementCollector> createBaseCollector)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("filter", out var filterElement) || filterElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            bool matchAny = string.Equals(JsonArgs.GetString(filterElement, "match"), "any", StringComparison.OrdinalIgnoreCase);
            if (!filterElement.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("filter.rules must be an array of { param, op, value?, unit? } rules.");

            var rules = rulesElement.EnumerateArray().Select(ParseRule).ToList();
            if (rules.Count == 0)
                return (null, null);

            // Probe a few in-scope elements so display-name parameters resolve to ids
            // and value typing / unit conversion can use the parameter's storage + spec.
            var probes = new List<Element>(ProbeSize);
            foreach (Element element in createBaseCollector())
            {
                probes.Add(element);
                if (probes.Count >= ProbeSize)
                    break;
            }

            // Display-name promotion is only trustworthy inside one category/class: the
            // probe sees just the first ProbeSize elements in collector order, so in an
            // unscoped query a unanimous sample can still hide other categories further
            // on whose same-named parameter has a different id — and a pinned quick rule
            // would silently drop their matches.
            bool scoped = !string.IsNullOrWhiteSpace(JsonArgs.GetString(args, "category"))
                || !string.IsNullOrWhiteSpace(JsonArgs.GetString(args, "of_class"));

            foreach (var rule in rules)
            {
                if (rule.Op is RuleOp.Regex or RuleOp.IsEmpty or RuleOp.IsNotEmpty)
                    continue; // always post-scan (missing-parameter semantics)
                var found = probes.Select(probe => FindParameter(probe, rule)).Where(parameter => parameter != null).ToList();
                if (found.Count == 0)
                    continue;
                // BuiltInParameter/guid rules address one global parameter id. A plain
                // display name can resolve to DIFFERENT ids per category or family
                // (e.g. 'Width' -> DOOR_WIDTH vs WINDOW_WIDTH). Promote a display-name
                // rule only when the query is scoped AND all probed elements agree on
                // the id; otherwise it stays on the (per-element, correct) post-scan path.
                bool oneGlobalId = rule.BuiltIn != null || rule.SharedGuid != null
                    || (scoped && found.All(parameter => parameter!.Id == found[0]!.Id));
                if (oneGlobalId)
                    rule.QuickRule = TryBuildQuickRule(doc, rule, found[0]!);
            }

            var quickRules = rules.Where(rule => rule.QuickRule != null).ToList();
            var postRules = rules.Where(rule => rule.QuickRule is null).ToList();

            if (matchAny)
            {
                // OR with any post-scan rule means everything must be post-scanned: a
                // collector-level OR filter would wrongly exclude post-rule-only matches.
                if (postRules.Count > 0)
                    return (null, element => rules.Any(rule => EvaluatePost(doc, element, rule)));

                var filters = quickRules.Select(rule => (ElementFilter)new ElementParameterFilter(rule.QuickRule!)).ToList();
                return (filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters), null);
            }

            ElementFilter? quick = quickRules.Count > 0
                ? new ElementParameterFilter(quickRules.Select(rule => rule.QuickRule!).ToList())
                : null;
            Func<Element, bool>? post = postRules.Count > 0
                ? element => postRules.All(rule => EvaluatePost(doc, element, rule))
                : null;
            return (quick, post);
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

            BuiltInParameter? builtIn = null;
            Guid? sharedGuid = null;
            if (param.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                sharedGuid = Guid.TryParse(param["guid:".Length..], out var guid)
                    ? guid
                    : throw new ArgumentException($"Invalid shared parameter guid: {param}");
            }
            else
            {
                string enumName = param.StartsWith("BuiltInParameter.", StringComparison.OrdinalIgnoreCase)
                    ? param["BuiltInParameter.".Length..]
                    : param;
                // BuiltInParameter names are SHOUTY_SNAKE_CASE; require an underscore or
                // all-caps so plain display names like "Comments" never collide.
                bool looksLikeEnumName = enumName.Length > 0 && char.IsLetter(enumName[0]) && !enumName.Contains(' ')
                    && (enumName.Contains('_') || enumName.All(c => !char.IsLetter(c) || char.IsUpper(c)));
                if (looksLikeEnumName && Enum.TryParse<BuiltInParameter>(enumName, true, out var parsed) && parsed != BuiltInParameter.INVALID)
                    builtIn = parsed;
            }

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
                BuiltIn = builtIn,
                SharedGuid = sharedGuid,
                CompiledRegex = regex,
            };
        }

        private static Parameter? FindParameter(Element element, Rule rule)
        {
            if (rule.BuiltIn is { } builtIn)
                return element.get_Parameter(builtIn);
            if (rule.SharedGuid is { } guid)
                return element.get_Parameter(guid);

            var direct = element.LookupParameter(rule.ParamInput);
            if (direct != null)
                return direct;
            foreach (Parameter parameter in element.Parameters)
            {
                if (string.Equals(parameter.Definition?.Name, rule.ParamInput, StringComparison.OrdinalIgnoreCase))
                    return parameter;
            }
            return null;
        }

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

        private static bool EvaluatePost(Document doc, Element element, Rule rule)
        {
            var parameter = FindParameter(element, rule);
            bool empty = parameter is null || !parameter.HasValue
                || (parameter.StorageType == StorageType.String && string.IsNullOrEmpty(parameter.AsString()));
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
                    string actual = parameter.AsString() ?? string.Empty;
                    string target = ValueAsString(rule.Value);
                    return rule.Op switch
                    {
                        RuleOp.Equals => string.Equals(actual, target, StringComparison.Ordinal),
                        RuleOp.NotEquals => !string.Equals(actual, target, StringComparison.Ordinal),
                        RuleOp.Contains => actual.Contains(target, StringComparison.Ordinal),
                        _ => Compare(string.CompareOrdinal(actual, target), rule.Op),
                    };
                }
                case StorageType.Double:
                {
                    if (rule.Op == RuleOp.Contains)
                        return ComparableText(doc, parameter).Contains(ValueAsString(rule.Value), StringComparison.Ordinal);
                    if (!TryValueAsDouble(rule.Value, out double raw))
                        return false;
                    double target = ToInternalUnits(doc, rule, parameter, raw);
                    double actual = parameter.AsDouble();
                    return rule.Op switch
                    {
                        RuleOp.Equals => Math.Abs(actual - target) <= Epsilon,
                        RuleOp.NotEquals => Math.Abs(actual - target) > Epsilon,
                        _ => Compare(actual.CompareTo(target), rule.Op),
                    };
                }
                case StorageType.Integer:
                {
                    if (rule.Op == RuleOp.Contains)
                        return ComparableText(doc, parameter).Contains(ValueAsString(rule.Value), StringComparison.Ordinal);
                    if (!TryValueAsInt(rule.Value, out int target))
                        return false;
                    return Compare(parameter.AsInteger().CompareTo(target), rule.Op);
                }
                case StorageType.ElementId:
                {
                    long actualId = parameter.AsElementId()?.Value ?? -1;
                    if (TryValueAsLong(rule.Value, out long targetId))
                        return Compare(actualId.CompareTo(targetId), rule.Op);

                    string actualText = ComparableText(doc, parameter);
                    string target = ValueAsString(rule.Value);
                    return rule.Op switch
                    {
                        RuleOp.Equals => string.Equals(actualText, target, StringComparison.Ordinal),
                        RuleOp.NotEquals => !string.Equals(actualText, target, StringComparison.Ordinal),
                        RuleOp.Contains => actualText.Contains(target, StringComparison.Ordinal),
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
