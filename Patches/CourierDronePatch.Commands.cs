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
        private static List<CourierItemMove> FindEntranceScrap(CourierCommand command, List<Vector3> searchPositions = null)
        {
            List<CourierItemMove> moves = new List<CourierItemMove>();
            List<Vector3> positions = searchPositions ?? FindMainEntranceSearchPositions(command);
            if (positions.Count == 0)
                return moves;

            GrabbableObject[] items = ScanGrabbableObjectsForCommandOrSweep();
            float radiusSqr = CourierDroneUpgrade.PICKUP_RADIUS * CourierDroneUpgrade.PICKUP_RADIUS;
            // #500 perf: only items inside the radius are sorted, by a distance computed once each,
            // instead of sorting the whole sweep with a comparer that recomputed both distances.
            List<EntranceScrapCandidate> candidates = EntranceScrapCandidates;
            candidates.Clear();
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (!IsCourierEligibleItem(item))
                    continue;

                float distanceSqr = DistanceToClosestPointSqr(item.transform.position, positions);
                if (distanceSqr <= radiusSqr)
                    candidates.Add(new EntranceScrapCandidate(item, distanceSqr));
            }

            candidates.Sort(EntranceScrapDistanceComparer);
            float reservedWeightLb = GetReservedCargoWeightPounds();
            for (int i = 0; i < candidates.Count && moves.Count < CourierDroneUpgrade.MAX_ITEMS_PER_SWEEP; i++)
            {
                GrabbableObject item = candidates[i].Item;
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

            candidates.Clear();

            return moves;
        }

        private static GrabbableObject[] ScanGrabbableObjectsForCommandOrSweep()
        {
            GrabbableObject[] items = UnityEngine.Object.FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None);
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
            // F-DRONE-6: seeds the host-side pose watchdog so the link is not dropped before the
            // pilot's first pose arrives.
            _lastManualPoseAt = Time.time;

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

        /// <summary>
        /// F-DRONE-3: a route the drone physically cannot finish used to leave it wedged in its BUSY
        /// mode for the rest of the round with the cargo undelivered - no coroutine was left running,
        /// so nothing ever moved _mode again and every tablet command answered "BUSY". Each route now
        /// gets one retry on a freshly built path (the route is rebuilt from where the drone actually
        /// stopped) and then falls back to owner-follow, which is a mode every command accepts again.
        /// </summary>
        private static bool IsRouteRetryNeeded(int version)
        {
            return version == _routeVersion && _lastRouteMoveBlocked;
        }

        private static void PrepareRouteRetry()
        {
            _lastRouteMoveBlocked = false;
            ClearFollowPathCache();
            ResetFollowMovementState();
        }

        private static void RecoverFromBlockedRoute(int playerId, int version)
        {
            if (version != _routeVersion)
                return;

            _lastRouteMoveBlocked = false;
            _mode = DroneMode.IdleAtPlayer;
            _isFollowingPlayer = false;
            _followSettled = false;
            ClearPointerTargetState();
            StartIdleFollow(++_routeVersion);
            DisplayOwnerTip(playerId, "Route blocked — drone returning to you.", isWarning: true);
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                BroadcastDroneState();
        }

        private static IEnumerator FlyToEntranceRoutine(CourierCommand command, int version)
        {
            _mode = DroneMode.FlyingToEntrance;
            List<Vector3> route = BuildRouteToEntrance(command);
            yield return MoveAlongRoute(command, route, version, pickUpAtEntrance: false);
            if (IsRouteRetryNeeded(version))
            {
                PrepareRouteRetry();
                route = BuildRouteToEntrance(command);
                yield return MoveAlongRoute(command, route, version, pickUpAtEntrance: false);
                if (IsRouteRetryNeeded(version))
                {
                    RecoverFromBlockedRoute(command.PlayerId, version);
                    yield break;
                }
            }

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
            if (IsRouteRetryNeeded(version))
            {
                // F-DRONE-3: one retry, then hand the drone back to follow instead of leaving it
                // stuck in FlyingToPlayer with the cargo still glued underneath.
                PrepareRouteRetry();
                yield return MoveAlongRoute(command, BuildRouteToDrop(command, includeShipApproach: false), version, pickUpAtEntrance: false);
                if (IsRouteRetryNeeded(version))
                {
                    RecoverFromBlockedRoute(command.PlayerId, version);
                    yield break;
                }
            }

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
            ReleaseCarriedItems();
            DroneThreats.Clear();
            DroneAttackThreats.Clear();
            NextDroneHitByEnemy.Clear();
            NextDogEnrageAtByEnemy.Clear();
            DogDroneEnrageTargetByEnemy.Clear();
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
            if (IsRouteRetryNeeded(version))
            {
                // F-DRONE-3: without this the tablet read RETURNING TO SHIP forever and up to 150 lb
                // of scrap was never banked.
                PrepareRouteRetry();
                yield return MoveAlongRoute(command, BuildRouteToDrop(command, includeShipApproach: true), version, pickUpAtEntrance: false);
                if (IsRouteRetryNeeded(version))
                {
                    RecoverFromBlockedRoute(command.PlayerId, version);
                    yield break;
                }
            }

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

            List<Vector3> searchPositions = GuardPickupSearchPositions;
            searchPositions.Clear();
            AddRoutePoint(searchPositions, droneGround);
            PlayerControllerB owner = ResolvePlayer(_ownerPlayerId);
            if (owner != null && !owner.isPlayerDead && owner.transform != null)
                AddRoutePoint(searchPositions, FindDroneFollowPosition(owner));

            GrabbableObject[] items = ScanGrabbableObjectsForCommandOrSweep();
            float radiusSqr = GUARD_PICKUP_RADIUS * GUARD_PICKUP_RADIUS;
            float carriedWeight = GetCarriedCargoWeightPounds();
            // #500 perf: one pass keeping the closest eligible item (distance computed once per item)
            // replaces a full-sweep sort whose comparer recomputed both distances per comparison.
            GrabbableObject best = null;
            float bestDistanceSqr = float.MaxValue;
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (!IsCourierEligibleItem(item))
                    continue;

                float distanceSqr = DistanceToClosestPointSqr(item.transform.position, searchPositions);
                if (!(distanceSqr <= radiusSqr) || distanceSqr >= bestDistanceSqr)
                    continue;
                if (item.isInFactory != _droneIsInside)
                    continue;
                if (IsAlreadyCarried(item.NetworkObjectId) || IsAlreadyPlanned(item.NetworkObjectId))
                    continue;
                if (WouldExceedCargoWeight(item, carriedWeight))
                    continue;

                best = item;
                bestDistanceSqr = distanceSqr;
            }

            return best;
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
    }
}
