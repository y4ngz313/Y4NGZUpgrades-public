using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Upgrades;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Host-visible Shadow Step state for every player in the lobby.
    ///
    /// WHY THIS EXISTS (#362 / S1): enemy AI runs on its owning peer, often the host. The old implementation early-returned
    /// unless the detection result was <c>localPlayerController</c>, so a non-host client's crouch
    /// reduction and cloak were evaluated on a machine that does not own the AI - they did nothing.
    /// Every peer now publishes its own (tier, cloaked) pair and every peer keeps the full table, so
    /// the enemy owner can resolve the Shadow Step state of an arbitrary player.
    ///
    /// Stance (crouch / sprint) is deliberately NOT synced. <c>isCrouching</c> IS replicated by
    /// vanilla (<c>PlayerControllerB.Crouch</c> -> <c>SyncCrouchingClientRpc</c>), so the host reads
    /// it straight off the target <see cref="PlayerControllerB"/>. <c>isSprinting</c> is NOT
    /// replicated - every assignment lives in the owner-only branch of
    /// <c>PlayerControllerB.Update</c> - so sprint is read from the replicated animator tag for
    /// non-owners exactly as vanilla does (F-SHADOW-2).
    ///
    /// Wire shapes (all named messages, ReliableFragmentedSequenced):
    ///   SetServerRpc      client -> host   : byte tier, bool cloaked           (owner implied by sender)
    ///   SetClientRpc      host   -> all    : ulong clientId, byte tier, bool cloaked
    ///   RequestAllServer  client -> host   : (empty)
    ///   SyncAllClientRpc  host   -> caller : short count, then count * (ulong, byte, bool)
    ///
    /// The payload never carries a caller-chosen owner id: the host trusts only the connection a
    /// message arrived on, so a client can only ever cloak itself.
    ///
    /// Modeled on <see cref="UpgradeTierSync"/>. Shadow Step is kept out of that table because it
    /// must also carry the volatile cloak flag, which changes many times per round and must not be
    /// coupled to the purchase-time tier broadcast.
    /// </summary>
#pragma warning disable Harmony003 // Custom message serializers mutate FastBufferReader/FastBufferWriter by design.
    internal static class ShadowStepNetState
    {
        private const string MSG_SET_SERVER = "Y4NGZShadowStep.SetServerRpc";
        private const string MSG_SET_CLIENT = "Y4NGZShadowStep.SetClientRpc";
        private const string MSG_REQUEST_ALL_SERVER = "Y4NGZShadowStep.RequestAllServerRpc";
        private const string MSG_SYNC_ALL_CLIENT = "Y4NGZShadowStep.SyncAllClientRpc";

        private const byte MaxSyncedTier = 3;

        private struct Entry
        {
            internal byte Tier;
            internal bool Cloaked;
        }

        private static readonly Dictionary<ulong, Entry> StatesByClient = new Dictionary<ulong, Entry>();

        // F-SHADOW-4: CheckLineOfSightForPosition is one of the busiest AI calls in the game and is
        // used far more often for objects than for players. Keep a running cloak count so the
        // postfix can bail with a single int compare when nobody in the lobby is cloaked.
        private static int _cloakedCount;

        private static bool _handlersRegistered;
        private static NetworkManager _registeredNetworkManager;

        // - Queries -

        internal static bool AnyCloaked => _cloakedCount > 0;

        internal static int GetTier(PlayerControllerB player)
        {
            if (player == null)
                return 0;

            return StatesByClient.TryGetValue(player.actualClientId, out Entry entry) ? entry.Tier : 0;
        }

        internal static bool IsCloaked(PlayerControllerB player)
        {
            if (player == null)
                return false;

            return StatesByClient.TryGetValue(player.actualClientId, out Entry entry) && entry.Cloaked;
        }

        // - Lifecycle -

        internal static void RegisterNetworkHandlers()
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null)
                return;

            // Netcode builds a fresh CustomMessagingManager per host/join, so comparing the manager
            // instance (not a bare latch) re-registers exactly once per NetworkManager (#UpgradeTierSync).
            if (_handlersRegistered && ReferenceEquals(_registeredNetworkManager, network))
                return;
            if (_handlersRegistered)
                UnregisterNetworkHandlers();

            try
            {
                if (network.IsServer)
                {
                    messaging.RegisterNamedMessageHandler(MSG_SET_SERVER, OnSetStateServerRpc);
                    messaging.RegisterNamedMessageHandler(MSG_REQUEST_ALL_SERVER, OnRequestAllServerRpc);
                }

                messaging.RegisterNamedMessageHandler(MSG_SET_CLIENT, OnSetStateClientRpc);
                messaging.RegisterNamedMessageHandler(MSG_SYNC_ALL_CLIENT, OnSyncAllClientRpc);

                _handlersRegistered = true;
                _registeredNetworkManager = network;
                Plugin.Log?.LogInfo("[Shadow Step] network handlers registered.");
            }
            catch (Exception e)
            {
                _handlersRegistered = false;
                _registeredNetworkManager = null;
                Plugin.Log?.LogError($"[Shadow Step] handler registration failed: {e}");
            }
        }

        internal static void Clear()
        {
            StatesByClient.Clear();
            _cloakedCount = 0;
            UnregisterNetworkHandlers();
        }

        /// <summary>
        /// Ship reset drops the cached table but keeps the handlers alive, then re-publishes and
        /// re-requests so the table repopulates within a round trip.
        /// </summary>
        internal static void ResetForNewCycle()
        {
            StatesByClient.Clear();
            _cloakedCount = 0;
            RegisterNetworkHandlers();
            ShadowStepPatch.PublishLocalState(force: true);
            RequestAllFromServer();
        }

        /// <summary>Late joiner / reconnect resync (#362 / S4).</summary>
        internal static void RequestAllFromServer()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.CustomMessagingManager == null || network.IsServer)
                return;

            var writer = new FastBufferWriter(4, Allocator.Temp);
            try
            {
                network.CustomMessagingManager.SendNamedMessage(
                    MSG_REQUEST_ALL_SERVER, NetworkManager.ServerClientId, writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Shadow Step] RequestAllFromServer failed: {e.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        internal static void RemoveClient(ulong clientId)
        {
            if (StatesByClient.TryGetValue(clientId, out Entry entry) && entry.Cloaked)
                _cloakedCount--;
            StatesByClient.Remove(clientId);
        }

        // - Publish -

        internal static void PublishLocalState(byte tier, bool cloaked)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;

            if (local == null)
                return;

            // Always record locally so a single-player / pre-connect session still reads correctly.
            ApplyState(local.actualClientId, tier, cloaked, allowVisuals: false);

            if (network == null || messaging == null || !network.IsClient || !_handlersRegistered)
                return;

            try
            {
                if (network.IsServer)
                {
                    BroadcastState(local.actualClientId, tier, cloaked);
                }
                else
                {
                    // tier(1) + cloaked(1)
                    var writer = new FastBufferWriter(4, Allocator.Temp);
                    try
                    {
                        writer.WriteValueSafe(tier);
                        writer.WriteValueSafe(cloaked);
                        messaging.SendNamedMessage(
                            MSG_SET_SERVER, NetworkManager.ServerClientId, writer,
                            NetworkDelivery.ReliableFragmentedSequenced);
                    }
                    finally
                    {
                        writer.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Shadow Step] PublishLocalState failed: {e.Message}");
            }
        }

        private static void BroadcastState(ulong clientId, byte tier, bool cloaked)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || network.CustomMessagingManager == null)
                return;

            // clientId(8) + tier(1) + cloaked(1)
            var writer = new FastBufferWriter(16, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(clientId);
                writer.WriteValueSafe(tier);
                writer.WriteValueSafe(cloaked);
                network.CustomMessagingManager.SendNamedMessageToAll(
                    MSG_SET_CLIENT, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Shadow Step] BroadcastState failed: {e.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        // - Receive -

        private static void OnSetStateServerRpc(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return;

            try
            {
                reader.ReadValueSafe(out byte tier);
                reader.ReadValueSafe(out bool cloaked);
                if (tier > MaxSyncedTier)
                    return;

                // The owner is the connection, never a payload field: a client can only cloak itself.
                ApplyState(senderClientId, tier, cloaked, allowVisuals: true);
                BroadcastState(senderClientId, tier, cloaked);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Shadow Step] malformed SetServer message: {e.Message}");
            }
        }

        private static void OnSetStateClientRpc(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.IsServer || senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                reader.ReadValueSafe(out ulong clientId);
                reader.ReadValueSafe(out byte tier);
                reader.ReadValueSafe(out bool cloaked);
                if (tier > MaxSyncedTier)
                    return;

                ApplyState(clientId, tier, cloaked, allowVisuals: true);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Shadow Step] malformed SetClient message: {e.Message}");
            }
        }

        private static void OnRequestAllServerRpc(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || network.CustomMessagingManager == null)
                return;

            try
            {
                int count = StatesByClient.Count;
                // count(2) + per entry clientId(8) + tier(1) + cloaked(1)
                var writer = new FastBufferWriter(4 + count * 12, Allocator.Temp);
                try
                {
                    writer.WriteValueSafe((short)count);
                    foreach (KeyValuePair<ulong, Entry> kvp in StatesByClient)
                    {
                        writer.WriteValueSafe(kvp.Key);
                        writer.WriteValueSafe(kvp.Value.Tier);
                        writer.WriteValueSafe(kvp.Value.Cloaked);
                    }

                    network.CustomMessagingManager.SendNamedMessage(
                        MSG_SYNC_ALL_CLIENT, senderClientId, writer,
                        NetworkDelivery.ReliableFragmentedSequenced);
                }
                finally
                {
                    writer.Dispose();
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[Shadow Step] OnRequestAllServerRpc failed: {e}");
            }
        }

        private static void OnSyncAllClientRpc(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.IsServer || senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                reader.ReadValueSafe(out short count);
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out ulong clientId);
                    reader.ReadValueSafe(out byte tier);
                    reader.ReadValueSafe(out bool cloaked);
                    if (tier <= MaxSyncedTier)
                        ApplyState(clientId, tier, cloaked, allowVisuals: true);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Shadow Step] malformed SyncAll message: {e.Message}");
            }
        }

        private static void ApplyState(ulong clientId, byte tier, bool cloaked, bool allowVisuals)
        {
            StatesByClient.TryGetValue(clientId, out Entry previous);
            bool hadEntry = StatesByClient.ContainsKey(clientId);
            StatesByClient[clientId] = new Entry { Tier = tier, Cloaked = cloaked };

            bool becameCloaked = cloaked && (!hadEntry || !previous.Cloaked);
            bool becameVisible = !cloaked && hadEntry && previous.Cloaked;

            if (becameCloaked) _cloakedCount++;
            else if (becameVisible) _cloakedCount--;

            if (becameCloaked)
            {
                // De-aggro runs on each enemy's current owning peer.
                ShadowStepPatch.ForceDeaggroForPlayer(ResolvePlayer(clientId));
            }

            if (!allowVisuals || (!becameCloaked && !becameVisible))
                return;

            PlayerControllerB player = ResolvePlayer(clientId);
            if (player == null)
                return; // The reconcile pass in ShadowStepPatch picks this up once the object exists.

            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (local != null && player == local)
                return; // The owner drives its own visuals through StartCloakEffects/StopCloakEffects.

            CamoShellCloak.SetCloaked(player, cloaked);
            ShadowStepPatch.SpawnSmokePoof(player.transform.position, cloaked);
        }

        internal static PlayerControllerB ResolvePlayer(ulong actualClientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance != null
                ? StartOfRound.Instance.allPlayerScripts
                : null;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].actualClientId == actualClientId)
                    return players[i];
            }

            return null;
        }

        private static void UnregisterNetworkHandlers()
        {
            if (!_handlersRegistered)
                return;

            NetworkManager network = _registeredNetworkManager ?? NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (messaging != null)
            {
                try
                {
                    if (network.IsServer)
                    {
                        messaging.UnregisterNamedMessageHandler(MSG_SET_SERVER);
                        messaging.UnregisterNamedMessageHandler(MSG_REQUEST_ALL_SERVER);
                    }

                    messaging.UnregisterNamedMessageHandler(MSG_SET_CLIENT);
                    messaging.UnregisterNamedMessageHandler(MSG_SYNC_ALL_CLIENT);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"[Shadow Step] handler unregister failed: {e.Message}");
                }
            }

            _handlersRegistered = false;
            _registeredNetworkManager = null;
        }
    }
