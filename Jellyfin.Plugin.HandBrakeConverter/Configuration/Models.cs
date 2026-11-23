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

    /// <summary>copy (same as source) | aac_51 / ac3_51 (5.1 surround kept) | aac / ac3 (stereo, 2 channels).</summary>
    public string AudioMode { get; set; } = "copy";

    public int AudioBitrate { get; set; } = 160;

    /// <summary>keep (same as source: all tracks) | none | burn (burn one track into the video, drop the rest).</summary>
    public string SubtitleMode { get; set; } = "keep";

    /// <summary>
    /// Subtitle mode "keep" only: the languages to keep, comma separated (eng, spa). Empty = keep every language. Tracks in other
    /// languages are dropped (see SubtitleKeepForced).
    /// </summary>
    public string SubtitleLanguages { get; set; } = "eng";

    /// <summary>Keep tracks marked as forced (translations of foreign-language lines) even when their language is not listed.</summary>
    public bool SubtitleKeepForced { get; set; } = true;

    /// <summary>
    /// Plain text subtitles (MOV_TEXT, WebVTT...). srt = turn them into SRT, which nearly every player shows without re-encoding the video
    /// (MP4: written as separate .srt files) | asis = leave them as they are. Styled subtitles (ASS/SSA) follow <see cref="SubtitleStyled"/>.
    /// </summary>
    public string SubtitleFormat { get; set; } = "srt";

    /// <summary>
    /// Styled subtitles (ASS/SSA: their own fonts, sizes, colours and positions, common in anime). keep = exactly as they are, with their
    /// fonts (MKV; MP4 can't hold them, so there they become a separate .ass file) | keep_srt = the same, plus a plain .srt file next to
    /// the video for players that can't draw styled subtitles | srt = plain SRT (the styling is lost).
    /// </summary>
    public string SubtitleStyled { get; set; } = "keep";

    /// <summary>
    /// Audio languages to keep, comma separated (eng, jpn). Empty = keep every audio track. Tracks with no language tag are always kept,
    /// and if no track matches nothing is dropped.
    /// </summary>
    public string AudioLanguages { get; set; } = string.Empty;

    /// <summary>auto = remove combing only from videos that are interlaced (DVD, TV recordings) | always | off.</summary>
    public string Deinterlace { get; set; } = "auto";

    /// <summary>
    /// Subtitle mode "keep" only. off = subtitles stay inside the video file | also = text tracks are ALSO written as separate .srt files next to
    /// the video | only = text tracks are written as separate .srt files and removed from inside the file (picture subtitles stay inside).
    /// </summary>
    public string SubtitleExternal { get; set; } = "off";

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

    /// <summary>
    /// 10-bit sources: source = stay 10-bit when the encoder has a 10-bit version (H.265, AV1; smoother gradients) | 8bit = convert to
    /// 8-bit (plays on older devices). HDR kept with "Convert and keep HDR" is always 10-bit, because HDR needs it.
    /// </summary>
    public string BitDepth { get; set; } = "source";

    /// <summary>
    /// x264 only: the highest H.264 level to use. "4.1" (default) plays directly on almost every TV, phone and browser. "auto" lets x264 pick,
    /// and options such as ref=5 then push 1080p to level 5.0, which many devices cannot play directly (Jellyfin then re-encodes it live).
    /// Not applied above 1080p, where level 4.1 is too low.
    /// </summary>
    public string H264Level { get; set; } = "4.1";

    public HandBrakeSettings Clone() => (HandBrakeSettings)MemberwiseClone();
}

/// <summary>Everything that decides how a file is converted and what happens to the original.</summary>
public class ConversionProfile
{
    public HandBrakeSettings HandBrake { get; set; } = new();

    /// <summary>true = replace the original with the converted file; false = keep both.</summary>
    public bool DeleteOriginal { get; set; } = false;

    /// <summary>
    /// Only with DeleteOriginal and an MKV/MP4 container: also change the file's extension (e.g. .mkv to .mp4). Jellyfin identifies a
    /// video by its path, so the library then sees a NEW item (watch history, favourites and Jellyfin-only edits do not carry over).
    /// </summary>
    public bool ChangeExtension { get; set; } = false;

    /// <summary>With a changed extension: copy watched status, favourites, rating, collections and date added onto the new item.</summary>
    public bool KeepWatchData { get; set; } = true;

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
