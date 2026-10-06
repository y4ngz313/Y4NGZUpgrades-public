using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class SquadSightPatch
    {
        private const float TargetRefreshInterval = 0.20f;
        // Room + Colliders, the same mask the vanilla scanner uses for line of sight.
        // Internal so ForemanPingPatch's occlusion test uses the identical mask (F-FOREMAN-A-9).
        internal const int ScannerLineOfSightMask = 134217984;
        private const float FogOutlineDistance = 10f;

        private static readonly Dictionary<Component, GameObject> ActiveOutlines =
            new Dictionary<Component, GameObject>();
        private static readonly HashSet<Component> SeenThisRefresh = new HashSet<Component>();

        private static float _nextRefreshTime;

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostLateUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            if (!BuddySystemUpgrade.IsUnlocked() || __instance.isPlayerDead)
            {
                ClearAll();
                return;
            }

            if (Time.time < _nextRefreshTime)
                return;

            _nextRefreshTime = Time.time + TargetRefreshInterval;
            RefreshTargets(__instance);
        }

        private static void RefreshTargets(PlayerControllerB localPlayer)
        {
            SeenThisRefresh.Clear();

            float range = BuddySystemUpgrade.GetRange();
            if (range <= 0f)
            {
                ClearAll();
                return;
            }

            float rangeSqr = range * range;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players != null)
            {
                for (int i = 0; i < players.Length; i++)
                {
                    PlayerControllerB teammate = players[i];
                    if (teammate == null || teammate == localPlayer) continue;
                    if (!teammate.isPlayerControlled || teammate.isPlayerDead) continue;

                    Vector3 center = ResolvePlayerCenter(teammate);
                    if ((center - localPlayer.transform.position).sqrMagnitude > rangeSqr) continue;

                    RefreshOneTarget(localPlayer, teammate, teammate.gameObject, center);
                }
            }

            List<Component> stale = null;
            foreach (KeyValuePair<Component, GameObject> pair in ActiveOutlines)
            {
                if (pair.Key != null && SeenThisRefresh.Contains(pair.Key))
                    continue;

                if (stale == null) stale = new List<Component>();
                stale.Add(pair.Key);
            }

            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
                RemoveOutline(stale[i]);
        }

        private static void RefreshOneTarget(
            PlayerControllerB localPlayer,
            Component key,
            GameObject targetRoot,
            Vector3 targetCenter)
        {
            SeenThisRefresh.Add(key);

            float distance = Vector3.Distance(localPlayer.transform.position, targetCenter);
            bool shouldOutline = !HasReliableDirectVisibility(localPlayer, targetRoot, targetCenter, distance);

            if (!shouldOutline)
            {
                RemoveOutline(key);
                return;
            }

            if (OutlineEffectBridge.Show(
                    targetRoot,
                    UpgradeOutlineChannel.Friendly,
                    TargetRefreshInterval * 3f,
                    UpgradeOutlineSource.BuddySystem))
            {
                ActiveOutlines[key] = targetRoot;
            }
        }

        private static bool HasReliableDirectVisibility(
            PlayerControllerB localPlayer,
            GameObject targetRoot,
            Vector3 targetCenter,
            float distance)
        {
            Camera camera = localPlayer.gameplayCamera != null ? localPlayer.gameplayCamera : Camera.main;
            if (camera == null) return false;

            Vector3 viewport = camera.WorldToViewportPoint(targetCenter);
            if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                return false;

            if (FogLikelyObscures(localPlayer, distance))
                return false;

            RaycastHit hit;
            if (!Physics.Linecast(
                    camera.transform.position,
                    targetCenter,
                    out hit,
                    ScannerLineOfSightMask,
                    QueryTriggerInteraction.Ignore))
            {
                return true;
            }

            return hit.collider != null
                && targetRoot != null
                && hit.collider.transform.IsChildOf(targetRoot.transform);
        }

        private static bool FogLikelyObscures(PlayerControllerB localPlayer, float distance)
        {
            if (localPlayer == null || distance < FogOutlineDistance) return false;

            if (localPlayer.isInsideFactory)
            {
                if (RoundManager.Instance == null || RoundManager.Instance.indoorFog == null) return false;
                return RoundManager.Instance.indoorFog.gameObject.activeSelf;
            }

            // F-FOREMAN-A-16: the catalog promises "walls or fog", but only RoundManager's
            // interior fog volume was ever consulted, so a foggy moon - the one place a player
            // expects the outline - never produced one outdoors. The ship interior is excluded
            // because the weather volume does not reach inside it.
            if (localPlayer.isInHangarShipRoom || localPlayer.isInElevator) return false;

            return TimeOfDay.Instance != null
                && TimeOfDay.Instance.currentLevelWeather == LevelWeatherType.Foggy;
        }

        private static Vector3 ResolvePlayerCenter(PlayerControllerB player)
        {
            if (player == null) return Vector3.zero;
            if (player.playerGlobalHead != null) return player.playerGlobalHead.position;
            if (player.thisPlayerBody != null) return player.thisPlayerBody.position + Vector3.up * 1.4f;
            return player.transform.position + Vector3.up * 1.4f;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null) return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static void RemoveOutline(Component key)
        {
            GameObject targetRoot;
            if (ActiveOutlines.TryGetValue(key, out targetRoot) && targetRoot != null)
                OutlineEffectBridge.Clear(targetRoot, UpgradeOutlineSource.BuddySystem);

            ActiveOutlines.Remove(key);
        }

        private static void ClearAll()
        {
            foreach (KeyValuePair<Component, GameObject> pair in ActiveOutlines)
            {
                if (pair.Value != null)
                    OutlineEffectBridge.Clear(pair.Value, UpgradeOutlineSource.BuddySystem);
            }

            ActiveOutlines.Clear();
            SeenThisRefresh.Clear();
            _nextRefreshTime = 0f;
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            ClearAll();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            ClearAll();
        }

    }
}
