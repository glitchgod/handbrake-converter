using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>One subtitle track of a file. <see cref="Index"/> counts subtitle tracks only (0 = the first one).</summary>
/// <param name="TextScale">MOV_TEXT only: line height as a share of the picture height (font size / canvas height) when a player or Jellyfin
/// draws it; about 0.05 is normal, 0 = not measured.</param>
/// <param name="SizeTags">SRT only: the text carries font size tags (made by older versions), which some players draw giant.</param>
public sealed record SubStream(int Index, string Codec, string Language, bool Forced, string Title, double TextScale = 0, bool SizeTags = false)
{
    /// <summary>MOV_TEXT whose font size doesn't fit its canvas, or SRT with font size tags: drawn giant by some or all players.</summary>
    public bool Giant => TextScale > 0.1 || SizeTags;
}

/// <summary>A subtitle track written as its own file next to the video. Format: srt (plain text), ass (styled, exactly as it was) or sup (PGS pictures, exactly as they were).</summary>
public sealed record Sidecar(SubStream Track, string Format);

/// <summary>What to do with the subtitle tracks of one file.</summary>
public sealed record SubPlan(IReadOnlyList<SubStream> Keep, IReadOnlyList<string> OutCodecs, int Total, string Summary)
{
    /// <summary>True when the file's tracks are not already exactly what the plan wants (so a fix is worth doing).</summary>
    public bool NeedsWork { get; init; }

    /// <summary>Tracks that are also (or only) written as separate files next to the video.</summary>
    public IReadOnlyList<Sidecar> External { get; init; } = Array.Empty<Sidecar>();
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

