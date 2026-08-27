using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Quota Guard ("Veteran", id quota_guard) effect wiring.
    /// Tier 1 caps the Y4NGZCompany crew-death quota penalty at 10% of the
    /// pre-penalty quota per round-end. Tier 2 extends the vanilla quota
    /// deadline to 4 days (see <see cref="QuotaGuardDeadlinePatch"/>).
    /// Both effects run host-side; the effective tier is the MAX tier across
    /// all connected crew members ("any crew member owns it"), resolved via
    /// <see cref="UpgradeTierSync"/>.
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
                Plugin.Log?.LogInfo("QuotaGuard: Y4NGZCompany not present; death-penalty cap integration disabled.");
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
                Plugin.Log?.LogInfo($"QuotaGuard: extended new quota deadline to {QuotaGuardUpgrade.EXTENDED_DEADLINE_DAYS} days.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"QuotaGuardDeadlinePatch.AfterSetNewProfitQuota: {e}");
            }
        }
    }
}
