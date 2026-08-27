using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
        // composed with. Company's plain " (+45 XP)" decorations carry no tag and stay foreign.
        private const string XpTagOpen = "<color=";
        private const string XpTagClose = "XP)</color>";

        /// <summary>
        /// Selects the highest-value unique XP sources and renders them as vanilla-style bullet
        /// notes. Amount descending is authoritative; first-observed order resolves equal awards.
        /// <paramref name="xpColorHex"/> tints the XP decoration; null keeps the legacy default.
        /// </summary>
        public static IReadOnlyList<string> BuildXpNotes(
            IReadOnlyList<ProgressionReportSource> sources,
            int roundSequence,
            int maxNotes = 3,
            string xpColorHex = null)
        {
            string marker = XpTagOpen
                + (string.IsNullOrWhiteSpace(xpColorHex) ? XpColorHex : xpColorHex.Trim())
                + ">";
            var result = new List<string>();
            if (sources == null || maxNotes <= 0)
                return result;

            var ranked = new List<ProgressionReportSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sources.Count; i++)
            {
                ProgressionReportSource source = sources[i];
                if (source == null || source.Amount <= 0 || !seen.Add(source.Key))
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
                    $"* {sentence} {marker}({source.Amount.ToString(CultureInfo.InvariantCulture)}XP)</color>");
            }

            return result;
        }

        /// <summary>
        /// Merges Upgrades notes into the existing three-line vanilla slot. If Coroner, vanilla,
        /// or another compatible mod already wrote a note, one foreign line stays and the two
        /// highest XP lines fill the remaining space. Reapplying after Company's delayed writer
        /// is idempotent because lines carrying Upgrades' XP tag shape are stripped first,
        /// whichever color they were composed with.
        /// </summary>
        public static string ComposeNotes(
            string existingText,
            IReadOnlyList<string> xpNotes,
            int maxLines = 3,
            bool preferLastForeign = false)
        {
            if (xpNotes == null || xpNotes.Count == 0 || maxLines <= 0)
                return existingText ?? string.Empty;

            string normalized = (existingText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] rows = normalized.Split(new[] { '\n' }, StringSplitOptions.None);
            var header = new List<string>();
            var foreign = new List<string>();
            bool sawBullet = false;

            for (int i = 0; i < rows.Length; i++)
            {
                string row = rows[i]?.TrimEnd() ?? string.Empty;
                string trimmed = row.TrimStart();
                if (trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    sawBullet = true;
                    if (!IsXpNoteLine(trimmed))
                        foreign.Add(trimmed);
                    continue;
                }

                // A slot written by an older Contracted can already carry a stacked pair of
                // "Notes:" headers (Y4NGZCompany#654); keep the first and drop the copies so
                // this pass never republishes the duplication.
                if (!sawBullet && row.Length > 0)
                {
                    bool isHeaderRow = trimmed.TrimEnd().EndsWith("Notes:", StringComparison.OrdinalIgnoreCase);
                    if (isHeaderRow && HasNotesHeader(header))
                        continue;
                    header.Add(row);
                }
            }

            if (header.Count == 0)
                header.Add("Notes:");

            int xpBudget = foreign.Count > 0 && maxLines > 1
                ? Math.Min(xpNotes.Count, maxLines - 1)
                : Math.Min(xpNotes.Count, maxLines);
            int foreignBudget = Math.Min(foreign.Count, maxLines - xpBudget);

            var output = new StringBuilder();
            for (int i = 0; i < header.Count; i++)
            {
                if (i > 0)
                    output.Append('\n');
                output.Append(header[i]);
            }
            output.Append('\n');

            int foreignStart = preferLastForeign
                ? Math.Max(0, foreign.Count - foreignBudget)
                : 0;
            for (int i = 0; i < foreignBudget; i++)
                output.Append(foreign[foreignStart + i]).Append('\n');
            for (int i = 0; i < xpBudget; i++)
            {
                string note = xpNotes[i]?.Trim();
                if (!string.IsNullOrEmpty(note))
                    output.Append(note).Append('\n');
            }

            return output.ToString();
        }

        private static bool IsXpNoteLine(string line)
        {
            int open = line.IndexOf(XpTagOpen, StringComparison.Ordinal);
            return open >= 0
                && line.IndexOf(XpTagClose, open, StringComparison.Ordinal) > open;
        }

        private static bool HasNotesHeader(List<string> header)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (header[i].TrimEnd().EndsWith("Notes:", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string BuildSentence(ProgressionReportSource source, bool alternate)
        {
            switch (source.Kind)
            {
                case ProgressionReportSourceKind.ScrapDiscovered:
                    return (alternate ? "Discovered " : "Found ")
                           + FormatCountedSubject(source.Subject, source.Count) + ".";
                case ProgressionReportSourceKind.ScrapDelivered:
                    return (alternate ? "Brought back " : "Delivered ")
                           + FormatCountedSubject(source.Subject, source.Count) + ".";
                case ProgressionReportSourceKind.MonsterKill:
                    return (alternate ? "Took down " : "Killed ")
                           + FormatCountedSubject(source.Subject, source.Count) + ".";
                case ProgressionReportSourceKind.Survival:
                    if (string.Equals(source.Subject, "extracted", StringComparison.OrdinalIgnoreCase))
                        return alternate ? "Made it back alive." : "Survived the shift.";
                    return alternate
                        ? $"Lasted {FormatMinutes(source.Count)}."
                        : $"Stayed alive for {FormatMinutes(source.Count)}.";
                case ProgressionReportSourceKind.BodyRetrieval:
                    if (source.Count == 1 && !string.IsNullOrWhiteSpace(source.Subject))
                    {
                        string owner = EscapeRichText(source.Subject);
                        return alternate
                            ? $"Brought back {owner}'s body."
                            : $"Recovered {owner}'s body.";
                    }
                    return (alternate ? "Brought back " : "Recovered ")
                           + (source.Count == 1
                               ? "a crew member's body."
                               : $"{Math.Max(2, source.Count).ToString(CultureInfo.InvariantCulture)} crew bodies.");
                case ProgressionReportSourceKind.MainframeHack:
                    if (source.Count > 1)
                    {
                        return (alternate ? "Breached " : "Hacked ")
                               + source.Count.ToString(CultureInfo.InvariantCulture)
                               + " mainframes.";
                    }
                    return alternate ? "Breached the mainframe." : "Hacked the mainframe.";
                case ProgressionReportSourceKind.Clutch:
                    return alternate ? "Came back as the last survivor." : "Made the clutch escape.";
                case ProgressionReportSourceKind.Contract:
                    return EnsureSentence(source.Subject, "Completed contract work.");
                default:
                    return string.Empty;
            }
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

        private static string EnsureSentence(string value, string fallback)
        {
            string text = EscapeRichText(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim());
            char last = text[text.Length - 1];
            return last == '.' || last == '!' || last == '?' ? text : text + ".";
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
