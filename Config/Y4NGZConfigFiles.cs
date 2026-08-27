using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace Y4NGZUpgrades.Config
{
    /// <summary>
    /// Owns the single Gale-visible Y4NGZUpgrades.cfg file and losslessly imports all earlier
    /// layouts: the fine-grained unified sections, the 31 focused split files, and the original
    /// com.y4ngz.upgrades.cfg monolith. Sources are retired only after grouped values are saved.
    /// </summary>
    internal static class Y4NGZConfigFiles
    {
        private static readonly Dictionary<string, Y4NGZConfigScope> Scopes =
            new Dictionary<string, Y4NGZConfigScope>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<ConfigFile> Files = new List<ConfigFile>(1);

        private static ConfigFile _legacyRoot;
        private static ConfigFile _unifiedRoot;
        private static LegacyConfigDocument _unifiedDocument;
        private static bool _initialized;
        private static bool _completed;

        internal static LegacyConfigDocument Legacy { get; private set; }
        internal static Y4NGZConfigScope PlayerMenu { get; private set; }
        internal static Y4NGZConfigScope Progression { get; private set; }
        internal static Y4NGZConfigScope XpSources { get; private set; }
        internal static Y4NGZConfigScope Lucky8 { get; private set; }
        internal static string UnifiedConfigPath =>
            Path.Combine(Paths.ConfigPath, Y4NGZConfigLayout.UnifiedFileName);
        internal static string ConfigDirectoryPath =>
            Path.Combine(Paths.ConfigPath, Y4NGZConfigLayout.SupportDirectoryName);

        internal static IReadOnlyCollection<ConfigFile> All => Files;

        internal static void Initialize(ConfigFile legacyRoot, BepInPlugin ownerMetadata)
        {
            if (_initialized)
                return;

            _initialized = true;
            _legacyRoot = legacyRoot ?? throw new ArgumentNullException(nameof(legacyRoot));
            Legacy = LegacyConfigDocument.Load(_legacyRoot.ConfigFilePath);
            Directory.CreateDirectory(ConfigDirectoryPath);

            _unifiedDocument = LegacyConfigDocument.Load(UnifiedConfigPath);
            _unifiedRoot = new ConfigFile(UnifiedConfigPath, saveOnInit: false, ownerMetadata)
            {
                SaveOnConfigSet = false
            };
            Files.Add(_unifiedRoot);

            PlayerMenu = OpenScope(
                Y4NGZConfigLayout.PlayerMenuScopeName,
                string.Empty,
                Y4NGZConfigLayout.PlayerMenuScopeName,
                Y4NGZConfigLayout.LegacyPlayerMenuFileName);
            Progression = OpenScope(
                Y4NGZConfigLayout.ProgressionScopeName,
                string.Empty,
                Y4NGZConfigLayout.ProgressionScopeName,
                Y4NGZConfigLayout.LegacyProgressionFileName);
            XpSources = OpenScope(
                Y4NGZConfigLayout.XpSourcesScopeName,
                string.Empty,
                Y4NGZConfigLayout.XpSourcesScopeName,
                Y4NGZConfigLayout.LegacyXpSourcesFileName);
            Lucky8 = OpenScope(
                Y4NGZConfigLayout.Lucky8ScopeName,
                string.Empty,
                Y4NGZConfigLayout.Lucky8ScopeName,
                Y4NGZConfigLayout.LegacyLucky8FileName);
        }

        internal static Y4NGZConfigScope Upgrade(UpgradeCatalogTable.Entry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            EnsureInitialized();
            return OpenScope(
                entry.TreeClass.ToString(),
                entry.DisplayName,
                Y4NGZConfigLayout.UpgradeScopeName(entry.TreeClass, entry.DisplayName),
                Y4NGZConfigLayout.LegacyUpgradeFileName(entry.TreeClass, entry.DisplayName));
        }

        internal static Y4NGZConfigScope Upgrade(string stableId)
        {
            return Upgrade(UpgradeCatalogTable.Get(stableId));
        }

        internal static ConfigEntry<T> BindMigrated<T>(
            Y4NGZConfigScope target,
            string section,
            string key,
            T defaultValue,
            string description,
            params LegacyConfigKey[] legacyKeys)
        {
            return BindMigrated(
                target,
                section,
                key,
                defaultValue,
                new ConfigDescription(description),
                legacyKeys);
        }

        internal static ConfigEntry<T> BindMigrated<T>(
            Y4NGZConfigScope target,
            string section,
            string key,
            T defaultValue,
            ConfigDescription description,
            params LegacyConfigKey[] legacyKeys)
        {
            EnsureInitialized();
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            string targetSection = target.GroupName;
            string targetKey = target.Key(section, key);
            bool groupedAlreadyDefines = _unifiedDocument.Contains(targetSection, targetKey);
            ConfigEntry<T> entry = _unifiedRoot.Bind(
                targetSection,
                targetKey,
                defaultValue,
                description);
            if (groupedAlreadyDefines)
                return entry;

            if (!Y4NGZConfigMigration.TryGetFallback(
                    _unifiedDocument,
                    target.PreviousSection(section),
                    key,
                    target.SplitDocument,
                    section,
                    key,
                    Legacy,
                    legacyKeys,
                    out T migrated,
                    out Y4NGZConfigMigrationSource source))
            {
                return entry;
            }

            entry.Value = migrated;
            string sourceName = source == Y4NGZConfigMigrationSource.PreviousUnified
                ? Y4NGZConfigLayout.UnifiedFileName
                : source == Y4NGZConfigMigrationSource.SplitFile
                    ? target.LegacySplitFileName
                    : Path.GetFileName(_legacyRoot.ConfigFilePath);
            string sourceSection = source == Y4NGZConfigMigrationSource.PreviousUnified
                ? target.PreviousSection(section)
                : section;
            Plugin.Log?.LogInfo(
                $"[Config] Migrated {sourceName} [{sourceSection}] {key} -> "
                + $"{Y4NGZConfigLayout.UnifiedFileName} [{targetSection}] {targetKey}.");
            return entry;
        }

        internal static bool TargetAlreadyDefines(
            Y4NGZConfigScope target,
            string section,
            string key)
        {
            EnsureInitialized();
            return target != null
                   && Y4NGZConfigMigration.HasUserValue(
                       _unifiedDocument,
                       target.GroupName,
                       target.Key(section, key),
                       target.PreviousSection(section),
                       key,
                       target.SplitDocument,
                       section,
                       key);
        }

        internal static bool TryGetLegacy<T>(string section, string key, out T value)
        {
            EnsureInitialized();
            return Legacy.TryGet(section, key, out value);
        }

        internal static bool RetireBindings(params ConfigEntryBase[] entries)
        {
            EnsureInitialized();
            if (entries == null || entries.Length == 0)
                return true;

            bool currentFileContainsRetiredValue = false;
            for (int index = 0; index < entries.Length; index++)
            {
                ConfigEntryBase entry = entries[index];
                if (entry != null
                    && _unifiedDocument.Contains(entry.Definition.Section, entry.Definition.Key))
                {
                    currentFileContainsRetiredValue = true;
                    break;
                }
            }

            if (currentFileContainsRetiredValue && File.Exists(UnifiedConfigPath))
            {
                try
                {
                    string archiveDirectory = Path.Combine(ConfigDirectoryPath, "Legacy");
                    Directory.CreateDirectory(archiveDirectory);
                    string archivePath = AvailableArchivePath(
                        archiveDirectory,
                        "Y4NGZUpgrades.pre-input-menu.cfg.bak");
                    File.Copy(UnifiedConfigPath, archivePath, overwrite: false);
                    Plugin.Log?.LogInfo(
                        $"[Config] Backed up legacy control settings before InputUtils migration: '{archivePath}'.");
                }
                catch (Exception exception)
                {
                    Plugin.Log?.LogWarning(
                        "[Config] Legacy controls were not retired because their safety backup failed: "
                        + exception.Message);
                    return false;
                }
            }

            int retired = 0;
            for (int index = 0; index < entries.Length; index++)
            {
                ConfigEntryBase entry = entries[index];
                if (entry != null && _unifiedRoot.Remove(entry.Definition))
                    retired++;
            }

            if (retired > 0)
            {
                Plugin.Log?.LogInfo(
                    $"[Config] Retired {retired} legacy control setting(s); controls now live in the in-game keybind menu.");
            }
            return true;
        }

        internal static void CompleteMigration()
        {
            if (!_initialized || _completed)
                return;

            // This save is the transaction boundary. None of the source files are retired until
            // every live binding is safely represented in the unified file.
            _unifiedRoot.Save();
            CompactUnifiedSections();
            _unifiedRoot.SaveOnConfigSet = true;

            ArchiveSplitFiles();
            ArchiveLegacyRoot();
            _completed = true;
        }

        private static Y4NGZConfigScope OpenScope(
            string groupName,
            string keyPrefix,
            string previousUnifiedScopeName,
            string legacySplitFileName)
        {
            EnsureInitialized();
            if (Scopes.TryGetValue(previousUnifiedScopeName, out Y4NGZConfigScope existing))
                return existing;

            string splitPath = Path.Combine(ConfigDirectoryPath, legacySplitFileName);
            var scope = new Y4NGZConfigScope(
                groupName,
                keyPrefix,
                previousUnifiedScopeName,
                legacySplitFileName,
                LegacyConfigDocument.Load(splitPath));
            Scopes[previousUnifiedScopeName] = scope;
            return scope;
        }

        private static void CompactUnifiedSections()
        {
            string source = UnifiedConfigPath;
            if (!File.Exists(source))
                return;

            var allowed = new HashSet<string>(
                Y4NGZConfigLayout.AllGroupNames(),
                StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(source);
            IReadOnlyList<string> kept = Y4NGZConfigSectionCompaction.KeepOnlyGroups(
                lines,
                allowed,
                out bool needsCompaction);
            if (!needsCompaction)
                return;

            string archiveDirectory = Path.Combine(ConfigDirectoryPath, "Legacy");
            string temporary = source + ".eight-groups.tmp";
            try
            {
                Directory.CreateDirectory(archiveDirectory);
                string archivePath = AvailableArchivePath(
                    archiveDirectory,
                    "Y4NGZUpgrades.pre-eight-groups.cfg.bak");
                File.Copy(source, archivePath, overwrite: false);

                if (File.Exists(temporary))
                    File.Delete(temporary);
                File.WriteAllLines(temporary, kept, new System.Text.UTF8Encoding(false));
                File.Replace(temporary, source, null);

                // Reload clears ConfigFile's orphan dictionary. Saving once more emits only the
                // eight live groups plus any user-authored keys already inside those groups.
                _unifiedRoot.Reload();
                _unifiedRoot.Save();
                Plugin.Log?.LogInfo(
                    $"[Config] Consolidated Gale into {allowed.Count} groups. "
                    + $"Pre-grouped backup: '{archivePath}'.");
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    $"[Config] Grouped values saved, but old Gale sections could not be compacted safely: {exception.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch
                {
                    // Best-effort cleanup; the .tmp extension is never presented by Gale.
                }
            }
        }

        private static void ArchiveSplitFiles()
        {
            string archiveDirectory = Path.Combine(ConfigDirectoryPath, "Legacy");
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Y4NGZConfigScope scope in Scopes.Values)
                fileNames.Add(scope.LegacySplitFileName);

            int retired = 0;
            foreach (string fileName in fileNames)
            {
                string source = Path.Combine(ConfigDirectoryPath, fileName);
                if (!File.Exists(source))
                    continue;

                try
                {
                    if (new FileInfo(source).Length > 0)
                    {
                        Directory.CreateDirectory(archiveDirectory);
                        string archivePath = AvailableArchivePath(
                            archiveDirectory,
                            fileName + ".pre-unified.bak");
                        File.Copy(source, archivePath, overwrite: false);
                    }

                    File.Delete(source);
                    retired++;
                }
                catch (Exception exception)
                {
                    Plugin.Log?.LogWarning(
                        $"[Config] Unified config saved, but '{fileName}' could not be archived safely: {exception.Message}");
                }
            }

            if (retired > 0)
            {
                Plugin.Log?.LogInfo(
                    $"[Config] Retired {retired} split config file(s); backups use .bak under "
                    + $"'{archiveDirectory}'.");
            }
        }

        private static void ArchiveLegacyRoot()
        {
            string source = _legacyRoot?.ConfigFilePath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                return;

            try
            {
                string archivePath = null;
                if (new FileInfo(source).Length > 0)
                {
                    string archiveDirectory = Path.Combine(ConfigDirectoryPath, "Legacy");
                    Directory.CreateDirectory(archiveDirectory);
                    archivePath = AvailableArchivePath(
                        archiveDirectory,
                        "com.y4ngz.upgrades.pre-unified.cfg.bak");
                    File.Copy(source, archivePath, overwrite: false);
                }

                // ConfigFile has no public orphan-removal API. Reloading an empty temporary file
                // clears its in-memory orphan dictionary before deletion, preventing a later
                // config-manager save from recreating the retired monolith.
                File.WriteAllText(source, string.Empty);
                _legacyRoot.Reload();
                File.Delete(source);

                string archiveMessage = archivePath == null
                    ? "The empty legacy file was removed."
                    : $"Original archived at '{archivePath}'.";
                Plugin.Log?.LogInfo(
                    $"[Config] Unified settings in {Y4NGZConfigLayout.UnifiedFileName}. "
                    + archiveMessage);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    $"[Config] Unified config saved, but the legacy monolith could not be archived safely: {exception.Message}");
            }
        }

        private static string AvailableArchivePath(string directory, string fileName)
        {
            string candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate))
                return candidate;

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            for (int index = 2; ; index++)
            {
                candidate = Path.Combine(directory, stem + "-" + index + extension);
                if (!File.Exists(candidate))
                    return candidate;
            }
        }

        private static void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("Y4NGZ config files have not been initialized.");
        }
    }

    internal sealed class Y4NGZConfigScope
    {
        internal Y4NGZConfigScope(
            string groupName,
            string keyPrefix,
            string previousUnifiedScopeName,
            string legacySplitFileName,
            LegacyConfigDocument splitDocument)
        {
            GroupName = groupName ?? string.Empty;
            KeyPrefix = keyPrefix ?? string.Empty;
            PreviousUnifiedScopeName = previousUnifiedScopeName ?? string.Empty;
            LegacySplitFileName = legacySplitFileName ?? string.Empty;
            SplitDocument = splitDocument;
        }

        internal string GroupName { get; }
        internal string KeyPrefix { get; }
        internal string PreviousUnifiedScopeName { get; }
        internal string LegacySplitFileName { get; }
        internal LegacyConfigDocument SplitDocument { get; }

        internal string Key(string subsection, string key)
        {
            return Y4NGZConfigLayout.GroupedKey(KeyPrefix, subsection, key);
        }

        internal string PreviousSection(string subsection)
        {
            return Y4NGZConfigLayout.PreviousUnifiedSection(
                PreviousUnifiedScopeName,
                subsection);
        }
    }
}
