using System;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Which HUD control block the Field Tablet publishes. <see cref="Stowed"/> is the tablet put
    /// away; every other value names the screen (or the input phase of a screen) the open tablet
    /// is showing (FieldTabletScreenRuntime.PromptState maps its own mode onto these).
    /// </summary>
    internal enum FieldTabletPromptState
    {
        Stowed,
        Boot,
        Scan,
        ScanTarget,
        Hack,
        // The splice result is on screen for the post-solve linger; Back no longer aborts.
        HackComplete,
        Map,
        Drone,
        // Remote pilot link: the drone tab is pinned and only Back (and the stow toggle) act.
        DronePilot,
        DroneCommands,
        MainframeRoot,
        // MFRM root while the uplink channel runs: Back aborts it, every row is disabled.
        MainframeUplink,
        MainframeCameras,
        MainframeStashCodes
    }

    /// <summary>
    /// Display labels for every tablet binding, already resolved from the live keybinds. Plain
    /// strings so the plan stays Unity-free and a console check can drive it.
    /// </summary>
    internal readonly struct FieldTabletPromptLabels : IEquatable<FieldTabletPromptLabels>
    {
        /// <summary>The fallbacks UpgradeInput.DisplayLabel uses for the default bindings.</summary>
        internal static readonly FieldTabletPromptLabels Defaults = new FieldTabletPromptLabels(
            "Y", "LEFT", "RIGHT", "UP", "DOWN", "ENTER", "LMB", "BKSP", "=", "-");

        internal readonly string Tablet;
        internal readonly string PreviousTab;
        internal readonly string NextTab;
        internal readonly string Up;
        internal readonly string Down;
        internal readonly string Activate;
        internal readonly string Primary;
        internal readonly string Back;
        internal readonly string ZoomIn;
        internal readonly string ZoomOut;

        internal FieldTabletPromptLabels(
            string tablet,
            string previousTab,
            string nextTab,
            string up,
            string down,
            string activate,
            string primary,
            string back,
            string zoomIn,
            string zoomOut)
        {
            Tablet = tablet;
            PreviousTab = previousTab;
            NextTab = nextTab;
            Up = up;
            Down = down;
            Activate = activate;
            Primary = primary;
            Back = back;
            ZoomIn = zoomIn;
            ZoomOut = zoomOut;
        }

        public bool Equals(FieldTabletPromptLabels other)
        {
            return string.Equals(Tablet, other.Tablet, StringComparison.Ordinal)
                   && string.Equals(PreviousTab, other.PreviousTab, StringComparison.Ordinal)
                   && string.Equals(NextTab, other.NextTab, StringComparison.Ordinal)
                   && string.Equals(Up, other.Up, StringComparison.Ordinal)
                   && string.Equals(Down, other.Down, StringComparison.Ordinal)
                   && string.Equals(Activate, other.Activate, StringComparison.Ordinal)
                   && string.Equals(Primary, other.Primary, StringComparison.Ordinal)
                   && string.Equals(Back, other.Back, StringComparison.Ordinal)
                   && string.Equals(ZoomIn, other.ZoomIn, StringComparison.Ordinal)
                   && string.Equals(ZoomOut, other.ZoomOut, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is FieldTabletPromptLabels other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (Tablet?.GetHashCode() ?? 0);
                hash = hash * 31 + (PreviousTab?.GetHashCode() ?? 0);
                hash = hash * 31 + (NextTab?.GetHashCode() ?? 0);
                hash = hash * 31 + (Up?.GetHashCode() ?? 0);
                hash = hash * 31 + (Down?.GetHashCode() ?? 0);
                hash = hash * 31 + (Activate?.GetHashCode() ?? 0);
                hash = hash * 31 + (Primary?.GetHashCode() ?? 0);
                hash = hash * 31 + (Back?.GetHashCode() ?? 0);
                hash = hash * 31 + (ZoomIn?.GetHashCode() ?? 0);
                hash = hash * 31 + (ZoomOut?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// The tablet's HUD control lines, one cached block per <see cref="FieldTabletPromptState"/>.
    /// Each block lists only the bindings that screen's input handler in FieldTabletScreenRuntime
    /// acts on, with that screen's own verb. Blocks are rebuilt only when a label changes, so the
    /// per-frame caller gets the same array instances back and allocates nothing.
    /// </summary>
    internal sealed class FieldTabletPromptPlan
    {
        private static readonly int StateCount = Enum.GetValues(typeof(FieldTabletPromptState)).Length;

        private readonly string[][] _blocks = new string[StateCount][];
        private FieldTabletPromptLabels _labels;
        private bool _built;

        /// <summary>Rebuilds every block when <paramref name="labels"/> differs from the last set; true when it did.</summary>
        internal bool SetLabels(in FieldTabletPromptLabels labels)
        {
            if (_built && _labels.Equals(labels))
                return false;

            _labels = labels;
            for (int i = 0; i < _blocks.Length; i++)
                _blocks[i] = Compose((FieldTabletPromptState)i, labels);
            _built = true;
            return true;
        }

        /// <summary>The cached block for <paramref name="state"/>; built from the default labels if none were set.</summary>
        internal string[] GetLines(FieldTabletPromptState state)
        {
            if (!_built)
                SetLabels(FieldTabletPromptLabels.Defaults);
            return _blocks[(int)state];
        }

        private static string[] Compose(FieldTabletPromptState state, in FieldTabletPromptLabels labels)
        {
            string stow = "Tablet: [" + labels.Tablet + "] Stow";
            string tabs = Keys(labels.PreviousTab, labels.NextTab) + " Tabs";
            string choose = Keys(labels.Up, labels.Down) + " Choose";
            string back = "[" + labels.Back + "] Back";
            string keyPair = Keys(labels.ZoomIn, labels.ZoomOut);

            switch (state)
            {
                case FieldTabletPromptState.Stowed:
                    return new[] { "Tablet: [" + labels.Tablet + "]" };
                // The boot screen reads no tablet input; only the stow toggle is live.
                case FieldTabletPromptState.Boot:
                    return new[] { stow };
                // No target in the reticle: activate does nothing until one is acquired.
                case FieldTabletPromptState.Scan:
                    return new[] { stow, tabs };
                // Scan accepts the primary action as activate.
                case FieldTabletPromptState.ScanTarget:
                    return new[] { stow, tabs, Keys(labels.Activate, labels.Primary) + " Splice" };
                case FieldTabletPromptState.Hack:
                    return new[] { stow, tabs, "[" + labels.Back + "] Abort" };
                // The result lingers; Back is ignored, a tab switch still leaves the screen.
                case FieldTabletPromptState.HackComplete:
                    return new[] { stow, tabs };
                // Nothing is selectable on the map: up/down zoom alongside the zoom keys, one line.
                case FieldTabletPromptState.Map:
                    return new[] { stow, tabs, Keys(labels.Up, labels.Down) + " " + keyPair + " Zoom" };
                // The drone feed is the radar centred on the drone, so the zoom keys apply.
                case FieldTabletPromptState.Drone:
                    return new[] { stow, tabs, "[" + labels.Activate + "] Commands", keyPair + " Zoom" };
                // Pilot mode reads only Back, which closes the remote link; the stow toggle also ends it.
                case FieldTabletPromptState.DronePilot:
                    return new[] { stow, "[" + labels.Back + "] Exit link" };
                case FieldTabletPromptState.DroneCommands:
                    return new[] { stow, tabs, choose, "[" + labels.Activate + "] Execute", back };
                case FieldTabletPromptState.MainframeRoot:
                    return new[] { stow, tabs, choose, "[" + labels.Activate + "] Run" };
                // Every row is disabled until the uplink lands, so activate only echoes a detail.
                case FieldTabletPromptState.MainframeUplink:
                    return new[] { stow, tabs, choose, "[" + labels.Back + "] Abort uplink" };
                case FieldTabletPromptState.MainframeCameras:
                    return new[] { stow, tabs, choose, "[" + labels.Activate + "] Shut down", back };
                // Stash code rows are read-only (all disabled): nothing to activate.
                case FieldTabletPromptState.MainframeStashCodes:
                    return new[] { stow, tabs, choose, back };
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, "No tablet prompt block for this state.");
            }
        }

        private static string Keys(string first, string second)
        {
            return "[" + first + "/" + second + "]";
        }
    }
}
