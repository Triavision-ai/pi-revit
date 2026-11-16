using System.Reflection;
using System.Runtime.Loader;

namespace RevitBridge
{
    /// <summary>
    /// Loads the script compiler (RevitBridge.ScriptCompiler.dll and the Roslyn assemblies next
    /// to it) into a private AssemblyLoadContext.
    ///
    /// Revit loads every add-in into one shared context, where only one copy of an assembly
    /// name can exist: whichever add-in loads Roslyn first decides the version for all others.
    /// With another add-in's Roslyn already loaded, this add-in's compiler ran on a mix of two
    /// Roslyn versions. In the private context the compiler always gets the exact Roslyn files
    /// shipped in the add-in's compiler folder, whatever else is installed.
    ///
    /// The boundary carries only .NET base types (string, Type, Assembly, byte[]), which are
    /// the same types on both sides, so nothing here references Roslyn. No Revit dependency.
    /// </summary>
    internal static class ScriptCompilerHost
    {
        /// <summary>Subfolder of the add-in folder that holds the compiler and its dependencies.</summary>
        internal const string FolderName = "compiler";
        internal const string AssemblyFile = "RevitBridge.ScriptCompiler.dll";
        internal const string ContextName = "pi-revit script compiler";
        private const string CompilerType = "RevitBridge.ScriptCompiler.Compiler";

        /// <summary>
        /// Private context: every assembly shipped in the compiler folder loads from there and
        /// only from there. Anything else (System.*, the add-in itself, Revit) is not in that
        /// folder and resolves through the shared context as usual.
        /// </summary>
        internal sealed class CompilerLoadContext : AssemblyLoadContext
        {
            private readonly string _directory;

            internal CompilerLoadContext(string directory) : base(ContextName) => _directory = directory;

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                if (string.IsNullOrEmpty(assemblyName.Name)) return null;
                string path = Path.Combine(_directory, assemblyName.Name + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }

        /// <summary>
        /// Loads the compiler from <paramref name="compilerDirectory"/> and returns its compile
        /// function: script text in; { byte[] assembly, byte[] symbols, string? entryType,
        /// string? entryMethod } out; ArgumentException for compile errors.
        /// </summary>
        internal static Func<string, object?[]> Load(string compilerDirectory, Type globalsType, Assembly[] references, string[] imports, string errorHint)
        {
            string path = Path.Combine(compilerDirectory, AssemblyFile);
            if (!File.Exists(path))
                throw new InvalidOperationException($"The C# script compiler is missing from the add-in folder ({path}). Close Revit and redeploy the add-in (npx.cmd -y pi-revit).");

            try
            {
                var assembly = new CompilerLoadContext(compilerDirectory).LoadFromAssemblyPath(path);
                var create = assembly.GetType(CompilerType, throwOnError: true)!.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException(CompilerType, "Create");
                return (Func<string, object?[]>)create.Invoke(null, new object[] { globalsType, references, imports, errorHint })!;
            }
            catch (Exception error)
            {
                Exception cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                throw new InvalidOperationException($"The C# script compiler could not be loaded from {compilerDirectory}: {cause.GetType().Name}: {cause.Message} Close Revit and redeploy the add-in (npx.cmd -y pi-revit).", cause);
            }
        }
    }
}
