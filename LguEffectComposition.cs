namespace Y4NGZUpgrades
{
    /// <summary>
    /// The arithmetic that decides how this mod's own player effects compose with an external
    /// provider that touches the same vanilla quantity - in practice Lategame Upgrades (LGU).
    ///
    /// Everything here is pure: no Unity, no Harmony, no reflection, no statics. The reflection
    /// and gating live in <see cref="LguEffectCompatibility"/>; this file exists so the composition
    /// rules can be exercised deterministically by <c>verification/LguCoexistenceChecks</c> instead
    /// of only inside a running game.
    ///
    /// Every function is written so that the "no external provider" argument values reproduce the
    /// pre-existing behaviour exactly, which is what keeps an install without LGU unchanged.
    ///
    /// It also holds the rank ladders of the native rows whose effects share those quantities
    /// (Sprinter, Surefooted, Resilience), so the checks drive the same thresholds the patches
    /// apply rather than a copy of them.
    /// </summary>
    internal static class LguEffectComposition
    {
        /// <summary>
        /// Resolves the un-bonused base of a stat this mod writes absolutely every frame.
        ///
        /// Without an external writer (<paramref name="reconcileExternalWrites"/> false) this is the
        /// original F-GHOST-6 / F-GHOST-16 rule: the base is a snapshot that is only refreshed while
        /// we are applying nothing, so neither our own <c>Mathf.Max</c> floors nor another mod's
        /// absolute write can be folded into the stored bonus and stick there.
        ///
        /// With one (LGU's Sick Beats does <c>player.movementSpeed += boost</c> from an
        /// <c>Update</c> postfix and <c>-= boost</c> when the music stops) that rule is wrong in
        /// both directions: while we hold any bonus the snapshot is stale, so our write erases the
        /// boost, and a boost that is added and later removed across a purchase leaves its value
        /// latched into our base forever. The fix is to compare the live value against what we
        /// ourselves last wrote. The difference is by definition someone else's, so folding it into
        /// the base carries it instead of fighting it, and our own writes - floors included - always
        /// produce a difference of zero and can never poison the base.
        /// </summary>
        /// <param name="storedBase">Base carried from the previous frame.</param>
        /// <param name="lastWritten">Exact value this mod last assigned to the stat.</param>
        /// <param name="currentValue">Live value of the stat right now.</param>
        /// <param name="appliedBonus">Bonus this mod had applied when it last wrote.</param>
        /// <param name="reconcileExternalWrites">True when an additive external writer is present.</param>
        internal static float ReconcileBase(
            float storedBase,
            float lastWritten,
            float currentValue,
            float appliedBonus,
            bool reconcileExternalWrites)
        {
            // Applying nothing means the live value IS the base, whoever wrote it last.
            if (appliedBonus == 0f)
                return currentValue;

            if (!reconcileExternalWrites)
                return storedBase;

            float external = currentValue - lastWritten;
            return external == 0f ? storedBase : storedBase + external;
        }

        /// <summary>
        /// Extra stamina the Sprinter line adds on top of the recovery vanilla already applied this
        /// frame (F-GHOST-2 / F-GHOST-11).
        ///
        /// Vanilla recovers <c>deltaTime / (sprintTime + offset) * factors</c>, so our own +6 s
        /// sprint time makes the bar refill slower; the compensation ratio undoes exactly that much
        /// before the advertised multiplier is applied. <paramref name="effectiveSprintTime"/> has
        /// to be the sprint time the game's own denominator used, which is not the field value when
        /// LGU's Bigger Lungs is installed: its transpiler replaces every <c>sprintTime</c> read in
        /// <c>LateUpdate</c> with <c>GetAdditionalStaminaTime(sprintTime)</c>. Passing the raw field
        /// there overstates the ratio and hands out more than the promised 5%.
        ///
        /// Every multiplicative factor in the recovery (drunkness, Sick Beats, Bigger Lungs' own
        /// regen multiplier) cancels in the ratio because the recovery is measured, not re-derived.
        /// </summary>
        internal static float StaminaRegenBonus(
            float recovered,
            float effectiveSprintTime,
            float ownSprintTimeBonus,
            float denominatorOffset,
            float advertisedMultiplier)
        {
            if (recovered <= 0f)
                return 0f;

            float withoutOwnBonus = effectiveSprintTime - ownSprintTimeBonus;
            if (withoutOwnBonus < 0.1f)
                withoutOwnBonus = 0.1f;

            float compensation = (effectiveSprintTime + denominatorOffset) / (withoutOwnBonus + denominatorOffset);
            float bonus = recovered * (compensation * advertisedMultiplier - 1f);
            return bonus > 0f ? bonus : 0f;
        }

        /// <summary>
        /// Whether Resilience's bonus health regeneration may tick this frame.
        ///
        /// F-ENF-6: vanilla already regenerates on its own timer below its cap, so ticking inside
        /// that band healed at twice the advertised rate. <paramref name="externalRegenFloor"/> is
        /// that cap as the game actually evaluates it - LGU's Medical Nanobots replaces vanilla's
        /// literal with <c>GetIncreasedHealthRegeneration(20)</c>, which can raise it above our own
        /// ceiling. When it does, the band is empty and we contribute nothing, which is correct:
        /// the external regeneration already covers everything ours would have.
        /// </summary>
        internal static bool ShouldTickBonusHealthRegen(int health, int externalRegenFloor, int ownRegenCap)
        {
            return health >= externalRegenFloor && health < ownRegenCap;
        }

        /// <summary>
        /// Health after a change in this mod's own maximum (F-ENF-11).
        ///
        /// Buying into Resilience tops the player up to the new ceiling; a respec downwards is the
        /// caller's clamp to deal with, not this function's. The result never lowers health, which
        /// matters once an external provider can legitimately hold the player above our ceiling -
        /// LGU's Stimpack adds flat health on purchase and restores it through its own
        /// <c>ReviveDeadPlayers</c> transpiler. Without such a provider health can never exceed
        /// <paramref name="storedOwnMax"/>, so the guard is inert and the result is the top-up.
        /// </summary>
        internal static int ResolveHealthTopUp(int health, int storedOwnMax, int nextOwnMax, int vanillaMax)
        {
            int previous = storedOwnMax > vanillaMax ? storedOwnMax : vanillaMax;
            if (nextOwnMax <= previous)
                return health;

            int threshold = vanillaMax < previous ? vanillaMax : previous;
            if (health < threshold)
                return health;

            return health > nextOwnMax ? health : nextOwnMax;
        }

        /// <summary>
        /// One carried item's contribution to the Transporter carry-weight recomputation.
        ///
        /// F-ENF-8 keeps the signed <c>weight - 1</c> so a sub-1.0 item is a benefit rather than
        /// merely free. LGU's Back Muscles, in its reduce-weight mode, scales the same quantity and
        /// floors it at zero, which would throw that benefit away - so the external reduction only
        /// applies to an item that actually costs something.
        /// </summary>
        internal static float ComposeWeightPenalty(float rawPenalty, float externallyReducedPenalty)
        {
            return rawPenalty > 0f ? externallyReducedPenalty : rawPenalty;
        }

        // LGU grants health locally before its owner-rank RPC comes back. The old
        // published rank is not a valid ceiling during either a purchase or removal.
        internal static bool IsHealthRankSynchronized(
            bool active, int localLevel, bool published, int publishedLevel)
        {
            return active ? published && localLevel == publishedLevel : !published;
        }

        /// <summary>
        /// Whether LGU's live effect state may be read right now (#493). Integrated into the
        /// player menu, the bridge's readiness decides: its load reconciliation has to settle the
        /// token-owned ranks first. Not integrated, LGU owns and sells every rank itself, so its
        /// own runtime being live is enough - the same state LGU's rewritten game reads use.
        /// Absent, nothing is read.
        /// </summary>
        internal static bool CanReadLiveEffects(bool lguPresent, bool integrated, bool bridgeReady, bool lguRuntimeLive)
        {
            if (!lguPresent)
                return false;
            return integrated ? bridgeReady : lguRuntimeLive;
        }

        /// <summary>
        /// Health at or above which vanilla's own regeneration has stopped. Without LGU it is
        /// vanilla's floor. With LGU it is Medical Nanobots' raised cap, never below vanilla's;
        /// when that cannot be read (<paramref name="lguFloor"/> null) the floor is unreachable, which
        /// stands Resilience's bonus tick down rather than risk healing twice.
        /// </summary>
        internal static int HealthRegenFloor(int vanillaFloor, bool lguPresent, bool readable, int? lguFloor)
        {
            if (!lguPresent)
                return vanillaFloor;
            if (!readable || !lguFloor.HasValue)
                return int.MaxValue;
            return lguFloor.Value < vanillaFloor ? vanillaFloor : lguFloor.Value;
        }

        // - Native ladders (#435) -
        //
        // Every ladder takes the rank its accessor resolved, which is already 0 whenever the row
        // does not sell its full self. A rank above the authored count reads as the top rank.

        internal const int SprinterMovementRank = 1;
        internal const int SprinterStaminaRank = 2;
        internal const int SprinterQuietRank = 3;
        internal const float SprinterMovementSpeedGain = 1.4f;
        internal const float SprinterSprintTimeGain = 6f;
        internal const float SprinterCrouchSpeedFraction = 0.10f;

        internal const int SurefootedRanks = 3;
        internal const float SurefootedClimbFractionPerRank = 0.20f;
        internal const float SurefootedTractionFraction = 0.25f;
        internal const float SurefootedJumpForceRankTwo = 3f;
        internal const float SurefootedJumpForceRankThree = 3.75f;
        internal const float SurefootedUphillFraction = 0.25f;
        internal const float SurefootedFallDamageMultiplier = 0.75f;

        internal const int ResilienceRanks = 3;
        internal const int ResilienceHealthPerRank = 20;
        internal const int ResilienceRegenCapRank = 2;
        internal const int ResilienceRegenCapGain = 10;

        /// <summary>Sprinter rank 1: flat movement speed.</summary>
        internal static float SprinterMovementSpeedBonus(int rank)
        {
            return rank >= SprinterMovementRank ? SprinterMovementSpeedGain : 0f;
        }

        /// <summary>Sprinter rank 2: sprint time, paired with the compensated stamina regen.</summary>
        internal static float SprinterSprintTimeBonus(int rank)
        {
            return rank >= SprinterStaminaRank ? SprinterSprintTimeGain : 0f;
        }

        internal static bool SprinterRegeneratesStamina(int rank)
        {
            return rank >= SprinterStaminaRank;
        }

        /// <summary>Sprinter rank 3: fraction of the crouching base speed added back.</summary>
        internal static float SprinterCrouchSpeedGain(int rank)
        {
            return rank >= SprinterQuietRank ? SprinterCrouchSpeedFraction : 0f;
        }

        /// <summary>Sprinter rank 3: the footstep noise-range reduction, locally and host-side.</summary>
        internal static bool SprinterQuietensFootsteps(int rank)
        {
            return rank >= SprinterQuietRank;
        }

        /// <summary>Surefooted: ladder speed +20% per rank, 60% at rank 3.</summary>
        internal static float SurefootedClimbSpeedGain(int rank)
        {
            if (rank <= 0)
                return 0f;

            return SurefootedClimbFractionPerRank * (rank < SurefootedRanks ? rank : SurefootedRanks);
        }

        /// <summary>Surefooted rank 1: traction.</summary>
        internal static float SurefootedTractionGain(int rank)
        {
            return rank >= 1 ? SurefootedTractionFraction : 0f;
        }

        /// <summary>Surefooted: jump force +3 at rank 2, +3.75 in total at rank 3.</summary>
        internal static float SurefootedJumpForceBonus(int rank)
        {
            if (rank >= 3)
                return SurefootedJumpForceRankThree;

            return rank >= 2 ? SurefootedJumpForceRankTwo : 0f;
        }

        /// <summary>Surefooted rank 2: fraction of the uphill slowdown removed.</summary>
        internal static float SurefootedUphillReduction(int rank)
        {
            return rank >= 2 ? SurefootedUphillFraction : 0f;
        }

        /// <summary>Surefooted rank 3: fall damage scale.</summary>
        internal static float SurefootedFallDamageScale(int rank)
        {
            return rank >= 3 ? SurefootedFallDamageMultiplier : 1f;
        }

        /// <summary>Resilience: 120/140/160 over <paramref name="vanillaMax"/> 100.</summary>
        internal static int ResilienceMaxHealth(int rank, int vanillaMax)
        {
            if (rank <= 0)
                return vanillaMax;

            return vanillaMax + ResilienceHealthPerRank * (rank < ResilienceRanks ? rank : ResilienceRanks);
        }

        /// <summary>
        /// Top of Resilience's own recovery band: vanilla's cap below rank 2, ten above it from
        /// rank 2. The band ticks a fixed 1 HP per second and is never scaled by another
        /// provider's per-tick healing.
        /// </summary>
        internal static int ResilienceRegenCap(int rank, int vanillaRegenCap)
        {
            return rank >= ResilienceRegenCapRank ? vanillaRegenCap + ResilienceRegenCapGain : vanillaRegenCap;
        }
    }
}
