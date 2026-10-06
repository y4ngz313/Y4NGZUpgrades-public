using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Y4NGZUpgrades.Interactive.Hud;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class TurretHackerPatch
    {
        private const string MSG_TURRET_HACK = "TechnicianTurretHack";
        private const string DoorHackPromptKey = "FieldMechanic.DoorHack";
        private const string TurretHackPromptKey = "FieldMechanic.TurretHack";
        // Longest hack any tier can legitimately request (see TurretHackerUpgrade.GetDuration).
        private const float MAX_HACK_DURATION_SECONDS = 90f;

        // Turret NetworkObjectId -> Time.time the disable expires at.
        private static readonly Dictionary<ulong, float> HackedTurrets = new Dictionary<ulong, float>();
        // F-TECH-6: senderClientId -> Time.time the host will accept that sender's next hack.
        // The client-side _cooldownEnd below is presentation only and is not enforceable.
        private static readonly Dictionary<ulong, float> HostCooldownEnd = new Dictionary<ulong, float>();
        private static bool _handlersRegistered;
        private static float _cooldownEnd;
        private static float _doorHackHold;
        private static float _turretHackHold;

        // Cached prompt strings: the tick methods run every frame, so the interpolated label is
        // rebuilt only when the bound Interact key actually changes.
        private static string _promptInteractLabel;
        private static string _turretPromptText;
        private static string _doorPromptText;
        private static float _nextPromptRefresh;
        private static int _cooldownPromptSeconds = -1;
        private static string _cooldownPromptText;

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        /// <summary>
        /// Netcode nulls the CustomMessagingManager on shutdown and builds a fresh one for the next
        /// host/join, so this latch has to drop or the second lobby of a session never receives
        /// turret hacks.
        /// </summary>
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _handlersRegistered = false;
            HackedTurrets.Clear();
            HostCooldownEnd.Clear();
            _doorHackHold = 0f;
            _turretHackHold = 0f;
            ClearDoorHackPrompt();
            ClearTurretHackPrompt();
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            HackedTurrets.Clear();
            HostCooldownEnd.Clear();
            _cooldownEnd = 0f;
            _doorHackHold = 0f;
            _turretHackHold = 0f;
            ClearDoorHackPrompt();
            ClearTurretHackPrompt();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            HackedTurrets.Clear();
            HostCooldownEnd.Clear();
            _doorHackHold = 0f;
            _turretHackHold = 0f;
            ClearDoorHackPrompt();
            ClearTurretHackPrompt();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            TickDoorHack(__instance);
            TickTurretHack(__instance);
        }

        [HarmonyPatch(typeof(Turret), "Update")]
        [HarmonyPrefix]
        private static bool PreTurretUpdate(Turret __instance)
        {
            if (__instance == null)
                return true;

            if (!HackedTurrets.TryGetValue(__instance.NetworkObjectId, out float expiresAt))
                return true;

            if (Time.time >= expiresAt)
            {
                HackedTurrets.Remove(__instance.NetworkObjectId);
                return true;
            }

            UpdateHackedTurret(__instance);
            return false;
        }

        /// <summary>
        /// Completes a tablet splice through the same networked disable path used by the nearby
        /// interaction. The tablet owns its own progress channel; this method applies the result.
        /// </summary>
        internal static bool TryHackFromTablet(Turret turret)
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (turret == null
                || player == null
                || player.isPlayerDead
                || player.isTypingChat
                || !FieldOperationsUpgrade.IsUnlocked()
                || !TurretHackerUpgrade.CanHackTurrets())
            {
                return false;
            }

            return TryApplyHack(turret);
        }

        private static void TickTurretHack(PlayerControllerB player)
        {
            if (!TurretHackerUpgrade.CanHackTurrets()
                || !CanUse(player)
                || player.inTerminalMenu
                || FieldOperationsTabletPatch.IsTabletActive)
            {
                _turretHackHold = 0f;
                ClearTurretHackPrompt();
                return;
            }

            Turret turret = FindLookedAtTurret(player);
            if (turret == null)
            {
                _turretHackHold = 0f;
                ClearTurretHackPrompt();
                return;
            }

            // F-TECH-12: the prompt used to disappear during the advertised 20 s cooldown, so a
            // player looking at a second turret saw exactly what they would see without the
            // upgrade. Keep it up with a countdown instead.
            if (Time.time < _cooldownEnd)
            {
                _turretHackHold = 0f;
                // Cached per whole second: this is an Update path.
                int remaining = Mathf.CeilToInt(_cooldownEnd - Time.time);
                if (remaining != _cooldownPromptSeconds || _cooldownPromptText == null)
                {
                    _cooldownPromptSeconds = remaining;
                    _cooldownPromptText = $"Turret splice recharging: {remaining}s";
                }

                Y4ngzPromptOverlay.SetPersistentPrompt(TurretHackPromptKey, _cooldownPromptText);
                Y4ngzPromptOverlay.SetPersistentPromptProgress(TurretHackPromptKey, 0f);
                return;
            }

            Y4ngzPromptOverlay.SetPersistentPrompt(TurretHackPromptKey, ResolveTurretPromptText());

            if (UpgradeInteractInput.IsHeld())
            {
                _turretHackHold += Time.deltaTime;
                if (_turretHackHold >= TurretHackerUpgrade.HACK_HOLD_SECONDS)
                {
                    _turretHackHold = 0f;
                    if (TryApplyHack(turret))
                        EmployeeStatistics.RecordDeviceHacked(turret);
                    ClearTurretHackPrompt();
                    return;
                }
            }
            else
            {
                _turretHackHold = Mathf.MoveTowards(
                    _turretHackHold,
                    0f,
                    Time.deltaTime * 2f);
            }

            // F-TECH-2: the hold was completely silent before - a text prompt and nothing in
            // between it and the 1.2 s completion.
            Y4ngzPromptOverlay.SetPersistentPromptProgress(
                TurretHackPromptKey,
                _turretHackHold / TurretHackerUpgrade.HACK_HOLD_SECONDS);
        }

        /// <summary>
        /// Rebuilds the two hold-hack prompt strings only when the bound Interact key changes;
        /// these are read from Update postfixes.
        /// </summary>
        private static void RefreshPromptStrings()
        {
            // DisplayLabel() itself allocates (InputAction.GetBindingDisplayString), and a
            // rebind cannot happen mid-hold, so polling it twice a second is plenty.
            if (_turretPromptText != null && Time.unscaledTime < _nextPromptRefresh)
                return;

            _nextPromptRefresh = Time.unscaledTime + 0.5f;
            string interact = UpgradeInteractInput.DisplayLabel();
            if (string.Equals(interact, _promptInteractLabel, StringComparison.Ordinal)
                && _turretPromptText != null)
            {
                return;
            }

            _promptInteractLabel = interact;
            _turretPromptText = $"Disable turret: Hold [{interact}]";
            _doorPromptText = $"Bypass lock: Hold [{interact}]";
        }

        private static string ResolveTurretPromptText()
        {
            RefreshPromptStrings();
            return _turretPromptText;
        }

        private static string ResolveDoorPromptText()
        {
            RefreshPromptStrings();
            return _doorPromptText;
        }

        private static bool TryApplyHack(Turret turret)
        {
            if (turret == null || Time.time < _cooldownEnd)
                return false;

            float duration = TurretHackerUpgrade.GetDuration();
            ApplyHack(turret.NetworkObjectId, duration);
            BroadcastHack(turret.NetworkObjectId, duration);
            _cooldownEnd = Time.time + TurretHackerUpgrade.COOLDOWN_SECONDS;
            return true;
        }

        // F-TECH-14: one reused non-alloc buffer, and one raycast per frame shared by the door,
        // turret and camera probes. All three run from PlayerControllerB.Update postfixes at the
        // same grab distance, so firing three RaycastAlls (one of which also allocated a sort
        // comparison delegate every frame) was pure waste.
        private static readonly RaycastHit[] LookHits = new RaycastHit[16];
        private static int _lookHitCount;
        private static int _lookHitFrame = -1;
        private static float _lookHitRange = -1f;

        internal static float ResolveLookRange(PlayerControllerB player)
        {
            return player != null && player.grabDistance > 0f ? player.grabDistance : 4f;
        }

        internal static int LookRaycast(PlayerControllerB player, float range, out RaycastHit[] hits)
        {
            hits = LookHits;
            if (_lookHitFrame == Time.frameCount && _lookHitRange == range)
                return _lookHitCount;

            _lookHitFrame = Time.frameCount;
            _lookHitRange = range;
            _lookHitCount = 0;

            Camera camera = player != null && player.gameplayCamera != null
                ? player.gameplayCamera
                : Camera.main;
            if (camera == null)
                return 0;

            Ray ray = new Ray(camera.transform.position, camera.transform.forward);
            _lookHitCount = Physics.RaycastNonAlloc(ray, LookHits, range, ~0, QueryTriggerInteraction.Collide);
            return _lookHitCount;
        }

        private static Turret FindLookedAtTurret(PlayerControllerB player)
        {
            int hitCount = LookRaycast(player, ResolveLookRange(player), out RaycastHit[] hits);

            // RaycastNonAlloc results are unordered; take the nearest hit that resolves a Turret.
            Turret nearest = null;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                Turret turret = hits[i].collider != null
                    ? hits[i].collider.GetComponentInParent<Turret>()
                    : null;
                if (turret == null || hits[i].distance >= nearestDistance)
                    continue;

                nearest = turret;
                nearestDistance = hits[i].distance;
            }

            return nearest;
        }

        private static void ClearTurretHackPrompt()
        {
            Y4ngzPromptOverlay.ClearPersistentPrompt(TurretHackPromptKey);
        }

        private static void ApplyHack(ulong turretNetworkId, float duration)
        {
            float expiresAt;
            bool alreadyHacked = HackedTurrets.TryGetValue(turretNetworkId, out expiresAt)
                                 && Time.time < expiresAt;
            if (!alreadyHacked)
                expiresAt = 0f;

            HackedTurrets[turretNetworkId] = Mathf.Max(expiresAt, Time.time + duration);

            Turret turret = ResolveTurret(turretNetworkId);
            if (turret != null)
            {
                turret.targetPlayerWithRotation = null;
                turret.targetTransform = null;
                if (!alreadyHacked)
                    SilenceHackedTurret(turret);
            }
        }

        /// <summary>
        /// F-TECH-5: PreTurretUpdate skips Turret.Update outright, so vanilla's
        /// Firing -> Detection transition - the only place the firing audio and bullet particles
        /// are stopped - never runs. A turret hacked mid-burst kept a stream of tracers and
        /// looping gunfire pointed at the crew for the whole 90 s while dealing no damage.
        /// Replicate that transition once, when the turret is newly hacked.
        /// </summary>
        private static void SilenceHackedTurret(Turret turret)
        {
            if (turret.mainAudio != null)
                turret.mainAudio.Stop();
            if (turret.farAudio != null)
                turret.farAudio.Stop();
            if (turret.berserkAudio != null)
                turret.berserkAudio.Stop();
            if (turret.bulletCollisionAudio != null)
                turret.bulletCollisionAudio.Stop();
            if (turret.bulletParticles != null)
                turret.bulletParticles.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);

            // Leaving this at Firing is what made vanilla run the cleanup only once the hack
            // expired; Detection also matches the steady state UpdateHackedTurret holds.
            turret.turretModeLastFrame = TurretMode.Detection;
        }

        private static Turret ResolveTurret(ulong networkId)
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkObject netObject;
                if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkId, out netObject) && netObject != null)
                    return netObject.GetComponent<Turret>();
            }

            Turret[] turrets = UnityEngine.Object.FindObjectsOfType<Turret>();
            for (int i = 0; i < turrets.Length; i++)
            {
                if (turrets[i] != null && turrets[i].NetworkObjectId == networkId)
                    return turrets[i];
            }

            return null;
        }

        private static void UpdateHackedTurret(Turret turret)
        {
            if (!turret.turretActive)
                return;

            // Disable-only: hold the turret in idle detection with no target for the hack duration.
            turret.targetPlayerWithRotation = null;
            turret.targetTransform = null;
            turret.turretMode = TurretMode.Detection;
            if (turret.turretAnimator != null)
                turret.turretAnimator.SetInteger("TurretMode", 0);
        }

        // -----------------------------------------------------------------------
        // TIER 2 - LOCKED-DOOR HACK (vanilla DoorLock; hold-interact, no key)
        // -----------------------------------------------------------------------

        private static void TickDoorHack(PlayerControllerB player)
        {
            if (!TurretHackerUpgrade.CanHackDoors())
            {
                _doorHackHold = 0f;
                ClearDoorHackPrompt();
                return;
            }
            if (!CanUse(player) || player.inTerminalMenu)
            {
                _doorHackHold = 0f;
                ClearDoorHackPrompt();
                return;
            }

            DoorLock door = FindLookedAtLockedDoor(player);
            if (door == null)
            {
                _doorHackHold = 0f;
                ClearDoorHackPrompt();
                return;
            }

            Y4ngzPromptOverlay.SetPersistentPrompt(DoorHackPromptKey, ResolveDoorPromptText());

            if (UpgradeInteractInput.IsHeld())
            {
                _doorHackHold += Time.deltaTime;
                if (_doorHackHold >= TurretHackerUpgrade.HACK_HOLD_SECONDS)
                {
                    _doorHackHold = 0f;
                    // Mirrors the vanilla lockpicker finish: unlock locally, then
                    // UnlockDoorServerRpc relays the unlock to everyone else.
                    door.UnlockDoorSyncWithServer();
                    EmployeeStatistics.RecordDeviceHacked(door);
                    ClearDoorHackPrompt();
                    return;
                }
            }
            else
            {
                _doorHackHold = Mathf.MoveTowards(_doorHackHold, 0f, Time.deltaTime * 2f);
            }

            // F-TECH-2: visible hold progress for the door bypass.
            Y4ngzPromptOverlay.SetPersistentPromptProgress(
                DoorHackPromptKey,
                _doorHackHold / TurretHackerUpgrade.HACK_HOLD_SECONDS);
        }

        private static void ClearDoorHackPrompt()
        {
            Y4ngzPromptOverlay.ClearPersistentPrompt(DoorHackPromptKey);
        }

        private static DoorLock FindLookedAtLockedDoor(PlayerControllerB player)
        {
            int hitCount = LookRaycast(player, ResolveLookRange(player), out RaycastHit[] hits);

            // RaycastNonAlloc results are unordered; take the nearest hit that
            // resolves a DoorLock (matches the previous sorted first-hit logic).
            DoorLock nearestDoor = null;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                DoorLock door = hits[i].collider != null ? hits[i].collider.GetComponentInParent<DoorLock>() : null;
                if (door == null || hits[i].distance >= nearestDistance)
                    continue;

                nearestDistance = hits[i].distance;
                nearestDoor = door;
            }

            if (nearestDoor == null)
                return null;

            // Standard locked doors only; leave an in-progress lockpicker's flow alone.
            return nearestDoor.isLocked && !nearestDoor.isPickingLock ? nearestDoor : null;
        }

        private static bool CanUse(PlayerControllerB player)
        {
            if (player == null || player.isPlayerDead || player.isTypingChat)
                return false;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return false;
            if (player.inSpecialInteractAnimation)
                return false;
            return true;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered) return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_TURRET_HACK, OnReceiveHack);
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"TurretHackerPatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

