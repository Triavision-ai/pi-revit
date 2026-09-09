using System.Text.Json;

namespace Autodesk.Revit.DB
{
    // Multiple managed wrappers share one native lifetime. Closing invalidates
    // all wrappers; reopening the same file creates a new native lifetime.
    internal sealed class NativeDocument(string title)
    {
        public string Title { get; set; } = title;
        public bool IsOpen { get; set; } = true;
    }
    internal sealed class Document(NativeDocument native)
    {
        public string Title => native.Title;
        public bool IsValidObject => native.IsOpen;
        public override bool Equals(object? other) => other is Document document && ReferenceEquals(native, document.Native);
        public override int GetHashCode() => native.GetHashCode();
        private NativeDocument Native => native;
    }
}
namespace RevitBridge.Tools
{
    internal static class JsonArgs
    {
        public static string? GetString(JsonElement args, string name)
            => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        public static bool GetBool(JsonElement args, string name, bool fallback)
            => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : fallback;
    }
}
