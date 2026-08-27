namespace Y4NGZUpgrades.Upgrades
{
    /// <summary>
    /// Registers the shipped upgrades. Every row of <see cref="UpgradeCatalogTable"/> builds both
    /// the skill-tree node and the upgrade definition in one pass, so a node's tree tier and the
    /// prices derived from it cannot drift apart (#217). Player price overrides still apply on top,
    /// through <see cref="PerUpgradeConfig.Resolve"/>.
    /// </summary>
    internal static class Y4NGZUpgradeCatalog
    {
        internal static void RegisterDefaults()
        {
            Y4NGZUpgradeManager.ClearDefinitions();

            RegisterSkillTrees();

            OptionalUpgradeProvider installedProviders =
                OptionalPluginCapabilities.LoadedUpgradeProviders;
            foreach (UpgradeCatalogTable.Entry entry in UpgradeCatalogTable.All)
            {
                bool active = entry.IsAvailable(installedProviders);
                EffectiveUpgradePrices prices = Plugin.ProgressionConfig?.UpgradePrices?.Resolve(entry, active)
                    ?? new EffectiveUpgradePrices(entry.UnlockPrice, entry.TierPrices);

                if (!active)
                {
                    Plugin.Log?.LogInfo(
                        $"[UpgradeCatalog] Hiding {entry.DisplayName}; provider requirement not met "
                        + $"(all={entry.RequiredProviders}, any={entry.AnyProviders}, "
                        + $"loaded={installedProviders}).");
                    continue;
                }

                Register(entry, prices);
            }

            RegisterMigrationDefaults();
            Plugin.Log?.LogInfo("[UpgradePrices] Effective catalog:\n" +
                                Plugin.ProgressionConfig?.UpgradePrices?.BuildAuditReport());

        }

        private static void Register(UpgradeCatalogTable.Entry entry, EffectiveUpgradePrices prices)
        {
            Y4NGZUpgradeManager.RegisterDefinition(new Y4NGZUpgradeDefinition(
                entry.Id,
                entry.DisplayName,
                entry.Description,
                prices.UnlockPrice,
                prices.TierPrices));

            Y4NGZUpgradeManager.RegisterSkillTreeNode(new Y4NGZSkillTreeNodeDefinition(
                entry.Id,
                entry.TreeClass,
                entry.Tier,
                entry.Column,
                entry.Row,
                entry.IconKey,
                entry.GateRequirement,
                entry.DisplayName,
                null,
                entry.PrerequisiteUpgradeIds,
                entry.ConnectionUpgradeIds));
        }

        private static void RegisterSkillTrees()
        {
            // Gate ladders are per tree: Foreman carries a lower tier-2 gate (see
            // UpgradeCatalogTable.TierGateOverrides).
            Y4NGZUpgradeManager.RegisterSkillTree(new Y4NGZSkillTreeDefinition(
                Y4NGZSkillTreeClass.Enforcer, "Enforcer", 0,
                UpgradeCatalogTable.GatesFor(Y4NGZSkillTreeClass.Enforcer)));
            Y4NGZUpgradeManager.RegisterSkillTree(new Y4NGZSkillTreeDefinition(
                Y4NGZSkillTreeClass.Ghost, "Ghost", 1,
                UpgradeCatalogTable.GatesFor(Y4NGZSkillTreeClass.Ghost)));
            Y4NGZUpgradeManager.RegisterSkillTree(new Y4NGZSkillTreeDefinition(
                Y4NGZSkillTreeClass.Technician, "Technician", 2,
                UpgradeCatalogTable.GatesFor(Y4NGZSkillTreeClass.Technician)));
            Y4NGZUpgradeManager.RegisterSkillTree(new Y4NGZSkillTreeDefinition(
                Y4NGZSkillTreeClass.Foreman, "Foreman", 3,
                UpgradeCatalogTable.GatesFor(Y4NGZSkillTreeClass.Foreman)));
        }

        private static void RegisterMigrationDefaults()
        {
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
