using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Effects;

namespace Y4NGZUpgrades.Upgrades
{
    [HarmonyPatch]
    internal static class LoneWolfUpgrade
    {
        public const string UPGRADE_ID = "lone_wolf";
        private static readonly IsolationState Isolation = new IsolationState();
        private static float _nextEvaluation;
        // F-GHOST-14: edge flag for the ineligible branch below.
        private static bool _wasEligible;
        internal static bool LoneWolfActive => Isolation.Active;
        internal static int ActiveLevel { get; private set; }

        internal static void Evaluate(PlayerControllerB player)
        {
            int tier = Y4NGZUpgradeState.GetTier(UPGRADE_ID);
            bool eligible = tier > 0 && player != null && player.isPlayerControlled && !player.isPlayerDead
                && !player.isInHangarShipRoom && StartOfRound.Instance != null && !StartOfRound.Instance.inShipPhase;
            if (!eligible)
            {
                // F-GHOST-14: Evaluate runs from an unthrottled PlayerControllerB.Update postfix, and
                // LoneWolfTrail.Clear() walks up to 120 LineRenderers calling SetActive(false) on each
                // - including ones already inactive. Teardown is a transition, so do it once.
                if (!_wasEligible) return;
                _wasEligible = false;
                Isolation.Reset();
                ActiveLevel = 0;
                LoneWolfTrail.Clear();
                return;
            }
            _wasEligible = true;
            if (Time.time >= _nextEvaluation)
            {
                _nextEvaluation = Time.time + 0.25f;
                float nearest = float.PositiveInfinity;
                var players = StartOfRound.Instance.allPlayerScripts;
                if (players != null)
                    foreach (var other in players)
                    {
                        if (other == null || other == player || other.isPlayerDead || !other.isPlayerControlled
                            || other.isInsideFactory != player.isInsideFactory) continue;
                        nearest = Mathf.Min(nearest, Vector3.Distance(player.transform.position, other.transform.position));
                    }
                bool wasActive = Isolation.Active;
                Isolation.Tick(Time.time, true, nearest);
                ActiveLevel = Isolation.Active ? tier : 0;
                if (wasActive != Isolation.Active)
                    HUDManager.Instance?.DisplayTip("LONE WOLF", Isolation.Active
                        ? "Isolated: stamina recovery enhanced." : "Crew nearby. Lone Wolf ended.");
            }
            LoneWolfTrail.Tick(player, tier >= 3, Isolation.Active && tier >= 3);
        }

        // Measure actual vanilla recovery rather than granting stamina during sprinting,
        // hindrance or exhaustion drains. Runs after Sprinter so it also composes with that bonus.
        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPrefix]
        private static void BeforeLateUpdate(PlayerControllerB __instance, out float __state)
        {
            __state = __instance.sprintMeter;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void AfterLateUpdate(PlayerControllerB __instance, float __state)
        {
            if (__instance != GameNetworkManager.Instance?.localPlayerController || !__instance.IsOwner
                || !LoneWolfActive || __instance.isPlayerDead || !__instance.isPlayerControlled
                || __instance.isInHangarShipRoom || __instance.isSprinting || __instance.isMovementHindered > 0) return;
            float recovered = Mathf.Max(0f, __instance.sprintMeter - __state);
            __instance.sprintMeter = Mathf.Clamp01(__instance.sprintMeter
                + recovered * SurvivalAbilityRules.StaminaRecoveryBonus(ActiveLevel));
            if (__instance.isExhausted && __instance.sprintMeter > 0.2f) __instance.isExhausted = false;
            if (__instance.sprintMeterUI != null) __instance.sprintMeterUI.fillAmount = __instance.sprintMeter;
        }

        internal static void Cleanup()
        {
            Isolation.Reset();
            ActiveLevel = 0;
            _nextEvaluation = 0f;
            // Cleanup has already torn everything down, so the next ineligible frame has nothing
            // left to do (F-GHOST-14).
            _wasEligible = false;
            LoneWolfTrail.Destroy();
        }
    }
}
