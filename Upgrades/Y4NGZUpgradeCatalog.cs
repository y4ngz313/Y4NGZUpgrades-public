using System;
using System.Collections.Generic;
using System.Linq;

namespace Y4NGZUpgrades.Upgrades
{
    /// <summary>
    /// Registers the shipped upgrades. Every row of <see cref="UpgradeCatalogTable"/> builds both
    /// the skill-tree node and the upgrade definition in one pass, so a node's tree tier and the
    /// prices derived from it cannot drift apart (#217). Player price overrides still apply on top,
    /// through <see cref="PerUpgradeConfig.Resolve"/>.
    ///
    /// A row the player switched off (`General - Enabled = false`, #417) takes the same road as a
    /// row whose optional provider is absent: it is never registered, so
    /// <see cref="Y4NGZUpgradeManager.GetLevel"/> reads zero, every effect stays off, the menu
    /// and tree never list it, and the tree's gate ladder is re-derived from what is left so no
    /// tier can ask for more levels than remain purchasable beneath it.
    /// </summary>
    internal static class Y4NGZUpgradeCatalog
    {
        internal static void RegisterDefaults()
        {
            Y4NGZUpgradeManager.ClearDefinitions();

            OptionalUpgradeProvider installedProviders =
                OptionalPluginCapabilities.LoadedUpgradeProviders;
            PerUpgradeConfig config = Plugin.ProgressionConfig?.UpgradePrices;
            // This method is idempotent by design: it may run a second time once patching reveals
            // that an optional provider is unusable (F-ENF-1). Re-binding a config key returns the
            // existing entry and the migration-rule table is keyed by source id, so the only state
            // that has to be reset explicitly is the price audit.
            config?.BeginCatalogPass();

            // One resolved snapshot of the native families for the whole pass, published before a
            // single row is registered so the catalog, the effects, the prices, the gates and the
            // menu all read the same answer instead of each guessing the active mode (#435).
            // Late Game Upgrades counts only while it is integrated into the player menu (#493);
            // switched off, every mode resolves the no-LGU policy.
            LguUpgradeMode upgradeMode = Plugin.LguUpgradeMode;
            NativeUpgradePolicy policy = NativeUpgradeFamilies.Resolve(
                upgradeMode,
                Plugin.LguIntegrationActive,
                LguUpgradeBridge.IsReady,
                id => LguUpgradeBridge.TryGetSupportedLevels(id, out int supported) && supported >= 1,
                Y4NGZUpgradeManager.CurrentPolicy);
            int previousGeneration = Y4NGZUpgradeManager.CurrentPolicy.Generation;
            Y4NGZUpgradeManager.CurrentPolicy = policy;
            bool flatLguCatalog = Gui.Plugin.UseSeparateLguCatalog;
            // Field Mechanic's first rank acts only through these two plugins (#441). Plugin
            // presence is fixed for the process, so resolving it with the policy keeps the
            // catalog, gates, prices, menu and purchases on one answer without generation churn.
            bool fieldMechanicProviders = (installedProviders
                & (OptionalUpgradeProvider.ShipSystems | OptionalUpgradeProvider.LethalCctv)) != 0;

            HashSet<string> disabled = ResolveDisabledIds(config);

            // Prerequisites only make sense between registered rows. A dependent whose
            // prerequisite is gone would otherwise lock forever behind a node nobody can buy.
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var levelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (UpgradeCatalogTable.Entry entry in UpgradeCatalogTable.All)
            {
                if (LguUpgradeBridge.IsSuppressed(entry.Id))
                    continue;
                if (policy.ModeOf(entry.Id) == NativeFamilyMode.Hidden)
                    continue;
                if (!entry.IsAvailable(installedProviders) || disabled.Contains(entry.Id))
                    continue;
                if (!TryResolveLevelCount(entry, policy, out int levels))
                    continue;

                levelCounts[entry.Id] = levels;
                registered.Add(entry.Id);
            }

            Dictionary<Y4NGZSkillTreeClass, Y4NGZSkillTreeDefinition> trees =
                RegisterSkillTrees(entry => GateSupply(
                    entry, policy, disabled, fieldMechanicProviders));

            int suppressedImports = 0;
            int hiddenNatives = 0;
            foreach (UpgradeCatalogTable.Entry entry in UpgradeCatalogTable.All)
            {
                bool providerMet = entry.IsAvailable(installedProviders);
                bool active = registered.Contains(entry.Id);
                // Bound even for a row this pass does not register, so a player's customised
                // prices and Enabled switch stay in the file instead of being pruned as orphans.
                EffectiveUpgradePrices prices = config != null && HasOwnConfigSection(entry)
                    ? config.Resolve(entry, active)
                    : new EffectiveUpgradePrices(entry.UnlockPrice, entry.TierPrices);

                if (active)
                {
                    Register(
                        entry,
                        prices,
                        trees[entry.TreeClass],
                        registered,
                        levelCounts[entry.Id],
                        policy,
                        flatLguCatalog,
                        fieldMechanicProviders);
                    continue;
                }

                // Imported rows a native row already supplies, and native rows the mode hides.
                // Both are counted rather than logged one by one: nothing went wrong.
                if (LguUpgradeBridge.IsSuppressed(entry.Id))
                {
                    suppressedImports++;
                    continue;
                }

                if (policy.NativeCatalogHidden && !entry.IsImported)
                {
                    hiddenNatives++;
                    continue;
                }

                if (!providerMet)
                {
                    Plugin.Log?.LogInfo(
                        $"[UpgradeCatalog] Hiding {entry.DisplayName}; provider requirement not met "
                        + $"(all={entry.RequiredProviders}, any={entry.AnyProviders}, "
                        + $"loaded={installedProviders}).");
                    continue;
                }

                if (policy.ModeOf(entry.Id) == NativeFamilyMode.Hidden
                    && NativeUpgradeFamilies.TryGet(entry.Id, out NativeUpgradeFamily hidden))
                {
                    Plugin.Log?.LogInfo(
                        $"[UpgradeCatalog] Hiding {entry.DisplayName}; LGU Upgrade Mode is {upgradeMode} and "
                        + $"{string.Join(", ", hidden.LguMemberIds)} supplies all of it. Levels "
                        + "already bought stay in the save and return in another mode.");
                    continue;
                }

                if (disabled.Contains(entry.Id))
                {
                    Plugin.Log?.LogInfo(
                        $"[UpgradeCatalog] Hiding {entry.DisplayName}; disabled in config "
                        + $"({Config.Y4NGZConfigLayout.UpgradeGroupName(entry)} - {entry.ConfigName} - General - Enabled = false).");
                    continue;
                }

                // The provider is installed but does not sell this row: the host switched the
                // matching Late Game Upgrades entry off, or priced it at nothing so it is free
                // and permanently on. Either way there is no rank for a token to buy.
                Plugin.Log?.LogInfo(
                    $"[UpgradeCatalog] Hiding {entry.DisplayName}; the installed provider does not "
                    + "offer it. Levels already bought stay in the save and return if it comes back.");
            }

            if (suppressedImports > 0)
            {
                Plugin.Log?.LogInfo(
                    $"[UpgradeCatalog] LGU Upgrade Mode is {upgradeMode}; hiding {suppressedImports} imported "
                    + "row(s) whose effects native upgrades already supply. Levels already bought "
                    + "stay in the save and return in another mode.");
            }

            if (hiddenNatives > 0)
            {
                Plugin.Log?.LogInfo(
                    $"[UpgradeCatalog] LGU Upgrade Mode is LguOnly; hiding and switching off {hiddenNatives} "
                    + "native row(s). Levels already bought stay in the save and return in another mode.");
            }

            RegisterMigrationDefaults();
            Plugin.Log?.LogInfo("[UpgradePrices] Effective catalog:\n" +
                                config?.BuildAuditReport());

            // A mode change is an upgrade-set change for everyone downstream: the open menu, the
            // inspector's dormant line, the tier sync and the effects all re-read on this event.
            // Same-answer passes stay silent so in-flight quotes survive (#435).
            if (policy.Generation != previousGeneration)
                Y4NGZUpgradeManager.RaiseUpgradesChanged();
        }

