using System;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Y4NGZUpgrades.Gui;

public partial class PurchaseMenu
{
    private static CosmeticStateSnapshot _cosmeticState;
    private static string _cosmeticFilter = "ALL";
    private static string _renderedCosCategory;
    private static int _selectedSuitId = -1;
    private static float _nextProviderRefresh;
    private static GameObject _modeRail;

    internal static void TickAppearanceState()
    {
        if (_root == null || Time.unscaledTime < _nextProviderRefresh) return;
        _nextProviderRefresh = Time.unscaledTime + 0.5f;
        CosmeticStateSnapshot next = CosmeticStateService.Capture();
        bool changed = _cosmeticState == null || _cosmeticState.Signature != next.Signature;
        _cosmeticState = next;
        if (!changed) return;
        InvalidateEmployeePreviewModel();
        // A provider may finish loading after the menu opens. Rebuild the rail
        // from the same availability snapshot instead of requiring a reopen.
        RebuildModeRail();
        if (location == nameof(showPlayerLevel)) showPlayerLevel();
        else if (location == nameof(showEmployeeFile)) showEmployeeFile();
    }

    // The cosmetics filters sit CosmeticFilterTop below the content area's top edge;
    // the grid's scroll view starts Space1 under them.
    private const float CosmeticFilterTop = 2f;
    private const float CosmeticFilterH = 32f;
    private const float CosmeticListTop = CosmeticFilterTop + CosmeticFilterH + Space1;

    private static void BuildCosmeticFilters(Transform parent)
    {
        string[] filters = { "ALL", "OWNED", "EQUIPPED" };
        for (int i = 0; i < filters.Length; i++)
        {
            string filter = filters[i];
            bool selected = _cosmeticFilter == filter;
            var (button, _) = BuildSegmentTab("CosmeticFilter_" + filter, parent, filter,
                selected, FontMd, new Vector2(i / 3f, 1), new Vector2((i + 1) / 3f, 1),
                new Vector2(4f, -(CosmeticFilterTop + CosmeticFilterH)), new Vector2(-4f, -CosmeticFilterTop));
            button.onClick.AddListener(() =>
            {
                _cosmeticFilter = filter;
                MenuAudio.PlayClick();
                showPlayerLevel(resetScroll: true);
            });
            RegisterFocusTarget("cosmeticFilter:" + filter, button.GetComponent<RectTransform>(), button, -1, i, null);
        }
    }

    private static void SelectSuitForInspection(int id)
    {
        _selectedSuitId = id;
        MenuAudio.PlayClick();
        showPlayerLevel();
    }

    private static void RenderSuitInspector()
    {
        var suits = StartOfRound.Instance?.unlockablesList?.unlockables;
        if (suits == null || _selectedSuitId < 0 || _selectedSuitId >= suits.Count || suits[_selectedSuitId]?.suitMaterial == null)
        {
            RenderInspectorPlaceholder("SUITS", "Select a suit to inspect it.");
            return;
        }
        ClearDetailViewChildren();
        var suit = suits[_selectedSuitId];
        string name = string.IsNullOrEmpty(suit.unlockableName) ? "Suit " + _selectedSuitId : suit.unlockableName;
        var player = StartOfRound.Instance?.localPlayerController;
        var data = PlayerLevelStore.Get();
        bool equipped = player != null && player.currentSuitID == _selectedSuitId;
        bool owned = equipped || suit.alreadyUnlocked || data.suits.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool ready = SaveKey.TryGetCurrent(out string scope);
        int price = ResolvePurchasePrice(Plugin.SuitPrice.Value);
        var stack = new InspectorStack(_detailView.transform, InspectorTop);
        BuildPlayerPreviewPlaceholder(stack);
        BuildInspectorNameBand(stack, name, name.GetHashCode());
        var statusBand = BuildInspectorBand(stack, "SuitStatusBand", Palette.Alpha(Palette.BgRow, 1f));
        AddInspectorMetric(statusBand.transform, 0, 3, "TYPE", "SUIT", Palette.Body);
        AddInspectorMetric(statusBand.transform, 1, 3, "STATUS",
            equipped ? "EQUIPPED" : owned ? "OWNED" : "LOCKED",
            equipped || owned ? Palette.Gold : Palette.Body);
        AddInspectorMetric(statusBand.transform, 2, 3, "COST",
            owned ? "COMPLETE" : FormatPurchasePrice(price),
            owned || SafeCurrency() >= price ? Palette.Gold : Palette.Warning);
        var status = UI.MakeText("SuitState", _detailView.transform,
            equipped ? "EQUIPPED" : owned ? "OWNED" : "AVAILABLE TO PURCHASE", FontMd,
            equipped || owned ? Palette.Gold : Palette.Body, TextAlignmentOptions.TopLeft);
        stack.Fill(status.rectTransform, 96f, Space5);

        string action = equipped ? "EQUIPPED" : owned ? "EQUIP" : "PURCHASE  " + FormatPurchasePrice(price);
        var (button, buttonLabel) = UI.MakeButton("SuitAction", _detailView.transform, action, FontSm,
            PurchaseButtonBg(ready && !equipped), PurchaseButtonHover(ready && !equipped), Palette.Accent,
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(Space2, Space2), new Vector2(-Space2, InspectorFootH));
        SetPurchaseButtonInteractable(button, buttonLabel, ready && !equipped);
        int capturedId = _selectedSuitId;
        button.onClick.AddListener(() =>
        {
            if (!SaveKey.TryGetCurrent(out string now) || now != scope) { MenuAudio.PlayDeny(); showPlayerLevel(); return; }
            var liveData = PlayerLevelStore.Get();
            bool liveOwned = suit.alreadyUnlocked || liveData.suits.Contains(name, StringComparer.OrdinalIgnoreCase);
            if (liveOwned) { TrySwitchSuit(capturedId); MenuAudio.PlayClick(); }
            else
            {
                if (ResolvePurchasePrice(Plugin.SuitPrice.Value) != price || !TryDeductCurrency(price))
                { MenuAudio.PlayDeny(); return; }
                liveData.suits.Add(name);
                PlayerLevelStore.Save();
                MenuAudio.PlayPurchaseRandom();
            }
            RefreshPlayerLevelAfterCosmeticChange();
        });
        RegisterInspectorAction(button, "suit");
    }
}
