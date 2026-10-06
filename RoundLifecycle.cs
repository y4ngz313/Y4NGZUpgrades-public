using System;
using HarmonyLib;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// The one place this plugin learns that a round started or ended.
    ///
    /// Paid for 2026-08-13 (#214): every per-round reset in this plugin used to postfix
    /// <c>StartOfRound.StartGame</c>. Clients never call that method at all — StartMatchLever
    /// routes a non-server pull through StartGameServerRpc and only the server enters the
    /// body — so on every client the hook was dead. Progression never started a round, every
    /// recorder early-returned, and the finalize fallback silently paid a survival bonus into
    /// a round that had never begun.
    ///
    /// The replacement hooks run on host AND clients:
    /// <list type="bullet">
    /// <item><c>StartOfRound.openingDoorsSequence</c> — started from
    /// <c>RoundManager.FinishGeneratingNewLevelClientRpc</c>'s Execute branch on every peer.
    /// A plain postfix on an iterator method runs at enumerator creation, i.e. exactly when
    /// StartCoroutine is called, with the level scene loaded and ship items present (which
    /// SnapshotPreexistingShipScrap requires).</item>
    /// <item><c>StartOfRound.EndOfGame</c> — started unconditionally from
    /// <c>EndOfGameClientRpc</c>'s Execute branch, OUTSIDE its <c>planetHasTime</c> guard.
    /// That guard is why finalization used to be skipped entirely on a no-clock moon.</item>
    /// </list>
    /// The ClientRpc methods themselves are deliberately NOT patched: their bodies execute
    /// twice on the host (send stage, then execute stage).
    /// </summary>
    [HarmonyPatch]
    internal static class RoundLifecycle
    {
        private static bool _startedThisRound;
        private static bool _endedThisRound;
        private static bool _installed;

        /// <summary>Fires once per round on every peer, at ship-doors-opening time.</summary>
        internal static event Action RoundStarted;

        /// <summary>Fires once per round on every peer, when the end-of-game sequence starts.</summary>
        internal static event Action RoundEnded;

        /// <summary>
        /// Subscribes every per-round reset. This list is the migration of the old dead
        /// <c>StartOfRound.StartGame</c> postfixes; each entry keeps its original semantics.
        /// </summary>
        internal static void Install()
        {
            if (_installed)
                return;
            _installed = true;

            // Progression / statistics ledgers first: the recorders below are gated on them.
            RoundStarted += ProgressionManager.BeginRound;
            RoundStarted += EmployeeStatistics.BeginRound;
            RoundStarted += ProgressionXpBarUi.ResetForRound;
            RoundStarted += ProgressionOrbitNameTags.OnRoundStarted;

            RoundStarted += Interactive.Patches.CombatFeedbackPatch.OnRoundStarted;
            RoundStarted += Interactive.Patches.WorldFeedbackPatch.OnRoundStarted;
            RoundStarted += Patches.NineLivesPatch.OnRoundStarted;
            RoundStarted += Upgrades.LoneWolfUpgrade.Cleanup;
            RoundStarted += Patches.CommandNetPatch.OnRoundStarted;
            RoundStarted += Patches.CourierDronePatch.OnRoundStarted;
            RoundStarted += Patches.EscapeArtistDoubleJumpPatch.OnRoundStarted;
            RoundStarted += Patches.FieldMechanicCompanyPatch.OnRoundStarted;
            RoundStarted += Patches.FieldTabletMainframeNet.OnRoundStarted;
            RoundStarted += Patches.ForemanPingPatch.OnRoundStarted;
            RoundStarted += Patches.ForemanSupportPatch.OnRoundStarted;
            RoundStarted += Patches.LedgeMantlePatch.OnRoundStarted;
            RoundStarted += Patches.NativeFistsPatch.OnRoundStarted;
            RoundStarted += Patches.PanicSlidePatch.OnRoundStarted;
            RoundStarted += Patches.ShadowStepPatch.OnRoundStarted;
            RoundStarted += Patches.SixthSensePatch.OnRoundStarted;
            // #459: the host's battery-break latch; kept only while its break is unresolved.
            RoundStarted += Patches.ShipBatteryXpPatch.OnRoundStarted;
            RoundStarted += Patches.SquadSightPatch.OnRoundStarted;
            // F-INFRA-4 (WP1 request): the per-turret Light Feet hold-fire timer table is keyed
            // on Turret instances from the previous scene, so it has to be cleared per round.
            RoundStarted += Patches.TurretPatch.OnRoundStarted;
            RoundStarted += Patches.TurretHackerPatch.OnRoundStarted;
            RoundStarted += Patches.WorklightBeaconPatch.OnRoundStarted;
            // Resilience adds its reserve layer only while bonus health is active. Clear that
            // child layer at every round boundary, including abandoned rounds.
            RoundStarted += HUD.ResilienceHealthPresentation.Restore;
            // WP12 dead-code sweep: GlowInTheDarkPatch and ShoulderCheckPatch are deleted - both
            // were gated on accessors whose names matched no catalog row, so neither could act.
            // The weapon, grenade and grenade-launcher per-round resets moved to
            // BetterArmory.ArmoryRoundLifecycle (#266), which postfixes the same
            // StartOfRound.openingDoorsSequence for the same reason (#214).

            // Finalization is a lifecycle concern, not a HUD concern. The vanilla HUD path
            // (FillEndGameStats / SetPlayerLevel) only runs inside `if (planetHasTime)`, so a
            // no-clock moon used to finalize for nobody, host included.
            RoundEnded += FinalizeProgression;
        }

        private static void FinalizeProgression()
        {
            int scrapCollected = StartOfRound.Instance != null
                ? StartOfRound.Instance.scrapCollectedLastRound
                : 0;
            ProgressionManager.FinalizeRound(scrapCollected);
            EmployeeStatistics.FinalizeRound(scrapCollected);
        }

        private static void RaiseRoundStarted()
        {
            if (_startedThisRound)
                return;

            _startedThisRound = true;
            _endedThisRound = false;
            Dispatch(RoundStarted, "RoundStarted");
        }

        private static void RaiseRoundEnded()
        {
            if (_endedThisRound)
                return;

            _endedThisRound = true;
            _startedThisRound = false;
            Dispatch(RoundEnded, "RoundEnded");
        }

        /// <summary>
        /// "The round is definitely over" latch for paths that pay nothing: a mid-round
        /// disconnect, or the orbit transition itself. Clears the lifecycle flags and the
        /// ledger latches so an unfinalized round can never wedge the next one. It never
        /// dispatches RoundEnded — a round abandoned this way has no payout.
        /// </summary>
        private static void ForceRoundOver(string reason)
        {
            _startedThisRound = false;
            _endedThisRound = true;

            // Both helpers are no-ops once the round finalized, so the normal
            // EndOfGame -> SetShipReadyToLand ordering never discards a real payout or the
            // breakdown the XP bar is still presenting.
            ProgressionManager.AbandonUnfinalizedRound(reason);
            EmployeeStatistics.AbandonUnfinalizedRound();
        }

        private static void Dispatch(Action handlers, string label)
        {
            if (handlers == null)
                return;

            Delegate[] list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action)list[i])();
                }
                catch (Exception ex)
                {
                    // One misbehaving subscriber must not strand the rest of the round.
                    Plugin.Log?.LogError(
                        $"[RoundLifecycle] {label} subscriber {list[i].Method?.DeclaringType?.Name}.{list[i].Method?.Name} threw: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(StartOfRound), "openingDoorsSequence")]
        [HarmonyPostfix]
        private static void PostOpeningDoorsSequence()
        {
            RaiseRoundStarted();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            RaiseRoundEnded();
        }

        [HarmonyPatch(typeof(StartOfRound), "SetShipReadyToLand")]
        [HarmonyPostfix]
        private static void PostSetShipReadyToLand()
        {
            ForceRoundOver("ship ready to land");
            // #458: Company clears its report ledger here; a crewmate's late share from this
            // round must not re-populate it.
            Patches.ProgressionReportShareNetwork.CloseRound();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            ForceRoundOver("disconnect");
        }
    }
}
