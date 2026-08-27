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
        PriceChanged
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

        // Veteran (quota_guard) shipped a third tier that QuotaGuardUpgrade never read. The tier
        // was removed; saves that already bought it drop to level 2 (the definition's MaxTier
        // clamp does that on its own) and get the 14 tokens back once, ledgered per save.
        private const string QuotaGuardId = "quota_guard";
        private const int QuotaGuardRemovedTier = 3;
        private const int QuotaGuardTier3RefundTokens = 14;
        private const string QuotaGuardTier3RefundGrantId = "quota_guard_tier3_refund";
        private static readonly HashSet<string> PendingQuotaGuardTier3Refunds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string SaveDir =
            Path.Combine(Application.persistentDataPath, "Y4NGZUpgrades");
        private static readonly string SavePath =
            Path.Combine(SaveDir, "upgrade_levels.txt");

        // Owns the per-save levels and which save is live. While no save key resolves the store has
        // no current state at all, so a level can never be written somewhere unpersistable. #213.
        private static readonly UpgradeSaveStateStore Store =
            new UpgradeSaveStateStore(SaveKey.TryGetCurrent);

        public static event Action UpgradesChanged;

        public static int Currency => ProgressionApi.UpgradeCurrency;

        internal static void Load()
        {
            Store.Clear();
            PendingQuotaGuardTier3Refunds.Clear();

            try
            {
                if (File.Exists(SavePath))
                {
                    foreach (string line in File.ReadAllLines(SavePath))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        string[] parts = line.Split('|');
                        if (parts.Length < 2)
                            continue;

                        string saveKey = parts[0].Trim();
                        if (string.IsNullOrWhiteSpace(saveKey))
                            continue;

                        var state = new UpgradeSaveState();
                        ParseLevels(parts[1], state.Levels);
                        state.ImportedLegacyLevels = parts.Length >= 3
                            && bool.TryParse(parts[2], out bool imported)
                            && imported;

                        // Recorded before NormalizeStateLevels silently clamps the removed tier.
                        if (state.Levels.TryGetValue(QuotaGuardId, out int quotaGuardLevel)
                            && quotaGuardLevel >= QuotaGuardRemovedTier)
                        {
                            PendingQuotaGuardTier3Refunds.Add(saveKey);
                        }

                        Store.SetState(saveKey, state);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Y4NGZUpgrades] Failed to load upgrade levels: {ex}");
            }

            // ProgressionManager.Load() runs first (Plugin.cs), so every save's token ledger is
            // already in memory and the refund can be paid for all affected saves at once.
            FlushPendingQuotaGuardRefunds();
            SwitchToCurrentSave();
        }

        private static void FlushPendingQuotaGuardRefunds(string onlySaveKey = null)
        {
            if (PendingQuotaGuardTier3Refunds.Count == 0)
                return;

            foreach (string saveKey in PendingQuotaGuardTier3Refunds.ToArray())
            {
                if (onlySaveKey != null && !string.Equals(saveKey, onlySaveKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ProgressionManager.TryApplyLedgeredTokenGrant(
                        saveKey,
                        QuotaGuardTier3RefundGrantId,
                        QuotaGuardTier3RefundTokens))
                {
                    continue;
                }

                PendingQuotaGuardTier3Refunds.Remove(saveKey);
                Plugin.Log?.LogInfo(
                    $"[Y4NGZUpgrades] Veteran tier {QuotaGuardRemovedTier} was removed; save '{saveKey}' drops to level {QuotaGuardRemovedTier - 1} and is refunded {QuotaGuardTier3RefundTokens} token(s).");
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

        internal static bool SwitchToCurrentSave()
        {
            if (!Store.SwitchToCurrentSave())
                return false;

            UpgradeSaveState state = Store.Current;

            // Both of these are ledgered per save in ProgressionManager, so re-running them on
            // every save switch is a no-op after the first application.
            if (state.Levels.TryGetValue(QuotaGuardId, out int quotaGuardLevel)
                && quotaGuardLevel >= QuotaGuardRemovedTier)
            {
                PendingQuotaGuardTier3Refunds.Add(Store.CurrentKey);
            }
            FlushPendingQuotaGuardRefunds(Store.CurrentKey);

            NormalizeStateLevels(state);
            TryImportLegacyLevels();
            ProgressionManager.ApplyOverachieverRankBonusBackfill();
            return true;
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

        public static IReadOnlyList<Y4NGZSkillTreeNodeDefinition> GetSkillTreeNodes(Y4NGZSkillTreeClass treeClass)
        {
            return SkillTreeNodes.Values
                .Where(x => x.TreeClass == treeClass)
                .OrderBy(x => x.Tier)
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
                if (node == null || node.TreeClass != treeClass)
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

        public static int GetLevel(string idOrName)
        {
            string id = ResolveId(idOrName);
            if (id == null || !Definitions.TryGetValue(id, out Y4NGZUpgradeDefinition definition))
                return 0;

            return Store.GetLevel(id, definition.MaxTier);
        }

        public static int GetExactLevel(string idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName) || Store.Current == null)
                return 0;

            if (!ExactIdCache.TryGetValue(idOrName, out string id))
            {
                string normalized = Y4NGZUpgradeDefinition.NormalizeId(idOrName);
                if (Definitions.ContainsKey(normalized))
                {
                    id = normalized;
                }
                else
                {
                    foreach (Y4NGZUpgradeDefinition candidate in Definitions.Values)
                    {
                        if (string.Equals(candidate.Name, idOrName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Y4NGZUpgradeDefinition.NormalizeId(candidate.Name), normalized, StringComparison.OrdinalIgnoreCase))
                        {
                            id = candidate.Id;
                            break;
                        }
                    }
                }

                // Only successful resolutions are memoized so late-registered
                // definitions are still discoverable on the next call.
                if (id != null)
                    ExactIdCache[idOrName] = id;
            }

            if (id == null || !Definitions.TryGetValue(id, out Y4NGZUpgradeDefinition definition))
                return 0;

            return Store.GetLevel(id, definition.MaxTier);
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
            int clamped = Mathf.Clamp(level, 0, definition.MaxTier);
            if (clamped <= 0)
                state.Levels.Remove(id);
            else
                state.Levels[id] = clamped;

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

            // The save key has to resolve before any token moves; otherwise the tokens are spent
            // for real while the level lands on state that is never written to disk. #213.
            if (!SwitchToCurrentSave())
                return Y4NGZUpgradePurchaseResult.SaveUnavailable;

            int level = GetLevel(node.Id);
            if (level >= node.Definition.MaxTier)
                return Y4NGZUpgradePurchaseResult.Maxed;

            Y4NGZSkillTreeNodeDefinition skillNode = GetSkillTreeNode(node.Id);
            if (IsGateLocked(skillNode, level))
                return Y4NGZUpgradePurchaseResult.GateLocked;

            if (IsPrerequisiteLocked(skillNode, level))
                return Y4NGZUpgradePurchaseResult.PrerequisiteLocked;

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
                out int newLevel);

            switch (outcome)
            {
                case UpgradePurchaseOutcome.Success:
                    break;
                case UpgradePurchaseOutcome.Maxed:
                    return Y4NGZUpgradePurchaseResult.Maxed;
                case UpgradePurchaseOutcome.SaveUnavailable:
                    return Y4NGZUpgradePurchaseResult.SaveUnavailable;
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

            foreach (Y4NGZUpgradeDefinition definition in Definitions.Values)
            {
                if (string.Equals(definition.Name, idOrName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Y4NGZUpgradeDefinition.NormalizeId(definition.Name), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    ResolvedIdCache[idOrName] = definition.Id;
                    return definition.Id;
                }
            }

            // Fallback (unresolved) results are not cached so late-registered
            // definitions are still discoverable on the next call.
            return normalized;
        }

        private static void ParseLevels(string encoded, Dictionary<string, int> levels)
        {
            levels.Clear();
            if (string.IsNullOrWhiteSpace(encoded))
                return;

            string[] entries = encoded.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i];
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                int separator = entry.IndexOf('=');
                if (separator <= 0 || separator >= entry.Length - 1)
                    continue;

                string id = Y4NGZUpgradeDefinition.NormalizeId(entry.Substring(0, separator));
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (int.TryParse(entry.Substring(separator + 1), out int level) && level > 0)
                    levels[id] = level;
            }
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
                if (!Definitions.TryGetValue(id, out Y4NGZUpgradeDefinition definition))
                {
                    // Provider-gated upgrades are intentionally not registered while their
                    // optional mods are absent. Keep those purchased levels dormant in the save
                    // so uninstalling an integration never destroys progress. GetLevel still
                    // returns zero without a live definition, so the hidden upgrade cannot apply.
                    if (UpgradeCatalogTable.TryGet(id, out UpgradeCatalogTable.Entry catalogEntry)
                        && catalogEntry.HasOptionalProviderRequirement)
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

                int clamped = Mathf.Clamp(state.Levels[id], 0, definition.MaxTier);
                if (clamped <= 0)
                    state.Levels.Remove(id);
                else
                    state.Levels[id] = clamped;
            }
        }

        private static void MergeLevels(UpgradeSaveState state, string targetId, string[] sourceIds)
        {
            if (state == null || sourceIds == null || !Definitions.TryGetValue(targetId, out Y4NGZUpgradeDefinition target))
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

            mergedLevel = Mathf.Clamp(mergedLevel, 0, target.MaxTier);
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

                if (Definitions.TryGetValue(rule.TargetId, out Y4NGZUpgradeDefinition targetDefinition))
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
                string encoded = string.Join(";", state.Levels
                    .Where(x => x.Value > 0)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => $"{x.Key}={Mathf.Max(0, x.Value)}"));
                lines.Add($"{pair.Key}|{encoded}|{state.ImportedLegacyLevels}");
            }

            SaveDataFile.WriteAllLinesAtomic(SavePath, lines);
        }
    }
}
