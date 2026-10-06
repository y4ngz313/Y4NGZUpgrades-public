using System;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.HUD
{
    /// <summary>
    /// Resilience's reserve layer uses the stock TopLeftCorner/Self silhouette first verified in
    /// the v81 HUD scene. When LethalHudBasic replaces that presentation, its exact
    /// MinimalHealthFill sprite remains a compatible adapter. Neither path touches SelfRed or
    /// the sibling SprintMeter.
    /// </summary>
    internal static class ResilienceHealthPresentation
    {
        private const string MinimalHealthPrefab = "hud/customui/healthbar/minimal.prefab";
        private const string VanillaSelfName = "Self";
        private const float ResolveRetrySeconds = 0.75f;

        private static Sprite _minimalHealthFillSprite;
        private static Image _liveHealthFill;
        private static Image _reserveFill;
        private static float _nextResolveAt;
        private static bool _missingFillLogged;

        internal static void Tick(PlayerControllerB player, int maxHealth)
        {
            if (player == null || player.isPlayerDead || !player.isPlayerControlled
                || maxHealth <= 100 || player.health <= 100)
            {
                SetVisible(false);
                return;
            }

            if (!EnsureReserveFill())
                return;

            // This is remaining reserve, not damage taken: 160/160 is a full yellow silhouette,
            // 130/160 is half, and it vanishes exactly when vanilla's 100-health range begins.
            _reserveFill.fillAmount = Mathf.Clamp01((player.health - 100f) / (maxHealth - 100f));
            _reserveFill.color = new Color(1f, 0.78f, 0.16f,
                _liveHealthFill.color.a * _liveHealthFill.canvasRenderer.GetAlpha());
            SetVisible(_reserveFill.fillAmount > 0f && _liveHealthFill.isActiveAndEnabled);
        }

        internal static void Restore()
        {
            if (_reserveFill != null)
                UnityEngine.Object.Destroy(_reserveFill.gameObject);
            _reserveFill = null;
            _liveHealthFill = null;
            _minimalHealthFillSprite = null;
            _nextResolveAt = 0f;
            _missingFillLogged = false;
        }

        private static bool EnsureReserveFill()
        {
            if (_reserveFill != null && _liveHealthFill != null && _liveHealthFill.isActiveAndEnabled)
                return true;

            if (Time.unscaledTime < _nextResolveAt)
                return false;
            _nextResolveAt = Time.unscaledTime + ResolveRetrySeconds;
            if (_reserveFill != null) UnityEngine.Object.Destroy(_reserveFill.gameObject);
            _reserveFill = null;

            Canvas canvas = HUDManager.Instance?.playerScreenTexture?.canvas;
            if (canvas == null)
                return false;

            // A profile using LethalHudBasic renders its own compact health display. Keep that
            // established surface when it is actually live, otherwise use stock HUDManager's
            // neutral silhouette rather than guessing from a generic canvas image.
            _liveHealthFill = ResolveLethalHudBasicFill(canvas) ?? ResolveVanillaSelfFill();
            if (_liveHealthFill != null)
            {
                _reserveFill = CreateReserveFill(_liveHealthFill);
                return _reserveFill != null;
            }

            LogMissingFillOnce();
            return false;
        }

        private static Image ResolveLethalHudBasicFill(Canvas canvas)
        {
            if (_minimalHealthFillSprite == null)
                _minimalHealthFillSprite = ResolveMinimalHealthFillSprite();
            if (_minimalHealthFillSprite == null)
                return null;

            Image[] images = canvas.GetComponentsInChildren<Image>(includeInactive: true);
            for (int i = 0; i < images.Length; i++)
            {
                Image candidate = images[i];
                if (candidate != null && candidate.isActiveAndEnabled && candidate.sprite == _minimalHealthFillSprite
                    && candidate.type == Image.Type.Filled)
                    return candidate;
            }

            return null;
        }

        private static Image ResolveVanillaSelfFill()
        {
            CanvasGroup injury = HUDManager.Instance?.selfRedCanvasGroup;
            Transform topLeft = injury != null ? injury.transform.parent : null;
            if (topLeft == null)
                return null;

            Transform self = topLeft.Find(VanillaSelfName);
            Image image = self != null ? self.GetComponent<Image>() : null;
            return image != null && image.isActiveAndEnabled ? image : null;
        }

        private static Sprite ResolveMinimalHealthFillSprite()
        {
            foreach (AssetBundle bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle == null || bundle.isStreamedSceneAssetBundle)
                    continue;

                GameObject prefab;
                try { prefab = bundle.LoadAsset<GameObject>(MinimalHealthPrefab); }
                catch { continue; }
                if (prefab == null)
                    continue;

                Image[] images = prefab.GetComponentsInChildren<Image>(includeInactive: true);
                for (int i = 0; i < images.Length; i++)
                {
                    Image image = images[i];
                    if (image != null && image.name == "MinimalHealthFill" && image.type == Image.Type.Filled)
                        return image.sprite;
                }
            }

            return null;
        }

        private static Image CreateReserveFill(Image healthFill)
        {
            if (healthFill == null || healthFill.sprite == null)
                return null;

            GameObject layer = new GameObject("Y4NGZResilienceReserve", typeof(RectTransform), typeof(Image));
            layer.transform.SetParent(healthFill.transform, worldPositionStays: false);
            layer.transform.SetAsLastSibling();
            RectTransform rect = layer.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image reserve = layer.GetComponent<Image>();
            reserve.sprite = healthFill.sprite;
            if (healthFill.type == Image.Type.Filled)
            {
                reserve.type = Image.Type.Filled;
                reserve.fillMethod = healthFill.fillMethod;
                reserve.fillOrigin = healthFill.fillOrigin;
                reserve.fillClockwise = healthFill.fillClockwise;
            }
            else
            {
                // The stock neutral silhouette is a simple Image. Its reserve layer fills from
                // the feet upward, so the remaining yellow silhouette depletes naturally.
                reserve.type = Image.Type.Filled;
                reserve.fillMethod = Image.FillMethod.Vertical;
                reserve.fillOrigin = (int)Image.OriginVertical.Bottom;
            }
            reserve.preserveAspect = healthFill.preserveAspect;
            reserve.material = healthFill.material;
            reserve.color = new Color(1f, 0.78f, 0.16f, 0.92f);
            reserve.raycastTarget = false;
            reserve.fillAmount = 0f;
            return reserve;
        }

        private static void SetVisible(bool visible)
        {
            if (_reserveFill != null && _reserveFill.gameObject.activeSelf != visible)
                _reserveFill.gameObject.SetActive(visible);
        }

        private static void LogMissingFillOnce()
        {
            if (_missingFillLogged)
                return;
            _missingFillLogged = true;
            Plugin.Log?.LogWarning(
                "[Resilience] no verified health silhouette was available; reserve health will not add a UI layer.");
        }
    }
}
