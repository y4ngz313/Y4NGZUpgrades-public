namespace Y4NGZUpgrades
{
    /// <summary>
    /// Unity-free eligibility rule for the last-survivor award. A disconnected or unresolved
    /// entrant is deliberately not counted as dead, preventing disconnects from manufacturing a
    /// clutch.
    /// </summary>
    public static class ClutchXpPolicy
    {
        public static bool IsEligible(
            bool localEnteredFacility,
            bool localExtractedAlive,
            int otherFacilityEntrants,
            int deadOtherFacilityEntrants)
        {
            return localEnteredFacility
                   && localExtractedAlive
                   && otherFacilityEntrants > 0
                   && deadOtherFacilityEntrants == otherFacilityEntrants;
        }
    }
}
