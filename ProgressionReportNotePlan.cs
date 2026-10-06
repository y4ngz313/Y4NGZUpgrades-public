using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Selects, words, and composes the XP notes that Upgrades owns in the performance report
    /// (#220). Unity-free so <c>verification/ProgressionChecks</c> can pin ordering, wording,
    /// Coroner coexistence, and idempotence without loading the game.
    /// </summary>
    public static class ProgressionReportNotePlan
    {
        // #349: the legacy mint default. Kept public for callers that pass no color, but the
        // report writer now supplies a hex resolved from the shared UI theme so the decoration
        // matches the active report palette instead of always reading mint-on-red.
        public const string XpColorHex = "#6FE7A8";
        // Own-line recognition is by tag shape, not one hex: any bullet whose XP decoration is
        // wrapped in a color tag ending "XP)</color>" is ours regardless of the color it was
        // composed with. Company's plain " (45 XP)" decorations carry no tag and stay foreign.
        private const string XpTagOpen = "<color=";
        private const string XpTagClose = "XP)</color>";
        // The reconciliation remainder is ours by tag shape too, but it is not a named note.
        private const string RemainderPrefix = "> Other work " + XpTagOpen;

        /// <summary>
        /// Selects the highest-value unique XP sources and renders them as vanilla-style bullet
        /// notes. Amount descending is authoritative; first-observed order resolves equal awards.
        /// <paramref name="xpColorHex"/> tints the XP decoration; null keeps the legacy default.
        /// #1201: a crew award (<see cref="ProgressionReportLineKeys.CrewWidePrefix"/> key) is
        /// never a line.
        /// </summary>
        public static IReadOnlyList<string> BuildXpNotes(
            IReadOnlyList<ProgressionReportSource> sources,
            int roundSequence,
            int maxNotes = 3,
            string xpColorHex = null)
        {
            string marker = BuildMarker(xpColorHex);
            var result = new List<string>();
            if (sources == null || maxNotes <= 0)
                return result;

            var ranked = new List<ProgressionReportSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sources.Count; i++)
            {
                ProgressionReportSource source = sources[i];
                if (source == null || source.Amount <= 0 || ProgressionReportLineKeys.IsCrewWideKey(source.Key)
                    || !seen.Add(source.Key))
                    continue;
                ranked.Add(source);
            }

            ranked.Sort((left, right) =>
            {
                int amount = right.Amount.CompareTo(left.Amount);
                if (amount != 0)
                    return amount;
                int order = left.Order.CompareTo(right.Order);
                return order != 0
                    ? order
                    : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            for (int i = 0; i < ranked.Count && result.Count < maxNotes; i++)
            {
                ProgressionReportSource source = ranked[i];
                string sentence = BuildSentence(source, UseAlternateWording(roundSequence, source.Key));
                if (string.IsNullOrWhiteSpace(sentence))
                    continue;

                result.Add(
                    $"> {sentence} {marker}({source.Amount.ToString(CultureInfo.InvariantCulture)} XP)</color>");
            }

            return result;
        }

        /// <summary>
        /// #1201: the XP of the crew-award sources, which the report hides from its lines and its
        /// remainder while the shift total still counts it.
        /// </summary>
        public static int SumCrewWide(IReadOnlyList<ProgressionReportSource> sources)
        {
            if (sources == null)
                return 0;

            long total = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                ProgressionReportSource source = sources[i];
                if (source != null && source.Amount > 0 && ProgressionReportLineKeys.IsCrewWideKey(source.Key))
                    total += source.Amount;
            }

            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        /// <summary>
        /// Merges Upgrades notes into the vanilla slot. <paramref name="maxLines"/> is the TOTAL
        /// rendered-line budget: heading rows count against it exactly like bullets, because the
        /// slot's underline graphics only cover so many rows no matter who wrote them (#360). A
        /// Coroner-style writer that contributes several heading rows therefore costs XP notes
        /// instead of pushing text past the rect. Reapplying after Company's delayed writer is
        /// idempotent because lines carrying Upgrades' XP tag shape are stripped first, whichever
        /// color they were composed with. #1201: <paramref name="hiddenXp"/> is the crew-award XP
        /// inside <paramref name="totalXp"/>; the remainder line leaves it out. #493: the remainder
        /// only takes a line left free after the named notes and never replaces one.
        /// </summary>
        public static string ComposeNotes(
            string existingText,
            IReadOnlyList<string> xpNotes,
            int maxLines = 4,
            bool preferLastForeign = false,
            int totalXp = -1,
            string xpColorHex = null,
            int hiddenXp = 0)
        {
            if (maxLines <= 0 || ((xpNotes == null || xpNotes.Count == 0) && totalXp <= 0))
                return existingText ?? string.Empty;

            xpNotes = xpNotes ?? Array.Empty<string>();
            string normalized = (existingText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] rows = normalized.Split(new[] { '\n' }, StringSplitOptions.None);
            var header = new List<string>();
            var foreign = new List<string>();
            bool sawBullet = false;

            for (int i = 0; i < rows.Length; i++)
            {
                string row = rows[i]?.TrimEnd() ?? string.Empty;
                string trimmed = row.TrimStart();
                if ((trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("> ", StringComparison.Ordinal)))
                {
                    sawBullet = true;
                    if (!IsXpNoteLine(trimmed))
                        foreign.Add(trimmed);
                    continue;
                }

                // A slot written by an older Contracted can already carry a stacked pair of
                // "Notes:" headers (Y4NGZCompany#654); keep the first and drop the copies so
                // this pass never republishes the duplication.
                if (row.Length > 0 && (!sawBullet || trimmed.EndsWith(":", StringComparison.Ordinal)))
                {
                    if (IsNotesHeaderRow(row) && HasNotesHeader(header))
                        continue;
                    header.Add(row);
                }
            }

            if (header.Count == 0)
                header.Add("Notes:");

            if (preferLastForeign && foreign.Count > 1)
                foreign.RemoveAll(line => line.StartsWith("> Died ", StringComparison.OrdinalIgnoreCase));

            var candidates = new List<string>();
            for (int i = 0; i < xpNotes.Count; i++)
            {
                string note = xpNotes[i]?.Trim();
                if (!string.IsNullOrEmpty(note))
                    candidates.Add(note);
            }
            var mine = new List<string>(candidates);

            // Drop order over budget, least valuable first: our lowest-XP notes down to a single
            // note, then surplus non-"Notes:" heading rows, then surplus foreign bullets, and only
            // when a single line is all that fits, our last note. Headings go before bullets
            // because a "Cause of Death:" label whose bullet was stripped reads as broken, while
            // the bullet on its own still reads correctly. Surplus headings come off the front so
            // the row nearest the bullets - the one most likely to label them - survives, and the
            // "Notes:" row is never dropped because it names the whole slot.
            int overflow = header.Count + foreign.Count + mine.Count - maxLines;
            while (overflow > 0 && mine.Count > 1)
            {
                mine.RemoveAt(mine.Count - 1);
                overflow--;
            }
            while (overflow > 0 && TryDropLeadingHeading(header))
                overflow--;
            while (overflow > 0 && foreign.Count > 0)
            {
                // preferLastForeign keeps Coroner's final cause-of-death bullet, so the surplus
                // comes off the front; otherwise the earliest bullets are the ones worth keeping.
                foreign.RemoveAt(preferLastForeign ? 0 : foreign.Count - 1);
                overflow--;
            }
            while (overflow > 0 && mine.Count > 0)
            {
                mine.RemoveAt(mine.Count - 1);
                overflow--;
            }

            // Orphan guard: budgeting can still leave a heading as the final rendered row with
            // nothing beneath it. Retire it and hand the freed line to our highest-XP note.
            while (header.Count > 0
                   && foreign.Count == 0
                   && mine.Count == 0
                   && !IsNotesHeaderRow(header[header.Count - 1]))
            {
                header.RemoveAt(header.Count - 1);
                if (candidates.Count > 0)
                {
                    mine.Add(candidates[0]);
                    break;
                }
            }

            // #493: the "Other work" remainder only takes a line that is still free once the named
            // notes and foreign lines have been budgeted. It never evicts a line to make room: an
            // evicted top award replaced by the remainder read as "Notes: Other work (170 XP)"
            // although named notes covered the whole shift, and the shift total stays visible in
            // the rank/XP strip regardless. With no free line the remainder is simply omitted.
            if (totalXp >= 0 && header.Count + foreign.Count + mine.Count < maxLines)
            {
                long shown = 0;
                foreach (string line in foreign) shown += ReadXpAmount(line);
                foreach (string line in mine) shown += ReadXpAmount(line);
                long owed = (long)totalXp - Math.Max(0, hiddenXp);
                // #456: the remainder carries the same XP tag as every other Upgrades line,
                // so a delayed second pass recognises it by shape instead of by label text.
                if (shown < owed)
                    mine.Add("> Other work " + BuildMarker(xpColorHex)
                             + "(" + (owed - shown).ToString(CultureInfo.InvariantCulture) + " XP)</color>");
            }

            var output = new StringBuilder();
            for (int i = 0; i < header.Count; i++)
                output.Append(header[i]).Append('\n');
            for (int i = 0; i < foreign.Count; i++)
                output.Append(foreign[i]).Append('\n');
            for (int i = 0; i < mine.Count; i++)
                output.Append(mine[i]).Append('\n');

            return output.ToString();
        }

        /// <summary>
        /// Removes the first heading row that is not the slot's own "Notes:" label, returning
        /// false once nothing but that label remains.
        /// </summary>
        private static bool TryDropLeadingHeading(List<string> header)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (IsNotesHeaderRow(header[i]))
                    continue;
                header.RemoveAt(i);
                return true;
            }
            return false;
        }

        private static bool IsNotesHeaderRow(string row)
        {
            return (row ?? string.Empty).Trim().EndsWith("Notes:", StringComparison.OrdinalIgnoreCase);
        }

        internal static int ReadXpAmount(string line)
        {
            Match match = Regex.Match(line ?? string.Empty, @"\(\+?(\d+)\s*XP\)");
            return match.Success && int.TryParse(match.Groups[1].Value, out int amount) ? amount : 0;
        }

        /// <summary>
        /// #493: how many named Upgrades XP notes a composed slot text renders: rows carrying the
        /// XP tag shape, excluding the "Other work" remainder.
        /// </summary>
        public static int CountNamedXpNotes(string composedText)
        {
            if (string.IsNullOrEmpty(composedText))
                return 0;

            int count = 0;
            string[] rows = composedText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < rows.Length; i++)
            {
                string trimmed = rows[i].Trim();
                if ((trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("> ", StringComparison.Ordinal))
                    && IsXpNoteLine(trimmed)
                    && !trimmed.StartsWith(RemainderPrefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Ours iff the bullet carries Upgrades' XP tag shape, whatever color composed it and
        /// whatever the label says. Company's plain decorations ("> Other work (30 XP)",
        /// "> Restored the breaker box (35 XP)") carry no tag and stay foreign (#456).
        /// </summary>
        private static bool IsXpNoteLine(string line)
        {
            int open = line.IndexOf(XpTagOpen, StringComparison.Ordinal);
            return open >= 0 && line.IndexOf(XpTagClose, open, StringComparison.Ordinal) > open;
        }

        private static string BuildMarker(string xpColorHex)
        {
            return XpTagOpen
                   + (string.IsNullOrWhiteSpace(xpColorHex) ? XpColorHex : xpColorHex.Trim())
                   + ">";
        }

        private static bool HasNotesHeader(List<string> header)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (IsNotesHeaderRow(header[i]))
                    return true;
            }
            return false;
        }

        public static string DescribeSource(ProgressionReportSource source, int roundSequence)
        {
            return BuildSentence(source, UseAlternateWording(roundSequence, source.Key));
        }

        private static string BuildSentence(ProgressionReportSource source, bool alternate)
        {
            switch (source.Kind)
            {
                case ProgressionReportSourceKind.ScrapDiscovered:
                    return (alternate ? "Discovered " : "Found ") + FormatScrapHaul(source);
                case ProgressionReportSourceKind.ScrapDelivered:
                    return (alternate ? "Brought back " : "Delivered ") + FormatScrapHaul(source);
                case ProgressionReportSourceKind.MonsterKill:
                    // One line for the round's kills: the enemy name when every kill was the same
                    // type, the generic noun once the round mixed types (#456).
                    return "Killed " + FormatCountedSubject(
                        string.IsNullOrWhiteSpace(source.Subject) ? "monster" : source.Subject,
                        source.Count);
                case ProgressionReportSourceKind.Survival:
                    return string.Equals(source.Subject, "extracted", StringComparison.OrdinalIgnoreCase)
                        ? "Survived the shift"
                        : $"Lasted {FormatMinutes(source.Count)}";
                case ProgressionReportSourceKind.BodyRetrieval:
                    if (source.Count == 1 && !string.IsNullOrWhiteSpace(source.Subject))
                        return $"Recovered {EscapeRichText(source.Subject)}'s body";
                    return "Recovered "
                           + (source.Count == 1
                               ? "a crew member's body"
                               : $"{Math.Max(2, source.Count).ToString(CultureInfo.InvariantCulture)} crew bodies");
                case ProgressionReportSourceKind.MainframeHack:
                    if (source.Count > 1)
                    {
                        return (alternate ? "Breached " : "Hacked ")
                               + source.Count.ToString(CultureInfo.InvariantCulture)
                               + " mainframes";
                    }
                    return alternate ? "Breached the mainframe" : "Hacked the mainframe";
                case ProgressionReportSourceKind.Clutch:
                    return "Last one out alive";
                case ProgressionReportSourceKind.Contract:
                    return TrimSentence(source.Subject, "Contract bonus");
                // #459: one line per kind per round; " xN" once the round paid more than one.
                case ProgressionReportSourceKind.ShipBatteryReplaced:
                    return "Replaced the ship battery" + FormatRepeat(source.Count);
                case ProgressionReportSourceKind.ShipApparatusDocked:
                    return "Docked an apparatus" + FormatRepeat(source.Count);
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The round's scrap haul as one phrase. The credits are authoritative; the item count is
        /// only the fallback for a haul that somehow paid XP without carrying a value.
        /// </summary>
        private static string FormatScrapHaul(ProgressionReportSource source)
        {
            if (source.Value > 0)
                return "$" + source.Value.ToString(CultureInfo.InvariantCulture) + " of scrap";

            int count = Math.Max(1, source.Count);
            return count.ToString(CultureInfo.InvariantCulture)
                   + (count == 1 ? " piece of scrap" : " pieces of scrap");
        }

        private static string FormatCountedSubject(string subject, int count)
        {
            int resolvedCount = Math.Max(1, count);
            string noun = EscapeRichText(string.IsNullOrWhiteSpace(subject) ? "scrap" : subject.Trim());
            noun = LowerFirst(noun);
            if (resolvedCount != 1)
                noun = Pluralize(noun);
            return resolvedCount.ToString(CultureInfo.InvariantCulture) + " " + noun;
        }

        private static string FormatMinutes(int minutes)
        {
            int resolved = Math.Max(1, minutes);
            return resolved.ToString(CultureInfo.InvariantCulture)
                   + (resolved == 1 ? " minute" : " minutes");
        }

        private static string FormatRepeat(int count)
        {
            return count > 1 ? " x" + count.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        /// <summary>
        /// #456: Upgrades' report lines never carry a trailing period, so a label that arrives
        /// with sentence punctuation (a contract outcome worded elsewhere) loses it here.
        /// </summary>
        private static string TrimSentence(string value, string fallback)
        {
            string text = EscapeRichText(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim());
            return text.TrimEnd('.', '!', '?', ' ');
        }

        private static string EscapeRichText(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static string LowerFirst(string value)
        {
            if (string.IsNullOrEmpty(value) || !char.IsUpper(value[0]))
                return value;
            if (value.Length == 1)
                return char.ToLowerInvariant(value[0]).ToString();
            return char.ToLowerInvariant(value[0]) + value.Substring(1);
        }

        private static string Pluralize(string noun)
        {
            if (string.IsNullOrEmpty(noun))
                return noun;

            if (string.Equals(noun, "teeth", StringComparison.OrdinalIgnoreCase)
                || string.Equals(noun, "scissors", StringComparison.OrdinalIgnoreCase)
                || string.Equals(noun, "dice", StringComparison.OrdinalIgnoreCase))
            {
                return noun;
            }

            if (noun.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
                || noun.EndsWith("sh", StringComparison.OrdinalIgnoreCase)
                || noun.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                || noun.EndsWith("x", StringComparison.OrdinalIgnoreCase)
                || noun.EndsWith("z", StringComparison.OrdinalIgnoreCase))
            {
                return noun + "es";
            }

            if (noun.Length > 1
                && noun.EndsWith("y", StringComparison.OrdinalIgnoreCase)
                && "aeiou".IndexOf(char.ToLowerInvariant(noun[noun.Length - 2])) < 0)
            {
                return noun.Substring(0, noun.Length - 1) + "ies";
            }

            return noun + "s";
        }

        private static bool UseAlternateWording(int roundSequence, string key)
        {
            unchecked
            {
                uint hash = 2166136261;
                string value = roundSequence.ToString(CultureInfo.InvariantCulture) + ":" + (key ?? string.Empty);
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return (hash & 1) != 0;
            }
        }
    }
}
