using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.UpgradeIgnore.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UpgradeIgnore;

/// <summary>
/// Listens for library remove/add events and, when an add matches a recently
/// removed item, copies the original DateCreated forward so the upgraded file
/// does not appear in Recently Added. On Jellyfin 12.x a movie whose new file arrives
/// before the old one is removed takes the date from the version it replaces.
/// </summary>
public sealed class UpgradeDetectionService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<UpgradeDetectionService> _logger;
    private readonly List<UpgradeCandidate> _candidates = new();
    private readonly object _candidatesLock = new();
    private readonly Timer _pruneTimer;

    public UpgradeDetectionService(
        ILibraryManager libraryManager,
        ILogger<UpgradeDetectionService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _pruneTimer = new Timer(_ => PruneExpired(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved += OnItemRemoved;
        _libraryManager.ItemAdded += OnItemAdded;
        _pruneTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _logger.LogInformation("UpgradeIgnore service started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _libraryManager.ItemAdded -= OnItemAdded;
        _pruneTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _pruneTimer.Dispose();
    }

    private static PluginConfigurationView Config()
    {
        var c = Plugin.Instance?.Configuration;
        return new PluginConfigurationView
        {
            Enabled = c?.Enabled ?? true,
            WindowMinutes = c?.UpgradeWindowMinutes ?? 30,
            MatchByProviderIds = c?.MatchByProviderIds ?? true,
            MatchByPathFallback = c?.MatchByPathFallback ?? true
        };
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var cfg = Config();
        if (!cfg.Enabled)
        {
            return;
        }

        var item = e.Item;
        if (!IsTrackedKind(item))
        {
            return;
        }

        var candidate = new UpgradeCandidate
        {
            OriginalId = item.Id,
            ItemKind = item.GetType().Name,
            OriginalDateCreated = item.DateCreated,
            RemovedAtUtc = DateTime.UtcNow,
            Path = item.Path,
            FileNameStem = SafeFileStem(item.Path),
            ParentFolder = SafeParentFolder(item.Path),
            Name = item.Name,
            ProductionYear = item.ProductionYear,
            IndexNumber = item.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber,
            ProviderIds = CopyProviderIds(item.ProviderIds),
            SeriesProviderIds = item is Episode ep && ep.Series != null
                ? CopyProviderIds(ep.Series.ProviderIds)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            SeriesName = (item as Episode)?.SeriesName
        };

        lock (_candidatesLock)
        {
            _candidates.Add(candidate);
        }

        _logger.LogDebug(
            "Tracking removed {Kind} '{Name}' (path={Path}) for upgrade detection",
            candidate.ItemKind,
            candidate.Name,
            candidate.Path);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var cfg = Config();
        if (!cfg.Enabled)
        {
            return;
        }

        var item = e.Item;
        if (!IsTrackedKind(item))
        {
            return;
        }

        UpgradeCandidate? match;
        lock (_candidatesLock)
        {
            match = FindMatch(item, cfg);
            if (match != null)
            {
                _candidates.Remove(match);
            }
        }

        DateTime originalDateCreated;
        if (match != null)
        {
            originalDateCreated = match.OriginalDateCreated;
        }
        else
        {
            // Jellyfin 12.x: when a better file is added next to an existing one, the new
            // file is created as a fresh primary item (new id, new DateCreated) listing the
            // old file as a local alternate version, and the old movie is demoted instead of
            // removed. No ItemRemoved fires, so inherit the date of the existing version.
            // Episodes are not handled here.
            var existing = FindOldestExistingVersionDate(item);
            if (existing == null)
            {
                return;
            }

            originalDateCreated = existing.Value;
        }

        if (item.DateCreated <= originalDateCreated)
        {
            return;
        }

        var previous = item.DateCreated;
        item.DateCreated = originalDateCreated;

        _ = Task.Run(async () =>
        {
            try
            {
                await _libraryManager.UpdateItemAsync(
                        item,
                        item.GetParent(),
                        ItemUpdateType.MetadataEdit,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Detected upgrade for '{Name}' ({Kind}); restored DateCreated {Original:o} (was {New:o})",
                    item.Name,
                    item.GetType().Name,
                    originalDateCreated,
                    previous);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist restored DateCreated for '{Name}'", item.Name);
            }
        });
    }

    private UpgradeCandidate? FindMatch(BaseItem added, PluginConfigurationView cfg)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(cfg.WindowMinutes);
        var addedKind = added.GetType().Name;
        var addedProviders = added.ProviderIds ?? new Dictionary<string, string>();
        var addedSeriesProviders = (added as Episode)?.Series?.ProviderIds
                                   ?? new Dictionary<string, string>();
        var addedStem = SafeFileStem(added.Path);
        var addedParent = SafeParentFolder(added.Path);

        UpgradeCandidate? best = null;

        for (var i = _candidates.Count - 1; i >= 0; i--)
        {
            var c = _candidates[i];
            if (c.RemovedAtUtc < cutoff)
            {
                continue;
            }

            if (!string.Equals(c.ItemKind, addedKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (added is Episode addedEpisode)
            {
                if (c.IndexNumber != addedEpisode.IndexNumber ||
                    c.ParentIndexNumber != addedEpisode.ParentIndexNumber)
                {
                    continue;
                }

                if (cfg.MatchByProviderIds && SharesAnyProvider(c.SeriesProviderIds, addedSeriesProviders))
                {
                    best = c;
                    break;
                }

                if (cfg.MatchByProviderIds && SharesAnyProvider(c.ProviderIds, addedProviders))
                {
                    best = c;
                    break;
                }

                if (cfg.MatchByPathFallback &&
                    !string.IsNullOrEmpty(c.SeriesName) &&
                    string.Equals(c.SeriesName, addedEpisode.SeriesName, StringComparison.OrdinalIgnoreCase))
                {
                    best = c;
                    break;
                }
            }
            else if (added is Movie)
            {
                if (cfg.MatchByProviderIds && SharesAnyProvider(c.ProviderIds, addedProviders))
                {
                    best = c;
                    break;
                }

                if (cfg.MatchByPathFallback &&
                    !string.IsNullOrEmpty(c.Name) &&
                    string.Equals(c.Name, added.Name, StringComparison.OrdinalIgnoreCase) &&
                    c.ProductionYear == added.ProductionYear)
                {
                    best = c;
                    break;
                }
            }

            if (best == null && cfg.MatchByPathFallback &&
                !string.IsNullOrEmpty(addedStem) &&
                !string.IsNullOrEmpty(c.FileNameStem) &&
                string.Equals(c.ParentFolder, addedParent, StringComparison.OrdinalIgnoreCase) &&
                StemsLookRelated(c.FileNameStem, addedStem))
            {
                best = c;
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the oldest DateCreated among the versions of the added movie that were
    /// already in the library, or null when there are none.
    /// </summary>
    /// <remarks>
    /// Jellyfin 12.x saves the new item, including its link to the old one, before it
    /// raises ItemAdded, and that save sets PrimaryVersionId on the old row. Library
    /// queries such as FindByPath then hide the old item as an alternate version, so it
    /// has to be fetched by id. The old item is demoted only after ItemAdded, so at this
    /// point its OwnerId is not the added movie. A version that does have that owner was
    /// created by the same save, which means it is as new as the added movie.
    /// </remarks>
    private DateTime? FindOldestExistingVersionDate(BaseItem added)
    {
        if (added is not Movie movie)
        {
            return null;
        }

        var alternates = movie.LocalAlternateVersions;
        if (alternates == null || alternates.Length == 0)
        {
            return null;
        }

        DateTime? oldest = null;
        try
        {
            foreach (var id in GetAlternateVersionIds(movie))
            {
                if (id.Equals(movie.Id) ||
                    _libraryManager.GetItemById(id) is not Movie version ||
                    version.OwnerId.Equals(movie.Id))
                {
                    continue;
                }

                if (oldest == null || version.DateCreated < oldest.Value)
                {
                    oldest = version.DateCreated;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lookup of existing versions of '{Name}' failed", movie.Name);
            return null;
        }

        _logger.LogInformation(
            "New movie '{Name}' lists {Count} alternate version(s); oldest existing DateCreated is {Oldest:o}",
            movie.Name,
            alternates.Length,
            oldest);

        return oldest;
    }

    /// <summary>
    /// Returns the ids of a video's local alternate versions, from the stored links and
    /// from the id Jellyfin derives for each listed path.
    /// </summary>
    private HashSet<Guid> GetAlternateVersionIds(Video video)
    {
        var ids = new HashSet<Guid>(_libraryManager.GetLocalAlternateVersionIds(video));
        foreach (var path in video.LocalAlternateVersions)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                ids.Add(_libraryManager.GetNewItemId(path, video.GetType()));
            }
        }

        return ids;
    }

    private void PruneExpired()
    {
        var cfg = Config();
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(cfg.WindowMinutes);
        lock (_candidatesLock)
        {
            _candidates.RemoveAll(c => c.RemovedAtUtc < cutoff);
        }
    }

    private static bool IsTrackedKind(BaseItem item) => item is Movie or Episode;

    private static Dictionary<string, string> CopyProviderIds(Dictionary<string, string>? source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return copy;
        }

        foreach (var kvp in source)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Value))
            {
                copy[kvp.Key] = kvp.Value;
            }
        }

        return copy;
    }

    private static bool SharesAnyProvider(
        IReadOnlyDictionary<string, string> a,
        IReadOnlyDictionary<string, string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        foreach (var kvp in a)
        {
            if (b.TryGetValue(kvp.Key, out var other) &&
                !string.IsNullOrWhiteSpace(other) &&
                string.Equals(kvp.Value, other, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? SafeFileStem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeParentFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Heuristic: do two filename stems share a meaningful prefix? Used as a last-ditch
    /// fallback when provider IDs are absent. Strips common quality / release tags.
    /// </summary>
    private static bool StemsLookRelated(string a, string b)
    {
        var na = NormalizeStem(a);
        var nb = NormalizeStem(b);
        if (na.Length < 3 || nb.Length < 3)
        {
            return false;
        }

        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var shorter = na.Length < nb.Length ? na : nb;
        var longer = na.Length < nb.Length ? nb : na;
        return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStem(string stem)
    {
        var idx = stem.IndexOfAny(new[] { '[', '(' });
        var trimmed = idx > 0 ? stem[..idx] : stem;
        return trimmed
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Trim()
            .ToLowerInvariant();
    }

    private sealed class PluginConfigurationView
    {
        public bool Enabled { get; init; }
        public int WindowMinutes { get; init; }
        public bool MatchByProviderIds { get; init; }
        public bool MatchByPathFallback { get; init; }
    }
}
