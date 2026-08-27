using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    internal static class ScavengerCompanyPatch
    {
        private const string ShipSystemsAssemblyName = "Y4NGZShipSystems";
        // Known simplification: the last holder gets credit even if another player nudges the item in.
        private static readonly ConditionalWeakTable<GrabbableObject, StrongBox<ulong>> LastHolders =
            new ConditionalWeakTable<GrabbableObject, StrongBox<ulong>>();

        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized)
                return;

            if (!OptionalPluginCapabilities.ShipSystems)
            {
                Plugin.Log?.LogInfo("[Scavenger] Y4NGZ Ship Systems not present; fuel-chute integration disabled.");
                return;
            }

            _initialized = true;
            try
            {
                InstallPatches();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Scavenger] Failed to initialize fuel-chute integration: {e.Message}");
            }
        }

        private static void InstallPatches()
        {
            Harmony harmony = new Harmony(Plugin.Guid);

            MethodInfo discardHeldObject = AccessTools.Method(
                typeof(PlayerControllerB),
                nameof(PlayerControllerB.DiscardHeldObject),
                new[] { typeof(bool), typeof(NetworkObject), typeof(Vector3), typeof(bool) });
            TryPatch(
                harmony,
                discardHeldObject,
                prefix: new HarmonyMethod(typeof(ScavengerCompanyPatch), nameof(DiscardHeldObjectPrefix)),
                postfix: null,
                "PlayerControllerB.DiscardHeldObject");

            MethodInfo placeGrabbableObject = AccessTools.Method(
                typeof(PlayerControllerB),
                nameof(PlayerControllerB.PlaceGrabbableObject),
                new[] { typeof(Transform), typeof(Vector3), typeof(bool), typeof(GrabbableObject) });
            TryPatch(
                harmony,
                placeGrabbableObject,
                prefix: null,
                postfix: new HarmonyMethod(typeof(ScavengerCompanyPatch), nameof(PlaceGrabbableObjectPostfix)),
                "PlayerControllerB.PlaceGrabbableObject");

            Type fuelChuteType = CompanyType("FuelChuteController");
            if (fuelChuteType == null)
            {
                Plugin.Log?.LogWarning("[Scavenger] Y4NGZShipSystems FuelChuteController type was not found; fuel bonus disabled.");
                return;
            }

            MethodInfo sinkFuelItemRoutine = AccessTools.Method(
                fuelChuteType,
                "SinkFuelItemRoutine",
                new[] { typeof(GrabbableObject), typeof(float) });
            TryPatch(
                harmony,
                sinkFuelItemRoutine,
                prefix: new HarmonyMethod(typeof(ScavengerCompanyPatch), nameof(SinkFuelItemRoutinePrefix)),
                postfix: null,
                "FuelChuteController.SinkFuelItemRoutine");
        }

        private static void DiscardHeldObjectPrefix(PlayerControllerB __instance)
        {
            try
            {
                if (__instance != null)
                    StampLastHolder(__instance.currentlyHeldObjectServer, __instance.actualClientId);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Scavenger] Failed to track discarded fuel-item holder: {e.Message}");
            }
        }

        private static void PlaceGrabbableObjectPostfix(PlayerControllerB __instance, GrabbableObject placeObject)
        {
            try
            {
                if (__instance != null)
                    StampLastHolder(placeObject, __instance.actualClientId);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Scavenger] Failed to track placed fuel-item holder: {e.Message}");
            }
        }

        private static void StampLastHolder(GrabbableObject grabbable, ulong clientId)
        {
            if (grabbable == null)
                return;

            LastHolders.GetOrCreateValue(grabbable).Value = clientId;
        }

        private static void SinkFuelItemRoutinePrefix(GrabbableObject grabbable, ref float fuelValue)
        {
            try
            {
                if (grabbable == null || !LastHolders.TryGetValue(grabbable, out StrongBox<ulong> holder))
                    return;

                ulong clientId = holder.Value;
                int tier = UpgradeTierSync.GetTier(clientId, ScavengerUpgrade.UPGRADE_ID);
                if (tier <= 0)
                    return;

                float originalFuelValue = fuelValue;
                fuelValue *= ScavengerUpgrade.GetFuelMultiplier(tier);
                Plugin.Log?.LogInfo(
                    $"[Scavenger] Fuel bonus for client {clientId}: {originalFuelValue:0.##} -> {fuelValue:0.##}.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Scavenger] Fuel bonus failed safely: {e.Message}");
            }
        }

        private static void TryPatch(
            Harmony harmony,
            MethodBase target,
            HarmonyMethod prefix,
            HarmonyMethod postfix,
            string targetName)
        {
            if (target == null)
            {
                Plugin.Log?.LogWarning($"[Scavenger] Patch target {targetName} was not found; continuing without it.");
                return;
            }

            try
            {
                harmony.Patch(target, prefix, postfix);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Scavenger] Failed to patch {targetName}; continuing without it: {e.Message}");
            }
        }

        private static Type CompanyType(string typeName)
        {
            Type resolved = Type.GetType(
                "Y4NGZCompany.ShipSystems.Fuel." + typeName + ", " + ShipSystemsAssemblyName,
                throwOnError: false);

            return resolved ?? FindShipSystemsTypeByName(typeName);
        }

        private static Type FindShipSystemsTypeByName(string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.GetName().Name != ShipSystemsAssemblyName)
                    continue;

                try
                {
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                            return type;
                    }
                }
                catch (ReflectionTypeLoadException e)
                {
                    foreach (Type type in e.Types)
                    {
                        if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                            return type;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"[Scavenger] Failed to inspect Y4NGZShipSystems types: {e.Message}");
                }
            }

            return null;
        }
    }
}
