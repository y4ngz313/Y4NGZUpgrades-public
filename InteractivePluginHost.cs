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


        public static ConfigEntry<bool> WorldFeedbackEnabled;
        // 'Interaction Reticle Enabled' stays BetterArmory-owned: its provider supplies the value
        // to Y4NGZUI, while the retained local renderer reads it on standalone installs.
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
            Y4ngzPromptOverlay.Initialize();
            GameplayHudMotion.Initialize();
            // The local facade owns standalone defaults, probes Y4NGZUI first, and retains the
            // Contracted facade as compatibility fallback. This side registers the gameplay hook
            // that tints protected hotbar slots.
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
            // BetterArmory shuts down its provider/fallback renderer, classifier, and signal
            // bridge. Upgrades owns only the prompt/danger producer bridges here.
            OptionalDangerHudBridge.Shutdown();
            Y4ngzPromptOverlay.Shutdown();
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
            // The ammo counter and selected reticle renderer also follow BetterArmory's poll of the
            // Y4NGZUI-first visibility bridge (#266, #267).
            OptionalDangerHudBridge.SetPresentationVisible(visible);
            CommandNetPatch.SetPresentationVisible(visible);
        }

        private static void OnHudMotionSampled(Vector2 offset)
        {
            CommandNetPatch.SetHudMotionOffset(offset);
        }

    }
}
