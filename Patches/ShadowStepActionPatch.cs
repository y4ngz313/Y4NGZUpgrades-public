using GameNetcodeStuff;
using HarmonyLib;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ShadowStepActionPatch
    {
        // Flashlight Update can re-enable its bulb after cloak reconciliation. Reassert only
        // the captured cosmetic lights in LateUpdate, after all item Update calls have run.
        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void AfterPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (__instance == GameNetworkManager.Instance?.localPlayerController)
                Effects.CamoShellCloak.EnforceHiddenLights();
        }

        // Activation covers misses, windups, thrown explosives and Better Armory's ShotgunItem
        // carriers. Flashlights, doors, pickups and other ordinary item uses keep their cloak.
        [HarmonyPatch(typeof(GrabbableObject), "UseItemOnClient")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void BeforeUse(GrabbableObject __instance, bool buttonDown)
        {
            if (!buttonDown || __instance == null) return;
            PlayerControllerB player = __instance.playerHeldBy;
            if (player == null || player != GameNetworkManager.Instance?.localPlayerController) return;
            if (__instance is Shovel || __instance is KnifeItem || __instance is ShotgunItem
                || __instance is StunGrenadeItem || __instance.GetType().Name == "PatcherTool")
                ShadowStepPatch.BreakCloakForAction(player);
        }
    }
}
