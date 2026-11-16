using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace RevitBridge.ScriptCompiler
{
    /// <summary>
    /// Compiles execute_csharp scripts with Roslyn. The add-in loads this assembly and the
    /// Roslyn assemblies next to it into a private AssemblyLoadContext, so the Roslyn version
    /// used here never depends on what other Revit add-ins have loaded.
    ///
    /// Because the add-in calls across that boundary, the public surface uses only .NET base
    /// types: no Roslyn type and no add-in type may appear in a signature. Scripts are compiled
    /// to bytes here and loaded and run by the add-in; nothing is executed in this assembly.
    /// </summary>
    public static class Compiler
    {
        private const int MaxReportedErrors = 20;

        /// <summary>One loader for every script. CSharpScript.Create otherwise builds a fresh,
        /// never-released AssemblyLoadContext per call, even for a script that is only compiled.
        /// Nothing is loaded through it: scripts are loaded by the add-in.</summary>
        private static readonly Lazy<InteractiveAssemblyLoader> SharedLoader = new(() => new InteractiveAssemblyLoader());

        /// <summary>
        /// Creates the compile function for one script environment.
        /// </summary>
        /// <param name="globalsType">Public type whose members are the script's globals.</param>
        /// <param name="references">Assemblies scripts may use.</param>
        /// <param name="imports">Namespaces imported into every script.</param>
        /// <param name="errorHint">Sentence appended to compile errors (what is in scope).</param>
        /// <returns>
        /// A function from script text to { byte[] assembly, byte[] symbols, string? entryType,
        /// string? entryMethod }. It throws ArgumentException for compile errors and for
        /// await/async, before any byte is produced.
        /// </returns>
        public static Func<string, object?[]> Create(Type globalsType, Assembly[] references, string[] imports, string errorHint)
        {
            // Built once: metadata references are expensive to create for large assemblies.
            // Debug info is emitted so runtime stack traces carry script line numbers.
            var options = ScriptOptions.Default
                .WithReferences(references)
                .WithImports(imports)
                .WithEmitDebugInformation(true);
            return code => Compile(code, options, globalsType, errorHint);
        }

        private static object?[] Compile(string code, ScriptOptions options, Type globalsType, string errorHint)
        {
            var script = CSharpScript.Create<object>(code, options, globalsType, SharedLoader.Value);
            var compilation = script.GetCompilation();
            // Syntax errors are reported alone (the bind errors that follow them are noise), then the rest.
            var errors = ErrorsOf(compilation.GetParseDiagnostics());
            if (errors.Count == 0)
                errors = ErrorsOf(compilation.GetDiagnostics());
            if (errors.Count > 0)
                throw new ArgumentException(FormatCompileErrors(errors, errorHint));
            RejectAsyncCode(compilation);

            var entryPoint = compilation.GetEntryPoint(CancellationToken.None);
            using var assemblyStream = new MemoryStream();
            using var symbolStream = new MemoryStream();
            var emitted = compilation.Emit(assemblyStream, symbolStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            if (!emitted.Success)
                throw new ArgumentException(FormatCompileErrors(ErrorsOf(emitted.Diagnostics), errorHint));

            string? entryType = null;
            if (entryPoint != null)
            {
                string ns = entryPoint.ContainingNamespace?.MetadataName ?? string.Empty;
                entryType = ns.Length == 0 ? entryPoint.ContainingType.MetadataName : ns + "." + entryPoint.ContainingType.MetadataName;
            }
            return new object?[] { assemblyStream.ToArray(), symbolStream.ToArray(), entryType, entryPoint?.MetadataName };
        }

        private static List<Diagnostic> ErrorsOf(IEnumerable<Diagnostic> diagnostics)
            => diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        private static string FormatCompileErrors(IReadOnlyList<Diagnostic> errors, string errorHint)
        {
            var lines = errors.Take(MaxReportedErrors).Select(diagnostic =>
            {
                var position = diagnostic.Location.GetLineSpan().StartLinePosition;
                return $"  line {position.Line + 1}, col {position.Character + 1}: {diagnostic.Id} {diagnostic.GetMessage()}";
            });
            string more = errors.Count > MaxReportedErrors ? $"\n  … +{errors.Count - MaxReportedErrors} more error(s)" : string.Empty;
            return $"C# compilation failed with {errors.Count} error(s):\n{string.Join("\n", lines)}{more}\n{errorHint}";
        }

        /// <summary>Rejects await/async at compile time. The script runs synchronously on the
        /// Revit API thread (entry task + GetResult): an await would either deadlock Revit
        /// forever (the dispatcher is blocked by GetResult, so a captured-context continuation
        /// can never run) or resume the rest of the script on a thread-pool thread inside the
        /// open transaction. Deterministic syntax-tree guard at the execution boundary, not
        /// source-text sniffing.</summary>
        private static void RejectAsyncCode(Compilation compilation)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                foreach (var token in tree.GetRoot().DescendantTokens())
                {
                    if (!token.IsKind(SyntaxKind.AwaitKeyword) && !token.IsKind(SyntaxKind.AsyncKeyword))
                        continue;
                    var position = token.GetLocation().GetLineSpan().StartLinePosition;
                    throw new ArgumentException(
                        $"'{token.ValueText}' at line {position.Line + 1}, col {position.Character + 1}: "
                        + "scripts run synchronously on the Revit API thread — remove await/async "
                        + "(and never block on Task.Result/.Wait()); write fully synchronous code.");
                }
            }
        }
    }
}
