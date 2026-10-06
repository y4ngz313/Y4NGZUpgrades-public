using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Upgrades;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Y4NGZUpgrades.Patches
{
    internal static partial class CourierDronePatch
    {
        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.Crouch), new[] { typeof(bool) })]
        [HarmonyPrefix]
        private static bool PrePlayerCrouch(PlayerControllerB __instance, bool crouch)
        {
            if (!_manualControlActive || !crouch || !IsLocalPlayer(__instance))
                return true;

            return false;
        }

        private static void TryStartManualControl(PlayerControllerB player)
        {
            if (player == null)
                return;
            if (_droneVisual == null || _droneDestroyedThisRound || _mode != DroneMode.ManualControl)
            {
                DisplayOwnerTip((int)player.playerClientId, "Drone link unavailable.", isWarning: true);
                return;
            }
            if (_manualControlActive)
                return;

            _manualControlActive = true;
            _manualControlCameraActivated = false;
            _manualControlPlayerId = (int)player.playerClientId;
            _manualDroneStartupUntil = 0f;
            _manualNextPoseSyncAt = 0f;
            InitializeManualAngles(player);
            SaveAndLockPlayerInput(player);
            SuppressManualPlayerPhysicalControls(player);
            FieldOperationsTabletPatch.TrySetPilotActionReadyHold(true);
            CreateManualCamera(player);
            ActivateManualCamera(player);
            DisplayOwnerTip((int)player.playerClientId, "Remote drone link active.", isWarning: false);
        }

        private static void TickManualControl(PlayerControllerB player)
        {
            if (Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.DronePilotExit))
            {
                StopManualControl(resumeDrone: true, notifyOwner: false);
                return;
            }

            if (!CanContinueManualControl(player))
            {
                StopManualControl(resumeDrone: true, notifyOwner: false);
                return;
            }

            SaveAndLockPlayerInput(player);
            SuppressManualPlayerPhysicalControls(player);
            FieldOperationsTabletPatch.TrySetPilotActionReadyHold(true);
            if (!_manualControlCameraActivated)
                ActivateManualCamera(player);

            ApplyManualPlayerPresentation(player);
            SuppressManualPlayerPhysicalControls(player);
            UpdateManualLook();
            UpdateManualDroneMovement(player);
            UpdateManualCameraTransform();
            UpdateManualHud();
            HandleManualDroneActions(player);

            if (Time.time >= _manualNextPoseSyncAt)
            {
                _manualNextPoseSyncAt = Time.time + MANUAL_POSE_SYNC_INTERVAL;
                BroadcastManualPose(active: true);
            }
        }

        private static bool CanContinueManualControl(PlayerControllerB player)
        {
            return player != null
                && !player.isPlayerDead
                && !player.isTypingChat
                && !player.inTerminalMenu
                && (!player.inSpecialInteractAnimation || FieldOperationsTabletPatch.IsTabletActive)
                && (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen)
                && !_droneDestroyedThisRound
                && _droneVisual != null
                && _mode == DroneMode.ManualControl;
        }

        private static void InitializeManualAngles(PlayerControllerB player)
        {
            Transform basis = _droneVisual != null ? _droneVisual.transform : player?.transform;
            Vector3 forward = basis != null ? basis.forward : Vector3.forward;
            Vector3 flat = new Vector3(forward.x, 0f, forward.z);
            if (flat.sqrMagnitude <= 0.001f && player != null)
                flat = new Vector3(player.transform.forward.x, 0f, player.transform.forward.z);
            if (flat.sqrMagnitude <= 0.001f)
                flat = Vector3.forward;

            _manualYaw = Quaternion.LookRotation(flat.normalized, Vector3.up).eulerAngles.y;
            _manualPitch = 0f;
        }

        private static void SaveAndLockPlayerInput(PlayerControllerB player)
        {
            if (player == null)
                return;

            if (!_manualSavedInputState)
            {
                _restoreMoveInput = player.disableMoveInput;
                _restoreLookInput = player.disableLookInput;
                _restoreInteractInput = player.disableInteract;
                _restoreGameplayCameraEnabled = player.gameplayCamera == null || player.gameplayCamera.enabled;
                _manualSavedInputState = true;
            }

            player.disableMoveInput = true;
            player.disableLookInput = true;
            player.disableInteract = true;
            player.externalForceAutoFade = Vector3.zero;
        }

        private static void CreateManualCamera(PlayerControllerB player)
        {
            if (_manualCameraObject != null)
                UnityEngine.Object.Destroy(_manualCameraObject);

            Camera source = player != null && player.gameplayCamera != null ? player.gameplayCamera : Camera.main;
            _manualCameraObject = new GameObject("Y4NGZ_CourierDroneManualCamera");
            _manualCamera = _manualCameraObject.AddComponent<Camera>();
            if (source != null)
            {
                _manualCamera.fieldOfView = source.fieldOfView;
                _manualCamera.nearClipPlane = source.nearClipPlane;
                _manualCamera.farClipPlane = source.farClipPlane;
                _manualCamera.cullingMask = source.cullingMask;
                _manualCamera.clearFlags = source.clearFlags;
                _manualCamera.backgroundColor = source.backgroundColor;
                _manualCamera.allowHDR = source.allowHDR;
                _manualCamera.allowMSAA = source.allowMSAA;
                _manualCamera.depth = source.depth + 1f;
            }
            else
            {
                _manualCamera.fieldOfView = 66f;
                _manualCamera.nearClipPlane = 0.03f;
                _manualCamera.farClipPlane = 1000f;
            }

            _manualCamera.enabled = false;
            _manualCamera.targetTexture = null;
            UpdateManualCameraTransform();
        }

        private static void ActivateManualCamera(PlayerControllerB player)
        {
            if (_manualCamera == null)
                CreateManualCamera(player);

            ApplyManualPlayerPresentation(player);
            _manualDroneStartupUntil = Time.unscaledTime + MANUAL_DRONE_BOOT_SCREEN_SECONDS;
            CreateManualHud();
            UpdateManualCameraTransform();
            if (player != null && player.gameplayCamera != null)
                player.gameplayCamera.enabled = false;
            if (_manualCamera != null)
            {
                _manualCamera.targetTexture = null;
                _manualCamera.enabled = true;
            }

            ActivateManualAudioListener(player);
            _manualControlCameraActivated = true;
            BroadcastManualPose(active: true);
        }

        /// <summary>
        /// F-DRONE-11: disabling the gameplay Camera left the AudioListener riding on the player, so
        /// a pilot 30 m away still heard the world from their own body. Unity allows exactly one
        /// active listener, so the player's is disabled (not destroyed) while the drone camera owns
        /// one, and restored verbatim on exit.
        /// </summary>
        private static void ActivateManualAudioListener(PlayerControllerB player)
        {
            if (_manualCameraObject == null)
                return;

            if (_savedAudioListener == null)
            {
                AudioListener listener = player != null && player.gameplayCamera != null
                    ? player.gameplayCamera.GetComponent<AudioListener>()
                    : null;
                if (listener == null)
                    listener = UnityEngine.Object.FindFirstObjectByType<AudioListener>();

                if (listener != null && listener.gameObject != _manualCameraObject)
                {
                    _savedAudioListener = listener;
                    _savedAudioListenerEnabled = listener.enabled;
                    listener.enabled = false;
                }
            }

            if (_manualAudioListener == null)
            {
                AudioListener existing = _manualCameraObject.GetComponent<AudioListener>();
                _manualAudioListener = existing != null ? existing : _manualCameraObject.AddComponent<AudioListener>();
            }

            _manualAudioListener.enabled = true;
        }

        private static void RestoreAudioListener()
        {
            if (_manualAudioListener != null)
            {
                UnityEngine.Object.Destroy(_manualAudioListener);
                _manualAudioListener = null;
            }

            if (_savedAudioListener != null)
            {
                _savedAudioListener.enabled = _savedAudioListenerEnabled;
                _savedAudioListener = null;
            }
        }

        private static void SuppressManualPlayerPhysicalControls(PlayerControllerB player)
        {
            if (player == null)
                return;

            player.disableMoveInput = true;
            player.disableLookInput = true;
            player.disableInteract = true;
            player.externalForceAutoFade = Vector3.zero;
            try
            {
                if (player.isCrouching)
                    player.Crouch(crouch: false);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Courier Drone manual crouch suppression skipped: {ex.Message}");
            }

            if (player.playerBodyAnimator != null)
            {
                player.playerBodyAnimator.SetBool("crouching", false);
                player.playerBodyAnimator.SetBool("Walking", false);
                player.playerBodyAnimator.SetBool("Sprinting", false);
                player.playerBodyAnimator.SetBool("Sideways", false);
            }
        }

        private static void ApplyManualPlayerPresentation(PlayerControllerB player)
        {
            if (player == null)
                return;

            FieldOperationsTabletPatch.ReleaseLocalThirdPersonPresentationForDrone();
            SaveManualPlayerPresentation(player);

            SetOriginalPlayerRenderersForDroneView(player);
            player.localArmsMatchCamera = false;

            if (player.playerBodyAnimator != null)
            {
                player.playerBodyAnimator.SetBool("Walking", false);
                player.playerBodyAnimator.SetBool("Sprinting", false);
                player.playerBodyAnimator.SetBool("Sideways", false);
                player.playerBodyAnimator.SetBool("Grab", true);
                player.playerBodyAnimator.SetBool("GrabValidated", true);
            }

        }

        private static void SaveManualPlayerPresentation(PlayerControllerB player)
        {
            if (_manualSavedPresentationState || player == null)
                return;

            _manualSavedPresentationState = true;
            _restoreBodyModelEnabled = player.thisPlayerModel != null && player.thisPlayerModel.enabled;
            _restoreBodyModelLod1Enabled = player.thisPlayerModelLOD1 != null && player.thisPlayerModelLOD1.enabled;
            _restoreBodyModelLod2Enabled = player.thisPlayerModelLOD2 != null && player.thisPlayerModelLOD2.enabled;
            _restoreBodyModelArmsEnabled = player.thisPlayerModelArms != null && player.thisPlayerModelArms.enabled;
            _restoreBodyShadowMode = player.thisPlayerModel != null ? player.thisPlayerModel.shadowCastingMode : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            _restoreBodyLod1ShadowMode = player.thisPlayerModelLOD1 != null ? player.thisPlayerModelLOD1.shadowCastingMode : UnityEngine.Rendering.ShadowCastingMode.Off;
            _restoreBodyLod2ShadowMode = player.thisPlayerModelLOD2 != null ? player.thisPlayerModelLOD2.shadowCastingMode : UnityEngine.Rendering.ShadowCastingMode.Off;
            _restoreLocalArmsMatchCamera = player.localArmsMatchCamera;
            ManualRendererStates.Clear();
            Renderer[] renderers = GetCachedRenderers(player.gameObject, ref _manualPlayerRendererCacheRoot, ref _manualPlayerRendererCache);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                ManualRendererStates.Add(new RendererPresentationState(renderer, renderer.enabled, renderer.shadowCastingMode, renderer.gameObject.layer));
            }

            if (player.playerBodyAnimator != null)
            {
                _restoreAnimatorGrab = player.playerBodyAnimator.GetBool("Grab");
                _restoreAnimatorGrabValidated = player.playerBodyAnimator.GetBool("GrabValidated");
                _manualSavedAnimatorFlags = true;
            }
        }

        private static void RestoreManualPlayerPresentation()
        {
            PlayerControllerB player = ResolvePlayer(_manualControlPlayerId)
                ?? GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;

            if (player == null || !_manualSavedPresentationState)
            {
                ManualRendererStates.Clear();
                _manualSavedPresentationState = false;
                return;
            }

            for (int i = 0; i < ManualRendererStates.Count; i++)
            {
                RendererPresentationState state = ManualRendererStates[i];
                if (state.Renderer == null)
                    continue;

                state.Renderer.enabled = state.Enabled;
                state.Renderer.shadowCastingMode = state.ShadowCastingMode;
                state.Renderer.gameObject.layer = state.Layer;
            }
            ManualRendererStates.Clear();

            SetSkinnedRenderer(player.thisPlayerModel, _restoreBodyModelEnabled, _restoreBodyShadowMode);
            SetSkinnedRenderer(player.thisPlayerModelLOD1, _restoreBodyModelLod1Enabled, _restoreBodyLod1ShadowMode);
            SetSkinnedRenderer(player.thisPlayerModelLOD2, _restoreBodyModelLod2Enabled, _restoreBodyLod2ShadowMode);
            SetSkinnedRenderer(player.thisPlayerModelArms, _restoreBodyModelArmsEnabled, UnityEngine.Rendering.ShadowCastingMode.Off);
            player.localArmsMatchCamera = _restoreLocalArmsMatchCamera;

            if (player.playerBodyAnimator != null)
            {
                if (_manualSavedAnimatorFlags)
                {
                    player.playerBodyAnimator.SetBool("Grab", _restoreAnimatorGrab);
                    player.playerBodyAnimator.SetBool("GrabValidated", _restoreAnimatorGrabValidated);
                }
                player.playerBodyAnimator.SetBool("cancelHolding", false);
            }

            _manualSavedPresentationState = false;
            _manualSavedAnimatorFlags = false;
        }

        private static void SetSkinnedRenderer(SkinnedMeshRenderer renderer, bool enabled, UnityEngine.Rendering.ShadowCastingMode shadowMode)
        {
            if (renderer == null)
                return;

            renderer.enabled = enabled;
            renderer.shadowCastingMode = shadowMode;
        }

        private static void SetOriginalPlayerRenderersForDroneView(PlayerControllerB player)
        {
            if (player == null)
                return;

            int defaultLayer = LayerMask.NameToLayer("Default");
            if (defaultLayer < 0)
                defaultLayer = 0;

            for (int i = 0; i < ManualRendererStates.Count; i++)
            {
                Renderer renderer = ManualRendererStates[i].Renderer;
                if (renderer == null)
                    continue;

                bool firstPersonArms = IsFirstPersonArmsRenderer(player, renderer);
                bool mainBody = player.thisPlayerModel != null && renderer == player.thisPlayerModel;
                bool bodyLod = (player.thisPlayerModelLOD1 != null && renderer == player.thisPlayerModelLOD1)
                    || (player.thisPlayerModelLOD2 != null && renderer == player.thisPlayerModelLOD2);

                if (IsDroneTabletRenderer(renderer))
                {
                    renderer.enabled = true;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    renderer.gameObject.layer = defaultLayer;
                    continue;
                }

                if (firstPersonArms)
                {
                    renderer.enabled = false;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    continue;
                }

                if (mainBody)
                {
                    renderer.enabled = true;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    renderer.gameObject.layer = defaultLayer;
                    continue;
                }

                if (bodyLod)
                {
                    renderer.enabled = false;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    continue;
                }

                renderer.enabled = false;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            SetSkinnedRenderer(player.thisPlayerModel, true, UnityEngine.Rendering.ShadowCastingMode.On);
            SetSkinnedRenderer(player.thisPlayerModelLOD1, false, UnityEngine.Rendering.ShadowCastingMode.Off);
            SetSkinnedRenderer(player.thisPlayerModelLOD2, false, UnityEngine.Rendering.ShadowCastingMode.Off);
            SetSkinnedRenderer(player.thisPlayerModelArms, false, UnityEngine.Rendering.ShadowCastingMode.Off);
            SetFirstPersonArmsRenderersVisible(player, visible: false);
        }

        private static bool IsFirstPersonArmsRenderer(PlayerControllerB player, Renderer renderer)
        {
            if (renderer == null)
                return false;
            if (player != null && player.thisPlayerModelArms != null && renderer == player.thisPlayerModelArms)
                return true;

            Transform current = renderer.transform;
            Transform stop = player != null ? player.transform : null;
            while (current != null && current != stop)
            {
                string name = current.name;
                if (!string.IsNullOrEmpty(name) &&
                    (name.IndexOf("ScavengerModelArmsOnly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("ArmsOnly", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static void SetFirstPersonArmsRenderersVisible(PlayerControllerB player, bool visible)
        {
            if (player == null)
                return;

            for (int i = 0; i < ManualRendererStates.Count; i++)
            {
                Renderer renderer = ManualRendererStates[i].Renderer;
                if (!IsFirstPersonArmsRenderer(player, renderer))
                    continue;

                renderer.enabled = visible;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        private static bool IsDroneTabletRenderer(Renderer renderer)
        {
            if (renderer == null)
                return false;

            Transform current = renderer.transform;
            while (current != null)
            {
                string name = current.name ?? string.Empty;
                if (name.IndexOf("Y4NGZ_DroneTablet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Y4NGZ_FPSTabletProp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Tablet_01", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                current = current.parent;
            }
            return false;
        }

        private static Renderer[] GetCachedDroneRenderers()
        {
            return GetCachedRenderers(_droneVisual, ref _droneRendererCacheRoot, ref _droneRendererCache);
        }

        private static Renderer[] GetCachedRenderers(GameObject root, ref GameObject cacheRoot, ref Renderer[] cache)
        {
            if (root == null)
                return Array.Empty<Renderer>();

            if (cacheRoot != root || cache == null)
            {
                cacheRoot = root;
                cache = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            }

            return cache;
        }

        private static void ClearDroneRendererCache()
        {
            _droneRendererCacheRoot = null;
            _droneRendererCache = null;
            _droneForwardExtent = -1f;
            _carryAnchorCacheRoot = null;
            _carryAnchorCache = null;
        }

        private static void UpdateManualLook()
        {
            Vector2 delta = Gui.UpgradeInput.ReadVanillaVector2("Look");
            _manualYaw += delta.x * MANUAL_MOUSE_SENSITIVITY;
            _manualPitch = Mathf.Clamp(_manualPitch - delta.y * MANUAL_MOUSE_SENSITIVITY, -38f, 42f);
        }

        private static void UpdateManualDroneMovement(PlayerControllerB player)
        {
            if (_droneVisual == null || Time.time < _manualPickupLockedUntil)
                return;

            Vector2 move = Gui.UpgradeInput.ReadVanillaVector2("Move");
            float x = move.x;
            float z = move.y;
            float y = 0f;
            if (Gui.UpgradeInput.IsPressed(Gui.Plugin.Keybinds?.DronePilotAscend)) y += 1f;
            if (Gui.UpgradeInput.IsPressed(Gui.Plugin.Keybinds?.DronePilotDescend)) y -= 1f;

            Quaternion yawRotation = Quaternion.Euler(0f, _manualYaw, 0f);
            Vector3 horizontal = yawRotation * new Vector3(x, 0f, z);
            if (horizontal.sqrMagnitude > 1f)
                horizontal.Normalize();

            Vector3 previous = _droneVisual.transform.position;
            Vector3 desired = previous
                + horizontal * (MANUAL_MOVE_SPEED * Time.deltaTime)
                + Vector3.up * (y * MANUAL_VERTICAL_SPEED * Time.deltaTime);

            desired = ClampManualDronePosition(previous, desired, player);
            _droneVisual.transform.position = desired;
            _droneGroundPosition = ResolveManualGroundPosition(desired);
            if (player != null)
                _droneIsInside = player.isInsideFactory;

            Quaternion desiredRotation = Quaternion.Euler(0f, _manualYaw, 0f);
            _droneVisual.transform.rotation = Quaternion.Slerp(_droneVisual.transform.rotation, desiredRotation, Mathf.Clamp01(Time.deltaTime * VISUAL_TURN_SPEED));
            UpdateCarriedItemTransforms();
        }

        private static Vector3 ClampManualDronePosition(Vector3 current, Vector3 requested, PlayerControllerB player)
        {
            Vector3 desired = requested;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            Vector3 delta = desired - current;
            float distance = delta.magnitude;
            if (distance > 0.001f && Physics.SphereCast(current, MANUAL_COLLISION_RADIUS, delta.normalized, out RaycastHit hit, distance + 0.05f, mask, QueryTriggerInteraction.Ignore))
                desired = current + delta.normalized * Mathf.Max(0f, hit.distance - 0.08f);

            float groundY = _droneGroundPosition != Vector3.zero
                ? _droneGroundPosition.y
                : player != null && player.transform != null
                    ? player.transform.position.y
                    : desired.y;
            Vector3 probe = desired + Vector3.up * 2.25f;
            if (Physics.Raycast(probe, Vector3.down, out RaycastHit groundHit, 9f, mask, QueryTriggerInteraction.Ignore))
                groundY = groundHit.point.y;

            desired.y = Mathf.Clamp(desired.y, groundY + MANUAL_MIN_GROUND_HEIGHT, groundY + MANUAL_MAX_GROUND_HEIGHT);

            StartOfRound round = StartOfRound.Instance;
            if (player != null && IsPlayerInShip(player) && round != null && round.shipInnerRoomBounds != null)
            {
                Bounds bounds = round.shipInnerRoomBounds.bounds;
                desired.x = ClampInsideBounds(desired.x, bounds.min.x, bounds.max.x, SHIP_DRONE_EDGE_MARGIN);
                desired.z = ClampInsideBounds(desired.z, bounds.min.z, bounds.max.z, SHIP_DRONE_EDGE_MARGIN);
                desired.y = Mathf.Clamp(desired.y, bounds.min.y + MANUAL_MIN_GROUND_HEIGHT, bounds.max.y - 0.35f);
            }

            return desired;
        }

        private static Vector3 ResolveManualGroundPosition(Vector3 dronePosition)
        {
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (Physics.Raycast(dronePosition + Vector3.up * 0.8f, Vector3.down, out RaycastHit hit, 8f, mask, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.05f;

            return _droneGroundPosition != Vector3.zero ? _droneGroundPosition : Vector3.zero;
        }

        private static void UpdateManualCameraTransform()
        {
            if (_manualCamera == null || _droneVisual == null)
                return;

            Quaternion rotation = Quaternion.Euler(_manualPitch, _manualYaw, 0f);
            Vector3 yawForward = Quaternion.Euler(0f, _manualYaw, 0f) * Vector3.forward;
            Vector3 cameraPosition = CalculateManualCameraPosition(_droneVisual.transform.position, yawForward);
            _manualCamera.nearClipPlane = 0.045f;
            _manualCamera.transform.position = cameraPosition;
            _manualCamera.transform.rotation = rotation;
        }

        private static Vector3 CalculateManualCameraPosition(Vector3 dronePosition, Vector3 yawForwardInput)
        {
            Vector3 yawForward = yawForwardInput;
            if (yawForward.sqrMagnitude <= 0.001f)
                yawForward = Vector3.forward;
            yawForward.Normalize();

            // F-DRONE-9: the nose offset used to be re-measured from a fresh Vector3[8] per renderer
            // per frame while piloting. The mesh does not change while the drone lives, so measure it
            // once per drone (the cache is dropped alongside the renderer cache).
            if (_droneForwardExtent < 0f)
                _droneForwardExtent = CalculateDroneForwardExtent(dronePosition, yawForward);

            return dronePosition + yawForward * _droneForwardExtent + Vector3.up * 0.11f;
        }

        private static float CalculateDroneForwardExtent(Vector3 dronePosition, Vector3 yawForward)
        {
            Renderer[] renderers = GetCachedDroneRenderers();
            if (renderers == null || renderers.Length == 0)
                return 0.62f;

            Vector3 direction = yawForward;
            float furthest = 0f;
            bool measured = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;

                // max over the eight AABB corners of dot(corner, direction), without building them.
                Bounds bounds = renderer.bounds;
                Vector3 extents = bounds.extents;
                Vector3 centerOffset = bounds.center - dronePosition;
                Vector3 absExtentProjection = new Vector3(
                    Mathf.Abs(extents.x * direction.x),
                    Mathf.Abs(extents.y * direction.y),
                    Mathf.Abs(extents.z * direction.z));
                float reach = Vector3.Dot(centerOffset, direction)
                    + absExtentProjection.x + absExtentProjection.y + absExtentProjection.z;
                furthest = Mathf.Max(furthest, reach);
                measured = true;
            }

            return measured ? Mathf.Clamp(furthest + MANUAL_CAMERA_CLEARANCE, 0.68f, 1.75f) : 0.62f;
        }

        private static void CreateManualHud()
        {
            DestroyManualHud();

            _manualHudRoot = new GameObject("Y4NGZ_CourierDroneManualHUD");
            Canvas canvas = _manualHudRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            CanvasScaler scaler = _manualHudRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _manualHudRoot.AddComponent<GraphicRaycaster>();

            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _manualHudStatusText = CreateManualHudText("Status", _manualHudRoot.transform, font, new Vector2(24f, -24f), new Vector2(0f, 1f), TextAnchor.UpperLeft, 24, new Color(0.73f, 1f, 0.96f, 0.96f));
            _manualHudCargoText = CreateManualHudText("Cargo", _manualHudRoot.transform, font, new Vector2(-24f, -24f), new Vector2(1f, 1f), TextAnchor.UpperRight, 22, new Color(0.92f, 0.98f, 1f, 0.94f));
            _manualHudTelemetryText = CreateManualHudText("Telemetry", _manualHudRoot.transform, font, new Vector2(24f, 24f), new Vector2(0f, 0f), TextAnchor.LowerLeft, 20, new Color(0.72f, 0.95f, 1f, 0.88f));
            _manualHudControlsText = CreateManualHudText("Controls", _manualHudRoot.transform, font, new Vector2(0f, 30f), new Vector2(0.5f, 0f), TextAnchor.LowerCenter, 19, new Color(0.92f, 0.98f, 1f, 0.78f));
            _manualHudControlsText.text = BuildManualControlLegend();
            // The status line never changes while the link is up, so it is written once (F-DRONE-9).
            _manualHudStatusText.text = "COURIER DRONE LINK\nREMOTE LINK ACTIVE";

            CreateManualHudImage("ReticleH", _manualHudRoot.transform, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(74f, 2f), new Color(0.2f, 1f, 0.92f, 0.72f));
            CreateManualHudImage("ReticleV", _manualHudRoot.transform, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(2f, 74f), new Color(0.2f, 1f, 0.92f, 0.72f));
            CreateManualHudImage("ReticleCore", _manualHudRoot.transform, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(8f, 8f), new Color(1f, 1f, 1f, 0.88f));

            CreateManualHudImage("TopBand", _manualHudRoot.transform, new Vector2(0f, 0f), new Vector2(0.5f, 1f), new Vector2(1920f, 4f), new Color(0.13f, 0.95f, 0.85f, 0.55f));
            CreateManualHudImage("BottomBand", _manualHudRoot.transform, new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(1920f, 4f), new Color(0.13f, 0.95f, 0.85f, 0.55f));
            CreateManualHudImage("LeftTick", _manualHudRoot.transform, new Vector2(42f, 0f), new Vector2(0f, 0.5f), new Vector2(3f, 220f), new Color(0.13f, 0.95f, 0.85f, 0.35f));
            CreateManualHudImage("RightTick", _manualHudRoot.transform, new Vector2(-42f, 0f), new Vector2(1f, 0.5f), new Vector2(3f, 220f), new Color(0.13f, 0.95f, 0.85f, 0.35f));

            CreateManualStartupOverlay(font);
            UpdateManualHud();
        }

        private static Text CreateManualHudText(string name, Transform parent, Font font, Vector2 anchoredPosition, Vector2 anchor, TextAnchor alignment, int size, Color color)
        {
            GameObject textObject = new GameObject("DroneHUD_" + name);
            textObject.transform.SetParent(parent, worldPositionStays: false);
            Text text = textObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            RectTransform rect = text.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(680f, 128f);
            return text;
        }

        private static Image CreateManualHudImage(string name, Transform parent, Vector2 anchoredPosition, Vector2 anchor, Vector2 size, Color color)
        {
            GameObject imageObject = new GameObject("DroneHUD_" + name);
            imageObject.transform.SetParent(parent, worldPositionStays: false);
            Image image = imageObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            RectTransform rect = image.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            return image;
        }

        private static void CreateManualStartupOverlay(Font font)
        {
            if (_manualHudRoot == null)
                return;

            _manualHudStartupRoot = new GameObject("DroneHUD_StartupOverlay");
            _manualHudStartupRoot.transform.SetParent(_manualHudRoot.transform, worldPositionStays: false);
            RectTransform rootRect = _manualHudStartupRoot.AddComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            Image background = _manualHudStartupRoot.AddComponent<Image>();
            background.color = new Color(0.005f, 0.035f, 0.04f, 0.94f);
            background.raycastTarget = false;

            CreateManualHudImage("StartupFrameTop", _manualHudStartupRoot.transform, new Vector2(0f, 92f), new Vector2(0.5f, 0.5f), new Vector2(700f, 3f), new Color(0.12f, 1f, 0.92f, 0.62f));
            CreateManualHudImage("StartupFrameBottom", _manualHudStartupRoot.transform, new Vector2(0f, -92f), new Vector2(0.5f, 0.5f), new Vector2(700f, 3f), new Color(0.12f, 1f, 0.92f, 0.62f));
            CreateManualHudImage("StartupBarBack", _manualHudStartupRoot.transform, new Vector2(0f, -54f), new Vector2(0.5f, 0.5f), new Vector2(620f, 12f), new Color(0.06f, 0.28f, 0.29f, 0.90f));
            _manualHudStartupProgressImage = CreateManualHudImage("StartupBarFill", _manualHudStartupRoot.transform, new Vector2(-310f, -54f), new Vector2(0.5f, 0.5f), new Vector2(0f, 8f), new Color(0.16f, 1f, 0.88f, 0.96f));
            RectTransform progressRect = _manualHudStartupProgressImage.GetComponent<RectTransform>();
            progressRect.pivot = new Vector2(0f, 0.5f);

            _manualHudStartupText = CreateManualHudText("StartupText", _manualHudStartupRoot.transform, font, new Vector2(0f, 18f), new Vector2(0.5f, 0.5f), TextAnchor.MiddleCenter, 40, new Color(0.76f, 1f, 0.96f, 0.98f));
            RectTransform textRect = _manualHudStartupText.GetComponent<RectTransform>();
            textRect.sizeDelta = new Vector2(900f, 150f);
            _manualHudStartupRoot.SetActive(false);
        }

        /// <summary>
        /// F-DRONE-9: this runs every frame while piloting and used to build three interpolated
        /// strings each time. The status line is constant, and the other two only change when the
        /// underlying numbers do, so they are rebuilt on change and reused otherwise.
        /// </summary>
        private static void UpdateManualHud()
        {
            if (_manualHudRoot == null)
                return;

            float cargoPounds = GetCarriedCargoWeightPounds();
            int cargoPoundsTenths = Mathf.RoundToInt(cargoPounds * 10f);
            int health = Mathf.Clamp(_droneHealth, 0, DRONE_MAX_HEALTH);
            if (_manualHudCargoText != null
                && (_manualHudCargoCache == null
                    || cargoPoundsTenths != _manualHudCargoPoundsTenths
                    || CarriedItems.Count != _manualHudCargoCount
                    || health != _manualHudHealth))
            {
                _manualHudCargoPoundsTenths = cargoPoundsTenths;
                _manualHudCargoCount = CarriedItems.Count;
                _manualHudHealth = health;
                _manualHudCargoCache = $"CARGO {FormatCargoWeight(cargoPounds)} ({CarriedItems.Count})\nHP {health}/{DRONE_MAX_HEALTH}";
                _manualHudCargoText.text = _manualHudCargoCache;
            }

            float altitude = _droneVisual != null
                ? Mathf.Max(0f, _droneVisual.transform.position.y - _droneGroundPosition.y)
                : 0f;
            int altitudeTenths = Mathf.RoundToInt(altitude * 10f);
            int insideFlag = _droneIsInside ? 1 : 0;
            if (_manualHudTelemetryText != null
                && (_manualHudTelemetryCache == null
                    || altitudeTenths != _manualHudAltitudeTenths
                    || insideFlag != _manualHudInsideFlag))
            {
                _manualHudAltitudeTenths = altitudeTenths;
                _manualHudInsideFlag = insideFlag;
                _manualHudTelemetryCache = $"ALT {altitude:0.0}m\nMODE {(_droneIsInside ? "INTERIOR" : "EXTERIOR")}";
                _manualHudTelemetryText.text = _manualHudTelemetryCache;
            }

            UpdateManualStartupOverlay();
        }

        private static void ResetManualHudTextCache()
        {
            _manualHudCargoCache = null;
            _manualHudTelemetryCache = null;
            _manualHudCargoCount = int.MinValue;
            _manualHudCargoPoundsTenths = int.MinValue;
            _manualHudHealth = int.MinValue;
            _manualHudAltitudeTenths = int.MinValue;
            _manualHudInsideFlag = int.MinValue;
        }

        private static string BuildManualControlLegend()
        {
            Gui.IngameKeybinds keybinds = Gui.Plugin.Keybinds;
            string altitude = Gui.UpgradeInput.DisplayPair(
                keybinds?.DronePilotAscend, "SPACE", keybinds?.DronePilotDescend, "CTRL");
            string flame = Gui.UpgradeInput.DisplayLabel(keybinds?.DronePilotFlame, "LMB");
            string grenade = Gui.UpgradeInput.DisplayLabel(keybinds?.DronePilotGrenade, "RMB");
            string lift = Gui.UpgradeInput.DisplayLabel(keybinds?.DronePilotLift, "E");
            string light = Gui.UpgradeInput.DisplayLabel(keybinds?.DronePilotLight, "F");
            string exit = Gui.UpgradeInput.DisplayLabel(keybinds?.DronePilotExit, "ESC");
            return $"VANILLA MOVE/LOOK   [{altitude}] ALT   [{flame}] FLAME   "
                + $"[{grenade}] GRENADE   [{lift}] LIFT   [{light}] LIGHT   [{exit}] EXIT";
        }

        private static void UpdateManualStartupOverlay()
        {
            if (_manualHudStartupRoot == null)
                return;

            bool booting = _manualControlCameraActivated && Time.unscaledTime < _manualDroneStartupUntil;
            if (_manualHudStartupRoot.activeSelf != booting)
                _manualHudStartupRoot.SetActive(booting);
            if (!booting)
                return;

            float remaining = Mathf.Max(0f, _manualDroneStartupUntil - Time.unscaledTime);
            float progress = Mathf.Clamp01(1f - remaining / MANUAL_DRONE_BOOT_SCREEN_SECONDS);
            if (_manualHudStartupProgressImage != null)
            {
                RectTransform rect = _manualHudStartupProgressImage.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(Mathf.Lerp(96f, 620f, progress), 8f);
            }

            if (_manualHudStartupText != null)
            {
                _manualHudStartupText.text = progress < 0.58f
                    ? "COURIER DRONE\nLINK INITIALIZING"
                    : "COURIER DRONE\nFEED SYNCING";
            }
        }

        private static void DestroyManualHud()
        {
            if (_manualHudRoot == null)
                return;

            UnityEngine.Object.Destroy(_manualHudRoot);
            _manualHudRoot = null;
            _manualHudStatusText = null;
            _manualHudCargoText = null;
            _manualHudTelemetryText = null;
            _manualHudControlsText = null;
            _manualHudStartupRoot = null;
            _manualHudStartupText = null;
            _manualHudStartupProgressImage = null;
            ResetManualHudTextCache();
        }
    }
}
