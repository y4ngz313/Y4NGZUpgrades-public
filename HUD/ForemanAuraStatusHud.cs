using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.HUD
{
    internal static class ForemanAuraStatusHud
    {
        private const float ResolveRetryInterval = 0.75f;
        private const float IconSize = 22f;

        private static GameObject _root;
        private static RectTransform _rootRect;
        private static RectTransform _healthBar;
        private static float _nextResolveAt;
        private static bool _active;
        private static Sprite _fallbackSprite;
        private static Texture2D _fallbackTexture;

        internal static void SetAuraActive(bool active)
        {
            _active = active;
            if (!_active)
            {
                Hide();
                return;
            }

            UpdateActiveState();
        }

        internal static void Destroy()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);

            _root = null;
            _rootRect = null;
            _healthBar = null;
            _nextResolveAt = 0f;
            _active = false;

            if (_fallbackSprite != null)
                UnityEngine.Object.Destroy(_fallbackSprite);
            if (_fallbackTexture != null)
                UnityEngine.Object.Destroy(_fallbackTexture);

            _fallbackSprite = null;
            _fallbackTexture = null;
        }

        private static void UpdateActiveState()
        {
            RectTransform healthBar = ResolveHealthBar();
            if (healthBar == null)
            {
                Hide();
                return;
            }

            EnsureRoot(healthBar);
            if (_root == null || _rootRect == null)
                return;

            if (_rootRect.parent != healthBar)
                _rootRect.SetParent(healthBar, worldPositionStays: false);

            _rootRect.anchorMin = new Vector2(0.5f, 1f);
            _rootRect.anchorMax = new Vector2(0.5f, 1f);
            _rootRect.pivot = new Vector2(0.5f, 0f);
            _rootRect.sizeDelta = new Vector2(IconSize, IconSize);
            _rootRect.anchoredPosition = new Vector2(0f, 4f);
            _root.SetActive(true);
        }

        private static void Hide()
        {
            if (_root != null)
                _root.SetActive(false);
        }

        private static RectTransform ResolveHealthBar()
        {
            if (_healthBar != null)
                return _healthBar;

            if (Time.unscaledTime < _nextResolveAt)
                return null;

            _nextResolveAt = Time.unscaledTime + ResolveRetryInterval;
            _healthBar = FindHealthBar();
            return _healthBar;
        }

        private static RectTransform FindHealthBar()
        {
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(includeInactive: true);
            RectTransform bestPreferred = null;
            int bestPreferredScore = 0;
            RectTransform bestGeneric = null;
            int bestGenericScore = 0;

            for (int canvasIndex = 0; canvasIndex < canvases.Length; canvasIndex++)
            {
                Canvas canvas = canvases[canvasIndex];
                if (canvas == null || canvas.transform == null)
                    continue;

                RectTransform[] rects = canvas.GetComponentsInChildren<RectTransform>(includeInactive: true);
                for (int rectIndex = 0; rectIndex < rects.Length; rectIndex++)
                {
                    RectTransform rect = rects[rectIndex];
                    bool preferred;
                    int score = ScoreHealthBarCandidate(rect, out preferred);
                    if (score <= 0)
                        continue;

                    if (preferred)
                    {
                        if (score <= bestPreferredScore)
                            continue;

                        bestPreferredScore = score;
                        bestPreferred = rect;
                    }
                    else
                    {
                        if (score <= bestGenericScore)
                            continue;

                        bestGenericScore = score;
                        bestGeneric = rect;
                    }
                }
            }

            return bestPreferred != null ? bestPreferred : bestGeneric;
        }

        private static int ScoreHealthBarCandidate(RectTransform rect, out bool preferred)
        {
            preferred = false;
            if (rect == null || string.IsNullOrWhiteSpace(rect.name))
                return 0;

            string name = rect.name.ToLowerInvariant();
            string path = BuildTransformPath(rect.transform).ToLowerInvariant();
            bool pathHasPreferredHud = path.Contains("elad") || path.Contains("customhud");
            bool nameHasHealth = name.Contains("health");
            bool nameHasHealthBar = name.Contains("healthbar") || name.Contains("health_bar");
            bool pathHasHealth = path.Contains("health");
            bool pathHasHealthBar = path.Contains("healthbar") || path.Contains("health_bar");

            preferred = pathHasPreferredHud && (nameHasHealth || pathHasHealth);
            if (!preferred && !nameHasHealthBar)
                return 0;

            int score = preferred ? 100 : 10;
            if (nameHasHealthBar)
                score += 30;
            else if (nameHasHealth)
                score += 12;
            if (pathHasHealthBar)
                score += 8;
            if (rect.GetComponent<Image>() != null || rect.GetComponent<RawImage>() != null)
                score += 2;
            if (rect.gameObject.activeInHierarchy)
                score += 1;

            return score;
        }

        private static string BuildTransformPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            Transform parent = transform.parent;
            int guard = 0;
            while (parent != null && guard++ < 48)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static void EnsureRoot(RectTransform healthBar)
        {
            if (_root != null)
                return;

            Sprite icon = UpgradeHUDManager.LoadUpgradeIconSprite("buddy_system");

            _root = new GameObject("Y4NGZ_ForemanBuddyAuraStatus");
            _root.transform.SetParent(healthBar, worldPositionStays: false);
            _rootRect = _root.AddComponent<RectTransform>();

            Image image = _root.AddComponent<Image>();
            image.sprite = icon != null ? icon : GetFallbackSprite();
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = icon != null ? Color.white : new Color(0.2f, 1f, 0.45f, 0.92f);

            if (icon == null)
                AddFallbackText(_root.transform);
        }

        private static void AddFallbackText(Transform parent)
        {
            TextMeshProUGUI label = new GameObject("Fallback").AddComponent<TextMeshProUGUI>();
            label.transform.SetParent(parent, worldPositionStays: false);
            label.text = "B";
            label.fontSize = 14f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 8f;
            label.fontSizeMax = 14f;
            label.enableWordWrapping = false;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.black;
            label.raycastTarget = false;

            RectTransform rect = label.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Sprite GetFallbackSprite()
        {
            if (_fallbackSprite != null)
                return _fallbackSprite;

            _fallbackTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _fallbackTexture.SetPixel(0, 0, Color.white);
            _fallbackTexture.Apply();
            _fallbackTexture.hideFlags = HideFlags.HideAndDontSave;
            _fallbackSprite = Sprite.Create(_fallbackTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
            _fallbackSprite.hideFlags = HideFlags.HideAndDontSave;
            return _fallbackSprite;
        }
    }
}
