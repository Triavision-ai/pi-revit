using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Minimal v1 document export: sheets/views to PDF, DWG, or PNG files, or the
    /// model to IFC. Write = true for the filesystem side effects; only the IFC
    /// branch wraps a Revit transaction (the IFC exporter stores IFC GUID
    /// parameters on exported elements), owned here per D2. Produced files are
    /// discovered by diffing the output directory because Revit appends its own
    /// view/sheet suffixes to multi-file export names.
    /// </summary>
    internal sealed class ExportDocuments : ITool
    {
        private const int MaxIds = 100;
        private const int PngPixelWidth = 2048;

        public string Name => "export_documents";
        public string Label => "Export Documents";
        public string Description => "Export documents from the open Revit model (v1 schema). format 'pdf'/'dwg'/'png' export the given sheet/view ids to files: pdf combines everything into one file by default (combine=false writes one PDF per sheet/view, named by Revit's naming rule); png renders 2048 px wide; ifc exports the whole model, or just what one given view shows. Files land in output_dir (created if missing; default: a fresh folder under the system temp directory); file_name_prefix sets the base file name (default: the document title; Revit appends view/sheet suffixes for multi-file exports). Returns the produced file paths with sizes. Find sheet/view ids with get_elements (category 'Sheets' or 'Views') first.";
        public bool Write => true;
        public string Tier => "advanced";

        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                format = new
                {
                    type = "string",
                    @enum = new[] { "pdf", "dwg", "png", "ifc" },
                    description = "Export format. pdf/dwg/png need sheet/view ids; ifc exports the model (optionally filtered to one view).",
                },
                ids = new
                {
                    type = "array",
                    items = new { type = "integer" },
                    description = "Sheet/view element ids to export (1-100). Required for pdf/dwg/png; for ifc at most one view id (default: the whole model).",
                },
                output_dir = new
                {
                    type = "string",
                    description = "Output directory, created if missing. Default: a fresh folder under the system temp directory.",
                },
                file_name_prefix = new
                {
                    type = "string",
                    description = "Base file name without extension. Default: the document title.",
                },
                combine = new
                {
                    type = "boolean",
                    description = "pdf only: combine all sheets/views into one PDF. Default true.",
                },
            },
            required = new[] { "format" },
        };

        public string? PromptSnippet => "Export sheets/views to PDF, DWG, or PNG files, or the model to IFC.";
        public IReadOnlyList<string>? PromptGuidelines => new[]
        {
            "export_documents needs explicit sheet/view ids for pdf/dwg/png — find them with get_elements (category 'Sheets' or 'Views') first.",
        };

        public object? Execute(JsonElement args, ToolContext context)
        {
            var doc = context.Document ?? throw new NoActiveDocumentException();

            string format = (JsonArgs.GetString(args, "format") ?? string.Empty).Trim().ToLowerInvariant();
            if (format is not ("pdf" or "dwg" or "png" or "ifc"))
                throw new ArgumentException($"Unknown format: {JsonArgs.GetString(args, "format")}. Supported (v1): pdf, dwg, png, ifc.");

            var views = ResolveViews(doc, args, format);
            string outputDir = ResolveOutputDir(args);
            string? prefixInput = JsonArgs.GetString(args, "file_name_prefix");
            string baseName = SanitizeFileName(string.IsNullOrWhiteSpace(prefixInput) ? doc.Title : prefixInput.Trim());
            bool combine = JsonArgs.GetBool(args, "combine", true);

            // Produced files are found by diffing the directory; the timestamp check
            // (with clock-skew slack) also catches overwrites of pre-existing names.
            DateTime startedUtc = DateTime.UtcNow.AddSeconds(-2);
            var before = new HashSet<string>(Directory.GetFiles(outputDir), StringComparer.OrdinalIgnoreCase);

            switch (format)
            {
                case "pdf": ExportPdf(doc, views, outputDir, baseName, combine); break;
                case "dwg": ExportDwg(doc, views, outputDir, baseName); break;
                case "png": ExportPng(doc, views, outputDir, baseName); break;
                default: ExportIfc(doc, views, outputDir, baseName); break;
            }

            var files = Directory.GetFiles(outputDir)
                .Where(path => !before.Contains(path) || File.GetLastWriteTimeUtc(path) >= startedUtc)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["fileSizeBytes"] = new FileInfo(path).Length,
                })
                .ToList();
            if (files.Count == 0)
                throw new InvalidOperationException($"The {format} export finished but produced no files in {outputDir}.");

            string sample = string.Join(", ", files.Take(3).Select(file => Path.GetFileName((string)file["path"]!)));
            string compact = $"Exported {files.Count} {format.ToUpperInvariant()} file(s) to {outputDir}: {sample}{(files.Count > 3 ? $" (+{files.Count - 3} more)" : string.Empty)}.";
            return new ToolOutput(new
            {
                format,
                outputDir,
                fileCount = files.Count,
                files,
            }, compact);
        }

        // ------------------------------------------------------------- format runs

        private static void ExportPdf(Document doc, List<View> views, string outputDir, string baseName, bool combine)
        {
            var options = new PDFExportOptions
            {
                Combine = combine,
                FileName = baseName, // names the combined file; per-view files follow Revit's naming rule
            };
            if (!Run(() => doc.Export(outputDir, views.Select(view => view.Id).ToList(), options), "PDF"))
                throw new InvalidOperationException("Revit reported a failed PDF export.");
        }

        private static void ExportDwg(Document doc, List<View> views, string outputDir, string baseName)
        {
            bool available = true;
            try
            {
                available = OptionalFunctionalityUtils.IsDWGExportAvailable();
            }
            catch
            {
                // Probe unavailable; let the export call itself report any failure.
            }
            if (!available)
                throw new InvalidOperationException("DWG export is not available in this Revit installation.");

            if (!Run(() => doc.Export(outputDir, baseName, views.Select(view => view.Id).ToList(), new DWGExportOptions()), "DWG"))
                throw new InvalidOperationException("Revit reported a failed DWG export.");
        }

        private static void ExportPng(Document doc, List<View> views, string outputDir, string baseName)
        {
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(outputDir, baseName),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = PngPixelWidth,
                FitDirection = FitDirectionType.Horizontal,
            };
            options.SetViewsAndSheets(views.Select(view => view.Id).ToList());
            Run(() => doc.ExportImage(options), "PNG");
        }

        /// <summary>The IFC exporter writes IFC GUID parameters onto exported elements,
        /// so the Revit API requires a transaction around it — owned here (D2), matching
        /// Revit's own behavior of committing those GUIDs on export.</summary>
        private static void ExportIfc(Document doc, List<View> views, string outputDir, string baseName)
        {
            var options = new IFCExportOptions();
            if (views.Count == 1)
                options.FilterViewId = views[0].Id;

            using var transaction = new Transaction(doc, "export_documents: ifc");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Unable to start the IFC export transaction.");
            try
            {
                if (!Run(() => doc.Export(outputDir, baseName, options), "IFC"))
                    throw new InvalidOperationException("Revit reported a failed IFC export.");
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("The IFC export transaction failed to commit.");
            }
            catch
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
                throw;
            }
        }

        /// <summary>Runs one export call, translating Revit API exceptions into plain errors.</summary>
        private static T Run<T>(Func<T> export, string what)
        {
            try
            {
                return export();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new InvalidOperationException($"Revit could not run the {what} export: {ex.Message}");
            }
        }

        private static void Run(Action export, string what)
            => Run<object?>(() => { export(); return null; }, what);

        // ----------------------------------------------------------------- parsing

        private static List<View> ResolveViews(Document doc, JsonElement args, string format)
        {
            bool present = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("ids", out var idsElement)
                && idsElement.ValueKind != JsonValueKind.Null;
            var ids = present ? JsonArgs.GetLongArray(args, "ids") : new List<long>();

            if (ids.Count > MaxIds)
                throw new ArgumentException($"Too many ids ({ids.Count}); max {MaxIds} per call. Export in batches.");
            if (format != "ifc" && ids.Count == 0)
                throw new ArgumentException($"format '{format}' requires ids of sheets or views. List them with get_elements (category 'Sheets' or 'Views').");
            if (format == "ifc" && ids.Count > 1)
                throw new ArgumentException("format 'ifc' takes at most one view id (the export is filtered to what that view shows); omit ids to export the whole model.");

            var views = new List<View>(ids.Count);
            foreach (long id in ids.Distinct())
            {
                var view = doc.GetElement(new ElementId(id)) as View
                    ?? throw new ArgumentException($"id {id} is not a view or sheet.");
                if (view.IsTemplate)
                    throw new ArgumentException($"id {id} ('{view.Name}') is a view template and cannot be exported.");
                if (format == "png" && view is ViewSchedule)
                    throw new ArgumentException($"id {id} ('{view.Name}') is a schedule; schedules cannot be exported as PNG images.");
                views.Add(view);
            }
            return views;
        }

        private static string ResolveOutputDir(JsonElement args)
        {
            string? requested = JsonArgs.GetString(args, "output_dir");
            string outputDir = string.IsNullOrWhiteSpace(requested)
                ? Path.Combine(Path.GetTempPath(), "revit_export_" + Guid.NewGuid().ToString("N"))
                : Path.GetFullPath(requested.Trim());
            Directory.CreateDirectory(outputDir);
            return outputDir;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return clean.Length > 0 ? clean : "export";
        }
    }
}
