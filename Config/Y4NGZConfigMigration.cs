using System;
using System.Collections.Generic;
using System.IO;

namespace Y4NGZUpgrades.Config
{
    internal enum Y4NGZConfigMigrationSource
    {
        None = 0,
        PreviousUnified = 1,
        SplitFile = 2,
        LegacyMonolith = 3
    }

    /// <summary>Which copy of a moved or renamed key supplied its value (#435, #442).</summary>
    internal enum Y4NGZGroupMoveSource
    {
        None = 0,
        LiveGroup = 1,
        RenamedGroup = 2,
        PreviousGroup = 3
    }

    /// <summary>
    /// How one live key resolves against its older copies: which copy supplied the value, and
    /// which older copies exist. A binding adopts and retires every older copy whether or not it
    /// supplied the value, so no stale copy reappears on save.
    /// </summary>
    internal readonly struct Y4NGZGroupMove
    {
        internal Y4NGZGroupMove(Y4NGZGroupMoveSource source, bool retiresRenamedCopy, bool retiresPreviousCopy)
        {
            Source = source;
            RetiresRenamedCopy = retiresRenamedCopy;
            RetiresPreviousCopy = retiresPreviousCopy;
        }

        internal Y4NGZGroupMoveSource Source { get; }
        internal bool RetiresRenamedCopy { get; }
        internal bool RetiresPreviousCopy { get; }

        /// <summary>An older copy supplied the value, so the live entry takes it.</summary>
        internal bool Carries =>
            Source == Y4NGZGroupMoveSource.RenamedGroup || Source == Y4NGZGroupMoveSource.PreviousGroup;
    }

    /// <summary>
    /// Unity-free precedence for the grouped Gale layout. A value already using the grouped
    /// schema wins implicitly; otherwise the fine-grained unified schema wins over a split file,
    /// which wins over the original com.y4ngz.upgrades.cfg monolith. A key whose group was
    /// renamed (#435) or that moved to another group (#442) is read from those older copies
    /// before any of the fine-grained sources.
    /// </summary>
    internal static class Y4NGZConfigMigration
    {
        internal static bool TryGetRenamedGroupValue<T>(LegacyConfigDocument document,
            string group, string currentKey, string previousKey, out T value)
        {
            value = default;
            return document != null && (document.TryGet(group, currentKey, out value)
                || document.TryGet(group, previousKey, out value));
        }

        /// <summary>
        /// Where a key in a renamed or moved group takes its value, newest copy first. The live
        /// group wins. Next is the group's former name (#435 renamed [General] to [LGU]), carried
        /// verbatim: the earlier move already settled it, so even a value equal to an old tier
        /// default is the player's. Last is the group the key lived in before it moved (#442),
        /// under the old-tier-default rule of <see cref="TryGetMovedGroupValue{T}"/>. A null or
        /// unchanged group is not an older copy. <see cref="Y4NGZGroupMoveSource.None"/> leaves
        /// the key to the fine-grained, split and monolith fallbacks.
        /// </summary>
        internal static Y4NGZGroupMove ResolveGroupMove<T>(
            LegacyConfigDocument document,
            string liveGroup,
            string liveKey,
            string renamedFromGroup,
            string previousGroup,
            string previousKey,
            bool replacePreviousDefault,
            T previousDefault,
            T currentDefault,
            out T value)
        {
            value = default;
            if (document == null)
                return default;

            bool renamedCopy = IsOlderGroup(renamedFromGroup, liveGroup)
                               && document.Contains(renamedFromGroup, liveKey);
            bool previousCopy = IsOlderGroup(previousGroup, liveGroup)
                                && document.Contains(previousGroup, previousKey);
            Y4NGZGroupMoveSource source;
            if (document.Contains(liveGroup, liveKey))
            {
                source = Y4NGZGroupMoveSource.LiveGroup;
                if (!document.TryGet(liveGroup, liveKey, out value))
                    value = currentDefault;
            }
            else if (renamedCopy && document.TryGet(renamedFromGroup, liveKey, out value))
            {
                source = Y4NGZGroupMoveSource.RenamedGroup;
            }
            else if (previousCopy && TryGetMovedGroupValue(
                         document,
                         previousGroup,
                         liveGroup,
                         previousKey,
                         replacePreviousDefault,
                         previousDefault,
                         currentDefault,
                         out value))
            {
                source = Y4NGZGroupMoveSource.PreviousGroup;
            }
            else
            {
                value = default;
                source = Y4NGZGroupMoveSource.None;
            }

            return new Y4NGZGroupMove(source, renamedCopy, previousCopy);
        }

