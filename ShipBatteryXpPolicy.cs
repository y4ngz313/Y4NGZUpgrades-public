namespace Y4NGZUpgrades
{
    /// <summary>
    /// Unity-free rules for the Ship Systems battery-restore award (#459).
    ///
    /// The instance is the HOST's break latch: who caused the most recent battery break. A
    /// restore is credited only when ship power was down before it and the restorer is not the
    /// player whose own hit caused that break, so breaking the battery and docking a spare
    /// cannot farm XP. The latch describes one break episode: every restore ends the episode,
    /// a break with no player behind it (an enemy's sabotage, an explosion) replaces it with
    /// "nobody", and a round reset clears it unless the battery is still broken - a break the
    /// crew carried into the next round is still that player's break.
    ///
    /// The per-round cap is the ACTOR's rule, applied on the actor's own machine with the
    /// actor's own config like every other capped XP source.
    /// </summary>
    public sealed class ShipBatteryXpPolicy
    {
        private bool _hasPlayerBreaker;
        private ulong _breakerClientId;

        public void RecordPlayerBreak(ulong breakerClientId)
        {
            _hasPlayerBreaker = true;
            _breakerClientId = breakerClientId;
        }

        public void RecordNonPlayerBreak()
        {
            _hasPlayerBreaker = false;
            _breakerClientId = 0;
        }

        /// <summary>
        /// Evaluates one restore and closes the break episode it ended, whether or not it paid.
        /// </summary>
        public bool TryCreditRestore(bool powerWasDown, ulong restorerClientId)
        {
            bool selfInflicted = _hasPlayerBreaker && _breakerClientId == restorerClientId;
            RecordNonPlayerBreak();
            return powerWasDown && !selfInflicted;
        }

        public void ResetRound(bool batteryStillBroken)
        {
            if (!batteryStillBroken)
                RecordNonPlayerBreak();
        }

        public void ResetSession()
        {
            RecordNonPlayerBreak();
        }

        /// <summary>Actor side: <paramref name="paidThisRound"/> restores already paid this round.</summary>
        public static bool IsUnderRoundCap(int paidThisRound, int maxPerRound)
        {
            return maxPerRound > 0 && paidThisRound >= 0 && paidThisRound < maxPerRound;
        }

        /// <summary>
        /// Actor side: the amount one restore pays, or zero when the cap or a zero-XP setting
        /// refuses it. The caller counts a restore against the cap only when this is positive.
        /// </summary>
        public static int ComputeRestoreXp(bool usedApparatus, int xpPerReplace, int xpPerApparatusDock, int paidThisRound, int maxPerRound)
        {
            if (!IsUnderRoundCap(paidThisRound, maxPerRound))
                return 0;

            int amount = usedApparatus ? xpPerApparatusDock : xpPerReplace;
            return amount > 0 ? amount : 0;
        }
    }
}
