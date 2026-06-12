using System.Collections.Concurrent;
using System.Diagnostics;
using Autodesk.Revit.UI;

namespace RevitBridge
{
    /// <summary>
    /// Thrown by queued work when no Revit document is open.
    /// The bridge maps this to HTTP 409 with hasActiveDocument = false.
    /// </summary>
    internal sealed class NoActiveDocumentException : InvalidOperationException
    {
        public NoActiveDocumentException()
            : base("No active Revit document is open.")
        {
        }
    }

    /// <summary>
    /// Schedules work onto the Revit API thread via an ExternalEvent and returns
    /// a Task that completes with the result. Adapted from the proven
    /// ExternalEvent + TaskCompletionSource pattern.
    /// </summary>
    internal sealed class CommandQueue : IExternalEventHandler
    {
        private sealed class WorkItem
        {
            public required Func<UIApplication, object?> Action { get; init; }
            public required TaskCompletionSource<object?> Completion { get; init; }

            /// <summary>Stopwatch timestamp after which the item must not start; 0 = no deadline.</summary>
            public long DeadlineTimestamp { get; init; }
        }

        private readonly ConcurrentQueue<WorkItem> _queue = new();

        /// <summary>Set once from Application.OnStartup (a valid Revit API context).</summary>
        public ExternalEvent? ExternalEvent { get; set; }

        public Task<T> RunAsync<T>(Func<UIApplication, T> action, TimeSpan? timeout = null)
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(new WorkItem
            {
                Action = app => action(app),
                Completion = completion,
                DeadlineTimestamp = timeout is { } budget
                    ? Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency)
                    : 0,
            });

            var externalEvent = ExternalEvent;
            if (externalEvent is null)
            {
                completion.TrySetException(new InvalidOperationException("Revit bridge command queue is not initialized."));
            }
            else
            {
                externalEvent.Raise();
            }

            return Await<T>(completion.Task);
        }

        private static async Task<T> Await<T>(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var item))
            {
                // The HTTP caller abandons the request at the same deadline, so running
                // the item late would mutate the model behind the agent's back.
                if (item.DeadlineTimestamp != 0 && Stopwatch.GetTimestamp() > item.DeadlineTimestamp)
                {
                    item.Completion.TrySetException(new TimeoutException(
                        "Tool call expired before execution; no work was performed. Revit stayed busy past the timeout_ms deadline; retry, optionally with a larger timeout_ms."));
                    continue;
                }

                try
                {
                    item.Completion.TrySetResult(item.Action(app));
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }

        public string GetName() => "Revit Bridge Command Queue";
    }
}
