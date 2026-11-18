using System;
using System.Collections.Generic;
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

    /// <summary>Text tracks that are also (or only) written as separate .srt files next to the video.</summary>
    public IReadOnlyList<SubStream> External { get; init; } = Array.Empty<SubStream>();
}

/// <summary>Names of the separate subtitle files, in the form Jellyfin and most players recognise: Name.eng.srt, Name.spa.forced.srt.</summary>
public static class SubtitleSidecars
{
    private static readonly System.Text.RegularExpressions.Regex Tail = new(@"^(\.[A-Za-z0-9\-]{1,12}){0,3}\.(srt|ass|ssa|sub|idx|vtt|sup)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Subtitle files named after <paramref name="videoPath"/> (Name.eng.srt, Name.spa.forced.srt) in the same folder.</summary>
    public static List<string> FilesNextTo(string videoPath)
    {
        var result = new List<string>();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(videoPath)!;
            var stem = System.IO.Path.GetFileNameWithoutExtension(videoPath);
            foreach (var f in System.IO.Directory.GetFiles(dir, stem + ".*"))
                if (Tail.IsMatch(System.IO.Path.GetFileName(f).Substring(stem.Length))) result.Add(f);
        }
        catch { /* unreadable folder: nothing to report */ }

        return result;
    }

    /// <summary>The language code in a subtitle file name ("Name.eng.srt" -> "eng"), or "" when there is none.</summary>
    public static string LanguageOf(string subtitleFile, string videoStem)
    {
        var name = System.IO.Path.GetFileName(subtitleFile);
        if (!name.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        foreach (var tok in name.Substring(videoStem.Length).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = tok.ToLowerInvariant();
            if (t.Length == 3 && t.All(char.IsLetter) && t is not ("srt" or "ass" or "ssa" or "sub" or "idx" or "vtt" or "sup")) return t;
            if (t.Length == 2 && SubtitlePlanner.ParseLanguages(t).Count > 1) return SubtitlePlanner.ParseLanguages(t).First(x => x.Length == 3);
        }

        return string.Empty;
    }

    /// <summary>The files whose language the converted video still has (inside it, or as a subtitle file the plugin wrote).</summary>
    public static List<string> Covered(IEnumerable<string> files, string videoStem, IEnumerable<string> coveredLanguages)
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in coveredLanguages) foreach (var x in SubtitlePlanner.ParseLanguages(l)) have.Add(x);
        return files.Where(f => LanguageOf(f, videoStem) is { Length: > 0 } lang && have.Contains(lang)).ToList();
    }

    public static List<(SubStream Track, string Path)> Plan(string videoPath, IEnumerable<SubStream> tracks)
    {
        var dir = System.IO.Path.GetDirectoryName(videoPath) ?? string.Empty;
        var stem = System.IO.Path.GetFileNameWithoutExtension(videoPath);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(SubStream, string)>();
        foreach (var t in tracks)
        {
            var lang = string.IsNullOrWhiteSpace(t.Language) ? "und" : t.Language.ToLowerInvariant();
            var title = (t.Title ?? string.Empty).ToLowerInvariant();
            var tag = (t.Forced ? ".forced" : string.Empty) + (title.Contains("sdh") || title.Contains("hearing") ? ".sdh" : string.Empty);
            var name = stem + "." + lang + tag;
            var n = 1;
            var candidate = name;
            while (!used.Add(candidate)) candidate = name + "." + (++n);
            result.Add((t, System.IO.Path.Combine(dir, candidate + ".srt")));
        }

        return result;
    }
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

        // Separate subtitle files: text tracks are written next to the video; with "only" they are taken out of the file itself.
        var external = new List<SubStream>();
        if (hb.SubtitleExternal is "also" or "only")
        {
            external = keep.Where(s => IsText(s.Codec)).ToList();
            if (hb.SubtitleExternal == "only") keep = keep.Where(s => !IsText(s.Codec)).ToList();
        }

        var codecs = keep.Select(s => OutCodec(s.Codec, hb, mp4)!).ToList();
        var work = keep.Count != streams.Count || keep.Where((s, i) => !AlreadyRight(s.Codec, codecs[i])).Any();
        var forced = keep.Count(s => s.Forced);
        var sb = new StringBuilder();
        sb.Append(keep.Count).Append(" of ").Append(streams.Count).Append(hb.SubtitleExternal == "only" ? " subtitle tracks stay inside the file" : " subtitle tracks kept");
        if (langs.Count > 0) sb.Append(" (").Append(string.Join(", ", langs.Where(l => l.Length == 3))).Append(forced > 0 && hb.SubtitleKeepForced ? " + forced" : string.Empty).Append(')');
        if (codecs.Contains("srt")) sb.Append(", text as SRT");
        sb.Append(note);
        if (external.Count > 0) sb.Append(", ").Append(external.Count).Append(hb.SubtitleExternal == "only" ? " as separate .srt files only" : " also written as separate .srt files");
        return new SubPlan(keep, codecs, streams.Count, sb.ToString()) { NeedsWork = work, External = external };
    }
}

/// <summary>ffmpeg command lines that build the final file from the video/audio file and the chosen subtitle tracks.</summary>
public static class SubtitleMux
{
    /// <summary>Writes one subtitle track of <paramref name="src"/> as an SRT text file.</summary>
    public static List<string> ExtractArgs(string src, int subtitleIndex, string output) => new()
    {
        "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", src, "-map", $"0:s:{subtitleIndex}", "-c:s", "srt", "-f", "srt", output
    };

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
        // Attachments (fonts that styled ASS subtitles need, cover art) are MKV-only. A subtitle-only fix keeps the ones already in the file;
        // after an encode, fonts come along from the original when a styled track is copied as it is.
        if (!mp4)
        {
            var styled = plan.Keep.Where((t, i) => plan.OutCodecs[i] == "copy" && (t.Codec.Equals("ass", StringComparison.OrdinalIgnoreCase) || t.Codec.Equals("ssa", StringComparison.OrdinalIgnoreCase))).Any();
            if (subSource == null) { a.Add("-map"); a.Add("0:t?"); }
            else if (styled) { a.Add("-map"); a.Add($"{si}:t?"); }
        }

        a.Add("-map_chapters"); a.Add("0");
        a.Add("-c"); a.Add("copy");
        for (var i = 0; i < plan.Keep.Count; i++) { a.Add($"-c:s:{i}"); a.Add(plan.OutCodecs[i]); }
        // Nothing is switched on by default (that makes players such as Roku show or burn subtitles on their own); forced tracks stay forced.
        for (var i = 0; i < plan.Keep.Count; i++) { a.Add($"-disposition:s:{i}"); a.Add(plan.Keep[i].Forced ? "forced" : "0"); }
        a.Add("-f"); a.Add(mp4 ? "mp4" : "matroska");
        a.Add(output);
        return a;
    }
}
