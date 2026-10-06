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
        // F-FIST-4: SetEnemyStunned is a purely local mutation, and the victim's own client is the
        // one that decides contact damage, so a server-only stun left the punching client walking
        // into a creature that looked stunned only on the host. Added as its own message rather
        // than a field on Impact.v1 so the existing wire formats stay byte-identical.
        private const string StunMessage = "Y4NGZ.NativeFists.Stun.v1";

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
        private const string FirstPersonViewmodelRootPrefix = "Y4NGZ_Viewmodel_y4ngz_native-fists_firstperson";

        private const float RangeMeters = 1.2f;
        private const float SphereRadiusMeters = 0.8f;
        // F-FIST-16: byte-for-byte vanilla Shovel.shovelMask (Assembly-CSharp v81). Keeping the
        // literal - rather than rebuilding it from LayerMask.GetMask - means a fist cast hits
        // exactly what a shovel swing hits, including the trigger colliders vanilla relies on.
        private const int CollisionMask = 11012424;
        private const int PlayerDamage = 10;
        private const float MaxAimOriginErrorMeters = 0.75f;
        // F-FIST-5: the 0.75 m clamp is compared against the host's interpolated copy of the
        // client's camera, which trails the client's send-time origin by roughly one replication
        // step plus one RTT. Widen it by the distance a sprinting player (vanilla max ~8 m/s)
        // covers in that RTT so latency cannot silently eat punches, and cap the widening so the
        // gate still clamps a teleporting client.
        private const float AimOriginRttSpeedMetersPerSecond = 8f;
        private const float MaxAimOriginRttToleranceMeters = 2.5f;
        private const float MinAimForwardDot = 0.25f;
        private const int FistNoiseId = 9011;
        private const float RejectionLogThrottleSeconds = 1f;

        private static readonly Dictionary<ulong, ServerSession> ServerSessions = new Dictionary<ulong, ServerSession>();
        private static readonly Dictionary<ulong, PresentationSession> Presentations = new Dictionary<ulong, PresentationSession>();
        private static readonly HashSet<ulong> PendingSnapshotPlayers = new HashSet<ulong>();
        private static readonly NativeFistFractionalDamage<EnemyAI> FractionalEnemyDamage = new NativeFistFractionalDamage<EnemyAI>();
        private static readonly System.Reflection.FieldInfo NutcrackerIsInspectingField =
            AccessTools.Field(typeof(NutcrackerEnemyAI), "isInspecting");
        private static readonly System.Reflection.MethodInfo BushWolfHitTongueLocalClientMethod =
            AccessTools.Method(typeof(BushWolfEnemy), "HitTongueLocalClient");
        private static readonly System.Reflection.FieldInfo BushWolfDraggingField =
            AccessTools.Field(typeof(BushWolfEnemy), "dragging");
        private static readonly System.Reflection.FieldInfo BushWolfStartedShootingTongueField =
            AccessTools.Field(typeof(BushWolfEnemy), "startedShootingTongue");
        // F-FIST-6: cached so the per-punch stale sweep never allocates a closure.
        private static readonly Predicate<EnemyAI> StaleFractionalTarget =
            enemy => enemy == null || enemy.isEnemyDead;

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
        private static bool vanillaAudioFailureLogged;
        private static bool presentationUnavailableTipShown;
        private static bool presentationStartFailureLogged;
        private static string lastRejectionReason;
        private static float nextRejectionLogAt;

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
            internal NativeFistHand ActiveHand = NativeFistHand.None;
            internal bool HasPunchSequence;
            internal uint LastPunchSequence;
            // Wall-clock start for the accepted punch visual. It is copied when Diagnostics
            // swaps the local viewmodel for BodyWorld, so the new controller resumes the same
            // authored punch rather than replaying its wind-up from frame zero.
            internal float PunchStartedAt;
            internal bool HasPendingPunchResume;
            internal int PunchResumeAfterFrame;
            internal bool HasNearWall;
            internal bool NearWall;
            // This describes the presentation selected on this CLIENT. It is deliberately
            // separate from network ownership: Diagnostics moves the local gameplay camera but
            // does not turn the local player into a remote player.
            internal bool UsesBodyWorld;
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
            if (!CanUseFists(__instance, true, "local-activate"))
                return true;

            try
            {
                RegisterNetworkHandlers();
                EnsurePackRegistered();
                if (localPhase == NativeFistPhase.Inactive)
                {
                    ShadowStepPatch.BreakCloakForAction(__instance);
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

                ShadowStepPatch.BreakCloakForAction(__instance);
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
                else if (!CanUseFists(__instance, true, "local-update"))
                    InterruptLocal(true);
            }
            if (__instance == GameNetworkManager.Instance?.localPlayerController &&
                localPhase != NativeFistPhase.Inactive)
            {
                // The diagnostics external camera reuses gameplayCamera. Re-evaluate the visual
                // lease each frame so moving it during a punch swaps only presentation, retaining
                // the current network-authoritative phase and alternating hand.
                EnsurePresentation(__instance, false);
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
            if (!CanUseFists(__instance, true, "server-update") || UpgradeTierSync.GetTier(clientId, NativeFistsUpgrade.UPGRADE_ID) <= 0)
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
            if (__instance == null || !__instance.isPlayerDead)
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
            {
                localPhase = NativeFistPhase.Idle;
                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
                if (local != null && Presentations.TryGetValue(local.actualClientId, out PresentationSession presentation) &&
                    presentation.Phase == NativeFistPhase.Punching && presentation.LastPunchSequence == sequence)
                {
                    // This is exactly the authored 0.75 s cadence, never an early contact or a
                    // shortened clip. Returning through the controller's idle trigger avoids the
                    // final punch frame lingering in first person while the server's Idle event
                    // travels back to the owner.
                    presentation.Phase = NativeFistPhase.Idle;
                    presentation.ActiveHand = NativeFistHand.None;
                    presentation.HasPendingPunchResume = false;
                    FirePresentationTrigger(local.actualClientId, SyncIdleTrigger);
                }
            }
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
                messaging.RegisterNamedMessageHandler(StunMessage, OnStun);
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
                    messaging.UnregisterNamedMessageHandler(StunMessage);
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
            // F-FIST-8: InterruptServer interrupts *and* publishes Inactive. The bare Interrupt()
            // this used to do left every other client holding a third-person fist presentation for
            // a player who is already gone, until the next ResetRuntime tore it down.
            if (ServerSessions.TryGetValue(clientId, out ServerSession session))
            {
                InterruptServer(clientId, session);
                // Bump the generation for an already-inactive session too, so a pending
                // FinishServerUnequip cannot resolve for a client that is gone.
                session.Model.Interrupt();
            }
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
                    !CanUseFists(player, true, "server-activate"))
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
                    !CanUseFists(player, true, "server-punch") ||
                    !ValidateAim(senderClientId, player, origin, forward))
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
            if (!CanUseFists(attacker, true, "server-contact"))
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
            // F-FIST-6: sweep remainders whose enemy died or despawned. Punches are 0.75 s apart, so
            // this is nowhere near a per-frame path, and it stops destroyed EnemyAI references from
            // living in the accumulator until the next ResetRuntime.
            FractionalEnemyDamage.PurgeTargets(StaleFractionalTarget);
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
                        ? FractionalEnemyDamage.Add(clientId, enemy, NativeFistsUpgrade.GetDamageForTier(tier))
                        : 0;
                    // F-FIST-6/F-FIST-10: always enter vanilla HitEnemy, even for a sub-threshold
                    // level-1 punch or an armoured Nutcracker. A shovel that deals no damage still
                    // plays the body/armour clip and still aggros (HoarderBugAI.angryAtPlayer,
                    // NutcrackerEnemyAI.SeeMovingThreatServerRpc); short-circuiting on zero made half
                    // the level-1 punches and every armour bounce completely inert.
                    ApplyMirroredHitEnemy(enemy, wholeDamage, attacker);
                    if (enemy.isEnemyDead)
                        FractionalEnemyDamage.Remove(enemy);
                    if (canDamage && NativeFistsUpgrade.ShouldStun(tier) && !enemy.isEnemyDead)
                        ApplyAuthoritativeStun(enemy, attacker);
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

        /// <summary>
        /// F-FIST-1: applies the hit once on the server and mirrors it to the *other* clients.
        /// <c>NetworkBehaviour.__endSendClientRpc</c> also invokes the RPC body locally on the host,
        /// and <c>EnemyAI.HitEnemyClientRpc</c> re-runs <c>HitEnemy</c> for every receiver whose
        /// <c>playerClientId</c> differs from <c>playerWhoHit</c>. The old <c>-1</c> matched nobody,
        /// so every host-side punch landed twice and Lethal Hands dealt double its advertised damage.
        /// Passing the host's own id is the same shape <c>BurningEnemyEffect.ApplyDamageTick</c> uses.
        /// </summary>
        private static void ApplyMirroredHitEnemy(EnemyAI enemy, int damage, PlayerControllerB attacker)
        {
            if (enemy == null)
                return;
            enemy.HitEnemy(damage, attacker, true, 1);

            int skipLocalPlayerId = -1;
            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            if (localPlayer != null)
                skipLocalPlayerId = (int)localPlayer.playerClientId;

            if (skipLocalPlayerId >= 0)
                enemy.HitEnemyClientRpc(damage, skipLocalPlayerId, true, 1);
        }

        /// <summary>
        /// F-FIST-4: stuns on the server for the AI interrupt the host owns, then tells every client
        /// so the victim's own machine - the one that runs
        /// <c>MeetsStandardPlayerCollisionConditions</c> - actually sees a stunned creature. The host
        /// re-entry through its own loopback is a no-op because vanilla's
        /// <c>postStunInvincibilityTimer</c> is already armed by the direct call.
        /// </summary>
        private static void ApplyAuthoritativeStun(EnemyAI enemy, PlayerControllerB attacker)
        {
            enemy.SetEnemyStunned(true, NativeFistsUpgrade.TIER_3_STUN_SECONDS, attacker);
            NetworkObject networkObject = enemy.NetworkObject;
            if (networkObject == null || !networkObject.IsSpawned)
                return;
            BroadcastStun(networkObject.NetworkObjectId, NativeFistsUpgrade.TIER_3_STUN_SECONDS, attacker);
        }

        private static void BroadcastStun(ulong enemyNetworkObjectId, float seconds, PlayerControllerB attacker)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null || !network.IsServer)
                return;
            ulong attackerClientId = attacker != null ? attacker.actualClientId : ulong.MaxValue;
            using (var writer = new FastBufferWriter(24, Allocator.Temp))
            {
                writer.WriteValueSafe(enemyNetworkObjectId);
                writer.WriteValueSafe(seconds);
                writer.WriteValueSafe(attackerClientId);
                network.CustomMessagingManager.SendNamedMessageToAll(StunMessage, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnStun(ulong senderClientId, FastBufferReader reader)
        {
            // Same host-validation shape as OnEvent/OnImpact: only the server may drive presentation
            // or gameplay state on a peer.
            if (senderClientId != NetworkManager.ServerClientId)
                return;
            try
            {
                reader.ReadValueSafe(out ulong enemyNetworkObjectId);
                reader.ReadValueSafe(out float seconds);
                reader.ReadValueSafe(out ulong attackerClientId);
                if (!IsFinite(seconds) || seconds <= 0f || seconds > 30f)
                    return;
                NetworkManager network = NetworkManager.Singleton;
                if (network?.SpawnManager == null ||
                    !network.SpawnManager.SpawnedObjects.TryGetValue(enemyNetworkObjectId, out NetworkObject networkObject) ||
                    networkObject == null)
                {
                    return;
                }
                EnemyAI enemy = networkObject.GetComponent<EnemyAI>() ?? networkObject.GetComponentInChildren<EnemyAI>();
                if (enemy == null)
                    return;
                enemy.SetEnemyStunned(true, seconds, attackerClientId == ulong.MaxValue ? null : ResolvePlayer(attackerClientId));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: invalid Stun: " + e.Message);
            }
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
            // F-FIST-7: vanilla BushWolfEnemy.HitTongue refuses the hit unless the wolf is actually
            // reeling a player in and the tongue is not still being shot out. The fist path only
            // checked draggingPlayer, so a mid-shot tongue could be "hit".
            if (!IsBushWolfTongueHittable(bushWolf))
                return false;

            try
            {
                // HitTongue normally starts on the attacking client. Native fists start on the
                // server, so apply the vanilla local primitive there and mirror it to every client.
                BushWolfHitTongueLocalClientMethod.Invoke(bushWolf, null);
                // F-FIST-7: same double-apply as F-FIST-1 - HitTongueClientRpc runs
                // HitTongueLocalClient on every receiver whose id differs from playerWhoHit, and the
                // host executes the RPC body locally, so -1 replayed the hit SFX and the behaviour
                // state switch on the host. Skip the host's own local player.
                PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
                if (localPlayer != null)
                    bushWolf.HitTongueClientRpc((int)localPlayer.playerClientId);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("NativeFists: fox tongue hit failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Mirrors vanilla <c>BushWolfEnemy.HitTongue</c>'s own guard (#F-FIST-7).</summary>
        private static bool IsBushWolfTongueHittable(BushWolfEnemy bushWolf)
        {
            if (BushWolfDraggingField == null || BushWolfStartedShootingTongueField == null)
                return true;
            bool dragging = BushWolfDraggingField.GetValue(bushWolf) is bool draggingValue && draggingValue;
            bool startedShootingTongue =
                BushWolfStartedShootingTongueField.GetValue(bushWolf) is bool shootingValue && shootingValue;
            return dragging && !startedShootingTongue;
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
                else if (phase == NativeFistPhase.Idle &&
                         (!Presentations.TryGetValue(playerId, out PresentationSession idleBefore) ||
                          idleBefore.Phase != NativeFistPhase.Idle))
                    FirePresentationTrigger(playerId, SyncIdleTrigger);
                else if (phase == NativeFistPhase.Unequipping)
                    StopPresentation(playerId, false);
                if (Presentations.TryGetValue(playerId, out PresentationSession presentation))
                {
                    presentation.Phase = phase;
                    if (phase == NativeFistPhase.Idle)
                    {
                        presentation.ActiveHand = NativeFistHand.None;
                        presentation.HasPendingPunchResume = false;
                    }
                }
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
                    ReportPresentationUnavailable(
                        "animation manifests missing; combat is active but presentation cannot start.");
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
                    ReportPresentationUnavailable("pack registration failed: " + reason);
                    return false;
                }
                packRegistered = true;
                missingManifestLogged = false;
                presentationStartFailureLogged = false;
                return true;
            }
            catch (Exception e)
            {
                ReportPresentationUnavailable("pack registration failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// F-FIST-2: a failed pack registration or viewmodel start used to be a single LogWarning,
        /// and the punch then returned before it played anything. That is verbatim the reported
        /// "no fists or anything, but I could hear a shovel sound" failure: the player concluded the
        /// upgrade was dead while enemies were quietly taking damage. Say so loudly in the log and
        /// tell the player once, in the HUD, that only the visuals are missing.
        /// </summary>
        private static void ReportPresentationUnavailable(string detail)
        {
            Plugin.Log?.LogError("NativeFists: " + detail);
            if (presentationUnavailableTipShown)
                return;
            presentationUnavailableTipShown = true;
            HUDManager.Instance?.DisplayTip(
                "LETHAL HANDS",
                "Fist visuals are unavailable - punches still land. Check the BepInEx log.",
                true);
        }

        private static void EnsurePresentation(PlayerControllerB player, bool directToIdle)
        {
            if (player == null)
                return;
            ulong playerId = player.actualClientId;
            bool useBodyWorld = ShouldUseBodyWorldPresentation(player);
            PresentationSession existing = null;
            bool switchingPresentation = false;
            if (Presentations.TryGetValue(playerId, out existing) && existing.Handle.IsValid && LCInteractionAnimationAPI.IsInteractionActive(existing.Handle))
            {
                if (existing.UsesBodyWorld == useBodyWorld)
                {
                    // A live presentation already owns its phase. Snapshot SyncIdle is only for a
                    // newly started controller.
                    return;
                }

                // Do not send or synthesize a network event here. This is a client-only camera
                // presentation transition, so combat authority and the punch sequence remain
                // untouched while the old resource lease is exchanged for the other one.
                LCInteractionAnimationAPI.TryStopInteraction(
                    existing.Handle, InteractionAnimationStopReason.Interrupted);
                Presentations.Remove(playerId);
                switchingPresentation = true;
            }
            if (!EnsurePackRegistered())
            {
                EnsureFallbackPresentation(playerId, directToIdle);
                return;
            }
            bool local = player == GameNetworkManager.Instance?.localPlayerController;
            var request = new InteractionAnimationRequest
            {
                Player = player,
                PackId = PackId,
                InteractionId = useBodyWorld ? ThirdPersonInteractionId : FirstPersonInteractionId,
            };
            if (!LCInteractionAnimationAPI.TryStartInteraction(request, out InteractionAnimationHandle handle, out string reason))
            {
                // F-FIST-2: latched so a repeating failure does not flood the log, but escalated to
                // an error because it means this player has no fists on screen at all.
                if (!presentationStartFailureLogged)
                {
                    presentationStartFailureLogged = true;
                    if (local)
                        ReportPresentationUnavailable("FP presentation failed: " + reason);
                    else
                        Plugin.Log?.LogError("NativeFists: TP presentation failed: " + reason);
                }
                EnsureFallbackPresentation(playerId, directToIdle);
                return;
            }
            presentationStartFailureLogged = false;
            NativeFistPhase phase = directToIdle
                ? NativeFistPhase.Idle
                : existing != null ? existing.Phase : NativeFistPhase.Equipping;
            var presentation = new PresentationSession
            {
                Handle = handle,
                Phase = phase,
                ActiveHand = existing != null ? existing.ActiveHand : NativeFistHand.None,
                HasPunchSequence = existing != null && existing.HasPunchSequence,
                LastPunchSequence = existing != null ? existing.LastPunchSequence : 0,
                PunchStartedAt = existing != null ? existing.PunchStartedAt : 0f,
                UsesBodyWorld = useBodyWorld,
            };
            Presentations[playerId] = presentation;
            if (switchingPresentation || directToIdle)
                ReplayPresentationPhase(playerId, presentation, directToIdle);
            if (local && !useBodyWorld)
                SynchronizeFirstPersonSleeves(player);
            UpdatePresentationParameters(player);
        }

        private static bool ShouldUseBodyWorldPresentation(PlayerControllerB player)
        {
            // Every remote player needs the body controller. The owner does too whenever the
            // Diagnostics camera exposes their body; FirstPersonInteractionId is a camera-bound
            // viewmodel and would otherwise follow that camera into the external orbit.
            return player != GameNetworkManager.Instance?.localPlayerController ||
                   LedgeMantlePatch.IsDebugThirdPersonCamActive();
        }

        private static void ReplayPresentationPhase(
            ulong playerId,
            PresentationSession presentation,
            bool directToIdle)
        {
            if (presentation == null || !presentation.Handle.IsValid)
                return;

            if (directToIdle || presentation.Phase == NativeFistPhase.Idle)
            {
                if (!LCInteractionAnimationAPI.TryFireInteractionTrigger(presentation.Handle, SyncIdleTrigger))
                    Plugin.Log?.LogWarning("NativeFists: SyncIdle presentation trigger rejected.");
                return;
            }
            if (presentation.Phase == NativeFistPhase.Equipping)
            {
                FirePresentationTrigger(playerId, EnterTrigger);
                return;
            }
            if (presentation.Phase == NativeFistPhase.Punching)
            {
                string trigger = presentation.ActiveHand == NativeFistHand.Right
                    ? PunchRightTrigger
                    : PunchLeftTrigger;
                if (FirePresentationTrigger(playerId, trigger))
                {
                    // Interactions has no public state-time continuation API. On the following
                    // frame, the narrow bridge seeks only this new handle's owned presenter to
                    // the elapsed accepted-punch progress; it never alters combat or networking.
                    presentation.HasPendingPunchResume = true;
                    presentation.PunchResumeAfterFrame = Time.frameCount + 1;
                }
                return;
            }
            if (presentation.Phase == NativeFistPhase.Unequipping)
            {
                LCInteractionAnimationAPI.TryBeginInteractionExit(presentation.Handle, out _);
            }
        }

        /// <summary>
        /// The authored fist bundle has sleeve renderers of its own. Reuse the live first-person
        /// arm materials after the viewmodel is created so the selected suit is reflected in the
        /// fists instead of leaving the bundle's authoring suit visible.
        /// </summary>
        private static void SynchronizeFirstPersonSleeves(PlayerControllerB player)
        {
            Transform armsMetarig = player != null ? player.playerModelArmsMetarig : null;
            Transform armsRoot = armsMetarig != null ? armsMetarig.parent : null;
            Transform cameraRoot = player != null && player.gameplayCamera != null
                ? player.gameplayCamera.transform
                : null;
            if (armsRoot == null || cameraRoot == null)
                return;

            Material[] sourceMaterials = null;
            Renderer[] sourceRenderers = armsRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < sourceRenderers.Length; i++)
            {
                Material[] materials = sourceRenderers[i] != null
                    ? sourceRenderers[i].sharedMaterials
                    : null;
                if (materials != null && materials.Length > 0)
                {
                    sourceMaterials = materials;
                    break;
                }
            }
            if (sourceMaterials == null || sourceMaterials.Length == 0)
                return;

            Renderer[] cameraRenderers = cameraRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < cameraRenderers.Length; i++)
            {
                Renderer renderer = cameraRenderers[i];
                if (renderer == null || !IsFirstPersonFistRenderer(renderer.transform, cameraRoot))
                    continue;

                Material[] destination = renderer.sharedMaterials;
                if (destination == null || destination.Length == 0)
                    continue;
                var remapped = new Material[destination.Length];
                for (int materialIndex = 0; materialIndex < remapped.Length; materialIndex++)
                    remapped[materialIndex] = sourceMaterials[Mathf.Min(materialIndex, sourceMaterials.Length - 1)];
                renderer.sharedMaterials = remapped;
            }
        }

        private static bool IsFirstPersonFistRenderer(Transform transform, Transform cameraRoot)
        {
            for (Transform current = transform; current != null && current != cameraRoot; current = current.parent)
            {
                if (current.name.StartsWith(FirstPersonViewmodelRootPrefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// F-FIST-2: a handle-less bookkeeping record kept when the visual layer is unavailable. It
        /// preserves phase tracking and punch-sequence de-duplication, so the fallback punch (swing
        /// audio + local cooldown) is not presented twice by owner prediction and then again by the
        /// authoritative event.
        /// </summary>
        private static void EnsureFallbackPresentation(ulong playerId, bool directToIdle)
        {
            if (Presentations.ContainsKey(playerId))
                return;
            Presentations[playerId] = new PresentationSession
            {
                Phase = directToIdle ? NativeFistPhase.Idle : NativeFistPhase.Equipping,
                UsesBodyWorld = ShouldUseBodyWorldPresentation(ResolvePlayer(playerId)),
            };
        }

        private static void UpdatePresentationParameters(PlayerControllerB player)
        {
            if (player == null ||
                !Presentations.TryGetValue(player.actualClientId, out PresentationSession presentation))
            {
                return;
            }

            bool active = presentation.Handle.IsValid &&
                          LCInteractionAnimationAPI.IsInteractionActive(presentation.Handle);
            if (!active)
                return;
            if (presentation.Phase == NativeFistPhase.Inactive ||
                presentation.Phase == NativeFistPhase.Unequipping)
                return;

            ResumePendingPunchPresentation(presentation);

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

        private static void ResumePendingPunchPresentation(PresentationSession presentation)
        {
            if (presentation == null || !presentation.HasPendingPunchResume ||
                Time.frameCount < presentation.PunchResumeAfterFrame)
            {
                return;
            }

            float normalizedProgress = NativeFistsPunchProgressMath.Evaluate(
                Time.realtimeSinceStartup - presentation.PunchStartedAt,
                (float)NativeFistSessionModel.PunchCadenceSeconds);
            if (normalizedProgress >= 1f ||
                NativeFistsInteractionResumeBridge.TryResumePunchAt(
                    presentation.Handle,
                    normalizedProgress,
                    presentation.ActiveHand == NativeFistHand.Right
                        ? PunchRightTrigger
                        : PunchLeftTrigger))
            {
                presentation.HasPendingPunchResume = false;
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
            // F-FIST-2: a missing presentation used to return here, before the swing audio and
            // before localPhase/localNextPunchAllowedAt were set, so the punch was silent, invisible
            // AND left the local state machine stuck. The presentation is now optional: without it
            // the punch still reads and still behaves as a combat action.
            if (!Presentations.TryGetValue(playerId, out PresentationSession presentation))
                return;
            if (presentation.HasPunchSequence && presentation.LastPunchSequence == sequence)
                return;
            presentation.HasPunchSequence = true;
            presentation.LastPunchSequence = sequence;
            presentation.Phase = NativeFistPhase.Punching;
            presentation.ActiveHand = hand;
            presentation.PunchStartedAt = Time.realtimeSinceStartup;
            presentation.HasPendingPunchResume = false;
            bool hasLiveHandle = presentation.Handle.IsValid &&
                                 LCInteractionAnimationAPI.IsInteractionActive(presentation.Handle);
            string trigger = hand == NativeFistHand.Right ? PunchRightTrigger : PunchLeftTrigger;
            bool animationStarted = hasLiveHandle && FirePresentationTrigger(playerId, trigger);
            if (animationStarted || !hasLiveHandle)
            {
                // Orphan swing audio is only suppressed for the one case it was introduced for: a
                // live presentation that refused the trigger (a replayed/latched trigger would have
                // produced sound with a one-frame stutter). With no presentation at all there is no
                // orphan to avoid - silence is strictly worse.
                PlayVanillaSwing(player);
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"NativeFists: punch presentation trigger rejected player={playerId} " +
                    $"sequence={sequence} trigger='{trigger}'; suppressing orphan swing audio.");
            }
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
            {
                Presentations.Remove(playerId);
            }
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

        internal static void InterruptForTablet(PlayerControllerB player)
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || player != local || localPhase == NativeFistPhase.Inactive)
                return;

            InterruptLocal(true);
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
            // F-FIST-8: Interrupt() is what bumps Generation, and FinishServerEquip /
            // ResolveServerPunch / FinishServerUnequip all captured the session object plus a
            // generation. Clearing the dictionary alone left those coroutines - which run on
            // StartOfRound and survive the reset - still passing IsCurrentPunch, so a punch could
            // resolve damage and broadcast for a session that no longer exists.
            foreach (ServerSession session in ServerSessions.Values)
                session.Model.Interrupt();
            ServerSessions.Clear();
            FractionalEnemyDamage.Clear();
            nextLocalSequence = 1;
            ResetLocalState();
            vanillaSwingClip = null;
            vanillaHitClips = null;
            vanillaAudioResolved = false;
            vanillaAudioFailureLogged = false;
            lastRejectionReason = null;
            nextRejectionLogAt = 0f;
            if (unregisterNetwork)
            {
                UnregisterNetworkHandlers();
                packRegistered = false;
                missingManifestLogged = false;
                presentationStartFailureLogged = false;
                presentationUnavailableTipShown = false;
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

        private static bool CanUseFists(PlayerControllerB player, bool requireEmptyHands, string context = null)
        {
            string reason = FistBlockReason(player, requireEmptyHands);
            if (reason == null)
                return true;
            LogFistRejection(context, reason);
            return false;
        }

        /// <summary>
        /// F-FIST-16: the eligibility gate was one ~700-character boolean, so a refused punch could
        /// never say which term rejected it. Returns null when fists are allowed, otherwise the name
        /// of the first gate that closed.
        /// </summary>
        private static string FistBlockReason(PlayerControllerB player, bool requireEmptyHands)
        {
            if (player == null)
                return "no-player";
            string replicated = ReplicatedFistBlockReason(player, requireEmptyHands);
            if (replicated != null)
                return replicated;
            // F-FIST-3/F-FIST-9: the remaining gates live on fields vanilla only ever assigns on the
            // machine that owns the player. PlayerControllerB.quickMenuManager in particular is a
            // single shared scene reference, so reading it off a remote player on the host returned
            // the *host's* pause-menu state and cancelled everyone else's fists. These are
            // owner-trusted: only the local player is evaluated against them.
            if (player != GameNetworkManager.Instance?.localPlayerController)
                return null;
            return LocalInputFistBlockReason(player);
        }

        /// <summary>
        /// Gates the server genuinely owns or that vanilla replicates, so they are valid for any
        /// player on any machine. This is the set the host enforces for remote clients.
        /// </summary>
        private static string ReplicatedFistBlockReason(PlayerControllerB player, bool requireEmptyHands)
        {
            if (!player.isPlayerControlled)
                return "not-controlled";
            if (player.isPlayerDead)
                return "dead";
            if (player.inAnimationWithEnemy)
                return "in-animation-with-enemy";
            if (FieldOperationsTabletPatch.IsTabletActiveForPlayer(player))
                return "tablet-active";
            if (requireEmptyHands && (player.currentlyHeldObjectServer != null || player.isHoldingObject))
                return "hands-occupied";
            return null;
        }

        /// <summary>
        /// Owner-trusted local input/animation state. Sprint is listed here for completeness, but the
        /// authoritative sprint interruption travels over SprintExitRequest precisely because the
        /// host never sees a remote client's isSprinting flag flip.
        /// </summary>
        private static string LocalInputFistBlockReason(PlayerControllerB player)
        {
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return "quick-menu-open";
            if (player.isTypingChat)
                return "typing-chat";
            if (player.inTerminalMenu)
                return "terminal-menu";
            if (player.inSpecialMenu)
                return "special-menu";
            if (player.isHoldingInteract)
                return "holding-interact";
            if (player.isSprinting)
                return "sprinting";
            if (player.isClimbingLadder)
                return "ladder";
            if (player.inVehicleAnimation)
                return "vehicle";
            if (player.inSpecialInteractAnimation || player.enteringSpecialAnimation || player.playingQuickSpecialAnimation)
                return "special-animation";
            if (player.performingEmote || player.doingUpperBodyEmote > 0f)
                return "emote";
            if (player.teleportedLastFrame)
                return "teleported";
            if (player.isGrabbingObjectAnimation)
                return "grabbing-object";
            return null;
        }

        private static void LogFistRejection(string context, string reason)
        {
            if (context == null || Plugin.Log == null)
                return;
            float now = Time.realtimeSinceStartup;
            if (string.Equals(lastRejectionReason, reason, StringComparison.Ordinal) && now < nextRejectionLogAt)
                return;
            lastRejectionReason = reason;
            nextRejectionLogAt = now + RejectionLogThrottleSeconds;
            Plugin.Log.LogDebug($"NativeFists: {context} rejected by gate '{reason}'.");
        }

        private static Vector3 ServerAimOrigin(PlayerControllerB player)
        {
            return player.gameplayCamera != null
                ? player.gameplayCamera.transform.position
                : player.transform.position + Vector3.up * 1.5f;
        }

        private static bool ValidateAim(ulong clientId, PlayerControllerB player, Vector3 origin, Vector3 forward)
        {
            if (!IsFinite(origin) || !IsFinite(forward) || forward.magnitude < 0.8f || forward.magnitude > 1.2f)
            {
                Plugin.Log?.LogInfo($"NativeFists: aim rejected for client {clientId}: non-finite or denormalised forward (magnitude={forward.magnitude:F3}).");
                return false;
            }
            Vector3 expectedOrigin = ServerAimOrigin(player);
            Vector3 expectedForward = player.gameplayCamera != null ? player.gameplayCamera.transform.forward : player.transform.forward;
            float originError = Vector3.Distance(origin, expectedOrigin);
            float tolerance = AimOriginToleranceMeters(clientId);
            if (originError > tolerance)
            {
                // F-FIST-5: an over-tolerance origin is the one rejection a normal, laggy player can
                // trip, and it costs them a punch they already saw and heard. Log the measured error
                // against the tolerance actually applied so a run's log says whether the clamp or a
                // real desync is at fault.
                Plugin.Log?.LogInfo($"NativeFists: aim rejected for client {clientId}: origin error {originError:F2} m > {tolerance:F2} m tolerance.");
                return false;
            }
            float facingDot = Vector3.Dot(forward.normalized, expectedForward.normalized);
            if (facingDot < MinAimForwardDot)
            {
                Plugin.Log?.LogInfo($"NativeFists: aim rejected for client {clientId}: facing dot {facingDot:F2} < {MinAimForwardDot:F2}.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// F-FIST-5: the static clamp plus however far a sprinting player travels in one RTT.
        /// NetworkTransport.GetCurrentRtt is a cached counter on the transport, so this is a field
        /// read rather than a measurement; the host's own loopback reports 0 and keeps the base value.
        /// </summary>
        private static float AimOriginToleranceMeters(ulong clientId)
        {
            float tolerance = MaxAimOriginErrorMeters;
            NetworkManager network = NetworkManager.Singleton;
            NetworkTransport transport = network?.NetworkConfig?.NetworkTransport;
            if (transport == null || clientId == NetworkManager.ServerClientId)
                return tolerance;
            try
            {
                float rttSeconds = transport.GetCurrentRtt(clientId) / 1000f;
                if (rttSeconds > 0f && rttSeconds < 2f)
                    tolerance += Mathf.Min(rttSeconds * AimOriginRttSpeedMetersPerSecond, MaxAimOriginRttToleranceMeters);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogDebug("NativeFists: RTT unavailable for aim tolerance: " + e.Message);
            }
            return tolerance;
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
            PulseLocalHitConfirmation(attacker, kind, audioSelection);
        }

        /// <summary>
        /// F-FIST-13: fists resolve on the server through HitEnemy/HitEnemyClientRpc, so they never
        /// reach EnemyAI.HitEnemyOnLocalClient and never fire the reticle hit marker every other
        /// damage source in the mod gets. Punching therefore read as weaker confirmation than any
        /// weapon. Pulse the vanilla hit clip through the HUD's own UIAudio for the attacker only:
        /// it is a non-positional, unmistakably local "that connected" cue, and it needs no shipped
        /// asset and no BetterArmory dependency.
        /// </summary>
        private static void PulseLocalHitConfirmation(PlayerControllerB attacker, NativeFistHitKind kind, byte audioSelection)
        {
            if (kind != NativeFistHitKind.Enemy && kind != NativeFistHitKind.ClingingEnemy)
                return;
            if (attacker == null || attacker != GameNetworkManager.Instance?.localPlayerController)
                return;
            AudioSource uiAudio = HUDManager.Instance?.UIAudio;
            if (uiAudio == null || vanillaHitClips == null || audioSelection >= vanillaHitClips.Length)
                return;
            AudioClip clip = vanillaHitClips[audioSelection];
            if (clip != null)
                uiAudio.PlayOneShot(clip, 0.5f);
        }

        private static void ResolveVanillaShovelAudio()
        {
            if (vanillaAudioResolved)
                return;
            // F-FIST-15: only latch on success. The flag used to be set before the lookup, so a null
            // item list or an item-list-replacing modpack with no Shovel prefab left both clips null
            // for the rest of the lifecycle window - a totally silent punch, with no retry and no
            // log, indistinguishable from a presentation failure.
            AllItemsList itemList = StartOfRound.Instance?.allItemsList;
            if (itemList?.itemsList != null)
            {
                for (int i = 0; i < itemList.itemsList.Count; i++)
                {
                    Item item = itemList.itemsList[i];
                    Shovel shovel = item?.spawnPrefab != null ? item.spawnPrefab.GetComponent<Shovel>() : null;
                    if (shovel == null)
                        continue;
                    vanillaSwingClip = shovel.swing;
                    vanillaHitClips = shovel.hitSFX;
                    vanillaAudioResolved = true;
                    return;
                }
            }
            if (vanillaAudioFailureLogged)
                return;
            vanillaAudioFailureLogged = true;
            Plugin.Log?.LogWarning(
                "NativeFists: no vanilla Shovel prefab in allItemsList; swing/hit audio is unavailable. " +
                "Retrying on the next punch.");
        }
    }
}
