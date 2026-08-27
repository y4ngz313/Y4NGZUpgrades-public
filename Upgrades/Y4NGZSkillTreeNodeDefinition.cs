using System;
using System.Collections.Generic;
using System.Linq;

namespace Y4NGZUpgrades.Upgrades
{
    public sealed class Y4NGZSkillTreeNodeDefinition
    {
        public Y4NGZSkillTreeNodeDefinition(
            string upgradeId,
            Y4NGZSkillTreeClass treeClass,
            int tier,
            int column,
            int row,
            string iconKey,
            int gateRequirement,
            string displayName = null,
            string prerequisiteUpgradeId = null,
            IEnumerable<string> prerequisiteUpgradeIds = null,
            IEnumerable<string> connectionUpgradeIds = null)
        {
            UpgradeId = Y4NGZUpgradeDefinition.NormalizeId(upgradeId);
            TreeClass = treeClass;
            Tier = Math.Max(1, tier);
            Column = Math.Max(0, column);
            Row = Math.Max(0, row);
            IconKey = string.IsNullOrWhiteSpace(iconKey)
                ? UpgradeId
                : Y4NGZUpgradeDefinition.NormalizeId(iconKey);
            GateRequirement = Math.Max(0, gateRequirement);
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? upgradeId ?? string.Empty
                : displayName;

            List<string> prerequisites = NormalizeIds(prerequisiteUpgradeIds);
            string singlePrerequisite = Y4NGZUpgradeDefinition.NormalizeId(prerequisiteUpgradeId);
            if (!string.IsNullOrWhiteSpace(singlePrerequisite)
                && !prerequisites.Contains(singlePrerequisite, StringComparer.OrdinalIgnoreCase))
            {
                prerequisites.Insert(0, singlePrerequisite);
            }

            List<string> connections = NormalizeIds(connectionUpgradeIds);
            if (connections.Count == 0)
                connections.AddRange(prerequisites);

            PrerequisiteUpgradeIds = prerequisites;
            ConnectionUpgradeIds = connections;
            PrerequisiteUpgradeId = prerequisites.Count > 0 ? prerequisites[0] : string.Empty;
        }

        public string UpgradeId { get; }
        public Y4NGZSkillTreeClass TreeClass { get; }
        public int Tier { get; }
        public int Column { get; }
        public int Row { get; }
        public string IconKey { get; }
        public int GateRequirement { get; }
        public string DisplayName { get; }
        public string PrerequisiteUpgradeId { get; }
        public IReadOnlyList<string> PrerequisiteUpgradeIds { get; }
        public IReadOnlyList<string> ConnectionUpgradeIds { get; }

        private static List<string> NormalizeIds(IEnumerable<string> ids)
        {
            var normalized = new List<string>();
            if (ids == null)
                return normalized;

            foreach (string raw in ids)
            {
                string id = Y4NGZUpgradeDefinition.NormalizeId(raw);
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (normalized.Contains(id, StringComparer.OrdinalIgnoreCase))
                    continue;

                normalized.Add(id);
            }

            return normalized;
        }
    }
}
