using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Offline Revit API documentation search. Data source: the RevitAPI.xml and
    /// RevitAPIUI.xml compiler doc files shipped next to the Revit install, resolved from
    /// the loaded RevitAPI.dll location. The index is built lazily on the first query and
    /// shared process-wide; a missing or unparsable file degrades to a warning instead of
    /// an error. RequiresDocument = false: the tool never touches the Revit API (reading
    /// assembly metadata for the install path is not an API call), so it runs on the
    /// bridge's server task — index construction never blocks the Revit thread.
    /// </summary>
    internal sealed class SearchApiDocs : ITool
    {
        private const int DefaultMaxResults = 10;
        private const int MaxResultsCap = 50;
        private const int MaxMarkdownChars = 6_000;
        private const int MaxSummaryChars = 600;
        private const int MaxRemarksChars = 600;
        private const int MaxReturnsChars = 300;
        private const int MaxParamDocChars = 240;
        private const int MaxParamsPerMember = 10;

        public string Name => "search_api_docs";
        public string Label => "Search API Docs";
        public string Description => "Search the offline Revit API documentation (the RevitAPI.xml and RevitAPIUI.xml files shipped with Revit) for types, methods, constructors, properties, fields, and events; every public API enum is fully searchable by value name (values the XML leaves undocumented are synthesized from the API assemblies). query is a single name or substring — e.g. 'FilteredElementCollector', 'Wall.Create', 'WALL_BASE_OFFSET' — ranked: exact name first, then prefix, then substring; dotted Type.Member queries match composites. Returns signatures with summary, remarks, parameter docs, return docs, and the Revit version a member was introduced in ('since'). Works with no document open. Use it to verify exact classes, members, and signatures before writing execute_csharp code. The first query builds the index (a few seconds); later queries are instant.";

        public bool RequiresDocument => false;

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "Type or member name to look up, e.g. FilteredElementCollector, Wall.Create, WALL_BASE_OFFSET. Substring and multi-word matches are ranked; Type.Member composites rank highest on exact match.",
                },
                kind = new
                {
                    type = "string",
                    @enum = new[] { "type", "method", "property", "field", "event" },
                    description = "Optional filter to one member kind ('method' includes constructors; 'field' covers enum values).",
                },
                max_results = new
                {
                    type = "integer",
                    description = $"Maximum matches to return (1-{MaxResultsCap}, default {DefaultMaxResults}).",
                },
            },
            required = new[] { "query" },
        };

        public string? PromptSnippet => "Search the offline Revit API documentation for types, members, and signatures.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Before writing execute_csharp code, verify exact classes and member signatures with search_api_docs (e.g. query 'Wall.Create' or 'FilteredElementCollector').",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            string query = (JsonArgs.GetString(args, "query") ?? string.Empty).Trim();
            if (query.Length == 0)
                throw new ArgumentException("query must be a non-empty string, e.g. 'FilteredElementCollector' or 'Wall.Create'.");
            char? kindFilter = ParseKindFilter(JsonArgs.GetString(args, "kind"));
            int maxResults = Math.Clamp(JsonArgs.GetInt(args, "max_results", DefaultMaxResults), 1, MaxResultsCap);

            var index = Index.Value;
            if (index.Members.Count == 0)
                throw new InvalidOperationException("The Revit API documentation index is empty. " + string.Join(" ", index.Warnings));

            var (top, total) = Search(index, query, kindFilter, maxResults);

            var matches = new List<Dictionary<string, object?>>(top.Count);
            for (int i = 0; i < top.Count; i++)
            {
                var member = top[i];
                matches.Add(new Dictionary<string, object?>
                {
                    ["rank"] = i + 1,
                    ["kind"] = KindLabel(member),
                    ["name"] = member.FullName,
                    ["signature"] = member.Signature,
                    ["assembly"] = member.Assembly,
                    ["since"] = member.Since,
                    ["summary"] = member.Summary,
                    ["remarks"] = member.Remarks,
                    ["parameters"] = member.Parameters?
                        .Select(pair => new Dictionary<string, object?> { ["name"] = pair.Key, ["description"] = pair.Value })
                        .ToList(),
                    ["returns"] = member.Returns,
                });
            }

            return new ToolOutput(new
            {
                query,
                totalMatches = total,
                returnedCount = matches.Count,
                matches,
                indexedMembers = index.Members.Count,
                sources = index.SourceFiles,
                warnings = index.Warnings.Count > 0 ? index.Warnings : null,
            }, BuildMarkdown(query, top, total, index));
        }

        // ----------------------------------------------------------------- search

        private static readonly char[] WordSeparators = { '.', ' ', '_', '(', ')', ',', ':', '-', '/' };

        private static (List<ApiMember> Top, int Total) Search(DocIndex index, string query, char? kindFilter, int maxResults)
        {
            string q = query.ToLowerInvariant();
            string[] words = q.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

            var scored = new List<(ApiMember Member, int Score)>();
            foreach (var member in index.Members)
            {
                if (kindFilter is { } kind && member.Kind != kind)
                    continue;
                int score = Score(member, q, words);
                if (score > 0)
                    scored.Add((member, score));
            }

            var top = scored
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Member.Composite.Length)
                .ThenBy(entry => entry.Member.FullName, StringComparer.Ordinal)
                .Take(maxResults)
                .Select(entry => entry.Member)
                .ToList();
            return (top, scored.Count);
        }

        private static int Score(ApiMember member, string q, string[] words)
        {
            int score;
            if (member.CompositeLower == q) score = 1000;
            else if (member.ShortNameLower == q) score = 900;
            else if (member.CompositeLower.StartsWith(q, StringComparison.Ordinal)) score = 700;
            else if (member.ShortNameLower.StartsWith(q, StringComparison.Ordinal)) score = 650;
            else if (member.CompositeLower.Contains(q, StringComparison.Ordinal)) score = 500;
            else if (member.FullNameLower.Contains(q, StringComparison.Ordinal)) score = 400;
            else if (words.Length > 1 && words.All(word => member.FullNameLower.Contains(word, StringComparison.Ordinal))) score = 300;
            else return 0;

            if (member.Kind == 'T') score += 30;
            if (member.FullNameLower.StartsWith("autodesk.revit.db.", StringComparison.Ordinal)) score += 10;
            return score;
        }

        private static string BuildMarkdown(string query, IReadOnlyList<ApiMember> top, int total, DocIndex index)
        {
            var markdown = new StringBuilder();
            markdown.Append($"Revit API docs for '{query}': ");
            if (total == 0)
            {
                markdown.Append("no matches. Try a shorter substring (a class name like 'FilteredElementCollector'), a Type.Member composite like 'Wall.Create', or drop the kind filter.");
            }
            else
            {
                markdown.Append(total == top.Count ? $"{total} match(es)." : $"top {top.Count} of {total} matches.");
                for (int i = 0; i < top.Count; i++)
                {
                    var member = top[i];
                    string origin = member.Since is null ? member.Assembly : $"{member.Assembly}, since {member.Since}";
                    string line = $"\n{i + 1}. **{member.Signature}** — {KindLabel(member)} ({origin}) — {FirstSentence(member.Summary) ?? "(no summary)"}";
                    if (markdown.Length + line.Length > MaxMarkdownChars)
                    {
                        markdown.Append($"\n… capped; {top.Count - i} more match(es) in details.payload.matches.");
                        break;
                    }
                    markdown.Append(line);
                    if (i == 0)
                        AppendTopMatchDocs(markdown, member);
                }
            }
            foreach (string warning in index.Warnings)
                markdown.Append($"\nNote: {warning}");
            return markdown.ToString();
        }

        /// <summary>The model sees only this markdown — details.payload never reaches it —
        /// so the top match carries its remarks, parameter, and return docs inline. Lines
        /// that would blow the markdown budget are dropped individually; narrowing the
        /// query promotes any other match to the top slot with its docs.</summary>
        private static void AppendTopMatchDocs(StringBuilder markdown, ApiMember member)
        {
            var lines = new List<string>(3);
            if (member.Remarks is { } remarks)
                lines.Add($"\n   remarks: {remarks}");
            if (member.Parameters is { Count: > 0 } parameters)
                lines.Add($"\n   params: {string.Join("; ", parameters.Select(pair => $"{pair.Key} — {pair.Value}"))}");
            if (member.Returns is { } returns)
                lines.Add($"\n   returns: {returns}");
            foreach (string line in lines)
            {
                if (markdown.Length + line.Length > MaxMarkdownChars)
                    break;
                markdown.Append(line);
            }
        }

        private static string KindLabel(ApiMember member) => member.IsConstructor ? "constructor" : member.Kind switch
        {
            'T' => "type",
            'M' => "method",
            'P' => "property",
            'F' => "field",
            'E' => "event",
            _ => "member",
        };

        private static char? ParseKindFilter(string? kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
                return null;
            return kind.Trim().ToLowerInvariant() switch
            {
                "type" or "class" or "t" => 'T',
                "method" or "constructor" or "m" => 'M',
                "property" or "p" => 'P',
                "field" or "enum" or "f" => 'F',
                "event" or "e" => 'E',
                _ => throw new ArgumentException($"Unknown kind: {kind}. Use type, method, property, field, or event."),
            };
        }

        private static string? FirstSentence(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            int end = text.IndexOf(". ", StringComparison.Ordinal);
            string sentence = end >= 0 ? text[..(end + 1)] : text;
            return sentence.Length <= 220 ? sentence : sentence[..220] + "…";
        }

        // ------------------------------------------------------------------ index

        private sealed class ApiMember
        {
            public required char Kind { get; init; }
            public required bool IsConstructor { get; init; }
            public required string Assembly { get; init; }

            /// <summary>Namespace-qualified name without the kind prefix or parameter list.</summary>
            public required string FullName { get; init; }

            /// <summary>"Wall" for types, "Wall.Create" for members — the primary match target.</summary>
            public required string Composite { get; init; }

            public required string Signature { get; init; }
            public required string FullNameLower { get; init; }
            public required string CompositeLower { get; init; }
            public required string ShortNameLower { get; init; }
            public string? Summary { get; init; }
            public string? Remarks { get; init; }
            public string? Returns { get; init; }
            public string? Since { get; init; }
            public IReadOnlyList<KeyValuePair<string, string>>? Parameters { get; init; }
        }

        private sealed class DocIndex
        {
            public required IReadOnlyList<ApiMember> Members { get; init; }
            public required IReadOnlyList<string> SourceFiles { get; init; }
            public required IReadOnlyList<string> Warnings { get; init; }
        }

        /// <summary>Built once per Revit session on first query; thread-safe via Lazy. The
        /// factory never throws — file problems are captured as warnings instead.</summary>
        private static readonly Lazy<DocIndex> Index = new(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);

        private static DocIndex BuildIndex()
        {
            var members = new List<ApiMember>(90_000);
            var sources = new List<string>();
            var warnings = new List<string>();

            foreach (string path in CandidateXmlFiles(warnings))
            {
                if (!File.Exists(path))
                {
                    warnings.Add($"Documentation file not found: {path}.");
                    continue;
                }
                try
                {
                    ParseFile(path, members);
                    sources.Add(path);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to parse {path}: {ex.Message}");
                }
            }

            AddUndocumentedEnumValues(members, warnings);

            return new DocIndex { Members = members, SourceFiles = sources, Warnings = warnings };
        }

        /// <summary>Autodesk's XML leaves many public API enums partially or entirely
        /// undocumented (measured on Revit 2025: 107 of 669, from BuiltInCategory's ~1200
        /// values down to small flags). Synthesize the missing values from the loaded API
        /// assemblies via reflection — pure metadata, safe off the Revit thread.</summary>
        private static void AddUndocumentedEnumValues(List<ApiMember> members, List<string> warnings)
        {
            var existing = new HashSet<string>(
                members.Where(member => member.Kind == 'F').Select(member => member.FullName),
                StringComparer.Ordinal);

            var assemblies = new (Func<System.Reflection.Assembly> Load, string Label)[]
            {
                (() => typeof(Document).Assembly, "RevitAPI"),
                (() => typeof(UIApplication).Assembly, "RevitAPIUI"),
            };
            foreach (var (load, label) in assemblies)
            {
                try
                {
                    foreach (var type in load().GetExportedTypes())
                    {
                        if (type.IsEnum)
                            AddEnumValues(type, label, existing, members);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not enumerate {label} enum values: {ex.Message}");
                }
            }
        }

        /// <summary>Adds every value of one enum that the XML did not document itself.</summary>
        private static void AddEnumValues(Type enumType, string assemblyName, HashSet<string> existing, List<ApiMember> members)
        {
            string typeName = enumType.Name;
            string fullPrefix = (enumType.FullName ?? typeName).Replace('+', '.');
            foreach (string name in Enum.GetNames(enumType))
            {
                string fullName = fullPrefix + "." + name;
                if (!existing.Add(fullName))
                    continue;

                string composite = typeName + "." + name;
                members.Add(new ApiMember
                {
                    Kind = 'F',
                    IsConstructor = false,
                    Assembly = assemblyName,
                    FullName = fullName,
                    Composite = composite,
                    Signature = composite,
                    FullNameLower = fullName.ToLowerInvariant(),
                    CompositeLower = composite.ToLowerInvariant(),
                    ShortNameLower = name.ToLowerInvariant(),
                    Summary = enumType == typeof(BuiltInCategory)
                        ? "BuiltInCategory enum value — usable as the 'category' argument of get_elements/get_element_types and with FilteredElementCollector.OfCategory in execute_csharp."
                        : $"{typeName} enum value (not documented in the XML; synthesized from {assemblyName} metadata).",
                });
            }
        }

        /// <summary>RevitAPI.xml + RevitAPIUI.xml next to the loaded RevitAPI.dll. When the
        /// assembly location cannot be resolved, indexing is skipped with a warning instead
        /// of guessing at an install directory for a Revit version that was never detected.</summary>
        private static IEnumerable<string> CandidateXmlFiles(List<string> warnings)
        {
            string? installDir = null;
            try
            {
                string location = typeof(Document).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                    installDir = Path.GetDirectoryName(location);
            }
            catch
            {
                // Handled below together with the empty-location case.
            }
            if (string.IsNullOrEmpty(installDir))
            {
                warnings.Add("Could not resolve the Revit install directory from the loaded RevitAPI.dll; documentation indexing skipped.");
                yield break;
            }
            yield return Path.Combine(installDir, "RevitAPI.xml");
            yield return Path.Combine(installDir, "RevitAPIUI.xml");
        }

        private static void ParseFile(string path, List<ApiMember> members)
        {
            string assemblyName = Path.GetFileNameWithoutExtension(path);
            using var reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "member")
                {
                    string? rawName = reader.GetAttribute("name");
                    // XNode.ReadFrom consumes the whole <member> element and leaves the
                    // reader on the following node — do not Read() again here.
                    var element = (XElement)XNode.ReadFrom(reader);
                    if (rawName != null && CreateMember(rawName, element, assemblyName) is { } member)
                        members.Add(member);
                }
                else
                {
                    reader.Read();
                }
            }
        }

        private static ApiMember? CreateMember(string rawName, XElement element, string assemblyName)
        {
            if (rawName.Length < 3 || rawName[1] != ':')
                return null;
            char kind = rawName[0];
            if (kind is not ('T' or 'M' or 'P' or 'F' or 'E'))
                return null;

            string body = rawName[2..];
            int paren = body.IndexOf('(');
            string path = paren >= 0 ? body[..paren] : body;
            string? paramText = null;
            if (paren >= 0)
            {
                int close = body.LastIndexOf(')');
                if (close > paren)
                    paramText = body[(paren + 1)..close];
            }

            string[] segments = path.Split('.');
            string shortName;
            string composite;
            string signature;
            bool isConstructor = false;

            if (kind == 'T')
            {
                shortName = StripArity(segments[^1]);
                composite = shortName;
                signature = shortName;
            }
            else
            {
                string memberName = segments[^1];
                string typeName = segments.Length >= 2 ? StripArity(segments[^2]) : string.Empty;
                if (memberName is "#ctor" or "#cctor")
                {
                    isConstructor = true;
                    shortName = typeName;
                    composite = typeName.Length > 0 ? $"{typeName}.{typeName}" : typeName;
                    signature = $"new {typeName}({(paramText is null ? string.Empty : ShortenTypeText(paramText))})";
                }
                else
                {
                    // Explicit interface implementations look like System#IDisposable#Dispose.
                    int hash = memberName.LastIndexOf('#');
                    if (hash >= 0)
                        memberName = memberName[(hash + 1)..];
                    shortName = StripArity(memberName);
                    composite = typeName.Length > 0 ? $"{typeName}.{shortName}" : shortName;
                    signature = kind == 'M' || paramText != null
                        ? $"{composite}({(paramText is null ? string.Empty : ShortenTypeText(paramText))})"
                        : composite;
                }
            }

            List<KeyValuePair<string, string>>? parameters = null;
            foreach (var param in element.Elements("param"))
            {
                if (parameters is { Count: >= MaxParamsPerMember })
                    break;
                string? name = param.Attribute("name")?.Value;
                string? text = Cap(CleanDocText(param), MaxParamDocChars);
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(text))
                    continue;
                (parameters ??= new List<KeyValuePair<string, string>>()).Add(new KeyValuePair<string, string>(name, text));
            }

            return new ApiMember
            {
                Kind = kind,
                IsConstructor = isConstructor,
                Assembly = assemblyName,
                FullName = path,
                Composite = composite,
                Signature = signature,
                FullNameLower = path.ToLowerInvariant(),
                CompositeLower = composite.ToLowerInvariant(),
                ShortNameLower = shortName.ToLowerInvariant(),
                Summary = Cap(CleanDocText(element.Element("summary")), MaxSummaryChars),
                Remarks = Cap(CleanDocText(element.Element("remarks")), MaxRemarksChars),
                Returns = Cap(CleanDocText(element.Element("returns")), MaxReturnsChars),
                Since = CleanDocText(element.Element("since")),
                Parameters = parameters,
            };
        }

        // ------------------------------------------------------------- text utils

        /// <summary>Flattens doc XML to plain text: see/seealso cref -> short type name,
        /// paramref/typeparamref -> the name, all other tags -> their inner text;
        /// whitespace collapsed.</summary>
        private static string? CleanDocText(XElement? element)
        {
            if (element is null)
                return null;
            var sb = new StringBuilder();
            foreach (var node in element.Nodes())
                AppendNode(sb, node);
            string text = CollapseWhitespace(sb.ToString());
            return text.Length == 0 ? null : text;
        }

        private static void AppendNode(StringBuilder sb, XNode node)
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement element:
                    switch (element.Name.LocalName)
                    {
                        case "see" or "seealso":
                            string? target = element.Attribute("cref")?.Value ?? element.Attribute("langword")?.Value;
                            if (target != null)
                                sb.Append(ShortCref(target));
                            else
                                foreach (var child in element.Nodes())
                                    AppendNode(sb, child);
                            break;
                        case "paramref" or "typeparamref":
                            sb.Append(element.Attribute("name")?.Value);
                            break;
                        default:
                            foreach (var child in element.Nodes())
                                AppendNode(sb, child);
                            sb.Append(' ');
                            break;
                    }
                    break;
            }
        }

        /// <summary>"T:Autodesk.Revit.DB.Wall" -> "Wall"; "M:...Wall.Create(...)" -> "Create".</summary>
        private static string ShortCref(string cref)
        {
            string text = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
            int paren = text.IndexOf('(');
            if (paren >= 0)
                text = text[..paren];
            int dot = text.LastIndexOf('.');
            if (dot >= 0)
                text = text[(dot + 1)..];
            return StripArity(text);
        }

        /// <summary>Shortens a doc-id parameter list: namespaces stripped, {} -> &lt;&gt;,
        /// `N arity dropped, ``N generic parameter references -> T, @ (by-ref) -> &amp;,
        /// System primitives -> C# keywords. "Autodesk.Revit.DB.Document,System.Collections.Generic.IList{Autodesk.Revit.DB.Curve},System.Boolean"
        /// -> "Document, IList&lt;Curve&gt;, bool".</summary>
        private static string ShortenTypeText(string text)
        {
            var result = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    int lastDot = -1;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                    {
                        if (text[i] == '.')
                            lastDot = i;
                        i++;
                    }
                    result.Append(MapTypeKeyword(text[(lastDot >= 0 ? lastDot + 1 : start)..i]));
                }
                else if (c == '`')
                {
                    // Directly after an identifier: a generic arity suffix (List`1) — drop it.
                    // Standalone: a generic parameter reference (``0) — render as T.
                    bool arity = i > 0 && char.IsLetterOrDigit(text[i - 1]);
                    while (i < text.Length && (text[i] == '`' || char.IsDigit(text[i])))
                        i++;
                    if (!arity)
                        result.Append('T');
                }
                else
                {
                    result.Append(c switch { '{' => '<', '}' => '>', '@' => '&', _ => c });
                    if (c == ',')
                        result.Append(' ');
                    i++;
                }
            }
            return result.ToString();
        }

        private static string MapTypeKeyword(string name) => name switch
        {
            "String" => "string",
            "Boolean" => "bool",
            "Int32" => "int",
            "Int64" => "long",
            "Int16" => "short",
            "Double" => "double",
            "Single" => "float",
            "Object" => "object",
            "Void" => "void",
            "Byte" => "byte",
            "SByte" => "sbyte",
            "Char" => "char",
            "Decimal" => "decimal",
            "UInt16" => "ushort",
            "UInt32" => "uint",
            "UInt64" => "ulong",
            _ => name,
        };

        private static string StripArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick >= 0 ? name[..tick] : name;
        }

        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string? Cap(string? text, int max)
            => text is null || text.Length <= max ? text : text[..max] + "…";
    }
}
