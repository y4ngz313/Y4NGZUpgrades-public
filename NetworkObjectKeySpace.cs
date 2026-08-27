namespace Y4NGZUpgrades
{
    /// <summary>
    /// The Unity-free half of <see cref="NetworkObjectKey"/>: the key number space itself.
    ///
    /// Split out so the deterministic checks can compile it and assert that the two key families
    /// — spawned network ids and local instance ids — cannot collide. They are all one
    /// <c>ulong</c> in one dictionary, so an overlap between them is not a crash: it is two
    /// different objects silently sharing one attribution record, which is the exact class of bug
    /// the ledger exists to prevent.
    ///
    /// There is deliberately no third family for corpses (#219). Bodies are keyed as the objects
    /// they are, through <see cref="NetworkObjectKey.For"/>, because a corpse IS a spawned object
    /// and its identity has to be the instance rather than the player it belonged to.
    /// </summary>
    internal static partial class NetworkObjectKey
    {
        /// <summary>
        /// Set on keys derived from a Unity instance ID rather than a network id. Instance IDs
        /// live in a different number space than NetworkObjectIds (which count up from zero),
        /// so without the tag a negative instance ID sign-extends into a huge value and a
        /// positive one can collide head-on with a real network id, aliasing two different
        /// objects onto one ledger entry.
        /// </summary>
        internal const ulong LocalInstanceKeyFlag = 0x8000000000000000UL;

        /// <summary>True for a key derived from a Unity instance ID rather than a network id.</summary>
        internal static bool IsLocalInstanceKey(ulong key)
        {
            return (key & LocalInstanceKeyFlag) != 0;
        }
    }
}
