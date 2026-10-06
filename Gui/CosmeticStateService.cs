using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;

namespace Y4NGZUpgrades.Gui;

/// <summary>
/// Read-only MoreCompany state plus the one permitted entitlement migration. Reflection is kept
/// here rather than in PurchaseMenu so every cosmetic row and collection count works from the
/// same one-frame snapshot.
/// </summary>
internal static class CosmeticStateService
{
    private static Assembly resolvedAssembly;
    private static FieldInfo registryField;
    private static FieldInfo selectedField;

    /// <summary>
    /// Captures the provider's actual registry and selection list. A snapshot becomes ready only
    /// after the local player and the active save identity exist. Until then it may report worn
    /// ids for display, but never reads or writes PlayerLevelStore.
    /// </summary>
    internal static CosmeticStateSnapshot Capture()
    {
        // Capture scope even while MoreCompany is still loading. The menu must discard a prior
        // host/save's quote and row state when the identity changes, regardless of provider state.
        bool identityReady = SaveKey.TryGetCurrent(out string scopeKey);
        if (!TryReadProvider(out HashSet<string> knownIds, out HashSet<string> equippedIds))
        {
            return new CosmeticStateSnapshot(
                ready: false,
                scopeKey: identityReady ? scopeKey : null,
                knownIds: Array.Empty<string>(),
                ownedIds: Array.Empty<string>(),
                equippedIds: Array.Empty<string>());
        }

        bool localPlayerReady = ResolveLocalPlayer() != null;
        if (!identityReady || !localPlayerReady)
        {
            // Equipped ids are deliberately passed as visible ownership while the save scope is
            // pending. This makes a cosmetic already worn at join render correctly without a
            // premature, wrong-host disk write.
            return new CosmeticStateSnapshot(
                ready: false,
                scopeKey: identityReady ? scopeKey : null,
                knownIds: knownIds,
                ownedIds: Array.Empty<string>(),
                equippedIds: equippedIds);
        }

        PlayerLevelData data = PlayerLevelStore.Get();
        if (!data.cosmeticInitialImportCompleted)
        {
            // Initial-import-only policy: grandfather only the first recognized worn item(s).
            // An empty selection remains pending because MoreCompany may apply the local outfit
            // after its catalog has already loaded. Once imported, later selections are display
            // truth only and must be purchased before they survive an unequip.
            PlayerLevelStore.TryImportInitialRecognizedCosmetics(knownIds, equippedIds);
            data = PlayerLevelStore.Get();
        }

        return new CosmeticStateSnapshot(
            ready: true,
            scopeKey: scopeKey,
            knownIds: knownIds,
            ownedIds: (IEnumerable<string>)data?.cosmetics ?? Array.Empty<string>(),
            equippedIds: equippedIds);
    }

    private static bool TryReadProvider(
        out HashSet<string> knownIds,
        out HashSet<string> equippedIds)
    {
        knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        equippedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ResolveProviderFields())
            return false;

        try
        {
            // Both fields must be concrete collection instances. MoreCompany constructs these
            // after assembly load, and treating a null/placeholder collection as ready would
            // permanently consume the initial import with an empty selection.
            if (!(registryField.GetValue(null) is IDictionary registry) ||
                !(selectedField.GetValue(null) is IList selected))
            {
                return false;
            }

            foreach (DictionaryEntry entry in registry)
            {
                string id = CosmeticStateSnapshot.NormalizeId(entry.Key as string);
                if (id != null && entry.Value != null)
                    knownIds.Add(id);
            }
            foreach (object entry in selected)
            {
                string id = CosmeticStateSnapshot.NormalizeId(entry as string ?? entry?.ToString());
                if (id != null)
                    equippedIds.Add(id);
            }
            // An instantiated-but-empty registry is MoreCompany's startup state on some joins.
            // Keep retrying until the catalog itself is populated. A live empty selected list
            // may precede MoreCompany's asynchronous local-outfit application; Capture keeps
            // the initial entitlement migration pending until a recognized worn id appears.
            return knownIds.Count > 0;
        }
        catch
        {
            // Providers can rebuild these static collections while joining. Retry on the next
            // menu update; this method must never leave a partial snapshot behind.
            return false;
        }
    }

    private static bool ResolveProviderFields()
    {
        Assembly assembly = FindMoreCompanyAssembly();
        if (assembly == null)
        {
            resolvedAssembly = null;
            registryField = null;
            selectedField = null;
            return false;
        }

        // Retry missing fields too: a delayed provider initialization can load the assembly
        // before its types/registries are usable.
        if (!ReferenceEquals(resolvedAssembly, assembly) || registryField == null || selectedField == null)
        {
            resolvedAssembly = assembly;
            Type registryType = assembly.GetType("MoreCompany.Cosmetics.CosmeticRegistry", throwOnError: false);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            registryField = registryType?.GetField("cosmeticInstances", flags);
            selectedField = registryType?.GetField("locallySelectedCosmetics", flags);
        }
        return registryField != null && selectedField != null;
    }

    private static Assembly FindMoreCompanyAssembly()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (string.Equals(assembly.GetName().Name, "MoreCompany", StringComparison.Ordinal))
                    return assembly;
            }
            catch { }
        }
        return null;
    }

    private static PlayerControllerB ResolveLocalPlayer()
    {
        try
        {
            return GameNetworkManager.Instance?.localPlayerController
                   ?? StartOfRound.Instance?.localPlayerController;
        }
        catch
        {
            return null;
        }
    }
}
