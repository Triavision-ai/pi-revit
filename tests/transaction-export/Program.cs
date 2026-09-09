using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitBridge.Tools;

var outputRoot = args.FirstOrDefault(x => x.StartsWith("--output-root="))?["--output-root=".Length..]
    ?? Path.Combine(Path.GetTempPath(), "pi-revit-transaction-export-tests", Guid.NewGuid().ToString("N"));
string evidenceRoot = Path.GetFullPath(outputRoot);
outputRoot = Path.Combine(evidenceRoot, "run-" + Guid.NewGuid().ToString("N"));
RevitBridge.Tools.Environment.TestDocuments = outputRoot;
Directory.CreateDirectory(outputRoot);
int passed = 0, failed = 0, exportNumber = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action run)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; failures.Add(name + ": " + ex.Message); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value);
JsonElement Export(Document doc, string format = "pdf", string? directory = null)
{
    var request = new Dictionary<string, object?> { ["format"] = format, ["ids"] = new[] { 1 }, ["file_name_prefix"] = $"test-{++exportNumber}" };
    if (directory != null) request["output_dir"] = directory;
    var result = (ToolOutput)new ExportDocuments().Execute(Arguments(request), new ToolContext(doc))!;
    return JsonSerializer.SerializeToElement(result.Data);
}
string DirectoryFor(Document doc) => Export(doc).GetProperty("outputDir").GetString()!;
string RollbackMessage(Transaction transaction)
{
    var method = typeof(FailureGuard).GetMethod("RollBackAndDescribe", BindingFlags.Public | BindingFlags.Static)
        ?? throw new Exception("transaction rollback reporting helper absent in baseline");
    return (string)method.Invoke(null, new object[] { transaction })!;
}

Test("reject FailureGuard attachment before Start", () =>
{
    using var tx = new Transaction(new Document(), "test");
    bool rejected = false;
    try { FailureGuard.Attach(tx); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "guard accepted attachment that Start would reset");
});
Test("guard retained after Start and clears warnings", () =>
{
    var doc = new Document(); doc.Failures.Add(new(FailureSeverity.Warning, "duplicate mark"));
    using var tx = new Transaction(doc, "test"); tx.Start(); var guard = FailureGuard.Attach(tx);
    Check(tx.Commit() == TransactionStatus.Committed && guard.Warnings.SequenceEqual(new[] { "duplicate mark" }), "warning not collected");
    Check(tx.GetFailureHandlingOptions().ClearAfterRollback, "rollback failures not cleared");
});
Test("error failure rolls transaction back and records errors", () =>
{
    var doc = new Document(); doc.Failures.Add(new(FailureSeverity.Error, "cannot keep joined"));
    using var tx = new Transaction(doc, "test"); tx.Start(); var guard = FailureGuard.Attach(tx);
    Check(tx.Commit() == TransactionStatus.RolledBack, "error committed");
    Check(guard.Errors.SequenceEqual(new[] { "cannot keep joined" }), "failure missing from report");
});
Test("selection isolate retains failure guard through commit", () =>
{
    var doc = new Document(); var context = new ToolContext(doc);
    new ManageSelection().Execute(Arguments(new { action = "set", element_ids = new[] { 1 }, isolate_in_view = true }), context);
    Check(doc.HadPreprocessorAtCommit, "Start reset the preprocessor");
});
Test("selection isolate reports dismissed warnings", () =>
{
    var doc = new Document(); doc.Failures.Add(new(FailureSeverity.Warning, "isolate warning"));
    var result = (ToolOutput)new ManageSelection().Execute(Arguments(new { action = "set", element_ids = new[] { 1 }, isolate_in_view = true }), new ToolContext(doc))!;
    var payload = JsonSerializer.SerializeToElement(result.Data);
    Check(payload.TryGetProperty("commitWarnings", out var warnings) && warnings.GetArrayLength() == 1, "warning lost");
});
Test("selection failure reports already completed UI action", () =>
{
    var doc = new Document(); doc.ActiveView!.ThrowOnIsolate = true; var context = new ToolContext(doc);
    string message = "";
    try { new ManageSelection().Execute(Arguments(new { action = "set", element_ids = new[] { 1 }, isolate_in_view = true }), context); }
    catch (Exception ex) { message = ex.Message; }
    Check(context.UIApplication.ActiveUIDocument!.Selection.GetElementIds().Count == 1, "test did not retain selection");
    Check(message.Contains("completed before") && message.Contains("Selection/zoom changes were not rolled back"), "failure hides prior UI change: " + message);
});

