using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using Y4NGZUpgrades.Patches;
using Y4NGZUpgrades.Gui;

namespace Y4NGZUpgrades
{
    internal static class ProgressionPatches
    {
        private static readonly Dictionary<string, Type> ContractTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        private static Type ContractType(string typeName)
        {
            if (ContractTypeCache.TryGetValue(typeName, out Type cached))
                return cached;

            Type resolved = Type.GetType("Y4NGZCompany.Contracts._Shared." + typeName + ", Y4NGZCompany", throwOnError: false)
                ?? Type.GetType("Y4NGZCompany.Contracts.ShadowRaid." + typeName + ", Y4NGZCompany", throwOnError: false)
                ?? FindCompanyTypeByName(typeName);

            ContractTypeCache[typeName] = resolved;
            return resolved;
        }

        private static Type FindCompanyTypeByName(string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.GetName().Name != "Y4NGZCompany")
                    continue;

                Type found = FindTypeByName(assembly, typeName);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static Type FindTypeByName(Assembly assembly, string typeName)
        {
            try
            {
                Type[] types = assembly.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        return type;
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                Type[] types = ex.Types;
                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        return type;
                }
            }
            catch
            {
            }

            return null;
        }

        [HarmonyPatch(typeof(StartOfRound), "Start")]
        internal static class StartOfRoundStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                ProgressionManager.SwitchToCurrentSave();
                EmployeeStatistics.SwitchToCurrentSave();
            }
        }

        // Round start/end now arrive through RoundLifecycle, which subscribes
        // ProgressionManager.BeginRound, EmployeeStatistics.BeginRound and
        // ProgressionXpBarUi.ResetForRound. The StartOfRound.StartGame postfix that used to
        // live here only ever ran on the host (#214).

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        internal static class PlayerRuntimeStatisticsPatch
        {
            [HarmonyPostfix]
            private static void Postfix(PlayerControllerB __instance)
            {
                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                    ?? StartOfRound.Instance?.localPlayerController;
                if (__instance == local)
                    EmployeeStatistics.UpdateRuntime(__instance, Time.deltaTime);
            }
        }

        [HarmonyPatch(typeof(EntranceTeleport), nameof(EntranceTeleport.TeleportPlayerClientRpc))]
        internal static class FacilityEntryPatch
        {
            [HarmonyPostfix]
            private static void Postfix(EntranceTeleport __instance, int playerObj)
            {
                if (__instance != null && __instance.isEntranceToBuilding)
                    ProgressionManager.RecordFacilityEntry(playerObj);
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.ResetSavedGameValues))]
        internal static class ResetSavePatch
        {
            [HarmonyPrefix]
            private static void Prefix(GameNetworkManager __instance, out string __state)
            {
                __state = null;
                if (__instance != null && __instance.isHostingGame)
                    SaveKey.TryGetCurrentExistingOrSlot(out __state);
            }

            [HarmonyPostfix]
            private static void Postfix(string __state)
            {
                if (!string.IsNullOrWhiteSpace(__state))
                    SaveDataLifecycle.DeleteSave(__state, "vanilla save-value reset");
            }
        }

        // DISCOVERY bucket only. GrabItem runs only on the grabbing player's own machine, so
        // it can establish "this peer lifted it first", never a crew-wide fact. Delivery
        // attribution used to be built on this hook and was therefore paid to every player who
        // ever lifted an object; it now lives on the boundary-crossing hooks below (#215).
        [HarmonyPatch(typeof(GrabbableObject), "GrabItem")]
        internal static class ScrapPickupPatch
        {
            [HarmonyPostfix]
            private static void Postfix(GrabbableObject __instance)
            {
                ProgressionManager.RecordScrapPickup(__instance);
                EmployeeStatistics.RecordItemGrabbed(__instance);
            }
        }

        // DELIVERY attribution. The ledger's truth is the ITEM's own isInShipRoom/isInElevator
        // delta, sampled around every vanilla site that writes those flags; the site only
        // supplies the candidate responsible player. Coverage of the write sites in the
        // shipped decompile:
        //
        //   PATCHED
        //   - PlayerControllerB.SetItemInElevator (PlayerControllerB.cs:3096) -- the canonical
        //     crossing, reached on every peer from GrabObjectClientRpc, SetObjectAsNoLongerHeld
        //     (discard/throw), DropHeldItem/DropAllHeldItems, PlaceGrabbableObject and the ship
        //     reverb triggers.
        //   - PlayerControllerB.SetHeldObjectInShip (PlayerControllerB.cs:4388) -- REQUIRED.
        //     For a remote player's held item this runs from the position RPCs and writes
        //     gObject.isInElevator DIRECTLY, forwarding to SetItemInElevator only when
        //     isInShipRoom changes. Without it an elevator-only (ship deck) crossing is seen
        //     on the carrier's own machine but nowhere else, and the machines disagree about
        //     who last brought the object in -- which paid two players for one object.
        //   - EnemyAI.SetItemInElevatorNonPlayer (EnemyAI.cs:621) -- a baboon or kiwi hauling
        //     scrap across. There is no responsible player, so it records as unattributed.
        //
        //   REPORTED DIRECTLY (this mod's own write site, no patch needed)
        //   - CourierDronePatch.MoveScrapToPlayer -- the drone writes both flags itself after
        //     nulling playerHeldBy, reaching none of the hooks above. It calls
        //     ProgressionManager.RecordAttributedBoundaryCrossing with the courier player id
        //     from the replicated command, covering both the delivery and the drop back out.
        //
        //   DOCUMENTED NON-DIVERGENT (no patch)
        //   - BaboonBirdAI.cs:863, GiantKiwiAI.cs:884 force isInShipRoom/isInElevator false as
        //     an enemy grabs an item. That can only move an object OUT; the payout independently
        //     requires the object to be inside at round end, so a stolen object cannot pay.
        //   - StartOfRound.cs:1360 sets the flags on scrap it spawns into the ship during the
        //     ship phase (scrapPersistedThroughRounds). Those objects are in the pre-existing
        //     snapshot taken at round start and can never pay.
        //   - StartOfRound.GetValueOfAllScrap (StartOfRound.cs:5183) and RoundManager.cs:1362
        //     (belt bag contents) reconcile flags without any carrier. They add no handler
        //     record, so an object that reaches the ship only through them pays nobody, and an
        //     object that already has a record keeps the one every machine agrees on.
        //
        // Both hooks need the prefix: vanilla mutates the flags and then early-returns on the
        // no-op case, so by postfix time a real crossing is indistinguishable from a call that
        // changed nothing.
        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.SetItemInElevator))]
        internal static class ItemBoundaryCrossingPatch
        {
            [HarmonyPrefix]
            private static void Prefix(GrabbableObject gObject, out bool __state)
            {
                __state = ProgressionManager.IsInsideShip(gObject);
            }

            [HarmonyPostfix]
            private static void Postfix(PlayerControllerB __instance, GrabbableObject gObject, bool __state)
            {
                ProgressionManager.RecordItemBoundaryCrossing(__instance, gObject, __state);
            }
        }

        // Nested inside the patch above whenever isInShipRoom changes, so the inner hook may
        // report the same transition first. That is harmless: the record is (key, handler,
        // side), the handler is the same player on both, and re-recording an already-recorded
        // transition is idempotent.
        // nameof, not a bare string: the publicized game reference exposes this private method
        // at compile time, so a vanilla rename becomes a build error here instead of a
        // HarmonyException that aborts PatchAll and takes the rest of the plugin down with it.
        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.SetHeldObjectInShip))]
        internal static class HeldObjectBoundaryCrossingPatch
        {
            [HarmonyPrefix]
            private static void Prefix(GrabbableObject gObject, out bool __state)
            {
                __state = ProgressionManager.IsInsideShip(gObject);
            }

            [HarmonyPostfix]
            private static void Postfix(PlayerControllerB __instance, GrabbableObject gObject, bool __state)
            {
                ProgressionManager.RecordItemBoundaryCrossing(__instance, gObject, __state);
            }
        }

        // Enemy carriers move scrap across the boundary with no responsible player at all.
        // Recording the crossing as unattributed is what stops the player who originally
        // delivered the object from keeping credit after a baboon hauls it back in.
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.SetItemInElevatorNonPlayer))]
        internal static class EnemyItemBoundaryCrossingPatch
        {
            [HarmonyPrefix]
            private static void Prefix(GrabbableObject gObject, out bool __state)
            {
                __state = ProgressionManager.IsInsideShip(gObject);
            }

            [HarmonyPostfix]
            private static void Postfix(GrabbableObject gObject, bool __state)
            {
                ProgressionManager.RecordItemBoundaryCrossing(null, gObject, __state);
            }
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.HitEnemy))]
        internal static class EnemyHitPatch
        {
            [HarmonyPostfix]
            private static void Postfix(EnemyAI __instance, PlayerControllerB playerWhoHit)
            {
                ProgressionManager.RecordEnemyHit(__instance, playerWhoHit);
                EmployeeStatistics.RecordEnemyHit(__instance, playerWhoHit);
            }
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.KillEnemy))]
        internal static class EnemyKillPatch
        {
            [HarmonyPrefix]
            private static void Prefix(EnemyAI __instance, out bool __state)
            {
                __state = __instance != null && !__instance.isEnemyDead;
            }

            [HarmonyPostfix]
            private static void Postfix(EnemyAI __instance, bool __state)
            {
                if (__state)
                {
                    ProgressionManager.RecordEnemyKilled(__instance);
                    EmployeeStatistics.RecordEnemyKilled(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.KillPlayer))]
        internal static class PlayerDeathPatch
        {
            [HarmonyPrefix]
            private static void Prefix(PlayerControllerB __instance)
            {
                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                    ?? StartOfRound.Instance?.localPlayerController;
                if (__instance != null && local != null && __instance == local && !__instance.isPlayerDead)
                {
                    ProgressionManager.MarkLocalPlayerDeath();
                    EmployeeStatistics.RecordDeath();
                }
            }
        }

        // Presentation priming only. Both of these run from inside the game's
        // `if (currentLevel.planetHasTime)` branch in EndOfGameClientRpc, so on a no-clock
        // moon they never fire at all -- RoundLifecycle.RoundEnded owns finalization now.
        // The FinalizeRound calls below are kept because this path carries the authoritative
        // scrapCollected value and is idempotent: whichever trigger runs first pays out, and
        // the other returns that same breakdown.
        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.FillEndGameStats))]
        internal static class FillEndGameStatsPatch
        {
            [HarmonyPrefix]
            private static void Prefix(int scrapCollected)
            {
                ProgressionManager.FinalizeRound(scrapCollected);
                EmployeeStatistics.FinalizeRound(scrapCollected);
            }

            [HarmonyFinalizer]
            [HarmonyPriority(Priority.Last)]
            [HarmonyAfter(
                OptionalPluginCapabilities.CoronerGuid,
                OptionalPluginCapabilities.BetterExpGuid)]
            private static Exception Finalizer(HUDManager __instance, Exception __exception)
            {
                if (__exception == null)
                    ProgressionPerformanceReportUi.Apply(__instance);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.SetPlayerLevel))]
        internal static class SetPlayerLevelPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(HUDManager __instance)
            {
                RoundXpBreakdown log = ProgressionManager.LastRoundBreakdown
                    ?? ProgressionManager.FinalizeRound(StartOfRound.Instance != null ? StartOfRound.Instance.scrapCollectedLastRound : 0);
                // Runs before ShowRoundXp so the level box never fades in showing stale
                // vanilla values while the Company report holds the actual replay.
                ProgressionXpBarUi.PrimeStartingValues(__instance, log);
                ProgressionXpBarUi.ShowRoundXp(__instance, log);
                return false;
            }
        }

        // Every reflective cross-plugin patch here guards on the METHOD, not just the type.
        // Paid for 2026-07-30: a Company-side refactor removed a method this file still named,
        // TargetMethod() returned null, and the resulting HarmonyException aborted PatchAll --
        // killing the rest of Plugin.Awake and every system initialized after it (VFX
        // diagnostics, visual config, weapon registration). A type-only Prepare converts a
        // sibling plugin's rename into this plugin dead at boot; a method-aware Prepare converts
        // it into one hook quietly inactive until the rename lands, with HarmonyX's own
        // "Could not find method" warning keeping it visible.
        //
        // Every contract objective and outcome now arrives through this single seam:
        // Y4NGZCompany publishes attributed, idempotent progression events for all contract
        // types, mirrored to every peer. That replaced the per-objective method patches this
        // file used to carry (whistleblower, survey drone, blackout breaker, breach drill),
        // which were host-only or leaked XP to the whole crew.
        [HarmonyPatch]
        internal static class ContractProgressionEventPatch
        {
            private static bool Prepare()
            {
                return AccessTools.Method(
                    ContractType("ContractProgressionEvents"),
                    "Publish") != null;
            }

            private static MethodBase TargetMethod()
            {
                return AccessTools.Method(
                    ContractType("ContractProgressionEvents"),
                    "Publish");
            }

            [HarmonyPostfix]
            private static void Postfix(object __0)
            {
                if (__0 == null || !TryGetProperty(__0, "SchemaVersion", out int schemaVersion) || schemaVersion != 1)
                    return;
                if (!TryGetProperty(__0, "PlayerClientId", out ulong playerClientId))
                    return;
                if (!TryGetProperty(__0, "EventId", out string eventId) || string.IsNullOrWhiteSpace(eventId))
                    return;

                string kind = GetPropertyValue(__0, "Kind")?.ToString();
                if (kind == "PestControlCompleted" || kind == "PestControlCapture")
                {
                    bool hasCapturedTotal = TryGetProperty(__0, "Count", out int capturedTotal);
                    EmployeeStatistics.ObservePestsTrapped(
                        eventId,
                        playerClientId,
                        hasCapturedTotal ? capturedTotal : 0);
                }

                if (!IsLocalPlayer(playerClientId))
                    return;

                // ContractType/SourceLabel are additive properties. An older Y4NGZCompany simply
                // does not declare them, so both read back as null and every label falls through
                // to its unqualified form.
                if (!TryGetProperty(__0, "ContractType", out string contractType) || string.IsNullOrWhiteSpace(contractType))
                    TryGetProperty(__0, "ContractId", out contractType);
                if (!TryGetProperty(__0, "SourceLabel", out string sourceLabel))
                    sourceLabel = null;

                Config.ProgressionSettings config = Plugin.ProgressionConfig;
                int amount;
                string label;
                // Set by the count-capped kinds only; null means "the uncapped tail".
                string capBucket = null;
                int capMaxPerRound = 0;
                switch (kind)
                {
                    case "DefuseCorrectWire":
                        amount = Plugin.ProgressionConfig.DefuseCorrectWireXp.Value;
                        label = "Cut the correct wire";
                        break;
                    case "DefuseCorrectCode":
                        amount = Plugin.ProgressionConfig.DefuseCorrectCodeXp.Value;
                        label = "Entered the defuse code";
                        EmployeeStatistics.RecordBombDefused(eventId);
                        break;
                    case "PayloadPilotSeconds":
                        if (!TryGetProperty(__0, "ContractSucceeded", out bool succeeded) || !succeeded
                            || !TryGetProperty(__0, "Seconds", out float seconds) || seconds <= 0f)
                        {
                            return;
                        }
                        amount = ProgressionEconomyMath.ComputePayloadPilotXp(
                            seconds,
                            Plugin.ProgressionConfig.PayloadPilotXpPerSecond.Value,
                            ProgressionManager.GetPayloadPilotMaxXp());
                        label = $"Piloted the cart {seconds:0.#}s";
                        break;
                    case "WhistleblowerSighted":
                        amount = config.WhistleblowerDiscoveryXp.Value;
                        label = TryGetProperty(__0, "Count", out int sighting) && sighting > 0
                            ? $"Spotted the Whistleblower ({sighting})"
                            : "Spotted the Whistleblower";
                        break;
                    case "WhistleblowerNeutralized":
                        amount = config.WhistleblowerKillXp.Value;
                        label = "Killed the Whistleblower";
                        break;
                    case "SurveyDronePlaced":
                        amount = config.SurveyDronePlacedXp.Value;
                        label = string.IsNullOrWhiteSpace(sourceLabel)
                            ? "Planted a survey beacon"
                            : $"Planted a survey beacon ({sourceLabel})";
                        // XP may be paid on a crew-wide fan-out; a PERSONAL LIFETIME counter may
                        // not. "You planted 40 beacons" has to mean this player planted them, so
                        // the statistic is gated on the event being individually attributed while
                        // the XP above is not.
                        if (ContractEventAttribution.IsIndividuallyAttributed(eventId, playerClientId))
                            EmployeeStatistics.RecordSurveyBeaconPlaced(eventId);
                        break;
                    // #392 (Company #794): one credit per restored breaker zone, to the player
                    // who flipped it. Each zone has its own event id, so the once-per-round
                    // dedupe pays every zone and a partial restore on a failed round pays too.
                    case "BlackoutBreakerRestored":
                        amount = config.BlackoutBreakerZoneRestoredXp.Value;
                        label = string.IsNullOrWhiteSpace(sourceLabel)
                            ? "Restored the breaker box"
                            : $"Restored the breaker box ({sourceLabel})";
                        break;
                    case "BlackoutAuditRestored":
                        amount = config.BlackoutBreakerRestoredXp.Value;
                        // Company words the lever path as its own act and merges every other
                        // source into the breaker line (Y4NGZCompany#1202).
                        label = sourceLabel != null && sourceLabel.IndexOf("lever", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "Pulled the audit lever"
                            : string.IsNullOrWhiteSpace(sourceLabel)
                                ? "Restored the breaker box"
                                : $"Restored the breaker box ({sourceLabel})";
                        // Multi-zone restorations are counted at Contracted's per-breaker seam.
                        // This event is the only seam used by the single-breaker fallback path.
                        if (string.Equals(sourceLabel, "single breaker box", StringComparison.OrdinalIgnoreCase))
                            EmployeeStatistics.RecordSingleBreakerRestored(eventId);
                        break;
                    case "ShadowRaidDrillAttached":
                        amount = config.BlacksiteDrillPlacedXp.Value;
                        label = "Mounted the breach drill";
                        // Company #1201: an unattributed drill fans out as '.crew.{player}' copies,
                        // one per participant, and each now reaches its own machine. XP may ride
                        // that payout; the personal lifetime counter may not.
                        if (!ContractEventAttribution.IsCrewFanOut(eventId))
                            EmployeeStatistics.RecordDrillPlaced(eventId);
                        break;
                    case "ShadowRaidDrillCompleted":
                        amount = config.ShadowRaidDrillCompletedXp.Value;
                        label = "Cracked the container";
                        break;
                    case "PestControlCompleted":
                        amount = config.PestControlCompletedXp.Value;
                        bool hasCompletedCount = TryGetProperty(__0, "Count", out int specimens);
                        label = hasCompletedCount && specimens > 1
                            ? $"Trapped {specimens} specimens"
                            : "Trapped a specimen";
                        break;
                    case "WasteDisposalCompleted":
                        amount = config.WasteDisposalCompletedXp.Value;
                        label = TryGetProperty(__0, "Count", out int burned) && burned > 1
                            ? $"Fired the incinerator ({burned})"
                            : "Fired the incinerator";
                        break;
                    // ---- kinds added by Company #406 / consumed for #194 ----
                    // Per LIVING participant, once per wave: the publisher credits only players
                    // who were alive when that wave closed, and gives each its own event id.
                    case "ContainmentBreachWaveSurvived":
                        amount = config.ContainmentWaveSurvivedXp.Value;
                        label = string.IsNullOrWhiteSpace(sourceLabel)
                            ? "Survived the wave"
                            : $"Survived {sourceLabel}";
                        EmployeeStatistics.RecordContainmentWaveSurvived(eventId);
                        break;
                    case "ShadowRaidHardDriveRecovered":
                        amount = config.ShadowRaidHardDriveXp.Value;
                        label = "Recovered the hard drive";
                        break;
                    // The two count-capped kinds. Both objectives re-arm on a timer, so the event
                    // id dedupe alone leaves them unbounded: the malfunction can be repaired
                    // dozens of times in one round, out-earning the contract completion several
                    // times over, and the Pest Control trap can be re-dropped indefinitely.
                    case "WasteDisposalRepaired":
                        amount = config.WasteDisposalRepairedXp.Value;
                        label = TryGetProperty(__0, "Count", out int repairIndex) && repairIndex > 1
                            ? $"Fixed the incinerator ({repairIndex})"
                            : "Fixed the incinerator";
                        capBucket = "wastedisposal.repair";
                        capMaxPerRound = config.WasteDisposalRepairMaxPerRound.Value;
                        break;
                    case "PestControlCapture":
                        amount = config.PestControlCaptureXp.Value;
                        // Count is the running captured TOTAL, not the cage ordinal: one cage
                        // drop can take several specimens at once.
                        bool hasCaptureCount = TryGetProperty(__0, "Count", out int captureNumber);
                        label = hasCaptureCount && captureNumber > 1
                            ? $"Trapped {captureNumber} specimens"
                            : "Trapped a specimen";
                        capBucket = "pestcontrol.capture";
                        capMaxPerRound = config.PestControlCaptureMaxPerRound.Value;
                        break;
                    case "ContractCompleted":
                        amount = ProgressionManager.GetContractCompletionXp(contractType);
                        label = string.IsNullOrWhiteSpace(contractType)
                            ? "Contract completed"
                            : $"{contractType} contract completed";
                        break;
                    case "ContractFailed":
                        amount = config.ContractFailedXp.Value;
                        label = string.IsNullOrWhiteSpace(contractType)
                            ? "Contract failed"
                            : $"{contractType} contract failed";
                        break;
                    default:
                        if (!LguContractXpCatalog.TryGet(kind, out LguContractXpCatalog.Award lguAward)
                            || !config.LguObjectiveXp.TryGetValue(kind, out var configuredXp))
                            return;
                        amount = configuredXp.Value;
                        label = lguAward.Label;
                        break;
                }

                // #457: the acts the rebalance scales are re-priced against the actor's own rank
                // band here, once, after the switch has resolved the flat award. Every other kind
                // - the count-capped credits, the contract outcomes, anything a newer Company
                // publishes - passes through unchanged.
                amount = ProgressionManager.GetContractActXp(kind, amount);

                // AddContractXp requires the round latch to still be Active, so the end-of-round
                // ordering matters and is load-bearing (#194). Verified against the shipped
                // decompile: StartOfRound.EndOfGame is an ITERATOR (<EndOfGame>d__322), started
                // from inside EndOfGameClientRpc's Execute branch. Company captures the round
                // outcome and publishes ContractCompleted/ContractFailed from a PREFIX on that
                // same ClientRpc, and RoundLifecycle finalizes from a postfix on the iterator --
                // which runs at StartCoroutine time, i.e. later in the same RPC body. So on every
                // peer the outcome events land while the latch is still Active, and finalize
                // closes it afterwards. Company's ShipLeave fallback capture is the one publish
                // point that lands after finalize; it only fires when the ClientRpc path never
                // ran, and it is already a fallback for a round that reported nothing.
                // The kind rides along so the award can be reported against the note line
                // Company writes for that objective (#220). It never affects the payout.
                // #1201: CrewWide is additive like ContractType/SourceLabel. An older Company
                // declares no such property, so it reads back false and every award is reported
                // as the player's own, exactly as before.
                bool crewWide = TryGetProperty(__0, "CrewWide", out bool crewWideFlag) && crewWideFlag;
                if (capBucket != null)
                    ProgressionManager.AddCappedContractXp(eventId, label, amount, capBucket, capMaxPerRound, kind, crewWide);
                else
                    ProgressionManager.AddContractXp(eventId, label, amount, oncePerRound: true, kind, crewWide);
            }

            private static bool IsLocalPlayer(ulong playerClientId)
            {
                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                                          ?? StartOfRound.Instance?.localPlayerController;
                return local != null && local.actualClientId == playerClientId;
            }

            private static bool TryGetProperty<T>(object source, string name, out T value)
            {
                object raw = GetPropertyValue(source, name);
                if (raw is T typed)
                {
                    value = typed;
                    return true;
                }

                value = default;
                return false;
            }

            private static object GetPropertyValue(object source, string name)
            {
                try
                {
                    return source.GetType()
                        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                        ?.GetValue(source);
                }
                catch
                {
                    return null;
                }
            }
        }

        [HarmonyPatch]
        internal static class BlackoutBreakerRestoredPatch
        {
            private static MethodBase TargetMethod()
            {
                return AccessTools.Method(
                    ContractType("MoonContractRoundStats"),
                    "RecordBlackoutBreakerRestored",
                    new[] { typeof(int), typeof(int) });
            }

            private static MethodInfo ActorResolver()
            {
                return AccessTools.Method(
                    ContractType("BlackoutBreakerActorLatch"),
                    "ResolveActorClientId");
            }

            private static bool Prepare()
            {
                return TargetMethod() != null && ActorResolver() != null;
            }

            [HarmonyPostfix]
            private static void Postfix(int __0)
            {
                try
                {
                    object actor = ActorResolver()?.Invoke(null, null);
                    if (actor is ulong actorClientId)
                        EmployeeStatistics.RecordBreakerRestored(__0, actorClientId);
                }
                catch
                {
                }
            }
        }

        [HarmonyPatch]
        internal static class WasteDisposalItemPlacementPatch
        {
            private static MethodBase TargetMethod()
            {
                return AccessTools.Method(
                    ContractType("WasteDisposalIncineratorController"),
                    "ConfirmPlaceHeldItemClientRpc",
                    new[] { typeof(int), typeof(ulong), typeof(Vector3), typeof(int) });
            }

            private static bool Prepare()
            {
                return TargetMethod() != null;
            }

            [HarmonyPostfix]
            private static void Postfix(int __0, ulong __1)
            {
                EmployeeStatistics.RecordItemIncinerated(__0, __1);
            }
        }

        [HarmonyPatch]
        internal static class WhistleblowerDamagePatch
        {
            private static MethodBase TargetMethod()
            {
                return AccessTools.Method(
                    ContractType("WhistleblowerEnemyAI"),
                    "HitEnemy",
                    new[] { typeof(int), typeof(PlayerControllerB), typeof(bool), typeof(int) });
            }

            private static bool Prepare()
            {
                return TargetMethod() != null;
            }

            [HarmonyPrefix]
            private static void Prefix(EnemyAI __instance, out int __state)
            {
                __state = __instance != null ? Mathf.Max(0, __instance.enemyHP) : 0;
            }

            [HarmonyPostfix]
            private static void Postfix(
                EnemyAI __instance,
                PlayerControllerB __1,
                int __state)
            {
                int remaining = __instance != null ? Mathf.Max(0, __instance.enemyHP) : __state;
                int damage = Mathf.Max(0, __state - remaining);
                EmployeeStatistics.RecordWhistleblowerDamage(__instance, __1, damage);
            }
        }

        [HarmonyPatch]
        internal static class LethalCctvMainframeHackPatch
        {
            private static Type _hackingOverlayType;
            private static FieldInfo _mainframeComponentField;
            private static MethodBase _successMethod;

            private static bool Prepare()
            {
                if (!OptionalPluginCapabilities.LethalCctv)
                    return false;

                _hackingOverlayType = Type.GetType(
                    "Y4NGZCompany.ShipSystems.Surveillance.HackingOverlay, LethalCCTV",
                    throwOnError: false);
                if (_hackingOverlayType == null)
                    return false;

                _mainframeComponentField = _hackingOverlayType?.GetField(
                    "_mainframeComponent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                _successMethod = _hackingOverlayType.GetMethod(
                    "TryMarkMainframeHacked",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                return _mainframeComponentField != null && _successMethod != null;
            }

            private static MethodBase TargetMethod()
            {
                return _successMethod;
            }

            [HarmonyPostfix]
            private static void Postfix(object __instance)
            {
                try
                {
                    UnityEngine.Object target = _mainframeComponentField?.GetValue(__instance) as UnityEngine.Object;
                    if (target is Component component)
                    {
                        ProgressionManager.RecordMainframeHacked(component);
                    }
                }
                catch
                {
                }
            }
        }

        // MainframeSupport is optional and uses a RequireOwnership=false RPC. The client-side
        // hacking-overlay callback above is still the progression hook, but it cannot prove that
        // the server accepted the action. This host-side patch is the employee-statistics hook.
        [HarmonyPatch]
        internal static class LethalCctvConfirmedMainframeHackPatch
        {
            private static MethodBase _target;
            private static PropertyInfo _isHackedProperty;

            private static bool Prepare()
            {
                if (!OptionalPluginCapabilities.LethalCctv)
                    return false;

                Type mainframeType = Type.GetType(
                    "Y4NGZCompany.Facility.Mainframe.MainframeSupport, LethalCCTV",
                    throwOnError: false);
                _target = mainframeType?.GetMethod(
                    "MarkHackedServerRpc",
                    BindingFlags.Instance | BindingFlags.Public);
                _isHackedProperty = mainframeType?.GetProperty(
                    "IsHacked",
                    BindingFlags.Instance | BindingFlags.Public);
                return _target != null && _isHackedProperty != null;
            }

            private static MethodBase TargetMethod()
            {
                return _target;
            }

            [HarmonyPrefix]
            private static void Prefix(object __instance, out bool __state)
            {
                __state = IsServer() && IsHacked(__instance);
            }

            [HarmonyPostfix]
            private static void Postfix(object __instance, ServerRpcParams __0, bool __state)
            {
                if (__state || !IsServer() || !IsHacked(__instance))
                    return;

                Component mainframe = __instance as Component;
                if (mainframe == null)
                    return;

                CctvEmployeeStatisticsNetwork.RecordHostConfirmedDeviceHack(
                    __0.Receive.SenderClientId,
                    "mainframe." + NetworkObjectKey.For(mainframe));
            }

            private static bool IsServer()
            {
                return NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
            }

            private static bool IsHacked(object mainframe)
            {
                try
                {
                    return _isHackedProperty != null
                           && _isHackedProperty.GetValue(mainframe) is bool hacked
                           && hacked;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
