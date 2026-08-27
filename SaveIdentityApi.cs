using System;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Reflection surface for other Y4NGZ plugins that persist per-save sidecar state next to this
    /// one (#266).
    ///
    /// Save identity is a networked concern: a client joined to a remote host must persist under the
    /// HOST's identity, which <see cref="HostSaveIdentity"/> negotiates over its own named messages.
    /// A second plugin cannot re-derive that without registering the same messages twice, so this
    /// exposes the resolved answer and the two moments a sidecar owner has to react to:
    ///
    /// <list type="bullet">
    /// <item><see cref="StoresRepointed"/> — the live save identity changed; re-read from the new
    /// key before anything else writes.</item>
    /// <item><see cref="SaveKeyDeleted"/> — a managed save identity was removed; drop the matching
    /// sidecar. Raised once per key AND once per bare slot name, matching what this plugin's own
    /// sidecars do.</item>
    /// </list>
    ///
    /// Better Armory's ammo reserve is the first consumer; it kept the
    /// <c>BepInEx/config/Y4NGZAmmoReserve</c> folder name across the split for save continuity.
    /// </summary>
    public static class SaveIdentityApi
    {
        public const int ProtocolVersion = 1;

        /// <summary>Raised after the live save identity has been re-pointed.</summary>
        public static event Action StoresRepointed;

        /// <summary>Raised once per save key (and per bare slot name) being deleted.</summary>
        public static event Action<string> SaveKeyDeleted;

        /// <summary>
        /// The current save key, creating a save identity if the slot exists without one. Null when
        /// no save is addressable yet (no slot chosen, or a host identity still pending).
        /// </summary>
        public static string GetCurrentSaveKey()
        {
            return SaveKey.TryGetCurrent(out string key) ? key : null;
        }

        /// <summary>
        /// The current save key WITHOUT creating an identity, falling back to the selected slot
        /// name. This is the read-side key: it must never mint a save identity on a plain read.
        /// </summary>
        public static string GetCurrentExistingOrSlotSaveKey()
        {
            return SaveKey.TryGetCurrentExistingOrSlot(out string key) ? key : null;
        }

        internal static void RaiseStoresRepointed()
        {
            Raise(StoresRepointed);
        }

        internal static void RaiseSaveKeyDeleted(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            Action<string> handlers = SaveKeyDeleted;
            if (handlers == null)
                return;

            Delegate[] list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<string>)list[i])(key);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[SaveIdentityApi] SaveKeyDeleted subscriber threw: {ex.Message}");
                }
            }
        }

        private static void Raise(Action handlers)
        {
            if (handlers == null)
                return;

            Delegate[] list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action)list[i])();
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[SaveIdentityApi] StoresRepointed subscriber threw: {ex.Message}");
                }
            }
        }
    }
}
