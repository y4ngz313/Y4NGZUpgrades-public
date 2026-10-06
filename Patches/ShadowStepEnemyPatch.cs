using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ShadowStepEnemyPatch
    {
        [ThreadStatic] private static EnemyAI _visualSelection;

        // Bracken stalking uses TargetClosestPlayer without requesting LOS. Scope this exception
        // to candidate selection; the same enemy's physical collision is still fully dangerous.
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.TargetClosestPlayer))]
        [HarmonyPrefix]
        private static void BeforeTargetSelection(EnemyAI __instance, bool requireLineOfSight, out EnemyAI __state)
        {
            __state = _visualSelection;
            _visualSelection = requireLineOfSight || __instance is FlowermanAI ? __instance : null;
            if (_visualSelection != null && ShadowStepPatch.IsPlayerCloaked(__instance.targetPlayer))
                __instance.targetPlayer = null; // Prevent vanilla's old-target distance buffer restoring cloak.
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.TargetClosestPlayer))]
        [HarmonyFinalizer]
        private static void AfterTargetSelection(EnemyAI __state) => _visualSelection = __state;

        internal static void FilterVisualCandidate(EnemyAI __instance, PlayerControllerB playerScript, ref bool __result)
        {
            if (_visualSelection == __instance && ShadowStepPatch.IsPlayerCloaked(playerScript)) __result = false;
        }

        [HarmonyPatch(typeof(ForestGiantAI), "LookForPlayers")]
        [HarmonyPrefix]
        private static void BeforeGiantLook(ForestGiantAI __instance)
        {
            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || __instance.playerStealthMeters == null) return;
            for (int i = 0; i < players.Length && i < __instance.playerStealthMeters.Length; i++)
                if (ShadowStepPatch.IsPlayerCloaked(players[i])) __instance.playerStealthMeters[i] = 0f;
        }

        [HarmonyPatch(typeof(ForestGiantAI), "LookForPlayers")]
        [HarmonyPostfix]
        private static void AfterGiantLook(ForestGiantAI __instance)
        {
            if (ShadowStepPatch.IsPlayerCloaked(__instance.chasingPlayer))
                LoseGiantTarget(__instance, __instance.chasingPlayer);
        }

        internal static void LoseGiantTarget(ForestGiantAI giant, PlayerControllerB player)
        {
            if (!giant.IsOwner || player == null || giant.chasingPlayer != player
                || giant.currentBehaviourStateIndex != 1) return;
            giant.chasingPlayerInLOS = false;
            if (!giant.lostPlayerInChase)
            {
                giant.lostPlayerInChase = true;
                giant.noticePlayerTimer = 0f;
                giant.movingTowardsTargetPlayer = false;
                giant.SetDestinationToPosition(giant.lastSeenPlayerPositionInChase, checkForPath: true);
            }
        }
    }

    /// <summary>
    /// F-SHADOW-4a: <c>EnemyAI.PlayerIsTargetable</c> is <c>public virtual</c> and <c>PumaAI</c>
    /// overrides it. A <c>[HarmonyPatch(typeof(EnemyAI), ...)]</c> binds the base body only, so a
    /// Puma's <c>TargetClosestPlayer</c> selection never saw the cloak filter at all.
    ///
    /// Enumerating the base method plus every declared override means a subclass override added by
    /// a future game version is covered automatically instead of silently bypassing the cloak.
    /// Turrets are deliberately out of scope: <c>Turret.CheckForPlayersInLineOfSight</c> is not on
    /// <c>EnemyAI</c>, and Light Feet L2 owns turret visibility.
    /// </summary>
    [HarmonyPatch]
    internal static class ShadowStepTargetableOverridePatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new List<MethodBase>();
            MethodInfo baseMethod = AccessTools.Method(typeof(EnemyAI), nameof(EnemyAI.PlayerIsTargetable));
            if (baseMethod != null) targets.Add(baseMethod);

            Type[] signature =
            {
                typeof(PlayerControllerB), typeof(bool), typeof(bool), typeof(bool)
            };

            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(EnemyAI).Assembly))
            {
                if (type == null || type == typeof(EnemyAI) || !typeof(EnemyAI).IsAssignableFrom(type))
                    continue;

                MethodInfo declared = type.GetMethod(
                    nameof(EnemyAI.PlayerIsTargetable),
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, signature, null);
                if (declared != null) targets.Add(declared);
            }

            return targets;
        }

        [HarmonyPostfix]
        private static void FilterVisualCandidate(EnemyAI __instance, PlayerControllerB playerScript, ref bool __result)
        {
            ShadowStepEnemyPatch.FilterVisualCandidate(__instance, playerScript, ref __result);
        }
    }
}