        private static bool IsOlderGroup(string group, string liveGroup) =>
            !string.IsNullOrEmpty(group)
            && !string.Equals(group, liveGroup, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The value a key held in the group it lived in before it moved (#442). A stored value
        /// equal to that key's previous default takes the current default instead, so a price
        /// that only ever followed the old tier follows the new one; a customised value carries
        /// over unchanged. Pass <paramref name="replacePreviousDefault"/> false once the one-time
        /// marker exists, so a value the player later sets on purpose is never rewritten.
        /// </summary>
        internal static bool TryGetMovedGroupValue<T>(
            LegacyConfigDocument document,
            string previousGroup,
            string currentGroup,
            string key,
            bool replacePreviousDefault,
            T previousDefault,
            T currentDefault,
            out T value)
        {
            value = default;
            if (document == null || string.IsNullOrEmpty(previousGroup)
                || string.Equals(previousGroup, currentGroup, StringComparison.OrdinalIgnoreCase)
                || !document.TryGet(previousGroup, key, out T stored))
            {
                return false;
            }

            value = replacePreviousDefault && EqualityComparer<T>.Default.Equals(stored, previousDefault)
                ? currentDefault
                : stored;
            return true;
        }

        /// <summary>
        /// Where each imported Late Game Upgrades row lived before #442 moved them all out of the
        /// class groups: its class group and the tier its default prices were derived from.
        /// Authored, never derived from the row's live class or tier, because #442 also moved
        /// Fedora Suit and Beekeeper out of Foreman and lowered four tiers.
        /// </summary>
        private static readonly Dictionary<string, KeyValuePair<string, int>> LguPreviousPlacements =
            new Dictionary<string, KeyValuePair<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["lgu_back_muscles"] = Placement("Enforcer", 1),
                ["lgu_stimpack"] = Placement("Enforcer", 1),
                ["lgu_protein_powder"] = Placement("Enforcer", 2),
                ["lgu_deeper_pockets"] = Placement("Enforcer", 2),
                ["lgu_explosion_resistance"] = Placement("Enforcer", 2),
                ["lgu_bullet_resistance"] = Placement("Enforcer", 3),
                ["lgu_hollow_point"] = Placement("Enforcer", 3),
                ["lgu_long_barrel"] = Placement("Enforcer", 3),
                ["lgu_sleight_of_hand"] = Placement("Enforcer", 4),
                ["lgu_silver_bullets"] = Placement("Enforcer", 4),
                ["lgu_running_shoes"] = Placement("Ghost", 1),
                ["lgu_strong_legs"] = Placement("Ghost", 1),
                ["lgu_bigger_lungs"] = Placement("Ghost", 2),
                ["lgu_carbon_kneejoints"] = Placement("Ghost", 2),
                ["lgu_hiking_boots"] = Placement("Ghost", 2),
                ["lgu_reinforced_boots"] = Placement("Ghost", 3),
                ["lgu_rubber_boots"] = Placement("Ghost", 3),
                ["lgu_traction_boots"] = Placement("Ghost", 3),
                ["lgu_climbing_gloves"] = Placement("Ghost", 3),
                ["lgu_oxygen_canisters"] = Placement("Ghost", 4),
                ["lgu_clay_glasses"] = Placement("Ghost", 4),
                ["lgu_better_scanner"] = Placement("Technician", 1),
                ["lgu_quick_hands"] = Placement("Technician", 1),
                ["lgu_mechanical_arms"] = Placement("Technician", 2),
                ["lgu_lithium_batteries"] = Placement("Technician", 2),
                ["lgu_nv_headset_batteries"] = Placement("Technician", 3),
                ["lgu_locksmith"] = Placement("Technician", 3),
                ["lgu_aluminium_coils"] = Placement("Technician", 3),
                ["lgu_jet_fuel"] = Placement("Technician", 4),
                ["lgu_jetpack_thrusters"] = Placement("Technician", 4),
                ["lgu_beekeeper"] = Placement("Foreman", 1),
                ["lgu_medical_nanobots"] = Placement("Foreman", 2),
                ["lgu_effective_bandaids"] = Placement("Foreman", 2),
                ["lgu_walkie_gps"] = Placement("Foreman", 3),
                ["lgu_sick_beats"] = Placement("Foreman", 3),
                ["lgu_tzp_buffer"] = Placement("Foreman", 3),
                ["lgu_weed_genetic_manipulation"] = Placement("Foreman", 3),
                ["lgu_fedora_suit"] = Placement("Foreman", 4)
            };

        private static KeyValuePair<string, int> Placement(string group, int tier) =>
            new KeyValuePair<string, int>(group, tier);

        internal static bool TryGetLguPreviousPlacement(string upgradeId, out string group, out int tier)
        {
            group = null;
            tier = 0;
            if (string.IsNullOrEmpty(upgradeId)
                || !LguPreviousPlacements.TryGetValue(upgradeId, out KeyValuePair<string, int> placement))
            {
                return false;
            }

            group = placement.Key;
            tier = placement.Value;
            return true;
        }

        /// <summary>
        /// The tier-derived default a price key had at <paramref name="tier"/>: unlock costs the
        /// tier, and each later level one token more than the one before it.
        /// </summary>
        internal static bool TryGetTierDefaultPrice(int tier, string subsection, string key, out int price)
        {
            price = 0;
            if (tier < 1 || !string.Equals(subsection, "Prices", StringComparison.Ordinal))
                return false;
            if (string.Equals(key, "Unlock Cost", StringComparison.Ordinal))
            {
                price = tier;
                return true;
            }

            const string prefix = "Level ", suffix = " Cost";
            if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)
                || !key.EndsWith(suffix, StringComparison.Ordinal)
                || !int.TryParse(key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length),
                    out int level)
                || level < 2)
            {
                return false;
            }

