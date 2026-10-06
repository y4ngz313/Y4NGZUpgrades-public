using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Y4NGZUpgrades.Config;

namespace Y4NGZUpgrades.Upgrades
{
    /// <summary>
    /// Binds prices inside each upgrade's class-first sections in Y4NGZUpgrades.cfg. The stable
    /// internal id is used only to import the former monolithic keys; players now edit one plainly
    /// named integer
    /// for each purchase step instead of a global id table and comma-separated tier lists.
    /// </summary>
    internal readonly struct EffectiveUpgradePrices
    {
        internal EffectiveUpgradePrices(int unlockPrice, int[] tierPrices)
        {
            UnlockPrice = Math.Max(0, unlockPrice);
            TierPrices = tierPrices ?? Array.Empty<int>();
        }

        internal int UnlockPrice { get; }
        internal int[] TierPrices { get; }
    }

    internal sealed class PerUpgradeConfig
    {
        private readonly List<AuditRow> _audit = new List<AuditRow>();

        /// <summary>
        /// The audit report is a snapshot of one catalog pass. F-ENF-1 can make
        /// <see cref="Y4NGZUpgradeCatalog.RegisterDefaults"/> run a second time, so start clean or
        /// every row is counted twice and the token-margin warning fires on a phantom shortfall.
        /// </summary>
        internal void BeginCatalogPass()
        {
            _audit.Clear();
        }

        /// <summary>
        /// Binds `General - Enabled` inside the upgrade's section (#417). It is bound for every
        /// shipped catalog row, including ones an absent optional provider hides, so the key is
        /// always present to edit. The one exception is a Late Game Upgrades row with that mod
        /// absent: see <see cref="Y4NGZUpgradeCatalog"/>. The switch is local config only; it is
        /// not host-synchronised.
        /// </summary>
        internal bool ResolveEnabled(UpgradeCatalogTable.Entry catalogEntry)
        {
            if (catalogEntry == null)
                throw new ArgumentNullException(nameof(catalogEntry));

            Y4NGZConfigScope config = Y4NGZConfigFiles.Upgrade(catalogEntry);
            ConfigEntry<bool> enabled = Y4NGZConfigFiles.BindMigrated(
                config,
                "General",
                "Enabled",
                true,
                $"Set to false to remove {catalogEntry.ConfigName} from the skill tree and the "
                + "player menu and switch its effects off. Levels already bought stay in the save "
                + "and return when it is enabled again; nothing is refunded. Local setting only.");
            return enabled.Value;
        }

        internal EffectiveUpgradePrices Resolve(UpgradeCatalogTable.Entry catalogEntry, bool active)
        {
            if (catalogEntry == null)
                throw new ArgumentNullException(nameof(catalogEntry));

            Y4NGZConfigScope config = Y4NGZConfigFiles.Upgrade(catalogEntry);
            bool unlockAlreadyDefined = Y4NGZConfigFiles.TargetAlreadyDefines(
                config, "Prices", "Unlock Cost");
            ConfigEntry<int> unlock = Y4NGZConfigFiles.BindMigrated(
                config,
                "Prices",
                "Unlock Cost",
                Math.Max(0, catalogEntry.UnlockPrice),
                new ConfigDescription(
                    $"Tokens required to unlock {catalogEntry.ConfigName} at level 1."
                    + VariantPricingNote(catalogEntry)
                    + ProviderOnlyUnlockNote(catalogEntry),
                    new AcceptableValueRange<int>(0, 999)),
                new LegacyConfigKey("30 - Upgrade Prices", catalogEntry.Id + ".Unlock"));
            if (!unlockAlreadyDefined)
                unlock.Value = ResolveLegacyUnlock(catalogEntry, unlock.Value);

            int[] authoredTiers = catalogEntry.TierPrices ?? Array.Empty<int>();
            int[] legacyTiers = ReadLegacyTiers(catalogEntry, authoredTiers);
            var resolvedTiers = new int[authoredTiers.Length];
            for (int index = 0; index < authoredTiers.Length; index++)
            {
                string key = $"Level {index + 2} Cost";
                bool targetAlreadyDefined = Y4NGZConfigFiles.TargetAlreadyDefines(config, "Prices", key);
                ConfigEntry<int> tier = Y4NGZConfigFiles.BindMigrated(
                    config,
                    "Prices",
                    key,
                    Math.Max(0, authoredTiers[index]),
                    new ConfigDescription(
                        $"Tokens required to purchase {catalogEntry.ConfigName} level {index + 2}."
                        + VariantPricingNote(catalogEntry),
                        new AcceptableValueRange<int>(0, 999)));
                if (!targetAlreadyDefined && legacyTiers != null)
                    tier.Value = legacyTiers[index];
                resolvedTiers[index] = Math.Max(0, tier.Value);
            }

            var effective = new EffectiveUpgradePrices(Math.Max(0, unlock.Value), resolvedTiers);
            if (active)
            {
                _audit.Add(new AuditRow(
                    catalogEntry.Id,
                    catalogEntry.DisplayName,
                    effective,
                    catalogEntry.HasOptionalProviderRequirement));
            }
            return effective;
        }

