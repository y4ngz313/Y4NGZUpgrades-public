using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.UITheme;

namespace Y4NGZUpgrades.Interactive.Feedback
{
    internal sealed class PlayerDangerHud : MonoBehaviour
    {
        private const int VignetteSize = 256;
        private const float FlashDecay = 2.8f;

        private static PlayerDangerHud _instance;
        private static Texture2D _vignetteTexture;
        private static AudioClip _heartbeatClip;

        private RawImage _vignette;
        private float _damageFlashAlpha;
        private float _nextHeartbeatTime;

        public static void UpdatePlayerState(PlayerControllerB player)
        {
            if (!GameplayUiVisibility.IsVisible || Plugin.DangerFeedbackEnabled?.Value == false)
            {
                HideInstance();
                return;
            }

            PlayerDangerHud hud = Ensure();
            if (hud == null) return;
            hud.UpdateForPlayer(player);
        }

        public static void PulseDamage(int damageNumber)
        {
            if (!GameplayUiVisibility.IsVisible || Plugin.DangerFeedbackEnabled?.Value == false) return;

            PlayerDangerHud hud = Ensure();
            if (hud == null) return;
            hud._damageFlashAlpha = Mathf.Max(
                hud._damageFlashAlpha,
                Mathf.Lerp(0.12f, 0.38f, Mathf.Clamp01(damageNumber / 80f)));
            hud.gameObject.SetActive(true);
        }

        public static void DestroyInstance()
        {
            if (_instance == null) return;
            Object.Destroy(_instance.gameObject);
            _instance = null;
        }

        internal static void SetPresentationVisible(bool visible)
        {
            if (!visible)
                HideInstance();
        }

        private static void HideInstance()
        {
            if (_instance == null) return;
            _instance.gameObject.SetActive(false);
        }

        private static PlayerDangerHud Ensure()
        {
            if (_instance != null) return _instance;
            if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null)
                return null;

            Canvas canvas = HUDManager.Instance.playerScreenTexture.canvas;
            if (canvas == null) return null;

            GameObject go = new GameObject("Y4NGZ_PlayerDangerHud");
            go.transform.SetParent(canvas.transform, worldPositionStays: false);

            PlayerDangerHud hud = go.AddComponent<PlayerDangerHud>();
            hud.Build();
            _instance = hud;
            return hud;
        }

        private void Build()
        {
            RectTransform rt = gameObject.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            _vignette = gameObject.AddComponent<RawImage>();
            _vignette.texture = GetVignetteTexture();
            _vignette.color = new Color(0.9f, 0.02f, 0.01f, 0f);
            _vignette.raycastTarget = false;
            gameObject.SetActive(false);
        }

        private void UpdateForPlayer(PlayerControllerB player)
        {
            if (_vignette == null) return;

            bool playerValid = player != null
                               && player == GameNetworkManager.Instance?.localPlayerController
                               && player.isPlayerControlled
                               && !player.isPlayerDead;
            if (!playerValid)
            {
                _damageFlashAlpha = 0f;
                _vignette.color = new Color(0.9f, 0.02f, 0.01f, 0f);
                gameObject.SetActive(false);
                return;
            }

            int threshold = Mathf.Clamp((Plugin.DangerFeedbackLowHealthThreshold?.Value ?? 35), 1, 99);
            float healthDanger = 0f;
            if (Plugin.DangerFeedbackLowHealthEnabled?.Value != false && player.health <= threshold)
            {
                healthDanger = Mathf.Clamp01((threshold - Mathf.Max(player.health, 1)) / (float)threshold);
            }

            float pulse = healthDanger > 0f
                ? Mathf.Abs(Mathf.Sin(Time.unscaledTime * Mathf.Lerp(2.2f, 4.8f, healthDanger))) * 0.06f
                : 0f;

            _damageFlashAlpha = Mathf.MoveTowards(_damageFlashAlpha, 0f, Time.unscaledDeltaTime * FlashDecay);
            float alpha = Mathf.Clamp01(_damageFlashAlpha + healthDanger * 0.18f + pulse);

            _vignette.color = new Color(0.88f, 0.03f, 0.015f, alpha);
            gameObject.SetActive(alpha > 0.01f);

            if (healthDanger > 0.45f && Plugin.DangerFeedbackHeartbeatEnabled?.Value != false)
                TryPlayHeartbeat(healthDanger);
        }

        private void TryPlayHeartbeat(float danger)
        {
            if (Time.unscaledTime < _nextHeartbeatTime) return;
            _nextHeartbeatTime = Time.unscaledTime + Mathf.Lerp(1.25f, 0.62f, danger);

            if (HUDManager.Instance == null || HUDManager.Instance.UIAudio == null) return;
            HUDManager.Instance.UIAudio.PlayOneShot(GetHeartbeatClip(), Mathf.Lerp(0.16f, 0.34f, danger));
        }

        private static Texture2D GetVignetteTexture()
        {
            if (_vignetteTexture != null) return _vignetteTexture;

            Texture2D tex = new Texture2D(VignetteSize, VignetteSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            float center = (VignetteSize - 1) * 0.5f;
            float inner = center * 0.46f;
            float fade = center - inner;
            Color[] pixels = new Color[VignetteSize * VignetteSize];

            for (int y = 0; y < VignetteSize; y++)
            {
                for (int x = 0; x < VignetteSize; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((dist - inner) / fade);
                    a = a * a;
                    pixels[y * VignetteSize + x] = new Color(1f, 1f, 1f, a);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            _vignetteTexture = tex;
            return _vignetteTexture;
        }

        private static AudioClip GetHeartbeatClip()
        {
            if (_heartbeatClip != null) return _heartbeatClip;

            const int sampleRate = 44100;
            const float duration = 0.18f;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            float[] data = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float first = Mathf.Sin(2f * Mathf.PI * 82f * t) * Mathf.Exp(-t * 42f);
                float secondTime = Mathf.Max(0f, t - 0.08f);
                float second = Mathf.Sin(2f * Mathf.PI * 66f * secondTime) * Mathf.Exp(-secondTime * 55f) * 0.55f;
                data[i] = (first + second) * 0.32f;
            }

            _heartbeatClip = AudioClip.Create("Y4NGZ_LowHealthHeartbeat", sampleCount, 1, sampleRate, false);
            _heartbeatClip.SetData(data, 0);
            _heartbeatClip.hideFlags = HideFlags.HideAndDontSave;
            return _heartbeatClip;
        }
    }
}
