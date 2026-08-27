using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Interactive.Feedback;
using UnityEngine;

namespace Y4NGZUpgrades.Interactive.Patches
{
    [HarmonyPatch]
    internal static class CombatFeedbackPatch
    {
        private const int BurnHitId = -9401;
        private const float DamageSourceValidityWindow = 1.25f;
        private const float DamageSourceValidityRange = 26f;
        private const float NearestEnemyDamageRange = 18f;
        private const float FeedbackSampleInterval = 0.05f;

        private static EnemyAI _lastPotentialDamageSource;
        private static float _lastPotentialDamageSourceTime = -999f;
        private static float _nextFeedbackSampleTime;

        // The hit-marker pulse on EnemyAI.HitEnemyOnLocalClient moved to
        // BetterArmory.ArmoryCombatReticlePatch with the reticle it draws into (#266). This class
        // keeps the danger-feedback half: enemy hit flash, directional damage and the low-health
        // vignette. Both sides postfix the same vanilla methods, so they compose.

        [HarmonyPatch(typeof(EnemyAI), "HitEnemy")]
        [HarmonyPostfix]
        private static void PostHitEnemy(EnemyAI __instance, int force, int hitID)
        {
            if (__instance == null || force == 0) return;
            if (hitID == BurnHitId) return;
            EnemyHitFlash.Pulse(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (__instance == null) return;
            if (__instance != GameNetworkManager.Instance?.localPlayerController) return;

            if (Time.unscaledTime < _nextFeedbackSampleTime) return;
            _nextFeedbackSampleTime = Time.unscaledTime + FeedbackSampleInterval;

            PlayerDangerHud.UpdatePlayerState(__instance);
        }

        [HarmonyPatch(typeof(EnemyAI), "OnCollideWithPlayer")]
        [HarmonyPrefix]
        private static void PreEnemyAIOnCollideWithPlayer(EnemyAI __instance, Collider other)
        {
            if (__instance == null || other == null) return;
            PlayerControllerB player = other.GetComponent<PlayerControllerB>();
            if (player == null || player != GameNetworkManager.Instance?.localPlayerController) return;

            _lastPotentialDamageSource = __instance;
            _lastPotentialDamageSourceTime = Time.time;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPostfix]
        private static void PostDamagePlayer(PlayerControllerB __instance, int damageNumber, bool fallDamage, CauseOfDeath causeOfDeath)
        {
            if (__instance == null || __instance != GameNetworkManager.Instance?.localPlayerController) return;
            if (damageNumber <= 0) return;

            PlayerDangerHud.PulseDamage(damageNumber);

            Vector3 sourcePosition;
            if (TryResolveDamageSource(__instance, fallDamage, causeOfDeath, out sourcePosition))
                DirectionalDamageHud.ShowFromWorldPosition(__instance, sourcePosition, damageNumber);
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            _lastPotentialDamageSource = null;
            _lastPotentialDamageSourceTime = -999f;
            _nextFeedbackSampleTime = 0f;
            DirectionalDamageHud.DestroyInstance();
            PlayerDangerHud.DestroyInstance();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            _lastPotentialDamageSource = null;
            _lastPotentialDamageSourceTime = -999f;
            _nextFeedbackSampleTime = 0f;
            DirectionalDamageHud.DestroyInstance();
            PlayerDangerHud.DestroyInstance();
        }

        private static bool TryResolveDamageSource(
            PlayerControllerB player,
            bool fallDamage,
            CauseOfDeath causeOfDeath,
            out Vector3 sourcePosition)
        {
            sourcePosition = Vector3.zero;
            if (player == null || fallDamage) return false;

            EnemyAI recent = GetRecentPotentialDamageSource(player);
            if (recent != null)
            {
                sourcePosition = recent.transform.position;
                return true;
            }

            if (!LooksLikeEnemyDamage(causeOfDeath)) return false;

            EnemyAI closest = FindClosestEnemy(player);
            if (closest == null) return false;

            sourcePosition = closest.transform.position;
            return true;
        }

        private static EnemyAI GetRecentPotentialDamageSource(PlayerControllerB player)
        {
            if (_lastPotentialDamageSource == null) return null;
            if (_lastPotentialDamageSource.isEnemyDead) return null;
            if (Time.time - _lastPotentialDamageSourceTime > DamageSourceValidityWindow) return null;
            if (Vector3.Distance(player.transform.position, _lastPotentialDamageSource.transform.position) > DamageSourceValidityRange) return null;
            return _lastPotentialDamageSource;
        }

        private static EnemyAI FindClosestEnemy(PlayerControllerB player)
        {
            EnemyAI closest = null;
            float closestDist = NearestEnemyDamageRange;
            EnemyAI[] enemies = Object.FindObjectsOfType<EnemyAI>();
            for (int i = 0; i < enemies.Length; i++)
            {
                EnemyAI enemy = enemies[i];
                if (enemy == null || enemy.isEnemyDead) continue;
                float dist = Vector3.Distance(player.transform.position, enemy.transform.position);
                if (dist >= closestDist) continue;

                closest = enemy;
                closestDist = dist;
            }

            return closest;
        }

        private static bool LooksLikeEnemyDamage(CauseOfDeath cause)
        {
            switch (cause)
            {
                case CauseOfDeath.Mauling:
                case CauseOfDeath.Bludgeoning:
                case CauseOfDeath.Strangulation:
                case CauseOfDeath.Stabbing:
                case CauseOfDeath.Crushing:
                case CauseOfDeath.Suffocation:
                case CauseOfDeath.Kicking:
                case CauseOfDeath.Unknown:
                    return true;
                default:
                    return false;
            }
        }
    }
}
