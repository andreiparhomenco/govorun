using System.Text.Json;
using Govorun.Core.History;

namespace Govorun.Core.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "govorun-tests-" + Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_dir, "history.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir cleanup is best-effort */ }
    }

    [Fact]
    public void ConcurrentAddsProduceValidCappedFile()
    {
        var store = new HistoryStore(HistoryPath);

        Parallel.For(0, 50, i => store.Add($"dictation {i}"));

        // Saves are queued on the thread pool; wait for the queue to drain.
        WaitForFile(HistoryPath);

        var json = File.ReadAllText(HistoryPath);
        var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
        Assert.NotNull(loaded);
        Assert.Equal(30, loaded!.Count);
        Assert.False(File.Exists(HistoryPath + ".tmp"));
    }

    [Fact]
    public async Task EntriesCanBeEnumeratedWhileAddsRun()
    {
        var store = new HistoryStore(HistoryPath);
        store.Add("seed");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested) store.Add($"entry {i++}");
        });

        // Before Entries returned a snapshot this threw "collection was modified".
        while (!cts.IsCancellationRequested)
            foreach (var entry in store.Entries)
                Assert.NotNull(entry.Text);

        await writer;
    }

    [Fact]
    public void ReloadsWhatWasSaved()
    {
        var store = new HistoryStore(HistoryPath);
        store.Add("привет, мир");
        WaitForFile(HistoryPath);

        var reloaded = new HistoryStore(HistoryPath);
        Assert.Equal("привет, мир", reloaded.Entries.Single().Text);
    }

    [Fact]
    public void BlankTextIsIgnored()
    {
        var store = new HistoryStore(HistoryPath);
        store.Add("   ");
        store.Add("");
        Assert.Empty(store.Entries);
    }

    private static void WaitForFile(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) { Thread.Sleep(200); return; }
            Thread.Sleep(50);
        }
        throw new TimeoutException($"History was never written to {path}");
    }
}
