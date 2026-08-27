using GameNetcodeStuff;
using Y4NGZUpgrades.Interactive;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Reflection surface for other Y4NGZ plugins that need the held item put away for the
    /// duration of their own animated interaction (the Company payload cart, for example).
    /// Begin/End are reason-paired and refcounted, so overlapping callers cannot restore each
    /// other's stow.
    /// </summary>
    public static class HeldItemStowApi
    {
        public const int ProtocolVersion = 1;

        /// <summary>Stows whatever the player is holding. Returns false when nothing was stowed.</summary>
        public static bool BeginStow(PlayerControllerB player, string reason)
        {
            return HeldItemStowService.Begin(player, reason, force: true).IsActive;
        }

        public static void EndStow(PlayerControllerB player, string reason)
        {
            HeldItemStowService.End(player, reason);
        }

        public static bool IsStowed(PlayerControllerB player)
        {
            return HeldItemStowService.IsStowed(player);
        }

        /// <summary>
        /// Token-scoped stow, for a caller that opens and closes exactly one stow rather than a
        /// reason-paired pair — Better Armory's grenade throw (#267), whose local and remote
        /// routines can be in flight for the same player at once and must not release each other's.
        /// Returns 0 when nothing was stowed; every other member below ignores a 0 token.
        /// </summary>
        /// <param name="safetyTimeoutSeconds">Backstop after which the janitor restores the item.</param>
        /// <param name="disableItemActions">Suppress item input for the local player.</param>
        /// <param name="force">Stow whatever is held instead of consulting the stow predicate.</param>
        public static long BeginStowToken(
            PlayerControllerB player,
            string reason,
            float safetyTimeoutSeconds,
            bool disableItemActions,
            bool force)
        {
            return HeldItemStowService
                .Begin(player, reason, safetyTimeoutSeconds, disableItemActions, force)
                .Token;
        }

        /// <summary>Whether a token from <see cref="BeginStowToken"/> still holds the stow open.</summary>
        public static bool IsStowTokenLive(PlayerControllerB player, long token)
        {
            return HeldItemStowService.IsTokenLive(player, token);
        }

        /// <summary>Releases one token. Idempotent; the item returns on the last live token.</summary>
        public static void ReleaseStowToken(PlayerControllerB player, long token)
        {
            HeldItemStowService.Release(player, token);
        }
    }
}
