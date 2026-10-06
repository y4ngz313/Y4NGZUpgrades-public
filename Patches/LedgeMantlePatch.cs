using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZInteractions.InteractionAnimationApi;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Upgrades;

#pragma warning disable Harmony003

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class LedgeMantlePatch
    {
        private const string MSG_MANTLE_START = "Y4NGZ_MantleStart";
        private const string MSG_MANTLE_STOP = "Y4NGZ_MantleStop";
        private const int MANTLE_START_EXTENSION_BYTES = sizeof(float) * 7;
        private const float WALL_MAX_UP_DOT = 0.35f;
        private const float WALKABLE_MIN_UP_DOT = 0.7f;
        private const float WALL_TO_LEDGE_FORWARD_OFFSET = 0.15f;
        private const float DESTINATION_FORWARD_PADDING = 0.35f;
        private const float CAPSULE_SKIN = 0.05f;
        private const float LIFT_SEGMENT_FRACTION = 0.6f;
        // Return control (and complete the over-the-ledge motion) at this fraction of the mantle
        // rather than 1.0: the last sliver of the clip is just a slow settle that reads as messy, so
        // finishing early makes the top-out feel snappier and hands movement back sooner.
        private const float MANTLE_FINISH_T = 0.9f;
        // The lift target rises this far ABOVE the ledge top before the forward move, so the torso
        // and legs clear the ledge lip instead of passing through it (reduces body clipping).
        private const float MANTLE_LIFT_CLEARANCE = 0.28f;
        private const float MANTLE_WALL_SCAN_START_HEIGHT = 0.45f;
        private const float MANTLE_WALL_SCAN_STEP = 0.3f;
        private const float MANTLE_LEDGE_SCAN_STEP = 0.25f;
        private const float MANTLE_INTERIOR_CEILING_CHECK_DISTANCE = 12f;
        private const float MANTLE_PATH_RADIUS_INSET = 0.08f;

        private static bool _handlersRegistered;
        private static float _nextRetryTime;
        private static float _cooldownEnd;
        private static readonly Dictionary<ulong, RemoteMantlePresentation> RemoteMantles =
            new Dictionary<ulong, RemoteMantlePresentation>();

        private static bool _mantleActive;
        private static int _mantleStartFrame = -1;
        private static bool _mantleStaminaCharged;
        private static PlayerControllerB _mantlePlayer;
        private static Vector3 _mantleStartPosition;
        private static Vector3 _mantleLiftPosition;
        private static Vector3 _mantleEndPosition;
        private static Vector3 _mantleLastSafePosition;
        private static float _mantleStartedAt;
        private static float _mantleDuration;
        private static bool _mantleAnimStarted;
        private static bool _mantleInputSaved;
        private static bool _savedDisableMoveInput;
        private static float _nextMantleArmsDiagnosticAt;
        private static bool _mantleArmsHandBonesResolved;
        private static Transform _mantleArmsHandLeft;
        private static Transform _mantleArmsHandRight;
        private static Transform _mantleArmsSpine003;
        private static Transform _mantleArmsShoulderLeft;
        private static Transform _mantleArmsShoulderRight;
        private static Transform _mantleArmsTargetLeft;
        private static Transform _mantleArmsTargetRight;
        private static bool _mantleOccluderDumpDone;
        private static bool _mantleHideResolved;
        private static Renderer[] _mantleBodyLods = Array.Empty<Renderer>();
        private static int[] _mantleBodyLodSavedLayers = Array.Empty<int>();
        private static int _mantleFpHiddenLayer = -1;
        private static Renderer[] _mantleHeadCosmetics = Array.Empty<Renderer>();
        private static bool[] _mantleHeadCosmeticSavedEnabled = Array.Empty<bool>();
        private static bool _mantleBodyForceHidden;
        private static bool _mantleFpArmRenderersResolved;
        private static Renderer[] _mantleFpArmRenderers = Array.Empty<Renderer>();
        // F-ESCAPE-6: IsMantleFpArmRenderer used to linear-scan the array for every renderer of
        // the player hierarchy, every frame of every mantle.
        private static readonly HashSet<Renderer> _mantleFpArmRendererSet = new HashSet<Renderer>();
        private static bool _mantleFpArmsHidden;
        // F-ESCAPE-6: CullMantleViewOccluders used to call GetComponentsInChildren<Renderer> from
        // the LateUpdate postfix, allocating a fresh array (dozens of entries: body, LODs, arms,
        // visor, head costume, held item) on every frame of the mantle. Scan once per mantle.
        private static bool _mantleOccluderCandidatesResolved;
        private static Renderer[] _mantleOccluderCandidates = Array.Empty<Renderer>();

        // Render-truth instrumentation (#347): in the Cuckoo profile the LateUpdate telemetry is
        // healthy while the screen shows floating arms, so something re-poses the camera/arms
        // between our LateUpdate postfix and rendering. Snapshot what we wrote and diff it at
        // beginCameraRendering for the same frame — the diff IS the foreign writer's edit.
        private static bool _renderDiagSubscribed;
        private static int _renderDiagSnapshotFrame = -1;
        private static bool _renderDiagLogThisFrame;
        private static Vector3 _renderDiagCamPos;
        private static Quaternion _renderDiagCamRot;
        private static Vector3 _renderDiagHandLCam;
        private static Vector3 _renderDiagHandRCam;
        private static Vector3 _renderDiagArmsRootCam;
        private static Vector3 _renderDiagArmsRootWorld;
        private static Vector3 _renderDiagSpine003Cam;
        private static bool _patchOwnerDumpDone;

        // Ledge grab IK (third-person body): plant the body hands on the actual ledge edge.
        private static Vector3 _mantleLedgeGripCenter;
        private static Vector3 _mantleWallNormal;
        private static bool _mantleBodyArmBonesResolved;
        private static Transform _bodyArmUpperL, _bodyArmLowerL, _bodyHandL;
        private static Transform _bodyArmUpperR, _bodyArmLowerR, _bodyHandR;
        private static bool _mantleFpArmBonesResolved;
        private static Transform _fpArmUpperL, _fpArmLowerL, _fpHandL;
        private static Transform _fpArmUpperR, _fpArmLowerR, _fpHandR;

        // Dynamic first-person view-occluder cull (catches intermittent head mask/visor meshes).
        private static readonly System.Collections.Generic.List<Renderer> _mantleTempCulled =
            new System.Collections.Generic.List<Renderer>();
        private static readonly System.Collections.Generic.HashSet<string> _mantleOccluderLogged =
            new System.Collections.Generic.HashSet<string>();
        private const float MANTLE_OCCLUDER_CULL_RADIUS = 0.32f;
        private const float MANTLE_GRAB_HAND_SPREAD = 0.28f;
        private const float REMOTE_MANTLE_MAX_REACH_FRACTION = 0.92f;
        // F-ESCAPE-4: plausibility bounds for the payload-supplied grip/normal. The probe only
        // ever grips a ledge within MANTLE_FORWARD_REACH of the player at up to 3 m, so anything
        // beyond a few metres of the named player is not a mantle that player could be performing.
        private const float REMOTE_MANTLE_MAX_GRIP_DISTANCE = 6f;
        private const float REMOTE_MANTLE_MAX_NORMAL_SQR_MAGNITUDE = 4f;
        // Camera-space forward depth the FP viewmodel hands are projected to when grabbing a ledge.
        private const float MANTLE_FP_GRAB_DEPTH = 0.5f;

        private static bool _fallFieldsResolved;
        private static FieldInfo _fallValueField;
        private static FieldInfo _fallValueUncappedField;

        private static bool _externalForcesResolved;
        private static FieldInfo _externalForcesField;

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
            _nextRetryTime = 0f;
            _cooldownEnd = 0f;
            AbortMantle("start-game");
            ClearRemoteMantlePresentations("start-game");
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            AbortMantle("end-of-game");
            ClearRemoteMantlePresentations("end-of-game");
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            RegisterNetworkHandlers();
            DumpMovementPatchOwnersOnce(__instance);
            if (_mantleActive)
            {
                UpdateMantle(__instance);
                return;
            }

            TryStartMantle(__instance);
        }

        // Priority -100: seechela First-Person View patches the same method at priority 0 and
        // absolutely re-poses the gameplay camera (head-bone follow) plus re-decides vanilla-arms
        // visibility there. Lower-priority postfixes run later, so -100 guarantees our movement
        // composition (camera dip, arms re-anchor, FP grab IK, body/arms visibility) is the final
        // written state of the frame — the presentation is identical with or without FPV (#347).
        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        [HarmonyPriority(-100)]
        private static void PostPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (__instance == null)
                return;

            if (!IsLocalPlayer(__instance))
            {
                ApplyRemoteMantleBodyGrabIK(__instance);
                return;
            }

            if (!_mantleActive || __instance != _mantlePlayer)
                return;

            PanicSlidePatch.ReapplyFirstPersonMovementPresentation(__instance);

            // First-Person View coexistence (#347): this postfix runs at priority -100, AFTER
            // FPV's priority-0 camera/visibility pass, so every write below composes against the
            // final camera of the frame and is what actually renders. FPV's head-follow offset
            // (applied before us) survives as the camera base; our dip stacks on top of it.
            float envelope = CalculateMantleCameraEnvelope();
            PanicSlidePatch.ApplyFirstPersonMovementCamera(
                __instance,
                PanicSlideUpgrade.MANTLE_CAMERA_DIP * envelope,
                0f);
            // Clamp the camera/body before the final renderer and hand-IK composition so every
            // downstream solve sees the actual rendered orientation.
            PanicSlidePatch.ApplyMovementLookLimit(__instance);

            // The Diagnostics debug third-person cam is the SAME gameplayCamera moved 2.5m
            // behind the player and it force-enables the body renderers to show the local body.
            // Behind the player the head can't block the view, so our FP body-hide must yield —
            // otherwise it re-hides the body the debug cam is trying to show (player invisible
            // in third person). Restore the body and skip the hide while that cam is active.
            // The FP arms are a camera-matched viewmodel: they follow the gameplay camera, so when
            // the debug cam moves it behind the player they render as a phantom extra pair of arms
            // in the third-person view. The full-body clip already shows the mantle on the real
            // body, so hide the FP arms whenever the third-person cam is active (show them in true
            // first person). It is the same single gameplay camera moved, so forceRenderingOff
            // (one view at a time) hides them from third-person without affecting the FP view.
            if (IsDebugThirdPersonCamActive())
            {
                RestoreLocalMantleBody();
                SetMantleFirstPersonArmsHidden(__instance, true);
                RestoreMantleViewOccluders();
            }
            else
            {
                HideLocalMantleBody(__instance);
                SetMantleFirstPersonArmsHidden(__instance, false);
                // Dynamically cull whatever pokes into the first-person lens (head/mask/visor
                // meshes clip on some ledge heights). Self-gating: only the eye-level FP camera has
                // anything within the cull radius; the moved-back debug cam catches nothing.
                CullMantleViewOccluders(__instance);
            }
            // Re-anchor BEFORE logging so the diagnostics reflect the final rendered arm
            // position, and AFTER the camera dip so we anchor to the actual view.
            ReanchorMantleFirstPersonArms(__instance);
            // Plant the third-person body hands on the actual ledge edge (blended by the camera
            // envelope so they ease onto and off the grab). Runs after the animator has posed the
            // body; only affects the body mesh, which is what third-person / debug cam sees.
            ApplyMantleBodyGrabIK(__instance, envelope);
            // First-person screen-space grab: plant the viewmodel hands on the ledge's on-screen
            // position at reachable depth so the mantle reads as actually gripping the surface.
            // This was removed during the session-2 falsification experiment (it amplified the
            // then-broken authored composition); with the hand keys restored to the accepted
            // low-forward envelope it is the piece that made pre-regression mantles read clean.
            ApplyMantleFpArmGrabIK(__instance, envelope);
            LogMantleArmsDiagnostics(__instance);
            CaptureMantleRenderTruthSnapshot(__instance);
            LogMantleOccluderDumpOnce(__instance);
        }

        internal static bool IsMantleActive => _mantleActive;

        private static void TryStartMantle(PlayerControllerB player)
        {
            if (!CanTryMantle(player, out Vector3 forward))
                return;

            if (!TryProbeMantle(player, forward, out MantleProbe probe))
            {
                _nextRetryTime = Time.time + PanicSlideUpgrade.MANTLE_RETRY_SECONDS;
                return;
            }

            BeginMantle(player, probe);
        }

        private static bool CanTryMantle(PlayerControllerB player, out Vector3 forward)
        {
            forward = Vector3.zero;

            if (Time.time < _cooldownEnd || Time.time < _nextRetryTime)
                return false;
            if (PanicSlideUpgrade.GetTier() < 2)
                return false;
            if (player == null || player.thisController == null || player.transform == null)
                return false;
            if (player.isPlayerDead || player.inSpecialInteractAnimation || player.isClimbingLadder)
                return false;
            if (player.isTypingChat || player.inTerminalMenu)
                return false;
            // F-ESCAPE-2: these used to live only in ShouldAbortMantle, so an injured player got a
            // 20 Hz teleport-to-the-wall-and-back loop that drained their whole stamina bar,
            // re-stowed their held item and spammed MSG_MANTLE_START/STOP — the exact state the
            // upgrade exists for. Refuse before anything is started or charged.
            if (player.criticallyInjured || player.isUnderwater || player.isMovementHindered > 0)
                return false;
            // F-ESCAPE-8: the 8% cost was charged but never required, so an out-of-stamina player
            // could chain mantles forever. Mirrors the slide's stamina gate.
            if (player.isExhausted || player.sprintMeter < PanicSlideUpgrade.MANTLE_MIN_SPRINT_METER)
                return false;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return false;
            if (IsPlayerInVehicle(player))
                return false;
            // The mover teleports to WORLD-space positions computed at mantle start; if the
            // ship is moving, those positions are stale within a frame and the player ends
            // up inside (then under) the ship geometry. No mantling aboard unless landed.
            if ((player.isInElevator || player.isInHangarShipRoom) &&
                StartOfRound.Instance != null && !StartOfRound.Instance.shipHasLanded)
            {
                return false;
            }
            if (player.thisController.isGrounded)
                return false;
            if (PanicSlidePatch.IsSlideActive())
                return false;

            forward = ResolveCameraForward(player);
            return forward.sqrMagnitude > 0.01f;
        }

        private static bool TryProbeMantle(PlayerControllerB player, Vector3 forward, out MantleProbe probe)
        {
            probe = default;

            CharacterController controller = player.thisController;
            if (controller == null || forward.sqrMagnitude < 0.01f)
                return false;

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            float radius = Mathf.Max(0.1f, controller.radius);
            float feetY = ResolveFeetY(player, controller);
            float castRadius = Mathf.Clamp(radius * 0.55f, 0.12f, 0.35f);

            if (!TryFindWallAnchor(
                    player,
                    forward,
                    castRadius,
                    feetY,
                    mask,
                    out RaycastHit wallHit,
                    out Vector3 wallAnchor))
            {
                return false;
            }

            if (!HasMantleIntent(player, forward, wallHit.normal))
                return false;

            if (!TryFindLedgeTop(wallAnchor, forward, wallHit, castRadius, feetY, mask, out RaycastHit topHit))
            {
                return false;
            }

            if (topHit.collider == null || Vector3.Dot(topHit.normal, Vector3.up) <= WALKABLE_MIN_UP_DOT)
                return false;
            if (IsRejectedMantleSurface(topHit.collider))
                return false;

            float ledgeHeight = topHit.point.y - feetY;
            if (ledgeHeight < PanicSlideUpgrade.MANTLE_MIN_LEDGE_HEIGHT ||
                ledgeHeight > PanicSlideUpgrade.MANTLE_MAX_LEDGE_HEIGHT)
            {
                return false;
            }

            Vector3 destinationFeet = topHit.point + forward * (radius + DESTINATION_FORWARD_PADDING);
            destinationFeet.y = topHit.point.y + CAPSULE_SKIN;
            if (!HasDestinationClearance(controller, destinationFeet, mask))
                return false;

            float transformToFeetY = player.transform.position.y - feetY;
            Vector3 wallNormal = wallHit.normal;
            Vector3 wallContact = wallHit.point + wallNormal * (controller.radius + 0.02f);
            wallContact.y = player.transform.position.y;
            Vector3 startPosition = wallContact;
            Vector3 endPosition = new Vector3(destinationFeet.x, destinationFeet.y + transformToFeetY, destinationFeet.z);
            Vector3 liftPosition = new Vector3(startPosition.x, endPosition.y, startPosition.z);
            Vector3 liftPathPosition = liftPosition + Vector3.up * MANTLE_LIFT_CLEARANCE;

            Vector3 liftFeet = liftPathPosition;
            liftFeet.y -= transformToFeetY;
            if (!HasDestinationClearance(controller, liftFeet, mask) ||
                !HasSweptCapsuleClearance(controller, player.transform.position, startPosition, mask) ||
                !HasSweptCapsuleClearance(controller, startPosition, liftPathPosition, mask) ||
                !HasSweptCapsuleClearance(controller, liftPathPosition, endPosition, mask) ||
                !HasInteriorContainment(player, destinationFeet, mask))
            {
                return false;
            }

            bool tall = ledgeHeight > PanicSlideUpgrade.MANTLE_TALL_THRESHOLD;
            // Grab lip = wall face at the ledge-top height (where a hand would clasp the edge).
            Vector3 ledgeGripCenter = new Vector3(wallHit.point.x, topHit.point.y, wallHit.point.z);
            probe = new MantleProbe(startPosition, liftPosition, endPosition, wallNormal, ledgeHeight, tall, ledgeGripCenter);
            return true;
        }

        private static bool TryFindWallAnchor(
            PlayerControllerB player,
            Vector3 forward,
            float castRadius,
            float feetY,
            int mask,
            out RaycastHit wallHit,
            out Vector3 wallAnchor)
        {
            wallHit = default;
            wallAnchor = default;
            if (player == null || player.transform == null)
                return false;

            Vector3 scanOrigin = player.transform.position;
            float maxScanY = feetY + PanicSlideUpgrade.MANTLE_MAX_LEDGE_HEIGHT;
            for (float scanY = feetY + MANTLE_WALL_SCAN_START_HEIGHT;
                 scanY <= maxScanY + 0.001f;
                 scanY += MANTLE_WALL_SCAN_STEP)
            {
                scanOrigin.y = scanY;
                if (!Physics.SphereCast(
                        new Ray(scanOrigin, forward),
                        castRadius,
                        out RaycastHit hit,
                        PanicSlideUpgrade.MANTLE_FORWARD_REACH,
                        mask,
                        QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                if (hit.collider == null)
                    continue;
                if (IsRejectedMantleSurface(hit.collider))
                    return false;
                if (Vector3.Dot(hit.normal, Vector3.up) >= WALL_MAX_UP_DOT)
                    continue;

                wallHit = hit;
                wallAnchor = scanOrigin;
                return true;
            }

            return false;
        }

        private static bool TryFindLedgeTop(
            Vector3 wallAnchor,
            Vector3 forward,
            RaycastHit wallHit,
            float castRadius,
            float feetY,
            int mask,
            out RaycastHit topHit)
        {
            topHit = default;

            float maxScanY = feetY + PanicSlideUpgrade.MANTLE_MAX_LEDGE_HEIGHT + 0.3f;
            for (float scanY = wallAnchor.y; scanY <= maxScanY + 0.001f; scanY += MANTLE_LEDGE_SCAN_STEP)
            {
                Vector3 scanOrigin = wallAnchor;
                scanOrigin.y = scanY;
                if (Physics.SphereCast(
                        new Ray(scanOrigin, forward),
                        castRadius,
                        out _,
                        PanicSlideUpgrade.MANTLE_FORWARD_REACH,
                        mask,
                        QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                Vector3 ledgeProbeOrigin = wallHit.point + forward * WALL_TO_LEDGE_FORWARD_OFFSET;
                ledgeProbeOrigin.y = scanY;
                if (!Physics.Raycast(
                        ledgeProbeOrigin,
                        Vector3.down,
                        out topHit,
                        MANTLE_LEDGE_SCAN_STEP + 0.5f,
                        mask,
                        QueryTriggerInteraction.Ignore))
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        private static float ResolveMantleDuration(MantleProbe probe)
        {
            if (!probe.Tall)
                return PanicSlideUpgrade.MANTLE_ONE_METER_SECONDS;

            float heightScale = Mathf.Clamp(probe.LedgeHeight / 2f, 0.9f, 1.4f);
            return PanicSlideUpgrade.MANTLE_TWO_METER_SECONDS * heightScale;
        }

        private static void FaceMantleWall(PlayerControllerB player, Vector3 wallNormal)
        {
            if (player == null || player.transform == null)
                return;

            Vector3 lookDirection = Vector3.ProjectOnPlane(-wallNormal, Vector3.up);
            if (lookDirection.sqrMagnitude < 0.001f)
                return;

            float yaw = Quaternion.LookRotation(lookDirection.normalized, Vector3.up).eulerAngles.y;
            Vector3 euler = player.transform.eulerAngles;
            player.transform.rotation = Quaternion.Euler(euler.x, yaw, euler.z);
        }

        private static void BeginMantle(PlayerControllerB player, MantleProbe probe)
        {
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

            // F-ESCAPE-3: the authored clip is presentation, the lift is gameplay. A missing or
            // contract-rejected movement bundle used to delete the mantle outright (no motion, no
            // stamina, no message). Mantle anyway and explain the missing animation once.
            if (!SlideAnimationBridge.BeginMantle(player, probe.Tall))
                PanicSlidePatch.NotifyMovementAnimationUnavailableOnce();

            _mantlePlayer = player;
            _mantleStartPosition = probe.StartPosition;
            _mantleLiftPosition = probe.LiftPosition;
            _mantleEndPosition = probe.EndPosition;
            _mantleLastSafePosition = probe.StartPosition;
            _mantleLedgeGripCenter = probe.LedgeGripCenter;
            _mantleWallNormal = probe.WallNormal;
            _mantleDuration = ResolveMantleDuration(probe);
            _mantleStartedAt = Time.time;
            // F-ESCAPE-2: lets AbortMantle refund the stamina when the mantle dies on its first
            // Update frame instead of charging for a lift that never moved the player.
            _mantleStartFrame = Time.frameCount;
            _mantleStaminaCharged = false;
            _mantleActive = true;
            _nextMantleArmsDiagnosticAt = 0f;
            _mantleOccluderDumpDone = false;
            SubscribeMantleRenderTruth();
            SetPlayerPosition(player, _mantleStartPosition);
            FaceMantleWall(player, probe.WallNormal);
            _mantleAnimStarted = true;
            PanicSlidePatch.BeginMovementLookLimit(player);
            Plugin.Log?.LogDebug("[Panic Slide] Mantle FP arms diagnostics enabled for active mantle.");

            SaveAndLockInput(player);
            ZeroMovementForces(player);
            ZeroFallValues(player);
            ApplyStaminaCost(player);
            PanicSlidePatch.BeginFirstPersonMovementPresentation(player, showBody: false);

            float remoteSessionSeconds = _mantleDuration * MANTLE_FINISH_T;
            SendMantleStart(
                player.playerClientId,
                probe.Tall ? (byte)1 : (byte)0,
                remoteSessionSeconds,
                probe.LedgeGripCenter,
                probe.WallNormal);
        }

        private static void UpdateMantle(PlayerControllerB player)
        {
            if (player == null || player != _mantlePlayer)
            {
                AbortMantle("player-changed");
                return;
            }

            // F-ESCAPE-5: an external teleport (Inverse Teleporter, or any mod) must win. The mover
            // is a world-space lerp over positions captured at mantle start, so after a teleport
            // the swept-clearance check fails and the generic abort would drag the player back to
            // the ledge — on the Inverse Teleporter that can be fatal. Bail without restoring.
            if (player.teleportingThisFrame || player.teleportedLastFrame)
            {
                AbortMantle("teleported", restorePosition: false);
                return;
            }

            if (ShouldAbortMantle(player))
            {
                AbortMantle("aborted");
                return;
            }

            float elapsed = Time.time - _mantleStartedAt;
            float t = _mantleDuration > 0.001f ? Mathf.Clamp01(elapsed / _mantleDuration) : 1f;
            Vector3 target = EvaluateMantlePosition(t);

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (!HasSweptCapsuleClearance(player.thisController, player.transform.position, target, mask))
            {
                SetPlayerPosition(player, _mantleLastSafePosition);
                AbortMantle("path-blocked");
                return;
            }

            SetPlayerPosition(player, target);
            _mantleLastSafePosition = target;
            ZeroMovementForces(player);
            ZeroFallValues(player);
            // Re-anchor the arms to the camera in the SAME frame as the teleport (vanilla's
            // camera-match already ran earlier in this Update, before we moved the player).
            ReanchorMantleFirstPersonArms(player);

            if (t >= MANTLE_FINISH_T)
                FinishMantle();
        }

        private static bool ShouldAbortMantle(PlayerControllerB player)
        {
            // F-ESCAPE-2: criticallyInjured moved to CanTryMantle. Aborting on it here charged the
            // stamina and teleported the player to the wall first, every 50 ms.
            if (player == null || player.isPlayerDead)
                return true;
            if (player.inSpecialInteractAnimation || player.isClimbingLadder)
                return true;
            return IsPlayerInVehicle(player);
        }

        private static Vector3 EvaluateMantlePosition(float t)
        {
            // Lift target overshoots above the ledge top so the body clears the lip before moving
            // forward, then the over-segment settles it back down onto the ledge.
            Vector3 liftTarget = _mantleLiftPosition + Vector3.up * MANTLE_LIFT_CLEARANCE;

            Vector3 position;
            if (t <= LIFT_SEGMENT_FRACTION)
            {
                float liftT = Mathf.Clamp01(t / LIFT_SEGMENT_FRACTION);
                liftT = Mathf.SmoothStep(0f, 1f, liftT);
                position = Vector3.Lerp(_mantleStartPosition, liftTarget, liftT);
            }
            else
            {
                // Complete the forward motion by MANTLE_FINISH_T (not 1.0) so control returns with
                // the player already on the ledge instead of mid-settle.
                float overSpan = Mathf.Max(0.0001f, MANTLE_FINISH_T - LIFT_SEGMENT_FRACTION);
                float overT = Mathf.Clamp01((t - LIFT_SEGMENT_FRACTION) / overSpan);
                overT = Mathf.SmoothStep(0f, 1f, overT);
                position = Vector3.Lerp(liftTarget, _mantleEndPosition, overT);
            }

            position.y -= PanicSlideUpgrade.MANTLE_BODY_LOWER_METERS * EvaluateMantleBodyLowerWeight(t);
            return position;
        }

        private static float EvaluateMantleBodyLowerWeight(float t)
        {
            float rampOutFraction = Mathf.Clamp01(PanicSlideUpgrade.MANTLE_BODY_LOWER_RAMP_OUT_FRACTION);
            if (rampOutFraction <= 0.0001f)
                return t >= 1f ? 0f : 1f;

            float rampStart = 1f - rampOutFraction;
            if (t <= rampStart)
                return 1f;

            float rampT = Mathf.Clamp01((t - rampStart) / rampOutFraction);
            return 1f - Mathf.SmoothStep(0f, 1f, rampT);
        }

        // Info level on purpose: the BepInEx disk logger drops Debug lines, and these numbers
        // exist to be read out of LogOutput.log after a playtest. Throttled to ~5 Hz.
        private static void LogMantleArmsDiagnostics(PlayerControllerB player)
        {
            if (player == null || Time.time < _nextMantleArmsDiagnosticAt)
                return;

            _nextMantleArmsDiagnosticAt = Time.time + 0.2f;
            // Ask the render-truth hook to log its same-frame diff alongside this line.
            _renderDiagLogThisFrame = true;
            Transform armsTransform = player.localArmsTransform;
            Transform cameraTransform = player.gameplayCamera != null ? player.gameplayCamera.transform : null;
            string delta = armsTransform != null && cameraTransform != null
                ? Vector3.Distance(armsTransform.position, cameraTransform.position).ToString("F4")
                : "<n/a>";

            ResolveMantleArmsHandBones(player);
            string metarigScale = player.playerModelArmsMetarig != null
                ? player.playerModelArmsMetarig.lossyScale.ToString("F3")
                : "<null>";
            string containerPos = FormatNullablePosition(player.cameraContainerTransform);

            Plugin.Log?.LogDebug(
                "[Panic Slide] Mantle FP arms diagnostics: " +
                $"frame={Time.frameCount}, " +
                $"localArmsTransform.position={FormatNullablePosition(armsTransform)}, " +
                $"camera.position={FormatNullablePosition(cameraTransform)}, " +
                $"cameraContainer.position={containerPos}, " +
                $"armsToCameraMeters={delta}, " +
                $"handL(cam-local)={FormatCameraLocal(cameraTransform, _mantleArmsHandLeft)}, " +
                $"handR(cam-local)={FormatCameraLocal(cameraTransform, _mantleArmsHandRight)}, " +
                $"armsMetarigScale={metarigScale}, " +
                $"fpvBodyView={SlideAnimationBridge.IsFirstPersonViewLocalBodyShown()}.");

            // Rig-anchor line (#347 session 3): everything Y4NGZFpMantleCompositionPreview needs
            // to place its camera exactly where the live one sits relative to the arms rig. The
            // mirror prefab's root geometry does not match the live rig (metarig localPos/scale
            // differ), so the preview must be anchored from these live numbers, not the hierarchy.
            Transform metarigTransform = player.playerModelArmsMetarig;
            Transform rotationTarget = player.localArmsRotationTarget;
            Plugin.Log?.LogDebug(
                "[Panic Slide] Mantle FP arms rig anchors: " +
                $"frame={Time.frameCount}, " +
                $"metarig(cam-local)={FormatCameraLocal(cameraTransform, metarigTransform)}, " +
                $"metarigLocalPos={FormatNullableLocalPosition(metarigTransform)}, " +
                $"metarigLocalEuler={FormatNullableLocalEuler(metarigTransform)}, " +
                $"metarigRotDeltaVsCam={FormatRotationDelta(cameraTransform, metarigTransform)}, " +
                $"rotTargetDeltaVsCam={FormatRotationDelta(cameraTransform, rotationTarget)}, " +
                $"armsRootLocalEuler={FormatNullableLocalEuler(armsTransform)}, " +
                $"armsRoot(cam-local)={FormatCameraLocal(cameraTransform, armsTransform)}, " +
                $"spine003(cam-local)={FormatCameraLocal(cameraTransform, _mantleArmsSpine003)}, " +
                $"spine003LocalPos={FormatNullableLocalPosition(_mantleArmsSpine003)}, " +
                $"spine003LocalEuler={FormatNullableLocalEuler(_mantleArmsSpine003)}, " +
                $"shoulderL(cam-local)={FormatCameraLocal(cameraTransform, _mantleArmsShoulderLeft)}, " +
                $"shoulderR(cam-local)={FormatCameraLocal(cameraTransform, _mantleArmsShoulderRight)}, " +
                $"targetL(cam-local)={FormatCameraLocal(cameraTransform, _mantleArmsTargetLeft)}, " +
                $"targetR(cam-local)={FormatCameraLocal(cameraTransform, _mantleArmsTargetRight)}.");
        }

        private static string FormatNullableLocalPosition(Transform transform)
        {
            return transform == null ? "<null>" : transform.localPosition.ToString("F4");
        }

        private static string FormatNullableLocalEuler(Transform transform)
        {
            return transform == null ? "<null>" : transform.localEulerAngles.ToString("F2");
        }

        // Rotation of 'bone' expressed in the camera's frame, as euler angles. (0,0,0) means the
        // bone's world rotation equals the camera's.
        private static string FormatRotationDelta(Transform cameraTransform, Transform bone)
        {
            if (cameraTransform == null || bone == null)
                return "<null>";

            return (Quaternion.Inverse(cameraTransform.rotation) * bone.rotation).eulerAngles.ToString("F2");
        }

        // Camera-local hand positions are the decisive number: x right / y up / z forward of
        // the camera. Healthy viewmodel hands sit around z 0.3-0.7; z near 0 or negative means
        // the hand geometry is at/behind the near plane (the "giant glove" symptom).
        private static string FormatCameraLocal(Transform cameraTransform, Transform bone)
        {
            if (cameraTransform == null || bone == null)
                return "<null>";

            return cameraTransform.InverseTransformPoint(bone.position).ToString("F3");
        }

        private static void SubscribeMantleRenderTruth()
        {
            if (_renderDiagSubscribed)
                return;

            _renderDiagSubscribed = true;
            _renderDiagSnapshotFrame = -1;
            _renderDiagLogThisFrame = false;
            RenderPipelineManager.beginCameraRendering += OnMantleBeginCameraRendering;
        }

        private static void UnsubscribeMantleRenderTruth()
        {
            if (!_renderDiagSubscribed)
                return;

            _renderDiagSubscribed = false;
            _renderDiagSnapshotFrame = -1;
            _renderDiagLogThisFrame = false;
            RenderPipelineManager.beginCameraRendering -= OnMantleBeginCameraRendering;
        }

        // Runs at the very end of our LateUpdate postfix: this is the state WE wrote. Whatever
        // beginCameraRendering sees later in the same frame is the state that actually renders.
        private static void CaptureMantleRenderTruthSnapshot(PlayerControllerB player)
        {
            Transform cameraTransform = player != null && player.gameplayCamera != null
                ? player.gameplayCamera.transform
                : null;
            if (cameraTransform == null)
                return;

            _renderDiagSnapshotFrame = Time.frameCount;
            _renderDiagCamPos = cameraTransform.position;
            _renderDiagCamRot = cameraTransform.rotation;
            _renderDiagHandLCam = CameraLocalOrZero(cameraTransform, _mantleArmsHandLeft);
            _renderDiagHandRCam = CameraLocalOrZero(cameraTransform, _mantleArmsHandRight);
            _renderDiagArmsRootCam = CameraLocalOrZero(cameraTransform, player.localArmsTransform);
            _renderDiagArmsRootWorld = player.localArmsTransform != null
                ? player.localArmsTransform.position
                : Vector3.zero;
            _renderDiagSpine003Cam = CameraLocalOrZero(cameraTransform, _mantleArmsSpine003);
        }

        private static Vector3 CameraLocalOrZero(Transform cameraTransform, Transform bone)
        {
            return bone == null ? Vector3.zero : cameraTransform.InverseTransformPoint(bone.position);
        }

        private static void OnMantleBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!_mantleActive || !_renderDiagLogThisFrame)
                return;

            PlayerControllerB player = _mantlePlayer;
            if (player == null || camera == null || player.gameplayCamera != camera)
                return;
            if (Time.frameCount != _renderDiagSnapshotFrame)
                return;

            // Log only the first gameplay-camera render of the frame — that is what the player sees.
            _renderDiagLogThisFrame = false;

            Transform cameraTransform = camera.transform;
            Vector3 camPosDelta = cameraTransform.position - _renderDiagCamPos;
            float camRotDeltaDeg = Quaternion.Angle(_renderDiagCamRot, cameraTransform.rotation);
            Vector3 handLNow = CameraLocalOrZero(cameraTransform, _mantleArmsHandLeft);
            Vector3 handRNow = CameraLocalOrZero(cameraTransform, _mantleArmsHandRight);
            Vector3 armsRootCamNow = CameraLocalOrZero(cameraTransform, player.localArmsTransform);
            Vector3 armsRootWorldNow = player.localArmsTransform != null
                ? player.localArmsTransform.position
                : Vector3.zero;
            Vector3 spine003Now = CameraLocalOrZero(cameraTransform, _mantleArmsSpine003);

            Plugin.Log?.LogDebug(
                "[Panic Slide] Mantle render-truth diff: " +
                $"frame={Time.frameCount}, " +
                $"camera={camera.name}, " +
                $"camPosDelta={camPosDelta.ToString("F4")}, " +
                $"camRotDeltaDeg={camRotDeltaDeg:F2}, " +
                $"handL(cam-local render)={handLNow.ToString("F3")}, " +
                $"handLDelta={(handLNow - _renderDiagHandLCam).ToString("F3")}, " +
                $"handR(cam-local render)={handRNow.ToString("F3")}, " +
                $"handRDelta={(handRNow - _renderDiagHandRCam).ToString("F3")}, " +
                $"armsRoot(cam-local render)={armsRootCamNow.ToString("F3")}, " +
                $"armsRootDelta={(armsRootCamNow - _renderDiagArmsRootCam).ToString("F3")}, " +
                $"armsRootWorldDelta={(armsRootWorldNow - _renderDiagArmsRootWorld).ToString("F4")}, " +
                $"spine003(cam-local render)={spine003Now.ToString("F3")}, " +
                $"spine003Delta={(spine003Now - _renderDiagSpine003Cam).ToString("F3")}.");
        }

        // One-shot foreign-writer census: name every Harmony patch owner on the two vanilla
        // methods our movement presentation composes against, plus every non-vanilla component on
        // the player/camera hierarchy, so the Cuckoo-profile writer is identified by name rather
        // than inferred from mod coexistence.
        private static void DumpMovementPatchOwnersOnce(PlayerControllerB player)
        {
            if (_patchOwnerDumpDone || player == null)
                return;

            _patchOwnerDumpDone = true;
            try
            {
                DumpPatchOwners(typeof(PlayerControllerB), "Update");
                DumpPatchOwners(typeof(PlayerControllerB), "LateUpdate");
                DumpForeignPlayerComponents(player);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning($"[Panic Slide] Patch-owner dump failed: {exception}");
            }
        }

        private static void DumpPatchOwners(Type declaringType, string methodName)
        {
            MethodBase method = AccessTools.Method(declaringType, methodName);
            HarmonyLib.Patches patches = method != null ? Harmony.GetPatchInfo(method) : null;
            if (patches == null)
            {
                Plugin.Log?.LogDebug(
                    $"[Panic Slide] Patch owners {declaringType.Name}.{methodName}: none.");
                return;
            }

            var builder = new StringBuilder();
            AppendPatchList(builder, "prefix", patches.Prefixes);
            AppendPatchList(builder, "postfix", patches.Postfixes);
            AppendPatchList(builder, "transpiler", patches.Transpilers);
            AppendPatchList(builder, "finalizer", patches.Finalizers);
            Plugin.Log?.LogDebug(
                $"[Panic Slide] Patch owners {declaringType.Name}.{methodName}: " +
                (builder.Length > 0 ? builder.ToString() : "none") + ".");
        }

        private static void AppendPatchList(
            StringBuilder builder,
            string kind,
            System.Collections.ObjectModel.ReadOnlyCollection<HarmonyLib.Patch> patchList)
        {
            if (patchList == null)
                return;

            foreach (HarmonyLib.Patch patch in patchList)
            {
                if (patch == null || patch.PatchMethod == null)
                    continue;

                if (builder.Length > 0)
                    builder.Append("; ");
                builder.Append(kind)
                    .Append(' ')
                    .Append(patch.owner)
                    .Append("(prio ")
                    .Append(patch.priority)
                    .Append(") ")
                    .Append(patch.PatchMethod.DeclaringType != null
                        ? patch.PatchMethod.DeclaringType.FullName
                        : "<null>")
                    .Append('.')
                    .Append(patch.PatchMethod.Name);
            }
        }

        private static void DumpForeignPlayerComponents(PlayerControllerB player)
        {
            var seen = new HashSet<string>();
            var builder = new StringBuilder();
            int count = 0;
            AppendForeignComponents(player.transform.root, seen, builder, ref count);
            Transform cameraRoot = player.gameplayCamera != null
                ? player.gameplayCamera.transform.root
                : null;
            if (cameraRoot != null && cameraRoot != player.transform.root)
                AppendForeignComponents(cameraRoot, seen, builder, ref count);

            Plugin.Log?.LogDebug(
                $"[Panic Slide] Foreign components on player/camera hierarchy ({count}): " +
                (count > 0 ? builder.ToString() : "none") + ".");
        }

        private static void AppendForeignComponents(
            Transform root,
            HashSet<string> seen,
            StringBuilder builder,
            ref int count)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;

                Type type = component.GetType();
                string assemblyName = type.Assembly.GetName().Name;
                if (IsVanillaAssembly(assemblyName))
                    continue;

                string entry = $"{assemblyName}!{type.FullName} @ {GetTransformPath(component.transform)}";
                if (!seen.Add(entry))
                    continue;

                count++;
                if (builder.Length > 0)
                    builder.Append("; ");
                builder.Append(entry);
            }
        }

        private static bool IsVanillaAssembly(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName))
                return true;

            return assemblyName == "Assembly-CSharp" ||
                assemblyName == "mscorlib" ||
                assemblyName == "netstandard" ||
                assemblyName.StartsWith("System", StringComparison.Ordinal) ||
                assemblyName.StartsWith("Unity", StringComparison.Ordinal) ||
                assemblyName.StartsWith("Dissonance", StringComparison.Ordinal) ||
                assemblyName.StartsWith("Facepunch", StringComparison.Ordinal) ||
                assemblyName.StartsWith("Y4NGZ", StringComparison.Ordinal);
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            var builder = new StringBuilder(transform.name);
            Transform parent = transform.parent;
            while (parent != null)
            {
                builder.Insert(0, '/').Insert(0, parent.name);
                parent = parent.parent;
            }

            return builder.ToString();
        }

        private static void ResolveMantleArmsHandBones(PlayerControllerB player)
        {
            if (_mantleArmsHandBonesResolved)
                return;

            _mantleArmsHandBonesResolved = true;
            Transform metarig = player != null ? player.playerModelArmsMetarig : null;
            if (metarig == null)
                return;

            foreach (Transform child in metarig.GetComponentsInChildren<Transform>(true))
            {
                if (_mantleArmsHandLeft == null && string.Equals(child.name, "hand.L", StringComparison.Ordinal))
                    _mantleArmsHandLeft = child;
                else if (_mantleArmsHandRight == null && string.Equals(child.name, "hand.R", StringComparison.Ordinal))
                    _mantleArmsHandRight = child;
                else if (_mantleArmsSpine003 == null && string.Equals(child.name, "spine.003", StringComparison.Ordinal))
                    _mantleArmsSpine003 = child;
                else if (_mantleArmsShoulderLeft == null && string.Equals(child.name, "shoulder.L", StringComparison.Ordinal))
                    _mantleArmsShoulderLeft = child;
                else if (_mantleArmsShoulderRight == null && string.Equals(child.name, "shoulder.R", StringComparison.Ordinal))
                    _mantleArmsShoulderRight = child;
                else if (_mantleArmsTargetLeft == null && string.Equals(child.name, "ArmsLeftArm_target", StringComparison.Ordinal))
                    _mantleArmsTargetLeft = child;
                else if (_mantleArmsTargetRight == null && string.Equals(child.name, "ArmsRightArm_target", StringComparison.Ordinal))
                    _mantleArmsTargetRight = child;
            }
        }

        private static string FormatNullablePosition(Transform transform)
        {
            if (transform == null)
                return "<null>";

            Vector3 position = transform.position;
            return $"({position.x:F4}, {position.y:F4}, {position.z:F4})";
        }

        private static void ReanchorMantleFirstPersonArms(PlayerControllerB player)
        {
            if (player == null)
                return;

            try
            {
                // Anchor to the ACTUAL rendered camera, NOT cameraContainerTransform. Pass-10
                // diagnostics showed the container diverging ~2.4m from the gameplay camera
                // during the kinematic mover (the arms sat 2.6m off, out of the viewmodel and
                // reading as "hands gone"). gameplayCamera.transform is the true viewpoint, so
                // anchoring there puts the hands at the viewmodel distance regardless of any
                // container lag. -0.5*up drops the arm ROOT to shoulder height (vanilla offset).
                if (player.localArmsTransform != null && player.gameplayCamera != null)
                {
                    Transform cam = player.gameplayCamera.transform;
                    player.localArmsTransform.position = cam.position + cam.up * -0.5f;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"[Panic Slide] Mantle local arms position re-anchor skipped: {ex.Message}");
            }

            try
            {
                if (player.playerModelArmsMetarig != null && player.localArmsRotationTarget != null)
                    player.playerModelArmsMetarig.rotation = player.localArmsRotationTarget.rotation;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"[Panic Slide] Mantle arms metarig rotation re-anchor skipped: {ex.Message}");
            }
        }

        // Keep the mantle body OUT OF THE FIRST-PERSON VIEW WITHOUT hiding it from third-person
        // cameras. The full-body mantle clip plays (bridge weight 1) so the debug/spectate cam
        // and remote clients see the animation; we only need the FP gameplay camera to skip the
        // body. Do it exactly the way vanilla hides the local body: by LAYER. thisPlayerModel
        // is already on the FP-excluded layer (it never appeared in the occluder dump) — its
        // LODs had drifted onto a visible layer, so we pin them to thisPlayerModel's layer for
        // the duration. forceRenderingOff (pass 12) was wrong: it hid the body from ALL cameras,
        // making the player invisible in third person. Head cosmetics (localVisor /
        // headCostumeContainerLocal) are first-person-only, so those we simply disable.
        private static void HideLocalMantleBody(PlayerControllerB player)
        {
            ResolveMantleHideTargets(player);

            if (_mantleFpHiddenLayer >= 0)
            {
                for (int i = 0; i < _mantleBodyLods.Length; i++)
                {
                    Renderer renderer = _mantleBodyLods[i];
                    if (renderer != null && renderer.gameObject != null)
                        renderer.gameObject.layer = _mantleFpHiddenLayer;
                }
            }

            // The layer-based hide relies on the gameplay camera not rendering the local-body
            // layer (vanilla 23). First-Person View defeats exactly that: it moves the body to a
            // rendered layer, force-adds layer 23 to the camera's culling mask, and force-LODs
            // the body group — putting the camera inside a fully visible body during the mantle.
            // FPV never touches forceRenderingOff, so while it is showing the local body we hide
            // the body meshes with that instead (asserted every frame at priority -100, after
            // FPV's pass). Without FPV the layer hide keeps working and body shadows survive.
            bool forceHide = SlideAnimationBridge.IsFirstPersonViewLocalBodyShown();
            SetMantleBodyForceHidden(player, forceHide);

            for (int i = 0; i < _mantleHeadCosmetics.Length; i++)
            {
                Renderer renderer = _mantleHeadCosmetics[i];
                if (renderer != null)
                    renderer.enabled = false;
            }
        }

        private static void SetMantleBodyForceHidden(PlayerControllerB player, bool hidden)
        {
            if (player == null)
                return;

            SetForceRenderingOff(player.thisPlayerModel, hidden);
            SetForceRenderingOff(player.thisPlayerModelLOD1, hidden);
            SetForceRenderingOff(player.thisPlayerModelLOD2, hidden);
            _mantleBodyForceHidden = hidden;
        }

        private static void SetForceRenderingOff(Renderer renderer, bool value)
        {
            if (renderer != null && renderer.forceRenderingOff != value)
                renderer.forceRenderingOff = value;
        }

        private static void RestoreLocalMantleBody()
        {
            if (_mantleBodyForceHidden)
                SetMantleBodyForceHidden(_mantlePlayer, false);

            for (int i = 0; i < _mantleBodyLods.Length; i++)
            {
                Renderer renderer = _mantleBodyLods[i];
                if (renderer != null && renderer.gameObject != null && i < _mantleBodyLodSavedLayers.Length)
                    renderer.gameObject.layer = _mantleBodyLodSavedLayers[i];
            }

            for (int i = 0; i < _mantleHeadCosmetics.Length; i++)
            {
                Renderer renderer = _mantleHeadCosmetics[i];
                if (renderer != null && i < _mantleHeadCosmeticSavedEnabled.Length)
                    renderer.enabled = _mantleHeadCosmeticSavedEnabled[i];
            }

            _mantleHideResolved = false;
            _mantleBodyLods = Array.Empty<Renderer>();
            _mantleBodyLodSavedLayers = Array.Empty<int>();
            _mantleFpHiddenLayer = -1;
            _mantleHeadCosmetics = Array.Empty<Renderer>();
            _mantleHeadCosmeticSavedEnabled = Array.Empty<bool>();
        }

        private static void ResolveMantleHideTargets(PlayerControllerB player)
        {
            if (_mantleHideResolved || player == null)
                return;

            _mantleHideResolved = true;
            _mantleFpHiddenLayer = player.thisPlayerModel != null && player.thisPlayerModel.gameObject != null
                ? player.thisPlayerModel.gameObject.layer
                : -1;

            var lods = new System.Collections.Generic.List<Renderer>();
            AddRenderer(player.thisPlayerModelLOD1, lods);
            AddRenderer(player.thisPlayerModelLOD2, lods);
            _mantleBodyLods = lods.ToArray();
            _mantleBodyLodSavedLayers = new int[_mantleBodyLods.Length];
            for (int i = 0; i < _mantleBodyLods.Length; i++)
                _mantleBodyLodSavedLayers[i] = _mantleBodyLods[i] != null && _mantleBodyLods[i].gameObject != null
                    ? _mantleBodyLods[i].gameObject.layer
                    : 0;

            var cosmetics = new System.Collections.Generic.List<Renderer>();
            AddRenderersUnder(player.localVisor, cosmetics);
            AddRenderersUnder(player.headCostumeContainerLocal, cosmetics);
            _mantleHeadCosmetics = cosmetics.ToArray();
            _mantleHeadCosmeticSavedEnabled = new bool[_mantleHeadCosmetics.Length];
            for (int i = 0; i < _mantleHeadCosmetics.Length; i++)
                _mantleHeadCosmeticSavedEnabled[i] = _mantleHeadCosmetics[i] != null && _mantleHeadCosmetics[i].enabled;
        }

        private static void AddRenderer(Renderer renderer, System.Collections.Generic.List<Renderer> renderers)
        {
            if (renderer != null && !renderers.Contains(renderer))
                renderers.Add(renderer);
        }

        // Toggle the first-person arms viewmodel (ScavengerModelArmsOnly, the parent of
        // playerModelArmsMetarig) on/off. Used to hide the phantom extra arms from the debug
        // third-person cam while keeping them visible in the real first-person view.
        private static void SetMantleFirstPersonArmsHidden(PlayerControllerB player, bool hidden)
        {
            ResolveMantleFpArmRenderers(player);
            // Re-assert every frame (no change-guard): the FP arms rig can be rebuilt mid-mantle and
            // vanilla also toggles the viewmodel, either of which can silently clear forceRenderingOff
            // and let the phantom arms leak back into the third-person view.
            _mantleFpArmsHidden = hidden;
            for (int i = 0; i < _mantleFpArmRenderers.Length; i++)
            {
                Renderer renderer = _mantleFpArmRenderers[i];
                if (renderer != null)
                    renderer.forceRenderingOff = hidden;
            }
        }

        private static void ResolveMantleFpArmRenderers(PlayerControllerB player)
        {
            if (_mantleFpArmRenderersResolved || player == null)
                return;

            _mantleFpArmRenderersResolved = true;
            Transform metarig = player.playerModelArmsMetarig;
            Transform armsRoot = metarig != null ? metarig.parent : null;
            _mantleFpArmRenderers = armsRoot != null
                ? armsRoot.GetComponentsInChildren<Renderer>(true)
                : Array.Empty<Renderer>();
            _mantleFpArmRendererSet.Clear();
            for (int i = 0; i < _mantleFpArmRenderers.Length; i++)
            {
                if (_mantleFpArmRenderers[i] != null)
                    _mantleFpArmRendererSet.Add(_mantleFpArmRenderers[i]);
            }
        }

        // ---- Third-person ledge grab IK -------------------------------------------------------

        // Plant the body's hands on the actual probed ledge edge so the third-person mantle reads
        // as a real grab instead of hands hovering above the ledge. Applied in LateUpdate after the
        // full-body clip has posed the arms, blended by the camera envelope so it eases in/out.
        private static void ApplyMantleBodyGrabIK(PlayerControllerB player, float weight)
        {
            if (weight <= 0.02f)
                return;

            ResolveBodyArmBones(player);
            ApplyMantleBodyGrabIK(
                weight,
                _mantleLedgeGripCenter,
                _mantleWallNormal,
                _bodyArmUpperL,
                _bodyArmLowerL,
                _bodyHandL,
                _bodyArmUpperR,
                _bodyArmLowerR,
                _bodyHandR);
        }

        private static void ApplyRemoteMantleBodyGrabIK(PlayerControllerB player)
        {
            ulong playerClientId = player.playerClientId;
            if (!RemoteMantles.TryGetValue(playerClientId, out RemoteMantlePresentation remote))
                return;

            if (!(Plugin.RemoteMantleBodyGrabIk?.Value ?? true))
            {
                RemoteMantles.Remove(playerClientId);
                Plugin.Log?.LogDebug(
                    "[Panic Slide.remote-mantle] body_grab_end_gate: " +
                    $"playerClientId={playerClientId} reason='config_disabled'.");
                return;
            }

            if (!ReferenceEquals(remote.Player, player) || !SlideAnimationBridge.IsActive(player))
            {
                RemoteMantles.Remove(playerClientId);
                Plugin.Log?.LogDebug(
                    "[Panic Slide.remote-mantle] body_grab_end_gate: " +
                    $"playerClientId={playerClientId} reason='" +
                    $"{(!ReferenceEquals(remote.Player, player) ? "player_replaced" : "bridge_inactive")}'.");
                return;
            }

            float weight = remote.CalculateGrabEnvelope();
            if (weight <= 0.02f)
            {
                remote.LogBodyGrabGate("envelope_zero", weight);
                return;
            }

            remote.ResolveBodyArmBones();
            if (!remote.HasBodyArmBones)
            {
                remote.LogBodyGrabGate("bones_missing", weight);
                return;
            }

            ApplyRemoteMantleBodyGrabPose(
                remote,
                weight);
        }

        private static void ApplyRemoteMantleBodyGrabPose(
            RemoteMantlePresentation remote,
            float weight)
        {
            AssignMantleGrips(
                remote.BodyHandL,
                remote.BodyHandR,
                remote.LedgeGripCenter,
                remote.WallNormal,
                out Vector3 gripL,
                out Vector3 gripR);
            bool leftSolved = SolveArmBendPreservingBlended(
                remote.BodyArmUpperL,
                remote.BodyArmLowerL,
                remote.BodyHandL,
                gripL,
                weight,
                out bool leftReachClamped);
            bool rightSolved = SolveArmBendPreservingBlended(
                remote.BodyArmUpperR,
                remote.BodyArmLowerR,
                remote.BodyHandR,
                gripR,
                weight,
                out bool rightReachClamped);
            if (!leftSolved || !rightSolved)
            {
                remote.LogBodyGrabGate("solve_skipped", weight);
                return;
            }

            remote.LogBodyGrabGate(
                leftReachClamped || rightReachClamped
                    ? "applied_bend_preserving_clamped"
                    : "applied_bend_preserving",
                weight);
        }

        private static void ApplyMantleBodyGrabIK(
            float weight,
            Vector3 ledgeGripCenter,
            Vector3 wallNormal,
            Transform armUpperL,
            Transform armLowerL,
            Transform handL,
            Transform armUpperR,
            Transform armLowerR,
            Transform handR)
        {
            if (weight <= 0.02f
                || armUpperL == null
                || armLowerL == null
                || handL == null
                || armUpperR == null
                || armLowerR == null
                || handR == null)
            {
                return;
            }

            AssignMantleGrips(
                handL,
                handR,
                ledgeGripCenter,
                wallNormal,
                out Vector3 gripL,
                out Vector3 gripR);
            SolveArmFabrikBlended(armUpperL, armLowerL, handL, gripL, weight);
            SolveArmFabrikBlended(armUpperR, armLowerR, handR, gripR, weight);
        }

        // First-person viewmodel grab. The FP arms are a short camera-anchored viewmodel, so they
        // can't physically reach a world ledge a metre away — a straight world IK would clamp and
        // still float short. Instead we project each world grip onto the viewmodel plane (a fixed
        // reachable camera-space depth) along the camera->grip ray, so the hands sit exactly on the
        // ledge's on-SCREEN position (occluding it) at a depth the short arms can reach. As the
        // camera dips/rises during the mantle the ledge's screen position moves and the hands track
        // it, so they read as staying attached to the surface. Applied after the clip + ChainIK.
        private static void ApplyMantleFpArmGrabIK(PlayerControllerB player, float weight)
        {
            if (weight <= 0.02f)
                return;

            ResolveFpArmBones(player);
            if (_fpArmUpperL == null || _fpArmLowerL == null || _fpHandL == null ||
                _fpArmUpperR == null || _fpArmLowerR == null || _fpHandR == null)
                return;

            Camera cam = player.gameplayCamera;
            if (cam == null)
                return;

            Vector3 tangent = Vector3.Cross(_mantleWallNormal, Vector3.up);
            tangent = tangent.sqrMagnitude < 1e-5f ? Vector3.right : tangent.normalized;
            Vector3 targetA = ProjectGripToViewmodel(cam, _mantleLedgeGripCenter - tangent * MANTLE_GRAB_HAND_SPREAD);
            Vector3 targetB = ProjectGripToViewmodel(cam, _mantleLedgeGripCenter + tangent * MANTLE_GRAB_HAND_SPREAD);

            Vector3 tL, tR;
            if (Vector3.Distance(_fpHandL.position, targetB) + Vector3.Distance(_fpHandR.position, targetA) <
                Vector3.Distance(_fpHandL.position, targetA) + Vector3.Distance(_fpHandR.position, targetB))
            {
                tL = targetB;
                tR = targetA;
            }
            else
            {
                tL = targetA;
                tR = targetB;
            }

            SolveArmFabrikBlended(_fpArmUpperL, _fpArmLowerL, _fpHandL, tL, weight);
            SolveArmFabrikBlended(_fpArmUpperR, _fpArmLowerR, _fpHandR, tR, weight);
        }

        // Map a world point to the point at MANTLE_FP_GRAB_DEPTH along the camera->world-point ray,
        // i.e. the same on-screen position at viewmodel depth.
        private static Vector3 ProjectGripToViewmodel(Camera cam, Vector3 worldGrip)
        {
            Vector3 dirCam = cam.transform.InverseTransformDirection(worldGrip - cam.transform.position);
            if (dirCam.z < 0.05f)
                dirCam.z = 0.05f; // guard against grips level with / behind the camera
            Vector3 handCam = dirCam * (MANTLE_FP_GRAB_DEPTH / dirCam.z);
            return cam.transform.TransformPoint(handCam);
        }

        // Two grab points on the ledge lip (spread along the edge), assigned to whichever hand is
        // nearer so the arms never cross.
        private static void AssignMantleGrips(Transform handL, Transform handR, out Vector3 gripL, out Vector3 gripR)
        {
            AssignMantleGrips(
                handL,
                handR,
                _mantleLedgeGripCenter,
                _mantleWallNormal,
                out gripL,
                out gripR);
        }

        private static void AssignMantleGrips(
            Transform handL,
            Transform handR,
            Vector3 ledgeGripCenter,
            Vector3 wallNormal,
            out Vector3 gripL,
            out Vector3 gripR)
        {
            Vector3 tangent = Vector3.Cross(wallNormal, Vector3.up);
            tangent = tangent.sqrMagnitude < 1e-5f ? Vector3.right : tangent.normalized;
            Vector3 gripA = ledgeGripCenter - tangent * MANTLE_GRAB_HAND_SPREAD;
            Vector3 gripB = ledgeGripCenter + tangent * MANTLE_GRAB_HAND_SPREAD;

            if (Vector3.Distance(handL.position, gripB) + Vector3.Distance(handR.position, gripA) <
                Vector3.Distance(handL.position, gripA) + Vector3.Distance(handR.position, gripB))
            {
                gripL = gripB;
                gripR = gripA;
            }
            else
            {
                gripL = gripA;
                gripR = gripB;
            }
        }

        private static void ResolveFpArmBones(PlayerControllerB player)
        {
            if (_mantleFpArmBonesResolved)
                return;

            _mantleFpArmBonesResolved = true;
            Transform metarig = player != null ? player.playerModelArmsMetarig : null;
            if (metarig == null)
                return;

            _fpArmUpperL = FindDeep(metarig, "arm.L_upper");
            _fpArmLowerL = FindDeep(metarig, "arm.L_lower");
            _fpHandL = FindDeep(metarig, "hand.L");
            _fpArmUpperR = FindDeep(metarig, "arm.R_upper");
            _fpArmLowerR = FindDeep(metarig, "arm.R_lower");
            _fpHandR = FindDeep(metarig, "hand.R");
        }

        // Analytic FABRIK (root->mid->tip), then slerp the resulting upper/lower world rotations
        // from the animated pose toward the solved pose by weight. Leaves the hand's own rotation
        // to the clip; only the reach is overridden.
        private static void SolveArmFabrikBlended(Transform upper, Transform lower, Transform hand, Vector3 target, float weight)
        {
            weight = Mathf.Clamp01(weight);
            if (weight <= 0.001f)
                return;

            Quaternion upperAnim = upper.rotation;
            Quaternion lowerAnim = lower.rotation;

            Vector3 p0 = upper.position, p1 = lower.position, p2 = hand.position;
            float d0 = Vector3.Distance(p0, p1);
            float d1 = Vector3.Distance(p1, p2);
            if (d0 < 1e-4f || d1 < 1e-4f)
                return;

            if (Vector3.Distance(p0, target) >= d0 + d1)
            {
                Vector3 dir = (target - p0).normalized;
                p1 = p0 + dir * d0;
                p2 = p1 + dir * d1;
            }
            else
            {
                for (int i = 0; i < 10 && Vector3.Distance(p2, target) > 1e-4f; i++)
                {
                    p2 = target;
                    p1 = p2 + (p1 - p2).normalized * d1;
                    p1 = p0 + (p1 - p0).normalized * d0;
                    p2 = p1 + (p2 - p1).normalized * d1;
                }
            }

            Vector3 oldDir0 = lower.position - upper.position;
            if (oldDir0.sqrMagnitude > 1e-8f)
                upper.rotation = Quaternion.FromToRotation(oldDir0, p1 - p0) * upper.rotation;
            Vector3 oldDir1 = hand.position - lower.position;
            if (oldDir1.sqrMagnitude > 1e-8f)
                lower.rotation = Quaternion.FromToRotation(oldDir1, p2 - p1) * lower.rotation;

            upper.rotation = Quaternion.Slerp(upperAnim, upper.rotation, weight);
            lower.rotation = Quaternion.Slerp(lowerAnim, lower.rotation, weight);
        }

        // Remote roots arrive through NetworkTransform interpolation and can trail the replicated
        // ledge target far enough that the old FABRIK unreachable branch made shoulder, elbow, and
        // hand exactly collinear. Keep a small bend and the clip's elbow side instead: this gives
        // the grab most of its reach without replacing the authored silhouette with a straight line.
        private static bool SolveArmBendPreservingBlended(
            Transform upper,
            Transform lower,
            Transform hand,
            Vector3 target,
            float weight,
            out bool reachClamped)
        {
            reachClamped = false;
            weight = Mathf.Clamp01(weight);
            if (weight <= 0.001f || upper == null || lower == null || hand == null)
                return false;

            Quaternion upperAnim = upper.rotation;
            Quaternion lowerAnim = lower.rotation;
            Vector3 p0 = upper.position;
            Vector3 p1 = lower.position;
            Vector3 p2 = hand.position;
            float upperLength = Vector3.Distance(p0, p1);
            float lowerLength = Vector3.Distance(p1, p2);
            Vector3 toTarget = target - p0;
            float targetDistance = toTarget.magnitude;
            if (upperLength < 1e-4f || lowerLength < 1e-4f || targetDistance < 1e-4f)
                return false;

            Vector3 targetDirection = toTarget / targetDistance;
            float minimumReach =
                Mathf.Abs(upperLength - lowerLength) + 0.0001f;
            float maximumReach = Mathf.Max(
                minimumReach,
                (upperLength + lowerLength) * REMOTE_MANTLE_MAX_REACH_FRACTION);
            float solvedDistance = Mathf.Clamp(targetDistance, minimumReach, maximumReach);
            reachClamped =
                targetDistance < minimumReach || targetDistance > maximumReach;

            Vector3 animatedUpper = p1 - p0;
            Vector3 bendDirection =
                animatedUpper - targetDirection * Vector3.Dot(animatedUpper, targetDirection);
            if (bendDirection.sqrMagnitude < 1e-8f)
            {
                bendDirection = Vector3.Cross(targetDirection, Vector3.up);
                if (bendDirection.sqrMagnitude < 1e-8f)
                    bendDirection = Vector3.Cross(targetDirection, Vector3.forward);
            }
            bendDirection.Normalize();

            float elbowAlong =
                (upperLength * upperLength
                 - lowerLength * lowerLength
                 + solvedDistance * solvedDistance)
                / (2f * solvedDistance);
            float elbowAway = Mathf.Sqrt(
                Mathf.Max(
                    0f,
                    upperLength * upperLength - elbowAlong * elbowAlong));
            Vector3 solvedElbow =
                p0 + targetDirection * elbowAlong + bendDirection * elbowAway;
            Vector3 solvedHand = p0 + targetDirection * solvedDistance;

            Vector3 animatedUpperDirection = p1 - p0;
            if (animatedUpperDirection.sqrMagnitude > 1e-8f)
            {
                upper.rotation =
                    Quaternion.FromToRotation(
                        animatedUpperDirection,
                        solvedElbow - p0)
                    * upper.rotation;
            }

            Vector3 currentLowerDirection = hand.position - lower.position;
            Vector3 solvedLowerDirection = solvedHand - solvedElbow;
            if (currentLowerDirection.sqrMagnitude > 1e-8f
                && solvedLowerDirection.sqrMagnitude > 1e-8f)
            {
                lower.rotation =
                    Quaternion.FromToRotation(
                        currentLowerDirection,
                        solvedLowerDirection)
                    * lower.rotation;
            }

            Quaternion upperSolved = upper.rotation;
            Quaternion lowerSolved = lower.rotation;
            upper.rotation = upperAnim;
            lower.rotation = lowerAnim;
            upper.rotation = Quaternion.Slerp(upperAnim, upperSolved, weight);
            lower.rotation = Quaternion.Slerp(lowerAnim, lowerSolved, weight);
            return true;
        }

        private static void ResolveBodyArmBones(PlayerControllerB player)
        {
            if (_mantleBodyArmBonesResolved)
                return;

            _mantleBodyArmBonesResolved = true;
            if (player == null)
                return;

            // The body metarig is the "metarig" whose parent is "ScavengerModel" (NOT the
            // first-person "ScavengerModelArmsOnly" viewmodel rig).
            Transform bodyMetarig = null;
            foreach (Transform t in player.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(t.name, "metarig", StringComparison.Ordinal) &&
                    t.parent != null && string.Equals(t.parent.name, "ScavengerModel", StringComparison.Ordinal))
                {
                    bodyMetarig = t;
                    break;
                }
            }
            if (bodyMetarig == null)
                return;

            _bodyArmUpperL = FindDeep(bodyMetarig, "arm.L_upper");
            _bodyArmLowerL = FindDeep(bodyMetarig, "arm.L_lower");
            _bodyHandL = FindDeep(bodyMetarig, "hand.L");
            _bodyArmUpperR = FindDeep(bodyMetarig, "arm.R_upper");
            _bodyArmLowerR = FindDeep(bodyMetarig, "arm.R_lower");
            _bodyHandR = FindDeep(bodyMetarig, "hand.R");
        }

        // ---- Dynamic first-person view-occluder cull -----------------------------------------

        private static void CullMantleViewOccluders(PlayerControllerB player)
        {
            RestoreMantleViewOccluders();
            if (player == null || player.gameplayCamera == null)
                return;

            ResolveMantleFpArmRenderers(player);
            ResolveMantleOccluderCandidates(player);
            Camera cam = player.gameplayCamera;
            int mask = cam.cullingMask;
            Vector3 camPos = cam.transform.position;
            float radiusSqr = MANTLE_OCCLUDER_CULL_RADIUS * MANTLE_OCCLUDER_CULL_RADIUS;

            for (int index = 0; index < _mantleOccluderCandidates.Length; index++)
            {
                Renderer renderer = _mantleOccluderCandidates[index];
                if (renderer == null || !renderer.enabled || renderer.gameObject == null)
                    continue;
                int layer = renderer.gameObject.layer;
                if (layer < 0 || layer >= 32 || (mask & (1 << layer)) == 0)
                    continue;
                if (IsMantleFpArmRenderer(renderer))
                    continue; // never cull the viewmodel arms — those we want to see
                float sqr = renderer.bounds.SqrDistance(camPos);
                if (sqr > radiusSqr)
                    continue;

                renderer.forceRenderingOff = true;
                _mantleTempCulled.Add(renderer);
                if (_mantleOccluderLogged.Add(renderer.name))
                    Plugin.Log?.LogDebug(
                        $"[Panic Slide] Mantle FP occluder culled: '{renderer.name}' (layer={layer}, dist={Mathf.Sqrt(sqr):F2})");
            }
        }

        private static void RestoreMantleViewOccluders()
        {
            for (int i = 0; i < _mantleTempCulled.Count; i++)
                if (_mantleTempCulled[i] != null)
                    _mantleTempCulled[i].forceRenderingOff = false;
            _mantleTempCulled.Clear();
        }

        private static bool IsMantleFpArmRenderer(Renderer renderer)
        {
            return renderer != null && _mantleFpArmRendererSet.Contains(renderer);
        }

        // F-ESCAPE-6: resolved once per mantle alongside ResolveMantleHideTargets; dropped in
        // RestoreMantleState so the next mantle re-scans a possibly rebuilt player model.
        private static void ResolveMantleOccluderCandidates(PlayerControllerB player)
        {
            if (_mantleOccluderCandidatesResolved || player == null)
                return;

            _mantleOccluderCandidatesResolved = true;
            _mantleOccluderCandidates = player.GetComponentsInChildren<Renderer>(true);
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
                return null;
            if (string.Equals(root.name, name, StringComparison.Ordinal))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        // Reflection read of Y4NGZDebugTools.DebugGUI.ThirdPersonCam (a separate mod assembly;
        // absent when Diagnostics isn't installed → treated as false).
        private static bool _debugThirdPersonResolved;
        private static System.Reflection.FieldInfo _debugThirdPersonField;
        private static System.Reflection.FieldInfo _debugStaticCamField;

        internal static bool IsDebugThirdPersonCamActive()
        {
            if (!_debugThirdPersonResolved)
            {
                _debugThirdPersonResolved = true;
                try
                {
                    Type type = Type.GetType("Y4NGZDebugTools.DebugGUI, Y4NGZDebugTools", false);
                    if (type == null)
                    {
                        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                        for (int i = 0; i < assemblies.Length; i++)
                        {
                            try { type = assemblies[i].GetType("Y4NGZDebugTools.DebugGUI", false); } catch { type = null; }
                            if (type != null) break;
                        }
                    }

                    if (type != null)
                    {
                        _debugThirdPersonField = type.GetField("ThirdPersonCam",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        // The debug tools have a second external-body view: "Freeze Camera Here"
                        // (StaticCamActive). Their own view-state patch treats the pair as one
                        // condition (ThirdPersonCam || StaticCamActive) — mirror that here so
                        // every debug-cam guard covers both modes.
                        _debugStaticCamField = type.GetField("StaticCamActive",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (_debugThirdPersonField == null)
                            Plugin.Log?.LogWarning(
                                "[Panic Slide] Y4NGZDebugTools.DebugGUI is loaded but its ThirdPersonCam field could not be resolved; the debug third-person camera gate is disabled.");
                        if (_debugStaticCamField == null)
                            Plugin.Log?.LogWarning(
                                "[Panic Slide] Y4NGZDebugTools.DebugGUI is loaded but its StaticCamActive field could not be resolved; the frozen-camera gate is disabled.");
                    }
                }
                catch (Exception ex)
                {
                    _debugThirdPersonField = null;
                    Plugin.Log?.LogWarning(
                        "[Panic Slide] failed to resolve Y4NGZDebugTools.DebugGUI.ThirdPersonCam: " + ex.Message);
                }
            }

            try
            {
                if (_debugThirdPersonField != null &&
                    _debugThirdPersonField.GetValue(null) is bool thirdPerson && thirdPerson)
                {
                    return true;
                }

                return _debugStaticCamField != null &&
                    _debugStaticCamField.GetValue(null) is bool staticCam && staticCam;
            }
            catch { return false; }
        }

        private static void AddRenderersUnder(Transform root, System.Collections.Generic.List<Renderer> renderers)
        {
            if (root == null)
                return;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                AddRenderer(renderer, renderers);
        }

        // One-shot per mantle: name every renderer the gameplay camera actually draws that sits
        // within 1.2m of the camera — i.e. whatever is filling the view. Pinpoints the helmet.
        private static void LogMantleOccluderDumpOnce(PlayerControllerB player)
        {
            if (_mantleOccluderDumpDone || player == null || player.gameplayCamera == null)
                return;

            _mantleOccluderDumpDone = true;
            try
            {
                Camera cam = player.gameplayCamera;
                int mask = cam.cullingMask;
                Vector3 camPos = cam.transform.position;
                var lines = new System.Collections.Generic.List<string>();
                foreach (Renderer renderer in player.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !renderer.enabled || renderer.gameObject == null)
                        continue;
                    int layer = renderer.gameObject.layer;
                    if (layer < 0 || layer >= 32 || (mask & (1 << layer)) == 0)
                        continue;
                    float dist = Vector3.Distance(renderer.bounds.center, camPos);
                    if (dist > 1.2f)
                        continue;
                    lines.Add($"'{renderer.name}'(layer={layer}, dist={dist:F2})");
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide] Mantle occluder dump (renderers drawn by gameplay camera within 1.2m): " +
                    (lines.Count == 0 ? "<none>" : string.Join(", ", lines.ToArray())));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[Panic Slide] Mantle occluder dump failed: {ex.Message}");
            }
        }

        private static void FinishMantle()
        {
            PlayerControllerB player = _mantlePlayer;
            if (player != null)
            {
                SendMantleStop(player.playerClientId);
                SetPlayerPosition(player, _mantleEndPosition);
                ZeroMovementForces(player);
                ZeroFallValues(player);
                // The kinematic mantle is complete at MANTLE_FINISH_T. Relinquish the shared
                // body Animator at the same moment instead of leaving the authored controller
                // active for the clip's remaining settle frames while vanilla movement resumes.
                SlideAnimationBridge.End(player, "mantle-finished");
            }

            RestoreMantleState();
            _cooldownEnd = Time.time + PanicSlideUpgrade.MANTLE_COOLDOWN_SECONDS;
        }

        private static void AbortMantle(string reason, bool restorePosition = true)
        {
            if (!_mantleActive)
                return;

            PlayerControllerB player = _mantlePlayer;
            if (player != null)
                SendMantleStop(player.playerClientId);
            // F-ESCAPE-2: a mantle that dies on its first Update frame never moved the player, so
            // the 8% is refunded rather than charged for nothing.
            if (_mantleStaminaCharged && Time.frameCount <= _mantleStartFrame + 1)
                RefundStaminaCost(player);
            if (restorePosition && player != null && !player.isPlayerDead && _mantleLastSafePosition != Vector3.zero)
                SetPlayerPosition(player, _mantleLastSafePosition);
            if (player != null && _mantleAnimStarted)
            {
                SlideAnimationBridge.TriggerExit(player);
                SlideAnimationBridge.End(player, reason);
            }

            RestoreMantleState();
            _nextRetryTime = Time.time + PanicSlideUpgrade.MANTLE_RETRY_SECONDS;
        }

        private static void RestoreMantleState()
        {
            PlayerControllerB player = _mantlePlayer;
            RestoreInput(player);
            if (player != null)
            {
                ZeroMovementForces(player);
                ZeroFallValues(player);
            }

            RestoreLocalMantleBody();
            // Make sure the FP arms viewmodel is left rendering normally for the next first-person
            // frame, then drop the cached renderer list so a re-resolve happens next mantle.
            SetMantleFirstPersonArmsHidden(player, false);
            _mantleFpArmRenderersResolved = false;
            _mantleFpArmRenderers = Array.Empty<Renderer>();
            _mantleFpArmRendererSet.Clear();
            _mantleFpArmsHidden = false;
            // Un-cull any renderers we force-hid from the first-person lens this mantle.
            RestoreMantleViewOccluders();
            // F-ESCAPE-6: the per-mantle occluder candidate list is re-scanned next mantle.
            _mantleOccluderCandidatesResolved = false;
            _mantleOccluderCandidates = Array.Empty<Renderer>();
            _mantleOccluderLogged.Clear();
            // F-ESCAPE-14: the arms hand/spine/shoulder caches are re-resolved per mantle for the
            // same reason as the body and FP arm bones below — the player model can be rebuilt
            // (respawn), which left the diagnostics reading destroyed transforms.
            _mantleArmsHandBonesResolved = false;
            _mantleArmsHandLeft = _mantleArmsHandRight = _mantleArmsSpine003 = null;
            _mantleArmsShoulderLeft = _mantleArmsShoulderRight = null;
            _mantleArmsTargetLeft = _mantleArmsTargetRight = null;
            // Body + FP arm bones are re-resolved per mantle (the player model can be rebuilt).
            _mantleBodyArmBonesResolved = false;
            _bodyArmUpperL = _bodyArmLowerL = _bodyHandL = null;
            _bodyArmUpperR = _bodyArmLowerR = _bodyHandR = null;
            _mantleFpArmBonesResolved = false;
            _fpArmUpperL = _fpArmLowerL = _fpHandL = null;
            _fpArmUpperR = _fpArmLowerR = _fpHandR = null;
            PanicSlidePatch.RestoreCameraOffset();
            PanicSlidePatch.EndFirstPersonMovementPresentation();
            PanicSlidePatch.EndMovementLookLimit(player);
            UnsubscribeMantleRenderTruth();

            _mantleActive = false;
            _mantleStartFrame = -1;
            _mantleStaminaCharged = false;
            _mantlePlayer = null;
            _mantleStartPosition = Vector3.zero;
            _mantleLiftPosition = Vector3.zero;
            _mantleEndPosition = Vector3.zero;
            _mantleLastSafePosition = Vector3.zero;
            _mantleStartedAt = 0f;
            _mantleDuration = 0f;
            _mantleAnimStarted = false;
            _nextMantleArmsDiagnosticAt = 0f;
        }

        private static void SaveAndLockInput(PlayerControllerB player)
        {
            if (player == null)
                return;

            if (!_mantleInputSaved)
            {
                _savedDisableMoveInput = player.disableMoveInput;
                _mantleInputSaved = true;
            }

            player.disableMoveInput = true;
        }

        private static void RestoreInput(PlayerControllerB player)
        {
            if (_mantleInputSaved && player != null)
                player.disableMoveInput = _savedDisableMoveInput;

            _savedDisableMoveInput = false;
            _mantleInputSaved = false;
        }

        private static void SetPlayerPosition(PlayerControllerB player, Vector3 position)
        {
            if (player == null)
                return;

            CharacterController controller = player.thisController;
            if (controller == null)
            {
                player.transform.position = position;
                return;
            }

            bool wasEnabled = controller.enabled;
            if (wasEnabled)
                controller.enabled = false;
            player.transform.position = position;
            if (wasEnabled)
                controller.enabled = true;
        }

        private static void ApplyStaminaCost(PlayerControllerB player)
        {
            if (player == null)
                return;

            player.sprintMeter = Mathf.Clamp01(player.sprintMeter - PanicSlideUpgrade.MANTLE_STAMINA_COST);
            if (player.sprintMeterUI != null)
                player.sprintMeterUI.fillAmount = player.sprintMeter;
            _mantleStaminaCharged = true;
        }

        // F-ESCAPE-2: first-frame abort refund (see AbortMantle).
        private static void RefundStaminaCost(PlayerControllerB player)
        {
            _mantleStaminaCharged = false;
            if (player == null)
                return;

            player.sprintMeter = Mathf.Clamp01(player.sprintMeter + PanicSlideUpgrade.MANTLE_STAMINA_COST);
            if (player.sprintMeterUI != null)
                player.sprintMeterUI.fillAmount = player.sprintMeter;
        }

        private static void ZeroMovementForces(PlayerControllerB player)
        {
            if (player == null)
                return;

            player.externalForceAutoFade = Vector3.zero;
            ResolveExternalForcesField();
            if (_externalForcesField != null)
            {
                try { _externalForcesField.SetValue(player, Vector3.zero); } catch { }
            }
        }

        private static void ZeroFallValues(PlayerControllerB player)
        {
            if (player == null)
                return;

            ResolveFallFields();
            try
            {
                if (_fallValueField != null)
                    _fallValueField.SetValue(player, 0f);
                if (_fallValueUncappedField != null)
                    _fallValueUncappedField.SetValue(player, 0f);
            }
            catch { }
        }

        private static float CalculateMantleCameraEnvelope()
        {
            if (!_mantleActive || _mantleDuration <= 0.001f)
                return 0f;

            float elapsed = Mathf.Clamp(Time.time - _mantleStartedAt, 0f, _mantleDuration);
            float enterT = Mathf.Clamp01(elapsed / PanicSlideUpgrade.MANTLE_CAMERA_DIP_IN_SECONDS);
            float enter = Mathf.SmoothStep(0f, 1f, enterT);

            // Gameplay hands control back at MANTLE_FINISH_T, so the camera envelope must also
            // be fully settled by that earlier endpoint. Basing this on the full clip duration
            // left a nonzero dip that RestoreCameraOffset snapped away on the teardown frame.
            float visualEnd = _mantleDuration * MANTLE_FINISH_T;
            float exitStart = Mathf.Max(0f, visualEnd - PanicSlideUpgrade.MANTLE_CAMERA_DIP_OUT_SECONDS);
            if (elapsed <= exitStart)
                return enter;

            float exitSpan = Mathf.Max(0.0001f, visualEnd - exitStart);
            float exitT = Mathf.Clamp01((elapsed - exitStart) / exitSpan);
            return Mathf.Min(enter, 1f - Mathf.SmoothStep(0f, 1f, exitT));
        }

        private static bool HasDestinationClearance(CharacterController controller, Vector3 destinationFeet, int mask)
        {
            if (controller == null)
                return false;

            float radius = Mathf.Max(0.05f, controller.radius - 0.02f);
            float height = Mathf.Max(controller.height, radius * 2f + 0.1f);
            Vector3 bottom = destinationFeet + Vector3.up * (radius + CAPSULE_SKIN);
            Vector3 top = destinationFeet + Vector3.up * (height - radius + CAPSULE_SKIN);
            return !Physics.CheckCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore);
        }

        private static bool HasSweptCapsuleClearance(
            CharacterController controller,
            Vector3 fromTransformPosition,
            Vector3 toTransformPosition,
            int mask)
        {
            if (controller == null)
                return false;

            Vector3 delta = toTransformPosition - fromTransformPosition;
            float distance = delta.magnitude;
            if (distance <= 0.001f)
                return true;

            float radius = Mathf.Max(0.05f, controller.radius - MANTLE_PATH_RADIUS_INSET);
            float height = Mathf.Max(controller.height, radius * 2f + 0.1f);
            Vector3 center = fromTransformPosition + controller.center;
            Vector3 bottom = center + Vector3.down * (height * 0.5f - radius);
            Vector3 top = center + Vector3.up * (height * 0.5f - radius);
            return !Physics.CapsuleCast(
                bottom,
                top,
                radius,
                delta / distance,
                out _,
                distance,
                mask,
                QueryTriggerInteraction.Ignore);
        }

        private static bool HasInteriorContainment(PlayerControllerB player, Vector3 destinationFeet, int mask)
        {
            if (player == null || !player.isInsideFactory)
                return true;

            // A legitimate interior landing has dungeon structure above it. The exposed upper
            // face of an interior shell (the common skybox escape target) has open sky, so reject
            // it. This is deliberately only enforced while the player is inside the factory.
            Vector3 origin = destinationFeet + Vector3.up * 0.15f;
            return Physics.Raycast(
                origin,
                Vector3.up,
                MANTLE_INTERIOR_CEILING_CHECK_DISTANCE,
                mask,
                QueryTriggerInteraction.Ignore);
        }

        private static bool IsRejectedMantleSurface(Collider collider)
        {
            if (collider == null)
                return true;

            if (collider.attachedRigidbody != null || collider.GetComponentInParent<Rigidbody>() != null)
                return true;
            if (collider.GetComponentInParent<EnemyAI>() != null)
                return true;

            return false;
        }

        private static float ResolveFeetY(PlayerControllerB player, CharacterController controller)
        {
            if (controller != null)
            {
                Bounds bounds = controller.bounds;
                if (bounds.size.y > 0.01f)
                    return bounds.min.y;
            }

            return player != null ? player.transform.position.y : 0f;
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

        private static bool HasMantleIntent(PlayerControllerB player, Vector3 forward, Vector3 wallNormal)
        {
            Vector3 horizontalForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            Vector3 horizontalWallNormal = Vector3.ProjectOnPlane(wallNormal, Vector3.up);
            if (horizontalForward.sqrMagnitude > 0.01f && horizontalWallNormal.sqrMagnitude > 0.01f)
            {
                float facingWall = Vector3.Dot(horizontalForward.normalized, -horizontalWallNormal.normalized);
                if (facingWall > 0.35f)
                    return true;
            }

            if (player == null || player.thisController == null)
                return false;

            Vector3 velocity = player.thisController.velocity;
            velocity.y = 0f;
            if (velocity.sqrMagnitude < 0.0001f || horizontalForward.sqrMagnitude <= 0.01f)
                return false;

            return Vector3.Dot(velocity, horizontalForward.normalized) > 0.25f;
        }

        // F-ESCAPE-6: this runs from CanTryMantle on every airborne frame of a tier-2 owner, not
        // just during a mantle. The old resolver probed four candidate member names by reflection
        // and boxed a bool through FieldInfo.GetValue each time; of those names only
        // inVehicleAnimation exists in v81, and it is a public field.
        private static bool IsPlayerInVehicle(PlayerControllerB player)
        {
            return player != null && player.inVehicleAnimation;
        }

        private static void ResolveFallFields()
        {
            if (_fallFieldsResolved)
                return;

            _fallFieldsResolved = true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fallValueField = FindFloatField("fallValue", flags);
            _fallValueUncappedField = FindFloatField("fallValueUncapped", flags);
        }

        private static void ResolveExternalForcesField()
        {
            if (_externalForcesResolved)
                return;

            _externalForcesResolved = true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo field = typeof(PlayerControllerB).GetField("externalForces", flags);
            if (field != null && field.FieldType == typeof(Vector3))
                _externalForcesField = field;
        }

        private static FieldInfo FindFloatField(string name, BindingFlags flags)
        {
            FieldInfo field = typeof(PlayerControllerB).GetField(name, flags);
            return field != null && field.FieldType == typeof(float) ? field : null;
        }

        /// <summary>
        /// Netcode nulls the CustomMessagingManager on shutdown and builds a fresh one for the next
        /// host/join, so this latch has to drop or the second lobby of a session never receives
        /// remote mantles.
        /// </summary>
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _handlersRegistered = false;
            RemoteMantles.Clear();
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered)
                return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_MANTLE_START, OnReceiveMantleStart);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_MANTLE_STOP, OnReceiveMantleStop);
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"LedgeMantlePatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

        private static void SendMantleStart(
            ulong playerClientId,
            byte clipId,
            float sessionSeconds,
            Vector3 ledgeGripCenter,
            Vector3 wallNormal)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient || network.CustomMessagingManager == null)
                return;

            if (network.IsServer)
            {
                SendMantleStart(
                    playerClientId,
                    clipId,
                    sessionSeconds,
                    ledgeGripCenter,
                    wallNormal,
                    includeExtension: true,
                    clientId: null);
            }
            else
            {
                SendMantleStart(
                    playerClientId,
                    clipId,
                    sessionSeconds,
                    ledgeGripCenter,
                    wallNormal,
                    includeExtension: true,
                    clientId: NetworkManager.ServerClientId);
            }
        }

        private static void SendMantleStart(
            ulong playerClientId,
            byte clipId,
            float sessionSeconds,
            Vector3 ledgeGripCenter,
            Vector3 wallNormal,
            bool includeExtension,
            ulong? clientId)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            int payloadBytes = sizeof(ulong) + sizeof(byte)
                + (includeExtension ? MANTLE_START_EXTENSION_BYTES : 0);
            FastBufferWriter writer = new FastBufferWriter(payloadBytes, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(playerClientId);
                writer.WriteValueSafe(clipId);
                if (includeExtension)
                {
                    writer.WriteValueSafe(sessionSeconds);
                    writer.WriteValueSafe(ledgeGripCenter.x);
                    writer.WriteValueSafe(ledgeGripCenter.y);
                    writer.WriteValueSafe(ledgeGripCenter.z);
                    writer.WriteValueSafe(wallNormal.x);
                    writer.WriteValueSafe(wallNormal.y);
                    writer.WriteValueSafe(wallNormal.z);
                }
                if (clientId.HasValue)
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                        MSG_MANTLE_START,
                        clientId.Value,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                else
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                        MSG_MANTLE_START,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void SendMantleStop(ulong playerClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient || network.CustomMessagingManager == null)
                return;

            if (network.IsServer)
                SendMantleStop(playerClientId, null);
            else
                SendMantleStop(playerClientId, NetworkManager.ServerClientId);
        }

        private static void SendMantleStop(ulong playerClientId, ulong? clientId)
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
                        MSG_MANTLE_STOP,
                        clientId.Value,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                else
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                        MSG_MANTLE_STOP,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveMantleStart(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                ulong playerClientId;
                byte clipId;
                reader.ReadValueSafe(out playerClientId);
                reader.ReadValueSafe(out clipId);
                // F-ESCAPE-4: only the connection that owns the named player (or the host relay)
                // may drive that player's mantle presentation on this machine.
                if (!PanicSlidePatch.IsAcceptedMovementSender(senderClientId, playerClientId, MSG_MANTLE_START))
                    return;

                float sessionSeconds = 0f;
                Vector3 ledgeGripCenter = Vector3.zero;
                Vector3 wallNormal = Vector3.zero;
                bool hasExtension = reader.Length - reader.Position >= MANTLE_START_EXTENSION_BYTES;
                if (hasExtension)
                {
                    reader.ReadValueSafe(out sessionSeconds);
                    reader.ReadValueSafe(out ledgeGripCenter.x);
                    reader.ReadValueSafe(out ledgeGripCenter.y);
                    reader.ReadValueSafe(out ledgeGripCenter.z);
                    reader.ReadValueSafe(out wallNormal.x);
                    reader.ReadValueSafe(out wallNormal.y);
                    reader.ReadValueSafe(out wallNormal.z);
                }

                ApplyRemoteMantleStart(
                    playerClientId,
                    clipId,
                    sessionSeconds,
                    ledgeGripCenter,
                    wallNormal,
                    hasExtension);
                RelayMantleStartIfHost(
                    senderClientId,
                    playerClientId,
                    clipId,
                    sessionSeconds,
                    ledgeGripCenter,
                    wallNormal,
                    hasExtension);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"LedgeMantlePatch: malformed mantle start message: {ex.Message}");
            }
        }

        private static void OnReceiveMantleStop(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                ulong playerClientId;
                reader.ReadValueSafe(out playerClientId);
                if (!PanicSlidePatch.IsAcceptedMovementSender(senderClientId, playerClientId, MSG_MANTLE_STOP))
                    return;
                ApplyRemoteMantleStop(playerClientId);
                RelayMantleStopIfHost(senderClientId, playerClientId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"LedgeMantlePatch: malformed mantle stop message: {ex.Message}");
            }
        }

        private static void RelayMantleStartIfHost(
            ulong senderClientId,
            ulong playerClientId,
            byte clipId,
            float sessionSeconds,
            Vector3 ledgeGripCenter,
            Vector3 wallNormal,
            bool hasExtension)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network != null
                && network.IsServer
                && senderClientId != network.LocalClientId)
            {
                SendMantleStart(
                    playerClientId,
                    clipId,
                    sessionSeconds,
                    ledgeGripCenter,
                    wallNormal,
                    hasExtension,
                    null);
            }
        }

        private static void RelayMantleStopIfHost(ulong senderClientId, ulong playerClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network != null
                && network.IsServer
                && senderClientId != network.LocalClientId)
            {
                SendMantleStop(playerClientId, null);
            }
        }

        private static void ApplyRemoteMantleStart(
            ulong playerClientId,
            byte clipId,
            float sessionSeconds,
            Vector3 ledgeGripCenter,
            Vector3 wallNormal,
            bool hasExtension)
        {
            bool tall = clipId != 0;
            bool durationValid = hasExtension
                && !float.IsNaN(sessionSeconds)
                && !float.IsInfinity(sessionSeconds)
                && sessionSeconds > 0.001f;
            float resolvedSessionSeconds =
                SlideAnimationBridge.ResolveMantleSessionSeconds(
                    tall,
                    durationValid ? sessionSeconds : 0f);
            bool bodyGrabConfigured = Plugin.RemoteMantleBodyGrabIk?.Value ?? true;
            PlayerControllerB player = ResolvePlayer(playerClientId);
            if (player == null || IsLocalPlayer(player))
            {
                Plugin.Log?.LogDebug(
                    "[Panic Slide.remote-mantle] start_gate: " +
                    $"playerClientId={playerClientId} tall={tall} bridgeStarted=False " +
                    $"payload='{(hasExtension ? "extended" : "legacy")}' " +
                    $"durationSource='{(durationValid ? "received" : "fallback")}' " +
                    $"duration={resolvedSessionSeconds:0.######} " +
                    $"bodyGrabConfigured={bodyGrabConfigured} bodyGrabEnabled=False " +
                    $"reason='{(player == null ? "player_missing" : "local_player")}'.");
                return;
            }
            bool bridgeStarted =
                SlideAnimationBridge.BeginMantle(player, tall, resolvedSessionSeconds);
            // F-ESCAPE-4: the grip and normal are fed straight into SolveArmBendPreservingBlended,
            // so they are range-checked before they can be stored. A NaN/infinite vector poisons
            // the arm rotations permanently, and a grip far from the named player is not a ledge
            // that player could be holding.
            bool wallNormalValid = IsFiniteVector(wallNormal)
                && wallNormal.sqrMagnitude > 0.0001f
                && wallNormal.sqrMagnitude < REMOTE_MANTLE_MAX_NORMAL_SQR_MAGNITUDE;
            bool gripPlausible = IsFiniteVector(ledgeGripCenter)
                && player.transform != null
                && (ledgeGripCenter - player.transform.position).sqrMagnitude
                    <= REMOTE_MANTLE_MAX_GRIP_DISTANCE * REMOTE_MANTLE_MAX_GRIP_DISTANCE;
            bool bodyGrabEnabled = bridgeStarted
                && bodyGrabConfigured
                && hasExtension
                && wallNormalValid
                && gripPlausible;
            string bodyGrabReason = !bridgeStarted
                ? "bridge_start_failed"
                : !bodyGrabConfigured
                    ? "config_disabled"
                    : !hasExtension
                        ? "legacy_payload"
                        : !wallNormalValid
                            ? "wall_normal_invalid"
                            : !gripPlausible
                                ? "grip_implausible"
                                : "ready";

            RemoteMantles.Remove(playerClientId);
            if (bodyGrabEnabled)
            {
                RemoteMantles[playerClientId] = new RemoteMantlePresentation(
                    player,
                    resolvedSessionSeconds,
                    ledgeGripCenter,
                    wallNormal.normalized);
            }

            Plugin.Log?.LogDebug(
                "[Panic Slide.remote-mantle] start_gate: " +
                $"playerClientId={playerClientId} tall={tall} bridgeStarted={bridgeStarted} " +
                $"payload='{(hasExtension ? "extended" : "legacy")}' " +
                $"durationSource='{(durationValid ? "received" : "fallback")}' " +
                $"duration={resolvedSessionSeconds:0.######} " +
                $"bodyGrabConfigured={bodyGrabConfigured} bodyGrabEnabled={bodyGrabEnabled} " +
                $"bodyGrabReason='{bodyGrabReason}'.");
        }

        private static void ApplyRemoteMantleStop(ulong playerClientId)
        {
            bool removedPresentation = RemoteMantles.Remove(playerClientId);
            if (removedPresentation)
            {
                Plugin.Log?.LogDebug(
                    "[Panic Slide.remote-mantle] body_grab_end_gate: " +
                    $"playerClientId={playerClientId} reason='stop_received'.");
            }
            PlayerControllerB player = ResolvePlayer(playerClientId);
            bool bridgeEnded = player != null && !IsLocalPlayer(player);
            if (bridgeEnded)
                SlideAnimationBridge.End(player, "remote-mantle-stop");

            Plugin.Log?.LogDebug(
                "[Panic Slide.remote-mantle] stop_gate: " +
                $"playerClientId={playerClientId} bridgeEnded={bridgeEnded} " +
                $"bodyGrabPresentationRemoved={removedPresentation}.");
        }

        private static void ClearRemoteMantlePresentations(string reason)
        {
            int cleared = RemoteMantles.Count;
            RemoteMantles.Clear();
            Plugin.Log?.LogDebug(
                "[Panic Slide.remote-mantle] body_grab_clear_gate: " +
                $"cleared={cleared} reason='{reason}'.");
        }

        // F-ESCAPE-4: NaN/infinity guard for payload-supplied vectors.
        private static bool IsFiniteVector(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
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

        private sealed class RemoteMantlePresentation
        {
            private const float GrabEaseFraction = 0.18f;

            private bool _bodyArmBonesResolved;

            internal PlayerControllerB Player { get; }
            internal float StartedAt { get; }
            internal float Duration { get; }
            internal Vector3 LedgeGripCenter { get; }
            internal Vector3 WallNormal { get; }
            internal Transform BodyArmUpperL { get; private set; }
            internal Transform BodyArmLowerL { get; private set; }
            internal Transform BodyHandL { get; private set; }
            internal Transform BodyArmUpperR { get; private set; }
            internal Transform BodyArmLowerR { get; private set; }
            internal Transform BodyHandR { get; private set; }
            internal bool HasBodyArmBones =>
                BodyArmUpperL != null
                && BodyArmLowerL != null
                && BodyHandL != null
                && BodyArmUpperR != null
                && BodyArmLowerR != null
                && BodyHandR != null;

            private string _lastBodyGrabGate;

            internal RemoteMantlePresentation(
                PlayerControllerB player,
                float duration,
                Vector3 ledgeGripCenter,
                Vector3 wallNormal)
            {
                Player = player;
                StartedAt = Time.time;
                Duration = Mathf.Max(0.001f, duration);
                LedgeGripCenter = ledgeGripCenter;
                WallNormal = wallNormal;
            }

            internal float CalculateGrabEnvelope()
            {
                float elapsed = Mathf.Clamp(Time.time - StartedAt, 0f, Duration);
                float remaining = Mathf.Max(0f, Duration - elapsed);
                float easeSeconds = Mathf.Max(0.001f, Duration * GrabEaseFraction);
                float easeIn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / easeSeconds));
                float easeOut = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(remaining / easeSeconds));
                return Mathf.Min(easeIn, easeOut);
            }

            internal void LogBodyGrabGate(string result, float weight)
            {
                if (string.Equals(_lastBodyGrabGate, result, StringComparison.Ordinal))
                    return;

                _lastBodyGrabGate = result;
                Plugin.Log?.LogDebug(
                    "[Panic Slide.remote-mantle] body_grab_gate: " +
                    $"playerClientId={Player?.playerClientId.ToString() ?? "<null>"} " +
                    $"result='{result}' weight={weight:0.######}.");
            }

            internal void ResolveBodyArmBones()
            {
                if (_bodyArmBonesResolved)
                    return;

                _bodyArmBonesResolved = true;
                if (Player == null)
                    return;

                Transform bodyMetarig = null;
                foreach (Transform transform in Player.GetComponentsInChildren<Transform>(true))
                {
                    if (string.Equals(transform.name, "metarig", StringComparison.Ordinal)
                        && transform.parent != null
                        && string.Equals(transform.parent.name, "ScavengerModel", StringComparison.Ordinal))
                    {
                        bodyMetarig = transform;
                        break;
                    }
                }

                if (bodyMetarig == null)
                    return;

                BodyArmUpperL = FindDeep(bodyMetarig, "arm.L_upper");
                BodyArmLowerL = FindDeep(bodyMetarig, "arm.L_lower");
                BodyHandL = FindDeep(bodyMetarig, "hand.L");
                BodyArmUpperR = FindDeep(bodyMetarig, "arm.R_upper");
                BodyArmLowerR = FindDeep(bodyMetarig, "arm.R_lower");
                BodyHandR = FindDeep(bodyMetarig, "hand.R");
            }
        }

        private readonly struct MantleProbe
        {
            internal readonly Vector3 StartPosition;
            internal readonly Vector3 LiftPosition;
            internal readonly Vector3 EndPosition;
            internal readonly Vector3 WallNormal;
            internal readonly float LedgeHeight;
            internal readonly bool Tall;
            // World point on the ledge front lip where the hands should grab (wall face at ledge-top height).
            internal readonly Vector3 LedgeGripCenter;

            internal MantleProbe(
                Vector3 startPosition,
                Vector3 liftPosition,
                Vector3 endPosition,
                Vector3 wallNormal,
                float ledgeHeight,
                bool tall,
                Vector3 ledgeGripCenter)
            {
                StartPosition = startPosition;
                LiftPosition = liftPosition;
                EndPosition = endPosition;
                WallNormal = wallNormal;
                LedgeHeight = ledgeHeight;
                Tall = tall;
                LedgeGripCenter = ledgeGripCenter;
            }
        }
    }
}

#pragma warning restore Harmony003
