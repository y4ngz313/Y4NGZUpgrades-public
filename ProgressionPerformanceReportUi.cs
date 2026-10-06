using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Owns the local player's end-of-round XP Notes presentation. It writes into vanilla's
    /// existing three-line slot, preserving one compatible foreign line when present, and makes
    /// one bounded delayed pass after Company's report writer has finished.
    /// </summary>
    internal static class ProgressionPerformanceReportUi
    {
        private static readonly string[] LegacyLineNames =
        {
            "Y4NGZ_ProgressionPerformanceLine",
            "Y4NGZ_EmployeeStatisticsLine"
        };

        private static int _lastLoggedRound;
        private static bool _betterExpSkipLogged;
        private static bool _companyStateLookupAttempted;
        private static FieldInfo _companyHasPendingField;
        private static FieldInfo _companyApplyCoroutineField;

        private const int CompanyWriterWaitFrames = 92;
        private const float ReportNotesMinimumFontSize = 4.2f;
        // The vanilla slot has room for the Notes heading plus three compact rows, so four total
        // rendered lines is the starting budget (#360). Two is the floor: the "Notes:" heading
        // plus a single XP note. Anything tighter would leave the slot with nothing of ours.
        private const int ReportNotesTotalLineBudget = 4;
        private const int ReportNotesMinimumLineBudget = 2;
        private const float ReportNotesFitTolerance = 0.5f;

        // The authored size must be captured once per slot instance: with autosizing enabled,
        // TMP's fontSize property returns the size it last computed, and HUDManager survives the
        // round, so re-reading it each round would ratchet fontSizeMax down until it bottomed out.
        private static readonly Dictionary<int, float> _authoredFontSizes = new Dictionary<int, float>();
        private const string CompanyRoundEndStateTypeName =
            "Y4NGZCompany.Contracts._Shared.MoonContractRoundEndState, Y4NGZCompany";

        internal static void Apply(HUDManager hud)
        {
            DestroyLegacyOverlayLines();
            if (hud == null)
                return;

            // BetterEXP replaces this same report and rank surface. Full interoperability is not
            // possible, so fail closed instead of fighting it one frame at a time.
            if (OptionalPluginCapabilities.BetterExp)
            {
                if (!_betterExpSkipLogged)
                {
                    _betterExpSkipLogged = true;
                    Plugin.Log?.LogWarning(
                        "[Progression] BetterEXP owns the performance-report XP surface; "
                        + "Y4NGZUpgrades Notes rendering is disabled for this session.");
                }
                return;
            }

            RoundXpBreakdown breakdown = ProgressionManager.LastRoundBreakdown;
            if (breakdown == null
                || breakdown.RoundSequence <= 0
)
            {
                return;
            }

            IReadOnlyList<ProgressionReportSource> sources = ProgressionManager.LastRoundReportSources;
            IReadOnlyList<string> notes = ProgressionReportNotePlan.BuildXpNotes(
                sources,
                breakdown.RoundSequence,
                maxNotes: int.MaxValue,
                xpColorHex: ResolveXpNoteColorHex());
            if (notes.Count == 0 && breakdown.AwardedTotal <= 0 && !CompanyReportXpBridge.HasNoteOwner)
                return;

            // A paired report has one final writer. Do not seed legacy XP notes into the
            // text Company captures as foreign while its delayed layout is pending.
            if (!CompanyReportXpBridge.HasNoteOwner)
                ApplyNow(hud, breakdown.RoundSequence, notes);
            try
            {
                hud.StartCoroutine(ApplyAfterDelayedWriters(hud, breakdown.RoundSequence, notes));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[Progression] Could not schedule the delayed report Notes pass: {ex.Message}");
            }
        }

        // #349: under the Default preset the report is vanilla red and Company's ledger renders
        // its vanilla-red family (Y4NGZCompany#654), so the mint default read as a glitch. The
        // Default hex below is that family's accent; any colored preset uses the shared accent.
        private const string VanillaReportXpColorHex = "#FF521F";

        private static string ResolveXpNoteColorHex()
        {
            try
            {
                if (UITheme.UiTheme.CurrentPreset == UITheme.HudColorPreset.Default)
                    return VanillaReportXpColorHex;
                return "#" + ColorUtility.ToHtmlStringRGB(UITheme.UiTheme.Accent);
            }
            catch
            {
                return VanillaReportXpColorHex;
            }
        }

        /// <summary>
        /// #458: a crewmate's shared report was recorded for <paramref name="slot"/>. If this
        /// round's report is on screen, re-apply that slot's paired row after Company's bounded
        /// writer, exactly as the local slot's delayed pass does. Once per received share, so
        /// nothing polls; with no report up Company declines and its own pass renders the row.
        /// </summary>
        internal static void ReapplyRemoteSlot(int slot)
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null || OptionalPluginCapabilities.BetterExp || slot < 0)
                return;

            try
            {
                hud.StartCoroutine(ApplyRemoteAfterDelayedWriters(hud, slot));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[Progression] Could not schedule the report row for slot {slot}: {ex.Message}");
            }
        }

        private static IEnumerator ApplyRemoteAfterDelayedWriters(HUDManager hud, int slot)
        {
            yield return WaitForCompanyReportWriter();
            // The paired presenter only: a remote slot never takes the standalone composer, which
            // writes the local player's own notes.
            if (hud != null && slot != GetLocalPlayerIndex())
                CompanyReportXpBridge.TryApplyNotes(hud, slot);
        }

        private static IEnumerator ApplyAfterDelayedWriters(
            HUDManager hud,
            int roundSequence,
            IReadOnlyList<string> notes)
        {
            yield return WaitForCompanyReportWriter();
            ApplyNow(hud, roundSequence, notes);
        }

        private static IEnumerator WaitForCompanyReportWriter()
        {
            // Contracted begins with WaitForEndOfFrame and may then retry for 90 frames while its
            // hierarchy becomes ready. Wait on its bounded writer state and compose once after
            // it clears; repeatedly overwriting the slot every frame would create a mod fight.
            yield return new WaitForEndOfFrame();
            int framesRemaining = CompanyWriterWaitFrames;
            while (framesRemaining-- > 0 && IsCompanyReportWriterPending())
                yield return null;

            if (IsCompanyReportWriterPending())
            {
                Plugin.Log?.LogWarning(
                    "[Progression] Contracted's performance-report writer remained pending "
                    + $"after {CompanyWriterWaitFrames} frames; composing with the current Notes text.");
            }
        }

        private static bool IsCompanyReportWriterPending()
        {
            if (!OptionalPluginCapabilities.Contracted)
                return false;

            EnsureCompanyReportStateFields();
            if (_companyHasPendingField == null && _companyApplyCoroutineField == null)
                return false;

            try
            {
                bool hasPending = _companyHasPendingField?.GetValue(null) is bool pending && pending;
                bool coroutineRunning = _companyApplyCoroutineField?.GetValue(null) != null;
                return hasPending || coroutineRunning;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureCompanyReportStateFields()
        {
            if (_companyStateLookupAttempted)
                return;

            _companyStateLookupAttempted = true;
            Type state = Type.GetType(CompanyRoundEndStateTypeName, throwOnError: false);
            _companyHasPendingField = state?.GetField(
                "_hasPending",
                BindingFlags.Static | BindingFlags.NonPublic);
            _companyApplyCoroutineField = state?.GetField(
                "_performanceReportApplyCoroutine",
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static void ApplyNow(
            HUDManager hud,
            int roundSequence,
            IReadOnlyList<string> notes)
        {
            RoundXpBreakdown current = ProgressionManager.LastRoundBreakdown;
            if (hud == null
                || current == null
                || current.RoundSequence != roundSequence
                || hud.statsUIElements?.playerNotesText == null)
            {
                return;
            }

            int playerIndex = GetLocalPlayerIndex();
            TextMeshProUGUI[] slots = hud.statsUIElements.playerNotesText;
            if (playerIndex < 0 || playerIndex >= slots.Length || slots[playerIndex] == null)
                return;

            if (CompanyReportXpBridge.TryApplyNotes(hud, playerIndex))
                return;
            TextMeshProUGUI slot = slots[playerIndex];
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            bool preserveLastCoronerLine =
                OptionalPluginCapabilities.Coroner && local != null && local.isPlayerDead;
            string existing = slot.text;
            ConfigureNoteSlot(slot);

            // #360: the line budget alone cannot know how tall the slot renders once a foreign
            // writer has filled it with its own heading rows, so compose, measure, and shrink the
            // budget until the text fits the rect. The loop is bounded by the budget itself, and
            // every pass composes from the pre-write text so a dropped line is never re-added.
            int budget = ReportNotesTotalLineBudget;
            string composed;
            while (true)
            {
                composed = ProgressionReportNotePlan.ComposeNotes(
                    existing,
                    notes,
                    maxLines: budget,
                    preferLastForeign: preserveLastCoronerLine,
                    totalXp: current.AwardedTotal,
                    xpColorHex: ResolveXpNoteColorHex(),
                    hiddenXp: ProgressionManager.LastRoundHiddenXp);
                composed = composed.TrimEnd('\r', '\n');
                if (!string.Equals(slot.text, composed, StringComparison.Ordinal))
                    slot.text = composed;
                slot.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);

                if (!OverflowsNoteSlot(slot, budget))
                    break;
                if (budget <= ReportNotesMinimumLineBudget)
                {
                    // Hard last resort at the floor: stop drawing past the rect rather than over
                    // the slot's underline and the neighbouring player's rows.
                    slot.overflowMode = TextOverflowModes.Truncate;
                    slot.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
                    break;
                }
                budget--;
            }

            if (_lastLoggedRound != roundSequence)
            {
                _lastLoggedRound = roundSequence;
                Plugin.Log?.LogInfo(
                    $"[Progression] Rendered {ProgressionReportNotePlan.CountNamedXpNotes(composed)}/{notes.Count} "
                    + $"XP note(s) at budget {budget} for local report slot {playerIndex} (round {roundSequence}).");
            }
        }

        private static void ConfigureNoteSlot(TextMeshProUGUI slot)
        {
            if (slot == null)
                return;

            // Standalone/older-Company fallback only. The paired presenter owns its
            // geometry; fallback text also keeps a hard boundary during autosizing.
            slot.richText = true;
            slot.enableWordWrapping = false;
            slot.overflowMode = TextOverflowModes.Ellipsis;
            slot.fontSizeMin = ReportNotesMinimumFontSize;
            slot.fontSizeMax = ResolveAuthoredFontSize(slot);
            slot.enableAutoSizing = true;
        }

        /// <summary>
        /// Returns the slot's authored font size, caching it the first time this session so the
        /// autosized value never becomes the next round's ceiling.
        /// </summary>
        private static float ResolveAuthoredFontSize(TextMeshProUGUI slot)
        {
            int id = slot.GetInstanceID();
            if (_authoredFontSizes.TryGetValue(id, out float cached))
                return cached;

            // If something already enabled autosizing on this slot, its own ceiling is the last
            // authored value available; plain fontSize would only report the fitted size.
            float authored = Mathf.Max(
                ReportNotesMinimumFontSize,
                slot.enableAutoSizing ? slot.fontSizeMax : slot.fontSize);
            _authoredFontSizes[id] = authored;
            return authored;
        }

        /// <summary>
        /// True when the RENDERED composition does not fit the slot: more lines than the budget,
        /// text TMP itself cut (overflow index or ellipsis/truncation), or visible glyph bounds
        /// larger than the rect minus margins. #493: preferredWidth/preferredHeight are not used
        /// because TMP measures them at fontSizeMax whenever autosizing is on, so any line wider
        /// than the slot at full size read as overflow even after autosizing shrank it to fit.
        /// After ForceMeshUpdate, a line that still overflows at fontSizeMin is cut by the
        /// Ellipsis mode, which sets isTextTruncated, so the floor case still shrinks the budget.
        /// </summary>
        private static bool OverflowsNoteSlot(TextMeshProUGUI slot, int budget)
        {
            RectTransform rect = slot.rectTransform;
            if (rect == null)
                return false;

            float available = rect.rect.height;
            // A slot whose layout has not resolved yet measures nothing useful; trust the
            // logical budget rather than shrinking the notes on a bogus measurement.
            if (available <= 0f)
                return false;

            if (slot.textInfo != null && slot.textInfo.lineCount > budget)
                return true;
            if (slot.isTextOverflowing || slot.isTextTruncated)
                return true;

            // With no visible character TMP returns inverted extents (negative size), which can
            // never exceed the rect, so an empty slot cannot flag here.
            Vector3 rendered = slot.textBounds.size;
            float availableWidth = rect.rect.width - slot.margin.x - slot.margin.z;
            float availableHeight = available - slot.margin.y - slot.margin.w;
            return rendered.y > availableHeight + ReportNotesFitTolerance
                || rendered.x > availableWidth + ReportNotesFitTolerance;
        }

        internal static int GetLocalPlayerIndex()
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local == null)
                return -1;

            ulong id = local.playerClientId;
            return id > int.MaxValue ? -1 : (int)id;
        }

        private static void DestroyLegacyOverlayLines()
        {
            RectTransform[] rects = Resources.FindObjectsOfTypeAll<RectTransform>();
            for (int i = 0; i < rects.Length; i++)
            {
                RectTransform rect = rects[i];
                if (rect == null || rect.gameObject == null)
                    continue;
                if (Array.IndexOf(LegacyLineNames, rect.gameObject.name) < 0)
                    continue;

                string name = rect.gameObject.name;
                rect.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(rect.gameObject);
                Plugin.Log?.LogInfo($"[Progression] Removed legacy report overlay line '{name}'.");
            }
        }
    }
}
