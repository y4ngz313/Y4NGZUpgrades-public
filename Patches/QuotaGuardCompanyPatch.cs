using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Quota Guard ("Veteran", id quota_guard) Y4NGZCompany integration.
    ///
    /// This is an OPPORTUNISTIC bonus on top of tier 1, not the tier itself (#365):
    /// when Y4NGZCompany is installed it also caps that mod's crew-death quota
    /// penalty at 10% of the pre-penalty quota per round-end. The tier-1 headline
    /// - halving the vanilla end-of-round credit fine for dead crew - is vanilla-only
    /// and lives in <see cref="QuotaGuardDeathPenaltyPatch"/>. Tier 2 extends the
    /// vanilla quota deadline to 4 days (see <see cref="QuotaGuardDeadlinePatch"/>).
    ///
    /// The effective tier is the MAX tier across all connected crew members ("any
    /// crew member owns it"), resolved via <see cref="UpgradeTierSync"/>.
    /// </summary>
    internal static class QuotaGuardCompanyPatch
    {
        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized)
                return;

            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.y4ngz.company"))
            {
                // Not a degradation of the tier: the vanilla credit-penalty relief in
                // QuotaGuardDeathPenaltyPatch is the tier-1 effect and is always active.
                Plugin.Log?.LogInfo("QuotaGuard: Y4NGZCompany not present; the optional quota-penalty cap bonus is inactive.");
                return;
            }

            try
            {
                InstallProvider();
                _initialized = true;
                Plugin.Log?.LogInfo("QuotaGuard: death-penalty fraction modifier installed.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"QuotaGuard: failed to install death-penalty fraction modifier: {e.Message}");
            }
        }

        // Reflection rather than a direct type reference: QuotaOverhaulRules is an
        // INTERNAL Y4NGZCompany type (unlike the public CctvSupportApi that
        // ChameleonCompanyPatch binds against), so only its public static
        // DeathPenaltyFractionModifier field is reachable from this assembly.
        private static void InstallProvider()
        {
            Type rules = Type.GetType("Y4NGZCompany.Contracts._Shared.QuotaOverhaulRules, Y4NGZCompany", throwOnError: false);
            if (rules == null)
                throw new InvalidOperationException("Y4NGZCompany.Contracts._Shared.QuotaOverhaulRules not found");

            FieldInfo field = rules.GetField("DeathPenaltyFractionModifier", BindingFlags.Public | BindingFlags.Static);
            if (field == null)
                throw new InvalidOperationException("QuotaOverhaulRules.DeathPenaltyFractionModifier not found (Y4NGZCompany too old?)");

            field.SetValue(null, (Func<float, float>)ModifyDeathPenaltyFraction);
        }

        // Runs on the host inside the Company round-end quota penalty. The Company
        // side clamps the returned value to [0, original], so this only ever lowers
        // the penalty.
        private static float ModifyDeathPenaltyFraction(float fraction)
        {
            if (GetCrewEffectiveTier() >= 1)
                return Math.Min(fraction, QuotaGuardUpgrade.DEATH_PENALTY_FRACTION_CAP);

            return fraction;
        }

        /// <summary>
        /// MAX quota_guard tier across every connected, controlled player.
        /// <see cref="UpgradeTierSync.GetTier"/> reads the local player's tier
        /// directly and remote tiers from the synced table.
        /// </summary>
        internal static int GetCrewEffectiveTier()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return QuotaGuardUpgrade.GetTier();

            int max = 0;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || !player.isPlayerControlled)
                    continue;

                int tier = UpgradeTierSync.GetTier(player.actualClientId, QuotaGuardUpgrade.UPGRADE_ID);
                if (tier > max)
                    max = tier;
            }

            return max;
        }
    }

    /// <summary>
    /// Quota Guard tier 1 (#365): the vanilla end-of-round credit fine for dead crew is
    /// halved. Vanilla-only - this is the effect the tier sells on a stock install, and it
    /// no longer depends on Y4NGZCompany being present.
    ///
    /// WHERE IT RUNS. Vanilla's fine is applied in HUDManager.ApplyPenalty, called from
    /// StartOfRound.EndOfGame, which is started inside the EXECUTE stage of
    /// EndOfGameClientRpc - so it runs on the host AND on every client, each deducting from
    /// its own local Terminal.groupCredits. Vanilla keeps the value consistent purely by
    /// running the same arithmetic everywhere, not by syncing the result.
    ///
    /// HOST/CLIENT CONSISTENCY. This patch keeps that property rather than breaking it:
    /// every peer applies the same halving, gated on the same crew-wide MAX tier that
    /// <see cref="QuotaGuardCompanyPatch.GetCrewEffectiveTier"/> reads out of the broadcast
    /// <see cref="UpgradeTierSync"/> table. Because the table is the same on every peer, the
    /// deduction stays identical. A host-only patch would be strictly worse here: nothing
    /// re-syncs groupCredits at round end, so clients would keep the un-halved figure until
    /// the next host-authoritative credits write.
    ///
    /// The correction is taken from the observed deduction instead of re-deriving vanilla's
    /// 20% / 8% split, so an insured-body mix, the 60-credit floor clamp and any future
    /// change to those rates all stay vanilla's business.
    /// </summary>
    [HarmonyPatch]
    internal static class QuotaGuardDeathPenaltyPatch
    {
        // -1 means "not eligible / nothing captured", so the postfix is a no-op.
        private const int NotCaptured = -1;

        [HarmonyPatch(typeof(HUDManager), "ApplyPenalty")]
        [HarmonyPrefix]
        private static void PreApplyPenalty(out int __state)
        {
            __state = NotCaptured;

            try
            {
                if (QuotaGuardCompanyPatch.GetCrewEffectiveTier() < 1)
                    return;

                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                if (terminal == null)
                    return;

                __state = terminal.groupCredits;
            }
            catch (Exception e)
            {
                __state = NotCaptured;
                Plugin.Log?.LogError($"QuotaGuardDeathPenaltyPatch.PreApplyPenalty: {e}");
            }
        }

        [HarmonyPatch(typeof(HUDManager), "ApplyPenalty")]
        [HarmonyPostfix]
        private static void PostApplyPenalty(HUDManager __instance, int __state)
        {
            if (__state == NotCaptured)
                return;

            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                if (terminal == null)
                    return;

                int fine = __state - terminal.groupCredits;
                if (fine <= 0)
                    return;

                // Integer halving rounds the fine DOWN, which favours the crew - and keeps
                // the arithmetic identical on every peer, which floats would not guarantee.
                int relieved = (int)(fine * QuotaGuardUpgrade.DEATH_CREDIT_PENALTY_MULTIPLIER);
                terminal.groupCredits = __state - relieved;

                // Vanilla wrote the un-halved figure into the end-of-round DUE line one
                // statement earlier; correct it so the screen matches the wallet. The
                // casualty percentage line above it is left alone: it reports the vanilla
                // rate per casualty, which is still what was assessed before the relief.
                if (__instance?.statsUIElements?.penaltyTotal != null)
                    __instance.statsUIElements.penaltyTotal.text = $"DUE: ${relieved}";

                Plugin.Log?.LogInfo(
                    $"QuotaGuard: crew-death credit penalty halved ({fine} -> {relieved}).");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"QuotaGuardDeathPenaltyPatch.PostApplyPenalty: {e}");
            }
        }
    }

    /// <summary>
    /// Quota Guard tier 2: 4-day quota deadline instead of 3. Vanilla-only patch -
    /// works without Y4NGZCompany. Host-authoritative: vanilla periodically pushes
    /// the host's timeUntilDeadline to clients (TimeOfDay.SyncTimeClientRpc), so
    /// clients that locally computed the 3-day deadline converge within seconds.
    /// Tier changes mid-run intentionally apply on the NEXT quota roll only - an
    /// already-running deadline is never retroactively extended or shortened.
    /// </summary>
    [HarmonyPatch]
    internal static class QuotaGuardDeadlinePatch
    {
        // Vanilla SetNewProfitQuota (server-only) sets timeUntilDeadline, then its
        // local ClientRpc execution overwrites it with
        // totalTime * quotaVariables.deadlineDaysAmount (3 days). This postfix runs
        // after both, so the host's extended value wins and syncs out via vanilla.
        [HarmonyPatch(typeof(TimeOfDay), "SetNewProfitQuota")]
        [HarmonyPostfix]
        private static void AfterSetNewProfitQuota(TimeOfDay __instance)
        {
            try
            {
                if (__instance == null || !__instance.IsServer)
                    return;

                if (QuotaGuardCompanyPatch.GetCrewEffectiveTier() < 2)
                    return;

                __instance.timeUntilDeadline = __instance.totalTime * QuotaGuardUpgrade.EXTENDED_DEADLINE_DAYS;
                __instance.UpdateProfitQuotaCurrentTime();

                // F-FOREMAN-A-18: vanilla only pushes timeUntilDeadline from SyncGlobalTimeOnNetwork,
                // which is gated on globalTime advancing - and globalTime does not advance in orbit.
                // Without this the host's monitor reads "4 Days" while every client still reads 3
                // until the crew lands. SyncTimeClientRpc is the same call vanilla uses.
                __instance.SyncTimeClientRpc(__instance.globalTime, (int)__instance.timeUntilDeadline);
                Plugin.Log?.LogInfo($"QuotaGuard: extended new quota deadline to {QuotaGuardUpgrade.EXTENDED_DEADLINE_DAYS} days.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"QuotaGuardDeadlinePatch.AfterSetNewProfitQuota: {e}");
            }
        }

        /// <summary>
        /// F-FOREMAN-A-18: SyncTimeClientRpc writes timeUntilDeadline but never refreshes the
        /// deadline monitor, which clients otherwise only recompute in
        /// UpdateProfitQuotaCurrentTime/SetBuyingRateForDay during a landed day.
        /// </summary>
        [HarmonyPatch(typeof(TimeOfDay), "SyncTimeClientRpc")]
        [HarmonyPostfix]
        private static void AfterSyncTime(TimeOfDay __instance)
        {
            try
            {
                if (__instance == null || __instance.IsServer || StartOfRound.Instance == null)
                    return;

                __instance.UpdateProfitQuotaCurrentTime();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"QuotaGuardDeadlinePatch.AfterSyncTime: {e}");
            }
        }

        /// <summary>
        /// F-FOREMAN-A-5: vanilla derives the Company buying rate from
        /// <c>quotaVariables.deadlineDaysAmount</c> (3), so a 4-day deadline drives
        /// <c>0.7/3 * (3 - 4) + 0.3</c> = ~6.7% on the newly added first day - a quarter of
        /// vanilla's worst case, i.e. a silent penalty bolted onto a bonus. Clamp back to
        /// vanilla's 0.3 floor. Every peer runs SetBuyingRateForDay and every peer reads the
        /// same crew tier out of UpgradeTierSync, so the clamp stays consistent.
        /// </summary>
        [HarmonyPatch(typeof(TimeOfDay), "SetBuyingRateForDay")]
        [HarmonyPostfix]
        private static void AfterSetBuyingRateForDay()
        {
            try
            {
                StartOfRound round = StartOfRound.Instance;
                if (round == null || round.companyBuyingRate >= VanillaBuyingRateFloor)
                    return;

                if (QuotaGuardCompanyPatch.GetCrewEffectiveTier() < 2)
                    return;

                round.companyBuyingRate = VanillaBuyingRateFloor;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"QuotaGuardDeadlinePatch.AfterSetBuyingRateForDay: {e}");
            }
        }

        // Vanilla's own worst-case rate, hardcoded as `float num = 0.3f` in SetBuyingRateForDay.
        private const float VanillaBuyingRateFloor = 0.3f;
    }
}
