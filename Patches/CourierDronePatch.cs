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

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class CourierDronePatch
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

        private const int DRONE_FLAME_HIT_ID = 74043;
        private const int DRONE_MAX_HEALTH = 30;
        private const int DRONE_ENEMY_CONTACT_DAMAGE = 10;
        private const float DRONE_PROXIMITY_AGGRO_RADIUS = 5f;
        private const float DRONE_ENEMY_ATTACK_RANGE = 2.1f;
        private const float DRONE_ENEMY_ATTACK_COOLDOWN = 1.1f;
        private const float DRONE_ENEMY_FACE_RANGE_MULTIPLIER = 1.5f;
        private const float DRONE_ENEMY_FACE_TURN_DEGREES_PER_SECOND = 360f;
        private const float DRONE_PROXIMITY_THREAT_SECONDS = 2.5f;
        private const float DRONE_ATTACK_THREAT_SECONDS = 8f;
        private const float DRONE_DEATH_VISUAL_SECONDS = 5f;
        private const float DRONE_HIT_JOLT_DISTANCE = 0.12f;
        private const float DRONE_HIT_JOLT_SECONDS = 0.25f;
        private const float DRONE_ATTACK_WARNING_INTERVAL = 5f;
        private const float ENEMY_SCAN_INTERVAL = 0.25f;
        private const float FLAME_TRIGGER_RANGE = 3.5f;
        private const float FLAME_GUARD_RANGE = 4.5f;
        private const float FLAME_DAMAGE_RADIUS = 3.2f;
        private const int FLAME_DAMAGE = 1;
        private const int FLAME_TICKS = 3;
        private const float FLAME_COOLDOWN = 5f;
        private const float FLAME_DAMAGE_WINDOW_SECONDS = 1.2f;
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
        private const float MANUAL_PICKUP_LOCK_SECONDS = 1.25f;
        private const float POINTER_COMMAND_RANGE = 45f;
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
        private static readonly Dictionary<ulong, float> NextDroneHitByEnemy = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, GrabbableObject> KnownGrabbableItems = new Dictionary<ulong, GrabbableObject>();
        private static readonly Dictionary<int, GameObject> ActiveGrenadeVisuals = new Dictionary<int, GameObject>();
        private static readonly List<Vector3> CachedFollowPath = new List<Vector3>();

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
        private static int _lastEnemyFacingFrame = -1;
        private static float _nextFlameAt;
        private static float _nextGrenadeAt;
        private static float _combatLandedUntil;
        private static int _nextGrenadeId = 1;
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
            _handlersRegistered = false;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            UpdateEnemyDroneFacingOncePerFrame();
            UpdateEnemyDroneAggroScanOncePerFrame();

            if (!IsLocalPlayer(__instance))
                return;

            UpdatePointerTargeting(__instance);

            if (_manualControlActive)
                TickManualControl(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.Crouch), new[] { typeof(bool) })]
        [HarmonyPrefix]
        private static bool PrePlayerCrouch(PlayerControllerB __instance, bool crouch)
        {
            if (!_manualControlActive || !crouch || !IsLocalPlayer(__instance))
                return true;

            return false;
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.DoAIInterval))]
        [HarmonyPostfix]
        private static void PostEnemyDoAIInterval(EnemyAI __instance)
        {
            try
            {
                if (!IsServerAuthority() || __instance == null || __instance.isEnemyDead || !CanDroneBeDamaged())
                    return;
                if (!EnemySharesDroneSpace(__instance) || !IsDroneThreat(__instance))
                    return;

                Vector3 dronePosition = GetDroneTargetPosition();
                if (dronePosition == Vector3.zero)
                    return;
                if (PlayerHasPriorityOverDrone(__instance, dronePosition))
                    return;

                __instance.movingTowardsTargetPlayer = false;
                __instance.targetPlayer = null;
                SteerEnemyTowardDrone(__instance);
            }
            catch (Exception ex)
            {
                string enemyName = __instance != null ? __instance.GetType().Name : "<null>";
                Plugin.Log?.LogDebug($"Courier Drone enemy DoAIInterval postfix skipped for {enemyName}: {ex.Message}");
            }
        }

        private static bool PlayerHasPriorityOverDrone(EnemyAI enemy, Vector3 dronePosition)
        {
            PlayerControllerB target = enemy != null ? enemy.targetPlayer : null;
            if (target == null || target.isPlayerDead || target.transform == null || enemy.transform == null)
                return false;

            float playerDistanceSqr = (target.transform.position - enemy.transform.position).sqrMagnitude;
            float droneDistanceSqr = (dronePosition - enemy.transform.position).sqrMagnitude;
            return playerDistanceSqr < droneDistanceSqr;
        }



        private static void UpdatePointerTargeting(PlayerControllerB player)
        {
            if (!CanPointerCommandBeActive(player))
            {
                FieldTabletScreenRuntime.SetDronePointerReticle(false, Color.clear);
                return;
            }

            PointerTarget target = FindPointerTarget(player);
            if (!target.IsValid)
            {
                FieldTabletScreenRuntime.SetDronePointerReticle(false, Color.clear);
                return;
            }

            string label = BuildPointerReticleLabel(target.CommandType);
            FieldTabletScreenRuntime.SetDronePointerReticle(true, target.ReticleColor, 0f, label);
            if (Time.time < _nextPointerCommandAt || !WasPointerCommandPressedThisFrame())
                return;

            _nextPointerCommandAt = Time.time + POINTER_COMMAND_COOLDOWN_SECONDS;
            FieldTabletScreenRuntime.SetDronePointerReticle(true, target.ReticleColor, 0.18f, label);
            SendPointerCommand((int)player.playerClientId, CourierDroneUpgrade.GetTier(), target.CommandType, target.TargetReference);
        }

        private static string BuildPointerReticleLabel(CourierCommandType commandType)
        {
            string action = commandType == CourierCommandType.PointerAttack ? "ATTACK" : "FETCH";
            return "[" + Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.CourierDroneCommand, "MMB") + "] " + action;
        }

        private static bool CanPointerCommandBeActive(PlayerControllerB player)
        {
            return CourierDroneUpgrade.IsUnlocked()
                && _droneVisual != null
                && !_droneDestroyedThisRound
                && _mode != DroneMode.NotSpawned
                && _mode != DroneMode.ManualControl
                && !_manualControlActive
                && CanUsePointer(player);
        }

        private static bool CanUsePointer(PlayerControllerB player)
        {
            return player != null
                && !player.isPlayerDead
                && !player.isTypingChat
                && !player.inTerminalMenu
                && (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen);
        }

        private static PointerTarget FindPointerTarget(PlayerControllerB player)
        {
            Camera camera = player != null ? player.gameplayCamera : null;
            if (camera == null)
                camera = Camera.main;
            if (camera == null)
                return default;

            Ray ray = new Ray(camera.transform.position, camera.transform.forward);
            RaycastHit[] hits = Physics.RaycastAll(ray, POINTER_COMMAND_RANGE, GetPointerRaycastMask(), QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0)
                return default;

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null)
                    continue;

                Transform hitTransform = collider.transform;
                if (hitTransform != null && player != null && hitTransform.IsChildOf(player.transform))
                    continue;
                if (hitTransform != null && _droneVisual != null && hitTransform.IsChildOf(_droneVisual.transform))
                    continue;
                if (hitTransform != null && hitTransform.name.IndexOf("FieldTablet", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                if (TryBuildPointerEnemyTarget(collider, out PointerTarget target))
                    return target;
                if (TryBuildPointerItemTarget(collider, out target))
                    return target;
                if (!collider.isTrigger)
                    break;
            }

            return default;
        }

        private static int GetPointerRaycastMask()
        {
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            mask |= 1 << 6;
            mask |= 1 << 9;
            mask |= 1 << 19;
            mask |= 1 << 23;
            return mask;
        }

        private static bool TryBuildPointerEnemyTarget(Collider collider, out PointerTarget target)
        {
            target = default;
            EnemyAI enemy = ResolveEnemyFromCollider(collider);
            if (enemy == null || enemy.isEnemyDead || !EnemySharesDroneSpace(enemy))
                return false;

            NetworkObject networkObject = enemy.NetworkObject != null ? enemy.NetworkObject : enemy.GetComponentInParent<NetworkObject>();
            if (networkObject == null)
                return false;

            target = new PointerTarget
            {
                CommandType = CourierCommandType.PointerAttack,
                TargetReference = new NetworkObjectReference(networkObject),
                ReticleColor = PointerEnemyColor
            };
            return true;
        }

        private static bool TryBuildPointerItemTarget(Collider collider, out PointerTarget target)
        {
            target = default;
            GrabbableObject item = collider.GetComponentInParent<GrabbableObject>();
            if (!IsPointerFetchEligible(item))
                return false;

            NetworkObject networkObject = item.NetworkObject != null ? item.NetworkObject : item.GetComponentInParent<NetworkObject>();
            if (networkObject == null)
                return false;

            target = new PointerTarget
            {
                CommandType = CourierCommandType.PointerFetch,
                TargetReference = new NetworkObjectReference(networkObject),
                ReticleColor = PointerItemColor
            };
            return true;
        }

        private static EnemyAI ResolveEnemyFromCollider(Collider collider)
        {
            if (collider == null)
                return null;

            EnemyAICollisionDetect collisionDetect = collider.GetComponentInParent<EnemyAICollisionDetect>();
            if (collisionDetect != null && collisionDetect.mainScript != null)
                return collisionDetect.mainScript;

            return collider.GetComponentInParent<EnemyAI>();
        }

        private static bool WasPointerCommandPressedThisFrame()
        {
            return Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.CourierDroneCommand);
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

        private static string GetNextDroneActionText()
        {
            if (_droneDestroyedThisRound)
                return "NEXT ROUND";

            DroneMode effectiveMode = _droneVisual == null ? DroneMode.NotSpawned : _mode;
            switch (effectiveMode)
            {
                case DroneMode.NotSpawned:
                    return "DEPLOY";
                case DroneMode.IdleAtPlayer:
                case DroneMode.GuardingPlayer:
                    return "SEND TO ENTRANCE";
                case DroneMode.AtEntrance:
                    return "RETURN TO SHIP";
                case DroneMode.FlyingToEntrance:
                case DroneMode.ReturningToShip:
                case DroneMode.FlyingToPlayer:
                case DroneMode.ManualControl:
                case DroneMode.PointerAttack:
                case DroneMode.PointerFetch:
                    return "BUSY";
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

        internal static bool TryCommandFromFieldTablet(PlayerControllerB player)
        {
            return TryExecuteTabletCommand(player, ResolveContextualSummonDismissType());
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

            SendRequest((int)player.playerClientId, commandType);
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

            _manualControlCameraActivated = true;
            BroadcastManualPose(active: true);
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

            float forwardExtent = 0.62f;
            Renderer[] renderers = GetCachedDroneRenderers();
            if (renderers != null && renderers.Length > 0)
            {
                float furthest = 0f;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || !renderer.enabled)
                        continue;

                    Bounds bounds = renderer.bounds;
                    Vector3 extents = bounds.extents;
                    Vector3 centerOffset = bounds.center - dronePosition;
                    Vector3[] corners =
                    {
                        centerOffset + new Vector3(extents.x, extents.y, extents.z),
                        centerOffset + new Vector3(extents.x, extents.y, -extents.z),
                        centerOffset + new Vector3(extents.x, -extents.y, extents.z),
                        centerOffset + new Vector3(extents.x, -extents.y, -extents.z),
                        centerOffset + new Vector3(-extents.x, extents.y, extents.z),
                        centerOffset + new Vector3(-extents.x, extents.y, -extents.z),
                        centerOffset + new Vector3(-extents.x, -extents.y, extents.z),
                        centerOffset + new Vector3(-extents.x, -extents.y, -extents.z)
                    };

                    for (int c = 0; c < corners.Length; c++)
                        furthest = Mathf.Max(furthest, Vector3.Dot(corners[c], yawForward));
                }

                forwardExtent = Mathf.Clamp(furthest + MANUAL_CAMERA_CLEARANCE, 0.68f, 1.75f);
            }

            return dronePosition + yawForward * forwardExtent + Vector3.up * 0.11f;
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

        private static void UpdateManualHud()
        {
            if (_manualHudRoot == null)
                return;

            string cargo = $"{FormatCargoWeight(GetCarriedCargoWeightPounds())} ({CarriedItems.Count})";
            float altitude = _droneVisual != null
                ? Mathf.Max(0f, _droneVisual.transform.position.y - _droneGroundPosition.y)
                : 0f;

            if (_manualHudStatusText != null)
                _manualHudStatusText.text = "COURIER DRONE LINK\nREMOTE LINK ACTIVE";
            if (_manualHudCargoText != null)
                _manualHudCargoText.text = $"CARGO {cargo}\nHP {Mathf.Clamp(_droneHealth, 0, DRONE_MAX_HEALTH)}/{DRONE_MAX_HEALTH}";
            if (_manualHudTelemetryText != null)
                _manualHudTelemetryText.text = $"ALT {altitude:0.0}m\nMODE {( _droneIsInside ? "INTERIOR" : "EXTERIOR" )}";
            UpdateManualStartupOverlay();
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

            SendManualFireRequest(playerId, fireKind, targetPosition);
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

        private static List<CourierItemMove> FindEntranceScrap(CourierCommand command, List<Vector3> searchPositions = null)
        {
            List<CourierItemMove> moves = new List<CourierItemMove>();
            List<Vector3> positions = searchPositions ?? FindMainEntranceSearchPositions(command);
            if (positions.Count == 0)
                return moves;

            GrabbableObject[] items = ScanGrabbableObjectsForCommandOrSweep();
            Array.Sort(items, (a, b) =>
            {
                float da = a == null ? float.MaxValue : DistanceToClosestPointSqr(a.transform.position, positions);
                float db = b == null ? float.MaxValue : DistanceToClosestPointSqr(b.transform.position, positions);
                return da.CompareTo(db);
            });

            float radiusSqr = CourierDroneUpgrade.PICKUP_RADIUS * CourierDroneUpgrade.PICKUP_RADIUS;
            float reservedWeightLb = GetReservedCargoWeightPounds();
            for (int i = 0; i < items.Length && moves.Count < CourierDroneUpgrade.MAX_ITEMS_PER_SWEEP; i++)
            {
                GrabbableObject item = items[i];
                if (!IsCourierEligibleEntranceScrap(item, positions, radiusSqr))
                    continue;
                if (IsAlreadyCarried(item.NetworkObjectId) || IsAlreadyPlanned(item.NetworkObjectId))
                    continue;
                if (WouldExceedCargoWeight(item, reservedWeightLb))
                    continue;

                Vector3 drop = command.DropPosition + new Vector3(
                    UnityEngine.Random.Range(-1.4f, 1.4f),
                    0.15f,
                    UnityEngine.Random.Range(-1.0f, 1.0f));
                moves.Add(new CourierItemMove(item.NetworkObjectId, item.transform.position, drop, item.isInFactory));
                reservedWeightLb += GetItemWeightPounds(item);
            }

            return moves;
        }

        private static GrabbableObject[] ScanGrabbableObjectsForCommandOrSweep()
        {
            GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            CacheKnownGrabbableItems(items);
            return items;
        }

        private static void CacheKnownGrabbableItems(GrabbableObject[] items)
        {
            if (items == null)
                return;

            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item != null)
                    KnownGrabbableItems[item.NetworkObjectId] = item;
            }
        }

        private static void ApplyCommand(CourierCommand command)
        {
            if (command == null || command.Type == CourierCommandType.None)
                return;
            if (_droneDestroyedThisRound)
                return;
            if (command.CourierTier > 0)
                _ownerCourierTier = command.CourierTier;
            if (command.Type != CourierCommandType.Flashlight)
                ClearPointerTargetState();

            switch (command.Type)
            {
                case CourierCommandType.Spawn:
                    SpawnDroneAt(command);
                    break;
                case CourierCommandType.Dismiss:
                    StartDismiss(command);
                    break;
                case CourierCommandType.Guard:
                    StartGuardingPlayer(command);
                    break;
                case CourierCommandType.FlyToEntrance:
                    StartFlyToEntrance(command);
                    break;
                case CourierCommandType.ReturnToShip:
                    StartReturnToShip(command);
                    break;
                case CourierCommandType.Flashlight:
                    ApplyFlashlightState(command.FlashlightEnabled);
                    _commandCooldownEnd = Time.time + GetCommandCooldownSeconds(command.Type);
                    BroadcastDroneState();
                    break;
                case CourierCommandType.Pilot:
                    StartPilotControl(command);
                    break;
            }
        }

        private static float GetCommandCooldownSeconds(CourierCommandType commandType)
        {
            return commandType == CourierCommandType.Guard || commandType == CourierCommandType.Flashlight || commandType == CourierCommandType.Pilot
                ? 1f
                : CourierDroneUpgrade.COMMAND_COOLDOWN_SECONDS;
        }

        private static void StartPilotControl(CourierCommand command)
        {
            if (command == null || _droneVisual == null || _droneDestroyedThisRound)
                return;

            if (_mode != DroneMode.ManualControl)
                _manualPreviousMode = _mode;

            _ownerPlayerId = command.PlayerId;
            _ownerCourierTier = Mathf.Max(_ownerCourierTier, command.CourierTier);
            _mode = DroneMode.ManualControl;
            _isFollowingPlayer = false;
            _followSettled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            _commandCooldownEnd = Time.time + GetCommandCooldownSeconds(command.Type);
            ++_routeVersion;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local != null && (int)local.playerClientId == command.PlayerId)
                TryStartManualControl(local);

            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                BroadcastDroneState();
        }

        private static void ApplyFlashlightState(bool enabled)
        {
            _flashlightEnabled = enabled;
            if (_droneVisual != null)
                CourierDroneRuntimeAssets.SetFlashlight(_droneVisual, enabled);
        }

        private static void SpawnDroneAt(CourierCommand command)
        {
            if (_droneDestroyedThisRound)
                return;

            _routeVersion++;
            PlannedItems.Clear();
            CarriedItems.Clear();
            _reportedNoEntranceScrap = false;
            _ownerPlayerId = command.PlayerId;
            _ownerCourierTier = Mathf.Max(command.CourierTier, 1);
            _droneIsInside = command.PlayerWasInside;
            _droneGroundPosition = command.DroneStartPosition;
            if (_droneHealth <= 0)
                _droneHealth = DRONE_MAX_HEALTH;
            _mode = DroneMode.IdleAtPlayer;
            _nextDroneAttackWarningAt = 0f;

            EnsureDroneVisualAt(command.DroneStartPosition);
            CourierDroneRuntimeAssets.PlayMaterializeIn(_droneVisual);
            CourierDroneRuntimeAssets.PlayLiftOff(_droneVisual);
            ApplyFlashlightState(_flashlightEnabled);
            _combatLandedUntil = 0f;
            CourierDroneRuntimeAssets.PlayWakeupSound(_droneVisual);
            _commandCooldownEnd = Time.time + GetCommandCooldownSeconds(command.Type);
            DisplayOwnerTip(command.PlayerId, "Drone deployed. It will stay with you.", isWarning: false);
            int version = _routeVersion;
            StartIdleFollow(version);
        }

        private static void StartDismiss(CourierCommand command)
        {
            if (_droneVisual == null)
                return;

            StopManualControl(resumeDrone: false, notifyOwner: false);
            EnsureDroneVisualAt(command.DroneStartPosition);
            CourierDroneRuntimeAssets.PlayRecallSound(_droneVisual);
            _combatLandedUntil = 0f;
            _followSettled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            _ownerPlayerId = command.PlayerId;
            _commandCooldownEnd = Time.time + GetCommandCooldownSeconds(command.Type);
            DisplayOwnerTip(command.PlayerId, "Drone dismissing.", isWarning: false);

            int version = ++_routeVersion;
            StartCoroutineOnAvailableHost(DismissRoutine(command, version));
        }

        private static void StartGuardingPlayer(CourierCommand command)
        {
            if (_droneVisual == null)
                return;

            StopManualControl(resumeDrone: true, notifyOwner: false);
            _routeVersion++;
            _mode = DroneMode.GuardingPlayer;
            _ownerPlayerId = command.PlayerId;
            if (command.CourierTier > 0)
                _ownerCourierTier = command.CourierTier;
            _combatLandedUntil = 0f;
            _nextGuardScrapScanAt = 0f;
            _followSettled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            _commandCooldownEnd = Time.time + GetCommandCooldownSeconds(command.Type);
            DisplayOwnerTip(command.PlayerId, "Guard mode active.", isWarning: false);
            StartCoroutineOnAvailableHost(GuardFollowRoutine(_routeVersion));
        }

        private static void StartFlyToEntrance(CourierCommand command)
        {
            if (command.EntrancePosition == Vector3.zero)
            {
                DisplayOwnerTip(command.PlayerId, "Main entrance route not found.", isWarning: true);
                return;
            }

            EnsureDroneVisualAt(command.DroneStartPosition);
            CourierDroneRuntimeAssets.PlaySendToEntranceSound(_droneVisual);
            _combatLandedUntil = 0f;
            _followSettled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            PlannedItems.Clear();
            PlannedItems.AddRange(command.Items);
            _reportedNoEntranceScrap = false;
            _nextEntranceScrapScanAt = 0f;
            _ownerPlayerId = command.PlayerId;
            _commandCooldownEnd = Time.time + GetCommandCooldownSeconds(command.Type);
            DisplayOwnerTip(
                command.PlayerId,
                PlannedItems.Count > 0
                    ? $"Scanning entrance. {PlannedItems.Count} scrap queued, {FormatCargoWeight(GetPlannedCargoWeightPounds())} planned."
                    : "Scanning entrance for scrap.",
                isWarning: false);

            int version = ++_routeVersion;
            StartCoroutineOnAvailableHost(FlyToEntranceRoutine(command, version));
        }

        private static void StartReturnToShip(CourierCommand command)
        {
            EnsureDroneVisualAt(command.DroneStartPosition);
            CourierDroneRuntimeAssets.PlayRecallSound(_droneVisual);
            _combatLandedUntil = 0f;
            _followSettled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            command.Items.Clear();
            command.Items.AddRange(PlannedItems);
            _ownerPlayerId = command.PlayerId;
            _commandCooldownEnd = Time.time + GetCommandCooldownSeconds(command.Type);
            DisplayOwnerTip(
                command.PlayerId,
                CarriedItems.Count > 0
                    ? $"Returning cargo {FormatCargoWeight(GetCarriedCargoWeightPounds())} ({CarriedItems.Count} items)."
                    : "Returning to ship.",
                isWarning: false);

            int version = ++_routeVersion;
            StartCoroutineOnAvailableHost(ReturnToShipRoutine(command, version));
        }


        private static bool HandlePointerCommand(int playerId, int courierTier, CourierCommandType commandType, NetworkObjectReference targetReference)
        {
            if (!IsServerAuthority())
                return false;

            PlayerControllerB player = ResolvePlayer(playerId);
            if (!CanServerAcceptPointerCommand(player))
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

        private static bool CanServerAcceptPointerCommand(PlayerControllerB player)
        {
            return CourierDroneUpgrade.IsUnlocked()
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
            MarkDroneThreat(enemy, POINTER_PRIORITY_SECONDS);
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
                _ownerCourierTier = Mathf.Max(_ownerCourierTier, courierTier);
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

                MarkDroneThreat(enemy, DRONE_ATTACK_THREAT_SECONDS);
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

        private static IEnumerator FlyToEntranceRoutine(CourierCommand command, int version)
        {
            _mode = DroneMode.FlyingToEntrance;
            List<Vector3> route = BuildRouteToEntrance(command);
            yield return MoveAlongRoute(command, route, version, pickUpAtEntrance: false);
            if (version != _routeVersion || _lastRouteMoveBlocked)
                yield break;

            _mode = DroneMode.AtEntrance;
            if (route.Count > 0)
            {
                _droneGroundPosition = route[route.Count - 1];
                _droneIsInside = IsSameRoutePoint(_droneGroundPosition, command.InsideEntrancePosition);
            }

            yield return WaitAtEntranceRoutine(command, version);
        }

        private static IEnumerator DismissRoutine(CourierCommand command, int version)
        {
            _mode = DroneMode.FlyingToPlayer;
            List<Vector3> route = BuildRouteToDrop(command, includeShipApproach: false);
            yield return MoveAlongRoute(command, route, version, pickUpAtEntrance: false);
            if (version != _routeVersion || _lastRouteMoveBlocked)
                yield break;

            CourierDroneRuntimeAssets.PlayLand(_droneVisual);
            _combatLandedUntil = Time.time + 0.35f;
            yield return new WaitForSeconds(0.35f);
            if (version != _routeVersion)
                yield break;

            float dematerializeSeconds = CourierDroneRuntimeAssets.PlayDematerializeOut(_droneVisual);
            if (dematerializeSeconds > 0.001f)
            {
                yield return new WaitForSeconds(dematerializeSeconds);
                if (version != _routeVersion)
                    yield break;
            }

            int dropped = DropCarriedScrap(command);
            PlannedItems.Clear();
            CarriedItems.Clear();
            DroneThreats.Clear();
            NextDroneHitByEnemy.Clear();
            DestroyActiveGrenadeVisuals();
            GameObject visual = _droneVisual;
            _droneVisual = null;
            ClearDroneRendererCache();
            _mode = DroneMode.NotSpawned;
            _droneGroundPosition = Vector3.zero;
            _droneIsInside = false;
            _isFollowingPlayer = false;
            _followSettled = false;
            _flashlightEnabled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            if (visual != null)
                UnityEngine.Object.Destroy(visual);
            DisplayOwnerTip(command.PlayerId, dropped > 0 ? $"Dropped {dropped} scrap." : "Drone dismissed.", isWarning: false);
        }

        private static IEnumerator ReturnToShipRoutine(CourierCommand command, int version)
        {
            _mode = DroneMode.ReturningToShip;
            List<Vector3> route = BuildRouteToDrop(command, includeShipApproach: true);
            yield return MoveAlongRoute(command, route, version, pickUpAtEntrance: false);
            if (version != _routeVersion || _lastRouteMoveBlocked)
                yield break;

            CourierDroneRuntimeAssets.PlayLand(_droneVisual);
            _combatLandedUntil = Time.time + 0.35f;
            yield return new WaitForSeconds(0.35f);
            if (version != _routeVersion)
                yield break;

            int recovered = DropCarriedScrap(command);
            PlannedItems.Clear();
            _mode = DroneMode.IdleAtPlayer;
            _droneGroundPosition = command.DropPosition;
            _droneIsInside = command.DropIsInFactory;
            _isFollowingPlayer = false;
            DisplayOwnerTip(command.PlayerId, recovered > 0 ? $"Delivered {recovered} scrap." : "Returned empty.", isWarning: recovered <= 0);
            CourierDroneRuntimeAssets.PlayLiftOff(_droneVisual);
            ApplyFlashlightState(_flashlightEnabled);
            _combatLandedUntil = 0f;
        }

        private static void StartIdleFollow(int version)
        {
            _followSettled = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
            StartCoroutineOnAvailableHost(IdleFollowRoutine(version));
        }

        private static IEnumerator IdleFollowRoutine(int version)
        {
            while (version == _routeVersion && _mode == DroneMode.IdleAtPlayer)
            {
                UpdateOwnerFollow();
                yield return null;
            }

            _isFollowingPlayer = false;
        }

        private static IEnumerator GuardFollowRoutine(int version)
        {
            while (version == _routeVersion && _mode == DroneMode.GuardingPlayer)
            {
                UpdateOwnerFollow();
                yield return TryGuardPickUpScrap(version);
                yield return null;
            }

            _isFollowingPlayer = false;
        }

        private static void UpdateOwnerFollow()
        {
            if (_droneVisual == null)
                return;

            if (_mode != DroneMode.IdleAtPlayer && _mode != DroneMode.GuardingPlayer)
            {
                _isFollowingPlayer = false;
                _followSettled = false;
                ClearFollowPathCache();
                ResetFollowStuckState();
                UpdateCarriedItemTransforms();
                return;
            }
            PlayerControllerB owner = ResolvePlayer(_ownerPlayerId);
            if (owner == null || owner.isPlayerDead || owner.transform == null || owner.isInsideFactory != _droneIsInside)
            {
                _isFollowingPlayer = false;
                _followSettled = false;
                ClearFollowPathCache();
                ResetFollowStuckState();
                UpdateCarriedItemTransforms();
                return;
            }

            Vector3 currentGround = CurrentDroneGroundPositionOr(_droneGroundPosition);
            Vector3 targetGround = FindDroneFollowPosition(owner);
            if (currentGround == Vector3.zero || targetGround == Vector3.zero)
            {
                _isFollowingPlayer = false;
                ResetFollowStuckState();
                ApplyIdleSway(currentGround, owner);
                UpdateCarriedItemTransforms();
                return;
            }

            float distanceToTarget = Vector3.Distance(currentGround, targetGround);
            if (distanceToTarget > FOLLOW_WARP_DISTANCE)
            {
                _isFollowingPlayer = false;
                _followSettled = true;
                ClearFollowPathCache();
                _droneGroundPosition = ResampleGroundPoint(targetGround, targetGround);
                _droneVisual.transform.position = ToFlightPosition(_droneGroundPosition);
                UpdateCarriedItemTransforms();
                return;
            }

            if (_followSettled)
            {
                if (distanceToTarget <= FOLLOW_RECHASE_DISTANCE)
                {
                    _isFollowingPlayer = false;
                    ResetFollowStuckState();
                    ApplyIdleSway(currentGround, owner);
                    UpdateCarriedItemTransforms();
                    return;
                }

                _followSettled = false;
            }

            if (distanceToTarget <= FOLLOW_SETTLE_DISTANCE)
            {
                _isFollowingPlayer = false;
                _followSettled = true;
                ClearFollowPathCache();
                ResetFollowStuckState();
                ApplyIdleSway(currentGround, owner);
                UpdateCarriedItemTransforms();
                return;
            }

            _isFollowingPlayer = true;
            List<Vector3> path = GetFollowPath(currentGround, targetGround);
            Vector3 nextTarget = SelectFollowWaypoint(path, currentGround, targetGround);
            if (nextTarget == Vector3.zero)
            {
                _isFollowingPlayer = false;
                UpdateFollowStuckRescue(owner, currentGround, currentGround, distanceToTarget);
                ApplyIdleSway(currentGround, owner);
                UpdateCarriedItemTransforms();
                return;
            }

            float speed = distanceToTarget > FOLLOW_CATCHUP_DISTANCE ? FOLLOW_SPEED * 1.55f : FOLLOW_SPEED;
            Vector3 nextGround = Vector3.SmoothDamp(
                currentGround,
                nextTarget,
                ref _followSmoothVelocity,
                FOLLOW_MOVE_SMOOTH_TIME,
                speed,
                Time.deltaTime);
            nextGround = ResampleGroundPoint(nextGround, currentGround);
            Vector3 previousPosition = _droneVisual.transform.position;
            Vector3 nextBasePosition = ToSmoothedFlightPosition(nextGround, previousPosition.y);
            if (TryResolveClutterMove(previousPosition, nextBasePosition, nextGround, out Vector3 adjustedPosition, out bool blocked))
            {
                nextBasePosition = adjustedPosition;
                nextGround = ResampleGroundPoint(new Vector3(adjustedPosition.x, nextGround.y, adjustedPosition.z), currentGround);
                if (blocked)
                    _followSmoothVelocity = Vector3.Lerp(_followSmoothVelocity, Vector3.zero, Mathf.Clamp01(Time.deltaTime * 2.5f));
            }

            Vector3 nextPosition = ApplyFlightBob(nextBasePosition);
            _droneVisual.transform.position = nextPosition;
            Vector3 delta = nextBasePosition - previousPosition;
            FaceTravelDirection(_droneVisual.transform, delta.x, delta.y, delta.z);
            _droneGroundPosition = nextGround;
            UpdateFollowStuckRescue(owner, currentGround, _droneGroundPosition, distanceToTarget);
            UpdateCarriedItemTransforms();
        }

        private static void UpdateFollowStuckRescue(PlayerControllerB owner, Vector3 previousGround, Vector3 currentGround, float distanceToTarget)
        {
            if (_stuckRescueActive)
                return;
            if (owner == null || owner.transform == null || currentGround == Vector3.zero || _followSettled || distanceToTarget <= FOLLOW_SETTLE_DISTANCE)
            {
                ResetFollowStuckState();
                return;
            }

            float ownerDistance = Vector3.Distance(currentGround, owner.transform.position);
            if (ownerDistance <= STUCK_RESCUE_OWNER_DISTANCE)
            {
                ResetFollowStuckState();
                return;
            }

            if (_stuckWindowStartGround == Vector3.zero || _stuckWindowStartAt <= 0f)
            {
                _stuckWindowStartGround = previousGround != Vector3.zero ? previousGround : currentGround;
                _stuckWindowStartAt = Time.time;
                return;
            }

            if (Time.time - _stuckWindowStartAt < STUCK_RESCUE_WINDOW_SECONDS)
                return;

            float progress = Vector3.Distance(currentGround, _stuckWindowStartGround);
            if (progress >= STUCK_RESCUE_MIN_PROGRESS)
            {
                _stuckWindowStartGround = currentGround;
                _stuckWindowStartAt = Time.time;
                _stuckRepathIssued = false;
                return;
            }

            if (!_stuckRepathIssued)
            {
                ClearFollowPathCache();
                _stuckRepathIssued = true;
                _stuckWindowStartGround = currentGround;
                _stuckWindowStartAt = Time.time;
                return;
            }

            Vector3 rescueGround = FindDroneFollowPosition(owner);
            if (rescueGround == Vector3.zero)
            {
                _stuckWindowStartGround = currentGround;
                _stuckWindowStartAt = Time.time;
                return;
            }

            StartCoroutineOnAvailableHost(StuckRescueRelocateRoutine(rescueGround, _routeVersion));
            _stuckWindowStartGround = currentGround;
            _stuckWindowStartAt = Time.time;
        }

        private static IEnumerator StuckRescueRelocateRoutine(Vector3 rescueGround, int version)
        {
            if (_droneVisual == null || rescueGround == Vector3.zero)
                yield break;

            _stuckRescueActive = true;
            float dematerializeSeconds = CourierDroneRuntimeAssets.PlayDematerializeOut(_droneVisual);
            if (dematerializeSeconds > 0.001f)
                yield return new WaitForSeconds(dematerializeSeconds);

            if (_droneVisual == null || version != _routeVersion)
            {
                _stuckRescueActive = false;
                yield break;
            }

            Vector3 resolvedRescueGround = ResampleGroundPoint(rescueGround, rescueGround);
            if (resolvedRescueGround != Vector3.zero)
            {
                _droneGroundPosition = resolvedRescueGround;
                _droneVisual.transform.position = ToFlightPosition(resolvedRescueGround);
                _followSmoothVelocity = Vector3.zero;
                _dynamicHoverHeight = VISUAL_FLIGHT_HEIGHT;
                ClearFollowPathCache();
                Plugin.Log?.LogInfo($"Courier Drone stuck-rescue at {resolvedRescueGround}");
                CourierDroneRuntimeAssets.PlayMaterializeIn(_droneVisual);
            }

            ResetFollowStuckState();
            _stuckRescueActive = false;
        }

        private static IEnumerator TryGuardPickUpScrap(int version)
        {
            if (_droneVisual == null || version != _routeVersion || _mode != DroneMode.GuardingPlayer || IsCargoWeightFull())
                yield break;
            if (Time.time < _nextGuardScrapScanAt)
                yield break;

            _nextGuardScrapScanAt = Time.time + GUARD_PICKUP_SCAN_INTERVAL;
            GrabbableObject item = FindGuardPickupTarget();
            if (item == null)
                yield break;

            PlayerControllerB owner = ResolvePlayer(_ownerPlayerId);
            CourierCommand context = BuildCommandContext(owner);
            Vector3 dropPosition = context != null ? context.DropPosition : FindShipDropPosition(owner);
            CourierItemMove move = new CourierItemMove(item.NetworkObjectId, item.transform.position, dropPosition, item.isInFactory);
            yield return MagnetPickUpScrap(move, move.PickupIsInFactory, item, version);
        }

        private static GrabbableObject FindGuardPickupTarget()
        {
            Vector3 droneGround = CurrentDroneGroundPositionOr(_droneGroundPosition);
            if (droneGround == Vector3.zero)
                return null;

            List<Vector3> searchPositions = new List<Vector3>();
            AddRoutePoint(searchPositions, droneGround);
            PlayerControllerB owner = ResolvePlayer(_ownerPlayerId);
            if (owner != null && !owner.isPlayerDead && owner.transform != null)
                AddRoutePoint(searchPositions, FindDroneFollowPosition(owner));

            GrabbableObject[] items = ScanGrabbableObjectsForCommandOrSweep();
            Array.Sort(items, (a, b) =>
            {
                float da = a == null ? float.MaxValue : DistanceToClosestPointSqr(a.transform.position, searchPositions);
                float db = b == null ? float.MaxValue : DistanceToClosestPointSqr(b.transform.position, searchPositions);
                return da.CompareTo(db);
            });

            float radiusSqr = GUARD_PICKUP_RADIUS * GUARD_PICKUP_RADIUS;
            float carriedWeight = GetCarriedCargoWeightPounds();
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (!IsCourierEligibleEntranceScrap(item, searchPositions, radiusSqr))
                    continue;
                if (item.isInFactory != _droneIsInside)
                    continue;
                if (IsAlreadyCarried(item.NetworkObjectId) || IsAlreadyPlanned(item.NetworkObjectId))
                    continue;
                if (WouldExceedCargoWeight(item, carriedWeight))
                    continue;

                return item;
            }

            return null;
        }

        private static IEnumerator WaitAtEntranceRoutine(CourierCommand command, int version)
        {
            while (version == _routeVersion && _mode == DroneMode.AtEntrance)
            {
                ApplyIdleSway(CurrentDroneGroundPositionOr(command.InsideEntrancePosition), ResolvePlayer(_ownerPlayerId));
                UpdateCarriedItemTransforms();

                Vector3 scanPoint = _droneIsInside
                    ? CurrentDroneGroundPositionOr(command.InsideEntrancePosition)
                    : command.OutsideEntrancePosition;
                if (scanPoint == Vector3.zero)
                    scanPoint = command.EntrancePosition;

                AddFreshEntranceScrapPlans(command, scanPoint);
                yield return TryPickUpScrapAtPoint(command, scanPoint, version);
                if (!_reportedNoEntranceScrap && CarriedItems.Count == 0 && PlannedItems.Count == 0)
                {
                    _reportedNoEntranceScrap = true;
                    DisplayOwnerTip(command.PlayerId, "No scrap near entrance yet.", isWarning: false);
                }

                if (IsCargoWeightFull())
                {
                    DisplayOwnerTip(command.PlayerId, "Cargo weight full. Returning to ship.", isWarning: false);
                    command.Type = CourierCommandType.ReturnToShip;
                    StartReturnToShip(command);
                    yield break;
                }

                float elapsed = 0f;
                while (elapsed < ENTRANCE_WAIT_SCAN_INTERVAL)
                {
                    if (version != _routeVersion || _mode != DroneMode.AtEntrance)
                        yield break;

                    ApplyIdleSway(CurrentDroneGroundPositionOr(scanPoint), ResolvePlayer(_ownerPlayerId));
                    UpdateCarriedItemTransforms();
                    elapsed += Time.deltaTime;
                    yield return null;
                }
            }
        }

        private static void AddFreshEntranceScrapPlans(CourierCommand command, Vector3 scanPoint)
        {
            if (command == null || IsCargoWeightFull())
                return;
            if (Time.time < _nextEntranceScrapScanAt)
                return;

            _nextEntranceScrapScanAt = Time.time + ENTRANCE_WAIT_SCAN_INTERVAL;

            List<Vector3> searchPositions = new List<Vector3>();
            AddRoutePoint(searchPositions, scanPoint);
            List<CourierItemMove> moves = FindEntranceScrap(command, searchPositions);
            for (int i = 0; i < moves.Count; i++)
            {
                CourierItemMove move = moves[i];
                if (IsAlreadyCarried(move.NetworkObjectId) || IsAlreadyPlanned(move.NetworkObjectId))
                    continue;

                PlannedItems.Add(move);
            }
        }

        private static IEnumerator MoveAlongRoute(CourierCommand command, List<Vector3> route, int version, bool pickUpAtEntrance)
        {
            if (_droneVisual == null || route.Count == 0)
                yield break;

            _lastRouteMoveBlocked = false;
            _droneGroundPosition = route[0];
            Vector3 previousPosition = _droneVisual.transform.position;
            for (int i = 1; i < route.Count; i++)
            {
                if (version != _routeVersion || _lastRouteMoveBlocked)
                    yield break;

                Vector3 from = route[i - 1];
                Vector3 to = route[i];
                if (IsEntranceTransition(command, from, to))
                {
                    _droneVisual.SetActive(false);
                    yield return new WaitForSeconds(0.16f);
                    _droneVisual.transform.position = ToFlightPosition(to);
                    previousPosition = _droneVisual.transform.position;
                    _droneGroundPosition = to;
                    _droneIsInside = IsSameRoutePoint(to, command.InsideEntrancePosition);
                    UpdateCarriedItemTransforms();
                    _droneVisual.SetActive(true);
                    yield return new WaitForSeconds(0.08f);
                }
                else
                {
                    List<Vector3> path = BuildNavMeshPath(from, to);
                    for (int p = 1; p < path.Count; p++)
                    {
                        if (version != _routeVersion)
                            yield break;

                        yield return MoveDroneSegment(path[p - 1], path[p], previousPosition.x, previousPosition.y, previousPosition.z, version);
                        if (_lastRouteMoveBlocked)
                            yield break;

                        previousPosition = _droneVisual.transform.position;
                    }

                    previousPosition = _droneVisual.transform.position;
                    if ((_droneGroundPosition - to).sqrMagnitude <= 0.25f)
                        _droneGroundPosition = ResampleGroundPoint(to, _droneGroundPosition);
                }

                if (pickUpAtEntrance && IsMainEntrancePoint(command, to))
                {
                    yield return TryPickUpScrapAtPoint(command, to, version);
                    yield return new WaitForSeconds(ENTRANCE_SCAN_SECONDS);
                }
            }
        }

        private static IEnumerator MoveDroneSegment(Vector3 fromGround, Vector3 toGround, float previousX, float previousY, float previousZ, int version)
        {
            Vector3 previousPosition = new Vector3(previousX, previousY, previousZ);
            Vector3 startGround = ResampleGroundPoint(fromGround, fromGround);
            Vector3 endGround = ResampleGroundPoint(toGround, toGround);
            Vector3 start = ToFlightPosition(startGround);
            Vector3 end = ToFlightPosition(endGround);
            float distance = Vector3.Distance(start, end);
            float duration = Mathf.Max(distance / VISUAL_SPEED, 0.2f);
            float maxDuration = Mathf.Max(duration * 2.25f, duration + 1.2f);
            float elapsed = 0f;
            Vector3 previousBasePosition = previousPosition;
            Vector3 routeSmoothVelocity = Vector3.zero;
            Vector3 currentGround = _droneGroundPosition != Vector3.zero ? _droneGroundPosition : startGround;
            _lastRouteMoveBlocked = false;

            while (elapsed < maxDuration)
            {
                if (version != _routeVersion)
                    yield break;

                if ((currentGround - endGround).sqrMagnitude <= 0.04f)
                    break;

                Vector3 nextGround = Vector3.SmoothDamp(
                    currentGround,
                    endGround,
                    ref routeSmoothVelocity,
                    FOLLOW_MOVE_SMOOTH_TIME,
                    VISUAL_SPEED,
                    Time.deltaTime);
                nextGround = ResampleGroundPoint(nextGround, currentGround);
                Vector3 nextBasePosition = ToSmoothedFlightPosition(nextGround, previousBasePosition.y);
                if (TryResolveClutterMove(previousBasePosition, nextBasePosition, nextGround, out Vector3 adjustedPosition, out bool blocked))
                {
                    nextBasePosition = adjustedPosition;
                    nextGround = ResampleGroundPoint(new Vector3(adjustedPosition.x, nextGround.y, adjustedPosition.z), currentGround);
                    if (blocked)
                        routeSmoothVelocity = Vector3.Lerp(routeSmoothVelocity, Vector3.zero, Mathf.Clamp01(Time.deltaTime * 2.5f));
                }

                Vector3 nextPosition = ApplyFlightBob(nextBasePosition);
                _droneVisual.transform.position = nextPosition;
                Vector3 travelDelta = nextBasePosition - previousBasePosition;
                FaceTravelDirection(_droneVisual.transform, travelDelta.x, travelDelta.y, travelDelta.z);
                _droneGroundPosition = nextGround;
                UpdateCarriedItemTransforms();
                previousPosition = nextPosition;
                previousBasePosition = nextBasePosition;
                currentGround = nextGround;

                elapsed += Time.deltaTime;
                yield return null;
            }

            Vector3 finalPosition = end;
            if (TryResolveClutterMove(previousBasePosition, finalPosition, endGround, out Vector3 finalAdjustedPosition, out bool finalBlocked))
            {
                finalPosition = finalAdjustedPosition;
                endGround = ResampleGroundPoint(new Vector3(finalPosition.x, endGround.y, finalPosition.z), _droneGroundPosition != Vector3.zero ? _droneGroundPosition : endGround);
                _lastRouteMoveBlocked = finalBlocked || (endGround - toGround).sqrMagnitude > 0.25f;
            }

            _droneVisual.transform.position = finalPosition;
            _droneGroundPosition = endGround;
            UpdateCarriedItemTransforms();
            yield return new WaitForSeconds(0.05f);
        }

        private static List<Vector3> BuildRouteToEntrance(CourierCommand command)
        {
            List<Vector3> route = new List<Vector3>();
            AddRoutePoint(route, CurrentDroneGroundPositionOr(command.DroneStartPosition));

            Vector3 insideEntrance = FirstNonZero(command.InsideEntrancePosition, command.EntrancePosition);
            Vector3 outsideEntrance = FirstNonZero(command.OutsideEntrancePosition, command.EntrancePosition);
            if (_droneIsInside)
            {
                AddRoutePoint(route, insideEntrance);
            }
            else
            {
                AddRoutePoint(route, outsideEntrance);
                AddRoutePoint(route, insideEntrance);
            }

            return route;
        }

        private static List<Vector3> BuildRouteToDrop(CourierCommand command, bool includeShipApproach)
        {
            List<Vector3> route = new List<Vector3>();
            AddRoutePoint(route, CurrentDroneGroundPositionOr(command.DroneStartPosition));

            if (_droneIsInside != command.DropIsInFactory)
            {
                if (_droneIsInside)
                {
                    AddRoutePoint(route, command.InsideEntrancePosition);
                    AddRoutePoint(route, command.OutsideEntrancePosition);
                }
                else
                {
                    AddRoutePoint(route, command.OutsideEntrancePosition);
                    AddRoutePoint(route, command.InsideEntrancePosition);
                }
            }

            if (includeShipApproach)
                AddRoutePoint(route, FindShipApproachPosition(command.DropPosition));
            AddRoutePoint(route, command.DropPosition);
            return route;
        }

        private static List<Vector3> BuildNavMeshPath(Vector3 from, Vector3 to)
        {
            List<Vector3> points = new List<Vector3>();
            AddRoutePoint(points, from);

            if (TryCalculateNavMeshPath(from, to, out Vector3[] corners))
            {
                for (int i = 0; i < corners.Length; i++)
                    AddRoutePoint(points, corners[i]);
            }

            AddRoutePoint(points, to);
            return points;
        }

        private static List<Vector3> GetFollowPath(Vector3 from, Vector3 to)
        {
            bool targetMoved = _cachedFollowPathTarget == Vector3.zero
                || (_cachedFollowPathTarget - to).sqrMagnitude > NAVMESH_REPATH_TARGET_MOVE_DISTANCE * NAVMESH_REPATH_TARGET_MOVE_DISTANCE;
            bool expired = Time.time >= _nextFollowRepathAt;
            if (CachedFollowPath.Count == 0 || targetMoved || expired)
            {
                CachedFollowPath.Clear();
                AddRoutePoint(CachedFollowPath, from);
                if (TryCalculateNavMeshPath(from, to, out Vector3[] corners))
                {
                    for (int i = 0; i < corners.Length; i++)
                        AddRoutePoint(CachedFollowPath, corners[i]);
                }
                else if (CanMoveDirectToFollowTarget(from, to))
                {
                    AddRoutePoint(CachedFollowPath, to);
                }

                _cachedFollowPathTarget = to;
                _nextFollowRepathAt = Time.time + NAVMESH_REPATH_INTERVAL;
            }

            TrimFollowPath(from);
            return CachedFollowPath;
        }

        private static Vector3 SelectFollowWaypoint(List<Vector3> path, Vector3 currentGround, Vector3 fallbackTarget)
        {
            if (path == null || path.Count <= 1)
                return CanMoveDirectToFollowTarget(currentGround, fallbackTarget) ? fallbackTarget : Vector3.zero;

            TrimFollowPath(currentGround);
            int lastCandidate = path.Count - 1;
            for (int i = lastCandidate; i >= 1; i--)
            {
                Vector3 candidate = path[i];
                if (candidate == Vector3.zero)
                    continue;
                if ((candidate - currentGround).sqrMagnitude > FOLLOW_LOOKAHEAD_DISTANCE * FOLLOW_LOOKAHEAD_DISTANCE)
                    continue;
                if (CanReachWaypointAtHoverHeight(currentGround, candidate))
                    return candidate;
            }

            int nearestIndex = 1;
            return path.Count > nearestIndex ? path[nearestIndex] : Vector3.zero;
        }

        private static void TrimFollowPath(Vector3 currentGround)
        {
            float reachedSqr = FOLLOW_SETTLE_DISTANCE * FOLLOW_SETTLE_DISTANCE * 0.25f;
            while (CachedFollowPath.Count > 2 && (CachedFollowPath[1] - currentGround).sqrMagnitude <= reachedSqr)
                CachedFollowPath.RemoveAt(0);
        }

        private static void ClearFollowPathCache()
        {
            CachedFollowPath.Clear();
            _cachedFollowPathTarget = Vector3.zero;
            _nextFollowRepathAt = 0f;
        }

        private static void ResetFollowMovementState()
        {
            _followSmoothVelocity = Vector3.zero;
            _nextHoverProbeAt = 0f;
            _dynamicHoverHeight = VISUAL_FLIGHT_HEIGHT;
            _nextCollisionRepathAt = 0f;
            ResetFollowStuckState();
        }

        private static void ResetFollowStuckState()
        {
            _stuckWindowStartGround = Vector3.zero;
            _stuckWindowStartAt = 0f;
            _stuckRepathIssued = false;
        }

        private static bool TryCalculateNavMeshPath(Vector3 from, Vector3 to, out Vector3[] corners)
        {
            corners = null;
            if (!NavMesh.SamplePosition(from, out NavMeshHit startHit, NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas))
                return false;
            if (!NavMesh.SamplePosition(to, out NavMeshHit endHit, NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas))
                return false;

            NavMeshPath path = new NavMeshPath();
            if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path)
                || path.status != NavMeshPathStatus.PathComplete
                || path.corners == null
                || path.corners.Length < 2)
            {
                return false;
            }

            corners = path.corners;
            return true;
        }

        private static IEnumerator TryPickUpScrapAtPoint(CourierCommand command, Vector3 routePoint, int version)
        {
            if (_droneVisual == null || IsCargoWeightFull())
                yield break;

            List<Vector3> searchPositions = new List<Vector3>();
            AddRoutePoint(searchPositions, routePoint);
            if (searchPositions.Count == 0)
                searchPositions = FindMainEntranceSearchPositions(command);
            float radiusSqr = CourierDroneUpgrade.PICKUP_RADIUS * CourierDroneUpgrade.PICKUP_RADIUS;

            for (int i = 0; i < PlannedItems.Count; i++)
            {
                if (version != _routeVersion || _mode == DroneMode.FlyingToPlayer)
                    yield break;
                if (IsCargoWeightFull())
                    yield break;

                CourierItemMove move = PlannedItems[i];
                if (IsAlreadyCarried(move.NetworkObjectId))
                    continue;
                if (move.PickupIsInFactory != _droneIsInside)
                    continue;

                GrabbableObject item = ResolveItem(move.NetworkObjectId);
                if (!IsCourierEligibleEntranceScrap(item, searchPositions, radiusSqr))
                    continue;
                if (WouldExceedCargoWeight(item, GetCarriedCargoWeightPounds()))
                {
                    PlannedItems.RemoveAt(i);
                    i--;
                    continue;
                }

                yield return MagnetPickUpScrap(move, move.PickupIsInFactory, item, version);
            }
        }

        private static IEnumerator MagnetPickUpScrap(CourierItemMove itemMove, bool pickupWasInside, GrabbableObject item, int version)
        {
            if (_droneVisual == null || item == null || version != _routeVersion)
                yield break;
            if (WouldExceedCargoWeight(item, GetCarriedCargoWeightPounds()))
                yield break;

            _magnetPickupActive = true;
            Vector3 itemGroundPosition = item.transform.position;
            Vector3 lowerPosition = itemGroundPosition + Vector3.up * MAGNET_PICKUP_LOWER_HEIGHT;
            yield return MoveDroneVisualPosition(_droneVisual.transform.position, lowerPosition, 0.35f, version);
            if (_lastRouteMoveBlocked)
            {
                _magnetPickupActive = false;
                yield break;
            }

            if (_droneVisual == null || item == null || version != _routeVersion)
            {
                _magnetPickupActive = false;
                yield break;
            }
            if (WouldExceedCargoWeight(item, GetCarriedCargoWeightPounds()))
            {
                _magnetPickupActive = false;
                yield break;
            }

            CourierDroneRuntimeAssets.StartMagnetSound(_droneVisual);
            item.EnablePhysics(enable: false);
            bool bodyWasKinematic = item.propBody != null && item.propBody.isKinematic;
            bool bodyUsedGravity = item.propBody != null && item.propBody.useGravity;
            SetItemKinematic(item, isKinematic: true, useGravity: false);
            Vector3 attachLocal = new Vector3(
                0.38f * (CarriedItems.Count % 2 == 0 ? -0.5f : 0.5f),
                -CARRY_DANGLE_HEIGHT - 0.22f * (CarriedItems.Count / 2),
                -0.12f);
            Vector3 itemStart = item.transform.position;
            Vector3 itemEnd = GetCarryWorldPosition(attachLocal);

            float elapsed = 0f;
            while (elapsed < MAGNET_PICKUP_LIFT_SECONDS)
            {
                if (version != _routeVersion)
                {
                    CourierDroneRuntimeAssets.StopMagnetSound(_droneVisual);
                    _magnetPickupActive = false;
                    yield break;
                }
                float t = Mathf.SmoothStep(0f, 1f, elapsed / MAGNET_PICKUP_LIFT_SECONDS);
                if (item != null)
                    item.transform.position = Vector3.Lerp(itemStart, itemEnd, t);

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (item == null || _droneVisual == null)
            {
                CourierDroneRuntimeAssets.StopMagnetSound(_droneVisual);
                _magnetPickupActive = false;
                yield break;
            }

            Vector3 originalWorldScale = item.transform.lossyScale;
            Quaternion carryLocalRotation = Quaternion.Inverse(_droneVisual.transform.rotation) * item.transform.rotation;
            item.transform.SetParent(null, worldPositionStays: true);
            item.parentObject = null;
            item.isHeld = false;
            item.isPocketed = false;
            item.isHeldByEnemy = false;
            item.playerHeldBy = null;
            item.grabbable = false;
            item.grabbableToEnemies = false;
            SetWorldScale(item.transform, originalWorldScale);
            CarriedItems.Add(new CarriedCourierItem(itemMove, item, originalWorldScale, attachLocal, carryLocalRotation, bodyWasKinematic, bodyUsedGravity));
            UpdateCarriedItemTransforms();

            _droneGroundPosition = itemGroundPosition;
            _droneIsInside = pickupWasInside;
            yield return MoveDroneVisualPosition(_droneVisual.transform.position, ToFlightPosition(itemGroundPosition), 0.25f, version);
            if (_lastRouteMoveBlocked)
            {
                CourierDroneRuntimeAssets.StopMagnetSound(_droneVisual);
                _magnetPickupActive = false;
                yield break;
            }

            CourierDroneRuntimeAssets.StopMagnetSound(_droneVisual);
            _magnetPickupActive = false;
        }

        private static IEnumerator MoveDroneVisualPosition(Vector3 start, Vector3 end, float duration, int version)
        {
            float elapsed = 0f;
            Vector3 previous = start;
            _lastRouteMoveBlocked = false;
            while (elapsed < duration)
            {
                if (_droneVisual == null || version != _routeVersion)
                    yield break;

                float t = Mathf.SmoothStep(0f, 1f, elapsed / Mathf.Max(duration, 0.01f));
                Vector3 next = Vector3.Lerp(start, end, t);
                Vector3 nextGround = ResampleGroundPoint(new Vector3(next.x, _droneGroundPosition.y, next.z), _droneGroundPosition);
                if (TryResolveClutterMove(previous, next, nextGround, out Vector3 adjustedPosition, out bool blocked))
                {
                    next = adjustedPosition;
                    _lastRouteMoveBlocked = blocked;
                }

                _droneVisual.transform.position = next;
                Vector3 delta = next - previous;
                FaceTravelDirection(_droneVisual.transform, delta.x, delta.y, delta.z);
                UpdateCarriedItemTransforms();
                previous = next;
                if (_lastRouteMoveBlocked)
                    yield break;

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (_droneVisual != null && version == _routeVersion)
            {
                Vector3 final = end;
                Vector3 finalGround = ResampleGroundPoint(new Vector3(final.x, _droneGroundPosition.y, final.z), _droneGroundPosition);
                if (TryResolveClutterMove(previous, final, finalGround, out Vector3 adjustedPosition, out bool blocked))
                {
                    final = adjustedPosition;
                    _lastRouteMoveBlocked = blocked;
                }

                _droneVisual.transform.position = final;
                UpdateCarriedItemTransforms();
            }
        }

        private static int DropCarriedScrap(CourierCommand command)
        {
            int recovered = 0;
            for (int i = 0; i < CarriedItems.Count; i++)
            {
                CarriedCourierItem carriedItem = CarriedItems[i];
                if (carriedItem.Item == null)
                    continue;

                Vector3 dropPosition = command.DropPosition + new Vector3(
                    0.28f * (i % 3 - 1),
                    1.15f,
                    0.28f * (i / 3));
                MoveScrapToPlayer(carriedItem.Item, dropPosition, command.DropIsInShip, command.DropIsInFactory, carriedItem.WorldScale, carriedItem.BodyWasKinematic, carriedItem.BodyUsedGravity, command.PlayerId);
                recovered++;
            }

            CarriedItems.Clear();
            return recovered;
        }

        private static int DropCarriedScrapAtDrone()
        {
            int dropped = 0;
            Vector3 basePosition = CurrentDroneGroundPositionOr(_droneGroundPosition);
            if (basePosition == Vector3.zero && _droneVisual != null)
                basePosition = ResampleGroundPoint(_droneVisual.transform.position, Vector3.zero);

            for (int i = 0; i < CarriedItems.Count; i++)
            {
                CarriedCourierItem carriedItem = CarriedItems[i];
                if (carriedItem.Item == null)
                    continue;

                Vector3 dropPosition = basePosition + new Vector3(
                    0.35f * (i % 3 - 1),
                    1.0f,
                    0.35f * (i / 3));
                MoveScrapToPlayer(carriedItem.Item, dropPosition, false, _droneIsInside, carriedItem.WorldScale, carriedItem.BodyWasKinematic, carriedItem.BodyUsedGravity, _ownerPlayerId);
                dropped++;
            }

            CarriedItems.Clear();
            return dropped;
        }

        private static void ResetDroneHealthForRound()
        {
            _droneHealth = DRONE_MAX_HEALTH;
            _droneDestroyedThisRound = false;
            _nextEnemyScanAt = 0f;
            _lastEnemyScanFrame = -1;
            _lastEnemyFacingFrame = -1;
            _nextFlameAt = 0f;
            _nextGrenadeAt = 0f;
            _combatLandedUntil = 0f;
            _nextGuardScrapScanAt = 0f;
            _magnetPickupActive = false;
            ClearPointerTargetState();
            DroneThreats.Clear();
            NextDroneHitByEnemy.Clear();
        }

        private static bool CanDroneBeDamaged()
        {
            return !_droneDestroyedThisRound
                && _droneHealth > 0
                && _droneVisual != null
                && _droneVisual.activeInHierarchy
                && _mode != DroneMode.NotSpawned;
        }

        internal static void NotifyDroneAttackedEnemy(EnemyAI enemy)
        {
            if (!IsServerAuthority() || enemy == null || enemy.isEnemyDead || !CanDroneBeDamaged())
                return;
            if (!EnemySharesDroneSpace(enemy))
                return;

            MarkDroneThreat(enemy, DRONE_ATTACK_THREAT_SECONDS);
        }

        private static void UpdateEnemyDroneAggroScanOncePerFrame()
        {
            if (_lastEnemyScanFrame == Time.frameCount)
                return;

            _lastEnemyScanFrame = Time.frameCount;
            if (Time.time < _nextEnemyScanAt)
                return;

            _nextEnemyScanAt = Time.time + ENEMY_SCAN_INTERVAL;
            if (!IsServerAuthority() || !CanDroneBeDamaged())
                return;

            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || roundManager.SpawnedEnemies == null)
                return;

            for (int i = 0; i < roundManager.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = roundManager.SpawnedEnemies[i];
                if (enemy != null)
                    UpdateEnemyDroneAggro(enemy);
            }

            UpdateDroneCombat(roundManager.SpawnedEnemies);
        }

        private static void UpdateEnemyDroneFacingOncePerFrame()
        {
            if (_lastEnemyFacingFrame == Time.frameCount)
                return;

            _lastEnemyFacingFrame = Time.frameCount;
            if (!IsServerAuthority() || !CanDroneBeDamaged())
                return;

            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || roundManager.SpawnedEnemies == null)
                return;

            RotateEnemyThreatsTowardDrone(roundManager.SpawnedEnemies);
        }

        private static void RotateEnemyThreatsTowardDrone(List<EnemyAI> enemies)
        {
            if (enemies == null)
                return;

            Vector3 dronePosition = GetDroneCombatPosition();
            if (dronePosition == Vector3.zero)
                return;

            float faceRange = DRONE_ENEMY_ATTACK_RANGE * DRONE_ENEMY_FACE_RANGE_MULTIPLIER;
            float faceRangeSqr = faceRange * faceRange;
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (enemy == null || enemy.isEnemyDead || enemy.transform == null)
                    continue;
                if (!EnemySharesDroneSpace(enemy) || !IsDroneThreat(enemy))
                    continue;
                if (PlayerHasPriorityOverDrone(enemy, dronePosition))
                    continue;

                Vector3 toDrone = dronePosition - enemy.transform.position;
                toDrone.y = 0f;
                float distanceSqr = toDrone.sqrMagnitude;
                if (distanceSqr <= 0.001f || distanceSqr > faceRangeSqr)
                    continue;

                Quaternion desired = Quaternion.LookRotation(toDrone.normalized, Vector3.up);
                enemy.transform.rotation = Quaternion.RotateTowards(
                    enemy.transform.rotation,
                    desired,
                    DRONE_ENEMY_FACE_TURN_DEGREES_PER_SECOND * Time.deltaTime);
            }
        }

        private static void UpdateEnemyDroneAggro(EnemyAI enemy)
        {
            if (!IsServerAuthority() || enemy == null || enemy.isEnemyDead || !CanDroneBeDamaged())
                return;
            if (!EnemySharesDroneSpace(enemy))
                return;

            Vector3 dronePosition = GetDroneTargetPosition();
            if (dronePosition == Vector3.zero)
                return;

            float distanceSqr = (enemy.transform.position - dronePosition).sqrMagnitude;
            if (distanceSqr <= DRONE_PROXIMITY_AGGRO_RADIUS * DRONE_PROXIMITY_AGGRO_RADIUS)
                MarkDroneThreat(enemy, DRONE_PROXIMITY_THREAT_SECONDS);

            if (!IsDroneThreat(enemy))
                return;

            if (distanceSqr <= DRONE_ENEMY_ATTACK_RANGE * DRONE_ENEMY_ATTACK_RANGE)
                TryEnemyContactDamageDrone(enemy);
        }

        private static void UpdateDroneCombat(List<EnemyAI> enemies)
        {
            if (!CanDroneUseCombat() || enemies == null)
                return;

            int tier = ResolveCombatTier();
            if (tier < 1)
                return;

            Vector3 dronePosition = GetDroneCombatPosition();
            Vector3 origin = GetDroneCombatOrigin();
            if (dronePosition == Vector3.zero || origin == Vector3.zero)
                return;

            EnemyAI flameTarget = null;
            float bestFlameSqr = FLAME_TRIGGER_RANGE * FLAME_TRIGGER_RANGE;
            EnemyAI grenadeTarget = null;
            Vector3 grenadeImpact = Vector3.zero;
            float bestGrenadeSqr = GRENADE_MAX_RANGE * GRENADE_MAX_RANGE;
            bool canFlame = Time.time >= _nextFlameAt;
            bool canGrenade = tier >= 2 && Time.time >= _nextGrenadeAt;
            Transform grenadeMuzzle = canGrenade ? ResolveGrenadeMuzzle() : null;
            Vector3 grenadeOrigin = grenadeMuzzle != null ? grenadeMuzzle.position : origin;
            if (TryUsePriorityCombatTarget(tier, origin, dronePosition, grenadeOrigin, canFlame, canGrenade))
                return;

            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (!IsCombatEligibleEnemy(enemy))
                    continue;

                Vector3 center = ResolveEnemyCenter(enemy);
                if (center == Vector3.zero)
                    continue;

                float droneSqr = (center - dronePosition).sqrMagnitude;
                if (canFlame && droneSqr <= bestFlameSqr && HasDroneLineOfSight(origin, center))
                {
                    bestFlameSqr = droneSqr;
                    flameTarget = enemy;
                }

                if (!canGrenade)
                    continue;

                float grenadeSqr = (center - grenadeOrigin).sqrMagnitude;
                if (grenadeSqr < GRENADE_MIN_RANGE * GRENADE_MIN_RANGE || grenadeSqr > bestGrenadeSqr)
                    continue;
                if (!HasDroneLineOfSight(grenadeOrigin, center))
                    continue;
                if (IsPlayerInGrenadeDanger(center))
                    continue;

                bestGrenadeSqr = grenadeSqr;
                grenadeTarget = enemy;
                grenadeImpact = center;
            }

            if (flameTarget != null)
            {
                FireDroneFlame(manualMode: false);
                return;
            }

            if (grenadeTarget != null)
                FireDroneGrenade(grenadeImpact);
        }


        private static bool TryUsePriorityCombatTarget(int tier, Vector3 origin, Vector3 dronePosition, Vector3 grenadeOrigin, bool canFlame, bool canGrenade)
        {
            EnemyAI priorityTarget = ResolvePriorityTarget();
            if (priorityTarget == null)
                return false;

            Vector3 center = ResolveEnemyCenter(priorityTarget);
            if (center == Vector3.zero)
                return true;

            float flameSqr = (center - dronePosition).sqrMagnitude;
            if (canFlame && flameSqr <= FLAME_TRIGGER_RANGE * FLAME_TRIGGER_RANGE && HasDroneLineOfSight(origin, center))
            {
                FireDroneFlame(manualMode: false);
                return true;
            }

            if (!canGrenade || tier < 2)
                return true;

            float grenadeSqr = (center - grenadeOrigin).sqrMagnitude;
            if (grenadeSqr < GRENADE_MIN_RANGE * GRENADE_MIN_RANGE || grenadeSqr > GRENADE_MAX_RANGE * GRENADE_MAX_RANGE)
                return true;
            if (!HasDroneLineOfSight(grenadeOrigin, center))
                return true;
            if (IsPlayerInGrenadeDanger(center))
                return true;

            FireDroneGrenade(center);
            return true;
        }

        private static EnemyAI ResolvePriorityTarget()
        {
            if (_priorityTargetId == 0)
                return null;
            if (Time.time > _priorityTargetExpiresAt)
            {
                ClearPriorityTargetOnly();
                return null;
            }

            EnemyAI enemy = ResolveEnemy(_priorityTargetId);
            if (!IsCombatEligibleEnemy(enemy))
            {
                ClearPriorityTargetOnly();
                return null;
            }

            return enemy;
        }

        private static bool CanDroneUseCombat()
        {
            return IsServerAuthority()
                && CanDroneBeDamaged()
                && _mode != DroneMode.ManualControl
                && Time.time >= _combatLandedUntil
                && _droneVisual != null
                && _droneVisual.activeInHierarchy;
        }

        private static bool CanDroneUseManualCombat()
        {
            return IsServerAuthority()
                && CanDroneBeDamaged()
                && _mode == DroneMode.ManualControl
                && Time.time >= _combatLandedUntil
                && !_magnetPickupActive
                && _droneVisual != null
                && _droneVisual.activeInHierarchy;
        }

        private static int ResolveCombatTier()
        {
            return _ownerCourierTier > 0 ? _ownerCourierTier : CourierDroneUpgrade.GetTier();
        }

        private static bool IsCombatEligibleEnemy(EnemyAI enemy)
        {
            return enemy != null
                && !enemy.isEnemyDead
                && enemy.transform != null
                && EnemySharesDroneSpace(enemy);
        }

        private static void FireDroneFlame(bool manualMode)
        {
            _nextFlameAt = Time.time + FLAME_COOLDOWN;
            Vector3 position = GetDroneCombatPosition();
            PlayDroneFlameLocal(position);
            BroadcastDroneFlame(position);
            StartCoroutineOnAvailableHost(FlameDamageRoutine(_routeVersion, manualMode));
        }

        private static IEnumerator FlameDamageRoutine(int version, bool manualMode)
        {
            float tickDelay = FLAME_TICKS > 1 ? FLAME_DAMAGE_WINDOW_SECONDS / FLAME_TICKS : 0f;
            for (int tick = 0; tick < FLAME_TICKS; tick++)
            {
                if (version != _routeVersion || !(manualMode ? CanDroneUseManualCombat() : CanDroneUseCombat()))
                    yield break;

                ApplyFlameDamageTick();
                if (tick < FLAME_TICKS - 1 && tickDelay > 0f)
                    yield return new WaitForSeconds(tickDelay);
            }
        }

        private static void ApplyFlameDamageTick()
        {
            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || roundManager.SpawnedEnemies == null)
                return;

            Vector3 origin = GetDroneCombatPosition();
            float radiusSqr = FLAME_DAMAGE_RADIUS * FLAME_DAMAGE_RADIUS;
            PlayerControllerB owner = ResolveCombatOwner();
            for (int i = 0; i < roundManager.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = roundManager.SpawnedEnemies[i];
                if (!IsCombatEligibleEnemy(enemy))
                    continue;

                Vector3 center = ResolveEnemyCenter(enemy);
                if ((center - origin).sqrMagnitude > radiusSqr)
                    continue;

                Vector3 direction = (center - origin).sqrMagnitude > 0.001f
                    ? (center - origin).normalized
                    : Vector3.up;
                enemy.HitEnemyOnLocalClient(FLAME_DAMAGE, direction, owner, false, DRONE_FLAME_HIT_ID);
                NotifyDroneAttackedEnemy(enemy);
            }
        }

        private static void FireDroneGrenade(Vector3 targetPosition)
        {
            Transform muzzle = ResolveGrenadeMuzzle();
            Vector3 start = muzzle != null ? muzzle.position : GetDroneCombatOrigin();
            Vector3 velocity = CalculateGrenadeVelocity(start, targetPosition);
            if (velocity == Vector3.zero)
                return;

            _nextGrenadeAt = Time.time + GRENADE_COOLDOWN;
            int grenadeId = _nextGrenadeId++;
            if (_nextGrenadeId == int.MaxValue)
                _nextGrenadeId = 1;

            PlayGrenadeLaunchLocal(grenadeId, start, velocity);
            BroadcastGrenadeLaunch(grenadeId, start, velocity);
            StartCoroutineOnAvailableHost(GrenadeDetonationRoutine(grenadeId, start, velocity));
        }

        private static bool HandleManualFireRequest(int playerId, ManualFireKind fireKind, Vector3 targetPosition)
        {
            if (!CanDroneUseManualCombat() || playerId != _ownerPlayerId)
                return false;

            int tier = ResolveCombatTier();
            switch (fireKind)
            {
                case ManualFireKind.Flame:
                    if (tier < 1 || Time.time < _nextFlameAt)
                        return false;

                    FireDroneFlame(manualMode: true);
                    return true;

                case ManualFireKind.Grenade:
                    if (tier < 2 || Time.time < _nextGrenadeAt)
                        return false;

                    Transform muzzle = ResolveGrenadeMuzzle();
                    Vector3 start = muzzle != null ? muzzle.position : GetDroneCombatOrigin();
                    if (start == Vector3.zero)
                        return false;

                    FireDroneGrenade(ClampManualGrenadeTarget(start, targetPosition));
                    return true;
            }

            return false;
        }

        private static IEnumerator GrenadeDetonationRoutine(int grenadeId, Vector3 start, Vector3 velocity)
        {
            float elapsed = 0f;
            Vector3 previous = start;
            Vector3 detonation = start;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;

            while (elapsed < GRENADE_FUSE_SECONDS)
            {
                elapsed += Time.deltaTime;
                Vector3 current = EvaluateGrenadePosition(start, velocity, elapsed);
                Vector3 delta = current - previous;
                float distance = delta.magnitude;
                if (distance > 0.001f && Physics.SphereCast(previous, GRENADE_PROJECTILE_RADIUS, delta.normalized, out RaycastHit hit, distance, mask, QueryTriggerInteraction.Ignore))
                {
                    detonation = hit.point;
                    DetonateDroneGrenade(grenadeId, detonation);
                    yield break;
                }

                previous = current;
                detonation = current;
                yield return null;
            }

            DetonateDroneGrenade(grenadeId, detonation);
        }

        private static void DetonateDroneGrenade(int grenadeId, Vector3 position)
        {
            PlayGrenadeExplosionLocal(grenadeId, position);
            BroadcastGrenadeExplosion(grenadeId, position);

            try
            {
                // Friendly-fire gate intentionally runs at fire time only; detonation still resolves if a player moves in.
                Landmine.SpawnExplosion(position, false, GRENADE_KILL_RANGE, GRENADE_DAMAGE_RANGE);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Courier Drone grenade Landmine.SpawnExplosion threw: {ex}");
            }
        }

        private static Vector3 CalculateGrenadeVelocity(Vector3 start, Vector3 target)
        {
            Vector3 toTarget = target - start;
            Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
            float horizontalDistance = flat.magnitude;
            if (horizontalDistance <= 0.001f)
                return Vector3.zero;

            float flightTime = Mathf.Clamp(horizontalDistance / GRENADE_LOB_SPEED, 0.75f, GRENADE_FUSE_SECONDS * 0.82f);
            Vector3 randomSpread = UnityEngine.Random.insideUnitSphere * 0.45f;
            randomSpread.y = Mathf.Abs(randomSpread.y) * 0.25f;
            Vector3 adjustedTarget = target + randomSpread;
            return (adjustedTarget - start - 0.5f * Physics.gravity * flightTime * flightTime) / flightTime;
        }

        private static Vector3 EvaluateGrenadePosition(Vector3 start, Vector3 velocity, float elapsed)
        {
            return start + velocity * elapsed + 0.5f * Physics.gravity * elapsed * elapsed;
        }

        private static PlayerControllerB ResolveCombatOwner()
        {
            PlayerControllerB owner = ResolvePlayer(_ownerPlayerId);
            return owner != null && !owner.isPlayerDead ? owner : null;
        }

        private static Transform ResolveGrenadeMuzzle()
        {
            return CourierDroneRuntimeAssets.FindGrenadeMuzzle(_droneVisual);
        }

        private static Vector3 GetDroneCombatPosition()
        {
            return _droneVisual != null ? _droneVisual.transform.position : ToFlightPosition(_droneGroundPosition);
        }

        private static Vector3 GetDroneCombatOrigin()
        {
            Vector3 position = GetDroneCombatPosition();
            return position != Vector3.zero ? position + Vector3.up * 0.12f : Vector3.zero;
        }

        private static Vector3 ResolveEnemyCenter(EnemyAI enemy)
        {
            if (enemy == null)
                return Vector3.zero;
            if (enemy.eye != null)
                return enemy.eye.position;

            Renderer renderer = null;
            if (enemy.skinnedMeshRenderers != null)
            {
                for (int i = 0; i < enemy.skinnedMeshRenderers.Length; i++)
                {
                    if (enemy.skinnedMeshRenderers[i] == null)
                        continue;

                    renderer = enemy.skinnedMeshRenderers[i];
                    break;
                }
            }

            if (renderer == null && enemy.meshRenderers != null)
            {
                for (int i = 0; i < enemy.meshRenderers.Length; i++)
                {
                    if (enemy.meshRenderers[i] == null)
                        continue;

                    renderer = enemy.meshRenderers[i];
                    break;
                }
            }

            return renderer != null ? renderer.bounds.center : enemy.transform.position + Vector3.up * 1.2f;
        }

        private static bool HasDroneLineOfSight(Vector3 origin, Vector3 target)
        {
            if (origin == Vector3.zero || target == Vector3.zero)
                return false;

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            return !Physics.Linecast(origin, target, mask, QueryTriggerInteraction.Ignore);
        }

        private static bool IsPlayerInGrenadeDanger(Vector3 impactPosition)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return false;

            float dangerSqr = (GRENADE_DAMAGE_RANGE + GRENADE_SAFETY_MARGIN) * (GRENADE_DAMAGE_RANGE + GRENADE_SAFETY_MARGIN);
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || player.isPlayerDead || player.transform == null)
                    continue;
                if (player.isInsideFactory != _droneIsInside)
                    continue;
                if ((player.transform.position - impactPosition).sqrMagnitude <= dangerSqr)
                    return true;
            }

            return false;
        }

        private static void PlayDroneFlameLocal(Vector3 position)
        {
            if (_droneVisual != null)
                CourierDroneRuntimeAssets.PlayFireAttack(_droneVisual);

            GameObject effect = SpawnCompanionEffect("FireAttackParticles", position, _droneVisual != null ? _droneVisual.transform.rotation : Quaternion.identity, 2.2f);
            if (effect != null && _droneVisual != null)
                effect.transform.SetParent(_droneVisual.transform, worldPositionStays: true);
        }

        private static void PlayGrenadeLaunchLocal(int grenadeId, Vector3 start, Vector3 velocity)
        {
            Vector3 launchDirection = velocity;
            float launchSqr = Vector3.Dot(launchDirection, launchDirection);
            if (launchSqr > 0.001f)
                launchDirection.Normalize();
            Quaternion rotation = launchSqr > 0.001f ? Quaternion.LookRotation(launchDirection, Vector3.up) : Quaternion.identity;
            SpawnCompanionEffect("GrenadeLaunchDustParticles", start, rotation, 2f);

            GameObject grenade = CourierDroneRuntimeAssets.InstantiateCompanionPrefab("Grenade", start, rotation);
            if (grenade == null)
                grenade = GameObject.CreatePrimitive(PrimitiveType.Sphere);

            grenade.name = "Y4NGZ_CourierDroneGrenade";
            grenade.transform.position = start;
            grenade.transform.rotation = rotation;
            grenade.transform.localScale = Vector3.one * 0.35f;
            Collider collider = grenade.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);

            Rigidbody body = grenade.GetComponent<Rigidbody>();
            if (body == null)
                body = grenade.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.isKinematic = false;
            body.velocity = velocity;
            body.angularVelocity = UnityEngine.Random.onUnitSphere * 9f;

            if (ActiveGrenadeVisuals.TryGetValue(grenadeId, out GameObject old) && old != null)
                UnityEngine.Object.Destroy(old);
            ActiveGrenadeVisuals[grenadeId] = grenade;
            UnityEngine.Object.Destroy(grenade, GRENADE_FUSE_SECONDS + 1f);
        }

        private static void PlayGrenadeExplosionLocal(int grenadeId, Vector3 position)
        {
            if (ActiveGrenadeVisuals.TryGetValue(grenadeId, out GameObject grenade) && grenade != null)
                UnityEngine.Object.Destroy(grenade);
            ActiveGrenadeVisuals.Remove(grenadeId);
            SpawnCompanionEffect("GrenadeExplosionParticles", position, Quaternion.identity, 4f);
        }

        private static void DestroyActiveGrenadeVisuals()
        {
            foreach (KeyValuePair<int, GameObject> pair in ActiveGrenadeVisuals)
            {
                if (pair.Value != null)
                    UnityEngine.Object.Destroy(pair.Value);
            }

            ActiveGrenadeVisuals.Clear();
        }

        private static GameObject SpawnCompanionEffect(string assetName, Vector3 position, Quaternion rotation, float destroyAfter)
        {
            GameObject effect = CourierDroneRuntimeAssets.InstantiateCompanionPrefab(assetName, position, rotation);
            if (effect == null)
                return null;

            effect.SetActive(true);
            ParticleSystem[] systems = effect.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem system = systems[i];
                if (system == null)
                    continue;

                system.gameObject.SetActive(true);
                system.Play(withChildren: true);
            }

            UnityEngine.Object.Destroy(effect, destroyAfter);
            return effect;
        }

        private static void MarkDroneThreat(EnemyAI enemy, float seconds)
        {
            DroneThreats[GetEnemyKey(enemy)] = Time.time + Mathf.Max(0.1f, seconds);
        }

        private static bool IsDroneThreat(EnemyAI enemy)
        {
            ulong key = GetEnemyKey(enemy);
            if (!DroneThreats.TryGetValue(key, out float expiresAt))
                return false;
            if (Time.time <= expiresAt)
                return true;

            DroneThreats.Remove(key);
            return false;
        }

        private static void SteerEnemyTowardDrone(EnemyAI enemy)
        {
            Vector3 dronePosition = GetDroneTargetPosition();
            if (dronePosition == Vector3.zero)
                return;

            try
            {
                enemy.SetDestinationToPosition(dronePosition);
                if (enemy.agent != null && enemy.agent.enabled && enemy.agent.isOnNavMesh)
                    enemy.agent.SetDestination(dronePosition);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Courier Drone enemy aggro steer skipped for {enemy.GetType().Name}: {ex.Message}");
            }
        }

        private static void TryEnemyContactDamageDrone(EnemyAI enemy)
        {
            ulong key = GetEnemyKey(enemy);
            if (NextDroneHitByEnemy.TryGetValue(key, out float nextHitAt) && Time.time < nextHitAt)
                return;

            NextDroneHitByEnemy[key] = Time.time + DRONE_ENEMY_ATTACK_COOLDOWN;
            PlayVerifiedEnemyAttackPresentation(enemy);
            ApplyDroneDamage(DRONE_ENEMY_CONTACT_DAMAGE, enemy);
        }

        private static bool EnemySharesDroneSpace(EnemyAI enemy)
        {
            return enemy != null && enemy.isOutside != _droneIsInside;
        }

        private static Vector3 GetDroneTargetPosition()
        {
            return CurrentDroneGroundPositionOr(_droneGroundPosition);
        }

        private static ulong GetEnemyKey(EnemyAI enemy)
        {
            if (enemy == null)
                return 0;
            if (enemy.NetworkObject != null)
                return enemy.NetworkObjectId;

            return unchecked((ulong)enemy.GetInstanceID());
        }

        private static void ApplyDroneDamage(int damage, EnemyAI sourceEnemy)
        {
            if (damage <= 0 || !CanDroneBeDamaged())
                return;
            if (!IsServerAuthority())
                return;

            _droneHealth = Mathf.Max(0, _droneHealth - damage);
            bool bigHit = damage > 3;
            PlayDroneHitLocal(damage, bigHit, _droneHealth);
            BroadcastDroneHit(damage, bigHit, _droneHealth);
            if (_droneHealth <= 0)
            {
                DestroyDroneFromDamage(sourceEnemy);
                return;
            }

            BroadcastDroneState();
        }

        private static void PlayDroneHitLocal(int damageAmount, bool bigHit, int healthAfter)
        {
            if (_droneVisual == null)
                return;

            Vector3 joltDirection = UnityEngine.Random.insideUnitSphere;
            joltDirection.y = 0f;
            if (joltDirection.sqrMagnitude <= 0.001f)
                joltDirection = Vector3.right;
            CourierDroneRuntimeAssets.PlayHitReaction(
                _droneVisual,
                bigHit,
                joltDirection.normalized,
                DRONE_HIT_JOLT_DISTANCE,
                DRONE_HIT_JOLT_SECONDS);
            MaybeDisplayOwnerAttackWarning(healthAfter);
        }

        private static void MaybeDisplayOwnerAttackWarning(int healthAfter)
        {
            if (healthAfter <= 0 || Time.time < _nextDroneAttackWarningAt)
                return;

            _nextDroneAttackWarningAt = Time.time + DRONE_ATTACK_WARNING_INTERVAL;
            int healthPercent = Mathf.CeilToInt(Mathf.Clamp01(healthAfter / (float)DRONE_MAX_HEALTH) * 100f);
            DisplayOwnerTip(_ownerPlayerId, $"Under attack — {healthPercent}%", isWarning: true);
        }

        private static void PlayVerifiedEnemyAttackPresentation(EnemyAI enemy)
        {
            if (enemy == null)
                return;

            int ownerPlayerId = Mathf.Max(0, _ownerPlayerId);
            if (enemy is CrawlerAI crawler)
            {
                TryPlayVerifiedEnemyAttack("CrawlerAI", () => crawler.HitPlayerClientRpc(ownerPlayerId));
                return;
            }

            if (enemy is HoarderBugAI hoarderBug)
            {
                TryPlayVerifiedEnemyAttack("HoarderBugAI", hoarderBug.HitPlayerClientRpc);
                return;
            }

            if (enemy is SandSpiderAI sandSpider)
                TryPlayVerifiedEnemyAttack("SandSpiderAI", () => sandSpider.HitPlayerClientRpc(ownerPlayerId));
        }

        private static void TryPlayVerifiedEnemyAttack(string enemyType, Action playAttack)
        {
            try
            {
                playAttack?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Courier Drone {enemyType} attack presentation skipped: {ex.Message}");
            }
        }

        private static void DestroyDroneFromDamage(EnemyAI sourceEnemy)
        {
            if (_droneDestroyedThisRound)
                return;

            _droneHealth = 0;
            _droneDestroyedThisRound = true;
            Vector3 deathPosition = CurrentDroneGroundPositionOr(_droneGroundPosition);
            if (deathPosition != Vector3.zero)
                _droneGroundPosition = deathPosition;

            DestroyDroneLocal(dropCargo: true, notifyOwner: true);
            BroadcastDroneDeath(_droneGroundPosition, _droneIsInside);
            BroadcastDroneState();
        }

        private static void DestroyDroneLocal(bool dropCargo, bool notifyOwner)
        {
            StopManualControl(resumeDrone: false, notifyOwner: false);
            _routeVersion++;
            CourierDroneRuntimeAssets.StopMagnetSound(_droneVisual);
            GameObject deathVisual = _droneVisual;
            Vector3 effectPosition = deathVisual != null
                ? deathVisual.transform.position
                : ToFlightPosition(_droneGroundPosition);
            bool animatedDeath = deathVisual != null && CourierDroneRuntimeAssets.PlayDeath(deathVisual);

            if (dropCargo)
                DropCarriedScrapAtDrone();
            else
                CarriedItems.Clear();

            PlannedItems.Clear();
            DroneThreats.Clear();
            NextDroneHitByEnemy.Clear();
            DestroyActiveGrenadeVisuals();
            _mode = DroneMode.NotSpawned;
            _isFollowingPlayer = false;
            _followSettled = false;
            ClearFollowPathCache();
            _reportedNoEntranceScrap = false;
            _commandCooldownEnd = 0f;
            _combatLandedUntil = 0f;
            _nextGuardScrapScanAt = 0f;
            _flashlightEnabled = false;
            _magnetPickupActive = false;
            _nextDroneAttackWarningAt = 0f;
            ClearPointerTargetState();

            if (!animatedDeath)
                SpawnDroneDeathEffect(effectPosition);

            if (deathVisual != null)
            {
                UnityEngine.Object.Destroy(deathVisual, animatedDeath ? DRONE_DEATH_VISUAL_SECONDS : 0f);
                if (_droneVisual == deathVisual)
                {
                    _droneVisual = null;
                    ClearDroneRendererCache();
                }
            }

            if (notifyOwner)
                DisplayOwnerTip(_ownerPlayerId, "Drone destroyed. It can be redeployed next round.", isWarning: true);
        }

        private static void SpawnDroneDeathEffect(Vector3 position)
        {
            if (position == Vector3.zero)
                return;

            GameObject flash = new GameObject("Y4NGZ_CourierDroneDeathFlash");
            flash.transform.position = position;
            Light light = flash.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.22f, 0.12f);
            light.range = 6f;
            light.intensity = 9f;
            light.shadows = LightShadows.None;
            UnityEngine.Object.Destroy(flash, 0.45f);
        }

        /// <summary>
        /// The drone writes the ship-boundary flags itself and nulls playerHeldBy first, so it
        /// bypasses every vanilla site the delivery ledger hooks (#215). Without an explicit
        /// report, drone-ferried scrap reaches the ship with no handler record and pays NOBODY,
        /// and a drone drop back outside leaves a stale handler behind.
        ///
        /// Credit goes to <paramref name="courierPlayerId"/> -- the player the drone is
        /// ferrying for, taken from the replicated courier command rather than from
        /// localPlayerController. That identity resolves to the same person on every machine,
        /// and because it names a fixed player rather than "whoever is local", a peer that
        /// never ran this routine can at worst fail to record: it can never produce a second,
        /// different payee. An unresolvable id records as unattributed and pays nobody.
        /// </summary>
        private static void MoveScrapToPlayer(GrabbableObject item, Vector3 dropPosition, bool dropIsInShip, bool dropIsInFactory, Vector3 worldScale, bool bodyWasKinematic, bool bodyUsedGravity, int courierPlayerId)
        {
            if (item == null)
                return;

            bool wasInsideShip = ProgressionManager.IsInsideShip(item);

            Transform parent = dropIsInShip && StartOfRound.Instance != null
                ? StartOfRound.Instance.elevatorTransform
                : null;

            item.transform.SetParent(parent, worldPositionStays: true);
            SetWorldScale(item.transform, worldScale);
            item.transform.position = dropPosition;
            SetWorldScale(item.transform, worldScale);
            item.parentObject = null;
            item.playerHeldBy = null;
            item.isHeld = false;
            item.isPocketed = false;
            item.isHeldByEnemy = false;
            item.grabbable = true;
            item.grabbableToEnemies = true;
            item.isInShipRoom = dropIsInShip;
            item.isInElevator = dropIsInShip;
            ProgressionManager.RecordAttributedBoundaryCrossing(
                ResolveCourierHandlerClientId(courierPlayerId), item, wasInsideShip);
            item.isInFactory = dropIsInFactory;
            item.hasHitGround = false;
            item.reachedFloorTarget = false;
            item.fallTime = 0f;
            item.floorYRot = -1;
            item.startFallingPosition = parent != null ? parent.InverseTransformPoint(dropPosition) : dropPosition;
            Vector3 floorPosition = item.GetItemFloorPosition(dropPosition);
            item.targetFloorPosition = parent != null ? parent.InverseTransformPoint(floorPosition) : floorPosition;
            item.EnablePhysics(enable: true);
            item.EnableItemMeshes(enable: true);
            SetItemKinematic(item, bodyWasKinematic, bodyUsedGravity);
            SetWorldScale(item.transform, worldScale);

            if (dropIsInShip)
            {
                try
                {
                    RoundManager.Instance?.CollectNewScrapForThisRound(item);
                    item.OnBroughtToShip();
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogDebug($"Courier Drone scrap collection bookkeeping skipped: {ex.Message}");
                }
            }
        }

        private static void EnsureDroneVisualAt(Vector3 groundPosition)
        {
            if (_droneVisual != null)
            {
                _droneVisual.SetActive(true);
                _droneVisual.transform.position = ToFlightPosition(groundPosition);
                _droneGroundPosition = groundPosition;
                CourierDroneRuntimeAssets.EnsureDroneAudio(_droneVisual);
                UpdateCarriedItemTransforms();
                return;
            }

            _droneVisual = CourierDroneRuntimeAssets.InstantiateDroneVisual(ToFlightPosition(groundPosition), Quaternion.identity)
                ?? CreateFallbackDroneVisual(ToFlightPosition(groundPosition));
            ClearDroneRendererCache();
            _droneGroundPosition = groundPosition;
            CourierDroneRuntimeAssets.EnsureDroneAudio(_droneVisual);
            UpdateCarriedItemTransforms();
        }

        private static void ResetDroneState(bool destroyVisual)
        {
            StopManualControl(resumeDrone: false, notifyOwner: false);
            _routeVersion++;
            CourierDroneRuntimeAssets.StopMagnetSound(_droneVisual);
            PlannedItems.Clear();
            CarriedItems.Clear();
            KnownGrabbableItems.Clear();
            DroneThreats.Clear();
            NextDroneHitByEnemy.Clear();
            DestroyActiveGrenadeVisuals();
            _ownerCourierTier = 0;
            _nextEnemyScanAt = 0f;
            _lastEnemyScanFrame = -1;
            _nextFlameAt = 0f;
            _nextGrenadeAt = 0f;
            _combatLandedUntil = 0f;
            _nextEntranceScrapScanAt = 0f;
            _nextGuardScrapScanAt = 0f;
            _flashlightEnabled = false;
            _magnetPickupActive = false;
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

        private static bool IsPointerFetchEligible(GrabbableObject item)
        {
            return IsCourierEligibleItem(item)
                && item.isInFactory == _droneIsInside
                && !IsAlreadyCarried(item.NetworkObjectId)
                && !IsAlreadyPlanned(item.NetworkObjectId)
                && !WouldExceedCargoWeight(item, GetReservedCargoWeightPounds());
        }

        private static bool IsCourierEligibleItem(GrabbableObject item)
        {
            return item != null
                && item.itemProperties != null
                && item.itemProperties.isScrap
                && !item.isHeld
                && !item.isPocketed
                && !item.isHeldByEnemy
                && !item.isInShipRoom
                && item.grabbable;
        }

        private static bool IsCourierEligibleEntranceScrap(GrabbableObject item, List<Vector3> searchPositions, float radiusSqr)
        {
            return IsCourierEligibleItem(item)
                && DistanceToClosestPointSqr(item.transform.position, searchPositions) <= radiusSqr;
        }

        private static MainEntranceRoute FindMainEntranceRoute()
        {
            EntranceTeleport[] entrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            MainEntranceRoute route = new MainEntranceRoute();

            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null || entrance.entranceId != 0)
                    continue;

                Vector3 position = ResolveEntrancePosition(entrance);
                if (position == Vector3.zero)
                    continue;

                if (entrance.isEntranceToBuilding)
                    route.OutsidePosition = FirstNonZero(route.OutsidePosition, position);
                else
                    route.InsidePosition = FirstNonZero(route.InsidePosition, position);
            }

            return route;
        }

        private static Vector3 ResolveEntrancePosition(EntranceTeleport entrance)
        {
            return entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
        }

        private static Vector3 FirstNonZero(Vector3 preferred, Vector3 fallback)
        {
            return preferred != Vector3.zero ? preferred : fallback;
        }

        private static List<Vector3> FindMainEntranceSearchPositions(CourierCommand command)
        {
            List<Vector3> positions = new List<Vector3>();
            AddRoutePoint(positions, FirstNonZero(command.InsideEntrancePosition, command.EntrancePosition));
            if (positions.Count == 0)
                AddRoutePoint(positions, command.OutsideEntrancePosition);
            return positions;
        }

        private static float DistanceToClosestPointSqr(Vector3 position, List<Vector3> points)
        {
            float best = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float distance = (position - points[i]).sqrMagnitude;
                if (distance < best)
                    best = distance;
            }

            return best;
        }

        private static Vector3 FindDroneStartPosition(PlayerControllerB player)
        {
            if (player == null)
                return Vector3.zero;

            if (IsPlayerInShip(player))
                return FindShipDroneDockPosition(player);

            Vector3 desired = player.transform.position
                + player.transform.right * 1.15f
                + player.transform.forward * 0.35f
                + Vector3.up * 0.15f;
            return ProjectToNavMesh(desired, 3.5f);
        }

        private static Vector3 FindDroneFollowPosition(PlayerControllerB player)
        {
            if (player == null)
                return Vector3.zero;

            if (IsPlayerInShip(player))
                return FindShipDroneDockPosition(player);

            Vector3 currentGround = CurrentDroneGroundPositionOr(Vector3.zero);
            if (currentGround != Vector3.zero)
            {
                Vector3 currentOffset = currentGround - player.transform.position;
                currentOffset.y = 0f;
                if (currentOffset.magnitude <= FOLLOW_RECHASE_DISTANCE)
                    return currentGround;
            }

            Vector3 ringDirection = currentGround != Vector3.zero
                ? currentGround - player.transform.position
                : Vector3.right;
            ringDirection.y = 0f;
            if (ringDirection.sqrMagnitude <= 0.001f)
                ringDirection = Vector3.right;
            ringDirection.Normalize();

            Vector3 desired = player.transform.position + ringDirection * FOLLOW_RING_RADIUS;
            return ProjectToNavMesh(desired, 3.5f);
        }

        private static bool IsPlayerInShip(PlayerControllerB player)
        {
            if (player == null || player.transform == null)
                return false;

            if (player.isInHangarShipRoom)
                return true;

            StartOfRound round = StartOfRound.Instance;
            return round != null
                && round.shipInnerRoomBounds != null
                && round.shipInnerRoomBounds.bounds.Contains(player.transform.position);
        }

        private static Vector3 FindShipDroneDockPosition(PlayerControllerB player)
        {
            if (player == null || player.transform == null)
                return Vector3.zero;

            Vector3 desired = player.transform.position
                + player.transform.right * 1.05f
                - player.transform.forward * 0.55f;

            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.insideShipPositions != null && round.insideShipPositions.Length > 0 && round.insideShipPositions[0] != null)
                    desired.y = round.insideShipPositions[0].position.y + 0.05f;
                else if (round.elevatorTransform != null)
                    desired.y = round.elevatorTransform.position.y + 0.05f;
                else if (round.middleOfShipNode != null)
                    desired.y = round.middleOfShipNode.position.y + 0.05f;

                if (round.shipInnerRoomBounds != null)
                {
                    Bounds bounds = round.shipInnerRoomBounds.bounds;
                    desired.x = ClampInsideBounds(desired.x, bounds.min.x, bounds.max.x, SHIP_DRONE_EDGE_MARGIN);
                    desired.z = ClampInsideBounds(desired.z, bounds.min.z, bounds.max.z, SHIP_DRONE_EDGE_MARGIN);

                    float maxGroundY = bounds.max.y - VISUAL_FLIGHT_HEIGHT - 0.25f;
                    if (maxGroundY > bounds.min.y)
                        desired.y = Mathf.Min(desired.y, maxGroundY);
                    desired.y = Mathf.Max(desired.y, bounds.min.y + 0.05f);
                }
            }

            return desired;
        }

        private static Vector3 ProjectToNavMesh(Vector3 point, float radius)
        {
            if (NavMesh.SamplePosition(point, out NavMeshHit hit, radius, NavMesh.AllAreas))
                return hit.position;

            return Vector3.zero;
        }

        private static int GetDroneCollisionMask()
        {
            return StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMask : ~0;
        }

        private static Vector3 ResampleGroundPoint(Vector3 candidate, Vector3 fallback)
        {
            if (candidate == Vector3.zero)
                return fallback;

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return hit.position;

            if (Physics.Raycast(candidate + Vector3.up * 2f, Vector3.down, out RaycastHit rayHit, 6f, GetDroneCollisionMask(), QueryTriggerInteraction.Ignore))
                return rayHit.point;

            return fallback != Vector3.zero ? fallback : candidate;
        }

