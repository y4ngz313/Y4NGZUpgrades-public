namespace Y4NGZUpgrades.Config
{
    internal enum Y4NGZConfigMigrationSource
    {
        None = 0,
        PreviousUnified = 1,
        SplitFile = 2,
        LegacyMonolith = 3
    }

    /// <summary>
    /// Unity-free precedence for the eight-group Gale layout. A value already using the grouped
    /// schema wins implicitly; otherwise the fine-grained unified schema wins over a split file,
    /// which wins over the original com.y4ngz.upgrades.cfg monolith.
    /// </summary>
    internal static class Y4NGZConfigMigration
    {
        internal static bool HasUserValue(
            LegacyConfigDocument unified,
            string targetSection,
            string targetKey,
            string previousUnifiedSection,
            string previousUnifiedKey,
            LegacyConfigDocument split,
            string splitSection,
            string splitKey)
        {
            return (unified != null && unified.Contains(targetSection, targetKey))
                   || (unified != null
                       && unified.Contains(previousUnifiedSection, previousUnifiedKey))
                   || (split != null && split.Contains(splitSection, splitKey));
        }

        internal static bool TryGetFallback<T>(
            LegacyConfigDocument previousUnified,
            string previousUnifiedSection,
            string previousUnifiedKey,
            LegacyConfigDocument split,
            string splitSection,
            string splitKey,
            LegacyConfigDocument legacyMonolith,
            LegacyConfigKey[] legacyKeys,
            out T value,
            out Y4NGZConfigMigrationSource source)
        {
            if (previousUnified != null
                && previousUnified.TryGet(previousUnifiedSection, previousUnifiedKey, out value))
            {
                source = Y4NGZConfigMigrationSource.PreviousUnified;
                return true;
            }

            if (split != null && split.TryGet(splitSection, splitKey, out value))
            {
                source = Y4NGZConfigMigrationSource.SplitFile;
                return true;
            }

            if (legacyMonolith != null && legacyKeys != null)
            {
                for (int i = 0; i < legacyKeys.Length; i++)
                {
                    LegacyConfigKey legacyKey = legacyKeys[i];
                    if (!legacyMonolith.TryGet(legacyKey.Section, legacyKey.Key, out value))
                        continue;

                    source = Y4NGZConfigMigrationSource.LegacyMonolith;
                    return true;
                }
            }

            value = default;
            source = Y4NGZConfigMigrationSource.None;
            return false;
        }
    }
}
