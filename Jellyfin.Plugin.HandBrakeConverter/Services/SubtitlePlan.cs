using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>One subtitle track of a file. <see cref="Index"/> counts subtitle tracks only (0 = the first one).</summary>
public sealed record SubStream(int Index, string Codec, string Language, bool Forced, string Title);

/// <summary>What to do with the subtitle tracks of one file.</summary>
public sealed record SubPlan(IReadOnlyList<SubStream> Keep, IReadOnlyList<string> OutCodecs, int Total, string Summary)
{
    /// <summary>True when the file's tracks are not already exactly what the plan wants (so a fix is worth doing).</summary>
    public bool NeedsWork { get; init; }
}

/// <summary>
/// Decides which subtitle tracks to keep (chosen languages, plus forced tracks for foreign-language parts) and which format each one
/// ends up in. Pure logic: the tracks come from ffprobe, the actual muxing is done by ffmpeg (<see cref="SubtitleMux"/>).
/// </summary>
public static class SubtitlePlanner
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "tx3g" };

    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new[] { "eng" }, ["es"] = new[] { "spa" }, ["fr"] = new[] { "fre", "fra" }, ["de"] = new[] { "ger", "deu" },
        ["ja"] = new[] { "jpn" }, ["it"] = new[] { "ita" }, ["pt"] = new[] { "por" }, ["ru"] = new[] { "rus" },
        ["zh"] = new[] { "chi", "zho" }, ["ko"] = new[] { "kor" }, ["nl"] = new[] { "dut", "nld" }, ["sv"] = new[] { "swe" }
    };

    public static bool IsText(string codec) => TextCodecs.Contains(codec ?? string.Empty);

    /// <summary>"eng, spa" or "en;es" -> normalised 3-letter codes (2-letter codes are expanded).</summary>
    public static HashSet<string> ParseLanguages(string? list)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (list ?? string.Empty).Split(new[] { ',', ';', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim().ToLowerInvariant();
            set.Add(t);
            if (Aliases.TryGetValue(t, out var a)) foreach (var x in a) set.Add(x);
        }

        return set;
    }

    private static bool IsUndetermined(string lang) => lang is "" or "und" or "unk" or "mis" or "zxx";

    /// <summary>The output codec for one kept text/picture track, or null when it cannot be stored in this container.</summary>
    public static string? OutCodec(string codec, HandBrakeSettings hb, bool mp4)
    {
        // MKV cannot hold MOV_TEXT (an MP4-only format), so those are always turned into SRT, even when "leave as they are" is chosen.
        if (IsText(codec)) return mp4 ? "mov_text" : hb.SubtitleFormat == "asis" && !codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase) && !codec.Equals("tx3g", StringComparison.OrdinalIgnoreCase) ? "copy" : "srt";
        return mp4 ? null : "copy"; // picture subtitles (PGS, VobSub) can't be turned into text and don't fit in MP4
    }

    private static bool AlreadyRight(string codec, string outCodec) =>
        outCodec == "copy" || (outCodec == "srt" && codec.Equals("subrip", StringComparison.OrdinalIgnoreCase)) || (outCodec == "mov_text" && codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase));

    /// <summary>null when subtitles are not being kept as tracks (burned in / removed / none in the file).</summary>
    public static SubPlan? Make(IReadOnlyList<SubStream>? streams, HandBrakeSettings hb, bool mp4)
    {
        if (streams == null || streams.Count == 0 || hb.SubtitleMode != "keep") return null;

        var langs = ParseLanguages(hb.SubtitleLanguages);
        var keep = streams.Where(s => OutCodec(s.Codec, hb, mp4) != null).ToList();
        var note = string.Empty;
        if (langs.Count > 0)
        {
            var wanted = keep.Where(s => langs.Contains(s.Language.ToLowerInvariant()) || (hb.SubtitleKeepForced && s.Forced)).ToList();
            if (wanted.Count == 0)
            {
                // None in the chosen languages: undetermined ones are the best guess; if even those are missing, leave the list alone
                // rather than strip every subtitle from the file.
                wanted = keep.Where(s => IsUndetermined(s.Language.ToLowerInvariant())).ToList();
                if (wanted.Count == 0) { wanted = keep; note = " (none in the chosen languages, so all kept)"; }
                else note = " (none in the chosen languages, so the undetermined ones were kept)";
            }

            keep = wanted;
        }

        var codecs = keep.Select(s => OutCodec(s.Codec, hb, mp4)!).ToList();
        var work = keep.Count != streams.Count || keep.Where((s, i) => !AlreadyRight(s.Codec, codecs[i])).Any();
        var forced = keep.Count(s => s.Forced);
        var sb = new StringBuilder();
        sb.Append(keep.Count).Append(" of ").Append(streams.Count).Append(" subtitle tracks kept");
        if (langs.Count > 0) sb.Append(" (").Append(string.Join(", ", langs.Where(l => l.Length == 3))).Append(forced > 0 && hb.SubtitleKeepForced ? " + forced" : string.Empty).Append(')');
        if (codecs.Contains("srt")) sb.Append(", text as SRT");
        sb.Append(note);
        return new SubPlan(keep, codecs, streams.Count, sb.ToString()) { NeedsWork = work };
    }
}

/// <summary>ffmpeg command lines that build the final file from the video/audio file and the chosen subtitle tracks.</summary>
public static class SubtitleMux
{
    /// <summary>
    /// <paramref name="main"/> supplies video, audio and chapters. The subtitle tracks come from <paramref name="subSource"/>
    /// (the original file after a HandBrake encode) or from <paramref name="main"/> itself (a subtitle-only fix, subSource = null).
    /// </summary>
    public static List<string> Args(string main, string? subSource, SubPlan plan, bool mp4, string output)
    {
        var a = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", main };
        if (subSource != null) { a.Add("-i"); a.Add(subSource); }
        var si = subSource != null ? 1 : 0;
        a.Add("-map"); a.Add("0:v"); a.Add("-map"); a.Add("0:a?");
        foreach (var s in plan.Keep) { a.Add("-map"); a.Add($"{si}:s:{s.Index}"); }
        a.Add("-map_chapters"); a.Add("0");
        a.Add("-c"); a.Add("copy");
        for (var i = 0; i < plan.Keep.Count; i++) { a.Add($"-c:s:{i}"); a.Add(plan.OutCodecs[i]); }
        a.Add("-f"); a.Add(mp4 ? "mp4" : "matroska");
        a.Add(output);
        return a;
    }
}
