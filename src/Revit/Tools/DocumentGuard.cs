using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

/// <summary>Opaque identity of one open native document in this loaded bridge session.</summary>
internal static class DocumentGuard
{
    private sealed record Identity(Document Document, string Value);
    private static readonly string Generation = Guid.NewGuid().ToString("N");
    private static readonly List<Identity> Identities = new();
    /// <summary>The shape GetIdentity produces: generation and document, each a "N"-format Guid.</summary>
    private static readonly System.Text.RegularExpressions.Regex IdentityShape = new("^[0-9a-f]{32}:[0-9a-f]{32}$");
    /// <summary>The short form ShortId produces.</summary>
    private static readonly System.Text.RegularExpressions.Regex ShortShape = new("^doc-[0-9a-f]{8}$");

    /// <summary>A short form of an identity for clients that type or copy it: "doc-" and the first 8 hex
    /// digits of its document part (random per open document and session, like the full identity).
    /// Accepted wherever the identity is, as long as no other open document shares it.</summary>
    public static string ShortId(string identity) => "doc-" + identity[(identity.IndexOf(':') + 1)..][..8];

    /// <summary>True when value is this document's identity, or its short form shared by no other open document.</summary>
    public static bool Matches(string? value, Document document)
    {
        string identity = GetIdentity(document);
        if (string.Equals(value, identity, StringComparison.Ordinal)) return true;
        return value != null && value == ShortId(identity) && !IsAmbiguousShortId(value);
    }

    private static bool IsAmbiguousShortId(string value)
    {
        Identities.RemoveAll(entry => !entry.Document.IsValidObject);
        return Identities.Count(entry => ShortId(entry.Value) == value) > 1;
    }

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

    /// <summary>Project or family: the kind of the active document, as tools declare it.</summary>
    public static string KindOf(Document document) => document.IsFamilyDocument ? DocumentKind.Family : DocumentKind.Project;

    /// <summary>Refuse a tool in a document kind it does not declare, before it runs (inv:document-kind-declared).</summary>
    public static void CheckKind(ITool tool, Document document)
    {
        if (DocumentKind.Refusal(tool.Name, tool.DocumentKinds, KindOf(document), document.Title) is { } refusal)
            throw new ArgumentException(refusal);
    }

    /// <summary>Must run on the Revit API thread immediately before the tool action.</summary>
    public static void CheckForTool(JsonElement args, Document document, string toolName, bool writes = false)
    {
        bool required = writes || AlwaysRequiresIdentity(toolName)
            || (toolName == "manage_selection"
                && (!string.Equals((JsonArgs.GetString(args, "action") ?? "get").Trim(), "get", StringComparison.OrdinalIgnoreCase)
                    || JsonArgs.GetBool(args, "isolate_in_view", false)));
        if (required && !args.TryGetProperty("expected_document_id", out _))
            throw new ArgumentException("expected_document_id is required for this operation: the project.documentId of the intended open document from get_model_overview (the pi-revit extension supplies it automatically). A title alone cannot identify a document. No action was performed.");
        CheckExpectedDocument(args, document);
    }

    public static void CheckExpectedDocument(JsonElement args, Document document)
    {
        if (args.TryGetProperty("expected_document_id", out var id))
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new ArgumentException("expected_document_id must be a non-empty identity from get_model_overview. No action was performed.");
            string value = id.GetString()!;
            // A malformed value is a copying mistake, not a different or stale document: say so, so a
            // client does not ask the user to reopen a model that is open and correct.
            if (!IdentityShape.IsMatch(value) && !ShortShape.IsMatch(value))
                throw new ArgumentException($"expected_document_id '{value}' is not a complete document identity ({value.Length} characters; an identity has 65: 32 hex digits, ':', 32 hex digits, or its short form doc- and 8 hex digits). It was probably copied incompletely. Pass project.documentId or project.shortDocumentId from get_model_overview exactly. No action was performed.");
            if (ShortShape.IsMatch(value) && IsAmbiguousShortId(value))
                throw new ArgumentException($"The short document ID '{value}' belongs to more than one open document. Pass the full project.documentId from get_model_overview. No action was performed.");
            if (!Matches(value, document))
                throw new ArgumentException($"The active document '{document.Title}' is not the document this call was bound to: another document is active, the document was reopened or Revit restarted, or the identity was copied incorrectly. No action was performed. Read get_model_overview again; if the active document is not the intended one, ask the user.");
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
