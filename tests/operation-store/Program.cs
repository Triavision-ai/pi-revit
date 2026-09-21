using System.Collections.Concurrent;
using System.Text.Json;
using RevitBridge;

int passed = 0, failed = 0;
void Test(string name, Action run)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.Message}"); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
JsonElement Parse(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}
string Id(OperationStore store) => store.Generation + ":" + Guid.NewGuid().ToString("N");
JsonElement Status(OperationStore store, string id) => J(store.Status(id));
OperationStore.Entry Reserve(OperationStore store, string id, object? args = null) =>
    store.Reserve(id, "fixture_edit", J(args ?? new { value = 1 }), TimeSpan.FromMinutes(1)).Entry;
OperationStore.Reply Finished(Task<OperationStore.Reply> task)
{
    Check(task.IsCompletedSuccessfully, "receipt task must complete successfully without polling or waiting");
    return task.GetAwaiter().GetResult();
}
void Reject<T>(Action action, string fragment) where T : Exception
{
    try { action(); }
    catch (T error)
    {
        Check(error.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase), $"missing error detail '{fragment}': {error.Message}");
        return;
    }
    throw new Exception($"expected {typeof(T).Name}; operation was unexpectedly accepted");
}

Test("concurrent duplicate reservations share one receipt and start once", () =>
{
    var store = new OperationStore();
    string id = Id(store);
    var entries = new ConcurrentBag<OperationStore.Entry>();
    int created = 0, started = 0;
    var args = J(new { value = 1 });
    Parallel.For(0, 64, _ =>
    {
        var reservation = store.Reserve(id, "fixture_edit", args, TimeSpan.FromMinutes(1));
        entries.Add(reservation.Entry);
        if (reservation.IsNew) Interlocked.Increment(ref created);
        if (store.TryStart(reservation.Entry)) Interlocked.Increment(ref started);
    });
    Check(created == 1 && started == 1, $"expected one reservation/start; received {created}/{started}");
    Check(entries.All(entry => ReferenceEquals(entry, entries.First())), "duplicates must share the same tracked operation");
    store.Complete(entries.First(), new(200, new { applied = 1 }));
    Check(Status(store, id).GetProperty("state").GetString() == "succeeded", "shared receipt did not finish");
});

Test("nested property ordering preserves an identical operation fingerprint", () =>
{
    var store = new OperationStore();
    string id = Id(store);
    var first = store.Reserve(id, "fixture_edit", Parse("{\"a\":1,\"nested\":{\"x\":2,\"y\":3},\"rows\":[{\"c\":4,\"d\":5}]}"), TimeSpan.FromMinutes(1));
    var second = store.Reserve(id, "fixture_edit", Parse("{\"rows\":[{\"d\":5,\"c\":4}],\"nested\":{\"y\":3,\"x\":2},\"a\":1}"), TimeSpan.FromMinutes(2));
    Check(first.IsNew && !second.IsNew && ReferenceEquals(first.Entry, second.Entry), "property order must not create a distinct edit");
});

Test("different values, array order and tool identity reject receipt reuse", () =>
{
    var store = new OperationStore();
    string id = Id(store);
    store.Reserve(id, "fixture_edit", J(new { values = new[] { 1, 2 }, label = "A" }), TimeSpan.FromMinutes(1));
    foreach (var args in new[] { J(new { values = new[] { 1, 2 }, label = "B" }), J(new { values = new[] { 2, 1 }, label = "A" }) })
        Reject<ArgumentException>(() => store.Reserve(id, "fixture_edit", args, TimeSpan.FromMinutes(1)), "different tool arguments");
    Reject<ArgumentException>(() => store.Reserve(id, "other_edit", J(new { values = new[] { 1, 2 }, label = "A" }), TimeSpan.FromMinutes(1)), "different tool arguments");
    Check(Status(store, id).GetProperty("state").GetString() == "queued", "rejected reuse must not start the original operation");
});

Test("UUID spelling variants canonicalize to one reservation and status", () =>
{
    var store = new OperationStore();
    var uuid = Guid.NewGuid();
    string canonical = store.Generation + ":" + uuid.ToString("N");
    var first = Reserve(store, store.Generation + ":" + uuid.ToString("B").ToUpperInvariant());
    foreach (string format in new[] { "D", "N", "B", "P" })
    {
        string id = store.Generation + ":" + uuid.ToString(format).ToUpperInvariant();
        var again = store.Reserve(id, "fixture_edit", J(new { value = 1 }), TimeSpan.FromMinutes(1));
        Check(!again.IsNew && ReferenceEquals(first, again.Entry), "UUID variant created another operation");
        Check(Status(store, id).GetProperty("operation_id").GetString() == canonical, "status lookup failed to canonicalize UUID");
    }
});

