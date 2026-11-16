using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace RevitBridge;

/// <summary>
/// Session-local operation receipts. Expiring a result never permits the same ID to execute again.
///
/// Memory stays bounded in three steps, and none of them frees an ID for reuse: full results
/// are dropped first (maxResults/maxBytes), then whole finished receipts are retired down to
/// their bare ID (maxRecords), and only when even those IDs exceed maxRetired does the store
/// refuse new tracked operations. A long Revit session can therefore run far more than
/// maxRecords tracked operations without a restart.
/// </summary>
internal sealed class OperationStore
{
    internal sealed record Reply(int Status, object Response, string? Outcome = null);
    internal sealed class Entry
    {
        internal required string Id;
        internal required string Tool;
        internal required string Fingerprint;
        internal required long Deadline;
        internal DateTimeOffset Created = DateTimeOffset.UtcNow;
        internal DateTimeOffset? Started;
        internal DateTimeOffset? Finished;
        internal string State = "queued";
        internal TaskCompletionSource<Reply>? Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Reply? SavedReply;
        internal int SavedBytes;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<Entry> _results = new();
    /// <summary>Finished receipts, oldest first: the order in which they are retired.</summary>
    private readonly Queue<Entry> _finished = new();
    /// <summary>IDs of retired receipts. An ID in here finished earlier and must never run again.</summary>
    private readonly HashSet<Guid> _retired = new();
    private readonly int _maxRecords;
    private readonly int _maxResults;
    private readonly int _maxBytes;
    private readonly int _maxRetired;
    private int _bytes;
    public string Generation { get; } = Guid.NewGuid().ToString("N");

    internal OperationStore(int maxRecords = 10000, int maxResults = 128, int maxBytes = 32 * 1024 * 1024, int maxRetired = 1_000_000)
    {
        _maxRecords = maxRecords; _maxResults = maxResults; _maxBytes = maxBytes; _maxRetired = maxRetired;
    }

    internal const string RetiredMessage = "This operation_id finished earlier in this bridge session and its receipt has been retired to bound memory. It was not repeated. Inspect current model state before deciding on a new operation.";

