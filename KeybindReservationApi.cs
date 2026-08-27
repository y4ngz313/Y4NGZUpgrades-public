namespace Y4NGZUpgrades
{
    /// <summary>
    /// The keys Y4NGZUpgrades has already claimed, for another Y4NGZ plugin resolving its own
    /// keybind defaults (#267).
    ///
    /// Before the Better Armory split every ability key and the grenade key resolved against one
    /// shared set, in one pass, with the grenade key last. Splitting the plugins split that set;
    /// publishing it here puts it back together without either assembly referencing the other. A
    /// consumer reserves these names, then resolves its own keys, and a player who moves an ability
    /// onto the grenade key is bounced off it exactly as before.
    ///
    /// Names are <c>UnityEngine.InputSystem.Key</c> member names ("B", "Digit1"), so a consumer
    /// parses them with <c>Enum.TryParse</c> and never has to agree on an ordinal.
    /// </summary>
    public static class KeybindReservationApi
    {
        public const int ProtocolVersion = 1;

        /// <summary>
        /// Snapshot of the reserved keys. Empty before this plugin's Awake has bound its config,
        /// which a consumer that loads first will see — hence the soft dependency that orders it.
        /// </summary>
        public static string[] GetReservedKeyNames()
        {
            string[] reserved = Plugin.ReservedKeyNames;
            return (string[])(reserved ?? new string[0]).Clone();
        }
    }
}
