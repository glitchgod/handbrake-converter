using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>
/// Remembers what Jellyfin's own transcode logs say. Jellyfin deletes old logs (3 days by default), so each new log is read once and
/// its summary kept here (transcodes.json); only a few KB per playback are stored, never the log itself.
/// </summary>
public class TranscodeStore
{
    private const int KeepSessions = 5000, KeepDays = 365, HeadBytes = 64 * 1024;

    private readonly string _logDir;
    private readonly string _file;
    private readonly ILogger<TranscodeStore> _log;
    private readonly object _lock = new();
    private Data _data = new();

    public TranscodeStore(IApplicationPaths paths, StateStore state, ILogger<TranscodeStore> log)
    {
        _log = log;
        _logDir = paths.LogDirectoryPath;
        _file = Path.Combine(state.DataDir, "transcodes.json");
        try
        {
            if (File.Exists(_file)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_file)) ?? new Data();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read transcodes.json, starting empty");
        }
    }

    public string LogDirectory => _logDir;

    public class Data
    {
        public List<TranscodeSession> Sessions { get; set; } = new();

        /// <summary>How each playback was played and why Jellyfin transcoded it (from the live sessions, see PlaybackReasonWatcher).</summary>
        public List<PlaybackRecord> Plays { get; set; } = new();

        /// <summary>Logs that were read and are not video playbacks (so they aren't read again).</summary>
        public List<string> Ignored { get; set; } = new();
    }

    /// <summary>Reads any logs not seen before. Cheap when nothing is new.</summary>
    public int Scan()
    {
        if (!Directory.Exists(_logDir)) return 0;
        var added = 0;
        var changed = false;
        lock (_lock)
        {
            var seen = new HashSet<string>(_data.Sessions.Select(s => s.Id).Concat(_data.Ignored));
            foreach (var path in Directory.EnumerateFiles(_logDir, "FFmpeg.*-*.log").Where(p => IsPlaybackLog(Path.GetFileName(p))))
            {
                var name = Path.GetFileName(path);
                if (seen.Contains(name)) continue;
                try
                {
                    var info = new FileInfo(path);
                    string head;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var buf = new byte[(int)Math.Min(HeadBytes, fs.Length)];
                        var n = fs.Read(buf, 0, buf.Length);
                        head = System.Text.Encoding.UTF8.GetString(buf, 0, n);
                    }

                    var session = TranscodeLog.Parse(head, name, info.LastWriteTime);
                    if (session != null) { _data.Sessions.Add(session); added++; changed = true; }
                    else if (DateTime.Now - info.LastWriteTime > TimeSpan.FromMinutes(1)) { _data.Ignored.Add(name); changed = true; } // not still starting up
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Could not read {Log}", name);
                }
            }

            var cutoff = DateTime.Now.AddDays(-KeepDays);
            _data.Sessions = _data.Sessions.Where(s => s.Time >= cutoff).OrderByDescending(s => s.Time).Take(KeepSessions).ToList();
            if (_data.Ignored.Count > 20000) _data.Ignored.RemoveRange(0, _data.Ignored.Count - 20000);
            if (changed) Save();
        }

        return added;
    }

    // ------------------------------------------------------------------ Tools > Update suggestions (manual check)

    /// <summary>One log looked at by the manual check, and what it showed.</summary>
    public sealed record CheckedLog(string Video, DateTime Time, string Result, bool Counted);

    /// <summary>Progress and outcome of the manual check.</summary>
    public sealed class LogCheck
    {
        public bool Running { get; set; }
        public int Checked { get; set; }
        public int Total { get; set; }
        public List<CheckedLog> Logs { get; set; } = new();
        public List<Suggestion> Suggestions { get; set; } = new();
        public string Message { get; set; } = string.Empty;
    }

    private readonly object _checkLock = new();
    private LogCheck _check = new();

    public LogCheck CheckStatus()
    {
        lock (_checkLock)
            return new LogCheck { Running = _check.Running, Checked = _check.Checked, Total = _check.Total, Logs = _check.Logs.ToList(), Suggestions = _check.Suggestions.ToList(), Message = _check.Message };
    }

    /// <summary>
    /// Reads Jellyfin's newest <paramref name="limit"/> transcode logs one by one (each is one time Jellyfin converted a video while
    /// someone watched), lists what each shows, then works the suggestions out again from everything known. Runs in the background.
    /// </summary>
    public bool StartCheck(int limit)
    {
        lock (_checkLock)
        {
            if (_check.Running) return false;
            _check = new LogCheck { Running = true, Message = "Looking for logs…" };
        }

        _ = Task.Run(() => RunCheck(Math.Clamp(limit, 1, 1000)));
        return true;
    }

    private void RunCheck(int limit)
    {
        try
        {
            var logs = Directory.Exists(_logDir)
                ? new DirectoryInfo(_logDir).EnumerateFiles("FFmpeg.*-*.log").Where(f => IsPlaybackLog(f.Name)).OrderByDescending(f => f.LastWriteTime).Take(limit).ToList()
                : new List<FileInfo>();
            lock (_checkLock) { _check.Total = logs.Count; _check.Message = logs.Count == 0 ? "No playback logs found in " + _logDir : "Reading the logs…"; }

            foreach (var f in logs)
            {
                CheckedLog row;
                try
                {
                    TranscodeSession? session;
                    lock (_lock) session = _data.Sessions.FirstOrDefault(s => s.Id == f.Name);
                    if (session == null)
                    {
                        session = TranscodeLog.Parse(ReadHead(f.FullName), f.Name, f.LastWriteTime);
                        if (session != null) lock (_lock) { _data.Sessions.Add(session); Save(); }
                    }

                    row = session == null
                        ? new CheckedLog(f.Name, f.LastWriteTime, "Not a video playback (an image or audio job); not counted", false)
                        : new CheckedLog(Path.GetFileName(session.Path), session.Time, Describe(session), true);
                }
                catch (Exception ex)
                {
                    row = new CheckedLog(f.Name, f.LastWriteTime, "Could not be read: " + ex.Message, false);
                }

                lock (_checkLock) { _check.Logs.Add(row); _check.Checked++; }
            }

            var result = TranscodeInsights.Analyze(Sessions(60), Plays(60), 60);
            lock (_checkLock)
            {
                _check.Suggestions = result.Suggestions;
                var counted = _check.Logs.Count(l => l.Counted);
                _check.Message = $"Checked {_check.Checked} log(s), {counted} counted. " +
                    (result.Suggestions.Count == 0 ? "Nothing points to a change, so the dropdowns keep marking the usual defaults." : $"{result.Suggestions.Count} setting(s) now have a suggestion from your playback.");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Manual log check failed");
            lock (_checkLock) _check.Message = "The check stopped: " + ex.Message;
        }
        finally
        {
            lock (_checkLock) _check.Running = false;
        }
    }

    /// <summary>One line on what Jellyfin did in a playback: "video HEVC 2160p → H.264 1080p, audio EAC3 5.1 → AAC stereo".</summary>
    private static string Describe(TranscodeSession s)
    {
        static string Ch(int c) => c switch { 0 => string.Empty, 1 => " mono", 2 => " stereo", 6 => " 5.1", 8 => " 7.1", _ => " " + c + "ch" };
        var parts = new List<string>
        {
            s.VideoTranscoded
                ? $"video {TranscodeInsights.Name(s.SrcVideo)} {s.SrcHeight}p → {TranscodeInsights.Name(s.DstVideo)}{(s.DstHeight > 0 ? " " + s.DstHeight + "p" : string.Empty)}{(s.Hardware ? " (hardware)" : string.Empty)}"
                : "video sent as it is",
            s.AudioTranscoded
                ? $"audio {s.SrcAudio.ToUpperInvariant()}{Ch(s.SrcAudioChannels)} → {s.DstAudio.Replace("lib", string.Empty, StringComparison.Ordinal).ToUpperInvariant()}{Ch(s.DstAudioChannels)}"
                : "audio sent as it is"
        };
        if (s.BurnedSubtitles) parts.Add("subtitles burned into the picture");
        if (s.Tonemapped) parts.Add("HDR tone-mapped to SDR");
        return string.Join(", ", parts);
    }

    private static string ReadHead(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[(int)Math.Min(HeadBytes, fs.Length)];
        var n = fs.Read(buf, 0, buf.Length);
        return System.Text.Encoding.UTF8.GetString(buf, 0, n);
    }

    public List<TranscodeSession> Sessions(int days)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        lock (_lock) return _data.Sessions.Where(s => s.Time >= cutoff).ToList();
    }

    /// <summary>Adds a playback, or replaces <paramref name="replaceId"/> (the same play, with details Jellyfin settled a moment later).</summary>
    public void AddPlay(PlaybackRecord record, string? replaceId)
    {
        lock (_lock)
        {
            if (replaceId != null) _data.Plays.RemoveAll(p => p.Id == replaceId);
            _data.Plays.Add(record);
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            if (_data.Plays.Count > KeepSessions || _data.Plays[0].Time < cutoff)
                _data.Plays = _data.Plays.Where(p => p.Time >= cutoff).OrderByDescending(p => p.Time).Take(KeepSessions).OrderBy(p => p.Time).ToList();
            Save();
        }
    }

    public List<PlaybackRecord> Plays(int days)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        lock (_lock) return _data.Plays.Where(p => p.Time >= cutoff).ToList();
    }

    private static bool IsPlaybackLog(string name) => name.StartsWith("FFmpeg.Transcode-", StringComparison.Ordinal) || name.StartsWith("FFmpeg.Remux-", StringComparison.Ordinal);

    private void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save transcodes.json");
        }
    }
}
