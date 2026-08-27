using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Keeps one process-lifetime identity for a hosted save slot before vanilla has written its
    /// ES3 file. The caller owns the authority check; this type only guarantees stable reservation,
    /// promotion cleanup, and case-insensitive slot matching.
    /// </summary>
    internal sealed class PendingSaveIdentityStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _identities =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal bool TryGet(string saveFileName, out string identity)
        {
            identity = null;
            if (!TryNormalize(saveFileName, out string normalized))
                return false;

            lock (_gate)
                return _identities.TryGetValue(normalized, out identity);
        }

        internal bool TryGetOrCreate(string saveFileName, out string identity)
        {
            identity = null;
            if (!TryNormalize(saveFileName, out string normalized))
                return false;

            lock (_gate)
            {
                if (_identities.TryGetValue(normalized, out identity))
                    return true;

                identity = Guid.NewGuid().ToString("N");
                _identities[normalized] = identity;
                return true;
            }
        }

        internal void MarkPersisted(string saveFileName)
        {
            if (!TryNormalize(saveFileName, out string normalized))
                return;

            lock (_gate)
                _identities.Remove(normalized);
        }

        internal void Clear()
        {
            lock (_gate)
                _identities.Clear();
        }

        private static bool TryNormalize(string saveFileName, out string normalized)
        {
            normalized = saveFileName?.Trim();
            if (!string.IsNullOrEmpty(normalized))
                return true;

            normalized = null;
            return false;
        }
    }
}