#pragma warning disable Harmony003
        private const int HACK_PAYLOAD_BYTES = sizeof(ulong) + sizeof(float);

        /// <summary>
        /// Netcode has no client-to-all primitive - SendNamedMessageToAll reads ConnectedClientsIds,
        /// whose getter throws off-host. That threw a client's hack away before it ever reached the
        /// host, which is the machine that actually drives turret targeting. A client now sends to
        /// the host, which applies the hack and relays it to the rest of the lobby.
        /// </summary>
        private static void BroadcastHack(ulong turretNetworkId, float duration)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            FastBufferWriter writer = new FastBufferWriter(HACK_PAYLOAD_BYTES, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(turretNetworkId);
                writer.WriteValueSafe(duration);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_TURRET_HACK, writer);
                else
                    messaging.SendNamedMessage(MSG_TURRET_HACK, NetworkManager.ServerClientId, writer);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"TurretHackerPatch: hack send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void RelayHack(ulong excludeClientId, ulong turretNetworkId, float duration)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(HACK_PAYLOAD_BYTES, Allocator.Temp);
                try
                {
                    writer.WriteValueSafe(turretNetworkId);
                    writer.WriteValueSafe(duration);
                    messaging.SendNamedMessage(MSG_TURRET_HACK, clientId, writer);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"TurretHackerPatch: hack relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        private static void OnReceiveHack(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
                return;

            try
            {
                ulong turretNetworkId;
                float duration;
                reader.ReadValueSafe(out turretNetworkId);
                reader.ReadValueSafe(out duration);

                NetworkManager network = NetworkManager.Singleton;
                if (network != null && network.IsServer && senderClientId != network.LocalClientId)
                {
                    // The turret is addressed by network object id, so there is no player identity
                    // in the payload to spoof; the sender-controlled quantity is the duration, which
                    // the host clamps to the longest legitimate hack before trusting it.
                    if (!(duration > 0f) || ResolveTurret(turretNetworkId) == null)
                        return;

                    // F-TECH-6: entitlement and the advertised 20 s cooldown are now enforced on
                    // the host. _cooldownEnd on the sender is a client-side static, so a modded
                    // or stale client could otherwise disable every turret in the level at will.
                    //
                    // #435: entitlement is the sender's published CAPABILITY, not their rank.
                    // Full-native rank 2 cannot hack a turret while unique-only rank 2 can, so the
                    // rank cannot answer this, and the host must never substitute its own
                    // preference for the sender's. A peer that never publishes the index reads 0:
                    // unknown is not authorisation.
                    if (UpgradeTierSync.GetTier(senderClientId, TurretHackerUpgrade.TURRET_HACK_CAPABILITY_ID) < 1)
                        return;
                    if (HostCooldownEnd.TryGetValue(senderClientId, out float readyAt) && Time.time < readyAt)
                        return;

                    HostCooldownEnd[senderClientId] = Time.time + TurretHackerUpgrade.COOLDOWN_SECONDS;
                    duration = Mathf.Min(duration, MAX_HACK_DURATION_SECONDS);
                    RelayHack(senderClientId, turretNetworkId, duration);
                }

                ApplyHack(turretNetworkId, duration);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"TurretHackerPatch: malformed hack sync: {ex.Message}");
            }
        }
#pragma warning restore Harmony003
    }
}
