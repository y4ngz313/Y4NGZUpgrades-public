using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZUpgrades.UITheme;
using Y4NGZUpgrades.Config;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Gui
{
    internal sealed class Plugin : MonoBehaviour
    {
        public static ManualLogSource CustomLogger;
        internal static Y4NGZConfigScope ConfigScope;
        internal static IngameKeybinds Keybinds;
        internal static Plugin Instance { get; private set; }

        public static ConfigEntry<bool> onlyInOrbit;
        public static ConfigEntry<bool> onlyOnShip;
        public static ConfigEntry<bool> extendedLog;
        public static ConfigEntry<int> SuitPrice;
        public static ConfigEntry<int> CosmeticPrice;
        public static ConfigEntry<float> MenuSoundVolume;
        public static ConfigEntry<float> MenuScale;
        public static ConfigEntry<bool> ReduceMenuMotion;
        public static ConfigEntry<LguUpgradeLayout> LguUpgradeLayout;

        /// <summary>
        /// Whether the catalog pass registers imported rows in the flat Augments catalog. The
        /// configured layout decides, except that LGU-only mode with Late Game Upgrades integrated
        /// always uses Augments: the catalog publishes the resolved policy before reading this.
        /// </summary>
        internal static bool UseSeparateLguCatalog =>
            LguCatalogLayout.UsesFlatCatalog(
                LguUpgradeLayout?.Value ?? LguCatalogLayout.DefaultLayout,
                Y4NGZUpgradeManager.CurrentPolicy.NativeCatalogHidden);

        private int _lastPressFrame = -1;

        internal static void Initialize(Y4NGZConfigScope config, ManualLogSource logger, MonoBehaviour host)
        {
            if (host == null)
                return;

            CustomLogger = logger;
            ConfigScope = config;

            Plugin component = host.GetComponent<Plugin>();
            if (component == null)
                component = host.gameObject.AddComponent<Plugin>();

            Instance = component;
            component.Configure();
        }

        internal static void Shutdown()
        {
            Y4NGZUpgrades.Patches.ExtraSlotManager.UnbindHotbarActions(Keybinds);

            Keybinds = null;
            Instance = null;
        }

        private void Configure()
        {
            ConfigSetup();

            if (Keybinds == null)
            {
                Keybinds = new IngameKeybinds();
                Y4NGZUpgrades.Patches.ExtraSlotManager.BindHotbarActions(Keybinds);
            }

            // Warm the authored menu sound set well before the first P press so
            // the opening click is never the fallback clip.
            MenuAudio.Preload();

            if (GameObject.Find("Y4NGZ_PlayerMenuHintHost") == null)
            {
                var hintHost = new GameObject("Y4NGZ_PlayerMenuHintHost");
                DontDestroyOnLoad(hintHost);
                hintHost.AddComponent<PlayerMenuHint>();
            }

            CustomLogger?.LogInfo("Y4NGZ player menu host initialized.");
        }

        public static void ExtendedLogging(string msg, LogLevel level = LogLevel.Info)
        {
            if (extendedLog != null && extendedLog.Value)
                CustomLogger?.Log(level, msg);
        }


        public void ConfigSetup()
        {
            onlyInOrbit = Y4NGZConfigFiles.BindMigrated(
                ConfigScope,
                "Availability",
                "Only In Orbit",
                false,
                "Allow the player menu only while the ship is in orbit.",
                new LegacyConfigKey("Player Menu", "Only In Orbit"));
            onlyOnShip = Y4NGZConfigFiles.BindMigrated(
                ConfigScope,
                "Availability",
                "Only On Ship",
                true,
                "Allow the player menu only while the local player is aboard the ship.",
                new LegacyConfigKey("Player Menu", "Only On Ship"));
            extendedLog = Y4NGZConfigFiles.BindMigrated(
                ConfigScope,
                "Troubleshooting",
                "Extended Logging",
                false,
                "Write extra player-menu diagnostics to the BepInEx log.",
                new LegacyConfigKey("Player Menu", "Extended Logging"));

            SuitPrice = Y4NGZConfigFiles.BindMigrated(
                ConfigScope,
                "Appearance Prices",
                "Suit Cost",
                5,
                new ConfigDescription(
                    "Upgrade-token cost to unlock one suit.",
                    new AcceptableValueRange<int>(0, 999)),
                new LegacyConfigKey("Player Cosmetics - Prices", "Suit Price"));
            CosmeticPrice = Y4NGZConfigFiles.BindMigrated(
                ConfigScope,
                "Appearance Prices",
                "Cosmetic Cost",
                3,
                new ConfigDescription(
                    "Upgrade-token cost to unlock one MoreCompany cosmetic.",
                    new AcceptableValueRange<int>(0, 999)),
                new LegacyConfigKey("Player Cosmetics - Prices", "Cosmetic Price"));

            MenuSoundVolume = Y4NGZConfigFiles.BindMigrated(
                ConfigScope,
                "Appearance",
                "Sound Volume",
                1f,
                new ConfigDescription(
                    "Volume multiplier for player-menu clicks, section changes, purchases, and exit sounds.",
                    new AcceptableValueRange<float>(0f, 1f)),
                new LegacyConfigKey("Player Menu", "Menu Sound Volume"));
            MenuScale = Y4NGZConfigFiles.BindMigrated(
                ConfigScope,
                "Appearance",
                "Menu Scale",
                1f,
                new ConfigDescription(
                    "Size multiplier for the EMPLOYEE FILE menu, limited to the available screen area.",
                    new AcceptableValueRange<float>(0.75f, 1.5f)),
                new LegacyConfigKey("Player Menu", "Menu Scale"));

            ReduceMenuMotion = Y4NGZConfigFiles.BindMigrated(
                ConfigScope, "Appearance", "Reduce Motion", false,
                new ConfigDescription("Show menu state changes immediately without purchase highlights or menu fades."));

            // #435: lives in [LGU] with the imported rows. An [LGU] scope reads [General] and then
            // this binding's old [Player Menu] key, so either earlier value carries over. #493
            // moved the default to SeparateCatalog; the one-time migration below moves a profile
            // still holding the old ClassTrees default before anything reads it.
            LguUpgradeLayout = Y4NGZConfigFiles.BindMigrated(
                Y4NGZConfigFiles.Lgu.MovedFrom(ConfigScope),
                "Appearance", "LGU Upgrade Layout", LguCatalogLayout.DefaultLayout,
                new ConfigDescription(
                    "Where imported Late Game Upgrades rows appear. Default: SeparateCatalog, one "
                    + "flat Augments catalog with no branch lines and no class-investment gates; "
                    + "the rows there do not count toward class investment, so they can be bought "
                    + "earlier if you can afford them. ClassTrees keeps them in the four class "
                    + "trees. Takes effect on restart. When LGU Upgrade Mode is LguOnly and Late "
                    + "Game Upgrades is installed, every upgrade on sale is an imported row, so they "
                    + "always use the Augments catalog and the native Upgrades tab is hidden, "
                    + "regardless of this setting. Does nothing while Integrate LGU Into Player "
                    + "Menu is off."));
            Y4NGZConfigFiles.MigrateLguLayoutDefault(LguUpgradeLayout);

            if (SuitPrice.Value == 50)
                SuitPrice.Value = 5;
            if (CosmeticPrice.Value == 25)
                CosmeticPrice.Value = 3;
        }

        public void tryShowMenu()
        {
            if (!CanOpenPurchaseMenu(out string reason))
            {
                ExtendedLogging($"[PMenu] P press ignored: {reason}");
                return;
            }

            try
            {
                MenuAudio.Preload();
                PurchaseMenu.initMenu();
                UiTheme.Refresh();
            }
            catch (Exception e)
            {
                CustomLogger?.LogError($"[PMenu] Failed to open purchase menu: {e}");
            }
        }

        internal static bool CanOpenPurchaseMenu(out string reason)
        {
            reason = string.Empty;

            if (MenuController.IsOpen)
            {
                reason = "menu already open";
                return false;
            }

            StartOfRound round = StartOfRound.Instance;
            if (round == null)
            {
                reason = "StartOfRound unavailable";
                return false;
            }

            PlayerControllerB player = round.localPlayerController;
            if (player == null)
            {
                reason = "local player unavailable";
                return false;
            }

            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
            {
                reason = "quick menu open";
                return false;
            }

            if (onlyInOrbit != null && onlyInOrbit.Value && !round.inShipPhase)
            {
                reason = "only-in-orbit enabled while not in orbit";
                return false;
            }

            if (onlyOnShip != null && onlyOnShip.Value && !IsPlayerShipSide(round, player))
            {
                reason = "only-on-ship enabled while not ship-side";
                return false;
            }

            if (player.inTerminalMenu)
            {
                reason = "terminal open";
                return false;
            }

            if (player.isTypingChat)
            {
                reason = "chat typing";
                return false;
            }

            return true;
        }

        private static bool IsPlayerShipSide(StartOfRound round, PlayerControllerB player)
        {
            if (round == null || player == null)
                return false;
            if (round.inShipPhase || player.isInHangarShipRoom || player.isInElevator)
                return true;

            try
            {
                Collider shipBounds = round.shipBounds;
                return shipBounds != null && shipBounds.bounds.Contains(player.transform.position + Vector3.up * 0.25f);
            }
            catch
            {
                return false;
            }
        }

        private void Update()
        {
            if (!UpgradeInput.WasPressed(Keybinds?.PurchaseMenu))
                return;
            if (_lastPressFrame == Time.frameCount)
                return;

            _lastPressFrame = Time.frameCount;
            tryShowMenu();
        }
    }
}
