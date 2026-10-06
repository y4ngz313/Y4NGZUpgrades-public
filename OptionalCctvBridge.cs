using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades
{
    /// <summary>Reflection-only access to LethalCCTV and Contracted tactical marker APIs.</summary>
    internal static class OptionalCctvBridge
    {
        private const string CctvApiTypeName =
            "Y4NGZCompany.Facility.Security.CctvSupportApi, LethalCCTV";
        private const string MainframeTypeName =
            "Y4NGZCompany.Facility.Mainframe.MainframeSupport, LethalCCTV";
        private const string CameraRegistryTypeName =
            "Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry, LethalCCTV";
        private const string ObjectiveApiTypeName =
            "Y4NGZCompany.Contracts._Shared.MoonContractObjectiveMarkerApi, Y4NGZCompany";

        // F-TABLET-11: the MAINFRAME tab reads these every row rebuild (~10 Hz x N cameras), and
        // Type.GetType + GetMethod/GetProperty are not free. Resolutions are cached; a *failed*
        // type lookup is deliberately not cached, because the CCTV plugin's assembly may not be
        // loaded yet the first time a read happens. Member lookups are cached either way: once the
        // declaring type exists its member set cannot change.
        private static Type _cctvApiType;
        private static Type _mainframeType;
        private static Type _cameraRegistryType;
        private static Type _objectiveApiType;
        private static readonly Dictionary<string, MethodInfo> MethodCache =
            new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, MemberInfo> StaticMemberCache =
            new Dictionary<string, MemberInfo>();
        private static readonly Dictionary<string, MemberInfo> InstanceMemberCache =
            new Dictionary<string, MemberInfo>();

        private static Type CctvApi => ResolveCctvType(CctvApiTypeName, ref _cctvApiType);
        private static Type MainframeType => ResolveCctvType(MainframeTypeName, ref _mainframeType);
        private static Type CameraRegistryType => ResolveCctvType(CameraRegistryTypeName, ref _cameraRegistryType);

        private static Type ResolveCctvType(string assemblyQualifiedName, ref Type cache)
        {
            if (cache != null)
                return cache;
            if (!OptionalPluginCapabilities.LethalCctv)
                return null;
            cache = Type.GetType(assemblyQualifiedName, throwOnError: false);
            return cache;
        }

        internal static bool InstallDetectionTimeMultiplierProvider(Func<PlayerControllerB, float> provider)
        {
            Type type = CctvApi;
            if (type == null)
                return false;

            try
            {
                FieldInfo field = type.GetField(
                    "DetectionTimeMultiplierProvider",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType.IsInstanceOfType(provider))
                {
                    field.SetValue(null, provider);
                    return true;
                }

                PropertyInfo property = type.GetProperty(
                    "DetectionTimeMultiplierProvider",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanWrite == true && property.PropertyType.IsInstanceOfType(provider))
                {
                    property.SetValue(null, provider);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        internal static bool TryDisableCamera(int cameraId) =>
            InvokeStaticBool(CctvApi, "TryDisableCamera", new object[] { cameraId });

        internal static bool IsCameraDisabled(int cameraId) =>
            InvokeStaticBool(CctvApi, "IsCameraDisabled", new object[] { cameraId });

        internal static bool TrySetLockdown(bool begin) =>
            InvokeStaticBool(CctvApi, begin ? "TryBeginLockdown" : "TryEndLockdown", null);

        internal static bool IsAlarmActive => ReadStaticBool(CctvApi, "IsAlarmActive");
        internal static bool IsTimedSecurityAlarmActive => ReadStaticBool(CctvApi, "IsTimedSecurityAlarmActive");
        internal static bool IsLockdownActive => ReadStaticBool(CctvApi, "IsLockdownActive");
        internal static bool IsMainframeHacked => ReadStaticBool(CctvApi, "IsMainframeHacked");

        internal static IReadOnlyList<int> GetStashCodes()
        {
            return ReadIntList(ReadStaticMember(CctvApi, "MainframeControlStashCodes"));
        }

        internal static IReadOnlyList<int> GetCameraIds()
        {
            return ReadIntList(InvokeStatic(CctvApi, "GetCameraIds", null));
        }

        internal static string FormatStashCode(int code)
        {
            return InvokeStatic(CctvApi, "FormatStashCode", new object[] { code }) as string
                   ?? code.ToString("0000");
        }

        internal static string GetCameraLabel(int cameraId)
        {
            return InvokeStatic(CctvApi, "GetCameraLabel", new object[] { cameraId }) as string;
        }

        internal static bool IsSecurityAlarmActive()
        {
            object support = ReadStaticMember(MainframeType, "Active");
            return ReadInstanceBool(support, "IsSecurityAlarmActive") || IsTimedSecurityAlarmActive;
        }

        /// <summary>
        /// Review pass: reports whether the alarm operation actually reached the mainframe. This
        /// used to return void and bail silently when <c>MainframeSupport.Active</c> was null, and
        /// <see cref="InvokeInstance"/> swallows every exception - so the caller's try/catch could
        /// never fire and the tablet always reported ALARM SET, eating its cooldown, even with no
        /// mainframe on the moon. Both entry points are void members, so a null return value
        /// carries no information; the signal is whether the support instance exists, the method
        /// resolved, and the call completed.
        /// </summary>
        internal static bool ApplyAlarmOperation(byte operation, byte silenceOperation)
        {
            object support = ReadStaticMember(MainframeType, "Active");
            if (support == null)
                return false;

            if (operation == silenceOperation)
            {
                return TryInvokeInstanceVoid(
                    support, "SilenceSecurityAlarmServerRpc", new object[] { default(ServerRpcParams) });
            }

            return TryInvokeInstanceVoid(
                support,
                "SetAlarmServerRpc",
                new object[] { operation != 0, default(ServerRpcParams) });
        }

        /// <summary>
        /// F-TABLET-2: maps a CCTVCamera behaviour picked up by the SCAN-tab splice raycast back to
        /// the CameraIndex that CctvSupportApi.TryDisableCamera / IsCameraDisabled take, so the
        /// splice can route through the same host path the MAINFRAME CAMERAS list uses. Prefers the
        /// camera's own CameraIndex and falls back to the security registry entry.
        /// </summary>
        internal static bool TryResolveCameraId(Component camera, out int cameraId)
        {
            cameraId = 0;
            if (camera == null || !OptionalPluginCapabilities.LethalCctv)
                return false;

            if (ReadInstanceMember(camera, "CameraIndex") is int index)
            {
                cameraId = index;
                return true;
            }

            object state = InvokeStatic(CameraRegistryType, "Find", new object[] { camera });
            if (state != null && ReadInstanceMember(state, "CameraIndex") is int registered)
            {
                cameraId = registered;
                return true;
            }

            return false;
        }

        internal static void CollectCameraTransforms(List<Transform> output)
        {
            if (output == null || !OptionalPluginCapabilities.LethalCctv)
                return;

            object cameras = ReadStaticMember(CameraRegistryType, "RegisteredCameras");
            if (!(cameras is IEnumerable enumerable))
                return;

            foreach (object camera in enumerable)
            {
                object value = ReadInstanceMember(camera, "Transform");
                if (value is Transform transform && transform != null)
                    output.Add(transform);
            }
        }

        internal static void CollectActiveObjectivePositions(List<Vector3> output)
        {
            if (output == null || !OptionalPluginCapabilities.Contracted)
                return;

            if (_objectiveApiType == null)
                _objectiveApiType = Type.GetType(ObjectiveApiTypeName, throwOnError: false);
            object markers = InvokeStatic(_objectiveApiType, "GetActiveMarkers", null);
            if (!(markers is IEnumerable enumerable))
                return;

            foreach (object marker in enumerable)
            {
                if (marker == null || ReadInstanceBool(marker, "IsComplete"))
                    continue;
                object position = ReadInstanceMember(marker, "Position");
                if (position is Vector3 vector)
                    output.Add(vector);
            }
        }

        private static IReadOnlyList<int> ReadIntList(object value)
        {
            var result = new List<int>();
            if (!(value is IEnumerable enumerable))
                return result;
            foreach (object entry in enumerable)
            {
                if (entry is int integer)
                    result.Add(integer);
            }
            return result;
        }

        private static bool InvokeStaticBool(Type type, string name, object[] arguments)
        {
            return InvokeStatic(type, name, arguments) is bool value && value;
        }

        private static bool ReadStaticBool(Type type, string name)
        {
            return ReadStaticMember(type, name) is bool value && value;
        }

        private static bool ReadInstanceBool(object instance, string name)
        {
            return ReadInstanceMember(instance, name) is bool value && value;
        }

        private static MethodInfo ResolveMethod(Type type, string name, BindingFlags flags)
        {
            if (type == null)
                return null;
            string key = type.FullName + "|" + name + "|" + (int)flags;
            if (MethodCache.TryGetValue(key, out MethodInfo cached))
                return cached;

            MethodInfo method = null;
            try
            {
                method = type.GetMethod(name, flags);
            }
            catch
            {
            }
            MethodCache[key] = method;
            return method;
        }

        private static MemberInfo ResolveMember(Type type, string name, BindingFlags flags,
            Dictionary<string, MemberInfo> cache)
        {
            if (type == null)
                return null;
            string key = type.FullName + "|" + name;
            if (cache.TryGetValue(key, out MemberInfo cached))
                return cached;

            MemberInfo member = null;
            try
            {
                member = (MemberInfo)type.GetProperty(name, flags) ?? type.GetField(name, flags);
            }
            catch
            {
            }
            cache[key] = member;
            return member;
        }

        private static object ReadMember(MemberInfo member, object instance)
        {
            try
            {
                if (member is PropertyInfo property)
                    return property.GetValue(instance);
                if (member is FieldInfo field)
                    return field.GetValue(instance);
            }
            catch
            {
            }
            return null;
        }

        private static object InvokeStatic(Type type, string name, object[] arguments)
        {
            MethodInfo method = ResolveMethod(
                type, name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return null;
            try
            {
                return method.Invoke(null, arguments);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Review pass: the <see cref="InvokeInstance"/> shape for a void member, where a null
        /// return value means nothing. True only when the method resolved and the call completed.
        /// </summary>
        private static bool TryInvokeInstanceVoid(object instance, string name, object[] arguments)
        {
            if (instance == null)
                return false;
            MethodInfo method = ResolveMethod(
                instance.GetType(), name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return false;
            try
            {
                method.Invoke(instance, arguments);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object InvokeInstance(object instance, string name, object[] arguments)
        {
            if (instance == null)
                return null;
            MethodInfo method = ResolveMethod(
                instance.GetType(), name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return null;
            try
            {
                return method.Invoke(instance, arguments);
            }
            catch
            {
                return null;
            }
        }

        private static object ReadStaticMember(Type type, string name)
        {
            return ReadMember(
                ResolveMember(
                    type, name,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    StaticMemberCache),
                null);
        }

        private static object ReadInstanceMember(object instance, string name)
        {
            if (instance == null)
                return null;
            return ReadMember(
                ResolveMember(
                    instance.GetType(), name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    InstanceMemberCache),
                instance);
        }
    }
}
