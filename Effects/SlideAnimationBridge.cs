using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZUpgrades.Interactive;

namespace Y4NGZUpgrades.Effects
{
    internal static class SlideAnimationBridge
    {
        private const string FullBodyLayerName = MovementPlayerAnimationRuntimeAssets.FullBodyLayerName;
        private const string FpArmsLayerName = MovementPlayerAnimationRuntimeAssets.FirstPersonArmsLayerName;
        private const string LocalBodyLayerName = MovementPlayerAnimationRuntimeAssets.LocalBodyLayerName;
        // The FP arms layer sits AFTER the full-body layer, and Unity resets a trigger as soon
        // as the first layer's transition consumes it — the arms layer never sees the shared
        // triggers. Its states are therefore entered directly via CrossFadeInFixedTime.
        private const float FpArmsCrossFadeSeconds = 0.15f;
        private const float ExitHoldSeconds = 0.55f;
        private const float Mantle1mSessionSeconds = 0.72f;
        private const float Mantle2mSessionSeconds = 1.36f;
        private const float MaxSessionSeconds = 6f;
        private const float AnimatorStateCrossfadeSeconds = 0.12f;
        private const string StowReason = "movement-animation";
        // Backstop only; every session release path runs through EndImmediate.
        private const float StowTimeoutSeconds = 8f;

        private static readonly Dictionary<PlayerControllerB, SlideAnimSession> Sessions =
            new Dictionary<PlayerControllerB, SlideAnimSession>();
        private static readonly List<PlayerControllerB> ScratchPlayers = new List<PlayerControllerB>();
        private static bool FirstPersonViewProbeResolved;
        private static FieldInfo FirstPersonViewLocalBodyShownField;
        private static bool FirstPersonViewProbeFailureLogged;

        private enum MovementAnimKind
        {
            Slide,
            Mantle1m,
            Mantle2m,
        }

        private enum AnimatorStateRestoreMode
        {
            Fresh,
            Crossfade,
            Replay,
        }

        private enum CameraRestoreBaselineMode
        {
            Begin,
            Live,
        }

        internal static bool Begin(PlayerControllerB player)
        {
            return BeginSession(player, MovementAnimKind.Slide);
        }

        internal static bool BeginMantle(PlayerControllerB player, bool tall)
        {
            return BeginMantle(player, tall, 0f);
        }

        internal static bool BeginMantle(PlayerControllerB player, bool tall, float sessionSeconds)
        {
            return BeginSession(
                player,
                tall ? MovementAnimKind.Mantle2m : MovementAnimKind.Mantle1m,
                sessionSeconds);
        }

        internal static bool IsActive(PlayerControllerB player)
        {
            return player != null
                && Sessions.TryGetValue(player, out SlideAnimSession session)
                && session.IsActive;
        }

        internal static float ResolveMantleSessionSeconds(bool tall, float receivedSeconds)
        {
            if (!float.IsNaN(receivedSeconds)
                && !float.IsInfinity(receivedSeconds)
                && receivedSeconds > 0.001f)
            {
                return Mathf.Min(receivedSeconds, MaxSessionSeconds);
            }

            return tall ? Mantle2mSessionSeconds : Mantle1mSessionSeconds;
        }

        private static bool ShouldUseGroundedLocalSlideBody(
            PlayerControllerB player,
            out string reason)
        {
            if (player == null || player != GameNetworkManager.Instance?.localPlayerController)
            {
                reason = "not_local";
                return false;
            }

            if (Y4NGZUpgrades.Patches.LedgeMantlePatch.IsDebugThirdPersonCamActive())
            {
                reason = "debug_third_person";
                return true;
            }

            // First-Person View no longer forces the grounded full-body mode: our LateUpdate
            // postfixes now run AFTER FPV's (priority -100 vs its 0), so the camera-composed
            // presentation — dip, pinned local-body clip, forced vanilla arms — is the frame's
            // final state and renders identically with or without FPV. The grounded mode under
            // FPV left the camera at standing height with the body on the floor (legs invisible
            // unless looking straight down), which is exactly the regression it caused.
            reason = "camera_composed_first_person";
            return false;
        }

        // Also consumed by LedgeMantlePatch/PanicSlidePatch: when First-Person View is actively
        // showing the local body, its priority-0 LateUpdate postfix (which runs AFTER ours)
        // re-poses the gameplay camera from the body's head bone and re-decides vanilla-arms
        // visibility. Our camera-composed FP viewmodel presentation must yield to it (#347).
        internal static bool IsFirstPersonViewLocalBodyShown()
        {
            if (!FirstPersonViewProbeResolved)
            {
                FirstPersonViewProbeResolved = true;
                Type controllerType = Type.GetType(
                    "FirstPersonView.LocalBodyViewController, FirstPersonView",
                    throwOnError: false);
                if (controllerType == null)
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < assemblies.Length; i++)
                    {
                        try
                        {
                            controllerType = assemblies[i].GetType(
                                "FirstPersonView.LocalBodyViewController",
                                throwOnError: false);
                        }
                        catch
                        {
                            controllerType = null;
                        }

                        if (controllerType != null)
                            break;
                    }
                }

                FirstPersonViewLocalBodyShownField = controllerType?.GetField(
                    "LocalBodyShown",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (controllerType != null && FirstPersonViewLocalBodyShownField == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.presentation] First-Person View is loaded but its local-body visibility signal could not be resolved; grounded compatibility mode will use only explicit third-person camera signals.");
                }
            }

            if (FirstPersonViewLocalBodyShownField == null)
                return false;

