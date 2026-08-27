using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.UITheme;
using Y4NGZUpgrades.Gui;

namespace Y4NGZUpgrades.HUD
{
    internal static class UpgradeHUDManager
    {
        private static AssetBundle _bundle;
        private static bool _bundleLoadAttempted;
        private static bool _bundleLoadFailed;

        private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, GameObject> _hudElements = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, Image> _hudImages = new Dictionary<string, Image>();
        private static readonly Dictionary<string, Image> _fillBars = new Dictionary<string, Image>();
        private static readonly Dictionary<string, TextMeshProUGUI> _labelTexts = new Dictionary<string, TextMeshProUGUI>();
        private static readonly Dictionary<string, int> _stackPositions = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> _stackHeights = new Dictionary<string, float>();
        private static readonly List<string> _stackOrder = new List<string>();
        private static Sprite _whiteFillSprite;
        private static TMP_FontAsset _hudFont;

        private const float ELEMENT_WIDTH = 105f;
        private const float ELEMENT_HEIGHT = 32f;
        private const float LARGE_ELEMENT_WIDTH = 190f;
        private const float LARGE_ELEMENT_HEIGHT = 58f;
        private const float RIGHT_MARGIN = 10f;
        private const float BOTTOM_MARGIN = 10f;
        private const float STACK_GAP = 3f;

        private const string BUNDLE_NAME = "Y4NGZUpgrades.lethalbundle";
        private static readonly bool LOG_BUNDLE_CONTENTS = false;

        public static void LoadBundle()
        {
            if (_bundleLoadAttempted) return;
            _bundleLoadAttempted = true;

            // LethalLevelLoader auto-loads any .lethalbundle in plugin folders.
            // Unity won't load the same bundle twice, so search loaded bundles first.
            foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
            {
                // LoadAsset on a streamed scene bundle throws (BetterArmory-public #1), and after a
                // lobby cycle LethalLevelLoader keeps moon scene bundles in this list.
                if (ab == null || ab.isStreamedSceneAssetBundle) continue;
                Sprite probe;
                try
                {
                    probe = ab.LoadAsset<Sprite>("shadow_hud");
                }
                catch (System.Exception)
                {
                    // Foreign bundle in an unknown state — a probe miss, not an error.
                    continue;
                }
                if (probe != null)
                {
                    _bundle = ab;
                    Plugin.Log?.LogInfo($"[Y4NGZUpgrades] Found preloaded HUD bundle: {ab.name}");
                    break;
                }
            }

            if (_bundle == null)
            {
                string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string bundlePath = Path.Combine(dllDir, BUNDLE_NAME);

                if (!File.Exists(bundlePath))
                {
                    Plugin.Log?.LogWarning($"[Y4NGZUpgrades] HUD bundle not found at {bundlePath}");
                    _bundleLoadFailed = true;
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    Plugin.Log?.LogError($"[Y4NGZUpgrades] Failed to load HUD bundle at {bundlePath}");
                    _bundleLoadFailed = true;
                    return;
                }
            }

            // Diagnostic - log every asset name in the bundle so we can confirm the
            // owner-authored sprite name (e.g. for Bait Bomb).
            if (LOG_BUNDLE_CONTENTS)
            {
                try
                {
                    string[] all = _bundle.GetAllAssetNames();
                    Plugin.Log?.LogInfo($"[Y4NGZUpgrades] Bundle '{_bundle.name}' contains {all.Length} assets:");
                    for (int i = 0; i < all.Length; i++)
                        Plugin.Log?.LogInfo($"[Y4NGZUpgrades]   asset[{i}] = {all[i]}");
                }
                catch { /* non-fatal */ }
            }

            CacheSprite("shadow_hud");
            CacheSprite("escape_hud");
            // Bait Bomb - cache the legacy pipe-bomb sprite names; whichever the owner
            // shipped will land in the cache, the others log a warning.
            CacheSprite("pipebomb_hud");
            CacheSprite("pipe_hud");
            CacheSprite("pipe_bomb_hud");

            Plugin.Log?.LogInfo("[Y4NGZUpgrades] HUD bundle loaded successfully.");
        }