        /// <summary>
        /// How many levels a row actually sells. A native row sells what the table authored,
        /// unless the resolved policy reduced it to a unique-only variant (#435). A Late Game
        /// Upgrades row sells what the host's own Late Game Upgrades configuration supports, which
        /// may be fewer ranks, more ranks, or none at all - and "none" means the row must not be
        /// registered, so no token can be spent on it.
        /// </summary>
        private static bool TryResolveLevelCount(
            UpgradeCatalogTable.Entry entry,
            NativeUpgradePolicy policy,
            out int levels)
        {
            if (!LguUpgradeBridge.IsBridgedUpgrade(entry.Id))
            {
                levels = policy.ModeOf(entry.Id) == NativeFamilyMode.UniqueOnly
                         && NativeUpgradeFamilies.TryGet(entry.Id, out NativeUpgradeFamily family)
                         && family.UniqueVariant != null
                    ? family.UniqueVariant.RankCount
                    : entry.MaxTier;
                return true;
            }

            return LguUpgradeBridge.TryGetSupportedLevels(entry.Id, out levels);
        }

        /// <summary>
        /// Levels this row contributes to its tree's gate ladder: the shared rule in
        /// <see cref="UpgradeCatalogTable.GateSupply(UpgradeCatalogTable.Entry, NativeFamilyMode, bool)"/>,
        /// and nothing for a row the player disabled.
        /// </summary>
        private static int GateSupply(
            UpgradeCatalogTable.Entry entry,
            NativeUpgradePolicy policy,
            HashSet<string> disabled,
            bool fieldMechanicProviders)
        {
            if (disabled.Contains(entry.Id))
                return 0;

            return UpgradeCatalogTable.GateSupply(
                entry,
                policy.ModeOf(entry.Id),
                fieldMechanicProviders);
        }

