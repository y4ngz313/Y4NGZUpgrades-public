using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZInteractions.InteractionAnimationApi;
using Y4NGZUpgrades.Upgrades;

#pragma warning disable Harmony003

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Y4NGZ-owned fists. Owners predict presentation, while the server owns hand selection,
    /// timing, hit resolution, damage, stun, noise, and the phase stream seen by every peer.
    /// </summary>
    [HarmonyPatch]
    internal static class NativeFistsPatch
    {
        private const string StateRequestMessage = "Y4NGZ.NativeFists.StateRequest.v1";
        private const string SprintExitRequestMessage = "Y4NGZ.NativeFists.SprintExitRequest.v1";
        private const string PunchRequestMessage = "Y4NGZ.NativeFists.PunchRequest.v1";
        private const string EventMessage = "Y4NGZ.NativeFists.Event.v1";
        private const string ImpactMessage = "Y4NGZ.NativeFists.Impact.v1";
        private const string SnapshotMessage = "Y4NGZ.NativeFists.Snapshot.v1";

        private const string PackId = "y4ngz.upgrades.native-fists";
        private const string FirstPersonInteractionId = "y4ngz.native-fists.firstperson";
        private const string ThirdPersonInteractionId = "y4ngz.native-fists.thirdperson";
        private const string FirstPersonManifestFile = "y4ngz-upgrades-fists-firstperson.manifest.json";
        private const string ThirdPersonManifestFile = "y4ngz-upgrades-fists-thirdperson.manifest.json";
        private const string EnterTrigger = "Y4NGZ_Fists_Enter";
        private const string PunchLeftTrigger = "PunchLeft";
        private const string PunchRightTrigger = "PunchRight";
        private const string SyncIdleTrigger = "SyncIdle";
        private const string NearWallBool = "FistsNearWall";
        private const string UpperBodyLayer = "UpperBodyEmotes";
        private const float NearWallEnterWeight = 0.08f;
        private const float NearWallExitWeight = 0.02f;

        private const float RangeMeters = 1.2f;
        private const float SphereRadiusMeters = 0.8f;
        private const int CollisionMask = 11012424;
        private const int PlayerDamage = 10;
        private const float EnemyStunSeconds = 1f;
        private const float MaxAimOriginErrorMeters = 0.75f;
        private const float MinAimForwardDot = 0.25f;
        private const int FistNoiseId = 9011;

        private static readonly Dictionary<ulong, ServerSession> ServerSessions = new Dictionary<ulong, ServerSession>();
        private static readonly Dictionary<ulong, PresentationSession> Presentations = new Dictionary<ulong, PresentationSession>();
        private static readonly HashSet<ulong> PendingSnapshotPlayers = new HashSet<ulong>();
        private static readonly NativeFistFractionalDamage<EnemyAI> FractionalEnemyDamage = new NativeFistFractionalDamage<EnemyAI>();
        private static readonly System.Reflection.FieldInfo NutcrackerIsInspectingField =
            AccessTools.Field(typeof(NutcrackerEnemyAI), "isInspecting");
        private static readonly System.Reflection.MethodInfo BushWolfHitTongueLocalClientMethod =
            AccessTools.Method(typeof(BushWolfEnemy), "HitTongueLocalClient");

        private static NetworkManager registeredNetworkManager;
        private static bool packRegistered;
        private static bool missingManifestLogged;
        private static float nextPackRegistrationAttemptAt;
        private static uint nextLocalSequence = 1;
        private static NativeFistPhase localPhase = NativeFistPhase.Inactive;
        private static NativeFistHand localNextHand = NativeFistHand.Left;
        private static uint localActivePunchSequence;
        private static int localGeneration;
        private static float localNextPunchAllowedAt;
        private static AudioClip vanillaSwingClip;
        private static AudioClip[] vanillaHitClips;
        private static bool vanillaAudioResolved;

        private sealed class ServerSession
        {
            internal readonly NativeFistSessionModel Model = new NativeFistSessionModel();
            internal Vector3 QueuedOrigin;
            internal Vector3 QueuedForward;
            internal uint ActivePunchSequence;
        }

        private sealed class PresentationSession
        {
            internal InteractionAnimationHandle Handle = InteractionAnimationHandle.Empty;
            internal NativeFistPhase Phase = NativeFistPhase.Inactive;
            internal bool HasPunchSequence;
            internal uint LastPunchSequence;
            internal bool HasNearWall;
            internal bool NearWall;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void ConnectClientToPlayerObjectPostfix()
        {
            RegisterNetworkHandlers();
            EnsurePackRegistered();
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            ResetRuntime(false);
            RegisterNetworkHandlers();
            EnsurePackRegistered();
        }

        [HarmonyPatch(typeof(StartOfRound), "ResetShip")]
        [HarmonyPostfix]
        private static void ResetShipPostfix() => ResetRuntime(false);

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void DisconnectPostfix() => ResetRuntime(true);

        /// <summary>Use vanilla's LMB activation path; RMB/scanning is never subscribed or patched.</summary>
        [HarmonyPatch(typeof(PlayerControllerB), "ActivateItem_performed")]
        [HarmonyPrefix]
        private static bool ActivateItemPerformedPrefix(PlayerControllerB __instance, InputAction.CallbackContext context)
        {
            if (__instance == null || __instance != GameNetworkManager.Instance?.localPlayerController)
                return true;
            if (!context.performed || !NativeFistsUpgrade.IsUnlocked())
                return true;
            if (!CanUseFists(__instance, true))
                return true;

            try
            {
                RegisterNetworkHandlers();
                EnsurePackRegistered();
                if (localPhase == NativeFistPhase.Inactive)
                {
                    uint stateSequence = NextLocalSequence();
                    uint punchSequence = NextLocalSequence();
                    localPhase = NativeFistPhase.Equipping;
                    localNextHand = NativeFistHand.Right;
                    localGeneration++;
                    int generation = localGeneration;
                    EnsurePresentation(__instance, false);
                    FirePresentationTrigger(__instance.actualClientId, EnterTrigger);
                    SendStateRequest(stateSequence, true);
                    SendPunchRequest(punchSequence, __instance);
                    Host()?.StartCoroutine(PredictInitialPunch(punchSequence, generation));
                    return false;
                }

                if (localPhase != NativeFistPhase.Idle || Time.realtimeSinceStartup + 0.0001f < localNextPunchAllowedAt)
                    return false;

                uint sequence = NextLocalSequence();
                NativeFistHand hand = localNextHand;
                localNextHand = hand == NativeFistHand.Left ? NativeFistHand.Right : NativeFistHand.Left;
                PresentPunch(__instance.actualClientId, sequence, hand);
                SendPunchRequest(sequence, __instance);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("NativeFists: local activation failed: " + e);
                InterruptLocal(true);
                return false;
            }
        }

        /// <summary>Only the actual punch window suppresses pickup; every other fist phase yields.</summary>
        [HarmonyPatch(typeof(PlayerControllerB), "BeginGrabObject")]
        [HarmonyPrefix]
        private static bool BeginGrabObjectPrefix(PlayerControllerB __instance)
        {
            if (__instance == null || __instance != GameNetworkManager.Instance?.localPlayerController)
                return true;
            if (localPhase == NativeFistPhase.Punching)
                return false;
            if (localPhase != NativeFistPhase.Inactive)
                InterruptLocal(true);
            return true;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PlayerLateUpdatePostfix(PlayerControllerB __instance)
        {
            if (__instance == null)
                return;
            RetryPendingSnapshot(__instance);
            if (__instance == GameNetworkManager.Instance?.localPlayerController &&
                localPhase != NativeFistPhase.Inactive)
            {
                if (__instance.isSprinting)
                    BeginLocalSprintExit(__instance);
                else if (!CanUseFists(__instance, true))
                    InterruptLocal(true);
            }
            UpdatePresentationParameters(__instance);

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return;
            ulong clientId = __instance.actualClientId;
            if (!ServerSessions.TryGetValue(clientId, out ServerSession session) || session.Model.Phase == NativeFistPhase.Inactive)
                return;
            if (__instance.isSprinting)
            {
                BeginServerUnequip(clientId, session);
                return;
            }
            if (!CanUseFists(__instance, true) || UpgradeTierSync.GetTier(clientId, NativeFistsUpgrade.UPGRADE_ID) <= 0)
            {
                InterruptServer(clientId, session);
                return;
            }
            if (session.Model.ShouldBeginUnequip(NetworkTime()))
                BeginServerUnequip(clientId, session);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPostfix]
        private static void KillPlayerPostfix(PlayerControllerB __instance)
        {
            if (__instance == null)
                return;
            ulong clientId = __instance.actualClientId;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && ServerSessions.TryGetValue(clientId, out ServerSession session))
                InterruptServer(clientId, session);
            StopPresentation(clientId, true);
            if (__instance == GameNetworkManager.Instance?.localPlayerController)
                ResetLocalState();
        }

        private static IEnumerator PredictInitialPunch(uint sequence, int generation)
        {
            yield return new WaitForSeconds((float)NativeFistSessionModel.InitialPunchLeadSeconds);
            if (generation != localGeneration || localPhase == NativeFistPhase.Inactive || localPhase == NativeFistPhase.Unequipping)
                yield break;
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local != null)
                PresentPunch(local.actualClientId, sequence, NativeFistHand.Left);
        }

        private static IEnumerator PredictPunchFinished(uint sequence, int generation)
        {
            yield return new WaitForSeconds((float)NativeFistSessionModel.PunchCadenceSeconds);
            if (generation == localGeneration && localPhase == NativeFistPhase.Punching && localActivePunchSequence == sequence)
                localPhase = NativeFistPhase.Idle;
        }

        private static void RegisterNetworkHandlers()
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsListening || registeredNetworkManager == network)
                return;
            UnregisterNetworkHandlers();
            try
            {
                if (network.IsServer)
                {
                    messaging.RegisterNamedMessageHandler(StateRequestMessage, OnStateRequest);
                    messaging.RegisterNamedMessageHandler(SprintExitRequestMessage, OnSprintExitRequest);
                    messaging.RegisterNamedMessageHandler(PunchRequestMessage, OnPunchRequest);
                }
                messaging.RegisterNamedMessageHandler(EventMessage, OnEvent);
                messaging.RegisterNamedMessageHandler(ImpactMessage, OnImpact);
                messaging.RegisterNamedMessageHandler(SnapshotMessage, OnSnapshot);
                network.OnClientConnectedCallback += OnClientConnected;
                network.OnClientDisconnectCallback += OnClientDisconnected;
                registeredNetworkManager = network;
                Plugin.Log?.LogInfo("NativeFists: reliable-sequenced handlers registered.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("NativeFists: handler registration failed: " + e);
                UnregisterNetworkHandlers();
            }
        }

        private static void UnregisterNetworkHandlers()
        {
            NetworkManager network = registeredNetworkManager;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (messaging != null)
            {
                try
                {
                    if (network.IsServer)
                    {
                        messaging.UnregisterNamedMessageHandler(StateRequestMessage);
                        messaging.UnregisterNamedMessageHandler(SprintExitRequestMessage);
                        messaging.UnregisterNamedMessageHandler(PunchRequestMessage);
                    }
                    messaging.UnregisterNamedMessageHandler(EventMessage);
                    messaging.UnregisterNamedMessageHandler(ImpactMessage);
                    messaging.UnregisterNamedMessageHandler(SnapshotMessage);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("NativeFists: handler unregister failed: " + e.Message);
                }
            }
            if (network != null)
            {
                network.OnClientConnectedCallback -= OnClientConnected;
                network.OnClientDisconnectCallback -= OnClientDisconnected;
            }
            registeredNetworkManager = null;
        }

        private static void OnClientConnected(ulong clientId)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                Host()?.StartCoroutine(SendSnapshotAfterJoin(clientId));
        }

        private static IEnumerator SendSnapshotAfterJoin(ulong clientId)
        {
            // The connection callback precedes ConnectClientToPlayerObject on a late joiner.
            // Delay the snapshot until its named-message handlers and player object exist.
            float deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline && ResolvePlayer(clientId) == null)
                yield return null;
            yield return new WaitForSeconds(0.25f);
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer &&
                NetworkManager.Singleton.ConnectedClients.ContainsKey(clientId))
                SendSnapshot(clientId);
        }

        private static void OnClientDisconnected(ulong clientId)
        {
            if (ServerSessions.TryGetValue(clientId, out ServerSession session))
                session.Model.Interrupt();
            ServerSessions.Remove(clientId);
            PendingSnapshotPlayers.Remove(clientId);
            StopPresentation(clientId, true);
        }

        private static void SendStateRequest(uint sequence, bool desiredActive)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null)
                return;
            using (var writer = new FastBufferWriter(5, Allocator.Temp))
            {
                writer.WriteValueSafe(sequence);
                writer.WriteValueSafe(desiredActive);
                network.CustomMessagingManager.SendNamedMessage(StateRequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void SendSprintExitRequest(uint sequence)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null)
                return;
            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            {
                writer.WriteValueSafe(sequence);
                network.CustomMessagingManager.SendNamedMessage(
                    SprintExitRequestMessage,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
        }

        private static void SendPunchRequest(uint sequence, PlayerControllerB player)
        {
            NetworkManager network = NetworkManager.Singleton;
            Transform camera = player?.gameplayCamera != null ? player.gameplayCamera.transform : null;
            if (network?.CustomMessagingManager == null || camera == null)
                return;
            Vector3 origin = camera.position;
            Vector3 forward = camera.forward.normalized;
            using (var writer = new FastBufferWriter(32, Allocator.Temp))
            {
                writer.WriteValueSafe(sequence);
                writer.WriteValueSafe(origin);
                writer.WriteValueSafe(forward);
                network.CustomMessagingManager.SendNamedMessage(PunchRequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnStateRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;
            try
            {
                reader.ReadValueSafe(out uint sequence);
                reader.ReadValueSafe(out bool desiredActive);
                PlayerControllerB player = ResolvePlayer(senderClientId);
                if (player == null || player.actualClientId != senderClientId)
                    return;
                ServerSession session = GetServerSession(senderClientId);
                if (!desiredActive)
                {
                    if (session.Model.TrySetActive(sequence, false))
                        BroadcastEvent(senderClientId, sequence, NativeFistPhase.Inactive, NativeFistHand.None);
                    return;
                }
                if (UpgradeTierSync.GetTier(senderClientId, NativeFistsUpgrade.UPGRADE_ID) <= 0 ||
                    !CanUseFists(player, true))
                {
                    BroadcastEvent(senderClientId, sequence, NativeFistPhase.Inactive, NativeFistHand.None);
                    return;
                }
                if (!session.Model.TrySetActive(sequence, true))
                    return;
                BroadcastEvent(senderClientId, sequence, NativeFistPhase.Equipping, NativeFistHand.None);
                int generation = session.Model.Generation;
                Host()?.StartCoroutine(FinishServerEquip(senderClientId, session, generation));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: invalid StateRequest: " + e.Message);
            }
        }

        private static void OnSprintExitRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;
            try
            {
                reader.ReadValueSafe(out uint sequence);
                PlayerControllerB player = ResolvePlayer(senderClientId);
                if (player == null || player.actualClientId != senderClientId)
                    return;
                ServerSession session = GetServerSession(senderClientId);
                BeginServerUnequip(senderClientId, session, sequence);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: invalid SprintExitRequest: " + e.Message);
            }
        }

        private static void OnPunchRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;
            try
            {
                reader.ReadValueSafe(out uint sequence);
                reader.ReadValueSafe(out Vector3 origin);
                reader.ReadValueSafe(out Vector3 forward);
                PlayerControllerB player = ResolvePlayer(senderClientId);
                if (player == null || player.actualClientId != senderClientId)
                    return;
                ServerSession session = GetServerSession(senderClientId);
                if (UpgradeTierSync.GetTier(senderClientId, NativeFistsUpgrade.UPGRADE_ID) <= 0 ||
                    !CanUseFists(player, true) || !ValidateAim(player, origin, forward))
                {
                    BroadcastEvent(senderClientId, sequence, session.Model.Phase,
                        session.Model.ActiveHand);
                    return;
                }
                NativeFistPhase before = session.Model.Phase;
                origin = ServerAimOrigin(player);
                forward.Normalize();
                if (!session.Model.TryQueueOrStartPunch(sequence, NetworkTime(), out NativeFistHand hand))
                    return;
                if (before == NativeFistPhase.Equipping)
                {
                    session.QueuedOrigin = origin;
                    session.QueuedForward = forward;
                    return;
                }
                StartServerPunch(senderClientId, session, sequence, hand, origin, forward);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: invalid PunchRequest: " + e.Message);
            }
        }

        private static IEnumerator FinishServerEquip(ulong clientId, ServerSession session, int generation)
        {
            yield return new WaitForSeconds((float)NativeFistSessionModel.InitialPunchLeadSeconds);
            if (session == null || session.Model.Generation != generation || session.Model.Phase != NativeFistPhase.Equipping)
                yield break;
            if (!session.Model.FinishEquip(NetworkTime(), out uint sequence, out NativeFistHand hand))
                yield break;
            if (hand == NativeFistHand.None)
            {
                BroadcastEvent(clientId, sequence, NativeFistPhase.Idle, NativeFistHand.None);
                BeginServerUnequip(clientId, session);
                yield break;
            }
            StartServerPunch(clientId, session, sequence, hand, session.QueuedOrigin, session.QueuedForward);
        }

        private static void StartServerPunch(ulong clientId, ServerSession session, uint sequence, NativeFistHand hand, Vector3 origin, Vector3 forward)
        {
            session.ActivePunchSequence = sequence;
            int generation = session.Model.Generation;
            BroadcastEvent(clientId, sequence, NativeFistPhase.Punching, hand);
            Host()?.StartCoroutine(ResolveServerPunch(clientId, session, sequence, origin, forward, generation));
        }

        private static IEnumerator ResolveServerPunch(ulong clientId, ServerSession session, uint sequence, Vector3 origin, Vector3 forward, int generation)
        {
            yield return new WaitForSeconds((float)NativeFistSessionModel.PunchContactSeconds);
            if (!IsCurrentPunch(session, sequence, generation))
                yield break;
            PlayerControllerB attacker = ResolvePlayer(clientId);
            if (attacker == null)
            {
                InterruptServer(clientId, session);
                yield break;
            }
            if (attacker.isSprinting)
            {
                BeginServerUnequip(clientId, session);
                yield break;
            }
            if (!CanUseFists(attacker, true))
            {
                InterruptServer(clientId, session);
                yield break;
            }
            ResolveAuthoritativeHit(clientId, sequence, attacker, origin, forward);
            yield return new WaitForSeconds((float)(NativeFistSessionModel.PunchCadenceSeconds - NativeFistSessionModel.PunchContactSeconds));
            if (!IsCurrentPunch(session, sequence, generation))
                yield break;
            if (session.Model.FinishPunch())
                BroadcastEvent(clientId, sequence, NativeFistPhase.Idle, NativeFistHand.None);
        }

        private static bool IsCurrentPunch(ServerSession session, uint sequence, int generation)
        {
            return session != null && session.Model.Generation == generation && session.Model.Phase == NativeFistPhase.Punching && session.ActivePunchSequence == sequence;
        }

        private static void BeginServerUnequip(
            ulong clientId,
            ServerSession session,
            uint? requestedSequence = null)
        {
            if (session == null)
                return;
            bool began = requestedSequence.HasValue
                ? session.Model.TryBeginUnequip(requestedSequence.Value)
                : session.Model.BeginUnequip();
            if (!began)
                return;
            int generation = session.Model.Generation;
            uint sequence = requestedSequence ?? session.Model.LastPunchSequence;
            BroadcastEvent(clientId, sequence, NativeFistPhase.Unequipping, NativeFistHand.None);
            Host()?.StartCoroutine(FinishServerUnequip(clientId, session, sequence, generation));
        }

        private static IEnumerator FinishServerUnequip(ulong clientId, ServerSession session, uint sequence, int generation)
        {
            yield return new WaitForSeconds((float)NativeFistSessionModel.UnequipSeconds);
            if (session == null || session.Model.Generation != generation || session.Model.Phase != NativeFistPhase.Unequipping)
                yield break;
            session.Model.FinishUnequip();
            BroadcastEvent(clientId, sequence, NativeFistPhase.Inactive, NativeFistHand.None);
        }

        private static void InterruptServer(ulong clientId, ServerSession session)
        {
            if (session == null || session.Model.Phase == NativeFistPhase.Inactive)
                return;
            uint sequence = session.Model.LastStateSequence;
            session.Model.Interrupt();
            BroadcastEvent(clientId, sequence, NativeFistPhase.Inactive, NativeFistHand.None);
        }

        private static void ResolveAuthoritativeHit(ulong clientId, uint sequence, PlayerControllerB attacker, Vector3 origin, Vector3 forward)
        {
            forward.Normalize();
            RaycastHit[] hits = Physics.SphereCastAll(origin, SphereRadiusMeters, forward, RangeMeters, CollisionMask, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                Collider collider = hit.collider;
                if (collider == null)
                    continue;
                Vector3 point = hit.point == Vector3.zero ? collider.ClosestPoint(origin + forward * Mathf.Max(hit.distance, 0.01f)) : hit.point;
                int layer = collider.gameObject.layer;
                if ((layer == 8 || layer == 11) && !collider.isTrigger)
                {
                    BroadcastImpact(clientId, sequence, NativeFistHitKind.Surface, FistSurfaceClassifier.ResolveSurfaceIndex(collider), ChooseVanillaHitClip(), point);
                    EmitAuthorityNoise(attacker, point);
                    return;
                }

                if (!IsWorldBlocked(origin, point) && TryHitClingingTongue(collider, attacker))
                {
                    BroadcastImpact(clientId, sequence, NativeFistHitKind.ClingingEnemy, FistSurfaceClassifier.UnknownSurface, ChooseVanillaHitClip(), point);
                    EmitAuthorityNoise(attacker, point);
                    return;
                }
                PlayerControllerB targetPlayer = collider.GetComponent<PlayerControllerB>() ?? collider.GetComponentInParent<PlayerControllerB>();
                EnemyAICollisionDetect collision = collider.GetComponent<EnemyAICollisionDetect>() ?? collider.GetComponentInParent<EnemyAICollisionDetect>();
                EnemyAI enemy = collision != null ? collision.mainScript : collider.GetComponent<EnemyAI>() ?? collider.GetComponentInParent<EnemyAI>();
                if (enemy != null)
                {
                    if (IsWorldBlocked(origin, point))
                        continue;
                    if (enemy.isEnemyDead)
                    {
                        FractionalEnemyDamage.Remove(enemy);
                        continue;
                    }
                    if (StartOfRound.Instance.hangarDoorsClosed && enemy.isInsidePlayerShip != attacker.isInHangarShipRoom)
                        continue;
                    bool clinging = IsClingingEnemy(enemy, attacker);
                    int tier = UpgradeTierSync.GetTier(clientId, NativeFistsUpgrade.UPGRADE_ID);
                    bool canDamage = CanDamageEnemy(enemy);
                    int wholeDamage = canDamage
                        ? FractionalEnemyDamage.Add(enemy, NativeFistsUpgrade.GetDamageForTier(tier))
                        : 0;
                    if (wholeDamage > 0)
                    {
                        enemy.HitEnemy(wholeDamage, attacker, true, 1);
                        // The server owns the gameplay mutation; this vanilla ClientRpc mirrors
                        // the accepted hit on every client without asking the attacker's client
                        // to authoritatively apply it first.
                        enemy.HitEnemyClientRpc(wholeDamage, -1, true, 1);
                        if (enemy.isEnemyDead)
                            FractionalEnemyDamage.Remove(enemy);
                    }
                    if (canDamage && tier >= 3 && !enemy.isEnemyDead)
                        enemy.SetEnemyStunned(true, EnemyStunSeconds, attacker);
                    NativeFistHitKind kind = clinging ? NativeFistHitKind.ClingingEnemy : NativeFistHitKind.Enemy;
                    BroadcastImpact(clientId, sequence, kind, FistSurfaceClassifier.UnknownSurface, ChooseVanillaHitClip(), point);
                    EmitAuthorityNoise(attacker, point);
                    return;
                }

                if (targetPlayer == attacker || IsWorldBlocked(origin, point))
                    continue;
                if (targetPlayer != null && targetPlayer.isPlayerControlled && !targetPlayer.isPlayerDead && !targetPlayer.inAnimationWithEnemy)
                {
                    targetPlayer.DamagePlayerFromOtherClientClientRpc(PlayerDamage, forward, (int)attacker.playerClientId, Mathf.Max(0, targetPlayer.health - PlayerDamage));
                    BroadcastImpact(clientId, sequence, NativeFistHitKind.Player, FistSurfaceClassifier.UnknownSurface, ChooseVanillaHitClip(), point);
                    EmitAuthorityNoise(attacker, point);
                    return;
                }
            }
            BroadcastImpact(clientId, sequence, NativeFistHitKind.Miss, FistSurfaceClassifier.UnknownSurface, byte.MaxValue, origin + forward * RangeMeters);
        }

        private static bool CanDamageEnemy(EnemyAI enemy)
        {
            if (!(enemy is NutcrackerEnemyAI nutcracker))
                return true;
            bool isInspecting = NutcrackerIsInspectingField != null &&
                                NutcrackerIsInspectingField.GetValue(nutcracker) is bool value && value;
            return isInspecting || nutcracker.currentBehaviourStateIndex == 2;
        }

        private static bool IsClingingEnemy(EnemyAI enemy, PlayerControllerB player)
        {
            if (enemy is CentipedeAI centipede)
                return centipede.clingingToPlayer == player;
            if (enemy is FlowerSnakeEnemy flowerSnake)
                return flowerSnake.clingingToPlayer == player;
            return false;
        }

        private static bool TryHitClingingTongue(Collider collider, PlayerControllerB attacker)
        {
            BushWolfTongueCollider tongue = collider.GetComponent<BushWolfTongueCollider>() ??
                                             collider.GetComponentInParent<BushWolfTongueCollider>();
            BushWolfEnemy bushWolf = tongue != null ? tongue.bushWolfScript : null;
            if (bushWolf == null || bushWolf.draggingPlayer != attacker ||
                BushWolfHitTongueLocalClientMethod == null)
                return false;

            try
            {
                // HitTongue normally starts on the attacking client. Native fists start on the
                // server, so apply the vanilla local primitive there and mirror it to every client.
                BushWolfHitTongueLocalClientMethod.Invoke(bushWolf, null);
                bushWolf.HitTongueClientRpc(-1);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: fox tongue hit failed: " + e.Message);
                return false;
            }
        }

        private static bool IsWorldBlocked(Vector3 origin, Vector3 point)
        {
            return point != Vector3.zero && StartOfRound.Instance != null && Physics.Linecast(origin, point, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore);
        }

        private static void EmitAuthorityNoise(PlayerControllerB attacker, Vector3 point)
        {
            RoundManager.Instance?.PlayAudibleNoise(point, 17f, 0.8f, 0, attacker != null && attacker.isInElevator && StartOfRound.Instance != null && StartOfRound.Instance.hangarDoorsClosed, FistNoiseId);
        }

        private static void BroadcastEvent(ulong playerId, uint sequence, NativeFistPhase phase, NativeFistHand hand)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null || !network.IsServer)
                return;
            using (var writer = new FastBufferWriter(14, Allocator.Temp))
            {
                writer.WriteValueSafe(playerId);
                writer.WriteValueSafe(sequence);
                writer.WriteValueSafe((byte)phase);
                writer.WriteValueSafe((byte)hand);
                network.CustomMessagingManager.SendNamedMessageToAll(EventMessage, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnEvent(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
                return;
            try
            {
                reader.ReadValueSafe(out ulong playerId);
                reader.ReadValueSafe(out uint sequence);
                reader.ReadValueSafe(out byte phaseValue);
                reader.ReadValueSafe(out byte handValue);
                if (phaseValue > (byte)NativeFistPhase.Unequipping || handValue > (byte)NativeFistHand.Right)
                    return;
                ApplyAuthoritativeEvent(playerId, sequence, (NativeFistPhase)phaseValue, (NativeFistHand)handValue);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: invalid Event: " + e.Message);
            }
        }

        private static void ApplyAuthoritativeEvent(ulong playerId, uint sequence, NativeFistPhase phase, NativeFistHand hand)
        {
            PlayerControllerB player = ResolvePlayer(playerId);
            if (player == null)
            {
                if (phase != NativeFistPhase.Inactive)
                    PendingSnapshotPlayers.Add(playerId);
                return;
            }
            if (phase == NativeFistPhase.Inactive)
                StopPresentation(playerId, true);
            else
            {
                bool alreadyEquipping = Presentations.TryGetValue(playerId, out PresentationSession before) &&
                                         before.Phase == NativeFistPhase.Equipping;
                EnsurePresentation(player, false);
                if (phase == NativeFistPhase.Equipping && !alreadyEquipping)
                    FirePresentationTrigger(playerId, EnterTrigger);
                else if (phase == NativeFistPhase.Punching)
                    PresentPunch(playerId, sequence, hand);
                else if (phase == NativeFistPhase.Unequipping)
                    StopPresentation(playerId, false);
                if (Presentations.TryGetValue(playerId, out PresentationSession presentation))
                    presentation.Phase = phase;
            }
            if (player == GameNetworkManager.Instance?.localPlayerController)
            {
                if (phase == NativeFistPhase.Unequipping && player.isSprinting)
                {
                    // Sprint is a one-way interruption locally. Keep input inactive while
                    // the presentation finishes its authored exit instead of re-entering
                    // the active fist state machine.
                    ResetLocalState();
                    return;
                }
                localPhase = phase;
                if (phase == NativeFistPhase.Punching)
                    localNextHand = hand == NativeFistHand.Left ? NativeFistHand.Right : NativeFistHand.Left;
                else if (phase == NativeFistPhase.Inactive)
                    ResetLocalState();
            }
        }

        private static void BroadcastImpact(ulong playerId, uint sequence, NativeFistHitKind kind, int surfaceIndex, byte audioSelection, Vector3 point)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null || !network.IsServer)
                return;
            using (var writer = new FastBufferWriter(32, Allocator.Temp))
            {
                writer.WriteValueSafe(playerId);
                writer.WriteValueSafe(sequence);
                writer.WriteValueSafe((byte)kind);
                writer.WriteValueSafe((short)surfaceIndex);
                writer.WriteValueSafe(audioSelection);
                writer.WriteValueSafe(point);
                network.CustomMessagingManager.SendNamedMessageToAll(ImpactMessage, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnImpact(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
                return;
            try
            {
                reader.ReadValueSafe(out ulong playerId);
                reader.ReadValueSafe(out uint sequence);
                reader.ReadValueSafe(out byte kindValue);
                reader.ReadValueSafe(out short surfaceIndex);
                reader.ReadValueSafe(out byte audioSelection);
                reader.ReadValueSafe(out Vector3 point);
                if (kindValue <= (byte)NativeFistHitKind.Player)
                    PlayImpactPresentation(ResolvePlayer(playerId), (NativeFistHitKind)kindValue, surfaceIndex, audioSelection, point);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: invalid Impact: " + e.Message);
            }
        }

        private static void SendSnapshot(ulong recipientClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null || !network.IsServer)
                return;
            var active = new List<ulong>();
            foreach (KeyValuePair<ulong, ServerSession> pair in ServerSessions)
            {
                NativeFistPhase phase = pair.Value.Model.Phase;
                if (phase != NativeFistPhase.Inactive && phase != NativeFistPhase.Unequipping)
                    active.Add(pair.Key);
            }
            using (var writer = new FastBufferWriter(2 + active.Count * 8, Allocator.Temp))
            {
                writer.WriteValueSafe((ushort)active.Count);
                for (int i = 0; i < active.Count; i++)
                    writer.WriteValueSafe(active[i]);
                network.CustomMessagingManager.SendNamedMessage(SnapshotMessage, recipientClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnSnapshot(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
                return;
            try
            {
                reader.ReadValueSafe(out ushort count);
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out ulong playerId);
                    PlayerControllerB player = ResolvePlayer(playerId);
                    if (player == null)
                        PendingSnapshotPlayers.Add(playerId);
                    else
                        EnsurePresentation(player, true);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: invalid Snapshot: " + e.Message);
            }
        }

        private static void RetryPendingSnapshot(PlayerControllerB player)
        {
            if (player != null && PendingSnapshotPlayers.Remove(player.actualClientId))
                EnsurePresentation(player, true);
        }

        private static bool EnsurePackRegistered()
        {
            if (packRegistered)
                return true;
            if (Time.realtimeSinceStartup < nextPackRegistrationAttemptAt)
                return false;
            nextPackRegistrationAttemptAt = Time.realtimeSinceStartup + 2f;
            string root = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
            string firstPath = Path.Combine(root, FirstPersonManifestFile);
            string thirdPath = Path.Combine(root, ThirdPersonManifestFile);
            if (!File.Exists(firstPath) || !File.Exists(thirdPath))
            {
                if (!missingManifestLogged)
                {
                    missingManifestLogged = true;
                    Plugin.Log?.LogWarning("NativeFists: animation manifests missing; combat is active but presentation cannot start.");
                }
                return false;
            }
            try
            {
                var pack = new InteractionAnimationPackDefinition
                {
                    PackId = PackId,
                    Version = Plugin.Version,
                    AssetRootPath = root,
                    Interactions = new[]
                    {
                        new InteractionAnimationDefinition { InteractionId = FirstPersonInteractionId, PresentationKind = InteractionAnimationPresentationKind.DedicatedLocalViewmodel, ManifestJson = File.ReadAllText(firstPath) },
                        new InteractionAnimationDefinition { InteractionId = ThirdPersonInteractionId, PresentationKind = InteractionAnimationPresentationKind.BodyWorld, ManifestJson = File.ReadAllText(thirdPath) },
                    },
                };
                if (!LCInteractionAnimationAPI.TryRegisterInteractionPack(pack, out string reason) && !string.Equals(reason, "pack_already_registered", StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log?.LogWarning("NativeFists: pack registration failed: " + reason);
                    return false;
                }
                packRegistered = true;
                missingManifestLogged = false;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: pack registration failed: " + e.Message);
                return false;
            }
        }

        private static void EnsurePresentation(PlayerControllerB player, bool directToIdle)
        {
            if (player == null || !EnsurePackRegistered())
                return;
            ulong playerId = player.actualClientId;
            if (Presentations.TryGetValue(playerId, out PresentationSession existing) && existing.Handle.IsValid && LCInteractionAnimationAPI.IsInteractionActive(existing.Handle))
            {
                // A live presentation already owns its phase. Snapshot SyncIdle is only for a newly started Hidden controller.
                return;
            }
            bool local = player == GameNetworkManager.Instance?.localPlayerController;
            var request = new InteractionAnimationRequest { Player = player, PackId = PackId, InteractionId = local ? FirstPersonInteractionId : ThirdPersonInteractionId };
            if (!LCInteractionAnimationAPI.TryStartInteraction(request, out InteractionAnimationHandle handle, out string reason))
            {
                Plugin.Log?.LogWarning("NativeFists: " + (local ? "FP" : "TP") + " presentation failed: " + reason);
                return;
            }
            Presentations[playerId] = new PresentationSession { Handle = handle, Phase = directToIdle ? NativeFistPhase.Idle : NativeFistPhase.Equipping };
            if (directToIdle)
                if (!LCInteractionAnimationAPI.TryFireInteractionTrigger(handle, SyncIdleTrigger))
                    Plugin.Log?.LogWarning("NativeFists: late-join SyncIdle presentation trigger rejected.");
            UpdatePresentationParameters(player);
        }

        private static void UpdatePresentationParameters(PlayerControllerB player)
        {
            if (player == null ||
                !Presentations.TryGetValue(player.actualClientId, out PresentationSession presentation) ||
                !presentation.Handle.IsValid ||
                !LCInteractionAnimationAPI.IsInteractionActive(presentation.Handle) ||
                presentation.Phase == NativeFistPhase.Inactive ||
                presentation.Phase == NativeFistPhase.Unequipping)
            {
                return;
            }

            float upperBodyWeight = 0f;
            Animator bodyAnimator = player.playerBodyAnimator;
            if (bodyAnimator != null)
            {
                int layer = bodyAnimator.GetLayerIndex(UpperBodyLayer);
                if (layer >= 0)
                    upperBodyWeight = bodyAnimator.GetLayerWeight(layer);
            }
            bool nearWall = presentation.HasNearWall && presentation.NearWall
                ? upperBodyWeight > NearWallExitWeight
                : upperBodyWeight >= NearWallEnterWeight;
            if (!presentation.HasNearWall || presentation.NearWall != nearWall)
            {
                if (LCInteractionAnimationAPI.TrySetInteractionBool(
                        presentation.Handle, NearWallBool, nearWall))
                {
                    presentation.HasNearWall = true;
                    presentation.NearWall = nearWall;
                }
            }
        }

        private static bool FirePresentationTrigger(ulong playerId, string trigger)
        {
            if (Presentations.TryGetValue(playerId, out PresentationSession presentation) && presentation.Handle.IsValid && LCInteractionAnimationAPI.IsInteractionActive(presentation.Handle))
                return LCInteractionAnimationAPI.TryFireInteractionTrigger(presentation.Handle, trigger);
            return false;
        }

        private static void PresentPunch(ulong playerId, uint sequence, NativeFistHand hand)
        {
            PlayerControllerB player = ResolvePlayer(playerId);
            if (player == null)
                return;
            EnsurePresentation(player, false);
            if (!Presentations.TryGetValue(playerId, out PresentationSession presentation))
                return;
            if (presentation.HasPunchSequence && presentation.LastPunchSequence == sequence)
                return;
            presentation.HasPunchSequence = true;
            presentation.LastPunchSequence = sequence;
            presentation.Phase = NativeFistPhase.Punching;
            string trigger = hand == NativeFistHand.Right ? PunchRightTrigger : PunchLeftTrigger;
            bool animationStarted = FirePresentationTrigger(playerId, trigger);
            if (animationStarted)
                PlayVanillaSwing(player);
            else
                Plugin.Log?.LogWarning(
                    $"NativeFists: punch presentation trigger rejected player={playerId} " +
                    $"sequence={sequence} trigger='{trigger}'; suppressing orphan swing audio.");
            if (player == GameNetworkManager.Instance?.localPlayerController)
            {
                localPhase = NativeFistPhase.Punching;
                localActivePunchSequence = sequence;
                localNextPunchAllowedAt = Time.realtimeSinceStartup + (float)NativeFistSessionModel.PunchCadenceSeconds;
                localGeneration++;
                Host()?.StartCoroutine(PredictPunchFinished(sequence, localGeneration));
            }
        }

        private static void StopPresentation(ulong playerId, bool immediate)
        {
            if (!Presentations.TryGetValue(playerId, out PresentationSession presentation))
                return;
            if (!immediate && presentation.Phase == NativeFistPhase.Unequipping)
                return;
            if (presentation.Handle.IsValid && LCInteractionAnimationAPI.IsInteractionActive(presentation.Handle))
            {
                if (immediate)
                {
                    LCInteractionAnimationAPI.TryStopInteraction(presentation.Handle, InteractionAnimationStopReason.Interrupted);
                }
                else if (!LCInteractionAnimationAPI.TryBeginInteractionExit(presentation.Handle, out string reason))
                {
                    Plugin.Log?.LogWarning("NativeFists: graceful Unequip presentation rejected: " + reason);
                    LCInteractionAnimationAPI.TryStopInteraction(
                        presentation.Handle,
                        InteractionAnimationStopReason.Requested);
                }
            }
            if (immediate)
                Presentations.Remove(playerId);
            else
                presentation.Phase = NativeFistPhase.Unequipping;
        }

        private static void BeginLocalSprintExit(PlayerControllerB local)
        {
            if (local == null || localPhase == NativeFistPhase.Inactive)
                return;
            uint sequence = NextLocalSequence();
            StopPresentation(local.actualClientId, false);
            ResetLocalState();
            if (registeredNetworkManager != null)
                SendSprintExitRequest(sequence);
        }

        private static void InterruptLocal(bool sendRequest)
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (sendRequest && registeredNetworkManager != null)
                SendStateRequest(NextLocalSequence(), false);
            if (local != null)
                StopPresentation(local.actualClientId, true);
            ResetLocalState();
        }

        private static void ResetLocalState()
        {
            localGeneration++;
            localPhase = NativeFistPhase.Inactive;
            localNextHand = NativeFistHand.Left;
            localActivePunchSequence = 0;
            localNextPunchAllowedAt = 0f;
        }

        private static void ResetRuntime(bool unregisterNetwork)
        {
            foreach (ulong playerId in new List<ulong>(Presentations.Keys))
                StopPresentation(playerId, true);
            Presentations.Clear();
            PendingSnapshotPlayers.Clear();
            ServerSessions.Clear();
            FractionalEnemyDamage.Clear();
            nextLocalSequence = 1;
            ResetLocalState();
            vanillaSwingClip = null;
            vanillaHitClips = null;
            vanillaAudioResolved = false;
            if (unregisterNetwork)
            {
                UnregisterNetworkHandlers();
                packRegistered = false;
                missingManifestLogged = false;
                nextPackRegistrationAttemptAt = 0f;
            }
        }

        private static ServerSession GetServerSession(ulong clientId)
        {
            if (!ServerSessions.TryGetValue(clientId, out ServerSession session))
            {
                session = new ServerSession();
                ServerSessions[clientId] = session;
            }
            return session;
        }

        private static PlayerControllerB ResolvePlayer(ulong clientId)
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

        private static bool CanUseFists(PlayerControllerB player, bool requireEmptyHands)
        {
            if (player == null || !player.isPlayerControlled || player.isPlayerDead || player.isSprinting || player.isTypingChat || player.inTerminalMenu || player.isClimbingLadder || player.inVehicleAnimation || player.inSpecialInteractAnimation || player.enteringSpecialAnimation || player.playingQuickSpecialAnimation || player.inAnimationWithEnemy || player.performingEmote || player.doingUpperBodyEmote > 0f || player.isHoldingInteract || player.teleportedLastFrame || player.inSpecialMenu || player.isGrabbingObjectAnimation || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen))
                return false;
            return !requireEmptyHands || (player.currentlyHeldObjectServer == null && !player.isHoldingObject);
        }

        private static Vector3 ServerAimOrigin(PlayerControllerB player)
        {
            return player.gameplayCamera != null
                ? player.gameplayCamera.transform.position
                : player.transform.position + Vector3.up * 1.5f;
        }

        private static bool ValidateAim(PlayerControllerB player, Vector3 origin, Vector3 forward)
        {
            if (!IsFinite(origin) || !IsFinite(forward) || forward.magnitude < 0.8f || forward.magnitude > 1.2f)
                return false;
            Vector3 expectedOrigin = ServerAimOrigin(player);
            Vector3 expectedForward = player.gameplayCamera != null ? player.gameplayCamera.transform.forward : player.transform.forward;
            return Vector3.Distance(origin, expectedOrigin) <= MaxAimOriginErrorMeters &&
                   Vector3.Dot(forward.normalized, expectedForward.normalized) >= MinAimForwardDot;
        }

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static double NetworkTime() => NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : Time.realtimeSinceStartupAsDouble;

        private static uint NextLocalSequence()
        {
            uint value = nextLocalSequence++;
            return value == 0 ? nextLocalSequence++ : value;
        }

        private static MonoBehaviour Host()
        {
            if (StartOfRound.Instance != null)
                return StartOfRound.Instance;
            if (GameNetworkManager.Instance != null)
                return GameNetworkManager.Instance;
            return HUDManager.Instance;
        }

        private static byte ChooseVanillaHitClip()
        {
            ResolveVanillaShovelAudio();
            return vanillaHitClips == null || vanillaHitClips.Length == 0 ? byte.MaxValue : (byte)UnityEngine.Random.Range(0, Mathf.Min(vanillaHitClips.Length, byte.MaxValue));
        }

        private static void PlayVanillaSwing(PlayerControllerB player)
        {
            ResolveVanillaShovelAudio();
            if (player?.movementAudio != null && vanillaSwingClip != null)
                player.movementAudio.PlayOneShot(vanillaSwingClip);
        }

        private static void PlayImpactPresentation(PlayerControllerB attacker, NativeFistHitKind kind, int surfaceIndex, byte audioSelection, Vector3 point)
        {
            if (kind == NativeFistHitKind.Miss)
                return;
            ResolveVanillaShovelAudio();
            AudioSource source = attacker?.movementAudio;
            if (source != null && vanillaHitClips != null && audioSelection < vanillaHitClips.Length && vanillaHitClips[audioSelection] != null)
                source.PlayOneShot(vanillaHitClips[audioSelection]);
            if (kind == NativeFistHitKind.Surface && source != null)
            {
                AudioClip surfaceClip = FistSurfaceClassifier.HitSurfaceClip(surfaceIndex);
                if (surfaceClip != null)
                    source.PlayOneShot(surfaceClip);
            }
        }

        private static void ResolveVanillaShovelAudio()
        {
            if (vanillaAudioResolved)
                return;
            vanillaAudioResolved = true;
            AllItemsList itemList = StartOfRound.Instance?.allItemsList;
            if (itemList?.itemsList == null)
                return;
            for (int i = 0; i < itemList.itemsList.Count; i++)
            {
                Item item = itemList.itemsList[i];
                Shovel shovel = item?.spawnPrefab != null ? item.spawnPrefab.GetComponent<Shovel>() : null;
                if (shovel == null)
                    continue;
                vanillaSwingClip = shovel.swing;
                vanillaHitClips = shovel.hitSFX;
                return;
            }
        }
    }
}
