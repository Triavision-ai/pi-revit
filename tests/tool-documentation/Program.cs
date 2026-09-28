using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Evaluate the real, composed bridge schemas without loading Autodesk assemblies.
// Only declarative metadata/schema members survive extraction. Execute is replaced
// by a throwing stub; the real registry and its document-identity schema overlay run.
// Missing dependencies, unsupported refactors, or parse errors fail the command.
if (args.Length != 2)
    throw new ArgumentException("Usage: schema-extractor <repository-root> <output.json>");
string root = Path.GetFullPath(args[0]);
string toolsDirectory = Path.Combine(root, "src", "Revit", "Tools");
var metadataProperties = new HashSet<string>(StringComparer.Ordinal)
{
    "Name", "Label", "Description", "ParametersSchema", "Write", "Effects",
    "RequiresDocument", "Tier", "PromptSnippet", "PromptGuidelines", "Keywords", "Limits", "Verification", "DocumentKinds",
};
var generatedTools = new List<string>();
foreach (string sourcePath in Directory.GetFiles(toolsDirectory, "*.cs"))
{
    var parsed = Parse(sourcePath);
    foreach (var tool in parsed.DescendantNodes().OfType<ClassDeclarationSyntax>()
        .Where(node => node.BaseList?.Types.Any(type => type.Type.ToString() == "ITool") == true))
    {
        if (tool.Members.OfType<ConstructorDeclarationSyntax>().Any())
            throw new InvalidOperationException($"{sourcePath}: tool constructors require explicit schema-extractor review.");
        var properties = tool.Members.OfType<PropertyDeclarationSyntax>()
            .Where(member => metadataProperties.Contains(member.Identifier.ValueText)).ToArray();
        foreach (string required in new[] { "Name", "Label", "Description", "ParametersSchema" })
            if (properties.Count(property => property.Identifier.ValueText == required) != 1)
                throw new InvalidOperationException($"{sourcePath}: expected exactly one direct {required} property.");

        // Constants used in descriptions/caps and literal arrays such as relationship
        // names are retained. No caches, lazy initializers, delegates, or Revit state.
        var fields = tool.Members.OfType<FieldDeclarationSyntax>().Where(field =>
            field.Modifiers.Any(SyntaxKind.ConstKeyword) ||
            (field.Modifiers.Any(SyntaxKind.StaticKeyword) &&
             field.Declaration.Type is ArrayTypeSyntax &&
             field.Declaration.Variables.All(variable => variable.Initializer?.Value is InitializerExpressionSyntax init &&
                 init.Expressions.All(expression => expression is LiteralExpressionSyntax))));
        string body = string.Join("\n", fields.Cast<MemberDeclarationSyntax>().Concat(properties).Select(member => member.ToFullString()));
        generatedTools.Add($"internal sealed class {tool.Identifier.ValueText} : ITool {{\n{body}\n" +
            "public object? Execute(JsonElement args, ToolContext context) => throw new InvalidOperationException(\"Tool execution is forbidden in schema extraction.\");\n}");
    }
}
if (generatedTools.Count == 0) throw new InvalidOperationException("No bridge ITool classes found.");

string modelInputs = ExtractMembers(Path.Combine(toolsDirectory, "ModelEditInputs.cs"), "ModelEditInputs",
    new[] { "IdsSchema", "PreviewSchema", "LengthUnitSchema", "VectorSchema" });
string queryScope = ExtractMembers(Path.Combine(toolsDirectory, "ElementQueryScope.cs"), "ElementQueryScope", new[] { "Schema" });
string guard = ExtractMembers(Path.Combine(toolsDirectory, "DocumentGuard.cs"), "DocumentGuard", new[] { "AlwaysRequiresIdentity" });
string registryPath = Path.Combine(root, "src", "Revit", "ToolRegistry.cs");
string contractPath = Path.Combine(toolsDirectory, "ToolContract.cs");
Parse(contractPath);
string registry = File.ReadAllText(registryPath);
Parse(registryPath); // Fail immediately on a changed language construct/parse error.

string prelude = "using System; using System.Collections.Generic; using System.Linq; using System.Text.Json; using System.Text.Json.Nodes;\n";
string generated = prelude + "namespace Autodesk.Revit.DB { public sealed class Document {} }\n" +
    "namespace Autodesk.Revit.UI { public sealed class UIApplication {} public sealed class UIDocument {} }\n" +
    "namespace RevitBridge.Tools {\n" + string.Join("\n", generatedTools) + "\n" + modelInputs + queryScope + guard + "\n}\n" +
    "public static class SchemaEntry { public static string Run() => JsonSerializer.Serialize(RevitBridge.ToolRegistry.CreateDefault().DescribeAll()); }";
var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
    ?? throw new InvalidOperationException("Runtime reference assembly list is unavailable.");
var compilation = CSharpCompilation.Create("RealBridgeSchemas",
    new[] { CSharpSyntaxTree.ParseText(generated, path: "generated-schema-stubs.cs"), CSharpSyntaxTree.ParseText(prelude + registry, path: registryPath),
        CSharpSyntaxTree.ParseText(prelude + File.ReadAllText(contractPath), path: contractPath) },
    trusted.Select(file => MetadataReference.CreateFromFile(file)),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
using var buffer = new MemoryStream();
var emitted = compilation.Emit(buffer);
if (!emitted.Success)
{
    foreach (var diagnostic in emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        Console.Error.WriteLine(diagnostic);
    throw new InvalidOperationException("Real schema extraction failed. Review source refactors; never substitute copied schemas.");
}
var assembly = Assembly.Load(buffer.ToArray());
string json = (string)(assembly.GetType("SchemaEntry")!.GetMethod("Run")!.Invoke(null, null)
    ?? throw new InvalidOperationException("Schema registry returned no data."));
File.WriteAllText(Path.GetFullPath(args[1]), json);
Console.WriteLine($"Extracted {generatedTools.Count} declarative bridge tool classes through the actual registry.");

static CompilationUnitSyntax Parse(string file)
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Latest), file);
    var errors = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length != 0) throw new InvalidOperationException(string.Join("\n", errors.Select(error => error.ToString())));
    return tree.GetCompilationUnitRoot();
}

static string ExtractMembers(string file, string className, string[] names)
{
    var target = Parse(file).DescendantNodes().OfType<ClassDeclarationSyntax>().Single(node => node.Identifier.ValueText == className);
    var members = names.Select(name => target.Members.Single(member => member switch
    {
        PropertyDeclarationSyntax property => property.Identifier.ValueText == name,
        MethodDeclarationSyntax method => method.Identifier.ValueText == name,
        _ => false,
    })).Select(member => member.ToFullString());
    return $"internal static class {className} {{\n{string.Join("\n", members)}\n}}\n";
}
