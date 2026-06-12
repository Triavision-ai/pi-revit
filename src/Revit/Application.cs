using Autodesk.Revit.UI;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitBridge
{
    /// <summary>
    /// Headless add-in entry point. Starts the local bridge on Revit startup and
    /// stops it on shutdown. No ribbon, no panels, no UI.
    /// </summary>
    public sealed class Application : IExternalApplication
    {
        private BridgeServer? _server;
        private ExternalEvent? _externalEvent;

        // ExternalEvents are only pumped while Revit is idle with a document
        // context; with zero documents open a queued tool call would hang
        // instead of failing. This cached flag lets the bridge answer 409
        // immediately. Maintained from document events.
        private static RevitApplication? _application;
        private static volatile bool _hasOpenDocument;

        internal static bool HasOpenDocument => _hasOpenDocument;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                var controlled = application.ControlledApplication;
                controlled.DocumentOpened += (sender, _) => UpdateDocumentState(sender);
                controlled.DocumentCreated += (sender, _) => UpdateDocumentState(sender);
                controlled.DocumentClosed += (sender, _) => UpdateDocumentState(sender);

                var queue = new CommandQueue();
                // Must be created here: OnStartup is a valid Revit API context.
                _externalEvent = ExternalEvent.Create(queue);
                queue.ExternalEvent = _externalEvent;

                _server = new BridgeServer(
                    queue,
                    ToolRegistry.CreateDefault(),
                    application.ControlledApplication.VersionNumber,
                    () => HasOpenDocument);
                _server.Start();
                TryDeleteStartupError();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // A broken bridge must never block Revit startup; Pi-side tooling
                // reports the missing bridge info file instead. Leave a breadcrumb
                // so "add-in loaded but bridge startup threw" is diagnosable.
                _server?.Stop();
                _server = null;
                TryWriteStartupError(ex);
                return Result.Succeeded;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _server?.Stop();
            _externalEvent?.Dispose();
            return Result.Succeeded;
        }

        private static void UpdateDocumentState(object? sender)
        {
            if (sender is RevitApplication app)
                _application = app;
            _hasOpenDocument = (_application?.Documents?.Size ?? 0) > 0;
        }

        private static string StartupErrorPath() =>
            Path.Combine(BridgeServer.BridgeInfoDirectory(), "startup-error.txt");

        private static void TryWriteStartupError(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(BridgeServer.BridgeInfoDirectory());
                File.WriteAllText(StartupErrorPath(), $"{DateTime.Now:o} Revit bridge failed to start:{Environment.NewLine}{ex}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never break Revit startup.
            }
        }

        private static void TryDeleteStartupError()
        {
            try { File.Delete(StartupErrorPath()); } catch { }
        }
    }
}
