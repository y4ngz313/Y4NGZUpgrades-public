using System;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Pure tier-price math shared by the upgrade node, the menu price label, and the purchase
    /// transaction, so a quoted price and a charged price can only ever differ by the level they
    /// were read at - never by the formula. Unity-free so the deterministic checks use it directly.
    /// </summary>
    internal static class UpgradePriceMath
    {
        /// <summary>
        /// Price to advance from <paramref name="level"/> to the next tier.
        /// <see cref="int.MaxValue"/> means "not purchasable" (already maxed, or no authored price).
        /// </summary>
        internal static int GetPriceForLevel(int level, int maxTier, int unlockPrice, int[] tierPrices)
        {
            if (level >= maxTier)
                return int.MaxValue;

            int raw;
            if (level <= 0)
            {
                raw = unlockPrice;
            }
            else
            {
                int index = level - 1;
                raw = tierPrices != null && index < tierPrices.Length ? tierPrices[index] : int.MaxValue;
            }

            return raw == int.MaxValue ? raw : Math.Max(0, raw);
        }

        internal static int GetMaxTier(int[] tierPrices)
        {
            return Math.Max(1, (tierPrices?.Length ?? 0) + 1);
        }
    }
}