Test("queued running and successful states expose honest timing and cached result", () =>
{
    var store = new OperationStore();
    string id = Id(store);
    var entry = Reserve(store, id);
    var before = Status(store, id);
    Check(before.GetProperty("state").GetString() == "queued" && before.GetProperty("started_at").ValueKind == JsonValueKind.Null && before.GetProperty("finished_at").ValueKind == JsonValueKind.Null, "queued receipt claims execution timestamps");
    Check(!before.GetProperty("result_available").GetBoolean(), "queued operation cannot have a result");
    var waiter = store.Wait(entry);
    Check(!waiter.IsCompleted && store.TryStart(entry), "queued operation should start with a pending waiter");
    var running = Status(store, id);
    Check(running.GetProperty("state").GetString() == "running" && running.GetProperty("started_at").ValueKind == JsonValueKind.String && running.GetProperty("finished_at").ValueKind == JsonValueKind.Null, "running state timestamps incorrect");
    store.Complete(entry, new(201, new { updated = 2 }));
    Check(Finished(waiter).Status == 201, "original waiter lost response status");
    var after = Status(store, id);
    Check(after.GetProperty("state").GetString() == "succeeded" && after.GetProperty("finished_at").ValueKind == JsonValueKind.String && after.GetProperty("result_available").GetBoolean(), "success state missing result or completion timestamp");
    Check(after.GetProperty("result_http_status").GetInt32() == 201 && after.GetProperty("result").GetProperty("updated").GetInt32() == 2, "status result differs from completed result");
    Check(J(Finished(store.Wait(entry)).Response).GetProperty("updated").GetInt32() == 2 && !store.TryStart(entry), "completed operation must return cached result without restarting");
});

Test("failed operation completes waiters and cannot be replayed", () =>
{
    var store = new OperationStore();
    string id = Id(store);
    var entry = Reserve(store, id);
    var waiter = store.Wait(entry);
    Check(store.TryStart(entry), "fixture must start");
    store.Complete(entry, new(422, new { error = true, message = "Fixture failure" }));
    Check(Finished(waiter).Status == 422 && Status(store, id).GetProperty("state").GetString() == "failed", "failure receipt incorrect");
    Check(!store.TryStart(Reserve(store, id)), "failed operation must not run again");
});

foreach (string firstObserver in new[] { "start", "wait", "status" })
Test($"expired-before-start refuses action when first observed by {firstObserver}", () =>
{
    var store = new OperationStore();
    string id = Id(store);
    var entry = store.Reserve(id, "fixture_edit", J(new { value = 1 }), TimeSpan.FromSeconds(-1)).Entry;
    if (firstObserver == "start") Check(!store.TryStart(entry), "expired work must never start");
    if (firstObserver == "wait") Check(Finished(store.Wait(entry)).Status == 408, "wait must expose queued expiry");
    if (firstObserver == "status") Check(Status(store, id).GetProperty("state").GetString() == "expired_before_start", "status must expire queued work");
    Check(!store.TryStart(entry), "expired receipt cannot be restarted");
    var status = Status(store, id);
    Check(status.GetProperty("state").GetString() == "expired_before_start" && status.GetProperty("started_at").ValueKind == JsonValueKind.Null, "expired receipt must disclose that it did not start");
    Check(Finished(store.Wait(entry)).Status == 408, "expired receipt must complete existing/future waits");
    Check(!store.Reserve(id, "fixture_edit", J(new { value = 1 }), TimeSpan.FromMinutes(1)).IsNew, "fresh timeout must not revive expired operation");
});

Test("result-count eviction retains an irreversible no-replay receipt", () =>
{
    var store = new OperationStore(maxResults: 1);
    string firstId = Id(store), secondId = Id(store);
    var first = Reserve(store, firstId);
    store.TryStart(first);
    var originalWaiter = store.Wait(first);
    store.Complete(first, new(200, new { value = "first" }));
    var second = Reserve(store, secondId);
    store.TryStart(second);
    store.Complete(second, new(200, new { value = "second" }));
    var status = Status(store, firstId);
    Check(status.GetProperty("state").GetString() == "succeeded" && !status.GetProperty("result_available").GetBoolean(), "eviction must retain outcome without advertising full result");
    Check(J(Finished(originalWaiter).Response).GetProperty("value").GetString() == "first", "eviction must not invalidate an already completed waiter");
    var expired = Finished(store.Wait(first));
    Check(expired.Status == 409 && J(expired.Response).GetProperty("message").GetString()!.Contains("not repeated"), "evicted result must report no replay explicitly");
    Check(!store.TryStart(Reserve(store, firstId)) && !store.Reserve(firstId, "fixture_edit", J(new { value = 1 }), TimeSpan.FromMinutes(1)).IsNew, "evicted receipt must prevent repeat execution");
    Check(Status(store, secondId).GetProperty("result_available").GetBoolean(), "latest bounded result should remain available");
});