#pragma warning disable Harmony003
        private static Vector3 ToSmoothedFlightPosition(Vector3 groundPosition, float currentFlightY)
        {
            float hoverHeight = ResolveDynamicHoverHeight(groundPosition, blockedAtCurrentHeight: false);
            return new Vector3(
                groundPosition.x,
                Mathf.MoveTowards(currentFlightY, groundPosition.y + hoverHeight, GROUND_VERTICAL_SMOOTH_SPEED * Time.deltaTime),
                groundPosition.z);
        }

        private static bool CanMoveDirectToFollowTarget(Vector3 currentGround, Vector3 targetGround)
        {
            if (currentGround == Vector3.zero || targetGround == Vector3.zero)
                return false;
            if ((targetGround - currentGround).sqrMagnitude > FOLLOW_DIRECT_MAX_DISTANCE * FOLLOW_DIRECT_MAX_DISTANCE)
                return false;

            Vector3 currentFlight = ToFlightPosition(currentGround);
            Vector3 targetFlight = ToFlightPosition(targetGround);
            Vector3 delta = targetFlight - currentFlight;
            float distance = delta.magnitude;
            if (distance <= 0.001f)
                return true;

            return !Physics.SphereCast(
                currentFlight,
                FOLLOW_COLLISION_RADIUS,
                delta.normalized,
                out _,
                distance,
                GetDroneCollisionMask(),
                QueryTriggerInteraction.Ignore);
        }

        private static bool CanReachWaypointAtHoverHeight(Vector3 currentGround, Vector3 candidateGround)
        {
            if (currentGround == Vector3.zero || candidateGround == Vector3.zero)
                return false;

            float hoverHeight = Mathf.Clamp(_dynamicHoverHeight, HOVER_MIN_CLEARANCE_HEIGHT, HOVER_MAX_CLEARANCE_HEIGHT);
            Vector3 fromFlight = new Vector3(currentGround.x, currentGround.y + hoverHeight, currentGround.z);
            Vector3 toFlight = new Vector3(candidateGround.x, candidateGround.y + hoverHeight, candidateGround.z);
            Vector3 delta = toFlight - fromFlight;
            float distance = delta.magnitude;
            if (distance <= 0.001f)
                return true;

            return !Physics.SphereCast(
                fromFlight,
                FOLLOW_COLLISION_RADIUS,
                delta.normalized,
                out _,
                distance,
                GetDroneCollisionMask(),
                QueryTriggerInteraction.Ignore);
        }

        private static bool TryResolveClutterMove(Vector3 fromFlight, Vector3 toFlight, Vector3 nextGround, out Vector3 adjustedFlight, out bool blocked)
        {
            adjustedFlight = toFlight;
            blocked = false;

            Vector3 horizontalDelta = new Vector3(toFlight.x - fromFlight.x, 0f, toFlight.z - fromFlight.z);
            float distance = horizontalDelta.magnitude;
            if (distance <= 0.001f)
            {
                float requestedHover = Mathf.Clamp(toFlight.y - nextGround.y, HOVER_MIN_CLEARANCE_HEIGHT, HOVER_MAX_CLEARANCE_HEIGHT);
                if (!IsHoverBandClear(nextGround, requestedHover))
                {
                    float raisedVerticalHover = ResolveDynamicHoverHeight(nextGround, blockedAtCurrentHeight: true);
                    adjustedFlight = new Vector3(toFlight.x, nextGround.y + raisedVerticalHover, toFlight.z);
                    return true;
                }

                return false;
            }

            Vector3 castStart = new Vector3(fromFlight.x, toFlight.y, fromFlight.z);
            if (TryMoveWithSphereCast(castStart, horizontalDelta, out Vector3 originalCandidate, out float originalTravel, out RaycastHit originalHit))
                return false;

            TryCollisionRepathThrottled();

            Vector3 slideCandidate = castStart;
            float slideTravel = 0f;
            TrySlideMove(castStart, horizontalDelta, originalHit.normal, out slideCandidate, out slideTravel);

            if (slideTravel > originalTravel)
            {
                adjustedFlight = new Vector3(slideCandidate.x, toFlight.y, slideCandidate.z);
                blocked = slideTravel <= 0.01f;
            }
            else
            {
                adjustedFlight = new Vector3(originalCandidate.x, toFlight.y, originalCandidate.z);
                blocked = originalTravel <= 0.01f;
            }

            if (!blocked)
                return true;

            float raisedHover = ResolveDynamicHoverHeight(nextGround, blockedAtCurrentHeight: true);
            Vector3 climbTarget = new Vector3(toFlight.x, nextGround.y + raisedHover, toFlight.z);
            Vector3 climbDelta = new Vector3(climbTarget.x - fromFlight.x, 0f, climbTarget.z - fromFlight.z);
            Vector3 climbStart = new Vector3(fromFlight.x, climbTarget.y, fromFlight.z);
            if (climbDelta.sqrMagnitude > 0.0001f && TryMoveWithSphereCast(climbStart, climbDelta, out Vector3 climbCandidate, out float climbTravel, out _))
            {
                adjustedFlight = new Vector3(climbCandidate.x, climbTarget.y, climbCandidate.z);
                blocked = false;
            }
            else if (IsHoverBandClear(nextGround, raisedHover))
            {
                adjustedFlight = new Vector3(fromFlight.x, climbTarget.y, fromFlight.z);
            }

            return true;
        }

        private static bool TrySlideMove(Vector3 castStart, Vector3 horizontalDelta, Vector3 hitNormal, out Vector3 slideCandidate, out float slideTravel)
        {
            slideCandidate = castStart;
            slideTravel = 0f;
            Vector3 slideDelta = Vector3.ProjectOnPlane(horizontalDelta, hitNormal);
            slideDelta.y = 0f;
            if (slideDelta.sqrMagnitude <= 0.0001f)
                return false;

            slideDelta = slideDelta.normalized * horizontalDelta.magnitude;
            return TryMoveWithSphereCast(castStart, slideDelta, out slideCandidate, out slideTravel, out _);
        }

        private static bool TryMoveWithSphereCast(Vector3 castStart, Vector3 horizontalDelta, out Vector3 candidate, out float travelled, out RaycastHit hit)
        {
            candidate = castStart;
            travelled = 0f;
            hit = default;

            float distance = horizontalDelta.magnitude;
            if (distance <= 0.001f)
                return true;

            Vector3 direction = horizontalDelta / distance;
            if (!Physics.SphereCast(castStart, FOLLOW_COLLISION_RADIUS, direction, out hit, distance, GetDroneCollisionMask(), QueryTriggerInteraction.Ignore))
            {
                candidate = castStart + horizontalDelta;
                travelled = distance;
                return true;
            }

            travelled = Mathf.Max(0f, hit.distance - FOLLOW_WALL_STOP_SHORT);
            candidate = castStart + direction * travelled;
            return false;
        }

        private static float ResolveDynamicHoverHeight(Vector3 groundPosition, bool blockedAtCurrentHeight)
        {
            if (groundPosition == Vector3.zero)
                return _dynamicHoverHeight;

            if (!blockedAtCurrentHeight && Time.time < _nextHoverProbeAt)
                return Mathf.Clamp(_dynamicHoverHeight, HOVER_MIN_CLEARANCE_HEIGHT, HOVER_MAX_CLEARANCE_HEIGHT);

            _nextHoverProbeAt = Time.time + HOVER_CLEARANCE_PROBE_INTERVAL;
            float targetHover = VISUAL_FLIGHT_HEIGHT;
            Vector3 probeStart = groundPosition + Vector3.up * HOVER_PROBE_TOP_HEIGHT;
            if (Physics.Raycast(probeStart, Vector3.down, out RaycastHit hit, HOVER_PROBE_TOP_HEIGHT + 0.05f, GetDroneCollisionMask(), QueryTriggerInteraction.Ignore))
            {
                float obstacleHeight = Mathf.Max(0f, hit.point.y - groundPosition.y);
                if (blockedAtCurrentHeight && obstacleHeight > HOVER_MIN_CLEARANCE_HEIGHT)
                    targetHover = Mathf.Max(VISUAL_FLIGHT_HEIGHT, obstacleHeight + HOVER_OBSTACLE_CLEARANCE);
            }

            if (blockedAtCurrentHeight)
            {
                for (float candidate = Mathf.Max(targetHover, _dynamicHoverHeight + 0.2f); candidate <= HOVER_MAX_CLEARANCE_HEIGHT + 0.001f; candidate += 0.2f)
                {
                    if (IsHoverBandClear(groundPosition, candidate))
                    {
                        targetHover = candidate;
                        break;
                    }
                }
            }

            _dynamicHoverHeight = Mathf.Clamp(targetHover, HOVER_MIN_CLEARANCE_HEIGHT, HOVER_MAX_CLEARANCE_HEIGHT);
            return _dynamicHoverHeight;
        }

        private static bool IsHoverBandClear(Vector3 groundPosition, float hoverHeight)
        {
            Vector3 center = new Vector3(groundPosition.x, groundPosition.y + hoverHeight, groundPosition.z);
            return !Physics.CheckSphere(center, FOLLOW_COLLISION_RADIUS, GetDroneCollisionMask(), QueryTriggerInteraction.Ignore);
        }

        private static void TryCollisionRepathThrottled()
        {
            if (Time.time < _nextCollisionRepathAt)
                return;

            _nextCollisionRepathAt = Time.time + COLLISION_REPATH_INTERVAL;
            ClearFollowPathCache();
        }
