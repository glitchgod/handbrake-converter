using System;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>What the Overview tab shows about the current conversion.</summary>
public sealed class ConversionStatus
{
    public string State { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string CurrentFile { get; set; } = string.Empty;
    public string CurrentJobId { get; set; } = string.Empty;
    public double Progress { get; set; }
    public string Eta { get; set; } = string.Empty;

    /// <summary>The encode is frozen right now (you paused it, or someone is watching).</summary>
    public bool Suspended { get; set; }

    /// <summary>Set via Pause on the Overview tab; nothing starts until Continue.</summary>
    public bool UserPaused { get; set; }

    internal ConversionStatus Copy() => (ConversionStatus)MemberwiseClone();

    /// <summary>Back to "not converting anything".</summary>
    internal void Clear()
    {
        CurrentFile = string.Empty;
        CurrentJobId = string.Empty;
        Progress = 0;
        Eta = string.Empty;
        Suspended = false;
    }
}

/// <summary>Thread-safe holder for <see cref="ConversionStatus"/>.</summary>
internal sealed class StatusBoard
{
    private readonly object _lock = new();
    private readonly ConversionStatus _status = new() { State = "Starting" };

    public void Set(Action<ConversionStatus> change)
    {
        lock (_lock) change(_status);
    }

    public ConversionStatus Snapshot()
    {
        lock (_lock) return _status.Copy();
    }

    /// <summary>Not converting: shows <paramref name="state"/> and a message, and clears the current file.</summary>
    public void Rest(string state, string message) => Set(s => { s.State = state; s.Message = message; s.Clear(); });
}
