using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

public class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ItemId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ConversionProfile Profile { get; set; } = new();

    /// <summary>true = may run outside the schedule window ("run now").</summary>
    public bool IgnoreSchedule { get; set; }

    /// <summary>Queued | Running | Done | Skipped | Failed | Cancelled</summary>
    public string Status { get; set; } = "Queued";

    public string Error { get; set; } = string.Empty;
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? Finished { get; set; }
}

/// <summary>Manual conversion queue ("Convert" from the admin page). Persisted so it survives restarts.</summary>
public class JobStore
{
    private const int KeepFinished = 50;

    private readonly string _file;
    private readonly ILogger<JobStore> _log;
    private readonly object _lock = new();
    private readonly HashSet<string> _cancel = new();
    private List<Job> _jobs = new();

    public JobStore(StateStore state, ILogger<JobStore> log)
    {
        _log = log;
        _file = System.IO.Path.Combine(state.DataDir, "jobs.json");
        try
        {
            if (File.Exists(_file))
            {
                _jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(_file)) ?? new List<Job>();
                // A job that was running when the server stopped goes back in the queue.
                foreach (var j in _jobs.Where(j => j.Status == "Running")) j.Status = "Queued";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not read jobs file, starting with an empty queue");
            try { File.Copy(_file, _file + ".bad", true); } catch { }
            _jobs = new List<Job>();
        }
    }

    private void Save()
    {
        var finished = _jobs.Where(IsFinished).OrderByDescending(j => j.Finished).Skip(KeepFinished).ToList();
        foreach (var j in finished) _jobs.Remove(j);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_jobs));
        File.Move(tmp, _file, true);
    }

    /// <returns>(added, skipped as already queued/running)</returns>
    public (int Added, int Skipped) Add(IEnumerable<Job> jobs)
    {
        int added = 0, skipped = 0;
        lock (_lock)
        {
            foreach (var j in jobs)
            {
                if (_jobs.Any(x => x.Path == j.Path && x.Status is "Queued" or "Running")) { skipped++; continue; }
                _jobs.Add(j);
                added++;
            }

            if (added > 0) Save();
        }

        return (added, skipped);
    }

    /// <summary>First queued job the caller says may run now.</summary>
    public Job? Next(Func<Job, bool> allowed)
    {
        lock (_lock) return _jobs.FirstOrDefault(j => j.Status == "Queued" && allowed(j));
    }

    public void SetStatus(string id, string status, string error = "")
    {
        lock (_lock)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j == null) return;
            j.Status = status;
            j.Error = error;
            if (status is "Done" or "Skipped" or "Failed" or "Cancelled") { j.Finished = DateTime.Now; _cancel.Remove(id); }
            Save();
        }
    }

    /// <summary>Puts a failed or cancelled job back at the end of the queue.</summary>
    public bool Requeue(string id)
    {
        lock (_lock)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j == null || j.Status is "Queued" or "Running") return false;
            _jobs.Remove(j);
            j.Status = "Queued";
            j.Error = string.Empty;
            j.Finished = null;
            _jobs.Add(j);
            Save();
            return true;
        }
    }

    public bool IsCancelRequested(string id)
    {
        lock (_lock) return _cancel.Contains(id);
    }

    /// <summary>Running first, then the queue in run order, then finished jobs (newest first).</summary>
    public List<Job> List()
    {
        lock (_lock)
        {
            var finished = _jobs.Where(IsFinished).OrderByDescending(j => j.Finished ?? j.Added);
            return _jobs.Where(j => j.Status == "Running").Concat(_jobs.Where(j => j.Status == "Queued")).Concat(finished).ToList();
        }
    }

    /// <summary>Moves a queued job within the queue. <paramref name="to"/>: up | down | top | bottom.</summary>
    public bool Move(string id, string to)
    {
        lock (_lock)
        {
            var queued = _jobs.Where(j => j.Status == "Queued").ToList();
            var from = queued.FindIndex(j => j.Id == id);
            if (from < 0) return false;
            var target = to switch
            {
                "up" => from - 1,
                "down" => from + 1,
                "top" => 0,
                "bottom" => queued.Count - 1,
                _ => from
            };
            target = Math.Clamp(target, 0, queued.Count - 1);
            if (target == from) return true;

            // Re-place the job at the queued job currently occupying the target slot (finished/running jobs keep their places).
            var job = queued[from];
            var anchor = queued[target];
            _jobs.Remove(job);
            var at = _jobs.IndexOf(anchor);
            _jobs.Insert(target > from ? at + 1 : at, job);
            Save();
            return true;
        }
    }

    /// <summary>Queued and finished jobs are deleted; a running one is told to stop. Returns how many were affected.</summary>
    public int Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        var n = 0;
        lock (_lock)
        {
            foreach (var j in _jobs.Where(j => set.Contains(j.Id)).ToList())
            {
                if (j.Status == "Running") _cancel.Add(j.Id);
                else _jobs.Remove(j);
                n++;
            }

            if (n > 0) Save();
        }

        return n;
    }

    private static bool IsFinished(Job j) => j.Status is "Done" or "Skipped" or "Failed" or "Cancelled";

    public int QueuedCount()
    {
        lock (_lock) return _jobs.Count(j => j.Status == "Queued");
    }
}
