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
            // F-DRONE-5: capture the body state BEFORE the magnet freezes it, then make sure every
            // abort below restores it. The item is not in CarriedItems yet, so nothing else ever
            // would - an interrupted lift used to leave the scrap frozen weightless in mid-air.
            bool bodyWasKinematic = item.propBody != null && item.propBody.isKinematic;
            bool bodyUsedGravity = item.propBody != null && item.propBody.useGravity;
            item.EnablePhysics(enable: false);
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
                    RestoreDroppedItem(item, bodyWasKinematic, bodyUsedGravity);
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
                RestoreDroppedItem(item, bodyWasKinematic, bodyUsedGravity);
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

        /// <summary>
        /// F-DRONE-5: puts an item the magnet froze back the way vanilla leaves a dropped prop.
        /// Used by every <see cref="MagnetPickUpScrap"/> abort after the lift disables physics, and
        /// by <see cref="ReleaseCarriedItems"/> when the cargo is released without being delivered.
        /// Deliberately leaves the transform parent alone: an aborted lift never reparented it.
        /// </summary>
        private static void RestoreDroppedItem(GrabbableObject item, bool bodyWasKinematic, bool bodyUsedGravity)
        {
            if (item == null)
                return;

            try
            {
                item.parentObject = null;
                item.playerHeldBy = null;
                item.isHeld = false;
                item.isPocketed = false;
                item.isHeldByEnemy = false;
                item.grabbable = true;
                item.grabbableToEnemies = true;
                item.hasHitGround = false;
                item.reachedFloorTarget = false;
                item.fallTime = 0f;

                Transform parent = item.transform.parent;
                Vector3 position = item.transform.position;
                item.startFallingPosition = parent != null ? parent.InverseTransformPoint(position) : position;
                Vector3 floorPosition = item.GetItemFloorPosition(position);
                item.targetFloorPosition = parent != null ? parent.InverseTransformPoint(floorPosition) : floorPosition;

                item.EnablePhysics(enable: true);
                item.EnableItemMeshes(enable: true);
                SetItemKinematic(item, bodyWasKinematic, bodyUsedGravity);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Courier Drone item release skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// F-DRONE-5: releases the cargo where it hangs instead of the bare CarriedItems.Clear()
        /// that used to strand every carried item frozen and non-grabbable.
        /// </summary>
        private static void ReleaseCarriedItems()
        {
            for (int i = 0; i < CarriedItems.Count; i++)
            {
                CarriedCourierItem carried = CarriedItems[i];
                if (carried.Item == null)
                    continue;

                SetWorldScale(carried.Item.transform, carried.WorldScale);
                RestoreDroppedItem(carried.Item, carried.BodyWasKinematic, carried.BodyUsedGravity);
            }

            CarriedItems.Clear();
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

#pragma warning disable Harmony003
        private static Vector3 GetCarryWorldPosition(Vector3 carryOffset)
        {
            if (_droneVisual == null)
                return carryOffset;

            Transform drone = _droneVisual.transform;
            // #500 perf: the anchor is cached per drone visual, so the per-item per-frame path skips
            // EnsureSteampunkController (GetComponentInChildren + Initialize). A missing anchor is not
            // cached: the fallback visual gets its controller lazily, so it is resolved again next call.
            if (_carryAnchorCacheRoot != _droneVisual || _carryAnchorCache == null)
            {
                _carryAnchorCacheRoot = _droneVisual;
                _carryAnchorCache = CourierDroneRuntimeAssets.FindCarryAnchor(_droneVisual);
            }

            Transform carryAnchor = _carryAnchorCache;
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
            if (_droneVisual == null || CarriedItems.Count == 0)
                return;

            // F-DRONE-9: this used to run a NavMesh.SamplePosition (plus a possible Physics.Raycast)
            // for every carried item every frame - up to twelve probes a frame at the cargo cap. The
            // items all dangle within half a metre of the drone, so one floor sample on the existing
            // hover-probe cadence is enough for the clearance clamp below.
            if (Time.time >= _nextCarriedGroundProbeAt)
            {
                _nextCarriedGroundProbeAt = Time.time + HOVER_CLEARANCE_PROBE_INTERVAL;
                Vector3 carryGround = ResampleGroundPoint(_droneGroundPosition, _droneGroundPosition);
                _carriedGroundValid = carryGround != Vector3.zero;
                _carriedGroundY = carryGround.y;
            }

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
                if (_carriedGroundValid)
                    carryPosition.y = Mathf.Max(carryPosition.y, _carriedGroundY + CARRY_FLOOR_CLEARANCE);
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
    }
}
