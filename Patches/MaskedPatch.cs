using System;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// NPC was removed from the upgrade catalog. This patch remains inert so old
    /// saved NPC levels cannot keep Masked enemies disabled.
    /// Two protection layers:
    /// 1) Postfix on EnemyAI.CheckLineOfSightForClosestPlayer + GetClosestPlayer - clears the
    ///    local player from masked enemies' candidate target results.
    /// 2) Prefix on MaskedPlayerEnemy.OnCollideWithPlayer - skips the kill animation.
    /// 3) Postfix on MaskedPlayerEnemy.DoAIInterval - clears any stale targetPlayer reference.
    /// </summary>
    [HarmonyPatch]
    internal static class MaskedPatch
    {
        private static bool LocalPlayerIsInvisibleToMasked()
        {
            return false;
        }

        /// <summary>
        /// Prevents Masked from acquiring the local player as a target via line of sight.
        /// On the server (host), this fully prevents targeting.
        /// On non-host clients, this prevents client-side target awareness.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnemyAI), "CheckLineOfSightForClosestPlayer")]
        private static void PreventMaskedTargeting(EnemyAI __instance, ref PlayerControllerB __result)
        {
            try
            {
                if (__instance is MaskedPlayerEnemy
                    && __result != null
                    && __result == GameNetworkManager.Instance.localPlayerController
                    && LocalPlayerIsInvisibleToMasked())
                {
                    __result = null;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"MaskedPatch.PreventMaskedTargeting failed: {e}");
            }
        }

        /// <summary>
        /// Prevents Masked from acquiring the local player via GetClosestPlayer proximity checks.
        /// MaskedPlayerEnemy.DoAIInterval uses GetClosestPlayer when no LOS target is found.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnemyAI), "GetClosestPlayer")]
        private static void PreventMaskedProximityTarget(EnemyAI __instance, ref PlayerControllerB __result)
        {
            try
            {
                if (__instance is MaskedPlayerEnemy
                    && __result != null
                    && __result == GameNetworkManager.Instance.localPlayerController
                    && LocalPlayerIsInvisibleToMasked())
                {
                    __result = null;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"MaskedPatch.PreventMaskedProximityTarget failed: {e}");
            }
        }

        /// <summary>
        /// Prevents the Masked kill animation from triggering on the local player.
        /// Final safety net - even if the Masked somehow reaches the player,
        /// the collision handler won't start the kill sequence.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaskedPlayerEnemy), "OnCollideWithPlayer")]
        private static bool PreventMaskedKill(Collider other)
        {
            try
            {
                PlayerControllerB player = other.gameObject.GetComponent<PlayerControllerB>();
                if (player != null
                    && player == GameNetworkManager.Instance.localPlayerController
                    && LocalPlayerIsInvisibleToMasked())
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"MaskedPatch.PreventMaskedKill failed: {e}");
            }
            return true;
        }

        /// <summary>
        /// Clears stale targetPlayer references after each AI tick - Masked enemies are stateful
        /// and will continue chasing a player they already targeted unless we clear the reference.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MaskedPlayerEnemy), "DoAIInterval")]
        private static void ClearMaskedTarget(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (__instance.targetPlayer != null
                    && __instance.targetPlayer == GameNetworkManager.Instance.localPlayerController
                    && LocalPlayerIsInvisibleToMasked())
                {
                    __instance.targetPlayer = null;
                    __instance.movingTowardsTargetPlayer = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"MaskedPatch.ClearMaskedTarget failed: {e}");
            }
        }
    }
}
