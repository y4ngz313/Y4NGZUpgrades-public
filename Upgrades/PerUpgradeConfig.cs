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
                    $"Tokens required to unlock {catalogEntry.DisplayName} at level 1.",
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
                        $"Tokens required to purchase {catalogEntry.DisplayName} level {index + 2}.",
                        new AcceptableValueRange<int>(0, 999)));
                if (!targetAlreadyDefined && legacyTiers != null)
                    tier.Value = legacyTiers[index];
                resolvedTiers[index] = Math.Max(0, tier.Value);
            }

            var effective = new EffectiveUpgradePrices(Math.Max(0, unlock.Value), resolvedTiers);
            if (active)
                _audit.Add(new AuditRow(catalogEntry.Id, catalogEntry.DisplayName, effective));
            return effective;
        }

        internal string BuildAuditReport()
        {
            var builder = new StringBuilder();
            int catalogTotal = 0;
            foreach (AuditRow row in _audit.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                int total = row.Prices.UnlockPrice + row.Prices.TierPrices.Sum();
                catalogTotal += total;
                builder.Append(row.Id)
                    .Append(" | ").Append(row.DisplayName)
                    .Append(" | unlock=").Append(row.Prices.UnlockPrice)
                    .Append(" | tiers=").Append(UpgradePriceMigration.Format(row.Prices.TierPrices))
                    .Append(" | total=").Append(total)
                    .AppendLine();
            }
            builder.Append("CATALOG TOTAL = ").Append(catalogTotal).AppendLine();

            int earnableRanks = RankCatalog.EarnableRankCount;
            int rankTokens = RankCatalog.GetTokenGrantForRankRange(1, earnableRanks);
            int overachieverTokens = earnableRanks * OverachieverUpgrade.MaxBonusTokensPerRank;
            int lifetimeTokens = rankTokens + overachieverTokens;
            int margin = lifetimeTokens - catalogTotal;

            builder.Append("LIFETIME TOKEN INCOME = ").Append(lifetimeTokens)
                .Append(" (ranks 1-").Append(earnableRanks).Append(" = ").Append(rankTokens)
                .Append(" + Overachiever max ").Append(overachieverTokens).Append(")").AppendLine();
            builder.Append("MARGIN = ").Append(margin);

            if (margin < 0)
            {
                Plugin.Log?.LogWarning(
                    $"[UpgradePrices] Lifetime token income ({lifetimeTokens}) no longer covers "
                    + $"the active catalog cost ({catalogTotal}); shortfall {-margin} token(s).");
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
            int stored = UpgradePriceMigration.LegacySchemaVersion;
            Y4NGZConfigFiles.TryGetLegacy("30 - Upgrade Prices", key, out stored);
            return stored < UpgradePriceMigration.CurrentSchemaVersion;
        }

        private readonly struct AuditRow
        {
            internal AuditRow(string id, string displayName, EffectiveUpgradePrices prices)
            {
                Id = id;
                DisplayName = displayName;
                Prices = prices;
            }

            internal string Id { get; }
            internal string DisplayName { get; }
            internal EffectiveUpgradePrices Prices { get; }
        }
    }
}
