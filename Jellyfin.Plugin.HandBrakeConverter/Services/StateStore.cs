using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>
/// "Already converted" database. Items are tracked by Jellyfin item id AND by output path, because a replaced
/// file can come back from a rescan as a brand-new item id; the path check is what prevents re-conversion loops.
/// </summary>
public class StateStore
{
    private readonly string _file;
    private readonly ILogger<StateStore> _log;
    private readonly object _lock = new();
    private StateData _data = new();

    public StateStore(MediaBrowser.Common.Configuration.IApplicationPaths paths, ILogger<StateStore> log)
    {
        _log = log;
        var dir = Path.Combine(paths.DataPath, "plugins", "HandBrakeConverter");
        Directory.CreateDirectory(dir);
        DataDir = dir;
        _file = Path.Combine(dir, "state.json");
        Load();
    }

    public string DataDir { get; }

    public class StateData
    {
        public HashSet<string> ProcessedIds { get; set; } = new();
        public HashSet<string> ProcessedPaths { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Failures { get; set; } = new();
        public long BytesSaved { get; set; }
        public int Converted { get; set; }
        public int Skipped { get; set; }
        public bool Paused { get; set; }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                _data = JsonSerializer.Deserialize<StateData>(File.ReadAllText(_file)) ?? new StateData();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not read state file, starting empty (a backup is kept)");
            try { File.Copy(_file, _file + ".bad", true); } catch { }
            _data = new StateData();
        }
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
        File.Move(tmp, _file, true);
    }

    public bool IsProcessed(string itemId, string path)
    {
        lock (_lock) return _data.ProcessedIds.Contains(itemId) || _data.ProcessedPaths.Contains(path);
    }

    public int FailureCount(string path)
    {
        lock (_lock) return _data.Failures.TryGetValue(path, out var n) ? n : 0;
    }

    public void MarkProcessed(string itemId, string sourcePath, string outputPath, long bytesSaved)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(itemId)) _data.ProcessedIds.Add(itemId);
            _data.ProcessedPaths.Add(sourcePath);
            _data.ProcessedPaths.Add(outputPath);
            _data.Failures.Remove(sourcePath);
            _data.Converted++;
            _data.BytesSaved += bytesSaved;
            Save();
        }
    }

    /// <summary>Result was discarded (not smaller); the original stays and is never retried.</summary>
    public void MarkSkipped(string itemId, string sourcePath)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(itemId)) _data.ProcessedIds.Add(itemId);
            _data.ProcessedPaths.Add(sourcePath);
            _data.Failures.Remove(sourcePath);
            _data.Skipped++;
            Save();
        }
    }

    /// <summary>Remember an extra path as handled (e.g. a restored original) without counting a conversion.</summary>
    public void MarkPath(string path)
    {
        lock (_lock) { _data.ProcessedPaths.Add(path); Save(); }
    }

    /// <summary>Lets Auto-convert try a failed file again (the Retry button on the History tab).</summary>
    public void ClearFailure(string path)
    {
        lock (_lock) { if (_data.Failures.Remove(path)) Save(); }
    }

    public int SkippedCount() { lock (_lock) return _data.Skipped; }

    /// <summary>The Pause button on the Overview tab; survives restarts and "Reset".</summary>
    public bool Paused
    {
        get { lock (_lock) return _data.Paused; }
        set { lock (_lock) { _data.Paused = value; Save(); } }
    }

    public int MarkFailed(string path)
    {
        lock (_lock)
        {
            _data.Failures[path] = FailureCount(path) + 1;
            Save();
            return _data.Failures[path];
        }
    }

    public (int Converted, long BytesSaved, int Failed) Stats()
    {
        lock (_lock) return (_data.Converted, _data.BytesSaved, _data.Failures.Count);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _data = new StateData { Paused = _data.Paused };
            Save();
        }
    }
}
