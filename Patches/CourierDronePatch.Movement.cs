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

        private static IEnumerator MoveAlongRoute(CourierCommand command, List<Vector3> route, int version, bool pickUpAtEntrance)
        {
            if (_droneVisual == null || route.Count == 0)
                yield break;

            _lastRouteMoveBlocked = false;
            _droneGroundPosition = route[0];
            Vector3 previousPosition = _droneVisual.transform.position;
            for (int i = 1; i < route.Count; i++)
            {
                // F-DRONE-10: the visual can be nulled from outside the coroutine (DestroyDroneLocal /
                // ResetDroneState) between yields, so every dereference below is guarded.
                if (version != _routeVersion || _lastRouteMoveBlocked || _droneVisual == null)
                    yield break;

                Vector3 from = route[i - 1];
                Vector3 to = route[i];
                if (IsEntranceTransition(command, from, to))
                {
                    _droneVisual.SetActive(false);
                    yield return new WaitForSeconds(0.16f);
                    if (version != _routeVersion || _droneVisual == null)
                        yield break;

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
                        if (version != _routeVersion || _droneVisual == null)
                            yield break;

                        yield return MoveDroneSegment(path[p - 1], path[p], previousPosition.x, previousPosition.y, previousPosition.z, version);
                        if (_lastRouteMoveBlocked || version != _routeVersion || _droneVisual == null)
                            yield break;

                        previousPosition = _droneVisual.transform.position;
                    }

                    if (version != _routeVersion || _droneVisual == null)
                        yield break;

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
                if (version != _routeVersion || _droneVisual == null)
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

            // F-DRONE-10: the loop exits on its own timer too, so the fall-through needs the same
            // null/version guard the loop body has.
            if (_droneVisual == null || version != _routeVersion)
                yield break;

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
            // F-DRONE-9: force a fresh cargo floor sample after a teleport/route change rather than
            // carrying the throttled one across.
            _nextCarriedGroundProbeAt = 0f;
            _carriedGroundValid = false;
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

        private static MainEntranceRoute FindMainEntranceRoute()
        {
            // #500 perf: BuildCommandContext calls this on every command and guard pickup. Entrances
            // spawn with the level and do not move, so the array is cached per round, as in
            // FieldTabletScreenRuntime.ResolveRoundEntrances: keyed on StartOfRound, refetched when an
            // entry is destroyed, an empty result never kept, and dropped in ResetDroneState.
            StartOfRound round = StartOfRound.Instance;
            EntranceTeleport[] entrances = _cachedMainEntrances;
            bool cacheIntact = entrances != null && entrances.Length > 0 && _cachedMainEntranceRound == round;
            for (int i = 0; cacheIntact && i < entrances.Length; i++)
            {
                if (entrances[i] == null)
                    cacheIntact = false;
            }

            if (!cacheIntact)
            {
                entrances = UnityEngine.Object.FindObjectsByType<EntranceTeleport>(FindObjectsSortMode.None);
                _cachedMainEntrances = entrances.Length > 0 ? entrances : null;
                _cachedMainEntranceRound = round;
            }

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
    }
}
