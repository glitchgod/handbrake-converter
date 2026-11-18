using System;
using System.Diagnostics;
using System.IO;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>Small file helpers: spotting links that replacing would break, and giving a new file the old file's permissions.</summary>
internal static class FileMeta
{
    /// <summary>
    /// true when <paramref name="path"/> is a symbolic link or has more than one hard link (for example a download folder that seeds
    /// the same file). Replacing such a file would break the link and leave two full-size copies on disk.
    /// </summary>
    public static bool IsLinked(string path, out string how)
    {
        how = string.Empty;
        try
        {
            if (new FileInfo(path).LinkTarget != null) { how = "a symbolic link"; return true; }
        }
        catch { /* unreadable: treat as not linked */ }

        if (OperatingSystem.IsWindows()) return false;
        var count = Run("stat", new[] { "-c", "%h", path });
        if (int.TryParse(count?.Trim(), out var n) && n > 1) { how = $"hard-linked ({n} names point at it)"; return true; }
        return false;
    }

    /// <summary>Size of the file's data. For a symbolic link that is the size of the file it points to, not of the link itself.</summary>
    public static long Length(string path)
    {
        var fi = new FileInfo(path);
        try { if (fi.LinkTarget != null && fi.ResolveLinkTarget(true) is FileInfo real) return real.Length; } catch { }
        return fi.Length;
    }

    /// <summary>
    /// Gives <paramref name="target"/> the permissions and group of <paramref name="source"/> (best effort; the owner can only be
    /// copied when Jellyfin is allowed to). The modified date is deliberately NOT copied: Jellyfin uses it to notice that a file
    /// changed and needs its stream details refreshed.
    /// </summary>
    public static void CopyAccess(string source, string target)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(target, File.GetUnixFileMode(source)); } catch { }
        Run("chown", new[] { "--reference=" + source, target });
        Run("chgrp", new[] { "--reference=" + source, target });
    }

    private static string? Run(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var o = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? o.GetAwaiter().GetResult() : null;
        }
        catch { return null; }
    }
}
