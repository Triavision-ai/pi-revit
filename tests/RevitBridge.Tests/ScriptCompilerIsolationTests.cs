using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace RevitBridge.Tests;

/// <summary>Globals of the test scripts. Public, like the add-in's ScriptGlobals: the compiled script binds to it.</summary>
public sealed class TestScriptGlobals
{
    public int seed { get; init; } = 41;
    public List<string> log { get; } = new();
}

/// <summary>
/// The script compiler runs on its own Roslyn files, whatever Roslyn version another add-in has
/// already loaded into the shared context. This test project references Roslyn 4.11 directly to
/// play that other add-in; the compiler ships Roslyn 4.8 in its own folder.
/// </summary>
public class ScriptCompilerIsolationTests
{
    private static readonly string CompilerDirectory = typeof(ScriptCompilerIsolationTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "ScriptCompilerDirectory").Value!;

    private const string Hint = "Globals: seed, log.";

    private static Func<string, object?[]> LoadCompiler() => ScriptCompilerHost.Load(
        CompilerDirectory,
        typeof(TestScriptGlobals),
        new[] { typeof(object).Assembly, Assembly.Load("System.Runtime"), Assembly.Load("netstandard"), typeof(Enumerable).Assembly, typeof(List<>).Assembly },
        new[] { "System", "System.Linq", "System.Collections.Generic" },
        Hint);

    /// <summary>Loads and runs a compiled script the way the add-in does: own collectible context, entry factory, globals in slot 0.</summary>
    private static object? Run(object?[] compiled, TestScriptGlobals globals)
    {
        var context = new AssemblyLoadContext("test script", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream((byte[])compiled[0]!), new MemoryStream((byte[])compiled[1]!));
            var entry = assembly.GetType((string)compiled[2]!, throwOnError: true)!
                .GetMethod((string)compiled[3]!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
                .CreateDelegate<Func<object?[], Task<object?>>>();
            return entry(new object?[] { globals, null }).GetAwaiter().GetResult();
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void Another_Roslyn_version_in_the_shared_context_does_not_reach_the_compiler()
    {
        // "Another add-in" loads and uses its Roslyn first, as pyRevit does before pi-revit starts.
        var sharedRoslyn = typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly;
        Assert.NotNull(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class C {}").GetRoot());
        Assert.Equal(new Version(4, 11, 0, 0), sharedRoslyn.GetName().Version);

        var compile = LoadCompiler();
        Assert.Equal(42, Run(compile("seed + 1"), new TestScriptGlobals()));

        var compilerContext = AssemblyLoadContext.GetLoadContext(compile.Method.Module.Assembly)!;
        var compilerRoslyn = compilerContext.Assemblies
            .Where(assembly => assembly.GetName().Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)).ToList();

        Assert.Equal(ScriptCompilerHost.ContextName, compilerContext.Name);
        Assert.NotSame(AssemblyLoadContext.Default, compilerContext);
        // All four Roslyn assemblies the compiler uses are its own 4.8 files: no mix of versions.
        Assert.Equal(
            new[] { "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis.CSharp.Scripting", "Microsoft.CodeAnalysis.Scripting" },
            compilerRoslyn.Select(assembly => assembly.GetName().Name).Order());
        Assert.All(compilerRoslyn, assembly =>
        {
            Assert.Equal(new Version(4, 8, 0, 0), assembly.GetName().Version);
            Assert.StartsWith(Path.GetFullPath(CompilerDirectory), Path.GetFullPath(assembly.Location), StringComparison.OrdinalIgnoreCase);
        });
        // And the other add-in still has its own version.
        Assert.Equal(new Version(4, 11, 0, 0), typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly.GetName().Version);
        Assert.DoesNotContain(sharedRoslyn, compilerRoslyn);
    }

    [Fact]
    public void A_script_reads_and_changes_the_hosts_globals()
    {
        var compile = LoadCompiler();
        var globals = new TestScriptGlobals { seed = 5 };

        object? result = Run(compile("log.Add(\"ran\"); var doubled = Enumerable.Range(1, seed).Select(n => n * 2).ToList(); return doubled.Sum();"), globals);

        Assert.Equal(30, result);
        Assert.Equal(new[] { "ran" }, globals.log);
    }

    /// <summary>A context for a script that records every assembly the script asks for.</summary>
    private sealed class RecordingContext() : AssemblyLoadContext("recording script", isCollectible: true)
    {
        public List<string> Requested { get; } = new();

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Requested.Add(assemblyName.Name ?? string.Empty);
            return null;
        }
    }

    [Fact]
    public async Task Running_a_compiled_script_never_asks_for_Roslyn()
    {
        // The add-in loads scripts where its private Roslyn is not available, so a script
        // must run without it. (Its metadata may list more assemblies than it uses.)
        var compiled = LoadCompiler()("log.Add(string.Join(\",\", new[] { seed, seed + 1 }.Select(n => n.ToString()))); return log.Count;");
        var context = new RecordingContext();
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream((byte[])compiled[0]!), new MemoryStream((byte[])compiled[1]!));
            var entry = assembly.GetType((string)compiled[2]!, throwOnError: true)!
                .GetMethod((string)compiled[3]!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
                .CreateDelegate<Func<object?[], Task<object?>>>();
            var globals = new TestScriptGlobals();

            Assert.Equal(1, await entry(new object?[] { globals, null }));
            Assert.Equal(new[] { "41,42" }, globals.log);
            Assert.Contains(typeof(TestScriptGlobals).Assembly.GetName().Name, context.Requested);
            Assert.DoesNotContain(context.Requested, name => name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void A_compile_error_names_line_and_column_and_carries_the_hint()
    {
        var compile = LoadCompiler();

        var error = Assert.Throws<ArgumentException>(() => compile("var a = 1;\nvar b = undefinedName + a;\nreturn b;"));

        Assert.StartsWith("C# compilation failed with 1 error(s):", error.Message);
        Assert.Contains("line 2, col 9: CS0103", error.Message);
        Assert.EndsWith(Hint, error.Message);
    }

    [Fact]
    public void A_syntax_error_is_reported_without_follow_up_noise()
    {
        var compile = LoadCompiler();

        var error = Assert.Throws<ArgumentException>(() => compile("var a = ;"));

        Assert.Contains("line 1, col 9: CS1525", error.Message);
    }

    [Theory]
    [InlineData("await System.Threading.Tasks.Task.Delay(1); return 1;", "'await' at line 1, col 1")]
    [InlineData("var later = async () => 1;\nreturn 2;", "'async' at line 1, col 13")]
    public void Await_and_async_are_rejected_before_anything_runs(string code, string expected)
    {
        var compile = LoadCompiler();

        var error = Assert.Throws<ArgumentException>(() => compile(code));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_missing_compiler_folder_is_reported_clearly()
    {
        string missing = Path.Combine(Path.GetTempPath(), "pi-revit-no-compiler-" + Guid.NewGuid().ToString("N"));

        var error = Assert.Throws<InvalidOperationException>(() => ScriptCompilerHost.Load(missing, typeof(TestScriptGlobals), Array.Empty<Assembly>(), Array.Empty<string>(), Hint));

        Assert.Contains("script compiler is missing", error.Message);
        Assert.Contains("redeploy", error.Message);
    }
}
