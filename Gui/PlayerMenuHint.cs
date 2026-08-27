using TMPro;
using UnityEngine;

namespace Y4NGZUpgrades.Gui;

// Bottom-centre player-menu hint that mirrors vanilla's interaction-prompt look. Visibility and
// the displayed InputUtils binding both track the real open action, so the hint cannot lie.
public class PlayerMenuHint : MonoBehaviour
{
    private GameObject _canvasGo;
    private TMP_Text   _label;
    private string     _bindingPath;

    private void Update()
    {
        if (!EnsureCanvas()) return;
        RefreshBindingLabel();
        _canvasGo.SetActive(ShouldShow());
    }

    private bool ShouldShow()
    {
        // Mirror the same gating tryShowMenu uses, so the hint is only visible
        // when pressing P would actually open the menu.
        return Plugin.CanOpenPurchaseMenu(out _);
    }

    private bool EnsureCanvas()
    {
        if (_canvasGo != null) return true;
        if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null) return false;
        var hudCanvas = HUDManager.Instance.playerScreenTexture.canvas;
        if (hudCanvas == null) return false;

        _canvasGo = new GameObject("Y4NGZ_PlayerMenuHint");
        _canvasGo.transform.SetParent(hudCanvas.transform, worldPositionStays: false);
        var rt = _canvasGo.AddComponent<RectTransform>();
        // Bottom-centre, ~120px above the bottom edge so it clears the
        // vanilla `[E] : Use` interaction line.
        rt.anchorMin        = new Vector2(0.5f, 0f);
        rt.anchorMax        = new Vector2(0.5f, 0f);
        rt.pivot            = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 120f);
        rt.sizeDelta        = new Vector2(420f, 30f);

        _label = _canvasGo.AddComponent<TextMeshProUGUI>();
        _label.text                = "Press [P] for player menu";
        _label.fontSize            = 14f;
        _label.alignment           = TextAlignmentOptions.Center;
        _label.color               = new Color(1f, 1f, 1f, 0.55f);
        _label.raycastTarget       = false;
        _label.enableWordWrapping  = false;

        var font = UI.GetTerminalFont();
        if (font != null) _label.font = font;

        RefreshBindingLabel();
        return true;
    }

    private void RefreshBindingLabel()
    {
        if (_label == null)
            return;

        string path = UpgradeInput.EffectivePath(Plugin.Keybinds?.PurchaseMenu);
        if (string.Equals(_bindingPath, path, System.StringComparison.Ordinal))
            return;

        _bindingPath = path;
        string label = UpgradeInput.DisplayLabel(Plugin.Keybinds?.PurchaseMenu, "P");
        _label.text = $"Press [{label}] for player menu";
    }

    private void OnDestroy()
    {
        if (_canvasGo != null) Object.Destroy(_canvasGo);
        _canvasGo = null;
        _label    = null;
    }
}
