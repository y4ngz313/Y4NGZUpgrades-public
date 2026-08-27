using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using GameNetcodeStuff;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using Y4NGZUpgrades;
using Y4NGZUpgrades.Upgrades;
using Y4NGZUpgrades.Config;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZUpgrades.UITheme;
using Object = UnityEngine.Object;

namespace Y4NGZUpgrades.Gui;

// -
//  COLOUR PALETTE
// -
internal static class Palette
{
    public static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);
    public static Color BgOverlay => Tone(0.42f, 0.44f);
    public static Color Backframe => Tone(0.50f, 0.86f);
    public static Color BgPanel => Tone(0.74f, 0.72f);
    public static Color BgHeader => Tone(0.92f, 0.78f);
    public static Color BgRow => Tone(0.62f, 0.64f);
    public static Color BgRowHover => Tone(0.86f, 0.78f);
    public static Color BgInput => Tone(0.70f, 0.66f);
    public static Color Separator => WithAlpha(UiTheme.Accent, 0.72f);
    public static Color BorderHot => WithAlpha(UiTheme.Accent, 0.88f);
    public static Color BorderDim => WithAlpha(UiTheme.Accent, 0.38f);

    // - Text and accent -
    // Every name here says what the color MEANS. Nothing is named after the hue
    // it happens to be under the default theme: this menu used to have `Green`,
    // `White`, `Cyan` and `Gold` that all resolved to warm accents, which is how
    // literals kept creeping back in.
    public static Color Accent => UiTheme.Accent;
    public static Color Primary => UiTheme.Accent;
    public static Color Body => Color.Lerp(UiTheme.Dim, Color.white, 0.38f);
    public static Color Dim => UiTheme.Dim;
    public static Color Lore => Color.Lerp(UiTheme.Dim, UiTheme.Accent, 0.28f);
    public static Color RowButtonText => Color.Lerp(UiTheme.Accent, Color.white, 0.72f);
    public static Color NavText => WithAlpha(UiTheme.Accent, 0.92f);

    // - Semantic states -
    // Danger is red in every theme on purpose (destructive actions and hard
    // failures); Warning is the theme's own caution color; merely-unavailable
    // states use Muted so they read as disabled rather than as an error in an
    // unrelated hue.
    public static Color Danger => UiTheme.Danger;
    public static Color Warning => UiTheme.Warning;
    public static Color Muted => UiTheme.Muted;

    // Locked skill-tree nodes are deliberately neutral across every theme.
    public static Color Locked => new Color(0.56f, 0.56f, 0.56f, 0.90f);

    // - Positive states -
    // Owned/maxed and on-sale used to be the same value under three names, so a
    // maxed upgrade and a discounted one were indistinguishable. Sale is now a
    // small hue rotation off the accent; both stay inside the active theme.
    public static Color Gold => Highlight;
    public static Color Sale => SaleTint();

    // Scrollbars follow the palette instead of the old baked orange handle.
    public static Color ScrollTrack => WithAlpha(Color.Lerp(UiTheme.Panel, Color.black, 0.45f), 0.85f);
    public static Color ScrollHandle => WithAlpha(UiTheme.Accent, 0.88f);

    // Backdrop for render-texture surfaces (player preview). Near-black with a
    // trace of the theme hue so it never reads as a leftover maroon plate.
    public static Color Shell => WithAlpha(Color.Lerp(Color.black, UiTheme.Panel, 0.22f), 0.98f);

    // - Upgrade type colours -
    // These drive row tinting and sorting inside each category tab.
    public static Color TypeSpeed => UiTheme.Accent;
    public static Color TypeDefense => Color.Lerp(UiTheme.Dim, UiTheme.Accent, 0.35f);
    public static Color TypeOffense => UiTheme.Accent;
    public static Color TypeStealth => Color.Lerp(UiTheme.Panel, UiTheme.Dim, 0.72f);
    public static Color TypeLoot => Highlight;
    public static Color TypeTeam => Color.Lerp(UiTheme.Dim, UiTheme.Accent, 0.58f);

    public static Color BtnNormal => Tone(0.72f, 0.62f);
    public static Color BtnHighlight => Tone(0.96f, 0.78f);
    public static Color BtnActive => WithAlpha(Color.Lerp(UiTheme.Panel, UiTheme.Accent, 0.30f), 0.94f);
    public static Color BtnDisabled => Tone(0.38f, 0.46f);
    public static Color BtnDanger => WithAlpha(Color.Lerp(UiTheme.Panel, Danger, 0.24f), 0.44f);
    public static Color BtnDangerHl => WithAlpha(Color.Lerp(UiTheme.Panel, Danger, 0.38f), 0.64f);

    private static Color Highlight => Color.Lerp(UiTheme.Accent, Color.white, 0.44f);

    private static Color SaleTint()
    {
        Color.RGBToHSV(UiTheme.Accent, out float hue, out float saturation, out float value);
        Color tint = Color.HSVToRGB(Mathf.Repeat(hue + 0.11f, 1f), saturation, value);
        tint.a = UiTheme.Accent.a;
        return Color.Lerp(tint, Color.white, 0.30f);
    }

    private static Color Tone(float intensity, float alpha)
    {
        Color panel = UiTheme.Panel;
        Color accent = UiTheme.Accent;
        Color color = Color.Lerp(panel, accent, Mathf.Clamp01(intensity) * 0.22f);
        return WithAlpha(color, alpha);
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }

    /// <summary>
    /// A darkened plate of <paramref name="tint"/>. Replaces the per-channel
    /// multiplies this menu used to darken with (<c>g * 0.35f, b * 0.25f</c> and
    /// friends), which only produced a sensible color when the accent was warm —
    /// under Blue or Green they collapsed to muddy grey.
    /// </summary>
    public static Color Shade(Color tint, float strength, float alpha)
    {
        return WithAlpha(Color.Lerp(Color.black, tint, Mathf.Clamp01(strength)), alpha);
    }

    /// <summary>
    /// The same slot at a different opacity. Replaces the
    /// <c>new Color(Palette.X.r, Palette.X.g, Palette.X.b, a)</c> spelling that
    /// was repeated at dozens of call sites; the alpha argument should come from
    /// one of the named alpha tokens on <see cref="PurchaseMenu"/> rather than
    /// being an ad-hoc literal.
    /// </summary>
    public static Color Alpha(Color color, float alpha) => WithAlpha(color, alpha);

    public static Color UpgradeRow(bool hasEnough, bool maxed, bool onSale)
    {
        if (maxed)   return Gold;
        if (onSale)  return Sale;
        return hasEnough ? Accent : Warning;
    }
}

// -
//  UI FACTORY  - thin helpers so build code stays readable
// -
internal static class UI
{
    private static readonly Dictionary<string, Texture2D> _scanlineTextures = new Dictionary<string, Texture2D>();
    private static readonly Dictionary<int, Texture2D> _panelTextures = new Dictionary<int, Texture2D>();
    private static readonly Dictionary<int, Texture2D> _grimeTextures = new Dictionary<int, Texture2D>();

    // - Canvas / root -
    // Reference resolution is 1920/1.25 - 1080/1.25 so every UI unit is
    // rendered 25% larger than the 1920-1080 reference would give (Bug #1).
    // pixelPerfect + a matching referencePixelsPerUnit snaps TMP text to
    // integer pixel positions, which fixes the sub-pixel blur that showed
    // up when the canvas scaled non-integer multiples (Bug #2).
    // Current code keeps the canvas at 1920x1080 and sizes the menu directly.
    public static GameObject MakeCanvas(string name)
    {
        var go     = new GameObject(name);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        canvas.pixelPerfect = true;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode           = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution   = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight    = 0.5f;
        scaler.referencePixelsPerUnit = 100f;
        scaler.dynamicPixelsPerUnit   = 100f;
        go.AddComponent<GraphicRaycaster>();
        return go;
    }

    // - Panels -
    public static GameObject MakePanel(string name, Transform parent,
        Color color, Vector2 anchorMin, Vector2 anchorMax,
        Vector2 offsetMin = default, Vector2 offsetMax = default)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin  = anchorMin;
        rt.anchorMax  = anchorMax;
        rt.offsetMin  = offsetMin;
        rt.offsetMax  = offsetMax;
        return go;
    }

    // - Vanilla bordered panel clone -
    // Clones the vanilla Performance Report LevelUpBox GameObject so our
    // panels match the vanilla game's CRT bordered aesthetic exactly.
    // Falls back to null if the vanilla GameObject can't be found (e.g.
    // we're opening the menu too early). The caller should fall back to
    // MakePanel() in that case.
    private static GameObject _cachedLevelUpBox;

    public static GameObject CloneBorderedPanel(string name, Transform parent,
        Vector2 anchorMin, Vector2 anchorMax,
        Vector2 offsetMin = default, Vector2 offsetMax = default)
    {
        if (_cachedLevelUpBox == null)
        {
            _cachedLevelUpBox = GameObject.Find("/Systems/UI/Canvas/EndgameStats/LevelUp/LevelUpBox");
            if (_cachedLevelUpBox == null)
            {
                // Try a few other known bordered cells
                _cachedLevelUpBox = GameObject.Find("/Systems/UI/Canvas/EndgameStats/Grade")
                                 ?? GameObject.Find("/Systems/UI/Canvas/EndgameStats/DaysInCompany")
                                 ?? GameObject.Find("/Systems/UI/Canvas/EndgameStats/Collected");
            }
            if (_cachedLevelUpBox == null)
                _cachedLevelUpBox = FindInactiveVanillaBorderedPanel();
        }

        if (_cachedLevelUpBox == null) return null;

        var clone = Object.Instantiate(_cachedLevelUpBox, parent);
        clone.name = name;
        clone.SetActive(true);

        // Keep non-text graphic children because vanilla panels often carry
        // their border/grime as child images. Blank text and raycasts only.
        foreach (var tmp in clone.GetComponentsInChildren<TMP_Text>(true))
        {
            tmp.text = "";
            tmp.enabled = false;
        }
        foreach (var graphic in clone.GetComponentsInChildren<Graphic>(true))
            graphic.raycastTarget = false;

        // Kill any animators or layout groups that came along for the ride.
        foreach (var animator in clone.GetComponents<Animator>())
            Object.Destroy(animator);
        foreach (var layout in clone.GetComponents<LayoutGroup>())
            Object.Destroy(layout);
        foreach (var fitter in clone.GetComponents<ContentSizeFitter>())
            Object.Destroy(fitter);

        // Reset the transform so our anchor/offset values take effect.
        var rt = clone.GetComponent<RectTransform>();
        if (rt == null) rt = clone.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return clone;
    }

    private static GameObject FindInactiveVanillaBorderedPanel()
    {
        try
        {
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            string[] preferredNames =
            {
                "LevelUpBox",
                "Collected",
                "Grade",
                "DaysInCompany",
                "EndgameStats"
            };

            for (int p = 0; p < preferredNames.Length; p++)
            {
                string preferred = preferredNames[p];
                for (int i = 0; i < all.Length; i++)
                {
                    GameObject go = all[i];
                    if (go == null || go.scene.name == null)
                        continue;

                    if (go.name.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (go.GetComponent<RectTransform>() != null && go.GetComponentInChildren<Image>(true) != null)
                        return go;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static bool VanillaPanelsAvailable()
    {
        if (_cachedLevelUpBox != null) return true;
        _cachedLevelUpBox = GameObject.Find("/Systems/UI/Canvas/EndgameStats/LevelUp/LevelUpBox");
        if (_cachedLevelUpBox == null)
            _cachedLevelUpBox = GameObject.Find("/Systems/UI/Canvas/EndgameStats/Grade");
        return _cachedLevelUpBox != null;
    }

    // - Text -
    public static TMP_Text MakeText(string name, Transform parent, string text,
        float fontSize, Color color, TextAlignmentOptions align = TextAlignmentOptions.Left)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        var _tf = GetTerminalFont(); if (_tf != null) tmp.font = _tf;
        tmp.text      = text;
        tmp.fontSize  = Mathf.Round(fontSize);
        tmp.color     = color;
        tmp.alignment = align;
        tmp.extraPadding = true;
        tmp.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        tmp.GetComponent<RectTransform>().anchorMax = Vector2.one;
        tmp.GetComponent<RectTransform>().offsetMin = new Vector2(6, 2);
        tmp.GetComponent<RectTransform>().offsetMax = new Vector2(-6, -2);
        return tmp;
    }

    // - Buttons -
    public static (Button btn, TMP_Text label) MakeButton(
        string name, Transform parent,
        string text, float fontSize,
        Color bgColor, Color hlColor, Color textColor,
        Vector2 anchorMin, Vector2 anchorMax,
        Vector2 offsetMin = default, Vector2 offsetMax = default)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bgColor;
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin  = anchorMin;
        rt.anchorMax  = anchorMax;
        rt.offsetMin  = offsetMin;
        rt.offsetMax  = offsetMax;

        var btn   = go.AddComponent<Button>();
        var cb    = btn.colors;
        cb.normalColor      = bgColor;
        cb.highlightedColor = hlColor;
        cb.pressedColor     = hlColor * 0.8f;
        cb.selectedColor    = hlColor;
        cb.disabledColor    = Palette.BtnDisabled;
        cb.fadeDuration     = 0.08f;
        btn.colors          = cb;

        // label child
        var labelGo  = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var lrt = labelGo.AddComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = new Vector2(4, 2);
        lrt.offsetMax = new Vector2(-4, -2);
        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        var _tf = GetTerminalFont(); if (_tf != null) tmp.font = _tf;
        tmp.text                = text;
        tmp.fontSize            = fontSize;
        tmp.color               = textColor;
        tmp.alignment           = TextAlignmentOptions.Center;
        tmp.enableWordWrapping  = false;
        tmp.overflowMode        = TextOverflowModes.Ellipsis;
        tmp.extraPadding        = true;

        return (btn, tmp);
    }

    // - Scroll view -
    // addScrollbar: when true, builds a permanent visible Scrollbar on
    // the right edge wired to scrollRect.verticalScrollbar. Same visual
    // style across all scroll views that use it.
    public static (GameObject scrollGo, Transform content) MakeScrollView(
        string name, Transform parent,
        Vector2 anchorMin, Vector2 anchorMax,
        Vector2 offsetMin, Vector2 offsetMax,
        bool addScrollbar = false)
    {
        // scroll root
        var scrollGo  = new GameObject(name);
        scrollGo.transform.SetParent(parent, false);
        var srt = scrollGo.AddComponent<RectTransform>();
        srt.anchorMin = anchorMin;
        srt.anchorMax = anchorMax;
        srt.offsetMin = offsetMin;
        srt.offsetMax = offsetMax;
        var scrollRect = scrollGo.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;

        // viewport
        var viewport = new GameObject("Viewport");
        viewport.transform.SetParent(scrollGo.transform, false);
        var vrt = viewport.AddComponent<RectTransform>();
        vrt.anchorMin = Vector2.zero;
        vrt.anchorMax = Vector2.one;
        vrt.offsetMin = Vector2.zero;
        // When the scrollbar is on, shrink the viewport's right edge by
        // 14px so the 12px scrollbar has horizontal room.
        vrt.offsetMax = addScrollbar ? new Vector2(-14f, 0f) : Vector2.zero;
        viewport.AddComponent<RectMask2D>();

        // content - plain RectTransform, rows are positioned manually
        var content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        var crt = content.AddComponent<RectTransform>();
        crt.anchorMin = new Vector2(0, 1);
        crt.anchorMax = new Vector2(1, 1);
        crt.pivot     = new Vector2(0.5f, 1f);
        crt.offsetMin = Vector2.zero;
        crt.offsetMax = Vector2.zero;

        scrollRect.viewport       = vrt;
        scrollRect.content        = crt;
        scrollRect.movementType   = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 25f;
        // Anchor content to top immediately so rows are visible on first frame.
        scrollRect.verticalNormalizedPosition = 1f;

        // - Vertical scrollbar (content-driven via Permanent + auto sized handle) -
        // Visible whenever the caller asks for one. The handle's size scales
        // automatically with content/viewport ratio: when content fits, the
        // handle fills the track and is inert (intended).
        Scrollbar vScrollbar = null;
        if (addScrollbar)
        {
            vScrollbar = BuildScrollbar(scrollGo.transform);
            scrollRect.verticalScrollbar = vScrollbar;
            scrollRect.verticalScrollbarVisibility =
                ScrollRect.ScrollbarVisibility.Permanent;
        }

        return (scrollGo, content.transform);
    }

    // Minimal Unity Scrollbar built from primitives - handle on a sliding
    // area inside a colored track. Direction = BottomToTop (top=1, bot=0).
    private static Scrollbar BuildScrollbar(Transform parent)
    {
        var sbGo = new GameObject("VScrollbar");
        sbGo.transform.SetParent(parent, false);
        var sbRt = sbGo.AddComponent<RectTransform>();
        sbRt.anchorMin = new Vector2(1, 0);
        sbRt.anchorMax = new Vector2(1, 1);
        sbRt.pivot     = new Vector2(1, 0.5f);
        sbRt.sizeDelta = new Vector2(12f, 0f);
        sbRt.anchoredPosition = Vector2.zero;
        var sbBg = sbGo.AddComponent<Image>();
        sbBg.color = Palette.ScrollTrack;
        var sb = sbGo.AddComponent<Scrollbar>();
        sb.direction = Scrollbar.Direction.BottomToTop;

        // Sliding area
        var saGo = new GameObject("SlidingArea");
        saGo.transform.SetParent(sbGo.transform, false);
        var saRt = saGo.AddComponent<RectTransform>();
        saRt.anchorMin = Vector2.zero;
        saRt.anchorMax = Vector2.one;
        saRt.offsetMin = new Vector2(2f, 2f);
        saRt.offsetMax = new Vector2(-2f, -2f);

        // Handle
        var hGo = new GameObject("Handle");
        hGo.transform.SetParent(saGo.transform, false);
        var hRt = hGo.AddComponent<RectTransform>();
        hRt.anchorMin = Vector2.zero;
        hRt.anchorMax = Vector2.one;
        hRt.offsetMin = Vector2.zero;
        hRt.offsetMax = Vector2.zero;
        var hImg = hGo.AddComponent<Image>();
        hImg.color = Palette.ScrollHandle;

        sb.targetGraphic = hImg;
        sb.handleRect    = hRt;
        return sb;
    }

    // Caller sets content.sizeDelta to the final height THEN calls this.
    //
    // Bug 3 history: this used to call LayoutRebuilder.ForceRebuildLayoutImmediate
    // inline, but if the content tree contains a TMP_InputField (the trade
    // view does, and other views may add one in the future) Unity logs
    // "Trying to add Caret (TMP_SelectionCaret) for graphic rebuild while we
    // are already inside a graphic rebuild loop. This is not supported." -
    // the caret's rebuild request is silently dropped, the ScrollRect ends
    // up with stale viewport/content metrics, and the next scroll input
    // jumps the view to the bottom.
    //
    // The fix is temporal, not structural: skip the inline rebuild, set the
    // anchored position directly, and re-clamp the normalized position on
    // the next two frames once Unity has finished its own pending rebuilds.
    public static void ResetScrollToTop(ScrollRect sr, RectTransform contentRt)
    {
        if (sr == null || contentRt == null) return;
        contentRt.anchoredPosition = Vector2.zero;
        sr.verticalNormalizedPosition = 1f;

        // Defer the re-clamp to the next frame via the menu's own
        // MonoBehaviour, since UI is a static helper class. If we can't find
        // an active MonoBehaviour to host the coroutine (menu in teardown,
        // for example) the immediate set above is still in effect.
        var host = sr.GetComponentInParent<MenuController>();
        if (host != null && host.isActiveAndEnabled)
            host.StartCoroutine(ResetScrollNextFrame(sr));
    }

    private static System.Collections.IEnumerator ResetScrollNextFrame(ScrollRect sr)
    {
        // Two-frame wait: one for layout to settle, one for any TMP caret /
        // ContentSizeFitter pass to finish. After that the ScrollRect's
        // metrics are correct and the clamp pins to the top reliably.
        yield return null;
        yield return null;
        if (sr != null) sr.verticalNormalizedPosition = 1f;
    }

    // - Input field -
    public static TMP_InputField MakeInputField(string name, Transform parent,
        string placeholder, float fontSize,
        Vector2 anchorMin, Vector2 anchorMax,
        Vector2 offsetMin, Vector2 offsetMax)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = Palette.BgInput;
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;

        // text area
        var ta  = new GameObject("TextArea");
        ta.transform.SetParent(go.transform, false);
        var tart = ta.AddComponent<RectTransform>();
        tart.anchorMin = Vector2.zero;
        tart.anchorMax = Vector2.one;
        tart.offsetMin = new Vector2(6, 2);
        tart.offsetMax = new Vector2(-6, -2);
        ta.AddComponent<RectMask2D>();

        // display text
        var dt  = new GameObject("Text");
        dt.transform.SetParent(ta.transform, false);
        var drt = dt.AddComponent<RectTransform>();
        drt.anchorMin = Vector2.zero;
        drt.anchorMax = Vector2.one;
        drt.offsetMin = Vector2.zero;
        drt.offsetMax = Vector2.zero;
        var dtmp = dt.AddComponent<TextMeshProUGUI>();
        var _tf = GetTerminalFont(); if (_tf != null) dtmp.font = _tf;
        dtmp.fontSize  = fontSize;
        dtmp.color     = Palette.Accent;
        dtmp.alignment = TextAlignmentOptions.MidlineLeft;

        // placeholder
        var ph  = new GameObject("Placeholder");
        ph.transform.SetParent(ta.transform, false);
        var prt = ph.AddComponent<RectTransform>();
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero;
        prt.offsetMax = Vector2.zero;
        var ptmp = ph.AddComponent<TextMeshProUGUI>();
        var _tf2 = GetTerminalFont(); if (_tf2 != null) ptmp.font = _tf2;
        ptmp.text      = placeholder;
        ptmp.fontSize  = fontSize;
        ptmp.color     = Palette.Dim;
        ptmp.alignment = TextAlignmentOptions.MidlineLeft;
        ptmp.fontStyle = FontStyles.Italic;

        var field = go.AddComponent<TMP_InputField>();
        field.textViewport   = tart;
        field.textComponent  = dtmp;
        field.placeholder    = ptmp;
        field.contentType    = TMP_InputField.ContentType.IntegerNumber;
        field.text           = "0";

        return field;
    }

    // - Game font helper -
    // Searches multiple sources for the Lethal Company monospace font
    // (3270-Regular SDF). Terminal may not be loaded when the menu opens,
    // so we also check HUDManager and all loaded TMP_FontAssets as fallbacks.
    private static TMP_FontAsset _terminalFont;
    public static TMP_FontAsset GetTerminalFont()
    {
        if (_terminalFont != null) return _terminalFont;

        TMP_FontAsset found = null;

        // 1. Try Terminal (original approach)
        var terminal = Object.FindObjectOfType<Terminal>();
        if (terminal != null)
        {
            var src = terminal.GetComponentInChildren<TMP_Text>();
            if (src?.font != null) found = src.font;
        }

        // 2. Try HUDManager - its text elements use the same game font
        if (found == null && HUDManager.Instance != null)
        {
            var src = HUDManager.Instance.GetComponentInChildren<TMP_Text>();
            if (src?.font != null) found = src.font;
        }

        // 3. Search all loaded TMP_FontAssets for the game's "3270" font by name
        if (found == null)
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f.name.Contains("3270"))
                {
                    found = f;
                    break;
                }
            }
        }

        // 4. Last resort: grab any TMP_Text font from the scene
        if (found == null)
        {
            var anyTmp = Object.FindObjectOfType<TMP_Text>();
            if (anyTmp?.font != null) found = anyTmp.font;
        }

        if (found == null) return null;

        // Use the original font directly - cloning corrupts the glyph atlas
        // and causes missing characters (U, D, d, etc.). The "atlas not readable"
        // console warnings from TMP are harmless.
        _terminalFont = found;
        return _terminalFont;
    }

    // - CRT scanline overlay -
    public static void AddScanlines(Transform parent, float alpha = 0.06f, int lineHeight = 2, bool overlay = false)
    {
        var go = new GameObject("Scanlines");
        go.transform.SetParent(parent, false);

        // Stretch over entire parent
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // Use RawImage so we can tile the texture via uvRect
        var raw    = go.AddComponent<RawImage>();
        raw.texture = GetScanlineTexture(alpha, lineHeight);
        raw.color   = Color.white;
        // Tile vertically: ~270 repetitions to cover any panel height
        raw.uvRect  = new Rect(0, 0, 1, 270);

        // Don't block mouse clicks
        raw.raycastTarget = false;

        if (overlay) go.transform.SetAsLastSibling();
        else go.transform.SetAsFirstSibling();
    }

    public static void AddCrtGrime(Transform parent, float alpha = 0.07f)
    {
        var go = new GameObject("CrtGrime");
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var raw = go.AddComponent<RawImage>();
        raw.texture = GetGrimeTexture(alpha);
        raw.color = Color.white;
        raw.uvRect = new Rect(0, 0, 8, 8);
        raw.raycastTarget = false;
        go.transform.SetAsLastSibling();
    }

    public static void AddPanelTexture(Transform parent, float alpha = 0.10f, int seed = 0)
    {
        var go = new GameObject("PanelTexture");
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var raw = go.AddComponent<RawImage>();
        raw.texture = GetPanelTexture(alpha);
        raw.color = Color.white;
        float u = ((seed & 0xFF) / 255f) * 0.38f;
        float v = (((seed >> 8) & 0xFF) / 255f) * 0.38f;
        raw.uvRect = new Rect(u, v, 5, 5);
        raw.raycastTarget = false;
        go.transform.SetAsFirstSibling();
    }

    private static Texture2D GetScanlineTexture(float alpha, int lineHeight)
    {
        string key = lineHeight + ":" + Mathf.RoundToInt(alpha * 1000f);
        if (_scanlineTextures.TryGetValue(key, out var cached) && cached != null)
            return cached;

        int stripeH = Mathf.Max(1, lineHeight);
        int texH = stripeH * 4;
        var tex = new Texture2D(1, texH, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Repeat;
        for (int y = 0; y < texH; y++)
        {
            Color px = Color.clear;
            if (y < stripeH)
                px = new Color(0f, 0f, 0f, alpha);
            else if (y == stripeH)
                px = new Color(0f, 0f, 0f, alpha * 0.35f);

            tex.SetPixel(0, y, px);
        }
        tex.Apply();
        _scanlineTextures[key] = tex;
        return tex;
    }

    private static Texture2D GetGrimeTexture(float alpha)
    {
        int key = Mathf.RoundToInt(alpha * 1000f) * 10 + (int)UiTheme.CurrentTheme;
        if (_grimeTextures.TryGetValue(key, out var cached) && cached != null)
            return cached;

        const int w = 96;
        const int h = 96;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Repeat;

        var rng = new System.Random(9174);
        Color textureAccent = Palette.Accent;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = 0f;
                if (rng.NextDouble() < 0.018) a = alpha * (0.30f + (float)rng.NextDouble() * 0.45f);
                if (y % 29 == 0 && rng.NextDouble() < 0.10) a = Mathf.Max(a, alpha * 0.35f);
                if (x % 73 == 0 && rng.NextDouble() < 0.06) a = Mathf.Max(a, alpha * 0.22f);

                tex.SetPixel(x, y, new Color(textureAccent.r, textureAccent.g, textureAccent.b, a));
            }
        }
        tex.Apply();
        _grimeTextures[key] = tex;
        return tex;
    }

    private static Texture2D GetPanelTexture(float alpha)
    {
        int key = Mathf.RoundToInt(alpha * 1000f) * 10 + (int)UiTheme.CurrentTheme;
        if (_panelTextures.TryGetValue(key, out var cached) && cached != null)
            return cached;

        const int w = 128;
        const int h = 128;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Repeat;

        var rng = new System.Random(43110);
        Color textureAccent = Palette.Accent;
        for (int y = 0; y < h; y++)
        {
            bool scratchBand = y % 43 == 0 || y % 79 == 0;
            for (int x = 0; x < w; x++)
            {
                double n = rng.NextDouble();
                Color px = Color.clear;

                if (scratchBand && n < 0.22)
                    px = new Color(textureAccent.r, textureAccent.g, textureAccent.b, alpha * 0.18f);

                if (n < 0.010)
                    px = new Color(0f, 0f, 0f, alpha * (0.30f + (float)rng.NextDouble() * 0.40f));
                else if (n > 0.998)
                    px = new Color(textureAccent.r, textureAccent.g, textureAccent.b, alpha * 0.30f);

                tex.SetPixel(x, y, px);
            }
        }
        tex.Apply();
        _panelTextures[key] = tex;
        return tex;
    }

    // - Thin separator line -
    public static void MakeSeparator(Transform parent, float topOffset)
    {
        var go  = new GameObject("Separator");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = Palette.Separator;
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0,   1);
        rt.anchorMax = new Vector2(1,   1);
        rt.pivot     = new Vector2(0.5f, 1);
        rt.offsetMin = new Vector2(8,  -topOffset - 1);
        rt.offsetMax = new Vector2(-8, -topOffset);
    }

    // - Sound effects -
    //
    // Every clip here is resolved from a *typed field on a known vanilla
    // component*. Nothing is ever picked by scanning the loaded AudioClip set
    // by name: Resources.FindObjectsOfTypeAll<AudioClip>() reaches every clip
    // every other installed mod has loaded, and matching on generic substrings
    // hands our UI whatever they happened to name their assets.
    // loaforc-FacilityMeltdown names its announcement lines warning1..warning4,
    // which matched the old "warning" deny substring - so a cannot-afford press
    // played the meltdown voice line instead of a blip (#216).
    //
    // Every clip reference below is a UnityEngine.Object, so it can be
    // *fake-null*: alive to C#, destroyed to Unity. `??`, `?.` and `!= null`
    // patterns that bypass or invert Unity's overloaded operator are avoided
    // throughout - go through Live() instead.
    private static AudioClip _hoverClip;
    private static AudioClip _confirmClip;
    private static AudioClip _denyClip;
    private static AudioClip _closeClip;

    // Set once every slot is filled *and* the confirm slot holds the terminal's
    // own buy clip, which is the best available confirmation and may not exist
    // yet on the first attempt.
    private static bool  _soundResolved;
    private static bool  _confirmFromTerminal;
    private static float _nextSoundResolveTime;

    // Resolving touches FindObjectOfType and reflection, and the deny path is
    // the one players mash. Until everything resolves, retry at most this often
    // rather than on every click.
    private const float SoundResolveInterval = 5f;

    private static Terminal  _terminal;
    private static FieldInfo _terminalBuyField;
    private static bool      _terminalBuyFieldSearched;

    /// <summary>
    /// Unity-lifetime-aware liveness test. A destroyed <c>AudioClip</c> is
    /// non-null to C# but null to Unity; this goes through Unity's overloaded
    /// operator so both cases read as dead.
    /// </summary>
    private static bool Live(AudioClip clip) => !(clip == null);

    private static AudioClip FirstLive(AudioClip a, AudioClip b, AudioClip c)
    {
        if (Live(a)) return a;
        if (Live(b)) return b;
        if (Live(c)) return c;
        return null;
    }

    private static void ResolveSounds()
    {
        // A latched set can still go stale: the clips are owned by scene
        // objects, so a scene transition can destroy them out from under the
        // cache. Re-resolve rather than playing nothing forever.
        if (_soundResolved)
        {
            if (Live(_denyClip) && Live(_closeClip) && Live(_hoverClip) && Live(_confirmClip))
                return;

            _soundResolved = false;
            _confirmFromTerminal = false;
            _nextSoundResolveTime = 0f;
        }

        if (!Live(_denyClip))    _denyClip    = null;
        if (!Live(_closeClip))   _closeClip   = null;
        if (!Live(_hoverClip))   _hoverClip   = null;
        if (!Live(_confirmClip)) _confirmClip = null;

        float now = Time.unscaledTime;
        if (now < _nextSoundResolveTime) return;
        _nextSoundResolveTime = now + SoundResolveInterval;

        // ShipBuildModeManager owns the vanilla UI blip bank: denyPlacementSFX
        // is the refusal buzz the game itself plays when a placement is
        // rejected, which is exactly the semantics of a refused purchase.
        var build = ShipBuildModeManager.Instance;
        if (!(build == null))
        {
            if (!Live(_denyClip))    _denyClip    = build.denyPlacementSFX;
            if (!Live(_closeClip))   _closeClip   = build.cancelPlacementSFX;
            if (!Live(_hoverClip))   _hoverClip   = build.beginPlacementSFX;
            if (!Live(_confirmClip)) _confirmClip = build.storeItemSFX;
        }

        // The terminal's own buy sound is the better confirmation, so it keeps
        // being attempted until it lands - otherwise the placement blip that
        // filled the slot above would latch it shut forever.
        if (!_confirmFromTerminal)
        {
            AudioClip terminalBuy = FindTerminalBuyClip();
            if (Live(terminalBuy))
            {
                _confirmClip = terminalBuy;
                _confirmFromTerminal = true;
            }
        }

        // Whatever did resolve covers the slots that did not. A refused click
        // must never be silent - the wrong blip is a papercut, no feedback at
        // all reads as the button being broken.
        if (!Live(_denyClip))    _denyClip    = FirstLive(_closeClip, _hoverClip, _confirmClip);
        if (!Live(_closeClip))   _closeClip   = FirstLive(_hoverClip, _denyClip, _confirmClip);
        if (!Live(_hoverClip))   _hoverClip   = FirstLive(_closeClip, _denyClip, _confirmClip);
        if (!Live(_confirmClip)) _confirmClip = FirstLive(_hoverClip, _closeClip, _denyClip);

        _soundResolved = _confirmFromTerminal
            && Live(_denyClip) && Live(_closeClip) && Live(_hoverClip);
    }

    /// <summary>
    /// One shared Terminal reference; re-found only once the cached one has
    /// been destroyed.
    /// </summary>
    private static Terminal GetTerminal()
    {
        if (_terminal == null)
            _terminal = Object.FindObjectOfType<Terminal>();
        return _terminal;
    }

    /// <summary>
    /// Terminal-owned lookup for the purchase "ka-ching": a typed AudioClip
    /// field named buy/purchase first, then the Terminal's own
    /// <c>syncedAudios</c> array. Name matching inside <c>syncedAudios</c> is
    /// safe because that array only ever holds the Terminal prefab's own
    /// clips - it is not a global search. The reflection lookup runs once per
    /// process; the array walk runs at most once per throttled resolve pass.
    /// </summary>
    private static AudioClip FindTerminalBuyClip()
    {
        Terminal terminal = GetTerminal();
        if (terminal == null) return null;

        if (!_terminalBuyFieldSearched)
        {
            _terminalBuyFieldSearched = true;
            foreach (var f in typeof(Terminal).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType != typeof(AudioClip)) continue;
                string fieldName = f.Name;
                if (fieldName.IndexOf("buy", StringComparison.OrdinalIgnoreCase) >= 0
                    || fieldName.IndexOf("purchase", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _terminalBuyField = f;
                    break;
                }
            }
        }

        if (_terminalBuyField != null
            && _terminalBuyField.GetValue(terminal) is AudioClip fieldClip
            && Live(fieldClip))
            return fieldClip;

        AudioClip[] synced = terminal.syncedAudios;
        if (synced != null)
        {
            foreach (AudioClip c in synced)
            {
                if (!Live(c)) continue;
                string clipName = c.name;
                if (clipName.IndexOf("purchase", StringComparison.OrdinalIgnoreCase) >= 0
                    || clipName.IndexOf("buy", StringComparison.OrdinalIgnoreCase) >= 0
                    || clipName.IndexOf("cash", StringComparison.OrdinalIgnoreCase) >= 0)
                    return c;
            }
        }

        return null;
    }

    public static void PlayHover()   { ResolveSounds(); PlayClip(_hoverClip, 0.55f, _closeClip); }
    public static void PlayConfirm() { ResolveSounds(); PlayClip(_confirmClip, 0.7f, _hoverClip); }

    /// <summary>
    /// The refusal buzz. <see cref="MenuAudio.PlayDeny"/> is the call every
    /// purchase-refusal path should use; this is its vanilla-derived fallback.
    /// Returns false when nothing vanilla-owned had resolved yet, so the
    /// caller can cover the gap rather than leave the click silent.
    /// </summary>
    public static bool PlayDeny()    { ResolveSounds(); return PlayClip(_denyClip, 0.6f, _closeClip); }

    /// <summary>Closing/backing out - not a refusal.</summary>
    public static void PlayClose()   { ResolveSounds(); PlayClip(_closeClip, 0.6f, _hoverClip); }

    /// <summary>
    /// The confirmation "ka-ching". Same clip and resolution path as
    /// <see cref="PlayConfirm"/> - kept as its own name because the authored
    /// bank in <see cref="MenuAudio"/> distinguishes the two.
    /// </summary>
    public static void PlayPurchase() => PlayConfirm();

    // Callers resolve first, then pass the field - resolving here would be too
    // late, since the arguments are already evaluated.
    private static bool PlayClip(AudioClip clip, float volume, AudioClip fallback = null)
    {
        AudioClip chosen = Live(clip) ? clip : fallback;
        if (!Live(chosen)) return false;

        HUDManager hud = HUDManager.Instance;
        if (hud == null || hud.UIAudio == null) return false;

        hud.UIAudio.PlayOneShot(chosen, volume);
        return true;
    }
}

// -
//  PURCHASE MENU
// -
public class PurchaseMenu
{
    // -
    //  DESIGN TOKENS
    // -
    // Three scales - spacing, typography, alpha - plus the panel/section sizes
    // derived from them. Everything the menu builds should reach for a token
    // here instead of writing a literal or doing arithmetic on another token
    // ("FontLg + 6f" used to be how new sizes were invented). The values are the
    // ones this menu already used; where two literals sat a fraction apart they
    // were snapped onto one step, which is the point of having a scale.

    // - Spacing scale -
    // Not a doubling scale: these are the gaps the menu actually uses, named in
    // increasing order so a call site can move one step without inventing a
    // number.
    private const float Space1 =  4f;   // hairline gap, row inner padding
    private const float Space2 =  8f;   // tight gap, button inset
    private const float Space3 = 12f;   // gap between stacked section panels
    private const float Space4 = 14f;   // inspector side inset, cell padding
    private const float Space5 = 18f;   // body padding around the content columns
    private const float Space6 = 26f;   // gap between the rail / list / inspector columns

    // - Typography scale -
    // FontSm/FontMd/FontLg keep their names because most of the menu is written
    // in them. The rest exist so nothing has to say "FontLg + 6f" again.
    private const float FontNano  =  9f;   // hard legibility floor for autosized labels
    private const float FontMicro = 10f;   // metric labels shrunk by autosize
    private const float FontTiny  = 11f;   // dense sub-labels (collection rows)
    private const float FontXs    = 12f;   // footer hint, ammo labels
    private const float FontSm    = 14f;
    private const float FontMd    = 16f;
    private const float FontMdLg  = 18f;   // rail and class buttons
    private const float FontLg    = 20f;
    private const float FontXl    = 22f;   // currency readout
    private const float FontTitle = 24f;   // section titles (PERFORMANCE RECORD)
    private const float FontHuge  = 26f;   // the EMPLOYEE FILE header

    // - Surface alpha scale -
    // Opacity of the procedural grain laid over a panel. Five steps; the old
    // 0.025/0.028 and 0.032 one-offs snap onto Faint and Mid respectively.
    private const float TexTrace  = 0.017f;  // barely-there wash on tree panels and list rows
    private const float TexFaint  = 0.026f;
    private const float TexLow    = 0.030f;
    private const float TexMid    = 0.034f;
    private const float TexHigh   = 0.040f;
    private const float TexStrong = 0.052f;

    // - Fill alpha scale -
    // Opacity of a palette slot used as a panel/row fill. The 0.2x steps are
    // barely-there washes behind a whole view; the 0.4-0.9 steps are rows and
    // bands that must read as distinct stripes.
    private const float FillWash    = 0.22f;  // rounded frame strokes
    private const float FillHint    = 0.26f;  // outer frame on a view background
    private const float FillGhost   = 0.30f;  // view background behind a scroll list
    private const float FillMuted   = 0.42f;  // progress track
    private const float FillSoft    = 0.48f;  // odd rows
    private const float FillRow     = 0.52f;  // even rows (dense lists)
    private const float FillRowAlt  = 0.58f;  // even rows (ammo list)
    private const float FillBand    = 0.76f;  // performance record rows
    private const float FillPlate   = 0.82f;  // inactive rail button
    private const float FillStrong  = 0.92f;  // rail button hover
    private const float FillOpaque  = 0.98f;  // active rail button

    // - Panel and section sizes -
    private const float PanelW       = 1180f;
    private const float PanelH       = 820f;
    private const float HeaderH      = 118f;
    private const float FooterH      =  44f;
    private const float RowH         =  34f;
    private const int   LowCurrency  =  50;
    private const float LeftRailW    = 170f;
    private const float InspectorW   = 360f;
    private const float BodyPad      = Space5;
    private const float ColumnGap    = Space6;

    // - Right inspector stack -
    // The inspector is built top-down by InspectorStack: every renderer starts
    // its cursor at InspectorTop, reserves the preview, then appends sections.
    // Nothing below the preview is at a fixed offset any more, so a section that
    // grows (a sixth ammunition family, an eighth collection row) pushes the
    // rest down instead of drawing past its own panel.
    private const float InspectorTop    = Space4;   // top inset of the first section
    private const float InspectorSide   = Space4;   // left/right inset of every section
    private const float InspectorGap    = Space2;   // gap between stacked sections
    private const float PreviewH        = 222f;     // player render box
    private const float InspectorNameH  =  38f;     // selected-item name header
    private const float InspectorBandH  =  44f;     // three-metric status band
    private const float InspectorFootH  =  42f;     // purchase button reserve at the bottom

    // - Employee File performance record -
    // The record is a scroll strip under the title card, so these describe one
    // row rather than an absolute offset into the view.
    private const float RecordListTop         = 76f;   // below the PERFORMANCE RECORD title card
    private const float RecordRowH            = 46f;   // icon + label/context + value
    private const float RecordClusterHeaderH  = 20f;   // FIELD WORK / COMBAT / ...
    private const float RecordIconSize        = 28f;
    private const float RecordTextLeft        = Space3 + RecordIconSize + Space3;

    // - Skill tree tier captions -
    // A caption band sits in the gap above each tier row, inside the tree glass.
    // 16px keeps the caption above the legibility floor while clearing the 18px
    // gap TierRowH leaves between one row's nodes and the next row's top.
    private const float TierLabelH     = 16f;
    private const float TierLabelInset = 10f;

    // - Skill tree vertical stack -
    // Top down: class tabs, clear space, the tree glass, then the first tier's
    // caption band sitting inside the glass above the first row of nodes.
    // SkillTreeLayout.TreeTop is derived from these rather than typed as a
    // literal, so moving the tabs or the gap can never silently close the gap
    // or push the glass under them.
    private const float SkillTreeTabsTop   = 8f;
    private const float SkillTreeTabsH     = 44f;
    private const float SkillTreeTabsGap   = 10f;  // clear space below the tabs
    private const float SkillTreeTabRadius = 10f;
    private const float SkillTreeGlassRise = 26f;  // glass top, measured up from TreeTop
    private const float SkillTreeGateRise  = 28f;  // tier-gate rail top, up from TreeTop

    // - Skill tree node internals -
    // A node box is 94x94 and stacks, bottom up: level pips, name, icon. There
    // is no state-marker band (#258) - cost, gate and level detail live in the
    // right inspector, and the node itself carries state in fill, stroke, group
    // alpha and pip fill only. The bands below are measured from the bottom edge
    // and must not overlap - the icon is the only one that grows on selection.
    private const float NodeNameBottom       = 18f;
    private const float NodeNameTop          = 46f;
    private const float NodeIconSize         = 40f;
    private const float NodeIconSelectedSize = 44f;
    private const float NodeIconCenterY      = -27f;  // from the node's top edge
    // Bottom of the description when a purchase-refusal notice occupies the strip above the button.
    private const float PurchaseNoticeTop = 76f;
    private const float PurchaseNoticeDuration = 5f;
    private static readonly bool EmotePreviewDiagnostics = false;
    private const string CurrencySingular = "TOKEN";
    private const string CurrencyPlural   = "TOKENS";
    private static readonly Dictionary<int, Texture2D> _ruggedLineTextures =
        new Dictionary<int, Texture2D>();
    private static readonly Dictionary<int, Sprite> _roundedFillSprites =
        new Dictionary<int, Sprite>();
    private static readonly Dictionary<int, Sprite> _roundedBorderSprites =
        new Dictionary<int, Sprite>();

    // - Category tabs -
    // Internal const names (CatDexterity / CatCombat / CatScavenging) are kept
    // intact so existing code paths and saved state references don't move; only
    // the player-facing display strings changed in the upgrade-reorg pass.
    private const string CatDexterity  = "Athletics";
    private const string CatCombat     = "Combat";
    private const string CatScavenging = "Utility";

    // - Sub-categories (within each tab) -
    private const string SubSpeed       = "Speed";
    private const string SubDexterity   = "Dexterity";
    private const string SubLethality   = "Lethality";
    private const string SubSurvival    = "Survival";
    private const string SubLogistics   = "Logistics";
    private const string SubOperations  = "Operations";

    // Sub-category - main category. Single source of truth for routing tabs.
    private static readonly Dictionary<string, string> SubCategoryMain =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { SubSpeed,       CatDexterity },
        { SubDexterity,   CatDexterity },
        { SubLethality,   CatCombat    },
        { SubSurvival,    CatCombat    },
        { SubLogistics,   CatScavenging },
        { SubOperations,  CatScavenging },
    };

    // Order of sub-categories displayed within each main tab.
    private static readonly Dictionary<string, string[]> SubCategoriesByMain =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        { CatDexterity,   new[] { SubSpeed, SubDexterity } },
        { CatCombat,      new[] { SubLethality, SubSurvival } },
        { CatScavenging,  new[] { SubLogistics, SubOperations } },
    };

    // Upgrade name - sub-category. Edit this map to re-route an upgrade.
    // The main-category routing for each upgrade is derived from this via
    // SubCategoryMain - there's no separate per-upgrade tab assignment.
    private static readonly Dictionary<string, string> UpgradeSubCategories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Athletics
        { "Adrenaline Rush",       SubSpeed },
        { "Deathbound",            SubSurvival },
        { "Deeper Pockets",        SubDexterity },
        { "Resilience",            SubDexterity },
        { "Sprinter",              SubSpeed },
        { "Transporter",           SubDexterity },

        // Combat
        { "Lethal Hands",          SubLethality },
        { "Lethal Hands Training", SubLethality },
        { "Bait Bomb",             SubLethality },
        { "Bait Beacon",           SubLethality },
        { "Pumping Iron",          SubLethality },

        // Utility
        { "Field Optics",          SubLogistics },
        { "Buddy System",          SubLogistics },
        { "Command Net",           SubOperations },
        { "Field Mechanic",        SubOperations },
        { "Field Operations",      SubOperations },
        { "Inspire",               SubOperations },
        { "Chameleon",             SubOperations },
        { "Scavenger",             SubLogistics },
        { "Overachiever",          SubOperations },
        { "Veteran",               SubOperations },
        { "Quick Hands",           SubLogistics },
        { "Light Feet",            SubOperations },
        { "Lone Wolf",             SubOperations },
        { "Ping",                  SubLogistics },
        { "Shadow Step",           SubOperations },
        { "Sixth Sense",           SubLogistics },
        { "Squad Sight",           SubLogistics },
        { "Worklight Beacon",      SubOperations },
    };

    // Accent colours for sub-category header bars. Hex values from the spec -
    // shift here if any read poorly against the dark-red panel background.
    private static readonly Dictionary<string, Color> SubCategoryColors =
        new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        { CatDexterity,   Palette.Accent },
        { CatCombat,      Palette.Accent },
        { CatScavenging,  Palette.Accent },
        { SubSpeed,       Palette.Dim },
        { SubDexterity,   Palette.Dim },
        { SubLethality,   Palette.Dim },
        { SubSurvival,    Palette.Dim },
        { SubLogistics,   Palette.Dim },
        { SubOperations,  Palette.Dim },
    };

    // Deliberately removed: the Hex(string) helper that used to parse baked
    // color literals. Every color in this menu now comes from the palette, and
    // reintroducing a hex parser is how off-theme colors get back in.

    private static string GetSubCategory(string upgradeName)
    {
        return upgradeName != null && UpgradeSubCategories.TryGetValue(upgradeName, out var sub) ? sub : null;
    }

    // Resolves the accent colour used for an upgrade's row tint and
    // detail-panel header. Sub-category accent takes precedence; the
    // legacy UpgradeColorType palette is only consulted for upgrades
    // outside the sub-category mapping (defensive - shouldn't trigger
    // for the 29 routed upgrades).
    private static Color GetRowAccentColor(string upgradeName)
    {
        var sub = GetSubCategory(upgradeName);
        if (sub != null && SubCategoryColors.TryGetValue(sub, out var accent))
            return accent;
        return GetUpgradeColor(upgradeName);
    }

    // Upgrades that should never appear in any tab - vanilla Y4NGZ entries not in this modpack.
    private static readonly HashSet<string> HiddenUpgrades = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Oxygen Canisters",
        "Beekeeper",
        "Hollow Point",
        "Long Barrel",
        "Locksmith",
        "Mechanical Arms",
        "Sleight of Hand",
        "Predator Instinct",
        "Salvager",
    };

    // Main-category routing is derived directly from UpgradeSubCategories so
    // there's only one place to edit when an upgrade moves. Unknown upgrades
    // fall through to CatScavenging via GetCategory below.
    private static readonly Dictionary<string, string> UpgradeCategories =
        UpgradeSubCategories.ToDictionary(
            kv => kv.Key,
            kv => SubCategoryMain[kv.Value],
            StringComparer.OrdinalIgnoreCase);


    private static string GetCategory(string upgradeName)
    {
        return UpgradeCategories.TryGetValue(upgradeName, out var cat) ? cat : CatScavenging;
    }

    // - Upgrade type classification -
    // Each upgrade maps to a "type" that drives row tint + sort order.
    // Types are independent from categories - a Combat tab upgrade may be
    // Offense, Defense, or Stealth; a Dexterity tab one may be Speed or
    // Defense.  Within a category we sort by type, so the visual grouping
    // is colour-coded.
    public enum UpgradeColorType { Speed, Defense, Offense, Stealth, Loot, Team }

    private static readonly Dictionary<string, UpgradeColorType> UpgradeColors =
        new Dictionary<string, UpgradeColorType>(StringComparer.OrdinalIgnoreCase)
    {
        // - Dexterity & Speed -
        { "Adrenaline Rush",       UpgradeColorType.Speed },
        { "Deathbound",            UpgradeColorType.Defense },
        { "Deeper Pockets",        UpgradeColorType.Loot },
        { "Resilience",            UpgradeColorType.Defense },
        { "Sprinter",              UpgradeColorType.Speed },
        { "Transporter",           UpgradeColorType.Loot },

        // - Combat & Survival -
        // Offense (Red)
        { "Lethal Hands",          UpgradeColorType.Offense },
        { "Lethal Hands Training", UpgradeColorType.Offense },
        { "Lethal Fists",          UpgradeColorType.Offense },
        { "Pumping Iron",          UpgradeColorType.Offense },
        // Stealth (Green)
        { "Shadow Step",           UpgradeColorType.Stealth },
        { "Chameleon",             UpgradeColorType.Stealth },

        // - Scavenging & Utility -
        // Loot (Orange)
        { "Field Optics",          UpgradeColorType.Loot },
        { "Field Mechanic",        UpgradeColorType.Loot },
        { "Field Operations",      UpgradeColorType.Team },
        { "Quick Hands",           UpgradeColorType.Loot },
        { "Scavenger",             UpgradeColorType.Loot },
        { "Light Feet",            UpgradeColorType.Speed },
        { "Sixth Sense",           UpgradeColorType.Team },
        { "Squad Sight",           UpgradeColorType.Team },
        // Team (Purple)
        { "Buddy System",          UpgradeColorType.Team },
        { "Command Net",           UpgradeColorType.Team },
        { "Inspire",               UpgradeColorType.Team },
        { "Overachiever",          UpgradeColorType.Team },
        { "Veteran",               UpgradeColorType.Team },
        { "Ping",                  UpgradeColorType.Team },
        { "Worklight Beacon",      UpgradeColorType.Team },
        { "Lone Wolf",             UpgradeColorType.Team },
    };

    // Sort order used inside a category - groups upgrades by type color.
    private static readonly Dictionary<UpgradeColorType, int> ColorSortOrder =
        new Dictionary<UpgradeColorType, int>
    {
        { UpgradeColorType.Speed,   0 },
        { UpgradeColorType.Defense, 1 },
        { UpgradeColorType.Offense, 2 },
        { UpgradeColorType.Stealth, 3 },
        { UpgradeColorType.Loot,    4 },
        { UpgradeColorType.Team,    5 },
    };

    public static UpgradeColorType? GetUpgradeColorType(string upgradeName)
    {
        if (upgradeName != null && UpgradeColors.TryGetValue(upgradeName, out var t))
            return t;
        return null;
    }

    public static Color GetUpgradeColor(string upgradeName)
    {
        var t = GetUpgradeColorType(upgradeName);
        if (!t.HasValue) return Palette.Accent;  // Unknown - fall back to default accent
        switch (t.Value)
        {
            case UpgradeColorType.Speed:   return Palette.TypeSpeed;
            case UpgradeColorType.Defense: return Palette.TypeDefense;
            case UpgradeColorType.Offense: return Palette.TypeOffense;
            case UpgradeColorType.Stealth: return Palette.TypeStealth;
            case UpgradeColorType.Loot:    return Palette.TypeLoot;
            case UpgradeColorType.Team:    return Palette.TypeTeam;
            default: return Palette.Accent;
        }
    }

    public static int GetUpgradeColorSortOrder(string upgradeName)
    {
        var t = GetUpgradeColorType(upgradeName);
        if (t.HasValue && ColorSortOrder.TryGetValue(t.Value, out var ord))
            return ord;
        return 99;  // Unknown types sort last
    }

    // - State -
    private static GameObject       _root;
    private static TMP_Text         _currencyText;
    private static Transform        _scrollContent;
    private static ScrollRect       _scrollRect;
    private static GameObject       _listView;
    private static GameObject       _detailView;
    private static GameObject       _tradeView;
    private static GameObject       _playerLevelView;
    private static GameObject       _employeeFileView;
    private static Button           _employeeFileButton;
    private static TMP_Text         _employeeFileLabel;
    // Footer keyboard legend. Rewritten per view by SetFooterHint so the
    // advertised bindings are always the ones HandleKeyboardNavigation runs.
    private static TMP_Text         _footerHint;
    // The single keyboard focus ring, re-parented onto whichever target holds
    // focus. One instance, so moving focus costs a SetParent and no allocation.
    private static GameObject       _focusRing;
    // The view the rail is currently pointing at. Section identity is resolved
    // through the registry rather than through one static field per tab.
    private static GameObject       _activeSectionView;
    private static UpgradeRowVisual _selectedUpgradeRowVisual;
    private static Y4NGZSkillTreeClass _activeSkillTreeClass = Y4NGZSkillTreeClass.Enforcer;
    private static string _selectedSkillTreeNodeId;
    private static SkillTreeNodeVisual _selectedSkillTreeNodeVisual;
    private static float _lastSkillTreeScrollTime = -100f;

    // - PLAYER COSMETICS sub-state -
    // Left-rail category for the PLAYER COSMETICS tab. Mirrors how
    // ENHANCEMENTS uses a category bar above its scroll list, but rotated
    // to a vertical rail to match the design spec. Each category gets its
    // own scroll content; we tear down + rebuild on switch so layout is
    // anchored to y=0 from the top of its own scroll content.
    private const string CosCatSuits     = "SUITS";
    private const string CosCatCosmetics = "COSMETICS";
    private const string CosCatEmotes    = "EMOTES";
    private static string _activeCosCategory = CosCatSuits;
    private static Transform        _cosScrollContent;
    private static ScrollRect       _cosScrollRect;

    // Selected emote for the two-click preview-purchase flow. Cleared on
    // category switch, on tier switch, on row purchase, and on menu close.
    private static object _selectedEmoteForPurchase;

    // Selected cosmetic for the same inspect-then-purchase flow used by
    // upgrades/emotes. Owned cosmetic cards still toggle equip directly.
    private static string _selectedCosmeticForPurchase;
    private static string _selectedCosmeticLabel;
    private static string _selectedCosmeticTypeLabel;

    // Whatever emote is currently being shown on TooManyEmotes' preview rig.
    // Tracked here so we can keep auto-previewing the first emote in a tier
    // when entering EMOTES with no user selection yet (Bug 1b: rig was empty
    // grey because no emote was active to wake the camera).
    private static object _previewingEmote;

    // EMOTES tier sub-state. _discoveredTiers is filled lazily once
    // TooManyEmotes' emote list is available; tier names + rarities come
    // straight from UnlockableEmote.rarity / .rarityText with no hardcoding.
    private static int                            _activeEmoteTier = int.MinValue;
    private static List<(int rarity, string name)> _discoveredTiers;
    private static readonly HashSet<int> _expandedEmoteTiers = new HashSet<int>();
    private static readonly Dictionary<int, ConfigEntry<int>> _tierPriceConfigs = new();
    // Persistent right-side employee render. ONE Y4NGZ-owned rig (camera +
    // spotlight + render texture + player clone) drives the preview for every
    // tab - upgrades, suits, cosmetics and emotes all show the same character.
    // Rig parameters mirror TooManyEmotes' AnimationPreviewer, the known-good
    // way to render a player clone under LC's HDRP pipeline (perspective
    // CameraType.Preview camera, plain spotlight, isolated layer). Layer 23
    // matches TME's render layer; the two rigs sit 200m apart vertically so
    // neither camera (far clip <= 10) can ever see the other's model, and
    // each spotlight (range 40) only reaches its own rig.
    private const int EmployeePreviewLayer = 23;
    private const float EmployeePreviewRenderInterval = 1f / 30f;
    private static readonly Vector3 EmployeePreviewOrigin = new Vector3(0f, -1200f, 0f);
    private static RenderTexture       _employeePreviewTexture;
    private static GameObject          _employeePreviewRoot;
    private static Camera              _employeePreviewCamera;
    private static EmployeePreviewTicker _employeePreviewTicker;
    private static GameObject          _employeePreviewModel;
    private static SkinnedMeshRenderer _previewBodyMesh;    // clone's LOD1
    private static SkinnedMeshRenderer _previewSourceMesh;  // player.thisPlayerModel
    private static PlayerControllerB   _previewSourcePlayer; // clone source, for per-tick liveness checks
    // ModelReplacementAPI mode. An MRAPI suit does not re-skin the scavenger:
    // it hides it and drives a separate replacement body that is a ROOT scene
    // object, not a child of the player. Cloning the player therefore yields
    // the hidden vanilla mesh, and pasting the suit material onto it smears a
    // texture authored for a completely different UV layout across it. When a
    // replacement is live we clone THAT instead and mirror its local
    // transforms; MRAPI has already posed (world-space bone copy plus per-bone
    // offsets) and auto-scaled it this frame, so there is no MRAPI math to
    // re-derive on our side. False => everything below is the vanilla path.
    private static bool       _previewUsesReplacement;
    private static GameObject _previewReplacementSource;
    // Latch for a replacement whose hierarchy we could not pair. Deliberately
    // NOT cleared by InvalidateEmployeePreviewModel: without it the vanilla
    // fallback would be torn down and retried every single tick.
    private static GameObject _previewReplacementRejected;
    // Pose mirroring: each tick copies localRotation from the real player's
    // spine bone chain onto the clone's, so the preview idles/crouches/emotes
    // exactly like the live character without needing its own Animator.
    private static readonly List<(Transform src, Transform dst)> _previewBonePairs = new();
    private static readonly List<(Transform src, Transform dst)> _previewEmoteBonePairs = new();
    private static Transform     _previewSpineRoot;
    private static Animator      _previewEmoteAnimatorSource;
    private static Transform     _previewEmoteSourceSpine;
    private static bool          _employeePreviewWarned;
    private static float         _employeePreviewYawOffset;
    private static float         _employeePreviewLastDragTime = -100f;
    private static float         _nextEmployeePreviewRenderAt;
    // TEMPORARY - blank-employee-preview investigation, see
    // .planning/debug/model-replacement-employee-preview.md. Remove with
    // LogEmployeePreviewDiagnostic and EmployeePreviewClearAlpha.
    private static float         _nextEmployeePreviewDiagnosticAt;
    private static bool          _employeePreviewStaleReplacementWarned;

    // Rank bar elements
    private static TMP_Text         _rankNameText;
    private static TMP_Text         _rankXpText;
    private static Image            _rankFillBar;

    public static Y4NGZUpgradeNode currentSelection;
    public static string             location;
    public static bool               isTrackedYet = false;

    private sealed class UpgradeRowVisual
    {
        public Image Background;
        public Button Button;
        public TMP_Text Label;
        public RectTransform NameRect;
        public GameObject Fill;
        public GameObject Rail;
        public GameObject Outline;
        public Color Normal;
        public Color Hover;
        public Color Selected;
        public Color SelectedHover;
        public Color LabelNormal;
        public Color LabelSelected;

        public void SetSelected(bool selected)
        {
            Color normal = selected ? Selected : Normal;
            Color hover  = selected ? SelectedHover : Hover;
            if (Background != null)
            {
                Background.color = normal;
                Background.CrossFadeColor(normal, 0f, true, true);
            }
            if (Button != null)
            {
                var cb = Button.colors;
                cb.normalColor = normal;
                cb.highlightedColor = hover;
                cb.selectedColor = normal;
                cb.disabledColor = normal;
                Button.colors = cb;
            }
            if (Fill != null) Fill.SetActive(selected);
            if (Rail != null) Rail.SetActive(selected);
            if (Outline != null) Outline.SetActive(selected);
            if (Label != null) Label.color = selected ? LabelSelected : LabelNormal;
            if (NameRect != null) NameRect.offsetMin = new Vector2(selected ? 24f : 18f, 0f);
        }
    }

    private sealed class SkillTreeNodeVisual
    {
        public Image Background;
        public Button Button;
        public TMP_Text Label;
        public Transform Border;
        public RectTransform IconRect;
        public Color Normal;
        public Color Hover;
        public Color Selected;
        public Color SelectedHover;
        public Color BorderNormal;
        public Color BorderSelected;
        /// <summary>Pointer-over stroke. One step brighter than BorderNormal, no motion.</summary>
        public Color BorderHover;
        public Color LabelNormal;
        public Color LabelSelected;
        public Vector2 IconNormalSize;
        public Vector2 IconSelectedSize;
        /// <summary>Whole-node fade for the out-of-reach states, and its resting value.</summary>
        public CanvasGroup Group;
        public float GroupNormal = 1f;

        private bool _selected;
        private bool _hovered;

        public void SetSelected(bool selected)
        {
            _selected = selected;
            Apply();
        }

        public void SetHovered(bool hovered)
        {
            _hovered = hovered;
            Apply();
        }

        // Selection outranks hover; hover only brightens the stroke of a node
        // that is not already the selected one. No coroutine, no per-frame work.
        private void Apply()
        {
            Color normal = Normal;
            Color hover = Hover;
            if (Background != null)
            {
                Background.color = normal;
                Background.CrossFadeColor(normal, 0f, true, true);
            }

            if (Button != null)
            {
                var cb = Button.colors;
                cb.normalColor = normal;
                cb.highlightedColor = hover;
                cb.selectedColor = normal;
                cb.disabledColor = normal;
                Button.colors = cb;
            }

            if (Border != null)
            {
                SetBorderColor(Border, _selected
                    ? BorderSelected
                    : _hovered ? BorderHover : BorderNormal);
            }

            if (IconRect != null)
                IconRect.sizeDelta = _selected ? IconSelectedSize : IconNormalSize;

            // Selecting or pointing at a dimmed node lifts it most of the way
            // back to full - the dim states are a resting emphasis, not a
            // reason the player cannot read what they just clicked on.
            if (Group != null)
            {
                float lift = _selected ? 0.92f : _hovered ? 0.80f : 0f;
                Group.alpha = Mathf.Max(GroupNormal, lift);
            }

            if (Label != null)
                Label.color = _selected ? LabelSelected : LabelNormal;
        }
    }

    // Pointer emphasis for a skill node. A Button's ColorBlock can only fade the
    // fill; the border is four separate Images, so brightening it needs the
    // enter/exit events. One component per node, no Update.
    private sealed class SkillTreeNodeHoverEmphasis : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler
    {
        internal SkillTreeNodeVisual Visual;

        public void OnPointerEnter(PointerEventData eventData) => Visual?.SetHovered(true);
        public void OnPointerExit(PointerEventData eventData) => Visual?.SetHovered(false);
    }

    private sealed class SkillTreeScrollPager : MonoBehaviour, IScrollHandler
    {
        public void OnScroll(PointerEventData eventData)
        {
            if (_listView == null || !_listView.activeInHierarchy || eventData == null)
                return;

            float delta = eventData.scrollDelta.y;
            if (Mathf.Abs(delta) < 0.01f)
                return;

            if (Time.unscaledTime - _lastSkillTreeScrollTime < 0.18f)
                return;

            _lastSkillTreeScrollTime = Time.unscaledTime;
            CycleSkillTree(delta < 0f ? 1 : -1);
            eventData.Use();
        }
    }

    private static readonly Dictionary<string, UpgradeRowVisual> _upgradeRowVisuals =
        new Dictionary<string, UpgradeRowVisual>(StringComparer.OrdinalIgnoreCase);

    // - Public entry point -
    // Every view registers how to re-enter itself, so a rebuild can land the
    // player back where they were instead of on the Employee File.
    private static Action _activeView;
    private static bool _themeHookInstalled;
    private static bool _rebuildingForTheme;

    public static void initMenu()
    {
        if (_root != null) return;
        Plugin.ExtendedLogging("Building Y4NGZ Menu from code");

        InstallThemeHook();
        _root = BuildRoot();
        _root.AddComponent<MenuController>().Init(_root);

        OpenCursor();
        showEmployeeFile();
    }

    private static void InstallThemeHook()
    {
        if (_themeHookInstalled) return;
        _themeHookInstalled = true;
        // Never unsubscribed: the handler is inert while the menu is closed, and
        // a static subscription cannot outlive the assembly.
        UiTheme.ThemeChanged += OnUiThemeChanged;
    }

    // The whole menu bakes palette colors at construction, so the only way to
    // re-theme it correctly is to rebuild it. Reachable in practice through
    // ConfigurationManager's F1 overlay, which can change the theme without
    // closing this menu.
    private static void OnUiThemeChanged(HudColorPreset preset)
    {
        if (_root == null || _rebuildingForTheme) return;

        Action restore = _activeView;
        _rebuildingForTheme = true;
        try
        {
            GameObject stale = _root;
            _root = null;
            if (stale != null) Object.Destroy(stale);

            initMenu();
            restore?.Invoke();
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogError($"[PMenu] Theme rebuild failed: {e}");
        }
        finally
        {
            _rebuildingForTheme = false;
        }
    }

    // -
    //  ROOT  BUILD
    // -

    /// <summary>
    /// Player-configured size multiplier for the whole menu. Read at build time
    /// and applied to the root panel's localScale; see the config binding in
    /// <see cref="Plugin"/> for why the panel scales instead of resizing.
    /// The acceptable range lives on that binding alone - BepInEx clamps the
    /// entry to its <c>AcceptableValueRange</c>, so re-declaring the bounds
    /// here would only give the range a second place to go stale.
    /// </summary>
    internal static float MenuScale
    {
        get
        {
            ConfigEntry<float> entry = Plugin.MenuScale;
            return entry == null ? 1f : entry.Value;
        }
    }

    private static GameObject BuildRoot()
    {
        // full-screen canvas
        var canvas = UI.MakeCanvas("Y4NGZMenu");

        // dark overlay
        UI.MakePanel("Overlay", canvas.transform, Palette.BgOverlay,
            Vector2.zero, Vector2.one);

        // centred panel
        var panel = UI.MakePanel("Panel", canvas.transform, Palette.Backframe,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        panel.GetComponent<Image>().raycastTarget = false;
        var prt   = panel.GetComponent<RectTransform>();
        prt.pivot        = new Vector2(0.5f, 0.5f);
        prt.sizeDelta    = new Vector2(PanelW, PanelH);
        prt.localScale   = Vector3.one * MenuScale;
        UI.AddPanelTexture(panel.transform, TexFaint, 1101);

        BuildHeader(panel.transform);
        BuildViews(panel.transform);

        UI.AddCrtGrime(panel.transform, alpha: TexHigh);
        // Keep the CRT character without blacking out one-pixel strokes in the
        // terminal font. At 0.20 the overlay visibly removed parts of small glyphs.
        UI.AddScanlines(panel.transform, alpha: 0.08f, lineHeight: 1, overlay: true);

        return canvas;
    }

    // - Helper: build a section panel, preferring vanilla border clone -
    // Each section of the menu (header, rank bar, nav, content, footer)
    // uses this so it gets the vanilla CRT bordered look, with a clean
    // programmatic fallback if the vanilla prefab isn't loaded yet.
    private static GameObject BuildSectionPanel(string name, Transform panel,
        Color fallbackColor, Vector2 anchorMin, Vector2 anchorMax,
        Vector2 offsetMin, Vector2 offsetMax)
    {
        var cloned = UI.CloneBorderedPanel(name, panel,
            anchorMin, anchorMax, offsetMin, offsetMax);
        bool usedClone = cloned != null;
        var result = cloned ?? UI.MakePanel(name, panel, fallbackColor,
            anchorMin, anchorMax, offsetMin, offsetMax);
        if (!usedClone)
            TintPanelImages(result, fallbackColor);
        UI.AddPanelTexture(result.transform, usedClone ? TexFaint : TexStrong, name.GetHashCode());
        if (!usedClone)
            AddThinOutline(result, 0.24f);
        return result;
    }

    private static void TintPanelImages(GameObject panel, Color color)
    {
        if (panel == null) return;
        var images = panel.GetComponents<Image>();
        for (int i = 0; i < images.Length; i++)
            images[i].color = color;
    }

    private static GameObject BuildInnerCell(string name, Transform parent,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax,
        bool rounded = false)
    {
        var cell = UI.MakePanel(name, parent, Palette.BgPanel,
            anchorMin, anchorMax, offsetMin, offsetMax);
        if (rounded) ApplyRoundedPanelShape(cell, CellRadius);
        UI.AddPanelTexture(cell.transform, TexHigh, name.GetHashCode());
        if (rounded)
            AddRoundedFrame(cell.transform, 3f, 4f,
                Palette.Alpha(Palette.Accent, 0.28f), CellRadius);
        else
            AddRuggedFrame(cell.transform, 0.28f);
        return cell;
    }

    // Gap between stacked section panels, and the corner radius every rounded
    // inner cell shares.
    private const float SectionGap = Space3;
    private const float CellRadius = 12f;

    // - Header -
    private static void BuildHeader(Transform panel)
    {
        var hdr = BuildSectionPanel("Header", panel, Palette.BgHeader,
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(0, -HeaderH), Vector2.zero);

        string playerName = StartOfRound.Instance?.localPlayerController?.playerUsername ?? "EMPLOYEE";
        var titleCell = BuildInnerCell("EmployeeFileCell", hdr.transform,
            new Vector2(0, 0), new Vector2(0.255f, 1f),
            new Vector2(18, 14), new Vector2(-10, -14), rounded: true);
        _employeeFileButton = titleCell.AddComponent<Button>();
        _employeeFileButton.targetGraphic = titleCell.GetComponent<Image>();
        _employeeFileButton.transition = Selectable.Transition.ColorTint;
        _employeeFileButton.navigation = new Navigation { mode = Navigation.Mode.None };
        _employeeFileButton.onClick.AddListener(() =>
        {
            MenuAudio.PlayNav();
            _selectedCosmeticForPurchase = null;
            _selectedEmoteForPurchase = null;
            _previewingEmote = null;
            StopEmotePreview();
            showEmployeeFile();
        });
        _employeeFileLabel = UI.MakeText("FileTitle", titleCell.transform,
            "EMPLOYEE FILE", FontHuge, Palette.Accent,
            TextAlignmentOptions.Center);
        _employeeFileLabel.raycastTarget = false;

        var rankCell = BuildInnerCell("RankCell", hdr.transform,
            new Vector2(0.275f, 0), new Vector2(0.705f, 1f),
            new Vector2(10, 14), new Vector2(-10, -14), rounded: true);
        _rankNameText = UI.MakeText("RankName", rankCell.transform,
            "", FontLg, Palette.Accent, TextAlignmentOptions.MidlineLeft);
        var rnrt = _rankNameText.GetComponent<RectTransform>();
        rnrt.anchorMin = new Vector2(0, 0.58f);
        rnrt.anchorMax = new Vector2(1, 0.92f);
        rnrt.offsetMin = new Vector2(18, 0);
        rnrt.offsetMax = new Vector2(-18, 0);

        var progBg = UI.MakePanel("ProgBg", rankCell.transform, Palette.BgInput,
            new Vector2(0, 0.23f), new Vector2(0.66f, 0.47f),
            new Vector2(18, 0), new Vector2(-6, 0));
        AddThinOutline(progBg, 0.55f);
        UI.AddPanelTexture(progBg.transform, TexHigh, 401);
        var fillGo = UI.MakePanel("ProgFill", progBg.transform, Palette.Shade(Palette.Accent, 0.85f, 0.92f),
            new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, Vector2.zero);
        _rankFillBar = fillGo.GetComponent<Image>();

        _rankXpText = UI.MakeText("RankXp", rankCell.transform,
            "", FontMd, Palette.Accent, TextAlignmentOptions.MidlineRight);
        var xprt = _rankXpText.GetComponent<RectTransform>();
        xprt.anchorMin = new Vector2(0.67f, 0.19f);
        xprt.anchorMax = new Vector2(1, 0.50f);
        xprt.offsetMin = new Vector2(8, 0);
        xprt.offsetMax = new Vector2(-18, 0);

        var bankCell = BuildInnerCell("BankCell", hdr.transform,
            new Vector2(0.725f, 0), new Vector2(0.972f, 1f),
            new Vector2(10, 14), new Vector2(-18, -14), rounded: true);
        _currencyText = UI.MakeText("Currency", bankCell.transform,
            "", FontXl, Palette.Gold,
            TextAlignmentOptions.Center);
        var crt = _currencyText.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0, 0.48f);
        crt.anchorMax = new Vector2(1, 0.92f);
        crt.offsetMin = new Vector2(12, 0);
        crt.offsetMax = new Vector2(-12, 0);

        UI.MakeText("PlayerName", bankCell.transform,
            "PLAYER: " + playerName.ToUpperInvariant(), FontMd, Palette.Accent,
            TextAlignmentOptions.Center)
            .GetComponent<RectTransform>().offsetMin = new Vector2(12, -48);

        // close button
        var (closeBtn, closeLbl) = UI.MakeButton("CloseBtn", panel,
            "X", FontMd, Palette.BtnDanger, Palette.BtnDangerHl, Palette.Danger,
            new Vector2(1, 1), new Vector2(1, 1),
            new Vector2(-38, -HeaderH), new Vector2(0, 0));
        closeBtn.onClick.AddListener(() =>
        {
            // CloseMenu plays the exit sound for every close path.
            _root?.GetComponent<MenuController>()?.CloseMenu();
        });

        RefreshCurrencyDisplay();
        RefreshRankDisplay();
    }

    // - Y4NGZ reflection for rank display -

    // Cached Y4NGZ state - RefreshRankDisplay is the single source of
    // truth, the player-level view reads these instead of duplicating the
    // reflection path.
    public static bool   BxpAvailable     { get; private set; }
    public static int    BxpCurrentLevel  { get; private set; }
    public static int    BxpTotalLevels   { get; private set; }
    public static string BxpCurrentName   { get; private set; }
    public static int    BxpTotal         { get; private set; }

    private static void RefreshRankDisplay()
    {
        if (_rankNameText == null) return;

        string name = ProgressionApi.RankName;
        int rankIndex = ProgressionApi.RankIndex;
        int totalRanks = RankCatalog.Count;
        int rankXp = ProgressionApi.RankXp;
        RankDefinition rank = RankCatalog.GetRank(rankXp);
        float progress = Mathf.Clamp01(ProgressionApi.RankProgress);

        _rankNameText.text = name.ToUpperInvariant();
        if (_rankXpText != null)
        {
            int rankEnd = rank.EndXp >= RankCatalog.MaxRankXp ? rankXp : rank.EndXp;
            _rankXpText.text = $"{rankXp} / {rankEnd} XP";
        }
        BxpAvailable = true;
        BxpCurrentLevel = rankIndex;
        BxpTotalLevels = totalRanks;
        BxpCurrentName = name;
        BxpTotal = rankXp;

        if (_rankFillBar != null)
        {
            var fillRt = _rankFillBar.GetComponent<RectTransform>();
            fillRt.anchorMax = new Vector2(progress, 1);
        }

        NotifyRankChanged();
    }

    // Bumped each time the cached Y4NGZ rank state is refreshed; the
    // suit list reads it on rebuild to avoid stale lock state.
    private static int _rankRefreshTick;
    private static void NotifyRankChanged() { _rankRefreshTick++; }

    // - View containers -
    private static void BuildViews(Transform panel)
    {
        float top    = HeaderH + SectionGap;
        float contentBottom = FooterH + BodyPad + 6f;

        var body = BuildSectionPanel("BodyShell", panel, Palette.BgPanel,
            new Vector2(0, 0), new Vector2(1, 1),
            Vector2.zero, new Vector2(0, -top));
        BuildEmbeddedFooter(body.transform);

        BuildModeRail(body.transform);

        float centerLeft  = BodyPad + LeftRailW + ColumnGap;
        float centerRight = BodyPad + InspectorW + ColumnGap;

        // Employee File hub. It owns the center performance record while the
        // persistent right inspector shows the profile, collection, and ammo.
        _employeeFileView = new GameObject("EmployeeFileView");
        _employeeFileView.transform.SetParent(body.transform, false);
        var efrt = _employeeFileView.AddComponent<RectTransform>();
        efrt.anchorMin = new Vector2(0, 0);
        efrt.anchorMax = new Vector2(1, 1);
        efrt.offsetMin = new Vector2(centerLeft, contentBottom);
        efrt.offsetMax = new Vector2(-centerRight, -BodyPad);
        AddBackground(_employeeFileView, Palette.Alpha(Palette.BgInput, FillGhost),
            outline: false);

        // List view  (upgrade scroll list)
        _listView = new GameObject("ListView");
        _listView.transform.SetParent(body.transform, false);
        var lvrt = _listView.AddComponent<RectTransform>();
        lvrt.anchorMin = new Vector2(0, 0);
        lvrt.anchorMax = new Vector2(1, 1);
        lvrt.offsetMin = new Vector2(centerLeft, contentBottom);
        lvrt.offsetMax = new Vector2(-centerRight, -BodyPad);
        AddBackground(_listView, Palette.Alpha(Palette.BgInput, FillGhost), outline: false);

        var (scrollGo, content) = UI.MakeScrollView("Scroll", _listView.transform,
            Vector2.zero, Vector2.one,
            new Vector2(10, 6), new Vector2(-10, -6),
            addScrollbar: false);
        _scrollContent = content;
        _scrollRect    = scrollGo.GetComponent<ScrollRect>();

        // Persistent inspector. This stays active while the center panel
        // switches between upgrades, cosmetics, emotes, and trade.
        _detailView = new GameObject("DetailView");
        _detailView.transform.SetParent(body.transform, false);
        var dvrt = _detailView.AddComponent<RectTransform>();
        dvrt.anchorMin = new Vector2(1, 0);
        dvrt.anchorMax = new Vector2(1, 1);
        dvrt.offsetMin = new Vector2(-(BodyPad + InspectorW), contentBottom);
        dvrt.offsetMax = new Vector2(-BodyPad, -BodyPad);
        AddBackground(_detailView, Palette.BgInput, outline: true);
        RenderInspectorPlaceholder("EMPLOYEE PREVIEW", "Select an upgrade, suit, cosmetic, emote, or crew trade.");

        // Trade view
        _tradeView = new GameObject("TradeView");
        _tradeView.transform.SetParent(body.transform, false);
        var tvrt = _tradeView.AddComponent<RectTransform>();
        tvrt.anchorMin = new Vector2(0, 0);
        tvrt.anchorMax = new Vector2(1, 1);
        tvrt.offsetMin = new Vector2(centerLeft, contentBottom);
        tvrt.offsetMax = new Vector2(-centerRight, -BodyPad);
        AddBackground(_tradeView, Palette.BgInput, outline: false);
        _tradeView.SetActive(false);

        // Player level view (was: Cosmetics view in v0)
        _playerLevelView = new GameObject("PlayerLevelView");
        _playerLevelView.transform.SetParent(body.transform, false);
        var cvrt = _playerLevelView.AddComponent<RectTransform>();
        cvrt.anchorMin = new Vector2(0, 0);
        cvrt.anchorMax = new Vector2(1, 1);
        cvrt.offsetMin = new Vector2(centerLeft, contentBottom);
        cvrt.offsetMax = new Vector2(-centerRight, -BodyPad);
        AddBackground(_playerLevelView, Palette.BgInput, outline: false);
        _playerLevelView.SetActive(false);

        // Column spacing now separates the rail, list, and inspector. The
        // old vertical divider strokes made the center list feel boxed in.
    }

    private static void BuildEmbeddedFooter(Transform body)
    {
        var footer = UI.MakePanel("FooterBand", body, Palette.BgInput,
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(BodyPad, 10), new Vector2(-BodyPad, FooterH + 10));
        ApplyRoundedPanelShape(footer, CellRadius);
        UI.AddPanelTexture(footer.transform, TexHigh, 773);
        AddRoundedFrame(footer.transform, 3f, 4f,
            Palette.Alpha(Palette.Accent, FillWash), CellRadius);
        _footerHint = UI.MakeText("Hint", footer.transform,
            HintDefault, FontXs, Palette.Dim, TextAlignmentOptions.Center);
        _footerHint.enableWordWrapping = false;
        _footerHint.overflowMode = TextOverflowModes.Ellipsis;
        _footerHint.raycastTarget = false;
    }

    // -
    //  FOOTER HINT STRINGS
    // -
    // The footer used to advertise a keyboard layer that did not exist. Each
    // string below is the literal contract of what HandleKeyboardNavigation
    // does in that view: if a binding changes, the string next to it changes.
    // ASCII only - the 3270 SDF atlas is not guaranteed to carry arrows.
    private const string HintDefault =
        "[W/S] MOVE   [ENTER] SELECT   [Q/E] SECTION   [ESC] CLOSE";
    private const string HintSkillTree =
        "[W/S] TIER   [A/D] NODE   [ENTER] SELECT   [Q/E] SECTION   [ESC] CLOSE";
    private const string HintGrid =
        "[W/S] ROW   [A/D] COLUMN   [ENTER] SELECT   [Q/E] SECTION   [ESC] CLOSE";
    private const string HintList =
        "[W/S] MOVE   [ENTER] SELECT   [Q/E] SECTION   [ESC] CLOSE";
    private const string HintRecord =
        "[W/S] SCROLL RECORD   [Q/E] SECTION   [ESC] CLOSE";
    // Everything the menu can always do. Used by views with no focusable
    // content of their own: the trade view's fields take the keyboard, and a
    // skill-tree section with no data registers no focus targets at all.
    private const string HintMinimal =
        "[Q/E] SECTION   [ESC] CLOSE";
    private const string HintTrade = HintMinimal;

    private static void SetFooterHint(string hint)
    {
        if (_footerHint == null)
            return;

        IngameKeybinds keybinds = Plugin.Keybinds;
        string resolved = hint ?? HintDefault;
        resolved = resolved.Replace("[W/S]", "[" + UpgradeInput.DisplayPair(
            keybinds?.PlayerMenuUp, "W", keybinds?.PlayerMenuDown, "S") + "]");
        resolved = resolved.Replace("[A/D]", "[" + UpgradeInput.DisplayPair(
            keybinds?.PlayerMenuLeft, "A", keybinds?.PlayerMenuRight, "D") + "]");
        resolved = resolved.Replace("[Q/E]", "[" + UpgradeInput.DisplayPair(
            keybinds?.PlayerMenuPreviousSection, "Q", keybinds?.PlayerMenuNextSection, "E") + "]");
        resolved = resolved.Replace("[ENTER]", "[" + UpgradeInput.DisplayLabel(
            keybinds?.PlayerMenuSelect, "ENTER") + "]");
        resolved = resolved.Replace("[ESC]", "[" + UpgradeInput.DisplayLabel(
            keybinds?.PlayerMenuClose, "ESC") + "]");
        _footerHint.text = resolved;
    }

    // -
    //  SECTION REGISTRY
    // -
    // One ordered list of the sections the left rail offers. The rail builder,
    // the active-tab highlight and section cycling all read it, so adding a
    // fifth section means adding a row here and nothing else: the rail slot
    // math divides by Sections.Length rather than by a hardcoded 4.
    //
    // The Employee File hub is deliberately NOT a row. It is the menu's home
    // screen, reached from the header cell rather than the rail, and cycling
    // moves between the rail sections only.
    internal sealed class SectionDef
    {
        /// <summary>Stable identifier. Not shown to the player.</summary>
        public string Id;
        /// <summary>Rail button caption.</summary>
        public string Label;
        /// <summary>File name under the menu icon set, or null for a caption-only button.</summary>
        public string IconFile;
        /// <summary>Enter this section. Owns any sub-state the section needs set first.</summary>
        public Action Show;
        /// <summary>Is <paramref name="view"/> plus the current sub-state this section?</summary>
        public Func<GameObject, bool> IsActive;
        /// <summary>Whether the provider has any content worth exposing in this session.</summary>
        public Func<bool> IsAvailable;
    }

    private static readonly SectionDef[] Sections =
    {
        new SectionDef
        {
            Id = "upgrades",
            Label = "UPGRADES",
            IconFile = "menu-icon-upgrades.png",
            Show = () =>
            {
                _selectedCosmeticForPurchase = null;
                _selectedEmoteForPurchase = null;
                _previewingEmote = null;
                _selectedSkillTreeNodeId = null;
                currentSelection = null;
                showUpgrades(false, resetScroll: true);
            },
            IsActive = view => view != null && view == _listView
        },
        new SectionDef
        {
            Id = "suits",
            Label = "SUITS",
            IconFile = "menu-icon-suits.png",
            Show = () =>
            {
                _activeCosCategory = CosCatSuits;
                _selectedCosmeticForPurchase = null;
                _selectedEmoteForPurchase = null;
                _previewingEmote = null;
                showPlayerLevel(resetScroll: true);
            },
            IsActive = view => view != null && view == _playerLevelView
                && _activeCosCategory == CosCatSuits
        },
        new SectionDef
        {
            Id = "cosmetics",
            Label = "COSMETICS",
            IconFile = "menu-icon-cosmetics.png",
            Show = () =>
            {
                _activeCosCategory = CosCatCosmetics;
                _selectedEmoteForPurchase = null;
                _previewingEmote = null;
                StopEmotePreview();
                showPlayerLevel(resetScroll: true);
            },
            IsActive = view => view != null && view == _playerLevelView
                && _activeCosCategory == CosCatCosmetics,
            IsAvailable = () => GetCosmeticCollectionCount().total > 0
        },
        new SectionDef
        {
            Id = "emotes",
            Label = "EMOTES",
            IconFile = "menu-icon-emotes.png",
            Show = () =>
            {
                _activeCosCategory = CosCatEmotes;
                _expandedEmoteTiers.Clear();
                _selectedCosmeticForPurchase = null;
                _selectedEmoteForPurchase = null;
                _previewingEmote = null;
                StopEmotePreview();
                showPlayerLevel(resetScroll: true);
            },
            IsActive = view => view != null && view == _playerLevelView
                && _activeCosCategory == CosCatEmotes,
            IsAvailable = () => GetEmoteCollectionCount().total > 0
        }
    };

    private sealed class SectionVisual
    {
        public Button Button;
        public TMP_Text Label;
        public RawImage Icon;
    }

    // Parallel to the visible section snapshot, refilled by BuildModeRail on every menu build. It is
    // readonly so MenuController's reflective teardown leaves it alone; the
    // entries it still holds after a close point at destroyed Buttons, which
    // every consumer already treats as "no button" through Unity's null test.
    private static readonly List<SectionDef> _visibleSections = new List<SectionDef>();
    private static readonly List<SectionVisual> _sectionVisuals = new List<SectionVisual>();

    /// <summary>The rail sections in display order. Consumed by keyboard cycling.</summary>
    internal static IReadOnlyList<SectionDef> SectionOrder => _visibleSections;

    private static void RefreshVisibleSections()
    {
        _visibleSections.Clear();
        for (int i = 0; i < Sections.Length; i++)
        {
            SectionDef section = Sections[i];
            bool available = section.IsAvailable == null;
            if (!available)
            {
                try { available = section.IsAvailable(); }
                catch { available = false; }
            }
            if (available)
                _visibleSections.Add(section);
        }
    }

    /// <summary>
    /// Index into <see cref="SectionOrder"/> of the section on screen, or -1 on
    /// the Employee File hub / trade view, which are not rail sections.
    /// </summary>
    internal static int ActiveSectionIndex
    {
        get
        {
            GameObject view = _activeSectionView;
            if (view == null) return -1;
            for (int i = 0; i < _visibleSections.Count; i++)
            {
                if (_visibleSections[i].IsActive != null && _visibleSections[i].IsActive(view))
                    return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// Move <paramref name="direction"/> steps through the rail (wrapping) and
    /// enter that section. From a non-rail view, a forward step lands on the
    /// first section and a backward step on the last. Driven by Q/E in
    /// <see cref="HandleKeyboardNavigation"/>.
    /// </summary>
    internal static void CycleSection(int direction)
    {
        if (_visibleSections.Count == 0)
            RefreshVisibleSections();
        if (_visibleSections.Count == 0 || direction == 0) return;

        int current = ActiveSectionIndex;
        int next;
        if (current < 0)
            next = direction > 0 ? 0 : _visibleSections.Count - 1;
        else
            next = ((current + direction) % _visibleSections.Count + _visibleSections.Count) % _visibleSections.Count;

        ShowSection(next);
    }

    /// <summary>Enter the section at <paramref name="index"/>, with the nav sound.</summary>
    internal static void ShowSection(int index)
    {
        if (index < 0 || index >= _visibleSections.Count) return;
        MenuAudio.PlayNav();
        _visibleSections[index].Show?.Invoke();
    }

    // -
    //  KEYBOARD FOCUS
    // -
    // The menu is built from raw Buttons wired to onClick and read by a
    // GraphicRaycaster; there is no uGUI Navigation graph and no EventSystem
    // selection, and turning either on would fight every custom hover/selected
    // visual in this file. So the keyboard layer is its own thing: each view
    // registers the things it considers reachable, in reading order, tagged
    // with a (Row, Col) cell. W/S walks rows, A/D walks the cells inside one
    // row, ENTER invokes the same onClick a mouse click would.
    //
    // A view that is a plain list registers every item as its own row with
    // Col 0, so A/D are simply inert there. A view laid out as a grid (the
    // skill tree's tiers, the cosmetics cards) registers real cells.
    //
    // Focus is deliberately NOT selection. Clicking a skill node selects it and
    // repaints the inspector; focusing one only moves the ring. Because a click
    // usually rebuilds the whole view, the click handler re-focuses by key
    // rather than by index, which survives the list being replaced underneath.
    private sealed class FocusTarget
    {
        /// <summary>Stable across rebuilds, so focus survives a purchase.</summary>
        public string Key;
        public RectTransform Rect;
        /// <summary>Null for a row that is readable but not actionable.</summary>
        public Button Button;
        public int Row;
        public int Col;
        /// <summary>Scroller to bring this target into view, or null.</summary>
        public ScrollRect Scroller;
    }

    // Rebuilt wholesale by every view. `readonly` so MenuController's reflective
    // teardown leaves it alone; ClearFocusTargets owns emptying it, and
    // MenuController calls ClearKeyboardFocus() on close so nothing here
    // outlives the GameObjects it points at.
    private static readonly List<FocusTarget> _focusTargets = new List<FocusTarget>();
    private static int _focusIndex = -1;
    private static string _pendingFocusKey;
    // A click updates the logical cursor too, but only keyboard activation may
    // leave the separate accessibility focus ring visible.
    private static bool _invokingFocusedButton;

    private const float FocusRingThickness = 2f;
    private const float FocusRingInset     = 2f;

    /// <summary>Drop every registered target and park the ring. Called by each view before it rebuilds.</summary>
    private static void ClearFocusTargets()
    {
        if (_focusIndex >= 0 && _focusIndex < _focusTargets.Count)
            _pendingFocusKey = _focusTargets[_focusIndex].Key;

        _focusTargets.Clear();
        _focusIndex = -1;
        HideFocusRing();
    }

    /// <summary>Teardown hook: the ring and every target belong to a destroyed root.</summary>
    internal static void ClearKeyboardFocus()
    {
        _focusTargets.Clear();
        _focusIndex = -1;
        _pendingFocusKey = null;
        _focusRing = null;
        _invokingFocusedButton = false;
    }

    private static void RegisterFocusTarget(
        string key, RectTransform rect, Button button, int row, int col, ScrollRect scroller)
    {
        if (rect == null) return;

        _focusTargets.Add(new FocusTarget
        {
            Key = key,
            Rect = rect,
            Button = button,
            Row = row,
            Col = col,
            Scroller = scroller
        });

        // Mouse and keyboard share one cursor: clicking an item moves focus to
        // it. Captured by key, not index, because the click frequently rebuilds
        // the view and replaces _focusTargets before this listener runs.
        if (button != null)
        {
            string capturedKey = key;
            button.onClick.AddListener(() =>
            {
                FocusByKey(capturedKey, scrollIntoView: false);
                if (!_invokingFocusedButton)
                    HideFocusRing();
            });
        }
    }

    /// <summary>
    /// Pin a rebuilt scroll view back to the top without fighting the keyboard
    /// layer. <see cref="UI.ResetScrollToTop"/> re-clamps two frames later, so
    /// when this rebuild is about to restore focus (a pending key is waiting
    /// for <see cref="CommitFocusTargets"/>) that deferred clamp would land
    /// after <c>ScrollFocusIntoView</c> and throw the focused row back off
    /// screen. In that case the immediate pin is applied alone and the commit
    /// wins; otherwise the deferred re-clamp is kept for the layout cases it
    /// exists for.
    /// </summary>
    private static void ResetScrollForRebuild(ScrollRect sr, RectTransform contentRt)
    {
        if (sr == null || contentRt == null) return;

        if (string.IsNullOrEmpty(_pendingFocusKey))
        {
            UI.ResetScrollToTop(sr, contentRt);
            return;
        }

        contentRt.anchoredPosition = Vector2.zero;
        sr.verticalNormalizedPosition = 1f;
    }

    /// <summary>Restore focus onto the same item after a view rebuilt itself.</summary>
    private static void CommitFocusTargets()
    {
        if (string.IsNullOrEmpty(_pendingFocusKey))
            return;

        string key = _pendingFocusKey;
        _pendingFocusKey = null;
        FocusByKey(key, scrollIntoView: true);
    }

    private static void FocusByKey(string key, bool scrollIntoView)
    {
        if (string.IsNullOrEmpty(key))
            return;

        for (int i = 0; i < _focusTargets.Count; i++)
        {
            if (string.Equals(_focusTargets[i].Key, key, StringComparison.Ordinal))
            {
                SetFocusIndex(i, scrollIntoView);
                return;
            }
        }

        // The item is not on screen (yet). Remember it so the next rebuild lands
        // focus back where the player left it.
        _pendingFocusKey = key;
    }

    private static void SetFocusIndex(int index, bool scrollIntoView)
    {
        if (index < 0 || index >= _focusTargets.Count)
        {
            _focusIndex = -1;
            HideFocusRing();
            return;
        }

        _focusIndex = index;
        FocusTarget target = _focusTargets[index];
        AttachFocusRing(target.Rect);
        if (scrollIntoView)
            ScrollFocusIntoView(target);
    }

    private static void HideFocusRing()
    {
        if (_focusRing != null)
            _focusRing.SetActive(false);
    }

    // Hover is a fill change owned by each Button's ColorBlock; focus is this
    // ring. They read as different things on purpose. The ring mirrors the
    // focused rect's geometry, so it is attached once the view has finished
    // building and its rects are final - CommitFocusTargets is that point.
    private static void AttachFocusRing(RectTransform rect)
    {
        Transform parent = rect != null ? rect.parent : null;
        if (rect == null || parent == null)
        {
            HideFocusRing();
            return;
        }

        if (_focusRing == null)
        {
            _focusRing = new GameObject("KeyboardFocusRing");
            _focusRing.AddComponent<RectTransform>();
            var group = _focusRing.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
            // Never a layout element, whatever container it lands beside.
            _focusRing.AddComponent<LayoutElement>().ignoreLayout = true;
            AddBorder(_focusRing.transform, FocusRingThickness, 0f,
                Palette.Alpha(Palette.Gold, FillOpaque));
        }

        // The ring is a *sibling* of the focused rect, not a child of it. A
        // skill node fades its own content through a CanvasGroup (the dim
        // states go as low as 0.45), and chrome must not inherit that. Riding
        // the target's parent keeps the ring out of exactly one group while
        // still inheriting every group above it - the menu root that
        // MenuController fades on open and close included - and keeps the ring
        // inside the same scroll content, so it tracks the target as it scrolls
        // and clips against the same mask.
        _focusRing.transform.SetParent(parent, worldPositionStays: false);
        _focusRing.transform.SetAsLastSibling();

        // Mirror the target's own rect, shrunk symmetrically by the inset. The
        // shift keeps the two centres together whatever pivot the target uses.
        var ringRt = _focusRing.GetComponent<RectTransform>();
        var shrink = new Vector2(FocusRingInset * 2f, FocusRingInset * 2f);
        ringRt.anchorMin = rect.anchorMin;
        ringRt.anchorMax = rect.anchorMax;
        ringRt.pivot = rect.pivot;
        ringRt.sizeDelta = rect.sizeDelta - shrink;
        ringRt.anchoredPosition = rect.anchoredPosition + new Vector2(
            (0.5f - rect.pivot.x) * shrink.x,
            (0.5f - rect.pivot.y) * shrink.y);
        ringRt.localScale = Vector3.one;
        _focusRing.SetActive(true);
    }

    private static void ScrollFocusIntoView(FocusTarget target)
    {
        ScrollRect sr = target?.Scroller;
        if (sr == null || target.Rect == null) return;

        RectTransform content = sr.content;
        RectTransform viewport = sr.viewport;
        if (content == null || viewport == null) return;

        float contentHeight = content.rect.height;
        float viewHeight = viewport.rect.height;
        float scrollable = contentHeight - viewHeight;
        if (scrollable <= 1f) return;

        Vector3 worldCenter = target.Rect.TransformPoint(target.Rect.rect.center);
        Vector2 localCenter = content.InverseTransformPoint(worldCenter);
        float fromTop = content.rect.yMax - localCenter.y;
        float desiredTop = Mathf.Clamp(fromTop - viewHeight * 0.5f, 0f, scrollable);
        sr.verticalNormalizedPosition = 1f - desiredTop / scrollable;
    }

    /// <summary>
    /// Polled once per frame by <see cref="MenuController"/> while the menu is
    /// open. Nothing here runs when the menu is closed, so it cannot race the
    /// vanilla chat box or the terminal for a keypress.
    /// </summary>
    internal static void HandleKeyboardNavigation()
    {
        if (_root == null) return;
        // The trade view has real text fields, and BepInEx's F1 ConfigurationManager draws its
        // own text boxes over us. While either has the caret, character keys are text, not commands.
        if (IsTextInputFocused()) return;

        IngameKeybinds keybinds = Plugin.Keybinds;
        if (UpgradeInput.WasPressed(keybinds?.PlayerMenuPreviousSection)) { CycleSection(-1); return; }
        if (UpgradeInput.WasPressed(keybinds?.PlayerMenuNextSection)) { CycleSection(1); return; }
        if (UpgradeInput.WasPressed(keybinds?.PlayerMenuUp)) { MoveFocus(0, -1); return; }
        if (UpgradeInput.WasPressed(keybinds?.PlayerMenuDown)) { MoveFocus(0, 1); return; }
        if (UpgradeInput.WasPressed(keybinds?.PlayerMenuLeft)) { MoveFocus(-1, 0); return; }
        if (UpgradeInput.WasPressed(keybinds?.PlayerMenuRight)) { MoveFocus(1, 0); return; }
        if (UpgradeInput.WasPressed(keybinds?.PlayerMenuSelect))
            ActivateFocusedTarget();
    }

    /// <summary>
    /// True while a keypress belongs to a text field rather than to the menu.
    /// Covers both input stacks: uGUI/TMP fields reached through the
    /// EventSystem, and IMGUI text boxes - BepInEx's F1 ConfigurationManager
    /// overlay is one - which never touch the EventSystem and instead park the
    /// caret in <c>GUIUtility.keyboardControl</c>. Without the IMGUI half,
    /// typing a config value walked the focus ring and ENTER could fire the
    /// focused purchase button.
    /// </summary>
    internal static bool IsTextInputFocused()
    {
        // Non-zero means some IMGUI control holds the keyboard this frame.
        if (GUIUtility.keyboardControl != 0) return true;

        EventSystem events = EventSystem.current;
        GameObject selected = events != null ? events.currentSelectedGameObject : null;
        if (selected == null) return false;

        var field = selected.GetComponent<TMP_InputField>();
        return field != null && field.isFocused;
    }

    /// <summary>
    /// <paramref name="dx"/> walks cells inside the focused row;
    /// <paramref name="dy"/> walks rows, keeping the column as close as the
    /// destination row allows. Both wrap.
    /// </summary>
    private static void MoveFocus(int dx, int dy)
    {
        if (_focusTargets.Count == 0) return;

        if (_focusIndex < 0 || _focusIndex >= _focusTargets.Count)
        {
            MenuAudio.PlayNav();
            SetFocusIndex(dy < 0 ? _focusTargets.Count - 1 : 0, scrollIntoView: true);
            return;
        }

        FocusTarget current = _focusTargets[_focusIndex];
        int next = dy != 0
            ? FindTargetInAdjacentRow(current, dy)
            : FindTargetInSameRow(current, dx);

        if (next < 0 || next == _focusIndex) return;

        MenuAudio.PlayNav();
        SetFocusIndex(next, scrollIntoView: true);
    }

    private static int FindTargetInSameRow(FocusTarget current, int dx)
    {
        if (dx == 0) return -1;

        int best = -1;
        int bestCol = 0;
        int wrap = -1;
        int wrapCol = 0;
        for (int i = 0; i < _focusTargets.Count; i++)
        {
            FocusTarget candidate = _focusTargets[i];
            if (candidate.Row != current.Row || candidate.Col == current.Col) continue;

            bool forward = candidate.Col > current.Col;
            if (forward == (dx > 0))
            {
                // Nearest cell in the travel direction.
                if (best < 0 || Mathf.Abs(candidate.Col - current.Col) < Mathf.Abs(bestCol - current.Col))
                {
                    best = i;
                    bestCol = candidate.Col;
                }
            }
            else if (wrap < 0
                || (dx > 0 ? candidate.Col < wrapCol : candidate.Col > wrapCol))
            {
                // Furthest cell the other way, for the wrap.
                wrap = i;
                wrapCol = candidate.Col;
            }
        }

        return best >= 0 ? best : wrap;
    }

    private static int FindTargetInAdjacentRow(FocusTarget current, int dy)
    {
        int targetRow = int.MinValue;
        int wrapRow = int.MinValue;
        bool hasTarget = false;
        bool hasWrap = false;

        for (int i = 0; i < _focusTargets.Count; i++)
        {
            int row = _focusTargets[i].Row;
            if (row == current.Row) continue;

            bool forward = row > current.Row;
            if (forward == (dy > 0))
            {
                if (!hasTarget
                    || Mathf.Abs(row - current.Row) < Mathf.Abs(targetRow - current.Row))
                {
                    targetRow = row;
                    hasTarget = true;
                }
            }
            else if (!hasWrap || (dy > 0 ? row < wrapRow : row > wrapRow))
            {
                wrapRow = row;
                hasWrap = true;
            }
        }

        if (!hasTarget)
        {
            if (!hasWrap) return -1;
            targetRow = wrapRow;
        }

        int best = -1;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < _focusTargets.Count; i++)
        {
            if (_focusTargets[i].Row != targetRow) continue;

            int distance = Mathf.Abs(_focusTargets[i].Col - current.Col);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static void ActivateFocusedTarget()
    {
        if (_focusIndex < 0 || _focusIndex >= _focusTargets.Count) return;

        Button button = _focusTargets[_focusIndex].Button;
        if (button == null || !button.interactable || !button.isActiveAndEnabled)
        {
            MenuAudio.PlayDeny();
            return;
        }

        // Reuse the button's normal handler while preserving keyboard input as
        // the only modality that displays the accessibility focus ring.
        _invokingFocusedButton = true;
        try
        {
            button.onClick.Invoke();
        }
        finally
        {
            _invokingFocusedButton = false;
        }
    }

    private static void BuildModeRail(Transform parent)
    {
        RefreshVisibleSections();
        _sectionVisuals.Clear();
        var rail = BuildInnerCell("ModeRail", parent,
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(BodyPad, FooterH + BodyPad + 6f), new Vector2(BodyPad + LeftRailW, -BodyPad),
            rounded: true);

        for (int i = 0; i < _visibleSections.Count; i++)
        {
            SectionDef section = _visibleSections[i];
            var (btn, label, icon) = BuildRailButton(
                rail.transform, section.Label, section.IconFile, i, _visibleSections.Count);

            _sectionVisuals.Add(new SectionVisual { Button = btn, Label = label, Icon = icon });

            int captured = i;
            btn.onClick.AddListener(() => ShowSection(captured));
        }
    }

    private static (Button btn, TMP_Text label, RawImage icon) BuildRailButton(
        Transform parent, string text, string iconFile, int index, int total)
    {
        const float pad = 14f;
        const float gap = 18f;
        float slot = 1f / Mathf.Max(1, total);
        float yMax = 1f - slot * index;
        float yMin = 1f - slot * (index + 1);

        var result = UI.MakeButton("Mode_" + text, parent,
            text, FontMdLg, Palette.BtnNormal, Palette.BtnHighlight, Palette.NavText,
            new Vector2(0, yMin), new Vector2(1, yMax),
            new Vector2(pad, gap), new Vector2(-pad, -gap));
        result.label.fontStyle = FontStyles.Bold;
        result.label.color = Palette.NavText;
        result.label.enableAutoSizing = true;
        result.label.fontSizeMin = FontSm;
        result.label.fontSizeMax = FontMdLg;

        var contentGo = new GameObject("ModeContent");
        contentGo.transform.SetParent(result.btn.transform, false);
        var contentRt = contentGo.AddComponent<RectTransform>();
        contentRt.anchorMin = Vector2.zero;
        contentRt.anchorMax = Vector2.one;
        contentRt.offsetMin = new Vector2(8f, 8f);
        contentRt.offsetMax = new Vector2(-8f, -8f);

        result.label.transform.SetParent(contentGo.transform, false);
        var labelRt = result.label.GetComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0, 1);
        labelRt.anchorMax = new Vector2(1, 1);
        labelRt.offsetMin = new Vector2(0, -32);
        labelRt.offsetMax = new Vector2(0, -8);

        RawImage icon = BuildRailIcon(contentGo.transform, iconFile);

        AddButtonDepth(result.btn.gameObject, text.GetHashCode());
        UI.AddPanelTexture(result.btn.transform, TexMid, text.GetHashCode());

        return (result.btn, result.label, icon);
    }

    private static RawImage BuildRailIcon(Transform parent, string iconFile)
    {
        Texture2D texture = LoadMenuIconTexture(iconFile);
        if (texture == null) return null;

        var iconGo = new GameObject("ModeIcon");
        iconGo.transform.SetParent(parent, false);
        var rt = iconGo.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(64f, 64f);
        rt.anchoredPosition = new Vector2(0f, -12f);

        var raw = iconGo.AddComponent<RawImage>();
        raw.texture = texture;
        raw.color = Palette.NavText;
        raw.raycastTarget = false;
        return raw;
    }

    internal static Texture2D LoadMenuIconTexture(string fileName)
    {
        // Whitened like every other icon surface; the rail keeps its own
        // Palette.NavText tint on top of the neutral glyph.
        return UpgradeIconLoader.LoadMenuIconTexture(fileName);
    }

    private static void AddButtonDepth(GameObject button, int seed)
    {
        if (button == null) return;

        AddBorder(button.transform, 4f, 0f, Palette.BorderDim);
    }

    private static void AddBackground(GameObject target, Color color, bool outline)
    {
        if (target == null) return;
        var img = target.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        UI.AddPanelTexture(target.transform, TexFaint, target.name.GetHashCode());
        if (outline) AddRuggedFrame(target.transform, FillHint);
    }

    // -
    //  BORDERS
    // -
    // One spec, two strokes. There used to be five entry points -
    // AddOutline / AddThinOutline / AddCleanBorder / AddDistressedBorder /
    // AddRuggedFrame - three of which were the same four-sided box differing
    // only in thickness, inset and alpha, and two of which differed only in
    // which line texture they drew with. AddBorder is now the single spec;
    // everything else is a named preset over it.
    private enum BorderStyle
    {
        /// <summary>Flat stroke. The default everywhere in the menu.</summary>
        Clean,
        /// <summary>Gapped, worn stroke from a procedural texture. The outermost frame only.</summary>
        Distressed
    }

    /// <summary>
    /// Clean borders use one sliced image so translucent corner pixels are never
    /// composited twice. The distressed outer frame keeps four non-overlapping lines.
    /// </summary>
    private static void AddBorder(Transform parent, float thickness, float inset, Color color,
        BorderStyle style = BorderStyle.Clean)
    {
        if (parent == null) return;

        float t = Mathf.Max(style == BorderStyle.Distressed ? 2f : 1f, thickness);
        float i = Mathf.Max(0f, inset);

        if (style == BorderStyle.Clean)
        {
            AddUnifiedBorderFrame(parent, t, i, color);
            return;
        }
        float joinInset = i + t;

        MakeBorderLine(parent, "BorderTop", style,
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(joinInset, -(i + t)), new Vector2(-joinInset, -i), color);
        MakeBorderLine(parent, "BorderBottom", style,
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(joinInset, i), new Vector2(-joinInset, i + t), color);
        MakeBorderLine(parent, "BorderLeft", style,
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(i, i), new Vector2(i + t, -i), color);
        MakeBorderLine(parent, "BorderRight", style,
            new Vector2(1, 0), new Vector2(1, 1),
            new Vector2(-(i + t), i), new Vector2(-i, -i), color);
    }

    /// <summary>A square or rounded frame rendered as one non-overlapping surface.</summary>
    private static void AddUnifiedBorderFrame(Transform parent, float thickness, float inset, Color color)
    {
        var frameGo = new GameObject("BorderFrame");
        frameGo.transform.SetParent(parent, false);
        var rt = frameGo.AddComponent<RectTransform>();
        float i = Mathf.Max(0f, inset);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(i, i);
        rt.offsetMax = new Vector2(-i, -i);

        var image = frameGo.AddComponent<Image>();
        int t = Mathf.Max(1, Mathf.RoundToInt(thickness));
        image.sprite = GetRoundedBorderSprite(0, t);
        image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
    }

    /// <summary>The heavy worn frame around the whole menu panel.</summary>
    private static void AddOutline(GameObject panel)
    {
        AddBorder(panel != null ? panel.transform : null, 6f, 0f, Palette.BorderHot,
            BorderStyle.Distressed);
    }

    /// <summary>Flush accent stroke on a panel that has no vanilla border of its own.</summary>
    private static void AddThinOutline(GameObject panel, float alpha = 0.75f)
    {
        if (panel == null) return;
        AddBorder(panel.transform, 3f, 0f, Palette.Alpha(Palette.Accent, alpha));
    }

    /// <summary>Inset accent stroke, one step in from a panel's edge.</summary>
    private static void AddRuggedFrame(Transform parent, float alpha)
    {
        AddBorder(parent, 3f, 4f, Palette.Alpha(Palette.Accent, alpha));
    }

    private static void SetBorderColor(Transform border, Color color)
    {
        if (border == null) return;

        var images = border.GetComponentsInChildren<Image>(true);
        for (int i = 0; i < images.Length; i++)
        {
            if (images[i] != null)
                images[i].color = color;
        }
    }

    private static void ApplyRoundedPanelShape(GameObject panel, float radius)
    {
        if (panel == null) return;
        var image = panel.GetComponent<Image>();
        if (image == null) return;
        image.sprite = GetRoundedFillSprite(Mathf.RoundToInt(radius));
        image.type = Image.Type.Sliced;
    }

    private static void AddRoundedFrame(Transform parent, float thickness, float inset, Color color, float radius)
    {
        if (parent == null) return;

        var frameGo = new GameObject("RoundedBorder");
        frameGo.transform.SetParent(parent, false);
        var rt = frameGo.AddComponent<RectTransform>();
        float i = Mathf.Max(0f, inset);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(i, i);
        rt.offsetMax = new Vector2(-i, -i);

        var image = frameGo.AddComponent<Image>();
        image.sprite = GetRoundedBorderSprite(Mathf.RoundToInt(radius), Mathf.RoundToInt(thickness));
        image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
    }

    private static Sprite GetRoundedFillSprite(int radius)
    {
        int r = Mathf.Clamp(radius, 2, 30);
        if (_roundedFillSprites.TryGetValue(r, out var cached) && cached != null)
            return cached;

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = IsInsideRoundedRect(x + 0.5f, y + 0.5f, size, size, r);
                tex.SetPixel(x, y, inside ? Color.white : Color.clear);
            }
        }

        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        _roundedFillSprites[r] = sprite;
        return sprite;
    }

    private static Sprite GetRoundedBorderSprite(int radius, int thickness)
    {
        int r = Mathf.Clamp(radius, 0, 30);
        int t = Mathf.Clamp(thickness, 1, 12);
        int key = (r << 8) ^ t;
        if (_roundedBorderSprites.TryGetValue(key, out var cached) && cached != null)
            return cached;

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f;
                float py = y + 0.5f;
                bool outer = IsInsideRoundedRect(px, py, size, size, r);
                bool inner = IsInsideRoundedRect(px - t, py - t, size - t * 2f, size - t * 2f, Mathf.Max(0, r - t));
                tex.SetPixel(x, y, outer && !inner ? Color.white : Color.clear);
            }
        }

        tex.Apply();
        int slice = Mathf.Max(r, t);
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, new Vector4(slice, slice, slice, slice));
        _roundedBorderSprites[key] = sprite;
        return sprite;
    }

    private static bool IsInsideRoundedRect(float x, float y, float width, float height, float radius)
    {
        if (width <= 0f || height <= 0f) return false;
        if (x < 0f || y < 0f || x > width || y > height) return false;

        float r = Mathf.Min(radius, Mathf.Min(width, height) * 0.5f);
        if (r <= 0f) return true;

        float cx = Mathf.Clamp(x, r, width - r);
        float cy = Mathf.Clamp(y, r, height - r);
        float dx = x - cx;
        float dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>
    /// A single decorative rule - a top accent, a bottom wear line, a tier gate
    /// stripe. Not part of a border; borders go through <see cref="AddBorder"/>.
    /// </summary>
    private static void MakeAccentLine(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
    {
        var lineGo = UI.MakePanel(name, parent, color, anchorMin, anchorMax, offsetMin, offsetMax);
        lineGo.GetComponent<Image>().raycastTarget = false;
    }

    private static void MakeBorderLine(Transform parent, string name, BorderStyle style,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
    {
        if (style == BorderStyle.Clean)
        {
            var flat = UI.MakePanel(name, parent, color, anchorMin, anchorMax, offsetMin, offsetMax);
            flat.GetComponent<Image>().raycastTarget = false;
            return;
        }

        var lineGo = new GameObject(name);
        lineGo.transform.SetParent(parent, false);
        var rt = lineGo.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;

        bool vertical = Mathf.Approximately(anchorMin.x, anchorMax.x)
            && Mathf.Abs(offsetMax.x - offsetMin.x) <= 12f;

        var raw = lineGo.AddComponent<RawImage>();
        raw.texture = GetRuggedLineTexture(name.GetHashCode(), vertical);
        raw.color = color;
        raw.uvRect = vertical ? new Rect(0, 0, 1, 18) : new Rect(0, 0, 18, 1);
        raw.raycastTarget = false;
    }

    private static Texture2D GetRuggedLineTexture(int seed, bool vertical)
    {
        int key = unchecked((seed * 397) ^ (vertical ? 0x2C56 : 0x7531));
        if (_ruggedLineTextures.TryGetValue(key, out var cached) && cached != null)
            return cached;

        int w = vertical ? 8 : 128;
        int h = vertical ? 128 : 8;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Repeat;

        var rng = new System.Random(key);
        int axis = vertical ? h : w;
        bool[] gaps = new bool[axis];
        for (int i = 0; i < axis;)
        {
            if (rng.NextDouble() < 0.10)
            {
                int len = rng.Next(2, 9);
                for (int j = 0; j < len && i + j < axis; j++)
                    gaps[i + j] = true;
                i += len;
            }
            else
            {
                i++;
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int along = vertical ? y : x;
                int across = vertical ? x : y;
                int acrossMax = vertical ? w - 1 : h - 1;

                float alpha = gaps[along] ? 0f : 1f;
                if (across == 0 || across == acrossMax)
                    alpha *= 0.34f;
                else if (across == 1 || across == acrossMax - 1)
                    alpha *= 0.72f;

                if (rng.NextDouble() < 0.035)
                    alpha *= 0.25f;
                if (rng.NextDouble() < 0.012)
                    alpha = 0f;

                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        tex.Apply();
        _ruggedLineTextures[key] = tex;
        return tex;
    }

    // -
    //  VIEWS
    // -

    private static void ShowView(GameObject view)
    {
        if (_employeeFileView != null)
            _employeeFileView.SetActive(view == _employeeFileView);
        if (_listView != null)
            _listView.SetActive(view == _listView);
        if (_tradeView != null)
            _tradeView.SetActive(view == _tradeView);
        if (_playerLevelView != null)
            _playerLevelView.SetActive(view == _playerLevelView);
        if (_detailView != null)
            _detailView.SetActive(true);
        _activeSectionView = view;
        UpdateNavTabs(view);

        if (view != _playerLevelView || _activeCosCategory != CosCatEmotes)
            StopEmotePreview();
    }

    private static void UpdateNavTabs(GameObject view)
    {
        for (int i = 0; i < _visibleSections.Count; i++)
        {
            SectionVisual visual = _sectionVisuals[i];
            if (visual == null) continue;

            bool active = _visibleSections[i].IsActive != null && _visibleSections[i].IsActive(view);
            SetNavButtonVisual(visual.Button, visual.Label, visual.Icon, active, Palette.NavText);
        }

        SetNavButtonVisual(_employeeFileButton, _employeeFileLabel, null,
            view == _employeeFileView, Palette.Accent);
    }

    private static void SetNavButtonVisual(Button btn, TMP_Text label, RawImage icon, bool active, Color inactiveText)
    {
        if (btn == null || label == null) return;

        Color bg = active
            ? Palette.Alpha(Palette.BtnActive, FillOpaque)
            : Palette.Alpha(Palette.BgRow, FillPlate);
        Color hl = active
            ? Palette.Alpha(Palette.BtnActive, 1f)
            : Palette.Alpha(Palette.BtnHighlight, FillStrong);
        Color txt = active ? Palette.Gold : inactiveText;

        var cb = btn.colors;
        cb.normalColor = bg;
        cb.highlightedColor = hl;
        cb.pressedColor = hl * 0.8f;
        cb.selectedColor = hl;
        cb.disabledColor = Palette.BtnDisabled;
        btn.colors = cb;

        if (btn.targetGraphic is Graphic g)
            g.color = bg;
        label.color = txt;
        label.fontStyle = FontStyles.Bold;

        if (icon != null)
            icon.color = txt;

        SetRailButtonOutlineColor(btn.transform, txt);
    }

    private static void SetRailButtonOutlineColor(Transform button, Color color)
    {
        if (button == null) return;
        for (int i = 0; i < button.childCount; i++)
        {
            Transform child = button.GetChild(i);
            if (child == null) continue;
            string name = child.name;
            if (name != "BorderTop"
                && name != "BorderBottom"
                && name != "BorderLeft"
                && name != "BorderRight"
                && name != "BorderFrame"
                && name != "RoundedBorder")
                continue;

            Graphic graphic = child.GetComponent<Graphic>();
            if (graphic != null)
                graphic.color = color;
        }
    }

    // - Employee File hub -
    public static void showEmployeeFile()
    {
        currentSelection = null;
        location = nameof(showEmployeeFile);
        _activeView = () => showEmployeeFile();
        ShowView(_employeeFileView);
        SetFooterHint(HintRecord);
        RefreshCurrencyDisplay();
        ClearFocusTargets();
        RenderEmployeePerformanceRecord();
        RenderEmployeeFileInspector();
        CommitFocusTargets();
    }

    private static void RenderEmployeePerformanceRecord()
    {
        if (_employeeFileView == null)
            return;

        foreach (Transform child in _employeeFileView.transform)
        {
            child.gameObject.SetActive(false);
            Object.Destroy(child.gameObject);
        }

        EmployeeStatisticsData stats = EmployeeStatistics.CurrentSnapshot;

        var header = UI.MakePanel("PerformanceHeader", _employeeFileView.transform, Palette.BgHeader,
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(14, -66), new Vector2(-14, -14));
        UI.AddPanelTexture(header.transform, TexMid, 7101);
        AddBorder(header.transform, 3f, 0f, Palette.BorderHot);

        TMP_Text title = UI.MakeText("PerformanceTitle", header.transform,
            "PERFORMANCE RECORD", FontTitle, Palette.Gold,
            TextAlignmentOptions.MidlineLeft);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0, 0.42f);
        titleRt.anchorMax = Vector2.one;
        titleRt.offsetMin = new Vector2(16, 0);
        titleRt.offsetMax = new Vector2(-16, -2);
        title.fontStyle = FontStyles.Bold;
        title.characterSpacing = 2f;

        TMP_Text subtitle = UI.MakeText("PerformanceSubtitle", header.transform,
            "LIFETIME TOTALS // ACTIVE ROUND INCLUDED", FontTiny, Palette.Body,
            TextAlignmentOptions.MidlineLeft);
        var subtitleRt = subtitle.GetComponent<RectTransform>();
        subtitleRt.anchorMin = Vector2.zero;
        subtitleRt.anchorMax = new Vector2(1, 0.42f);
        subtitleRt.offsetMin = new Vector2(16, 2);
        subtitleRt.offsetMax = new Vector2(-16, 0);
        subtitle.characterSpacing = 1f;

        // The record used to be ten fixed rows drawn straight onto the view, so
        // an eleventh statistic would have run off the bottom edge with nothing
        // to scroll. It is now four themed clusters inside a scroll strip: the
        // list can grow, and W/S walks it (#255).
        var (recordScroll, recordContent) = UI.MakeScrollView("PerformanceScroll",
            _employeeFileView.transform,
            new Vector2(0, 0), new Vector2(1, 1),
            new Vector2(Space4, Space4), new Vector2(-Space4, -RecordListTop),
            addScrollbar: true);
        var recordBg = recordScroll.AddComponent<Image>();
        recordBg.color = Palette.Alpha(Palette.BgInput, FillGhost);
        // Left as a raycast target so the wheel scrolls anywhere over the list.
        // Nothing inside the strip is clickable, so it swallows no input.
        recordBg.raycastTarget = true;
        UI.AddPanelTexture(recordScroll.transform, TexFaint, 7102);
        AddBorder(recordScroll.transform, 3f, 0f, Palette.BorderDim);
        ScrollRect recordRect = recordScroll.GetComponent<ScrollRect>();

        float y = Space2;
        int rowIndex = 0;
        var clusters = BuildPerformanceClusters(stats);
        for (int c = 0; c < clusters.Length; c++)
        {
            BuildPerformanceClusterHeader(recordContent, clusters[c].Title, y);
            y += RecordClusterHeaderH + Space1;

            PerformanceStat[] entries = clusters[c].Stats;
            for (int s = 0; s < entries.Length; s++)
            {
                BuildPerformanceRow(recordContent, entries[s], rowIndex, y, recordRect);
                y += RecordRowH + Space1;
                rowIndex++;
            }

            y += Space3;
        }

        var contentRt = recordContent.GetComponent<RectTransform>();
        contentRt.sizeDelta = new Vector2(0f, y);
        // Row heights here are explicit, so there is no layout pass to wait
        // for; the helper additionally makes sure the deferred re-pin never
        // undoes the scroll CommitFocusTargets does to bring a restored focus
        // row back into view.
        ResetScrollForRebuild(recordRect, contentRt);
    }

    /// <summary>One line of the performance record. Context is optional muted sub-text.</summary>
    private readonly struct PerformanceStat
    {
        internal readonly string Key;
        internal readonly string IconKey;
        internal readonly string Label;
        internal readonly string Value;
        internal readonly string Context;

        internal PerformanceStat(string key, string iconKey, string label, string value, string context)
        {
            Key = key;
            IconKey = iconKey;
            Label = label;
            Value = value;
            Context = context;
        }
    }

    private readonly struct PerformanceCluster
    {
        internal readonly string Title;
        internal readonly PerformanceStat[] Stats;

        internal PerformanceCluster(string title, PerformanceStat[] stats)
        {
            Title = title;
            Stats = stats;
        }
    }

    // Counters are grouped by the part of the job they describe rather than by declaration order.
    // Optional providers contribute complete blocks only when loaded, so the Employee File never
    // advertises unavailable work. Icon
    // keys are the upgrade glyphs under Assets/UI/UpgradeIcons; a row whose key
    // does not resolve falls back to an ASCII marker, never a missing-texture box.
    private static PerformanceCluster[] BuildPerformanceClusters(EmployeeStatisticsData stats)
    {
        int quotas = Mathf.Max(0, stats.quotasCompleted);

        // Secondary figures only where one number genuinely qualifies another.
        string distance = stats.stepsTaken > 0
            ? "~ " + (stats.stepsTaken * EmployeeStatistics.StepMeters).ToString("N0") + " M ON FOOT"
            : null;
        string beaconRate = quotas > 0
            ? (stats.surveyBeaconsPlaced / (float)quotas).ToString("N1") + " PER QUOTA"
            : null;
        string killRate = stats.timesDied > 0
            ? (stats.monstersKilled / (float)stats.timesDied).ToString("N2") + " PER DEATH"
            : null;
        string scrapRate = quotas > 0
            ? "$" + (stats.scrapValueDelivered / (float)quotas).ToString("N0") + " PER QUOTA"
            : null;

        var clusters = new List<PerformanceCluster>();
        var fieldWork = new List<PerformanceStat>
        {
            new PerformanceStat("stat.steps", "light_feet",
                "STEPS TAKEN", stats.stepsTaken.ToString("N0"), distance)
        };
        if (OptionalPluginCapabilities.Contracted)
        {
            fieldWork.Add(new PerformanceStat("stat.bombs", "escape_protocol",
                "BOMBS DEFUSED", stats.bombsDefused.ToString("N0"), null));
            fieldWork.Add(new PerformanceStat("stat.payload", "transporter",
                "PAYLOAD DISTANCE PUSHED",
                stats.payloadDistancePushedMeters.ToString("N1") + " M", null));
            fieldWork.Add(new PerformanceStat("stat.beacons", "worklight_beacon",
                "SURVEY BEACONS PLACED", stats.surveyBeaconsPlaced.ToString("N0"), beaconRate));
            fieldWork.Add(new PerformanceStat("stat.incinerated", "scavenger",
                "ITEMS INCINERATED", stats.itemsIncinerated.ToString("N0"), null));
            fieldWork.Add(new PerformanceStat("stat.whistleblower_damage", "lethal_hands",
                "DAMAGE DEALT TO WHISTLEBLOWER", stats.whistleblowerDamageDealt.ToString("N0"), null));
            fieldWork.Add(new PerformanceStat("stat.pests", "quick_hands",
                "PESTS TRAPPED", stats.pestsTrapped.ToString("N0"), null));
            fieldWork.Add(new PerformanceStat("stat.breach_waves", "resilience",
                "CONTAINMENT WAVES SURVIVED", stats.containmentWavesSurvived.ToString("N0"), null));
            fieldWork.Add(new PerformanceStat("stat.drills", "turret_hacker",
                "DRILLS PLACED", stats.drillsPlaced.ToString("N0"), null));
            fieldWork.Add(new PerformanceStat("stat.breakers", "field_operations",
                "BREAKERS RESTORED", stats.breakersRestored.ToString("N0"), null));
        }
        clusters.Add(new PerformanceCluster("FIELD WORK", fieldWork.ToArray()));

        clusters.Add(new PerformanceCluster("COMBAT", new[]
        {
            new PerformanceStat("stat.kills", "lethal_hands",
                "MONSTERS KILLED", stats.monstersKilled.ToString("N0"), killRate),
            new PerformanceStat("stat.deaths", "deathbound",
                "TIMES DIED", stats.timesDied.ToString("N0"), null)
        }));

        var operations = new List<PerformanceStat>
        {
            new PerformanceStat("stat.hacks", "turret_hacker",
                "DEVICES HACKED", stats.devicesHacked.ToString("N0"), null)
        };
        if (OptionalPluginCapabilities.LethalCctv)
        {
            operations.Add(new PerformanceStat("stat.cctv", "better_scanner",
                "TIME SPENT ON CCTV",
                EmployeeStatistics.FormatDuration(stats.timeSpentOnCctvSeconds), null));
        }
        clusters.Add(new PerformanceCluster("OPERATIONS", operations.ToArray()));

        clusters.Add(new PerformanceCluster("ECONOMY", new[]
        {
            new PerformanceStat("stat.scrap", "scavenger",
                "SCRAP VALUE DELIVERED", "$" + stats.scrapValueDelivered.ToString("N0"), scrapRate),
            new PerformanceStat("stat.quotas", "quota_guard",
                "QUOTAS COMPLETED", stats.quotasCompleted.ToString("N0"), null)
        }));
        return clusters.ToArray();
    }

    private static void BuildPerformanceClusterHeader(Transform parent, string title, float yTop)
    {
        var label = UI.MakeText("RecordCluster_" + title, parent,
            title, FontXs, Palette.Alpha(Palette.Accent, 0.92f),
            TextAlignmentOptions.MidlineLeft);
        label.fontStyle = FontStyles.Bold;
        label.characterSpacing = 3f;
        label.raycastTarget = false;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        var rt = label.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.offsetMin = new Vector2(Space3, -(yTop + RecordClusterHeaderH));
        rt.offsetMax = new Vector2(-Space3, -yTop);

        MakeAccentLine(parent, "RecordClusterRule_" + title,
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(Space3, -(yTop + RecordClusterHeaderH - 2f)),
            new Vector2(-Space3, -(yTop + RecordClusterHeaderH - 3f)),
            Palette.Alpha(Palette.Accent, FillWash));
    }

    private static void BuildPerformanceRow(
        Transform parent, PerformanceStat stat, int index, float yTop, ScrollRect scroller)
    {
        // Wide stripe: the band and the wash have to read as clearly different
        // rows, which two near-equal alphas would not.
        Color background = index % 2 == 0
            ? Palette.Alpha(Palette.BgRow, FillBand)
            : Palette.Alpha(Palette.BgInput, FillSoft);
        var row = UI.MakePanel("PerformanceRow_" + stat.Key, parent, background,
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(Space2, -(yTop + RecordRowH)), new Vector2(-Space2, -yTop));
        row.GetComponent<Image>().raycastTarget = false;
        UI.AddPanelTexture(row.transform, TexFaint, 7200 + index);

        var rail = UI.MakePanel("RecordRail", row.transform, Palette.BorderHot,
            Vector2.zero, new Vector2(0, 1),
            Vector2.zero, new Vector2(Space1, 0));
        rail.GetComponent<Image>().raycastTarget = false;

        BuildPerformanceRowIcon(row.transform, stat.IconKey);

        bool hasContext = !string.IsNullOrEmpty(stat.Context);
        TMP_Text label = UI.MakeText("Label", row.transform,
            stat.Label, FontMd, Palette.Body, TextAlignmentOptions.MidlineLeft);
        label.fontStyle = FontStyles.Bold;
        label.characterSpacing = 1f;
        label.raycastTarget = false;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        var labelRt = label.GetComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0, hasContext ? 0.44f : 0f);
        labelRt.anchorMax = new Vector2(0.66f, 1);
        labelRt.offsetMin = new Vector2(RecordTextLeft, 0);
        labelRt.offsetMax = new Vector2(-Space1, hasContext ? -Space1 : 0);

        if (hasContext)
        {
            TMP_Text context = UI.MakeText("Context", row.transform,
                stat.Context, FontTiny, Palette.Muted, TextAlignmentOptions.MidlineLeft);
            context.raycastTarget = false;
            context.enableWordWrapping = false;
            context.overflowMode = TextOverflowModes.Ellipsis;
            var contextRt = context.GetComponent<RectTransform>();
            contextRt.anchorMin = new Vector2(0, 0);
            contextRt.anchorMax = new Vector2(0.66f, 0.44f);
            contextRt.offsetMin = new Vector2(RecordTextLeft, Space1);
            contextRt.offsetMax = new Vector2(-Space1, 0);
        }

        TMP_Text value = UI.MakeText("Value", row.transform,
            stat.Value, FontLg, Palette.Gold, TextAlignmentOptions.MidlineRight);
        value.fontStyle = FontStyles.Bold;
        value.raycastTarget = false;
        value.enableWordWrapping = false;
        value.overflowMode = TextOverflowModes.Ellipsis;
        value.enableAutoSizing = true;
        value.fontSizeMin = FontSm;
        value.fontSizeMax = FontLg;
        var valueRt = value.GetComponent<RectTransform>();
        valueRt.anchorMin = new Vector2(0.62f, 0);
        valueRt.anchorMax = Vector2.one;
        valueRt.offsetMin = new Vector2(Space1, 0);
        valueRt.offsetMax = new Vector2(-Space4, 0);

        // Readable, not actionable: no Button, so ENTER on a record row is a
        // deny rather than a silent nothing. W/S still scrolls it into view.
        RegisterFocusTarget(stat.Key, row.GetComponent<RectTransform>(), null, index, 0, scroller);
    }

    private static void BuildPerformanceRowIcon(Transform parent, string iconKey)
    {
        Texture2D texture = LoadUpgradeIconTexture(iconKey);
        if (texture == null)
        {
            // No fitting glyph on disk: an ASCII marker, never an empty box.
            var marker = UI.MakeText("IconMarker", parent, ">",
                FontMd, Palette.Alpha(Palette.Accent, 0.72f), TextAlignmentOptions.Center);
            marker.fontStyle = FontStyles.Bold;
            marker.raycastTarget = false;
            var markerRt = marker.GetComponent<RectTransform>();
            markerRt.anchorMin = new Vector2(0, 0.5f);
            markerRt.anchorMax = new Vector2(0, 0.5f);
            markerRt.pivot = new Vector2(0.5f, 0.5f);
            markerRt.sizeDelta = new Vector2(RecordIconSize, RecordIconSize);
            markerRt.anchoredPosition = new Vector2(Space3 + RecordIconSize * 0.5f, 0f);
            return;
        }

        var paneGo = new GameObject("IconPane");
        paneGo.transform.SetParent(parent, false);
        var paneRt = paneGo.AddComponent<RectTransform>();
        paneRt.anchorMin = new Vector2(0, 0.5f);
        paneRt.anchorMax = new Vector2(0, 0.5f);
        paneRt.pivot = new Vector2(0.5f, 0.5f);
        paneRt.sizeDelta = new Vector2(RecordIconSize, RecordIconSize);
        paneRt.anchoredPosition = new Vector2(Space3 + RecordIconSize * 0.5f, 0f);

        var iconGo = new GameObject("Icon");
        iconGo.transform.SetParent(paneGo.transform, false);
        var raw = iconGo.AddComponent<RawImage>();
        raw.texture = texture;
        raw.color = Palette.Alpha(Palette.Accent, 0.88f);
        raw.raycastTarget = false;
        var rt = raw.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(RecordIconSize, RecordIconSize);
        rt.anchoredPosition = Vector2.zero;
        var fitter = iconGo.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = texture.width > 0 && texture.height > 0
            ? (float)texture.width / texture.height
            : 1f;
    }

    private static void RenderEmployeeFileInspector()
    {
        if (_detailView == null)
            return;

        ClearDetailViewChildren();

        var stack = new InspectorStack(_detailView.transform, InspectorTop);
        BuildPlayerPreviewPlaceholder(stack);

        // COLLECTION and AMMUNITION both grow with content that is defined
        // elsewhere - a new skill tree, a new ammunition family - so they live
        // in a scroll view that owns the whole strip below the preview. Their
        // heights are computed from their row counts and stacked inside it, so
        // overflow scrolls instead of drawing past the inspector's bottom edge.
        var (listScroll, listContent) = UI.MakeScrollView("EmployeeFileScroll",
            _detailView.transform,
            Vector2.zero, Vector2.one,
            new Vector2(InspectorSide, InspectorGap),
            new Vector2(-InspectorSide, -stack.Cursor),
            addScrollbar: false);
        var listStack = new InspectorStack(listContent, 0f, side: 0f);

        var ghost = GetSkillTreeCollectionCount(Y4NGZSkillTreeClass.Ghost);
        var technician = GetSkillTreeCollectionCount(Y4NGZSkillTreeClass.Technician);
        var enforcer = GetSkillTreeCollectionCount(Y4NGZSkillTreeClass.Enforcer);
        var foreman = GetSkillTreeCollectionCount(Y4NGZSkillTreeClass.Foreman);
        var suits = GetSuitCollectionCount();
        var cosmetics = GetCosmeticCollectionCount();
        var emotes = GetEmoteCollectionCount();

        var collectionRows = new List<(string label, int owned, int total)>
        {
            ("GHOST UPGRADES", ghost.owned, ghost.total),
            ("TECHNICIAN UPGRADES", technician.owned, technician.total),
            ("ENFORCER UPGRADES", enforcer.owned, enforcer.total),
            ("FOREMAN UPGRADES", foreman.owned, foreman.total),
            ("SUITS", suits.owned, suits.total)
        };
        if (cosmetics.total > 0)
            collectionRows.Add(("COSMETICS", cosmetics.owned, cosmetics.total));
        if (emotes.total > 0)
            collectionRows.Add(("EMOTES", emotes.owned, emotes.total));

        // The ammo reserve lives in Better Armory, so the families and their balances come across
        // the bridge (#267). Without that plugin the array is empty and the panel is not built at
        // all - there is no ammunition to report on an Upgrades-only install.
        string[] ammoFamilyIds = ArmoryBridge.IsArmoryPresent
            ? ArmoryBridge.GetAmmoFamilyIds()
            : Array.Empty<string>();

        var collection = listStack.Panel("CollectionPanel", Palette.BgInput,
            CollectionPanelHeight(collectionRows.Count));
        UI.AddPanelTexture(collection.transform, TexLow, 7301);
        AddBorder(collection.transform, 3f, 0f, Palette.BorderHot);

        TMP_Text collectionTitle = UI.MakeText("CollectionTitle", collection.transform,
            "COLLECTION", FontMd, Palette.Gold,
            TextAlignmentOptions.MidlineLeft);
        var collectionTitleRt = collectionTitle.GetComponent<RectTransform>();
        collectionTitleRt.anchorMin = new Vector2(0, 1);
        collectionTitleRt.anchorMax = new Vector2(1, 1);
        collectionTitleRt.offsetMin = new Vector2(10, -26);
        collectionTitleRt.offsetMax = new Vector2(-10, -3);
        collectionTitle.fontStyle = FontStyles.Bold;
        collectionTitle.characterSpacing = 2f;

        for (int i = 0; i < collectionRows.Count; i++)
            BuildEmployeeCollectionRow(collection.transform, collectionRows[i], i);

        if (ArmoryBridge.IsArmoryPresent && ammoFamilyIds.Length > 0)
        {
            var ammunition = listStack.Panel("AmmunitionPanel", Palette.BgInput,
                AmmoPanelHeight(ammoFamilyIds.Length));
            UI.AddPanelTexture(ammunition.transform, TexLow, 7401);
            AddBorder(ammunition.transform, 3f, 0f, Palette.BorderHot);

            TMP_Text ammunitionTitle = UI.MakeText("AmmunitionTitle", ammunition.transform,
                "AMMUNITION", FontMd, Palette.Gold,
                TextAlignmentOptions.MidlineLeft);
            var ammunitionTitleRt = ammunitionTitle.GetComponent<RectTransform>();
            ammunitionTitleRt.anchorMin = new Vector2(0, 1);
            ammunitionTitleRt.anchorMax = new Vector2(1, 1);
            ammunitionTitleRt.offsetMin = new Vector2(10, -27);
            ammunitionTitleRt.offsetMax = new Vector2(-10, -3);
            ammunitionTitle.fontStyle = FontStyles.Bold;
            ammunitionTitle.characterSpacing = 2f;

            for (int i = 0; i < ammoFamilyIds.Length; i++)
            {
                BuildEmployeeAmmoRow(
                    ammunition.transform,
                    ArmoryBridge.GetAmmoFamilyLabel(ammoFamilyIds[i]),
                    ArmoryBridge.GetAmmoFamilyReserve(ammoFamilyIds[i]),
                    i);
            }
        }

        var listContentRt = listContent.GetComponent<RectTransform>();
        listContentRt.sizeDelta = new Vector2(0, listStack.Cursor);
        // showEmployeeFile commits focus after this renderer, so the reset has
        // to yield to a pending focus restore rather than re-pin over it.
        ResetScrollForRebuild(listScroll.GetComponent<ScrollRect>(), listContentRt);
    }

    // Row metrics for the two Employee File summary panels. Each panel's height
    // is derived from its row count so the panel grows with its content instead
    // of a hand-measured bottom edge going stale (a sixth ammunition family used
    // to draw past the old -617 offset).
    private const float CollectionRowTop    = 28f;
    private const float CollectionRowH      = 21f;
    private const float CollectionRowGap    =  1f;
    private const float CollectionRowFoot   =  9f;
    private const float AmmoRowTop          = 29f;
    private const float AmmoRowH            = 27f;
    private const float AmmoRowFoot         = 12f;

    private static float CollectionPanelHeight(int rows)
    {
        if (rows <= 0) return CollectionRowTop + CollectionRowFoot;
        return CollectionRowTop + rows * CollectionRowH
            + (rows - 1) * CollectionRowGap + CollectionRowFoot;
    }

    private static float AmmoPanelHeight(int rows)
    {
        if (rows <= 0) return AmmoRowTop + AmmoRowFoot;
        return AmmoRowTop + rows * AmmoRowH - 1f + AmmoRowFoot;
    }

    private static void BuildEmployeeCollectionRow(
        Transform parent,
        (string label, int owned, int total) row,
        int index)
    {
        float top = CollectionRowTop + index * (CollectionRowH + CollectionRowGap);

        var rowGo = UI.MakePanel("CollectionRow_" + index, parent,
            index % 2 == 0
                ? Palette.Alpha(Palette.BgRow, FillRow)
                : Palette.Transparent,
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(7, -(top + CollectionRowH)), new Vector2(-7, -top));

        TMP_Text label = UI.MakeText("Label", rowGo.transform, row.label,
            FontTiny, Palette.Body, TextAlignmentOptions.MidlineLeft);
        var labelRt = label.GetComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0, 0.16f);
        labelRt.anchorMax = new Vector2(0.76f, 1);
        labelRt.offsetMin = new Vector2(6, 0);
        labelRt.offsetMax = new Vector2(-2, 0);
        label.fontStyle = FontStyles.Bold;

        TMP_Text count = UI.MakeText("Count", rowGo.transform,
            Mathf.Max(0, row.owned).ToString("N0") + " / " + Mathf.Max(0, row.total).ToString("N0"),
            FontXs, Palette.Gold, TextAlignmentOptions.MidlineRight);
        var countRt = count.GetComponent<RectTransform>();
        countRt.anchorMin = new Vector2(0.66f, 0.16f);
        countRt.anchorMax = Vector2.one;
        countRt.offsetMin = Vector2.zero;
        countRt.offsetMax = new Vector2(-6, 0);
        count.fontStyle = FontStyles.Bold;

        float ratio = row.total > 0 ? Mathf.Clamp01((float)row.owned / row.total) : 0f;
        var barBackground = UI.MakePanel("ProgressBackground", rowGo.transform,
            Palette.Alpha(Palette.BorderDim, FillMuted),
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(6, 2), new Vector2(-6, 4));
        var fill = UI.MakePanel("ProgressFill", barBackground.transform, Palette.Gold,
            Vector2.zero, new Vector2(ratio, 1), Vector2.zero, Vector2.zero);
        fill.GetComponent<Image>().raycastTarget = false;
    }

    private static void BuildEmployeeAmmoRow(Transform parent, string labelText, int amount, int index)
    {
        float top = AmmoRowTop + index * AmmoRowH;
        var row = UI.MakePanel("AmmoSummaryRow_" + index, parent,
            index % 2 == 0
                ? Palette.Alpha(Palette.BgRow, FillRowAlt)
                : Palette.Transparent,
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(7, -(top + AmmoRowH - 1f)), new Vector2(-7, -top));

        TMP_Text label = UI.MakeText("Label", row.transform, labelText,
            FontXs, Palette.Body, TextAlignmentOptions.MidlineLeft);
        var labelRt = label.GetComponent<RectTransform>();
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = new Vector2(0.82f, 1);
        labelRt.offsetMin = new Vector2(8, 0);
        labelRt.offsetMax = new Vector2(-2, 0);
        label.enableAutoSizing = true;
        label.fontSizeMin = FontMicro;
        label.fontSizeMax = FontXs;

        TMP_Text value = UI.MakeText("Amount", row.transform,
            Mathf.Max(0, amount).ToString("N0"), FontMd, Palette.Gold,
            TextAlignmentOptions.MidlineRight);
        var valueRt = value.GetComponent<RectTransform>();
        valueRt.anchorMin = new Vector2(0.78f, 0);
        valueRt.anchorMax = Vector2.one;
        valueRt.offsetMin = Vector2.zero;
        valueRt.offsetMax = new Vector2(-8, 0);
        value.fontStyle = FontStyles.Bold;
    }

    private static (int owned, int total) GetSkillTreeCollectionCount(Y4NGZSkillTreeClass treeClass)
    {
        IReadOnlyList<Y4NGZSkillTreeNodeDefinition> nodes = Y4NGZUpgradeManager.GetSkillTreeNodes(treeClass);
        int total = nodes?.Count ?? 0;
        int owned = 0;
        if (nodes != null)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] != null && Y4NGZUpgradeManager.GetLevel(nodes[i].UpgradeId) > 0)
                    owned++;
            }
        }
        return (owned, total);
    }

    private static (int owned, int total) GetSuitCollectionCount()
    {
        var unlockables = StartOfRound.Instance?.unlockablesList?.unlockables;
        if (unlockables == null)
            return (0, 0);

        PlayerLevelData data = PlayerLevelStore.Get();
        int owned = 0;
        int total = 0;
        for (int i = 0; i < unlockables.Count; i++)
        {
            var unlockable = unlockables[i];
            if (unlockable == null || unlockable.unlockableType != 0 || unlockable.suitMaterial == null)
                continue;

            total++;
            string name = !string.IsNullOrEmpty(unlockable.unlockableName)
                ? unlockable.unlockableName
                : "Suit " + i;
            if (unlockable.alreadyUnlocked || (data.suits?.Contains(name) ?? false))
                owned++;
        }
        return (owned, total);
    }

    private static (int owned, int total) GetCosmeticCollectionCount()
    {
        ResolveMCReflection();
        IDictionary dictionary = _mcInstancesField?.GetValue(null) as IDictionary;
        if (dictionary == null)
            return (0, 0);

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in dictionary)
        {
            string id = entry.Key as string;
            if (!string.IsNullOrEmpty(id) && entry.Value != null)
                available.Add(id);
        }

        PlayerLevelData data = PlayerLevelStore.Get();
        int owned = data.cosmetics == null
            ? 0
            : data.cosmetics
                .Where(id => !string.IsNullOrEmpty(id) && available.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        return (owned, available.Count);
    }

    private static (int owned, int total) GetEmoteCollectionCount()
    {
        EnsureEmotesReflection();
        var emotes = GetAllPurchasableEmotes();
        if (emotes == null)
            return (0, 0);

        int owned = 0;
        for (int i = 0; i < emotes.Count; i++)
        {
            if (TmeIsEmoteUnlocked(emotes[i].obj))
                owned++;
        }
        return (owned, emotes.Count);
    }

    // - Upgrade list -
    // resetScroll: when true, snap content back to the top after rebuild.
    // Default false so back-button returns from the detail view preserve
    // the user's scroll position. Tab/category switches pass true.
    public static void showUpgrades(bool shared, bool resetScroll = false)
    {
        string previousNodeId = !string.IsNullOrEmpty(_selectedSkillTreeNodeId)
            ? _selectedSkillTreeNodeId
            : currentSelection?.Id;
        location = nameof(showUpgrades) + "," + shared;
        _activeView = () => showUpgrades(shared, resetScroll: false);
        ShowView(_listView);
        // The full hint is set only once there is a tree to walk: both early
        // returns below register zero focus targets, so advertising W/S/A/D
        // there would promise bindings that do nothing.
        SetFooterHint(HintMinimal);
        RefreshCurrencyDisplay();
        ClearContent();
        ClearFocusTargets();

        if (_scrollRect != null)
        {
            _scrollRect.horizontal = false;
            _scrollRect.vertical = false;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 0f;
        }

        List<Y4NGZSkillTreeDefinition> trees = Y4NGZUpgradeManager.GetSkillTrees().ToList();
        if (trees.Count == 0)
        {
            RenderInspectorPlaceholder("UPGRADES", "No skill-tree data is registered.");
            var emptyRt = _scrollContent.GetComponent<RectTransform>();
            emptyRt.sizeDelta = Vector2.zero;
            return;
        }

        EnsureActiveSkillTree(trees, previousNodeId);
        Y4NGZSkillTreeDefinition activeTree = trees.FirstOrDefault(x => x.TreeClass == _activeSkillTreeClass)
            ?? trees[0];
        _activeSkillTreeClass = activeTree.TreeClass;

        Dictionary<string, Y4NGZUpgradeNode> upgradesById = UpgradeApi.GetUpgradeNodes()
            .Where(x => x.SharedUpgrade == shared)
            .Where(x => !HiddenUpgrades.Contains(x.Name))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        List<Y4NGZSkillTreeNodeDefinition> nodes = Y4NGZUpgradeManager
            .GetSkillTreeNodes(activeTree.TreeClass)
            .ToList();

        if (nodes.Count == 0)
        {
            BuildSkillTreeNavigation(trees, activeTree, SkillTreeTabsTop, SkillTreeTabsH);
            RenderSkillTreeClassOverview(activeTree);
            var emptyRt = _scrollContent.GetComponent<RectTransform>();
            emptyRt.sizeDelta = new Vector2(0f, 110f);
            return;
        }

        SetFooterHint(HintSkillTree);

        int treeInvestment = GetTreeInvestment(activeTree.TreeClass, upgradesById);
        SkillTreeLayout layout = BuildSkillTreeLayout(nodes);

        BuildSkillTreeNavigation(trees, activeTree, SkillTreeTabsTop, SkillTreeTabsH);
        BuildSkillTreeBackdrop(layout, activeTree.TreeClass);
        BuildSkillTreeTierLabels(activeTree, treeInvestment, layout);
        BuildSkillTreeConnections(nodes, upgradesById, layout);

        Y4NGZSkillTreeNodeDefinition selectedNode = SelectSkillTreeNodeForRender(nodes, previousNodeId);
        for (int i = 0; i < nodes.Count; i++)
            BuildSkillTreeNode(nodes[i], upgradesById, treeInvestment, layout, selectedNode);

        var crt = _scrollContent.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(0f, layout.ContentHeight);
        if (resetScroll)
        {
            crt.anchoredPosition = Vector2.zero;
            if (_scrollRect != null)
            {
                _scrollRect.horizontalNormalizedPosition = 0f;
                _scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        if (selectedNode != null)
        {
            _selectedSkillTreeNodeId = selectedNode.UpgradeId;
            upgradesById.TryGetValue(selectedNode.UpgradeId, out Y4NGZUpgradeNode selectedUpgrade);
            RenderSkillTreeSelection(selectedNode, selectedUpgrade, treeInvestment);
        }
        else
        {
            currentSelection = null;
            _selectedSkillTreeNodeId = null;
            RenderSkillTreeClassOverview(activeTree);
        }

        CommitFocusTargets();
    }

    private sealed class SkillTreeLayout
    {
        public float NavTop = SkillTreeTabsTop;
        // Tabs end at 52; the glass opens SkillTreeTabsGap below that and
        // TreeTop is SkillTreeGlassRise further down, leaving the glass a band
        // of its own for the first tier caption. 88 today - the 20px the
        // removed token readout used to occupy (#258) went back to the tree.
        public float TreeTop =
            SkillTreeTabsTop + SkillTreeTabsH + SkillTreeTabsGap + SkillTreeGlassRise;
        public float Left = 42f;
        public float NodeW = 94f;
        public float NodeH = 94f;
        public float NodeGap = 108f;
        public float MinNodeGap = 26f;
        public float SidePad = 22f;
        public float TierRowH = 112f;
        public float ColumnW => NodeW + NodeGap;
        public float ContentWidth;
        public float ContentHeight;
        public readonly Dictionary<string, Vector2> NodePositions =
            new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);

        // Grid address of each node - x is the tier row index counted from the
        // top of the drawing (tier 4 first), y is the node's slot inside it.
        // Keyboard navigation walks these; nothing about the pixel layout has
        // to be re-derived to know what is left of what.
        public readonly Dictionary<string, Vector2Int> NodeCells =
            new Dictionary<string, Vector2Int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Authored tier number and drawing top, in row order.</summary>
        public readonly List<(int Tier, float Top)> TierRows = new List<(int, float)>();

        public Vector2Int NodeCell(Y4NGZSkillTreeNodeDefinition node)
        {
            if (node != null && NodeCells.TryGetValue(node.UpgradeId, out Vector2Int cell))
                return cell;

            return Vector2Int.zero;
        }

        public Vector2 NodeTopLeft(Y4NGZSkillTreeNodeDefinition node)
        {
            if (node != null && NodePositions.TryGetValue(node.UpgradeId, out Vector2 position))
                return position;

            return new Vector2(Left, TreeTop);
        }

        public Vector2 NodeCenter(Y4NGZSkillTreeNodeDefinition node)
        {
            Vector2 topLeft = NodeTopLeft(node);
            return new Vector2(topLeft.x + NodeW * 0.5f, topLeft.y + NodeH * 0.5f);
        }

        public Vector2 NodeTopCenter(Y4NGZSkillTreeNodeDefinition node)
        {
            Vector2 topLeft = NodeTopLeft(node);
            return new Vector2(topLeft.x + NodeW * 0.5f, topLeft.y);
        }

        public Vector2 NodeBottomCenter(Y4NGZSkillTreeNodeDefinition node)
        {
            Vector2 topLeft = NodeTopLeft(node);
            return new Vector2(topLeft.x + NodeW * 0.5f, topLeft.y + NodeH);
        }
    }

    private static SkillTreeLayout BuildSkillTreeLayout(List<Y4NGZSkillTreeNodeDefinition> nodes)
    {
        var layout = new SkillTreeLayout();
        float viewportWidth = 0f;
        if (_scrollRect != null && _scrollRect.viewport != null)
            viewportWidth = _scrollRect.viewport.rect.width;
        float viewportHeight = 0f;
        if (_scrollRect != null && _scrollRect.viewport != null)
            viewportHeight = _scrollRect.viewport.rect.height;

        // Node positions are anchored to the scroll content, and the tree
        // backdrop stretches across that same rect, so the content width is
        // the only width the cluster may be centred against. Measuring the
        // viewport instead - or clamping it up to a design minimum - centres
        // the tree on a box that is not the one being drawn.
        float contentWidth = 0f;
        RectTransform contentRt = _scrollContent != null
            ? _scrollContent.GetComponent<RectTransform>()
            : null;
        if (contentRt != null)
            contentWidth = contentRt.rect.width;
        if (contentWidth <= 10f)
            contentWidth = viewportWidth;
        if (contentWidth <= 10f)
            contentWidth = 542f;
        layout.ContentWidth = contentWidth;

        List<IGrouping<int, Y4NGZSkillTreeNodeDefinition>> tiers = nodes
            .GroupBy(x => Mathf.Clamp(x.Tier, 1, 4))
            .OrderByDescending(x => x.Key)
            .ToList();

        var tierRows = new List<List<Y4NGZSkillTreeNodeDefinition>>(tiers.Count);
        int widestTier = 0;
        for (int i = 0; i < tiers.Count; i++)
        {
            List<Y4NGZSkillTreeNodeDefinition> tierNodes = tiers[i]
                .OrderBy(x => x.Row)
                .ThenBy(x => x.Column)
                .ThenBy(x => x.DisplayName)
                .ToList();
            tierRows.Add(tierNodes);
            widestTier = Mathf.Max(widestTier, tierNodes.Count);
        }

        // Squeeze the gap - never the 94px boxes - until the widest tier row
        // clears the backdrop border on both sides. The gap is only ever
        // reduced, so trees that already fit keep their authored spacing.
        float usableWidth = Mathf.Max(layout.NodeW, layout.ContentWidth - layout.SidePad * 2f);
        if (widestTier > 1)
        {
            float fitGap = (usableWidth - widestTier * layout.NodeW) / (widestTier - 1);
            layout.NodeGap = Mathf.Clamp(fitGap, layout.MinNodeGap, layout.NodeGap);
        }

        for (int i = 0; i < tierRows.Count; i++)
        {
            List<Y4NGZSkillTreeNodeDefinition> tierNodes = tierRows[i];
            float totalWidth = tierNodes.Count * layout.NodeW + Mathf.Max(0, tierNodes.Count - 1) * layout.NodeGap;
            float startX = (layout.ContentWidth - totalWidth) * 0.5f;
            float y = layout.TreeTop + i * layout.TierRowH;
            layout.TierRows.Add((tierNodes.Count > 0 ? Mathf.Clamp(tierNodes[0].Tier, 1, 4) : 1, y));

            for (int n = 0; n < tierNodes.Count; n++)
            {
                layout.NodePositions[tierNodes[n].UpgradeId] = new Vector2(startX + n * (layout.NodeW + layout.NodeGap), y);
                layout.NodeCells[tierNodes[n].UpgradeId] = new Vector2Int(i, n);
            }
        }

        int tierCount = Mathf.Max(1, tiers.Count);
        float naturalHeight = layout.TreeTop + layout.NodeH + (tierCount - 1) * layout.TierRowH + 18f;
        layout.ContentHeight = Mathf.Max(naturalHeight, viewportHeight - 4f);
        return layout;
    }

    private static void EnsureActiveSkillTree(List<Y4NGZSkillTreeDefinition> trees, string previousNodeId)
    {
        if (trees == null || trees.Count == 0)
            return;

        if (!string.IsNullOrEmpty(previousNodeId))
        {
            Y4NGZSkillTreeNodeDefinition previousNode = Y4NGZUpgradeManager.GetSkillTreeNode(previousNodeId);
            if (previousNode != null && trees.Any(x => x.TreeClass == previousNode.TreeClass))
            {
                _activeSkillTreeClass = previousNode.TreeClass;
                return;
            }
        }

        if (!trees.Any(x => x.TreeClass == _activeSkillTreeClass))
            _activeSkillTreeClass = trees[0].TreeClass;
    }

    private static Y4NGZSkillTreeNodeDefinition SelectSkillTreeNodeForRender(
        List<Y4NGZSkillTreeNodeDefinition> nodes,
        string previousNodeId)
    {
        if (nodes == null || nodes.Count == 0)
            return null;

        if (!string.IsNullOrEmpty(previousNodeId))
        {
            Y4NGZSkillTreeNodeDefinition previous = nodes.FirstOrDefault(x =>
                string.Equals(x.UpgradeId, previousNodeId, StringComparison.OrdinalIgnoreCase));
            if (previous != null)
                return previous;
        }

        return null;
    }

    private static void BuildSkillTreeNavigation(
        List<Y4NGZSkillTreeDefinition> trees,
        Y4NGZSkillTreeDefinition activeTree,
        float yTop,
        float height)
    {
        if (trees == null || trees.Count == 0)
            return;

        const float outerPad = 4f;
        const float innerGap = 2f;
        float tabW = 1f / Mathf.Max(1, trees.Count);
        for (int i = 0; i < trees.Count; i++)
        {
            Y4NGZSkillTreeDefinition tree = trees[i];
            bool active = tree.TreeClass == activeTree.TreeClass;
            Color accent = Palette.Accent;
            Color bg = active
                ? Palette.BtnActive
                : Palette.Alpha(Palette.BgRow, FillGhost);
            Color hover = active
                ? Palette.Alpha(Palette.BtnActive, 1f)
                : Palette.Alpha(Palette.BgRowHover, FillSoft);
            Color text = active
                ? accent
                : Palette.Alpha(Palette.Dim, 0.54f);
            float leftPad = i == 0 ? outerPad : innerGap;
            float rightPad = i == trees.Count - 1 ? outerPad : innerGap;

            string caption = tree.DisplayName.ToUpperInvariant();

            var (btn, lbl) = UI.MakeButton(
                "TreeTab_" + tree.DisplayName,
                _scrollContent,
                caption,
                FontMdLg,
                bg,
                hover,
                text,
                new Vector2(tabW * i, 1),
                new Vector2(tabW * (i + 1), 1),
                new Vector2(leftPad, -(yTop + height)),
                new Vector2(-rightPad, -yTop));
            lbl.fontStyle = FontStyles.Bold;
            lbl.enableAutoSizing = true;
            lbl.fontSizeMin = FontSm;
            lbl.fontSizeMax = FontMdLg;
            ApplyRoundedPanelShape(btn.gameObject, SkillTreeTabRadius);
            UI.AddPanelTexture(btn.transform, active ? TexMid : TexTrace, tree.DisplayName.GetHashCode());
            AddRoundedFrame(btn.transform, active ? 3f : 2f, 1f,
                active
                    ? Palette.Alpha(accent, 0.92f)
                    : Palette.Alpha(Palette.BorderDim, 0.32f),
                SkillTreeTabRadius);
            if (active)
            {
                var underline = UI.MakePanel("TreeTabActiveUnderline", btn.transform,
                    Palette.Alpha(accent, 0.95f),
                    new Vector2(0.08f, 0), new Vector2(0.92f, 0),
                    new Vector2(0f, 0f), new Vector2(0f, 3f));
                underline.GetComponent<Image>().raycastTarget = false;
            }

            Y4NGZSkillTreeClass captured = tree.TreeClass;
            btn.onClick.AddListener(() =>
            {
                MenuAudio.PlayClick();
                _activeSkillTreeClass = captured;
                _selectedSkillTreeNodeId = null;
                currentSelection = null;
                showUpgrades(false, resetScroll: true);
            });
        }
    }

    private static void BuildSkillTreeBackdrop(SkillTreeLayout layout, Y4NGZSkillTreeClass treeClass)
    {
        if (layout == null || _scrollContent == null)
            return;

        Color accent = Palette.Accent;
        // Reaches up past the first tier caption so the captions read as part of
        // the tree surface rather than floating above it.
        float yTop = layout.TreeTop - SkillTreeGlassRise;
        float yBottom = layout.ContentHeight - 4f;
        var panel = UI.MakePanel("SkillTreeGlass", _scrollContent,
            Palette.Alpha(Palette.BgInput, FillWash),
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(4f, -yBottom), new Vector2(-4f, -yTop));
        Image bg = panel.GetComponent<Image>();
        if (bg != null)
            bg.raycastTarget = false;

        UI.AddPanelTexture(panel.transform, TexTrace, treeClass.GetHashCode());
        AddBorder(panel.transform, 2f, 0f, Palette.Alpha(accent, FillGhost));
    }

    private static void BuildSkillTreeHeader(
        Y4NGZSkillTreeDefinition tree,
        List<Y4NGZSkillTreeNodeDefinition> nodes,
        Dictionary<string, Y4NGZUpgradeNode> upgradesById,
        int treeInvestment,
        float yTop,
        float height)
    {
        Color accent = Palette.Accent;
        var header = UI.MakePanel("TreeHeader_" + tree.DisplayName, _scrollContent,
            new Color(Palette.BgHeader.r, Palette.BgHeader.g, Palette.BgHeader.b, 0.82f),
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(4f, -(yTop + height)), new Vector2(-4f, -yTop));
        UI.AddPanelTexture(header.transform, TexMid, tree.DisplayName.GetHashCode());
        AddBorder(header.transform, 3f, 0f, Palette.BorderDim);
        UI.MakePanel("TreeAccent", header.transform, accent,
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(0, 0), new Vector2(5f, 0));

        var label = UI.MakeText("TreeName", header.transform,
            tree.DisplayName.ToUpperInvariant(),
            FontLg, accent, TextAlignmentOptions.MidlineLeft);
        label.fontStyle = FontStyles.Bold;
        var labelRt = label.GetComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0, 0);
        labelRt.anchorMax = new Vector2(0.50f, 1);
        labelRt.offsetMin = new Vector2(18f, 0);
        labelRt.offsetMax = new Vector2(-8f, 0);

        int implemented = nodes.Count(x => upgradesById.ContainsKey(x.UpgradeId));
        int purchasedLevels = nodes.Sum(x => upgradesById.TryGetValue(x.UpgradeId, out Y4NGZUpgradeNode upgrade)
            ? Mathf.Max(0, upgrade.GetCurrentLevel())
            : 0);
        int totalLevels = nodes.Sum(x => upgradesById.TryGetValue(x.UpgradeId, out Y4NGZUpgradeNode upgrade)
            ? Mathf.Max(1, upgrade.MaxUpgrade + 1)
            : 0);
        string metaText = $"{implemented} / {nodes.Count} FILES    {purchasedLevels} / {Mathf.Max(1, totalLevels)} LEVELS    {treeInvestment} TREE LEVELS";
        var meta = UI.MakeText("TreeMeta", header.transform,
            metaText,
            FontSm, Palette.Body, TextAlignmentOptions.MidlineRight);
        meta.enableAutoSizing = true;
        meta.fontSizeMin = FontMicro;
        meta.fontSizeMax = FontSm;
        var metaRt = meta.GetComponent<RectTransform>();
        metaRt.anchorMin = new Vector2(0.50f, 0);
        metaRt.anchorMax = new Vector2(1, 1);
        metaRt.offsetMin = new Vector2(8f, 0);
        metaRt.offsetMax = new Vector2(-16f, 0);
    }

    private static void BuildSkillTreeTierGates(
        Y4NGZSkillTreeDefinition tree,
        int treeInvestment,
        SkillTreeLayout layout)
    {
        if (tree == null || layout == null)
            return;

        float gateTop = layout.TreeTop - SkillTreeGateRise;
        float gateBottom = layout.ContentHeight - 20f;
        for (int tier = 2; tier <= 4; tier++)
        {
            int requirement = tree.GetGateRequirement(tier);
            bool unlocked = treeInvestment >= requirement;
            float x = layout.Left + (tier - 1) * layout.ColumnW - 24f;
            Color accent = Palette.Accent;
            Color color = unlocked
                ? new Color(accent.r, accent.g, accent.b, 0.62f)
                : Palette.Alpha(Palette.Locked, 0.48f);

            MakeAccentLine(_scrollContent, "TierGate_" + tier,
                new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(x, -gateBottom),
                new Vector2(x + 4f, -gateTop),
                color);

            var label = UI.MakeText("TierGateLabel_" + tier, _scrollContent,
                $"T{tier}  {requirement}",
                FontXs, unlocked ? accent : Palette.Locked,
                TextAlignmentOptions.Center);
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            var rt = label.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(66f, 20f);
            rt.anchoredPosition = new Vector2(x + 2f, -(gateTop - 14f));
        }
    }

    private static void BuildSkillTreeConnections(
        List<Y4NGZSkillTreeNodeDefinition> nodes,
        Dictionary<string, Y4NGZUpgradeNode> upgradesById,
        SkillTreeLayout layout)
    {
        if (nodes == null || layout == null)
            return;

        // A link the player has actually walked reads as live wiring; everything
        // else is dim plan. Both endpoints must be bought into for the accent.
        Color accent = Palette.Accent;
        Color liveColor = Palette.Alpha(accent, 0.72f);
        Color plannedColor = Palette.Alpha(Palette.Locked, FillGhost);
        var drawnConnections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < nodes.Count; i++)
        {
            Y4NGZSkillTreeNodeDefinition node = nodes[i];
            // Tier 1 has no tier below it to fall back to, but a root-row node
            // may still declare an explicit link to a node further up the tree.
            bool hasExplicitConnections = node.ConnectionUpgradeIds != null
                && node.ConnectionUpgradeIds.Count > 0;
            if (node.Tier <= 1 && !hasExplicitConnections)
                continue;

            List<Y4NGZSkillTreeNodeDefinition> previousNodes = FindSkillTreeConnectionSources(nodes, node);
            for (int c = 0; c < previousNodes.Count; c++)
            {
                Y4NGZSkillTreeNodeDefinition previous = previousNodes[c];
                if (previous == null)
                    continue;

                string key = previous.UpgradeId + ">" + node.UpgradeId;
                if (!drawnConnections.Add(key))
                    continue;

                Vector2 previousCenter = layout.NodeCenter(previous);
                Vector2 nodeCenter = layout.NodeCenter(node);
                bool targetAbove = nodeCenter.y < previousCenter.y;
                Vector2 from = targetAbove
                    ? layout.NodeTopCenter(previous)
                    : layout.NodeBottomCenter(previous);
                Vector2 to = targetAbove
                    ? layout.NodeBottomCenter(node)
                    : layout.NodeTopCenter(node);
                bool live = IsSkillTreeNodeOwned(previous, upgradesById)
                    && IsSkillTreeNodeOwned(node, upgradesById);
                MakeSkillTreeConnection(_scrollContent, from, to,
                    live ? liveColor : plannedColor, live ? 2f : 1f);
            }
        }
    }

    /// <summary>Owned = at least one level bought. Used to light up connection lines.</summary>
    private static bool IsSkillTreeNodeOwned(
        Y4NGZSkillTreeNodeDefinition node,
        Dictionary<string, Y4NGZUpgradeNode> upgradesById)
    {
        return node != null
            && upgradesById != null
            && upgradesById.TryGetValue(node.UpgradeId, out Y4NGZUpgradeNode upgrade)
            && upgrade.GetCurrentLevel() > 0;
    }

    private static List<Y4NGZSkillTreeNodeDefinition> FindSkillTreeConnectionSources(
        List<Y4NGZSkillTreeNodeDefinition> nodes,
        Y4NGZSkillTreeNodeDefinition node)
    {
        var sources = new List<Y4NGZSkillTreeNodeDefinition>();
        if (nodes == null || node == null)
            return sources;

        if (node.ConnectionUpgradeIds != null && node.ConnectionUpgradeIds.Count > 0)
        {
            for (int i = 0; i < node.ConnectionUpgradeIds.Count; i++)
            {
                string id = node.ConnectionUpgradeIds[i];
                Y4NGZSkillTreeNodeDefinition source = nodes.FirstOrDefault(x =>
                    string.Equals(x.UpgradeId, id, StringComparison.OrdinalIgnoreCase));
                if (source != null && source != node)
                    sources.Add(source);
            }
        }

        if (sources.Count == 0)
        {
            Y4NGZSkillTreeNodeDefinition previous = FindPreviousSkillTreeNode(nodes, node);
            if (previous != null)
                sources.Add(previous);
        }

        return sources;
    }

    private static Y4NGZSkillTreeNodeDefinition FindPreviousSkillTreeNode(
        List<Y4NGZSkillTreeNodeDefinition> nodes,
        Y4NGZSkillTreeNodeDefinition node)
    {
        return nodes
            .Where(x => x.Tier < node.Tier)
            .OrderByDescending(x => x.Tier)
            .ThenBy(x => Mathf.Abs(x.Row - node.Row))
            .ThenBy(x => Mathf.Abs(x.Column - node.Column))
            .FirstOrDefault();
    }

    private static void MakeSkillTreeConnection(
        Transform parent,
        Vector2 from,
        Vector2 to,
        Color color,
        float thickness)
    {
        float midY = (from.y + to.y) * 0.5f;
        Vector2 bendA = new Vector2(from.x, midY);
        Vector2 bendB = new Vector2(to.x, midY);

        MakeSkillTreeSegment(parent, from, bendA, color, thickness);
        MakeSkillTreeSegment(parent, bendA, bendB, color, thickness);
        MakeSkillTreeSegment(parent, bendB, to, color, thickness);
    }

    private static void MakeSkillTreeSegment(
        Transform parent,
        Vector2 a,
        Vector2 b,
        Color color,
        float thickness)
    {
        if (parent == null)
            return;

        float t = Mathf.Max(1f, thickness);
        float xMin = Mathf.Min(a.x, b.x);
        float xMax = Mathf.Max(a.x, b.x);
        float yMin = Mathf.Min(a.y, b.y);
        float yMax = Mathf.Max(a.y, b.y);
        if (Mathf.Abs(xMax - xMin) < 0.01f)
        {
            xMin -= t * 0.5f;
            xMax += t * 0.5f;
        }
        if (Mathf.Abs(yMax - yMin) < 0.01f)
        {
            yMin -= t * 0.5f;
            yMax += t * 0.5f;
        }

        var line = UI.MakePanel("TreeConnectionSegment", parent, color,
            new Vector2(0, 1), new Vector2(0, 1),
            new Vector2(xMin, -yMax), new Vector2(xMax, -yMin));
        line.GetComponent<Image>().raycastTarget = false;
    }

    private static void MakeSkillTreeJunction(Transform parent, Vector2 point, Color color)
    {
        if (parent == null)
            return;

        const float size = 8f;
        var joint = UI.MakePanel("TreeConnectionJunction", parent,
            new Color(color.r, color.g, color.b, Mathf.Min(0.72f, color.a + 0.20f)),
            new Vector2(0, 1), new Vector2(0, 1),
            new Vector2(point.x - size * 0.5f, -(point.y + size * 0.5f)),
            new Vector2(point.x + size * 0.5f, -(point.y - size * 0.5f)));
        joint.GetComponent<Image>().raycastTarget = false;
    }

    private static void BuildSkillTreeNode(
        Y4NGZSkillTreeNodeDefinition node,
        Dictionary<string, Y4NGZUpgradeNode> upgradesById,
        int treeInvestment,
        SkillTreeLayout layout,
        Y4NGZSkillTreeNodeDefinition selectedNode)
    {
        if (node == null || layout == null)
            return;

        upgradesById.TryGetValue(node.UpgradeId, out Y4NGZUpgradeNode upgrade);
        bool implemented = upgrade != null;
        int currentLevel = implemented ? upgrade.GetCurrentLevel() : 0;
        int maxLevel = implemented ? Mathf.Max(1, upgrade.MaxUpgrade + 1) : 0;
        bool selected = selectedNode != null
            && string.Equals(selectedNode.UpgradeId, node.UpgradeId, StringComparison.OrdinalIgnoreCase);

        Color accent = Palette.Accent;
        SkillNodeState state = ClassifySkillTreeNode(node, upgrade);

        // A node draws only its icon, its name and its level pips (#258), so the
        // state has to read off fill, stroke colour, stroke weight, group alpha
        // and pip fill together - five axes, none of which is text. The exact
        // cost, gate requirement and level count live in the right inspector,
        // which is one selection away from any node here.
        bool dimmed = state == SkillNodeState.Gated || state == SkillNodeState.Planned;
        Color normal;
        Color borderNormal;
        float borderWeight = 2f;
        float groupAlpha = 1f;
        Color labelColor;

        switch (state)
        {
            case SkillNodeState.Owned:
                // The only state with an accent-filled body.
                normal = Palette.Alpha(accent, 0.34f);
                borderNormal = Palette.Alpha(Palette.Gold, 0.90f);
                labelColor = Palette.RowButtonText;
                break;
            case SkillNodeState.Affordable:
                // The only state with the heavy 3px stroke - the buyable ones
                // are what the eye should land on first.
                normal = Palette.Alpha(Palette.BgRow, 0.20f);
                borderNormal = Palette.Alpha(accent, 0.95f);
                borderWeight = 3f;
                labelColor = Palette.RowButtonText;
                break;
            case SkillNodeState.TokenLocked:
                // Reachable but unpaid: undimmed body, accent stroke at half
                // weight of the affordable one, muted name.
                normal = Palette.Alpha(Palette.BgRow, 0.20f);
                borderNormal = Palette.Alpha(accent, 0.45f);
                labelColor = Palette.Muted;
                break;
            case SkillNodeState.Gated:
                // Out of reach: a neutral grey fill, stroke, label, and pips
                // distinguish the prerequisite/tier lock from mere cost.
                normal = Palette.Alpha(Palette.Locked, 0.12f);
                borderNormal = Palette.Alpha(Palette.Locked, 0.56f);
                groupAlpha = 0.72f;
                labelColor = Palette.Locked;
                break;
            default:
                // Planned: dimmer still, a hairline stroke, and no pips at all
                // because an unimplemented node has no levels to draw.
                normal = Palette.Alpha(Palette.BgRow, 0.04f);
                borderNormal = Palette.Alpha(Palette.Locked, 0.24f);
                borderWeight = 1f;
                groupAlpha = 0.45f;
                labelColor = Palette.Locked;
                break;
        }

        // One hover wash for every state. It used to be halved on the dimmed
        // ones to keep them quiet, but the CanvasGroup now lifts a hovered node
        // toward full - halving it again would cancel that lift out.
        Color hoverBase = state == SkillNodeState.Gated || state == SkillNodeState.Planned
            ? Palette.Locked
            : state == SkillNodeState.Owned ? Palette.Gold : accent;
        Color hover = Palette.Alpha(hoverBase, 0.18f);
        Color borderHover = Palette.Alpha(borderNormal, Mathf.Min(1f, borderNormal.a + 0.30f));

        Vector2 topLeft = layout.NodeTopLeft(node);
        var nodeGo = new GameObject("SkillNode_" + node.UpgradeId);
        nodeGo.transform.SetParent(_scrollContent, false);
        var bg = nodeGo.AddComponent<Image>();
        bg.color = normal;
        var rt = bg.rectTransform;
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(layout.NodeW, layout.NodeH);
        rt.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
        // The one remaining reach-keyed choice that is not an alpha: out-of-reach
        // nodes take the sparser grain, which survives the CanvasGroup lift as a
        // surface difference rather than cancelling it out.
        UI.AddPanelTexture(nodeGo.transform, dimmed ? TexTrace : TexFaint, node.UpgradeId.GetHashCode());
        Color borderSelected = Palette.Alpha(Color.white, 0.94f);
        var borderGo = new GameObject("NodeBorder");
        borderGo.transform.SetParent(nodeGo.transform, false);
        var borderRt = borderGo.AddComponent<RectTransform>();
        borderRt.anchorMin = Vector2.zero;
        borderRt.anchorMax = Vector2.one;
        borderRt.offsetMin = Vector2.zero;
        borderRt.offsetMax = Vector2.zero;
        AddBorder(borderGo.transform, borderWeight, 0f, borderNormal);
        // Hard guarantee that nothing a node draws - icon, label, progress -
        // can ever paint outside the node box, whatever the source art does.
        nodeGo.AddComponent<RectMask2D>();
        var group = nodeGo.AddComponent<CanvasGroup>();
        group.alpha = groupAlpha;

        var button = nodeGo.AddComponent<Button>();
        var cb = button.colors;
        cb.normalColor = normal;
        cb.highlightedColor = hover;
        cb.pressedColor = Palette.BtnActive * 0.7f;
        cb.selectedColor = normal;
        cb.disabledColor = normal;
        cb.fadeDuration = 0.06f;
        button.colors = cb;
        button.targetGraphic = bg;

        RectTransform iconRect = BuildSkillNodeIcon(nodeGo.transform, node);

        var label = UI.MakeText("NodeLabel", nodeGo.transform,
            node.DisplayName.ToUpperInvariant(),
            FontSm, labelColor,
            TextAlignmentOptions.Center);
        label.fontStyle = FontStyles.Bold;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.enableAutoSizing = true;
        label.fontSizeMin = FontTiny;
        label.fontSizeMax = FontSm;
        var labelRt = label.GetComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0, 0);
        labelRt.anchorMax = new Vector2(1, 0);
        labelRt.offsetMin = new Vector2(4f, NodeNameBottom);
        labelRt.offsetMax = new Vector2(-4f, NodeNameTop);

        Color progressColor = state == SkillNodeState.Owned
            ? Palette.Gold
            : state == SkillNodeState.Gated ? Palette.Locked : accent;
        BuildSkillNodeProgress(nodeGo.transform, currentLevel, maxLevel,
            progressColor);

        var visual = new SkillTreeNodeVisual
        {
            Background = bg,
            Button = button,
            Label = label,
            Border = borderGo.transform,
            IconRect = iconRect,
            Normal = normal,
            Hover = hover,
            Selected = normal,
            SelectedHover = hover,
            BorderNormal = borderNormal,
            BorderSelected = borderSelected,
            BorderHover = borderHover,
            LabelNormal = labelColor,
            LabelSelected = Palette.Body,
            IconNormalSize = new Vector2(NodeIconSize, NodeIconSize),
            IconSelectedSize = new Vector2(NodeIconSelectedSize, NodeIconSelectedSize),
            Group = group,
            GroupNormal = groupAlpha
        };
        visual.SetSelected(selected);
        if (selected)
            _selectedSkillTreeNodeVisual = visual;

        nodeGo.AddComponent<SkillTreeNodeHoverEmphasis>().Visual = visual;

        button.onClick.AddListener(() =>
        {
            MenuAudio.PlayClick();
            _selectedSkillTreeNodeId = node.UpgradeId;
            SetSelectedSkillTreeNode(visual);
            RenderSkillTreeSelection(node, upgrade, treeInvestment);
        });

        // Keyboard cell: W/S walks tier rows, A/D walks nodes inside one tier.
        Vector2Int cell = layout.NodeCell(node);
        RegisterFocusTarget("node:" + node.UpgradeId, rt, button, cell.x, cell.y, _scrollRect);
    }

    // The icon is never tinted for state. The node's CanvasGroup is the single
    // place the out-of-reach fade lives, so selecting or hovering a dimmed node
    // lifts the icon back with everything else instead of leaving it grey.
    private static RectTransform BuildSkillNodeIcon(
        Transform parent,
        Y4NGZSkillTreeNodeDefinition node)
    {
        Texture2D iconTexture = LoadUpgradeIconTexture(node.IconKey);
        if (iconTexture != null)
        {
            // The pane owns the icon slot; the RawImage only fills it. Sizing the
            // pane (not the image) keeps a non-square glyph inside the slot, and
            // FitInParent stops any icon whose source aspect differs from 1:1
            // from stretching or spilling toward the label.
            var paneGo = new GameObject("IconPane");
            paneGo.transform.SetParent(parent, false);
            var paneRt = paneGo.AddComponent<RectTransform>();
            paneRt.anchorMin = new Vector2(0.5f, 1f);
            paneRt.anchorMax = new Vector2(0.5f, 1f);
            paneRt.pivot = new Vector2(0.5f, 0.5f);
            paneRt.sizeDelta = new Vector2(NodeIconSize, NodeIconSize);
            paneRt.anchoredPosition = new Vector2(0f, NodeIconCenterY);

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(paneGo.transform, false);
            var raw = iconGo.AddComponent<RawImage>();
            raw.texture = iconTexture;
            raw.color = Color.white;
            raw.raycastTarget = false;
            var rt = raw.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(NodeIconSize, NodeIconSize);
            rt.anchoredPosition = Vector2.zero;
            var fitter = iconGo.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = iconTexture.width > 0 && iconTexture.height > 0
                ? (float)iconTexture.width / iconTexture.height
                : 1f;
            return paneRt;
        }

        var initials = UI.MakeText("IconInitials", parent,
            GetSkillNodeInitials(node.DisplayName),
            FontLg,
            Palette.Body,
            TextAlignmentOptions.Center);
        initials.fontStyle = FontStyles.Bold;
        initials.enableWordWrapping = false;
        initials.overflowMode = TextOverflowModes.Ellipsis;
        var rtInitials = initials.GetComponent<RectTransform>();
        rtInitials.anchorMin = new Vector2(0.5f, 1f);
        rtInitials.anchorMax = new Vector2(0.5f, 1f);
        rtInitials.pivot = new Vector2(0.5f, 0.5f);
        rtInitials.sizeDelta = new Vector2(NodeIconSize, NodeIconSize);
        rtInitials.anchoredPosition = new Vector2(0f, NodeIconCenterY);
        return rtInitials;
    }

    private static void BuildSkillNodeProgress(Transform parent, int currentLevel, int maxLevel, Color accent)
    {
        if (parent == null || maxLevel <= 0)
            return;

        int safeMax = Mathf.Clamp(maxLevel, 1, 6);
        float pipW = 7f;
        float gap = 3f;
        float totalW = safeMax * pipW + (safeMax - 1) * gap;
        var holder = new GameObject("NodeProgress");
        holder.transform.SetParent(parent, false);
        var rt = holder.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0);
        rt.anchorMax = new Vector2(0.5f, 0);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(totalW, 8f);
        rt.anchoredPosition = new Vector2(0f, 13f);

        for (int i = 0; i < safeMax; i++)
        {
            bool filled = i < currentLevel;
            var pip = UI.MakePanel("NodePip_" + i, holder.transform,
                filled
                    ? Palette.Alpha(accent, FillStrong)
                    : Palette.Alpha(Palette.Dim, 0.36f),
                new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(i * (pipW + gap), 1f),
                new Vector2(i * (pipW + gap) + pipW, -1f));
            pip.GetComponent<Image>().raycastTarget = false;
        }
    }

    private static void SetSelectedSkillTreeNode(SkillTreeNodeVisual visual)
    {
        if (_selectedSkillTreeNodeVisual == visual)
            return;

        if (_selectedSkillTreeNodeVisual != null)
            _selectedSkillTreeNodeVisual.SetSelected(false);

        _selectedSkillTreeNodeVisual = visual;
        if (_selectedSkillTreeNodeVisual != null)
            _selectedSkillTreeNodeVisual.SetSelected(true);
    }

    private static void RenderSkillTreeSelection(
        Y4NGZSkillTreeNodeDefinition node,
        Y4NGZUpgradeNode upgrade,
        int treeInvestment)
    {
        if (node == null)
            return;

        _selectedSkillTreeNodeId = node.UpgradeId;
        if (upgrade != null)
        {
            showUpgradeGui(upgrade, node, IsSkillTreeNodeGateLocked(node, upgrade.GetCurrentLevel()), treeInvestment);
            return;
        }

        currentSelection = null;
        RenderPlannedSkillTreeNode(node, treeInvestment);
    }

    private static void RenderPlannedSkillTreeNode(Y4NGZSkillTreeNodeDefinition node, int treeInvestment)
    {
        if (_detailView == null || node == null)
            return;

        ClearDetailViewChildren();
        var stack = new InspectorStack(_detailView.transform, InspectorTop);
        BuildPlayerPreviewPlaceholder(stack);

        Color accent = Palette.Accent;
        bool gateLocked = IsSkillTreeNodeGateLocked(node, 0);
        var nameHdr = stack.Panel("PlannedNameHdr", Palette.BgHeader, InspectorNameH);
        UI.AddPanelTexture(nameHdr.transform, TexMid, node.UpgradeId.GetHashCode());
        AddBorder(nameHdr.transform, 3f, 0f, Palette.BorderDim);
        UI.MakePanel("NameAccent", nameHdr.transform, Palette.Accent,
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(0, 0), new Vector2(4, 0));
        UI.MakeText("Name", nameHdr.transform,
            node.DisplayName, FontMd, Palette.Primary,
            TextAlignmentOptions.MidlineLeft)
            .GetComponent<RectTransform>().offsetMin = new Vector2(14, 0);

        var statusBand = BuildInspectorBand(stack, "PlannedStatusBand",
            Palette.Alpha(Palette.BgRow, 1f));
        AddInspectorMetric(statusBand.transform, 0, 3, "TREE",
            node.TreeClass.ToString(),
            accent);
        AddInspectorMetric(statusBand.transform, 1, 3, "TIER",
            "T" + node.Tier,
            Palette.Body);
        AddInspectorMetric(statusBand.transform, 2, 3, "STATUS",
            gateLocked ? $"{treeInvestment}/{node.GateRequirement}" : "PLANNED",
            gateLocked ? Palette.Locked : Palette.Dim);

        string body = GetPlannedSkillTreeNodeSummary(node);
        var msg = UI.MakeText("PlannedInspectorMessage", _detailView.transform,
            body, FontMd, Palette.Body, TextAlignmentOptions.TopLeft);
        msg.enableWordWrapping = true;
        msg.overflowMode = TextOverflowModes.Overflow;
        stack.Fill(msg.GetComponent<RectTransform>(), 72f, Space5);

        string label = gateLocked
            ? $"LOCKED  {treeInvestment}/{node.GateRequirement} TREE LEVELS"
            : "- PLANNED -";
        var (button, _) = UI.MakeButton("PlannedPurchaseBtn",
            _detailView.transform,
            label, FontSm, PurchaseButtonBg(false), PurchaseButtonHover(false),
            gateLocked ? Palette.Locked : Palette.Dim,
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(Space2, Space2), new Vector2(-Space2, InspectorFootH));
        AddButtonDepth(button.gameObject, node.UpgradeId.GetHashCode());
        button.interactable = false;
    }

    private static bool IsSkillTreeNodeGateLocked(Y4NGZSkillTreeNodeDefinition node, int currentLevel)
    {
        return Y4NGZUpgradeManager.IsGateLocked(node, currentLevel);
    }

    private struct SkillTreeLockState
    {
        internal bool GateLocked;
        internal bool PrerequisiteLocked;
        internal string Label;
    }

    private static SkillTreeLockState GetSkillTreeLockState(
        Y4NGZSkillTreeNodeDefinition node,
        int currentLevel)
    {
        SkillTreeLockState state = new SkillTreeLockState();
        if (node == null || currentLevel > 0)
            return state;

        int treeInvestment = Y4NGZUpgradeManager.GetTreeInvestment(node.TreeClass);
        if (Y4NGZUpgradeManager.IsGateLocked(node, currentLevel))
        {
            state.GateLocked = true;
            state.Label = $"LOCKED  {treeInvestment}/{node.GateRequirement} TREE LEVELS";
            return state;
        }

        if (Y4NGZUpgradeManager.IsPrerequisiteLocked(node, currentLevel))
        {
            state.PrerequisiteLocked = true;
            string prerequisite = Y4NGZUpgradeManager.GetPrerequisiteDisplayName(node);
            state.Label = $"REQUIRES  {prerequisite.ToUpperInvariant()}";
        }

        return state;
    }

    // -
    //  SKILL TREE NODE STATE (#258)
    // -
    // Five states the player must be able to tell apart at a glance, in every
    // UiTheme preset, with no text on the node to help. They are read off the
    // same purchase/prereq/gate calls the inspector and the purchase button
    // already use - this classifies, it never decides anything.
    private enum SkillNodeState
    {
        /// <summary>Every level bought. Accent-filled body, Gold stroke, all pips filled.</summary>
        Owned,
        /// <summary>Buyable right now. The only 3px stroke, at full accent alpha.</summary>
        Affordable,
        /// <summary>Tier gate or prerequisite is shut. Body dims, stroke leaves the accent hue.</summary>
        Gated,
        /// <summary>Unlocked but unaffordable. Undimmed body, half-alpha accent stroke.</summary>
        TokenLocked,
        /// <summary>Authored but not implemented yet. Dimmest, hairline stroke, no pips.</summary>
        Planned
    }

    // Classification only. Callers that need the gate detail or the exact price
    // ask GetSkillTreeLockState / ComputeEffectivePrice for themselves - the
    // inspector is the only thing that still wants those numbers, and handing
    // them back through out-parameters nobody read was dead surface.
    private static SkillNodeState ClassifySkillTreeNode(
        Y4NGZSkillTreeNodeDefinition node,
        Y4NGZUpgradeNode upgrade)
    {
        if (upgrade == null)
            return SkillNodeState.Planned;

        if (upgrade.GetRemainingLevels() == 0)
            return SkillNodeState.Owned;

        SkillTreeLockState lockState = GetSkillTreeLockState(node, upgrade.GetCurrentLevel());
        if (lockState.GateLocked || lockState.PrerequisiteLocked)
            return SkillNodeState.Gated;

        int price = ComputeEffectivePrice(upgrade);
        if (price == int.MaxValue)
            return SkillNodeState.Owned;

        return SafeCurrency() >= price ? SkillNodeState.Affordable : SkillNodeState.TokenLocked;
    }

    /// <summary>
    /// One small caption per tier row, naming the row and nothing else (#258).
    /// A shut tier reads a shade brighter than an open one so the band still
    /// separates them, but the requirement itself belongs to the right
    /// inspector, which states it whenever a gated node is selected.
    /// </summary>
    private static void BuildSkillTreeTierLabels(
        Y4NGZSkillTreeDefinition tree,
        int treeInvestment,
        SkillTreeLayout layout)
    {
        if (tree == null || layout == null || _scrollContent == null)
            return;

        for (int i = 0; i < layout.TierRows.Count; i++)
        {
            (int tier, float top) = layout.TierRows[i];
            int requirement = tree.GetGateRequirement(tier);
            bool unlocked = requirement <= 0 || treeInvestment >= requirement;

            var label = UI.MakeText("TierRowLabel_" + tier, _scrollContent,
                "TIER " + tier, FontXs,
                unlocked ? Palette.Alpha(Palette.Dim, 0.62f) : Palette.Locked,
                TextAlignmentOptions.MidlineLeft);
            label.fontStyle = FontStyles.Bold;
            label.characterSpacing = 1f;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.enableAutoSizing = true;
            label.fontSizeMin = FontMicro;
            label.fontSizeMax = FontXs;

            var rt = label.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.offsetMin = new Vector2(TierLabelInset, -(top - Space1));
            rt.offsetMax = new Vector2(-TierLabelInset, -(top - Space1 - TierLabelH));
        }
    }

    private static int GetTreeInvestment(
        Y4NGZSkillTreeClass treeClass,
        Dictionary<string, Y4NGZUpgradeNode> upgradesById)
    {
        if (upgradesById == null || upgradesById.Count == 0)
            return 0;

        return Y4NGZUpgradeManager.GetSkillTreeNodes(treeClass)
            .Sum(x => upgradesById.TryGetValue(x.UpgradeId, out Y4NGZUpgradeNode upgrade)
                ? Mathf.Max(0, upgrade.GetCurrentLevel())
                : 0);
    }

    private static void CycleSkillTree(int direction)
    {
        List<Y4NGZSkillTreeDefinition> trees = Y4NGZUpgradeManager.GetSkillTrees().ToList();
        if (trees.Count <= 1)
            return;

        int current = trees.FindIndex(x => x.TreeClass == _activeSkillTreeClass);
        if (current < 0)
            current = 0;

        int next = (current + direction) % trees.Count;
        if (next < 0)
            next += trees.Count;

        _activeSkillTreeClass = trees[next].TreeClass;
        _selectedSkillTreeNodeId = null;
        showUpgrades(false, resetScroll: true);
    }

    private static Texture2D LoadUpgradeIconTexture(string iconKey)
    {
        return UpgradeIconLoader.LoadUpgradeIconTexture(iconKey);
    }

    private static string GetSkillNodeInitials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "?";

        string[] words = displayName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
            return words[0].Substring(0, Mathf.Min(2, words[0].Length)).ToUpperInvariant();

        var sb = new StringBuilder();
        for (int i = 0; i < words.Length && sb.Length < 2; i++)
        {
            string word = words[i];
            if (!string.IsNullOrWhiteSpace(word))
                sb.Append(char.ToUpperInvariant(word[0]));
        }

        return sb.Length == 0 ? "?" : sb.ToString();
    }

    private static string GetPlannedSkillTreeNodeSummary(Y4NGZSkillTreeNodeDefinition node)
    {
        if (node == null)
            return "This upgrade file is planned for a later implementation phase.";

        switch (node.UpgradeId)
        {
            case "night_vision":
                return "Interior low-light visibility assist. Exact scaling will be tuned after implementation.";
            case "panic_slide":
                return "Sprint plus crouch will produce a slide-like movement result without custom animation work in the first pass.";
            case "escape_protocol":
                return "Passive exit awareness. Every seven seconds, nearby exits will receive a dim outline when the player is within range.";
            case "quick_hands":
                return "Faster hold and interact actions. This receives the interaction-speed behavior previously bundled into Sprinter.";
            case "deathbound":
                return "Binds the far-left hotbar slot so its held item stays with the player through death and respawn.";
            case "field_operations":
                return "Grants the Field Operations tablet.";
            case "turret_hacker":
                return "Improves drill, pump, and battery field repairs, and adds on-site hacking of cameras, locked doors, and turrets.";
            case "courier_drone":
                return "Deploys a courier drone companion. Toggle it between your position and the main entrance to retrieve nearby staged scrap.";
            case "buddy_system":
                return "Nearby teammates receive scaling speed and damage-reduction bonuses while close to the upgraded player.";
            case "ping":
                return "Uses the vanilla pointing behavior to mark locations or outline enemies, interactables, items, and traps for the crew.";
            case "rally_call":
                return "An active crew rally that grants nearby players speed and damage reduction, and breaks current Ghost Girl haunting on activation.";
            case "worklight_beacon":
                return "Throws sticky worklight beacons that illuminate the area where they land.";
            case "inspire":
                return "Reuses Ping on a nearby dead body for a once-per-round chance to revive, consuming the use only on a successful revive.";
            default:
                return "This upgrade file is planned for a later implementation phase.";
        }
    }

    // Category header band. It reads as a calm surface break: slightly
    // brighter fill and extra breathing room instead of ruler-line clutter.
    private static void BuildSubCategoryHeader(
        string subCategory, float yTop, float height, int fileCount, int purchasedLevels, int totalLevels)
    {
        if (!SubCategoryColors.TryGetValue(subCategory, out var accent))
            accent = Palette.Accent;

        const float pad = 4f;

        var bar = new GameObject("SubCatHeader_" + subCategory);
        bar.transform.SetParent(_scrollContent, false);
        var barImg = bar.AddComponent<Image>();
        barImg.color = new Color(
            Mathf.Min(1f, Palette.BgHeader.r * 1.10f),
            Mathf.Min(1f, Palette.BgHeader.g * 1.04f),
            Mathf.Min(1f, Palette.BgHeader.b * 1.02f),
            0.82f);
        barImg.raycastTarget = false;
        var barRt = barImg.rectTransform;
        barRt.anchorMin = new Vector2(0, 1);
        barRt.anchorMax = new Vector2(1, 1);
        barRt.pivot     = new Vector2(0.5f, 1f);
        barRt.offsetMin = new Vector2(pad,  -(yTop + height));
        barRt.offsetMax = new Vector2(-pad, -yTop);
        UI.AddPanelTexture(bar.transform, TexMid, subCategory.GetHashCode());
        MakeAccentLine(bar.transform, "CategoryBottomWear",
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(18, 2), new Vector2(-18, 5),
            new Color(0f, 0f, 0f, 0.28f));

        var label = UI.MakeText("Label", bar.transform,
            subCategory.ToUpperInvariant(),
            FontMd, new Color(accent.r, accent.g, accent.b, 0.96f),
            TextAlignmentOptions.MidlineLeft);
        label.fontStyle = FontStyles.Bold;
        var lrt = label.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0, 0);
        lrt.anchorMax = new Vector2(0.56f, 1);
        lrt.offsetMin = new Vector2(22, 0);
        lrt.offsetMax = new Vector2(-8, 0);

        string countLabel = fileCount == 1 ? "1 FILE" : fileCount + " FILES";
        string levelLabel = totalLevels > 0 ? $"{purchasedLevels} / {totalLevels} LEVELS" : countLabel;
        var meta = UI.MakeText("Meta", bar.transform,
            countLabel + "    " + levelLabel,
            FontSm, Palette.Dim, TextAlignmentOptions.MidlineRight);
        var mrt = meta.GetComponent<RectTransform>();
        mrt.anchorMin = new Vector2(0.56f, 0);
        mrt.anchorMax = new Vector2(1, 1);
        mrt.offsetMin = new Vector2(4, 0);
        mrt.offsetMax = new Vector2(-16, 0);

    }

    private static void SetSelectedUpgradeRow(UpgradeRowVisual visual)
    {
        if (_selectedUpgradeRowVisual == visual) return;
        if (_selectedUpgradeRowVisual != null)
            _selectedUpgradeRowVisual.SetSelected(false);
        _selectedUpgradeRowVisual = visual;
        if (_selectedUpgradeRowVisual != null)
            _selectedUpgradeRowVisual.SetSelected(true);
    }

    private static void BuildUpgradeProgressPips(Transform row, int currentLevel)
    {
        if (row == null || currentLevel <= 0) return;

        int pipCount = Mathf.Clamp(currentLevel, 1, 8);
        const float pipW = 8f;
        const float pipH = 10f;
        const float gap  = 4f;
        float totalW = pipCount * pipW + (pipCount - 1) * gap;

        var holder = new GameObject("ProgressPips");
        holder.transform.SetParent(row, false);
        var rt = holder.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 0.5f);
        rt.anchorMax = new Vector2(1, 0.5f);
        rt.pivot = new Vector2(1, 0.5f);
        rt.sizeDelta = new Vector2(totalW, pipH);
        rt.anchoredPosition = new Vector2(-18f, 0f);

        for (int i = 0; i < pipCount; i++)
        {
            var pip = UI.MakePanel("Pip_" + i, holder.transform,
                Palette.Shade(Palette.Accent, 0.78f, 0.88f),
                new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(i * (pipW + gap), 1f),
                new Vector2(i * (pipW + gap) + pipW, -1f));
            pip.GetComponent<Image>().raycastTarget = false;
        }
    }

    // Builds a single upgrade row at the current y-cursor and advances it.
    // Extracted from the showUpgrades loop so the sub-category grouping can
    // call it from both the routed-by-sub and unrouted-bucket passes.
    private static void BuildUpgradeRow(Y4NGZUpgradeNode upgrade, int currency,
                                        ref float yCursor, float pad, float spacing)
    {
        {
            int  currentLevel   = upgrade.GetCurrentLevel();
            int  maxLevel       = currentLevel + upgrade.GetRemainingLevels();
            bool maxed          = upgrade.GetRemainingLevels() == 0;
            bool selected       = currentSelection != null
                && string.Equals(currentSelection.Id, upgrade.Id, StringComparison.OrdinalIgnoreCase);

            // Sub-category accent colour - overrides the legacy
            // per-upgrade UpgradeColorType so row tints match the
            // sub-category header. Falls back to the old palette only
            // for upgrades not present in the sub-category mapping.
            // Colour-group separator removed - sub-category headers now
            // own the visual grouping (see BuildSubCategoryHeader).

            float yTop = yCursor;

            // - Row container -
            var row = new GameObject("Row_" + upgrade.Name);
            row.transform.SetParent(_scrollContent, false);
            var rowImg = row.AddComponent<Image>();
            var rowRt = rowImg.rectTransform;
            rowRt.anchorMin = new Vector2(0, 1);
            rowRt.anchorMax = new Vector2(1, 1);
            rowRt.pivot     = new Vector2(0.5f, 1f);
            rowRt.offsetMin = new Vector2(pad, -(yTop + RowH));
            rowRt.offsetMax = new Vector2(-pad, -yTop);

            // - Row background colour progression (Task 4) -
            // Dim, desaturated - vivid as tiers are purchased, but always
            // translucent so text stays readable.
            float progress = (float)currentLevel / Mathf.Max(1, maxLevel);

            float alpha = Mathf.Lerp(0.84f, 0.94f, progress);
            Color normalCol = new Color(Palette.BgRow.r, Palette.BgRow.g, Palette.BgRow.b, alpha);
            Color hoverCol = new Color(Palette.BgHeader.r, Palette.BgHeader.g, Palette.BgHeader.b, 0.98f);
            Color selectedCol = new Color(Palette.BgHeader.r, Palette.BgHeader.g, Palette.BgHeader.b, 0.96f);
            Color selectedHoverCol = new Color(Palette.BgHeader.r, Palette.BgHeader.g, Palette.BgHeader.b, 1f);

            // Maxed: override background to Gold so completion is visually distinct from type colour.
            if (maxed)
            {
                Color g  = Palette.Gold;
                normalCol = Palette.Shade(g, 0.10f, 0.48f);
                hoverCol  = Palette.Shade(g, 0.15f, 0.62f);
                selectedCol = Palette.Shade(g, 0.15f, 0.68f);
                selectedHoverCol = Palette.Shade(g, 0.20f, 0.78f);
            }
            // Clickable button covering the whole row.
            // Image.color stays white (default) - canvasRenderer is the sole color source.
            // Button.OnEnable fires immediately on AddComponent and calls CrossFadeColor(white)
            // on the auto-detected Image, so we force the correct color after wiring colors up.
            var rowBtn = row.AddComponent<Button>();
            var rowCb  = rowBtn.colors;
            rowCb.normalColor      = normalCol;
            rowCb.highlightedColor = hoverCol;
            rowCb.pressedColor     = Palette.BtnActive * 0.5f;
            rowCb.selectedColor    = normalCol;
            rowCb.disabledColor    = normalCol;
            rowCb.fadeDuration     = 0.06f;
            rowBtn.colors          = rowCb;
            rowBtn.targetGraphic   = rowImg;
            rowImg.color           = normalCol;
            rowImg.CrossFadeColor(normalCol, 0f, true, true);
            UI.AddPanelTexture(row.transform, TexTrace, upgrade.Name.GetHashCode());

            MakeAccentLine(row.transform, "RowDepth",
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(10, 1), new Vector2(-10, 6),
                new Color(0f, 0f, 0f, 0.26f));
            AddBorder(row.transform, 1f, 0f,
                new Color(Palette.Accent.r, Palette.Accent.g, Palette.Accent.b, 0.24f));

            var selectedFill = UI.MakePanel("RowSelectedFill", row.transform,
                Palette.Shade(Palette.Accent, 0.45f, 0.13f),
                new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(6, 4), new Vector2(-8, -4));
            selectedFill.GetComponent<Image>().raycastTarget = false;
            selectedFill.SetActive(false);

            var selectedRail = UI.MakePanel("RowSelectedRail", row.transform,
                new Color(Palette.Accent.r, Palette.Accent.g, Palette.Accent.b, 0.92f),
                new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(0, 4), new Vector2(7, -4));
            selectedRail.GetComponent<Image>().raycastTarget = false;
            selectedRail.SetActive(false);

            var selectedOutline = new GameObject("RowSelectedOutline");
            selectedOutline.transform.SetParent(row.transform, false);
            var outlineRt = selectedOutline.AddComponent<RectTransform>();
            outlineRt.anchorMin = Vector2.zero;
            outlineRt.anchorMax = Vector2.one;
            outlineRt.offsetMin = Vector2.zero;
            outlineRt.offsetMax = Vector2.zero;
            AddBorder(selectedOutline.transform, 2f, 0f,
                new Color(Palette.Accent.r, Palette.Accent.g, Palette.Accent.b, 0.64f));
            selectedOutline.SetActive(false);

            // - Name label -
            // Auto-shrink long upgrade names.
            // would otherwise clip at 13pt bold.
            var nameText = UI.MakeText("Name", row.transform,
                upgrade.Name, FontMd, maxed ? Palette.Gold : Palette.RowButtonText);
            var nrt = nameText.GetComponent<RectTransform>();
            nrt.anchorMin = new Vector2(0, 0);
            nrt.anchorMax = new Vector2(0.78f, 1);
            nrt.offsetMin = new Vector2(18, 0);
            nrt.offsetMax = new Vector2(-2, 0);
            nameText.enableWordWrapping = false;
            nameText.overflowMode       = TextOverflowModes.Ellipsis;
            nameText.fontStyle          = FontStyles.Bold;
            nameText.enableAutoSizing   = true;
            nameText.fontSizeMin        = FontSm;
            nameText.fontSizeMax        = FontMd;

            BuildUpgradeProgressPips(row.transform, currentLevel);

            var visual = new UpgradeRowVisual();
            visual.Background = rowImg;
            visual.Button = rowBtn;
            visual.Label = nameText;
            visual.NameRect = nrt;
            visual.Fill = selectedFill;
            visual.Rail = selectedRail;
            visual.Outline = selectedOutline;
            visual.Normal = normalCol;
            visual.Hover = hoverCol;
            visual.Selected = selectedCol;
            visual.SelectedHover = selectedHoverCol;
            visual.LabelNormal = maxed ? Palette.Gold : Palette.RowButtonText;
            visual.LabelSelected = maxed ? Palette.Gold : Palette.Body;
            visual.SetSelected(selected);
            if (!string.IsNullOrEmpty(upgrade.Id))
                _upgradeRowVisuals[upgrade.Id] = visual;
            if (selected)
                _selectedUpgradeRowVisual = visual;

            rowBtn.onClick.AddListener(() =>
            {
                MenuAudio.PlayClick();
                currentSelection = upgrade;
                SetSelectedUpgradeRow(visual);
                showUpgradeGui(upgrade);
            });

            yCursor += RowH + spacing;
        }
    }

    private static GameObject BuildPlayerPreviewPlaceholder(InspectorStack stack)
    {
        var preview = stack.Panel("PlayerPreview", Palette.BgInput, PreviewH);
        preview.AddComponent<RectMask2D>();
        UI.AddPanelTexture(preview.transform, TexFaint, 9901);
        AddBorder(preview.transform, 3f, 0f, Palette.BorderDim);

        var surface = UI.MakePanel("PreviewSurface", preview.transform,
            Palette.Shell,
            Vector2.zero, Vector2.one,
            new Vector2(3, 3), new Vector2(-3, -3));
        surface.GetComponent<Image>().raycastTarget = true;
        surface.AddComponent<PlayerPreviewDragHandler>();

        Texture previewTexture = TryGetEmployeePreviewTexture();
        if (previewTexture != null)
        {
            var rawGo = new GameObject("PlayerPreviewTexture");
            rawGo.transform.SetParent(surface.transform, false);
            var raw = rawGo.AddComponent<RawImage>();
            raw.texture = previewTexture;
            raw.color = Color.white;
            raw.raycastTarget = false;
            var rawRt = raw.rectTransform;
            rawRt.anchorMin = Vector2.zero;
            rawRt.anchorMax = Vector2.one;
            rawRt.pivot     = new Vector2(0.5f, 0.5f);
            rawRt.offsetMin = new Vector2(32f, 18f);
            rawRt.offsetMax = new Vector2(-32f, -18f);
            var fitter = rawGo.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = previewTexture.width > 0 && previewTexture.height > 0
                ? (float)previewTexture.width / previewTexture.height
                : 1f;
        }
        else
        {
            TMP_Text offline = UI.MakeText("PreviewOffline", surface.transform,
                "EMPLOYEE FEED OFFLINE", FontSm, Palette.Dim,
                TextAlignmentOptions.Center);
            var offlineRt = offline.GetComponent<RectTransform>();
            offlineRt.offsetMin = new Vector2(18, 34);
            offlineRt.offsetMax = new Vector2(-18, -44);
        }


        return preview;
    }

    private sealed class EmployeePreviewTicker : MonoBehaviour
    {
        // LateUpdate so the real player's Animator + IK have already posed
        // the source bones this frame before we mirror them onto the clone.
        private void LateUpdate()
        {
            TickEmployeePreview();
            RenderEmployeePreviewIfDue();
        }
    }

    private sealed class PlayerPreviewDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public void OnBeginDrag(PointerEventData eventData)
        {
            _employeePreviewLastDragTime = Time.unscaledTime;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData == null) return;
            RotateEmployeePreview(eventData.delta.x);
        }
    }

    private static void RotateEmployeePreview(float deltaX)
    {
        _employeePreviewLastDragTime = Time.unscaledTime;
        _employeePreviewYawOffset = Mathf.Repeat(_employeePreviewYawOffset - deltaX * 0.42f, 360f);
    }

    private static Texture TryGetEmployeePreviewTexture()
    {
        try
        {
            if (!MenuController.IsOpen)
            {
                HideEmployeePreview();
                return null;
            }

            EnsureEmployeePreviewScene();
            SetEmployeePreviewPipelineActive(true);
            RefreshEmployeePreviewModel(force: false);
            return _employeePreviewModel != null ? _employeePreviewTexture : null;
        }
        catch (Exception e)
        {
            if (!_employeePreviewWarned)
            {
                _employeePreviewWarned = true;
                Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu] Employee preview failed: {e.Message}");
            }
            return null;
        }
    }

    private static void EnsureEmployeePreviewScene()
    {
        if (_employeePreviewTexture == null)
        {
            _employeePreviewTexture = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32)
            {
                name = "Y4NGZ_EmployeePreviewRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _employeePreviewTexture.Create();
        }

        if (_employeePreviewRoot == null)
        {
            _employeePreviewRoot = new GameObject("Y4NGZ_EmployeePreviewRig");
            _employeePreviewRoot.transform.position = EmployeePreviewOrigin;
            _employeePreviewTicker = _employeePreviewRoot.AddComponent<EmployeePreviewTicker>();
            _employeePreviewTicker.enabled = false;

            // Camera at the rig origin looking straight ahead (+Z); the model
            // holder sits 3m in front. Perspective + CameraType.Preview is
            // what TooManyEmotes uses - an HDRP-safe offscreen camera that
            // skips scene exposure/post so the character isn't washed out.
            var camGo = new GameObject("EmployeePreviewCamera");
            camGo.transform.SetParent(_employeePreviewRoot.transform, false);
            camGo.layer = EmployeePreviewLayer;

            _employeePreviewCamera = camGo.AddComponent<Camera>();
            _employeePreviewCamera.cameraType = CameraType.Preview;
            _employeePreviewCamera.clearFlags = CameraClearFlags.SolidColor;
            _employeePreviewCamera.backgroundColor = EmployeePreviewClearColor();
            _employeePreviewCamera.cullingMask = 1 << EmployeePreviewLayer;
            _employeePreviewCamera.nearClipPlane = 0.05f;
            _employeePreviewCamera.farClipPlane = 10f;
            _employeePreviewCamera.allowHDR = false;
            _employeePreviewCamera.allowMSAA = false;
            _employeePreviewCamera.targetTexture = _employeePreviewTexture;
            _employeePreviewCamera.enabled = false;

            // Spotlight riding on the camera, same numbers as TME's rig.
            // No cullingMask: HDRP ignores light culling masks, isolation
            // comes from the 40m range and the rig's remote position.
            var lightGo = new GameObject("EmployeePreviewSpotlight");
            lightGo.transform.SetParent(camGo.transform, false);
            lightGo.layer = EmployeePreviewLayer;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Spot;
            light.intensity = 50f;
            light.range = 40f;
            light.innerSpotAngle = 100f;
            light.spotAngle = 120f;
        }
        else if (_employeePreviewCamera != null)
        {
            _employeePreviewCamera.targetTexture = _employeePreviewTexture;
            // The rig outlives a theme change, so re-read the clear color here
            // instead of only at construction.
            _employeePreviewCamera.backgroundColor = EmployeePreviewClearColor();
            _employeePreviewCamera.enabled = false;
        }

        if (_employeePreviewTicker == null && _employeePreviewRoot != null)
            _employeePreviewTicker = _employeePreviewRoot.GetComponent<EmployeePreviewTicker>();
    }

    // TEMPORARY - blank-employee-preview investigation, see
    // .planning/debug/model-replacement-employee-preview.md. Normally 0f, so the panel
    // behind the render texture supplies the visible plate. Forced to 1f to make the
    // clear itself visible: if the panel becomes an opaque plate that is still empty the
    // camera IS rendering and the model is not being drawn (cause #3 or #4); if the
    // starfield still shows through, Camera.Render never runs (cause #1). Restore to 0f
    // once the run has been read.
    private const float EmployeePreviewClearAlpha = 1f;

    // Near-black with a trace of the active hue. Alpha stays 0 so the panel
    // behind the render texture supplies the visible plate.
    private static Color EmployeePreviewClearColor()
    {
        Color tint = Color.Lerp(Color.black, UiTheme.Panel, 0.30f);
        return new Color(tint.r, tint.g, tint.b, EmployeePreviewClearAlpha);
    }

    private static void HideEmployeePreview()
    {
        SetEmployeePreviewPipelineActive(false);
    }

    private static void SetEmployeePreviewPipelineActive(bool active)
    {
        if (!active)
        {
            if (_employeePreviewCamera != null)
                _employeePreviewCamera.enabled = false;
            if (_employeePreviewTicker != null)
                _employeePreviewTicker.enabled = false;
            if (_employeePreviewRoot != null)
                _employeePreviewRoot.SetActive(false);
            _nextEmployeePreviewRenderAt = 0f;
            return;
        }

        bool wasActive = _employeePreviewRoot != null
            && _employeePreviewRoot.activeSelf
            && _employeePreviewTicker != null
            && _employeePreviewTicker.enabled;

        if (_employeePreviewRoot != null && !_employeePreviewRoot.activeSelf)
            _employeePreviewRoot.SetActive(true);

        if (_employeePreviewTicker == null && _employeePreviewRoot != null)
            _employeePreviewTicker = _employeePreviewRoot.GetComponent<EmployeePreviewTicker>();
        if (_employeePreviewTicker != null)
            _employeePreviewTicker.enabled = true;

        if (_employeePreviewCamera != null)
        {
            _employeePreviewCamera.targetTexture = _employeePreviewTexture;
            _employeePreviewCamera.enabled = false;
        }

        if (!wasActive)
            _nextEmployeePreviewRenderAt = 0f;
    }

    private static void RefreshEmployeePreviewModel(bool force)
    {
        if (!force && _employeePreviewModel != null)
            return;

        InvalidateEmployeePreviewModel();
        if (_employeePreviewRoot == null)
            return;

        var player = GameNetworkManager.Instance?.localPlayerController
                  ?? StartOfRound.Instance?.localPlayerController;
        if (player == null || player.thisPlayerModel == null)
            return;

        // Holder sits 3m in front of the camera facing it - same placement
        // TooManyEmotes uses for its preview clone.
        _employeePreviewModel = new GameObject("EmployeePreviewModel");
        _employeePreviewModel.transform.SetParent(_employeePreviewRoot.transform, false);
        _employeePreviewModel.transform.localPosition = new Vector3(0f, -1.25f, 3f);
        _employeePreviewModel.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

        // ModelReplacementAPI first. The replacement body is what the rest of
        // the lobby actually sees, so it is what the profile has to show. The
        // rejected latch stops a model we already failed to pair from tearing
        // down and rebuilding the vanilla fallback on every tick.
        GameObject replacement = TryGetLocalReplacementModel(player);
        if (replacement != null && replacement != _previewReplacementRejected)
        {
            if (BuildReplacementPreviewModel(player, replacement))
                return;

            // Structurally unusable rig - drop the half-built clone and let the
            // vanilla recipe below draw something rather than blanking the panel.
            DestroyChildrenExcept(_employeePreviewModel.transform);
            _previewUsesReplacement   = false;
            _previewReplacementSource = null;
            _previewReplacementRejected = replacement;
            _previewBonePairs.Clear();
            if (!_employeePreviewReplacementWarned)
            {
                _employeePreviewReplacementWarned = true;
                Plugin.CustomLogger?.LogWarning(
                    "[Y4NGZMenu] ModelReplacementAPI body could not be mirrored - "
                    + "falling back to the vanilla employee preview.");
            }
        }

        var clone = Object.Instantiate(player.gameObject, _employeePreviewModel.transform, false);
        clone.name = "EmployeePreviewClone";
        clone.transform.localPosition = Vector3.zero;
        clone.transform.localRotation = Quaternion.identity;
        clone.transform.localScale = Vector3.one;

        Transform model   = clone.transform.Find("ScavengerModel");
        Transform metarig = model != null ? model.Find("metarig") : null;
        Transform spine   = metarig != null ? metarig.Find("spine") : null;
        if (spine == null)
        {
            InvalidateEmployeePreviewModel();
            return;
        }

        // Prune everything that isn't the visible body: the player root keeps
        // only ScavengerModel, the model keeps only LOD1 + metarig, the
        // metarig keeps only the spine bone chain. Same recipe as
        // TooManyEmotes' AnimationPreviewer.
        DestroyChildrenExcept(clone.transform, "ScavengerModel");
        DestroyChildrenExcept(model, "LOD1", "metarig");
        DestroyChildrenExcept(metarig, "spine");

        // The source player carries MoreCompany cosmetic instances parented
        // to its bones (hidden for the local player). Clear the cloned ghosts
        // before re-applying the live selection below.
        ClearCloneCosmetics(clone);

        StripCloneToRenderComponents(clone);

        _previewBodyMesh = null;
        foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.name != "LOD1") continue;
            _previewBodyMesh = renderer;
            break;
        }
        if (_previewBodyMesh == null)
        {
            InvalidateEmployeePreviewModel();
            return;
        }

        // The local player's body mesh is ShadowsOnly so you don't see your
        // own model in first person - flip the clone to a normal mesh.
        _previewBodyMesh.gameObject.SetActive(true);
        _previewBodyMesh.enabled = true;
        _previewBodyMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        _previewBodyMesh.updateWhenOffscreen = true;
        _previewBodyMesh.sharedMaterial = player.thisPlayerModel.sharedMaterial;

        ApplyMoreCompanyCosmeticsToPreview(metarig.gameObject);

        SetLayerRecursively(_employeePreviewModel, EmployeePreviewLayer);

        _previewSourceMesh   = player.thisPlayerModel;
        _previewSourcePlayer = player;
        Transform srcMetarig = player.transform.Find("ScavengerModel/metarig");
        Transform srcSpine   = srcMetarig != null ? srcMetarig.Find("spine") : null;
        _previewSpineRoot = spine;
        _previewBonePairs.Clear();
        if (srcSpine != null)
            BuildPreviewBonePairs(srcSpine, spine);
    }

    // Strip behaviour - keep transforms and render components only. The clone
    // is posed by mirroring a live hierarchy each tick, so even the Animator
    // goes. On the ModelReplacementAPI path this is also what keeps the clone
    // out of MRAPI's own per-frame posing and ViewStateManager layer/shadow
    // work, which would otherwise fight the preview rig every frame.
    private static void StripCloneToRenderComponents(GameObject clone)
    {
        if (clone == null) return;
        foreach (var component in clone.GetComponentsInChildren<Component>(true))
        {
            if (component is Transform
                || component is SkinnedMeshRenderer
                || component is MeshRenderer
                || component is MeshFilter)
                continue;
            Object.Destroy(component);
        }
    }

    // - ModelReplacementAPI (optional, reflection only) -
    // Soft dependency on purpose: this mod ships no assembly reference and no
    // vendored DLL for MRAPI, matching how MoreCompany is integrated above. We
    // need exactly one component lookup by type and one public GameObject
    // field, so reflection costs less than a build-time dependency would.
    private static bool      _mrapiResolved;
    private static bool      _mrapiFailed;      // one-shot session kill switch
    private static Type      _mrapiBodyType;    // ModelReplacement.BodyReplacementBase
    private static FieldInfo _mrapiModelField;  // BodyReplacementBase.replacementModel
    private static bool      _employeePreviewReplacementWarned;

    private static bool ResolveModelReplacementReflection()
    {
        if (_mrapiFailed) return false;
        if (_mrapiResolved) return _mrapiBodyType != null && _mrapiModelField != null;
        _mrapiResolved = true;

        try
        {
            // Cheap gate first: one dictionary probe keeps the assembly scan
            // off the path entirely for the (common) no-MRAPI install, and
            // every later call short-circuits on _mrapiResolved.
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("meow.ModelReplacementAPI"))
                return false;

            Assembly asm = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "ModelReplacementAPI") { asm = a; break; }
            }
            if (asm == null) return false;

            // BodyReplacementBase is abstract; every model pack subclasses it.
            // Unity's GetComponent(Type) matches derived components, so this
            // one type covers all of them.
            _mrapiBodyType = asm.GetType("ModelReplacement.BodyReplacementBase", false);
            _mrapiModelField = _mrapiBodyType?.GetField(
                "replacementModel", BindingFlags.Public | BindingFlags.Instance);
            return _mrapiBodyType != null && _mrapiModelField != null;
        }
        catch (Exception e)
        {
            // An optional dependency changing shape must never throw into the
            // 30Hz render loop - log once and stay on the vanilla path.
            _mrapiFailed = true;
            Plugin.CustomLogger?.LogWarning(
                $"[Y4NGZMenu] ModelReplacementAPI preview integration disabled: {e.Message}");
            return false;
        }
    }

    // Returns the live replacement body for this player, or null when MRAPI is
    // absent / no replacement suit is worn. MRAPI adds BodyReplacementBase to
    // the player's OWN GameObject and destroys it when the suit comes off, so
    // this doubles as the presence check the ticker polls.
    private static GameObject TryGetLocalReplacementModel(PlayerControllerB player)
    {
        if (player == null) return null;
        if (!ResolveModelReplacementReflection()) return null;

        try
        {
            var body = player.GetComponent(_mrapiBodyType);
            if (body == null) return null;
            // Unity-null the result too: the field can still hold a destroyed
            // model for the frame between suit swaps.
            var model = _mrapiModelField.GetValue(body) as GameObject;
            return model != null ? model : null;
        }
        catch (Exception e)
        {
            _mrapiFailed = true;
            Plugin.CustomLogger?.LogWarning(
                $"[Y4NGZMenu] ModelReplacementAPI preview integration disabled: {e.Message}");
            return null;
        }
    }

    // Clone MRAPI's live replacement body into the existing preview holder.
    // Returns false when the rig yields nothing renderable or the clone and
    // the live tree cannot be paired, so the caller can fall back to vanilla.
    private static bool BuildReplacementPreviewModel(PlayerControllerB player, GameObject replacement)
    {
        var clone = Object.Instantiate(replacement, _employeePreviewModel.transform, false);
        clone.name = "EmployeePreviewReplacementClone";
        clone.SetActive(true);

        // The live model is a ROOT object MRAPI drives in world space, so
        // Instantiate copies a world-space transform into our local slot. Pin
        // it to the holder origin - the holder already carries the placement
        // and the facing-the-camera 180 degree yaw - but keep the root scale,
        // which is MRAPI's height auto-fit and the only reason framing still
        // matches the vanilla path.
        clone.transform.localPosition = Vector3.zero;
        clone.transform.localRotation = Quaternion.identity;
        clone.transform.localScale    = replacement.transform.localScale;

        StripCloneToRenderComponents(clone);

        bool anyRenderer = false;
        foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
        {
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer))
                continue;
            // MRAPI's ViewStateManager parks the live model on ShadowsOnly (and
            // its own layer) while the owner is in first person; the clone
            // inherits that state and would render nothing. Materials are left
            // alone - the replacement carries its own, and the vanilla suit
            // material is exactly the UV-mismatched texture we are fixing.
            renderer.enabled = true;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            if (renderer is SkinnedMeshRenderer skinned)
                skinned.updateWhenOffscreen = true; // no Animator => bounds never refresh
            anyRenderer = true;
        }
        if (!anyRenderer)
            return false;

        SetLayerRecursively(_employeePreviewModel, EmployeePreviewLayer);

        _previewBodyMesh     = null;  // suppresses the vanilla suit-material sync
        _previewSpineRoot    = null;  // TME emote mirroring needs vanilla bone names
        _previewSourceMesh   = player.thisPlayerModel;  // still the liveness sentinel
        _previewSourcePlayer = player;
        _previewBonePairs.Clear();
        if (!PairReplacementTransforms(replacement.transform, clone.transform, _previewBonePairs))
        {
            _previewBonePairs.Clear();
            return false;
        }

        // MoreCompany's CosmeticApplication resolves attachment points on a
        // vanilla metarig, which a replacement rig does not have, so cosmetics
        // are deliberately skipped here rather than attached to nothing.
        _previewUsesReplacement   = true;
        _previewReplacementSource = replacement;
        return true;
    }

    // Pair the clone against the live replacement tree. The clone came out of
    // Instantiate on that exact object, so the two trees are index-for-index
    // identical; walking by sibling index is both the cheapest match and the
    // only correct one, because model packs freely reuse bone names across
    // limbs and a name lookup would collapse them onto one another. Names are
    // still compared as a tripwire - if anything re-parents or adds to the live
    // model afterwards the structures diverge and we bail instead of posing the
    // wrong bone. The roots are excluded on purpose: the clone root is pinned
    // to the holder while the live root carries a world-space pose.
    private static bool PairReplacementTransforms(
        Transform src, Transform dst, List<(Transform src, Transform dst)> pairs)
    {
        if (src == null || dst == null || pairs == null) return false;
        if (src.childCount != dst.childCount) return false;

        for (int i = 0; i < dst.childCount; i++)
        {
            Transform s = src.GetChild(i);
            Transform d = dst.GetChild(i);
            if (s.name != d.name) return false;
            pairs.Add((s, d));
            if (!PairReplacementTransforms(s, d, pairs)) return false;
        }
        return true;
    }

    private static void DestroyChildrenExcept(Transform parent, params string[] keep)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i);
            bool keepIt = false;
            for (int k = 0; k < keep.Length; k++)
            {
                if (child.name == keep[k]) { keepIt = true; break; }
            }
            if (!keepIt)
            {
                child.gameObject.SetActive(false);
                Object.Destroy(child.gameObject);
            }
        }
    }

    // Walk the destination spine chain and pair each bone with the
    // same-named bone on the real player. Ghost children that are being
    // destroyed this frame produce dead pairs; the tick null-checks them.
    private static void BuildPreviewBonePairs(Transform src, Transform dst)
    {
        BuildPreviewBonePairs(src, dst, _previewBonePairs);
    }

    private static void BuildPreviewBonePairs(
        Transform src, Transform dst, List<(Transform src, Transform dst)> pairs)
    {
        if (src == null || dst == null || pairs == null) return;
        pairs.Add((src, dst));
        for (int i = 0; i < dst.childCount; i++)
        {
            var d = dst.GetChild(i);
            var s = src.Find(d.name);
            if (s != null)
                BuildPreviewBonePairs(s, d, pairs);
        }
    }

    private static void TickEmployeePreview()
    {
        if (!MenuController.IsOpen)
            return;
        if (_employeePreviewRoot != null && !_employeePreviewRoot.activeInHierarchy)
            return;

        if (_employeePreviewModel == null)
        {
            // Self-heal: menu can open before the local player is available,
            // and suit/cosmetic changes invalidate the model to force a
            // rebuild here.
            if (MenuController.IsOpen)
                RefreshEmployeePreviewModel(force: false);
            if (_employeePreviewModel == null)
                return;
        }

        // Source player went away (respawn/late join) - rebuild next tick.
        if (_previewSourceMesh == null)
        {
            InvalidateEmployeePreviewModel();
            return;
        }

        // A ModelReplacementAPI body can appear or vanish outside the suit path
        // this menu drives: mod-side equips never touch it, and even our own
        // suit switch invalidates synchronously while MRAPI spawns its model a
        // frame or two later, so the rebuild that switch triggers can land on
        // the vanilla body. Reference-compare what is live against what we
        // cloned. Cost when MRAPI is absent is a cached bool; when present it
        // is one GetComponent plus one cached FieldInfo read, and only while
        // the menu is open.
        GameObject liveReplacement = TryGetLocalReplacementModel(_previewSourcePlayer);
        if (_previewUsesReplacement
                ? liveReplacement != _previewReplacementSource
                : liveReplacement != null && liveReplacement != _previewReplacementRejected)
        {
            InvalidateEmployeePreviewModel();
            return;
        }

        if (_previewUsesReplacement)
        {
            // MRAPI re-fits the model root to player height, and the root is
            // deliberately outside the mirrored pairs, so carry its scale over
            // on its own. No suit-material sync here: the replacement body owns
            // its materials and the vanilla suit texture is UV garbage on it.
            Transform replacementClone = _employeePreviewModel.transform.childCount > 0
                ? _employeePreviewModel.transform.GetChild(0)
                : null;
            // liveReplacement can be C#-null here even though the compare above passed:
            // TryGetLocalReplacementModel returns a real null once MRAPI destroys the model,
            // while _previewReplacementSource still holds the destroyed-object wrapper, and
            // Unity's overloaded != treats those two as equal. Dereferencing it threw out of
            // LateUpdate before RenderEmployeePreviewIfDue could run, which blanks the panel
            // for exactly as long as the condition holds - so this is a candidate for the
            // blank preview, not only a latent hazard. Log it once if it ever happens.
            if (replacementClone != null && liveReplacement != null)
                replacementClone.localScale = liveReplacement.transform.localScale;
            else if (liveReplacement == null && !_employeePreviewStaleReplacementWarned)
            {
                _employeePreviewStaleReplacementWarned = true;
                Plugin.CustomLogger?.LogWarning(
                    "[Y4NGZMenu] preview diag: replacement mode with a destroyed live model - " +
                    "the liveness compare did not fire. This is the NRE path.");
            }

            CopyPreviewLocalTransforms(_previewBonePairs);
        }
        else
        {
            // Live suit sync - reference compare per frame, assignment only on
            // actual suit switches.
            if (_previewBodyMesh != null
                && _previewBodyMesh.sharedMaterial != _previewSourceMesh.sharedMaterial)
                _previewBodyMesh.sharedMaterial = _previewSourceMesh.sharedMaterial;

            // A selected emote still uses the same persistent Y4NGZ preview
            // body. We mirror TME's hidden animated skeleton into our clone;
            // otherwise we mirror the live player's current idle/crouch pose.
            if (!TryMirrorSelectedEmotePose())
                CopyPreviewPose(_previewBonePairs);
        }

        float idleSway = Time.unscaledTime - _employeePreviewLastDragTime < 1.5f
            ? 0f
            : Mathf.Sin(Time.unscaledTime * 0.55f) * 6f;
        float yaw = 180f + _employeePreviewYawOffset + idleSway;
        _employeePreviewModel.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
    }

    private static void RenderEmployeePreviewIfDue()
    {
        if (!MenuController.IsOpen)
            return;
        if (_employeePreviewRoot == null || !_employeePreviewRoot.activeInHierarchy)
            return;
        if (_employeePreviewCamera == null || _employeePreviewTexture == null || _employeePreviewModel == null)
            return;

        float time = Time.unscaledTime;
        if (time < _nextEmployeePreviewRenderAt)
            return;

        _nextEmployeePreviewRenderAt = time + EmployeePreviewRenderInterval;
        RenderTexture previousActive = RenderTexture.active;
        try
        {
            _employeePreviewCamera.targetTexture = _employeePreviewTexture;
            _employeePreviewCamera.enabled = false;
            LogEmployeePreviewDiagnostic();
            _employeePreviewCamera.Render();
        }
        catch (Exception e)
        {
            if (!_employeePreviewWarned)
            {
                _employeePreviewWarned = true;
                Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu] Employee preview render failed: {e.Message}");
            }
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (_employeePreviewCamera != null)
                _employeePreviewCamera.enabled = false;
        }
    }

    /// <summary>
    /// TEMPORARY - blank-employee-preview investigation, see
    /// .planning/debug/model-replacement-employee-preview.md. Sits immediately before
    /// Camera.Render, after every early-out in RenderEmployeePreviewIfDue, so the absence
    /// of the line is itself the signal. Read it as: no line at all -> the camera never
    /// renders (cause #1, the liveness compare invalidating every tick); zero active
    /// renderers -> the clone's GameObjects are inactive (#3); renderers active with
    /// bounds centred near (0, -1200 +/- 1.7, +3) and about 2.9m tall -> geometry and
    /// framing are fine, so the material is writing alpha 0 (#2); bounds wildly off ->
    /// the mirrored pose collapsed the rig (#4). Throttled to 1Hz; reads sharedMaterial,
    /// never material, so it cannot leak material instances.
    /// </summary>
    private static void LogEmployeePreviewDiagnostic()
    {
        float now = Time.unscaledTime;
        if (now < _nextEmployeePreviewDiagnosticAt)
            return;
        _nextEmployeePreviewDiagnosticAt = now + 1f;

        try
        {
            Transform holder = _employeePreviewModel.transform;
            Transform clone  = holder.childCount > 0 ? holder.GetChild(0) : null;

            int    activeRenderers = 0;
            bool   haveBounds      = false;
            Bounds bounds          = default;
            string shader          = "-";
            string renderType      = "-";

            if (clone != null)
            {
                foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                        continue;

                    activeRenderers++;
                    if (haveBounds)
                        bounds.Encapsulate(renderer.bounds);
                    else
                    {
                        bounds = renderer.bounds;
                        haveBounds = true;
                    }

                    if (shader == "-" && renderer.sharedMaterial != null)
                    {
                        shader = renderer.sharedMaterial.shader != null
                            ? renderer.sharedMaterial.shader.name
                            : "(null shader)";
                        string tag = renderer.sharedMaterial.GetTag("RenderType", false);
                        renderType = string.IsNullOrEmpty(tag) ? "(none)" : tag;
                    }
                }
            }

            string boundsText = haveBounds
                ? $"c{bounds.center.ToString("F2")} s{bounds.size.ToString("F2")}"
                : "(none)";

            Plugin.CustomLogger?.LogInfo(
                "[Y4NGZMenu] preview diag: " +
                $"replacement={_previewUsesReplacement} children={holder.childCount} " +
                $"cloneActive={(clone != null && clone.gameObject.activeInHierarchy)} " +
                $"cloneScaleY={(clone != null ? clone.lossyScale.y : 0f):F3} " +
                $"activeRenderers={activeRenderers} shader={shader} renderType={renderType} " +
                $"bounds={boundsText}");
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu] preview diag failed: {e.Message}");
        }
    }

    private static bool TryMirrorSelectedEmotePose()
    {
        if (_previewingEmote == null || _previewSpineRoot == null)
            return false;

        Animator animator = TryGetTmePreviewAnimator();
        if (animator == null)
            return false;

        Transform sourceSpine = FindPreviewSpine(animator.transform);
        if (sourceSpine == null)
            return false;

        if (_previewEmoteAnimatorSource != animator
            || _previewEmoteSourceSpine != sourceSpine
            || _previewEmoteBonePairs.Count == 0)
        {
            _previewEmoteAnimatorSource = animator;
            _previewEmoteSourceSpine = sourceSpine;
            _previewEmoteBonePairs.Clear();
            BuildPreviewBonePairs(sourceSpine, _previewSpineRoot, _previewEmoteBonePairs);
        }

        return CopyPreviewPose(_previewEmoteBonePairs);
    }

    private static bool CopyPreviewPose(List<(Transform src, Transform dst)> pairs)
    {
        if (pairs == null || pairs.Count == 0)
            return false;

        bool copiedAny = false;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (src, dst) = pairs[i];
            if (src == null || dst == null) continue;
            dst.localRotation = src.localRotation;
            if (i == 0) dst.localPosition = src.localPosition;
            copiedAny = true;
        }

        return copiedAny;
    }

    // Full local TRS copy, used by the ModelReplacementAPI path. MRAPI has
    // already posed the live model this frame (it writes world-space bone
    // rotations right-multiplied by per-bone offsets) and applied its auto-fit
    // scale, and Unity stores the result as parent-relative locals. Replaying
    // those locals onto a structurally identical clone therefore reproduces the
    // pose exactly, with none of MRAPI's math repeated here. Position and scale
    // come along because replacement rigs are not uniformly scaled skeletons -
    // several packs animate bone scale, and some drive translation directly.
    private static bool CopyPreviewLocalTransforms(List<(Transform src, Transform dst)> pairs)
    {
        if (pairs == null || pairs.Count == 0)
            return false;

        bool copiedAny = false;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (src, dst) = pairs[i];
            if (src == null || dst == null) continue;
            dst.localPosition = src.localPosition;
            dst.localRotation = src.localRotation;
            dst.localScale    = src.localScale;
            copiedAny = true;
        }

        return copiedAny;
    }

    private static Transform FindPreviewSpine(Transform root)
    {
        if (root == null) return null;

        Transform spine = root.Find("ScavengerModel/metarig/spine")
                       ?? root.Find("metarig/spine")
                       ?? root.Find("spine");
        if (spine != null) return spine;

        return FindDeepChild(root, "spine");
    }

    private static Transform FindDeepChild(Transform root, string name)
    {
        if (root == null) return null;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child.name == name)
                return child;
            Transform nested = FindDeepChild(child, name);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private static void InvalidateEmployeePreviewModel()
    {
        if (_employeePreviewModel != null)
        {
            _employeePreviewModel.SetActive(false);
            Object.Destroy(_employeePreviewModel);
        }
        _employeePreviewModel = null;
        _previewBodyMesh = null;
        _previewSourceMesh = null;
        _previewSourcePlayer = null;
        _previewSpineRoot = null;
        // _previewReplacementRejected deliberately survives: it is what stops a
        // rig we already failed to pair from being retried every tick.
        _previewUsesReplacement = false;
        _previewReplacementSource = null;
        _previewBonePairs.Clear();
        ClearPreviewEmotePoseSource();
    }

    private static void StopEmployeePreview()
    {
        InvalidateEmployeePreviewModel();

        if (_employeePreviewCamera != null)
            _employeePreviewCamera.targetTexture = null;
        if (_employeePreviewRoot != null)
        {
            _employeePreviewRoot.SetActive(false);
            Object.Destroy(_employeePreviewRoot);
        }
        if (_employeePreviewTexture != null)
        {
            _employeePreviewTexture.Release();
            Object.Destroy(_employeePreviewTexture);
        }

        _employeePreviewRoot = null;
        _employeePreviewCamera = null;
        _employeePreviewTicker = null;
        _employeePreviewTexture = null;
        _employeePreviewWarned = false;
        _nextEmployeePreviewRenderAt = 0f;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        if (root == null) return;
        root.layer = layer;
        for (int i = 0; i < root.transform.childCount; i++)
            SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
    }

    private static void ClearDetailViewChildren()
    {
        if (_detailView == null) return;

        foreach (Transform child in _detailView.transform)
        {
            child.gameObject.SetActive(false);
            Object.Destroy(child.gameObject);
        }
    }

    private static void RenderInspectorPlaceholder(string title, string message)
    {
        if (_detailView == null) return;

        ClearDetailViewChildren();

        var stack = new InspectorStack(_detailView.transform, InspectorTop);
        BuildPlayerPreviewPlaceholder(stack);

        var msg = UI.MakeText("InspectorMessage", _detailView.transform,
            message, FontMd, Palette.Body, TextAlignmentOptions.TopLeft)
            .GetComponent<RectTransform>();
        stack.Fill(msg, 52f, Space5);
    }

    private static void RenderSkillTreeClassOverview(Y4NGZSkillTreeDefinition tree)
    {
        if (_detailView == null) return;

        ClearDetailViewChildren();

        var stack = new InspectorStack(_detailView.transform, InspectorTop);
        BuildPlayerPreviewPlaceholder(stack);

        string displayName = tree != null && !string.IsNullOrWhiteSpace(tree.DisplayName)
            ? tree.DisplayName
            : "Upgrades";

        var nameHdr = stack.Panel("ClassNameHdr", Palette.BgHeader, InspectorNameH);
        UI.AddPanelTexture(nameHdr.transform, TexMid, displayName.GetHashCode());
        AddBorder(nameHdr.transform, 3f, 0f, Palette.BorderDim);
        UI.MakePanel("ClassNameAccent", nameHdr.transform, Palette.Accent,
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(0, 0), new Vector2(4, 0));
        TMP_Text nameText = UI.MakeText("ClassName", nameHdr.transform,
            displayName, FontMd, Palette.Primary, TextAlignmentOptions.MidlineLeft);
        nameText.fontStyle = FontStyles.Bold;
        nameText.GetComponent<RectTransform>().offsetMin = new Vector2(14, 0);
    }

    private static void RenderEmoteInspector()
    {
        if (_detailView == null) return;

        ClearDetailViewChildren();

        var stack = new InspectorStack(_detailView.transform, InspectorTop);
        BuildPlayerPreviewPlaceholder(stack);

        object selObj = _selectedEmoteForPurchase;
        bool hasSelection = selObj != null;
        bool owned = false;
        int price = 0;
        string selectedName = hasSelection ? "SELECTED EMOTE" : "SELECT EMOTE";
        string tierName = "";
        Color titleColor = hasSelection ? Palette.Primary : Palette.Dim;

        if (hasSelection)
        {
            try { owned = TmeIsEmoteUnlocked(selObj); } catch { owned = false; }

            var allForPrice = GetAllPurchasableEmotes();
            if (allForPrice != null)
            {
                foreach (var e in allForPrice)
                {
                    if (!ReferenceEquals(e.obj, selObj)) continue;
                    selectedName = Humanize(e.displayName);
                    tierName = e.rarityName;
                    price = GetEmotePriceForRarity(e.rarity);
                    titleColor = e.rarityColor;
                    break;
                }
            }
        }

        var nameHdr = stack.Panel("EmoteNameHdr", Palette.BgHeader, InspectorNameH);
        UI.AddPanelTexture(nameHdr.transform, TexMid, selectedName.GetHashCode());
        AddBorder(nameHdr.transform, 3f, 0f, Palette.BorderDim);
        UI.MakePanel("NameAccent", nameHdr.transform, titleColor,
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(0, 0), new Vector2(5, 0));
        UI.MakeText("Name", nameHdr.transform,
            selectedName, FontMd, titleColor,
            TextAlignmentOptions.MidlineLeft)
            .GetComponent<RectTransform>().offsetMin = new Vector2(16, 0);

        int curBxp = SafeCurrency();
        bool canAfford = hasSelection && !owned && price > 0 && curBxp >= price;
        var statusBand = BuildInspectorBand(stack, "EmoteStatusBand",
            Palette.Alpha(Palette.BgRow, 1f));
        AddInspectorMetric(statusBand.transform, 0, 3, "TIER",
            hasSelection && !string.IsNullOrEmpty(tierName) ? tierName : "NONE",
            titleColor);
        AddInspectorMetric(statusBand.transform, 1, 3, "STATUS",
            !hasSelection ? "WAITING" : owned ? "OWNED" : "LOCKED",
            owned ? Palette.Gold : hasSelection ? Palette.Body : Palette.Dim);
        AddInspectorMetric(statusBand.transform, 2, 3, "COST",
            hasSelection && !owned ? FormatMarks(price) : owned ? "COMPLETE" : "--",
            canAfford || owned ? Palette.Gold : Palette.Warning);

        string bodyText;
        if (!hasSelection)
            bodyText = "Select an emote from the list to preview it here.";
        else if (owned)
            bodyText = "Owned. Selecting or equipping emotes will use this employee render.";
        else if (canAfford)
            bodyText = $"{tierName.ToUpperInvariant()} FILE READY\nCost: {FormatMarks(price)}";
        else
            bodyText = $"{tierName.ToUpperInvariant()} FILE LOCKED\nCost: {FormatMarks(price)}\nAvailable: {FormatMarks(curBxp)}";

        var msg = UI.MakeText("EmoteInspectorMessage", _detailView.transform,
            bodyText, FontMd, Palette.Body, TextAlignmentOptions.TopLeft);
        msg.enableWordWrapping = true;
        stack.Fill(msg.GetComponent<RectTransform>(), 96f, Space5);

        string buttonLabel;
        Color buttonBg, buttonHl, buttonText;
        if (!hasSelection)
        {
            buttonLabel = "SELECT EMOTE";
            buttonBg = PurchaseButtonBg(false);
            buttonHl = PurchaseButtonHover(false);
            buttonText = Palette.Dim;
        }
        else if (owned)
        {
            buttonLabel = "- OWNED -";
            buttonBg = PurchaseButtonBg(false);
            buttonHl = PurchaseButtonHover(false);
            buttonText = Palette.Gold;
        }
        else
        {
            buttonLabel = $"PURCHASE  {FormatMarks(price)}";
            buttonBg = PurchaseButtonBg(canAfford);
            buttonHl = PurchaseButtonHover(canAfford);
            buttonText = canAfford ? Palette.Accent : Palette.Warning;
        }

        var (buyBtn, _buyLbl) = UI.MakeButton(
            "EmotePurchaseBtn", _detailView.transform,
            buttonLabel, FontSm, buttonBg, buttonHl, buttonText,
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(Space2, Space2), new Vector2(-Space2, InspectorFootH));
        AddButtonDepth(buyBtn.gameObject, selectedName.GetHashCode());
        UI.AddPanelTexture(buyBtn.transform, TexMid, selectedName.GetHashCode());
        buyBtn.interactable = canAfford;
        buyBtn.onClick.AddListener(() =>
        {
            if (!canAfford) { MenuAudio.PlayDeny(); return; }
            PurchaseSelectedEmote();
        });
    }

    private static void RenderCosmeticInspector()
    {
        if (_detailView == null) return;

        ClearDetailViewChildren();

        var stack = new InspectorStack(_detailView.transform, InspectorTop);
        BuildPlayerPreviewPlaceholder(stack);

        string selectedId = _selectedCosmeticForPurchase;
        bool hasSelection = !string.IsNullOrEmpty(selectedId);
        var data = PlayerLevelStore.Get();
        int price = Mathf.Max(0, Plugin.CosmeticPrice.Value);
        int curTokens = SafeCurrency();
        bool owned = hasSelection && data.cosmetics.Contains(selectedId);
        bool equipped = owned && IsMoreCompanyCosmeticEquipped(selectedId);
        bool canAfford = hasSelection && !owned && curTokens >= price;

        string selectedName = hasSelection
            ? (!string.IsNullOrEmpty(_selectedCosmeticLabel) ? _selectedCosmeticLabel : Humanize(selectedId))
            : "SELECT COSMETIC";
        string typeName = hasSelection && !string.IsNullOrEmpty(_selectedCosmeticTypeLabel)
            ? _selectedCosmeticTypeLabel
            : "COSMETIC";
        Color titleColor = !hasSelection
            ? Palette.Dim
            : owned
                ? (equipped ? Palette.Gold : Palette.Primary)
                : canAfford ? Palette.Sale : Palette.Warning;

        var nameHdr = stack.Panel("CosmeticNameHdr", Palette.BgHeader, InspectorNameH);
        UI.AddPanelTexture(nameHdr.transform, TexMid, selectedName.GetHashCode());
        AddBorder(nameHdr.transform, 3f, 0f, Palette.BorderDim);
        UI.MakePanel("NameAccent", nameHdr.transform, titleColor,
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(0, 0), new Vector2(5, 0));
        UI.MakeText("Name", nameHdr.transform,
            selectedName, FontMd, titleColor,
            TextAlignmentOptions.MidlineLeft)
            .GetComponent<RectTransform>().offsetMin = new Vector2(16, 0);

        var statusBand = BuildInspectorBand(stack, "CosmeticStatusBand",
            Palette.Alpha(Palette.BgRow, 1f));
        AddInspectorMetric(statusBand.transform, 0, 3, "TYPE",
            hasSelection ? typeName : "NONE",
            hasSelection ? Palette.Body : Palette.Dim);
        AddInspectorMetric(statusBand.transform, 1, 3, "STATUS",
            !hasSelection ? "WAITING" : equipped ? "EQUIPPED" : owned ? "OWNED" : "LOCKED",
            equipped || owned ? Palette.Gold : hasSelection ? Palette.Body : Palette.Dim);
        AddInspectorMetric(statusBand.transform, 2, 3, "COST",
            hasSelection && !owned ? FormatMarks(price) : owned ? "COMPLETE" : "--",
            canAfford || owned ? Palette.Gold : Palette.Warning);

        string bodyText;
        if (!hasSelection)
            bodyText = "Select a cosmetic file from the grid.";
        else if (equipped)
            bodyText = "Equipped on the employee render.";
        else if (owned)
            bodyText = "Unlocked in this save.";
        else if (canAfford)
            bodyText = $"COSMETIC FILE READY\nCost: {FormatMarks(price)}";
        else
            bodyText = $"COSMETIC FILE LOCKED\nCost: {FormatMarks(price)}\nAvailable: {FormatMarks(curTokens)}";

        var msg = UI.MakeText("CosmeticInspectorMessage", _detailView.transform,
            bodyText, FontMd, Palette.Body, TextAlignmentOptions.TopLeft);
        msg.enableWordWrapping = true;
        stack.Fill(msg.GetComponent<RectTransform>(), 96f, Space5);

        string buttonLabel;
        Color buttonBg, buttonHl, buttonText;
        if (!hasSelection)
        {
            buttonLabel = "SELECT COSMETIC";
            buttonBg = PurchaseButtonBg(false);
            buttonHl = PurchaseButtonHover(false);
            buttonText = Palette.Dim;
        }
        else if (owned)
        {
            buttonLabel = equipped ? "- EQUIPPED -" : "- OWNED -";
            buttonBg = PurchaseButtonBg(false);
            buttonHl = PurchaseButtonHover(false);
            buttonText = Palette.Gold;
        }
        else
        {
            buttonLabel = $"PURCHASE  {FormatMarks(price)}";
            buttonBg = PurchaseButtonBg(canAfford);
            buttonHl = PurchaseButtonHover(canAfford);
            buttonText = canAfford ? Palette.Accent : Palette.Warning;
        }

        var (buyBtn, _buyLbl) = UI.MakeButton(
            "CosmeticPurchaseBtn", _detailView.transform,
            buttonLabel, FontSm, buttonBg, buttonHl, buttonText,
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(Space2, Space2), new Vector2(-Space2, InspectorFootH));
        AddButtonDepth(buyBtn.gameObject, selectedName.GetHashCode());
        UI.AddPanelTexture(buyBtn.transform, TexMid, selectedName.GetHashCode());
        buyBtn.interactable = canAfford;
        buyBtn.onClick.AddListener(() =>
        {
            if (!canAfford) { MenuAudio.PlayDeny(); return; }
            PurchaseSelectedCosmetic();
        });
    }

    /// <summary>
    /// Top-down layout cursor for the right inspector.
    ///
    /// Every detail renderer used to position its sections at absolute pixel
    /// offsets measured from the top of the inspector (the collection panel at
    /// -244/-434, ammunition at -442/-617, and so on). Those numbers encoded the
    /// row counts of the day: adding a sixth ammunition family or an eighth
    /// collection row drew straight past the panel's own bottom edge, and every
    /// section below had to be re-measured by hand.
    ///
    /// This stacks instead. Each section declares only its own height; the
    /// cursor supplies the position and a uniform <see cref="InspectorGap"/>.
    /// The sections whose height depends on their content compute it from the
    /// row count, and the two lists that can grow without bound live inside a
    /// scroll view whose content is itself stacked, so growth scrolls rather
    /// than clips.
    /// </summary>
    private sealed class InspectorStack
    {
        private readonly Transform _parent;
        private readonly float _side;
        private float _cursor;

        public InspectorStack(Transform parent, float top, float side = InspectorSide)
        {
            _parent = parent;
            _side = side;
            _cursor = top;
        }

        public Transform Parent => _parent;

        /// <summary>Distance from the top of this stack down to the next free row.</summary>
        public float Cursor => _cursor;

        /// <summary>Anchor an existing rect as the next full-width section.</summary>
        public void Place(RectTransform rt, float height, float gap = InspectorGap)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.offsetMin = new Vector2(_side, -(_cursor + height));
            rt.offsetMax = new Vector2(-_side, -_cursor);
            _cursor += height + gap;
        }

        /// <summary>Full-width panel of the given height at the cursor.</summary>
        public GameObject Panel(string name, Color color, float height, float gap = InspectorGap)
        {
            var go = UI.MakePanel(name, _parent, color,
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(_side, -(_cursor + height)), new Vector2(-_side, -_cursor));
            _cursor += height + gap;
            return go;
        }

        /// <summary>
        /// Stretch a rect from the cursor down to <paramref name="bottom"/>
        /// pixels above the inspector's bottom edge. One flexible element per
        /// renderer - the description body - claims whatever is left.
        /// </summary>
        public void Fill(RectTransform rt, float bottom, float inset)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, bottom);
            rt.offsetMax = new Vector2(-inset, -_cursor);
        }
    }

    private static GameObject BuildInspectorBand(
        InspectorStack stack, string name, Color color)
    {
        var band = stack.Panel(name, color, InspectorBandH);
        UI.AddPanelTexture(band.transform, TexLow, name.GetHashCode());
        AddBorder(band.transform, 3f, 0f, Palette.BorderDim);
        return band;
    }

    private static void AddInspectorMetric(
        Transform parent, int index, int total, string label, string value, Color valueColor)
    {
        float x0 = (float)index / Mathf.Max(1, total);
        float x1 = (float)(index + 1) / Mathf.Max(1, total);

        var metric = new GameObject("Metric_" + label);
        metric.transform.SetParent(parent, false);
        var rt = metric.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(x0, 0);
        rt.anchorMax = new Vector2(x1, 1);
        rt.offsetMin = new Vector2(index == 0 ? 18f : 10f, 5f);
        rt.offsetMax = new Vector2(index == total - 1 ? -18f : -10f, -5f);

        string labelHex = "#" + ColorUtility.ToHtmlStringRGBA(Palette.Primary);
        string valueHex = "#" + ColorUtility.ToHtmlStringRGBA(valueColor);
        var pairText = UI.MakeText("MetricPair", metric.transform,
            $"<color={labelHex}>{label.ToUpperInvariant()}</color>  <color={valueHex}>{value.ToUpperInvariant()}</color>",
            FontSm, valueColor, TextAlignmentOptions.Center);
        var prt = pairText.GetComponent<RectTransform>();
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero;
        prt.offsetMax = Vector2.zero;
        pairText.richText = true;
        pairText.enableWordWrapping = false;
        pairText.overflowMode = TextOverflowModes.Ellipsis;
        pairText.enableAutoSizing = true;
        pairText.fontSizeMin = FontMicro;
        pairText.fontSizeMax = FontSm;
    }

    // - Detail / purchase panel -
    public static void showUpgradeGui(Y4NGZUpgradeNode currentUpgrade)
    {
        Y4NGZSkillTreeNodeDefinition skillNode = currentUpgrade != null
            ? Y4NGZUpgradeManager.GetSkillTreeNode(currentUpgrade.Id)
            : null;
        int treeInvestment = 0;
        bool gateLocked = false;

        if (skillNode != null)
        {
            Dictionary<string, Y4NGZUpgradeNode> upgradesById = UpgradeApi.GetUpgradeNodes()
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            treeInvestment = GetTreeInvestment(skillNode.TreeClass, upgradesById);
            gateLocked = IsSkillTreeNodeGateLocked(skillNode, currentUpgrade.GetCurrentLevel());
        }

        showUpgradeGui(currentUpgrade, skillNode, gateLocked, treeInvestment);
    }

    private static void showUpgradeGui(
        Y4NGZUpgradeNode currentUpgrade,
        Y4NGZSkillTreeNodeDefinition skillNode,
        bool gateLocked,
        int treeInvestment)
    {
        if (currentUpgrade == null)
        {
            currentSelection = null;
            RenderInspectorPlaceholder("EMPLOYEE PREVIEW", "No upgrade selected.");
            return;
        }

        currentSelection = currentUpgrade;
        _selectedSkillTreeNodeId = skillNode?.UpgradeId ?? currentUpgrade.Id;
        location = nameof(showUpgrades) + "," + currentUpgrade.SharedUpgrade;
        if (_detailView == null) return;
        _detailView.SetActive(true);

        ClearDetailViewChildren();

        var stack = new InspectorStack(_detailView.transform, InspectorTop);
        BuildPlayerPreviewPlaceholder(stack);

        int  effectivePrice = ComputeEffectivePrice(currentUpgrade);
        bool maxed          = effectivePrice == int.MaxValue;
        bool canAfford      = !maxed && CurrencyManager.Instance.CurrencyAmount >= effectivePrice;
        int  currentLevel   = currentUpgrade.GetCurrentLevel();
        SkillTreeLockState lockState = GetSkillTreeLockState(skillNode, currentLevel);
        bool prerequisiteLocked = lockState.PrerequisiteLocked;
        string displayName  = skillNode != null && !string.IsNullOrWhiteSpace(skillNode.DisplayName)
            ? skillNode.DisplayName
            : currentUpgrade.Name;

        Color detailTypeColor = Palette.Accent;
        var nameHdr = stack.Panel("NameHdr", Palette.BgHeader, InspectorNameH);
        UI.AddPanelTexture(nameHdr.transform, TexMid, displayName.GetHashCode());
        AddBorder(nameHdr.transform, 3f, 0f, Palette.BorderDim);
        UI.MakePanel("NameAccent", nameHdr.transform, Palette.Accent,
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(0, 0), new Vector2(4, 0));
        UI.MakeText("Name", nameHdr.transform,
            displayName, FontMd, Palette.Primary,
            TextAlignmentOptions.MidlineLeft)
            .GetComponent<RectTransform>().offsetMin = new Vector2(14, 0);


        // - Build description: stats + world-building -
        string statsText  = "";
        string loreText   = "";
        BuildUpgradeDescription(currentUpgrade, out statsText, out loreText);
        List<UpgradeLevelDetail> levelDetails = BuildUpgradeLevelDetails(
            currentUpgrade,
            out string summaryText);

        // description scroll - scrollbar on so long descriptions can be
        // dragged as well as wheel-scrolled.
        // A refusal notice takes a line above the purchase button, so the description gives it room.
        bool hasNotice = TryGetPurchaseNotice(currentUpgrade.Id, out string noticeText, out Color noticeColor);
        var (descScroll, descContent) = UI.MakeScrollView("DescScroll",
            _detailView.transform,
            new Vector2(0, 0), new Vector2(1, 1),
            new Vector2(InspectorSide, hasNotice ? PurchaseNoticeTop : 54),
            new Vector2(-InspectorSide, -stack.Cursor),
            addScrollbar: true);
        var descBg = descScroll.AddComponent<Image>();
        descBg.color = Palette.BgInput;
        descBg.raycastTarget = false;
        UI.AddPanelTexture(descScroll.transform, TexFaint, currentUpgrade.Name.GetHashCode() ^ 0x57C4);
        AddBorder(descScroll.transform, 3f, 0f, Palette.BorderDim);

        // Stats section (per-level info) - orange accent
        float descY = 0f;
        if (levelDetails.Count > 0)
        {
            descY = BuildUpgradeLevelDetailRows(
                descContent,
                levelDetails,
                summaryText,
                detailTypeColor);
        }
        else if (!string.IsNullOrEmpty(statsText))
        {
            var statsTmp = UI.MakeText("Stats", descContent,
                statsText.TrimEnd(), FontMd, Palette.Accent);
            statsTmp.richText = true;
            statsTmp.enableWordWrapping = true;
            statsTmp.overflowMode       = TextOverflowModes.Overflow;
            var statsRt = statsTmp.GetComponent<RectTransform>();
            statsRt.anchorMin = new Vector2(0, 1);
            statsRt.anchorMax = new Vector2(1, 1);
            statsRt.pivot     = new Vector2(0.5f, 1f);

            // Force mesh update to get preferred height
            statsTmp.ForceMeshUpdate();
            float statsH = statsTmp.preferredHeight + 12f;
            statsRt.sizeDelta = new Vector2(-12f, statsH);
            statsRt.anchoredPosition = new Vector2(0, -6f);
            descY = statsH + 12f;
        }

        // World-building text follows with whitespace instead of another rule.
        if (!string.IsNullOrEmpty(loreText))
        {
            descY += 6f;

            // Lore text - warmer and dimmer than mechanical upgrade terms.
            var loreTmp = UI.MakeText("Lore", descContent,
                loreText.Trim(), FontSm, Palette.Lore);
            loreTmp.enableWordWrapping = true;
            loreTmp.overflowMode       = TextOverflowModes.Overflow;
            loreTmp.fontStyle          = FontStyles.Italic;
            var loreRt = loreTmp.GetComponent<RectTransform>();
            loreRt.anchorMin = new Vector2(0, 1);
            loreRt.anchorMax = new Vector2(1, 1);
            loreRt.pivot     = new Vector2(0.5f, 1f);
            loreTmp.ForceMeshUpdate();
            float loreH = loreTmp.preferredHeight + 12f;
            loreRt.sizeDelta = new Vector2(-12f, loreH);
            loreRt.anchoredPosition = new Vector2(0, -(descY + 2f));
            descY += loreH + 8f;
        }

        // If no generated text, fall back to original Description
        if (string.IsNullOrEmpty(statsText) && string.IsNullOrEmpty(loreText))
        {
            var descText = UI.MakeText("Desc", descContent,
                currentUpgrade.Description, FontMd, Palette.Primary);
            descText.enableWordWrapping = true;
            descText.overflowMode       = TextOverflowModes.Overflow;
            var fallbackRt = descText.GetComponent<RectTransform>();
            fallbackRt.anchorMin = new Vector2(0, 1);
            fallbackRt.anchorMax = new Vector2(1, 1);
            fallbackRt.pivot     = new Vector2(0.5f, 1f);
            descText.ForceMeshUpdate();
            float fbH = descText.preferredHeight + 12f;
            fallbackRt.sizeDelta = new Vector2(-12f, fbH);
            fallbackRt.anchoredPosition = new Vector2(0, -6f);
            descY = fbH + 12f;
        }

        // Set scroll content height
        var descCrt = descContent.GetComponent<RectTransform>();
        descCrt.sizeDelta = new Vector2(0, descY + 8f);
        var descSR = descScroll.GetComponent<ScrollRect>();
        UI.ResetScrollToTop(descSR, descCrt);

        // button row at bottom
        float btnY  = 8f;
        float btnH  = 34f;

        // purchase button label
        string btnLabel;
        Color  btnBg, btnHl, btnTxt;
        if (maxed)
        {
            btnLabel = "- MAXED -";
            btnBg    = PurchaseButtonBg(false);
            btnHl    = PurchaseButtonHover(false);
            btnTxt   = Palette.Gold;
        }
        else if (gateLocked && skillNode != null)
        {
            btnLabel = $"LOCKED  {treeInvestment}/{skillNode.GateRequirement} TREE LEVELS";
            btnBg    = PurchaseButtonBg(false);
            btnHl    = PurchaseButtonHover(false);
            btnTxt   = Palette.Muted;
        }
        else if (prerequisiteLocked && skillNode != null)
        {
            btnLabel = lockState.Label;
            btnBg    = PurchaseButtonBg(false);
            btnHl    = PurchaseButtonHover(false);
            btnTxt   = Palette.Muted;
        }
        else
        {
            btnLabel = $"PURCHASE  {FormatMarks(effectivePrice)}";
            btnBg    = PurchaseButtonBg(canAfford);
            btnHl    = PurchaseButtonHover(canAfford);
            btnTxt   = canAfford ? Palette.Accent : Palette.Warning;
        }

        var (purchaseBtn, purchaseLbl) = UI.MakeButton("PurchaseBtn",
            _detailView.transform,
            btnLabel, FontSm, btnBg, btnHl, btnTxt,
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(8, btnY),
            new Vector2(-8, btnY + btnH));
        AddButtonDepth(purchaseBtn.gameObject, displayName.GetHashCode());
        UI.AddPanelTexture(purchaseBtn.transform, TexMid, displayName.GetHashCode());

        purchaseBtn.interactable = !maxed && !gateLocked && !prerequisiteLocked;

        if (hasNotice)
        {
            var notice = UI.MakeText("PurchaseNotice", _detailView.transform,
                noticeText, FontSm, noticeColor, TextAlignmentOptions.Midline);
            notice.enableWordWrapping = false;
            notice.overflowMode = TextOverflowModes.Ellipsis;
            var noticeRt = notice.GetComponent<RectTransform>();
            noticeRt.anchorMin = new Vector2(0, 0);
            noticeRt.anchorMax = new Vector2(1, 0);
            noticeRt.offsetMin = new Vector2(14, btnY + btnH + 2f);
            noticeRt.offsetMax = new Vector2(-14, PurchaseNoticeTop - 2f);
        }

        purchaseBtn.onClick.AddListener(() =>
        {
            if (gateLocked)
            {
                MenuAudio.PlayDeny();
                return;
            }
            if (prerequisiteLocked)
            {
                MenuAudio.PlayDeny();
                return;
            }

            // The label this click acted on is the one rendered above; the charge is only
            // authorized for exactly that amount. If the live price has moved since (a save
            // re-point mid-menu), the manager refuses instead of charging a different number.
            int quotedPrice = effectivePrice;
            if (quotedPrice == int.MaxValue || CurrencyManager.Instance.CurrencyAmount < quotedPrice)
            {
                MenuAudio.PlayDeny();
                // A rejected purchase is a real failure, so the flash keeps the
                // danger red even though the resting wallet color is themed.
                PulseCurrency(Palette.Danger);
                SetPurchaseNotice(currentUpgrade.Id, "NOT ENOUGH TOKENS", Palette.Warning);
                showUpgrades(currentUpgrade.SharedUpgrade, resetScroll: false);
                return;
            }

            // Read the tier at click time, like the price above, so the tier the sound announces
            // is the tier actually bought. The jingle only plays once the purchase went through.
            int purchasedTier = currentUpgrade.GetCurrentLevel() + 1;
            Plugin.ExtendedLogging($"Purchasing: {currentUpgrade.Name}");
            Y4NGZUpgradePurchaseResult result =
                UpgradeApi.TriggerUpgradeRankup(currentUpgrade, quotedPrice);
            if (result == Y4NGZUpgradePurchaseResult.Success)
            {
                ClearPurchaseNotice();
                MenuAudio.PlayPurchase(purchasedTier);
                PulseCurrency(Palette.Gold);
            }
            else
            {
                MenuAudio.PlayDeny();
                PulseCurrency(Palette.Danger);
                SetPurchaseNotice(currentUpgrade.Id, DescribePurchaseFailure(result), Palette.Danger);
            }

            RefreshCurrencyDisplay();
            // Always re-render: a failed attempt must repaint the price label it was refused
            // against, not leave the stale quote on screen.
            showUpgrades(currentUpgrade.SharedUpgrade, resetScroll: false);
        });
    }

    // Legacy entry point retained for any external caller. Ammunition now lives
    // in the Employee File and no longer has a standalone navigation tab.
    public static void showAmmo()
    {
        showEmployeeFile();
    }

    // - Trade panel -
    public static void showTradeGui()
    {
        currentSelection = null;
        location = nameof(showTradeGui);
        _activeView = () => showTradeGui();
        ShowView(_tradeView);
        // Transfers are typed into TMP_InputFields, so this view deliberately
        // registers no focus targets: W/S/A/D belong to whatever has the caret.
        ClearFocusTargets();
        SetFooterHint(HintTrade);
        RefreshCurrencyDisplay();
        RenderInspectorPlaceholder("TRADE", "Select a crewmate and transfer spendable Tokens.");

        foreach (Transform child in _tradeView.transform)
            Object.Destroy(child.gameObject);

        int currency = CurrencyManager.Instance.CurrencyAmount;

        // scroll to list players
        var (scrollGo, content) = UI.MakeScrollView("TradeScroll",
            _tradeView.transform,
            Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero);

        const float tradePad  = 4f;
        const float tradeRowH = 46f;
        const float tradeSpc  = 3f;
        int tradeIdx = 0;

        foreach (var player in StartOfRound.Instance.allPlayerScripts
                     .OrderBy(p => p.playerUsername))
        {
            if (StartOfRound.Instance.localPlayerController == player) continue;
            if (!player.isPlayerControlled && !player.isPlayerDead)    continue;

            float yTop = tradePad + tradeIdx * (tradeRowH + tradeSpc);

            // player row container - manual position
            var row = new GameObject("TradeRow_" + player.playerUsername);
            row.transform.SetParent(content, false);
            var rowImg = row.AddComponent<Image>();
            rowImg.color = Palette.BgRow;
            var rowRt2 = rowImg.rectTransform;
            rowRt2.anchorMin = new Vector2(0, 1);
            rowRt2.anchorMax = new Vector2(1, 1);
            rowRt2.pivot     = new Vector2(0.5f, 1f);
            rowRt2.offsetMin = new Vector2(tradePad, -(yTop + tradeRowH));
            rowRt2.offsetMax = new Vector2(-tradePad, -yTop);
            tradeIdx++;

            // player name
            var nameText = UI.MakeText("PlayerName", row.transform,
                player.playerUsername, FontMd, Palette.Gold);
            var nrt = nameText.GetComponent<RectTransform>();
            nrt.anchorMin = new Vector2(0,    0);
            nrt.anchorMax = new Vector2(0.4f, 1);
            nrt.offsetMin = new Vector2(10, 0);
            nrt.offsetMax = new Vector2(-4, 0);
            nameText.fontStyle = FontStyles.Bold;

            // amount input
            var targetId    = player.actualClientId;
            bool isUpdating = false;
            var input = UI.MakeInputField("Input_" + player.playerUsername,
                row.transform, "0", FontMd,
                new Vector2(0.4f, 0), new Vector2(0.72f, 1),
                new Vector2(4, 6), new Vector2(-4, -6));
            input.text = "0";

            // send button
            var (sendBtn, sendLbl) = UI.MakeButton("SendBtn", row.transform,
                "SEND  TOKENS", FontSm, Palette.BtnNormal, Palette.BtnHighlight, Palette.Sale,
                new Vector2(0.72f, 0), new Vector2(1, 1),
                new Vector2(4, 6), new Vector2(-8, -6));
            sendBtn.interactable = false;

            input.onValueChanged.AddListener((string text) =>
            {
                if (isUpdating) return;
                if (int.TryParse(text, out int amount))
                {
                    int clamped = Mathf.Clamp(amount, 0, currency);
                    if (clamped.ToString() != text)
                    {
                        isUpdating  = true;
                        input.text  = clamped.ToString();
                        isUpdating  = false;
                    }
                    sendBtn.interactable = clamped > 0;
                }
                else
                {
                    isUpdating = true;
                    input.text = "0";
                    isUpdating = false;
                    sendBtn.interactable = false;
                }
            });

            sendBtn.onClick.AddListener(() =>
            {
                int amt = int.Parse(input.text);
                if (!CurrencyManager.Instance.RequestPlayerTokenTrade(targetId, amt))
                {
                    MenuAudio.PlayDeny();
                    return;
                }

                MenuAudio.PlayClick();
                showTradeGui();
            });
        }

        // Set content height to exactly fit all trade rows.
        float tradeH = tradeIdx > 0
            ? tradePad + tradeIdx * (tradeRowH + tradeSpc) - tradeSpc + tradePad : 0f;
        var tradeCrt = content.GetComponent<RectTransform>();
        tradeCrt.sizeDelta = new Vector2(0, tradeH);
        var tradeScrollRect = scrollGo.GetComponent<ScrollRect>();
        UI.ResetScrollToTop(tradeScrollRect, tradeCrt);
    }

    // - Player Level view -
    // Replaces the old Cosmetics-only tab. Shows Y4NGZ rank header,
    // suits list (gated by rank via auto-generated config), MoreCompany
    // cosmetics, and (if installed) TooManyEmotes unlocked emotes.

    // Cached reflection for MoreCompany
    private static bool       _mcReflResolved;
    private static FieldInfo  _mcInstancesField;     // CosmeticRegistry.cosmeticInstances
    private static FieldInfo  _mcSelectedField;      // CosmeticRegistry.locallySelectedCosmetics
    private static MethodInfo _mcToggleMethod;        // CosmeticRegistry.ToggleCosmetic
    private static MethodInfo _mcIsEquippedMethod;    // CosmeticRegistry.IsEquipped
    private static FieldInfo  _mcDisplayGuyAppField;  // CosmeticRegistry.displayGuyCosmeticApplication
    private static MethodInfo _mcSyncCosmeticsMethod; // CosmeticSyncPatch.SyncCosmeticsToOtherClients
    private static MethodInfo _mcUpdateCosmeticsForPlayerMethod; // CosmeticSyncPatch.UpdateCosmeticsForPlayer
    private static MethodInfo _mcWriteMethod;         // MainClass.WriteCosmeticsToFile
    private static FieldInfo  _mcCosmeticIdField;     // CosmeticInstance.cosmeticId
    private static FieldInfo  _mcIconField;           // CosmeticInstance.icon
    private static FieldInfo  _mcCosmeticTypeField;   // CosmeticInstance.cosmeticType

    // CosmeticApplication - MoreCompany's own component for attaching
    // cosmetics to a humanoid bone chain. We add one to our preview clone's
    // metarig instead of borrowing MoreCompany's UI display guy.
    private static Type       _mcCosmeticApplicationType;
    private static MethodInfo _mcApplyCosmeticMethod;    // ApplyCosmetic(string, bool)
    private static MethodInfo _mcClearCosmeticsMethod;   // ClearCosmetics()
    private static FieldInfo  _mcParentTypeField;        // parentType
    private static FieldInfo  _mcSpawnedCosmeticsField;  // spawnedCosmetics
    private static object     _mcParentTypeDisplayGuy;   // ParentType.DisplayGuy

    private static void ResolveMCReflection()
    {
        if (_mcReflResolved) return;
        _mcReflResolved = true;

        Assembly asm = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name == "MoreCompany")
            {
                asm = a;
                break;
            }
        }
        if (asm == null) return;

        var regType = asm.GetType("MoreCompany.Cosmetics.CosmeticRegistry");
        if (regType == null) return;

        _mcInstancesField = regType.GetField("cosmeticInstances",
            BindingFlags.Public | BindingFlags.Static);
        _mcSelectedField = regType.GetField("locallySelectedCosmetics",
            BindingFlags.Public | BindingFlags.Static);
        _mcToggleMethod = regType.GetMethod("ToggleCosmetic",
            BindingFlags.Public | BindingFlags.Static);
        _mcIsEquippedMethod = regType.GetMethod("IsEquipped",
            BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        _mcDisplayGuyAppField = regType.GetField("displayGuyCosmeticApplication",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        var mainType = asm.GetType("MoreCompany.MainClass");
        if (mainType != null)
        {
            _mcWriteMethod = mainType.GetMethod("WriteCosmeticsToFile",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        var syncType = asm.GetType("MoreCompany.CosmeticSyncPatch");
        if (syncType != null)
        {
            _mcSyncCosmeticsMethod = syncType.GetMethod("SyncCosmeticsToOtherClients",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(PlayerControllerB), typeof(bool), typeof(bool) }, null);
            _mcUpdateCosmeticsForPlayerMethod = syncType.GetMethod("UpdateCosmeticsForPlayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(int), typeof(List<string>) }, null);
        }

        // CosmeticInstance fields
        var ciType = asm.GetType("MoreCompany.Cosmetics.CosmeticInstance");
        if (ciType != null)
        {
            _mcCosmeticIdField   = ciType.GetField("cosmeticId",   BindingFlags.Public | BindingFlags.Instance);
            _mcIconField         = ciType.GetField("icon",         BindingFlags.Public | BindingFlags.Instance);
            _mcCosmeticTypeField = ciType.GetField("cosmeticType", BindingFlags.Public | BindingFlags.Instance);
        }

        var caType = asm.GetType("MoreCompany.Cosmetics.CosmeticApplication");
        if (caType != null)
        {
            _mcCosmeticApplicationType = caType;
            _mcApplyCosmeticMethod   = caType.GetMethod("ApplyCosmetic", new[] { typeof(string), typeof(bool) });
            _mcClearCosmeticsMethod  = caType.GetMethod("ClearCosmetics", Type.EmptyTypes);
            _mcParentTypeField       = caType.GetField("parentType", BindingFlags.Public | BindingFlags.Instance);
            _mcSpawnedCosmeticsField = caType.GetField("spawnedCosmetics", BindingFlags.Public | BindingFlags.Instance);
        }

        var ptType = asm.GetType("MoreCompany.Cosmetics.ParentType");
        if (ptType != null)
        {
            try { _mcParentTypeDisplayGuy = Enum.Parse(ptType, "DisplayGuy"); }
            catch { }
        }
    }

    private static string CosmeticSelectionKey(object entry)
    {
        return entry as string ?? entry?.ToString();
    }

    private static bool IsMoreCompanyCosmeticSelected(IList selectedList, string cosmeticId)
    {
        if (selectedList == null || string.IsNullOrEmpty(cosmeticId)) return false;
        foreach (object entry in selectedList)
        {
            string selectedId = CosmeticSelectionKey(entry);
            if (string.Equals(selectedId, cosmeticId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsMoreCompanyCosmeticEquipped(string cosmeticId, IList selectedList = null)
    {
        if (string.IsNullOrEmpty(cosmeticId)) return false;

        ResolveMCReflection();
        if (_mcIsEquippedMethod != null)
        {
            try
            {
                return Convert.ToBoolean(_mcIsEquippedMethod.Invoke(null, new object[] { cosmeticId }));
            }
            catch { }
        }

        selectedList ??= _mcSelectedField?.GetValue(null) as IList;
        return IsMoreCompanyCosmeticSelected(selectedList, cosmeticId);
    }

    private static bool SetMoreCompanyCosmeticSelected(string cosmeticId, bool selected)
    {
        ResolveMCReflection();
        var selectedList = _mcSelectedField?.GetValue(null) as IList;
        if (selectedList == null || string.IsNullOrEmpty(cosmeticId)) return false;

        bool currentlySelected = IsMoreCompanyCosmeticSelected(selectedList, cosmeticId);
        if (selected)
        {
            if (!currentlySelected) selectedList.Add(cosmeticId);
            return true;
        }

        if (!currentlySelected) return true;
        for (int i = selectedList.Count - 1; i >= 0; i--)
        {
            string selectedId = CosmeticSelectionKey(selectedList[i]);
            if (string.Equals(selectedId, cosmeticId, StringComparison.OrdinalIgnoreCase))
                selectedList.RemoveAt(i);
        }
        return true;
    }

    private static List<string> GetMoreCompanySelectedCosmeticsSnapshot()
    {
        ResolveMCReflection();
        var selectedList = _mcSelectedField?.GetValue(null) as IList;
        var snapshot = new List<string>();
        if (selectedList == null) return snapshot;

        foreach (object entry in selectedList)
        {
            string selectedId = CosmeticSelectionKey(entry);
            if (!string.IsNullOrEmpty(selectedId))
                snapshot.Add(selectedId);
        }
        return snapshot;
    }

    private static int GetLocalPlayerClientId(PlayerControllerB player)
    {
        try
        {
            if (StartOfRound.Instance != null)
                return StartOfRound.Instance.thisClientPlayerId;
        }
        catch { }

        try { return player != null ? (int)player.playerClientId : 0; }
        catch { return 0; }
    }

    private static void HideAndDestroySpawnedCosmetics(object cosmeticApp, Transform requiredAncestor)
    {
        if (cosmeticApp == null) return;

        try
        {
            if (!(_mcSpawnedCosmeticsField?.GetValue(cosmeticApp) is IEnumerable spawned))
                return;

            foreach (object inst in spawned)
            {
                GameObject go = (inst as Component)?.gameObject ?? inst as GameObject;
                if (go == null) continue;
                if (requiredAncestor != null && !go.transform.IsChildOf(requiredAncestor))
                    continue;

                go.SetActive(false);
                Object.Destroy(go);
            }
        }
        catch { }
    }

    private static void ClearMoreCompanyDisplayGuyCosmetics()
    {
        ResolveMCReflection();
        if (_mcDisplayGuyAppField == null || _mcClearCosmeticsMethod == null)
            return;

        try
        {
            object displayGuyApp = _mcDisplayGuyAppField.GetValue(null);
            if (displayGuyApp == null) return;

            HideAndDestroySpawnedCosmetics(displayGuyApp, null);
            _mcClearCosmeticsMethod.Invoke(displayGuyApp, null);
        }
        catch { }
    }

    private static void ClearPlayerMoreCompanyCosmeticsImmediate(PlayerControllerB player)
    {
        ResolveMCReflection();
        if (player == null || _mcCosmeticApplicationType == null)
            return;

        try
        {
            Transform metarig = player.transform.Find("ScavengerModel/metarig");
            if (metarig == null) return;

            var app = metarig.GetComponent(_mcCosmeticApplicationType);
            if (app == null) return;

            HideAndDestroySpawnedCosmetics(app, player.transform);
        }
        catch { }
    }

    private static void RefreshMoreCompanyCosmeticsAfterSelectionChange()
    {
        var selected = GetMoreCompanySelectedCosmeticsSnapshot();

        try { _mcWriteMethod?.Invoke(null, null); }
        catch { }

        ClearMoreCompanyDisplayGuyCosmetics();

        try
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (player != null)
            {
                if (_mcUpdateCosmeticsForPlayerMethod != null)
                {
                    ClearPlayerMoreCompanyCosmeticsImmediate(player);
                    _mcUpdateCosmeticsForPlayerMethod.Invoke(null, new object[]
                    {
                        GetLocalPlayerClientId(player),
                        selected
                    });
                }
                _mcSyncCosmeticsMethod?.Invoke(null, new object[] { player, false, false });
            }
        }
        catch { }
    }

    private static bool ToggleMoreCompanyCosmeticSelection(string cosmeticId)
    {
        if (string.IsNullOrEmpty(cosmeticId)) return false;

        ResolveMCReflection();
        var selectedList = _mcSelectedField?.GetValue(null) as IList;
        bool shouldEquip = !IsMoreCompanyCosmeticEquipped(cosmeticId, selectedList);

        try
        {
            if (_mcToggleMethod != null)
                _mcToggleMethod.Invoke(null, new object[] { cosmeticId });
        }
        catch { }

        try
        {
            bool afterToggle = IsMoreCompanyCosmeticEquipped(cosmeticId);
            if (afterToggle != shouldEquip)
                SetMoreCompanyCosmeticSelected(cosmeticId, shouldEquip);
        }
        catch { }

        RefreshMoreCompanyCosmeticsAfterSelectionChange();
        return shouldEquip;
    }

    // Destroy any cosmetic instances the clone inherited from the real
    // player (MoreCompany parents them to bones, hidden for the local
    // player). The cloned CosmeticApplication's spawnedCosmetics list is
    // remapped to the clone's own children by Instantiate.
    private static void ClearCloneCosmetics(GameObject clone)
    {
        ResolveMCReflection();
        if (_mcCosmeticApplicationType == null || _mcClearCosmeticsMethod == null)
            return;

        try
        {
            foreach (var app in clone.GetComponentsInChildren(_mcCosmeticApplicationType, true))
            {
                HideAndDestroySpawnedCosmetics(app, clone.transform);
                _mcClearCosmeticsMethod.Invoke(app, null);
            }
        }
        catch { }
    }

    private static bool _previewCosmeticsWarned;

    private static void ApplyMoreCompanyCosmeticsToPreview(GameObject metarig)
    {
        ResolveMCReflection();
        if (metarig == null || _mcCosmeticApplicationType == null || _mcApplyCosmeticMethod == null)
            return;

        // CosmeticApplication resolves its attachment points off a vanilla LC
        // metarig in Awake. Handed a rig that has none - a ModelReplacementAPI
        // body being the case that reaches here - it does not throw: it spawns
        // the cosmetics with a null parent, i.e. loose at the world origin in
        // the live scene. Bail before AddComponent rather than leaving that
        // litter behind.
        if (metarig.transform.Find("spine/spine.001/spine.002/spine.003") == null)
            return;

        var selected = _mcSelectedField?.GetValue(null) as IList;
        if (selected == null || selected.Count == 0)
            return;

        try
        {
            var app = metarig.AddComponent(_mcCosmeticApplicationType);
            // DisplayGuy keeps cosmetics visible regardless of the user's
            // "show cosmetics" settings. Set AFTER AddComponent: Awake runs
            // during AddComponent with the default Player type, and the
            // DisplayGuy branch of Awake would call back into MoreCompany's
            // own display guy, which may not exist in-game.
            if (_mcParentTypeField != null && _mcParentTypeDisplayGuy != null)
                _mcParentTypeField.SetValue(app, _mcParentTypeDisplayGuy);

            foreach (object entry in selected)
            {
                string cosmeticId = CosmeticSelectionKey(entry);
                if (!string.IsNullOrEmpty(cosmeticId))
                    _mcApplyCosmeticMethod.Invoke(app, new object[] { cosmeticId, true });
            }

            // MoreCompany scales cosmetics down by this factor when attaching
            // them to real player rigs (COSMETIC_PLAYER_SCALE_MULT).
            if (_mcSpawnedCosmeticsField?.GetValue(app) is IEnumerable spawned)
            {
                foreach (object inst in spawned)
                {
                    var t = (inst as Component)?.transform;
                    if (t != null) t.localScale *= 0.38f;
                }
            }
        }
        catch (Exception e)
        {
            // Once per session: this runs from the preview rebuild path, which
            // fires on every suit/cosmetic change, so a per-rebuild warning
            // would flood the log.
            if (!_previewCosmeticsWarned)
            {
                _previewCosmeticsWarned = true;
                Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu] preview cosmetics failed: {e.Message}");
            }
        }
    }

    // resetScroll: when true, snap content back to the top after rebuild.
    // Default false so in-page interactions (suit/cosmetic/emote clicks,
    // post-purchase rebuilds) preserve the user's scroll position. Only
    // tab/category/tier switches pass true.
    public static void showPlayerLevel(bool resetScroll = false)
    {
        currentSelection = null;
        location = nameof(showPlayerLevel);
        _activeView = () => showPlayerLevel(resetScroll: false);
        ShowView(_playerLevelView);
        ClearFocusTargets();

        // Selection/preview state intentionally persists across rebuilds.
        // The two-click purchase flow (click row to select, click again to
        // confirm) calls showPlayerLevel() to re-render with [SELECTED]
        // visuals - clearing _selectedEmoteForPurchase here would wipe that
        // state synchronously and break the second click. Selection IS reset
        // when the user changes category/tier (see rail handlers below) and
        // when the menu is closed (MenuController.AnimateClose).

        // Clear previous content
        foreach (Transform child in _playerLevelView.transform)
        {
            child.gameObject.SetActive(false);
            Object.Destroy(child.gameObject);
        }

        EnsureEmotesReflection();

        // The outer mode rail now owns SUITS/COSMETICS/EMOTES. The local
        // rail is only kept for emote rarity tiers, where there is still a
        // genuine secondary choice.
        var categories = new List<string> { CosCatSuits };
        if (GetCosmeticCollectionCount().total > 0) categories.Add(CosCatCosmetics);
        if (GetEmoteCollectionCount().total > 0) categories.Add(CosCatEmotes);

        // Snap active category back to a visible one if EMOTES disappears
        // on subsequent opens (mod uninstall mid-session, etc.).
        if (!categories.Contains(_activeCosCategory)) _activeCosCategory = CosCatSuits;
        UpdateNavTabs(_playerLevelView);

        // Discover tier list lazily so the rail can render indented sub-rails.
        // Sub-rails are auto-populated from TooManyEmotes - never hardcoded.
        if (_activeCosCategory == CosCatEmotes && _emotesAvailable)
            EnsureEmoteTiersDiscovered();

        if (_activeCosCategory == CosCatEmotes && _emotesAvailable)
            RenderEmoteInspector();
        else if (_activeCosCategory == CosCatCosmetics)
            RenderCosmeticInspector();
        else
            RenderInspectorPlaceholder(_activeCosCategory, "Inspecting or equipping player items will use this employee render.");

        // - Right-hand content area (per-category) -
        var contentArea = new GameObject("CosContentArea");
        contentArea.transform.SetParent(_playerLevelView.transform, false);
        var carea = contentArea.AddComponent<RectTransform>();
        carea.anchorMin = new Vector2(0, 0);
        carea.anchorMax = new Vector2(1, 1);
        carea.offsetMin = Vector2.zero;
        carea.offsetMax = new Vector2(0, 0);

        float scrollLeftOffset = 0f;

        // Per-category scroll view - own ScrollRect, own content,
        // resets to top on every showPlayerLevel call.
        // Scroll fills the full vertical extent of contentArea.
        var (scrollGo, scrollContent) = UI.MakeScrollView("CosScroll",
            contentArea.transform,
            new Vector2(0, 0), new Vector2(1, 1),
            new Vector2(scrollLeftOffset, 0), new Vector2(0, 0),
            addScrollbar: _activeCosCategory == CosCatCosmetics || _activeCosCategory == CosCatEmotes);
        _cosScrollContent = scrollContent;
        _cosScrollRect    = scrollGo.GetComponent<ScrollRect>();

        // Render the active category into the scroll content
        float yCursor = 4f;
        if      (_activeCosCategory == CosCatSuits)     yCursor = BuildSuitsSection(scrollContent, yCursor);
        else if (_activeCosCategory == CosCatCosmetics) yCursor = BuildCosmeticsSection(scrollContent, yCursor);
        else if (_activeCosCategory == CosCatEmotes)    yCursor = BuildEmotesSection(scrollContent, yCursor);

        // COSMETICS is the only one of the three laid out as a grid, so it is
        // the only one where A/D means anything.
        SetFooterHint(_activeCosCategory == CosCatCosmetics ? HintGrid : HintList);

        var contentRt = scrollContent.GetComponent<RectTransform>();
        contentRt.sizeDelta = new Vector2(0, yCursor + 6f);
        if (resetScroll) ResetScrollForRebuild(_cosScrollRect, contentRt);

        CommitFocusTargets();
    }

    private static System.Collections.IEnumerator DeferredAutoPreview()
    {
        yield return null;
        // Re-check conditions: the user may have clicked a row or
        // switched category during the one-frame wait.
        if (_activeCosCategory != CosCatEmotes) yield break;
        if (!_emotesAvailable) yield break;
        if (_selectedEmoteForPurchase != null) yield break;
        EnsureAutoPreviewForActiveTier();
    }

    // Picks an emote for the current tier and asks TooManyEmotes to play it
    // on its preview rig, but only if the rig isn't already showing one of
    // ours from this tier - re-triggering the same emote would interrupt the
    // animation on every menu rebuild.
    private static void EnsureAutoPreviewForActiveTier()
    {
        var all = GetAllPurchasableEmotes();
        if (all == null) return;
        var tierEmotes = all.Where(e => e.rarity == _activeEmoteTier).ToList();
        if (tierEmotes.Count == 0) return;

        // If something from this tier is already playing, leave it alone.
        if (_previewingEmote != null
            && tierEmotes.Any(e => ReferenceEquals(e.obj, _previewingEmote)))
            return;

        var first = tierEmotes[0];
        _previewingEmote = first.obj;

        // - DIAG: AUTOFIRE path entry (Bug 5) -
        // Log everything we know about the emote BEFORE handing it to
        // PlayEmotePreview so we can compare against the CLICK path log.
        if (EmotePreviewDiagnostics)
        {
            try
            {
                string emName = first.emoteName;
                object emObj  = first.obj;
                bool clipPresent = false, loopPresent = false;
                try
                {
                    var t = emObj?.GetType();
                    if (t != null)
                    {
                        const BindingFlags PUB_INST = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                        var fAnimClip = t.GetField("animationClip", PUB_INST)
                                     ?? t.GetField("transitionsToClip", PUB_INST);
                        if (fAnimClip != null) clipPresent = fAnimClip.GetValue(emObj) != null;
                        var fAudio = t.GetProperty("audioClipName", PUB_INST);
                        if (fAudio != null)
                        {
                            var v = fAudio.GetValue(emObj) as string;
                            loopPresent = !string.IsNullOrEmpty(v);
                        }
                    }
                }
                catch { }
                Plugin.CustomLogger?.LogInfo(
                    $"[Y4NGZMenu][DIAG][AUTOFIRE] tier={_activeEmoteTier} " +
                    $"emote={emName} animClipPresent={clipPresent} audioClipPresent={loopPresent} " +
                    $"frame={Time.frameCount}");
            }
            catch { }
        }

        PlayEmotePreview(first.obj);
        LogPostPlayDiag("AUTOFIRE-POST");
    }

    // Back-compat alias - older paths (mockup_preview, external patches)
    // may still reference showCosmetics. Routes to the new tab.
    public static void showCosmetics() => showPlayerLevel();

    private static void SelectCosmeticForInspection(string cosmeticId, string label, string typeLabel)
    {
        _selectedCosmeticForPurchase = cosmeticId;
        _selectedCosmeticLabel = label;
        _selectedCosmeticTypeLabel = typeLabel;
    }

    private static void RefreshPlayerLevelAfterCosmeticChange()
    {
        showPlayerLevel();

        var host = _root != null ? _root.GetComponent<MenuController>() : null;
        if (host != null && host.isActiveAndEnabled)
        {
            host.StartCoroutine(DeferredCosmeticPreviewRefresh());
            return;
        }

        InvalidateEmployeePreviewModel();
        showPlayerLevel();
    }

    private static IEnumerator DeferredCosmeticPreviewRefresh()
    {
        yield return null;
        yield return null;

        if (_root == null || _playerLevelView == null || location != nameof(showPlayerLevel))
            yield break;

        InvalidateEmployeePreviewModel();
        showPlayerLevel();
    }

    // - Player level: suits section (single column, full-width rows) -
    // Returns the new yCursor. Rows are full-width to mirror the
    // ENHANCEMENTS row geometry - same height (RowH) and same fill style.
    private static float BuildSuitsSection(Transform content, float yCursor)
    {
        var sor = StartOfRound.Instance;
        var suits = new List<(int unlockableId, string name, bool isDefault)>();
        if (sor?.unlockablesList?.unlockables != null)
        {
            var list = sor.unlockablesList.unlockables;
            for (int i = 0; i < list.Count; i++)
            {
                var u = list[i];
                if (u == null) continue;
                if (u.unlockableType != 0)   continue;     // 0 = suit
                if (u.suitMaterial   == null) continue;
                string nm = !string.IsNullOrEmpty(u.unlockableName) ? u.unlockableName : ("Suit " + i);
                suits.Add((i, nm, u.alreadyUnlocked));
            }
        }

        if (suits.Count == 0)
        {
            UI.MakeText("NoSuits", content,
                "No suits found.", FontSm, Palette.Dim, TextAlignmentOptions.MidlineLeft)
                .GetComponent<RectTransform>().offsetMin = new Vector2(14, -(yCursor + 18f));
            return yCursor + 18f;
        }

        var data  = PlayerLevelStore.Get();
        int price = Mathf.Max(0, Plugin.SuitPrice.Value);
        int bxp   = SafeCurrency();

        const float pad = 4f;
        const float spacing = 3f;
        for (int idx = 0; idx < suits.Count; idx++)
        {
            int    unlockableId = suits[idx].unlockableId;
            string suitName     = suits[idx].name;
            bool   owned        = suits[idx].isDefault || data.suits.Contains(suitName);
            string ownedRight   = "[OWNED]";

            var (suitBtn, _) = BuildOwnableRow(
                content, "Suit_" + suitName, suitName, ownedRight,
                price, owned, /*highlightOwned*/ false, bxp,
                yCursor, pad,
                onActivateOwned: () =>
                {
                    MenuAudio.PlayClick();
                    TrySwitchSuit(unlockableId);
                    showPlayerLevel();
                },
                onPurchase: () =>
                {
                    if (!TryDeductCurrency(price)) { MenuAudio.PlayDeny(); return; }
                    MenuAudio.PlayPurchaseRandom();
                    if (!data.suits.Contains(suitName)) data.suits.Add(suitName);
                    PlayerLevelStore.Save();
                    showPlayerLevel();
                });
            RegisterFocusTarget("suit:" + suitName,
                suitBtn != null ? suitBtn.GetComponent<RectTransform>() : null,
                suitBtn, idx, 0, _cosScrollRect);
            yCursor += RowH + spacing;
        }
        return yCursor;
    }

    // - Player level: cosmetics section (purchase + toggle) -
    private static float BuildCosmeticsSection(Transform content, float yCursor)
    {
        ResolveMCReflection();
        if (_mcInstancesField == null)
        {
            UI.MakeText("NoCos", content,
                "MoreCompany not detected - cosmetics unavailable.",
                FontSm, Palette.Dim, TextAlignmentOptions.MidlineLeft)
                .GetComponent<RectTransform>().offsetMin = new Vector2(14, -(yCursor + 18f));
            return yCursor + 18f;
        }

        var rawDict = _mcInstancesField.GetValue(null) as IDictionary;
        if (rawDict == null || rawDict.Count == 0)
        {
            UI.MakeText("NoCos", content,
                "No cosmetics loaded.",
                FontSm, Palette.Dim, TextAlignmentOptions.MidlineLeft)
                .GetComponent<RectTransform>().offsetMin = new Vector2(14, -(yCursor + 18f));
            return yCursor + 18f;
        }

        var selectedList = _mcSelectedField?.GetValue(null) as IList;
        var data         = PlayerLevelStore.Get();
        int price        = Mathf.Max(0, Plugin.CosmeticPrice.Value);
        int bxp          = SafeCurrency();
        var cards = new List<(string cosmeticId, string label, string typeLabel, Texture2D iconTex, bool owned, bool equipped, bool selected)>();
        foreach (DictionaryEntry entry in rawDict)
        {
            string cosmeticId = entry.Key as string;
            if (string.IsNullOrEmpty(cosmeticId) || entry.Value == null) continue;

            string typeStr = null;
            if (_mcCosmeticTypeField != null)
            {
                try
                {
                    var t = _mcCosmeticTypeField.GetValue(entry.Value);
                    if (t != null) typeStr = t.ToString();
                }
                catch { }
            }

            Texture2D iconTex = null;
            if (_mcIconField != null)
            {
                try { iconTex = _mcIconField.GetValue(entry.Value) as Texture2D; }
                catch { }
            }

            bool   owned      = data.cosmetics.Contains(cosmeticId);
            bool   equipped   = owned && IsMoreCompanyCosmeticEquipped(cosmeticId, selectedList);
            string prettyId   = GetCosmeticDisplayName(cosmeticId, entry.Value, iconTex, typeStr, out bool fromTypeFallback);
            string prettyType = HumanizeCosmeticType(typeStr);
            string label      = !string.IsNullOrEmpty(prettyType)
                && !fromTypeFallback
                && !string.Equals(prettyId, prettyType, StringComparison.OrdinalIgnoreCase)
                    ? $"{prettyId}  [{prettyType}]"
                    : prettyId;
            bool selected = string.Equals(_selectedCosmeticForPurchase, cosmeticId, StringComparison.OrdinalIgnoreCase);
            if (selected)
                SelectCosmeticForInspection(cosmeticId, label, prettyType);
            cards.Add((cosmeticId, label, prettyType, iconTex, owned, equipped, selected));
        }

        if (cards.Count == 0) return yCursor + 18f;

        const int columns = 3;
        const float pad = 4f;
        const float cardH = 154f;
        const float rowGap = 8f;
        const float colGap = 8f;

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            BuildCosmeticCard(content, card.cosmeticId, card.label, card.typeLabel,
                card.iconTex, price, card.owned, card.equipped, card.selected, bxp,
                i, columns, yCursor, cardH, pad, rowGap, colGap);
        }

        int rowCount = Mathf.CeilToInt(cards.Count / (float)columns);
        return yCursor + rowCount * cardH + Mathf.Max(0, rowCount - 1) * rowGap;
    }

    private static void BuildCosmeticCard(
        Transform content,
        string cosmeticId,
        string label,
        string typeLabel,
        Texture2D iconTex,
        int price,
        bool owned,
        bool equipped,
        bool selected,
        int bxp,
        int index,
        int columns,
        float yStart,
        float cardH,
        float pad,
        float rowGap,
        float colGap)
    {
        int col = index % Mathf.Max(1, columns);
        int rowIndex = index / Mathf.Max(1, columns);
        float x0 = col / (float)columns;
        float x1 = (col + 1) / (float)columns;
        float yTop = yStart + rowIndex * (cardH + rowGap);
        float leftPad = col == 0 ? pad : colGap * 0.5f;
        float rightPad = col == columns - 1 ? pad : colGap * 0.5f;

        bool canAfford = owned || bxp >= price;
        Color normalCol;
        Color hoverCol;
        Color labelColor;
        Color statusColor;
        string statusText;

        if (selected && !owned)
        {
            Color c = bxp >= price ? Palette.Sale : Palette.Warning;
            float tint = bxp >= price ? 0.16f : 0.075f;
            normalCol = new Color(c.r * tint, c.g * tint, c.b * tint, 0.76f);
            hoverCol = new Color(c.r * (tint + 0.08f), c.g * (tint + 0.08f), c.b * (tint + 0.08f), 0.84f);
            labelColor = bxp >= price ? Palette.Primary : Palette.Dim;
            statusColor = bxp >= price ? Palette.Sale : Palette.Warning;
            statusText = FormatMarks(price);
        }
        else if (owned)
        {
            Color g = Palette.Gold;
            float tint = equipped ? 0.22f : 0.13f;
            float alpha = equipped ? 0.78f : 0.62f;
            normalCol = new Color(g.r * tint, g.g * tint, g.b * tint, alpha);
            hoverCol = new Color(g.r * (tint + 0.08f), g.g * (tint + 0.08f), g.b * (tint + 0.08f),
                Mathf.Min(1f, alpha + 0.08f));
            labelColor = Palette.Primary;
            statusColor = Palette.Gold;
            statusText = equipped ? "EQUIPPED" : "OWNED";
        }
        else
        {
            Color c = canAfford ? Palette.Sale : Palette.Warning;
            float tint = canAfford ? 0.10f : 0.055f;
            normalCol = new Color(c.r * tint, c.g * tint, c.b * tint, canAfford ? 0.68f : 0.52f);
            hoverCol = new Color(c.r * (tint + 0.08f), c.g * (tint + 0.08f), c.b * (tint + 0.08f), 0.78f);
            labelColor = canAfford ? Palette.Primary : Palette.Dim;
            statusColor = canAfford ? Palette.Sale : Palette.Warning;
            statusText = FormatMarks(price);
        }

        var card = new GameObject("CosCard_" + cosmeticId);
        card.transform.SetParent(content, false);
        var cardImg = card.AddComponent<Image>();
        cardImg.color = normalCol;
        var rt = cardImg.rectTransform;
        rt.anchorMin = new Vector2(x0, 1);
        rt.anchorMax = new Vector2(x1, 1);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(leftPad, -(yTop + cardH));
        rt.offsetMax = new Vector2(-rightPad, -yTop);
        UI.AddPanelTexture(card.transform, TexFaint, cosmeticId.GetHashCode());
        AddBorder(card.transform, 2f, 0f,
            equipped || selected
                ? Palette.Gold
                : new Color(Palette.Accent.r, Palette.Accent.g, Palette.Accent.b, 0.42f));

        var btn = card.AddComponent<Button>();
        var cb = btn.colors;
        cb.normalColor = normalCol;
        cb.highlightedColor = hoverCol;
        cb.pressedColor = Palette.BtnActive * 0.5f;
        cb.selectedColor = selected ? hoverCol : normalCol;
        cb.disabledColor = normalCol;
        cb.fadeDuration = 0.06f;
        btn.colors = cb;
        btn.targetGraphic = cardImg;

        var iconPane = UI.MakePanel("IconPane", card.transform,
            new Color(0f, 0f, 0f, 0.36f),
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(12, -92), new Vector2(-12, -12));
        iconPane.GetComponent<Image>().raycastTarget = false;

        if (iconTex != null)
        {
            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(iconPane.transform, false);
            var raw = iconGo.AddComponent<RawImage>();
            raw.texture = iconTex;
            raw.color = Color.white;
            raw.raycastTarget = false;
            var iconRt = raw.rectTransform;
            iconRt.anchorMin = new Vector2(0.5f, 0.5f);
            iconRt.anchorMax = new Vector2(0.5f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.sizeDelta = new Vector2(70f, 70f);
            iconRt.anchoredPosition = Vector2.zero;
            var fitter = iconGo.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = iconTex.width > 0 && iconTex.height > 0
                ? (float)iconTex.width / iconTex.height
                : 1f;
        }
        else
        {
            var noIcon = UI.MakeText("NoIcon", iconPane.transform,
                "NO ICON", FontXs, Palette.Dim, TextAlignmentOptions.Center);
            noIcon.raycastTarget = false;
        }

        var nameText = UI.MakeText("Name", card.transform,
            label, FontSm, labelColor, TextAlignmentOptions.Center);
        var nrt = nameText.GetComponent<RectTransform>();
        nrt.anchorMin = new Vector2(0, 0);
        nrt.anchorMax = new Vector2(1, 0);
        nrt.offsetMin = new Vector2(8, 28);
        nrt.offsetMax = new Vector2(-8, 56);
        nameText.fontStyle = FontStyles.Bold;
        nameText.enableAutoSizing = true;
        nameText.fontSizeMin = FontMicro;
        nameText.fontSizeMax = FontSm;
        nameText.enableWordWrapping = true;
        nameText.overflowMode = TextOverflowModes.Ellipsis;

        var status = UI.MakeText("Status", card.transform,
            statusText, FontXs, statusColor, TextAlignmentOptions.Center);
        var srt = status.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0, 0);
        srt.anchorMax = new Vector2(1, 0);
        srt.offsetMin = new Vector2(8, 6);
        srt.offsetMax = new Vector2(-8, 24);
        status.enableWordWrapping = false;
        status.overflowMode = TextOverflowModes.Ellipsis;

        string capturedId = cosmeticId;
        string capturedLabel = label;
        string capturedType = typeLabel;
        btn.onClick.AddListener(() =>
        {
            SelectCosmeticForInspection(capturedId, capturedLabel, capturedType);

            if (owned)
            {
                MenuAudio.PlayClick();
                ToggleMoreCompanyCosmeticSelection(capturedId);
                RefreshPlayerLevelAfterCosmeticChange();
                return;
            }

            MenuAudio.PlayClick();
            showPlayerLevel();
        });

        // Real grid cells: W/S changes card row, A/D moves along one row.
        RegisterFocusTarget("cosmetic:" + cosmeticId, rt, btn, rowIndex, col, _cosScrollRect);
    }

    /// <summary>Returns the header's Button so the keyboard layer can focus it.</summary>
    private static Button BuildEmoteTierHeader(
        Transform content, int rarity, string tierName, int itemCount, bool expanded,
        float yTop, float pad)
    {
        const float headerH = 38f;
        Color tierColor = GetTierColorForRarity(rarity);
        Color bg = expanded
            ? new Color(Palette.BgHeader.r, Palette.BgHeader.g, Palette.BgHeader.b, 0.88f)
            : new Color(Palette.BgRow.r, Palette.BgRow.g, Palette.BgRow.b, 0.68f);
        Color hover = new Color(Palette.BgRowHover.r, Palette.BgRowHover.g, Palette.BgRowHover.b, 0.82f);

        var row = new GameObject("EmoteTier_" + tierName);
        row.transform.SetParent(content, false);
        var img = row.AddComponent<Image>();
        img.color = bg;
        var rt = img.rectTransform;
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(pad, -(yTop + headerH));
        rt.offsetMax = new Vector2(-pad, -yTop);

        var btn = row.AddComponent<Button>();
        var cb = btn.colors;
        cb.normalColor = bg;
        cb.highlightedColor = hover;
        cb.pressedColor = Palette.BtnActive * 0.55f;
        cb.selectedColor = bg;
        cb.disabledColor = bg;
        cb.fadeDuration = 0.06f;
        btn.colors = cb;
        btn.targetGraphic = img;

        UI.MakePanel("TierAccent", row.transform,
            new Color(tierColor.r, tierColor.g, tierColor.b, expanded ? 0.86f : 0.52f),
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(0, 5), new Vector2(6, -5))
            .GetComponent<Image>().raycastTarget = false;

        MakeAccentLine(row.transform, "TierDepth",
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(14, 1), new Vector2(-12, 5),
            new Color(0f, 0f, 0f, 0.28f));

        string marker = expanded ? "[-]" : "[+]";
        var label = UI.MakeText("TierLabel", row.transform,
            marker + "  " + tierName.ToUpperInvariant(), FontMd, tierColor,
            TextAlignmentOptions.MidlineLeft);
        var lrt = label.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0, 0);
        lrt.anchorMax = new Vector2(0.72f, 1);
        lrt.offsetMin = new Vector2(18, 0);
        lrt.offsetMax = new Vector2(-4, 0);
        label.fontStyle = FontStyles.Bold;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;

        var count = UI.MakeText("TierCount", row.transform,
            itemCount + " FILES", FontSm, Palette.Dim,
            TextAlignmentOptions.MidlineRight);
        var crt = count.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0.72f, 0);
        crt.anchorMax = new Vector2(1, 1);
        crt.offsetMin = new Vector2(4, 0);
        crt.offsetMax = new Vector2(-12, 0);

        btn.onClick.AddListener(() =>
        {
            MenuAudio.PlayClick();
            if (expanded)
                _expandedEmoteTiers.Remove(rarity);
            else
                _expandedEmoteTiers.Add(rarity);
            _activeEmoteTier = rarity;
            showPlayerLevel(resetScroll: false);
        });

        return btn;
    }

    // - Player level: emotes section (TooManyEmotes, optional) -
    // Bridges Tokens to TooManyEmotes' built-in UnlockEmoteLocal.
    // Tiers render as closed accordions by default; opening a tier draws
    // its emote rows directly below the tier header in the same panel.
    private static float BuildEmotesSection(Transform content, float yCursor)
    {
        if (!_emotesAvailable) return yCursor;
        EnsureEmoteTiersDiscovered();
        var allEmotes = GetAllPurchasableEmotes();
        if (allEmotes == null) return yCursor;

        if (allEmotes.Count == 0)
        {
            UI.MakeText("NoEmotes", content,
                "No emotes available.",
                FontSm, Palette.Dim, TextAlignmentOptions.MidlineLeft)
                .GetComponent<RectTransform>().offsetMin = new Vector2(14, -(yCursor + 18f));
            return yCursor + 18f;
        }

        int bxp   = SafeCurrency();
        const float pad = 4f;
        const float spacing = 3f;
        const float headerH = 38f;
        const float tierGap = 6f;
        // One flat keyboard column: tier headers and the rows they reveal share
        // it, so W/S walks the accordion exactly as it reads.
        int focusRow = 0;

        var tierOrder = _discoveredTiers != null && _discoveredTiers.Count > 0
            ? _discoveredTiers
            : allEmotes.Select(e => (e.rarity, e.rarityName)).Distinct().OrderBy(e => e.rarity).ToList();

        for (int t = 0; t < tierOrder.Count; t++)
        {
            var tier = tierOrder[t];
            var tierEmotes = allEmotes
                .Where(e => e.rarity == tier.rarity)
                .OrderBy(e => Humanize(e.displayName), StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool expanded = _expandedEmoteTiers.Contains(tier.rarity);

            Button tierBtn = BuildEmoteTierHeader(
                content, tier.rarity, tier.Item2, tierEmotes.Count, expanded, yCursor, pad);
            RegisterFocusTarget("emoteTier:" + tier.rarity,
                tierBtn != null ? tierBtn.GetComponent<RectTransform>() : null,
                tierBtn, focusRow, 0, _cosScrollRect);
            focusRow++;
            yCursor += headerH + spacing;

            if (!expanded)
            {
                yCursor += tierGap;
                continue;
            }

            for (int idx = 0; idx < tierEmotes.Count; idx++)
            {
                object emoteObj = tierEmotes[idx].obj;
                string label    = Humanize(tierEmotes[idx].displayName);
                string nodeName = tierEmotes[idx].emoteName;
                int    price    = GetEmotePriceForRarity(tierEmotes[idx].rarity);
                Color  tierCol  = tierEmotes[idx].rarityColor;
                bool   owned    = TmeIsEmoteUnlocked(emoteObj);
                bool   selected = _selectedEmoteForPurchase != null
                                  && ReferenceEquals(_selectedEmoteForPurchase, emoteObj);
                string ownedRight = "[OWNED]";

                Action onClick = () =>
                {
                    MenuAudio.PlayClick();
                    _selectedEmoteForPurchase = emoteObj;
                    _previewingEmote          = emoteObj;
                    _activeEmoteTier          = tier.rarity;
                    LogClickDiag(owned ? "OWNED-CLICK" : "SELECT-CLICK", nodeName, emoteObj);
                    PlayEmotePreview(emoteObj);
                    LogPostPlayDiag(owned ? "OWNED-CLICK-POST" : "SELECT-CLICK-POST");
                    showPlayerLevel();
                };

                var (rowBtn, rowLbl) = BuildOwnableRow(
                    content, "Emote_" + nodeName, label,
                    ownedRight,
                    price, owned, /*highlightOwned*/ false, bxp,
                    yCursor, pad + 12f,
                    onActivateOwned: onClick,
                    onPurchase:      onClick,
                    selected:        selected);

                if (rowLbl != null && !owned)
                    rowLbl.color = tierCol;

                RegisterFocusTarget("emote:" + nodeName,
                    rowBtn != null ? rowBtn.GetComponent<RectTransform>() : null,
                    rowBtn, focusRow, 0, _cosScrollRect);
                focusRow++;
                yCursor += RowH + spacing;
            }
            yCursor += tierGap;
        }
        return yCursor;
    }

    private static void PurchaseSelectedCosmetic()
    {
        string cosmeticId = _selectedCosmeticForPurchase;
        if (string.IsNullOrEmpty(cosmeticId)) { MenuAudio.PlayDeny(); return; }

        var data = PlayerLevelStore.Get();
        data.cosmetics ??= new List<string>();
        if (data.cosmetics.Contains(cosmeticId)) { MenuAudio.PlayDeny(); return; }

        int price = Mathf.Max(0, Plugin.CosmeticPrice.Value);
        if (!TryDeductCurrency(price)) { MenuAudio.PlayDeny(); return; }

        MenuAudio.PlayPurchaseRandom();
        data.cosmetics.Add(cosmeticId);
        PlayerLevelStore.Save();
        showPlayerLevel();
    }

    // - Purchase the currently-selected emote -
    // Routed from the Purchase button under the preview rig. Selection
    // is preserved across the buy so the now-owned row stays highlighted
    // and the button refreshes to disabled (since selOwned will be true
    // on the next showPlayerLevel rebuild).
    private static void PurchaseSelectedEmote()
    {
        object emoteObj = _selectedEmoteForPurchase;
        if (emoteObj == null) { MenuAudio.PlayDeny(); return; }
        if (TmeIsEmoteUnlocked(emoteObj)) { MenuAudio.PlayDeny(); return; }

        int price = 0;
        var all = GetAllPurchasableEmotes();
        if (all != null)
        {
            foreach (var e in all)
            {
                if (ReferenceEquals(e.obj, emoteObj))
                {
                    price = GetEmotePriceForRarity(e.rarity);
                    break;
                }
            }
        }
        if (!TryDeductCurrency(price)) { MenuAudio.PlayDeny(); return; }
        MenuAudio.PlayPurchaseRandom();
        TmeUnlockEmoteLocal(emoteObj);
        // Keep _selectedEmoteForPurchase pointing at the now-owned emote
        // so the row stays highlighted; the Purchase button re-renders
        // disabled because TmeIsEmoteUnlocked is now true.
        showPlayerLevel();
    }

    // - Currency helpers (single source of truth for BXP spend) -
    private static int SafeCurrency()
    {
        try { return CurrencyManager.Instance != null ? CurrencyManager.Instance.CurrencyAmount : 0; }
        catch { return 0; }
    }

    private static bool TryDeductCurrency(int amount)
    {
        if (amount <= 0) return true;
        try
        {
            if (CurrencyManager.Instance == null) return false;
            if (!CurrencyManager.Instance.TrySpend(amount)) return false;
            RefreshCurrencyDisplay();
            return true;
        }
        catch { return false; }
    }

    // - Ownable row factory -
    // Builds a full-width row in the ENHANCEMENTS/[MAX] style - name on
    // the left, status badge on the right, background tint shifts based
    // on owned/affordable state. Mirrors the row geometry from
    // showUpgrades() so the visual language is consistent.
    //
    // highlightOwned: lifts the owned tint to a brighter "active" variant.
    //                 Used for currently-equipped cosmetics.
    // selected:       marks the row with a left-hand terminal selector.
    //                 Selection is independent of ownership; tier-colored
    //                 name text stays untouched.
    private static (Button, TMP_Text) BuildOwnableRow(
        Transform parent, string name, string nameLabel, string ownedRight,
        int price, bool owned, bool highlightOwned, int bxp,
        float yTop, float pad,
        Action onActivateOwned, Action onPurchase,
        bool selected = false)
    {
        var row = new GameObject(name);
        row.transform.SetParent(parent, false);
        var rowImg = row.AddComponent<Image>();
        var rowRt  = rowImg.rectTransform;
        rowRt.anchorMin = new Vector2(0, 1);
        rowRt.anchorMax = new Vector2(1, 1);
        rowRt.pivot     = new Vector2(0.5f, 1f);
        rowRt.offsetMin = new Vector2(pad, -(yTop + RowH));
        rowRt.offsetMax = new Vector2(-pad, -yTop);

        bool canAfford = owned || bxp >= price;

        // Background fill - same idiom as the ENHANCEMENTS [MAX] row:
        //   maxed/owned    - gold tinted, partial alpha
        //   highlightOwned - brighter gold (used for equipped cosmetic /
        //                    selected emote)
        //   for-sale       - cyan tint at low intensity (mirrors the
        //                    "purchasable" affordance), red-ish if broke
        Color normalCol, hoverCol, textColor, rightColor;
        string rightStr;

        if (owned)
        {
            Color g = Palette.Gold;
            float baseTint  = highlightOwned ? 0.22f : 0.14f;
            float baseAlpha = highlightOwned ? 0.78f : 0.66f;
            normalCol = new Color(g.r * baseTint, g.g * baseTint, g.b * baseTint, baseAlpha);
            hoverCol  = new Color(g.r * (baseTint + 0.10f), g.g * (baseTint + 0.10f), g.b * (baseTint + 0.10f),
                                  Mathf.Min(1f, baseAlpha + 0.05f));
            textColor  = Palette.Primary;
            rightColor = Palette.Gold;
            rightStr   = ownedRight;
        }
        else
        {
            Color c = Palette.Sale;
            float baseTint  = canAfford ? 0.12f : 0.055f;
            float baseAlpha = canAfford ? 0.70f : 0.54f;
            normalCol = new Color(c.r * baseTint, c.g * baseTint, c.b * baseTint, baseAlpha);
            hoverCol  = new Color(c.r * (baseTint + 0.10f), c.g * (baseTint + 0.10f), c.b * (baseTint + 0.10f),
                                  Mathf.Min(1f, baseAlpha + 0.10f));
            textColor  = canAfford ? Palette.Primary : Palette.Dim;
            rightColor = canAfford ? Palette.Sale  : Palette.Warning;
            rightStr   = FormatMarks(price);
        }

        var rowBtn = row.AddComponent<Button>();
        var rowCb  = rowBtn.colors;
        rowCb.normalColor      = normalCol;
        rowCb.highlightedColor = hoverCol;
        rowCb.pressedColor     = Palette.BtnActive * 0.5f;
        rowCb.selectedColor    = selected ? hoverCol : normalCol;
        rowCb.disabledColor    = normalCol;
        rowCb.fadeDuration     = 0.06f;
        rowBtn.colors          = rowCb;
        rowBtn.targetGraphic   = rowImg;
        rowImg.color           = normalCol;
        rowImg.CrossFadeColor(normalCol, 0f, true, true);

        MakeAccentLine(row.transform, "OwnableRowDepth",
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(10, 1), new Vector2(-10, 4),
            new Color(0f, 0f, 0f, selected ? 0.48f : 0.24f));

        if (selected)
        {
            var rail = UI.MakePanel("OwnableSelectedRail", row.transform,
                new Color(Palette.Accent.r, Palette.Accent.g, Palette.Accent.b, 0.82f),
                new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(0, 5), new Vector2(5, -5));
            rail.GetComponent<Image>().raycastTarget = false;
        }

        // Name label - left 65%
        var nameTxt = UI.MakeText("Name", row.transform, nameLabel,
            FontMd, textColor, TextAlignmentOptions.MidlineLeft);
        var nrt = nameTxt.GetComponent<RectTransform>();
        nrt.anchorMin = new Vector2(0, 0);
        nrt.anchorMax = new Vector2(0.65f, 1);
        nrt.offsetMin = new Vector2(10, 0);
        nrt.offsetMax = new Vector2(-2, 0);
        nameTxt.enableWordWrapping = false;
        nameTxt.overflowMode       = TextOverflowModes.Ellipsis;
        nameTxt.fontStyle          = FontStyles.Bold;
        nameTxt.enableAutoSizing   = true;
        nameTxt.fontSizeMin        = FontSm;
        nameTxt.fontSizeMax        = FontMd;

        // Status / price badge - right 35%
        var rightTxt = UI.MakeText("Right", row.transform, rightStr,
            FontSm, rightColor, TextAlignmentOptions.MidlineRight);
        var rrt = rightTxt.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.65f, 0);
        rrt.anchorMax = new Vector2(1,     1);
        rrt.offsetMin = new Vector2(2,  0);
        rrt.offsetMax = new Vector2(-10, 0);
        rightTxt.enableWordWrapping = false;
        rightTxt.overflowMode       = TextOverflowModes.Ellipsis;

        rowBtn.onClick.AddListener(() =>
        {
            if (owned) onActivateOwned?.Invoke();
            else        onPurchase?.Invoke();
        });

        return (rowBtn, nameTxt);
    }

    // - TooManyEmotes preview bridge -
    // The right-side panel always shows our own persistent employee rig -
    // TME's render texture is never displayed there. We still call
    // `AnimationPreviewer.SetPreviewAnimation` when an emote row is clicked
    // so TME's own rig stays in sync and our audio bridge gets fed.
    //
    // If reflection misses the TME types or fields, we soft-fail:
    // PlayEmotePreview becomes a no-op - emote rows still purchase.
    private static MethodInfo _tmeSetPreviewAnim;    // AnimationPreviewer.SetPreviewAnimation(UnlockableEmote)
    private static FieldInfo  _tmePreviewerEnabled;  // AnimationPreviewer.enabled
    private static FieldInfo  _tmeSimpleController;   // AnimationPreviewer.simpleEmoteController
    private static FieldInfo  _tmeSimpleAnimator;     // simpleEmoteController.animator
    private static object     _tmeLastSimpleController;
    private static Animator   _tmePreviewAnimator;
    private static bool       _tmePreviewResolved;
    private static bool       _tmePreviewWarned;

    // Audio bridge - TooManyEmotes' own preview rig is built with a
    // base-class EmoteController (isSimpleEmoteController == true), so it
    // never instantiates a PersonalEmoteAudioSource and never plays preview
    // audio. We sidestep that entirely: pull the AudioClip via
    // AudioManager.LoadAudioClip(audioClipName) and play it through our own
    // AudioSource sitting on the Y4NGZ Menu canvas. 2D blend so the listener
    // position doesn't matter.
    private static MethodInfo  _tmeAudioExists;       // AudioManager.AudioExists(string)
    private static MethodInfo  _tmeLoadAudioClip;     // AudioManager.LoadAudioClip(string)
    private static PropertyInfo _tmeAudioClipName;    // UnlockableEmote.audioClipName
    private static PropertyInfo _tmeAudioLoopName;    // UnlockableEmote.audioLoopClipName
    private static FieldInfo   _tmeTransitionsToClip; // UnlockableEmote.transitionsToClip
    private static MethodInfo  _tmeEmoteLoadAudioClip;     // UnlockableEmote.LoadAudioClip()
    private static MethodInfo  _tmeEmoteLoadAudioLoopClip; // UnlockableEmote.LoadAudioLoopClip()
    private static AudioSource _previewAudioSource;
    private static AudioSource _previewAudioLoopSource;
    private static GameObject  _previewAudioHolder;
    private static bool        _audioDiagDumped;

    private static void EnsurePreviewReflection()
    {
        if (_tmePreviewResolved) return;
        _tmePreviewResolved = true;
        try
        {
            Assembly asm = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "TooManyEmotes") { asm = a; break; }
            if (asm == null) return;

            const BindingFlags PUB_STATIC = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            var tPreviewer  = asm.GetType("TooManyEmotes.UI.AnimationPreviewer")
                           ?? asm.GetType("TooManyEmotes.AnimationPreviewer");

            if (tPreviewer != null)
            {
                _tmePreviewerEnabled = tPreviewer.GetField("enabled", PUB_STATIC);
                _tmeSimpleController = tPreviewer.GetField("simpleEmoteController", PUB_STATIC);
                foreach (var m in tPreviewer.GetMethods(PUB_STATIC))
                {
                    if (m.Name != "SetPreviewAnimation") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1) { _tmeSetPreviewAnim = m; break; }
                }
            }

            var tAudioMgr = asm.GetType("TooManyEmotes.AudioManager");
            if (tAudioMgr != null)
            {
                _tmeAudioExists   = tAudioMgr.GetMethod("AudioExists",   PUB_STATIC, null, new[] { typeof(string) }, null);
                _tmeLoadAudioClip = tAudioMgr.GetMethod("LoadAudioClip", PUB_STATIC, null, new[] { typeof(string) }, null);
            }

            var tEmote = asm.GetType("TooManyEmotes.UnlockableEmote");
            if (tEmote != null)
            {
                const BindingFlags PUB_INST = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _tmeAudioClipName     = tEmote.GetProperty("audioClipName",     PUB_INST);
                _tmeAudioLoopName     = tEmote.GetProperty("audioLoopClipName", PUB_INST);
                _tmeTransitionsToClip = tEmote.GetField   ("transitionsToClip", PUB_INST);
                // Bug 2: prefer UnlockableEmote.LoadAudioClip()/LoadAudioLoopClip()
                // - these are exactly what TooManyEmotes' own EmoteController
                // calls (SetAudioFromEmote, line 10201 in decompile). Going
                // through this entry point sidesteps name-resolution edge
                // cases (AudioManager.AudioExists checks, override names).
                _tmeEmoteLoadAudioClip     = tEmote.GetMethod("LoadAudioClip",     PUB_INST, null, Type.EmptyTypes, null);
                _tmeEmoteLoadAudioLoopClip = tEmote.GetMethod("LoadAudioLoopClip", PUB_INST, null, Type.EmptyTypes, null);
            }
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu] preview reflection failed: {e.Message}");
        }
    }

    private static Animator TryGetTmePreviewAnimator()
    {
        EnsurePreviewReflection();
        try
        {
            object controller = _tmeSimpleController?.GetValue(null);
            if (controller == null)
                return null;

            if (!ReferenceEquals(controller, _tmeLastSimpleController))
            {
                _tmeLastSimpleController = controller;
                _tmePreviewAnimator = null;
                _tmeSimpleAnimator = controller.GetType().GetField(
                    "animator",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_tmePreviewAnimator == null && _tmeSimpleAnimator != null)
                _tmePreviewAnimator = _tmeSimpleAnimator.GetValue(controller) as Animator;

            return _tmePreviewAnimator;
        }
        catch
        {
            return null;
        }
    }

    // - DIAG helpers (Bug 3 / Bug 5) -
    private static void LogClickDiag(string tag, string nodeName, object emoteObj)
    {
        if (!EmotePreviewDiagnostics) return;
        try
        {
            bool clipPresent = false; string audioName = "";
            var t = emoteObj?.GetType();
            if (t != null)
            {
                const BindingFlags PUB_INST = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var fAnim = t.GetField("animationClip", PUB_INST)
                         ?? t.GetField("transitionsToClip", PUB_INST);
                if (fAnim != null) clipPresent = fAnim.GetValue(emoteObj) != null;
                var pAud  = t.GetProperty("audioClipName", PUB_INST);
                if (pAud != null) audioName = pAud.GetValue(emoteObj) as string ?? "";
            }
            Plugin.CustomLogger?.LogInfo(
                $"[Y4NGZMenu][DIAG][{tag}] node={nodeName} animClipPresent={clipPresent} " +
                $"audioName='{audioName}' frame={Time.frameCount}");
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu][DIAG][{tag}] exception: {e.Message}");
        }
    }

    private static void LogPostPlayDiag(string tag)
    {
        if (!EmotePreviewDiagnostics) return;
        try
        {
            int stateHash = 0; float normTime = 0f; string clipName = ""; bool audioPlaying = false;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "TooManyEmotes");
                if (asm != null)
                {
                    var tPrev = asm.GetType("TooManyEmotes.UI.AnimationPreviewer")
                             ?? asm.GetType("TooManyEmotes.AnimationPreviewer");
                    var fSec  = tPrev?.GetField("simpleEmoteController",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var sec   = fSec?.GetValue(null);
                    if (sec != null)
                    {
                        var fAnim = sec.GetType().GetField("animator",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var animator = fAnim?.GetValue(sec) as Animator;
                        if (animator != null)
                        {
                            var info = animator.GetCurrentAnimatorStateInfo(0);
                            stateHash = info.fullPathHash;
                            normTime  = info.normalizedTime;
                            var clips = animator.GetCurrentAnimatorClipInfo(0);
                            if (clips != null && clips.Length > 0 && clips[0].clip != null)
                                clipName = clips[0].clip.name;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu][DIAG][{tag}] reflect failed: {ex.Message}");
            }
            if (_previewAudioSource != null) audioPlaying = _previewAudioSource.isPlaying;
            Plugin.CustomLogger?.LogInfo(
                $"[Y4NGZMenu][DIAG][{tag}] stateHash={stateHash} normTime={normTime:0.000} " +
                $"clipName={clipName} audioPlaying={audioPlaying}");
        }
        catch { }
    }

    private static void PlayEmotePreview(object emote)
    {
        EnsurePreviewReflection();
        if (_tmeSetPreviewAnim == null || emote == null)
        {
            if (!_tmePreviewWarned)
            {
                _tmePreviewWarned = true;
                Plugin.CustomLogger?.LogWarning(
                    "[Y4NGZMenu] TooManyEmotes preview rig unavailable - falling back to no-preview purchase.");
            }
            return;
        }
        try
        {
            // Force the previewer enabled - TooManyEmotes flips this off
            // when their own EmoteMenu closes. Re-enable for our use.
            if (_tmePreviewerEnabled != null) _tmePreviewerEnabled.SetValue(null, true);
            _tmeSetPreviewAnim.Invoke(null, new object[] { emote });
            _previewingEmote = emote;
            PlayEmoteAudio(emote);
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu] PlayEmotePreview failed: {e.Message}");
        }
    }

    private static void StopEmotePreview()
    {
        EnsurePreviewReflection();
        StopEmoteAudio();
        _previewingEmote = null;
        ClearPreviewEmotePoseSource();
        if (_tmeSetPreviewAnim == null) return;
        try { _tmeSetPreviewAnim.Invoke(null, new object[] { null }); } catch { }
    }

    private static void ClearPreviewEmotePoseSource()
    {
        _previewEmoteAnimatorSource = null;
        _previewEmoteSourceSpine = null;
        _previewEmoteBonePairs.Clear();
    }

    private static void EnsurePreviewAudioSource()
    {
        if (_previewAudioSource != null && _previewAudioLoopSource != null) return;
        var parent = _root != null ? _root.transform : null;
        if (parent == null) return;
        if (_previewAudioHolder == null)
        {
            _previewAudioHolder = new GameObject("Y4NGZ_EmotePreviewAudio");
            _previewAudioHolder.transform.SetParent(parent, false);
        }
        if (_previewAudioSource == null)
        {
            _previewAudioSource = _previewAudioHolder.AddComponent<AudioSource>();
            _previewAudioSource.spatialBlend = 0f;
            _previewAudioSource.loop         = false;
            _previewAudioSource.playOnAwake  = false;
            _previewAudioSource.volume       = 1f;
        }
        if (_previewAudioLoopSource == null)
        {
            _previewAudioLoopSource = _previewAudioHolder.AddComponent<AudioSource>();
            _previewAudioLoopSource.spatialBlend = 0f;
            _previewAudioLoopSource.loop         = true;
            _previewAudioLoopSource.playOnAwake  = false;
            _previewAudioLoopSource.volume       = 1f;
        }
    }

    private static void PlayEmoteAudio(object emote)
    {
        StopEmoteAudio();
        if (emote == null) return;
        EnsurePreviewAudioSource();
        if (_previewAudioSource == null) return;
        try
        {
            // Prefer UnlockableEmote's own loaders - they apply override
            // names + AudioManager lookups in one shot, exactly mirroring
            // what TooManyEmotes' EmoteController does on the main rig.
            AudioClip mainClip = null;
            AudioClip loopClip = null;
            if (_tmeEmoteLoadAudioClip != null)
                mainClip = _tmeEmoteLoadAudioClip.Invoke(emote, null) as AudioClip;
            if (_tmeEmoteLoadAudioLoopClip != null)
                loopClip = _tmeEmoteLoadAudioLoopClip.Invoke(emote, null) as AudioClip;

            // Fallback: resolve via name properties + AudioManager.LoadAudioClip
            if (mainClip == null && _tmeAudioClipName != null && _tmeLoadAudioClip != null)
            {
                string mainName = _tmeAudioClipName.GetValue(emote, null) as string;
                if (!string.IsNullOrEmpty(mainName))
                    mainClip = _tmeLoadAudioClip.Invoke(null, new object[] { mainName }) as AudioClip;
            }
            if (loopClip == null && _tmeAudioLoopName != null && _tmeLoadAudioClip != null)
            {
                string loopName = _tmeAudioLoopName.GetValue(emote, null) as string;
                if (!string.IsNullOrEmpty(loopName))
                    loopClip = _tmeLoadAudioClip.Invoke(null, new object[] { loopName }) as AudioClip;
            }

            if (!_audioDiagDumped)
            {
                _audioDiagDumped = true;
                string emoteLabel = "?";
                try { emoteLabel = TooManyEmotesBridge.GetEmoteName(emote); } catch { }
                Plugin.CustomLogger?.LogInfo(
                    $"[Y4NGZMenu] preview audio diag: emote='{emoteLabel}' mainClip="
                    + (mainClip != null ? mainClip.name : "<null>")
                    + " loopClip=" + (loopClip != null ? loopClip.name : "<null>")
                    + $" src.volume={_previewAudioSource.volume} mute={_previewAudioSource.mute}"
                    + $" mixer={(_previewAudioSource.outputAudioMixerGroup != null ? _previewAudioSource.outputAudioMixerGroup.name : "<default>")}"
                    + $" spatial={_previewAudioSource.spatialBlend}"
                    + $" loadByObj={(_tmeEmoteLoadAudioClip != null ? "yes" : "no")}");
            }

            if (mainClip != null)
            {
                _previewAudioSource.clip = mainClip;
                _previewAudioSource.time = 0f;
                _previewAudioSource.Play();
            }
            if (loopClip != null)
            {
                _previewAudioLoopSource.clip = loopClip;
                _previewAudioLoopSource.time = 0f;
                if (mainClip != null)
                {
                    _previewAudioLoopSource.PlayScheduled(
                        AudioSettings.dspTime + (double)mainClip.length);
                }
                else
                {
                    _previewAudioLoopSource.Play();
                }
            }
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogWarning($"[Y4NGZMenu] PlayEmoteAudio failed: {e.Message}");
        }
    }

    private static void StopEmoteAudio()
    {
        try
        {
            if (_previewAudioSource     != null) _previewAudioSource.Stop();
            if (_previewAudioLoopSource != null) _previewAudioLoopSource.Stop();
        }
        catch { }
    }

    // - Suit switching (reflection - defensive across game versions) -
    private static bool        _suitReflResolved;
    private static MethodInfo  _switchSuitMethod;  // UnlockableSuit.SwitchSuitForPlayer

    private static void ResolveSuitReflection()
    {
        if (_suitReflResolved) return;
        _suitReflResolved = true;
        try
        {
            var t = AccessTools.TypeByName("UnlockableSuit");
            if (t != null)
            {
                _switchSuitMethod = t.GetMethod("SwitchSuitForPlayer",
                    BindingFlags.Public | BindingFlags.Static);
            }
        }
        catch { }
    }

    private static void TrySwitchSuit(int unlockableId)
    {
        var player = StartOfRound.Instance?.localPlayerController;
        if (player == null) return;

        ResolveSuitReflection();
        try
        {
            if (_switchSuitMethod != null)
            {
                var pars = _switchSuitMethod.GetParameters();
                // Common signatures: (PlayerControllerB, int) or (PlayerControllerB, int, bool)
                if (pars.Length == 2)
                {
                    _switchSuitMethod.Invoke(null, new object[] { player, unlockableId });
                    InvalidateEmployeePreviewModel();
                }
                else if (pars.Length == 3)
                {
                    _switchSuitMethod.Invoke(null, new object[] { player, unlockableId, true });
                    InvalidateEmployeePreviewModel();
                }
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.ExtendedLogging($"SwitchSuitForPlayer failed: {e.Message}");
        }

        // Fallback: direct material assignment so the local player visibly changes.
        try
        {
            var sor = StartOfRound.Instance;
            if (sor?.unlockablesList?.unlockables != null
                && unlockableId >= 0 && unlockableId < sor.unlockablesList.unlockables.Count)
            {
                var u = sor.unlockablesList.unlockables[unlockableId];
                if (u?.suitMaterial != null && player.thisPlayerModel != null)
                {
                    player.thisPlayerModel.material = u.suitMaterial;
                    InvalidateEmployeePreviewModel();
                }
            }
        }
        catch { }
    }

    // - TooManyEmotes bridge -
    // We do NOT track emote unlocks separately. TooManyEmotes ships its own
    // shop, currency model, save key (TooManyEmotes.UnlockedEmotes), and
    // sync RPC. We just call into it: deduct BXP locally, then invoke
    // SessionManager.UnlockEmoteLocal(emote, purchased=true). TooManyEmotes'
    // own SaveGameValues postfix persists it on host save.
    private static bool _emotesAvailable => TooManyEmotesBridge.IsAvailable;

    private static void EnsureEmotesReflection()
    {
        TooManyEmotesBridge.EnsureResolved();
    }

    private static List<TooManyEmotesEntry> GetAllPurchasableEmotes()
    {
        return TooManyEmotesBridge.GetAllPurchasableEmotes();
    }

    private static Color GetTierColorForRarity(int rarity)
    {
        return TooManyEmotesBridge.GetTierColorForRarity(rarity);
    }

    // Build an ordered (rarity, name) list from whatever tiers TooManyEmotes
    // actually reports. Cached per session - the result is stable for a run
    // because the emote list is built once at game boot.
    private static void EnsureEmoteTiersDiscovered()
    {
        if (_discoveredTiers != null) return;
        var emotes = GetAllPurchasableEmotes();
        var seen = new Dictionary<int, string>();
        if (emotes != null)
        {
            foreach (var e in emotes)
                if (!seen.ContainsKey(e.rarity)) seen[e.rarity] = e.rarityName;
        }
        _discoveredTiers = seen.OrderBy(kv => kv.Key)
                               .Select(kv => (kv.Key, kv.Value))
                               .ToList();
        BindTierPriceConfigs();
    }

    // One readable entry per rarity in Player Menu - Emote Prices by Rarity. TooManyEmotes can
    // add or rename tiers, so these bind after its catalog is available rather than assuming four IDs.
    private static void BindTierPriceConfigs()
    {
        if (Plugin.ConfigScope == null) return;
        int n = _discoveredTiers.Count;
        for (int i = 0; i < n; i++)
        {
            var (rarity, name) = _discoveredTiers[i];
            int defaultPrice = GetDefaultEmoteTierPrice(i, n);
            int oldDefaultPrice = n == 1
                ? 9
                : Mathf.RoundToInt(Mathf.Lerp(3f, 15f, (float)i / (n - 1)));
            string oldKey = "EmotePrice." + name;
            string newKey = name + " Cost";
            if (_tierPriceConfigs.ContainsKey(rarity))
                continue;

            bool targetAlreadyDefined = Y4NGZConfigFiles.TargetAlreadyDefines(
                Plugin.ConfigScope,
                "Emote Prices by Rarity",
                newKey);
            bool hasTierSpecificLegacy = Y4NGZConfigFiles.TryGetLegacy(
                "Player Level - Prices",
                oldKey,
                out int _);
            ConfigEntry<int> price = Y4NGZConfigFiles.BindMigrated(
                Plugin.ConfigScope,
                "Emote Prices by Rarity",
                newKey,
                defaultPrice,
                new ConfigDescription(
                    $"Upgrade-token cost to unlock one {name}-rarity emote.",
                    new AcceptableValueRange<int>(0, 999)),
                new LegacyConfigKey("Player Level - Prices", oldKey));

            if (!targetAlreadyDefined && hasTierSpecificLegacy && price.Value == oldDefaultPrice)
            {
                price.Value = defaultPrice;
            }
            else if (!targetAlreadyDefined
                     && !hasTierSpecificLegacy
                     && Y4NGZConfigFiles.TryGetLegacy(
                         "Player Cosmetics - Prices",
                         "Emote Price",
                         out int legacyFlatPrice)
                     && legacyFlatPrice != 3
                     && legacyFlatPrice != 30)
            {
                price.Value = Mathf.Clamp(legacyFlatPrice, 0, 999);
            }

            _tierPriceConfigs[rarity] = price;
        }
    }

    private static int GetDefaultEmoteTierPrice(int index, int count)
    {
        if (count <= 1)
            return 1;

        return Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Lerp(1f, 3f, Mathf.Clamp01(index / (float)(count - 1)))),
            1,
            3);
    }

    // Display-only: turn an underscore-id into a readable label.
    // "fish_dance" - "Fish Dance".
    private static string Humanize(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var parts = id.Split('_');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            parts[i] = char.ToUpperInvariant(parts[i][0]) +
                       (parts[i].Length > 1 ? parts[i].Substring(1) : "");
        }
        return string.Join(" ", parts);
    }

    private static string GetCosmeticDisplayName(
        string cosmeticId,
        object cosmeticInstance,
        Texture2D iconTex,
        string typeStr,
        out bool fromTypeFallback)
    {
        fromTypeFallback = false;
        var candidates = new List<string>();

        if (cosmeticInstance is Component component)
        {
            if (!string.IsNullOrEmpty(component.gameObject.name))
                candidates.Add(component.gameObject.name);
            if (!string.IsNullOrEmpty(component.name))
                candidates.Add(component.name);
        }
        else if (cosmeticInstance is Object unityObject && !string.IsNullOrEmpty(unityObject.name))
        {
            candidates.Add(unityObject.name);
        }

        if (iconTex != null && !string.IsNullOrEmpty(iconTex.name))
            candidates.Add(iconTex.name);

        try
        {
            if (_mcCosmeticIdField != null)
            {
                string reflectedId = _mcCosmeticIdField.GetValue(cosmeticInstance) as string;
                if (!string.IsNullOrEmpty(reflectedId))
                    candidates.Add(reflectedId);
            }
        }
        catch { }

        candidates.Add(cosmeticId);

        for (int i = 0; i < candidates.Count; i++)
        {
            string cleaned = CleanCosmeticLabel(candidates[i]);
            if (IsUsableCosmeticLabel(cleaned))
                return cleaned;
        }

        string typeLabel = HumanizeCosmeticType(typeStr);
        if (!string.IsNullOrEmpty(typeLabel))
        {
            fromTypeFallback = true;
            return typeLabel;
        }

        string fallback = CleanCosmeticLabel(cosmeticId);
        return !string.IsNullOrEmpty(fallback) ? fallback : Humanize(cosmeticId);
    }

    private static string CleanCosmeticLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        string s = raw.Trim();
        s = s.Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < s.Length)
            s = s.Substring(slash + 1);

        s = Regex.Replace(s, @"\.(prefab|asset|png|jpg|jpeg|dds|tga|cosmetics)$", "",
            RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\((clone|instance)\)$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s,
            @"^(morecompany|more_company|more-company|lethalcompany_cosmetic|lethal_company_cosmetic|mccosmetic|mc_cosmetic|cosmetic)[_\-\.\s]*",
            "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-z])([A-Z])", "$1 $2");
        s = Regex.Replace(s, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
            " ", RegexOptions.IgnoreCase);
        s = s.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');

        string[] tokens = s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i].Trim();
            token = Regex.Replace(token, @"^[\[\]\{\}\(\)]+|[\[\]\{\}\(\)]+$", "");
            if (string.IsNullOrEmpty(token)) continue;
            if (IsCosmeticStopWord(token)) continue;
            if (IsLikelyHashToken(token)) continue;
            kept.Add(token);
        }

        return kept.Count == 0 ? string.Empty : TitleCaseLabel(string.Join(" ", kept));
    }

    private static bool IsUsableCosmeticLabel(string label)
    {
        if (string.IsNullOrEmpty(label))
            return false;

        string lower = label.ToLowerInvariant();
        if (lower == "cosmetic" || lower == "prefab" || lower == "asset" || lower == "icon")
            return false;

        return !Regex.IsMatch(label, @"^[0-9a-f\s-]{8,}$", RegexOptions.IgnoreCase);
    }

    private static bool IsCosmeticStopWord(string token)
    {
        string lower = token.ToLowerInvariant();
        return lower == "asset"
            || lower == "assets"
            || lower == "bundle"
            || lower == "cosmetic"
            || lower == "cosmetics"
            || lower == "prefab"
            || lower == "icon"
            || lower == "icons"
            || lower == "texture"
            || lower == "tex"
            || lower == "mesh"
            || lower == "mat"
            || lower == "material"
            || lower == "gameobject"
            || lower == "object";
    }

    private static bool IsLikelyHashToken(string token)
    {
        if (token.Length >= 8 && Regex.IsMatch(token, @"^[0-9a-f]+$", RegexOptions.IgnoreCase))
            return true;

        return token.Length >= 16
            && Regex.IsMatch(token, @"^[a-z0-9]+$", RegexOptions.IgnoreCase)
            && token.Any(char.IsDigit)
            && token.Any(char.IsLetter);
    }

    private static string TitleCaseLabel(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        string[] parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length <= 3 && part.Any(char.IsDigit))
                continue;

            string lower = part.ToLowerInvariant();
            parts[i] = char.ToUpperInvariant(lower[0]) + (lower.Length > 1 ? lower.Substring(1) : "");
        }
        return string.Join(" ", parts);
    }

    private static string HumanizeCosmeticType(string typeStr)
    {
        if (string.IsNullOrEmpty(typeStr))
            return string.Empty;

        switch (typeStr.ToUpperInvariant())
        {
            case "HAT": return "Hat";
            case "R_LOWER_ARM": return "Right Arm";
            case "HIP": return "Hip";
            case "L_SHIN": return "Left Shin";
            case "R_SHIN": return "Right Shin";
            case "CHEST": return "Chest";
            default:
                string cleaned = CleanCosmeticLabel(typeStr);
                return !string.IsNullOrEmpty(cleaned) ? cleaned : Humanize(typeStr);
        }
    }

    // Cosmetic IDs are save/sync keys, not necessarily friendly labels.
    // Keep the key intact and clean only the display string.
    private static string HumanizeCosmeticId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        string cleaned = CleanCosmeticLabel(id);
        return !string.IsNullOrEmpty(cleaned) ? cleaned : Humanize(id);
    }

    // Resolve the BXP cost for a given rarity. Falls back to the linear-
    // interpolated default if a config key was hand-deleted from the file.
    private static int GetEmotePriceForRarity(int rarity)
    {
        if (_tierPriceConfigs.TryGetValue(rarity, out var ce) && ce != null)
            return Mathf.Max(0, ce.Value);
        if (_discoveredTiers != null)
        {
            int idx = _discoveredTiers.FindIndex(t => t.rarity == rarity);
            int n   = _discoveredTiers.Count;
            if (idx >= 0 && n > 0)
                return GetDefaultEmoteTierPrice(idx, n);
        }
        return 1;
    }

    private static bool TmeIsEmoteUnlocked(object emote)
    {
        return TooManyEmotesBridge.IsEmoteUnlocked(emote);
    }

    private static void TmeUnlockEmoteLocal(object emote)
    {
        TooManyEmotesBridge.TryUnlockEmoteLocal(emote);
    }

    // -
    //  HELPERS  (restored from decompiled Y4NGZMenu.dll)
    // -

    // - Price computation -
    // Single source of truth for both the list/detail price labels and the
    // amount deducted on purchase. Bugs #4 and #5 were caused by three
    // independent copies of this computation drifting out of sync (one in
    // the list view, one in the detail view, and a stale closure value in
    // the purchase click handler). All three now route through this.
    private static int ComputeEffectivePrice(Y4NGZUpgradeNode node)
    {
        if (node == null) return 0;
        int raw = node.GetCurrentPrice();
        if (raw == int.MaxValue) return raw;  // maxed out
        return ConvertPriceToBxp(node, raw);
    }

    // Every upgrade price is authored in BXP directly, so the conversion is a
    // clamp. Running it through GetCurrencyAmountFromCredits() would misread a
    // 40-BXP price as 1 BXP.
    private static int ConvertPriceToBxp(Y4NGZUpgradeNode node, int rawPrice)
    {
        if (rawPrice == int.MaxValue) return rawPrice;
        return Mathf.Max(0, rawPrice);
    }

    private static int GetRawTierPrice(Y4NGZUpgradeNode node, int zeroBasedTier)
    {
        if (node == null) return 0;
        if (zeroBasedTier <= 0) return node.UnlockPrice;

        int priceIndex = zeroBasedTier - 1;
        if (node.Prices != null && priceIndex >= 0 && priceIndex < node.Prices.Length)
            return node.Prices[priceIndex];

        return int.MaxValue;
    }

    private static int GetTierPriceBxp(Y4NGZUpgradeNode node, int zeroBasedTier)
    {
        return ConvertPriceToBxp(node, GetRawTierPrice(node, zeroBasedTier));
    }

    private static string FormatBxpPrice(int price)
    {
        return price == int.MaxValue ? "MAXED" : FormatMarks(price);
    }

    private static string FormatMarks(int amount)
    {
        return amount + " " + (amount == 1 ? CurrencySingular : CurrencyPlural);
    }

    private static Color PurchaseButtonBg(bool enabled)
    {
        return enabled
            ? new Color(Palette.BgHeader.r, Palette.BgHeader.g, Palette.BgHeader.b, 0.74f)
            : new Color(Palette.BgRow.r, Palette.BgRow.g, Palette.BgRow.b, 0.42f);
    }

    private static Color PurchaseButtonHover(bool enabled)
    {
        return enabled
            ? new Color(Palette.BgHeader.r, Palette.BgHeader.g, Palette.BgHeader.b, 0.92f)
            : new Color(Palette.BgRowHover.r, Palette.BgRowHover.g, Palette.BgRowHover.b, 0.56f);
    }

    private static void OpenCursor()
    {
        Cursor.visible   = true;
        Cursor.lockState = CursorLockMode.None;
        var player = StartOfRound.Instance?.localPlayerController;
        if (player != null) player.quickMenuManager.isMenuOpen = true;
        // #237: tell Company's input watchdog this latch is deliberately held, so it does not
        // count our open menu as a stranded flag. Released in MenuController.
        CompanyInputWatchdogLatch.Push();
    }

    // - Purchase refusal notice -
    // A refused purchase has to be visible in the menu, not only in the log: the player otherwise
    // sees a click that did nothing. #213.
    private static string _purchaseNotice;
    private static string _purchaseNoticeUpgradeId;
    private static Color _purchaseNoticeColor = Palette.Danger;
    private static float _purchaseNoticeUntil;

    private static void SetPurchaseNotice(string upgradeId, string text, Color color)
    {
        _purchaseNotice = text;
        _purchaseNoticeUpgradeId = upgradeId;
        _purchaseNoticeColor = color;
        _purchaseNoticeUntil = Time.unscaledTime + PurchaseNoticeDuration;
        SchedulePurchaseNoticeExpiry(_purchaseNoticeUntil);
    }

    private static void ClearPurchaseNotice()
    {
        _purchaseNotice = null;
        _purchaseNoticeUpgradeId = null;
        _purchaseNoticeUntil = 0f;
    }

    // The notice belongs to the upgrade it refused: another upgrade's panel must not inherit it.
    private static bool TryGetPurchaseNotice(string upgradeId, out string text, out Color color)
    {
        text = null;
        color = _purchaseNoticeColor;
        if (string.IsNullOrEmpty(_purchaseNotice)
            || !string.Equals(upgradeId, _purchaseNoticeUpgradeId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Time.unscaledTime > _purchaseNoticeUntil)
        {
            ClearPurchaseNotice();
            return false;
        }

        text = _purchaseNotice;
        return true;
    }

    // Nothing repaints the panel on its own, so the notice needs a timer to take itself off screen.
    private static void SchedulePurchaseNoticeExpiry(float until)
    {
        if (_root == null) return;
        var host = _root.GetComponent<MenuController>();
        if (host != null && host.isActiveAndEnabled)
            host.StartCoroutine(PurchaseNoticeExpiryRoutine(until));
    }

    private static IEnumerator PurchaseNoticeExpiryRoutine(float until)
    {
        while (Time.unscaledTime <= until)
            yield return null;

        // A newer notice replaced this one and scheduled its own expiry.
        if (_purchaseNoticeUntil != until)
            yield break;

        Y4NGZUpgradeNode selection = currentSelection;
        bool wasVisible = selection != null
            && string.Equals(selection.Id, _purchaseNoticeUpgradeId, StringComparison.OrdinalIgnoreCase);
        ClearPurchaseNotice();

        if (wasVisible && _root != null && _root.activeInHierarchy && _detailView != null)
            showUpgrades(selection.SharedUpgrade, resetScroll: false);
    }

    private static string DescribePurchaseFailure(Y4NGZUpgradePurchaseResult result)
    {
        switch (result)
        {
            case Y4NGZUpgradePurchaseResult.PriceChanged:
                return "PRICE CHANGED - NOTHING CHARGED";
            case Y4NGZUpgradePurchaseResult.SaveUnavailable:
                return "SAVE NOT READY - NOTHING CHARGED";
            case Y4NGZUpgradePurchaseResult.NotEnoughCurrency:
                return "NOT ENOUGH TOKENS";
            case Y4NGZUpgradePurchaseResult.Maxed:
                return "ALREADY AT MAX TIER";
            case Y4NGZUpgradePurchaseResult.GateLocked:
                return "TREE REQUIREMENT NOT MET";
            case Y4NGZUpgradePurchaseResult.PrerequisiteLocked:
                return "PREREQUISITE NOT MET";
            default:
                return "PURCHASE REFUSED - NOTHING CHARGED";
        }
    }

    private static void RefreshCurrencyDisplay()
    {
        if (_currencyText == null) return;

        int amount = 0;
        try { amount = CurrencyManager.Instance.CurrencyAmount; } catch { }

        _currencyText.color = amount <= LowCurrency ? Palette.Warning : Palette.Gold;
        _currencyText.text  = FormatMarks(amount);
    }

    private static void PulseCurrency(Color flashColor)
    {
        if (_currencyText == null || _root == null) return;
        var host = _root.GetComponent<MenuController>();
        if (host != null && host.isActiveAndEnabled)
            host.StartCoroutine(PulseCurrencyRoutine(_currencyText, flashColor));
    }

    private static IEnumerator PulseCurrencyRoutine(TMP_Text target, Color flashColor)
    {
        if (target == null) yield break;

        var rt = target.GetComponent<RectTransform>();
        Color baseColor = target.color;
        Vector3 baseScale = rt != null ? rt.localScale : Vector3.one;
        const float duration = 0.18f;

        for (float t = 0f; t < duration; t += Time.unscaledDeltaTime)
        {
            if (target == null) yield break;
            float p = Mathf.Clamp01(t / duration);
            target.color = Color.Lerp(flashColor, baseColor, p);
            if (rt != null)
            {
                float scale = 1f + 0.08f * Mathf.Sin(p * Mathf.PI);
                rt.localScale = baseScale * scale;
            }
            yield return null;
        }

        if (target != null) target.color = baseColor;
        if (rt != null) rt.localScale = baseScale;
    }

    private static void ClearContent()
    {
        if (_scrollContent == null) return;
        _selectedUpgradeRowVisual = null;
        _selectedSkillTreeNodeVisual = null;
        _upgradeRowVisuals.Clear();
        foreach (Transform child in _scrollContent)
            Object.Destroy(child.gameObject);
    }

    private sealed class UpgradeCopy
    {
        internal readonly string Summary;
        internal readonly string[] TierEffects;

        internal UpgradeCopy(string summary, params string[] tierEffects)
        {
            Summary = summary ?? "";
            TierEffects = tierEffects ?? Array.Empty<string>();
        }
    }

    private sealed class UpgradeLevelDetail
    {
        internal int Tier;
        internal bool Complete;
        internal bool Next;
        internal int Price = int.MaxValue;
        internal string Effect = "";
    }

    private static readonly Dictionary<string, UpgradeCopy> UpgradeCopyText =
        new Dictionary<string, UpgradeCopy>(StringComparer.OrdinalIgnoreCase)
    {
        { "Sprinter", new UpgradeCopy(
            "Combines mobility, stamina, climbing, terrain handling, quiet movement, and fall training.",
            "Movement speed, sinking movement, and ladder speed improve.",
            "Stamina capacity, stamina recovery, traction, and ladder speed improve further.",
            "Jump height improves, uphill movement penalties are reduced, and ladder speed reaches peak output.",
            "Footstep noise drops and crouch movement improves.",
            "Jumping improves again and fall damage is reduced.") },
        { "Quick Hands", new UpgradeCopy(
            "Improves hold/interact speed and close pickup handling.",
            "Hold interactions are 10% faster and grab reach increases slightly.",
            "Hold interactions are 20% faster and grab reach improves further.",
            "Hold interactions are 30% faster and grab reach reaches its cap.") },
        { "Light Feet", new UpgradeCopy(
            "Reduces automated hazard response to employee movement.",
            "Landmine footstep detection is disabled.",
            "Turrets take longer to fire after spotting the employee.",
            "Lightning strikes no longer kill or damage the employee.") },
        { "Lethal Hands", new UpgradeCopy(
            "Authorizes empty-handed combat and improves punch force by tier.",
            "Empty-hand punches deal 0.5x enemy punch damage.",
            "Empty-hand punches deal 1.0x enemy punch damage.",
            "Empty-hand punches deal 1.5x damage and briefly stun targets.") },
        { "Bait Bomb", new UpgradeCopy(
            "Throws a single-use lure bomb that redirects hostile attention.",
            "Bomb beeps for 3 seconds and attracts enemies within 10m.",
            "Bomb range increases to 20m and active time increases to 6 seconds.",
            "Bomb detonates after beeping and ignites enemies caught in the blast.") },
        { "Pumping Iron", new UpgradeCopy(
            "Increases valid melee weapon hit force; empty-hand punches are excluded.",
            "Valid melee weapon hits gain +1 force.",
            "Valid melee weapon hits gain +2 force.",
            "Valid melee weapon hits gain +3 force.") },
        { "Deathbound", new UpgradeCopy(
            "Binds the far-left hotbar slot through death.",
            "The item held in the far-left hotbar slot stays with you through death and respawn.") },
        { "Adrenaline Rush", new UpgradeCopy(
            "Triggers emergency movement and survival bonuses when health collapses.",
            "Dropping to 20 HP or lower grants a 30% movement speed surge.",
            "Once per round, a lethal enemy or trap hit leaves you at 1 HP with brief invincibility.") },
        { "Lone Wolf", new UpgradeCopy(
            "Activates survival bonuses when you are the last living employee outside the ship.",
            "Last-survivor state grants a 30% movement speed bonus.",
            "Lone Wolf also restores stamina while active.",
            "Lone Wolf briefly reveals nearby enemies when it activates.") },
        { "Shadow Step", new UpgradeCopy(
            "Reduces enemy awareness and later unlocks a short invisibility cloak.",
            "Crouching reduces enemy detection range.",
            "Walking also receives reduced detection; sprinting remains fully detectable.",
            "Configured key activates a 6 second cloak with a 120 second cooldown.") },
        { "Resilience", new UpgradeCopy(
            "Raises maximum health, restores critical injuries over time, and culminates in a countershock.",
            "Max health increases by 20 and critical injuries regenerate toward stable health.",
            "Max health increases by 40 and the regeneration cap improves.",
            "Max health increases by 60 with improved injury regeneration.",
            "Max health increases by 80 and enemy melee hits reflect back with a brief stun.") },
        { "Transporter", new UpgradeCopy(
            "Reduces the operational penalty of hauling heavy quota objects.",
            "Carry-weight penalty is reduced by 20%.",
            "Carry-weight penalty is reduced by 40%.",
            "Carry-weight penalty is reduced by 60%.") },
        { "Field Optics", new UpgradeCopy(
            "Upgrades issued optics - scanner glass and lamp alike - so you spot scrap sooner and light more of the room.",
            "Scanner range +15%, flashlight brightness +10%, cone width +10%.",
            "Scanner range +30%, flashlight brightness +15%, cone width +20%.",
            "Scanner range +45%, flashlight brightness +40%, cone width +30%.") },
        { "Squad Sight", new UpgradeCopy(
            "Outlines teammates through walls or visibility blockers.",
            "Eligible teammate outlines appear within 15m.",
            "Eligible teammate outlines appear within 30m.") },
        { "Field Operations", new UpgradeCopy(
            "Grants the Field Operations tablet.",
            "Grants access to the Field Operations tablet.") },
        { "Field Mechanic", new UpgradeCopy(
            "Improves field repairs and adds on-site hacking of facility security hardware.",
            "Placed drills and pumps are less likely to break, drill/pump repairs complete 50% faster, and Company CCTV cameras can be disabled with a 1.2s hold interaction.",
            "Batteries can now be repaired, and standard locked doors can be hacked open with a 1.2s hold interaction, no key required.",
            "Disable turrets from the Field Operations tablet or with a nearby hold interaction.") },
        { "Worklight Beacon", new UpgradeCopy(
            "Throws recoverable sticky worklights that illuminate the area where they land.",
            "Carry three brighter sticky worklight beacons.",
            "Carry five stronger worklight beacons.",
            "Carry nine maximum-brightness worklight beacons.") },
        { "Deeper Pockets", new UpgradeCopy(
            "Expands the employee carry harness with additional item slots.",
            "Adds 1 extra inventory slot.",
            "Adds 2 extra inventory slots.",
            "Adds 3 extra inventory slots and allows carrying two two-handed items.") },
        { "Chameleon", new UpgradeCopy(
            "Adaptive camouflage plating confuses security camera tracking.",
            "Security cameras take 15% longer to detect you.",
            "Security cameras take 30% longer to detect you.",
            "Security cameras take 50% longer to detect you.") },
        { "Scavenger", new UpgradeCopy(
            "Squeezes extra value out of everything scavenged in the field.",
            "Ammo pickups grant 10% more rounds and your fuel chute deposits are worth 10% more.",
            "Ammo pickups grant 20% more rounds and fuel deposits are worth 20% more.",
            "Ammo pickups grant 30% more rounds and fuel deposits are worth 30% more.") },
        { "Overachiever", new UpgradeCopy(
            "Eager careerists earn extra upgrade tokens with every promotion.",
            "Each level-up grants 1 extra upgrade token.",
            "Each level-up grants 2 extra upgrade tokens.",
            "Each level-up grants 4 extra upgrade tokens.") },
        { "Veteran", new UpgradeCopy(
            "A veteran Foreman doesn't get rattled - the Company goes easy on survivors.",
            "Quota increases caused by crew deaths are capped at 10% of the pre-penalty quota per round-end.",
            "The quota deadline is extended to 4 days instead of 3.") },
        { "Inspire", new UpgradeCopy(
            "Uses Ping on a nearby dead teammate for a once-per-round revive attempt.",
            "Pinging a dead body within 7m has a 25% chance to revive the player. The use is consumed only on success.") },
    };

    private static readonly UpgradeCopy ScavengerAmmoOnlyCopy = new UpgradeCopy(
        "Recovers more ammunition from field pickups.",
        "Ammo pickups grant 10% more rounds.",
        "Ammo pickups grant 20% more rounds.",
        "Ammo pickups grant 30% more rounds.");

    private static readonly UpgradeCopy ScavengerFuelOnlyCopy = new UpgradeCopy(
        "Extracts more value from fuel delivered through the chute.",
        "Fuel-chute deposits are worth 10% more.",
        "Fuel-chute deposits are worth 20% more.",
        "Fuel-chute deposits are worth 30% more.");

    private static readonly Dictionary<string, string> UpgradeFlavorText =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Sprinter", "Company fitness program, billed per breath, step, ladder rung, and questionable landing." },
        { "Quick Hands", "Approved for employees with steady hands and unstable priorities." },
        { "Light Feet", "Every quiet step is one less form to fill out." },
        { "Lethal Hands", "The Company reminds you that hands are not covered equipment." },
        { "Bait Bomb", "The Company-approved way to make bad decisions look elsewhere." },
        { "Pumping Iron", "Morale in a scoop. Side effects pending approval." },
        { "Deathbound", "Some things stay on the employee, even after the employee stops staying." },
        { "Adrenaline Rush", "Panic, refined and monetized." },
        { "Lone Wolf", "Solo productivity metrics, violently interpreted." },
        { "Shadow Step", "A personnel movement exception for poorly lit hallways." },
        { "Resilience", "Resilience training with fewer lectures." },
        { "Transporter", "Carry more so the quota can ask for even more." },
        { "Field Optics", "Because missing scrap in the dark is a personal failure." },
        { "Field Operations", "Tablet privileges granted. Responsibility regrettably follows." },
        { "Field Mechanic", "For when the facility breaks, and then breaks back." },
        { "Worklight Beacon", "Stick it where the dark keeps winning." },
        { "Squad Sight", "Perfect trust, now with plausible deniability." },
        { "Deeper Pockets", "More pockets, more problems, mostly more quota." },
        { "Chameleon", "The cameras still see you. They just take a while to believe it." },
        { "Scavenger", "One employee's trash is the Company's untaxed revenue." },
        { "Overachiever", "Ambition is its own reward. The extra tokens are just bookkeeping." },
        { "Veteran", "Seen it all, billed for most of it. The Company respects a survivor." },
        { "Inspire", "Motivation so strong it occasionally violates company death policy." },
    };

    private static List<UpgradeLevelDetail> BuildUpgradeLevelDetails(
        Y4NGZUpgradeNode node,
        out string summaryText)
    {
        summaryText = "";
        var result = new List<UpgradeLevelDetail>();
        if (node == null)
            return result;

        int currentLevel = node.GetCurrentLevel();
        int maxTier = Mathf.Max(1, node.MaxUpgrade + 1);
        bool maxed = node.GetRemainingLevels() == 0
            || (node.Unlocked && node.CurrentUpgrade >= node.MaxUpgrade);

        var effects = ExtractUpgradeLevelEffects(node, maxTier, out string descriptionText);
        UpgradeCopy copy = GetUpgradeCopy(node);
        if (copy != null)
        {
            for (int i = 0; i < copy.TierEffects.Length && i < maxTier; i++)
                effects[i + 1] = copy.TierEffects[i];
        }

        for (int tier = 1; tier <= maxTier; tier++)
        {
            result.Add(new UpgradeLevelDetail
            {
                Tier = tier,
                Complete = tier <= currentLevel,
                Next = !maxed && tier == Mathf.Clamp(currentLevel + 1, 1, maxTier),
                Price = tier <= currentLevel ? int.MaxValue : GetTierPriceBxp(node, tier - 1),
                Effect = GetUpgradeTierEffect(node, tier, maxTier, effects)
            });
        }

        summaryText = GetUpgradeSummary(node, copy, descriptionText);
        return result;
    }

    private static float BuildUpgradeLevelDetailRows(
        Transform parent,
        List<UpgradeLevelDetail> levels,
        string summaryText,
        Color accent)
    {
        if (parent == null || levels == null || levels.Count == 0)
            return 0f;

        float y = 6f;
        var header = UI.MakeText("LevelsHeader", parent,
            "LEVELS", FontMd, Palette.Gold, TextAlignmentOptions.MidlineLeft);
        header.fontStyle = FontStyles.Bold;
        var headerRt = header.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0, 1);
        headerRt.anchorMax = new Vector2(1, 1);
        headerRt.pivot = new Vector2(0.5f, 1f);
        headerRt.sizeDelta = new Vector2(-12f, 24f);
        headerRt.anchoredPosition = new Vector2(0f, -y);
        y += 30f;

        for (int i = 0; i < levels.Count; i++)
        {
            UpgradeLevelDetail detail = levels[i];
            float rowH = BuildUpgradeLevelDetailRow(parent, detail, y, accent);
            y += rowH + 8f;
        }

        if (!string.IsNullOrWhiteSpace(summaryText))
        {
            y += 8f;

            var summaryHeader = UI.MakeText("SummaryHeader", parent,
                "FILE SUMMARY", FontMd, Palette.Gold, TextAlignmentOptions.MidlineLeft);
            summaryHeader.fontStyle = FontStyles.Bold;
            var shRt = summaryHeader.GetComponent<RectTransform>();
            shRt.anchorMin = new Vector2(0, 1);
            shRt.anchorMax = new Vector2(1, 1);
            shRt.pivot = new Vector2(0.5f, 1f);
            shRt.sizeDelta = new Vector2(-12f, 22f);
            shRt.anchoredPosition = new Vector2(0f, -y);
            y += 24f;

            var summary = UI.MakeText("Summary", parent,
                EnsurePeriod(summaryText), FontSm, Palette.Body, TextAlignmentOptions.TopLeft);
            summary.enableWordWrapping = true;
            summary.overflowMode = TextOverflowModes.Overflow;
            var sumRt = summary.GetComponent<RectTransform>();
            sumRt.anchorMin = new Vector2(0, 1);
            sumRt.anchorMax = new Vector2(1, 1);
            sumRt.pivot = new Vector2(0.5f, 1f);
            summary.ForceMeshUpdate();
            float summaryH = Mathf.Max(34f, summary.preferredHeight + 8f);
            sumRt.sizeDelta = new Vector2(-12f, summaryH);
            sumRt.anchoredPosition = new Vector2(0f, -y);
            y += summaryH + 6f;
        }

        return y;
    }

    private static float BuildUpgradeLevelDetailRow(
        Transform parent,
        UpgradeLevelDetail detail,
        float y,
        Color accent)
    {
        const float minHeight = 64f;
        float height = minHeight;

        bool emphasized = detail.Next;
        bool future = !detail.Complete && !detail.Next;
        Color rowBg = emphasized
            ? new Color(Palette.Gold.r, Palette.Gold.g, Palette.Gold.b, 0.10f)
            : new Color(Palette.BgRow.r, Palette.BgRow.g, Palette.BgRow.b, detail.Complete ? 0.16f : 0.05f);
        var row = UI.MakePanel("LevelRow_" + detail.Tier, parent,
            rowBg,
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(0f, -(y + height)), new Vector2(-8f, -y));
        var group = row.AddComponent<CanvasGroup>();
        group.alpha = future ? 0.56f : 1f;
        Image rowImg = row.GetComponent<Image>();
        if (rowImg != null)
            rowImg.raycastTarget = false;
        if (emphasized)
        {
            UI.MakePanel("LevelRowAccent", row.transform, Palette.Gold,
                new Vector2(1, 0), new Vector2(1, 1),
                new Vector2(-5f, 0f), new Vector2(0f, 0f))
                .GetComponent<Image>().raycastTarget = false;
        }

        BuildLevelNumberDiamond(row.transform, detail.Tier, emphasized, detail.Complete, future, accent);

        string status = detail.Complete
            ? "COMPLETE"
            : "COST: " + FormatBxpPrice(detail.Price);
        var cost = UI.MakeText("LevelCost", row.transform,
            status, FontSm, emphasized ? Palette.Gold : future ? Palette.Dim : Palette.Accent,
            TextAlignmentOptions.MidlineLeft);
        cost.fontStyle = FontStyles.Bold;
        cost.enableWordWrapping = false;
        cost.overflowMode = TextOverflowModes.Ellipsis;
        var costRt = cost.GetComponent<RectTransform>();
        costRt.anchorMin = new Vector2(0, 1);
        costRt.anchorMax = new Vector2(1, 1);
        costRt.offsetMin = new Vector2(72f, -26f);
        costRt.offsetMax = new Vector2(-16f, -6f);

        var effect = UI.MakeText("LevelEffect", row.transform,
            EnsurePeriod(detail.Effect), FontSm,
            future
                ? new Color(Palette.Body.r, Palette.Body.g, Palette.Body.b, 0.72f)
                : Palette.Body,
            TextAlignmentOptions.TopLeft);
        effect.enableWordWrapping = true;
        effect.overflowMode = TextOverflowModes.Overflow;
        var effectRt = effect.GetComponent<RectTransform>();
        effectRt.anchorMin = new Vector2(0, 0);
        effectRt.anchorMax = new Vector2(1, 1);
        effectRt.offsetMin = new Vector2(72f, 8f);
        effectRt.offsetMax = new Vector2(-16f, -28f);

        RectTransform parentRt = parent as RectTransform;
        float parentWidth = parentRt != null ? parentRt.rect.width : 0f;
        if (parentWidth <= 0f)
            parentWidth = InspectorW - (InspectorSide * 2f) - 14f;
        float effectWidth = Mathf.Max(120f, parentWidth - 96f);
        float effectHeight = effect.GetPreferredValues(effect.text, effectWidth, Mathf.Infinity).y;
        float resolvedHeight = Mathf.Max(minHeight, Mathf.Ceil(effectHeight) + 40f);

        var rowRt = row.GetComponent<RectTransform>();
        rowRt.offsetMin = new Vector2(0f, -(y + resolvedHeight));
        rowRt.offsetMax = new Vector2(-8f, -y);
        return resolvedHeight;

    }

    private static void BuildLevelNumberDiamond(
        Transform parent,
        int tier,
        bool emphasized,
        bool complete,
        bool future,
        Color accent)
    {
        var diamond = new GameObject("LevelDiamond_" + tier);
        diamond.transform.SetParent(parent, false);
        var rt = diamond.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 0.5f);
        rt.anchorMax = new Vector2(0, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(34f, 34f);
        rt.anchoredPosition = new Vector2(34f, 0f);
        rt.localRotation = Quaternion.Euler(0f, 0f, 45f);

        Color marker = emphasized
            ? Palette.Gold
            : complete
                ? accent
                : future
                    ? Palette.Dim
                    : Palette.Accent;
        AddBorder(diamond.transform, 2f, 0f,
            new Color(marker.r, marker.g, marker.b, emphasized ? 0.94f : future ? 0.56f : 0.76f));

        var label = UI.MakeText("LevelDiamondNumber", diamond.transform,
            tier.ToString(), FontMd, marker, TextAlignmentOptions.Center);
        label.fontStyle = FontStyles.Bold;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        var labelRt = label.GetComponent<RectTransform>();
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
        labelRt.localRotation = Quaternion.Euler(0f, 0f, -45f);
    }

    private static void BuildUpgradeDescription(Y4NGZUpgradeNode node, out string statsText, out string loreText)
    {
        statsText = "";
        loreText  = "";
        if (node == null) return;

        int currentLevel = node.GetCurrentLevel();
        int maxTier      = Mathf.Max(1, node.MaxUpgrade + 1);
        bool maxed       = node.GetRemainingLevels() == 0 ||
                           (node.Unlocked && node.CurrentUpgrade >= node.MaxUpgrade);

        var effects = ExtractUpgradeLevelEffects(node, maxTier, out string descriptionText);
        UpgradeCopy copy = GetUpgradeCopy(node);
        if (copy != null)
        {
            for (int i = 0; i < copy.TierEffects.Length && i < maxTier; i++)
                effects[i + 1] = copy.TierEffects[i];
        }

        var sb = new StringBuilder();

        AppendSectionHeader(sb, "LEVELS");
        for (int tier = 1; tier <= maxTier; tier++)
        {
            if (tier > 1) sb.Append('\n');

            string tierLabel = "LEVEL " + tier;
            int price = int.MaxValue;
            if (tier <= currentLevel)
                tierLabel += " COMPLETE";
            else
                price = GetTierPriceBxp(node, tier - 1);

            AppendTierAuthorization(sb, node, tier, tierLabel,
                GetUpgradeTierEffect(node, tier, maxTier, effects),
                price);
        }

        string summary = GetUpgradeSummary(node, copy, descriptionText);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.Append('\n');
            AppendSectionHeader(sb, "FILE SUMMARY");
            sb.Append(ColorTag(Palette.Body, EnsurePeriod(summary))).Append('\n');
        }

        statsText = sb.ToString();
    }

    private static UpgradeCopy GetUpgradeCopy(Y4NGZUpgradeNode node)
    {
        if (node == null) return null;
        if (string.Equals(node.Id, "scavenger", StringComparison.OrdinalIgnoreCase))
            return BuildScavengerUpgradeCopy();
        return UpgradeCopyText.TryGetValue(node.Name, out var copy) ? copy : null;
    }

    private static UpgradeCopy BuildScavengerUpgradeCopy()
    {
        bool ammunitionActive = OptionalPluginCapabilities.BetterArmory;
        bool fuelActive = OptionalPluginCapabilities.ShipSystems;
        if (ammunitionActive && !fuelActive)
            return ScavengerAmmoOnlyCopy;
        if (fuelActive && !ammunitionActive)
            return ScavengerFuelOnlyCopy;

        return UpgradeCopyText["Scavenger"];
    }


    private static void AppendSectionHeader(StringBuilder sb, string label)
    {
        sb.Append(ColorTag(Palette.Gold, label.ToUpperInvariant())).Append('\n');
    }

    private static void AppendTierAuthorization(
        StringBuilder sb,
        Y4NGZUpgradeNode node,
        int tier,
        string tierLabel,
        string effect,
        int price = int.MaxValue)
    {
        string line = tierLabel.ToUpperInvariant();
        if (price != int.MaxValue)
            line += "   COST: " + FormatBxpPrice(price);

        sb.Append(ColorTag(Palette.Accent, line)).Append('\n');
        sb.Append(ColorTag(Palette.Body, EnsurePeriod(effect))).Append('\n');
    }

    private static string GetUpgradeTierEffect(
        Y4NGZUpgradeNode node,
        int tier,
        int maxTier,
        Dictionary<int, string> parsedEffects)
    {
        if (parsedEffects != null
            && parsedEffects.TryGetValue(tier, out string parsed)
            && !IsPlaceholderUpgradeText(parsed))
            return SummarizeEffectText(parsed);

        UpgradeCopy copy = GetUpgradeCopy(node);
        if (copy != null && tier >= 1 && tier <= copy.TierEffects.Length)
            return EnsurePeriod(copy.TierEffects[tier - 1]);

        return tier >= maxTier
            ? "Final level for this upgrade file."
            : "Unlocks the next level.";
    }

    private static string GetUpgradeSummary(Y4NGZUpgradeNode node, UpgradeCopy copy, string parsedSummary)
    {
        if (copy != null && !string.IsNullOrWhiteSpace(copy.Summary))
            return EnsurePeriod(copy.Summary);

        string cleaned = CleanDescriptionText(parsedSummary);
        if (!string.IsNullOrWhiteSpace(cleaned) && !IsPlaceholderUpgradeText(cleaned))
            return SummarizeEffectText(cleaned);

        return GetFallbackFlavorText(node);
    }

    private static bool IsPlaceholderUpgradeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string lower = StripRichText(text).ToLowerInvariant();
        return lower.Contains("no native runtime effect")
            || lower.Contains("no current effect")
            || lower.Contains("no effect yet")
            || lower.Contains("currently implemented");
    }

    private static string SummarizeEffectText(string text)
    {
        string cleaned = CleanEffectText(text);
        if (cleaned.Length == 0) return "Unlocks this level.";
        if (cleaned.Length <= 118) return EnsurePeriod(cleaned);

        string[] sentences = Regex.Split(cleaned, @"(?<=[.!?])\s+");
        for (int i = 0; i < sentences.Length; i++)
        {
            string sentence = sentences[i].Trim();
            if (sentence.Length > 0 && sentence.Length <= 118)
                return EnsurePeriod(sentence);
        }

        string clipped = cleaned.Substring(0, Mathf.Min(cleaned.Length, 114)).Trim();
        int lastSpace = clipped.LastIndexOf(' ');
        if (lastSpace > 72) clipped = clipped.Substring(0, lastSpace).Trim();
        return EnsurePeriod(clipped);
    }

    private static string ColorTag(Color color, string text)
    {
        string hex = "#" + ColorUtility.ToHtmlStringRGBA(color);
        return "<color=" + hex + ">" + text + "</color>";
    }

    private static Dictionary<int, string> ExtractUpgradeLevelEffects(
        Y4NGZUpgradeNode node, int maxTier, out string loreText)
    {
        var effects = new Dictionary<int, string>();
        var loreLines = new List<string>();
        loreText = "";

        string description = "";
        try { description = node.Description ?? ""; } catch { }
        if (string.IsNullOrWhiteSpace(description)) return effects;

        string[] lines = description.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripRichText(lines[i]).Trim();
            if (line.Length == 0)
            {
                AddLoreLine(loreLines, "");
                continue;
            }

            if (IsDuplicateUpgradeTitle(line, node.Name))
                continue;

            if (TryParseLevelEffectLine(line, out int tier, out string effect) &&
                tier >= 1 && tier <= maxTier)
            {
                effects[tier] = effect;
                continue;
            }

            if (maxTier == 1 && effects.Count == 0 && HasLeadingPrice(line))
            {
                effects[1] = CleanEffectText(RemoveLeadingPrice(line));
                continue;
            }

            AddLoreLine(loreLines, line);
        }

        loreText = BuildLoreText(loreLines);
        return effects;
    }

    private static bool TryParseLevelEffectLine(string line, out int tier, out string effect)
    {
        tier = 0;
        effect = "";

        string working = RemoveLeadingPrice(line);
        var match = Regex.Match(working,
            @"\b(?:lvl|level|tier)\s*(\d+)\b\s*[:\-\)]?\s*(.*)$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups[1].Value, out tier)) return false;
        effect = CleanEffectText(match.Groups[2].Value);
        return effect.Length > 0;
    }

    private static string StripRichText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return Regex.Replace(text, "<.*?>", "");
    }

    private static bool HasLeadingPrice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text,
            @"^\s*(?:\$|\[\s*(?:BXP|MARKS?|TOKENS?)\s*\]|\d+\s*(?:BXP|BXPS|MARK|MARKS|TOKEN|TOKENS|PC)\b|\d+\s*[-:])",
            RegexOptions.IgnoreCase);
    }

    private static string RemoveLeadingPrice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        string cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned,
            @"^\s*\[\s*(?:BXP|MARKS?|TOKENS?)\s*\]\s*\d+\s*(?:[-:]\s*)?",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned,
            @"^\s*\$\d+(?:\s*/\s*\d+\s*(?:PC|BXP|BXPS|MARK|MARKS|TOKEN|TOKENS)?)?\s*(?:[-:]\s*)?",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned,
            @"^\s*\d+\s*(?:PC|BXP|BXPS|MARK|MARKS|TOKEN|TOKENS)\s*(?:[-:]\s*)?",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned,
            @"^\s*\d+\s*[-:]\s+",
            "",
            RegexOptions.IgnoreCase);

        return cleaned.Trim();
    }

    private static string CleanEffectText(string text)
    {
        string cleaned = StripRichText(text);
        cleaned = Regex.Replace(cleaned, @"^\s*[-:]\s*", "");
        cleaned = RemoveLeadingPrice(cleaned);
        cleaned = Regex.Replace(cleaned, @"^\s*[-:]\s*", "");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Trim(' ', '.', ';');
    }

    private static string CleanDescriptionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        string cleaned = StripRichText(text).Trim();
        cleaned = Regex.Replace(cleaned,
            @"^\s*(?:description|overview|effect|effects)\s*:\s*",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length == 0) return "";
        return EnsurePeriod(cleaned);
    }

    private static string EnsurePeriod(string text)
    {
        string cleaned = string.IsNullOrWhiteSpace(text) ? "Unlocks this level" : text.Trim();
        char last = cleaned[cleaned.Length - 1];
        return last == '.' || last == '!' || last == '?' ? cleaned : cleaned + ".";
    }

    private static bool IsDuplicateUpgradeTitle(string line, string upgradeName)
    {
        string a = NormalizeTitle(line);
        string b = NormalizeTitle(upgradeName);
        return a.Length > 0 && a == b;
    }

    private static string NormalizeTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "");
    }

    private static void AddLoreLine(List<string> loreLines, string line)
    {
        if (line.Length == 0)
        {
            if (loreLines.Count > 0 && loreLines[loreLines.Count - 1].Length != 0)
                loreLines.Add("");
            return;
        }

        loreLines.Add(line);
    }

    private static string BuildLoreText(List<string> loreLines)
    {
        while (loreLines.Count > 0 && loreLines[0].Length == 0)
            loreLines.RemoveAt(0);
        while (loreLines.Count > 0 && loreLines[loreLines.Count - 1].Length == 0)
            loreLines.RemoveAt(loreLines.Count - 1);

        return string.Join("\n", loreLines).Trim();
    }

    private static string GetFallbackFlavorText(Y4NGZUpgradeNode node)
    {
        if (node != null && UpgradeFlavorText.TryGetValue(node.Name, out var flavor))
            return flavor;

        string sub = GetSubCategory(node?.Name);
        if (string.Equals(sub, SubSpeed, StringComparison.OrdinalIgnoreCase))
            return "Movement paperwork is easier when the employee is still moving.";
        if (string.Equals(sub, SubDexterity, StringComparison.OrdinalIgnoreCase))
            return "Approved for employees with steady hands and unstable priorities.";
        if (string.Equals(sub, SubLethality, StringComparison.OrdinalIgnoreCase))
            return "The Company does not endorse violence. It invoices for outcomes.";
        if (string.Equals(sub, SubSurvival, StringComparison.OrdinalIgnoreCase))
            return "A small comfort between the employee and the incident report.";
        if (string.Equals(sub, SubLogistics, StringComparison.OrdinalIgnoreCase))
            return "Quota optimization disguised as personal development.";
        if (string.Equals(sub, SubOperations, StringComparison.OrdinalIgnoreCase))
            return "Operational awareness for crews with a future tense.";

        return "Company-approved enhancement. Results may vary under pressure.";
    }

    // Called by Y4NGZMenuRefresh when an upgrade is bought or credits are
    // traded, to re-draw the currently visible view with fresh data.
    public void refresh(bool fromTrade = false)
    {
        if (_root == null) return;

        RefreshCurrencyDisplay();
        RefreshRankDisplay();

        if (location == null) return;
        if      (location == nameof(showEmployeeFile)) showEmployeeFile();
        else if (location == nameof(showUpgrades) + ",True")  showUpgrades(true);
        else if (location == nameof(showUpgrades) + ",False") showUpgrades(false);
        else if (location == nameof(showUpgradeGui) && currentSelection != null) showUpgradeGui(currentSelection);
        else if (location == nameof(showTradeGui))    showTradeGui();
        else if (location == nameof(showPlayerLevel)) showPlayerLevel();
    }
}
