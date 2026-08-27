using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch(typeof(PlayerControllerB), "Awake")]
    internal static class ExtraSlotPlayerAwakePatch
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerControllerB __instance)
        {
            ExtraSlotManager.EnsurePhysicalInventory(__instance);
        }
    }

    [HarmonyPatch(typeof(HUDManager), "Awake")]
    internal static class ExtraSlotHudAwakePatch
    {
        [HarmonyPostfix]
        private static void Postfix(HUDManager __instance)
        {
            ExtraSlotManager.AllocateHudSlots(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
    internal static class ExtraSlotConnectPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            ExtraSlotManager.EnsureAllPlayerInventories();
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "FirstEmptyItemSlot")]
    internal static class ExtraSlotFirstEmptyPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            PlayerControllerB __instance,
            GrabbableObject attemptingGrab,
            ref int __result)
        {
            if (!ExtraSlotManager.TryGetFirstEmptySlot(__instance, attemptingGrab, out int slot))
                return true;

            __result = slot;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "NextItemSlot")]
    internal static class ExtraSlotNextItemPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerControllerB __instance, bool forward, ref int __result)
        {
            if (!ExtraSlotManager.TryGetNextItemSlot(__instance, forward, out int slot))
                return true;

            __result = slot;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
    internal static class ExtraSlotCapacityTickPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerControllerB __instance)
        {
            ExtraSlotManager.TickLocalCapacity(__instance);
        }
    }

    /// <summary>
    /// Lethal Company v81 scrolls locally to an exact slot but tells peers only a direction.
    /// Seven physical slots and per-player logical capacities require the already-present exact
    /// slot RPC. Native inventory expansion remains disabled unless both call sites validate.
    /// </summary>
    [HarmonyPatch]
    internal static class ExtraSlotScrollRpcPatch
    {
        private static MethodInfo _scrollMethod;
        private static MethodInfo _directionRpc;
        private static MethodInfo _exactSlotRpc;
        private static FieldInfo _currentItemSlot;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            if (!OptionalPluginCapabilities.NativeInventoryAvailable)
            {
                ExtraSlotManager.DisableForHotbarPlus();
                return false;
            }

            ResolveContract();
            bool valid = _scrollMethod != null
                && _directionRpc != null
                && _exactSlotRpc != null
                && _exactSlotRpc.ReturnType == typeof(void)
                && !_exactSlotRpc.IsStatic
                && _currentItemSlot != null
                && _currentItemSlot.FieldType == typeof(int);
            if (!valid)
            {
                ExtraSlotManager.SetScrollCompatibilityResult(
                    false,
                    "The exact v81 SwitchToSlotServerRpc(int) contract or scroll method was not found.");
            }

            return valid;
        }

        private static void ResolveContract()
        {
            _scrollMethod = AccessTools.DeclaredMethod(
                typeof(PlayerControllerB),
                "ScrollMouse_performed",
                new[] { typeof(InputAction.CallbackContext) });
            _directionRpc = AccessTools.DeclaredMethod(
                typeof(PlayerControllerB),
                "SwitchItemSlotsServerRpc",
                new[] { typeof(bool) });
            _exactSlotRpc = AccessTools.DeclaredMethod(
                typeof(PlayerControllerB),
                "SwitchToSlotServerRpc",
                new[] { typeof(int) });
            _currentItemSlot = AccessTools.DeclaredField(typeof(PlayerControllerB), "currentItemSlot");
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            if (_scrollMethod == null)
                ResolveContract();
            return _scrollMethod;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList();
            int replacements = original.Count(instruction => instruction.Calls(_directionRpc));
            if (replacements != 2)
            {
                ExtraSlotManager.SetScrollCompatibilityResult(
                    false,
                    $"Expected exactly two v81 boolean scroll RPC calls, but found {replacements}.");
                return original;
            }

            List<CodeInstruction> patched = new List<CodeInstruction>(original.Count + replacements * 3);
            for (int i = 0; i < original.Count; i++)
            {
                CodeInstruction instruction = original[i];
                if (!instruction.Calls(_directionRpc))
                {
                    patched.Add(instruction);
                    continue;
                }

                // Before the old call the stack is [player, forward]. Discard the direction,
                // duplicate the player for the field read, then call the exact-index RPC.
                CodeInstruction popDirection = new CodeInstruction(OpCodes.Pop);
                popDirection.labels.AddRange(instruction.labels);
                popDirection.blocks.AddRange(instruction.blocks);
                patched.Add(popDirection);
                patched.Add(new CodeInstruction(OpCodes.Dup));
                patched.Add(new CodeInstruction(OpCodes.Ldfld, _currentItemSlot));
                patched.Add(new CodeInstruction(OpCodes.Call, _exactSlotRpc));
            }

            ExtraSlotManager.SetScrollCompatibilityResult(true);
            return patched;
        }
    }
}
