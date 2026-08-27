using System.Collections.Generic;
using UnityEngine;
using Y4NGZUpgrades;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Gui
{
    internal sealed class CurrencyManager
    {
        internal static readonly CurrencyManager Instance = new CurrencyManager();

        // Deliberately asymmetric: the getter reports the SPENDABLE balance (banked minus
        // outstanding reservations) so affordability reads the same number the spend subtracts
        // from, while the setter writes the BANKED total. Never read-modify-write this property
        // (`CurrencyAmount -= n`) - during an open reservation that would erase the reserved
        // tokens. Spend through TrySpend; set an absolute banked value through
        // SetCurrencyAmount/ProgressionApi.SetUpgradeCurrency. #213.
        public int CurrencyAmount
        {
            get => ProgressionApi.AvailableTokens;
            set => ProgressionApi.SetUpgradeCurrency(value);
        }

        public int TokensAmount
        {
            get => CurrencyAmount;
            set => CurrencyAmount = value;
        }

        public int TokenAmount
        {
            get => CurrencyAmount;
            set => CurrencyAmount = value;
        }

        public int MarksAmount
        {
            get => CurrencyAmount;
            set => CurrencyAmount = value;
        }

        public int BxpAmount
        {
            get => CurrencyAmount;
            set => CurrencyAmount = value;
        }

        /// <summary>
        /// Spends against the same spendable balance the affordability check reads, and subtracts
        /// from the banked total so an outstanding reservation is not erased. #213.
        /// </summary>
        public bool TrySpend(int amount)
        {
            if (amount <= 0)
                return true;
            if (ProgressionApi.AvailableTokens < amount)
                return false;

            ProgressionApi.AddTokens(-amount);
            return true;
        }

        public void SetCurrencyAmount(int amount) => CurrencyAmount = amount;
        public void SetTokensAmount(int amount) => CurrencyAmount = amount;
        public void SetTokenAmount(int amount) => CurrencyAmount = amount;
        public void SetMarksAmount(int amount) => CurrencyAmount = amount;
        public void SetBxpAmount(int amount) => CurrencyAmount = amount;

        public int GetCurrencyAmountFromCredits(int credits)
        {
            return Mathf.Max(0, credits);
        }

        // No networking exists for token transfers yet, so the request is
        // refused outright. Granting the amount locally would either mint
        // tokens or silently no-op depending on the caller.
        public bool RequestPlayerTokenTrade(ulong targetId, int tokenAmount)
        {
            Y4NGZUpgrades.Interactive.Plugin.Log?.LogWarning(
                $"[Y4NGZ Trade] Refused transfer of {tokenAmount} token(s) to {targetId}: trade sync is not available yet.");
            return false;
        }
    }

    internal static class UpgradeApi
    {
        public static IReadOnlyList<Y4NGZUpgradeNode> GetUpgradeNodes()
        {
            return Y4NGZUpgradeManager.GetUpgradeNodes();
        }

        /// <param name="quotedPrice">
        /// Exact price the button label showed. The manager refuses the transaction if the live
        /// price disagrees, so the caller must surface the result rather than assume success. #213.
        /// </param>
        public static Y4NGZUpgradePurchaseResult TriggerUpgradeRankup(
            Y4NGZUpgradeNode node,
            int quotedPrice = UpgradePurchaseFlow.NoQuotedPrice)
        {
            if (node == null)
                return Y4NGZUpgradePurchaseResult.UnknownUpgrade;

            Y4NGZUpgradePurchaseResult result = Y4NGZUpgradeManager.Purchase(node.Id, quotedPrice);
            if (result != Y4NGZUpgradePurchaseResult.Success)
                Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu] Purchase of '{node.Name}' returned {result}.");
            return result;
        }
    }
}
