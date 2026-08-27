using System;
using UnityEngine;

namespace Y4NGZUpgrades.Upgrades
{
    public sealed class Y4NGZUpgradeNode : IComparable
    {
        internal Y4NGZUpgradeNode(Y4NGZUpgradeDefinition definition)
        {
            Definition = definition;
        }

        public Y4NGZUpgradeDefinition Definition { get; }
        public string Id => Definition.Id;
        public string Name => Definition.Name;
        public string OriginalName => Definition.Name;
        public int[] Prices => Definition.Prices;
        public int UnlockPrice => Definition.UnlockPrice;
        public string Description => Definition.Description;
        public bool SharedUpgrade => Definition.SharedUpgrade;
        public bool Visible => Definition.Visible;
        public bool Refundable => false;
        public float RefundPercentage => 1f;

        public int CurrentUpgrade
        {
            get
            {
                int level = Y4NGZUpgradeManager.GetLevel(Id);
                return level <= 0 ? 0 : Mathf.Clamp(level - 1, 0, MaxUpgrade);
            }
        }

        public int MaxUpgrade => Mathf.Max(0, Definition.MaxTier - 1);
        public bool Unlocked => Y4NGZUpgradeManager.GetLevel(Id) > 0;

        public int CompareTo(object obj)
        {
            return obj is Y4NGZUpgradeNode other
                ? string.Compare(Name, other.Name, StringComparison.OrdinalIgnoreCase)
                : -1;
        }

        public int GetCurrentPrice()
        {
            return GetPriceForLevel(Y4NGZUpgradeManager.GetLevel(Id));
        }

        internal int GetPriceForLevel(int level)
        {
            return UpgradePriceMath.GetPriceForLevel(level, Definition.MaxTier, UnlockPrice, Prices);
        }

        public int GetCurrentLevel()
        {
            return Mathf.Clamp(Y4NGZUpgradeManager.GetLevel(Id), 0, Definition.MaxTier);
        }

        public int GetRemainingLevels()
        {
            return Mathf.Max(0, Definition.MaxTier - GetCurrentLevel());
        }
    }
}
