using System;
using GameNetcodeStuff;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Feeds the Chameleon detection-time multiplier into Y4NGZCompany's CCTV security
    /// sweep. Detection runs on the server, so remote players' tiers come from
    /// <see cref="UpgradeTierSync"/>; the local player's tier is read directly.
    /// </summary>
    internal static class ChameleonCompanyPatch
    {
        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized)
                return;

            // CctvSupportApi moved from Y4NGZCompany.dll to LethalCCTV.dll (Y4NGZCompany#393).
            // The namespace did not change, but the plugin that owns it did, and com.y4ngz.company
            // can now be installed without the CCTV plugin — so gate on the CCTV GUID.
            if (!OptionalPluginCapabilities.LethalCctv)
            {
                Plugin.Log?.LogInfo("Chameleon: LethalCCTV not present; camera-detection integration disabled.");
                return;
            }

            try
            {
                InstallProvider();
                _initialized = true;
                Plugin.Log?.LogInfo("Chameleon: camera-detection multiplier provider installed.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Chameleon: failed to install camera-detection provider: {e.Message}");
            }
        }

        private static void InstallProvider()
        {
            if (!OptionalCctvBridge.InstallDetectionTimeMultiplierProvider(GetDetectionTimeMultiplier))
                throw new MissingMemberException("LethalCCTV detection-time provider API was not found.");
        }

        private static float GetDetectionTimeMultiplier(PlayerControllerB player)
        {
            if (player == null)
                return 1f;

            PlayerControllerB local = StartOfRound.Instance?.localPlayerController;
            int tier = local != null && local.actualClientId == player.actualClientId
                ? ChameleonUpgrade.GetTier()
                : UpgradeTierSync.GetTier(player.actualClientId, ChameleonUpgrade.UPGRADE_ID);

            return ChameleonUpgrade.GetDetectionTimeMultiplier(tier);
        }
    }
}
