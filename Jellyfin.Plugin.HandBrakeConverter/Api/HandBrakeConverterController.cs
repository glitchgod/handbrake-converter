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

public class CheckTitleRequest
{
    public string ItemId { get; set; } = string.Empty;

    /// <summary>The settings to check against (the library's own or Conversion settings, as shown on the page).</summary>
    public ConversionProfile? Profile { get; set; }
}

public class QueueRequest
{
    /// <summary>Movies, episodes, seasons or series (the last two expand to every episode).</summary>
    public string[] ItemIds { get; set; } = Array.Empty<string>();

    public ConversionProfile Profile { get; set; } = new();

    /// <summary>true = start even outside the schedule window.</summary>
    public bool IgnoreSchedule { get; set; }

    /// <summary>true = convert again even if a file was already converted with these settings.</summary>
    public bool Force { get; set; }
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
    private readonly BacklogEstimator _eta;

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
        _eta = new BacklogEstimator(backlog, jobs, history);
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
        var waiting = _eta.Get(cfg, snap.CurrentFile);
        return Ok(new
        {
            snap.State,
            snap.Message,
            snap.CurrentFile,
            snap.CurrentJobId,
            snap.Origin,
            snap.Progress,
            snap.Eta,
            Also = _service.OtherSnapshots().Select(x => new { x.CurrentFile, x.CurrentJobId, x.Origin, x.Progress, x.Eta, x.Suspended }).ToList(),
            ParallelJobs = Math.Clamp(cfg.ParallelJobs, 1, ConversionService.MaxLanes),
            EtaSeconds = BacklogEstimator.ParseEta(snap.Eta),
            Waiting = new { waiting.Files, waiting.Bytes, waiting.Seconds },
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

    /// <summary>Stops the running automatic conversion so queued jobs can start (the automatic one resumes afterwards).</summary>
    [HttpPost("Queue/StopAutomatic")]
    public ActionResult StopAutomatic() => Ok(new { Stopped = _service.StopAutomatic() });

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
            Index = i.IndexNumber,
            ExcludedDirect = Plugin.Instance!.Configuration.Excluded.Any(x => string.Equals(x.ItemId, i.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)),
            Excluded = Plugin.Instance!.Configuration.Excluded.Any(x => string.Equals(x.ItemId, i.Id.ToString("N"), StringComparison.OrdinalIgnoreCase) || (i.Path != null && x.Covers(i.Path)))
        }));
    }

    /// <summary>Items Auto-convert must never touch.</summary>
    [HttpGet("Excluded")]
    public ActionResult GetExcluded() => Ok(Plugin.Instance!.Configuration.Excluded.Select(x => new { x.ItemId, x.Name }));

    /// <summary>Adds the item (movie, series, season or episode) to the exclusion list, or removes it when already there. Returns the new list.</summary>
    [HttpPost("Excluded/Toggle")]
    public ActionResult ToggleExcluded([FromQuery] string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest(new { Error = "bad id" });
        var key = guid.ToString("N");
        var cfg = Plugin.Instance!.Configuration;
        var existing = cfg.Excluded.FirstOrDefault(x => string.Equals(x.ItemId, key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            cfg.Excluded = cfg.Excluded.Where(x => x != existing).ToArray();
        }
        else
        {
            var item = _library.GetItemById(guid);
            if (item == null || string.IsNullOrEmpty(item.Path)) return NotFound(new { Error = "That item has no folder or file to exclude." });
            cfg.Excluded = cfg.Excluded.Append(new ExcludedItem { ItemId = key, Name = item.Name ?? string.Empty, Path = item.Path }).ToArray();
        }

        Plugin.Instance!.SaveConfiguration();
        return Ok(cfg.Excluded);
    }

    /// <summary>
    /// Dry run: what Auto-convert and the queue would do right now, with an estimated saving. Reads sizes only; no file is touched.
    /// The saving is guessed from your own finished conversions when there are enough, otherwise from what the encoder typically achieves.
    /// </summary>
    [HttpGet("DryRun")]
    public ActionResult DryRun()
    {
        var cfg = Plugin.Instance!.Configuration;
        var all = _backlog.FindPending(cfg, int.MaxValue);
        var done = _history.List(200).Where(h => h.Result == "Converted" && h.OldBytes > 1_000_000 && h.NewBytes > 0 && h.NewBytes < h.OldBytes * 2).Take(40).ToList();
        double? learned = done.Count >= 3 ? done.Sum(h => (double)h.NewBytes) / done.Sum(h => (double)h.OldBytes) : null;
        double Typical(string enc) => enc.Contains("265") || enc.Contains("hevc", StringComparison.OrdinalIgnoreCase) ? 0.55 : enc.Contains("av1", StringComparison.OrdinalIgnoreCase) ? 0.5 : enc.Contains("264") ? 0.8 : 0.7;

        long Size(string path) { try { return FileMeta.Length(path); } catch { return 0; } }
        var rows = all.Select(x => new { P = x, Size = Size(x.Path), Ratio = learned ?? Typical(x.Profile.HandBrake.Encoder ?? string.Empty) }).ToList();
        var libs = rows.GroupBy(r => r.P.Library).Select(g => new { Name = g.Key, Files = g.Count(), Bytes = g.Sum(r => r.Size), EstBytes = (long)g.Sum(r => r.Size * r.Ratio) }).ToList();
        var manual = _jobs.List().Where(j => j.Status == "Queued" && !j.IsAuto).ToList();
        var manualBytes = manual.Sum(j => Size(j.Path));
        var sample = rows.Take(100).Select(r =>
        {
            var note = string.Empty;
            if (r.P.Profile.DeleteOriginal && cfg.LinkedFiles != "normal" && FileMeta.IsLinked(r.P.Path, out var how)) note = cfg.LinkedFiles == "skip" ? "would be skipped: " + how : "linked (" + how + "): replaced in place, every link changes";
            return new { Name = System.IO.Path.GetFileName(r.P.Path), r.P.Library, r.Size, Est = (long)(r.Size * r.Ratio), Note = note };
        }).ToList();
        var eta = _eta.Get(cfg, string.Empty);
        return Ok(new
        {
            Enabled = cfg.Enabled,
            Libraries = libs,
            Files = rows.Count,
            Bytes = rows.Sum(r => r.Size),
            EstBytes = (long)rows.Sum(r => r.Size * r.Ratio),
            Manual = new { Files = manual.Count, Bytes = manualBytes },
            Excluded = cfg.Excluded.Length,
            EstSeconds = eta.Seconds,
            Basis = learned != null ? $"your last {done.Count} conversions (about {(int)Math.Round((1 - learned.Value) * 100)}% smaller)" : "what the chosen encoder typically achieves (a guess until you have a few finished conversions)",
            Sample = sample,
            More = Math.Max(0, rows.Count - sample.Count)
        });
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
                    Title = JobTitles.For(f, path),
                    Profile = req.Profile,
                    IgnoreSchedule = req.IgnoreSchedule,
                    Force = req.Force
                });
            }
        }

        // Files that were converted before: they will be skipped (no re-encode) unless "convert again" is ticked.
        var already = req.Force ? 0 : jobs.Count(j => _state.IsProcessed(j.ItemId, j.Path));
        var (added, skipped) = _jobs.Add(jobs);
        return Ok(new { Added = added, AlreadyQueued = skipped, NothingFound = missing, AlreadyConverted = already });
    }

    [HttpGet("Queue")]
    public ActionResult GetQueue()
    {
        var cfg = Plugin.Instance!.Configuration;
        _service.SyncAutoEntries(cfg);
        var snap = _service.Snapshot();
        var pending = _backlog.FindPending(cfg, int.MaxValue).GroupBy(p => p.LibraryId).ToDictionary(g => g.Key, g => g.ToList());
        var runningAuto = new[] { snap }.Concat(_service.OtherSnapshots()).Where(x => x.CurrentFile.Length > 0 && x.CurrentJobId.Length == 0).ToList();
        return Ok(_jobs.List().Where(j => j.Status is "Running" or "Queued").Select(j => j.IsAuto
            ? (object)new
            {
                j.Id,
                Kind = "auto",
                j.Title,
                j.LibraryId,
                Status = "Queued",
                Running = runningAuto.Any(x => x.Origin == j.Title),
                File = runningAuto.FirstOrDefault(x => x.Origin == j.Title)?.CurrentFile ?? string.Empty,
                Total = pending.TryGetValue(j.LibraryId, out var l) ? l.Count : 0,
                Next = (pending.TryGetValue(j.LibraryId, out var l2) ? l2 : new List<Pending>()).Take(25).Select(i => i.Path).ToList()
            }
            : new
            {
                j.Id,
                Kind = "",
                j.Title,
                j.Path,
                j.Status,
                j.Error,
                j.IgnoreSchedule,
                DeleteOriginal = j.Profile.DeleteOriginal,
                Encoder = j.Profile.HandBrake.Encoder,
                Subtitles = j.Profile.HandBrake.SubtitleMode,
                SubtitleLanguages = j.Profile.HandBrake.SubtitleLanguages,
                SubtitleKeepForced = j.Profile.HandBrake.SubtitleKeepForced,
                MaxHeight = j.Profile.HandBrake.MaxHeight,
                Container = ConversionService.ResolveContainer(j.Profile, j.Path).Extension.TrimStart('.').ToUpperInvariant(),
                Suffix = j.Profile.GetLabel(),
                j.Force,
                NewItem = ConversionService.ChangesExtension(j.Profile, j.Path),
                j.Added,
                j.Finished
            }));
    }

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
        if (launcher == null) return Ok(new { Encoders = Array.Empty<string>(), Tests = _caps.Results() });
        return Ok(new { Encoders = await _caps.EncodersAsync(launcher, ct).ConfigureAwait(false), Tests = _caps.Results() });
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

    /// <summary>How playback went (Jellyfin's transcode reasons per play and its ffmpeg logs), with one suggested value per setting.</summary>
    /// <summary>
    /// Tools > Check a title: one movie or episode as it is (video, audio, subtitles with their styles and sizes), what the given settings
    /// would make of it, and how it has played (Jellyfin's reason for each conversion while watching). Reads only.
    /// </summary>
    [HttpPost("CheckTitle")]
    public async Task<ActionResult> CheckTitle([FromBody] CheckTitleRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(req.ItemId, out var id)) return BadRequest(new { Error = "itemId required" });
        var item = _library.GetItemById(id);
        if (item == null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path)) return NotFound(new { Error = "That title has no video file." });
        var report = await _service.CheckTitleAsync(item.Path, req.Profile ?? Plugin.Instance!.Configuration.Defaults, ct).ConfigureAwait(false);

        // How it played: Jellyfin's own reasons per playback; before those were recorded, its transcode logs.
        try { _transcodes.Scan(); } catch { }
        var plays = _transcodes.Plays(365).Where(p => p.Path == item.Path).OrderByDescending(p => p.Time).Take(10)
            .Select(p => new PlayRow(p.Time, p.Client, p.Reasons.Count == 0 && p.VideoDirect && p.AudioDirect ? "Played directly" : "Converted while watching",
                string.Join(", ", p.Reasons.Select(TranscodeInsights.Friendly)))).ToList();
        if (plays.Count == 0)
            plays = _transcodes.Sessions(365).Where(s => s.Path == item.Path).OrderByDescending(s => s.Time).Take(10)
                .Select(s => new PlayRow(s.Time, string.Empty, "Converted while watching",
                    string.Join(", ", new[] { s.VideoTranscoded ? "video re-encoded" : null, s.AudioTranscoded ? "audio re-encoded" : null, s.BurnedSubtitles ? "subtitles burned in" : null, s.Tonemapped ? "HDR tone-mapped" : null }.Where(x => x != null)))).ToList();
        return Ok(report with { Plays = plays });
    }

    /// <summary>Tools > Update suggestions: progress of the manual check of Jellyfin's newest transcode logs, and the suggestions it gave.</summary>
    [HttpGet("SuggestionCheck")]
    public ActionResult SuggestionCheck() => Ok(_transcodes.CheckStatus());

    /// <summary>Starts reading Jellyfin's newest transcode logs (default 100) and working the suggestions out again (background; poll SuggestionCheck).</summary>
    [HttpPost("SuggestionCheck")]
    public ActionResult SuggestionCheckStart([FromQuery] int limit = 100) =>
        _transcodes.StartCheck(limit) ? Ok(_transcodes.CheckStatus()) : Conflict(new { Error = "A check is already running." });

    /// <summary>Tools > Repair giant subtitles: progress and findings of the scan.</summary>
    [HttpGet("GiantSubtitles")]
    public ActionResult GiantSubtitles() => Ok(_service.GiantStatus());

    /// <summary>Starts looking through every MP4 in the libraries for giant MOV_TEXT subtitles (background; poll GiantSubtitles).</summary>
    [HttpPost("GiantSubtitles/Scan")]
    public ActionResult GiantSubtitlesScan() => _service.StartGiantScan() ? Ok(_service.GiantStatus()) : Conflict(new { Error = "A scan is already running." });

    /// <summary>Queues a subtitles-only repair for every file the scan found (no re-encode).</summary>
    [HttpPost("GiantSubtitles/Fix")]
    public ActionResult GiantSubtitlesFix() => Ok(new { Added = _service.QueueGiantRepairs() });

    [HttpGet("Insights")]
    public ActionResult Insights([FromQuery] int days = 60)
    {
        days = Math.Clamp(days, 1, 365);
        try { _transcodes.Scan(); } catch { }
        var result = TranscodeInsights.Analyze(_transcodes.Sessions(days), _transcodes.Plays(days), days);
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
            if (!_jobs.Requeue(e.JobId)) return NotFound(new { Error = "That job is no longer in the Queue. Add it again from Convert titles." });
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
