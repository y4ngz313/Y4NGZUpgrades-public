using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Delivers a host-credited ship battery restore (#459) to the player who did it. Ship
    /// Systems runs the restore on the host only and replicates no actor, so the host decides
    /// eligibility and this message is the only way the actor's machine learns it earned XP.
    ///
    /// A host actor is paid in place. A client actor gets one targeted named message stamped with
    /// the shared host-issued round epoch and a session-unique event id: a message from any other
    /// round is stale and dropped, and a repeated event id is a duplicate and dropped.
    /// </summary>
    internal static class ShipBatteryXpNetwork
    {
        private const string RestoreMessage = "Y4NGZProgression.ShipBatteryRestore.v1";

        private static readonly HashSet<ulong> ReceivedEventIds = new HashSet<ulong>();
        private static ulong _receivedEpoch;
        private static ulong _receivedLevelToken;
        private static ulong _nextEventId;
        private static NetworkManager _registeredNetworkManager;

        internal static void EnsureRegistered()
        {
            if (!OptionalPluginCapabilities.ShipSystems)
                return;

            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsListening)
                return;
            if (ReferenceEquals(_registeredNetworkManager, network))
                return;

            Unregister();
            try
            {
                messaging.RegisterNamedMessageHandler(RestoreMessage, OnRestore);
                _registeredNetworkManager = network;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Progression] Ship battery XP handler registration failed: " + ex.Message);
                Unregister();
            }
        }

        // Nested so the outer class carries no [HarmonyPatch]: the message handler below is not
        // a patch method and must not be analysed as one.
        [HarmonyPatch]
        internal static class SessionHooks
        {
            [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
            [HarmonyPostfix]
            private static void PostConnectClientToPlayerObject()
            {
                EnsureRegistered();
            }

            [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
            [HarmonyPostfix]
            private static void PostDisconnect()
            {
                ReceivedEventIds.Clear();
                _receivedEpoch = 0;
                _receivedLevelToken = 0;
                _nextEventId = 0;
                Unregister();
                ShipBatteryXpPatch.ResetSession();
            }
        }

        /// <summary>Host only: pays <paramref name="actorClientId"/> for one credited restore.</summary>
        internal static void Publish(ulong actorClientId, bool usedApparatus)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return;

            ulong eventId = NextEventId();
            if (actorClientId == network.LocalClientId)
            {
                ProgressionManager.RecordShipBatteryRestore(usedApparatus, "ship.battery.host." + eventId);
                return;
            }

            EnsureRegistered();
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null || !network.ConnectedClients.ContainsKey(actorClientId))
                return;

            if (!CctvEmployeeStatisticsNetwork.TryGetRoundEpoch(out ulong epoch, out ulong levelToken))
            {
                Plugin.Log?.LogWarning($"[Progression] Ship battery XP for client {actorClientId} dropped: no round epoch yet.");
                return;
            }

            using (var writer = new FastBufferWriter(32, Allocator.Temp))
            {
                writer.WriteValueSafe(epoch);
                writer.WriteValueSafe(levelToken);
                writer.WriteValueSafe(eventId);
                writer.WriteValueSafe(usedApparatus);
                messaging.SendNamedMessage(RestoreMessage, actorClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnRestore(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong epoch);
                reader.ReadValueSafe(out ulong levelToken);
                reader.ReadValueSafe(out ulong eventId);
                reader.ReadValueSafe(out bool usedApparatus);
                if (senderClientId != NetworkManager.ServerClientId
                    || eventId == 0
                    || !CctvEmployeeStatisticsNetwork.IsCurrentRoundEpoch(epoch, levelToken))
                {
                    return;
                }

                if (epoch != _receivedEpoch || levelToken != _receivedLevelToken)
                {
                    ReceivedEventIds.Clear();
                    _receivedEpoch = epoch;
                    _receivedLevelToken = levelToken;
                }

                if (!ReceivedEventIds.Add(eventId))
                    return;

                ProgressionManager.RecordShipBatteryRestore(
                    usedApparatus,
                    "ship.battery." + epoch + "." + eventId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Progression] Invalid ship battery XP message: " + ex.Message);
            }
        }

        private static ulong NextEventId()
        {
            _nextEventId++;
            if (_nextEventId == 0)
                _nextEventId = 1;
            return _nextEventId;
        }

        private static void Unregister()
        {
            CustomMessagingManager messaging = _registeredNetworkManager?.CustomMessagingManager;
            if (messaging != null)
            {
                try
                {
                    messaging.UnregisterNamedMessageHandler(RestoreMessage);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogDebug("[Progression] Ship battery XP handler unregister failed: " + ex.Message);
                }
            }
            _registeredNetworkManager = null;
        }
    }
}