            price = tier + level - 1;
            return true;
        }

        /// <summary>
        /// The one-time marker for the old-tier-default price rule, kept beside the config as a
        /// non-.cfg sidecar so Gale never lists it. Written once the moved rows were bound and
        /// saved; from then on a moved value always carries over verbatim.
        /// </summary>
        internal const string MovedDefaultsMarkerFileName = "lgu-general-group.migrated";

        internal static bool HasMovedDefaultsMarker(string supportDirectory) =>
            !string.IsNullOrEmpty(supportDirectory)
            && File.Exists(Path.Combine(supportDirectory, MovedDefaultsMarkerFileName));

        internal static void WriteMovedDefaultsMarker(string supportDirectory)
        {
            Directory.CreateDirectory(supportDirectory);
            File.WriteAllText(
                Path.Combine(supportDirectory, MovedDefaultsMarkerFileName),
                "Late Game Upgrades rows moved into the General group (#442); old tier defaults were replaced once.\n");
        }

        /// <summary>
        /// The XP Sources default-migration stamp (#455), a versioned sidecar beside the config
        /// so Gale never lists it. An unreadable or absent marker reads as the legacy stamp, so
        /// the worst a damaged file costs is one more pass.
        /// </summary>
        internal static int ReadXpSourceDefaultsStamp(string supportDirectory)
        {
            if (string.IsNullOrEmpty(supportDirectory))
                return XpSourceDefaultsMigration.LegacySchemaVersion;

            try
            {
                string path = Path.Combine(supportDirectory, XpSourceDefaultsMigration.MarkerFileName);
                return File.Exists(path)
                    ? XpSourceDefaultsMigration.ParseStamp(File.ReadAllText(path))
                    : XpSourceDefaultsMigration.LegacySchemaVersion;
            }
            catch (Exception)
            {
                return XpSourceDefaultsMigration.LegacySchemaVersion;
            }
        }

        internal static void WriteXpSourceDefaultsStamp(string supportDirectory, int version)
        {
            Directory.CreateDirectory(supportDirectory);
            File.WriteAllText(
                Path.Combine(supportDirectory, XpSourceDefaultsMigration.MarkerFileName),
                XpSourceDefaultsMigration.FormatStamp(version));
        }

        /// <summary>
        /// The one-time marker for the LGU Upgrade Layout default migration (#493), a non-.cfg
        /// sidecar beside the config so Gale never lists it. Written after the save that holds the
        /// migrated layout; from then on the stored layout is always the player's own.
        /// </summary>
        internal const string LguLayoutDefaultMarkerFileName = "lgu-layout-default.migrated";

        internal static bool HasLguLayoutDefaultMarker(string supportDirectory) =>
            !string.IsNullOrEmpty(supportDirectory)
            && File.Exists(Path.Combine(supportDirectory, LguLayoutDefaultMarkerFileName));

        internal static void WriteLguLayoutDefaultMarker(string supportDirectory)
        {
            Directory.CreateDirectory(supportDirectory);
            File.WriteAllText(
                Path.Combine(supportDirectory, LguLayoutDefaultMarkerFileName),
                "LGU Upgrade Layout moved from the old ClassTrees default to SeparateCatalog once (#493).\n");
        }

        internal static bool HasUserValue(
            LegacyConfigDocument unified,
            string targetSection,
            string targetKey,
            string previousUnifiedSection,
            string previousUnifiedKey,
            LegacyConfigDocument split,
            string splitSection,
            string splitKey)
        {
            return (unified != null && unified.Contains(targetSection, targetKey))
                   || (unified != null
                       && unified.Contains(previousUnifiedSection, previousUnifiedKey))
                   || (split != null && split.Contains(splitSection, splitKey));
        }

        internal static bool TryGetFallback<T>(
            LegacyConfigDocument previousUnified,
            string previousUnifiedSection,
            string previousUnifiedKey,
            LegacyConfigDocument split,
            string splitSection,
            string splitKey,
            LegacyConfigDocument legacyMonolith,
            LegacyConfigKey[] legacyKeys,
            out T value,
            out Y4NGZConfigMigrationSource source)
        {
            if (previousUnified != null
                && previousUnified.TryGet(previousUnifiedSection, previousUnifiedKey, out value))
            {
                source = Y4NGZConfigMigrationSource.PreviousUnified;
                return true;
            }

            if (split != null && split.TryGet(splitSection, splitKey, out value))
            {
                source = Y4NGZConfigMigrationSource.SplitFile;
                return true;
            }

            if (legacyMonolith != null && legacyKeys != null)
            {
                for (int i = 0; i < legacyKeys.Length; i++)
                {
                    LegacyConfigKey legacyKey = legacyKeys[i];
                    if (!legacyMonolith.TryGet(legacyKey.Section, legacyKey.Key, out value))
                        continue;

                    source = Y4NGZConfigMigrationSource.LegacyMonolith;
                    return true;
                }
            }

            value = default;
            source = Y4NGZConfigMigrationSource.None;
            return false;
        }
    }
}
