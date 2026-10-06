using System;
using System.Collections.Generic;

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
        NotEnoughCurrency,
        ProviderUnavailable,
        /// <summary>
        /// The resolved catalog moved between the quote and the commit - a native family changed
        /// mode, or the provider that supplies it did. The purchase is refused even when the new
        /// numeric price happens to match, because the thing being bought is not the same (#435).
        /// </summary>
        CatalogChanged
    }

    /// <summary>Token wallet seam. Reserve/commit is the two-phase spend in ProgressionManager.</summary>
    internal interface IUpgradeWallet
    {
        bool TryReserve(string transactionId, int amount);
        bool Commit(string transactionId);
        void Cancel(string transactionId);
    }

    internal interface IUpgradePurchaseEffect
    {
        bool TryApply(int level);
        void Rollback();
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
        /// <param name="record">
        /// Which per-save rank dictionary this purchase reads, writes and rolls back. Updating
        /// only the read would corrupt the other record (#435).
        /// </param>
        /// <param name="contextUnchanged">
        /// Re-asserts that the resolved catalog is still the one that produced the quote. Checked
        /// everywhere the save identity is re-checked; false cancels the transaction.
        /// </param>
        /// <param name="ladder">
        /// Non-null when the row sells fewer levels than its record stores (#441). Then
        /// <paramref name="maxTier"/>, <paramref name="priceForLevel"/> and <paramref name="newLevel"/>
        /// count live levels, and the record receives the next live level's stored rank.
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
            out int newLevel,
            IUpgradePurchaseEffect effect = null,
            UpgradeRankRecord record = UpgradeRankRecord.Full,
            Func<bool> contextUnchanged = null,
            LiveRankLadder ladder = null)
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

            int level = ladder == null
                ? store.GetLevel(upgradeId, maxTier, record)
                : ladder.Project(store.GetLevel(upgradeId, ladder.StoredCap, record));
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

            if (!ContextHolds(contextUnchanged))
            {
                wallet.Cancel(transactionId);
                return UpgradePurchaseOutcome.CatalogChanged;
            }

            int storedRank = ladder == null ? level + 1 : ladder.StoredRankFor(level + 1);
            if (effect != null)
            {
                bool applied;
                try { applied = effect.TryApply(storedRank); }
                catch { applied = false; }
                if (!applied)
                {
                    try { effect.Rollback(); }
                    finally { wallet.Cancel(transactionId); }
                    return UpgradePurchaseOutcome.ProviderUnavailable;
                }
                if (!store.SwitchToCurrentSave() || !ReferenceEquals(store.Current, state))
                {
                    try { effect.Rollback(); }
                    finally { wallet.Cancel(transactionId); }
                    return UpgradePurchaseOutcome.SaveUnavailable;
                }
            }

            if (!ContextHolds(contextUnchanged))
            {
                try { effect?.Rollback(); }
                finally { wallet.Cancel(transactionId); }
                return UpgradePurchaseOutcome.CatalogChanged;
            }

            Dictionary<string, int> ranks = state.RanksFor(record);
            bool hadRank = ranks.TryGetValue(upgradeId, out int rankBefore);
            ranks[upgradeId] = storedRank;
            if (price > 0 && !wallet.Commit(transactionId))
            {
                if (hadRank)
                    ranks[upgradeId] = rankBefore;
                else
                    ranks.Remove(upgradeId);
                try { effect?.Rollback(); }
                finally { wallet.Cancel(transactionId); }
                return UpgradePurchaseOutcome.NotEnoughCurrency;
            }

            chargedPrice = price;
            newLevel = level + 1;
            return UpgradePurchaseOutcome.Success;
        }

        private static bool ContextHolds(Func<bool> contextUnchanged)
        {
            if (contextUnchanged == null)
                return true;

            try { return contextUnchanged(); }
            catch { return false; }
        }
    }
}