        /// <summary>
        /// Reads every row's `Enabled` switch and closes the disabled set over functional
        /// dependencies, logging each row that went missing because of one it depends on.
        /// </summary>
        private static HashSet<string> ResolveDisabledIds(PerUpgradeConfig config)
        {
            var configured = new List<string>();
            if (config != null)
            {
                foreach (UpgradeCatalogTable.Entry entry in UpgradeCatalogTable.All)
                {
                    if (!HasOwnConfigSection(entry))
                        continue;
                    if (!config.ResolveEnabled(entry))
                        configured.Add(entry.Id);
                }
            }

            var cascaded = new List<KeyValuePair<string, string>>();
            HashSet<string> disabled = UpgradeCatalogTable.ExpandDisabled(configured, cascaded);
            foreach (KeyValuePair<string, string> pair in cascaded)
            {
                Plugin.Log?.LogInfo(
                    $"[UpgradeCatalog] Disabling {UpgradeCatalogTable.Get(pair.Key).DisplayName} as well; "
                    + $"it cannot work without {UpgradeCatalogTable.Get(pair.Value).DisplayName}, "
                    + "which is disabled in config.");
            }

            return disabled;
        }

        /// <summary>
        /// Whether this row gets its own section in Y4NGZUpgrades.cfg. Shipped rows always do,
        /// including ones whose optional provider is absent, so a player can switch an upgrade off
        /// before installing the mod that supplies it (#417).
        ///
        /// A Late Game Upgrades row is the exception: there are 38 of them and each one already
        /// has an Enabled switch and a price list in Late Game Upgrades' own configuration, so
        /// binding a second set for a player who does not have that mod would add roughly two
        /// hundred keys that can never do anything (#435).
        /// </summary>
        private static bool HasOwnConfigSection(UpgradeCatalogTable.Entry entry)
        {
            return !LguUpgradeBridge.IsBridgedUpgrade(entry.Id) || LguUpgradeBridge.IsInstalled;
        }

