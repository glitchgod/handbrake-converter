using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>
/// The background worker: each tick picks the next manual job (else the next auto-convert file) and converts it. An original is only
/// touched after the encode, size, duration and playback checks have all passed.
/// </summary>
public sealed class ConversionService : BackgroundService
{
    private readonly ILibraryManager _library;
    private readonly StateStore _state;
    private readonly JobStore _jobs;
    private readonly HandBrakeLocator _locator;
    private readonly TrashStore _trash;
    private readonly MediaProbe _probe;
    private readonly PlaybackWatcher _watcher;
    private readonly BacklogScanner _backlog;
    private readonly HistoryStore _history;
    private readonly HandBrakeCapabilities _caps;
    private readonly ILogger<ConversionService> _log;
    private readonly StatusBoard _board = new();

    public ConversionService(
        ILibraryManager library,
        StateStore state,
        JobStore jobs,
        HandBrakeLocator locator,
        TrashStore trash,
        MediaProbe probe,
        PlaybackWatcher watcher,
        BacklogScanner backlog,
        HistoryStore history,
        HandBrakeCapabilities caps,
        ILogger<ConversionService> log)
    {
        _library = library;
        _state = state;
        _jobs = jobs;
        _locator = locator;
        _trash = trash;
        _probe = probe;
        _watcher = watcher;
        _backlog = backlog;
        _history = history;
        _caps = caps;
        _log = log;
    }

    private static PluginConfiguration Cfg => Plugin.Instance!.Configuration;

    public ConversionStatus Snapshot()
    {
        var s = _board.Snapshot();
        s.UserPaused = _state.Paused;
        return s;
    }

    /// <summary>Pause freezes a running encode and stops anything new from starting; Continue undoes it. Persisted.</summary>
    public void SetPaused(bool paused)
    {
        _state.Paused = paused;
        _watcher.Wake();
    }

    /// <summary>Everything a conversion needs, worked out once up front.</summary>
    private sealed record Plan(Work Work, HandBrakeSettings HandBrake, string Source, string Final, string Temp, long SourceSize, int KeepDays, MediaInfo? SourceInfo)
    {
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public Job? Job => Work.Job;
        public ConversionProfile Profile => Work.Profile;
    }

    private sealed record Work(string ItemId, string Path, ConversionProfile Profile, Job? Job, string Origin);

