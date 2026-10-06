using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using Y4NGZUpgrades.Gui;
using Y4NGZUpgrades.HUD;

namespace Y4NGZUpgrades.Interactive.Hud
{
    /// <summary>
    /// Name-only producer bridge into Y4NGZUI's prompt composer. Each call is routed by its own
    /// resolved MethodInfo: when Y4NGZUI exposes that method it owns the call; otherwise the
    /// persistent-prompt and post-player-menu calls fall back to <see cref="UpgradeHUDManager"/>
    /// and the rest (theme, visibility, cursor tip, providers) degrade to a safe no-op.
    /// </summary>
    internal static class Y4ngzPromptOverlay
    {
        private const string ExternalTypeName = "Y4NGZUI.Y4ngzPromptOverlay, Y4NGZUI";

        private static Type _externalType;
        private static MethodInfo _refreshTheme;
        private static MethodInfo _setPresentationVisible;
        private static MethodInfo _setPersistentPrompt;
        private static MethodInfo _clearPersistentPrompt;
        private static MethodInfo _setPostPlayerMenuPrompt;
        private static MethodInfo _setPostPlayerMenuPromptLines;
        private static MethodInfo _clearPostPlayerMenuPrompt;
        private static MethodInfo _setCursorTipOverride;
        private static MethodInfo _setPlayerMenuPromptProvider;
        private static MethodInfo _setHeldItemPromptSuppressionProbe;

        private static readonly Func<string> PlayerMenuPromptProvider = ResolvePlayerMenuPrompt;
        private static readonly Func<GrabbableObject, bool> HeldItemSuppressionProbe =
            ArmoryItemProbe.IsArmoryWeapon;

        internal static void Initialize()
        {
            Resolve();
            Invoke(_setPlayerMenuPromptProvider, PlayerMenuPromptProvider);
            Invoke(_setHeldItemPromptSuppressionProbe, HeldItemSuppressionProbe);
        }

        internal static void Shutdown()
        {
            Invoke(_setPlayerMenuPromptProvider, new object[] { null });
            Invoke(_setHeldItemPromptSuppressionProbe, new object[] { null });
            // F-TECH-2: the fallback renderer owns a GameObject under the player-screen canvas.
            UpgradeHUDManager.ClearAll();
            ClearResolution();
        }

        internal static void RefreshTheme()
        {
            Resolve();
            Invoke(_refreshTheme);
        }

        internal static void SetPresentationVisible(bool visible)
        {
            Resolve();
            Invoke(_setPresentationVisible, visible);
        }

        /// <summary>
        /// F-TECH-2: Y4NGZUI owns the composed prompt line when it is installed, but on a plain
        /// Upgrades install every producer call used to resolve to a null MethodInfo and vanish.
        /// The Field Mechanic hold-hacks had no other UI, so the fallback renderer in
        /// <see cref="UpgradeHUDManager"/> takes over whenever the external overlay is absent.
        /// </summary>
        internal static void SetPersistentPrompt(string key, string text)
        {
            Resolve();
            if (_setPersistentPrompt != null)
            {
                Invoke(_setPersistentPrompt, key, text);
                return;
            }

            UpgradeHUDManager.SetPersistentPrompt(key, text);
        }

        internal static void ClearPersistentPrompt(string key)
        {
            Resolve();
            if (_clearPersistentPrompt != null)
            {
                Invoke(_clearPersistentPrompt, key);
                return;
            }

            UpgradeHUDManager.ClearPersistentPrompt(key);
        }

        /// <summary>
        /// Hold-progress feedback for a persistent prompt, 0..1. Y4NGZUI has no equivalent seam,
        /// so this always runs locally: it drives the fallback prompt's own fill when that
        /// renderer is up, and the vanilla hold ring either way.
        /// </summary>
        internal static void SetPersistentPromptProgress(string key, float normalized)
        {
            UpgradeHUDManager.SetPersistentPromptProgress(key, normalized);
        }

        /// <summary>
        /// The post-player-menu calls route per method, like the persistent prompt: the probe is
        /// the MethodInfo of the call being made, not some other seam of the external type.
        /// Without Y4NGZUI the blocks land on the fallback stack in <see cref="UpgradeHUDManager"/>.
        /// </summary>
        internal static void SetPostPlayerMenuPrompt(string key, string text)
        {
            Resolve();
            if (_setPostPlayerMenuPrompt != null)
            {
                Invoke(_setPostPlayerMenuPrompt, key, text);
                return;
            }

            UpgradeHUDManager.SetPostPlayerMenuPrompt(key, text);
        }

