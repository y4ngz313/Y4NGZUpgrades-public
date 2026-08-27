using System;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZUpgrades.Gui;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Native Deeper Pockets inventory and HUD coordinator. Every peer owns the same seven
    /// physical positions; only the authoritative local player applies tier-based capacity.
    /// </summary>
    internal static class ExtraSlotManager
    {
        internal const int VanillaSlotCount = NativeInventoryModel.VanillaSlotCount;
        internal const int MaximumBonusSlots = NativeInventoryModel.MaximumBonusSlots;
        internal const int PhysicalSlotCount = NativeInventoryModel.PhysicalSlotCount;
        internal const int UtilitySlotIndex = NativeInventoryModel.UtilitySlotIndex;

        private static bool _compatibilityValidated;
        private static bool _compatibilityErrorLogged;
        private static bool _externalInventoryOwnerWarningLogged;
        private static bool _upgradeHooked;
        private static int _lastCapacitySignature = int.MinValue;
        private static HUDManager _layoutHud;
        private static Vector2 _layoutCenter;
        private static Vector2 _layoutStep;
        private static Vector2 _frameSize;
        private static Vector2 _iconSize;
        private static Vector3 _frameEuler;
        private static Vector3 _iconEuler;
        private static Vector3 _frameScale;
        private static Vector3 _iconScale;

        internal static bool NativeSlotsEnabled => _compatibilityValidated;

        internal static void Initialize()
        {
            if (!OptionalPluginCapabilities.NativeInventoryAvailable)
            {
                DisableForHotbarPlus();
                return;
            }

            if (!_upgradeHooked)
            {
                Y4NGZUpgradeManager.UpgradesChanged += RefreshCapacityImmediately;
                _upgradeHooked = true;
            }

            RefreshCapacityImmediately();
        }

        internal static void Shutdown()
        {
            if (_upgradeHooked)
            {
                Y4NGZUpgradeManager.UpgradesChanged -= RefreshCapacityImmediately;
                _upgradeHooked = false;
            }
            _compatibilityValidated = false;
            _compatibilityErrorLogged = false;
            _externalInventoryOwnerWarningLogged = false;

            _lastCapacitySignature = int.MinValue;
            _layoutHud = null;
        }

        internal static void DisableForHotbarPlus()
        {
            _compatibilityValidated = false;
            if (_externalInventoryOwnerWarningLogged)
                return;

            _externalInventoryOwnerWarningLogged = true;
            Plugin.Log?.LogWarning(
                "HotbarPlus detected: Y4NGZUpgrades remains active, while native Deeper Pockets " +
                "inventory expansion, HUD slots, scroll RPC replacement, and number-key handling " +
                "are disabled. Saved Deeper Pockets levels remain dormant until HotbarPlus is absent.");
        }

        internal static void SetScrollCompatibilityResult(bool valid, string reason = null)
        {
            _compatibilityValidated = valid;
            if (valid)
            {
                Plugin.Log?.LogInfo(
                    "Native Deeper Pockets enabled: v81 slot RPC contract validated (2/2 scroll calls replaced).");
                return;
            }

            if (_compatibilityErrorLogged)
                return;

            _compatibilityErrorLogged = true;
            Plugin.Log?.LogError(
                "Native Deeper Pockets disabled for compatibility. Inventories will remain at four slots. " +
                (string.IsNullOrWhiteSpace(reason) ? "The v81 slot-switch contract did not validate." : reason));
        }

        internal static int GetUnlockedSlotCount()
        {
            return NativeSlotsEnabled
                ? NativeInventoryModel.GetUnlockedSlotCount(ExtraSlotUpgrade.GetTier())
                : VanillaSlotCount;
        }

        internal static void EnsureAllPlayerInventories()
        {
            if (!NativeSlotsEnabled || StartOfRound.Instance?.allPlayerScripts == null)
                return;

            PlayerControllerB[] players = StartOfRound.Instance.allPlayerScripts;
            for (int i = 0; i < players.Length; i++)
                EnsurePhysicalInventory(players[i]);
        }

        internal static void EnsurePhysicalInventory(PlayerControllerB player)
        {
            if (!NativeSlotsEnabled || player?.ItemSlots == null || player.ItemSlots.Length >= PhysicalSlotCount)
                return;

            GrabbableObject[] expanded = new GrabbableObject[PhysicalSlotCount];
            Array.Copy(player.ItemSlots, expanded, player.ItemSlots.Length);
            player.ItemSlots = expanded;
        }

        internal static bool TryGetFirstEmptySlot(
            PlayerControllerB player,
            GrabbableObject attemptingGrab,
            out int slot)
        {
            slot = -1;
            if (!NativeSlotsEnabled || player?.ItemSlots == null)
                return false;

            EnsurePhysicalInventory(player);
            int capacity = IsAuthoritativeLocalPlayer(player) ? GetUnlockedSlotCount() : PhysicalSlotCount;
            bool utilityAvailable = player.ItemOnlySlot == null
                && attemptingGrab != null
                && attemptingGrab.itemProperties != null
                && !attemptingGrab.itemProperties.isScrap
                && !attemptingGrab.itemProperties.twoHanded
                && !attemptingGrab.itemProperties.disallowUtilitySlot;

            slot = NativeInventoryModel.FindFirstEmptySlot(
                utilityAvailable,
                player.currentItemSlot,
                capacity,
                GetOccupancyMask(player));
            return true;
        }

        internal static bool TryGetNextItemSlot(PlayerControllerB player, bool forward, out int slot)
        {
            slot = -1;
            if (!NativeSlotsEnabled || !IsAuthoritativeLocalPlayer(player) || player.ItemSlots == null)
                return false;

            slot = NativeInventoryModel.FindNextSelectableSlot(
                player.currentItemSlot,
                forward,
                GetUnlockedSlotCount(),
                GetOccupancyMask(player));
            return true;
        }

        internal static void AllocateHudSlots(HUDManager hud)
        {
            if (!NativeSlotsEnabled || hud?.itemSlotIconFrames == null || hud.itemSlotIcons == null)
                return;
            if (hud.itemSlotIconFrames.Length < VanillaSlotCount
                || hud.itemSlotIcons.Length < VanillaSlotCount)
            {
                Plugin.Log?.LogError("Native Deeper Pockets could not allocate HUD slots: vanilla templates were unavailable.");
                return;
            }

            CaptureVanillaLayout(hud);
            if (hud.itemSlotIconFrames.Length >= PhysicalSlotCount
                && hud.itemSlotIcons.Length >= PhysicalSlotCount)
            {
                _lastCapacitySignature = int.MinValue;
                RefreshCapacityImmediately();
                return;
            }

            Image[] frames = new Image[PhysicalSlotCount];
            Image[] icons = new Image[PhysicalSlotCount];
            Array.Copy(hud.itemSlotIconFrames, frames, Math.Min(hud.itemSlotIconFrames.Length, PhysicalSlotCount));
            Array.Copy(hud.itemSlotIcons, icons, Math.Min(hud.itemSlotIcons.Length, PhysicalSlotCount));

            Image templateFrame = frames[VanillaSlotCount - 1] ?? frames[0];
            Image templateIcon = icons[VanillaSlotCount - 1] ?? icons[0];
            if (templateFrame == null || templateIcon == null)
            {
                Plugin.Log?.LogError("Native Deeper Pockets could not allocate HUD slots: a vanilla template was null.");
                return;
            }

            for (int slot = VanillaSlotCount; slot < PhysicalSlotCount; slot++)
            {
                Image frame = UnityEngine.Object.Instantiate(templateFrame, templateFrame.transform.parent);
                frame.name = $"Y4NGZHotbarSlot{slot + 1}";
                frame.transform.SetSiblingIndex(frames[slot - 1].transform.GetSiblingIndex() + 1);

                Image icon = ResolveClonedIcon(frame, templateFrame, templateIcon);
                if (icon == null)
                {
                    UnityEngine.Object.Destroy(frame.gameObject);
                    Plugin.Log?.LogError("Native Deeper Pockets could not allocate HUD slots: a cloned icon was missing.");
                    return;
                }

                icon.name = $"Y4NGZHotbarIcon{slot + 1}";
                icon.sprite = null;
                icon.enabled = false;
                frames[slot] = frame;
                icons[slot] = icon;
            }

            hud.itemSlotIconFrames = frames;
            hud.itemSlotIcons = icons;
            _lastCapacitySignature = int.MinValue;
            RefreshCapacityImmediately();
        }

        internal static void TickLocalCapacity(PlayerControllerB player)
        {
            if (!NativeSlotsEnabled || !IsAuthoritativeLocalPlayer(player))
                return;

            int signature = BuildCapacitySignature(player);
            if (signature == _lastCapacitySignature && HUDManager.Instance == _layoutHud)
                return;

            _lastCapacitySignature = signature;
            RefreshLocalHudAndSelection(player);
        }

        internal static void BindHotbarActions(IngameKeybinds keybinds)
        {
            if (keybinds == null || !OptionalPluginCapabilities.NativeInventoryAvailable)
                return;

            keybinds.HotbarSlot1.performed += OnHotbarAction;
            keybinds.HotbarSlot2.performed += OnHotbarAction;
            keybinds.HotbarSlot3.performed += OnHotbarAction;
            keybinds.HotbarSlot4.performed += OnHotbarAction;
            keybinds.HotbarSlot5.performed += OnHotbarAction;
            keybinds.HotbarSlot6.performed += OnHotbarAction;
            keybinds.HotbarSlot7.performed += OnHotbarAction;
        }

        internal static void UnbindHotbarActions(IngameKeybinds keybinds)
        {
            if (keybinds == null)
                return;

            keybinds.HotbarSlot1.performed -= OnHotbarAction;
            keybinds.HotbarSlot2.performed -= OnHotbarAction;
            keybinds.HotbarSlot3.performed -= OnHotbarAction;
            keybinds.HotbarSlot4.performed -= OnHotbarAction;
            keybinds.HotbarSlot5.performed -= OnHotbarAction;
            keybinds.HotbarSlot6.performed -= OnHotbarAction;
            keybinds.HotbarSlot7.performed -= OnHotbarAction;
        }

        internal static bool TrySelectSlot(PlayerControllerB player, int slot)
        {
            if (!NativeSlotsEnabled
                || !IsAuthoritativeLocalPlayer(player)
                || player.isPlayerDead
                || player.ItemSlots == null
                || slot < 0
                || slot >= PhysicalSlotCount
                || slot >= player.ItemSlots.Length
                || slot == player.currentItemSlot
                || !NativeInventoryModel.IsSelectable(slot, GetUnlockedSlotCount(), GetOccupancyMask(player))
                || !CanSwitchSlotsNow(player))
            {
                return false;
            }

            using (ScannerTransporterPatch.BeginSlotSwitchScope(player))
            {
                ShipBuildModeManager.Instance?.CancelBuildMode();
                player.playerBodyAnimator?.SetBool("GrabValidated", false);
                player.SwitchToItemSlot(slot);
                player.SwitchToSlotServerRpc(slot);
                PlayHeldItemSwitchSound(player);
                player.timeSinceSwitchingSlots = 0f;
            }

            return true;
        }

        internal static bool IsAuthoritativeLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = StartOfRound.Instance?.localPlayerController
                ?? GameNetworkManager.Instance?.localPlayerController;
            return player == local;
        }

        internal static int GetOccupancyMask(PlayerControllerB player)
        {
            int mask = 0;
            if (player?.ItemSlots == null)
                return mask;

            int count = Math.Min(player.ItemSlots.Length, PhysicalSlotCount);
            for (int slot = 0; slot < count; slot++)
            {
                if (player.ItemSlots[slot] != null)
                    mask |= 1 << slot;
            }

            return mask;
        }

        private static void RefreshCapacityImmediately()
        {
            if (!NativeSlotsEnabled)
                return;

            EnsureAllPlayerInventories();
            PlayerControllerB local = StartOfRound.Instance?.localPlayerController
                ?? GameNetworkManager.Instance?.localPlayerController;
            _lastCapacitySignature = int.MinValue;
            if (local != null)
                TickLocalCapacity(local);
        }

        private static void RefreshLocalHudAndSelection(PlayerControllerB player)
        {
            if (player == null)
                return;

            int unlocked = GetUnlockedSlotCount();
            int occupancy = GetOccupancyMask(player);
            if (player.currentItemSlot >= unlocked
                && player.currentItemSlot < PhysicalSlotCount
                && (occupancy & (1 << player.currentItemSlot)) == 0)
            {
                int fallback = NativeInventoryModel.GetHighestUnlockedSlot(unlocked);
                if (player.currentItemSlot != fallback && CanSwitchSlotsNow(player))
                {
                    using (ScannerTransporterPatch.BeginSlotSwitchScope(player))
                    {
                        player.SwitchToItemSlot(fallback);
                        player.SwitchToSlotServerRpc(fallback);
                        player.timeSinceSwitchingSlots = 0f;
                    }
                }
            }

            RecenterVisibleHudSlots(HUDManager.Instance, unlocked, occupancy);
        }

        private static void CaptureVanillaLayout(HUDManager hud)
        {
            if (hud == null || hud.itemSlotIconFrames == null || hud.itemSlotIcons == null)
                return;

            Image firstFrame = hud.itemSlotIconFrames[0];
            Image lastFrame = hud.itemSlotIconFrames[VanillaSlotCount - 1];
            Image firstIcon = hud.itemSlotIcons[0];
            if (firstFrame == null || lastFrame == null || firstIcon == null)
                return;

            _layoutHud = hud;
            _layoutCenter = (firstFrame.rectTransform.anchoredPosition + lastFrame.rectTransform.anchoredPosition) * 0.5f;
            _layoutStep = (lastFrame.rectTransform.anchoredPosition - firstFrame.rectTransform.anchoredPosition)
                / (VanillaSlotCount - 1);
            _frameSize = firstFrame.rectTransform.sizeDelta;
            _iconSize = firstIcon.rectTransform.sizeDelta;
            _frameEuler = firstFrame.rectTransform.localEulerAngles;
            _iconEuler = firstIcon.rectTransform.localEulerAngles;
            _frameScale = firstFrame.rectTransform.localScale;
            _iconScale = firstIcon.rectTransform.localScale;
        }

        private static Image ResolveClonedIcon(Image clonedFrame, Image templateFrame, Image templateIcon)
        {
            if (clonedFrame == null || templateFrame == null || templateIcon == null)
                return null;

            string relativePath = GetRelativePath(templateFrame.transform, templateIcon.transform);
            Transform clonedIconTransform = string.IsNullOrEmpty(relativePath)
                ? clonedFrame.transform
                : clonedFrame.transform.Find(relativePath);
            return clonedIconTransform?.GetComponent<Image>();
        }

        private static string GetRelativePath(Transform root, Transform child)
        {
            if (root == null || child == null || child == root)
                return string.Empty;

            string path = child.name;
            Transform current = child.parent;
            while (current != null && current != root)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return current == root ? path : string.Empty;
        }

        private static void RecenterVisibleHudSlots(HUDManager hud, int unlocked, int occupancyMask)
        {
            if (hud == null
                || hud.itemSlotIconFrames == null
                || hud.itemSlotIcons == null
                || hud.itemSlotIconFrames.Length < PhysicalSlotCount
                || hud.itemSlotIcons.Length < PhysicalSlotCount)
            {
                return;
            }

            if (_layoutHud != hud)
                CaptureVanillaLayout(hud);

            int visibleCount = 0;
            for (int slot = 0; slot < PhysicalSlotCount; slot++)
            {
                if (NativeInventoryModel.IsSelectable(slot, unlocked, occupancyMask))
                    visibleCount++;
            }

            int visibleIndex = 0;
            for (int slot = 0; slot < PhysicalSlotCount; slot++)
            {
                Image frame = hud.itemSlotIconFrames[slot];
                Image icon = hud.itemSlotIcons[slot];
                if (frame == null || icon == null)
                    continue;

                bool visible = NativeInventoryModel.IsSelectable(slot, unlocked, occupancyMask);
                frame.gameObject.SetActive(visible);
                if (!visible)
                    continue;

                float centeredIndex = visibleIndex - (visibleCount - 1) * 0.5f;
                frame.rectTransform.anchoredPosition = _layoutCenter + _layoutStep * centeredIndex;
                frame.rectTransform.sizeDelta = _frameSize;
                frame.rectTransform.localEulerAngles = _frameEuler;
                frame.rectTransform.localScale = _frameScale;
                icon.rectTransform.sizeDelta = _iconSize;
                icon.rectTransform.localEulerAngles = _iconEuler;
                icon.rectTransform.localScale = _iconScale;
                visibleIndex++;
            }
        }

        private static int BuildCapacitySignature(PlayerControllerB player)
        {
            return (ExtraSlotUpgrade.GetTier() & 0x3) | (GetOccupancyMask(player) << 2);
        }

        private static bool CanSwitchSlotsNow(PlayerControllerB player)
        {
            if (player == null
                || !player.IsOwner
                || !player.isPlayerControlled
                || (player.IsServer && !player.isHostPlayerObject)
                || player.isPlayerDead
                || player.timeSinceSwitchingSlots < 0.3f
                || player.isGrabbingObjectAnimation
                || player.quickMenuManager == null
                || player.quickMenuManager.isMenuOpen
                || player.inTerminalMenu
                || player.inSpecialInteractAnimation
                || player.throwingObject
                || player.isTypingChat
                || (player.twoHanded && !ExtraSlotUpgrade.CanCarryTwoTwoHandedItems())
                || player.activatingItem)
            {
                return false;
            }

            return (!player.jetpackControls && !player.disablingJetpackControls)
                || player.currentlyHeldObjectServer == null
                || player.currentlyHeldObjectServer.itemProperties == null
                || player.currentlyHeldObjectServer.itemProperties.itemId != 13;
        }

        private static void PlayHeldItemSwitchSound(PlayerControllerB player)
        {
            GrabbableObject held = player?.currentlyHeldObjectServer;
            if (held?.itemProperties?.grabSFX == null)
                return;

            held.GetComponent<AudioSource>()?.PlayOneShot(held.itemProperties.grabSFX, 0.6f);
        }

        private static void OnHotbarAction(InputAction.CallbackContext context)
        {
            try
            {
                IngameKeybinds keybinds = Gui.Plugin.Keybinds;
                int slot = ResolveActionSlot(keybinds, context.action);
                if (slot < 0)
                    return;

                PlayerControllerB player = StartOfRound.Instance?.localPlayerController
                    ?? GameNetworkManager.Instance?.localPlayerController;
                TrySelectSlot(player, slot);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"Native Deeper Pockets number-key selection failed: {e}");
            }
        }

        private static int ResolveActionSlot(IngameKeybinds keybinds, InputAction action)
        {
            if (keybinds == null || action == null) return -1;
            if (action == keybinds.HotbarSlot1) return 0;
            if (action == keybinds.HotbarSlot2) return 1;
            if (action == keybinds.HotbarSlot3) return 2;
            if (action == keybinds.HotbarSlot4) return 3;
            if (action == keybinds.HotbarSlot5) return 4;
            if (action == keybinds.HotbarSlot6) return 5;
            if (action == keybinds.HotbarSlot7) return 6;
            return -1;
        }
    }
}
