namespace Y4NGZUpgrades.Upgrades
{
    public sealed class Y4NGZUpgradeMigrationRule
    {
        public Y4NGZUpgradeMigrationRule(string sourceIdOrName, string targetId, string reason = null)
        {
            SourceId = Y4NGZUpgradeDefinition.NormalizeId(sourceIdOrName);
            TargetId = Y4NGZUpgradeDefinition.NormalizeId(targetId);
            Reason = reason ?? string.Empty;
        }

        public string SourceId { get; }
        public string TargetId { get; }
        public string Reason { get; }
    }
}