Test("oversize result completes current waiter but keeps only a no-replay receipt", () =>
{
    var store = new OperationStore(maxBytes: 8);
    string id = Id(store);
    var entry = Reserve(store, id);
    store.TryStart(entry);
    var waiter = store.Wait(entry);
    store.Complete(entry, new(200, new { text = new string('x', 100) }));
    Check(Finished(waiter).Status == 200, "oversize completed response must still resolve current waiter");
    Check(!Status(store, id).GetProperty("result_available").GetBoolean() && Finished(store.Wait(entry)).Status == 409, "oversize response must leave the cache");
    Check(!store.TryStart(Reserve(store, id)), "byte eviction must not allow replay");
});

Test("total receipt capacity counts tombstones and keeps existing receipts queryable", () =>
{
    var store = new OperationStore(maxRecords: 2, maxResults: 0);
    string firstId = Id(store), secondId = Id(store);
    var first = Reserve(store, firstId);
    store.TryStart(first);
    store.Complete(first, new(200, new { ok = true }));
    Reserve(store, secondId);
    Reject<InvalidOperationException>(() => Reserve(store, Id(store)), "capacity reached");
    Check(ReferenceEquals(first, Reserve(store, firstId)), "capacity must not block lookup of an existing receipt");
    Check(Status(store, firstId).GetProperty("state").GetString() == "succeeded" && Status(store, secondId).GetProperty("state").GetString() == "queued", "capacity failure lost existing receipts");
});

Test("another bridge generation and malformed IDs cannot reserve operations", () =>
{
    var store = new OperationStore();
    var oldStore = new OperationStore();
    foreach (string id in new[] { Id(oldStore), "missing-generation", store.Generation + ":not-a-guid", Id(store) + ":extra" })
        Reject<ArgumentException>(() => Reserve(store, id), "this bridge session");
});

Test("unknown status never claims an operation did not execute", () =>
{
    var store = new OperationStore();
    foreach (string id in new[] { Id(store), Id(new OperationStore()) })
    {
        var status = Status(store, id);
        Check(status.GetProperty("state").GetString() == "unknown", "missing receipt must remain unknown");
        Check(status.GetProperty("message").GetString()!.Contains("does not prove that the operation never executed"), "unknown result must preserve outcome uncertainty");
        Check(!status.TryGetProperty("result", out _), "unknown receipt cannot contain a fabricated result");
    }
});

Test("cached reply detaches mutable payload and ignores duplicate completion", () =>
{
    var store = new OperationStore();
    string id = Id(store);
    var entry = Reserve(store, id);
    store.TryStart(entry);
    var payload = new Dictionary<string, int> { ["value"] = 7 };
    store.Complete(entry, new(200, payload));
    payload["value"] = 99;
    store.Complete(entry, new(500, new { value = -1 }));
    var saved = Finished(store.Wait(entry));
    Check(saved.Status == 200 && J(saved.Response).GetProperty("value").GetInt32() == 7, "cached receipt must remain the original detached value");
    Check(Status(store, id).GetProperty("state").GetString() == "succeeded", "duplicate completion changed recorded outcome");
});

Test("serialization failure completes waiters with uncertain-effects disclosure", () =>
{
    var store = new OperationStore();
    string id = Id(store);
    var entry = Reserve(store, id);
    store.TryStart(entry);
    var waiter = store.Wait(entry);
    store.Complete(entry, new(200, new UnserializableResponse()));
    var reply = Finished(waiter);
    Check(reply.Status == 500 && J(reply.Response).GetProperty("message").GetString()!.Contains("effects may already have occurred"), "serialization failure must not claim rollback or absence of effects");
    Check(Status(store, id).GetProperty("state").GetString() == "result_unavailable", "serialization failure state must be distinct from tool failure");
    Check(Finished(store.Wait(entry)).Status == 500 && !store.TryStart(Reserve(store, id)), "unserializable completed operation must remain nonreplayable");
});

Console.WriteLine($"RESULT {passed} passed; {failed} failed. Production OperationStore; no bridge, Revit, model, network or sleep operations.");
return failed == 0 ? 0 : 1;

sealed class UnserializableResponse
{
    public int Value => throw new InvalidOperationException("Fixture getter cannot serialize");
}
