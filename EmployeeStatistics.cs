using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZUpgrades
{
    [Serializable]
    internal sealed class EmployeeStatisticsData
    {
        public int monstersKilled;
        public int stepsTaken;
        public int scrapValueDelivered;
        public int timesDied;
        public int bombsDefused;
        public float payloadDistancePushedMeters;
        public int surveyBeaconsPlaced;
        public int itemsIncinerated;
        public int whistleblowerDamageDealt;
        public int pestsTrapped;
        public int containmentWavesSurvived;
        public int drillsPlaced;
        public int breakersRestored;
        public float timeSpentOnCctvSeconds;
        public int devicesHacked;
        public int quotasCompleted;

        internal EmployeeStatisticsData Clone()
        {
            return new EmployeeStatisticsData
            {
                monstersKilled = monstersKilled,
                stepsTaken = stepsTaken,
                scrapValueDelivered = scrapValueDelivered,
                timesDied = timesDied,
                bombsDefused = bombsDefused,
                payloadDistancePushedMeters = payloadDistancePushedMeters,
                surveyBeaconsPlaced = surveyBeaconsPlaced,
                itemsIncinerated = itemsIncinerated,
                whistleblowerDamageDealt = whistleblowerDamageDealt,
                pestsTrapped = pestsTrapped,
                containmentWavesSurvived = containmentWavesSurvived,
                drillsPlaced = drillsPlaced,
                breakersRestored = breakersRestored,
                timeSpentOnCctvSeconds = timeSpentOnCctvSeconds,
                devicesHacked = devicesHacked,
                quotasCompleted = quotasCompleted
            };
        }

        internal void Add(EmployeeStatisticsData other)
        {
            if (other == null)
                return;

            monstersKilled = AddClamped(monstersKilled, other.monstersKilled);
            stepsTaken = AddClamped(stepsTaken, other.stepsTaken);
            scrapValueDelivered = AddClamped(scrapValueDelivered, other.scrapValueDelivered);
            timesDied = AddClamped(timesDied, other.timesDied);
            bombsDefused = AddClamped(bombsDefused, other.bombsDefused);
            payloadDistancePushedMeters = AddClamped(payloadDistancePushedMeters, other.payloadDistancePushedMeters);
            surveyBeaconsPlaced = AddClamped(surveyBeaconsPlaced, other.surveyBeaconsPlaced);
            itemsIncinerated = AddClamped(itemsIncinerated, other.itemsIncinerated);
            whistleblowerDamageDealt = AddClamped(whistleblowerDamageDealt, other.whistleblowerDamageDealt);
            pestsTrapped = AddClamped(pestsTrapped, other.pestsTrapped);
            containmentWavesSurvived = AddClamped(containmentWavesSurvived, other.containmentWavesSurvived);
            drillsPlaced = AddClamped(drillsPlaced, other.drillsPlaced);
            breakersRestored = AddClamped(breakersRestored, other.breakersRestored);
            timeSpentOnCctvSeconds = AddClamped(timeSpentOnCctvSeconds, other.timeSpentOnCctvSeconds);
            devicesHacked = AddClamped(devicesHacked, other.devicesHacked);
            quotasCompleted = AddClamped(quotasCompleted, other.quotasCompleted);
        }

        private static int AddClamped(int left, int right)
        {
            long value = (long)Math.Max(0, left) + Math.Max(0, right);
            return (int)Math.Min(int.MaxValue, value);
        }

        private static float AddClamped(float left, float right)
        {
            if (float.IsNaN(left) || float.IsInfinity(left)) left = 0f;
            if (float.IsNaN(right) || float.IsInfinity(right)) right = 0f;
            return Mathf.Clamp(Mathf.Max(0f, left) + Mathf.Max(0f, right), 0f, 1000000000f);
        }
    }

    /// <summary>
    /// Per-save, local-player lifetime statistics shared by the Employee File and
    /// the performance report. Round counters remain live while a moon is active,
    /// then are committed exactly once when HUDManager builds the end-game report.
    /// </summary>
    internal static class EmployeeStatistics
    {
        private const string CompanyAssemblyName = "Y4NGZCompany";
        private const string CctvAssemblyName = "LethalCCTV";

        // Internal so the Employee File can render "steps taken" as a distance
        // without re-deriving the stride length the counter was built from.
        internal const float StepMeters = 0.76f;
        private const float MinimumTrackedMovement = 0.02f;
        private const float MaximumTrackedMovement = 8f;

        private static readonly string SaveDir =
            Path.Combine(Paths.ConfigPath, "Y4NGZEmployeeFile");

        private static EmployeeStatisticsData _lifetime;
        private static EmployeeStatisticsData _lastRound;
        private static RoundState _round = new RoundState();
        // Outlives the per-round RoundState so the round sequence number stays monotonic.
        private static readonly RoundLatch _latch = new RoundLatch();
        private static string _key;
        private static int _lastTimesFulfilledQuota = -1;

        private static bool _cctvReflectionResolved;
        private static PropertyInfo _cctvFocusedProperty;
        private static bool _payloadReflectionResolved;
        private static PropertyInfo _localPayloadCartProperty;
        private static PropertyInfo _payloadAttachedToLocalProperty;

        internal static EmployeeStatisticsData CurrentSnapshot
        {
            get
            {
                EnsureLoaded();
                EmployeeStatisticsData result = _lifetime?.Clone() ?? new EmployeeStatisticsData();
                if (_latch.Active && !_latch.Finalized)
                    result.Add(_round.Data);
                return result;
            }
        }

        internal static EmployeeStatisticsData LastRoundSnapshot => _lastRound?.Clone();

        internal static void SwitchToCurrentSave()
        {
            if (!SaveKey.TryGetCurrent(out string key))
            {
                _key = null;
                _lifetime = new EmployeeStatisticsData();
                ResetRuntimeState();
                return;
            }

            if (_lifetime != null && string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
                return;

            _key = key;
            _lifetime = Load(key);
            _lastRound = null;
            _round = new RoundState();
            _latch.Abandon();
            _lastTimesFulfilledQuota = CurrentQuotaCount();
            ResetPositionTracking();
        }

        internal static void BeginRound()
        {
            SwitchToCurrentSave();
            _round = new RoundState();
            // Unconditional, so a round that never finalized cannot leave Finalized set and
            // freeze every later round's recording (the client-side symptom of #214).
            _latch.Begin();
            _lastRound = null;
            _lastTimesFulfilledQuota = CurrentQuotaCount();
            ResetPositionTracking();
        }

        /// <summary>
        /// A round that pays nothing (mid-round disconnect, orbit reset). No-op once the
        /// round finalized, so a completed round keeps its lifetime contribution.
        /// </summary>
        internal static void AbandonUnfinalizedRound()
        {
            if (_latch.Finalized || !_latch.Active)
                return;

            _latch.Abandon();
            _round = new RoundState();
            ResetPositionTracking();
        }

        internal static void ResetCurrentSave()
        {
            if (!SaveKey.TryGetCurrentExistingOrSlot(out string key))
            {
                _key = null;
                _lifetime = new EmployeeStatisticsData();
                ResetRuntimeState();
                return;
            }

            DeleteForKey(key);
        }

        internal static bool DeleteForKey(string key)
        {
            bool deleted = false;
            try
            {
                string path = GetPath(key);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[EmployeeFile] Failed resetting statistics: {ex.Message}");
            }

            if (string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
            {
                _key = null;
                _lifetime = new EmployeeStatisticsData();
            }
            ResetRuntimeState();
            return deleted;
        }

        internal static void RecordItemGrabbed(GrabbableObject item)
        {
            if (!_latch.Active || _latch.Finalized || item == null)
                return;

            PlayerControllerB holder = item.playerHeldBy;
            if (holder != null)
                _round.LastItemHolderByKey[NetworkKey(item)] = holder.actualClientId;
        }

        internal static void RecordEnemyHit(EnemyAI enemy, PlayerControllerB playerWhoHit)
        {
            if (!_latch.Active || _latch.Finalized || enemy == null || !IsLocalPlayer(playerWhoHit))
                return;

            _round.LastEnemyHitByLocalPlayer[NetworkKey(enemy)] = Time.time;
        }

        internal static void RecordEnemyKilled(EnemyAI enemy)
        {
            if (!_latch.Active || _latch.Finalized || enemy == null)
                return;

            ulong key = NetworkKey(enemy);
            if (!_round.KilledEnemies.Add(key))
                return;

            if (!_round.LastEnemyHitByLocalPlayer.TryGetValue(key, out float lastHit)
                || Time.time - lastHit > Mathf.Max(1f, Plugin.ProgressionConfig.KillAttributionWindowSeconds.Value))
            {
                return;
            }

            _round.Data.monstersKilled = AddOne(_round.Data.monstersKilled);
        }

        internal static void RecordDeath()
        {
            AddRoundOrLifetime(data => data.timesDied = AddOne(data.timesDied));
        }

        internal static void RecordBombDefused(string eventId)
        {
            if (!TryAddRoundKey("bomb." + (eventId ?? string.Empty)))
                return;
            _round.Data.bombsDefused = AddOne(_round.Data.bombsDefused);
        }

        /// <summary>
        /// Driven by the attributed <c>SurveyDronePlaced</c> progression event (#194). The caller
        /// has already filtered the event to the local player, so this only has to dedupe: the
        /// host mirrors each event live and may replay it to a client that registered its handler
        /// late, and both copies carry the same id.
        /// </summary>
        internal static void RecordSurveyBeaconPlaced(string eventId)
        {
            if (!TryAddRoundKey("survey." + (eventId ?? string.Empty)))
                return;
            _round.Data.surveyBeaconsPlaced = AddOne(_round.Data.surveyBeaconsPlaced);
        }

        internal static void RecordItemIncinerated(int playerId, ulong itemNetworkObjectId)
        {
            PlayerControllerB local = LocalPlayer();
            if (local == null || (int)local.playerClientId != playerId)
                return;
            if (!TryAddRoundKey("incinerator.item." + itemNetworkObjectId))
                return;
            _round.Data.itemsIncinerated = AddOne(_round.Data.itemsIncinerated);
        }

        internal static void RecordWhistleblowerDamage(
            EnemyAI enemy,
            PlayerControllerB playerWhoHit,
            int damage)
        {
            if (enemy == null || damage <= 0 || !IsLocalPlayer(playerWhoHit))
                return;
            if (!_latch.Active || _latch.Finalized)
                return;
            _round.Data.whistleblowerDamageDealt = AddClamped(
                _round.Data.whistleblowerDamageDealt,
                damage);
        }

        internal static void ObservePestsTrapped(
            string eventId,
            ulong creditedClientId,
            int capturedTotal)
        {
            if (!_latch.Active || _latch.Finalized)
                return;

            // Every peer observes every attributed event before the caller filters it to the
            // local player. That keeps the cumulative total's delta correct when different
            // employees pull consecutive drops. Cached deltas also make Contracted's crew-wide
            // fallback deterministic for every participant in the fan-out.
            int gained = _round.PestCaptureCounts.Observe(capturedTotal);
            PlayerControllerB local = LocalPlayer();
            if (gained <= 0 || local == null || local.actualClientId != creditedClientId)
                return;
            if (!TryAddRoundKey("pest.trapped." + (eventId ?? string.Empty)))
                return;

            _round.Data.pestsTrapped = AddClamped(_round.Data.pestsTrapped, gained);
        }

        internal static void RecordContainmentWaveSurvived(string eventId)
        {
            if (!TryAddRoundKey("containment.wave." + (eventId ?? string.Empty)))
                return;
            _round.Data.containmentWavesSurvived = AddOne(_round.Data.containmentWavesSurvived);
        }

        internal static void RecordDrillPlaced(string eventId)
        {
            if (!TryAddRoundKey("shadowraid.drill." + (eventId ?? string.Empty)))
                return;
            _round.Data.drillsPlaced = AddOne(_round.Data.drillsPlaced);
        }

        internal static void RecordBreakerRestored(int restoredCount, ulong actorClientId)
        {
            PlayerControllerB local = LocalPlayer();
            if (local == null || local.actualClientId != actorClientId)
                return;
            if (!TryAddRoundKey("blackout.breaker.count." + Mathf.Max(1, restoredCount)))
                return;
            _round.Data.breakersRestored = AddOne(_round.Data.breakersRestored);
        }

        internal static void RecordSingleBreakerRestored(string eventId)
        {
            if (!TryAddRoundKey("blackout.breaker.single." + (eventId ?? string.Empty)))
                return;
            _round.Data.breakersRestored = AddOne(_round.Data.breakersRestored);
        }

        internal static void RecordDeviceHacked(UnityEngine.Object target)
        {
            string key = target != null
                ? target.GetType().FullName + "." + target.GetInstanceID()
                : "unknown." + Time.frameCount;
            RecordDeviceHacked(key);
        }

        internal static void RecordDeviceHacked(string targetKey)
        {
            if (!TryAddRoundKey("hack." + (targetKey ?? string.Empty)))
                return;
            _round.Data.devicesHacked = AddOne(_round.Data.devicesHacked);
        }

        internal static void UpdateRuntime(PlayerControllerB localPlayer, float deltaTime)
        {
            if (_lifetime == null)
                SwitchToCurrentSave();

            UpdateQuotaCompletion();
            if (!_latch.Active || _latch.Finalized || !IsLocalPlayer(localPlayer))
                return;

            float delta = Mathf.Clamp(deltaTime, 0f, 0.25f);
            UpdateSteps(localPlayer);
            if (IsCctvFocused())
                _round.Data.timeSpentOnCctvSeconds += delta;
            UpdatePayloadDistance();
        }

        internal static EmployeeStatisticsData FinalizeRound(int scrapCollected)
        {
            EnsureLoaded();
            if (!_latch.TryBeginFinalize(out bool recoveredMissedStart))
                return _lastRound?.Clone() ?? new EmployeeStatisticsData();

            if (recoveredMissedStart)
            {
                Plugin.Log?.LogWarning(
                    "[EmployeeStatistics] FinalizeRound ran without a round start "
                    + $"(sequence={_latch.Sequence}, scrapCollected={scrapCollected}). "
                    + "Nothing was recorded this round — the RoundLifecycle.RoundStarted hook "
                    + "did not fire for this peer.");
            }

            _round.Data.scrapValueDelivered = ComputeLocalDeliveredScrapValue();
            _lastRound = _round.Data.Clone();
            _lifetime ??= new EmployeeStatisticsData();
            _lifetime.Add(_lastRound);
            _latch.CompleteFinalize();
            Save();
            return _lastRound.Clone();
        }

        /// <summary>
        /// Candidate note lines for the Company performance report's Notes column,
        /// highest priority first. Only covers facts the Company report cannot derive
        /// itself — kills, steps, delivered scrap and deaths already have their own
        /// note categories over there, so repeating them would just crowd the column.
        /// </summary>
        internal static IReadOnlyList<ReportNoteCandidate> BuildReportNoteCandidates()
        {
            var candidates = new List<ReportNoteCandidate>();
            EmployeeStatisticsData stats = _lastRound;
            if (stats == null)
                return candidates;

            if (stats.bombsDefused > 0)
            {
                candidates.Add(new ReportNoteCandidate(
                    64,
                    $"* Defused {FormatCount(stats.bombsDefused, "live bomb")} during the operation."));
            }

            if (stats.surveyBeaconsPlaced > 0)
            {
                candidates.Add(new ReportNoteCandidate(
                    60,
                    $"* Planted {FormatCount(stats.surveyBeaconsPlaced, "survey beacon")} across the site."));
            }

            if (stats.devicesHacked > 0)
            {
                candidates.Add(new ReportNoteCandidate(
                    56,
                    $"* Hacked {FormatCount(stats.devicesHacked, "secured device")} during the operation."));
            }

            if (stats.payloadDistancePushedMeters >= 5f)
            {
                candidates.Add(new ReportNoteCandidate(
                    52,
                    $"* Pushed the payload {stats.payloadDistancePushedMeters:0}m across the site."));
            }

            if (stats.timeSpentOnCctvSeconds >= 20f)
            {
                candidates.Add(new ReportNoteCandidate(
                    48,
                    $"* Monitored ship cameras for {FormatDurationCompact(stats.timeSpentOnCctvSeconds)}."));
            }

            if (stats.quotasCompleted > 0)
            {
                candidates.Add(new ReportNoteCandidate(
                    40,
                    "* Closed out the profit quota during this operation."));
            }

            return candidates;
        }

        internal readonly struct ReportNoteCandidate
        {
            internal readonly int Priority;
            internal readonly string Text;

            internal ReportNoteCandidate(int priority, string text)
            {
                Priority = priority;
                Text = text;
            }
        }

        private static readonly string[] SpelledNumbers =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"
        };

        // Matches the Company note catalog voice: "two hostile creatures", "540 steps".
        private static string FormatCount(int count, string singularNoun)
        {
            int value = Mathf.Max(0, count);
            string quantity = value < SpelledNumbers.Length
                ? SpelledNumbers[value]
                : value.ToString("N0");
            return value == 1 ? $"{quantity} {singularNoun}" : $"{quantity} {singularNoun}s";
        }

        private static string FormatDurationCompact(float seconds)
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            int minutes = total / 60;
            int remainingSeconds = total % 60;
            return minutes > 0 ? $"{minutes}m {remainingSeconds}s" : $"{remainingSeconds}s";
        }

        internal static string FormatDuration(float seconds)
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            int hours = total / 3600;
            int minutes = (total % 3600) / 60;
            int remainingSeconds = total % 60;
            return hours > 0
                ? $"{hours}:{minutes:00}:{remainingSeconds:00}"
                : $"{minutes:00}:{remainingSeconds:00}";
        }

        private static void EnsureLoaded()
        {
            if (_lifetime == null)
                SwitchToCurrentSave();
        }

        private static EmployeeStatisticsData Load(string key)
        {
            try
            {
                string path = GetPath(key);
                if (File.Exists(path))
                    return JsonUtility.FromJson<EmployeeStatisticsData>(File.ReadAllText(path))
                           ?? new EmployeeStatisticsData();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[EmployeeFile] Failed reading statistics: {ex.Message}");
            }

            return new EmployeeStatisticsData();
        }

        private static void Save()
        {
            if (_lifetime == null || string.IsNullOrWhiteSpace(_key))
                return;

            try
            {
                Directory.CreateDirectory(SaveDir);
                File.WriteAllText(GetPath(_key), JsonUtility.ToJson(_lifetime, true));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[EmployeeFile] Failed writing statistics: {ex.Message}");
            }
        }

        private static string GetPath(string key)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = (key ?? "employee").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }
            return Path.Combine(SaveDir, new string(chars) + ".json");
        }

        private static void UpdateSteps(PlayerControllerB player)
        {
            if (player == null || !player.isPlayerControlled || player.isPlayerDead)
            {
                _round.PlayerPositionValid = false;
                return;
            }

            Vector3 current = player.transform.position;
            if (!_round.PlayerPositionValid)
            {
                _round.LastPlayerPosition = current;
                _round.PlayerPositionValid = true;
                return;
            }

            float distance = Vector3.Distance(_round.LastPlayerPosition, current);
            _round.LastPlayerPosition = current;
            if (distance < MinimumTrackedMovement || distance > MaximumTrackedMovement)
                return;

            _round.StepDistance += distance;
            _round.Data.stepsTaken = Mathf.Clamp(
                Mathf.FloorToInt(_round.StepDistance / StepMeters),
                0,
                int.MaxValue);
        }

        private static void UpdatePayloadDistance()
        {
            object cart = GetLocalPayloadCart();
            Component component = cart as Component;
            if (component == null || !IsPayloadAttachedToLocal(cart))
            {
                _round.PayloadCart = null;
                _round.PayloadPositionValid = false;
                return;
            }

            Vector3 current = component.transform.position;
            if (!ReferenceEquals(_round.PayloadCart, cart) || !_round.PayloadPositionValid)
            {
                _round.PayloadCart = cart;
                _round.LastPayloadPosition = current;
                _round.PayloadPositionValid = true;
                return;
            }

            float distance = Vector3.Distance(_round.LastPayloadPosition, current);
            _round.LastPayloadPosition = current;
            if (distance < 0.005f || distance > MaximumTrackedMovement)
                return;

            _round.Data.payloadDistancePushedMeters = Mathf.Clamp(
                _round.Data.payloadDistancePushedMeters + distance,
                0f,
                1000000000f);
        }

        private static bool IsCctvFocused()
        {
            if (!_cctvReflectionResolved)
            {
                _cctvReflectionResolved = true;
                Type type = ResolveCompanyType("Y4NGZCompany.ShipSystems.Surveillance.MonitorFocus");
                _cctvFocusedProperty = type?.GetProperty(
                    "IsFocused",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            try
            {
                return _cctvFocusedProperty != null
                       && _cctvFocusedProperty.GetValue(null) is bool focused
                       && focused;
            }
            catch
            {
                return false;
            }
        }

        private static object GetLocalPayloadCart()
        {
            ResolvePayloadReflection();
            try { return _localPayloadCartProperty?.GetValue(null); }
            catch { return null; }
        }

        private static bool IsPayloadAttachedToLocal(object cart)
        {
            ResolvePayloadReflection();
            try
            {
                return cart != null
                       && _payloadAttachedToLocalProperty != null
                       && _payloadAttachedToLocalProperty.GetValue(cart) is bool attached
                       && attached;
            }
            catch
            {
                return false;
            }
        }

        private static void ResolvePayloadReflection()
        {
            if (_payloadReflectionResolved)
                return;

            _payloadReflectionResolved = true;
            Type type = ResolveCompanyType("Y4NGZCompany.Contracts.Payload.PayloadCartController");
            _localPayloadCartProperty = type?.GetProperty(
                "LocalAttachedCart",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _payloadAttachedToLocalProperty = type?.GetProperty(
                "IsAttachedToLocalPlayer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static Type ResolveCompanyType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return null;
            // ShipSystems.Surveillance moved to LethalCCTV.dll in Y4NGZCompany#393 while
            // Contracts.* stayed put, so both assemblies have to be probed.
            return Type.GetType(fullName + ", " + CompanyAssemblyName, throwOnError: false)
                   ?? Type.GetType(fullName + ", " + CctvAssemblyName, throwOnError: false);
        }

        private static int ComputeLocalDeliveredScrapValue()
        {
            PlayerControllerB local = LocalPlayer();
            if (local == null)
                return 0;

            long value = 0;
            var counted = new HashSet<ulong>();
            GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item?.itemProperties == null || !item.itemProperties.isScrap)
                    continue;
                if (!item.isInShipRoom && !item.isInElevator)
                    continue;

                ulong key = NetworkKey(item);
                if (!counted.Add(key)
                    || !_round.LastItemHolderByKey.TryGetValue(key, out ulong holder)
                    || holder != local.actualClientId)
                {
                    continue;
                }

                value += Mathf.Max(0, item.scrapValue);
            }

            return (int)Math.Min(int.MaxValue, value);
        }

        private static void UpdateQuotaCompletion()
        {
            int current = CurrentQuotaCount();
            if (current < 0)
                return;
            if (_lastTimesFulfilledQuota < 0 || current < _lastTimesFulfilledQuota)
            {
                _lastTimesFulfilledQuota = current;
                return;
            }
            if (current == _lastTimesFulfilledQuota)
                return;

            int gained = current - _lastTimesFulfilledQuota;
            _lastTimesFulfilledQuota = current;
            if (_latch.Active && !_latch.Finalized)
            {
                _round.Data.quotasCompleted = AddClamped(_round.Data.quotasCompleted, gained);
            }
            else
            {
                _lifetime ??= new EmployeeStatisticsData();
                _lifetime.quotasCompleted = AddClamped(_lifetime.quotasCompleted, gained);
                Save();
            }
        }

        private static int CurrentQuotaCount()
        {
            try { return TimeOfDay.Instance != null ? Mathf.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : -1; }
            catch { return -1; }
        }

        private static bool TryAddRoundKey(string key)
        {
            return _latch.Active
                   && !_latch.Finalized
                   && _round.UniqueEventKeys.Add(key ?? string.Empty);
        }

        private static void AddRoundOrLifetime(Action<EmployeeStatisticsData> mutation)
        {
            if (mutation == null)
                return;
            EnsureLoaded();
            if (_latch.Active && !_latch.Finalized)
            {
                mutation(_round.Data);
                return;
            }

            mutation(_lifetime);
            Save();
        }

        private static PlayerControllerB LocalPlayer()
        {
            return GameNetworkManager.Instance?.localPlayerController
                   ?? StartOfRound.Instance?.localPlayerController;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            PlayerControllerB local = LocalPlayer();
            return player != null && local != null && player == local;
        }

        // Shared with the progression ledger on purpose: one definition of object identity.
        // NOTE: this statistic's ATTRIBUTION model is still the pre-#215 GrabItem one and is
        // knowingly wrong across a crew; migrating it is issue #223. Only the key is unified.
        private static ulong NetworkKey(Component component)
        {
            return NetworkObjectKey.For(component);
        }

        private static int AddOne(int value)
        {
            return value >= int.MaxValue ? int.MaxValue : Mathf.Max(0, value) + 1;
        }

        private static int AddClamped(int left, int right)
        {
            return (int)Math.Min(int.MaxValue, (long)Mathf.Max(0, left) + Mathf.Max(0, right));
        }

        private static void ResetPositionTracking()
        {
            _round.PlayerPositionValid = false;
            _round.PayloadPositionValid = false;
            _round.PayloadCart = null;
        }

        private static void ResetRuntimeState()
        {
            _lastRound = null;
            _round = new RoundState();
            _latch.Abandon();
            _lastTimesFulfilledQuota = CurrentQuotaCount();
            ResetPositionTracking();
        }

        private sealed class RoundState
        {
            internal readonly EmployeeStatisticsData Data = new EmployeeStatisticsData();
            internal readonly Dictionary<ulong, ulong> LastItemHolderByKey = new Dictionary<ulong, ulong>();
            internal readonly Dictionary<ulong, float> LastEnemyHitByLocalPlayer = new Dictionary<ulong, float>();
            internal readonly HashSet<ulong> KilledEnemies = new HashSet<ulong>();
            internal readonly HashSet<string> UniqueEventKeys = new HashSet<string>(StringComparer.Ordinal);
            internal Vector3 LastPlayerPosition;
            internal bool PlayerPositionValid;
            internal float StepDistance;
            internal object PayloadCart;
            internal Vector3 LastPayloadPosition;
            internal bool PayloadPositionValid;
            internal readonly CumulativeCountTracker PestCaptureCounts = new CumulativeCountTracker();
        }
    }
}
