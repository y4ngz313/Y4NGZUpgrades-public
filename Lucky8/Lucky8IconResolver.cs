using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZUpgrades.Gui;
using Object = UnityEngine.Object;

namespace Y4NGZUpgrades.Lucky8
{
    internal sealed class Lucky8ResolvedIcon
    {
        internal readonly Texture2D Texture;
        internal readonly Sprite Sprite;

        internal Lucky8ResolvedIcon(Texture2D texture, Sprite sprite)
        {
            Texture = texture;
            Sprite = sprite;
        }
    }

    /// <summary>
    /// Resolves reward art once, bakes it into the LUCKY-8 screen aspect, and
    /// caches both the Texture2D and Sprite presentation. Preview cameras are
    /// strictly one-shot and never survive a resolve call.
    /// </summary>
    internal static class Lucky8IconResolver
    {
        private const int IconWidth = 256;
        private const int IconHeight = 184;
        private const int SourceLimit = 768;
        private const int PreviewLayer = 23;
        private const float VisibleAlpha = 0.045f;
        private static readonly Vector3 PreviewOrigin = new Vector3(0f, -1600f, 0f);

        private static readonly Dictionary<string, Lucky8ResolvedIcon> IconCache =
            new Dictionary<string, Lucky8ResolvedIcon>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Lucky8RewardCategory, Lucky8ResolvedIcon> FallbackCache =
            new Dictionary<Lucky8RewardCategory, Lucky8ResolvedIcon>();
        private static readonly Dictionary<string, object> CosmeticSources =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<(string id, string label)> CosmeticCatalog =
            new List<(string id, string label)>();

        private static bool cosmeticsDiscovered;
        private static bool cosmeticPreviewReflectionResolved;
        private static Type cosmeticApplicationType;
        private static MethodInfo clearCosmeticsMethod;
        private static MethodInfo applyCosmeticMethod;
        private static FieldInfo parentTypeField;
        private static FieldInfo spawnedCosmeticsField;
        private static object displayGuyParentType;

        internal static Lucky8ResolvedIcon Resolve(Lucky8RewardDefinition reward)
        {
            Lucky8RewardCategory category = reward?.Category ?? Lucky8RewardCategory.Weapon;
            if (reward == null)
                return GetFallback(category);

            string key = BuildCacheKey(reward);
            if (IconCache.TryGetValue(key, out Lucky8ResolvedIcon cached) && cached?.Texture != null)
                return cached;

            Texture2D texture = null;
            try
            {
                switch (category)
                {
                    case Lucky8RewardCategory.Suit:
                        texture = ResolveSuit(reward);
                        break;
                    case Lucky8RewardCategory.Cosmetic:
                        texture = ResolveCosmetic(reward);
                        break;
                    case Lucky8RewardCategory.Emote:
                        texture = ResolveEmote(reward);
                        break;
                    case Lucky8RewardCategory.Ammo:
                    case Lucky8RewardCategory.Weapon:
                        texture = ResolveItem(reward);
                        break;
                }
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    "[LUCKY-8] Icon resolve failed safely for " + reward.StableId + ": " + exception.Message);
            }

            Lucky8ResolvedIcon resolved = texture != null
                ? CreateResolved(texture, key)
                : GetFallback(category);
            IconCache[key] = resolved;
            reward.IconTexture = resolved.Texture;
            return resolved;
        }

        internal static IReadOnlyList<(string id, string label)> DiscoverMoreCompanyCosmetics()
        {
            if (cosmeticsDiscovered)
                return CosmeticCatalog;

            cosmeticsDiscovered = true;
            try
            {
                Assembly assembly = FindAssembly("MoreCompany");
                Type registry = assembly?.GetType("MoreCompany.Cosmetics.CosmeticRegistry", false);
                FieldInfo instancesField = registry?.GetField(
                    "cosmeticInstances",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (!(instancesField?.GetValue(null) is IDictionary instances))
                    return CosmeticCatalog;

                foreach (DictionaryEntry pair in instances)
                {
                    string id = pair.Key as string;
                    if (string.IsNullOrWhiteSpace(id) || pair.Value == null)
                        continue;

                    id = id.Trim();
                    CosmeticSources[id] = pair.Value;
                    CosmeticCatalog.Add((id, Humanize(id)));
                }
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    "[LUCKY-8] MoreCompany cosmetic discovery failed safely: " + exception.Message);
            }
            return CosmeticCatalog;
        }

