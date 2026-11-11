using System.IO;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>The name shown for a video in the Queue: "Title (Year)" for movies, "Series - S01E05 - Episode title" for episodes.</summary>
public static class JobTitles
{
    public static string For(BaseItem item, string path)
    {
        var name = item.Name;
        var file = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) return file;

        var series = item.GetType().GetProperty("SeriesName")?.GetValue(item) as string;
        if (!string.IsNullOrWhiteSpace(series))
        {
            var season = item.GetType().GetProperty("ParentIndexNumber")?.GetValue(item) as int?;
            var ep = item.IndexNumber;
            var code = (season, ep) switch
            {
                ({ } s, { } e) => $"S{s:00}E{e:00}",
                (null, { } e) => $"E{e:00}",
                _ => string.Empty
            };
            return code.Length > 0 ? $"{series} - {code} - {name}" : $"{series} - {name}";
        }

        return item.ProductionYear is { } y && y > 0 ? $"{name} ({y})" : name;
    }
}
