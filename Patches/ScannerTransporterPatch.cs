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

            // #435: gate on the multiplier, never on the rank. In LGU-preferred mode Field Optics
            // keeps only the lamp and this multiplier is neutral, so the patch must not touch
            // maxRange at all - the Mathf.Max floor below would otherwise still move a node whose
            // maxRange sits at or below minRange.
            float multiplier = FieldOpticsUpgrade.GetRangeMultiplier();
            if (multiplier <= 1f) return;

            __state = node.maxRange;
            node.maxRange = Mathf.Max(node.minRange + 1,
                Mathf.CeilToInt(node.maxRange * multiplier));
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

            // F-ENF-8: at tier 0 the multiplier is 1.0, so this postfix used to own carryWeight for
            // every player whether or not they bought Transporter - recomputing and reassigning it
            // every frame, which silently stomps any other weight-affecting mod's modifier for no
            // gain of our own. Own the field only while we are actually changing it.
            int tier = TransporterUpgrade.GetTier();
            if (tier <= 0) return;

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
            // F-ENF-10: NativeInventoryAvailable only means "HotbarPlus is absent". The slots (and
            // therefore the two-handed limit that goes with them) only actually exist when the v81
            // scroll-RPC IL contract validated, which is what NativeSlotsEnabled reports.
            if (!ExtraSlotManager.NativeSlotsEnabled) return;
            if (__result == -1) return;
            if (!IsLocalPlayer(__instance)) return;
            if (attemptingGrab == null || attemptingGrab.itemProperties == null) return;
            if (!attemptingGrab.itemProperties.twoHanded) return;

            // #435: the allowance, not the rank. The unique-only variant sells the same three
            // hotbar ranks but never the second two-handed item, which is LGU Deeper Pockets'.
            int allowed = NativeInventoryModel.GetTwoHandedLimit(ExtraSlotUpgrade.CanCarryTwoTwoHandedItems());
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
            // F-ENF-10: only a second TWO-HANDED item earns the relaxation.
            if (!GrabTargetIsTwoHanded(__instance)) return;

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
                // F-ENF-10: same gate correction as LimitTwoHandedInventory. Under the failed-IL
                // path the inventory stays at four slots, so the relaxation must stay off too.
                && ExtraSlotManager.NativeSlotsEnabled
                && ExtraSlotUpgrade.CanCarryTwoTwoHandedItems()
                && player.twoHanded;
        }

        /// <summary>
        /// F-ENF-10: Deeper Pockets L3 promises "you can carry two two-handed items at once", not
        /// "you can grab anything while your hands are full". Vanilla's BeginGrabObject bails on
        /// `twoHanded` before it ever resolves the grabbed object, so clearing the flag in the
        /// prefix also let a shotgun-carrying player scoop up one-handed scrap. The raycast below
        /// mirrors vanilla's own so the relaxation only opens for a genuinely two-handed target;
        /// it runs once per interact press, not per frame.
        /// </summary>
        private static bool GrabTargetIsTwoHanded(PlayerControllerB player)
        {
            if (player == null || player.gameplayCamera == null) return false;

            Transform camera = player.gameplayCamera.transform;
            RaycastHit hit;
            if (!Physics.Raycast(new Ray(camera.position, camera.forward), out hit, player.grabDistance, GetInteractableObjectsMask()))
                return false;

            Collider collider = hit.collider;
            if (collider == null || collider.gameObject.layer == 8 || !collider.CompareTag("PhysicsProp"))
                return false;

            GrabbableObject target = collider.transform.gameObject.GetComponent<GrabbableObject>();
            return target != null && target.itemProperties != null && target.itemProperties.twoHanded;
        }

        // PlayerControllerB.interactableObjectsMask is private; read it once by reflection and fall
        // back to the v81 literal so a field rename degrades to vanilla's mask rather than throwing.
        private const int VanillaInteractableObjectsMask = 1073742656;
        private static int _interactableObjectsMask;

        private static int GetInteractableObjectsMask()
        {
            if (_interactableObjectsMask != 0) return _interactableObjectsMask;

            _interactableObjectsMask = VanillaInteractableObjectsMask;
            try
            {
                System.Reflection.FieldInfo field =
                    AccessTools.Field(typeof(PlayerControllerB), "interactableObjectsMask");
                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
                if (field != null && field.FieldType == typeof(int) && local != null)
                {
                    int value = (int)field.GetValue(local);
                    if (value != 0)
                        _interactableObjectsMask = value;
                }
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning($"ScannerTransporterPatch: interactableObjectsMask probe failed ({error.Message}); using the v81 literal.");
            }

            return _interactableObjectsMask;
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
            // F-ENF-8: vanilla accumulates the SIGNED (weight - 1f) - BeginGrabObject does
            // `carryWeight = Clamp(carryWeight + (weight - 1f), 1f, 10f)` - so a sub-1.0-weight item
            // subtracts. Clamping at zero made those items merely free instead of beneficial; the
            // caller still applies vanilla's [1, 10] clamp to the total.
            //
            // Transporter recomputes the whole figure from scratch every frame, which makes it the
            // last writer of carryWeight. LGU's Back Muscles reduce-weight mode rewrites the same
            // vanilla arithmetic and writes the field absolutely on purchase, so the recomputation
            // has to apply its per-item reduction too or it silently deletes that upgrade. In Back
            // Muscles' other two modes - and with LGU absent - this is the unchanged value.
            return LguEffectCompatibility.ComposeItemWeightPenalty(item.itemProperties.weight - 1f);
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
