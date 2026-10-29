// Minimal stand-ins for the Jellyfin APIs so our own code type-checks and can be exercised without NuGet access.
using Jellyfin.Plugin.HandBrakeConverter.Configuration;

namespace MediaBrowser.Model.Plugins { public class BasePluginConfiguration { } }
namespace MediaBrowser.Common.Configuration { public interface IApplicationPaths { string DataPath { get; } string LogDirectoryPath { get; } } }
namespace Jellyfin.Data.Enums { public enum BaseItemKind { Movie, Episode, Season, Series } public enum MediaType { Unknown, Video, Audio } }
namespace MediaBrowser.Model.Entities { public class VirtualFolderInfo { public string[] Locations { get; set; } = System.Array.Empty<string>(); } }
namespace MediaBrowser.Model.Dto { public class BaseItemDto { public Guid Id { get; set; } public Jellyfin.Data.Enums.MediaType MediaType { get; set; } } }
namespace MediaBrowser.Controller.Entities
{
    public class BaseItem { public Guid Id { get; set; } public string? Path { get; set; } public string? Name { get; set; } public int? ProductionYear { get; set; } public int? IndexNumber { get; set; } }
    public class Movie : BaseItem { }
    public class Series : BaseItem { }
    public class Season : BaseItem { }
    public class Episode : BaseItem { }
    public class InternalItemsQuery
    {
        public Guid ParentId { get; set; }
        public bool Recursive { get; set; }
        public Jellyfin.Data.Enums.BaseItemKind[]? IncludeItemTypes { get; set; }
        public bool? IsVirtualItem { get; set; }
    }
}
namespace MediaBrowser.Controller.Library
{
    public interface ILibraryManager
    {
        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> GetItemList(MediaBrowser.Controller.Entities.InternalItemsQuery q);
        MediaBrowser.Controller.Entities.BaseItem? GetItemById(Guid id);
        void QueueLibraryScan();
        List<MediaBrowser.Model.Entities.VirtualFolderInfo> GetVirtualFolders();
        event EventHandler<ItemChangeEventArgs>? ItemAdded;
        event EventHandler<ItemChangeEventArgs>? ItemUpdated;
        event EventHandler<ItemChangeEventArgs>? ItemRemoved;
    }
    public class ItemChangeEventArgs : EventArgs { }
    public class PlaybackProgressEventArgs : EventArgs { }
    public class PlaybackStopEventArgs : PlaybackProgressEventArgs { }
}
namespace MediaBrowser.Controller.MediaEncoding { public interface IMediaEncoder { string EncoderPath { get; } string ProbePath { get; } } }
namespace MediaBrowser.Controller.Session
{
    public class PlayState { public bool IsPaused { get; set; } }
    public class SessionInfo { public MediaBrowser.Model.Dto.BaseItemDto? NowPlayingItem { get; set; } public PlayState? PlayState { get; set; } }
    public interface ISessionManager
    {
        IEnumerable<SessionInfo> Sessions { get; }
        event EventHandler<MediaBrowser.Controller.Library.PlaybackProgressEventArgs> PlaybackStart;
        event EventHandler<MediaBrowser.Controller.Library.PlaybackProgressEventArgs> PlaybackProgress;
        event EventHandler<MediaBrowser.Controller.Library.PlaybackStopEventArgs> PlaybackStopped;
    }
}
namespace Jellyfin.Plugin.HandBrakeConverter
{
    public class Plugin
    {
        public static Plugin? Instance { get; set; }
        public PluginConfiguration Configuration { get; set; } = new();
    }
}
