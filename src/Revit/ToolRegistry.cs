using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitBridge.Tools;

namespace RevitBridge
{
    /// <summary>
    /// Execution context handed to tools. Document and UIApplication are non-null when the
    /// tool runs on the Revit API thread (RequiresDocument = true, the default). Tools with
    /// RequiresDocument = false run on the bridge's server task with both set to null and
    /// must not touch the Revit API.
    /// </summary>
    internal sealed record ToolContext(Document? Document, UIApplication? UIApplication);

    /// <summary>
    /// Optional tool return shape: compact text for model context plus the full payload for
    /// details. Tools may instead return any plain JSON-serializable object, which the bridge
    /// serializes into both channels (capped in content).
    /// </summary>
    internal sealed record ToolOutput(object? Payload, string? CompactText = null);

    /// <summary>A single Revit tool exposed through the bridge.</summary>
    internal interface ITool
    {
        string Name { get; }
        string Label { get; }
        string Description { get; }

        /// <summary>JSON schema (anonymous object) describing the tool arguments.</summary>
        object ParametersSchema { get; }

        /// <summary>True only for tools that mutate the model. Write tools own their transaction.</summary>
        bool Write => false;

        /// <summary>
        /// False only for tools that never touch the Revit API. They skip the bridge's
        /// no-document 409 gate and run directly on the server task instead of the Revit
        /// API thread, so ToolContext.Document/UIApplication are null for them.
        /// </summary>
        bool RequiresDocument => true;

        /// <summary>Activation tier metadata: "core" (default) or "advanced".</summary>
        string Tier => "core";

        /// <summary>Optional one-line "Available tools" system prompt entry.</summary>
        string? PromptSnippet => null;

        /// <summary>Optional system prompt guideline bullets. Each bullet must name the tool.</summary>
        IReadOnlyList<string>? PromptGuidelines => null;

        /// <summary>
        /// Runs on the Revit API thread (or the server task when RequiresDocument is false).
        /// Returns a ToolOutput or any plain JSON-serializable payload.
        /// </summary>
        object? Execute(JsonElement args, ToolContext context);
    }

    /// <summary>Name -> tool map plus the metadata projection served by GET /tools.</summary>
    internal sealed class ToolRegistry
    {
        private readonly SortedDictionary<string, ITool> _tools = new(StringComparer.Ordinal);

        public static ToolRegistry CreateDefault()
        {
            var registry = new ToolRegistry();
            registry.Add(new SearchApiDocs());
            registry.Add(new ExecuteCsharp());
            registry.Add(new GetModelOverview());
            registry.Add(new GetElements());
            registry.Add(new GetElementTypes());
            registry.Add(new GetElementDetails());
            registry.Add(new ManageSelection());
            registry.Add(new OpenView());
            registry.Add(new SetParameters());
            registry.Add(new CaptureView());
            registry.Add(new ExportDocuments());
            registry.Add(new GetModelHealth());
            return registry;
        }

        public void Add(ITool tool)
        {
            if (_tools.ContainsKey(tool.Name))
                throw new InvalidOperationException($"Duplicate tool name: {tool.Name}");
            _tools[tool.Name] = tool;
        }

        public ITool? Get(string name)
            => _tools.TryGetValue(name, out var tool) ? tool : null;

        public IReadOnlyList<object> DescribeAll()
            => _tools.Values.Select(Describe).ToArray();

        public object Describe(ITool tool) => new
        {
            name = tool.Name,
            label = tool.Label,
            description = tool.Description,
            category = tool.Write ? "write" : "read",
            tier = tool.Tier,
            parameters = tool.ParametersSchema,
            executionMode = "sequential",
            write = tool.Write,
            requiresDocument = tool.RequiresDocument,
            promptSnippet = tool.PromptSnippet,
            promptGuidelines = tool.PromptGuidelines,
        };
    }
}
