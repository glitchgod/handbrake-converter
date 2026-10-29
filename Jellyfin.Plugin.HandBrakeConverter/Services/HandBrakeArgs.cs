using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>Builds the argument list (not a shell string) for HandBrakeCLI.</summary>
    public static List<string> Build(HandBrakeSettings s, string input, string output)
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

        a.Add("--all-audio");
        switch (s.AudioMode)
        {
            case "aac": a.Add("--aencoder"); a.Add("av_aac"); a.Add("-B"); a.Add(N(s.AudioBitrate)); break;
            case "ac3": a.Add("--aencoder"); a.Add("ac3"); a.Add("-B"); a.Add(N(s.AudioBitrate)); break;
            default: a.Add("--aencoder"); a.Add("copy"); break;
        }

        // --maxHeight alone makes HandBrake keep the source width and store non-square pixels (1280x480 with a 2:3 pixel
        // aspect), which many players mishandle. --non-anamorphic forces square pixels, so 720p -> 854x480.
        if (s.MaxHeight > 0) { a.Add("--maxHeight"); a.Add(N(s.MaxHeight)); a.Add("--non-anamorphic"); }

        switch (s.SubtitleMode)
        {
            case "burn": AddBurn(a, s); break;
            case "none": break;
            default: a.Add("--all-subtitles"); break;
        }

        if (!string.IsNullOrWhiteSpace(s.ExtraArgs))
        {
            a.AddRange(s.ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