#pragma warning restore Harmony003

        private static Vector3 ApplyFlightBob(Vector3 flightPosition)
        {
            float phase = Time.time * IDLE_SWAY_BOB_FREQUENCY * Mathf.PI * 2f;
            return flightPosition + Vector3.up * (Mathf.Sin(phase) * IDLE_SWAY_BOB_AMPLITUDE);
        }

        private static void ApplyIdleSway(Vector3 groundPosition, PlayerControllerB owner)
        {
            if (_droneVisual == null || groundPosition == Vector3.zero)
                return;

            float bobPhase = Time.time * IDLE_SWAY_BOB_FREQUENCY * Mathf.PI * 2f;
            float driftPhase = Time.time * IDLE_SWAY_DRIFT_FREQUENCY * Mathf.PI * 2f;
            Vector3 sway = new Vector3(
                Mathf.Sin(driftPhase) * IDLE_SWAY_DRIFT_AMPLITUDE,
                Mathf.Sin(bobPhase) * IDLE_SWAY_BOB_AMPLITUDE,
                Mathf.Sin(driftPhase + Mathf.PI * 0.5f) * IDLE_SWAY_DRIFT_AMPLITUDE);

            _droneVisual.transform.position = ToFlightPosition(groundPosition) + sway;
            EaseIdleYawTowardOwner(owner);
        }

        private static void EaseIdleYawTowardOwner(PlayerControllerB owner)
        {
            if (_droneVisual == null)
                return;

            PlayerControllerB targetOwner = owner ?? ResolvePlayer(_ownerPlayerId);
            if (targetOwner == null || targetOwner.transform == null)
                return;

            Vector3 lookDirection = targetOwner.transform.position - _droneVisual.transform.position;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude <= 0.001f)
                return;

            Quaternion desired = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            _droneVisual.transform.rotation = Quaternion.Slerp(_droneVisual.transform.rotation, desired, Mathf.Clamp01(Time.deltaTime * IDLE_SWAY_YAW_SPEED));
        }

        private static float ClampInsideBounds(float value, float min, float max, float margin)
        {
            if (max <= min)
                return value;

            float innerMin = min + margin;
            float innerMax = max - margin;
            if (innerMin > innerMax)
                return (min + max) * 0.5f;

            return Mathf.Clamp(value, innerMin, innerMax);
        }

        private static Vector3 FindPlayerDropPosition(PlayerControllerB player)
        {
            if (player == null)
                return Vector3.zero;

            return player.transform.position
                + player.transform.right * 1.25f
                + player.transform.forward * 0.65f
                + Vector3.up * 0.1f;
        }

        private static Vector3 FindShipDropPosition(PlayerControllerB player)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.middleOfShipNode != null)
                    return round.middleOfShipNode.position + Vector3.up * 0.1f;

                if (round.insideShipPositions != null && round.insideShipPositions.Length > 0 && round.insideShipPositions[0] != null)
                    return round.insideShipPositions[0].position + Vector3.up * 0.1f;

                if (round.shipInnerRoomBounds != null)
                    return round.shipInnerRoomBounds.bounds.center + Vector3.up * 0.1f;

                if (round.elevatorTransform != null)
                    return round.elevatorTransform.position + Vector3.up * 0.1f;
            }

            return FindPlayerDropPosition(player);
        }

        private static Vector3 FindShipApproachPosition(Vector3 fallback)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.outsideDoorPosition != null)
                    return round.outsideDoorPosition.position + Vector3.up * 0.1f;

                if (round.shipDoorNode != null)
                    return round.shipDoorNode.position + Vector3.up * 0.1f;

                if (round.groundOutsideShipSpawnPosition != null)
                    return round.groundOutsideShipSpawnPosition.position + Vector3.up * 0.1f;
            }

            return fallback;
        }

        private static Vector3 CurrentDroneGroundPositionOr(Vector3 fallback)
        {
            return _droneGroundPosition != Vector3.zero ? _droneGroundPosition : fallback;
        }

        private static void AddRoutePoint(List<Vector3> route, Vector3 point)
        {
            if (point == Vector3.zero)
                return;

            if (route.Count > 0 && (route[route.Count - 1] - point).sqrMagnitude < 0.25f)
                return;

            route.Add(point);
        }

        private static Vector3 ToFlightPosition(Vector3 groundPosition)
        {
            return groundPosition + Vector3.up * VISUAL_FLIGHT_HEIGHT;
        }

        private static bool IsEntranceTransition(CourierCommand command, Vector3 from, Vector3 to)
        {
            if (command == null || command.OutsideEntrancePosition == Vector3.zero || command.InsideEntrancePosition == Vector3.zero)
                return false;

            return (IsSameRoutePoint(from, command.OutsideEntrancePosition) && IsSameRoutePoint(to, command.InsideEntrancePosition))
                || (IsSameRoutePoint(from, command.InsideEntrancePosition) && IsSameRoutePoint(to, command.OutsideEntrancePosition));
        }

        private static bool IsMainEntrancePoint(CourierCommand command, Vector3 point)
        {
            if (command == null)
                return false;

            return IsSameRoutePoint(point, command.OutsideEntrancePosition)
                || IsSameRoutePoint(point, command.InsideEntrancePosition);
        }

        private static bool IsSameRoutePoint(Vector3 a, Vector3 b)
        {
            return a != Vector3.zero && b != Vector3.zero && (a - b).sqrMagnitude <= 0.35f;
        }

