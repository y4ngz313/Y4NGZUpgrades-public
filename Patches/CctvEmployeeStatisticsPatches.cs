using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Optional CCTV seams which only become active when the LethalCCTV assembly is present.
    /// Camera detection supplies an authoritative tracked player. Physical sabotage does not,
    /// so it is intentionally excluded from the personal alarm statistic.
    /// </summary>
    [HarmonyPatch]
    internal static class CctvEmployeeStatisticsAlarmPatch
    {
        private static MethodBase _refreshAlarmMethod;
        private static MethodInfo _findCameraState;
        private static FieldInfo _trackedPlayerField;
        private static PropertyInfo _timedAlarmActiveProperty;

        private struct AlarmAttempt
        {
            internal bool Valid;
            internal ulong ActorClientId;
            internal string CameraKey;
        }

        private static bool Prepare()
        {
            if (!OptionalPluginCapabilities.LethalCctv)
                return false;

            Type directorType = Type.GetType(
                "Y4NGZCompany.Facility.Security.CctvSecurityDirector, LethalCCTV",
                throwOnError: false);
            Type registryType = Type.GetType(
                "Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry, LethalCCTV",
                throwOnError: false);
            Type stateType = Type.GetType(
                "Y4NGZCompany.Facility.Security.CctvSecurityCameraState, LethalCCTV",
                throwOnError: false);
            _refreshAlarmMethod = directorType?.GetMethod(
                "RefreshAlarmTimer",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Component), typeof(string) },
                modifiers: null);
            _findCameraState = registryType?.GetMethod(
                "Find",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(Component) },
                modifiers: null);
            _trackedPlayerField = stateType?.GetField(
                "TrackedPlayer",
                BindingFlags.Instance | BindingFlags.Public);
            _timedAlarmActiveProperty = directorType?.GetProperty(
                "IsTimedAlarmActive",
                BindingFlags.Static | BindingFlags.Public);
            return _refreshAlarmMethod != null
                   && _findCameraState != null
                   && _trackedPlayerField != null
                   && _timedAlarmActiveProperty != null;
        }

        private static MethodBase TargetMethod()
        {
            return _refreshAlarmMethod;
        }

        [HarmonyPrefix]
        private static void Prefix(Component __0, string __1, out AlarmAttempt __state)
        {
            __state = default;
            if (!IsServer()
                || !string.Equals(__1, "SECURITY CAMERA", StringComparison.Ordinal)
                || IsTimedAlarmActive())
            {
                return;
            }

            try
            {
                object cameraState = _findCameraState.Invoke(null, new object[] { __0 });
                PlayerControllerB actor = _trackedPlayerField.GetValue(cameraState) as PlayerControllerB;
                if (actor == null)
                    return;

                __state = new AlarmAttempt
                {
                    Valid = true,
                    ActorClientId = actor.actualClientId,
                    CameraKey = "camera." + NetworkObjectKey.For(__0)
                };
            }
            catch
            {
                // A provider update that changes this optional seam must not affect CCTV play.
            }
        }

        [HarmonyPostfix]
        private static void Postfix(AlarmAttempt __state)
        {
            if (__state.Valid && IsTimedAlarmActive())
                CctvEmployeeStatisticsNetwork.RecordHostConfirmedAlarm(
                    __state.ActorClientId,
                    __state.CameraKey);
        }

        private static bool IsServer()
        {
            return NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        }

        private static bool IsTimedAlarmActive()
        {
            try
            {
                return _timedAlarmActiveProperty != null
                       && _timedAlarmActiveProperty.GetValue(null) is bool active
                       && active;
            }
            catch
            {
                return false;
            }
        }
    }
}
