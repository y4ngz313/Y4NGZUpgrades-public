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
    [HarmonyPatch]
    internal static partial class CourierDronePatch
    {
        private const string MSG_COURIER_REQUEST = "TechnicianCourier_Request";
        private const string MSG_COURIER_COMMAND = "TechnicianCourier_Command";
        private const string MSG_COURIER_STATE = "TechnicianCourier_State";
        private const string MSG_COURIER_DEATH = "TechnicianCourier_Death";
        private const string MSG_COURIER_HIT = "TechnicianCourier_Hit";
        private const string MSG_COURIER_FLAME = "TechnicianCourier_Flame";
        private const string MSG_COURIER_GRENADE_LAUNCH = "TechnicianCourier_GrenadeLaunch";
        private const string MSG_COURIER_GRENADE_EXPLODE = "TechnicianCourier_GrenadeExplode";
        private const string MSG_COURIER_MANUAL_POSE = "TechnicianCourier_ManualPose";
        private const string MSG_COURIER_MANUAL_PICKUP = "TechnicianCourier_ManualPickup";
        private const string MSG_COURIER_MANUAL_FIRE = "TechnicianCourier_ManualFire";
        private const string MSG_COURIER_POINTER_COMMAND = "TechnicianCourier_PointerCommand";
        private const string MSG_COURIER_ENEMY_ATTACK = "TechnicianCourier_EnemyAttack";

        private const int DRONE_FLAME_HIT_ID = 74043;
        private const int DRONE_MAX_HEALTH = 30;
        private const int DRONE_RETREAT_HEALTH = 10;
        private const float DRONE_RETREAT_SECONDS = 6f;
        private const float DRONE_PROXIMITY_AGGRO_RADIUS = 5f;
        private const float DRONE_ENEMY_FACE_RANGE_MULTIPLIER = 1.5f;
        private const float DRONE_ENEMY_FACE_TURN_DEGREES_PER_SECOND = 360f;
        // #500 eyeless dog: a noise guess this close to the drone means the dog is hunting the drone.
        private const float DOG_DRONE_GUESS_RADIUS = 1.5f;
        private const float DOG_DRONE_ENRAGE_INTERVAL = 1f;
        private const float DOG_DRONE_LUNGE_MAX_ANGLE = 60f;
        // #500: vanilla MouthDogAI.OnCollideWithPlayer turns the dog to the player and then calls
        // EnterLunge (MouthDogAI.cs:615-657). A lunge with a live player this close is that one.
        private const float DOG_PLAYER_LUNGE_RADIUS = 3f;
        // #500, deliberate deviation: vanilla clears 'killing', sets 'carryingBody' and then waits 0.65 s
        // (FlowermanAI.cs:773-775). The drone kill carries no body, so 'killing' is held for those 0.65 s.
        private const float FLOWERMAN_DRONE_KILL_SECONDS = 0.65f;
        private const float DRONE_PROXIMITY_THREAT_SECONDS = 2.5f;
        private const float DRONE_ATTACK_THREAT_SECONDS = 8f;
        private const float DRONE_DEATH_VISUAL_SECONDS = 5f;
        private const float DRONE_HIT_JOLT_DISTANCE = 0.12f;
        private const float DRONE_HIT_JOLT_SECONDS = 0.25f;
        private const float DRONE_ATTACK_WARNING_INTERVAL = 5f;
        private const float ENEMY_SCAN_INTERVAL = 0.25f;
        private const float FLAME_TRIGGER_RANGE = 3.5f;
        private const float FLAME_DAMAGE_RADIUS = 3.2f;
        private const float FLAME_DAMAGE_WINDOW_SECONDS = 1.2f;
        // #500 perf: Debug-only timing buckets, split into idle and combat frames.
        private const int PERF_BUCKET_SCAN = 0;
        private const int PERF_BUCKET_COMBAT = 1;
        private const int PERF_BUCKET_POINTER = 2;
        private const int PERF_BUCKET_MANUAL = 3;
        private const int PERF_BUCKET_FLAME = 4;
        private const int PERF_BUCKET_COUNT = 5;
        private const int PERF_STATE_IDLE = 0;
        private const int PERF_STATE_COMBAT = 1;
        private const int PERF_STATE_COUNT = 2;
        private const float DRONE_PERF_WINDOW_SECONDS = 10f;
        private const float DRONE_COMBAT_ACTIVE_SECONDS = 3f;
        private const float GRENADE_MIN_RANGE = 8f;
        private const float GRENADE_MAX_RANGE = 18f;
        private const float GRENADE_COOLDOWN = 8f;
        private const float GRENADE_KILL_RANGE = 3f;
        private const float GRENADE_DAMAGE_RANGE = 5.5f;
        private const float GRENADE_SAFETY_MARGIN = 2f;
        private const float GRENADE_FUSE_SECONDS = 2.5f;
        private const float GRENADE_LOB_SPEED = 15.5f;
        private const float GRENADE_PROJECTILE_RADIUS = 0.18f;
        private const float MANUAL_DRONE_BOOT_SCREEN_SECONDS = 1.0f;
        private const float MANUAL_MOVE_SPEED = 6.2f;
        private const float MANUAL_VERTICAL_SPEED = 3.8f;
        private const float MANUAL_MOUSE_SENSITIVITY = 0.075f;
        private const float MANUAL_COLLISION_RADIUS = 0.34f;
        private const float MANUAL_CAMERA_CLEARANCE = 0.28f;
        private const float MANUAL_MIN_GROUND_HEIGHT = 0.88f;
        private const float MANUAL_MAX_GROUND_HEIGHT = 5.25f;
        private const float MANUAL_PICKUP_RADIUS = 4.2f;
        private const float MANUAL_POSE_SYNC_INTERVAL = 0.08f;
        private const float MANUAL_POSE_TIMEOUT_SECONDS = 2f;
        private const float MANUAL_PICKUP_LOCK_SECONDS = 1.25f;
        private const float LATE_JOIN_STATE_DELAY_SECONDS = 3f;
        private const float POINTER_COMMAND_RANGE = 45f;
        private const int POINTER_RAYCAST_BUFFER = 16;
        private const float POINTER_LABEL_REFRESH_SECONDS = 1f;
        private const float POINTER_RESOLVE_INTERVAL = 0.05f;
        private const float POINTER_COMMAND_COOLDOWN_SECONDS = 1.5f;
        private const float POINTER_PRIORITY_SECONDS = 20f;
        private const float PRIORITY_TARGET_UNREACHABLE_SECONDS = 8f;
        private const float POINTER_FETCH_TIMEOUT_SECONDS = 25f;
        private const float VISUAL_FLIGHT_HEIGHT = 0.95f;
        private const float VISUAL_SPEED = 10.5f;
        private const float VISUAL_TURN_SPEED = 7.5f;
        private const float VISUAL_FORWARD_TILT = 10f;
        private const float VISUAL_MAX_BANK = 13f;
        private const float ENTRANCE_SCAN_SECONDS = 0.75f;
        private const float ENTRANCE_WAIT_SCAN_INTERVAL = 0.8f;
        private const float GUARD_PICKUP_RADIUS = 6f;
        private const float GUARD_PICKUP_SCAN_INTERVAL = 0.8f;
        private const float MAGNET_PICKUP_LOWER_HEIGHT = 0.78f;
        private const float MAGNET_PICKUP_LIFT_SECONDS = 0.55f;
        private const float NAVMESH_SAMPLE_RADIUS = 8f;
        private const float FOLLOW_SPEED = 6.8f;
        private const float FOLLOW_CATCHUP_DISTANCE = 8.5f;
        private const float FOLLOW_WARP_DISTANCE = 28f;
        private const float FOLLOW_SETTLE_DISTANCE = 1.35f;
        private const float FOLLOW_RECHASE_DISTANCE = 3.4f;
        private const float FOLLOW_RING_RADIUS = 2.3f;
        private const float FOLLOW_DIRECT_MAX_DISTANCE = 4f;
        private const float FOLLOW_COLLISION_RADIUS = 0.3f;
        private const float FOLLOW_WALL_STOP_SHORT = 0.35f;
        private const float FOLLOW_MOVE_SMOOTH_TIME = 0.28f;
        private const float FOLLOW_LOOKAHEAD_DISTANCE = 8f;
        private const float COLLISION_REPATH_INTERVAL = 0.6f;
        private const float HOVER_CLEARANCE_PROBE_INTERVAL = 0.2f;
        private const float HOVER_PROBE_TOP_HEIGHT = 2.2f;
        private const float HOVER_MAX_CLEARANCE_HEIGHT = 1.9f;
        private const float HOVER_MIN_CLEARANCE_HEIGHT = 0.55f;
        private const float HOVER_OBSTACLE_CLEARANCE = 0.22f;
        private const float STUCK_RESCUE_WINDOW_SECONDS = 3f;
        private const float STUCK_RESCUE_MIN_PROGRESS = 0.3f;
        private const float STUCK_RESCUE_OWNER_DISTANCE = 6f;
        private const float NAVMESH_REPATH_INTERVAL = 0.5f;
        private const float NAVMESH_REPATH_TARGET_MOVE_DISTANCE = 2f;
        private const float GROUND_VERTICAL_SMOOTH_SPEED = 3f;
        private const float CARRY_DANGLE_HEIGHT = 0.55f;
        private const float CARRY_FLOOR_CLEARANCE = 0.15f;
        private const float IDLE_SWAY_BOB_AMPLITUDE = 0.05f;
        private const float IDLE_SWAY_BOB_FREQUENCY = 1.2f;
        private const float IDLE_SWAY_DRIFT_AMPLITUDE = 0.08f;
        private const float IDLE_SWAY_DRIFT_FREQUENCY = 0.4f;
        private const float IDLE_SWAY_YAW_SPEED = 1.8f;
        private const float SHIP_DRONE_EDGE_MARGIN = 0.75f;

        private static readonly Color PointerEnemyColor = new Color(1f, 0.18f, 0.16f, 0.95f);
        private static readonly Color PointerItemColor = new Color(1f, 0.70f, 0.26f, 0.95f);

        private static readonly List<CourierItemMove> PlannedItems = new List<CourierItemMove>();
        private static readonly List<CarriedCourierItem> CarriedItems = new List<CarriedCourierItem>();
        private static readonly Dictionary<ulong, float> DroneThreats = new Dictionary<ulong, float>();
        // F-DRONE-7: only enemies the drone actually attacked (or that a pointer order named) may be
        // pulled off the crew; bare proximity no longer counts.
        private static readonly Dictionary<ulong, float> DroneAttackThreats = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, float> NextDroneHitByEnemy = new Dictionary<ulong, float>();
        // #500 eyeless dog: next time the host may re-aim each dog at the drone.
        private static readonly Dictionary<ulong, float> NextDogEnrageAtByEnemy = new Dictionary<ulong, float>();
        // #500 eyeless dog: the exact drone position the host last enraged each dog toward. While the
        // dog's lastHeardNoisePosition still equals it, the dog's hunt is that enrage.
        private static readonly Dictionary<ulong, Vector3> DogDroneEnrageTargetByEnemy = new Dictionary<ulong, Vector3>();
        // #500: one warning per enemy type, message only, for the per-hit attack and flame paths.
        private static readonly HashSet<string> DroneAttackWarnedEnemyTypes = new HashSet<string>();
        private static readonly HashSet<string> FlameHitWarnedEnemyTypes = new HashSet<string>();
        // #500: clients already warned about a refused Courier message this session.
        private static readonly HashSet<ulong> RefusedCourierMessageSenders = new HashSet<ulong>();
        // #500: reach (m) / cooldown (s) / drone damage of each supported enemy's attack on the drone,
        // indexed by DroneAttacker.
        private static readonly DroneAttackProfile[] DroneAttackProfiles =
        {
            default,                                // None
            new DroneAttackProfile(2.4f, 1.2f, 6),  // SandSpider
            new DroneAttackProfile(2.6f, 1.0f, 5),  // Crawler
            new DroneAttackProfile(1.8f, 0.8f, 3),  // HoarderBug
            new DroneAttackProfile(2.0f, 0.9f, 3),  // Baboon
            new DroneAttackProfile(2.0f, 1.5f, 8),  // Nutcracker (kick)
            new DroneAttackProfile(2.0f, 3.0f, 10), // Flowerman
            new DroneAttackProfile(4.5f, 2.0f, 10), // MouthDog (lunge)
        };
        private static readonly Dictionary<ulong, GrabbableObject> KnownGrabbableItems = new Dictionary<ulong, GrabbableObject>();
        private static readonly Dictionary<int, GameObject> ActiveGrenadeVisuals = new Dictionary<int, GameObject>();
        private static readonly List<Vector3> CachedFollowPath = new List<Vector3>();
        // F-DRONE-9: the pointer raycast runs every frame from PlayerControllerB.Update; a fixed
        // buffer plus RaycastNonAlloc replaces a fresh RaycastHit[] and a Comparison delegate.
        private static readonly RaycastHit[] PointerRaycastHits = new RaycastHit[POINTER_RAYCAST_BUFFER];
        // #500: pointer hits whose name contains "FieldTablet", by Transform instance id, so the
        // per-frame pointer ray stops allocating a name string per hit. Cleared in ResetDroneState.
        private static readonly Dictionary<int, bool> PointerTabletTransformCache = new Dictionary<int, bool>();
        // #500 perf: the pointer ray and its hit processing run at most once per
        // POINTER_RESOLVE_INTERVAL of unscaled time; between runs the reticle shows this target.
        private static PointerTarget _pointerCachedTarget;
        private static float _nextPointerResolveAt;
        // #500 perf: the pointer layer mask, rebuilt only when StartOfRound.Instance changes.
        private static int _pointerRaycastMask;
        private static bool _pointerRaycastMaskValid;
        private static StartOfRound _pointerRaycastMaskRound;
        // #500 perf: the renderer ResolveEnemyCenter reads bounds from, by EnemyAI instance id, so
        // the skinned/mesh renderer walk runs once per enemy. Cleared in ResetDroneState.
        private static readonly Dictionary<int, Renderer> EnemyCenterRenderers = new Dictionary<int, Renderer>();
        // #500 perf: reusable buffers for the guard and entrance scrap sweeps, valid only during one call.
        private static readonly List<Vector3> GuardPickupSearchPositions = new List<Vector3>();
        private static readonly List<EntranceScrapCandidate> EntranceScrapCandidates = new List<EntranceScrapCandidate>();
        private static readonly IComparer<EntranceScrapCandidate> EntranceScrapDistanceComparer =
            Comparer<EntranceScrapCandidate>.Create((a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));
        // #500 perf: EntranceTeleports for FindMainEntranceRoute, per round. Cleared in ResetDroneState.
        private static EntranceTeleport[] _cachedMainEntrances;
        private static StartOfRound _cachedMainEntranceRound;
        // #500 perf: the carry anchor of the current _droneVisual. Cleared in ClearDroneRendererCache.
        private static GameObject _carryAnchorCacheRoot;
        private static Transform _carryAnchorCache;
        // #500 perf: ticks per [state * PERF_BUCKET_COUNT + bucket] and frames per state, this window.
        private static readonly long[] DronePerfTicks = new long[PERF_STATE_COUNT * PERF_BUCKET_COUNT];
        private static readonly int[] DronePerfFrames = new int[PERF_STATE_COUNT];
        private static readonly string[] DronePerfBucketNames = { "scan", "combat", "pointer", "manual", "flame" };

        private static bool _handlersRegistered;
        private static float _commandCooldownEnd;
        private static DroneMode _mode = DroneMode.NotSpawned;
        private static GameObject _droneVisual;
        private static Vector3 _droneGroundPosition;
        private static bool _droneIsInside;
        private static bool _isFollowingPlayer;
        private static bool _reportedNoEntranceScrap;
        private static int _ownerPlayerId = -1;
        private static int _routeVersion;
        private static int _droneHealth = DRONE_MAX_HEALTH;
        private static bool _droneDestroyedThisRound;
        private static int _ownerCourierTier;
        private static float _nextEnemyScanAt;
        private static int _lastEnemyScanFrame = -1;
        private static float _nextFlameAt;
        private static float _nextGrenadeAt;
        private static float _combatLandedUntil;
        private static int _nextGrenadeId = 1;
        // #500: while above zero, Landmine.SpawnExplosion is running for a drone grenade and
        // PreEnemyHitEnemyOnLocalClient swaps vanilla's force 6 for _droneGrenadeForceOverride.
        private static int _droneGrenadeExplosionScope;
        private static int _droneGrenadeForceOverride;
        private static bool _droneGrenadeExplosionWarned;
        // #500 perf: combat frames run until this time; bumped by threats, fire, attacks and hits.
        private static float _droneCombatActiveUntil;
        private static bool _dronePerfEnabled;
        private static bool _dronePerfTiming;
        private static int _dronePerfStateOffset;
        private static int _dronePerfLastFrame = -1;
        private static float _dronePerfWindowStartedAt;
        private static float _dronePerfWindowEndsAt;
        private static float _nextEntranceScrapScanAt;
        private static float _nextGuardScrapScanAt;
        private static bool _flashlightEnabled;
        private static bool _followSettled;
        private static Vector3 _cachedFollowPathTarget;
        private static float _nextFollowRepathAt;
        private static bool _lastRouteMoveBlocked;
        private static float _nextCollisionRepathAt;
        private static Vector3 _followSmoothVelocity;
        private static float _nextHoverProbeAt;
        private static float _dynamicHoverHeight = VISUAL_FLIGHT_HEIGHT;
        private static Vector3 _stuckWindowStartGround;
        private static float _stuckWindowStartAt;
        private static bool _stuckRepathIssued;
        private static bool _stuckRescueActive;
        private static GameObject _manualPlayerRendererCacheRoot;
        private static Renderer[] _manualPlayerRendererCache;
        private static GameObject _droneRendererCacheRoot;
        private static Renderer[] _droneRendererCache;
        private static bool _manualControlActive;
        private static bool _manualControlCameraActivated;
        private static int _manualControlPlayerId = -1;
        private static DroneMode _manualPreviousMode = DroneMode.NotSpawned;
        private static GameObject _manualCameraObject;
        private static Camera _manualCamera;
        private static GameObject _manualHudRoot;
        private static Text _manualHudStatusText;
        private static Text _manualHudCargoText;
        private static Text _manualHudTelemetryText;
        private static Text _manualHudControlsText;
        private static GameObject _manualHudStartupRoot;
        private static Text _manualHudStartupText;
        private static Image _manualHudStartupProgressImage;
        private static bool _restoreMoveInput;
        private static bool _restoreLookInput;
        private static bool _restoreInteractInput;
        private static bool _restoreGameplayCameraEnabled;
        private static bool _manualSavedInputState;
        private static bool _manualSavedPresentationState;
        private static readonly List<RendererPresentationState> ManualRendererStates = new List<RendererPresentationState>();
        private static bool _restoreBodyModelEnabled;
        private static bool _restoreBodyModelLod1Enabled;
        private static bool _restoreBodyModelLod2Enabled;
        private static bool _restoreBodyModelArmsEnabled;
        private static UnityEngine.Rendering.ShadowCastingMode _restoreBodyShadowMode;
        private static UnityEngine.Rendering.ShadowCastingMode _restoreBodyLod1ShadowMode;
        private static UnityEngine.Rendering.ShadowCastingMode _restoreBodyLod2ShadowMode;
        private static bool _restoreLocalArmsMatchCamera;
        private static bool _restoreAnimatorGrab;
        private static bool _restoreAnimatorGrabValidated;
        private static bool _manualSavedAnimatorFlags;
        private static float _manualDroneStartupUntil;
        private static float _manualYaw;
        private static float _manualPitch;
        private static float _manualNextPoseSyncAt;
        private static float _manualPickupLockedUntil;
        private static bool _magnetPickupActive;
        private static float _nextPointerCommandAt;
        private static float _nextDroneAttackWarningAt;
        private static ulong _priorityTargetId;
        private static float _priorityTargetExpiresAt;
        private static float _priorityTargetUnreachableSince;
        private static DroneMode _pointerPreviousMode = DroneMode.NotSpawned;
        private static float _retreatUntil;
        private static int _lastManualWatchdogFrame = -1;
        private static float _lastManualPoseAt;
        private static AudioListener _manualAudioListener;
        private static AudioListener _savedAudioListener;
        private static bool _savedAudioListenerEnabled;
        private static float _droneForwardExtent = -1f;
        private static float _nextCarriedGroundProbeAt;
        private static float _carriedGroundY;
        private static bool _carriedGroundValid;
        private static float _nextPointerLabelRefreshAt;
        private static string _pointerAttackLabel;
        private static string _pointerFetchLabel;
        private static string _manualHudCargoCache;
        private static string _manualHudTelemetryCache;
        private static int _manualHudCargoCount = int.MinValue;
        private static int _manualHudCargoPoundsTenths = int.MinValue;
        private static int _manualHudHealth = int.MinValue;
        private static int _manualHudAltitudeTenths = int.MinValue;
        private static int _manualHudInsideFlag = int.MinValue;
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
            _commandCooldownEnd = 0f;
            StopManualControl(resumeDrone: false, notifyOwner: false);
            ResetDroneHealthForRound();
            ResetDroneState(destroyVisual: true);
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
StopManualControl(resumeDrone: false, notifyOwner: false);
            ResetDroneState(destroyVisual: true);
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            StopManualControl(resumeDrone: false, notifyOwner: false);
            UnsubscribeNetworkLifecycleCallbacks();
            _handlersRegistered = false;
            RefusedCourierMessageSenders.Clear();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (_dronePerfLastFrame != Time.frameCount)
                BeginDronePerfFrame();

            UpdateEnemyDroneAggroScanOncePerFrame();
            bool timing = _dronePerfTiming;
            long started = timing ? Stopwatch.GetTimestamp() : 0L;
            TickManualControlWatchdogOncePerFrame();
            if (timing)
                AddDronePerfTicks(PERF_BUCKET_MANUAL, started);

            if (!IsLocalPlayer(__instance))
                return;

            if (timing)
                started = Stopwatch.GetTimestamp();
            UpdatePointerTargeting(__instance);
            if (timing)
                AddDronePerfTicks(PERF_BUCKET_POINTER, started);

            if (_manualControlActive)
            {
                if (timing)
                    started = Stopwatch.GetTimestamp();
                TickManualControl(__instance);
                if (timing)
                    AddDronePerfTicks(PERF_BUCKET_MANUAL, started);
            }
        }


        // Field Operations tablet drone view. Returns true while a live drone exists.
        internal static bool TryGetTabletTelemetry(
            out Vector3 position,
            out float healthNormalized,
            out float cargoNormalized,
            out int cargoCount,
            out string status,
            out bool droneInside)
        {
            bool hasDrone = _droneVisual != null && !_droneDestroyedThisRound;
            position = _droneVisual != null ? _droneVisual.transform.position : Vector3.zero;
            healthNormalized = _droneDestroyedThisRound
                ? 0f
                : Mathf.Clamp01(_droneHealth / (float)DRONE_MAX_HEALTH);
            cargoNormalized = Mathf.Clamp01(GetCarriedCargoWeightPounds() / CourierDroneUpgrade.MAX_CARGO_WEIGHT_LB);
            cargoCount = CarriedItems.Count;
            status = GetDroneStatusText();
            droneInside = _droneIsInside;
            return hasDrone;
        }

        internal static string GetTabletActiveModeName()
        {
            if (_droneDestroyedThisRound)
                return "DESTROYED";

            DroneMode effectiveMode = _droneVisual == null ? DroneMode.NotSpawned : _mode;
            switch (effectiveMode)
            {
                case DroneMode.NotSpawned:
                    return "OFFLINE";
                case DroneMode.IdleAtPlayer:
                    return "IDLE";
                case DroneMode.GuardingPlayer:
                    return "GUARDING PLAYER";
                case DroneMode.FlyingToEntrance:
                    return "ENTRANCE SWEEP";
                case DroneMode.AtEntrance:
                    return "AT ENTRANCE";
                case DroneMode.ReturningToShip:
                    return "RETURNING TO SHIP";
                case DroneMode.FlyingToPlayer:
                    return "DISMISSING";
                case DroneMode.ManualControl:
                    return "PILOT LINK";
                case DroneMode.PointerAttack:
                    return "POINTER ATTACK";
                case DroneMode.PointerFetch:
                    return "POINTER FETCH";
                default:
                    return "READY";
            }
        }

        private static string FormatCargoWeight(float pounds)
        {
            return $"{Mathf.RoundToInt(pounds)}/{Mathf.RoundToInt(CourierDroneUpgrade.MAX_CARGO_WEIGHT_LB)} lb";
        }

        private static float GetItemWeightPounds(GrabbableObject item)
        {
            if (item == null || item.itemProperties == null)
                return 0f;

            return Mathf.Max(0f, item.itemProperties.weight - 1f) * 105f;
        }

        private static float GetCarriedCargoWeightPounds()
        {
            float total = 0f;
            for (int i = 0; i < CarriedItems.Count; i++)
                total += GetItemWeightPounds(CarriedItems[i].Item);

            return total;
        }

        private static float GetPlannedCargoWeightPounds()
        {
            float total = 0f;
            for (int i = 0; i < PlannedItems.Count; i++)
                total += GetItemWeightPounds(ResolveItem(PlannedItems[i].NetworkObjectId));

            return total;
        }

        private static float GetReservedCargoWeightPounds()
        {
            return GetCarriedCargoWeightPounds() + GetPlannedCargoWeightPounds();
        }

        private static bool WouldExceedCargoWeight(GrabbableObject item, float existingWeightLb)
        {
            return existingWeightLb + GetItemWeightPounds(item) > CourierDroneUpgrade.MAX_CARGO_WEIGHT_LB + 0.01f;
        }

        private static bool IsCargoWeightFull()
        {
            return GetCarriedCargoWeightPounds() >= CourierDroneUpgrade.MAX_CARGO_WEIGHT_LB - 0.01f;
        }

        private static string GetDroneStatusText()
        {
            if (_droneDestroyedThisRound)
                return "DESTROYED";

            DroneMode effectiveMode = _droneVisual == null ? DroneMode.NotSpawned : _mode;
            switch (effectiveMode)
            {
                case DroneMode.NotSpawned:
                    return "OFFLINE";
                case DroneMode.IdleAtPlayer:
                    if (CarriedItems.Count > 0)
                        return "HOLDING";
                    return _isFollowingPlayer ? "FOLLOWING" : "WAITING";
                case DroneMode.GuardingPlayer:
                    return "GUARDING";
                case DroneMode.FlyingToEntrance:
                    return "OUTBOUND";
                case DroneMode.AtEntrance:
                    return CarriedItems.Count > 0 ? "CARGO READY" : "SCANNING";
                case DroneMode.ReturningToShip:
                    return "RETURNING TO SHIP";
                case DroneMode.FlyingToPlayer:
                    return "RETURNING";
                case DroneMode.ManualControl:
                    return _manualControlCameraActivated ? "MANUAL" : "LINKING";
                case DroneMode.PointerAttack:
                    return "ENGAGING";
                case DroneMode.PointerFetch:
                    return "FETCHING";
                default:
                    return "READY";
            }
        }

        private static bool IsBusyMode(DroneMode mode)
        {
            return mode == DroneMode.FlyingToEntrance
                || mode == DroneMode.ReturningToShip
                || mode == DroneMode.FlyingToPlayer
                || mode == DroneMode.ManualControl
                || mode == DroneMode.PointerAttack
                || mode == DroneMode.PointerFetch;
        }

        internal static bool IsLocalPilotModeActive => _manualControlActive;

        internal static void StopLocalPilotMode(bool notifyOwner)
        {
            StopManualControl(resumeDrone: true, notifyOwner: notifyOwner);
        }

        internal static int GetTabletCommandEntries(PlayerControllerB player, TabletCommandEntry[] entries)
        {
            if (entries == null || entries.Length == 0)
                return 0;

            int count = 0;
            AddTabletCommandEntry(player, entries, ref count, ResolveContextualSummonDismissType());
            AddTabletCommandEntry(player, entries, ref count, CourierCommandType.Guard);
            AddTabletCommandEntry(player, entries, ref count, CourierCommandType.FlyToEntrance);
            AddTabletCommandEntry(player, entries, ref count, CourierCommandType.ReturnToShip);
            AddTabletCommandEntry(player, entries, ref count, CourierCommandType.Flashlight);
            if (CourierDroneUpgrade.GetTier() >= 3)
                AddTabletCommandEntry(player, entries, ref count, CourierCommandType.Pilot);
            return count;
        }

        internal static bool TryExecuteTabletCommand(PlayerControllerB player, CourierCommandType commandType)
        {
            if (!IsTabletCommandAvailable(commandType, player, out string reason))
            {
                if (player != null)
                    DisplayOwnerTip((int)player.playerClientId, reason, isWarning: true);
                return false;
            }

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient)
            {
                CourierCommand command = BuildCommand(player, commandType);
                ApplyCommand(command);
                return command != null && command.Type != CourierCommandType.None;
            }

            if (NetworkManager.Singleton.IsServer)
            {
                CourierCommand command = BuildCommand(player, commandType);
                ApplyCommand(command);
                BroadcastCommand(command);
                return command != null && command.Type != CourierCommandType.None;
            }

            SendRequest(commandType);
            return true;
        }

        private static void AddTabletCommandEntry(PlayerControllerB player, TabletCommandEntry[] entries, ref int count, CourierCommandType commandType)
        {
            if (count >= entries.Length)
                return;

            bool available = IsTabletCommandAvailable(commandType, player, out string reason);
            entries[count++] = new TabletCommandEntry(commandType, GetTabletCommandLabel(commandType), available, available ? string.Empty : reason);
        }

        private static CourierCommandType ResolveContextualSummonDismissType()
        {
            DroneMode effectiveMode = _droneVisual == null ? DroneMode.NotSpawned : _mode;
            return effectiveMode == DroneMode.NotSpawned ? CourierCommandType.Spawn : CourierCommandType.Dismiss;
        }

        private static string GetTabletCommandLabel(CourierCommandType commandType)
        {
            switch (commandType)
            {
                case CourierCommandType.Spawn:
                    return "SUMMON";
                case CourierCommandType.Dismiss:
                    return "DISMISS";
                case CourierCommandType.Guard:
                    return "GUARD ME";
                case CourierCommandType.FlyToEntrance:
                    return "ENTRANCE SWEEP";
                case CourierCommandType.ReturnToShip:
                    return "RETURN TO SHIP";
                case CourierCommandType.Flashlight:
                    return _flashlightEnabled ? "FLASHLIGHT OFF" : "FLASHLIGHT ON";
                case CourierCommandType.Pilot:
                    return "PILOT";
                default:
                    return "COMMAND";
            }
        }

        private static bool IsTabletCommandAvailable(CourierCommandType commandType, PlayerControllerB player, out string reason)
        {
            reason = string.Empty;
            if (commandType == CourierCommandType.None)
            {
                reason = "NO COMMAND";
                return false;
            }
            if (commandType == CourierCommandType.Pilot)
            {
                if (CourierDroneUpgrade.GetTier() < 3)
                {
                    reason = "PILOT LOCKED";
                    return false;
                }
            }
            if (!CourierDroneUpgrade.IsUnlocked())
            {
                reason = "DRONE LOCKED";
                return false;
            }
            if (player == null || !CanUse(player))
            {
                reason = "PLAYER BUSY";
                return false;
            }
            if (_droneDestroyedThisRound)
            {
                reason = "NEXT ROUND";
                return false;
            }
            if (Time.time < _commandCooldownEnd)
            {
                reason = $"LOCK {Mathf.CeilToInt(_commandCooldownEnd - Time.time)}S";
                return false;
            }

            DroneMode effectiveMode = _droneVisual == null ? DroneMode.NotSpawned : _mode;
            bool hasDrone = _droneVisual != null && effectiveMode != DroneMode.NotSpawned;
            switch (commandType)
            {
                case CourierCommandType.Spawn:
                    if (hasDrone)
                    {
                        reason = "DEPLOYED";
                        return false;
                    }
                    return true;

                case CourierCommandType.Dismiss:
                    if (!hasDrone)
                    {
                        reason = "NO DRONE";
                        return false;
                    }
                    return true;

                case CourierCommandType.Guard:
                    if (!hasDrone)
                    {
                        reason = "NO DRONE";
                        return false;
                    }
                    if (effectiveMode == DroneMode.GuardingPlayer)
                    {
                        reason = "ACTIVE";
                        return false;
                    }
                    if (IsBusyMode(effectiveMode))
                    {
                        reason = "BUSY";
                        return false;
                    }
                    return true;

                case CourierCommandType.FlyToEntrance:
                    if (!hasDrone)
                    {
                        reason = "NO DRONE";
                        return false;
                    }
                    if (effectiveMode == DroneMode.AtEntrance)
                    {
                        reason = "AT ENTRANCE";
                        return false;
                    }
                    if (IsBusyMode(effectiveMode))
                    {
                        reason = "BUSY";
                        return false;
                    }
                    return true;

                case CourierCommandType.ReturnToShip:
                    if (!hasDrone)
                    {
                        reason = "NO DRONE";
                        return false;
                    }
                    if (IsBusyMode(effectiveMode))
                    {
                        reason = "BUSY";
                        return false;
                    }
                    return true;

                case CourierCommandType.Flashlight:
                    if (!hasDrone)
                    {
                        reason = "NO DRONE";
                        return false;
                    }
                    return true;

                case CourierCommandType.Pilot:
                    if (!hasDrone)
                    {
                        reason = "NO DRONE";
                        return false;
                    }
                    if (effectiveMode == DroneMode.ManualControl)
                    {
                        reason = "ACTIVE";
                        return false;
                    }
                    if (IsBusyMode(effectiveMode))
                    {
                        reason = "BUSY";
                        return false;
                    }
                    if (_magnetPickupActive || Time.time < _manualPickupLockedUntil)
                    {
                        reason = "MAGNET BUSY";
                        return false;
                    }
                    return true;
            }

            reason = "UNAVAILABLE";
            return false;
        }


        private static void HandleManualDroneActions(PlayerControllerB player)
        {
            Gui.IngameKeybinds keybinds = Gui.Plugin.Keybinds;
            if (Gui.UpgradeInput.WasPressed(keybinds?.DronePilotFlame))
                TryRequestManualFire(player, ManualFireKind.Flame, GetDroneCombatPosition());
            if (Gui.UpgradeInput.WasPressed(keybinds?.DronePilotGrenade))
                TryRequestManualFire(player, ManualFireKind.Grenade, ResolveManualGrenadeTarget());
            if (Gui.UpgradeInput.WasPressed(keybinds?.DronePilotLift))
                TryManualPickup(player);
            if (Gui.UpgradeInput.WasPressed(keybinds?.DronePilotLight))
                TryExecuteTabletCommand(player, CourierCommandType.Flashlight);
        }

        private static void TryRequestManualFire(PlayerControllerB player, ManualFireKind fireKind, Vector3 targetPosition)
        {
            if (player == null || !_manualControlActive)
                return;

            int playerId = (int)player.playerClientId;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient)
            {
                HandleManualFireRequest(playerId, fireKind, targetPosition);
                return;
            }

            if (NetworkManager.Singleton.IsServer)
            {
                HandleManualFireRequest(playerId, fireKind, targetPosition);
                return;
            }

            SendManualFireRequest(fireKind, targetPosition);
        }

        private static Vector3 ResolveManualGrenadeTarget()
        {
            Vector3 start = ResolveGrenadeMuzzle() != null
                ? ResolveGrenadeMuzzle().position
                : GetDroneCombatOrigin();
            if (start == Vector3.zero)
                return Vector3.zero;

            Camera camera = _manualCamera != null ? _manualCamera : Camera.main;
            Vector3 direction = _droneVisual != null ? _droneVisual.transform.forward : Vector3.forward;
            Vector3 rayOrigin = start;
            if (camera != null)
            {
                rayOrigin = camera.transform.position;
                direction = camera.transform.forward;
            }

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            Vector3 target = rayOrigin + direction.normalized * GRENADE_MAX_RANGE;
            if (Physics.Raycast(rayOrigin, direction, out RaycastHit hit, GRENADE_MAX_RANGE, mask, QueryTriggerInteraction.Ignore))
                target = hit.point;

            return ClampManualGrenadeTarget(start, target);
        }

        private static Vector3 ClampManualGrenadeTarget(Vector3 start, Vector3 target)
        {
            Vector3 offset = target - start;
            if (offset.sqrMagnitude <= 0.001f)
                offset = _droneVisual != null ? _droneVisual.transform.forward : Vector3.forward;

            float distance = Mathf.Clamp(offset.magnitude, GRENADE_MIN_RANGE, GRENADE_MAX_RANGE);
            return start + offset.normalized * distance;
        }

        private static void TryManualPickup(PlayerControllerB player)
        {
            if (_droneVisual == null || Time.time < _manualPickupLockedUntil)
                return;
            if (IsCargoWeightFull())
            {
                DisplayOwnerTip(_manualControlPlayerId, "Cargo weight full.", isWarning: true);
                return;
            }

            GrabbableObject item = FindManualPickupTarget();
            if (item == null)
            {
                DisplayOwnerTip(_manualControlPlayerId, "No scrap close enough to lift.", isWarning: true);
                return;
            }
            if (WouldExceedCargoWeight(item, GetCarriedCargoWeightPounds()))
            {
                DisplayOwnerTip(_manualControlPlayerId, "That scrap would exceed 150 lb cargo.", isWarning: true);
                return;
            }

            CourierItemMove move = new CourierItemMove(
                item.NetworkObjectId,
                item.transform.position,
                FindShipDropPosition(player),
                item.isInFactory);
            _manualPickupLockedUntil = Time.time + MANUAL_PICKUP_LOCK_SECONDS;
            StartCoroutineOnAvailableHost(MagnetPickUpScrap(move, move.PickupIsInFactory, item, _routeVersion));
            BroadcastManualPickup(move);
        }

        private static GrabbableObject FindManualPickupTarget()
        {
            if (_droneVisual == null)
                return null;

            Vector3 dronePosition = _droneVisual.transform.position;
            Vector3 cameraPosition = _manualCamera != null ? _manualCamera.transform.position : dronePosition;
            Vector3 cameraForward = _manualCamera != null ? _manualCamera.transform.forward : _droneVisual.transform.forward;
            float radiusSqr = MANUAL_PICKUP_RADIUS * MANUAL_PICKUP_RADIUS;
            GrabbableObject[] items = ScanGrabbableObjectsForCommandOrSweep();
            GrabbableObject best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (!IsManualPickupEligible(item, dronePosition, radiusSqr))
                    continue;

                Vector3 itemCenter = item.transform.position + Vector3.up * 0.2f;
                Vector3 toItem = itemCenter - cameraPosition;
                float angle = Vector3.Angle(cameraForward, toItem);
                float distance = Vector3.Distance(dronePosition, item.transform.position);
                float score = distance + angle * 0.035f;
                if (angle > 82f)
                    score += 8f;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                best = item;
            }

            return best;
        }

        private static bool IsManualPickupEligible(GrabbableObject item, Vector3 dronePosition, float radiusSqr)
        {
            return item != null
                && item.itemProperties != null
                && item.itemProperties.isScrap
                && item.isInFactory == _droneIsInside
                && !item.isHeld
                && !item.isPocketed
                && !item.isHeldByEnemy
                && !item.isInShipRoom
                && item.grabbable
                && !IsAlreadyCarried(item.NetworkObjectId)
                && !IsAlreadyPlanned(item.NetworkObjectId)
                && (item.transform.position - dronePosition).sqrMagnitude <= radiusSqr;
        }

        private static void StopManualControl(bool resumeDrone, bool notifyOwner)
        {
            bool wasActive = _manualControlActive;
            int playerId = _manualControlPlayerId;
            if (!wasActive && _manualCameraObject == null)
                return;

            FieldOperationsTabletPatch.TrySetPilotActionReadyHold(false);
            DestroyManualHud();
            RestoreManualPlayerPresentation();
            RestoreAudioListener();
            RestorePlayerInputAndCamera();

            if (_manualCameraObject != null)
            {
                UnityEngine.Object.Destroy(_manualCameraObject);
                _manualCameraObject = null;
                _manualCamera = null;
            }

            _manualControlActive = false;
            _manualControlCameraActivated = false;
            _manualControlPlayerId = -1;
            _manualDroneStartupUntil = 0f;
            _manualNextPoseSyncAt = 0f;
            _manualPickupLockedUntil = 0f;
            _manualSavedInputState = false;
            _manualSavedPresentationState = false;

            if (wasActive)
                BroadcastManualPose(active: false);

            if (resumeDrone && _droneVisual != null && !_droneDestroyedThisRound && _mode == DroneMode.ManualControl)
            {
                _mode = _manualPreviousMode == DroneMode.NotSpawned || IsBusyMode(_manualPreviousMode)
                    ? DroneMode.IdleAtPlayer
                    : _manualPreviousMode;
                if (_mode == DroneMode.ManualControl)
                    _mode = DroneMode.IdleAtPlayer;

                int version = ++_routeVersion;
                if (_mode == DroneMode.IdleAtPlayer)
                    StartIdleFollow(version);
            }

            if (notifyOwner && wasActive)
                DisplayOwnerTip(playerId, "Tablet control closed.", isWarning: false);
        }

        private static void RestorePlayerInputAndCamera()
        {
            PlayerControllerB player = ResolvePlayer(_manualControlPlayerId)
                ?? GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (player == null)
                return;

            player.disableMoveInput = _restoreMoveInput;
            player.disableLookInput = _restoreLookInput;
            player.disableInteract = _restoreInteractInput;
            if (player.gameplayCamera != null)
                player.gameplayCamera.enabled = _restoreGameplayCameraEnabled;
        }

        private static CourierCommand BuildCommand(PlayerControllerB player, CourierCommandType commandType, int courierTierOverride = -1)
        {
            if (_droneDestroyedThisRound)
                return null;

            CourierCommand command = BuildCommandContext(player, courierTierOverride);
            if (command == null)
                return null;

            DroneMode effectiveMode = _droneVisual == null ? DroneMode.NotSpawned : _mode;
            switch (commandType)
            {
                case CourierCommandType.Spawn:
                    command.Type = effectiveMode == DroneMode.NotSpawned
                        ? CourierCommandType.Spawn
                        : CourierCommandType.None;
                    command.DroneStartPosition = FindDroneStartPosition(player);
                    if (command.DroneStartPosition == Vector3.zero)
                        command.Type = CourierCommandType.None;
                    break;

                case CourierCommandType.Dismiss:
                    command.Type = effectiveMode == DroneMode.NotSpawned ? CourierCommandType.None : CourierCommandType.Dismiss;
                    command.DroneStartPosition = CurrentDroneGroundPositionOr(FindDroneStartPosition(player));
                    command.DropIsInShip = IsPlayerInShip(player);
                    command.DropIsInFactory = player != null && player.isInsideFactory;
                    command.DropPosition = FindPlayerDropPosition(player);
                    break;

                case CourierCommandType.Guard:
                    command.Type = effectiveMode == DroneMode.NotSpawned || IsBusyMode(effectiveMode)
                        ? CourierCommandType.None
                        : CourierCommandType.Guard;
                    command.DroneStartPosition = CurrentDroneGroundPositionOr(FindDroneStartPosition(player));
                    break;

                case CourierCommandType.FlyToEntrance:
                    command.Type = effectiveMode == DroneMode.NotSpawned || IsBusyMode(effectiveMode) || effectiveMode == DroneMode.AtEntrance
                        ? CourierCommandType.None
                        : CourierCommandType.FlyToEntrance;
                    command.DroneStartPosition = CurrentDroneGroundPositionOr(FindDroneStartPosition(player));
                    if (command.Type == CourierCommandType.FlyToEntrance)
                        command.Items.AddRange(FindEntranceScrap(command));
                    break;

                case CourierCommandType.ReturnToShip:
                    command.Type = effectiveMode == DroneMode.NotSpawned || IsBusyMode(effectiveMode)
                        ? CourierCommandType.None
                        : CourierCommandType.ReturnToShip;
                    command.DroneStartPosition = CurrentDroneGroundPositionOr(command.EntrancePosition);
                    break;

                case CourierCommandType.Flashlight:
                    command.Type = effectiveMode == DroneMode.NotSpawned ? CourierCommandType.None : CourierCommandType.Flashlight;
                    command.DroneStartPosition = CurrentDroneGroundPositionOr(FindDroneStartPosition(player));
                    command.FlashlightEnabled = !_flashlightEnabled;
                    break;

                case CourierCommandType.Pilot:
                    command.Type = command.CourierTier >= 3
                        && effectiveMode != DroneMode.NotSpawned
                        && effectiveMode != DroneMode.ManualControl
                        && !IsBusyMode(effectiveMode)
                        && !_magnetPickupActive
                        && Time.time >= _manualPickupLockedUntil
                            ? CourierCommandType.Pilot
                            : CourierCommandType.None;
                    command.DroneStartPosition = CurrentDroneGroundPositionOr(FindDroneStartPosition(player));
                    break;

                default:
                    command.Type = CourierCommandType.None;
                    break;
            }

            return command;
        }

        private static CourierCommand BuildCommandContext(PlayerControllerB player, int courierTierOverride = -1)
        {
            if (player == null)
                return null;

            MainEntranceRoute entrance = FindMainEntranceRoute();
            CourierCommand command = new CourierCommand
            {
                CourierTier = courierTierOverride >= 0 ? Mathf.Clamp(courierTierOverride, 0, 3) : CourierDroneUpgrade.GetTier(),
                PlayerId = (int)player.playerClientId,
                PlayerWasInside = player.isInsideFactory,
                DropIsInShip = true,
                DropIsInFactory = false,
                DropPosition = FindShipDropPosition(player),
                OutsideEntrancePosition = entrance.OutsidePosition,
                InsideEntrancePosition = entrance.InsidePosition
            };
            command.EntrancePosition = command.PlayerWasInside
                ? FirstNonZero(command.InsideEntrancePosition, command.OutsideEntrancePosition)
                : FirstNonZero(command.OutsideEntrancePosition, command.InsideEntrancePosition);
            return command;
        }



        private static bool HandlePointerCommand(int playerId, int courierTier, CourierCommandType commandType, NetworkObjectReference targetReference)
        {
            if (!IsServerAuthority())
                return false;

            PlayerControllerB player = ResolvePlayer(playerId);
            if (!CanServerAcceptPointerCommand(player, courierTier))
                return false;

            switch (commandType)
            {
                case CourierCommandType.PointerAttack:
                    if (!TryResolvePointerAttackTarget(targetReference, out EnemyAI enemy))
                        return false;

                    StartPointerAttack(player, enemy, courierTier);
                    return true;

                case CourierCommandType.PointerFetch:
                    if (!TryResolvePointerFetchTarget(targetReference, out GrabbableObject item))
                        return false;

                    StartPointerFetch(player, item, courierTier);
                    return true;
            }

            return false;
        }

        // #500: the requester's own tier (the host's on its local path, the sender's synced tier for
        // a client), not whether the host itself bought the drone.
        private static bool CanServerAcceptPointerCommand(PlayerControllerB player, int courierTier)
        {
            return courierTier > 0
                && _droneVisual != null
                && !_droneDestroyedThisRound
                && _mode != DroneMode.NotSpawned
                && _mode != DroneMode.ManualControl
                && !_manualControlActive
                && CanUsePointer(player);
        }

        private static bool TryResolvePointerAttackTarget(NetworkObjectReference targetReference, out EnemyAI enemy)
        {
            enemy = null;
            if (!TryResolveNetworkObjectReference(targetReference, out NetworkObject targetObject))
                return false;

            enemy = targetObject.GetComponent<EnemyAI>();
            if (enemy == null)
                enemy = targetObject.GetComponentInChildren<EnemyAI>();
            if (enemy == null)
                enemy = targetObject.GetComponentInParent<EnemyAI>();
            if (enemy == null)
            {
                EnemyAICollisionDetect detect = targetObject.GetComponentInChildren<EnemyAICollisionDetect>();
                if (detect != null)
                    enemy = detect.mainScript;
            }

            return enemy != null && !enemy.isEnemyDead && EnemySharesDroneSpace(enemy);
        }

        private static bool TryResolvePointerFetchTarget(NetworkObjectReference targetReference, out GrabbableObject item)
        {
            item = null;
            if (!TryResolveNetworkObjectReference(targetReference, out NetworkObject targetObject))
                return false;

            item = targetObject.GetComponent<GrabbableObject>();
            if (item == null)
                item = targetObject.GetComponentInChildren<GrabbableObject>();
            if (item == null)
                item = targetObject.GetComponentInParent<GrabbableObject>();

            return IsPointerFetchEligible(item);
        }

        private static bool TryResolveNetworkObjectReference(NetworkObjectReference targetReference, out NetworkObject targetObject)
        {
            targetObject = null;
            NetworkObjectReference reference = targetReference;
            return reference.TryGet(out targetObject, NetworkManager.Singleton) && targetObject != null;
        }

        private static void StartPointerAttack(PlayerControllerB player, EnemyAI enemy, int courierTier)
        {
            if (player == null || enemy == null)
                return;

            PreparePointerCommandMode(player, courierTier, DroneMode.PointerAttack);
            _priorityTargetId = enemy.NetworkObject != null ? enemy.NetworkObjectId : GetEnemyKey(enemy);
            _priorityTargetExpiresAt = Time.time + POINTER_PRIORITY_SECONDS;
            _priorityTargetUnreachableSince = 0f;
            MarkDroneThreat(enemy, POINTER_PRIORITY_SECONDS, attackedByDrone: true);
            CourierDroneRuntimeAssets.PlaySendToEntranceSound(_droneVisual);
            StartCoroutineOnAvailableHost(PointerAttackRoutine(_priorityTargetId, _routeVersion));
            BroadcastDroneState();
        }

        private static void StartPointerFetch(PlayerControllerB player, GrabbableObject item, int courierTier)
        {
            if (player == null || item == null)
                return;

            PreparePointerCommandMode(player, courierTier, DroneMode.PointerFetch);
            CourierDroneRuntimeAssets.PlaySendToEntranceSound(_droneVisual);
            StartCoroutineOnAvailableHost(PointerFetchRoutine(item.NetworkObjectId, (int)player.playerClientId, _routeVersion));
            BroadcastDroneState();
        }

        private static void PreparePointerCommandMode(PlayerControllerB player, int courierTier, DroneMode mode)
        {
            StopManualControl(resumeDrone: false, notifyOwner: false);
            if (_mode != DroneMode.PointerAttack && _mode != DroneMode.PointerFetch)
                _pointerPreviousMode = _mode;
            if (!CanResumePointerMode(_pointerPreviousMode))
                _pointerPreviousMode = DroneMode.IdleAtPlayer;

            _ownerPlayerId = (int)player.playerClientId;
            if (courierTier > 0)
                _ownerCourierTier = Mathf.Max(_ownerCourierTier, Mathf.Clamp(courierTier, 0, 3));
            _mode = mode;
            _isFollowingPlayer = false;
            _followSettled = false;
            _combatLandedUntil = 0f;
            _lastRouteMoveBlocked = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            ++_routeVersion;
        }

        private static IEnumerator PointerAttackRoutine(ulong enemyKey, int version)
        {
            while (version == _routeVersion && _mode == DroneMode.PointerAttack)
            {
                EnemyAI enemy = ResolveEnemy(enemyKey);
                if (!IsValidPriorityTarget(enemy) || Time.time > _priorityTargetExpiresAt)
                    break;

                MarkDroneThreat(enemy, DRONE_ATTACK_THREAT_SECONDS, attackedByDrone: true);
                Vector3 center = ResolveEnemyCenter(enemy);
                Vector3 combatPosition = GetDroneCombatPosition();
                float holdRange = FLAME_TRIGGER_RANGE * 0.9f;
                if (center != Vector3.zero && combatPosition != Vector3.zero && (center - combatPosition).sqrMagnitude <= holdRange * holdRange)
                {
                    _priorityTargetUnreachableSince = 0f;
                    HoldPointerAttackPosition(enemy);
                    yield return null;
                    continue;
                }

                Vector3 targetGround = ResampleGroundPoint(enemy.transform.position, CurrentDroneGroundPositionOr(_droneGroundPosition));
                if (targetGround == Vector3.zero)
                {
                    if (HasPointerBeenUnreachable(blocked: true))
                        break;
                    yield return null;
                    continue;
                }

                yield return MovePointerTowardGround(targetGround, version);
                if (version != _routeVersion)
                    yield break;
                if (HasPointerBeenUnreachable(_lastRouteMoveBlocked))
                    break;
            }

            ResumePointerPreviousMode(version);
        }

        private static IEnumerator PointerFetchRoutine(ulong itemId, int ownerPlayerId, int version)
        {
            float deadline = Time.time + POINTER_FETCH_TIMEOUT_SECONDS;
            bool pickedUp = IsAlreadyCarried(itemId);
            bool returnedToPlayer = false;

            while (!pickedUp && version == _routeVersion && _mode == DroneMode.PointerFetch && Time.time < deadline)
            {
                GrabbableObject item = ResolveItem(itemId);
                if (!IsPointerFetchEligible(item))
                    break;

                if (IsDroneCloseEnoughToItem(item))
                {
                    PlayerControllerB owner = ResolvePlayer(ownerPlayerId);
                    Vector3 dropPosition = FindPlayerDropPosition(owner);
                    CourierItemMove move = new CourierItemMove(item.NetworkObjectId, item.transform.position, dropPosition, item.isInFactory);
                    yield return MagnetPickUpScrap(move, move.PickupIsInFactory, item, version);
                    pickedUp = IsAlreadyCarried(itemId);
                    break;
                }

                Vector3 targetGround = ResampleGroundPoint(item.transform.position, CurrentDroneGroundPositionOr(_droneGroundPosition));
                if (targetGround == Vector3.zero)
                {
                    if (HasPointerBeenUnreachable(blocked: true))
                        break;
                    yield return null;
                    continue;
                }

                yield return MovePointerTowardGround(targetGround, version);
                if (version != _routeVersion)
                    yield break;
                if (HasPointerBeenUnreachable(_lastRouteMoveBlocked))
                    break;
            }

            if (pickedUp && version == _routeVersion && _mode == DroneMode.PointerFetch)
            {
                yield return ReturnPointerFetchCargo(ownerPlayerId, version, deadline);
                returnedToPlayer = IsPointerFetchReturnComplete(ownerPlayerId);
            }

            if ((!pickedUp || !returnedToPlayer) && version == _routeVersion && _mode == DroneMode.PointerFetch)
                CourierDroneRuntimeAssets.PlayRecallSound(_droneVisual);

            ResumePointerPreviousMode(version);
        }

        private static IEnumerator ReturnPointerFetchCargo(int ownerPlayerId, int version, float deadline)
        {
            while (version == _routeVersion && _mode == DroneMode.PointerFetch && Time.time < deadline)
            {
                PlayerControllerB owner = ResolvePlayer(ownerPlayerId);
                if (owner == null || owner.isPlayerDead || owner.transform == null || owner.isInsideFactory != _droneIsInside)
                    yield break;

                Vector3 targetGround = ResampleGroundPoint(FindPlayerDropPosition(owner), CurrentDroneGroundPositionOr(_droneGroundPosition));
                if (targetGround == Vector3.zero)
                    yield break;

                Vector3 currentGround = CurrentDroneGroundPositionOr(_droneGroundPosition);
                if (currentGround != Vector3.zero && (currentGround - targetGround).sqrMagnitude <= FOLLOW_SETTLE_DISTANCE * FOLLOW_SETTLE_DISTANCE)
                    yield break;

                yield return MovePointerTowardGround(targetGround, version);
                if (version != _routeVersion)
                    yield break;
                if (HasPointerBeenUnreachable(_lastRouteMoveBlocked))
                    yield break;
            }
        }


        private static bool IsPointerFetchReturnComplete(int ownerPlayerId)
        {
            PlayerControllerB owner = ResolvePlayer(ownerPlayerId);
            if (owner == null || owner.isPlayerDead || owner.transform == null || owner.isInsideFactory != _droneIsInside)
                return false;

            Vector3 currentGround = CurrentDroneGroundPositionOr(_droneGroundPosition);
            Vector3 targetGround = ResampleGroundPoint(FindPlayerDropPosition(owner), currentGround);
            return currentGround != Vector3.zero
                && targetGround != Vector3.zero
                && (currentGround - targetGround).sqrMagnitude <= FOLLOW_SETTLE_DISTANCE * FOLLOW_SETTLE_DISTANCE;
        }

        private static IEnumerator MovePointerTowardGround(Vector3 targetGround, int version)
        {
            _lastRouteMoveBlocked = false;
            if (_droneVisual == null || targetGround == Vector3.zero)
            {
                _lastRouteMoveBlocked = true;
                yield break;
            }

            Vector3 currentGround = CurrentDroneGroundPositionOr(_droneGroundPosition);
            if (currentGround == Vector3.zero)
                currentGround = ResampleGroundPoint(_droneVisual.transform.position, targetGround);
            if (currentGround == Vector3.zero)
            {
                _lastRouteMoveBlocked = true;
                yield break;
            }

            List<Vector3> path = BuildNavMeshPath(currentGround, targetGround);
            if (path == null || path.Count < 2)
            {
                _lastRouteMoveBlocked = true;
                yield break;
            }

            Vector3 previousPosition = _droneVisual.transform.position;
            yield return MoveDroneSegment(path[0], path[1], previousPosition.x, previousPosition.y, previousPosition.z, version);
        }

        private static void HoldPointerAttackPosition(EnemyAI enemy)
        {
            if (_droneVisual == null || enemy == null)
                return;

            Vector3 center = ResolveEnemyCenter(enemy);
            Vector3 delta = center - _droneVisual.transform.position;
            FaceTravelDirection(_droneVisual.transform, delta.x, delta.y, delta.z);
            UpdateCarriedItemTransforms();
        }

        private static bool HasPointerBeenUnreachable(bool blocked)
        {
            if (!blocked)
            {
                _priorityTargetUnreachableSince = 0f;
                return false;
            }

            if (_priorityTargetUnreachableSince <= 0f)
                _priorityTargetUnreachableSince = Time.time;
            return Time.time - _priorityTargetUnreachableSince >= PRIORITY_TARGET_UNREACHABLE_SECONDS;
        }

        private static bool IsValidPriorityTarget(EnemyAI enemy)
        {
            return enemy != null
                && !enemy.isEnemyDead
                && EnemySharesDroneSpace(enemy)
                && Time.time <= _priorityTargetExpiresAt;
        }

        private static EnemyAI ResolveEnemy(ulong enemyKey)
        {
            if (enemyKey == 0)
                return null;

            if (NetworkManager.Singleton != null)
            {
                NetworkObject networkObject;
                if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(enemyKey, out networkObject) && networkObject != null)
                {
                    EnemyAI enemy = networkObject.GetComponent<EnemyAI>();
                    if (enemy != null)
                        return enemy;
                }
            }

            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || roundManager.SpawnedEnemies == null)
                return null;

            for (int i = 0; i < roundManager.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = roundManager.SpawnedEnemies[i];
                if (enemy != null && GetEnemyKey(enemy) == enemyKey)
                    return enemy;
            }

            return null;
        }

        private static bool IsDroneCloseEnoughToItem(GrabbableObject item)
        {
            if (item == null || _droneVisual == null)
                return false;

            float radius = CourierDroneUpgrade.PICKUP_RADIUS;
            return (item.transform.position - _droneVisual.transform.position).sqrMagnitude <= radius * radius;
        }

        private static bool CanResumePointerMode(DroneMode mode)
        {
            return mode == DroneMode.IdleAtPlayer
                || mode == DroneMode.GuardingPlayer
                || mode == DroneMode.AtEntrance;
        }

        private static void ResumePointerPreviousMode(int version)
        {
            if (version != _routeVersion)
                return;

            DroneMode resumeMode = CanResumePointerMode(_pointerPreviousMode) ? _pointerPreviousMode : DroneMode.IdleAtPlayer;
            ClearPointerTargetState();
            _followSettled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();

            int nextVersion = ++_routeVersion;
            if (resumeMode == DroneMode.GuardingPlayer)
            {
                _mode = DroneMode.GuardingPlayer;
                StartCoroutineOnAvailableHost(GuardFollowRoutine(nextVersion));
            }
            else if (resumeMode == DroneMode.AtEntrance)
            {
                PlayerControllerB owner = ResolvePlayer(_ownerPlayerId);
                CourierCommand context = BuildCommandContext(owner);
                if (context != null)
                {
                    _mode = DroneMode.AtEntrance;
                    StartCoroutineOnAvailableHost(WaitAtEntranceRoutine(context, nextVersion));
                }
                else
                {
                    _mode = DroneMode.IdleAtPlayer;
                    StartIdleFollow(nextVersion);
                }
            }
            else
            {
                _mode = DroneMode.IdleAtPlayer;
                StartIdleFollow(nextVersion);
            }

            BroadcastDroneState();
        }

        private static void ClearPriorityTargetOnly()
        {
            _priorityTargetId = 0;
            _priorityTargetExpiresAt = 0f;
            _priorityTargetUnreachableSince = 0f;
        }

        private static void ClearPointerTargetState()
        {
            ClearPriorityTargetOnly();
            _pointerPreviousMode = DroneMode.NotSpawned;
        }


        private static void ResetDroneState(bool destroyVisual)
        {
            StopManualControl(resumeDrone: false, notifyOwner: false);
            _routeVersion++;
            CourierDroneRuntimeAssets.StopMagnetSound(_droneVisual);
            PlannedItems.Clear();
            // F-DRONE-5: release rather than forget, or the cargo stays frozen in mid-air.
            ReleaseCarriedItems();
            KnownGrabbableItems.Clear();
            DroneThreats.Clear();
            DroneAttackThreats.Clear();
            NextDroneHitByEnemy.Clear();
            NextDogEnrageAtByEnemy.Clear();
            DogDroneEnrageTargetByEnemy.Clear();
            DestroyActiveGrenadeVisuals();
            PointerTabletTransformCache.Clear();
            EnemyCenterRenderers.Clear();
            _cachedMainEntrances = null;
            _cachedMainEntranceRound = null;
            _ownerCourierTier = 0;
            _nextEnemyScanAt = 0f;
            _lastEnemyScanFrame = -1;
            _nextFlameAt = 0f;
            _nextGrenadeAt = 0f;
            _combatLandedUntil = 0f;
            _droneCombatActiveUntil = 0f;
            _nextEntranceScrapScanAt = 0f;
            _nextGuardScrapScanAt = 0f;
            _flashlightEnabled = false;
            _magnetPickupActive = false;
            _retreatUntil = 0f;
            _lastManualPoseAt = 0f;
            ClearPointerTargetState();
            _mode = DroneMode.NotSpawned;
            _droneGroundPosition = Vector3.zero;
            _droneIsInside = false;
            _isFollowingPlayer = false;
            _followSettled = false;
            ClearFollowPathCache();
            _reportedNoEntranceScrap = false;
            _ownerPlayerId = -1;

            if (destroyVisual && _droneVisual != null)
            {
                UnityEngine.Object.Destroy(_droneVisual);
                _droneVisual = null;
                ClearDroneRendererCache();
            }
        }


        private static void StartCoroutineOnAvailableHost(IEnumerator routine)
        {
            MonoBehaviour host = HUDManager.Instance != null
                ? (MonoBehaviour)HUDManager.Instance
                : UnityEngine.Object.FindFirstObjectByType<StartOfRound>();
            if (host != null)
                host.StartCoroutine(routine);
        }


        private static void DisplayOwnerTip(int playerId, string message, bool isWarning)
        {
            if (HUDManager.Instance == null)
                return;
            // F-DRONE-15: courier tips are owner-only, and playerId -1 (the reset value of
            // _ownerPlayerId / _manualControlPlayerId) used to fall straight through the filter below
            // and pop up for every crew member instead of nobody.
            if (playerId < 0)
                return;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local != null && (int)local.playerClientId != playerId)
                return;

            HUDManager.Instance.DisplayTip("COURIER DRONE", message, isWarning);
        }

        private static bool CanUse(PlayerControllerB player)
        {
            return player != null
                && !player.isPlayerDead
                && !player.isTypingChat
                && !player.inTerminalMenu
                && !player.inSpecialInteractAnimation
                && (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen);
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static bool IsServerAuthority()
        {
            return NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;
        }

        /// <summary>
        /// Maps a courier player id onto the client id the delivery ledger compares against.
        /// allPlayerScripts is index-stable across the crew, so this resolves identically on
        /// every machine; an id that does not resolve pays nobody rather than paying wrongly.
        /// </summary>
        private static ulong ResolveCourierHandlerClientId(int courierPlayerId)
        {
            PlayerControllerB courier = courierPlayerId >= 0 ? ResolvePlayer(courierPlayerId) : null;
            return courier != null
                ? courier.actualClientId
                : DeliveryAttributionLedger.UnattributedHandler;
        }

        private static PlayerControllerB ResolvePlayer(int playerId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            if (playerId >= 0 && playerId < players.Length && players[playerId] != null)
                return players[playerId];

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && (int)player.playerClientId == playerId)
                    return player;
            }

            return null;
        }

        private static PlayerControllerB ResolvePlayerByActualClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == clientId)
                    return player;
            }

            return null;
        }

        /// <summary>
        /// #500: the Courier tier the host trusts for a client's drone order: the tier that client
        /// published through UpgradeTierSync (the Field Operations tablet's source too), never one
        /// carried in a drone message.
        /// </summary>
        private static int ResolveServerCourierTier(ulong senderClientId)
        {
            return Mathf.Clamp(UpgradeTierSync.GetTier(senderClientId, CourierDroneUpgrade.UPGRADE_ID), 0, 3);
        }

        /// <summary>#500: one warning per sender per session for a refused Courier message, no stack.</summary>
        private static void WarnRefusedCourierMessage(string messageName, ulong senderClientId)
        {
            if (RefusedCourierMessageSenders.Add(senderClientId))
                Plugin.Log?.LogWarning($"CourierDronePatch: refused {messageName} from client {senderClientId}; later refusals from this client are not logged.");
        }

        /// <summary>
        /// #500: a host-to-all message applies only on a client and only from the server, as
        /// OnReceiveEnemyAttack does. The host applied it before broadcasting, so its own loopback
        /// is dropped quietly; any other sender reaching the host is warned about once.
        /// </summary>
        private static bool IsServerBroadcast(string messageName, ulong senderClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return false;
            if (network.IsServer)
            {
                if (senderClientId != network.LocalClientId)
                    WarnRefusedCourierMessage(messageName, senderClientId);
                return false;
            }

            return senderClientId == NetworkManager.ServerClientId;
        }

        /// <summary>
        /// #500: the host takes a client's pilot pose or pickup only from the player its own Pilot
        /// gate handed the link to (StartPilotControl: ManualControl with that player as owner), and
        /// an active pose also needs that player's synced Courier tier 3. With no link held the
        /// message is the tail of a release the pilot has not heard of yet (watchdog, dismiss, drone
        /// death); the State or Command broadcast that released it already takes the pilot out, so
        /// it is dropped without a warning.
        /// </summary>
        private static bool IsFromHostPilot(string messageName, ulong senderClientId, PlayerControllerB sender, bool requirePilotTier)
        {
            if (_mode != DroneMode.ManualControl)
                return false;

            if (sender == null
                || _manualControlActive
                || _ownerPlayerId != (int)sender.playerClientId
                || (requirePilotTier && ResolveServerCourierTier(senderClientId) < 3))
            {
                WarnRefusedCourierMessage(messageName, senderClientId);
                return false;
            }

            return true;
        }

        private static GrabbableObject ResolveItem(ulong networkId)
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkObject netObject;
                if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkId, out netObject) && netObject != null)
                    return netObject.GetComponent<GrabbableObject>();
            }

            GrabbableObject cachedItem;
            if (KnownGrabbableItems.TryGetValue(networkId, out cachedItem))
            {
                if (cachedItem != null)
                    return cachedItem;

                KnownGrabbableItems.Remove(networkId);
            }

            return null;
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered) return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_REQUEST, OnReceiveRequest);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_COMMAND, OnReceiveCommand);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_STATE, OnReceiveState);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_DEATH, OnReceiveDeath);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_HIT, OnReceiveHit);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_FLAME, OnReceiveFlame);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_GRENADE_LAUNCH, OnReceiveGrenadeLaunch);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_GRENADE_EXPLODE, OnReceiveGrenadeExplosion);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_MANUAL_POSE, OnReceiveManualPose);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_MANUAL_PICKUP, OnReceiveManualPickup);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_MANUAL_FIRE, OnReceiveManualFire);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_POINTER_COMMAND, OnReceivePointerCommand);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_COURIER_ENEMY_ATTACK, OnReceiveEnemyAttack);
                SubscribeNetworkLifecycleCallbacks();
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Host-side lifecycle hooks. F-DRONE-6 needs to know when the piloting client leaves, and
        /// F-DRONE-18 needs to resync a player who joins after the drone is already deployed.
        /// Subscriptions are removed first so a re-registration cannot double-subscribe.
        /// </summary>
        private static void SubscribeNetworkLifecycleCallbacks()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return;

            network.OnClientDisconnectCallback -= OnNetworkClientDisconnected;
            network.OnClientDisconnectCallback += OnNetworkClientDisconnected;
            network.OnClientConnectedCallback -= OnNetworkClientConnected;
            network.OnClientConnectedCallback += OnNetworkClientConnected;
        }

        private static void UnsubscribeNetworkLifecycleCallbacks()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return;

            network.OnClientDisconnectCallback -= OnNetworkClientDisconnected;
            network.OnClientConnectedCallback -= OnNetworkClientConnected;
        }

        /// <summary>
        /// F-DRONE-6: a pilot whose process dies never sends the active:false pose that
        /// OnReceiveManualPose releases the link on, and PostDisconnect only runs on the machine that
        /// disconnected - so the drone sat frozen showing PILOT LINK and answering "BUSY" for the
        /// rest of the round. The host releases the link as soon as that client's connection drops.
        /// </summary>
        private static void OnNetworkClientDisconnected(ulong clientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return;
            if (_mode != DroneMode.ManualControl || _manualControlActive)
                return;

            PlayerControllerB pilot = ResolvePlayer(_ownerPlayerId);
            if (pilot == null || pilot.actualClientId != clientId)
                return;

            ReleaseManualControlOnHost("Drone pilot disconnected. Drone returning to follow.");
        }

        /// <summary>
        /// F-DRONE-18 (late-join part only): RegisterNetworkHandlers is driven off
        /// ConnectClientToPlayerObject, and there was no snapshot for a player who joined after the
        /// drone was deployed - their tablet showed nothing until the next command. The delay gives
        /// the joiner time to run ConnectClientToPlayerObject and register its own named-message
        /// handlers before the state message arrives.
        /// </summary>
        private static void OnNetworkClientConnected(ulong clientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || clientId == network.LocalClientId)
                return;

            StartCoroutineOnAvailableHost(BroadcastDroneStateForLateJoiner());
        }

        private static IEnumerator BroadcastDroneStateForLateJoiner()
        {
            yield return new WaitForSeconds(LATE_JOIN_STATE_DELAY_SECONDS);
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                yield break;

            BroadcastDroneState();
        }

        /// <summary>
        /// F-DRONE-6: the disconnect callback covers a clean drop, but a hard crash or a stalled
        /// connection can leave the link held with no further poses arriving. Two seconds of pose
        /// silence hands the drone back to autonomous follow. Host-side only, and skipped entirely
        /// when this machine is the one piloting.
        /// </summary>
        private static void TickManualControlWatchdogOncePerFrame()
        {
            if (_lastManualWatchdogFrame == Time.frameCount)
                return;

            _lastManualWatchdogFrame = Time.frameCount;
            if (!IsServerAuthority() || _manualControlActive || _mode != DroneMode.ManualControl)
                return;
            if (_droneVisual == null || _droneDestroyedThisRound)
                return;
            if (_lastManualPoseAt <= 0f || Time.time - _lastManualPoseAt < MANUAL_POSE_TIMEOUT_SECONDS)
                return;

            ReleaseManualControlOnHost("Drone pilot link lost. Drone returning to follow.");
        }

        private static void ReleaseManualControlOnHost(string ownerMessage)
        {
            int pilotPlayerId = _ownerPlayerId;
            _lastManualPoseAt = 0f;
            _mode = DroneMode.IdleAtPlayer;
            _isFollowingPlayer = false;
            _followSettled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            StartIdleFollow(++_routeVersion);
            DisplayOwnerTip(pilotPlayerId, ownerMessage, isWarning: true);
            BroadcastDroneState();
        }

