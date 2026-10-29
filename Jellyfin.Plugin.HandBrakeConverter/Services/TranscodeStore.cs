using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    public List<TranscodeSession> Sessions(int days)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        lock (_lock) return _data.Sessions.Where(s => s.Time >= cutoff).ToList();
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
