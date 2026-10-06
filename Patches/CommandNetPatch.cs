using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZUpgrades.Interactive.Hud;
using Y4NGZUpgrades.UITheme;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class CommandNetPatch
    {
        private const string MSG_WALKIE = "CommandNet_Walkie";
        private const string MSG_DAMAGE_OUTLINE = "CommandNet_DamageOutline";
        private const string PROMPT_KEY = "command_net_walkie";
        // F-FOREMAN-B-6: 5 Hz. Every refresh is a second full HDRP camera render on top of
        // vanilla's own radar render, for a widget that is 130 px across.
        private const float MINIMAP_REFRESH_INTERVAL = 0.20f;
        private const float MINIMAP_SIZE = 130f;
        private const float MINIMAP_MARGIN = 18f;
        private const float MINIMAP_CHAT_GAP = 10f;
        private const float MINIMAP_INSET = 5f;
        private const float MINIMAP_BORDER_THICKNESS = 1.5f;
        private const float MINIMAP_BORDER_ALPHA = 0.84f;
        private const float MINIMAP_GRID_ALPHA = 0.20f;
        private const float MINIMAP_CAMERA_HEIGHT = 3.636f;
        private const float MINIMAP_BOOT_DURATION = 0.42f;
        private const float MINIMAP_BOOT_LINE_PHASE = 0.16f;
        private const float MINIMAP_TACTICAL_DOT_SIZE = 7.5f;
        private const float MINIMAP_TACTICAL_DOT_INSET = 1.35f;
        // F-FOREMAN-B-6: 256 px is already more than the 130 px widget resolves.
        private const int MINIMAP_TEXTURE_SIZE = 256;
        private const int MINIMAP_SCANLINE_COUNT = 11;
        // F-FOREMAN-B-14: the dedup sets used to grow for a whole round. A few hundred ids is far
        // more than the reliable-sequenced reorder window these messages can produce.
        private const int SEEN_MESSAGE_CAPACITY = 256;

        private static readonly HashSet<long> SeenWalkieMessages = new HashSet<long>();
        private static readonly Queue<long> SeenWalkieOrder = new Queue<long>();
        private static readonly HashSet<long> SeenDamageMessages = new HashSet<long>();
        private static readonly Queue<long> SeenDamageOrder = new Queue<long>();

        private static bool _handlersRegistered;
        private static uint _nextMessageCounter;
        private static PlayerControllerB _listeningPlayer;
        private static bool _localSpeaking;
        private static float _nextMinimapRefresh;
        private static bool _presentationVisible;
        private static bool _hangarStateKnown;
        private static bool _wasInHangarShipRoom;
        private static Vector2 _hudMotionOffset;
        private static EntranceTeleport[] _cachedEntrances = Array.Empty<EntranceTeleport>();
        private static bool _entrancesCached;
        private static bool _cameraMarkerFailureLogged;

        private static GameObject _minimapRoot;
        private static RectTransform _minimapRect;
        private static CanvasGroup _minimapCanvasGroup;
        private static RawImage _radarImage;
        private static RectTransform _tacticalMarkerLayer;
        private static RectTransform _markerLayer;
        private static Image _bootLine;
        private static RenderTexture _minimapTexture;
        private static Camera _minimapCamera;
        private static Camera _minimapSourceCamera;
        private static Sprite _pixelSprite;
        private static Sprite _circleSprite;
        private static Sprite _vignetteSprite;
        private static RectTransform _chatRect;
        private static Vector2 _chatOriginalAnchoredPosition;
        private static bool _chatLayoutCaptured;
        private static float _minimapBootStartTime;
        private static bool _minimapBootActive;
        private static readonly List<Image> MinimapMarkers = new List<Image>();
        private static readonly List<MinimapTacticalDot> MinimapTacticalDots = new List<MinimapTacticalDot>();
        private static readonly List<Image> MinimapAccentImages = new List<Image>();
        private static readonly List<Image> MinimapGridImages = new List<Image>();
        private static readonly List<Image> MinimapScanlineImages = new List<Image>();

        private sealed class MinimapTacticalDot
        {
            public RectTransform Rect;
            public Image Fill;
        }

        private struct VanillaMapUiState
        {
            public bool MapScreenPlayerNameEnabled;
            public bool MapScreenPlayerNameBgEnabled;
            public bool HeadMountedCamUiEnabled;
            public bool LocalPlayerPlaceholderEnabled;
            public bool CompassRoseEnabled;
            public bool ShipArrowUiActive;
            public bool ShipIconActive;
            public bool LostSignalUiActive;
            public bool ExitLineEnabled;
        }

        private static VanillaMapUiState _vanillaMapUiState;

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            RegisterNetworkHandlers();
            ClearSeenMessages();
            _nextMinimapRefresh = 0f;
            _hangarStateKnown = false;
            _wasInHangarShipRoom = true;
            _cachedEntrances = Array.Empty<EntranceTeleport>();
            _entrancesCached = false;
            _cameraMarkerFailureLogged = false;
            SetMinimapPresentationActive(false);
            SetLocalSpeaking(false);
        }

        internal static void SetPresentationVisible(bool visible)
        {
            _presentationVisible = visible;
            if (!visible)
                SetMinimapPresentationActive(false);
        }

        internal static void SetHudMotionOffset(Vector2 offset)
        {
            if ((_hudMotionOffset - offset).sqrMagnitude <= 0.0001f)
                return;

            _hudMotionOffset = offset;
            ApplyMinimapLayout();
            if (_minimapRoot != null && _minimapRoot.activeSelf)
                ApplyChatLayoutForMinimap();
        }

        internal static void RefreshPresentationTheme()
        {
            RefreshMinimapStyle();
        }

        internal static void ShutdownPresentation()
        {
            _presentationVisible = false;
            _hudMotionOffset = Vector2.zero;
            _hangarStateKnown = false;
            _cachedEntrances = Array.Empty<EntranceTeleport>();
            _entrancesCached = false;
            DestroyMinimap();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            SetLocalSpeaking(false);
            ExitVirtualRadio();
            DestroyMinimap();
            Y4ngzPromptOverlay.ClearPostPlayerMenuPrompt(PROMPT_KEY);
            ClearSeenMessages();
        }

        private static void ClearSeenMessages()
        {
            SeenWalkieMessages.Clear();
            SeenWalkieOrder.Clear();
            SeenDamageMessages.Clear();
            SeenDamageOrder.Clear();
        }

        /// <summary>
        /// F-FOREMAN-B-14: dedup with a bounded window. The sets used to be cleared only at round
        /// start and end, so they grew monotonically for a whole round.
        /// </summary>
        private static bool TryMarkSeen(HashSet<long> seen, Queue<long> order, long messageId)
        {
            if (!seen.Add(messageId))
                return false;

            order.Enqueue(messageId);
            while (order.Count > SEEN_MESSAGE_CAPACITY)
                seen.Remove(order.Dequeue());
            return true;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            RegisterNetworkHandlers();
            UpdateVirtualRadioState(__instance);
            PollTransmitKey(__instance);
            UpdateCommandPrompt(__instance);
            UpdateMinimap(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPostfix]
        private static void PostDamagePlayer(PlayerControllerB __instance, int damageNumber)
        {
            if (!IsLocalPlayer(__instance))
                return;
            if (damageNumber <= 0 || __instance.isPlayerDead)
                return;

            int playerId = (int)__instance.playerClientId;
            long messageId = NextMessageId();
            HandleDamageNotice(messageId, playerId);
            SendDamageNotice(messageId, playerId);
        }

        private static void UpdateVirtualRadioState(PlayerControllerB player)
        {
            if (player == null || player.isPlayerDead || !CommandNetUpgrade.HasVirtualWalkie())
            {
                SetLocalSpeaking(false);
                ExitVirtualRadio();
                return;
            }

            EnterVirtualRadio(player);
        }

        private static void PollTransmitKey(PlayerControllerB player)
        {
            if (player == null || !CommandNetUpgrade.HasVirtualWalkie())
                return;

            bool canTransmit = !player.isPlayerDead
                               && !player.isTypingChat
                               && !player.inTerminalMenu
                               && !player.inSpecialInteractAnimation
                               && (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen);

            bool keyDown = canTransmit
                && Gui.UpgradeInput.IsPressed(Gui.Plugin.Keybinds?.CommandNetTransmit);
            SetLocalSpeaking(keyDown);
        }

        private static void EnterVirtualRadio(PlayerControllerB player)
        {
            if (player == null)
                return;

            // F-FOREMAN-B-1: this runs from a PlayerControllerB.Update postfix, so it is reached
            // every frame. UpdatePlayerVoiceEffects() walks every player doing three GetComponent
            // calls each and resets vanilla's own 2 s interval, so it may only run on an actual
            // transition - never on a frame where the state is already what we want.
            if (_listeningPlayer == player && player.holdingWalkieTalkie)
                return;

            if (_listeningPlayer != player)
            {
                ExitVirtualRadio();
                _listeningPlayer = player;
            }

            player.holdingWalkieTalkie = true;
            UpdateVoiceEffects();
        }

        private static void ExitVirtualRadio()
        {
            // F-FOREMAN-B-1: nothing to undo means nothing to recompute. This used to fall
            // through to UpdateVoiceEffects() every frame for every player, upgrade or not.
            if (_listeningPlayer == null)
                return;

            PlayerControllerB player = _listeningPlayer;
            _listeningPlayer = null;
            // F-FOREMAN-B-15: the walkie item owns this flag. Restoring a snapshot taken on the
            // first Enter wrote a stale false over a player who had since switched a real walkie
            // on, leaving them deaf to it; re-read the live state from their own inventory.
            player.holdingWalkieTalkie = HasSwitchedOnWalkie(player);
            UpdateVoiceEffects();
        }

        private static bool HasSwitchedOnWalkie(PlayerControllerB player)
        {
            if (player == null || player.ItemSlots == null)
                return false;

            for (int i = 0; i < player.ItemSlots.Length; i++)
            {
                WalkieTalkie walkie = player.ItemSlots[i] as WalkieTalkie;
                if (walkie != null && walkie.isBeingUsed)
                    return true;
            }

            return false;
        }

        private static void SetLocalSpeaking(bool speaking)
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null)
            {
                _localSpeaking = false;
                return;
            }

            if (!speaking && !_localSpeaking)
                return;
            if (speaking && _localSpeaking && player.speakingToWalkieTalkie)
                return;

            if (speaking && _listeningPlayer == null)
                EnterVirtualRadio(player);

            _localSpeaking = speaking;
            long messageId = NextMessageId();
            int playerId = (int)player.playerClientId;
            ApplyWalkieMessage(messageId, playerId, speaking, localSender: true);
            SendWalkieMessage(messageId, playerId, speaking);
        }

        private static void ApplyWalkieMessage(long messageId, int playerId, bool speaking, bool localSender)
        {
            if (!TryMarkSeen(SeenWalkieMessages, SeenWalkieOrder, messageId))
                return;

            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.allPlayerScripts == null)
                return;
            if (playerId < 0 || playerId >= round.allPlayerScripts.Length)
                return;

            PlayerControllerB player = round.allPlayerScripts[playerId];
            if (player == null)
                return;

            player.speakingToWalkieTalkie = speaking;
            if (localSender)
            {
                player.activatingItem = speaking;
                if (player.playerBodyAnimator != null)
                    player.playerBodyAnimator.SetBool("walkieTalkie", speaking);
            }

            // F-FOREMAN-B-4: the old SetVirtualSpeakerState walked WalkieTalkie.allWalkieTalkies and
            // set clientIsHoldingAndSpeakingIntoThis on *every* switched-on walkie in the world.
            // In vanilla WalkieTalkie.Update that flag is what puts a walkie into another walkie's
            // talkiesSendingToThis list, so keying the Command Net channel made every unrelated
            // walkie start relaying its own holder's ambient sounds. The vanilla radio SFX this was
            // reaching for is already handled by PlayTransmissionSfx, and the voice routing itself
            // only needs speakingToWalkieTalkie (set above) plus the listener's holdingWalkieTalkie.
            PlayTransmissionSfx(playerId, speaking);
            UpdateVoiceEffects();
        }

        private static void PlayTransmissionSfx(int playerId, bool speaking)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round == null || WalkieTalkie.allWalkieTalkies == null)
                return;

            PlayerControllerB speakingPlayer = playerId >= 0 && playerId < round.allPlayerScripts.Length
                ? round.allPlayerScripts[playerId]
                : null;

            for (int i = 0; i < WalkieTalkie.allWalkieTalkies.Count; i++)
            {
                WalkieTalkie walkie = WalkieTalkie.allWalkieTalkies[i];
                if (walkie == null || !walkie.isBeingUsed || walkie.thisAudio == null)
                    continue;
                if (speakingPlayer != null && walkie.playerHeldBy == speakingPlayer)
                    continue;
                if (IsPocketedHeldWalkie(walkie))
                    continue;

                AudioClip[] clips = speaking ? walkie.startTransmissionSFX : walkie.stopTransmissionSFX;
                if (clips == null || clips.Length == 0)
                    continue;
                RoundManager.PlayRandomClip(walkie.thisAudio, clips);
            }
        }

        private static bool IsPocketedHeldWalkie(WalkieTalkie walkie)
        {
            if (walkie == null || walkie.playerHeldBy == null)
                return false;
            GrabbableObject held = walkie.playerHeldBy.currentlyHeldObjectServer;
            return held != null && held.GetComponent<WalkieTalkie>() != null && walkie.isPocketed;
        }

        private static void UpdateVoiceEffects()
        {
            if (StartOfRound.Instance == null)
                return;

            try
            {
                StartOfRound.Instance.UpdatePlayerVoiceEffects();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"[Command Net] Voice update skipped: {ex.Message}");
            }
        }

        private static void HandleDamageNotice(long messageId, int playerId)
        {
            if (!TryMarkSeen(SeenDamageMessages, SeenDamageOrder, messageId))
                return;
            if (!CommandNetUpgrade.HasDamageOutline())
                return;

            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.allPlayerScripts == null)
                return;
            if (playerId < 0 || playerId >= round.allPlayerScripts.Length)
                return;

            PlayerControllerB damaged = round.allPlayerScripts[playerId];
            if (damaged == null || damaged.isPlayerDead || !damaged.isPlayerControlled)
                return;

            // F-FOREMAN-B-3: apply the outline locally only. The damage notice is already fanned
            // out to every client and gated here on the *receiver* owning level 2, so every
            // eligible player paints their own outline. Re-fanning a ping mark from each receiver
            // amplified O(N^2) and, because the ping receive path has no ownership check, painted
            // the outline on players who own neither upgrade.
            // F-FOREMAN-B-12: the duration comes from Command Net's own constant so the catalog's
            // "5 seconds" cannot drift when Ping is retuned.
            ForemanPingPatch.MarkTeammateFromCommandNetLocal(damaged, CommandNetUpgrade.DAMAGE_OUTLINE_SECONDS);
        }

        private static string _cachedPromptText;
        private static string _cachedPromptPath;
        private static bool _cachedPromptSpeaking;

        private static void UpdateCommandPrompt(PlayerControllerB player)
        {
            // F-FOREMAN-B-18: also clear in orbit. The prompt used to sit on the HUD all game,
            // matching the Worklight prompt's gating (WorklightBeaconPatch.UpdatePrompt).
            if (!CommandNetUpgrade.HasVirtualWalkie()
                || player == null
                || player.isPlayerDead
                || player.isTypingChat
                || player.inTerminalMenu
                || (StartOfRound.Instance != null && StartOfRound.Instance.inShipPhase)
                || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen))
            {
                Y4ngzPromptOverlay.ClearPostPlayerMenuPrompt(PROMPT_KEY);
                return;
            }

            string bindingPath = Gui.UpgradeInput.EffectivePath(Gui.Plugin.Keybinds?.CommandNetTransmit);
            if (_cachedPromptText == null
                || _cachedPromptSpeaking != _localSpeaking
                || !string.Equals(_cachedPromptPath, bindingPath, StringComparison.Ordinal))
            {
                _cachedPromptPath = bindingPath;
                _cachedPromptSpeaking = _localSpeaking;
                string label = Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.CommandNetTransmit, "J");
                // F-FOREMAN-B-18: nothing on screen told the player the key was actually keying
                // the channel.
                _cachedPromptText = _localSpeaking
                    ? $"Walkie-talkie: [{label}] TRANSMITTING"
                    : $"Walkie-talkie: [{label}]";
            }

            Y4ngzPromptOverlay.SetPostPlayerMenuPrompt(PROMPT_KEY, _cachedPromptText);
        }

        private static void UpdateMinimap(PlayerControllerB player)
        {
            if (!CommandNetUpgrade.HasMinimap() || player == null || player.isPlayerDead || !IsLandedOnPlanet())
            {
                _hangarStateKnown = false;
                DestroyMinimap();
                return;
            }

            bool inHangarShipRoom = player.isInHangarShipRoom;
            bool exitedShip = !_hangarStateKnown
                ? !inHangarShipRoom
                : _wasInHangarShipRoom && !inHangarShipRoom;
            _hangarStateKnown = true;
            _wasInHangarShipRoom = inHangarShipRoom;

            if (inHangarShipRoom || !_presentationVisible)
            {
                SetMinimapPresentationActive(false);
                return;
            }

            bool existedBeforeExit = _minimapRoot != null;
            EnsureMinimap();
            if (_minimapRoot == null)
                return;

            SetMinimapPresentationActive(true);
            if (existedBeforeExit && exitedShip)
                RestartMinimapBootAnimation();

            ApplyChatLayoutForMinimap();
            UpdateMinimapBootAnimation();

            if (Time.unscaledTime < _nextMinimapRefresh)
                return;
            _nextMinimapRefresh = Time.unscaledTime + MINIMAP_REFRESH_INTERVAL;

            bool rendered = RenderMinimap(player);
            if (!rendered)
            {
                if (_radarImage != null)
                    _radarImage.enabled = false;
                HideAllMinimapMarkers();
                return;
            }

            if (_radarImage != null)
                _radarImage.enabled = true;

            UpdateMinimapMarkers(player);
        }

        private static bool IsLandedOnPlanet()
        {
            StartOfRound round = StartOfRound.Instance;
            return round != null && !round.inShipPhase && round.shipDoorsEnabled;
        }

        private static void SetMinimapPresentationActive(bool active)
        {
            if (_minimapRoot == null)
            {
                if (!active)
                    RestoreChatLayout();
                return;
            }

            if (_minimapRoot.activeSelf != active)
                _minimapRoot.SetActive(active);

            if (active)
            {
                ApplyMinimapLayout();
                return;
            }

            RestoreChatLayout();
        }

        private static void RestartMinimapBootAnimation()
        {
            if (_minimapRect == null || _minimapCanvasGroup == null)
                return;

            _minimapBootStartTime = Time.unscaledTime;
            _minimapBootActive = true;
            _minimapRect.localScale = new Vector3(1f, 0.02f, 1f);
            _minimapCanvasGroup.alpha = 0f;
            SetBootLineVisible(false, 0f);
        }

        private static void ApplyMinimapLayout()
        {
            if (_minimapRect == null)
                return;

            Canvas canvas = _minimapRect.GetComponentInParent<Canvas>();
            float scaleFactor = canvas != null && canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f;
            Rect safeArea = Screen.safeArea;
            float safeLeft = safeArea.xMin / scaleFactor;
            float safeBottom = safeArea.yMin / scaleFactor;
            _minimapRect.anchoredPosition = new Vector2(
                MINIMAP_MARGIN + safeLeft + _hudMotionOffset.x,
                MINIMAP_MARGIN + MINIMAP_SIZE * 0.5f + safeBottom + _hudMotionOffset.y);
        }

        private static void UpdateMinimapBootAnimation()
        {
            if (_minimapRect == null || _minimapCanvasGroup == null)
                return;

            if (!_minimapBootActive)
            {
                _minimapRect.localScale = Vector3.one;
                _minimapCanvasGroup.alpha = 1f;
                if (_bootLine != null)
                    _bootLine.gameObject.SetActive(false);
                return;
            }

            float elapsed = Time.unscaledTime - _minimapBootStartTime;
            float lineT = Mathf.Clamp01(elapsed / MINIMAP_BOOT_LINE_PHASE);
            float expandT = Mathf.Clamp01((elapsed - MINIMAP_BOOT_LINE_PHASE) / (MINIMAP_BOOT_DURATION - MINIMAP_BOOT_LINE_PHASE));
            float smoothLine = Mathf.SmoothStep(0f, 1f, lineT);
            float smoothExpand = Mathf.SmoothStep(0f, 1f, expandT);

            if (elapsed < MINIMAP_BOOT_LINE_PHASE)
            {
                _minimapRect.localScale = new Vector3(1f, Mathf.Lerp(0.012f, 0.035f, smoothLine), 1f);
                _minimapCanvasGroup.alpha = Mathf.Lerp(0.2f, 1f, smoothLine);
                SetBootLineVisible(true, Mathf.Lerp(0.35f, 0.96f, smoothLine));
                return;
            }

            _minimapRect.localScale = new Vector3(1f, Mathf.Lerp(0.05f, 1f, smoothExpand), 1f);
            _minimapCanvasGroup.alpha = 1f;
            SetBootLineVisible(expandT < 0.78f, Mathf.Lerp(0.9f, 0f, expandT));

            if (elapsed >= MINIMAP_BOOT_DURATION)
            {
                _minimapBootActive = false;
                _minimapRect.localScale = Vector3.one;
                _minimapCanvasGroup.alpha = 1f;
                SetBootLineVisible(false, 0f);
            }
        }

        private static void SetBootLineVisible(bool visible, float alpha)
        {
            if (_bootLine == null)
                return;

            _bootLine.gameObject.SetActive(visible);
            _bootLine.color = GetMinimapAccentColor(alpha);
        }

        private static void ApplyChatLayoutForMinimap()
        {
            RectTransform chatRect = ResolveChatRect();
            if (chatRect == null)
                return;

            if (_chatRect != chatRect)
            {
                RestoreChatLayout();
                _chatRect = chatRect;
                _chatOriginalAnchoredPosition = chatRect.anchoredPosition;
                _chatLayoutCaptured = true;
            }

            float bottomMargin = _minimapRect != null
                ? _minimapRect.anchoredPosition.y - MINIMAP_SIZE * 0.5f
                : MINIMAP_MARGIN;
            float verticalOffset = MINIMAP_SIZE + bottomMargin + MINIMAP_CHAT_GAP;
            chatRect.anchoredPosition = _chatOriginalAnchoredPosition + new Vector2(0f, verticalOffset);
        }

        /// <summary>
        /// F-FOREMAN-B-6: ApplyChatLayoutForMinimap is not throttled - it runs every frame - so the
        /// GetComponent is cached and only re-paid when the HUDManager instance itself changes.
        /// </summary>
        private static RectTransform ResolveChatRect()
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null)
            {
                _resolvedChatHud = null;
                _resolvedChatRect = null;
                return null;
            }

            if (ReferenceEquals(_resolvedChatHud, hud) && _resolvedChatRect != null)
                return _resolvedChatRect;

            if (hud.Chat == null || hud.Chat.canvasGroup == null)
                return null;

            _resolvedChatHud = hud;
            _resolvedChatRect = hud.Chat.canvasGroup.GetComponent<RectTransform>();
            return _resolvedChatRect;
        }

        private static HUDManager _resolvedChatHud;
        private static RectTransform _resolvedChatRect;

        private static void RestoreChatLayout()
        {
            if (_chatLayoutCaptured && _chatRect != null)
                _chatRect.anchoredPosition = _chatOriginalAnchoredPosition;

            _chatRect = null;
            _chatOriginalAnchoredPosition = Vector2.zero;
            _chatLayoutCaptured = false;
        }

        private static void EnsureMinimap()
        {
            if (_minimapRoot != null)
                return;
            if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null)
                return;

            Canvas canvas = HUDManager.Instance.playerScreenTexture.canvas;
            if (canvas == null)
                return;

            _minimapRoot = new GameObject("Y4NGZ_CommandNetMinimap");
            _minimapRoot.transform.SetParent(canvas.transform, worldPositionStays: false);
            _minimapRoot.transform.SetAsLastSibling();
            _minimapCanvasGroup = _minimapRoot.AddComponent<CanvasGroup>();
            _minimapCanvasGroup.alpha = 0f;
            _minimapCanvasGroup.interactable = false;
            _minimapCanvasGroup.blocksRaycasts = false;
            _minimapRect = _minimapRoot.AddComponent<RectTransform>();
            _minimapRect.anchorMin = Vector2.zero;
            _minimapRect.anchorMax = Vector2.zero;
            _minimapRect.pivot = new Vector2(0f, 0.5f);
            _minimapRect.sizeDelta = new Vector2(MINIMAP_SIZE, MINIMAP_SIZE);
            _minimapRect.localScale = new Vector3(1f, 0.02f, 1f);

            Image bg = _minimapRoot.AddComponent<Image>();
            bg.sprite = GetPixelSprite();
            bg.color = GetMinimapPanelColor(0.76f);
            bg.raycastTarget = false;

            GameObject imageGo = new GameObject("VanillaRadarTexture");
            imageGo.transform.SetParent(_minimapRoot.transform, worldPositionStays: false);
            RectTransform imageRect = imageGo.AddComponent<RectTransform>();
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.offsetMin = new Vector2(MINIMAP_INSET, MINIMAP_INSET);
            imageRect.offsetMax = new Vector2(-MINIMAP_INSET, -MINIMAP_INSET);
            _radarImage = imageGo.AddComponent<RawImage>();
            _radarImage.color = new Color(0.86f, 0.95f, 0.78f, 0.90f);
            _radarImage.raycastTarget = false;
            _radarImage.uvRect = new Rect(0f, 0f, 1f, 1f);

            GameObject tacticalMarkerGo = new GameObject("TacticalMarkers");
            tacticalMarkerGo.transform.SetParent(_minimapRoot.transform, worldPositionStays: false);
            _tacticalMarkerLayer = tacticalMarkerGo.AddComponent<RectTransform>();
            _tacticalMarkerLayer.anchorMin = Vector2.zero;
            _tacticalMarkerLayer.anchorMax = Vector2.one;
            _tacticalMarkerLayer.offsetMin = imageRect.offsetMin;
            _tacticalMarkerLayer.offsetMax = imageRect.offsetMax;

            GameObject markerGo = new GameObject("NavigationMarkers");
            markerGo.transform.SetParent(_minimapRoot.transform, worldPositionStays: false);
            _markerLayer = markerGo.AddComponent<RectTransform>();
            _markerLayer.anchorMin = Vector2.zero;
            _markerLayer.anchorMax = Vector2.one;
            _markerLayer.offsetMin = imageRect.offsetMin;
            _markerLayer.offsetMax = imageRect.offsetMax;

            Color accent = GetMinimapAccentColor(MINIMAP_BORDER_ALPHA);
            Color grid = GetMinimapAccentColor(MINIMAP_GRID_ALPHA);
            CreateCrtScanlines(_minimapRoot.transform);
            CreateCrtVignette(_minimapRoot.transform);
            CreateLine("GridV1", _minimapRoot.transform, new Vector2(0.333f, 0f), new Vector2(0.333f, 1f), new Vector2(-0.5f, MINIMAP_INSET + 2f), new Vector2(0.5f, -MINIMAP_INSET - 2f), grid, registerAccent: false);
            CreateLine("GridV2", _minimapRoot.transform, new Vector2(0.666f, 0f), new Vector2(0.666f, 1f), new Vector2(-0.5f, MINIMAP_INSET + 2f), new Vector2(0.5f, -MINIMAP_INSET - 2f), grid, registerAccent: false);
            CreateLine("GridH1", _minimapRoot.transform, new Vector2(0f, 0.333f), new Vector2(1f, 0.333f), new Vector2(MINIMAP_INSET + 2f, -0.5f), new Vector2(-MINIMAP_INSET - 2f, 0.5f), grid, registerAccent: false);
            CreateLine("GridH2", _minimapRoot.transform, new Vector2(0f, 0.666f), new Vector2(1f, 0.666f), new Vector2(MINIMAP_INSET + 2f, -0.5f), new Vector2(-MINIMAP_INSET - 2f, 0.5f), grid, registerAccent: false);

            CreateLine("Top", _minimapRoot.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -MINIMAP_BORDER_THICKNESS), new Vector2(0f, 0f), accent, registerAccent: true);
            CreateLine("Bottom", _minimapRoot.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, MINIMAP_BORDER_THICKNESS), accent, registerAccent: true);
            CreateLine("Left", _minimapRoot.transform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(MINIMAP_BORDER_THICKNESS, 0f), accent, registerAccent: true);
            CreateLine("Right", _minimapRoot.transform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-MINIMAP_BORDER_THICKNESS, 0f), new Vector2(0f, 0f), accent, registerAccent: true);
            CreateBootLine(_minimapRoot.transform, accent);

            EnsureMinimapTexture();
            ApplyMinimapLayout();
            RefreshMinimapStyle();
            RestartMinimapBootAnimation();
        }

        private static void CreateLine(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            Color color,
            bool registerAccent)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            Image img = go.AddComponent<Image>();
            img.sprite = GetPixelSprite();
            img.color = color;
            img.raycastTarget = false;

            if (registerAccent)
                MinimapAccentImages.Add(img);
            else
                MinimapGridImages.Add(img);
        }

        private static Color GetMinimapAccentColor(float alpha)
        {
            Color color = UiTheme.Accent;
            color.a = alpha;
            return color;
        }

        private static Color GetMinimapPanelColor(float alpha)
        {
            Color color = UiTheme.Panel;
            color.a = alpha;
            return color;
        }

        private static void CreateCrtScanlines(Transform parent)
        {
            for (int i = 1; i < MINIMAP_SCANLINE_COUNT; i++)
            {
                float y = i / (float)MINIMAP_SCANLINE_COUNT;
                GameObject go = new GameObject($"Scanline{i}");
                go.transform.SetParent(parent, worldPositionStays: false);
                RectTransform rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, y);
                rt.anchorMax = new Vector2(1f, y);
                rt.offsetMin = new Vector2(MINIMAP_INSET + 1f, -0.4f);
                rt.offsetMax = new Vector2(-MINIMAP_INSET - 1f, 0.4f);

                Image img = go.AddComponent<Image>();
                img.sprite = GetPixelSprite();
                img.color = new Color(0f, 0f, 0f, 0.20f);
                img.raycastTarget = false;
                MinimapScanlineImages.Add(img);
            }
        }

        private static void CreateCrtVignette(Transform parent)
        {
            GameObject go = new GameObject("CrtVignette");
            go.transform.SetParent(parent, worldPositionStays: false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(MINIMAP_INSET, MINIMAP_INSET);
            rt.offsetMax = new Vector2(-MINIMAP_INSET, -MINIMAP_INSET);

            Image img = go.AddComponent<Image>();
            img.sprite = GetVignetteSprite();
            img.color = new Color(1f, 1f, 1f, 0.90f);
            img.raycastTarget = false;
        }

        private static void CreateBootLine(Transform parent, Color accent)
        {
            GameObject go = new GameObject("BootLine");
            go.transform.SetParent(parent, worldPositionStays: false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.offsetMin = new Vector2(MINIMAP_INSET + 1f, -0.8f);
            rt.offsetMax = new Vector2(-MINIMAP_INSET - 1f, 0.8f);

            _bootLine = go.AddComponent<Image>();
            _bootLine.sprite = GetPixelSprite();
            _bootLine.color = accent;
            _bootLine.raycastTarget = false;
            _bootLine.gameObject.SetActive(false);
        }

        private static bool RenderMinimap(PlayerControllerB player)
        {
            ManualCameraRenderer mapScreen = StartOfRound.Instance != null
                ? StartOfRound.Instance.mapScreen
                : null;
            Camera sourceCamera = mapScreen != null ? mapScreen.mapCamera : null;
            if (player == null || sourceCamera == null)
                return false;

            EnsureMinimapTexture();
            EnsureMinimapCamera(sourceCamera);
            if (_minimapCamera == null || _minimapTexture == null)
                return false;

            Vector3 focus = ResolveMinimapFocus(player);

            // F-FOREMAN-B-6: everything that mutates vanilla state happens inside the try. Hiding
            // the vanilla map UI before it meant an exception in the camera or contour setup left
            // the real map screen blanked until the next successful render.
            GameObject contourMap = null;
            bool hidVanillaUi = false;
            bool restoreContour = false;
            bool contourWasActive = false;
            Vector3 contourPosition = Vector3.zero;
            try
            {
                ConfigureMinimapCamera(player, mapScreen, sourceCamera, focus.x, focus.y, focus.z);

                _vanillaMapUiState = HideVanillaMapUi(mapScreen);
                hidVanillaUi = true;

                contourMap = ResolveContourMap(mapScreen);
                if (contourMap != null)
                {
                    restoreContour = true;
                    contourWasActive = contourMap.activeSelf;
                    contourPosition = contourMap.transform.position;
                    bool showOutsideMap = !player.isInsideFactory;
                    contourMap.SetActive(showOutsideMap);
                    if (showOutsideMap)
                    {
                        Vector3 pos = contourMap.transform.position;
                        contourMap.transform.position = new Vector3(pos.x, focus.y - 1.5f, pos.z);
                    }
                }

                _minimapCamera.Render();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"[Command Net] Minimap render skipped: {ex.Message}");
                return false;
            }
            finally
            {
                if (restoreContour && contourMap != null)
                {
                    contourMap.SetActive(contourWasActive);
                    contourMap.transform.position = contourPosition;
                }
                if (hidVanillaUi)
                    RestoreVanillaMapUi(mapScreen);
            }

            return true;
        }

        private static VanillaMapUiState HideVanillaMapUi(ManualCameraRenderer mapScreen)
        {
            VanillaMapUiState state = new VanillaMapUiState();
            StartOfRound round = StartOfRound.Instance;

            if (round != null)
            {
                if (round.mapScreenPlayerName != null)
                {
                    state.MapScreenPlayerNameEnabled = round.mapScreenPlayerName.enabled;
                    round.mapScreenPlayerName.enabled = false;
                }

                if (round.mapScreenPlayerNameBG != null)
                {
                    state.MapScreenPlayerNameBgEnabled = round.mapScreenPlayerNameBG.enabled;
                    round.mapScreenPlayerNameBG.enabled = false;
                }
            }

            if (mapScreen == null)
                return state;

            if (mapScreen.headMountedCamUI != null)
            {
                state.HeadMountedCamUiEnabled = mapScreen.headMountedCamUI.enabled;
                mapScreen.headMountedCamUI.enabled = false;
            }

            if (mapScreen.localPlayerPlaceholder != null)
            {
                state.LocalPlayerPlaceholderEnabled = mapScreen.localPlayerPlaceholder.enabled;
                mapScreen.localPlayerPlaceholder.enabled = false;
            }

            if (mapScreen.compassRose != null)
            {
                state.CompassRoseEnabled = mapScreen.compassRose.enabled;
                mapScreen.compassRose.enabled = false;
            }

            if (mapScreen.shipArrowUI != null)
            {
                state.ShipArrowUiActive = mapScreen.shipArrowUI.activeSelf;
                mapScreen.shipArrowUI.SetActive(false);
            }

            if (mapScreen.shipIcon != null)
            {
                state.ShipIconActive = mapScreen.shipIcon.activeSelf;
                mapScreen.shipIcon.SetActive(false);
            }

            if (mapScreen.LostSignalUI != null)
            {
                state.LostSignalUiActive = mapScreen.LostSignalUI.activeSelf;
                mapScreen.LostSignalUI.SetActive(false);
            }

            if (mapScreen.lineFromRadarTargetToExit != null)
            {
                state.ExitLineEnabled = mapScreen.lineFromRadarTargetToExit.enabled;
                mapScreen.lineFromRadarTargetToExit.enabled = false;
            }

            return state;
        }

        private static void RestoreVanillaMapUi(ManualCameraRenderer mapScreen)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.mapScreenPlayerName != null)
                    round.mapScreenPlayerName.enabled = _vanillaMapUiState.MapScreenPlayerNameEnabled;
                if (round.mapScreenPlayerNameBG != null)
                    round.mapScreenPlayerNameBG.enabled = _vanillaMapUiState.MapScreenPlayerNameBgEnabled;
            }

            if (mapScreen == null)
                return;

            if (mapScreen.headMountedCamUI != null)
                mapScreen.headMountedCamUI.enabled = _vanillaMapUiState.HeadMountedCamUiEnabled;
            if (mapScreen.localPlayerPlaceholder != null)
                mapScreen.localPlayerPlaceholder.enabled = _vanillaMapUiState.LocalPlayerPlaceholderEnabled;
            if (mapScreen.compassRose != null)
                mapScreen.compassRose.enabled = _vanillaMapUiState.CompassRoseEnabled;
            if (mapScreen.shipArrowUI != null)
                mapScreen.shipArrowUI.SetActive(_vanillaMapUiState.ShipArrowUiActive);
            if (mapScreen.shipIcon != null)
                mapScreen.shipIcon.SetActive(_vanillaMapUiState.ShipIconActive);
            if (mapScreen.LostSignalUI != null)
                mapScreen.LostSignalUI.SetActive(_vanillaMapUiState.LostSignalUiActive);
            if (mapScreen.lineFromRadarTargetToExit != null)
                mapScreen.lineFromRadarTargetToExit.enabled = _vanillaMapUiState.ExitLineEnabled;
        }

        private static void EnsureMinimapTexture()
        {
            if (_minimapTexture != null && _minimapTexture.IsCreated())
            {
                if (_radarImage != null && _radarImage.texture != _minimapTexture)
                    _radarImage.texture = _minimapTexture;
                return;
            }

            if (_minimapTexture != null)
                _minimapTexture.Release();

            _minimapTexture = new RenderTexture(MINIMAP_TEXTURE_SIZE, MINIMAP_TEXTURE_SIZE, 16, RenderTextureFormat.ARGB32)
            {
                name = "Y4NGZ_CommandNetMinimapTexture",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            _minimapTexture.Create();

            if (_radarImage != null)
                _radarImage.texture = _minimapTexture;
        }

        private static void EnsureMinimapCamera(Camera sourceCamera)
        {
            if (sourceCamera == null)
                return;

            if (_minimapCamera == null)
            {
                GameObject cameraGo = new GameObject("Y4NGZ_CommandNetMinimapCamera")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                _minimapCamera = cameraGo.AddComponent<Camera>();
            }

            if (_minimapSourceCamera != sourceCamera)
            {
                _minimapCamera.CopyFrom(sourceCamera);
                _minimapSourceCamera = sourceCamera;
            }

            _minimapCamera.enabled = false;
            _minimapCamera.targetTexture = _minimapTexture;
            _minimapCamera.rect = new Rect(0f, 0f, 1f, 1f);
            _minimapCamera.aspect = 1f;
        }

        private static void ConfigureMinimapCamera(
            PlayerControllerB player,
            ManualCameraRenderer mapScreen,
            Camera sourceCamera,
            float focusX,
            float focusY,
            float focusZ)
        {
            _minimapCamera.transform.SetPositionAndRotation(
                new Vector3(focusX, focusY + MINIMAP_CAMERA_HEIGHT, focusZ),
                sourceCamera.transform.rotation);

            _minimapCamera.cullingMask = sourceCamera.cullingMask;
            _minimapCamera.clearFlags = sourceCamera.clearFlags;
            _minimapCamera.backgroundColor = sourceCamera.backgroundColor;
            _minimapCamera.orthographic = sourceCamera.orthographic;
            _minimapCamera.orthographicSize = sourceCamera.orthographicSize;
            _minimapCamera.fieldOfView = sourceCamera.fieldOfView;

            if (player.isInHangarShipRoom)
            {
                _minimapCamera.nearClipPlane = -0.96f;
                _minimapCamera.farClipPlane = 7.52f;
            }
            else if (!player.isInsideFactory)
            {
                float near = mapScreen != null ? mapScreen.cameraNearPlane : sourceCamera.nearClipPlane;
                float far = mapScreen != null ? mapScreen.cameraFarPlane : sourceCamera.farClipPlane;
                _minimapCamera.nearClipPlane = near - 18f;
                _minimapCamera.farClipPlane = far + 18f;
            }
            else
            {
                _minimapCamera.nearClipPlane = mapScreen != null ? mapScreen.cameraNearPlane : sourceCamera.nearClipPlane;
                _minimapCamera.farClipPlane = mapScreen != null ? mapScreen.cameraFarPlane : sourceCamera.farClipPlane;
            }
        }

        private static Vector3 ResolveMinimapFocus(PlayerControllerB player)
        {
            Vector3 focus = player.transform.position;
            if (Physics.Raycast(
                    player.transform.position + Vector3.up * 0.1f,
                    Vector3.down,
                    out RaycastHit hit,
                    5f,
                    StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMask : ~0,
                    QueryTriggerInteraction.Ignore))
            {
                focus = hit.point + Vector3.up * 0.06f;
            }

            return focus;
        }

        private static GameObject ResolveContourMap(ManualCameraRenderer mapScreen)
        {
            if (mapScreen == null)
                return null;
            if (mapScreen.contourMap != null)
                return mapScreen.contourMap;

            GameObject contour = null;
            try
            {
                contour = GameObject.FindGameObjectWithTag("TerrainContourMap");
            }
            catch
            {
                return null;
            }

            if (contour != null)
                mapScreen.contourMap = contour;
            return contour;
        }

        private static void RefreshMinimapStyle()
        {
            Color accent = GetMinimapAccentColor(MINIMAP_BORDER_ALPHA);
            for (int i = 0; i < MinimapAccentImages.Count; i++)
            {
                if (MinimapAccentImages[i] != null)
                    MinimapAccentImages[i].color = accent;
            }

            Color grid = GetMinimapAccentColor(MINIMAP_GRID_ALPHA);
            for (int i = 0; i < MinimapGridImages.Count; i++)
            {
                if (MinimapGridImages[i] != null)
                    MinimapGridImages[i].color = grid;
            }

            for (int i = 0; i < MinimapScanlineImages.Count; i++)
            {
                if (MinimapScanlineImages[i] != null)
                    MinimapScanlineImages[i].color = new Color(0f, 0f, 0f, 0.20f);
            }

            if (_bootLine != null && !_minimapBootActive)
                _bootLine.color = accent;
        }

        private static void UpdateMinimapMarkers(PlayerControllerB player)
        {
            if (_tacticalMarkerLayer == null || _markerLayer == null || _minimapCamera == null || player == null)
                return;

            int tacticalUsed = 0;
            if (OptionalPluginCapabilities.LethalCctv)
            {
                try
                {
                    AddCameraMarkers(ref tacticalUsed);
                }
                catch (Exception ex)
                {
                    if (!_cameraMarkerFailureLogged)
                    {
                        _cameraMarkerFailureLogged = true;
                        Plugin.Log?.LogWarning($"[Command Net] CCTV marker integration unavailable: {ex.Message}");
                    }
                }
            }

            AddObjectiveMarkers(ref tacticalUsed);
            AddEnemyMarkers(ref tacticalUsed, player);
            for (int i = tacticalUsed; i < MinimapTacticalDots.Count; i++)
            {
                MinimapTacticalDot dot = MinimapTacticalDots[i];
                if (dot?.Rect != null)
                    dot.Rect.gameObject.SetActive(false);
            }

            int used = 0;
            Color accent = GetMinimapAccentColor(0.92f);
            AddMinimapMarker(ref used, player.transform.position, accent, 7f, player.transform.eulerAngles.y);

            StartOfRound round = StartOfRound.Instance;
            if (round != null && round.allPlayerScripts != null)
            {
                for (int i = 0; i < round.allPlayerScripts.Length; i++)
                {
                    PlayerControllerB teammate = round.allPlayerScripts[i];
                    if (teammate == null
                        || teammate == player
                        || teammate.isPlayerDead
                        || !teammate.isPlayerControlled
                        || teammate.isInsideFactory != player.isInsideFactory)
                    {
                        continue;
                    }

                    AddMinimapMarker(
                        ref used,
                        teammate.transform.position,
                        new Color(0.96f, 0.86f, 0.62f, 0.92f),
                        5.5f,
                        float.NaN);
                }
            }

            AddEntranceMarkers(ref used, player.isInsideFactory, accent.r, accent.g, accent.b);

            if (!player.isInsideFactory && round != null && round.elevatorTransform != null)
            {
                AddMinimapMarker(
                    ref used,
                    round.elevatorTransform.position,
                    new Color(0.62f, 0.88f, 1f, 0.78f),
                    6f,
                    float.NaN);
            }

            for (int i = used; i < MinimapMarkers.Count; i++)
            {
                if (MinimapMarkers[i] != null)
                    MinimapMarkers[i].gameObject.SetActive(false);
            }
        }

        // F-FOREMAN-B-6: reused buffers. These used to allocate a fresh List on every refresh.
        private static readonly List<Transform> CameraMarkerBuffer = new List<Transform>();
        private static readonly List<Vector3> ObjectiveMarkerBuffer = new List<Vector3>();

        private static void AddCameraMarkers(ref int used)
        {
            List<Transform> cameras = CameraMarkerBuffer;
            cameras.Clear();
            OptionalCctvBridge.CollectCameraTransforms(cameras);

            Color color = UiTheme.Accent;
            color.a = 0.98f;
            for (int i = 0; i < cameras.Count; i++)
            {
                Transform camera = cameras[i];
                if (camera == null)
                    continue;

                AddMinimapTacticalDot(ref used, camera.position, color);
            }
        }

        private static void AddObjectiveMarkers(ref int used)
        {
            List<Vector3> objectives = ObjectiveMarkerBuffer;
            objectives.Clear();
            OptionalCctvBridge.CollectActiveObjectivePositions(objectives);

            Color color = UiTheme.Warning;
            color.a = 0.98f;
            for (int i = 0; i < objectives.Count; i++)
            {
                AddMinimapTacticalDot(ref used, objectives[i], color);
            }
        }

        private static void AddEnemyMarkers(ref int used, PlayerControllerB player)
        {
            List<EnemyAI> enemies = RoundManager.Instance != null
                ? RoundManager.Instance.SpawnedEnemies
                : null;
            if (enemies == null || player == null)
                return;

            bool playerIsOutside = !player.isInsideFactory;
            Color color = UiTheme.Danger;
            color.a = 0.98f;
            // F-FOREMAN-B-17 / F-FOREMAN-B-12: the declared 80 m range is now the enemy-marker
            // limit. Frustum culling alone made the widget perfect ESP over a large fraction of
            // the interior, which is a lot for one level of a tier-2 upgrade.
            Vector3 origin = player.transform.position;
            float rangeSqr = CommandNetUpgrade.MINIMAP_RANGE_METERS * CommandNetUpgrade.MINIMAP_RANGE_METERS;
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (enemy == null || enemy.isEnemyDead || enemy.isOutside != playerIsOutside)
                    continue;
                if ((enemy.transform.position - origin).sqrMagnitude > rangeSqr)
                    continue;

                AddMinimapTacticalDot(ref used, enemy.transform.position, color);
            }
        }

        private static void AddEntranceMarkers(ref int used, bool playerIsInside, float accentR, float accentG, float accentB)
        {
            if (!_entrancesCached)
            {
                _cachedEntrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
                _entrancesCached = true;
            }

            bool wantEntranceToBuilding = !playerIsInside;
            for (int i = 0; i < _cachedEntrances.Length; i++)
            {
                EntranceTeleport entrance = _cachedEntrances[i];
                if (entrance == null || entrance.isEntranceToBuilding != wantEntranceToBuilding)
                    continue;

                bool mainEntrance = entrance.entranceId == 0;
                Color color = mainEntrance
                    ? new Color(accentR, accentG, accentB, 0.86f)
                    : new Color(1f, 0.68f, 0.34f, 0.78f);
                AddMinimapMarker(ref used, ResolveEntrancePosition(entrance), color, mainEntrance ? 6f : 4.5f, float.NaN);
            }
        }

        private static Vector3 ResolveEntrancePosition(EntranceTeleport entrance)
        {
            if (entrance == null)
                return Vector3.zero;
            return entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
        }

        private static void AddMinimapMarker(ref int used, Vector3 worldPosition, Color color, float size, float headingDegrees)
        {
            if (!TryWorldToMinimapPosition(worldPosition + Vector3.up * 0.25f, out Vector2 anchoredPosition))
                return;

            Image marker = GetMinimapMarker(used);
            used++;

            RectTransform rect = marker.GetComponent<RectTransform>();
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(size, size);
            rect.localEulerAngles = float.IsNaN(headingDegrees)
                ? new Vector3(0f, 0f, 45f)
                : new Vector3(0f, 0f, -headingDegrees);

            marker.color = color;
            marker.gameObject.SetActive(true);
        }

        private static void AddMinimapTacticalDot(ref int used, Vector3 worldPosition, Color color)
        {
            if (!TryWorldToMinimapPosition(worldPosition + Vector3.up * 0.25f, out Vector2 anchoredPosition))
                return;

            MinimapTacticalDot dot = GetMinimapTacticalDot(used);
            used++;
            dot.Rect.anchoredPosition = anchoredPosition;
            dot.Rect.sizeDelta = new Vector2(MINIMAP_TACTICAL_DOT_SIZE, MINIMAP_TACTICAL_DOT_SIZE);
            dot.Rect.localEulerAngles = Vector3.zero;
            dot.Fill.color = color;
            dot.Rect.gameObject.SetActive(true);
        }

        private static bool TryWorldToMinimapPosition(Vector3 worldPosition, out Vector2 anchoredPosition)
        {
            anchoredPosition = Vector2.zero;
            if (_minimapCamera == null || _markerLayer == null)
                return false;

            Vector3 viewport = _minimapCamera.WorldToViewportPoint(worldPosition);
            float nearDepth = Mathf.Min(_minimapCamera.nearClipPlane, _minimapCamera.farClipPlane);
            float farDepth = Mathf.Max(_minimapCamera.nearClipPlane, _minimapCamera.farClipPlane);
            if (viewport.z < nearDepth
                || viewport.z > farDepth
                || viewport.x < 0f
                || viewport.x > 1f
                || viewport.y < 0f
                || viewport.y > 1f)
            {
                return false;
            }

            Rect rect = _markerLayer.rect;
            anchoredPosition = new Vector2(
                (viewport.x - 0.5f) * rect.width,
                (viewport.y - 0.5f) * rect.height);
            return true;
        }

        private static Image GetMinimapMarker(int index)
        {
            while (MinimapMarkers.Count <= index)
            {
                GameObject go = new GameObject("Marker");
                go.transform.SetParent(_markerLayer, worldPositionStays: false);
                RectTransform rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                Image img = go.AddComponent<Image>();
                img.sprite = GetPixelSprite();
                img.raycastTarget = false;
                go.SetActive(false);
                MinimapMarkers.Add(img);
            }

            return MinimapMarkers[index];
        }

        private static MinimapTacticalDot GetMinimapTacticalDot(int index)
        {
            while (MinimapTacticalDots.Count <= index)
            {
                GameObject go = new GameObject("TacticalDot");
                go.transform.SetParent(_tacticalMarkerLayer, worldPositionStays: false);
                RectTransform rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);

                Image halo = go.AddComponent<Image>();
                halo.sprite = GetCircleSprite();
                halo.color = new Color(0.01f, 0.015f, 0.02f, 0.94f);
                halo.raycastTarget = false;

                GameObject fillGo = new GameObject("Fill");
                fillGo.transform.SetParent(rt, worldPositionStays: false);
                RectTransform fillRect = fillGo.AddComponent<RectTransform>();
                fillRect.anchorMin = Vector2.zero;
                fillRect.anchorMax = Vector2.one;
                fillRect.offsetMin = new Vector2(MINIMAP_TACTICAL_DOT_INSET, MINIMAP_TACTICAL_DOT_INSET);
                fillRect.offsetMax = new Vector2(-MINIMAP_TACTICAL_DOT_INSET, -MINIMAP_TACTICAL_DOT_INSET);
                Image fill = fillGo.AddComponent<Image>();
                fill.sprite = GetCircleSprite();
                fill.raycastTarget = false;

                go.SetActive(false);
                MinimapTacticalDots.Add(new MinimapTacticalDot
                {
                    Rect = rt,
                    Fill = fill,
                });
            }

            return MinimapTacticalDots[index];
        }

        private static void HideAllMinimapMarkers()
        {
            for (int i = 0; i < MinimapTacticalDots.Count; i++)
            {
                MinimapTacticalDot dot = MinimapTacticalDots[i];
                if (dot?.Rect != null)
                    dot.Rect.gameObject.SetActive(false);
            }

            for (int i = 0; i < MinimapMarkers.Count; i++)
            {
                if (MinimapMarkers[i] != null)
                    MinimapMarkers[i].gameObject.SetActive(false);
            }
        }

        private static void DestroyMinimap()
        {
            RestoreChatLayout();

            if (_minimapRoot != null)
                UnityEngine.Object.Destroy(_minimapRoot);

            if (_minimapCamera != null)
                UnityEngine.Object.Destroy(_minimapCamera.gameObject);

            if (_minimapTexture != null)
            {
                _minimapTexture.Release();
                UnityEngine.Object.Destroy(_minimapTexture);
            }

            _minimapRoot = null;
            _minimapRect = null;
            _minimapCanvasGroup = null;
            _radarImage = null;
            _tacticalMarkerLayer = null;
            _markerLayer = null;
            _bootLine = null;
            _minimapCamera = null;
            _minimapSourceCamera = null;
            _minimapTexture = null;
            _minimapBootStartTime = 0f;
            _minimapBootActive = false;
            MinimapMarkers.Clear();
            MinimapTacticalDots.Clear();
            MinimapAccentImages.Clear();
            MinimapGridImages.Clear();
            MinimapScanlineImages.Clear();
        }

        private static Sprite GetPixelSprite()
        {
            if (_pixelSprite != null)
                return _pixelSprite;

            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false, true);
            _pixelSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
            _pixelSprite.hideFlags = HideFlags.HideAndDontSave;
            return _pixelSprite;
        }

        private static Sprite GetCircleSprite()
        {
            if (_circleSprite != null)
                return _circleSprite;

            const int size = 32;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = ((x + 0.5f) / size) * 2f - 1f;
                    float ny = ((y + 0.5f) / size) * 2f - 1f;
                    float distance = Mathf.Sqrt(nx * nx + ny * ny);
                    float alpha = 1f - Mathf.SmoothStep(0.78f, 1f, distance);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply(false, true);
            _circleSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            _circleSprite.hideFlags = HideFlags.HideAndDontSave;
            return _circleSprite;
        }

        private static Sprite GetVignetteSprite()
        {
            if (_vignetteSprite != null)
                return _vignetteSprite;

            const int size = 48;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = ((x + 0.5f) / size) * 2f - 1f;
                    float ny = ((y + 0.5f) / size) * 2f - 1f;
                    float dist = Mathf.Sqrt(nx * nx + ny * ny);
                    float alpha = Mathf.SmoothStep(0f, 0.42f, Mathf.InverseLerp(0.62f, 1.25f, dist));
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
                }
            }

            tex.Apply(false, true);
            _vignetteSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            _vignetteSprite.hideFlags = HideFlags.HideAndDontSave;
            return _vignetteSprite;
        }

        /// <summary>
        /// F-FOREMAN-B-14: a plain monotonic (clientId &lt;&lt; 32 | counter). The old id mixed the
        /// clock into the low bits, so two ids could collide and silently suppress a legitimate
        /// outline - or, worse, a stop-speaking walkie message, leaving a player's voice stuck on
        /// the radio channel for the rest of the round.
        /// </summary>
        private static long NextMessageId()
        {
            unchecked
            {
                ulong client = GameNetworkManager.Instance?.localPlayerController != null
                    ? GameNetworkManager.Instance.localPlayerController.actualClientId
                    : 0UL;
                _nextMessageCounter++;
                return (long)(((client & 0xFFFFFFFFUL) << 32) | _nextMessageCounter);
            }
        }

        /// <summary>
        /// Netcode nulls the CustomMessagingManager on shutdown and builds a fresh one for the next
        /// host/join, so this latch has to drop or the second lobby of a session never receives
        /// walkie or damage notices.
        /// </summary>
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _handlersRegistered = false;
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered)
                return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_WALKIE, OnReceiveWalkie);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DAMAGE_OUTLINE, OnReceiveDamageNotice);
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"CommandNetPatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

