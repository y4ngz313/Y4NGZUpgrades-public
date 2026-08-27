using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Y4NGZInteractions.InteractionAnimationApi;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Upgrades;

#pragma warning disable Harmony003

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class PanicSlidePatch
    {
        private const string MSG_SLIDE_START = "Y4NGZ_SlideStart";
        private const string MSG_SLIDE_STOP = "Y4NGZ_SlideStop";
        private const float STARTING_SECONDS = 0.833f;
        private const float MOVEMENT_LOOK_YAW_DEGREES_PER_SECOND = 300f;
        private const float MOVEMENT_LOOK_PITCH_DEGREES_PER_SECOND = 220f;
        private const float REMOTE_SLIDE_GROUND_RAY_START_METERS = 1.5f;
        private const float REMOTE_SLIDE_GROUND_RAY_DISTANCE_METERS = 4f;
        private const float REMOTE_SLIDE_GROUND_CLEARANCE_METERS = 0.02f;
        private const float REMOTE_SLIDE_NORMAL_RESPONSE = 14f;

        private enum SlideState
        {
            None,
            Starting,
            Sliding,
            Exiting,
        }

        private struct RendererPresentationState
        {
            internal Renderer Renderer;
            internal bool Enabled;
            internal ShadowCastingMode ShadowMode;
            internal int Layer;
            internal bool Valid;

            internal RendererPresentationState(Renderer renderer)
            {
                Renderer = renderer;
                Enabled = renderer != null && renderer.enabled;
                ShadowMode = renderer != null ? renderer.shadowCastingMode : ShadowCastingMode.Off;
                Layer = renderer != null && renderer.gameObject != null ? renderer.gameObject.layer : 0;
                Valid = renderer != null;
            }
        }

        private static float _cooldownEnd;
        private static bool _handlersRegistered;
        private static readonly Dictionary<ulong, RemoteSlideTerrainPresentation> RemoteSlidePresentations =
            new Dictionary<ulong, RemoteSlideTerrainPresentation>();

        private static SlideState _localSlideState;
        private static PlayerControllerB _localSlidePlayer;
        private static Vector3 _slideDirection;
        private static float _slideStartedAt;
        private static float _slideExitStartedAt;
        private static float _airborneSeconds;
        private static bool _stopSent;
        private static bool _slideForcedCrouch;

        private static PlayerControllerB _lookLimitedPlayer;
        private static bool _lookLimiterInitialized;
        private static float _lastLimitedBodyYaw;
        private static float _lastLimitedCameraPitch;
        private static bool _cameraUpFieldResolved;
        private static FieldInfo _cameraUpField;

        private static Transform _cameraOffsetTransform;
        private static Vector3 _cameraBaseLocalPosition;
        private static float _cameraAppliedOffset;
        private static bool _cameraOffsetApplied;
        private static bool _cameraRotationApplied;

        private static PlayerControllerB _armsPresentationPlayer;
        private static bool _armsPresentationSaved;
        private static bool _savedArmsRendererEnabled;
        private static bool _savedLocalArmsMatchCamera;
        private static bool _movementRenderHookSubscribed;
        // Vanilla gameplay-camera local position, captured at spawn BEFORE First-Person View's
        // first Tick moves the camera. FPV keeps its own copy of the same baseline; during a
        // movement session we re-anchor the camera to this so dip/roll compose from the vanilla
        // baseline with or without FPV.
        private static bool _vanillaCameraBaseCaptured;
        private static Vector3 _vanillaCameraBaseLocal;

        private static PlayerControllerB _presentationPlayer;
        private static RendererPresentationState _savedPlayerModelState;
        private static RendererPresentationState _savedPlayerModelLod1State;
        private static RendererPresentationState _savedPlayerModelLod2State;
        private static bool _presentationLayerRedirectActive;
        private static int _presentationVisibleLayer = -1;
        private static bool _presentationSaved;

        private static bool _jumpMemberResolved;
        private static FieldInfo _isJumpingField;
        private static PropertyInfo _isJumpingProperty;

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            RegisterNetworkHandlers();
            _cooldownEnd = 0f;
            ForceEndLocalSlide("start-game", sendStop: false);
            SlideAnimationBridge.EndAll("start-game");
            ClearRemoteSlideTerrainPresentations("start-game");
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            ForceEndLocalSlide("end-of-game", sendStop: true);
            SlideAnimationBridge.EndAll("end-of-game");
            ClearRemoteSlideTerrainPresentations("end-of-game");
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectCaptureCameraBase(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance) || __instance.gameplayCamera == null)
                return;

            // This runs before any LateUpdate, so the camera local position is still vanilla.
            _vanillaCameraBaseLocal = __instance.gameplayCamera.transform.localPosition;
            _vanillaCameraBaseCaptured = true;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            RegisterNetworkHandlers();
            UpdateLocalSlide(__instance);
            SlideAnimationBridge.Tick();
        }

        // Priority -100: run after First-Person View's priority-0 LateUpdate postfix so the slide
        // camera treatment and arms presentation are the frame's final writes (see LedgeMantlePatch).
        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        [HarmonyPriority(-100)]
        private static void PostPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (__instance == null)
                return;

            if (!IsLocalPlayer(__instance))
            {
                ApplyRemoteSlideTerrainConformance(__instance);
                return;
            }

            if (__instance == _localSlidePlayer && _localSlideState != SlideState.None)
            {
                ReapplyFirstPersonMovementPresentation(__instance);
                ApplySlideCameraTreatment(__instance);
                ApplyMovementLookLimit(__instance);
                return;
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Crouch_performed")]
        [HarmonyPrefix]
        private static bool PreCrouchPerformed(PlayerControllerB __instance, InputAction.CallbackContext context)
        {
            if (!context.performed)
                return true;

            if (IsLocalPlayer(__instance) && IsLocalSlideActive())
            {
                StartLocalExit("crouch-cancel");
                return false;
            }

            if (!IsLocalPlayer(__instance) || !PanicSlideUpgrade.IsUnlocked())
                return true;

            if (!CanSlide(__instance))
                return true;

            TrySlide(__instance);
            return false;
        }

        private static bool CanSlide(PlayerControllerB player)
        {
            if (player == null || player.isPlayerDead)
                return false;
            if (_localSlideState != SlideState.None)
                return false;
            if (!player.isSprinting || player.isCrouching)
                return false;
            if (player.thisController == null || !player.thisController.isGrounded)
                return false;
            if (player.sprintMeter < PanicSlideUpgrade.MIN_SPRINT_METER)
                return false;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return false;
            if (player.isTypingChat || player.inTerminalMenu || player.inSpecialInteractAnimation)
                return false;
            return true;
        }

        private static void TrySlide(PlayerControllerB player)
        {
            if (Time.time < _cooldownEnd)
                return;

            Vector3 forward = ResolveCameraForward(player);

            // Escape Artist is a direct controller owner. End the API session through its own
            // restore path before snapshotting vanilla state for the slide.
            if (LCInteractionAnimationAPI.TryGetActiveInteraction(
                    player,
                    InteractionAnimationPresentationKind.BodyWorld,
                    out InteractionAnimationHandle activeBodyHandle))
            {
                LCInteractionAnimationAPI.TryStopInteraction(
                    activeBodyHandle,
                    InteractionAnimationStopReason.Interrupted);
            }
            if (LCInteractionAnimationAPI.TryGetActiveInteraction(
                    player,
                    InteractionAnimationPresentationKind.DedicatedLocalViewmodel,
                    out InteractionAnimationHandle activeViewmodelHandle))
            {
                LCInteractionAnimationAPI.TryStopInteraction(
                    activeViewmodelHandle,
                    InteractionAnimationStopReason.Interrupted);
            }
            if (LCInteractionAnimationAPI.TryGetActiveInteraction(
                    player,
                    InteractionAnimationPresentationKind.BodyWorld,
                    out _) ||
                LCInteractionAnimationAPI.TryGetActiveInteraction(
                    player,
                    InteractionAnimationPresentationKind.DedicatedLocalViewmodel,
                    out _))
            {
                return;
            }

            if (!SlideAnimationBridge.Begin(player))
                return;

            BeginLocalAnimatedSlide(player, forward);

            // Use vanilla crouch as the gameplay posture for the entire slide. Besides setting
            // the replicated crouch state, PlayerControllerB.Update shrinks the CharacterController
            // from 2.5 m to 1.5 m, so the slide can pass anywhere vanilla crouch can. Vanilla's
            // Crouch(false) also refuses to stand beneath an obstruction, which gives us a safe
            // exit without inventing a second clearance system.
            player.Crouch(crouch: true);
            _slideForcedCrouch = player.isCrouching;
            if (!_slideForcedCrouch)
            {
                ForceEndLocalSlide("crouch-rejected", sendStop: false);
                return;
            }

            float weightMultiplier = Mathf.Clamp(1.15f - (player.carryWeight - 1f) * 0.12f, 0.55f, 1.15f);
            player.externalForceAutoFade += forward * PanicSlideUpgrade.SLIDE_FORCE * weightMultiplier;
            player.sprintMeter = Mathf.Clamp01(player.sprintMeter - PanicSlideUpgrade.STAMINA_COST);
            if (player.sprintMeterUI != null)
                player.sprintMeterUI.fillAmount = player.sprintMeter;

            _cooldownEnd = Time.time + PanicSlideUpgrade.COOLDOWN_SECONDS;
            HUDManager.Instance?.ShakeCamera(ScreenShakeType.Small);
            SendSlideStart(player.playerClientId);
        }

        private static void BeginLocalAnimatedSlide(PlayerControllerB player, Vector3 direction)
        {
            RestoreCameraOffset();

            _localSlidePlayer = player;
            _localSlideState = SlideState.Starting;
            _slideDirection = direction.sqrMagnitude > 0.001f ? direction.normalized : player.transform.forward;
            _slideStartedAt = Time.time;
            _slideExitStartedAt = 0f;
            _airborneSeconds = 0f;
            _stopSent = false;
            BeginMovementLookLimit(player);
            // Body-visible FP slide: the body cam clips keep the head pinned to the eye anchor.
            BeginFirstPersonMovementPresentation(player, showBody: true);
        }

        private static void UpdateLocalSlide(PlayerControllerB player)
        {
            if (_localSlideState == SlideState.None)
            {
                RestoreCameraOffset();
                return;
            }

            if (player == null || player != _localSlidePlayer)
            {
                ForceEndLocalSlide("player-changed", sendStop: true);
                return;
            }

            if (player.isPlayerDead)
            {
                ForceEndLocalSlide("player-dead", sendStop: true);
                return;
            }

            if (_localSlideState == SlideState.Exiting)
            {
                if (Time.time - _slideExitStartedAt >= PanicSlideUpgrade.SLIDE_EXIT_ANIMATION_SECONDS)
                    FinishLocalSlideState();
                return;
            }

            float elapsed = Time.time - _slideStartedAt;
            if (_localSlideState == SlideState.Starting && elapsed >= STARTING_SECONDS)
                _localSlideState = SlideState.Sliding;

            if (ShouldExitSlide(player, elapsed, out string reason))
            {
                // Vanilla special interactions own this same Animator. Do not play our graceful
                // exit on top of a lever/ladder/terminal animation; hand ownership back now.
                if (string.Equals(reason, "special-interact", StringComparison.Ordinal))
                {
                    ForceEndLocalSlide(reason, sendStop: true);
                    return;
                }

                StartLocalExit(reason);
                return;
            }

            UpdateSlideDirection(player);
            ApplySustainForce(player, elapsed);

            if (elapsed >= PanicSlideUpgrade.SLIDE_DURATION_SECONDS)
                StartLocalExit("duration");
        }

        private static bool ShouldExitSlide(PlayerControllerB player, float elapsed, out string reason)
        {
            reason = null;

            if (player == null || player.isPlayerDead)
            {
                reason = "player-dead";
                return true;
            }

            if (player.inSpecialInteractAnimation)
            {
                reason = "special-interact";
                return true;
            }

            if (IsPlayerJumping(player))
            {
                reason = "jump";
                return true;
            }

            bool grounded = player.thisController != null && player.thisController.isGrounded;
            if (grounded)
            {
                _airborneSeconds = 0f;
            }
            else
            {
                _airborneSeconds += Time.deltaTime;
                if (_airborneSeconds > PanicSlideUpgrade.SLIDE_AIRBORNE_CANCEL_SECONDS)
                {
                    reason = "airborne";
                    return true;
                }
            }

            if (elapsed > PanicSlideUpgrade.SLIDE_MIN_SPEED_GRACE_SECONDS &&
                GetHorizontalSpeed(player) < PanicSlideUpgrade.SLIDE_MIN_HORIZONTAL_SPEED)
            {
                reason = "low-speed";
                return true;
            }

            return false;
        }

        private static bool IsPlayerJumping(PlayerControllerB player)
        {
            if (player == null)
                return false;

            ResolveJumpMember();
            try
            {
                if (_isJumpingField != null && _isJumpingField.GetValue(player) is bool fieldValue)
                    return fieldValue;
                if (_isJumpingProperty != null && _isJumpingProperty.GetValue(player, null) is bool propertyValue)
                    return propertyValue;
            }
            catch { }

            return false;
        }

        private static void ResolveJumpMember()
        {
            if (_jumpMemberResolved)
                return;

            _jumpMemberResolved = true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _isJumpingField = typeof(PlayerControllerB).GetField("isJumping", flags);
            _isJumpingProperty = typeof(PlayerControllerB).GetProperty("isJumping", flags);
        }

        private static void UpdateSlideDirection(PlayerControllerB player)
        {
            Vector3 target = ResolveCameraForward(player);
            if (target.sqrMagnitude < 0.001f)
                return;

            float maxRadians = PanicSlideUpgrade.SLIDE_STEERING_DEGREES_PER_SECOND * Mathf.Deg2Rad * Time.deltaTime;
            _slideDirection = Vector3.RotateTowards(_slideDirection, target, maxRadians, 0f).normalized;
        }

        private static void ApplySustainForce(PlayerControllerB player, float elapsed)
        {
            if (player == null || _slideDirection.sqrMagnitude < 0.001f)
                return;

            float fade = Mathf.Clamp01(1f - elapsed / PanicSlideUpgrade.SLIDE_DURATION_SECONDS);
            player.externalForceAutoFade += _slideDirection * (PanicSlideUpgrade.SLIDE_SUSTAIN_FORCE * fade * Time.deltaTime);
        }

        private static void StartLocalExit(string reason)
        {
            if (_localSlideState == SlideState.None)
                return;

            _localSlideState = SlideState.Exiting;
            _slideExitStartedAt = Time.time;
            _airborneSeconds = 0f;

            ClearSlideHorizontalMomentum(_localSlidePlayer);

            if (_localSlidePlayer != null)
                SlideAnimationBridge.TriggerExit(_localSlidePlayer);

            if (!_stopSent && _localSlidePlayer != null)
            {
                _stopSent = true;
                SendSlideStop(_localSlidePlayer.playerClientId);
            }
        }

        private static void FinishLocalSlideState()
        {
            PlayerControllerB player = _localSlidePlayer;
            RestoreCameraOffset();
            EndFirstPersonMovementPresentation();

            ClearSlideHorizontalMomentum(player);
            if (_slideForcedCrouch && player != null && player.isCrouching)
            {
                // If there is not enough room, vanilla intentionally leaves isCrouching true.
                player.Crouch(crouch: false);
            }

            _localSlideState = SlideState.None;
            _localSlidePlayer = null;
            _slideDirection = Vector3.zero;
            _slideStartedAt = 0f;
            _slideExitStartedAt = 0f;
            _airborneSeconds = 0f;
            _stopSent = false;
            _slideForcedCrouch = false;
            EndMovementLookLimit(player);
        }

        internal static void BeginMovementLookLimit(PlayerControllerB player)
        {
            _lookLimitedPlayer = player;
            _lookLimiterInitialized = false;
            if (player == null || player.gameplayCamera == null)
                return;

            Transform body = player.thisPlayerBody != null ? player.thisPlayerBody : player.transform;
            _lastLimitedBodyYaw = body != null ? body.eulerAngles.y : 0f;
            _lastLimitedCameraPitch = ReadVanillaCameraPitch(player);
            _lookLimiterInitialized = true;
        }

        // Vanilla's look-pitch accumulator (cameraUp), NOT the camera transform's current euler:
        // this postfix runs at priority -100, after Camera_Overhaul's priority-0 tilt pass, so
        // the transform euler carries CO's per-frame visual offset. Reading that and writing it
        // back into cameraUp integrates CO's offset every frame — a runaway pitch dive that
        // pinned the slide view at the ground clamp. cameraUp only ever contains vanilla input.
        private static float ReadVanillaCameraPitch(PlayerControllerB player)
        {
            ResolveCameraUpField();
            if (_cameraUpField != null)
            {
                try
                {
                    if (_cameraUpField.GetValue(player) is float pitch)
                        return NormalizeSignedAngle(pitch);
                }
                catch { }
            }

            return player.gameplayCamera != null
                ? NormalizeSignedAngle(player.gameplayCamera.transform.localEulerAngles.x)
                : 0f;
        }

        internal static void ApplyMovementLookLimit(PlayerControllerB player)
        {
            if (player == null || player.gameplayCamera == null || player != _lookLimitedPlayer)
                return;
            // The debug third-person cam (Y4NGZDebugTools, priority 0 — before us) poses the
            // camera itself; at priority -100 writing pitch here would fight it every frame.
            if (LedgeMantlePatch.IsDebugThirdPersonCamActive())
                return;
            if (!_lookLimiterInitialized)
            {
                BeginMovementLookLimit(player);
                return;
            }

            float deltaTime = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            Transform body = player.thisPlayerBody != null ? player.thisPlayerBody : player.transform;
            if (body != null)
            {
                float currentYaw = body.eulerAngles.y;
                float maxYawDelta = MOVEMENT_LOOK_YAW_DEGREES_PER_SECOND * deltaTime;
                float limitedYaw = _lastLimitedBodyYaw + Mathf.Clamp(
                    Mathf.DeltaAngle(_lastLimitedBodyYaw, currentYaw),
                    -maxYawDelta,
                    maxYawDelta);
                Vector3 bodyEuler = body.eulerAngles;
                bodyEuler.y = limitedYaw;
                body.eulerAngles = bodyEuler;
                _lastLimitedBodyYaw = limitedYaw;
            }

            Transform camera = player.gameplayCamera.transform;
            float currentPitch = ReadVanillaCameraPitch(player);
            float maxPitchDelta = MOVEMENT_LOOK_PITCH_DEGREES_PER_SECOND * deltaTime;
            float limitedPitch = _lastLimitedCameraPitch + Mathf.Clamp(
                Mathf.DeltaAngle(_lastLimitedCameraPitch, currentPitch),
                -maxPitchDelta,
                maxPitchDelta);
            limitedPitch = Mathf.Clamp(limitedPitch, -80f, 80f);
            Vector3 cameraEuler = camera.localEulerAngles;
            cameraEuler.x = limitedPitch;
            camera.localEulerAngles = cameraEuler;
            _lastLimitedCameraPitch = limitedPitch;

            ResolveCameraUpField();
            if (_cameraUpField != null)
            {
                try { _cameraUpField.SetValue(player, limitedPitch); } catch { }
            }
        }

        internal static void EndMovementLookLimit(PlayerControllerB player)
        {
            if (player != null && _lookLimitedPlayer != null && player != _lookLimitedPlayer)
                return;

            _lookLimitedPlayer = null;
            _lookLimiterInitialized = false;
            _lastLimitedBodyYaw = 0f;
            _lastLimitedCameraPitch = 0f;
        }

        private static float NormalizeSignedAngle(float angle)
        {
            return Mathf.DeltaAngle(0f, angle);
        }

        private static void ResolveCameraUpField()
        {
            if (_cameraUpFieldResolved)
                return;

            _cameraUpFieldResolved = true;
            _cameraUpField = typeof(PlayerControllerB).GetField(
                "cameraUp",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static void ClearSlideHorizontalMomentum(PlayerControllerB player)
        {
            if (player == null)
                return;

            // Panic Slide injects only horizontal externalForceAutoFade. Clearing that plane at
            // exit prevents the long exponential tail that caused input-free gliding, while
            // retaining vertical knockback/fall impulses owned by the game or other systems.
            Vector3 force = player.externalForceAutoFade;
            force.x = 0f;
            force.z = 0f;
            player.externalForceAutoFade = force;
        }

        private static void ForceEndLocalSlide(string reason, bool sendStop)
        {
            PlayerControllerB player = _localSlidePlayer;
            if (sendStop && !_stopSent && player != null)
                SendSlideStop(player.playerClientId);

            if (player != null)
                SlideAnimationBridge.End(player, reason);

            FinishLocalSlideState();
        }

        private static void ApplySlideCameraTreatment(PlayerControllerB player)
        {
            float envelope = CalculateSlideCameraEnvelope();
            ApplyFirstPersonMovementCamera(
                player,
                PanicSlideUpgrade.SLIDE_CAMERA_DIP * envelope,
                -PanicSlideUpgrade.SLIDE_CAMERA_ROLL_DEGREES * envelope);
        }

        internal static void ApplyFirstPersonMovementCamera(PlayerControllerB player, float dip, float rollDegrees)
        {
            if (player == null || player.gameplayCamera == null)
                return;

            // The debug third-person cam (Y4NGZDebugTools, priority 0 — before us) poses the
            // camera itself; at priority -100 our reset/dip/roll would yank it back toward first
            // person every frame. Historically (priority 400) it simply overwrote us — skipping
            // reproduces that composition.
            if (LedgeMantlePatch.IsDebugThirdPersonCamActive())
            {
                RestoreCameraOffset();
                ReapplyFirstPersonArmsPresentation(player);
                return;
            }

            Transform cameraTransform = player.gameplayCamera.transform;
            // First-Person View's eye-anchor follow (applied before us at priority 0) does not
            // ride the head through our authored clips: at slide start it dives toward the
            // blending head pose (clamped 0.5 m), which stacked under our dip plunged the view
            // into the ground. During a movement session the camera is ours — reset it to the
            // vanilla spawn-time baseline so dip/roll compose identically with or without FPV.
            // FPV re-takes the camera the frame the session ends.
            if (_vanillaCameraBaseCaptured && SlideAnimationBridge.IsFirstPersonViewLocalBodyShown())
                cameraTransform.localPosition = _vanillaCameraBaseLocal;
            ReapplyFirstPersonArmsPresentation(player);
            ApplyCameraRoll(cameraTransform, rollDegrees);
            ApplyCameraDip(cameraTransform, dip);
        }

        private static void ApplyCameraDip(Transform cameraTransform, float dip)
        {
            if (cameraTransform == null)
                return;

            if (dip <= 0.0001f)
            {
                RestoreCameraPositionOffset();
                return;
            }

            if (_cameraOffsetTransform != null && _cameraOffsetTransform != cameraTransform)
                RestoreCameraOffset();

            Vector3 baseLocalPosition = cameraTransform.localPosition;
            if (_cameraOffsetApplied && _cameraOffsetTransform == cameraTransform)
            {
                Vector3 expected = _cameraBaseLocalPosition;
                expected.y += _cameraAppliedOffset;
                if ((baseLocalPosition - expected).sqrMagnitude < 0.0004f)
                {
                    baseLocalPosition = _cameraBaseLocalPosition;
                    cameraTransform.localPosition = baseLocalPosition;
                }
            }

            _cameraOffsetTransform = cameraTransform;
            _cameraBaseLocalPosition = baseLocalPosition;
            _cameraAppliedOffset = -dip;
            _cameraOffsetApplied = dip > 0.0001f;

            Vector3 adjusted = baseLocalPosition;
            adjusted.y -= dip;
            if (_cameraOffsetApplied)
                cameraTransform.localPosition = adjusted;
        }

        private static void ApplyCameraRoll(Transform cameraTransform, float rollDegrees)
        {
            if (cameraTransform == null)
                return;

            if (_cameraOffsetTransform != null && _cameraOffsetTransform != cameraTransform)
                RestoreCameraOffset();

            _cameraOffsetTransform = cameraTransform;
            if (Mathf.Abs(rollDegrees) <= 0.001f)
                rollDegrees = 0f;
            bool writeRoll = Mathf.Abs(rollDegrees) > 0.001f || _cameraRotationApplied;
            if (writeRoll)
            {
                Vector3 euler = cameraTransform.localEulerAngles;
                euler.z = rollDegrees;
                cameraTransform.localEulerAngles = euler;
            }
            _cameraRotationApplied = Mathf.Abs(rollDegrees) > 0.001f;
            if (!_cameraOffsetApplied && !_cameraRotationApplied)
                _cameraOffsetTransform = null;
        }

        private static float CalculateSlideCameraEnvelope()
        {
            if (_localSlideState == SlideState.None)
                return 0f;

            if (_localSlideState == SlideState.Exiting)
            {
                float exitT = Mathf.Clamp01((Time.time - _slideExitStartedAt) / PanicSlideUpgrade.SLIDE_CAMERA_DIP_OUT_SECONDS);
                return 1f - Mathf.SmoothStep(0f, 1f, exitT);
            }

            float enterT = Mathf.Clamp01((Time.time - _slideStartedAt) / PanicSlideUpgrade.SLIDE_CAMERA_DIP_IN_SECONDS);
            return Mathf.SmoothStep(0f, 1f, enterT);
        }

        internal static void RestoreCameraOffset()
        {
            if (!_cameraOffsetApplied && !_cameraRotationApplied)
                return;

            if (_cameraOffsetTransform != null)
            {
                if (_cameraOffsetApplied)
                    _cameraOffsetTransform.localPosition = _cameraBaseLocalPosition;
                if (_cameraRotationApplied)
                {
                    Vector3 euler = _cameraOffsetTransform.localEulerAngles;
                    euler.z = 0f;
                    _cameraOffsetTransform.localEulerAngles = euler;
                }
            }

            _cameraOffsetTransform = null;
            _cameraBaseLocalPosition = Vector3.zero;
            _cameraAppliedOffset = 0f;
            _cameraOffsetApplied = false;
            _cameraRotationApplied = false;
        }

        private static void RestoreCameraPositionOffset()
        {
            if (!_cameraOffsetApplied)
                return;

            if (_cameraOffsetTransform != null)
                _cameraOffsetTransform.localPosition = _cameraBaseLocalPosition;

            _cameraBaseLocalPosition = Vector3.zero;
            _cameraAppliedOffset = 0f;
            _cameraOffsetApplied = false;
            if (!_cameraRotationApplied)
                _cameraOffsetTransform = null;
        }

        internal static void BeginFirstPersonMovementPresentation(PlayerControllerB player, bool showBody = true)
        {
            if (!IsLocalPlayer(player))
                return;

            if (!showBody)
            {
                // Pure arms-viewmodel presentation: leave LC's vanilla local body hiding
                // untouched so no body parts can swing through the fixed FP camera, but FORCE
                // the FP arms visible — vanilla hides them while empty-handed, which would
                // leave the authored arms clips playing on an invisible mesh.
                EndFirstPersonMovementPresentation();
                BeginFirstPersonArmsPresentation(player);
                return;
            }

            if (_presentationSaved && _presentationPlayer == player)
            {
                ReapplyFirstPersonMovementPresentation(player);
                return;
            }

            EndFirstPersonMovementPresentation();
            _presentationPlayer = player;
            _presentationSaved = true;
            _savedPlayerModelState = new RendererPresentationState(player.thisPlayerModel);
            _savedPlayerModelLod1State = new RendererPresentationState(player.thisPlayerModelLOD1);
            _savedPlayerModelLod2State = new RendererPresentationState(player.thisPlayerModelLOD2);
            _presentationVisibleLayer = ResolveFirstPersonVisibleLayer(player);

            int cameraMask = player.gameplayCamera != null ? player.gameplayCamera.cullingMask : ~0;
            _presentationLayerRedirectActive = _presentationVisibleLayer >= 0 &&
                (RendererLayerNeedsRedirect(_savedPlayerModelState, cameraMask) ||
                 RendererLayerNeedsRedirect(_savedPlayerModelLod1State, cameraMask) ||
                 RendererLayerNeedsRedirect(_savedPlayerModelLod2State, cameraMask));

            BeginFirstPersonArmsPresentation(player);
            LogFirstPersonPresentationDiagnostics(player, cameraMask);
            ReapplyFirstPersonMovementPresentation(player);
        }

        internal static void ReapplyFirstPersonMovementPresentation(PlayerControllerB player)
        {
            if (!_presentationSaved || player == null || player != _presentationPlayer)
                return;

            ApplyBodyRendererPresentation(player.thisPlayerModel);
            ApplyBodyRendererPresentation(player.thisPlayerModelLOD1);
            ApplyBodyRendererPresentation(player.thisPlayerModelLOD2);
            ReapplyFirstPersonArmsPresentation(player);

            if (_presentationLayerRedirectActive)
            {
                ApplyPresentationLayer(player.thisPlayerModel);
                ApplyPresentationLayer(player.thisPlayerModelLOD1);
                ApplyPresentationLayer(player.thisPlayerModelLOD2);
            }
        }

        private static void BeginFirstPersonArmsPresentation(PlayerControllerB player)
        {
            if (player == null)
                return;

            if (!_armsPresentationSaved || _armsPresentationPlayer != player)
            {
                _armsPresentationPlayer = player;
                _armsPresentationSaved = true;
                _savedArmsRendererEnabled = player.thisPlayerModelArms == null || player.thisPlayerModelArms.enabled;
                _savedLocalArmsMatchCamera = player.localArmsMatchCamera;
            }

            SubscribeMovementRenderHook();
            ReapplyFirstPersonArmsPresentation(player);
        }

        // First-Person View disables the vanilla arms renderer for the gameplay camera from its
        // OWN beginCameraRendering pass (FirstPersonBody.ApplyForCamera), which runs after every
        // LateUpdate — a LateUpdate-priority win is not enough. This hook subscribes at movement-
        // presentation begin, which is always AFTER FPV's session-long subscription, so delegate
        // order makes ours the final render-time writer for the gameplay camera.
        private static void SubscribeMovementRenderHook()
        {
            if (_movementRenderHookSubscribed)
                return;

            _movementRenderHookSubscribed = true;
            RenderPipelineManager.beginCameraRendering += OnMovementBeginCameraRendering;
        }

        private static void UnsubscribeMovementRenderHook()
        {
            if (!_movementRenderHookSubscribed)
                return;

            _movementRenderHookSubscribed = false;
            RenderPipelineManager.beginCameraRendering -= OnMovementBeginCameraRendering;
        }

        private static void OnMovementBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            PlayerControllerB player = _armsPresentationPlayer;
            if (player == null || camera == null || player.gameplayCamera != camera)
                return;

            ReapplyFirstPersonArmsPresentation(player);
        }

        internal static void ReapplyFirstPersonArmsPresentation(PlayerControllerB player)
        {
            if (!_armsPresentationSaved || player == null || player != _armsPresentationPlayer)
                return;

            // First-Person View hides the vanilla arms from its priority-0 LateUpdate postfix
            // whenever its movement gate is idle; this reapply runs at priority -100 (after it)
            // and again from the render hook, so it is the frame's final visibility state.
            // The FP arms are a LOCAL-ONLY viewmodel glued to the gameplay camera: when the
            // debug third-person cam (the same camera moved behind the player) is active they
            // read as a phantom extra pair of arms floating over the body, so force them off
            // there — forceRenderingOff also wins against FPV's render-time re-enable. Remote
            // players never render this mesh either way.
            if (player.thisPlayerModelArms != null)
            {
                bool debugThirdPerson = LedgeMantlePatch.IsDebugThirdPersonCamActive();
                player.thisPlayerModelArms.forceRenderingOff = debugThirdPerson;
                if (!debugThirdPerson)
                    player.thisPlayerModelArms.enabled = true;
            }
            player.localArmsMatchCamera = true;
        }

        private static void EndFirstPersonArmsPresentation()
        {
            UnsubscribeMovementRenderHook();
            if (!_armsPresentationSaved)
                return;

            PlayerControllerB player = _armsPresentationPlayer;
            if (player != null)
            {
                if (player.thisPlayerModelArms != null)
                {
                    player.thisPlayerModelArms.forceRenderingOff = false;
                    player.thisPlayerModelArms.enabled = _savedArmsRendererEnabled;
                }
                player.localArmsMatchCamera = _savedLocalArmsMatchCamera;
            }

            _armsPresentationPlayer = null;
            _armsPresentationSaved = false;
            _savedArmsRendererEnabled = false;
            _savedLocalArmsMatchCamera = false;
        }

        internal static void EndFirstPersonMovementPresentation()
        {
            EndFirstPersonArmsPresentation();

            if (!_presentationSaved)
                return;

            RestoreBodyRendererPresentation(_savedPlayerModelState);
            RestoreBodyRendererPresentation(_savedPlayerModelLod1State);
            RestoreBodyRendererPresentation(_savedPlayerModelLod2State);

            _presentationPlayer = null;
            _savedPlayerModelState = default;
            _savedPlayerModelLod1State = default;
            _savedPlayerModelLod2State = default;
            _presentationLayerRedirectActive = false;
            _presentationVisibleLayer = -1;
            _presentationSaved = false;
        }

        private static void ApplyBodyRendererPresentation(Renderer renderer)
        {
            if (renderer == null)
                return;

            renderer.shadowCastingMode = ShadowCastingMode.On;
        }

        private static void ApplyPresentationLayer(Renderer renderer)
        {
            if (renderer == null || renderer.gameObject == null || _presentationVisibleLayer < 0)
                return;

            renderer.gameObject.layer = _presentationVisibleLayer;
        }

        private static void RestoreBodyRendererPresentation(RendererPresentationState state)
        {
            if (!state.Valid || state.Renderer == null)
                return;

            state.Renderer.enabled = state.Enabled;
            state.Renderer.shadowCastingMode = state.ShadowMode;
            if (state.Renderer.gameObject != null)
                state.Renderer.gameObject.layer = state.Layer;
        }

        private static bool RendererLayerNeedsRedirect(RendererPresentationState state, int cameraMask)
        {
            return state.Valid && !IsLayerRenderedByMask(state.Layer, cameraMask);
        }

        private static bool IsLayerRenderedByMask(int layer, int cameraMask)
        {
            return layer >= 0 && layer < 32 && (cameraMask & (1 << layer)) != 0;
        }

        private static int ResolveFirstPersonVisibleLayer(PlayerControllerB player)
        {
            if (player != null && player.thisPlayerModelArms != null && player.thisPlayerModelArms.gameObject != null)
                return player.thisPlayerModelArms.gameObject.layer;

            int cameraMask = player != null && player.gameplayCamera != null ? player.gameplayCamera.cullingMask : ~0;
            for (int layer = 0; layer < 32; layer++)
            {
                if (IsLayerRenderedByMask(layer, cameraMask))
                    return layer;
            }

            return 0;
        }

        private static void LogFirstPersonPresentationDiagnostics(PlayerControllerB player, int cameraMask)
        {
            Plugin.Log?.LogInfo(
                "[Panic Slide] First-person movement presentation active. " +
                $"gameplayCamera.cullingMask=0x{cameraMask:X8}; " +
                $"redirectLayer={(_presentationLayerRedirectActive ? _presentationVisibleLayer.ToString() : "<none>")}; " +
                $"{FormatRendererForPresentationLog("thisPlayerModel", player?.thisPlayerModel, cameraMask)}; " +
                $"{FormatRendererForPresentationLog("thisPlayerModelLOD1", player?.thisPlayerModelLOD1, cameraMask)}; " +
                $"{FormatRendererForPresentationLog("thisPlayerModelLOD2", player?.thisPlayerModelLOD2, cameraMask)}; " +
                $"{FormatRendererForPresentationLog("thisPlayerModelArms", player?.thisPlayerModelArms, cameraMask)}");
        }

        private static string FormatRendererForPresentationLog(string label, Renderer renderer, int cameraMask)
        {
            if (renderer == null)
                return label + "=<null>";

            int layer = renderer.gameObject != null ? renderer.gameObject.layer : -1;
            return label +
                $" name='{renderer.name}'" +
                $" enabled={renderer.enabled}" +
                $" shadowCastingMode={renderer.shadowCastingMode}" +
                $" gameObject.layer={layer}" +
                $" renderedByGameplayCamera={IsLayerRenderedByMask(layer, cameraMask)}";
        }

        private static Vector3 ResolveCameraForward(PlayerControllerB player)
        {
            Transform camera = player != null && player.gameplayCamera != null ? player.gameplayCamera.transform : player?.transform;
            Vector3 forward = camera != null ? Vector3.ProjectOnPlane(camera.forward, Vector3.up) : Vector3.zero;
            if (forward.sqrMagnitude < 0.01f && player != null)
                forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.forward;
            return forward.normalized;
        }

        private static float GetHorizontalSpeed(PlayerControllerB player)
        {
            if (player == null || player.thisController == null)
                return 0f;

            Vector3 velocity = player.thisController.velocity;
            velocity.y = 0f;
            return velocity.magnitude;
        }

        internal static bool IsSlideActive()
        {
            // Treat the exit animation as slide-owned too. Starting a mantle or air jump while
            // crouch/controller restoration is still in flight creates conflicting collider and
            // Animator ownership for roughly half a second.
            return _localSlideState != SlideState.None;
        }

        private static bool IsLocalSlideActive()
        {
            return _localSlideState == SlideState.Starting || _localSlideState == SlideState.Sliding;
        }

        /// <summary>
        /// Netcode nulls the CustomMessagingManager on shutdown and builds a fresh one for the next
        /// host/join, so this latch has to drop or the second lobby of a session never receives
        /// remote slides.
        /// </summary>
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _handlersRegistered = false;
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered)
                return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_SLIDE_START, OnReceiveSlideStart);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_SLIDE_STOP, OnReceiveSlideStop);
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"PanicSlidePatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

        private static void SendSlideStart(ulong playerClientId)
        {
            SendSlideMessage(MSG_SLIDE_START, playerClientId);
        }

        private static void SendSlideStop(ulong playerClientId)
        {
            SendSlideMessage(MSG_SLIDE_STOP, playerClientId);
        }

        private static void SendSlideMessage(string messageName, ulong playerClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient || network.CustomMessagingManager == null)
                return;

            if (network.IsServer)
                SendSlideMessage(messageName, playerClientId, null);
            else
                SendSlideMessage(messageName, playerClientId, NetworkManager.ServerClientId);
        }

        private static void SendSlideMessage(string messageName, ulong playerClientId, ulong? clientId)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(ulong), Allocator.Temp);
            try
            {
                writer.WriteValueSafe(playerClientId);
                if (clientId.HasValue)
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                        messageName,
                        clientId.Value,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                else
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                        messageName,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveSlideStart(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                ulong playerClientId;
                reader.ReadValueSafe(out playerClientId);
                ApplyRemoteSlideStart(playerClientId);
                RelaySlideMessageIfHost(MSG_SLIDE_START, senderClientId, playerClientId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"PanicSlidePatch: malformed slide start message: {ex.Message}");
            }
        }

        private static void OnReceiveSlideStop(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                ulong playerClientId;
                reader.ReadValueSafe(out playerClientId);
                ApplyRemoteSlideStop(playerClientId);
                RelaySlideMessageIfHost(MSG_SLIDE_STOP, senderClientId, playerClientId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"PanicSlidePatch: malformed slide stop message: {ex.Message}");
            }
        }

        private static void RelaySlideMessageIfHost(string messageName, ulong senderClientId, ulong playerClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network != null
                && network.IsServer
                && senderClientId != network.LocalClientId)
            {
                SendSlideMessage(messageName, playerClientId, null);
            }
        }

        private static void ApplyRemoteSlideStart(ulong playerClientId)
        {
            bool terrainConfigured = Plugin.RemoteSlideTerrainConformance?.Value ?? true;
            PlayerControllerB player = ResolvePlayer(playerClientId);
            if (player == null)
            {
                Plugin.Log?.LogInfo(
                    "[Panic Slide.remote-slide] start_gate: " +
                    $"playerClientId={playerClientId} bridgeStarted=False " +
                    $"terrainConfigured={terrainConfigured} " +
                    "terrainPresentationStarted=False reason='player_missing'.");
                return;
            }
            if (IsLocalPlayer(player))
            {
                Plugin.Log?.LogInfo(
                    "[Panic Slide.remote-slide] start_gate: " +
                    $"playerClientId={playerClientId} bridgeStarted=False " +
                    $"terrainConfigured={terrainConfigured} " +
                    "terrainPresentationStarted=False reason='local_player'.");
                return;
            }

            bool bridgeStarted = SlideAnimationBridge.Begin(player);
            bool terrainPresentationStarted = false;
            string reason;

            if (!bridgeStarted)
            {
                RemoveRemoteSlideTerrainPresentation(playerClientId, "bridge_start_failed");
                reason = "bridge_start_failed";
            }
            else if (!terrainConfigured)
            {
                RemoveRemoteSlideTerrainPresentation(playerClientId, "config_disabled");
                reason = "config_disabled";
            }
            else if (RemoteSlidePresentations.TryGetValue(
                         playerClientId,
                         out RemoteSlideTerrainPresentation existing)
                     && ReferenceEquals(existing.Player, player))
            {
                terrainPresentationStarted = true;
                reason = "already_active";
            }
            else
            {
                RemoveRemoteSlideTerrainPresentation(playerClientId, "replaced");
                var presentation = new RemoteSlideTerrainPresentation(player);
                if (presentation.IsReady)
                {
                    RemoteSlidePresentations[playerClientId] = presentation;
                    terrainPresentationStarted = true;
                    reason = "ready";
                }
                else
                {
                    reason = "presentation_root_missing";
                }
            }

            Plugin.Log?.LogInfo(
                "[Panic Slide.remote-slide] start_gate: " +
                $"playerClientId={playerClientId} bridgeStarted={bridgeStarted} " +
                $"terrainConfigured={terrainConfigured} " +
                $"terrainPresentationStarted={terrainPresentationStarted} reason='{reason}'.");
        }

        private static void ApplyRemoteSlideStop(ulong playerClientId)
        {
            PlayerControllerB player = ResolvePlayer(playerClientId);
            bool bridgeExitTriggered = player != null && !IsLocalPlayer(player);
            if (bridgeExitTriggered)
                SlideAnimationBridge.TriggerExit(player);

            bool terrainPresentationActive = RemoteSlidePresentations.ContainsKey(playerClientId);
            if (!bridgeExitTriggered)
                RemoveRemoteSlideTerrainPresentation(playerClientId, "stop_without_remote_player");

            Plugin.Log?.LogInfo(
                "[Panic Slide.remote-slide] stop_gate: " +
                $"playerClientId={playerClientId} bridgeExitTriggered={bridgeExitTriggered} " +
                $"terrainPresentationActive={terrainPresentationActive} " +
                $"reason='{(player == null ? "player_missing" : IsLocalPlayer(player) ? "local_player" : "exit_started")}'.");
        }

        private static void ApplyRemoteSlideTerrainConformance(PlayerControllerB player)
        {
            ulong playerClientId = player.playerClientId;
            if (!RemoteSlidePresentations.TryGetValue(
                    playerClientId,
                    out RemoteSlideTerrainPresentation presentation))
            {
                return;
            }

            if (!ReferenceEquals(presentation.Player, player))
            {
                RemoveRemoteSlideTerrainPresentation(playerClientId, "player_replaced");
                return;
            }

            if (!(Plugin.RemoteSlideTerrainConformance?.Value ?? true))
            {
                RemoveRemoteSlideTerrainPresentation(playerClientId, "config_disabled");
                return;
            }

            if (!SlideAnimationBridge.IsActive(player))
            {
                RemoveRemoteSlideTerrainPresentation(playerClientId, "bridge_inactive");
                return;
            }

            presentation.Apply();
        }

        private static bool RemoveRemoteSlideTerrainPresentation(
            ulong playerClientId,
            string reason)
        {
            if (!RemoteSlidePresentations.TryGetValue(
                    playerClientId,
                    out RemoteSlideTerrainPresentation presentation))
            {
                return false;
            }

            RemoteSlidePresentations.Remove(playerClientId);
            presentation.Restore();
            Plugin.Log?.LogInfo(
                "[Panic Slide.remote-slide] terrain_end_gate: " +
                $"playerClientId={playerClientId} reason='{reason}'.");
            return true;
        }

        private static void ClearRemoteSlideTerrainPresentations(string reason)
        {
            int restored = 0;
            foreach (RemoteSlideTerrainPresentation presentation in RemoteSlidePresentations.Values)
            {
                presentation.Restore();
                restored++;
            }

            RemoteSlidePresentations.Clear();
            Plugin.Log?.LogInfo(
                "[Panic Slide.remote-slide] terrain_clear_gate: " +
                $"restored={restored} reason='{reason}'.");
        }

        private sealed class RemoteSlideTerrainPresentation
        {
            private readonly Transform _presentationRoot;
            private readonly Vector3 _baseLocalPosition;
            private readonly Quaternion _baseLocalRotation;
            private Vector3 _smoothedGroundNormal = Vector3.up;
            private bool _groundNormalInitialized;
            private string _lastGateResult;

            internal PlayerControllerB Player { get; }
            internal bool IsReady => Player != null && _presentationRoot != null;

            internal RemoteSlideTerrainPresentation(PlayerControllerB player)
            {
                Player = player;
                _presentationRoot = ResolveBodyMetarig(player);
                if (_presentationRoot != null)
                {
                    _baseLocalPosition = _presentationRoot.localPosition;
                    _baseLocalRotation = _presentationRoot.localRotation;
                }
            }

            internal void Apply()
            {
                if (!IsReady)
                {
                    LogGate("presentation_root_missing");
                    return;
                }

                _presentationRoot.localPosition = _baseLocalPosition;
                _presentationRoot.localRotation = _baseLocalRotation;

                Vector3 baseWorldPosition = _presentationRoot.position;
                Quaternion baseWorldRotation = _presentationRoot.rotation;
                int mask = StartOfRound.Instance != null
                    ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                    : ~0;
                Vector3 rayOrigin =
                    baseWorldPosition + Vector3.up * REMOTE_SLIDE_GROUND_RAY_START_METERS;
                if (!Physics.Raycast(
                        rayOrigin,
                        Vector3.down,
                        out RaycastHit groundHit,
                        REMOTE_SLIDE_GROUND_RAY_DISTANCE_METERS,
                        mask,
                        QueryTriggerInteraction.Ignore))
                {
                    LogGate("ground_miss");
                    return;
                }

                Vector3 sampledNormal = groundHit.normal.sqrMagnitude > 0.0001f
                    ? groundHit.normal.normalized
                    : Vector3.up;
                if (!_groundNormalInitialized)
                {
                    _smoothedGroundNormal = sampledNormal;
                    _groundNormalInitialized = true;
                }
                else
                {
                    float blend = 1f - Mathf.Exp(-REMOTE_SLIDE_NORMAL_RESPONSE * Time.deltaTime);
                    _smoothedGroundNormal =
                        Vector3.Slerp(_smoothedGroundNormal, sampledNormal, blend).normalized;
                }

                _presentationRoot.rotation =
                    Quaternion.FromToRotation(Vector3.up, _smoothedGroundNormal)
                    * baseWorldRotation;
                _presentationRoot.position = new Vector3(
                    baseWorldPosition.x,
                    groundHit.point.y + REMOTE_SLIDE_GROUND_CLEARANCE_METERS,
                    baseWorldPosition.z);

                LogGate(
                    "applied",
                    groundHit.point.y,
                    _presentationRoot.position.y - baseWorldPosition.y,
                    _smoothedGroundNormal);
            }

            internal void Restore()
            {
                if (_presentationRoot == null)
                    return;

                _presentationRoot.localPosition = _baseLocalPosition;
                _presentationRoot.localRotation = _baseLocalRotation;
            }

            private void LogGate(
                string result,
                float groundY = 0f,
                float presentationDeltaY = 0f,
                Vector3 groundNormal = default)
            {
                if (string.Equals(_lastGateResult, result, StringComparison.Ordinal))
                    return;

                _lastGateResult = result;
                Plugin.Log?.LogInfo(
                    "[Panic Slide.remote-slide] terrain_gate: " +
                    $"playerClientId={Player?.playerClientId.ToString() ?? "<null>"} " +
                    $"result='{result}' groundY={groundY:0.######} " +
                    $"presentationDeltaY={presentationDeltaY:0.######} " +
                    $"groundNormal=({groundNormal.x:0.######},{groundNormal.y:0.######},{groundNormal.z:0.######}).");
            }

            private static Transform ResolveBodyMetarig(PlayerControllerB player)
            {
                if (player == null)
                    return null;

                foreach (Transform transform in player.GetComponentsInChildren<Transform>(true))
                {
                    if (string.Equals(transform.name, "metarig", StringComparison.Ordinal)
                        && transform.parent != null
                        && string.Equals(
                            transform.parent.name,
                            "ScavengerModel",
                            StringComparison.Ordinal))
                    {
                        return transform;
                    }
                }

                return null;
            }
        }

        private static PlayerControllerB ResolvePlayer(ulong playerClientId)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.allPlayerScripts == null)
                return null;

            for (int i = 0; i < round.allPlayerScripts.Length; i++)
            {
                PlayerControllerB player = round.allPlayerScripts[i];
                if (player != null && player.playerClientId == playerClientId)
                    return player;
            }

            return null;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }
    }
}

#pragma warning restore Harmony003
