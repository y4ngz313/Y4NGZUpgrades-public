using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System;
using System.Reflection;
using Y4NGZUpgrades.UITheme;

namespace Y4NGZUpgrades.Interactive.Hud
{
    internal static class Y4ngzPromptOverlay
    {
        private static readonly Vector2 PromptAnchor = new Vector2(1f, 1f);
        private static readonly Vector2 PromptPivot = new Vector2(1f, 1f);

        private const int MaxPromptLines = 12;
        private const float PromptRight = 24f;

        // The authored resting position. Prompts live here whenever the space
        // above them is free; the shared stack only ever pushes them further
        // down, never up into the contract card's region.
        private const float PromptBaseTop = 224f;
        private const float PromptWidth = 460f;
        private const float PromptHeight = 26f;
        private const float PromptSpacing = 27f;
        private const float TickInterval = 0.1f;
        private const float InitialFadeDelay = 0.35f;
        private const float InitialFadeDuration = 1.65f;
        private const float InactivityDelay = 3f;
        private const float InactivityFadeDuration = 0.55f;
        private const float StackLayoutSmoothTime = 0.16f;
        private const float ExternalSourceInitialScanInterval = 0.75f;
        private const float ExternalSourceStableScanInterval = 5f;
        private const float FontFallbackScanInterval = 5f;
        private const string PoltergeistControllerTypeName = "Poltergeist.SpectatorCamController";

        private static GameObject _promptCanvasObject;
        private static RectTransform _promptRoot;
        private static CanvasGroup _promptCanvasGroup;
        private static TextMeshProUGUI[] _promptLines;
        private static readonly List<TextMeshProUGUI> _externalPromptSources = new List<TextMeshProUGUI>();
        private static readonly Dictionary<string, string> _persistentPromptLines = new Dictionary<string, string>();
        // Insertion-ordered. A producer may now emit several lines at once (the field tablet emits
        // one line per binding), so a block has to keep its authored order, and the whole set shares
        // the twelve-line budget with Poltergeist's spectator block, whose priority is order-sensitive.
        // Dictionary enumeration order was never guaranteed and is not good enough for either.
        private static readonly List<string> _postPlayerMenuPromptKeys = new List<string>();
        private static readonly Dictionary<string, string[]> _postPlayerMenuPromptBlocks =
            new Dictionary<string, string[]>();
        private static readonly string[] _cachedPromptLines = new string[MaxPromptLines];
        private static string _cursorTipOverride;
        private static TMP_FontAsset _fontCache;
        private static float _nextTickAt;
        private static float _nextExternalSourceScanAt;
        private static float _nextFontFallbackScanAt;
        private static int _lastPromptSignature = int.MinValue;
        private static int _lastSourceSignature = int.MinValue;
        private static int _lastActivitySignature = int.MinValue;
        private static bool _promptDirty = true;
        private static bool _activitySignatureInitialized;
        private static bool _wasDeadOrSpectating;
        private static int _externalSourceStableScans;
        private static bool _poltergeistControllerContractLookupAttempted;
        private static Type _poltergeistControllerType;
        private static FieldInfo _poltergeistControllerInstanceField;
        private static FieldInfo _poltergeistControlsTextField;
        private static TextMeshProUGUI _activePoltergeistPromptSource;
        private static HUDManager _lastStyledHud;
        private static TextMeshProUGUI[] _lastStyledControlTipLines;
        private static TextMeshProUGUI _lastStyledCursorTip;
        private static Color _lastStyledHudAccent;
        private static Color _lastStyledCursorAccent;
        private static int _fadeSceneHandle = -1;
        private static float _fadeStartedAt = -1f;
        private static float _lastActivityAt = -1f;
        private static int _heldItemInstanceId;
        private static GrabbableObject _heldWeapon;
        private static float _weaponPromptShownAt = -1f;
        private static bool _weaponPromptsSuppressed;
        private static bool _motionSubscribed;
        private static Vector2 _motionOffset;
        private static float _currentStackTop = PromptBaseTop;
        private static float _stackTopVelocity;
        private static bool _stackLayoutInitialized;

        internal static void Apply(HUDManager hud)
        {
            if (hud == null || hud.controlTipLines == null)
                return;

            ApplyHudSourceStyleIfNeeded(hud, force: true);

            bool visible = GameplayUiVisibility.IsVisible;
            SetPresentationVisible(visible);
            if (!visible)
                return;

            UpdateHeldItemState(ResolveLocalPlayer());
            _promptDirty = true;
        }

        internal static void Apply(PlayerControllerB player)
        {
            if (player == null || player.cursorTip == null)
                return;

            TextMeshProUGUI source = player.cursorTip;
            ApplyCursorSourceStyleIfNeeded(source, HUDManager.Instance, force: true);
            source.enabled = false;
            bool visible = GameplayUiVisibility.IsVisible;
            SetPresentationVisible(visible);
            if (!visible)
                return;

            bool itemChanged = UpdateHeldItemState(player);
            string cursorText = ResolveCursorText(source.text);
            if (ObservePromptActivity(HUDManager.Instance, cursorText) || itemChanged)
            {
                _promptDirty = true;
                RefreshPromptOverlay(cursorText);
            }
        }

