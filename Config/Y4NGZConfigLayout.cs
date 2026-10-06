using System;
using System.Collections.Generic;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Config
{
    /// <summary>
    /// Human-facing names for the single Gale-visible Y4NGZUpgrades configuration file. Gale
    /// renders BepInEx sections as expandable rows, so the live layout deliberately exposes only
    /// nine broad groups. Earlier fine-grained section and split-file names remain migration-only.
    /// </summary>
    internal static class Y4NGZConfigLayout
    {
        internal const string UnifiedFileName = "Y4NGZUpgrades.cfg";
        internal const string SupportDirectoryName = "Y4NGZUpgrades";

        internal const string PlayerMenuScopeName = "Player Menu";
        internal const string ProgressionScopeName = "Progression";
        internal const string XpSourcesScopeName = "XP Sources";
        internal const string Lucky8ScopeName = "LUCKY-8";

        /// <summary>
        /// Every imported Late Game Upgrades row plus the three LGU switches (mode, layout and
        /// player-menu integration). Their keys keep their names, so a row reads
        /// <c>[LGU] LGU Beekeeper - General - Enabled</c>, and a later class or tier move of an
        /// imported row needs no config migration.
        /// </summary>
        internal const string LguScopeName = "LGU";

        /// <summary>
        /// Migration only: the name #442 shipped the LGU group under. #435 renamed the group with
        /// every key name unchanged; nothing binds here.
        /// </summary>
        internal const string LegacyGeneralGroupName = "General";

        internal const string LegacyPlayerMenuFileName = "Player Menu.cfg";
        internal const string LegacyProgressionFileName = "Progression.cfg";
        internal const string LegacyXpSourcesFileName = "XP Sources.cfg";
        internal const string LegacyLucky8FileName = "LUCKY-8.cfg";

        internal static IReadOnlyList<string> AllGroupNames()
        {
            return new[]
            {
                "Enforcer",
                "Foreman",
                "Ghost",
                LguScopeName,
                Lucky8ScopeName,
                PlayerMenuScopeName,
                ProgressionScopeName,
                "Technician",
                XpSourcesScopeName
            };
        }

        /// <summary>
        /// The sections compaction keeps: the live groups plus <see cref="LegacyGeneralGroupName"/>.
        /// A binding adopts and retires its own [General] copy; the rest stays, because an imported
        /// row is not bound while Late Game Upgrades is absent or not integrated, and a key the
        /// player added is theirs.
        /// </summary>
        internal static IReadOnlyList<string> CompactionKeptGroupNames()
        {
            var names = new List<string>(AllGroupNames());
            names.Add(LegacyGeneralGroupName);
            return names;
        }

        /// <summary>
        /// The name a live group shipped under before it was renamed, or null. Only LGU was.
        /// </summary>
        internal static string RenamedFromGroupName(string groupName)
        {
            return string.Equals(groupName, LguScopeName, StringComparison.OrdinalIgnoreCase)
                ? LegacyGeneralGroupName
                : null;
        }

        internal static string GroupedKey(string prefix, string subsection, string key)
        {
            var parts = new List<string>(3);
            AddPart(parts, prefix);
            AddPart(parts, subsection);
            AddPart(parts, key);
            return string.Join(" - ", parts);
        }

        /// <summary>
        /// Produces the fine-grained section name written by the immediately previous unified
        /// layout. It is not used for new bindings.
        /// </summary>
        internal static string PreviousUnifiedSection(string scopeName, string subsection)
        {
            return GroupedKey(scopeName, subsection, string.Empty);
        }

        internal static string UpgradeScopeName(Y4NGZSkillTreeClass treeClass, string displayName)
        {
            return GroupedKey(treeClass.ToString(), displayName, string.Empty);
        }

        /// <summary>The group an upgrade row's settings live in: its class, or LGU for imported rows.</summary>
        internal static string UpgradeGroupName(UpgradeCatalogTable.Entry entry)
        {
            return entry.RequiredProviders == OptionalUpgradeProvider.LateGameUpgrades
                ? LguScopeName
                : entry.TreeClass.ToString();
        }

        internal static IReadOnlyList<string> AllUpgradeScopeNames()
        {
            var names = new List<string>(UpgradeCatalogTable.All.Count);
            for (int i = 0; i < UpgradeCatalogTable.All.Count; i++)
            {
                UpgradeCatalogTable.Entry entry = UpgradeCatalogTable.All[i];
                names.Add(UpgradeScopeName(entry.TreeClass, entry.ConfigName));
            }

            return names;
        }

        internal static string LegacyUpgradeFileName(
            Y4NGZSkillTreeClass treeClass,
            string displayName)
        {
            return UpgradeScopeName(treeClass, displayName) + ".cfg";
        }

        internal static IReadOnlyList<string> AllLegacySplitFileNames()
        {
            var names = new List<string>
            {
                LegacyPlayerMenuFileName,
                LegacyProgressionFileName,
                LegacyXpSourcesFileName,
                LegacyLucky8FileName
            };
            for (int i = 0; i < UpgradeCatalogTable.All.Count; i++)
            {
                UpgradeCatalogTable.Entry entry = UpgradeCatalogTable.All[i];
                names.Add(LegacyUpgradeFileName(entry.TreeClass, entry.ConfigName));
            }

            return names;
        }

        private static void AddPart(List<string> parts, string value)
        {
            string sanitized = SanitizePart(value);
            if (sanitized.Length > 0)
                parts.Add(sanitized);
        }

        private static string SanitizePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Trim()
                .Replace('[', '(')
                .Replace(']', ')')
                .Replace('=', '-')
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }
    }
}
