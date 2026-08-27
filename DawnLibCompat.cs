using System.Runtime.CompilerServices;
using Dawn;
using UnityEngine;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Thin isolation shim over DawnLib, COPIED from what is now
    /// BetterArmory/Weapons/DawnLibCompat.cs (#266). Calls remain isolated behind NoInlining
    /// methods, and items register under the shared <c>y4ngz_upgrades</c> namespace.
    ///
    /// The namespace string is frozen: it is part of every registered item's identity, so the two
    /// copies must keep using the same one. LUCKY-8's claim capsule is this side's only consumer.
    /// </summary>
    internal static class DawnLibCompat
    {
        internal const string ItemNamespace = "y4ngz_upgrades";

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void RegisterNetworkPrefab(GameObject prefab)
        {
            DawnLib.RegisterNetworkPrefab(prefab);
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void FixMixerGroups(GameObject prefab)
        {
            DawnLib.FixMixerGroups(prefab);
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void RegisterItem(string itemKey, Item item)
        {
            if (item == null || string.IsNullOrWhiteSpace(itemKey))
                return;

            NamespacedKey<DawnItemInfo> key = NamespacedKey<DawnItemInfo>.From(ItemNamespace, itemKey);
            DawnLib.DefineItem(key, item, builder => { });
        }
    }
}
