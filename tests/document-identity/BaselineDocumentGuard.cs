// Original ToolSupport.cs guard at fc9d78554760ae27df4c1f4fcac29aae2db9179f.
// Extracted unchanged except class name; original blob 3d1e1d3f02b1ebb16ec8366009c4d4f012cc3254.
using System.Text.Json;
using Autodesk.Revit.DB;
namespace RevitBridge.Tools;
    internal static class BaselineDocumentGuard
    {
        /// <summary>Throws when expected_document is provided and does not match the
        /// active document's title (case-insensitive; the .rvt extension and a
        /// detached suffix are tolerated). No-op when the argument is absent.</summary>
        public static void CheckExpectedDocument(JsonElement args, Document doc)
        {
            string? expected = JsonArgs.GetString(args, "expected_document");
            if (string.IsNullOrWhiteSpace(expected))
                return;
            string actual = doc.Title;
            if (Matches(expected, actual))
                return;
            throw new ArgumentException(
                $"Active document is '{actual}' but this call expected '{expected}'. Nothing was changed. "
                + "The user switched models (or several are open); re-read the target model (get_model_overview) and retry against the right one.");
        }

        private static bool Matches(string expected, string actual)
        {
            static string Normalize(string value)
            {
                string v = value.Trim();
                if (v.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                    v = v[..^4];
                if (v.EndsWith("_detached", StringComparison.OrdinalIgnoreCase))
                    v = v[..^"_detached".Length];
                return v;
            }
            return string.Equals(Normalize(expected), Normalize(actual), StringComparison.OrdinalIgnoreCase);
        }
    }
