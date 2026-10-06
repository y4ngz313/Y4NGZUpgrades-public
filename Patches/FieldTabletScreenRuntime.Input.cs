using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    internal static partial class FieldTabletScreenRuntime
    {
        // Every discrete tablet command is an InputUtils action. Mouse-wheel scrolling remains an
        // analog convenience, and input is read only while the tablet is deployed.
        private static void HandleTabletInputs(PlayerControllerB player, Action fireTabletActionGesture)
        {
            // F-TABLET-7: UpgradeInput.WasPressed is a raw WasPressedThisFrame with no focus guard,
            // and the tablet's bindings collide with chat and the ESC menu head-on (Enter, Backspace,
            // the arrows, LMB, and a raw Mouse.current.scroll read). Without this, submitting a chat
            // message on the MAINFRAME tab also fired the selected row - a lockdown, say. The stow
            // toggle lives in FieldOperationsTabletPatch.PostPlayerUpdate and deliberately stays
            // live, so the tablet can always be put away.
            if (player != null
                && (player.isTypingChat
                    || player.inTerminalMenu
                    || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)))
            {
                // The channels still tick so an in-flight splice or uplink aborts on target loss
                // or on leaving the facility while the menu is up; only the reads are suppressed.
                UpdateSpliceChannel();
                UpdateUplinkChannel(player);
                return;
            }

            Mouse mouse = Mouse.current;
            Gui.IngameKeybinds keybinds = Gui.Plugin.Keybinds;

            if (CourierDronePatch.IsLocalPilotModeActive)
            {
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
                _mode = TabletScreenMode.Drone;
                _statusLine = "REMOTE LINK ACTIVE";
                if (Gui.UpgradeInput.WasPressed(keybinds?.TabletBack))
                {
                    FieldTabletAudio.PlayBack();
                    fireTabletActionGesture?.Invoke();
                    CourierDronePatch.StopLocalPilotMode(notifyOwner: false);
                    _statusLine = "REMOTE LINK CLOSED";
                }
                return;
            }

            if (_mode == TabletScreenMode.Boot)
                return;

            bool up = Gui.UpgradeInput.WasPressed(keybinds?.TabletUp);
            bool down = Gui.UpgradeInput.WasPressed(keybinds?.TabletDown);
            bool primaryAction = Gui.UpgradeInput.WasPressed(keybinds?.TabletPrimaryAction);
            bool activate = Gui.UpgradeInput.WasPressed(keybinds?.TabletActivate)
                || (_mode == TabletScreenMode.Scan && primaryAction);
            bool back = Gui.UpgradeInput.WasPressed(keybinds?.TabletBack);
            bool previousTab = Gui.UpgradeInput.WasPressed(keybinds?.TabletPreviousTab);
            bool nextTab = Gui.UpgradeInput.WasPressed(keybinds?.TabletNextTab);

            if (previousTab || nextTab)
            {
                SwitchTab(nextTab ? 1 : -1);
                FieldTabletAudio.PlayMovement();
                fireTabletActionGesture?.Invoke();
                return;
            }

            switch (_mode)
            {
                case TabletScreenMode.Scan:
                    if (activate && _currentTarget.IsValid)
                        BeginTargetSplice(fireTabletActionGesture);
                    break;
                case TabletScreenMode.Hack:
                    if (back && _hackSolvedAt <= 0f)
                    {
                        StopHack("aborted");
                        FieldTabletAudio.PlayBack();
                        _statusLine = "SPLICE ABORTED";
                    }
                    break;
                case TabletScreenMode.Radar:
                    HandleMapInputs(mouse, up, down);
                    break;
                case TabletScreenMode.Drone:
                    HandleMapZoomKeys(mouse);
                    if (activate)
                    {
                        FieldTabletAudio.PlaySelect();
                        if (CourierDroneUpgrade.IsUnlocked())
                        {
                            EnterCommandScreen();
                            fireTabletActionGesture?.Invoke();
                            _statusLine = "DRONE COMMAND";
                        }
                        else
                        {
                            _statusLine = "DRONE MODULE LOCKED";
                        }
                    }
                    break;
                case TabletScreenMode.Command:
                    HandleCommandScreenInputs(player, mouse, up, down, activate, back, fireTabletActionGesture);
                    break;
                case TabletScreenMode.Mainframe:
                    HandleMainframeInputs(player, up, down, activate, back, fireTabletActionGesture);
                    break;
            }

            UpdateSpliceChannel();
            UpdateUplinkChannel(player);
        }

        private static int TabIndexForMode(TabletScreenMode mode)
        {
            switch (mode)
            {
                case TabletScreenMode.Radar: return 1;
                case TabletScreenMode.Drone:
                case TabletScreenMode.Command: return 2;
                case TabletScreenMode.Mainframe: return 3;
                default: return 0;
            }
        }

        private static void SwitchTab(int delta)
        {
            if (_mode == TabletScreenMode.Hack)
                StopHack("tab-switch");
            if (_mode == TabletScreenMode.Command)
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
            if (_uplinkChannelActive)
            {
                _uplinkChannelActive = false;
                _uplinkProgress = 0f;
                _mainframeStatus = "UPLINK ABORTED";
            }

            int tab = TabIndexForMode(_mode) + delta;
            if (tab < 0)
                tab += FieldTabletLayout.TabNames.Length;
            tab %= FieldTabletLayout.TabNames.Length;

            switch (tab)
            {
                case 0:
                    _mode = TabletScreenMode.Scan;
                    _statusLine = "SCAN READY";
                    break;
                case 1:
                    _mode = TabletScreenMode.Radar;
                    _statusLine = "INTERIOR MAP";
                    break;
                case 2:
                    _mode = TabletScreenMode.Drone;
                    _statusLine = "DRONE LINK";
                    break;
                default:
                    _mode = TabletScreenMode.Mainframe;
                    _mainframeView = MainframeView.Root;
                    _mainframeSelection = 0;
                    _mainframeWindowStart = 0;
                    _nextMainframeRowsRefreshAt = 0f;
                    _statusLine = "MAINFRAME";
                    // Raise the tablet for the reading-heavy mainframe screen.
                    FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(true);
                    break;
            }

            if (_mode != TabletScreenMode.Mainframe && _mode != TabletScreenMode.Command)
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
        }

        private static void HandleMapInputs(Mouse mouse, bool up, bool down)
        {
            // Zoom: up/down (nothing else is selectable on the MAP tab), +/- keys, or scroll.
            if (up)
            {
                AdjustRadarZoom(0.12f);
                FieldTabletAudio.PlayMovement();
            }
            if (down)
            {
                AdjustRadarZoom(-0.12f);
                FieldTabletAudio.PlayMovement();
            }
            HandleMapZoomKeys(mouse);
        }

        private static void HandleMapZoomKeys(Mouse mouse)
        {
            if (Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.TabletZoomIn))
            {
                AdjustRadarZoom(0.12f);
                FieldTabletAudio.PlayMovement();
            }
            if (Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.TabletZoomOut))
            {
                AdjustRadarZoom(-0.12f);
                FieldTabletAudio.PlayMovement();
            }

            if (mouse != null)
            {
                float scroll = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    AdjustRadarZoom(Mathf.Sign(scroll) * 0.12f);
                    FieldTabletAudio.PlayMovement();
                }
            }
        }

        private static void AdjustRadarZoom(float delta)
        {
            _radarZoom = Mathf.Clamp(_radarZoom + delta, 0.65f, 2.35f);
            _nextRadarRenderAt = 0f;
        }

        private static void HandleCommandScreenInputs(PlayerControllerB player, Mouse mouse, bool up, bool down, bool activate, bool back, Action fireTabletActionGesture)
        {
            if (back)
            {
                FieldTabletAudio.PlayBack();
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
                _mode = TabletScreenMode.Drone;
                _statusLine = "DRONE LINK";
                fireTabletActionGesture?.Invoke();
                return;
            }

            if (up)
            {
                MoveCommandSelection(-1);
                FieldTabletAudio.PlayMovement();
            }
            if (down)
            {
                MoveCommandSelection(1);
                FieldTabletAudio.PlayMovement();
            }

            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    MoveCommandSelection(scroll < 0f ? 1 : -1);
                    FieldTabletAudio.PlayMovement();
                }
            }

            if (!activate)
                return;

            ExecuteSelectedCommand(player, fireTabletActionGesture);
        }

        private static void EnterCommandScreen()
        {
            if (_mode == TabletScreenMode.Command)
                return;

            if (_mode == TabletScreenMode.Hack)
                StopHack("command-screen");

            _modeBeforeCommand = _mode == TabletScreenMode.Boot || _mode == TabletScreenMode.Hack
                ? TabletScreenMode.Scan
                : _mode;
            _commandSelection = 0;
            _commandWindowStart = 0;
            _nextCommandEntriesRefreshAt = 0f;
            _mode = TabletScreenMode.Command;
            FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(true);
        }

        private static void ExecuteSelectedCommand(PlayerControllerB player, Action fireTabletActionGesture)
        {
            int count = CourierDronePatch.GetTabletCommandEntries(player, CommandEntries);
            _commandEntryCount = count;
            if (count <= 0)
            {
                _statusLine = "NO COMMANDS";
                FieldTabletAudio.PlaySelect();
                return;
            }

            _commandSelection = Mathf.Clamp(_commandSelection, 0, count - 1);
            CourierDronePatch.TabletCommandEntry entry = CommandEntries[_commandSelection];
            fireTabletActionGesture?.Invoke();
            if (!entry.Available)
            {
                _statusLine = string.IsNullOrWhiteSpace(entry.Reason) ? "COMMAND LOCKED" : entry.Reason;
                FieldTabletAudio.PlaySelect();
                return;
            }

            if (CourierDronePatch.TryExecuteTabletCommand(player, entry.Type))
            {
                if (entry.Type == CourierDronePatch.CourierCommandType.Spawn)
                    FieldTabletAudio.PlayDroneSummon();
                else
                    FieldTabletAudio.PlayDroneCommand();
                _statusLine = entry.Type == CourierDronePatch.CourierCommandType.Pilot
                    ? "REMOTE LINK ACTIVE"
                    : entry.Label + " SENT";
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
                _mode = entry.Type == CourierDronePatch.CourierCommandType.Pilot
                    ? TabletScreenMode.Drone
                    : _modeBeforeCommand == TabletScreenMode.Command || _modeBeforeCommand == TabletScreenMode.Hack || _modeBeforeCommand == TabletScreenMode.Boot
                    ? TabletScreenMode.Drone
                    : _modeBeforeCommand;
            }
            else
            {
                FieldTabletAudio.PlaySelect();
            }
        }

        private static void MoveCommandSelection(int delta)
        {
            int count = CourierDronePatch.GetTabletCommandEntries(GameNetworkManager.Instance?.localPlayerController, CommandEntries);
            _commandEntryCount = count;
            if (count <= 0)
            {
                _commandSelection = 0;
                return;
            }

            _commandSelection = (_commandSelection + delta) % count;
            if (_commandSelection < 0)
                _commandSelection += count;
        }

        private static void BeginTargetSplice(Action fireTabletActionGesture)
        {
            if (_hackHoldActive || !_currentTarget.IsValid)
                return;

            _hackHoldActive = true;
            _hackTarget = _currentTarget;
            _hackProgress = 0f;
            _hackSolvedAt = 0f;
            _hackResult = string.Empty;
            _hackResultFailed = false;
            _hackStatusDetail = string.Empty;
            _statusLine = "SPLICING";
            _mode = TabletScreenMode.Hack;
            FieldTabletAudio.PlayHack();
            fireTabletActionGesture?.Invoke();
        }

        // Enter-started channel: runs while the reticle stays on the same target;
        // BACKSPACE or losing the target aborts.
        private static void UpdateSpliceChannel()
        {
            if (!_hackHoldActive || _mode != TabletScreenMode.Hack)
                return;
            if (_hackSolvedAt > 0f)
                return;

            bool sameTarget = _currentTarget.IsValid
                && _hackTarget.Root != null
                && _currentTarget.Root == _hackTarget.Root;
            if (!sameTarget)
            {
                StopHack("target-lost");
                _statusLine = "LINK LOST";
                return;
            }

            _hackTarget.Distance = _currentTarget.Distance;
            _hackProgress += Time.deltaTime / TargetSpliceSeconds;
            if (_hackProgress < 1f)
                return;

            _hackProgress = 1f;
            _hackSolvedAt = Time.unscaledTime;
            // A camera splice only sends a request; the host's answer replaces this readout from
            // OnMainframeActionResult (possibly synchronously, on a host), so it is set first.
            bool cameraSplice = _hackTarget.Kind == TabletHackTargetKind.CctvCamera;
            // An earlier splice's refusal must not show on this one while it awaits the host.
            if (cameraSplice)
            {
                _hackResult = "AWAITING HOST";
                _hackResultFailed = false;
                _hackStatusDetail = string.Empty;
            }
            bool applied = TryNotifyHackSuccess();
            if (applied
                && _hackTarget.Kind != TabletHackTargetKind.Mainframe
                && _hackTarget.Kind != TabletHackTargetKind.CctvCamera)
            {
                UnityEngine.Object hackedTarget = _hackTarget.Root != null
                    ? _hackTarget.Root
                    : _hackTarget.Component;
                EmployeeStatistics.RecordDeviceHacked(hackedTarget);
            }
            if (!cameraSplice || !applied)
            {
                _hackResult = applied ? "ACCESS GRANTED" : "ACCESS FAILED";
                _hackResultFailed = !applied;
            }
            _statusLine = "SCAN READY";
        }

        // Enter-started 10 s uplink channel; aborts on BACKSPACE, tab switch,
        // leaving the facility, or death.
        private static void UpdateUplinkChannel(PlayerControllerB player)
        {
            if (!_uplinkChannelActive)
                return;

            if (_mode != TabletScreenMode.Mainframe || player == null || player.isPlayerDead || !player.isInsideFactory)
            {
                _uplinkChannelActive = false;
                _uplinkProgress = 0f;
                _mainframeStatus = "UPLINK ABORTED";
                return;
            }

            _uplinkProgress += Time.deltaTime / UplinkSeconds;
            if (_uplinkProgress < 1f)
                return;

            _uplinkChannelActive = false;
            _uplinkProgress = 0f;
            _uplinkEstablished = true;
            _mainframeStatus = "UPLINK ESTABLISHED";
        }
    }
}