        /// <summary>
        /// Look up a cached sprite by name. Returns null if not loaded.
        /// </summary>
        public static Sprite GetCachedSprite(string spriteName)
        {
            _spriteCache.TryGetValue(spriteName, out Sprite s);
            return s;
        }

        /// <summary>
        /// Resolve the first sprite name that is cached. Lets callers express
        /// "use whichever of these the bundle actually has".
        /// </summary>
        public static string ResolveSpriteName(params string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                if (_spriteCache.ContainsKey(candidates[i])) return candidates[i];
            }
            return null;
        }

        private static Sprite GetWhiteFillSprite()
        {
            if (_whiteFillSprite != null) return _whiteFillSprite;
            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _whiteFillSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            return _whiteFillSprite;
        }

        private static TMP_FontAsset ResolveHudFont()
        {
            if (_hudFont != null)
                return _hudFont;

            HUDManager hud = HUDManager.Instance;
            if (hud != null)
            {
                if (hud.clockNumber != null && hud.clockNumber.font != null)
                {
                    _hudFont = hud.clockNumber.font;
                    return _hudFont;
                }

                TMP_Text source = hud.GetComponentInChildren<TMP_Text>();
                if (source != null && source.font != null)
                {
                    _hudFont = source.font;
                    return _hudFont;
                }
            }

            TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < fonts.Length; i++)
            {
                TMP_FontAsset font = fonts[i];
                if (font == null || string.IsNullOrEmpty(font.name))
                    continue;

                if (font.name.Contains("3270") || font.name.Contains("edunline") || font.name.Contains("EdgeOf"))
                {
                    _hudFont = font;
                    return _hudFont;
                }
            }

            TMP_Text anyText = Object.FindObjectOfType<TMP_Text>();
            if (anyText != null && anyText.font != null)
                _hudFont = anyText.font;

