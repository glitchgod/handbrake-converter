using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>Reads new Jellyfin transcode logs every few minutes, so their summaries are kept after Jellyfin cleans the logs up.</summary>
public sealed class TranscodeLogWatcher : BackgroundService
{
    private readonly TranscodeStore _store;
    private readonly ILogger<TranscodeLogWatcher> _log;

    public TranscodeLogWatcher(TranscodeStore store, ILogger<TranscodeLogWatcher> log)
    {
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { _store.Scan(); }
            catch (Exception ex) { _log.LogWarning(ex, "Reading transcode logs failed"); }

            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
