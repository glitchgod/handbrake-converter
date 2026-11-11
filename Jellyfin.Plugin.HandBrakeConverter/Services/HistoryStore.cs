using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

public class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string Title { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    /// <summary>Converted | Skipped | Failed</summary>
    public string Result { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
    public long OldBytes { get; set; }
    public long NewBytes { get; set; }

    /// <summary>Wall-clock time for the conversion, including time spent paused.</summary>
    public double Seconds { get; set; }

    /// <summary>auto | queue</summary>
    public string Source { get; set; } = "auto";

    /// <summary>The queue job this came from (empty for auto-convert).</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Library an auto-convert file belongs to (empty for manual jobs).</summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>File name of the converted video (same as the original's when it was replaced in place).</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>true = original replaced in place; false = original kept next to a converted copy.</summary>
    public bool Replaced { get; set; }

    // The encode settings that were used (so History can say exactly what was done).
    public string Encoder { get; set; } = string.Empty;
    public int Quality { get; set; }
    public string Preset { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public int MaxHeight { get; set; }
    public string Audio { get; set; } = string.Empty;
    public string Subtitles { get; set; } = string.Empty;
}

/// <summary>The results log shown on the History tab (newest first, capped). Persisted as history.json.</summary>
public class HistoryStore
{
    private const int Keep = 500;

    private readonly string _file;
    private readonly ILogger<HistoryStore> _log;
    private readonly object _lock = new();
    private List<HistoryEntry> _entries = new();

    public HistoryStore(StateStore state, ILogger<HistoryStore> log)
    {
        _log = log;
        _file = Path.Combine(state.DataDir, "history.json");
        try
        {
            if (File.Exists(_file)) _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not read history file, starting empty");
            _entries = new();
        }
    }

    public void Add(HistoryEntry e)
    {
        lock (_lock)
        {
            _entries.Insert(0, e);
            if (_entries.Count > Keep) _entries.RemoveRange(Keep, _entries.Count - Keep);
            Save();
        }
    }

    public List<HistoryEntry> List(int limit)
    {
        lock (_lock) return _entries.Take(limit).ToList();
    }

    public HistoryEntry? Find(string id)
    {
        lock (_lock) return _entries.FirstOrDefault(e => e.Id == id);
    }

    /// <summary>Adds a sentence to an existing entry's detail (used to report what happened after the fact).</summary>
    public void Annotate(string id, string text)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e == null) return;
            e.Detail = string.IsNullOrEmpty(e.Detail) ? text : e.Detail + " " + text;
            Save();
        }
    }

    public void Clear()
    {
        lock (_lock) { _entries.Clear(); Save(); }
    }

    private void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save history");
        }
    }
}
