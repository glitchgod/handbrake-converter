using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>One style of an ASS/SSA track: the look of a kind of line (dialogue, signs, songs...).</summary>
public sealed record AssStyle(string Name, string Font, double Size, string Line);

/// <summary>The parts of an ASS/SSA track that decide how it looks.</summary>
public sealed record AssLook(int PlayResX, int PlayResY, IReadOnlyList<AssStyle> Styles, int Events)
{
    /// <summary>Fonts the styles ask for, plus fonts named inside lines ({\fnName}).</summary>
    public IReadOnlyList<string> Fonts { get; init; } = Array.Empty<string>();
}

/// <summary>Cue count and the first/last start time of a text track (as SRT).</summary>
public sealed record TextCues(int Count, TimeSpan First, TimeSpan Last);

/// <summary>
/// Compares a subtitle track before and after conversion, so a result whose subtitles lost their styling, size, lines or timing is
/// never used. The tracks are dumped by ffmpeg: ASS as it is (to compare styles), other text as SRT (to compare lines and timing),
/// PGS as .sup (copied pictures must be byte for byte the same size).
/// </summary>
public static class SubtitleCheck
{
    private static readonly Regex Time = new(@"(\d+):(\d\d):(\d\d)[,.](\d{1,3})\s*-->", RegexOptions.Compiled);
    private static readonly Regex InlineFont = new(@"\\fn([^\\}]+)", RegexOptions.Compiled);

    /// <summary>How a track is dumped for comparing: "ass", "sup", "srt", or "" (VobSub and others: not compared).</summary>
    public static string DumpFormat(string codec, string outCodec)
    {
        if (outCodec == "copy" && SubtitlePlanner.IsStyled(codec)) return "ass";
        if (SubtitlePlanner.IsText(codec)) return "srt";
        if (outCodec == "copy" && SubtitlePlanner.IsPgs(codec)) return "sup";
        return string.Empty;
    }

    /// <summary>One ffmpeg run that writes each listed subtitle track of <paramref name="src"/> to its own file.</summary>
    public static List<string> DumpArgs(string src, IEnumerable<(int Index, string Format, string Output)> tracks, double? seconds = null)
    {
        var a = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", src };
        foreach (var (index, format, output) in tracks)
        {
            if (seconds != null) { a.Add("-t"); a.Add(seconds.Value.ToString(CultureInfo.InvariantCulture)); }
            a.Add("-map"); a.Add($"0:s:{index}");
            // assx: decode to ASS (MOV_TEXT -> ASS shows the canvas and font size players and Jellyfin will draw it with)
            a.Add("-c:s"); a.Add(format switch { "srt" => "srt", "assx" => "ass", _ => "copy" });
            a.Add("-f"); a.Add(format == "assx" ? "ass" : format);
            a.Add(output);
        }

        return a;
    }

