using System.Text.Json;

namespace RevitBridge.Tools;

/// <summary>Shared bounded pixel-input validation; limits are tool resource budgets, not Revit API limits.</summary>
internal static class ImageResolutionOptions
{
    internal const int DefaultExportWidth = 2048;
    internal const int MaxExportWidth = 8192;
    internal const int DefaultCaptureLongEdge = 1568;
    internal const int MaxCaptureLongEdge = 4096;

    internal static int ReadPixels(JsonElement args, string name, int fallback, int minimum, int maximum)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int pixels) || pixels < minimum || pixels > maximum)
            throw new ArgumentException($"{name} must be an integer from {minimum} to {maximum}. No image export was started.");
        return pixels;
    }
}
