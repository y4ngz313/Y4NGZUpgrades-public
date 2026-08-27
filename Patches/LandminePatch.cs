using System;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch(typeof(Landmine))]
    internal static class LandminePatch
    {
        /// <summary>
        /// Prefix on TriggerMineOnLocalClientByExiting.
        /// Tier 1+ of Light Feet: the local player's footsteps don't detonate mines.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("TriggerMineOnLocalClientByExiting")]
        private static bool PreventMineExplosion()
        {
            try
            {
                if (LightFeetUpgrade.HasTier(LightFeetUpgrade.TIER_LIGHT_FEET))
                {
                    Plugin.Log.LogDebug("Light Feet T1: Prevented landmine explosion for local player.");
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"LandminePatch.PreventMineExplosion failed: {e}");
            }
            return true;
        }
    }
}