    public static List<(SubStream Track, string Path)> Plan(string videoPath, IEnumerable<Sidecar> sidecars)
    {
        var dir = System.IO.Path.GetDirectoryName(videoPath) ?? string.Empty;
        var stem = System.IO.Path.GetFileNameWithoutExtension(videoPath);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(SubStream, string)>();
        foreach (var sc in sidecars)
        {
            var t = sc.Track;
            var ext = "." + sc.Format;
            var lang = string.IsNullOrWhiteSpace(t.Language) ? "und" : t.Language.ToLowerInvariant();
            var title = (t.Title ?? string.Empty).ToLowerInvariant();
            var tag = (t.Forced ? ".forced" : string.Empty) + (title.Contains("sdh") || title.Contains("hearing") ? ".sdh" : string.Empty);
            var name = stem + "." + lang + tag;
            var n = 1;
            var candidate = name;
            while (!used.Add(candidate + ext)) candidate = name + "." + (++n);
            result.Add((t, System.IO.Path.Combine(dir, candidate + ext)));
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

    /// <summary>ASS/SSA: subtitles with their own fonts, sizes, colours and positions.</summary>
    public static bool IsStyled(string codec) => codec is not null && (codec.Equals("ass", StringComparison.OrdinalIgnoreCase) || codec.Equals("ssa", StringComparison.OrdinalIgnoreCase));

    /// <summary>MP4's own text subtitle format.</summary>
    public static bool IsMovText(string codec) => codec is not null && (codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase) || codec.Equals("tx3g", StringComparison.OrdinalIgnoreCase));

    /// <summary>Blu-ray picture subtitles, which can be written as a .sup file exactly as they are.</summary>
    public static bool IsPgs(string codec) => codec is "hdmv_pgs_subtitle" or "pgssub" or "pgs";

    /// <summary>Styled tracks stay styled (inside MKV, or as a .ass file next to an MP4).</summary>
    private static bool KeepStyled(string codec, HandBrakeSettings hb) => IsStyled(codec) && hb.SubtitleStyled != "srt";

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
        if (KeepStyled(codec, hb)) return mp4 ? null : "copy";
        // MKV cannot hold MOV_TEXT (an MP4-only format), so those are always turned into SRT, even when "leave as they are" is chosen.
        if (IsText(codec)) return mp4 ? (IsMovText(codec) ? "copy" : "mov_text") : hb.SubtitleFormat == "asis" && !codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase) && !codec.Equals("tx3g", StringComparison.OrdinalIgnoreCase) ? "copy" : "srt";
        return mp4 ? null : "copy"; // picture subtitles (PGS, VobSub) can't be turned into text and don't fit in MP4
    }

    private static bool AlreadyRight(string codec, string outCodec) =>
        outCodec == "copy" || (outCodec == "srt" && codec.Equals("subrip", StringComparison.OrdinalIgnoreCase)) || (outCodec == "mov_text" && codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase));

    /// <summary>null when subtitles are not being kept as tracks (burned in / removed / none in the file).</summary>
    /// <param name="audioLanguages">languages of the audio tracks the file will keep. A forced track (the lines for foreign-language
    /// parts) only belongs to audio in its own language, so forced tracks for audio that isn't there are dropped. null = keep all forced.</param>
    public static SubPlan? Make(IReadOnlyList<SubStream>? streams, HandBrakeSettings hb, bool mp4, IEnumerable<string>? audioLanguages = null)
    {
        if (streams == null || streams.Count == 0 || hb.SubtitleMode != "keep") return null;

        var langs = ParseLanguages(hb.SubtitleLanguages);
        // Everything that can be kept somewhere: inside the file, or (MP4) as a separate file that keeps its exact look.
        bool Placeable(SubStream t) => OutCodec(t.Codec, hb, mp4) != null || (mp4 && (KeepStyled(t.Codec, hb) || IsPgs(t.Codec)));
        var dropped = streams.Where(t => !Placeable(t)).ToList();
        var keep = streams.Where(Placeable).ToList();
        var note = string.Empty;
        if (langs.Count > 0)
        {
            var audio = audioLanguages == null ? null : new HashSet<string>(audioLanguages.SelectMany(l => ParseLanguages(l)), StringComparer.OrdinalIgnoreCase);
            bool ForcedFits(SubStream s) => audio == null || IsUndetermined(s.Language.ToLowerInvariant()) || audio.Count == 0 || audio.Contains(s.Language.ToLowerInvariant());
            var wanted = keep.Where(s => langs.Contains(s.Language.ToLowerInvariant()) || (hb.SubtitleKeepForced && s.Forced && ForcedFits(s))).ToList();
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

        // MP4 can only store plain text subtitles as MOV_TEXT, which many players can't show without Jellyfin converting the stream, and
        // ffmpeg's MP4 writer always marks the first subtitle track as default (so players switch it on by themselves). When SRT is
        // chosen, the plain text tracks of an MP4 are therefore written as separate .srt files and taken out of the file.
        bool Plain(SubStream t) => IsText(t.Codec) && !KeepStyled(t.Codec, hb);
        var externalMode = hb.SubtitleExternal;
        var mp4Sidecars = mp4 && hb.SubtitleFormat == "srt" && externalMode is not ("also" or "only") && keep.Any(Plain);
        if (mp4Sidecars) externalMode = "only";

        var inside = new List<SubStream>();
        var external = new List<Sidecar>();
        foreach (var t in keep)
        {
            if (KeepStyled(t.Codec, hb))
            {
                // Styled tracks keep their exact look: inside MKV (with their fonts), as a .ass file next to an MP4. The plain .srt copy
                // is extra, for players that can't draw styled subtitles. "Separate subtitle files" doesn't apply: it would lose the styling.
                if (mp4) external.Add(new Sidecar(t, "ass"));
                else inside.Add(t);
                if (hb.SubtitleStyled == "keep_srt") external.Add(new Sidecar(t, "srt"));
            }
            else if (!IsText(t.Codec))
            {
                // Picture subtitles (PGS, VobSub) are kept exactly as they are; MP4 can't hold them, so PGS becomes a .sup file next to it.
                if (mp4) external.Add(new Sidecar(t, "sup"));
                else inside.Add(t);
            }
            else
            {
                // New MOV_TEXT is never made: converting other text to it keeps the font size but not the canvas it belonged to, so the
                // text comes out giant when a player or Jellyfin draws it. The rule is the same for every file (nothing is decided by
                // measuring a track), so a series never ends up with some episodes drawn one size and others another: with SRT every plain
                // text track becomes SRT, drawn by the player at its own size; with "leave as they are" MOV_TEXT stays exactly as it was.
                var fileOnly = externalMode == "only" || (mp4 && !IsMovText(t.Codec));
                if (externalMode is "also" or "only" || fileOnly) external.Add(new Sidecar(t, "srt"));
                if (!fileOnly) inside.Add(t);
            }
        }

        // ffmpeg's MP4 writer switches the first subtitle track on when none is marked default. Putting forced tracks first means the
        // one that ends up switched on is the forced track, which players would show anyway.
        if (mp4) inside = inside.OrderByDescending(s => s.Forced).ToList();

        var codecs = inside.Select(s => OutCodec(s.Codec, hb, mp4)!).ToList();
        // SRT that carries font size tags is rewritten without them (see SubtitleCheck.CleanSrt), even though it is already SRT.
        var work = inside.Count != streams.Count || inside.Where((s, i) => !AlreadyRight(s.Codec, codecs[i]) || (s.SizeTags && codecs[i] == "srt")).Any();
        var forced = keep.Count(s => s.Forced);
        var sb = new StringBuilder();
        sb.Append(inside.Count).Append(" of ").Append(streams.Count).Append(" subtitle tracks kept inside the file");
        if (langs.Count > 0) sb.Append(" (").Append(string.Join(", ", langs.Where(l => l.Length == 3))).Append(forced > 0 && hb.SubtitleKeepForced ? " + forced" : string.Empty).Append(')');
        if (inside.Where((t, i) => codecs[i] == "copy" && IsStyled(t.Codec)).Any()) sb.Append(", styled subtitles exactly as they are");
        if (codecs.Contains("srt")) sb.Append(", text as SRT");
        sb.Append(note);
        if (external.Count > 0)
            sb.Append(", ").Append(external.Count).Append(" as separate files (").Append(string.Join(", ", external.Select(x => "." + x.Format).Distinct())).Append(')');
        if (mp4Sidecars) sb.Append(" (MP4 can't hold SRT)");
        var giant = inside.Count(t => t.TextScale > 0.1);
        if (giant > 0) sb.Append(", ").Append(giant).Append(" MOV_TEXT track(s) kept as they are although their text is drawn giant (choose SRT for plain text subtitles to fix that)");
        if (dropped.Count > 0) sb.Append(", ").Append(dropped.Count).Append(" picture track(s) dropped (").Append(string.Join(", ", dropped.Select(d => d.Codec).Distinct())).Append(" can't go in MP4 or a separate file)");
        return new SubPlan(inside, codecs, streams.Count, sb.ToString()) { NeedsWork = work, External = external };
    }
}

