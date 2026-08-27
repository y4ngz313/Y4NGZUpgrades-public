using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Lucky8
{
    internal static class Lucky8Manager
    {
        private const string SpinRequestServer = "Y4NGZ.Lucky8.SpinRequest.Server";
        private const string SpinResultClient = "Y4NGZ.Lucky8.SpinResult.Client";
        private const string StateClient = "Y4NGZ.Lucky8.State.Client";
        private const string SnapshotRequestServer = "Y4NGZ.Lucky8.SnapshotRequest.Server";
        private const string CapsulePayloadClient = "Y4NGZ.Lucky8.CapsulePayload.Client";
        private const string CapsuleConsumeServer = "Y4NGZ.Lucky8.CapsuleConsume.Server";
        private const string AssignmentRequestServer = "Y4NGZ.Lucky8.AssignmentRequest.Server";
        private const string AssignmentClient = "Y4NGZ.Lucky8.Assignment.Client";

        // AssignmentClient wire layout. Written by BroadcastAssignments, read back by
        // OnAssignmentClient — keep the two in sync when adding a field.
        // Header: enabled(bool) + spinCost(int) + holdDuration(float) + pressDuration(float) + count(int).
        private const int AssignmentHeaderBytes = sizeof(bool) + sizeof(int) + sizeof(float) * 2 + sizeof(int);
        // Per route: routeIndex(int) + levelId(int).
        private const int AssignmentRouteBytes = sizeof(int) * 2;

        private static readonly HashSet<int> AssignedRoutes = new HashSet<int>();
        private static readonly Dictionary<int, int> AssignedLevelIds = new Dictionary<int, int>();
        private static readonly Dictionary<ulong, Lucky8MachineState> States = new Dictionary<ulong, Lucky8MachineState>();
        private static readonly Dictionary<ulong, Lucky8MachineBehaviour> Machines = new Dictionary<ulong, Lucky8MachineBehaviour>();
        private static readonly Dictionary<ulong, Lucky8ClaimCapsule> Capsules = new Dictionary<ulong, Lucky8ClaimCapsule>();
        private static readonly Dictionary<ulong, Lucky8RewardDefinition> CapsulePayloads = new Dictionary<ulong, Lucky8RewardDefinition>();
        private static readonly Dictionary<ulong, Lucky8RewardDefinition> PendingCapsulePayloads = new Dictionary<ulong, Lucky8RewardDefinition>();
        private static readonly HashSet<string> PendingTransactions = new HashSet<string>(StringComparer.Ordinal);

        private static NetworkManager registeredNetwork;
        private static string assignmentSignature = string.Empty;
        private static string assignmentInputSignature = string.Empty;
        private static float nextAssignmentRefresh;
        /// <summary>Valid pool results remain cached until a cheap fingerprint of the
        /// underlying config and registries changes. Invalid results retry periodically so
        /// late-loading dependencies can recover without rebuilding every pool forever.</summary>
        private const float InvalidPoolValidationRetrySeconds = 30f;
        private static float _nextInvalidPoolValidationAt;
        private static string _validatedPoolSignature = string.Empty;
        private static bool _cachedPoolsValid;
        private static bool _poolValidityKnown;
        private static bool spawnAttempted;
        private static bool placementPending;
        private static bool poolWarningLogged;
        private static bool initialized;
        private static int visitEpoch;
        private static bool hasHostAssignments;
        private static bool hostFeatureEnabled;
        private static int hostSpinCost = 3;
        private static float hostHoldDuration = 0.75f;
        private static float hostPressDuration = 1f;

        internal static Lucky8Config Config { get; private set; }
        internal static bool IsInitialized => initialized;
        internal static int EffectiveSpinCost => UseHostConfiguration ? hostSpinCost : Config.SpinCost;
        internal static float EffectiveHoldDuration => UseHostConfiguration ? hostHoldDuration : Config.HoldDuration;
        internal static float EffectivePressDuration => UseHostConfiguration ? hostPressDuration : Config.PressDuration;
        private static bool UseHostConfiguration => NetworkManager.Singleton is NetworkManager network
            && network.IsListening && network.IsClient && !network.IsServer && hasHostAssignments;

        internal static void Initialize(Y4NGZUpgrades.Config.Y4NGZConfigScope config)
        {
            if (initialized) return;
            Config = new Lucky8Config(config);
            if (!Config.FeatureEnabled)
            {
                Plugin.Log?.LogMessage(
                    "[LUCKY-8] Disabled; skipping runtime assets, Company bridge, animation, and network initialization.");
                return;
            }
            hostSpinCost = Config.SpinCost;
            hostHoldDuration = Config.HoldDuration;
            hostPressDuration = Config.PressDuration;
            Lucky8RuntimeAssets.Initialize();
            Lucky8CompanyBridge.Initialize();
            Lucky8ButtonPressAnimation.Initialize();
            initialized = true;
        }

        internal static void Shutdown()
        {
            if (!initialized) return;
            Lucky8CompanyBridge.Shutdown();
            UnregisterNetworkHandlers();
            foreach (string transaction in PendingTransactions.ToArray())
                ProgressionApi.CancelTokenSpend(transaction);
            PendingTransactions.Clear();
            States.Clear();
            Machines.Clear();
            Capsules.Clear();
            CapsulePayloads.Clear();
            PendingCapsulePayloads.Clear();
            AssignedRoutes.Clear();
            AssignedLevelIds.Clear();
            assignmentSignature = string.Empty;
            assignmentInputSignature = string.Empty;
            nextAssignmentRefresh = 0f;
            _validatedPoolSignature = string.Empty;
            _nextInvalidPoolValidationAt = 0f;
            _cachedPoolsValid = false;
            _poolValidityKnown = false;
            hasHostAssignments = false;
            hostFeatureEnabled = false;
            Lucky8RuntimeAssets.Shutdown();
            initialized = false;
        }

        internal static bool IsRouteAssigned(int routeIndex)
        {
            RefreshAssignments();
            NetworkManager network = NetworkManager.Singleton;
            bool enabled = network != null && network.IsListening && network.IsClient && !network.IsServer
                ? hasHostAssignments && hostFeatureEnabled
                : Config?.FeatureEnabled == true;
            return enabled && AssignedRoutes.Contains(routeIndex);
        }

        internal static void UpdateRuntime()
        {
            if (!initialized) return;
            RegisterNetworkHandlers();
            RefreshAssignments();
            Lucky8RuntimeAssets.TryRegisterCapsulePrefab();

            StartOfRound start = StartOfRound.Instance;
            RoundManager round = RoundManager.Instance;
            if (start == null || round == null) return;
            if (start.inShipPhase || start.shipIsLeaving)
            {
                if (spawnAttempted || placementPending || States.Count > 0)
                    ResetVisit();
                return;
            }
            if (!round.IsServer || Config?.FeatureEnabled != true) return;
            if (spawnAttempted || placementPending || !round.bakedNavMesh || !round.dungeonFinishedGeneratingForAllPlayers)
                return;
            if (round.dungeonGenerator?.Generator?.CurrentDungeon?.AllTiles == null)
                return;
            if (!IsCurrentMoonAssigned())
            {
                spawnAttempted = true;
                return;
            }
            if (!Lucky8RewardCatalog.HasMinimumPools(out string reason))
            {
                spawnAttempted = true;
                if (!poolWarningLogged)
                {
                    poolWarningLogged = true;
                    Plugin.Log?.LogWarning("[LUCKY-8] Assigned machine suppressed because the configured pools are incomplete: " + reason);
                }
                return;
            }

            int requestEpoch = visitEpoch;
            placementPending = Lucky8CompanyBridge.RequestPlacement((ok, position, rotation, interactionPoint, tier) =>
            {
                if (requestEpoch != visitEpoch) return;
                OnPlacementReady(ok, position, rotation, interactionPoint, tier);
            });
            if (!placementPending)
            {
                spawnAttempted = true;
                Plugin.Log?.LogWarning("[LUCKY-8] Facility placement API did not accept the machine request.");
            }
        }

        internal static void RegisterMachine(Lucky8MachineBehaviour machine)
        {
            NetworkObject networkObject = machine?.GetComponent<NetworkObject>();
            if (networkObject == null || !networkObject.IsSpawned) return;
            Machines[networkObject.NetworkObjectId] = machine;
        }

        internal static void UnregisterMachine(Lucky8MachineBehaviour machine)
        {
            NetworkObject networkObject = machine?.GetComponent<NetworkObject>();
            if (networkObject != null) Machines.Remove(networkObject.NetworkObjectId);
        }

        internal static void RegisterCapsule(Lucky8ClaimCapsule capsule)
        {
            NetworkObject networkObject = capsule?.GetComponent<NetworkObject>();
            if (networkObject == null || !networkObject.IsSpawned) return;
            Capsules[networkObject.NetworkObjectId] = capsule;
            if (PendingCapsulePayloads.TryGetValue(networkObject.NetworkObjectId, out Lucky8RewardDefinition reward))
            {
                capsule.SetReward(reward);
                PendingCapsulePayloads.Remove(networkObject.NetworkObjectId);
            }
        }

        internal static void UnregisterCapsule(Lucky8ClaimCapsule capsule)
        {
            NetworkObject networkObject = capsule?.GetComponent<NetworkObject>();
            if (networkObject != null) Capsules.Remove(networkObject.NetworkObjectId);
        }

        internal static bool TryGetState(ulong networkObjectId, out Lucky8MachineState state)
        {
            return States.TryGetValue(networkObjectId, out state);
        }

        internal static void TryBeginLocalSpin(ulong machineId, PlayerControllerB player)
        {
            if (!States.TryGetValue(machineId, out Lucky8MachineState state) || state.IsSpinning || state.SpinsRemaining <= 0)
                return;
            int cost = EffectiveSpinCost;
            ulong clientId = player.actualClientId;
            string transaction = $"lucky8:{clientId}:{BuildDaySignature()}:{Guid.NewGuid():N}";
            if (!ProgressionApi.TryReserveTokenSpend(transaction, cost))
            {
                HUDManager.Instance?.DisplayTip("LUCKY-8", $"This spin needs {cost} tokens. You have {ProgressionApi.AvailableTokens} available.", true);
                return;
            }

            PendingTransactions.Add(transaction);
            Lucky8ButtonPressAnimation.Play(player);
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient)
            {
                RejectLocalTransaction(transaction, "network-unavailable");
                return;
            }
            if (network.IsServer)
            {
                HandleSpinRequest(clientId, machineId, transaction, cost);
                return;
            }

            using var writer = new FastBufferWriter(512, Allocator.Temp);
            writer.WriteValueSafe(machineId);
            writer.WriteValueSafe(new FixedString128Bytes(transaction));
            writer.WriteValueSafe(cost);
            network.CustomMessagingManager.SendNamedMessage(SpinRequestServer, 0uL, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        internal static void RequestConsumeCapsule(ulong capsuleId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            if (network.IsServer)
            {
                ConsumeCapsule(capsuleId, network.LocalClientId);
                return;
            }
            using var writer = new FastBufferWriter(sizeof(ulong), Allocator.Temp);
            writer.WriteValueSafe(capsuleId);
            network.CustomMessagingManager.SendNamedMessage(CapsuleConsumeServer, 0uL, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        internal static void ResetVisit()
        {
            visitEpoch++;
            spawnAttempted = false;
            placementPending = false;
            poolWarningLogged = false;
            States.Clear();
            Machines.Clear();
            Capsules.Clear();
            CapsulePayloads.Clear();
            PendingCapsulePayloads.Clear();
        }

        private static void OnPlacementReady(bool ok, Vector3 position, Quaternion rotation, Vector3 interactionPoint, string tier)
        {
            placementPending = false;
            spawnAttempted = true;
            NetworkManager network = NetworkManager.Singleton;
            StartOfRound start = StartOfRound.Instance;
            if (!ok || network == null || !network.IsServer || Lucky8RuntimeAssets.MachinePrefab == null
                || start == null || start.inShipPhase || start.shipIsLeaving || !IsCurrentMoonAssigned())
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Placement failed: " + tier);
                return;
            }

            IReadOnlyList<Lucky8RewardDefinition> weapons = Lucky8RewardCatalog.BuildWeapons();
            IReadOnlyList<Lucky8RewardDefinition> suits = Lucky8RewardCatalog.BuildSuits();
            IReadOnlyList<Lucky8RewardDefinition> cosmetics = Lucky8RewardCatalog.BuildCosmetics();
            IReadOnlyList<Lucky8RewardDefinition> ammo;
            IReadOnlyList<Lucky8RewardDefinition> emotes;
            try
            {
                ammo = Lucky8RewardCatalog.BuildAmmo();
            }
            catch (Exception e)
            {
                ammo = Array.Empty<Lucky8RewardDefinition>();
                Plugin.Log?.LogWarning("[LUCKY-8] Ammo bonus pool failed safely: " + e.Message);
            }
            emotes = Lucky8RewardCatalog.BuildEmotes();
            int seed = StableHash(BuildDaySignature() + ":lineup:" + StartOfRound.Instance.currentLevel.levelID);
            if (!Lucky8LineupGenerator.TryCreate(weapons, suits, cosmetics, ammo, emotes, seed, out Lucky8RewardDefinition[] lineup, out string reason))
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Lineup generation failed: " + reason);
                return;
            }

            GameObject instance = UnityEngine.Object.Instantiate(Lucky8RuntimeAssets.MachinePrefab, position, rotation);
            instance.SetActive(true);
            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                UnityEngine.Object.Destroy(instance);
                return;
            }
            networkObject.Spawn();
            var state = new Lucky8MachineState
            {
                NetworkObjectId = networkObject.NetworkObjectId,
                SpinsRemaining = Config.SpinLimit,
                SpinDuration = Config.WheelDuration,
            };
            for (int i = 0; i < lineup.Length; i++) state.Rewards[i] = lineup[i];
            States[state.NetworkObjectId] = state;
            BroadcastState(state);
            LogDebug($"spawned id={state.NetworkObjectId} tier={tier} lineup={string.Join(",", lineup.Select(reward => reward.StableId))}");
        }

        private static void HandleSpinRequest(ulong senderClientId, ulong machineId, string transaction, int cost)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer) return;
            if (!States.TryGetValue(machineId, out Lucky8MachineState state))
            {
                SendSpinResult(senderClientId, transaction, false, "machine-not-found");
                return;
            }
            if (cost != Config.SpinCost || state.IsSpinning || state.SpinsRemaining <= 0 || state.SoldMask == byte.MaxValue)
            {
                SendSpinResult(senderClientId, transaction, false, state.IsSpinning ? "machine-busy" : "machine-depleted");
                return;
            }

            int seed = StableHash(transaction + ":winner:" + state.SpinsRemaining + ":" + state.SoldMask);
            int winner = Lucky8LineupGenerator.PickWinner(state, seed);
            if (winner < 0)
            {
                SendSpinResult(senderClientId, transaction, false, "no-prizes-left");
                return;
            }

            state.IsSpinning = true;
            state.WinnerIndex = winner;
            state.SpinStartedAt = network.ServerTime.Time + 0.15d;
            state.SpinDuration = Config.WheelDuration;
            state.SpinsRemaining--;
            SendSpinResult(senderClientId, transaction, true, "accepted");
            BroadcastState(state);
            Y4NGZPersistentRunner.Run(FinishSpin(state.NetworkObjectId, winner));
            LogDebug($"spin accepted machine={machineId} client={senderClientId} winner={winner} tx={transaction}");
        }

        private static IEnumerator FinishSpin(ulong machineId, int winner)
        {
            yield return new WaitForSecondsRealtime(Config.WheelDuration + 0.18f);
            if (!States.TryGetValue(machineId, out Lucky8MachineState state) || !state.IsSpinning || state.WinnerIndex != winner)
                yield break;
            state.IsSpinning = false;
            state.SoldMask = (byte)(state.SoldMask | (1 << winner));
            BroadcastState(state);
            Payout(state, winner);
        }

        private static void Payout(Lucky8MachineState state, int winner)
        {
            Lucky8RewardDefinition reward = state.Rewards[winner];
            if (reward == null || !Machines.TryGetValue(state.NetworkObjectId, out Lucky8MachineBehaviour machine))
                return;
            Vector3 position = machine.PrizeChutePosition;
            // Weapon and ammo prefabs live in Better Armory, so both are spawned through the prize
            // bridge (#267). A null prefab means the plugin is absent or the id went stale; the
            // warning exists so a drawn prize cannot fail to arrive silently.
            if (reward.Category == Lucky8RewardCategory.Weapon)
            {
                GameObject weaponPrefab = ArmoryBridge.GetWeaponPrizePrefab(reward.StableId);
                if (weaponPrefab == null)
                {
                    Plugin.Log?.LogWarning(
                        $"[LUCKY-8] Weapon prize '{reward.StableId}' has no prefab; it was not delivered.");
                    return;
                }

                SpawnNetworkPrize(weaponPrefab, position, instance =>
                    ArmoryBridge.InitializeWeaponPrize(instance, reward.StableId));
                return;
            }

            if (reward.Category == Lucky8RewardCategory.Ammo)
            {
                GameObject ammoPrefab = ArmoryBridge.GetAmmoPrizePrefab(reward.StableId);
                if (ammoPrefab == null)
                {
                    Plugin.Log?.LogWarning(
                        $"[LUCKY-8] Ammo prize '{reward.StableId}' has no prefab; it was not delivered.");
                    return;
                }

                SpawnNetworkPrize(ammoPrefab, position);
                return;
            }

            if (!Lucky8RuntimeAssets.TryRegisterCapsulePrefab())
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Claim capsule was not registered; payout could not spawn.");
                return;
            }
            NetworkObject capsule = SpawnNetworkPrize(Lucky8RuntimeAssets.CapsulePrefab, position);
            if (capsule != null)
            {
                CapsulePayloads[capsule.NetworkObjectId] = reward;
                PendingCapsulePayloads[capsule.NetworkObjectId] = reward;
                if (capsule.GetComponent<Lucky8ClaimCapsule>() is Lucky8ClaimCapsule localCapsule)
                    localCapsule.SetReward(reward);
                BroadcastCapsulePayload(capsule.NetworkObjectId, reward);
            }
        }

        private static NetworkObject SpawnNetworkPrize(GameObject prefab, Vector3 position, Action<GameObject> configure = null)
        {
            if (prefab == null || NetworkManager.Singleton?.IsServer != true) return null;
            GameObject prize = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            prize.SetActive(true);
            configure?.Invoke(prize);
            NetworkObject networkObject = prize.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                UnityEngine.Object.Destroy(prize);
                return null;
            }
            networkObject.Spawn();
            return networkObject;
        }

        private static void RefreshAssignments()
        {
            if (Config == null || Time.unscaledTime < nextAssignmentRefresh) return;
            nextAssignmentRefresh = Time.unscaledTime + 1f;
            NetworkManager network = NetworkManager.Singleton;
            if (network != null && network.IsListening && network.IsClient && !network.IsServer)
            {
                if (!hasHostAssignments)
                {
                    AssignedRoutes.Clear();
                    AssignedLevelIds.Clear();
                }
                return;
            }
            if (Config.FeatureEnabled != true)
            {
                AssignedRoutes.Clear();
                AssignedLevelIds.Clear();
                assignmentInputSignature = string.Empty;
                if (network?.IsServer == true) BroadcastAssignments();
                return;
            }
            string inputSignature = BuildDaySignature()
                + ":" + Config.AssignmentChance.ToString("0.000")
                + ":" + Config.SpinCost
                + ":" + Config.HoldDuration.ToString("0.000")
                + ":" + Config.PressDuration.ToString("0.000")
                + ":p" + StableHash(Config.WeaponPool.Value + "|" + Config.SuitPool.Value + "|" + Config.CosmeticPool.Value + "|" + Config.AmmoPool.Value)
                + ":l" + (StartOfRound.Instance?.levels?.Length ?? 0);

            bool signatureChanged = inputSignature != assignmentInputSignature;
            string poolSignature = BuildPoolValidationSignature();
            bool poolInputsChanged = !string.Equals(
                poolSignature,
                _validatedPoolSignature,
                StringComparison.Ordinal);
            bool retryInvalid = _poolValidityKnown
                && !_cachedPoolsValid
                && Time.unscaledTime >= _nextInvalidPoolValidationAt;
            if (!_poolValidityKnown || poolInputsChanged || retryInvalid)
            {
                _cachedPoolsValid = Lucky8RewardCatalog.HasMinimumPools(out _);
                _poolValidityKnown = true;

                // Validation may finish registering weapon assets, so capture the resulting
                // source state rather than the pre-validation fingerprint.
                _validatedPoolSignature = BuildPoolValidationSignature();
                _nextInvalidPoolValidationAt = _cachedPoolsValid
                    ? float.PositiveInfinity
                    : Time.unscaledTime + InvalidPoolValidationRetrySeconds;
            }

            if (!_cachedPoolsValid)
            {
                AssignedRoutes.Clear();
                AssignedLevelIds.Clear();
                assignmentInputSignature = string.Empty;
                if (network?.IsServer == true) BroadcastAssignments();
                return;
            }

            if (!signatureChanged) return;
            IReadOnlyList<Lucky8RouteInfo> routes = Lucky8CompanyBridge.GetRoutes();
            if (routes.Count == 0) return;
            assignmentInputSignature = inputSignature;
            string signature = inputSignature + ":" + routes.Count;
            if (signature == assignmentSignature) return;
            assignmentSignature = signature;
            AssignedRoutes.Clear();
            AssignedLevelIds.Clear();

            foreach (IGrouping<string, Lucky8RouteInfo> constellation in routes
                .Where(route => route.HasFacility && !route.IsCompanyMoon)
                .GroupBy(route => route.ConstellationKey ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                Lucky8RouteInfo[] eligible = constellation.OrderBy(route => route.RouteIndex).ToArray();
                var random = new StableRandom(StableHash(signature + ":" + constellation.Key));
                if (random.NextDouble() >= Config.AssignmentChance) continue;
                Lucky8RouteInfo selected = eligible[random.Next(eligible.Length)];
                AssignedRoutes.Add(selected.RouteIndex);
                AssignedLevelIds[selected.RouteIndex] = selected.LevelId;
            }
            LogDebug("daily assignments=" + string.Join(",", AssignedRoutes.OrderBy(value => value)));
            if (network?.IsServer == true)
                BroadcastAssignments();
        }

        private static string BuildPoolValidationSignature()
        {
            string configuredPools = Config.WeaponPool.Value
                + "|" + Config.SuitPool.Value
                + "|" + Config.CosmeticPool.Value;
            return StableHash(configuredPools) + ":" + Lucky8RewardCatalog.MinimumPoolSourceFingerprint();
        }

        private static bool IsCurrentMoonAssigned()
        {
            int levelId = StartOfRound.Instance?.currentLevel?.levelID ?? int.MinValue;
            return AssignedLevelIds.Any(pair => pair.Value == levelId && AssignedRoutes.Contains(pair.Key));
        }

        private static string BuildDaySignature()
        {
            SaveKey.TryGetCurrentOrSlot(out string save);
            object time = TimeOfDay.Instance;
            int quota = ReadInt(time, "timesFulfilledQuota");
            int remaining = ReadInt(time, "daysUntilDeadline");
            int deadline = ReadInt(time, "deadlineDaysAmount");
            return (save ?? "unknown") + ":q" + quota + ":d" + remaining + ":m" + deadline;
        }

        private static int ReadInt(object instance, string name)
        {
            if (instance == null) return 0;
            Type type = instance.GetType();
            object value = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
                ?? type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
            try { return Convert.ToInt32(value); }
            catch { return 0; }
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }

        private static void RegisterNetworkHandlers()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsListening || network.CustomMessagingManager == null || registeredNetwork == network) return;
            UnregisterNetworkHandlers();
            try
            {
                if (network.IsServer)
                {
                    network.CustomMessagingManager.RegisterNamedMessageHandler(SpinRequestServer, OnSpinRequestServer);
                    network.CustomMessagingManager.RegisterNamedMessageHandler(SnapshotRequestServer, OnSnapshotRequestServer);
                    network.CustomMessagingManager.RegisterNamedMessageHandler(CapsuleConsumeServer, OnCapsuleConsumeServer);
                    network.CustomMessagingManager.RegisterNamedMessageHandler(AssignmentRequestServer, OnAssignmentRequestServer);
                }
                network.CustomMessagingManager.RegisterNamedMessageHandler(SpinResultClient, OnSpinResultClient);
                network.CustomMessagingManager.RegisterNamedMessageHandler(StateClient, OnStateClient);
                network.CustomMessagingManager.RegisterNamedMessageHandler(CapsulePayloadClient, OnCapsulePayloadClient);
                network.CustomMessagingManager.RegisterNamedMessageHandler(AssignmentClient, OnAssignmentClient);
                registeredNetwork = network;
                if (network.IsClient && !network.IsServer)
                {
                    using var writer = new FastBufferWriter(0, Allocator.Temp);
                    network.CustomMessagingManager.SendNamedMessage(SnapshotRequestServer, 0uL, writer, NetworkDelivery.ReliableFragmentedSequenced);
                    using var assignmentWriter = new FastBufferWriter(0, Allocator.Temp);
                    network.CustomMessagingManager.SendNamedMessage(AssignmentRequestServer, 0uL, assignmentWriter, NetworkDelivery.ReliableFragmentedSequenced);
                }
            }
            catch (Exception e)
            {
                registeredNetwork = null;
                Plugin.Log?.LogWarning("[LUCKY-8] Network handler registration failed: " + e.Message);
            }
        }

        private static void UnregisterNetworkHandlers()
        {
            if (registeredNetwork?.CustomMessagingManager == null)
            {
                registeredNetwork = null;
                return;
            }
            try
            {
                if (registeredNetwork.IsServer)
                {
                    registeredNetwork.CustomMessagingManager.UnregisterNamedMessageHandler(SpinRequestServer);
                    registeredNetwork.CustomMessagingManager.UnregisterNamedMessageHandler(SnapshotRequestServer);
                    registeredNetwork.CustomMessagingManager.UnregisterNamedMessageHandler(CapsuleConsumeServer);
                    registeredNetwork.CustomMessagingManager.UnregisterNamedMessageHandler(AssignmentRequestServer);
                }
                registeredNetwork.CustomMessagingManager.UnregisterNamedMessageHandler(SpinResultClient);
                registeredNetwork.CustomMessagingManager.UnregisterNamedMessageHandler(StateClient);
                registeredNetwork.CustomMessagingManager.UnregisterNamedMessageHandler(CapsulePayloadClient);
                registeredNetwork.CustomMessagingManager.UnregisterNamedMessageHandler(AssignmentClient);
            }
            catch { }
            registeredNetwork = null;
            hasHostAssignments = false;
            hostFeatureEnabled = false;
        }

        private static void BroadcastAssignments(ulong? onlyClient = null)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null || !network.IsServer) return;
            using var writer = new FastBufferWriter(
                AssignmentHeaderBytes + AssignedRoutes.Count * AssignmentRouteBytes, Allocator.Temp);
            writer.WriteValueSafe(Config?.FeatureEnabled == true);
            writer.WriteValueSafe(Config?.SpinCost ?? 3);
            writer.WriteValueSafe(Config?.HoldDuration ?? 0.75f);
            writer.WriteValueSafe(Config?.PressDuration ?? 1f);
            writer.WriteValueSafe(AssignedRoutes.Count);
            foreach (int routeIndex in AssignedRoutes.OrderBy(value => value))
            {
                writer.WriteValueSafe(routeIndex);
                writer.WriteValueSafe(AssignedLevelIds.TryGetValue(routeIndex, out int levelId) ? levelId : -1);
            }
            if (onlyClient.HasValue)
                network.CustomMessagingManager.SendNamedMessage(AssignmentClient, onlyClient.Value, writer, NetworkDelivery.ReliableFragmentedSequenced);
            else
                network.CustomMessagingManager.SendNamedMessageToAll(AssignmentClient, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        private static void OnAssignmentRequestServer(ulong sender, FastBufferReader reader)
        {
            RefreshAssignments();
            BroadcastAssignments(sender);
        }

        private static void OnAssignmentClient(ulong sender, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out bool enabled);
                reader.ReadValueSafe(out int spinCost);
                reader.ReadValueSafe(out float holdDuration);
                reader.ReadValueSafe(out float pressDuration);
                reader.ReadValueSafe(out int count);
                count = Mathf.Clamp(count, 0, 128);
                AssignedRoutes.Clear();
                AssignedLevelIds.Clear();
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out int routeIndex);
                    reader.ReadValueSafe(out int levelId);
                    AssignedRoutes.Add(routeIndex);
                    AssignedLevelIds[routeIndex] = levelId;
                }
                hostFeatureEnabled = enabled;
                hostSpinCost = Mathf.Clamp(spinCost, 1, 99);
                hostHoldDuration = Mathf.Clamp(holdDuration, 0.1f, 3f);
                hostPressDuration = Mathf.Clamp(pressDuration, 0.2f, 3f);
                hasHostAssignments = true;
                LogDebug("host assignments=" + string.Join(",", AssignedRoutes.OrderBy(value => value)));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Malformed assignment snapshot: " + e.Message);
            }
        }

        private static void OnSpinRequestServer(ulong sender, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong machineId);
                reader.ReadValueSafe(out FixedString128Bytes transaction);
                reader.ReadValueSafe(out int cost);
                HandleSpinRequest(sender, machineId, transaction.ToString(), cost);
            }
            catch (Exception e) { Plugin.Log?.LogWarning("[LUCKY-8] Malformed spin request: " + e.Message); }
        }

        private static void OnSpinResultClient(ulong sender, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out FixedString128Bytes transactionValue);
                reader.ReadValueSafe(out bool accepted);
                reader.ReadValueSafe(out FixedString128Bytes reasonValue);
                string transaction = transactionValue.ToString();
                if (!PendingTransactions.Remove(transaction)) return;
                if (accepted)
                {
                    if (!ProgressionApi.CommitTokenSpend(transaction))
                        HUDManager.Instance?.DisplayTip("LUCKY-8", "The accepted token transaction could not be committed.", true);
                }
                else
                {
                    ProgressionApi.CancelTokenSpend(transaction);
                    HUDManager.Instance?.DisplayTip("LUCKY-8", "Spin rejected: " + reasonValue, true);
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("[LUCKY-8] Malformed spin result: " + e.Message); }
        }

        private static void SendSpinResult(ulong clientId, string transaction, bool accepted, string reason)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null) return;
            if (network.IsHost && clientId == network.LocalClientId)
            {
                if (PendingTransactions.Remove(transaction))
                {
                    if (accepted) ProgressionApi.CommitTokenSpend(transaction);
                    else ProgressionApi.CancelTokenSpend(transaction);
                }
                return;
            }
            using var writer = new FastBufferWriter(512, Allocator.Temp);
            writer.WriteValueSafe(new FixedString128Bytes(transaction));
            writer.WriteValueSafe(accepted);
            writer.WriteValueSafe(new FixedString128Bytes(reason ?? string.Empty));
            network.CustomMessagingManager.SendNamedMessage(SpinResultClient, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        private static void RejectLocalTransaction(string transaction, string reason)
        {
            PendingTransactions.Remove(transaction);
            ProgressionApi.CancelTokenSpend(transaction);
            HUDManager.Instance?.DisplayTip("LUCKY-8", "Spin rejected: " + reason, true);
        }

        private static void BroadcastState(Lucky8MachineState state, ulong? onlyClient = null)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null || state == null) return;
            using var writer = new FastBufferWriter(4096, Allocator.Temp);
            WriteState(writer, state);
            if (onlyClient.HasValue)
                network.CustomMessagingManager.SendNamedMessage(StateClient, onlyClient.Value, writer, NetworkDelivery.ReliableFragmentedSequenced);
            else
                network.CustomMessagingManager.SendNamedMessageToAll(StateClient, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        private static void WriteState(FastBufferWriter writer, Lucky8MachineState state)
        {
            writer.WriteValueSafe(state.NetworkObjectId);
            writer.WriteValueSafe(state.SoldMask);
            writer.WriteValueSafe(state.SpinsRemaining);
            writer.WriteValueSafe(state.IsSpinning);
            writer.WriteValueSafe(state.WinnerIndex);
            writer.WriteValueSafe(state.SpinStartedAt);
            writer.WriteValueSafe(state.SpinDuration);
            for (int i = 0; i < 8; i++)
            {
                Lucky8RewardDefinition reward = state.Rewards[i] ?? new Lucky8RewardDefinition();
                writer.WriteValueSafe((byte)reward.Category);
                writer.WriteValueSafe((byte)reward.Rarity);
                writer.WriteValueSafe(new FixedString128Bytes(reward.StableId ?? string.Empty));
                writer.WriteValueSafe(new FixedString128Bytes(reward.DisplayName ?? string.Empty));
            }
        }

        private static Lucky8MachineState ReadState(FastBufferReader reader)
        {
            var state = new Lucky8MachineState();
            reader.ReadValueSafe(out state.NetworkObjectId);
            reader.ReadValueSafe(out state.SoldMask);
            reader.ReadValueSafe(out state.SpinsRemaining);
            reader.ReadValueSafe(out state.IsSpinning);
            reader.ReadValueSafe(out state.WinnerIndex);
            reader.ReadValueSafe(out state.SpinStartedAt);
            reader.ReadValueSafe(out state.SpinDuration);
            for (int i = 0; i < 8; i++)
            {
                reader.ReadValueSafe(out byte category);
                reader.ReadValueSafe(out byte rarity);
                reader.ReadValueSafe(out FixedString128Bytes id);
                reader.ReadValueSafe(out FixedString128Bytes label);
                state.Rewards[i] = Lucky8RewardCatalog.Resolve((Lucky8RewardCategory)category, id.ToString(), label.ToString(), (Lucky8Rarity)rarity);
            }
            return state;
        }

        private static void OnStateClient(ulong sender, FastBufferReader reader)
        {
            try
            {
                Lucky8MachineState state = ReadState(reader);
                States[state.NetworkObjectId] = state;
            }
            catch (Exception e) { Plugin.Log?.LogWarning("[LUCKY-8] Malformed machine state: " + e.Message); }
        }

        private static void OnSnapshotRequestServer(ulong sender, FastBufferReader reader)
        {
            foreach (Lucky8MachineState state in States.Values) BroadcastState(state, sender);
            foreach (KeyValuePair<ulong, Lucky8RewardDefinition> payload in CapsulePayloads) BroadcastCapsulePayload(payload.Key, payload.Value, sender);
        }

        private static void BroadcastCapsulePayload(ulong capsuleId, Lucky8RewardDefinition reward, ulong? onlyClient = null)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null || reward == null) return;
            using var writer = new FastBufferWriter(512, Allocator.Temp);
            writer.WriteValueSafe(capsuleId);
            writer.WriteValueSafe((byte)reward.Category);
            writer.WriteValueSafe((byte)reward.Rarity);
            writer.WriteValueSafe(new FixedString128Bytes(reward.StableId ?? string.Empty));
            writer.WriteValueSafe(new FixedString128Bytes(reward.DisplayName ?? string.Empty));
            if (onlyClient.HasValue)
                network.CustomMessagingManager.SendNamedMessage(CapsulePayloadClient, onlyClient.Value, writer, NetworkDelivery.ReliableFragmentedSequenced);
            else
                network.CustomMessagingManager.SendNamedMessageToAll(CapsulePayloadClient, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        private static void OnCapsulePayloadClient(ulong sender, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong capsuleId);
                reader.ReadValueSafe(out byte category);
                reader.ReadValueSafe(out byte rarity);
                reader.ReadValueSafe(out FixedString128Bytes id);
                reader.ReadValueSafe(out FixedString128Bytes label);
                Lucky8RewardDefinition reward = Lucky8RewardCatalog.Resolve((Lucky8RewardCategory)category, id.ToString(), label.ToString(), (Lucky8Rarity)rarity);
                if (NetworkManager.Singleton?.IsServer == true)
                    CapsulePayloads[capsuleId] = reward;
                PendingCapsulePayloads[capsuleId] = reward;
                if (Capsules.TryGetValue(capsuleId, out Lucky8ClaimCapsule capsule))
                {
                    capsule.SetReward(reward);
                    PendingCapsulePayloads.Remove(capsuleId);
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("[LUCKY-8] Malformed capsule payload: " + e.Message); }
        }

        private static void OnCapsuleConsumeServer(ulong sender, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong capsuleId);
                ConsumeCapsule(capsuleId, sender);
            }
            catch { }
        }

        private static void ConsumeCapsule(ulong capsuleId, ulong requesterClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.IsServer != true) return;
            if (!CapsulePayloads.ContainsKey(capsuleId)) return;
            if (network.SpawnManager.SpawnedObjects.TryGetValue(capsuleId, out NetworkObject capsule))
            {
                if (capsule.GetComponent<Lucky8ClaimCapsule>() == null) return;
                GrabbableObject grabbable = capsule.GetComponent<GrabbableObject>();
                if (grabbable?.playerHeldBy == null || grabbable.playerHeldBy.actualClientId != requesterClientId) return;
                capsule.Despawn(true);
            }
            CapsulePayloads.Remove(capsuleId);
            PendingCapsulePayloads.Remove(capsuleId);
        }

        private static void LogDebug(string message)
        {
            if (Config?.DebugLogging?.Value == true) Plugin.Log?.LogInfo("[LUCKY-8] " + message);
        }
    }
}
