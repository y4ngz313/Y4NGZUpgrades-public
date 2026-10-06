using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Y4NGZUpgrades.Gui;

/// <summary>
/// Immutable cosmetic state consumed by one menu rebuild. Ownership and equipment are distinct:
/// a currently worn item remains visible as owned while a pending save import is reconciled, but
/// only the first ready snapshot can turn recognized worn items into persisted entitlements.
/// </summary>
internal sealed class CosmeticStateSnapshot
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    internal static readonly CosmeticStateSnapshot Unavailable = new CosmeticStateSnapshot(
        ready: false,
        scopeKey: null,
        knownIds: Array.Empty<string>(),
        ownedIds: Array.Empty<string>(),
        equippedIds: Array.Empty<string>());

    internal CosmeticStateSnapshot(
        bool ready,
        string scopeKey,
        IEnumerable<string> knownIds,
        IEnumerable<string> ownedIds,
        IEnumerable<string> equippedIds)
    {
        Ready = ready;
        ScopeKey = scopeKey ?? string.Empty;
        KnownIds = Freeze(knownIds);
        EquippedIds = Freeze(equippedIds);
        var visibleOwned = new HashSet<string>(IdComparer);
        foreach (string raw in ownedIds ?? Array.Empty<string>())
        {
            string id = NormalizeId(raw);
            if (id != null)
                visibleOwned.Add(id);
        }
        visibleOwned.UnionWith(EquippedIds);
        OwnedIds = new ReadOnlyCollection<string>(visibleOwned.OrderBy(id => id, IdComparer).ToList());
        Signature = BuildSignature(Ready, ScopeKey, KnownIds, OwnedIds, EquippedIds);
    }

    internal bool Ready { get; }
    /// <summary>The active host/save identity captured with this state, or empty while unavailable.</summary>
    internal string ScopeKey { get; }
    internal IReadOnlyCollection<string> KnownIds { get; }
    internal IReadOnlyCollection<string> OwnedIds { get; }
    internal IReadOnlyCollection<string> EquippedIds { get; }
    internal string Signature { get; }

    internal bool IsOwned(string id) => Contains(OwnedIds, id);
    internal bool IsEquipped(string id) => Contains(EquippedIds, id);

    private static IReadOnlyCollection<string> Freeze(IEnumerable<string> source)
    {
        var ids = new HashSet<string>(IdComparer);
        if (source != null)
        {
            foreach (string raw in source)
            {
                string id = NormalizeId(raw);
                if (id != null)
                    ids.Add(id);
            }
        }
        return new ReadOnlyCollection<string>(ids.OrderBy(id => id, IdComparer).ToList());
    }

    private static bool Contains(IEnumerable<string> values, string raw)
    {
        string id = NormalizeId(raw);
        return id != null && values != null && values.Contains(id, IdComparer);
    }

    private static string BuildSignature(
        bool ready,
        string scopeKey,
        IEnumerable<string> known,
        IEnumerable<string> owned,
        IEnumerable<string> equipped)
    {
        return string.Concat(
            ready ? "ready|" : "waiting|",
            scopeKey ?? string.Empty, "|",
            string.Join(",", known ?? Array.Empty<string>()), "|",
            string.Join(",", owned ?? Array.Empty<string>()), "|",
            string.Join(",", equipped ?? Array.Empty<string>()));
    }

    internal static string NormalizeId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return raw.Trim().ToLowerInvariant();
    }
}

/// <summary>
/// Pure one-time entitlement migration. It deliberately never consumes a provider's transient
/// empty selection. MoreCompany can populate the catalog before asynchronously applying an
/// outfit, so this migration remains pending until a recognized worn id is actually observed.
/// </summary>
internal static class CosmeticInitialImportPolicy
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    internal static bool ShouldImport(
        bool providerAndSaveReady,
        bool alreadyImported,
        bool hasRecognizedWornId)
    {
        return providerAndSaveReady && !alreadyImported && hasRecognizedWornId;
    }

    internal static bool HasRecognizedWornId(
        IEnumerable<string> knownIds,
        IEnumerable<string> equippedIds)
    {
        var known = Normalize(knownIds);
        return Normalize(equippedIds).Overlaps(known);
    }

    internal static HashSet<string> MergeRecognizedWornIds(
        IEnumerable<string> existingOwned,
        IEnumerable<string> knownIds,
        IEnumerable<string> equippedIds)
    {
        var merged = Normalize(existingOwned);
        var known = Normalize(knownIds);
        foreach (string id in Normalize(equippedIds))
        {
            if (known.Contains(id))
                merged.Add(id);
        }
        return merged;
    }

    internal static bool SetEquals(IEnumerable<string> existing, ISet<string> expected)
    {
        return Normalize(existing).SetEquals(expected ?? new HashSet<string>(IdComparer));
    }

    private static HashSet<string> Normalize(IEnumerable<string> source)
    {
        var ids = new HashSet<string>(IdComparer);
        if (source == null)
            return ids;
        foreach (string raw in source)
        {
            string id = CosmeticStateSnapshot.NormalizeId(raw);
            if (id != null)
                ids.Add(id);
        }
        return ids;
    }
}
