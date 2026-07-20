using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Globals visible to execute_csharp scripts: doc, uidoc, uiapp, and the Dump helper.
    /// Public because the dynamically compiled script assembly must bind to it; the
    /// lowercase member names are the script-facing contract.
    /// </summary>
    public sealed class ScriptGlobals
    {
        internal ScriptGlobals(Document document, UIDocument uiDocument, UIApplication uiApplication, Action<object?> dump)
        {
            doc = document;
            uidoc = uiDocument;
            uiapp = uiApplication;
            Dump = dump;
        }

        /// <summary>The active document.</summary>
        public Document doc { get; }

        /// <summary>The active UI document (selection, active view).</summary>
        public UIDocument uidoc { get; }

        /// <summary>The Revit UI application.</summary>
        public UIApplication uiapp { get; }

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
        public string Label => "Execute C#";
        public string Description => "Compile and run a C# script on the Revit API thread against the open model — the escape hatch for anything without a dedicated tool: element creation, deletion, geometry edits (move/copy/rotate), views, sheets, schedules, tagging, families, links, worksets. Globals: doc (Document), uidoc (UIDocument), uiapp (UIApplication), and Dump(value) to record intermediate values into the result's dumps[]. Default imports: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI — add using directives at the top for sub-namespaces (e.g. using Autodesk.Revit.DB.Architecture;). The entire run is wrapped in ONE transaction named 'execute_csharp': committed on success, rolled back on any exception, so a failed script never changes the model (do not open your own Transaction; sub-transactions are fine). The script's final expression or return statement becomes returnValue; return primitives, strings, or anonymous objects/lists — Revit API values are projected to safe shapes (Element -> {id,name,category,typeName,levelId}, ElementId -> number, XYZ -> {x,y,z}, Parameter -> {name,value,displayValue}; other API objects become strings) with depth and item caps, so never rely on raw API objects round-tripping. Lengths are in internal units (decimal feet) — convert with UnitUtils. Prefer collector-level filtering (FilteredElementCollector .OfCategory/.OfClass/.WhereElementIsNotElementType) and bounded loops: the call budget is 120s and Revit cannot be interrupted mid-script. Scripts must be fully synchronous — await/async is rejected at compile time, and blocking on tasks (Task.Result/.Wait()) can freeze Revit. Modal dialogs raised while running are auto-dismissed and reported in suppressedDialogs — unrecognized dialogs are answered dismissively (Cancel/Close/No) rather than confirmed, so an operation that raises a confirmation prompt may be cancelled; check suppressedDialogs when a result looks incomplete. Verify unfamiliar signatures with search_api_docs first.";
        public bool Write => true;

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                code = new
                {
                    type = "string",
                    description = "C# script body (top-level statements; using directives allowed at the top). Globals doc/uidoc/uiapp and Dump(value) are in scope. The final expression or a return statement is the result.",
                },
            },
            required = new[] { "code" },
        };

        public string? PromptSnippet => "Run a C# script against the Revit API inside one transaction (globals doc/uidoc/uiapp, Dump helper) — the escape hatch for operations without a dedicated tool.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "Use execute_csharp for Revit operations no dedicated tool covers (creation, deletion, geometry edits, views/sheets, tagging, ...); check exact signatures with search_api_docs before writing the script.",
            "execute_csharp scripts must be fully synchronous (await/async is rejected; never block on Task.Result/.Wait()) and should return primitives or anonymous objects/lists, using Dump(...) for intermediates — raw Revit objects are projected to compact summaries; keep loops bounded and filter at the collector level.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();
            var uiapp = context.UIApplication ?? throw new NoActiveDocumentException();
            var uidoc = uiapp.ActiveUIDocument ?? throw new NoActiveDocumentException();

            string code = JsonArgs.GetString(args, "code") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("code must be a non-empty C# script.");

            var stopwatch = Stopwatch.StartNew();

            // Compile before opening the transaction: compile errors must not touch the model.
            var script = CSharpScript.Create<object>(code, Options.Value, typeof(ScriptGlobals));
            var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                throw new ArgumentException(FormatCompileErrors(errors));
            RejectAsyncCode(script.GetCompilation());

            var dumps = new List<object?>();
            var globals = new ScriptGlobals(doc, uidoc, uiapp, value =>
            {
                if (dumps.Count < MaxDumps)
                    dumps.Add(Project(value, 0));
                else if (dumps.Count == MaxDumps)
                    dumps.Add($"… Dump cap reached ({MaxDumps}); further values were ignored.");
            });

            using var dialogGuard = new DialogGuard(uiapp);
            using var transaction = new Transaction(doc, "execute_csharp");
            var failureGuard = FailureGuard.Attach(transaction);
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Unable to start the execute_csharp transaction.");

            object? returnValue;
            try
            {
                // Runs synchronously on this (the Revit API) thread; await/async was
                // rejected at compile time, so the task completes without suspending.
                var state = script.RunAsync(globals).GetAwaiter().GetResult();
                // Project inside the transaction so values reflect the script-end model state.
                returnValue = Project(state.ReturnValue, 0);
            }
            catch (Exception ex)
            {
                try
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();
                }
                catch
                {
                    // Reporting the script failure outranks a rollback hiccup.
                }
                throw new InvalidOperationException(FormatRuntimeError(ex, dialogGuard.Suppressed));
            }

            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    "Revit rolled back the execute_csharp transaction during commit (failure processing rejected the changes); no model changes were saved."
                    + failureGuard.DescribeErrors()
                    + DescribeDialogs(dialogGuard.Suppressed));

            stopwatch.Stop();

            var payload = new Dictionary<string, object?>
            {
                ["returnValue"] = returnValue,
                ["dumps"] = dumps,
                ["durationMs"] = stopwatch.ElapsedMilliseconds,
            };
            if (dialogGuard.Suppressed.Count > 0)
                payload["suppressedDialogs"] = dialogGuard.Suppressed;
            if (failureGuard.Warnings.Count > 0)
                payload["commitWarnings"] = failureGuard.Warnings;
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
                + "Globals: doc, uidoc, uiapp, Dump(value). Default imports: System, System.Linq, System.Collections.Generic, "
                + "Autodesk.Revit.DB, Autodesk.Revit.UI — add using directives for other namespaces.";
        }

        /// <summary>Rejects await/async at compile time. The script runs synchronously on the
        /// Revit API thread (RunAsync + GetResult): an await would either deadlock Revit
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

        private static string FormatRuntimeError(Exception ex, IReadOnlyList<string> suppressedDialogs)
        {
            string line = TryGetScriptLine(ex) is { } scriptLine ? $" at script line {scriptLine}" : string.Empty;
            string inner = ex.InnerException is { } innerEx ? $" Inner: {innerEx.GetType().Name}: {innerEx.Message}" : string.Empty;
            return $"C# script threw {ex.GetType().Name}{line}: {ex.Message}.{inner}"
                + " The execute_csharp transaction was rolled back; no model changes were saved."
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
                    return double.IsFinite(number) ? number : number.ToString();
                case float number:
                    return float.IsFinite(number) ? (object)number : number.ToString();
                case char or Guid or DateTime or DateTimeOffset or TimeSpan or Uri:
                    return value.ToString();
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
                return Safe(value.ToString);

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
                    result[entry.Key?.ToString() ?? "null"] = Project(entry.Value, depth + 1);
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
                return Safe(value.ToString);
            if (value is Task)
                return "<Task — return awaited values, not tasks>";

            // Plain CLR object (anonymous types, records, user classes): public properties.
            var projected = new Dictionary<string, object?>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (projected.Count >= MaxProperties)
                {
                    projected["…"] = $"truncated at {MaxProperties} properties";
                    break;
                }
                if (property.GetIndexParameters().Length > 0)
                    continue;
                projected[property.Name] = Safe(() => Project(property.GetValue(value), depth + 1));
            }
            return projected.Count > 0 ? projected : Safe(value.ToString);
        }

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
