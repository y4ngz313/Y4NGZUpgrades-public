using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;

namespace Y4NGZUpgrades.Interactive.Patches
{
    /// <summary>
    /// Drives <see cref="HeldItemStowService"/> from vanilla state: any special interaction
    /// (ladder, CCTV station, mainframe, terminal-style props) puts a mod weapon or a two-handed
    /// item away for its duration, and death / drop / round-end tear the bookkeeping down.
    /// </summary>
    [HarmonyPatch]
    internal static class HeldItemStowPatches
    {
        private const string SpecialInteractReason = "special-interact";

        // Per-instance so a remote player's ladder climb is detected on that player, not on
        // whichever controller happened to Update first.
        private static readonly Dictionary<PlayerControllerB, bool> SpecialInteractState =
            new Dictionary<PlayerControllerB, bool>();

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (__instance == null)
                return;

            bool inSpecial = __instance.inSpecialInteractAnimation || __instance.isClimbingLadder;
            SpecialInteractState.TryGetValue(__instance, out bool wasInSpecial);
            if (inSpecial != wasInSpecial)
            {
                SpecialInteractState[__instance] = inSpecial;
                if (inSpecial)
                {
                    // Not forced: a one-handed vanilla item on a ladder keeps vanilla behavior.
                    HeldItemStowService.Begin(__instance, SpecialInteractReason);
                }
                else
                {
                    HeldItemStowService.End(__instance, SpecialInteractReason);
                }
            }

            // Tick() deliberately does NOT run here any more (#211): the local player's Update is
            // the first thing to stop when a stow strands on player teardown, so the janitor runs
            // on Y4NGZPersistentRunner instead.
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPrefix]
        private static void PreKillPlayer(PlayerControllerB __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            // No restore: the item stays in ItemSlots so the vanilla death drop still finds it.
            HeldItemStowService.Clear(__instance);
            if (__instance != null)
                SpecialInteractState.Remove(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DropAllHeldItems")]
        [HarmonyPrefix]
        private static void PreDropAllHeldItems(PlayerControllerB __instance)
        {
            HeldItemStowService.Clear(__instance);
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            HeldItemStowService.AbortAll("round-end");
            SpecialInteractState.Clear();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            HeldItemStowService.AbortAll("disconnect");
            SpecialInteractState.Clear();
        }
    }
}
