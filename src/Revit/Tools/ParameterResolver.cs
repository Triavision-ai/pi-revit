using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// A caller's parameter reference: <c>guid:&lt;GUID&gt;</c> for a shared parameter, a
    /// BuiltInParameter name (SHOUTY_SNAKE_CASE or <c>BuiltInParameter.NAME</c>), or a
    /// localized display name. One parsing rule for every tool.
    /// </summary>
    internal readonly record struct ParameterReference(string Input, BuiltInParameter? BuiltIn, Guid? SharedGuid)
    {
        public bool IsDisplayName => BuiltIn is null && SharedGuid is null;

        public static ParameterReference Parse(string input)
        {
            string text = input.Trim();
            if (text.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                return Guid.TryParse(text["guid:".Length..], out var guid)
                    ? new ParameterReference(text, null, guid)
                    : throw new ArgumentException($"Invalid shared parameter guid: {text}");
            string enumName = text.StartsWith("BuiltInParameter.", StringComparison.OrdinalIgnoreCase)
                ? text["BuiltInParameter.".Length..]
                : text;
            // BuiltInParameter names are SHOUTY_SNAKE_CASE; require an underscore or
            // all-caps so plain display names like "Comments" never collide.
            bool looksLikeEnumName = enumName.Length > 0 && char.IsLetter(enumName[0]) && !enumName.Contains(' ')
                && (enumName.Contains('_') || enumName.All(c => !char.IsLetter(c) || char.IsUpper(c)));
            if (looksLikeEnumName && Enum.TryParse<BuiltInParameter>(enumName, true, out var parsed) && parsed != BuiltInParameter.INVALID)
                return new ParameterReference(text, parsed, null);
            return new ParameterReference(text, null, null);
        }
    }

    /// <summary>
    /// The single parameter-resolution policy for every tool (inv:parameter-ambiguity).
    /// Exact identities resolve to at most one parameter. A display name returns every
    /// parameter with that name (exact case first, then case-insensitive), because Revit
    /// elements can carry several same-named parameters and <c>LookupParameter</c> would
    /// silently return an arbitrary one. Writes and filters use <see cref="FindSingle"/>,
    /// which rejects ambiguity with the candidates' exact identities; projections use
    /// <see cref="FindAll"/> and report ambiguity. Missing (null) is distinct from empty.
    /// </summary>
    internal static class ParameterResolver
    {
        public static IReadOnlyList<Parameter> FindAll(Element element, ParameterReference reference)
        {
            if (reference.BuiltIn is { } builtIn)
                return One(element.get_Parameter(builtIn));
            if (reference.SharedGuid is { } guid)
                return One(element.get_Parameter(guid));
            // GetParameters is Revit's native exact-name lookup returning every match.
            var exact = element.GetParameters(reference.Input);
            if (exact.Count > 0)
                return Distinct(exact);
            var folded = new List<Parameter>();
            foreach (Parameter parameter in element.Parameters)
                if (string.Equals(parameter.Definition?.Name, reference.Input, StringComparison.OrdinalIgnoreCase))
                    folded.Add(parameter);
            return Distinct(folded);
        }

        /// <summary>The one matching parameter, null when missing; throws when ambiguous.</summary>
        public static Parameter? FindSingle(Element element, ParameterReference reference)
        {
            var matches = FindAll(element, reference);
            return matches.Count switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new ArgumentException(AmbiguityMessage(element, reference, matches)),
            };
        }

        public static string AmbiguityMessage(Element element, ParameterReference reference, IReadOnlyList<Parameter> matches)
            => $"Parameter '{reference.Input}' is ambiguous on element {element.Id.Value}: {matches.Count} parameters share that display name "
               + $"({string.Join("; ", matches.Select(Identity))}). Nothing was chosen. Use one of these exact identities instead.";

        /// <summary>The language-independent identity a caller can pass back.</summary>
        public static string Identity(Parameter parameter)
        {
            if (parameter.IsShared)
                return $"guid:{parameter.GUID}";
            if (parameter.Definition is InternalDefinition definition && definition.BuiltInParameter != BuiltInParameter.INVALID)
                return definition.BuiltInParameter.ToString();
            return $"'{parameter.Definition?.Name}' (parameter id {parameter.Id.Value}, no language-independent identity)";
        }

        private static IReadOnlyList<Parameter> One(Parameter? parameter)
            => parameter is null ? Array.Empty<Parameter>() : new[] { parameter };

        private static IReadOnlyList<Parameter> Distinct(IEnumerable<Parameter> parameters)
            => parameters.GroupBy(parameter => parameter.Id.Value).Select(group => group.First()).ToList();
    }
}
