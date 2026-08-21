using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Offline tests for the search_api_docs engine. The engine is PURE C# (no Revit API),
// so this runner slices it VERBATIM out of the real source file at run time, compiles
// it in memory, and asserts on it -- no Revit required, and no copy that could drift
// from the shipped code. Run with:  dotnet run --project tests/search-engine
// The slice markers below must exist in SearchApiDocs.cs; if a refactor moves them,
// this runner fails loudly with the marker name instead of silently testing nothing.

const string EngineStart = "        private static readonly char[] WordSeparators";
const string EngineEnd = "        private static string BuildMarkdown";
const string ClassesStart = "        private sealed class ApiMember";
const string ClassesEnd = "        /// <summary>Built once per Revit session";

string sourcePath = FindSource(args);
string source = File.ReadAllText(sourcePath);
Console.WriteLine($"engine under test: {sourcePath}");

string engine = Slice(source, EngineStart, EngineEnd);
string classes = Slice(source, ClassesStart, ClassesEnd).Replace("private sealed class", "public sealed class");

string unit =
    "using System;\nusing System.Collections.Generic;\nusing System.Linq;\n" +
    "using System.Text;\nusing System.Text.RegularExpressions;\n\n" +
    classes + "\n" +
    "public static class Engine\n{\n" +
    "    public static (List<ApiMember> Top, int Total, string? Note) Run(DocIndex index, string query, char? kind, int max) => Search(index, query, kind, max);\n" +
    engine + "\n}\n" +
    TestCode;

var compilation = CSharpCompilation.Create(
    "SearchEngineUnderTest",
    new[] { CSharpSyntaxTree.ParseText(unit, new CSharpParseOptions(LanguageVersion.Latest)) },
    new[]
    {
        typeof(object).Assembly,
        Assembly.Load("System.Runtime"),
        Assembly.Load("netstandard"),
        Assembly.Load("System.Collections"),
        typeof(Enumerable).Assembly,
        typeof(System.Text.RegularExpressions.Regex).Assembly,
        typeof(Console).Assembly,
    }.Select(a => MetadataReference.CreateFromFile(a.Location)).ToArray(),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

using var stream = new MemoryStream();
var emit = compilation.Emit(stream);
if (!emit.Success)
{
    foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        Console.Error.WriteLine(d.ToString());
    Console.Error.WriteLine("FAILED to compile the sliced engine -- did a refactor move the slice markers?");
    return 2;
}

var assembly = Assembly.Load(stream.ToArray());
int fails = (int)assembly.GetType("TestRun")!.GetMethod("Execute")!.Invoke(null, null)!;
Console.WriteLine(fails == 0 ? "\nALL SEARCH-ENGINE TESTS PASS" : $"\n{fails} TEST(S) FAILED");
return fails;

static string Slice(string text, string start, string end)
{
    int i = text.IndexOf(start, StringComparison.Ordinal);
    int j = text.IndexOf(end, StringComparison.Ordinal);
    if (i < 0 || j < 0 || j <= i)
        throw new InvalidOperationException($"Slice marker not found or out of order: '{start.Trim()}' .. '{end.Trim()}'. Update the markers in tests/search-engine/Program.cs to match SearchApiDocs.cs.");
    return text[i..j];
}

static string FindSource(string[] args)
{
    if (args.Length > 0 && File.Exists(args[0]))
        return args[0];
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (; dir != null; dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, "src", "Revit", "Tools", "SearchApiDocs.cs");
        if (File.Exists(candidate))
            return candidate;
    }
    throw new FileNotFoundException("src/Revit/Tools/SearchApiDocs.cs not found above the test binary; pass its path as the first argument.");
}

