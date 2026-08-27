using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.UITheme;

namespace Y4NGZUpgrades.Interactive.Feedback
{
    internal sealed class DirectionalDamageHud : MonoBehaviour
    {
        private const float Duration = 0.95f;

        private static DirectionalDamageHud _instance;
        private static Sprite _arcSprite;

        private RectTransform _root;
        private RectTransform _arcTransform;
        private Image _arcImage;
        private float _shownAt = -999f;
        private float _damageScale = 1f;

        public static void ShowFromWorldPosition(PlayerControllerB player, Vector3 sourcePosition, int damageNumber)
        {
            if (!GameplayUiVisibility.IsVisible
                || Plugin.DangerFeedbackEnabled?.Value == false
                || Plugin.DangerFeedbackDirectionalDamageEnabled?.Value == false)
                return;
            if (player == null || player.gameplayCamera == null) return;

            DirectionalDamageHud hud = Ensure();
            if (hud == null) return;

            hud.PositionArrow(player, sourcePosition, damageNumber);
            hud._shownAt = Time.unscaledTime;
            hud.gameObject.SetActive(true);
            hud.UpdateVisual();
        }

        public static void DestroyInstance()
        {
            if (_instance == null) return;
            Object.Destroy(_instance.gameObject);
            _instance = null;
        }

        internal static void SetPresentationVisible(bool visible)
        {
            if (_instance == null || visible)
                return;

            _instance._shownAt = -999f;
            if (_instance._arcImage != null)
                _instance._arcImage.color = new Color(1f, 0.08f, 0.02f, 0f);
            _instance.gameObject.SetActive(false);
        }

        private static DirectionalDamageHud Ensure()
        {
            if (_instance != null) return _instance;
            if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null)
                return null;

            Canvas canvas = HUDManager.Instance.playerScreenTexture.canvas;
            if (canvas == null) return null;

            GameObject go = new GameObject("Y4NGZ_DirectionalDamageHud");
            go.transform.SetParent(canvas.transform, worldPositionStays: false);

            DirectionalDamageHud hud = go.AddComponent<DirectionalDamageHud>();
            hud.Build();
            _instance = hud;
            return hud;
        }

        private void Build()
        {
            _root = gameObject.AddComponent<RectTransform>();
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;

            GameObject arcGo = new GameObject("DamageDirectionArc");
            arcGo.transform.SetParent(transform, worldPositionStays: false);
            _arcTransform = arcGo.AddComponent<RectTransform>();
            _arcTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _arcTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _arcTransform.pivot = new Vector2(0.5f, 0.5f);
            _arcTransform.sizeDelta = new Vector2(132f, 54f);

            _arcImage = arcGo.AddComponent<Image>();
            _arcImage.sprite = GetArcSprite();
            _arcImage.raycastTarget = false;
            _arcImage.color = new Color(1f, 0.08f, 0.02f, 0f);

            gameObject.SetActive(false);
        }

        private void PositionArrow(PlayerControllerB player, Vector3 sourcePosition, int damageNumber)
        {
            Transform cameraTransform = player.gameplayCamera.transform;
            Vector3 toSource = sourcePosition - player.transform.position;
            toSource.y = 0f;

            Vector3 forward = cameraTransform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                forward = player.transform.forward;
            forward.Normalize();

            Vector3 right = cameraTransform.right;
            right.y = 0f;
            if (right.sqrMagnitude < 0.001f)
                right = player.transform.right;
            right.Normalize();

            Vector2 dir;
            if (toSource.sqrMagnitude < 0.001f)
            {
                dir = Vector2.down;
            }
            else
            {
                toSource.Normalize();
                dir = new Vector2(Vector3.Dot(right, toSource), Vector3.Dot(forward, toSource));
                if (dir.sqrMagnitude < 0.001f)
                    dir = Vector2.down;
                else
                    dir.Normalize();
            }

            float radius = 270f;
            if (_root != null && _root.rect.width > 0f && _root.rect.height > 0f)
                radius = Mathf.Min(_root.rect.width, _root.rect.height) * 0.36f;

            _arcTransform.anchoredPosition = dir * radius;
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            _arcTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
            _damageScale = Mathf.Lerp(0.9f, 1.25f, Mathf.Clamp01(damageNumber / 80f));
        }

        private void Update()
        {
            UpdateVisual();
        }

        private void UpdateVisual()
        {
            if (_arcImage == null) return;
            if (!GameplayUiVisibility.IsVisible)
            {
                SetPresentationVisible(false);
                return;
            }

            transform.SetAsLastSibling();

            float elapsed = Time.unscaledTime - _shownAt;
            if (elapsed >= Duration)
            {
                _arcImage.color = new Color(1f, 0.08f, 0.02f, 0f);
                gameObject.SetActive(false);
                return;
            }

            float t = Mathf.Clamp01(elapsed / Duration);
            float alpha = Mathf.Lerp(0.58f, 0f, t * t);
            _arcImage.color = new Color(1f, 0.12f, 0.04f, alpha);
            _arcTransform.localScale = Vector3.one * Mathf.Lerp(_damageScale * 1.12f, _damageScale, t);
        }

        private static Sprite GetArcSprite()
        {
            if (_arcSprite != null) return _arcSprite;

            const int width = 192;
            const int height = 96;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            Color clear = new Color(1f, 1f, 1f, 0f);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    tex.SetPixel(x, y, clear);
            }

            Vector2 center = new Vector2(width * 0.5f, -height * 0.7f);
            float radius = height * 1.42f;
            float thickness = 12f;
            float halfArcRadians = 0.46f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    Vector2 fromCenter = p - center;
                    float distance = fromCenter.magnitude;
                    float radialDelta = Mathf.Abs(distance - radius);
                    if (radialDelta > thickness) continue;

                    float angle = Mathf.Atan2(fromCenter.y, fromCenter.x);
                    float angleDelta = Mathf.Abs(Mathf.DeltaAngle(angle * Mathf.Rad2Deg, 90f)) * Mathf.Deg2Rad;
                    if (angleDelta > halfArcRadians) continue;

                    float radialAlpha = 1f - Mathf.Clamp01(radialDelta / thickness);
                    float arcAlpha = 1f - Mathf.Clamp01((angleDelta - halfArcRadians * 0.72f) / (halfArcRadians * 0.28f));
                    float alpha = Mathf.Clamp01(radialAlpha * arcAlpha);
                    alpha = alpha * alpha * (3f - 2f * alpha);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply(false, true);
            _arcSprite = Sprite.Create(tex, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
            _arcSprite.hideFlags = HideFlags.HideAndDontSave;
            return _arcSprite;
        }
    }
}
