using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class DeathboundPatch
    {
        // F-TECH-1: the protection used to be purely local, so only the dying player's own
        // machine skipped the slot. Vanilla KillPlayerClientRpc runs DropAllHeldItems for the
        // same player on the host and on every peer, which cleared isHeld/heldByPlayerOnServer
        // on the host's copy and let RoundManager.DespawnPropsAtEndOfRound eat the item. The
        // dying owner now announces the protected item before KillPlayerServerRpc fires and
        // every machine skips that slot for that player, locality independent.
        private const string MSG_PROTECT_SERVER = "Y4NGZDeathbound.ProtectServerRpc";
        private const string MSG_PROTECT_CLIENT = "Y4NGZDeathbound.ProtectClientRpc";

        private sealed class ProtectedItemState
        {
            internal PlayerControllerB Player;
            internal GrabbableObject Item;
            internal int SlotIndex;
        }

        private static bool _localDeathDropActive;
        private static ProtectedItemState _pendingState;

        // clientId -> NetworkObjectId of the item this machine must not drop for that player.
        // Consumed by the one DropAllHeldItems the death triggers here.
        private static readonly Dictionary<ulong, ulong> ProtectedItems = new Dictionary<ulong, ulong>();

        private static bool _handlersRegistered;
        private static NetworkManager _registeredNetworkManager;

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPrefix]
        private static void PreKillPlayer(PlayerControllerB __instance)
        {
            if (!ShouldProtectPlayer(__instance))
                return;

            _localDeathDropActive = true;

            // Mirror vanilla's own entry guard: a KillPlayer that is going to no-op must not
            // publish a protection the rest of the lobby would then hold on to.
            if (!__instance.IsOwner || __instance.isPlayerDead || !__instance.AllowPlayerDeath())
                return;

            // KillPlayer sends KillPlayerServerRpc BEFORE it drops the owner's own items, so
            // announcing here puts this message ahead of the kill RPC on the same reliable
            // channel; the host and every peer therefore know about the protected item before
            // KillPlayerClientRpc asks them to drop it.
            AnnounceProtectedItem(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPostfix]
        private static void PostKillPlayer(PlayerControllerB __instance)
        {
            if (ShouldProtectPlayer(__instance))
                StartRestoreCoroutine(__instance, delaySeconds: 0.15f);

            _localDeathDropActive = false;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DropAllHeldItems")]
        [HarmonyPrefix]
        private static void PreDropAllHeldItems(PlayerControllerB __instance, out ProtectedItemState __state)
        {
            __state = null;
            if (__instance == null)
                return;

            bool localProtect = _localDeathDropActive && ShouldProtectPlayer(__instance);
            bool remoteProtect = ProtectedItems.TryGetValue(__instance.actualClientId, out ulong protectedItemId);
            if (!localProtect && !remoteProtect)
                return;

            // One announcement covers exactly one death, so the entry is spent either way -
            // including when the slot no longer holds the announced item and we bail below.
            if (remoteProtect)
                ProtectedItems.Remove(__instance.actualClientId);

            int slot = DeathboundUpgrade.PROTECTED_SLOT_INDEX;
            GrabbableObject item = GetProtectedItem(__instance, slot);
            if (item == null)
                return;

            if (!localProtect && ResolveNetworkObjectId(item) != protectedItemId)
                return;

            __state = new ProtectedItemState
            {
                Player = __instance,
                Item = item,
                SlotIndex = slot
            };

            // The restore-on-respawn path is the local player's alone; a remote copy is put
            // back by the postfix below and never needs the delayed coroutine.
            if (localProtect)
                _pendingState = __state;

            __instance.ItemSlots[slot] = null;
            if (__instance.currentlyHeldObjectServer == item)
            {
                __instance.currentlyHeldObjectServer = null;
                __instance.isHoldingObject = false;
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DropAllHeldItems")]
        [HarmonyPostfix]
        private static void PostDropAllHeldItems(ProtectedItemState __state)
        {
            RestoreProtectedItem(__state, equipIfSelected: false);
        }

        [HarmonyPatch(typeof(StartOfRound), "ReviveDeadPlayers")]
        [HarmonyPostfix]
        private static void PostReviveDeadPlayers()
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local != null)
                StartRestoreCoroutine(local, delaySeconds: 0.10f);

            // F-TECH-11: the protection is spent once the crew is revived. The coroutine above
            // captured the state it needs, so dropping the static here cannot strand a restore.
            _pendingState = null;
        }

        /// <summary>
        /// F-TECH-11: EndOfGame is an iterator, so this postfix runs at enumerator creation -
        /// long before the ReviveDeadPlayers call inside it. Only the per-round routing state
        /// is dropped here; <c>_pendingState</c> is cleared by PostReviveDeadPlayers.
        /// </summary>
        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            ProtectedItems.Clear();
            _localDeathDropActive = false;
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            // F-TECH-11: a player who dies and never gets revived used to leave a static
            // PlayerControllerB/GrabbableObject pair alive for the rest of the session.
            ProtectedItems.Clear();
            _localDeathDropActive = false;
            _pendingState = null;
            _handlersRegistered = false;
            _registeredNetworkManager = null;
        }

        private static bool ShouldProtectPlayer(PlayerControllerB player)
        {
            if (player == null || !DeathboundUpgrade.IsUnlocked())
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static GrabbableObject GetProtectedItem(PlayerControllerB player, int slot)
        {
            if (player == null || player.ItemSlots == null || slot < 0 || slot >= player.ItemSlots.Length)
                return null;

            return player.ItemSlots[slot];
        }

        private static void StartRestoreCoroutine(PlayerControllerB player, float delaySeconds)
        {
            ProtectedItemState state = _pendingState;
            if (state == null || state.Player != player)
                return;

            if (GameNetworkManager.Instance != null)
                GameNetworkManager.Instance.StartCoroutine(RestoreAfterDelay(player, state, delaySeconds));
        }

        private static IEnumerator RestoreAfterDelay(
            PlayerControllerB player,
            ProtectedItemState state,
            float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);
            RestoreProtectedItem(state, equipIfSelected: player != null && !player.isPlayerDead);
        }

        private static void RestoreProtectedItem(ProtectedItemState state, bool equipIfSelected)
        {
            if (state == null || state.Player == null || state.Item == null)
                return;

            PlayerControllerB player = state.Player;
            GrabbableObject item = state.Item;
            int slot = state.SlotIndex;
            if (player.ItemSlots == null || slot < 0 || slot >= player.ItemSlots.Length)
                return;

            player.ItemSlots[slot] = item;
            item.playerHeldBy = player;
            item.isHeld = true;
            // F-TECH-1: vanilla DropHeldItem clears heldByPlayerOnServer, and the host's copy of
            // that flag is what DespawnPropsAtEndOfRound consults alongside isHeld.
            item.heldByPlayerOnServer = true;
            item.isHeldByEnemy = false;
            item.grabbable = false;
            item.grabbableToEnemies = false;

            bool selected = equipIfSelected && player.currentItemSlot == slot && !player.isPlayerDead;
            item.isPocketed = !selected;
            if (selected)
            {
                player.currentlyHeldObjectServer = item;
                player.isHoldingObject = true;
            }
            else if (player.currentlyHeldObjectServer == item)
            {
                player.currentlyHeldObjectServer = null;
                player.isHoldingObject = false;
            }

            Transform holder = ResolveItemHolder(player);
            if (holder != null)
            {
                item.parentObject = holder;
                item.transform.SetParent(holder, worldPositionStays: false);
                item.transform.localPosition = Vector3.zero;
                item.transform.localRotation = Quaternion.identity;
            }

            item.EnablePhysics(enable: false);
            if (item.propBody != null)
            {
                item.propBody.velocity = Vector3.zero;
                item.propBody.angularVelocity = Vector3.zero;
                item.propBody.isKinematic = true;
                item.propBody.useGravity = false;
            }

            if (player.IsOwner)
                RefreshSlotIcon(item, slot);
        }

        private static Transform ResolveItemHolder(PlayerControllerB player)
        {
            if (player == null)
                return null;

            // F-TECH-1: vanilla parents a held item to localItemHolder on the owner and to
            // serverItemHolder on every other machine (PlayerControllerB.GrabObjectClientRpc),
            // so a restore on a peer has to land on the same transform vanilla would have used.
            Transform preferred = player.IsOwner ? player.localItemHolder : player.serverItemHolder;

            return preferred
                ?? player.localItemHolder
                ?? player.serverItemHolder
                ?? player.leftHandItemTarget
                ?? player.transform;
        }

        private static void RefreshSlotIcon(GrabbableObject item, int slot)
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null || hud.itemSlotIcons == null || slot < 0 || slot >= hud.itemSlotIcons.Length)
                return;

            Image icon = hud.itemSlotIcons[slot];
            if (icon == null)
                return;

            icon.sprite = item != null && item.itemProperties != null ? item.itemProperties.itemIcon : null;
            icon.enabled = icon.sprite != null;
        }

        private static ulong ResolveNetworkObjectId(GrabbableObject item)
        {
            if (item == null)
                return 0uL;

            // GrabbableObject.NetworkObject throws when the behaviour is not spawned, so the
            // component is resolved the way vanilla KillPlayerServerRpc does it.
            return item.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned
                ? networkObject.NetworkObjectId
                : 0uL;
        }

        private static PlayerControllerB ResolvePlayer(ulong clientId)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.allPlayerScripts == null)
                return null;

            for (int i = 0; i < round.allPlayerScripts.Length; i++)
            {
                PlayerControllerB candidate = round.allPlayerScripts[i];
                if (candidate != null && candidate.actualClientId == clientId)
                    return candidate;
            }

            return null;
        }

