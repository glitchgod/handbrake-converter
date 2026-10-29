using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;
using Jellyfin.Plugin.HandBrakeConverter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.HandBrakeConverter.Api;

public class RemoveRequest
{
    public string[] Ids { get; set; } = Array.Empty<string>();
}

public class QueueRequest
{
    /// <summary>Movies, episodes, seasons or series (the last two expand to every episode).</summary>
    public string[] ItemIds { get; set; } = Array.Empty<string>();

    public ConversionProfile Profile { get; set; } = new();

    /// <summary>true = start even outside the schedule window.</summary>
    public bool IgnoreSchedule { get; set; }
}

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("HandBrakeConverter")]
[Produces("application/json")]
public class HandBrakeConverterController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, string?> VersionCache = new();

    private readonly ConversionService _service;
    private readonly BacklogScanner _backlog;
    private readonly HistoryStore _history;
    private readonly HandBrakeCapabilities _caps;
    private readonly TranscodeStore _transcodes;
    private readonly HandBrakeLocator _locator;
    private readonly StateStore _state;
    private readonly JobStore _jobs;
    private readonly TrashStore _trash;
    private readonly ILibraryManager _library;

    public HandBrakeConverterController(
        ConversionService service,
        BacklogScanner backlog,
        HistoryStore history,
        HandBrakeCapabilities caps,
        TranscodeStore transcodes,
        HandBrakeLocator locator,
        StateStore state,
        JobStore jobs,
        TrashStore trash,
        ILibraryManager library)
    {
        _service = service;
        _backlog = backlog;
        _history = history;
        _caps = caps;
        _transcodes = transcodes;
        _locator = locator;
        _state = state;
        _jobs = jobs;
        _trash = trash;
        _library = library;
    }

    [HttpGet("Status")]
    public async Task<ActionResult> Status(CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        var launcher = _locator.Resolve(cfg.HandBrakePath);
        string? version = null;
        if (launcher != null)
        {
            var key = launcher.FileName + "|" + string.Join(' ', launcher.PrefixArgs) + "|" + launcher.Description;
            if (!VersionCache.TryGetValue(key, out version) || version == null)
            {
                version = await HandBrakeLocator.VersionAsync(launcher, ct).ConfigureAwait(false);
                VersionCache[key] = version;
            }
        }

        var (converted, saved, failed) = _state.Stats();
        var snap = _service.Snapshot();
        return Ok(new
        {
            snap.State,
            snap.Message,
            snap.CurrentFile,
            snap.CurrentJobId,
            snap.Progress,
            snap.Eta,
            snap.Suspended,
            Paused = snap.UserPaused,
            HandBrakeFound = launcher != null,
            HandBrakeSource = launcher?.Description,
            HandBrakePath = launcher?.FileName,
            HandBrakeVersion = version,
            Converted = converted,
            BytesSaved = saved,
            Failed = failed,
            Skipped = _state.SkippedCount(),
            TrashCount = _trash.List().Count,
            TrashBytes = _trash.TotalBytes(),
            Queued = _jobs.QueuedCount()
        });
    }

    /// <summary>Auto-process backlog (libraries with Auto-process on).</summary>
    [HttpGet("Pending")]
    public ActionResult Pending([FromQuery] int limit = 50)
    {
        var cfg = Plugin.Instance!.Configuration;
        var items = _backlog.FindPending(cfg, Math.Clamp(limit, 1, 500));
        return Ok(items.Select(i => new { i.Path, i.Library }));
    }

    /// <summary>
    /// Browse for the Convert tab. With libraryId: the movies and series in that library (optional search).
    /// With parentId: the seasons or episodes directly under a series or season.
    /// </summary>
    [HttpGet("Browse")]
    public ActionResult Browse([FromQuery] string? libraryId, [FromQuery] string? parentId, [FromQuery] string? search, [FromQuery] int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 500);
        IEnumerable<BaseItem> items;

        if (Guid.TryParse(parentId, out var parent))
        {
            items = _library.GetItemList(new InternalItemsQuery
            {
                ParentId = parent,
                Recursive = false,
                IncludeItemTypes = new[] { BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie },
                IsVirtualItem = false
            }).OrderBy(i => i.IndexNumber ?? int.MaxValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        }
        else if (Guid.TryParse(libraryId, out var lib))
        {
            items = _library.GetItemList(new InternalItemsQuery
            {
                ParentId = lib,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false
            });
            if (!string.IsNullOrWhiteSpace(search))
                items = items.Where(i => (i.Name ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));
            items = items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            return BadRequest(new { Error = "libraryId or parentId required" });
        }

        return Ok(items.Take(limit).Select(i => new
        {
            Id = i.Id.ToString("N"),
            Name = i.Name,
            Kind = i.GetType().Name,
            Year = i.ProductionYear,
            Index = i.IndexNumber
        }));
    }

    [HttpPost("Queue")]
    public ActionResult Enqueue([FromBody] QueueRequest req)
    {
        if (req.ItemIds.Length == 0) return BadRequest(new { Error = "Nothing selected." });

        var jobs = new List<Job>();
        var missing = 0;
        foreach (var raw in req.ItemIds)
        {
            if (!Guid.TryParse(raw, out var id)) continue;
            var files = _backlog.ExpandTargets(id);
            if (files.Count == 0) missing++;
            foreach (var f in files)
            {
                var path = f.Path;
                if (!VideoFiles.IsVideo(path)) continue;
                jobs.Add(new Job
                {
                    ItemId = f.Id.ToString("N"),
                    Path = path,
                    Title = f.Name ?? System.IO.Path.GetFileNameWithoutExtension(path),
                    Profile = req.Profile,
                    IgnoreSchedule = req.IgnoreSchedule
                });
            }
        }

        var (added, skipped) = _jobs.Add(jobs);
        return Ok(new { Added = added, AlreadyQueued = skipped, NothingFound = missing });
    }

    [HttpGet("Queue")]
    public ActionResult GetQueue() =>
        Ok(_jobs.List().Select(j => new
        {
            j.Id,
            j.Title,
            j.Path,
            j.Status,
            j.Error,
            j.IgnoreSchedule,
            DeleteOriginal = j.Profile.DeleteOriginal,
            Encoder = j.Profile.HandBrake.Encoder,
            Subtitles = j.Profile.HandBrake.SubtitleMode,
            MaxHeight = j.Profile.HandBrake.MaxHeight,
            j.Added,
            j.Finished
        }));

    /// <summary>up | down | top | bottom, for a queued job.</summary>
    [HttpPost("Queue/{id}/Move")]
    public ActionResult MoveJob(string id, [FromQuery] string to) => _jobs.Move(id, to) ? Ok(new { Ok = true }) : NotFound();

    /// <summary>Deletes queued/finished jobs and stops a running one.</summary>
    [HttpPost("Queue/Remove")]
    public ActionResult RemoveJobs([FromBody] RemoveRequest req) => Ok(new { Removed = _jobs.Remove(req.Ids) });

    /// <summary>Originals held after "replace original" (restorable until they expire).</summary>
    [HttpGet("Trash")]
    public ActionResult GetTrash() =>
        Ok(_trash.List().Select(e => new { e.Id, Name = System.IO.Path.GetFileName(e.OriginalPath), e.OriginalPath, e.Size, e.Trashed, e.Expires }));

    /// <summary>Puts the original back beside the converted file as "Name - Original.ext" (shows up as a second version).</summary>
    [HttpPost("Trash/{id}/Restore")]
    public ActionResult RestoreTrash(string id)
    {
        try
        {
            var path = _trash.Restore(id);
            if (path == null) return NotFound(new { Error = "That file is no longer in the holding folder." });
            try { _library.QueueLibraryScan(); } catch { }
            return Ok(new { Path = path });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpPost("Trash/{id}/Delete")]
    public ActionResult DeleteTrash(string id) => _trash.DeleteNow(id) ? Ok(new { Ok = true }) : NotFound();

    /// <summary>Video encoders this HandBrake can use on this server (empty list = could not be read).</summary>
    [HttpGet("Encoders")]
    public async Task<ActionResult> Encoders(CancellationToken ct)
    {
        var launcher = _locator.Resolve(Plugin.Instance!.Configuration.HandBrakePath);
        if (launcher == null) return Ok(new { Encoders = Array.Empty<string>() });
        return Ok(new { Encoders = await _caps.EncodersAsync(launcher, ct).ConfigureAwait(false) });
    }

    /// <summary>Makes a 2-second sample encode with one encoder to prove it works here.</summary>
    [HttpPost("Encoders/Test")]
    public async Task<ActionResult> TestEncoder([FromQuery] string name, CancellationToken ct)
    {
        var launcher = _locator.Resolve(Plugin.Instance!.Configuration.HandBrakePath);
        if (launcher == null) return BadRequest(new { Error = "HandBrake is not installed." });
        var available = await _caps.EncodersAsync(launcher, ct).ConfigureAwait(false);
        if (!available.Contains(name, StringComparer.OrdinalIgnoreCase)) return BadRequest(new { Error = "HandBrake doesn't list that encoder on this server." });
        var (ok, message) = await _caps.TestAsync(launcher, name, ct).ConfigureAwait(false);
        return Ok(new { Ok = ok, Message = message });
    }

    /// <summary>What Jellyfin's own transcode logs say gets re-encoded when people watch, with suggested settings.</summary>
    [HttpGet("Insights")]
    public ActionResult Insights([FromQuery] int days = 60)
    {
        days = Math.Clamp(days, 1, 365);
        try { _transcodes.Scan(); } catch { }
        var result = TranscodeInsights.Analyze(_transcodes.Sessions(days), days);
        result.LogDirectory = _transcodes.LogDirectory;
        return Ok(result);
    }

    /// <summary>Results log, newest first.</summary>
    [HttpGet("History")]
    public ActionResult GetHistory([FromQuery] int limit = 200) => Ok(_history.List(Math.Clamp(limit, 1, 500)));

    /// <summary>Retry a failed file: a queue job goes back in the queue, an auto-convert file becomes eligible again.</summary>
    [HttpPost("History/{id}/Retry")]
    public ActionResult RetryHistory(string id)
    {
        var e = _history.Find(id);
        if (e == null || e.Result != "Failed") return NotFound(new { Error = "Nothing to retry." });
        if (e.JobId.Length > 0)
        {
            if (!_jobs.Requeue(e.JobId)) return NotFound(new { Error = "That job is no longer in the Queue. Add it again from Manual convert." });
        }
        else
        {
            _state.ClearFailure(e.Path);
        }

        return Ok(new { Ok = true });
    }

    [HttpPost("History/Clear")]
    public ActionResult ClearHistory()
    {
        _history.Clear();
        return Ok(new { Ok = true });
    }

    [HttpPost("Pause")]
    public ActionResult Pause()
    {
        _service.SetPaused(true);
        return Ok(new { Paused = true });
    }

    [HttpPost("Resume")]
    public ActionResult Resume()
    {
        _service.SetPaused(false);
        return Ok(new { Paused = false });
    }

    [HttpPost("ResetState")]
    public ActionResult ResetState()
    {
        _state.Reset();
        return Ok(new { Ok = true });
    }
}
