using System.Text.Json;
using Xunit;

namespace RevitBridge.Tests;

/// <summary>Operation receipts: an ID never runs twice, and a long Revit session never runs out of receipts.</summary>
public class OperationStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static JsonElement Args(string json = "{}")
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string NewId(OperationStore store) => $"{store.Generation}:{Guid.NewGuid()}";

    private static string State(OperationStore store, string id)
        => JsonSerializer.SerializeToElement(store.Status(id)).GetProperty("state").GetString()!;

    /// <summary>Runs one tracked operation to completion, the way the bridge does.</summary>
    private static string RunOne(OperationStore store)
    {
        string id = NewId(store);
        var (entry, isNew) = store.Reserve(id, "set_parameters", Args(), Timeout);
        Assert.True(isNew);
        Assert.True(store.TryStart(entry));
        store.Complete(entry, new OperationStore.Reply(200, new { ok = true }));
        return id;
    }

    [Fact]
    public void A_repeated_id_returns_the_same_receipt_and_is_not_new()
    {
        var store = new OperationStore();
        string id = NewId(store);

        var first = store.Reserve(id, "set_parameters", Args("{\"a\":1,\"b\":2}"), Timeout);
        var second = store.Reserve(id, "set_parameters", Args("{\"b\":2,\"a\":1}"), Timeout); // same arguments, other key order

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Same(first.Entry, second.Entry);
    }

    [Fact]
    public void A_repeated_id_with_different_arguments_is_refused()
    {
        var store = new OperationStore();
        string id = NewId(store);
        store.Reserve(id, "set_parameters", Args("{\"a\":1}"), Timeout);

        Assert.Throws<ArgumentException>(() => store.Reserve(id, "set_parameters", Args("{\"a\":2}"), Timeout));
        Assert.Throws<ArgumentException>(() => store.Reserve(id, "delete_elements", Args("{\"a\":1}"), Timeout));
    }

    [Fact]
    public void An_id_from_another_bridge_session_is_refused()
    {
        var store = new OperationStore();

        Assert.Throws<ArgumentException>(() => store.Reserve($"{Guid.NewGuid():N}:{Guid.NewGuid()}", "set_parameters", Args(), Timeout));
        Assert.Throws<ArgumentException>(() => store.Reserve("not-an-id", "set_parameters", Args(), Timeout));
    }

    [Fact]
    public void A_long_session_runs_far_more_operations_than_the_receipt_limit()
    {
        // 0.7.0 refused every tracked operation after maxRecords of them, until Revit restarted.
        var store = new OperationStore(maxRecords: 100);

        for (int i = 0; i < 25_000; i++)
            RunOne(store);
    }

    [Fact]
    public void A_retired_id_can_never_run_again()
    {
        var store = new OperationStore(maxRecords: 3);
        string first = RunOne(store);
        for (int i = 0; i < 5; i++) RunOne(store);   // pushes the first receipt out

        var error = Assert.Throws<ArgumentException>(() => store.Reserve(first, "set_parameters", Args(), Timeout));

        Assert.Equal(OperationStore.RetiredMessage, error.Message);
        Assert.Equal("retired", State(store, first));
    }

    [Fact]
    public void A_recent_receipt_survives_retirement_of_older_ones()
    {
        var store = new OperationStore(maxRecords: 3);
        for (int i = 0; i < 10; i++) RunOne(store);
        string recent = RunOne(store);

        Assert.Equal("succeeded", State(store, recent));
        var (_, isNew) = store.Reserve(recent, "set_parameters", Args(), Timeout);
        Assert.False(isNew);
    }

    [Fact]
    public void Receipts_that_are_still_queued_or_running_are_never_retired()
    {
        var store = new OperationStore(maxRecords: 2);
        string queuedId = NewId(store), runningId = NewId(store);
        var queued = store.Reserve(queuedId, "set_parameters", Args(), Timeout).Entry;
        var running = store.Reserve(runningId, "set_parameters", Args(), Timeout).Entry;
        Assert.True(store.TryStart(running));

        // Both slots are in flight: a third operation is refused and nothing is thrown away.
        var error = Assert.Throws<InvalidOperationException>(() => store.Reserve(NewId(store), "set_parameters", Args(), Timeout));
        Assert.Contains("still queued or running", error.Message);
        Assert.Equal("queued", State(store, queuedId));
        Assert.Equal("running", State(store, runningId));

        // Once one finishes there is room again.
        store.Complete(running, new OperationStore.Reply(200, new { ok = true }));
        Assert.True(store.Reserve(NewId(store), "set_parameters", Args(), Timeout).IsNew);
        Assert.Equal("queued", State(store, queuedId));
        Assert.NotNull(queued.Completion);
    }

    [Fact]
    public void The_retired_id_limit_is_the_only_hard_stop()
    {
        var store = new OperationStore(maxRecords: 2, maxRetired: 3);
        for (int i = 0; i < 5; i++) RunOne(store);   // 2 receipts kept + 3 retired IDs: full

        var error = Assert.Throws<InvalidOperationException>(() => store.Reserve(NewId(store), "set_parameters", Args(), Timeout));

        Assert.Contains("Restart Revit", error.Message);
    }

    [Fact]
    public async Task A_finished_operation_whose_result_expired_is_not_repeated()
    {
        var store = new OperationStore(maxResults: 1);
        string first = RunOne(store);
        RunOne(store);   // only one full result is kept: the first one's is dropped

        var (entry, isNew) = store.Reserve(first, "set_parameters", Args(), Timeout);
        var reply = await store.Wait(entry);

        Assert.False(isNew);
        Assert.Equal(409, reply.Status);
        Assert.Equal("succeeded", State(store, first));
    }

    [Fact]
    public async Task A_queued_operation_past_its_deadline_expires_without_running()
    {
        var store = new OperationStore();
        string id = NewId(store);
        var (entry, _) = store.Reserve(id, "set_parameters", Args(), TimeSpan.Zero);
        await Task.Delay(20);

        Assert.False(store.TryStart(entry));
        var reply = await store.Wait(entry);

        Assert.Equal(408, reply.Status);
        Assert.Equal("expired_before_start", State(store, id));
    }

    [Fact]
    public void An_id_this_session_never_saw_is_unknown()
    {
        var store = new OperationStore();

        Assert.Equal("unknown", State(store, NewId(store)));
    }
}
