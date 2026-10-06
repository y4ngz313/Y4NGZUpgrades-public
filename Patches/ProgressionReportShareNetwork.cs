using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// #458: shares each player's performance-report XP lines with the crew, so every report
    /// shows each player's own top lines on that player's row. Presentation only: payouts stay
    /// local, and a receiver records the share into Company's report seams for the sender's slot.
    ///
    /// NGO named messages from a client reach only the server, so a client sends its own report
    /// to the host, and the host records it and relays the same bytes to every other client. The
    /// host's own report goes host to every client. A client accepts only what the server sends;
    /// the host accepts an original only for the slot its sender's own connection plays.
    ///
    /// Every share is stamped with the host-issued CCTV round epoch and level token (the identity
    /// #459's battery award also reuses). A share from any other round is stale, a second share
    /// for a slot in one round is a duplicate, and once the ship is made ready to land - where
    /// Company clears its report ledger - the round is closed to late messages.
    /// </summary>
    internal static class ProgressionReportShareNetwork
    {
        private const string ShareMessage = "Y4NGZProgression.ReportXpShare.v1";

        private static readonly ProgressionReportShareInbox Inbox = new ProgressionReportShareInbox();
        private static NetworkManager _registeredNetworkManager;

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
                messaging.RegisterNamedMessageHandler(ShareMessage, OnShare);
                _registeredNetworkManager = network;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Progression] Report XP share handler registration failed: " + ex.Message);
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
                Inbox.ResetSession();
                Unregister();
            }
        }

        /// <summary>
        /// Every peer, once per round from the finalize the round latch admits, after its own
        /// report was recorded: sends that same presentation to the rest of the crew.
        /// </summary>
        internal static void PublishLocal(
            int localSlot,
            int total,
            IReadOnlyList<ProgressionReportSource> sources,
            int roundSequence,
            IReadOnlyList<KeyValuePair<string, int>> lines)
        {
            try
            {
                NetworkManager network = NetworkManager.Singleton;
                if (localSlot < 0 || network == null || !network.IsListening || !CompanyReportXpBridge.CanShare)
                    return;
                if (network.IsServer && network.ConnectedClientsIds.Count <= 1)
                    return;

                if (!CctvEmployeeStatisticsNetwork.TryGetRoundEpoch(out ulong epoch, out ulong levelToken))
                {
                    Plugin.Log?.LogWarning("[Progression] Report XP not shared with the crew: no round epoch yet.");
                    return;
                }

                byte[] payload = ProgressionReportShare
                    .FromRound(epoch, levelToken, localSlot, total, sources, roundSequence, lines)
                    .Encode();
                if (payload == null)
                {
                    Plugin.Log?.LogWarning(
                        "[Progression] Report XP not shared with the crew: it breaks a share cap "
                        + $"({ProgressionReportShare.MaxPayloadBytes} bytes, {ProgressionReportShare.MaxSources} sources, "
                        + $"{ProgressionReportShare.MaxLines} lines, {ProgressionReportShare.MaxKeyBytes}-byte keys).");
                    return;
                }

                EnsureRegistered();
                if (network.IsServer)
                    SendToClients(network, payload, network.LocalClientId);
                else
                    SendToServer(network, payload);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Progression] Could not share report XP with the crew: " + ex.Message);
            }
        }

        /// <summary>The report is over (ship made ready to land); its round admits nothing more.</summary>
        internal static void CloseRound()
        {
            if (CctvEmployeeStatisticsNetwork.TryGetRoundEpoch(out ulong epoch, out ulong levelToken))
                Inbox.CloseRound(epoch, levelToken);
        }

        private static void OnShare(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            try
            {
                if (network == null || !network.IsListening || senderClientId == network.LocalClientId)
                    return;

                if (!TryReadPayload(reader, out byte[] payload)
                    || !ProgressionReportShare.TryDecode(payload, out ProgressionReportShare share))
                {
                    Plugin.Log?.LogDebug($"[Progression] Dropped a malformed or oversized report XP share from client {senderClientId}.");
                    return;
                }

                if (network.IsServer)
                {
                    // An original: a client's own report, for the slot its own connection plays.
                    if (!TryGetConnectionSlot(senderClientId, out int senderSlot) || senderSlot != share.Slot)
                        return;
                }
                else if (senderClientId != NetworkManager.ServerClientId)
                {
                    // A client hears only the host: its own report, or a relay.
                    return;
                }

                bool current = CctvEmployeeStatisticsNetwork.IsCurrentRoundEpoch(share.Epoch, share.LevelToken);
                if (!Inbox.TryAdmit(share.Epoch, share.LevelToken, current, share.Slot,
                        ProgressionPerformanceReportUi.GetLocalPlayerIndex()))
                {
                    return;
                }

                // Relayed whether or not this host can render it: the host is the only route
                // between clients.
                if (network.IsServer)
                    SendToClients(network, payload, senderClientId);

                if (CompanyReportXpBridge.RecordShare(share))
                    ProgressionPerformanceReportUi.ReapplyRemoteSlot(share.Slot);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Progression] Invalid report XP share: " + ex.Message);
            }
        }

        private static bool TryReadPayload(FastBufferReader reader, out byte[] payload)
        {
            payload = null;
            reader.ReadValueSafe(out ushort length);
            // At least the declared bytes; the payload itself refuses trailing data, so no
            // assumption is made about how the transport sizes the reader around it.
            if (length == 0
                || length > ProgressionReportShare.MaxPayloadBytes
                || reader.Length - reader.Position < length)
            {
                return false;
            }

            payload = new byte[length];
            reader.ReadBytesSafe(ref payload, length);
            return true;
        }

        private static bool TryGetConnectionSlot(ulong clientId, out int slot)
        {
            slot = -1;
            Dictionary<ulong, int> players = StartOfRound.Instance?.ClientPlayerList;
            return players != null && players.TryGetValue(clientId, out slot) && slot >= 0;
        }

        private static void SendToClients(NetworkManager network, byte[] payload, ulong exceptClientId)
        {
            var recipients = new List<ulong>();
            IReadOnlyList<ulong> connected = network.ConnectedClientsIds;
            for (int i = 0; i < connected.Count; i++)
            {
                ulong clientId = connected[i];
                if (clientId != network.LocalClientId && clientId != exceptClientId)
                    recipients.Add(clientId);
            }

            if (recipients.Count == 0)
                return;

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null)
                return;
            using (FastBufferWriter writer = CreateWriter(payload))
                messaging.SendNamedMessage(ShareMessage, recipients, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        /// <summary>
        /// A client's own share. NGO refuses the recipient-list overload from a client, so this is
        /// the single-recipient overload every other client-to-host send here uses.
        /// </summary>
        private static void SendToServer(NetworkManager network, byte[] payload)
        {
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null)
                return;
            using (FastBufferWriter writer = CreateWriter(payload))
                messaging.SendNamedMessage(ShareMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        private static FastBufferWriter CreateWriter(byte[] payload)
        {
            var writer = new FastBufferWriter(payload.Length + sizeof(ushort), Allocator.Temp);
            writer.WriteValueSafe((ushort)payload.Length);
            writer.WriteBytesSafe(payload, payload.Length);
            return writer;
        }

        private static void Unregister()
        {
            CustomMessagingManager messaging = _registeredNetworkManager?.CustomMessagingManager;
            if (messaging != null)
            {
                try
                {
                    messaging.UnregisterNamedMessageHandler(ShareMessage);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogDebug("[Progression] Report XP share handler unregister failed: " + ex.Message);
                }
            }
            _registeredNetworkManager = null;
        }
    }
}
