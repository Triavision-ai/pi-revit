using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Exports a PNG of a view to a temp file for the agent to open with the read
    /// tool: the response carries only the file path and image metadata,
    /// never base64 image data. The long edge defaults to 1568 px; the fit
    /// direction comes from the view outline and is verified against the produced
    /// PNG header, re-exporting once along the long edge if the guess was wrong.
    /// </summary>
    internal sealed class CaptureView : ITool
    {

        /// <summary>How long a captured PNG stays on disk before a later capture sweeps it.
        /// Long enough that a capture is never pulled out from under a session still reading it,
        /// and the same as the Pi extension's saved results beside it in the workspace temp folder.</summary>
        private static readonly TimeSpan CaptureRetention = TimeSpan.FromDays(7);

        public string Name => "capture_view";
        public IReadOnlyList<string> DocumentKinds => DocumentKind.Both;
        public IReadOnlyList<string> Keywords => new[] { "screenshot", "image", "png", "picture", "snapshot", "visual check" };
        public IReadOnlyList<ToolLimit> Limits => new[]
        {
            new ToolLimit("Image contents or visual judgement (returns a file path only)", "tool", "read (open the returned PNG)"),
            new ToolLimit("Deliverable PDF, DWG or IFC files", "tool", "export_documents"),
        };
        public string? Verification => "inspect_output";
        public IReadOnlyList<string> Effects => new[] { "files" };
        public string Label => "Capture View";
        public string Description => "Export a PNG snapshot of a Revit view to a short-lived file (kept 7 days in the pi-revit workspace's temp\\captures folder) and return its path — the response contains NO image data; open filePath to see it. Defaults to the active view; pass view_id for another graphical view or sheet. scope=visible_region captures only the current UI zoom of the active view; it never activates a different view. Default scope=entire_view fits the whole view. max_dimension sets the long-edge limit (256-4096 px, default 1568). Schedules and view templates cannot be captured.";
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
                max_dimension = new
                {
                    type = "integer",
                    minimum = 256,
                    maximum = ImageResolutionOptions.MaxCaptureLongEdge,
                    @default = ImageResolutionOptions.DefaultCaptureLongEdge,
                    description = "Maximum long image edge in pixels (256-4096). Default 1568. Choose a smaller size for a quick look, or frame a close-up and increase it for fine detail. Larger captures use more time and memory.",
                },
                scope = new { type = "string", @enum = new[] { "entire_view", "visible_region" }, @default = "entire_view",
                    description = "entire_view exports the whole requested view. visible_region exports the current UI zoom and requires the requested view to be active; no view activation is performed." },
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
            int maxDimension = ImageResolutionOptions.ReadPixels(args, "max_dimension", ImageResolutionOptions.DefaultCaptureLongEdge, 256, ImageResolutionOptions.MaxCaptureLongEdge);
            var view = ResolveView(doc, args);
            string scope = JsonArgs.GetString(args, "scope") ?? "entire_view";
            if (scope is not ("entire_view" or "visible_region")) throw new ArgumentException("scope must be entire_view or visible_region.");
            if (scope == "visible_region" && doc.ActiveView?.Id.Value != view.Id.Value)
                throw new ArgumentException("visible_region requires the requested view to be the active view. No view was activated; use open_view first if that change is intended.");

            PruneOldCaptures(CaptureDirectory());
            if (LegacyCaptureDirectory() is { } legacy)
                PruneOldCaptures(legacy);
            string prefix = Path.Combine(CaptureDirectory(), "revit_view_" + Guid.NewGuid().ToString("N"));
            ExportPng(doc, view, prefix, GuessLandscape(view), maxDimension, scope);
            string filePath = FindExportedFile(prefix);
            var (width, height) = ReadPngSize(filePath);

            // The outline-based aspect guess can be wrong (crop boxes, title blocks);
            // the PNG header is authoritative. One re-export with the fit direction
            // on the long edge applies the requested clamp.
            if (width > 0 && Math.Max(width, height) > maxDimension)
            {
                File.Delete(filePath);
                ExportPng(doc, view, prefix, horizontal: width >= height, maxDimension: maxDimension, scope: scope);
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
                maxDimension,
                scope,
                fileSizeBytes,
            }, compact);
        }

        /// <summary>Captures live in one folder this add-in owns, NOT in Path.GetTempPath().
        /// Revit can hand out a fresh per-session temp folder (observed: %LOCALAPPDATA%\Temp\
        /// &lt;guid&gt;\), so a sweep of GetTempPath() only ever sees the current session's own
        /// files while every earlier session's captures accumulate unreachably in sibling
        /// folders. A fixed directory makes the sweep below correct and cheap. The folder is
        /// the workspace's temp folder (Documents\pi-revit\temp), where the user can see it and
        /// may delete it at any time. Falls back to the temp path if the folder cannot be
        /// created — a capture must never fail over where it is filed.</summary>
        private static string CaptureDirectory()
        {
            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(documents))
                    return Path.GetTempPath();
                string directory = Path.Combine(documents, "pi-revit", "temp", "captures");
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch
            {
                return Path.GetTempPath();
            }
        }

        /// <summary>Where 0.5.2 and earlier filed captures; only swept now, never written.</summary>
        private static string? LegacyCaptureDirectory()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(localData) ? null : Path.Combine(localData, "pi-revit", "captures");
        }

        /// <summary>The PNG must outlive the call — the agent opens it with the read tool
        /// after the result returns — so this run's file is never deleted here. Instead each
        /// capture sweeps the ones left by earlier sessions, which otherwise accumulate
        /// forever. Best-effort: a locked or vanished file is skipped, and a failing sweep
        /// never costs the caller their capture. The wildcard also covers the older
        /// 'revit_view_snapshot_*' naming so pre-existing captures are collected too.</summary>
        private static void PruneOldCaptures(string directory)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - CaptureRetention;
                foreach (string path in Directory.EnumerateFiles(directory, "revit_view_*.png"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                            File.Delete(path);
                    }
                    catch
                    {
                        // In use by a reader, or already gone: leave it for the next sweep.
                    }
                }
            }
            catch
            {
                // Capture folder unreadable: cleanup is never worth failing a capture over.
            }
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
                throw new ArgumentException($"View '{view.Name}' is a schedule; schedules cannot be exported as images. Capture a graphical view or sheet, or read the schedule's rows with get_schedules instead.");
            return view;
        }

        private static void ExportPng(Document doc, View view, string prefix, bool horizontal, int maxDimension, string scope)
        {
            var options = new ImageExportOptions
            {
                ExportRange = scope == "visible_region" ? ExportRange.VisibleRegionOfCurrentView : ExportRange.SetOfViews,
                FilePath = prefix,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = maxDimension,
                FitDirection = horizontal ? FitDirectionType.Horizontal : FitDirectionType.Vertical,
            };
            if (scope == "entire_view") options.SetViewsAndSheets(new List<ElementId> { view.Id });

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
