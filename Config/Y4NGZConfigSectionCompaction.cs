using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades.Config
{
    /// <summary>
    /// Filters a BepInEx config document to the broad sections Gale should display. The caller
    /// backs up the source before replacing it; keeping this transform Unity-free makes the
    /// lossless migration rule directly regression-testable.
    /// </summary>
    internal static class Y4NGZConfigSectionCompaction
    {
        internal static IReadOnlyList<string> KeepOnlyGroups(
            IEnumerable<string> lines,
            ISet<string> allowedGroups,
            out bool removedSections)
        {
            if (lines == null)
                throw new ArgumentNullException(nameof(lines));
            if (allowedGroups == null)
                throw new ArgumentNullException(nameof(allowedGroups));

            var kept = new List<string>();
            bool keepCurrentSection = true;
            removedSections = false;
            foreach (string line in lines)
            {
                if (TryReadSection(line, out string section))
                {
                    keepCurrentSection = allowedGroups.Contains(section);
                    if (!keepCurrentSection)
                        removedSections = true;
                }

                if (keepCurrentSection)
                    kept.Add(line ?? string.Empty);
            }

            return kept;
        }

        private static bool TryReadSection(string line, out string section)
        {
            section = string.Empty;
            string trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed)
                || trimmed.Length < 2
                || trimmed[0] != '['
                || trimmed[trimmed.Length - 1] != ']')
            {
                return false;
            }

            section = trimmed.Substring(1, trimmed.Length - 2).Trim();
            return section.Length > 0;
        }
    }
}
