using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    internal static partial class FieldTabletScreenRuntime
    {
        private static bool TryFindHackableTarget(PlayerControllerB player, out TabletHackTarget target)
        {
            target = default;
            Camera camera = player != null ? player.gameplayCamera : null;
            if (camera == null)
                camera = Camera.main;
            if (camera == null)
                return false;

            Ray ray = new Ray(camera.transform.position, camera.transform.forward);

            // F-TABLET-8: the trigger-inclusive sweep below walks past anything that does not
            // resolve to a target kind, and a wall collider resolves to nothing - so without an
            // occlusion pass first the mainframe, cameras, doors and the stash could all be
            // spliced through solid geometry at the full ScanRange. Take the opaque distance from
            // the room mask and accept only hits in front of it.
            float opaqueDistance = ScanRange;
            int roomMask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMask
                : Physics.DefaultRaycastLayers;
            if (Physics.Raycast(ray, out RaycastHit opaqueHit, ScanRange, roomMask, QueryTriggerInteraction.Ignore))
                opaqueDistance = opaqueHit.distance + ScanOcclusionSkinDepth;

            // #500: hits past opaqueDistance were always discarded, so the sweep stops there, into
            // a reused buffer. A full buffer may have dropped a nearer hit, so that rare case falls
            // back to the allocating sweep over the same range to keep nearest-first exact.
            float sweepRange = Mathf.Min(ScanRange, opaqueDistance);
            RaycastHit[] hits = TargetScanHits;
            int count = Physics.RaycastNonAlloc(ray, hits, sweepRange, ~0, QueryTriggerInteraction.Collide);
            if (count >= hits.Length)
            {
                hits = Physics.RaycastAll(ray, sweepRange, ~0, QueryTriggerInteraction.Collide);
                count = hits != null ? hits.Length : 0;
            }
            if (count <= 0)
                return false;

            Array.Sort(hits, 0, count, TargetScanHitComparer);
            for (int i = 0; i < count; i++)
            {
                if (hits[i].distance > opaqueDistance)
                    break;
                Collider collider = hits[i].collider;
                if (collider == null)
                    continue;
                Transform hitTransform = collider.transform;
                if (hitTransform != null && player != null && hitTransform.IsChildOf(player.transform))
                    continue;
                if (hitTransform != null && IsFieldTabletTransform(hitTransform))
                    continue;

                if (TryResolveTarget(collider, hits[i].distance, out target))
                    return true;
            }

            return false;
        }

        private static bool IsFieldTabletTransform(Transform transform)
        {
            int id = transform.GetInstanceID();
            if (!FieldTabletNamedTransforms.TryGetValue(id, out bool named))
            {
                named = transform.name.IndexOf("FieldTablet", StringComparison.OrdinalIgnoreCase) >= 0;
                FieldTabletNamedTransforms[id] = named;
            }
            return named;
        }

        private static bool TryResolveTarget(Collider collider, float distance, out TabletHackTarget target)
        {
            target = default;
            if (collider == null)
                return false;

            Component component = FindCompanyComponent(collider, MainframeTypeName, "MainframeSupport");
            if (component != null)
                return BuildTarget(TabletHackTargetKind.Mainframe, component, "MAINFRAME", distance, out target);

            component = FindCompanyComponent(collider, CompanyStashTypeName, "CompanyStashController");
            if (component != null)
                return BuildTarget(TabletHackTargetKind.CompanyStash, component, ResolveDisplayName(component, "COMPANY STASH"), distance, out target);

            // Camera targets need LethalCCTV: without it the host refuses every shutdown, so a
            // camera is not a target at all (and does not fall through to another kind).
            bool cctv = IsCompanyPresent();
            component = FindCompanyComponent(collider, CctvCameraTypeName, "CCTVCamera");
            if (component != null)
                return cctv && BuildTarget(TabletHackTargetKind.CctvCamera, component, ResolveDisplayName(component, "CCTV CAMERA"), distance, out target);

            // F-TABLET-12: Field Mechanic level 2 is what buys keyless doors. The turret branch was
            // already gated on CanHackTurrets (tier 3); the door branch was not gated at all, which
            // handed the level-2 ability to anyone with level 1 plus this tablet.
            DoorLock door = collider.GetComponentInParent<DoorLock>();
            if (door != null && door.isLocked && TurretHackerUpgrade.CanHackDoors())
                return BuildTarget(TabletHackTargetKind.LockedDoor, door, "LOCKED DOOR", distance, out target);

            Turret turret = collider.GetComponentInParent<Turret>();
            if (turret != null && TurretHackerUpgrade.CanHackTurrets())
                return BuildTarget(TabletHackTargetKind.Turret, turret, "TURRET", distance, out target);

            ScanNodeProperties scan = collider.GetComponentInParent<ScanNodeProperties>();
            if (scan != null)
            {
                string scanText = (scan.headerText ?? string.Empty) + " " + (scan.subText ?? string.Empty);
                if (Contains(scanText, "mainframe"))
                    return BuildTarget(TabletHackTargetKind.Mainframe, scan, "MAINFRAME", distance, out target);
                if (Contains(scanText, "camera") || Contains(scanText, "cctv"))
                    return cctv && BuildTarget(TabletHackTargetKind.CctvCamera, scan, "CCTV CAMERA", distance, out target);
                if ((Contains(scanText, "locked") || Contains(scanText, "door")) && TurretHackerUpgrade.CanHackDoors())
                    return BuildTarget(TabletHackTargetKind.LockedDoor, scan, "LOCKED DOOR", distance, out target);
            }

            return false;
        }

        private static Type ResolveTargetType(string fullName)
        {
            if (ResolvedTargetTypes.TryGetValue(fullName, out Type cached) && cached != null)
                return cached;

            // A miss is not cached permanently: the CCTV assembly may simply not be loaded yet.
            //
            // Review pass: but it must not be re-probed on every call either. ResolveType
            // allocates a fresh Assembly[] through AppDomain.CurrentDomain.GetAssemblies() and
            // then calls GetType on each one, and FindCompanyComponent runs three times per
            // collider hit inside RefreshHackableTargetScan's TargetScanInterval (0.10 s) raycast
            // loop. On a base install without LethalCCTV - where all three probes always miss, and
            // the SCAN tab is still live for turrets and doors - that was three whole-AppDomain
            // reflection sweeps per hit, ten times a second, for the entire tablet session.
            float now = Time.unscaledTime;
            if (TargetTypeRetryAt.TryGetValue(fullName, out float retryAt) && now < retryAt)
                return null;
            TargetTypeRetryAt[fullName] = now + TargetTypeRetrySeconds;

            Type resolved = ResolveType(fullName);
            if (resolved != null)
                ResolvedTargetTypes[fullName] = resolved;
            return resolved;
        }

        private static Component FindCompanyComponent(Collider collider, string fullName, string simpleName)
        {
            Type type = ResolveTargetType(fullName);
            if (type != null)
                return collider.GetComponentInParent(type);
            return FindComponentByTypeName(collider, simpleName);
        }

        private static bool BuildTarget(TabletHackTargetKind kind, Component component, string label, float distance, out TabletHackTarget target)
        {
            target = new TabletHackTarget
            {
                Kind = kind,
                Component = component,
                Root = component != null ? component.gameObject : null,
                Label = string.IsNullOrWhiteSpace(label) ? FormatTargetKind(kind) : label,
                Distance = distance
            };

            bool valid = target.IsValid;
            if (valid && !_lastTargetWasValid)
                _statusLine = "OBJECT DETECTED";
            _lastTargetWasValid = valid;
            return valid;
        }

        private static Component FindComponentByTypeName(Collider collider, params string[] fragments)
        {
            if (collider == null)
                return null;

            Transform cursor = collider.transform;
            for (int depth = 0; cursor != null && depth < 8; depth++, cursor = cursor.parent)
            {
                Component found = FindComponentOnTransform(cursor, fragments);
                if (found != null)
                    return found;
            }

            return FindComponentOnTransform(collider.transform.root, fragments);
        }

        private static Component FindComponentOnTransform(Transform transform, params string[] fragments)
        {
            if (transform == null)
                return null;

            Component[] components = transform.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                // F-TABLET-9: Transform is always element 0, and matching used to include
                // component.name - which is the GameObject's name, not the component's - so a
                // well-named GameObject made the Transform itself a "match".
                if (component == null || component is Transform)
                    continue;

                string text = component.GetType().FullName;
                bool all = true;
                for (int f = 0; f < fragments.Length; f++)
                {
                    if (!Contains(text, fragments[f]))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                    return component;
            }

            return null;
        }

        private static bool TryNotifyHackSuccess()
        {
            switch (_hackTarget.Kind)
            {
                case TabletHackTargetKind.Mainframe:
                    return TryInvokeInstanceMethod(_hackTarget.Component, "MarkHackedServerRpc");
                case TabletHackTargetKind.CctvCamera:
                    return TryDisableCctvCamera(_hackTarget.Component);
                case TabletHackTargetKind.LockedDoor:
                    return TryUnlockDoor(_hackTarget.Component as DoorLock);
                case TabletHackTargetKind.Turret:
                    return TurretHackerPatch.TryHackFromTablet(
                        _hackTarget.Component as Turret);
                case TabletHackTargetKind.CompanyStash:
                    return TryInvokeInstanceMethod(_hackTarget.Component, "UnlockFromRemoteHackServerRpc")
                           || TryInvokeInstanceMethod(_hackTarget.Component, "UnlockServerRpc")
                           || TryInvokeInstanceMethod(_hackTarget.Component, "MarkUnlockedServerRpc");
                default:
                    return false;
            }
        }

        /// <summary>
        /// F-TABLET-2: routes the SCAN-tab camera splice through the same host path the MAINFRAME
        /// CAMERAS list uses, so CctvCameraShutdownSync replicates the shutdown to the whole crew.
        /// It used to call MarkBroken/BreakCamera/DisableCamera - none of which exist on
        /// CCTVCamera any more - and then fall back to CctvSecurityDirector.OnCameraBroken, which
        /// is local security-director bookkeeping with no netcode: the camera kept running, kept
        /// detecting everyone, and still read as online in the MAINFRAME list.
        /// </summary>
        private static bool TryDisableCctvCamera(Component component)
        {
            if (component == null || !IsCompanyPresent())
                return false;

            if (!OptionalCctvBridge.TryResolveCameraId(component, out int cameraId))
            {
                Plugin.Log?.LogDebug("[Field Tablet] CCTV splice: no camera id on " + component.GetType().Name);
                return false;
            }

            // Marked before sending: on a host the request is handled, and answered, synchronously.
            _spliceCameraRequestPending = true;
            FieldTabletMainframeNet.RequestCameraDisable(cameraId);
            return true;
        }

        /// <summary>
        /// F-TABLET-1: DoorLock.UnlockDoorSyncWithServer() and DoorLock.UnlockDoor() both wrap
        /// their entire body in `if (isLocked)`, so clearing isLocked first made the call a no-op:
        /// no UnlockDoorServerRpc, no re-enabled InteractTrigger, no SFX - and the tablet still
        /// printed ACCESS GRANTED. DoorLock is vanilla, so this needs no reflection, and
        /// UnlockDoorServerRpc is RequireOwnership = false, so the client-initiated call replicates
        /// once it is actually reached.
        /// </summary>
        private static bool TryUnlockDoor(DoorLock door)
        {
            // F-TABLET-12: re-checked here as well as at target resolution, so any future path into
            // the unlock carries the Field Mechanic level-2 gate with it.
            if (door == null || !door.isLocked || !TurretHackerUpgrade.CanHackDoors())
                return false;

            try
            {
                door.UnlockDoorSyncWithServer();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] door unlock failed: " + ex.Message);
                return false;
            }

            return !door.isLocked;
        }

        private static bool TryInvokeInstanceMethod(Component component, string methodName, params object[] preferredArgs)
        {
            if (component == null || string.IsNullOrWhiteSpace(methodName))
                return false;

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo method = component.GetType().GetMethod(methodName, flags);
            if (method == null)
                return false;

            try
            {
                object[] args = BuildArguments(method.GetParameters(), preferredArgs);
                method.Invoke(component, args);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] invoke " + methodName + " failed: " + ex.Message);
                return false;
            }
        }

        private static object[] BuildArguments(ParameterInfo[] parameters, object[] preferredArgs)
        {
            if (parameters == null || parameters.Length == 0)
                return Array.Empty<object>();

            object[] args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (preferredArgs != null && i < preferredArgs.Length && preferredArgs[i] != null && type.IsInstanceOfType(preferredArgs[i]))
                {
                    args[i] = preferredArgs[i];
                    continue;
                }
                if (type == typeof(float) && preferredArgs != null && preferredArgs.Length > 0 && preferredArgs[0] is float f)
                {
                    args[i] = f;
                    continue;
                }
                if (parameters[i].HasDefaultValue
                    && parameters[i].DefaultValue != DBNull.Value
                    && parameters[i].DefaultValue != Missing.Value)
                {
                    object defaultValue = parameters[i].DefaultValue;
                    args[i] = defaultValue ?? (type.IsValueType ? Activator.CreateInstance(type) : null);
                    continue;
                }
                args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
            }
            return args;
        }

        private static Type ResolveType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            Type type = Type.GetType(fullName + ", Y4NGZCompany", throwOnError: false)
                        ?? Type.GetType(fullName + ", LethalCCTV", throwOnError: false)
                        ?? Type.GetType(fullName + ", LGUContractHUD", throwOnError: false);
            if (type != null)
                return type;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    type = assemblies[i].GetType(fullName, throwOnError: false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // Ignore dynamic assemblies that cannot enumerate types.
                }
            }

            return null;
        }
    }
}
