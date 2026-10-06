using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Gui;

public partial class PurchaseMenu
{
    private static string _contentFocusKey;
    private static string _detailScrollOwner;
    private static float _detailScrollPosition = 1f;

    private static void RegisterInspectorAction(Button button, string id)
    {
        RegisterFocusTarget("inspector:" + id, button.GetComponent<RectTransform>(), button,
            10000, 0, null);
    }

    private static void ClearInspectorFocusTargets()
    {
        string key = _focusIndex >= 0 && _focusIndex < _focusTargets.Count ? _focusTargets[_focusIndex].Key : null;
        _focusTargets.RemoveAll(x => x.Key.StartsWith("inspector:", StringComparison.Ordinal));
        _focusIndex = key == null ? -1 : _focusTargets.FindIndex(x => x.Key == key);
    }

    private static void ToggleInspectorFocus()
    {
        if (_focusIndex >= 0 && _focusIndex < _focusTargets.Count &&
            _focusTargets[_focusIndex].Key.StartsWith("inspector:", StringComparison.Ordinal))
        {
            int index = _focusTargets.FindIndex(x => x.Key == _contentFocusKey);
            if (index < 0) index = _focusTargets.FindIndex(x => !x.Key.StartsWith("inspector:", StringComparison.Ordinal));
            if (index >= 0) SetFocusIndex(index, true);
            return;
        }
        int inspector = _focusTargets.FindIndex(x => x.Key.StartsWith("inspector:", StringComparison.Ordinal));
        if (inspector < 0) return;
        if (_focusIndex >= 0 && _focusIndex < _focusTargets.Count) _contentFocusKey = _focusTargets[_focusIndex].Key;
        SetFocusIndex(inspector, false);
    }

    internal static float FittedMenuScale(RectTransform canvasRect)
    {
        if (canvasRect == null || canvasRect.rect.width <= 0f || canvasRect.rect.height <= 0f) return MenuScale;
        float fit = Mathf.Min((canvasRect.rect.width - 40f) / PanelW, (canvasRect.rect.height - 40f) / PanelH);
        return Mathf.Min(MenuScale, Mathf.Max(0.1f, fit));
    }

    private static float ScrollPosition(ScrollRect scroll)
        => scroll != null && scroll.content != null && scroll.viewport != null &&
           scroll.content.rect.height > scroll.viewport.rect.height + 1f
            ? scroll.verticalNormalizedPosition : 1f;

    private static void RestoreScroll(ScrollRect scroll, float position)
    {
        if (scroll is MenuScrollRect menuScroll) menuScroll.RestorePosition(position);
        else if (scroll != null) scroll.verticalNormalizedPosition = position;
    }
}
