using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>
/// Asks the installed HandBrake which video encoders it can use here (HandBrake only lists NVENC/QSV when it finds the hardware),
/// and can prove one works with a 2-second sample encode.
/// </summary>
public class HandBrakeCapabilities
{
    private static readonly Regex Name = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    private readonly IMediaEncoder _encoder;
    private readonly StateStore _state;
    private readonly ILogger<HandBrakeCapabilities> _log;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache = new();

    public HandBrakeCapabilities(IMediaEncoder encoder, StateStore state, ILogger<HandBrakeCapabilities> log)
    {
        _encoder = encoder;
        _state = state;
        _log = log;
    }

    /// <summary>Encoder names HandBrake lists for this machine; empty if they could not be read.</summary>
    public async Task<IReadOnlyList<string>> EncodersAsync(HandBrakeLocator.Launcher launcher, CancellationToken ct)
    {
        var key = launcher.FileName + "|" + string.Join(' ', launcher.PrefixArgs);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var (_, text) = await RunAsync(launcher, new[] { "--help" }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var list = ParseEncoders(text);
        if (list.Count > 0) _cache[key] = list; // don't remember a failed read
        return list;
    }

    /// <summary>Reads the indented names under "-e, --encoder" in <c>HandBrakeCLI --help</c>.</summary>
    public static IReadOnlyList<string> ParseEncoders(string help)
    {
        var lines = help.Replace("\r", string.Empty).Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("--encoder ", StringComparison.Ordinal) && l.Contains("<string>", StringComparison.Ordinal) && !l.Contains("preset", StringComparison.Ordinal) && !l.Contains("tune", StringComparison.Ordinal));
        var names = new List<string>();
        if (start < 0) return names;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('-') || t.Contains(' ')) break;
            names.Add(t);
        }

        return names;
    }

    /// <summary>Encodes a 2-second generated clip with <paramref name="encoder"/>. Used by the "Test" button on the HandBrake & tools tab.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(HandBrakeLocator.Launcher launcher, string encoder, CancellationToken ct)
    {
        if (!Name.IsMatch(encoder)) return (false, "Unknown encoder name.");
        var ffmpeg = FfmpegPath();
        if (ffmpeg == null) return (false, "Could not find ffmpeg (it comes with Jellyfin) to make the sample clip.");

        var dir = Path.Combine(_state.DataDir, "encoder-test");
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "sample.mkv");
        var output = Path.Combine(dir, "out.mkv");
        try
        {
            File.Delete(output);
            var (mk, mkText) = await RunAsync(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=d=2:s=1280x720:r=24", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "ultrafast", input },
                TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (mk != 0 || !File.Exists(input)) return (false, "Could not make the sample clip: " + Tail(mkText));

            var sw = Stopwatch.StartNew();
            var (code, text) = await RunAsync(launcher, new[] { "-i", input, "-o", output, "-f", "av_mkv", "-e", encoder, "-q", "28" }, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 1000)
                return (true, $"Works ({sw.Elapsed.TotalSeconds:0.0} s for a 2-second sample).");
            _log.LogWarning("Encoder test for {Encoder} failed (exit {Code}): {Text}", encoder, code, Tail(text));
            return (false, $"Failed (exit {code}): {Tail(text)}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private string? FfmpegPath()
    {
        try
        {
            var p = _encoder.EncoderPath;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        }
        catch { }

        return HandBrakeLocator.FindOnPath("ffmpeg");
    }

    private static string Tail(string text) =>
        string.Join(" | ", text.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)).Trim();

    private static Task<(int Code, string Output)> RunAsync(HandBrakeLocator.Launcher l, IEnumerable<string> args, TimeSpan limit, CancellationToken ct) =>
        RunAsync(l.FileName, l.PrefixArgs.Concat(args), l.Env, limit, ct);

    private static Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, TimeSpan limit, CancellationToken ct) =>
        RunAsync(exe, args, new Dictionary<string, string>(), limit, ct);

    private static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, IReadOnlyDictionary<string, string> env, TimeSpan limit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, string.Empty);
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(true); } catch { }
                return (-1, "Timed out.");
            }

            return (p.ExitCode, await o.ConfigureAwait(false) + await e.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
