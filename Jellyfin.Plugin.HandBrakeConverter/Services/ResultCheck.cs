using System;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>Everything compared between the source and the converted file before the original may be replaced.</summary>
public static class ResultCheck
{
    /// <summary>Output may differ by a few seconds (container rounding); more than max(3 s, 1 %) means a truncated or broken encode.</summary>
    public static bool DurationsMatch(double source, double output) => Math.Abs(source - output) <= Math.Max(3.0, source * 0.01);

    /// <summary>null = the result is good; otherwise why it must be discarded.</summary>
    public static string? Problem(MediaInfo src, MediaInfo result, HandBrakeSettings hb, bool outputIsMp4)
    {
        if (!DurationsMatch(src.Duration, result.Duration))
            return $"Converted file is {result.Duration:0}s long but the source is {src.Duration:0}s; discarded it and kept the original.";

        if (result.AudioTracks != src.AudioTracks)
            return $"Converted file has {result.AudioTracks} audio track(s) but the source has {src.AudioTracks}; discarded it and kept the original.";

        // MP4 can only hold text subtitles, so HandBrake legitimately drops picture subtitles there. Burn/none change the count on purpose.
        if (hb.SubtitleMode == "keep" && !outputIsMp4 && result.SubtitleTracks != src.SubtitleTracks)
            return $"Converted file has {result.SubtitleTracks} subtitle track(s) but the source has {src.SubtitleTracks}; discarded it and kept the original.";

        if (src.Chapters > 0 && result.Chapters != src.Chapters)
            return $"Converted file has {result.Chapters} chapter(s) but the source has {src.Chapters}; discarded it and kept the original.";

        if (src.IsHdr && hb.HdrMode == "keep" && (result.Transfer != src.Transfer || result.BitDepth < 10))
            return "The HDR information was not kept in the converted file; discarded it and kept the original.";

        return null;
    }
}
