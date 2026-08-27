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
        private const float ReportNotesMinimumFontSize = 12f;
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
                || !ProgressionManager.TryGetLastRoundReportSources(
                    out IReadOnlyList<ProgressionReportSource> sources))
            {
                return;
            }

            IReadOnlyList<string> notes = ProgressionReportNotePlan.BuildXpNotes(
                sources,
                breakdown.RoundSequence,
                maxNotes: 3,
                xpColorHex: ResolveXpNoteColorHex());
            if (notes.Count == 0)
                return;

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

        private static IEnumerator ApplyAfterDelayedWriters(
            HUDManager hud,
            int roundSequence,
            IReadOnlyList<string> notes)
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
            ApplyNow(hud, roundSequence, notes);
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

            TextMeshProUGUI slot = slots[playerIndex];
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            bool preserveLastCoronerLine =
                OptionalPluginCapabilities.Coroner && local != null && local.isPlayerDead;
            string composed = ProgressionReportNotePlan.ComposeNotes(
                slot.text,
                notes,
                maxLines: 3,
                preferLastForeign: preserveLastCoronerLine);
            composed = composed.TrimEnd('\r', '\n');
            ConfigureNoteSlot(slot);
            if (!string.Equals(slot.text, composed, StringComparison.Ordinal))
                slot.text = composed;
            slot.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);

            if (_lastLoggedRound != roundSequence)
            {
                _lastLoggedRound = roundSequence;
                Plugin.Log?.LogInfo(
                    $"[Progression] Rendered {notes.Count} ranked XP note candidate(s) "
                    + $"for local report slot {playerIndex} (round {roundSequence}).");
            }
        }

        private static void ConfigureNoteSlot(TextMeshProUGUI slot)
        {
            if (slot == null)
                return;

            // The vanilla slot has room for the Notes heading plus three compact rows. Keeping
            // each logical note to one rendered line prevents a wrapped XP suffix from consuming
            // the next player's row; autosizing then fits both the widest note and all four rows.
            float authoredFontSize = Mathf.Max(ReportNotesMinimumFontSize, slot.fontSize);
            slot.richText = true;
            slot.enableWordWrapping = false;
            slot.overflowMode = TextOverflowModes.Overflow;
            slot.enableAutoSizing = true;
            slot.fontSizeMin = ReportNotesMinimumFontSize;
            slot.fontSizeMax = authoredFontSize;
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
