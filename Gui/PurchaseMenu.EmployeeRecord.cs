using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Gui;

public partial class PurchaseMenu
{
    private static readonly HashSet<string> _expandedRecordSections = new HashSet<string>
        { "EMPLOYEE RECORD", "CCTV" };
    private static ScrollRect _performanceScroll;
    private static string _recordAnchorTitle;
    private static float _recordAnchorOffset;
    private static float _recordRestoreY;

    private static PerformanceCluster[] BuildPerformanceClusters(EmployeeStatisticsData stats)
    {
        var sections = new List<PerformanceCluster>
        {
            new PerformanceCluster("EMPLOYEE RECORD", new[]
            {
                new PerformanceStat("stat.scrap", "scavenger", "SCRAP DELIVERED", "$" + stats.scrapValueDelivered.ToString("N0"), null),
                new PerformanceStat("stat.quotas", "quota_guard", "QUOTAS COMPLETED", stats.quotasCompleted.ToString("N0"), null),
                new PerformanceStat("stat.kills", "lethal_hands", "MONSTERS KILLED", stats.monstersKilled.ToString("N0"), null),
                new PerformanceStat("stat.deaths", "deathbound", "DEATHS", stats.timesDied.ToString("N0"), null),
                new PerformanceStat("stat.steps", "light_feet", "STEPS TAKEN", stats.stepsTaken.ToString("N0"), null)
            })
        };
        if (OptionalPluginCapabilities.LethalCctv)
            sections.Add(new PerformanceCluster("CCTV", new[]
            {
                new PerformanceStat("stat.cctv_alarms", "escape_protocol", "ALARMS SET OFF", stats.cctvAlarmsSetOff.ToString("N0"), null),
                new PerformanceStat("stat.cctv_hacks", "turret_hacker", "DEVICES HACKED", stats.cctvDevicesHacked.ToString("N0"), null),
                new PerformanceStat("stat.cctv_time", "better_scanner", "TIME SPENT ON CCTV", EmployeeStatistics.FormatDuration(stats.timeSpentOnCctvSeconds), null)
            }));
        if (OptionalPluginCapabilities.Contracted)
            sections.Add(new PerformanceCluster("CONTRACTED", new[]
            {
                new PerformanceStat("stat.bombs", "escape_protocol", "BOMBS DEFUSED", stats.bombsDefused.ToString("N0"), null),
                new PerformanceStat("stat.payload", "transporter", "PAYLOAD DISTANCE PUSHED", stats.payloadDistancePushedMeters.ToString("N1") + " M", null),
                new PerformanceStat("stat.beacons", "worklight_beacon", "SURVEY BEACONS PLACED", stats.surveyBeaconsPlaced.ToString("N0"), null),
                new PerformanceStat("stat.incinerated", "scavenger", "ITEMS INCINERATED", stats.itemsIncinerated.ToString("N0"), null),
                new PerformanceStat("stat.whistleblower_damage", "lethal_hands", "WHISTLEBLOWER DAMAGE", stats.whistleblowerDamageDealt.ToString("N0"), null),
                new PerformanceStat("stat.pests", "quick_hands", "PESTS TRAPPED", stats.pestsTrapped.ToString("N0"), null),
                new PerformanceStat("stat.breach_waves", "resilience", "CONTAINMENT WAVES SURVIVED", stats.containmentWavesSurvived.ToString("N0"), null),
                new PerformanceStat("stat.drills", "turret_hacker", "DRILLS PLACED", stats.drillsPlaced.ToString("N0"), null),
                new PerformanceStat("stat.breakers", "field_operations", "BREAKERS RESTORED", stats.breakersRestored.ToString("N0"), null)
            }));
        return sections.ToArray();
    }

    private static void BuildPerformanceClusterHeader(Transform parent, string title, float yTop, int row)
    {
        bool expanded = _expandedRecordSections.Contains(title);
        var (button, label) = UI.MakeButton("RecordSection_" + title, parent,
            (expanded ? "[-] " : "[+] ") + title, FontMd, Palette.Alpha(Palette.BgHeader, 0.6f),
            Palette.BgRowHover, Palette.Accent, new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(Space2, -(yTop + RecordClusterHeaderH)), new Vector2(-Space2, -yTop));
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.rectTransform.offsetMin = new Vector2(Space2, 0f);
        if (_recordAnchorTitle == title) _recordRestoreY = Mathf.Max(0f, yTop - _recordAnchorOffset);
        button.onClick.AddListener(() =>
        {
            _recordAnchorTitle = title;
            _recordAnchorOffset = yTop - (_performanceScroll?.content?.anchoredPosition.y ?? 0f);
            if (!_expandedRecordSections.Remove(title)) _expandedRecordSections.Add(title);
            MenuAudio.PlayClick();
            showEmployeeFile();
        });
        RegisterFocusTarget("record:" + title, button.GetComponent<RectTransform>(), button, row, 0, _performanceScroll);
    }
}
