using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using Y4NGZUpgrades.Interactive.Feedback;
using Y4NGZUpgrades.HUD;
using Y4NGZUpgrades.Interactive.Hud;
using Y4NGZUpgrades.Patches;
using Y4NGZUpgrades.UITheme;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Interactive
{
    public static class Plugin
    {
        public const string ModuleName = "Y4NGZUpgrades Interactive Systems";

        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<bool> CombatFeedbackEnabled;
        public static ConfigEntry<bool> CombatFeedbackHitMarkerEnabled;
        public static ConfigEntry<bool> CombatFeedbackHitSoundEnabled;
        public static ConfigEntry<bool> CombatFeedbackEnemyFlashEnabled;
        public static ConfigEntry<bool> CombatFeedbackEnemyImpactLightEnabled;
        // 'Weapon Crosshair Enabled' and 'Weapon Fire Pulse Enabled' moved to BetterArmory with
        // the crosshair (#266), under the same section and key names.
        // 'Enemy Impact VFX Enabled' was removed with EnemyHitFlash's particle burst (#125, 2026-08-03).
        // The position-accurate creature impact in WeaponImpactEffects replaces it.
        public static ConfigEntry<bool> CombatFeedbackEnemyImpactSoundEnabled;

        // The whole 'Crosshair' section moved to BetterArmory with the crosshair (#266).

        public static ConfigEntry<bool> DangerFeedbackEnabled;
        public static ConfigEntry<bool> DangerFeedbackDirectionalDamageEnabled;
        public static ConfigEntry<bool> DangerFeedbackLowHealthEnabled;
        public static ConfigEntry<bool> DangerFeedbackHeartbeatEnabled;
        public static ConfigEntry<int> DangerFeedbackLowHealthThreshold;

        public static ConfigEntry<bool> WorldFeedbackEnabled;
        // 'Interaction Reticle Enabled' moved to BetterArmory: the interaction cue is drawn by
        // CombatReticleHud, which is the same object as the weapon crosshair (#266).
        public static ConfigEntry<bool> WorldFeedbackScrapPickupEnabled;
        public static ConfigEntry<bool> WorldFeedbackHighValueGlintEnabled;
        public static ConfigEntry<bool> WorldFeedbackNearMissWhooshEnabled;
        public static ConfigEntry<bool> WorldFeedbackScanEnhancementsEnabled;

        private static bool _initialized;

        internal static void Initialize(ManualLogSource log, MonoBehaviour coroutineHost)
        {
            if (_initialized)
                return;

            _initialized = true;
            Log = log;
            UiTheme.Initialize();
            GameplayUiVisibility.Initialize();
            GameplayHudMotion.Initialize();
            // The local facade owns the standalone defaults and mirrors the
            // Contracted theme when that optional provider is present. This side
            // registers the gameplay hook that tints protected hotbar slots.
            UiTheme.ProtectedSlotResolver = slot =>
                DeathboundUpgrade.IsUnlocked() && slot == DeathboundUpgrade.PROTECTED_SLOT_INDEX;
            UiTheme.ThemeChanged += OnThemeChanged;
            GameplayUiVisibility.VisibilityChanged += OnGameplayUiVisibilityChanged;
            GameplayHudMotion.MotionSampled += OnHudMotionSampled;
            OnThemeChanged(UiTheme.CurrentPreset);
            OnGameplayUiVisibilityChanged(GameplayUiVisibility.IsVisible);
            OnHudMotionSampled(GameplayHudMotion.CurrentOffset);
            Log?.LogInfo($"{ModuleName} initialized.");
        }

        internal static void Shutdown()
        {
            // The reticle, its classifier and the crosshair signal bridge shut down inside
            // BetterArmory now (#266).
            DirectionalDamageHud.DestroyInstance();
            PlayerDangerHud.DestroyInstance();
            CommandNetPatch.ShutdownPresentation();
            UiTheme.ThemeChanged -= OnThemeChanged;
            GameplayUiVisibility.VisibilityChanged -= OnGameplayUiVisibilityChanged;
            GameplayHudMotion.MotionSampled -= OnHudMotionSampled;
            UiTheme.ProtectedSlotResolver = null;
            GameplayHudMotion.Shutdown();
            GameplayUiVisibility.Shutdown();
            UiTheme.Shutdown();
            _initialized = false;
        }

        private static void OnThemeChanged(HudColorPreset preset)
        {
            Y4ngzPromptOverlay.RefreshTheme();
            UpgradeHUDManager.RefreshTheme();
            CommandNetPatch.RefreshPresentationTheme();
        }

        private static void OnGameplayUiVisibilityChanged(bool visible)
        {
            Y4ngzPromptOverlay.SetPresentationVisible(visible);
            // The ammo counter and the weapon reticle follow this from BetterArmory's own poll of
            // GameplayUiVisibility across its theming bridge (#266, #267) rather than from here.
            DirectionalDamageHud.SetPresentationVisible(visible);
            PlayerDangerHud.SetPresentationVisible(visible);
            CommandNetPatch.SetPresentationVisible(visible);
        }

        private static void OnHudMotionSampled(Vector2 offset)
        {
            CommandNetPatch.SetHudMotionOffset(offset);
        }

    }
}
