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
        internal const string ShipSystemsGuid = "com.y4ngz.company.shipsystems";
        internal const string LethalCctvGuid = "com.y4ngz.company.lethalcctv";
        internal const string BetterArmoryGuid = "com.y4ngz.betterarmory";
        internal const string BloodFxGuid = "com.y4ngz.bloodfx";
        internal const string MoreCompanyGuid = "me.swipez.melonloader.morecompany";
        internal const string TooManyEmotesGuid = "FlipMods.TooManyEmotes";
        internal const string HotbarPlusGuid = "FlipMods.HotbarPlus";
        internal const string CoronerGuid = "com.elitemastereric.coroner";
        internal const string BetterExpGuid = "Swaggies.BetterEXP";

        internal static bool Contracted => IsLoaded(ContractedGuid);
        internal static bool ShipSystems => IsLoaded(ShipSystemsGuid);
        internal static bool LethalCctv => IsLoaded(LethalCctvGuid);
        internal static bool BetterArmory => IsLoaded(BetterArmoryGuid);
        internal static bool HotbarPlus => IsLoaded(HotbarPlusGuid);
        internal static bool NativeInventoryAvailable => !HotbarPlus;
        internal static bool Coroner => IsLoaded(CoronerGuid);
        internal static bool BetterExp => IsLoaded(BetterExpGuid);

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
                   && (!providers.HasFlag(OptionalUpgradeProvider.NativeInventory) || NativeInventoryAvailable);
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
