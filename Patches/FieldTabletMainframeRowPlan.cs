using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades.Patches
{
    internal enum FieldTabletMainframeAction
    {
        None,
        EstablishUplink,
        OpenStashCodes,
        ScrapCheck,
        OpenCameras,
        DisableCamera,
        AlarmToggle,
        AlarmSilence,
        Lockdown,
        DisableTraps
    }

    internal struct FieldTabletMainframeRowSpec
    {
        public string Label;
        public string Detail;
        public bool Enabled;
        public FieldTabletMainframeAction Action;
    }

    /// <summary>
    /// Everything the MFRM root rows read, gathered by the runtime once per rebuild. The CCTV
    /// flags (alarm, hack, security, lockdown) are only meaningful when <see cref="CctvPresent"/>
    /// is true; the plan ignores them otherwise.
    /// </summary>
    internal struct FieldTabletMainframeRootInputs
    {
        public bool CctvPresent;
        public int Tier;
        public bool InFacility;
        public bool UplinkEstablished;
        public bool UplinkChannelActive;
        public float UplinkProgress;
        // "[<activate>] 10S", cached by the caller so a rebind is reflected without rebuilding
        // the string on every refresh.
        public string UplinkReadyDetail;
        public string ScrapCheckResult;
        public bool AlarmOn;
        public bool MainframeHacked;
        public bool SecurityAlarmActive;
        public bool LockdownActive;
        public float AlarmCooldownSeconds;
        public float LockdownCooldownSeconds;
        public float TrapsCooldownSeconds;
    }

    /// <summary>
    /// The MFRM root row decision, kept Unity-free so verification/FieldTabletChecks can run it.
    /// Rows that need LethalCCTV (stash codes, cameras, alarm, silence, lockdown) are left out
    /// entirely when the plugin is absent instead of being listed as disabled.
    /// </summary>
    internal static class FieldTabletMainframeRowPlan
    {
        // field_operations is a single-tier upgrade in the current catalog, so the designed
        // T1/T2/T3 split compresses to "everything at tier 1"; the per-row tier plumbing stays so a
        // future multi-tier catalog re-splits cleanly.
        internal const int RequiredTier = 1;

        // Rows are rebuilt every 0.1 s while MFRM is open, so every number-bearing detail comes
        // out of a lazily filled table: a refresh whose numbers did not move allocates nothing.
        private static readonly string RequiredTierDetail = "TIER " + RequiredTier;
        private const int MaxCachedCooldownSeconds = 999;
        private static readonly string[] CooldownDetails = new string[MaxCachedCooldownSeconds + 1];
        private static readonly string[] LinkPercentDetails = new string[101];

        internal static void BuildRoot(in FieldTabletMainframeRootInputs inputs, List<FieldTabletMainframeRowSpec> rows)
        {
            rows.Clear();
            bool cctv = inputs.CctvPresent;

            if (!inputs.UplinkEstablished)
            {
                rows.Add(new FieldTabletMainframeRowSpec
                {
                    Label = "ESTABLISH UPLINK",
                    Detail = inputs.UplinkChannelActive
                        ? FormatLinkPercent((int)Math.Floor(Clamp01(inputs.UplinkProgress) * 100f))
                        : inputs.InFacility ? inputs.UplinkReadyDetail : "NO FACILITY",
                    Enabled = inputs.InFacility && !inputs.UplinkChannelActive,
                    Action = FieldTabletMainframeAction.EstablishUplink
                });
            }

            if (cctv)
                AddRow(rows, inputs, "STASH CODES", FieldTabletMainframeAction.OpenStashCodes, "VIEW");
            AddRow(rows, inputs, "SCRAP CHECK", FieldTabletMainframeAction.ScrapCheck,
                string.IsNullOrEmpty(inputs.ScrapCheckResult) ? "RUN SWEEP" : inputs.ScrapCheckResult);
            if (cctv)
            {
                // F-TABLET-5: the CAMERAS view is a list with a shutdown action; there is no camera
                // feed anywhere in the runtime, and the row used to advertise one.
                AddRow(rows, inputs, "CAMERAS", FieldTabletMainframeAction.OpenCameras, "DISABLE");

                bool alarmCoolingDown = inputs.AlarmCooldownSeconds > 0f;
                AddRow(rows, inputs, inputs.AlarmOn ? "ALARM OFF" : "ALARM ON", FieldTabletMainframeAction.AlarmToggle,
                    !inputs.MainframeHacked ? "NEEDS HACK"
                        : alarmCoolingDown ? "BUSY"
                        : inputs.AlarmOn ? "ACTIVE" : "QUIET",
                    extraLock: alarmCoolingDown || !inputs.MainframeHacked);
                AddRow(rows, inputs, "SILENCE ALARM", FieldTabletMainframeAction.AlarmSilence,
                    alarmCoolingDown ? "BUSY"
                        : inputs.SecurityAlarmActive ? "SEC ACTIVE" : "SEC QUIET",
                    extraLock: alarmCoolingDown);

                bool lockdownCoolingDown = inputs.LockdownCooldownSeconds > 0f;
                AddRow(rows, inputs, inputs.LockdownActive ? "LOCKDOWN END" : "LOCKDOWN BEGIN", FieldTabletMainframeAction.Lockdown,
                    lockdownCoolingDown ? FormatCooldown(inputs.LockdownCooldownSeconds)
                        : inputs.LockdownActive ? "GATES DOWN" : "READY",
                    extraLock: lockdownCoolingDown);
            }

            bool trapsCoolingDown = inputs.TrapsCooldownSeconds > 0f;
            AddRow(rows, inputs, "DISABLE TRAPS", FieldTabletMainframeAction.DisableTraps,
                trapsCoolingDown ? FormatCooldown(inputs.TrapsCooldownSeconds) : "TEMPORARY",
                extraLock: trapsCoolingDown);
        }

        // Disabled-reason precedence: tier, then uplink, then the row's own lock (which keeps its
        // enabled detail so the player sees why, e.g. "BUSY" or "CD 12S").
        private static void AddRow(List<FieldTabletMainframeRowSpec> rows, in FieldTabletMainframeRootInputs inputs,
            string label, FieldTabletMainframeAction action, string enabledDetail, bool extraLock = false)
        {
            string detail = enabledDetail;
            bool enabled = true;

            if (inputs.Tier < RequiredTier)
            {
                enabled = false;
                detail = RequiredTierDetail;
            }
            else if (!inputs.UplinkEstablished)
            {
                enabled = false;
                detail = "NO UPLINK";
            }
            else if (extraLock)
            {
                enabled = false;
            }

            rows.Add(new FieldTabletMainframeRowSpec
            {
                Label = label,
                Detail = detail,
                Enabled = enabled,
                Action = action
            });
        }

        internal static string FormatCooldown(float remainingSeconds)
        {
            int seconds = (int)Math.Ceiling(remainingSeconds);
            if (seconds < 0 || seconds > MaxCachedCooldownSeconds)
                return "CD " + seconds + "S";
            return CooldownDetails[seconds] ?? (CooldownDetails[seconds] = "CD " + seconds + "S");
        }

        private static string FormatLinkPercent(int percent)
        {
            if (percent < 0 || percent >= LinkPercentDetails.Length)
                return "LINK " + percent.ToString("00") + "%";
            return LinkPercentDetails[percent] ?? (LinkPercentDetails[percent] = "LINK " + percent.ToString("00") + "%");
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
                return 0f;
            return value > 1f ? 1f : value;
        }
    }
}
