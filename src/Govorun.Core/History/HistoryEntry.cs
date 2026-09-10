namespace Govorun.Core.History;

/// <summary>One dictated phrase kept for later recovery/copy.</summary>
public sealed record HistoryEntry(DateTime TimestampUtc, string Text);
