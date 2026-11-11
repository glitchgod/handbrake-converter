using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>
/// Knows whether anyone is watching. Jellyfin sets a session's NowPlayingItem on playback start and clears it on stop (sessions stay
/// listed afterwards), so "playing" simply means NowPlayingItem != null. Playback events also wake the scheduler immediately instead of
/// waiting for its next poll.
/// </summary>
public sealed class PlaybackWatcher
{
    private readonly ISessionManager _sessions;
    private readonly ILogger<PlaybackWatcher> _log;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    public PlaybackWatcher(ISessionManager sessions, ILogger<PlaybackWatcher> log)
    {
        _sessions = sessions;
        _log = log;
        _sessions.PlaybackStart += (_, _) => Wake();
        _sessions.PlaybackStopped += (_, _) => Wake();
    }

    public void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Sleeps up to <paramref name="delay"/>; returns early when playback starts or stops (or <see cref="Wake"/> is called).</summary>
    public async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (!await _wake.WaitAsync(delay, ct).ConfigureAwait(false)) return;
        while (_wake.CurrentCount > 0) _wake.Wait(0, CancellationToken.None);
        // Jellyfin updates the session around the event; let it settle before anyone looks.
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
    }

    /// <summary>Someone is playing video (paused and audio-only playback count only if the settings say so).</summary>
    public bool IsAnyoneWatching(PluginConfiguration cfg)
    {
        try
        {
            return _sessions.Sessions.Any(s =>
                s.NowPlayingItem != null
                && !(cfg.IgnoreAudioPlayback && s.NowPlayingItem.MediaType.ToString() == "Audio")
                && (cfg.PausedCountsAsStreaming || !(s.PlayState?.IsPaused ?? false)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read sessions");
            return false;
        }
    }

    /// <summary>This exact item is open in a player (paused counts: the client will resume reading the file).</summary>
    public bool IsPlaying(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id)) return false;
        try
        {
            return _sessions.Sessions.Any(s => s.NowPlayingItem != null && s.NowPlayingItem.Id == id);
        }
        catch
        {
            return false;
        }
    }
}