#pragma warning disable Harmony003
        private static Vector3 GetCarryWorldPosition(Vector3 carryOffset)
        {
            if (_droneVisual == null)
                return carryOffset;

            Transform drone = _droneVisual.transform;
            Transform carryAnchor = CourierDroneRuntimeAssets.FindCarryAnchor(_droneVisual);
            if (carryAnchor != null)
            {
                Vector3 anchorOffset = new Vector3(carryOffset.x, carryOffset.y + CARRY_DANGLE_HEIGHT, carryOffset.z);
                return carryAnchor.position
                    + drone.right * anchorOffset.x
                    + drone.forward * anchorOffset.z
                    + Vector3.up * anchorOffset.y;
            }

            return drone.position
                + drone.right * carryOffset.x
                + drone.forward * carryOffset.z
                + Vector3.up * carryOffset.y;
        }
#pragma warning restore Harmony003

        private static void UpdateCarriedItemTransforms()
        {
            if (_droneVisual == null)
                return;

            for (int i = 0; i < CarriedItems.Count; i++)
            {
                CarriedCourierItem carried = CarriedItems[i];
                GrabbableObject item = carried.Item;
                if (item == null)
                    continue;

                item.transform.SetParent(null, worldPositionStays: true);
                item.parentObject = null;
                item.isHeld = false;
                item.isPocketed = false;
                item.isHeldByEnemy = false;
                item.playerHeldBy = null;
                Vector3 carryPosition = GetCarryWorldPosition(carried.CarryOffset);
                Vector3 carryGround = ResampleGroundPoint(new Vector3(carryPosition.x, _droneGroundPosition.y, carryPosition.z), _droneGroundPosition);
                if (carryGround != Vector3.zero)
                    carryPosition.y = Mathf.Max(carryPosition.y, carryGround.y + CARRY_FLOOR_CLEARANCE);
                item.transform.position = carryPosition;
                item.transform.rotation = _droneVisual.transform.rotation * carried.CarryLocalRotation;
                SetWorldScale(item.transform, carried.WorldScale);
            }
        }

        private static void SetItemKinematic(GrabbableObject item, bool isKinematic, bool useGravity)
        {
            if (item == null || item.propBody == null)
                return;

            item.propBody.velocity = Vector3.zero;
            item.propBody.angularVelocity = Vector3.zero;
            item.propBody.isKinematic = isKinematic;
            item.propBody.useGravity = useGravity;
        }

