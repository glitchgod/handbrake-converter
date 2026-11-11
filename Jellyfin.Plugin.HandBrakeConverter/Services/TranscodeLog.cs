using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>One playback that Jellyfin had to transcode (video) or re-encode the audio for, read from its ffmpeg log.</summary>
public class TranscodeSession
{
    /// <summary>The log file name; unique per session.</summary>
    public string Id { get; set; } = string.Empty;

    public DateTime Time { get; set; }
    public string Path { get; set; } = string.Empty;

    /// <summary>Source video codec family: h264, hevc, av1, vp9, ...</summary>
    public string SrcVideo { get; set; } = string.Empty;

    public bool SrcHdr { get; set; }
    public int SrcHeight { get; set; }

    /// <summary>true = the video was re-encoded; false = only the audio was (video copied).</summary>
    public bool VideoTranscoded { get; set; }

    public string DstVideo { get; set; } = string.Empty;
    public int DstHeight { get; set; }
    public string SrcAudio { get; set; } = string.Empty;
    public bool AudioTranscoded { get; set; }
    public string DstAudio { get; set; } = string.Empty;
    public bool BurnedSubtitles { get; set; }
    public bool Tonemapped { get; set; }
    public bool Hardware { get; set; }
}

/// <summary>Reads the top of a Jellyfin <c>FFmpeg.Transcode-*.log</c> / <c>FFmpeg.Remux-*.log</c> (command line + ffmpeg's stream summary).</summary>
public static class TranscodeLog
{
    private static readonly Regex InputLine = new(@"^Input #0.*? from '(?:file:)?(?<path>.*)':\s*$", RegexOptions.Compiled);
    private static readonly Regex StreamLine = new(@"^\s*Stream #0:(?<n>\d+)(?:\([^)]*\))?(?:\[0x[0-9a-fA-F]+\])?: (?<kind>Video|Audio|Subtitle): (?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex VideoRest = new(@"^(?<codec>\w+)[^,]*,\s*(?<pix>[a-z0-9_]+)(?:\((?<cs>[^)]*)\))?.*?\b(?<w>\d{3,5})x(?<h>\d{3,5})\b", RegexOptions.Compiled);
    private static readonly Regex VCodec = new(@"-c(?:odec)?:v(?::\d+)?\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex ACodec = new(@"-c(?:odec)?:a(?::\d+)?\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex MapIn = new(@"-map\s+0:(\d+)", RegexOptions.Compiled);
    private static readonly Regex InputArg = new(@"-i\s+(?:file:)?""[^""]*""|-i\s+\S+", RegexOptions.Compiled);
    private static readonly Regex Burn = new(@"\boverlay(?:_\w+)?\b|\bsubtitles=|\bass=", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameTime = new(@"-(\d{4})-(\d{2})-(\d{2})_(\d{2})-(\d{2})-(\d{2})", RegexOptions.Compiled);

    /// <summary>Codec family for an ffmpeg codec or encoder name ("libx264", "h264_nvenc", "hevc" -> h264 / hevc ...).</summary>
    public static string Family(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("264")) return "h264";
        if (n.Contains("265") || n.Contains("hevc")) return "hevc";
        if (n.Contains("av1") || n.Contains("aom")) return "av1";
        if (n.Contains("vp9")) return "vp9";
        return n;
    }

    /// <summary>null when the log is not a video playback that needed re-encoding (music, direct stream, unreadable).</summary>
    public static TranscodeSession? Parse(string head, string fileName, DateTime fallbackTime)
    {
        var lines = head.Replace("\r", string.Empty).Split('\n');
        var inputAt = Array.FindIndex(lines, l => l.StartsWith("Input #0", StringComparison.Ordinal));
        if (inputAt < 0) return null;
        var m = InputLine.Match(lines[inputAt]);
        if (!m.Success) return null;

        var command = lines.Take(inputAt).FirstOrDefault(l => l.Contains(" -i ", StringComparison.Ordinal)) ?? string.Empty;
        var flags = InputArg.Replace(command, " "); // don't match words inside the file's own path

        var end = Array.FindIndex(lines, inputAt, l => l.StartsWith("Stream mapping:", StringComparison.Ordinal) || l.StartsWith("Output #0", StringComparison.Ordinal));
        if (end < 0) end = lines.Length;
        var input = new Dictionary<int, (string Kind, string Text)>();
        foreach (var line in lines.Skip(inputAt + 1).Take(end - inputAt - 1))
        {
            var s = StreamLine.Match(line);
            if (s.Success && !s.Groups["rest"].Value.Contains("(attached pic)", StringComparison.Ordinal))
                input[int.Parse(s.Groups["n"].Value, CultureInfo.InvariantCulture)] = (s.Groups["kind"].Value, s.Groups["rest"].Value);
        }

        var video = input.OrderBy(k => k.Key).Where(k => k.Value.Kind == "Video").Select(k => VideoRest.Match(k.Value.Text)).FirstOrDefault(v => v.Success);
        if (video == null) return null; // audio-only (music)

        var vc = VCodec.Match(flags);
        if (!vc.Success) return null;
        var videoCopy = vc.Groups[1].Value == "copy";
        var ac = ACodec.Match(flags);
        var audioTranscoded = ac.Success && ac.Groups[1].Value != "copy";
        if (videoCopy && !audioTranscoded) return null; // plain direct stream

        // The audio track Jellyfin mapped (first mapped stream that is audio), else the first audio stream.
        var mapped = MapIn.Matches(flags).Select(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        var audioIdx = mapped.Where(i => input.TryGetValue(i, out var v) && v.Kind == "Audio").Cast<int?>().FirstOrDefault()
            ?? input.Where(k => k.Value.Kind == "Audio").Select(k => (int?)k.Key).FirstOrDefault();
        var srcAudio = audioIdx != null ? input[audioIdx.Value].Text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)[0] : string.Empty;

        // The video stream of the output, for the resolution that was actually delivered.
        var outAt = Array.FindIndex(lines, l => l.StartsWith("Output #0", StringComparison.Ordinal));
        var dstHeight = 0;
        if (outAt >= 0)
            foreach (var line in lines.Skip(outAt + 1))
            {
                var s = StreamLine.Match(line);
                if (!s.Success || s.Groups["kind"].Value != "Video") continue;
                var v = VideoRest.Match(s.Groups["rest"].Value);
                if (v.Success) dstHeight = int.Parse(v.Groups["h"].Value, CultureInfo.InvariantCulture);
                break;
            }

        var cs = video.Groups["cs"].Value;
        var time = NameTime.Match(fileName) is { Success: true } t
            ? new DateTime(int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(t.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(t.Groups[4].Value, CultureInfo.InvariantCulture), int.Parse(t.Groups[5].Value, CultureInfo.InvariantCulture), int.Parse(t.Groups[6].Value, CultureInfo.InvariantCulture), DateTimeKind.Local)
            : fallbackTime;

        return new TranscodeSession
        {
            Id = fileName,
            Time = time,
            Path = m.Groups["path"].Value,
            SrcVideo = Family(video.Groups["codec"].Value),
            SrcHdr = cs.Contains("smpte2084", StringComparison.Ordinal) || cs.Contains("arib-std-b67", StringComparison.Ordinal),
            SrcHeight = int.Parse(video.Groups["h"].Value, CultureInfo.InvariantCulture),
            VideoTranscoded = !videoCopy,
            DstVideo = videoCopy ? string.Empty : Family(vc.Groups[1].Value),
            DstHeight = videoCopy ? 0 : dstHeight,
            SrcAudio = srcAudio,
            AudioTranscoded = audioTranscoded,
            DstAudio = audioTranscoded ? ac.Groups[1].Value : string.Empty,
            BurnedSubtitles = !videoCopy && Burn.IsMatch(flags),
            Tonemapped = !videoCopy && flags.Contains("tonemap", StringComparison.OrdinalIgnoreCase),
            Hardware = !videoCopy && Regex.IsMatch(vc.Groups[1].Value, "nvenc|qsv|vaapi|videotoolbox|amf|v4l2", RegexOptions.IgnoreCase)
        };
    }
}
