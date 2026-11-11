using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

public class TrashEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; set; } = string.Empty;
    public string TrashedPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime Trashed { get; set; } = DateTime.Now;
    public DateTime Expires { get; set; }
}

/// <summary>
/// Holding folder for replaced originals. Nothing here is visible to Jellyfin (it lives outside the libraries); entries are
/// purged after their retention time or can be restored from the admin page.
/// </summary>
public class TrashStore
{
    private readonly string _file;
    private readonly StateStore _state;
    private readonly ILogger<TrashStore> _log;
    private readonly object _lock = new();
    private List<TrashEntry> _entries = new();
    private DateTime _lastPurge = DateTime.MinValue;

    public TrashStore(StateStore state, ILogger<TrashStore> log)
    {
        _state = state;
        _log = log;
        _file = Path.Combine(state.DataDir, "trash.json");
        try
        {
            if (File.Exists(_file)) _entries = JsonSerializer.Deserialize<List<TrashEntry>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not read trash index; starting empty (backup kept)");
            try { File.Copy(_file, _file + ".bad", true); } catch { }
        }
    }

    public const string HiddenFolderName = ".hb-originals";

    /// <summary>
    /// Where a replaced original is held. Blank setting = a hidden folder inside the same library folder as the video, so the move
    /// stays on the same drive (instant, no copying, nothing written to the system disk). Jellyfin skips folders that contain a
    /// ".ignore" file, which is created in it.
    /// </summary>
    public string HoldingFolderFor(string configured, string sourcePath, IEnumerable<string> libraryRoots)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        string? best = null;
        foreach (var r in libraryRoots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var root = r.TrimEnd('/', '\\');
            if (sourcePath.StartsWith(root + "/", StringComparison.Ordinal) && (best == null || root.Length > best.Length)) best = root;
        }

        return Path.Combine(best ?? Path.GetDirectoryName(sourcePath)!, HiddenFolderName);
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
        File.Move(tmp, _file, true);
    }

    /// <summary>Moves a file into the holding folder and records it. Returns the entry.</summary>
    public TrashEntry Add(string sourcePath, int days, string holdingRoot)
    {
        var root = holdingRoot;
        Directory.CreateDirectory(root);
        var ignore = Path.Combine(root, ".ignore"); // tells Jellyfin's library scan to skip this folder
        if (!File.Exists(ignore)) File.WriteAllText(ignore, string.Empty);
        var entry = new TrashEntry { OriginalPath = sourcePath, Trashed = DateTime.Now, Expires = DateTime.Now.AddDays(Math.Max(1, days)) };
        var folder = Path.Combine(root, entry.Id);
        Directory.CreateDirectory(folder);
        entry.TrashedPath = Path.Combine(folder, Path.GetFileName(sourcePath));
        entry.Size = new FileInfo(sourcePath).Length;
        File.Move(sourcePath, entry.TrashedPath); // same drive = rename; other drive = copy + delete
        lock (_lock) { _entries.Add(entry); Save(); }
        return entry;
    }

    public List<TrashEntry> List()
    {
        lock (_lock) return _entries.OrderBy(e => e.Expires).ToList();
    }

    public long TotalBytes()
    {
        lock (_lock) return _entries.Sum(e => e.Size);
    }

    /// <summary>Deletes expired entries (at most once a minute unless force).</summary>
    public int Purge(bool force = false)
    {
        lock (_lock)
        {
            if (!force && DateTime.Now - _lastPurge < TimeSpan.FromMinutes(1)) return 0;
            _lastPurge = DateTime.Now;
            var due = _entries.Where(e => e.Expires <= DateTime.Now).ToList();
            foreach (var e in due) Remove(e);
            if (due.Count > 0) Save();
            return due.Count;
        }
    }

    private void Remove(TrashEntry e)
    {
        try
        {
            if (File.Exists(e.TrashedPath)) File.Delete(e.TrashedPath);
            var dir = Path.GetDirectoryName(e.TrashedPath);
            if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not remove trashed file {Path}", e.TrashedPath); }
        _entries.Remove(e);
    }

    public bool DeleteNow(string id)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e == null) return false;
            Remove(e);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Puts the original back next to the converted file as "Name - Original.ext" (Jellyfin lists it as a second version;
    /// the converted file already occupies the original name). Returns the restored path.
    /// </summary>
    public string? Restore(string id)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e == null || !File.Exists(e.TrashedPath)) return null;
            var dir = Path.GetDirectoryName(e.OriginalPath)!;
            var target = e.OriginalPath;
            if (File.Exists(target))
            {
                target = Path.Combine(dir, Path.GetFileNameWithoutExtension(e.OriginalPath) + " - Original" + Path.GetExtension(e.OriginalPath));
                var n = 2;
                while (File.Exists(target))
                    target = Path.Combine(dir, Path.GetFileNameWithoutExtension(e.OriginalPath) + $" - Original {n++}" + Path.GetExtension(e.OriginalPath));
            }

            File.Move(e.TrashedPath, target);
            _state.MarkPath(target);
            Remove(e);
            Save();
            return target;
        }
    }
}
