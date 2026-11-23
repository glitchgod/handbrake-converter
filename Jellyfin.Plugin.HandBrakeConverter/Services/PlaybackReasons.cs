using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>
/// One playback as Jellyfin itself described it: how it was played (direct or transcoded), the reasons Jellyfin gave for transcoding
/// (the same ones its dashboard shows, e.g. "audio codec not supported"), and what the played tracks were.
/// </summary>
public class PlaybackRecord
{
    public string Id { get; set; } = string.Empty;
    public DateTime Time { get; set; }
    public string Path { get; set; } = string.Empty;

    /// <summary>The app that played it ("Jellyfin Web", "Jellyfin Android TV"...).</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>DirectPlay, DirectStream or Transcode.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Jellyfin's transcode reasons (AudioCodecNotSupported, SubtitleCodecNotSupported...). Empty for direct play.</summary>
    public List<string> Reasons { get; set; } = new();

    public bool VideoDirect { get; set; } = true;
    public bool AudioDirect { get; set; } = true;

    public string SrcVideo { get; set; } = string.Empty;
    public int SrcHeight { get; set; }
    public bool SrcHdr { get; set; }
    public string SrcAudio { get; set; } = string.Empty;
    public int SrcAudioChannels { get; set; }

    /// <summary>Codec of the subtitle track that was switched on ("" = none).</summary>
    public string SrcSubtitle { get; set; } = string.Empty;

    /// <summary>The subtitle that was on came from a separate file next to the video.</summary>
    public bool SubtitleExternal { get; set; }

    public string DstContainer { get; set; } = string.Empty;
    public string DstVideo { get; set; } = string.Empty;
    public int DstHeight { get; set; }
    public string DstAudio { get; set; } = string.Empty;
    public int DstAudioChannels { get; set; }

    public bool HasReason(string name) => Reasons.Any(r => r.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Watches playback and keeps one <see cref="PlaybackRecord"/> per play (a new one when the way it plays changes, e.g. subtitles switched on).
/// Jellyfin's API differs between versions, so everything past the session list is read by name and missing fields are simply left empty.
/// </summary>
public sealed class PlaybackReasonWatcher : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly TranscodeStore _store;
    private readonly ILogger<PlaybackReasonWatcher> _log;
    private readonly object _lock = new();

    // session+item -> (signature of how it plays, record id, when it was recorded)
    private readonly Dictionary<string, (string Sig, string Id, DateTime At)> _last = new();
    private readonly object _captureLock = new();
    private bool _running;

    public PlaybackReasonWatcher(ISessionManager sessions, ILibraryManager library, TranscodeStore store, ILogger<PlaybackReasonWatcher> log)
    {
        _sessions = sessions;
        _library = library;
        _store = store;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _running = true;
        _sessions.PlaybackStart += (_, _) => CaptureAll();
        _sessions.PlaybackProgress += (_, _) => CaptureAll();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _running = false;
        return Task.CompletedTask;
    }

    private void CaptureAll()
    {
        if (!_running || !Monitor.TryEnter(_captureLock)) return; // another event is already reading the sessions
        try
        {
            var active = new HashSet<string>();
            foreach (var s in _sessions.Sessions.ToList())
            {
                if (s.NowPlayingItem == null) continue;
                if (s.NowPlayingItem.MediaType.ToString() == "Audio") continue;
                var key = Str(Get(s, "Id")) + "|" + s.NowPlayingItem.Id.ToString("N");
                active.Add(key);
                Capture(s, key);
            }

            lock (_lock)
                foreach (var k in _last.Keys.Where(k => !active.Contains(k)).ToList()) _last.Remove(k);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read playback sessions");
        }
        finally
        {
            Monitor.Exit(_captureLock);
        }
    }

    private void Capture(SessionInfo s, string key)
    {
        var ps = Get(s, "PlayState");
        var ti = Get(s, "TranscodingInfo");
        var method = Str(ps != null ? Get(ps, "PlayMethod") : null);
        var reasons = ti != null ? ReasonList(Get(ti, "TranscodeReasons")) : new List<string>();
        if (method.Length == 0) method = ti != null ? "Transcode" : "DirectPlay";
        var audioIdx = Int(ps != null ? Get(ps, "AudioStreamIndex") : null);
        var subIdx = Int(ps != null ? Get(ps, "SubtitleStreamIndex") : null);
        var sig = string.Join("|", method, string.Join(",", reasons), audioIdx, subIdx,
            ti != null ? Get(ti, "IsVideoDirect") : null, ti != null ? Get(ti, "IsAudioDirect") : null, ti != null ? Get(ti, "AudioCodec") : null);

        string? replaceId = null;
        lock (_lock)
        {
            if (_last.TryGetValue(key, out var prev))
            {
                if (prev.Sig == sig) return;
                // Jellyfin often settles the details in the first seconds of a play: that is still the same playback.
                if (DateTime.UtcNow - prev.At < TimeSpan.FromSeconds(90)) replaceId = prev.Id;
            }
        }

        var item = _library.GetItemById(s.NowPlayingItem!.Id);
        if (item == null || string.IsNullOrEmpty(item.Path)) return;

        var r = new PlaybackRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Time = DateTime.Now,
            Path = item.Path,
            Client = Str(Get(s, "Client")),
            Method = method,
            Reasons = reasons
        };

        if (ti != null)
        {
            r.VideoDirect = Get(ti, "IsVideoDirect") is true;
            r.AudioDirect = Get(ti, "IsAudioDirect") is true;
            r.DstContainer = Str(Get(ti, "Container")).ToLowerInvariant();
            r.DstVideo = r.VideoDirect ? string.Empty : TranscodeLog.Family(Str(Get(ti, "VideoCodec")));
            r.DstHeight = r.VideoDirect ? 0 : Int(Get(ti, "Height")) ?? 0;
            r.DstAudio = r.AudioDirect ? string.Empty : Str(Get(ti, "AudioCodec")).ToLowerInvariant();
            r.DstAudioChannels = r.AudioDirect ? 0 : Int(Get(ti, "AudioChannels")) ?? 0;
        }

        FillStreams(r, item, audioIdx, subIdx);

        lock (_lock) _last[key] = (sig, r.Id, DateTime.UtcNow);
        _store.AddPlay(r, replaceId);
    }

