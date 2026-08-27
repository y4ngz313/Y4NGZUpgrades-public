using HarmonyLib;

namespace Y4NGZUpgrades.Lucky8
{
    [HarmonyPatch]
    internal static class Lucky8Patches
    {
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.Awake))]
        [HarmonyPostfix]
        private static void StartOfRoundAwakePostfix()
        {
            if (!Lucky8Manager.IsInitialized) return;
            Lucky8RuntimeAssets.TryRegisterCapsulePrefab();
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.Update))]
        [HarmonyPostfix]
        private static void RoundManagerUpdatePostfix()
        {
            if (!Lucky8Manager.IsInitialized) return;
            Lucky8Manager.UpdateRuntime();
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        [HarmonyPostfix]
        private static void ShipLeavePostfix()
        {
            if (!Lucky8Manager.IsInitialized) return;
            Lucky8Manager.ResetVisit();
        }

        [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.ItemActivate))]
        [HarmonyPostfix]
        private static void ItemActivatePostfix(GrabbableObject __instance, bool buttonDown)
        {
            if (!Lucky8Manager.IsInitialized || !buttonDown || __instance == null) return;
            __instance.GetComponent<Lucky8ClaimCapsule>()?.TryClaimLocal(__instance);
        }
    }
}
