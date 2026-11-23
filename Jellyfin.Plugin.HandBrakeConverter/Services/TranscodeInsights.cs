using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

public record CountRow(string Name, int Count);

public record TopFile(string Path, int Count, string Why);

/// <summary>The one suggested value for one dropdown (Key is the field's data-k), with the evidence behind it.</summary>
public record Suggestion(string Key, string Value, string Reason);

public class InsightsResult
{
    public int Days { get; set; }

    /// <summary>Re-encodes found in Jellyfin's ffmpeg logs (repeated starts of the same file collapsed).</summary>
    public int Sessions { get; set; }

    public int VideoTranscodes { get; set; }
    public int AudioTranscodes { get; set; }

    /// <summary>Playbacks recorded from Jellyfin's live sessions (direct plays included).</summary>
    public int Plays { get; set; }

    public int DirectPlays { get; set; }

    /// <summary>Jellyfin's own transcode reasons, most common first.</summary>
    public List<CountRow> Reasons { get; set; } = new();

    public List<CountRow> Clients { get; set; } = new();
    public List<CountRow> Routes { get; set; } = new();
    public List<TopFile> TopFiles { get; set; } = new();
    public List<Suggestion> Suggestions { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string LogDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Turns what Jellyfin did during playback (its transcode reasons per play, and its ffmpeg logs) into one suggested value per setting:
/// the format that would have let those players play the file directly.
/// </summary>
public static class TranscodeInsights
{
    /// <summary>Playbacks needed before anything is suggested: one odd playback (or one unusual player) should not steer the whole library.</summary>
    private const int MinPlays = 2;

    /// <summary>Video re-encodes needed before the video encoder or resolution is suggested.</summary>
    private const int MinVideo = 2;

    private static readonly int[] Heights = { 2160, 1440, 1080, 720, 576, 480 };
    private static readonly HashSet<string> PictureSubs = new(StringComparer.OrdinalIgnoreCase) { "pgs", "pgssub", "hdmv_pgs_subtitle", "dvdsub", "dvd_subtitle", "dvbsub", "dvb_subtitle", "vobsub", "xsub" };

    public static string Name(string family) => family switch
    {
        "h264" => "H.264", "hevc" => "HEVC", "av1" => "AV1", "vp9" => "VP9", "mpeg2video" => "MPEG-2", "mpeg4" => "MPEG-4", "vc1" => "VC-1", "" => "?", _ => family.ToUpperInvariant()
    };

    /// <summary>"SubtitleCodecNotSupported" -> "Subtitle codec not supported".</summary>
    public static string Friendly(string reason)
    {
        var words = Regex.Replace(reason, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();
        return words.Length == 0 ? reason : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Channels(int ch) => ch switch { 0 => string.Empty, 1 => " mono", 2 => " stereo", 6 => " 5.1", 8 => " 7.1", _ => " " + ch + "ch" };

    private static string AudioName(string codec, int ch)
    {
        var c = codec.ToLowerInvariant() switch { "libmp3lame" or "mp3" => "MP3", "libfdk_aac" or "aac" or "aac_at" => "AAC", "libopus" or "opus" => "Opus", "" => "?", var x => x.StartsWith("lib", StringComparison.Ordinal) ? x[3..].ToUpperInvariant() : x.ToUpperInvariant() };
        return c + Channels(ch);
    }

    /// <summary>
    /// The audio option that plays on the most players while keeping the source's channel layout: AAC (the most widely supported codec)
    /// at the same number of channels. Surround is never suggested away, because a downmix can't be undone and other players use it.
    /// </summary>
    private static string AudioMode(int srcChannels)
    {
        if (srcChannels is > 0 and <= 2) return "aac";
        if (srcChannels > 6) return "aac_71";
        return "aac_51"; // 5.1, or unknown (most re-encoded tracks are surround)
    }

    private static readonly Dictionary<string, string> AudioLabels = new() { ["aac"] = "AAC stereo", ["aac_51"] = "AAC 5.1", ["ac3_51"] = "AC3 5.1", ["aac_71"] = "AAC 7.1" };

    private static string Most<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.Select(key).Where(k => k.Length > 0).GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? string.Empty;

    private static string OnClient(IEnumerable<PlaybackRecord> plays)
    {
        var c = Most(plays, p => p.Client);
        return c.Length > 0 ? " on " + c : string.Empty;
    }

    public static InsightsResult Analyze(IEnumerable<TranscodeSession> all, IEnumerable<PlaybackRecord> allPlays, int days)
    {
        var r = new InsightsResult { Days = days };

        // Seeking or resuming starts a new log for the same playback; count one playback per file per 10 minutes.
        var sessions = new List<TranscodeSession>();
        foreach (var s in all.OrderBy(x => x.Time))
        {
            var prev = sessions.LastOrDefault(x => x.Path == s.Path);
            if (prev != null && s.Time - prev.Time < TimeSpan.FromMinutes(10)) sessions.Remove(prev);
            sessions.Add(s);
        }

        var plays = new List<PlaybackRecord>();
        foreach (var p in allPlays.OrderBy(x => x.Time))
        {
            var prev = plays.LastOrDefault(x => x.Path == p.Path);
            if (prev != null && p.Time - prev.Time < TimeSpan.FromMinutes(10) && string.Join(",", prev.Reasons) == string.Join(",", p.Reasons)) plays.Remove(prev);
            plays.Add(p);
        }

        var video = sessions.Where(s => s.VideoTranscoded).ToList();
        r.Sessions = sessions.Count;
        r.VideoTranscodes = video.Count;
        r.AudioTranscodes = sessions.Count(s => s.AudioTranscoded);
        r.Plays = plays.Count;
        r.DirectPlays = plays.Count(p => p.Reasons.Count == 0 && p.VideoDirect && p.AudioDirect);

        r.Reasons = plays.SelectMany(p => p.Reasons).GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CountRow(Friendly(g.Key), g.Count())).OrderByDescending(x => x.Count).ToList();
        r.Clients = plays.GroupBy(p => p.Client.Length > 0 ? p.Client : "Unknown app")
            .Select(g => new CountRow($"{g.Key} ({g.Count(p => p.Reasons.Count == 0)} of {g.Count()} played directly)", g.Count()))
            .OrderByDescending(x => x.Count).ToList();
        r.Routes = video.GroupBy(Route).Select(g => new CountRow(g.Key, g.Count())).OrderByDescending(x => x.Count).Take(6).ToList();

        var why = new Dictionary<string, (int Count, HashSet<string> Why)>(StringComparer.Ordinal);
        void Add(string path, IEnumerable<string> what)
        {
            if (!why.TryGetValue(path, out var w)) w = (0, new HashSet<string>());
            foreach (var x in what) w.Why.Add(x);
            why[path] = (w.Count + 1, w.Why);
        }

        foreach (var pl in plays.Where(x => x.Reasons.Count > 0)) Add(pl.Path, Why(pl));
        if (plays.Count == 0) foreach (var s in sessions) Add(s.Path, Why(s));
        r.TopFiles = why.Select(k => new TopFile(k.Key, k.Value.Count, string.Join(", ", k.Value.Why)))
            .OrderByDescending(f => f.Count).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(10).ToList();

        var sug = new Dictionary<string, Suggestion>();
        void Suggest(string key, string value, string reason) { if (!sug.ContainsKey(key)) sug[key] = new Suggestion(key, value, reason); }
        // Share of playbacks: from the recorded plays when there are any, else from the logs (which only list re-encodes).
        var total = plays.Count > 0 ? plays.Count : sessions.Count;
        bool Enough(int n) => n >= MinPlays && n * 100 >= total * 20;

        // ---------------------------------------------------------------- audio
        // Only a codec problem is worth converting for: AAC plays nearly everywhere. A player that can only do stereo is left to Jellyfin's
        // cheap audio-only downmix, so surround stays for every player that can use it.
        static bool CodecReason(string x) => x.Equals("AudioCodecNotSupported", StringComparison.OrdinalIgnoreCase) || x.Equals("AudioProfileNotSupported", StringComparison.OrdinalIgnoreCase);
        var audioPlays = plays.Where(p => !p.AudioDirect && p.Reasons.Any(CodecReason)).ToList();
        var audioLogs = sessions.Where(s => s.AudioTranscoded && !s.SrcAudio.StartsWith("aac", StringComparison.OrdinalIgnoreCase)).ToList();
        var audioN = plays.Count > 0 ? audioPlays.Count : audioLogs.Count;
        if (Enough(audioN))
        {
            var src = plays.Count > 0
                ? audioPlays.Select(p => (Codec: p.SrcAudio, Ch: p.SrcAudioChannels)).ToList()
                : audioLogs.Select(s => (Codec: s.SrcAudio.ToLowerInvariant(), Ch: s.SrcAudioChannels)).ToList();
            var mode = Most(src, t => AudioMode(t.Ch));
            var from = Most(src, t => AudioName(t.Codec, t.Ch));
            var players = audioPlays.Select(p => p.Client).Where(c => c.Length > 0).Distinct().Count();
            Suggest("hb.AudioMode", mode,
                $"In {audioN} of {total} playbacks the player couldn't decode the audio (mostly {from}{(players > 1 ? $", on {players} different players" : OnClient(audioPlays))}), so Jellyfin re-encoded it every time. " +
                $"{AudioLabels[mode]} keeps the same channels and plays on nearly every device. Players limited to stereo still get a quick audio-only downmix from Jellyfin.");
        }
        else if (plays.Count(p => p.HasReason("AudioChannelsNotSupported")) >= MinPlays)
            r.Notes.Add("Some players could only take stereo, so Jellyfin downmixed the audio while they watched. That is cheap for the server, and converting the library to stereo would take surround away from every other player, so nothing is suggested for it.");

        // ---------------------------------------------------------------- subtitles
        var subPlays = plays.Where(p => p.HasReason("SubtitleCodecNotSupported")).ToList();
        var subText = subPlays.Where(p => !PictureSubs.Contains(p.SrcSubtitle)).ToList();
        var subPicture = subPlays.Count - subText.Count;
        if (Enough(subText.Count))
        {
            var codec = Most(subText, p => p.SrcSubtitle.ToUpperInvariant());
            var reason = $"In {subText.Count} of {total} playbacks the player couldn't take the subtitles as they are ({(codec.Length > 0 ? codec : "text subtitles")}{OnClient(subText)}), so Jellyfin had to convert the stream.";
            Suggest("hb.SubtitleMode", "keep", reason + " Keeping the tracks lets each player pick one it can show.");
            if (SubtitlePlanner.IsStyled(codec))
                Suggest("hb.SubtitleStyled", "keep_srt", reason + " \"Original look, plus a simple text copy\" keeps the fancy track for players that can draw it and gives the others a simple copy they show directly.");
        }

        var burned = sessions.Count(s => s.BurnedSubtitles);
        if (subPicture > 0 || burned >= 2)
            r.Notes.Add($"Picture subtitles (PGS, VobSub) were burned into the picture in {Math.Max(subPicture, burned)} playback(s). Those can't be turned into text by this plugin; a text (SRT) version of those subtitles avoids it.");

        // ---------------------------------------------------------------- video
        var videoPlays = plays.Where(p => !p.VideoDirect && p.DstVideo.Length > 0).ToList();
        var dstFamilies = videoPlays.Count > 0 ? videoPlays.Select(p => (Src: p.SrcVideo, Dst: p.DstVideo)).ToList() : video.Where(s => s.DstVideo.Length > 0).Select(s => (Src: s.SrcVideo, Dst: s.DstVideo)).ToList();
        var codecPlays = videoPlays.Where(p => p.HasReason("VideoCodecNotSupported")).ToList();
        var encoderEvidence = videoPlays.Count > 0 ? codecPlays.Select(p => (Src: p.SrcVideo, Dst: p.DstVideo)).ToList() : dstFamilies;
        if (encoderEvidence.Count >= MinVideo)
        {
            var dst = Most(encoderEvidence, x => x.Dst);
            var n = encoderEvidence.Count(x => x.Dst == dst);
            var encoder = dst switch { "hevc" => "x265", "av1" => "svt_av1", _ => "x264" };
            Suggest("hb.Encoder", encoder,
                $"{n} of {encoderEvidence.Count} video re-encodes produced {Name(dst)}{OnClient(codecPlays)}, so that player wants {Name(dst)}; mostly from {Name(Most(encoderEvidence, x => x.Src))}. Converting the library to {Name(dst)} lets it play directly.");
        }

        var levelPlays = plays.Where(p => p.SrcVideo == "h264" && (p.HasReason("VideoLevelNotSupported") || p.HasReason("VideoProfileNotSupported") || p.HasReason("RefFramesNotSupported"))).ToList();
        if (Enough(levelPlays.Count))
            Suggest("hb.H264Level", "4.1", $"In {levelPlays.Count} of {total} playbacks the H.264 level or profile was too high for the player{OnClient(levelPlays)}. Level 4.1 plays directly on almost every device.");

        var downPlays = plays.Where(p => p.DstHeight > 0 && p.SrcHeight > p.DstHeight + 8 && (p.HasReason("VideoResolutionNotSupported") || p.HasReason("VideoBitrateNotSupported") || p.HasReason("ContainerBitrateExceedsLimit"))).Select(p => p.DstHeight)
            .Concat(plays.Count > 0 ? Enumerable.Empty<int>() : video.Where(s => s.DstHeight > 0 && s.SrcHeight > s.DstHeight + 8).Select(s => s.DstHeight)).ToList();
        var videoTotal = Math.Max(videoPlays.Count, video.Count);
        if (downPlays.Count >= MinVideo && downPlays.Count * 100 >= Math.Max(1, videoTotal) * 40)
        {
            var bucket = downPlays.GroupBy(Nearest).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            Suggest("hb.MaxHeight", bucket.Key.ToString(CultureInfo.InvariantCulture),
                $"{downPlays.Count} video re-encodes were scaled down, most often to {bucket.Key}p ({bucket.Count()} times). Capping the library at {bucket.Key}p matches what gets watched and shrinks the files.");
        }

        var tone = video.Count(s => s.Tonemapped) + plays.Count(p => p.HasReason("VideoRangeTypeNotSupported"));
        if (tone >= 1)
            Suggest("hb.HdrMode", "skip", $"HDR was tone-mapped to SDR in {tone} playback(s) for a player that can't show HDR. Converting can't fix that and would wash the colours out for players that can show it, so leave HDR files alone.");

        var interlaced = plays.Where(p => p.HasReason("InterlacedVideoNotSupported")).ToList();
        if (interlaced.Count >= 1)
            Suggest("hb.Deinterlace", "auto", $"In {interlaced.Count} playback(s) the player couldn't take interlaced video{OnClient(interlaced)}. Cleaning up interlaced files during conversion fixes that for good.");

        var containerPlays = plays.Where(p => p.HasReason("ContainerNotSupported") && p.DstContainer is "mp4" or "mkv").ToList();
        if (Enough(containerPlays.Count))
        {
            var c = Most(containerPlays, p => p.DstContainer);
            Suggest("hb.Container", c, $"In {containerPlays.Count} of {total} playbacks the player couldn't open the file type and Jellyfin repackaged it as {c.ToUpperInvariant()}{OnClient(containerPlays)}.");
        }

        r.Suggestions = sug.Values.ToList();

        var hw = video.Count(s => s.Hardware);
        if (hw > 0) r.Notes.Add($"{hw} of {video.Count} video re-encodes already ran on hardware, so those cost the server little.");
        if (plays.Count == 0) r.Notes.Add("Jellyfin's reason for each playback (for example \"audio codec not supported\") is recorded from the next playback on. Until then the suggestions come from Jellyfin's transcode logs alone.");
        return r;
    }

    private static string Route(TranscodeSession s) =>
        $"{Name(s.SrcVideo)}{(s.SrcHdr ? " HDR" : string.Empty)} {s.SrcHeight}p → {Name(s.DstVideo)}{(s.DstHeight > 0 ? " " + s.DstHeight + "p" : string.Empty)}";

    private static IEnumerable<string> Why(PlaybackRecord p)
    {
        if (!p.VideoDirect) yield return "video";
        if (!p.AudioDirect) yield return "audio";
        if (p.HasReason("SubtitleCodecNotSupported")) yield return "subtitles";
        if (p.HasReason("ContainerNotSupported")) yield return "file type";
        if (p.HasReason("VideoRangeTypeNotSupported")) yield return "HDR";
    }

    private static IEnumerable<string> Why(TranscodeSession s)
    {
        if (s.VideoTranscoded) yield return "video";
        if (s.AudioTranscoded) yield return "audio";
        if (s.BurnedSubtitles) yield return "burned subtitles";
        if (s.Tonemapped) yield return "HDR tone-mapping";
    }

    private static int Nearest(int height) => Heights.OrderBy(h => Math.Abs(h - height)).First();
}