        /// <summary>
        /// The native rows that keep a reduced unique-only variant in LGU-preferred mode (#435)
        /// still price that variant from these keys, so the player's own numbers decide what a
        /// residual milestone costs.
        /// </summary>
        private static string VariantPricingNote(UpgradeCatalogTable.Entry catalogEntry)
        {
            return NativeUpgradeFamilies.TryGet(catalogEntry.Id, out NativeUpgradeFamily family)
                   && family.UniqueVariant != null
                ? " In LguPreferred mode the unique-only rank's price is the sum of the full-rank "
                  + "steps up to its milestone, less the steps already owned."
                : string.Empty;
        }

        /// <summary>
        /// Field Mechanic's level 1 is not sold without its hacking providers (#441), so its unlock
        /// price goes unused there. The key keeps its name so the value is ready when one returns.
        /// </summary>
        private static string ProviderOnlyUnlockNote(UpgradeCatalogTable.Entry catalogEntry)
        {
            return string.Equals(catalogEntry.Id, NativeUpgradeFamilies.FieldMechanicId, StringComparison.Ordinal)
                ? " Unused without Y4NGZ Ship Systems or LethalCCTV: level 1 is not sold then, "
                  + "door and turret hacking cost the Level 2 and Level 3 prices, and in LguPreferred "
                  + "mode the single turret rank costs Level 2 plus Level 3."
                : string.Empty;
        }

        internal string BuildAuditReport()
        {
            var builder = new StringBuilder();
            int catalogTotal = 0;
            int alwaysAvailableTotal = 0;
            int optionalTotal = 0;
            foreach (AuditRow row in _audit.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                int total = row.Prices.UnlockPrice + row.Prices.TierPrices.Sum();
                catalogTotal += total;
                if (row.Optional)
                    optionalTotal += total;
                else
                    alwaysAvailableTotal += total;
                builder.Append(row.Id)
                    .Append(" | ").Append(row.DisplayName)
                    .Append(" | unlock=").Append(row.Prices.UnlockPrice)
                    .Append(" | tiers=").Append(UpgradePriceMigration.Format(row.Prices.TierPrices))
                    .Append(" | total=").Append(total)
                    .AppendLine();
            }
            builder.Append("CATALOG TOTAL = ").Append(catalogTotal).AppendLine();
            builder.Append("OPTIONAL PROVIDER TOTAL = ").Append(optionalTotal).AppendLine();

            int earnableRanks = RankCatalog.EarnableRankCount;
            int rankTokens = RankCatalog.GetTokenGrantForRankRange(1, earnableRanks);
            int overachieverTokens = Y4NGZUpgradeManager.CurrentPolicy.ModeOf(OverachieverUpgrade.UPGRADE_ID)
                == NativeFamilyMode.Hidden ? 0 : earnableRanks * OverachieverUpgrade.MaxBonusTokensPerRank;
            int lifetimeTokens = rankTokens + overachieverTokens;

            // The margin is the native economy's, not the whole catalog's. Rows that need an
            // optional provider are extra choices a player opted into by installing that mod
            // (38 imported personal upgrades), and they are deliberately more than a career's tokens
            // can buy - that is the point of a skill tree. Folding them in would make the shortfall
            // warning fire on every launch and stop saying anything about the shipped balance.
            int margin = lifetimeTokens - alwaysAvailableTotal;

            builder.Append("LIFETIME TOKEN INCOME = ").Append(lifetimeTokens)
                .Append(" (ranks 1-").Append(earnableRanks).Append(" = ").Append(rankTokens)
                .Append(" + Overachiever max ").Append(overachieverTokens).Append(")").AppendLine();
            builder.Append("MARGIN = ").Append(margin);

            if (margin < 0)
            {
                Plugin.Log?.LogWarning(
                    $"[UpgradePrices] Lifetime token income ({lifetimeTokens}) no longer covers "
                    + $"the always-available catalog cost ({alwaysAvailableTotal}); shortfall "
                    + $"{-margin} token(s).");
            }

            return builder.ToString();
        }