            return _hudFont;
        }

        private static void ApplyHudTextStyle(
            TextMeshProUGUI label,
            float fontSize,
            float fontSizeMin,
            float fontSizeMax,
            TextAlignmentOptions alignment,
            Color color)
        {
            if (label == null)
                return;

            TMP_FontAsset font = ResolveHudFont();
            if (font != null && label.font != font)
                label.font = font;

            label.fontSize = fontSize;
            label.enableAutoSizing = true;
            label.fontSizeMin = fontSizeMin;
            label.fontSizeMax = fontSizeMax;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.alignment = alignment;
            label.color = color;
            label.fontStyle = FontStyles.Normal;
            label.characterSpacing = 0f;
            label.lineSpacing = 0f;
            label.extraPadding = true;
            label.raycastTarget = false;
        }

        private static void CacheSprite(string name)
        {
            if (_bundle == null) return;
            Sprite sprite = _bundle.LoadAsset<Sprite>(name);
            if (sprite != null)
                _spriteCache[name] = sprite;
            else if (LOG_BUNDLE_CONTENTS)
                Plugin.Log?.LogWarning($"[Y4NGZUpgrades] Sprite '{name}' not found in bundle.");
        }

        private static Sprite LoadHudSprite(string spriteName)
        {
            if (string.IsNullOrWhiteSpace(spriteName))
                return null;

            string safeName = spriteName.Trim().ToLowerInvariant().Replace(' ', '_');
            if (_spriteCache.TryGetValue(safeName, out Sprite cached) && cached != null)
                return cached;

            LoadBundle();
            if (_bundle != null)
            {
                Sprite bundled = _bundle.LoadAsset<Sprite>(safeName);
                if (bundled != null)
                {
                    _spriteCache[safeName] = bundled;
                    return bundled;
                }
            }

            string path = FindHudSpritePath(safeName);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes))
                {
                    Object.Destroy(texture);
                    return null;
                }

                texture.name = Path.GetFileNameWithoutExtension(path);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                _spriteCache[safeName] = sprite;
                return sprite;
            }
            catch
            {
                return null;
            }
        }

        public static GameObject CreateHUDElement(string spriteName, int stackPosition, Transform parentCanvas)
        {
            if (_bundleLoadFailed || parentCanvas == null) return null;
            LoadBundle();
            if (!_spriteCache.TryGetValue(spriteName, out Sprite sprite)) return null;

            if (_hudElements.TryGetValue(spriteName, out GameObject existing) && existing != null)
                Object.Destroy(existing);

            GameObject element = new GameObject($"UpgradeHUD_{spriteName}");
            element.transform.SetParent(parentCanvas, worldPositionStays: false);

            RectTransform rt = element.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.sizeDelta = new Vector2(ELEMENT_WIDTH, ELEMENT_HEIGHT);
            rt.anchoredPosition = new Vector2(-RIGHT_MARGIN, BOTTOM_MARGIN);

            Image img = element.AddComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Simple;
            img.preserveAspect = true;
            img.raycastTarget = false;

            GameObject fillGo = new GameObject("CooldownFill");
            fillGo.transform.SetParent(element.transform, worldPositionStays: false);
            RectTransform fillRt = fillGo.AddComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            Image fillImg = fillGo.AddComponent<Image>();
            fillImg.sprite = GetWhiteFillSprite();
            fillImg.color = new Color(0f, 0.8f, 0f, 0.25f);
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImg.fillAmount = 0f;
            fillImg.raycastTarget = false;
            fillGo.SetActive(false);

            _hudElements[spriteName] = element;
            _hudImages[spriteName] = img;
            _fillBars[spriteName] = fillImg;
            RegisterStackElement(spriteName, stackPosition, ELEMENT_HEIGHT);

            if (element.GetComponent<UpgradeHUDStackDriver>() == null)
                element.AddComponent<UpgradeHUDStackDriver>();

            RefreshStackLayout();
            RefreshTheme();

            return element;
        }

        public static GameObject CreateIconKeyHUDElement(
            string elementKey,
            string iconKey,
            string labelText,
            int stackPosition,
            Transform parentCanvas)
        {
            if (parentCanvas == null || string.IsNullOrWhiteSpace(elementKey))
                return null;

            if (_hudElements.TryGetValue(elementKey, out GameObject existing) && existing != null)
            {
                SetHUDElementLabel(elementKey, labelText);
                return existing;
            }

            Sprite icon = LoadUpgradeIconSprite(iconKey);

            GameObject element = new GameObject($"UpgradeHUD_{elementKey}");
            element.transform.SetParent(parentCanvas, worldPositionStays: false);

            RectTransform rt = element.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.sizeDelta = new Vector2(ELEMENT_WIDTH, ELEMENT_HEIGHT);
            rt.anchoredPosition = new Vector2(-RIGHT_MARGIN, BOTTOM_MARGIN);

            Image bg = element.AddComponent<Image>();
            bg.sprite = GetWhiteFillSprite();
            bg.color = new Color(0f, 0.32f, 0f, 0.58f);
            bg.raycastTarget = false;

            AddBorder(element.transform, new Color(0.65f, 1f, 0.12f, 0.90f));

            GameObject iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(element.transform, false);
            RectTransform iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0f, 0.5f);
            iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.sizeDelta = new Vector2(24f, 24f);
            iconRt.anchoredPosition = new Vector2(18f, 0f);
            Image iconImg = iconGo.AddComponent<Image>();
            iconImg.sprite = icon != null ? icon : GetWhiteFillSprite();
            iconImg.preserveAspect = true;
            iconImg.color = icon != null ? Color.white : new Color(232f / 255f, 100f / 255f, 42f / 255f, 1f);
            iconImg.raycastTarget = false;

            GameObject divider = new GameObject("Divider");
            divider.transform.SetParent(element.transform, false);
            RectTransform dividerRt = divider.AddComponent<RectTransform>();
            dividerRt.anchorMin = new Vector2(0f, 0f);
            dividerRt.anchorMax = new Vector2(0f, 1f);
            dividerRt.pivot = new Vector2(0.5f, 0.5f);
            dividerRt.offsetMin = new Vector2(38f, 4f);
            dividerRt.offsetMax = new Vector2(40f, -4f);
            Image dividerImg = divider.AddComponent<Image>();
            dividerImg.sprite = GetWhiteFillSprite();
            dividerImg.color = new Color(0.65f, 1f, 0.12f, 0.70f);
            dividerImg.raycastTarget = false;

            TextMeshProUGUI label = new GameObject("Label").AddComponent<TextMeshProUGUI>();
            label.transform.SetParent(element.transform, false);
            label.text = labelText ?? "";
            label.fontSize = 9f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 6f;
            label.fontSizeMax = 9f;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = new Color(1f, 0.45f, 0.08f, 1f);
            label.raycastTarget = false;
            ApplyHudTextStyle(label, 9f, 6f, 9f, TextAlignmentOptions.MidlineLeft, new Color(1f, 0.45f, 0.08f, 1f));
            RectTransform labelRt = label.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.offsetMin = new Vector2(46f, 1f);
            labelRt.offsetMax = new Vector2(-4f, -1f);

            GameObject fillGo = new GameObject("CooldownFill");
            fillGo.transform.SetParent(element.transform, worldPositionStays: false);
            RectTransform fillRt = fillGo.AddComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            Image fillImg = fillGo.AddComponent<Image>();
            fillImg.sprite = GetWhiteFillSprite();
            fillImg.color = new Color(0f, 0.8f, 0f, 0.24f);
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImg.fillAmount = 0f;
            fillImg.raycastTarget = false;
            fillGo.SetActive(false);

            _hudElements[elementKey] = element;
            _hudImages[elementKey] = bg;
            _fillBars[elementKey] = fillImg;
            _labelTexts[elementKey] = label;
            RegisterStackElement(elementKey, stackPosition, ELEMENT_HEIGHT);

            if (element.GetComponent<UpgradeHUDStackDriver>() == null)
                element.AddComponent<UpgradeHUDStackDriver>();

            RefreshStackLayout();
            RefreshTheme();
            return element;
        }

        public static GameObject CreateLargeIconKeyHUDElement(
            string elementKey,
            string iconKey,
            string labelText,
            int stackPosition,
            Transform parentCanvas)
        {
            if (parentCanvas == null || string.IsNullOrWhiteSpace(elementKey))
                return null;

            if (_hudElements.TryGetValue(elementKey, out GameObject existing) && existing != null)
            {
                SetHUDElementLabel(elementKey, labelText);
                return existing;
            }

            Sprite icon = LoadUpgradeIconSprite(iconKey);

            GameObject element = new GameObject($"UpgradeHUD_{elementKey}");
            element.transform.SetParent(parentCanvas, worldPositionStays: false);

            RectTransform rt = element.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.sizeDelta = new Vector2(LARGE_ELEMENT_WIDTH, LARGE_ELEMENT_HEIGHT);
            rt.anchoredPosition = new Vector2(-RIGHT_MARGIN, BOTTOM_MARGIN);

            Image bg = element.AddComponent<Image>();
            bg.sprite = GetWhiteFillSprite();
            bg.color = new Color(0f, 0.32f, 0f, 0.58f);
            bg.raycastTarget = false;

            AddBorder(element.transform, new Color(0.65f, 1f, 0.12f, 0.90f));

            GameObject iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(element.transform, false);
            RectTransform iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0f, 0.5f);
            iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.sizeDelta = new Vector2(30f, 30f);
            iconRt.anchoredPosition = new Vector2(22f, 0f);
            Image iconImg = iconGo.AddComponent<Image>();
            iconImg.sprite = icon != null ? icon : GetWhiteFillSprite();
            iconImg.preserveAspect = true;
            iconImg.color = icon != null ? Color.white : new Color(232f / 255f, 100f / 255f, 42f / 255f, 1f);
            iconImg.raycastTarget = false;

            GameObject divider = new GameObject("Divider");
            divider.transform.SetParent(element.transform, false);
            RectTransform dividerRt = divider.AddComponent<RectTransform>();
            dividerRt.anchorMin = new Vector2(0f, 0f);
            dividerRt.anchorMax = new Vector2(0f, 1f);
            dividerRt.pivot = new Vector2(0.5f, 0.5f);
            dividerRt.offsetMin = new Vector2(48f, 5f);
            dividerRt.offsetMax = new Vector2(50f, -5f);
            Image dividerImg = divider.AddComponent<Image>();
            dividerImg.sprite = GetWhiteFillSprite();
            dividerImg.color = new Color(0.65f, 1f, 0.12f, 0.70f);
            dividerImg.raycastTarget = false;

            TextMeshProUGUI label = new GameObject("Label").AddComponent<TextMeshProUGUI>();
            label.transform.SetParent(element.transform, false);
            label.text = labelText ?? "";
            label.fontSize = 8.5f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 5.8f;
            label.fontSizeMax = 8.5f;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.alignment = TextAlignmentOptions.Left;
            label.color = new Color(1f, 0.55f, 0.12f, 1f);
            label.raycastTarget = false;
            ApplyHudTextStyle(label, 8.5f, 5.8f, 8.5f, TextAlignmentOptions.Left, new Color(1f, 0.55f, 0.12f, 1f));
            RectTransform labelRt = label.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.offsetMin = new Vector2(58f, 5f);
            labelRt.offsetMax = new Vector2(-6f, -5f);

            GameObject fillGo = new GameObject("CooldownFill");
            fillGo.transform.SetParent(element.transform, worldPositionStays: false);
            RectTransform fillRt = fillGo.AddComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            Image fillImg = fillGo.AddComponent<Image>();
            fillImg.sprite = GetWhiteFillSprite();
            fillImg.color = new Color(0f, 0.8f, 0f, 0.24f);
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImg.fillAmount = 0f;
            fillImg.raycastTarget = false;
            fillGo.SetActive(false);

            _hudElements[elementKey] = element;
            _hudImages[elementKey] = bg;
            _fillBars[elementKey] = fillImg;
            _labelTexts[elementKey] = label;
            RegisterStackElement(elementKey, stackPosition, LARGE_ELEMENT_HEIGHT);

            if (element.GetComponent<UpgradeHUDStackDriver>() == null)
                element.AddComponent<UpgradeHUDStackDriver>();

            RefreshStackLayout();
            RefreshTheme();
            return element;
        }

        public static GameObject CreateLargeSpriteHUDElement(
            string elementKey,
            string spriteName,
            string labelText,
            int stackPosition,
            Transform parentCanvas)
        {
            if (parentCanvas == null || string.IsNullOrWhiteSpace(elementKey))
                return null;

            if (_hudElements.TryGetValue(elementKey, out GameObject existing) && existing != null)
            {
                SetHUDElementLabel(elementKey, labelText);
                return existing;
            }

            Sprite background = LoadHudSprite(spriteName);
            if (background == null)
                return null;

            GameObject element = new GameObject($"UpgradeHUD_{elementKey}");
            element.transform.SetParent(parentCanvas, worldPositionStays: false);

            RectTransform rt = element.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.sizeDelta = new Vector2(LARGE_ELEMENT_WIDTH, LARGE_ELEMENT_HEIGHT);
            rt.anchoredPosition = new Vector2(-RIGHT_MARGIN, BOTTOM_MARGIN);

            Image bg = element.AddComponent<Image>();
            bg.sprite = background;
            bg.type = Image.Type.Simple;
            bg.preserveAspect = true;
            bg.color = Color.white;
            bg.raycastTarget = false;

            TextMeshProUGUI label = new GameObject("Label").AddComponent<TextMeshProUGUI>();
            label.transform.SetParent(element.transform, false);
            label.text = labelText ?? "";
            label.fontSize = 8.2f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 5.6f;
            label.fontSizeMax = 8.2f;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.alignment = TextAlignmentOptions.Left;
            label.color = new Color(232f / 255f, 100f / 255f, 42f / 255f, 1f);
            label.raycastTarget = false;
            ApplyHudTextStyle(label, 8.2f, 5.6f, 8.2f, TextAlignmentOptions.Left, new Color(232f / 255f, 100f / 255f, 42f / 255f, 1f));
            RectTransform labelRt = label.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.offsetMin = new Vector2(58f, 5f);
            labelRt.offsetMax = new Vector2(-7f, -5f);

            GameObject fillGo = new GameObject("CooldownFill");
            fillGo.transform.SetParent(element.transform, worldPositionStays: false);
            RectTransform fillRt = fillGo.AddComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            Image fillImg = fillGo.AddComponent<Image>();
            fillImg.sprite = GetWhiteFillSprite();
            fillImg.color = new Color(1f, 0.52f, 0.12f, 0.18f);
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImg.fillAmount = 0f;
            fillImg.raycastTarget = false;
            fillGo.SetActive(false);

            _hudElements[elementKey] = element;
            _hudImages[elementKey] = bg;
            _fillBars[elementKey] = fillImg;
            _labelTexts[elementKey] = label;
            RegisterStackElement(elementKey, stackPosition, LARGE_ELEMENT_HEIGHT);

            if (element.GetComponent<UpgradeHUDStackDriver>() == null)
                element.AddComponent<UpgradeHUDStackDriver>();

            RefreshStackLayout();
            RefreshTheme();
            return element;
        }

        internal static void RefreshTheme()
        {
            Color accent = UiTheme.Accent;
            Color panel = UiTheme.Panel;
            Color faint = UiTheme.Faint;
            Sprite whiteFill = GetWhiteFillSprite();

            foreach (TextMeshProUGUI label in _labelTexts.Values)
                TintPreservingAlpha(label, accent);

            foreach (Image fill in _fillBars.Values)
                TintPreservingAlpha(fill, faint);

            foreach (GameObject element in _hudElements.Values)
            {
                if (element == null)
                    continue;

                Image[] images = element.GetComponentsInChildren<Image>(true);
                for (int i = 0; i < images.Length; i++)
                {
                    Image image = images[i];
                    if (image == null || _fillBars.ContainsValue(image))
                        continue;

                    string name = image.gameObject.name;
                    if (name.StartsWith("Border") || name == "Divider")
                    {
                        TintPreservingAlpha(image, accent);
                    }
                    else if (image.gameObject == element && image.sprite == whiteFill)
                    {
                        TintPreservingAlpha(image, panel);
                    }
                    else if (name == "Icon" && image.sprite == whiteFill)
                    {
                        TintPreservingAlpha(image, accent);
                    }
                }
            }
        }

        private static void TintPreservingAlpha(Graphic graphic, Color color)
        {
            if (graphic == null)
                return;

            color.a = graphic.color.a;
            graphic.color = color;
        }

        public static void SetHUDElementLabel(string elementKey, string labelText)
        {
            if (_labelTexts.TryGetValue(elementKey, out TextMeshProUGUI label) && label != null)
                label.text = labelText ?? "";
        }

        private static void RegisterStackElement(string spriteName, int stackPosition, float elementHeight)
        {
            if (!_stackOrder.Contains(spriteName))
                _stackOrder.Add(spriteName);
            _stackPositions[spriteName] = stackPosition;
            _stackHeights[spriteName] = Mathf.Max(ELEMENT_HEIGHT, elementHeight);
            _stackOrder.Sort((a, b) =>
            {
                int ap = _stackPositions.TryGetValue(a, out int av) ? av : 0;
                int bp = _stackPositions.TryGetValue(b, out int bv) ? bv : 0;
                int cmp = ap.CompareTo(bp);
                return cmp != 0 ? cmp : string.CompareOrdinal(a, b);
            });
        }

        internal static void RefreshStackLayout()
        {
            float y = BOTTOM_MARGIN;
            for (int i = 0; i < _stackOrder.Count; i++)
            {
                string spriteName = _stackOrder[i];
                if (!_hudElements.TryGetValue(spriteName, out GameObject go) || go == null) continue;
                if (!go.activeSelf) continue;

                RectTransform rt = go.GetComponent<RectTransform>();
                if (rt == null) continue;
                rt.anchoredPosition = new Vector2(-RIGHT_MARGIN, y);
                float height = _stackHeights.TryGetValue(spriteName, out float value) ? value : ELEMENT_HEIGHT;
                y += height + STACK_GAP;
            }
        }

        public static void SetCooldownActive(string spriteName, bool active)
        {
            if (_hudImages.TryGetValue(spriteName, out Image img) && img != null)
            {
                Color c = img.color;
                c.a = active ? 0.35f : 1f;
                img.color = c;
            }

            if (_fillBars.TryGetValue(spriteName, out Image fill) && fill != null)
            {
                fill.gameObject.SetActive(active);
            }
        }

        public static void UpdateFillBar(string spriteName, float normalizedTimeRemaining)
        {
            if (_fillBars.TryGetValue(spriteName, out Image fill) && fill != null)
                fill.fillAmount = Mathf.Clamp01(normalizedTimeRemaining);
        }

        public static GameObject GetHUDElement(string spriteName)
        {
            _hudElements.TryGetValue(spriteName, out GameObject go);
            return go;
        }

        public static void DestroyHUDElement(string spriteName)
        {
            if (_hudElements.TryGetValue(spriteName, out GameObject go) && go != null)
                Object.Destroy(go);
            _hudElements.Remove(spriteName);
            _hudImages.Remove(spriteName);
            _fillBars.Remove(spriteName);
            _labelTexts.Remove(spriteName);
            _stackOrder.Remove(spriteName);
            _stackPositions.Remove(spriteName);
            _stackHeights.Remove(spriteName);
            RefreshStackLayout();
        }

        public static void DestroyAllHUDElements()
        {
            foreach (var kvp in _hudElements)
            {
                if (kvp.Value != null) Object.Destroy(kvp.Value);
            }
            _hudElements.Clear();
            _hudImages.Clear();
            _fillBars.Clear();
            _labelTexts.Clear();
            _stackOrder.Clear();
            _stackPositions.Clear();
            _stackHeights.Clear();
        }

        // Shared with the purchase menu so the HUD shows the same whitened glyph
        // under the same key normalisation. HUD callers keep applying their own
        // Image color on top of the neutral white icon.
        public static Sprite LoadUpgradeIconSprite(string iconKey)
        {
            return UpgradeIconLoader.LoadUpgradeIconSprite(iconKey);
        }

        private static string FindHudSpritePath(string spriteName)
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string rootDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string fileName = spriteName + ".png";
            string[] candidates =
            {
                Path.Combine(assemblyDir ?? "", "Assets", "UI", "HUD", fileName),
                Path.Combine(assemblyDir ?? "", "InteractiveAssets", "UI", "HUD", fileName),
                Path.Combine(rootDir, "BepInEx", "plugins", "y4ngz-Y4NGZUpgrades", "Assets", "UI", "HUD", fileName),
                Path.Combine(rootDir, "BepInEx", "plugins", "Y4NGZUpgrades", "Assets", "UI", "HUD", fileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static void AddBorder(Transform parent, Color color)
        {
            AddLine(parent, "Top", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -2), Vector2.zero, color);
            AddLine(parent, "Bottom", Vector2.zero, new Vector2(1, 0), Vector2.zero, new Vector2(0, 2), color);
            AddLine(parent, "Left", Vector2.zero, new Vector2(0, 1), Vector2.zero, new Vector2(2, 0), color);
            AddLine(parent, "Right", new Vector2(1, 0), Vector2.one, new Vector2(-2, 0), Vector2.zero, color);
        }

        private static void AddLine(
            Transform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            Color color)
        {
            GameObject line = new GameObject("Border" + name);
            line.transform.SetParent(parent, false);
            RectTransform rt = line.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            Image img = line.AddComponent<Image>();
            img.sprite = GetWhiteFillSprite();
            img.color = color;
            img.raycastTarget = false;
        }
    }

    internal sealed class UpgradeHUDStackDriver : MonoBehaviour
    {
        private void OnEnable()
        {
            UpgradeHUDManager.RefreshStackLayout();
        }

        private void OnDisable()
        {
            UpgradeHUDManager.RefreshStackLayout();
        }
    }
}
