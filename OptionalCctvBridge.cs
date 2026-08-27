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

        private static Type CctvApi => OptionalPluginCapabilities.LethalCctv
            ? Type.GetType(CctvApiTypeName, throwOnError: false)
            : null;
        private static Type MainframeType => OptionalPluginCapabilities.LethalCctv
            ? Type.GetType(MainframeTypeName, throwOnError: false)
            : null;

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

        internal static void ApplyAlarmOperation(byte operation, byte silenceOperation)
        {
            object support = ReadStaticMember(MainframeType, "Active");
            if (support == null)
                return;

            if (operation == silenceOperation)
                InvokeInstance(support, "SilenceSecurityAlarmServerRpc", new object[] { default(ServerRpcParams) });
            else
                InvokeInstance(
                    support,
                    "SetAlarmServerRpc",
                    new object[] { operation != 0, default(ServerRpcParams) });
        }

        internal static void CollectCameraTransforms(List<Transform> output)
        {
            if (output == null || !OptionalPluginCapabilities.LethalCctv)
                return;

            Type registry = Type.GetType(CameraRegistryTypeName, throwOnError: false);
            object cameras = ReadStaticMember(registry, "RegisteredCameras");
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

            Type api = Type.GetType(ObjectiveApiTypeName, throwOnError: false);
            object markers = InvokeStatic(api, "GetActiveMarkers", null);
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

        private static object InvokeStatic(Type type, string name, object[] arguments)
        {
            try
            {
                return type?.GetMethod(
                    name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(null, arguments);
            }
            catch
            {
                return null;
            }
        }

        private static object InvokeInstance(object instance, string name, object[] arguments)
        {
            if (instance == null)
                return null;
            try
            {
                return instance.GetType().GetMethod(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(instance, arguments);
            }
            catch
            {
                return null;
            }
        }

        private static object ReadStaticMember(Type type, string name)
        {
            try
            {
                PropertyInfo property = type?.GetProperty(
                    name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                    return property.GetValue(null);
                return type?.GetField(
                    name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static object ReadInstanceMember(object instance, string name)
        {
            if (instance == null)
                return null;
            try
            {
                Type type = instance.GetType();
                PropertyInfo property = type.GetProperty(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                    return property.GetValue(instance);
                return type.GetField(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }
    }
}
