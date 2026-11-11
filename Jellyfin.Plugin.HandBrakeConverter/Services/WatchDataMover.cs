using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

public class UserWatchData
{
    public Guid UserId { get; set; }
    public bool Played { get; set; }
    public int PlayCount { get; set; }
    public long PositionTicks { get; set; }
    public bool IsFavorite { get; set; }
    public double? Rating { get; set; }
    public DateTime? LastPlayed { get; set; }
}

/// <summary>Everything worth carrying from the old library item to the new one (captured before the old file is swapped out).</summary>
public class PendingMove
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid OldItemId { get; set; }
    public string NewPath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string HistoryId { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.Now;
    public int Attempts { get; set; }
    public DateTime? DateAdded { get; set; }
    public List<UserWatchData> Users { get; set; } = new();
    public List<Guid> Collections { get; set; } = new();
}

/// <summary>
/// When a conversion changes a file's extension, Jellyfin sees a brand-new item (items are identified by path) and the old item's watch
/// data is deleted with it. This captures that data from the old item before the swap and copies it onto the new item once the library
/// scan has found it. Best effort: every Jellyfin call is made late-bound and guarded, so a different Jellyfin version can only make this
/// step do nothing (it is logged), never break a conversion.
/// </summary>
public class WatchDataMover
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(48);

    private readonly ILibraryManager _library;
    private readonly IServiceProvider _services;
    private readonly HistoryStore _history;
    private readonly ILogger<WatchDataMover> _log;
    private readonly string _file;
    private readonly object _lock = new();
    private List<PendingMove> _pending = new();

    public WatchDataMover(ILibraryManager library, IServiceProvider services, StateStore state, HistoryStore history, ILogger<WatchDataMover> log)
    {
        _library = library;
        _services = services;
        _history = history;
        _log = log;
        _file = Path.Combine(state.DataDir, "pending-moves.json");
        try
        {
            if (File.Exists(_file)) _pending = JsonSerializer.Deserialize<List<PendingMove>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read pending watch-data moves"); _pending = new(); }
    }

    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    // ------------------------------------------------------------------ late-bound access to Jellyfin services

    private object? Service(string typeName)
    {
        try
        {
            var t = typeof(ILibraryManager).Assembly.GetType(typeName);
            return t == null ? null : _services.GetService(t);
        }
        catch { return null; }
    }

    private object? UserData => Service("MediaBrowser.Controller.Library.IUserDataManager");
    private object? UserManager => Service("MediaBrowser.Controller.Library.IUserManager");
    private object? Collections => Service("MediaBrowser.Controller.Collections.ICollectionManager");

    private static bool Accepts(Type param, object? arg)
    {
        if (arg == null) return !param.IsValueType || Nullable.GetUnderlyingType(param) != null;
        var t = Nullable.GetUnderlyingType(param) ?? param;
        if (t.IsEnum && arg is string) return true;
        return t.IsInstanceOfType(arg);
    }

    private static object? Convert(Type param, object? arg)
    {
        var t = Nullable.GetUnderlyingType(param) ?? param;
        if (t.IsEnum && arg is string s)
        {
            // An unknown name must never silently become some other value (e.g. a "playback started" reason that other plugins would react to).
            if (!Enum.GetNames(t).Contains(s)) throw new ArgumentException("Unknown " + t.Name + " value: " + s);
            return Enum.Parse(t, s);
        }

        return arg;
    }

    /// <summary>Calls a public instance method by name, matching on argument count and types. Throws if there is no match.</summary>
    private static object? Call(object target, string name, params object?[] args)
    {
        foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == name))
        {
            var ps = m.GetParameters();
            if (ps.Length != args.Length || !ps.Select((p, i) => Accepts(p.ParameterType, args[i])).All(x => x)) continue;
            try { return m.Invoke(target, ps.Select((p, i) => Convert(p.ParameterType, args[i])).ToArray()); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }

        throw new MissingMethodException(target.GetType().Name + "." + name + "(" + args.Length + " args)");
    }

    private static async Task CallAsync(object target, string name, params object?[] args)
    {
        if (Call(target, name, args) is Task t) await t.ConfigureAwait(false);
    }

    private static object? Get(object o, string prop) => o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);

    private static void Set(object o, string prop, object? value)
    {
        var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
        if (p == null || !p.CanWrite) return;
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        p.SetValue(o, value == null ? null : System.Convert.ChangeType(value, t));
    }

    private IEnumerable<object> Users()
    {
        var um = UserManager;
        if (um == null) return Enumerable.Empty<object>();
        object? r;
        try { r = Call(um, "GetUsers"); }
        catch (MissingMethodException) { r = Get(um, "Users"); }
        return (r as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
    }

    // ------------------------------------------------------------------ 1) before the swap

    /// <summary>Reads the old item's watch data, collections and date added. Returns null if nothing could be read (the move is then skipped).</summary>
    public PendingMove? Capture(string oldItemId, string newPath, string title)
    {
        try
        {
            if (!Guid.TryParse(oldItemId, out var id)) return null;
            var item = _library.GetItemById(id);
            var udm = UserData;
            if (item == null || udm == null) { _log.LogInformation("Watch data: old item or user-data service not available; nothing carried over for {Title}", title); return null; }

            var move = new PendingMove { OldItemId = id, NewPath = newPath, Title = title };
            if (Get(item, "DateCreated") is DateTime dc && dc.Year > 1) move.DateAdded = dc;

            var collections = Collections;
            foreach (var user in Users())
            {
                if (Get(user, "Id") is not Guid uid) continue;
                var d = Call(udm, "GetUserData", user, item);
                if (d != null)
                {
                    var w = new UserWatchData
                    {
                        UserId = uid,
                        Played = Get(d, "Played") is true,
                        PlayCount = Get(d, "PlayCount") is int pc ? pc : 0,
                        PositionTicks = Get(d, "PlaybackPositionTicks") is long pt ? pt : 0,
                        IsFavorite = Get(d, "IsFavorite") is true,
                        Rating = Get(d, "Rating") as double?,
                        LastPlayed = Get(d, "LastPlayedDate") as DateTime?
                    };
                    if (w.Played || w.PlayCount > 0 || w.PositionTicks > 0 || w.IsFavorite || w.Rating != null || w.LastPlayed != null) move.Users.Add(w);
                }

                if (collections != null)
                {
                    try
                    {
                        foreach (var c in (Call(collections, "GetCollectionsContainingItem", user, id) as IEnumerable ?? Array.Empty<object>()).Cast<object>())
                            if (Get(c, "Id") is Guid cid && !move.Collections.Contains(cid)) move.Collections.Add(cid);
                    }
                    catch (Exception ex) { _log.LogDebug(ex, "Watch data: collections lookup failed"); }
                }
            }

            _log.LogInformation("Watch data captured for {Title}: {Users} user(s), {Cols} collection(s)", title, move.Users.Count, move.Collections.Count);
            return move;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the watch data of {Title}; it will not be carried over", title);
            return null;
        }
    }

    public void Register(PendingMove move, string historyId)
    {
        move.HistoryId = historyId;
        lock (_lock) { _pending.Add(move); Save(); }
    }

    // ------------------------------------------------------------------ 2) after the library scan found the new file

    /// <summary>Applies every pending move whose new item now exists. Cheap when nothing is pending.</summary>
    public async Task ApplyPendingAsync(CancellationToken ct)
    {
        List<PendingMove> todo;
        lock (_lock) todo = _pending.ToList();
        foreach (var m in todo)
        {
            ct.ThrowIfCancellationRequested();
            object? item = null;
            try { item = Call(_library, "FindByPath", m.NewPath, false); }
            catch (Exception ex) { _log.LogDebug(ex, "FindByPath failed"); }

            if (item == null)
            {
                m.Attempts++;
                if (DateTime.Now - m.Created > GiveUpAfter)
                {
                    _log.LogWarning("Watch data for {Title}: the new file never showed up in the library; giving up", m.Title);
                    Finish(m, "Watch data could not be carried over (the new file was not found in the library).");
                }
                else if (m.Attempts % 10 == 0)
                {
                    try { _library.QueueLibraryScan(); } catch { }
                }

                continue;
            }

            string note;
            try { note = await ApplyAsync(m, item, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Carrying watch data over to {Title} failed", m.Title);
                note = "Watch data could not be carried over (" + ex.Message + ").";
            }

            Finish(m, note);
        }

        lock (_lock) Save();
    }

    private async Task<string> ApplyAsync(PendingMove m, object item, CancellationToken ct)
    {
        var udm = UserData;
        var um = UserManager;
        var parts = new List<string>();
        var newId = Get(item, "Id") is Guid g ? g : Guid.Empty;

        var moved = 0;
        if (udm != null && um != null)
        {
            foreach (var w in m.Users)
            {
                var user = Call(um, "GetUserById", w.UserId);
                if (user == null) continue;
                var cur = Call(udm, "GetUserData", user, item);
                if (cur == null) continue;
                // Never overwrite something the user already did on the new item.
                if (Get(cur, "Played") is true || (Get(cur, "PlaybackPositionTicks") is long p && p > 0) || Get(cur, "IsFavorite") is true) continue;
                Set(cur, "Played", w.Played);
                Set(cur, "PlayCount", w.PlayCount);
                Set(cur, "PlaybackPositionTicks", w.PositionTicks);
                Set(cur, "IsFavorite", w.IsFavorite);
                Set(cur, "Rating", w.Rating);
                Set(cur, "LastPlayedDate", w.LastPlayed);
                // "Import" is the reason Jellyfin itself uses when it reads watched state from an NFO file: it does not look like playback.
                try { Call(udm, "SaveUserData", user, item, cur, "Import", ct); }
                catch (ArgumentException) { Call(udm, "SaveUserData", user, item, cur, "UpdateUserData", ct); }
                moved++;
            }
        }

        if (m.Users.Count > 0) parts.Add(moved + " of " + m.Users.Count + " user(s)' watch data");

        var cols = 0;
        var cm = Collections;
        if (cm != null && newId != Guid.Empty)
        {
            foreach (var c in m.Collections)
            {
                try { await CallAsync(cm, "AddToCollectionAsync", c, new List<Guid> { newId }).ConfigureAwait(false); cols++; }
                catch (Exception ex) { _log.LogDebug(ex, "Adding to collection failed"); }
            }
        }

        if (m.Collections.Count > 0) parts.Add(cols + " collection(s)");

        if (m.DateAdded != null)
        {
            try
            {
                Set(item, "DateCreated", m.DateAdded.Value);
                await CallAsync(item, "UpdateToRepositoryAsync", "MetadataEdit", ct).ConfigureAwait(false);
                parts.Add("date added");
            }
            catch (Exception ex) { _log.LogDebug(ex, "Setting date added failed"); }
        }

        _log.LogInformation("Carried over to {Title}: {Parts}", m.Title, string.Join(", ", parts));
        return parts.Count == 0 ? "Nothing needed carrying over to the new item." : "Carried over to the new item: " + string.Join(", ", parts) + ".";
    }

    private void Finish(PendingMove m, string note)
    {
        lock (_lock) { _pending.RemoveAll(x => x.Id == m.Id); Save(); }
        if (!string.IsNullOrEmpty(m.HistoryId)) _history.Annotate(m.HistoryId, note);
    }

    private void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_pending));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save pending watch-data moves"); }
    }
}
