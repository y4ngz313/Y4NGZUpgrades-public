using UnityEngine;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// The one definition of "which object is this" used by every per-round ledger
    /// (progression delivery attribution and body retrieval, employee statistics). Both ledgers
    /// used to carry their own copy of this logic and drifted apart; a key that means different
    /// things in two ledgers is a silent attribution bug, so there is exactly one implementation
    /// now, and it is the whole of what they share.
    ///
    /// Scope of that claim, stated precisely: the KEY is unified — the same object passed to this
    /// method yields the same number in both ledgers, corpses included, since #219 keys a body as
    /// the spawned object it is (<c>EmployeeStatistics.NetworkKey</c> calls straight through to
    /// here). What each ledger DOES with a key is its own business, and their ATTRIBUTION models
    /// are not the same: employee statistics still uses the pre-#215 GrabItem model, which is
    /// knowingly wrong across a crew and is issue #223.
    ///
    /// The key number space lives in the Unity-free <c>NetworkObjectKeySpace.cs</c> half of this
    /// class, where the checks can compile it.
    /// </summary>
    internal static partial class NetworkObjectKey
    {
        internal static ulong For(Component component)
        {
            if (component == null)
                return 0;

            try
            {
                // IsSpawned matters as much as the null check: a NetworkObject that exists but
                // has not been spawned reports NetworkObjectId 0, so every such object would
                // alias onto the same key, and its key would change the moment it spawned --
                // making a round-start snapshot entry unfindable at finalize.
                if (component is Unity.Netcode.NetworkBehaviour behaviour
                    && behaviour.NetworkObject != null
                    && behaviour.NetworkObject.IsSpawned
                    && behaviour.NetworkObjectId != 0)
                {
                    return behaviour.NetworkObjectId;
                }
            }
            catch
            {
            }

            return LocalInstanceKeyFlag | unchecked((uint)component.GetInstanceID());
        }
    }
}
