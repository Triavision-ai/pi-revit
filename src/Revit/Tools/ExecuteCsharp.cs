using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Globals visible to execute_csharp scripts: doc, uidoc, uiapp, and the Dump helper.
    /// Public because the dynamically compiled script assembly must bind to it; the
    /// lowercase member names are the script-facing contract.
    /// </summary>
    public sealed class ScriptGlobals
    {
        internal ScriptGlobals(Document document, UIDocument uiDocument, UIApplication uiApplication, JsonElement inputValues, Action<object?> dump)
        {
            doc = document;
            uidoc = uiDocument;
            uiapp = uiApplication;
            inputs = inputValues;
            Dump = dump;
        }

        /// <summary>The active document.</summary>
        public Document doc { get; }

        /// <summary>The active UI document (selection, active view).</summary>
        public UIDocument uidoc { get; }

        /// <summary>The Revit UI application.</summary>
        public UIApplication uiapp { get; }

        /// <summary>Structured inputs supplied separately from the script source.</summary>
        public JsonElement inputs { get; }

        /// <summary>Records a value into the result's dumps[] (safely projected at call time).</summary>
        public Action<object?> Dump { get; }
    }

    /// <summary>
    /// Unrestricted C# escape hatch: compiles the script with Roslyn, runs it on the
    /// Revit API thread inside ONE backend-owned Transaction("execute_csharp") — commit on
    /// success, rollback on any exception. Results are projected through a depth-limited
    /// serializer that never emits raw Revit API objects. A DialogBoxShowing guard
    /// auto-dismisses modal dialogs so a script cannot hang the Revit thread behind a popup.
    /// </summary>
    internal sealed class ExecuteCsharp : ITool
    {
        private const int MaxDumps = 200;
        private const int MaxDepth = 6;
        private const int MaxItems = 100;
        private const int MaxProperties = 40;
        private const int MaxStringChars = 4_000;
        private const int MaxReportedErrors = 20;

        public string Name => "execute_csharp";
        public IReadOnlyList<string> DocumentKinds => DocumentKind.Both;
        public IReadOnlyList<string> Keywords => new[] { "script", "code", "csharp", "c#", "custom", "automation", "macro" };
        public IReadOnlyList<ToolLimit> Limits => new[]
        {
            new ToolLimit("Automatic preview or rollback of custom code", "tool", "dedicated tools with preview (set_parameters, transform_elements, delete_elements, manage_views, ...)"),
            new ToolLimit("Undoing file, UI or external effects on rollback", "user", "Inspect and clean up those effects explicitly"),
            new ToolLimit("Saving the model", "user", "Save only when the user asks"),
            new ToolLimit("Operations that must run outside any transaction (Document.EditFamily, TransactionGroup, ...)", "user", "Do it in Revit's UI; the script's transaction is always open (a SubTransaction works)"),
        };
        public string? Verification => "reread";
        public string Label => "Execute C#";
        public string Description => "Compile and run a C# script on the Revit API thread against the open model, for operations that no dedicated tool covers. Specialist tools start inactive, so resolve the capability with find_revit_tools first and follow a tool's declared limits and alternatives. Globals: doc (Document), uidoc (UIDocument), uiapp (UIApplication), inputs (System.Text.Json.JsonElement, defaults to an empty object), and Dump(value) to record intermediate values into the result's dumps[]. Default imports: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI — add using directives at the top for sub-namespaces (e.g. using Autodesk.Revit.DB.Architecture;). The entire run is wrapped in ONE transaction named 'execute_csharp': committed on success; on failure rollback is attempted and its confirmed status is reported (do not open your own Transaction; sub-transactions are fine). The script's final expression or return statement becomes returnValue; return primitives, strings, or anonymous objects/lists — Revit API values are projected to safe shapes (Element -> {id,name,category,typeName,levelId}, ElementId -> number, XYZ -> {x,y,z}, Parameter -> {name,value,displayValue}; other API objects become strings) with depth and item caps, so never rely on raw API objects round-tripping. Lengths are in internal units (decimal feet) — convert with UnitUtils. Prefer collector-level filtering (FilteredElementCollector .OfCategory/.OfClass/.WhereElementIsNotElementType) and bounded loops: the call budget is 120s and Revit cannot be interrupted mid-script. Scripts must be fully synchronous — await/async is rejected at compile time, and blocking on tasks (Task.Result/.Wait()) can freeze Revit. Modal dialogs raised while running are auto-dismissed and reported in suppressedDialogs — unrecognized dialogs are answered dismissively (Cancel/Close/No) rather than confirmed, so an operation that raises a confirmation prompt may be cancelled; check suppressedDialogs when a result looks incomplete. Verify unfamiliar signatures with search_api_docs first.";
        public bool Write => true;
        public IReadOnlyList<string> Effects => new[] { "model", "ui", "files", "external" };

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                code = new
                {
                    type = "string",
                    description = "C# script body (top-level statements; using directives allowed at the top). Globals doc/uidoc/uiapp/inputs and Dump(value) are in scope. The final expression or a return statement is the result.",
                },
                inputs = new { type = "object", description = "Optional structured JSON inputs, available as the inputs JsonElement global. Default empty object. Values are never interpolated into source code." },
                expected_document = new
                {
                    type = "string",
                    description = "Optional safety check: title of the document this script is meant for (as reported by get_model_overview). If the active document differs (user switched models), the call fails before running anything.",
                },
            },
            required = new[] { "code" },
        };

        public string? PromptSnippet => "Run a C# script against the Revit API inside one transaction (globals doc/uidoc/uiapp, Dump helper) for operations no dedicated tool covers (check find_revit_tools first).";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use execute_csharp only when no dedicated tool covers the operation (check find_revit_tools and the tool's declared limits first); verify exact signatures with search_api_docs before writing the script.",
            "execute_csharp scripts must be fully synchronous (await/async is rejected; never block on Task.Result/.Wait()) and should return primitives or anonymous objects/lists, using Dump(...) for intermediates — raw Revit objects are projected to compact summaries; keep loops bounded and filter at the collector level.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();
            var uiapp = context.UIApplication ?? throw new NoActiveDocumentException();
            var uidoc = uiapp.ActiveUIDocument ?? throw new NoActiveDocumentException();
            DocumentGuard.CheckExpectedDocument(args, doc);

            string code = JsonArgs.GetString(args, "code") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("code must be a non-empty C# script.");
            var inputValues = args.TryGetProperty("inputs", out var suppliedInputs) ? suppliedInputs.Clone() : JsonSerializer.SerializeToElement(new { });
            if (inputValues.ValueKind != JsonValueKind.Object) throw new ArgumentException("inputs must be a JSON object.");
            if (inputValues.GetRawText().Length > 100000) throw new ArgumentException("inputs must not exceed 100,000 JSON characters.");

            var stopwatch = Stopwatch.StartNew();

            // Compile and load before opening the transaction: compile errors must not touch the model.
            // The script assembly lives in its own collectible context, unloaded when this call ends.
            using var loaded = Compile(code).Load();

            var dumps = new List<object?>();
            var globals = new ScriptGlobals(doc, uidoc, uiapp, inputValues, value =>
            {
                if (dumps.Count < MaxDumps)
                    dumps.Add(Project(value, 0));
                else if (dumps.Count == MaxDumps)
                    dumps.Add($"… Dump cap reached ({MaxDumps}); further values were ignored.");
            });

            using var dialogGuard = new DialogGuard(uiapp);
            using var otherDocuments = new OtherDocumentWatcher(uiapp.Application, doc);
            using var transaction = new Transaction(doc, "execute_csharp");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Unable to start the execute_csharp transaction.");
            var failureGuard = FailureGuard.Attach(transaction);

            object? returnValue;
            string? projectionError = null;
            try
            {
                // Runs synchronously on this (the Revit API) thread; await/async was
                // rejected at compile time, so the task completes without suspending.
                var scriptResult = loaded.Run(globals);
                // Project inside the transaction so values reflect the script-end model
                // state — but never let projection failure masquerade as script failure:
                // a lazy sequence that faults while being walked (a deleted element, a
                // throwing property) would otherwise roll back a script that fully
                // succeeded. The script's changes stand; only the return value is lost.
                try
                {
                    returnValue = Project(scriptResult, 0);
                }
                catch (Exception ex)
                {
                    projectionError = $"{ex.GetType().Name}: {ex.Message}";
                    returnValue = $"<the script succeeded, but its return value could not be projected: {projectionError}. Return primitives, strings, or anonymous objects/lists, and use Dump(...) for intermediates.>";
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    FormatRuntimeError(ex, dialogGuard.Suppressed) + " " + FailureGuard.RollBackAndDescribe(transaction)
                    + otherDocuments.DescribeFailureEffects(), ex);
            }

            try
            {
                var status = transaction.Commit();
                var finalStatus = transaction.GetStatus();
                if (status != TransactionStatus.Committed || finalStatus != TransactionStatus.Committed)
                    throw new InvalidOperationException($"The execute_csharp commit returned {status}; current transaction status is {finalStatus}.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{ex.Message} {FailureGuard.RollBackAndDescribe(transaction)}"
                    + failureGuard.DescribeErrors()
                    + DescribeDialogs(dialogGuard.Suppressed)
                    + otherDocuments.DescribeFailureEffects(), ex);
            }

            stopwatch.Stop();

            var payload = new Dictionary<string, object?>
            {
                ["returnValue"] = returnValue,
                ["dumps"] = dumps,
                ["durationMs"] = stopwatch.ElapsedMilliseconds,
            };
            if (projectionError != null)
                payload["returnValueError"] = projectionError;
            if (dialogGuard.Suppressed.Count > 0)
                payload["suppressedDialogs"] = dialogGuard.Suppressed;
            if (failureGuard.Warnings.Count > 0)
                payload["commitWarnings"] = failureGuard.Warnings;
            if (otherDocuments.Changes() is { Count: > 0 } otherChanges)
            {
                payload["other_documents_changed"] = otherChanges;
                payload["other_documents_warning"] = OtherDocumentWatcher.Warning;
            }
            return payload;
        }

        // -------------------------------------------------------------- scripting

        /// <summary>Compile options: core BCL + LINQ + Revit API references; the five
        /// default imports; debug info so runtime stack traces carry script line numbers.</summary>
        private static readonly Lazy<ScriptOptions> Options = new(() => ScriptOptions.Default
            .WithReferences(
                typeof(object).Assembly,                                    // System.Private.CoreLib
                Assembly.Load("System.Runtime"),
                Assembly.Load("netstandard"),
                typeof(Enumerable).Assembly,                                // System.Linq
                typeof(Regex).Assembly,                                     // System.Text.RegularExpressions
                typeof(Console).Assembly,                                   // System.Console
                typeof(JsonElement).Assembly,                               // System.Text.Json
                typeof(Document).Assembly,                                  // RevitAPI
                typeof(UIApplication).Assembly)                             // RevitAPIUI
            .WithImports("System", "System.Linq", "System.Collections.Generic", "Autodesk.Revit.DB", "Autodesk.Revit.UI")
            .WithEmitDebugInformation(true));

        private static string FormatCompileErrors(IReadOnlyList<Diagnostic> errors)
        {
            var lines = errors.Take(MaxReportedErrors).Select(diagnostic =>
            {
                var position = diagnostic.Location.GetLineSpan().StartLinePosition;
                return $"  line {position.Line + 1}, col {position.Character + 1}: {diagnostic.Id} {diagnostic.GetMessage()}";
            });
            string more = errors.Count > MaxReportedErrors ? $"\n  … +{errors.Count - MaxReportedErrors} more error(s)" : string.Empty;
            return $"C# compilation failed with {errors.Count} error(s):\n{string.Join("\n", lines)}{more}\n"
                + "Globals: doc, uidoc, uiapp, inputs (JsonElement), Dump(value). Default imports: System, System.Linq, System.Collections.Generic, "
                + "Autodesk.Revit.DB, Autodesk.Revit.UI — add using directives for other namespaces.";
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

        /// <summary>One loader for every script. CSharpScript.Create otherwise builds a fresh,
        /// never-released AssemblyLoadContext per call, even for a script that is only compiled.
        /// Nothing is loaded through it: scripts are loaded by LoadedScript.</summary>
        private static readonly Lazy<InteractiveAssemblyLoader> SharedLoader = new(() => new InteractiveAssemblyLoader());

        /// <summary>Compiles the script to an in-memory assembly without loading it. Compile errors
        /// and async/await are reported here, before anything loads or any transaction opens.</summary>
        private static CompiledScript Compile(string code)
        {
            var script = CSharpScript.Create<object>(code, Options.Value, typeof(ScriptGlobals), SharedLoader.Value);
            var compilation = script.GetCompilation();
            // Syntax errors are reported alone (the bind errors that follow them are noise), then the rest.
            var errors = ErrorsOf(compilation.GetParseDiagnostics());
            if (errors.Count == 0)
                errors = ErrorsOf(compilation.GetDiagnostics());
            if (errors.Count > 0)
                throw new ArgumentException(FormatCompileErrors(errors));
            RejectAsyncCode(compilation);

            var entryPoint = compilation.GetEntryPoint(CancellationToken.None);
            using var assemblyStream = new MemoryStream();
            using var symbolStream = new MemoryStream();
            var emitted = compilation.Emit(assemblyStream, symbolStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            if (!emitted.Success)
                throw new ArgumentException(FormatCompileErrors(ErrorsOf(emitted.Diagnostics)));

            string? entryType = null;
            if (entryPoint != null)
            {
                string ns = entryPoint.ContainingNamespace?.MetadataName ?? string.Empty;
                entryType = ns.Length == 0 ? entryPoint.ContainingType.MetadataName : ns + "." + entryPoint.ContainingType.MetadataName;
            }
            return new CompiledScript(assemblyStream.ToArray(), symbolStream.ToArray(), entryType, entryPoint?.MetadataName);
        }

        private static List<Diagnostic> ErrorsOf(IEnumerable<Diagnostic> diagnostics)
            => diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        /// <summary>A compiled script: assembly and symbol bytes plus the name of its entry method.</summary>
        private sealed record CompiledScript(byte[] Assembly, byte[] Symbols, string? EntryType, string? EntryMethod)
        {
            public LoadedScript Load() => new(this);
        }

        /// <summary>A script assembly loaded into its own collectible context. Disposing unloads the
        /// context, so the assembly, its types and its JIT-ed code are reclaimed by a later GC instead of
        /// accumulating for the life of Revit. Values it returns must be projected before Dispose.</summary>
        private sealed class LoadedScript : IDisposable
        {
            private readonly ScriptLoadContext _context = new();
            private readonly Func<object?[], Task<object?>>? _entry;
            private bool _unloaded;

            public LoadedScript(CompiledScript compiled)
            {
                try
                {
                    var assembly = _context.LoadFromStream(new MemoryStream(compiled.Assembly), new MemoryStream(compiled.Symbols));
                    // The entry method is Roslyn's submission factory: static Task<object> (object[] submissions).
                    _entry = compiled.EntryType is null || compiled.EntryMethod is null ? null
                        : assembly.GetType(compiled.EntryType, throwOnError: true)!
                            .GetMethod(compiled.EntryMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
                            .CreateDelegate<Func<object?[], Task<object?>>>();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            /// <summary>Runs the script synchronously; slot 0 of the submission array is the globals object.</summary>
            public object? Run(ScriptGlobals globals)
                => _entry is null ? null : _entry(new object?[] { globals, null }).GetAwaiter().GetResult();

            public void Dispose()
            {
                if (_unloaded) return;
                _unloaded = true;
                _context.Unload();
            }
        }

        /// <summary>Collectible context for one script. Everything but the script itself (RevitAPI, this
        /// add-in, System.*) resolves through the add-in's own context, so ScriptGlobals and every Revit
        /// type are the very instances the add-in works with.</summary>
        private sealed class ScriptLoadContext : AssemblyLoadContext
        {
            private static readonly AssemblyLoadContext Owner = GetLoadContext(typeof(ScriptGlobals).Assembly) ?? Default;

            public ScriptLoadContext() : base("execute_csharp", isCollectible: true) { }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                try { return Owner.LoadFromAssemblyName(assemblyName); }
                catch (FileNotFoundException) { return null; }
            }
        }

        private static string FormatRuntimeError(Exception ex, IReadOnlyList<string> suppressedDialogs)
        {
            string line = TryGetScriptLine(ex) is { } scriptLine ? $" at script line {scriptLine}" : string.Empty;
            string inner = ex.InnerException is { } innerEx ? $" Inner: {innerEx.GetType().Name}: {innerEx.Message}" : string.Empty;
            return $"C# script threw {ex.GetType().Name}{line}: {ex.Message}.{inner}"
                + DescribeDialogs(suppressedDialogs);
        }

        private static string DescribeDialogs(IReadOnlyList<string> suppressed)
            => suppressed.Count > 0 ? $" Auto-dismissed dialog(s) during the run: {string.Join(", ", suppressed)}." : string.Empty;

        /// <summary>Script frames carry sequence points (EmitDebugInformation): pull the
        /// first ":line N" out of the stack trace.</summary>
        private static int? TryGetScriptLine(Exception ex)
        {
            string? stackTrace = ex.StackTrace;
            if (stackTrace is null)
                return null;
            var match = Regex.Match(stackTrace, @":line (\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out int line) ? line : null;
        }

        /// <summary>Auto-dismisses any modal Revit dialog raised while the script runs, so a
        /// popup cannot hang the Revit thread; dismissed dialog ids and the answer given are
        /// reported. Unrecognized dialogs get the dismissive answer (Cancel, then Close, then
        /// No) — on several Revit dialogs OK is the destructive choice (e.g. "Delete
        /// Element(s)"), so confirming blind can silently damage the model. OK leads only for
        /// dialogs known to be safe to confirm, and is otherwise the last resort: every
        /// override attempt failing would leave the dialog up and hang the Revit thread,
        /// which is the one outcome this guard exists to prevent.</summary>
        private sealed class DialogGuard : IDisposable
        {
            private const int IDOK = 1;
            private const int IDCANCEL = 2;
            private const int IDNO = 7;
            private const int IDCLOSE = 8;

            /// <summary>Dialogs where confirming is the benign answer and cancelling would
            /// abort the operation the script deliberately started.</summary>
            private static readonly HashSet<string> ConfirmSafeDialogIds = new(StringComparer.OrdinalIgnoreCase)
            {
                // "Export with temporary hide/isolate": OK exports the view as displayed.
                "TaskDialog_Really_Print_Or_Export_Temp_View_Modes",
            };

            private static readonly int[] ConfirmFirst = { IDOK, IDCANCEL, IDCLOSE };
            private static readonly int[] DismissFirst = { IDCANCEL, IDCLOSE, IDNO, IDOK };

            private readonly UIApplication _uiapp;
            public List<string> Suppressed { get; } = new();

            public DialogGuard(UIApplication uiapp)
            {
                _uiapp = uiapp;
                _uiapp.DialogBoxShowing += OnDialogBoxShowing;
            }

            private void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
            {
                string id = string.IsNullOrEmpty(e.DialogId) ? e.GetType().Name : e.DialogId;
                int[] answers = ConfirmSafeDialogIds.Contains(id) ? ConfirmFirst : DismissFirst;
                foreach (int answer in answers)
                {
                    try
                    {
                        if (e.OverrideResult(answer))
                        {
                            Suppressed.Add($"{id} (answered {AnswerName(answer)})");
                            return;
                        }
                    }
                    catch
                    {
                        // Some dialogs reject specific overrides; try the next answer.
                    }
                }
                Suppressed.Add($"{id} (override rejected — dialog may need manual dismissal)");
            }

            private static string AnswerName(int answer) => answer switch
            {
                IDOK => "OK",
                IDCANCEL => "Cancel",
                IDNO => "No",
                IDCLOSE => "Close",
                _ => answer.ToString(),
            };

            public void Dispose()
            {
                try
                {
                    _uiapp.DialogBoxShowing -= OnDialogBoxShowing;
                }
                catch
                {
                    // Unsubscribe must never mask the tool result.
                }
            }
        }

        /// <summary>Watches the other open documents while a script runs. The backend transaction, its
        /// rollback and model_changes cover only the target document, yet a script can create, open,
        /// edit or close others (Application.NewProjectDocument, OpenDocumentFile). Those effects are
        /// reported as counts, never blocked. Linked-model documents are not counted.</summary>
        private sealed class OtherDocumentWatcher : IDisposable
        {
            public const string Warning = "This script changed, opened or closed other open documents (other_documents_changed, counts only). Its transaction, its rollback and model_changes cover only the target document, so those changes are not itemized and are not rolled back on failure. Inspect those documents before reporting what changed.";

            private sealed class Seen
            {
                public required Document Document { get; init; }
                public required string Title { get; init; }
                public int Added, Modified, Deleted;
                public bool Opened;
            }

            private readonly Autodesk.Revit.ApplicationServices.Application _application;
            private readonly Document _target;
            private readonly List<Seen> _seen = new();

            public OtherDocumentWatcher(Autodesk.Revit.ApplicationServices.Application application, Document target)
            {
                _application = application;
                _target = target;
                foreach (var open in OpenOthers())
                    _seen.Add(new Seen { Document = open, Title = open.Title });
                _application.DocumentChanged += OnDocumentChanged;
            }

            private IEnumerable<Document> OpenOthers()
                => _application.Documents.Cast<Document>().Where(d => !d.Equals(_target) && !d.IsLinked);

            private Seen? Find(Document document)
                => _seen.FirstOrDefault(s => s.Document.IsValidObject && s.Document.Equals(document));

            private void OnDocumentChanged(object? sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
            {
                try
                {
                    var changed = e.GetDocument();
                    if (changed.Equals(_target) || changed.IsLinked)
                        return;
                    var seen = Find(changed);
                    if (seen is null)
                        _seen.Add(seen = new Seen { Document = changed, Title = changed.Title, Opened = true });
                    seen.Added += e.GetAddedElementIds().Count;
                    seen.Modified += e.GetModifiedElementIds().Count;
                    seen.Deleted += e.GetDeletedElementIds().Count;
                }
                catch (Exception)
                {
                    // Reporting must never break the script's own work.
                }
            }

            /// <summary>Documents the script changed, opened or closed, with element counts.</summary>
            private List<Seen> Touched()
            {
                try
                {
                    // A document can be opened without any element change (OpenDocumentFile, an empty NewProjectDocument).
                    foreach (var open in OpenOthers())
                        if (Find(open) is null)
                            _seen.Add(new Seen { Document = open, Title = open.Title, Opened = true });
                }
                catch (Exception)
                {
                    // Best effort: the changes already recorded still count.
                }
                return _seen.Where(s => s.Added + s.Modified + s.Deleted > 0 || s.Opened || !s.Document.IsValidObject)
                    .OrderBy(s => s.Title, StringComparer.Ordinal).ToList();
            }

            /// <summary>The other_documents_changed result field: counts per document, empty when none.</summary>
            public List<Dictionary<string, object?>> Changes() => Touched().Select(seen =>
            {
                var entry = new Dictionary<string, object?> { ["title"] = seen.Title, ["added"] = seen.Added, ["modified"] = seen.Modified, ["deleted"] = seen.Deleted };
                if (seen.Opened) entry["opened"] = true;
                if (!seen.Document.IsValidObject) entry["closed"] = true;
                return entry;
            }).ToList();

            /// <summary>For failure messages: the script's rollback cannot undo these.</summary>
            public string DescribeFailureEffects()
            {
                var touched = Touched();
                return touched.Count == 0 ? string.Empty
                    : " Other open documents were changed and are NOT rolled back: "
                        + string.Join("; ", touched.Select(s => $"'{s.Title}' ({s.Added} added, {s.Modified} modified, {s.Deleted} deleted{(s.Opened ? ", opened by the script" : string.Empty)}{(s.Document.IsValidObject ? string.Empty : ", closed")})")) + ".";
            }

            public void Dispose()
            {
                try
                {
                    _application.DocumentChanged -= OnDocumentChanged;
                }
                catch
                {
                    // Unsubscribe must never mask the tool result.
                }
            }
        }

        // ------------------------------------------------------------- projection

        /// <summary>Safe serializer: depth-limited, item-capped, and never emits raw Revit
        /// API objects — Element/ElementId/XYZ/Parameter/BoundingBoxXYZ/Category get compact
        /// shapes, any other Autodesk type falls back to ToString().</summary>
        private static object? Project(object? value, int depth)
        {
            if (value is null)
                return null;

            switch (value)
            {
                case string text:
                    return text.Length <= MaxStringChars ? text : text[..MaxStringChars] + "… [truncated]";
                case bool or int or long or short or byte or sbyte or uint or ulong or ushort or decimal:
                    return value;
                case double number:
                    return double.IsFinite(number) ? number : number.ToString(CultureInfo.InvariantCulture);
                case float number:
                    return float.IsFinite(number) ? (object)number : number.ToString(CultureInfo.InvariantCulture);
                case char or Guid or DateTime or DateTimeOffset or TimeSpan or Uri:
                    return InvariantText(value);
                case Enum enumValue:
                    return enumValue.ToString();
                case JsonElement json:
                    return json.Clone();
                case ElementId id:
                    return id.Value;
                case XYZ point:
                    return new Dictionary<string, object?> { ["x"] = point.X, ["y"] = point.Y, ["z"] = point.Z };
                case UV uv:
                    return new Dictionary<string, object?> { ["u"] = uv.U, ["v"] = uv.V };
                case BoundingBoxXYZ box:
                    return new Dictionary<string, object?>
                    {
                        ["min"] = Safe(() => Project(box.Min, depth + 1)),
                        ["max"] = Safe(() => Project(box.Max, depth + 1)),
                    };
                case Element element:
                    return ProjectElement(element);
                case Parameter parameter:
                    return ProjectParameter(parameter);
                case Category category:
                    return new Dictionary<string, object?>
                    {
                        ["id"] = Safe(() => category.Id.Value),
                        ["name"] = Safe(() => category.Name),
                    };
            }

            if (depth >= MaxDepth)
                return Safe(() => InvariantText(value));

            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (result.Count >= MaxItems)
                    {
                        result["…"] = $"truncated at {MaxItems} entries";
                        break;
                    }
                    result[InvariantText(entry.Key) ?? "null"] = Project(entry.Value, depth + 1);
                }
                return result;
            }

            if (value is IEnumerable enumerable)
            {
                var items = new List<object?>();
                foreach (object? item in enumerable)
                {
                    if (items.Count >= MaxItems)
                    {
                        items.Add($"… truncated at {MaxItems} items");
                        break;
                    }
                    items.Add(Project(item, depth + 1));
                }
                return items;
            }

            var type = value.GetType();
            // Any other Revit/Autodesk API object (Curve, Material asset, ...) has no safe
            // JSON shape: string fallback instead of reflecting into native handles.
            if (type.Namespace?.StartsWith("Autodesk.", StringComparison.Ordinal) == true)
                return Safe(() => InvariantText(value));
            if (value is Task)
                return "<Task — return awaited values, not tasks>";

            // Plain CLR object (anonymous types, records, user classes): public properties, then
            // public fields — a ValueTuple or a class of plain fields has no properties at all.
            var projected = new Dictionary<string, object?>();
            const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
            foreach (var member in type.GetProperties(publicInstance).Cast<MemberInfo>().Concat(type.GetFields(publicInstance)))
            {
                if (projected.Count >= MaxProperties)
                {
                    projected["…"] = $"truncated at {MaxProperties} members";
                    break;
                }
                if (member is PropertyInfo property && property.GetIndexParameters().Length > 0)
                    continue;
                projected[member.Name] = Safe(() => Project(member is PropertyInfo p ? p.GetValue(value) : ((FieldInfo)member).GetValue(value), depth + 1));
            }
            return projected.Count > 0 ? projected : Safe(() => InvariantText(value));
        }

        /// <summary>Culture-invariant text for a value: the machine's regional settings (a decimal
        /// comma, localized dates) must never reach a result. DateTime/DateTimeOffset use the
        /// round-trip "o" format.</summary>
        private static string? InvariantText(object? value) => value switch
        {
            null => null,
            DateTime dateTime => dateTime.ToString("o", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("o", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        private static object ProjectElement(Element element) => new Dictionary<string, object?>
        {
            ["id"] = Safe(() => element.Id.Value),
            ["name"] = Safe(() => element.Name),
            ["category"] = Safe(() => element.Category?.Name),
            ["typeName"] = Safe(() => element.Document.GetElement(element.GetTypeId())?.Name),
            ["levelId"] = Safe(() => element.LevelId != ElementId.InvalidElementId ? element.LevelId.Value : (object?)null),
        };

        private static object ProjectParameter(Parameter parameter) => new Dictionary<string, object?>
        {
            ["name"] = Safe(() => parameter.Definition?.Name),
            ["storageType"] = Safe(() => parameter.StorageType.ToString()),
            ["value"] = Safe(() => parameter.StorageType switch
            {
                StorageType.String => parameter.AsString(),
                StorageType.Double => parameter.AsDouble(),
                StorageType.Integer => parameter.AsInteger(),
                StorageType.ElementId => (object?)parameter.AsElementId().Value,
                _ => null,
            }),
            ["displayValue"] = Safe(() => parameter.AsValueString()),
        };

        private static object? Safe(Func<object?> producer)
        {
            try
            {
                return producer();
            }
            catch (Exception ex)
            {
                return $"<error: {ex.Message}>";
            }
        }
    }
}
