using System;
using System.Collections.Generic;
using System.IO;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZInteractions.InteractionAnimationApi;
using Y4NGZUpgrades.Interactive;
using Y4NGZUpgrades.Interactive.Hud;
using Y4NGZUpgrades.Upgrades;

#pragma warning disable Harmony003

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class FieldOperationsTabletPatch
    {
        private const string PackId = "y4ngz.upgrades.field_operations_tablet";
        private const string InteractionId = "y4ngz.drone_tablet.inspect_livebody";
        private const string ManifestFileName = "y4ngz-interactions-dronetablet-livebody.manifest.json";
        private const string PromptKey = "field_operations_tablet";
        private const string ActionReadyBoolName = "Y4NGZ_DroneTablet_ActionReady";
        private const string ActionIndexIntName = "Y4NGZ_DroneTablet_ActionIndex";
        private const string ActionTriggerName = "Y4NGZ_DroneTablet_Action";
        private const string MsgTabletStart = "Y4NGZ_FieldTabletStart";
        private const string MsgTabletStop = "Y4NGZ_FieldTabletStop";
        // Raise the tablet toward the face when the player is basically touching a wall;
        // hysteresis keeps the animation from flickering at the threshold.
        private const float WallRaiseEnterDistance = 1.25f;
        private const float WallRaiseExitDistance = 1.55f;
        private const float WallNormalMaxY = 0.45f;
        private const float WallCastRadius = 0.25f;
        private const float WallExtraPullDistanceWindow = 0.8f;
        private const float TabletOffsetLerpSpeed = 10f;
        private const float WallExtraPullLerpSpeed = 12f;
        private const float GestureRaiseSeconds = 1.2f;
        // Vanilla uses this exact near-focus boundary while inspecting a held item. The tablet is
        // another close-up held surface, but unlike GrabbableObject.InspectItem it has no vanilla
        // path that acquires the scope for it.
        private const float TabletNearFocusEnd = 0.2f;
        private const string StowReason = "field-tablet";
        private const string StowTabletCursorTip = "STOW TABLET";
        // The tablet stays open as long as the player wants it; this is only a leak backstop.
        private const float StowTimeoutSeconds = 900f;

        private static bool _packRegistered;
        private static bool _networkHandlersRegistered;
        private static bool _missingManifestLogged;
        private static bool _startFailureLogged;
        private static bool _savedDisableInteract;
        private static bool _savedDisableInteractValue;
        private static bool _wallRaiseActive;
        private static float _wallHitDistance = WallRaiseExitDistance;
        private static float _currentWallExtraPull;
        private static Vector3 _currentArmsOffset;
        private static bool _actionReadyApplied;
        private static bool _pilotActionReadyHold;
        private static bool _commandScreenActionReadyHold;
        private static float _gestureRaiseUntil;
        private static int _nextActionIndex = 1;
        private static float _nextRegistrationAttemptAt;
        private static InteractionAnimationHandle _activeHandle = InteractionAnimationHandle.Empty;
        private static readonly Dictionary<ulong, InteractionAnimationHandle> RemoteHandles =
            new Dictionary<ulong, InteractionAnimationHandle>();
        private static readonly List<TabletRendererState> ThirdPersonRendererStates =
            new List<TabletRendererState>();
        private static bool _thirdPersonPresentationSaved;
        private static HeldItemStowHandle _tabletStow;
        private static DepthOfField _tabletDepthOfField;
        private static float _savedNearFocusEnd;
        private static bool _savedNearFocusOverride;
        private static bool _ownsTabletNearFocus;
        private static bool _missingTabletDepthOfFieldLogged;

        internal static bool IsTabletActive => _activeHandle.IsValid
            && LCInteractionAnimationAPI.IsInteractionActive(_activeHandle);

        internal static void Initialize()
        {
            EnsurePackRegistered();
            RegisterNetworkHandlers();
            FieldTabletAudio.Preload();
        }

        internal static void Shutdown()
        {
            ReleaseTabletNearFocus();
            FieldTabletAudio.Shutdown();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        internal static void TrySetPilotActionReadyHold(bool hold)
        {
            _pilotActionReadyHold = hold;
            if (!hold && !_commandScreenActionReadyHold)
                _gestureRaiseUntil = 0f;
            ApplyActionReadyState();
        }

        internal static void TrySetCommandScreenActionReadyHold(bool hold)
        {
            _commandScreenActionReadyHold = hold;
            if (!hold && !_pilotActionReadyHold)
                _gestureRaiseUntil = 0f;
            ApplyActionReadyState();
        }

        private static void ApplyActionReadyState()
        {
            if (!_activeHandle.IsValid)
                return;

            bool wantRaised = _pilotActionReadyHold
                || _commandScreenActionReadyHold
                || _wallRaiseActive
                || Time.unscaledTime < _gestureRaiseUntil;
            if (wantRaised == _actionReadyApplied)
                return;

            LCInteractionAnimationAPI.TrySetInteractionBool(_activeHandle, ActionReadyBoolName, wantRaised);
            _actionReadyApplied = wantRaised;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            RegisterNetworkHandlers();
            ClearInactiveHandle(__instance);
            ClearInactiveRemoteHandles();
            bool togglePressed = Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.FieldTablet);
            UpdateTabletPrompt(__instance);
            FieldTabletScreenRuntime.TickUplinkState(__instance);

            if (togglePressed)
                ToggleTablet(__instance);

            if (!IsTabletActive)
                return;

            MaintainTabletNearFocus();
            __instance.disableInteract = true;
            UpdateTabletRaise(__instance);
            FieldTabletScreenRuntime.UpdateActive(__instance, FireTabletActionGesture);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            ApplyTabletArmsOffset(__instance);
            if (IsTabletActive)
                FieldTabletAudio.FollowBoundAnchor();
            UpdateLocalThirdPersonPresentation(__instance);
        }

        /// <summary>
        /// The only thing that actually stops a pickup. <c>disableInteract</c> is checked inside
        /// <c>InteractTriggerUseConditionsMet</c>, but <c>Interact_performed</c> reaches
        /// <c>BeginGrabObject</c> before that gate, and <c>BeginGrabObject</c> only consults
        /// <c>inSpecialInteractAnimation</c>, which the tablet never sets. Grabbing here would
        /// rebuild a held-item presentation on top of the live tablet body animation.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "BeginGrabObject")]
        [HarmonyPrefix]
        private static bool BlockGrabWhileTabletActivePrefix(PlayerControllerB __instance)
        {
            return !IsTabletActive || !IsLocalPlayer(__instance);
        }

        /// <summary>
        /// Covers the empty-handed case that <c>disableItemActions</c> cannot: with nothing held,
        /// <c>HeldItemStowService.Begin</c> never creates an entry, so it never disables the input
        /// actions. Also covers third-party hotbar mods that call the handlers directly.
        /// <c>SwitchToItemSlot</c> is deliberately not blocked -- <c>ExtraSlotManager</c> calls it
        /// directly and blanket-blocking it would wedge the extra slot.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "ScrollMouse_performed")]
        [HarmonyPrefix]
        private static bool BlockSlotScrollWhileTabletActivePrefix(PlayerControllerB __instance)
        {
            return !IsTabletActive || !IsLocalPlayer(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "UseUtilitySlot_performed")]
        [HarmonyPrefix]
        private static bool BlockUtilitySlotWhileTabletActivePrefix(PlayerControllerB __instance)
        {
            return !IsTabletActive || !IsLocalPlayer(__instance);
        }

        private static void UpdateTabletRaise(PlayerControllerB player)
        {
            _wallRaiseActive = IsFacingNearbyWall(player, out _wallHitDistance);
            ApplyActionReadyState();
        }

        private static bool IsFacingNearbyWall(PlayerControllerB player, out float hitDistance)
        {
            hitDistance = WallRaiseExitDistance;
            Camera camera = player != null ? player.gameplayCamera : null;
            if (camera == null)
                return false;

            float distance = _wallRaiseActive ? WallRaiseExitDistance : WallRaiseEnterDistance;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMask : Physics.DefaultRaycastLayers;
            if (!Physics.SphereCast(camera.transform.position, WallCastRadius, camera.transform.forward, out RaycastHit hit, distance, mask, QueryTriggerInteraction.Ignore))
                return false;

            if (Mathf.Abs(hit.normal.y) >= WallNormalMaxY)
                return false;

            hitDistance = hit.distance;
            return true;
        }

        private static float CalculateWallExtraPull()
        {
            float target = 0f;
            if (_wallRaiseActive)
            {
                float penetration01 = Mathf.Clamp01((WallRaiseEnterDistance - _wallHitDistance) / WallExtraPullDistanceWindow);
                target = penetration01 * Plugin.GetWallMaxExtraPullMeters();
            }

            _currentWallExtraPull = Mathf.Lerp(_currentWallExtraPull, target, SmoothFactor(WallExtraPullLerpSpeed));
            return _currentWallExtraPull;
        }

        private static void ApplyTabletArmsOffset(PlayerControllerB player)
        {
            Transform armsT = player != null ? player.localArmsTransform : null;
            Camera cam = player != null ? player.gameplayCamera : null;
            if (armsT == null || cam == null)
            {
                _currentArmsOffset = Vector3.zero;
                return;
            }

            Vector3 targetOffset = Vector3.zero;
            if (IsTabletActive)
            {
                bool raised = _actionReadyApplied;
                float pullMeters = (raised ? Plugin.GetTabletRaisedPullInMeters() : Plugin.GetTabletPullInMeters()) + CalculateWallExtraPull();
                float upMeters = raised ? 0f : Plugin.GetTabletPullUpMeters();
                targetOffset = -cam.transform.forward * pullMeters + cam.transform.up * upMeters;
            }
            else
            {
                _currentWallExtraPull = Mathf.Lerp(_currentWallExtraPull, 0f, SmoothFactor(WallExtraPullLerpSpeed));
            }

            _currentArmsOffset = Vector3.Lerp(_currentArmsOffset, targetOffset, SmoothFactor(TabletOffsetLerpSpeed));
            if (_currentArmsOffset.sqrMagnitude > 0.000001f)
                armsT.position += _currentArmsOffset;
        }

        private static float SmoothFactor(float speed)
        {
            return 1f - Mathf.Exp(-Mathf.Max(0.01f, speed) * Mathf.Max(0.0001f, Time.deltaTime));
        }

        /// <summary>
        /// Takes temporary ownership of the same HDRP near-focus parameter vanilla relaxes for
        /// inspected items. Only that parameter is touched: far focus and every other volume
        /// override continue to come from the active player graphics profile.
        /// </summary>
        private static void AcquireTabletNearFocus()
        {
            if (_ownsTabletNearFocus)
            {
                MaintainTabletNearFocus();
                return;
            }

            try
            {
                VolumeProfile profile = HUDManager.Instance != null
                    && HUDManager.Instance.playerGraphicsVolume != null
                    ? HUDManager.Instance.playerGraphicsVolume.sharedProfile
                    : null;
                if (profile == null
                    || !profile.TryGet<DepthOfField>(out DepthOfField depthOfField)
                    || depthOfField == null)
                {
                    if (!_missingTabletDepthOfFieldLogged)
                    {
                        _missingTabletDepthOfFieldLogged = true;
                        Plugin.Log?.LogWarning("[Field Tablet] player graphics DepthOfField was not available; near-focus ownership will retry.");
                    }
                    return;
                }

                _tabletDepthOfField = depthOfField;
                _savedNearFocusEnd = depthOfField.nearFocusEnd.value;
                _savedNearFocusOverride = depthOfField.nearFocusEnd.overrideState;
                _ownsTabletNearFocus = true;
                _missingTabletDepthOfFieldLogged = false;
                MaintainTabletNearFocus();
                Plugin.Log?.LogInfo(
                    $"[Field Tablet] near focus acquired saved={_savedNearFocusEnd:0.000}m "
                    + $"override={_savedNearFocusOverride} tablet={TabletNearFocusEnd:0.000}m");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] near-focus acquire failed: " + ex.Message);
            }
        }

        private static void MaintainTabletNearFocus()
        {
            if (!_ownsTabletNearFocus)
            {
                AcquireTabletNearFocus();
                return;
            }
            if (_tabletDepthOfField == null)
            {
                _ownsTabletNearFocus = false;
                AcquireTabletNearFocus();
                return;
            }

            // Re-assert while deployed. Menu/action inputs move the prop across the near-focus
            // threshold, and vanilla or another camera owner may also rewrite the shared profile.
            _tabletDepthOfField.nearFocusEnd.overrideState = true;
            if (!Mathf.Approximately(_tabletDepthOfField.nearFocusEnd.value, TabletNearFocusEnd))
                _tabletDepthOfField.nearFocusEnd.value = TabletNearFocusEnd;
        }

        private static void ReleaseTabletNearFocus()
        {
            if (!_ownsTabletNearFocus)
                return;

            DepthOfField depthOfField = _tabletDepthOfField;
            float savedValue = _savedNearFocusEnd;
            bool savedOverride = _savedNearFocusOverride;
            _tabletDepthOfField = null;
            _ownsTabletNearFocus = false;
            _savedNearFocusEnd = 0f;
            _savedNearFocusOverride = false;

            try
            {
                if (depthOfField == null)
                    return;
                depthOfField.nearFocusEnd.value = savedValue;
                depthOfField.nearFocusEnd.overrideState = savedOverride;
                Plugin.Log?.LogInfo(
                    $"[Field Tablet] near focus restored value={savedValue:0.000}m override={savedOverride}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] near-focus restore failed: " + ex.Message);
            }
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            SendTabletState(MsgTabletStop);
            StopTabletImmediate();
            StopAllRemoteTablets(InteractionAnimationStopReason.Shutdown);
            FieldTabletScreenRuntime.ResetUplinkState();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            StopTabletImmediate();
            StopAllRemoteTablets(InteractionAnimationStopReason.Shutdown);
            FieldTabletScreenRuntime.ResetUplinkState();
            _networkHandlersRegistered = false;
            _packRegistered = false;
        }

        private static void ToggleTablet(PlayerControllerB player)
        {
            if (IsTabletActive)
            {
                CourierDronePatch.StopLocalPilotMode(notifyOwner: false);
                BeginTabletExit(player);
                return;
            }

            TryStartTablet(player);
        }

        private static string _cachedTabletPath;
        private static string _cachedTabletPreviousPath;
        private static string _cachedTabletNextPath;
        private static string _cachedTabletUpPath;
        private static string _cachedTabletDownPath;
        private static string _cachedTabletActivatePath;
        private static string _cachedTabletPrimaryPath;
        private static string _cachedTabletBackPath;
        private static string[] _cachedIdlePrompt;
        private static string[] _cachedCommandPrompt;
        private static string[] _cachedActivePrompt;
        private static string[] _cachedActiveHackPrompt;

        private static void UpdateTabletPrompt(PlayerControllerB player)
        {
            if (!FieldOperationsUpgrade.IsUnlocked()
                || player == null
                || player.isPlayerDead
                || player.isTypingChat
                || player.inTerminalMenu
                || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen))
            {
                Y4ngzPromptOverlay.ClearPostPlayerMenuPrompt(PromptKey);
                Y4ngzPromptOverlay.SetCursorTipOverride(null);
                return;
            }

            Gui.IngameKeybinds keybinds = Gui.Plugin.Keybinds;
            string tabletPath = Gui.UpgradeInput.EffectivePath(keybinds?.FieldTablet);
            string previousPath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletPreviousTab);
            string nextPath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletNextTab);
            string upPath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletUp);
            string downPath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletDown);
            string activatePath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletActivate);
            string primaryPath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletPrimaryAction);
            string backPath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletBack);
            if (_cachedIdlePrompt == null
                || !string.Equals(_cachedTabletPath, tabletPath, StringComparison.Ordinal)
                || !string.Equals(_cachedTabletPreviousPath, previousPath, StringComparison.Ordinal)
                || !string.Equals(_cachedTabletNextPath, nextPath, StringComparison.Ordinal)
                || !string.Equals(_cachedTabletUpPath, upPath, StringComparison.Ordinal)
                || !string.Equals(_cachedTabletDownPath, downPath, StringComparison.Ordinal)
                || !string.Equals(_cachedTabletActivatePath, activatePath, StringComparison.Ordinal)
                || !string.Equals(_cachedTabletPrimaryPath, primaryPath, StringComparison.Ordinal)
                || !string.Equals(_cachedTabletBackPath, backPath, StringComparison.Ordinal))
            {
                _cachedTabletPath = tabletPath;
                _cachedTabletPreviousPath = previousPath;
                _cachedTabletNextPath = nextPath;
                _cachedTabletUpPath = upPath;
                _cachedTabletDownPath = downPath;
                _cachedTabletActivatePath = activatePath;
                _cachedTabletPrimaryPath = primaryPath;
                _cachedTabletBackPath = backPath;

                string tablet = Gui.UpgradeInput.DisplayLabel(keybinds?.FieldTablet, "Y");
                string tabs = Gui.UpgradeInput.DisplayPair(
                    keybinds?.TabletPreviousTab, "LEFT", keybinds?.TabletNextTab, "RIGHT");
                string select = Gui.UpgradeInput.DisplayPair(
                    keybinds?.TabletUp, "UP", keybinds?.TabletDown, "DOWN");
                string activate = Gui.UpgradeInput.DisplayLabel(keybinds?.TabletActivate, "ENTER");
                string splice = Gui.UpgradeInput.DisplayPair(
                    keybinds?.TabletActivate, "ENTER", keybinds?.TabletPrimaryAction, "LMB");
                string back = Gui.UpgradeInput.DisplayLabel(keybinds?.TabletBack, "BKSP");
                string stow = $"Tablet: [{tablet}] Stow";
                string navigation = $"[{tabs}] Tabs  [{select}] Select";
                _cachedIdlePrompt = new[] { $"Tablet: [{tablet}]" };
                _cachedCommandPrompt = new[] { stow, $"[{select}] Select", $"[{activate}] Execute", $"[{back}] Back" };
                _cachedActivePrompt = new[] { stow, navigation, $"[{activate}] Activate", $"[{back}] Back" };
                _cachedActiveHackPrompt = new[] { stow, navigation, $"[{splice}] Splice", $"[{back}] Back" };
            }

            if (!IsTabletActive)
            {
                Y4ngzPromptOverlay.SetPostPlayerMenuPromptLines(PromptKey, _cachedIdlePrompt);
                Y4ngzPromptOverlay.SetCursorTipOverride(null);
                return;
            }

            // Nothing can be interacted with or grabbed while the tablet is out, so any hover cue
            // vanilla still produces would be a lie. Say what the player actually has to do.
            Y4ngzPromptOverlay.SetCursorTipOverride(StowTabletCursorTip);

            if (FieldTabletScreenRuntime.IsCommandScreenOpen)
            {
                Y4ngzPromptOverlay.SetPostPlayerMenuPromptLines(PromptKey, _cachedCommandPrompt);
                return;
            }

            Y4ngzPromptOverlay.SetPostPlayerMenuPromptLines(
                PromptKey,
                FieldTabletScreenRuntime.HasHackableTarget ? _cachedActiveHackPrompt : _cachedActivePrompt);
        }

        private static void TryStartTablet(PlayerControllerB player)
        {
            if (!FieldOperationsUpgrade.IsUnlocked())
                return;
            if (!CanUse(player))
                return;
            if (!EnsurePackRegistered())
            {
                Plugin.Log?.LogWarning("[Field Tablet] Animation pack is not ready.");
                return;
            }
            if (!TryPocketHeldItem(player))
                return;

            var request = new InteractionAnimationRequest
            {
                Player = player,
                PackId = PackId,
                InteractionId = InteractionId,
            };

            if (!LCInteractionAnimationAPI.TryStartInteraction(request, out InteractionAnimationHandle handle, out string reason))
            {
                RestorePocketedItem(player);
                RestoreInteractInput(player);
                if (!_startFailureLogged)
                {
                    _startFailureLogged = true;
                    Plugin.Log?.LogWarning("[Field Tablet] start rejected: " + reason);
                }
                return;
            }

            _activeHandle = handle;
            _nextActionIndex = 1;
            _startFailureLogged = false;
            AcquireTabletNearFocus();
            FieldTabletScreenRuntime.OnTabletOpened();
            FieldTabletAudio.QueueEquip();
            SaveAndSuppressInteractInput(player);
            SendTabletState(MsgTabletStart);
        }

        private static void BeginTabletExit(PlayerControllerB player)
        {
            if (!_activeHandle.IsValid)
                return;

            FieldTabletAudio.PlayEquip();
            SendTabletState(MsgTabletStop);

            if (!LCInteractionAnimationAPI.TryBeginInteractionExit(_activeHandle, out string reason))
            {
                Plugin.Log?.LogWarning("[Field Tablet] graceful exit rejected: " + reason);
                LCInteractionAnimationAPI.TryStopInteraction(_activeHandle, InteractionAnimationStopReason.Requested);
                ClearInactiveHandle(player);
            }
        }

        private static void StopTabletImmediate()
        {
            if (_activeHandle.IsValid)
                LCInteractionAnimationAPI.TryStopInteraction(_activeHandle, InteractionAnimationStopReason.Shutdown);

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            ClearTabletState(player);
        }

        private static void ClearInactiveHandle(PlayerControllerB player)
        {
            if (!_activeHandle.IsValid)
                return;
            if (LCInteractionAnimationAPI.IsInteractionActive(_activeHandle))
                return;

            ClearTabletState(player);
        }

        private static void ClearTabletState(PlayerControllerB player)
        {
            RestoreLocalThirdPersonPresentation(player);
            CourierDronePatch.StopLocalPilotMode(notifyOwner: false);
            _activeHandle = InteractionAnimationHandle.Empty;
            ReleaseTabletNearFocus();
            FieldTabletScreenRuntime.Destroy();
            RestorePocketedItem(player);
            RestoreInteractInput(player);
            // Disconnect and end-of-game tear down without another PostPlayerUpdate, so the
            // takeover has to be released here rather than left for the prompt refresh.
            Y4ngzPromptOverlay.ClearPostPlayerMenuPrompt(PromptKey);
            Y4ngzPromptOverlay.SetCursorTipOverride(null);
            _nextActionIndex = 1;
            _wallRaiseActive = false;
            _wallHitDistance = WallRaiseExitDistance;
            _currentWallExtraPull = 0f;
            _actionReadyApplied = false;
            _pilotActionReadyHold = false;
            _commandScreenActionReadyHold = false;
            _gestureRaiseUntil = 0f;
        }

        private static bool EnsurePackRegistered()
        {
            if (_packRegistered)
                return true;
            if (Time.realtimeSinceStartup < _nextRegistrationAttemptAt)
                return false;

            _nextRegistrationAttemptAt = Time.realtimeSinceStartup + 2f;

            string assetRootPath = GetConsumerAssetRoot();
            string manifestPath = Path.Combine(assetRootPath, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                if (!_missingManifestLogged)
                {
                    _missingManifestLogged = true;
                    Plugin.Log?.LogWarning("[Field Tablet] manifest not found: " + manifestPath);
                }
                return false;
            }

            string manifestJson;
            try
            {
                manifestJson = File.ReadAllText(manifestPath);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Field Tablet] manifest read failed: " + ex.Message);
                return false;
            }

            var pack = new InteractionAnimationPackDefinition
            {
                PackId = PackId,
                Version = Plugin.Version,
                AssetRootPath = assetRootPath,
                Interactions = new[]
                {
                    new InteractionAnimationDefinition
                    {
                        InteractionId = InteractionId,
                        PresentationKind = InteractionAnimationPresentationKind.BodyWorld,
                        ManifestJson = manifestJson
                    }
                }
            };

            if (!LCInteractionAnimationAPI.TryRegisterInteractionPack(pack, out string reason))
            {
                if (string.Equals(reason, "pack_already_registered", StringComparison.OrdinalIgnoreCase))
                {
                    _packRegistered = true;
                    return true;
                }

                Plugin.Log?.LogWarning("[Field Tablet] pack registration failed: " + reason);
                return false;
            }

            _packRegistered = true;
            Plugin.Log?.LogInfo("[Field Tablet] registered Interactions animation pack.");
            return true;
        }

        private static string GetConsumerAssetRoot()
        {
            return Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
        }

        private static void FireTabletActionGesture()
        {
            if (!_activeHandle.IsValid)
                return;

            LCInteractionAnimationAPI.TrySetInteractionBool(_activeHandle, ActionReadyBoolName, true);
            LCInteractionAnimationAPI.TrySetInteractionInt(_activeHandle, ActionIndexIntName, _nextActionIndex);
            LCInteractionAnimationAPI.TryFireInteractionTrigger(_activeHandle, ActionTriggerName);
            _actionReadyApplied = true;
            _gestureRaiseUntil = Time.unscaledTime + GestureRaiseSeconds;
            _nextActionIndex++;
            if (_nextActionIndex > 4)
                _nextActionIndex = 1;
        }

        // Two-handed and custom weapons are no longer refused: the shared stow puts whatever is
        // held away like a hotbar switch and hands it back when the tablet closes.
        private static bool TryPocketHeldItem(PlayerControllerB player)
        {
            if (player == null)
                return false;

            SaveAndSuppressInteractInput(player);
            // disableItemActions kills SwitchItem/UseUtilitySlot/ActivateItem/DiscardHeldObject for
            // the local player, so a scroll or slot key cannot re-equip on top of the tablet body
            // animation. It only engages when something was actually held -- Begin returns an inert
            // handle before creating an entry when the hands are empty -- so the input-action
            // prefixes below carry the empty-handed case.
            _tabletStow = HeldItemStowService.Begin(
                player,
                StowReason,
                StowTimeoutSeconds,
                disableItemActions: true,
                force: true);
            return true;
        }

        private static void RestorePocketedItem(PlayerControllerB player)
        {
            _tabletStow.Dispose();
            _tabletStow = default;
        }

        private static void SaveAndSuppressInteractInput(PlayerControllerB player)
        {
            if (player == null || _savedDisableInteract)
                return;

            _savedDisableInteract = true;
            _savedDisableInteractValue = player.disableInteract;
            player.disableInteract = true;
        }

        private static void RestoreInteractInput(PlayerControllerB player)
        {
            if (!_savedDisableInteract)
                return;

            if (player != null)
                player.disableInteract = _savedDisableInteractValue;
            _savedDisableInteract = false;
            _savedDisableInteractValue = false;
        }

        private static bool CanUse(PlayerControllerB player)
        {
            return player != null
                && !player.isPlayerDead
                && !player.isTypingChat
                && !player.inTerminalMenu
                && (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen);
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static void RegisterNetworkHandlers()
        {
            if (_networkHandlersRegistered)
                return;

            CustomMessagingManager messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null)
                return;

            try
            {
                messaging.RegisterNamedMessageHandler(MsgTabletStart, OnReceiveTabletStart);
                messaging.RegisterNamedMessageHandler(MsgTabletStop, OnReceiveTabletStop);
                _networkHandlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Field Tablet] network handler registration failed: " + ex.Message);
            }
        }

        private static void SendTabletState(string messageName)
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            NetworkManager network = NetworkManager.Singleton;
            if (player == null || network == null || !network.IsClient || network.CustomMessagingManager == null)
                return;

            SendTabletState(messageName, player.playerClientId, network.IsServer ? (ulong?)null : NetworkManager.ServerClientId);
        }

        private static void SendTabletState(string messageName, ulong playerClientId, ulong? clientId)
        {
            CustomMessagingManager messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null)
                return;

            using (var writer = new FastBufferWriter(sizeof(ulong), Allocator.Temp))
            {
                writer.WriteValueSafe(playerClientId);
                if (clientId.HasValue)
                    messaging.SendNamedMessage(messageName, clientId.Value, writer, NetworkDelivery.ReliableSequenced);
                else
                    messaging.SendNamedMessageToAll(messageName, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnReceiveTabletStart(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong playerClientId);
                ApplyRemoteTabletStart(playerClientId);
                RelayTabletStateIfHost(MsgTabletStart, senderClientId, playerClientId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] malformed start message: " + ex.Message);
            }
        }

        private static void OnReceiveTabletStop(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong playerClientId);
                ApplyRemoteTabletStop(playerClientId);
                RelayTabletStateIfHost(MsgTabletStop, senderClientId, playerClientId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] malformed stop message: " + ex.Message);
            }
        }

        private static void RelayTabletStateIfHost(string messageName, ulong senderClientId, ulong playerClientId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network != null && network.IsServer && senderClientId != network.LocalClientId)
                SendTabletState(messageName, playerClientId, null);
        }

        private static void ApplyRemoteTabletStart(ulong playerClientId)
        {
            PlayerControllerB player = ResolvePlayer(playerClientId);
            if (player == null || IsLocalPlayer(player) || !EnsurePackRegistered())
                return;

            if (RemoteHandles.TryGetValue(playerClientId, out InteractionAnimationHandle existing))
            {
                if (existing.IsValid && LCInteractionAnimationAPI.IsInteractionActive(existing))
                    return;
                RemoteHandles.Remove(playerClientId);
            }

            var request = new InteractionAnimationRequest
            {
                Player = player,
                PackId = PackId,
                InteractionId = InteractionId,
            };

            if (LCInteractionAnimationAPI.TryStartInteraction(request, out InteractionAnimationHandle handle, out string reason))
            {
                RemoteHandles[playerClientId] = handle;
            }
            else
            {
                Plugin.Log?.LogWarning($"[Field Tablet] remote presentation start rejected for player {playerClientId}: {reason}");
            }
        }

        private static void ApplyRemoteTabletStop(ulong playerClientId)
        {
            if (!RemoteHandles.TryGetValue(playerClientId, out InteractionAnimationHandle handle))
                return;

            if (!handle.IsValid || !LCInteractionAnimationAPI.IsInteractionActive(handle) ||
                !LCInteractionAnimationAPI.TryBeginInteractionExit(handle, out _))
            {
                if (handle.IsValid)
                    LCInteractionAnimationAPI.TryStopInteraction(handle, InteractionAnimationStopReason.Requested);
                RemoteHandles.Remove(playerClientId);
            }
        }

        private static void ClearInactiveRemoteHandles()
        {
            if (RemoteHandles.Count == 0)
                return;

            var stale = new List<ulong>();
            foreach (KeyValuePair<ulong, InteractionAnimationHandle> pair in RemoteHandles)
            {
                if (!pair.Value.IsValid || !LCInteractionAnimationAPI.IsInteractionActive(pair.Value))
                    stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                RemoteHandles.Remove(stale[i]);
        }

        private static void StopAllRemoteTablets(InteractionAnimationStopReason reason)
        {
            foreach (InteractionAnimationHandle handle in RemoteHandles.Values)
            {
                if (handle.IsValid)
                    LCInteractionAnimationAPI.TryStopInteraction(handle, reason);
            }
            RemoteHandles.Clear();
        }

        private static PlayerControllerB ResolvePlayer(ulong playerClientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.playerClientId == playerClientId)
                    return player;
            }
            return null;
        }

        internal static void ReleaseLocalThirdPersonPresentationForDrone()
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            RestoreLocalThirdPersonPresentation(player);
        }

        private static void UpdateLocalThirdPersonPresentation(PlayerControllerB player)
        {
            if (!IsTabletActive || CourierDronePatch.IsLocalPilotModeActive || !IsThirdPersonCamera(player))
            {
                RestoreLocalThirdPersonPresentation(player);
                return;
            }

            // The debug third-person camera (Y4NGZDebugTools) snapshots and restores the
            // body, first-person arms, and local head cosmetics itself, and its LateUpdate
            // postfix (Priority.Last) runs after this one. Touching those renderers here
            // poisons its "original" snapshot and leaves the body/head rendering into the
            // first-person camera after the debug camera exits. The tablet therefore only
            // owns its own prop renderers; everything else yields to the debug tool.
            if (!_thirdPersonPresentationSaved)
            {
                _thirdPersonPresentationSaved = true;
                ThirdPersonRendererStates.Clear();
                Renderer[] renderers = player.GetComponentsInChildren<Renderer>(includeInactive: true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (IsTabletRenderer(renderers[i]))
                        ThirdPersonRendererStates.Add(new TabletRendererState(renderers[i]));
                }
            }

            int defaultLayer = LayerMask.NameToLayer("Default");
            if (defaultLayer < 0)
                defaultLayer = 0;

            for (int i = 0; i < ThirdPersonRendererStates.Count; i++)
            {
                Renderer renderer = ThirdPersonRendererStates[i].Renderer;
                if (renderer == null)
                    continue;

                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.gameObject.layer = defaultLayer;
            }
        }

        // Restores every renderer state the tablet still owns (tablet prop renderers only).
        // Runs unconditionally from ClearTabletState so ending the session while the debug
        // third-person camera is still active cannot strand a forced state.
        private static void RestoreLocalThirdPersonPresentation(PlayerControllerB player)
        {
            if (!_thirdPersonPresentationSaved)
                return;

            for (int i = 0; i < ThirdPersonRendererStates.Count; i++)
                ThirdPersonRendererStates[i].Restore();
            ThirdPersonRendererStates.Clear();
            _thirdPersonPresentationSaved = false;
        }

        private static bool IsThirdPersonCamera(PlayerControllerB player)
        {
            // Never infer camera mode from an animated bone. Authored clips can move the head
            // far from the gameplay camera and previously triggered a false third-person switch.
            return player != null && LedgeMantlePatch.IsDebugThirdPersonCamActive();
        }

        private static bool IsTabletRenderer(Renderer renderer)
        {
            Transform current = renderer != null ? renderer.transform : null;
            while (current != null)
            {
                string name = current.name ?? string.Empty;
                if (name.IndexOf("Y4NGZ_FPSTabletProp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Y4NGZ_DroneTablet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Tablet_01", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                current = current.parent;
            }
            return false;
        }

        private readonly struct TabletRendererState
        {
            internal readonly Renderer Renderer;
            private readonly bool _enabled;
            private readonly bool _forceRenderingOff;
            private readonly ShadowCastingMode _shadowCastingMode;
            private readonly int _layer;

            internal TabletRendererState(Renderer renderer)
            {
                Renderer = renderer;
                _enabled = renderer != null && renderer.enabled;
                _forceRenderingOff = renderer != null && renderer.forceRenderingOff;
                _shadowCastingMode = renderer != null ? renderer.shadowCastingMode : ShadowCastingMode.Off;
                _layer = renderer != null && renderer.gameObject != null ? renderer.gameObject.layer : 0;
            }

            internal void Restore()
            {
                if (Renderer == null)
                    return;
                Renderer.enabled = _enabled;
                Renderer.forceRenderingOff = _forceRenderingOff;
                Renderer.shadowCastingMode = _shadowCastingMode;
                if (Renderer.gameObject != null)
                    Renderer.gameObject.layer = _layer;
            }
        }

    }

    /// <summary>
    /// Client -> host routing for the Field Operations tablet MAINFRAME tab.
    /// The uplink itself is local-authoritative (client-side channel); the host
    /// validates the requester's Field Operations tier via UpgradeTierSync and
    /// enforces the per-function cooldowns before touching Company/vanilla state.
    /// Idiom mirrors FieldMechanicCompanyPatch.CameraHackPatch.
    /// </summary>
    [HarmonyPatch]
    internal static class FieldTabletMainframeNet
    {
        private const string MsgCameraDisable = "Y4NGZFieldTablet.CameraDisableServerRpc";
        private const string MsgAlarm = "Y4NGZFieldTablet.AlarmServerRpc";
        private const string MsgLockdown = "Y4NGZFieldTablet.LockdownServerRpc";
        private const string MsgTrapsDisable = "Y4NGZFieldTablet.TrapsDisableServerRpc";
        private const string MsgActionResult = "Y4NGZFieldTablet.ActionResultClientRpc";

        internal const byte ActionLockdownBegin = 1;
        internal const byte ActionLockdownEnd = 2;
        internal const byte ActionTraps = 3;

        internal const byte AlarmOpOff = 0;
        internal const byte AlarmOpOn = 1;
        internal const byte AlarmOpSilence = 2;

        // Host-enforced cooldowns (mirrored client-side for UI only).
        private const float CameraDisableCooldownSeconds = 20f;
        private const float LockdownCooldownSeconds = 30f;
        private const float TrapsDisableCooldownSeconds = 90f;
        private const float TrapDisableFallbackSeconds = 12f;

        private static bool _handlersRegistered;
        private static float _hostCameraReadyAt;
        private static float _hostLockdownReadyAt;
        private static float _hostTrapsReadyAt;
        private static readonly List<PendingTrapReenable> PendingReenables = new List<PendingTrapReenable>();

        private struct PendingTrapReenable
        {
            internal Component Trap;
            internal byte Kind; // 0 turret, 1 landmine, 2 spike trap
            internal float ReenableAt;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _handlersRegistered = false;
            PendingReenables.Clear();
            _hostCameraReadyAt = 0f;
            _hostLockdownReadyAt = 0f;
            _hostTrapsReadyAt = 0f;
        }

        // Host tick for the direct-RPC fallback re-enables (objects without a
        // TerminalAccessibleObject; the TAO path re-enables itself like the ship
        // terminal does).
        [HarmonyPatch(typeof(StartOfRound), "Update")]
        [HarmonyPostfix]
        private static void PostStartOfRoundUpdate()
        {
            if (PendingReenables.Count == 0)
                return;
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
            {
                PendingReenables.Clear();
                return;
            }

            float now = Time.unscaledTime;
            for (int i = PendingReenables.Count - 1; i >= 0; i--)
            {
                PendingTrapReenable pending = PendingReenables[i];
                if (now < pending.ReenableAt)
                    continue;
                PendingReenables.RemoveAt(i);
                if (pending.Trap == null)
                    continue;
                try
                {
                    ToggleTrap(pending.Trap, pending.Kind, enable: true);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogDebug("[Field Tablet] trap re-enable failed: " + ex.Message);
                }
            }
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered)
                return;

            CustomMessagingManager messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null)
                return;

            try
            {
                messaging.RegisterNamedMessageHandler(MsgActionResult, OnActionResult);
                if (NetworkManager.Singleton.IsServer)
                {
                    messaging.RegisterNamedMessageHandler(MsgCameraDisable, OnCameraDisableRequest);
                    messaging.RegisterNamedMessageHandler(MsgAlarm, OnAlarmRequest);
                    messaging.RegisterNamedMessageHandler(MsgLockdown, OnLockdownRequest);
                    messaging.RegisterNamedMessageHandler(MsgTrapsDisable, OnTrapsDisableRequest);
                }
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Field Tablet] mainframe net handler registration failed: " + ex.Message);
            }
        }

        // - Client entry points (host handles locally) -

        internal static void RequestCameraDisable(int cameraId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return;
            if (network.IsServer)
            {
                HandleCameraDisable(network.LocalClientId, cameraId);
                return;
            }

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null)
                return;
            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(cameraId);
                messaging.SendNamedMessage(MsgCameraDisable, NetworkManager.ServerClientId, writer);
            }
        }

        internal static void RequestAlarm(byte op)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return;
            if (network.IsServer)
            {
                HandleAlarm(network.LocalClientId, op);
                return;
            }

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null)
                return;
            using (var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp))
            {
                writer.WriteValueSafe(op);
                messaging.SendNamedMessage(MsgAlarm, NetworkManager.ServerClientId, writer);
            }
        }

        internal static void RequestLockdown(bool begin)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return;
            if (network.IsServer)
            {
                HandleLockdown(network.LocalClientId, begin);
                return;
            }

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null)
                return;
            using (var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp))
            {
                writer.WriteValueSafe(begin ? (byte)1 : (byte)0);
                messaging.SendNamedMessage(MsgLockdown, NetworkManager.ServerClientId, writer);
            }
        }

        internal static void RequestTrapsDisable()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return;
            if (network.IsServer)
            {
                HandleTrapsDisable(network.LocalClientId);
                return;
            }

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null)
                return;
            using (var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp))
            {
                writer.WriteValueSafe((byte)1);
                messaging.SendNamedMessage(MsgTrapsDisable, NetworkManager.ServerClientId, writer);
            }
        }

        // - Host handlers -

        private static bool SenderHasFieldOperations(ulong senderClientId)
        {
            return UpgradeTierSync.GetTier(senderClientId, FieldOperationsUpgrade.UPGRADE_ID) >= 1;
        }

        private static void OnCameraDisableRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;
            int cameraId;
            try
            {
                reader.ReadValueSafe(out cameraId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Field Tablet] malformed camera disable request: " + ex.Message);
                return;
            }
            HandleCameraDisable(senderClientId, cameraId);
        }

        private static void HandleCameraDisable(ulong senderClientId, int cameraId)
        {
            if (!SenderHasFieldOperations(senderClientId))
                return;
            float now = Time.unscaledTime;
            if (now < _hostCameraReadyAt)
                return;
            if (!IsCompanyPresent())
                return;

            _hostCameraReadyAt = now + CameraDisableCooldownSeconds;
            HostTryDisableCamera(cameraId);
        }

        private static void OnAlarmRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;
            byte op;
            try
            {
                reader.ReadValueSafe(out op);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Field Tablet] malformed alarm request: " + ex.Message);
                return;
            }
            HandleAlarm(senderClientId, op);
        }

        private static void HandleAlarm(ulong senderClientId, byte op)
        {
            if (!SenderHasFieldOperations(senderClientId) || !IsCompanyPresent())
                return;

            try
            {
                HostApplyAlarmOp(op);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Field Tablet] alarm op failed: " + ex.Message);
            }
        }

        private static void OnLockdownRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;
            byte begin;
            try
            {
                reader.ReadValueSafe(out begin);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Field Tablet] malformed lockdown request: " + ex.Message);
                return;
            }
            HandleLockdown(senderClientId, begin != 0);
        }

        private static void HandleLockdown(ulong senderClientId, bool begin)
        {
            if (!SenderHasFieldOperations(senderClientId))
                return;
            float now = Time.unscaledTime;
            if (now < _hostLockdownReadyAt)
                return;
            if (!IsCompanyPresent())
            {
                SendActionResult(senderClientId, begin ? ActionLockdownBegin : ActionLockdownEnd, success: false, 0f, 0);
                return;
            }

            _hostLockdownReadyAt = now + LockdownCooldownSeconds;
            bool ok = HostTryLockdown(begin);
            SendActionResult(senderClientId, begin ? ActionLockdownBegin : ActionLockdownEnd, ok, 0f, 0);
        }

        private static void OnTrapsDisableRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;
            HandleTrapsDisable(senderClientId);
        }

        /// <summary>
        /// Vanilla temporary disable for every Turret/Landmine/SpikeRoofTrap: prefer
        /// the object's own TerminalAccessibleObject flash-code path (event disables,
        /// cooldown coroutine re-enables — exactly what the ship terminal does); fall
        /// back to the Toggle*ServerRpc(false) + scheduled re-enable for objects
        /// without one.
        /// </summary>
        private static void HandleTrapsDisable(ulong senderClientId)
        {
            if (!SenderHasFieldOperations(senderClientId))
                return;
            float now = Time.unscaledTime;
            if (now < _hostTrapsReadyAt)
                return;

            _hostTrapsReadyAt = now + TrapsDisableCooldownSeconds;

            int affected = 0;
            float maxDuration = 0f;
            try
            {
                Turret[] turrets = UnityEngine.Object.FindObjectsOfType<Turret>();
                for (int i = 0; i < turrets.Length; i++)
                {
                    if (turrets[i] != null && turrets[i].turretActive && DisableTrapVanillaStyle(turrets[i], 0, ref maxDuration))
                        affected++;
                }

                Landmine[] mines = UnityEngine.Object.FindObjectsOfType<Landmine>();
                for (int i = 0; i < mines.Length; i++)
                {
                    if (mines[i] != null && !mines[i].hasExploded && DisableTrapVanillaStyle(mines[i], 1, ref maxDuration))
                        affected++;
                }

                SpikeRoofTrap[] spikes = UnityEngine.Object.FindObjectsOfType<SpikeRoofTrap>();
                for (int i = 0; i < spikes.Length; i++)
                {
                    if (spikes[i] != null && DisableTrapVanillaStyle(spikes[i], 2, ref maxDuration))
                        affected++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Field Tablet] traps disable sweep failed: " + ex.Message);
            }

            SendActionResult(senderClientId, ActionTraps, affected > 0, maxDuration, affected);
        }

        private static bool DisableTrapVanillaStyle(Component trap, byte kind, ref float maxDuration)
        {
            TerminalAccessibleObject accessible = trap.GetComponent<TerminalAccessibleObject>()
                ?? trap.GetComponentInParent<TerminalAccessibleObject>()
                ?? trap.GetComponentInChildren<TerminalAccessibleObject>();
            if (accessible != null && !accessible.inCooldown)
            {
                accessible.CallFunctionFromTerminal();
                float duration = accessible.codeAccessCooldownTimer > 0f
                    ? accessible.codeAccessCooldownTimer
                    : TrapDisableFallbackSeconds;
                maxDuration = Mathf.Max(maxDuration, duration);
                return true;
            }
            if (accessible != null)
                return false; // Already flashing/cooling down; leave the vanilla cycle alone.

            ToggleTrap(trap, kind, enable: false);
            PendingReenables.Add(new PendingTrapReenable
            {
                Trap = trap,
                Kind = kind,
                ReenableAt = Time.unscaledTime + TrapDisableFallbackSeconds
            });
            maxDuration = Mathf.Max(maxDuration, TrapDisableFallbackSeconds);
            return true;
        }

        private static void ToggleTrap(Component trap, byte kind, bool enable)
        {
            switch (kind)
            {
                case 0:
                    ((Turret)trap).ToggleTurretServerRpc(enable);
                    break;
                case 1:
                    ((Landmine)trap).ToggleMineServerRpc(enable);
                    break;
                case 2:
                    ((SpikeRoofTrap)trap).ToggleSpikesServerRpc(enable);
                    break;
            }
        }

        // - Result feedback -

        private static void SendActionResult(ulong targetClientId, byte action, bool success, float value, int count)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return;

            if (targetClientId == network.LocalClientId)
            {
                FieldTabletScreenRuntime.OnMainframeActionResult(action, success, value, count);
                return;
            }

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null)
                return;
            // action(1) + success(1) + value(4) + count(1)
            using (var writer = new FastBufferWriter(sizeof(byte) * 3 + sizeof(float), Allocator.Temp))
            {
                writer.WriteValueSafe(action);
                writer.WriteValueSafe(success ? (byte)1 : (byte)0);
                writer.WriteValueSafe(value);
                writer.WriteValueSafe((byte)Mathf.Clamp(count, 0, byte.MaxValue));
                messaging.SendNamedMessage(MsgActionResult, targetClientId, writer);
            }
        }

        private static void OnActionResult(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out byte action);
                reader.ReadValueSafe(out byte success);
                reader.ReadValueSafe(out float value);
                reader.ReadValueSafe(out byte count);
                FieldTabletScreenRuntime.OnMainframeActionResult(action, success != 0, value, count);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] malformed action result: " + ex.Message);
            }
        }

        // - Company calls (non-inlined behind the presence gate, FieldMechanic idiom) -

        private static bool? _companyPresent;

        private static bool IsCompanyPresent()
        {
            // Every non-inlined call below reaches CctvSupportApi or MainframeSupport, which
            // Y4NGZCompany#393 moved into LethalCCTV.dll. Gating on com.y4ngz.company would let
            // these JIT with the CCTV plugin absent.
            if (!_companyPresent.HasValue)
                _companyPresent = OptionalPluginCapabilities.LethalCctv;
            return _companyPresent.Value;
        }

        private static bool HostTryDisableCamera(int cameraId)
        {
            return OptionalCctvBridge.TryDisableCamera(cameraId);
        }

        private static bool HostTryLockdown(bool begin)
        {
            return OptionalCctvBridge.TrySetLockdown(begin);
        }

        private static void HostApplyAlarmOp(byte op)
        {
            OptionalCctvBridge.ApplyAlarmOperation(op, AlarmOpSilence);
        }
    }
}
