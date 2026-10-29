using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

public record CountRow(string Name, int Count);

public record TopFile(string Path, int Count, string Why);

/// <summary>A suggested value for one setting on the Default settings tab (Key is the field's data-k).</summary>
public record Suggestion(string Key, string Value, string Reason);

public class InsightsResult
{
    public int Days { get; set; }

    /// <summary>Playbacks found (video re-encodes + audio-only re-encodes), repeated starts of the same file collapsed.</summary>
    public int Sessions { get; set; }

    public int VideoTranscodes { get; set; }
    public int AudioTranscodes { get; set; }
    public List<CountRow> Routes { get; set; } = new();
    public List<TopFile> TopFiles { get; set; } = new();
    public List<Suggestion> Suggestions { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string LogDirectory { get; set; } = string.Empty;
}

/// <summary>Turns the transcode history into "what to convert your library to" suggestions.</summary>
public static class TranscodeInsights
{
    private const int MinSessions = 3;
    private static readonly int[] Heights = { 2160, 1440, 1080, 720, 576, 480 };

    public static string Name(string family) => family switch
    {
        "h264" => "H.264", "hevc" => "HEVC", "av1" => "AV1", "vp9" => "VP9", "mpeg2video" => "MPEG-2", "mpeg4" => "MPEG-4", "vc1" => "VC-1", "" => "?", _ => family.ToUpperInvariant()
    };

    public static InsightsResult Analyze(IEnumerable<TranscodeSession> all, int days)
    {
        var r = new InsightsResult { Days = days };

        // Seeking or resuming starts a new log for the same playback; count one playback per file per 10 minutes.
        var sessions = new List<TranscodeSession>();
        foreach (var s in all.OrderBy(x => x.Time))
        {
            var prev = sessions.LastOrDefault(x => x.Path == s.Path);
            if (prev != null && s.Time - prev.Time < TimeSpan.FromMinutes(10)) { sessions.Remove(prev); }
            sessions.Add(s);
        }

        var video = sessions.Where(s => s.VideoTranscoded).ToList();
        r.Sessions = sessions.Count;
        r.VideoTranscodes = video.Count;
        r.AudioTranscodes = sessions.Count(s => s.AudioTranscoded);

        r.Routes = video
            .GroupBy(s => Route(s))
            .Select(g => new CountRow(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).Take(6).ToList();

        r.TopFiles = sessions
            .GroupBy(s => s.Path)
            .Select(g => new TopFile(g.Key, g.Count(), Why(g.ToList())))
            .Where(f => f.Count > 1).OrderByDescending(f => f.Count).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(10).ToList();

        if (sessions.Count < MinSessions) return r;

        // Encoder: whatever format Jellyfin most often has to produce is what the clients play natively.
        if (video.Count >= MinSessions)
        {
            var dst = video.Where(s => s.DstVideo != string.Empty).GroupBy(s => s.DstVideo).OrderByDescending(g => g.Count()).First();
            var encoder = dst.Key switch { "hevc" => "x265", "av1" => "svt_av1", _ => "x264" };
            var fromOther = video.Count(s => s.SrcVideo != dst.Key);
            r.Suggestions.Add(new Suggestion("hb.Encoder", encoder,
                $"{dst.Count()} of {video.Count} video transcodes produced {Name(dst.Key)} ({Pct(dst.Count(), video.Count)}), so your players want {Name(dst.Key)}; {fromOther} of them started from a different format. Converting the library to {Name(dst.Key)} lets them play it directly."));

            var down = video.Where(s => s.DstHeight > 0 && s.SrcHeight > s.DstHeight + 8).ToList();
            if (down.Count * 100 >= video.Count * 40)
            {
                var bucket = down.GroupBy(s => Nearest(s.DstHeight)).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
                r.Suggestions.Add(new Suggestion("hb.MaxHeight", bucket.Key.ToString(CultureInfo.InvariantCulture),
                    $"{down.Count} of {video.Count} video transcodes were scaled down, most often to {bucket.Key}p ({bucket.Count()} times). Capping the library at {bucket.Key}p matches what gets watched and shrinks the files."));
            }
        }

        // Audio: re-encoded in a lot of playbacks (TrueHD/DTS to something the client can play).
        var audio = sessions.Where(s => s.AudioTranscoded).ToList();
        if (audio.Count >= MinSessions && audio.Count * 100 >= sessions.Count * 30)
        {
            var target = audio.GroupBy(s => s.DstAudio.ToLowerInvariant()).OrderByDescending(g => g.Count()).First().Key;
            var mode = target.Contains("ac3", StringComparison.Ordinal) ? "ac3" : "aac";
            var from = audio.Where(s => s.SrcAudio != string.Empty).GroupBy(s => s.SrcAudio.ToUpperInvariant()).OrderByDescending(g => g.Count()).FirstOrDefault();
            r.Suggestions.Add(new Suggestion("hb.AudioMode", mode,
                $"The audio was re-encoded in {audio.Count} of {sessions.Count} playbacks{(from != null ? $", mostly from {from.Key}" : string.Empty)}. Converting it once to {mode.ToUpperInvariant()} saves the server doing it every time (surround is kept)."));
        }

        var tone = video.Count(s => s.Tonemapped);
        if (tone >= 2 && tone * 100 >= video.Count * 20)
            r.Notes.Add($"{tone} of {video.Count} video transcodes were HDR being tone-mapped to SDR for a player that can't show HDR. Leave 'HDR videos' on 'Leave them alone': converting HDR would only wash the colours out for the players that can show it.");

        var burn = video.Count(s => s.BurnedSubtitles);
        if (burn >= 2 && burn * 100 >= video.Count * 20)
            r.Notes.Add($"Subtitles were burned into the picture in {burn} of {video.Count} video transcodes (usually picture subtitles such as PGS that the player couldn't show). This plugin can't convert those; text subtitles (SRT) avoid it.");

        var hw = video.Count(s => s.Hardware);
        if (hw > 0) r.Notes.Add($"{hw} of {video.Count} video transcodes already ran on hardware, so those cost the server little.");
        return r;
    }

    private static string Route(TranscodeSession s) =>
        $"{Name(s.SrcVideo)}{(s.SrcHdr ? " HDR" : string.Empty)} {s.SrcHeight}p → {Name(s.DstVideo)}{(s.DstHeight > 0 ? " " + s.DstHeight + "p" : string.Empty)}";

    private static string Why(List<TranscodeSession> g)
    {
        var why = new List<string>();
        if (g.Any(s => s.VideoTranscoded)) why.Add("video");
        if (g.Any(s => s.AudioTranscoded)) why.Add("audio");
        if (g.Any(s => s.BurnedSubtitles)) why.Add("burned subtitles");
        if (g.Any(s => s.Tonemapped)) why.Add("HDR tone-mapping");
        return string.Join(", ", why);
    }

    private static int Nearest(int height) => Heights.OrderBy(h => Math.Abs(h - height)).First();

    private static string Pct(int a, int b) => (100 * a / Math.Max(1, b)).ToString(CultureInfo.InvariantCulture) + "%";
}
