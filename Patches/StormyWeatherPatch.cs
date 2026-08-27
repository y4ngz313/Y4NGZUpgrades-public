using System;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Light Feet Tier 3+ (Grounded effect): the local player is immune to lightning damage.
    /// Uses a flag-based approach: sets a static flag during LightningStrike execution,
    /// then prefixes KillPlayer/DamagePlayer to skip if the flag is set and the player owns at least Tier 3.
    /// </summary>
    [HarmonyPatch]
    internal static class StormyWeatherPatch
    {
        internal static bool IsLightningStrike = false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StormyWeather), "LightningStrike")]
        private static void LightningStrikePrefix()
        {
            IsLightningStrike = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StormyWeather), "LightningStrike")]
        private static void LightningStrikePostfix()
        {
            IsLightningStrike = false;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(StormyWeather), "LightningStrike")]
        private static Exception LightningStrikeFinalizer(Exception __exception)
        {
            // A postfix is skipped when LightningStrike throws. Never leave the
            // damage guard active for an unrelated later player-damage call.
            IsLightningStrike = false;
            return __exception;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        private static bool PreventLightningKill(PlayerControllerB __instance)
        {
            try
            {
                if (IsLightningStrike
                    && __instance == GameNetworkManager.Instance.localPlayerController
                    && LightFeetUpgrade.HasTier(LightFeetUpgrade.TIER_GROUNDED))
                {
                    Plugin.Log.LogDebug("Light Feet T3: Prevented lightning kill on local player.");
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"StormyWeatherPatch.PreventLightningKill failed: {e}");
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        private static bool PreventLightningDamage(PlayerControllerB __instance)
        {
            try
            {
                if (IsLightningStrike
                    && __instance == GameNetworkManager.Instance.localPlayerController
                    && LightFeetUpgrade.HasTier(LightFeetUpgrade.TIER_GROUNDED))
                {
                    Plugin.Log.LogDebug("Light Feet T3: Prevented lightning damage on local player.");
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"StormyWeatherPatch.PreventLightningDamage failed: {e}");
            }
            return true;
        }
    }
}
