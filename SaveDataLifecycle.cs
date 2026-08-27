using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Y4NGZUpgrades.Gui;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades
{
    internal static class SaveDataLifecycle
    {
        private static readonly string[] SidecarDirectoryNames =
        {
            "Y4NGZAmmoReserve",
            "Y4NGZPlayerLevel",
            "Y4NGZEmployeeFile"
        };

        internal static void DeleteSave(string key, string reason)
        {
            DeleteSaves(new[] { key }, reason);
        }

        internal static void ReconcileOrphans()
        {
            try
            {
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                candidates.UnionWith(ProgressionManager.GetKnownSaveKeys());
                candidates.UnionWith(Y4NGZUpgradeManager.GetKnownSaveKeys());
                CollectSidecarKeys(candidates);

                SaveSlotSnapshot[] snapshots = SaveKey.ReadManagedSlotSnapshots();
                IReadOnlyList<string> orphans =
                    SaveIdentityCleanupPolicy.FindOrphans(candidates, snapshots);
                if (orphans.Count == 0)
                    return;

                DeleteSaves(orphans, "startup orphan reconciliation");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[Y4NGZUpgrades] Save-data orphan reconciliation skipped safely: {ex.Message}");
            }
        }

        private static void DeleteSaves(IEnumerable<string> keys, string reason)
        {
            string[] validKeys = (keys ?? Enumerable.Empty<string>())
                .Where(key => SaveIdentityCleanupPolicy.TryGetManagedSlotName(key, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (validKeys.Length == 0)
                return;

            int sidecarsRemoved = 0;
            for (int i = 0; i < validKeys.Length; i++)
            {
                string key = validKeys[i];
                // The Y4NGZAmmoReserve sidecar belongs to BetterArmory since #266; the folder name
                // is unchanged for save continuity and it prunes itself off SaveIdentityApi.
                SaveIdentityApi.RaiseSaveKeyDeleted(key);
                if (PlayerLevelStore.DeleteForKey(key))
                    sidecarsRemoved++;
                if (EmployeeStatistics.DeleteForKey(key))
                    sidecarsRemoved++;

                // A never-loaded save still carries its pre-identity sidecar under the bare slot
                // name; deleting the save has to take that file with it.
                if (!SaveIdentityCleanupPolicy.IsLegacyBareSlotKey(key)
                    && SaveIdentityCleanupPolicy.TryGetManagedSlotName(key, out string slot))
                {
                    SaveIdentityApi.RaiseSaveKeyDeleted(slot);
                    if (PlayerLevelStore.DeleteForKey(slot))
                        sidecarsRemoved++;
                    if (EmployeeStatistics.DeleteForKey(slot))
                        sidecarsRemoved++;
                }
            }

            int progressionRowsRemoved = 0;
            int upgradeRowsRemoved = 0;
            try
            {
                progressionRowsRemoved = ProgressionManager.DeleteForKeys(validKeys);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[Y4NGZUpgrades] Failed pruning progression rows: {ex.Message}");
            }

            try
            {
                upgradeRowsRemoved = Y4NGZUpgradeManager.DeleteForKeys(validKeys);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[Y4NGZUpgrades] Failed pruning upgrade rows: {ex.Message}");
            }

            Plugin.Log?.LogInfo(
                $"[Y4NGZUpgrades] Cleaned {validKeys.Length} save identity(s) after {reason}; " +
                $"removed {sidecarsRemoved} sidecar file(s), " +
                $"{progressionRowsRemoved} progression row(s), and " +
                $"{upgradeRowsRemoved} upgrade row(s).");
        }

        private static void CollectSidecarKeys(ISet<string> candidates)
        {
            for (int i = 0; i < SidecarDirectoryNames.Length; i++)
            {
                string directory = Path.Combine(Paths.ConfigPath, SidecarDirectoryNames[i]);
                if (!Directory.Exists(directory))
                    continue;

                foreach (string path in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    string key = Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrWhiteSpace(key))
                        candidates.Add(key);
                }
            }
        }
    }
}