        private static int ResolveLegacyUnlock(UpgradeCatalogTable.Entry entry, int storedValue)
        {
            UpgradePriceMigration.LegacyPrices legacy = UpgradePriceMigration.Find(entry.Id);
            if (legacy == null)
                return storedValue;

            return UpgradePriceMigration.ResolveValue(
                IsLegacySchemaBelowCurrent(entry.HasOptionalProviderRequirement),
                storedValue,
                legacy.UnlockPrices,
                entry.UnlockPrice,
                out _);
        }

        private static int[] ReadLegacyTiers(
            UpgradeCatalogTable.Entry entry,
            int[] authoredTiers)
        {
            int expectedCount = authoredTiers?.Length ?? 0;
            if (!Y4NGZConfigFiles.TryGetLegacy(
                    "30 - Upgrade Prices",
                    entry.Id + ".Tiers",
                    out string stored))
            {
                return null;
            }

            UpgradePriceMigration.LegacyPrices legacy = UpgradePriceMigration.Find(entry.Id);
            if (legacy != null)
            {
                stored = UpgradePriceMigration.ResolveValue(
                    IsLegacySchemaBelowCurrent(entry.HasOptionalProviderRequirement),
                    stored,
                    legacy.TierLists,
                    UpgradePriceMigration.Format(authoredTiers),
                    out _);
            }

            if (expectedCount == 0)
                return string.IsNullOrWhiteSpace(stored) ? Array.Empty<int>() : null;

            string[] parts = stored.Split(',');
            if (parts.Length != expectedCount)
            {
                Plugin.Log?.LogWarning(
                    $"[Config] Ignored invalid legacy price list for {entry.Id}; expected {expectedCount} value(s).");
                return null;
            }

            var values = new int[expectedCount];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(
                        parts[i].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out values[i])
                    || values[i] < 0)
                {
                    Plugin.Log?.LogWarning($"[Config] Ignored invalid legacy price list for {entry.Id}.");
                    return null;
                }
            }

            return values;
        }

        private static bool IsLegacySchemaBelowCurrent(bool optionalProviderPrice)
        {
            string key = optionalProviderPrice ? "SchemaVersion.Company" : "SchemaVersion";
            // F-INFRA-14: TryGet assigns default(T) before it looks the key up, so passing the seed
            // straight in as the out parameter silently overwrote it with 0 on every miss. Seed the
            // pre-stamp schema version explicitly instead.
            if (!Y4NGZConfigFiles.TryGetLegacy("30 - Upgrade Prices", key, out int stored))
                stored = UpgradePriceMigration.LegacySchemaVersion;
            return stored < UpgradePriceMigration.CurrentSchemaVersion;
        }

        private readonly struct AuditRow
        {
            internal AuditRow(string id, string displayName, EffectiveUpgradePrices prices, bool optional)
            {
                Id = id;
                DisplayName = displayName;
                Prices = prices;
                Optional = optional;
            }

            internal string Id { get; }
            internal string DisplayName { get; }
            internal EffectiveUpgradePrices Prices { get; }

            /// <summary>The row needs an optional provider, so it is not part of the shipped economy.</summary>
            internal bool Optional { get; }
        }
    }
}
