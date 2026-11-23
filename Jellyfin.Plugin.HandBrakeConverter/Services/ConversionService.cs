using System;
using System.Collections.Generic;
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
    private readonly WatchDataMover _mover;
    private readonly ILogger<ConversionService> _log;

    // One "lane" = one conversion running at a time. Lane 0 is always on; the others work when "Parallel conversions" is above 1.
    // Each lane has its own status and its own "stop automatic" flag; the code below uses them through the lane of the running task.
    private sealed class Lane
    {
        public int Id;
        public readonly StatusBoard Board = new();
        public volatile bool StopAuto;
        public volatile string CurrentItem = string.Empty;   // library item being converted by this lane
        public volatile string CurrentPath = string.Empty;
        public volatile bool ItemRemoved;                    // Jellyfin removed that item while it was being converted
    }

    public const int MaxLanes = 4;
    private readonly Lane[] _lanes = Enumerable.Range(0, MaxLanes).Select(i => new Lane { Id = i }).ToArray();
    private readonly AsyncLocal<Lane?> _laneContext = new();
    private Lane CurrentLane => _laneContext.Value ?? _lanes[0];
    private StatusBoard _board => CurrentLane.Board;
    private bool _stopAuto { get => CurrentLane.StopAuto; set => CurrentLane.StopAuto = value; }

    // Files being converted right now (path -> bytes reserved), so two lanes never take the same file and the disk check counts them all.
    private readonly object _claimLock = new();
    private readonly Dictionary<string, long> _claims = new(StringComparer.Ordinal);

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
        WatchDataMover mover,
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
        _mover = mover;
        _log = log;
        _library.ItemRemoved += OnItemRemoved;
    }

    /// <summary>When the item being converted is removed from the library, flag its lane so the conversion stops and cleans up.</summary>
    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (item == null) return;
        var id = item.Id.ToString("N");
        var path = item.Path;
        foreach (var lane in _lanes)
        {
            var cur = lane.CurrentPath;
            if (cur.Length == 0) continue;
            var sameItem = string.Equals(lane.CurrentItem, id, StringComparison.OrdinalIgnoreCase);
            var parent = !string.IsNullOrEmpty(path) && cur.StartsWith(path.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (sameItem || parent) lane.ItemRemoved = true;
        }
    }

    private void WatchItem(Plan p)
    {
        var lane = CurrentLane;
        lane.ItemRemoved = false;
        lane.CurrentItem = p.Work.ItemId;
        lane.CurrentPath = p.Source;
    }

    private void UnwatchItem()
    {
        var lane = CurrentLane;
        lane.CurrentPath = string.Empty;
        lane.CurrentItem = string.Empty;
        lane.ItemRemoved = false;
    }

    private static PluginConfiguration Cfg => Plugin.Instance!.Configuration;

    /// <summary>The status to show: the first lane that is converting, otherwise lane 0.</summary>
    public ConversionStatus Snapshot()
    {
        var s = _lanes.Select(l => l.Board.Snapshot()).FirstOrDefault(x => x.CurrentFile.Length > 0) ?? _lanes[0].Board.Snapshot();
        s.UserPaused = _state.Paused;
        return s;
    }

    /// <summary>Every other conversion running at the same time as the one <see cref="Snapshot"/> returns.</summary>
    public List<ConversionStatus> OtherSnapshots()
    {
        var busy = _lanes.Select(l => l.Board.Snapshot()).Where(x => x.CurrentFile.Length > 0).ToList();
        return busy.Skip(1).ToList();
    }

    /// <summary>One queue entry per Auto-convert library that has files waiting (see <see cref="JobStore.SyncAuto"/>).</summary>
    internal void SyncAutoEntries(PluginConfiguration cfg)
    {
        // Only "is anything waiting" per library (cheap); the library order in the settings is the default order of new entries.
        var withPending = cfg.Libraries.Where(l => l.AutoProcess && _backlog.FindPending(cfg, 1, l.LibraryId).Count > 0).Select(l => (l.LibraryId, l.Name)).ToList();
        _jobs.SyncAuto(withPending);
    }

    /// <summary>
    /// Stops the automatic conversion that is running right now (it is picked up again later) so queued jobs can start. Returns false
    /// when no automatic conversion is running.
    /// </summary>
    public bool StopAutomatic()
    {
        foreach (var lane in _lanes)
        {
            var s = lane.Board.Snapshot();
            if (s.CurrentFile.Length == 0 || s.CurrentJobId.Length > 0) continue;
            lane.StopAuto = true;
            _watcher.Wake();
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ repair of giant subtitles made by older versions

    /// <summary>Progress and result of Tools > Repair giant subtitles.</summary>
    public sealed class GiantScan
    {
        public bool Running { get; set; }
        public int Checked { get; set; }
        public int Total { get; set; }
        public List<GiantFile> Found { get; set; } = new();
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>A video whose subtitles are drawn giant by some players, and why.</summary>
    public sealed record GiantFile(string Path, string Why);

    private readonly object _giantLock = new();
    private GiantScan _giant = new();

    public GiantScan GiantStatus()
    {
        lock (_giantLock) return new GiantScan { Running = _giant.Running, Checked = _giant.Checked, Total = _giant.Total, Found = _giant.Found.ToList(), Message = _giant.Message };
    }

    /// <summary>
    /// Looks through every MP4 and MKV in the libraries for subtitles older versions made giant: MOV_TEXT that keeps a styled track's
    /// font size on a much smaller canvas (a line can fill a quarter of the screen), and SRT, inside the file or next to it, carrying
    /// font size tags. Runs in the background.
    /// </summary>
    public bool StartGiantScan()
    {
        lock (_giantLock)
        {
            if (_giant.Running) return false;
            _giant = new GiantScan { Running = true, Message = "Listing video files…" };
        }

        _ = Task.Run(GiantScanAsync);
        return true;
    }

    private async Task GiantScanAsync()
    {
        try
        {
            var files = LibraryVideoFiles();
            lock (_giantLock) { _giant.Total = files.Count; _giant.Message = "Checking the subtitles…"; }
            foreach (var f in files)
            {
                try
                {
                    var why = new List<string>();
                    var info = await _probe.ProbeAsync(f, CancellationToken.None).ConfigureAwait(false);
                    if (info?.Subs is { Count: > 0 })
                    {
                        info = await MeasureTextAsync(f, info, CancellationToken.None).ConfigureAwait(false);
                        var worst = info.Subs!.Select(s => s.TextScale).DefaultIfEmpty(0).Max();
                        if (worst > 0.1) why.Add($"MOV_TEXT lines {worst:P0} of the picture height");
                        var tagged = info.Subs!.Count(s => s.SizeTags);
                        if (tagged > 0) why.Add($"{tagged} SRT track(s) with font size tags");
                    }

                    var tagged2 = TaggedSidecars(f);
                    if (tagged2.Count > 0) why.Add($"{tagged2.Count} .srt file(s) with font size tags");
                    if (why.Count > 0) lock (_giantLock) _giant.Found.Add(new GiantFile(f, string.Join(", ", why)));
                }
                catch (Exception ex) { _log.LogDebug(ex, "Could not check the subtitles of {File}", f); }

                lock (_giantLock) _giant.Checked++;
            }

            lock (_giantLock) _giant.Message = _giant.Found.Count == 0 ? "No giant subtitles found." : $"{_giant.Found.Count} file(s) with giant subtitles.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Giant subtitle scan failed");
            lock (_giantLock) _giant.Message = "The scan stopped: " + ex.Message;
        }
        finally
        {
            lock (_giantLock) _giant.Running = false;
        }
    }

    /// <summary>The .srt files next to a video that carry font size tags.</summary>
    private static List<string> TaggedSidecars(string video)
    {
        var result = new List<string>();
        foreach (var s in SubtitleSidecars.FilesNextTo(video).Where(x => x.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)))
        {
            try { if (SubtitleCheck.HasSizeTags(File.ReadAllText(s))) result.Add(s); } catch { }
        }

        return result;
    }

    /// <summary>Every MP4, M4V and MKV in the libraries, skipping hidden folders (such as the holding folder for replaced originals).</summary>
    private List<string> LibraryVideoFiles()
    {
        var roots = new List<string>();
        foreach (var f in _library.GetVirtualFolders())
            if (f.Locations != null) roots.AddRange(f.Locations);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return roots.Where(Directory.Exists).Distinct(StringComparer.Ordinal)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", options).Where(p => !Path.GetRelativePath(r, p).Split(Path.DirectorySeparatorChar).Any(seg => seg.StartsWith('.'))))
            .Where(p => IsMp4(Path.GetExtension(p)) || Path.GetExtension(p).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Queues a subtitle repair for every file the scan found. Returns how many were added.</summary>
    public int QueueGiantRepairs()
    {
        var found = GiantStatus().Found;
        var profile = System.Text.Json.JsonSerializer.Deserialize<ConversionProfile>(System.Text.Json.JsonSerializer.Serialize(Cfg.Defaults)) ?? new ConversionProfile();
        profile.DeleteOriginal = true; // the repaired file takes the place of the giant one (which is held like any replaced original)
        var jobs = found.Select(g =>
        {
            var itemId = string.Empty;
            try { itemId = _library.FindByPath(g.Path, false)?.Id.ToString("N") ?? string.Empty; } catch { }
            return new Job { Path = g.Path, ItemId = itemId, Title = Path.GetFileNameWithoutExtension(g.Path) + " (subtitle repair)", Kind = "subs", IgnoreSchedule = true, Profile = profile };
        }).ToList();
        var (added, _) = _jobs.Add(jobs);
        _watcher.Wake();
        return added;
    }

    /// <summary>
    /// A subtitle repair: the file keeps its video, audio and container; its text subtitles are rewritten by today's rules (plain text
    /// as .srt files next to an MP4, drawn by the player at its normal size). Every language is kept. Never re-encodes.
    /// </summary>
    private async Task<bool> RepairSubtitlesAsync(Work w, CancellationToken ct)
    {
        // .srt files next to the video only need their font tags removed: no ffmpeg needed.
        var cleaned = 0;
        foreach (var file in TaggedSidecars(w.Path))
        {
            try
            {
                var tmp = file + ".hbtmp";
                await File.WriteAllTextAsync(tmp, SubtitleCheck.CleanSrt(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
                FileMeta.CopyAccess(file, tmp);
                File.Move(tmp, file, true);
                cleaned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Could not clean {File}", file); }
        }

        var (hb, _) = ResolveContainer(w.Profile, w.Path);
        hb.SubtitleMode = "keep";
        hb.SubtitleLanguages = string.Empty;
        hb.SubtitleFormat = "srt";
        if (await TryFixSubtitlesAsync(w, hb, Path.GetExtension(w.Path), ct).ConfigureAwait(false)) return true;

        if (cleaned > 0)
        {
            var done = $"Removed font size tags from {cleaned} subtitle file(s) next to it; the video file was already fine.";
            if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Done");
            Record(w, "Converted", done, new FileInfo(w.Path).Length, new FileInfo(w.Path).Length, 0, Path.GetFileName(w.Path));
            _board.Rest("Idle", done);
            return true;
        }

        const string why = "Subtitles are already fine; nothing to repair.";
        if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Skipped", why);
        Record(w, "Skipped", why);
        _board.Rest("Idle", why);
        return true;
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
        public string Signature { get; init; } = string.Empty;
        public SubPlan? SubPlan { get; init; }
        public string? Ffmpeg { get; init; }
        public string LinkNote { get; init; } = string.Empty;
        public bool InPlace { get; init; }
        public TrashEntry? Held { get; set; }
        public List<string> OriginalSubFiles { get; set; } = new();
        public int[]? AudioTracks { get; init; }
        public IReadOnlyList<AudioStream>? AudioStreams { get; init; }
        public bool Deinterlace { get; init; }
        public string SidecarNote { get; set; } = string.Empty;
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

        try { CleanupStaleTemps(); } catch (Exception ex) { _log.LogWarning(ex, "Startup clean-up failed"); }

        // Originals held by an older version were renamed so Jellyfin can't list them; a scan removes the duplicates it already made.
        if (_trash.Hidden > 0)
        {
            _log.LogInformation("Hid {Count} held originals from Jellyfin; scanning libraries to remove the duplicates they caused", _trash.Hidden);
            try { _library.QueueLibraryScan(); } catch (Exception ex) { _log.LogWarning(ex, "Library scan request failed"); }
        }

        await Task.WhenAll(_lanes.Select(l => Task.Run(() => LaneLoopAsync(l, stoppingToken), stoppingToken))).ConfigureAwait(false);
    }

    private async Task LaneLoopAsync(Lane lane, CancellationToken stoppingToken)
    {
        _laneContext.Value = lane;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Lanes above the chosen number just wait (a lane that is already converting finishes its file first).
            if (lane.Id >= Math.Clamp(Cfg.ParallelJobs, 1, MaxLanes))
            {
                try { await _watcher.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

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

            if (lane.Id == 0)
            {
                try { await _mover.ApplyPendingAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "Carrying watch data over failed"); }
            }

            try { await _watcher.WaitAsync(TimeSpan.FromSeconds(didWork ? 3 : 30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <returns>true if something was processed (so the loop continues promptly).</returns>
    private async Task<bool> TickAsync(CancellationToken ct)
    {
        var cfg = Cfg;
        if (CurrentLane.Id == 0) { try { _trash.Purge(); } catch (Exception ex) { _log.LogWarning(ex, "Trash purge failed"); } }

        if (!cfg.Enabled) { _board.Rest("Disabled", "Turned off (master switch on the Overview tab)."); return false; }
        if (_state.Paused) { _board.Rest("Paused", "Paused. Press Continue on the Overview tab to resume."); return false; }

        var inWindow = ScheduleEvaluator.IsAllowed(DateTime.Now, cfg);
        SyncAutoEntries(cfg);

        // The queue order decides: walk it from the top; a manual job is the work, an Auto-convert entry is the next waiting file of its library.
        // Choosing and claiming happen together under a lock, so two lanes can never take the same file.
        Job? manual = null; Pending? auto = null;
        string? claimed = null;
        lock (_claimLock)
        {
            foreach (var e in _jobs.Entries())
            {
                if (e.IsAuto)
                {
                    if (!inWindow) continue;
                    if (_backlog.FindPending(cfg, _claims.Count + 1, e.LibraryId).FirstOrDefault(p => !_claims.ContainsKey(p.Path)) is { } found) { auto = found; break; }
                }
                else if ((inWindow || e.IgnoreSchedule) && !_claims.ContainsKey(e.Path)) { manual = e; break; }
            }

            claimed = manual?.Path ?? auto?.Path;
            if (claimed != null) _claims[claimed] = 0;
        }

        try
        {
            return await TickWorkAsync(cfg, inWindow, manual, auto, ct).ConfigureAwait(false);
        }
        finally
        {
            if (claimed != null) lock (_claimLock) _claims.Remove(claimed);
        }
    }

    private async Task<bool> TickWorkAsync(PluginConfiguration cfg, bool inWindow, Job? manual, Pending? auto, CancellationToken ct)
    {

        if (manual == null && auto == null && !inWindow)
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
        else if (auto != null) work = new Work(auto.ItemId, auto.Path, auto.Profile, null, auto.Library);

        if (work == null) { _board.Rest("Idle", "Nothing to convert."); return false; }

        // A chosen encoder this HandBrake doesn't have (e.g. NVENC without the GPU) is a settings problem, not a bad file:
        // say so once instead of failing every file.
        var encoder = work.Profile.HandBrake.Encoder;
        var available = await _caps.EncodersAsync(launcher, ct).ConfigureAwait(false);
        if (available.Count > 0 && !available.Contains(encoder, StringComparer.OrdinalIgnoreCase))
        {
            var why = $"The encoder '{encoder}' is not available in this HandBrake. Pick another under Conversion settings > Video encoder (Tools > Encoder test shows the ones that work here).";
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

        _stopAuto = false; // a stop request only ever applies to the automatic conversion that was running when it was made
        if (!File.Exists(src)) { Fail(w, "Source file not found: " + src); return true; }
        if (w.Job is { Kind: "subs" }) return await RepairSubtitlesAsync(w, ct).ConfigureAwait(false);
        if (VideoFiles.Unsupported(src, out var unsupportedWhy))
        {
            var msg = "Left alone: " + unsupportedWhy + ", which this plugin cannot convert.";
            _log.LogInformation("{Src}: {Why}", src, msg);
            _state.MarkSkipped(w.ItemId, src);
            if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Skipped", msg);
            Record(w, "Skipped", msg);
            _board.Rest("Idle", msg);
            return true;
        }


        var (hb, ext) = ResolveContainer(profile, src);
        var signature = VideoSignature(hb);

        // Already converted with these very video/audio settings (e.g. 3 of 9 episodes done by hand earlier)? Then the video is not
        // touched again. If only the subtitles are not what the settings ask for, they alone are fixed (fast, no re-encode);
        // otherwise the job finishes with nothing to do. "Convert again" (Force) overrides.
        var knownSig = _state.SignatureOf(src);
        if (w.Job is { Force: false } && _state.IsProcessed(w.ItemId, src) && (knownSig == null || knownSig == signature || knownSig == SettingsSignature(hb)))
        {
            if (await TryFixSubtitlesAsync(w, hb, ext, ct).ConfigureAwait(false)) return true;

            const string why = "Already converted with these settings; nothing to do.";
            _log.LogInformation("{Src}: {Why}", src, why);
            _jobs.SetStatus(w.Job.Id, "Skipped", why);
            Record(w, "Skipped", why);
            _board.Rest("Idle", why);
            return true;
        }

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

        var linkedHow = string.Empty;
        var isLinked = profile.DeleteOriginal && FileMeta.IsLinked(src, out linkedHow);
        if (isLinked && cfg.LinkedFiles == "skip")
        {
            var why = $"Left alone: this file is {linkedHow}. Change \"Linked files\" in Original files to convert it anyway.";
            _log.LogInformation("{Src}: {Why}", src, why);
            _state.MarkSkipped(w.ItemId, src);
            if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Skipped", why);
            Record(w, "Skipped", why);
            _board.Rest("Idle", why);
            return true;
        }

        // 1-for-1 replacement of a linked file: the converted video is written INTO the existing file, so every link to it changes together.
        // Only possible when the file keeps its name (a new extension means a new file).
        var inPlace = isLinked && cfg.LinkedFiles != "normal" && final == src;
        var size = FileMeta.Length(src);
        var keepDays = profile.DeleteOriginal ? Math.Max(0, cfg.KeepReplacedDays) : 0;
        var space = CheckSpace(cfg, dir, src, inPlace ? size * 2 : size, keepDays > 0);
        if (space != null)
        {
            _log.LogWarning("Not converting {Src}: {Why}", src, space);
            _board.Rest("Waiting", space);
            return false;
        }

        lock (_claimLock) { if (_claims.ContainsKey(src)) _claims[src] = inPlace ? size * 2 : size; }
        var info = await _probe.ProbeAsync(src, ct).ConfigureAwait(false);
        // Level 4.1 only holds up to 1080p; bigger pictures need a higher level, so leave it to x264 there.
        if (info is { Height: > 1080 } && (hb.MaxHeight == 0 || hb.MaxHeight > 1080) && hb.H264Level != "auto")
        {
            _log.LogInformation("{Src} is {H}p: not capping the H.264 level", src, info.Height);
            hb.H264Level = "auto";
        }

        var hdrNote = await HdrDecisionAsync(hb, info, launcher, ct).ConfigureAwait(false);

        // Auto-convert only: a video already in the chosen format, no taller than the cap, would cost hours to re-encode for a file that
        // is rarely smaller and never better.
        if (hdrNote == null && w.Job == null && cfg.SkipSameFormat && info != null && info.VideoCodec.Length > 0)
        {
            var target = HandBrakeArgs.Family(hb.Encoder);
            if (target.Length > 0 && TranscodeLog.Family(info.VideoCodec) == target && (hb.MaxHeight <= 0 || info.Height <= hb.MaxHeight))
                hdrNote = $"Already {TranscodeInsights.Name(target)}; re-encoding would only lose quality (convert it by hand to force it).";
        }

        if (hdrNote != null)
        {
            _log.LogInformation("{Src}: {Why}", src, hdrNote);
            _state.MarkSkipped(w.ItemId, src);
            if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Skipped", hdrNote);
            Record(w, "Skipped", hdrNote);
            _board.Rest("Idle", hdrNote);
            return true;
        }

        // A 10-bit source stays 10-bit when the encoder has a 10-bit version (8-bit would add banding to gradients like skies and dark
        // scenes). H.264 has none that plays widely, so H.264 output stays 8-bit.
        if (hb.BitDepth != "8bit" && info is { BitDepth: >= 10 } && HandBrakeArgs.TenBit(hb.Encoder) is { } tenBit && tenBit != hb.Encoder)
        {
            var encoders = await _caps.EncodersAsync(launcher, ct).ConfigureAwait(false);
            if (encoders.Count == 0 || encoders.Contains(tenBit, StringComparer.OrdinalIgnoreCase))
            {
                _log.LogInformation("{Src} is {Bits}-bit: encoding with {Encoder}", src, info.BitDepth, tenBit);
                hb.Encoder = tenBit;
            }
        }

        // Non-video extension so Jellyfin never scans a half-written file.
        // Subtitles are chosen and written by ffmpeg after the encode (HandBrake would store text subtitles as SSA/ASS and cannot pick tracks
        // by forced flag). Without ffmpeg HandBrake keeps all tracks itself.
        SubPlan? subPlan = null;
        var ffmpeg = hb.SubtitleMode == "keep" && info?.Subs is { Count: > 0 } ? _probe.FfmpegPath() : null;
        var audioTracks = AudioPlanner.Select(info?.AudioLangs, hb.AudioLanguages);
        if (ffmpeg != null)
        {
            info = await MeasureTextAsync(src, info!, ct).ConfigureAwait(false);
            subPlan = SubtitlePlanner.Make(info.Subs, hb, IsMp4(ext), KeptAudioLanguages(info, audioTracks));
        }
        var audioStreams = KeptAudio(info, audioTracks);
        var deinterlace = hb.Deinterlace == "auto" && info is { Interlaced: true };
        var plan = new Plan(w, hb, src, final, final + ".hbtmp", size, keepDays, info) { Signature = signature, SubPlan = subPlan, Ffmpeg = ffmpeg,
            InPlace = inPlace, AudioTracks = audioTracks, AudioStreams = audioStreams, Deinterlace = deinterlace,
            LinkNote = !isLinked ? string.Empty : inPlace ? $"; the file was {linkedHow}, so every link to it now holds the converted video" : $"; the file was {linkedHow}, so the other copy still holds the old data" };
        TryDelete(plan.Temp);
        _state.TrackTemp(plan.Temp);
        WatchItem(plan);
        try
        {
            return await ConvertPlanAsync(plan, w, launcher, cfg, profile, subPlan, ffmpeg, src, final, ct).ConfigureAwait(false);
        }
        finally { UnwatchItem(); _state.UntrackTemp(plan.Temp); }
    }

    private async Task<bool> ConvertPlanAsync(Plan plan, Work w, HandBrakeLocator.Launcher launcher, PluginConfiguration cfg, ConversionProfile profile, SubPlan? subPlan, string? ffmpeg, string src, string final, CancellationToken ct)
    {
        if (!await EncodeAsync(plan, launcher, ct).ConfigureAwait(false)) return true;

        var outSize = new FileInfo(plan.Temp).Length;
        if (!await ResultAcceptableAsync(plan, outSize, cfg, ct).ConfigureAwait(false)) return true;
        if (!await WaitForViewersAsync(plan, ct).ConfigureAwait(false)) return true;
        if (SourceGone(plan)) return true;

        // Separate subtitle files come from the original, so they are made before it is replaced and moved into place afterwards.
        var sidecars = new List<(string Temp, string Final)>();
        if (subPlan is { External.Count: > 0 } && ffmpeg != null)
        {
            var planned = SubtitleSidecars.Plan(final, subPlan.External);
            var (made, failed) = await ExtractSidecarsAsync(ffmpeg, plan.Source, planned, w.Job, ct).ConfigureAwait(false);
            sidecars = made;
            if (LostSubtitles(subPlan, planned, failed) is { } lost)
            {
                FinishSidecars(sidecars, false);
                TryDelete(plan.Temp);
                Fail(w, lost, plan.Clock.Elapsed.TotalSeconds);
                return true;
            }

            if (sidecars.Count > 0) plan.SidecarNote = $"; {sidecars.Count} subtitle file(s) written next to it";
        }

        if (profile.DeleteOriginal) plan.OriginalSubFiles = SubtitleSidecars.FilesNextTo(src);
        Swap(plan, outSize, cfg);
        var swapped = File.Exists(plan.Final) && !File.Exists(plan.Temp);
        FinishSidecars(sidecars, swapped);
        if (swapped) AttachRelatedSubtitles(plan, sidecars.Where(x => File.Exists(x.Final)).Select(x => x.Final));
        if (swapped && !profile.DeleteOriginal) CopySubtitleFiles(plan.Source, plan.Final);
        return true;
    }

    /// <summary>
    /// HDR / Dolby Vision handling. Returns why the file must be left alone, or null to go ahead (in "keep" mode the settings are
    /// switched to the 10-bit encoder so the HDR information survives).
    /// </summary>
    private async Task<string?> HdrDecisionAsync(HandBrakeSettings hb, MediaInfo? info, HandBrakeLocator.Launcher launcher, CancellationToken ct)
    {
        if (info == null || !(info.IsHdr || info.DolbyVision) || hb.HdrMode == "ignore") return null;
        var kind = info.DolbyVision ? "a Dolby Vision" : "an HDR";
        if (hb.HdrMode != "keep") return $"Left alone: this is {kind} video and HDR videos are set to be skipped (Conversion settings > Video > HDR videos).";
        if (info.DolbyVision) return "Left alone: Dolby Vision can't be kept by a re-encode (change 'HDR videos' to convert it anyway).";

        var tenBit = HandBrakeArgs.TenBit(hb.Encoder);
        if (tenBit == null) return $"Left alone: {hb.Encoder} can't keep HDR (it has no 10-bit mode). Pick an H.265 or AV1 encoder, or change 'HDR videos'.";
        var available = await _caps.EncodersAsync(launcher, ct).ConfigureAwait(false);
        if (available.Count > 0 && !available.Contains(tenBit, StringComparer.OrdinalIgnoreCase))
            return $"Left alone: the 10-bit encoder '{tenBit}' needed to keep HDR is not available in this HandBrake.";
        hb.Encoder = tenBit;
        return null;
    }

    /// <summary>The audio tracks that will be encoded, in HandBrake's order; null when ffprobe's view doesn't line up with the file.</summary>
    private static IReadOnlyList<AudioStream>? KeptAudio(MediaInfo? info, int[]? audioTracks)
    {
        if (info?.Audio is not { Count: > 0 } all || all.Count != info.AudioTracks) return null;
        if (audioTracks == null) return all;
        return audioTracks.All(n => n >= 1 && n <= all.Count) ? audioTracks.Select(n => all[n - 1]).ToList() : null;
    }

    /// <summary>Languages of the audio tracks the converted file keeps; null when unknown.</summary>
    private static IReadOnlyList<string>? KeptAudioLanguages(MediaInfo? info, int[]? audioTracks)
    {
        if (info?.AudioLangs is not { Count: > 0 } all) return null;
        if (audioTracks == null) return all;
        return audioTracks.Where(n => n >= 1 && n <= all.Count).Select(n => all[n - 1]).ToList();
    }

    /// <summary>
    /// Container: "same as source" (and always when replacing a file) keeps the exact extension, so Jellyfin sees the same item
    /// (watch history, favourites and metadata survive). Formats MKV/MP4 can't stand in for (AVI, TS, ...) become MKV.
    /// </summary>
    internal static (HandBrakeSettings Settings, string Extension) ResolveContainer(ConversionProfile profile, string src)
    {
        var hb = profile.HandBrake.Clone();
        var srcExt = Path.GetExtension(src);
        var low = srcExt.ToLowerInvariant();
        var wanted = (hb.Container ?? "source").ToLowerInvariant();

        var keepable = low is ".mkv" or ".mp4" or ".m4v";
        var alreadyThatType = (wanted == "mkv" && low == ".mkv") || (wanted == "mp4" && low is ".mp4" or ".m4v");
        var switching = profile.DeleteOriginal && profile.ChangeExtension && wanted != "source" && !alreadyThatType;
        if (keepable && !switching && (profile.DeleteOriginal || wanted == "source"))
        {
            hb.Container = low == ".mkv" ? "mkv" : "mp4";
            return (hb, srcExt);
        }

        hb.Container = wanted == "mp4" ? "mp4" : "mkv";
        return (hb, HandBrakeArgs.Extension(hb));
    }

    /// <summary>true when replacing this file will change its extension (so Jellyfin lists the result as a new item).</summary>
    internal static bool ChangesExtension(ConversionProfile profile, string src) =>
        profile.DeleteOriginal && !string.Equals(ResolveContainer(profile, src).Extension, Path.GetExtension(src), StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs HandBrake and watches it. Returns true when the temp file is ready; otherwise the outcome has been recorded.</summary>
    private async Task<bool> EncodeAsync(Plan p, HandBrakeLocator.Launcher launcher, CancellationToken ct)
    {
        var job = p.Job;
        HandBrakeRun run;
        try
        {
            run = HandBrakeRun.Start(launcher, HandBrakeArgs.Build(p.HandBrake, p.Source, p.Temp, noSubtitles: p.SubPlan != null, audioTracks: p.AudioTracks, deinterlace: p.Deinterlace, audioStreams: p.AudioStreams), Cfg.LowPriority,
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
            _board.Set(s => { s.State = "Encoding"; s.Message = string.Empty; s.CurrentFile = p.Source; s.CurrentJobId = job?.Id ?? string.Empty; s.Progress = 0; s.Eta = string.Empty; s.Suspended = false; s.Origin = p.Work.Origin; });

            string? stop;
            try { stop = await MonitorAsync(run, job, p.Source, ct).ConfigureAwait(false); }
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
                if (stop == "gone") SourceGone(p);
                else if (stop == "autostop")
                {
                    _stopAuto = false;
                    _log.LogInformation("Automatic conversion of {Src} stopped to let queued jobs run", p.Source);
                    Record(p.Work, "Skipped", "Stopped so your queued job could run; it will be picked up again afterwards", seconds: p.Clock.Elapsed.TotalSeconds);
                    _board.Rest("Idle", "Stopped the automatic conversion; starting your queued job.");
                }
                else if (stop == "stall")
                {
                    _log.LogError("HandBrake printed nothing for {Min} minutes on {Src}; stopped it", Cfg.StallMinutes, p.Source);
                    Fail(p.Work, $"HandBrake stopped responding (no output for {Cfg.StallMinutes} minutes), so it was stopped. The original was not touched.", p.Clock.Elapsed.TotalSeconds);
                }
                else if (stop == "cancel")
                {
                    _log.LogInformation("Cancelled {Src}", p.Source);
                    if (job != null) _jobs.SetStatus(job.Id, "Cancelled");
                    Record(p.Work, "Skipped", "Stopped by you", seconds: p.Clock.Elapsed.TotalSeconds);
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
            if (exit == 0 && File.Exists(p.Temp) && new FileInfo(p.Temp).Length > 100_000)
                return p.SubPlan is { Keep.Count: > 0 } ? await AddSubtitlesAsync(p, ct).ConfigureAwait(false) : true;

            _log.LogError("HandBrake failed (exit {Code}) on {Src}. Last output:\n{Tail}", exit, p.Source, run.AllOutput());
            TryDelete(p.Temp);
            Fail(p.Work, $"HandBrake failed (exit {exit}) on {Path.GetFileName(p.Source)}: {run.LastLines(3)}".TrimEnd(' ', ':'), p.Clock.Elapsed.TotalSeconds);
            return false;
        }
    }

    /// <summary>Adds the planned subtitle tracks from the original to the encoded file (ffmpeg, copying everything else as it is).</summary>
    private async Task<bool> AddSubtitlesAsync(Plan p, CancellationToken ct)
    {
        var mux = p.Temp + ".sub";
        _board.Set(s => { s.State = "Encoding"; s.Message = "Adding subtitles…"; s.Progress = 100; });
        var (clean, cleanDir) = await CleanSrtTracksAsync(p.Ffmpeg!, p.Source, p.SubPlan!, p.Job, ct).ConfigureAwait(false);
        var (ok, tail) = await RunFfmpegAsync(p.Ffmpeg!, SubtitleMux.Args(p.Temp, p.Source, p.SubPlan!, IsMp4(Path.GetExtension(p.Final)), mux, clean), p.Job, ct).ConfigureAwait(false);
        try { cleanDir?.Delete(true); } catch { }
        if (ok && File.Exists(mux) && new FileInfo(mux).Length > 100_000)
        {
            File.Move(mux, p.Temp, true);
            return true;
        }

        TryDelete(mux);
        TryDelete(p.Temp);
        if (SourceGone(p)) return false;
        if (p.Job != null && _jobs.IsCancelRequested(p.Job.Id))
        {
            _jobs.SetStatus(p.Job.Id, "Cancelled");
            Record(p.Work, "Skipped", "Stopped by you", seconds: p.Clock.Elapsed.TotalSeconds);
            _board.Rest("Idle", "Cancelled.");
            return false;
        }

        if (p.Job == null && tail == "stopped")
        {
            Record(p.Work, "Skipped", "Stopped so your queued job could run; it will be picked up again afterwards", seconds: p.Clock.Elapsed.TotalSeconds);
            _board.Rest("Idle", "Stopped the automatic conversion; starting your queued job.");
            return false;
        }

        Fail(p.Work, "Could not add the subtitles (ffmpeg): " + tail, p.Clock.Elapsed.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Compares every subtitle track kept inside <paramref name="result"/> with the same track in <paramref name="source"/>: styled tracks
    /// must keep their canvas size, every style (font, size, colours, position) and every line; text tracks every line at the same time;
    /// copied PGS pictures their exact size. null = all the same; otherwise what changed (the result is then not used).
    /// </summary>
    private async Task<string?> CheckSubtitlesAsync(string ffmpeg, string source, string result, SubPlan plan, Job? job, CancellationToken ct)
    {
        var items = plan.Keep.Select((t, i) => (Track: t, Out: i, Format: SubtitleCheck.DumpFormat(t.Codec, plan.OutCodecs[i]))).Where(x => x.Format.Length > 0).ToList();
        if (items.Count == 0) return null;
        _board.Set(s => { s.State = "Verifying"; s.Message = "Checking the subtitles…"; s.Progress = 100; });
        DirectoryInfo? dir = null;
        try
        {
            dir = Directory.CreateTempSubdirectory("hbconv-subs-");
            var folder = dir.FullName;
            string P(string side, int i, string f) => Path.Combine(folder, $"{side}{i}.{f}");
            var (okA, tailA) = await RunFfmpegAsync(ffmpeg, SubtitleCheck.DumpArgs(source, items.Select(x => (x.Track.Index, x.Format, P("a", x.Out, x.Format)))), job, ct).ConfigureAwait(false);
            var (okB, tailB) = await RunFfmpegAsync(ffmpeg, SubtitleCheck.DumpArgs(result, items.Select(x => (x.Out, x.Format, P("b", x.Out, x.Format)))), job, ct).ConfigureAwait(false);
            if (!okA) { _log.LogWarning("Could not read the original's subtitles to compare them: {Why}", tailA); return null; } // can't compare: the track count check still applies
            if (!okB) return "Could not read the subtitles of the converted file (" + tailB + "); discarded it and kept the original.";

            foreach (var x in items)
            {
                var a = P("a", x.Out, x.Format);
                var b = P("b", x.Out, x.Format);
                if (!File.Exists(a)) continue;
                if (!File.Exists(b)) return $"Subtitle track {x.Out + 1} ({x.Track.Language}) is missing from the converted file; discarded it and kept the original.";
                string? why = x.Format switch
                {
                    "ass" => SubtitleCheck.CompareAss(SubtitleCheck.ParseAss(await File.ReadAllTextAsync(a, ct).ConfigureAwait(false)), SubtitleCheck.ParseAss(await File.ReadAllTextAsync(b, ct).ConfigureAwait(false))),
                    "srt" => SubtitleCheck.CompareText(SubtitleCheck.ParseSrt(await File.ReadAllTextAsync(a, ct).ConfigureAwait(false)), SubtitleCheck.ParseSrt(await File.ReadAllTextAsync(b, ct).ConfigureAwait(false))),
                    _ => new FileInfo(a).Length == new FileInfo(b).Length ? null : "its pictures are not the same as the original's"
                };
                if (why != null)
                    return $"Subtitle track {x.Out + 1} ({(x.Track.Language.Length > 0 ? x.Track.Language : "und")}, {x.Track.Codec.ToUpperInvariant()}) doesn't match the original: {why}. Discarded it and kept the original.";
            }

            return null;
        }
        finally
        {
            try { dir?.Delete(true); } catch { }
        }
    }

    /// <summary>
    /// Reads the look of the given text tracks from their first seconds: styled tracks as they are, MOV_TEXT decoded the way players and
    /// Jellyfin draw it. Runs on its own (not through the conversion lane), at most 2 minutes.
    /// </summary>
    private async Task<Dictionary<int, AssLook>> ReadLooksAsync(string ffmpeg, string path, IEnumerable<SubStream> tracks, CancellationToken ct)
    {
        var heads = await ReadHeadsAsync(ffmpeg, path, tracks.Select(t => (t, SubtitlePlanner.IsStyled(t.Codec) ? "ass" : "assx")), 30, ct).ConfigureAwait(false);
        return heads.ToDictionary(h => h.Key, h => SubtitleCheck.ParseAss(h.Value));
    }

    /// <summary>The first <paramref name="seconds"/> of each given subtitle track as text, in the given dump format (see SubtitleCheck.DumpArgs).</summary>
    private async Task<Dictionary<int, string>> ReadHeadsAsync(string ffmpeg, string path, IEnumerable<(SubStream Track, string Format)> tracks, double seconds, CancellationToken ct)
    {
        var looks = new Dictionary<int, string>();
        var list = tracks.ToList();
        if (list.Count == 0) return looks;
        DirectoryInfo? dir = null;
        try
        {
            dir = Directory.CreateTempSubdirectory("hbconv-look-");
            var folder = dir.FullName;
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in SubtitleCheck.DumpArgs(path, list.Select(t => (t.Track.Index, t.Format, Path.Combine(folder, t.Track.Index + ".sub"))), seconds: seconds))
                psi.ArgumentList.Add(arg);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return looks;
            _ = proc.StandardError.ReadToEndAsync(ct);
            _ = proc.StandardOutput.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            try { await proc.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } if (ct.IsCancellationRequested) throw; return looks; }
            if (proc.ExitCode != 0) return looks;
            foreach (var t in list)
            {
                var f = Path.Combine(folder, t.Track.Index + ".sub");
                if (File.Exists(f)) looks[t.Track.Index] = await File.ReadAllTextAsync(f, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Could not read subtitle styles of {Path}", path); }
        finally { try { dir?.Delete(true); } catch { } }
        return looks;
    }

    /// <summary>
    /// Measures how big each MOV_TEXT track is drawn (<see cref="SubStream.TextScale"/>; only reported, never decides anything) and finds
    /// SRT tracks carrying font size tags from older versions (<see cref="SubStream.SizeTags"/>; those are rewritten without the tags).
    /// </summary>
    private async Task<MediaInfo> MeasureTextAsync(string path, MediaInfo info, CancellationToken ct)
    {
        static bool IsSrt(string c) => c.Equals("subrip", StringComparison.OrdinalIgnoreCase) || c.Equals("srt", StringComparison.OrdinalIgnoreCase);
        var tracks = info.Subs?.Where(t => SubtitlePlanner.IsMovText(t.Codec) || IsSrt(t.Codec)).ToList();
        var ffmpeg = _probe.FfmpegPath();
        if (tracks is not { Count: > 0 } || ffmpeg == null) return info;
        // Ten minutes is enough to meet the first lines of nearly any track; only the first part of the file is read.
        var heads = await ReadHeadsAsync(ffmpeg, path, tracks.Select(t => (t, IsSrt(t.Codec) ? "srt" : "assx")), 600, ct).ConfigureAwait(false);
        if (heads.Count == 0) return info;
        return info with
        {
            Subs = info.Subs!.Select(t => !heads.TryGetValue(t.Index, out var h) ? t
                : IsSrt(t.Codec) ? t with { SizeTags = SubtitleCheck.HasSizeTags(h) }
                : t with { TextScale = SubtitleCheck.Scale(SubtitleCheck.ParseAss(h)) }).ToList()
        };
    }

    /// <summary>
    /// For every track the plan writes as SRT inside the file (converted from another format, or SRT with font tags): the track as an
    /// SRT file with its font tags removed, to be muxed in its place. Tracks that fail fall back to the normal conversion.
    /// </summary>
    private async Task<(Dictionary<int, string> Files, DirectoryInfo? Dir)> CleanSrtTracksAsync(string ffmpeg, string source, SubPlan plan, Job? job, CancellationToken ct)
    {
        var files = new Dictionary<int, string>();
        var wanted = Enumerable.Range(0, plan.Keep.Count).Where(i => plan.OutCodecs[i] == "srt").ToList();
        if (wanted.Count == 0) return (files, null);
        var dir = Directory.CreateTempSubdirectory("hbconv-srt-");
        var (ok, tail) = await RunFfmpegAsync(ffmpeg, SubtitleCheck.DumpArgs(source, wanted.Select(i => (plan.Keep[i].Index, "srt", Path.Combine(dir.FullName, i + ".srt")))), job, ct).ConfigureAwait(false);
        if (!ok) { _log.LogWarning("Could not prepare clean SRT tracks: {Why}", tail); return (files, dir); }
        foreach (var i in wanted)
        {
            var f = Path.Combine(dir.FullName, i + ".srt");
            if (!File.Exists(f) || new FileInfo(f).Length == 0) continue;
            await File.WriteAllTextAsync(f, SubtitleCheck.CleanSrt(await File.ReadAllTextAsync(f, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            files[i] = f;
        }

        return (files, dir);
    }

    /// <summary>
    /// Tools > Check a title: the video, every audio track and every subtitle track of <paramref name="path"/> (with styles, canvas size,
    /// font sizes and fonts), and what <paramref name="profile"/> would make of each, including whether the subtitles keep their look.
    /// Reads only; changes nothing.
    /// </summary>
    public async Task<TitleReport> CheckTitleAsync(string path, ConversionProfile profile, CancellationToken ct)
    {
        var info = await _probe.ProbeAsync(path, ct).ConfigureAwait(false);
        if (info == null) return new TitleReport(Path.GetFileName(path), string.Empty, string.Empty, string.Empty, Array.Empty<string>(), string.Empty, Array.Empty<SubtitleReportTrack>(), Array.Empty<string>(), "Could not read the file with ffprobe.");
        var (hb, ext) = ResolveContainer(profile, path);
        var mp4 = IsMp4(ext);

        // ---- video
        var range = info.DolbyVision ? "Dolby Vision" : info.IsHdr ? "HDR" : "SDR";
        var video = $"{TranscodeInsights.Name(TranscodeLog.Family(info.VideoCodec))}, {info.Height}p, {info.BitDepth}-bit, {range}{(info.Interlaced ? ", interlaced" : string.Empty)}";
        string videoAfter;
        if (info.DolbyVision && hb.HdrMode != "ignore") videoAfter = "Left alone: Dolby Vision files are skipped";
        else if (info.IsHdr && hb.HdrMode == "skip") videoAfter = "Left alone: HDR files are skipped with these settings";
        else
        {
            var encoder = hb.Encoder;
            var tenBit = HandBrakeArgs.TenBit(encoder);
            var keeps10 = tenBit != null && (info.IsHdr && hb.HdrMode == "keep" || info.BitDepth >= 10 && hb.BitDepth != "8bit");
            if (keeps10) encoder = tenBit!;
            var height = hb.MaxHeight > 0 && info.Height > hb.MaxHeight ? hb.MaxHeight : info.Height;
            var outBits = encoder.Contains("10bit", StringComparison.OrdinalIgnoreCase) || encoder.Contains("_10", StringComparison.OrdinalIgnoreCase) ? 10 : 8;
            videoAfter = $"{encoder}, quality {hb.Quality}, {height}p, {outBits}-bit{(info.IsHdr && hb.HdrMode == "keep" ? ", HDR kept" : string.Empty)}, never cropped" +
                (info.Interlaced && hb.Deinterlace != "off" ? ", interlacing cleaned up" : string.Empty);
        }

        // ---- audio
        var keptAudio = AudioPlanner.Select(info.AudioLangs, hb.AudioLanguages);
        var audio = new List<string>();
        for (var n = 1; n <= info.AudioTracks; n++)
        {
            var lang = info.AudioLangs != null && n <= info.AudioLangs.Count && info.AudioLangs[n - 1].Length > 0 ? info.AudioLangs[n - 1] : "und";
            var st = info.Audio != null && n <= info.Audio.Count ? info.Audio[n - 1] : null;
            var now = st != null ? $"{st.Codec.ToUpperInvariant()} {AudioLayout(st.Channels)}{(st.ObjectAudio ? (st.Profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase) ? " Atmos" : " DTS:X") : string.Empty)}" : "?";
            string after;
            if (keptAudio != null && !keptAudio.Contains(n)) after = "removed (language not in the list)";
            else if (st == null) after = "kept";
            else
            {
                var t = HandBrakeArgs.AudioTrack(hb, st, mp4);
                after = t.Encoder == "copy" ? "copied exactly as it is"
                    : $"{(t.Encoder == "ac3" ? "AC3" : "AAC")} {(t.Mixdown switch { "mono" => "mono", "stereo" => "stereo", "5point1" => "5.1", _ => "7.1" })}, {t.Bitrate} kbps";
            }

            audio.Add($"Track {n} · {lang} · {now} → {after}");
        }

        info = await MeasureTextAsync(path, info, ct).ConfigureAwait(false);
        var subs = info.Subs ?? Array.Empty<SubStream>();
        var plan = hb.SubtitleMode == "keep" ? SubtitlePlanner.Make(subs, hb, mp4, KeptAudioLanguages(info, AudioPlanner.Select(info.AudioLangs, hb.AudioLanguages))) : null;
        var attachments = info.Attachments ?? Array.Empty<string>();

        // The look of styled tracks comes from their header; MOV_TEXT is decoded the way players and Jellyfin draw it, to measure its size.
        var ffmpeg = _probe.FfmpegPath();
        var looks = ffmpeg == null ? new Dictionary<int, AssLook>()
            : await ReadLooksAsync(ffmpeg, path, subs.Where(t => SubtitlePlanner.IsStyled(t.Codec) || SubtitlePlanner.IsMovText(t.Codec)), ct).ConfigureAwait(false);

        static string Norm(string x) => new string(x.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var attachedNames = attachments.Select(a => Norm(Path.GetFileNameWithoutExtension(a))).ToList();

        var tracks = new List<SubtitleReportTrack>();
        foreach (var t in subs)
        {
            var isStyled = SubtitlePlanner.IsStyled(t.Codec);
            var kind = isStyled ? "Styled" : SubtitlePlanner.IsText(t.Codec) ? "Text" : "Pictures";
            looks.TryGetValue(t.Index, out var look);
            var missing = look == null || !isStyled ? new List<string>() : look.Fonts.Where(f => !attachedNames.Any(n => n.Contains(Norm(f), StringComparison.Ordinal) || Norm(f).Contains(n, StringComparison.Ordinal))).ToList();
            string outcome, result;
            if (hb.SubtitleMode == "burn") { outcome = "Burned into the picture if it is the chosen track, otherwise removed"; result = "The burned track looks exactly as it does now (drawn into the picture)"; }
            else if (hb.SubtitleMode == "none" || plan == null) { outcome = "Removed"; result = "—"; }
            else
            {
                var i = plan.Keep.ToList().IndexOf(t);
                var files = plan.External.Where(x => x.Track == t).Select(x => x.Format).ToList();
                var parts = new List<string>();
                if (i >= 0) parts.Add(plan.OutCodecs[i] switch { "copy" => "kept inside, exactly as it is", "srt" => "kept inside as SRT", "mov_text" => "kept inside as MOV_TEXT", var c => "kept inside as " + c });
                parts.AddRange(files.Select(f => f switch { "ass" => "separate .ass file, exactly as it is", "sup" => "separate .sup file, exactly as it is", _ => "separate plain .srt file" }));
                var exact = (i >= 0 && plan.OutCodecs[i] == "copy") || files.Contains("ass") || files.Contains("sup");
                if (parts.Count == 0)
                {
                    outcome = SubtitlePlanner.OutCodec(t.Codec, hb, mp4) == null && !(mp4 && (isStyled || SubtitlePlanner.IsPgs(t.Codec))) ? "Dropped: " + ext.TrimStart('.').ToUpperInvariant() + " can't hold it" : "Not kept (its language isn't in the list)";
                    result = "—";
                }
                else
                {
                    outcome = char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..];
                    result = t.Giant && !(i >= 0)
                        ? $"Its text is drawn at {t.TextScale:P0} of the picture height per line (normal is about 5%), so it shows giant when streamed. As a separate .srt file players draw it at their normal size"
                        : t.Giant ? $"Kept as it is, so still giant: drawn at {t.TextScale:P0} of the picture height per line (normal is about 5%). Choose SRT for plain text subtitles to fix it"
                        : exact
                        ? isStyled && mp4 ? "Same look; fonts can't travel with a separate file, so players use their own if they don't have them" : "Same look: identical fonts, sizes, colours and positions"
                        : isStyled ? "Same text and timing, but the styling (fonts, sizes, colours, positions) is lost" : "Same text and timing; italics and bold are kept";
                }
            }

            tracks.Add(new SubtitleReportTrack(t.Index + 1, kind, t.Codec, t.Language.Length > 0 ? t.Language : "und", t.Title, t.Forced, outcome, result, look, missing));
        }

        var note = subs.Count == 0 ? "This file has no subtitle tracks." : string.Empty;
        return new TitleReport(Path.GetFileName(path), ext.TrimStart('.').ToUpperInvariant(), video, videoAfter, audio, plan?.Summary ?? string.Empty, tracks, attachments, note);
    }

    private static string AudioLayout(int ch) => ch switch { 0 => string.Empty, 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => ch + " channels" };

    /// <summary>
    /// Writes the given subtitle tracks of <paramref name="source"/> to temporary files. Failed lists the files ffmpeg could not write
    /// (a track with no lines at all gives no file and is not a failure); see <see cref="LostSubtitles"/>.
    /// </summary>
    private async Task<(List<(string Temp, string Final)> Made, List<string> Failed)> ExtractSidecarsAsync(string ffmpeg, string source, List<(SubStream Track, string Path)> items, Job? job, CancellationToken ct)
    {
        var made = new List<(string, string)>();
        var failed = new List<string>();
        foreach (var (track, path) in items)
        {
            if (File.Exists(path)) continue; // never overwrite a subtitle file that is already there
            var tmp = path + ".hbtmp";
            TryDelete(tmp);
            _state.TrackTemp(tmp);
            var (ok, tail) = await RunFfmpegAsync(ffmpeg, SubtitleMux.ExtractArgs(source, track.Index, tmp, Path.GetExtension(path).TrimStart('.').ToLowerInvariant()), job, ct).ConfigureAwait(false);
            if (ok && File.Exists(tmp) && new FileInfo(tmp).Length > 0)
            {
                if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                    await File.WriteAllTextAsync(tmp, SubtitleCheck.CleanSrt(await File.ReadAllTextAsync(tmp, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
                FileMeta.CopyAccess(source, tmp);
                made.Add((tmp, path));
            }
            else
            {
                TryDelete(tmp);
                _state.UntrackTemp(tmp);
                if (!ok)
                {
                    failed.Add(path);
                    _log.LogWarning("Could not write subtitle file {File}: {Why}", path, tail);
                }
            }
        }

        return (made, failed);
    }

    /// <summary>
    /// A track that only exists as a separate file (it is taken out of the video) must not be lost when that file could not be written:
    /// returns why the original has to be kept, or null when nothing is lost.
    /// </summary>
    private static string? LostSubtitles(SubPlan plan, List<(SubStream Track, string Path)> planned, List<string> failed)
    {
        var lost = planned.Where(x => failed.Contains(x.Path) && !plan.Keep.Contains(x.Track)).Select(x => Path.GetFileName(x.Path)).ToList();
        return lost.Count == 0 ? null
            : $"Kept the original: could not write {string.Join(", ", lost)}, and that subtitle track would not be inside the converted file.";
    }

    private static readonly System.Text.RegularExpressions.Regex SubtitleFileTail = new(@"^(\.[A-Za-z0-9\-]{1,12}){0,3}\.(srt|ass|ssa|sub|idx|vtt|sup)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Keep-original mode: subtitle files that sit next to the original (Name.eng.srt) are copied to the converted copy's name
    /// (Name - Label.eng.srt) so the copy has them too. Never overwrites.
    /// </summary>
    private void CopySubtitleFiles(string original, string converted)
    {
        try
        {
            var dir = Path.GetDirectoryName(original)!;
            var from = Path.GetFileNameWithoutExtension(original);
            var to = Path.GetFileNameWithoutExtension(converted);
            foreach (var f in Directory.GetFiles(dir, from + ".*"))
            {
                var name = Path.GetFileName(f);
                var tail = name.Substring(from.Length);
                if (!SubtitleFileTail.IsMatch(tail)) continue;
                var dest = Path.Combine(dir, to + tail);
                if (!File.Exists(dest)) File.Copy(f, dest);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not copy subtitle files for {File}", converted); }
    }

    /// <summary>
    /// At startup: clears temporary files a crash or power cut left behind. A backup made during an in-place overwrite is put back over
    /// the file instead (the overwrite may have been cut off half-way), never just deleted.
    /// </summary>
    internal void CleanupStaleTemps()
    {
        foreach (var path in _state.TakeStaleTemps())
        {
            try
            {
                if (path.EndsWith(".hbbak", StringComparison.Ordinal))
                {
                    var target = path.Substring(0, path.Length - ".hbbak".Length);
                    if (File.Exists(path) && File.Exists(target))
                    {
                        using (var b = new FileStream(path, FileMode.Open, FileAccess.Read))
                        using (var t = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None)) { t.SetLength(0); b.CopyTo(t, 1 << 20); t.Flush(true); }
                        _log.LogWarning("Put {File} back from its backup after an interrupted in-place conversion", target);
                    }

                    TryDelete(path);
                    continue;
                }

                TryDelete(path);
                TryDelete(path + ".sub");
                _log.LogInformation("Removed leftover temporary file {File}", path);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not clean up {File}", path); }
        }
    }

    /// <summary>
    /// Replace mode: the subtitle files that sat next to the original are deleted together with the held original, but only the ones
    /// whose language the converted video still has (inside it, or as a subtitle file written for it). Anything else stays.
    /// </summary>
    private void AttachRelatedSubtitles(Plan p, IEnumerable<string> writtenFiles)
    {
        var held = p.Held;
        if (p.OriginalSubFiles.Count == 0 || (held == null && p.KeepDays > 0)) return;
        try
        {
            var langs = new List<string>();
            var finalStem = Path.GetFileNameWithoutExtension(p.Final);
            if (p.SubPlan != null) { langs.AddRange(p.SubPlan.Keep.Select(s => s.Language)); langs.AddRange(writtenFiles.Select(f => SubtitleSidecars.LanguageOf(f, finalStem))); }
            else if (p.HandBrake.SubtitleMode == "keep" && p.SourceInfo?.Subs != null) langs.AddRange(p.SourceInfo.Subs.Select(s => s.Language));
            var stem = Path.GetFileNameWithoutExtension(p.Source);
            var covered = SubtitleSidecars.Covered(p.OriginalSubFiles, stem, langs);
            if (covered.Count == 0) return;
            if (held != null) _trash.SetRelated(held.Id, covered);
            else foreach (var f in covered) TryDelete(f); // nothing is held, so they go right away with the old file
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not note the subtitle files that belong to {Src}", p.Source); }
    }

    /// <summary>Moves the finished subtitle files into place (when the video was swapped in), or throws them away.</summary>
    private void FinishSidecars(List<(string Temp, string Final)> files, bool swapped)
    {
        foreach (var (tmp, fin) in files)
        {
            try
            {
                if (swapped && !File.Exists(fin)) File.Move(tmp, fin);
                else TryDelete(tmp);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not place subtitle file {File}", fin); TryDelete(tmp); }
            _state.UntrackTemp(tmp);
        }
    }

    private static bool IsMp4(string ext) => ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs ffmpeg to completion; stopping the job (or Jellyfin shutting down) kills it.</summary>
    private async Task<(bool Ok, string Tail)> RunFfmpegAsync(string ffmpeg, System.Collections.Generic.List<string> args, Job? job, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return (false, "could not start ffmpeg");
        var err = proc.StandardError.ReadToEndAsync(ct);
        _ = proc.StandardOutput.ReadToEndAsync(ct);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!proc.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromHours(3)) { try { proc.Kill(true); } catch { } return (false, "ffmpeg ran for over 3 hours and was stopped"); }
                if (job != null && _jobs.IsCancelRequested(job.Id)) { try { proc.Kill(true); } catch { } return (false, "stopped"); }
                if (job == null && _stopAuto) { try { proc.Kill(true); } catch { } _stopAuto = false; return (false, "stopped"); }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var text = (await err.ConfigureAwait(false)).Trim();
        return (proc.ExitCode == 0, text.Length > 300 ? text[^300..] : text);
    }

    /// <summary>
    /// The file was converted before with the same video/audio settings. If its subtitles are not what the settings ask for (too many
    /// languages, wrong text format, or all of them to be removed), fix just those with a stream copy: no re-encode, a minute at most.
    /// Returns true when it handled the job (fixed it, or failed trying); false when there was nothing to fix.
    /// </summary>
    private async Task<bool> TryFixSubtitlesAsync(Work w, HandBrakeSettings hb, string ext, CancellationToken ct)
    {
        if (hb.SubtitleMode is not ("keep" or "none")) return false;
        var cfg = Cfg; var profile = w.Profile; var src = w.Path;
        var dir = Path.GetDirectoryName(src)!;
        var baseName = Path.GetFileNameWithoutExtension(src);
        var final = Path.Combine(dir, profile.DeleteOriginal ? baseName + ext : baseName + " " + profile.GetLabel() + ext);
        var target = File.Exists(final) ? final : profile.DeleteOriginal && File.Exists(src) ? src : null;
        var ffmpeg = _probe.FfmpegPath();
        if (target == null || ffmpeg == null) return false;

        var info = await _probe.ProbeAsync(target, ct).ConfigureAwait(false);
        if (info == null) return false;
        info = await MeasureTextAsync(target, info, ct).ConfigureAwait(false);
        var mp4 = IsMp4(Path.GetExtension(target));
        SubPlan? sp = hb.SubtitleMode == "none"
            ? (info.SubtitleTracks > 0 ? new SubPlan(Array.Empty<SubStream>(), Array.Empty<string>(), info.SubtitleTracks, "all subtitle tracks removed") { NeedsWork = true } : null)
            : SubtitlePlanner.Make(info.Subs, hb, mp4, info.AudioLangs);
        // Separate subtitle files that are wanted but not there yet.
        var wantedSidecars = hb.SubtitleMode == "keep" && sp != null ? SubtitleSidecars.Plan(target, sp.External) : new List<(SubStream Track, string Path)>();
        var missing = wantedSidecars.Where(x => !File.Exists(x.Path)).ToList();
        if (sp is not { NeedsWork: true } && missing.Count == 0) return false;

        if (sp is not { NeedsWork: true })
        {
            // Only the separate files are missing: write them from the video's own subtitle tracks, nothing else changes.
            if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Running");
            _board.Set(s => { s.State = "Encoding"; s.Message = "Writing subtitle files…"; s.CurrentFile = target; s.CurrentJobId = w.Job?.Id ?? string.Empty; s.Progress = 50; s.Eta = string.Empty; s.Suspended = false; s.Origin = w.Origin; });
            var (onlyFiles, _) = await ExtractSidecarsAsync(ffmpeg, target, missing, w.Job, ct).ConfigureAwait(false);
            FinishSidecars(onlyFiles, swapped: true);
            if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Done");
            Record(w, "Converted", $"Subtitle files written next to it ({onlyFiles.Count}); video and audio untouched", new FileInfo(target).Length, new FileInfo(target).Length, 0, Path.GetFileName(target));
            _board.Rest("Idle", "Wrote the subtitle files for " + Path.GetFileName(target));
            return true;
        }

        var size = FileMeta.Length(target);
        var keepDays = profile.DeleteOriginal ? Math.Max(0, cfg.KeepReplacedDays) : 0;
        var space = CheckSpace(cfg, dir, target, size, keepDays > 0);
        if (space != null) { _board.Rest("Waiting", space); return false; }

        var plan = new Plan(w, hb, target, target, target + ".hbtmp", size, keepDays, info) { Signature = VideoSignature(hb) };
        TryDelete(plan.Temp);
        _state.TrackTemp(plan.Temp);
        WatchItem(plan);
        try { return await FixSubtitlesCoreAsync(w, hb, plan, sp!, missing, ffmpeg, target, mp4, info, size, keepDays, cfg, profile, ct).ConfigureAwait(false); }
        finally { UnwatchItem(); _state.UntrackTemp(plan.Temp); }
    }

    private async Task<bool> FixSubtitlesCoreAsync(Work w, HandBrakeSettings hb, Plan plan, SubPlan sp, List<(SubStream Track, string Path)> missing, string ffmpeg, string target, bool mp4, MediaInfo info, long size, int keepDays, PluginConfiguration cfg, ConversionProfile profile, CancellationToken ct)
    {
        if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Running");
        _board.Set(s => { s.State = "Encoding"; s.Message = "Fixing subtitles only (video is not touched)…"; s.CurrentFile = target; s.CurrentJobId = w.Job?.Id ?? string.Empty; s.Progress = 50; s.Eta = string.Empty; s.Suspended = false; s.Origin = w.Origin; });
        _log.LogInformation("{Target}: fixing subtitles only ({Plan})", target, sp.Summary);

        // Separate files first: they are taken from the tracks that are about to be rewritten.
        var fixSidecars = new List<(string Temp, string Final)>();
        if (missing.Count > 0)
        {
            var (made, failed) = await ExtractSidecarsAsync(ffmpeg, target, missing, w.Job, ct).ConfigureAwait(false);
            fixSidecars = made;
            if (LostSubtitles(sp, missing, failed) is { } lost)
            {
                FinishSidecars(fixSidecars, false);
                Fail(w, lost, plan.Clock.Elapsed.TotalSeconds);
                return true;
            }
        }

        var (clean, cleanDir) = await CleanSrtTracksAsync(ffmpeg, target, sp, w.Job, ct).ConfigureAwait(false);
        var (ok, tail) = await RunFfmpegAsync(ffmpeg, SubtitleMux.Args(target, null, sp, mp4, plan.Temp, clean), w.Job, ct).ConfigureAwait(false);
        try { cleanDir?.Delete(true); } catch { }
        if (!ok || !File.Exists(plan.Temp) || new FileInfo(plan.Temp).Length < 100_000)
        {
            TryDelete(plan.Temp);
            FinishSidecars(fixSidecars, false);
            if (w.Job != null && _jobs.IsCancelRequested(w.Job.Id)) { _jobs.SetStatus(w.Job.Id, "Cancelled"); Record(w, "Skipped", "Stopped by you"); _board.Rest("Idle", "Cancelled."); }
            else Fail(w, "Could not fix the subtitles (ffmpeg): " + tail, plan.Clock.Elapsed.TotalSeconds);
            return true;
        }

        var result = await _probe.ProbeAsync(plan.Temp, ct).ConfigureAwait(false);
        var bad = result == null ? "Could not read the fixed file with ffprobe, so it was not used (the file was left as it was)."
            : ResultCheck.Problem(info, result, hb, mp4, sp.Keep.Count);
        if (bad == null && sp.Keep.Count > 0 && cfg.VerifyDuration) bad = await CheckSubtitlesAsync(ffmpeg, target, plan.Temp, sp, w.Job, ct).ConfigureAwait(false);
        if (bad != null) { TryDelete(plan.Temp); FinishSidecars(fixSidecars, false); Fail(w, bad, plan.Clock.Elapsed.TotalSeconds); return true; }

        if (!await WaitForViewersAsync(plan, ct).ConfigureAwait(false)) { FinishSidecars(fixSidecars, false); return true; }
        if (SourceGone(plan)) { FinishSidecars(fixSidecars, false); return true; }
        TrashEntry? held = null;
        FileMeta.CopyAccess(target, plan.Temp);
        try
        {
            if (profile.DeleteOriginal && keepDays > 0) held = _trash.Add(target, keepDays, HoldingFolder(cfg, target));
            File.Move(plan.Temp, target, true);
        }
        catch (Exception ex)
        {
            TryDelete(plan.Temp);
            FinishSidecars(fixSidecars, false);
            Fail(w, "Could not finalise the subtitle fix: " + ex.Message, plan.Clock.Elapsed.TotalSeconds);
            return true;
        }

        FinishSidecars(fixSidecars, true);
        var newSize = new FileInfo(target).Length;
        if (w.Job != null) _jobs.SetStatus(w.Job.Id, "Done");
        Record(w, "Converted", "Subtitles fixed, video and audio untouched: " + sp.Summary + (held != null ? $"; previous file held {keepDays} days" : string.Empty), size, newSize, plan.Clock.Elapsed.TotalSeconds, Path.GetFileName(target));
        _board.Rest("Idle", "Fixed the subtitles of " + Path.GetFileName(target));
        try { _library.QueueLibraryScan(); } catch (Exception ex) { _log.LogWarning(ex, "Library scan request failed"); }
        return true;
    }

    /// <summary>Waits for the encode to end, freezing it while you paused or someone is watching. Returns "cancel", "window" or null (finished).</summary>
    private async Task<string?> MonitorAsync(HandBrakeRun run, Job? job, string source, CancellationToken ct)
    {
        var suspended = false;
        while (!run.HasExited)
        {
            ct.ThrowIfCancellationRequested();
            var cfg = Cfg;

            if (CurrentLane.ItemRemoved || !File.Exists(source)) return "gone";
            if (job != null && _jobs.IsCancelRequested(job.Id)) return "cancel";
            if (job == null && _stopAuto) return "autostop";
            if (!cfg.FinishCurrentAfterWindow && !(job?.IgnoreSchedule ?? false) && !ScheduleEvaluator.IsAllowed(DateTime.Now, cfg)) return "window";

            var byUser = _state.Paused;
            var freeze = byUser || (cfg.PauseWhileStreaming && _watcher.IsAnyoneWatching(cfg));
            if (freeze) run.Touch();
            else if (cfg.StallMinutes > 0 && run.Idle > TimeSpan.FromMinutes(cfg.StallMinutes)) return "stall";
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
            : ResultCheck.Problem(p.SourceInfo, result, p.HandBrake, Path.GetExtension(p.Final).Equals(".mp4", StringComparison.OrdinalIgnoreCase), p.SubPlan?.Keep.Count, p.AudioTracks?.Length);
        if (bad == null && p.SubPlan is { Keep.Count: > 0 } && p.Ffmpeg != null)
            bad = await CheckSubtitlesAsync(p.Ffmpeg, p.Source, p.Temp, p.SubPlan, job, ct).ConfigureAwait(false);
        if (bad == null) return true;

        TryDelete(p.Temp);
        _log.LogError("{Src}: {Why}", p.Source, bad);
        Fail(p.Work, bad, p.Clock.Elapsed.TotalSeconds);
        return false;
    }

    /// <summary>
    /// If the original was deleted or moved while the encode ran, the result is thrown away: writing it back would bring
    /// back a video that was removed on purpose. Returns true when the source is gone.
    /// </summary>
    private bool SourceGone(Plan p)
    {
        if (File.Exists(p.Source) && !CurrentLane.ItemRemoved) return false;
        TryDelete(p.Temp);
        const string why = "The original was deleted or moved while converting (or removed from the library), so the conversion was stopped and everything it made was removed.";
        _log.LogWarning("{Src}: {Why}", p.Source, why);
        if (p.Job != null) _jobs.SetStatus(p.Job.Id, "Skipped", why);
        Record(p.Work, "Skipped", why, p.SourceSize, seconds: p.Clock.Elapsed.TotalSeconds);
        _board.Rest("Idle", why);
        return true;
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
                    Record(p.Work, "Skipped", "Stopped by you", seconds: p.Clock.Elapsed.TotalSeconds);
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
    /// Writes the converted file's bytes into the existing file (same inode), so hard links and symbolic links all see the new video.
    /// A real copy of the old file is made first (held for the usual days, or kept only until the write succeeded) and is put back if the
    /// write fails part-way, because an in-place write is not atomic. Returns the held entry, if any.
    /// </summary>
    private TrashEntry? OverwriteInPlace(Plan p, PluginConfiguration cfg)
    {
        TrashEntry? held = null;
        string backup;
        var tempBackup = false;
        if (p.KeepDays > 0)
        {
            held = _trash.Add(p.Source, p.KeepDays, HoldingFolder(cfg, p.Source), copy: true);
            backup = held.TrashedPath;
        }
        else
        {
            backup = p.Source + ".hbbak";
            _state.TrackTemp(backup);
            File.Copy(p.Source, backup, true);
            tempBackup = true;
        }

        try
        {
            using (var from = new FileStream(p.Temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var to = new FileStream(p.Source, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                to.SetLength(0);
                from.CopyTo(to, 1 << 20);
                to.Flush(true);
            }
        }
        catch
        {
            try { using var b = new FileStream(backup, FileMode.Open, FileAccess.Read); using var t = new FileStream(p.Source, FileMode.Open, FileAccess.Write, FileShare.None); t.SetLength(0); b.CopyTo(t, 1 << 20); t.Flush(true); }
            catch (Exception ex) { _log.LogError(ex, "Could not put {Src} back after a failed in-place write; the old copy is at {Backup}", p.Source, backup); tempBackup = false; }
            if (held != null) { try { _trash.DeleteNow(held.Id); } catch { } }
            if (tempBackup) { TryDelete(backup); _state.UntrackTemp(backup); }
            throw;
        }

        TryDelete(p.Temp);
        if (tempBackup) { TryDelete(backup); _state.UntrackTemp(backup); }
        return held;
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

        // The extension changes, so Jellyfin will see a new item and drop the old one's data: read it now, while the old item still exists.
        PendingMove? carry = null;
        if (p.Profile.DeleteOriginal && p.Final != p.Source && p.Profile.KeepWatchData)
            carry = _mover.Capture(p.Work.ItemId, p.Final, p.Job?.Title is { Length: > 0 } jt ? jt : Path.GetFileNameWithoutExtension(p.Source));

        FileMeta.CopyAccess(p.Source, p.Temp);
        try
        {
            if (!p.Profile.DeleteOriginal)
            {
                _state.MarkProcessed(p.Work.ItemId, p.Source, p.Final, saved, p.Signature); marked = true;
                File.Move(p.Temp, p.Final);
            }
            else if (p.Final == p.Source && p.InPlace)
            {
                held = OverwriteInPlace(p, cfg);
                _state.MarkProcessed(p.Work.ItemId, p.Source, p.Final, saved, p.Signature); marked = true;
            }
            else if (p.Final == p.Source)
            {
                if (p.KeepDays > 0) held = _trash.Add(p.Source, p.KeepDays, HoldingFolder(cfg, p.Source));
                _state.MarkProcessed(p.Work.ItemId, p.Source, p.Final, saved, p.Signature); marked = true;
                File.Move(p.Temp, p.Final, true);
            }
            else
            {
                _state.MarkProcessed(p.Work.ItemId, p.Source, p.Final, saved, p.Signature); marked = true;
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

        p.Held = held;

        _log.LogInformation("Done {Final} ({Old} -> {New} MB){Held}", p.Final, p.SourceSize / 1048576, outSize / 1048576,
            held != null ? $"; original held until {held.Expires:yyyy-MM-dd}" : string.Empty);
        if (p.Job != null) _jobs.SetStatus(p.Job.Id, "Done");
        var outcome = !p.Profile.DeleteOriginal ? "Original kept; converted copy added: " + Path.GetFileName(p.Final)
            : p.Final == p.Source ? "Original replaced in place (same file name)" + (held != null ? $"; old file held {p.KeepDays} days" : string.Empty)
            : "Original replaced by " + Path.GetFileName(p.Final) + " (new file type, so Jellyfin lists it as a new item)" + (held != null ? $"; old file held {p.KeepDays} days" : string.Empty);
        if (p.AudioTracks != null) outcome += $"; kept {p.AudioTracks.Length} of {p.SourceInfo?.AudioTracks} audio tracks";
        if (p.Deinterlace) outcome += "; interlaced picture cleaned up";
        outcome += p.LinkNote + p.SidecarNote;
        var histId = Record(p.Work, "Converted", outcome, p.SourceSize, outSize, p.Clock.Elapsed.TotalSeconds, Path.GetFileName(p.Final));
        if (carry != null) _mover.Register(carry, histId);
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

    /// <summary>Short fingerprint of everything that shapes the encoded video (HandBrake arguments minus the file names).</summary>
    internal static string SettingsSignature(HandBrakeSettings hb) => Hash(hb);

    /// <summary>
    /// Like <see cref="SettingsSignature"/> but ignoring everything about subtitle tracks (they can be fixed without re-encoding the
    /// video). Burning subtitles in changes the picture, so that mode still counts.
    /// </summary>
    internal static string VideoSignature(HandBrakeSettings hb)
    {
        var c = hb.Clone();
        if (c.SubtitleMode != "burn") c.SubtitleMode = "none";
        return Hash(c);
    }

    private static string Hash(HandBrakeSettings hb)
    {
        // "--crop 0:0:0:0" is left out so files converted before cropping was switched off still count as converted with these settings.
        var args = HandBrakeArgs.Build(hb, "in", "out");
        var crop = args.IndexOf("--crop");
        if (crop >= 0) args.RemoveRange(crop, 2);
        var text = string.Join(' ', args);
        if (!string.IsNullOrWhiteSpace(hb.AudioLanguages)) text += " |audio-languages=" + hb.AudioLanguages.Trim().ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8);
    }

    private string Record(Work w, string result, string detail, long oldBytes = 0, long newBytes = 0, double seconds = 0, string output = "")
    {
        var hb = w.Profile.HandBrake;
        var entry = new HistoryEntry
        {
            Library = w.Job != null ? string.Empty : w.Origin,
            Output = output,
            Replaced = w.Profile.DeleteOriginal,
            Encoder = hb.Encoder,
            Quality = hb.Quality,
            Preset = hb.EncoderPreset,
            Container = ResolveContainer(w.Profile, w.Path).Extension.TrimStart('.').ToUpperInvariant(),
            MaxHeight = hb.MaxHeight,
            Audio = hb.AudioMode,
            Subtitles = hb.SubtitleMode,
            Title = w.Job?.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(w.Path),
            Path = w.Path,
            Result = result,
            Detail = detail,
            OldBytes = oldBytes,
            NewBytes = newBytes,
            Seconds = seconds,
            Source = w.Job != null ? "queue" : "auto",
            JobId = w.Job?.Id ?? string.Empty
        };
        _history.Add(entry);
        return entry.Id;
    }

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
        // Other conversions running right now will each still need room for their output.
        long others; lock (_claimLock) others = _claims.Where(c => c.Key != src).Sum(c => c.Value);
        var need = srcSize + others + Math.Max(0, cfg.MinFreeSpaceGb) * 1024L * 1024L * 1024L;
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
