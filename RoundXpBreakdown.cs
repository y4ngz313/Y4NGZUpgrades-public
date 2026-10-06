using UnityEngine;

namespace Y4NGZUpgrades
{
    public sealed class RoundXpBreakdown
    {
        /// <summary>
        /// Monotonic round number this breakdown was produced for; 0 means "unknown"
        /// (an empty/disabled breakdown). The XP bar keys its duplicate-presentation
        /// suppression on this rather than object identity, so a round whose presentation
        /// was missed can never suppress a later round's.
        /// </summary>
        public int RoundSequence;
        public int OldXp;
        public int NewXp;
        public int ScrapPickupXp;
        public int ScrapDeliveredXp;
        public int MonsterKillXp;
        public int SurvivalXp;
        public int MainframeHackXp;
        public int ClutchXp;
        /// <summary>Ship Systems battery restores (#459), flat.</summary>
        public int ShipBatteryXp;
        public int ContractXp;
        public int ContractXpAwarded;
        /// <summary>
        /// Base XP for teammates' bodies the local player carried aboard (#219), before the
        /// moon risk multiplier. Its own bucket rather than part of ContractXp: it is
        /// risk-scaled like contract XP, but it must not pick up the per-contract-type
        /// multipliers or the once-per-round contract dedupe.
        /// </summary>
        public int BodyRetrievalXp;
        /// <summary>Body-retrieval XP after the moon risk multiplier; this is what was paid.</summary>
        public int BodyRetrievalXpAwarded;
        public float MoonMultiplier = 1f;
        public string MoonRisk = "C";
        public int RawTotal;
        public int AwardedTotal;
        public int OldRankIndex;
        public int NewRankIndex;
        public int RanksGained;
        public int CurrencyGranted;

        public int PositiveRawTotal =>
            Mathf.Max(0, ScrapPickupXp)
            + Mathf.Max(0, ScrapDeliveredXp)
            + Mathf.Max(0, MonsterKillXp)
            + Mathf.Max(0, SurvivalXp)
            + Mathf.Max(0, MainframeHackXp)
            + Mathf.Max(0, ClutchXp)
            + Mathf.Max(0, ShipBatteryXp)
            + Mathf.Max(0, ContractXp)
            + Mathf.Max(0, BodyRetrievalXp);

        public int PositiveAwardedTotal =>
            Mathf.Max(0, ScrapPickupXp)
            + Mathf.Max(0, ScrapDeliveredXp)
            + Mathf.Max(0, MonsterKillXp)
            + Mathf.Max(0, SurvivalXp)
            + Mathf.Max(0, MainframeHackXp)
            + Mathf.Max(0, ClutchXp)
            + Mathf.Max(0, ShipBatteryXp)
            + Mathf.Max(0, ContractXpAwarded)
            + Mathf.Max(0, BodyRetrievalXpAwarded);

        public RoundXpBreakdown Clone()
        {
            return (RoundXpBreakdown)MemberwiseClone();
        }
    }
}
