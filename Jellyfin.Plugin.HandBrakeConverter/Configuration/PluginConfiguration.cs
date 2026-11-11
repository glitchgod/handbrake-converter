using System;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.HandBrakeConverter.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Master switch. Off = nothing runs (auto or manual).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Settings used by every library that doesn't have custom settings, and as the starting point for manual conversions.</summary>
    public ConversionProfile Defaults { get; set; } = new();

    public LibrarySettings[] Libraries { get; set; } = Array.Empty<LibrarySettings>();

    /// <summary>Optional absolute path to an existing HandBrakeCLI. Empty = look for HandBrakeCLI on the server (apt install handbrake-cli).</summary>
    public string HandBrakePath { get; set; } = string.Empty;

    // ---- Schedule (applies to auto-processing and to queued jobs that don't say "run now") ----
    public bool ScheduleEnabled { get; set; } = true;
    public ScheduleWindow[] Windows { get; set; } = new[]
    {
        new ScheduleWindow { Days = new[] { 0, 1, 2, 3, 4, 5, 6 }, Start = "23:00", End = "10:00" }
    };
    public BlackoutRange[] Blackouts { get; set; } = Array.Empty<BlackoutRange>();

    /// <summary>Suspend the encode (SIGSTOP) while anyone is playing something.</summary>
    public bool PauseWhileStreaming { get; set; } = true;

    /// <summary>Count paused playback sessions as streaming too.</summary>
    public bool PausedCountsAsStreaming { get; set; } = false;

    /// <summary>Don't treat audio-only playback (music) as streaming.</summary>
    public bool IgnoreAudioPlayback { get; set; } = true;

    /// <summary>When the window closes mid-encode: true = let it finish, false = cancel and retry next window.</summary>
    public bool FinishCurrentAfterWindow { get; set; } = true;

    /// <summary>Run HandBrake at low CPU priority (nice 10).</summary>
    public bool LowPriority { get; set; } = true;

    /// <summary>Failures per file before auto-processing skips it permanently.</summary>
    public int MaxFailures { get; set; } = 2;

    // ---- Original files ----
    /// <summary>Auto-convert only: skip files smaller than this (MB). 0 = no limit.</summary>
    public int MinSizeMb { get; set; } = 0;

    /// <summary>Replaced originals are kept in the holding folder this many days, then deleted. 0 = delete immediately.</summary>
    public int KeepReplacedDays { get; set; } = 7;

    // ---- Safety ----
    /// <summary>Discard the result (keep the original) when it isn't smaller than the source. Doesn't apply to manual jobs that keep the original or burn subtitles.</summary>
    public bool SkipIfNotSmaller { get; set; } = true;

    /// <summary>Extra saving required before a result is accepted (0 = merely smaller).</summary>
    public int MinSavingsPercent { get; set; } = 0;

    /// <summary>Compare the output's duration with the source (ffprobe) before the original is touched.</summary>
    public bool VerifyDuration { get; set; } = true;

    /// <summary>Don't start an encode unless this much space (GB) remains free after the expected output size.</summary>
    public int MinFreeSpaceGb { get; set; } = 2;

    /// <summary>Where replaced originals are held. Empty = a hidden ".hb-originals" folder inside the video's own library folder (same drive, ignored by Jellyfin).</summary>
    public string TrashFolder { get; set; } = string.Empty;

    public LibrarySettings? FindLibrary(string id) =>
        Libraries.FirstOrDefault(l => string.Equals(l.LibraryId, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Profile that applies to a library: its custom one if enabled, otherwise the defaults.</summary>
    public ConversionProfile EffectiveProfile(LibrarySettings? lib) => lib != null && lib.UseCustom ? lib.Profile : Defaults;
}
