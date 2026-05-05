using System;
using System.Collections.Generic;
using Jellyfin.Plugin.UpgradeIgnore.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.UpgradeIgnore;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Upgrade Ignore";

    public override Guid Id => Guid.Parse("a4d3f7d2-9f1c-4d2a-9b8a-7c2d3e4f5a6b");

    public override string Description =>
        "Detects when a media file is replaced with an upgraded version and preserves the original DateCreated so the item does not reappear in Recently Added.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