    // ------------------------------------------------------------------ main loop

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Jellyfin finish starting before touching the library.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HandBrake Converter tick failed");
                _board.Rest("Error", ex.Message);
            }

            try { await _watcher.WaitAsync(TimeSpan.FromSeconds(didWork ? 3 : 30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <returns>true if something was processed (so the loop continues promptly).</returns>
    private async Task<bool> TickAsync(CancellationToken ct)
    {
        var cfg = Cfg;
        try { _trash.Purge(); } catch (Exception ex) { _log.LogWarning(ex, "Trash purge failed"); }

        if (!cfg.Enabled) { _board.Rest("Disabled", "Turned off (master switch on the Overview tab)."); return false; }
        if (_state.Paused) { _board.Rest("Paused", "Paused. Press Continue on the Overview tab to resume."); return false; }

        var inWindow = ScheduleEvaluator.IsAllowed(DateTime.Now, cfg);
        var manual = _jobs.Next(j => inWindow || j.IgnoreSchedule);
        if (manual == null && !inWindow)
        {
            var q = _jobs.QueuedCount();
            _board.Rest("Waiting", q > 0 ? $"Outside the scheduled window ({q} queued)." : "Outside the scheduled window.");
            return false;
        }

        if (cfg.PauseWhileStreaming && _watcher.IsAnyoneWatching(cfg))
        {
            _board.Set(s => { s.State = "Waiting"; s.Message = "Someone is playing media; will start when idle."; });
            return false;
        }

        var launcher = _locator.Resolve(cfg.HandBrakePath);
        if (launcher == null)
        {
            _board.Set(s => { s.State = "Error"; s.Message = "HandBrakeCLI not found. Install it on the server with: sudo apt install handbrake-cli (see the HandBrake section on the Overview tab)."; });
            return false;
        }

        Work? work = null;
        if (manual != null) work = new Work(manual.ItemId, manual.Path, manual.Profile, manual, "queue");
        else if (_backlog.FindPending(cfg, 1).FirstOrDefault() is { } p) work = new Work(p.ItemId, p.Path, p.Profile, null, p.Library);

        if (work == null) { _board.Rest("Idle", "Nothing to convert."); return false; }

        // A chosen encoder this HandBrake doesn't have (e.g. NVENC without the GPU) is a settings problem, not a bad file:
        // say so once instead of failing every file.
        var encoder = work.Profile.HandBrake.Encoder;
        var available = await _caps.EncodersAsync(launcher, ct).ConfigureAwait(false);
        if (available.Count > 0 && !available.Contains(encoder, StringComparer.OrdinalIgnoreCase))
        {
            var why = $"The encoder '{encoder}' is not available in this HandBrake. Pick another under Default settings > Video encoder (see HandBrake & tools for the ones that work here).";
            if (work.Job != null) Fail(work, why); else _board.Rest("Error", why);
            return work.Job != null;
        }

        return await ConvertAsync(work, launcher, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ one conversion

    /// <returns>false only when waiting for disk space (nothing was attempted).</returns>
    private async Task<bool> ConvertAsync(Work w, HandBrakeLocator.Launcher launcher, CancellationToken ct)
    {
        var cfg = Cfg;
        var src = w.Path;
        var profile = w.Profile;

        if (!File.Exists(src)) { Fail(w, "Source file not found: " + src); return true; }

        var (hb, ext) = ResolveContainer(profile, src);
        var dir = Path.GetDirectoryName(src)!;
        var baseName = Path.GetFileNameWithoutExtension(src);
        var final = Path.Combine(dir, profile.DeleteOriginal ? baseName + ext : baseName + " " + profile.GetLabel() + ext);

        if (!profile.DeleteOriginal && File.Exists(final))
        {
            if (w.Job != null) { _jobs.SetStatus(w.Job.Id, "Failed", "A converted copy already exists: " + Path.GetFileName(final)); return true; }
            _log.LogInformation("Converted copy already exists for {Src}; marking done", src);
            _state.MarkProcessed(w.ItemId, src, final, 0);
            Record(w, "Skipped", "A converted copy already exists: " + Path.GetFileName(final));
            return true;
        }

        if (profile.DeleteOriginal && final != src && File.Exists(final))
        {
            Fail(w, $"Target {Path.GetFileName(final)} already exists and is a different file.");
            return true;
        }

        var size = new FileInfo(src).Length;
        var keepDays = profile.DeleteOriginal ? Math.Max(0, cfg.KeepReplacedDays) : 0;
        var space = CheckSpace(cfg, dir, src, size, keepDays > 0);
        if (space != null)
        {
            _log.LogWarning("Not converting {Src}: {Why}", src, space);
            _board.Rest("Waiting", space);
            return false;
        }

        var info = await _probe.ProbeAsync(src, ct).ConfigureAwait(false);
        var hdrNote = await HdrDecisionAsync(w, hb, info, launcher, ct).ConfigureAwait(false);
        if (hdrNote != null)
        {
            _log.LogInformation("{Src}: {Why}", src, hdrNote);
            _state.MarkSkipped(w.ItemId, src);
            if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Skipped", hdrNote);
            Record(w, "Skipped", hdrNote);
            _board.Rest("Idle", hdrNote);
            return true;
        }

        // Non-video extension so Jellyfin never scans a half-written file.
        var plan = new Plan(w, hb, src, final, final + ".hbtmp", size, keepDays, info);
        TryDelete(plan.Temp);

        if (!await EncodeAsync(plan, launcher, ct).ConfigureAwait(false)) return true;

        var outSize = new FileInfo(plan.Temp).Length;
        if (!await ResultAcceptableAsync(plan, outSize, cfg, ct).ConfigureAwait(false)) return true;
        if (!await WaitForViewersAsync(plan, ct).ConfigureAwait(false)) return true;

        Swap(plan, outSize, cfg);
        return true;
    }

    /// <summary>
    /// HDR / Dolby Vision handling. Returns why the file must be left alone, or null to go ahead (in "keep" mode the settings are
    /// switched to the 10-bit encoder so the HDR information survives).
    /// </summary>
    private async Task<string?> HdrDecisionAsync(Work w, HandBrakeSettings hb, MediaInfo? info, HandBrakeLocator.Launcher launcher, CancellationToken ct)
    {
        if (info == null || !(info.IsHdr || info.DolbyVision) || hb.HdrMode == "ignore") return null;
        var kind = info.DolbyVision ? "a Dolby Vision" : "an HDR";
        if (hb.HdrMode != "keep") return $"Left alone: this is {kind} video and HDR videos are set to be skipped (Default settings > Video > HDR videos).";
        if (info.DolbyVision) return "Left alone: Dolby Vision can't be kept by a re-encode (change 'HDR videos' to convert it anyway).";

        var tenBit = HandBrakeArgs.TenBit(hb.Encoder);
        if (tenBit == null) return $"Left alone: {hb.Encoder} can't keep HDR (it has no 10-bit mode). Pick an H.265 or AV1 encoder, or change 'HDR videos'.";
        var available = await _caps.EncodersAsync(launcher, ct).ConfigureAwait(false);
        if (available.Count > 0 && !available.Contains(tenBit, StringComparer.OrdinalIgnoreCase))
            return $"Left alone: the 10-bit encoder '{tenBit}' needed to keep HDR is not available in this HandBrake.";
        hb.Encoder = tenBit;
        return null;
    }

    /// <summary>
    /// Container: "same as source" (and always when replacing a file) keeps the exact extension, so Jellyfin sees the same item
    /// (watch history, favourites and metadata survive). Formats MKV/MP4 can't stand in for (AVI, TS, ...) become MKV.
    /// </summary>
    private static (HandBrakeSettings Settings, string Extension) ResolveContainer(ConversionProfile profile, string src)
    {
        var hb = profile.HandBrake.Clone();
        var srcExt = Path.GetExtension(src);
        var low = srcExt.ToLowerInvariant();
        var wanted = (hb.Container ?? "source").ToLowerInvariant();

        if ((profile.DeleteOriginal || wanted == "source") && low is ".mkv" or ".mp4" or ".m4v")
        {
            hb.Container = low == ".mkv" ? "mkv" : "mp4";
            return (hb, srcExt);
        }

        hb.Container = wanted == "mp4" ? "mp4" : "mkv";
        return (hb, HandBrakeArgs.Extension(hb));
    }

    /// <summary>Runs HandBrake and watches it. Returns true when the temp file is ready; otherwise the outcome has been recorded.</summary>
    private async Task<bool> EncodeAsync(Plan p, HandBrakeLocator.Launcher launcher, CancellationToken ct)
    {
        var job = p.Job;
        HandBrakeRun run;
        try
        {
            run = HandBrakeRun.Start(launcher, HandBrakeArgs.Build(p.HandBrake, p.Source, p.Temp), Cfg.LowPriority,
                (pct, eta) => _board.Set(s => { s.Progress = pct; if (eta != null) s.Eta = eta; }));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start HandBrake");
            Fail(p.Work, "Could not start HandBrake: " + ex.Message, p.Clock.Elapsed.TotalSeconds);
            return false;
        }

        using (run)
        {
            _log.LogInformation("Converting ({Origin}) {Src} -> {Final}", p.Work.Origin, p.Source, p.Final);
            if (job != null) _jobs.SetStatus(job.Id, "Running");
            _board.Set(s => { s.State = "Encoding"; s.Message = string.Empty; s.CurrentFile = p.Source; s.CurrentJobId = job?.Id ?? string.Empty; s.Progress = 0; s.Eta = string.Empty; s.Suspended = false; });

            string? stop;
            try { stop = await MonitorAsync(run, job, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Jellyfin is shutting down: stop the encode, keep the job queued for next start.
                await run.KillAsync().ConfigureAwait(false);
                TryDelete(p.Temp);
                if (job != null) _jobs.SetStatus(job.Id, "Queued");
                throw;
            }

            if (stop != null)
            {
                await run.KillAsync().ConfigureAwait(false);
                TryDelete(p.Temp);
                if (stop == "cancel")
                {
                    _log.LogInformation("Cancelled {Src}", p.Source);
                    if (job != null) _jobs.SetStatus(job.Id, "Cancelled");
                    _board.Rest("Idle", "Cancelled.");
                }
                else
                {
                    _log.LogInformation("Window closed; cancelling {Src}, will retry next window", p.Source);
                    if (job != null) _jobs.SetStatus(job.Id, "Queued");
                    _board.Rest("Waiting", "Window closed; cancelled current encode.");
                }

                return false;
            }

            var exit = await run.WaitAsync().ConfigureAwait(false);
            if (exit == 0 && File.Exists(p.Temp) && new FileInfo(p.Temp).Length > 100_000) return true;

            _log.LogError("HandBrake failed (exit {Code}) on {Src}. Last output:\n{Tail}", exit, p.Source, run.AllOutput());
            TryDelete(p.Temp);
            Fail(p.Work, $"HandBrake failed (exit {exit}) on {Path.GetFileName(p.Source)}: {run.LastLines(3)}".TrimEnd(' ', ':'), p.Clock.Elapsed.TotalSeconds);
            return false;
        }
    }

    /// <summary>Waits for the encode to end, freezing it while you paused or someone is watching. Returns "cancel", "window" or null (finished).</summary>
    private async Task<string?> MonitorAsync(HandBrakeRun run, Job? job, CancellationToken ct)
    {
        var suspended = false;
        while (!run.HasExited)
        {
            ct.ThrowIfCancellationRequested();
            var cfg = Cfg;

            if (job != null && _jobs.IsCancelRequested(job.Id)) return "cancel";
            if (!cfg.FinishCurrentAfterWindow && !(job?.IgnoreSchedule ?? false) && !ScheduleEvaluator.IsAllowed(DateTime.Now, cfg)) return "window";

            var byUser = _state.Paused;
            var freeze = byUser || (cfg.PauseWhileStreaming && _watcher.IsAnyoneWatching(cfg));
            if (freeze != suspended)
            {
                if (freeze) run.Suspend(); else run.Resume();
                suspended = freeze;
            }

            _board.Set(s =>
            {
                s.Suspended = suspended;
                s.State = byUser && suspended ? "Paused" : "Encoding";
                s.Message = suspended ? (byUser ? "Paused by you." : "Paused while someone is watching.") : string.Empty;
            });

            await _watcher.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Size guard + duration check. Returns false (after recording why) when the result must not be used.</summary>
    private async Task<bool> ResultAcceptableAsync(Plan p, long outSize, PluginConfiguration cfg, CancellationToken ct)
    {
        var job = p.Job;

        // A result that isn't smaller is pointless (and with "replace original" would make things worse).
        // Manual jobs that keep the original, or burn subtitles in, are deliberate and exempt.
        var guardApplies = cfg.SkipIfNotSmaller && (job == null || (p.Profile.DeleteOriginal && p.HandBrake.SubtitleMode != "burn"));
        var needed = p.SourceSize * (100 - Math.Clamp(cfg.MinSavingsPercent, 0, 95)) / 100.0;
        if (guardApplies && outSize >= needed)
        {
            TryDelete(p.Temp);
            var why = $"Kept the original: converted file ({outSize / 1048576} MB) is not smaller than the source ({p.SourceSize / 1048576} MB).";
            _log.LogInformation("{Src}: {Why}", p.Source, why);
            _state.MarkSkipped(p.Work.ItemId, p.Source);
            if (job != null) _jobs.SetStatus(job.Id, "Skipped", why);
            Record(p.Work, "Skipped", why, p.SourceSize, outSize, p.Clock.Elapsed.TotalSeconds);
            _board.Rest("Idle", why);
            return false;
        }

        // Compare the result with the source: length, audio/subtitle tracks, chapters and HDR (catches truncated or broken encodes).
        if (!cfg.VerifyDuration) return true;
        _board.Set(s => { s.State = "Verifying"; s.Message = "Checking the converted file…"; s.Progress = 100; });
        var result = await _probe.ProbeAsync(p.Temp, ct).ConfigureAwait(false);
        var bad = p.SourceInfo == null || result == null
            ? "Could not read the file details with ffprobe, so the result was not trusted (original kept). Turn off the check in Original files > After converting if ffprobe is unavailable."
            : ResultCheck.Problem(p.SourceInfo, result, p.HandBrake, Path.GetExtension(p.Final).Equals(".mp4", StringComparison.OrdinalIgnoreCase));
        if (bad == null) return true;

        TryDelete(p.Temp);
        _log.LogError("{Src}: {Why}", p.Source, bad);
        Fail(p.Work, bad, p.Clock.Elapsed.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Never swap out a file someone is watching: direct-play clients re-read the file by path, so replacing it mid-stream breaks
    /// playback. The finished temp file is kept while waiting. Returns false if the job was cancelled while waiting.
    /// </summary>
    private async Task<bool> WaitForViewersAsync(Plan p, CancellationToken ct)
    {
        if (!p.Profile.DeleteOriginal || !_watcher.IsPlaying(p.Work.ItemId)) return true;

        var job = p.Job;
        _log.LogInformation("{Src} is being played; waiting before replacing it", p.Source);
        _board.Set(s => { s.State = "Finishing"; s.Message = "Encode finished; waiting for playback of this item to stop before replacing the original."; s.Progress = 100; });
        try
        {
            while (_watcher.IsPlaying(p.Work.ItemId))
            {
                if (job != null && _jobs.IsCancelRequested(job.Id))
                {
                    TryDelete(p.Temp);
                    _jobs.SetStatus(job.Id, "Cancelled");
                    _board.Rest("Idle", "Cancelled.");
                    return false;
                }

                await _watcher.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(p.Temp);
            if (job != null) _jobs.SetStatus(job.Id, "Queued");
            throw;
        }

        return true;
    }

    /// <summary>
    /// Order matters: state is recorded before the swap so a crash between the steps can never cause a re-conversion loop, and the
    /// original is moved aside (not deleted) before the new file takes its place.
    /// </summary>
    private void Swap(Plan p, long outSize, PluginConfiguration cfg)
    {
        var saved = p.SourceSize - outSize;
        var marked = false;
        TrashEntry? held = null;
        try
        {
            if (!p.Profile.DeleteOriginal)
            {
                _state.MarkProcessed(p.Work.ItemId, p.Source, p.Final, saved); marked = true;
                File.Move(p.Temp, p.Final);
            }
            else if (p.Final == p.Source)
            {
                if (p.KeepDays > 0) held = _trash.Add(p.Source, p.KeepDays, HoldingFolder(cfg, p.Source));
                _state.MarkProcessed(p.Work.ItemId, p.Source, p.Final, saved); marked = true;
                File.Move(p.Temp, p.Final, true);
            }
            else
            {
                _state.MarkProcessed(p.Work.ItemId, p.Source, p.Final, saved); marked = true;
                File.Move(p.Temp, p.Final);
                if (p.KeepDays > 0) held = _trash.Add(p.Source, p.KeepDays, HoldingFolder(cfg, p.Source));
                else File.Delete(p.Source);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Finalising {Src} failed", p.Source);
            if (!marked) TryDelete(p.Temp); // original untouched; retried later (counts as a failure)
            Fail(p.Work, "Could not finalise output: " + ex.Message, p.Clock.Elapsed.TotalSeconds);
            return;
        }

        _log.LogInformation("Done {Final} ({Old} -> {New} MB){Held}", p.Final, p.SourceSize / 1048576, outSize / 1048576,
            held != null ? $"; original held until {held.Expires:yyyy-MM-dd}" : string.Empty);
        if (p.Job != null) _jobs.SetStatus(p.Job.Id, "Done");
        Record(p.Work, "Converted", string.Empty, p.SourceSize, outSize, p.Clock.Elapsed.TotalSeconds);
        _board.Rest("Idle", "Finished " + Path.GetFileName(p.Final));

        try { _library.QueueLibraryScan(); } catch (Exception ex) { _log.LogWarning(ex, "Library scan request failed"); }
    }

    // ------------------------------------------------------------------ helpers

    private void Fail(Work w, string why, double seconds = 0)
    {
        if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Failed", why);
        else _state.MarkFailed(w.Path);
        Record(w, "Failed", why, seconds: seconds);
        _board.Rest("Error", why);
    }

    private void Record(Work w, string result, string detail, long oldBytes = 0, long newBytes = 0, double seconds = 0) =>
        _history.Add(new HistoryEntry
        {
            Title = w.Job?.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(w.Path),
            Path = w.Path,
            Result = result,
            Detail = detail,
            OldBytes = oldBytes,
            NewBytes = newBytes,
            Seconds = seconds,
            Source = w.Job != null ? "queue" : "auto",
            JobId = w.Job?.Id ?? string.Empty
        });

    private string HoldingFolder(PluginConfiguration cfg, string src)
    {
        var roots = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var f in _library.GetVirtualFolders())
                if (f.Locations != null) roots.AddRange(f.Locations);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read library folders; holding folder will sit next to the file");
        }

        return _trash.HoldingFolderFor(cfg.TrashFolder, src, roots);
    }

    /// <summary>null = enough room; otherwise a message. The output is assumed to be at most as large as the source.</summary>
    private string? CheckSpace(PluginConfiguration cfg, string libraryDir, string src, long srcSize, bool usesHolding)
    {
        var need = srcSize + Math.Max(0, cfg.MinFreeSpaceGb) * 1024L * 1024L * 1024L;
        try
        {
            var free = new DriveInfo(libraryDir).AvailableFreeSpace;
            if (free < need)
                return $"Not enough free disk space in {libraryDir}: {free / 1048576} MB free, need {need / 1048576} MB (file + {cfg.MinFreeSpaceGb} GB spare).";
            if (usesHolding)
            {
                var root = HoldingFolder(cfg, src);
                Directory.CreateDirectory(root);
                var hfree = new DriveInfo(root).AvailableFreeSpace;
                if (hfree < need)
                    return $"Not enough free disk space in the holding folder {root}: {hfree / 1048576} MB free, need {need / 1048576} MB.";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not check free disk space; continuing");
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