        private static void Register(
            UpgradeCatalogTable.Entry entry,
            EffectiveUpgradePrices prices,
            Y4NGZSkillTreeDefinition tree,
            HashSet<string> registered,
            int levels,
            NativeUpgradePolicy policy,
            bool flatLguCatalog,
            bool fieldMechanicProviders)
        {
            NativeUpgradeVariant variant = null;
            if (policy.ModeOf(entry.Id) == NativeFamilyMode.UniqueOnly
                && NativeUpgradeFamilies.TryGet(entry.Id, out NativeUpgradeFamily family))
            {
                variant = family.UniqueVariant;
            }

            // The stable id never changes: saves, icons and every runtime lookup are keyed on it.
            // Only the title, the copy and the rank count follow the resolved variant (#435).
            string displayName = variant?.DisplayName ?? entry.DisplayName;
            bool flatCatalog = flatLguCatalog && LguUpgradeBridge.IsBridgedUpgrade(entry.Id);
            // An imported row's per-level copy is generated from the live Late Game Upgrades
            // configuration for the rank count it actually sells (#442); the authored row copy
            // is only the fallback when a value cannot be read.
            string description = variant != null
                ? variant.Description
                : LguUpgradeBridge.IsBridgedUpgrade(entry.Id)
                  && LguUpgradeBridge.TryDescribe(entry.Id, levels, out string generated)
                    ? generated
                    : entry.Description;

            int unlockPrice;
            int[] tierPrices;
            LiveRankLadder ladder = NativeUpgradeFamilies.LiveLadderFor(
                entry.Id, policy.ModeOf(entry.Id), entry.MaxTier, fieldMechanicProviders);
            if (ladder != null)
            {
                // Each live level costs its stored rank's own step; a skipped rank is dropped,
                // never folded into the next price (#441).
                int[] live = ladder.LivePrices(variant == null
                    ? (Func<int, int>)(stored => UpgradePriceMath.GetPriceForLevel(
                        stored, entry.MaxTier, prices.UnlockPrice, prices.TierPrices))
                    : stored => NativeUpgradeFamilies.UniqueRankPrice(
                        variant, stored, 0, prices.UnlockPrice, prices.TierPrices));
                unlockPrice = live[0];
                tierPrices = live.Skip(1).ToArray();
            }
            else if (variant == null)
            {
                unlockPrice = prices.UnlockPrice;
                tierPrices = FitTierPrices(entry, prices.TierPrices, levels);
            }
            else
            {
                unlockPrice = NativeUpgradeFamilies.UniqueRankPrice(
                    variant, 0, 0, prices.UnlockPrice, prices.TierPrices);
                tierPrices = FreshSaveVariantPrices(variant, prices);
            }

            Y4NGZUpgradeManager.RegisterDefinition(new Y4NGZUpgradeDefinition(
                entry.Id,
                displayName,
                description,
                unlockPrice,
                tierPrices,
                sharedUpgrade: false,
                visible: true,
                purchaseMode: Y4NGZPurchaseMode.UpgradeCurrency,
                variant: variant == null
                    ? null
                    : new NativeVariantBinding(variant, prices.UnlockPrice, prices.TierPrices),
                liveLadder: ladder));

            // The node reads the gate from the tree that was actually registered, not the
            // shipped ladder, so a disabled row below it can never leave a tier unreachable.
            // A row in the flat Augments catalog has no gate and no branch lines at all.
            Y4NGZUpgradeManager.RegisterSkillTreeNode(new Y4NGZSkillTreeNodeDefinition(
                entry.Id,
                entry.TreeClass,
                entry.Tier,
                entry.Column,
                entry.Row,
                entry.IconKey,
                flatCatalog ? 0 : tree.GetGateRequirement(entry.Tier),
                displayName,
                null,
                entry.PrerequisiteUpgradeIds
                    .Select(id => ResolvePrerequisiteId(id, policy))
                    .Where(registered.Contains),
                flatCatalog ? Array.Empty<string>() : entry.ConnectionUpgradeIds,
                flatCatalog));
        }

        /// <summary>
        /// A dependant gated on a native family follows whichever row actually sells that
        /// strength in the resolved mode: Lethal Hands needs melee-strength training, which
        /// stun-only Stagger is not, so in LGU-preferred mode it is gated on Protein Powder
        /// instead (#435). Only registered ids survive the filter in <see cref="Register"/>.
        /// </summary>
        private static string ResolvePrerequisiteId(string prerequisiteId, NativeUpgradePolicy policy)
        {
            return NativeUpgradeFamilies.TryGet(prerequisiteId, out NativeUpgradeFamily family)
                   && policy.ModeOf(prerequisiteId) != NativeFamilyMode.Full
                   && !string.IsNullOrEmpty(family.PrerequisiteReplacementId)
                ? family.PrerequisiteReplacementId
                : prerequisiteId;
        }

        /// <summary>
        /// The sub-tier prices a unique-only variant advertises on a fresh save: each rank costs
        /// the full-native steps up to its milestone. A live quote re-aggregates against the
        /// full-native ranks the save owns, so these are the ceiling, never a second price table.
        /// </summary>
        private static int[] FreshSaveVariantPrices(
            NativeUpgradeVariant variant,
            EffectiveUpgradePrices prices)
        {
            var tierPrices = new int[Math.Max(0, variant.RankCount - 1)];
            for (int i = 0; i < tierPrices.Length; i++)
            {
                tierPrices[i] = NativeUpgradeFamilies.UniqueRankPrice(
                    variant, i + 1, 0, prices.UnlockPrice, prices.TierPrices);
            }

            return tierPrices;
        }

