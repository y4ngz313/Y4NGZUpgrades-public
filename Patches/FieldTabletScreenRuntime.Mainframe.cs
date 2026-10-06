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
        private static void HandleMainframeInputs(PlayerControllerB player, bool up, bool down, bool activate, bool back, Action fireTabletActionGesture)
        {
            BuildMainframeRows(player);
            int count = MainframeRows.Count;
            if (count > 0)
            {
                _mainframeSelection = Mathf.Clamp(_mainframeSelection, 0, count - 1);
                if (up)
                {
                    _mainframeSelection = (_mainframeSelection - 1 + count) % count;
                    FieldTabletAudio.PlayMovement();
                }
                if (down)
                {
                    _mainframeSelection = (_mainframeSelection + 1) % count;
                    FieldTabletAudio.PlayMovement();
                }
            }
            else
            {
                _mainframeSelection = 0;
            }

            if (back)
            {
                if (_uplinkChannelActive)
                {
                    _uplinkChannelActive = false;
                    _uplinkProgress = 0f;
                    _mainframeStatus = "UPLINK ABORTED";
                    FieldTabletAudio.PlayBack();
                }
                else if (_mainframeView != MainframeView.Root)
                {
                    _mainframeView = MainframeView.Root;
                    _mainframeSelection = 0;
                    _mainframeWindowStart = 0;
                    _nextMainframeRowsRefreshAt = 0f;
                    fireTabletActionGesture?.Invoke();
                    FieldTabletAudio.PlayBack();
                }
                return;
            }

            if (!activate || count == 0 || _mainframeSelection >= count)
                return;

            BuildMainframeRows(player, force: true);
            count = MainframeRows.Count;
            if (count == 0 || _mainframeSelection >= count)
                return;

            MainframeRow row = MainframeRows[_mainframeSelection];
            if (!row.Enabled)
            {
                _mainframeStatus = row.Detail;
                FieldTabletAudio.PlaySelect();
                return;
            }

            ActivateMainframeRow(player, row, fireTabletActionGesture);
        }

        // F-TABLET-4: no cooldown mirror is set here. The request is only a request; the mirrors
        // are set in OnMainframeActionResult from the host's reply, so a refused press does not
        // lock this client's row out for a cooldown the host never started.
        private static void ActivateMainframeRow(PlayerControllerB player, MainframeRow row, Action fireTabletActionGesture)
        {
            if (row.Action == FieldTabletMainframeAction.EstablishUplink)
                FieldTabletAudio.PlayHack();
            else
                FieldTabletAudio.PlaySelect();

            switch (row.Action)
            {
                case FieldTabletMainframeAction.EstablishUplink:
                    if (_uplinkChannelActive)
                        return;
                    _uplinkChannelActive = true;
                    _uplinkProgress = 0f;
                    _mainframeStatus = "UPLINKING";
                    fireTabletActionGesture?.Invoke();
                    break;
                case FieldTabletMainframeAction.OpenStashCodes:
                    _mainframeView = MainframeView.StashCodes;
                    _mainframeSelection = 0;
                    _mainframeWindowStart = 0;
                    _nextMainframeRowsRefreshAt = 0f;
                    fireTabletActionGesture?.Invoke();
                    break;
                case FieldTabletMainframeAction.ScrapCheck:
                    RefreshScrapCheck();
                    _mainframeStatus = _scrapCheckResult;
                    fireTabletActionGesture?.Invoke();
                    break;
                case FieldTabletMainframeAction.OpenCameras:
                    _mainframeView = MainframeView.Cameras;
                    _mainframeSelection = 0;
                    _mainframeWindowStart = 0;
                    _nextMainframeRowsRefreshAt = 0f;
                    fireTabletActionGesture?.Invoke();
                    break;
                case FieldTabletMainframeAction.DisableCamera:
                    FieldTabletMainframeNet.RequestCameraDisable(row.CameraId);
                    _mainframeStatus = FieldTabletLayout.ShutdownSentPrefix + FieldTabletLayout.FitChars(row.Label,
                        FieldTabletLayout.MaxChars(FieldTabletLayout.MainframeStatus) - FieldTabletLayout.ShutdownSentPrefix.Length);
                    fireTabletActionGesture?.Invoke();
                    break;
                case FieldTabletMainframeAction.AlarmToggle:
                    FieldTabletMainframeNet.RequestAlarm(CompanyIsAlarmOn() ? FieldTabletMainframeNet.AlarmOpOff : FieldTabletMainframeNet.AlarmOpOn);
                    _mainframeStatus = "ALARM TOGGLE SENT";
                    fireTabletActionGesture?.Invoke();
                    break;
                case FieldTabletMainframeAction.AlarmSilence:
                    FieldTabletMainframeNet.RequestAlarm(FieldTabletMainframeNet.AlarmOpSilence);
                    _mainframeStatus = "SILENCE SENT";
                    fireTabletActionGesture?.Invoke();
                    break;
                case FieldTabletMainframeAction.Lockdown:
                    FieldTabletMainframeNet.RequestLockdown(!CompanyIsLockdownActive());
                    _mainframeStatus = "LOCKDOWN REQUEST SENT";
                    fireTabletActionGesture?.Invoke();
                    break;
                case FieldTabletMainframeAction.DisableTraps:
                    FieldTabletMainframeNet.RequestTrapsDisable();
                    _mainframeStatus = "TRAP SHUTDOWN SENT";
                    fireTabletActionGesture?.Invoke();
                    break;
            }
        }

        // Row tier gates live in FieldTabletMainframeRowPlan.RequiredTier.
        private static int GetFieldOperationsTier()
        {
            return FieldOperationsUpgrade.GetTier();
        }

        // Row rebuilds are throttled like the rest of the tablet text refreshes;
        // activations force a rebuild so Enter always acts on fresh data.
        private static void BuildMainframeRows(PlayerControllerB player, bool force = false)
        {
            float time = Time.unscaledTime;
            if (!force && MainframeRows.Count > 0 && time < _nextMainframeRowsRefreshAt)
                return;
            _nextMainframeRowsRefreshAt = time + TabletDynamicTextRefreshInterval;

            MainframeRows.Clear();
            RefreshActivateKeyText();
            bool cctv = IsCompanyPresent();

            // The stash and camera views only exist with LethalCCTV; without it they fall back
            // to the root list rather than showing a dead row.
            if (_mainframeView != MainframeView.Root && !cctv)
            {
                _mainframeView = MainframeView.Root;
                _mainframeSelection = 0;
                _mainframeWindowStart = 0;
            }
            if (_mainframeView == MainframeView.StashCodes)
            {
                BuildStashCodeRows();
                return;
            }
            if (_mainframeView == MainframeView.Cameras)
            {
                BuildCameraRows(time);
                return;
            }

            FieldTabletMainframeRootInputs inputs = new FieldTabletMainframeRootInputs
            {
                CctvPresent = cctv,
                Tier = GetFieldOperationsTier(),
                InFacility = player != null && player.isInsideFactory && !player.isPlayerDead,
                UplinkEstablished = _uplinkEstablished,
                UplinkChannelActive = _uplinkChannelActive,
                UplinkProgress = _uplinkProgress,
                UplinkReadyDetail = _uplinkReadyDetail,
                ScrapCheckResult = _scrapCheckResult,
                AlarmCooldownSeconds = _alarmReadyAt - time,
                LockdownCooldownSeconds = _lockdownReadyAt - time,
                TrapsCooldownSeconds = _trapsReadyAt - time
            };
            if (cctv)
            {
                inputs.AlarmOn = CompanyIsAlarmOn();
                inputs.MainframeHacked = CompanyIsMainframeHacked();
                inputs.SecurityAlarmActive = CompanyIsSecurityAlarmActive();
                inputs.LockdownActive = CompanyIsLockdownActive();
            }

            FieldTabletMainframeRowPlan.BuildRoot(inputs, MainframeRootSpecs);
            for (int i = 0; i < MainframeRootSpecs.Count; i++)
            {
                FieldTabletMainframeRowSpec spec = MainframeRootSpecs[i];
                MainframeRows.Add(new MainframeRow
                {
                    Label = spec.Label,
                    Detail = spec.Detail,
                    Enabled = spec.Enabled,
                    Action = spec.Action
                });
            }
        }

        // Only reached with LethalCCTV present; BuildMainframeRows returns to the root first.
        private static void BuildStashCodeRows()
        {
            if (!_uplinkEstablished)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "STASH CODES",
                    Detail = "NO UPLINK",
                    Enabled = false,
                    Action = FieldTabletMainframeAction.None
                });
                return;
            }

            int added = AppendStashCodeRows();
            if (added == 0)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "NO CODES ON FILE",
                    Detail = string.Empty,
                    Enabled = false,
                    Action = FieldTabletMainframeAction.None
                });
            }
        }

        // Only reached with LethalCCTV present; BuildMainframeRows returns to the root first.
        private static void BuildCameraRows(float now)
        {
            if (!_uplinkEstablished)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "CAMERAS",
                    Detail = "NO UPLINK",
                    Enabled = false,
                    Action = FieldTabletMainframeAction.None
                });
                return;
            }

            MainframeCameraIdBuffer.Clear();
            CompanyCollectCameraIds(MainframeCameraIdBuffer);
            if (MainframeCameraIdBuffer.Count == 0)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "NO CAMERAS FOUND",
                    Detail = string.Empty,
                    Enabled = false,
                    Action = FieldTabletMainframeAction.None
                });
                return;
            }

            bool coolingDown = now < _cameraDisableReadyAt;
            for (int i = 0; i < MainframeCameraIdBuffer.Count; i++)
            {
                int id = MainframeCameraIdBuffer[i];
                bool disabled = CompanyIsCameraDisabled(id);
                MainframeRows.Add(new MainframeRow
                {
                    Label = ResolveCameraRowLabel(id),
                    Detail = disabled ? "OFFLINE"
                        : coolingDown ? FieldTabletMainframeRowPlan.FormatCooldown(_cameraDisableReadyAt - now)
                        : _cameraOffDetail,
                    Enabled = !disabled && !coolingDown,
                    Action = FieldTabletMainframeAction.DisableCamera,
                    CameraId = id
                });
            }
        }

        private static string ResolveCameraRowLabel(int cameraId)
        {
            string source = CompanyGetCameraLabel(cameraId);
            if (CameraRowLabels.TryGetValue(cameraId, out KeyValuePair<string, string> cached)
                && string.Equals(cached.Key, source, StringComparison.Ordinal))
                return cached.Value;

            string label = string.IsNullOrWhiteSpace(source) ? "CAM_" + cameraId : source;
            string display = FieldTabletLayout.FitChars(label.ToUpperInvariant(), FieldTabletLayout.MaxChars(FieldTabletLayout.RowLabel));
            CameraRowLabels[cameraId] = new KeyValuePair<string, string>(source, display);
            return display;
        }

        // Rebind-aware "[<activate>]" details for the uplink and camera rows. EffectivePath is a
        // stored string, so the per-refresh compare allocates nothing; the details are rebuilt
        // only when the binding changes.
        private static void RefreshActivateKeyText()
        {
            InputAction activate = Gui.Plugin.Keybinds?.TabletActivate;
            string path = Gui.UpgradeInput.EffectivePath(activate);
            if (_activateBindingPath != null && string.Equals(path, _activateBindingPath, StringComparison.Ordinal))
                return;

            _activateBindingPath = path;
            string label = Gui.UpgradeInput.DisplayLabel(activate, "ENTER");
            _uplinkReadyDetail = "[" + label + "] " + UplinkSeconds.ToString("0") + "S";
            _cameraOffDetail = "[" + label + "] OFF";
        }

        // Mirrors the ship terminal scan sweep (GrabbableObject + isScrap); display-only, so a local
        // read is fine.
        // F-TABLET-17: the total is interior scrap that is not in the ship. "Interior" is the
        // nearest-AI-node test (FacilityPositionQuery): an item counts when its nearest AI node is
        // an inside node. The vanilla isInFactory flag is not used - it defaults to true on the
        // prefab and is only rewritten at an EntranceTeleport, so surface scrap reported it too.
        // A level without interior nodes (the Company moon) has no interior and reads "$0/0".
        private static void RefreshScrapCheck()
        {
            int total = 0;
            int count = 0;
            try
            {
                if (FacilityPositionQuery.CaptureNodes())
                {
                    GrabbableObject[] items = UnityEngine.Object.FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None);
                    for (int i = 0; i < items.Length; i++)
                    {
                        GrabbableObject item = items[i];
                        if (item == null || item.itemProperties == null || !item.itemProperties.isScrap)
                            continue;
                        if (item.isInShipRoom || item.isInElevator)
                            continue;
                        if (!FacilityPositionQuery.IsInsideFactoryCaptured(item.transform.position))
                            continue;
                        total += item.scrapValue;
                        count++;
                    }
                }
                _scrapCheckResult = "$" + total + "/" + count;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] scrap check failed: " + ex.Message);
                _scrapCheckResult = "SWEEP FAILED";
            }
        }

        // - Company reads (kept in non-inlined methods behind the presence gate so the
        //   Y4NGZCompany type references only JIT behind IsCompanyPresent()) -

        // F-TABLET-18: one gate, shared with the host side in FieldTabletMainframeNet. The reads
        // below still JIT only behind it, which is the whole point of the non-inlined wrappers.
        private static bool IsCompanyPresent() => FieldTabletCooldowns.IsCompanyPresent();

        private static int AppendStashCodeRows()
        {
            IReadOnlyList<int> codes = OptionalCctvBridge.GetStashCodes();
            if (codes == null)
                return 0;
            for (int i = 0; i < codes.Count; i++)
            {
                int code = codes[i];
                if (!StashCodeLabels.TryGetValue(code, out string label))
                {
                    label = "CODE " + OptionalCctvBridge.FormatStashCode(code);
                    StashCodeLabels[code] = label;
                }
                MainframeRows.Add(new MainframeRow
                {
                    Label = label,
                    Detail = string.Empty,
                    Enabled = false,
                    Action = FieldTabletMainframeAction.None
                });
            }
            return codes.Count;
        }

        private static void CompanyCollectCameraIds(List<int> output)
        {
            IReadOnlyList<int> ids = OptionalCctvBridge.GetCameraIds();
            for (int i = 0; i < ids.Count; i++)
                output.Add(ids[i]);
        }

        private static string CompanyGetCameraLabel(int cameraId)
        {
            return OptionalCctvBridge.GetCameraLabel(cameraId);
        }

        private static bool CompanyIsCameraDisabled(int cameraId)
        {
            return OptionalCctvBridge.IsCameraDisabled(cameraId);
        }

        private static bool CompanyIsAlarmOn()
        {
            return IsCompanyPresent() && OptionalCctvBridge.IsAlarmActive;
        }

        private static bool CompanyIsSecurityAlarmActive()
        {
            return IsCompanyPresent() && OptionalCctvBridge.IsSecurityAlarmActive();
        }

        private static bool CompanyIsLockdownActive()
        {
            return IsCompanyPresent() && OptionalCctvBridge.IsLockdownActive;
        }

        private static bool CompanyIsMainframeHacked()
        {
            return IsCompanyPresent() && OptionalCctvBridge.IsMainframeHacked;
        }

        private static void StopHack(string reason)
        {
            // F-TABLET-18: `reason` used to be accepted and thrown away by every one of the seven
            // call sites. Logging it is what makes a splice that ends early diagnosable at all.
            if (_hackHoldActive)
                Plugin.Log?.LogDebug("[Field Tablet] splice stopped: " + reason);
            _hackHoldActive = false;
            _hackTarget = default;
            _hackProgress = 0f;
            _hackSolvedAt = 0f;
            _hackResult = string.Empty;
            _hackResultFailed = false;
            _hackStatusDetail = string.Empty;
            if (_mode == TabletScreenMode.Hack)
                _mode = TabletScreenMode.Scan;
        }

        private static void UpdateBootState()
        {
            if (_mode != TabletScreenMode.Boot)
                return;

            float elapsed = Time.unscaledTime - _openedAt;
            if (elapsed >= BootSeconds + BootTransitionSeconds)
                _mode = TabletScreenMode.Scan;
        }
    }
}