        private static Texture2D ResolveItem(Lucky8RewardDefinition reward)
        {
            // The weapon and ammo registries live in Better Armory, so both halves of the icon —
            // the inventory sprite and the prefab the bust render falls back to — come across the
            // prize bridge (#267). Both are null without that plugin, which degrades to the generic
            // prize icon.
            Item item;
            GameObject prefab;
            if (reward.Category == Lucky8RewardCategory.Ammo)
            {
                item = ArmoryBridge.GetAmmoItem(reward.StableId);
                prefab = ArmoryBridge.GetAmmoPrizePrefab(reward.StableId);
            }
            else
            {
                item = ArmoryBridge.GetWeaponItem(reward.StableId);
                prefab = ArmoryBridge.GetWeaponPrizePrefab(reward.StableId);
            }

            if (TryPresentSprite(item?.itemIcon, reward, preserveColor: false, swatch: false, out Texture2D icon))
                return icon;

            Texture2D preview = RenderPrefabOnce(prefab, bust: false);
            if (preview == null)
                return null;
            try
            {
                return TryPresentTexture(preview, null, reward, preserveColor: false, swatch: false, out icon)
                    ? icon
                    : null;
            }
            finally
            {
                Object.Destroy(preview);
            }
        }

        private static Texture2D ResolveSuit(Lucky8RewardDefinition reward)
        {
            Material suitMaterial = FindSuitMaterial(reward);
            Texture source = suitMaterial != null ? suitMaterial.mainTexture : null;
            if (TryPresentTexture(source, null, reward, preserveColor: true, swatch: true, out Texture2D icon))
                return icon;

            Texture2D preview = RenderEmployeeOnce(suitMaterial, null);
            if (preview == null)
                return null;
            try
            {
                return TryPresentTexture(preview, null, reward, preserveColor: true, swatch: false, out icon)
                    ? icon
                    : null;
            }
            finally
            {
                Object.Destroy(preview);
            }
        }

        private static Texture2D ResolveCosmetic(Lucky8RewardDefinition reward)
        {
            DiscoverMoreCompanyCosmetics();
            if (CosmeticSources.TryGetValue(reward.StableId ?? string.Empty, out object cosmetic))
            {
                object rawIcon = GetMemberValue(cosmetic, "icon")
                    ?? GetMemberValue(cosmetic, "Icon")
                    ?? GetMemberValue(cosmetic, "iconTexture");
                if (rawIcon is Sprite sprite
                    && TryPresentSprite(sprite, reward, preserveColor: true, swatch: false, out Texture2D icon))
                    return icon;
                if (rawIcon is Texture texture
                    && TryPresentTexture(texture, null, reward, preserveColor: true, swatch: false, out icon))
                    return icon;
            }

            Texture2D preview = RenderEmployeeOnce(null, reward.StableId);
            if (preview == null)
                return null;
            try
            {
                return TryPresentTexture(preview, null, reward, preserveColor: true, swatch: false, out Texture2D icon)
                    ? icon
                    : null;
            }
            finally
            {
                Object.Destroy(preview);
            }
        }

        private static Texture2D ResolveEmote(Lucky8RewardDefinition reward)
        {
            object rawIcon = reward.IconSource;
            if (rawIcon == null)
            {
                TooManyEmotesEntry emote = TooManyEmotesBridge.FindPurchasableEmote(reward.StableId);
                rawIcon = emote?.icon;
            }
            if (rawIcon is Sprite sprite
                && TryPresentSprite(sprite, reward, preserveColor: true, swatch: false, out Texture2D icon))
                return icon;
            if (rawIcon is Texture texture
                && TryPresentTexture(texture, null, reward, preserveColor: true, swatch: false, out icon))
                return icon;
            return null;
        }