#pragma warning restore Harmony003

    [HarmonyPatch]
    internal static class ShadowStepPatch
    {

        private const float RemoteReconcileInterval = 0.25f;

        /// <summary>Tier that unlocks the final-second expiry cue and its vignette ramp (F-SHADOW-7).</summary>
        private const int EXPIRY_CUE_TIER = 3;

        private static byte _publishedTier = 255;
        private static bool _publishedCloak;
        private static bool _publishedOnce;
        private static float _nextRemoteReconcile;

        // F-SHADOW-17: the local tier used to be re-resolved (twice) per sight check per enemy.
        // Cache it and let Y4NGZUpgradeManager.UpgradesChanged invalidate it, the same way
        // UpgradeTierSync already refreshes off that event.
        private static int _localTier = -1;
        private static bool _tierHookInstalled;

        // --- Procedural audio clips (cached, generated once) ---
        private static AudioClip _activationClip;
        private static AudioClip _deactivationClip;
        private static AudioClip _breakClip;

        // --- Cloak effects state ---
        private static bool _audioVolumeDucked;
        private static AudioSource _hissSource;
        private static AudioClip _hissClip;
        private static bool _cloakEffectsActive = false;
        private static AudioLowPassFilter _cloakLowPassFilter;
        private static bool _cloakLowPassWasAdded = false;
        private static float _originalLowPassCutoff = 22000f;
        private static float _originalLowPassResonance = 1f;

        // F-SHADOW-5: the cloak used to deafen the player (850 Hz / 0.72 volume) and black out the
        // screen for its whole duration, during the exact window they need to hear a chase. The
        // penalty is now a tell, not a handicap: a light high-shelf roll-off and a faint vignette.
        private const float CLOAK_AUDIO_VOLUME_SCALE = 0.92f;
        private const float CLOAK_LOW_PASS_CUTOFF = 4000f;
        private const float CLOAK_LOW_PASS_RESONANCE = 1.15f;
        private const float CLOAK_VIGNETTE_BASE_ALPHA = 0.25f;

        // --- Cloak vignette ---
        private static Canvas _cloakOverlayCanvas;
        private static GameObject _cloakVignetteObj;
        private static RawImage _cloakVignette;
        private static Texture2D _vignetteTexture;

        private const float CooldownTipThrottleSeconds = 2f;
        private static float _nextCooldownTipAt;

        // -----------------------------------------------------------------------
        // PROCEDURAL AUDIO GENERATION
        // -----------------------------------------------------------------------

        private static AudioClip GetActivationClip()
        {
            if (_activationClip != null) return _activationClip;

            int sampleRate = 44100;
            float duration = 0.25f;
            int sampleCount = (int)(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float progress = t / duration;

                float freq = Mathf.Lerp(1200f, 200f, progress);
                float envelope = Mathf.Exp(-t * 6f);
                float sample = Mathf.Sin(2f * Mathf.PI * freq * t) * envelope * 1.5f;
                float noise = (UnityEngine.Random.Range(-1f, 1f)) * envelope * 0.3f;

                samples[i] = Mathf.Clamp(sample + noise, -1f, 1f);
            }

            _activationClip = AudioClip.Create("Y4NGZ_ShadowStep_PhaseOut", sampleCount, 1, sampleRate, false);
            bool setDataOk = _activationClip.SetData(samples, 0);
            Plugin.Log.LogInfo($"[Shadow Step] Generated activation clip: samples={sampleCount}, SetData={setDataOk}");
            return _activationClip;
        }

        private static AudioClip GetDeactivationClip()
        {
            if (_deactivationClip != null) return _deactivationClip;

            int sampleRate = 44100;
            float duration = 0.12f;
            int sampleCount = (int)(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float progress = t / duration;

                float freq = Mathf.Lerp(200f, 800f, progress);
                float envelope = Mathf.Exp(-t * 10f);

                samples[i] = Mathf.Clamp(Mathf.Sin(2f * Mathf.PI * freq * t) * envelope * 1.5f, -1f, 1f);
            }

            _deactivationClip = AudioClip.Create("Y4NGZ_ShadowStep_SnapOff", sampleCount, 1, sampleRate, false);
            bool setDataOk = _deactivationClip.SetData(samples, 0);
            Plugin.Log.LogInfo($"[Shadow Step] Generated deactivation clip: samples={sampleCount}, SetData={setDataOk}");
            return _deactivationClip;
        }

        /// <summary>
        /// F-SHADOW-13: a cloak that was broken by an action must not sound like one that simply ran
        /// out. This is a harsher, noisier downward chirp than <see cref="GetDeactivationClip"/>.
        /// </summary>
        private static AudioClip GetBreakClip()
        {
            if (_breakClip != null) return _breakClip;

            int sampleRate = 44100;
            float duration = 0.18f;
            int sampleCount = (int)(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float progress = t / duration;

                float freq = Mathf.Lerp(900f, 90f, progress * progress);
                float envelope = Mathf.Exp(-t * 9f);
                float square = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * freq * t));
                float noise = UnityEngine.Random.Range(-1f, 1f) * envelope * 0.55f;

                samples[i] = Mathf.Clamp(square * envelope * 0.9f + noise, -1f, 1f);
            }

            _breakClip = AudioClip.Create("Y4NGZ_ShadowStep_CloakBroken", sampleCount, 1, sampleRate, false);
            _breakClip.SetData(samples, 0);
            return _breakClip;
        }

        private static AudioClip GetHissClip()
        {
            if (_hissClip != null) return _hissClip;

            int sampleRate = 44100;
            float duration = 2f;
            int sampleCount = (int)(sampleRate * duration);
            float[] samples = new float[sampleCount];

            System.Random rng = new System.Random(42);
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.12f;
            }

            _hissClip = AudioClip.Create("Y4NGZ_ShadowStep_Hiss", sampleCount, 1, sampleRate, false);
            _hissClip.SetData(samples, 0);
            return _hissClip;
        }

        // -----------------------------------------------------------------------
        // 1) SHADOW STEP STATE RESOLUTION (ANY PLAYER, HOST-SAFE)
        // -----------------------------------------------------------------------

        private static PlayerControllerB LocalPlayer =>
            GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;

        private static bool IsLocal(PlayerControllerB player)
        {
            PlayerControllerB local = LocalPlayer;
            return local != null && player != null && player == local;
        }

        /// <summary>
        /// Cloak state for an ARBITRARY player. The local player answers from its own live flags
        /// (no network round trip, no one-frame lag); everyone else answers from the synced table.
        /// </summary>
        internal static bool IsPlayerCloaked(PlayerControllerB player)
        {
            if (player == null)
                return false;
            if (IsLocal(player))
                return IsAnyInvisibilityActive();

            return ShadowStepNetState.IsCloaked(player);
        }

        /// <summary>
        /// F-SHADOW-17: the local tier, resolved once and invalidated by the manager's own change
        /// event instead of two dictionary lookups per candidate per sight check.
        /// </summary>
        private static int LocalTier
        {
            get
            {
                if (!_tierHookInstalled)
                {
                    Y4NGZUpgradeManager.UpgradesChanged += InvalidateLocalTier;
                    _tierHookInstalled = true;
                }

                if (_localTier < 0)
                    _localTier = ShadowStepUpgrade.GetTier();
                return _localTier;
            }
        }

        private static void InvalidateLocalTier() => _localTier = -1;

        private static int GetTierFor(PlayerControllerB player)
        {
            if (player == null)
                return 0;
            if (IsLocal(player))
                return LocalTier;

            return ShadowStepNetState.GetTier(player);
        }

        /// <summary>
        /// F-SHADOW-2: vanilla replicates <c>isCrouching</c> but NOT <c>isSprinting</c> - every
        /// assignment sits in the owner-only branch of <c>PlayerControllerB.Update</c>. Vanilla
        /// itself works around this by reading the replicated animator tag on non-owners
        /// (<c>playerBodyAnimator.GetCurrentAnimatorStateInfo(0).IsTag("Sprinting")</c>); mirror it,
        /// or a remote client at tier 2/3 keeps its 25% reduction while sprinting on the host.
        /// </summary>
        private static bool IsSprinting(PlayerControllerB player)
        {
            if (player.IsOwner)
                return player.isSprinting;

            return player.playerBodyAnimator != null
                && player.playerBodyAnimator.GetCurrentAnimatorStateInfo(0).IsTag("Sprinting");
        }

        /// <summary>
        /// Detection range multiplier for <paramref name="player"/>. 1 = unchanged, 0 = undetectable.
        /// Crouch comes straight off the PlayerControllerB (vanilla replicates it); sprint goes
        /// through <see cref="IsSprinting"/> because vanilla does not replicate that flag.
        /// </summary>
        private static float GetDetectionMultiplierFor(PlayerControllerB player)
        {
            if (player == null || player.isPlayerDead || !player.isPlayerControlled)
                return 1f;
            if (IsPlayerCloaked(player))
                return 0f;

            switch (GetTierFor(player))
            {
                case 1:
                    return player.isCrouching ? ShadowStepUpgrade.REDUCED_DETECTION_MULTIPLIER : 1f;
                case 2:
                case 3:
                    return IsSprinting(player) ? 1f : ShadowStepUpgrade.REDUCED_DETECTION_MULTIPLIER;
                default:
                    return 1f;
            }
        }

        /// <summary>Distance the vanilla sight checks use: enemy eye to player camera.</summary>
        private static float DetectionDistance(EnemyAI enemy, PlayerControllerB player)
        {
            Transform eye = enemy.eye != null ? enemy.eye : enemy.transform;
            Transform target = player.gameplayCamera != null ? player.gameplayCamera.transform : player.transform;
            return Vector3.Distance(eye.position, target.position);
        }

        /// <summary>
        /// True when <paramref name="player"/> must be removed from a sight result: cloaked outright,
        /// or outside the reduced version of the check's own range. No magic baseline range exists
        /// any more (#362 / S9) - each check scales the range it was actually called with.
        /// </summary>
        private static bool ShouldDropFromSight(EnemyAI enemy, PlayerControllerB player, float baseRange)
        {
            float multiplier = GetDetectionMultiplierFor(player);
            if (multiplier >= 1f)
                return false;
            if (multiplier <= 0f)
                return true;
            if (baseRange <= 0f)
                return false;

            return DetectionDistance(enemy, player) >= SightRange(enemy, (int)baseRange) * multiplier;
        }

        // -----------------------------------------------------------------------
        // 2) DETECTION OVERRIDES (POSTFIX) - APPLY TO EVERY PLAYER, NOT JUST LOCAL
        // -----------------------------------------------------------------------

        private static int SightRange(EnemyAI enemy, int range)
        {
            return enemy.isOutside && enemy.enemyType != null && !enemy.enemyType.canSeeThroughFog
                && TimeOfDay.Instance != null && TimeOfDay.Instance.currentLevelWeather == LevelWeatherType.Foggy
                ? Mathf.Clamp(range, 0, 30) : range;
        }

        /// <summary>
        /// Byte-for-byte vanilla <c>CheckLineOfSightFor(Closest)Player</c> with one change: each
        /// candidate's range is scaled by its detection multiplier.
        ///
        /// F-SHADOW-6: this deliberately does NOT call <c>PlayerIsTargetable</c>. Vanilla's two
        /// sight methods iterate <c>allPlayerScripts</c> raw, so adding that gate would have
        /// silently hidden sinking, mineshaft-split and inside/outside-mismatched crewmates from
        /// every enemy on any peer where a Shadow Step owner exists - including players who own no
        /// Shadow Step at all.
        /// </summary>
        private static PlayerControllerB SelectVisiblePlayer(EnemyAI enemy, float width, int range,
            int proximityAwareness, bool closest, float bufferDistance)
        {
            var round = StartOfRound.Instance;
            if (round == null || enemy == null || enemy.eye == null) return null;
            int effectiveRange = SightRange(enemy, range);
            PlayerControllerB best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (var candidate in round.allPlayerScripts)
            {
                if (candidate == null || candidate.gameplayCamera == null) continue;
                float multiplier = GetDetectionMultiplierFor(candidate);
                if (multiplier <= 0f) continue;
                Vector3 position = candidate.gameplayCamera.transform.position;
                float distance = Vector3.Distance(enemy.eye.position, position);
                if (distance >= effectiveRange * multiplier) continue;
                if (Vector3.Angle(enemy.eye.forward, position - enemy.eye.position) >= width
                    && (proximityAwareness < 0 || distance >= proximityAwareness)) continue;
                int mask = round.collidersAndRoomMaskAndDefault;
                // F-SHADOW-6: the closest-player variant is the one that publishes raycastHit;
                // some AI reads it after the call, so write it exactly where vanilla does.
                if (closest)
                {
                    if (Physics.Linecast(enemy.eye.position, position, out enemy.raycastHit, mask, QueryTriggerInteraction.Ignore)
                        || Physics.Linecast(position, enemy.eye.position, out enemy.raycastHit, mask, QueryTriggerInteraction.Ignore)) continue;
                }
                else if (Physics.Linecast(enemy.eye.position, position, mask, QueryTriggerInteraction.Ignore)
                    || Physics.Linecast(position, enemy.eye.position, mask, QueryTriggerInteraction.Ignore)) continue;
                if (!closest) return candidate;
                if (distance < bestDistance) { best = candidate; bestDistance = distance; }
            }
            if (best != null && enemy.targetPlayer != null && !IsPlayerCloaked(enemy.targetPlayer)
                && enemy.targetPlayer != best && bufferDistance > 0f
                && Mathf.Abs(bestDistance - Vector3.Distance(enemy.transform.position, enemy.targetPlayer.transform.position)) < bufferDistance)
                return null;
            if (best != null) enemy.mostOptimalDistance = bestDistance;
            return best;
        }

        // F-SHADOW-6: postfix, not prefix. The old prefix skipped the original whenever ANY player
        // was reduced, which at tier 2/3 is permanently true, so the replacement ran for the whole
        // crew. A reduction can only ever REMOVE candidates, so when vanilla's own pick is not
        // reduced it is still the right answer and the selection never has to be re-run - which
        // shrinks the blast radius to zero for lobbies and enemies that have nothing to hide.
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.CheckLineOfSightForPlayer))]
        [HarmonyPostfix]
        private static void PostCheckLineOfSightForPlayer(EnemyAI __instance,
            ref PlayerControllerB __result, float width, int range, int proximityAwareness)
        {
            if (__instance == null || __result == null) return;
            if (GetDetectionMultiplierFor(__result) >= 1f) return;
            __result = SelectVisiblePlayer(__instance, width, range, proximityAwareness, false, 0f);
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.CheckLineOfSightForClosestPlayer))]
        [HarmonyPostfix]
        private static void PostCheckLineOfSightForClosestPlayer(EnemyAI __instance,
            ref PlayerControllerB __result, float width, int range, int proximityAwareness, float bufferDistance)
        {
            if (__instance == null || __result == null) return;
            if (GetDetectionMultiplierFor(__result) >= 1f) return;
            __result = SelectVisiblePlayer(__instance, width, range, proximityAwareness, true, bufferDistance);
        }

        /// <summary>
        /// F-SHADOW-4b: <c>CheckLineOfSightForPosition</c> is a player-visibility path for the
        /// Nutcracker, Old Bird, Spring Man, Jester, Sand Spider and Cave Dweller - they pass a
        /// player's gameplayCamera/transform position rather than going through the player-typed
        /// sight helpers, so the cloak used to be invisible to all of them.
        ///
        /// Scoping: only a position that actually coincides with a cloaked player's eye or feet is
        /// suppressed, so object LOS checks (doors, ship, scrap) are untouched. Turrets are
        /// deliberately NOT covered: <c>Turret.CheckForPlayersInLineOfSight</c> is on Turret, not
        /// EnemyAI, and Light Feet L2 owns turret visibility.
        /// </summary>
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.CheckLineOfSightForPosition))]
        [HarmonyPostfix]
        private static void PostCheckLineOfSightForPosition(ref bool __result, Vector3 objectPosition)
        {
            if (!__result || !ShadowStepNetState.AnyCloaked) return;

            PlayerControllerB[] players = StartOfRound.Instance != null
                ? StartOfRound.Instance.allPlayerScripts
                : null;
            if (players == null) return;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || !IsPlayerCloaked(player)) continue;

                if (player.gameplayCamera != null
                    && (player.gameplayCamera.transform.position - objectPosition).sqrMagnitude <= CLOAKED_POSITION_TOLERANCE_SQR)
                {
                    __result = false;
                    return;
                }

                if ((player.transform.position - objectPosition).sqrMagnitude <= CLOAKED_POSITION_TOLERANCE_SQR)
                {
                    __result = false;
                    return;
                }
            }
        }

        /// <summary>0.35 m squared - tight enough that no unrelated object shares the sample point.</summary>
        private const float CLOAKED_POSITION_TOLERANCE_SQR = 0.1225f;

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.GetAllPlayersInLineOfSight))]
        [HarmonyPostfix]
        private static void PostGetAllPlayersInLineOfSight(
            EnemyAI __instance, ref PlayerControllerB[] __result, int range)
        {
            try
            {
                if (__instance == null || __result == null || __result.Length == 0) return;

                bool anyDropped = false;
                for (int i = 0; i < __result.Length; i++)
                {
                    if (__result[i] != null && ShouldDropFromSight(__instance, __result[i], range))
                    {
                        anyDropped = true;
                        break;
                    }
                }

                if (!anyDropped) return;

                // Vanilla returns StartOfRound.allPlayerScripts itself when all four are visible,
                // so the filtered result MUST be a fresh array - mutating that one corrupts the game.
                var kept = new List<PlayerControllerB>(__result.Length);
                for (int i = 0; i < __result.Length; i++)
                {
                    if (__result[i] != null && !ShouldDropFromSight(__instance, __result[i], range))
                        kept.Add(__result[i]);
                }

                __result = kept.Count == 0 ? null : kept.ToArray();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] GetAllPlayersInLineOfSight patch error: {e}");
            }
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.GetAllPlayersInLineOfSightNonAlloc),
            new[] { typeof(float), typeof(int), typeof(Transform), typeof(float), typeof(int) })]
        [HarmonyPostfix]
        private static void PostGetAllPlayersNonAlloc(EnemyAI __instance, ref int __result, int range)
        {
            FilterNonAllocSight(__instance, RoundManager.Instance?.tempPlayersArray, ref __result, range);
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.GetAllPlayersInLineOfSightNonAlloc),
            new[] { typeof(PlayerControllerB[]), typeof(float), typeof(int), typeof(Transform), typeof(float), typeof(int) })]
        [HarmonyPostfix]
        private static void PostGetAllPlayersNonAllocBuffer(EnemyAI __instance, PlayerControllerB[] playersArray,
            ref int __result, int range)
        {
            FilterNonAllocSight(__instance, playersArray, ref __result, range);
        }

        private static void FilterNonAllocSight(EnemyAI enemy, PlayerControllerB[] results, ref int count, int range)
        {
            if (enemy == null || results == null || count <= 0) return;
            int length = Mathf.Min(count, results.Length);
            int kept = 0;
            for (int i = 0; i < length; i++)
                if (results[i] != null && !ShouldDropFromSight(enemy, results[i], range))
                    results[kept++] = results[i];
            for (int i = kept; i < length; i++) results[i] = null;
            count = kept;
        }

        /// <summary>
        /// GetClosestPlayer has no range argument, so there is nothing to scale directly. Instead the
        /// vanilla selection is re-run with each candidate's distance divided by its detection
        /// multiplier: a crouching Shadow Step player reads as 1/0.75 = 1.33x farther away, so the
        /// enemy prefers a nearer, unreduced crew mate exactly as a shrunk detection radius would.
        /// This replaces the old hardcoded 40 m CLOSEST_PLAYER_BASELINE_RANGE (#362 / S9).
        /// Only visual searches filter cloak. Non-visual searches and physical collisions retain
        /// vanilla targetability so noise and contact can still endanger a cloaked player.
        /// </summary>
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.GetClosestPlayer))]
        [HarmonyPostfix]
        private static void PostGetClosestPlayer(
            EnemyAI __instance,
            ref PlayerControllerB __result,
            bool requireLineOfSight,
            bool cannotBeInShip,
            bool cannotBeNearShip)
        {
            try
            {
                if (__instance == null || __result == null || (!requireLineOfSight && !(__instance is FlowermanAI))) return;

                // Vanilla already returned the nearest real distance. If that player is not reduced,
                // no reduction on anyone else can beat them, so there is nothing to re-evaluate.
                if (GetDetectionMultiplierFor(__result) >= 1f) return;

                PlayerControllerB[] players = StartOfRound.Instance != null
                    ? StartOfRound.Instance.allPlayerScripts
                    : null;
                if (players == null) return;

                PlayerControllerB best = null;
                float bestEffective = float.MaxValue;
                float bestReal = 0f;

                for (int i = 0; i < players.Length; i++)
                {
                    PlayerControllerB candidate = players[i];
                    if (candidate == null) continue;
                    if (!__instance.PlayerIsTargetable(candidate, cannotBeInShip)) continue;

                    if (cannotBeNearShip)
                    {
                        if (candidate.isInElevator) continue;
                        if (IsNearSpawnDenialPoint(candidate)) continue;
                    }

                    if (requireLineOfSight && Physics.Linecast(
                            __instance.transform.position + Vector3.up * 0.25f,
                            candidate.transform.position, 256))
                    {
                        continue;
                    }

                    float multiplier = GetDetectionMultiplierFor(candidate);
                    if (multiplier <= 0f) continue;

                    float real = Vector3.Distance(__instance.transform.position, candidate.transform.position);
                    float effective = real / multiplier;
                    if (effective < bestEffective)
                    {
                        bestEffective = effective;
                        bestReal = real;
                        best = candidate;
                    }
                }

                __result = best;
                // Downstream vanilla logic reads mostOptimalDistance; keep it the REAL distance.
                // F-SHADOW-15: when the only candidate was cloaked, vanilla has already written that
                // player's distance here. Restore vanilla's "nobody found" sentinel instead of
                // leaving a cloaked player's distance visible to an AI that skips the null check.
                __instance.mostOptimalDistance = best != null ? bestReal : 2000f;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] GetClosestPlayer patch error: {e}");
            }
        }

        private static bool IsNearSpawnDenialPoint(PlayerControllerB player)
        {
            GameObject[] denialPoints = RoundManager.Instance != null
                ? RoundManager.Instance.spawnDenialPoints
                : null;
            if (denialPoints == null) return false;

            for (int i = 0; i < denialPoints.Length; i++)
            {
                if (denialPoints[i] == null) continue;
                if (Vector3.Distance(denialPoints[i].transform.position, player.transform.position) < 10f)
                    return true;
            }

            return false;
        }

        // -----------------------------------------------------------------------
        // 3) LOCAL-PLAYER UPDATE TICK (KEYBIND POLL + HUD/OVERLAY DRIVERS)
        // -----------------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (__instance == null) return;
            if (!IsLocal(__instance)) return;

            // Janitors first: they must keep running even when the local player is dead or the
            // owner checks below bail out (#362 / S7-S8).
            CamoShellCloak.Tick();
            ReconcileRemoteCloakVisuals();

            if (!__instance.IsOwner || !__instance.isPlayerControlled) return;

            PublishLocalState(force: false);
            PollKeybind(__instance);
            if (__instance.isSprinting) BreakCloakForAction(__instance);
            UpdateInvisibilityTimer(__instance);
            UpdateCloakVignette();
        }

        /// <summary>
        /// Publishes (tier, cloaked) whenever either changes. Covers every activation path -
        /// keybind, timer expiry, tier purchase - without each one remembering to broadcast.
        /// </summary>
        internal static void PublishLocalState(bool force)
        {
            byte tier = (byte)Mathf.Clamp(LocalTier, 0, 3);
            bool cloaked = IsAnyInvisibilityActive();

            if (!force && _publishedOnce && tier == _publishedTier && cloaked == _publishedCloak)
                return;

            _publishedTier = tier;
            _publishedCloak = cloaked;
            _publishedOnce = true;
            ShadowStepNetState.PublishLocalState(tier, cloaked);
        }

        /// <summary>
        /// Drift guard for remote camo. A snapshot that arrived before the player object existed
        /// (late join, mid-round spawn) leaves the table correct and the visual missing; this pass
        /// closes that gap and also repairs any visual that a scene load stranded.
        /// </summary>
        private static void ReconcileRemoteCloakVisuals()
        {
            if (Time.time < _nextRemoteReconcile) return;
            _nextRemoteReconcile = Time.time + RemoteReconcileInterval;

            PlayerControllerB[] players = StartOfRound.Instance != null
                ? StartOfRound.Instance.allPlayerScripts
                : null;
            if (players == null) return;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || IsLocal(player)) continue;

                bool wantCloaked = player.isPlayerControlled
                    && !player.isPlayerDead
                    && ShadowStepNetState.IsCloaked(player);

                if (CamoShellCloak.IsCloaked(player) != wantCloaked)
                    CamoShellCloak.SetCloaked(player, wantCloaked);
            }
        }

        private static void PollKeybind(PlayerControllerB player)
        {
            if (!ShadowStepUpgrade.HasActiveAbility()) return;
            if (player.isPlayerDead) return;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen) return;
            if (player.isTypingChat) return;
            if (player.inSpecialInteractAnimation) return;
            if (player.inTerminalMenu || player.isSprinting) return;

            if (Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.ShadowStep))
                ActivateInvisibility(player);
        }

        // -----------------------------------------------------------------------
        // 4) INVISIBILITY ACTIVATION + TIMER
        // -----------------------------------------------------------------------

        private static void ActivateInvisibility(PlayerControllerB player)
        {
            if (!ShadowStepUpgrade.HasActiveAbility()) return;
            if (ShadowStepUpgrade.IsInvisible) return;
            if (Time.time < ShadowStepUpgrade.CooldownEndTime)
            {
                ShowCooldownTip();
                return;
            }

            _expiryCuePlayed = false;
            ShadowStepUpgrade.IsInvisible         = true;
            ShadowStepUpgrade.InvisibilityEndTime = Time.time + ShadowStepUpgrade.INVISIBILITY_DURATION;
            ShadowStepUpgrade.CooldownEndTime     =
                Time.time + ShadowStepUpgrade.COOLDOWN_DURATION;

            // The de-aggro is host work: publishing the cloak makes the host run it (S3).
            // F-SHADOW-14: PublishLocalState -> ShadowStepNetState.ApplyState already calls
            // ForceDeaggroForPlayer on the becameCloaked edge, so do NOT call it again here.
            PublishLocalState(force: true);

            var clip = GetActivationClip();
            PlayLocalShadowStepClip(clip, player.transform.position, 0.75f);

            StartCloakEffects();

            Plugin.Log.LogInfo($"[Shadow Step] Invisibility ACTIVATED - duration={ShadowStepUpgrade.INVISIBILITY_DURATION}s, IsInvisible={ShadowStepUpgrade.IsInvisible}");
        }

        private static void ShowCooldownTip()
        {
            float now = Time.unscaledTime;
            if (now < _nextCooldownTipAt)
                return;

            _nextCooldownTipAt = now + CooldownTipThrottleSeconds;
            int remainingSeconds = Mathf.CeilToInt(Mathf.Max(
                0f,
                ShadowStepUpgrade.CooldownEndTime - Time.time));
            HUDManager.Instance?.DisplayTip(
                "SHADOW STEP",
                "Cloak ready in " + remainingSeconds + "s.",
                isWarning: false);
        }

        private static bool _expiryCuePlayed;

        internal static void BreakCloakForAction(PlayerControllerB player)
        {
            if (!IsLocal(player) || !IsAnyInvisibilityActive()) return;
            ShadowStepUpgrade.IsInvisible = false;
            PublishLocalState(force: true);
            StopCloakEffects();
            // F-SHADOW-13: "I sprinted / swung and lost it" must not sound or read like "it ran out".
            PlayLocalShadowStepClip(GetBreakClip(), player.transform.position, 0.6f);
            HUDManager.Instance?.DisplayTip("SHADOW STEP", "Cloak broken.", isWarning: true);
        }

        private static void UpdateInvisibilityTimer(PlayerControllerB player)
        {
            if (!ShadowStepUpgrade.IsInvisible) return;
            float remaining = ShadowStepUpgrade.InvisibilityEndTime - Time.time;
            // F-SHADOW-7: the final-second cue is the level 3 differentiator the catalog sells, so
            // it must not fire at level 1 or 2.
            if (remaining > 0f && remaining <= 1f && !_expiryCuePlayed && LocalTier >= EXPIRY_CUE_TIER)
            {
                _expiryCuePlayed = true;
                PlayLocalShadowStepClip(GetDeactivationClip(), player.transform.position, 0.25f);
            }
            if (remaining > 0f) return;

            ShadowStepUpgrade.IsInvisible = false;

            PublishLocalState(force: true);
            PlayLocalShadowStepClip(GetDeactivationClip(), player.transform.position, 0.7f);
            StopCloakEffects();

            Plugin.Log.LogInfo("[Shadow Step] Invisibility ENDED - cooldown active.");
        }

        // -----------------------------------------------------------------------
        // 5) FORCE DE-AGGRO (SERVER-AUTHORITATIVE)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Drops the generic visual pursuit target on each enemy's current owning peer.
        ///
        /// Never switch an arbitrary AI to state zero: those indices have enemy-specific meaning.
        /// Hearing may reacquire the last noise position, and physical contact remains dangerous.
        /// </summary>
        internal static void ForceDeaggroForPlayer(PlayerControllerB player)
        {
            try
            {
                if (player == null) return;

                NetworkManager network = NetworkManager.Singleton;
                if (network == null || !network.IsListening) return;

                // F-SHADOW-14: RoundManager already keeps the live enemy list, and every peer used
                // to run UnityEngine.Object.FindObjectsOfType over the whole scene graph on every
                // broadcast even though the !IsOwner guard below made it a no-op for most of them.
                // The scan cannot be gated on IsServer: EnemyAI.ChangeOwnershipOfEnemy hands
                // individual enemies to clients, so a client really can own the enemy to de-aggro.
                List<EnemyAI> enemies = RoundManager.Instance != null
                    ? RoundManager.Instance.SpawnedEnemies
                    : null;
                if (enemies == null || enemies.Count == 0) return;

                int deaggroCount = 0;

                for (int i = 0; i < enemies.Count; i++)
                {
                    EnemyAI enemy = enemies[i];
                    if (enemy == null || enemy.isEnemyDead || !enemy.IsOwner) continue;
                    if (enemy is ForestGiantAI giant) ShadowStepEnemyPatch.LoseGiantTarget(giant, player);
                    if (enemy.targetPlayer != player) continue;

                    enemy.targetPlayer = null;
                    enemy.movingTowardsTargetPlayer = false;

                    deaggroCount++;
                }

                if (deaggroCount > 0)
                {
                    Plugin.Log.LogInfo(
                        $"[Shadow Step] Owner de-aggroed {deaggroCount} enemies from player {player.playerClientId}.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] ForceDeaggroForPlayer error: {e}");
            }
        }


        // -----------------------------------------------------------------------
        // 7) CLOAK EFFECTS - CAMO SHELL + AUDIO DAMPENING + HISS + VIGNETTE
        // -----------------------------------------------------------------------

        private static void StartCloakEffects()
        {
            if (_cloakEffectsActive) return;

            _cloakEffectsActive = true;

            PlayerControllerB player = LocalPlayer;
            if (player != null)
            {
                // Third-person body only: CamoShellCloak never touches the FP arms rig or the visor,
                // so the owner keeps their own hands and HUD while their body camo-fades for others
                // (and in mirrors / shadows / the security cam).
                CamoShellCloak.Cloak(player);

                StartAudioDampening(player);

                Transform audioHost = player.gameplayCamera != null ? player.gameplayCamera.transform : player.transform;
                _hissSource = audioHost.gameObject.AddComponent<AudioSource>();
                _hissSource.clip = GetHissClip();
                _hissSource.loop = true;
                _hissSource.volume = 0.09f;
                _hissSource.spatialBlend = 0f;
                _hissSource.playOnAwake = false;
                _hissSource.Play();
            }

            CreateCloakVignette();
        }

        private static void StopCloakEffects()
        {
            _cloakEffectsActive = false;

            PlayerControllerB player = LocalPlayer;
            if (player != null)
                CamoShellCloak.Decloak(player);

            // F-SHADOW-9: undo the duck as the same multiplicative delta that applied it. Writing a
            // captured absolute back would revert a master-volume change the player made from the
            // pause menu while cloaked, and leave it reverted.
            if (_audioVolumeDucked)
            {
                AudioListener.volume = Mathf.Clamp01(AudioListener.volume / CLOAK_AUDIO_VOLUME_SCALE);
                _audioVolumeDucked = false;
            }

            StopAudioDampening();

            // F-SHADOW-9: null the handle even when the Unity-null check fails because a scene load
            // destroyed the camera GameObject - otherwise a destroyed reference is held until the
            // next StartCloakEffects overwrites it.
            if (_hissSource != null)
            {
                _hissSource.Stop();
                UnityEngine.Object.Destroy(_hissSource);
            }
            _hissSource = null;

            DestroyCloakVignette();
        }

        private static void StartAudioDampening(PlayerControllerB player)
        {
            if (!_audioVolumeDucked)
            {
                AudioListener.volume = Mathf.Clamp01(AudioListener.volume * CLOAK_AUDIO_VOLUME_SCALE);
                _audioVolumeDucked = true;
            }

            GameObject filterHost = null;
            AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            if (listener != null) filterHost = listener.gameObject;
            if (filterHost == null && player != null && player.gameplayCamera != null)
                filterHost = player.gameplayCamera.gameObject;
            if (filterHost == null) return;

            _cloakLowPassFilter = filterHost.GetComponent<AudioLowPassFilter>();
            _cloakLowPassWasAdded = _cloakLowPassFilter == null;

            if (_cloakLowPassFilter == null)
                _cloakLowPassFilter = filterHost.AddComponent<AudioLowPassFilter>();

            // F-SHADOW-9: capture in BOTH branches. The added-filter branch used to leave the
            // "original" values on their 22000/1 defaults - correct only by luck.
            _originalLowPassCutoff = _cloakLowPassFilter.cutoffFrequency;
            _originalLowPassResonance = _cloakLowPassFilter.lowpassResonanceQ;

            _cloakLowPassFilter.cutoffFrequency = CLOAK_LOW_PASS_CUTOFF;
            _cloakLowPassFilter.lowpassResonanceQ = CLOAK_LOW_PASS_RESONANCE;
        }

        private static void StopAudioDampening()
        {
            if (_cloakLowPassFilter != null)
            {
                if (_cloakLowPassWasAdded)
                {
                    UnityEngine.Object.Destroy(_cloakLowPassFilter);
                }
                else
                {
                    _cloakLowPassFilter.cutoffFrequency = _originalLowPassCutoff;
                    _cloakLowPassFilter.lowpassResonanceQ = _originalLowPassResonance;
                }
            }

            _cloakLowPassFilter = null;
            _cloakLowPassWasAdded = false;
            _originalLowPassCutoff = 22000f;
            _originalLowPassResonance = 1f;
        }

        private static void PlayLocalShadowStepClip(AudioClip clip, Vector3 position, float volume)
        {
            if (clip == null) return;

            AudioSource uiAudio = HUDManager.Instance?.UIAudio;
            if (uiAudio != null)
                uiAudio.PlayOneShot(clip, volume);

            PlayerControllerB player = LocalPlayer;
            if (player != null && player.gameplayCamera != null)
            {
                AudioSource source = player.gameplayCamera.GetComponent<AudioSource>();
                if (source != null)
                    source.PlayOneShot(clip, volume * 0.65f);
            }
            else
            {
                AudioSource.PlayClipAtPoint(clip, position, volume * 0.5f);
            }
        }

        // --- Vignette texture generation (cached once) ---

        private static Texture2D GetVignetteTexture()
        {
            if (_vignetteTexture != null) return _vignetteTexture;

            int size = 256;
            _vignetteTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[size * size];
            float center = size / 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) / center;
                    float dy = (y - center) / center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    float alpha = Mathf.Clamp01((dist - 0.38f) / 0.62f);
                    alpha = alpha * alpha * (3f - 2f * alpha);

                    pixels[y * size + x] = new Color(0f, 0f, 0f, alpha);
                }
            }

            _vignetteTexture.SetPixels(pixels);
            _vignetteTexture.Apply();
            return _vignetteTexture;
        }

        private static void CreateCloakVignette()
        {
            if (_cloakVignetteObj != null) return;

            Canvas targetCanvas = null;
            if (HUDManager.Instance != null && HUDManager.Instance.playerScreenTexture != null)
                targetCanvas = HUDManager.Instance.playerScreenTexture.canvas;

            if (targetCanvas == null)
            {
                GameObject canvasGo = new GameObject("Y4NGZ_ShadowStep_OverlayCanvas");
                _cloakOverlayCanvas = canvasGo.AddComponent<Canvas>();
                _cloakOverlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _cloakOverlayCanvas.sortingOrder = 320;
                canvasGo.AddComponent<CanvasScaler>();
                canvasGo.AddComponent<GraphicRaycaster>();
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);
                targetCanvas = _cloakOverlayCanvas;
            }

            _cloakVignetteObj = new GameObject("ShadowStepCloakVignette");
            _cloakVignetteObj.transform.SetParent(targetCanvas.transform, false);

            RectTransform rect = _cloakVignetteObj.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _cloakVignette = _cloakVignetteObj.AddComponent<RawImage>();
            _cloakVignette.texture = GetVignetteTexture();
            _cloakVignette.color = new Color(0f, 0.02f, 0.015f, CLOAK_VIGNETTE_BASE_ALPHA);
            _cloakVignette.raycastTarget = false;
            _cloakVignetteObj.transform.SetAsLastSibling();

            Plugin.Log.LogInfo("[Shadow Step] Cloak vignette created.");
        }

        private static void DestroyCloakVignette()
        {
            if (_cloakVignetteObj != null)
            {
                UnityEngine.Object.Destroy(_cloakVignetteObj);
                _cloakVignetteObj = null;
                _cloakVignette = null;
            }

            if (_cloakOverlayCanvas != null)
            {
                UnityEngine.Object.Destroy(_cloakOverlayCanvas.gameObject);
                _cloakOverlayCanvas = null;
            }
        }

        private static void UpdateCloakVignette()
        {
            if (!_cloakEffectsActive || _cloakVignette == null) return;

            _cloakVignetteObj?.transform.SetAsLastSibling();
            float remaining = GetCurrentInvisibilityEndTime() - Time.time;

            float pulseSpeed = 2f;
            float pulseRange = 0.05f;
            float baseAlpha = CLOAK_VIGNETTE_BASE_ALPHA;

            // F-SHADOW-7: the "about to expire" ramp is part of the level 3 expiry cue, so it is
            // gated with it rather than running at every level.
            if (remaining < 2f && LocalTier >= EXPIRY_CUE_TIER)
            {
                pulseSpeed = 6f;
                pulseRange = 0.09f;
                baseAlpha = CLOAK_VIGNETTE_BASE_ALPHA + 0.13f;
            }

            float pulse = baseAlpha + Mathf.Sin(Time.time * pulseSpeed) * pulseRange;
            _cloakVignette.color = new Color(0f, 0.02f, 0.015f, Mathf.Clamp01(pulse));
        }

        // F-SHADOW-12: the external-invisibility state machine had exactly one caller,
        // EscapeReflexPatch, which was itself dead (F-SHADOW-10). Cloak state is now just the
        // Shadow Step flag.
        private static bool IsAnyInvisibilityActive() => ShadowStepUpgrade.IsInvisible;

        private static float GetCurrentInvisibilityEndTime()
        {
            return ShadowStepUpgrade.IsInvisible ? ShadowStepUpgrade.InvisibilityEndTime : 0f;
        }

        // -----------------------------------------------------------------------
        // 8) NETWORK LIFECYCLE HOOKS
        // -----------------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            try
            {
                // S4: a late joiner registers, publishes its own state, then pulls everyone else's.
                ShadowStepNetState.RegisterNetworkHandlers();
                PublishLocalState(force: true);
                ShadowStepNetState.RequestAllFromServer();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[Shadow Step] ConnectClientToPlayerObject hook failed: {e}");
            }
        }

        [HarmonyPatch(typeof(StartOfRound), "ResetShip")]
        [HarmonyPostfix]
        private static void PostResetShip()
        {
            try
            {
                ShadowStepNetState.ResetForNewCycle();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[Shadow Step] ResetShip hook failed: {e}");
            }
        }

        /// <summary>S7: a departing player leaves no orphaned table entry or stranded camo shell.</summary>
        [HarmonyPatch(typeof(StartOfRound), "OnPlayerDC")]
        [HarmonyPostfix]
        private static void PostOnPlayerDC(int playerObjectNumber, ulong clientId)
        {
            try
            {
                PlayerControllerB player = ShadowStepNetState.ResolvePlayer(clientId);
                if (player == null && StartOfRound.Instance != null
                    && StartOfRound.Instance.allPlayerScripts != null
                    && playerObjectNumber >= 0
                    && playerObjectNumber < StartOfRound.Instance.allPlayerScripts.Length)
                {
                    player = StartOfRound.Instance.allPlayerScripts[playerObjectNumber];
                }

                ShadowStepNetState.RemoveClient(clientId);
                if (player != null)
                    CamoShellCloak.ForgetPlayer(player);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[Shadow Step] OnPlayerDC cleanup failed: {e}");
            }
        }

        // -----------------------------------------------------------------------
        // 9) SMOKE POOF (BORROWED PARTICLE + PROCEDURAL AUDIO)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Remote cloak / decloak tell for a player who is NOT the local one.
        ///
        /// F-SHADOW-8: this used to Instantiate the first loaded object whose name contained
        /// "smoke", "poof" or "stun" - Resources.FindObjectsOfTypeAll returns live scene instances
        /// and mod-added systems too, so it could clone a stun-grenade host with its own audio,
        /// light, scripts and an unspawned NetworkObject. The self-owned procedural effect is now
        /// the only path.
        ///
        /// F-SHADOW-1: the 2D HUDManager.UIAudio one-shot and the 7-intensity point light are gone.
        /// They announced every cloak AND every decloak to the entire lobby, through walls, from
        /// anywhere on the map, with the same clip for both directions. The cue that replaces them
        /// is a world-space AudioSource that falls off within 12 m and plays the clip matching the
        /// direction of the transition. The owner's own audio stays with
        /// ActivateInvisibility / BreakCloakForAction / UpdateInvisibilityTimer.
        /// </summary>
        internal static void SpawnSmokePoof(Vector3 position, bool cloaked)
        {
            GameObject poof = new GameObject("Y4NGZ_ShadowStep_ProceduralPoof");
            poof.transform.position = position + Vector3.up;
            ParticleSystem ps = poof.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.28f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.38f, 0.72f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.85f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.45f, 1.2f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.08f, 0.14f, 0.13f, 0.42f),
                new Color(0.28f, 0.42f, 0.36f, 0.34f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = -0.03f;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.35f;

            ps.Play(withChildren: true);
            UnityEngine.Object.Destroy(poof, 1.4f);

            PlayWorldShadowStepCue(cloaked ? GetActivationClip() : GetDeactivationClip(), position, 0.5f);
        }

        /// <summary>
        /// F-SHADOW-1: a remote cue must be audible only to people who are actually near the player
        /// it belongs to, so it rides its own spatialised source instead of the 2D UI mixer.
        /// </summary>
        private static void PlayWorldShadowStepCue(AudioClip clip, Vector3 position, float volume)
        {
            if (clip == null) return;

            GameObject host = new GameObject("Y4NGZ_ShadowStep_WorldCue");
            host.transform.position = position;

            AudioSource source = host.AddComponent<AudioSource>();
            source.clip = clip;
            source.volume = volume;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1.5f;
            source.maxDistance = 12f;
            source.playOnAwake = false;
            source.Play();

            UnityEngine.Object.Destroy(host, clip.length + 0.1f);
        }

        // -----------------------------------------------------------------------
        // 10) ROUND RESET + END-OF-ROUND + DEATH CLEANUP
        // -----------------------------------------------------------------------

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            ShadowStepUpgrade.IsInvisible         = false;
            ShadowStepUpgrade.CooldownEndTime     = 0f;
            ShadowStepUpgrade.InvisibilityEndTime = 0f;
            // F-SHADOW-7: the expiry latch used to be reset only on activation, so a round that
            // started mid-cloak inherited a consumed cue.
            _expiryCuePlayed = false;
            _nextCooldownTipAt = 0f;
            _localTier = -1;

            StopCloakEffects();
            DestroyCloakVignette();
            CamoShellCloak.CleanupAll();
            PublishLocalState(force: true);
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            Cleanup();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPostfix]
        private static void PostKillPlayer(PlayerControllerB __instance)
        {
            try
            {
                if (__instance == null || !__instance.isPlayerDead) return;

                // Any player's death restores their visuals, local or remote (S8).
                CamoShellCloak.ForgetPlayer(__instance);

                if (!IsLocal(__instance)) return;

                ShadowStepUpgrade.IsInvisible = false;
                StopCloakEffects();
                PublishLocalState(force: true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] KillPlayer cleanup error: {e}");
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            Cleanup();
            ShadowStepNetState.Clear();
        }

        private static void Cleanup()
        {
            StopCloakEffects();
            DestroyCloakVignette();

            ShadowStepUpgrade.IsInvisible         = false;
            ShadowStepUpgrade.CooldownEndTime     = 0f;
            ShadowStepUpgrade.InvisibilityEndTime = 0f;
            _expiryCuePlayed = false;      // F-SHADOW-7
            _nextCooldownTipAt = 0f;
            _localTier = -1;

            _publishedOnce = false;
            _publishedTier = 255;
            _publishedCloak = false;

            CamoShellCloak.CleanupAll();
        }
    }
}