        internal static void RefreshTheme()
        {
            _lastStyledHud = null;
            _lastStyledControlTipLines = null;
            _lastStyledCursorTip = null;
            _lastStyledHudAccent = default(Color);
            _lastStyledCursorAccent = default(Color);

            HUDManager hud = HUDManager.Instance;
            ApplyHudSourceStyleIfNeeded(hud, force: true);

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player != null && player.cursorTip != null)
                ApplyCursorSourceStyleIfNeeded(player.cursorTip, hud, force: true);

            if (_promptLines != null)
            {
                for (int i = 0; i < _promptLines.Length; i++)
                {
                    if (_promptLines[i] != null)
                        ApplyPromptLineStyleIfNeeded(_promptLines[i]);
                }
            }

            _promptDirty = true;
        }

        internal static void Tick()
        {
            bool visible = GameplayUiVisibility.IsVisible;
            SetPresentationVisible(visible);
            if (!visible)
                return;

            if (Time.unscaledTime >= _nextTickAt)
            {
                _nextTickAt = Time.unscaledTime + TickInterval;
                string cursorText = null;
                PlayerControllerB player = ResolveLocalPlayer();

                if (player != null && player.cursorTip != null)
                {
                    cursorText = ResolveCursorText(player.cursorTip.text);
                    ApplyCursorSourceStyleIfNeeded(player.cursorTip, HUDManager.Instance);
                    if (player.cursorTip.enabled)
                        player.cursorTip.enabled = false;
                }

                HUDManager hud = HUDManager.Instance;
                ApplyHudSourceStyleIfNeeded(hud);

                RefreshExternalPromptSources();
                UpdateHeldItemState(player);
                ObservePromptActivity(hud, cursorText);
                int sourceSignature = BuildSourceSignature(hud, cursorText);
                if (_promptDirty || sourceSignature != _lastSourceSignature)
                {
                    _lastSourceSignature = sourceSignature;
                    _promptDirty = false;
                    RefreshPromptOverlay(cursorText);
                }
            }

            UpdatePresentationFrame();
        }

        internal static void SetPresentationVisible(bool visible)
        {
            bool changed = _promptCanvasObject != null && _promptCanvasObject.activeSelf != visible;
            if (changed)
                _promptCanvasObject.SetActive(visible);

            if (visible && changed)
            {
                _promptDirty = true;
                _nextTickAt = 0f;
            }
        }

        private static void UpdatePresentationFrame()
        {
            bool visible = GameplayUiVisibility.IsVisible;
            SetPresentationVisible(visible);
            if (!visible || _promptCanvasObject == null)
                return;

            bool fadeReady = BeginInitialFadeForCurrentSceneIfNeeded();
            UpdateStackLayoutFrame();

            if (_promptCanvasGroup == null || !fadeReady)
                return;

            float entranceElapsed = Mathf.Max(0f, Time.unscaledTime - _fadeStartedAt - InitialFadeDelay);
            float entranceProgress = InitialFadeDuration <= 0f
                ? 1f
                : Mathf.Clamp01(entranceElapsed / InitialFadeDuration);
            float entranceAlpha = Mathf.SmoothStep(0f, 1f, entranceProgress);

            float inactiveFor = _lastActivityAt < 0f
                ? 0f
                : Mathf.Max(0f, Time.unscaledTime - _lastActivityAt);
            if (_heldWeapon != null
                && !_weaponPromptsSuppressed
                && _weaponPromptShownAt >= 0f
                && Time.unscaledTime - _weaponPromptShownAt >= InactivityDelay)
                _weaponPromptsSuppressed = true;

            float inactivityProgress = InactivityFadeDuration <= 0f
                ? (inactiveFor >= InactivityDelay ? 1f : 0f)
                : Mathf.Clamp01((inactiveFor - InactivityDelay) / InactivityFadeDuration);
            float inactivityAlpha = 1f - Mathf.SmoothStep(0f, 1f, inactivityProgress);
            _promptCanvasGroup.alpha = entranceAlpha * inactivityAlpha;
        }

