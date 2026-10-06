using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ForemanSupportPatch
    {
        // F-FOREMAN-A-6: the name is bumped alongside the payload shrink (the dead Rally Call
        // tier/expiry fields are gone). An old build cannot mis-parse the shorter record
        // because it never registers a handler for this name.
        private const string MSG_SUPPORT_STATE = "ForemanSupport_State2";
        private const float STATE_BROADCAST_INTERVAL = 0.75f;
        private const float STATE_EXPIRY_SECONDS = 2.5f;

        private sealed class SupportState
        {
            internal int BuddyTier;
            internal float ExpiresAt;
            internal Vector3 Position;
        }

        // Review pass: the aura is registered with the single owner of movementSpeed rather than
        // written into the field here (see ApplySpeedBuff). The id only has to be stable.
        private const string SPEED_MULTIPLIER_SOURCE = "y4ngz.foreman.buddy-aura";

        private static readonly Dictionary<int, SupportState> SupportStates = new Dictionary<int, SupportState>();

        // F-FOREMAN-A-11: leftover sub-1HP mitigation, per player id, carried into the next hit.
        private static readonly Dictionary<int, float> MitigationCarry = new Dictionary<int, float>();

        private static bool _handlersRegistered;
        private static float _nextStateBroadcastTime;

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        /// <summary>
        /// Netcode nulls the CustomMessagingManager on shutdown and creates a fresh one for the
        /// next host/join, so the registration latch has to drop here or the second lobby of a
        /// session never receives support state.
        /// </summary>
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _handlersRegistered = false;
            SupportStates.Clear();
            UnwindAppliedSpeedBonuses();
            MitigationCarry.Clear();
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            SupportStates.Clear();
            UnwindAppliedSpeedBonuses();
            MitigationCarry.Clear();
            _nextStateBroadcastTime = 0f;
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            SupportStates.Clear();
            UnwindAppliedSpeedBonuses();
            MitigationCarry.Clear();
        }

        /// <summary>
        /// F-FOREMAN-A-1: the aura must not survive the lifecycle boundary the crew crosses while
        /// standing together in the ship, or it compounds into the next landing. Deregistering the
        /// multiplier is the whole unwind now that <see cref="MergedUpgradePatches"/> owns the
        /// field (see <see cref="ApplySpeedBuff"/>); it recomputes from its own snapshot base on
        /// the next LateUpdate, so nothing stays baked into movementSpeed.
        /// </summary>
        private static void UnwindAppliedSpeedBonuses()
        {
            MergedUpgradePatches.ClearSpeedMultiplier(SPEED_MULTIPLIER_SOURCE);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            RegisterNetworkHandlers();
            BroadcastLocalStateIfNeeded(__instance, force: false);
        }


        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            ApplySpeedBuff(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPrefix]
        private static void DamagePlayerPrefix(PlayerControllerB __instance, ref int damageNumber)
        {
            if (!IsLocalPlayer(__instance) || damageNumber <= 0)
                return;

            // The reductions deliberately do NOT stack - the largest one applies (documented in
            // the Buddy System / Ping catalog text, F-FOREMAN-A-15).
            float reduction = Mathf.Clamp01(Mathf.Max(
                GetSupportDamageReduction(__instance),
                ForemanPingPatch.GetMarkedTeammateDamageReduction(__instance),
                ForemanPingPatch.GetRecentMarkedEnemyDamageReduction()));

            if (reduction <= 0f)
                return;

            // F-FOREMAN-A-11: RoundToInt erased the whole tier-1 3% cut (20 -> 19.4 -> 19 was the
            // only vanilla hit size that moved at all). Mitigate in fractions of a point and carry
            // the remainder to the next hit, so a small percentage pays out exactly over time.
            int playerId = (int)__instance.playerClientId;
            MitigationCarry.TryGetValue(playerId, out float carry);

            float mitigation = damageNumber * reduction + carry;
            int whole = Mathf.FloorToInt(mitigation);
            if (whole > damageNumber)
                whole = damageNumber;

            MitigationCarry[playerId] = Mathf.Clamp(mitigation - whole, 0f, 1f);
            damageNumber = Mathf.Max(0, damageNumber - whole);
        }

        private static void BroadcastLocalStateIfNeeded(PlayerControllerB player, bool force)
        {
            if (player == null)
                return;

            UpdateLocalSupportState(player);
            if (!force && Time.time < _nextStateBroadcastTime)
                return;

            int buddyTier = GetLocalBuddyTier(player);
            if (buddyTier <= 0)
                return;

            _nextStateBroadcastTime = Time.time + STATE_BROADCAST_INTERVAL;
            BroadcastSupportState((int)player.playerClientId, buddyTier, player.transform.position);
        }

        private static void UpdateLocalSupportState(PlayerControllerB player)
        {
            if (player == null)
                return;

            int playerId = (int)player.playerClientId;
            SupportState state = GetState(playerId);
            state.BuddyTier = GetLocalBuddyTier(player);
            state.ExpiresAt = Time.time + STATE_EXPIRY_SECONDS;
            state.Position = player.transform.position;
        }

        private static int GetLocalBuddyTier(PlayerControllerB player)
        {
            return CanSupplyBuddyAura(player) ? BuddySystemUpgrade.GetTier() : 0;
        }

        private static bool CanSupplyBuddyAura(PlayerControllerB player)
        {
            return player != null && player.isPlayerControlled && !player.isPlayerDead;
        }

        private static float GetSupportSpeedBonus(PlayerControllerB localPlayer)
        {
            float bestBuddy = 0f;
            int localId = (int)localPlayer.playerClientId;

            foreach (KeyValuePair<int, SupportState> pair in SupportStates)
            {
                SupportState state = pair.Value;
                if (!IsStateFresh(state))
                    continue;

                if (pair.Key == localId || state.BuddyTier <= 0 || !CanUseProviderStateForBuddy(pair.Key))
                    continue;

                Vector3 providerPosition = ResolveProviderPosition(pair.Key, state);
                if (Vector3.Distance(localPlayer.transform.position, providerPosition) <= BuddySystemUpgrade.GetRange(state.BuddyTier))
                    bestBuddy = Mathf.Max(bestBuddy, BuddySystemUpgrade.GetBuff(state.BuddyTier));
            }

            return bestBuddy;
        }

        private static float GetSupportDamageReduction(PlayerControllerB localPlayer)
        {
            float bestBuddy = 0f;
            int localId = (int)localPlayer.playerClientId;

            foreach (KeyValuePair<int, SupportState> pair in SupportStates)
            {
                SupportState state = pair.Value;
                if (!IsStateFresh(state))
                    continue;

                if (pair.Key == localId || state.BuddyTier <= 0 || !CanUseProviderStateForBuddy(pair.Key))
                    continue;

                Vector3 providerPosition = ResolveProviderPosition(pair.Key, state);
                if (Vector3.Distance(localPlayer.transform.position, providerPosition) <= BuddySystemUpgrade.GetRange(state.BuddyTier))
                    bestBuddy = Mathf.Max(bestBuddy, BuddySystemUpgrade.GetBuff(state.BuddyTier));
            }

            return Mathf.Clamp01(bestBuddy);
        }

        /// <summary>
        /// Read-only inspector query for the support auras currently applying to one player.
        /// This follows the same fresh-state, provider-validity, and range gates as the actual
        /// speed and damage effects; it never creates or refreshes network state.
        /// </summary>
        internal static int GetActiveBuddyAuraProviderCount(PlayerControllerB localPlayer)
        {
            if (localPlayer == null)
                return 0;

            int count = 0;
            int localId = (int)localPlayer.playerClientId;
            foreach (KeyValuePair<int, SupportState> pair in SupportStates)
            {
                SupportState state = pair.Value;
                if (!IsStateFresh(state) || pair.Key == localId || state.BuddyTier <= 0 ||
                    !CanUseProviderStateForBuddy(pair.Key))
                {
                    continue;
                }

                Vector3 providerPosition = ResolveProviderPosition(pair.Key, state);
                if (Vector3.Distance(localPlayer.transform.position, providerPosition) <=
                    BuddySystemUpgrade.GetRange(state.BuddyTier))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Registers the aura with the sole owner of movementSpeed.</summary>
        private static void ApplySpeedBuff(PlayerControllerB player)
        {
            // SetSpeedMultiplier drops the source when the value is <= 1, so a lapsed aura needs no
            // separate clear here.
            MergedUpgradePatches.SetSpeedMultiplier(
                SPEED_MULTIPLIER_SOURCE, 1f + GetSupportSpeedBonus(player));
        }

        private static SupportState GetState(int playerId)
        {
            if (!SupportStates.TryGetValue(playerId, out SupportState state))
            {
                state = new SupportState();
                SupportStates[playerId] = state;
            }

            return state;
        }

        private static bool IsStateFresh(SupportState state)
        {
            return state != null && Time.time < state.ExpiresAt;
        }

        private static bool CanUseProviderStateForBuddy(int playerId)
        {
            PlayerControllerB player;
            if (!TryResolveProviderPlayer(playerId, out player))
                return true;

            return player.isPlayerControlled && !player.isPlayerDead;
        }

        private static Vector3 ResolveProviderPosition(int playerId, SupportState state)
        {
            PlayerControllerB player;
            if (TryResolveProviderPlayer(playerId, out player))
                return player.transform.position;

            return state.Position;
        }

        private static bool TryResolveProviderPlayer(int playerId, out PlayerControllerB player)
        {
            player = null;
            if (StartOfRound.Instance == null
                || StartOfRound.Instance.allPlayerScripts == null
                || playerId < 0
                || playerId >= StartOfRound.Instance.allPlayerScripts.Length)
            {
                return false;
            }

            player = StartOfRound.Instance.allPlayerScripts[playerId];
            return player != null;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered) return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_SUPPORT_STATE, OnReceiveSupportState);
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanSupportPatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

#pragma warning disable Harmony003
        private const int SUPPORT_STATE_BYTES = sizeof(int) * 2 + sizeof(float) * 3;

        private static void WriteSupportState(FastBufferWriter writer, int playerId, int buddyTier, Vector3 position)
        {
            writer.WriteValueSafe(playerId);
            writer.WriteValueSafe(buddyTier);
            writer.WriteValueSafe(position.x);
            writer.WriteValueSafe(position.y);
            writer.WriteValueSafe(position.z);
        }

        /// <summary>
        /// Netcode has no client-to-all primitive: SendNamedMessageToAll reads ConnectedClientsIds,
        /// whose getter throws off-host. The host broadcasts; a client sends to the host, which
        /// relays. This runs every 0.75s from a PlayerControllerB.Update postfix, so an escaping
        /// exception would break the whole patch chain - hence the catch.
        /// </summary>
        private static void BroadcastSupportState(int playerId, int buddyTier, Vector3 position)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            FastBufferWriter writer = new FastBufferWriter(SUPPORT_STATE_BYTES, Allocator.Temp);
            try
            {
                WriteSupportState(writer, playerId, buddyTier, position);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_SUPPORT_STATE, writer);
                else
                    messaging.SendNamedMessage(MSG_SUPPORT_STATE, NetworkManager.ServerClientId, writer);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanSupportPatch: support state send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>Host-only re-broadcast of one client's state to every other client.</summary>
        private static void RelaySupportState(ulong excludeClientId, int playerId, int buddyTier, Vector3 position)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(SUPPORT_STATE_BYTES, Allocator.Temp);
                try
                {
                    WriteSupportState(writer, playerId, buddyTier, position);
                    messaging.SendNamedMessage(MSG_SUPPORT_STATE, clientId, writer);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"ForemanSupportPatch: support state relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        private static PlayerControllerB ResolvePlayerByActualClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].actualClientId == clientId)
                    return players[i];
            }

            return null;
        }

        private static void OnReceiveSupportState(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                int playerId;
                int buddyTier;
                float x, y, z;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out buddyTier);
                reader.ReadValueSafe(out x);
                reader.ReadValueSafe(out y);
                reader.ReadValueSafe(out z);

                NetworkManager network = NetworkManager.Singleton;
                bool relaying = network != null && network.IsServer && senderClientId != network.LocalClientId;
                if (relaying)
                {
                    // The payload's player id is attacker-controlled, so the host ignores it and
                    // uses the connection the message actually arrived on. A client can therefore
                    // only ever publish an aura for itself.
                    PlayerControllerB sender = ResolvePlayerByActualClientId(senderClientId);
                    if (sender == null)
                        return;

                    playerId = (int)sender.playerClientId;
                }

                SupportState state = GetState(playerId);
                state.BuddyTier = buddyTier;
                state.ExpiresAt = Time.time + STATE_EXPIRY_SECONDS;
                state.Position = new Vector3(x, y, z);

                if (relaying)
                    RelaySupportState(senderClientId, playerId, buddyTier, new Vector3(x, y, z));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanSupportPatch: malformed support sync: {ex.Message}");
            }
        }
#pragma warning restore Harmony003
    }
}