    /// <summary>The codecs of the video, the audio track and the subtitle track that were playing.</summary>
    private void FillStreams(PlaybackRecord r, object item, int? audioIdx, int? subIdx)
    {
        IEnumerable? streams = null;
        try { streams = Call(item, "GetMediaStreams") as IEnumerable; }
        catch (Exception ex) { _log.LogDebug(ex, "Could not read the streams of {Path}", r.Path); }
        if (streams == null) return;

        object? firstAudio = null, audio = null, sub = null;
        foreach (var st in streams)
        {
            if (st == null) continue;
            var type = Str(Get(st, "Type"));
            var index = Int(Get(st, "Index"));
            if (type == "Video" && r.SrcVideo.Length == 0)
            {
                r.SrcVideo = TranscodeLog.Family(Str(Get(st, "Codec")));
                r.SrcHeight = Int(Get(st, "Height")) ?? 0;
                var range = (Str(Get(st, "VideoRange")) + " " + Str(Get(st, "VideoRangeType"))).ToUpperInvariant();
                r.SrcHdr = range.Contains("HDR", StringComparison.Ordinal) || range.Contains("DOVI", StringComparison.Ordinal) || range.Contains("HLG", StringComparison.Ordinal);
            }
            else if (type == "Audio")
            {
                firstAudio ??= st;
                if (audioIdx != null && index == audioIdx) audio = st;
                if (audioIdx == null && Get(st, "IsDefault") is true) audio ??= st;
            }
            else if (type == "Subtitle" && subIdx is >= 0 && index == subIdx) sub = st;
        }

        audio ??= firstAudio;
        if (audio != null)
        {
            r.SrcAudio = Str(Get(audio, "Codec")).ToLowerInvariant();
            r.SrcAudioChannels = Int(Get(audio, "Channels")) ?? 0;
        }

        if (sub != null)
        {
            r.SrcSubtitle = Str(Get(sub, "Codec")).ToLowerInvariant();
            r.SubtitleExternal = Get(sub, "IsExternal") is true;
        }
    }

    /// <summary>Jellyfin's TranscodeReasons as names. Works for the flags enum (newer servers) and for a list (older ones).</summary>
    public static List<string> ReasonList(object? value)
    {
        var result = new List<string>();
        if (value == null) return result;
        IEnumerable<string> parts = value is string text ? text.Split(',')
            : value is IEnumerable list ? list.Cast<object?>().Select(x => Str(x))
            : Str(value).Split(',');
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length == 0 || t == "0" || t.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (!result.Contains(t, StringComparer.OrdinalIgnoreCase)) result.Add(t);
        }

        return result;
    }

    private static object? Get(object o, string prop)
    {
        try { return o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); }
        catch { return null; }
    }

    private static object? Call(object o, string name)
    {
        var m = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
        return m?.Invoke(o, null);
    }

    private static string Str(object? o) => o == null ? string.Empty : Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty;

    private static int? Int(object? o)
    {
        if (o == null) return null;
        try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
        catch { return null; }
    }
}
