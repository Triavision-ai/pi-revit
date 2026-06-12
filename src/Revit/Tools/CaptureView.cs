using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Exports a PNG of a view to a temp file for the agent to open with the read
    /// tool (D10): the response carries only the file path and image metadata,
    /// never base64 image data. The long edge is clamped to 1568 px; the fit
    /// direction comes from the view outline and is verified against the produced
    /// PNG header, re-exporting once along the long edge if the guess was wrong.
    /// </summary>
    internal sealed class CaptureView : ITool
    {
        private const int MaxLongEdgePx = 1568;

        public string Name => "capture_view";
        public string Label => "Capture View";
        public string Description => "Export a PNG snapshot of a Revit view to a temporary file and return its path — the response contains NO image data; open the returned filePath with the read tool to actually see the image. Defaults to the active view; pass view_id for any other graphical view or sheet (find ids with get_elements, category 'Views' or 'Sheets'). The long image edge is capped at 1568 px. Schedules and view templates cannot be captured.";
        public string Tier => "advanced";

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                view_id = new
                {
                    type = "integer",
                    description = "Element id of the view or sheet to capture. Default: the active view.",
                },
            },
            required = Array.Empty<string>(),
        };

        public string? PromptSnippet => "Export a PNG of a Revit view to a temp file (open it with the read tool).";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "capture_view returns a filePath and never image data — open that file with the read tool to actually see the view.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();
            var view = ResolveView(doc, args);

            string prefix = Path.Combine(Path.GetTempPath(), "revit_view_" + Guid.NewGuid().ToString("N"));
            ExportPng(doc, view, prefix, GuessLandscape(view));
            string filePath = FindExportedFile(prefix);
            var (width, height) = ReadPngSize(filePath);

            // The outline-based aspect guess can be wrong (crop boxes, title blocks);
            // the PNG header is authoritative. One re-export with the fit direction
            // on the long edge guarantees the <= 1568 px clamp.
            if (width > 0 && Math.Max(width, height) > MaxLongEdgePx)
            {
                File.Delete(filePath);
                ExportPng(doc, view, prefix, horizontal: width >= height);
                filePath = FindExportedFile(prefix);
                (width, height) = ReadPngSize(filePath);
            }

            long fileSizeBytes = new FileInfo(filePath).Length;
            string compact = $"Captured view '{view.Name}' (id {view.Id.Value}) to {filePath} ({width}x{height} px, {Math.Max(1, fileSizeBytes / 1024)} KB). Open filePath with the read tool to see it.";
            return new ToolOutput(new
            {
                filePath,
                viewId = view.Id.Value,
                viewName = view.Name,
                width,
                height,
                fileSizeBytes,
            }, compact);
        }

        private static View ResolveView(Document doc, JsonElement args)
        {
            View view;
            if (JsonArgs.GetLong(args, "view_id") is { } viewId)
            {
                view = doc.GetElement(new ElementId(viewId)) as View
                    ?? throw new ArgumentException($"view_id {viewId} is not a view. Find views with get_elements (category 'Views' or 'Sheets').");
                if (view.IsTemplate)
                    throw new ArgumentException($"View '{view.Name}' ({viewId}) is a view template and cannot be captured.");
            }
            else
            {
                view = doc.ActiveView
                    ?? throw new ArgumentException("No active view to capture; pass view_id.");
            }

            if (view is ViewSchedule)
                throw new ArgumentException($"View '{view.Name}' is a schedule; schedules cannot be exported as images. Capture a graphical view or sheet, or query the data with get_elements instead.");
            return view;
        }

        private static void ExportPng(Document doc, View view, string prefix, bool horizontal)
        {
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = prefix,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = MaxLongEdgePx,
                FitDirection = horizontal ? FitDirectionType.Horizontal : FitDirectionType.Vertical,
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            try
            {
                doc.ExportImage(options);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new InvalidOperationException($"Revit could not export view '{view.Name}' as an image: {ex.Message}");
            }
        }

        /// <summary>Initial fit direction from the view outline; verified against the produced PNG.</summary>
        private static bool GuessLandscape(View view)
        {
            try
            {
                var outline = view.Outline;
                return outline.Max.U - outline.Min.U >= outline.Max.V - outline.Min.V;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>ExportRange.SetOfViews appends " - [view type] - [view name]" to the
        /// file path prefix, so the produced name is discovered by globbing the prefix.</summary>
        private static string FindExportedFile(string prefix)
        {
            string directory = Path.GetDirectoryName(prefix)!;
            var matches = Directory.GetFiles(directory, Path.GetFileName(prefix) + "*.png");
            return matches.Length > 0
                ? matches[0]
                : throw new InvalidOperationException("Revit reported a successful export but no PNG file was produced.");
        }

        /// <summary>Reads width/height from the PNG IHDR chunk (offsets 16/20, big-endian).</summary>
        private static (int Width, int Height) ReadPngSize(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                Span<byte> header = stackalloc byte[24];
                stream.ReadExactly(header);
                if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
                    return (0, 0);
                int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return (width, height);
            }
            catch
            {
                return (0, 0);
            }
        }
    }
}
