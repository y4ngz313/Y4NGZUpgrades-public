using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZUpgrades.Config;
using Y4NGZUpgrades.Interactive.Feedback;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.y4ngz.company", BepInDependency.DependencyFlags.SoftDependency)]
    // Ship Systems owns fuel, power, layout and monitor-row integrations after Y4NGZCompany#613.
    // Keep load ordering soft so Upgrades remains independently installable.
    [BepInDependency("com.y4ngz.company.shipsystems", BepInDependency.DependencyFlags.SoftDependency)]
    // Y4NGZCompany#393 split the CCTV and blood systems out of Y4NGZCompany.dll into their own
    // plugins. They stay soft dependencies — every integration that touches them is gated on the
    // GUID — but they must be declared so BepInEx chainloads them *before* this plugin: the
    // Chameleon provider install and the Field Tablet's presence caches run in Awake and would
    // otherwise read PluginInfos before those entries exist.
    [BepInDependency("com.y4ngz.company.lethalcctv", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.y4ngz.bloodfx", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.y4ngz.betterarmory", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.rune580.LethalCompanyInputUtils", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("FlipMods.HotbarPlus", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.y4ngz.interactions", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.github.teamxiaolan.dawnlib", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("me.swipez.melonloader.morecompany", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("FlipMods.TooManyEmotes", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.y4ngz.upgrades";
        public const string Name = "Y4NGZUpgrades";
        public const string Version = "1.0.0";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        internal static ProgressionSettings ProgressionConfig { get; private set; }

        private static ConfigEntry<string> _legacyShadowStepKeybind;
        private static ConfigEntry<string> _legacyPingKeybind;
        private static ConfigEntry<string> _legacyFieldTabletKeybind;
        private static ConfigEntry<string> _legacyCommandNetKeybind;
        private static ConfigEntry<string> _legacyWorklightBeaconKeybind;
        private static ConfigEntry<string> _legacyDronePointerCommandKeybind;
        public static ConfigEntry<float> TabletPullInMeters;
        public static ConfigEntry<float> TabletPullUpMeters;
        public static ConfigEntry<float> TabletRaisedPullInMeters;
        public static ConfigEntry<float> WallMaxExtraPullMeters;
        public static ConfigEntry<float> TabletScreenOffsetX;
        public static ConfigEntry<float> TabletScreenOffsetY;
        public static ConfigEntry<float> TabletScreenWidth;
        public static ConfigEntry<float> TabletScreenHeight;
        public static ConfigEntry<bool> TabletScreenCalibration;
        public static ConfigEntry<string> SlideRestoreStateMode;
        public static ConfigEntry<bool> SlidePostRestoreDiagnostics;
        public static ConfigEntry<string> SlideRestoreCameraBaseline;
        public static ConfigEntry<bool> SlideRestoreCameraRotation;
        public static ConfigEntry<bool> SlideRestoreVisorPose;
        public static ConfigEntry<bool> RemoteSlideTerrainConformance;
        public static ConfigEntry<bool> RemoteMantleBodyGrabIk;


        // The whole Weapons/Ammo config surface moved to BetterArmory.Plugin (#266), key for key.


        public static ConfigEntry<bool> BurningStatusEnabled;
        public static ConfigEntry<bool> BurningFromFlamethrowerEnabled;
        public static ConfigEntry<float> BurningDuration;
        public static ConfigEntry<int> BurningDamagePerTick;
        public static ConfigEntry<float> BurningTickInterval;
        public static ConfigEntry<float> BurningLightIntensity;
        public static ConfigEntry<bool> BurningDebugLogging;

        public static ConfigEntry<bool> BurningRigEnabled;
        public static ConfigEntry<float> BurningRigReferenceHeight;
        public static ConfigEntry<float> BurningRigScaleMultiplier;
        public static ConfigEntry<bool> BurningRigAudioEnabled;
        public static ConfigEntry<int> BurningMaxSimultaneousLights;




        /// <summary>
        /// Set by Unity's process-level quit signal. BepInEx's manager GameObject can be destroyed
        /// during the first scene transition, so component destruction alone is not a quit signal.
        /// Static state outlives that component and lets the real quit path save and clean up later.
        /// </summary>
        private static bool _applicationQuitting;
        private static bool _shutdown;
        private static Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            _applicationQuitting = false;
            _shutdown = false;
            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;
            Y4NGZConfigFiles.Initialize(Config, Info.Metadata);
            ProgressionConfig = new ProgressionSettings(
                Y4NGZConfigFiles.Progression,
                Y4NGZConfigFiles.XpSources);
            // Hosts every session-lifetime component and coroutine that must outlive scenes, player
            // objects and BepInEx_Manager itself (#211, #313).
            Y4NGZPersistentRunner.Ensure();
            MonoBehaviour sessionHost = Y4NGZPersistentRunner.Host;
            if (sessionHost == null)
            {
                Log.LogError(
                    "[Y4NGZUpgrades] Persistent session host could not be created; falling back to " +
                    "the BepInEx plugin host. Scene-lifetime services may stop if " +
                    "HideManagerGameObject is false.");
                sessionHost = this;
            }
            BindCustomUpgradeConfig();
            Y4NGZUpgrades.Interactive.Plugin.Initialize(Logger, sessionHost);
            Y4NGZUpgrades.Gui.Plugin.Initialize(Y4NGZConfigFiles.PlayerMenu, Logger, sessionHost);
            MigrateLegacyKeybinds();
            RefreshReservedKeyNames();
            Y4NGZUpgradeCatalog.RegisterDefaults();
            Effects.MovementPlayerAnimationRuntimeAssets.Prewarm();

            ProgressionManager.Load();
            Lucky8.Lucky8Manager.Initialize(Y4NGZConfigFiles.Lucky8);
            Y4NGZConfigFiles.CompleteMigration();
            EffectiveConfigInventory.Write(Y4NGZConfigFiles.All);
            ProgressionXpBarUi.Initialize(sessionHost);
            Y4NGZUpgradeManager.Load();
            SaveDataLifecycle.ReconcileOrphans();

            // Must run before PatchAll: RoundLifecycle's hooks can fire as soon as they exist.
            RoundLifecycle.Install();
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(ProgressionPatches).Assembly);
            Y4NGZUpgrades.Patches.ExtraSlotManager.Initialize();
            Y4NGZUpgrades.Patches.FieldOperationsTabletPatch.Initialize();
            Y4NGZUpgrades.Patches.ChameleonCompanyPatch.Initialize();
            Y4NGZUpgrades.Patches.ScavengerCompanyPatch.Initialize();
            Y4NGZUpgrades.Patches.QuotaGuardCompanyPatch.Initialize();

            // The entire weapon block - dev spawn hosts, creature blood, impact tints, VFX
            // diagnostics, ballistics toggle, bullet-hole decals, visual/FP-prop config and weapon
            // registration - moved to BetterArmory.Plugin.Awake (#266).

            Log.LogMessage($"{Name} v{Version} loaded. Native progression enabled.");
        }

        /// <summary>
        /// Component destruction is incidental when BepInEx runs with its raw
        /// HideManagerGameObject=false default. Preserve the session in that case; the static
        /// Application.quitting subscription remains alive and performs real teardown at process
        /// exit even though this component is already gone (#313).
        /// </summary>
        private void OnDestroy()
        {
            if (!_applicationQuitting)
            {
                Log?.LogWarning(
                    "[Y4NGZUpgrades] The plugin host GameObject was destroyed mid-session (BepInEx " +
                    "[Chainloader] HideManagerGameObject is false). Harmony patches, keybinds and " +
                    "session services remain ACTIVE; cleanup is deferred until application quit.");
                return;
            }

            Shutdown();
        }

        private static void OnApplicationQuitting()
        {
            _applicationQuitting = true;
            Shutdown();
        }

        private static void Shutdown()
        {
            if (_shutdown)
                return;

            _shutdown = true;
            Application.quitting -= OnApplicationQuitting;

            // Keep every step independent. A best-effort presentation cleanup must never prevent
            // either save from reaching disk during the one real application-quit signal.
            RunShutdownStep("LUCKY-8", Lucky8.Lucky8Manager.Shutdown);
            RunShutdownStep("progression save", ProgressionManager.Save);
            RunShutdownStep("upgrade save", Y4NGZUpgradeManager.Save);
            RunShutdownStep(
                "Field Operations tablet",
                Y4NGZUpgrades.Patches.FieldOperationsTabletPatch.Shutdown);
            RunShutdownStep("extra slots", Y4NGZUpgrades.Patches.ExtraSlotManager.Shutdown);
            RunShutdownStep("player menu", Y4NGZUpgrades.Gui.Plugin.Shutdown);
            RunShutdownStep("interactive systems", Y4NGZUpgrades.Interactive.Plugin.Shutdown);

            Harmony harmony = _harmony;
            _harmony = null;
            if (harmony != null)
                RunShutdownStep("Harmony patches", harmony.UnpatchSelf);
        }

        private static void RunShutdownStep(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Log?.LogError($"[Y4NGZUpgrades] Application-quit {name} teardown failed safely: {exception}");
            }
        }

        public static float GetTabletPullInMeters() => ClampConfig(TabletPullInMeters, 0.10f, 0f, 0.25f);
        public static float GetTabletPullUpMeters() => ClampConfig(TabletPullUpMeters, 0.02f, 0f, 0.25f);
        public static float GetTabletRaisedPullInMeters() => ClampConfig(TabletRaisedPullInMeters, 0.05f, 0f, 0.25f);
        public static float GetWallMaxExtraPullMeters() => ClampConfig(WallMaxExtraPullMeters, 0.18f, 0f, 0.25f);
        // Tablet screen aperture. The authored mesh has one material and no screen submesh, so the
        // render-texture quad's rect cannot be measured at runtime; it is calibrated in game and baked
        // as the defaults below. Live-applied so it can be nudged without a rebuild.
        public static float GetTabletScreenOffsetX() => ClampConfig(TabletScreenOffsetX, 0.01229f, -0.20f, 0.20f);
        public static float GetTabletScreenOffsetY() => ClampConfig(TabletScreenOffsetY, 0.108905f, -0.20f, 0.40f);
        public static float GetTabletScreenWidth() => ClampConfig(TabletScreenWidth, 0.21328f, 0.04f, 0.60f);
        public static float GetTabletScreenHeight() => ClampConfig(TabletScreenHeight, 0.14803f, 0.03f, 0.40f);
        public static bool GetTabletScreenCalibration() => TabletScreenCalibration != null && TabletScreenCalibration.Value;
        private void BindCustomUpgradeConfig()
        {
            _legacyShadowStepKeybind = BindUpgradeSetting(
                "shadow_step", "Controls", "Activation Key", "g",
                "Key used to activate Shadow Step at level 3. Use a Unity Input System key name.",
                "Keybinds", "Shadow Step Key");
            _legacyPingKeybind = BindUpgradeSetting(
                "ping", "Controls", "Activation Key", "q",
                "Key used to place a Foreman Ping. This is separate from the vanilla scanner input.",
                "Keybinds", "Ping Key");
            _legacyFieldTabletKeybind = BindUpgradeSetting(
                "field_operations", "Controls", "Tablet Toggle Key", "y",
                "Key used to open or close the Field Operations tablet.",
                "Keybinds", "Field Tablet Key");
            _legacyCommandNetKeybind = BindUpgradeSetting(
                "command_net", "Controls", "Transmit Key", "j",
                "Hold this key to transmit over Command Net's built-in walkie channel.",
                "Keybinds", "Command Net Key");

            Y4NGZConfigScope worklightConfig = Y4NGZConfigFiles.Upgrade("worklight_beacon");
            bool worklightKeyAlreadyDefined = Y4NGZConfigFiles.TargetAlreadyDefines(
                worklightConfig, "Controls", "Throw Beacon Key");
            _legacyWorklightBeaconKeybind = Y4NGZConfigFiles.BindMigrated(
                worklightConfig,
                "Controls",
                "Throw Beacon Key",
                "v",
                "Key used to throw a Worklight Beacon.",
                new LegacyConfigKey("Keybinds", "Worklight Beacon Key"));
            if (!worklightKeyAlreadyDefined
                && string.Equals(_legacyWorklightBeaconKeybind.Value?.Trim(), "r", StringComparison.OrdinalIgnoreCase)
                && (!Y4NGZConfigFiles.TryGetLegacy("Keybinds", "SchemaVersion", out int keySchema)
                    || keySchema < 2))
            {
                _legacyWorklightBeaconKeybind.Value = "v";
                Log?.LogInfo("[Config] Updated the old Worklight Beacon default from R to V to avoid weapon reload.");
            }

            _legacyDronePointerCommandKeybind = BindUpgradeSetting(
                "courier_drone", "Controls", "Pointer Command", "MiddleButton",
                "Button used for Courier Drone pointer commands. Mouse values: MiddleButton, RightButton, ForwardButton, or BackButton; keyboard values use Unity Input System key names.",
                "Courier Drone", "DronePointerCommandKeybind");

            Y4NGZConfigScope tabletConfig = Y4NGZConfigFiles.Upgrade("field_operations");
            TabletPullInMeters = BindTabletSetting(
                tabletConfig, "Tablet Placement", "Pull Toward Camera", 0.10f,
                "Regular tablet pull toward the camera, in metres.", 0f, 0.25f,
                "TabletPullInMeters");
            TabletPullUpMeters = BindTabletSetting(
                tabletConfig, "Tablet Placement", "Move Up", 0.02f,
                "Regular tablet upward camera-space offset, in metres.", 0f, 0.25f,
                "TabletPullUpMeters");
            TabletRaisedPullInMeters = BindTabletSetting(
                tabletConfig, "Tablet Placement", "Raised Pull Toward Camera", 0.05f,
                "Raised tablet pull toward the camera, in metres.", 0f, 0.25f,
                "TabletRaisedPullInMeters");
            WallMaxExtraPullMeters = BindTabletSetting(
                tabletConfig, "Tablet Placement", "Maximum Wall Avoidance Pull", 0.18f,
                "Maximum extra pull toward the camera when the tablet is near a wall, in metres.", 0f, 0.25f,
                "WallMaxExtraPullMeters");
            TabletScreenOffsetX = BindTabletSetting(
                tabletConfig, "Screen Placement", "Horizontal Offset", 0.01229f,
                "Screen overlay centre offset along the tablet's local X axis, in metres.", -0.20f, 0.20f,
                "TabletScreenOffsetX");
            TabletScreenOffsetY = BindTabletSetting(
                tabletConfig, "Screen Placement", "Vertical Offset", 0.108905f,
                "Screen overlay centre offset along the tablet's local Y axis, in metres.", -0.20f, 0.40f,
                "TabletScreenOffsetY");
            TabletScreenWidth = BindTabletSetting(
                tabletConfig, "Screen Placement", "Width", 0.21328f,
                "Screen overlay width, in metres.", 0.04f, 0.60f,
                "TabletScreenWidth");
            TabletScreenHeight = BindTabletSetting(
                tabletConfig, "Screen Placement", "Height", 0.14803f,
                "Screen overlay height, in metres.", 0.03f, 0.40f,
                "TabletScreenHeight");
            TabletScreenCalibration = Y4NGZConfigFiles.BindMigrated(
                tabletConfig,
                "Screen Placement",
                "Show Calibration Pattern",
                false,
                "Show a temporary border, corner marks, crosshair, and live measurements for aligning the tablet screen.",
                new LegacyConfigKey("Field Operations Tablet", "TabletScreenCalibration"));

            SlideRestoreStateMode = BindUpgradeSetting(
                "panic_slide", "Animation Restore", "State Mode", "fresh",
                "Vanilla Animator state restore mode after slide/mantle: fresh, crossfade, or replay. Fresh lets the restored controller enter from Entry using restored vanilla parameters.",
                "Panic Slide", "Slide Restore State Mode");
            SlidePostRestoreDiagnostics = BindUpgradeSetting(
                "panic_slide", "Animation Restore", "Post-Restore Diagnostics", true,
                "Log camera-container, gameplay-camera, and Animator layer state for the restore frame and next two frames.",
                "Panic Slide", "Slide Post-Restore Diagnostics");
            SlideRestoreCameraBaseline = BindUpgradeSetting(
                "panic_slide", "Animation Restore", "Camera Baseline", "begin",
                "Camera pose target after slide/mantle: begin restores clean pre-controller camera/container TRS with live cameraUp pitch; live keeps restore-entry pose as a diagnostic kill-switch.",
                "Panic Slide", "Slide Restore Camera Baseline");
            SlideRestoreCameraRotation = BindUpgradeSetting(
                "panic_slide", "Animation Restore", "Restore Camera Rotation", true,
                "Restore gameplay-camera and camera-container rotation using the selected camera baseline; begin keeps live cameraUp pitch with clean yaw and roll.",
                "Panic Slide", "Slide Restore Camera Rotation");
            SlideRestoreVisorPose = BindUpgradeSetting(
                "panic_slide", "Animation Restore", "Restore Visor Pose", true,
                "Preserve the local helmet visor pose and vanilla rotation-lerp state across slide/mantle Animator-controller restores.",
                "Panic Slide", "Slide Restore Visor Pose");
            RemoteSlideTerrainConformance = BindUpgradeSetting(
                "panic_slide", "Presentation", "Remote Slide Terrain Conformance", true,
                "Ground and slope-align the presentation-only body of remote players during panic slides.",
                "Panic Slide", "Remote Slide Terrain Conformance");
            RemoteMantleBodyGrabIk = BindUpgradeSetting(
                "panic_slide", "Presentation", "Remote Mantle Body Grab IK", true,
                "Plant remote mantle hands near the replicated ledge grip while preserving the authored arm bend.",
                "Panic Slide", "Remote Mantle Body Grab IK");

        }

        private static ConfigEntry<T> BindUpgradeSetting<T>(
            string upgradeId,
            string section,
            string key,
            T defaultValue,
            string description,
            string legacySection,
            string legacyKey)
        {
            return Y4NGZConfigFiles.BindMigrated(
                Y4NGZConfigFiles.Upgrade(upgradeId),
                section,
                key,
                defaultValue,
                description,
                new LegacyConfigKey(legacySection, legacyKey));
        }

        private static ConfigEntry<float> BindTabletSetting(
            Y4NGZConfigScope config,
            string section,
            string key,
            float defaultValue,
            string description,
            float minimum,
            float maximum,
            string legacyKey)
        {
            return Y4NGZConfigFiles.BindMigrated(
                config,
                section,
                key,
                defaultValue,
                new ConfigDescription(
                    description,
                    new AcceptableValueRange<float>(minimum, maximum)),
                new LegacyConfigKey("Field Operations Tablet", legacyKey));
        }

        private static float ClampConfig(ConfigEntry<float> entry, float fallback, float min, float max)
        {
            float value = entry != null ? entry.Value : fallback;
            return Mathf.Clamp(value, min, max);
        }



        private static Vector3 ParseVec(string s, Vector3 fallback)
        {
            if (string.IsNullOrWhiteSpace(s))
                return fallback;
            string[] p = s.Split(',');
            if (p.Length != 3)
                return fallback;

            var ci = System.Globalization.CultureInfo.InvariantCulture;
            const System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Float;
            if (float.TryParse(p[0].Trim(), ns, ci, out float x)
                && float.TryParse(p[1].Trim(), ns, ci, out float y)
                && float.TryParse(p[2].Trim(), ns, ci, out float z))
                return new Vector3(x, y, z);
            return fallback;
        }

        private static void MigrateLegacyKeybinds()
        {
            Gui.IngameKeybinds actions = Gui.Plugin.Keybinds;
            if (!Gui.LegacyKeybindMigration.TryMigrate(
                    actions,
                    _legacyShadowStepKeybind,
                    _legacyPingKeybind,
                    _legacyFieldTabletKeybind,
                    _legacyCommandNetKeybind,
                    _legacyWorklightBeaconKeybind,
                    _legacyDronePointerCommandKeybind))
            {
                return;
            }

            Y4NGZConfigFiles.RetireBindings(
                _legacyShadowStepKeybind,
                _legacyPingKeybind,
                _legacyFieldTabletKeybind,
                _legacyCommandNetKeybind,
                _legacyWorklightBeaconKeybind,
                _legacyDronePointerCommandKeybind);
        }

        /// <summary>
        /// Effective InputUtils keyboard paths published for Better Armory's startup conflict
        /// resolver. Rebinding in-game is an explicit player choice, so no hidden fallback key is
        /// assigned and no config value is rewritten.
        /// </summary>
        internal static string[] ReservedKeyNames { get; private set; } = new string[0];

        private static void RefreshReservedKeyNames()
        {
            var used = new System.Collections.Generic.HashSet<Key>();
            Gui.IngameKeybinds keybinds = Gui.Plugin.Keybinds;
            if (keybinds != null)
            {
                foreach (InputAction action in keybinds.GameplayReservationActions)
                    ReserveActionKeys(used, action);
            }

            var names = new string[used.Count];
            int next = 0;
            foreach (Key key in used)
                names[next++] = key.ToString();
            Array.Sort(names, StringComparer.Ordinal);
            ReservedKeyNames = names;
        }

        private static void ReserveActionKeys(System.Collections.Generic.HashSet<Key> used, InputAction action)
        {
            if (action == null)
                return;

            foreach (InputBinding binding in action.bindings)
            {
                Key? key = ParseKeyboardBindingPath(binding.effectivePath);
                if (key.HasValue)
                    used.Add(key.Value);
            }
        }

        private static Key? ParseKeyboardBindingPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            const string prefix = "<Keyboard>/";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            string control = path.Substring(prefix.Length).Trim();
            if (control.Length == 1 && control[0] >= '1' && control[0] <= '9')
                return Key.Digit1 + (control[0] - '1');
            if (control == "0")
                return Key.Digit0;

            return Enum.TryParse(control, ignoreCase: true, out Key parsed) ? parsed : (Key?)null;
        }
    }
}
