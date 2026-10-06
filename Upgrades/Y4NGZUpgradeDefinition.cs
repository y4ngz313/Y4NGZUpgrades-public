using System;

namespace Y4NGZUpgrades.Upgrades
{
    public sealed class Y4NGZUpgradeDefinition
    {
        public Y4NGZUpgradeDefinition(
            string id,
            string name,
            string description,
            int unlockPrice,
            int[] tierPrices,
            bool sharedUpgrade = false,
            bool visible = true,
            Y4NGZPurchaseMode purchaseMode = Y4NGZPurchaseMode.UpgradeCurrency)
            : this(id, name, description, unlockPrice, tierPrices, sharedUpgrade, visible, purchaseMode, null)
        {
        }

        /// <summary>
        /// The unique-only native variant a resolved policy registers (#435), and the live ladder
        /// of a row that sells fewer levels than it stores (#441). Internal because both types
        /// are: the public surface never advertises which mode a row is in, it just reads the
        /// definition it was given.
        /// </summary>
        internal Y4NGZUpgradeDefinition(
            string id,
            string name,
            string description,
            int unlockPrice,
            int[] tierPrices,
            bool sharedUpgrade,
            bool visible,
            Y4NGZPurchaseMode purchaseMode,
            NativeVariantBinding variant,
            LiveRankLadder liveLadder = null)
        {
            Id = NormalizeId(id);
            Name = name ?? id;
            Description = description ?? string.Empty;
            UnlockPrice = Math.Max(0, unlockPrice);
            Prices = tierPrices ?? Array.Empty<int>();
            SharedUpgrade = sharedUpgrade;
            Visible = visible;
            PurchaseMode = purchaseMode;
            Variant = variant;
            LiveLadder = liveLadder;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public int UnlockPrice { get; }
        public int[] Prices { get; }
        public bool SharedUpgrade { get; }
        public bool Visible { get; }
        public Y4NGZPurchaseMode PurchaseMode { get; }

        /// <summary>
        /// Non-null only on a unique-only native variant (#435). The definition's own
        /// <see cref="Prices"/> are the fresh-save aggregates; the binding carries the full row's
        /// effective prices so a live quote can credit the full-native ranks this save owns.
        /// </summary>
        internal NativeVariantBinding Variant { get; }

        /// <summary>
        /// Non-null only when some stored ranks are not sold in this process (#441). Then
        /// <see cref="MaxTier"/> and the prices count live levels, the stored record keeps its
        /// own ranks, and effects read those stored ranks directly.
        /// </summary>
        internal LiveRankLadder LiveLadder { get; }
        public int MaxTier => Math.Max(1, Prices.Length + 1);

        internal static string NormalizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            char[] chars = value.Trim().ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                    continue;
                chars[i] = '_';
            }

            return new string(chars).Trim('_');
        }
    }

    /// <summary>
    /// Ties a registered unique-only definition back to the full native row it was reduced from,
    /// so pricing stays a live aggregation of the player's configured full-rank prices rather
    /// than a second authored price table.
    /// </summary>
    internal sealed class NativeVariantBinding
    {
        internal NativeVariantBinding(NativeUpgradeVariant variant, int fullUnlockPrice, int[] fullTierPrices)
        {
            Variant = variant ?? throw new ArgumentNullException(nameof(variant));
            FullUnlockPrice = Math.Max(0, fullUnlockPrice);
            FullTierPrices = fullTierPrices ?? Array.Empty<int>();
        }

        internal NativeUpgradeVariant Variant { get; }
        internal int FullUnlockPrice { get; }
        internal int[] FullTierPrices { get; }
    }
}
