using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>What ffprobe says about a video file.</summary>
public sealed record MediaInfo(
    double Duration, int AudioTracks, int SubtitleTracks, int Chapters,
    string VideoCodec, string PixelFormat, string Transfer, bool DolbyVision, int Height = 0, System.Collections.Generic.IReadOnlyList<SubStream>? Subs = null,
    System.Collections.Generic.IReadOnlyList<string>? AudioLangs = null, bool Interlaced = false)
{
    /// <summary>HDR10 / PQ or HLG.</summary>
    public bool IsHdr => Transfer is "smpte2084" or "arib-std-b67";

    public int BitDepth => PixelFormat.Contains("12", StringComparison.Ordinal) ? 12 : PixelFormat.Contains("10", StringComparison.Ordinal) ? 10 : 8;
}

/// <summary>Reads a file's tracks, chapters, duration and colour information with the ffprobe that Jellyfin itself uses.</summary>
public class MediaProbe
{
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<MediaProbe> _log;

    public MediaProbe(IMediaEncoder encoder, ILogger<MediaProbe> log)
    {
        _encoder = encoder;
        _log = log;
    }

    private string? ProbePath()
    {
        try
        {
            var p = _encoder.ProbePath;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        }
        catch { }

        return HandBrakeLocator.FindOnPath("ffprobe");
    }

    /// <summary>The ffmpeg Jellyfin itself uses (or one on the PATH); null when there is none.</summary>
    /// <summary>Tests only: pretend there is no ffmpeg.</summary>
    internal static bool NoFfmpeg { get; set; }

    public string? FfmpegPath()
    {
        if (NoFfmpeg) return null;
        try
        {
            var p = _encoder.EncoderPath;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        }
        catch { }

        return HandBrakeLocator.FindOnPath("ffmpeg");
    }

    /// <summary>null when ffprobe is missing, fails, or the file has no readable duration.</summary>
    public async Task<MediaInfo?> ProbeAsync(string file, CancellationToken ct)
    {
        var probe = ProbePath();
        if (probe == null) return null;
        try
        {
            var psi = new ProcessStartInfo(probe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-v", "error", "-show_streams", "-show_chapters", "-show_format", "-of", "json", file }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var o = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return p.ExitCode == 0 ? Parse(await o.ConfigureAwait(false)) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ffprobe failed for {File}", file);
            return null;
        }
    }

    /// <summary>Parses <c>ffprobe -of json -show_streams -show_chapters -show_format</c> output.</summary>
    public static MediaInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var fmt) || !fmt.TryGetProperty("duration", out var dur)
            || !double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            return null;

        var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : new();
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        int Count(string type) => streams.Count(x => Str(x, "codec_type") == type);

        var video = streams.FirstOrDefault(x => Str(x, "codec_type") == "video"
            && !(x.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1));
        var dolby = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("side_data_list", out var sd)
            && sd.EnumerateArray().Any(e => Str(e, "side_data_type").Contains("DOVI", StringComparison.OrdinalIgnoreCase));
        var chapters = root.TryGetProperty("chapters", out var c) ? c.GetArrayLength() : 0;
        var isVideo = video.ValueKind == JsonValueKind.Object;

        var subs = new System.Collections.Generic.List<SubStream>();
        foreach (var st in streams.Where(x => Str(x, "codec_type") == "subtitle"))
        {
            string tag(string n) => st.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Object
                ? tg.EnumerateObject().Where(pr => pr.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Select(pr => pr.Value.GetString() ?? string.Empty).FirstOrDefault() ?? string.Empty : string.Empty;
            var forcedFlag = st.TryGetProperty("disposition", out var dd) && dd.TryGetProperty("forced", out var fv) && fv.ValueKind == JsonValueKind.Number && fv.GetInt32() == 1;
            var title = tag("title");
            subs.Add(new SubStream(subs.Count, Str(st, "codec_name"), tag("language").ToLowerInvariant(), forcedFlag || title.Contains("forced", StringComparison.OrdinalIgnoreCase), title));
        }

        var audioLangs = streams.Where(x => Str(x, "codec_type") == "audio").Select(st =>
            st.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Object
                ? tg.EnumerateObject().Where(pr => pr.Name.Equals("language", StringComparison.OrdinalIgnoreCase)).Select(pr => (pr.Value.GetString() ?? string.Empty).ToLowerInvariant()).FirstOrDefault() ?? string.Empty
                : string.Empty).ToList();
        // field_order is tt/bb/tb/bt for interlaced video; progressive (or missing/unknown) means a normal picture.
        var fieldOrder = isVideo ? Str(video, "field_order") : string.Empty;
        var interlaced = fieldOrder is "tt" or "bb" or "tb" or "bt";

        return new MediaInfo(seconds, Count("audio"), Count("subtitle"), chapters,
            isVideo ? Str(video, "codec_name") : string.Empty, isVideo ? Str(video, "pix_fmt") : string.Empty, isVideo ? Str(video, "color_transfer") : string.Empty, dolby,
            isVideo && video.TryGetProperty("height", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 0, subs, audioLangs, interlaced);
    }
}
