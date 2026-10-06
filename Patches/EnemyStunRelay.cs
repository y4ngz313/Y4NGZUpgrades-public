using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// The shared client-to-host enemy stagger relay. Pumping Iron's melee stagger (#364) is its
    /// caller; it outlived Resilience's retired countershock, which used to own it (#435).
    /// </summary>
    [HarmonyPatch]
    internal static class EnemyStunRelay
    {
        // Host-relayed stagger request. Named-message plumbing mirrors EnemyStatusEffects. The
        // wire name predates the rename and stays as it is, so a peer on an older build still
        // reaches this handler with the same payload.
        private const string MSG_STUN_REQUEST = "Y4NGZ_Retaliation_Stun";
        private const int STUN_PAYLOAD_BYTES = sizeof(ulong) + sizeof(float);

        // Longest stagger the host will honour from a client request, whichever upgrade asked
        // for it, so one hostile payload cannot freeze an enemy indefinitely.
        internal const float MAX_RELAYED_STUN_SECONDS = 1.0f;

        private static bool _handlersRegistered;
        private static NetworkManager _registeredNetworkManager;

        /// <summary>
        /// Applies the stagger locally for immediate feedback and, off-host, asks the host
        /// to apply it too. EnemyAI runs its behaviour on the host, so a client-only
        /// SetEnemyStunned changes nothing about the enemy's actual movement -- everyone
        /// else (including the enemy's own AI) would keep going.
        ///
        /// Limitation: there is no vanilla broadcast for "this enemy is stunned", so remote
        /// non-host clients still don't get the local stun flag. That only affects
        /// client-side reads of stunNormalizedTimer; the AI interrupt itself is applied
        /// where it matters. Verify in a live lobby before treating the stagger as reliable.
        /// </summary>
        internal static void StunEnemy(EnemyAI enemy, float seconds, PlayerControllerB source)
        {
            if (enemy == null)
                return;

            enemy.SetEnemyStunned(true, seconds, source);

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsListening || network.IsServer) return;

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            FastBufferWriter writer = new FastBufferWriter(STUN_PAYLOAD_BYTES, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(enemy.NetworkObjectId);
                writer.WriteValueSafe(seconds);
                messaging.SendNamedMessage(MSG_STUN_REQUEST, NetworkManager.ServerClientId, writer);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[StunRelay] stun relay failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>Host-side handler: apply a client's requested stagger authoritatively.</summary>
        private static void OnStunRequest(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                NetworkManager network = NetworkManager.Singleton;
                if (network == null || !network.IsServer) return;

                ulong enemyNetworkObjectId;
                float seconds;
                reader.ReadValueSafe(out enemyNetworkObjectId);
                reader.ReadValueSafe(out seconds);

                // The duration is attacker-supplied; clamp it to the sanctioned value so a
                // malformed or hostile payload can't freeze an enemy indefinitely.
                seconds = Mathf.Clamp(seconds, 0f, MAX_RELAYED_STUN_SECONDS);
                if (seconds <= 0f) return;

                EnemyAI enemy = ResolveEnemy(enemyNetworkObjectId);
                if (enemy == null || enemy.isEnemyDead) return;

                enemy.SetEnemyStunned(true, seconds, ResolvePlayerByActualClientId(senderClientId));
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[StunRelay] malformed stun request: {ex.Message}");
            }
        }

        /// <summary>
        /// F-ENF-13: netcode drops the CustomMessagingManager on shutdown and builds a fresh one for
        /// the next host/join, so a bare `if (_handlersRegistered) return;` latch leaves a second
        /// lobby deaf on any path that does not route through GameNetworkManager.Disconnect (host
        /// migration, lobby recycle). Key the latch on the NetworkManager instance instead, exactly
        /// as UpgradeTierSync.RegisterNetworkHandlers does.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null) return;

            if (_handlersRegistered && ReferenceEquals(_registeredNetworkManager, network)) return;

            try
            {
                network.CustomMessagingManager.RegisterNamedMessageHandler(MSG_STUN_REQUEST, OnStunRequest);
                _handlersRegistered = true;
                _registeredNetworkManager = network;
            }
            catch (System.Exception ex)
            {
                _handlersRegistered = false;
                _registeredNetworkManager = null;
                Plugin.Log?.LogWarning($"[StunRelay] RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _handlersRegistered = false;
            _registeredNetworkManager = null;
        }

        private static EnemyAI ResolveEnemy(ulong networkObjectId)
        {
            NetworkSpawnManager spawnManager = NetworkManager.Singleton?.SpawnManager;
            if (spawnManager == null) return null;

            NetworkObject netObj;
            if (!spawnManager.SpawnedObjects.TryGetValue(networkObjectId, out netObj)) return null;
            if (netObj == null) return null;

            EnemyAI enemy = netObj.GetComponent<EnemyAI>();
            if (enemy != null) return enemy;

            enemy = netObj.GetComponentInChildren<EnemyAI>();
            if (enemy != null) return enemy;

            return netObj.GetComponentInParent<EnemyAI>();
        }

        private static PlayerControllerB ResolvePlayerByActualClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null) return null;

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].actualClientId == clientId)
                    return players[i];
            }

            return null;
        }
    }
}
