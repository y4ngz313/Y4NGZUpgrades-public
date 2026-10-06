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
            NativeVariantBinding variant = Definition.Variant;
            int configuredPrice = variant == null
                // A row with a live ladder (#441) registers live-level prices already.
                ? UpgradePriceMath.GetPriceForLevel(level, Definition.MaxTier, UnlockPrice, Prices)
                // A unique-only rank costs the full-native steps up to its milestone that this
                // save has not already bought (#435), so the quote moves with the player's own
                // configured prices and with the full ranks they own. A live ladder prices the
                // unique rank its next live level writes.
                : NativeUpgradeFamilies.UniqueRankPrice(
                    variant.Variant,
                    Definition.LiveLadder?.PricedStoredLevel(level) ?? level,
                    Y4NGZUpgradeManager.GetFullNativeLevel(Id),
                    variant.FullUnlockPrice,
                    variant.FullTierPrices);
            return PurchaseTokenCostPolicy.Resolve(configuredPrice, Plugin.PurchasesCostTokens);
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
