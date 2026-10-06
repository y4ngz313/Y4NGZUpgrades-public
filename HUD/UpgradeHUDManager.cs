using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.UITheme;

namespace Y4NGZUpgrades.HUD
{
    /// <summary>
    /// Fallback prompt renderers for when the optional Y4NGZUI composer is absent: the single
    /// persistent interaction prompt (hold-hacks) and the post-player-menu control stack.
    /// </summary>
    internal static class UpgradeHUDManager
    {
        private const float PromptWidth = 560f;
        private const float PromptHeight = 26f;
        private const float PromptOffsetY = -108f;
        private const float PromptFillWidth = 190f;

        // Post-player-menu stack. Placement and type match Y4NGZUI's prompt overlay (top-right
        // column, 1080-referenced units) so a profile without Y4NGZUI reads the same.
        private const int StackMaxLines = 12;
        private const float StackRight = 24f;
        private const float StackBaseTop = 224f;
        private const float StackWidth = 460f;
        private const float StackRowHeight = 26f;
        private const float StackSpacing = 27f;
        private const float StackRowGap = StackSpacing - StackRowHeight;
        // Clearance below the lowest visible vanilla control tip when the stack has to start
        // beneath them instead of at its resting top.
        private const float StackVanillaTipGap = 6f;
        private const float StackFontSize = 24f;
        private const float StackInactivityDelay = 3f;
        private const float StackFadeDuration = 0.55f;
        private const float StackTickInterval = 0.1f;
        private const float ReferenceHeight = 1080f;

        private static GameObject _promptRoot;
        private static TextMeshProUGUI _promptLabel;
        private static Image _promptFill;
        private static Sprite _whiteFillSprite;
        private static readonly List<string> PromptOrder = new List<string>();
        private static readonly Dictionary<string, string> PromptLines =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly List<string> StackKeys = new List<string>();
        private static readonly Dictionary<string, string[]> StackBlocks =
            new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static readonly List<string> StackLines = new List<string>(StackMaxLines);
        // Keys whose block suspends the inactivity fade while it has lines (the held tablet).
        private static readonly HashSet<string> StackHoldOpenKeys = new HashSet<string>(StringComparer.Ordinal);
        private static bool _stackHeldOpen;
        private static GameObject _stackHost;
        private static Canvas _stackCanvas;
        private static Transform _stackPauseMenuAnchor;
        private static RectTransform _stackRoot;
        private static CanvasGroup _stackGroup;
        private static TextMeshProUGUI[] _stackRows;
        private static bool _stackFontApplied;
        private static bool _stackRowsDirty;
        private static bool _stackLayoutDirty;
        private static float _stackLastChangeAt;
        private static float _stackNextTickAt;
        private static float _stackScaleRatio = -1f;
        private static float _stackTop = StackBaseTop;
        private static float _stackAlpha = -1f;

        internal static void RefreshTheme()
        {
            if (_promptLabel != null) _promptLabel.color = WithAlpha(UiTheme.Accent, _promptLabel.color.a);
            if (_promptFill != null) _promptFill.color = WithAlpha(UiTheme.Accent, _promptFill.color.a);
            if (_stackRows == null) return;
            for (int i = 0; i < _stackRows.Length; i++)
                if (_stackRows[i] != null) _stackRows[i].color = UiTheme.Accent;
        }

        internal static void SetPersistentPrompt(string key, string text)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (string.IsNullOrEmpty(text)) { ClearPersistentPrompt(key); return; }
            if (PromptLines.TryGetValue(key, out string current) && string.Equals(current, text, StringComparison.Ordinal))
            {
                EnsurePromptVisible();
                return;
            }
            if (!PromptLines.ContainsKey(key)) PromptOrder.Add(key);
            PromptLines[key] = text;
            RefreshPersistentPrompt();
        }

        internal static void ClearPersistentPrompt(string key)
        {
            if (string.IsNullOrEmpty(key) || !PromptLines.Remove(key)) return;
            PromptOrder.Remove(key);
            if (_promptFill != null) _promptFill.fillAmount = 0f;
            RefreshPersistentPrompt();
        }

