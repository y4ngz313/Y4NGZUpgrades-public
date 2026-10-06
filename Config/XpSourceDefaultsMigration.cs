using System;
using System.Collections.Generic;
using System.Globalization;

namespace Y4NGZUpgrades.Config
{
    /// <summary>
    /// One-time repair of XP Sources values that still hold a default the code has since moved
    /// (#455). Every XP source is a persisted config key, so changing a default in code reaches
    /// exactly zero existing profiles: the stale value parses cleanly and wins. The most visible
    /// case is the Blackout Audit completion multiplier, which #392 dropped to 0 because the
    /// restore is already paid per breaker - profiles written before it still pay every crew
    /// member a completion line.
    ///
    /// The decision per key: the profile's stamp is at or above the row's schema version -> keep;
    /// the stored value already equals the current default -> keep; the stored value equals a
    /// known old default -> take the current default; anything else is a player's own choice and
    /// is kept. A key that is absent binds to the current default, so it needs no special case.
    ///
    /// Adding a row (this is all the Option A rebalance in #457 has to do):
    ///   1. bump <see cref="CurrentSchemaVersion"/> by one;
    ///   2. append a <see cref="Row"/> with the section, the key, the new default, every old
    ///      default that key has ever shipped with, and the bumped version as its schema version.
    /// No other plumbing: <see cref="Y4NGZConfigFiles"/> walks this table, and a row only applies
    /// to profiles stamped below its own version, so rows added later never re-judge a key an
    /// earlier pass already settled.
    ///
    /// Unity-free and free of BepInEx types so the deterministic checks exercise the decision
    /// directly; the caller owns the ConfigEntry reads, the save and the marker.
    /// </summary>
    internal static class XpSourceDefaultsMigration
    {
        /// <summary>
        /// 1 = the Blackout Audit completion multiplier moves from its pre-#392 default of 1 to 0.
        /// 2 = the #457 Option A rebalance: the completion fraction halves to 0.10, and the two
        ///     4 XP defusal wire awards become 8.
        /// </summary>
        internal const int CurrentSchemaVersion = 2;

        /// <summary>
        /// The stamp a profile carries before the marker exists - every deployed config today.
        /// A missing or unreadable marker reads as this, which is what makes the pass run once on
        /// the next boot.
        /// </summary>
        internal const int LegacySchemaVersion = 0;

        /// <summary>The marker's file name, kept beside the config so Gale never lists it.</summary>
        internal const string MarkerFileName = "xp-source-defaults.migrated";

        /// <summary>
        /// Config floats round-trip through TOML text, so compare with the tolerance the written
        /// decimals actually carry rather than with ==.
        /// </summary>
        private const double Tolerance = 0.0001d;

        internal sealed class Row
        {
            internal Row(string section, string key, double currentDefault, double[] oldDefaults, int schemaVersion)
            {
                Section = section;
                Key = key;
                CurrentDefault = currentDefault;
                OldDefaults = oldDefaults ?? Array.Empty<double>();
                SchemaVersion = schemaVersion;
            }

            /// <summary>The XP Sources subsection, as written in the grouped key.</summary>
            internal string Section { get; }

            internal string Key { get; }

            /// <summary>The default the code binds today.</summary>
            internal double CurrentDefault { get; }

            /// <summary>Every default this key has shipped with before the current one.</summary>
            internal IReadOnlyList<double> OldDefaults { get; }

            /// <summary>The stamp that settles this row; a profile below it still has to be judged.</summary>
            internal int SchemaVersion { get; }
        }

        /// <summary>
        /// The audited default changes. Every XP Sources key since the split (#323) and in the
        /// monolith before it has held its authored default from the day it was bound, with one
        /// exception: the Blackout Audit completion multiplier, 1 since #199/#229 and 0 since
        /// #392. The completion keys were flat XP before #229, but under different key names
        /// (ContractCompletedXp.*), so no deployed value of those ever lands on these keys.
        ///
        /// Schema 2 is the #457 rebalance. Only the keys whose DEFAULT moved are listed: the act
        /// awards that gained rank scaling kept their flat numbers, which are now floors, so a
        /// stored value of theirs is still correct.
        /// </summary>
        private static readonly Row[] Rows =
        {
            new Row("Contract Completion Multipliers", "BlackoutAudit", 0d, new[] { 1d }, 1),
            new Row("Contract Completion", "Rank Width Fraction", 0.10d, new[] { 0.20d }, 2),
            new Row("Defuse Correct Wire", "XP Per Wire", 8d, new[] { 4d }, 2),
            new Row("LGU Defusal Wire", "XP", 8d, new[] { 4d }, 2)
        };

        internal static IReadOnlyList<Row> All => Rows;

        internal static bool IsStale(int storedStamp) => storedStamp < CurrentSchemaVersion;

        /// <summary>The stamp a profile carries once the pass has run.</summary>
        internal static int NextStamp(int storedStamp) =>
            storedStamp < CurrentSchemaVersion ? CurrentSchemaVersion : storedStamp;

        /// <summary>
        /// The whole decision for one stored value. True means the caller has to write
        /// <paramref name="migrated"/> back; false means the stored value stands.
        /// </summary>
        internal static bool TryMigrate(Row row, int storedStamp, double storedValue, out double migrated)
        {
            migrated = storedValue;
            if (row == null || storedStamp >= row.SchemaVersion)
                return false;

            // Already current (fresh profile, absent key, or a second pass): nothing to do.
            if (Matches(storedValue, row.CurrentDefault))
                return false;

            for (int i = 0; i < row.OldDefaults.Count; i++)
            {
                if (!Matches(storedValue, row.OldDefaults[i]))
                    continue;

                migrated = row.CurrentDefault;
                return true;
            }

            // Anything else is a deliberate customization.
            return false;
        }

        /// <summary>
        /// The same decision straight off a config file's raw text. An unparsable value is left
        /// exactly as it is: BepInEx already falls back to the default for it, and rewriting a
        /// value nobody can read is not a migration.
        /// </summary>
        internal static bool TryMigrateRaw(Row row, int storedStamp, string storedText, out double migrated)
        {
            migrated = 0d;
            return TryParseValue(storedText, out double storedValue)
                   && TryMigrate(row, storedStamp, storedValue, out migrated);
        }

        internal static bool TryParseValue(string text, out double value)
        {
            value = 0d;
            return !string.IsNullOrWhiteSpace(text)
                   && double.TryParse(
                       text.Trim(),
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out value);
        }

        /// <summary>
        /// The stamp held by a marker file's contents. Anything unreadable reads as the legacy
        /// stamp, so a damaged marker costs one extra pass instead of throwing.
        /// </summary>
        internal static int ParseStamp(string markerText)
        {
            if (string.IsNullOrWhiteSpace(markerText))
                return LegacySchemaVersion;

            string firstLine = markerText.Split('\n')[0].Trim();
            return int.TryParse(firstLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stamp)
                   && stamp >= LegacySchemaVersion
                ? stamp
                : LegacySchemaVersion;
        }

        internal static string FormatStamp(int version)
        {
            return version.ToString(CultureInfo.InvariantCulture)
                   + "\nXP Sources defaults migrated through this schema version (#455).\n";
        }

        private static bool Matches(double left, double right)
        {
            return Math.Abs(left - right) <= Tolerance;
        }
    }
}
