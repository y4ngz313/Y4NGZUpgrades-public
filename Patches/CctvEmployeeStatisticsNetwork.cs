using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Delivers host-confirmed, actor-attributed CCTV events to the employee who earned them.
    /// The Employee File is a per-save local ledger, so no crew-wide statistic state is mirrored.
    /// </summary>
    [HarmonyPatch]
    internal static class CctvEmployeeStatisticsNetwork
    {
        private const string EventMessage = "Y4NGZEmployeeStatistics.CctvEvent.v2";
        private const string EpochMessage = "Y4NGZEmployeeStatistics.CctvEpoch.v1";
        private const string EpochRequestMessage = "Y4NGZEmployeeStatistics.CctvEpochRequest.v1";
        private const byte DeviceHackKind = 1;
        private const byte AlarmKind = 2;

        private static readonly HashSet<ulong> ReceivedEventIds = new HashSet<ulong>();
        private static readonly CctvStatisticsEpochGuard Epoch = new CctvStatisticsEpochGuard();
        private static NetworkManager _registeredNetworkManager;
        private static ulong _nextEventId;
        private static ulong _nextEpoch;
        private static bool _epochRequestPending;
        private static bool _hostEpochStartedForRound;

        internal static void EnsureRegistered()
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsListening)
                return;
            if (ReferenceEquals(_registeredNetworkManager, network))
                return;

            Unregister();
            try
            {
                messaging.RegisterNamedMessageHandler(EventMessage, OnEvent);
                messaging.RegisterNamedMessageHandler(EpochMessage, OnEpoch);
                if (network.IsServer)
                    messaging.RegisterNamedMessageHandler(EpochRequestMessage, OnEpochRequest);
                network.OnClientConnectedCallback += OnClientConnected;
                _registeredNetworkManager = network;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[EmployeeFile] CCTV event handler registration failed: " + ex.Message);
                Unregister();
            }
        }

        internal static void ResetRound()
        {
            ReceivedEventIds.Clear();
            _epochRequestPending = false;
            _hostEpochStartedForRound = false;
            Epoch.BeginRound(0);
            EnsureRegistered();
            EnsureEpochForActiveRound();
        }

        /// <summary>
        /// Seeds a peer from locally replicated, landed-map metadata before it asks the host for
        /// an epoch. This also covers a late joiner that never receives openingDoorsSequence.
        /// </summary>
        internal static void EnsureEpochForActiveRound()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsListening || !TryResolveLevelToken(out ulong levelToken))
                return;

            if (network.IsServer)
            {
                if (_hostEpochStartedForRound && Epoch.IsReady && Epoch.LevelToken == levelToken)
                    return;

                Epoch.BeginRound(levelToken);
                if (!Epoch.TryAcceptHostEpoch(NextEpoch(), levelToken))
                    return;

                _hostEpochStartedForRound = true;
                EnsureRegistered();
                SendEpochToAll(network);
                return;
            }

            if (Epoch.LevelToken != levelToken)
            {
                ReceivedEventIds.Clear();
                _epochRequestPending = false;
                Epoch.BeginRound(levelToken);
            }

            if (!Epoch.IsReady)
            {
                EnsureRegistered();
                RequestEpoch(network);
            }
        }

        internal static void ResetSession()
        {
            ReceivedEventIds.Clear();
            _nextEventId = 0;
            _nextEpoch = 0;
            _epochRequestPending = false;
            _hostEpochStartedForRound = false;
            Epoch.ResetSession();
            Unregister();
        }

        /// <summary>
        /// The host-issued round epoch this peer currently holds. Other host-to-actor deliveries
        /// (the ship battery award, #459) stamp and check the same round identity instead of
        /// running a second epoch handshake.
        /// </summary>
        internal static bool TryGetRoundEpoch(out ulong epoch, out ulong levelToken)
        {
            epoch = Epoch.ActiveEpoch;
            levelToken = Epoch.LevelToken;
            return Epoch.IsReady;
        }

        internal static bool IsCurrentRoundEpoch(ulong epoch, ulong levelToken)
        {
            return Epoch.IsCurrent(epoch, levelToken);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            // EmployeeStatistics.UpdateRuntime aligns late joiners only after the local player and
            // landed-map metadata are both ready, then calls EnsureEpochForActiveRound.
            EnsureRegistered();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            ResetSession();
        }

        internal static void RecordHostConfirmedDeviceHack(ulong actorClientId, string logicalDeviceKey)
        {
            Publish(actorClientId, DeviceHackKind, logicalDeviceKey);
        }

        internal static void RecordHostConfirmedAlarm(ulong actorClientId, string episodeKey)
        {
            Publish(actorClientId, AlarmKind, episodeKey);
        }

        private static void Publish(ulong actorClientId, byte kind, string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || !Epoch.IsReady)
                return;

            EnsureRegistered();
            ulong eventId = NextEventId();
            if (actorClientId == network.LocalClientId)
            {
                Apply(kind, eventId, key);
                return;
            }

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null || !network.ConnectedClients.ContainsKey(actorClientId))
                return;

            using (var writer = new FastBufferWriter(112, Allocator.Temp))
            {
                writer.WriteValueSafe(kind);
                writer.WriteValueSafe(Epoch.ActiveEpoch);
                writer.WriteValueSafe(Epoch.LevelToken);
                writer.WriteValueSafe(eventId);
                writer.WriteValueSafe(new FixedString64Bytes(key));
                messaging.SendNamedMessage(EventMessage, actorClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static ulong NextEventId()
        {
            _nextEventId++;
            if (_nextEventId == 0)
                _nextEventId = 1;
            return _nextEventId;
        }

        private static ulong NextEpoch()
        {
            _nextEpoch++;
            if (_nextEpoch == 0)
                _nextEpoch = 1;
            return _nextEpoch;
        }

        private static void OnEvent(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            try
            {
                reader.ReadValueSafe(out byte kind);
                reader.ReadValueSafe(out ulong epoch);
                reader.ReadValueSafe(out ulong levelToken);
                reader.ReadValueSafe(out ulong eventId);
                reader.ReadValueSafe(out FixedString64Bytes fixedKey);
                string key = fixedKey.ToString();
                if (network == null
                    || !CctvStatisticsEventGuard.IsTrustedDelivery(
                        senderClientId,
                        NetworkManager.ServerClientId,
                        network.LocalClientId,
                        network.LocalClientId,
                        eventId)
                    || !Epoch.IsCurrent(epoch, levelToken)
                    || (kind != DeviceHackKind && kind != AlarmKind)
                    || !ReceivedEventIds.Add(eventId))
                {
                    return;
                }

                Apply(kind, eventId, key);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[EmployeeFile] Invalid CCTV event: " + ex.Message);
            }
        }

        private static void Apply(byte kind, ulong eventId, string key)
        {
            if (kind == DeviceHackKind)
                EmployeeStatistics.RecordCctvDeviceHacked(key);
            else if (kind == AlarmKind)
                EmployeeStatistics.RecordCctvAlarmSetOff(eventId.ToString() + "." + (key ?? string.Empty));
        }
        private static void OnEpoch(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong epoch);
                reader.ReadValueSafe(out ulong levelToken);
                if (senderClientId != NetworkManager.ServerClientId
                    || !TryResolveLevelToken(out ulong localLevelToken)
                    || levelToken != localLevelToken)
                {
                    return;
                }

                if (Epoch.TryAcceptHostEpoch(epoch, levelToken))
                    _epochRequestPending = false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[EmployeeFile] Invalid CCTV epoch: " + ex.Message);
            }
        }

        private static void OnEpochRequest(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null
                || !network.IsServer
                || senderClientId == network.LocalClientId
                || !network.ConnectedClients.ContainsKey(senderClientId))
            {
                return;
            }

            SendEpoch(network, senderClientId);
        }

        private static void OnClientConnected(ulong clientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || clientId == network.LocalClientId)
                return;

            // The connection callback runs before the remote player's named-message registration.
            // Its own request is authoritative; this delayed sync reduces the no-stat window when
            // it has already registered without depending on PlayerObject spawn ordering.
            Y4NGZPersistentRunner.Run(SendEpochAfterJoin(network, clientId));
        }

        private static IEnumerator SendEpochAfterJoin(NetworkManager expectedNetwork, ulong clientId)
        {
            yield return new WaitForSecondsRealtime(0.25f);
            if (ReferenceEquals(NetworkManager.Singleton, expectedNetwork)
                && expectedNetwork.IsServer
                && expectedNetwork.ConnectedClients.ContainsKey(clientId))
            {
                SendEpoch(expectedNetwork, clientId);
            }
        }

        private static void RequestEpoch(NetworkManager network)
        {
            if (_epochRequestPending
                || network == null
                || network.IsServer
                || network.CustomMessagingManager == null
                || !network.IsListening)
            {
                return;
            }

            try
            {
                using (var writer = new FastBufferWriter(1, Allocator.Temp))
                {
                    network.CustomMessagingManager.SendNamedMessage(
                        EpochRequestMessage,
                        NetworkManager.ServerClientId,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                _epochRequestPending = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[EmployeeFile] CCTV epoch request failed: " + ex.Message);
            }
        }

        private static void SendEpochToAll(NetworkManager network)
        {
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (messaging == null || !network.IsServer || !Epoch.IsReady)
                return;

            using (var writer = new FastBufferWriter(24, Allocator.Temp))
            {
                writer.WriteValueSafe(Epoch.ActiveEpoch);
                writer.WriteValueSafe(Epoch.LevelToken);
                messaging.SendNamedMessageToAll(EpochMessage, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void SendEpoch(NetworkManager network, ulong recipientClientId)
        {
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (messaging == null
                || !network.IsServer
                || !Epoch.IsReady
                || !network.ConnectedClients.ContainsKey(recipientClientId))
            {
                return;
            }

            using (var writer = new FastBufferWriter(24, Allocator.Temp))
            {
                writer.WriteValueSafe(Epoch.ActiveEpoch);
                writer.WriteValueSafe(Epoch.LevelToken);
                messaging.SendNamedMessage(EpochMessage, recipientClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static bool TryResolveLevelToken(out ulong levelToken)
        {
            levelToken = 0;
            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.currentLevel == null)
                return false;

            unchecked
            {
                levelToken = ((ulong)(uint)round.randomMapSeed << 32) | (uint)round.currentLevelID;
                if (levelToken == 0)
                    levelToken = 1;
                return true;
            }
        }

        private static void Unregister()
        {
            NetworkManager network = _registeredNetworkManager;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (messaging != null)
            {
                try
                {
                    messaging.UnregisterNamedMessageHandler(EventMessage);
                    messaging.UnregisterNamedMessageHandler(EpochMessage);
                    if (network.IsServer)
                        messaging.UnregisterNamedMessageHandler(EpochRequestMessage);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogDebug("[EmployeeFile] CCTV event handler unregister failed: " + ex.Message);
                }
            }
            if (network != null)
                network.OnClientConnectedCallback -= OnClientConnected;
            _registeredNetworkManager = null;
        }
    }
}
