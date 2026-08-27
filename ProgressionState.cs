using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    internal sealed class ProgressionState
    {
        internal int RankXp;
        internal int UpgradeCurrency;
        internal readonly HashSet<string> ExternalTokenGrantIds =
            new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> CompletedTokenSpendIds =
            new HashSet<string>(StringComparer.Ordinal);

        internal ProgressionState Clone()
        {
            var clone = new ProgressionState
            {
                RankXp = RankXp,
                UpgradeCurrency = UpgradeCurrency
            };

            clone.ExternalTokenGrantIds.UnionWith(ExternalTokenGrantIds);
            clone.CompletedTokenSpendIds.UnionWith(CompletedTokenSpendIds);
            return clone;
        }
    }
}