#pragma warning disable Harmony003

        private static void SendPointerCommand(int playerId, int courierTier, CourierCommandType commandType, NetworkObjectReference targetReference)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null || !NetworkManager.Singleton.IsClient)
            {
                HandlePointerCommand(playerId, courierTier, commandType, targetReference);
                return;
            }

            if (NetworkManager.Singleton.IsServer)
            {
                if (HandlePointerCommand(playerId, courierTier, commandType, targetReference))
                    BroadcastPointerCommand(playerId, courierTier, commandType, targetReference);
                return;
            }

            // #500 request layout: int commandType, target. The host takes the player and the
            // Courier tier from the connection; playerId and courierTier serve the local paths above.
            FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(ulong) + 16, Allocator.Temp);
            try
            {
                writer.WriteValueSafe((int)commandType);
                writer.WriteNetworkSerializable(targetReference);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                    MSG_COURIER_POINTER_COMMAND,
                    NetworkManager.ServerClientId,
                    writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void BroadcastPointerCommand(int playerId, int courierTier, CourierCommandType commandType, NetworkObjectReference targetReference)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 3 + sizeof(ulong) + 16, Allocator.Temp);
            try
            {
                WritePointerCommandPayload(ref writer, playerId, courierTier, commandType, targetReference);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_POINTER_COMMAND, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        // #500: host-to-all rebroadcast only. A client's request carries just commandType and target.
        private static void WritePointerCommandPayload(ref FastBufferWriter writer, int playerId, int courierTier, CourierCommandType commandType, NetworkObjectReference targetReference)
        {
            writer.WriteValueSafe(playerId);
            writer.WriteValueSafe(courierTier);
            writer.WriteValueSafe((int)commandType);
            writer.WriteNetworkSerializable(targetReference);
        }

        private static void OnReceivePointerCommand(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return;

            try
            {
                int commandType;
                NetworkObjectReference targetReference;
                if (network.IsServer)
                {
                    // The host's own rebroadcast looping back: it handled the order before sending.
                    if (senderClientId == network.LocalClientId)
                        return;

                    // #500 client request: int commandType, target. The acting player and the
                    // Courier tier come from the connection, never the payload.
                    reader.ReadValueSafe(out commandType);
                    reader.ReadNetworkSerializable(out targetReference);
                    PlayerControllerB requester = ResolvePlayerByActualClientId(senderClientId);
                    int requesterTier = ResolveServerCourierTier(senderClientId);
                    if (requester == null || requesterTier < 1)
                    {
                        WarnRefusedCourierMessage(MSG_COURIER_POINTER_COMMAND, senderClientId);
                        return;
                    }

                    int requesterId = (int)requester.playerClientId;
                    if (HandlePointerCommand(requesterId, requesterTier, (CourierCommandType)commandType, targetReference))
                        BroadcastPointerCommand(requesterId, requesterTier, (CourierCommandType)commandType, targetReference);
                    return;
                }

                // #500: off-host only the server's rebroadcast, carrying the player and tier the
                // host derived (WritePointerCommandPayload).
                if (senderClientId != NetworkManager.ServerClientId)
                    return;

                int playerId;
                int courierTier;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out courierTier);
                reader.ReadValueSafe(out commandType);
                reader.ReadNetworkSerializable(out targetReference);
                ApplyPointerCommandBroadcast(playerId, courierTier, (CourierCommandType)commandType, targetReference);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed pointer command: {ex.Message}");
            }
        }

        private static void ApplyPointerCommandBroadcast(int playerId, int courierTier, CourierCommandType commandType, NetworkObjectReference targetReference)
        {
            PlayerControllerB player = ResolvePlayer(playerId);
            if (player == null || _droneVisual == null || _droneDestroyedThisRound)
                return;

            switch (commandType)
            {
                case CourierCommandType.PointerAttack:
                    if (TryResolvePointerAttackTarget(targetReference, out EnemyAI enemy))
                        StartPointerAttack(player, enemy, courierTier);
                    break;
                case CourierCommandType.PointerFetch:
                    if (TryResolvePointerFetchTarget(targetReference, out GrabbableObject item))
                        StartPointerFetch(player, item, courierTier);
                    break;
            }
        }

        // #500 layout: int commandType. The host takes the player and Courier tier from the connection.
        private static void SendRequest(CourierCommandType commandType)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
            try
            {
                writer.WriteValueSafe((int)commandType);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                    MSG_COURIER_REQUEST,
                    NetworkManager.ServerClientId,
                    writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            try
            {
                int commandType;
                reader.ReadValueSafe(out commandType);
                // #500: who asks and at which Courier tier come from the connection, never the
                // payload, so the Pilot gate and _ownerCourierTier only see the sender's own tier.
                PlayerControllerB player = ResolvePlayerByActualClientId(senderClientId);
                int courierTier = ResolveServerCourierTier(senderClientId);
                if (player == null || courierTier < 1)
                {
                    WarnRefusedCourierMessage(MSG_COURIER_REQUEST, senderClientId);
                    return;
                }

                CourierCommand command = BuildCommand(player, (CourierCommandType)commandType, courierTier);
                ApplyCommand(command);
                BroadcastCommand(command);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed request: {ex.Message}");
            }
        }

        private static void BroadcastCommand(CourierCommand command)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;
            if (command == null) return;

            int count = command.Items.Count;
            FastBufferWriter writer = new FastBufferWriter(
                sizeof(int) * 6 + sizeof(float) * 12 + count * (sizeof(ulong) + sizeof(int) + sizeof(float) * 6),
                Allocator.Temp);
            try
            {
                writer.WriteValueSafe((int)command.Type);
                writer.WriteValueSafe(command.PlayerId);
                writer.WriteValueSafe(command.CourierTier);
                writer.WriteValueSafe(BuildCommandFlags(command));
                writer.WriteValueSafe(count);
                writer.WriteValueSafe(_droneIsInside ? 1 : 0);
                WriteVector(ref writer, command.DroneStartPosition.x, command.DroneStartPosition.y, command.DroneStartPosition.z);
                WriteVector(ref writer, command.OutsideEntrancePosition.x, command.OutsideEntrancePosition.y, command.OutsideEntrancePosition.z);
                WriteVector(ref writer, command.InsideEntrancePosition.x, command.InsideEntrancePosition.y, command.InsideEntrancePosition.z);
                WriteVector(ref writer, command.DropPosition.x, command.DropPosition.y, command.DropPosition.z);
                for (int i = 0; i < count; i++)
                {
                    writer.WriteValueSafe(command.Items[i].NetworkObjectId);
                    writer.WriteValueSafe(command.Items[i].PickupIsInFactory ? 1 : 0);
                    WriteVector(ref writer, command.Items[i].PickupPosition.x, command.Items[i].PickupPosition.y, command.Items[i].PickupPosition.z);
                    WriteVector(ref writer, command.Items[i].DropPosition.x, command.Items[i].DropPosition.y, command.Items[i].DropPosition.z);
                }

                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_COMMAND, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void BroadcastDroneState()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            Vector3 groundPosition = CurrentDroneGroundPositionOr(_droneGroundPosition);
            FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 5 + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(_droneHealth);
                writer.WriteValueSafe(_droneDestroyedThisRound ? 1 : 0);
                writer.WriteValueSafe(_droneIsInside ? 1 : 0);
                writer.WriteValueSafe((int)_mode);
                writer.WriteValueSafe(_flashlightEnabled ? 1 : 0);
                WriteVector(ref writer, groundPosition.x, groundPosition.y, groundPosition.z);

                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_STATE, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void BroadcastDroneDeath(Vector3 groundPosition, bool isInside)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(isInside ? 1 : 0);
                WriteVector(ref writer, groundPosition.x, groundPosition.y, groundPosition.z);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_DEATH, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void BroadcastDroneHit(int damageAmount, bool bigHit, int healthAfter)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 2 + sizeof(bool), Allocator.Temp);
            try
            {
                writer.WriteValueSafe(damageAmount);
                writer.WriteValueSafe(bigHit);
                writer.WriteValueSafe(healthAfter);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_HIT, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void BroadcastDroneFlame(Vector3 position)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(float) * 3, Allocator.Temp);
            try
            {
                WriteVector(ref writer, position.x, position.y, position.z);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_FLAME, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void BroadcastGrenadeLaunch(int grenadeId, Vector3 start, Vector3 velocity)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(float) * 6, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(grenadeId);
                WriteVector(ref writer, start.x, start.y, start.z);
                WriteVector(ref writer, velocity.x, velocity.y, velocity.z);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_GRENADE_LAUNCH, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        // #500: host to all. Payload: int grenadeId, Vector3 position, int force (enemy damage).
        private static void BroadcastGrenadeExplosion(int grenadeId, Vector3 position, int force)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 2 + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(grenadeId);
                WriteVector(ref writer, position.x, position.y, position.z);
                writer.WriteValueSafe(force);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_GRENADE_EXPLODE, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        // #500: host to all. Payload: ulong enemy NetworkObjectId, Vector3 drone position.
        private static void BroadcastEnemyDroneAttack(EnemyAI enemy, Vector3 dronePosition)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;
            if (enemy.NetworkObject == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(ulong) + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(enemy.NetworkObjectId);
                WriteVector(ref writer, dronePosition.x, dronePosition.y, dronePosition.z);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_ENEMY_ATTACK, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private const int MANUAL_POSE_BYTES = sizeof(int) * 3 + sizeof(float) * 4;
        private const int MANUAL_PICKUP_BYTES = sizeof(int) * 2 + sizeof(ulong) + sizeof(float) * 6;

        private static void WriteManualPosePayload(
            ref FastBufferWriter writer, int playerId, int activeFlag, int insideFlag, Vector3 position, float yaw)
        {
            writer.WriteValueSafe(playerId);
            writer.WriteValueSafe(activeFlag);
            writer.WriteValueSafe(insideFlag);
            WriteVector(ref writer, position.x, position.y, position.z);
            writer.WriteValueSafe(yaw);
        }

        private static void WriteManualPickupPayload(
            ref FastBufferWriter writer, int playerId, int insideFlag, ulong itemId, Vector3 pickup, Vector3 drop)
        {
            writer.WriteValueSafe(playerId);
            writer.WriteValueSafe(insideFlag);
            writer.WriteValueSafe(itemId);
            WriteVector(ref writer, pickup.x, pickup.y, pickup.z);
            WriteVector(ref writer, drop.x, drop.y, drop.z);
        }

        /// <summary>
        /// Netcode has no client-to-all primitive - SendNamedMessageToAll reads ConnectedClientsIds,
        /// whose getter throws off-host, so a client pilot's pose never left its own machine. The
        /// host broadcasts; a client sends to the host, which relays (see OnReceiveManualPose).
        /// </summary>
        private static void BroadcastManualPose(bool active)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            Vector3 position = _droneVisual != null ? _droneVisual.transform.position : Vector3.zero;
            FastBufferWriter writer = new FastBufferWriter(MANUAL_POSE_BYTES, Allocator.Temp);
            try
            {
                WriteManualPosePayload(
                    ref writer,
                    _manualControlPlayerId >= 0 ? _manualControlPlayerId : _ownerPlayerId,
                    active ? 1 : 0,
                    _droneIsInside ? 1 : 0,
                    position,
                    _manualYaw);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_COURIER_MANUAL_POSE, writer);
                else
                    messaging.SendNamedMessage(MSG_COURIER_MANUAL_POSE, NetworkManager.ServerClientId, writer);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: manual pose send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void RelayManualPose(
            ulong excludeClientId, int playerId, int activeFlag, int insideFlag, Vector3 position, float yaw)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(MANUAL_POSE_BYTES, Allocator.Temp);
                try
                {
                    WriteManualPosePayload(ref writer, playerId, activeFlag, insideFlag, position, yaw);
                    messaging.SendNamedMessage(MSG_COURIER_MANUAL_POSE, clientId, writer);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"CourierDronePatch: manual pose relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        private static void BroadcastManualPickup(CourierItemMove move)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            FastBufferWriter writer = new FastBufferWriter(MANUAL_PICKUP_BYTES, Allocator.Temp);
            try
            {
                WriteManualPickupPayload(
                    ref writer,
                    _manualControlPlayerId >= 0 ? _manualControlPlayerId : _ownerPlayerId,
                    move.PickupIsInFactory ? 1 : 0,
                    move.NetworkObjectId,
                    move.PickupPosition,
                    move.DropPosition);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_COURIER_MANUAL_PICKUP, writer);
                else
                    messaging.SendNamedMessage(MSG_COURIER_MANUAL_PICKUP, NetworkManager.ServerClientId, writer);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: manual pickup send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void RelayManualPickup(
            ulong excludeClientId, int playerId, int insideFlag, ulong itemId, Vector3 pickup, Vector3 drop)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(MANUAL_PICKUP_BYTES, Allocator.Temp);
                try
                {
                    WriteManualPickupPayload(ref writer, playerId, insideFlag, itemId, pickup, drop);
                    messaging.SendNamedMessage(MSG_COURIER_MANUAL_PICKUP, clientId, writer);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"CourierDronePatch: manual pickup relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        // #500 layout: int fireKind, Vector3 target. The host takes the shooter from the connection.
        private static void SendManualFireRequest(ManualFireKind fireKind, Vector3 targetPosition)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe((int)fireKind);
                WriteVector(ref writer, targetPosition.x, targetPosition.y, targetPosition.z);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                    MSG_COURIER_MANUAL_FIRE,
                    NetworkManager.ServerClientId,
                    writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveCommand(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerBroadcast(MSG_COURIER_COMMAND, senderClientId))
                return;

            try
            {
                int commandType;
                int playerId;
                int courierTier;
                int flags;
                int count;
                int droneSideFlag;
                reader.ReadValueSafe(out commandType);
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out courierTier);
                reader.ReadValueSafe(out flags);
                reader.ReadValueSafe(out count);
                reader.ReadValueSafe(out droneSideFlag);

                CourierCommand command = new CourierCommand
                {
                    Type = (CourierCommandType)commandType,
                    PlayerId = playerId,
                    CourierTier = courierTier,
                    PlayerWasInside = (flags & 1) != 0,
                    DropIsInShip = (flags & 2) != 0,
                    DropIsInFactory = (flags & 4) != 0,
                    FlashlightEnabled = (flags & 8) != 0,
                    DroneStartPosition = ReadVector(ref reader),
                    OutsideEntrancePosition = ReadVector(ref reader),
                    InsideEntrancePosition = ReadVector(ref reader),
                    DropPosition = ReadVector(ref reader)
                };
                command.EntrancePosition = command.PlayerWasInside
                    ? FirstNonZero(command.InsideEntrancePosition, command.OutsideEntrancePosition)
                    : FirstNonZero(command.OutsideEntrancePosition, command.InsideEntrancePosition);

                for (int i = 0; i < count; i++)
                {
                    ulong netId;
                    int itemFlags;
                    reader.ReadValueSafe(out netId);
                    reader.ReadValueSafe(out itemFlags);
                    Vector3 pickup = ReadVector(ref reader);
                    Vector3 drop = ReadVector(ref reader);
                    command.Items.Add(new CourierItemMove(netId, pickup, drop, (itemFlags & 1) != 0));
                }

                if (_droneVisual == null)
                    _droneIsInside = droneSideFlag != 0;
                ApplyCommand(command);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed command: {ex.Message}");
            }
        }

        private static void OnReceiveState(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerBroadcast(MSG_COURIER_STATE, senderClientId))
                return;

            try
            {
                int health;
                int destroyedFlag;
                int insideFlag;
                int mode;
                int flashlightFlag;
                reader.ReadValueSafe(out health);
                reader.ReadValueSafe(out destroyedFlag);
                reader.ReadValueSafe(out insideFlag);
                reader.ReadValueSafe(out mode);
                reader.ReadValueSafe(out flashlightFlag);
                Vector3 groundPosition = ReadVector(ref reader);

                bool wasDestroyed = _droneDestroyedThisRound;
                _droneHealth = Mathf.Clamp(health, 0, DRONE_MAX_HEALTH);
                _droneDestroyedThisRound = destroyedFlag != 0;
                _droneIsInside = insideFlag != 0;
                _mode = (DroneMode)mode;
                ApplyFlashlightState(flashlightFlag != 0);
                if (groundPosition != Vector3.zero)
                    _droneGroundPosition = groundPosition;

                if (_droneDestroyedThisRound)
                    DestroyDroneLocal(dropCargo: !wasDestroyed, notifyOwner: false);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed state sync: {ex.Message}");
            }
        }

        private static void OnReceiveDeath(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerBroadcast(MSG_COURIER_DEATH, senderClientId))
                return;

            try
            {
                int insideFlag;
                reader.ReadValueSafe(out insideFlag);
                Vector3 groundPosition = ReadVector(ref reader);

                bool wasDestroyed = _droneDestroyedThisRound;
                _droneHealth = 0;
                _droneDestroyedThisRound = true;
                _droneIsInside = insideFlag != 0;
                if (groundPosition != Vector3.zero)
                    _droneGroundPosition = groundPosition;

                DestroyDroneLocal(dropCargo: !wasDestroyed, notifyOwner: false);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed death sync: {ex.Message}");
            }
        }

        private static void OnReceiveHit(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerBroadcast(MSG_COURIER_HIT, senderClientId))
                return;

            try
            {
                int damageAmount;
                bool bigHit;
                int healthAfter;
                reader.ReadValueSafe(out damageAmount);
                reader.ReadValueSafe(out bigHit);
                reader.ReadValueSafe(out healthAfter);

                _droneHealth = Mathf.Clamp(healthAfter, 0, DRONE_MAX_HEALTH);
                PlayDroneHitLocal(damageAmount, bigHit, _droneHealth);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed hit sync: {ex.Message}");
            }
        }

        private static void OnReceiveFlame(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerBroadcast(MSG_COURIER_FLAME, senderClientId))
                return;

            try
            {
                Vector3 position = ReadVector(ref reader);
                PlayDroneFlameLocal(position);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed flame sync: {ex.Message}");
            }
        }

        private static void OnReceiveGrenadeLaunch(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerBroadcast(MSG_COURIER_GRENADE_LAUNCH, senderClientId))
                return;

            try
            {
                int grenadeId;
                reader.ReadValueSafe(out grenadeId);
                Vector3 start = ReadVector(ref reader);
                Vector3 velocity = ReadVector(ref reader);
                PlayGrenadeLaunchLocal(grenadeId, start, velocity);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed grenade launch sync: {ex.Message}");
            }
        }

        private static void OnReceiveGrenadeExplosion(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerBroadcast(MSG_COURIER_GRENADE_EXPLODE, senderClientId))
                return;

            try
            {
                int grenadeId;
                reader.ReadValueSafe(out grenadeId);
                Vector3 position = ReadVector(ref reader);
                int force;
                reader.ReadValueSafe(out force);
                PlayGrenadeExplosionLocal(grenadeId, position, Mathf.Clamp(force, 1, 20));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed grenade explosion sync: {ex.Message}");
            }
        }

        private static void OnReceiveEnemyAttack(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            // Host-authored only, and the host already played it before broadcasting.
            if (network == null || senderClientId != NetworkManager.ServerClientId || network.IsServer)
                return;

            ulong enemyId;
            Vector3 dronePosition;
            try
            {
                reader.ReadValueSafe(out enemyId);
                dronePosition = ReadVector(ref reader);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed enemy attack sync: {ex.Message}");
                return;
            }

            if (network.SpawnManager == null
                || !network.SpawnManager.SpawnedObjects.TryGetValue(enemyId, out NetworkObject networkObject)
                || networkObject == null)
                return;

            EnemyAI enemy = networkObject.GetComponent<EnemyAI>();
            if (enemy == null || enemy.isEnemyDead)
                return;

            DroneAttacker attacker = ResolveDroneAttacker(enemy);
            if (attacker == DroneAttacker.None || attacker == DroneAttacker.MouthDog)
                return;

            PlayEnemyDroneAttackLocal(enemy, attacker, dronePosition);
        }

        private static void OnReceiveManualPose(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || senderClientId == network.LocalClientId)
                return;
            // #500: off-host a pose is the server's broadcast or relay, never another sender.
            if (!network.IsServer && senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                int playerId;
                int activeFlag;
                int insideFlag;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out activeFlag);
                reader.ReadValueSafe(out insideFlag);
                Vector3 position = ReadVector(ref reader);
                float yaw;
                reader.ReadValueSafe(out yaw);

                // #500: the host takes a pose only from the player it handed the pilot link to (an
                // active one also needs that player's synced Courier tier 3), then relays it.
                if (network.IsServer)
                {
                    PlayerControllerB sender = ResolvePlayerByActualClientId(senderClientId);
                    if (!IsFromHostPilot(MSG_COURIER_MANUAL_POSE, senderClientId, sender, requirePilotTier: activeFlag != 0))
                        return;

                    playerId = (int)sender.playerClientId;
                    RelayManualPose(senderClientId, playerId, activeFlag, insideFlag, position, yaw);
                    // F-DRONE-6: the watchdog's liveness signal. Stamped on the host only, from the
                    // validated pilot, so a silent pilot times out and the link is released.
                    _lastManualPoseAt = activeFlag != 0 ? Time.time : 0f;
                }

                if (_manualControlActive)
                    return;

                if (activeFlag == 0)
                {
                    if (_mode == DroneMode.ManualControl && _droneVisual != null && !_droneDestroyedThisRound)
                    {
                        _mode = DroneMode.IdleAtPlayer;
                        StartIdleFollow(++_routeVersion);
                    }
                    return;
                }

                if (_droneDestroyedThisRound || position == Vector3.zero)
                    return;

                if (_droneVisual == null)
                    EnsureDroneVisualAt(ResampleGroundPoint(position, _droneGroundPosition));

                _ownerPlayerId = playerId;
                _ownerCourierTier = Mathf.Max(_ownerCourierTier, 3);
                _droneIsInside = insideFlag != 0;
                _mode = DroneMode.ManualControl;
                Vector3 previous = _droneVisual.transform.position;
                _droneVisual.transform.position = position;
                _droneGroundPosition = ResolveManualGroundPosition(position);
                Quaternion desired = Quaternion.Euler(0f, yaw, 0f);
                _droneVisual.transform.rotation = Quaternion.Slerp(_droneVisual.transform.rotation, desired, Mathf.Clamp01(Time.deltaTime * VISUAL_TURN_SPEED));
                if ((position - previous).sqrMagnitude > 0.001f)
                    FaceTravelDirection(_droneVisual.transform, position.x - previous.x, position.y - previous.y, position.z - previous.z);
                UpdateCarriedItemTransforms();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed manual pose sync: {ex.Message}");
            }
        }

        private static void OnReceiveManualPickup(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || senderClientId == network.LocalClientId)
                return;
            // #500: off-host a pickup is the server's broadcast or relay, never another sender.
            if (!network.IsServer && senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                int playerId;
                int insideFlag;
                ulong itemId;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out insideFlag);
                reader.ReadValueSafe(out itemId);
                Vector3 pickup = ReadVector(ref reader);
                Vector3 drop = ReadVector(ref reader);

                // The pilot id in the payload is attacker-controlled; the host substitutes the
                // player behind the connection the message arrived on, accepts it only from the
                // player it handed the pilot link to (#500), then fans it out.
                if (network.IsServer)
                {
                    PlayerControllerB sender = ResolvePlayerByActualClientId(senderClientId);
                    if (!IsFromHostPilot(MSG_COURIER_MANUAL_PICKUP, senderClientId, sender, requirePilotTier: false))
                        return;

                    playerId = (int)sender.playerClientId;
                    RelayManualPickup(senderClientId, playerId, insideFlag, itemId, pickup, drop);
                }

                if (_droneDestroyedThisRound || IsCargoWeightFull() || IsAlreadyCarried(itemId))
                    return;

                GrabbableObject item = ResolveItem(itemId);
                if (item == null || WouldExceedCargoWeight(item, GetCarriedCargoWeightPounds()))
                    return;

                _ownerPlayerId = playerId;
                _droneIsInside = insideFlag != 0;
                CourierItemMove move = new CourierItemMove(itemId, pickup, drop, _droneIsInside);
                _manualPickupLockedUntil = Time.time + MANUAL_PICKUP_LOCK_SECONDS;
                StartCoroutineOnAvailableHost(MagnetPickUpScrap(move, move.PickupIsInFactory, item, _routeVersion));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed manual pickup sync: {ex.Message}");
            }
        }

        private static void OnReceiveManualFire(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            try
            {
                // #500 layout: int fireKind, Vector3 target. The shooter is the player behind the
                // connection; HandleManualFireRequest still requires that player to hold the link.
                int fireKind;
                reader.ReadValueSafe(out fireKind);
                Vector3 targetPosition = ReadVector(ref reader);

                PlayerControllerB player = ResolvePlayerByActualClientId(senderClientId);
                if (player == null)
                {
                    WarnRefusedCourierMessage(MSG_COURIER_MANUAL_FIRE, senderClientId);
                    return;
                }

                HandleManualFireRequest((int)player.playerClientId, (ManualFireKind)fireKind, targetPosition);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed manual fire request: {ex.Message}");
            }
        }
