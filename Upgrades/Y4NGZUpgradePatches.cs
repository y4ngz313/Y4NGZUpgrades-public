using System;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZUpgrades.Upgrades
{
    [HarmonyPatch]
    internal static class Y4NGZUpgradePatches
    {
        /// <summary>
        /// Resolving a save key reaches ES3 through reflection, so the retry below cannot run every
        /// frame. It reacts immediately to a save-scope change and otherwise polls slowly.
        /// </summary>
        private const float RepointRetrySeconds = 2f;

        private static string _lastRetryScope;
        private static float _nextRetryAt;

        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPostfix]
        private static void PostStartOfRoundStart()
        {
            if (Y4NGZUpgradeManager.SwitchToCurrentSave())
                return;

            // F-INFRA-2: on a joining client the host save identity has not arrived yet, so this
            // used to fail silently and every accessor read level 0 for the rest of the lobby.
            Plugin.Log?.LogWarning(
                "[Y4NGZUpgrades] No save scope resolved at StartOfRound.Start; every upgrade reads "
                + "level 0 until one does. Retrying from StartOfRound.Update.");
            _lastRetryScope = null;
            _nextRetryAt = 0f;
        }

        /// <summary>
        /// F-INFRA-2. The only re-points that existed were the host-identity handoff and a
        /// purchase, so a client that never got an identity (vanilla host, older build) - or a host
        /// whose ES3 file did not exist yet at <c>Start</c> - stayed unpointed for the session.
        /// </summary>
        [HarmonyPatch(typeof(StartOfRound), "Update")]
        [HarmonyPostfix]
        private static void PostStartOfRoundUpdate()
        {
            if (Y4NGZUpgradeManager.HasCurrentSave)
            {
                _lastRetryScope = null;
                _nextRetryAt = 0f;
                return;
            }

            string scope = HostSaveIdentity.CurrentHostSaveKey
                ?? GameNetworkManager.Instance?.currentSaveFileName;
            bool scopeChanged = !string.Equals(scope, _lastRetryScope, StringComparison.Ordinal);
            if (!scopeChanged && Time.unscaledTime < _nextRetryAt)
                return;

            _lastRetryScope = scope;
            _nextRetryAt = Time.unscaledTime + RepointRetrySeconds;
            Y4NGZUpgradeManager.SwitchToCurrentSave();
        }
    }
}