#pragma warning disable Harmony003
        private static void SetWorldScale(Transform transform, Vector3 worldScale)
        {
            if (transform == null || worldScale == Vector3.zero)
                return;

            Transform parent = transform.parent;
            if (parent == null)
            {
                transform.localScale = worldScale;
                return;
            }

            Vector3 parentScale = parent.lossyScale;
            transform.localScale = new Vector3(
                SafeDivideScale(worldScale.x, parentScale.x, transform.localScale.x),
                SafeDivideScale(worldScale.y, parentScale.y, transform.localScale.y),
                SafeDivideScale(worldScale.z, parentScale.z, transform.localScale.z));
        }

        private static float SafeDivideScale(float worldAxis, float parentAxis, float fallback)
        {
            return Mathf.Abs(parentAxis) > 0.0001f ? worldAxis / parentAxis : fallback;
        }
#pragma warning restore Harmony003

        private static bool IsAlreadyCarried(ulong networkObjectId)
        {
            for (int i = 0; i < CarriedItems.Count; i++)
            {
                if (CarriedItems[i].Move.NetworkObjectId == networkObjectId)
                    return true;
            }

            return false;
        }

        private static bool IsAlreadyPlanned(ulong networkObjectId)
        {
            for (int i = 0; i < PlannedItems.Count; i++)
            {
                if (PlannedItems[i].NetworkObjectId == networkObjectId)
                    return true;
            }

            return false;
        }

        private static void StartCoroutineOnAvailableHost(IEnumerator routine)
        {
            MonoBehaviour host = HUDManager.Instance != null
                ? (MonoBehaviour)HUDManager.Instance
                : UnityEngine.Object.FindObjectOfType<StartOfRound>();
            if (host != null)
                host.StartCoroutine(routine);
        }

        private static GameObject CreateFallbackDroneVisual(Vector3 position)
        {
            GameObject drone = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            drone.name = "Y4NGZ_CourierDrone_Placeholder";
            drone.transform.position = position;
            drone.transform.localScale = Vector3.one * 0.45f;
            Collider collider = drone.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            Renderer renderer = drone.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Standard"));
                renderer.material.color = new Color(0.45f, 0.95f, 1f, 0.65f);
            }

            Light light = drone.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.45f, 0.95f, 1f);
            light.range = 5f;
            light.intensity = 2f;
            light.shadows = LightShadows.None;
            return drone;
        }

        private static void FaceTravelDirection(Transform transform, float deltaX, float deltaY, float deltaZ)
        {
            Vector3 delta = new Vector3(deltaX, deltaY, deltaZ);
            if (transform == null || delta.sqrMagnitude <= 0.0001f)
                return;

            Vector3 flatDirection = new Vector3(delta.x, 0f, delta.z);
            if (flatDirection.sqrMagnitude <= 0.0001f)
                flatDirection = transform.forward;

            flatDirection.Normalize();
            Vector3 currentFlatForward = new Vector3(transform.forward.x, 0f, transform.forward.z);
            if (currentFlatForward.sqrMagnitude <= 0.0001f)
                currentFlatForward = flatDirection;
            else
                currentFlatForward.Normalize();

            float signedTurn = Vector3.SignedAngle(currentFlatForward, flatDirection, Vector3.up);
            float bank = Mathf.Clamp(-signedTurn * 0.45f, -VISUAL_MAX_BANK, VISUAL_MAX_BANK);
            float pitch = Mathf.Lerp(0f, VISUAL_FORWARD_TILT, Mathf.Clamp01(flatDirection.magnitude));
            Quaternion desired = Quaternion.LookRotation(flatDirection, Vector3.up) * Quaternion.Euler(pitch, 0f, bank);
            transform.rotation = Quaternion.Slerp(transform.rotation, desired, Mathf.Clamp01(Time.deltaTime * VISUAL_TURN_SPEED));
        }

        private static void DisplayOwnerTip(int playerId, string message, bool isWarning)
        {
            if (HUDManager.Instance == null)
                return;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local != null && playerId >= 0 && (int)local.playerClientId != playerId)
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
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
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

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 3 + sizeof(ulong) + 16, Allocator.Temp);
            try
            {
                WritePointerCommandPayload(ref writer, playerId, courierTier, commandType, targetReference);
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

        private static void WritePointerCommandPayload(ref FastBufferWriter writer, int playerId, int courierTier, CourierCommandType commandType, NetworkObjectReference targetReference)
        {
            writer.WriteValueSafe(playerId);
            writer.WriteValueSafe(courierTier);
            writer.WriteValueSafe((int)commandType);
            writer.WriteNetworkSerializable(targetReference);
        }

        private static void OnReceivePointerCommand(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                int playerId;
                int courierTier;
                int commandType;
                NetworkObjectReference targetReference;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out courierTier);
                reader.ReadValueSafe(out commandType);
                reader.ReadNetworkSerializable(out targetReference);

                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && senderClientId != NetworkManager.ServerClientId)
                {
                    PlayerControllerB player = ResolvePlayer(playerId) ?? ResolvePlayerByActualClientId(senderClientId);
                    if (player == null)
                        return;

                    playerId = (int)player.playerClientId;
                    if (HandlePointerCommand(playerId, courierTier, (CourierCommandType)commandType, targetReference))
                        BroadcastPointerCommand(playerId, courierTier, (CourierCommandType)commandType, targetReference);
                    return;
                }

                if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
                    return;

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

        private static void SendRequest(int playerId, CourierCommandType commandType)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(playerId);
                writer.WriteValueSafe(CourierDroneUpgrade.GetTier());
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
                int playerId;
                int courierTier;
                int commandType;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out courierTier);
                reader.ReadValueSafe(out commandType);
                PlayerControllerB player = ResolvePlayer(playerId) ?? ResolvePlayerByActualClientId(senderClientId);
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

        private static void BroadcastGrenadeExplosion(int grenadeId, Vector3 position)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(grenadeId);
                WriteVector(ref writer, position.x, position.y, position.z);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(MSG_COURIER_GRENADE_EXPLODE, writer);
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

        private static void SendManualFireRequest(int playerId, ManualFireKind fireKind, Vector3 targetPosition)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient) return;
            if (NetworkManager.Singleton.CustomMessagingManager == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 2 + sizeof(float) * 3, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(playerId);
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
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
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
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
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
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
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
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
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
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
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
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
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
            if (NetworkManager.Singleton != null && senderClientId == NetworkManager.Singleton.LocalClientId)
                return;

            try
            {
                int grenadeId;
                reader.ReadValueSafe(out grenadeId);
                Vector3 position = ReadVector(ref reader);
                PlayGrenadeExplosionLocal(grenadeId, position);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CourierDronePatch: malformed grenade explosion sync: {ex.Message}");
            }
        }

        private static void OnReceiveManualPose(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network != null && senderClientId == network.LocalClientId)
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

                // Relay before the local-piloting bail-out: a host that is itself flying the drone
                // still has to forward the message, or the other clients never see this pilot.
                if (network != null && network.IsServer && senderClientId != network.LocalClientId)
                {
                    PlayerControllerB sender = ResolvePlayerByActualClientId(senderClientId);
                    if (sender == null)
                        return;

                    playerId = (int)sender.playerClientId;
                    RelayManualPose(senderClientId, playerId, activeFlag, insideFlag, position, yaw);
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
            if (network != null && senderClientId == network.LocalClientId)
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
                // player behind the connection the message arrived on, then fans it out.
                if (network != null && network.IsServer && senderClientId != network.LocalClientId)
                {
                    PlayerControllerB sender = ResolvePlayerByActualClientId(senderClientId);
                    if (sender == null)
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
                int playerId;
                int fireKind;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out fireKind);
                Vector3 targetPosition = ReadVector(ref reader);

                PlayerControllerB player = ResolvePlayer(playerId) ?? ResolvePlayerByActualClientId(senderClientId);
                if (player == null)
                    return;

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

        private struct PointerTarget
        {
            internal CourierCommandType CommandType;
            internal NetworkObjectReference TargetReference;
            internal Color ReticleColor;

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
