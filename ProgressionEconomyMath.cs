using System;
using System.Globalization;

namespace Y4NGZUpgrades
{
    public static class ProgressionEconomyMath
    {
        /// <summary>
        /// The one moon-risk scaling rule: ceiling, never negative, zero multiplier pays zero.
        /// Two buckets are risk-scaled — contract XP and body retrieval (#219) — and they must
        /// round identically, so both go through this.
        /// </summary>
        public static int ScaleRiskXp(int rawXp, float multiplier)
        {
            if (rawXp <= 0 || multiplier <= 0f)
                return 0;
            return Math.Max(0, (int)Math.Ceiling(rawXp * (double)multiplier));
        }

        public static int ScaleContractXp(int rawContractXp, float multiplier)
        {
            return ScaleRiskXp(rawContractXp, multiplier);
        }

        /// <summary>
        /// Splits an already risk-scaled bucket total back across the raw per-line amounts that
        /// fed it, with LARGEST-REMAINDER reconciliation (#220).
        ///
        /// The contract bucket is scaled ONCE, at finalize, for the whole round — that single
        /// ceiling is what the player is paid and what the XP bar shows. Reporting each line as
        /// its own <c>ceil(raw * multiplier)</c> would print a set of numbers that sums to more
        /// than the award, so the scaled total is the authority and the lines are divided out of
        /// it: floor each line's exact share, then hand the leftover units one at a time to the
        /// largest fractional remainders, ties going to the earlier line. The result therefore
        /// sums to <paramref name="scaledTotal"/> EXACTLY, for any multiplier and any line count.
        ///
        /// Lines whose raw amount is non-positive are paid nothing and take part in no tie: a
        /// zero-XP award has no share of the bucket to reconcile.
        ///
        /// Note the input must be EVERY line that fed the bucket, including awards that are never
        /// reported (the crew-wide contract outcome). Excluding them here would spread their
        /// share over the reported lines and overstate them.
        /// </summary>
        public static int[] DistributeScaledXp(int[] rawAmounts, int scaledTotal)
        {
            if (rawAmounts == null || rawAmounts.Length == 0)
                return Array.Empty<int>();

            var result = new int[rawAmounts.Length];
            if (scaledTotal <= 0)
                return result;

            long rawTotal = 0;
            for (int i = 0; i < rawAmounts.Length; i++)
            {
                if (rawAmounts[i] > 0)
                    rawTotal += rawAmounts[i];
            }

            if (rawTotal <= 0)
                return result;

            // Exact integer share: floor(raw * scaledTotal / rawTotal), remembering the remainder
            // so the leftover units go where the truncation hurt most.
            var remainders = new long[rawAmounts.Length];
            long assigned = 0;
            for (int i = 0; i < rawAmounts.Length; i++)
            {
                if (rawAmounts[i] <= 0)
                {
                    remainders[i] = -1;
                    continue;
                }

                long numerator = (long)rawAmounts[i] * scaledTotal;
                long share = numerator / rawTotal;
                remainders[i] = numerator % rawTotal;
                result[i] = share > int.MaxValue ? int.MaxValue : (int)share;
                assigned += result[i];
            }

            // Strictly fewer leftover units than contributing lines, so one pass of "give the
            // largest remainder a unit" per unit terminates without revisiting a line.
            long leftover = scaledTotal - assigned;
            while (leftover > 0)
            {
                int best = -1;
                for (int i = 0; i < remainders.Length; i++)
                {
                    if (remainders[i] < 0)
                        continue;
                    if (best < 0 || remainders[i] > remainders[best])
                        best = i;
                }

                if (best < 0)
                    break;

                result[best]++;
                remainders[best] = -1;
                leftover--;
            }

            return result;
        }

        public static int ComputeRoundAwardedXp(
            int discoveryXp,
            int deliveryXp,
            int combatXp,
            int survivalXp,
            int rawContractXp,
            float contractMultiplier)
        {
            return ComputeRoundAwardedXp(
                discoveryXp, deliveryXp, combatXp, survivalXp, rawContractXp, 0, contractMultiplier);
        }

        /// <summary>
        /// The round total. Discovery, delivery, combat, survival, mainframe and clutch awards
        /// are paid flat; the contract and body-retrieval buckets are each scaled ONCE by the
        /// moon risk multiplier.
        /// Body retrieval is deliberately its own bucket rather than contract XP: it must be
        /// risk-scaled, but it must not inherit the contract per-type multipliers or the
        /// once-per-round contract dedupe.
        /// </summary>
        public static int ComputeRoundAwardedXp(
            int discoveryXp,
            int deliveryXp,
            int combatXp,
            int survivalXp,
            int rawContractXp,
            int rawBodyRetrievalXp,
            float riskMultiplier)
        {
            return ComputeRoundAwardedXp(
                discoveryXp,
                deliveryXp,
                combatXp,
                survivalXp,
                mainframeHackXp: 0,
                clutchXp: 0,
                rawContractXp,
                rawBodyRetrievalXp,
                riskMultiplier);
        }

