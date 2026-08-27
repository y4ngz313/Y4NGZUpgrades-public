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
        {
            Id = NormalizeId(id);
            Name = name ?? id;
            Description = description ?? string.Empty;
            UnlockPrice = Math.Max(0, unlockPrice);
            Prices = tierPrices ?? Array.Empty<int>();
            SharedUpgrade = sharedUpgrade;
            Visible = visible;
            PurchaseMode = purchaseMode;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public int UnlockPrice { get; }
        public int[] Prices { get; }
        public bool SharedUpgrade { get; }
        public bool Visible { get; }
        public Y4NGZPurchaseMode PurchaseMode { get; }
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
}
