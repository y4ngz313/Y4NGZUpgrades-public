using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZUpgrades
{
    internal static class ProgressionManager
    {
        private static readonly Dictionary<string, ProgressionState> States =
            new Dictionary<string, ProgressionState>(StringComparer.OrdinalIgnoreCase);

        private static readonly string SaveDir =
            Path.Combine(Application.persistentDataPath, "Y4NGZUpgrades");

        private static readonly string SavePath =
            Path.Combine(SaveDir, "progression_save.txt");

        private static ProgressionState _state = new ProgressionState();
        private static string _currentSaveKey;
        private static RoundTracker _round = new RoundTracker();
        // Survives the per-round RoundTracker so the round sequence number is monotonic for
        // the lifetime of the process; see RoundLatch for why that matters to the XP bar.
        private static readonly RoundLatch _latch = new RoundLatch();
        private static readonly Dictionary<string, TokenReservation> TokenReservations =
            new Dictionary<string, TokenReservation>(StringComparer.Ordinal);
        private const int MaxCompletedTokenSpends = 256;
        private const string OverachieverRetroGrantIdPrefix = "overachiever_retro_tier";
        private static bool _backfillingOverachiever;

        internal static RoundXpBreakdown LastRoundBreakdown { get; private set; }

        /// <summary>
        /// Upgrades-owned, player-facing reasons for the finalized local payout. Amounts are the
        /// amounts actually paid (including body risk scaling), not a second calculation in UI.
        /// </summary>
        internal static IReadOnlyList<ProgressionReportSource> LastRoundReportSources { get; private set; }
            = Array.Empty<ProgressionReportSource>();

        /// <summary>
        /// #1201: the crew-award XP inside <see cref="LastRoundBreakdown"/>'s awarded total that
        /// no report line and no remainder shows - the crew amounts handed to Company plus the
        /// <c>crew.</c> sources. The standalone composer subtracts it from its remainder.
        /// </summary>
        internal static int LastRoundHiddenXp { get; private set; }

        /// <summary>
        /// The finalized round sequence the report-source snapshot describes. Zero invalidates
        /// the snapshot on disabled, abandoned, or newly started rounds.
        /// </summary>
        private static int LastRoundReportSequence { get; set; }

        internal static int CurrentRankXp => _state?.RankXp ?? 0;
        internal static int CurrentUpgradeCurrency => _state?.UpgradeCurrency ?? 0;
        internal static int AvailableUpgradeCurrency
        {
            get
            {
                SwitchToCurrentSave();
                return Mathf.Max(0, CurrentUpgradeCurrency - ReservedCurrencyForCurrentSave());
            }
        }

        internal static bool TrySpendUpgradeCurrency(int amount)
        {
            if (amount <= 0)
                return true;

            SwitchToCurrentSave();
            if (_state == null || AvailableUpgradeCurrency < amount)
                return false;

            _state.UpgradeCurrency -= amount;
            Save();
            return true;
        }

        internal static bool TryReserveTokenSpend(string transactionId, int amount)
        {
            transactionId = NormalizeTransactionId(transactionId);
            if (transactionId.Length == 0 || amount <= 0 || !SwitchToCurrentSave())
                return false;

            if (_state.CompletedTokenSpendIds.Contains(transactionId))
                return true;
            if (TokenReservations.TryGetValue(transactionId, out TokenReservation existing))
                return string.Equals(existing.SaveKey, _currentSaveKey, StringComparison.OrdinalIgnoreCase)
                    && existing.Amount == amount;
            if (AvailableUpgradeCurrency < amount)
                return false;

            TokenReservations[transactionId] = new TokenReservation(_currentSaveKey, amount);
            return true;
        }

        internal static bool CommitTokenSpend(string transactionId)
        {
            transactionId = NormalizeTransactionId(transactionId);
            if (transactionId.Length == 0 || !SwitchToCurrentSave())
                return false;
            if (_state.CompletedTokenSpendIds.Contains(transactionId))
                return true;
            if (!TokenReservations.TryGetValue(transactionId, out TokenReservation reservation)
                || !string.Equals(reservation.SaveKey, _currentSaveKey, StringComparison.OrdinalIgnoreCase)
                || _state.UpgradeCurrency < reservation.Amount)
                return false;

            _state.UpgradeCurrency -= reservation.Amount;
            TokenReservations.Remove(transactionId);
            _state.CompletedTokenSpendIds.Add(transactionId);
            TrimCompletedTokenSpends(_state.CompletedTokenSpendIds);
            Save();
            return true;
        }

        internal static bool CancelTokenSpend(string transactionId)
        {
            transactionId = NormalizeTransactionId(transactionId);
            return transactionId.Length > 0 && TokenReservations.Remove(transactionId);
        }

        internal static void AddUpgradeCurrency(int amount)
        {
            if (amount == 0)
                return;

            SwitchToCurrentSave();
            if (_state == null)
                _state = new ProgressionState();

            _state.UpgradeCurrency = Mathf.Max(0, _state.UpgradeCurrency + amount);
            Save();
        }

        internal static void SetUpgradeCurrency(int amount)
        {
            SwitchToCurrentSave();
            if (_state == null)
                _state = new ProgressionState();

            _state.UpgradeCurrency = Mathf.Max(ReservedCurrencyForCurrentSave(), amount);
            Save();
        }

        /// <summary>
        /// Debug/test-only exact setter. An exact reset invalidates in-flight purchase
        /// reservations for this save so their held amount cannot silently raise the requested
        /// balance. Production purchases and awards never use this surface.
        /// </summary>
        internal static bool TrySetUpgradeCurrencyForTesting(int amount)
        {
            if (amount < 0 || !SwitchToCurrentSave())
                return false;

            string[] reservations = TokenReservations
                .Where(pair => string.Equals(
                    pair.Value.SaveKey,
                    _currentSaveKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();
            for (int i = 0; i < reservations.Length; i++)
                TokenReservations.Remove(reservations[i]);

            if (_state == null)
                _state = new ProgressionState();
            _state.UpgradeCurrency = amount;
            Save();
            return CurrentUpgradeCurrency == amount;
        }

        internal static bool TryApplyExternalTokenGrant(string actionId, int amount)
        {
            return TryApplyExternalTokenGrant(actionId, amount, out _);
        }

        /// <summary>
        /// Applies an external token grant exactly once per save. <paramref name="alreadyApplied"/>
        /// reports that this save's ledger already held <paramref name="actionId"/>, which makes the
        /// call a no-op success: replayed grants (late joiners) are safe to deliver repeatedly.
        /// </summary>
        internal static bool TryApplyExternalTokenGrant(string actionId, int amount, out bool alreadyApplied)
        {
            alreadyApplied = false;
            if (string.IsNullOrWhiteSpace(actionId) || amount <= 0)
                return false;

            if (!SwitchToCurrentSave())
                return false;

            if (_state == null)
                _state = new ProgressionState();

            actionId = actionId.Trim();
            if (_state.ExternalTokenGrantIds.Contains(actionId))
            {
                alreadyApplied = true;
                return true;
            }

            long updated = (long)Mathf.Max(0, _state.UpgradeCurrency) + amount;
            _state.UpgradeCurrency = (int)Math.Min(int.MaxValue, updated);
            _state.ExternalTokenGrantIds.Add(actionId);
            Save();
            return true;
        }

        /// <summary>
        /// Applies a one-time token grant to a specific save, ledgered by <paramref name="grantId"/>
        /// in the same <c>ExternalTokenGrantIds</c> set used by external grants. Safe to call
        /// repeatedly: the second call is a no-op and still reports success. Zero-token grants are
        /// still ledgered so a later call cannot re-evaluate the amount.
        /// </summary>
        internal static bool TryApplyLedgeredTokenGrant(string saveKey, string grantId, int amount)
        {
            if (string.IsNullOrWhiteSpace(saveKey) || string.IsNullOrWhiteSpace(grantId) || amount < 0)
                return false;

            grantId = grantId.Trim();

            // The current save's live state is NOT the object stored in States (Save() stores a
            // clone), so the current key has to be written through _state.
            if (!string.IsNullOrWhiteSpace(_currentSaveKey)
                && string.Equals(saveKey, _currentSaveKey, StringComparison.OrdinalIgnoreCase)
                && _state != null)
            {
                if (!_state.ExternalTokenGrantIds.Add(grantId))
                    return true;

                _state.UpgradeCurrency = AddCurrencyClamped(_state.UpgradeCurrency, amount);
                Save();
                return true;
            }

            if (!States.TryGetValue(saveKey, out ProgressionState state) || state == null)
                return false;
            if (!state.ExternalTokenGrantIds.Add(grantId))
                return true;

            state.UpgradeCurrency = AddCurrencyClamped(state.UpgradeCurrency, amount);
            WriteStatesToDisk();
            return true;
        }

        /// <summary>
        /// Makes Overachiever order-independent. The per-round grant only pays for ranks earned
        /// after purchase, so each owned tier retroactively pays its own per-rank delta for the
        /// ranks already earned. Ledgered once per save per tier, which also makes saves that
        /// bought the upgrade before this change whole on their first load after the update.
        /// </summary>
        internal static void ApplyOverachieverRankBonusBackfill()
        {
            if (_backfillingOverachiever || !IsEnabled())
                return;

            _backfillingOverachiever = true;
            try
            {
                if (!SwitchToCurrentSave() || _state == null || string.IsNullOrWhiteSpace(_currentSaveKey))
                    return;

                int tier = Upgrades.OverachieverUpgrade.GetTier();
                if (tier <= 0)
                    return;

                int earnedRanks = RankCatalog.GetRankIndex(_state.RankXp);
                bool changed = false;
                for (int t = 1; t <= tier; t++)
                {
                    string grantId = OverachieverRetroGrantIdPrefix + t;
                    if (!_state.ExternalTokenGrantIds.Add(grantId))
                        continue;

                    int perRankDelta = Upgrades.OverachieverUpgrade.GetBonusTokensForTier(t)
                                       - Upgrades.OverachieverUpgrade.GetBonusTokensForTier(t - 1);
                    int amount = Mathf.Max(0, earnedRanks * Mathf.Max(0, perRankDelta));
                    if (amount > 0)
                        _state.UpgradeCurrency = AddCurrencyClamped(_state.UpgradeCurrency, amount);

                    changed = true;
                    LogDebug($"Overachiever tier {t} retroactive grant +{amount} for {earnedRanks} already-earned rank(s).");
                }

                if (changed)
                    Save();
            }
            finally
            {
                _backfillingOverachiever = false;
            }
        }

        private static int AddCurrencyClamped(int current, int amount)
        {
            long updated = (long)Mathf.Max(0, current) + Mathf.Max(0, amount);
            return (int)Math.Min(int.MaxValue, updated);
        }

        internal static void Load()
        {
            States.Clear();
            TokenReservations.Clear();

            try
            {
                if (File.Exists(SavePath))
                {
                    foreach (string line in File.ReadAllLines(SavePath))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        string[] parts = line.Split('|');
                        if (parts.Length < 3)
                            continue;

                        string saveKey = parts[0].Trim();
                        if (string.IsNullOrWhiteSpace(saveKey))
                            continue;

                        int.TryParse(parts[1], out int rankXp);
                        int.TryParse(parts[2], out int currency);

                        var state = new ProgressionState
                        {
                            RankXp = RankCatalog.ClampXp(rankXp),
                            UpgradeCurrency = Mathf.Max(0, currency)
                        };
                        if (parts.Length >= 4)
                            state.ExternalTokenGrantIds.UnionWith(DecodeExternalGrantIds(parts[3]));
                        if (parts.Length >= 5)
                            state.CompletedTokenSpendIds.UnionWith(DecodeExternalGrantIds(parts[4]));
                        TrimCompletedTokenSpends(state.CompletedTokenSpendIds);
                        States[saveKey] = state;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[Progression] Failed reading progression save: {ex.Message}");
            }

            SwitchToCurrentSave();
        }

        internal static void Save()
        {
            try
            {
                if (!SwitchToCurrentSave(preserveExistingState: true))
                    return;

                States[_currentSaveKey] = _state?.Clone() ?? new ProgressionState();
                WriteStatesToDisk();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[Progression] Failed writing progression save: {ex.Message}");
            }
        }

        internal static bool SwitchToCurrentSave(bool preserveExistingState = false)
        {
            if (!SaveKey.TryGetCurrent(out string key))
            {
                if (preserveExistingState && !string.IsNullOrWhiteSpace(_currentSaveKey) && _state != null)
                    States[_currentSaveKey] = _state.Clone();

                _currentSaveKey = null;
                _state = new ProgressionState();
                return false;
            }

            if (preserveExistingState && !string.IsNullOrWhiteSpace(_currentSaveKey) && _state != null)
                States[_currentSaveKey] = _state.Clone();

            _currentSaveKey = key;
            if (!States.TryGetValue(_currentSaveKey, out _state) || _state == null)
            {
                _state = new ProgressionState();
                States[_currentSaveKey] = _state;
            }

            SyncVanillaRank();
            ProgressionRankSync.PublishLocalRank();
            return true;
        }

        internal static void ResetCurrentSave()
        {
            if (!SaveKey.TryGetCurrentExistingOrSlot(out string key))
            {
                _state = new ProgressionState();
                _currentSaveKey = null;
                Plugin.Log?.LogInfo("[Progression] Reset skipped; no active save file.");
                return;
            }

            DeleteForKeys(new[] { key });
            Plugin.Log?.LogInfo($"[Progression] Reset progression state for save '{key}'.");
        }

        internal static IReadOnlyCollection<string> GetKnownSaveKeys()
        {
            return States.Keys.ToArray();
        }

        internal static int DeleteForKeys(IEnumerable<string> keys)
        {
            var keySet = new HashSet<string>(
                (keys ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            if (keySet.Count == 0)
                return 0;

            int removed = 0;
            foreach (string key in keySet)
            {
                if (States.Remove(key))
                    removed++;
            }

            foreach (string transactionId in TokenReservations
                .Where(pair => keySet.Contains(pair.Value.SaveKey))
                .Select(pair => pair.Key)
                .ToArray())
            {
                TokenReservations.Remove(transactionId);
            }

            if (!string.IsNullOrWhiteSpace(_currentSaveKey) && keySet.Contains(_currentSaveKey))
            {
                _state = new ProgressionState();
                _currentSaveKey = null;
                _round = new RoundTracker();
                _latch.Abandon();
                LastRoundBreakdown = null;
                ClearLastRoundReportLines();
            }

            if (removed > 0)
                WriteStatesToDisk();
            return removed;
        }

        internal static void BeginRound()
        {
            if (!IsEnabled())
                return;

            // The pin is only trustworthy if the save key actually resolved: a failed switch
            // installs a blank state, whose rank reads 0, which would quietly pay a rank-26
            // player the rank-1 completion award for the whole round. An unresolved switch
            // leaves the pin unset and the award falls back to the live rank instead.
            bool saveResolved = SwitchToCurrentSave();
            _round = new RoundTracker
            {
                RoundStartTime = Time.time,
                MoonRisk = GetCurrentMoonRisk(),
                MoonMultiplier = GetMoonMultiplier(GetCurrentMoonRisk()),
                StartRankIndex = saveResolved
                    ? RankCatalog.GetRankIndex(CurrentRankXp)
                    : RoundTracker.UnpinnedRank
            };
            // Unconditional: a previous round that never finalized (disconnect, crash, a
            // missed end event) must not leave Finalized set or a stale breakdown cached,
            // or FinalizeRound would return last round's instance forever and the XP bar's
            // per-round dedupe would swallow every later round.
            _latch.Begin();
            LastRoundBreakdown = null;
            ClearLastRoundReportLines();
            SnapshotPreexistingShipScrap();
            SnapshotPreexistingShipBodies();
            LogDebug($"Round {_latch.Sequence} started. risk={_round.MoonRisk} multiplier={_round.MoonMultiplier:0.##} banked={_round.Delivery.PreexistingInsideCount}");
        }

        /// <summary>
        /// A round that pays nothing (mid-round disconnect, orbit reset). Never touches a
        /// round that already finalized, so the pending XP presentation keeps its breakdown.
        /// </summary>
        internal static void AbandonUnfinalizedRound(string reason)
        {
            if (_latch.Finalized || !_latch.Active)
                return;

            LogDebug($"Round {_latch.Sequence} abandoned without finalizing ({reason}).");
            _latch.Abandon();
            _round = new RoundTracker();
            LastRoundBreakdown = null;
            ClearLastRoundReportLines();
        }

        /// <summary>
        /// Records every scrap object that is already banked in the ship when the round starts.
        /// Those objects can never pay delivery XP again, which is what stops re-touching banked
        /// scrap (or dumping it outside and re-collecting it) from paying the 25% delivery rate
        /// once per round forever.
        /// </summary>
        private static void SnapshotPreexistingShipScrap()
        {
            _round.Delivery.Reset();

            GrabbableObject[] scrap = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < scrap.Length; i++)
            {
                GrabbableObject item = scrap[i];
                if (item == null || item.itemProperties == null || !item.itemProperties.isScrap)
                    continue;
                // A corpse is not scrap for this purpose whatever its Item asset says: bodies
                // are snapshotted and paid by the body pass below, under the same key. Letting
                // one object through both passes would either double-bank it here or, worse,
                // let it collect the delivery rate on top of its retrieval award at finalize.
                if (item is RagdollGrabbableObject)
                    continue;
                if (!IsInsideShip(item))
                    continue;

                _round.Delivery.MarkPreexistingInside(GetNetworkKey(item));
            }
        }

        /// <summary>
        /// Records every corpse already aboard when the round starts (#219), into the SAME
        /// ledger snapshot the scrap sweep uses. Without it a body stored across rounds would
        /// pay its retriever again every single round, because it sits in the ship at every
        /// round end.
        ///
        /// KEYED BY CORPSE INSTANCE, not by whose body it is. A player can leave more than one
        /// corpse in a session (revive, then die again), and those are genuinely separate
        /// retrievals: keying on <c>bodyID</c> would let the first corpse's stale record pay for
        /// the second one, and would let one stored corpse permanently block every future
        /// retrieval of that player. The grabbable is network-spawned, so its key is available
        /// on every peer with no id RPC to wait for, and it dies with the instance.
        ///
        /// <see cref="IsAboard"/> is the same union test the award pass uses; the two must not
        /// disagree about what "aboard" means or a body could escape the snapshot and still be
        /// paid.
        ///
        /// <c>includeInactive</c> matters: a deactivated corpse's GameObject can be inactive, and
        /// an unbanked corpse aboard is one that can be dragged out and back in for a second
        /// payout.
        ///
        /// RESIDUAL WINDOW, not closed: this runs from the RoundStarted hook
        /// (<c>openingDoorsSequence</c>), which is late enough that ship contents are loaded and
        /// present, but a corpse carried over from a previous round can still have its ragdoll
        /// link unresolved for a few frames on a client (RagdollGrabbableObject.Update relinks
        /// it), and its own boundary flags are only what the previous round left behind. A body
        /// in that state is skipped by the <c>IsAboard</c> test and could be re-paid on that one
        /// peer if a player then carries it out and back in. It is bounded — one peer, one
        /// corpse, one round — and closing it would need a deferred re-snapshot, which is more
        /// machinery than the exploit is worth.
        /// </summary>
        private static void SnapshotPreexistingShipBodies()
        {
            RagdollGrabbableObject[] bodies =
                UnityEngine.Object.FindObjectsOfType<RagdollGrabbableObject>(includeInactive: true);
            for (int i = 0; i < bodies.Length; i++)
            {
                RagdollGrabbableObject body = bodies[i];
                if (body == null || !IsAboard(body))
                    continue;

                _round.Delivery.MarkPreexistingInside(GetNetworkKey(body));
            }
        }

        /// <summary>
        /// "This corpse is in the ship", as both the round-start snapshot and the payout read it.
        ///
        /// The union of the ragdoll's own <c>isInShip</c> and the grabbable's boundary flags is
        /// deliberate and must be identical in both places. The two settle at different moments
        /// and through different code: the ragdoll's flag is recomputed every frame from its
        /// parenting (and force-set for everything inside <c>shipBounds</c> by
        /// <c>ElevatorAnimationEvents.ElevatorFullyRunning</c>), while the grabbable's flags come
        /// from the vanilla boundary write sites. Requiring only the ragdoll flag made the payout
        /// depend on a peer having observed that one animation event, which is exactly how one
        /// player sees BODIES +30 and another sees +0 for the same corpse.
        /// </summary>
        private static bool IsAboard(RagdollGrabbableObject body)
        {
            return body != null
                   && (IsInsideShip(body) || (body.ragdoll != null && body.ragdoll.isInShip));
        }

        /// <summary>
        /// Reads the object's side of the ship boundary. This is the single definition of
        /// "inside" used by both the crossing recorder and the payout, so a delta observed at
        /// a write site always means the same thing the finalize pass will test for.
        /// </summary>
        internal static bool IsInsideShip(GrabbableObject item)
        {
            return item != null && (item.isInShipRoom || item.isInElevator);
        }

        /// <summary>
        /// One observed transition of an object's ship-boundary flags, reported from every
        /// vanilla site that writes them (see <see cref="ProgressionPatches"/> for the site
        /// list and <see cref="DeliveryAttributionLedger"/> for why more than one hook is
        /// required). <paramref name="wasInside"/> is <see cref="IsInsideShip"/> sampled in the
        /// patch prefix: the truth is the ITEM's flag delta, never the arguments the call site
        /// passed, because several sites write the flags without changing them and one writes
        /// isInElevator behind SetItemInElevator's back.
        ///
        /// <paramref name="handler"/> is credited only when it is the object's actual holder.
        /// A site that names a player who is not holding the object is naming that machine's
        /// own local player (belt bag contents, kicked soccer balls, the vehicle magnet), which
        /// resolves differently on every peer; those crossings and enemy-carried ones are
        /// recorded as unattributed and pay nobody rather than paying divergently.
        ///
        /// Deliberately not filtered to scrap: the ledger is object-kind agnostic, so a corpse
        /// crossing the boundary records a handler under its own network key exactly the way
        /// scrap does (#219). The two finalize passes — delivered scrap and retrieved bodies —
        /// then read their own kind of object out of the one ledger, and each object is keyed
        /// exactly once.
        /// </summary>
        internal static void RecordItemBoundaryCrossing(
            PlayerControllerB handler,
            GrabbableObject item,
            bool wasInside)
        {
            if (!IsEnabled() || !_latch.Active || item == null)
                return;

            bool nowInside = IsInsideShip(item);
            if (nowInside == wasInside)
                return;

            // A corpse cannot un-deliver an item; neither can a player who never left the ship.
            // See DeliveryAttributionLedger.SuppressOutwardTransition -- the vanilla sites are
            // PlayerControllerB.cs:6836/:6969 reaching the :7289 defaults (setInShip:false).
            if (DeliveryAttributionLedger.SuppressOutwardTransition(
                    nowInside,
                    handler != null,
                    handler != null && handler.isPlayerDead,
                    handler != null && (handler.isInHangarShipRoom || handler.isInElevator)))
            {
                LogDebug(
                    $"Ignoring outward transition of '{GetItemName(item)}': client {handler.actualClientId} "
                    + $"is {(handler.isPlayerDead ? "dead" : "still inside the ship")}, so the item did not leave with them.");
                return;
            }

            ulong handlerClientId = handler != null && item.playerHeldBy == handler
                ? handler.actualClientId
                : DeliveryAttributionLedger.UnattributedHandler;

            RecordBoundaryCrossingCore(handlerClientId, item, nowInside);
        }

        /// <summary>
        /// A boundary crossing performed by something other than a carrying player, where the
        /// responsible player is known from replicated data rather than from the item's holder
        /// (the courier drone ferry). The holder-credibility rule cannot apply: the mover
        /// deliberately clears <c>playerHeldBy</c>. Pass
        /// <see cref="DeliveryAttributionLedger.UnattributedHandler"/> when no crew-consistent
        /// player exists.
        /// </summary>
        internal static void RecordAttributedBoundaryCrossing(
            ulong handlerClientId,
            GrabbableObject item,
            bool wasInside)
        {
            if (!IsEnabled() || !_latch.Active || item == null)
                return;

            bool nowInside = IsInsideShip(item);
            if (nowInside == wasInside)
                return;

            RecordBoundaryCrossingCore(handlerClientId, item, nowInside);
        }

        private static void RecordBoundaryCrossingCore(ulong handlerClientId, GrabbableObject item, bool nowInside)
        {
            ulong key = GetNetworkKey(item);
            _round.Delivery.RecordBoundaryCrossing(key, handlerClientId, nowInside);
            LogDebug(
                $"Boundary crossing '{GetItemName(item)}' {(nowInside ? "into" : "out of")} the ship by "
                + (handlerClientId == DeliveryAttributionLedger.UnattributedHandler
                    ? "an unattributed carrier."
                    : $"client {handlerClientId}."));
        }

        internal static void MarkLocalPlayerDeath()
        {
            if (!_latch.Active || _round.LocalPlayerDied)
                return;

            _round.LocalPlayerDied = true;
            _round.LocalPlayerDeathTime = Time.time;
            LogDebug($"Local survival ended after {Mathf.Max(0f, _round.LocalPlayerDeathTime - _round.RoundStartTime):0.0}s.");
        }

        /// <summary>
        /// Records that a player crossed an EntranceTeleport into the facility. Every peer sees
        /// the vanilla ClientRpc, so the local clutch decision is based on one crew-wide entrant
        /// set rather than on who this machine happened to observe nearby.
        /// </summary>
        internal static void RecordFacilityEntry(int playerObjectIndex)
        {
            if (!_latch.Active || _latch.Finalized)
                return;

            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || playerObjectIndex < 0 || playerObjectIndex >= players.Length)
                return;

            PlayerControllerB player = players[playerObjectIndex];
            if (player != null)
                _round.FacilityEntrants.Add(player.actualClientId);
        }

        /// <summary>
        /// Pays a successful local LethalCCTV signal-splice once per distinct mainframe object.
        /// The patched success path is local-player UI, so no actor guess or crew-wide fan-out is
        /// needed; the object key only prevents replay/reopen duplication.
        /// </summary>
        internal static void RecordMainframeHacked(Component target)
        {
            if (!IsEnabled() || Plugin.ProgressionConfig.MainframeHackEnabled.Value == false || !_latch.Active || _latch.Finalized || target == null)
                return;

            ulong key = GetNetworkKey(target);
            if (!_round.HackedMainframes.Add(key))
                return;

            int xp = Mathf.Max(0, Plugin.ProgressionConfig.MainframeHackXp.Value);
            if (xp <= 0)
                return;

            _round.MainframeHackXp = AddClamped(_round.MainframeHackXp, xp);
            _round.AddReportSource(
                "mainframe.hacked",
                ProgressionReportSourceKind.MainframeHack,
                string.Empty,
                count: 1,
                amount: xp);
            LogDebug($"Mainframe hack key={key} xp={xp}.");
        }

        /// <summary>
        /// Pays the LOCAL player for restoring Ship Systems power at the battery socket (#459).
        /// The host already decided the restore counts - power was down, and the restorer did not
        /// break the battery - and delivered it here, on the actor's own machine. This side owns
        /// the actor's config: the enable switch, the two amounts and the per-round cap.
        /// <paramref name="eventKey"/> is the host's per-restore identity, so a replayed delivery
        /// cannot pay or burn a capped slot twice.
        /// </summary>
        internal static void RecordShipBatteryRestore(bool usedApparatus, string eventKey)
        {
            Config.ProgressionSettings config = Plugin.ProgressionConfig;
            if (!IsEnabled() || config.ShipBatteryEnabled.Value == false || !_latch.Active || _latch.Finalized
                || string.IsNullOrWhiteSpace(eventKey) || _round.ShipBatteryRestoreKeys.Contains(eventKey))
                return;

            int xp = ShipBatteryXpPolicy.ComputeRestoreXp(
                usedApparatus,
                config.ShipBatteryReplaceXp.Value,
                config.ShipBatteryApparatusDockXp.Value,
                _round.ShipBatteryRestoresPaid,
                config.ShipBatteryMaxPerRound.Value);
            if (xp <= 0)
            {
                LogDebug($"Ship battery restore '{eventKey}' declined at {_round.ShipBatteryRestoresPaid}/{config.ShipBatteryMaxPerRound.Value}.");
                return;
            }

            _round.ShipBatteryRestoreKeys.Add(eventKey);
            _round.ShipBatteryRestoresPaid++;
            _round.ShipBatteryXp = AddClamped(_round.ShipBatteryXp, xp);
            _round.AddReportSource(
                usedApparatus ? "ship.apparatus.docked" : "ship.battery.replaced",
                usedApparatus ? ProgressionReportSourceKind.ShipApparatusDocked : ProgressionReportSourceKind.ShipBatteryReplaced,
                string.Empty,
                count: 1,
                amount: xp);
            LogDebug($"Ship battery restore '{eventKey}' apparatus={usedApparatus} xp={xp} ({_round.ShipBatteryRestoresPaid}/{config.ShipBatteryMaxPerRound.Value}).");
        }

        /// <summary>
        /// DISCOVERY bucket only. GrabItem fires only on the grabbing player's own machine
        /// (GrabItemOnClient is owner-only, and vanilla scrap does not set syncGrabFunction),
        /// so first-lift here means "first lift this peer saw", not a crew-wide first lift.
        /// That is an accepted limitation for discovery — each player is paid for what they
        /// personally found, and the payout is local anyway. Delivery attribution must NOT be
        /// built on this hook; it lives on the SetItemInElevator stream instead (#215).
        /// </summary>
        internal static void RecordScrapPickup(GrabbableObject item)
        {
            if (!IsEnabled() || Plugin.ProgressionConfig.ScrapPickupEnabled.Value == false || !_latch.Active || item == null || item.itemProperties == null || !item.itemProperties.isScrap)
                return;

            PlayerControllerB holder = item.playerHeldBy;
            ulong key = GetNetworkKey(item);
            if (holder == null || _round.FirstScrapDiscovererByKey.ContainsKey(key))
                return;
            _round.FirstScrapDiscovererByKey[key] = holder.actualClientId;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local == null || holder.actualClientId != local.actualClientId)
                return;

            int xp = Mathf.CeilToInt(Mathf.Max(0, item.scrapValue) * Plugin.ProgressionConfig.ScrapPickupXpPerValue.Value);
            if (xp <= 0)
                return;

            _round.ScrapPickupXp += xp;
            // #456: one discovery line per round, carrying the haul's credits. The report amount
            // is the sum of the same per-item awards the XP bar was paid, so the two reconcile.
            _round.AddReportSource(
                "scrap.discovered",
                ProgressionReportSourceKind.ScrapDiscovered,
                string.Empty,
                count: 1,
                amount: xp,
                value: Mathf.Max(0, item.scrapValue));
            LogDebug($"Scrap first pickup '{GetItemName(item)}' value={item.scrapValue} xp={xp}.");
        }

        internal static void RecordEnemyHit(EnemyAI enemy, PlayerControllerB playerWhoHit)
        {
            if (!IsEnabled() || Plugin.ProgressionConfig.MonsterKillEnabled.Value == false || !_latch.Active || enemy == null || playerWhoHit == null)
                return;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local == null || playerWhoHit != local)
                return;

            _round.LastEnemyHitByLocalPlayer[GetNetworkKey(enemy)] = Time.time;
        }

        internal static void RecordEnemyKilled(EnemyAI enemy)
        {
            if (!IsEnabled() || Plugin.ProgressionConfig.MonsterKillEnabled.Value == false || !_latch.Active || enemy == null)
                return;

            ulong key = GetNetworkKey(enemy);
            if (!_round.KilledEnemies.Add(key))
                return;

            if (!_round.LastEnemyHitByLocalPlayer.TryGetValue(key, out float lastHitTime)
                || Time.time - lastHitTime > Mathf.Max(1f, Plugin.ProgressionConfig.KillAttributionWindowSeconds.Value))
            {
                return;
            }

            int xp = GetEnemyKillXp(enemy);
            if (xp <= 0)
                return;

            _round.MonsterKillXp += xp;
            // #456: one kill line per round. The enemy name survives while the round killed one
            // type; a second type collapses the subject so the line reads "Killed 3 monsters".
            _round.AddReportSource(
                "monster.killed",
                ProgressionReportSourceKind.MonsterKill,
                GetEnemyName(enemy),
                count: 1,
                amount: xp);
            LogDebug($"Enemy kill '{GetEnemyName(enemy)}' xp={xp}.");
        }

        /// <summary>
        /// Base XP for completing a contract: a fraction (10% by default) of the completing
        /// player's rank XP width, times the per-contract-type multiplier.
        ///
        /// The width is read from the rank the player held AT ROUND START, pinned in
        /// <see cref="BeginRound"/>. Rank XP only ever moves in FinalizeRound, so at event time
        /// the live rank is the same value; pinning it makes that independent of the round's own
        /// payout - a player who crosses a rank boundary on this round's XP is still paid the
        /// completion award of the rank they actually did the contract in, whatever order the
        /// buckets happen to settle in.
        ///
        /// #457: it is also PARTICIPATION-gated. Company fans the outcome out to the whole crew,
        /// so without this a player who stayed in the ship all round - or joined late - collected
        /// the same completion award as the crew that ran the contract. Taking part means
        /// entering the facility or earning contract act XP; both are recorded during the round,
        /// and the outcome event is priced at round end, so the answer is already complete here.
        ///
        /// The result is BASE contract XP. It goes into the same round contract bucket as every
        /// other contract award and is scaled by the moon risk multiplier exactly once, at
        /// finalize.
        /// </summary>
        internal static int GetContractCompletionXp(string contractType)
        {
            Config.ProgressionSettings config = Plugin.ProgressionConfig;
            if (config == null || !config.IsContractSourceEnabled("ContractCompleted"))
                return 0;

            if (!ProgressionEconomyMath.IsEligibleForContractCompletionXp(
                    LocalPlayerEnteredFacility(),
                    _round.ContractActXp > 0))
            {
                LogDebug("Contract completion declined: the local player entered no facility and earned no act XP.");
                return 0;
            }

            return ProgressionEconomyMath.ComputeContractCompletionXp(
                RankCatalog.GetRankWidth(ResolveCompletionRankIndex()),
                config.ContractCompletionRankWidthFraction.Value,
                config.GetContractCompletedMultiplier(contractType));
        }

        /// <summary>
        /// The price of one contract ACT (#457). A kind the rebalance does not scale - the small
        /// repeatable credits, and anything a newer Company publishes - is paid its flat award
        /// unchanged.
        ///
        /// The rank basis is the completion award's, so the actor's act and the crew's completion
        /// share are always quoted against the same band.
        /// </summary>
        internal static int GetContractActXp(string eventKind, int flatXp)
        {
            Config.ProgressionSettings config = Plugin.ProgressionConfig;
            if (config == null || flatXp <= 0 || !ContractActCatalog.TryGet(eventKind, out ContractActCatalog.Row row))
                return flatXp;

            float fraction = row.Scaling == ContractActCatalog.ActScaling.Headline
                ? config.ContractHeadlineRankWidthFraction.Value
                : config.ContractStepRankWidthFraction.Value;
            return ProgressionEconomyMath.ComputeContractActXp(
                flatXp, RankCatalog.GetRankWidth(ResolveCompletionRankIndex()), fraction);
        }

        /// <summary>
        /// The Payload piloting ceiling for this round (#457): piloting pays per second, so the
        /// rank band raises the CAP rather than the rate.
        /// </summary>
        internal static int GetPayloadPilotMaxXp()
        {
            Config.ProgressionSettings config = Plugin.ProgressionConfig;
            if (config == null)
                return 0;

            return ProgressionEconomyMath.ComputePayloadPilotCap(
                config.PayloadPilotMaxXp.Value,
                RankCatalog.GetRankWidth(ResolveCompletionRankIndex()),
                config.ContractHeadlineRankWidthFraction.Value);
        }

        private static bool LocalPlayerEnteredFacility()
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            return local != null && _round.FacilityEntrants.Contains(local.actualClientId);
        }

        /// <summary>
        /// The pinned round-start rank, or - when the pin never took because the save key was
        /// unresolved at landing - the live rank. Reading the blank state's rank 0 instead would
        /// underpay a high-rank player silently, which is the one failure this award model must
        /// not have. If the key resolves now, the live value becomes the pin so the rest of the
        /// round stays on one rank.
        /// </summary>
        private static int ResolveCompletionRankIndex()
        {
            if (_round.StartRankIndex >= 0)
                return _round.StartRankIndex;

            bool saveResolved = SwitchToCurrentSave();
            int liveRank = RankCatalog.GetRankIndex(CurrentRankXp);
            if (saveResolved)
            {
                _round.StartRankIndex = liveRank;
                LogDebug($"Completion rank pinned late at rank index {liveRank} (round start had no resolvable save).");
            }

            return liveRank;
        }

        /// <summary>
        /// Records one contract award. Returns true when the award actually landed, which is what
        /// lets a count-capped caller advance its bucket only on a real payment (#194).
        /// </summary>
        internal static bool AddContractXp(string key, string label, int amount, bool oncePerRound)
        {
            return AddContractXp(key, label, amount, oncePerRound, eventKind: null, crewWide: false);
        }

        /// <summary>
        /// <paramref name="eventKind"/> is the published <c>ContractProgressionEventKind</c> member
        /// name, retained per kind so the performance report can decorate the line Company already
        /// writes for that objective with this award's share of the bucket (#220). The scalar
        /// <see cref="RoundTracker.ContractXp"/> stays the payout authority; the per-kind sums are
        /// a reporting view of the same numbers and never feed the award.
        ///
        /// A null kind — the public <see cref="ProgressionApi.AwardContractXp"/> surface, which
        /// names no event — still occupies a bucket, under the empty key. It maps to no line and
        /// is never reported, but it must stay in the reconciliation input or its share of the
        /// scaled total would be spread over the lines that ARE reported and overstate them.
        ///
        /// #1201: <paramref name="crewWide"/> is Company's per-occurrence <c>CrewWide</c> flag - a
        /// crew award every player in the round received. It only moves the award into a separate
        /// reporting bucket, so the report can hide it; payout, source gating and the #457
        /// participation gate still read the plain kind.
        /// </summary>
        internal static bool AddContractXp(string key, string label, int amount, bool oncePerRound, string eventKind, bool crewWide)
        {
            if (!IsEnabled()
                || Plugin.ProgressionConfig?.IsContractSourceEnabled(eventKind) != true
                || !_latch.Active
                || amount <= 0)
                return false;

            string resolvedKey = string.IsNullOrWhiteSpace(key) ? label : key;
            if (oncePerRound && !_round.ContractXpKeys.Add(resolvedKey))
                return false;

            _round.ContractXp += amount;
            _round.AddContractXpForKind(ProgressionReportLineKeys.ContractBucket(eventKind, crewWide), amount);
            // #457: the completion gate asks whether this player took part. Only a NAMED act kind
            // answers that: the outcome awards are the thing being gated, and the unnamed public
            // API bucket names no event and could have been granted to a ship-sitter.
            if (ContractActCatalog.CountsAsParticipation(eventKind))
                _round.ContractActXp += amount;
            LogDebug($"Contract XP '{label ?? resolvedKey}' +{amount}.");
            return true;
        }

        /// <summary>
        /// A repeatable contract award that is capped by OCCURRENCE COUNT within the round
        /// (#194). The publisher gives every occurrence its own event id, so the id dedupe alone
        /// would pay an unbounded number of times for a re-armable objective; the counter here is
        /// what bounds it.
        ///
        /// The counter advances only when an award actually lands, so a replayed duplicate (the
        /// client replay channel overlapping the live mirror) cannot burn a slot, and a cap that
        /// rejects an occurrence does not consume one either.
        /// </summary>
        internal static void AddCappedContractXp(string key, string label, int xpPerCredit, string capBucket, int maxPerRound)
        {
            AddCappedContractXp(key, label, xpPerCredit, capBucket, maxPerRound, eventKind: null, crewWide: false);
        }

        /// <inheritdoc cref="AddCappedContractXp(string,string,int,string,int)"/>
        internal static void AddCappedContractXp(
            string key,
            string label,
            int xpPerCredit,
            string capBucket,
            int maxPerRound,
            string eventKind,
            bool crewWide)
        {
            string resolvedKey = string.IsNullOrWhiteSpace(key) ? label : key;
            if (string.IsNullOrWhiteSpace(resolvedKey))
                return;

            string bucket = capBucket ?? string.Empty;
            _round.CappedContractCredits.TryGetValue(bucket, out int paid);
            int amount = ProgressionEconomyMath.ComputeCappedRepeatXp(paid, maxPerRound, xpPerCredit);
            if (amount <= 0)
            {
                LogDebug($"Contract XP '{label ?? resolvedKey}' declined: bucket '{bucket}' at {paid}/{maxPerRound}.");
                return;
            }

            // The bucket advances only on a real payment, so a duplicate event id -- the client
            // replay channel overlapping the live mirror -- cannot consume a capped slot, and
            // neither can an award refused because the round is over or progression is disabled.
            if (!AddContractXp(resolvedKey, label, amount, oncePerRound: true, eventKind, crewWide))
                return;

            _round.CappedContractCredits[bucket] = paid + 1;
            LogDebug($"Contract XP '{label ?? resolvedKey}' is {paid + 1}/{maxPerRound} in '{bucket}'.");
        }

        internal static RoundXpBreakdown FinalizeRound(int scrapCollected)
        {
            // Idempotent by design: whichever end-of-round trigger fires first (the
            // RoundLifecycle.RoundEnded event, or the HUD's FillEndGameStats on a timed moon)
            // does the payout, and every later call returns that same breakdown.
            if (LastRoundBreakdown != null && _latch.Finalized)
                return LastRoundBreakdown;

            if (!IsEnabled())
            {
                LastRoundBreakdown = BuildEmptyBreakdown();
                // The body sweep never ran on this path, so anything still stored belongs to an
                // earlier round and must not survive into this round's report.
                ClearLastRoundReportLines();
                return LastRoundBreakdown;
            }

            SwitchToCurrentSave();
            if (!_latch.TryBeginFinalize(out bool recoveredMissedStart))
                return LastRoundBreakdown ?? BuildEmptyBreakdown();

            if (recoveredMissedStart)
            {
                // The round-start event never reached this peer. Everything recorded during
                // the round was dropped, so this payout is survival-only and wrong. Before
                // #214 this was the client's permanent state; if it ever fires again it is a
                // regression in the RoundLifecycle hooks, not a recoverable condition.
                Plugin.Log?.LogWarning(
                    "[Progression] FinalizeRound ran without a round start "
                    + $"(sequence={_latch.Sequence}, moon={GetCurrentMoonRisk()}, scrapCollected={scrapCollected}, "
                    + $"host={GameNetworkManager.Instance?.isHostingGame.ToString() ?? "?"}). "
                    + "Every per-round recorder was inactive, so this payout is incomplete — "
                    + "the RoundLifecycle.RoundStarted hook did not fire for this peer.");
                _round.RoundStartTime = Time.time;
                _round.MoonRisk = GetCurrentMoonRisk();
                _round.MoonMultiplier = GetMoonMultiplier(_round.MoonRisk);
                // Same rule as BeginRound: pin the rank the player holds before this payout is
                // applied. SwitchToCurrentSave already ran above, and _state.RankXp is still the
                // pre-round value here, so both paths pin the same thing.
                _round.StartRankIndex = RankCatalog.GetRankIndex(CurrentRankXp);
            }

            _round.ScrapDeliveredXp = ComputeLocalDeliveredScrapXp();
            _round.BodyRetrievalXp = ComputeLocalBodyRetrievalXp();
            _round.SurvivalXp = ComputeSurvivalXp();
            _round.ClutchXp = ComputeClutchXp();

            int oldXp = RankCatalog.ClampXp(_state.RankXp);
            int oldRank = RankCatalog.GetRankIndex(oldXp);
            int raw = SumPositive(
                _round.ScrapPickupXp,
                _round.ScrapDeliveredXp,
                _round.MonsterKillXp,
                _round.SurvivalXp,
                _round.MainframeHackXp,
                _round.ClutchXp,
                _round.ShipBatteryXp,
                _round.ContractXp,
                _round.BodyRetrievalXp);
            int contractAwarded = ProgressionEconomyMath.ScaleContractXp(_round.ContractXp, _round.MoonMultiplier);
            int bodyAwarded = ProgressionEconomyMath.ScaleRiskXp(_round.BodyRetrievalXp, _round.MoonMultiplier);
            int awarded = ProgressionEconomyMath.ComputeRoundAwardedXp(
                _round.ScrapPickupXp,
                _round.ScrapDeliveredXp,
                _round.MonsterKillXp,
                _round.SurvivalXp,
                _round.MainframeHackXp,
                _round.ClutchXp,
                _round.ShipBatteryXp,
                _round.ContractXp,
                _round.BodyRetrievalXp,
                _round.MoonMultiplier);
            int newXp = RankCatalog.ClampXp(oldXp + awarded);
            int newRank = RankCatalog.GetRankIndex(newXp);
            int ranksGained = Mathf.Max(0, newRank - oldRank);
            int currencyGranted = RankCatalog.GetTokenGrantForRankRange(oldRank + 1, newRank);
            currencyGranted += ranksGained * Upgrades.OverachieverUpgrade.GetBonusTokensPerRank();

            _state.RankXp = newXp;
            _state.UpgradeCurrency = Mathf.Max(0, _state.UpgradeCurrency + currencyGranted);

            LastRoundBreakdown = new RoundXpBreakdown
            {
                RoundSequence = _latch.Sequence,
                OldXp = oldXp,
                NewXp = newXp,
                ScrapPickupXp = _round.ScrapPickupXp,
                ScrapDeliveredXp = _round.ScrapDeliveredXp,
                MonsterKillXp = _round.MonsterKillXp,
                SurvivalXp = _round.SurvivalXp,
                MainframeHackXp = _round.MainframeHackXp,
                ClutchXp = _round.ClutchXp,
                ShipBatteryXp = _round.ShipBatteryXp,
                ContractXp = _round.ContractXp,
                ContractXpAwarded = contractAwarded,
                BodyRetrievalXp = _round.BodyRetrievalXp,
                BodyRetrievalXpAwarded = bodyAwarded,
                MoonMultiplier = _round.MoonMultiplier,
                MoonRisk = _round.MoonRisk,
                RawTotal = raw,
                AwardedTotal = awarded,
                OldRankIndex = oldRank,
                NewRankIndex = newRank,
                RanksGained = ranksGained,
                CurrencyGranted = currencyGranted
            };

            // #392: each contract act's share of the scaled bucket goes to the report line
            // Company already writes for that act; only what maps to no Company line stays on
            // this plugin's contract note. Recorded from inside the payout, which the latch
            // admits once per round, so Company's summing sink is never double-fed - and this
            // runs a frame or more before Company reads it (FillEndGameStats prefix or the
            // RoundEnded handler; Company reads on the frame after FillEndGameStats).
            IReadOnlyList<KeyValuePair<string, int>> companyLines = ProgressionReportLineKeys.BuildContractLineAmounts(
                _round.ContractXpKindOrder,
                _round.ContractXpByKind,
                contractAwarded);
            int contractDecoratedByCompany = CompanyReportXpBridge.Record(
                ProgressionPerformanceReportUi.GetLocalPlayerIndex(),
                companyLines,
                out int crewWideHandedToCompany);
            if (contractDecoratedByCompany > 0)
                LogDebug($"Handed {contractDecoratedByCompany} of {contractAwarded} contract XP to Company's report lines ({companyLines.Count} line(s)).");
            AddFinalizedReportSources(bodyAwarded, Mathf.Max(0, contractAwarded - contractDecoratedByCompany));
            LastRoundReportSources = _round.BuildReportSources();
            LastRoundHiddenXp = crewWideHandedToCompany + ProgressionReportNotePlan.SumCrewWide(LastRoundReportSources);
            LastRoundReportSequence = LastRoundBreakdown.RoundSequence;
            int localReportSlot = ProgressionPerformanceReportUi.GetLocalPlayerIndex();
            CompanyReportXpBridge.RecordSources(localReportSlot,
                LastRoundReportSources, awarded, LastRoundReportSequence);
            // #458: the same presentation to the crew, so every report shows this player's own
            // lines on this player's row. Presentation only; nothing here changes the payout.
            Patches.ProgressionReportShareNetwork.PublishLocal(localReportSlot, awarded,
                LastRoundReportSources, LastRoundReportSequence, companyLines);

            _latch.CompleteFinalize();
            Save();
            SyncVanillaRank();
            ProgressionRankSync.PublishLocalRank();
            LogDebug($"Round {_latch.Sequence} finalized raw={raw} awarded={awarded} old={oldXp} new={newXp} ranks={ranksGained} currency={currencyGranted}.");
            return LastRoundBreakdown;
        }

        internal static void SyncVanillaRank()
        {
            try
            {
                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
                if (local == null || HUDManager.Instance == null)
                    return;

                HUDManager.Instance.SyncPlayerLevelServerRpc(
                    (int)local.playerClientId,
                    RankCatalog.GetVanillaRankIndex(CurrentRankXp),
                    hasBeta: true);
            }
            catch (Exception ex)
            {
                LogDebug($"Vanilla rank sync skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Drops the report view alongside the breakdown it describes. Any path that replaces or
        /// abandons the breakdown must invalidate these too, or a report could reuse the previous
        /// round's sources. The sequence is reset to zero as the freshness stamp.
        /// </summary>
        private static void ClearLastRoundReportLines()
        {
            LastRoundReportSources = Array.Empty<ProgressionReportSource>();
            LastRoundHiddenXp = 0;
            LastRoundReportSequence = 0;
        }

        internal static bool TryGetLastRoundReportSources(
            out IReadOnlyList<ProgressionReportSource> sources)
        {
            sources = Array.Empty<ProgressionReportSource>();
            RoundXpBreakdown breakdown = LastRoundBreakdown;
            if (breakdown == null
                || LastRoundReportSequence <= 0
                || breakdown.RoundSequence != LastRoundReportSequence
                || LastRoundReportSources == null
                || LastRoundReportSources.Count == 0)
            {
                return false;
            }

            sources = LastRoundReportSources;
            return true;
        }

        internal static string BuildPerformanceReportLine()
        {
            RoundXpBreakdown log = LastRoundBreakdown;
            if (log == null)
                return string.Empty;

            return $"XP +{log.AwardedTotal}  FOUND +{log.ScrapPickupXp} | SCRAP +{log.ScrapDeliveredXp} | KILLS +{log.MonsterKillXp} | SURV +{log.SurvivalXp} | HACK +{log.MainframeHackXp} | CLUTCH +{log.ClutchXp} | BATTERY +{log.ShipBatteryXp} | BODIES +{log.BodyRetrievalXpAwarded} ({log.BodyRetrievalXp} x{log.MoonMultiplier:0.##}) | CONTRACT +{log.ContractXpAwarded} ({log.ContractXp} x{log.MoonMultiplier:0.##})";
        }

        private static RoundXpBreakdown BuildEmptyBreakdown()
        {
            int xp = _state?.RankXp ?? 0;
            int rank = RankCatalog.GetRankIndex(xp);
            return new RoundXpBreakdown
            {
                OldXp = xp,
                NewXp = xp,
                OldRankIndex = rank,
                NewRankIndex = rank,
                MoonRisk = GetCurrentMoonRisk(),
                MoonMultiplier = GetMoonMultiplier(GetCurrentMoonRisk())
            };
        }

        /// <summary>
        /// <paramref name="contractUnreported"/> is the contract XP no Company line carries
        /// (#392): the crew-wide outcome award and API awards, or the whole bucket when Company
        /// is not loaded. Zero writes no contract note at all.
        /// </summary>
        private static void AddFinalizedReportSources(int bodyAwarded, int contractUnreported)
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            bool extractedAlive = local != null && !local.isPlayerDead;

            if (_round.SurvivalXp > 0)
            {
                _round.AddReportSource(
                    "survival",
                    ProgressionReportSourceKind.Survival,
                    extractedAlive ? "extracted" : "timed",
                    _round.SurvivalMinutesForReport,
                    _round.SurvivalXp);
            }

            if (bodyAwarded > 0 && _round.RetrievedBodyCount > 0)
            {
                string owner = _round.RetrievedBodyCount == 1 && _round.RetrievedBodyNames.Count == 1
                    ? _round.RetrievedBodyNames[0]
                    : string.Empty;
                _round.AddReportSource(
                    "body.retrieved",
                    ProgressionReportSourceKind.BodyRetrieval,
                    owner,
                    _round.RetrievedBodyCount,
                    bodyAwarded);
            }

            if (_round.ClutchXp > 0)
            {
                _round.AddReportSource(
                    "survival.clutch",
                    ProgressionReportSourceKind.Clutch,
                    string.Empty,
                    count: 1,
                    amount: _round.ClutchXp);
            }

            if (contractUnreported > 0)
            {
                // Retain the exact per-kind shares used by the amount bridge. No second
                // rounding pass.
                var raw = new int[_round.ContractXpKindOrder.Count];
                for (int i = 0; i < raw.Length; i++) raw[i] = _round.ContractXpByKind[_round.ContractXpKindOrder[i]];
                int[] scaled = ProgressionEconomyMath.DistributeScaledXp(raw, LastRoundBreakdown.ContractXpAwarded);
                int remaining = contractUnreported;
                for (int i = 0; i < raw.Length && remaining > 0; i++)
                {
                    string kind = _round.ContractXpKindOrder[i];
                    if (ProgressionReportLineKeys.MapEventKindToLineKey(kind) != null) continue;
                    int amount = Math.Min(scaled[i], remaining);
                    if (amount <= 0) continue;
                    // #1201: a crew-award outcome goes out as crew.contract.<kind>, which the
                    // report keeps out of its lines and its remainder.
                    _round.AddReportSource(ProgressionReportLineKeys.ContractSourceKey(kind), ProgressionReportSourceKind.Contract,
                        ProgressionReportLineKeys.OutcomeLabel(kind), 1, amount);
                    remaining -= amount;
                }
                if (remaining > 0)
                    _round.AddReportSource("contract.other", ProgressionReportSourceKind.Contract,
                        "Other contract work", 1, remaining);
            }
        }

        private static int ComputeSurvivalXp()
        {
            bool timeEnabled = Plugin.ProgressionConfig.SurvivalTimeEnabled.Value;
            bool extractionEnabled = Plugin.ProgressionConfig.SurvivalExtractBonusEnabled.Value;
            if (!timeEnabled && !extractionEnabled)
                return 0;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            int xp = 0;
            if (timeEnabled)
            {
                float endTime = _round.LocalPlayerDied
                    ? _round.LocalPlayerDeathTime
                    : Time.time;
                float seconds = Mathf.Max(0f, endTime - _round.RoundStartTime);
                // Survival is wall-clock, so cap payable time instead of rewarding ship idling.
                float maxMinutes = Mathf.Max(1, Plugin.ProgressionConfig.SurvivalMaxMinutesPerRound.Value);
                float minutes = Mathf.Min(seconds / 60f, maxMinutes);
                _round.SurvivalMinutesForReport = Mathf.Max(1, Mathf.CeilToInt(minutes));
                xp = Mathf.FloorToInt(
                    minutes * Mathf.Max(0, Plugin.ProgressionConfig.SurvivalXpPerMinute.Value));
            }

            if (extractionEnabled && local != null && !local.isPlayerDead)
            {
                _round.SurvivalMinutesForReport = Mathf.Max(1, _round.SurvivalMinutesForReport);
                xp += Mathf.Max(0, Plugin.ProgressionConfig.SurvivalExtractBonus.Value);
            }

            return Mathf.Max(0, xp);
        }

        private static int ComputeClutchXp()
        {
            if (Plugin.ProgressionConfig.ClutchExtractEnabled.Value == false)
                return 0;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local == null)
                return 0;

            bool localEntered = _round.FacilityEntrants.Contains(local.actualClientId);
            bool localExtractedAlive = !local.isPlayerDead;
            int otherEntrants = 0;
            int deadOtherEntrants = 0;

            foreach (ulong entrantId in _round.FacilityEntrants)
            {
                if (entrantId == local.actualClientId)
                    continue;

                otherEntrants++;
                PlayerControllerB entrant = FindPlayerByActualClientId(entrantId);
                if (entrant != null && entrant.isPlayerDead)
                    deadOtherEntrants++;
            }

            if (!ClutchXpPolicy.IsEligible(
                    localEntered,
                    localExtractedAlive,
                    otherEntrants,
                    deadOtherEntrants))
            {
                return 0;
            }

            int xp = Mathf.Max(0, Plugin.ProgressionConfig.ClutchExtractXp.Value);
            LogDebug(
                $"Clutch extraction credited: entrants={otherEntrants + 1} "
                + $"deadOthers={deadOtherEntrants} xp={xp}.");
            return xp;
        }

        private static int ComputeLocalDeliveredScrapXp()
        {
            if (Plugin.ProgressionConfig.ScrapDeliveredEnabled.Value == false)
                return 0;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local == null)
                return 0;

            int attributedValue = 0;
            int deliveredCount = 0;
            var counted = new HashSet<ulong>();
            GrabbableObject[] scrap = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < scrap.Length; i++)
            {
                GrabbableObject item = scrap[i];
                if (item == null || item.itemProperties == null || !item.itemProperties.isScrap)
                    continue;
                // Corpses are paid by ComputeLocalBodyRetrievalXp, at the flat body rate, under
                // this same key. Excluded here so a body whose Item asset happens to be marked
                // scrap cannot also draw the per-value delivery rate for the same haul.
                if (item is RagdollGrabbableObject)
                    continue;
                if (!IsInsideShip(item))
                    continue;

                ulong key = GetNetworkKey(item);
                if (!counted.Add(key)
                    || !_round.Delivery.ShouldPayDelivery(key, insideAtRoundEnd: true, local.actualClientId))
                {
                    continue;
                }

                attributedValue = AddClamped(attributedValue, Mathf.Max(0, item.scrapValue));
                deliveredCount = AddClamped(deliveredCount, 1);
            }

            int xp = Mathf.CeilToInt(
                attributedValue * Mathf.Max(0f, Plugin.ProgressionConfig.ScrapDeliveredXpPerValue.Value));
            xp = Mathf.Max(0, xp);

            // #456: one delivery line per round, stating the haul's credits. The XP is rounded
            // once over the whole attributed value, so the line carries exactly what was paid
            // and no per-item division can drift away from the bar.
            if (xp > 0 && deliveredCount > 0)
            {
                _round.AddReportSource(
                    "scrap.delivered",
                    ProgressionReportSourceKind.ScrapDelivered,
                    string.Empty,
                    deliveredCount,
                    xp,
                    attributedValue);
            }

            LogDebug($"Individual delivered scrap value={attributedValue} xp={xp}.");
            return xp;
        }

        /// <summary>
        /// Base XP for teammates' corpses the local player carried aboard (#219), before the moon
        /// risk multiplier.
        ///
        /// WHEN THIS RUNS. Finalization reaches this from two triggers, and both are late enough:
        /// <c>RoundLifecycle.RoundEnded</c> fires from <c>StartOfRound.EndOfGame</c>, which the
        /// host starts only after <c>unloadSceneForAllPlayers</c> has waited out its two-second
        /// delay and the scene unload, and <c>HUDManager.FillEndGameStats</c> is later still.
        /// <c>ElevatorAnimationEvents.ElevatorFullyRunning</c> — which force-sets
        /// <c>isInShip</c> on every ragdoll standing inside <c>shipBounds</c> and then freezes it
        /// — runs at the top of that sequence, immediately after <c>ShipHasLeft</c>. A body that
        /// rode up in the ship therefore reads <c>isInShip</c> by the time this pass looks, both
        /// through that pass and through the ragdoll's own parenting (a corpse dropped in the ship
        /// is re-parented to the elevator, which is what keeps the flag true frame after frame).
        ///
        /// WHAT IT ENUMERATES. <c>RagdollGrabbableObject</c>, not
        /// <c>StartOfRound.GetBodiesInShip</c>: the vanilla helper is host-only and returns a
        /// count, and this payout has to be computed identically on every machine. The grabbable
        /// is the network-spawned half of a corpse (so it carries the ledger key) and it holds a
        /// direct reference to its ragdoll, so one sweep answers both "whose body" and "is it
        /// aboard". Active objects only: a corpse whose object has been deactivated is not a
        /// retrieval, and the ragdoll's own <c>deactivated</c> flag is checked besides.
        ///
        /// IDENTITY IS THE CORPSE INSTANCE, not the player. A player who is revived and dies
        /// again leaves a second, different corpse; that is a second genuine retrieval, and the
        /// first corpse's record must not pay for it. Instance keys give both properties for
        /// free, because the key dies with the object.
        ///
        /// A body revived by Inspire is not here at all: <c>ForemanPingPatch</c> destroys the
        /// DeadBodyInfo, and the grabbable is parented under it, so both are gone. Its ledger
        /// record simply goes unread, which is exactly the "no body, no pay" outcome.
        /// </summary>
        private static int ComputeLocalBodyRetrievalXp()
        {
            if (Plugin.ProgressionConfig.BodyRetrievalEnabled.Value == false)
                return 0;

            int perBody = Mathf.Max(0, Plugin.ProgressionConfig.BodyRetrievalXp.Value);
            if (perBody <= 0)
                return 0;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local == null)
                return 0;

            int retrieved = 0;
            var counted = new HashSet<ulong>();
            RagdollGrabbableObject[] bodies = UnityEngine.Object.FindObjectsOfType<RagdollGrabbableObject>();
            for (int i = 0; i < bodies.Length; i++)
            {
                RagdollGrabbableObject body = bodies[i];
                if (body == null || body.ragdoll == null)
                    continue;

                ulong key = GetNetworkKey(body);
                bool ownerResolved = TryResolveBodyOwner(body, out ulong ownerClientId);
                if (!DeliveryAttributionLedger.ShouldPayBodyRetrieval(
                        _round.Delivery.ShouldPayDelivery(key, insideAtRoundEnd: true, local.actualClientId),
                        IsAboard(body),
                        body.ragdoll.deactivated,
                        ownerResolved,
                        payeeOwnsBody: ownerResolved && ownerClientId == local.actualClientId))
                {
                    continue;
                }

                // Dedupe AFTER the credit decision, and per key rather than per object. With
                // instance keys two corpses can never share one, so this cannot currently fire;
                // it is kept so that a future change to key semantics degrades into "paid once"
                // rather than "paid per duplicate".
                if (!counted.Add(key))
                    continue;

                retrieved++;
                // Count and name captured here rather than re-derived later so the report note
                // can only ever describe bodies this pass actually paid for (#220). The count is
                // kept separately because an unresolvable username still paid.
                _round.RetrievedBodyCount++;
                string ownerName = ResolveBodyOwnerName(body, ownerClientId);
                if (!string.IsNullOrWhiteSpace(ownerName))
                    _round.RetrievedBodyNames.Add(ownerName);
                LogDebug($"Body retrieval credited for corpse key {key} (body id {body.bodyID}).");
            }

            return retrieved * perBody;
        }

        /// <summary>
        /// The client id of the player whose corpse this is, for the self-retrieval exclusion:
        /// hauling your own body aboard through a revive or teleporter edge case is not a rescue
        /// and pays nothing.
        ///
        /// FAILS CLOSED. <c>bodyID</c> indexes <c>allPlayerScripts</c>, and that lookup can come
        /// back empty — an id that has not arrived over RPC yet, a slot outside the array in a
        /// modded lobby, a player object torn down by a mid-round disconnect. Returning "not your
        /// body" there would PAY on an unresolved owner, so an unresolvable owner suppresses the
        /// award instead, matching the ledger's own rule that an unattributable crossing pays
        /// nobody. The ragdoll's own <c>playerScript</c> back-reference is tried first because it
        /// is set from the body itself and needs no id at all.
        /// </summary>
        private static bool TryResolveBodyOwner(RagdollGrabbableObject body, out ulong ownerClientId)
        {
            ownerClientId = 0;
            if (body == null)
                return false;

            PlayerControllerB owner = body.ragdoll != null ? body.ragdoll.playerScript : null;
            if (owner == null)
            {
                PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
                if (players == null || body.bodyID < 0 || body.bodyID >= players.Length)
                    return false;

                owner = players[body.bodyID];
            }

            if (owner == null)
                return false;

            ownerClientId = owner.actualClientId;
            return true;
        }

        /// <summary>
        /// Display name for a paid corpse's owner, for the report note ("Recovered Aldo's body").
        /// The owner id is already resolved by the payout — an unresolved owner never pays — so
        /// this only turns it into a username, preferring the ragdoll's own back-reference.
        /// Returns empty when no readable name exists, and the caller then omits the name rather
        /// than printing a slot number.
        /// </summary>
        private static string ResolveBodyOwnerName(RagdollGrabbableObject body, ulong ownerClientId)
        {
            PlayerControllerB owner = body?.ragdoll != null ? body.ragdoll.playerScript : null;
            if (owner == null)
            {
                PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
                if (players != null)
                {
                    for (int i = 0; i < players.Length; i++)
                    {
                        if (players[i] != null && players[i].actualClientId == ownerClientId)
                        {
                            owner = players[i];
                            break;
                        }
                    }
                }
            }

            string name = owner != null ? owner.playerUsername : null;
            return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        }

        private static int GetEnemyKillXp(EnemyAI enemy)
        {
            int power = 1;
            try
            {
                if (enemy.enemyType != null)
                    power = Mathf.Max(1, Mathf.RoundToInt(enemy.enemyType.PowerLevel));
            }
            catch
            {
                power = 1;
            }

            int xp = power * Mathf.Max(1, Plugin.ProgressionConfig.MonsterKillXpPerPower.Value);
            return Mathf.Clamp(
                xp,
                Mathf.Max(0, Plugin.ProgressionConfig.MonsterKillMinXp.Value),
                Mathf.Max(0, Plugin.ProgressionConfig.MonsterKillMaxXp.Value));
        }

        private static string GetCurrentMoonRisk()
        {
            string risk = StartOfRound.Instance?.currentLevel?.riskLevel;
            if (string.IsNullOrWhiteSpace(risk))
                return "C";
            return risk.Trim().ToUpperInvariant();
        }

        private static float GetMoonMultiplier(string risk)
        {
            if (string.IsNullOrWhiteSpace(risk))
                return 1f;

            risk = risk.Trim().ToUpperInvariant();
            if (risk.StartsWith("S", StringComparison.Ordinal))
                return Mathf.Max(0f, Plugin.ProgressionConfig.RiskMultiplierS.Value);
            if (risk.StartsWith("A", StringComparison.Ordinal))
                return Mathf.Max(0f, Plugin.ProgressionConfig.RiskMultiplierA.Value);
            if (risk.StartsWith("B", StringComparison.Ordinal))
                return Mathf.Max(0f, Plugin.ProgressionConfig.RiskMultiplierB.Value);
            if (risk.StartsWith("D", StringComparison.Ordinal))
                return Mathf.Max(0f, Plugin.ProgressionConfig.RiskMultiplierD.Value);
            return Mathf.Max(0f, Plugin.ProgressionConfig.RiskMultiplierC.Value);
        }

        private static bool IsEnabled()
        {
            return Plugin.ProgressionConfig?.Enabled?.Value == true;
        }

        private static void WriteStatesToDisk()
        {
            Directory.CreateDirectory(SaveDir);
            var lines = new List<string>(States.Count);
            foreach (KeyValuePair<string, ProgressionState> pair in States)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                ProgressionState state = pair.Value ?? new ProgressionState();
                lines.Add($"{pair.Key}|{RankCatalog.ClampXp(state.RankXp)}|{Mathf.Max(0, state.UpgradeCurrency)}|{EncodeExternalGrantIds(state.ExternalTokenGrantIds)}|{EncodeExternalGrantIds(state.CompletedTokenSpendIds)}");
            }

            SaveDataFile.WriteAllLinesAtomic(SavePath, lines);
        }

        private static string EncodeExternalGrantIds(IEnumerable<string> actionIds)
        {
            if (actionIds == null)
                return string.Empty;

            return string.Join(",", actionIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => Convert.ToBase64String(Encoding.UTF8.GetBytes(id))));
        }

        private static IEnumerable<string> DecodeExternalGrantIds(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded))
                yield break;

            string[] values = encoded.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < values.Length; i++)
            {
                string value;
                try
                {
                    value = Encoding.UTF8.GetString(Convert.FromBase64String(values[i]));
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(value))
                    yield return value.Trim();
            }
        }

        private static int ReservedCurrencyForCurrentSave()
        {
            if (string.IsNullOrWhiteSpace(_currentSaveKey))
                return 0;

            long total = 0;
            foreach (TokenReservation reservation in TokenReservations.Values)
            {
                if (string.Equals(reservation.SaveKey, _currentSaveKey, StringComparison.OrdinalIgnoreCase))
                    total += reservation.Amount;
            }
            return (int)Math.Min(int.MaxValue, total);
        }

        private static string NormalizeTransactionId(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= 128 ? value : value.Substring(0, 128);
        }

        private static void TrimCompletedTokenSpends(HashSet<string> ids)
        {
            if (ids == null || ids.Count <= MaxCompletedTokenSpends)
                return;

            foreach (string id in ids.OrderBy(value => value, StringComparer.Ordinal).Take(ids.Count - MaxCompletedTokenSpends).ToArray())
                ids.Remove(id);
        }

        private readonly struct TokenReservation
        {
            internal readonly string SaveKey;
            internal readonly int Amount;

            internal TokenReservation(string saveKey, int amount)
            {
                SaveKey = saveKey;
                Amount = amount;
            }
        }

        private static PlayerControllerB FindPlayerByActualClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == clientId)
                    return player;
            }

            return null;
        }

        private static int AddClamped(int left, int right)
        {
            long total = (long)Math.Max(0, left) + Math.Max(0, right);
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        private static int SumPositive(params int[] values)
        {
            long total = 0;
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] <= 0)
                        continue;
                    total += values[i];
                    if (total >= int.MaxValue)
                        return int.MaxValue;
                }
            }

            return (int)total;
        }

        private static ulong GetNetworkKey(Component component)
        {
            return NetworkObjectKey.For(component);
        }

        private static string GetItemName(GrabbableObject item)
        {
            return item?.itemProperties?.itemName ?? item?.name ?? "scrap";
        }

        private static string GetEnemyName(EnemyAI enemy)
        {
            return enemy?.enemyType?.enemyName ?? enemy?.gameObject?.name ?? "enemy";
        }

        private static void LogDebug(string message)
        {
            if (Plugin.ProgressionConfig?.DebugLogging?.Value == true)
                Plugin.Log?.LogInfo("[Progression] " + message);
        }

        private sealed class ReportSourceAccumulator
        {
            internal readonly string Key;
            internal readonly ProgressionReportSourceKind Kind;
            internal readonly int Order;
            internal string Subject;
            internal int Count;
            internal int Amount;
            internal int Value;

            internal ReportSourceAccumulator(
                string key,
                ProgressionReportSourceKind kind,
                string subject,
                int order)
            {
                Key = key;
                Kind = kind;
                Subject = subject;
                Order = order;
            }
        }

        private sealed class RoundTracker
        {
            internal float RoundStartTime;
            internal bool LocalPlayerDied;
            internal float LocalPlayerDeathTime;
            internal int ScrapPickupXp;
            internal int ScrapDeliveredXp;
            internal int MonsterKillXp;
            internal int SurvivalXp;
            internal int SurvivalMinutesForReport = 1;
            internal int MainframeHackXp;
            internal int ClutchXp;
            /// <summary>Ship Systems battery restores paid to the local player (#459).</summary>
            internal int ShipBatteryXp;
            internal int ShipBatteryRestoresPaid;
            internal readonly HashSet<string> ShipBatteryRestoreKeys = new HashSet<string>(StringComparer.Ordinal);
            internal int ContractXp;
            /// <summary>
            /// The part of <see cref="ContractXp"/> earned by ACTS rather than by a contract
            /// outcome; the participation half of the #457 completion gate.
            /// </summary>
            internal int ContractActXp;
            /// <summary>Base body-retrieval XP, computed at finalize (#219).</summary>
            internal int BodyRetrievalXp;
            internal string MoonRisk = "C";
            internal float MoonMultiplier = 1f;
            /// <summary>The pin never took: no save key resolved when the round began.</summary>
            internal const int UnpinnedRank = -1;
            /// <summary>Rank held when the round began; the completion award's width source.</summary>
            internal int StartRankIndex = UnpinnedRank;
            internal readonly Dictionary<ulong, ulong> FirstScrapDiscovererByKey = new Dictionary<ulong, ulong>();
            internal readonly DeliveryAttributionLedger Delivery = new DeliveryAttributionLedger();
            internal readonly HashSet<ulong> KilledEnemies = new HashSet<ulong>();
            internal readonly HashSet<ulong> HackedMainframes = new HashSet<ulong>();
            internal readonly HashSet<ulong> FacilityEntrants = new HashSet<ulong>();
            internal readonly Dictionary<ulong, float> LastEnemyHitByLocalPlayer = new Dictionary<ulong, float>();
            internal readonly HashSet<string> ContractXpKeys = new HashSet<string>();

            /// <summary>
            /// #392: raw contract XP per published event kind, in first-award order, so finalize
            /// can divide the scaled bucket back across the kinds and hand each act's share to
            /// Company's report line for it. The scalar <see cref="ContractXp"/> stays the
            /// payout authority; this is a reporting view of the same numbers. The order list
            /// keeps the largest-remainder division deterministic. The empty key is the "no
            /// kind named" bucket (the public AwardContractXp surface): it takes its share of
            /// the division and reaches no line. #1201: a crew award is bucketed apart from the
            /// same kind's own acts, under <see cref="ProgressionReportLineKeys.ContractBucket"/>'s
            /// <c>crew.</c> key, so its share reaches the report as hidden XP.
            /// </summary>
            internal readonly List<string> ContractXpKindOrder = new List<string>();
            internal readonly Dictionary<string, int> ContractXpByKind =
                new Dictionary<string, int>(StringComparer.Ordinal);

            internal void AddContractXpForKind(string bucket, int amount)
            {
                if (amount <= 0)
                    return;

                bucket = bucket == null ? string.Empty : bucket.Trim();
                if (!ContractXpByKind.TryGetValue(bucket, out int existing))
                    ContractXpKindOrder.Add(bucket);
                ContractXpByKind[bucket] = AddClamped(existing, amount);
            }
            /// <summary>Occurrences already PAID per count-capped contract bucket (#194).</summary>
            internal readonly Dictionary<string, int> CappedContractCredits =
                new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>
            /// Usernames of the teammates whose corpses the local player brought aboard this
            /// round, in sweep order. Computed with the body-retrieval payout so the report note
            /// names exactly the bodies that were actually paid for (#219/#220). This can be
            /// SHORTER than <see cref="RetrievedBodyCount"/>: an owner whose username does not
            /// resolve still pays, so the count is tracked on its own.
            /// </summary>
            internal readonly List<string> RetrievedBodyNames = new List<string>();

            /// <summary>Corpses that actually paid the local player this round.</summary>
            internal int RetrievedBodyCount;

            private readonly List<ReportSourceAccumulator> _reportSourceOrder =
                new List<ReportSourceAccumulator>();
            private readonly Dictionary<string, ReportSourceAccumulator> _reportSources =
                new Dictionary<string, ReportSourceAccumulator>(StringComparer.Ordinal);

            /// <summary>
            /// Folds one award into its category line (#456). A second, different subject under
            /// the same key clears the subject: the category kept a name only while every award
            /// in it was the same thing ("Killed 2 thumpers"), and reads generically once the
            /// round mixed them ("Killed 3 monsters"). <paramref name="value"/> accumulates the
            /// credits a haul line states.
            /// </summary>
            internal void AddReportSource(
                string key,
                ProgressionReportSourceKind kind,
                string subject,
                int count,
                int amount,
                int value = 0)
            {
                if (amount <= 0 || string.IsNullOrWhiteSpace(key))
                    return;

                string resolvedSubject = subject?.Trim() ?? string.Empty;
                if (!_reportSources.TryGetValue(key, out ReportSourceAccumulator source))
                {
                    source = new ReportSourceAccumulator(
                        key,
                        kind,
                        resolvedSubject,
                        _reportSourceOrder.Count);
                    _reportSources.Add(key, source);
                    _reportSourceOrder.Add(source);
                }
                else if (!string.Equals(source.Subject, resolvedSubject, StringComparison.OrdinalIgnoreCase))
                {
                    source.Subject = string.Empty;
                }

                source.Count = AddClamped(source.Count, count);
                source.Amount = AddClamped(source.Amount, amount);
                source.Value = AddClamped(source.Value, value);
            }

            internal IReadOnlyList<ProgressionReportSource> BuildReportSources()
            {
                var result = new ProgressionReportSource[_reportSourceOrder.Count];
                for (int i = 0; i < _reportSourceOrder.Count; i++)
                {
                    ReportSourceAccumulator source = _reportSourceOrder[i];
                    result[i] = new ProgressionReportSource(
                        source.Key,
                        source.Kind,
                        source.Subject,
                        source.Count,
                        source.Amount,
                        source.Order,
                        source.Value);
                }

                return result;
            }

        }
    }
}
