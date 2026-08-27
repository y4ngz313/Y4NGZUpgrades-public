using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Interactive;
using Y4NGZUpgrades.Interactive.Hud;
using Y4NGZUpgrades.Upgrades;

#pragma warning disable Harmony003

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ForemanPingPatch
    {
        private const string MSG_MARK_SYNC = "ForemanPing_Mark";
        private const string MSG_REVIVE_REQUEST = "ForemanPing_InspireRequest";
        private const string MSG_REVIVE_RESULT = "ForemanPing_InspireResult";
        private const string MSG_REVIVE_SYNC = "ForemanPing_Revive";
        private const string MSG_POINT_SYNC = "ForemanPing_Point";
        private const string PROMPT_KEY = "foreman_ping";
        private const int POINT_EMOTE_ID = 2;
        private const float POINT_DURATION = 1.0f;
        private const float POINT_FADE_SECONDS = 0.08f;
        private const string STOW_REASON = "foreman-ping-point";
        // Backstop for a pointing layer that never finishes fading out.
        private const float POINT_STOW_TIMEOUT_SECONDS = 4f;
        private const float TARGET_SPHERE_RADIUS = 0.75f;
        private const float TARGET_MAX_ANGLE = 22f;
        private const float TARGET_NETWORK_FALLBACK_RADIUS = 4f;

        private enum MarkKind
        {
            Location = 0,
            Enemy = 1,
            Player = 2,
            Item = 3,
            Interactable = 4,
            Trap = 5,
            Entrance = 6
        }

        private enum InspireResult : byte
        {
            Success = 0,
            NoResponse = 1,
            AlreadyUsed = 2,
            InvalidTarget = 3,
            OutOfRange = 4
        }

        private struct PingTarget
        {
            internal MarkKind Kind;
            internal GameObject Root;
            internal ulong NetworkObjectId;
            internal int PlayerId;
            internal Vector3 Position;
            internal string Label;
        }

        private struct PingCandidate
        {
            internal PingTarget Target;
            internal Collider Collider;
            internal float Distance;
            internal float Angle;
            internal float Score;
            internal string Source;
        }

        private struct RemotePointState
        {
            internal float Until;
            internal float Weight;
            internal HeldItemStowHandle Stow;
        }

        private static readonly Dictionary<ulong, float> MarkedEnemies = new Dictionary<ulong, float>();
        private static readonly Dictionary<int, float> MarkedPlayers = new Dictionary<int, float>();
        private static readonly Dictionary<int, RemotePointState> RemotePointStates = new Dictionary<int, RemotePointState>();
        private static HeldItemStowHandle _localPointStow;
        private static readonly HashSet<ulong> InspireUsedThisRoundByClient = new HashSet<ulong>();

        private static float _nextPingTime;
        private static float _nextInspireTime;
        private static bool _handlersRegistered;
        private static bool _pointingLayerActive;
        private static float _localPointUntil;
        private static float _localPointWeight;
        private static EnemyAI _recentContactEnemy;
        private static float _recentContactExpiresAt;

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            RegisterNetworkHandlers();
            _nextPingTime = 0f;
            _nextInspireTime = 0f;
            ClearServerInspireUses();
            _pointingLayerActive = false;
            _localPointUntil = 0f;
            _localPointWeight = 0f;
            _localPointStow.Dispose();
            _localPointStow = default;
            RemotePointStates.Clear();
            MarkedEnemies.Clear();
            MarkedPlayers.Clear();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            _nextInspireTime = 0f;
            ClearServerInspireUses();
            _pointingLayerActive = false;
            _localPointUntil = 0f;
            _localPointWeight = 0f;
            _localPointStow.Dispose();
            _localPointStow = default;
            RemotePointStates.Clear();
            MarkedEnemies.Clear();
            MarkedPlayers.Clear();
            Y4ngzPromptOverlay.ClearPersistentPrompt(PROMPT_KEY);
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _nextInspireTime = 0f;
            InspireUsedThisRoundByClient.Clear();
            _handlersRegistered = false;
        }

        private static void ClearServerInspireUses()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.IsServer)
                InspireUsedThisRoundByClient.Clear();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            try
            {
                if (__instance == null)
                    return;

                PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
                if (__instance != player)
                {
                    UpdateRemotePointingLayer(__instance, advanceWeight: true);
                    return;
                }

                UpdatePrompt(player);
                UpdateLocalPointingLayer(player, advanceWeight: true);

                if (!Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.ForemanPing))
                    return;

                if (!CanUsePingInput(player))
                    return;

                if (!HasAnyPingFeature())
                    return;

                if (TryHandleInspire(player))
                    return;

                if (Time.time < _nextPingTime)
                    return;

                _nextPingTime = Time.time + PingUpgrade.COOLDOWN_SECONDS;
                PulsePointing(player);
                TryHandlePing(player);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"ForemanPingPatch: PostPlayerUpdate failed: {ex}");
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (__instance == null)
                return;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (__instance == player)
                UpdateLocalPointingLayer(player, advanceWeight: false);
            else
                UpdateRemotePointingLayer(__instance, advanceWeight: false);
        }

        [HarmonyPatch(typeof(EnemyAI), "OnCollideWithPlayer")]
        [HarmonyPrefix]
        private static void PreEnemyCollideWithPlayer(EnemyAI __instance, Collider other)
        {
            if (__instance == null || other == null)
                return;

            PlayerControllerB player = other.GetComponent<PlayerControllerB>();
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || player != local)
                return;

            _recentContactEnemy = __instance;
            _recentContactExpiresAt = Time.time + 0.4f;
        }

        internal static float GetMarkedTeammateDamageReduction(PlayerControllerB player)
        {
            if (player == null)
                return 0f;

            int playerId = (int)player.playerClientId;
            if (!MarkedPlayers.TryGetValue(playerId, out float expiresAt))
                return 0f;

            if (Time.time >= expiresAt)
            {
                MarkedPlayers.Remove(playerId);
                return 0f;
            }

            return PingUpgrade.TEAMMATE_DAMAGE_REDUCTION;
        }

        internal static float GetRecentMarkedEnemyDamageReduction()
        {
            if (_recentContactEnemy == null || Time.time >= _recentContactExpiresAt)
                return 0f;

            if (!IsEnemyMarked(_recentContactEnemy))
                return 0f;

            return PingUpgrade.ENEMY_DAMAGE_REDUCTION;
        }

        private static bool IsEnemyMarked(EnemyAI enemy)
        {
            if (enemy == null)
                return false;

            ulong id = enemy.NetworkObjectId;
            if (!MarkedEnemies.TryGetValue(id, out float expiresAt))
                return false;

            if (Time.time >= expiresAt)
            {
                MarkedEnemies.Remove(id);
                return false;
            }

            return true;
        }

        private static bool CanUsePingInput(PlayerControllerB player)
        {
            return player != null
                && player.IsOwner
                && player.isPlayerControlled
                && !player.isPlayerDead
                && !player.isTypingChat
                && !player.inTerminalMenu
                && (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen);
        }

        private static bool HasAnyPingFeature()
        {
            return PingUpgrade.IsUnlocked() || InspireUpgrade.IsUnlocked();
        }

        private static void TryHandlePing(PlayerControllerB player)
        {
            if (!PingUpgrade.IsUnlocked())
                return;

            if (TryFindPingTarget(player, out PingTarget target))
            {
                ApplyMark(target, PingUpgrade.GetTier(), fromNetwork: false);
                BroadcastMark(target, PingUpgrade.GetTier());
                return;
            }

            Plugin.Log?.LogDebug("ForemanPingPatch: no valid ping target; ignoring ping without location marker.");
        }

        private static bool TryHandleInspire(PlayerControllerB player)
        {
            if (!InspireUpgrade.IsUnlocked())
                return false;

            DeadBodyInfo body = FindLookedAtDeadBody(player);
            if (body == null || body.playerScript == null)
                return false;

            if (Time.time < _nextInspireTime)
            {
                int remainingSeconds = Mathf.CeilToInt(_nextInspireTime - Time.time);
                HUDManager.Instance?.DisplayTip(
                    "INSPIRE",
                    $"Ready in {remainingSeconds}s.",
                    isWarning: true);
                return true;
            }

            int playerId = (int)body.playerScript.playerClientId;
            RequestRevive(playerId);
            return true;
        }

        private static DeadBodyInfo FindLookedAtDeadBody(PlayerControllerB player)
        {
            Camera camera = player.gameplayCamera != null ? player.gameplayCamera : Camera.main;
            if (camera == null)
                return null;

            Ray ray = new Ray(camera.transform.position, camera.transform.forward);
            RaycastHit[] hits = Physics.RaycastAll(ray, InspireUpgrade.REVIVE_RANGE, ~0, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                DeadBodyInfo hitBody = hits[i].collider != null
                    ? hits[i].collider.GetComponentInParent<DeadBodyInfo>()
                    : null;
                if (hitBody != null && hitBody.playerScript != null && hitBody.playerScript.isPlayerDead)
                    return hitBody;
            }

            DeadBodyInfo best = null;
            float bestAngle = 6f;
            DeadBodyInfo[] bodies = UnityEngine.Object.FindObjectsOfType<DeadBodyInfo>();
            for (int i = 0; i < bodies.Length; i++)
            {
                DeadBodyInfo body = bodies[i];
                if (body == null || body.playerScript == null || !body.playerScript.isPlayerDead)
                    continue;

                Vector3 center = ResolveBodyCenter(body);
                if (Vector3.Distance(player.transform.position, center) > InspireUpgrade.REVIVE_RANGE)
                    continue;

                float angle = Vector3.Angle(camera.transform.forward, center - camera.transform.position);
                if (angle < bestAngle)
                {
                    best = body;
                    bestAngle = angle;
                }
            }

            return best;
        }

        private static bool TryFindPingTarget(PlayerControllerB player, out PingTarget target)
        {
            target = default;

            Camera camera = player.gameplayCamera != null ? player.gameplayCamera : Camera.main;
            if (camera == null)
                return false;

            Ray ray = new Ray(camera.transform.position, camera.transform.forward);
            RaycastHit[] rayHits = Physics.RaycastAll(ray, PingUpgrade.TARGET_RANGE, ~0, QueryTriggerInteraction.Collide);
            RaycastHit[] sphereHits = Physics.SphereCastAll(
                ray,
                TARGET_SPHERE_RADIUS,
                PingUpgrade.TARGET_RANGE,
                ~0,
                QueryTriggerInteraction.Collide);

            List<PingCandidate> candidates = new List<PingCandidate>(rayHits.Length + sphereHits.Length);
            AddPingCandidates(player, ray, rayHits, "ray", candidates);
            AddPingCandidates(player, ray, sphereHits, "sphere", candidates);

            if (candidates.Count <= 0)
            {
                Plugin.Log?.LogDebug(
                    $"ForemanPingPatch: target resolution missed. rayHits={rayHits.Length}, sphereHits={sphereHits.Length}");
                return false;
            }

            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            PingCandidate best = candidates[0];
            target = best.Target;
            Plugin.Log?.LogDebug(
                $"ForemanPingPatch: target={target.Kind}:{target.Label} source={best.Source} collider={DescribeCollider(best.Collider)} distance={best.Distance:F1} angle={best.Angle:F1} root={(target.Root != null ? target.Root.name : "null")} net={target.NetworkObjectId}");
            return true;
        }

        private static void AddPingCandidates(
            PlayerControllerB player,
            Ray ray,
            RaycastHit[] hits,
            string source,
            List<PingCandidate> candidates)
        {
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            HashSet<GameObject> seenRoots = new HashSet<GameObject>();
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null)
                    continue;

                PlayerControllerB hitPlayer = collider.GetComponentInParent<PlayerControllerB>();
                if (hitPlayer == player)
                    continue;

                PingTarget resolved;
                if (!TryResolveTarget(collider, hits[i].point, out resolved))
                    continue;

                GameObject key = resolved.Root != null ? resolved.Root : collider.gameObject;
                if (key != null && seenRoots.Contains(key))
                    continue;

                if (key != null)
                    seenRoots.Add(key);

                Vector3 aimPoint = resolved.Position;
                if (aimPoint == Vector3.zero)
                    aimPoint = hits[i].point;

                Vector3 toTarget = aimPoint - ray.origin;
                float distance = Mathf.Max(0f, toTarget.magnitude);
                float angle = distance > 0.01f
                    ? Vector3.Angle(ray.direction, toTarget / distance)
                    : 0f;

                if (angle > TARGET_MAX_ANGLE)
                    continue;

                float sourcePenalty = source == "ray" ? 0f : 3f;
                float score = distance + angle * 1.75f + KindScorePenalty(resolved.Kind) + sourcePenalty;
                candidates.Add(new PingCandidate
                {
                    Target = resolved,
                    Collider = collider,
                    Distance = distance,
                    Angle = angle,
                    Score = score,
                    Source = source
                });
            }
        }

        private static bool TryResolveTarget(Collider collider, Vector3 hitPoint, out PingTarget target)
        {
            target = default;

            EnemyAI enemy = collider.GetComponentInParent<EnemyAI>();
            if (enemy != null && !enemy.isEnemyDead)
            {
                target = BuildTarget(MarkKind.Enemy, enemy.gameObject, enemy.NetworkObjectId, -1, ResolveEnemyCenter(enemy), "enemy");
                return true;
            }

            PlayerControllerB player = collider.GetComponentInParent<PlayerControllerB>();
            if (player != null && player.isPlayerControlled && !player.isPlayerDead && PingUpgrade.CanMarkTeammates())
            {
                target = BuildTarget(MarkKind.Player, player.gameObject, player.NetworkObjectId, (int)player.playerClientId, ResolvePlayerCenter(player), player.playerUsername);
                return true;
            }

            GrabbableObject item = collider.GetComponentInParent<GrabbableObject>();
            if (item != null)
            {
                GameObject root = ResolveRenderableRoot(item.gameObject, item.gameObject);
                target = BuildTarget(MarkKind.Item, root, item.NetworkObjectId, -1, ResolveRendererCenter(root, item.transform.position), item.itemProperties != null ? item.itemProperties.itemName : "item");
                return true;
            }

            EntranceTeleport entrance = collider.GetComponentInParent<EntranceTeleport>();
            if (entrance != null)
            {
                GameObject root = ResolveRenderableRoot(entrance.gameObject, entrance.gameObject);
                string label = entrance.entranceId == 0 ? "main entrance" : "fire exit";
                target = BuildTarget(MarkKind.Entrance, root, entrance.NetworkObjectId, -1, ResolveRendererCenter(root, ResolveEntranceCenter(entrance)), label);
                return true;
            }

            return false;
        }

        private static float KindScorePenalty(MarkKind kind)
        {
            switch (kind)
            {
                case MarkKind.Enemy:
                case MarkKind.Player:
                    return 0f;
                case MarkKind.Trap:
                    return 2f;
                case MarkKind.Entrance:
                    return 4f;
                case MarkKind.Item:
                    return 5f;
                case MarkKind.Interactable:
                    return 8f;
                default:
                    return 12f;
            }
        }

        private static PingTarget BuildTarget(MarkKind kind, GameObject root, ulong networkObjectId, int playerId, Vector3 position, string label)
        {
            return new PingTarget
            {
                Kind = kind,
                Root = root,
                NetworkObjectId = networkObjectId,
                PlayerId = playerId,
                Position = position,
                Label = string.IsNullOrWhiteSpace(label) ? kind.ToString().ToLowerInvariant() : label
            };
        }

        private static ulong ResolveNetworkObjectId(GameObject root)
        {
            NetworkObject net = root != null ? root.GetComponentInParent<NetworkObject>() : null;
            return net != null ? net.NetworkObjectId : 0UL;
        }

        private static void ApplyMark(PingTarget target, int pingTier, bool fromNetwork)
        {
            float expiresAt = Time.time + PingUpgrade.MARK_DURATION;

            if (target.Kind == MarkKind.Enemy && target.NetworkObjectId != 0UL && pingTier >= 2)
                MarkedEnemies[target.NetworkObjectId] = expiresAt;

            if (target.Kind == MarkKind.Player && target.PlayerId >= 0 && pingTier >= 3)
                MarkedPlayers[target.PlayerId] = expiresAt;

            if (target.Root != null)
            {
                bool shown = OutlineEffectBridge.Show(
                    target.Root,
                    OutlineChannelFor(target.Kind),
                    PingUpgrade.MARK_DURATION,
                    UpgradeOutlineSource.Ping);
                Plugin.Log?.LogInfo(
                    $"ForemanPingPatch: mark {(fromNetwork ? "recv" : "local")} kind={target.Kind} label={target.Label} root={target.Root.name} shown={shown} net={target.NetworkObjectId} player={target.PlayerId}");
            }
            else
            {
                Plugin.Log?.LogInfo(
                    $"ForemanPingPatch: mark {(fromNetwork ? "recv" : "local")} kind={target.Kind} label={target.Label} had no root; no marker spawned.");
            }
        }

        internal static void MarkTeammateFromCommandNet(PlayerControllerB player)
        {
            if (player == null)
                return;

            PingTarget target = BuildTarget(
                MarkKind.Player,
                player.gameObject,
                ResolveNetworkObjectId(player.gameObject),
                (int)player.playerClientId,
                ResolvePlayerCenter(player),
                "damaged teammate");

            ApplyMark(target, pingTier: 2, fromNetwork: true);
            BroadcastMark(target, pingTier: 2);
        }

        private static void UpdatePrompt(PlayerControllerB player)
        {
            if (!PingUpgrade.IsUnlocked())
            {
                Y4ngzPromptOverlay.ClearPersistentPrompt(PROMPT_KEY);
                return;
            }

            bool gated = player == null
                || player.isPlayerDead
                || player.isTypingChat
                || player.inTerminalMenu
                || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen);
            if (gated)
            {
                Y4ngzPromptOverlay.ClearPersistentPrompt(PROMPT_KEY);
                return;
            }

            string bindingPath = Gui.UpgradeInput.EffectivePath(Gui.Plugin.Keybinds?.ForemanPing);
            if (_cachedPromptText == null || !string.Equals(_cachedPromptPath, bindingPath, StringComparison.Ordinal))
            {
                _cachedPromptPath = bindingPath;
                string label = Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.ForemanPing, "Q");
                _cachedPromptText = $"[{label}] to ping";
            }

            Y4ngzPromptOverlay.SetPersistentPrompt(PROMPT_KEY, _cachedPromptText);
        }

        private static string _cachedPromptText;
        private static string _cachedPromptPath;

        private static void PulsePointing(PlayerControllerB player)
        {
            if (player == null || player.playerBodyAnimator == null)
                return;

            // Stow before the emote claims the arms, so a held weapon's third-person session
            // tears down instead of pointing along with the hand.
            if (!_localPointStow.IsActive)
            {
                _localPointStow = HeldItemStowService.Begin(
                    player,
                    STOW_REASON,
                    POINT_STOW_TIMEOUT_SECONDS,
                    force: true);
            }

            _localPointUntil = Time.time + POINT_DURATION;
            _pointingLayerActive = true;
            _localPointWeight = 1f;
            Animator animator = player.playerBodyAnimator;
            animator.SetInteger("emoteNumber", POINT_EMOTE_ID);
            ApplyPointingLayer(player, animator, _localPointWeight);
            BroadcastPointing(player, POINT_DURATION);
        }

        private static void UpdateLocalPointingLayer(PlayerControllerB player, bool advanceWeight)
        {
            if (player == null || player.playerBodyAnimator == null)
                return;

            Animator animator = player.playerBodyAnimator;
            bool shouldPoint = Time.time < _localPointUntil;
            if (shouldPoint)
            {
                animator.SetInteger("emoteNumber", POINT_EMOTE_ID);
                _pointingLayerActive = true;
            }

            if (!_pointingLayerActive && _localPointWeight <= 0f)
                return;

            if (advanceWeight)
                _localPointWeight = shouldPoint ? 1f : MovePointWeight(_localPointWeight, 0f);
            ApplyPointingLayer(player, animator, _localPointWeight);
            if (advanceWeight && !shouldPoint && _localPointWeight <= 0.001f)
            {
                _localPointWeight = 0f;
                _pointingLayerActive = false;
                animator.SetInteger("emoteNumber", 0);
                ApplyPointingLayer(player, animator, 0f);
                _localPointStow.Dispose();
                _localPointStow = default;
            }
        }

        private static void StartRemotePointing(int playerId, float duration)
        {
            if (playerId < 0)
                return;

            RemotePointState state;
            RemotePointStates.TryGetValue(playerId, out state);
            state.Until = Time.time + Mathf.Clamp(duration, 0.1f, POINT_DURATION + 0.25f);
            if (state.Weight < 1f)
                state.Weight = 1f;
            if (!state.Stow.IsActive)
            {
                state.Stow = HeldItemStowService.Begin(
                    ResolvePlayerById(playerId),
                    STOW_REASON,
                    POINT_STOW_TIMEOUT_SECONDS,
                    force: true);
            }
            RemotePointStates[playerId] = state;
        }

        private static void UpdateRemotePointingLayer(PlayerControllerB player, bool advanceWeight)
        {
            if (player == null || player.playerBodyAnimator == null)
                return;

            int playerId = (int)player.playerClientId;
            RemotePointState state;
            if (!RemotePointStates.TryGetValue(playerId, out state))
                return;

            Animator animator = player.playerBodyAnimator;
            bool shouldPoint = Time.time < state.Until;
            if (shouldPoint)
                animator.SetInteger("emoteNumber", POINT_EMOTE_ID);

            if (advanceWeight)
                state.Weight = shouldPoint ? 1f : MovePointWeight(state.Weight, 0f);
            ApplyPointingLayer(player, animator, state.Weight);
            if (advanceWeight && !shouldPoint && state.Weight <= 0.001f)
            {
                animator.SetInteger("emoteNumber", 0);
                ApplyPointingLayer(player, animator, 0f);
                state.Stow.Dispose();
                RemotePointStates.Remove(playerId);
                return;
            }

            RemotePointStates[playerId] = state;
        }

        private static PlayerControllerB ResolvePlayerById(int playerId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || playerId < 0 || playerId >= players.Length)
                return null;
            return players[playerId];
        }

        private static float MovePointWeight(float current, float target)
        {
            float step = POINT_FADE_SECONDS > 0f ? Time.deltaTime / POINT_FADE_SECONDS : 1f;
            return Mathf.MoveTowards(current, target, Mathf.Clamp01(step));
        }

        private static void ApplyPointingLayer(PlayerControllerB player, Animator animator, float weight)
        {
            if (player == null || animator == null)
                return;

            weight = Mathf.Clamp01(weight);
            player.emoteLayerWeight = weight;
            SetLayerWeight(animator, "EmotesNoArms", weight);
        }

        private static void SetLayerWeight(Animator animator, string layerName, float weight)
        {
            if (animator == null || string.IsNullOrWhiteSpace(layerName))
                return;

            int layer = animator.GetLayerIndex(layerName);
            if (layer >= 0)
                animator.SetLayerWeight(layer, weight);
        }

        private static UpgradeOutlineChannel OutlineChannelFor(MarkKind kind)
        {
            switch (kind)
            {
                case MarkKind.Enemy:
                case MarkKind.Trap:
                    return UpgradeOutlineChannel.Danger;
                case MarkKind.Player:
                    return UpgradeOutlineChannel.Friendly;
                default:
                    return UpgradeOutlineChannel.Utility;
            }
        }

        private static Vector3 ResolveEnemyCenter(EnemyAI enemy)
        {
            if (enemy == null) return Vector3.zero;
            if (enemy.eye != null) return enemy.eye.position;

            Renderer renderer = null;
            if (enemy.skinnedMeshRenderers != null)
            {
                for (int i = 0; i < enemy.skinnedMeshRenderers.Length; i++)
                {
                    if (enemy.skinnedMeshRenderers[i] == null) continue;
                    renderer = enemy.skinnedMeshRenderers[i];
                    break;
                }
            }

            if (renderer == null && enemy.meshRenderers != null)
            {
                for (int i = 0; i < enemy.meshRenderers.Length; i++)
                {
                    if (enemy.meshRenderers[i] == null) continue;
                    renderer = enemy.meshRenderers[i];
                    break;
                }
            }

            return renderer != null ? renderer.bounds.center : enemy.transform.position + Vector3.up * 1.2f;
        }

        private static GameObject ResolveRenderableRoot(GameObject preferredRoot, GameObject fallbackRoot)
        {
            if (HasUsableRenderer(preferredRoot))
                return preferredRoot;

            if (HasUsableRenderer(fallbackRoot))
                return fallbackRoot;

            Transform cursor = preferredRoot != null ? preferredRoot.transform.parent : null;
            while (cursor != null)
            {
                if (HasUsableRenderer(cursor.gameObject))
                    return cursor.gameObject;

                NetworkObject networkObject = cursor.GetComponent<NetworkObject>();
                if (networkObject != null)
                    return cursor.gameObject;

                cursor = cursor.parent;
            }

            return preferredRoot != null ? preferredRoot : fallbackRoot;
        }

        private static bool HasUsableRenderer(GameObject root)
        {
            if (root == null)
                return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: false);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;
                if (renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                    continue;

                return true;
            }

            return false;
        }

        private static Vector3 ResolveRendererCenter(GameObject root, Vector3 fallback)
        {
            if (root == null)
                return fallback;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: false);
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;
                if (renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds ? bounds.center : fallback;
        }

        private static Vector3 ResolvePlayerCenter(PlayerControllerB player)
        {
            if (player == null) return Vector3.zero;
            if (player.playerGlobalHead != null) return player.playerGlobalHead.position;
            if (player.thisPlayerBody != null) return player.thisPlayerBody.position + Vector3.up * 1.4f;
            return player.transform.position + Vector3.up * 1.4f;
        }

        private static Vector3 ResolveEntranceCenter(EntranceTeleport entrance)
        {
            if (entrance == null) return Vector3.zero;
            if (entrance.entrancePoint != null) return entrance.entrancePoint.position + Vector3.up * 1.2f;
            return entrance.transform.position + Vector3.up * 1.2f;
        }

        private static Vector3 ResolveBodyCenter(DeadBodyInfo body)
        {
            if (body == null) return Vector3.zero;
            if (body.bodyParts != null && body.bodyParts.Length > 5 && body.bodyParts[5] != null)
                return body.bodyParts[5].position;
            return body.transform.position + Vector3.up * 0.8f;
        }

        private static void RevivePlayerLocal(int playerId, Vector3 revivePosition)
        {
            if (StartOfRound.Instance == null || playerId < 0 || playerId >= StartOfRound.Instance.allPlayerScripts.Length)
                return;

            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerId];
            if (player == null || !player.isPlayerDead)
                return;

            player.isPlayerDead = false;
            player.isPlayerControlled = true;
            player.health = 100;
            player.criticallyInjured = false;
            player.hasBeenCriticallyInjured = false;
            player.bleedingHeavily = false;
            player.disableMoveInput = false;
            player.disableLookInput = false;
            player.disableInteract = false;
            player.inSpecialInteractAnimation = false;
            player.isClimbingLadder = false;
            player.isHoldingObject = false;
            player.currentlyHeldObjectServer = null;
            player.carryWeight = 1f;
            player.externalForceAutoFade = Vector3.zero;
            player.spectatedPlayerScript = null;
            player.overrideGameOverSpectatePivot = null;
            player.hasBegunSpectating = false;
            player.setPositionOfDeadPlayer = false;
            player.isInElevator = false;
            player.isInHangarShipRoom = false;

            if (player.thisController != null)
                player.thisController.enabled = true;

            player.DisablePlayerModel(player.gameObject, enable: true, disableLocalArms: true);
            player.Crouch(crouch: false);
            player.TeleportPlayer(revivePosition + Vector3.up * 0.25f);

            if (player.playerBodyAnimator != null)
            {
                player.playerBodyAnimator.SetBool("Limp", false);
                player.playerBodyAnimator.SetBool("crouching", false);
            }

            if (player.mapRadarDotAnimator != null)
                player.mapRadarDotAnimator.SetBool("dead", false);

            DeadBodyInfo body = player.deadBody;
            player.deadBody = null;
            if (body != null)
                UnityEngine.Object.Destroy(body.gameObject);

            StartOfRound.Instance.livingPlayers = Mathf.Min(
                StartOfRound.Instance.connectedPlayersAmount + 1,
                StartOfRound.Instance.livingPlayers + 1);
            StartOfRound.Instance.allPlayersDead = false;
            StartOfRound.Instance.SendChangedWeightEvent();
            StartOfRound.Instance.UpdatePlayerVoiceEffects();

            if (player.IsOwner)
            {
                HUDManager hud = HUDManager.Instance;
                if (hud != null)
                {
                    hud.RemoveSpectateUI();
                    hud.gameOverAnimator.SetTrigger("revive");
                    hud.UpdateHealthUI(player.health, hurtPlayer: false);
                    hud.SetCracksOnVisor(100f);
                }
            }
            else if (GameNetworkManager.Instance?.localPlayerController != null
                     && GameNetworkManager.Instance.localPlayerController.isPlayerDead)
            {
                HUDManager.Instance?.UpdateBoxesSpectateUI();
            }
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered) return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_MARK_SYNC, OnReceiveMark);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_REVIVE_REQUEST, OnReceiveReviveRequest);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_REVIVE_RESULT, OnReceiveReviveResult);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_REVIVE_SYNC, OnReceiveRevive);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_POINT_SYNC, OnReceivePointing);
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanPingPatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

        private static void RequestRevive(int playerId)
        {
            RegisterNetworkHandlers();

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.CustomMessagingManager == null)
                return;

            if (network.IsServer)
            {
                HandleReviveRequestOnServer(network.LocalClientId, playerId);
                return;
            }

            if (!network.IsClient)
                return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
            try
            {
                writer.WriteValueSafe(playerId);
                network.CustomMessagingManager.SendNamedMessage(
                    MSG_REVIVE_REQUEST,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void BroadcastPointing(PlayerControllerB player, float duration)
        {
            RegisterNetworkHandlers();
            if (player == null)
                return;

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient || network.CustomMessagingManager == null)
                return;

            int playerId = (int)player.playerClientId;
            if (network.IsServer)
                SendPointMessage(playerId, duration, null);
            else
                SendPointMessage(playerId, duration, NetworkManager.ServerClientId);
        }

        private static void SendPointMessage(int playerId, float duration, ulong? clientId)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(float), Allocator.Temp);
            try
            {
                writer.WriteValueSafe(playerId);
                writer.WriteValueSafe(duration);
                if (clientId.HasValue)
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                        MSG_POINT_SYNC,
                        clientId.Value,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                else
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                        MSG_POINT_SYNC,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceivePointing(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
                return;

            try
            {
                int playerId;
                float duration;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out duration);

                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
                bool isLocalPlayer = local != null && (int)local.playerClientId == playerId;
                if (!isLocalPlayer)
                    StartRemotePointing(playerId, duration);

                if (NetworkManager.Singleton != null
                    && NetworkManager.Singleton.IsServer
                    && senderClientId != NetworkManager.Singleton.LocalClientId)
                {
                    SendPointMessage(playerId, duration, null);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"ForemanPingPatch: malformed point sync: {ex.Message}");
            }
        }

        private static void BroadcastMark(PingTarget target, int pingTier)
        {
            RegisterNetworkHandlers();

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            if (network.CustomMessagingManager == null) return;

            if (network.IsServer)
            {
                SendMarkMessageToAll(target, pingTier);
            }
            else
            {
                SendMarkMessage(target, pingTier, NetworkManager.ServerClientId);
            }
        }

        private static void SendMarkMessageToAll(PingTarget target, int pingTier)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;
            SendMarkMessage(target, pingTier, null);
        }

        private static void SendMarkMessage(PingTarget target, int pingTier, ulong? clientId)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 3 + sizeof(ulong) + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe((int)target.Kind);
                writer.WriteValueSafe(target.NetworkObjectId);
                writer.WriteValueSafe(target.PlayerId);
                writer.WriteValueSafe(target.Position.x);
                writer.WriteValueSafe(target.Position.y);
                writer.WriteValueSafe(target.Position.z);
                writer.WriteValueSafe(pingTier);
                if (clientId.HasValue)
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                        MSG_MARK_SYNC,
                        clientId.Value,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                else
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                        MSG_MARK_SYNC,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveMark(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
                return;

            try
            {
                int kindValue;
                ulong netId;
                int playerId;
                float x, y, z;
                int pingTier;
                reader.ReadValueSafe(out kindValue);
                reader.ReadValueSafe(out netId);
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out x);
                reader.ReadValueSafe(out y);
                reader.ReadValueSafe(out z);
                reader.ReadValueSafe(out pingTier);

                PingTarget target = ResolveNetworkTarget((MarkKind)kindValue, netId, playerId, new Vector3(x, y, z));
                ApplyMark(target, pingTier, fromNetwork: true);
                if (NetworkManager.Singleton != null
                    && NetworkManager.Singleton.IsServer
                    && senderClientId != NetworkManager.Singleton.LocalClientId)
                {
                    BroadcastMark(target, pingTier);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanPingPatch: malformed mark sync: {ex.Message}");
            }
        }

        private static PingTarget ResolveNetworkTarget(MarkKind kind, ulong netId, int playerId, Vector3 position)
        {
            GameObject root = null;
            if (kind == MarkKind.Player && StartOfRound.Instance != null && playerId >= 0 && playerId < StartOfRound.Instance.allPlayerScripts.Length)
            {
                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerId];
                if (player != null)
                    root = player.gameObject;
            }

            if (root == null && netId != 0UL && NetworkManager.Singleton != null)
            {
                NetworkObject netObject;
                if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out netObject) && netObject != null)
                    root = netObject.gameObject;
            }

            if (root == null)
                root = FindTargetNear(kind, position);

            return new PingTarget
            {
                Kind = kind,
                Root = root,
                NetworkObjectId = netId,
                PlayerId = playerId,
                Position = position,
                Label = kind.ToString().ToLowerInvariant()
            };
        }

        private static GameObject FindTargetNear(MarkKind kind, Vector3 position)
        {
            if (position == Vector3.zero)
                return null;

            GameObject best = null;
            float bestSqr = TARGET_NETWORK_FALLBACK_RADIUS * TARGET_NETWORK_FALLBACK_RADIUS;

            switch (kind)
            {
                case MarkKind.Enemy:
                    FindClosestEnemy(position, ref best, ref bestSqr);
                    break;
                case MarkKind.Item:
                    FindClosestComponent<GrabbableObject>(position, ref best, ref bestSqr);
                    break;
                case MarkKind.Entrance:
                    FindClosestComponent<EntranceTeleport>(position, ref best, ref bestSqr);
                    break;
            }

            return best;
        }

        private static void FindClosestEnemy(Vector3 position, ref GameObject best, ref float bestSqr)
        {
            EnemyAI[] enemies = UnityEngine.Object.FindObjectsOfType<EnemyAI>();
            for (int i = 0; i < enemies.Length; i++)
            {
                EnemyAI enemy = enemies[i];
                if (enemy == null || enemy.isEnemyDead)
                    continue;

                ConsiderClosest(enemy.gameObject, ResolveEnemyCenter(enemy), position, ref best, ref bestSqr);
            }
        }

        private static void FindClosestComponent<T>(Vector3 position, ref GameObject best, ref float bestSqr) where T : Component
        {
            T[] components = UnityEngine.Object.FindObjectsOfType<T>();
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (component == null)
                    continue;

                GameObject root = ResolveRenderableRoot(component.gameObject, component.gameObject);
                ConsiderClosest(root, ResolveRendererCenter(root, component.transform.position), position, ref best, ref bestSqr);
            }
        }

        private static void ConsiderClosest(
            GameObject root,
            Vector3 center,
            Vector3 position,
            ref GameObject best,
            ref float bestSqr)
        {
            if (root == null)
                return;

            float sqr = (center - position).sqrMagnitude;
            if (sqr >= bestSqr)
                return;

            best = root;
            bestSqr = sqr;
        }

        private static string DescribeCollider(Collider collider)
        {
            if (collider == null)
                return "null";

            return $"{collider.gameObject.name}/{collider.GetType().Name}";
        }

        private static void OnReceiveReviveRequest(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return;

            try
            {
                int playerId;
                reader.ReadValueSafe(out playerId);
                HandleReviveRequestOnServer(senderClientId, playerId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanPingPatch: malformed revive request: {ex.Message}");
            }
        }

        private static void HandleReviveRequestOnServer(ulong requesterClientId, int playerId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return;

            if (InspireUsedThisRoundByClient.Contains(requesterClientId))
            {
                SendReviveResult(requesterClientId, InspireResult.AlreadyUsed);
                return;
            }

            PlayerControllerB requester = FindPlayerByClientId(requesterClientId);
            if (requester == null || requester.isPlayerDead || !requester.isPlayerControlled)
            {
                SendReviveResult(requesterClientId, InspireResult.InvalidTarget);
                return;
            }

            PlayerControllerB target = null;
            if (StartOfRound.Instance != null
                && playerId >= 0
                && playerId < StartOfRound.Instance.allPlayerScripts.Length)
            {
                target = StartOfRound.Instance.allPlayerScripts[playerId];
            }

            DeadBodyInfo body = target != null ? target.deadBody : null;
            if (target == null
                || !target.isPlayerDead
                || body == null
                || body.playerScript != target)
            {
                SendReviveResult(requesterClientId, InspireResult.InvalidTarget);
                return;
            }

            Vector3 bodyPosition = ResolveBodyCenter(body);
            if (Vector3.Distance(requester.transform.position, bodyPosition) > InspireUpgrade.REVIVE_RANGE)
            {
                SendReviveResult(requesterClientId, InspireResult.OutOfRange);
                return;
            }

            if (UnityEngine.Random.value > InspireUpgrade.REVIVE_CHANCE)
            {
                SendReviveResult(requesterClientId, InspireResult.NoResponse);
                return;
            }

            InspireUsedThisRoundByClient.Add(requesterClientId);
            RevivePlayerLocal(playerId, bodyPosition);
            BroadcastRevive(playerId, bodyPosition);
            SendReviveResult(requesterClientId, InspireResult.Success);
        }

        private static PlayerControllerB FindPlayerByClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == clientId)
                    return player;
            }

            return null;
        }

        private static void SendReviveResult(ulong requesterClientId, InspireResult result)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || network.CustomMessagingManager == null)
                return;

            if (network.IsClient && requesterClientId == network.LocalClientId)
            {
                ShowReviveResult(result);
                return;
            }

            FastBufferWriter writer = new FastBufferWriter(sizeof(byte), Allocator.Temp);
            try
            {
                writer.WriteValueSafe((byte)result);
                network.CustomMessagingManager.SendNamedMessage(
                    MSG_REVIVE_RESULT,
                    requesterClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveReviveResult(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                byte result;
                reader.ReadValueSafe(out result);
                ShowReviveResult((InspireResult)result);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanPingPatch: malformed revive result: {ex.Message}");
            }
        }

        private static void ShowReviveResult(InspireResult result)
        {
            switch (result)
            {
                case InspireResult.Success:
                    _nextInspireTime = Time.time + InspireUpgrade.COOLDOWN_SECONDS;
                    break;
                case InspireResult.NoResponse:
                    HUDManager.Instance?.DisplayTip("INSPIRE", "No response...", isWarning: true);
                    break;
                case InspireResult.AlreadyUsed:
                    HUDManager.Instance?.DisplayTip("INSPIRE", "Already used this round.", isWarning: true);
                    break;
                case InspireResult.OutOfRange:
                    HUDManager.Instance?.DisplayTip("INSPIRE", "Move closer to the body.", isWarning: true);
                    break;
                case InspireResult.InvalidTarget:
                    HUDManager.Instance?.DisplayTip("INSPIRE", "Target is no longer available.", isWarning: true);
                    break;
            }
        }

        private static void BroadcastRevive(int playerId, Vector3 revivePosition)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || network.CustomMessagingManager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(playerId);
                writer.WriteValueSafe(revivePosition.x);
                writer.WriteValueSafe(revivePosition.y);
                writer.WriteValueSafe(revivePosition.z);
                network.CustomMessagingManager.SendNamedMessageToAll(
                    MSG_REVIVE_SYNC,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveRevive(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.IsServer || senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                int playerId;
                float x, y, z;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out x);
                reader.ReadValueSafe(out y);
                reader.ReadValueSafe(out z);
                RevivePlayerLocal(playerId, new Vector3(x, y, z));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanPingPatch: malformed revive sync: {ex.Message}");
            }
        }
    }
}

#pragma warning restore Harmony003
