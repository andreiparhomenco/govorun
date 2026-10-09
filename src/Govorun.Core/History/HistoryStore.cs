using System.IO;
using System.Text.Json;

namespace Govorun.Core.History;

/// <summary>
/// A small, capped log of recent dictations — the recovery net for "I dictated
/// but it landed nowhere (no active field) or in the wrong window". Text only,
/// no audio: cheap to keep, cheap to persist, no privacy footprint beyond what
/// already got typed somewhere. Persisted to %APPDATA%\Govorun\history.json.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 30;

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<HistoryEntry> _entries = new();

    // Saves coalesce: one background loop writes until nothing is dirty, so a burst of
    // dictations costs one or two writes instead of one per dictation — and awaiting
    // _saveTask is a real "everything is on disk" signal, which per-Add tasks never gave.
    private readonly object _saveLock = new();
    private Task _saveTask = Task.CompletedTask;
    private bool _saveRunning;
    private bool _dirty;

    /// <summary>
    /// Raised after the in-memory list changes. Fired synchronously on whichever
    /// thread called <see cref="Add"/> — that is the ASR worker thread, not the UI
    /// thread, so handlers must marshal themselves.
    /// </summary>
    public event Action? Changed;

    public HistoryStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    /// <summary>
    /// Newest first. Returns a snapshot: handing out the live list would let a
    /// UI-thread enumeration race an <see cref="Add"/> on the ASR thread and throw
    /// "collection was modified" mid-render.
    /// </summary>
    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock)
        {
            _entries.Insert(0, new HistoryEntry(DateTime.UtcNow, text));
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        Changed?.Invoke();
        QueueSave();
    }

    private void QueueSave()
    {
        lock (_saveLock)
        {
            _dirty = true;
            if (_saveRunning) return;
            _saveRunning = true;
            _saveTask = Task.Run(SaveLoopAsync);
        }
    }

    private async Task SaveLoopAsync()
    {
        while (true)
        {
            lock (_saveLock)
            {
                if (!_dirty)
                {
                    _saveRunning = false;
                    return;
                }
                _dirty = false;
            }
            Save();
            // Give other adds a chance to mark the store dirty again, so a burst
            // collapses into the next pass instead of queueing more writes.
            await Task.Yield();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_filePath));
                if (loaded is not null) _entries = loaded;
            }
        }
        catch
        {
            // Corrupt history file — start empty rather than crash at boot.
            _entries = new List<HistoryEntry>();
        }
    }

    /// <summary>
    /// Completes once everything added so far is on disk and no write is in flight.
    /// Saves happen in the background, so without this a dictation made just before the
    /// app closes could be lost when the process exits.
    /// </summary>
    public Task FlushAsync()
    {
        lock (_saveLock) return _saveTask;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            List<HistoryEntry> snapshot;
            lock (_lock) snapshot = new List<HistoryEntry>(_entries);

            // Write-then-rename: a crash mid-write leaves the previous history
            // intact instead of a truncated file we'd discard at next boot.
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence; losing history to a locked/full disk
            // must never crash the dictation flow.
        }
    }
}
