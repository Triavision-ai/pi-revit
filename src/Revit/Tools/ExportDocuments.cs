using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Minimal document export: sheets/views to PDF, DWG, or PNG files, or the
    /// model to IFC. Write = true for the filesystem side effects; only the IFC
    /// branch wraps a Revit transaction (the IFC exporter stores IFC GUID
    /// parameters on exported elements), owned here. Produced files are
    /// discovered by diffing the output directory because Revit appends its own
    /// view/sheet suffixes to multi-file export names.
    /// </summary>
    internal sealed class ExportDocuments : ITool
    {
        private const int MaxIds = 100;

        public string Name => "export_documents";
        public IReadOnlyList<string> DocumentKinds => DocumentKind.Both;
        public IReadOnlyList<string> Keywords => new[] { "pdf", "dwg", "ifc", "png", "print", "publish", "plot", "export sheets" };
        public IReadOnlyList<ToolLimit> Limits => new[]
        {
            new ToolLimit("Formats other than PDF, DWG, PNG and IFC", "api", "Document.Export overloads (e.g. DXFExportOptions, NavisworksExportOptions); verify the installed exporter"),
            new ToolLimit("Printing to a physical printer", "api", "PrintManager"),
        };
        public string? Verification => "inspect_output";
        public string Label => "Export Documents";
        public string Description => "Export documents from the open Revit model. format 'pdf'/'dwg'/'png' export the given sheet/view ids to files: pdf combines everything into one file by default (combine=false writes one PDF per sheet/view, named by Revit's naming rule); png defaults to 2048 px wide (png_pixel_width: 64-8192); ifc exports the whole model, or just what one given view shows. Files sort themselves per model: with output_dir omitted they land in Documents\\pi-revit\\Models\\<model title>--<identity hash>\\exports, derived from the document being exported (pass output_dir only for a different explicit target); file_name_prefix sets the base file name (default: the document title; Revit appends view/sheet suffixes for multi-file exports). Returns the produced file paths with sizes. Find sheet/view ids with get_elements (category 'Sheets' or 'Views') first.";
        public bool Write => true;
        public IReadOnlyList<string> Effects => new[] { "model", "files" };
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
                    description = "Output directory, created if missing. Default: Documents\\pi-revit\\Models\\<model title>--<model creation GUID>\\exports — files sort under the exported model automatically; omit unless the user names a different target.",
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
                png_pixel_width = new
                {
                    type = "integer",
                    minimum = 64,
                    maximum = ImageResolutionOptions.MaxExportWidth,
                    @default = ImageResolutionOptions.DefaultExportWidth,
                    description = "PNG only: requested horizontal pixel width (64-8192), default 2048. Height follows the view aspect ratio; portrait images can be taller than this width. Larger exports use more time and memory.",
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
                throw new ArgumentException($"Unknown format: {JsonArgs.GetString(args, "format")}. Supported: pdf, dwg, png, ifc.");

            int pngPixelWidth = ResolvePngPixelWidth(args, format);
            var views = ResolveViews(doc, args, format);
            if (format is "pdf" or "dwg")
                RefuseTemporaryHideIsolate(doc, views, format);
            string outputDir = ResolveOutputDir(args, doc);
            string? prefixInput = JsonArgs.GetString(args, "file_name_prefix");
            string baseName = SanitizeFileName(string.IsNullOrWhiteSpace(prefixInput) ? doc.Title : prefixInput.Trim());
            bool combine = JsonArgs.GetBool(args, "combine", true);

            // Produced files are found by diffing the directory, because Revit appends its
            // own view/sheet suffixes to multi-file export names. Each file is compared
            // against ITS OWN pre-export write time, so an overwrite of a pre-existing name
            // still counts as produced while an untouched file that merely happens to be
            // recent (a shared output_dir, another tool writing alongside) does not.
            var before = SnapshotWriteTimes(outputDir);

            IReadOnlyList<string> commitWarnings = Array.Empty<string>();
            try
            {
                switch (format)
                {
                    case "pdf": ExportPdf(doc, views, outputDir, baseName, combine); break;
                    case "dwg": ExportDwg(doc, views, outputDir, baseName); break;
                    case "png": ExportPng(doc, views, outputDir, baseName, pngPixelWidth); break;
                    default: commitWarnings = ExportIfc(doc, views, outputDir, baseName); break;
                }
            }
            catch (Exception ex)
            {
                // A failed export may already have written some files. A Revit
                // transaction rollback cannot undo those filesystem changes.
                string observed;
                try
                {
                    var changed = FindChangedFiles(outputDir, before).ToList();
                    observed = changed.Count == 0
                        ? "No changed files were observed."
                        : $"Observed {changed.Count} new or changed file(s): {string.Join(", ", changed.Take(20))}{(changed.Count > 20 ? " (additional files omitted)" : string.Empty)}.";
                }
                catch (Exception scanError)
                {
                    observed = $"Could not inspect remaining files: {scanError.Message}.";
                }
                throw new InvalidOperationException($"{ex.Message} The export failed; files in '{outputDir}' may be incomplete and were not removed. {observed}", ex);
            }

            // Revit writes extra files next to some exports (a DWG comes with .pcp plot setups and,
            // for sheets, title-block images). fileCount/files describe only the requested format;
            // everything else that changed is listed separately so no new file is hidden.
            var newFiles = FindChangedFiles(outputDir, before).ToList();
            string extension = "." + format;
            var files = newFiles
                .Where(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                .Select(path => DescribeFile(path, before))
                .ToList();
            var companionFiles = newFiles
                .Where(path => !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                .Select(path => DescribeFile(path, before))
                .ToList();
            // A file that already existed was replaced without asking: say so.
            var overwritten = files.Concat(companionFiles).Where(file => (bool)file["overwritten"]!).Select(file => (string)file["path"]!).ToList();
            if (files.Count == 0)
                throw new InvalidOperationException($"The {format} export finished but produced no {extension} files in {outputDir}."
                    + (companionFiles.Count == 0 ? string.Empty : $" It did write {companionFiles.Count} other file(s): {string.Join(", ", companionFiles.Take(5).Select(file => Path.GetFileName((string)file["path"]!)))}{(companionFiles.Count > 5 ? " (+more)" : string.Empty)}."));

            string sample = string.Join(", ", files.Take(3).Select(file => Path.GetFileName((string)file["path"]!)));
            string compact = $"Exported {files.Count} {format.ToUpperInvariant()} file(s) to {outputDir}: {sample}{(files.Count > 3 ? $" (+{files.Count - 3} more)" : string.Empty)}.";
            if (companionFiles.Count > 0)
                compact += $" Revit also wrote {companionFiles.Count} companion file(s) (see companionFiles).";
            if (overwritten.Count > 0)
                compact += $" {overwritten.Count} existing file(s) were replaced (overwritten: true).";
            if (commitWarnings.Count > 0)
                compact += $" {commitWarnings.Count} Revit warning(s) auto-dismissed (see commitWarnings).";
            return new ToolOutput(new
            {
                format,
                pngPixelWidth = format == "png" ? (int?)pngPixelWidth : null,
                outputDir,
                fileCount = files.Count,
                files,
                companionFiles,
                overwrittenCount = overwritten.Count,
                commitWarnings,
            }, compact);
        }

        private static Dictionary<string, object?> DescribeFile(string path, IReadOnlyDictionary<string, DateTime> before) => new()
        {
            ["path"] = path,
            ["fileSizeBytes"] = new FileInfo(path).Length,
            ["overwritten"] = before.ContainsKey(path),
        };

        private static IEnumerable<string> FindChangedFiles(string directory, IReadOnlyDictionary<string, DateTime> before)
            => Directory.GetFiles(directory)
                .Where(path => !before.TryGetValue(path, out DateTime writtenBefore) || SafeWriteTime(path) != writtenBefore)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        /// <summary>path -> last write time for the files already in the output directory.
        /// A file whose time cannot be read is left out, so the export reports it if it
        /// shows up afterwards rather than silently swallowing a produced file.</summary>
        private static Dictionary<string, DateTime> SnapshotWriteTimes(string directory)
        {
            var snapshot = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(directory))
            {
                DateTime writtenAt = SafeWriteTime(path);
                if (writtenAt != DateTime.MinValue)
                    snapshot[path] = writtenAt;
            }
            return snapshot;
        }

        /// <summary>DateTime.MinValue when the timestamp is unreadable (file locked or gone),
        /// which compares as "changed" — an unknown file is reported, never hidden.</summary>
        private static DateTime SafeWriteTime(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
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

        private static int ResolvePngPixelWidth(JsonElement args, string format)
        {
            if (!args.TryGetProperty("png_pixel_width", out _)) return ImageResolutionOptions.DefaultExportWidth;
            if (format != "png") throw new ArgumentException("png_pixel_width is only supported for format 'png'. No export was started.");
            return ImageResolutionOptions.ReadPixels(args, "png_pixel_width", ImageResolutionOptions.DefaultExportWidth, 64, ImageResolutionOptions.MaxExportWidth);
        }

        private static void ExportPng(Document doc, List<View> views, string outputDir, string baseName, int pixelWidth)
        {
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(outputDir, baseName),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixelWidth,
                FitDirection = FitDirectionType.Horizontal,
            };
            options.SetViewsAndSheets(views.Select(view => view.Id).ToList());
            Run(() => doc.ExportImage(options), "PNG");
        }

        /// <summary>The IFC exporter writes IFC GUID parameters onto exported elements,
        /// so the Revit API requires a transaction around it — owned here, matching
        /// Revit's own behavior of committing those GUIDs on export.</summary>
        private static IReadOnlyList<string> ExportIfc(Document doc, List<View> views, string outputDir, string baseName)
        {
            var options = new IFCExportOptions();
            if (views.Count == 1)
                options.FilterViewId = views[0].Id;

            using var transaction = new Transaction(doc, "export_documents: ifc");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Unable to start the IFC export transaction.");
            var failureGuard = FailureGuard.Attach(transaction);
            try
            {
                if (!Run(() => doc.Export(outputDir, baseName, options), "IFC"))
                    throw new InvalidOperationException("Revit reported a failed IFC export.");
                var status = transaction.Commit();
                var finalStatus = transaction.GetStatus();
                if (status != TransactionStatus.Committed || finalStatus != TransactionStatus.Committed)
                    throw new InvalidOperationException($"The IFC export commit returned {status}; current transaction status is {finalStatus}." + failureGuard.DescribeErrors());
                return failureGuard.Warnings;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{ex.Message} {FailureGuard.RollBackAndDescribe(transaction)}", ex);
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
                // Filtering IFC by a sheet, schedule, legend or drafting view shows no model
                // elements, so Revit still "succeeds" with an IFC holding only the storeys.
                if (format == "ifc" && !IfcFilterViewTypes.Contains(view.ViewType))
                    throw new ArgumentException($"id {id} ('{view.Name}') is a {view.ViewType} view, which shows no model elements, so an IFC filtered by it would be empty. Pass a floor, ceiling, structural or area plan, elevation, section, detail or 3D view, or omit ids to export the whole model.");
                views.Add(view);
            }
            return views;
        }

        /// <summary>View types whose visibility settings can govern an IFC export.</summary>
        private static readonly HashSet<ViewType> IfcFilterViewTypes = new()
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan,
            ViewType.Elevation, ViewType.Section, ViewType.Detail, ViewType.ThreeD, ViewType.Walkthrough,
        };

        /// <summary>Revit's print and CAD export path stops on a modal "export with temporary
        /// hide/isolate" dialog when an exported view is in that mode, so the call would block until a
        /// person clicks it. Refuse before anything is written. A sheet exports the views placed on it.
        /// PNG (image export) did not show the dialog when tried live; IFC is not checked here (unverified).</summary>
        private static void RefuseTemporaryHideIsolate(Document doc, List<View> views, string format)
        {
            var isolated = new List<string>();
            var seen = new HashSet<long>();
            foreach (var target in views)
            {
                var shown = target is ViewSheet sheet
                    ? sheet.GetAllPlacedViews().Select(id => doc.GetElement(id)).OfType<View>().ToList()
                    : new List<View> { target };
                foreach (var view in shown)
                    if (seen.Add(view.Id.Value) && InTemporaryHideIsolate(view))
                        isolated.Add($"'{view.Name}' (id {view.Id.Value}{(target is ViewSheet ? $", on sheet '{target.Name}'" : string.Empty)})");
            }
            if (isolated.Count == 0)
                return;
            throw new ArgumentException($"Cannot export {format}: {string.Join(", ", isolated.Take(10))}{(isolated.Count > 10 ? $" (+{isolated.Count - 10} more)" : string.Empty)} "
                + $"{(isolated.Count == 1 ? "is" : "are")} in temporary hide/isolate mode, and Revit would stop on a dialog until someone clicks it. "
                + "Clear it with manage_selection action \"clear\" with isolate_in_view true (it acts on the active view, so open_view the view first if it is not active), then retry. Nothing was exported.");
        }

        private static bool InTemporaryHideIsolate(View view)
        {
            try
            {
                return view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return false; } // view kinds without temporary modes
            catch (Autodesk.Revit.Exceptions.ArgumentException) { return false; }
        }

        private static string ResolveOutputDir(JsonElement args, Document doc)
        {
            string? requested = JsonArgs.GetString(args, "output_dir");
            // Absent (or null) means the default model folder; a present but blank value is a
            // mistake, not a request for the default, so it is refused instead of guessed at.
            if (requested != null && string.IsNullOrWhiteSpace(requested))
                throw new ArgumentException("output_dir is empty; omit output_dir to use the default model folder.");
            // A relative path would resolve against Revit's own working directory (its install folder),
            // not anything the caller can see, so only a fully qualified path is accepted.
            if (requested != null && !Path.IsPathFullyQualified(requested.Trim()))
                throw new ArgumentException($"output_dir '{requested.Trim()}' is not an absolute path; give an absolute folder, or omit output_dir to use the model's default folder.");
            string outputDir = requested == null
                ? Path.Combine(ModelFolder(doc), "exports")
                : Path.GetFullPath(requested.Trim());
            Directory.CreateDirectory(outputDir);
            return outputDir;
        }

        /// <summary>Per-model folder under the pi-revit workspace:
        /// Documents\pi-revit\Models\&lt;model title&gt;--&lt;creation GUID&gt; (or --&lt;identity hash&gt;
        /// when the document has no creation GUID).
        /// Existing title-only folders are left untouched; identity markers are
        /// diagnostic records, not the mechanism that separates exports.</summary>
        private static string ModelFolder(Document doc)
        {
            string workspace = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "pi-revit");
            string identity = GetModelIdentity(doc);
            string folder = Path.Combine(workspace, "Models", GetModelFolderName(doc.Title, identity));
            Directory.CreateDirectory(folder);
            TryRecordModelIdentity(folder, doc, identity);
            return folder;
        }

        internal static string GetModelFolderName(string title, string identity)
        {
            string cleanTitle = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "untitled" : title);
            // Bound the default path component, leaving room for the identity and exports.
            if (cleanTitle.Length > 80)
                cleanTitle = cleanTitle[..80];
            // The document's creation GUID is shown as is, so a folder can be matched to its model.
            if (identity.StartsWith(CreationPrefix, StringComparison.Ordinal))
                return $"{cleanTitle}--{identity[CreationPrefix.Length..]}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
            return $"{cleanTitle}--{hash}";
        }

        private const string CreationPrefix = "creation:";

        // Revit can return different managed wrappers for one open native document.
        // Document.Equals/GetHashCode identify that open document, unlike reference
        // equality in ConditionalWeakTable. Closed document entries are pruned below.
        // Tool execution and this dictionary are confined to the Revit API thread.
        private static readonly Dictionary<Document, string> OpenDocumentIdentities = new();

        /// <summary>The model's lasting identity: Revit's creation GUID (made when the document was first
        /// created; it stays when the file is moved or renamed, and copies of a file keep it too, which
        /// is accepted). Without one, the cloud model, Revit Server path or file path, else a per-session
        /// value for an unsaved document.</summary>
        internal static string GetModelIdentity(Document doc)
        {
            try
            {
                Guid creation = doc.CreationGUID;
                if (creation != Guid.Empty)
                    return CreationPrefix + creation.ToString("N");
            }
            catch
            {
                // No creation GUID available: use the location-based identity below.
            }
            try
            {
                if (doc.IsModelInCloud)
                {
                    var path = doc.GetCloudModelPath();
                    return $"cloud:{path.Region.ToUpperInvariant()}:{path.GetProjectGUID():D}:{path.GetModelGUID():D}";
                }
                string pathName = doc.PathName;
                if (!string.IsNullOrWhiteSpace(pathName))
                {
                    // Revit file paths are Windows paths; canonicalize separators,
                    // relative segments and case. Revit Server identities use RSN URLs.
                    if (pathName.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                        return "server:" + pathName.Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
                    return "file:" + Path.GetFullPath(pathName).Replace('/', '\\').ToUpperInvariant();
                }
            }
            catch
            {
                // An unavailable identity must not collapse unrelated documents into
                // a shared title-only directory. Use the same safe fallback as unsaved docs.
            }
            foreach (var closed in OpenDocumentIdentities.Keys.Where(key => !key.IsValidObject).ToList())
                OpenDocumentIdentities.Remove(closed);
            if (!OpenDocumentIdentities.TryGetValue(doc, out string? identity))
                OpenDocumentIdentities[doc] = identity = $"session:{Guid.NewGuid():N}";
            return identity;
        }

        private static void TryRecordModelIdentity(string folder, Document doc, string identity)
        {
            try
            {
                string line = $"{identity}\t{doc.PathName}";
                string marker = Path.Combine(folder, "model.txt");
                if (!File.Exists(marker) || !File.ReadAllLines(marker).Contains(line))
                    File.AppendAllLines(marker, new[] { line });
            }
            catch
            {
                // The identity record is best-effort; never block an export on it.
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return clean.Length > 0 ? clean : "export";
        }
    }
}