        /// <summary>
        /// Trims or extends a row's sub-tier prices to the number of levels it really sells. A
        /// provider that supports fewer ranks than the table authored must never have tokens spent
        /// on the missing ones; one that supports more gets them at the same tier-derived price the
        /// rule produces everywhere else (#217), since there is no authored key to read for a rank
        /// the table never knew about.
        /// </summary>
        private static int[] FitTierPrices(UpgradeCatalogTable.Entry entry, int[] authored, int levels)
        {
            authored = authored ?? Array.Empty<int>();
            int wanted = Math.Max(0, levels - 1);
            if (authored.Length == wanted)
                return authored;

            var fitted = new int[wanted];
            for (int i = 0; i < wanted; i++)
                fitted[i] = i < authored.Length ? authored[i] : entry.Tier + 1 + i;

            Plugin.Log?.LogInfo(
                $"[UpgradeCatalog] {entry.DisplayName} sells {levels} level(s) here instead of the "
                + $"authored {entry.MaxTier}; the provider's configuration decides.");
            return fitted;
        }

        private static Dictionary<Y4NGZSkillTreeClass, Y4NGZSkillTreeDefinition> RegisterSkillTrees(
            Func<UpgradeCatalogTable.Entry, int> suppliesLevels)
        {
            // Gate ladders are per tree and derived from the rows that actually sell levels:
            // Foreman carries a lower tier-2 gate out of the box (see
            // UpgradeCatalogTable.DerivedTierGates), and a disabled row lowers whatever sits
            // above it the same way.
            var trees = new Dictionary<Y4NGZSkillTreeClass, Y4NGZSkillTreeDefinition>();
            RegisterSkillTree(trees, Y4NGZSkillTreeClass.Enforcer, "Enforcer", 0, suppliesLevels);
            RegisterSkillTree(trees, Y4NGZSkillTreeClass.Ghost, "Ghost", 1, suppliesLevels);
            RegisterSkillTree(trees, Y4NGZSkillTreeClass.Technician, "Technician", 2, suppliesLevels);
            RegisterSkillTree(trees, Y4NGZSkillTreeClass.Foreman, "Foreman", 3, suppliesLevels);
            return trees;
        }

        private static void RegisterSkillTree(
            Dictionary<Y4NGZSkillTreeClass, Y4NGZSkillTreeDefinition> trees,
            Y4NGZSkillTreeClass treeClass,
            string displayName,
            int order,
            Func<UpgradeCatalogTable.Entry, int> suppliesLevels)
        {
            var tree = new Y4NGZSkillTreeDefinition(
                treeClass, displayName, order, UpgradeCatalogTable.GatesFor(treeClass, suppliesLevels));
            Y4NGZUpgradeManager.RegisterSkillTree(tree);
            trees[treeClass] = tree;
        }

        private static void RegisterMigrationDefaults()
        {
            RegisterMigration("nine_lives", "adrenaline_rush", "Nine Lives retains the Adrenaline Rush purchase ID.");
            RegisterMigration("lethal_hands", "lethal_hands_training", "Lethal Hands Training display rename.");
            RegisterMigration("lethal_hands_haymaker", "lethal_hands_training", "Legacy Haymaker investment maps to native Lethal Hands.");
            RegisterMigration("deeper_pockets", "extra_inventory_slot", "Deeper Pockets legacy display-name save key.");
            RegisterMigration("glow_in_the_dark", "sixth_sense", "Glow in the Dark legacy save key became Sixth Sense.");
            RegisterMigration("night_vision", "sixth_sense", "Night Vision became Sixth Sense.");
            RegisterMigration("escape_reflex", "sixth_sense", "Escape Reflex investment became Sixth Sense.");
            RegisterMigration("escape_protocol", "sixth_sense", "Escape Protocol legacy slot now maps into Sixth Sense.");
            RegisterMigration("squad_sight", "buddy_system", "Squad Sight merged into Buddy System.");
            RegisterMigration("retaliation", "thick_skin", "Retaliation merged into Resilience.");
            RegisterMigration("force_field", "veteran", "Force Field retired; investment becomes Overachiever.");
        }

        private static void RegisterMigration(string sourceIdOrName, string targetId, string reason)
        {
            Y4NGZUpgradeManager.RegisterMigrationRule(new Y4NGZUpgradeMigrationRule(
                sourceIdOrName,
                targetId,
                reason));
        }
    }
}
