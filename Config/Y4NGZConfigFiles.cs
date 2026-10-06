using System;
using System.Collections.Generic;
using System.Globalization;
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

        // #435 renamed-group and #442 moved-group migration. Old copies are retired in one batch
        // behind one backup; the old-tier-default price rule runs until its one-time sidecar
        // marker exists, and never applies to a renamed-group copy.
        private static readonly List<ConfigEntryBase> PendingGroupRetirements = new List<ConfigEntryBase>();
        private static readonly HashSet<string> BackedUpBeforeRetiring =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _movedDefaultsMarked;
        private static bool _movedPriceRowsBound;
        private static int _renamedValues;
        private static int _movedValues;
        private static int _movedDefaultsReplaced;

        // #455 XP Sources default migration. The stamp is read once at startup; the pass runs
        // while SaveOnConfigSet is off, so CompleteMigration's save persists it in one batch and
        // stamps the profile afterwards.
        private static int _xpSourceDefaultsStamp;
        private static bool _xpSourceDefaultsPassRan;

        // #493 LGU Upgrade Layout default migration. Same shape: the marker is read once at
        // startup, the bound value changes in memory, and CompleteMigration stamps after its save.
        private static bool _lguLayoutDefaultStamped;
        private static bool _lguLayoutDefaultPassRan;
        private static bool _lguLayoutDefaultStampPending;

        internal static LegacyConfigDocument Legacy { get; private set; }
        internal static Y4NGZConfigScope PlayerMenu { get; private set; }
        internal static Y4NGZConfigScope Progression { get; private set; }
        internal static Y4NGZConfigScope XpSources { get; private set; }
        internal static Y4NGZConfigScope Lucky8 { get; private set; }

        /// <summary>
        /// The Late Game Upgrades group. #442 shipped it as [General]; every LGU scope carries its
        /// [General] copy over (<see cref="Y4NGZConfigScope.RenamedFromGroupName"/>). A switch that
        /// lived elsewhere before binds through <see cref="Y4NGZConfigScope.MovedFrom"/>.
        /// </summary>
        internal static Y4NGZConfigScope Lgu { get; private set; }

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
            _movedDefaultsMarked = Y4NGZConfigMigration.HasMovedDefaultsMarker(ConfigDirectoryPath);
            _xpSourceDefaultsStamp = Y4NGZConfigMigration.ReadXpSourceDefaultsStamp(ConfigDirectoryPath);
            _lguLayoutDefaultStamped = Y4NGZConfigMigration.HasLguLayoutDefaultMarker(ConfigDirectoryPath);
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
            Lgu = OpenScope(
                Y4NGZConfigLayout.LguScopeName,
                string.Empty,
                Y4NGZConfigLayout.LguScopeName,
                string.Empty);
        }

        internal static Y4NGZConfigScope Upgrade(UpgradeCatalogTable.Entry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            EnsureInitialized();
            // Rows that were renamed. The previous display name is what the earlier layouts used
            // for the section, the split file name and the grouped key prefix, so without it the
            // rename-migration branch in BindMigrated is never taken and the old customised values
            // are orphaned. F-INFRA-12 adds "veteran": it shipped as "Veteran" before #364 renamed
            // it to "Overachiever", and "Veteran" is now quota_guard's display name - so the old
            // keys were left for quota_guard to adopt instead of migrating to their own row.
            //
            // #435: the identity is the row's immutable ConfigName, never its visible title. The
            // title is now conditional (a native family can sell a unique-only variant under a
            // different name) and the imported rows dropped their "LGU " prefix, which would
            // otherwise collide native and imported Quick Hands and Deeper Pockets on one key.
            string previousName =
                entry.Id == "adrenaline_rush" ? "Adrenaline Rush" :
                entry.Id == "veteran" ? "Veteran" :
                entry.ConfigName;
            // #442: every imported row moved out of its class group; it now lives in LGU, which
            // #442 shipped as General. Its previous group and the tier its old default prices came
            // from are an explicit table, never its live class: Fedora Suit and Beekeeper also
            // changed class in the same release.
            string group = Y4NGZConfigLayout.UpgradeGroupName(entry);
            string previousGroup = entry.TreeClass.ToString();
            int previousTier = 0;
            if (group != previousGroup
                && Y4NGZConfigMigration.TryGetLguPreviousPlacement(entry.Id, out string placedGroup, out int placedTier))
            {
                previousGroup = placedGroup;
                previousTier = placedTier;
            }

            string previousScope = Y4NGZConfigLayout.GroupedKey(previousGroup, previousName, string.Empty);
            return OpenScope(
                group,
                entry.ConfigName,
                previousScope,
                previousScope + ".cfg",
                previousName,
                previousGroup,
                previousTier);
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
            // A later catalog pass re-binds every row. Hand back the live entry untouched: the
            // document is a boot-time snapshot, and re-running a migration from it would
            // overwrite whatever the player has changed since (#442 idempotence).
            if (_unifiedRoot.ContainsKey(new ConfigDefinition(targetSection, targetKey)))
                return _unifiedRoot.Bind(targetSection, targetKey, defaultValue, description);

            bool groupedAlreadyDefines = _unifiedDocument.Contains(targetSection, targetKey);
            ConfigEntry<T> entry = _unifiedRoot.Bind(
                targetSection,
                targetKey,
                defaultValue,
                description);
            if (target.HasRenamedGroup || target.HasPreviousGroup)
            {
                if (target.PreviousTier > 0)
                    _movedPriceRowsBound = true;
                bool moved = TryMoveGroupCopies(target, section, key, targetKey, defaultValue, description, entry);
                if (_completed)
                    FinishMovedGroupMigration();
                if (moved)
                    return entry;
            }

            if (groupedAlreadyDefines)
                return entry;

            if (target.PreviousKeyPrefix != target.KeyPrefix && Y4NGZConfigMigration.TryGetRenamedGroupValue(
                    _unifiedDocument, targetSection, targetKey, target.PreviousKey(section, key), out T renamed))
            {
                entry.Value = renamed;
                return entry;
            }

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

        /// <summary>
        /// A key whose group was renamed (#435) or moved (#442) keeps its name. Each older copy
        /// of this key is adopted and queued for retirement whether or not it supplies the value,
        /// so no orphan outlives the move; nothing else in an older group is touched. The newest
        /// copy wins: see <see cref="Y4NGZConfigScope.ResolveGroupMove{T}"/>.
        /// </summary>
        private static bool TryMoveGroupCopies<T>(
            Y4NGZConfigScope target,
            string section,
            string key,
            string targetKey,
            T defaultValue,
            ConfigDescription description,
            ConfigEntry<T> entry)
        {
            string previousKey = target.PreviousKey(section, key);
            Y4NGZGroupMove move = target.ResolveGroupMove(
                _unifiedDocument, section, key, defaultValue, _movedDefaultsMarked, out T value);
            if (move.RetiresRenamedCopy)
                AdoptRetiringCopy(target.RenamedFromGroupName, targetKey, defaultValue, description);
            if (move.RetiresPreviousCopy)
                AdoptRetiringCopy(target.PreviousGroupName, previousKey, defaultValue, description);
            if (!move.Carries)
                return false;

            entry.Value = value;
            if (move.Source == Y4NGZGroupMoveSource.RenamedGroup)
            {
                _renamedValues++;
                return true;
            }

            _movedValues++;
            if (_unifiedDocument.TryGet(target.PreviousGroupName, previousKey, out T stored)
                && !EqualityComparer<T>.Default.Equals(stored, value))
            {
                _movedDefaultsReplaced++;
            }

            return true;
        }

        /// <summary>Binds an older copy so the retirement batch can remove it, unless it is live.</summary>
        private static void AdoptRetiringCopy<T>(string group, string key, T defaultValue, ConfigDescription description)
        {
            if (_unifiedRoot.ContainsKey(new ConfigDefinition(group, key)))
                return;
            PendingGroupRetirements.Add(_unifiedRoot.Bind(group, key, defaultValue, description));
        }

        /// <summary>Retires the renamed and moved keys' old copies in one batch behind one backup.</summary>
        private static void RetireMovedGroupKeys()
        {
            if (PendingGroupRetirements.Count > 0)
            {
                ConfigEntryBase[] retiring = PendingGroupRetirements.ToArray();
                PendingGroupRetirements.Clear();
                RetireBindings("lgu-group", "old copies of setting(s) that moved into the LGU group", retiring);
            }

            if (_renamedValues > 0 || _movedValues > 0)
            {
                Plugin.Log?.LogInfo(
                    $"[Config] Carried {_renamedValues} setting(s) unchanged from [{Y4NGZConfigLayout.LegacyGeneralGroupName}] "
                    + $"and {_movedValues} from their earlier groups into [{Y4NGZConfigLayout.LguScopeName}]; "
                    + $"{_movedDefaultsReplaced} price(s) that still matched an old tier default took the new default.");
                _renamedValues = 0;
                _movedValues = 0;
                _movedDefaultsReplaced = 0;
            }
        }

        /// <summary>
        /// After a save that holds the imported rows in their new group, the old-tier-default rule
        /// has run its once: the sidecar marker makes every later move carry values verbatim.
        /// </summary>
        private static void MarkMovedDefaults()
        {
            if (!_movedPriceRowsBound || _movedDefaultsMarked)
                return;
            try
            {
                Y4NGZConfigMigration.WriteMovedDefaultsMarker(ConfigDirectoryPath);
                _movedDefaultsMarked = true;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    $"[Config] Imported upgrade settings moved, but the one-time migration marker could not be written: {exception.Message}");
            }
        }

        /// <summary>
        /// Replaces XP Sources values that still hold a default the code has moved (#455), once
        /// per profile. Called after the XP source bindings exist - including the legacy-section
        /// fallback, so a value carried from an old key is judged the same way. Only in-memory
        /// values change here; <see cref="CompleteMigration"/> saves them in one batch and then
        /// stamps the profile. Adding a key is a row in
        /// <see cref="XpSourceDefaultsMigration"/>, never a change here.
        /// </summary>
        internal static void MigrateXpSourceDefaults()
        {
            EnsureInitialized();
            if (_xpSourceDefaultsPassRan || !XpSourceDefaultsMigration.IsStale(_xpSourceDefaultsStamp))
                return;

            _xpSourceDefaultsPassRan = true;
            IReadOnlyList<XpSourceDefaultsMigration.Row> rows = XpSourceDefaultsMigration.All;
            for (int index = 0; index < rows.Count; index++)
            {
                XpSourceDefaultsMigration.Row row = rows[index];
                var definition = new ConfigDefinition(
                    XpSources.GroupName,
                    XpSources.Key(row.Section, row.Key));
                if (!_unifiedRoot.ContainsKey(definition))
                    continue;

                // TryGetEntry<T> casts instead of type-testing, so asking for the wrong T throws
                // (an int XP amount asked for as float killed Plugin.Awake). Switch on the entry's
                // bound type; a row may move a fraction or an integer XP amount.
                switch (_unifiedRoot[definition])
                {
                    case ConfigEntry<float> floatEntry
                        when XpSourceDefaultsMigration.TryMigrate(
                            row, _xpSourceDefaultsStamp, floatEntry.Value, out double migratedFloat):
                        LogXpSourceMigration(definition, floatEntry.Value, migratedFloat);
                        floatEntry.Value = (float)migratedFloat;
                        break;
                    case ConfigEntry<int> intEntry
                        when XpSourceDefaultsMigration.TryMigrate(
                            row, _xpSourceDefaultsStamp, intEntry.Value, out double migratedInt):
                        LogXpSourceMigration(definition, intEntry.Value, migratedInt);
                        intEntry.Value = (int)Math.Round(migratedInt);
                        break;
                }
            }
        }

        private static void LogXpSourceMigration(ConfigDefinition definition, double oldValue, double newValue)
        {
            Plugin.Log?.LogInfo(
                $"[Config] Migrated XP source [{definition.Section}] {definition.Key}: "
                + $"{oldValue.ToString(CultureInfo.InvariantCulture)} -> "
                + $"{newValue.ToString(CultureInfo.InvariantCulture)} (its default changed since "
                + "this profile was written).");
        }

        /// <summary>
        /// After the save that holds the migrated XP Sources values, the pass has run its once:
        /// the versioned sidecar stamp keeps the next boot from judging those keys again.
        /// </summary>
        private static void MarkXpSourceDefaults()
        {
            if (!_xpSourceDefaultsPassRan)
                return;
            try
            {
                int stamp = XpSourceDefaultsMigration.NextStamp(_xpSourceDefaultsStamp);
                Y4NGZConfigMigration.WriteXpSourceDefaultsStamp(ConfigDirectoryPath, stamp);
                _xpSourceDefaultsStamp = stamp;
                _xpSourceDefaultsPassRan = false;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    $"[Config] XP source defaults were migrated, but the one-time stamp could not be written: {exception.Message}");
            }
        }

        /// <summary>
        /// Moves the LGU Upgrade Layout off its old ClassTrees default once per profile (#493).
        /// Called straight after the binding, before anything reads it, so a value carried from an
        /// older group is judged the same way. Only the in-memory value changes here;
        /// <see cref="CompleteMigration"/> saves it and then stamps the profile.
        /// </summary>
        internal static void MigrateLguLayoutDefault(ConfigEntry<Gui.LguUpgradeLayout> layout)
        {
            EnsureInitialized();
            if (layout == null || _lguLayoutDefaultPassRan)
                return;

            _lguLayoutDefaultPassRan = true;
            LguLayoutDefaultMigration.Outcome outcome =
                LguLayoutDefaultMigration.Decide(_lguLayoutDefaultStamped, layout.Value);
            if (outcome.Changed)
            {
                Plugin.Log?.LogInfo(
                    $"[Config] Migrated [{layout.Definition.Section}] {layout.Definition.Key}: {layout.Value} -> "
                    + $"{outcome.Layout} (its default changed since this profile was written).");
                layout.Value = outcome.Layout;
            }

            _lguLayoutDefaultStampPending = outcome.Stamps;
            if (_completed)
                MarkLguLayoutDefault();
        }

        /// <summary>After the save that holds the migrated layout, the pass has run its once.</summary>
        private static void MarkLguLayoutDefault()
        {
            if (!_lguLayoutDefaultStampPending)
                return;
            try
            {
                Y4NGZConfigMigration.WriteLguLayoutDefaultMarker(ConfigDirectoryPath);
                _lguLayoutDefaultStamped = true;
                _lguLayoutDefaultStampPending = false;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    $"[Config] The LGU Upgrade Layout default was migrated, but the one-time stamp could not be written: {exception.Message}");
            }
        }

        /// <summary>The moved-group tail for a row bound after <see cref="CompleteMigration"/>.</summary>
        private static void FinishMovedGroupMigration()
        {
            if (PendingGroupRetirements.Count == 0 && (_movedDefaultsMarked || !_movedPriceRowsBound))
                return;
            RetireMovedGroupKeys();
            _unifiedRoot.Save();
            MarkMovedDefaults();
        }

        internal static bool TargetAlreadyDefines(
            Y4NGZConfigScope target,
            string section,
            string key)
        {
            EnsureInitialized();
            return target != null
                   && (_unifiedDocument.Contains(target.GroupName, target.PreviousKey(section, key))
                       || (target.HasRenamedGroup
                           && _unifiedDocument.Contains(target.RenamedFromGroupName, target.Key(section, key)))
                       || (target.HasPreviousGroup
                           && _unifiedDocument.Contains(target.PreviousGroupName, target.PreviousKey(section, key)))
                       || Y4NGZConfigMigration.HasUserValue(
                       _unifiedDocument,
                       target.GroupName,
                       target.Key(section, key),
                       target.PreviousSection(section),
                       key,
                       target.SplitDocument,
                       section,
                       key));
        }

        internal static bool TryGetLegacy<T>(string section, string key, out T value)
        {
            EnsureInitialized();
            return Legacy.TryGet(section, key, out value);
        }

        /// <summary>
        /// Adopts then removes obsolete keys so the next save drops them. The file is backed up
        /// first, once per <paramref name="backupLabel"/> per launch; if that backup fails the
        /// keys stay put and false is returned.
        /// </summary>
        internal static bool RetireBindings(string backupLabel, string retiredWhat, params ConfigEntryBase[] entries)
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

            if (currentFileContainsRetiredValue && File.Exists(UnifiedConfigPath)
                && !BackedUpBeforeRetiring.Contains(backupLabel))
            {
                try
                {
                    string archiveDirectory = Path.Combine(ConfigDirectoryPath, "Legacy");
                    Directory.CreateDirectory(archiveDirectory);
                    string archivePath = AvailableArchivePath(
                        archiveDirectory,
                        $"Y4NGZUpgrades.pre-{backupLabel}.cfg.bak");
                    File.Copy(UnifiedConfigPath, archivePath, overwrite: false);
                    BackedUpBeforeRetiring.Add(backupLabel);
                    Plugin.Log?.LogInfo(
                        $"[Config] Backed up {Y4NGZConfigLayout.UnifiedFileName} before retiring {retiredWhat}: '{archivePath}'.");
                }
                catch (Exception exception)
                {
                    Plugin.Log?.LogWarning(
                        $"[Config] Kept {retiredWhat} because the safety backup failed: {exception.Message}");
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
                Plugin.Log?.LogInfo($"[Config] Retired {retired} {retiredWhat}.");
            return true;
        }

        internal static void CompleteMigration()
        {
            if (!_initialized || _completed)
                return;

            // This save is the transaction boundary. None of the source files are retired until
            // every live binding is safely represented in the unified file.
            RetireMovedGroupKeys();
            _unifiedRoot.Save();
            MarkMovedDefaults();
            MarkXpSourceDefaults();
            MarkLguLayoutDefault();
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
            string legacySplitFileName,
            string previousKeyPrefix = null,
            string previousGroupName = null,
            int previousTier = 0)
        {
            EnsureInitialized();
            // Keyed on the LIVE identity (group + key prefix), not on the previous scope name.
            // Two rows can legitimately share a previous name - F-INFRA-12 gives "veteran" the
            // "Veteran" that quota_guard now displays - and keying on the old name would hand the
            // second row the first row's scope and bind its prices under the wrong prefix.
            string cacheKey = groupName + "\n" + keyPrefix;
            if (Scopes.TryGetValue(cacheKey, out Y4NGZConfigScope existing))
                return existing;

            string splitPath = Path.Combine(ConfigDirectoryPath, legacySplitFileName);
            var scope = new Y4NGZConfigScope(
                groupName,
                keyPrefix,
                previousUnifiedScopeName,
                legacySplitFileName,
                LegacyConfigDocument.Load(splitPath), previousKeyPrefix, previousGroupName, previousTier);
            Scopes[cacheKey] = scope;
            return scope;
        }

        private static void CompactUnifiedSections()
        {
            string source = UnifiedConfigPath;
            if (!File.Exists(source))
                return;

            var allowed = new HashSet<string>(
                Y4NGZConfigLayout.CompactionKeptGroupNames(),
                StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(source);
            IReadOnlyList<string> kept = Y4NGZConfigSectionCompaction.KeepOnlyGroups(
                lines,
                allowed,
                out bool needsCompaction);
            if (!needsCompaction)
                return;

            string archiveDirectory = Path.Combine(ConfigDirectoryPath, "Legacy");
            string temporary = source + ".grouped.tmp";
            try
            {
                Directory.CreateDirectory(archiveDirectory);
                string archivePath = AvailableArchivePath(
                    archiveDirectory,
                    "Y4NGZUpgrades.pre-grouped.cfg.bak");
                File.Copy(source, archivePath, overwrite: false);

                if (File.Exists(temporary))
                    File.Delete(temporary);
                File.WriteAllLines(temporary, kept, new System.Text.UTF8Encoding(false));
                File.Replace(temporary, source, null);

                // Reload clears ConfigFile's orphan dictionary. Saving once more emits only the
                // live groups plus any user-authored keys already inside those groups.
                _unifiedRoot.Reload();
                _unifiedRoot.Save();
                Plugin.Log?.LogInfo(
                    $"[Config] Consolidated Gale into {Y4NGZConfigLayout.AllGroupNames().Count} groups. "
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
}
