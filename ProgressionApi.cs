namespace Y4NGZUpgrades
{
    public static class ProgressionApi
    {
        public static int RankXp => ProgressionManager.CurrentRankXp;
        public static int UpgradeCurrency => ProgressionManager.CurrentUpgradeCurrency;
        public static int Tokens => UpgradeCurrency;
        public static int Token => UpgradeCurrency;
        public static int Marks => UpgradeCurrency;
        public static int Bxp => UpgradeCurrency;
        public static int AvailableTokens => ProgressionManager.AvailableUpgradeCurrency;
        public static int RankIndex => RankCatalog.GetRankIndex(RankXp);
        public static string RankName => RankCatalog.GetRank(RankXp).Name;
        public static float RankProgress => RankCatalog.GetProgress(RankXp);

        public static void AwardContractXp(string key, string label, int amount, bool oncePerRound = true)
        {
            ProgressionManager.AddContractXp(key, label, amount, oncePerRound);
        }

        public static bool TrySpendUpgradeCurrency(int amount)
        {
            return ProgressionManager.TrySpendUpgradeCurrency(amount);
        }

        public static bool TryReserveTokenSpend(string transactionId, int amount)
        {
            return ProgressionManager.TryReserveTokenSpend(transactionId, amount);
        }

        public static bool CommitTokenSpend(string transactionId)
        {
            return ProgressionManager.CommitTokenSpend(transactionId);
        }

        public static bool CancelTokenSpend(string transactionId)
        {
            return ProgressionManager.CancelTokenSpend(transactionId);
        }

        // Guarded surface (#195): unrestricted token mutation is not part of the public API.
        // Purchases go through TryReserveTokenSpend/CommitTokenSpend or TrySpendUpgradeCurrency,
        // and one-time external awards go through ExternalTokenGrantApi, which is ledgered per
        // save so a grant can never be replayed.
        internal static void AddUpgradeCurrency(int amount)
        {
            ProgressionManager.AddUpgradeCurrency(amount);
        }

        internal static void SetUpgradeCurrency(int amount)
        {
            ProgressionManager.SetUpgradeCurrency(amount);
        }

        internal static void AddTokens(int amount)
        {
            AddUpgradeCurrency(amount);
        }

        internal static void SetTokens(int amount)
        {
            SetUpgradeCurrency(amount);
        }

        internal static bool TrySetTokensForTesting(int amount)
        {
            return ProgressionManager.TrySetUpgradeCurrencyForTesting(amount);
        }

        internal static void AddToken(int amount)
        {
            AddUpgradeCurrency(amount);
        }

        internal static void SetToken(int amount)
        {
            SetUpgradeCurrency(amount);
        }

        internal static void AddMarks(int amount)
        {
            AddUpgradeCurrency(amount);
        }

        internal static void SetMarks(int amount)
        {
            SetUpgradeCurrency(amount);
        }

        internal static void AddBxp(int amount)
        {
            AddUpgradeCurrency(amount);
        }

        internal static void SetBxp(int amount)
        {
            SetUpgradeCurrency(amount);
        }

        public static RoundXpBreakdown GetLastRoundBreakdown()
        {
            return ProgressionManager.LastRoundBreakdown?.Clone();
        }

        /// <summary>
        /// Multiplier the Scavenger upgrade applies to a picked-up ammo box, 1 when it is not
        /// owned. Better Armory owns ammo pickups and reads this by name (#267); the effect is
        /// declared here rather than left on the internal accessor so a rename inside
        /// <c>Y4NGZCustomUpgradeAccessors</c> cannot silently drop the bonus.
        /// </summary>
        public static float GetScavengerAmmoMultiplier()
        {
            return Upgrades.ScavengerUpgrade.GetAmmoMultiplier();
        }
    }
}
