using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZUpgrades.HUD;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ForemanSupportPatch
    {
        private const string MSG_SUPPORT_STATE = "ForemanSupport_State";
        private const float STATE_BROADCAST_INTERVAL = 0.75f;
        private const float STATE_EXPIRY_SECONDS = 2.5f;
        private const string RALLY_HUD_KEY = "rally_call";
        private const int RALLY_HUD_STACK_POSITION = 4;

        private sealed class SupportState
        {
            internal int BuddyTier;
            internal int RallyTier;
            internal float RallyExpiresAt;
            internal float ExpiresAt;
            internal Vector3 Position;
        }

        private sealed class AppliedSupportBonus
        {
            internal float MovementSpeedBonus;
        }

        private static readonly Dictionary<int, SupportState> SupportStates = new Dictionary<int, SupportState>();
        private static readonly Dictionary<int, AppliedSupportBonus> AppliedBonuses = new Dictionary<int, AppliedSupportBonus>();

        private static bool _handlersRegistered;
        private static float _nextStateBroadcastTime;
        private static float _localRallyExpiresAt;
        private static float _localRallyCooldownEnd;

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
            AppliedBonuses.Clear();
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            SupportStates.Clear();
            AppliedBonuses.Clear();
            _nextStateBroadcastTime = 0f;
            _localRallyExpiresAt = 0f;
            _localRallyCooldownEnd = 0f;
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            SupportStates.Clear();
            AppliedBonuses.Clear();
            UpgradeHUDManager.DestroyHUDElement(RALLY_HUD_KEY);
            ForemanAuraStatusHud.Destroy();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            RegisterNetworkHandlers();
            PollRallyKey(__instance);
            UpdateRallyHUD(__instance);
            BroadcastLocalStateIfNeeded(__instance, force: false);
        }

        private static void UpdateRallyHUD(PlayerControllerB player)
        {
            if (!RallyCallUpgrade.IsUnlocked())
            {
                GameObject go = UpgradeHUDManager.GetHUDElement(RALLY_HUD_KEY);
                if (go != null) go.SetActive(false);
                return;
            }

            bool gated = player == null
                || player.isPlayerDead
                || player.isTypingChat
                || player.inTerminalMenu
                || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen);
            if (gated)
            {
                GameObject go = UpgradeHUDManager.GetHUDElement(RALLY_HUD_KEY);
                if (go != null) go.SetActive(false);
                return;
            }

            EnsureRallyHUD();
            GameObject element = UpgradeHUDManager.GetHUDElement(RALLY_HUD_KEY);
            if (element == null)
                return;

            element.SetActive(true);
            UpgradeHUDManager.SetHUDElementLabel(RALLY_HUD_KEY, $"RALLY [{Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.RallyCall, "R")}]");

            if (Time.time < _localRallyCooldownEnd)
            {
                float total = RallyCallUpgrade.DURATION_SECONDS + RallyCallUpgrade.COOLDOWN_SECONDS;
                UpgradeHUDManager.SetCooldownActive(RALLY_HUD_KEY, true);
                UpgradeHUDManager.UpdateFillBar(RALLY_HUD_KEY, (_localRallyCooldownEnd - Time.time) / Mathf.Max(0.01f, total));
            }
            else
            {
                UpgradeHUDManager.SetCooldownActive(RALLY_HUD_KEY, false);
            }
        }

        private static void EnsureRallyHUD()
        {
            if (UpgradeHUDManager.GetHUDElement(RALLY_HUD_KEY) != null)
                return;
            if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null)
                return;

            Canvas canvas = HUDManager.Instance.playerScreenTexture.canvas;
            if (canvas == null)
                return;

            UpgradeHUDManager.CreateIconKeyHUDElement(
                RALLY_HUD_KEY,
                "rally_call",
                $"RALLY [{Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.RallyCall, "R")}]",
                RALLY_HUD_STACK_POSITION,
                canvas.transform);
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

            float reduction = Mathf.Max(
                GetSupportDamageReduction(__instance),
                ForemanPingPatch.GetMarkedTeammateDamageReduction(__instance),
                ForemanPingPatch.GetRecentMarkedEnemyDamageReduction());

            if (reduction <= 0f)
                return;

            damageNumber = Mathf.Max(0, Mathf.RoundToInt(damageNumber * (1f - Mathf.Clamp01(reduction))));
        }

        private static void PollRallyKey(PlayerControllerB player)
        {
            if (!RallyCallUpgrade.IsUnlocked())
                return;
            if (!Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.RallyCall))
                return;

            TryActivateRally(player);
        }

        private static void TryActivateRally(PlayerControllerB player)
        {
            if (!RallyCallUpgrade.IsUnlocked())
                return;
            if (!CanUseActiveAbility(player))
                return;

            if (Time.time < _localRallyCooldownEnd)
            {
                HUDManager.Instance?.DisplayTip("RALLY CALL", $"Cooldown: {_localRallyCooldownEnd - Time.time:F0}s", isWarning: true);
                return;
            }

            int tier = RallyCallUpgrade.GetTier();
            _localRallyExpiresAt = Time.time + RallyCallUpgrade.DURATION_SECONDS;
            _localRallyCooldownEnd = _localRallyExpiresAt + RallyCallUpgrade.COOLDOWN_SECONDS;
            UpdateLocalSupportState(player);
            BroadcastLocalStateIfNeeded(player, force: true);
            BreakGhostGirlHaunting();
            Plugin.Log?.LogInfo($"[Rally Call] Activated tier {tier}.");
        }

        private static bool CanUseActiveAbility(PlayerControllerB player)
        {
            return player != null
                && !player.isPlayerDead
                && !player.isTypingChat
                && !player.inTerminalMenu
                && !player.inSpecialInteractAnimation
                && (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen);
        }

        private static void BroadcastLocalStateIfNeeded(PlayerControllerB player, bool force)
        {
            if (player == null)
                return;

            UpdateLocalSupportState(player);
            if (!force && Time.time < _nextStateBroadcastTime)
                return;

            int buddyTier = GetLocalBuddyTier(player);
            int rallyTier = Time.time < _localRallyExpiresAt ? RallyCallUpgrade.GetTier() : 0;
            if (buddyTier <= 0 && rallyTier <= 0)
                return;

            _nextStateBroadcastTime = Time.time + STATE_BROADCAST_INTERVAL;
            BroadcastSupportState((int)player.playerClientId, buddyTier, rallyTier, _localRallyExpiresAt, player.transform.position);
        }

        private static void UpdateLocalSupportState(PlayerControllerB player)
        {
            if (player == null)
                return;

            int playerId = (int)player.playerClientId;
            SupportState state = GetState(playerId);
            state.BuddyTier = GetLocalBuddyTier(player);
            state.RallyTier = Time.time < _localRallyExpiresAt ? RallyCallUpgrade.GetTier() : 0;
            state.RallyExpiresAt = _localRallyExpiresAt;
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
            float bestRally = 0f;
            int localId = (int)localPlayer.playerClientId;

            foreach (KeyValuePair<int, SupportState> pair in SupportStates)
            {
                SupportState state = pair.Value;
                if (!IsStateFresh(state))
                    continue;

                Vector3 providerPosition = ResolveProviderPosition(pair.Key, state);
                float distance = Vector3.Distance(localPlayer.transform.position, providerPosition);

                if (pair.Key != localId
                    && state.BuddyTier > 0
                    && CanUseProviderStateForBuddy(pair.Key)
                    && distance <= BuddySystemUpgrade.GetRange(state.BuddyTier))
                {
                    bestBuddy = Mathf.Max(bestBuddy, BuddySystemUpgrade.GetBuff(state.BuddyTier));
                }

                if (state.RallyTier > 0 && Time.time < state.RallyExpiresAt && distance <= RallyCallUpgrade.RANGE)
                    bestRally = Mathf.Max(bestRally, RallyCallUpgrade.GetSpeedBonus(state.RallyTier));
            }

            return bestBuddy + bestRally;
        }

        private static float GetSupportDamageReduction(PlayerControllerB localPlayer)
        {
            float bestBuddy = 0f;
            float bestRally = 0f;
            int localId = (int)localPlayer.playerClientId;

            foreach (KeyValuePair<int, SupportState> pair in SupportStates)
            {
                SupportState state = pair.Value;
                if (!IsStateFresh(state))
                    continue;

                Vector3 providerPosition = ResolveProviderPosition(pair.Key, state);
                float distance = Vector3.Distance(localPlayer.transform.position, providerPosition);

                if (pair.Key != localId
                    && state.BuddyTier > 0
                    && CanUseProviderStateForBuddy(pair.Key)
                    && distance <= BuddySystemUpgrade.GetRange(state.BuddyTier))
                {
                    bestBuddy = Mathf.Max(bestBuddy, BuddySystemUpgrade.GetBuff(state.BuddyTier));
                }

                if (state.RallyTier > 0 && Time.time < state.RallyExpiresAt && distance <= RallyCallUpgrade.RANGE)
                    bestRally = Mathf.Max(bestRally, RallyCallUpgrade.GetDamageReduction(state.RallyTier));
            }

            return Mathf.Clamp01(bestBuddy + bestRally);
        }

        private static void ApplySpeedBuff(PlayerControllerB player)
        {
            AppliedSupportBonus applied = GetAppliedBonus(player);
            float baseSpeed = player.movementSpeed - applied.MovementSpeedBonus;
            float speedBonus = GetSupportSpeedBonus(player);
            float nextBonus = Mathf.Max(0f, baseSpeed) * speedBonus;
            player.movementSpeed = Mathf.Max(0.1f, baseSpeed + nextBonus);
            applied.MovementSpeedBonus = nextBonus;
            ForemanAuraStatusHud.SetAuraActive(IsReceivingBuddyAura(player));
        }

        private static bool IsReceivingBuddyAura(PlayerControllerB localPlayer)
        {
            if (localPlayer == null || localPlayer.isPlayerDead)
                return false;

            int localId = (int)localPlayer.playerClientId;
            Vector3 localPosition = localPlayer.transform.position;

            foreach (KeyValuePair<int, SupportState> pair in SupportStates)
            {
                if (pair.Key == localId)
                    continue;

                SupportState state = pair.Value;
                if (!IsStateFresh(state) || state.BuddyTier <= 0)
                    continue;
                if (!CanUseProviderStateForBuddy(pair.Key))
                    continue;

                Vector3 providerPosition = ResolveProviderPosition(pair.Key, state);
                if (Vector3.Distance(localPosition, providerPosition) <= BuddySystemUpgrade.GetRange(state.BuddyTier))
                    return true;
            }

            return false;
        }

        private static AppliedSupportBonus GetAppliedBonus(PlayerControllerB player)
        {
            int key = player.GetInstanceID();
            if (!AppliedBonuses.TryGetValue(key, out AppliedSupportBonus bonus))
            {
                bonus = new AppliedSupportBonus();
                AppliedBonuses[key] = bonus;
            }

            return bonus;
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

        private static void BreakGhostGirlHaunting()
        {
            DressGirlAI[] girls = UnityEngine.Object.FindObjectsOfType<DressGirlAI>();
            for (int i = 0; i < girls.Length; i++)
            {
                DressGirlAI girl = girls[i];
                if (girl == null || girl.isEnemyDead || girl.hauntingPlayer == null)
                    continue;

                try
                {
                    girl.staringInHaunt = false;
                    girl.disappearingFromStare = true;
                    girl.moveTowardsDestination = false;
                    girl.targetPlayer = null;
                    girl.SwitchToBehaviourStateOnLocalClient(0);
                    girl.EnableEnemyMesh(enable: false, overrideDoNotSet: true, tamperWithMeshes: true);
                    if (girl.creatureVoice != null) girl.creatureVoice.Stop();
                    if (girl.creatureSFX != null) girl.creatureSFX.Stop();
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[Rally Call] Failed to break Ghost Girl haunting: {ex.Message}");
                }
            }
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
        private const int SUPPORT_STATE_BYTES = sizeof(int) * 3 + sizeof(float) * 4;

        private static void WriteSupportState(
            FastBufferWriter writer, int playerId, int buddyTier, int rallyTier, float rallyExpiresAt, Vector3 position)
        {
            writer.WriteValueSafe(playerId);
            writer.WriteValueSafe(buddyTier);
            writer.WriteValueSafe(rallyTier);
            writer.WriteValueSafe(rallyExpiresAt);
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
        private static void BroadcastSupportState(int playerId, int buddyTier, int rallyTier, float rallyExpiresAt, Vector3 position)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            FastBufferWriter writer = new FastBufferWriter(SUPPORT_STATE_BYTES, Allocator.Temp);
            try
            {
                WriteSupportState(writer, playerId, buddyTier, rallyTier, rallyExpiresAt, position);
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
        private static void RelaySupportState(
            ulong excludeClientId, int playerId, int buddyTier, int rallyTier, float rallyExpiresAt, Vector3 position)
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
                    WriteSupportState(writer, playerId, buddyTier, rallyTier, rallyExpiresAt, position);
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
                int rallyTier;
                float rallyExpiresAt;
                float x, y, z;
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out buddyTier);
                reader.ReadValueSafe(out rallyTier);
                reader.ReadValueSafe(out rallyExpiresAt);
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
                state.RallyTier = rallyTier;
                state.RallyExpiresAt = rallyExpiresAt;
                state.ExpiresAt = Time.time + STATE_EXPIRY_SECONDS;
                state.Position = new Vector3(x, y, z);

                if (relaying)
                    RelaySupportState(senderClientId, playerId, buddyTier, rallyTier, rallyExpiresAt, new Vector3(x, y, z));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ForemanSupportPatch: malformed support sync: {ex.Message}");
            }
        }
#pragma warning restore Harmony003
    }
}
