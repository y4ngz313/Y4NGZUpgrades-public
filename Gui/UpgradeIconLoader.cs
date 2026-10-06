using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;
using Object = UnityEngine.Object;

namespace Y4NGZUpgrades.Gui;

// Single source of truth for on-disk UI icon loading.
//
// The purchase menu, the upgrade HUD, the Foreman aura HUD and the left rail all
// draw the same PNGs. They used to each own a loader, which meant they disagreed
// about key normalisation and about whether the glyph gets whitened, so the same
// upgrade could read orange on the HUD and theme-neutral in the menu. Everything
// now resolves paths, normalises keys and whitens through this class; each
// surface still applies its own tint on top of the neutral white glyph.
internal static class UpgradeIconLoader
{
    private static readonly Dictionary<string, Texture2D> _upgradeIconTextures =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Texture2D> _menuIconTextures =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Sprite> _upgradeIconSprites =
        new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<Texture2D, Rect> _visibleUvRects =
        new Dictionary<Texture2D, Rect>();

    // Tree cards fit the drawing, not the PNG's transparent canvas. This is a
    // cached UV window; the shared texture and its HUD/inspector users keep
    // their authored pixels and framing.
    internal static Rect VisibleUvRect(Texture2D texture)
    {
        if (texture == null) return new Rect(0f, 0f, 1f, 1f);
        if (_visibleUvRects.TryGetValue(texture, out Rect cached)) return cached;

        int width = texture.width;
        int height = texture.height;
        int left = width, bottom = height, right = -1, top = -1;
        Color32[] pixels = texture.GetPixels32();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pixels[y * width + x].a <= 16) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            bottom = Math.Min(bottom, y);
            top = Math.Max(top, y);
        }

        Rect uv = new Rect(0f, 0f, 1f, 1f);
        if (right >= left && top >= bottom)
        {
            // Preserve the antialiased edge instead of cropping to hard ink.
            left = Math.Max(0, left - 2);
            bottom = Math.Max(0, bottom - 2);
            right = Math.Min(width - 1, right + 2);
            top = Math.Min(height - 1, top + 2);
            uv = new Rect((float)left / width, (float)bottom / height,
                (float)(right - left + 1) / width, (float)(top - bottom + 1) / height);
        }
        _visibleUvRects[texture] = uv;
        return uv;
    }

    internal static string NormalizeKey(string iconKey)
    {
        return Y4NGZUpgradeDefinition.NormalizeId(iconKey);
    }

    internal static Texture2D LoadUpgradeIconTexture(string iconKey)
    {
        if (string.IsNullOrWhiteSpace(iconKey))
            return null;

        string safeKey = NormalizeKey(iconKey);
        if (string.IsNullOrEmpty(safeKey))
            return null;

        if (_upgradeIconTextures.TryGetValue(safeKey, out Texture2D cached) && cached != null)
            return cached;

        string path = FindUpgradeIconPath(safeKey);
        Texture2D texture = LoadTexture(path, $"upgrade icon '{safeKey}'");
        if (texture == null)
            return null;

        NormalizeToWhite(texture);
        _upgradeIconTextures[safeKey] = texture;
        return texture;
    }

    internal static Sprite LoadUpgradeIconSprite(string iconKey)
    {
        if (string.IsNullOrWhiteSpace(iconKey))
            return null;

        string safeKey = NormalizeKey(iconKey);
        if (string.IsNullOrEmpty(safeKey))
            return null;

        if (_upgradeIconSprites.TryGetValue(safeKey, out Sprite cached) && cached != null)
            return cached;

        Texture2D texture = LoadUpgradeIconTexture(safeKey);
        if (texture == null)
            return null;

        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);
        _upgradeIconSprites[safeKey] = sprite;
        return sprite;
    }

    internal static Texture2D LoadMenuIconTexture(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        if (_menuIconTextures.TryGetValue(fileName, out Texture2D cached) && cached != null)
            return cached;

        string path = FindMenuIconPath(fileName);
        Texture2D texture = LoadTexture(path, $"menu icon '{fileName}'");
        if (texture == null)
            return null;

        NormalizeToWhite(texture);
        _menuIconTextures[fileName] = texture;
        return texture;
    }

    // Upgrade glyph PNGs historically baked their original orange into the
    // pixels. A RawImage/Image tint can only multiply that color, so blue and
    // green HUD themes could never produce a neutral icon. Preserve the authored
    // antialiasing/alpha while making every visible glyph pixel white once at
    // load time; per-surface tinting still works through the graphic color.
    internal static void NormalizeToWhite(Texture2D texture)
    {
        if (texture == null)
            return;

        Color32[] pixels = texture.GetPixels32();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 pixel = pixels[i];
            pixel.r = byte.MaxValue;
            pixel.g = byte.MaxValue;
            pixel.b = byte.MaxValue;
            pixels[i] = pixel;
        }

        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
    }

    internal static string FindUpgradeIconPath(string iconKey)
    {
        string safeKey = NormalizeKey(iconKey);
        if (string.IsNullOrEmpty(safeKey))
            return null;

        return FindAssetPath(safeKey + ".png", "UI", "UpgradeIcons");
    }

    internal static string FindMenuIconPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return FindAssetPath(fileName, "UI");
    }

    private static string FindAssetPath(string fileName, params string[] subFolders)
    {
        string assemblyDir = Path.GetDirectoryName(typeof(global::Y4NGZUpgrades.Plugin).Assembly.Location) ?? "";
        string rootDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string[] roots =
        {
            Path.Combine(assemblyDir, "Assets"),
            Path.Combine(assemblyDir, "InteractiveAssets"),
            Path.Combine(rootDir, "BepInEx", "plugins", "y4ngz-Y4NGZUpgrades", "Assets"),
            Path.Combine(rootDir, "BepInEx", "plugins", "Y4NGZUpgrades", "Assets")
        };

        for (int i = 0; i < roots.Length; i++)
        {
            string candidate = roots[i];
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            for (int f = 0; f < subFolders.Length; f++)
                candidate = Path.Combine(candidate, subFolders[f]);

            candidate = Path.Combine(candidate, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static Texture2D LoadTexture(string path, string description)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                Object.Destroy(texture);
                return null;
            }

            texture.name = Path.GetFileNameWithoutExtension(path);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return texture;
        }
        catch (Exception e)
        {
            global::Y4NGZUpgrades.Plugin.Log?.LogWarning(
                $"[UpgradeIconLoader] Failed to load {description}: {e.Message}");
            return null;
        }
    }
}