#pragma warning disable Harmony003
        private static void SendWalkieMessage(long messageId, int playerId, bool speaking)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient || network.CustomMessagingManager == null)
                return;
            // F-FOREMAN-B-14: no round, nobody to tell.
            if (StartOfRound.Instance == null)
                return;

            if (network.IsServer)
                SendWalkieMessage(messageId, playerId, speaking, null);
            else
                SendWalkieMessage(messageId, playerId, speaking, NetworkManager.ServerClientId);
        }

        private static void SendWalkieMessage(long messageId, int playerId, bool speaking, ulong? clientId)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(long) + sizeof(int) + sizeof(bool), Allocator.Temp);
            try
            {
                writer.WriteValueSafe(messageId);
                writer.WriteValueSafe(playerId);
                writer.WriteValueSafe(speaking);
                if (clientId.HasValue)
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                        MSG_WALKIE,
                        clientId.Value,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                else
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                        MSG_WALKIE,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveWalkie(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                long messageId;
                int playerId;
                bool speaking;
                reader.ReadValueSafe(out messageId);
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out speaking);

                bool isLocal = GameNetworkManager.Instance?.localPlayerController != null
                               && (int)GameNetworkManager.Instance.localPlayerController.playerClientId == playerId;
                ApplyWalkieMessage(messageId, playerId, speaking, localSender: isLocal);

                NetworkManager network = NetworkManager.Singleton;
                if (network != null
                    && network.IsServer
                    && senderClientId != network.LocalClientId)
                {
                    SendWalkieMessage(messageId, playerId, speaking, null);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"CommandNetPatch: malformed walkie message: {ex.Message}");
            }
        }

        private static void SendDamageNotice(long messageId, int playerId)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient || network.CustomMessagingManager == null)
                return;
            // F-FOREMAN-B-14: no round, nobody to tell.
            if (StartOfRound.Instance == null)
                return;

            if (network.IsServer)
                SendDamageNotice(messageId, playerId, null);
            else
                SendDamageNotice(messageId, playerId, NetworkManager.ServerClientId);
        }

        private static void SendDamageNotice(long messageId, int playerId, ulong? clientId)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(long) + sizeof(int), Allocator.Temp);
            try
            {
                writer.WriteValueSafe(messageId);
                writer.WriteValueSafe(playerId);
                if (clientId.HasValue)
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                        MSG_DAMAGE_OUTLINE,
                        clientId.Value,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
                else
                {
                    NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                        MSG_DAMAGE_OUTLINE,
                        writer,
                        NetworkDelivery.ReliableSequenced);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveDamageNotice(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                long messageId;
                int playerId;
                reader.ReadValueSafe(out messageId);
                reader.ReadValueSafe(out playerId);
                HandleDamageNotice(messageId, playerId);

                NetworkManager network = NetworkManager.Singleton;
                if (network != null
                    && network.IsServer
                    && senderClientId != network.LocalClientId)
                {
                    SendDamageNotice(messageId, playerId, null);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"CommandNetPatch: malformed damage notice: {ex.Message}");
            }
        }
#pragma warning restore Harmony003

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }
    }
}
