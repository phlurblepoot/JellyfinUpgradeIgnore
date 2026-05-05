using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.UpgradeIgnore;

/// <summary>
/// Snapshot of an item that was removed from the library. Held briefly so that
/// a near-future ItemAdded for the same logical media can inherit its DateCreated.
/// </summary>
internal sealed class UpgradeCandidate
{
    public required Guid OriginalId { get; init; }

    public required string ItemKind { get; init; }

    public required DateTime OriginalDateCreated { get; init; }

    public required DateTime RemovedAtUtc { get; init; }

    public Dictionary<string, string> ProviderIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Path { get; init; }

    public string? FileNameStem { get; init; }

    public string? ParentFolder { get; init; }

    public string? Name { get; init; }

    public int? ProductionYear { get; init; }

    public int? IndexNumber { get; init; }

    public int? ParentIndexNumber { get; init; }

    public Dictionary<string, string> SeriesProviderIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? SeriesName { get; init; }
}