    public (Entry Entry, bool IsNew) Reserve(string id, string tool, JsonElement args, TimeSpan timeout)
    {
        id = ValidateId(id);
        string fingerprint = Fingerprint(tool, args);
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var existing))
            {
                if (existing.Fingerprint != fingerprint) throw new ArgumentException("operation_id was already used with different tool arguments. No new operation was started.");
                return (existing, false);
            }
            if (_retired.Contains(KeyOf(id))) throw new ArgumentException(RetiredMessage);
            if (_entries.Count >= _maxRecords) RetireFinished();
            var entry = new Entry { Id = id, Tool = tool, Fingerprint = fingerprint,
                Deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency) };
            _entries.Add(id, entry);
            return (entry, true);
        }
    }

    /// <summary>
    /// Makes room for one more receipt by retiring the oldest finished ones down to their ID.
    /// Receipts that are still queued or running are never retired. Throws when no room can
    /// be made; the caller answers 503 and starts nothing.
    /// </summary>
    private void RetireFinished()
    {
        while (_entries.Count >= _maxRecords)
        {
            if (_finished.Count == 0)
                throw new InvalidOperationException($"Operation receipt capacity reached: {_entries.Count} tracked operations are still queued or running. Wait for them to finish before starting new tracked operations; existing receipts remain queryable.");
            if (_retired.Count >= _maxRetired)
                throw new InvalidOperationException("Operation receipt capacity reached for this Revit session. Restart Revit before starting new tracked operations; existing receipts remain queryable.");
            var old = _finished.Dequeue();
            _entries.Remove(old.Id);
            _retired.Add(KeyOf(old.Id));
            // Its full result is normally long gone; release it if it is still held.
            _bytes -= old.SavedBytes; old.SavedBytes = 0; old.SavedReply = null;
        }
    }

    /// <summary>The UUID half of a validated "generation:uuid" operation ID.</summary>
    private static Guid KeyOf(string validatedId) => Guid.ParseExact(validatedId[(validatedId.IndexOf(':') + 1)..], "N");

    public Task<Reply> Wait(Entry entry)
    {
        lock (_gate)
        {
            ExpireQueued(entry);
            return entry.Completion?.Task ?? Task.FromResult(entry.SavedReply ?? new Reply(409, new
            {
                error = true, operation_id = entry.Id, state = entry.State,
                message = "The operation already finished, but its full result has expired. It was not repeated. Inspect current model state before deciding on a new operation.",
            }));
        }
    }

    public bool TryStart(Entry entry)
    {
        lock (_gate)
        {
            ExpireQueued(entry);
            if (entry.State != "queued") return false;
            entry.State = "running"; entry.Started = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void Complete(Entry entry, Reply reply)
    {
        lock (_gate)
        {
            ExpireQueued(entry);
            if (entry.Completion == null) return;
            Finish(entry, reply.Outcome ?? (reply.Status < 400 ? "succeeded" : "failed"), reply);
        }
    }

    public object Status(string id)
    {
        var parts = id.Split(':');
        if (parts.Length == 2 && Guid.TryParse(parts[1], out var uuid)) id = parts[0] + ":" + uuid.ToString("N");
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                if (parts.Length == 2 && parts[0] == Generation && Guid.TryParse(parts[1], out var key) && _retired.Contains(key)) return new
                {
                    operation_id = id, bridge_id = Generation, state = "retired", result_available = false,
                    message = RetiredMessage,
                };
                return new
                {
                    operation_id = id, bridge_id = Generation, state = "unknown",
                    message = "No receipt exists in this bridge session. The outcome is unknown; this does not prove that the operation never executed. Check the original bridge and model before retrying.",
                };
            }
            ExpireQueued(entry);
            return new
            {
                operation_id = id, bridge_id = Generation, tool = entry.Tool, state = entry.State,
                created_at = entry.Created, started_at = entry.Started, finished_at = entry.Finished,
                result_available = entry.SavedReply != null, result_http_status = entry.SavedReply?.Status,
                result = entry.SavedReply?.Response,
            };
        }
    }

    private void ExpireQueued(Entry entry)
    {
        if (entry.State == "queued" && Stopwatch.GetTimestamp() > entry.Deadline)
            Finish(entry, "expired_before_start", new Reply(408, new { error = true, operation_id = entry.Id,
                message = "Operation expired before it started; no tool action was performed." }));
    }

    private void Finish(Entry entry, string state, Reply reply)
    {
        var completion = entry.Completion!;
        // Keep a detached JSON value, never API objects or callbacks, in the receipt cache.
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(reply.Response); }
        catch (Exception error)
        {
            state = "result_unavailable";
            reply = new Reply(500, new { error = true, message = "The tool finished but its receipt could not be serialized. Model or other effects may already have occurred. Inspect before retrying. " + error.Message });
            bytes = JsonSerializer.SerializeToUtf8Bytes(reply.Response);
        }
        entry.State = state; entry.Finished = DateTimeOffset.UtcNow;
        entry.SavedReply = new Reply(reply.Status, JsonSerializer.Deserialize<JsonElement>(bytes));
        entry.SavedBytes = bytes.Length; _bytes += bytes.Length;
        _results.Enqueue(entry);
        _finished.Enqueue(entry);
        entry.Completion = null;
        while (_results.Count > _maxResults || _bytes > _maxBytes)
        {
            var old = _results.Dequeue();
            _bytes -= old.SavedBytes; old.SavedBytes = 0; old.SavedReply = null;
        }
        completion.TrySetResult(reply);
    }

    private string ValidateId(string id)
    {
        string[] parts = id.Split(':');
        if (parts.Length != 2 || parts[0] != Generation || !Guid.TryParse(parts[1], out _))
            throw new ArgumentException("operation_id must contain this bridge session's bridgeId and a UUID. An ID from another bridge/restart cannot be replayed; its outcome must be checked separately.");
        return parts[0] + ":" + Guid.Parse(parts[1]).ToString("N");
    }

    private static string Fingerprint(string tool, JsonElement args)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            void Write(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); Write(property.Value); }
                    writer.WriteEndObject();
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item); writer.WriteEndArray();
                }
                else element.WriteTo(writer);
            }
            writer.WriteStartArray(); writer.WriteStringValue(tool); Write(args); writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
