using System.Diagnostics;
using Xunit;

namespace RevitBridge.Tests;

/// <summary>bridge.json is replaced in one step: a reader never sees an empty, half-written or missing file.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("pi-revit-atomic-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private string Target => Path.Combine(_folder, "bridge.json");

    [Fact]
    public void A_new_file_is_written()
    {
        Assert.True(AtomicFile.WriteAllText(Target, "{\"port\":47777}"));

        Assert.Equal("{\"port\":47777}", File.ReadAllText(Target));
    }

    [Fact]
    public void An_existing_file_is_replaced()
    {
        File.WriteAllText(Target, "old content that is longer than the new one");

        Assert.True(AtomicFile.WriteAllText(Target, "new"));

        Assert.Equal("new", File.ReadAllText(Target));
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        AtomicFile.WriteAllText(Target, "one");
        AtomicFile.WriteAllText(Target, "two");

        Assert.Equal(new[] { "bridge.json" }, Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_reader_never_sees_an_empty_partial_or_missing_file_while_it_is_rewritten()
    {
        // Two complete versions of clearly different length. A plain overwrite truncates the
        // file first, so a reader in that moment gets an empty or cut-off file (0.7.0 did that).
        string small = "{\"version\":\"small\"}";
        string large = "{\"version\":\"large\",\"padding\":\"" + new string('x', 200_000) + "\"}";
        AtomicFile.WriteAllText(Target, small);

        using var stop = new CancellationTokenSource();
        var wrongContent = new List<string>();
        int reads = 0;
        var reader = Task.Run(() =>
        {
            // Polls like a Pi session does: open, read, close, pause. A missing file throws
            // here and fails the test.
            while (!stop.IsCancellationRequested)
            {
                using (var stream = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var text = new StreamReader(stream))
                {
                    string content = text.ReadToEnd();
                    if (content != small && content != large) wrongContent.Add($"{content.Length} characters");
                }
                reads++;
                Thread.Sleep(1);
            }
        });

        // Rewrite without pause for a while, so every poll lands on a file that is being replaced.
        int writes = 0, inPlace = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            if (!AtomicFile.WriteAllText(Target, writes % 2 == 0 ? large : small)) inPlace++;
            writes++;
        }
        stop.Cancel();
        await reader;

        Assert.True(reads > 20, $"the reader only got {reads} reads in");
        // Every one-step replace is invisible to the reader. Only a write that had to fall back
        // to an in-place overwrite (the file stayed locked for the whole patience) may show.
        Assert.True(inPlace <= writes / 20, $"{inPlace} of {writes} writes were not replaced in one step");
        if (inPlace == 0) Assert.Empty(wrongContent);
    }

    [Fact]
    public async Task A_reader_that_locks_the_file_for_a_moment_only_delays_the_write()
    {
        // .NET's File.ReadAllText (used by another Revit's bridge) does not allow the file to
        // be replaced while it is open; the write waits for that reader instead of failing.
        AtomicFile.WriteAllText(Target, "first");
        var holding = new TaskCompletionSource();
        var reader = Task.Run(async () =>
        {
            using var stream = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
            holding.SetResult();
            await Task.Delay(120);
        });
        await holding.Task;

        bool replacedInOneStep = AtomicFile.WriteAllText(Target, "second");
        await reader;

        Assert.True(replacedInOneStep);
        Assert.Equal("second", File.ReadAllText(Target));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_file_that_stays_locked_is_still_updated()
    {
        // Something keeps the file open past the patience: the update must not be lost.
        AtomicFile.WriteAllText(Target, "first");
        bool replacedInOneStep;
        using (new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            replacedInOneStep = AtomicFile.WriteAllText(Target, "second", patience: TimeSpan.FromMilliseconds(100));

        Assert.False(replacedInOneStep);
        Assert.Equal("second", File.ReadAllText(Target));
        Assert.Single(Directory.GetFiles(_folder));
    }
}
