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
    public void ConcurrentAddsKeepTheListCapped()
    {
        var store = new HistoryStore(HistoryPath);

        Parallel.For(0, 50, i => store.Add($"dictation {i}"));

        Assert.Equal(30, store.Entries.Count);
    }

    [Fact]
    public async Task ConcurrentAddsProduceAValidFile()
    {
        var store = new HistoryStore(HistoryPath);
        Parallel.For(0, 50, i => store.Add($"dictation {i}"));
        // Deterministic: FlushAsync completes only when no write is in flight, so there
        // is no window where the file is half-replaced or a .tmp still exists.
        await store.FlushAsync();

        var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistoryPath));
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
    public async Task ReloadsWhatWasSaved()
    {
        var store = new HistoryStore(HistoryPath);
        store.Add("привет, мир");
        await store.FlushAsync();

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

    [Fact]
    public async Task FlushWithNothingToSaveDoesNothing()
    {
        var store = new HistoryStore(HistoryPath);

        await store.FlushAsync();

        // Nothing was added, so there is nothing to persist and no file to create.
        Assert.False(File.Exists(HistoryPath));
    }

}
