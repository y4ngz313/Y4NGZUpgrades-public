using System.Collections;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class DeathboundPatch
    {
        private sealed class ProtectedItemState
        {
            internal PlayerControllerB Player;
            internal GrabbableObject Item;
            internal int SlotIndex;
        }

        private static bool _localDeathDropActive;
        private static ProtectedItemState _pendingState;

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPrefix]
        private static void PreKillPlayer(PlayerControllerB __instance)
        {
            if (!ShouldProtectPlayer(__instance))
                return;

            _localDeathDropActive = true;
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
            if (!_localDeathDropActive || !ShouldProtectPlayer(__instance))
                return;

            int slot = DeathboundUpgrade.PROTECTED_SLOT_INDEX;
            if (__instance.ItemSlots == null || slot < 0 || slot >= __instance.ItemSlots.Length)
                return;

            GrabbableObject item = __instance.ItemSlots[slot];
            if (item == null)
                return;

            __state = new ProtectedItemState
            {
                Player = __instance,
                Item = item,
                SlotIndex = slot
            };
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
        }

        private static bool ShouldProtectPlayer(PlayerControllerB player)
        {
            if (player == null || !DeathboundUpgrade.IsUnlocked())
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static void StartRestoreCoroutine(PlayerControllerB player, float delaySeconds)
        {
            if (_pendingState == null || _pendingState.Player != player)
                return;

            if (GameNetworkManager.Instance != null)
                GameNetworkManager.Instance.StartCoroutine(RestoreAfterDelay(player, delaySeconds));
        }

        private static IEnumerator RestoreAfterDelay(PlayerControllerB player, float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);
            RestoreProtectedItem(_pendingState, equipIfSelected: player != null && !player.isPlayerDead);
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

            RefreshSlotIcon(item, slot);

            if (!player.isPlayerDead)
                _pendingState = null;
        }

        private static Transform ResolveItemHolder(PlayerControllerB player)
        {
            if (player == null)
                return null;

            return player.localItemHolder
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
    }
}
