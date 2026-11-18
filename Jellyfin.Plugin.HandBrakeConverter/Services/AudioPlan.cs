using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>Picks which audio tracks to keep by language. Pure logic; the tracks come from ffprobe.</summary>
public static class AudioPlanner
{
    private static bool IsUndetermined(string lang) => lang is "" or "und" or "unk" or "mis" or "zxx";

    /// <summary>
    /// 1-based numbers of the audio tracks to keep (HandBrake counts audio tracks from 1), or null to leave the tracks alone:
    /// no filter set, no track information, nothing would be removed, or no track matches (never strip a file of all its sound).
    /// Tracks with no language tag are kept.
    /// </summary>
    public static int[]? Select(IReadOnlyList<string>? trackLanguages, string? wanted)
    {
        if (trackLanguages == null || trackLanguages.Count == 0) return null;
        var langs = SubtitlePlanner.ParseLanguages(wanted);
        if (langs.Count == 0) return null;

        var keep = new List<int>();
        for (var i = 0; i < trackLanguages.Count; i++)
        {
            var l = (trackLanguages[i] ?? string.Empty).ToLowerInvariant();
            if (IsUndetermined(l) || langs.Contains(l)) keep.Add(i + 1);
        }

        if (keep.Count == 0 || keep.Count == trackLanguages.Count) return null;
        // If everything that matched is an unlabelled track, the filter has found nothing it actually asked for: leave the file alone.
        if (keep.All(n => IsUndetermined((trackLanguages[n - 1] ?? string.Empty).ToLowerInvariant()))) return null;
        return keep.ToArray();
    }
}
