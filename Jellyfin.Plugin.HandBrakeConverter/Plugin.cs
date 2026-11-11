using System;
using System.Collections.Generic;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.HandBrakeConverter;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "HandBrake Converter";

    public override Guid Id => Guid.Parse("6b1f3e52-8c0a-4d57-9a41-2f7d5c8e1b90");

    public override string Description => "Re-encodes library videos with HandBrake: per-library auto-processing, a Convert queue, schedule.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        // EnableInMainMenu puts a "HandBrake Converter" entry in the admin dashboard sidebar (like the Reports plugin).
        yield return new PluginPageInfo
        {
            Name = "HandBrakeConverter",
            DisplayName = "HandBrake Converter",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "transform"
        };
    }
}