        private static Lucky8ResolvedIcon GetFallback(Lucky8RewardCategory category)
        {
            if (FallbackCache.TryGetValue(category, out Lucky8ResolvedIcon cached) && cached?.Texture != null)
                return cached;

            string fileName;
            switch (category)
            {
                case Lucky8RewardCategory.Suit:
                    fileName = "menu-icon-suits.png";
                    break;
                case Lucky8RewardCategory.Cosmetic:
                    fileName = "menu-icon-cosmetics.png";
                    break;
                case Lucky8RewardCategory.Emote:
                    fileName = "menu-icon-emotes.png";
                    break;
                default:
                    // The shipped upgrades/equipment mark is the existing art
                    // closest to both weapon and ammunition categories.
                    fileName = "menu-icon-upgrades.png";
                    break;
            }

            var fallbackReward = new Lucky8RewardDefinition
            {
                StableId = "category-fallback-" + category,
                DisplayName = category.ToString(),
                Category = category,
                Rarity = Lucky8Rarity.Common,
            };

            Texture2D texture = null;
            try
            {
                Texture2D shipped = PurchaseMenu.LoadMenuIconTexture(fileName);
                TryPresentTexture(shipped, null, fallbackReward, preserveColor: false, swatch: false, out texture);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    "[LUCKY-8] Category icon asset failed safely for " + category + ": " + exception.Message);
            }

