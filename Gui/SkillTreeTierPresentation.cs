using System;
using System.Collections.Generic;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Gui;

internal static class SkillTreeTierPresentation
{
    // The caller supplies the live purchase gate check, including owned-node
    // exceptions. Only visible, implemented nodes may contribute a hint.
    internal static (int Tier, int RemainingLevels) NextGate(
        IEnumerable<Y4NGZSkillTreeNodeDefinition> visibleNodes,
        int investment,
        Func<Y4NGZSkillTreeNodeDefinition, bool> isGateLocked)
    {
        int tier = -1;
        int requirement = 0;
        if (visibleNodes != null && isGateLocked != null)
        {
            foreach (var node in visibleNodes)
            {
                if (node == null || node.GateRequirement <= investment || !isGateLocked(node))
                    continue;
                if (tier < 0 || node.Tier < tier)
                {
                    tier = node.Tier;
                    requirement = node.GateRequirement;
                }
                else if (node.Tier == tier)
                {
                    requirement = Math.Max(requirement, node.GateRequirement);
                }
            }
        }
        return (tier, tier < 0 ? 0 : Math.Max(0, requirement - investment));
    }

    internal static string Title(int tier) => "TIER " + tier;

    internal static string Hint(int tier, (int Tier, int RemainingLevels) nextGate)
    {
        if (tier == nextGate.Tier && nextGate.RemainingLevels > 0)
        {
            int remaining = nextGate.RemainingLevels;
            return remaining + " MORE " +
                (remaining == 1 ? "LEVEL" : "LEVELS") + " TO UNLOCK";
        }
        return string.Empty;
    }
}