    public static AssLook ParseAss(string text)
    {
        int resX = 0, resY = 0, events = 0;
        var styles = new List<AssStyle>();
        var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[]? format = null;
        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[')) { section = line.ToLowerInvariant(); format = null; continue; }
            if (section == "[script info]")
            {
                if (line.StartsWith("PlayResX:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[9..].Trim(), out resX);
                else if (line.StartsWith("PlayResY:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[9..].Trim(), out resY);
            }
            else if (section.Contains("styles", StringComparison.Ordinal))
            {
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) format = line[7..].Split(',').Select(x => x.Trim().ToLowerInvariant()).ToArray();
                else if (line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line[6..].Split(',').Select(x => x.Trim()).ToArray();
                    string Field(string n, int fallback) { var i = format != null ? Array.IndexOf(format, n) : fallback; return i >= 0 && i < v.Length ? v[i] : string.Empty; }
                    double.TryParse(Field("fontsize", 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var size);
                    var font = Field("fontname", 1);
                    styles.Add(new AssStyle(Field("name", 0), font, size, string.Join(",", v)));
                    if (font.Length > 0) fonts.Add(font.TrimStart('@'));
                }
            }
            else if (section == "[events]" && line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                events++;
                foreach (Match m in InlineFont.Matches(line)) fonts.Add(m.Groups[1].Value.Trim().TrimStart('@'));
            }
        }

        return new AssLook(resX, resY, styles, events) { Fonts = fonts.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() };
    }

    private static readonly Regex Markup = new(@"<[^>]*>|\{\\[^}]*\}", RegexOptions.Compiled);

    /// <summary>Lines that show something (cues left empty once tags are removed, such as drawings from styled subtitles, don't count).</summary>
    public static TextCues ParseSrt(string text)
    {
        var times = new List<TimeSpan>();
        foreach (var block in Regex.Split(text.Replace("\r", string.Empty, StringComparison.Ordinal), @"\n\s*\n"))
        {
            var m = Time.Match(block);
            if (!m.Success) continue;
            var body = block[(block.IndexOf('\n', m.Index) is var nl && nl >= 0 ? nl + 1 : block.Length)..];
            if (Markup.Replace(body, string.Empty).Trim().Length == 0) continue;
            times.Add(new TimeSpan(0, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture)));
        }

        return times.Count == 0 ? new TextCues(0, TimeSpan.Zero, TimeSpan.Zero) : new TextCues(times.Count, times.Min(), times.Max());
    }

    private static readonly Regex FontTag = new(@"</?font\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SizedFont = new(@"<font\b[^>]*\bsize\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Plain text has no business carrying a font: ffmpeg copies the font name, size and colour of styled or MOV_TEXT subtitles into SRT
    /// as &lt;font&gt; tags, and players that honour them draw the text giant. Removing them leaves the text at the size set in Jellyfin;
    /// italics, bold and underline stay.
    /// </summary>
    public static string CleanSrt(string text) => FontTag.Replace(text, string.Empty);

    /// <summary>SRT text with font size tags (from older conversions), which some players draw giant.</summary>
    public static bool HasSizeTags(string text) => SizedFont.IsMatch(text);

    /// <summary>Line height as a share of the picture height: the biggest style font size over the canvas height (ASS default 288).</summary>
    public static double Scale(AssLook look) =>
        look.Styles.Count == 0 ? 0 : look.Styles.Max(s => s.Size) / (look.PlayResY > 0 ? look.PlayResY : 288.0);

    /// <summary>null when the styled track looks the same; otherwise what changed.</summary>
    public static string? CompareAss(AssLook before, AssLook after)
    {
        if (before.PlayResX != after.PlayResX || before.PlayResY != after.PlayResY)
            return $"its canvas size changed ({before.PlayResX}x{before.PlayResY} → {after.PlayResX}x{after.PlayResY}), so text would be drawn at a different size";
        foreach (var s in before.Styles)
        {
            var m = after.Styles.FirstOrDefault(x => x.Name == s.Name);
            if (m == null) return $"style \"{s.Name}\" is missing";
            if (m.Font != s.Font || Math.Abs(m.Size - s.Size) > 0.01) return $"style \"{s.Name}\" changed ({s.Font} {s.Size:0.#} → {m.Font} {m.Size:0.#})";
            if (m.Line != s.Line) return $"style \"{s.Name}\" changed (colour, outline or position)";
        }

        if (after.Styles.Count != before.Styles.Count) return $"it has {after.Styles.Count} styles instead of {before.Styles.Count}";
        if (after.Events != before.Events) return $"it has {after.Events} lines instead of {before.Events}";
        return null;
    }

    /// <summary>null when the text track has the same lines at the same times; otherwise what changed.</summary>
    public static string? CompareText(TextCues before, TextCues after)
    {
        if (after.Count != before.Count) return $"it has {after.Count} lines instead of {before.Count}";
        if (before.Count > 0 && (Math.Abs((after.First - before.First).TotalSeconds) > 0.5 || Math.Abs((after.Last - before.Last).TotalSeconds) > 0.5))
            return $"its timing moved (first line {before.First:hh\\:mm\\:ss} → {after.First:hh\\:mm\\:ss}, last {before.Last:hh\\:mm\\:ss} → {after.Last:hh\\:mm\\:ss})";
        return null;
    }
}

/// <summary>Tools > Check a title: one subtitle track, how it looks now and what the chosen settings would do with it.</summary>
public sealed record SubtitleReportTrack(int Number, string Kind, string Codec, string Language, string Title, bool Forced, string Outcome, string Look,
    AssLook? Styled, IReadOnlyList<string> MissingFonts);

/// <summary>One playback of the title: when, which app, how it played and why Jellyfin had to convert it.</summary>
public sealed record PlayRow(DateTime Time, string Client, string How, string Why);

/// <summary>
/// Tools > Check a title: the file as it is (video, audio, subtitle tracks with their styles and sizes), what the chosen settings would
/// make of each part, and how the title has played so far.
/// </summary>
public sealed record TitleReport(string File, string Container, string Video, string VideoAfter, IReadOnlyList<string> Audio, string Summary,
    IReadOnlyList<SubtitleReportTrack> Tracks, IReadOnlyList<string> Attachments, string Note)
{
    public IReadOnlyList<PlayRow> Plays { get; init; } = Array.Empty<PlayRow>();
}
