using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

/// <summary>Opaque identity of one open native document in this loaded bridge session.</summary>
internal static class DocumentGuard
{
    private sealed record Identity(Document Document, string Value);
    private static readonly string Generation = Guid.NewGuid().ToString("N");
    private static readonly List<Identity> Identities = new();

    public static string GetIdentity(Document document)
    {
        if (!document.IsValidObject)
            throw new ArgumentException("The target document is closed or invalid. Read get_model_overview again.");
        // Revit can supply multiple managed wrappers for the same native document.
        // Its documented Equals semantics identify that native document; reference identity cannot.
        // All accesses happen on the Revit API thread. Pruning avoids retaining closed documents.
        Identities.RemoveAll(entry => !entry.Document.IsValidObject);
        var existing = Identities.FirstOrDefault(entry => entry.Document.Equals(document));
        if (existing != null) return existing.Value;
        var created = new Identity(document, Generation + ":" + Guid.NewGuid().ToString("N"));
        Identities.Add(created);
        return created.Value;
    }

    public static bool AlwaysRequiresIdentity(string toolName)
        => toolName is "set_parameters" or "execute_csharp" or "export_documents" or "open_view";

    /// <summary>Must run on the Revit API thread immediately before the tool action.</summary>
    public static void CheckForTool(JsonElement args, Document document, string toolName)
    {
        bool required = AlwaysRequiresIdentity(toolName)
            || (toolName == "manage_selection"
                && (!string.Equals((JsonArgs.GetString(args, "action") ?? "get").Trim(), "get", StringComparison.OrdinalIgnoreCase)
                    || JsonArgs.GetBool(args, "isolate_in_view", false)));
        if (required && !args.TryGetProperty("expected_document_id", out _))
            throw new ArgumentException("expected_document_id is required for this operation. Read project.documentId from get_model_overview for the intended open document and pass it unchanged. A title alone cannot identify a document. No action was performed.");
        CheckExpectedDocument(args, document);
    }

    public static void CheckExpectedDocument(JsonElement args, Document document)
    {
        if (args.TryGetProperty("expected_document_id", out var id))
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new ArgumentException("expected_document_id must be a non-empty identity from get_model_overview. No action was performed.");
            if (!string.Equals(id.GetString(), GetIdentity(document), StringComparison.Ordinal))
                throw new ArgumentException($"The active document '{document.Title}' is not the exact open document this call expected, or that identity is stale after reopening/restarting. No action was performed. Activate the intended document and read get_model_overview again.");
        }

        // Retained as an additional human-readable check, never as a substitute for identity.
        string? expected = JsonArgs.GetString(args, "expected_document");
        if (string.IsNullOrWhiteSpace(expected)) return;
        static string Normalize(string title)
        {
            string value = title.Trim();
            return value.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
        }
        if (!string.Equals(Normalize(expected), Normalize(document.Title), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Active document is '{document.Title}' but this call expected title '{expected}'. No action was performed. Read get_model_overview for the intended document.");
    }
}
