using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Broadcasts a small set of upgrade tiers to every peer so that server-side effects
    /// (Chameleon camera detection, Scavenger fuel deposits) can resolve the tier of an
    /// arbitrary player. Upgrade state is otherwise client-local.
    /// Modeled on <see cref="ExtraSlotManager"/>.
    /// </summary>
    internal static class UpgradeTierSync
    {
        private const string MSG_SET_SERVER = "Y4NGZUpgradeTier.SetServerRpc";
        private const string MSG_SET_CLIENT = "Y4NGZUpgradeTier.SetClientRpc";
        private const string MSG_REQUEST_ALL_SERVER = "Y4NGZUpgradeTier.RequestAllServerRpc";
        private const string MSG_SYNC_ALL_CLIENT = "Y4NGZUpgradeTier.SyncAllClientRpc";
        private const byte MaxSyncedTier = 3;

        // Wire format uses the array index instead of the id string; append only.
        private static readonly string[] SyncedUpgradeIds =
        {
            ChameleonUpgrade.UPGRADE_ID,
            ScavengerUpgrade.UPGRADE_ID,
            QuotaGuardUpgrade.UPGRADE_ID,
            TurretHackerUpgrade.UPGRADE_ID,
            FieldOperationsUpgrade.UPGRADE_ID,
            NativeFistsUpgrade.UPGRADE_ID,
        };

        // (clientId, syncedIndex) -> tier
        private static readonly Dictionary<(ulong, int), byte> tiersByClient = new Dictionary<(ulong, int), byte>();
        private static bool handlersRegistered;
        private static NetworkManager registeredNetworkManager;
        private static bool upgradesChangedHooked;

        // - Public API -

        public static int GetTier(ulong clientId, string upgradeId)
        {
            int index = IndexOf(upgradeId);
            if (index < 0)
                return 0;

            // The local player's own tiers never need the network round-trip.
            PlayerControllerB local = StartOfRound.Instance?.localPlayerController;
            if (local != null && local.actualClientId == clientId)
                return GetLocalTier(index);

            return tiersByClient.TryGetValue((clientId, index), out byte tier) ? tier : 0;
        }

        public static void RegisterNetworkHandlers()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            CustomMessagingManager mgr = networkManager?.CustomMessagingManager;
            if (networkManager == null || mgr == null)
                return;

            // Netcode drops the CustomMessagingManager on shutdown and builds a fresh one for the
            // next host/join, so the latch alone would leave a second lobby deaf. Comparing the
            // manager instance re-registers exactly once per NetworkManager and never twice for
            // the same one, which is what makes repeated ResetShip calls safe.
            if (handlersRegistered && ReferenceEquals(registeredNetworkManager, networkManager))
                return;
            if (handlersRegistered)
                UnregisterNetworkHandlers();

            try
            {
                if (networkManager.IsServer)
                {
                    mgr.RegisterNamedMessageHandler(MSG_SET_SERVER, OnSetTierServerRpc);
                    mgr.RegisterNamedMessageHandler(MSG_REQUEST_ALL_SERVER, OnRequestAllServerRpc);
                }

                mgr.RegisterNamedMessageHandler(MSG_SET_CLIENT, OnSetTierClientRpc);
                mgr.RegisterNamedMessageHandler(MSG_SYNC_ALL_CLIENT, OnSyncAllClientRpc);

                handlersRegistered = true;
                registeredNetworkManager = networkManager;
                Plugin.Log?.LogInfo("UpgradeTierSync: network handlers registered.");
            }
            catch (Exception e)
            {
                handlersRegistered = false;
                registeredNetworkManager = null;
                Plugin.Log?.LogError($"UpgradeTierSync: handler registration failed: {e}");
            }

            if (!upgradesChangedHooked)
            {
                Y4NGZUpgradeManager.UpgradesChanged += BroadcastLocalTiers;
                upgradesChangedHooked = true;
            }
        }

        public static void BroadcastLocalTiers()
        {
            try
            {
                if (!handlersRegistered)
                    return;

                PlayerControllerB local = StartOfRound.Instance?.localPlayerController;
                NetworkManager networkManager = NetworkManager.Singleton;
                if (local == null || networkManager == null || networkManager.CustomMessagingManager == null)
                    return;

                for (int i = 0; i < SyncedUpgradeIds.Length; i++)
                    SendTier(local.actualClientId, i, (byte)GetLocalTier(i));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"UpgradeTierSync: BroadcastLocalTiers failed: {e}");
            }
        }

        public static void RequestAllFromServer()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null || networkManager.CustomMessagingManager == null || networkManager.IsServer)
                return;

            var writer = new FastBufferWriter(1, Allocator.Temp);
            networkManager.CustomMessagingManager.SendNamedMessage(
                MSG_REQUEST_ALL_SERVER, 0uL, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        public static void Clear()
        {
            tiersByClient.Clear();
            UnregisterNetworkHandlers();
        }

        /// <summary>
        /// Ship reset (crew wipe) drops the cached table but must NOT drop the handlers: nothing
        /// re-registers them mid-session, so unregistering here used to leave every consumer
        /// reading tier 0 for the rest of the lobby. Every peer runs this same postfix, so each
        /// one re-publishes its own tiers and the table repopulates within a round-trip.
        /// </summary>
        public static void ResetForNewCycle()
        {
            tiersByClient.Clear();
            RegisterNetworkHandlers();
            BroadcastLocalTiers();
            RequestAllFromServer();
        }

        // - Private -

        private static int IndexOf(string upgradeId)
        {
            for (int i = 0; i < SyncedUpgradeIds.Length; i++)
            {
                if (string.Equals(SyncedUpgradeIds[i], upgradeId, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int GetLocalTier(int index)
        {
            switch (SyncedUpgradeIds[index])
            {
                case ChameleonUpgrade.UPGRADE_ID: return ChameleonUpgrade.GetTier();
                case ScavengerUpgrade.UPGRADE_ID: return ScavengerUpgrade.GetTier();
                case QuotaGuardUpgrade.UPGRADE_ID: return QuotaGuardUpgrade.GetTier();
                case TurretHackerUpgrade.UPGRADE_ID: return TurretHackerUpgrade.GetTier();
                case FieldOperationsUpgrade.UPGRADE_ID: return FieldOperationsUpgrade.GetTier();
                case NativeFistsUpgrade.UPGRADE_ID: return NativeFistsUpgrade.GetTier();
                default: return 0;
            }
        }

        private static void SendTier(ulong clientId, int index, byte tier)
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager.IsServer)
            {
                tiersByClient[(clientId, index)] = tier;
                SendSetToAllClients(clientId, index, tier);
            }
            else
            {
                // clientId(8) + index(1) + tier(1)
                var writer = new FastBufferWriter(10, Allocator.Temp);
                writer.WriteValueSafe(clientId);
                writer.WriteValueSafe((byte)index);
                writer.WriteValueSafe(tier);
                networkManager.CustomMessagingManager.SendNamedMessage(
                    MSG_SET_SERVER, 0uL, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static void SendSetToAllClients(ulong clientId, int index, byte tier)
        {
            var writer = new FastBufferWriter(10, Allocator.Temp);
            writer.WriteValueSafe(clientId);
            writer.WriteValueSafe((byte)index);
            writer.WriteValueSafe(tier);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                MSG_SET_CLIENT, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        private static void OnSetTierServerRpc(ulong senderId, FastBufferReader reader)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            try
            {
                reader.ReadValueSafe(out ulong clientId);
                reader.ReadValueSafe(out byte index);
                reader.ReadValueSafe(out byte tier);
                if (clientId != senderId || index >= SyncedUpgradeIds.Length || tier > MaxSyncedTier)
                    return;

                tiersByClient[(clientId, index)] = tier;
                SendSetToAllClients(clientId, index, tier);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"UpgradeTierSync: OnSetTierServerRpc failed: {e}");
            }
        }

        private static void OnSetTierClientRpc(ulong senderId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer ||
                senderId != NetworkManager.ServerClientId)
                return;

            try
            {
                reader.ReadValueSafe(out ulong clientId);
                reader.ReadValueSafe(out byte index);
                reader.ReadValueSafe(out byte tier);
                if (index >= SyncedUpgradeIds.Length || tier > MaxSyncedTier)
                    return;

                tiersByClient[(clientId, index)] = tier;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"UpgradeTierSync: OnSetTierClientRpc failed: {e}");
            }
        }

        private static void OnRequestAllServerRpc(ulong senderId, FastBufferReader reader)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            try
            {
                int count = tiersByClient.Count;
                // count(2) + per entry: clientId(8) + index(1) + tier(1)
                var writer = new FastBufferWriter(2 + count * 10, Allocator.Temp);
                writer.WriteValueSafe((short)count);
                foreach (KeyValuePair<(ulong, int), byte> kvp in tiersByClient)
                {
                    writer.WriteValueSafe(kvp.Key.Item1);
                    writer.WriteValueSafe((byte)kvp.Key.Item2);
                    writer.WriteValueSafe(kvp.Value);
                }

                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                    MSG_SYNC_ALL_CLIENT, senderId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"UpgradeTierSync: OnRequestAllServerRpc failed: {e}");
            }
        }

        private static void OnSyncAllClientRpc(ulong senderId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer ||
                senderId != NetworkManager.ServerClientId)
                return;

            try
            {
                reader.ReadValueSafe(out short count);
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out ulong clientId);
                    reader.ReadValueSafe(out byte index);
                    reader.ReadValueSafe(out byte tier);
                    if (index < SyncedUpgradeIds.Length && tier <= MaxSyncedTier)
                        tiersByClient[(clientId, index)] = tier;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"UpgradeTierSync: OnSyncAllClientRpc failed: {e}");
            }
        }

        private static void UnregisterNetworkHandlers()
        {
            if (!handlersRegistered)
                return;

            NetworkManager networkManager = registeredNetworkManager ?? NetworkManager.Singleton;
            CustomMessagingManager mgr = networkManager?.CustomMessagingManager;
            if (mgr != null)
            {
                try
                {
                    if (networkManager.IsServer)
                    {
                        mgr.UnregisterNamedMessageHandler(MSG_SET_SERVER);
                        mgr.UnregisterNamedMessageHandler(MSG_REQUEST_ALL_SERVER);
                    }

                    mgr.UnregisterNamedMessageHandler(MSG_SET_CLIENT);
                    mgr.UnregisterNamedMessageHandler(MSG_SYNC_ALL_CLIENT);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"UpgradeTierSync: handler unregister failed: {e.Message}");
                }
            }

            handlersRegistered = false;
            registeredNetworkManager = null;
        }
    }

    [HarmonyPatch]
    internal static class UpgradeTierSyncPatch
    {
        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void AfterConnectClientToPlayerObject()
        {
            try
            {
                UpgradeTierSync.RegisterNetworkHandlers();
                UpgradeTierSync.BroadcastLocalTiers();
                UpgradeTierSync.RequestAllFromServer();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"UpgradeTierSyncPatch.AfterConnectClientToPlayerObject: {e}");
            }
        }

        [HarmonyPatch(typeof(StartOfRound), "ResetShip")]
        [HarmonyPostfix]
        private static void AfterResetShip()
        {
            try
            {
                UpgradeTierSync.ResetForNewCycle();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"UpgradeTierSyncPatch.AfterResetShip: {e}");
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void AfterDisconnect()
        {
            try
            {
                UpgradeTierSync.Clear();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"UpgradeTierSyncPatch.AfterDisconnect: {e}");
            }
        }
    }
}
