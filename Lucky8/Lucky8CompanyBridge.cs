using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Y4NGZUpgrades.Lucky8
{
    internal static class Lucky8CompanyBridge
    {
        private const string CompanyAssemblyName = "Y4NGZCompany";
        private static Type routeApiType;
        private static Type placementApiType;
        private static MethodInfo getRouteCatalog;
        private static MethodInfo registerMarkerProvider;
        private static MethodInfo unregisterMarkerProvider;
        private static MethodInfo requestPlacement;
        private static bool resolved;

        internal static bool Initialize()
        {
            Resolve();
            if (registerMarkerProvider == null)
                return false;

            try
            {
                return Convert.ToBoolean(registerMarkerProvider.Invoke(null, new object[]
                {
                    "lucky8",
                    new Func<int, bool>(Lucky8Manager.IsRouteAssigned),
                }));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Company route marker registration failed: " + Unwrap(e));
                return false;
            }
        }

        internal static void Shutdown()
        {
            try { unregisterMarkerProvider?.Invoke(null, new object[] { "lucky8" }); }
            catch { }
        }

        internal static IReadOnlyList<Lucky8RouteInfo> GetRoutes()
        {
            Resolve();
            var routes = new List<Lucky8RouteInfo>();
            if (getRouteCatalog == null)
                return routes;

            try
            {
                if (!(getRouteCatalog.Invoke(null, null) is IEnumerable catalog))
                    return routes;

                foreach (object entry in catalog)
                {
                    if (entry == null) continue;
                    Type type = entry.GetType();
                    routes.Add(new Lucky8RouteInfo
                    {
                        RouteIndex = Read<int>(type, entry, "RouteIndex"),
                        LevelId = Read<int>(type, entry, "LevelId"),
                        MoonName = Read<string>(type, entry, "MoonName") ?? string.Empty,
                        ConstellationKey = Read<string>(type, entry, "ConstellationKey") ?? string.Empty,
                        IsCompanyMoon = Read<bool>(type, entry, "IsCompanyMoon"),
                        HasFacility = Read<bool>(type, entry, "HasFacility"),
                    });
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Company route catalog read failed: " + Unwrap(e));
            }
            return routes;
        }

        internal static bool RequestPlacement(Action<bool, Vector3, Quaternion, Vector3, string> callback)
        {
            Resolve();
            if (requestPlacement == null || callback == null)
                return false;

            try
            {
                return Convert.ToBoolean(requestPlacement.Invoke(null, new object[]
                {
                    Plugin.Guid,
                    1.40f,
                    2.42f,
                    0.76f,
                    1.45f,
                    Mathf.Max(8f, Lucky8Manager.Config.MinEntranceDistance.Value),
                    callback,
                }));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Company placement request failed: " + Unwrap(e));
                return false;
            }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            Assembly company = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, CompanyAssemblyName, StringComparison.Ordinal))
                {
                    company = assembly;
                    break;
                }
            }
            if (company == null) return;

            routeApiType = company.GetType("Y4NGZCompany.Core.RouteFeatureApi", false);
            placementApiType = company.GetType("Y4NGZCompany.Core.FacilityFeaturePlacementApi", false);
            getRouteCatalog = routeApiType?.GetMethod("GetRouteCatalog", BindingFlags.Public | BindingFlags.Static);
            registerMarkerProvider = routeApiType?.GetMethod("RegisterMarkerProvider", BindingFlags.Public | BindingFlags.Static);
            unregisterMarkerProvider = routeApiType?.GetMethod("UnregisterMarkerProvider", BindingFlags.Public | BindingFlags.Static);
            requestPlacement = placementApiType?.GetMethod("RequestLargeBackWallPlacement", BindingFlags.Public | BindingFlags.Static);
        }

        private static T Read<T>(Type type, object instance, string name)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            object value = property?.GetValue(instance);
            return value is T typed ? typed : default;
        }

        private static string Unwrap(Exception error)
        {
            return (error as TargetInvocationException)?.InnerException?.Message ?? error.Message;
        }
    }
}
