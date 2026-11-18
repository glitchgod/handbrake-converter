using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

internal static class VideoFiles
{
    private static readonly string[] Extensions =
        { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".webm" };

    /// <summary>Files Jellyfin lists as video that HandBrake cannot convert: stream links and disc images / folders.</summary>
    public static bool Unsupported(string? path, out string why)
    {
        why = string.Empty;
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var norm = path.Replace('\\', '/');
        if (ext == ".strm") why = "this is a stream link (.strm), not a video file";
        else if (ext is ".iso" or ".img" or ".bin") why = "this is a disc image";
        else if (norm.Contains("/BDMV/", StringComparison.OrdinalIgnoreCase) || norm.Contains("/VIDEO_TS/", StringComparison.OrdinalIgnoreCase)) why = "this is part of a disc folder (BDMV / VIDEO_TS)";
        return why.Length > 0;
    }

    public static bool IsVideo(string? path) =>
        !string.IsNullOrEmpty(path) && Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