Test("same-title saved models have separate default folders", () =>
{
    Check(DirectoryFor(new Document { PathName = @"C:\ProjectA\SameTitle.rvt" }) != DirectoryFor(new Document { PathName = @"C:\ProjectB\SameTitle.rvt" }), "same-titled models share export folder");
});
Test("copied projects with inherited UniqueId stay separate", () =>
{
    var one = new Document { PathName = @"C:\Original\SameTitle.rvt" };
    var two = new Document { PathName = @"C:\Copy\SameTitle.rvt" };
    Check(one.ProjectInformation.UniqueId == two.ProjectInformation.UniqueId, "test setup");
    Check(DirectoryFor(one) != DirectoryFor(two), "inherited identity merged two files");
});
Test("filename sanitization collision does not merge saved models", () =>
{
    Check(DirectoryFor(new Document { Title = "A:B", PathName = @"C:\one.rvt" }) != DirectoryFor(new Document { Title = "A?B", PathName = @"C:\two.rvt" }), "sanitized title collision");
});
Test("Windows path case separators and relative segments normalize", () =>
{
    Check(DirectoryFor(new Document { PathName = @"C:\Projects\Old\..\SameTitle.rvt" }) == DirectoryFor(new Document { PathName = "c:/projects/sametitle.rvt" }), "equivalent file paths split identity");
});
Test("unsaved same-title documents separate with stable per-document folders", () =>
{
    var one = new Document(); var two = new Document(); var first = DirectoryFor(one);
    Check(first == DirectoryFor(one), "unsaved identity changes each export");
    Check(first != DirectoryFor(two), "different unsaved documents share folder");
});
Test("unsaved identity survives different wrappers of same open document", () =>
{
    var one = new Document();
    var secondWrapper = new Document { OpenDocumentId = one.OpenDocumentId };
    Check(!ReferenceEquals(one, secondWrapper) && one.Equals(secondWrapper), "test setup");
    Check(DirectoryFor(one) == DirectoryFor(secondWrapper), "same open document split across managed wrappers");
});
Test("cloud model identity uses project model and region", () =>
{
    var one = new Document { IsModelInCloud = true };
    var two = new Document { IsModelInCloud = true, CloudPath = new ModelPath { Project = one.CloudPath.Project, Model = one.CloudPath.Model, Region = "EMEA" } };
    var three = new Document { IsModelInCloud = true, CloudPath = new ModelPath { Project = one.CloudPath.Project, Model = Guid.NewGuid() } };
    Check(DirectoryFor(one) != DirectoryFor(two), "different cloud regions merged");
    Check(DirectoryFor(one) != DirectoryFor(three), "different cloud models merged");
    var reopen = new Document { IsModelInCloud = true, CloudPath = one.CloudPath };
    Check(DirectoryFor(one) == DirectoryFor(reopen), "same cloud identity changes on reopen");
});
Test("unavailable cloud identity safely falls back per open document", () =>
{
    var one = new Document { IsModelInCloud = true, CloudPathUnavailable = true };
    var two = new Document { IsModelInCloud = true, CloudPathUnavailable = true };
    Check(DirectoryFor(one) == DirectoryFor(one), "fallback unstable");
    Check(DirectoryFor(one) != DirectoryFor(two), "fallback merges documents");
});
Test("existing title-only exports preserved without automatic migration", () =>
{
    var oldFolder = Path.Combine(outputRoot, "pi-revit", "Models", "SameTitle", "exports");
    Directory.CreateDirectory(oldFolder); string sentinel = Path.Combine(oldFolder, "existing.pdf"); File.WriteAllText(sentinel, "keep me");
    var folder = DirectoryFor(new Document { PathName = @"C:\Legacy\SameTitle.rvt" });
    Check(File.ReadAllText(sentinel) == "keep me", "existing export modified");
    Check(folder != Path.GetFullPath(oldFolder), "new default reuses legacy shared folder");
});
Test("explicit output_dir preserved", () =>
{
    string explicitFolder = Path.Combine(outputRoot, "explicit");
    Check(Export(new Document(), directory: explicitFolder).GetProperty("outputDir").GetString() == Path.GetFullPath(explicitFolder), "explicit directory changed");
});
Test("failed export preserves and reports partially written file", () =>
{
    string directory = Path.Combine(outputRoot, "partial"); string message = "";
    try { Export(new Document { FailExportAfterWrite = true }, directory: directory); } catch (Exception ex) { message = ex.Message; }
    string file = Directory.GetFiles(directory).Single();
    Check(message.Contains(file) && message.Contains("may be incomplete") && message.Contains("not removed"), "partial file omitted from error: " + message);
});
Test("IFC commit has failure guard", () =>
{
    var doc = new Document(); Export(doc, "ifc"); Check(doc.HadPreprocessorAtCommit, "IFC commit unguarded");
});
Test("IFC validates current status after commit returns Committed", () =>
{
    var doc = new Document { FinalCommitStatus = TransactionStatus.Pending }; string message = "";
    try { Export(doc, "ifc"); } catch (Exception ex) { message = ex.Message; }
    Check(message.Contains("Pending") && message.Contains("not confirmed") && !message.Contains("was rolled back"), "unconfirmed commit reported as success: " + message);
});
Test("failed IFC commit reports rollback and retained file", () =>
{
    var doc = new Document(); doc.Failures.Add(new(FailureSeverity.Error, "IFC commit rejected"));
    string directory = Path.Combine(outputRoot, "ifc-rollback"); string message = "";
    try { Export(doc, "ifc", directory); } catch (Exception ex) { message = ex.Message; }
    Check(message.Contains("IFC commit rejected") && message.Contains("rolled back") && message.Contains("may be incomplete"), "missing IFC rollback/file status: " + message);
    Check(Directory.GetFiles(directory).Length == 1, "IFC artifact should remain");
});

