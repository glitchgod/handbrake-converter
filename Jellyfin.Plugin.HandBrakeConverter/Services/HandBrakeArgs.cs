using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

public static class HandBrakeArgs
{
    /// <summary>File extension for an explicit container choice ("source" is resolved by the caller from the input file).</summary>
    public static string Extension(HandBrakeSettings s) =>
        string.Equals(s.Container, "mp4", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".mkv";

    /// <summary>The 10-bit version of an encoder (needed to keep HDR), or null when it has none (H.264 hardware encoders).</summary>
    public static string? TenBit(string encoder)
    {
        if (encoder.EndsWith("_10bit", StringComparison.Ordinal) || encoder.EndsWith("_12bit", StringComparison.Ordinal)) return encoder;
        return encoder is "x265" or "svt_av1" or "nvenc_h265" or "nvenc_av1" or "qsv_h265" or "qsv_av1" ? encoder + "_10bit" : null;
    }

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static bool SupportsEncoderPreset(string enc) =>
        enc.StartsWith("x264", StringComparison.Ordinal) || enc.StartsWith("x265", StringComparison.Ordinal) || enc.StartsWith("svt_av1", StringComparison.Ordinal);

    // Audio HandBrake can pass through untouched, per container. Anything else is re-encoded (MP4 players rarely handle the rest anyway).
    private static readonly HashSet<string> Mp4Copy = new(StringComparer.OrdinalIgnoreCase) { "aac", "ac3", "eac3", "mp3" };
    private static readonly HashSet<string> MkvCopy = new(StringComparer.OrdinalIgnoreCase) { "aac", "ac3", "eac3", "truehd", "dts", "mp3", "mp2", "flac", "opus" };

    /// <summary>The video format an encoder makes, as TranscodeLog.Family names it ("hevc" for x265, nvenc_h265, qsv_h265...), or "" when unknown.</summary>
    public static string Family(string encoder)
    {
        var e = encoder.ToLowerInvariant();
        if (e.Contains("265") || e.Contains("hevc")) return "hevc";
        if (e.Contains("av1")) return "av1";
        if (e.Contains("264")) return "h264";
        if (e.Contains("vp9")) return "vp9";
        return string.Empty;
    }

    private static string Mixdown(int channels) => channels switch { <= 1 => "mono", 2 => "stereo", <= 6 => "5point1", _ => "7point1" };

    /// <summary>
    /// Encoder, mixdown and bitrate for one audio track. A track that is already in the chosen codec with no more channels than the
    /// chosen layout is copied (re-encoding it would only lose quality). Nothing is ever upmixed. In "same as source" mode a track the
    /// container can't carry (DTS or TrueHD going into MP4, PCM...) becomes AAC with the same channels instead of HandBrake's fallback.
    /// </summary>
    internal static (string Encoder, string Mixdown, int Bitrate) AudioTrack(HandBrakeSettings s, AudioStream track, bool mp4)
    {
        var ch = track.Channels > 0 ? track.Channels : 2;
        var copyable = (mp4 ? Mp4Copy : MkvCopy).Contains(track.Codec);
        var stereo = Math.Clamp(s.AudioBitrate, 64, 640);
        var (encoder, family, maxCh) = s.AudioMode switch
        {
            "aac" => ("av_aac", "aac", 2),
            "ac3" => ("ac3", "ac3", 2),
            "aac_51" => ("av_aac", "aac", 6),
            "ac3_51" => ("ac3", "ac3", 6),
            "aac_71" => ("av_aac", "aac", 8),
            _ => ("copy", track.Codec, 8)
        };

        // Atmos and DTS:X are copied whenever the container can carry them, whatever the audio setting: re-encoding would flatten them.
        if (copyable && (encoder == "copy" || track.ObjectAudio || track.Codec.Equals(family, StringComparison.OrdinalIgnoreCase) && ch <= maxCh))
            return ("copy", "none", stereo);
        if (encoder == "copy") encoder = "av_aac";

        var outCh = Math.Min(ch, maxCh);
        var ac3 = encoder == "ac3";
        var rate = outCh switch
        {
            <= 1 => ac3 ? Math.Max(96, stereo / 2) : Math.Max(64, stereo / 2),
            2 => ac3 ? Math.Max(192, stereo) : stereo,
            <= 6 => ac3 ? 448 : 384,
            _ => 640
        };
        return (encoder, Mixdown(outCh), rate);
    }

    /// <summary>Builds the argument list (not a shell string) for HandBrakeCLI.</summary>
    /// <param name="audioTracks">1-based audio tracks to keep, or null for all of them.</param>
    /// <param name="deinterlace">remove combing (the source is interlaced).</param>
    /// <param name="audioStreams">the audio tracks that will be encoded, in order (from ffprobe). When given, every track gets its own
    /// encoder, mixdown and bitrate; without it (no probe) one setting is given for all tracks.</param>
    public static List<string> Build(HandBrakeSettings s, string input, string output, bool noSubtitles = false, int[]? audioTracks = null, bool deinterlace = false,
        IReadOnlyList<AudioStream>? audioStreams = null)
    {
        var a = new List<string> { "-i", input, "-o", output };
        var mp4 = Extension(s) == ".mp4";

        a.Add("-f"); a.Add(mp4 ? "av_mp4" : "av_mkv");
        a.Add("-e"); a.Add(s.Encoder);
        a.Add("-q"); a.Add(N(s.Quality));
        if (!string.IsNullOrWhiteSpace(s.EncoderPreset) && SupportsEncoderPreset(s.Encoder))
        {
            a.Add("--encoder-preset"); a.Add(s.EncoderPreset);
        }

        if (s.Encoder.StartsWith("x264", StringComparison.Ordinal) && s.H264Level is { Length: > 0 } lvl && lvl != "auto"
            && lvl.All(ch => char.IsDigit(ch) || ch == '.'))
        {
            a.Add("--encoder-level"); a.Add(lvl);
        }

        if (audioTracks is { Length: > 0 }) { a.Add("--audio"); a.Add(string.Join(",", audioTracks.Select(N))); }
        else a.Add("--all-audio");
        if (audioStreams is { Count: > 0 })
        {
            var tracks = audioStreams.Select(t => AudioTrack(s, t, mp4)).ToList();
            a.Add("--aencoder"); a.Add(string.Join(",", tracks.Select(t => t.Encoder)));
            a.Add("--mixdown"); a.Add(string.Join(",", tracks.Select(t => t.Mixdown)));
            a.Add("-B"); a.Add(string.Join(",", tracks.Select(t => N(t.Bitrate))));
            a.Add("--audio-copy-mask"); a.Add("aac,ac3,eac3,truehd,dts,dtshd,mp3,mp2,flac,opus");
            a.Add("--audio-fallback"); a.Add("av_aac");
        }
        else switch (s.AudioMode)
        {
            // Stereo modes: every track is mixed down to 2 channels (a mono track stays mono).
            case "aac": a.Add("--aencoder"); a.Add("av_aac"); a.Add("--mixdown"); a.Add("stereo"); a.Add("-B"); a.Add(N(s.AudioBitrate)); break;
            case "ac3": a.Add("--aencoder"); a.Add("ac3"); a.Add("--mixdown"); a.Add("stereo"); a.Add("-B"); a.Add(N(s.AudioBitrate)); break;
            // Surround modes: 5.1 tracks keep 6 channels, stereo tracks stay stereo (nothing is upmixed). Explicit bitrates,
            // because HandBrake's own default for 5.1 is low (AC3 224 kbps, AAC ~195 kbps).
            case "aac_51": a.Add("--aencoder"); a.Add("av_aac"); a.Add("--mixdown"); a.Add("5point1"); a.Add("-B"); a.Add("384"); break;
            case "ac3_51": a.Add("--aencoder"); a.Add("ac3"); a.Add("--mixdown"); a.Add("5point1"); a.Add("-B"); a.Add("448"); break;
            // AC3 cannot carry more than 5.1, so 7.1 is AAC only. 5.1 tracks stay 5.1 and stereo stays stereo (nothing is upmixed).
            case "aac_71": a.Add("--aencoder"); a.Add("av_aac"); a.Add("--mixdown"); a.Add("7point1"); a.Add("-B"); a.Add("640"); break;
            default: a.Add("--aencoder"); a.Add("copy"); break;
        }

        // Never crop: HandBrake trims black bars by itself by default and can misjudge dark scenes or changing aspect ratios.
        a.Add("--crop"); a.Add("0:0:0:0");

        // --maxHeight alone makes HandBrake keep the source width and store non-square pixels (1280x480 with a 2:3 pixel
        // aspect), which many players mishandle. --non-anamorphic forces square pixels, so 720p -> 854x480.
        if (s.MaxHeight > 0) { a.Add("--maxHeight"); a.Add(N(s.MaxHeight)); a.Add("--non-anamorphic"); }

        // comb-detect + decomb only touches frames that really show combing, so progressive frames in the same file are left alone.
        // Detected interlacing is often film that was telecined for TV/DVD (3:2 pulldown): detelecine rebuilds those frames first, and
        // leaves real interlaced video for decomb.
        if (deinterlace) a.Add("--detelecine");
        if (deinterlace || s.Deinterlace == "always") { a.Add("--comb-detect"); a.Add("--decomb"); }

        switch (s.SubtitleMode)
        {
            case "burn": AddBurn(a, s); break;
            case "none": break;
            default: if (!noSubtitles) a.Add("--all-subtitles"); break; // noSubtitles: ffmpeg adds the chosen tracks afterwards
        }

        return a;
    }

    /// <summary>
    /// Burn one subtitle track into the video and drop the other tracks. Explicit track: --subtitle N --subtitle-burned=1 (index 1 =
    /// the only track in the --subtitle list). Otherwise the first track matching the language is selected and burned.
    /// </summary>
    private static void AddBurn(List<string> a, HandBrakeSettings s)
    {
        if (s.BurnTrack > 0)
        {
            a.Add("--subtitle"); a.Add(N(s.BurnTrack));
            a.Add("--subtitle-burned=1");
            if (s.BurnForcedOnly) a.Add("--subtitle-forced=1");
        }
        else
        {
            var lang = string.IsNullOrWhiteSpace(s.BurnLanguage) ? "eng" : s.BurnLanguage.Trim().ToLowerInvariant();
            a.Add("--subtitle-lang-list"); a.Add(lang);
            a.Add("--first-subtitle");
            a.Add("--subtitle-burned");   // no number = burn the first selected track
            if (s.BurnForcedOnly) a.Add("--subtitle-forced");
        }
    }
}
