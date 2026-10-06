using System.Collections.Generic;

namespace Y4NGZUpgrades.Effects
{
    // Several cloaked players can briefly own the same visual during a hand-off. Only the last
    // release restores its pre-cloak state; a later owner must not capture 'already hidden' as
    // the baseline and strand the item invisible after both cloaks end.
    internal sealed class CloakOverrideLease<T> where T : class
    {
        private sealed class Entry { internal bool Original; internal int Count; }
        private readonly Dictionary<T, Entry> _entries = new Dictionary<T, Entry>();
        internal void Acquire(T key, bool original)
        {
            if (!_entries.TryGetValue(key, out Entry entry))
                _entries[key] = entry = new Entry { Original = original };
            entry.Count++;
        }
        internal bool Release(T key, out bool original)
        {
            original = false;
            if (!_entries.TryGetValue(key, out Entry entry)) return false;
            original = entry.Original;
            if (--entry.Count > 0) return false;
            _entries.Remove(key);
            return true;
        }
    }
}