        /// <summary>Drops every fallback prompt and destroys both renderers.</summary>
        internal static void ClearAll()
        {
            PromptLines.Clear();
            PromptOrder.Clear();
            if (_promptRoot != null) UnityEngine.Object.Destroy(_promptRoot);
            _promptRoot = null;
            _promptLabel = null;
            _promptFill = null;

            StackKeys.Clear();
            StackBlocks.Clear();
            StackLines.Clear();
            StackHoldOpenKeys.Clear();
            _stackHeldOpen = false;
            if (_stackHost != null) UnityEngine.Object.Destroy(_stackHost);
            _stackHost = null;
            _stackCanvas = null;
            _stackPauseMenuAnchor = null;
            _stackRoot = null;
            _stackGroup = null;
            _stackRows = null;
        }

        internal static void SetPersistentPromptProgress(string key, float normalized)
        {
            if (string.IsNullOrEmpty(key)) return;
            normalized = Mathf.Clamp01(normalized);
            if (_promptFill != null && PromptLines.ContainsKey(key)) _promptFill.fillAmount = normalized;
            if (normalized <= 0f) return;
            HUDManager hud = HUDManager.Instance;
            if (hud == null) return;
            hud.holdFillAmount = normalized;
            if (hud.holdInteractionFillAmount != null) hud.holdInteractionFillAmount.fillAmount = normalized;
        }

        /// <summary>Single-line block; null or whitespace clears the key (Y4NGZUI's contract).</summary>
        internal static void SetPostPlayerMenuPrompt(string key, string text)
        {
            if (string.IsNullOrEmpty(key)) return;
            string line = NormalizeLine(text);
            if (line == null) { ClearPostPlayerMenuPrompt(key); return; }
            if (StackBlocks.TryGetValue(key, out string[] current)
                && current.Length == 1
                && string.Equals(current[0], line, StringComparison.Ordinal))
            {
                EnsureStackHost();
                return;
            }
            StoreStackBlock(key, new[] { line });
        }

        /// <summary>
        /// Keyed block in registration order: re-setting a key keeps its position, empty lines are
        /// dropped, an empty block clears the key and an unchanged block is a no-op (it neither
        /// allocates nor wakes the fade).
        /// </summary>
        internal static void SetPostPlayerMenuPromptLines(string key, IReadOnlyList<string> lines)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (StackBlocks.TryGetValue(key, out string[] current) && BlockMatches(current, lines))
            {
                EnsureStackHost();
                return;
            }

            int count = 0;
            if (lines != null)
                for (int i = 0; i < lines.Count; i++)
                    if (NormalizeLine(lines[i]) != null) count++;
            if (count == 0) { ClearPostPlayerMenuPrompt(key); return; }

