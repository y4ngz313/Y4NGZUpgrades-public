using System;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch(typeof(Landmine))]
    internal static class LandminePatch
    {
        // F-GHOST-13: the mine still plays its arming click, so the only thing that told a player
        // Light Feet had just saved them was a Debug-level log line. One tip, rate-limited because
        // a minefield reaches this several times a second and a crossed mine can re-trigger.
        private const float SaveTipCooldownSeconds = 60f;
        private static float _nextSaveTipAt;

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
                    NotifyMineSave();
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"LandminePatch.PreventMineExplosion failed: {e}");
            }
            return true;
        }

        private static void NotifyMineSave()
        {
            if (Time.time < _nextSaveTipAt) return;

            _nextSaveTipAt = Time.time + SaveTipCooldownSeconds;
            HUDManager.Instance?.DisplayTip("LIGHT FEET", "The mine ignored your step.");
        }
    }
}
