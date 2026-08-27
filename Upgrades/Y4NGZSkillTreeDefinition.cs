using System;

namespace Y4NGZUpgrades.Upgrades
{
    public enum Y4NGZSkillTreeClass
    {
        Enforcer,
        Ghost,
        Technician,
        Foreman
    }

    public sealed class Y4NGZSkillTreeDefinition
    {
        public Y4NGZSkillTreeDefinition(
            Y4NGZSkillTreeClass treeClass,
            string displayName,
            int order,
            int[] tierGateRequirements)
        {
            TreeClass = treeClass;
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? treeClass.ToString()
                : displayName;
            Id = Y4NGZUpgradeDefinition.NormalizeId(DisplayName);
            Order = Math.Max(0, order);
            TierGateRequirements = tierGateRequirements ?? Array.Empty<int>();
        }

        public Y4NGZSkillTreeClass TreeClass { get; }
        public string Id { get; }
        public string DisplayName { get; }
        public int Order { get; }
        public int[] TierGateRequirements { get; }

        /// <summary>
        /// Levels that must already be bought in this tree before <paramref name="tier"/> opens.
        /// A tier the ladder does not cover throws: silently treating it as ungated would ship an
        /// authoring mistake as a free tier.
        /// </summary>
        public int GetGateRequirement(int tier)
        {
            if (tier <= 1)
                return 0;

            int index = tier - 1;
            if (index >= TierGateRequirements.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tier), $"Skill tree '{DisplayName}' has no gate authored for tier {tier}.");
            }

            return Math.Max(0, TierGateRequirements[index]);
        }
    }
}
