using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;

namespace Y4NGZUpgrades
{
    internal static class ProgressionRankSync
    {
        private const string MsgSetServer = "Y4NGZProgression.SetRankServerRpc";
        private const string MsgSetClient = "Y4NGZProgression.SetRankClientRpc";
        private const string MsgRequestAllServer = "Y4NGZProgression.RequestAllRanksServerRpc";
        private const string MsgSyncAllClient = "Y4NGZProgression.SyncAllRanksClientRpc";

        private static readonly Dictionary<ulong, int> RankXpByClientId =
            new Dictionary<ulong, int>();

        private static bool _handlersRegistered;

        internal static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered)
                return;

            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messages = network?.CustomMessagingManager;
            if (network == null || messages == null)
                return;

            try
            {
                if (network.IsServer)
                {
                    messages.RegisterNamedMessageHandler(MsgSetServer, OnSetRankServerRpc);
                    messages.RegisterNamedMessageHandler(MsgRequestAllServer, OnRequestAllRanksServerRpc);
                }

                messages.RegisterNamedMessageHandler(MsgSetClient, OnSetRankClientRpc);
                messages.RegisterNamedMessageHandler(MsgSyncAllClient, OnSyncAllRanksClientRpc);
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                _handlersRegistered = false;
                Plugin.Log?.LogWarning($"[ProgressionRankSync] handler registration failed: {ex.Message}");
            }
        }

        internal static void Clear()
        {
            RankXpByClientId.Clear();
            UnregisterNetworkHandlers();
        }

        internal static void PublishLocalRank()
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local == null)
                return;

            PublishRank(local.actualClientId, ProgressionManager.CurrentRankXp);
        }

        internal static void RequestAllRanksFromServer()
        {
            RegisterNetworkHandlers();

            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messages = network?.CustomMessagingManager;
            if (network == null || messages == null || !network.IsClient)
                return;

            if (network.IsServer)
                return;

            using (FastBufferWriter writer = new FastBufferWriter(0, Allocator.Temp))
            {
                messages.SendNamedMessage(
                    MsgRequestAllServer,
                    0uL,
                    writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        internal static int GetRankXpForPlayer(PlayerControllerB player)
        {
            if (player == null)
                return 0;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local != null && player == local)
                return ProgressionManager.CurrentRankXp;

            return RankXpByClientId.TryGetValue(player.actualClientId, out int xp)
                ? RankCatalog.ClampXp(xp)
                : 0;
        }

        private static void PublishRank(ulong clientId, int rankXp)
        {
            rankXp = RankCatalog.ClampXp(rankXp);
            RankXpByClientId[clientId] = rankXp;
            RegisterNetworkHandlers();

            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messages = network?.CustomMessagingManager;
            if (network == null || messages == null || !network.IsClient)
                return;

            if (network.IsServer)
            {
                SendSetRankToAllClients(clientId, rankXp);
                return;
            }

            using (FastBufferWriter writer = new FastBufferWriter(sizeof(ulong) + sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(clientId);
                writer.WriteValueSafe(rankXp);
                messages.SendNamedMessage(
                    MsgSetServer,
                    0uL,
                    writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static void SendSetRankToAllClients(ulong clientId, int rankXp)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messages = network?.CustomMessagingManager;
            if (network == null || messages == null)
                return;

            using (FastBufferWriter writer = new FastBufferWriter(sizeof(ulong) + sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(clientId);
                writer.WriteValueSafe(RankCatalog.ClampXp(rankXp));
                messages.SendNamedMessageToAll(
                    MsgSetClient,
                    writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static void OnSetRankServerRpc(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return;

            try
            {
                reader.ReadValueSafe(out ulong clientId);
                reader.ReadValueSafe(out int rankXp);

                rankXp = RankCatalog.ClampXp(rankXp);
                RankXpByClientId[clientId] = rankXp;
                SendSetRankToAllClients(clientId, rankXp);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ProgressionRankSync] malformed rank set from {senderClientId}: {ex.Message}");
            }
        }

        private static void OnSetRankClientRpc(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong clientId);
                reader.ReadValueSafe(out int rankXp);
                RankXpByClientId[clientId] = RankCatalog.ClampXp(rankXp);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ProgressionRankSync] malformed rank sync from {senderClientId}: {ex.Message}");
            }
        }

        private static void OnRequestAllRanksServerRpc(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messages = network?.CustomMessagingManager;
            if (network == null || messages == null || !network.IsServer)
                return;

            try
            {
                int count = RankXpByClientId.Count;
                using (FastBufferWriter writer = new FastBufferWriter(sizeof(short) + count * (sizeof(ulong) + sizeof(int)), Allocator.Temp))
                {
                    writer.WriteValueSafe((short)count);
                    foreach (KeyValuePair<ulong, int> pair in RankXpByClientId)
                    {
                        writer.WriteValueSafe(pair.Key);
                        writer.WriteValueSafe(RankCatalog.ClampXp(pair.Value));
                    }

                    messages.SendNamedMessage(
                        MsgSyncAllClient,
                        senderClientId,
                        writer,
                        NetworkDelivery.ReliableFragmentedSequenced);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ProgressionRankSync] failed to send rank snapshot: {ex.Message}");
            }
        }

        private static void OnSyncAllRanksClientRpc(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out short count);
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out ulong clientId);
                    reader.ReadValueSafe(out int rankXp);
                    RankXpByClientId[clientId] = RankCatalog.ClampXp(rankXp);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ProgressionRankSync] malformed rank snapshot from {senderClientId}: {ex.Message}");
            }
        }

        private static void UnregisterNetworkHandlers()
        {
            if (!_handlersRegistered)
                return;

            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messages = network?.CustomMessagingManager;
            if (network != null && messages != null)
            {
                try
                {
                    if (network.IsServer)
                    {
                        messages.UnregisterNamedMessageHandler(MsgSetServer);
                        messages.UnregisterNamedMessageHandler(MsgRequestAllServer);
                    }

                    messages.UnregisterNamedMessageHandler(MsgSetClient);
                    messages.UnregisterNamedMessageHandler(MsgSyncAllClient);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[ProgressionRankSync] handler unregister failed: {ex.Message}");
                }
            }

            _handlersRegistered = false;
        }
    }
}
