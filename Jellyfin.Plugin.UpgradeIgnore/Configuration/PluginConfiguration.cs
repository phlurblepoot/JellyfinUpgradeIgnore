using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.UpgradeIgnore.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        UpgradeWindowMinutes = 30;
        MatchByProviderIds = true;
        MatchByPathFallback = true;
        Enabled = true;
    }

    /// <summary>
    /// How long after a removal a matching add is still considered an upgrade.
    /// </summary>
    public int UpgradeWindowMinutes { get; set; }

    /// <summary>
    /// Match removed and added items by external provider IDs (TMDB / IMDB / TVDB).
    /// </summary>
    public bool MatchByProviderIds { get; set; }

    /// <summary>
    /// If provider IDs are missing, fall back to matching by item path / file name stem.
    /// </summary>
    public bool MatchByPathFallback { get; set; }

    public bool Enabled { get; set; }
}
