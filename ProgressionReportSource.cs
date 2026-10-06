using System;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// A player-facing reason XP was paid. The payout remains authoritative elsewhere; this is
    /// the immutable presentation snapshot retained through the performance report.
    /// </summary>
    public enum ProgressionReportSourceKind
    {
        ScrapDiscovered,
        ScrapDelivered,
        MonsterKill,
        Survival,
        BodyRetrieval,
        MainframeHack,
        Clutch,
        Contract,
        // #459: appended, never reordered - the member names are the report's category identity.
        ShipBatteryReplaced,
        ShipApparatusDocked
    }

    public sealed class ProgressionReportSource
    {
        public string Key { get; }
        public ProgressionReportSourceKind Kind { get; }
        public string Subject { get; }
        public int Count { get; }
        public int Amount { get; }
        public int Order { get; }

        /// <summary>
        /// Credits the category carried this round, for the sources whose sentence states a haul
        /// rather than a count (#456: "Found $412 of scrap"). Zero where the sentence has no
        /// currency to state.
        /// </summary>
        public int Value { get; }

        public ProgressionReportSource(
            string key,
            ProgressionReportSourceKind kind,
            string subject,
            int count,
            int amount,
            int order,
            int value = 0)
        {
            Key = string.IsNullOrWhiteSpace(key) ? kind.ToString() : key.Trim();
            Kind = kind;
            Subject = subject?.Trim() ?? string.Empty;
            Count = Math.Max(0, count);
            Amount = Math.Max(0, amount);
            Order = Math.Max(0, order);
            Value = Math.Max(0, value);
        }
    }
}
