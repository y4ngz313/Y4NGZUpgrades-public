using System;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;

namespace Y4NGZUpgrades
{
    internal static class LobbyTokenCommandNetwork
    {
        private const string SetTokensMessage = "Y4NGZUpgrades.SetLobbyTokens.v1";
        private static NetworkManager _registeredNetworkManager;

        internal static bool TrySetLobbyTokens(
            int amount,
            out int connectedEmployees,
            out string error)
        {
            connectedEmployees = 0;
            if (amount < 0 || amount > LobbyTokenCommandParser.MaximumTokenBalance)
            {
                error = $"Amount must be from 0 to {LobbyTokenCommandParser.MaximumTokenBalance}.";
                return false;
            }

            EnsureRegistered();
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messages = network?.CustomMessagingManager;
            if (network == null || messages == null)
            {
                error = "Lobby networking is not ready.";
                return false;
            }
            if (!network.IsServer)
            {
                error = "Only the host can set lobby Tokens.";
                return false;
            }
            if (!ProgressionApi.TrySetTokensForTesting(amount))
            {
                error = "The host token balance could not be set for the current save.";
                return false;
            }

            try
            {
                using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
                {
                    writer.WriteValueSafe(amount);
                    foreach (ulong clientId in network.ConnectedClientsIds)
                    {
                        connectedEmployees++;
                        if (clientId == network.LocalClientId)
                            continue;

                        messages.SendNamedMessage(
                            SetTokensMessage,
                            clientId,
                            writer,
                            NetworkDelivery.ReliableSequenced);
                    }
                }

                Plugin.Log?.LogInfo(
                    $"[SetTokens] Host set {connectedEmployees} connected employee balance(s) to {amount} Tokens.");
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Host Tokens were set, but the lobby broadcast failed: {ex.Message}";
                Plugin.Log?.LogWarning($"[SetTokens] {error}");
                return false;
            }
        }

        private static void EnsureRegistered()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null || _registeredNetworkManager == network)
                return;

            Shutdown();
            try
            {
                network.CustomMessagingManager.RegisterNamedMessageHandler(
                    SetTokensMessage,
                    OnSetTokensMessage);
                _registeredNetworkManager = network;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[SetTokens] Network handler registration failed: {ex.Message}");
            }
        }

        private static void Shutdown()
        {
            if (_registeredNetworkManager?.CustomMessagingManager != null)
            {
                try
                {
                    _registeredNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(
                        SetTokensMessage);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[SetTokens] Network handler unregister failed: {ex.Message}");
                }
            }

            _registeredNetworkManager = null;
        }

        private static void OnSetTokensMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                reader.ReadValueSafe(out int amount);
                if (amount < 0 || amount > LobbyTokenCommandParser.MaximumTokenBalance)
                    return;

                if (!ProgressionApi.TrySetTokensForTesting(amount))
                    Plugin.Log?.LogWarning("[SetTokens] Client could not set Tokens for the current save.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[SetTokens] Malformed host message: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.Awake))]
        private static class StartOfRoundAwakePatch
        {
            private static void Postfix() => EnsureRegistered();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        private static class DisconnectPatch
        {
            private static void Prefix() => Shutdown();
        }
    }
}
