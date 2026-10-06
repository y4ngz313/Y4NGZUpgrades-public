using System;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Y4NGZUpgrades.Gui;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades
{
    internal enum HostSaveScope
    {
        /// <summary>Hosting or solo: the local save identity is authoritative.</summary>
        UseLocalSave = 0,

        /// <summary>Joined a remote host whose save identity is known.</summary>
        UseHostKey = 1,

        /// <summary>Joined a remote host, identity not yet delivered: persist nothing.</summary>
        Pending = 2
    }

    /// <summary>
    /// Scopes every Y4NGZUpgrades persistent store to the HOST's save identity while joined to a
    /// remote lobby.
    ///
    /// <b>Why.</b> Every store (progression, upgrade tiers, PLAYER LEVEL unlocks, EmployeeFile
    /// statistics, ammo reserve) resolves its key through <see cref="SaveKey"/>, which reads the
    /// LOCAL machine's <c>currentSaveFileName</c> and its identity GUID. A joining client therefore
    /// reloaded its own last-used save no matter which host save was actually running, so a host
    /// starting a fresh file still saw clients arrive with rank 25 and a full upgrade tree.
    ///
    /// <b>Mechanism.</b> The host transmits its save identity GUID over a named-message channel
    /// modeled on <see cref="Patches.UpgradeTierSync"/>. Clients then persist under
    /// <c>HOST#&lt;hostGuid&gt;</c> instead of their own <c>LCSaveFileN#&lt;ownGuid&gt;</c>. A fresh
    /// host save produces a new GUID, so clients find no stored state and start fresh; rejoining an
    /// old host save restores that host's state. Hosting and solo play are untouched.
    ///
    /// <b>The client's own identity-keyed state is never read, written, or deleted while joined.</b>
    /// </summary>
    internal static class HostSaveIdentity
    {
        private const string MSG_REQUEST_SERVER = "Y4NGZHostSave.RequestServerRpc";
        private const string MSG_IDENTITY_CLIENT = "Y4NGZHostSave.IdentityClientRpc";

        internal const string HostKeyPrefix = "HOST#";

        private const float RequestRetrySeconds = 1.5f;

        /// <summary>
        /// Only ever consumed when the host never answers at all (vanilla host, or a host running a
        /// build without this channel). A host that answers "no identity yet" - which happens while
        /// a brand-new save file has not been written to disk - keeps the client Pending forever
        /// rather than silently falling back onto the client's own save.
        /// </summary>
        private const float NoReplyGraceSeconds = 12f;

        private static bool handlersRegistered;
        private static NetworkManager registeredNetworkManager;
        private static string hostIdentity;
        private static string hostSaveKey;
        private static bool hostSpeaksProtocol;
        private static float nextRequestAt;
        private static float graceExpiresAt;
        private static bool fallbackLogged;
        private static bool resolving;
        private static bool mutatingStores;

        // - Public API -

        internal static string CurrentHostSaveKey => hostSaveKey;

        /// <summary>
        /// Decides which save scope the local machine should persist under right now.
        /// Called from <see cref="SaveKey"/> on every key resolution, so it must stay cheap.
        /// </summary>
        internal static HostSaveScope Resolve(out string key)
        {
            key = null;

            // Re-entrancy. Two paths come back through SaveKey while we are mid-decision:
            //  - TryGetLocalHostIdentity asks SaveKey for the host's own key.
            //  - Flushing/re-pointing the stores makes each of them ask for a key.
            // Both must observe the fields exactly as they stand right now and must not re-run any
            // request, fallback, or clear logic - otherwise ClearIdentity recurses into itself.
            if (resolving || mutatingStores)
            {
                if (hostSaveKey == null)
                    return HostSaveScope.UseLocalSave;

                key = hostSaveKey;
                return HostSaveScope.UseHostKey;
            }

            if (!IsJoinedRemoteClient())
            {
                if (hostSaveKey != null || hostIdentity != null)
                    ClearIdentity("no longer joined to a remote host");
                return HostSaveScope.UseLocalSave;
            }

            if (hostSaveKey != null)
            {
                key = hostSaveKey;
                return HostSaveScope.UseHostKey;
            }

            if (graceExpiresAt <= 0f)
                graceExpiresAt = Time.unscaledTime + NoReplyGraceSeconds;

            RequestIdentityIfDue();

            // A host that has answered at least once is running this channel: keep waiting for a
            // real identity rather than writing host-session progress into the client's own save.
            if (!hostSpeaksProtocol && Time.unscaledTime >= graceExpiresAt)
            {
                if (!fallbackLogged)
                {
                    fallbackLogged = true;
                    Plugin.Log?.LogWarning(
                        "[HostSaveIdentity] Host never sent a save identity; falling back to this " +
                        "client's own save scope (host is probably running an older build).");

                    // F-INFRA-2: the fallback is a store-scope change like any other. Without this
                    // the stores stay pointed at nothing - StartOfRound.Start already tried and
                    // failed - and every upgrade reads level 0 for the whole lobby. The latch makes
                    // the re-entrant SaveKey calls the re-point makes observe the decision above.
                    mutatingStores = true;
                    try
                    {
                        RepointLiveStores();
                    }
                    finally
                    {
                        mutatingStores = false;
                    }
                }

                return HostSaveScope.UseLocalSave;
            }

            return HostSaveScope.Pending;
        }

        internal static void RegisterNetworkHandlers()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            CustomMessagingManager mgr = networkManager?.CustomMessagingManager;
            if (networkManager == null || mgr == null)
                return;

            // Netcode builds a fresh CustomMessagingManager per host/join, so key the latch on the
            // manager instance the way UpgradeTierSync does.
            if (handlersRegistered && ReferenceEquals(registeredNetworkManager, networkManager))
                return;
            if (handlersRegistered)
                UnregisterNetworkHandlers();

            try
            {
                if (networkManager.IsServer)
                    mgr.RegisterNamedMessageHandler(MSG_REQUEST_SERVER, OnRequestIdentityServerRpc);
                else
                    mgr.RegisterNamedMessageHandler(MSG_IDENTITY_CLIENT, OnIdentityClientRpc);

                handlersRegistered = true;
                registeredNetworkManager = networkManager;
                Plugin.Log?.LogInfo("[HostSaveIdentity] network handlers registered.");
            }
            catch (Exception e)
            {
                handlersRegistered = false;
                registeredNetworkManager = null;
                Plugin.Log?.LogError($"[HostSaveIdentity] handler registration failed: {e}");
            }
        }

        internal static void RequestIdentityFromHost()
        {
            nextRequestAt = 0f;
            RequestIdentityIfDue();
        }

        internal static void Clear()
        {
            ClearIdentity("disconnected");
            UnregisterNetworkHandlers();
        }

        /// <summary>
        /// Drives the request retry while nothing else is asking <see cref="SaveKey"/> for a key.
        /// </summary>
        internal static void Tick()
        {
            Resolve(out _);
        }

        // - Private -

        private static bool IsJoinedRemoteClient()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            return networkManager != null
                   && networkManager.IsListening
                   && networkManager.IsClient
                   && !networkManager.IsServer;
        }

        private static void RequestIdentityIfDue()
        {
            if (Time.unscaledTime < nextRequestAt)
                return;

            nextRequestAt = Time.unscaledTime + RequestRetrySeconds;

            NetworkManager networkManager = NetworkManager.Singleton;
            CustomMessagingManager mgr = networkManager?.CustomMessagingManager;
            if (mgr == null || networkManager.IsServer || !networkManager.IsConnectedClient)
                return;

            try
            {
                using var writer = new FastBufferWriter(1, Allocator.Temp);
                mgr.SendNamedMessage(
                    MSG_REQUEST_SERVER, NetworkManager.ServerClientId, writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[HostSaveIdentity] identity request failed: {e.Message}");
            }
        }

        private static void OnRequestIdentityServerRpc(ulong senderId, FastBufferReader reader)
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null || !networkManager.IsServer)
                return;

            try
            {
                // Always answer, even with an empty identity: the reply itself tells the client that
                // this host speaks the channel, which keeps it from falling back to its own save
                // while a brand-new host file has yet to be written to disk.
                string identity = TryGetLocalHostIdentity(out string resolved) ? resolved : string.Empty;

                using var writer = new FastBufferWriter(128, Allocator.Temp);
                writer.WriteValueSafe(new FixedString64Bytes(identity));
                networkManager.CustomMessagingManager.SendNamedMessage(
                    MSG_IDENTITY_CLIENT, senderId, writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[HostSaveIdentity] OnRequestIdentityServerRpc failed: {e}");
            }
        }

        private static void OnIdentityClientRpc(ulong senderId, FastBufferReader reader)
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null || networkManager.IsServer
                || senderId != NetworkManager.ServerClientId)
            {
                return;
            }

            try
            {
                reader.ReadValueSafe(out FixedString64Bytes payload);
                hostSpeaksProtocol = true;

                string identity = payload.ToString()?.Trim();
                if (string.IsNullOrEmpty(identity))
                {
                    // Host has no save identity yet (fresh file not written). Keep retrying.
                    return;
                }

                if (!Guid.TryParseExact(identity, "N", out _))
                {
                    Plugin.Log?.LogWarning(
                        $"[HostSaveIdentity] Ignoring malformed host identity '{identity}'.");
                    return;
                }

                ApplyIdentity(identity);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[HostSaveIdentity] OnIdentityClientRpc failed: {e}");
            }
        }

        private static void ApplyIdentity(string identity)
        {
            if (string.Equals(hostIdentity, identity, StringComparison.OrdinalIgnoreCase))
                return;

            mutatingStores = true;
            try
            {
                if (hostIdentity != null)
                {
                    // The host wiped and recreated its file mid-lobby. Flush what is still live under
                    // the OLD host key before re-pointing; nothing is deleted, so the old rows
                    // survive a later rejoin of that same host save.
                    FlushLiveStores();
                    Plugin.Log?.LogInfo(
                        $"[HostSaveIdentity] joined-host identity changed {hostIdentity} -> {identity}; " +
                        "swapping store scope (previous host state retained on disk).");
                }

                hostIdentity = identity;
                hostSaveKey = HostKeyPrefix + identity;
                fallbackLogged = false;

                Plugin.Log?.LogInfo(
                    $"[HostSaveIdentity] joined-host identity={identity}, store={hostSaveKey}");

                // Re-point every cached store immediately so the first read after the handoff already
                // sees host-scoped state instead of an empty Pending snapshot.
                RepointLiveStores();
            }
            finally
            {
                mutatingStores = false;
            }
        }

        private static void ClearIdentity(string reason)
        {
            mutatingStores = true;
            try
            {
                if (hostIdentity != null)
                {
                    // Runs while hostSaveKey is still set, so this writes host-session state back
                    // under the HOST key rather than leaking it into the local save.
                    FlushLiveStores();
                    Plugin.Log?.LogInfo(
                        $"[HostSaveIdentity] releasing joined-host scope {hostSaveKey} ({reason}).");
                }

                hostIdentity = null;
                hostSaveKey = null;
                hostSpeaksProtocol = false;
                nextRequestAt = 0f;
                graceExpiresAt = 0f;
                fallbackLogged = false;

                // Drop the host-scoped snapshots so the next read re-loads the local save.
                RepointLiveStores();
            }
            finally
            {
                mutatingStores = false;
            }
        }

        private static void FlushLiveStores()
        {
            TryRun(ProgressionManager.Save, "progression flush");
            TryRun(Y4NGZUpgradeManager.Save, "upgrade flush");
            TryRun(PlayerLevelStore.Save, "player-level flush");
        }

        private static void RepointLiveStores()
        {
            TryRun(() => ProgressionManager.SwitchToCurrentSave(), "progression re-point");
            TryRun(() => Y4NGZUpgradeManager.SwitchToCurrentSave(), "upgrade re-point");
            TryRun(PlayerLevelStore.Reload, "player-level re-point");
            TryRun(EmployeeStatistics.SwitchToCurrentSave, "employee-file re-point");
            // F-INFRA-3: whatever tiers the host has cached for this client were published from the
            // ConnectClientToPlayerObject postfix, while the store was still unpointed - i.e. six
            // zeros. SwitchToCurrentSave now raises UpgradesChanged on a real scope change, but a
            // re-point that lands on the same key must re-publish too, so say it explicitly here.
            TryRun(Patches.UpgradeTierSync.BroadcastLocalTiers, "upgrade tier re-broadcast");
            // BetterArmory's ammo reserve re-points off this event (#266).
            TryRun(SaveIdentityApi.RaiseStoresRepointed, "cross-plugin store re-point");
        }

        private static void TryRun(Action action, string label)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[HostSaveIdentity] {label} failed: {e.Message}");
            }
        }

        private static bool TryGetLocalHostIdentity(out string identity)
        {
            identity = null;
            resolving = true;
            try
            {
                if (!SaveKey.TryGetCurrentOrSlot(out string key) || string.IsNullOrWhiteSpace(key))
                    return false;

                int separator = key.LastIndexOf('#');
                if (separator <= 0 || separator + 1 >= key.Length)
                    return false;

                string candidate = key.Substring(separator + 1);
                if (!Guid.TryParseExact(candidate, "N", out _))
                    return false;

                identity = candidate;
                return true;
            }
            finally
            {
                resolving = false;
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
                        mgr.UnregisterNamedMessageHandler(MSG_REQUEST_SERVER);
                    else
                        mgr.UnregisterNamedMessageHandler(MSG_IDENTITY_CLIENT);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning(
                        $"[HostSaveIdentity] handler unregister failed: {e.Message}");
                }
            }

            handlersRegistered = false;
            registeredNetworkManager = null;
        }
    }

    [HarmonyPatch]
    internal static class HostSaveIdentityPatch
    {
        /// <summary>
        /// Runs on every peer. The host registers its responder here (before any client can join)
        /// and clients fire their first identity request from the same hook UpgradeTierSync uses.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void AfterConnectClientToPlayerObject()
        {
            try
            {
                HostSaveIdentity.RegisterNetworkHandlers();
                HostSaveIdentity.RequestIdentityFromHost();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[HostSaveIdentity] AfterConnectClientToPlayerObject: {e}");
            }
        }

        /// <summary>
        /// A crew wipe can delete and recreate the host's save file, minting a new identity GUID.
        /// Re-ask so clients follow the host onto the new save instead of writing into the old one.
        /// </summary>
        [HarmonyPatch(typeof(StartOfRound), "ResetShip")]
        [HarmonyPostfix]
        private static void AfterResetShip()
        {
            try
            {
                HostSaveIdentity.RegisterNetworkHandlers();
                HostSaveIdentity.RequestIdentityFromHost();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[HostSaveIdentity] AfterResetShip: {e}");
            }
        }

        [HarmonyPatch(typeof(StartOfRound), "Update")]
        [HarmonyPostfix]
        private static void AfterStartOfRoundUpdate()
        {
            HostSaveIdentity.Tick();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void AfterDisconnect()
        {
            try
            {
                HostSaveIdentity.Clear();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[HostSaveIdentity] AfterDisconnect: {e}");
            }
            finally
            {
                // F-INFRA-10: vanilla Disconnect has already run SaveGame(), so the ES3 file a
                // pending identity was reserved for now exists on disk. Resolve once more - that is
                // what promotes the reserved GUID into the file - before the reservation is
                // dropped. Otherwise the next boot mints a fresh GUID, the old row becomes an
                // orphan and SaveDataLifecycle.ReconcileOrphans deletes the session's progress.
                try
                {
                    SaveKey.TryGetCurrent(out _);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning(
                        $"[HostSaveIdentity] pending save identity could not be promoted: {e.Message}");
                }

                SaveKey.ClearPendingIdentities();
            }
        }
    }
}
