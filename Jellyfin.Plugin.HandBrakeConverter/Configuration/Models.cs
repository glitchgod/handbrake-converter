using System;

namespace Jellyfin.Plugin.HandBrakeConverter.Configuration;

/// <summary>One allowed run window. Days use System.DayOfWeek ints (0=Sunday).</summary>
public class ScheduleWindow
{
    public int[] Days { get; set; } = Array.Empty<int>();

    /// <summary>HH:mm, server local time.</summary>
    public string Start { get; set; } = "23:00";

    /// <summary>HH:mm. If earlier than Start the window crosses midnight. Equal to Start = all day.</summary>
    public string End { get; set; } = "10:00";
}

/// <summary>Inclusive date range (yyyy-MM-dd) when nothing may run.</summary>
public class BlackoutRange
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public class HandBrakeSettings
{
    /// <summary>HandBrake encoder id: x264, x265, svt_av1, nvenc_h264, nvenc_h265, nvenc_av1, qsv_h265, qsv_av1 ...</summary>
    public string Encoder { get; set; } = "x265";

    /// <summary>Constant quality (RF/CQ).</summary>
    public int Quality { get; set; } = 22;

    /// <summary>ultrafast..veryslow, only used for x264/x265/svt_av1. Empty = encoder default.</summary>
    public string EncoderPreset { get; set; } = "medium";

    /// <summary>source (same as the original file: MKV stays MKV, MP4 stays MP4) | mkv | mp4.</summary>
    public string Container { get; set; } = "source";

    /// <summary>copy (same as source) | aac | ac3.</summary>
    public string AudioMode { get; set; } = "copy";

    public int AudioBitrate { get; set; } = 160;

    /// <summary>keep (same as source: all tracks) | none | burn (burn one track into the video, drop the rest).</summary>
    public string SubtitleMode { get; set; } = "keep";

    /// <summary>ISO 639-2 language (eng, jpn...) used to pick the track to burn when BurnTrack is 0.</summary>
    public string BurnLanguage { get; set; } = "eng";

    /// <summary>Source subtitle track number (1-based) to burn. 0 = pick by BurnLanguage.</summary>
    public int BurnTrack { get; set; } = 0;

    /// <summary>Burn only forced subtitles (signs/foreign dialogue).</summary>
    public bool BurnForcedOnly { get; set; } = false;

    /// <summary>0 = same as source; otherwise scale down to at most this height (never upscales).</summary>
    public int MaxHeight { get; set; } = 0;

    /// <summary>
    /// What to do with HDR / Dolby Vision sources: skip (leave the file alone) | keep (re-encode as 10-bit with the HDR
    /// information; Dolby Vision files are left alone because it can't be kept) | ignore (convert like any other file).
    /// </summary>
    public string HdrMode { get; set; } = "skip";

    /// <summary>Raw extra arguments appended last.</summary>
    public string ExtraArgs { get; set; } = string.Empty;

    public HandBrakeSettings Clone() => (HandBrakeSettings)MemberwiseClone();
}

/// <summary>Everything that decides how a file is converted and what happens to the original.</summary>
public class ConversionProfile
{
    public HandBrakeSettings HandBrake { get; set; } = new();

    /// <summary>true = replace the original with the converted file; false = keep both.</summary>
    public bool DeleteOriginal { get; set; } = false;

    /// <summary>Filename label for converted copies when originals are kept. Jellyfin shows "Name - Label" files as versions.</summary>
    public string OutputSuffix { get; set; } = "- HandBrake";

    /// <summary>The trimmed label, e.g. "- HandBrake" (never empty).</summary>
    public string GetLabel()
    {
        var s = (OutputSuffix ?? string.Empty).Trim();
        return s.Length > 0 ? s : "- HandBrake";
    }
}

public class LibrarySettings
{
    public string LibraryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Convert everything new in this library automatically (inside the schedule).</summary>
    public bool AutoProcess { get; set; } = false;

    /// <summary>false = use the plugin defaults, true = use Profile below.</summary>
    public bool UseCustom { get; set; } = false;

    public ConversionProfile Profile { get; set; } = new();
}
