using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Y4NGZUpgrades.Gui;

/// <summary>
/// The catalog row IS the copy (#366): this turns one authored
/// <c>"Description: ...\nLevel N: ..."</c> block into the file summary and the per-rank effect
/// lines the inspector draws. Unity-free so the menu checks exercise the parser the menu runs.
///
/// Authored levels are deliberately sparse (#435). An imported row may author only
/// <c>Level 4:</c>, and a unique-only native variant authors fewer levels than the full row, so
/// the result is keyed by rank rather than dense: a rank with no authored line simply has no
/// entry, and the menu draws that rank with its price and no effect line. Nothing here invents a
/// line for an unauthored rank, and nothing produces a rank above the definition's own cap.
/// </summary>
internal static class UpgradeLevelCopy
{
    /// <summary>
    /// Parses <paramref name="description"/> into the authored per-rank effects plus the leftover
    /// prose. Ranks outside 1..<paramref name="maxTier"/> are discarded rather than clamped, so a
    /// stale <c>Level 5:</c> line on a four-rank row cannot manufacture a fifth row.
    /// </summary>
    internal static Dictionary<int, string> ExtractLevelEffects(
        string description, string upgradeName, int maxTier, out string loreText)
    {
        var effects = new Dictionary<int, string>();
        var loreLines = new List<string>();
        loreText = "";

        if (string.IsNullOrWhiteSpace(description)) return effects;

        string[] lines = description.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripRichText(lines[i]).Trim();
            if (line.Length == 0)
            {
                AddLoreLine(loreLines, "");
                continue;
            }

            if (IsDuplicateUpgradeTitle(line, upgradeName))
                continue;

            if (TryParseLevelEffectLine(line, out int tier, out string effect))
            {
                // A level line naming a rank this row does not sell is discarded, never clamped
                // onto the last real rank and never demoted into the summary as prose. Late Game
                // Upgrades can report fewer ranks than the catalog authors, and a unique-only
                // variant sells fewer ranks than the full native row (#435).
                if (tier >= 1 && tier <= maxTier)
                    effects[tier] = effect;
                continue;
            }

            if (maxTier == 1 && effects.Count == 0 && HasLeadingPrice(line))
            {
                effects[1] = CleanEffectText(RemoveLeadingPrice(line));
                continue;
            }

            AddLoreLine(loreLines, line);
        }

        loreText = BuildLoreText(loreLines);
        return effects;
    }

    internal static bool TryParseLevelEffectLine(string line, out int tier, out string effect)
    {
        tier = 0;
        effect = "";

        string working = RemoveLeadingPrice(line);
        var match = Regex.Match(working,
            @"\b(?:lvl|level|tier)\s*(\d+)\b\s*[:\-\)]?\s*(.*)$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups[1].Value, out tier)) return false;
        effect = CleanEffectText(match.Groups[2].Value);
        return effect.Length > 0;
    }

    internal static string StripRichText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return Regex.Replace(text, "<.*?>", "");
    }

    internal static bool HasLeadingPrice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text,
            @"^\s*(?:\$|\[\s*(?:BXP|MARKS?|TOKENS?)\s*\]|\d+\s*(?:BXP|BXPS|MARK|MARKS|TOKEN|TOKENS|PC)\b|\d+\s*[-:])",
            RegexOptions.IgnoreCase);
    }

    internal static string RemoveLeadingPrice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        string cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned,
            @"^\s*\[\s*(?:BXP|MARKS?|TOKENS?)\s*\]\s*\d+\s*(?:[-:]\s*)?",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned,
            @"^\s*\$\d+(?:\s*/\s*\d+\s*(?:PC|BXP|BXPS|MARK|MARKS|TOKEN|TOKENS)?)?\s*(?:[-:]\s*)?",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned,
            @"^\s*\d+\s*(?:PC|BXP|BXPS|MARK|MARKS|TOKEN|TOKENS)\s*(?:[-:]\s*)?",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned,
            @"^\s*\d+\s*[-:]\s+",
            "",
            RegexOptions.IgnoreCase);

        return cleaned.Trim();
    }

    internal static string CleanEffectText(string text)
    {
        string cleaned = StripRichText(text);
        cleaned = Regex.Replace(cleaned, @"^\s*[-:]\s*", "");
        cleaned = RemoveLeadingPrice(cleaned);
        cleaned = Regex.Replace(cleaned, @"^\s*[-:]\s*", "");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Trim(' ', '.', ';');
    }

    internal static string CleanDescriptionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        string cleaned = StripRichText(text).Trim();
        cleaned = Regex.Replace(cleaned,
            @"^\s*(?:description|overview|effect|effects)\s*:\s*",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length == 0) return "";
        return EnsurePeriod(cleaned);
    }

    /// <summary>
    /// Terminates authored copy. Empty in, empty out: a rank the catalog does not describe gets
    /// no sentence at all, rather than a fabricated one (#435).
    /// </summary>
    internal static string EnsurePeriod(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string cleaned = text.Trim();
        char last = cleaned[cleaned.Length - 1];
        return last == '.' || last == '!' || last == '?' ? cleaned : cleaned + ".";
    }

    internal static bool IsPlaceholder(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string lower = StripRichText(text).ToLowerInvariant();
        return lower.Contains("no native runtime effect")
            || lower.Contains("no current effect")
            || lower.Contains("no effect yet")
            || lower.Contains("currently implemented");
    }

    /// <summary>
    /// Normalizes authored effect text for display. Deliberately lossless: the level rows and the
    /// file summary both auto-size and scroll, so authored copy is never clipped or dropped
    /// (#366). Whitespace collapsing and leading-price stripping stay; the old 118-character
    /// truncation does not.
    /// </summary>
    internal static string Summarize(string text)
    {
        return EnsurePeriod(CleanEffectText(text));
    }

    private static bool IsDuplicateUpgradeTitle(string line, string upgradeName)
    {
        string a = NormalizeTitle(line);
        string b = NormalizeTitle(upgradeName);
        return a.Length > 0 && a == b;
    }

    private static string NormalizeTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "");
    }

    private static void AddLoreLine(List<string> loreLines, string line)
    {
        if (line.Length == 0)
        {
            if (loreLines.Count > 0 && loreLines[loreLines.Count - 1].Length != 0)
                loreLines.Add("");
            return;
        }

        loreLines.Add(line);
    }

    private static string BuildLoreText(List<string> loreLines)
    {
        while (loreLines.Count > 0 && loreLines[0].Length == 0)
            loreLines.RemoveAt(0);
        while (loreLines.Count > 0 && loreLines[loreLines.Count - 1].Length == 0)
            loreLines.RemoveAt(loreLines.Count - 1);

        return string.Join("\n", loreLines).Trim();
    }
}
