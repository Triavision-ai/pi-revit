using System.Text.Json;

namespace Autodesk.Revit.DB
{
    public enum TransactionStatus { Uninitialized, Started, Committed, RolledBack, Pending, Error }
    public enum FailureSeverity { None, Warning, Error }
    public enum FailureProcessingResult { Continue, ProceedWithRollBack }
    public interface IFailuresPreprocessor { FailureProcessingResult PreprocessFailures(FailuresAccessor accessor); }
    public class FailureMessageAccessor(FailureSeverity severity, string text)
    {
        public FailureSeverity GetSeverity() => severity;
        public string GetDescriptionText() => text;
    }
    public class FailuresAccessor(List<FailureMessageAccessor> messages)
    {
        public int DeletedWarnings { get; private set; }
        public IEnumerable<FailureMessageAccessor> GetFailureMessages() => messages;
        public void DeleteWarning(FailureMessageAccessor message) => DeletedWarnings++;
    }
    public class FailureHandlingOptions
    {
        public IFailuresPreprocessor? Preprocessor { get; private set; }
        public bool ClearAfterRollback { get; private set; }
        public void SetFailuresPreprocessor(IFailuresPreprocessor value) => Preprocessor = value;
        public void SetClearAfterRollback(bool value) => ClearAfterRollback = value;
    }
    public class Transaction(Document doc, string name) : IDisposable
    {
        public string Name => name;
        private TransactionStatus status;
        private FailureHandlingOptions options = new();
        public TransactionStatus Start() { options = new(); return status = TransactionStatus.Started; }
        public TransactionStatus GetStatus() => status;
        public FailureHandlingOptions GetFailureHandlingOptions() => options;
        public void SetFailureHandlingOptions(FailureHandlingOptions value) => options = value;
        public TransactionStatus Commit()
        {
            doc.HadPreprocessorAtCommit = options.Preprocessor != null;
            if (options.Preprocessor?.PreprocessFailures(new FailuresAccessor(doc.Failures)) == FailureProcessingResult.ProceedWithRollBack)
                return status = TransactionStatus.RolledBack;
            status = doc.FinalCommitStatus ?? doc.CommitStatus;
            return doc.CommitStatus;
        }
        public TransactionStatus RollBack()
        {
            if (doc.ThrowOnRollback) throw new InvalidOperationException("injected rollback failure");
            return status = doc.RollbackStatus;
        }
        public void Dispose() { }
    }
    public record ElementId(long Value);
    public class Element
    {
        public ElementId Id { get; set; } = new(1);
        public string Name { get; set; } = "element";
    }
    public enum TemporaryViewMode { TemporaryHideIsolate }
    public class View : Element
    {
        public bool IsTemplate { get; set; }
        public bool ThrowOnIsolate { get; set; }
        public void DisableTemporaryViewMode(TemporaryViewMode mode) { if (ThrowOnIsolate) throw new InvalidOperationException("unsupported isolate"); }
        public void IsolateElementsTemporary(ICollection<ElementId> ids) { if (ThrowOnIsolate) throw new InvalidOperationException("unsupported isolate"); }
    }
    public class ViewSchedule : View { }
    public class ModelPath
    {
        public string Region { get; set; } = "US";
        public Guid Project { get; set; } = Guid.NewGuid();
        public Guid Model { get; set; } = Guid.NewGuid();
        public Guid GetProjectGUID() => Project;
        public Guid GetModelGUID() => Model;
    }
    public class ProjectInfo { public string UniqueId { get; set; } = "inherited-template-id"; }
    public class Document
    {
        public Guid OpenDocumentId { get; set; } = Guid.NewGuid();
        public bool IsValidObject { get; set; } = true;
        public override bool Equals(object? other) => other is Document doc && doc.OpenDocumentId == OpenDocumentId;
        public override int GetHashCode() => OpenDocumentId.GetHashCode();
        public string Title { get; set; } = "SameTitle";
        public string PathName { get; set; } = "";
        public bool IsModelInCloud { get; set; }
        public ModelPath CloudPath { get; set; } = new();
        public bool CloudPathUnavailable { get; set; }
        public ModelPath GetCloudModelPath() => CloudPathUnavailable ? throw new InvalidOperationException("cloud identity unavailable") : CloudPath;
        public ProjectInfo ProjectInformation { get; } = new();
        public Dictionary<long, Element> Elements { get; } = new() { [1] = new View() };
        public Element? GetElement(ElementId id) => Elements.GetValueOrDefault(id.Value);
        public View? ActiveView { get; set; } = new View();
        public TransactionStatus CommitStatus { get; set; } = TransactionStatus.Committed;
        public TransactionStatus? FinalCommitStatus { get; set; }
        public TransactionStatus RollbackStatus { get; set; } = TransactionStatus.RolledBack;
        public bool ThrowOnRollback { get; set; }
        public bool HadPreprocessorAtCommit { get; set; }
        public List<FailureMessageAccessor> Failures { get; } = new();
        public bool FailExportAfterWrite { get; set; }
        private bool WriteExport(string path)
        {
            File.WriteAllText(path, Guid.NewGuid().ToString());
            return !FailExportAfterWrite;
        }
        public bool Export(string dir, IList<ElementId> ids, PDFExportOptions options) => WriteExport(Path.Combine(dir, options.FileName + ".pdf"));
        public bool Export(string dir, string name, IList<ElementId> ids, DWGExportOptions options) => WriteExport(Path.Combine(dir, name + ".dwg"));
        public bool Export(string dir, string name, IFCExportOptions options) => WriteExport(Path.Combine(dir, name + ".ifc"));
        public void ExportImage(ImageExportOptions options) { if (!WriteExport(options.FilePath + ".png")) throw new InvalidOperationException("partial PNG failure"); }
    }
    public class PDFExportOptions { public bool Combine { get; set; } public string FileName { get; set; } = ""; }
    public class DWGExportOptions { }
    public class IFCExportOptions { public ElementId FilterViewId { get; set; } = new(0); }
    public static class OptionalFunctionalityUtils { public static bool IsDWGExportAvailable() => true; }
    public enum ExportRange { SetOfViews }
    public enum ImageFileType { PNG }
    public enum ImageResolution { DPI_150 }
    public enum ZoomFitType { FitToPage }
    public enum FitDirectionType { Horizontal }
    public class ImageExportOptions
    {
        public ExportRange ExportRange { get; set; }
        public string FilePath { get; set; } = "";
        public ImageFileType HLRandWFViewsFileType { get; set; }
        public ImageFileType ShadowViewsFileType { get; set; }
        public ImageResolution ImageResolution { get; set; }
        public ZoomFitType ZoomType { get; set; }
        public int PixelSize { get; set; }
        public FitDirectionType FitDirection { get; set; }
        public void SetViewsAndSheets(IList<ElementId> ids) { }
    }
}
namespace Autodesk.Revit.UI
{
    using Autodesk.Revit.DB;
    public class Selection
    {
        private List<ElementId> selected = new();
        public ICollection<ElementId> GetElementIds() => selected.ToList();
        public void SetElementIds(ICollection<ElementId> ids) => selected = ids.ToList();
    }
    public class UIDocument(Document doc)
    {
        public Document Document => doc;
        public Selection Selection { get; } = new();
        public View? ActiveGraphicalView { get; set; } = doc.ActiveView;
        public void ShowElements(ICollection<ElementId> ids) { }
    }
    public class UIApplication { public UIDocument? ActiveUIDocument { get; set; } }
}
namespace Autodesk.Revit.Exceptions
{
    public class ApplicationException(string message) : Exception(message) { }
    public class InvalidOperationException(string message) : Exception(message) { }
}
namespace RevitBridge.Tools
{
    using Autodesk.Revit.DB;
    using Autodesk.Revit.UI;
    internal interface ITool { }
    internal class NoActiveDocumentException : Exception { }
    internal record ToolOutput(object Data, string Compact);
    internal class ToolContext(Document doc)
    {
        public Document Document => doc;
        public UIApplication UIApplication { get; } = new() { ActiveUIDocument = new UIDocument(doc) };
    }
    // Redirect default Documents exports into a unique test workspace; never touch
    // the user's real Documents/pi-revit folder.
    internal static class Environment
    {
        public enum SpecialFolder { MyDocuments }
        public static string TestDocuments { get; set; } = "";
        public static string GetFolderPath(SpecialFolder folder) => TestDocuments;
    }
    internal static class JsonArgs
    {
        public static string? GetString(JsonElement args, string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        public static bool GetBool(JsonElement args, string key, bool fallback) => args.TryGetProperty(key, out var value) ? value.GetBoolean() : fallback;
        public static List<long> GetLongArray(JsonElement args, string key) => args.GetProperty(key).EnumerateArray().Select(x => x.GetInt64()).ToList();
    }
    internal static class ElementIdentity
    {
        public static IReadOnlyList<string> Fields = new[] { "id", "name" };
        public static Dictionary<string, object?> Build(Document doc, Element element, IReadOnlyList<string> fields) => new() { ["id"] = element.Id.Value, ["name"] = element.Name };
    }
}