        private static bool BeginInitialFadeForCurrentSceneIfNeeded()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || scene.name != "SampleSceneRelay")
                return false;
            if (_fadeSceneHandle == scene.handle)
                return _fadeStartedAt >= 0f;

            // Scene activation precedes local-player control by enough time that a
            // scene-based timer can finish behind the loading transition. Keep the
            // overlay transparent until the local HUD is genuinely playable, then
            // start the deliberate entrance fade the player can actually see.
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (player == null || !player.isPlayerControlled || HUDManager.Instance == null)
            {
                if (_promptCanvasGroup != null)
                    _promptCanvasGroup.alpha = 0f;
                return false;
            }

            _fadeSceneHandle = scene.handle;
            _fadeStartedAt = Time.unscaledTime;
            _lastActivitySignature = int.MinValue;
            _activitySignatureInitialized = false;
            WakePromptPresentation();
            if (_promptCanvasGroup != null)
                _promptCanvasGroup.alpha = 0f;
            return true;
        }

        private static PlayerControllerB ResolveLocalPlayer()
        {
            return GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
        }

        private static bool UpdateHeldItemState(PlayerControllerB player)
        {
            GrabbableObject held = player != null ? player.currentlyHeldObjectServer : null;
            if (held != null && held.isPocketed)
                held = null;

            int instanceId = held != null ? held.GetInstanceID() : 0;
            if (instanceId == _heldItemInstanceId)
                return false;

            _heldItemInstanceId = instanceId;
            _heldWeapon = IsWeapon(held) ? held : null;
            _weaponPromptShownAt = _heldWeapon != null ? Time.unscaledTime : -1f;
            _weaponPromptsSuppressed = false;
            _lastSourceSignature = int.MinValue;
            _promptDirty = true;
            WakePromptPresentation();
            return true;
        }

        /// <summary>
        /// The weapon types live in Better Armory, so this is a full-type-name probe (#267). It
        /// answers false without that plugin, where there are no weapon prompts to suppress.
        /// </summary>
        private static bool IsWeapon(GrabbableObject item)
        {
            return ArmoryItemProbe.IsArmoryWeapon(item);
        }

        private static bool IsHeldWeaponPrompt(string normalized)
        {
            if (_heldWeapon == null || string.IsNullOrWhiteSpace(normalized))
                return false;
            if (normalized.StartsWith("Drop ", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Turn safety ", StringComparison.OrdinalIgnoreCase))
                return true;

            string[] tips = _heldWeapon.itemProperties != null
                ? _heldWeapon.itemProperties.toolTips
                : null;
            if (tips == null)
                return false;

            for (int i = 0; i < tips.Length; i++)
            {
                string tip = NormalizePromptText(tips[i]);
                if (string.Equals(normalized, tip, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool ObservePromptActivity(HUDManager hud, string cursorText)
        {
            int signature = BuildActivitySignature(hud, cursorText);
            if (_activitySignatureInitialized && signature == _lastActivitySignature)
                return false;

            _activitySignatureInitialized = true;
            _lastActivitySignature = signature;
            WakePromptPresentation();
            return true;
        }

        private static void WakePromptPresentation()
        {
            _lastActivityAt = Time.unscaledTime;
            _nextTickAt = 0f;
        }

        internal static void SetPersistentPrompt(string key, string text)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            string normalized = NormalizePromptText(text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                ClearPersistentPrompt(key);
                return;
            }

            if (_persistentPromptLines.TryGetValue(key, out string existing) && existing == normalized)
                return;

            _persistentPromptLines[key] = normalized;
            _promptDirty = true;
            WakePromptPresentation();
        }

        internal static void ClearPersistentPrompt(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            if (_persistentPromptLines.Remove(key))
            {
                _promptDirty = true;
                WakePromptPresentation();
            }
        }

        internal static void SetPostPlayerMenuPrompt(string key, string text)
        {
            SetPostPlayerMenuPromptLines(key, text == null ? null : new[] { text });
        }

        /// <summary>
        /// Registers an ordered block of prompt lines under one key. Each line is normalized
        /// individually, so a caller no longer has to flatten several bindings into one string to
        /// survive <see cref="NormalizePromptText"/>. Blocks render in the order their keys were
        /// first registered, and lines render in the order given.
        /// </summary>
        internal static void SetPostPlayerMenuPromptLines(string key, IReadOnlyList<string> lines)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            string[] normalized = NormalizePromptBlock(lines);
            if (normalized.Length == 0)
            {
                ClearPostPlayerMenuPrompt(key);
                return;
            }

            if (_postPlayerMenuPromptBlocks.TryGetValue(key, out string[] existing)
                && BlocksEqual(existing, normalized))
                return;

            if (!_postPlayerMenuPromptBlocks.ContainsKey(key))
                _postPlayerMenuPromptKeys.Add(key);
            _postPlayerMenuPromptBlocks[key] = normalized;
            _promptDirty = true;
            WakePromptPresentation();
        }

        internal static void ClearPostPlayerMenuPrompt(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            if (_postPlayerMenuPromptBlocks.Remove(key))
            {
                _postPlayerMenuPromptKeys.Remove(key);
                _promptDirty = true;
                WakePromptPresentation();
            }
        }

        private static string[] NormalizePromptBlock(IReadOnlyList<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return Array.Empty<string>();

            var normalized = new List<string>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = NormalizePromptText(lines[i]);
                if (!string.IsNullOrWhiteSpace(line))
                    normalized.Add(line);
            }

            return normalized.Count == 0 ? Array.Empty<string>() : normalized.ToArray();
        }

        private static bool BlocksEqual(string[] left, string[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Replaces the vanilla cursor/interact text while a takeover is active, but only when
        /// vanilla is actually showing something. Nothing hovered still means no cursor line.
        /// </summary>
        internal static void SetCursorTipOverride(string text)
        {
            string normalized = string.IsNullOrWhiteSpace(text) ? null : NormalizePromptText(text);
            if (string.Equals(_cursorTipOverride, normalized, StringComparison.Ordinal))
                return;

            _cursorTipOverride = normalized;
            _promptDirty = true;
            WakePromptPresentation();
        }

        private static string ResolveCursorText(string rawCursorText)
        {
            if (_cursorTipOverride == null || string.IsNullOrWhiteSpace(rawCursorText))
                return rawCursorText;

            return _cursorTipOverride;
        }

        private static void ApplyTextStyle(TextMeshProUGUI tmp, HUDManager hud, TextAlignmentOptions alignment, bool flatten = true)
        {
            if (tmp == null)
                return;

            TMP_FontAsset font = ResolveVanillaFont(hud);
            if (font != null && tmp.font != font)
                tmp.font = font;
            if (!Mathf.Approximately(tmp.fontSize, Y4ngzUiStyle.ControlTextSize))
                tmp.fontSize = Y4ngzUiStyle.ControlTextSize;
            if (tmp.fontStyle != FontStyles.Normal)
                tmp.fontStyle = FontStyles.Normal;
            if (tmp.alignment != alignment)
                tmp.alignment = alignment;
            if (!Mathf.Approximately(tmp.characterSpacing, 0f))
                tmp.characterSpacing = 0f;
            if (!Mathf.Approximately(tmp.lineSpacing, 0f))
                tmp.lineSpacing = 0f;
            if (tmp.enableAutoSizing)
                tmp.enableAutoSizing = false;
            if (tmp.enableWordWrapping)
                tmp.enableWordWrapping = false;
            if (tmp.overflowMode != TextOverflowModes.Overflow)
                tmp.overflowMode = TextOverflowModes.Overflow;
            if (!tmp.richText)
                tmp.richText = true;
            if (tmp.color != Y4ngzUiStyle.CurrentAccentColor)
                tmp.color = Y4ngzUiStyle.CurrentAccentColor;

            if (flatten)
            {
                string flattened = FlattenToSingleLine(tmp.text);
                if (flattened != tmp.text)
                    tmp.text = flattened;
            }
        }

        private static void RefreshPromptOverlay(string cursorText)
        {
            EnsurePromptOverlay();
            if (_promptLines == null)
                return;

            int lineCount = BuildPromptLines(cursorText, _cachedPromptLines);
            int signature = BuildPromptSignature(_cachedPromptLines, lineCount);
            bool textChanged = signature != _lastPromptSignature;
            if (textChanged)
                _lastPromptSignature = signature;

            for (int i = 0; i < _promptLines.Length; i++)
            {
                TextMeshProUGUI line = _promptLines[i];
                if (line == null)
                    continue;

                bool visible = i < lineCount && !string.IsNullOrWhiteSpace(_cachedPromptLines[i]);
                if (line.enabled != visible)
                    line.enabled = visible;
                if (textChanged)
                {
                    string targetText = visible ? _cachedPromptLines[i] : string.Empty;
                    if (line.text != targetText)
                        line.text = targetText;
                }

                ApplyPromptLineStyleIfNeeded(line);
            }
        }

        private static int BuildPromptLines(string cursorText, string[] output)
        {
            for (int i = 0; i < output.Length; i++)
                output[i] = string.Empty;

            int count = 0;

            string cursor = NormalizePromptText(cursorText);
            if (!string.IsNullOrWhiteSpace(cursor))
                AddPromptLine(output, ref count, cursor);

            // Poltergeist's expanded spectator controls contain eleven meaningful lines.
            // Keep them ahead of generic control-tip producers so the twelve-line budget can
            // always hold the interaction cursor plus the complete active control set.
            AppendExternalPromptLines(output, ref count);

            HUDManager hud = HUDManager.Instance;
            if (hud != null && hud.controlTipLines != null)
            {
                for (int i = 0; i < hud.controlTipLines.Length && count < MaxPromptLines; i++)
                {
                    TextMeshProUGUI controlLine = hud.controlTipLines[i];
                    if (controlLine == null)
                        continue;

                    string text = NormalizePromptText(controlLine.text);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    if (_weaponPromptsSuppressed && IsHeldWeaponPrompt(text))
                        continue;
                    if (!string.IsNullOrWhiteSpace(text))
                        AddPromptLine(output, ref count, text);
                }
            }

            AppendPersistentPromptLines(output, ref count);

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            bool playerIsShipSide = player != null && (player.isInHangarShipRoom || player.isInElevator || (StartOfRound.Instance != null && StartOfRound.Instance.inShipPhase));
            bool playerMenuAvailable = playerIsShipSide && !player.inTerminalMenu && !player.isTypingChat;
            if (playerMenuAvailable)
                AddPromptLine(
                    output,
                    ref count,
                    "Player Menu: [" + Y4NGZUpgrades.Gui.UpgradeInput.DisplayLabel(
                        Y4NGZUpgrades.Gui.Plugin.Keybinds?.PurchaseMenu,
                        "P") + "]");

            AppendPostPlayerMenuPromptLines(output, ref count);
            return count;
        }

        private static void AppendPersistentPromptLines(string[] lines, ref int count)
        {
            if (_persistentPromptLines.Count == 0 || count >= MaxPromptLines)
                return;

            foreach (KeyValuePair<string, string> pair in _persistentPromptLines)
            {
                if (count >= MaxPromptLines)
                    return;

                if (!string.IsNullOrWhiteSpace(pair.Value))
                    AddPromptLine(lines, ref count, pair.Value);
            }
        }

        private static void AppendPostPlayerMenuPromptLines(string[] lines, ref int count)
        {
            if (_postPlayerMenuPromptKeys.Count == 0 || count >= MaxPromptLines)
                return;

            for (int k = 0; k < _postPlayerMenuPromptKeys.Count; k++)
            {
                if (count >= MaxPromptLines)
                    return;
                if (!_postPlayerMenuPromptBlocks.TryGetValue(_postPlayerMenuPromptKeys[k], out string[] block))
                    continue;

                for (int i = 0; i < block.Length; i++)
                {
                    if (count >= MaxPromptLines)
                        return;

                    AddPromptLine(lines, ref count, block[i]);
                }
            }
        }

        private static void RefreshExternalPromptSources()
        {
            bool hasActivePoltergeistSource =
                TryGetActivePoltergeistPromptSource(out TextMeshProUGUI poltergeistSource);
            _activePoltergeistPromptSource = hasActivePoltergeistSource
                ? poltergeistSource
                : null;

            if (hasActivePoltergeistSource)
            {
                bool sourceChanged = _externalPromptSources.Count != 1
                    || !ReferenceEquals(_externalPromptSources[0], poltergeistSource);
                if (sourceChanged)
                {
                    _externalPromptSources.Clear();
                    _externalPromptSources.Add(poltergeistSource);
                    _promptDirty = true;
                }

                ApplyTextStyle(poltergeistSource, HUDManager.Instance, TextAlignmentOptions.TopRight, flatten: false);
                if (poltergeistSource.enabled)
                    poltergeistSource.enabled = false;

                _wasDeadOrSpectating = true;
                _externalSourceStableScans = 0;
                return;
            }

            // Once the deployed producer contract resolves, its enabled controller is the
            // visibility authority. Do not resurrect the persistent controls clone by name
            // after Poltergeist disables its spectator camera.
            if (PoltergeistControllerContractResolved)
            {
                if (_externalPromptSources.Count > 0)
                {
                    _externalPromptSources.Clear();
                    _promptDirty = true;
                }

                _wasDeadOrSpectating = false;
                _externalSourceStableScans = 0;
                return;
            }

            bool deadOrSpectating = LocalPlayerIsDeadOrSpectating();
            if (!deadOrSpectating)
            {
                if (_externalPromptSources.Count > 0)
                {
                    _externalPromptSources.Clear();
                    _promptDirty = true;
                }

                _wasDeadOrSpectating = false;
                _externalSourceStableScans = 0;
                return;
            }

            if (!_wasDeadOrSpectating)
            {
                _wasDeadOrSpectating = true;
                _externalSourceStableScans = 0;
                _nextExternalSourceScanAt = 0f;
                _promptDirty = true;
            }

            float now = Time.unscaledTime;
            if (now < _nextExternalSourceScanAt)
                return;

            int previousCount = _externalPromptSources.Count;
            _externalPromptSources.Clear();
            TextMeshProUGUI[] texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
            foreach (TextMeshProUGUI text in texts)
            {
                if (!IsExternalPromptSource(text))
                    continue;

                ApplyTextStyle(text, HUDManager.Instance, TextAlignmentOptions.TopRight, flatten: false);
                text.enabled = false;
                _externalPromptSources.Add(text);
            }

            if (_externalPromptSources.Count != previousCount)
            {
                _promptDirty = true;
                _externalSourceStableScans = 0;
            }
            else
            {
                _externalSourceStableScans++;
            }

            _nextExternalSourceScanAt = now + (_externalSourceStableScans >= 3
                ? ExternalSourceStableScanInterval
                : ExternalSourceInitialScanInterval);
        }

        private static bool PoltergeistControllerContractResolved =>
            _poltergeistControllerType != null
            && _poltergeistControllerInstanceField != null
            && _poltergeistControlsTextField != null;

        private static bool TryGetActivePoltergeistPromptSource(out TextMeshProUGUI source)
        {
            source = null;

            try
            {
                Type controllerType = _poltergeistControllerType;
                if (controllerType == null)
                {
                    if (_poltergeistControllerContractLookupAttempted)
                        return false;

                    _poltergeistControllerContractLookupAttempted = true;
                    controllerType = AccessTools.TypeByName(PoltergeistControllerTypeName);
                    if (controllerType == null)
                        return false;

                    _poltergeistControllerType = controllerType;
                    _poltergeistControllerInstanceField = AccessTools.Field(controllerType, "instance");
                    _poltergeistControlsTextField = AccessTools.Field(controllerType, "controlsText");
                }

                if (!PoltergeistControllerContractResolved)
                    return false;

                object controller = _poltergeistControllerInstanceField.GetValue(null);
                Behaviour controllerBehaviour = controller as Behaviour;
                if (controllerBehaviour == null || !controllerBehaviour.isActiveAndEnabled)
                    return false;

                source = _poltergeistControlsTextField.GetValue(controller) as TextMeshProUGUI;
                return source != null
                    && source.gameObject != null
                    && source.gameObject.activeInHierarchy;
            }
            catch
            {
                source = null;
                return false;
            }
        }

        private static bool IsExternalPromptSource(TextMeshProUGUI text)
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy)
                return false;

            if (ReferenceEquals(text, _activePoltergeistPromptSource))
                return true;

            string objectName = text.gameObject.name ?? string.Empty;
            if (objectName.IndexOf("Y4NGZUpgrades_PromptOverlay", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (objectName.IndexOf("PoltergeistControlsText", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return LocalPlayerIsDeadOrSpectating();

            string hierarchy = GetHierarchyName(text.transform);
            return hierarchy.IndexOf("PoltergeistControlsText", System.StringComparison.OrdinalIgnoreCase) >= 0
                && LocalPlayerIsDeadOrSpectating();
        }

        private static bool LocalPlayerIsDeadOrSpectating()
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;

            return player != null && player.isPlayerDead;
        }

        private static void AppendExternalPromptLines(string[] lines, ref int count)
        {
            if (_externalPromptSources.Count == 0 || count >= MaxPromptLines)
                return;

            for (int i = _externalPromptSources.Count - 1; i >= 0 && count < MaxPromptLines; i--)
            {
                TextMeshProUGUI source = _externalPromptSources[i];
                if (!IsExternalPromptSource(source))
                {
                    _externalPromptSources.RemoveAt(i);
                    _promptDirty = true;
                    continue;
                }

                if (source.enabled)
                    source.enabled = false;

                string text = source.text;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                string[] sourceLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (string sourceLine in sourceLines)
                {
                    if (count >= MaxPromptLines)
                        break;

                    string normalized = NormalizePromptText(sourceLine);
                    if (!string.IsNullOrWhiteSpace(normalized))
                        AddPromptLine(lines, ref count, normalized);
                }
            }
        }

        private static void EnsurePromptOverlay()
        {
            if (!_motionSubscribed)
            {
                GameplayHudMotion.MotionSampled += OnHudMotionSampled;
                _motionSubscribed = true;
            }

            if (_promptLines != null)
                return;

            if (_promptCanvasObject == null)
            {
                _promptCanvasObject = new GameObject("Y4NGZUpgrades_PromptOverlayCanvas");
                UnityEngine.Object.DontDestroyOnLoad(_promptCanvasObject);

                var canvas = _promptCanvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 104;

                var scaler = _promptCanvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 1f;

                _promptCanvasGroup = _promptCanvasObject.AddComponent<CanvasGroup>();
                _promptCanvasGroup.interactable = false;
                _promptCanvasGroup.blocksRaycasts = false;

                var rootObject = new GameObject("PromptMotionRoot", typeof(RectTransform));
                rootObject.transform.SetParent(_promptCanvasObject.transform, false);
                _promptRoot = rootObject.GetComponent<RectTransform>();
                _promptRoot.anchorMin = Vector2.zero;
                _promptRoot.anchorMax = Vector2.one;
                _promptRoot.pivot = new Vector2(0.5f, 0.5f);
                _promptRoot.offsetMin = Vector2.zero;
                _promptRoot.offsetMax = Vector2.zero;
                _motionOffset = GameplayHudMotion.CurrentOffset;
                _currentStackTop = ResolvePromptStackTop();
                _stackLayoutInitialized = true;
                ApplyPromptRootPosition();

                BeginInitialFadeForCurrentSceneIfNeeded();
                _promptCanvasObject.SetActive(GameplayUiVisibility.IsVisible);
            }

            _promptLines = new TextMeshProUGUI[MaxPromptLines];
            for (int i = 0; i < _promptLines.Length; i++)
            {
                var textGo = new GameObject($"PromptLine_{i:00}", typeof(RectTransform));
                textGo.transform.SetParent(_promptRoot != null ? _promptRoot : _promptCanvasObject.transform, false);
                TextMeshProUGUI line = textGo.AddComponent<TextMeshProUGUI>();
                ApplyTextStyle(line, HUDManager.Instance, TextAlignmentOptions.TopRight);
                PositionPromptLine(line.rectTransform, i);
                line.enabled = false;
                _promptLines[i] = line;
            }
        }

        private static void OnHudMotionSampled(Vector2 offset)
        {
            _motionOffset = offset;
            ApplyPromptRootPosition();
        }

        private static void UpdateStackLayoutFrame()
        {
            if (_promptRoot == null)
                return;

            float targetTop = ResolvePromptStackTop();
            if (!_stackLayoutInitialized)
            {
                _currentStackTop = targetTop;
                _stackTopVelocity = 0f;
                _stackLayoutInitialized = true;
            }
            else if (targetTop > _currentStackTop)
            {
                // Follow downward growth directly so the stack gap can never
                // collapse. Upward compaction remains eased below.
                _currentStackTop = targetTop;
                _stackTopVelocity = 0f;
            }
            else
            {
                _currentStackTop = Mathf.SmoothDamp(
                    _currentStackTop,
                    targetTop,
                    ref _stackTopVelocity,
                    StackLayoutSmoothTime,
                    Mathf.Infinity,
                    Mathf.Max(0.0001f, Time.unscaledDeltaTime));
            }

            ApplyPromptRootPosition();
        }

        private static void ApplyPromptRootPosition()
        {
            if (_promptRoot == null)
                return;

            _promptRoot.anchoredPosition = _motionOffset
                + new Vector2(0f, -(_currentStackTop - PromptBaseTop));
        }

        private static void PositionPromptLine(RectTransform rect, int index)
        {
            if (rect == null)
                return;

            rect.anchorMin = PromptAnchor;
            rect.anchorMax = PromptAnchor;
            rect.pivot = PromptPivot;
            rect.anchoredPosition = new Vector2(-PromptRight, -PromptBaseTop - index * PromptSpacing);
            rect.sizeDelta = new Vector2(PromptWidth, PromptHeight);
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
        }

        private static float ResolvePromptStackTop()
        {
            return Mathf.Max(PromptBaseTop, ContractHudLayoutBridge.GetStackedPromptTop());
        }

        private static string FlattenToSingleLine(string text)
        {
            return string.IsNullOrEmpty(text)
                ? text
                : text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        }

        private static string NormalizePromptText(string text)
        {
            string normalized = FlattenToSingleLine(text);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            normalized = normalized.Trim();

            if (normalized.Contains("Open Emote") || normalized.Contains("Emote Radial") || normalized.Contains("Emote Menu"))
                return "Emote Wheel : [`]";

            return normalized;
        }

        private static void ApplyHudSourceStyleIfNeeded(HUDManager hud, bool force = false)
        {
            if (hud == null || hud.controlTipLines == null)
                return;

            Color accent = Y4ngzUiStyle.CurrentAccentColor;
            bool restyle = force
                || _lastStyledHud != hud
                || _lastStyledControlTipLines != hud.controlTipLines
                || _lastStyledHudAccent != accent;

            for (int i = 0; i < hud.controlTipLines.Length; i++)
            {
                TextMeshProUGUI line = hud.controlTipLines[i];
                if (line == null)
                    continue;

                if (restyle)
                    ApplyTextStyle(line, hud, TextAlignmentOptions.Left);
                if (line.enabled)
                    line.enabled = false;
            }

            if (restyle)
            {
                _lastStyledHud = hud;
                _lastStyledControlTipLines = hud.controlTipLines;
                _lastStyledHudAccent = accent;
            }
        }

        private static void ApplyCursorSourceStyleIfNeeded(TextMeshProUGUI source, HUDManager hud, bool force = false)
        {
            if (source == null)
                return;

            Color accent = Y4ngzUiStyle.CurrentAccentColor;
            if (!force && _lastStyledCursorTip == source && _lastStyledCursorAccent == accent)
                return;

            ApplyTextStyle(source, hud, TextAlignmentOptions.Center);
            _lastStyledCursorTip = source;
            _lastStyledCursorAccent = accent;
        }

        private static void ApplyPromptLineStyleIfNeeded(TextMeshProUGUI line)
        {
            if (!Mathf.Approximately(line.fontSize, Y4ngzUiStyle.ControlTextSize))
                line.fontSize = Y4ngzUiStyle.ControlTextSize;
            if (line.color != Y4ngzUiStyle.CurrentAccentColor)
                line.color = Y4ngzUiStyle.CurrentAccentColor;
            if (line.alignment != TextAlignmentOptions.TopRight)
                line.alignment = TextAlignmentOptions.TopRight;
        }

        private static TMP_FontAsset ResolveVanillaFont(HUDManager hud)
        {
            if (_fontCache != null)
                return _fontCache;

            if (hud != null && hud.clockNumber != null && hud.clockNumber.font != null)
            {
                _fontCache = hud.clockNumber.font;
                return _fontCache;
            }

            if (Time.unscaledTime < _nextFontFallbackScanAt)
                return null;
            _nextFontFallbackScanAt = Time.unscaledTime + FontFallbackScanInterval;

            TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            foreach (TMP_FontAsset font in fonts)
            {
                if (font.name.Contains("3270") || font.name.Contains("edunline") || font.name.Contains("EdgeOf"))
                {
                    _fontCache = font;
                    return _fontCache;
                }
            }

            return null;
        }

        private static int BuildSourceSignature(HUDManager hud, string cursorText)
        {
            int signature = AddSignaturePart(17, NormalizePromptText(cursorText));
            if (hud != null && hud.controlTipLines != null)
            {
                for (int i = 0; i < hud.controlTipLines.Length; i++)
                {
                    TextMeshProUGUI line = hud.controlTipLines[i];
                    signature = AddSignaturePart(
                        signature,
                        line == null ? string.Empty : NormalizePromptText(line.text));
                }
            }

            signature = AddSignaturePart(signature, IsPlayerMenuAvailable() ? "ship:1" : "ship:0");
            signature = AddSignaturePart(signature, _externalPromptSources.Count);
            for (int i = 0; i < _externalPromptSources.Count; i++)
            {
                TextMeshProUGUI source = _externalPromptSources[i];
                signature = AddSignaturePart(
                    signature,
                    source == null ? string.Empty : NormalizePromptText(source.text));
            }
            foreach (KeyValuePair<string, string> pair in _persistentPromptLines)
            {
                signature = AddSignaturePart(signature, pair.Key);
                signature = AddSignaturePart(signature, pair.Value);
            }
            signature = AddPostPlayerMenuSignatureParts(signature);
            return signature;
        }

        /// <summary>
        /// Hashes every key and every line in registration order, so a reordered or edited block
        /// redraws. Both signature builders share this: a block that changes without changing the
        /// signature would leave stale lines on screen until some other producer moved.
        /// </summary>
        private static int AddPostPlayerMenuSignatureParts(int signature)
        {
            for (int k = 0; k < _postPlayerMenuPromptKeys.Count; k++)
            {
                string key = _postPlayerMenuPromptKeys[k];
                signature = AddSignaturePart(signature, key);
                if (!_postPlayerMenuPromptBlocks.TryGetValue(key, out string[] block))
                    continue;

                signature = AddSignaturePart(signature, block.Length);
                for (int i = 0; i < block.Length; i++)
                    signature = AddSignaturePart(signature, block[i]);
            }

            return signature;
        }

        private static int BuildActivitySignature(HUDManager hud, string cursorText)
        {
            int signature = AddSignaturePart(17, NormalizePromptText(cursorText));
            if (hud != null && hud.controlTipLines != null)
            {
                for (int i = 0; i < hud.controlTipLines.Length; i++)
                {
                    TextMeshProUGUI line = hud.controlTipLines[i];
                    string text = line != null ? NormalizePromptText(line.text) : string.Empty;
                    if (!IsHeldWeaponPrompt(text))
                        signature = AddSignaturePart(signature, text);
                }
            }

            for (int i = 0; i < _externalPromptSources.Count; i++)
            {
                TextMeshProUGUI source = _externalPromptSources[i];
                signature = AddSignaturePart(signature, source != null ? source.text : string.Empty);
            }
            foreach (KeyValuePair<string, string> pair in _persistentPromptLines)
            {
                signature = AddSignaturePart(signature, pair.Key);
                signature = AddSignaturePart(signature, pair.Value);
            }
            signature = AddPostPlayerMenuSignatureParts(signature);
            return AddSignaturePart(signature, IsPlayerMenuAvailable() ? 1 : 0);
        }

        private static int AddSignaturePart(int signature, string value)
        {
            unchecked { return signature * 31 + (value != null ? value.GetHashCode() : 0); }
        }

        private static int AddSignaturePart(int signature, int value)
        {
            unchecked { return signature * 31 + value; }
        }

        private static bool IsPlayerMenuAvailable()
        {
            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            return player != null
                && (player.isInHangarShipRoom || player.isInElevator || (StartOfRound.Instance != null && StartOfRound.Instance.inShipPhase))
                && !player.inTerminalMenu
                && !player.isTypingChat;
        }

        private static void AddPromptLine(string[] lines, ref int count, string text)
        {
            if (count >= MaxPromptLines || string.IsNullOrWhiteSpace(text))
                return;

            for (int i = 0; i < count; i++)
            {
                if (lines[i] == text)
                    return;
            }

            lines[count++] = text;
        }

        private static int BuildPromptSignature(string[] lines, int count)
        {
            int signature = 17;
            for (int i = 0; i < count; i++)
                signature = AddSignaturePart(signature, lines[i]);
            return signature;
        }

        private static string GetHierarchyName(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string value = transform.name ?? string.Empty;
            Transform current = transform.parent;
            while (current != null)
            {
                value += "/" + (current.name ?? string.Empty);
                current = current.parent;
            }

            return value;
        }
    }

    [HarmonyPatch(typeof(HUDManager), "Start")]
    internal static class Y4ngzPromptStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(HUDManager __instance)
        {
            Y4ngzPromptOverlay.Apply(__instance);
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.ChangeControlTip))]
    internal static class Y4ngzPromptSinglePatch
    {
        [HarmonyPostfix]
        private static void Postfix(HUDManager __instance)
        {
            Y4ngzPromptOverlay.Apply(__instance);
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.ChangeControlTipMultiple))]
    internal static class Y4ngzPromptMultiplePatch
    {
        [HarmonyPostfix]
        private static void Postfix(HUDManager __instance)
        {
            Y4ngzPromptOverlay.Apply(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "SetHoverTipAndCurrentInteractTrigger")]
    internal static class Y4ngzPromptCursorPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerControllerB __instance)
        {
            Y4ngzPromptOverlay.Apply(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
    internal static class Y4ngzPromptTickPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerControllerB __instance)
        {
            if (__instance == null || __instance != GameNetworkManager.Instance?.localPlayerController)
                return;

            Y4ngzPromptOverlay.Tick();
        }
    }
}
