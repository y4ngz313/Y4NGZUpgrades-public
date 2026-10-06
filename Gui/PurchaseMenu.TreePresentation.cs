using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Gui;

public partial class PurchaseMenu
{
    private static readonly HashSet<int> _pendingTierUnlocks = new HashSet<int>();

    private static void BuildSkillTreeBands(List<Y4NGZSkillTreeNodeDefinition> nodes,
        Dictionary<string, Y4NGZUpgradeNode> upgradesById, int investment, SkillTreeLayout layout)
    {
        bool GateLocked(Y4NGZSkillTreeNodeDefinition node) =>
            upgradesById.TryGetValue(node.UpgradeId, out var upgrade) &&
            Y4NGZUpgradeManager.IsGateLocked(node, upgrade.GetCurrentLevel());
        var nextGate = SkillTreeTierPresentation.NextGate(nodes, investment, GateLocked);
        foreach (var row in layout.TierBands)
        {
            // The group key is the clamped tier, so a node authored above the
            // fourth tier belongs to the band that actually draws it.
            var tierNodes = nodes.Where(n => Mathf.Clamp(n.Tier, 1, 4) == row.Tier).ToList();
            bool locked = tierNodes.Any(GateLocked);
            float headingTop = row.Top - SkillTreeCaptionInset;
            var band = UI.MakePanel("TierBand_" + row.Tier, _scrollContent,
                Color.clear,
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(8f, -(row.Top + row.CardsHeight + SkillTreeCardLayout.Clearance)),
                new Vector2(-8f, -headingTop));
            band.GetComponent<Image>().raycastTarget = false;

            var separator = UI.MakePanel("TierSeparator_" + row.Tier, _scrollContent,
                Palette.Alpha(Palette.Accent, 0.10f),
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(8f, -headingTop), new Vector2(-8f, -headingTop + 1f));
            separator.GetComponent<Image>().raycastTarget = false;

            var label = UI.MakeText("TierAccess_" + row.Tier, _scrollContent,
                SkillTreeTierPresentation.Title(row.Tier),
                FontSm, Palette.Accent, TextAlignmentOptions.MidlineLeft);
            label.fontStyle = FontStyles.Bold;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            var rt = label.rectTransform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            float headingBottom = headingTop + SkillTreeCardLayout.CaptionHeight;
            rt.offsetMin = new Vector2(18f, -headingBottom);
            rt.offsetMax = new Vector2(-18f, -headingTop);

            string hint = SkillTreeTierPresentation.Hint(row.Tier, nextGate);
            if (!string.IsNullOrEmpty(hint))
            {
                var hintLabel = UI.MakeText("TierUnlockHint_" + row.Tier, _scrollContent, hint,
                    FontSm, Palette.Accent, TextAlignmentOptions.MidlineRight);
                hintLabel.raycastTarget = false;
                hintLabel.enableWordWrapping = false;
                var hintRt = hintLabel.rectTransform;
                hintRt.anchorMin = hintRt.anchorMax = new Vector2(1f, 1f);
                float hintWidth = hintLabel.GetPreferredValues(hint).x + 2f;
                hintRt.offsetMin = new Vector2(-18f - hintWidth, -headingBottom);
                hintRt.offsetMax = new Vector2(-18f, -headingTop);
            }

            // The compact rail is the only class-progress line. The tree draws no
            // node-to-node lines; the padlock and the inspector carry prerequisites.
            var rail = UI.MakePanel("TierAccessRail_" + row.Tier, band.transform,
                Palette.Alpha(locked ? Palette.Locked : Palette.Accent, locked ? 0.12f : 0.30f),
                Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(1f, 0f));
            rail.GetComponent<Image>().raycastTarget = false;
            if (_pendingTierUnlocks.Contains(row.Tier) && !(Plugin.ReduceMenuMotion?.Value ?? false))
                band.AddComponent<TierUnlockFeedback>().Begin(band.GetComponent<Image>(), label);
        }
    }

    private static void BuildSkillNodeBadge(Transform parent, bool locked)
    {
        var go = new GameObject(locked ? "NodeLock" : "NodeMaxed");
        go.transform.SetParent(parent, false);
        var badge = go.AddComponent<SkillTreeNodeBadge>();
        badge.Locked = locked;
        badge.color = locked ? SkillTreeLockedText : Palette.Gold;
        badge.raycastTarget = false;
        var rt = badge.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.one;
        rt.pivot = Vector2.one;
        rt.sizeDelta = new Vector2(16f, 16f);
        rt.anchoredPosition = new Vector2(-6f, -6f);
        badge.SetVerticesDirty();
    }

    private static void RecordTierUnlocks(Y4NGZSkillTreeClass treeClass, int before)
    {
        _pendingTierUnlocks.Clear();
        int after = Y4NGZUpgradeManager.GetTreeInvestment(treeClass);
        foreach (var node in Y4NGZUpgradeManager.GetSkillTreeNodes(treeClass))
            if (node.GateRequirement > before && node.GateRequirement <= after)
                _pendingTierUnlocks.Add(node.Tier);
    }

    private sealed class TierUnlockFeedback : MonoBehaviour
    {
        internal void Begin(Image band, TMP_Text label) { StartCoroutine(Play(band, label)); }

        private IEnumerator Play(Image band, TMP_Text label)
        {
            Color rest = band.color;
            Color textRest = label.color;
            float elapsed = 0f;
            while (elapsed < 0.48f)
            {
                elapsed += Time.unscaledDeltaTime;
                float emphasis = 1f - Mathf.SmoothStep(0f, 1f, elapsed / 0.48f);
                band.color = Color.Lerp(rest, Palette.Alpha(Palette.Accent, 0.08f), emphasis);
                label.color = Color.Lerp(textRest, Palette.Gold, emphasis);
                yield return null;
            }
            band.color = rest;
            label.color = textRest;
        }
    }
}
