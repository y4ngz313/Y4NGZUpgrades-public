using System;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ScannerTransporterPatch
    {
        private const float MinCarryWeight = 1f;
        private const float MaxCarryWeight = 10f;

        internal sealed class SlotSwitchScope : IDisposable
        {
            private PlayerControllerB _player;

            internal SlotSwitchScope(PlayerControllerB player)
            {
                _player = player;
                _player.twoHanded = false;
            }

            public void Dispose()
            {
                PlayerControllerB player = _player;
                _player = null;
                if (player != null)
                    SyncTwoHandedFlagsToHeldItem(player);
            }
        }

        [HarmonyPatch(typeof(HUDManager), "MeetsScanNodeRequirements")]
        [HarmonyPrefix]
        private static void BetterScannerRangePrefix(ScanNodeProperties node, out int __state)
        {
            __state = 0;
            if (node == null) return;

            if (node.maxRange <= 0) return;

            int tier = FieldOpticsUpgrade.GetTier();
            if (tier <= 0) return;

            __state = node.maxRange;
            node.maxRange = Mathf.Max(node.minRange + 1,
                Mathf.CeilToInt(node.maxRange * FieldOpticsUpgrade.GetRangeMultiplier()));
        }

        [HarmonyPatch(typeof(HUDManager), "MeetsScanNodeRequirements")]
        [HarmonyPostfix]
        private static void BetterScannerRangePostfix(ScanNodeProperties node, int __state)
        {
            if (node == null || __state <= 0) return;
            node.maxRange = __state;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void ApplyTransporterCarryWeight(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance)) return;

            float penalty = GetCarriedItemWeightPenalty(__instance);
            float multiplier = TransporterUpgrade.GetCarryWeightMultiplier();
            float targetCarryWeight = Mathf.Clamp(MinCarryWeight + penalty * multiplier, MinCarryWeight, MaxCarryWeight);

            if (Mathf.Abs(__instance.carryWeight - targetCarryWeight) <= 0.005f)
                return;

            __instance.carryWeight = targetCarryWeight;
            StartOfRound.Instance?.SendChangedWeightEvent();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "FirstEmptyItemSlot")]
        [HarmonyPostfix]
        private static void LimitTwoHandedInventory(
            PlayerControllerB __instance,
            GrabbableObject attemptingGrab,
            ref int __result)
        {
            if (!OptionalPluginCapabilities.NativeInventoryAvailable) return;
            if (__result == -1) return;
            if (!IsLocalPlayer(__instance)) return;
            if (attemptingGrab == null || attemptingGrab.itemProperties == null) return;
            if (!attemptingGrab.itemProperties.twoHanded) return;

            int allowed = NativeInventoryModel.GetTwoHandedLimit(ExtraSlotUpgrade.GetTier());
            if (CountTwoHandedItems(__instance) >= allowed)
                __result = -1;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "BeginGrabObject")]
        [HarmonyPrefix]
        private static void AllowSecondTwoHandedGrabPrefix(
            PlayerControllerB __instance,
            out SlotSwitchScope __state)
        {
            __state = null;
            if (!CanRelaxTwoHandedHandsFull(__instance)) return;
            if (CountTwoHandedItems(__instance) >= 2) return;

            __state = new SlotSwitchScope(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "BeginGrabObject")]
        [HarmonyFinalizer]
        private static Exception AllowSecondTwoHandedGrabFinalizer(
            Exception __exception,
            SlotSwitchScope __state)
        {
            __state?.Dispose();
            return __exception;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ScrollMouse_performed")]
        [HarmonyPrefix]
        private static void AllowTwoHandedSlotSwitchPrefix(
            PlayerControllerB __instance,
            out SlotSwitchScope __state)
        {
            __state = BeginSlotSwitchScope(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ScrollMouse_performed")]
        [HarmonyFinalizer]
        private static Exception AllowTwoHandedSlotSwitchFinalizer(
            Exception __exception,
            SlotSwitchScope __state)
        {
            __state?.Dispose();
            return __exception;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "UseUtilitySlot_performed")]
        [HarmonyPrefix]
        private static void AllowTwoHandedUtilitySwitchPrefix(
            PlayerControllerB __instance,
            out SlotSwitchScope __state)
        {
            __state = BeginSlotSwitchScope(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "UseUtilitySlot_performed")]
        [HarmonyFinalizer]
        private static Exception AllowTwoHandedUtilitySwitchFinalizer(
            Exception __exception,
            SlotSwitchScope __state)
        {
            __state?.Dispose();
            return __exception;
        }

        internal static SlotSwitchScope BeginSlotSwitchScope(PlayerControllerB player)
        {
            return CanRelaxTwoHandedHandsFull(player) ? new SlotSwitchScope(player) : null;
        }

        private static bool CanRelaxTwoHandedHandsFull(PlayerControllerB player)
        {
            return IsLocalPlayer(player)
                && OptionalPluginCapabilities.NativeInventoryAvailable
                && ExtraSlotUpgrade.CanCarryTwoTwoHandedItems()
                && player.twoHanded;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null) return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static float GetCarriedItemWeightPenalty(PlayerControllerB player)
        {
            float penalty = 0f;
            if (player == null) return penalty;

            if (player.ItemSlots != null)
            {
                for (int i = 0; i < player.ItemSlots.Length; i++)
                    penalty += GetItemWeightPenalty(player.ItemSlots[i]);
            }

            penalty += GetItemWeightPenalty(player.ItemOnlySlot);
            return penalty;
        }

        private static float GetItemWeightPenalty(GrabbableObject item)
        {
            if (item == null || item.itemProperties == null) return 0f;
            return Mathf.Max(0f, item.itemProperties.weight - 1f);
        }

        private static int CountTwoHandedItems(PlayerControllerB player)
        {
            int count = 0;
            if (player == null) return count;

            if (player.ItemSlots != null)
            {
                for (int i = 0; i < player.ItemSlots.Length; i++)
                {
                    GrabbableObject item = player.ItemSlots[i];
                    if (item != null && item.itemProperties != null && item.itemProperties.twoHanded)
                        count++;
                }
            }

            if (player.ItemOnlySlot != null
                && player.ItemOnlySlot.itemProperties != null
                && player.ItemOnlySlot.itemProperties.twoHanded)
            {
                count++;
            }

            return count;
        }

        private static void SyncTwoHandedFlagsToHeldItem(PlayerControllerB player)
        {
            if (player == null) return;

            GrabbableObject held = player.currentlyHeldObjectServer;
            if (held == null || held.itemProperties == null)
            {
                player.twoHanded = false;
                player.twoHandedAnimation = false;
                return;
            }

            player.twoHanded = held.itemProperties.twoHanded;
            player.twoHandedAnimation = held.itemProperties.twoHandedAnimation;
        }
    }
}
