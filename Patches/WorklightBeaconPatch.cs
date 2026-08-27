using System;
using System.Collections;
using System.IO;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZInteractions.InteractionAnimationApi;
using Y4NGZUpgrades.HUD;
using Y4NGZUpgrades.Interactive;
using Y4NGZUpgrades.Interactive.Hud;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class WorklightBeaconPatch
    {
        private const string MSG_THROW_SYNC = "WorklightBeacon_Throw";
        private const string MSG_PICKUP_SYNC = "WorklightBeacon_Pickup";
        private const string HUD_KEY = "worklight_beacon";
        private const string PROMPT_KEY = "worklight_beacon";
        // Baked 30 FPS throw: right-hand camera-depth maximum is frame 8.
        private const float THROW_RELEASE_DELAY = 8f / 30f;
        private const string PackId = "y4ngz.upgrades.worklight_beacon";
        private const string InteractionId = "y4ngz.worklight_beacon.throw";
        private const string ManifestFileName = "y4ngz-interactions-worklightbeacon-livebody.manifest.json";

        private const string THROW_ANIMATION = "PullGrenadePin";
        private const string THROW_ANIMATION_FULL_PATH = "UpperBodyEmotes.PullGrenadePin";
        private const int UPPER_BODY_EMOTES_LAYER = 3;
        private const float UPPER_BODY_SECONDS = 1.16f;
        private const string STOW_REASON = "worklight-beacon-throw";
        // Backstop for a coroutine that dies with its player mid-throw.
        private const float STOW_TIMEOUT_SECONDS = 4f;

        private static readonly int ThrowAnimationHash = Animator.StringToHash(THROW_ANIMATION);
        private static readonly int ThrowAnimationFullPathHash = Animator.StringToHash(THROW_ANIMATION_FULL_PATH);

        private static int _chargesRemaining;
        private static float _cooldownTimer;
        private static bool _handlersRegistered;
        private static bool _roundChargeGranted;
        private static bool _packRegistered;
        private static bool _missingManifestLogged;
        private static float _nextRegistrationAttemptAt;
        private static uint _nextBeaconId = 1u;

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            int maxCharges = WorklightBeaconUpgrade.GetMaxCharges();
            _chargesRemaining = maxCharges;
            _roundChargeGranted = maxCharges > 0;
            _cooldownTimer = 0f;
            _nextBeaconId = 1u;
            UpgradeHUDManager.DestroyHUDElement(HUD_KEY);
            EnsurePackRegistered();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            WorklightBeaconProjectile.DestroyAllActive();
            UpgradeHUDManager.DestroyHUDElement(HUD_KEY);
            Y4ngzPromptOverlay.ClearPostPlayerMenuPrompt(PROMPT_KEY);
            _chargesRemaining = 0;
            _roundChargeGranted = false;
            _cooldownTimer = 0f;
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            // Netcode nulls the CustomMessagingManager on shutdown and builds a fresh one for the
            // next host/join; without dropping this latch the second lobby never receives beacons.
            _handlersRegistered = false;
            _packRegistered = false;
            _missingManifestLogged = false;
            _nextRegistrationAttemptAt = 0f;
            Y4ngzPromptOverlay.ClearPostPlayerMenuPrompt(PROMPT_KEY);
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
            if (__instance == null) return;
            if (!__instance.IsOwner || !__instance.isPlayerControlled) return;
            if (__instance != GameNetworkManager.Instance?.localPlayerController) return;

            if (_cooldownTimer > 0f)
                _cooldownTimer = Mathf.Max(0f, _cooldownTimer - Time.deltaTime);

            UpdatePrompt(__instance);

            if (__instance.isPlayerDead) return;
            if (__instance.quickMenuManager != null && __instance.quickMenuManager.isMenuOpen) return;
            if (__instance.isTypingChat) return;
            if (__instance.inTerminalMenu) return;
            if (__instance.inSpecialInteractAnimation) return;

            if (Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.WorklightBeacon))
                TryThrow(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Interact_performed")]
        [HarmonyPrefix]
        private static bool PreInteractPerformed(PlayerControllerB __instance)
        {
            if (__instance == null
                || __instance != GameNetworkManager.Instance?.localPlayerController
                || !WorklightBeaconProjectile.TryGetAimedBeacon(__instance, out WorklightBeaconProjectile beacon))
            {
                return true;
            }

            TryPickupBeacon(beacon, __instance);
            return false;
        }

        private static void TryThrow(PlayerControllerB player)
        {
            if (StartOfRound.Instance == null) return;
            if (!WorklightBeaconUpgrade.IsUnlocked()) return;
            if (player.isInHangarShipRoom) return;
            if (StartOfRound.Instance.inShipPhase) return;
            if (_cooldownTimer > 0f) return;

            int max = WorklightBeaconUpgrade.GetMaxCharges();
            EnsureRoundChargeAvailable(max);
            if (_chargesRemaining <= 0) return;

            _chargesRemaining--;
            _cooldownTimer = WorklightBeaconUpgrade.COOLDOWN_SECONDS;

            PlayThrowAnimation(player);

            Vector3 spawnPosition = player.gameplayCamera != null
                ? player.gameplayCamera.transform.position + player.gameplayCamera.transform.forward * 0.4f
                : player.transform.position + player.transform.forward * 0.4f + Vector3.up * 1.2f;
            Vector3 throwDirection = player.gameplayCamera != null
                ? player.gameplayCamera.transform.forward
                : player.transform.forward;

            ulong playerNetObjId = player.NetworkObjectId;
            uint beaconId = _nextBeaconId++;
            int tier = WorklightBeaconUpgrade.GetTier();
            // Spawn locally first: the charge is already spent, so a failure inside the send path
            // must not be able to swallow the beacon the thrower paid for.
            player.StartCoroutine(DelayedSpawn(playerNetObjId, beaconId, tier, spawnPosition, throwDirection, isLocalThrower: true));
            BroadcastThrow(playerNetObjId, beaconId, tier, spawnPosition, throwDirection);
        }

#pragma warning disable Harmony003
        private const int THROW_PAYLOAD_BYTES = sizeof(ulong) + sizeof(uint) + sizeof(int) + sizeof(float) * 6;
        private const int PICKUP_PAYLOAD_BYTES = sizeof(ulong) + sizeof(uint);

        private static void WriteThrow(FastBufferWriter writer, ulong playerNetObjId, uint beaconId, int tier, Vector3 position, Vector3 direction)
        {
            writer.WriteValueSafe(playerNetObjId);
            writer.WriteValueSafe(beaconId);
            writer.WriteValueSafe(tier);
            writer.WriteValueSafe(position.x);
            writer.WriteValueSafe(position.y);
            writer.WriteValueSafe(position.z);
            writer.WriteValueSafe(direction.x);
            writer.WriteValueSafe(direction.y);
            writer.WriteValueSafe(direction.z);
        }

        /// <summary>
        /// Netcode has no client-to-all primitive - SendNamedMessageToAll reads ConnectedClientsIds,
        /// whose getter throws off-host. The host broadcasts; a client sends to the host, which
        /// relays. Send failures are logged, never thrown: the caller has already spent a charge.
        /// </summary>
        private static void BroadcastThrow(ulong playerNetObjId, uint beaconId, int tier, Vector3 position, Vector3 direction)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            FastBufferWriter writer = new FastBufferWriter(THROW_PAYLOAD_BYTES, Allocator.Temp);
            try
            {
                WriteThrow(writer, playerNetObjId, beaconId, tier, position, direction);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_THROW_SYNC, writer);
                else
                    messaging.SendNamedMessage(MSG_THROW_SYNC, NetworkManager.ServerClientId, writer);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[WorklightBeacon] throw send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void RelayThrow(ulong excludeClientId, ulong playerNetObjId, uint beaconId, int tier, Vector3 position, Vector3 direction)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(THROW_PAYLOAD_BYTES, Allocator.Temp);
                try
                {
                    WriteThrow(writer, playerNetObjId, beaconId, tier, position, direction);
                    messaging.SendNamedMessage(MSG_THROW_SYNC, clientId, writer);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogWarning($"[WorklightBeacon] throw relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        private static PlayerControllerB ResolvePlayerByActualClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].actualClientId == clientId)
                    return players[i];
            }

            return null;
        }

        private static void OnReceiveThrowEvent(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                ulong playerNetObjId;
                uint beaconId;
                int tier;
                float px, py, pz, dx, dy, dz;
                reader.ReadValueSafe(out playerNetObjId);
                reader.ReadValueSafe(out beaconId);
                reader.ReadValueSafe(out tier);
                reader.ReadValueSafe(out px);
                reader.ReadValueSafe(out py);
                reader.ReadValueSafe(out pz);
                reader.ReadValueSafe(out dx);
                reader.ReadValueSafe(out dy);
                reader.ReadValueSafe(out dz);

                NetworkManager network = NetworkManager.Singleton;
                if (network != null && network.IsServer && senderClientId != network.LocalClientId)
                {
                    // The thrower id in the payload is attacker-controlled. The host replaces it
                    // with the player behind the connection the message arrived on, so a client
                    // can only ever throw a beacon as itself.
                    PlayerControllerB sender = ResolvePlayerByActualClientId(senderClientId);
                    if (sender == null)
                        return;

                    playerNetObjId = sender.NetworkObjectId;
                    RelayThrow(
                        senderClientId,
                        playerNetObjId,
                        beaconId,
                        tier,
                        new Vector3(px, py, pz),
                        new Vector3(dx, dy, dz));
                }

                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
                if (local != null && local.NetworkObjectId == playerNetObjId)
                    return;

                MonoBehaviour host = HUDManager.Instance != null
                    ? HUDManager.Instance
                    : UnityEngine.Object.FindObjectOfType<StartOfRound>();
                if (host == null)
                {
                    SpawnRemoteAnimationAndBeacon(playerNetObjId, beaconId, tier, new Vector3(px, py, pz), new Vector3(dx, dy, dz));
                    return;
                }

                host.StartCoroutine(DelayedSpawn(
                    playerNetObjId,
                    beaconId,
                    tier,
                    new Vector3(px, py, pz),
                    new Vector3(dx, dy, dz),
                    isLocalThrower: false));
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[WorklightBeacon] malformed throw sync: {ex.Message}");
            }
        }
