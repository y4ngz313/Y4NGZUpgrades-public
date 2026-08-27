using HarmonyLib;

namespace Y4NGZUpgrades.Upgrades
{
    [HarmonyPatch]
    internal static class Y4NGZUpgradePatches
    {
        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPostfix]
        private static void PostStartOfRoundStart()
        {
            Y4NGZUpgradeManager.SwitchToCurrentSave();
        }
    }
}