            if (texture == null)
                texture = CreateProceduralFallback(fallbackReward);
            cached = CreateResolved(texture, "fallback-" + category);
            FallbackCache[category] = cached;
            return cached;
        }

        private static bool TryPresentSprite(
            Sprite sprite,
            Lucky8RewardDefinition reward,
            bool preserveColor,
            bool swatch,
            out Texture2D result)
        {
            result = null;
            if (sprite == null || sprite.texture == null)
                return false;

            Rect rect;
            try { rect = sprite.textureRect; }
            catch { rect = sprite.rect; }
            return TryPresentTexture(sprite.texture, rect, reward, preserveColor, swatch, out result);
        }

        private static bool TryPresentTexture(
            Texture source,
            Rect? sourceRect,
            Lucky8RewardDefinition reward,
            bool preserveColor,
            bool swatch,
            out Texture2D result)
        {
            result = null;
            if (source == null || source.width <= 0 || source.height <= 0)
                return false;
            if (!swatch && IsPlaceholderName(source.name))
                return false;

            if (!TryReadPixels(source, sourceRect, out PixelBuffer buffer))
                return false;
            if ((!swatch && (buffer.Width <= 4 || buffer.Height <= 4))
                || !TryBuildMask(buffer, swatch, out float[] alpha, out Color32[] colors, out RectInt content))
                return false;

            result = BuildPresentation(alpha, colors, buffer.Width, buffer.Height, content, reward, preserveColor, swatch);
            return result != null;
        }

        private static bool TryReadPixels(Texture source, Rect? requested, out PixelBuffer buffer)
        {
            buffer = default;
            Rect rect = requested ?? new Rect(0f, 0f, source.width, source.height);
            rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, source.width - 1f));
            rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, source.height - 1f));
            rect.width = Mathf.Clamp(rect.width, 1f, source.width - rect.x);
            rect.height = Mathf.Clamp(rect.height, 1f, source.height - rect.y);

            float scale = Mathf.Min(1f, SourceLimit / Mathf.Max(rect.width, rect.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(rect.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(rect.height * scale));
            RenderTexture temporary = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                temporary.wrapMode = TextureWrapMode.Clamp;
                temporary.filterMode = FilterMode.Bilinear;
                Vector2 uvScale = new Vector2(rect.width / source.width, rect.height / source.height);
                Vector2 uvOffset = new Vector2(rect.x / source.width, rect.y / source.height);
                Graphics.Blit(source, temporary, uvScale, uvOffset);
                RenderTexture.active = temporary;
                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                readable.Apply(false, false);
                buffer = new PixelBuffer(width, height, readable.GetPixels32());
                Object.Destroy(readable);
                return true;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Icon pixels were unreadable: " + exception.Message);
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null)
                    RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private static bool TryBuildMask(
            PixelBuffer buffer,
            bool swatch,
            out float[] alpha,
            out Color32[] colors,
            out RectInt content)
        {
            colors = buffer.Pixels;
            alpha = new float[colors.Length];
            content = default;
            if (colors.Length == 0)
                return false;

            Color background = AverageCorners(colors, buffer.Width, buffer.Height);
            int opaque = 0;
            for (int i = 0; i < colors.Length; i++)
                if (colors[i].a >= 250) opaque++;
            bool opaqueSource = opaque >= colors.Length * 0.985f;

            int minX = buffer.Width;
            int minY = buffer.Height;
            int maxX = -1;
            int maxY = -1;
            int visible = 0;
            float colorEnergy = 0f;
            for (int y = 0; y < buffer.Height; y++)
            {
                for (int x = 0; x < buffer.Width; x++)
                {
                    int index = y * buffer.Width + x;
                    Color color = colors[index];
                    float value = color.a;
                    if (opaqueSource && !swatch)
                    {
                        float difference = Mathf.Max(
                            Mathf.Abs(color.r - background.r),
                            Mathf.Max(Mathf.Abs(color.g - background.g), Mathf.Abs(color.b - background.b)));
                        value *= Mathf.Clamp01((difference - 0.025f) * 5.5f);
                        colorEnergy += difference;
                    }
                    else
                    {
                        colorEnergy += Mathf.Max(color.r, Mathf.Max(color.g, color.b))
                            - Mathf.Min(color.r, Mathf.Min(color.g, color.b));
                    }
                    alpha[index] = value;
                    if (value < VisibleAlpha)
                        continue;

                    visible++;
                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            if (visible < Mathf.Max(12, colors.Length / 900))
                return false;
            if (opaqueSource && !swatch && colorEnergy / colors.Length < 0.015f)
                return false;
            if (swatch && !IsRecognizableSwatch(colors, colorEnergy))
                return false;

            content = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return content.width > 1 && content.height > 1;
        }

        private static bool IsRecognizableSwatch(Color32[] pixels, float colorEnergy)
        {
            if (pixels.Length < 16)
                return false;

            Color average = Color.black;
            int stride = Mathf.Max(1, pixels.Length / 512);
            int samples = 0;
            for (int i = 0; i < pixels.Length; i += stride)
            {
                if (pixels[i].a < 32) continue;
                average += (Color)pixels[i];
                samples++;
            }
            if (samples == 0)
                return false;
            average /= samples;
            Color.RGBToHSV(average, out _, out float saturation, out float brightness);
            return saturation > 0.035f
                || (brightness > 0.055f && brightness < 0.94f)
                || colorEnergy / pixels.Length > 0.02f;
        }

        private static Texture2D BuildPresentation(
            float[] sourceAlpha,
            Color32[] sourceColors,
            int sourceWidth,
            int sourceHeight,
            RectInt content,
            Lucky8RewardDefinition reward,
            bool preserveColor,
            bool swatch)
        {
            const int padding = 17;
            int availableWidth = IconWidth - padding * 2;
            int availableHeight = IconHeight - padding * 2;
            float fit = Mathf.Min(
                availableWidth / (float)content.width,
                availableHeight / (float)content.height);
            if (swatch)
                fit *= 0.82f;
            int drawWidth = Mathf.Max(1, Mathf.RoundToInt(content.width * fit));
            int drawHeight = Mathf.Max(1, Mathf.RoundToInt(content.height * fit));
            int left = (IconWidth - drawWidth) / 2;
            int bottom = (IconHeight - drawHeight) / 2;

            var fittedAlpha = new float[IconWidth * IconHeight];
            var fittedColors = new Color32[IconWidth * IconHeight];
            for (int y = 0; y < drawHeight; y++)
            {
                float sourceY = content.y + ((y + 0.5f) * content.height / drawHeight) - 0.5f;
                int sy = Mathf.Clamp(Mathf.RoundToInt(sourceY), content.y, content.yMax - 1);
                for (int x = 0; x < drawWidth; x++)
                {
                    float sourceX = content.x + ((x + 0.5f) * content.width / drawWidth) - 0.5f;
                    int sx = Mathf.Clamp(Mathf.RoundToInt(sourceX), content.x, content.xMax - 1);
                    int sourceIndex = sy * sourceWidth + sx;
                    int targetIndex = (bottom + y) * IconWidth + left + x;
                    fittedAlpha[targetIndex] = sourceAlpha[sourceIndex];
                    fittedColors[targetIndex] = sourceColors[sourceIndex];
                }
            }

            Color tint = IconTint(reward);
            Color fill = Color.Lerp(new Color(0.96f, 0.97f, 0.94f, 1f), tint, 0.28f);
            Color outline = Color.Lerp(Color.black, tint, 0.42f);
            Color32[] output = new Color32[IconWidth * IconHeight];
            const int radius = 3;
            for (int y = 0; y < IconHeight; y++)
            {
                for (int x = 0; x < IconWidth; x++)
                {
                    int index = y * IconWidth + x;
                    float value = fittedAlpha[index];
                    if (value >= VisibleAlpha)
                    {
                        Color applied = fill;
                        if (preserveColor)
                        {
                            Color source = fittedColors[index];
                            source.a = 1f;
                            applied = Color.Lerp(source, tint, swatch ? 0.12f : 0.22f);
                            applied *= 1.12f;
                            applied.a = 1f;
                        }
                        applied.a = Mathf.Clamp01(value);
                        output[index] = applied;
                        continue;
                    }

                    float neighbor = 0f;
                    for (int oy = -radius; oy <= radius && neighbor < 0.92f; oy++)
                    {
                        int ny = y + oy;
                        if (ny < 0 || ny >= IconHeight) continue;
                        for (int ox = -radius; ox <= radius; ox++)
                        {
                            if (ox * ox + oy * oy > radius * radius) continue;
                            int nx = x + ox;
                            if (nx < 0 || nx >= IconWidth) continue;
                            neighbor = Mathf.Max(neighbor, fittedAlpha[ny * IconWidth + nx]);
                        }
                    }
                    if (neighbor >= VisibleAlpha)
                    {
                        Color edge = outline;
                        edge.a = Mathf.Clamp01(neighbor * 0.94f);
                        output[index] = edge;
                    }
                }
            }

            var texture = new Texture2D(IconWidth, IconHeight, TextureFormat.RGBA32, false)
            {
                name = "Lucky8Icon_" + SafeName(reward?.StableId),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixels32(output);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D CreateProceduralFallback(Lucky8RewardDefinition reward)
        {
            const int size = 64;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool filled;
                    switch (reward.Category)
                    {
                        case Lucky8RewardCategory.Suit:
                            float dx = x - 32f;
                            float dy = y - 46f;
                            filled = dx * dx + dy * dy <= 8f * 8f
                                || (y >= 13 && y <= 38 && x >= 17 && x <= 47
                                    && Mathf.Abs(x - 32f) <= 22f - Mathf.Abs(y - 24f) * 0.26f);
                            break;
                        case Lucky8RewardCategory.Cosmetic:
                            float angle = Mathf.Atan2(y - 32f, x - 32f);
                            float distance = Vector2.Distance(new Vector2(x, y), new Vector2(32f, 32f));
                            float star = 13f + 9f * Mathf.Abs(Mathf.Cos(angle * 4f));
                            filled = distance <= star;
                            break;
                        case Lucky8RewardCategory.Ammo:
                            filled = (x >= 13 && x <= 24 || x >= 27 && x <= 38 || x >= 41 && x <= 52)
                                && y >= 13 && y <= 50
                                && (y <= 44 || (x % 14 >= 3 && x % 14 <= 9));
                            break;
                        case Lucky8RewardCategory.Emote:
                            float noteStemX = x - 39f;
                            float noteStemY = y - 38f;
                            float noteHeadX = x - 28f;
                            float noteHeadY = y - 18f;
                            filled = noteStemX * noteStemX <= 5f * 5f && y >= 19 && y <= 48
                                || noteHeadX * noteHeadX + noteHeadY * noteHeadY <= 10f * 10f
                                || y >= 43 && y <= 49 && x >= 36 && x <= 53;
                            break;
                        default:
                            filled = Mathf.Abs((y - 18f) - (x - 10f) * 0.58f) <= 4f
                                && x >= 8 && x <= 55
                                || x >= 10 && x <= 25 && y >= 12 && y <= 23;
                            break;
                    }
                    if (filled)
                        pixels[y * size + x] = Color.white;
                }
            }

            var source = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Lucky8ProceduralFallbackSource",
                hideFlags = HideFlags.HideAndDontSave,
            };
            source.SetPixels32(pixels);
            source.Apply(false, false);
            TryPresentTexture(source, null, reward, preserveColor: false, swatch: false, out Texture2D result);
            Object.Destroy(source);
            if (result != null)
                return result;

            // Last-resort nonblank texture; this path does not depend on any
            // external asset, shader, optional mod, or readable source image.
            var emergency = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "Lucky8EmergencyFallback",
                hideFlags = HideFlags.HideAndDontSave,
            };
            Color tint = IconTint(reward);
            emergency.SetPixels(new[] { tint, tint, tint, tint });
            emergency.Apply(false, true);
            return emergency;
        }

        private static Texture2D RenderPrefabOnce(GameObject prefab, bool bust)
        {
            if (prefab == null)
                return null;

            GameObject root = null;
            try
            {
                root = new GameObject("Lucky8_OneShotItemPreview");
                root.SetActive(false);
                root.transform.position = PreviewOrigin;
                GameObject clone = Object.Instantiate(prefab, root.transform, false);
                clone.name = "Lucky8_ItemPreviewClone";
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                DisableBehaviours(clone);
                SetLayerRecursively(root, PreviewLayer);
                clone.SetActive(true);
                root.SetActive(true);
                return CaptureRoot(root, bust);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Item preview failed safely: " + exception.Message);
                return null;
            }
            finally
            {
                DestroyTemporaryRoot(root);
            }
        }

        private static Texture2D RenderEmployeeOnce(Material suitMaterial, string cosmeticId)
        {
            GameObject root = null;
            try
            {
                GameNetcodeStuff.PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                    ?? StartOfRound.Instance?.localPlayerController;
                if (player == null || player.thisPlayerModel == null)
                    return null;

                root = new GameObject("Lucky8_OneShotEmployeePreview");
                root.SetActive(false);
                root.transform.position = PreviewOrigin;
                GameObject clone = Object.Instantiate(player.gameObject, root.transform, false);
                clone.name = "Lucky8_EmployeePreviewClone";
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                clone.transform.localScale = Vector3.one;
                DisableBehaviours(clone);

                foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = false;
                Transform model = clone.transform.Find("ScavengerModel");
                Transform metarig = model != null ? model.Find("metarig") : null;
                SkinnedMeshRenderer body = player.thisPlayerModel != null
                    ? FindBodyRenderer(model)
                    : null;
                if (body == null || metarig == null)
                    return null;

                body.gameObject.SetActive(true);
                body.enabled = true;
                body.updateWhenOffscreen = true;
                body.shadowCastingMode = ShadowCastingMode.On;
                body.sharedMaterial = suitMaterial != null ? suitMaterial : player.thisPlayerModel.sharedMaterial;
                SetLayerRecursively(root, PreviewLayer);
                root.SetActive(true);

                if (!string.IsNullOrWhiteSpace(cosmeticId)
                    && !TryApplySingleCosmetic(metarig.gameObject, cosmeticId))
                    return null;

                SetLayerRecursively(root, PreviewLayer);
                return CaptureRoot(root, bust: true);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Employee preview failed safely: " + exception.Message);
                return null;
            }
            finally
            {
                DestroyTemporaryRoot(root);
            }
        }

        private static Texture2D CaptureRoot(GameObject root, bool bust)
        {
            Renderer[] allRenderers = root.GetComponentsInChildren<Renderer>(true);
            var visible = new List<Renderer>(allRenderers.Length);
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer renderer = allRenderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    continue;
                Bounds candidate = renderer.bounds;
                if (candidate.size.sqrMagnitude <= 0.000001f || candidate.size.sqrMagnitude > 100000f)
                    continue;
                visible.Add(renderer);
                if (!hasBounds)
                {
                    bounds = candidate;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(candidate);
                }
            }
            if (!hasBounds || visible.Count == 0)
                return null;

            GameObject cameraObject = new GameObject("Lucky8_OneShotPreviewCamera");
            cameraObject.transform.SetParent(root.transform, true);
            cameraObject.layer = PreviewLayer;
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.cameraType = CameraType.Preview;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.cullingMask = 1 << PreviewLayer;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 50f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.orthographic = true;
            camera.enabled = false;

            Vector3 focus = bounds.center;
            float viewHeight = bounds.size.y;
            if (bust)
            {
                focus.y += bounds.size.y * 0.19f;
                viewHeight *= 0.64f;
            }
            float aspect = IconWidth / (float)IconHeight;
            camera.orthographicSize = Mathf.Max(viewHeight * 0.58f, bounds.size.x / aspect * 0.64f);
            camera.orthographicSize = Mathf.Max(camera.orthographicSize, 0.08f);
            Vector3 direction = new Vector3(0.82f, 0.18f, -1f).normalized;
            camera.transform.position = focus - direction * Mathf.Max(2f, bounds.extents.magnitude * 3.2f);
            camera.transform.rotation = Quaternion.LookRotation(focus - camera.transform.position, Vector3.up);

            GameObject lightObject = new GameObject("Lucky8_OneShotPreviewLight");
            lightObject.transform.SetParent(cameraObject.transform, false);
            lightObject.layer = PreviewLayer;
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = new Color(1f, 0.92f, 0.82f);
            light.intensity = 55f;
            light.range = Mathf.Max(12f, bounds.extents.magnitude * 8f);
            light.innerSpotAngle = 80f;
            light.spotAngle = 110f;
            light.shadows = LightShadows.None;

            RenderTexture renderTexture = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                renderTexture = RenderTexture.GetTemporary(512, 368, 24, RenderTextureFormat.ARGB32);
                renderTexture.filterMode = FilterMode.Bilinear;
                renderTexture.wrapMode = TextureWrapMode.Clamp;
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var result = new Texture2D(512, 368, TextureFormat.RGBA32, false)
                {
                    name = "Lucky8_OneShotPreviewCapture",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                result.ReadPixels(new Rect(0f, 0f, 512f, 368f), 0, 0, false);
                result.Apply(false, false);
                return result;
            }
            finally
            {
                camera.targetTexture = null;
                camera.enabled = false;
                RenderTexture.active = previous;
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
                light.enabled = false;
                cameraObject.SetActive(false);
                Object.Destroy(cameraObject);
            }
        }

        private static bool TryApplySingleCosmetic(GameObject metarig, string cosmeticId)
        {
            ResolveCosmeticPreviewReflection();
            if (metarig == null || cosmeticApplicationType == null || applyCosmeticMethod == null)
                return false;

            try
            {
                Component application = metarig.AddComponent(cosmeticApplicationType);
                if (parentTypeField != null && displayGuyParentType != null)
                    parentTypeField.SetValue(application, displayGuyParentType);
                applyCosmeticMethod.Invoke(application, new object[] { cosmeticId, true });

                int spawnedCount = 0;
                if (spawnedCosmeticsField?.GetValue(application) is IEnumerable spawned)
                {
                    foreach (object entry in spawned)
                    {
                        Transform transform = (entry as Component)?.transform
                            ?? (entry as GameObject)?.transform;
                        if (transform == null) continue;
                        transform.localScale *= 0.38f;
                        spawnedCount++;
                    }
                }
                if (application is Behaviour behaviour)
                    behaviour.enabled = false;
                return spawnedCount > 0;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    "[LUCKY-8] MoreCompany cosmetic preview failed safely: " + exception.Message);
                return false;
            }
        }

        private static void ResolveCosmeticPreviewReflection()
        {
            if (cosmeticPreviewReflectionResolved)
                return;
            cosmeticPreviewReflectionResolved = true;

            try
            {
                Assembly assembly = FindAssembly("MoreCompany");
                cosmeticApplicationType = assembly?.GetType("MoreCompany.Cosmetics.CosmeticApplication", false);
                clearCosmeticsMethod = cosmeticApplicationType?.GetMethod("ClearCosmetics", Type.EmptyTypes);
                applyCosmeticMethod = cosmeticApplicationType?.GetMethod(
                    "ApplyCosmetic",
                    new[] { typeof(string), typeof(bool) });
                parentTypeField = cosmeticApplicationType?.GetField(
                    "parentType",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                spawnedCosmeticsField = cosmeticApplicationType?.GetField(
                    "spawnedCosmetics",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Type parentType = assembly?.GetType("MoreCompany.Cosmetics.ParentType", false);
                if (parentType != null)
                    displayGuyParentType = Enum.Parse(parentType, "DisplayGuy");
            }
            catch
            {
                cosmeticApplicationType = null;
            }
        }

        private static void DisableBehaviours(GameObject root)
        {
            if (root == null) return;
            ResolveCosmeticPreviewReflection();
            foreach (Behaviour behaviour in root.GetComponentsInChildren<Behaviour>(true))
                behaviour.enabled = false;

            if (cosmeticApplicationType == null || clearCosmeticsMethod == null)
                return;
            try
            {
                foreach (Component application in root.GetComponentsInChildren(cosmeticApplicationType, true))
                    clearCosmeticsMethod.Invoke(application, null);
            }
            catch { }
        }

        private static SkinnedMeshRenderer FindBodyRenderer(Transform model)
        {
            if (model == null) return null;
            foreach (SkinnedMeshRenderer renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (string.Equals(renderer.name, "LOD1", StringComparison.Ordinal))
                    return renderer;
            return null;
        }

        private static Material FindSuitMaterial(Lucky8RewardDefinition reward)
        {
            List<UnlockableItem> unlockables = StartOfRound.Instance?.unlockablesList?.unlockables;
            if (unlockables == null)
                return null;

            for (int i = 0; i < unlockables.Count; i++)
            {
                UnlockableItem suit = unlockables[i];
                if (suit == null || suit.unlockableType != 0 || suit.suitMaterial == null)
                    continue;
                string label = string.IsNullOrWhiteSpace(suit.unlockableName)
                    ? "Suit " + i
                    : suit.unlockableName.Trim();
                if (string.Equals(label, reward.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Slug(label), reward.StableId, StringComparison.OrdinalIgnoreCase))
                    return suit.suitMaterial;
            }
            return null;
        }

        private static Lucky8ResolvedIcon CreateResolved(Texture2D texture, string name)
        {
            if (texture == null)
                return null;
            texture.name = "Lucky8_" + SafeName(name);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            sprite.name = texture.name + "_Sprite";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return new Lucky8ResolvedIcon(texture, sprite);
        }

        private static Color IconTint(Lucky8RewardDefinition reward)
        {
            Lucky8RewardCategory category = reward?.Category ?? Lucky8RewardCategory.Weapon;
            Color categoryColor;
            switch (category)
            {
                case Lucky8RewardCategory.Suit:
                    categoryColor = new Color(0.14f, 0.78f, 0.88f);
                    break;
                case Lucky8RewardCategory.Cosmetic:
                    categoryColor = new Color(0.82f, 0.30f, 0.94f);
                    break;
                case Lucky8RewardCategory.Ammo:
                    categoryColor = new Color(1f, 0.79f, 0.20f);
                    break;
                case Lucky8RewardCategory.Emote:
                    categoryColor = Lucky8RewardPresentation.TryGetColor(reward, out Color emoteColor)
                        ? emoteColor
                        : new Color(0.36f, 0.62f, 1f);
                    break;
                default:
                    categoryColor = new Color(1f, 0.47f, 0.10f);
                    break;
            }

            uint hash = StableHash(reward?.StableId);
            float uniqueHue = (hash % 1000u) / 1000f;
            Color unique = Color.HSVToRGB(uniqueHue, 0.58f, 1f);
            float uniqueMix = category == Lucky8RewardCategory.Suit
                || category == Lucky8RewardCategory.Cosmetic
                || category == Lucky8RewardCategory.Emote
                ? 0.22f
                : 0.08f;
            Color tint = Color.Lerp(categoryColor, unique, uniqueMix);
            float boost = 0.92f + 0.08f * (int)(reward?.Rarity ?? Lucky8Rarity.Common);
            return tint * boost;
        }

        private static Color AverageCorners(Color32[] pixels, int width, int height)
        {
            if (pixels.Length == 0) return Color.clear;
            int radiusX = Mathf.Max(1, width / 12);
            int radiusY = Mathf.Max(1, height / 12);
            Color total = Color.clear;
            int count = 0;
            for (int y = 0; y < height; y++)
            {
                if (y >= radiusY && y < height - radiusY) continue;
                for (int x = 0; x < width; x++)
                {
                    if (x >= radiusX && x < width - radiusX) continue;
                    total += (Color)pixels[y * width + x];
                    count++;
                }
            }
            return count > 0 ? total / count : (Color)pixels[0];
        }

        private static object GetMemberValue(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                Type type = instance.GetType();
                return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           ?.GetValue(instance)
                    ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           ?.GetValue(instance, null);
            }
            catch
            {
                return null;
            }
        }

        private static Assembly FindAssembly(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
                    return assembly;
            return null;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null) return;
            root.layer = layer;
            for (int i = 0; i < root.transform.childCount; i++)
                SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
        }

        private static void DestroyTemporaryRoot(GameObject root)
        {
            if (root == null) return;
            root.SetActive(false);
            Object.Destroy(root);
        }

        private static string BuildCacheKey(Lucky8RewardDefinition reward)
        {
            return reward.Category + ":" + (reward.StableId ?? string.Empty) + ":" + reward.Rarity;
        }

        private static bool IsPlaceholderName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("simpleicon", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Humanize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Cosmetic";
            return value.Replace('_', ' ').Replace('-', ' ').Trim();
        }

        private static string Slug(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            char[] chars = value.ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
            return new string(chars).Trim('_');
        }

        private static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown";
            return value.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        }

        private static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        private readonly struct PixelBuffer
        {
            internal readonly int Width;
            internal readonly int Height;
            internal readonly Color32[] Pixels;

            internal PixelBuffer(int width, int height, Color32[] pixels)
            {
                Width = width;
                Height = height;
                Pixels = pixels;
            }
        }
    }
}