        public static int ComputeRoundAwardedXp(
            int discoveryXp,
            int deliveryXp,
            int combatXp,
            int survivalXp,
            int mainframeHackXp,
            int clutchXp,
            int rawContractXp,
            int rawBodyRetrievalXp,
            float riskMultiplier)
        {
            return ComputeRoundAwardedXp(
                discoveryXp,
                deliveryXp,
                combatXp,
                survivalXp,
                mainframeHackXp,
                clutchXp,
                shipBatteryXp: 0,
                rawContractXp,
                rawBodyRetrievalXp,
                riskMultiplier);
        }

        /// <summary>#459: the ship battery restore bucket is flat, like mainframe and clutch.</summary>
        public static int ComputeRoundAwardedXp(
            int discoveryXp,
            int deliveryXp,
            int combatXp,
            int survivalXp,
            int mainframeHackXp,
            int clutchXp,
            int shipBatteryXp,
            int rawContractXp,
            int rawBodyRetrievalXp,
            float riskMultiplier)
        {
            long total = Math.Max(0, discoveryXp)
                         + (long)Math.Max(0, deliveryXp)
                         + Math.Max(0, combatXp)
                         + Math.Max(0, survivalXp)
                         + Math.Max(0, mainframeHackXp)
                         + Math.Max(0, clutchXp)
                         + Math.Max(0, shipBatteryXp)
                         + ScaleRiskXp(rawContractXp, riskMultiplier)
                         + ScaleRiskXp(rawBodyRetrievalXp, riskMultiplier);
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        /// <summary>
        /// XP width of one rank, read from the earnable-rank width curve.
        /// <paramref name="rankWidths"/> is the configured per-level curve (30 entries by
        /// default); index 0 is rank 1's band.
        ///
        /// The final catalog rank is synthesized one XP wide - it is a terminal marker, not a
        /// band a player earns through - so any index at or past the end of the curve falls back
        /// to the last EARNABLE rank's width. A maxed player therefore keeps being paid the
        /// 2000-wide band's rate instead of collapsing to a 1 XP rank width.
        /// </summary>
        public static int GetRankWidth(int[] rankWidths, int rankIndex)
        {
            if (rankWidths == null || rankWidths.Length == 0)
                return 0;

            int index = rankIndex;
            if (index < 0)
                index = 0;
            if (index > rankWidths.Length - 1)
                index = rankWidths.Length - 1;

            return Math.Max(1, rankWidths[index]);
        }

        /// <summary>
        /// Rank index for an XP total, walked from the same width curve the bands are built from.
        /// Index 0 is the first earnable rank; an XP total at or past the end of the curve returns
        /// the terminal index (one past the last earnable rank), matching the catalog's synthesized
        /// final rank. Widths are floored at 1 exactly as the band builder floors them, so the walk
        /// cannot disagree with the bands about where a boundary sits.
        /// </summary>
        public static int GetRankIndex(int[] rankWidths, int xp)
        {
            if (rankWidths == null || rankWidths.Length == 0)
                return 0;

            int end = 0;
            for (int i = 0; i < rankWidths.Length; i++)
            {
                end += Math.Max(1, rankWidths[i]);
                if (xp < end)
                    return i;
            }

            return rankWidths.Length;
        }

        /// <summary>
        /// The contract-completion award: a percentage of the completing player's current rank
        /// width, scaled by a per-contract-type multiplier. A multiplier of zero is the
        /// "this type pays no completion bonus" case and must stay exactly zero.
        ///
        /// This is the BASE award. The moon risk multiplier is applied later, once, by
        /// <see cref="ScaleContractXp"/> at finalize, exactly like every other contract award.
        /// </summary>
        public static int ComputeContractCompletionXp(int rankWidth, float widthFraction, float typeMultiplier)
        {
            if (rankWidth <= 0 || widthFraction <= 0f || typeMultiplier <= 0f)
                return 0;

            // Saturating, like ComputeRoundAwardedXp: an absurd configured multiplier must cap the
            // award, not wrap an unchecked cast to int.MinValue and silently pay nothing.
            double award = Math.Round(rankWidth * (double)widthFraction * typeMultiplier, MidpointRounding.AwayFromZero);
            if (award <= 0d)
                return 0;
            return award >= int.MaxValue ? int.MaxValue : (int)award;
        }

        /// <summary>
        /// The share of one rank band a fraction key buys, rounded the same way the completion
        /// award rounds and saturating rather than wrapping on an absurd curve. Every #457 act
        /// price is built from this, so the act and the completion move together by construction.
        /// </summary>
        public static int ComputeRankWidthShare(int rankWidth, float widthFraction)
        {
            if (rankWidth <= 0 || widthFraction <= 0f)
                return 0;

            double share = Math.Round(rankWidth * (double)widthFraction, MidpointRounding.AwayFromZero);
            if (share <= 0d)
                return 0;
            return share >= int.MaxValue ? int.MaxValue : (int)share;
        }

        /// <summary>
        /// The price of one contract ACT (#457): the configured flat award, or the actor's rank
        /// band share when that is larger. The flat value is a FLOOR, never a ceiling — a low
        /// rank keeps the authored number, and a high rank is paid in proportion to the band it
        /// is climbing, which is what makes the player who did the act out-earn a bystander whose
        /// only income is the completion share.
        ///
        /// A flat award configured to zero is "this act is disabled" and stays zero: an operator
        /// who zeroed an award must not have it resurrected by their rank.
        ///
        /// Base XP, like every other contract award: the moon risk multiplier is applied once, at
        /// finalize, by <see cref="ScaleContractXp"/>.
        /// </summary>
        public static int ComputeContractActXp(int flatXp, int rankWidth, float widthFraction)
        {
            if (flatXp <= 0)
                return 0;
            return Math.Max(flatXp, ComputeRankWidthShare(rankWidth, widthFraction));
        }

        /// <summary>
        /// The Payload piloting ceiling (#457). Piloting pays per second, so rank scaling has to
        /// raise the CAP rather than the rate; the configured cap is the floor of that ceiling so
        /// a raised config value is never lowered by a low rank.
        /// </summary>
        public static int ComputePayloadPilotCap(int configuredCap, int rankWidth, float widthFraction)
        {
            if (configuredCap <= 0)
                return 0;
            return Math.Max(configuredCap, ComputeRankWidthShare(rankWidth, widthFraction));
        }

        /// <summary>
        /// Whether the local player has any claim on the crew-wide contract completion award
        /// (#457). Company fans the outcome out to every member of the crew, including one who
        /// joined late or never left the ship; taking part means either entering the facility or
        /// earning contract act XP this round. Act XP alone qualifies because several objectives
        /// (Payload piloting, the surface half of a Survey) are earned without an entrance
        /// crossing.
        /// </summary>
        public static bool IsEligibleForContractCompletionXp(bool enteredFacility, bool earnedContractActXp)
        {
            return enteredFacility || earnedContractActXp;
        }

        /// <summary>
        /// The award for one occurrence of a REPEATABLE sub-objective that is capped by count
        /// rather than by total XP (#194: extra Pest Control captures, whose trap is re-armable).
        ///
        /// <paramref name="creditsAlreadyPaid"/> counts only occurrences that actually paid, so a
        /// duplicate event id -- which the caller rejects before it gets here -- can never consume
        /// a slot. A cap at or below zero means the award is disabled, NOT unlimited: a count cap
        /// exists to bound farming, so the degenerate configuration must be the safe one.
        /// </summary>
        public static int ComputeCappedRepeatXp(int creditsAlreadyPaid, int maxCredits, int xpPerCredit)
        {
            if (xpPerCredit <= 0 || maxCredits <= 0)
                return 0;
            return Math.Max(0, creditsAlreadyPaid) >= maxCredits ? 0 : xpPerCredit;
        }

        public static int ComputePayloadPilotXp(float seconds, float xpPerSecond, int cap)
        {
            if (seconds <= 0f || xpPerSecond <= 0f || cap <= 0)
                return 0;
            int earned = (int)Math.Floor(seconds * (double)xpPerSecond);
            return Math.Min(Math.Max(0, cap), Math.Max(0, earned));
        }

        public static int GetTokenGrantForRankIndex(
            int rankIndex,
            int earnableRankCount,
            int ranks1To5,
            int ranks6To10,
            int ranks11To19,
            int ranks20To30)
        {
            if (rankIndex <= 0 || rankIndex > earnableRankCount)
                return 0;
            if (rankIndex <= 5)
                return Math.Max(0, ranks1To5);
            if (rankIndex <= 10)
                return Math.Max(0, ranks6To10);
            if (rankIndex <= 19)
                return Math.Max(0, ranks11To19);
            return Math.Max(0, ranks20To30);
        }

        /// <summary>
        /// Tokens granted across an inclusive rank range. An empty range - notably "everything
        /// still to come" asked at the final rank, where first is one past last - pays nothing:
        /// clamping the start back into range would re-pay a grant the player already banked.
        /// </summary>
        public static int GetTokenGrantForRankRange(
            int firstRankIndex,
            int finalRankIndex,
            int earnableRankCount,
            int ranks1To5,
            int ranks6To10,
            int ranks11To19,
            int ranks20To30)
        {
            if (firstRankIndex > finalRankIndex)
                return 0;

            int first = Math.Max(1, firstRankIndex);
            int final = Math.Min(finalRankIndex, earnableRankCount);
            int total = 0;
            for (int rank = first; rank <= final; rank++)
            {
                total += GetTokenGrantForRankIndex(
                    rank, earnableRankCount, ranks1To5, ranks6To10, ranks11To19, ranks20To30);
            }

            return total;
        }

        public static bool TryParsePositiveIntCsv(string value, int expectedCount, out int[] result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(value) || expectedCount < 0)
                return false;

            string[] parts = value.Split(',');
            if (parts.Length != expectedCount)
                return false;

            var parsed = new int[expectedCount];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed[i])
                    || parsed[i] <= 0)
                {
                    return false;
                }
            }

            result = parsed;
            return true;
        }
    }
}
