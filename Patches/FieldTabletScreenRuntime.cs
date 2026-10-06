using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    internal static partial class FieldTabletScreenRuntime
    {
        private const string PropInstanceName = "Y4NGZ_Y4NGZ_FPSTabletProp_Instance";
        private const float BootSeconds = 1.05f;
        private const float BootTransitionSeconds = 0.42f;
        // Mainframe uplink: one Enter-initiated 10 s channel per facility entry
        // (replaces the retired 12 s per-target hack hold).
        private const float UplinkSeconds = 10f;
        // Per-target splice on the SCAN tab is an Enter-started channel now.
        private const float TargetSpliceSeconds = 4f;
        private const float HackSolvedLingerSeconds = 1.15f;
        private const float ScanRange = 28f;
        // A target's own collider usually sits a few centimetres proud of the wall or ceiling it is
        // mounted on, and a wall-mounted camera's room collider can register marginally in front of
        // it. Allow that much slack past the opaque hit before a target counts as "behind a wall".
        private const float ScanOcclusionSkinDepth = 0.35f;
        private const float TargetScanInterval = 0.10f;
        private const float TabletDynamicTextRefreshInterval = 0.10f;
        private const float ScreenRenderInterval = 1f / 30f;
        private const float RadarRenderInterval = 1f / 15f;
        private const float RadarCameraHeight = 3.636f;
        // Render-texture mip 0 is sized to the screen's real on-screen footprint instead of being
        // oversized. Lethal Company renders the gameplay camera at 860x520 by default, where the
        // tablet screen occupies ~247x166 real pixels; a 2048-wide RT is 8.3x minification, so the
        // GPU samples mip 3-4 and the mip chain is what you end up reading. Buckets are multiples
        // of 64 in the canvas's own 1.4408:1 aperture ratio (F-TABLET-18: this said 16:10, which
        // the aperture correction in FieldTabletLayout retired) so a rebuild is rare.
        private const int ScreenRtBucketStep = 64;
        private const int ScreenRtMinWidth = 256;
        private const int ScreenRtMaxWidth = 1024;
        private const int ScreenRtDefaultWidth = 640;
        // Sample after the Get animation has settled into the Hold pose, then keep checking so FOV,
        // aspect and LCUltrawide multiplier changes re-tune without a stow/deploy cycle.
        private const float ScreenRtMeasureDelaySeconds = 0.5f;
        private const float ScreenRtMeasureIntervalSeconds = 0.5f;
        // Only shrink once the footprint is clear of the smaller bucket's ceiling, and only after two
        // consecutive samples agree, so a span hovering on a bucket edge cannot ping-pong the RT.
        private const int ScreenRtShrinkGuardPixels = 8;
        // A projection wider or taller than this many camera viewports is the quad passing
        // through the near plane during the Get animation, not a footprint (#500).
        private const float ScreenRtMaxSpanViewportRatio = 1.25f;
        // The black-RT probe waits until the canvas has been built and rendered at least once:
        // the third render after an open or RT rebuild, or the first render this long after open.
        private const int ScreenProbeRenderIndex = 3;
        private const float ScreenProbeMinSecondsAfterOpen = 0.2f;
        private const float ContourMapRetrySeconds = 5f;
        private const int TargetScanHitCapacity = 32;
        private const int RadarTextureSize = 1024;
        private const int ScreenRenderLayer = 5;
        // Fallback overlay depth when the tablet mesh cannot be measured at runtime.
        private const float ScreenLocalDepthFallback = 0.0176f;
        // Lift the exact-fit quad this far off the surface it sits on to avoid z-fighting.
        private const float ScreenSurfaceEpsilon = 0.0008f;
        // The screen is a flat plate recessed 7.9 mm inside the bezel, not the bezel's front face.
        // Parking the quad on the front face floated it 8.7 mm proud of the glass, which at the
        // ~0.25 m viewmodel distance is ~3% of parallax: the render slid across the aperture as the
        // hand moved, so no single rect could fit it from every angle. Sitting the quad on the
        // plate removes the parallax outright, and the bezel then occludes the far edge at grazing
        // angles the way a real recessed screen does. Measured off Tablet.FBX (FPS_Devices pack);
        // see docs/WORKFLOW_MOVEMENT_AND_TABLET_RUNTIME.md for the derivation.
        private const float ScreenPlaneLocalDepth = 0.00601f;
        // Half-depth of that same mesh. ResolveScreenLocalDepth reads it back from mesh bounds, so
        // it doubles as the fingerprint that says the bound prop is still the mesh these numbers
        // came from; if it is not, the baked plate depth cannot be trusted and the front face wins.
        private const float TabletMeshLocalHalfDepth = 0.013924f;
        private const float TabletMeshHalfDepthTolerance = 0.0015f;
        private const float ScreenSelectionAlpha = 0.24f;
        // Ten segments across the telemetry column is ~18 real pixels each with sub-pixel gaps at
        // vanilla resolution, so the bar reads as a solid block. Five segments read as five.
        private const int DroneTelemetrySegments = 5;
        // F-TABLET-18: the client-side cooldown mirrors read the same constants the host enforces
        // (FieldTabletCooldowns); they used to be a second, independent copy of the three numbers.
        private const int MainframeVisibleRows = FieldTabletLayout.VisibleRows;
        private const float WorldReticleDistance = 0.8f;
        private const float WorldReticleBasePixels = 58f;

        private static readonly Vector3 ScreenRenderRigPosition = new Vector3(0f, -9500f, 250f);
        // Baked screen aperture in prop-local meters. The authored tablet mesh has a single
        // material, no screen submesh and isReadable = 0, so the aperture cannot be measured at
        // runtime the way the depth can. It is instead measured off the source mesh
        // (Tablet.FBX in the FPS_Devices pack) and baked: the screen is the recessed flat plate,
        // which spans mesh X [-11.893, 9.435] and Z [3.489, 18.292] centimetres. Plugin's
        // TabletScreenOffsetX/Y/Width/Height default to these and are applied live, so the rect
        // can still be nudged without a rebuild if the prop prefab is ever rebuilt.
        // The plate is off-centre on the tablet by 12.3 mm, which is the whole of the "empty strip
        // down one side" symptom: the old rect was centred 20 mm the other way and 27 mm too wide,
        // so it overhung the bezel on one side and left bare glass on the other.
        private static readonly Vector3 ScreenLocalPosition = new Vector3(0.01229f, 0.108905f, 0f);
        private static readonly Vector3 ScreenLocalScale = new Vector3(0.21328f, 0.14803f, 1f);
        // Contrast budget. Each color is alpha-composited over ScreenPanel and measured as a WCAG
        // relative-luminance ratio; primary text needs 7:1, secondary 4.5:1. The measured ratios
        // are recorded in docs/WORKFLOW_MOVEMENT_AND_TABLET_RUNTIME.md - re-derive them if a value
        // changes. Do not drop the alpha of a text color to "de-emphasise" it: ScreenDim at 0.72
        // measures 3.39:1 and fails the gate that the same color at 0.95 passes.
        private static readonly Color ScreenGreen = new Color(0.13f, 1f, 0.18f, 0.95f);   // 13.00:1
        private static readonly Color ScreenDim = new Color(0.10f, 0.62f, 0.16f, 0.95f);  //  5.13:1
        private static readonly Color ScreenPanel = new Color(0.012f, 0.055f, 0.03f, 1f); // background
        private static readonly Color ScreenAmber = new Color(1f, 0.70f, 0.26f, 0.95f);   //  9.94:1
        private static readonly Color ScreenCyan = new Color(0.30f, 0.95f, 1f, 0.92f);    // 12.27:1
        private static readonly Color ScreenRed = new Color(1f, 0.26f, 0.22f, 0.95f);     //  5.20:1

        private enum TabletScreenMode
        {
            Boot,
            Scan,
            Radar,
            Drone,
            Command,
            Hack,
            Mainframe
        }

        private enum MainframeView
        {
            Root,
            StashCodes,
            Cameras
        }

        private struct MainframeRow
        {
            public string Label;
            public string Detail;
            public bool Enabled;
            public FieldTabletMainframeAction Action;
            public int CameraId;
        }

        private enum TabletHackTargetKind
        {
            None,
            Mainframe,
            CompanyStash,
            CctvCamera,
            Turret,
            LockedDoor
            // F-TABLET-3: DronePickup is gone. It had no handler in TryNotifyHackSuccess, yet it
            // claimed every GrabbableObject in the crosshair whenever Courier Drone was owned, so
            // all the scrap in the level lit the reticle and spliced into nothing - and it masked
            // the mainframe/camera/door fallbacks behind it. The drone's real pickup designation
            // is the separate CourierDroneCommand pointer path, not this splice.
        }

        private struct TabletHackTarget
        {
            public TabletHackTargetKind Kind;
            public Component Component;
            public GameObject Root;
            public string Label;
            public float Distance;

            public bool IsValid => Kind != TabletHackTargetKind.None && (Component != null || Root != null);
        }

        private struct VanillaMapUiState
        {
            public bool MapScreenPlayerNameEnabled;
            public bool MapScreenPlayerNameBgEnabled;
            public bool HeadMountedCamUiEnabled;
            public bool LocalPlayerPlaceholderEnabled;
            public bool CompassRoseEnabled;
            public bool ShipArrowUiActive;
            public bool ShipIconActive;
            public bool LostSignalUiActive;
            public bool ExitLineEnabled;
        }

        internal struct WorldReticleRequest
        {
            internal bool Show;
            internal Color Color;
            internal float Size;
            internal float FlashUntil;
            internal string Label;
        }

        private static TabletScreenMode _mode = TabletScreenMode.Boot;
        private static TabletHackTarget _currentTarget;
        private static TabletHackTarget _hackTarget;
        private static VanillaMapUiState _vanillaMapUiState;
        private static float _openedAt;
        private static float _nextTargetScanAt;
        private static float _nextDynamicTextRefreshAt;
        private static float _nextScreenRenderAt;
        private static float _nextRadarRenderAt;
        private static float _radarZoom = 1f;
        private static float _hackProgress;
        private static float _hackSolvedAt;
        private static bool _hackHoldActive;
        private static bool _lastTargetWasValid;
        private static bool _radarRendered;
        private static bool _hasRenderedScreenTexture;
        private static TabletScreenMode _lastRadarRenderedMode = TabletScreenMode.Boot;
        private static bool _screenBindingDiagnosticsLogged;
        private static bool _screenRenderDiagnosticsLogged;
        private static int _screenRendersSinceRebind;
        // Set by every write that can move the screen canvas layout (text, font style, active
        // state, rig build, RT size, aperture rect); RenderScreenTexture rebuilds layout only then.
        private static bool _screenLayoutDirty = true;
        private static bool _screenRtRejectLoggedThisOpen;
        private static float _contourMapRetryAt;
        private static bool _screenMissingPropDiagnosticsLogged;
        private static string _statusLine = "SCAN READY";
        private static string _hackResult = string.Empty;
        // Hack-view status line once the splice resolves: empty reads LINKED, otherwise the
        // host's refusal reason (too long for the 14-character readout).
        private static string _hackStatusDetail = string.Empty;
        private static bool _hackResultFailed;
        // A SCAN-tab camera splice is a fire-and-forget request; its ActionCamera result arrives
        // later through OnMainframeActionResult and is reported against the splice.
        private static bool _spliceCameraRequestPending;
        // Rebind-aware on-screen key text. The binding paths are compared each refresh (no
        // allocation) and the strings rebuilt only when a path changes; null forces a rebuild.
        private static string _activateBindingPath;
        private static string _uplinkReadyDetail = string.Empty;
        private static string _cameraOffDetail = string.Empty;
        private static string _zoomInBindingPath;
        private static string _zoomOutBindingPath;
        private static WorldReticleRequest _dronePointerReticle;
        private static float _worldReticleAlpha;
        private static GameObject _lastScanTargetRoot;
        // Raw target label last fitted, and the fitted upper-case name drawn before the distance.
        private static string _lastScanTargetLabel = string.Empty;
        private static string _lastScanTargetName = string.Empty;
        private static int _lastScanTargetDistanceTenths = int.MinValue;
        private static int _lastRadarZoomTenths = int.MinValue;
        private static int _lastHackPercent = int.MinValue;
        private static string _lastHackTargetLabel = string.Empty;
        private static string _lastCommandTelemetry = string.Empty;
        private static int _commandWindowStart;
        private static string _lastCommandStatusHint = string.Empty;
        // Inputs the command hint and telemetry lines were last built from, so the 0.1 s refresh
        // allocates only when one of them moved.
        private static string _commandHintBase;
        private static bool _commandHintHasDrone;
        private static string _commandHintBindingPath;
        private static bool _commandTelemetryHasDrone;
        private static int _commandTelemetryHealth = int.MinValue;
        private static int _commandTelemetryCargo = int.MinValue;
        private static string _commandTelemetryStatus;
        // Command entries are re-read from CourierDronePatch at the text refresh interval; input
        // handling re-reads them itself on a press.
        private static int _commandEntryCount;
        private static float _nextCommandEntriesRefreshAt;

        private static GameObject _screenQuad;
        private static GameObject _screenQuadBack;
        private static GameObject _screenRigRoot;
        private static GameObject _screenCanvasRoot;
        private static GameObject _playerReticleRoot;
        private static TextMeshProUGUI _worldReticleLabel;
        // Cached when the reticle is built and cleared in Destroy, so UpdateWorldReticle makes no
        // GetComponent calls; the last-applied values skip writes that would change nothing.
        private static Canvas _playerReticleCanvas;
        private static CanvasGroup _playerReticleGroup;
        private static RectTransform _playerReticleRect;
        private static Camera _playerReticleCamera;
        private static float _playerReticleAppliedAlpha = float.NaN;
        private static float _playerReticleAppliedSize = float.NaN;
        private static float _playerReticleAppliedScale = float.NaN;
        private static Camera _screenCamera;
        private static Camera _radarCamera;
        private static Camera _radarSourceCamera;
        private static RenderTexture _screenRenderTexture;
        private static RenderTexture _radarTexture;
        private static Material _screenMaterial;
        private static string _screenMaterialSource = "<none>";

        private static Sprite _pixelSprite;
        private static TMP_FontAsset _font;
        // Set by ProbeEllipsisGlyph when the screen font resolves; ASCII Truncate until then.
        private static TMP_FontAsset _ellipsisProbedFont;
        private static bool _ellipsisGlyphSupported;

        private static CanvasGroup _bootGroup;
        private static CanvasGroup _scanGroup;
        private static CanvasGroup _radarGroup;
        private static CanvasGroup _droneGroup;
        private static CanvasGroup _commandGroup;
        private static CanvasGroup _hackGroup;
        private static RectTransform _bootFill;
        private static TextMeshProUGUI _bootTitle;
        private static TextMeshProUGUI _scanStatus;
        private static TextMeshProUGUI _scanTarget;
        private static TextMeshProUGUI _scanLinkReady;
        private static GameObject _scanLinkGlyph;
        private static Image _scanCenterDot;
        private static TextMeshProUGUI _radarStatus;
        private static TextMeshProUGUI _radarZoomHint;
        private static TextMeshProUGUI _droneStatusValue;
        private static TextMeshProUGUI _droneFeedFallback;
        private static RawImage _droneFeedImage;
        private static Image _droneMarker;
        private static readonly List<Image> DroneHpSegments = new List<Image>();
        private static readonly List<Image> DroneCargoSegments = new List<Image>();
        private static TextMeshProUGUI _commandStatusLine;
        private static TextMeshProUGUI _commandTelemetryLine;
        private static TextMeshProUGUI _commandPageText;
        private static readonly List<Image> CommandSelectors = new List<Image>();
        private static readonly List<TextMeshProUGUI> CommandMarkers = new List<TextMeshProUGUI>();
        private static readonly List<TextMeshProUGUI> CommandLabels = new List<TextMeshProUGUI>();
        private static readonly List<TextMeshProUGUI> CommandReasons = new List<TextMeshProUGUI>();
        private static readonly CourierDronePatch.TabletCommandEntry[] CommandEntries = new CourierDronePatch.TabletCommandEntry[6];
        private static readonly string[] LastCommandEntryReasons = new string[CommandEntries.Length];
        private static readonly bool[] LastCommandEntryAvailable = new bool[CommandEntries.Length];
        private static TextMeshProUGUI _hackStatusText;
        private static TextMeshProUGUI _hackTargetText;
        private static TextMeshProUGUI _hackPercentText;
        private static TextMeshProUGUI _hackHintText;
        private static RectTransform _scanReticleRoot;
        private static RectTransform _radarMarkerLayer;
        private static RectTransform _hackProgressFill;
        private static RawImage _radarImage;
        private static Image _rollingScanline;
        private static Image _scanPulse;
        private static readonly List<Image> ScanReticleLines = new List<Image>();
        private static readonly List<Image> PlayerReticleLines = new List<Image>();
        private static readonly List<Image> RadarMarkers = new List<Image>();
        private static readonly Dictionary<TMP_Text, string> CachedTextValues = new Dictionary<TMP_Text, string>();
        private static readonly Dictionary<TMP_Text, Color> CachedTextColors = new Dictionary<TMP_Text, Color>();
        private static readonly Dictionary<TMP_Text, FontStyles> CachedFontStyles = new Dictionary<TMP_Text, FontStyles>();
        private static readonly Dictionary<Graphic, Color> CachedGraphicColors = new Dictionary<Graphic, Color>();
        private static readonly Dictionary<GameObject, bool> CachedActiveStates = new Dictionary<GameObject, bool>();
        private static TabletScreenMode _modeBeforeCommand = TabletScreenMode.Scan;
        private static int _commandSelection;

        // - Mainframe uplink state (local-authoritative; host validates tier on actions) -
        private static bool _uplinkEstablished;
        private static bool _uplinkChannelActive;
        private static float _uplinkProgress;
        private static MainframeView _mainframeView = MainframeView.Root;
        private static int _mainframeSelection;
        private static int _mainframeWindowStart;
        private static string _mainframeStatus = "MAINFRAME";
        private static string _scrapCheckResult = string.Empty;
        private static float _cameraDisableReadyAt;
        private static float _lockdownReadyAt;
        private static float _trapsReadyAt;
        private static float _alarmReadyAt;
        private static readonly List<MainframeRow> MainframeRows = new List<MainframeRow>();
        private static readonly List<FieldTabletMainframeRowSpec> MainframeRootSpecs = new List<FieldTabletMainframeRowSpec>();
        private static readonly List<int> MainframeCameraIdBuffer = new List<int>();
        // Fitted, upper-cased camera row labels keyed by camera id, rebuilt only when the
        // provider's label for that id changes.
        private static readonly Dictionary<int, KeyValuePair<string, string>> CameraRowLabels = new Dictionary<int, KeyValuePair<string, string>>();
        private static readonly Dictionary<int, string> StashCodeLabels = new Dictionary<int, string>();
        // Lazily filled number strings for the per-refresh readouts (#500): once a value has been
        // shown, showing it again allocates nothing.
        private static readonly Dictionary<int, string> PageTexts = new Dictionary<int, string>();
        private static readonly string[] ScanDistanceSuffixes = new string[Mathf.CeilToInt(ScanRange * 10f) + 1];
        private static readonly string[] RadarZoomTexts = new string[41];
        private static readonly string[] HackPercentTexts = new string[101];
        private static readonly RaycastHit[] TargetScanHits = new RaycastHit[TargetScanHitCapacity];
        private static readonly RaycastHitDistanceComparer TargetScanHitComparer = new RaycastHitDistanceComparer();

        private sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        private static CanvasGroup _mainframeGroup;
        private static CanvasGroup _tabBarGroup;
        private static TextMeshProUGUI _mainframeStatusText;
        private static TextMeshProUGUI _mainframePageText;
        private static RectTransform _uplinkProgressFill;
        private static GameObject _uplinkProgressRoot;
        private static readonly List<Image> MainframeSelectors = new List<Image>();
        private static readonly List<TextMeshProUGUI> MainframeMarkers = new List<TextMeshProUGUI>();
        private static readonly List<TextMeshProUGUI> MainframeLabels = new List<TextMeshProUGUI>();
        private static readonly List<TextMeshProUGUI> MainframeDetails = new List<TextMeshProUGUI>();
        private static readonly List<TextMeshProUGUI> TabLabels = new List<TextMeshProUGUI>();
        private static readonly List<Image> TabUnderlines = new List<Image>();

        private static float _nextMainframeRowsRefreshAt;

        // Transform.name allocates a new string per read, and the scan reads it for every hit ten
        // times a second; the answer per transform is cached until the tablet is destroyed.
        private static readonly Dictionary<int, bool> FieldTabletNamedTransforms = new Dictionary<int, bool>();

        // F-TABLET-9/-11: the three Company behaviours the splice can target, resolved once each
        // and then reached with GetComponentInParent(Type) instead of a name walk. The old
        // FindComponentByTypeName path matched against the GameObject's *name* as well as the type,
        // so LethalCCTV's "LethalCCTV_CameraScanNode" child made every component on that object a
        // match for the ("CCTV", "Camera") fragment pair - and GetComponents<Component>() returns
        // Transform first, so the splice regularly captured a Transform and then found nothing to
        // invoke. The name walk survives only as a fallback for the type not resolving.
        private const string MainframeTypeName = "Y4NGZCompany.Facility.Mainframe.MainframeSupport";
        private const string CompanyStashTypeName = "Y4NGZCompany.Facility.Stash.CompanyStashController";
        private const string CctvCameraTypeName = "Y4NGZCompany.Facility.Cameras.CCTVCamera";
        private static readonly Dictionary<string, Type> ResolvedTargetTypes = new Dictionary<string, Type>();

        // Review pass: how long a failed probe stands before it is retried. See ResolveTargetType.
        private const float TargetTypeRetrySeconds = 5f;
        private static readonly Dictionary<string, float> TargetTypeRetryAt = new Dictionary<string, float>();

        // Player-crosshair canvas, not the tablet screen: 58 pixels square, so it has its own scale.
        private const float WorldReticleLabelFontSize = 10f;

        private static float _measuredScreenLocalDepth = ScreenLocalDepthFallback;
        private static readonly Vector4 InvalidScreenRect = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        private static Vector4 _appliedScreenRect = InvalidScreenRect;
        private static GameObject _calibrationRoot;
        private static TextMeshProUGUI _calibrationOffsetReadout;
        private static TextMeshProUGUI _calibrationSizeReadout;

        private static int _screenRtWidth = ScreenRtDefaultWidth;
        private static int _pendingShrinkWidth;
        private static bool _screenRtMeasured;
        private static float _nextScreenRtMeasureAt;
        private static bool _screenRtLoggedThisOpen;

        // F-TABLET-11: UpdateRadarMarkers ran a full-scene FindObjectsOfType<EntranceTeleport>()
        // on every radar render (15 Hz) - and it is the single most expensive call on the MAP tab
        // in a large interior. Entrances are spawned with the dungeon and do not move, so the list
        // is cached for the round: the cache is dropped when StartOfRound changes (new round /
        // scene reload) or when any cached entry has been destroyed under us.
        private static EntranceTeleport[] _cachedEntrances;
        private static StartOfRound _cachedEntranceRound;

        internal static bool IsUplinkEstablished => _uplinkEstablished;

        // The HUD prompt block for the screen currently shown. FieldOperationsTabletPatch only
        // reads it while the tablet is deployed, and handles the stowed state itself. Each branch
        // mirrors what HandleTabletInputs reads in that phase.
        internal static FieldTabletPromptState PromptState
        {
            get
            {
                // Pilot mode pins the drone tab and reads only Back, ahead of the per-mode reads.
                if (CourierDronePatch.IsLocalPilotModeActive)
                    return FieldTabletPromptState.DronePilot;

                switch (_mode)
                {
                    case TabletScreenMode.Boot: return FieldTabletPromptState.Boot;
                    case TabletScreenMode.Hack:
                        return _hackSolvedAt > 0f ? FieldTabletPromptState.HackComplete : FieldTabletPromptState.Hack;
                    case TabletScreenMode.Radar: return FieldTabletPromptState.Map;
                    case TabletScreenMode.Drone: return FieldTabletPromptState.Drone;
                    case TabletScreenMode.Command: return FieldTabletPromptState.DroneCommands;
                    case TabletScreenMode.Mainframe:
                        // HandleMainframeInputs spends Back on aborting a running channel first.
                        if (_uplinkChannelActive)
                            return FieldTabletPromptState.MainframeUplink;
                        switch (_mainframeView)
                        {
                            case MainframeView.Cameras: return FieldTabletPromptState.MainframeCameras;
                            case MainframeView.StashCodes: return FieldTabletPromptState.MainframeStashCodes;
                            default: return FieldTabletPromptState.MainframeRoot;
                        }
                    default:
                        return _currentTarget.IsValid ? FieldTabletPromptState.ScanTarget : FieldTabletPromptState.Scan;
                }
            }
        }

        internal static void OnTabletOpened()
        {
            _openedAt = Time.unscaledTime;
            _mode = TabletScreenMode.Boot;
            _radarZoom = 1f;
            _commandSelection = 0;
            _commandWindowStart = 0;
            _modeBeforeCommand = TabletScreenMode.Scan;
            _nextTargetScanAt = 0f;
            _nextDynamicTextRefreshAt = 0f;
            _nextScreenRenderAt = 0f;
            _nextRadarRenderAt = 0f;
            _screenRtMeasured = false;
            _screenRtLoggedThisOpen = false;
            _pendingShrinkWidth = 0;
            _screenRtRejectLoggedThisOpen = false;
            _screenRendersSinceRebind = 0;
            _screenLayoutDirty = true;
            _nextCommandEntriesRefreshAt = 0f;
            _nextScreenRtMeasureAt = _openedAt + ScreenRtMeasureDelaySeconds;
            _radarRendered = false;
            _hasRenderedScreenTexture = false;
            _lastRadarRenderedMode = TabletScreenMode.Boot;
            _screenBindingDiagnosticsLogged = false;
            _screenRenderDiagnosticsLogged = false;
            _screenMissingPropDiagnosticsLogged = false;
            _statusLine = "SCAN READY";
            _currentTarget = default;
            _mainframeView = MainframeView.Root;
            _mainframeSelection = 0;
            _mainframeWindowStart = 0;
            _uplinkChannelActive = false;
            _uplinkProgress = 0f;
            // A splice request left unanswered by an earlier open must not claim the next camera
            // result, and a MAP miss in orbit must not hide the moon contour after landing.
            _spliceCameraRequestPending = false;
            _contourMapRetryAt = 0f;
            ResetDynamicTextState();
            FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
            StopHack("tablet-opened");
        }

        /// <summary>
        /// Runs every frame for the local player (tablet deployed or stowed) so the
        /// per-facility-entry uplink drops the moment the player leaves the facility
        /// or dies. Late-joiners start with the default (no uplink).
        /// </summary>
        internal static void TickUplinkState(PlayerControllerB player)
        {
            if (player == null)
                return;

            if (player.isPlayerDead || !player.isInsideFactory)
            {
                if (_uplinkEstablished)
                {
                    _uplinkEstablished = false;
                    _mainframeStatus = "UPLINK LOST";
                }
                if (_uplinkChannelActive)
                {
                    _uplinkChannelActive = false;
                    _uplinkProgress = 0f;
                    _mainframeStatus = "UPLINK ABORTED";
                }
            }
        }

        internal static void ResetUplinkState()
        {
            _uplinkEstablished = false;
            _uplinkChannelActive = false;
            _uplinkProgress = 0f;
            _mainframeStatus = "MAINFRAME";
            _scrapCheckResult = string.Empty;
            ResetMainframeCooldownMirrors();
            _spliceCameraRequestPending = false;
            _contourMapRetryAt = 0f;
        }

        /// <summary>
        /// Zeroes the client mirrors of the host's per-function cooldowns. Also called at every
        /// round start (FieldTabletMainframeNet.OnRoundStarted) when the host drops its own, so the
        /// tablet never shows a cooldown the host no longer enforces.
        /// </summary>
        internal static void ResetMainframeCooldownMirrors()
        {
            _cameraDisableReadyAt = 0f;
            _lockdownReadyAt = 0f;
            _trapsReadyAt = 0f;
            _alarmReadyAt = 0f;
        }

        /// <summary>
        /// Host/relay result feedback for every mainframe action.
        /// F-TABLET-4: the cooldown mirrors are set from here rather than optimistically at press
        /// time. The host cooldowns are global (one per facility function, shared by the crew)
        /// while these mirrors are per client, so a press that the host refuses because another
        /// player just used the same function must not start this client's own 90 s countdown -
        /// which is exactly what it used to do, under a screen that said "TRAP SHUTDOWN SENT".
        /// </summary>
        internal static void OnMainframeActionResult(byte action, bool success, float value, int count, byte reason)
        {
            float now = Time.unscaledTime;
            switch (action)
            {
                case FieldTabletMainframeNet.ActionLockdownBegin:
                case FieldTabletMainframeNet.ActionLockdownEnd:
                    // Review pass: HandleLockdown burns the host's shared LockdownSeconds BEFORE it
                    // knows whether HostTryLockdown landed, so a ReasonRefused result (the bridge
                    // missed, or the gates were already in the requested state) left the host on a
                    // live cooldown while this row still read READY - the same host/mirror
                    // divergence the ActionTraps case below was written to avoid. Only the
                    // refusals the host returns on *before* the burn keep the mirror where it was.
                    if (reason == FieldTabletMainframeNet.ReasonCooldown)
                        _lockdownReadyAt = now + Mathf.Max(0f, value);
                    else if (reason != FieldTabletMainframeNet.ReasonNoTier
                             && reason != FieldTabletMainframeNet.ReasonNoCompany)
                        _lockdownReadyAt = now + FieldTabletCooldowns.LockdownSeconds;
                    _mainframeStatus = success
                        ? (action == FieldTabletMainframeNet.ActionLockdownBegin ? "LOCKDOWN ENGAGED" : "GATES OPENED")
                        : DescribeRejection(reason,
                            action == FieldTabletMainframeNet.ActionLockdownBegin
                                ? "LOCKDOWN UNAVAILABLE"
                                : "GATES HELD BY ANOTHER SYSTEM");
                    break;
                case FieldTabletMainframeNet.ActionTraps:
                    // The host burns the traps cooldown whenever it reaches the sweep, found
                    // traps or not, so only an outright cooldown refusal keeps the shorter timer.
                    if (reason == FieldTabletMainframeNet.ReasonCooldown)
                        _trapsReadyAt = now + Mathf.Max(0f, value);
                    else if (reason != FieldTabletMainframeNet.ReasonNoTier)
                        _trapsReadyAt = now + FieldTabletCooldowns.TrapsDisableSeconds;
                    _mainframeStatus = success
                        ? "TRAPS OFFLINE " + count + " FOR ~" + value.ToString("0") + "S"
                        : DescribeRejection(reason, "NO ACTIVE TRAPS FOUND");
                    break;
                case FieldTabletMainframeNet.ActionCamera:
                    if (success)
                        _cameraDisableReadyAt = now + FieldTabletCooldowns.CameraDisableSeconds;
                    else if (reason == FieldTabletMainframeNet.ReasonCooldown)
                        _cameraDisableReadyAt = now + Mathf.Max(0f, value);
                    _mainframeStatus = success
                        ? "CAMERA OFFLINE"
                        : DescribeRejection(reason, "CAMERA SHUTDOWN REFUSED");
                    // ActionCamera carries no camera id, so the first camera result after a SCAN
                    // splice request is that splice's answer.
                    if (_spliceCameraRequestPending)
                    {
                        _spliceCameraRequestPending = false;
                        ReportSpliceCameraResult(success, _mainframeStatus);
                    }
                    break;
                case FieldTabletMainframeNet.ActionAlarm:
                    // Review pass: HandleAlarm burns the host's AlarmSeconds unconditionally once
                    // it gets past the entry guards - deliberately, since SetAlarmServerRpc is
                    // RequireOwnership = false and a refused request still has to cost its rate
                    // limit - so a ReasonRefused result (no live MainframeSupport) left the host
                    // on a live cooldown while this row read READY. Same shape as the lockdown and
                    // ActionTraps cases: only the refusals the host returns on *before* the burn
                    // (no tier, cooldown, no Company, mainframe not hacked) keep the mirror put.
                    if (reason == FieldTabletMainframeNet.ReasonCooldown)
                        _alarmReadyAt = now + Mathf.Max(0f, value);
                    else if (reason != FieldTabletMainframeNet.ReasonNoTier
                             && reason != FieldTabletMainframeNet.ReasonNoCompany
                             && reason != FieldTabletMainframeNet.ReasonNotHacked)
                        _alarmReadyAt = now + FieldTabletCooldowns.AlarmSeconds;
                    _mainframeStatus = success
                        ? (count == FieldTabletMainframeNet.AlarmOpSilence ? "ALARM SILENCED" : "ALARM SET")
                        : DescribeRejection(reason, "ALARM REFUSED");
                    break;
            }
        }

        private static string DescribeRejection(byte reason, string fallback)
        {
            switch (reason)
            {
                case FieldTabletMainframeNet.ReasonNoTier: return "NO CLEARANCE";
                case FieldTabletMainframeNet.ReasonCooldown: return "SYSTEM COOLING DOWN";
                case FieldTabletMainframeNet.ReasonNoCompany: return "HOST LACKS LETHALCCTV";
                case FieldTabletMainframeNet.ReasonNotHacked: return "NEEDS MAINFRAME HACK";
                default: return fallback;
            }
        }

        // The readout keeps to its 14-character budget; the host's longer reason goes on the
        // hack status line, or on the SCAN status line once the hack view has closed.
        private static void ReportSpliceCameraResult(bool success, string reason)
        {
            if (_mode == TabletScreenMode.Hack && _hackHoldActive && _hackTarget.Kind == TabletHackTargetKind.CctvCamera)
            {
                _hackResult = success ? "ACCESS GRANTED" : "ACCESS DENIED";
                _hackResultFailed = !success;
                _hackStatusDetail = success ? string.Empty : reason;
                return;
            }

            _statusLine = success ? "ACCESS GRANTED" : reason;
        }

        internal static void UpdateActive(PlayerControllerB player, Action fireTabletActionGesture)
        {
            if (player == null)
                return;

            long startedAt = FieldTabletPerfMeter.Timestamp();
            EnsureScreenOverlay(player);
            UpdateScreenRenderTextureSize(player);
            long lap = FieldTabletPerfMeter.Lap(FieldTabletPerfMeter.Section.RtMeasure, startedAt);
            UpdateBootState();
            RefreshHackableTargetScan(player);
            lap = FieldTabletPerfMeter.Lap(FieldTabletPerfMeter.Section.Scan, lap);
            HandleTabletInputs(player, fireTabletActionGesture);
            lap = FieldTabletPerfMeter.Lap(FieldTabletPerfMeter.Section.Input, lap);
            UpdatePlayerCrosshair(player);
            lap = FieldTabletPerfMeter.Lap(FieldTabletPerfMeter.Section.Reticle, lap);
            RenderActiveScreen(player);
            FieldTabletPerfMeter.Lap(FieldTabletPerfMeter.Section.Views, lap);
            FieldTabletPerfMeter.AddScreenFrame(startedAt);
        }

        internal static void Destroy()
        {
            FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
            StopHack("tablet-closed");
            _spliceCameraRequestPending = false;
            FieldTabletAudio.Unbind();
            DestroyObject(_screenQuad);
            DestroyObject(_screenQuadBack);
            DestroyObject(_screenRigRoot);
            DestroyObject(_playerReticleRoot);
            DestroyObject(_radarCamera != null ? _radarCamera.gameObject : null);

            DestroyObject(_screenMaterial);

            if (_screenRenderTexture != null)
            {
                _screenRenderTexture.Release();
                DestroyObject(_screenRenderTexture);
            }
            if (_radarTexture != null)
            {
                _radarTexture.Release();
                DestroyObject(_radarTexture);
            }

            _screenQuad = null;
            _screenQuadBack = null;
            _screenRigRoot = null;
            _screenCanvasRoot = null;
            _playerReticleRoot = null;
            _worldReticleLabel = null;
            _playerReticleCanvas = null;
            _playerReticleGroup = null;
            _playerReticleRect = null;
            _playerReticleCamera = null;
            _playerReticleAppliedAlpha = float.NaN;
            _playerReticleAppliedSize = float.NaN;
            _playerReticleAppliedScale = float.NaN;
            _screenCamera = null;
            _radarCamera = null;
            _radarSourceCamera = null;
            _screenRenderTexture = null;
            _radarTexture = null;
            _screenMaterial = null;
            _screenMaterialSource = "<none>";

            _bootGroup = null;
            _scanGroup = null;
            _radarGroup = null;
            _droneGroup = null;
            _hackGroup = null;
            _radarImage = null;
            _droneFeedImage = null;
            _droneMarker = null;
            _droneStatusValue = null;
            _droneFeedFallback = null;
            _commandGroup = null;
            _commandStatusLine = null;
            _commandTelemetryLine = null;
            _commandPageText = null;
            _mainframePageText = null;
            _scanLinkReady = null;
            _radarZoomHint = null;
            _scanLinkGlyph = null;
            _scanCenterDot = null;
            _mainframeGroup = null;
            _tabBarGroup = null;
            _mainframeStatusText = null;
            _uplinkProgressFill = null;
            _uplinkProgressRoot = null;
            _calibrationRoot = null;
            _calibrationOffsetReadout = null;
            _calibrationSizeReadout = null;
            _appliedScreenRect = InvalidScreenRect;
            DroneHpSegments.Clear();
            DroneCargoSegments.Clear();
            CommandSelectors.Clear();
            CommandMarkers.Clear();
            CommandLabels.Clear();
            CommandReasons.Clear();
            MainframeSelectors.Clear();
            MainframeMarkers.Clear();
            MainframeLabels.Clear();
            MainframeDetails.Clear();
            TabLabels.Clear();
            TabUnderlines.Clear();
            ClearUiWriteCaches();
            _screenBindingDiagnosticsLogged = false;
            _screenRenderDiagnosticsLogged = false;
            _screenMissingPropDiagnosticsLogged = false;
            _radarMarkerLayer = null;
            _hackProgressFill = null;
            _hackStatusText = null;
            _hackTargetText = null;
            _hackPercentText = null;
            _hackHintText = null;
            ScanReticleLines.Clear();
            PlayerReticleLines.Clear();
            RadarMarkers.Clear();
            FieldTabletNamedTransforms.Clear();
            _currentTarget = default;
            _hackTarget = default;
            ResetDynamicTextState();
        }

        private static void SetGroup(CanvasGroup group, bool active, float alpha)
        {
            if (group == null)
                return;
            SetActiveIfChanged(group.gameObject, active);
            if (!Mathf.Approximately(group.alpha, alpha))
                group.alpha = alpha;
        }

        private static string ResolveDisplayName(Component component, string fallback)
        {
            if (component == null)
                return fallback;
            PropertyInfo prop = component.GetType().GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance);
            object value = prop != null ? prop.GetValue(component) : null;
            string text = value as string;
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        private static string FormatTargetKind(TabletHackTargetKind kind)
        {
            switch (kind)
            {
                case TabletHackTargetKind.Mainframe: return "MAINFRAME";
                case TabletHackTargetKind.CompanyStash: return "COMPANY STASH";
                case TabletHackTargetKind.CctvCamera: return "CCTV CAMERA";
                case TabletHackTargetKind.Turret: return "TURRET";
                case TabletHackTargetKind.LockedDoor: return "LOCKED DOOR";
                default: return "SIGNAL";
            }
        }

        private static TMP_FontAsset ResolveFont()
        {
            if (_font != null)
                return _font;
            try
            {
                if (HUDManager.Instance != null && HUDManager.Instance.controlTipLines != null)
                {
                    for (int i = 0; i < HUDManager.Instance.controlTipLines.Length; i++)
                    {
                        TextMeshProUGUI line = HUDManager.Instance.controlTipLines[i];
                        if (line != null && line.font != null)
                        {
                            _font = line.font;
                            ProbeEllipsisGlyph(_font);
                            return _font;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        // Once per resolved font: TMP's Ellipsis mode needs U+2026 in the font or its fallbacks, and
        // the 3270 SDF atlas is not guaranteed to carry it. Without it the screen cuts to ASCII.
        private static void ProbeEllipsisGlyph(TMP_FontAsset font)
        {
            if (ReferenceEquals(font, _ellipsisProbedFont))
                return;
            _ellipsisProbedFont = font;
            bool supported;
            try
            {
                supported = font.HasCharacter('\u2026', true, false);
            }
            catch
            {
                supported = false;
            }
            _ellipsisGlyphSupported = supported;
            FieldTabletLayout.SetEllipsisGlyphSupported(supported);
            Plugin.Log?.LogDebug("[Field Tablet] screen font '" + font.name + "' "
                + (supported ? "has U+2026; cut text uses Ellipsis" : "lacks U+2026; cut text uses Truncate and '...'"));
        }

        private static Sprite GetPixelSprite()
        {
            if (_pixelSprite != null)
                return _pixelSprite;

            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false, true);
            _pixelSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
            _pixelSprite.hideFlags = HideFlags.HideAndDontSave;
            return _pixelSprite;
        }

        private static void SetTexture(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, value);
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            List<string> parts = new List<string>();
            Transform current = transform;
            while (current != null && parts.Count < 12)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            if (root == null)
                return;

            Transform[] children = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] != null)
                    children[i].gameObject.layer = layer;
            }
        }

        private static bool Contains(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                   && !string.IsNullOrEmpty(fragment)
                   && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj != null)
                UnityEngine.Object.Destroy(obj);
        }
    }
}