if (!args.Contains("--baseline"))
{
    Test("confirmed rollback reports confirmation", () =>
    {
        using var tx = new Transaction(new Document(), "test"); tx.Start();
        Check(RollbackMessage(tx).Contains("was rolled back"), "confirmed rollback not reported");
    });
    Test("pending commit never reported as rolled back", () =>
    {
        using var tx = new Transaction(new Document { CommitStatus = TransactionStatus.Pending }, "test"); tx.Start(); tx.Commit();
        string message = RollbackMessage(tx);
        Check(message.Contains("Pending") && message.Contains("not confirmed") && !message.Contains("was rolled back"), message);
    });
    Test("rollback exception is reported without false success", () =>
    {
        using var tx = new Transaction(new Document { ThrowOnRollback = true }, "test"); tx.Start();
        string message = RollbackMessage(tx);
        Check(message.Contains("injected rollback failure") && message.Contains("could not be confirmed") && !message.Contains("was rolled back"), message);
    });
    Test("committed transaction never reported as rolled back", () =>
    {
        using var tx = new Transaction(new Document(), "test"); tx.Start(); tx.Commit();
        string message = RollbackMessage(tx);
        Check(message.Contains("Committed") && message.Contains("not confirmed") && !message.Contains("was rolled back"), message);
    });
}
Console.WriteLine($"Results: {passed} passed, {failed} failed. Test files: {Path.GetFullPath(outputRoot)}");
string results = JsonSerializer.Serialize(new { passed, failed, failures, outputRoot }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(outputRoot, "results.json"), results);
File.WriteAllText(Path.Combine(evidenceRoot, "results.json"), results);
return failed == 0 ? 0 : 1;
