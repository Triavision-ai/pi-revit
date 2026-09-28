using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// Optional tool return shape: complete structured payload plus a display summary.
    /// The bridge/extension expose complete bounded data or explicit result retrieval to
    /// the model; structured details remain available for rendering and diagnostics.
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

        /// <summary>Potential effects, independent of whether this particular call changes anything.</summary>
        IReadOnlyList<string> Effects => Write ? new[] { "model" } : Array.Empty<string>();

        /// <summary>
        /// False only for tools that never touch the Revit API. They skip the bridge's
        /// no-document 409 gate and run directly on the server task instead of the Revit
        /// API thread, so ToolContext.Document/UIApplication are null for them.
        /// </summary>
        bool RequiresDocument => true;

        /// <summary>Activation tier metadata: "core" (default) or "advanced".</summary>
        string Tier => "core";

        /// <summary>
        /// Document kinds the tool works in: DocumentKind.Both or DocumentKind.ProjectOnly. Every
        /// bridge tool declares it (the documentation gate requires the declaration); the
        /// dispatcher refuses other kinds before the tool runs.
        /// </summary>
        IReadOnlyList<string> DocumentKinds => DocumentKind.Both;

        /// <summary>Optional one-line "Available tools" system prompt entry.</summary>
        string? PromptSnippet => null;

        /// <summary>Optional system prompt guideline bullets. Each bullet must name the tool.</summary>
        IReadOnlyList<string>? PromptGuidelines => null;

        /// <summary>
        /// English discovery terms beyond the name and description: synonyms, outcome words
        /// ("screenshot"), verbs and Revit nouns. Other UI languages are mapped onto these
        /// terms by discovery alias files, never by translating tool metadata.
        /// </summary>
        IReadOnlyList<string> Keywords => Array.Empty<string>();

        /// <summary>Declared capability boundaries, each with an alternative route.</summary>
        IReadOnlyList<ToolLimit> Limits => Array.Empty<ToolLimit>();

        /// <summary>
        /// Minimum sufficient check that the requested outcome happened: "reread" (query the
        /// changed state again), "capture" (inspect an image of the visible result),
        /// "inspect_output" (open the produced file) or "none" (no durable outcome to check).
        /// Required when the tool can write or has effects; null for pure reads.
        /// </summary>
        string? Verification => null;

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
            registry.Add(new GetLinkedModels());
            registry.Add(new GetLinkedElements());
            registry.Add(new GetSchedules());
            registry.Add(new GetElementRelationships());
            registry.Add(new SummarizeElements());
            registry.Add(new ManageElementSets());
            registry.Add(new TransformElements());
            registry.Add(new Tools.DeleteElements());
            registry.Add(new ChangeElementTypes());
            registry.Add(new ManageViews());
            registry.Add(new ManageSheets());
            registry.Add(new ManageSheetPlacements());
            registry.Add(new GetScheduleFields());
            registry.Add(new ManageSchedules());
            registry.Add(new CreateTags());
            registry.Add(new QuerySpatialElements());
            registry.Add(new MeasureGeometry());
            registry.Add(new GetModelCoordinates());
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
            parameters = DescribeParameters(tool),
            executionMode = "sequential",
            write = tool.Write,
            effects = tool.Effects,
            requiresDocument = tool.RequiresDocument,
            documentKinds = tool.DocumentKinds,
            keywords = tool.Keywords,
            limits = tool.Limits.Select(limit => new { what = limit.What, alternative = new { kind = limit.Alternative, @ref = limit.Ref } }).ToArray(),
            verification = tool.Verification,
            promptSnippet = tool.PromptSnippet,
            // Cross-cutting rules (document identity, manuals, capability and completion
            // protocols) are stated once by the Pi extension's platform section, not
            // repeated per tool; guidelines here are tool-specific.
            promptGuidelines = tool.PromptGuidelines,
        };

        private static object DescribeParameters(ITool tool)
        {
            if (!tool.RequiresDocument) return tool.ParametersSchema;
            var schema = JsonSerializer.SerializeToNode(tool.ParametersSchema)!.AsObject();
            var properties = schema["properties"]!.AsObject();
            bool identityRequired = tool.Write || DocumentGuard.AlwaysRequiresIdentity(tool.Name);
            // The description must agree with requiredness in the same schema.
            properties["expected_document_id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = identityRequired
                    ? "Required: exact opaque project.documentId from get_model_overview for the intended open document. Invalid after close/reopen or bridge restart."
                    : "Exact opaque project.documentId from get_model_overview. Not required by the schema for reads, but required at runtime for any model change or UI change this tool performs; when supplied, the call runs only against that document. Invalid after close/reopen or bridge restart."
            };
            if (identityRequired)
            {
                var required = schema["required"] as JsonArray ?? new JsonArray();
                if (!required.Any(x => x?.GetValue<string>() == "expected_document_id")) required.Add("expected_document_id");
                schema["required"] = required;
            }
            return schema;
        }
    }
}
