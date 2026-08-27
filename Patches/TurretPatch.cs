using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Light Feet Tier 2+ makes turrets hesitate after spotting the local player
    /// instead of making the player invisible to turret line-of-sight checks.
    /// </summary>
    [HarmonyPatch(typeof(Turret))]
    internal static class TurretPatch
    {
        private const float VanillaChargingSeconds = 1.5f;
        private const float ChargingClampPadding = 0.05f;

        private static readonly Dictionary<int, float> LightFeetDelayUntilByTurret =
            new Dictionary<int, float>();

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static void DelayLightFeetFiringPrefix(Turret __instance)
        {
            try
            {
                if (!ShouldDelayForLocalPlayer(__instance))
                {
                    ClearDelay(__instance);
                    return;
                }

                int key = __instance.GetInstanceID();
                if (!LightFeetDelayUntilByTurret.TryGetValue(key, out float delayUntil))
                {
                    delayUntil = Time.time + LightFeetUpgrade.TURRET_FIRE_DELAY_SECONDS;
                    LightFeetDelayUntilByTurret[key] = delayUntil;
                }

                if (Time.time < delayUntil)
                    __instance.turretInterval = Mathf.Min(
                        __instance.turretInterval,
                        VanillaChargingSeconds - ChargingClampPadding);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"TurretPatch.DelayLightFeetFiringPrefix failed: {e}");
            }
        }

        private static bool ShouldDelayForLocalPlayer(Turret turret)
        {
            if (turret == null || turret.turretMode != TurretMode.Charging)
                return false;

            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            return localPlayer != null
                && turret.targetPlayerWithRotation == localPlayer
                && LightFeetUpgrade.HasTier(LightFeetUpgrade.TIER_TURRET_DELAY);
        }

        private static void ClearDelay(Turret turret)
        {
            if (turret == null)
                return;

            LightFeetDelayUntilByTurret.Remove(turret.GetInstanceID());
        }
    }
}