/// <summary>ffmpeg command lines that build the final file from the video/audio file and the chosen subtitle tracks.</summary>
public static class SubtitleMux
{
    /// <summary>
    /// Writes one subtitle track of <paramref name="src"/> as its own file: srt = converted to plain SRT; ass / sup = copied exactly as it is
    /// (styles, fonts sizes, positions, or the original pictures).
    /// </summary>
    public static List<string> ExtractArgs(string src, int subtitleIndex, string output, string format = "srt") => new()
    {
        "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", src, "-map", $"0:s:{subtitleIndex}",
        "-c:s", format is "ass" or "sup" ? "copy" : "srt", "-f", format is "ass" or "sup" ? format : "srt", output
    };

    /// <summary>
    /// <paramref name="main"/> supplies video, audio and chapters. The subtitle tracks come from <paramref name="subSource"/>
    /// (the original file after a HandBrake encode) or from <paramref name="main"/> itself (a subtitle-only fix, subSource = null).
    /// </summary>
    /// <param name="cleaned">output subtitle number -> an SRT file to use for that track instead (the track written as SRT with its
    /// font tags removed, see SubtitleCheck.CleanSrt).</param>
    public static List<string> Args(string main, string? subSource, SubPlan plan, bool mp4, string output, IReadOnlyDictionary<int, string>? cleaned = null)
    {
        var a = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", main };
        if (subSource != null) { a.Add("-i"); a.Add(subSource); }
        var si = subSource != null ? 1 : 0;
        var extra = new Dictionary<int, int>(); // output subtitle number -> input number of its cleaned file
        if (cleaned != null)
            foreach (var (i, file) in cleaned.OrderBy(x => x.Key)) { a.Add("-i"); a.Add(file); extra[i] = si + 1 + extra.Count; }
        a.Add("-map"); a.Add("0:v"); a.Add("-map"); a.Add("0:a?");
        for (var i = 0; i < plan.Keep.Count; i++) { a.Add("-map"); a.Add(extra.TryGetValue(i, out var input) ? $"{input}:s:0" : $"{si}:s:{plan.Keep[i].Index}"); }
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
        for (var i = 0; i < plan.Keep.Count; i++)
        {
            a.Add($"-c:s:{i}"); a.Add(extra.ContainsKey(i) ? "copy" : plan.OutCodecs[i]);
            if (!extra.ContainsKey(i)) continue;
            // a separate file carries no language or title: give the track the original's
            a.Add($"-metadata:s:s:{i}"); a.Add("language=" + (plan.Keep[i].Language.Length > 0 ? plan.Keep[i].Language : "und"));
            if (plan.Keep[i].Title.Length > 0) { a.Add($"-metadata:s:s:{i}"); a.Add("title=" + plan.Keep[i].Title); }
        }

        // Nothing is switched on by default (that makes many players show, or ask Jellyfin to burn in, subtitles on their own); forced tracks stay forced.
        for (var i = 0; i < plan.Keep.Count; i++) { a.Add($"-disposition:s:{i}"); a.Add(plan.Keep[i].Forced ? "forced" : "0"); }
        a.Add("-f"); a.Add(mp4 ? "mp4" : "matroska");
        a.Add(output);
        return a;
    }
}
