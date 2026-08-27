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
        Contract
    }

    public sealed class ProgressionReportSource
    {
        public string Key { get; }
        public ProgressionReportSourceKind Kind { get; }
        public string Subject { get; }
        public int Count { get; }
        public int Amount { get; }
        public int Order { get; }

        public ProgressionReportSource(
            string key,
            ProgressionReportSourceKind kind,
            string subject,
            int count,
            int amount,
            int order)
        {
            Key = string.IsNullOrWhiteSpace(key) ? kind.ToString() : key.Trim();
            Kind = kind;
            Subject = subject?.Trim() ?? string.Empty;
            Count = Math.Max(0, count);
            Amount = Math.Max(0, amount);
            Order = Math.Max(0, order);
        }
    }
}
