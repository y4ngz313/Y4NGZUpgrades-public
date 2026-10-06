using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Resolves the save key the upgrade state must be persisted under. Returns false while no
    /// save is addressable (fresh slot, or a client whose host identity has not arrived yet).
    /// </summary>
    internal delegate bool TryResolveSaveKeyDelegate(out string key);

    internal sealed class UpgradeSaveState
    {
        /// <summary>
        /// Full-native ranks and imported Late Game Upgrades ranks. This is the record every save
        /// has always had; it keeps its meaning whatever the live policy says (#435).
        /// </summary>
        internal readonly Dictionary<string, int> Levels =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal bool ImportedLegacyLevels;
        internal readonly HashSet<string> ImportedLguLevels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Unique-only ranks of the native families (#435), kept apart from <see cref="Levels"/>
        /// so neither mode's ownership is ever reinterpreted as the other's. A smaller live
        /// MaxTier is not a save migration.
        /// </summary>
        internal readonly Dictionary<string, int> UniqueLevels =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, int> RanksFor(UpgradeRankRecord record)
        {
            return record == UpgradeRankRecord.UniqueOnly ? UniqueLevels : Levels;
        }

        internal bool ImportLguLevel(string id, int level)
        {
            if (!ImportedLguLevels.Add(id))
                return false;
            Levels.TryGetValue(id, out int owned);
            if (level > owned)
                Levels[id] = level;
            return true;
        }

        /// <summary>
        /// Re-arms the one-time import for a row the provider stopped offering. Without this a
        /// rank bought back with company credits while the row was unavailable would be driven
        /// away the next time it binds.
        /// </summary>
        internal void ClearLguImport(string id)
        {
            ImportedLguLevels.Remove(id);
        }

        /// <summary>
        /// Re-arms every one-time import (#493) for a session in which Late Game Upgrades is
        /// installed but not integrated into the player menu: it sells the imported rows for
        /// credits again then, so the next integrated load must merge what it holds (the larger of
        /// the token rank and LGU's) instead of driving it away. Levels are untouched. True when a
        /// marker was cleared, so the caller has something to persist.
        /// </summary>
        internal bool RearmLguImports()
        {
            if (ImportedLguLevels.Count == 0)
                return false;
            ImportedLguLevels.Clear();
            return true;
        }

        /// <summary>
        /// One save's line in upgrade_levels.txt:
        /// <c>key|levels|importedLegacy|importedLgu|uniqueLevels</c>. The fifth field is new in
        /// #435; a line written before it still decodes, with an empty unique record.
        /// </summary>
        internal string Encode(string saveKey)
        {
            var builder = new StringBuilder();
            builder.Append(saveKey).Append('|');
            AppendRanks(builder, Levels);
            builder.Append('|').Append(ImportedLegacyLevels).Append('|');
            bool first = true;
            foreach (string id in ImportedLguLevels.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!first)
                    builder.Append(';');
                builder.Append(id);
                first = false;
            }

            builder.Append('|');
            AppendRanks(builder, UniqueLevels);
            return builder.ToString();
        }

        internal static bool TryDecode(
            string line,
            Func<string, bool> isBridgedUpgrade,
            out string saveKey,
            out UpgradeSaveState state)
        {
            saveKey = null;
            state = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string[] parts = line.Split('|');
            if (parts.Length < 2)
                return false;

            string key = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(key))
                return false;

            var decoded = new UpgradeSaveState();
            ParseRanks(parts[1], decoded.Levels);
            decoded.ImportedLegacyLevels = parts.Length >= 3
                && bool.TryParse(parts[2], out bool imported)
                && imported;
            if (parts.Length >= 4)
            {
                foreach (string id in parts[3].Split(';'))
                {
                    if (isBridgedUpgrade != null && isBridgedUpgrade(id))
                        decoded.ImportedLguLevels.Add(id);
                }
            }

            if (parts.Length >= 5)
                ParseRanks(parts[4], decoded.UniqueLevels);

            saveKey = key;
            state = decoded;
            return true;
        }

        private static void AppendRanks(StringBuilder builder, Dictionary<string, int> ranks)
        {
            bool first = true;
            foreach (KeyValuePair<string, int> pair in ranks
                         .Where(x => x.Value > 0)
                         .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!first)
                    builder.Append(';');
                builder.Append(pair.Key).Append('=').Append(pair.Value);
                first = false;
            }
        }

        private static void ParseRanks(string encoded, Dictionary<string, int> ranks)
        {
            ranks.Clear();
            if (string.IsNullOrWhiteSpace(encoded))
                return;

            string[] entries = encoded.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i];
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                int separator = entry.IndexOf('=');
                if (separator <= 0 || separator >= entry.Length - 1)
                    continue;

                string id = Y4NGZUpgradeDefinition.NormalizeId(entry.Substring(0, separator));
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (int.TryParse(entry.Substring(separator + 1), out int level) && level > 0)
                    ranks[id] = level;
            }
        }
    }

    /// <summary>
    /// Owns the per-save upgrade state dictionary and which save is live. Unity-free so the
    /// deterministic checks exercise the same code the plugin runs.
    ///
    /// The invariant that makes purchases safe: while no save key resolves, <see cref="Current"/>
    /// is null instead of an orphan state object. Callers cannot write a level into a state that
    /// will never be persisted, and a later switch cannot silently discard one. #213.
    /// </summary>
    internal sealed class UpgradeSaveStateStore
    {
        private readonly Dictionary<string, UpgradeSaveState> _states =
            new Dictionary<string, UpgradeSaveState>(StringComparer.OrdinalIgnoreCase);
        private readonly TryResolveSaveKeyDelegate _tryResolveKey;
        private UpgradeSaveState _state;
        private string _currentKey;

        internal UpgradeSaveStateStore(TryResolveSaveKeyDelegate tryResolveKey)
        {
            _tryResolveKey = tryResolveKey ?? throw new ArgumentNullException(nameof(tryResolveKey));
        }

        internal string CurrentKey => _currentKey;

        /// <summary>Live state for the current save, or null while no save key resolves.</summary>
        internal UpgradeSaveState Current => _currentKey == null ? null : _state;

        internal bool HasCurrent => _currentKey != null && _state != null;

        internal IReadOnlyCollection<string> Keys
        {
            get
            {
                var keys = new string[_states.Count];
                _states.Keys.CopyTo(keys, 0);
                return keys;
            }
        }

        internal IEnumerable<KeyValuePair<string, UpgradeSaveState>> Entries => _states;

        internal bool TryGetState(string key, out UpgradeSaveState state)
        {
            state = null;
            return !string.IsNullOrWhiteSpace(key) && _states.TryGetValue(key, out state);
        }

        internal void SetState(string key, UpgradeSaveState state)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            _states[key] = state ?? new UpgradeSaveState();
            if (string.Equals(key, _currentKey, StringComparison.OrdinalIgnoreCase))
                _state = _states[key];
        }

        internal void Clear()
        {
            _states.Clear();
            _currentKey = null;
            _state = null;
        }

        /// <summary>
        /// Re-points the live state at whatever save currently resolves. The live state is always
        /// written back into the dictionary first, so a mutation made under a resolvable key is
        /// never lost - not even when the following resolution fails.
        /// </summary>
        internal bool SwitchToCurrentSave()
        {
            if (_currentKey != null && _state != null)
                _states[_currentKey] = _state;

            if (!_tryResolveKey(out string key) || string.IsNullOrWhiteSpace(key))
            {
                _currentKey = null;
                _state = null;
                return false;
            }

            _currentKey = key;
            if (!_states.TryGetValue(key, out _state) || _state == null)
            {
                _state = new UpgradeSaveState();
                _states[key] = _state;
            }

            return true;
        }

        internal int RemoveKeys(IEnumerable<string> keys)
        {
            if (keys == null)
                return 0;

            var keySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    keySet.Add(key);
            }

            int removed = 0;
            foreach (string key in keySet)
            {
                if (_states.Remove(key))
                    removed++;
            }

            if (_currentKey != null && keySet.Contains(_currentKey))
            {
                _currentKey = null;
                _state = null;
            }

            return removed;
        }

        /// <summary>
        /// <see cref="UpgradeSaveState.RearmLguImports"/> for every save this store holds; the
        /// number of saves that changed and so need writing back.
        /// </summary>
        internal int RearmLguImports()
        {
            int rearmed = 0;
            foreach (UpgradeSaveState state in _states.Values)
            {
                if (state != null && state.RearmLguImports())
                    rearmed++;
            }

            return rearmed;
        }

        /// <summary>
        /// The level a record holds for this row, clamped to what the row currently sells. A
        /// <see cref="UpgradeRankRecord.UniqueOnly"/> read projects the full-native ranks this
        /// save already owns onto the variant's milestones: the credit is inherent to the read,
        /// so it is idempotent, cannot be consumed by a one-time marker, and never runs backwards
        /// (#435).
        /// </summary>
        internal int GetLevel(
            string upgradeId,
            int maxTier,
            UpgradeRankRecord record = UpgradeRankRecord.Full)
        {
            UpgradeSaveState state = Current;
            if (state == null || string.IsNullOrWhiteSpace(upgradeId))
                return 0;

            int level;
            if (record == UpgradeRankRecord.UniqueOnly)
            {
                state.UniqueLevels.TryGetValue(upgradeId, out level);
                if (NativeUpgradeFamilies.TryGet(upgradeId, out NativeUpgradeFamily family)
                    && family.UniqueVariant != null)
                {
                    state.Levels.TryGetValue(upgradeId, out int fullRank);
                    int projected = family.UniqueVariant.ProjectFullRank(fullRank);
                    if (projected > level)
                        level = projected;
                }
            }
            else
            {
                state.Levels.TryGetValue(upgradeId, out level);
            }

            return level < 0 ? 0 : level > maxTier ? maxTier : level;
        }
    }
}