#pragma warning restore Harmony003

        private static IEnumerator DelayedSpawn(ulong playerNetObjId, uint beaconId, int tier, Vector3 position, Vector3 direction, bool isLocalThrower)
        {
            if (!isLocalThrower)
                TriggerRemoteThrowAnimation(playerNetObjId);
            yield return new WaitForSeconds(THROW_RELEASE_DELAY);
            ResolveReleasePose(playerNetObjId, ref position, ref direction);
            WorklightBeaconProjectile.Spawn(position, direction, WorklightBeaconUpgrade.THROW_FORCE, playerNetObjId, beaconId, tier);
        }

        private static void ResolveReleasePose(ulong playerNetObjId, ref Vector3 position, ref Vector3 direction)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || player.NetworkObjectId != playerNetObjId)
                    continue;

                bool isLocal = player == GameNetworkManager.Instance?.localPlayerController;
                Transform searchRoot = isLocal && player.playerModelArmsMetarig != null
                    ? player.playerModelArmsMetarig
                    : player.playerBodyAnimator?.transform;
                Transform hand = FindChildRecursive(searchRoot, "hand.R");
                direction = isLocal && player.gameplayCamera != null
                    ? player.gameplayCamera.transform.forward
                    : player.transform.forward;
                if (hand != null)
                    position = hand.position + direction.normalized * 0.12f;
                else if (isLocal && player.gameplayCamera != null)
                    position = player.gameplayCamera.transform.position + direction.normalized * 0.35f;
                else
                    position = player.transform.position + Vector3.up * 1.2f + direction.normalized * 0.25f;
                return;
            }
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName))
                return null;
            if (string.Equals(root.name, childName, StringComparison.OrdinalIgnoreCase))
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), childName);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static void SpawnRemoteAnimationAndBeacon(ulong playerNetObjId, uint beaconId, int tier, Vector3 position, Vector3 direction)
        {
            TriggerRemoteThrowAnimation(playerNetObjId);
            WorklightBeaconProjectile.Spawn(position, direction, WorklightBeaconUpgrade.THROW_FORCE, playerNetObjId, beaconId, tier);
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered) return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_THROW_SYNC, OnReceiveThrowEvent);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_PICKUP_SYNC, OnReceivePickupEvent);
                _handlersRegistered = true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[WorklightBeacon] RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

        private static void UpdatePrompt(PlayerControllerB player)
        {
            if (!WorklightBeaconUpgrade.IsUnlocked()
                || player == null
                || player.isPlayerDead
                || player.isTypingChat
                || player.inTerminalMenu
                || player.isInHangarShipRoom
                || (StartOfRound.Instance != null && StartOfRound.Instance.inShipPhase)
                || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen))
            {
                Y4ngzPromptOverlay.ClearPostPlayerMenuPrompt(PROMPT_KEY);
                return;
            }

            int max = WorklightBeaconUpgrade.GetMaxCharges();
            EnsureRoundChargeAvailable(max);

            string bindingPath = Gui.UpgradeInput.EffectivePath(Gui.Plugin.Keybinds?.WorklightBeacon);
            if (_cachedPromptText == null
                || !string.Equals(_cachedPromptPath, bindingPath, StringComparison.Ordinal)
                || _cachedPromptCharges != _chargesRemaining
                || _cachedPromptMax != max)
            {
                _cachedPromptPath = bindingPath;
                _cachedPromptCharges = _chargesRemaining;
                _cachedPromptMax = max;
                string key = Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.WorklightBeacon, "V");
                _cachedPromptText = $"Worklights: [{key}] {_chargesRemaining}/{max}";
            }

            Y4ngzPromptOverlay.SetPostPlayerMenuPrompt(PROMPT_KEY, _cachedPromptText);
        }

        private static string _cachedPromptText;
        private static string _cachedPromptPath;
        private static int _cachedPromptCharges = -1;
        private static int _cachedPromptMax = -1;

        internal static void TryPickupBeacon(WorklightBeaconProjectile beacon, PlayerControllerB player)
        {
            if (beacon == null || player == null)
                return;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local != null && player != local)
                return;
            if (!WorklightBeaconUpgrade.IsUnlocked())
                return;

            int max = WorklightBeaconUpgrade.GetMaxCharges();
            EnsureRoundChargeAvailable(max);
            if (!RefundChargeFromPickup(max))
            {
                HUDManager.Instance?.DisplayTip("WORKLIGHT BEACON", "Beacon harness is full.", isWarning: true);
                return;
            }

            // Destroy locally first: the charge has already been refunded, so a failure inside the
            // send path must not leave the beacon standing on the picker's own machine.
            ulong throwerNetObjId = beacon.ThrowerNetObjId;
            uint pickedBeaconId = beacon.BeaconId;
            WorklightBeaconProjectile.DestroySynced(throwerNetObjId, pickedBeaconId);
            BroadcastPickup(throwerNetObjId, pickedBeaconId);
        }

        private static bool RefundChargeFromPickup(int maxCharges)
        {
            if (maxCharges <= 0 || _chargesRemaining >= maxCharges)
                return false;

            _chargesRemaining = Mathf.Min(maxCharges, _chargesRemaining + 1);
            return true;
        }

        private static void BroadcastPickup(ulong playerNetObjId, uint beaconId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            FastBufferWriter writer = new FastBufferWriter(PICKUP_PAYLOAD_BYTES, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(playerNetObjId);
                writer.WriteValueSafe(beaconId);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_PICKUP_SYNC, writer);
                else
                    messaging.SendNamedMessage(MSG_PICKUP_SYNC, NetworkManager.ServerClientId, writer);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[WorklightBeacon] pickup send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void RelayPickup(ulong excludeClientId, ulong playerNetObjId, uint beaconId)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(PICKUP_PAYLOAD_BYTES, Allocator.Temp);
                try
                {
                    writer.WriteValueSafe(playerNetObjId);
                    writer.WriteValueSafe(beaconId);
                    messaging.SendNamedMessage(MSG_PICKUP_SYNC, clientId, writer);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogWarning($"[WorklightBeacon] pickup relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

#pragma warning disable Harmony003
        private static void OnReceivePickupEvent(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                ulong playerNetObjId;
                uint beaconId;
                reader.ReadValueSafe(out playerNetObjId);
                reader.ReadValueSafe(out beaconId);

                // The payload names a beacon, not a claimed identity, so there is nothing to
                // re-derive from the sender here; the host just fans the removal out so the
                // beacon disappears on every machine rather than only the picker's.
                NetworkManager network = NetworkManager.Singleton;
                if (network != null && network.IsServer && senderClientId != network.LocalClientId)
                    RelayPickup(senderClientId, playerNetObjId, beaconId);

                WorklightBeaconProjectile.DestroySynced(playerNetObjId, beaconId);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[WorklightBeacon] malformed pickup sync: {ex.Message}");
            }
        }
#pragma warning restore Harmony003

        private static void EnsureRoundChargeAvailable(int maxCharges)
        {
            if (_roundChargeGranted)
                return;
            if (maxCharges <= 0)
                return;

            _chargesRemaining = maxCharges;
            _roundChargeGranted = true;
        }

        private static void TriggerRemoteThrowAnimation(ulong playerNetObjId)
        {
            if (StartOfRound.Instance == null) return;
            PlayerControllerB[] players = StartOfRound.Instance.allPlayerScripts;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || player.NetworkObjectId != playerNetObjId)
                    continue;

                PlayThrowAnimation(player);
                return;
            }
        }

        private static void PlayThrowAnimation(PlayerControllerB player)
        {
            // Stow first: nulling currentlyHeldObjectServer is what releases a weapon's
            // third-person BodyWorld session before this throw claims the body.
            StowHeldItemForThrow(player);

            if (TryPlayInteractionThrow(player, out string interactionReason))
                return;

            // A direct animator owner (for example Escape Artist) must remain untouched. The
            // gameplay throw can still happen, but firing a vanilla trigger/state into that
            // owner's custom controller would recreate the same overlapping-owner corruption.
            if (string.Equals(
                    interactionReason,
                    "player_animator_owned_externally",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Keep the existing readable world-body animation as a fallback until the
            // independently packaged retargeted interaction is present and valid.
            if (player == null || player.playerBodyAnimator == null)
                return;

            try
            {
                player.doingUpperBodyEmote = Mathf.Max(player.doingUpperBodyEmote, UPPER_BODY_SECONDS);
                player.playerBodyAnimator.SetTrigger(THROW_ANIMATION);
                TryPlayThrowState(player.playerBodyAnimator);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[WorklightBeacon] throw animation failed: {ex.Message}");
            }
        }

        // Shared by the local throw and the remote presentation, so observers see the same
        // put-away/re-equip the thrower does.
        private static void StowHeldItemForThrow(PlayerControllerB player)
        {
            if (player == null)
                return;

            HeldItemStowHandle stow = HeldItemStowService.Begin(
                player,
                STOW_REASON,
                STOW_TIMEOUT_SECONDS,
                force: true);
            if (!stow.IsActive)
                return;

            try
            {
                player.StartCoroutine(ReleaseThrowStow(stow));
            }
            catch (System.Exception ex)
            {
                stow.Dispose();
                Plugin.Log?.LogWarning($"[WorklightBeacon] throw stow release could not start: {ex.Message}");
            }
        }

        // Harmony003 reads the handle parameter as a patch argument; it is a plain coroutine
        // argument and Dispose only releases the refcount.
#pragma warning disable Harmony003
        private static IEnumerator ReleaseThrowStow(HeldItemStowHandle stow)
        {
            yield return new WaitForSeconds(UPPER_BODY_SECONDS);
            stow.Dispose();
        }
#pragma warning restore Harmony003

        private static bool TryPlayInteractionThrow(PlayerControllerB player, out string reason)
        {
            reason = string.Empty;
            if (player == null)
            {
                reason = "player_missing";
                return false;
            }

            if (player != GameNetworkManager.Instance?.localPlayerController)
            {
                // The custom pack is first-person-arms only. Remote observers use the vanilla
                // world-body throw, but first release any API controller through its owner.
                if (LCInteractionAnimationAPI.TryGetActiveInteraction(
                        player,
                        InteractionAnimationPresentationKind.BodyWorld,
                        out InteractionAnimationHandle activeBodyHandle))
                {
                    LCInteractionAnimationAPI.TryStopInteraction(
                        activeBodyHandle,
                        InteractionAnimationStopReason.Interrupted);
                }
                RuntimeAnimatorController expectedRemote = StartOfRound.Instance?.otherClientsAnimatorController;
                if (LCInteractionAnimationAPI.TryGetActiveInteraction(
                        player,
                        InteractionAnimationPresentationKind.BodyWorld,
                        out _) ||
                    (expectedRemote != null &&
                     player.playerBodyAnimator?.runtimeAnimatorController != expectedRemote))
                {
                    reason = "player_animator_owned_externally";
                    return false;
                }

                reason = "remote_uses_vanilla_throw";
                return false;
            }

            if (!EnsurePackRegistered())
            {
                reason = "interaction_pack_unavailable";
                return false;
            }

            var request = new InteractionAnimationRequest
            {
                Player = player,
                PackId = PackId,
                InteractionId = InteractionId,
            };

            if (LCInteractionAnimationAPI.TryStartInteraction(request, out InteractionAnimationHandle _, out reason))
                return true;

            Plugin.Log?.LogWarning("[WorklightBeacon] retargeted throw animation failed: " + reason);
            return false;
        }

        private static bool EnsurePackRegistered()
        {
            if (_packRegistered)
                return true;
            if (Time.realtimeSinceStartup < _nextRegistrationAttemptAt)
                return false;

            _nextRegistrationAttemptAt = Time.realtimeSinceStartup + 2f;
            string pluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            string manifestPath = Path.Combine(pluginDirectory ?? string.Empty, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                if (!_missingManifestLogged)
                {
                    _missingManifestLogged = true;
                    Plugin.Log?.LogWarning("[WorklightBeacon] retargeted throw manifest not found: " + manifestPath);
                }
                return false;
            }

            string manifestJson;
            try
            {
                manifestJson = File.ReadAllText(manifestPath);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[WorklightBeacon] retargeted throw manifest read failed: " + ex.Message);
                return false;
            }

            var pack = new InteractionAnimationPackDefinition
            {
                PackId = PackId,
                Version = Plugin.Version,
                AssetRootPath = pluginDirectory ?? string.Empty,
                Interactions = new[]
                {
                    new InteractionAnimationDefinition
                    {
                        InteractionId = InteractionId,
                        PresentationKind = InteractionAnimationPresentationKind.BodyWorld,
                        ManifestJson = manifestJson
                    }
                }
            };

            if (!LCInteractionAnimationAPI.TryRegisterInteractionPack(pack, out string reason))
            {
                if (string.Equals(reason, "pack_already_registered", StringComparison.OrdinalIgnoreCase))
                {
                    _packRegistered = true;
                    return true;
                }

                Plugin.Log?.LogWarning("[WorklightBeacon] retargeted throw pack registration failed: " + reason);
                return false;
            }

            _packRegistered = true;
            _missingManifestLogged = false;
            Plugin.Log?.LogInfo("[WorklightBeacon] registered retargeted throw animation pack.");
            return true;
        }

        private static void TryPlayThrowState(Animator animator)
        {
            if (animator == null || animator.layerCount <= UPPER_BODY_EMOTES_LAYER)
                return;

            if (animator.HasState(UPPER_BODY_EMOTES_LAYER, ThrowAnimationFullPathHash))
            {
                animator.Play(ThrowAnimationFullPathHash, UPPER_BODY_EMOTES_LAYER, 0f);
                return;
            }

            if (animator.HasState(UPPER_BODY_EMOTES_LAYER, ThrowAnimationHash))
                animator.Play(ThrowAnimationHash, UPPER_BODY_EMOTES_LAYER, 0f);
        }
    }
}
