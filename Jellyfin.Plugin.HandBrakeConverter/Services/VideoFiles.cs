using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

internal static class VideoFiles
{
    private static readonly string[] Extensions =
        { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".webm" };

    public static bool IsVideo(string? path) =>
        !string.IsNullOrEmpty(path) && Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
