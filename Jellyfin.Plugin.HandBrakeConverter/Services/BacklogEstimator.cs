using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>What is still waiting, and a rough time for it based on how fast your finished conversions ran.</summary>
public record BacklogEta(int Files, long Bytes, double? Seconds);

/// <summary>
/// Overview ETA. Waiting = queued manual jobs + the Auto-convert backlog (minus the file being encoded). Speed = source bytes per
/// second over the last conversions in History, so the estimate is your own server's, including its pauses. The result is cached
/// briefly because the Overview polls every few seconds.
/// </summary>
public sealed class BacklogEstimator
{
    /// <summary>How long a result is reused (tests set 0).</summary>
    public static int CacheSeconds = 60;

    private static readonly Regex HandBrakeEta = new(@"(\d+)h(\d+)m(\d+)s", RegexOptions.Compiled);
    private static readonly object Lock = new();
    private static (DateTime At, string Current, BacklogEta Value)? _cached;

    private readonly BacklogScanner _backlog;
    private readonly JobStore _jobs;
    private readonly HistoryStore _history;

    public BacklogEstimator(BacklogScanner backlog, JobStore jobs, HistoryStore history)
    {
        _backlog = backlog;
        _jobs = jobs;
        _history = history;
    }

    /// <summary>HandBrake's "00h12m05s" as seconds, or null when it has no estimate yet.</summary>
    public static int? ParseEta(string? eta)
    {
        if (string.IsNullOrEmpty(eta)) return null;
        var m = HandBrakeEta.Match(eta);
        return m.Success ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) : null;
    }

    public BacklogEta Get(PluginConfiguration cfg, string currentPath)
    {
        lock (Lock)
        {
            if (_cached is { } c && c.Current == currentPath && (DateTime.UtcNow - c.At).TotalSeconds < CacheSeconds) return c.Value;
        }

        var value = Compute(cfg, currentPath);
        lock (Lock) _cached = (DateTime.UtcNow, currentPath, value);
        return value;
    }

    private BacklogEta Compute(PluginConfiguration cfg, string currentPath)
    {
        if (!cfg.Enabled) return new BacklogEta(0, 0, null);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in _jobs.List().Where(j => j.Status == "Queued")) paths.Add(j.Path);
        foreach (var p in _backlog.FindPending(cfg, int.MaxValue)) paths.Add(p.Path);
        if (!string.IsNullOrEmpty(currentPath)) paths.Remove(currentPath);

        long bytes = 0;
        foreach (var p in paths)
        {
            try { bytes += new FileInfo(p).Length; } catch { }
        }

        var done = _history.List(200).Where(h => h.Result == "Converted" && h.Seconds > 0 && h.OldBytes > 0).Take(30).ToList();
        var seconds = done.Sum(h => h.Seconds);
        double? eta = seconds > 0 && paths.Count > 0 ? bytes / (done.Sum(h => (double)h.OldBytes) / seconds) : null;
        return new BacklogEta(paths.Count, bytes, eta);
    }
}
