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
        private static void UpdatePointerTargeting(PlayerControllerB player)
        {
            if (!CanPointerCommandBeActive(player))
            {
                _pointerCachedTarget = default;
                _nextPointerResolveAt = 0f;
                FieldTabletScreenRuntime.SetDronePointerReticle(false, Color.clear);
                return;
            }

            // #500 perf: the ray runs at most once per POINTER_RESOLVE_INTERVAL. A command press, or a
            // cached target destroyed since, forces a fresh resolve so a command acts on a current target.
            bool commandPressed = WasPointerCommandPressedThisFrame();
            float now = Time.unscaledTime;
            if (commandPressed || now >= _nextPointerResolveAt || (_pointerCachedTarget.IsValid && _pointerCachedTarget.Source == null))
            {
                _nextPointerResolveAt = now + POINTER_RESOLVE_INTERVAL;
                _pointerCachedTarget = FindPointerTarget(player);
            }

            PointerTarget target = _pointerCachedTarget;
            if (!target.IsValid)
            {
                FieldTabletScreenRuntime.SetDronePointerReticle(false, Color.clear);
                return;
            }

            string label = BuildPointerReticleLabel(target.CommandType);
            FieldTabletScreenRuntime.SetDronePointerReticle(true, target.ReticleColor, 0f, label);
            if (Time.time < _nextPointerCommandAt || !commandPressed)
                return;

            _nextPointerCommandAt = Time.time + POINTER_COMMAND_COOLDOWN_SECONDS;
            FieldTabletScreenRuntime.SetDronePointerReticle(true, target.ReticleColor, 0.18f, label);
            SendPointerCommand((int)player.playerClientId, CourierDroneUpgrade.GetTier(), target.CommandType, target.TargetReference);
        }

        /// <summary>
        /// F-DRONE-9: this runs every frame from the PlayerControllerB.Update postfix. Both the
        /// binding lookup (GetBindingDisplayString + Trim + ToUpperInvariant) and the concatenation
        /// allocate, and the binding only changes when the player rebinds, so the pair of labels is
        /// cached and refreshed on a slow timer instead of being rebuilt per frame.
        /// </summary>
        private static string BuildPointerReticleLabel(CourierCommandType commandType)
        {
            if (_pointerAttackLabel == null || Time.time >= _nextPointerLabelRefreshAt)
            {
                _nextPointerLabelRefreshAt = Time.time + POINTER_LABEL_REFRESH_SECONDS;
                string key = Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.CourierDroneCommand, "MMB");
                _pointerAttackLabel = "[" + key + "] ATTACK";
                _pointerFetchLabel = "[" + key + "] FETCH";
            }

            return commandType == CourierCommandType.PointerAttack ? _pointerAttackLabel : _pointerFetchLabel;
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
            int hitCount = Physics.RaycastNonAlloc(ray, PointerRaycastHits, POINTER_COMMAND_RANGE, GetPointerRaycastMask(), QueryTriggerInteraction.Collide);
            if (hitCount <= 0)
                return default;

            SortPointerHitsByDistance(hitCount);
            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = PointerRaycastHits[i].collider;
                if (collider == null)
                    continue;

                Transform hitTransform = collider.transform;
                if (hitTransform != null && player != null && hitTransform.IsChildOf(player.transform))
                    continue;
                if (hitTransform != null && _droneVisual != null && hitTransform.IsChildOf(_droneVisual.transform))
                    continue;
                if (hitTransform != null && IsFieldTabletTransform(hitTransform))
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

        private static bool IsFieldTabletTransform(Transform hitTransform)
        {
            int id = hitTransform.GetInstanceID();
            if (!PointerTabletTransformCache.TryGetValue(id, out bool isTablet))
            {
                isTablet = hitTransform.name.IndexOf("FieldTablet", StringComparison.OrdinalIgnoreCase) >= 0;
                PointerTabletTransformCache[id] = isTablet;
            }

            return isTablet;
        }

        /// <summary>
        /// F-DRONE-9: insertion sort over the fixed hit buffer. RaycastNonAlloc does not sort, and
        /// Array.Sort with a lambda allocated a ComparisonComparer on every frame the pointer ran.
        /// </summary>
        private static void SortPointerHitsByDistance(int count)
        {
            for (int i = 1; i < count; i++)
            {
                RaycastHit current = PointerRaycastHits[i];
                int j = i - 1;
                while (j >= 0 && PointerRaycastHits[j].distance > current.distance)
                {
                    PointerRaycastHits[j + 1] = PointerRaycastHits[j];
                    j--;
                }

                PointerRaycastHits[j + 1] = current;
            }
        }

        private static int GetPointerRaycastMask()
        {
            // A destroyed StartOfRound reads as null, as the uncached check did.
            StartOfRound round = StartOfRound.Instance != null ? StartOfRound.Instance : null;
            if (_pointerRaycastMaskValid && ReferenceEquals(round, _pointerRaycastMaskRound))
                return _pointerRaycastMask;

            int mask = round != null
                ? round.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            mask |= 1 << 6;
            mask |= 1 << 9;
            mask |= 1 << 19;
            mask |= 1 << 23;
            _pointerRaycastMask = mask;
            _pointerRaycastMaskRound = round;
            _pointerRaycastMaskValid = true;
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
                ReticleColor = PointerEnemyColor,
                Source = enemy
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
                ReticleColor = PointerItemColor,
                Source = item
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
    }
}
