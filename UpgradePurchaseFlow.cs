using System;

namespace Y4NGZUpgrades
{
    internal enum UpgradePurchaseOutcome
    {
        Success,
        Maxed,
        /// <summary>No save key resolves, so nothing bought here could be persisted.</summary>
        SaveUnavailable,
        /// <summary>The recomputed price disagrees with the price the caller quoted.</summary>
        PriceChanged,
        NotEnoughCurrency
    }

    /// <summary>Token wallet seam. Reserve/commit is the two-phase spend in ProgressionManager.</summary>
    internal interface IUpgradeWallet
    {
        bool TryReserve(string transactionId, int amount);
        bool Commit(string transactionId);
        void Cancel(string transactionId);
    }

    /// <summary>
    /// The one place a token spend and a level write are tied together. Unity-free so the
    /// deterministic checks exercise the production transaction rather than a copy of it. #213.
    /// </summary>
    internal static class UpgradePurchaseFlow
    {
        internal const int NoQuotedPrice = -1;

        /// <summary>
        /// Resolve the save key, honour the quoted price, reserve, write the level, commit. Any
        /// failure leaves both the wallet and the stored level exactly as they were.
        /// </summary>
        /// <param name="quotedPrice">
        /// Price the caller (menu) showed the player, or <see cref="NoQuotedPrice"/> to accept
        /// whatever the current state prices at.
        /// </param>
        internal static UpgradePurchaseOutcome TryPurchase(
            UpgradeSaveStateStore store,
            string upgradeId,
            int maxTier,
            Func<int, int> priceForLevel,
            int quotedPrice,
            IUpgradeWallet wallet,
            string transactionId,
            out int chargedPrice,
            out int newLevel)
        {
            chargedPrice = 0;
            newLevel = 0;
            if (store == null || priceForLevel == null || wallet == null
                || string.IsNullOrWhiteSpace(upgradeId) || string.IsNullOrWhiteSpace(transactionId))
                return UpgradePurchaseOutcome.SaveUnavailable;

            if (!store.SwitchToCurrentSave())
                return UpgradePurchaseOutcome.SaveUnavailable;

            UpgradeSaveState state = store.Current;
            if (state == null)
                return UpgradePurchaseOutcome.SaveUnavailable;

            int level = store.GetLevel(upgradeId, maxTier);
            if (level >= maxTier)
                return UpgradePurchaseOutcome.Maxed;

            int price = priceForLevel(level);
            if (price == int.MaxValue)
                return UpgradePurchaseOutcome.Maxed;
            if (quotedPrice != NoQuotedPrice && quotedPrice != price)
                return UpgradePurchaseOutcome.PriceChanged;

            if (price > 0 && !wallet.TryReserve(transactionId, price))
                return UpgradePurchaseOutcome.NotEnoughCurrency;

            // The wallet call can itself re-point the live save (host identity arriving mid-menu),
            // so the state that gets the level must still be the state that was priced.
            if (!store.SwitchToCurrentSave() || !ReferenceEquals(store.Current, state))
            {
                wallet.Cancel(transactionId);
                return UpgradePurchaseOutcome.SaveUnavailable;
            }

            state.Levels[upgradeId] = level + 1;
            if (price > 0 && !wallet.Commit(transactionId))
            {
                if (level <= 0)
                    state.Levels.Remove(upgradeId);
                else
                    state.Levels[upgradeId] = level;
                wallet.Cancel(transactionId);
                return UpgradePurchaseOutcome.NotEnoughCurrency;
            }

            chargedPrice = price;
            newLevel = level + 1;
            return UpgradePurchaseOutcome.Success;
        }
    }
}
