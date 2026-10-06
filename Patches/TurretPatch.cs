using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Light Feet Tier 2+ makes turrets hesitate after spotting a player instead of making that
    /// player invisible to turret line-of-sight checks.
    ///
    /// The decision is host-authoritative (F-INFRA-4 / F-GHOST-1). In v81 <c>Turret.Update</c>'s
    /// <c>case TurretMode.Charging:</c> breaks out on <c>!base.IsServer</c> before
    /// <c>turretInterval</c> is ever incremented or compared, so clamping it on a client wrote a
    /// field nobody reads: only the host got the reprieve, and only for its own player. The target's
    /// tier therefore has to come over <see cref="UpgradeTierSync"/>.
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
                // Only the server drives Charging -> Firing, so only the server can hold fire.
                NetworkManager network = NetworkManager.Singleton;
                if (network == null || !network.IsServer)
                    return;

                if (!ShouldDelayForTarget(__instance))
                {
                    ClearDelay(__instance);
                    return;
                }

                int key = __instance.GetInstanceID();
                if (!LightFeetDelayUntilByTurret.TryGetValue(key, out float delayUntil))
                {
                    // F-GHOST-3: the vanilla 1.5 s charge runs inside this window, so measuring the
                    // whole delay from the first Charging frame bought about 1.0 s, not the 2.5
                    // EXTRA seconds the catalog promises. Stack it on top of the vanilla charge.
                    delayUntil = Time.time
                        + VanillaChargingSeconds
                        + LightFeetUpgrade.TURRET_FIRE_DELAY_SECONDS;
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

        private static bool ShouldDelayForTarget(Turret turret)
        {
            if (turret == null || turret.turretMode != TurretMode.Charging)
                return false;

            PlayerControllerB target = turret.targetPlayerWithRotation;
            if (target == null)
                return false;

            // GetTier short-circuits to the local tier for the host's own player, so this one
            // lookup covers the host and every client.
            return UpgradeTierSync.GetTier(target.actualClientId, LightFeetUpgrade.UPGRADE_ID)
                   >= LightFeetUpgrade.TIER_TURRET_DELAY;
        }

        /// <summary>
        /// F-GHOST-7. Entries are otherwise only dropped by a turret that keeps updating, so a
        /// destroyed turret leaks its entry for the session and a turret that inherits its Unity
        /// instance id starts with a <c>delayUntil</c> in the past - i.e. no delay at all.
        /// </summary>
        internal static void OnRoundStarted()
        {
            LightFeetDelayUntilByTurret.Clear();
        }

        internal static void ClearAllDelays()
        {
            LightFeetDelayUntilByTurret.Clear();
        }

        private static void ClearDelay(Turret turret)
        {
            if (turret == null)
                return;

            LightFeetDelayUntilByTurret.Remove(turret.GetInstanceID());
        }
    }

    /// <summary>
    /// Separate class so the class-level <c>[HarmonyPatch(typeof(Turret))]</c> above cannot leak
    /// into this unrelated target (F-GHOST-7).
    /// </summary>
    [HarmonyPatch]
    internal static class TurretPatchLifecycle
    {
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void AfterDisconnect()
        {
            TurretPatch.ClearAllDelays();
        }
    }
}