#pragma warning restore Harmony003

        private static int BuildCommandFlags(CourierCommand command)
        {
            int flags = 0;
            if (command.PlayerWasInside) flags |= 1;
            if (command.DropIsInShip) flags |= 2;
            if (command.DropIsInFactory) flags |= 4;
            if (command.FlashlightEnabled) flags |= 8;
            return flags;
        }

        private static void WriteVector(ref FastBufferWriter writer, float x, float y, float z)
        {
            writer.WriteValueSafe(x);
            writer.WriteValueSafe(y);
            writer.WriteValueSafe(z);
        }

        private static Vector3 ReadVector(ref FastBufferReader reader)
        {
            float x, y, z;
            reader.ReadValueSafe(out x);
            reader.ReadValueSafe(out y);
            reader.ReadValueSafe(out z);
            return new Vector3(x, y, z);
        }

        private enum DroneMode
        {
            NotSpawned,
            IdleAtPlayer,
            GuardingPlayer,
            FlyingToEntrance,
            AtEntrance,
            ReturningToShip,
            FlyingToPlayer,
            ManualControl,
            PointerAttack,
            PointerFetch
        }

        internal enum CourierCommandType
        {
            None,
            Spawn,
            Dismiss,
            Guard,
            FlyToEntrance,
            ReturnToShip,
            Flashlight,
            Pilot,
            PointerAttack,
            PointerFetch
        }

        private enum ManualFireKind
        {
            Flame = 1,
            Grenade = 2
        }

        // #500: vanilla enemies that can attack the drone; indexes DroneAttackProfiles.
        private enum DroneAttacker
        {
            None,
            SandSpider,
            Crawler,
            HoarderBug,
            Baboon,
            Nutcracker,
            Flowerman,
            MouthDog
        }

        private readonly struct DroneAttackProfile
        {
            internal readonly float Reach;
            internal readonly float Cooldown;
            internal readonly int Damage;

            internal DroneAttackProfile(float reach, float cooldown, int damage)
            {
                Reach = reach;
                Cooldown = cooldown;
                Damage = damage;
            }
        }

        private readonly struct EntranceScrapCandidate
        {
            internal readonly GrabbableObject Item;
            internal readonly float DistanceSqr;

            internal EntranceScrapCandidate(GrabbableObject item, float distanceSqr)
            {
                Item = item;
                DistanceSqr = distanceSqr;
            }
        }

        private struct PointerTarget
        {
            internal CourierCommandType CommandType;
            internal NetworkObjectReference TargetReference;
            internal Color ReticleColor;
            internal UnityEngine.Object Source;

            internal bool IsValid => CommandType == CourierCommandType.PointerAttack || CommandType == CourierCommandType.PointerFetch;
        }
        internal readonly struct TabletCommandEntry
        {
            internal readonly CourierCommandType Type;
            internal readonly string Label;
            internal readonly bool Available;
            internal readonly string Reason;

            internal TabletCommandEntry(CourierCommandType type, string label, bool available, string reason)
            {
                Type = type;
                Label = label;
                Available = available;
                Reason = reason;
            }
        }

        private sealed class CourierCommand
        {
            internal CourierCommandType Type;
            internal int CourierTier;
            internal int PlayerId = -1;
            internal bool PlayerWasInside;
            internal bool DropIsInShip;
            internal bool DropIsInFactory;
            internal bool FlashlightEnabled;
            internal Vector3 DroneStartPosition;
            internal Vector3 EntrancePosition;
            internal Vector3 OutsideEntrancePosition;
            internal Vector3 InsideEntrancePosition;
            internal Vector3 DropPosition;
            internal readonly List<CourierItemMove> Items = new List<CourierItemMove>();
        }

        private sealed class MainEntranceRoute
        {
            internal Vector3 OutsidePosition;
            internal Vector3 InsidePosition;
        }

        private struct CourierItemMove
        {
            internal readonly ulong NetworkObjectId;
            internal readonly Vector3 PickupPosition;
            internal readonly Vector3 DropPosition;
            internal readonly bool PickupIsInFactory;

            internal CourierItemMove(ulong networkObjectId, Vector3 pickupPosition, Vector3 dropPosition, bool pickupIsInFactory)
            {
                NetworkObjectId = networkObjectId;
                PickupPosition = pickupPosition;
                DropPosition = dropPosition;
                PickupIsInFactory = pickupIsInFactory;
            }
        }

        private struct RendererPresentationState
        {
            internal readonly Renderer Renderer;
            internal readonly bool Enabled;
            internal readonly UnityEngine.Rendering.ShadowCastingMode ShadowCastingMode;
            internal readonly int Layer;

            internal RendererPresentationState(Renderer renderer, bool enabled, UnityEngine.Rendering.ShadowCastingMode shadowCastingMode, int layer)
            {
                Renderer = renderer;
                Enabled = enabled;
                ShadowCastingMode = shadowCastingMode;
                Layer = layer;
            }
        }

        private struct CarriedCourierItem
        {
            internal readonly CourierItemMove Move;
            internal readonly GrabbableObject Item;
            internal readonly Vector3 WorldScale;
            internal readonly Vector3 CarryOffset;
            internal readonly Quaternion CarryLocalRotation;
            internal readonly bool BodyWasKinematic;
            internal readonly bool BodyUsedGravity;

            internal CarriedCourierItem(CourierItemMove move, GrabbableObject item, Vector3 worldScale, Vector3 carryOffset, Quaternion carryLocalRotation, bool bodyWasKinematic, bool bodyUsedGravity)
            {
                Move = move;
                Item = item;
                WorldScale = worldScale;
                CarryOffset = carryOffset;
                CarryLocalRotation = carryLocalRotation;
                BodyWasKinematic = bodyWasKinematic;
                BodyUsedGravity = bodyUsedGravity;
            }
        }
    }
}
