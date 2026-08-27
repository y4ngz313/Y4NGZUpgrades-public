using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    internal static class EnemyStatusEffects
    {
        private const string MSG_BURN_START = "Y4NGZ_Status_BurnStart";

        private static bool _handlersRegistered;

        internal static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered) return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                    MSG_BURN_START, OnReceiveBurnStart);
                _handlersRegistered = true;
                LogBurnDebug("Registered burn sync network handler.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[EnemyStatusEffects] RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Netcode nulls the CustomMessagingManager on shutdown and builds a fresh one for the next
        /// host/join, so the latch has to drop between lobbies or burn sync never re-registers.
        /// </summary>
        internal static void ResetNetworkHandlers()
        {
            _handlersRegistered = false;
        }

        internal static void ApplyBurning(
            EnemyAI enemy,
            PlayerControllerB sourcePlayer = null,
            float duration = -1f,
            int damagePerTick = -1,
            float tickInterval = -1f)
        {
            if (Plugin.BurningStatusEnabled?.Value == false) return;
            if (enemy == null || enemy.isEnemyDead) return;

            float resolvedDuration = duration > 0f
                ? duration
                : Mathf.Max(0.1f, (Plugin.BurningDuration?.Value ?? 3f));
            int resolvedDamage = damagePerTick >= 0
                ? damagePerTick
                : Mathf.Max(0, (Plugin.BurningDamagePerTick?.Value ?? 1));
            float resolvedTickInterval = tickInterval > 0f
                ? tickInterval
                : Mathf.Max(0.1f, (Plugin.BurningTickInterval?.Value ?? 1f));
            int sourcePlayerId = sourcePlayer != null ? (int)sourcePlayer.playerClientId : -1;

            LogBurnDebug(
                $"ApplyBurning enemy='{enemy.gameObject.name}' netId={enemy.NetworkObjectId} sourcePlayerId={sourcePlayerId} " +
                $"duration={resolvedDuration:0.00} damagePerTick={resolvedDamage} tickInterval={resolvedTickInterval:0.00}.");

            StartBurnLocal(enemy, sourcePlayerId, resolvedDuration, resolvedDamage, resolvedTickInterval);
            BroadcastBurnStart(enemy.NetworkObjectId, sourcePlayerId, resolvedDuration, resolvedDamage, resolvedTickInterval);
        }

        private static void StartBurnLocal(
            EnemyAI enemy,
            int sourcePlayerId,
            float duration,
            int damagePerTick,
            float tickInterval)
        {
            if (enemy == null || enemy.isEnemyDead) return;

            BurningEnemyEffect effect = FindActiveBurningEffect(enemy);
            if (effect == null)
            {
                GameObject go = new GameObject("Y4NGZ_BurningEnemy");
                go.transform.SetParent(enemy.transform, worldPositionStays: false);
                effect = go.AddComponent<BurningEnemyEffect>();
                LogBurnDebug($"Created local burn effect object for enemy='{enemy.gameObject.name}' netId={enemy.NetworkObjectId}.");
            }
            else
            {
                LogBurnDebug($"Refreshing existing burn effect for enemy='{enemy.gameObject.name}' netId={enemy.NetworkObjectId}.");
            }

            effect.Refresh(enemy, sourcePlayerId, duration, damagePerTick, tickInterval);
        }

        private static BurningEnemyEffect FindActiveBurningEffect(EnemyAI enemy)
        {
            BurningEnemyEffect[] effects = enemy.GetComponentsInChildren<BurningEnemyEffect>(true);
            for (int i = 0; i < effects.Length; i++)
            {
                BurningEnemyEffect effect = effects[i];
                if (effect == null || effect.IsStopping) continue;
                return effect;
            }

            return null;
        }

        private const int BURN_PAYLOAD_BYTES = sizeof(ulong) + sizeof(int) + sizeof(float) + sizeof(int) + sizeof(float);

        private static void WriteBurnStart(
            FastBufferWriter writer,
            ulong enemyNetworkObjectId,
            int sourcePlayerId,
            float duration,
            int damagePerTick,
            float tickInterval)
        {
            writer.WriteValueSafe(enemyNetworkObjectId);
            writer.WriteValueSafe(sourcePlayerId);
            writer.WriteValueSafe(duration);
            writer.WriteValueSafe(damagePerTick);
            writer.WriteValueSafe(tickInterval);
        }

        /// <summary>
        /// Netcode has no client-to-all primitive - SendNamedMessageToAll reads ConnectedClientsIds,
        /// whose getter throws off-host. This runs from an EnemyAI.HitEnemyOnLocalClient postfix, so
        /// an escaping exception would break the patch chain. The host broadcasts; a client sends to
        /// the host, which relays.
        /// </summary>
        private static void BroadcastBurnStart(
            ulong enemyNetworkObjectId,
            int sourcePlayerId,
            float duration,
            int damagePerTick,
            float tickInterval)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsListening) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            LogBurnDebug(
                $"BroadcastBurnStart enemyNetId={enemyNetworkObjectId} sourcePlayerId={sourcePlayerId} " +
                $"duration={duration:0.00} damagePerTick={damagePerTick} tickInterval={tickInterval:0.00}.");

            FastBufferWriter writer = new FastBufferWriter(BURN_PAYLOAD_BYTES, Allocator.Temp);
            try
            {
                WriteBurnStart(writer, enemyNetworkObjectId, sourcePlayerId, duration, damagePerTick, tickInterval);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_BURN_START, writer);
                else
                    messaging.SendNamedMessage(MSG_BURN_START, NetworkManager.ServerClientId, writer);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[EnemyStatusEffects] burn send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>Host-only re-broadcast of a client's burn to every other client.</summary>
        private static void RelayBurnStart(
            ulong excludeClientId,
            ulong enemyNetworkObjectId,
            int sourcePlayerId,
            float duration,
            int damagePerTick,
            float tickInterval)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(BURN_PAYLOAD_BYTES, Allocator.Temp);
                try
                {
                    WriteBurnStart(writer, enemyNetworkObjectId, sourcePlayerId, duration, damagePerTick, tickInterval);
                    messaging.SendNamedMessage(MSG_BURN_START, clientId, writer);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogWarning($"[EnemyStatusEffects] burn relay to {clientId} failed: {ex.Message}");
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

        private static void OnReceiveBurnStart(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                ulong enemyNetworkObjectId;
                int sourcePlayerId;
                float duration;
                int damagePerTick;
                float tickInterval;

                reader.ReadValueSafe(out enemyNetworkObjectId);
                reader.ReadValueSafe(out sourcePlayerId);
                reader.ReadValueSafe(out duration);
                reader.ReadValueSafe(out damagePerTick);
                reader.ReadValueSafe(out tickInterval);

                LogBurnDebug(
                    $"Received burn sync from client={senderClientId} enemyNetId={enemyNetworkObjectId} sourcePlayerId={sourcePlayerId} " +
                    $"duration={duration:0.00} damagePerTick={damagePerTick} tickInterval={tickInterval:0.00}.");

                NetworkManager network = NetworkManager.Singleton;
                if (network != null && network.IsServer && senderClientId != network.LocalClientId)
                {
                    // The source player id in the payload is attacker-controlled and is used for
                    // damage attribution, so the host overwrites it with the player behind the
                    // connection the message arrived on.
                    PlayerControllerB sender = ResolvePlayerByActualClientId(senderClientId);
                    if (sender == null)
                        return;

                    sourcePlayerId = (int)sender.playerClientId;
                    RelayBurnStart(
                        senderClientId, enemyNetworkObjectId, sourcePlayerId, duration, damagePerTick, tickInterval);
                }

                EnemyAI enemy = ResolveEnemy(enemyNetworkObjectId);
                StartBurnLocal(enemy, sourcePlayerId, duration, damagePerTick, tickInterval);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[EnemyStatusEffects] malformed burn sync: {ex.Message}");
            }
        }

        private static EnemyAI ResolveEnemy(ulong networkObjectId)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
                return null;

            NetworkObject netObj;
            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out netObj))
                return null;

            if (netObj == null) return null;

            EnemyAI enemy = netObj.GetComponent<EnemyAI>();
            if (enemy != null) return enemy;

            enemy = netObj.GetComponentInChildren<EnemyAI>();
            if (enemy != null) return enemy;

            return netObj.GetComponentInParent<EnemyAI>();
        }

        private static void LogBurnDebug(string message)
        {
            if (Plugin.BurningDebugLogging == null || !Plugin.BurningDebugLogging.Value) return;
            Plugin.Log?.LogInfo($"[BurnDebug] {message}");
        }
    }
}