partial class Program
{
    // The tests themselves are compiled INTO the emitted assembly so they can use
    // ApiMember/DocIndex/Engine directly. Assertions cover every deliberate query
    // rewrite the engine has grown (0.2.8 spacing, 0.2.10 ctor, 0.2.11 aliases and
    // factory paths, 0.2.14 accessors, 0.2.16 kind widening) plus honesty controls.
    const string TestCode = """

public static class TestRun
{
    static ApiMember M(char kind, bool ctor, string full, string composite, string signature, int pc) => new ApiMember
    {
        Kind = kind, IsConstructor = ctor, Assembly = "RevitAPI", FullName = full, Composite = composite,
        Signature = signature, FullNameLower = full.ToLowerInvariant(), CompositeLower = composite.ToLowerInvariant(),
        ShortNameLower = composite.Split('.')[^1].ToLowerInvariant(), SignatureLower = signature.ToLowerInvariant(),
        ParameterCount = pc,
    };

    static int fails;
    static void Check(string label, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}  [{detail}]");
        if (!ok) fails++;
    }

    public static int Execute()
    {
        var index = new DocIndex
        {
            Members = new List<ApiMember>
            {
                M('P', false, "Autodesk.Revit.DB.Element.Parameter", "Element.Parameter", "Element.Parameter(BuiltInParameter)", 1),
                M('M', false, "Autodesk.Revit.DB.Wall.Create", "Wall.Create", "Wall.Create(Document, Curve, ElementId, bool)", 4),
                M('M', false, "Autodesk.Revit.DB.LocationCurve.get_ElementsAtJoin", "LocationCurve.get_ElementsAtJoin", "LocationCurve.get_ElementsAtJoin(int)", 1),
                M('M', false, "Autodesk.Revit.DB.Element.GetParameter", "Element.GetParameter", "Element.GetParameter(ForgeTypeId)", 1),
                M('M', false, "Autodesk.Revit.DB.UnitUtils.ConvertToInternalUnits", "UnitUtils.ConvertToInternalUnits", "UnitUtils.ConvertToInternalUnits(double, ForgeTypeId)", 2),
                M('M', true,  "Autodesk.Revit.DB.FilteredElementCollector.#ctor", "FilteredElementCollector", "new FilteredElementCollector(Document)", 1),
                M('M', false, "Autodesk.Revit.Creation.Document.NewRoom", "Document.NewRoom", "Document.NewRoom(Level, UV)", 2),
                M('F', false, "Autodesk.Revit.DB.BuiltInParameter.TAG_ELEMENT_COUNT", "BuiltInParameter.TAG_ELEMENT_COUNT", "BuiltInParameter.TAG_ELEMENT_COUNT", 0),
            },
            SourceFiles = new List<string>(), Warnings = new List<string>(),
        };

        string Top((List<ApiMember> Top, int Total, string? Note) r) => r.Top.Count > 0 ? r.Top[0].Signature : "-";

        var r = Engine.Run(index, "Element.get_Parameter(BuiltInParameter)", 'M', 10);
        Check("accessor + kind=method admits the documented property, with widening note",
            Top(r) == "Element.Parameter(BuiltInParameter)" && r.Note != null && r.Note.Contains("admitted properties"),
            $"top={Top(r)} note={(r.Note ?? "null")}");

        r = Engine.Run(index, "Wall.Create(Document, Curve", 'M', 10);
        Check("real method + kind=method: unchanged, no widening note",
            Top(r).StartsWith("Wall.Create") && r.Note == null, $"top={Top(r)} note={(r.Note ?? "null")}");

        r = Engine.Run(index, "LocationCurve.get_ElementsAtJoin", null, 10);
        Check("literal get_-documented member matches directly, no note",
            Top(r) == "LocationCurve.get_ElementsAtJoin(int)" && r.Note == null, $"top={Top(r)}");

        r = Engine.Run(index, "Element.get_Parameter(BuiltInParameter)", null, 10);
        Check("accessor without kind: rewrite note only, no widening sentence",
            Top(r) == "Element.Parameter(BuiltInParameter)" && r.Note != null && !r.Note.Contains("admitted properties"),
            $"top={Top(r)}");

        r = Engine.Run(index, "Element.get_Parameter(BuiltInParameter)", 'P', 10);
        Check("accessor + kind=property: found via rewrite, no widening needed",
            Top(r) == "Element.Parameter(BuiltInParameter)" && r.Note != null && !r.Note.Contains("admitted properties"),
            $"top={Top(r)}");

        r = Engine.Run(index, "UnitUtils.ConvertToInternalUnits(Double, ForgeTypeId", null, 10);
        Check("CLR alias: 'Double' in the query matches the rendered 'double'",
            Top(r) == "UnitUtils.ConvertToInternalUnits(double, ForgeTypeId)", $"top={Top(r)}");

        r = Engine.Run(index, "wall.create( document ,curve", null, 10);
        Check("spacing + case normalization",
            Top(r).StartsWith("Wall.Create"), $"top={Top(r)}");

        r = Engine.Run(index, "FilteredElementCollector(Document", null, 10);
        Check("constructor matches without the 'new' prefix",
            Top(r) == "new FilteredElementCollector(Document)", $"top={Top(r)}");

        r = Engine.Run(index, "Document.Create.NewRoom(Level, UV", null, 10);
        Check("Creation-factory rewrite, with note",
            Top(r) == "Document.NewRoom(Level, UV)" && r.Note != null && r.Note.Contains("Creation factory"),
            $"top={Top(r)}");

        r = Engine.Run(index, "Document.Create.Banana", null, 10);
        Check("factory honesty: nonsense factory member finds nothing", r.Total == 0, $"total={r.Total}");

        r = Engine.Run(index, "CompletelyMadeUpNonsense", null, 10);
        Check("honesty: nonsense query finds nothing", r.Total == 0, $"total={r.Total}");

        return fails;
    }
}
""";
}
