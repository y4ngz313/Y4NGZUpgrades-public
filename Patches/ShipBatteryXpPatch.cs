using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Ship Systems battery-restore XP (#459), host side. Ship Systems restores power on the host
    /// only (the local-host request and the client restore message both end in
    /// <c>ShipSystemsController.RestoreBatteryPower(bool usedApparatus, PlayerControllerB restoredBy)</c>)
    /// and replicates no actor, so the host decides and <see cref="ShipBatteryXpNetwork"/> pays.
    ///
    /// Three hooks, all-or-nothing: without the break hooks the anti-farm rule would be blind, so
    /// the restore hook declines unless every target resolves.
    /// - <c>ApplyPlayerBatteryHit</c> marks "a player's hit is being applied" for the duration of
    ///   the call; <c>ExternalBatteryStation.ApplyDamage</c> breaks the battery synchronously from
    ///   inside it.
    /// - <c>BreakBattery</c> latches the breaker: that player, or nobody for an enemy's sabotage
    ///   or an explosion.
    /// - <c>RestoreBatteryPower</c> captures whether power was down before the call (it restores
    ///   unconditionally, even with power on) and credits the restorer through the policy.
    ///
    /// Without Ship Systems every <c>Prepare</c> declines: no hook, no message handler, no effect.
    /// </summary>
    internal static class ShipBatteryXpPatch
    {
        private const string ControllerTypeName = "Y4NGZCompany.ShipSystems.Layout.ShipSystemsController";
        private const string ShipSystemsAssemblyName = "Y4NGZShipSystems";

        private static readonly ShipBatteryXpPolicy Policy = new ShipBatteryXpPolicy();

        private static bool _resolved;
        private static bool _available;
        private static MethodBase _restoreMethod;
        private static MethodBase _breakMethod;
        private static MethodBase _playerHitMethod;
        private static FieldInfo _batteryBrokenField;
        private static FieldInfo _shipPowerOnlineField;

        // Set only while ApplyPlayerBatteryHit runs on the host.
        private static bool _playerHitActive;
        private static ulong _playerHitClientId;

        // The controller whose break is latched, so a round reset can tell an unresolved break
        // (still that player's) from a finished one.
        private static UnityEngine.Object _latchedController;

        internal static void OnRoundStarted()
        {
            bool stillBroken = _latchedController != null && ReadBool(_batteryBrokenField, _latchedController);
            Policy.ResetRound(stillBroken);
            if (!stillBroken)
                _latchedController = null;
        }

        internal static void ResetSession()
        {
            Policy.ResetSession();
            _latchedController = null;
            _playerHitActive = false;
        }

        private static bool Resolve()
        {
            if (_resolved)
                return _available;

            _resolved = true;
            if (!OptionalPluginCapabilities.ShipSystems)
                return false;

            Type controller = FindControllerType();
            _restoreMethod = AccessTools.Method(controller, "RestoreBatteryPower", new[] { typeof(bool), typeof(PlayerControllerB) });
            _breakMethod = AccessTools.Method(controller, "BreakBattery", new[] { typeof(string) });
            _playerHitMethod = AccessTools.Method(
                controller,
                "ApplyPlayerBatteryHit",
                new[] { typeof(int), typeof(Vector3), typeof(PlayerControllerB), typeof(bool) });
            _batteryBrokenField = AccessTools.Field(controller, "_batteryBroken");
            _shipPowerOnlineField = AccessTools.Field(controller, "_shipPowerOnline");

            _available = _restoreMethod != null
                         && _breakMethod != null
                         && _playerHitMethod != null
                         && _batteryBrokenField?.FieldType == typeof(bool)
                         && _shipPowerOnlineField?.FieldType == typeof(bool);
            if (!_available)
            {
                Plugin.Log?.LogWarning(
                    "[Progression] Ship Systems is installed but its battery restore/break members were not found; "
                    + "ship battery XP is inactive.");
            }

            return _available;
        }

        private static Type FindControllerType()
        {
            Type resolved = Type.GetType(ControllerTypeName + ", " + ShipSystemsAssemblyName, throwOnError: false);
            if (resolved != null)
                return resolved;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.GetName().Name != ShipSystemsAssemblyName)
                    continue;

                try
                {
                    resolved = assembly.GetType(ControllerTypeName, throwOnError: false);
                }
                catch
                {
                    resolved = null;
                }

                if (resolved != null)
                    return resolved;
            }

            return null;
        }

        private static bool IsServer()
        {
            return NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        }

        private static bool ReadBool(FieldInfo field, object instance)
        {
            try
            {
                return field != null && instance != null && field.GetValue(instance) is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetConnectedActor(PlayerControllerB player, out ulong clientId)
        {
            clientId = 0;
            NetworkManager network = NetworkManager.Singleton;
            if (player == null || network == null || !player.isPlayerControlled)
                return false;

            clientId = player.actualClientId;
            return network.ConnectedClients.ContainsKey(clientId);
        }

        [HarmonyPatch]
        internal static class PlayerBatteryHitPatch
        {
            private static bool Prepare()
            {
                return Resolve();
            }

            private static MethodBase TargetMethod()
            {
                return _playerHitMethod;
            }

            [HarmonyPrefix]
            private static void Prefix(PlayerControllerB __2)
            {
                _playerHitActive = __2 != null && IsServer();
                _playerHitClientId = __2 != null ? __2.actualClientId : 0;
            }

            [HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
            {
                _playerHitActive = false;
                return __exception;
            }
        }

        [HarmonyPatch]
        internal static class BreakBatteryPatch
        {
            private static bool Prepare()
            {
                return Resolve();
            }

            private static MethodBase TargetMethod()
            {
                return _breakMethod;
            }

            // Prefix, because BreakBattery returns early once the battery is already broken; only
            // the call that actually breaks it may move the latch.
            [HarmonyPrefix]
            private static void Prefix(object __instance)
            {
                if (!IsServer() || ReadBool(_batteryBrokenField, __instance))
                    return;

                if (_playerHitActive)
                    Policy.RecordPlayerBreak(_playerHitClientId);
                else
                    Policy.RecordNonPlayerBreak();
                _latchedController = __instance as UnityEngine.Object;
            }
        }

        [HarmonyPatch]
        internal static class RestoreBatteryPowerPatch
        {
            private static bool Prepare()
            {
                return Resolve();
            }

            private static MethodBase TargetMethod()
            {
                return _restoreMethod;
            }

            [HarmonyPrefix]
            private static void Prefix(object __instance, out bool __state)
            {
                __state = IsServer()
                          && (ReadBool(_batteryBrokenField, __instance) || !ReadBool(_shipPowerOnlineField, __instance));
            }

            [HarmonyPostfix]
            private static void Postfix(bool __0, PlayerControllerB __1, bool __state)
            {
                if (!IsServer())
                    return;

                bool hasActor = TryGetConnectedActor(__1, out ulong actorClientId);
                // Always evaluated: every restore closes the break episode, paid or not.
                bool credited = Policy.TryCreditRestore(__state, actorClientId);
                _latchedController = null;
                if (!credited || !hasActor)
                    return;

                try
                {
                    ShipBatteryXpNetwork.Publish(actorClientId, usedApparatus: __0);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("[Progression] Ship battery XP publish failed: " + ex.Message);
                }
            }
        }
    }
}