        internal static void SetPostPlayerMenuPromptLines(string key, IReadOnlyList<string> lines)
        {
            Resolve();
            if (_setPostPlayerMenuPromptLines != null)
            {
                Invoke(_setPostPlayerMenuPromptLines, key, lines);
                return;
            }

            UpgradeHUDManager.SetPostPlayerMenuPromptLines(key, lines);
        }

        internal static void ClearPostPlayerMenuPrompt(string key)
        {
            Resolve();
            Invoke(_clearPostPlayerMenuPrompt, key);
            // Always cleared locally as well: if only one of the setters resolved, the key may
            // live on the fallback stack. A key the fallback never held is a dictionary miss.
            UpgradeHUDManager.ClearPostPlayerMenuPrompt(key);
        }

        /// <summary>
        /// Keeps a key's block from fading on the fallback stack while it has lines. Fallback only,
        /// with no Y4NGZUI seam: Y4NGZUI's E12 rule already holds every post-player-menu block
        /// open, so there is nothing to forward. The fallback otherwise fades all content after
        /// three seconds (#153); this exempts the held Field Tablet alone.
        /// </summary>
        internal static void SetPostPlayerMenuPromptHoldOpen(string key, bool holdOpen)
        {
            UpgradeHUDManager.SetPostPlayerMenuPromptHoldOpen(key, holdOpen);
        }

        internal static void SetCursorTipOverride(string text)
        {
            Resolve();
            Invoke(_setCursorTipOverride, text);
        }

        private static void Resolve()
        {
            if (_externalType != null || !OptionalPluginCapabilities.Y4NGZUi)
                return;

            _externalType = Type.GetType(ExternalTypeName, throwOnError: false);
            if (_externalType == null)
                return;

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public;
            _refreshTheme = _externalType.GetMethod("RefreshTheme", flags);
            _setPresentationVisible = _externalType.GetMethod("SetPresentationVisible", flags);
            _setPersistentPrompt = _externalType.GetMethod("SetPersistentPrompt", flags);
            _clearPersistentPrompt = _externalType.GetMethod("ClearPersistentPrompt", flags);
            _setPostPlayerMenuPrompt = _externalType.GetMethod("SetPostPlayerMenuPrompt", flags);
            _setPostPlayerMenuPromptLines =
                _externalType.GetMethod("SetPostPlayerMenuPromptLines", flags);
            _clearPostPlayerMenuPrompt = _externalType.GetMethod("ClearPostPlayerMenuPrompt", flags);
            _setCursorTipOverride = _externalType.GetMethod("SetCursorTipOverride", flags);
            _setPlayerMenuPromptProvider =
                _externalType.GetMethod("SetPlayerMenuPromptProvider", flags);
            _setHeldItemPromptSuppressionProbe =
                _externalType.GetMethod("SetHeldItemPromptSuppressionProbe", flags);
        }

        private static void ClearResolution()
        {
            _externalType = null;
            _refreshTheme = null;
            _setPresentationVisible = null;
            _setPersistentPrompt = null;
            _clearPersistentPrompt = null;
            _setPostPlayerMenuPrompt = null;
            _setPostPlayerMenuPromptLines = null;
            _clearPostPlayerMenuPrompt = null;
            _setCursorTipOverride = null;
            _setPlayerMenuPromptProvider = null;
            _setHeldItemPromptSuppressionProbe = null;
        }

        private static void Invoke(MethodInfo method, params object[] arguments)
        {
            if (method == null)
                return;
            try { method.Invoke(null, arguments); }
            catch { }
        }

        private static string ResolvePlayerMenuPrompt()
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null)
                return null;

            bool shipSide = player.isInHangarShipRoom
                            || player.isInElevator
                            || StartOfRound.Instance?.inShipPhase == true;
            if (!shipSide || player.inTerminalMenu || player.isTypingChat)
                return null;

            return "Player Menu: [" + UpgradeInput.DisplayLabel(
                Y4NGZUpgrades.Gui.Plugin.Keybinds?.PurchaseMenu, "P") + "]";
        }
    }
}
