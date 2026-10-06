using HarmonyLib;
using GameNetcodeStuff;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch(typeof(PlayerControllerB))]
    internal static class PlayerControllerBPatch
    {
        // -
        //  LONE WOLF - Activation poll + stamina bonus
        // -
        //
        // WP12 dead-code sweep: the Salvager death-item glow used to live here (a KillPlayer /
        // KillPlayerClientRpc / DropAllHeldItems prefix-postfix quartet plus a SalvagerGlow
        // MonoBehaviour). "Salvager" was never a catalog id or display name, so
        // Y4NGZUpgradeState.GetActiveUpgrade was permanently false and none of it could run.
        // SalvagerUpgrade is gone with it; only the live Lone Wolf poll below remains.

        /// <summary>
        /// Drives Lone Wolf from the local player's Update: a throttled poll re-derives whether
        /// the local player has been isolated long enough. Recovery is applied around LateUpdate.
        ///
        /// Lone Wolf used to activate from a KillPlayerClientRpc postfix too. It no longer does:
        /// a death is only one of the ways the survivor count changes, and an edge trigger could
        /// never see a teammate walking back out of the ship or being revived (#363 audit L1/L2).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        private static void UpdatePostfix(PlayerControllerB __instance)
        {
            if (!__instance.IsOwner || !__instance.isPlayerControlled) return;
            if (__instance != GameNetworkManager.Instance?.localPlayerController) return;

            LoneWolfUpgrade.Evaluate(__instance);

        }
    }

    // -
    //  LONE WOLF - Round end / disconnect cleanup
    // -

    /// <summary>
    /// Teardown only. The per-round activation and deactivation transitions are handled by the
    /// poll in <see cref="PlayerControllerBPatch.UpdatePostfix"/>; the old ReviveDeadPlayers
    /// special case is gone because a revived teammate is inside the ship and the poll sees it.
    /// These hooks clear the isolation state, HUD and personal trail at teardown.
    /// </summary>
    [HarmonyPatch]
    internal static class LoneWolfCleanupPatch
    {
        /// <summary>The ship leaving ends the run; drop the buff without waiting for the poll.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        private static void ShipLeavePostfix()
        {
            LoneWolfUpgrade.Cleanup();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        private static void EndOfGamePostfix()
        {
            LoneWolfUpgrade.Cleanup();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        private static void DisconnectPostfix()
        {
            LoneWolfUpgrade.Cleanup();
        }
    }
}
