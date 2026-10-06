using TMPro;
using UnityEngine;

namespace Y4NGZUpgrades.Gui;

public partial class PurchaseMenu
{
    private static float AddAbilityInspectorStatus(Transform parent, string id, float top)
    {
        string status = AbilityInspectorStatus.Describe(id);
        if (string.IsNullOrEmpty(status)) return top;
        var label = UI.MakeText("AbilityStatus", parent, status, FontSm, Palette.Body,
            TextAlignmentOptions.TopLeft);
        label.enableWordWrapping = true;
        label.rectTransform.anchorMin = new Vector2(0, 1);
        label.rectTransform.anchorMax = new Vector2(1, 1);
        // Measured, not fixed: a dormant-ownership line (#435) sits under the live readout, so
        // the block is one or two paragraphs deep depending on the row and the save.
        RectTransform parentRt = parent as RectTransform;
        float width = parentRt != null ? parentRt.rect.width - 20f : 0f;
        if (width <= 0f)
            width = InspectorW - (InspectorSide * 2f) - 34f;
        float height = Mathf.Max(64f, Mathf.Ceil(
            label.GetPreferredValues(status, width, Mathf.Infinity).y) + 10f);
        label.rectTransform.offsetMin = new Vector2(10f, -(top + height));
        label.rectTransform.offsetMax = new Vector2(-10f, -top);
        label.gameObject.AddComponent<AbilityStatusText>().Bind(label, id);
        return top + height + 2f;
    }

    // Lives and dies with the selected inspector. A countdown updates in place,
    // without rebuilding the list or moving its scroll/focus position.
    private sealed class AbilityStatusText : MonoBehaviour
    {
        private TMP_Text _label;
        private string _id;
        private float _nextRefresh;

        internal void Bind(TMP_Text label, string id) { _label = label; _id = id; }

        private void Update()
        {
            if (_label == null || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.25f;
            string status = AbilityInspectorStatus.Describe(_id) ?? string.Empty;
            if (_label.text != status) _label.text = status;
        }
    }
}
