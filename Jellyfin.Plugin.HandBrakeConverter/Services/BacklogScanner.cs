using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>A file the auto-convert will pick up, with the profile of the library it lives in.</summary>
public record Pending(string ItemId, string Path, ConversionProfile Profile, string Library);

/// <summary>
/// Finds unconverted files in libraries with Auto-convert on. The expensive library query is cached and only redone when Jellyfin
/// reports library changes (after they settle), the settings change, or every 10 minutes as a safety net; each call just re-checks
/// the cheap per-file state.
/// </summary>
public sealed class BacklogScanner
{
    /// <summary>Seconds the library must be quiet (no add/update/remove events) before the list is rebuilt.</summary>
    public static int QuietSeconds = 20;

    private static readonly BaseItemKind[] Playable = { BaseItemKind.Movie, BaseItemKind.Episode };

    private readonly ILibraryManager _library;
    private readonly StateStore _state;
    private readonly object _lock = new();
    private List<Pending>? _cache;
    private string _cacheSig = string.Empty;
    private DateTime _cacheBuilt = DateTime.MinValue;
    private DateTime _lastChange = DateTime.MinValue;
    private bool _dirty = true;

    public BacklogScanner(ILibraryManager library, StateStore state)
    {
        _library = library;
        _state = state;
        _library.ItemAdded += (_, _) => LibraryChanged();
        _library.ItemUpdated += (_, _) => LibraryChanged();
        _library.ItemRemoved += (_, _) => LibraryChanged();
    }

    public void LibraryChanged()
    {
        lock (_lock)
        {
            _dirty = true;
            _lastChange = DateTime.UtcNow;
        }
    }

    public List<Pending> FindPending(PluginConfiguration cfg, int limit)
    {
        var sig = Signature(cfg);
        List<Pending>? list;
        bool rebuild;
        lock (_lock)
        {
            rebuild = _cache == null
                || sig != _cacheSig
                || (_dirty && (DateTime.UtcNow - _lastChange).TotalSeconds >= QuietSeconds)
                || DateTime.UtcNow - _cacheBuilt > TimeSpan.FromMinutes(10);
            list = _cache;
        }

        if (rebuild)
        {
            list = Build(cfg);
            lock (_lock)
            {
                _cache = list;
                _cacheSig = sig;
                _cacheBuilt = DateTime.UtcNow;
                _dirty = false;
            }
        }

        var maxFailures = Math.Max(1, cfg.MaxFailures);
        return list!
            .Where(c => !_state.IsProcessed(c.ItemId, c.Path) && _state.FailureCount(c.Path) < maxFailures && File.Exists(c.Path))
            .Take(limit)
            .ToList();
    }

    /// <summary>A movie/episode is itself; a series or season expands to all of its episodes.</summary>
    public List<BaseItem> ExpandTargets(Guid id)
    {
        var item = _library.GetItemById(id);
        if (item == null) return new List<BaseItem>();
        if (item.GetType().Name is "Movie" or "Episode") return new List<BaseItem> { item };
        return Query(id).ToList();
    }

    private static string Signature(PluginConfiguration cfg) =>
        JsonSerializer.Serialize(new { cfg.MinSizeMb, L = cfg.Libraries.Where(l => l.AutoProcess).Select(l => new { l.LibraryId, l.Name, P = cfg.EffectiveProfile(l) }) });

    private IEnumerable<BaseItem> Query(Guid parent) =>
        _library.GetItemList(new InternalItemsQuery
        {
            ParentId = parent,
            Recursive = true,
            IncludeItemTypes = Playable,
            IsVirtualItem = false
        }).OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase);

    private List<Pending> Build(PluginConfiguration cfg)
    {
        var result = new List<Pending>();
        var minBytes = cfg.MinSizeMb * 1024L * 1024L;

        foreach (var lib in cfg.Libraries.Where(l => l.AutoProcess))
        {
            if (!Guid.TryParse(lib.LibraryId, out var guid)) continue;
            var profile = cfg.EffectiveProfile(lib);
            var label = profile.GetLabel();

            foreach (var it in Query(guid))
            {
                var path = it.Path;
                var id = it.Id.ToString("N");
                if (!VideoFiles.IsVideo(path) || _state.IsProcessed(id, path)) continue;

                // Never re-convert our own converted copies (kept-original mode).
                if (!profile.DeleteOriginal && System.IO.Path.GetFileNameWithoutExtension(path).EndsWith(label, StringComparison.OrdinalIgnoreCase)) continue;

                if (!File.Exists(path) || (minBytes > 0 && new FileInfo(path).Length < minBytes)) continue;
                result.Add(new Pending(id, path, profile, lib.Name));
            }
        }

        return result;
    }
}