            string[] block = new string[count];
            int index = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = NormalizeLine(lines[i]);
                if (line != null) block[index++] = line;
            }
            StoreStackBlock(key, block);
        }

        internal static void ClearPostPlayerMenuPrompt(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            StackHoldOpenKeys.Remove(key);
            if (!StackBlocks.Remove(key)) { RefreshStackHold(); return; }
            StackKeys.Remove(key);
            MarkStackChanged();
        }

        /// <summary>
        /// While a held key has lines the stack stays at full alpha and its inactivity timer is
        /// held at zero; dropping the hold starts the three-second timer from that moment.
        /// Clearing the key clears its hold.
        /// </summary>
        internal static void SetPostPlayerMenuPromptHoldOpen(string key, bool holdOpen)
        {
            if (string.IsNullOrEmpty(key)) return;
            bool changed = holdOpen ? StackHoldOpenKeys.Add(key) : StackHoldOpenKeys.Remove(key);
            if (changed) RefreshStackHold();
        }

        private static void EnsurePromptVisible()
        {
            if (_promptRoot == null) RefreshPersistentPrompt();
            else if (!_promptRoot.activeSelf) _promptRoot.SetActive(true);
        }

        private static void RefreshPersistentPrompt()
        {
            if (PromptLines.Count == 0)
            {
                if (_promptRoot != null) _promptRoot.SetActive(false);
                return;
            }
            if (!EnsurePromptElement()) return;
            _promptLabel.text = string.Join("\n", PromptOrder.ConvertAll(key => PromptLines[key]).ToArray());
            _promptRoot.SetActive(true);
        }

        private static bool EnsurePromptElement()
        {
            if (_promptRoot != null && _promptLabel != null && _promptFill != null) return true;
            Canvas canvas = HUDManager.Instance?.playerScreenTexture?.canvas;
            if (canvas == null) return false;
            if (_promptRoot != null) UnityEngine.Object.Destroy(_promptRoot);

            _promptRoot = new GameObject("UpgradePromptFallback");
            _promptRoot.transform.SetParent(canvas.transform, false);
            RectTransform root = _promptRoot.AddComponent<RectTransform>();
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(.5f, .5f);
            root.sizeDelta = new Vector2(PromptWidth, PromptHeight);
            root.anchoredPosition = new Vector2(0f, PromptOffsetY);

            _promptLabel = new GameObject("Label").AddComponent<TextMeshProUGUI>();
            _promptLabel.transform.SetParent(_promptRoot.transform, false);
            if (HUDManager.Instance.clockNumber != null) _promptLabel.font = HUDManager.Instance.clockNumber.font;
            _promptLabel.fontSize = 13f;
            _promptLabel.alignment = TextAlignmentOptions.Center;
            _promptLabel.enableWordWrapping = true;
            _promptLabel.raycastTarget = false;
            RectTransform label = _promptLabel.rectTransform;
            label.anchorMin = Vector2.zero; label.anchorMax = Vector2.one;
            label.offsetMin = new Vector2(0f, 6f); label.offsetMax = Vector2.zero;

            GameObject track = new GameObject("HoldTrack");
            track.transform.SetParent(_promptRoot.transform, false);
            RectTransform trackRect = track.AddComponent<RectTransform>();
            trackRect.anchorMin = trackRect.anchorMax = new Vector2(.5f, 0f);
            trackRect.pivot = new Vector2(.5f, 0f);
            trackRect.sizeDelta = new Vector2(PromptFillWidth, 3f);
            Image trackImage = track.AddComponent<Image>();
            trackImage.sprite = WhiteFillSprite;
            trackImage.color = new Color(0f, 0f, 0f, .45f);

            GameObject fill = new GameObject("HoldFill");
            fill.transform.SetParent(track.transform, false);
            RectTransform fillRect = fill.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero; fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero; fillRect.offsetMax = Vector2.zero;
            _promptFill = fill.AddComponent<Image>();
            _promptFill.sprite = WhiteFillSprite;
            _promptFill.type = Image.Type.Filled;
            _promptFill.fillMethod = Image.FillMethod.Horizontal;
            _promptFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _promptFill.raycastTarget = false;
            RefreshTheme();
            return true;
        }

        private static void StoreStackBlock(string key, string[] block)
        {
            if (!StackBlocks.ContainsKey(key)) StackKeys.Add(key);
            StackBlocks[key] = block;
            MarkStackChanged();
        }

        /// <summary>Recomposes the visible lines and wakes the fade; rows are rewritten on the next tick.</summary>
        private static void MarkStackChanged()
        {
            StackLines.Clear();
            for (int k = 0; k < StackKeys.Count && StackLines.Count < StackMaxLines; k++)
            {
                string[] block = StackBlocks[StackKeys[k]];
                for (int i = 0; i < block.Length && StackLines.Count < StackMaxLines; i++)
                    if (!StackLines.Contains(block[i])) StackLines.Add(block[i]);
            }

            _stackRowsDirty = true;
            _stackLastChangeAt = Time.unscaledTime;
            RefreshStackHold();
            EnsureStackHost();
        }

        /// <summary>Recomputes whether any held key has lines; either transition wakes the fade timer.</summary>
        private static void RefreshStackHold()
        {
            bool held = false;
            foreach (string key in StackHoldOpenKeys)
            {
                if (!StackBlocks.ContainsKey(key)) continue;
                held = true;
                break;
            }
            if (held == _stackHeldOpen) return;
            _stackHeldOpen = held;
            _stackLastChangeAt = Time.unscaledTime;
        }

        private static bool BlockMatches(string[] current, IReadOnlyList<string> lines)
        {
            if (lines == null) return false;
            int index = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = NormalizeLine(lines[i]);
                if (line == null) continue;
                if (index >= current.Length || !string.Equals(current[index], line, StringComparison.Ordinal))
                    return false;
                index++;
            }
            return index == current.Length;
        }

        /// <summary>Trimmed single-line text, or null for nothing to show. Allocation-free when already clean.</summary>
        private static string NormalizeLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string line = text.Trim();
            if (line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0)
                line = line.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            return line;
        }

        /// <summary>
        /// Creates the stack host under the vanilla HUD canvas when there is something to show and
        /// no live host. A scene change destroys the canvas and the host with it; the next producer
        /// call lands here and rebuilds it.
        /// </summary>
        private static void EnsureStackHost()
        {
            if (_stackHost != null || StackLines.Count == 0) return;
            Canvas canvas = ResolveHudCanvas();
            if (canvas == null) return;

            _stackHost = new GameObject("UpgradePromptStackFallback", typeof(RectTransform));
            _stackHost.layer = canvas.gameObject.layer;
            RectTransform host = (RectTransform)_stackHost.transform;
            host.SetParent(canvas.transform, false);
            Stretch(host);
            _stackCanvas = canvas;
            _stackPauseMenuAnchor = null;
            KeepStackBeneathPauseMenu();

            _stackGroup = _stackHost.AddComponent<CanvasGroup>();
            _stackGroup.interactable = false;
            _stackGroup.blocksRaycasts = false;
            _stackAlpha = -1f;

            GameObject rootObject = new GameObject("StackRoot", typeof(RectTransform));
            rootObject.layer = _stackHost.layer;
            _stackRoot = (RectTransform)rootObject.transform;
            _stackRoot.SetParent(host, false);
            _stackRoot.anchorMin = Vector2.zero;
            _stackRoot.anchorMax = Vector2.one;
            // Pivot top-right so the reference-scale keeps the column pinned to that corner.
            _stackRoot.pivot = Vector2.one;
            _stackRoot.offsetMin = Vector2.zero;
            _stackRoot.offsetMax = Vector2.zero;
            _stackScaleRatio = -1f;
            RefreshStackScale();

            _stackFontApplied = false;
            _stackRows = new TextMeshProUGUI[StackMaxLines];
            for (int i = 0; i < _stackRows.Length; i++)
            {
                GameObject rowObject = new GameObject($"PromptLine_{i:00}", typeof(RectTransform));
                rowObject.layer = _stackHost.layer;
                rowObject.transform.SetParent(_stackRoot, false);
                TextMeshProUGUI row = rowObject.AddComponent<TextMeshProUGUI>();
                row.fontSize = StackFontSize;
                row.fontStyle = FontStyles.Normal;
                row.alignment = TextAlignmentOptions.TopRight;
                row.characterSpacing = 0f;
                row.lineSpacing = 0f;
                row.enableAutoSizing = false;
                row.enableWordWrapping = true;
                row.overflowMode = TextOverflowModes.Overflow;
                row.richText = true;
                row.raycastTarget = false;
                row.color = UiTheme.Accent;
                RectTransform rect = row.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
                rect.sizeDelta = new Vector2(StackWidth, StackRowHeight);
                row.enabled = false;
                _stackRows[i] = row;
            }
            TryApplyStackFont();

            _stackHost.AddComponent<PromptStackDriver>();
            rootObject.SetActive(false);
            _stackTop = StackBaseTop;
            _stackNextTickAt = 0f;
            _stackRowsDirty = true;
            // A rebuilt host (the canvas went with a scene change) shows content that producers
            // re-send unchanged, which wakes nothing on its own: start a fresh fade window.
            _stackLastChangeAt = Time.unscaledTime;
            Plugin.Log?.LogInfo(
                $"[Prompt Stack] fallback post-player-menu stack hosted under '{canvas.name}' " +
                $"(scale ratio {_stackScaleRatio:F3}); Y4NGZUI's composer is not handling these prompts.");
        }

        /// <summary>Per-frame fade and visibility, plus the 10 Hz placement checks. Runs on the host.</summary>
        private static void TickStack()
        {
            if (_stackRoot == null || _stackRows == null) return;
            if (_stackRowsDirty) ApplyStackRows();

            float now = Time.unscaledTime;
            float alpha = StackLines.Count > 0 && IsStackPresentable() ? ResolveStackAlpha(now) : 0f;
            bool show = alpha > 0f;
            if (_stackRoot.gameObject.activeSelf != show) _stackRoot.gameObject.SetActive(show);
            if (!show) return;

            if (now >= _stackNextTickAt)
            {
                _stackNextTickAt = now + StackTickInterval;
                KeepStackBeneathPauseMenu();
                RefreshStackScale();
                if (!_stackFontApplied) TryApplyStackFont();
                float top = ResolveStackTop();
                if (!Mathf.Approximately(top, _stackTop))
                {
                    _stackTop = top;
                    _stackLayoutDirty = true;
                }
            }

            if (_stackLayoutDirty) LayoutStackRows();
            if (!Mathf.Approximately(_stackAlpha, alpha))
            {
                _stackAlpha = alpha;
                _stackGroup.alpha = alpha;
            }
        }

        private static void ApplyStackRows()
        {
            _stackRowsDirty = false;
            _stackLayoutDirty = true;
            for (int i = 0; i < _stackRows.Length; i++)
            {
                TextMeshProUGUI row = _stackRows[i];
                if (row == null) continue;
                bool used = i < StackLines.Count;
                string text = used ? StackLines[i] : string.Empty;
                if (!string.Equals(row.text, text, StringComparison.Ordinal)) row.text = text;
                if (row.enabled != used) row.enabled = used;
            }
        }

        /// <summary>
        /// Top-down from the resolved top; a row that wraps inside the 460-unit column takes the
        /// height its text needs. Runs only after a text or top change.
        /// </summary>
        private static void LayoutStackRows()
        {
            _stackLayoutDirty = false;
            float cursor = 0f;
            for (int i = 0; i < _stackRows.Length; i++)
            {
                TextMeshProUGUI row = _stackRows[i];
                if (row == null) continue;
                RectTransform rect = row.rectTransform;
                if (!row.enabled)
                {
                    rect.sizeDelta = new Vector2(StackWidth, StackRowHeight);
                    rect.anchoredPosition = new Vector2(-StackRight, -_stackTop - cursor);
                    continue;
                }

                float height = StackRowHeight;
                float preferred = row.GetPreferredValues(row.text, StackWidth, 0f).y;
                if (preferred > height) height = Mathf.Ceil(preferred);
                rect.sizeDelta = new Vector2(StackWidth, height);
                rect.anchoredPosition = new Vector2(-StackRight, -_stackTop - cursor);
                cursor += height + StackRowGap;
            }
        }

        /// <summary>
        /// The resting top, or just below the lowest drawn vanilla control tip when that reaches
        /// further down. Without Y4NGZUI those tips stay visible in the same top-right corner.
        /// </summary>
        private static float ResolveStackTop()
        {
            float top = StackBaseTop;
            TextMeshProUGUI[] tips = HUDManager.Instance != null ? HUDManager.Instance.controlTipLines : null;
            if (tips == null) return top;
            for (int i = 0; i < tips.Length; i++)
            {
                TextMeshProUGUI tip = tips[i];
                if (tip == null || !tip.isActiveAndEnabled || string.IsNullOrEmpty(tip.text)) continue;
                Bounds bounds = tip.textBounds;
                if (bounds.size.y <= 0f) continue;
                Vector3 bottom = tip.rectTransform.TransformPoint(new Vector3(bounds.center.x, bounds.min.y, 0f));
                // Stack-root space: the origin is its top-right pivot, so depth is the negated y.
                float depth = -_stackRoot.InverseTransformPoint(bottom).y + StackVanillaTipGap;
                if (depth > top) top = depth;
            }
            return top;
        }

        private static float ResolveStackAlpha(float now)
        {
            if (_stackHeldOpen)
            {
                _stackLastChangeAt = now;
                return 1f;
            }
            float inactiveFor = Mathf.Max(0f, now - _stackLastChangeAt);
            float progress = Mathf.Clamp01((inactiveFor - StackInactivityDelay) / StackFadeDuration);
            return 1f - Mathf.SmoothStep(0f, 1f, progress);
        }

        private static bool IsStackPresentable()
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null || hud.hudHidden) return false;
            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null) return true;
            return !player.inTerminalMenu
                   && (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen);
        }

        private static void TryApplyStackFont()
        {
            TMP_FontAsset font = HUDManager.Instance != null && HUDManager.Instance.clockNumber != null
                ? HUDManager.Instance.clockNumber.font
                : null;
            if (font == null || _stackRows == null) return;
            for (int i = 0; i < _stackRows.Length; i++)
                if (_stackRows[i] != null) _stackRows[i].font = font;
            _stackFontApplied = true;
            _stackLayoutDirty = true;
        }

        /// <summary>
        /// The column is authored in 1080-referenced units scaled by screen height; the vanilla
        /// canvas applies its own scale factor, so the root carries the ratio of the two.
        /// </summary>
        private static void RefreshStackScale()
        {
            if (_stackRoot == null || _stackCanvas == null) return;
            float reference = Mathf.Max(0.001f, Screen.height / ReferenceHeight);
            float ratio = reference / Mathf.Max(0.001f, _stackCanvas.scaleFactor);
            if (Mathf.Abs(ratio - _stackScaleRatio) <= 0.0005f) return;
            _stackScaleRatio = ratio;
            _stackRoot.localScale = new Vector3(ratio, ratio, 1f);
        }

        /// <summary>
        /// Keeps the host immediately before the pause menu's top-level node so the menu covers
        /// it; until the quick menu resolves, just after the in-game HUD container.
        /// </summary>
        private static void KeepStackBeneathPauseMenu()
        {
            if (_stackHost == null || _stackCanvas == null) return;
            Transform host = _stackHost.transform;
            if (_stackPauseMenuAnchor == null)
            {
                PlayerControllerB player = GameNetworkManager.Instance != null
                    ? GameNetworkManager.Instance.localPlayerController
                    : null;
                GameObject menu = player != null && player.quickMenuManager != null
                    ? player.quickMenuManager.menuContainer
                    : null;
                _stackPauseMenuAnchor = menu != null
                    ? CanvasLevelAncestor(menu.transform, _stackCanvas.transform)
                    : null;
            }

            if (_stackPauseMenuAnchor != null)
            {
                int anchorIndex = _stackPauseMenuAnchor.GetSiblingIndex();
                if (host.GetSiblingIndex() > anchorIndex) host.SetSiblingIndex(anchorIndex);
                return;
            }

            GameObject hudContainer = HUDManager.Instance != null ? HUDManager.Instance.HUDContainer : null;
            Transform hudAnchor = hudContainer != null
                ? CanvasLevelAncestor(hudContainer.transform, _stackCanvas.transform)
                : null;
            if (hudAnchor != null && host.GetSiblingIndex() > hudAnchor.GetSiblingIndex() + 1)
                host.SetSiblingIndex(hudAnchor.GetSiblingIndex() + 1);
        }

        private static Canvas ResolveHudCanvas()
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null) return null;
            if (hud.playerScreenTexture != null && hud.playerScreenTexture.canvas != null)
                return hud.playerScreenTexture.canvas;
            return hud.HUDContainer != null ? hud.HUDContainer.GetComponentInParent<Canvas>() : null;
        }

        private static Transform CanvasLevelAncestor(Transform node, Transform canvasRoot)
        {
            while (node != null && node.parent != canvasRoot)
                node = node.parent;
            return node;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(.5f, .5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
        }

        private static Sprite WhiteFillSprite
        {
            get
            {
                if (_whiteFillSprite != null) return _whiteFillSprite;
                Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                texture.SetPixel(0, 0, Color.white);
                texture.Apply();
                _whiteFillSprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(.5f, .5f));
                return _whiteFillSprite;
            }
        }

        private static Color WithAlpha(Color color, float alpha) { color.a = alpha; return color; }

        /// <summary>Drives the stack's fade and visibility from its own host, not from producers.</summary>
        private sealed class PromptStackDriver : MonoBehaviour
        {
            private void Update()
            {
                TickStack();
            }
        }
    }
}
