using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
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
    internal static class FieldMechanicCompanyPatch
    {
        private const string CameraHackPromptKey = "FieldMechanic.CameraHack";

        private static readonly Dictionary<string, Type> CompanyTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            ResetInteractionPrompts();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            ResetInteractionPrompts();
        }

        internal static float ApplyBreakChanceMultiplier(float chance)
        {
            return chance * TurretHackerUpgrade.GetBreakChanceMultiplier();
        }

        private static Type CompanyType(string typeName)
        {
            if (CompanyTypeCache.TryGetValue(typeName, out Type cached))
                return cached;

            // ShipSystems.{Layout,Fuel,Power} moved to Y4NGZShipSystems.dll in Y4NGZCompany#613;
            // Facility.{Cameras,Security} moved to LethalCCTV.dll in Y4NGZCompany#393.
            // CameraHackPatch.Prepare() resolves
            // "CCTVCamera" through here, so without the LethalCCTV probes the whole camera-hack
            // patch silently declines to apply.
            Type resolved = Type.GetType("Y4NGZCompany.ShipSystems.Layout." + typeName + ", Y4NGZShipSystems", throwOnError: false)
                ?? Type.GetType("Y4NGZCompany.ShipSystems.Fuel." + typeName + ", Y4NGZShipSystems", throwOnError: false)
                ?? Type.GetType("Y4NGZCompany.ShipSystems.Power." + typeName + ", Y4NGZShipSystems", throwOnError: false)
                ?? Type.GetType("Y4NGZCompany.Facility.Cameras." + typeName + ", LethalCCTV", throwOnError: false)
                ?? Type.GetType("Y4NGZCompany.Facility.Security." + typeName + ", LethalCCTV", throwOnError: false)
                ?? FindCompanyTypeByName(typeName);

            CompanyTypeCache[typeName] = resolved;
            return resolved;
        }

        private static Type FindCompanyTypeByName(string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Post-split, the Company family is several assemblies (Y4NGZCompany#393).
                string name = assembly?.GetName().Name;
                if (name != "Y4NGZShipSystems"
                    && name != "Y4NGZCompany"
                    && name != "LethalCCTV"
                    && name != "Y4NGZCore")
                    continue;

                Type found = FindTypeByName(assembly, typeName);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static Type FindTypeByName(Assembly assembly, string typeName)
        {
            try
            {
                Type[] types = assembly.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        return type;
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                Type[] types = ex.Types;
                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        return type;
                }
            }
            catch
            {
            }

            return null;
        }

        [HarmonyPatch]
        internal static class FuelPumpStallChancePatch
        {
            private static bool Prepare()
            {
                return CompanyType("FuelPumpItem") != null;
            }

            private static MethodBase TargetMethod()
            {
                return AccessTools.Method(CompanyType("FuelPumpItem"), "ServerTickFill");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo getter = AccessTools.PropertyGetter(CompanyType("ShipFuelConfig"), "PumpStallChance01");
                MethodInfo multiplier = AccessTools.Method(typeof(FieldMechanicCompanyPatch), nameof(ApplyBreakChanceMultiplier));
                foreach (CodeInstruction instruction in instructions)
                {
                    yield return instruction;
                    if (getter != null && multiplier != null && instruction.Calls(getter))
                        yield return new CodeInstruction(OpCodes.Call, multiplier);
                }
            }
        }

        /// <summary>
        /// Tier 1: hold-interact on a physical Company CCTV camera to permanently
        /// disable it for the round. CctvSupportApi.TryDisableCamera is host-only,
        /// so clients send a named-message request carrying the camera's
        /// deterministic CameraIndex; the host validates the sender's Field
        /// Mechanic tier via UpgradeTierSync before disabling. Company replicates
        /// the disabled state to every client itself.
        /// </summary>
        [HarmonyPatch]
        internal static class CameraHackPatch
        {
            private const string MSG_CAMERA_HACK = "Y4NGZFieldMechanic.CameraHackServerRpc";

            private static bool _handlersRegistered;
            private static float _cameraHackHold;

            internal static void ResetLocalState()
            {
                _cameraHackHold = 0f;
            }

            private static bool Prepare()
            {
                return CompanyType("CCTVCamera") != null;
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
                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
                if (__instance == null || __instance != local)
                    return;

                if (!TurretHackerUpgrade.CanHackCameras())
                {
                    _cameraHackHold = 0f;
                    ClearCameraHackPrompt();
                    return;
                }

                if (__instance.isPlayerDead || __instance.isTypingChat || __instance.inTerminalMenu
                    || __instance.inSpecialInteractAnimation
                    || (__instance.quickMenuManager != null && __instance.quickMenuManager.isMenuOpen))
                {
                    _cameraHackHold = 0f;
                    ClearCameraHackPrompt();
                    return;
                }

                int cameraIndex;
                if (!TryFindTargetedCamera(__instance, out cameraIndex))
                {
                    _cameraHackHold = 0f;
                    ClearCameraHackPrompt();
                    return;
                }

                string interact = UpgradeInteractInput.DisplayLabel();
                Y4ngzPromptOverlay.SetPersistentPrompt(
                    CameraHackPromptKey,
                    $"Disable camera: Hold [{interact}]");

                bool hackHeld = UpgradeInteractInput.IsHeld();
                if (hackHeld)
                {
                    _cameraHackHold += Time.deltaTime;
                    if (_cameraHackHold >= TurretHackerUpgrade.HACK_HOLD_SECONDS)
                    {
                        _cameraHackHold = 0f;
                        RequestCameraDisable(cameraIndex);
                        ClearCameraHackPrompt();
                    }
                }
                else
                {
                    _cameraHackHold = Mathf.MoveTowards(_cameraHackHold, 0f, Time.deltaTime * 2f);
                }
            }

            private static bool TryFindTargetedCamera(PlayerControllerB player, out int cameraIndex)
            {
                cameraIndex = -1;

                Camera camera = player.gameplayCamera != null ? player.gameplayCamera : Camera.main;
                if (camera == null)
                    return false;

                float range = player.grabDistance > 0f ? player.grabDistance : 4f;
                Ray ray = new Ray(camera.transform.position, camera.transform.forward);
                RaycastHit[] hits = Physics.RaycastAll(ray, range, ~0, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i].collider == null)
                        continue;

                    Component cctvCamera = ResolveCameraComponent(hits[i].collider);
                    if (cctvCamera == null)
                        continue;

                    int index = GetCameraIndex(cctvCamera);
                    if (index < 0 || IsCameraDisabled(index))
                        return false;

                    cameraIndex = index;
                    return true;
                }

                return false;
            }

            private static Component ResolveCameraComponent(Collider collider)
            {
                Type cameraType = CompanyType("CCTVCamera");
                Component cctvCamera = cameraType != null
                    ? collider.GetComponentInParent(cameraType)
                    : null;
                if (cctvCamera != null)
                    return cctvCamera;

                // The visible camera prop (and its collider) is parented under the
                // owning tile, not the CCTVCamera holder; CctvBreakableCamera on the
                // prop keeps a private reference back to the holder.
                Type breakableType = CompanyType("CctvBreakableCamera");
                if (breakableType == null)
                    return null;

                Component breakable = collider.GetComponentInParent(breakableType);
                if (breakable == null)
                    return null;

                FieldInfo cameraField = AccessTools.Field(breakableType, "_camera");
                return cameraField != null ? cameraField.GetValue(breakable) as Component : null;
            }

            private static int GetCameraIndex(Component cctvCamera)
            {
                try
                {
                    return cctvCamera?.GetType().GetProperty(
                        "CameraIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(cctvCamera) is int index ? index : -1;
                }
                catch
                {
                    return -1;
                }
            }

            private static bool IsCameraDisabled(int cameraIndex)
            {
                return OptionalCctvBridge.IsCameraDisabled(cameraIndex);
            }

            private static bool HostTryDisableCamera(int cameraIndex)
            {
                return OptionalCctvBridge.TryDisableCamera(cameraIndex);
            }

#pragma warning disable Harmony003 // Custom message serializers mutate FastBufferReader/FastBufferWriter by design.
            private static void RequestCameraDisable(int cameraIndex)
            {
                NetworkManager networkManager = NetworkManager.Singleton;
                if (networkManager == null)
                    return;

                if (networkManager.IsServer)
                {
                    HostTryDisableCamera(cameraIndex);
                    return;
                }

                if (networkManager.CustomMessagingManager == null)
                    return;

                FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
                try
                {
                    writer.WriteValueSafe(cameraIndex);
                    networkManager.CustomMessagingManager.SendNamedMessage(MSG_CAMERA_HACK, 0uL, writer);
                }
                finally
                {
                    writer.Dispose();
                }
            }

            private static void RegisterNetworkHandlers()
            {
                if (_handlersRegistered) return;
                if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;
                if (!NetworkManager.Singleton.IsServer) return;

                try
                {
                    NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_CAMERA_HACK, OnCameraHackRequest);
                    _handlersRegistered = true;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[FieldMechanic] RegisterNamedMessageHandler failed: {ex.Message}");
                }
            }

            private static void OnCameraHackRequest(ulong senderClientId, FastBufferReader reader)
            {
                if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                    return;

                int cameraIndex;
                try
                {
                    reader.ReadValueSafe(out cameraIndex);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[FieldMechanic] malformed camera hack request: {ex.Message}");
                    return;
                }

                if (UpgradeTierSync.GetTier(senderClientId, TurretHackerUpgrade.UPGRADE_ID) < 1)
                    return;

                HostTryDisableCamera(cameraIndex);
            }
#pragma warning restore Harmony003
        }

        private static void ResetInteractionPrompts()
        {
            CameraHackPatch.ResetLocalState();
            ClearCameraHackPrompt();
        }

        private static void ClearCameraHackPrompt()
        {
            Y4ngzPromptOverlay.ClearPersistentPrompt(CameraHackPromptKey);
        }
    }
}
