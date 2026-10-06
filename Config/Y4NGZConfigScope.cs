using System;

namespace Y4NGZUpgrades.Config
{
    internal sealed class Y4NGZConfigScope
    {
        internal Y4NGZConfigScope(
            string groupName,
            string keyPrefix,
            string previousUnifiedScopeName,
            string legacySplitFileName,
            LegacyConfigDocument splitDocument,
            string previousKeyPrefix = null,
            string previousGroupName = null,
            int previousTier = 0)
        {
            GroupName = groupName ?? string.Empty;
            KeyPrefix = keyPrefix ?? string.Empty;
            PreviousKeyPrefix = previousKeyPrefix ?? KeyPrefix;
            PreviousGroupName = string.IsNullOrEmpty(previousGroupName) ? GroupName : previousGroupName;
            RenamedFromGroupName = Y4NGZConfigLayout.RenamedFromGroupName(GroupName);
            PreviousTier = previousTier;
            PreviousUnifiedScopeName = previousUnifiedScopeName ?? string.Empty;
            LegacySplitFileName = legacySplitFileName ?? string.Empty;
            SplitDocument = splitDocument;
        }

        internal string GroupName { get; }
        internal string KeyPrefix { get; }
        internal string PreviousKeyPrefix { get; }

        /// <summary>
        /// The group these keys lived in before they moved (#442); <see cref="GroupName"/> when
        /// they never moved. Key names do not change with the move.
        /// </summary>
        internal string PreviousGroupName { get; }

        /// <summary>
        /// The name this scope's live group shipped under before it was renamed (#435: [General]
        /// for [LGU]), or null. Derived from the live group, so every LGU scope, including one
        /// built by <see cref="MovedFrom"/>, reads its [General] copy. Key names did not change.
        /// </summary>
        internal string RenamedFromGroupName { get; }

        /// <summary>
        /// The tier an imported row's default prices were derived from before #442 re-tiered it,
        /// or 0 when the scope carries no price defaults to compare.
        /// </summary>
        internal int PreviousTier { get; }

        internal bool HasPreviousGroup =>
            !string.Equals(PreviousGroupName, GroupName, StringComparison.OrdinalIgnoreCase);

        internal bool HasRenamedGroup => RenamedFromGroupName != null;

        /// <summary>
        /// This group's scope as seen by keys that moved here from <paramref name="previous"/>:
        /// the live group and prefix are this scope's, the migration sources are the old one's.
        /// </summary>
        internal Y4NGZConfigScope MovedFrom(Y4NGZConfigScope previous)
        {
            if (previous == null)
                throw new ArgumentNullException(nameof(previous));
            return new Y4NGZConfigScope(
                GroupName,
                KeyPrefix,
                previous.PreviousUnifiedScopeName,
                previous.LegacySplitFileName,
                previous.SplitDocument,
                previous.KeyPrefix,
                previous.GroupName);
        }

        /// <summary>
        /// Resolves one key against its live group, its renamed group's copy and its previous
        /// group's copy. A previous-group price still equal to its old tier default takes
        /// <paramref name="currentDefault"/> until <paramref name="movedDefaultsMarked"/>; a
        /// renamed-group copy always carries verbatim.
        /// </summary>
        internal Y4NGZGroupMove ResolveGroupMove<T>(
            LegacyConfigDocument document,
            string section,
            string key,
            T currentDefault,
            bool movedDefaultsMarked,
            out T value)
        {
            bool replacePreviousDefault = false;
            T previousDefault = default;
            if (PreviousTier > 0 && !movedDefaultsMarked && typeof(T) == typeof(int)
                && Y4NGZConfigMigration.TryGetTierDefaultPrice(PreviousTier, section, key, out int oldPrice))
            {
                replacePreviousDefault = true;
                previousDefault = (T)(object)oldPrice;
            }

            return Y4NGZConfigMigration.ResolveGroupMove(
                document,
                GroupName,
                Key(section, key),
                RenamedFromGroupName,
                PreviousGroupName,
                PreviousKey(section, key),
                replacePreviousDefault,
                previousDefault,
                currentDefault,
                out value);
        }

        internal string PreviousKey(string subsection, string key) =>
            Y4NGZConfigLayout.GroupedKey(PreviousKeyPrefix, subsection, key);
        internal string PreviousUnifiedScopeName { get; }
        internal string LegacySplitFileName { get; }
        internal LegacyConfigDocument SplitDocument { get; }

        internal string Key(string subsection, string key)
        {
            return Y4NGZConfigLayout.GroupedKey(KeyPrefix, subsection, key);
        }

        internal string PreviousSection(string subsection)
        {
            return Y4NGZConfigLayout.PreviousUnifiedSection(
                PreviousUnifiedScopeName,
                subsection);
        }
    }
}
