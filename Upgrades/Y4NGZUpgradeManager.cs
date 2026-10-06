using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Y4NGZUpgrades.Upgrades
{
    public enum Y4NGZUpgradePurchaseResult
    {
        Success,
        UnknownUpgrade,
        Maxed,
        GateLocked,
        PrerequisiteLocked,
        NotEnoughCurrency,
        /// <summary>No save key resolves yet, so the purchase could not be persisted. #213.</summary>
        SaveUnavailable,
        /// <summary>The live price no longer matches the price the caller quoted. #213.</summary>
        PriceChanged,
        ProviderUnavailable
    }

    public static class Y4NGZUpgradeManager
    {
        private sealed class ProgressionWallet : IUpgradeWallet
        {
            internal static readonly ProgressionWallet Instance = new ProgressionWallet();

            public bool TryReserve(string transactionId, int amount)
            {
                return ProgressionApi.TryReserveTokenSpend(transactionId, amount);
            }

            public bool Commit(string transactionId)
            {
                return ProgressionApi.CommitTokenSpend(transactionId);
            }

            public void Cancel(string transactionId)
            {
                ProgressionApi.CancelTokenSpend(transactionId);
            }
        }

        private static readonly Dictionary<string, Y4NGZUpgradeDefinition> Definitions =
            new Dictionary<string, Y4NGZUpgradeDefinition>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Y4NGZUpgradeNode> Nodes =
            new Dictionary<string, Y4NGZUpgradeNode>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Y4NGZSkillTreeClass, Y4NGZSkillTreeDefinition> SkillTrees =
            new Dictionary<Y4NGZSkillTreeClass, Y4NGZSkillTreeDefinition>();
        private static readonly Dictionary<string, Y4NGZSkillTreeNodeDefinition> SkillTreeNodes =
            new Dictionary<string, Y4NGZSkillTreeNodeDefinition>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Y4NGZUpgradeMigrationRule> MigrationRules =
            new Dictionary<string, Y4NGZUpgradeMigrationRule>(StringComparer.OrdinalIgnoreCase);
        // Memoized name/id resolution. Accessors call IsUnlocked()/GetTier() every
        // frame from PlayerControllerB.Update patches; NormalizeId allocates several
        // strings per call, so successful resolutions are cached to keep the
        // steady-state path allocation-free. Cleared with ClearDefinitions().
        private static readonly Dictionary<string, string> ExactIdCache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ResolvedIdCache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly string[] PhysicalConditioningMergeIds =
        {
            "physical_conditioning",
            "reinforced_boots",
            "climbing_gloves"
        };
        private static readonly string[] LightFeetMergeIds =
        {
            "light_feet",
            "dont_shoot",
            "don_t_shoot",
            "grounded",
            "spatial_awareness"
        };
        private static readonly string[] RemovedUpgradeIds =
        {
            "aluminium_coils",
            "escape_protocol",
            "glow_in_the_dark",
            "lithium_batteries",
            "npc",
            "locksmith",
            "mechanical_arms",
            "sleight_of_hand",
            "predator_instinct",
            "salvager",
            "bandaids",
            "stimpack",
            "deeper_pockets",
            "shoulder_check",
            "pipe_bomb",
            "bait_bomb",
            "bait_beacon",
            "flare_burst",
            "rally_call"
        };

        // Sub-tiers that shipped and were later removed. NormalizeStateLevels already drops a
        // saved level to the definition's MaxTier on its own, so all that is owed is the tokens,
        // paid once per save through the ProgressionManager ledger.
        private sealed class RemovedTierRefund
        {
            internal RemovedTierRefund(string displayName, string upgradeId, int removedTier, int tokens, string grantId)
            {
                DisplayName = displayName;
                UpgradeId = upgradeId;
                RemovedTier = removedTier;
                Tokens = tokens;
                GrantId = grantId;
            }

            internal string DisplayName { get; }
            internal string UpgradeId { get; }
            internal int RemovedTier { get; }
            internal int Tokens { get; }
            internal string GrantId { get; }
        }

        private static readonly RemovedTierRefund[] RemovedTierRefunds =
        {
            // Veteran shipped a third tier that QuotaGuardUpgrade never read. F-INFRA-11: the 14
            // here was the pre-#217 authored price, but #217 replaced authored prices with the
            // tier-derived rule (tierPrices[i] = tier + 1 + i), so on this tree-tier-4 row the
            // 2 -> 3 step actually cost 4 + 2 = 6 tokens. Refund what was charged.
            new RemovedTierRefund("Veteran", "quota_guard", 3, 6, "quota_guard_tier3_refund"),
            // Pumping Iron dropped to two sub-tiers in #364. Under the tier-derived rule its
            // 2 -> 3 step cost tree tier 2 + 2 = 4 tokens.
            new RemovedTierRefund("Pumping Iron", "protein_powder", 3, 4, "protein_powder_tier3_refund")
        };

        // saveKey -> indices into RemovedTierRefunds that are owed but not yet paid.
        private static readonly Dictionary<string, HashSet<int>> PendingRemovedTierRefunds =
            new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        private static readonly string SaveDir =
            Path.Combine(Application.persistentDataPath, "Y4NGZUpgrades");
        private static readonly string SavePath =
            Path.Combine(SaveDir, "upgrade_levels.txt");

        // Owns the per-save levels and which save is live. While no save key resolves the store has
        // no current state at all, so a level can never be written somewhere unpersistable. #213.
        private static readonly UpgradeSaveStateStore Store =
            new UpgradeSaveStateStore(SaveKey.TryGetCurrent);

        // Last save scope announced by LogResolvedSaveScope, so the line lands once per lobby.
        private static string _loggedSaveScopeKey;

        public static event Action UpgradesChanged;

        /// <summary>
        /// Republishes the upgrade set after something other than a purchase changed which rows
        /// exist - a second catalog pass, for instance, when an optional provider turned out not to
        /// support a row after all (#435). Levels are untouched; this only tells every consumer to
        /// re-read them.
        /// </summary>
        internal static void RaiseUpgradesChanged()
        {
            UpgradesChanged?.Invoke();
        }

        public static int Currency => ProgressionApi.UpgradeCurrency;

        private static NativeUpgradePolicy _policy = NativeUpgradePolicy.AllFull;

        /// <summary>
        /// The one resolved snapshot of which native families sell what (#435). Published by
        /// <see cref="Y4NGZUpgradeCatalog.RegisterDefaults"/> before any row is registered, so the
        /// catalog, the effects, the prices and the menu can never disagree about the active mode.
        /// </summary>
        internal static NativeUpgradePolicy CurrentPolicy
        {
            get => _policy;
            set => _policy = value ?? NativeUpgradePolicy.AllFull;
        }

        internal static NativeFamilyMode ModeOf(string upgradeId) => CurrentPolicy.ModeOf(upgradeId);

        /// <summary>
        /// True while this family's identity is still unsettled because Late Game Upgrades is
        /// installed but has not finished loading. Selling a rank now would sell an effect that is
        /// about to change meaning.
        /// </summary>
        internal static bool IsFamilyPending(string upgradeId) => CurrentPolicy.IsPending(upgradeId);

        internal static void Load()
        {
            Store.Clear();
            PendingRemovedTierRefunds.Clear();

            bool loaded = true;
            try
            {
                if (File.Exists(SavePath))
                {
                    foreach (string line in File.ReadAllLines(SavePath))
                    {
                        if (!UpgradeSaveState.TryDecode(
                                line,
                                LguUpgradeBridge.IsBridgedUpgrade,
                                out string saveKey,
                                out UpgradeSaveState state))
                        {
                            continue;
                        }

                        // Recorded before NormalizeStateLevels silently clamps the removed tiers.
                        RecordRemovedTierRefunds(saveKey, state);

                        Store.SetState(saveKey, state);
                    }
                }
            }
            catch (Exception ex)
            {
                loaded = false;
                Plugin.Log?.LogError($"[Y4NGZUpgrades] Failed to load upgrade levels: {ex}");
            }

            // ProgressionManager.Load() runs first (Plugin.cs), so every save's token ledger is
            // already in memory and the refund can be paid for all affected saves at once.
            FlushPendingRemovedTierRefunds();

            // #493: Late Game Upgrades installed but not integrated sells the imported rows for
            // credits this session. Every save's one-time import is re-armed and written now, so
            // the next integrated load merges what was bought (the larger of the token rank and
            // LGU's) instead of driving it away. Token levels are untouched. After the refund
            // flush, because the write normalizes away removed-tier evidence; a file that did not
            // load completely is never rewritten from here.
            if (loaded && !Plugin.LguIntegrationActive
                && OptionalPluginCapabilities.IsLoaded(OptionalPluginCapabilities.LateGameUpgradesGuid))
            {
                try
                {
                    int rearmed = Store.RearmLguImports();
                    if (rearmed > 0)
                    {
                        WriteStatesToDisk();
                        Plugin.Log?.LogInfo(
                            $"[LGU] Integration is off; re-armed the one-time Late Game Upgrades import on {rearmed} save(s).");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"[Y4NGZUpgrades] Failed to re-arm the Late Game Upgrades import: {ex}");
                }
            }

            SwitchToCurrentSave();
        }

        /// <summary>
        /// Notes every removed sub-tier this save had already bought. Must be called before
        /// NormalizeStateLevels, which clamps the level and erases the evidence.
        /// </summary>
        private static void RecordRemovedTierRefunds(string saveKey, UpgradeSaveState state)
        {
            if (string.IsNullOrWhiteSpace(saveKey) || state == null)
                return;

            for (int i = 0; i < RemovedTierRefunds.Length; i++)
            {
                RemovedTierRefund refund = RemovedTierRefunds[i];
                if (!state.Levels.TryGetValue(refund.UpgradeId, out int level)
                    || level < refund.RemovedTier)
                {
                    continue;
                }

                if (!PendingRemovedTierRefunds.TryGetValue(saveKey, out HashSet<int> pending))
                {
                    pending = new HashSet<int>();
                    PendingRemovedTierRefunds[saveKey] = pending;
                }

                pending.Add(i);
            }
        }

        private static void FlushPendingRemovedTierRefunds(string onlySaveKey = null)
        {
            if (PendingRemovedTierRefunds.Count == 0)
                return;

            foreach (string saveKey in PendingRemovedTierRefunds.Keys.ToArray())
            {
                if (onlySaveKey != null && !string.Equals(saveKey, onlySaveKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                HashSet<int> pending = PendingRemovedTierRefunds[saveKey];
                foreach (int index in pending.ToArray())
                {
                    RemovedTierRefund refund = RemovedTierRefunds[index];
                    if (!ProgressionManager.TryApplyLedgeredTokenGrant(
                            saveKey,
                            refund.GrantId,
                            refund.Tokens))
                    {
                        continue;
                    }

                    pending.Remove(index);
                    Plugin.Log?.LogInfo(
                        $"[Y4NGZUpgrades] {refund.DisplayName} tier {refund.RemovedTier} was removed; save '{saveKey}' drops to level {refund.RemovedTier - 1} and is refunded {refund.Tokens} token(s).");
                }

                if (pending.Count == 0)
                    PendingRemovedTierRefunds.Remove(saveKey);
            }
        }

        internal static void Save()
        {
            try
            {
                // The switch writes the live state back into the store before re-pointing, so
                // pending mutations survive a save that lands on a different (or no) key.
                if (!SwitchToCurrentSave())
                    return;

                WriteStatesToDisk();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Y4NGZUpgrades] Failed to save upgrade levels: {ex}");
            }
        }

        /// <summary>True once a save key resolves and the live state is addressable.</summary>
        internal static bool HasCurrentSave => Store.Current != null;
        internal static string CurrentSaveKey => Store.CurrentKey;

        internal static bool ImportLguLevel(string id, int level)
        {
            return Store.Current != null && Store.Current.ImportLguLevel(id, Math.Max(0, level));
        }

        internal static void ClearLguImport(string id)
        {
            Store.Current?.ClearLguImport(id);
        }

        // Import must reach disk before the bridge retires LGU's copy of these ranks.
        internal static void PersistLguImport()
        {
            if (Store.Current == null)
                throw new InvalidOperationException("No save for LGU ownership migration.");
            WriteStatesToDisk();
        }

        internal static bool SwitchToCurrentSave()
        {
            string previousKey = Store.CurrentKey;
            if (!Store.SwitchToCurrentSave())
                return false;

            UpgradeSaveState state = Store.Current;

            // Both of these are ledgered per save in ProgressionManager, so re-running them on
            // every save switch is a no-op after the first application.
            RecordRemovedTierRefunds(Store.CurrentKey, state);
            FlushPendingRemovedTierRefunds(Store.CurrentKey);

            NormalizeStateLevels(state);
            TryImportLegacyLevels();
            ProgressionManager.ApplyOverachieverRankBonusBackfill();

            // F-INFRA-3: a save-scope change is an upgrade change for everyone downstream. Without
            // this a joining client's real tiers never reached the host (it had cached the all-zero
            // set published while the store was unpointed) and Chameleon, Scavenger, Veteran, Field
            // Mechanic and Field Operations silently did nothing for every non-host player.
            if (!string.Equals(previousKey, Store.CurrentKey, StringComparison.OrdinalIgnoreCase))
            {
                LogResolvedSaveScope(Store.CurrentKey);
                UpgradesChanged?.Invoke();
            }

            return true;
        }

        /// <summary>
        /// F-INFRA-7. Joining a friend's lobby moves every store onto the HOST's save, which is by
        /// design but reads as lost progress. Say which profile is live, once per scope.
        /// </summary>
        private static void LogResolvedSaveScope(string key)
        {
            if (string.IsNullOrWhiteSpace(key)
                || string.Equals(key, _loggedSaveScopeKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _loggedSaveScopeKey = key;
            if (key.StartsWith(HostSaveIdentity.HostKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log?.LogInfo(
                    "[Y4NGZUpgrades] Upgrade profile: " + DescribeSaveScope(key)
                    + ". Your own solo progress is untouched and returns when you host or play alone.");
                return;
            }

            Plugin.Log?.LogInfo("[Y4NGZUpgrades] Upgrade profile: " + DescribeSaveScope(key) + ".");
        }

        /// <summary>
        /// F-INFRA-7: the resolved profile as one line, for the purchase-menu header. The log
        /// above and the menu must never disagree, so both derive it from the live store key.
        /// </summary>
        internal static string DescribeResolvedSaveScope() => DescribeSaveScope(Store.CurrentKey);

        private static string DescribeSaveScope(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "no active save";

            if (key.StartsWith(HostSaveIdentity.HostKeyPrefix, StringComparison.OrdinalIgnoreCase))
                return "crew save of host " + key.Substring(HostSaveIdentity.HostKeyPrefix.Length);

            int separator = key.IndexOf('#');
            return "solo save " + (separator > 0 ? key.Substring(0, separator) : key);
        }

        internal static void ResetCurrentSave()
        {
            if (!SaveKey.TryGetCurrentExistingOrSlot(out string key))
            {
                Plugin.Log?.LogInfo("[Y4NGZUpgrades] Upgrade reset skipped; no active save file.");
                return;
            }

            DeleteForKeys(new[] { key });
            Plugin.Log?.LogInfo($"[Y4NGZUpgrades] Reset upgrade levels for save '{key}'.");
        }

        internal static IReadOnlyCollection<string> GetKnownSaveKeys()
        {
            return Store.Keys;
        }

        /// <summary>
        /// Clears whole saves. Both ownership records live on the same state object, so a reset
        /// takes the unique-only ranks with the full-native ones and nothing can be re-granted
        /// from metadata left behind (#435).
        /// </summary>
        internal static int DeleteForKeys(IEnumerable<string> keys)
        {
            int removed = Store.RemoveKeys(keys);
            if (removed > 0)
            {
                WriteStatesToDisk();
                UpgradesChanged?.Invoke();
            }
            return removed;
        }

        internal static void ClearDefinitions()
        {
            Definitions.Clear();
            Nodes.Clear();
            SkillTrees.Clear();
            SkillTreeNodes.Clear();
            MigrationRules.Clear();
            ExactIdCache.Clear();
            ResolvedIdCache.Clear();
        }

        internal static void RegisterDefinition(Y4NGZUpgradeDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.Id))
                return;

            Definitions[definition.Id] = definition;
            Nodes[definition.Id] = new Y4NGZUpgradeNode(definition);
            InvalidateResolutionCaches();
        }

        internal static void RegisterSkillTree(Y4NGZSkillTreeDefinition definition)
        {
            if (definition == null)
                return;

            SkillTrees[definition.TreeClass] = definition;
        }

        internal static void RegisterSkillTreeNode(Y4NGZSkillTreeNodeDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.UpgradeId))
                return;

            SkillTreeNodes[definition.UpgradeId] = definition;
        }

        internal static void RegisterMigrationRule(Y4NGZUpgradeMigrationRule rule)
        {
            if (rule == null
                || string.IsNullOrWhiteSpace(rule.SourceId)
                || string.IsNullOrWhiteSpace(rule.TargetId)
                || string.Equals(rule.SourceId, rule.TargetId, StringComparison.OrdinalIgnoreCase))
                return;

            MigrationRules[rule.SourceId] = rule;
            InvalidateResolutionCaches();
        }

        /// <summary>
        /// F-FOREMAN-A-7. Misses are memoized too, so anything that can change a resolution -
        /// registering a definition or a migration rule - has to drop both caches, otherwise a
        /// late-registered row stays invisible for the rest of the session.
        /// </summary>
        private static void InvalidateResolutionCaches()
        {
            ExactIdCache.Clear();
            ResolvedIdCache.Clear();
        }

        public static IReadOnlyList<Y4NGZUpgradeNode> GetUpgradeNodes()
        {
            return Nodes.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static IReadOnlyList<Y4NGZSkillTreeDefinition> GetSkillTrees()
        {
            return SkillTrees.Values.OrderBy(x => x.Order).ToList();
        }

        public static IReadOnlyList<Y4NGZSkillTreeNodeDefinition> GetSkillTreeNodes()
        {
            return SkillTreeNodes.Values
                .OrderBy(x => SkillTrees.TryGetValue(x.TreeClass, out Y4NGZSkillTreeDefinition tree) ? tree.Order : int.MaxValue)
                .ThenBy(x => x.Tier)
                .ThenBy(x => x.Row)
                .ThenBy(x => x.Column)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The class tree's own nodes. Rows the player moved into the flat Augments catalog
        /// (#435) are not part of any tree: no branch lines, no gates, no investment.
        /// </summary>
        public static IReadOnlyList<Y4NGZSkillTreeNodeDefinition> GetSkillTreeNodes(Y4NGZSkillTreeClass treeClass)
        {
            return SkillTreeNodes.Values
                .Where(x => x.TreeClass == treeClass && !x.FlatCatalog)
                .OrderBy(x => x.Tier)
                .ThenBy(x => x.Row)
                .ThenBy(x => x.Column)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The flat Augments catalog, in the same order the trees would have listed these rows.
        /// Empty unless the catalog registered imported rows flat: the separate-catalog layout,
        /// or LGU-only mode with Late Game Upgrades installed (#435).
        /// </summary>
        public static IReadOnlyList<Y4NGZSkillTreeNodeDefinition> GetFlatCatalogNodes()
        {
            return SkillTreeNodes.Values
                .Where(x => x.FlatCatalog)
                .OrderBy(x => SkillTrees.TryGetValue(x.TreeClass, out Y4NGZSkillTreeDefinition tree) ? tree.Order : int.MaxValue)
                .ThenBy(x => x.Tier)
                .ThenBy(x => x.Row)
                .ThenBy(x => x.Column)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static Y4NGZSkillTreeNodeDefinition GetSkillTreeNode(string idOrName)
        {
            string id = ResolveId(idOrName);
            if (id != null && SkillTreeNodes.TryGetValue(id, out Y4NGZSkillTreeNodeDefinition node))
                return node;

            // A dormant native id must not select its imported namesake after a mode change.
            if (IsCatalogStableId(idOrName))
                return null;

            string normalized = Y4NGZUpgradeDefinition.NormalizeId(idOrName);
            foreach (Y4NGZSkillTreeNodeDefinition candidate in SkillTreeNodes.Values)
            {
                if (string.Equals(candidate.DisplayName, idOrName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Y4NGZUpgradeDefinition.NormalizeId(candidate.DisplayName), normalized, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        public static int GetTreeInvestment(Y4NGZSkillTreeClass treeClass)
        {
            int total = 0;
            foreach (Y4NGZSkillTreeNodeDefinition node in SkillTreeNodes.Values)
            {
                if (node == null || node.TreeClass != treeClass || node.FlatCatalog)
                    continue;

                total += Mathf.Max(0, GetLevel(node.UpgradeId));
            }

            return total;
        }

        public static bool IsGateLocked(Y4NGZSkillTreeNodeDefinition node, int currentLevel)
        {
            if (node == null || currentLevel > 0 || node.GateRequirement <= 0)
                return false;

            return GetTreeInvestment(node.TreeClass) < node.GateRequirement;
        }

        public static bool IsPrerequisiteLocked(Y4NGZSkillTreeNodeDefinition node, int currentLevel)
        {
            if (node == null || currentLevel > 0 || node.PrerequisiteUpgradeIds == null || node.PrerequisiteUpgradeIds.Count == 0)
                return false;

            for (int i = 0; i < node.PrerequisiteUpgradeIds.Count; i++)
            {
                if (GetLevel(node.PrerequisiteUpgradeIds[i]) <= 0)
                    return true;
            }

            return false;
        }

        public static string GetPrerequisiteDisplayName(Y4NGZSkillTreeNodeDefinition node)
        {
            if (node == null || node.PrerequisiteUpgradeIds == null || node.PrerequisiteUpgradeIds.Count == 0)
                return string.Empty;

            var names = new List<string>();
            for (int i = 0; i < node.PrerequisiteUpgradeIds.Count; i++)
            {
                string prerequisiteId = node.PrerequisiteUpgradeIds[i];
                if (Definitions.TryGetValue(prerequisiteId, out Y4NGZUpgradeDefinition definition))
                {
                    names.Add(definition.Name);
                    continue;
                }

                Y4NGZSkillTreeNodeDefinition skillNode = GetSkillTreeNode(prerequisiteId);
                names.Add(skillNode != null ? skillNode.DisplayName : prerequisiteId);
            }

            return string.Join(" + ", names);
        }

        public static Y4NGZUpgradeNode GetUpgradeNode(string idOrName)
        {
            string id = ResolveId(idOrName);
            return id != null && Nodes.TryGetValue(id, out Y4NGZUpgradeNode node) ? node : null;
        }

        public static bool IsUnlocked(string idOrName)
        {
            return GetLevel(idOrName) > 0;
        }

        public static bool IsExactUnlocked(string idOrName)
        {
            return GetExactLevel(idOrName) > 0;
        }

        /// <summary>
        /// The live level: what the menu, purchases, gates, prerequisites and tree investment
        /// read. A row that sells fewer levels than it stores (#441) shows how many of its live
        /// ranks the stored rank has reached.
        /// </summary>
        public static int GetLevel(string idOrName)
        {
            string id = ResolveId(idOrName);
            if (id == null || !Definitions.TryGetValue(id, out Y4NGZUpgradeDefinition definition))
                return 0;

            LiveRankLadder ladder = definition.LiveLadder;
            return ladder == null
                ? Store.GetLevel(id, definition.MaxTier, CurrentPolicy.RecordOf(id))
                : ladder.Project(Store.GetLevel(id, ladder.StoredCap, CurrentPolicy.RecordOf(id)));
        }

        /// <summary>
        /// The stored rank effects read. For a row with a live ladder (#441) this is the record's
        /// own rank clamped to its authored cap, never the live level count, so a stored rank 3
        /// still hacks turrets while only two levels are sold.
        /// </summary>
        public static int GetExactLevel(string idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName) || Store.Current == null)
                return 0;

            if (!ExactIdCache.TryGetValue(idOrName, out string id))
            {
                id = ResolveExactId(idOrName);

                // F-FOREMAN-A-7: misses are memoized too (as null). A miss used to re-run NormalizeId
                // plus a full linear scan on every frame for every dead accessor. The caches are
                // dropped by RegisterDefinition/RegisterMigrationRule, which keeps late registration
                // discoverable without paying for it per call.
                ExactIdCache[idOrName] = id;
            }

            if (id == null || !Definitions.TryGetValue(id, out Y4NGZUpgradeDefinition definition))
                return 0;

            return Store.GetLevel(
                id,
                definition.LiveLadder?.StoredCap ?? definition.MaxTier,
                CurrentPolicy.RecordOf(id));
        }

        /// <summary>
        /// The full-native ranks this save owns, whatever the live policy sells. Always clamped
        /// to the authored catalog row, never to a smaller live variant: a reduced MaxTier is not
        /// a save migration, and dormant full ranks must come back intact (#435).
        /// </summary>
        internal static int GetFullNativeLevel(string upgradeId)
        {
            if (string.IsNullOrWhiteSpace(upgradeId) || Store.Current == null)
                return 0;

            int maxTier = UpgradeCatalogTable.TryGet(upgradeId, out UpgradeCatalogTable.Entry entry)
                ? entry.MaxTier
                : int.MaxValue;
            return Store.GetLevel(upgradeId, maxTier, UpgradeRankRecord.Full);
        }

        /// <summary>
        /// The unique-only ranks this save owns, including the ones its full-native ranks have
        /// already earned. Zero for a row that has no unique variant.
        /// </summary>
        internal static int GetUniqueOnlyLevel(string upgradeId)
        {
            if (string.IsNullOrWhiteSpace(upgradeId) || Store.Current == null)
                return 0;
            if (!NativeUpgradeFamilies.TryGet(upgradeId, out NativeUpgradeFamily family)
                || family.UniqueVariant == null)
            {
                return 0;
            }

            return Store.GetLevel(
                upgradeId, family.UniqueVariant.RankCount, UpgradeRankRecord.UniqueOnly);
        }

        /// <summary>
        /// Exact (non-migrating) id/display-name resolution, or null.
        ///
        /// F-INFRA-12: an exact display-name match wins over a normalized-id match unless the caller
        /// passed a registered id verbatim. `NormalizeId("Veteran")` is `"veteran"`, which is the
        /// Overachiever row's stable id, so the id-first order silently returned Overachiever's
        /// level for a lookup by the Veteran row's display name. Ids are lower_snake_case, so an
        /// ordinal comparison against the stored id is what separates "an id" from "a display name".
        /// </summary>
        private static string ResolveExactId(string idOrName)
        {
            if (Definitions.TryGetValue(idOrName, out Y4NGZUpgradeDefinition byId)
                && string.Equals(byId.Id, idOrName, StringComparison.Ordinal))
            {
                return byId.Id;
            }

            // A catalog stable id passed verbatim resolves only to itself. An unregistered family
            // id such as "back_muscles" must not fall through to the display-name scan, where it
            // would match the imported "Back Muscles" row and hand a hidden native effect the LGU
            // rank (#435). Ordinal: "Veteran" is a display name, "veteran" is an id.
            if (IsCatalogStableId(idOrName))
                return null;

            foreach (Y4NGZUpgradeDefinition candidate in Definitions.Values)
            {
                if (string.Equals(candidate.Name, idOrName, StringComparison.OrdinalIgnoreCase))
                    return candidate.Id;
            }

            string normalized = Y4NGZUpgradeDefinition.NormalizeId(idOrName);
            if (Definitions.ContainsKey(normalized))
                return normalized;

            foreach (Y4NGZUpgradeDefinition candidate in Definitions.Values)
            {
                if (string.Equals(
                        Y4NGZUpgradeDefinition.NormalizeId(candidate.Name),
                        normalized,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.Id;
                }
            }

            return null;
        }

        /// <summary>A catalog stable id passed verbatim (ordinal), as opposed to a display name.</summary>
        private static bool IsCatalogStableId(string value)
        {
            return value != null
                && UpgradeCatalogTable.TryGet(value, out UpgradeCatalogTable.Entry entry)
                && string.Equals(entry.Id, value, StringComparison.Ordinal);
        }

        public static void SetLevel(string idOrName, int level)
        {
            string id = ResolveId(idOrName);
            if (id == null || !Definitions.TryGetValue(id, out Y4NGZUpgradeDefinition definition))
                return;

            // Without a resolvable save key the write would land on state nothing persists. #213.
            if (!SwitchToCurrentSave())
            {
                Plugin.Log?.LogWarning(
                    $"[Y4NGZUpgrades] Ignored level change for '{id}': no save is active yet.");
                return;
            }

            UpgradeSaveState state = Store.Current;
            // Reset and debug writes address the record the row actually sells, so clearing a
            // unique-only rank cannot be undone by stale full-native metadata and vice versa.
            Dictionary<string, int> ranks = state.RanksFor(CurrentPolicy.RecordOf(id));
            // The argument is a live level; a live ladder stores it as that level's own rank (#441).
            int clamped = Mathf.Clamp(level, 0, definition.MaxTier);
            if (definition.LiveLadder != null)
                clamped = definition.LiveLadder.StoredRankFor(clamped);
            if (clamped <= 0)
                ranks.Remove(id);
            else
                ranks[id] = clamped;

            Save();
            ApplyOverachieverBackfillFor(id);
            UpgradesChanged?.Invoke();
        }

        /// <summary>
        /// Overachiever only pays for ranks gained after purchase, so buying or tiering it up has
        /// to settle up for the ranks already earned. Idempotent per save per tier.
        /// </summary>
        private static void ApplyOverachieverBackfillFor(string upgradeId)
        {
            if (string.Equals(upgradeId, OverachieverUpgrade.UPGRADE_ID, StringComparison.OrdinalIgnoreCase))
                ProgressionManager.ApplyOverachieverRankBonusBackfill();
        }

        public static int GetCurrentPrice(string idOrName)
        {
            Y4NGZUpgradeNode node = GetUpgradeNode(idOrName);
            return node?.GetCurrentPrice() ?? int.MaxValue;
        }

        /// <param name="quotedPrice">
        /// Price the caller displayed to the player. The purchase is refused outright when the live
        /// price no longer agrees, so a menu label and the charge can never diverge. Pass
        /// <see cref="UpgradePurchaseFlow.NoQuotedPrice"/> for non-interactive callers. #213.
        /// </param>
        public static Y4NGZUpgradePurchaseResult Purchase(
            string idOrName,
            int quotedPrice = UpgradePurchaseFlow.NoQuotedPrice)
        {
            Y4NGZUpgradeNode node = GetUpgradeNode(idOrName);
            if (node == null)
                return Y4NGZUpgradePurchaseResult.UnknownUpgrade;

            // A family whose identity is still resolving must not sell anything: the rank bought
            // now would change meaning the moment Late Game Upgrades finishes loading (#435).
            if (IsFamilyPending(node.Id))
                return Y4NGZUpgradePurchaseResult.ProviderUnavailable;

            // The save key has to resolve before any token moves; otherwise the tokens are spent
            // for real while the level lands on state that is never written to disk. #213.
            if (!SwitchToCurrentSave())
                return Y4NGZUpgradePurchaseResult.SaveUnavailable;
            if (!LguUpgradeBridge.TryCreatePurchase(node.Id, out IUpgradePurchaseEffect effect))
                return Y4NGZUpgradePurchaseResult.ProviderUnavailable;

            int level = GetLevel(node.Id);
            if (level >= node.Definition.MaxTier)
                return Y4NGZUpgradePurchaseResult.Maxed;

            Y4NGZSkillTreeNodeDefinition skillNode = GetSkillTreeNode(node.Id);
            if (IsGateLocked(skillNode, level))
                return Y4NGZUpgradePurchaseResult.GateLocked;

            if (IsPrerequisiteLocked(skillNode, level))
                return Y4NGZUpgradePurchaseResult.PrerequisiteLocked;

            // The quote belongs to one resolved catalog. If a family changes mode before the
            // commit the purchase is refused even when the new numeric price happens to match.
            int generation = CurrentPolicy.Generation;
            UpgradeRankRecord record = CurrentPolicy.RecordOf(node.Id);
            string transactionId = "upgrade_purchase:" + node.Id + ":" + Guid.NewGuid().ToString("N");
            UpgradePurchaseOutcome outcome = UpgradePurchaseFlow.TryPurchase(
                Store,
                node.Id,
                node.Definition.MaxTier,
                node.GetPriceForLevel,
                quotedPrice,
                ProgressionWallet.Instance,
                transactionId,
                out int chargedPrice,
                out int newLevel,
                effect,
                record,
                () => CurrentPolicy.Generation == generation,
                node.Definition.LiveLadder);

            switch (outcome)
            {
                case UpgradePurchaseOutcome.Success:
                    break;
                case UpgradePurchaseOutcome.Maxed:
                    return Y4NGZUpgradePurchaseResult.Maxed;
                case UpgradePurchaseOutcome.SaveUnavailable:
                    return Y4NGZUpgradePurchaseResult.SaveUnavailable;
                case UpgradePurchaseOutcome.ProviderUnavailable:
                    return Y4NGZUpgradePurchaseResult.ProviderUnavailable;
                case UpgradePurchaseOutcome.CatalogChanged:
                    Plugin.Log?.LogWarning(
                        $"[Y4NGZUpgrades] Refused '{node.Id}': the upgrade catalog changed while the purchase was in flight.");
                    return Y4NGZUpgradePurchaseResult.PriceChanged;
                case UpgradePurchaseOutcome.PriceChanged:
                    Plugin.Log?.LogWarning(
                        $"[Y4NGZUpgrades] Refused '{node.Id}': quoted {quotedPrice} token(s), live price is {node.GetCurrentPrice()}.");
                    return Y4NGZUpgradePurchaseResult.PriceChanged;
                default:
                    return Y4NGZUpgradePurchaseResult.NotEnoughCurrency;
            }

            Save();
            ApplyOverachieverBackfillFor(node.Id);
            UpgradesChanged?.Invoke();
            Plugin.Log?.LogInfo(
                $"[Y4NGZUpgrades] Purchased '{node.Id}' level {newLevel} for {chargedPrice} token(s) on save '{Store.CurrentKey}'.");
            return Y4NGZUpgradePurchaseResult.Success;
        }

        private static string ResolveId(string idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName))
                return null;

            if (ResolvedIdCache.TryGetValue(idOrName, out string cached))
                return cached;

            string normalized = Y4NGZUpgradeDefinition.NormalizeId(idOrName);
            if (Definitions.ContainsKey(normalized))
            {
                ResolvedIdCache[idOrName] = normalized;
                return normalized;
            }

            if (MigrationRules.TryGetValue(normalized, out Y4NGZUpgradeMigrationRule rule))
            {
                ResolvedIdCache[idOrName] = rule.TargetId;
                return rule.TargetId;
            }

            // Same rule as ResolveExactId: a stable id passed verbatim never binds another row
            // through its display name, registered or not (#435).
            if (!IsCatalogStableId(idOrName))
            {
                foreach (Y4NGZUpgradeDefinition definition in Definitions.Values)
                {
                    if (string.Equals(definition.Name, idOrName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Y4NGZUpgradeDefinition.NormalizeId(definition.Name), normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        ResolvedIdCache[idOrName] = definition.Id;
                        return definition.Id;
                    }
                }
            }

            // F-FOREMAN-A-7: the fallback is cached too. RegisterDefinition/RegisterMigrationRule
            // drop the cache, so a late-registered definition is still discoverable.
            ResolvedIdCache[idOrName] = normalized;
            return normalized;
        }

        private static void NormalizeStateLevels(UpgradeSaveState state)
        {
            if (state == null)
                return;

            ApplyMigrationRules(state);
            MergeLevels(state, "physical_conditioning", PhysicalConditioningMergeIds);
            MergeLevels(state, "light_feet", LightFeetMergeIds);

            for (int i = 0; i < RemovedUpgradeIds.Length; i++)
            {
                string removedId = RemovedUpgradeIds[i];
                if (!SkillTreeNodes.ContainsKey(removedId))
                    state.Levels.Remove(removedId);
            }

            foreach (string id in state.Levels.Keys.ToList())
            {
                // LGU's host can expose more ranks than the authored default, or
                // temporarily expose fewer. Only the live view is capped; owned
                // ranks survive provider/config changes in the persisted state.
                if (LguUpgradeBridge.IsBridgedUpgrade(id))
                {
                    if (state.Levels[id] <= 0)
                        state.Levels.Remove(id);
                    continue;
                }
                if (!Definitions.TryGetValue(id, out Y4NGZUpgradeDefinition definition))
                {
                    // A catalog row can be unregistered on purpose: its optional provider is
                    // absent, or the player switched it off in config (#417). Keep those
                    // purchased levels dormant in the save so neither uninstalling an
                    // integration nor flipping a switch destroys progress. GetLevel still
                    // returns zero without a live definition, so the hidden upgrade cannot apply.
                    if (UpgradeCatalogTable.TryGet(id, out UpgradeCatalogTable.Entry catalogEntry))
                    {
                        int dormantLevel = Mathf.Clamp(state.Levels[id], 0, catalogEntry.MaxTier);
                        if (dormantLevel <= 0)
                            state.Levels.Remove(id);
                        else
                            state.Levels[id] = dormantLevel;
                        continue;
                    }

                    if (!SkillTreeNodes.ContainsKey(id))
                        state.Levels.Remove(id);
                    continue;
                }

                // Always the authored catalog cap, never the live definition's: a unique-only
                // variant sells fewer ranks than the row was authored with, and clamping the full
                // record to it would read a reduced MaxTier as a save migration (#435).
                int clamped = Mathf.Clamp(state.Levels[id], 0, CatalogCapFor(id, definition));
                if (clamped <= 0)
                    state.Levels.Remove(id);
                else
                    state.Levels[id] = clamped;
            }

            NormalizeUniqueLevels(state);
        }

        /// <summary>
        /// The authored rank count for a row, which is what the persisted full record is measured
        /// in. Falls back to the live definition for ids the catalog does not carry (migrated
        /// legacy keys).
        /// </summary>
        private static int CatalogCapFor(string id, Y4NGZUpgradeDefinition definition)
        {
            return UpgradeCatalogTable.TryGet(id, out UpgradeCatalogTable.Entry entry)
                ? entry.MaxTier
                : definition.MaxTier;
        }

        /// <summary>
        /// Keeps the unique-only record inside its variant's rank count and writes the credit the
        /// save's full-native ranks have already earned straight through. Writing it through is
        /// what makes the credit idempotent and independent of any one-time marker: a full rank
        /// bought later still grants its milestone, and a unique rank bought on its own is never
        /// promoted back into a full-native rank.
        /// </summary>
        private static void NormalizeUniqueLevels(UpgradeSaveState state)
        {
            foreach (string id in state.UniqueLevels.Keys.ToList())
            {
                if (!NativeUpgradeFamilies.TryGet(id, out NativeUpgradeFamily owner)
                    || owner.UniqueVariant == null)
                {
                    state.UniqueLevels.Remove(id);
                }
            }

            IReadOnlyList<NativeUpgradeFamily> families = NativeUpgradeFamilies.All;
            for (int i = 0; i < families.Count; i++)
            {
                NativeUpgradeFamily family = families[i];
                NativeUpgradeVariant variant = family.UniqueVariant;
                if (variant == null)
                    continue;

                state.UniqueLevels.TryGetValue(family.NativeId, out int stored);
                state.Levels.TryGetValue(family.NativeId, out int fullRank);
                int earned = Mathf.Clamp(
                    Mathf.Max(stored, variant.ProjectFullRank(fullRank)), 0, variant.RankCount);
                if (earned > 0)
                    state.UniqueLevels[family.NativeId] = earned;
                else
                    state.UniqueLevels.Remove(family.NativeId);
            }
        }

        private static void MergeLevels(UpgradeSaveState state, string targetId, string[] sourceIds)
        {
            if (state == null || sourceIds == null)
                return;

            // F-INFRA-5: a row hidden by #417's Enabled switch (or by a missing optional provider)
            // is never registered, so keying the merge on Definitions alone skipped it entirely -
            // and the sweep in NormalizeStateLevels then deleted every legacy sub-id, because they
            // are neither catalog rows nor tree nodes. The catalog table always has the target row.
            // #417 promises the levels stay in the save and come back when the row is switched on
            // again. The cap comes from the table first because this is a legacy full-record
            // repair, measured in authored ranks even when the live definition is a smaller
            // unique-only variant (#435).
            int maxTier;
            if (UpgradeCatalogTable.TryGet(targetId, out UpgradeCatalogTable.Entry catalogEntry))
                maxTier = catalogEntry.MaxTier;
            else if (Definitions.TryGetValue(targetId, out Y4NGZUpgradeDefinition target))
                maxTier = target.MaxTier;
            else
                return;

            int mergedLevel = state.Levels.TryGetValue(targetId, out int existing) ? existing : 0;
            for (int i = 0; i < sourceIds.Length; i++)
            {
                string sourceId = sourceIds[i];
                if (state.Levels.TryGetValue(sourceId, out int sourceLevel))
                    mergedLevel = Mathf.Max(mergedLevel, sourceLevel);
            }

            for (int i = 0; i < sourceIds.Length; i++)
            {
                if (!string.Equals(sourceIds[i], targetId, StringComparison.OrdinalIgnoreCase))
                    state.Levels.Remove(sourceIds[i]);
            }

            mergedLevel = Mathf.Clamp(mergedLevel, 0, maxTier);
            if (mergedLevel > 0)
                state.Levels[targetId] = mergedLevel;
            else
                state.Levels.Remove(targetId);
        }

        private static void ApplyMigrationRules(UpgradeSaveState state)
        {
            if (state == null || MigrationRules.Count == 0)
                return;

            foreach (Y4NGZUpgradeMigrationRule rule in MigrationRules.Values)
            {
                if (!state.Levels.TryGetValue(rule.SourceId, out int sourceLevel))
                    continue;

                int mergedLevel = sourceLevel;
                if (state.Levels.TryGetValue(rule.TargetId, out int targetLevel))
                    mergedLevel = Mathf.Max(mergedLevel, targetLevel);

                if (UpgradeCatalogTable.TryGet(rule.TargetId, out UpgradeCatalogTable.Entry targetEntry))
                    mergedLevel = Mathf.Clamp(mergedLevel, 0, targetEntry.MaxTier);
                else if (Definitions.TryGetValue(rule.TargetId, out Y4NGZUpgradeDefinition targetDefinition))
                    mergedLevel = Mathf.Clamp(mergedLevel, 0, targetDefinition.MaxTier);

                state.Levels.Remove(rule.SourceId);
                if (mergedLevel > 0)
                    state.Levels[rule.TargetId] = mergedLevel;
            }
        }

        private static void TryImportLegacyLevels()
        {
            UpgradeSaveState state = Store.Current;
            if (state == null || state.ImportedLegacyLevels)
                return;

            // The legacy import is intentionally conservative for now. Once every old
            // upgrade has a confirmed native equivalent, this seam can read the old
            // legacy upgrade save payload and map levels into this state.
            state.ImportedLegacyLevels = true;
            Save();
        }

        private static void WriteStatesToDisk()
        {
            Directory.CreateDirectory(SaveDir);

            var lines = new List<string>();
            foreach (KeyValuePair<string, UpgradeSaveState> pair in Store.Entries.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                UpgradeSaveState state = pair.Value ?? new UpgradeSaveState();
                NormalizeStateLevels(state);
                lines.Add(state.Encode(pair.Key));
            }

            SaveDataFile.WriteAllLinesAtomic(SavePath, lines);
        }
    }
}
