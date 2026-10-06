using System;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Resolves the price a local purchase should display and charge without mutating the
    /// configured price. Keeping this Unity-free lets the transaction checks prove that turning
    /// token costs back on restores the authored amount.
    /// </summary>
    internal static class PurchaseTokenCostPolicy
    {
        internal static int Resolve(int configuredCost, bool purchasesCostTokens)
        {
            if (configuredCost == int.MaxValue)
                return configuredCost;

            return purchasesCostTokens ? Math.Max(0, configuredCost) : 0;
        }
    }
}
