using HarmonyLib;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Prefix guard for DepositItemsDesk.UpdateEffects. If the container or any of its audio
    /// sources are null (can happen when items are sold mid-round under certain mod combos)
    /// the original method throws and spams the log; skipping in that state is harmless.
    ///
    /// Field names updated to match the current Lethal Company v70 API:
    ///   deskAudioSource  -> deskAudio
    ///   counterTopAudio  -> speakerAudio
    /// </summary>
    [HarmonyPatch(typeof(DepositItemsDesk))]
    internal class DepositItemsDeskPatch
    {
        [HarmonyPatch("UpdateEffects")]
        [HarmonyPrefix]
        static bool UpdateEffectsPrefix(DepositItemsDesk __instance)
        {
            if (__instance.deskAudio == null ||
                __instance.deskObjectsContainer == null ||
                __instance.speakerAudio == null)
            {
                return false; // Don't run the original method
            }
            return true;
        }
    }
}
