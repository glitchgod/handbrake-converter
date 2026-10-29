using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>One running HandBrakeCLI process: progress parsing, a tail of its output for error messages, suspend/resume/kill.</summary>
internal sealed class HandBrakeRun : IDisposable
{
    private static readonly Regex Percent = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex Eta = new(@"ETA\s+(\S+)", RegexOptions.Compiled);

    private readonly Process _proc;
    private readonly Queue<string> _tail = new();
    private readonly Task[] _readers;
    private readonly Action<double, string?> _onProgress;

    private HandBrakeRun(Process proc, Action<double, string?> onProgress)
    {
        _proc = proc;
        _onProgress = onProgress;
        _readers = new[] { Task.Run(() => PumpAsync(proc.StandardOutput)), Task.Run(() => PumpAsync(proc.StandardError)) };
    }

    /// <summary>Starts HandBrake (optionally under <c>nice</c>). Throws if the process can't be started.</summary>
    public static HandBrakeRun Start(HandBrakeLocator.Launcher launcher, IEnumerable<string> args, bool lowPriority, Action<double, string?> onProgress)
    {
        var psi = new ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (lowPriority && File.Exists("/usr/bin/nice"))
        {
            psi.FileName = "/usr/bin/nice";
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("10");
            psi.ArgumentList.Add(launcher.FileName);
        }
        else
        {
            psi.FileName = launcher.FileName;
        }

        foreach (var a in launcher.PrefixArgs.Concat(args)) psi.ArgumentList.Add(a);
        foreach (var kv in launcher.Env) psi.Environment[kv.Key] = kv.Value;

        var proc = new Process { StartInfo = psi };
        proc.Start();
        return new HandBrakeRun(proc, onProgress);
    }

    public bool HasExited => _proc.HasExited;

    public void Suspend() => ProcessControl.Suspend(_proc.Id);

    public void Resume() => ProcessControl.Resume(_proc.Id);

    public async Task KillAsync()
    {
        ProcessControl.Kill(_proc.Id);
        await _proc.WaitForExitAsync().ConfigureAwait(false);
    }

    /// <summary>Waits for the process and for all its output to be read; returns the exit code.</summary>
    public async Task<int> WaitAsync()
    {
        await _proc.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(_readers).ConfigureAwait(false);
        return _proc.ExitCode;
    }

    public string LastLines(int count)
    {
        lock (_tail) return string.Join(" | ", _tail.TakeLast(count));
    }

    public string AllOutput()
    {
        lock (_tail) return string.Join("\n", _tail);
    }

    public void Dispose() => _proc.Dispose();

    /// <summary>HandBrake redraws its progress line with carriage returns, so split on both \r and \n.</summary>
    private async Task PumpAsync(StreamReader reader)
    {
        var line = new StringBuilder();
        var ch = new char[1];
        while (await reader.ReadAsync(ch, 0, 1).ConfigureAwait(false) > 0)
        {
            if (ch[0] is not ('\r' or '\n')) { line.Append(ch[0]); continue; }
            if (line.Length == 0) continue;
            Handle(line.ToString());
            line.Clear();
        }

        if (line.Length > 0) Handle(line.ToString());
    }

    private void Handle(string line)
    {
        if (line.StartsWith("Encoding:", StringComparison.Ordinal))
        {
            var p = Percent.Match(line);
            if (p.Success && double.TryParse(p.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                var e = Eta.Match(line);
                _onProgress(pct, e.Success ? e.Groups[1].Value : null);
            }

            return;
        }

        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > 25) _tail.Dequeue();
        }
    }
}
