using System;
using System.Collections.Generic;
using System.Linq;

namespace Y4NGZUpgrades
{
    internal readonly struct SaveSlotSnapshot
    {
        internal readonly string SaveFileName;
        internal readonly bool StatusKnown;
        internal readonly bool FileExists;
        internal readonly string LiveKey;

        internal SaveSlotSnapshot(
            string saveFileName,
            bool statusKnown,
            bool fileExists,
            string liveKey)
        {
            SaveFileName = saveFileName;
            StatusKnown = statusKnown;
            FileExists = fileExists;
            LiveKey = liveKey;
        }
    }

    internal static class SaveIdentityCleanupPolicy
    {
        internal static IReadOnlyList<string> FindOrphans(
            IEnumerable<string> candidateKeys,
            IEnumerable<SaveSlotSnapshot> snapshots)
        {
            var snapshotBySlot = (snapshots ?? Enumerable.Empty<SaveSlotSnapshot>())
                .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.SaveFileName))
                .GroupBy(snapshot => snapshot.SaveFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);

            var orphans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidateKeys ?? Enumerable.Empty<string>())
            {
                if (!TryGetManagedSlotName(candidate, out string saveFileName)
                    || !snapshotBySlot.TryGetValue(saveFileName, out SaveSlotSnapshot snapshot)
                    || !snapshot.StatusKnown)
                {
                    continue;
                }

                if (!snapshot.FileExists)
                {
                    orphans.Add(candidate);
                    continue;
                }

                // A legacy bare-slot file (pre-identity naming, e.g. "LCSaveFile1") is prunable, but
                // only once its slot is genuinely gone. While the slot still exists the file is the
                // live save's un-migrated data and the store adopts it into the identity-keyed name
                // on the next load; deleting it here would destroy it before that ever happens.
                if (IsLegacyBareSlotKey(candidate))
                    continue;

                // A pre-Y4NGZ or temporarily unreadable identity is not enough evidence to delete.
                if (string.IsNullOrWhiteSpace(snapshot.LiveKey))
                    continue;

                if (!string.Equals(candidate, snapshot.LiveKey, StringComparison.OrdinalIgnoreCase))
                    orphans.Add(candidate);
            }

            return orphans.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static readonly string[] ManagedSlotNames =
        {
            "LCSaveFile1",
            "LCSaveFile2",
            "LCSaveFile3"
        };

        /// <summary>
        /// True for a pre-identity store name such as "LCSaveFile1", written before save keys grew
        /// their "#&lt;guid&gt;" suffix. The PLAYER LEVEL store shipped files in this shape.
        /// </summary>
        internal static bool IsLegacyBareSlotKey(string key)
        {
            return !string.IsNullOrWhiteSpace(key)
                   && key.IndexOf('#') < 0
                   && IsManagedSlotName(key.Trim());
        }

        private static bool IsManagedSlotName(string slot)
        {
            for (int i = 0; i < ManagedSlotNames.Length; i++)
            {
                if (string.Equals(slot, ManagedSlotNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        internal static bool TryGetManagedSlotName(string key, out string saveFileName)
        {
            saveFileName = null;
            if (string.IsNullOrWhiteSpace(key))
                return false;

            int separator = key.IndexOf('#');

            // Legacy bare-slot names are managed too, otherwise their files could never be pruned.
            if (separator < 0)
            {
                string bareSlot = key.Trim();
                if (!IsManagedSlotName(bareSlot))
                    return false;

                saveFileName = bareSlot;
                return true;
            }

            if (separator == 0 || separator != key.LastIndexOf('#'))
                return false;

            string slot = key.Substring(0, separator);
            if (!IsManagedSlotName(slot))
                return false;

            string identity = key.Substring(separator + 1);
            if (identity.Length != 32)
                return false;

            for (int i = 0; i < identity.Length; i++)
            {
                char value = identity[i];
                bool isHex = value >= '0' && value <= '9'
                             || value >= 'a' && value <= 'f'
                             || value >= 'A' && value <= 'F';
                if (!isHex)
                    return false;
            }

            saveFileName = slot;
            return true;
        }
    }
}
