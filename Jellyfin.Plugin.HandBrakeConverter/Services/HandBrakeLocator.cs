using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>
/// Finds HandBrakeCLI on the server. The plugin never installs anything: HandBrake comes from the system
/// (<c>sudo apt install handbrake-cli</c>) or from a path set on the Overview tab.
/// </summary>
public class HandBrakeLocator
{
    public const string Exe = "HandBrakeCLI";

    /// <summary>How to start HandBrake: program, arguments that go before ours, extra environment, and where it was found.</summary>
    public record Launcher(string FileName, string[] PrefixArgs, IReadOnlyDictionary<string, string> Env, string Description);

    /// <summary>Searched after PATH (a service's PATH is often minimal). Settable so tests can simulate "not installed".</summary>
    public static string[] FallbackDirs { get; set; } = { "/usr/bin", "/usr/local/bin", "/bin" };

    public static string? FindOnPath(string exe)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(FallbackDirs);
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, exe);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    /// <summary>Order: the path set on the Overview tab, then HandBrakeCLI on the server's PATH. Cheap (no processes spawned).</summary>
    public Launcher? Resolve(string configuredPath)
    {
        var empty = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return new Launcher(configuredPath, Array.Empty<string>(), empty, "path set in the plugin");

        var direct = FindOnPath(Exe);
        return direct != null ? new Launcher(direct, Array.Empty<string>(), empty, "installed on this server") : null;
    }

    /// <summary>Runs <c>--version</c>; null if it doesn't run.</summary>
    public static async Task<string?> VersionAsync(Launcher l, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(l.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var a in l.PrefixArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--version");
            foreach (var kv in l.Env) psi.Environment[kv.Key] = kv.Value;

            using var p = Process.Start(psi);
            if (p == null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var text = (await outTask.ConfigureAwait(false) + await errTask.ConfigureAwait(false)).Trim();
            var line = text.Split('\n').FirstOrDefault(x => x.Contains("HandBrake", StringComparison.OrdinalIgnoreCase));
            return p.ExitCode == 0 && line != null ? line.Trim() : null;
        }
        catch { return null; }
    }
}
