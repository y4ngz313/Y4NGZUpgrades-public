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

        /// <summary>
        /// F-TECH-3: FuelPumpItem.ServerTickFill only ever runs on the host, so reading the
        /// local save state here halved stalls for the whole crew whenever the HOST owned the
        /// upgrade and did nothing at all for a client who bought it. The transpiler now also
        /// pushes the pump instance so the responsible crewmate can be resolved and their
        /// synced tier read, the same way ScavengerCompanyPatch credits fuel deposits.
        /// </summary>
        internal static float ApplyBreakChanceMultiplier(float chance, object pump)
        {
            try
            {
                if (!(pump is Component pumpComponent))
                    return chance;

                GrabbableObject grabbable = pumpComponent.GetComponent<GrabbableObject>();
                if (grabbable == null
                    || !ScavengerCompanyPatch.TryGetLastHolderClientId(grabbable, out ulong clientId))
                {
                    return chance;
                }

                int tier = UpgradeTierSync.GetTier(clientId, TurretHackerUpgrade.UPGRADE_ID);
                return chance * TurretHackerUpgrade.GetBreakChanceMultiplier(tier);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[FieldMechanic] pump stall multiplier failed safely: {ex.Message}");
                return chance;
            }
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
                    if (getter == null || multiplier == null || !instruction.Calls(getter))
                        continue;

                    // F-TECH-3: push the FuelPumpItem instance alongside the stall chance so the
                    // helper can attribute the pump to the crewmate who set it up. ServerTickFill
                    // is a plain instance method, so ldarg.0 is `this`.
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
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
            private static NetworkManager _registeredNetworkManager;
            private static float _cameraHackHold;
            private static string _cameraPromptInteractLabel;
            private static string _cameraPromptText;
            private static float _nextCameraPromptRefresh;

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

                Y4ngzPromptOverlay.SetPersistentPrompt(CameraHackPromptKey, ResolveCameraPromptText());

                bool hackHeld = UpgradeInteractInput.IsHeld();
                if (hackHeld)
                {
                    _cameraHackHold += Time.deltaTime;
                    if (_cameraHackHold >= TurretHackerUpgrade.HACK_HOLD_SECONDS)
                    {
                        _cameraHackHold = 0f;
                        RequestCameraDisable(cameraIndex);
                        ClearCameraHackPrompt();
                        return;
                    }
                }
                else
                {
                    _cameraHackHold = Mathf.MoveTowards(_cameraHackHold, 0f, Time.deltaTime * 2f);
                }

                // F-TECH-2: the hold had no feedback at all between the prompt and the 1.2 s
                // completion, and without Y4NGZUI it had no prompt either.
                Y4ngzPromptOverlay.SetPersistentPromptProgress(
                    CameraHackPromptKey,
                    _cameraHackHold / TurretHackerUpgrade.HACK_HOLD_SECONDS);
            }

            private static string ResolveCameraPromptText()
            {
                // Rebuilt only when the bound Interact key changes: this runs every frame.
                if (_cameraPromptText != null && Time.unscaledTime < _nextCameraPromptRefresh)
                    return _cameraPromptText;

                _nextCameraPromptRefresh = Time.unscaledTime + 0.5f;
                string interact = UpgradeInteractInput.DisplayLabel();
                if (_cameraPromptText == null
                    || !string.Equals(interact, _cameraPromptInteractLabel, StringComparison.Ordinal))
                {
                    _cameraPromptInteractLabel = interact;
                    _cameraPromptText = $"Disable camera: Hold [{interact}]";
                }

                return _cameraPromptText;
            }

            private static bool TryFindTargetedCamera(PlayerControllerB player, out int cameraIndex)
            {
                cameraIndex = -1;

                // F-TECH-14: this used to allocate a fresh RaycastHit[] plus a comparison
                // delegate and its closure every frame, for every player with level 1. It now
                // shares the single non-alloc look raycast with the door and turret probes and
                // scans for the nearest match, which is what Array.Sort was there to find.
                float range = TurretHackerPatch.ResolveLookRange(player);
                int hitCount = TurretHackerPatch.LookRaycast(player, range, out RaycastHit[] hits);

                Component nearestCamera = null;
                float nearestDistance = float.MaxValue;
                for (int i = 0; i < hitCount; i++)
                {
                    if (hits[i].collider == null || hits[i].distance >= nearestDistance)
                        continue;

                    Component cctvCamera = ResolveCameraComponent(hits[i].collider);
                    if (cctvCamera == null)
                        continue;

                    nearestCamera = cctvCamera;
                    nearestDistance = hits[i].distance;
                }

                if (nearestCamera == null)
                    return false;

                int index = GetCameraIndex(nearestCamera);
                if (index < 0 || IsCameraDisabled(index))
                    return false;

                cameraIndex = index;
                return true;
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
                bool wasDisabled = OptionalCctvBridge.IsCameraDisabled(cameraIndex);
                if (wasDisabled)
                    return false;

                bool providerAccepted = OptionalCctvBridge.TryDisableCamera(cameraIndex);
                bool isDisabled = OptionalCctvBridge.IsCameraDisabled(cameraIndex);
                return CctvStatisticsEventGuard.IsNewCameraDisable(
                    wasDisabled,
                    providerAccepted,
                    isDisabled);
            }

#pragma warning disable Harmony003 // Custom message serializers mutate FastBufferReader/FastBufferWriter by design.
            private static void RequestCameraDisable(int cameraIndex)
            {
                NetworkManager networkManager = NetworkManager.Singleton;
                if (networkManager == null)
                    return;

                if (networkManager.IsServer)
                {
                    if (HostTryDisableCamera(cameraIndex))
                    {
                        CctvEmployeeStatisticsNetwork.RecordHostConfirmedDeviceHack(
                            networkManager.LocalClientId,
                            "camera." + cameraIndex);
                    }
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

            /// <summary>
            /// F-TECH-4: the latch used to be a bare bool that nothing ever cleared. Netcode
            /// nulls the CustomMessagingManager on shutdown and builds a fresh one for the next
            /// host/join, so hosting a second lobby in one session left the host deaf to every
            /// client's camera hack (its own still worked, because RequestCameraDisable
            /// short-circuits to HostTryDisableCamera when IsServer). Keying the latch on the
            /// NetworkManager instance is the robust form of the TurretHackerPatch fix: it
            /// re-registers exactly once per manager and never twice for the same one.
            /// </summary>
            private static void RegisterNetworkHandlers()
            {
                NetworkManager network = NetworkManager.Singleton;
                CustomMessagingManager messaging = network?.CustomMessagingManager;
                if (network == null || messaging == null) return;
                if (!network.IsServer) return;
                if (_handlersRegistered && ReferenceEquals(_registeredNetworkManager, network)) return;

                try
                {
                    messaging.RegisterNamedMessageHandler(MSG_CAMERA_HACK, OnCameraHackRequest);
                    _handlersRegistered = true;
                    _registeredNetworkManager = network;
                }
                catch (Exception ex)
                {
                    _handlersRegistered = false;
                    _registeredNetworkManager = null;
                    Plugin.Log?.LogWarning($"[FieldMechanic] RegisterNamedMessageHandler failed: {ex.Message}");
                }
            }

            [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
            [HarmonyPostfix]
            private static void PostDisconnect()
            {
                _handlersRegistered = false;
                _registeredNetworkManager = null;
                _cameraHackHold = 0f;
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

                if (HostTryDisableCamera(cameraIndex))
                {
                    CctvEmployeeStatisticsNetwork.RecordHostConfirmedDeviceHack(
                        senderClientId,
                        "camera." + cameraIndex);
                }
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