#pragma warning disable Harmony003 // Custom message serializers mutate FastBufferReader/FastBufferWriter by design.
        private const int PROTECT_REQUEST_BYTES = sizeof(ulong);
        private const int PROTECT_RELAY_BYTES = sizeof(ulong) + sizeof(ulong);

        private static void AnnounceProtectedItem(PlayerControllerB player)
        {
            GrabbableObject item = GetProtectedItem(player, DeathboundUpgrade.PROTECTED_SLOT_INDEX);
            ulong itemNetworkId = ResolveNetworkObjectId(item);
            if (itemNetworkId == 0uL)
                return;

            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsClient)
                return;

            if (network.IsServer)
            {
                // The host is the authority: no round trip, just publish to everyone else. The
                // host's own drop is already handled by the local path.
                ProtectedItems[player.actualClientId] = itemNetworkId;
                RelayProtection(player.actualClientId, itemNetworkId, player.actualClientId);
                return;
            }

            FastBufferWriter writer = new FastBufferWriter(PROTECT_REQUEST_BYTES, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(itemNetworkId);
                messaging.SendNamedMessage(
                    MSG_PROTECT_SERVER,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"DeathboundPatch: protect request failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void RelayProtection(ulong protectedClientId, ulong itemNetworkId, ulong excludeClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer)
                return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(PROTECT_RELAY_BYTES, Allocator.Temp);
                try
                {
                    writer.WriteValueSafe(protectedClientId);
                    writer.WriteValueSafe(itemNetworkId);
                    messaging.SendNamedMessage(
                        MSG_PROTECT_CLIENT,
                        clientId,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"DeathboundPatch: protect relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        private static void OnProtectRequest(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || senderClientId == network.LocalClientId)
                return;

            try
            {
                reader.ReadValueSafe(out ulong itemNetworkId);

                // House rule: the protected player is the connection's own id, never a payload
                // field. The sender must own Deathbound and the item must really be in their
                // far-left slot right now, so a spoofed id cannot rescue someone else's scrap.
                if (UpgradeTierSync.GetTier(senderClientId, DeathboundUpgrade.UPGRADE_ID) < 1)
                    return;

                PlayerControllerB player = ResolvePlayer(senderClientId);
                GrabbableObject item = GetProtectedItem(player, DeathboundUpgrade.PROTECTED_SLOT_INDEX);
                if (item == null || ResolveNetworkObjectId(item) != itemNetworkId)
                    return;

                ProtectedItems[senderClientId] = itemNetworkId;
                RelayProtection(senderClientId, itemNetworkId, senderClientId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"DeathboundPatch: malformed protect request: {ex.Message}");
            }
        }

        private static void OnProtectRelay(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.IsServer || senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                reader.ReadValueSafe(out ulong protectedClientId);
                reader.ReadValueSafe(out ulong itemNetworkId);
                if (protectedClientId == network.LocalClientId)
                    return;

                ProtectedItems[protectedClientId] = itemNetworkId;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"DeathboundPatch: malformed protect relay: {ex.Message}");
            }
        }

        /// <summary>
        /// Netcode nulls the CustomMessagingManager on shutdown and builds a fresh one for the
        /// next host/join, so the latch is keyed on the NetworkManager instance (the
        /// UpgradeTierSync pattern) rather than a bare bool that would go deaf after one lobby.
        /// </summary>
        private static void RegisterNetworkHandlers()
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null)
                return;
            if (_handlersRegistered && ReferenceEquals(_registeredNetworkManager, network))
                return;

            try
            {
                if (network.IsServer)
                    messaging.RegisterNamedMessageHandler(MSG_PROTECT_SERVER, OnProtectRequest);

                messaging.RegisterNamedMessageHandler(MSG_PROTECT_CLIENT, OnProtectRelay);
                _handlersRegistered = true;
                _registeredNetworkManager = network;
            }
            catch (Exception ex)
            {
                _handlersRegistered = false;
                _registeredNetworkManager = null;
                Plugin.Log?.LogWarning($"DeathboundPatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }
#pragma warning restore Harmony003
    }
}
