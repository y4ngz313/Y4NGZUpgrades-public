using BepInEx.Bootstrap;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// One authoritative view of the optional plugins whose content Upgrades can augment. Plugin
    /// presence comes from BepInEx rather than type discovery: a stale or similarly named type in
    /// another assembly must never make an unavailable feature appear in the catalog or Employee
    /// File.
    /// </summary>
    internal static class OptionalPluginCapabilities
    {
        internal const string ContractedGuid = "com.y4ngz.company";
        internal const string Y4NGZUiGuid = "com.y4ngz.ui";
        internal const string ShipSystemsGuid = "com.y4ngz.company.shipsystems";
        internal const string LethalCctvGuid = "com.y4ngz.company.lethalcctv";
        internal const string BetterArmoryGuid = "com.y4ngz.betterarmory";
        internal const string BloodFxGuid = "com.y4ngz.bloodfx";
        internal const string MoreCompanyGuid = "me.swipez.melonloader.morecompany";
        internal const string TooManyEmotesGuid = "FlipMods.TooManyEmotes";
        internal const string HotbarPlusGuid = "FlipMods.HotbarPlus";
        internal const string CoronerGuid = "com.elitemastereric.coroner";
        internal const string BetterExpGuid = "Swaggies.BetterEXP";
        internal const string LateGameUpgradesGuid = "com.malco.lethalcompany.moreshipupgrades";

        internal static bool Contracted => IsLoaded(ContractedGuid);
        internal static bool Y4NGZUi => IsLoaded(Y4NGZUiGuid);
        internal static bool ShipSystems => IsLoaded(ShipSystemsGuid);
        internal static bool LethalCctv => IsLoaded(LethalCctvGuid);
        internal static bool BetterArmory => IsLoaded(BetterArmoryGuid);
        internal static bool HotbarPlus => IsLoaded(HotbarPlusGuid);

        /// <summary>
        /// Set once when the native inventory turned out to be unusable for a reason plugin presence
        /// cannot see - the v81 slot-switch IL contract failing during patching (F-ENF-1). Plugin
        /// presence alone said the provider was available, so Deeper Pockets stayed purchasable
        /// while it could never grant a slot.
        /// </summary>
        private static bool _nativeInventorySuppressed;

        internal static bool NativeInventoryAvailable => !HotbarPlus && !_nativeInventorySuppressed;

        internal static void SuppressNativeInventory(string reason)
        {
            if (_nativeInventorySuppressed)
                return;

            _nativeInventorySuppressed = true;
            Plugin.Log?.LogWarning(
                "[Y4NGZUpgrades] Deeper Pockets is being removed from the catalog: "
                + (string.IsNullOrWhiteSpace(reason) ? "native inventory expansion is unavailable" : reason)
                + ". Levels already bought stay dormant in the save and return when the contract "
                + "validates again.");
        }
        internal static bool Coroner => IsLoaded(CoronerGuid);
        internal static bool BetterExp => IsLoaded(BetterExpGuid);

        /// <summary>
        /// Late Game Upgrades counts as a provider only once <see cref="LguUpgradeBridge"/> has
        /// resolved its whole reflected contract and installed its hooks. Plugin presence alone is
        /// not enough here, unlike every other provider: the bridge sells LGU ranks for tokens, so
        /// a drifted signature that left the bridge inert would otherwise charge a token for a rank
        /// nothing can apply. The bridge fails closed, and this reads that.
        /// </summary>
        internal static bool LateGameUpgrades => LguUpgradeBridge.IsReady;

        internal static OptionalUpgradeProvider LoadedUpgradeProviders
        {
            get
            {
                OptionalUpgradeProvider loaded = OptionalUpgradeProvider.None;
                if (Contracted)
                    loaded |= OptionalUpgradeProvider.Contracted;
                if (ShipSystems)
                    loaded |= OptionalUpgradeProvider.ShipSystems;
                if (LethalCctv)
                    loaded |= OptionalUpgradeProvider.LethalCctv;
                if (BetterArmory)
                    loaded |= OptionalUpgradeProvider.BetterArmory;
                if (NativeInventoryAvailable)
                    loaded |= OptionalUpgradeProvider.NativeInventory;
                if (LateGameUpgrades)
                    loaded |= OptionalUpgradeProvider.LateGameUpgrades;
                return loaded;
            }
        }

        internal static bool HasAll(OptionalUpgradeProvider providers)
        {
            if (providers == OptionalUpgradeProvider.None)
                return true;

            return (!providers.HasFlag(OptionalUpgradeProvider.Contracted) || Contracted)
                   && (!providers.HasFlag(OptionalUpgradeProvider.ShipSystems) || ShipSystems)
                   && (!providers.HasFlag(OptionalUpgradeProvider.LethalCctv) || LethalCctv)
                   && (!providers.HasFlag(OptionalUpgradeProvider.BetterArmory) || BetterArmory)
                   && (!providers.HasFlag(OptionalUpgradeProvider.NativeInventory) || NativeInventoryAvailable)
                   && (!providers.HasFlag(OptionalUpgradeProvider.LateGameUpgrades) || LateGameUpgrades);
        }

        internal static bool IsLoaded(string pluginGuid)
        {
            if (string.IsNullOrWhiteSpace(pluginGuid))
                return false;

            try
            {
                return Chainloader.PluginInfos != null
                       && Chainloader.PluginInfos.ContainsKey(pluginGuid);
            }
            catch
            {
                return false;
            }
        }
    }
}
