using System;
using System.Globalization;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

public static class ScheduleEvaluator
{
    public static bool TryParseTime(string? s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s ?? string.Empty, new[] { "hh\\:mm", "h\\:mm" }, CultureInfo.InvariantCulture, out t);

    public static bool InBlackout(DateTime now, BlackoutRange[] ranges)
    {
        foreach (var r in ranges)
        {
            if (!DateTime.TryParseExact(r.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) continue;
            if (!DateTime.TryParseExact(r.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) to = from;
            if (now.Date >= from.Date && now.Date <= to.Date) return true;
        }

        return false;
    }

    /// <summary>True if <paramref name="now"/> is inside any window and not in a blackout. A window belongs to the day it starts on.</summary>
    public static bool IsAllowed(DateTime now, PluginConfiguration cfg)
    {
        if (!cfg.ScheduleEnabled) return true; // not time-limited: always allowed (blackout dates are part of the time limits)
        if (InBlackout(now, cfg.Blackouts)) return false;

        var tod = now.TimeOfDay;
        var today = (int)now.DayOfWeek;
        var yesterday = (int)now.AddDays(-1).DayOfWeek;

        foreach (var w in cfg.Windows)
        {
            if (!TryParseTime(w.Start, out var start) || !TryParseTime(w.End, out var end)) continue;

            if (start == end)
            {
                if (Array.IndexOf(w.Days, today) >= 0) return true;
            }
            else if (start < end)
            {
                if (Array.IndexOf(w.Days, today) >= 0 && tod >= start && tod < end) return true;
            }
            else
            {
                // crosses midnight
                if (Array.IndexOf(w.Days, today) >= 0 && tod >= start) return true;
                if (Array.IndexOf(w.Days, yesterday) >= 0 && tod < end) return true;
            }
        }

        return false;
    }
}
