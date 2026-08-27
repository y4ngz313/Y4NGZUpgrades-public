using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Resolves the save key the upgrade state must be persisted under. Returns false while no
    /// save is addressable (fresh slot, or a client whose host identity has not arrived yet).
    /// </summary>
    internal delegate bool TryResolveSaveKeyDelegate(out string key);

    internal sealed class UpgradeSaveState
    {
        internal readonly Dictionary<string, int> Levels =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal bool ImportedLegacyLevels;
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

        internal int GetLevel(string upgradeId, int maxTier)
        {
            UpgradeSaveState state = Current;
            if (state == null || string.IsNullOrWhiteSpace(upgradeId))
                return 0;

            int level = state.Levels.TryGetValue(upgradeId, out int value) ? value : 0;
            return level < 0 ? 0 : level > maxTier ? maxTier : level;
        }
    }
}