            try
            {
                return FirstPersonViewLocalBodyShownField.GetValue(null) is bool shown && shown;
            }
            catch (Exception ex)
            {
                if (!FirstPersonViewProbeFailureLogged)
                {
                    FirstPersonViewProbeFailureLogged = true;
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.presentation] First-Person View local-body visibility probe failed; " +
                        $"grounded compatibility mode will use only explicit third-person camera signals. error='{ex.Message}'.");
                }
                return false;
            }
        }

        private static bool BeginSession(
            PlayerControllerB player,
            MovementAnimKind kind,
            float sessionSeconds = 0f)
        {
            if (player == null)
                return false;

            RuntimeAnimatorController controller = MovementPlayerAnimationRuntimeAssets.ResolveController();
            if (controller == null)
                return false;

            // Taken before an existing session is replaced: the refcount keeps the item stowed
            // across the handover instead of restoring and re-stowing it in the same frame.
            HeldItemStowHandle stow = HeldItemStowService.Begin(
                player,
                StowReason,
                StowTimeoutSeconds,
                force: true);

            if (Sessions.TryGetValue(player, out SlideAnimSession existing))
            {
                if (existing.IsActive && !existing.IsExiting)
                {
                    stow.Dispose();
                    return true;
                }

                existing.EndImmediate("replaced");
                Sessions.Remove(player);
            }

            var session = new SlideAnimSession(player);
            if (!session.Begin(controller, kind, sessionSeconds))
            {
                stow.Dispose();
                return false;
            }

            session.StowHandle = stow;
            Sessions[player] = session;
            return true;
        }

        internal static void TriggerExit(PlayerControllerB player)
        {
            if (player == null)
                return;

            if (Sessions.TryGetValue(player, out SlideAnimSession session))
                session.TriggerExit("requested");
        }

        internal static void End(PlayerControllerB player, string reason)
        {
            if (player == null)
                return;

            if (!Sessions.TryGetValue(player, out SlideAnimSession session))
                return;

            session.EndImmediate(string.IsNullOrWhiteSpace(reason) ? "ended" : reason);
            Sessions.Remove(player);
        }

        internal static void Tick()
        {
            if (Sessions.Count == 0)
                return;

            ScratchPlayers.Clear();
            foreach (KeyValuePair<PlayerControllerB, SlideAnimSession> pair in Sessions)
                ScratchPlayers.Add(pair.Key);

            for (int i = 0; i < ScratchPlayers.Count; i++)
            {
                PlayerControllerB player = ScratchPlayers[i];
                if (!Sessions.TryGetValue(player, out SlideAnimSession session))
                    continue;

                session.Tick();
                if (!session.IsActive)
                    Sessions.Remove(player);
            }

            ScratchPlayers.Clear();
        }

        internal static void EndAll(string reason)
        {
            if (Sessions.Count == 0)
                return;

            ScratchPlayers.Clear();
            foreach (KeyValuePair<PlayerControllerB, SlideAnimSession> pair in Sessions)
                ScratchPlayers.Add(pair.Key);

            for (int i = 0; i < ScratchPlayers.Count; i++)
            {
                PlayerControllerB player = ScratchPlayers[i];
                if (Sessions.TryGetValue(player, out SlideAnimSession session))
                    session.EndImmediate(string.IsNullOrWhiteSpace(reason) ? "ended" : reason);
            }

            Sessions.Clear();
            ScratchPlayers.Clear();
        }

        private sealed class SlideAnimSession
        {
            private static readonly int ActiveHash = Animator.StringToHash(MovementPlayerAnimationRuntimeAssets.ActiveBool);
            private static readonly int EnterHash = Animator.StringToHash(MovementPlayerAnimationRuntimeAssets.EnterTrigger);
            private static readonly int ExitHash = Animator.StringToHash(MovementPlayerAnimationRuntimeAssets.ExitTrigger);
            private static readonly int Mantle1mHash = Animator.StringToHash(MovementPlayerAnimationRuntimeAssets.Mantle1mTrigger);
            private static readonly int Mantle2mHash = Animator.StringToHash(MovementPlayerAnimationRuntimeAssets.Mantle2mTrigger);
            private static readonly int WalkingHash = Animator.StringToHash("Walking");
            private static readonly int SprintingHash = Animator.StringToHash("Sprinting");
            private static readonly int SidewaysHash = Animator.StringToHash("Sideways");
            private static readonly int CrouchingHash = Animator.StringToHash("crouching");
            private static readonly int JumpingHash = Animator.StringToHash("Jumping");
            private static readonly int FallNoJumpHash = Animator.StringToHash("FallNoJump");
            private static readonly int AnimationSpeedHash = Animator.StringToHash("animationSpeed");

            private readonly Dictionary<int, AnimatorControllerParameterType> _parameterTypes =
                new Dictionary<int, AnimatorControllerParameterType>();

            private Animator _animator;
            private RuntimeAnimatorController _controller;
            private RuntimeAnimatorController _savedController;
            private CameraPoseSnapshot _beginCameraPose;
            private CameraRestoreBaselineMode _cameraRestoreBaselineMode;
            private float[] _savedLayerWeights = Array.Empty<float>();
            private int[] _savedStateHashes = Array.Empty<int>();
            private float[] _savedStateTimes = Array.Empty<float>();
            private readonly List<AnimatorParameterSnapshot> _savedParameters =
                new List<AnimatorParameterSnapshot>();
            private bool _controllerApplied;
            private bool _rigBuilderRebuildLogged;
            private int _fullBodyLayer = -1;
            private int _fpArmsLayer = -1;
            private int _localBodyLayer = -1;
            private MovementAnimKind _kind;
            private bool _useGroundedLocalSlideBody;
            private bool _localSlideBodyModeInitialized;
            private string _localSlideBodyModeReason = string.Empty;
            private bool _fpArmsCrossFadeWarned;
            private bool _localBodyCrossFadeWarned;
            private float _startedAt;
            private float _exitTriggeredAt;
            private int _enterHash;
            private float _autoEndSeconds;
            private bool _lookRig1WeightCaptured;
            private bool _lookRig2WeightCaptured;
            private float _savedLookRig1Weight;
            private float _savedLookRig2Weight;
            private bool _lookRigSuppressionLogged;
            private static bool _rigBuilderReflectionResolved;
            private static Type _rigBuilderType;
            private static MethodInfo _rigBuilderBuildMethod;
            private static MethodInfo _rigBuilderEvaluateMethod;
            private static bool _visorHierarchyLogged;

            internal PlayerControllerB Player { get; private set; }
            internal bool IsActive { get; private set; }
            internal bool IsExiting { get; private set; }

            // The held item comes back when the session ends, not when the exit blend starts —
            // the exit still needs the body free.
            internal HeldItemStowHandle StowHandle { get; set; }

            internal SlideAnimSession(PlayerControllerB player)
            {
                Player = player;
            }

            internal bool Begin(
                RuntimeAnimatorController controller,
                MovementAnimKind kind,
                float sessionSeconds)
            {
                if (Player == null || controller == null)
                    return false;

                _animator = Player.playerBodyAnimator;
                if (_animator == null)
                {
                    Plugin.Log?.LogWarning("[Panic Slide] Slide anim session skipped; playerBodyAnimator is missing.");
                    return false;
                }

                _controller = controller;
                _savedController = _animator.runtimeAnimatorController;
                SaveVanillaAnimatorState(_animator);
                CaptureLookRigWeights();
                _cameraRestoreBaselineMode = ResolveCameraRestoreBaselineMode(
                    out string configuredCameraBaseline,
                    out bool usedCameraBaselineFallback);
                Plugin.Log?.LogDebug(
                    "[Panic Slide.camerabaseline] mode_selected: " +
                    $"phase='session_begin' mode='{FormatCameraRestoreBaselineMode(_cameraRestoreBaselineMode)}' " +
                    $"configured='{configuredCameraBaseline}' fallback={usedCameraBaselineFallback} " +
                    $"frame={Time.frameCount} player={FormatPlayerForLog(Player)}.");
                _beginCameraPose = CaptureBeginCameraPose("session_begin");

                try
                {
                    _animator.runtimeAnimatorController = controller;
                    _controllerApplied = true;
                    RebuildAnimationRigging(_animator, "controller apply");
                }
                catch (Exception ex)
                {
                    _controllerApplied = false;
                    Plugin.Log?.LogWarning(
                        $"[Panic Slide] Slide animator controller apply failed for player {FormatPlayerForLog(Player)}: {ex.Message}");
                    return false;
                }

                CacheParameters(_animator);
                MovementPlayerAnimationRuntimeAssets.LogAnimatorControllerDiagnosticsOnce(_animator, controller);
                if (!MovementPlayerAnimationRuntimeAssets.ValidateAnimatorControllerContract(_animator, controller, "manual-session"))
                {
                    try
                    {
                        RestoreVanillaAnimatorController(_animator);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning(
                            $"[Panic Slide] Slide rejected-controller restore failed for player {FormatPlayerForLog(Player)}: {ex.Message}");
                    }
                    finally
                    {
                        StartPostRestoreSampler(_animator);
                        RestoreLookRigWeights();
                    }

                    _controllerApplied = false;
                    return false;
                }

                CacheLayerIndices();
                _kind = kind;
                _useGroundedLocalSlideBody = false;
                _localSlideBodyModeInitialized = false;
                _localSlideBodyModeReason = string.Empty;
                _enterHash = ResolveEnterHash(kind);
                _autoEndSeconds = kind == MovementAnimKind.Slide
                    ? 0f
                    : ResolveMantleSessionSeconds(kind == MovementAnimKind.Mantle2m, sessionSeconds);
                IsActive = true;
                IsExiting = false;
                _startedAt = Time.time;
                _exitTriggeredAt = 0f;
                ApplyLookRigSuppression();
                SetBool(ActiveHash, true);
                FireTrigger(_enterHash);
                CrossFadeFpArmsState(ResolveFpArmsEnterState(kind));
                CrossFadeLocalBodySlideState("SlideStart");
                ApplyAnimatorParameters();
                return true;
            }

            internal void Tick()
            {
                if (!IsActive || _animator == null)
                    return;

                if (IsPlayerInvalid())
                {
                    EndImmediate("player-invalid");
                    return;
                }

                if (Time.time - _startedAt > MaxSessionSeconds)
                {
                    EndImmediate("session-timeout");
                    return;
                }

                EnsureControllerStillApplied();
                ApplyLookRigSuppression();
                ApplyAnimatorParameters();

                if (IsExiting && Time.time - _exitTriggeredAt >= ExitHoldSeconds)
                {
                    EndImmediate("exit-finished");
                    return;
                }

                if (!IsExiting && _autoEndSeconds > 0f && Time.time - _startedAt >= _autoEndSeconds)
                    EndImmediate("auto-finished");
            }

            internal void TriggerExit(string reason)
            {
                if (!IsActive || IsExiting)
                    return;

                IsExiting = true;
                _exitTriggeredAt = Time.time;
                SetBool(ActiveHash, false);
                FireTrigger(ExitHash);
                if (_kind == MovementAnimKind.Slide)
                {
                    CrossFadeFpArmsState("SlideExit");
                    CrossFadeLocalBodySlideState("SlideExit");
                }
            }

            internal void EndImmediate(string reason)
            {
                StowHandle.Dispose();
                StowHandle = default;
                if (!IsActive && _animator == null)
                    return;

                Animator animator = _animator;
                ParkourCameraPositionReleaseGuard cameraGuard =
                    BeginCameraRestoreGuard(Player, reason);

                if (animator != null && _controllerApplied)
                {
                    try
                    {
                        RestoreVanillaAnimatorController(animator);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"[Panic Slide] Slide player anim session restore failed: {ex.Message}");
                    }
                    finally
                    {
                        StartPostRestoreSampler(animator);
                    }
                }

                if (cameraGuard != null)
                    cameraGuard.ReleaseAfterLateUpdates(2);

                RestoreLookRigWeights();
                Player = null;
                _animator = null;
                _controller = null;
                _savedController = null;
                _beginCameraPose = default;
                _cameraRestoreBaselineMode = CameraRestoreBaselineMode.Begin;
                _savedLayerWeights = Array.Empty<float>();
                _savedStateHashes = Array.Empty<int>();
                _savedStateTimes = Array.Empty<float>();
                _savedParameters.Clear();
                _parameterTypes.Clear();
                _controllerApplied = false;
                _fullBodyLayer = -1;
                _fpArmsLayer = -1;
                _localBodyLayer = -1;
                _kind = MovementAnimKind.Slide;
                _useGroundedLocalSlideBody = false;
                _localSlideBodyModeInitialized = false;
                _localSlideBodyModeReason = string.Empty;
                _fpArmsCrossFadeWarned = false;
                _localBodyCrossFadeWarned = false;
                _startedAt = 0f;
                _exitTriggeredAt = 0f;
                _enterHash = 0;
                _autoEndSeconds = 0f;
                _lookRig1WeightCaptured = false;
                _lookRig2WeightCaptured = false;
                _savedLookRig1Weight = 0f;
                _savedLookRig2Weight = 0f;
                _lookRigSuppressionLogged = false;
                IsExiting = false;
                IsActive = false;
            }

            private void CaptureLookRigWeights()
            {
                _lookRig1WeightCaptured = Player != null && Player.cameraLookRig1 != null;
                _lookRig2WeightCaptured = Player != null && Player.cameraLookRig2 != null;
                _savedLookRig1Weight = _lookRig1WeightCaptured
                    ? Player.cameraLookRig1.weight
                    : 0f;
                _savedLookRig2Weight = _lookRig2WeightCaptured
                    ? Player.cameraLookRig2.weight
                    : 0f;
            }

            private void ApplyLookRigSuppression()
            {
                if (!IsActive || Player == null)
                    return;

                if (_lookRig1WeightCaptured && Player.cameraLookRig1 != null)
                    Player.cameraLookRig1.weight = 0f;
                if (_lookRig2WeightCaptured && Player.cameraLookRig2 != null)
                    Player.cameraLookRig2.weight = 0f;

                if (_lookRigSuppressionLogged)
                    return;

                _lookRigSuppressionLogged = true;
                Plugin.Log?.LogDebug(
                    "[Panic Slide.lookrig] suppression_started: " +
                    $"player={FormatPlayerForLog(Player)} " +
                    $"cameraLookRig1Captured={_lookRig1WeightCaptured} " +
                    $"cameraLookRig1PriorWeight={_savedLookRig1Weight:0.######} " +
                    $"cameraLookRig2Captured={_lookRig2WeightCaptured} " +
                    $"cameraLookRig2PriorWeight={_savedLookRig2Weight:0.######}.");
            }

            private void RestoreLookRigWeights()
            {
                if (Player == null)
                    return;

                if (_lookRig1WeightCaptured && Player.cameraLookRig1 != null)
                    Player.cameraLookRig1.weight = _savedLookRig1Weight;
                if (_lookRig2WeightCaptured && Player.cameraLookRig2 != null)
                    Player.cameraLookRig2.weight = _savedLookRig2Weight;
            }

            private bool IsPlayerInvalid()
            {
                if (Player == null || Player.isPlayerDead || !Player.isPlayerControlled)
                    return true;
                if (Player.gameObject == null || !Player.gameObject.activeInHierarchy)
                    return true;
                return !Player.enabled;
            }

            private ParkourCameraPositionReleaseGuard BeginCameraRestoreGuard(
                PlayerControllerB player,
                string reason)
            {
                bool settledEnd = string.Equals(reason, "exit-finished", StringComparison.Ordinal) ||
                    string.Equals(reason, "mantle-finished", StringComparison.Ordinal) ||
                    string.Equals(reason, "auto-finished", StringComparison.Ordinal);
                if (!settledEnd)
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.camerabaseline] guard_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                        $"endReason='{reason}' reason='unsettled_end'.");
                    return null;
                }

                if (player == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] guard_skipped: " +
                        $"frame={Time.frameCount} player=<null> endReason='{reason}' " +
                        "reason='missing_player'.");
                    return null;
                }

                PlayerControllerB localPlayer;
                try
                {
                    localPlayer = GameNetworkManager.Instance != null
                        ? GameNetworkManager.Instance.localPlayerController
                        : null;
                    if (localPlayer == null && StartOfRound.Instance != null)
                        localPlayer = StartOfRound.Instance.localPlayerController;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] guard_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                        $"endReason='{reason}' reason='local_player_resolution_failed' " +
                        $"error='{ex.Message}'.");
                    return null;
                }

                if (!ReferenceEquals(player, localPlayer))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.camerabaseline] guard_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                        $"endReason='{reason}' reason='not_local_player'.");
                    return null;
                }

                if (player.transform == null || player.gameplayCamera == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] guard_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                        $"endReason='{reason}' reason='missing_camera_target'.");
                    return null;
                }

                Vector3 playerLocalPosition;
                string targetSource;
                if (_cameraRestoreBaselineMode == CameraRestoreBaselineMode.Begin &&
                    _beginCameraPose.GameplayCameraPlayerLocalPositionCaptured)
                {
                    playerLocalPosition = _beginCameraPose.GameplayCameraPlayerLocalPosition;
                    targetSource = "begin";
                }
                else
                {
                    if (_cameraRestoreBaselineMode == CameraRestoreBaselineMode.Begin)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerabaseline] guard_target_fallback: " +
                            $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                            "requested='begin' resolved='live' " +
                            "reason='begin_player_local_position_unavailable'.");
                    }

                    try
                    {
                        playerLocalPosition = player.transform.InverseTransformPoint(
                            player.gameplayCamera.transform.position);
                        targetSource = "live";
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerabaseline] guard_skipped: " +
                            $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                            $"endReason='{reason}' reason='position_capture_failed' " +
                            $"error='{ex.Message}'.");
                        return null;
                    }
                }

                try
                {
                    var guard = player.gameplayCamera.gameObject.AddComponent<ParkourCameraPositionReleaseGuard>();
                    guard.Initialize(player.transform, player.gameplayCamera.transform, playerLocalPosition);
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.camerabaseline] guard_started: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                        $"mode='{FormatCameraRestoreBaselineMode(_cameraRestoreBaselineMode)}' " +
                        $"targetSource='{targetSource}' playerLocalPosition={DescribeVector(playerLocalPosition)}.");
                    return guard;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] guard_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                        $"endReason='{reason}' reason='guard_start_failed' error='{ex.Message}'.");
                    return null;
                }
            }

            private void EnsureControllerStillApplied()
            {
                if (_animator == null || !_controllerApplied || _controller == null)
                    return;
                if (_animator.runtimeAnimatorController == _controller)
                    return;

                try
                {
                    _animator.runtimeAnimatorController = _controller;
                    RebuildAnimationRigging(_animator, "controller reapply");
                    CacheParameters(_animator);
                    CacheLayerIndices();
                    SetBool(ActiveHash, !IsExiting);
                    if (IsExiting)
                    {
                        FireTrigger(ExitHash);
                        if (_kind == MovementAnimKind.Slide)
                        {
                            CrossFadeFpArmsState("SlideExit");
                            CrossFadeLocalBodySlideState("SlideExit");
                        }
                    }
                    else
                    {
                        FireTrigger(_enterHash);
                        CrossFadeFpArmsState(ResolveFpArmsEnterState(_kind));
                        CrossFadeLocalBodySlideState("SlideStart");
                    }
                    Plugin.Log?.LogDebug("[Panic Slide] Slide animator controller was restored after another system replaced it during slide playback.");
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[Panic Slide] Slide animator controller reapply failed: {ex.Message}");
                }
            }

            private void ApplyAnimatorParameters()
            {
                if (_animator == null || !_controllerApplied)
                    return;

                SetBool(ActiveHash, !IsExiting);
                // Local SLIDE normally uses the camera-composed body layer so its head remains
                // behind the fixed vanilla camera. That clip intentionally pins the upper body
                // about 0.79 m above the grounded authored pose, so it must not be used when an
                // explicit third-person camera or First-Person View exposes the actual body.
                // Local MANTLE: the full-body clip DOES play (weight 1) so the third-person /
                // debug cam sees the mantle animate — the body is instead kept out of the FP
                // view by LAYER (LedgeMantlePatch puts the body LODs on the FP-excluded layer),
                // which third-person cameras still render. Remote sessions always play it.
                bool localSession = IsLocalPlayerSession();
                bool localSlideSession = localSession && _kind == MovementAnimKind.Slide;
                if (localSlideSession)
                    RefreshLocalSlideBodyMode();
                bool groundedLocalSlide = localSlideSession && _useGroundedLocalSlideBody;
                SetLayerWeight(_fullBodyLayer, !localSlideSession || groundedLocalSlide ? 1f : 0f);
                SetLayerWeight(_fpArmsLayer, 1f);
                SetLayerWeight(_localBodyLayer, localSlideSession && !groundedLocalSlide ? 1f : 0f);

                if (Player == null)
                    return;

                bool grounded = Player.thisController != null && Player.thisController.isGrounded;
                Vector3 velocity = Player.thisController != null ? Player.thisController.velocity : Vector3.zero;
                velocity.y = 0f;

                SetBool(WalkingHash, velocity.sqrMagnitude > 0.05f);
                SetBool(SprintingHash, Player.isSprinting);
                SetBool(SidewaysHash, false);
                SetBool(CrouchingHash, Player.isCrouching);
                SetBool(JumpingHash, !grounded && velocity.sqrMagnitude > 0.01f);
                SetBool(FallNoJumpHash, !grounded);
                SetFloat(AnimationSpeedHash, 1f);
            }

            private void RefreshLocalSlideBodyMode()
            {
                bool grounded = ShouldUseGroundedLocalSlideBody(Player, out string reason);
                if (_localSlideBodyModeInitialized &&
                    _useGroundedLocalSlideBody == grounded &&
                    string.Equals(_localSlideBodyModeReason, reason, StringComparison.Ordinal))
                {
                    return;
                }

                _localSlideBodyModeInitialized = true;
                _useGroundedLocalSlideBody = grounded;
                _localSlideBodyModeReason = reason;
                Plugin.Log?.LogDebug(
                    "[Panic Slide.presentation] local_body_mode_selected: " +
                    $"player={FormatPlayerForLog(Player)} " +
                    $"mode='{(grounded ? "grounded_full_body" : "camera_composed_body")}' " +
                    $"reason='{reason}' fullBodyWeight={(grounded ? 1 : 0)} " +
                    $"localBodyWeight={(grounded ? 0 : 1)}.");
            }

            private void CacheLayerIndices()
            {
                _fullBodyLayer = FindLayerIndex(FullBodyLayerName);
                _fpArmsLayer = FindLayerIndex(FpArmsLayerName);
                _localBodyLayer = FindLayerIndex(LocalBodyLayerName);
            }

            private static string ResolveFpArmsEnterState(MovementAnimKind kind)
            {
                switch (kind)
                {
                    case MovementAnimKind.Mantle1m:
                        return "Mantle1m";
                    case MovementAnimKind.Mantle2m:
                        return "Mantle2m";
                    default:
                        return "SlideStart";
                }
            }

            private void CrossFadeFpArmsState(string stateName)
            {
                // Mantle entries fade fast: during the 0.15s blend the vanilla lowered-arms
                // pose leaves glove geometry at the near plane (one-frame giant-glove flash
                // in the pass-9 diagnostics).
                float duration = _kind == MovementAnimKind.Slide ? FpArmsCrossFadeSeconds : 0.05f;
                CrossFadeMovementLayerState(_fpArmsLayer, "FP arms", stateName, ref _fpArmsCrossFadeWarned, duration);
            }

            private void CrossFadeLocalBodySlideState(string stateName)
            {
                if (_kind != MovementAnimKind.Slide || !IsLocalPlayerSession())
                    return;

                CrossFadeMovementLayerState(_localBodyLayer, "local body", stateName, ref _localBodyCrossFadeWarned);
            }

            private void CrossFadeMovementLayerState(int layerIndex, string layerLabel, string stateName, ref bool warned, float duration = FpArmsCrossFadeSeconds)
            {
                if (_animator == null || layerIndex < 0 || string.IsNullOrEmpty(stateName))
                    return;

                try
                {
                    _animator.CrossFadeInFixedTime(stateName, duration, layerIndex, 0f);
                }
                catch (Exception ex)
                {
                    if (!warned)
                    {
                        warned = true;
                        Plugin.Log?.LogWarning(
                            $"[Panic Slide] {layerLabel} layer crossfade to '{stateName}' failed for player {FormatPlayerForLog(Player)}: {ex.Message}");
                    }
                }
            }

            private bool IsLocalPlayerSession()
            {
                return Player != null && Player == GameNetworkManager.Instance?.localPlayerController;
            }

            private int FindLayerIndex(string layerName)
            {
                if (_animator == null || string.IsNullOrEmpty(layerName))
                    return -1;

                try
                {
                    for (int i = 0; i < _animator.layerCount; i++)
                    {
                        if (string.Equals(_animator.GetLayerName(i), layerName, StringComparison.Ordinal))
                            return i;
                    }
                }
                catch { }

                return -1;
            }

            private void SetLayerWeight(int layerIndex, float weight)
            {
                if (_animator == null || layerIndex < 0 || layerIndex >= _animator.layerCount)
                    return;

                try { _animator.SetLayerWeight(layerIndex, Mathf.Clamp01(weight)); } catch { }
            }

            private static int ResolveEnterHash(MovementAnimKind kind)
            {
                switch (kind)
                {
                    case MovementAnimKind.Mantle1m:
                        return Mantle1mHash;
                    case MovementAnimKind.Mantle2m:
                        return Mantle2mHash;
                    default:
                        return EnterHash;
                }
            }

            private static float ResolveAutoEndSeconds(MovementAnimKind kind)
            {
                switch (kind)
                {
                    case MovementAnimKind.Mantle1m:
                        return Mantle1mSessionSeconds;
                    case MovementAnimKind.Mantle2m:
                        return Mantle2mSessionSeconds;
                    default:
                        return 0f;
                }
            }

            private void SaveVanillaAnimatorState(Animator animator)
            {
                if (animator == null)
                    return;

                int layerCount = Mathf.Max(0, animator.layerCount);
                _savedLayerWeights = new float[layerCount];
                _savedStateHashes = new int[layerCount];
                _savedStateTimes = new float[layerCount];
                for (int i = 0; i < layerCount; i++)
                {
                    _savedLayerWeights[i] = animator.GetLayerWeight(i);
                    try
                    {
                        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(i);
                        _savedStateHashes[i] = state.fullPathHash;
                        _savedStateTimes[i] = state.normalizedTime;
                    }
                    catch
                    {
                        _savedStateHashes[i] = 0;
                        _savedStateTimes[i] = 0f;
                    }
                }

                _savedParameters.Clear();
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    try
                    {
                        switch (parameter.type)
                        {
                            case AnimatorControllerParameterType.Bool:
                                _savedParameters.Add(AnimatorParameterSnapshot.Bool(parameter.nameHash, animator.GetBool(parameter.nameHash)));
                                break;
                            case AnimatorControllerParameterType.Float:
                                _savedParameters.Add(AnimatorParameterSnapshot.Float(parameter.nameHash, animator.GetFloat(parameter.nameHash)));
                                break;
                            case AnimatorControllerParameterType.Int:
                                _savedParameters.Add(AnimatorParameterSnapshot.Int(parameter.nameHash, animator.GetInteger(parameter.nameHash)));
                                break;
                        }
                    }
                    catch { }
                }
            }

            private void RestoreVanillaAnimatorController(Animator animator)
            {
                AnimatorStateRestoreMode restoreStateMode = ResolveRestoreStateMode(
                    out string configuredRestoreStateMode,
                    out bool usedRestoreStateFallback);
                string restoreStateModeLabel = FormatRestoreStateMode(restoreStateMode);
                Plugin.Log?.LogDebug(
                    "[Panic Slide.staterestore] restore_begin: " +
                    $"mode='{restoreStateModeLabel}' configured='{configuredRestoreStateMode}' " +
                    $"fallback={usedRestoreStateFallback} frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(Player)} savedLayers={_savedStateHashes.Length}.");
                CameraRotationSnapshot seamCameraRotation =
                    CaptureSeamCameraRotation("controller_restore");
                VisorPoseSnapshot seamVisorPose =
                    CaptureSeamVisorPose(animator, "controller_restore");
                if (animator == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerarotation] restore_skipped: " +
                        $"phase='controller_restore' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='missing_animator'.");
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] restore_skipped: " +
                        $"phase='controller_restore' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='missing_animator'.");
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.staterestore] restore_skipped: " +
                        $"mode='{restoreStateModeLabel}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='missing_animator'.");
                    return;
                }

                try
                {
                    if (animator.runtimeAnimatorController == _controller)
                        ResetMovementRuntimeParameters();
                    animator.runtimeAnimatorController = _savedController;
                    // A controller swap can leave movement-clip bone and constraint-target values
                    // in the live hierarchy. Replaying the session-begin transform hierarchy here
                    // mixed an airborne/pre-slide pose with the freshly-entered vanilla state and
                    // rebuilt the leg constraints against stale targets, permanently folding knees.
                    // Rebind is the Animator-owned reset for those bindings; the targeted camera
                    // and visor seam snapshots below preserve the view state that must survive it.
                    animator.Rebind();
                    RestoreLayerWeights(animator);
                    RestoreAnimatorParameters(animator);
                    RestoreAnimatorStates(animator, restoreStateMode);
                    animator.Update(0f);
                    // Rig rebuild LAST, after the restored controller has evaluated once —
                    // rebuilding mid-restore left the state machine stalled until the next
                    // parameter-driven transition (body frozen in third person until e.g. a jump).
                    RebuildAnimationRigging(
                        animator,
                        "controller restore",
                        evaluateRebuiltRigsAtZeroDelta: true);
                }
                finally
                {
                    // The controller assignment implicitly rebinds the Animator, and the scoped
                    // pose restore can contain a slide-start camera pose. The configured camera
                    // baseline and captured visor pose must win before the final seam evaluation.
                    ReapplySeamCameraRotation(seamCameraRotation, "controller_restore");
                    ReapplySeamVisorPose(seamVisorPose, "controller_restore");
                }

                animator.Update(0f);
                Plugin.Log?.LogDebug(
                    $"[Panic Slide] Vanilla Animator rebind cleared movement pose residue " +
                    $"for player {FormatPlayerForLog(Player)}.");
            }

            private static AnimatorStateRestoreMode ResolveRestoreStateMode(
                out string configuredMode,
                out bool usedFallback)
            {
                string rawMode = Plugin.SlideRestoreStateMode?.Value;
                configuredMode = rawMode == null ? "<default:fresh>" : rawMode.Trim();
                usedFallback = false;

                if (string.Equals(configuredMode, "crossfade", StringComparison.OrdinalIgnoreCase))
                    return AnimatorStateRestoreMode.Crossfade;
                if (string.Equals(configuredMode, "replay", StringComparison.OrdinalIgnoreCase))
                    return AnimatorStateRestoreMode.Replay;
                if (rawMode == null || string.Equals(configuredMode, "fresh", StringComparison.OrdinalIgnoreCase))
                    return AnimatorStateRestoreMode.Fresh;

                usedFallback = true;
                Plugin.Log?.LogWarning(
                    "[Panic Slide.staterestore] mode_fallback: " +
                    $"configured='{configuredMode}' resolved='fresh' reason='unsupported_value'.");
                return AnimatorStateRestoreMode.Fresh;
            }

            private static string FormatRestoreStateMode(AnimatorStateRestoreMode restoreStateMode)
            {
                switch (restoreStateMode)
                {
                    case AnimatorStateRestoreMode.Crossfade:
                        return "crossfade";
                    case AnimatorStateRestoreMode.Replay:
                        return "replay";
                    default:
                        return "fresh";
                }
            }

            private static CameraRestoreBaselineMode ResolveCameraRestoreBaselineMode(
                out string configuredMode,
                out bool usedFallback)
            {
                string rawMode = Plugin.SlideRestoreCameraBaseline?.Value;
                configuredMode = rawMode == null ? "<default:begin>" : rawMode.Trim();
                usedFallback = false;

                if (string.Equals(configuredMode, "live", StringComparison.OrdinalIgnoreCase))
                    return CameraRestoreBaselineMode.Live;
                if (rawMode == null || string.Equals(configuredMode, "begin", StringComparison.OrdinalIgnoreCase))
                    return CameraRestoreBaselineMode.Begin;

                usedFallback = true;
                Plugin.Log?.LogWarning(
                    "[Panic Slide.camerabaseline] mode_fallback: " +
                    $"configured='{configuredMode}' resolved='begin' reason='unsupported_value'.");
                return CameraRestoreBaselineMode.Begin;
            }

            private static string FormatCameraRestoreBaselineMode(
                CameraRestoreBaselineMode restoreBaselineMode)
            {
                return restoreBaselineMode == CameraRestoreBaselineMode.Live
                    ? "live"
                    : "begin";
            }

            private CameraPoseSnapshot CaptureBeginCameraPose(string phase)
            {
                PlayerControllerB player = Player;
                if (player == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} player=<null> " +
                        "reason='missing_player'.");
                    return default;
                }

                PlayerControllerB localPlayer;
                try
                {
                    localPlayer = GameNetworkManager.Instance != null
                        ? GameNetworkManager.Instance.localPlayerController
                        : null;
                    if (localPlayer == null && StartOfRound.Instance != null)
                        localPlayer = StartOfRound.Instance.localPlayerController;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} " +
                        $"reason='local_player_resolution_failed' error='{ex.Message}'.");
                    return default;
                }

                if (!ReferenceEquals(player, localPlayer))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.camerabaseline] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} reason='not_local_player'.");
                    return default;
                }

                SeamTransformPose gameplayCamera = default;
                SeamTransformPose cameraContainer = default;
                Vector3 gameplayCameraPlayerLocalPosition = Vector3.zero;
                bool gameplayCameraPlayerLocalPositionCaptured = false;

                try
                {
                    Transform gameplayCameraTransform = player.gameplayCamera != null
                        ? player.gameplayCamera.transform
                        : null;
                    if (gameplayCameraTransform == null)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerabaseline] capture_target_missing: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(player)} target='gameplayCamera'.");
                    }
                    else
                    {
                        bool underAnimatorHierarchy = _animator != null &&
                            gameplayCameraTransform.IsChildOf(_animator.transform);
                        gameplayCamera = new SeamTransformPose(
                            gameplayCameraTransform,
                            underAnimatorHierarchy);

                        if (player.transform != null)
                        {
                            gameplayCameraPlayerLocalPosition =
                                player.transform.InverseTransformPoint(gameplayCameraTransform.position);
                            gameplayCameraPlayerLocalPositionCaptured = true;
                        }
                        else
                        {
                            Plugin.Log?.LogWarning(
                                "[Panic Slide.camerabaseline] capture_target_missing: " +
                                $"phase='{phase}' frame={Time.frameCount} " +
                                $"player={FormatPlayerForLog(player)} target='playerRoot'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] capture_target_failed: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} target='gameplayCamera' " +
                        $"error='{ex.Message}'.");
                }

                try
                {
                    Transform cameraContainerTransform = player.cameraContainerTransform;
                    if (cameraContainerTransform == null)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerabaseline] capture_target_missing: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(player)} target='cameraContainerTransform'.");
                    }
                    else
                    {
                        bool underAnimatorHierarchy = _animator != null &&
                            cameraContainerTransform.IsChildOf(_animator.transform);
                        cameraContainer = new SeamTransformPose(
                            cameraContainerTransform,
                            underAnimatorHierarchy);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] capture_target_failed: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} target='cameraContainerTransform' " +
                        $"error='{ex.Message}'.");
                }

                if (!gameplayCamera.Captured && !cameraContainer.Captured)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} reason='no_pose_targets'.");
                    return default;
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide.camerabaseline] captured: " +
                    $"phase='{phase}' frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(player)} " +
                    $"mode='{FormatCameraRestoreBaselineMode(_cameraRestoreBaselineMode)}' " +
                    $"gameplayCamera={DescribeCapturedSeamPose(gameplayCamera)} " +
                    $"gameplayCameraPlayerLocalPosition=" +
                    $"{(gameplayCameraPlayerLocalPositionCaptured ? DescribeVector(gameplayCameraPlayerLocalPosition) : "<not_captured>")} " +
                    $"cameraContainer={DescribeCapturedSeamPose(cameraContainer)}.");
                return new CameraPoseSnapshot(
                    gameplayCamera,
                    cameraContainer,
                    gameplayCameraPlayerLocalPosition,
                    gameplayCameraPlayerLocalPositionCaptured);
            }

            private void StartPostRestoreSampler(Animator animator)
            {
                if (!(Plugin.SlidePostRestoreDiagnostics?.Value ?? false))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.postrestore] sampler_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(Player)} reason='disabled'.");
                    return;
                }

                PlayerControllerB player = Player;
                if (player == null || player.gameObject == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.postrestore] sampler_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(player)} reason='missing_player'.");
                    return;
                }

                try
                {
                    var sampler = player.gameObject.AddComponent<ParkourPostRestoreSampler>();
                    sampler.Initialize(
                        animator,
                        player.transform,
                        player.cameraContainerTransform,
                        player.gameplayCamera != null ? player.gameplayCamera.transform : null,
                        animator != null ? animator.transform : null,
                        player.thisPlayerBody,
                        FormatPlayerForLog(player));
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.postrestore] sampler_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(player)} " +
                        $"reason='sampler_start_failed' error='{ex.Message}'.");
                }
            }

            private CameraRotationSnapshot CaptureSeamCameraRotation(string phase)
            {
                if (!(Plugin.SlideRestoreCameraRotation?.Value ?? true))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.camerarotation] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='disabled'.");
                    return default;
                }

                PlayerControllerB player = Player;
                if (player == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerarotation] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} player=<null> " +
                        "reason='missing_player'.");
                    return default;
                }

                PlayerControllerB localPlayer;
                try
                {
                    localPlayer = GameNetworkManager.Instance != null
                        ? GameNetworkManager.Instance.localPlayerController
                        : null;
                    if (localPlayer == null && StartOfRound.Instance != null)
                        localPlayer = StartOfRound.Instance.localPlayerController;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerarotation] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} " +
                        $"reason='local_player_resolution_failed' error='{ex.Message}'.");
                    return default;
                }

                if (!ReferenceEquals(player, localPlayer))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.camerarotation] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} reason='not_local_player'.");
                    return default;
                }

                Transform gameplayCameraTransform = null;
                Transform cameraContainerTransform = null;
                Quaternion gameplayCameraLocalRotation = Quaternion.identity;
                Quaternion cameraContainerLocalRotation = Quaternion.identity;
                bool gameplayCameraCaptured = false;
                bool cameraContainerCaptured = false;

                try
                {
                    gameplayCameraTransform = player.gameplayCamera != null
                        ? player.gameplayCamera.transform
                        : null;
                    if (gameplayCameraTransform != null)
                    {
                        gameplayCameraLocalRotation = gameplayCameraTransform.localRotation;
                        gameplayCameraCaptured = true;
                    }
                    else
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerarotation] capture_target_missing: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(player)} target='gameplayCamera'.");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerarotation] capture_target_failed: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} target='gameplayCamera' " +
                        $"error='{ex.Message}'.");
                }

                try
                {
                    cameraContainerTransform = player.cameraContainerTransform;
                    if (cameraContainerTransform != null)
                    {
                        cameraContainerLocalRotation = cameraContainerTransform.localRotation;
                        cameraContainerCaptured = true;
                    }
                    else
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerarotation] capture_target_missing: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(player)} target='cameraContainerTransform'.");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerarotation] capture_target_failed: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} target='cameraContainerTransform' " +
                        $"error='{ex.Message}'.");
                }

                if (!gameplayCameraCaptured && !cameraContainerCaptured)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerarotation] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} reason='no_rotation_targets'.");
                    return default;
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide.camerarotation] captured: " +
                    $"phase='{phase}' frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(player)} " +
                    $"gameplayCameraLocalEuler={DescribeCapturedEuler(gameplayCameraCaptured, gameplayCameraLocalRotation)} " +
                    $"cameraContainerLocalEuler={DescribeCapturedEuler(cameraContainerCaptured, cameraContainerLocalRotation)}.");
                return new CameraRotationSnapshot(
                    gameplayCameraTransform,
                    gameplayCameraLocalRotation,
                    gameplayCameraCaptured,
                    cameraContainerTransform,
                    cameraContainerLocalRotation,
                    cameraContainerCaptured);
            }

            private void ReapplySeamCameraRotation(CameraRotationSnapshot captured, string phase)
            {
                if (_cameraRestoreBaselineMode == CameraRestoreBaselineMode.Begin)
                {
                    if (_beginCameraPose.HasAnyPose)
                    {
                        ReapplyBeginCameraPose(captured, phase);
                        return;
                    }

                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] restore_fallback: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} requested='begin' resolved='live' " +
                        "reason='begin_pose_unavailable'.");
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide.camerabaseline] restore_selected: " +
                    $"phase='{phase}' frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(Player)} mode='live' " +
                    "gameplayCameraStrategy='live_rotation' cameraContainerStrategy='live_rotation'.");

                if (!(Plugin.SlideRestoreCameraRotation?.Value ?? true))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.camerarotation] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='disabled'.");
                    return;
                }

                if (!captured.HasAnyRotation)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerarotation] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='capture_unavailable'.");
                    return;
                }

                bool gameplayCameraApplied = false;
                bool cameraContainerApplied = false;
                string gameplayCameraEuler = captured.GameplayCameraCaptured
                    ? "<target_missing>"
                    : "<not_captured>";
                string cameraContainerEuler = captured.CameraContainerCaptured
                    ? "<target_missing>"
                    : "<not_captured>";

                if (captured.GameplayCameraCaptured)
                {
                    if (captured.GameplayCameraTransform == null)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerarotation] reapply_target_missing: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} target='gameplayCamera'.");
                    }
                    else
                    {
                        try
                        {
                            captured.GameplayCameraTransform.localRotation =
                                captured.GameplayCameraLocalRotation;
                            gameplayCameraEuler =
                                DescribeEuler(captured.GameplayCameraTransform.localEulerAngles);
                            gameplayCameraApplied = true;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.LogWarning(
                                "[Panic Slide.camerarotation] reapply_target_failed: " +
                                $"phase='{phase}' frame={Time.frameCount} " +
                                $"player={FormatPlayerForLog(Player)} target='gameplayCamera' " +
                                $"error='{ex.Message}'.");
                        }
                    }
                }

                if (captured.CameraContainerCaptured)
                {
                    if (captured.CameraContainerTransform == null)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerarotation] reapply_target_missing: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} target='cameraContainerTransform'.");
                    }
                    else
                    {
                        try
                        {
                            captured.CameraContainerTransform.localRotation =
                                captured.CameraContainerLocalRotation;
                            cameraContainerEuler =
                                DescribeEuler(captured.CameraContainerTransform.localEulerAngles);
                            cameraContainerApplied = true;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.LogWarning(
                                "[Panic Slide.camerarotation] reapply_target_failed: " +
                                $"phase='{phase}' frame={Time.frameCount} " +
                                $"player={FormatPlayerForLog(Player)} target='cameraContainerTransform' " +
                                $"error='{ex.Message}'.");
                        }
                    }
                }

                if (!gameplayCameraApplied && !cameraContainerApplied)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerarotation] reapply_failed: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='no_rotation_targets_applied'.");
                    return;
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide.camerarotation] reapplied: " +
                    $"phase='{phase}' frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(Player)} " +
                    $"gameplayCameraApplied={gameplayCameraApplied} gameplayCameraLocalEuler={gameplayCameraEuler} " +
                    $"cameraContainerApplied={cameraContainerApplied} cameraContainerLocalEuler={cameraContainerEuler}.");
            }

            private void ReapplyBeginCameraPose(
                CameraRotationSnapshot liveCaptured,
                string phase)
            {
                bool restoreRotation = Plugin.SlideRestoreCameraRotation?.Value ?? true;
                bool livePitchCaptured = false;
                float livePitch = 0f;
                string pitchSource = "<not_needed>";

                if (restoreRotation)
                {
                    if (Player != null)
                    {
                        try
                        {
                            livePitch = Player.cameraUp;
                            livePitchCaptured = true;
                            pitchSource = "cameraUp";
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.LogWarning(
                                "[Panic Slide.camerabaseline] pitch_capture_failed: " +
                                $"phase='{phase}' frame={Time.frameCount} " +
                                $"player={FormatPlayerForLog(Player)} source='cameraUp' " +
                                $"error='{ex.Message}'.");
                        }
                    }

                    if (!livePitchCaptured && liveCaptured.GameplayCameraCaptured)
                    {
                        livePitch = liveCaptured.GameplayCameraLocalRotation.eulerAngles.x;
                        livePitchCaptured = true;
                        pitchSource = "live_camera_fallback";
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerabaseline] pitch_fallback: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} requested='cameraUp' " +
                            "resolved='live_camera_capture'.");
                    }

                    if (!livePitchCaptured && _beginCameraPose.GameplayCamera.Captured)
                    {
                        livePitch = _beginCameraPose.GameplayCamera.LocalRotation.eulerAngles.x;
                        livePitchCaptured = true;
                        pitchSource = "begin_pose_fallback";
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerabaseline] pitch_fallback: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} requested='cameraUp' " +
                            "resolved='begin_pose'.");
                    }
                }
                else
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.camerarotation] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} baseline='begin' reason='disabled'.");
                }

                bool gameplayCameraApplied = false;
                bool cameraContainerApplied = false;
                string gameplayCameraPose = _beginCameraPose.GameplayCamera.Captured
                    ? "<target_missing>"
                    : "<not_captured>";
                string cameraContainerPose = _beginCameraPose.CameraContainer.Captured
                    ? "<target_missing>"
                    : "<not_captured>";

                SeamTransformPose gameplayCamera = _beginCameraPose.GameplayCamera;
                if (!gameplayCamera.Captured)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] restore_target_missing: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='gameplayCamera' " +
                        "reason='begin_pose_not_captured'.");
                }
                else if (gameplayCamera.Transform == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] restore_target_missing: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='gameplayCamera' " +
                        "reason='transform_destroyed'.");
                }
                else
                {
                    try
                    {
                        gameplayCamera.Transform.localPosition = gameplayCamera.LocalPosition;
                        gameplayCamera.Transform.localScale = gameplayCamera.LocalScale;
                        if (restoreRotation && livePitchCaptured)
                        {
                            Vector3 cleanEuler = gameplayCamera.LocalRotation.eulerAngles;
                            gameplayCamera.Transform.localEulerAngles =
                                new Vector3(livePitch, cleanEuler.y, cleanEuler.z);
                        }

                        gameplayCameraPose = DescribeCurrentSeamPose(gameplayCamera.Transform);
                        gameplayCameraApplied = true;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerabaseline] restore_target_failed: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} target='gameplayCamera' " +
                            $"error='{ex.Message}'.");
                    }
                }

                SeamTransformPose cameraContainer = _beginCameraPose.CameraContainer;
                if (!cameraContainer.Captured)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] restore_target_missing: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='cameraContainerTransform' " +
                        "reason='begin_pose_not_captured'.");
                }
                else if (cameraContainer.Transform == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] restore_target_missing: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='cameraContainerTransform' " +
                        "reason='transform_destroyed'.");
                }
                else
                {
                    try
                    {
                        cameraContainer.Transform.localPosition = cameraContainer.LocalPosition;
                        cameraContainer.Transform.localScale = cameraContainer.LocalScale;
                        if (restoreRotation)
                            cameraContainer.Transform.localRotation = cameraContainer.LocalRotation;

                        cameraContainerPose = DescribeCurrentSeamPose(cameraContainer.Transform);
                        cameraContainerApplied = true;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.camerabaseline] restore_target_failed: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} target='cameraContainerTransform' " +
                            $"error='{ex.Message}'.");
                    }
                }

                if (!gameplayCameraApplied && !cameraContainerApplied)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.camerabaseline] restore_failed: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='no_pose_targets_applied'.");
                    return;
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide.camerabaseline] restored: " +
                    $"phase='{phase}' frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(Player)} mode='begin' " +
                    $"pitchSource='{pitchSource}' pitch={livePitch:0.######} " +
                    $"gameplayCameraApplied={gameplayCameraApplied} gameplayCamera={gameplayCameraPose} " +
                    $"cameraContainerApplied={cameraContainerApplied} cameraContainer={cameraContainerPose} " +
                    "gameplayCameraStrategy='begin_position_scale_clean_yaw_roll_live_pitch' " +
                    "cameraContainerStrategy='begin_trs'.");
            }

            private VisorPoseSnapshot CaptureSeamVisorPose(Animator animator, string phase)
            {
                if (!(Plugin.SlideRestoreVisorPose?.Value ?? true))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='disabled'.");
                    return default;
                }

                PlayerControllerB player = Player;
                if (player == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} player=<null> " +
                        "reason='missing_player'.");
                    return default;
                }

                PlayerControllerB localPlayer;
                try
                {
                    localPlayer = GameNetworkManager.Instance != null
                        ? GameNetworkManager.Instance.localPlayerController
                        : null;
                    if (localPlayer == null && StartOfRound.Instance != null)
                        localPlayer = StartOfRound.Instance.localPlayerController;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} " +
                        $"reason='local_player_resolution_failed' error='{ex.Message}'.");
                    return default;
                }

                if (!ReferenceEquals(player, localPlayer))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} reason='not_local_player'.");
                    return default;
                }

                Transform animatorRoot = null;
                Transform localVisor = null;
                Transform localVisorTargetPoint = null;
                bool localVisorReadFailed = false;
                bool targetPointReadFailed = false;

                try
                {
                    animatorRoot = animator != null
                        ? animator.transform
                        : player.playerBodyAnimator != null
                            ? player.playerBodyAnimator.transform
                            : null;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} target='animatorRoot' " +
                        $"reason='field_read_failed' error='{ex.Message}'.");
                }

                try
                {
                    localVisor = player.localVisor;
                }
                catch (Exception ex)
                {
                    localVisorReadFailed = true;
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} target='localVisor' " +
                        $"reason='field_read_failed' error='{ex.Message}'.");
                }

                try
                {
                    localVisorTargetPoint = player.localVisorTargetPoint;
                }
                catch (Exception ex)
                {
                    targetPointReadFailed = true;
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} target='localVisorTargetPoint' " +
                        $"reason='field_read_failed' error='{ex.Message}'.");
                }

                LogVisorHierarchyOnce(
                    animatorRoot,
                    localVisor,
                    localVisorTargetPoint,
                    phase);

                SeamTransformPose localVisorPose = default;
                SeamTransformPose targetPointPose = default;
                if (!localVisorReadFailed)
                {
                    TryCaptureSeamVisorTarget(
                        localVisor,
                        animatorRoot,
                        "localVisor",
                        phase,
                        out localVisorPose);
                }
                if (!targetPointReadFailed)
                {
                    TryCaptureSeamVisorTarget(
                        localVisorTargetPoint,
                        animatorRoot,
                        "localVisorTargetPoint",
                        phase,
                        out targetPointPose);
                }

                var captured = new VisorPoseSnapshot(localVisorPose, targetPointPose);
                if (!captured.HasAnyPose)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} reason='no_pose_targets'.");
                    return default;
                }

                if (!captured.HasAnyRestoreEligiblePose)
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(player)} " +
                        "reason='no_targets_under_animator_hierarchy'.");
                    return default;
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide.visor] captured: " +
                    $"phase='{phase}' frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(player)} " +
                    $"localVisor={DescribeCapturedSeamPose(captured.LocalVisor)} " +
                    $"localVisorTargetPoint={DescribeCapturedSeamPose(captured.LocalVisorTargetPoint)}.");
                return captured;
            }

            private bool TryCaptureSeamVisorTarget(
                Transform target,
                Transform animatorRoot,
                string targetName,
                string phase,
                out SeamTransformPose captured)
            {
                captured = default;
                if (target == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='{targetName}' " +
                        "reason='missing_transform'.");
                    return false;
                }

                try
                {
                    bool underAnimatorHierarchy =
                        animatorRoot != null &&
                        (ReferenceEquals(target, animatorRoot) || target.IsChildOf(animatorRoot));
                    captured = new SeamTransformPose(target, underAnimatorHierarchy);
                    return true;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] capture_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='{targetName}' " +
                        $"reason='capture_failed' error='{ex.Message}'.");
                    return false;
                }
            }

            private void ReapplySeamVisorPose(VisorPoseSnapshot captured, string phase)
            {
                if (!(Plugin.SlideRestoreVisorPose?.Value ?? true))
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.visor] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='disabled'.");
                    return;
                }

                if (!captured.HasAnyPose)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='capture_unavailable'.");
                    return;
                }

                bool targetPointApplied = TryReapplySeamVisorTarget(
                    captured.LocalVisorTargetPoint,
                    "localVisorTargetPoint",
                    phase,
                    restoreWorldPose: false);
                bool localVisorApplied = TryReapplySeamVisorTarget(
                    captured.LocalVisor,
                    "localVisor",
                    phase,
                    restoreWorldPose: true);
                bool vanillaGlueApplied = false;

                if (captured.LocalVisor.Captured && captured.LocalVisorTargetPoint.Captured &&
                    captured.LocalVisor.Transform != null &&
                    captured.LocalVisorTargetPoint.Transform != null)
                {
                    try
                    {
                        // Vanilla snaps visor position each LateUpdate, but its rotation trails the
                        // target through a Lerp. Preserve that exact pre-seam rotation state.
                        captured.LocalVisor.Transform.position =
                            captured.LocalVisorTargetPoint.Transform.position;
                        captured.LocalVisor.Transform.rotation =
                            captured.LocalVisor.WorldRotation;
                        vanillaGlueApplied = true;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.visor] reapply_skipped: " +
                            $"phase='{phase}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} target='vanillaVisorGlue' " +
                            $"reason='apply_failed' error='{ex.Message}'.");
                    }
                }
                else
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='vanillaVisorGlue' " +
                        "reason='missing_captured_transform'.");
                }

                if (!targetPointApplied && !localVisorApplied && !vanillaGlueApplied)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='no_targets_applied'.");
                    return;
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide.visor] reapplied: " +
                    $"phase='{phase}' frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(Player)} " +
                    $"localVisorApplied={localVisorApplied} " +
                    $"localVisor={DescribeCurrentSeamPose(captured.LocalVisor.Transform)} " +
                    $"localVisorTargetPointApplied={targetPointApplied} " +
                    $"localVisorTargetPoint={DescribeCurrentSeamPose(captured.LocalVisorTargetPoint.Transform)} " +
                    $"vanillaGlueApplied={vanillaGlueApplied} " +
                    $"preservedVisorWorldEuler={DescribeEuler(captured.LocalVisor.WorldRotation.eulerAngles)}.");
            }

            private bool TryReapplySeamVisorTarget(
                SeamTransformPose captured,
                string targetName,
                string phase,
                bool restoreWorldPose)
            {
                if (!captured.Captured)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='{targetName}' " +
                        "reason='capture_unavailable'.");
                    return false;
                }

                if (!captured.UnderAnimatorHierarchy)
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.visor] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='{targetName}' " +
                        "reason='not_under_animator_hierarchy'.");
                    return false;
                }

                if (captured.Transform == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='{targetName}' " +
                        "reason='target_missing'.");
                    return false;
                }

                try
                {
                    captured.Transform.localPosition = captured.LocalPosition;
                    captured.Transform.localRotation = captured.LocalRotation;
                    captured.Transform.localScale = captured.LocalScale;
                    if (restoreWorldPose)
                    {
                        captured.Transform.SetPositionAndRotation(
                            captured.WorldPosition,
                            captured.WorldRotation);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] reapply_skipped: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} target='{targetName}' " +
                        $"reason='apply_failed' error='{ex.Message}'.");
                    return false;
                }
            }

            private void LogVisorHierarchyOnce(
                Transform animatorRoot,
                Transform localVisor,
                Transform localVisorTargetPoint,
                string phase)
            {
                if (_visorHierarchyLogged)
                    return;

                _visorHierarchyLogged = true;
                try
                {
                    Plugin.Log?.LogDebug(
                        "[Panic Slide.visor] hierarchy: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} " +
                        $"animatorRootPath='{DescribeHierarchyPath(animatorRoot)}' " +
                        $"localVisorPath='{DescribeHierarchyPath(localVisor)}' " +
                        $"localVisorUnderAnimatorHierarchy={IsUnderHierarchy(localVisor, animatorRoot)} " +
                        $"localVisorTargetPointPath='{DescribeHierarchyPath(localVisorTargetPoint)}' " +
                        $"localVisorTargetPointUnderAnimatorHierarchy={IsUnderHierarchy(localVisorTargetPoint, animatorRoot)}.");
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.visor] hierarchy_failed: " +
                        $"phase='{phase}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} error='{ex.Message}'.");
                }
            }

            private static bool IsUnderHierarchy(Transform target, Transform root)
            {
                try
                {
                    return target != null && root != null &&
                        (ReferenceEquals(target, root) || target.IsChildOf(root));
                }
                catch
                {
                    return false;
                }
            }

            private static string DescribeHierarchyPath(Transform transform)
            {
                if (transform == null)
                    return "<missing>";

                try
                {
                    var names = new List<string>();
                    Transform current = transform;
                    while (current != null)
                    {
                        names.Add(current.name ?? "<unnamed>");
                        current = current.parent;
                    }
                    names.Reverse();
                    return string.Join("/", names.ToArray())
                        .Replace('\r', ' ')
                        .Replace('\n', ' ')
                        .Replace('\'', '"');
                }
                catch (Exception ex)
                {
                    return "<read_failed:" + ex.GetType().Name + ">";
                }
            }

            private static string DescribeCapturedSeamPose(SeamTransformPose captured)
            {
                if (!captured.Captured)
                    return "[captured=False]";

                return
                    $"[captured=True underAnimatorHierarchy={captured.UnderAnimatorHierarchy} " +
                    $"localPos={DescribeVector(captured.LocalPosition)} " +
                    $"localEuler={DescribeEuler(captured.LocalRotation.eulerAngles)} " +
                    $"localScale={DescribeVector(captured.LocalScale)} " +
                    $"worldPos={DescribeVector(captured.WorldPosition)} " +
                    $"worldEuler={DescribeEuler(captured.WorldRotation.eulerAngles)} " +
                    $"worldScale={DescribeVector(captured.WorldScale)}]";
            }

            private static string DescribeCurrentSeamPose(Transform transform)
            {
                if (transform == null)
                    return "<target_missing>";

                try
                {
                    return
                        $"[localPos={DescribeVector(transform.localPosition)} " +
                        $"localEuler={DescribeEuler(transform.localEulerAngles)} " +
                        $"localScale={DescribeVector(transform.localScale)} " +
                        $"worldPos={DescribeVector(transform.position)} " +
                        $"worldEuler={DescribeEuler(transform.rotation.eulerAngles)} " +
                        $"worldScale={DescribeVector(transform.lossyScale)}]";
                }
                catch (Exception ex)
                {
                    return "<read_failed:" + ex.GetType().Name + ">";
                }
            }

            private static string DescribeVector(Vector3 value)
            {
                return $"({value.x:0.######},{value.y:0.######},{value.z:0.######})";
            }

            private static string DescribeCapturedEuler(bool captured, Quaternion rotation)
            {
                return captured ? DescribeEuler(rotation.eulerAngles) : "<not_captured>";
            }

            private static string DescribeEuler(Vector3 euler)
            {
                return $"({euler.x:0.######},{euler.y:0.######},{euler.z:0.######})";
            }

            private void RebuildAnimationRigging(
                Animator animator,
                string reason,
                bool evaluateRebuiltRigsAtZeroDelta = false)
            {
                int rebuilt = 0;
                int evaluated = 0;
                Type rigBuilderType = ResolveRigBuilderType();
                MethodInfo buildMethod = _rigBuilderBuildMethod;
                MethodInfo evaluateMethod = _rigBuilderEvaluateMethod;
                var rebuiltBuilders = evaluateRebuiltRigsAtZeroDelta
                    ? new List<Component>()
                    : null;

                if (animator != null && rigBuilderType != null && buildMethod != null)
                {
                    var rigBuilders = new HashSet<Component>();
                    AddRigBuilders(animator, rigBuilderType, rigBuilders);
                    if (Player != null)
                        AddRigBuilders(Player, rigBuilderType, rigBuilders);

                    foreach (Component rigBuilder in rigBuilders)
                    {
                        if (rigBuilder == null)
                            continue;
                        if (rigBuilder is Behaviour behaviour && !behaviour.enabled)
                            continue;

                        try
                        {
                            buildMethod.Invoke(rigBuilder, null);
                            rebuilt++;
                            rebuiltBuilders?.Add(rigBuilder);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.LogWarning(
                                $"[Panic Slide] RigBuilder rebuild failed for player {FormatPlayerForLog(Player)}: {ex.Message}");
                        }
                    }
                }

                if (evaluateRebuiltRigsAtZeroDelta)
                {
                    if (evaluateMethod != null && rebuiltBuilders != null)
                    {
                        for (int i = 0; i < rebuiltBuilders.Count; i++)
                        {
                            Component rigBuilder = rebuiltBuilders[i];
                            if (rigBuilder == null)
                                continue;
                            if (rigBuilder is Behaviour behaviour && !behaviour.enabled)
                                continue;

                            try
                            {
                                evaluateMethod.Invoke(rigBuilder, new object[] { 0f });
                                evaluated++;
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log?.LogWarning(
                                    $"[Panic Slide] RigBuilder zero-delta evaluation failed for player {FormatPlayerForLog(Player)}: {ex.Message}");
                            }
                        }
                    }

                    Plugin.Log?.LogDebug(
                        "[Panic Slide] RigBuilder zero-delta restore evaluation gate: " +
                        $"rebuilt={rebuilt} evaluated={evaluated} " +
                        $"evaluateMethodResolved={evaluateMethod != null} reason='{reason}' " +
                        $"player={FormatPlayerForLog(Player)}.");
                }

                if (!_rigBuilderRebuildLogged)
                {
                    _rigBuilderRebuildLogged = true;
                    Plugin.Log?.LogDebug(
                        $"[Panic Slide] Rebuilt {rebuilt} Animation Rigging RigBuilder(s) after {reason} for player {FormatPlayerForLog(Player)}.");
                }
            }

            private static Type ResolveRigBuilderType()
            {
                if (_rigBuilderReflectionResolved)
                    return _rigBuilderType;

                _rigBuilderReflectionResolved = true;
                _rigBuilderType = Type.GetType("UnityEngine.Animations.Rigging.RigBuilder, Unity.Animation.Rigging");
                if (_rigBuilderType == null)
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < assemblies.Length; i++)
                    {
                        try
                        {
                            _rigBuilderType = assemblies[i].GetType("UnityEngine.Animations.Rigging.RigBuilder", false);
                        }
                        catch
                        {
                            _rigBuilderType = null;
                        }

                        if (_rigBuilderType != null)
                            break;
                    }
                }

                if (_rigBuilderType != null)
                {
                    _rigBuilderBuildMethod = _rigBuilderType.GetMethod(
                        "Build",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);
                    _rigBuilderEvaluateMethod = _rigBuilderType.GetMethod(
                        "Evaluate",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(float) },
                        null);
                }

                return _rigBuilderType;
            }

            private static void AddRigBuilders(Component root, Type rigBuilderType, HashSet<Component> rigBuilders)
            {
                if (root == null || rigBuilderType == null || rigBuilders == null)
                    return;

                try
                {
                    Component parentRigBuilder = root.GetComponentInParent(rigBuilderType);
                    if (parentRigBuilder != null)
                        rigBuilders.Add(parentRigBuilder);
                }
                catch { }

                try
                {
                    Component[] childRigBuilders = root.GetComponentsInChildren(rigBuilderType, true);
                    if (childRigBuilders == null)
                        return;

                    for (int i = 0; i < childRigBuilders.Length; i++)
                    {
                        if (childRigBuilders[i] != null)
                            rigBuilders.Add(childRigBuilders[i]);
                    }
                }
                catch { }
            }

            private void RestoreLayerWeights(Animator animator)
            {
                if (animator == null)
                    return;

                int layerCount = Mathf.Min(animator.layerCount, _savedLayerWeights.Length);
                for (int i = 0; i < layerCount; i++)
                {
                    try { animator.SetLayerWeight(i, _savedLayerWeights[i]); } catch { }
                }
            }

            private void RestoreAnimatorParameters(Animator animator)
            {
                if (animator == null)
                    return;

                for (int i = 0; i < _savedParameters.Count; i++)
                {
                    AnimatorParameterSnapshot parameter = _savedParameters[i];
                    try
                    {
                        switch (parameter.Type)
                        {
                            case AnimatorControllerParameterType.Bool:
                                animator.SetBool(parameter.Hash, parameter.BoolValue);
                                break;
                            case AnimatorControllerParameterType.Float:
                                animator.SetFloat(parameter.Hash, parameter.FloatValue);
                                break;
                            case AnimatorControllerParameterType.Int:
                                animator.SetInteger(parameter.Hash, parameter.IntValue);
                                break;
                        }
                    }
                    catch { }
                }
            }

            private void RestoreAnimatorStates(
                Animator animator,
                AnimatorStateRestoreMode restoreStateMode)
            {
                if (animator == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.staterestore] states_skipped: " +
                        $"mode='{FormatRestoreStateMode(restoreStateMode)}' frame={Time.frameCount} " +
                        "reason='missing_animator'.");
                    return;
                }

                int savedLayerCount = Mathf.Min(_savedStateHashes.Length, _savedStateTimes.Length);
                if (restoreStateMode == AnimatorStateRestoreMode.Fresh)
                {
                    var skippedStates = new List<string>(savedLayerCount);
                    for (int i = 0; i < savedLayerCount; i++)
                    {
                        skippedStates.Add(
                            $"layer={i} hash={_savedStateHashes[i]} savedTime={_savedStateTimes[i]:0.######}");
                    }

                    Plugin.Log?.LogDebug(
                        "[Panic Slide.staterestore] fresh_states_skipped: " +
                        $"frame={Time.frameCount} player={FormatPlayerForLog(Player)} " +
                        $"savedStates=[{string.Join("; ", skippedStates)}].");
                    return;
                }

                int layerCount = Mathf.Min(animator.layerCount, savedLayerCount);
                if (layerCount == 0)
                {
                    Plugin.Log?.LogWarning(
                        "[Panic Slide.staterestore] states_skipped: " +
                        $"mode='{FormatRestoreStateMode(restoreStateMode)}' frame={Time.frameCount} " +
                        $"player={FormatPlayerForLog(Player)} reason='no_common_layers' " +
                        $"animatorLayers={animator.layerCount} savedLayers={savedLayerCount}.");
                    return;
                }

                int restoredLayers = 0;
                for (int i = 0; i < layerCount; i++)
                {
                    if (_savedStateHashes[i] == 0)
                    {
                        Plugin.Log?.LogDebug(
                            "[Panic Slide.staterestore] layer_skipped: " +
                            $"mode='{FormatRestoreStateMode(restoreStateMode)}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} layer={i} hash=0 " +
                            $"savedTime={_savedStateTimes[i]:0.######} reason='missing_saved_hash'.");
                        continue;
                    }

                    try
                    {
                        if (restoreStateMode == AnimatorStateRestoreMode.Crossfade)
                        {
                            animator.CrossFadeInFixedTime(
                                _savedStateHashes[i],
                                AnimatorStateCrossfadeSeconds,
                                i,
                                Mathf.Repeat(_savedStateTimes[i], 1f));
                        }
                        else
                        {
                            animator.Play(_savedStateHashes[i], i, _savedStateTimes[i]);
                        }

                        restoredLayers++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning(
                            "[Panic Slide.staterestore] layer_failed: " +
                            $"mode='{FormatRestoreStateMode(restoreStateMode)}' frame={Time.frameCount} " +
                            $"player={FormatPlayerForLog(Player)} layer={i} hash={_savedStateHashes[i]} " +
                            $"savedTime={_savedStateTimes[i]:0.######} error='{ex.Message}'.");
                    }
                }

                Plugin.Log?.LogDebug(
                    "[Panic Slide.staterestore] states_restored: " +
                    $"mode='{FormatRestoreStateMode(restoreStateMode)}' frame={Time.frameCount} " +
                    $"player={FormatPlayerForLog(Player)} restoredLayers={restoredLayers} " +
                    $"commonLayers={layerCount} animatorLayers={animator.layerCount} savedLayers={savedLayerCount}.");
            }

            private void CacheParameters(Animator animator)
            {
                _parameterTypes.Clear();
                if (animator == null)
                    return;

                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    _parameterTypes[parameter.nameHash] = parameter.type;
                }
            }

            private void SetBool(int hash, bool value)
            {
                if (_animator != null &&
                    _parameterTypes.TryGetValue(hash, out AnimatorControllerParameterType type) &&
                    type == AnimatorControllerParameterType.Bool)
                {
                    _animator.SetBool(hash, value);
                }
            }

            private void SetFloat(int hash, float value)
            {
                if (_animator != null &&
                    _parameterTypes.TryGetValue(hash, out AnimatorControllerParameterType type) &&
                    type == AnimatorControllerParameterType.Float)
                {
                    _animator.SetFloat(hash, value);
                }
            }

            private void FireTrigger(int hash)
            {
                if (_animator != null &&
                    _parameterTypes.TryGetValue(hash, out AnimatorControllerParameterType type) &&
                    type == AnimatorControllerParameterType.Trigger)
                {
                    _animator.ResetTrigger(hash);
                    _animator.SetTrigger(hash);
                }
            }

            private void ResetMovementRuntimeParameters()
            {
                SetBool(ActiveHash, false);
                ResetTrigger(EnterHash);
                ResetTrigger(ExitHash);
                ResetTrigger(Mantle1mHash);
                ResetTrigger(Mantle2mHash);
            }

            private void ResetTrigger(int hash)
            {
                if (_animator != null &&
                    _parameterTypes.TryGetValue(hash, out AnimatorControllerParameterType type) &&
                    type == AnimatorControllerParameterType.Trigger)
                {
                    _animator.ResetTrigger(hash);
                }
            }

            private static string FormatPlayerForLog(PlayerControllerB player)
            {
                if (player == null)
                    return "<null>";

                string username = string.IsNullOrEmpty(player.playerUsername) ? "<unnamed>" : player.playerUsername;
                return "#" + player.playerClientId + " '" + username + "'";
            }

            private readonly struct AnimatorParameterSnapshot
            {
                internal readonly int Hash;
                internal readonly AnimatorControllerParameterType Type;
                internal readonly bool BoolValue;
                internal readonly float FloatValue;
                internal readonly int IntValue;

                private AnimatorParameterSnapshot(
                    int hash,
                    AnimatorControllerParameterType type,
                    bool boolValue,
                    float floatValue,
                    int intValue)
                {
                    Hash = hash;
                    Type = type;
                    BoolValue = boolValue;
                    FloatValue = floatValue;
                    IntValue = intValue;
                }

                internal static AnimatorParameterSnapshot Bool(int hash, bool value) =>
                    new AnimatorParameterSnapshot(hash, AnimatorControllerParameterType.Bool, value, 0f, 0);

                internal static AnimatorParameterSnapshot Float(int hash, float value) =>
                    new AnimatorParameterSnapshot(hash, AnimatorControllerParameterType.Float, false, value, 0);

                internal static AnimatorParameterSnapshot Int(int hash, int value) =>
                    new AnimatorParameterSnapshot(hash, AnimatorControllerParameterType.Int, false, 0f, value);
            }

            private readonly struct CameraPoseSnapshot
            {
                internal SeamTransformPose GameplayCamera { get; }
                internal SeamTransformPose CameraContainer { get; }
                internal Vector3 GameplayCameraPlayerLocalPosition { get; }
                internal bool GameplayCameraPlayerLocalPositionCaptured { get; }
                internal bool HasAnyPose =>
                    GameplayCamera.Captured || CameraContainer.Captured;

                internal CameraPoseSnapshot(
                    SeamTransformPose gameplayCamera,
                    SeamTransformPose cameraContainer,
                    Vector3 gameplayCameraPlayerLocalPosition,
                    bool gameplayCameraPlayerLocalPositionCaptured)
                {
                    GameplayCamera = gameplayCamera;
                    CameraContainer = cameraContainer;
                    GameplayCameraPlayerLocalPosition = gameplayCameraPlayerLocalPosition;
                    GameplayCameraPlayerLocalPositionCaptured =
                        gameplayCameraPlayerLocalPositionCaptured;
                }
            }

            private readonly struct CameraRotationSnapshot
            {
                internal Transform GameplayCameraTransform { get; }
                internal Quaternion GameplayCameraLocalRotation { get; }
                internal bool GameplayCameraCaptured { get; }
                internal Transform CameraContainerTransform { get; }
                internal Quaternion CameraContainerLocalRotation { get; }
                internal bool CameraContainerCaptured { get; }
                internal bool HasAnyRotation => GameplayCameraCaptured || CameraContainerCaptured;

                internal CameraRotationSnapshot(
                    Transform gameplayCameraTransform,
                    Quaternion gameplayCameraLocalRotation,
                    bool gameplayCameraCaptured,
                    Transform cameraContainerTransform,
                    Quaternion cameraContainerLocalRotation,
                    bool cameraContainerCaptured)
                {
                    GameplayCameraTransform = gameplayCameraTransform;
                    GameplayCameraLocalRotation = gameplayCameraLocalRotation;
                    GameplayCameraCaptured = gameplayCameraCaptured;
                    CameraContainerTransform = cameraContainerTransform;
                    CameraContainerLocalRotation = cameraContainerLocalRotation;
                    CameraContainerCaptured = cameraContainerCaptured;
                }
            }

            private readonly struct VisorPoseSnapshot
            {
                internal SeamTransformPose LocalVisor { get; }
                internal SeamTransformPose LocalVisorTargetPoint { get; }
                internal bool HasAnyPose =>
                    LocalVisor.Captured || LocalVisorTargetPoint.Captured;
                internal bool HasAnyRestoreEligiblePose =>
                    (LocalVisor.Captured && LocalVisor.UnderAnimatorHierarchy) ||
                    (LocalVisorTargetPoint.Captured &&
                     LocalVisorTargetPoint.UnderAnimatorHierarchy);

                internal VisorPoseSnapshot(
                    SeamTransformPose localVisor,
                    SeamTransformPose localVisorTargetPoint)
                {
                    LocalVisor = localVisor;
                    LocalVisorTargetPoint = localVisorTargetPoint;
                }
            }

            private readonly struct SeamTransformPose
            {
                internal Transform Transform { get; }
                internal Vector3 LocalPosition { get; }
                internal Quaternion LocalRotation { get; }
                internal Vector3 LocalScale { get; }
                internal Vector3 WorldPosition { get; }
                internal Quaternion WorldRotation { get; }
                internal Vector3 WorldScale { get; }
                internal bool UnderAnimatorHierarchy { get; }
                internal bool Captured { get; }

                internal SeamTransformPose(Transform transform, bool underAnimatorHierarchy)
                {
                    Transform = transform;
                    LocalPosition = transform.localPosition;
                    LocalRotation = transform.localRotation;
                    LocalScale = transform.localScale;
                    WorldPosition = transform.position;
                    WorldRotation = transform.rotation;
                    WorldScale = transform.lossyScale;
                    UnderAnimatorHierarchy = underAnimatorHierarchy;
                    Captured = true;
                }
            }
        }
    }

    // Keeps the final settled slide/mantle viewpoint stable while the restored vanilla animator
    // and RigBuilder evaluate naturally. Rotation remains untouched, so mouse look stays live.
    [DefaultExecutionOrder(32000)]
    internal sealed class ParkourCameraPositionReleaseGuard : MonoBehaviour
    {
        private Transform playerRoot;
        private Transform cameraTransform;
        private Vector3 playerLocalPosition;
        private int releaseLateUpdates = -1;

        internal void Initialize(
            Transform playerRoot,
            Transform cameraTransform,
            Vector3 playerLocalPosition)
        {
            this.playerRoot = playerRoot;
            this.cameraTransform = cameraTransform;
            this.playerLocalPosition = playerLocalPosition;
            ApplyNow();
        }

        internal void ReleaseAfterLateUpdates(int lateUpdates)
        {
            releaseLateUpdates = Mathf.Max(1, lateUpdates);
        }

        private void ApplyNow()
        {
            if (playerRoot != null && cameraTransform != null)
                cameraTransform.position = playerRoot.TransformPoint(playerLocalPosition);
        }

        private void LateUpdate()
        {
            ApplyNow();
            if (releaseLateUpdates < 0)
                return;

            releaseLateUpdates--;
            if (releaseLateUpdates <= 0)
            {
                enabled = false;
                UnityEngine.Object.Destroy(this);
            }
        }
    }

    [DefaultExecutionOrder(32001)]
    internal sealed class ParkourPostRestoreSampler : MonoBehaviour
    {
        private const int RequiredSamples = 3;

        private Animator animator;
        private Transform playerRootTransform;
        private Transform cameraContainerTransform;
        private Transform gameplayCameraTransform;
        private Transform metarigTransform;
        private Transform playerBodyTransform;
        private string playerLabel;
        private int restoreFrame;
        private int lastSampledFrame;
        private int samplesWritten;
        private bool initialized;

        internal void Initialize(
            Animator restoredAnimator,
            Transform playerRoot,
            Transform cameraContainer,
            Transform gameplayCamera,
            Transform metarig,
            Transform playerBody,
            string restoredPlayerLabel)
        {
            animator = restoredAnimator;
            playerRootTransform = playerRoot;
            cameraContainerTransform = cameraContainer;
            gameplayCameraTransform = gameplayCamera;
            metarigTransform = metarig;
            playerBodyTransform = playerBody;
            playerLabel = string.IsNullOrEmpty(restoredPlayerLabel) ? "<unknown>" : restoredPlayerLabel;
            restoreFrame = Time.frameCount;
            initialized = true;
            WriteSample();
        }

        private void LateUpdate()
        {
            if (!initialized || samplesWritten >= RequiredSamples || Time.frameCount <= lastSampledFrame)
                return;

            WriteSample();
        }

        private void WriteSample()
        {
            lastSampledFrame = Time.frameCount;
            int sampleIndex = samplesWritten;
            samplesWritten++;

            Plugin.Log?.LogDebug(
                "[Panic Slide.postrestore] sample: " +
                $"sample={sampleIndex} restoreFrame={restoreFrame} frame={Time.frameCount} player={playerLabel} " +
                $"cameraContainerLocalPosition={DescribeLocalPosition(cameraContainerTransform)} " +
                $"cameraContainerLocalEuler={DescribeLocalEuler(cameraContainerTransform)} " +
                $"gameplayCameraLocalPosition={DescribeLocalPosition(gameplayCameraTransform)} " +
                $"gameplayCameraLocalEuler={DescribeLocalEuler(gameplayCameraTransform)} " +
                $"playerRootWorldPosition={DescribeWorldPosition(playerRootTransform)} " +
                $"metarigLocalPosition={DescribeLocalPosition(metarigTransform)} " +
                $"metarigLocalEuler={DescribeLocalEuler(metarigTransform)} " +
                $"metarigLocalScale={DescribeLocalScale(metarigTransform)} " +
                $"playerBodyWorldYaw={DescribeWorldYaw(playerBodyTransform)} " +
                $"gameplayCameraWorldYaw={DescribeWorldYaw(gameplayCameraTransform)} " +
                $"bodyToCameraYawDelta={DescribeYawDelta(playerBodyTransform, gameplayCameraTransform)} " +
                $"animatorLayers={DescribeAnimatorLayers(animator)}.");

            if (samplesWritten >= RequiredSamples)
            {
                initialized = false;
                enabled = false;
                UnityEngine.Object.Destroy(this);
            }
        }

        private static string DescribeLocalPosition(Transform transform)
        {
            if (transform == null)
                return "<missing>";

            try { return DescribeVector(transform.localPosition); }
            catch (Exception ex) { return "<read_failed:" + ex.GetType().Name + ">"; }
        }

        private static string DescribeLocalEuler(Transform transform)
        {
            if (transform == null)
                return "<missing>";

            try { return DescribeVector(transform.localEulerAngles); }
            catch (Exception ex) { return "<read_failed:" + ex.GetType().Name + ">"; }
        }

        private static string DescribeLocalScale(Transform transform)
        {
            if (transform == null)
                return "<missing>";

            try { return DescribeVector(transform.localScale); }
            catch (Exception ex) { return "<read_failed:" + ex.GetType().Name + ">"; }
        }

        private static string DescribeWorldPosition(Transform transform)
        {
            if (transform == null)
                return "<missing>";

            try { return DescribeVector(transform.position); }
            catch (Exception ex) { return "<read_failed:" + ex.GetType().Name + ">"; }
        }

        private static string DescribeWorldYaw(Transform transform)
        {
            if (transform == null)
                return "<missing>";

            try { return transform.eulerAngles.y.ToString("0.######"); }
            catch (Exception ex) { return "<read_failed:" + ex.GetType().Name + ">"; }
        }

        private static string DescribeYawDelta(Transform from, Transform to)
        {
            if (from == null || to == null)
                return "<missing>";

            try { return Mathf.DeltaAngle(from.eulerAngles.y, to.eulerAngles.y).ToString("0.######"); }
            catch (Exception ex) { return "<read_failed:" + ex.GetType().Name + ">"; }
        }

        private static string DescribeAnimatorLayers(Animator restoredAnimator)
        {
            if (restoredAnimator == null)
                return "<missing>";

            try
            {
                int layerCount = Mathf.Max(0, restoredAnimator.layerCount);
                var layers = new List<string>(layerCount);
                for (int i = 0; i < layerCount; i++)
                {
                    try
                    {
                        AnimatorStateInfo state = restoredAnimator.GetCurrentAnimatorStateInfo(i);
                        layers.Add(
                            $"layer={i} hash={state.fullPathHash} normalizedTime={state.normalizedTime:0.######}");
                    }
                    catch (Exception ex)
                    {
                        layers.Add($"layer={i} read_failed={ex.GetType().Name}");
                    }
                }

                return "[" + string.Join("; ", layers) + "]";
            }
            catch (Exception ex)
            {
                return "<read_failed:" + ex.GetType().Name + ">";
            }
        }

        private static string DescribeVector(Vector3 value)
        {
            return $"({value.x:0.######},{value.y:0.######},{value.z:0.######})";
        }
    }
}
