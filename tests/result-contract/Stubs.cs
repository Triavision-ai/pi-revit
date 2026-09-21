using System.Text.Json;

// Only collaborators are simulated. The response builder is compiled directly
// from production BridgeServer.cs, and no server, queue, or Revit API is run.
namespace RevitBridge
{
    internal sealed record ToolOutput(object? Payload, string? CompactText = null);
    internal sealed record ToolContext(object? Document, object? UIApplication);
    internal interface ITool
    {
        string Name { get; }
        bool RequiresDocument { get; }
        bool Write => false;
        object? Execute(JsonElement args, ToolContext context);
    }
    internal sealed class ToolRegistry
    {
        public IReadOnlyList<object> DescribeAll() => Array.Empty<object>();
        public ITool? Get(string name) => null;
        public object Describe(ITool tool) => new { };
    }
    internal sealed class FakeUIApplication
    {
        public FakeUIDocument? ActiveUIDocument => null;
    }
    internal sealed class FakeUIDocument { public object Document => new(); }
    internal sealed class CommandQueue
    {
        public Task<T> RunAsync<T>(Func<FakeUIApplication, T> action, TimeSpan? timeout)
            => throw new NotSupportedException("The offline response test must not dispatch API work.");
    }
    internal sealed class NoActiveDocumentException : Exception { }
}
namespace RevitBridge.Tools
{
    internal static class DocumentGuard
    {
        public static void CheckForTool(JsonElement args, object document, string toolName, bool writes = false)
            => throw new NotSupportedException("The offline response test must not inspect Revit documents.");
    }
}
