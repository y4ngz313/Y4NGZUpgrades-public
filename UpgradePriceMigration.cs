using System;
using System.Collections.Generic;
using System.Globalization;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Config schema migration for the "30 - Upgrade Prices" section (#217).
    ///
    /// Every price is a persisted config key, so a rebalance in code reaches exactly zero existing
    /// profiles: the stale values parse cleanly and win. When the section's stored schema stamp is
    /// below <see cref="CurrentSchemaVersion"/>, each key whose stored value still equals one of the
    /// old authored defaults is rewritten to the new derived default. Anything a player actually
    /// changed is left alone.
    ///
    /// Unity-free and free of BepInEx types so the deterministic checks exercise the decision
    /// directly; the caller owns the ConfigEntry reads and writes.
    /// </summary>
    internal static class UpgradePriceMigration
    {
        /// <summary>
        /// 1 = hand-authored price literals (everything shipped before #217).
        /// 2 = tier-derived prices.
        /// 3 = Pumping Iron dropped to two sub-tiers (#364). Profiles stamped 2 already hold the
        ///     derived three-level list, which the narrowed schema would reject on every boot, so
        ///     the stamp has to advance for the repair pass to reach them.
        /// </summary>
        internal const int CurrentSchemaVersion = 3;

        /// <summary>
        /// Schema stamp assumed for a profile written before the stamp existed. A missing key binds
        /// to this, which is what makes an already-deployed config migrate on its next boot.
        /// </summary>
        internal const int LegacySchemaVersion = 1;

        internal sealed class LegacyPrices
        {
            internal LegacyPrices(string id, int[] unlockPrices, string[] tierLists, bool optionalGated = false)
            {
                Id = id;
                UnlockPrices = unlockPrices ?? Array.Empty<int>();
                TierLists = tierLists ?? Array.Empty<string>();
                OptionalGated = optionalGated;
            }

            internal string Id { get; }

            /// <summary>
            /// True for a row that only binds when its optional provider set is installed, so its
            /// keys are stamped separately.
            /// </summary>
            internal bool OptionalGated { get; }

            /// <summary>Every unlock price this key has ever defaulted to.</summary>
            internal int[] UnlockPrices { get; }

            /// <summary>Every tier list this key has ever defaulted to, newest first.</summary>
            internal string[] TierLists { get; }
        }

        /// <summary>
        /// The pre-#217 authored defaults, kept as migration data. quota_guard carries two tier
        /// lists: deployed profiles still hold the pre-removal three-level list "10,14", which the
        /// current one-value schema rejects with a warning on every boot. Repairing it here is the
        /// same decision as any other stale default.
        /// </summary>
        private static readonly LegacyPrices[] Legacy =
        {
            Entry("physical_conditioning", 1, "2,3,5,7"),
            Entry("thick_skin", 2, "3,5,7"),
            Entry("lethal_hands_training", 3, "5,7"),
            Entry("deathbound", 5, ""),
            Entry("adrenaline_rush", 3, "7"),
            Entry("lone_wolf", 3, "5,7"),
            Entry("light_feet", 3, "5,7"),
            Entry("shadow_step", 3, "5,7"),
            Entry("sixth_sense", 1, "2,3"),
            new LegacyPrices("extra_inventory_slot", new[] { 3 }, new[] { "5,7" }, optionalGated: true),
            // Three tier lists: the pre-#217 authored "5,7", the #217-derived three-level "3,4"
            // that stamped-2 profiles still hold, and the current one-value list they migrate to.
            new LegacyPrices("protein_powder", new[] { 3 }, new[] { "5,7", "3,4" }),
            Entry("back_muscles", 3, "5,7"),
            Entry("better_scanner", 3, "5,7"),
            Entry("quick_hands", 1, "2,3"),
            Entry("panic_slide", 2, "4,6"),
            Entry("field_operations", 3, ""),
            Entry("turret_hacker", 4, "8,12"),
            Entry("courier_drone", 7, "10,14"),
            Entry("buddy_system", 2, "4"),
            Entry("ping", 3, "5,7"),
            Entry("command_net", 4, "6,8"),
            Entry("worklight_beacon", 3, "5,7"),
            Entry("veteran", 4, "6,8"),
            new LegacyPrices("quota_guard", new[] { 7 }, new[] { "10", "10,14" }),
            new LegacyPrices("scavenger", new[] { 2 }, new[] { "4,6" }, optionalGated: true),
            new LegacyPrices("chameleon", new[] { 2 }, new[] { "3,5" }, optionalGated: true),
            Entry("inspire", 7, "")
        };

        private static readonly Dictionary<string, LegacyPrices> ById = BuildIndex();

        internal static IReadOnlyList<LegacyPrices> All => Legacy;

        internal static LegacyPrices Find(string id)
        {
            if (!string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id.Trim(), out LegacyPrices legacy))
                return legacy;

            return null;
        }

        /// <summary>
        /// The stamp a row set carries after a boot. A set only advances once its keys have
        /// actually been visited, so a boot without optional providers advances the base stamp and
        /// leaves the optional stamp behind for the boot that can migrate those keys.
        /// </summary>
        internal static int NextStamp(int storedStamp, bool rowSetResolved)
        {
            return rowSetResolved && storedStamp < CurrentSchemaVersion ? CurrentSchemaVersion : storedStamp;
        }

        /// <summary>
        /// The whole migration decision for one persisted value. Returns the value the section
        /// should carry; <paramref name="rewrite"/> is true only when the stored value has to be
        /// overwritten (an untouched old default that the rebalance moved).
        /// </summary>
        internal static string ResolveValue(
            bool stampBelowCurrent,
            string storedValue,
            IReadOnlyList<string> oldDefaults,
            string newDefault,
            out bool rewrite)
        {
            rewrite = false;
            if (!stampBelowCurrent)
                return storedValue;

            // Already at the new default (fresh profile, or a second migration pass): nothing to do.
            if (Matches(storedValue, newDefault))
                return storedValue;

            // Anything that is not a recognized old default is a deliberate customization.
            if (!MatchesAny(storedValue, oldDefaults))
                return storedValue;

            rewrite = true;
            return newDefault;
        }

        internal static int ResolveValue(
            bool stampBelowCurrent,
            int storedValue,
            IReadOnlyList<int> oldDefaults,
            int newDefault,
            out bool rewrite)
        {
            rewrite = false;
            if (!stampBelowCurrent || storedValue == newDefault)
                return storedValue;

            if (oldDefaults != null)
            {
                for (int i = 0; i < oldDefaults.Count; i++)
                {
                    if (oldDefaults[i] == storedValue)
                    {
                        rewrite = true;
                        return newDefault;
                    }
                }
            }

            return storedValue;
        }

        private static bool MatchesAny(string storedValue, IReadOnlyList<string> candidates)
        {
            if (candidates == null)
                return false;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (Matches(storedValue, candidates[i]))
                    return true;
            }

            return false;
        }

        // Whitespace around the commas is cosmetic, so "5, 7" is still the authored default.
        private static bool Matches(string left, string right)
        {
            return string.Equals(Strip(left), Strip(right), StringComparison.Ordinal);
        }

        private static string Strip(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var chars = new char[value.Length];
            int length = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsWhiteSpace(value[i]))
                    chars[length++] = value[i];
            }

            return new string(chars, 0, length);
        }

        internal static string Format(IReadOnlyList<int> values)
        {
            if (values == null || values.Count == 0)
                return string.Empty;

            var parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
                parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }

        private static LegacyPrices Entry(string id, int unlockPrice, string tierList)
        {
            return new LegacyPrices(id, new[] { unlockPrice }, new[] { tierList });
        }

        private static Dictionary<string, LegacyPrices> BuildIndex()
        {
            var index = new Dictionary<string, LegacyPrices>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Legacy.Length; i++)
                index[Legacy[i].Id] = Legacy[i];
            return index;
        }
    }
}
