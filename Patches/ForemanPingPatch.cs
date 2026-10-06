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
        private const int POINT_EMOTE_ID = 2;
        private const float POINT_DURATION = 1.0f;
        private const float POINT_RAISE_SECONDS = 0.14f;
        private const float POINT_RETURN_SECONDS = 0.20f;
        private const string STOW_REASON = "foreman-ping-point";
        // Backstop for a pointing layer that never finishes fading out.
        private const float POINT_STOW_TIMEOUT_SECONDS = 4f;
        private const float TARGET_SPHERE_RADIUS = 0.75f;
        private const float TARGET_MAX_ANGLE = 22f;
        private const float TARGET_NETWORK_FALLBACK_RADIUS = 4f;
        // F-FOREMAN-A-19: melee that routes through a ServerRpc -> ClientRpc round trip
        // (Nutcracker's leg kick) can land well after OnCollideWithPlayer on a laggy link,
        // so the contact window has to outlive one RTT rather than a single physics step.
        private const float ENEMY_CONTACT_WINDOW_SECONDS = 1.5f;
        // Location marker idle bob, so a static world marker still reads as "placed just now".
        private const float LOCATION_MARKER_BOB_SPEED = 2.2f;
        private const float LOCATION_MARKER_BOB_HEIGHT = 0.12f;

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
            OutOfRange = 4,
            // Append-only: the server's own retry stamp rejected the request (F-FOREMAN-A-2).
            OnCooldown = 5
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
            internal float BlendFrom;
            internal float BlendTarget;
            internal float BlendStartedAt;
            internal HeldItemStowHandle Stow;
        }

        private static readonly Dictionary<ulong, float> MarkedEnemies = new Dictionary<ulong, float>();
        private static readonly Dictionary<int, float> MarkedPlayers = new Dictionary<int, float>();
        private static readonly Dictionary<int, RemotePointState> RemotePointStates = new Dictionary<int, RemotePointState>();
        private static HeldItemStowHandle _localPointStow;
        private static readonly HashSet<ulong> InspireUsedThisRoundByClient = new HashSet<ulong>();
        // F-FOREMAN-A-2: server-side retry stamp per client, so a modified client that ignores
        // its own cooldown cannot re-roll the 50% every frame.
        private static readonly Dictionary<ulong, float> InspireRetryAtByClient = new Dictionary<ulong, float>();
        // F-FOREMAN-A-19: keyed per enemy instead of a single "most recent" slot, so a second
        // enemy touching the player cannot evict the marked one out of the window.
        private static readonly Dictionary<EnemyAI, float> RecentEnemyContacts = new Dictionary<EnemyAI, float>();
        private static readonly List<EnemyAI> ExpiredContactBuffer = new List<EnemyAI>();

        private static float _nextPingTime;
        private static float _nextInspireTime;
        private static float _nextInspireTipTime;
        private static bool _handlersRegistered;
        private static bool _pointingLayerActive;
        private static float _localPointUntil;
        private static float _localPointWeight;
        private static float _localPointBlendFrom;
        private static float _localPointBlendTarget;
        private static float _localPointBlendStartedAt;

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            RegisterNetworkHandlers();
            _nextPingTime = 0f;
            _nextInspireTime = 0f;
            _nextInspireTipTime = 0f;
            ClearServerInspireUses();
            _pointingLayerActive = false;
            _localPointUntil = 0f;
            _localPointWeight = 0f;
            _localPointBlendFrom = 0f;
            _localPointBlendTarget = 0f;
            _localPointBlendStartedAt = 0f;
            _localPointStow.Dispose();
            _localPointStow = default;
            RemotePointStates.Clear();
            MarkedEnemies.Clear();
            MarkedPlayers.Clear();
            RecentEnemyContacts.Clear();
            LocationMarker.DestroyAll();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            _nextInspireTime = 0f;
            _nextInspireTipTime = 0f;
            ClearServerInspireUses();
            _pointingLayerActive = false;
            _localPointUntil = 0f;
            _localPointWeight = 0f;
            _localPointBlendFrom = 0f;
            _localPointBlendTarget = 0f;
            _localPointBlendStartedAt = 0f;
            _localPointStow.Dispose();
            _localPointStow = default;
            RemotePointStates.Clear();
            MarkedEnemies.Clear();
            MarkedPlayers.Clear();
            RecentEnemyContacts.Clear();
            LocationMarker.DestroyAll();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _nextInspireTime = 0f;
            _nextInspireTipTime = 0f;
            InspireUsedThisRoundByClient.Clear();
            InspireRetryAtByClient.Clear();
            RecentEnemyContacts.Clear();
            LocationMarker.DestroyAll();
            _handlersRegistered = false;
        }

        private static void ClearServerInspireUses()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.IsServer)
            {
                InspireUsedThisRoundByClient.Clear();
                InspireRetryAtByClient.Clear();
            }
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

                // F-FOREMAN-A-10: only a ping that actually produced a mark charges the 3s
                // cooldown. Aiming at nothing used to cost a silent three-second lockout.
                if (!TryHandlePing(player))
                    return;

                _nextPingTime = Time.time + PingUpgrade.COOLDOWN_SECONDS;
                PulsePointing(player);
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

            PruneExpiredContacts();
            RecentEnemyContacts[__instance] = Time.time + ENEMY_CONTACT_WINDOW_SECONDS;
        }

        // Runs per physics contact, not per frame, and reuses a static buffer so the wider
        // window does not trade a bug for allocation churn.
        private static void PruneExpiredContacts()
        {
            if (RecentEnemyContacts.Count <= 0)
                return;

            float now = Time.time;
            ExpiredContactBuffer.Clear();
            foreach (KeyValuePair<EnemyAI, float> pair in RecentEnemyContacts)
            {
                if (now < pair.Value && pair.Key != null)
                    continue;

                ExpiredContactBuffer.Add(pair.Key);
            }

            for (int i = 0; i < ExpiredContactBuffer.Count; i++)
                RecentEnemyContacts.Remove(ExpiredContactBuffer[i]);
            ExpiredContactBuffer.Clear();
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
            if (RecentEnemyContacts.Count <= 0)
                return 0f;

            // Dictionary<K,V> has a struct enumerator, so this stays allocation-free on the
            // damage path. First marked, still-fresh contact wins.
            float now = Time.time;
            foreach (KeyValuePair<EnemyAI, float> pair in RecentEnemyContacts)
            {
                if (now >= pair.Value || pair.Key == null)
                    continue;

                if (IsEnemyMarked(pair.Key))
                    return PingUpgrade.ENEMY_DAMAGE_REDUCTION;
            }

            return 0f;
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

        /// <summary>Returns true only when a mark was actually produced (F-FOREMAN-A-10).</summary>
        private static bool TryHandlePing(PlayerControllerB player)
        {
            if (!PingUpgrade.IsUnlocked())
                return false;

            if (TryFindPingTarget(player, out PingTarget target))
            {
                ApplyMark(target, PingUpgrade.GetTier(), fromNetwork: false);
                BroadcastMark(target, PingUpgrade.GetTier());
                return true;
            }

            // With the Location fallback in place this is only reachable when there is no camera
            // or nothing at all in front of the player; say so instead of failing silently.
            HUDManager.Instance?.DisplayTip("PING", "Nothing to mark.", isWarning: true);
            Plugin.Log?.LogDebug("ForemanPingPatch: no valid ping target and no location fallback.");
            return false;
        }

        private static bool TryHandleInspire(PlayerControllerB player)
        {
            if (!InspireUpgrade.IsUnlocked())
                return false;

            DeadBodyInfo body = FindLookedAtDeadBody(player);
            if (body == null || body.playerScript == null)
                return false;

            // F-FOREMAN-A-10: on cooldown the press used to be swallowed, so a player standing
            // near any corpse could not ping at all. Fall through to the normal ping instead,
            // with a throttled tip explaining why no revive was attempted.
            if (Time.time < _nextInspireTime)
            {
                if (Time.time >= _nextInspireTipTime)
                {
                    _nextInspireTipTime = Time.time + 5f;
                    int remainingSeconds = Mathf.CeilToInt(_nextInspireTime - Time.time);
                    HUDManager.Instance?.DisplayTip(
                        "INSPIRE",
                        $"Ready in {remainingSeconds}s.",
                        isWarning: true);
                }

                return false;
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
                // F-FOREMAN-A-3: MarkKind.Location was declared, scored and coloured but never
                // constructed, so pinging a wall, a floor or any unrecognised object was a silent
                // no-op. Fall back to a world marker on whatever the ray actually hit.
                if (TryBuildLocationTarget(ray, rayHits, out target))
                {
                    Plugin.Log?.LogDebug(
                        $"ForemanPingPatch: no object target; falling back to location marker at {target.Position}.");
                    return true;
                }

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

        /// <summary>
        /// F-FOREMAN-A-3: the Location half of "Mark a location, or outline ...". Uses the first
        /// solid thing the aim ray hit; the mark message already carries a position and
        /// ResolveNetworkTarget already tolerates a null root, so this replicates like any other
        /// mark and every peer spawns its own marker object.
        /// </summary>
        private static bool TryBuildLocationTarget(Ray ray, RaycastHit[] rayHits, out PingTarget target)
        {
            target = default;

            // rayHits was already distance-sorted by AddPingCandidates.
            for (int i = 0; i < rayHits.Length; i++)
            {
                Collider collider = rayHits[i].collider;
                if (collider == null || collider.isTrigger)
                    continue;

                target = BuildTarget(MarkKind.Location, null, 0UL, -1, rayHits[i].point, "location");
                return true;
            }

            // Nothing solid within reach: drop the marker at the far end of the aim ray so the
            // press still communicates a direction rather than doing nothing.
            RaycastHit blocking;
            Vector3 point = Physics.Raycast(
                ray,
                out blocking,
                PingUpgrade.TARGET_RANGE,
                SquadSightPatch.ScannerLineOfSightMask,
                QueryTriggerInteraction.Ignore)
                ? blocking.point
                : ray.origin + ray.direction * PingUpgrade.TARGET_RANGE;

            target = BuildTarget(MarkKind.Location, null, 0UL, -1, point, "location");
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

                // F-FOREMAN-A-9: both casts collect every hit and TryResolveTarget simply skips
                // geometry, so a wall never blocked anything - Ping doubled as an 80m through-wall
                // radar and Ping L2's damage cut could be pre-applied to an unseen enemy. Same mask
                // the vanilla scanner (and SquadSightPatch) uses for line of sight.
                if (!HasLineOfSight(ray.origin, aimPoint, resolved.Root, collider))
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

        /// <summary>
        /// Mirrors <c>SquadSightPatch.HasReliableDirectVisibility</c>: the candidate is visible
        /// when nothing on the line-of-sight mask sits between the camera and it, or when the
        /// only thing hit belongs to the candidate itself.
        /// </summary>
        private static bool HasLineOfSight(Vector3 origin, Vector3 aimPoint, GameObject root, Collider candidate)
        {
            RaycastHit blocking;
            if (!Physics.Linecast(
                    origin,
                    aimPoint,
                    out blocking,
                    SquadSightPatch.ScannerLineOfSightMask,
                    QueryTriggerInteraction.Ignore))
            {
                return true;
            }

            if (blocking.collider == null || blocking.collider == candidate)
                return true;

            return root != null && blocking.collider.transform.IsChildOf(root.transform);
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

            // F-FOREMAN-A-3: turrets, mines and spike traps are neither EnemyAI nor
            // GrabbableObject, so no branch could ever reach them - MarkKind.Trap was dead.
            if (TryResolveTrap(collider, out target))
                return true;

            // F-FOREMAN-A-3: levers, doors and terminals likewise. Checked last so an entrance
            // (which carries its own InteractTrigger) still resolves as an entrance.
            InteractTrigger interact = collider.GetComponentInParent<InteractTrigger>();
            if (interact != null)
            {
                GameObject root = ResolveRenderableRoot(interact.gameObject, interact.gameObject);
                target = BuildTarget(
                    MarkKind.Interactable,
                    root,
                    interact.NetworkObjectId,
                    -1,
                    ResolveRendererCenter(root, interact.transform.position),
                    string.IsNullOrWhiteSpace(interact.hoverTip) ? "interactable" : interact.hoverTip);
                return true;
            }

            return false;
        }

        private static bool TryResolveTrap(Collider collider, out PingTarget target)
        {
            target = default;

            Turret turret = collider.GetComponentInParent<Turret>();
            if (turret != null)
            {
                GameObject root = ResolveRenderableRoot(turret.gameObject, turret.gameObject);
                target = BuildTarget(MarkKind.Trap, root, turret.NetworkObjectId, -1, ResolveRendererCenter(root, turret.transform.position), "turret");
                return true;
            }

            Landmine mine = collider.GetComponentInParent<Landmine>();
            if (mine != null && !mine.hasExploded)
            {
                GameObject root = ResolveRenderableRoot(mine.gameObject, mine.gameObject);
                target = BuildTarget(MarkKind.Trap, root, mine.NetworkObjectId, -1, ResolveRendererCenter(root, mine.transform.position), "landmine");
                return true;
            }

            SpikeRoofTrap spikes = collider.GetComponentInParent<SpikeRoofTrap>();
            if (spikes != null)
            {
                GameObject root = ResolveRenderableRoot(spikes.gameObject, spikes.gameObject);
                target = BuildTarget(MarkKind.Trap, root, spikes.NetworkObjectId, -1, ResolveRendererCenter(root, spikes.transform.position), "spike trap");
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
                // WP12: one line per mark at Info spammed the log at the 3 s ping cadence for
                // every crew member; marks are routine, so this is diagnostic only.
                Plugin.Log?.LogDebug(
                    $"ForemanPingPatch: mark {(fromNetwork ? "recv" : "local")} kind={target.Kind} label={target.Label} root={target.Root.name} shown={shown} net={target.NetworkObjectId} player={target.PlayerId}");
                return;
            }

            // F-FOREMAN-A-3: an outline needs renderers, so a Location mark (and any object mark
            // whose root did not survive replication) gets a spawned world marker instead.
            LocationMarker.Spawn(target.Position, OutlineChannelFor(target.Kind), PingUpgrade.MARK_DURATION);
            Plugin.Log?.LogDebug(
                $"ForemanPingPatch: mark {(fromNetwork ? "recv" : "local")} kind={target.Kind} label={target.Label} had no root; spawned world marker at {target.Position}.");
        }

        // WP12 dead-code sweep: MarkTeammateFromCommandNet(PlayerControllerB) is deleted. It was
        // the pre-F-FOREMAN-B-3 mark-and-rebroadcast entry point; CommandNetPatch now calls only
        // MarkTeammateFromCommandNetLocal, which CommandNetChecks asserts.

        /// <summary>
        /// F-FOREMAN-B-3: the receive-only half of the Command Net damage mark. Command
        /// Net's damage notice is already fanned out to every client and gated on the receiver
        /// owning level 2, so each eligible player paints their own outline; re-broadcasting a mark
        /// from every receiver amplified O(N^2) and, because the mark receive path has no ownership
        /// check, painted the outline on players who own neither upgrade.
        /// F-FOREMAN-B-12: <paramref name="outlineSeconds"/> lets Command Net source its advertised
        /// 5 s from its own constant instead of inheriting whatever Ping's MARK_DURATION is tuned
        /// to. Pass 0 to keep the Ping duration.
        /// </summary>
        internal static void MarkTeammateFromCommandNetLocal(PlayerControllerB damaged, float outlineSeconds = 0f)
        {
            if (damaged == null)
                return;

            PingTarget target = BuildTarget(
                MarkKind.Player,
                damaged.gameObject,
                ResolveNetworkObjectId(damaged.gameObject),
                (int)damaged.playerClientId,
                ResolvePlayerCenter(damaged),
                "damaged teammate");

            ApplyMark(target, pingTier: 2, fromNetwork: true);

            if (outlineSeconds > 0f && target.Root != null)
            {
                OutlineEffectBridge.Show(
                    target.Root,
                    OutlineChannelFor(MarkKind.Player),
                    outlineSeconds,
                    UpgradeOutlineSource.Ping);
            }
        }

        // PurchaseMenu owns the one-shot activation tooltip.  These are intentionally small
        // read-only hooks so it uses the live binding without recreating the old persistent HUD.
        internal static bool IsPingUnlockedForTooltip() => PingUpgrade.IsUnlocked();

        internal static string GetPingBindingLabelForTooltip()
        {
            return Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.ForemanPing, "Q");
        }

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
            SetLocalPointBlendTarget(1f);
            Animator animator = player.playerBodyAnimator;
            animator.SetInteger("emoteNumber", POINT_EMOTE_ID);
            ApplyPointingLayer(player, animator, EvaluateLocalPointWeight());
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
                SetLocalPointBlendTarget(1f);
            }

            if (!_pointingLayerActive && _localPointWeight <= 0f)
                return;

            if (!shouldPoint)
                SetLocalPointBlendTarget(0f);

            float weight = EvaluateLocalPointWeight();
            if (advanceWeight)
                _localPointWeight = weight;
            ApplyPointingLayer(player, animator, weight);
            if (advanceWeight && !shouldPoint && weight <= 0.001f)
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
            SetRemotePointBlendTarget(ref state, 1f);
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
            {
                animator.SetInteger("emoteNumber", POINT_EMOTE_ID);
                SetRemotePointBlendTarget(ref state, 1f);
            }
            else
            {
                SetRemotePointBlendTarget(ref state, 0f);
            }

            float weight = EvaluateRemotePointWeight(state);
            if (advanceWeight)
                state.Weight = weight;
            ApplyPointingLayer(player, animator, weight);
            if (advanceWeight && !shouldPoint && weight <= 0.001f)
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

        private static void SetLocalPointBlendTarget(float target)
        {
            target = Mathf.Clamp01(target);
            if (Mathf.Approximately(_localPointBlendTarget, target))
                return;
            _localPointWeight = EvaluateLocalPointWeight();
            _localPointBlendFrom = _localPointWeight;
            _localPointBlendTarget = target;
            _localPointBlendStartedAt = Time.time;
        }

        private static float EvaluateLocalPointWeight()
        {
            float duration = _localPointBlendTarget >= _localPointBlendFrom
                ? POINT_RAISE_SECONDS
                : POINT_RETURN_SECONDS;
            return ForemanPingGestureMath.Evaluate(
                _localPointBlendFrom,
                _localPointBlendTarget,
                Time.time - _localPointBlendStartedAt,
                duration);
        }

        private static void SetRemotePointBlendTarget(ref RemotePointState state, float target)
        {
            target = Mathf.Clamp01(target);
            if (Mathf.Approximately(state.BlendTarget, target))
                return;
            state.Weight = EvaluateRemotePointWeight(state);
            state.BlendFrom = state.Weight;
            state.BlendTarget = target;
            state.BlendStartedAt = Time.time;
        }

        private static float EvaluateRemotePointWeight(RemotePointState state)
        {
            float duration = state.BlendTarget >= state.BlendFrom
                ? POINT_RAISE_SECONDS
                : POINT_RETURN_SECONDS;
            return ForemanPingGestureMath.Evaluate(
                state.BlendFrom,
                state.BlendTarget,
                Time.time - state.BlendStartedAt,
                duration);
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
            // This stands in for vanilla's StartOfRound.ReviveDeadPlayers, whose health literal an
            // external provider may rewrite - LGU's Stimpack restores its flat health exactly
            // there. Reviving to the bare literal would delete that health until the next round.
            // The composition deliberately starts from vanilla's 100, not our own raised maximum:
            // a Foreman revive has never granted Resilience's extra health and still does not.
            player.health = LguEffectCompatibility.TryComposeMaxHealth(100, out int revivedHealth)
                ? revivedHealth
                : 100;
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

            // F-FOREMAN-A-4: the rest of what vanilla's StartOfRound.ReviveDeadPlayers resets.
            // Without these a player who died sinking came back permanently crawling (and still
            // sinking), a two-handed carry stayed flagged, and blood stayed smeared on the visor.
            player.ResetPlayerBloodObjects(true);
            player.enemyWaitingForBodyRagdoll = null;
            player.overridePoisonValue = false;
            player.clampLooking = false;
            player.inVehicleAnimation = false;
            player.ResetZAndXRotation();
            player.activatingItem = false;
            player.twoHanded = false;
            player.inShockingMinigame = false;
            player.freeRotationInInteractAnimation = false;
            player.disableSyncInAnimation = false;
            player.inAnimationWithEnemy = null;
            player.holdingWalkieTalkie = false;
            player.speakingToWalkieTalkie = false;
            player.isSinking = false;
            player.isUnderwater = false;
            player.sinkingValue = 0f;
            player.parentedToElevatorLastFrame = false;
            if (player.statusEffectAudio != null)
                player.statusEffectAudio.Stop();
            if (player.nightVisionRadar != null)
                player.nightVisionRadar.enabled = false;
            if (player.helmetLight != null)
                player.helmetLight.enabled = false;
            player.DisableJetpackControlsLocally();
            StartOfRound.Instance.SetPlayerObjectExtrapolate(enable: false);

            // F-FOREMAN-A-4: these were hardcoded to false regardless of where the body actually
            // lay, so reviving someone inside the ship desynced every isInHangarShipRoom check.
            ApplyRevivePositionFlags(player, revivePosition);

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
                // F-FOREMAN-A-4: vanilla only resets these on the owner - they are the movement
                // state that made a quicksand death revive into a permanent crawl.
                player.hinderedMultiplier = 1f;
                player.isMovementHindered = 0;
                player.sourcesCausingSinking = 0;

                HUDManager hud = HUDManager.Instance;
                if (hud != null)
                {
                    hud.RemoveSpectateUI();
                    hud.gameOverAnimator.SetTrigger("revive");
                    hud.UpdateHealthUI(player.health, hurtPlayer: false);
                    hud.SetCracksOnVisor(100f);
                    if (hud.gasHelmetAnimator != null)
                        hud.gasHelmetAnimator.SetBool("gasEmitting", false);
                }
            }
            else if (GameNetworkManager.Instance?.localPlayerController != null
                     && GameNetworkManager.Instance.localPlayerController.isPlayerDead)
            {
                HUDManager.Instance?.UpdateBoxesSpectateUI();
            }
        }

        /// <summary>
        /// F-FOREMAN-A-4: derive the location flags from where the body actually lies. Vanilla's
        /// revive always lands in the ship and so can hardcode them; an Inspire revive happens at
        /// the corpse, which may be in the ship, outdoors, or in the interior.
        /// </summary>
        private static void ApplyRevivePositionFlags(PlayerControllerB player, Vector3 revivePosition)
        {
            StartOfRound round = StartOfRound.Instance;
            bool inElevator = round != null
                && round.shipBounds != null
                && round.shipBounds.bounds.Contains(revivePosition);
            bool inShipRoom = inElevator
                && round.shipInnerRoomBounds != null
                && round.shipInnerRoomBounds.bounds.Contains(revivePosition);

            player.isInElevator = inElevator;
            player.isInHangarShipRoom = inShipRoom;
            player.isInsideFactory = !inElevator && FacilityPositionQuery.IsInsideFactory(revivePosition);
        }

        /// <summary>
        /// F-FOREMAN-A-3: a short-lived world marker for marks that cannot be outlined, built
        /// from primitives with an unlit material tinted to the same channel colours the outline
        /// effect uses. Self-destructs; no netcode (each peer spawns its own from the replicated
        /// mark position).
        /// </summary>
        private sealed class LocationMarker : MonoBehaviour
        {
            // Mirrors OutlineEffectBridge's private line colours; that bridge owns the outline
            // channels and does not expose them, so the constants are duplicated here.
            private static readonly Color DangerColor = new Color(1f, 0.18f, 0.12f, 1f);
            private static readonly Color FriendlyColor = new Color(0.2f, 1f, 0.45f, 1f);
            private static readonly Color UtilityColor = new Color(0.25f, 0.85f, 1f, 1f);

            private static readonly List<LocationMarker> Live = new List<LocationMarker>();

            private float _expiresAt;
            private Vector3 _basePosition;
            // Review pass: owned so OnDestroy can reclaim it - see the note there.
            private Material _material;

            internal static void Spawn(Vector3 position, UpgradeOutlineChannel channel, float duration)
            {
                if (duration <= 0f || position == Vector3.zero)
                    return;

                try
                {
                    GameObject root = new GameObject("Y4NGZ_PingLocationMarker");
                    root.transform.position = position + Vector3.up * 0.25f;

                    Material material = CreateMaterial(ColorFor(channel));
                    AddPart(root.transform, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.35f, 0.35f, 0.35f), material);
                    AddPart(root.transform, PrimitiveType.Cylinder, new Vector3(0f, 0.75f, 0f), new Vector3(0.045f, 0.75f, 0.045f), material);

                    LocationMarker marker = root.AddComponent<LocationMarker>();
                    marker._material = material;
                    marker._expiresAt = Time.time + duration;
                    marker._basePosition = root.transform.position;
                    Live.Add(marker);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"ForemanPingPatch: location marker spawn failed: {ex.Message}");
                }
            }

            internal static void DestroyAll()
            {
                for (int i = 0; i < Live.Count; i++)
                {
                    LocationMarker marker = Live[i];
                    if (marker != null)
                        UnityEngine.Object.Destroy(marker.gameObject);
                }

                Live.Clear();
            }

            private static Color ColorFor(UpgradeOutlineChannel channel)
            {
                switch (channel)
                {
                    case UpgradeOutlineChannel.Danger: return DangerColor;
                    case UpgradeOutlineChannel.Friendly: return FriendlyColor;
                    default: return UtilityColor;
                }
            }

            private static void AddPart(Transform parent, PrimitiveType shape, Vector3 localPosition, Vector3 localScale, Material material)
            {
                GameObject part = GameObject.CreatePrimitive(shape);
                Collider collider = part.GetComponent<Collider>();
                if (collider != null)
                    UnityEngine.Object.Destroy(collider);

                part.transform.SetParent(parent, worldPositionStays: false);
                part.transform.localPosition = localPosition;
                part.transform.localScale = localScale;

                Renderer renderer = part.GetComponent<Renderer>();
                if (renderer == null)
                    return;

                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            private static Material CreateMaterial(Color color)
            {
                Shader shader = Shader.Find("HDRP/Unlit")
                    ?? Shader.Find("Unlit/Color")
                    ?? Shader.Find("Sprites/Default")
                    ?? Shader.Find("Standard");

                Material material = shader != null
                    ? new Material(shader)
                    : new Material(Shader.Find("Standard"));
                material.name = "M_Y4NGZPingLocationMarker";
                material.hideFlags = HideFlags.HideAndDontSave;
                material.color = color;
                // HDRP/Unlit reads _UnlitColor; the built-in fallbacks read _Color / _BaseColor.
                if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color * 4f);
                return material;
            }

            private void LateUpdate()
            {
                if (Time.time >= _expiresAt)
                {
                    Destroy(gameObject);
                    return;
                }

                transform.position = _basePosition
                    + Vector3.up * (Mathf.Sin(Time.time * LOCATION_MARKER_BOB_SPEED) * LOCATION_MARKER_BOB_HEIGHT);
            }

            private void OnDestroy()
            {
                Live.Remove(this);

                // Review pass: CreateMaterial runs once per marker and the result is attached with
                // renderer.sharedMaterial, which Destroy(gameObject) does not touch, and stamped
                // HideAndDontSave - which includes DontUnloadUnusedAsset, so the
                // Resources.UnloadUnusedAssets between rounds cannot reclaim it either. Every
                // location ping (the default outcome whenever the aim ray hits plain geometry, on
                // every peer, for every crew member) leaked one Material for the rest of the
                // process.
                if (_material != null)
                {
                    Destroy(_material);
                    _material = null;
                }
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

        /// <summary>
        /// F-FOREMAN-A-13: host re-broadcast of a client's mark, skipping the originator (whose
        /// own ApplyMark already ran) and the host itself. Mirrors
        /// <c>ForemanSupportPatch.RelaySupportState</c>; SendNamedMessageToAll would echo the
        /// mark back to the sender, refreshing its 5s timer and re-running the outline work.
        /// </summary>
        private static void RelayMark(ulong excludeClientId, PingTarget target, int pingTier)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || network.CustomMessagingManager == null) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                SendMarkMessage(target, pingTier, clientId);
            }
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
                    RelayMark(senderClientId, target, pingTier);
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
                // F-FOREMAN-A-3: the new kinds need the same position fallback the others have,
                // or a client that has not spawned the NetworkObject yet drops the mark.
                case MarkKind.Trap:
                    FindClosestComponent<Turret>(position, ref best, ref bestSqr);
                    FindClosestComponent<Landmine>(position, ref best, ref bestSqr);
                    FindClosestComponent<SpikeRoofTrap>(position, ref best, ref bestSqr);
                    break;
                case MarkKind.Interactable:
                    FindClosestComponent<InteractTrigger>(position, ref best, ref bestSqr);
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

            // F-FOREMAN-A-2: the 75s cooldown was purely client-side, so a modified client could
            // re-roll the 50% every frame until it succeeded. The server owns the retry stamp now.
            if (InspireRetryAtByClient.TryGetValue(requesterClientId, out float retryAt) && Time.time < retryAt)
            {
                SendReviveResult(requesterClientId, InspireResult.OnCooldown);
                return;
            }

            PlayerControllerB requester = FindPlayerByClientId(requesterClientId);
            if (requester == null || requester.isPlayerDead || !requester.isPlayerControlled)
            {
                SendReviveResult(requesterClientId, InspireResult.InvalidTarget);
                return;
            }

            // F-FOREMAN-A-20: everything else on this path was already server-authoritative;
            // ownership was the one gap. The tier comes from the broadcast UpgradeTierSync table,
            // keyed by the connection's actualClientId (never a payload-supplied id).
            if (UpgradeTierSync.GetTier(requesterClientId, InspireUpgrade.UPGRADE_ID) < 1)
            {
                Plugin.Log?.LogWarning(
                    $"ForemanPingPatch: revive request from client {requesterClientId} without the Inspire upgrade; ignoring.");
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

            // F-FOREMAN-A-2: the roll is what the 75s cooldown is FOR - a failure must cost the
            // attempt. The once-per-round use is still only consumed on success.
            if (UnityEngine.Random.value > InspireUpgrade.REVIVE_CHANCE)
            {
                InspireRetryAtByClient[requesterClientId] = Time.time + InspireUpgrade.COOLDOWN_SECONDS;
                SendReviveResult(requesterClientId, InspireResult.NoResponse);
                return;
            }

            InspireRetryAtByClient[requesterClientId] = Time.time + InspireUpgrade.COOLDOWN_SECONDS;
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
                    // F-FOREMAN-A-2: a failed roll is exactly what the 75s cooldown exists for.
                    // Without this the 50% chance was meaningless - the player just pressed again
                    // on the next frame until it landed. The once-per-round use stays unconsumed.
                    _nextInspireTime = Time.time + InspireUpgrade.COOLDOWN_SECONDS;
                    HUDManager.Instance?.DisplayTip("INSPIRE", "No response...", isWarning: true);
                    break;
                case InspireResult.OnCooldown:
                    // The server refused on its own retry stamp; mirror it locally so the client
                    // stops asking (this is the path a desynced or modified client lands on).
                    _nextInspireTime = Time.time + InspireUpgrade.COOLDOWN_SECONDS;
                    HUDManager.Instance?.DisplayTip("INSPIRE", "Still recovering.", isWarning: true);
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
