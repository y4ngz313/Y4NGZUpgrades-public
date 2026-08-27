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
    internal static class FieldTabletScreenRuntime
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
        private const float TargetScanInterval = 0.10f;
        private const float TabletDynamicTextRefreshInterval = 0.10f;
        private const float ScreenRenderInterval = 1f / 30f;
        private const float RadarRenderInterval = 1f / 15f;
        private const float RadarCameraHeight = 3.636f;
        // Canvas resolution matches the physical screen region aspect, which is 0.21328m x
        // 0.14803m = 1.4408:1 measured off the prop mesh - not the 16:10 this file assumed. These
        // are world-space canvas UNITS, not render-texture pixels: every UI number in this file is
        // expressed in them, and they stay fixed while the RT resizes underneath. Width and height
        // must stay in the aperture's ratio or the render texture is stretched onto the quad; at
        // 1844 x 1280 a canvas unit is square in both axes (8646 units/m).
        private const int ScreenWidth = 1844;
        private const int ScreenHeight = 1280;
        // Render-texture mip 0 is sized to the screen's real on-screen footprint instead of being
        // oversized. Lethal Company renders the gameplay camera at 860x520 by default, where the
        // tablet screen occupies ~247x166 real pixels; a 2048-wide RT is 8.3x minification, so the
        // GPU samples mip 3-4 and the mip chain is what you end up reading. Buckets are multiples
        // of 64 at 16:10 so a rebuild is rare.
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
        // Typography budget. fontSize is the em size in canvas units, and for the all-caps strings
        // this UI uses the visible glyph height is the cap height, ~0.70 x fontSize. The screen
        // occupies ~219x164 real pixels at Lethal Company's default 860x520 gameplay render
        // resolution, so 7.8 canvas units = 1 real pixel in both axes. The legibility floor is 8
        // real pixels of cap height: fontSize = (target_real_px x 7.8) / 0.70. Nothing on the
        // tablet screen goes below 80 - if a string does not fit at 80, cut the string. The
        // aperture correction narrowed the canvas from 2048 to 1844 units without touching these
        // sizes, so every string now costs 11% more of the width than it did.
        private const float ScreenTitleFontSize = 132f;        // 12.0 real px of cap height
        private const float ScreenTabFontSize = 112f;          // 10.2
        private const float ScreenRowFontSize = 104f;          //  9.4
        private const float ScreenRowSelectedFontSize = 112f;  // 10.2
        private const float ScreenValueFontSize = 88f;         //  8.0
        private const float ScreenHintFontSize = 80f;          //  7.3, hard floor
        private const float ScreenReadoutFontSize = 140f;      // 12.7, single big readouts
        // Density budget. 1280 units tall, less a ~150-unit header band and a ~130-unit footer
        // band, leaves 1000 usable; at fontSize 104 a row needs a 140-unit pitch, or 164 with a
        // selection band you can actually see. That is five content rows per screen.
        private const int ScreenVisibleRows = 5;
        private const float ScreenRowPitch = 0.128f;           // 164 canvas units
        private const float ScreenRowHalfBand = 0.062f;
        private const float ScreenFirstRowY = 0.70f;
        private const float ScreenStatusY = 0.815f;
        private const float ScreenFooterY = 0.05f;
        private const float ScreenRowLabelX = 0.065f;
        private const float ScreenRowDetailX = 0.935f;
        private const float ScreenRowBandMinX = 0.045f;
        private const float ScreenRowBandMaxX = 0.955f;
        // Any line meant to be seen needs 2 real pixels, and 1 real pixel is 7.7 canvas units.
        private const float ScreenDividerThickness = 0.0125f;  // 16 canvas units
        private const float ScreenSelectionAlpha = 0.24f;
        // Ten segments across the telemetry column is ~18 real pixels each with sub-pixel gaps at
        // vanilla resolution, so the bar reads as a solid block. Five segments read as five.
        private const int DroneTelemetrySegments = 5;
        private static readonly Vector2 ScreenRowLabelSize = new Vector2(945f, 132f);
        private static readonly Vector2 ScreenRowDetailSize = new Vector2(594f, 112f);
        private static readonly Vector2 ScreenLineSize = new Vector2(1711f, 132f);
        private static readonly Vector2 ScreenValueLineSize = new Vector2(1711f, 112f);
        // Client-side mirrors of the host cooldowns (UI feedback only; the host enforces).
        private const float CameraDisableCooldownSeconds = 20f;
        private const float LockdownCooldownSeconds = 30f;
        private const float TrapsDisableCooldownSeconds = 90f;
        private const int MainframeVisibleRows = ScreenVisibleRows;
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

        private enum MainframeAction
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

        private struct MainframeRow
        {
            public string Label;
            public string Detail;
            public bool Enabled;
            public MainframeAction Action;
            public int CameraId;
        }

        private enum TabletHackTargetKind
        {
            None,
            Mainframe,
            CompanyStash,
            CctvCamera,
            Turret,
            LockedDoor,
            DronePickup
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
        private static bool _screenMissingPropDiagnosticsLogged;
        private static string _statusLine = "SCAN READY";
        private static string _hackResult = string.Empty;
        private static WorldReticleRequest _dronePointerReticle;
        private static float _worldReticleAlpha;
        private static GameObject _lastScanTargetRoot;
        private static string _lastScanTargetLabelUpper = string.Empty;
        private static int _lastScanTargetDistanceTenths = int.MinValue;
        private static int _lastRadarZoomTenths = int.MinValue;
        private static int _lastHackPercent = int.MinValue;
        private static string _lastHackTargetLabel = string.Empty;
        private static string _lastCommandTelemetry = string.Empty;
        private static int _commandWindowStart;
        private static string _lastCommandStatusHint = string.Empty;

        private static GameObject _screenQuad;
        private static GameObject _screenQuadBack;
        private static GameObject _screenRigRoot;
        private static GameObject _screenCanvasRoot;
        private static GameObject _playerReticleRoot;
        private static TextMeshProUGUI _worldReticleLabel;
        private static Camera _screenCamera;
        private static Camera _radarCamera;
        private static Camera _radarSourceCamera;
        private static RenderTexture _screenRenderTexture;
        private static RenderTexture _radarTexture;
        private static Material _screenMaterial;
        private static string _screenMaterialSource = "<none>";

        private static Sprite _pixelSprite;
        private static TMP_FontAsset _font;

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
        private static readonly List<TextMeshProUGUI> CommandLabels = new List<TextMeshProUGUI>();
        private static readonly List<TextMeshProUGUI> CommandReasons = new List<TextMeshProUGUI>();
        private static readonly CourierDronePatch.TabletCommandEntry[] CommandEntries = new CourierDronePatch.TabletCommandEntry[6];
        private static readonly string[] LastCommandEntryLabels = new string[CommandEntries.Length];
        private static readonly string[] LastCommandEntryReasons = new string[CommandEntries.Length];
        private static readonly bool[] LastCommandEntrySelected = new bool[CommandEntries.Length];
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
        private static readonly Dictionary<TMP_Text, float> CachedFontSizes = new Dictionary<TMP_Text, float>();
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
        private static readonly List<MainframeRow> MainframeRows = new List<MainframeRow>();
        private static readonly List<int> MainframeCameraIdBuffer = new List<int>();

        private static CanvasGroup _mainframeGroup;
        private static CanvasGroup _tabBarGroup;
        private static TextMeshProUGUI _mainframeStatusText;
        private static TextMeshProUGUI _mainframePageText;
        private static RectTransform _uplinkProgressFill;
        private static GameObject _uplinkProgressRoot;
        private static readonly List<Image> MainframeSelectors = new List<Image>();
        private static readonly List<TextMeshProUGUI> MainframeLabels = new List<TextMeshProUGUI>();
        private static readonly List<TextMeshProUGUI> MainframeDetails = new List<TextMeshProUGUI>();
        private static readonly List<TextMeshProUGUI> TabLabels = new List<TextMeshProUGUI>();
        // Four tabs at fontSize 112 is 24 characters of the 32 the full canvas width allows once the
        // selection caret is counted, so MAINFRAME is abbreviated rather than shrunk.
        private static readonly string[] TabNames = { "SCAN", "MAP", "DRONE", "MFRM" };

        internal static bool HasHackableTarget => _currentTarget.IsValid;
        internal static bool IsCommandScreenOpen => _mode == TabletScreenMode.Command;
        internal static bool IsUplinkEstablished => _uplinkEstablished;

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
            _cameraDisableReadyAt = 0f;
            _lockdownReadyAt = 0f;
            _trapsReadyAt = 0f;
        }

        /// <summary>Host/relay result feedback for mainframe actions (lockdown, traps).</summary>
        internal static void OnMainframeActionResult(byte action, bool success, float value, int count)
        {
            switch (action)
            {
                case FieldTabletMainframeNet.ActionLockdownBegin:
                    _mainframeStatus = success ? "LOCKDOWN ENGAGED" : "LOCKDOWN UNAVAILABLE";
                    break;
                case FieldTabletMainframeNet.ActionLockdownEnd:
                    _mainframeStatus = success ? "GATES OPENED" : "GATES HELD BY ANOTHER SYSTEM";
                    break;
                case FieldTabletMainframeNet.ActionTraps:
                    _mainframeStatus = success
                        ? "TRAPS OFFLINE " + count + " FOR ~" + value.ToString("0") + "S"
                        : "NO ACTIVE TRAPS FOUND";
                    break;
            }
        }

        internal static void UpdateActive(PlayerControllerB player, Action fireTabletActionGesture)
        {
            if (player == null)
                return;

            EnsureScreenOverlay(player);
            UpdateScreenRenderTextureSize(player);
            UpdateBootState();
            RefreshHackableTargetScan(player);
            HandleTabletInputs(player, fireTabletActionGesture);
            UpdatePlayerCrosshair(player);
            RenderActiveScreen(player);
        }

        internal static void Destroy()
        {
            FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
            StopHack("tablet-closed");
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
            CommandLabels.Clear();
            CommandReasons.Clear();
            MainframeSelectors.Clear();
            MainframeLabels.Clear();
            MainframeDetails.Clear();
            TabLabels.Clear();
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
            _currentTarget = default;
            _hackTarget = default;
            ResetDynamicTextState();
        }

        // Every discrete tablet command is an InputUtils action. Mouse-wheel scrolling remains an
        // analog convenience, and input is read only while the tablet is deployed.
        private static void HandleTabletInputs(PlayerControllerB player, Action fireTabletActionGesture)
        {
            Mouse mouse = Mouse.current;
            Gui.IngameKeybinds keybinds = Gui.Plugin.Keybinds;

            if (CourierDronePatch.IsLocalPilotModeActive)
            {
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
                _mode = TabletScreenMode.Drone;
                _statusLine = "REMOTE LINK ACTIVE";
                if (Gui.UpgradeInput.WasPressed(keybinds?.TabletBack))
                {
                    FieldTabletAudio.PlayBack();
                    fireTabletActionGesture?.Invoke();
                    CourierDronePatch.StopLocalPilotMode(notifyOwner: false);
                    _statusLine = "REMOTE LINK CLOSED";
                }
                return;
            }

            if (_mode == TabletScreenMode.Boot)
                return;

            bool up = Gui.UpgradeInput.WasPressed(keybinds?.TabletUp);
            bool down = Gui.UpgradeInput.WasPressed(keybinds?.TabletDown);
            bool primaryAction = Gui.UpgradeInput.WasPressed(keybinds?.TabletPrimaryAction);
            bool activate = Gui.UpgradeInput.WasPressed(keybinds?.TabletActivate)
                || (_mode == TabletScreenMode.Scan && primaryAction);
            bool back = Gui.UpgradeInput.WasPressed(keybinds?.TabletBack);
            bool previousTab = Gui.UpgradeInput.WasPressed(keybinds?.TabletPreviousTab);
            bool nextTab = Gui.UpgradeInput.WasPressed(keybinds?.TabletNextTab);

            if (previousTab || nextTab)
            {
                SwitchTab(nextTab ? 1 : -1);
                FieldTabletAudio.PlayMovement();
                fireTabletActionGesture?.Invoke();
                return;
            }

            switch (_mode)
            {
                case TabletScreenMode.Scan:
                    if (activate && _currentTarget.IsValid)
                        BeginTargetSplice(fireTabletActionGesture);
                    break;
                case TabletScreenMode.Hack:
                    if (back && _hackSolvedAt <= 0f)
                    {
                        StopHack("aborted");
                        FieldTabletAudio.PlayBack();
                        _statusLine = "SPLICE ABORTED";
                    }
                    break;
                case TabletScreenMode.Radar:
                    HandleMapInputs(mouse, up, down);
                    break;
                case TabletScreenMode.Drone:
                    HandleMapZoomKeys(mouse);
                    if (activate)
                    {
                        FieldTabletAudio.PlaySelect();
                        if (CourierDroneUpgrade.IsUnlocked())
                        {
                            EnterCommandScreen();
                            fireTabletActionGesture?.Invoke();
                            _statusLine = "DRONE COMMAND";
                        }
                        else
                        {
                            _statusLine = "DRONE MODULE LOCKED";
                        }
                    }
                    break;
                case TabletScreenMode.Command:
                    HandleCommandScreenInputs(player, mouse, up, down, activate, back, fireTabletActionGesture);
                    break;
                case TabletScreenMode.Mainframe:
                    HandleMainframeInputs(player, up, down, activate, back, fireTabletActionGesture);
                    break;
            }

            UpdateSpliceChannel();
            UpdateUplinkChannel(player);
        }

        private static int TabIndexForMode(TabletScreenMode mode)
        {
            switch (mode)
            {
                case TabletScreenMode.Radar: return 1;
                case TabletScreenMode.Drone:
                case TabletScreenMode.Command: return 2;
                case TabletScreenMode.Mainframe: return 3;
                default: return 0;
            }
        }

        private static void SwitchTab(int delta)
        {
            if (_mode == TabletScreenMode.Hack)
                StopHack("tab-switch");
            if (_mode == TabletScreenMode.Command)
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
            if (_uplinkChannelActive)
            {
                _uplinkChannelActive = false;
                _uplinkProgress = 0f;
                _mainframeStatus = "UPLINK ABORTED";
            }

            int tab = TabIndexForMode(_mode) + delta;
            if (tab < 0)
                tab += TabNames.Length;
            tab %= TabNames.Length;

            switch (tab)
            {
                case 0:
                    _mode = TabletScreenMode.Scan;
                    _statusLine = "SCAN READY";
                    break;
                case 1:
                    _mode = TabletScreenMode.Radar;
                    _statusLine = "INTERIOR MAP";
                    break;
                case 2:
                    _mode = TabletScreenMode.Drone;
                    _statusLine = "DRONE LINK";
                    break;
                default:
                    _mode = TabletScreenMode.Mainframe;
                    _mainframeView = MainframeView.Root;
                    _mainframeSelection = 0;
                    _mainframeWindowStart = 0;
                    _nextMainframeRowsRefreshAt = 0f;
                    _statusLine = "MAINFRAME";
                    // Raise the tablet for the reading-heavy mainframe screen.
                    FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(true);
                    break;
            }

            if (_mode != TabletScreenMode.Mainframe && _mode != TabletScreenMode.Command)
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
        }

        private static void HandleMapInputs(Mouse mouse, bool up, bool down)
        {
            // Zoom: up/down (nothing else is selectable on the MAP tab), +/- keys, or scroll.
            if (up)
            {
                AdjustRadarZoom(0.12f);
                FieldTabletAudio.PlayMovement();
            }
            if (down)
            {
                AdjustRadarZoom(-0.12f);
                FieldTabletAudio.PlayMovement();
            }
            HandleMapZoomKeys(mouse);
        }

        private static void HandleMapZoomKeys(Mouse mouse)
        {
            if (Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.TabletZoomIn))
            {
                AdjustRadarZoom(0.12f);
                FieldTabletAudio.PlayMovement();
            }
            if (Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.TabletZoomOut))
            {
                AdjustRadarZoom(-0.12f);
                FieldTabletAudio.PlayMovement();
            }

            if (mouse != null)
            {
                float scroll = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    AdjustRadarZoom(Mathf.Sign(scroll) * 0.12f);
                    FieldTabletAudio.PlayMovement();
                }
            }
        }

        private static void AdjustRadarZoom(float delta)
        {
            _radarZoom = Mathf.Clamp(_radarZoom + delta, 0.65f, 2.35f);
            _nextRadarRenderAt = 0f;
        }

        private static void HandleCommandScreenInputs(PlayerControllerB player, Mouse mouse, bool up, bool down, bool activate, bool back, Action fireTabletActionGesture)
        {
            if (back)
            {
                FieldTabletAudio.PlayBack();
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
                _mode = TabletScreenMode.Drone;
                _statusLine = "DRONE LINK";
                fireTabletActionGesture?.Invoke();
                return;
            }

            if (up)
            {
                MoveCommandSelection(-1);
                FieldTabletAudio.PlayMovement();
            }
            if (down)
            {
                MoveCommandSelection(1);
                FieldTabletAudio.PlayMovement();
            }

            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    MoveCommandSelection(scroll < 0f ? 1 : -1);
                    FieldTabletAudio.PlayMovement();
                }
            }

            if (!activate)
                return;

            ExecuteSelectedCommand(player, fireTabletActionGesture);
        }

        private static void EnterCommandScreen()
        {
            if (_mode == TabletScreenMode.Command)
                return;

            if (_mode == TabletScreenMode.Hack)
                StopHack("command-screen");

            _modeBeforeCommand = _mode == TabletScreenMode.Boot || _mode == TabletScreenMode.Hack
                ? TabletScreenMode.Scan
                : _mode;
            _commandSelection = 0;
            _commandWindowStart = 0;
            _mode = TabletScreenMode.Command;
            FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(true);
        }

        private static void ExecuteSelectedCommand(PlayerControllerB player, Action fireTabletActionGesture)
        {
            int count = CourierDronePatch.GetTabletCommandEntries(player, CommandEntries);
            if (count <= 0)
            {
                _statusLine = "NO COMMANDS";
                FieldTabletAudio.PlaySelect();
                return;
            }

            _commandSelection = Mathf.Clamp(_commandSelection, 0, count - 1);
            CourierDronePatch.TabletCommandEntry entry = CommandEntries[_commandSelection];
            fireTabletActionGesture?.Invoke();
            if (!entry.Available)
            {
                _statusLine = string.IsNullOrWhiteSpace(entry.Reason) ? "COMMAND LOCKED" : entry.Reason;
                FieldTabletAudio.PlaySelect();
                return;
            }

            if (CourierDronePatch.TryExecuteTabletCommand(player, entry.Type))
            {
                if (entry.Type == CourierDronePatch.CourierCommandType.Spawn)
                    FieldTabletAudio.PlayDroneSummon();
                else
                    FieldTabletAudio.PlayDroneCommand();
                _statusLine = entry.Type == CourierDronePatch.CourierCommandType.Pilot
                    ? "REMOTE LINK ACTIVE"
                    : entry.Label + " SENT";
                FieldOperationsTabletPatch.TrySetCommandScreenActionReadyHold(false);
                _mode = entry.Type == CourierDronePatch.CourierCommandType.Pilot
                    ? TabletScreenMode.Drone
                    : _modeBeforeCommand == TabletScreenMode.Command || _modeBeforeCommand == TabletScreenMode.Hack || _modeBeforeCommand == TabletScreenMode.Boot
                    ? TabletScreenMode.Drone
                    : _modeBeforeCommand;
            }
            else
            {
                FieldTabletAudio.PlaySelect();
            }
        }

        private static void MoveCommandSelection(int delta)
        {
            int count = CourierDronePatch.GetTabletCommandEntries(GameNetworkManager.Instance?.localPlayerController, CommandEntries);
            if (count <= 0)
            {
                _commandSelection = 0;
                return;
            }

            _commandSelection = (_commandSelection + delta) % count;
            if (_commandSelection < 0)
                _commandSelection += count;
        }

        private static void BeginTargetSplice(Action fireTabletActionGesture)
        {
            if (_hackHoldActive || !_currentTarget.IsValid)
                return;

            _hackHoldActive = true;
            _hackTarget = _currentTarget;
            _hackProgress = 0f;
            _hackSolvedAt = 0f;
            _hackResult = string.Empty;
            _statusLine = "SPLICING";
            _mode = TabletScreenMode.Hack;
            FieldTabletAudio.PlayHack();
            fireTabletActionGesture?.Invoke();
        }

        // Enter-started channel: runs while the reticle stays on the same target;
        // BACKSPACE or losing the target aborts.
        private static void UpdateSpliceChannel()
        {
            if (!_hackHoldActive || _mode != TabletScreenMode.Hack)
                return;
            if (_hackSolvedAt > 0f)
                return;

            bool sameTarget = _currentTarget.IsValid
                && _hackTarget.Root != null
                && _currentTarget.Root == _hackTarget.Root;
            if (!sameTarget)
            {
                StopHack("target-lost");
                _statusLine = "LINK LOST";
                return;
            }

            _hackTarget.Distance = _currentTarget.Distance;
            _hackProgress += Time.deltaTime / TargetSpliceSeconds;
            if (_hackProgress < 1f)
                return;

            _hackProgress = 1f;
            _hackSolvedAt = Time.unscaledTime;
            bool applied = TryNotifyHackSuccess();
            if (applied)
            {
                UnityEngine.Object hackedTarget = _hackTarget.Root != null
                    ? _hackTarget.Root
                    : _hackTarget.Component;
                EmployeeStatistics.RecordDeviceHacked(hackedTarget);
            }
            _hackResult = applied ? "ACCESS GRANTED" : "ACCESS COMPLETE";
            _statusLine = "SCAN READY";
        }

        // Enter-started 10 s uplink channel; aborts on BACKSPACE, tab switch,
        // leaving the facility, or death.
        private static void UpdateUplinkChannel(PlayerControllerB player)
        {
            if (!_uplinkChannelActive)
                return;

            if (_mode != TabletScreenMode.Mainframe || player == null || player.isPlayerDead || !player.isInsideFactory)
            {
                _uplinkChannelActive = false;
                _uplinkProgress = 0f;
                _mainframeStatus = "UPLINK ABORTED";
                return;
            }

            _uplinkProgress += Time.deltaTime / UplinkSeconds;
            if (_uplinkProgress < 1f)
                return;

            _uplinkChannelActive = false;
            _uplinkProgress = 0f;
            _uplinkEstablished = true;
            _mainframeStatus = "UPLINK ESTABLISHED";
        }

        private static void HandleMainframeInputs(PlayerControllerB player, bool up, bool down, bool activate, bool back, Action fireTabletActionGesture)
        {
            BuildMainframeRows(player);
            int count = MainframeRows.Count;
            if (count > 0)
            {
                _mainframeSelection = Mathf.Clamp(_mainframeSelection, 0, count - 1);
                if (up)
                {
                    _mainframeSelection = (_mainframeSelection - 1 + count) % count;
                    FieldTabletAudio.PlayMovement();
                }
                if (down)
                {
                    _mainframeSelection = (_mainframeSelection + 1) % count;
                    FieldTabletAudio.PlayMovement();
                }
            }
            else
            {
                _mainframeSelection = 0;
            }

            if (back)
            {
                if (_uplinkChannelActive)
                {
                    _uplinkChannelActive = false;
                    _uplinkProgress = 0f;
                    _mainframeStatus = "UPLINK ABORTED";
                    FieldTabletAudio.PlayBack();
                }
                else if (_mainframeView != MainframeView.Root)
                {
                    _mainframeView = MainframeView.Root;
                    _mainframeSelection = 0;
                    _mainframeWindowStart = 0;
                    _nextMainframeRowsRefreshAt = 0f;
                    fireTabletActionGesture?.Invoke();
                    FieldTabletAudio.PlayBack();
                }
                return;
            }

            if (!activate || count == 0 || _mainframeSelection >= count)
                return;

            BuildMainframeRows(player, force: true);
            count = MainframeRows.Count;
            if (count == 0 || _mainframeSelection >= count)
                return;

            MainframeRow row = MainframeRows[_mainframeSelection];
            if (!row.Enabled)
            {
                _mainframeStatus = row.Detail;
                FieldTabletAudio.PlaySelect();
                return;
            }

            ActivateMainframeRow(player, row, fireTabletActionGesture);
        }

        private static void ActivateMainframeRow(PlayerControllerB player, MainframeRow row, Action fireTabletActionGesture)
        {
            float now = Time.unscaledTime;
            if (row.Action == MainframeAction.EstablishUplink)
                FieldTabletAudio.PlayHack();
            else
                FieldTabletAudio.PlaySelect();

            switch (row.Action)
            {
                case MainframeAction.EstablishUplink:
                    if (_uplinkChannelActive)
                        return;
                    _uplinkChannelActive = true;
                    _uplinkProgress = 0f;
                    _mainframeStatus = "UPLINKING";
                    fireTabletActionGesture?.Invoke();
                    break;
                case MainframeAction.OpenStashCodes:
                    _mainframeView = MainframeView.StashCodes;
                    _mainframeSelection = 0;
                    _mainframeWindowStart = 0;
                    _nextMainframeRowsRefreshAt = 0f;
                    fireTabletActionGesture?.Invoke();
                    break;
                case MainframeAction.ScrapCheck:
                    RefreshScrapCheck();
                    _mainframeStatus = _scrapCheckResult;
                    fireTabletActionGesture?.Invoke();
                    break;
                case MainframeAction.OpenCameras:
                    _mainframeView = MainframeView.Cameras;
                    _mainframeSelection = 0;
                    _mainframeWindowStart = 0;
                    _nextMainframeRowsRefreshAt = 0f;
                    fireTabletActionGesture?.Invoke();
                    break;
                case MainframeAction.DisableCamera:
                    _cameraDisableReadyAt = now + CameraDisableCooldownSeconds;
                    FieldTabletMainframeNet.RequestCameraDisable(row.CameraId);
                    _mainframeStatus = "SHUTDOWN SENT " + row.Label;
                    fireTabletActionGesture?.Invoke();
                    break;
                case MainframeAction.AlarmToggle:
                    FieldTabletMainframeNet.RequestAlarm(CompanyIsAlarmOn() ? FieldTabletMainframeNet.AlarmOpOff : FieldTabletMainframeNet.AlarmOpOn);
                    _mainframeStatus = "ALARM TOGGLE SENT";
                    fireTabletActionGesture?.Invoke();
                    break;
                case MainframeAction.AlarmSilence:
                    FieldTabletMainframeNet.RequestAlarm(FieldTabletMainframeNet.AlarmOpSilence);
                    _mainframeStatus = "SILENCE SENT";
                    fireTabletActionGesture?.Invoke();
                    break;
                case MainframeAction.Lockdown:
                    _lockdownReadyAt = now + LockdownCooldownSeconds;
                    FieldTabletMainframeNet.RequestLockdown(!CompanyIsLockdownActive());
                    _mainframeStatus = "LOCKDOWN REQUEST SENT";
                    fireTabletActionGesture?.Invoke();
                    break;
                case MainframeAction.DisableTraps:
                    _trapsReadyAt = now + TrapsDisableCooldownSeconds;
                    FieldTabletMainframeNet.RequestTrapsDisable();
                    _mainframeStatus = "TRAP SHUTDOWN SENT";
                    fireTabletActionGesture?.Invoke();
                    break;
            }
        }

        // field_operations is a single-tier upgrade in the current catalog, so the
        // designed T1/T2/T3 split compresses to "everything at tier 1"; the per-row
        // RequiredTier plumbing stays so a future multi-tier catalog re-splits cleanly.
        private static int GetFieldOperationsTier()
        {
            return FieldOperationsUpgrade.GetTier();
        }

        private static float _nextMainframeRowsRefreshAt;

        // Row rebuilds are throttled like the rest of the tablet text refreshes;
        // activations force a rebuild so Enter always acts on fresh data.
        private static void BuildMainframeRows(PlayerControllerB player, bool force = false)
        {
            float time = Time.unscaledTime;
            if (!force && MainframeRows.Count > 0 && time < _nextMainframeRowsRefreshAt)
                return;
            _nextMainframeRowsRefreshAt = time + TabletDynamicTextRefreshInterval;

            MainframeRows.Clear();
            bool inFacility = player != null && player.isInsideFactory && !player.isPlayerDead;
            bool company = IsCompanyPresent();
            float now = Time.unscaledTime;

            if (_mainframeView == MainframeView.StashCodes)
            {
                BuildStashCodeRows(company);
                return;
            }
            if (_mainframeView == MainframeView.Cameras)
            {
                BuildCameraRows(company, now);
                return;
            }

            if (!_uplinkEstablished)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "ESTABLISH UPLINK",
                    Detail = _uplinkChannelActive
                        ? "LINK " + Mathf.FloorToInt(Mathf.Clamp01(_uplinkProgress) * 100f).ToString("00") + "%"
                        : inFacility ? "[ENT] " + UplinkSeconds.ToString("0") + "S" : "NO FACILITY",
                    Enabled = inFacility && !_uplinkChannelActive,
                    Action = MainframeAction.EstablishUplink
                });
            }

            AddMainframeRow("STASH CODES", requiredTier: 1, company: true, companyPresent: company,
                MainframeAction.OpenStashCodes, enabledDetail: "VIEW");
            AddMainframeRow("SCRAP CHECK", requiredTier: 1, company: false, companyPresent: company,
                MainframeAction.ScrapCheck,
                enabledDetail: string.IsNullOrEmpty(_scrapCheckResult) ? "RUN SWEEP" : _scrapCheckResult);
            AddMainframeRow("CAMERAS", requiredTier: 1, company: true, companyPresent: company,
                MainframeAction.OpenCameras, enabledDetail: "VIEW/DISABLE");
            AddMainframeRow("ALARM " + (company && CompanyIsAlarmOn() ? "OFF" : "ON"), requiredTier: 1, company: true, companyPresent: company,
                MainframeAction.AlarmToggle,
                enabledDetail: company && !CompanyIsMainframeHacked() ? "NEEDS HACK"
                    : company && CompanyIsAlarmOn() ? "ACTIVE" : "QUIET",
                extraLock: company && !CompanyIsMainframeHacked());
            AddMainframeRow("SILENCE ALARM", requiredTier: 1, company: true, companyPresent: company,
                MainframeAction.AlarmSilence,
                enabledDetail: company && CompanyIsSecurityAlarmActive() ? "SEC ACTIVE" : "SEC QUIET");
            bool lockdownActive = company && CompanyIsLockdownActive();
            AddMainframeRow(lockdownActive ? "LOCKDOWN END" : "LOCKDOWN BEGIN", requiredTier: 1, company: true, companyPresent: company,
                MainframeAction.Lockdown,
                enabledDetail: now < _lockdownReadyAt
                    ? "CD " + Mathf.CeilToInt(_lockdownReadyAt - now) + "S"
                    : lockdownActive ? "GATES DOWN" : "READY",
                extraLock: now < _lockdownReadyAt);
            AddMainframeRow("DISABLE TRAPS", requiredTier: 1, company: false, companyPresent: company,
                MainframeAction.DisableTraps,
                enabledDetail: now < _trapsReadyAt
                    ? "CD " + Mathf.CeilToInt(_trapsReadyAt - now) + "S"
                    : "TEMPORARY",
                extraLock: now < _trapsReadyAt);
        }

        private static void AddMainframeRow(string label, int requiredTier, bool company, bool companyPresent,
            MainframeAction action, string enabledDetail, bool extraLock = false)
        {
            string detail = enabledDetail;
            bool enabled = true;

            if (company && !companyPresent)
            {
                enabled = false;
                detail = "NO COMPANY";
            }
            else if (GetFieldOperationsTier() < requiredTier)
            {
                enabled = false;
                detail = "TIER " + requiredTier;
            }
            else if (!_uplinkEstablished)
            {
                enabled = false;
                detail = "NO UPLINK";
            }
            else if (extraLock)
            {
                enabled = false;
            }

            MainframeRows.Add(new MainframeRow
            {
                Label = label,
                Detail = detail,
                Enabled = enabled,
                Action = action
            });
        }

        private static void BuildStashCodeRows(bool company)
        {
            if (!company || !_uplinkEstablished)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "STASH CODES",
                    Detail = company ? "NO UPLINK" : "NO COMPANY",
                    Enabled = false,
                    Action = MainframeAction.None
                });
                return;
            }

            int added = AppendStashCodeRows();
            if (added == 0)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "NO CODES ON FILE",
                    Detail = string.Empty,
                    Enabled = false,
                    Action = MainframeAction.None
                });
            }
        }

        private static void BuildCameraRows(bool company, float now)
        {
            if (!company || !_uplinkEstablished)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "CAMERAS",
                    Detail = company ? "NO UPLINK" : "NO COMPANY",
                    Enabled = false,
                    Action = MainframeAction.None
                });
                return;
            }

            MainframeCameraIdBuffer.Clear();
            CompanyCollectCameraIds(MainframeCameraIdBuffer);
            if (MainframeCameraIdBuffer.Count == 0)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "NO CAMERAS FOUND",
                    Detail = string.Empty,
                    Enabled = false,
                    Action = MainframeAction.None
                });
                return;
            }

            bool coolingDown = now < _cameraDisableReadyAt;
            for (int i = 0; i < MainframeCameraIdBuffer.Count; i++)
            {
                int id = MainframeCameraIdBuffer[i];
                string label = CompanyGetCameraLabel(id);
                if (string.IsNullOrWhiteSpace(label))
                    label = "CAM_" + id;
                bool disabled = CompanyIsCameraDisabled(id);
                MainframeRows.Add(new MainframeRow
                {
                    Label = label.ToUpperInvariant(),
                    Detail = disabled ? "OFFLINE"
                        : coolingDown ? "CD " + Mathf.CeilToInt(_cameraDisableReadyAt - now) + "S"
                        : "[ENT] OFF",
                    Enabled = !disabled && !coolingDown,
                    Action = MainframeAction.DisableCamera,
                    CameraId = id
                });
            }
        }

        // Mirrors the ship terminal scan sweep (GrabbableObject + isScrap) but scoped
        // to scrap still inside the facility; display-only, so a local read is fine.
        private static void RefreshScrapCheck()
        {
            int total = 0;
            int count = 0;
            try
            {
                GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
                for (int i = 0; i < items.Length; i++)
                {
                    GrabbableObject item = items[i];
                    if (item == null || item.itemProperties == null || !item.itemProperties.isScrap)
                        continue;
                    if (!item.isInFactory || item.isInShipRoom || item.isInElevator)
                        continue;
                    total += item.scrapValue;
                    count++;
                }
                _scrapCheckResult = "$" + total + "/" + count;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] scrap check failed: " + ex.Message);
                _scrapCheckResult = "SWEEP FAILED";
            }
        }

        // - Company reads (kept in non-inlined methods behind the presence gate so the
        //   Y4NGZCompany type references only JIT behind IsCompanyPresent()) -

        private static bool? _companyPresent;

        private static bool IsCompanyPresent()
        {
            // CctvSupportApi and MainframeSupport moved to LethalCCTV.dll (Y4NGZCompany#393);
            // the reads below JIT behind this gate, so it has to name the CCTV plugin.
            if (!_companyPresent.HasValue)
                _companyPresent = OptionalPluginCapabilities.LethalCctv;
            return _companyPresent.Value;
        }

        private static int AppendStashCodeRows()
        {
            IReadOnlyList<int> codes = OptionalCctvBridge.GetStashCodes();
            if (codes == null)
                return 0;
            for (int i = 0; i < codes.Count; i++)
            {
                MainframeRows.Add(new MainframeRow
                {
                    Label = "CODE " + OptionalCctvBridge.FormatStashCode(codes[i]),
                    Detail = string.Empty,
                    Enabled = false,
                    Action = MainframeAction.None
                });
            }
            return codes.Count;
        }

        private static void CompanyCollectCameraIds(List<int> output)
        {
            IReadOnlyList<int> ids = OptionalCctvBridge.GetCameraIds();
            for (int i = 0; i < ids.Count; i++)
                output.Add(ids[i]);
        }

        private static string CompanyGetCameraLabel(int cameraId)
        {
            return OptionalCctvBridge.GetCameraLabel(cameraId);
        }

        private static bool CompanyIsCameraDisabled(int cameraId)
        {
            return OptionalCctvBridge.IsCameraDisabled(cameraId);
        }

        private static bool CompanyIsAlarmOn()
        {
            return IsCompanyPresent() && OptionalCctvBridge.IsAlarmActive;
        }

        private static bool CompanyIsSecurityAlarmActive()
        {
            return IsCompanyPresent() && OptionalCctvBridge.IsSecurityAlarmActive();
        }

        private static bool CompanyIsLockdownActive()
        {
            return IsCompanyPresent() && OptionalCctvBridge.IsLockdownActive;
        }

        private static bool CompanyIsMainframeHacked()
        {
            return IsCompanyPresent() && OptionalCctvBridge.IsMainframeHacked;
        }

        private static void StopHack(string reason)
        {
            _hackHoldActive = false;
            _hackTarget = default;
            _hackProgress = 0f;
            _hackSolvedAt = 0f;
            _hackResult = string.Empty;
            if (_mode == TabletScreenMode.Hack)
                _mode = TabletScreenMode.Scan;
        }

        private static void UpdateBootState()
        {
            if (_mode != TabletScreenMode.Boot)
                return;

            float elapsed = Time.unscaledTime - _openedAt;
            if (elapsed >= BootSeconds + BootTransitionSeconds)
                _mode = TabletScreenMode.Scan;
        }

        private static void RenderActiveScreen(PlayerControllerB player)
        {
            if (_screenCanvasRoot == null)
                return;

            float time = Time.unscaledTime;
            UpdateScanlines(time);
            RenderTabBar();

            if (_mode == TabletScreenMode.Boot)
                RenderTabletBootView();
            else if (_mode == TabletScreenMode.Radar)
                RenderTabletRadarView(player);
            else if (_mode == TabletScreenMode.Drone)
                RenderTabletDroneView(player);
            else if (_mode == TabletScreenMode.Command)
                RenderTabletCommandView(player);
            else if (_mode == TabletScreenMode.Hack)
                RenderTabletHackView();
            else if (_mode == TabletScreenMode.Mainframe)
                RenderTabletMainframeView(player);
            else
                RenderTabletScanView();

            RenderScreenTexture();
        }

        private static void RenderScreenTexture()
        {
            if (_screenCamera == null || _screenRenderTexture == null || _screenCanvasRoot == null)
                return;
            if (!ShouldRenderScreenTexture(Time.unscaledTime))
                return;

            try
            {
                Canvas.ForceUpdateCanvases();
                RenderTexture previousActive = RenderTexture.active;
                RenderTexture previousTarget = _screenCamera.targetTexture;
                _screenCamera.targetTexture = _screenRenderTexture;
                _screenCamera.Render();
                RenderTexture.active = previousActive;
                _screenCamera.targetTexture = previousTarget;
                if (!_screenRenderDiagnosticsLogged)
                {
                    _screenRenderDiagnosticsLogged = true;
                    float maxLuma = ProbeRenderTextureMaxLuma(_screenRenderTexture);
                    Plugin.Log?.LogInfo($"[Field Tablet] screen runtime v5 rendered rt={_screenRenderTexture.width}x{_screenRenderTexture.height} canvasActive={_screenCanvasRoot.activeInHierarchy} cameraLayer={ScreenRenderLayer} cameraEnabled={_screenCamera.enabled} rtMaxLuma={maxLuma:0.000}");
                    if (maxLuma <= 0.01f)
                        Plugin.Log?.LogWarning("[Field Tablet] screen RT probe came back black: the off-screen canvas is not reaching the render texture.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] screen render skipped: " + ex.Message);
            }
        }

        private static float ProbeRenderTextureMaxLuma(RenderTexture texture)
        {
            if (texture == null)
                return -1f;

            RenderTexture previous = RenderTexture.active;
            Texture2D probe = null;
            try
            {
                const int probeSize = 64;
                RenderTexture.active = texture;
                probe = new Texture2D(probeSize, probeSize, TextureFormat.RGBA32, false);
                probe.ReadPixels(new Rect((texture.width - probeSize) / 2f, (texture.height - probeSize) / 2f, probeSize, probeSize), 0, 0);
                probe.Apply(false);
                Color32[] pixels = probe.GetPixels32();
                byte max = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].r > max) max = pixels[i].r;
                    if (pixels[i].g > max) max = pixels[i].g;
                    if (pixels[i].b > max) max = pixels[i].b;
                }
                return max / 255f;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] RT probe failed: " + ex.Message);
                return -1f;
            }
            finally
            {
                RenderTexture.active = previous;
                if (probe != null)
                    DestroyObject(probe);
            }
        }

        private static void RefreshHackableTargetScan(PlayerControllerB player)
        {
            float time = Time.unscaledTime;
            if (time < _nextTargetScanAt)
                return;

            _nextTargetScanAt = time + TargetScanInterval;
            if (TryFindHackableTarget(player, out TabletHackTarget target))
            {
                _currentTarget = target;
                return;
            }

            _currentTarget = default;
            _lastTargetWasValid = false;
        }

        private static bool ShouldRenderScreenTexture(float time)
        {
            if (_hasRenderedScreenTexture && time < _nextScreenRenderAt)
                return false;

            _hasRenderedScreenTexture = true;
            _nextScreenRenderAt = time + ScreenRenderInterval;
            return true;
        }

        private static bool ShouldRefreshDynamicText(float time)
        {
            if (time < _nextDynamicTextRefreshAt)
                return false;

            _nextDynamicTextRefreshAt = time + TabletDynamicTextRefreshInterval;
            return true;
        }

        private static void RefreshScanTargetText(bool hasTarget)
        {
            if (_scanTarget == null)
                return;
            if (!hasTarget)
            {
                _lastScanTargetRoot = null;
                _lastScanTargetLabelUpper = string.Empty;
                _lastScanTargetDistanceTenths = int.MinValue;
                SetTextIfChanged(_scanTarget, string.Empty);
                return;
            }

            int distanceTenths = Mathf.RoundToInt(Mathf.Max(0f, _currentTarget.Distance) * 10f);
            string label = string.IsNullOrWhiteSpace(_currentTarget.Label)
                ? FormatTargetKind(_currentTarget.Kind)
                : _currentTarget.Label;
            bool targetChanged = _lastScanTargetRoot != _currentTarget.Root
                || !_lastScanTargetLabelUpper.Equals(label, StringComparison.OrdinalIgnoreCase);
            bool distanceChanged = _lastScanTargetDistanceTenths != distanceTenths;
            if (!targetChanged && (!distanceChanged || !ShouldRefreshDynamicText(Time.unscaledTime)))
                return;

            if (targetChanged)
            {
                _lastScanTargetRoot = _currentTarget.Root;
                _lastScanTargetLabelUpper = label.ToUpperInvariant();
            }
            _lastScanTargetDistanceTenths = distanceTenths;
            float displayedDistance = distanceTenths / 10f;
            SetTextIfChanged(_scanTarget, _lastScanTargetLabelUpper + "  " + displayedDistance.ToString("00.0") + "M");
        }

        private static void RefreshRadarStatusText()
        {
            if (_radarStatus == null)
                return;

            int zoomTenths = Mathf.RoundToInt(_radarZoom * 10f);
            if (_lastRadarZoomTenths == zoomTenths)
                return;

            _lastRadarZoomTenths = zoomTenths;
            SetTextIfChanged(_radarStatus, "ZOOM " + (zoomTenths / 10f).ToString("0.0") + "X");
        }

        private static void RefreshHackTargetText()
        {
            if (_hackTargetText == null)
                return;

            string label = string.IsNullOrWhiteSpace(_hackTarget.Label) ? "FIELD TARGET" : _hackTarget.Label;
            if (_lastHackTargetLabel.Equals(label, StringComparison.OrdinalIgnoreCase))
                return;

            _lastHackTargetLabel = label;
            SetTextIfChanged(_hackTargetText, label.ToUpperInvariant());
        }

        private static void RefreshHackPercentText(bool solved)
        {
            if (_hackPercentText == null)
                return;
            if (solved)
            {
                _lastHackPercent = int.MinValue;
                SetTextIfChanged(_hackPercentText, _hackResult);
                return;
            }

            int percent = Mathf.FloorToInt(Mathf.Clamp01(_hackProgress) * 100f);
            if (_lastHackPercent == percent)
                return;

            _lastHackPercent = percent;
            SetTextIfChanged(_hackPercentText, percent.ToString("00") + "%");
        }

        private static void RenderTabletBootView()
        {
            SetActiveGroup(_bootGroup);

            float elapsed = Mathf.Max(0f, Time.unscaledTime - _openedAt);
            float progress = Mathf.Clamp01(elapsed / BootSeconds);
            float transition = Mathf.Clamp01((elapsed - BootSeconds) / BootTransitionSeconds);
            if (_bootTitle != null)
            {
                SetTextIfChanged(_bootTitle, "Y4NGZ INDUSTRIES");
                _bootTitle.alpha = Mathf.Lerp(0.78f, 1f, Mathf.PingPong(Time.unscaledTime * 2.4f, 1f));
            }
            if (_bootFill != null)
                _bootFill.anchorMax = new Vector2(Mathf.Lerp(0.30f, 0.70f, progress), 0.45f);
            if (_bootGroup != null && transition > 0f)
                _bootGroup.alpha = Mathf.Lerp(1f, 0.15f, transition);
            if (transition > 0f)
            {
                SetGroup(_scanGroup, true, Mathf.Clamp01(transition * 1.25f));
                RefreshTabletScanContent();
            }
        }

        private static void RenderTabletScanView()
        {
            SetActiveGroup(_scanGroup);
            RefreshTabletScanContent();
        }

        private static void RefreshTabletScanContent()
        {
            bool hasTarget = _currentTarget.IsValid;
            if (_scanStatus != null)
            {
                SetTextIfChanged(_scanStatus, _statusLine);
                SetColorIfChanged(_scanStatus, hasTarget ? ScreenAmber : ScreenGreen);
            }
            if (_scanTarget != null)
            {
                RefreshScanTargetText(hasTarget);
                SetColorIfChanged(_scanTarget, ScreenGreen);
            }
            if (_scanLinkReady != null)
                SetActiveIfChanged(_scanLinkReady.gameObject, hasTarget);
            if (_scanLinkGlyph != null)
                SetActiveIfChanged(_scanLinkGlyph, hasTarget);
            if (_scanCenterDot != null)
            {
                SetActiveIfChanged(_scanCenterDot.gameObject, !hasTarget);
                float blink = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 3.4f);
                _scanCenterDot.color = new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, blink);
            }

            UpdateTabletScanReticle(hasTarget);
        }

        private static void RenderTabletRadarView(PlayerControllerB player)
        {
            SetActiveGroup(_radarGroup);

            RefreshRadarStatusText();

            _radarRendered = TryRenderRadarTextureThrottled(player, ResolveRadarFocus(player), player.isInsideFactory, player.isInHangarShipRoom, updateMarkers: true);

            if (_radarImage != null)
            {
                SetEnabledIfChanged(_radarImage, _radarRendered);
                SetColorIfChanged(_radarImage, _radarRendered ? new Color(0.62f, 1f, 0.66f, 0.9f) : new Color(0f, 0f, 0f, 0f));
            }
        }

        private static void RenderTabletDroneView(PlayerControllerB player)
        {
            SetActiveGroup(_droneGroup);

            bool hasDrone = CourierDronePatch.TryGetTabletTelemetry(
                out Vector3 dronePosition,
                out float healthNormalized,
                out float cargoNormalized,
                out int cargoCount,
                out string status,
                out bool droneInside);

            SetSegmentBar(DroneHpSegments, hasDrone ? healthNormalized : 0f, healthNormalized <= 0.3f ? ScreenRed : ScreenGreen);
            SetSegmentBar(DroneCargoSegments, hasDrone ? cargoNormalized : 0f, ScreenGreen);
            if (_droneStatusValue != null)
            {
                bool pilotActive = CourierDronePatch.IsLocalPilotModeActive;
                SetTextIfChanged(_droneStatusValue, pilotActive ? "REMOTE LINK ACTIVE" : hasDrone ? status : "NO LINK");
                SetColorIfChanged(_droneStatusValue, !hasDrone || status == "DESTROYED" ? ScreenRed : ScreenGreen);
            }

            bool feedRendered = false;
            if (hasDrone)
                _radarRendered = TryRenderRadarTextureThrottled(player, dronePosition + Vector3.up * 0.06f, droneInside, inShipRoom: false, updateMarkers: false);
            feedRendered = hasDrone && _radarRendered;

            if (_droneFeedImage != null)
            {
                if (_radarTexture != null && _droneFeedImage.texture != _radarTexture)
                    _droneFeedImage.texture = _radarTexture;
                SetEnabledIfChanged(_droneFeedImage, feedRendered);
                SetColorIfChanged(_droneFeedImage, feedRendered ? new Color(0.55f, 1f, 0.75f, 0.85f) : new Color(0f, 0f, 0f, 0f));
            }
            if (_droneMarker != null)
                SetActiveIfChanged(_droneMarker.gameObject, feedRendered);
            if (_droneFeedFallback != null)
                SetActiveIfChanged(_droneFeedFallback.gameObject, !feedRendered);
        }

        private static void RenderTabletCommandView(PlayerControllerB player)
        {
            SetActiveGroup(_commandGroup);

            int count = CourierDronePatch.GetTabletCommandEntries(player, CommandEntries);
            if (count <= 0)
            {
                _commandSelection = 0;
                _commandWindowStart = 0;
                if (_commandStatusLine != null)
                    SetTextIfChanged(_commandStatusLine, BuildCommandStatusHint("NO COMMANDS"));
            }
            else
            {
                _commandSelection = Mathf.Clamp(_commandSelection, 0, count - 1);
                // The bindings themselves live on the HUD prompt stack now.
                if (_commandStatusLine != null)
                    SetTextIfChanged(_commandStatusLine, BuildCommandStatusHint(string.Empty));
            }

            _commandWindowStart = ResolveRowWindowStart(_commandSelection, count);
            RenderPageIndicator(_commandPageText, _commandWindowStart, count);

            for (int slot = 0; slot < CommandLabels.Count; slot++)
            {
                int index = _commandWindowStart + slot;
                bool visible = index < count;
                if (CommandLabels[slot] != null)
                    SetActiveIfChanged(CommandLabels[slot].gameObject, visible);
                if (CommandReasons[slot] != null)
                    SetActiveIfChanged(CommandReasons[slot].gameObject, visible);
                if (CommandSelectors[slot] != null)
                    SetActiveIfChanged(CommandSelectors[slot].gameObject, visible);
                if (!visible)
                    continue;

                CourierDronePatch.TabletCommandEntry entry = CommandEntries[index];
                bool selected = index == _commandSelection;

                if (CommandLabels[slot] != null)
                {
                    string label = entry.Label ?? string.Empty;
                    if (!string.Equals(LastCommandEntryLabels[slot], label, StringComparison.Ordinal) || LastCommandEntrySelected[slot] != selected)
                    {
                        LastCommandEntryLabels[slot] = label;
                        LastCommandEntrySelected[slot] = selected;
                        SetTextIfChanged(CommandLabels[slot], (selected ? "> " : "  ") + label);
                    }
                    SetColorIfChanged(CommandLabels[slot], ResolveRowLabelColor(selected, entry.Available));
                    SetFontSizeIfChanged(CommandLabels[slot], selected ? ScreenRowSelectedFontSize : ScreenRowFontSize);
                    SetFontStyleIfChanged(CommandLabels[slot], selected ? FontStyles.Bold : FontStyles.Normal);
                }
                if (CommandReasons[slot] != null)
                {
                    string reason = entry.Available ? string.Empty : entry.Reason ?? string.Empty;
                    if (!string.Equals(LastCommandEntryReasons[slot], reason, StringComparison.Ordinal) || LastCommandEntryAvailable[slot] != entry.Available)
                    {
                        LastCommandEntryReasons[slot] = reason;
                        LastCommandEntryAvailable[slot] = entry.Available;
                        SetTextIfChanged(CommandReasons[slot], reason);
                    }
                    SetColorIfChanged(CommandReasons[slot], ResolveRowDetailColor(selected, entry.Available));
                }
                if (CommandSelectors[slot] != null)
                    SetColorIfChanged(CommandSelectors[slot], ResolveRowSelectorColor(selected));
            }

            RenderCommandTelemetry();
        }

        /// <summary>
        /// Resolves which five-row page holds the selection. Discrete pages rather than a sliding
        /// window, so the "PAGE n/m" footer means exactly what it says. Shared by the command and
        /// mainframe lists.
        /// </summary>
        private static int ResolveRowWindowStart(int selection, int count)
        {
            if (count <= ScreenVisibleRows)
                return 0;
            return Mathf.Clamp(selection, 0, count - 1) / ScreenVisibleRows * ScreenVisibleRows;
        }

        private static void RenderPageIndicator(TMP_Text target, int windowStart, int count)
        {
            if (target == null)
                return;

            bool paged = count > ScreenVisibleRows;
            SetActiveIfChanged(target.gameObject, paged);
            if (!paged)
                return;

            int pages = Mathf.Max(1, Mathf.CeilToInt(count / (float)ScreenVisibleRows));
            int page = Mathf.Clamp(windowStart / ScreenVisibleRows + 1, 1, pages);
            SetTextIfChanged(target, "PAGE " + page + "/" + pages);
        }

        // A selected row is drawn on a green selection band, so its text has to clear 4.5:1 against
        // that band rather than against the panel: dim-on-band measures 2.82:1 and fails, amber
        // measures 5.47:1. Availability is carried by the detail string on a selected row.
        private static Color ResolveRowLabelColor(bool selected, bool enabled)
            => selected ? ScreenAmber : enabled ? ScreenGreen : ScreenDim;

        private static Color ResolveRowDetailColor(bool selected, bool enabled)
            => selected ? ScreenAmber : enabled ? ScreenDim : ScreenRed;

        private static Color ResolveRowSelectorColor(bool selected)
            => selected
                ? new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, ScreenSelectionAlpha)
                : Color.clear;

        private static string BuildCommandStatusHint(string baseText)
        {
            string text = baseText ?? string.Empty;
            bool hasDrone = CourierDronePatch.TryGetTabletTelemetry(
                out _,
                out _,
                out _,
                out _,
                out _,
                out _);
            if (hasDrone)
                text += (text.Length > 0 ? "  " : string.Empty) + "[" + Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.CourierDroneCommand, "MMB") + "] TARGET";

            if (string.Equals(_lastCommandStatusHint, text, StringComparison.Ordinal))
                return _lastCommandStatusHint;

            _lastCommandStatusHint = text;
            return text;
        }

        private static void RenderCommandTelemetry()
        {
            bool hasDrone = CourierDronePatch.TryGetTabletTelemetry(
                out _,
                out float healthNormalized,
                out _,
                out int cargoCount,
                out _,
                out _);
            string status = CourierDronePatch.GetTabletActiveModeName();

            if (_commandTelemetryLine == null)
                return;

            // One numeric line instead of two ten-segment bars: at the real footprint a segment is
            // ~18 real pixels with a sub-pixel gap, so the bars read as solid blocks anyway.
            string telemetry = hasDrone
                ? "HP " + Mathf.RoundToInt(Mathf.Clamp01(healthNormalized) * 100f) + "%  CGO " + cargoCount + "  " + status
                : "NO LINK";
            if (string.Equals(_lastCommandTelemetry, telemetry, StringComparison.Ordinal))
                return;

            _lastCommandTelemetry = telemetry;
            SetTextIfChanged(_commandTelemetryLine, telemetry);
            SetColorIfChanged(_commandTelemetryLine, !hasDrone || status == "DESTROYED" ? ScreenRed : ScreenDim);
        }

        private static void SetSegmentBar(List<Image> segments, float normalized, Color litColor)
        {
            int lit = Mathf.RoundToInt(Mathf.Clamp01(normalized) * segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i] == null)
                    continue;
                SetColorIfChanged(segments[i], i < lit
                    ? litColor
                    : new Color(litColor.r, litColor.g, litColor.b, 0.14f));
            }
        }

        private static void SetActiveGroup(CanvasGroup active)
        {
            SetGroup(_bootGroup, active == _bootGroup, 1f);
            SetGroup(_scanGroup, active == _scanGroup, 1f);
            SetGroup(_radarGroup, active == _radarGroup, 1f);
            SetGroup(_droneGroup, active == _droneGroup, 1f);
            SetGroup(_commandGroup, active == _commandGroup, 1f);
            SetGroup(_hackGroup, active == _hackGroup, 1f);
            SetGroup(_mainframeGroup, active == _mainframeGroup, 1f);
        }

        private static void RenderTabBar()
        {
            if (_tabBarGroup == null)
                return;

            bool visible = _mode != TabletScreenMode.Boot;
            SetGroup(_tabBarGroup, visible, 1f);
            if (!visible)
                return;

            int activeTab = TabIndexForMode(_mode);
            for (int i = 0; i < TabLabels.Count && i < TabNames.Length; i++)
            {
                TextMeshProUGUI label = TabLabels[i];
                if (label == null)
                    continue;
                bool selected = i == activeTab;
                SetTextIfChanged(label, (selected ? "> " : "  ") + TabNames[i]);
                SetColorIfChanged(label, selected ? ScreenAmber : ScreenDim);
                // One size for both states: four tabs already fill the width, so growing the
                // selected one would push the last tab off the screen.
                SetFontSizeIfChanged(label, ScreenTabFontSize);
                SetFontStyleIfChanged(label, selected ? FontStyles.Bold : FontStyles.Normal);
            }
        }

        private static void RenderTabletMainframeView(PlayerControllerB player)
        {
            SetActiveGroup(_mainframeGroup);

            BuildMainframeRows(player);
            int count = MainframeRows.Count;
            _mainframeSelection = count > 0 ? Mathf.Clamp(_mainframeSelection, 0, count - 1) : 0;

            _mainframeWindowStart = ResolveRowWindowStart(_mainframeSelection, count);
            // The uplink bar owns the footer band while a channel is running, so the two never
            // share it.
            if (_mainframePageText != null && _uplinkChannelActive)
                SetActiveIfChanged(_mainframePageText.gameObject, false);
            else
                RenderPageIndicator(_mainframePageText, _mainframeWindowStart, count);

            if (_mainframeStatusText != null)
            {
                SetTextIfChanged(_mainframeStatusText, _mainframeStatus);
                SetColorIfChanged(_mainframeStatusText, _uplinkEstablished ? ScreenGreen : ScreenAmber);
            }

            for (int slot = 0; slot < MainframeLabels.Count; slot++)
            {
                int index = _mainframeWindowStart + slot;
                bool rowVisible = index < count;
                if (MainframeLabels[slot] != null)
                    SetActiveIfChanged(MainframeLabels[slot].gameObject, rowVisible);
                if (MainframeDetails[slot] != null)
                    SetActiveIfChanged(MainframeDetails[slot].gameObject, rowVisible);
                if (MainframeSelectors[slot] != null)
                    SetActiveIfChanged(MainframeSelectors[slot].gameObject, rowVisible);
                if (!rowVisible)
                    continue;

                MainframeRow row = MainframeRows[index];
                bool selected = index == _mainframeSelection;

                if (MainframeLabels[slot] != null)
                {
                    SetTextIfChanged(MainframeLabels[slot], (selected ? "> " : "  ") + row.Label);
                    SetColorIfChanged(MainframeLabels[slot], ResolveRowLabelColor(selected, row.Enabled));
                    SetFontSizeIfChanged(MainframeLabels[slot], selected ? ScreenRowSelectedFontSize : ScreenRowFontSize);
                    SetFontStyleIfChanged(MainframeLabels[slot], selected ? FontStyles.Bold : FontStyles.Normal);
                }
                if (MainframeDetails[slot] != null)
                {
                    SetTextIfChanged(MainframeDetails[slot], row.Detail ?? string.Empty);
                    SetColorIfChanged(MainframeDetails[slot], ResolveRowDetailColor(selected, row.Enabled));
                }
                if (MainframeSelectors[slot] != null)
                    SetColorIfChanged(MainframeSelectors[slot], ResolveRowSelectorColor(selected));
            }

            if (_uplinkProgressRoot != null)
                SetActiveIfChanged(_uplinkProgressRoot, _uplinkChannelActive);
            if (_uplinkChannelActive && _uplinkProgressFill != null)
                _uplinkProgressFill.anchorMax = new Vector2(Mathf.Lerp(0.16f, 0.72f, Mathf.Clamp01(_uplinkProgress)), 0.075f);
        }

        private static void SetTextIfChanged(TMP_Text target, string value)
        {
            if (target == null)
                return;

            value = value ?? string.Empty;
            if (CachedTextValues.TryGetValue(target, out string cached) && string.Equals(cached, value, StringComparison.Ordinal))
                return;
            if (!CachedTextValues.ContainsKey(target) && string.Equals(target.text, value, StringComparison.Ordinal))
            {
                CachedTextValues[target] = value;
                return;
            }

            target.text = value;
            CachedTextValues[target] = value;
        }

        private static void SetColorIfChanged(TMP_Text target, Color value)
        {
            if (target == null)
                return;
            if (CachedTextColors.TryGetValue(target, out Color cached) && cached == value)
                return;
            if (!CachedTextColors.ContainsKey(target) && target.color == value)
            {
                CachedTextColors[target] = value;
                return;
            }

            target.color = value;
            CachedTextColors[target] = value;
        }

        private static void SetColorIfChanged(Graphic target, Color value)
        {
            if (target == null)
                return;
            if (CachedGraphicColors.TryGetValue(target, out Color cached) && cached == value)
                return;
            if (!CachedGraphicColors.ContainsKey(target) && target.color == value)
            {
                CachedGraphicColors[target] = value;
                return;
            }

            target.color = value;
            CachedGraphicColors[target] = value;
        }

        private static void SetActiveIfChanged(GameObject target, bool active)
        {
            if (target == null)
                return;
            if (CachedActiveStates.TryGetValue(target, out bool cached) && cached == active)
                return;
            if (!CachedActiveStates.ContainsKey(target) && target.activeSelf == active)
            {
                CachedActiveStates[target] = active;
                return;
            }

            target.SetActive(active);
            CachedActiveStates[target] = active;
        }

        private static void SetEnabledIfChanged(Behaviour target, bool enabled)
        {
            if (target != null && target.enabled != enabled)
                target.enabled = enabled;
        }

        private static void SetFontSizeIfChanged(TMP_Text target, float value)
        {
            if (target == null)
                return;
            if (CachedFontSizes.TryGetValue(target, out float cached) && Mathf.Approximately(cached, value))
                return;
            if (!CachedFontSizes.ContainsKey(target) && Mathf.Approximately(target.fontSize, value))
            {
                CachedFontSizes[target] = value;
                return;
            }

            target.fontSize = value;
            CachedFontSizes[target] = value;
        }

        private static void SetFontStyleIfChanged(TMP_Text target, FontStyles value)
        {
            if (target == null)
                return;
            if (CachedFontStyles.TryGetValue(target, out FontStyles cached) && cached == value)
                return;
            if (!CachedFontStyles.ContainsKey(target) && target.fontStyle == value)
            {
                CachedFontStyles[target] = value;
                return;
            }

            target.fontStyle = value;
            CachedFontStyles[target] = value;
        }

        private static void ClearUiWriteCaches()
        {
            CachedTextValues.Clear();
            CachedTextColors.Clear();
            CachedFontSizes.Clear();
            CachedFontStyles.Clear();
            CachedGraphicColors.Clear();
            CachedActiveStates.Clear();
        }

        private static void ResetDynamicTextState()
        {
            _lastScanTargetRoot = null;
            _lastScanTargetLabelUpper = string.Empty;
            _lastScanTargetDistanceTenths = int.MinValue;
            _lastRadarZoomTenths = int.MinValue;
            _lastHackPercent = int.MinValue;
            _lastHackTargetLabel = string.Empty;
            _lastCommandTelemetry = string.Empty;
            _lastCommandStatusHint = string.Empty;
            Array.Clear(LastCommandEntryLabels, 0, LastCommandEntryLabels.Length);
            Array.Clear(LastCommandEntryReasons, 0, LastCommandEntryReasons.Length);
            Array.Clear(LastCommandEntrySelected, 0, LastCommandEntrySelected.Length);
            Array.Clear(LastCommandEntryAvailable, 0, LastCommandEntryAvailable.Length);
        }

        private static void RenderTabletHackView()
        {
            SetActiveGroup(_hackGroup);

            bool solved = _hackSolvedAt > 0f;
            if (_hackStatusText != null)
            {
                SetTextIfChanged(_hackStatusText, solved ? "LINKED" : "SPLICING");
                SetColorIfChanged(_hackStatusText, solved ? ScreenGreen : ScreenAmber);
            }
            if (_hackTargetText != null)
                RefreshHackTargetText();
            if (_hackPercentText != null)
            {
                RefreshHackPercentText(solved);
                SetColorIfChanged(_hackPercentText, solved ? ScreenGreen : ScreenAmber);
            }
            if (_hackHintText != null)
                SetActiveIfChanged(_hackHintText.gameObject, !solved);
            if (_hackProgressFill != null)
                _hackProgressFill.anchorMax = new Vector2(Mathf.Lerp(0.16f, 0.84f, Mathf.Clamp01(_hackProgress)), 0.47f);

            if (solved && Time.unscaledTime > _hackSolvedAt + HackSolvedLingerSeconds)
                StopHack("solved");
        }

        private static void UpdateTabletScanReticle(bool hasTarget)
        {
            if (_scanReticleRoot == null)
                return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * (hasTarget ? 7.8f : 2.2f));
            float size = hasTarget ? Mathf.Lerp(172f, 192f, pulse) : Mathf.Lerp(68f, 80f, pulse);
            _scanReticleRoot.sizeDelta = new Vector2(size, size);
            Color color = hasTarget ? ScreenAmber : ScreenDim;
            for (int i = 0; i < ScanReticleLines.Count; i++)
            {
                if (ScanReticleLines[i] != null)
                    SetColorIfChanged(ScanReticleLines[i], color);
            }
            if (_scanPulse != null)
            {
                _scanPulse.color = hasTarget
                    ? new Color(ScreenAmber.r, ScreenAmber.g, ScreenAmber.b, Mathf.Lerp(0.06f, 0.18f, pulse))
                    : new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.035f);
                _scanPulse.rectTransform.sizeDelta = new Vector2(size * 1.2f, size * 1.2f);
            }
        }

        private static void UpdateScanlines(float time)
        {
            if (_rollingScanline == null)
                return;

            float y = Mathf.Lerp(-ScreenHeight * 0.52f, ScreenHeight * 0.52f, Mathf.Repeat(time * 0.18f, 1f));
            _rollingScanline.rectTransform.anchoredPosition = new Vector2(0f, y);
            float flicker = 0.17f + Mathf.PerlinNoise(time * 2.7f, 0.33f) * 0.11f;
            _rollingScanline.color = new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, flicker);
        }

        internal static void SetDronePointerReticle(bool show, Color color, float flashSeconds = 0f, string label = null)
        {
            _dronePointerReticle.Show = show;
            _dronePointerReticle.Color = color;
            _dronePointerReticle.Size = WorldReticleBasePixels;
            _dronePointerReticle.FlashUntil = show && flashSeconds > 0f ? Time.unscaledTime + flashSeconds : (show ? _dronePointerReticle.FlashUntil : 0f);
            _dronePointerReticle.Label = show ? label ?? string.Empty : string.Empty;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            UpdateWorldReticle(player);
        }

        private static void UpdatePlayerCrosshair(PlayerControllerB player)
        {
            UpdateWorldReticle(player);
        }

        private static void UpdateWorldReticle(PlayerControllerB player)
        {
            Camera camera = player != null ? player.gameplayCamera : null;
            if (camera == null)
                camera = Camera.main;
            if (camera == null)
                return;

            WorldReticleRequest request = BuildWorldReticleRequest();
            EnsurePlayerCrosshair(camera);
            if (_playerReticleRoot == null)
                return;

            Canvas canvas = _playerReticleRoot.GetComponent<Canvas>();
            if (canvas != null)
                canvas.worldCamera = camera;

            bool show = request.Show;
            _worldReticleAlpha = Mathf.MoveTowards(_worldReticleAlpha, show ? 0.92f : 0f, Time.unscaledDeltaTime * 9f);
            SetActiveIfChanged(_playerReticleRoot, show || _worldReticleAlpha > 0.001f);

            CanvasGroup group = _playerReticleRoot.GetComponent<CanvasGroup>();
            if (group != null)
                group.alpha = _worldReticleAlpha;

            Transform cameraTransform = camera.transform;
            _playerReticleRoot.transform.position = cameraTransform.position + cameraTransform.forward * WorldReticleDistance;
            _playerReticleRoot.transform.rotation = Quaternion.LookRotation(cameraTransform.forward, cameraTransform.up);

            float flashPulse = request.FlashUntil > Time.unscaledTime
                ? 1f + 0.16f * Mathf.Sin(Time.unscaledTime * 46f)
                : 1f;
            float size = Mathf.Max(1f, request.Size <= 0f ? WorldReticleBasePixels : request.Size) * flashPulse;
            RectTransform root = _playerReticleRoot.GetComponent<RectTransform>();
            if (root != null)
            {
                root.sizeDelta = new Vector2(size, size);
                float pixels = Mathf.Max(1f, camera.pixelHeight);
                float worldHeight = 2f * WorldReticleDistance * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
                float worldUnitsPerPixel = worldHeight / pixels;
                root.localScale = Vector3.one * worldUnitsPerPixel;
            }

            Color color = show ? request.Color : new Color(request.Color.r, request.Color.g, request.Color.b, 0f);
            for (int i = 0; i < PlayerReticleLines.Count; i++)
            {
                if (PlayerReticleLines[i] != null)
                    SetColorIfChanged(PlayerReticleLines[i], color);
            }

            if (_worldReticleLabel != null)
            {
                string label = show ? request.Label ?? string.Empty : string.Empty;
                bool showLabel = show && !string.IsNullOrWhiteSpace(label);
                SetActiveIfChanged(_worldReticleLabel.gameObject, showLabel || _worldReticleAlpha > 0.001f);
                SetTextIfChanged(_worldReticleLabel, label);
                SetColorIfChanged(_worldReticleLabel, showLabel ? color : new Color(color.r, color.g, color.b, 0f));
            }
        }

        private static WorldReticleRequest BuildWorldReticleRequest()
        {
            if (FieldOperationsTabletPatch.IsTabletActive && _currentTarget.IsValid)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f);
                return new WorldReticleRequest
                {
                    Show = true,
                    Color = ScreenAmber,
                    Size = Mathf.Lerp(54f, 64f, pulse),
                    FlashUntil = 0f,
                    Label = string.Empty
                };
            }

            if (_dronePointerReticle.Show)
                return _dronePointerReticle;

            return new WorldReticleRequest
            {
                Show = false,
                Color = ScreenAmber,
                Size = 30f,
                FlashUntil = 0f,
                Label = string.Empty
            };
        }

        private static bool TryFindHackableTarget(PlayerControllerB player, out TabletHackTarget target)
        {
            target = default;
            Camera camera = player != null ? player.gameplayCamera : null;
            if (camera == null)
                camera = Camera.main;
            if (camera == null)
                return false;

            Ray ray = new Ray(camera.transform.position, camera.transform.forward);
            RaycastHit[] hits = Physics.RaycastAll(ray, ScanRange, ~0, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0)
                return false;

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null)
                    continue;
                Transform hitTransform = collider.transform;
                if (hitTransform != null && player != null && hitTransform.IsChildOf(player.transform))
                    continue;
                if (hitTransform != null && hitTransform.name.IndexOf("FieldTablet", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                if (TryResolveTarget(collider, hits[i].distance, out target))
                    return true;
            }

            return false;
        }

        private static bool TryResolveTarget(Collider collider, float distance, out TabletHackTarget target)
        {
            target = default;
            if (collider == null)
                return false;

            Component component = FindComponentByTypeName(collider, "MainframeSupport");
            if (component != null)
                return BuildTarget(TabletHackTargetKind.Mainframe, component, "MAINFRAME", distance, out target);

            component = FindComponentByTypeName(collider, "CompanyStashController");
            if (component != null)
                return BuildTarget(TabletHackTargetKind.CompanyStash, component, ResolveDisplayName(component, "COMPANY STASH"), distance, out target);

            component = FindComponentByTypeName(collider, "CCTV", "Camera") ?? FindComponentByTypeName(collider, "Cctv", "Camera");
            if (component != null)
                return BuildTarget(TabletHackTargetKind.CctvCamera, component, ResolveDisplayName(component, "CCTV CAMERA"), distance, out target);

            DoorLock door = collider.GetComponentInParent<DoorLock>();
            if (door != null && door.isLocked)
                return BuildTarget(TabletHackTargetKind.LockedDoor, door, "LOCKED DOOR", distance, out target);

            Turret turret = collider.GetComponentInParent<Turret>();
            if (turret != null && TurretHackerUpgrade.CanHackTurrets())
                return BuildTarget(TabletHackTargetKind.Turret, turret, "TURRET", distance, out target);

            GrabbableObject item = collider.GetComponentInParent<GrabbableObject>();
            if (item != null && CourierDroneUpgrade.IsUnlocked())
                return BuildTarget(TabletHackTargetKind.DronePickup, item, ResolveItemName(item), distance, out target);

            ScanNodeProperties scan = collider.GetComponentInParent<ScanNodeProperties>();
            if (scan != null)
            {
                string scanText = (scan.headerText ?? string.Empty) + " " + (scan.subText ?? string.Empty);
                if (Contains(scanText, "mainframe"))
                    return BuildTarget(TabletHackTargetKind.Mainframe, scan, "MAINFRAME", distance, out target);
                if (Contains(scanText, "camera") || Contains(scanText, "cctv"))
                    return BuildTarget(TabletHackTargetKind.CctvCamera, scan, "CCTV CAMERA", distance, out target);
                if (Contains(scanText, "locked") || Contains(scanText, "door"))
                    return BuildTarget(TabletHackTargetKind.LockedDoor, scan, "LOCKED DOOR", distance, out target);
            }

            return false;
        }

        private static bool BuildTarget(TabletHackTargetKind kind, Component component, string label, float distance, out TabletHackTarget target)
        {
            target = new TabletHackTarget
            {
                Kind = kind,
                Component = component,
                Root = component != null ? component.gameObject : null,
                Label = string.IsNullOrWhiteSpace(label) ? FormatTargetKind(kind) : label,
                Distance = distance
            };

            bool valid = target.IsValid;
            if (valid && !_lastTargetWasValid)
                _statusLine = "OBJECT DETECTED";
            _lastTargetWasValid = valid;
            return valid;
        }

        private static Component FindComponentByTypeName(Collider collider, params string[] fragments)
        {
            if (collider == null)
                return null;

            Transform cursor = collider.transform;
            for (int depth = 0; cursor != null && depth < 8; depth++, cursor = cursor.parent)
            {
                Component found = FindComponentOnTransform(cursor, fragments);
                if (found != null)
                    return found;
            }

            return FindComponentOnTransform(collider.transform.root, fragments);
        }

        private static Component FindComponentOnTransform(Transform transform, params string[] fragments)
        {
            if (transform == null)
                return null;

            Component[] components = transform.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                    continue;

                string text = component.GetType().FullName + " " + component.GetType().Name + " " + component.name;
                bool all = true;
                for (int f = 0; f < fragments.Length; f++)
                {
                    if (!Contains(text, fragments[f]))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                    return component;
            }

            return null;
        }

        private static bool TryNotifyHackSuccess()
        {
            switch (_hackTarget.Kind)
            {
                case TabletHackTargetKind.Mainframe:
                    return TryInvokeInstanceMethod(_hackTarget.Component, "MarkHackedServerRpc");
                case TabletHackTargetKind.CctvCamera:
                    return TryDisableCctvCamera(_hackTarget.Component);
                case TabletHackTargetKind.LockedDoor:
                    return TryUnlockDoor(_hackTarget.Component as DoorLock);
                case TabletHackTargetKind.Turret:
                    return TurretHackerPatch.TryHackFromTablet(
                        _hackTarget.Component as Turret);
                case TabletHackTargetKind.CompanyStash:
                    return TryInvokeInstanceMethod(_hackTarget.Component, "UnlockFromRemoteHackServerRpc")
                           || TryInvokeInstanceMethod(_hackTarget.Component, "UnlockServerRpc")
                           || TryInvokeInstanceMethod(_hackTarget.Component, "MarkUnlockedServerRpc");
                default:
                    return false;
            }
        }

        private static bool TryDisableCctvCamera(Component component)
        {
            if (component == null)
                return false;

            if (TryInvokeInstanceMethod(component, "MarkBroken")
                || TryInvokeInstanceMethod(component, "BreakCamera")
                || TryInvokeInstanceMethod(component, "DisableCamera"))
            {
                return true;
            }

            Type registry = ResolveType("Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry");
            Type director = ResolveType("Y4NGZCompany.Facility.Security.CctvSecurityDirector");
            if (registry == null || director == null)
                return false;

            try
            {
                MethodInfo find = registry.GetMethod("Find", BindingFlags.Public | BindingFlags.Static);
                object state = find != null ? find.Invoke(null, new object[] { component }) : null;
                if (state == null)
                    return false;

                MethodInfo broken = director.GetMethod("OnCameraBroken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (broken == null)
                    return false;
                broken.Invoke(null, new[] { state });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] CCTV disable failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryUnlockDoor(DoorLock door)
        {
            if (door == null)
                return false;

            try
            {
                door.isLocked = false;
            }
            catch
            {
                // Some modded doors expose lock state through methods only.
            }

            return TryInvokeInstanceMethod(door, "UnlockDoorSyncWithServer")
                   || TryInvokeInstanceMethod(door, "OpenDoorAsEnemyServerRpc")
                   || TryInvokeInstanceMethod(door, "OpenDoorServerRpc")
                   || !door.isLocked;
        }

        private static bool TryInvokeInstanceMethod(Component component, string methodName, params object[] preferredArgs)
        {
            if (component == null || string.IsNullOrWhiteSpace(methodName))
                return false;

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo method = component.GetType().GetMethod(methodName, flags);
            if (method == null)
                return false;

            try
            {
                object[] args = BuildArguments(method.GetParameters(), preferredArgs);
                method.Invoke(component, args);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] invoke " + methodName + " failed: " + ex.Message);
                return false;
            }
        }

        private static object[] BuildArguments(ParameterInfo[] parameters, object[] preferredArgs)
        {
            if (parameters == null || parameters.Length == 0)
                return Array.Empty<object>();

            object[] args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (preferredArgs != null && i < preferredArgs.Length && preferredArgs[i] != null && type.IsInstanceOfType(preferredArgs[i]))
                {
                    args[i] = preferredArgs[i];
                    continue;
                }
                if (type == typeof(float) && preferredArgs != null && preferredArgs.Length > 0 && preferredArgs[0] is float f)
                {
                    args[i] = f;
                    continue;
                }
                if (parameters[i].HasDefaultValue
                    && parameters[i].DefaultValue != DBNull.Value
                    && parameters[i].DefaultValue != Missing.Value)
                {
                    object defaultValue = parameters[i].DefaultValue;
                    args[i] = defaultValue ?? (type.IsValueType ? Activator.CreateInstance(type) : null);
                    continue;
                }
                args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
            }
            return args;
        }

        private static Type ResolveType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            Type type = Type.GetType(fullName + ", Y4NGZCompany", throwOnError: false)
                        ?? Type.GetType(fullName + ", LethalCCTV", throwOnError: false)
                        ?? Type.GetType(fullName + ", LGUContractHUD", throwOnError: false);
            if (type != null)
                return type;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    type = assemblies[i].GetType(fullName, throwOnError: false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // Ignore dynamic assemblies that cannot enumerate types.
                }
            }

            return null;
        }

        private static void EnsureScreenOverlay(PlayerControllerB player)
        {
            if (_screenQuad != null && _screenCanvasRoot != null)
            {
                ApplyScreenQuadRect();
                SetActiveIfChanged(_calibrationRoot, Plugin.GetTabletScreenCalibration());
                FieldTabletAudio.BindTo(_screenQuad.transform, player != null ? player.itemAudio : null);
                return;
            }

            Transform prop = FindTabletProp(player);
            if (prop == null)
            {
                if (!_screenMissingPropDiagnosticsLogged)
                {
                    _screenMissingPropDiagnosticsLogged = true;
                    Plugin.Log?.LogWarning("[Field Tablet] screen runtime v5 waiting: visible tablet prop was not found under local player.");
                }
                return;
            }

            EnsureScreenRig();

            if (_screenQuad == null)
            {
                // The authored tablet mesh has a single HDRP/Lit material (no dedicated
                // screen submesh), so the RT lives on an exact-fit overlay quad sitting
                // on the recessed screen plate. The screen faces one of the local +/-z
                // sides and the vanilla screen material is single-sided, so cover both
                // sides; at the plate depth the quad facing away is well inside the
                // tablet body, which hides it exactly as it did at the front face.
                float depth = ResolveScreenLocalDepth(prop);
                Vector2 offset = ResolveScreenLocalOffset();
                _screenQuad = CreateScreenQuad(prop, "Y4NGZ_FieldTablet_ScreenOverlay",
                    new Vector3(offset.x, offset.y, -depth), Quaternion.identity);
                _screenQuadBack = CreateScreenQuad(prop, "Y4NGZ_FieldTablet_ScreenOverlayBack",
                    new Vector3(offset.x, offset.y, depth), Quaternion.Euler(0f, 180f, 0f));
                _measuredScreenLocalDepth = depth;
                _appliedScreenRect = InvalidScreenRect;
                ApplyScreenQuadRect();
                SetActiveIfChanged(_calibrationRoot, Plugin.GetTabletScreenCalibration());
            }

            FieldTabletAudio.BindTo(_screenQuad.transform, player != null ? player.itemAudio : null);

            if (!_screenBindingDiagnosticsLogged)
            {
                _screenBindingDiagnosticsLogged = true;
                string shaderName = _screenMaterial != null && _screenMaterial.shader != null ? _screenMaterial.shader.name : "<null>";
                string rtSize = _screenRenderTexture != null ? $"{_screenRenderTexture.width}x{_screenRenderTexture.height}" : "<null>";
                Vector3 quadWorld = _screenQuad != null ? _screenQuad.transform.position : Vector3.zero;
                Vector3 quadScale = _screenQuad != null ? _screenQuad.transform.lossyScale : Vector3.zero;
                Plugin.Log?.LogInfo($"[Field Tablet] screen runtime v5 bound prop='{GetHierarchyPath(prop)}' propLayer={prop.gameObject.layer} quadLayer={_screenQuad?.layer ?? -1} shader='{shaderName}' materialSource='{_screenMaterialSource}' rt={rtSize} quadWorld={quadWorld} quadScale={quadScale} measuredDepth={_measuredScreenLocalDepth:0.0000}");
            }
        }

        // Player-crosshair canvas, not the tablet screen: 58 pixels square, so it has its own scale.
        private const float WorldReticleLabelFontSize = 10f;

        private static float _measuredScreenLocalDepth = ScreenLocalDepthFallback;
        private static readonly Vector4 InvalidScreenRect = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        private static Vector4 _appliedScreenRect = InvalidScreenRect;
        private static GameObject _calibrationRoot;
        private static TextMeshProUGUI _calibrationOffsetReadout;
        private static TextMeshProUGUI _calibrationSizeReadout;

        private static void RefreshCalibrationReadout(Vector2 offset, Vector2 size)
        {
            SetTextIfChanged(_calibrationOffsetReadout, "X " + offset.x.ToString("0.0000") + "  Y " + offset.y.ToString("0.0000"));
            SetTextIfChanged(_calibrationSizeReadout, "W " + size.x.ToString("0.0000") + "  H " + size.y.ToString("0.0000"));
        }

        private static Vector2 ResolveScreenLocalOffset()
        {
            if (Plugin.TabletScreenOffsetX == null || Plugin.TabletScreenOffsetY == null)
                return new Vector2(ScreenLocalPosition.x, ScreenLocalPosition.y);
            return new Vector2(Plugin.GetTabletScreenOffsetX(), Plugin.GetTabletScreenOffsetY());
        }

        private static Vector2 ResolveScreenLocalSize()
        {
            if (Plugin.TabletScreenWidth == null || Plugin.TabletScreenHeight == null)
                return new Vector2(ScreenLocalScale.x, ScreenLocalScale.y);
            return new Vector2(Plugin.GetTabletScreenWidth(), Plugin.GetTabletScreenHeight());
        }

        /// <summary>
        /// Pushes the configured aperture rect onto both screen quads. Runs every frame while the
        /// tablet is deployed but only touches transforms when a value actually changed, so the
        /// rect can be nudged live during calibration without a rebuild.
        /// </summary>
        private static void ApplyScreenQuadRect()
        {
            if (_screenQuad == null)
                return;

            Vector2 offset = ResolveScreenLocalOffset();
            Vector2 size = ResolveScreenLocalSize();
            Vector4 rect = new Vector4(offset.x, offset.y, size.x, size.y);
            if (rect == _appliedScreenRect)
                return;

            _appliedScreenRect = rect;
            Vector3 scale = new Vector3(size.x, size.y, 1f);
            _screenQuad.transform.localPosition = new Vector3(offset.x, offset.y, -_measuredScreenLocalDepth);
            _screenQuad.transform.localScale = scale;
            if (_screenQuadBack != null)
            {
                _screenQuadBack.transform.localPosition = new Vector3(offset.x, offset.y, _measuredScreenLocalDepth);
                _screenQuadBack.transform.localScale = scale;
            }

            // The on-screen footprint just moved, so the render-texture bucket has to be re-measured.
            _screenRtMeasured = false;
            _nextScreenRtMeasureAt = 0f;
            RefreshCalibrationReadout(offset, size);
            Plugin.Log?.LogInfo($"[Field Tablet] screen aperture rect offset=({offset.x:0.0000}, {offset.y:0.0000}) size=({size.x:0.0000} x {size.y:0.0000}) depth={_measuredScreenLocalDepth:0.0000}");
        }

        /// <summary>
        /// Returns the local depth the screen quads sit at, as a distance from the prop origin.
        /// The screen is the recessed plate, so on the known tablet mesh that is the baked
        /// <see cref="ScreenPlaneLocalDepth"/> plus a small epsilon. The mesh is measured anyway -
        /// bounds are readable even for non-readable meshes - and its half-depth has to match the
        /// mesh the plate depth was measured from; if it does not, the prop is some other model
        /// and the front face is the only surface still safe to assume.
        /// </summary>
        private static float ResolveScreenLocalDepth(Transform prop)
        {
            float measuredHalfDepth = MeasurePropLocalHalfDepth(prop);
            if (measuredHalfDepth > 0.0005f
                && Mathf.Abs(measuredHalfDepth - TabletMeshLocalHalfDepth) <= TabletMeshHalfDepthTolerance)
            {
                return ScreenPlaneLocalDepth + ScreenSurfaceEpsilon;
            }

            Plugin.Log?.LogWarning(
                $"[Field Tablet] screen prop half-depth {measuredHalfDepth:0.0000} does not match the mesh the "
                + $"aperture was measured from ({TabletMeshLocalHalfDepth:0.0000}); falling back to its front face. "
                + "Re-measure the aperture if the prop prefab was rebuilt.");
            return measuredHalfDepth > 0.0005f ? measuredHalfDepth + ScreenSurfaceEpsilon : ScreenLocalDepthFallback;
        }

        /// <summary>
        /// Measures the tablet mesh in prop-local space and returns the half-depth of its front
        /// face. Mesh bounds are readable even for non-readable meshes.
        /// </summary>
        private static float MeasurePropLocalHalfDepth(Transform prop)
        {
            float halfDepth = 0f;
            try
            {
                MeshFilter[] filters = prop.GetComponentsInChildren<MeshFilter>(includeInactive: true);
                for (int i = 0; i < filters.Length; i++)
                {
                    MeshFilter filter = filters[i];
                    if (filter == null || filter.sharedMesh == null)
                        continue;
                    if (filter.name.IndexOf("ScreenOverlay", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    Bounds bounds = filter.sharedMesh.bounds;
                    Vector3 min = bounds.min;
                    Vector3 max = bounds.max;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 local = new Vector3(
                            (corner & 1) == 0 ? min.x : max.x,
                            (corner & 2) == 0 ? min.y : max.y,
                            (corner & 4) == 0 ? min.z : max.z);
                        Vector3 inProp = prop.InverseTransformPoint(filter.transform.TransformPoint(local));
                        halfDepth = Mathf.Max(halfDepth, Mathf.Abs(inProp.z));
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] screen depth measurement failed: " + ex.Message);
            }

            return halfDepth;
        }

        private static GameObject CreateScreenQuad(Transform prop, string name, Vector3 localPosition, Quaternion localRotation)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            quad.layer = prop.gameObject.layer;
            Collider collider = quad.GetComponent<Collider>();
            if (collider != null)
                DestroyObject(collider);
            quad.transform.SetParent(prop, worldPositionStays: false);
            quad.transform.localPosition = localPosition;
            quad.transform.localRotation = localRotation;
            Vector2 size = ResolveScreenLocalSize();
            quad.transform.localScale = new Vector3(size.x, size.y, 1f);

            Renderer renderer = quad.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = EnsureScreenMaterial();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return quad;
        }

        private static Transform FindTabletProp(PlayerControllerB player)
        {
            if (player == null)
                return null;

            Transform found = FindChildByName(player.transform, PropInstanceName);
            if (found != null)
                return found;
            if (player.thisPlayerModelArms != null)
            {
                found = FindChildByName(player.thisPlayerModelArms.transform.root, PropInstanceName);
                if (found != null)
                    return found;
            }
            return FindChildByName(player.transform, "Y4NGZ_FPSTabletProp");
        }

        private static Transform FindChildByName(Transform root, string namePart)
        {
            if (root == null || string.IsNullOrWhiteSpace(namePart))
                return null;

            Transform[] children = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && child.name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;
            }
            return null;
        }

        private static void EnsureScreenRig()
        {
            if (_screenRigRoot != null && _screenRenderTexture != null && _screenRenderTexture.IsCreated() && _screenCanvasRoot != null)
                return;

            if (_screenRenderTexture == null || !_screenRenderTexture.IsCreated())
            {
                // Opens at the last bucket this session measured, so the common case never
                // rebuilds; the first open of a session starts at the fixed fallback.
                _screenRenderTexture = CreateScreenRenderTexture(_screenRtWidth, ScreenRtHeightFor(_screenRtWidth));
            }

            if (_screenRigRoot != null)
                return;

            // Off-world camera+canvas rig, same shape as the in-game verified
            // LGUShipSystems power monitor renderer.
            _screenRigRoot = new GameObject("Y4NGZ_FieldTablet_ScreenRig");
            UnityEngine.Object.DontDestroyOnLoad(_screenRigRoot);
            _screenRigRoot.transform.position = ScreenRenderRigPosition;

            GameObject cameraObject = new GameObject("Y4NGZ_FieldTablet_ScreenCamera");
            cameraObject.transform.SetParent(_screenRigRoot.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -10f);
            _screenCamera = cameraObject.AddComponent<Camera>();
            _screenCamera.clearFlags = CameraClearFlags.SolidColor;
            _screenCamera.backgroundColor = ScreenPanel;
            _screenCamera.cullingMask = 1 << ScreenRenderLayer;
            _screenCamera.targetTexture = _screenRenderTexture;
            _screenCamera.enabled = false;
            _screenCamera.allowHDR = false;
            _screenCamera.allowMSAA = false;
            _screenCamera.orthographic = true;
            _screenCamera.orthographicSize = ScreenHeight * 0.5f;
            _screenCamera.aspect = ScreenWidth / (float)ScreenHeight;
            _screenCamera.nearClipPlane = 0.1f;
            _screenCamera.farClipPlane = 30f;

            _screenCanvasRoot = new GameObject("Y4NGZ_FieldTablet_ScreenCanvas", typeof(RectTransform));
            _screenCanvasRoot.transform.SetParent(_screenRigRoot.transform, false);
            Canvas canvas = _screenCanvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = _screenCamera;
            canvas.sortingOrder = 0;

            RectTransform root = _screenCanvasRoot.GetComponent<RectTransform>();
            root.sizeDelta = new Vector2(ScreenWidth, ScreenHeight);

            Image bg = CreateImage("ScreenBackground", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, ScreenPanel);
            bg.transform.SetAsFirstSibling();
            _bootGroup = CreateGroup("Boot", root);
            _scanGroup = CreateGroup("Scan", root);
            _radarGroup = CreateGroup("Radar", root);
            _droneGroup = CreateGroup("Drone", root);
            _commandGroup = CreateGroup("Command", root);
            _hackGroup = CreateGroup("Hack", root);
            _mainframeGroup = CreateGroup("Mainframe", root);
            _tabBarGroup = CreateGroup("TabBar", root);
            BuildBootUi(_bootGroup.transform);
            BuildScanUi(_scanGroup.transform);
            BuildRadarUi(_radarGroup.transform);
            BuildDroneUi(_droneGroup.transform);
            BuildCommandUi(_commandGroup.transform);
            BuildHackUi(_hackGroup.transform);
            BuildMainframeUi(_mainframeGroup.transform);
            BuildTabBar(_tabBarGroup.transform);
            BuildCrtOverlay(root);
            BuildCalibrationOverlay(root);
            SetLayerRecursive(_screenRigRoot, ScreenRenderLayer);
        }

        private static int _screenRtWidth = ScreenRtDefaultWidth;
        private static int _pendingShrinkWidth;
        private static bool _screenRtMeasured;
        private static float _nextScreenRtMeasureAt;
        private static bool _screenRtLoggedThisOpen;

        private static int ScreenRtHeightFor(int width)
            => Mathf.Max(1, Mathf.RoundToInt(width * (ScreenHeight / (float)ScreenWidth)));

        private static RenderTexture CreateScreenRenderTexture(int width, int height)
        {
            // Mips + trilinear stay on: the tablet is hand-held, so the quad moves every frame and
            // a no-mip RT makes thin green text crawl. Aniso does the real work here because the
            // screen is read at a ~35 degree tilt, and the negative bias keeps the sampler on mip 0
            // when the footprint sits just under the bucket size.
            RenderTexture texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "Y4NGZ_FieldTablet_ScreenRT",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                useMipMap = true,
                autoGenerateMips = true
            };
            texture.Create();
            texture.mipMapBias = -0.5f;
            return texture;
        }

        /// <summary>
        /// Projects the screen quad's four corners through the gameplay camera and keeps the render
        /// texture's mip 0 at the footprint the player actually sees. Camera.pixelWidth/Height
        /// report Lethal Company's low internal render resolution, so this self-tunes for
        /// LCUltrawide's multiplier, ultrawide aspects and FOV changes.
        /// </summary>
        private static void UpdateScreenRenderTextureSize(PlayerControllerB player)
        {
            if (_screenQuad == null || _screenRenderTexture == null)
                return;

            float time = Time.unscaledTime;
            if (time < _nextScreenRtMeasureAt)
                return;
            _nextScreenRtMeasureAt = time + ScreenRtMeasureIntervalSeconds;

            Camera camera = player != null ? player.gameplayCamera : null;
            if (camera == null)
                return;
            if (!TryMeasureScreenQuadPixelSpan(camera, out float spanX, out float spanY))
                return;

            // The canvas is 16:10, so whichever axis is more demanding decides the bucket.
            float needed = Mathf.Max(spanX, spanY * (ScreenWidth / (float)ScreenHeight));
            int bucket = BucketScreenRtWidth(needed);
            int target = bucket;
            if (_screenRtMeasured && bucket < _screenRtWidth)
            {
                // Growing is immediate, but shrinking is not worth a rebuild unless the footprint is
                // clear of the smaller bucket's ceiling and stays there: the tablet bobs with the
                // walk cycle, so a single sample near a bucket edge is not evidence.
                if (needed > bucket - ScreenRtShrinkGuardPixels || _pendingShrinkWidth != bucket)
                {
                    _pendingShrinkWidth = needed > bucket - ScreenRtShrinkGuardPixels ? 0 : bucket;
                    target = _screenRtWidth;
                }
            }
            else
            {
                _pendingShrinkWidth = 0;
            }
            _screenRtMeasured = true;

            ApplyScreenRenderTextureSize(target);
            LogScreenRenderTextureOnce(camera, spanX, spanY, target);
        }

        private static bool TryMeasureScreenQuadPixelSpan(Camera camera, out float spanX, out float spanY)
        {
            spanX = 0f;
            spanY = 0f;
            Transform quad = _screenQuad != null ? _screenQuad.transform : null;
            if (quad == null)
                return false;

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, 0f);
                Vector3 screen = camera.WorldToScreenPoint(quad.TransformPoint(local));
                if (screen.z <= 0.001f)
                    return false;

                minX = Mathf.Min(minX, screen.x);
                maxX = Mathf.Max(maxX, screen.x);
                minY = Mathf.Min(minY, screen.y);
                maxY = Mathf.Max(maxY, screen.y);
            }

            spanX = maxX - minX;
            spanY = maxY - minY;
            return spanX > 1f && spanY > 1f;
        }

        private static int BucketScreenRtWidth(float needed)
        {
            int steps = Mathf.CeilToInt(Mathf.Max(1f, needed) / ScreenRtBucketStep);
            return Mathf.Clamp(steps * ScreenRtBucketStep, ScreenRtMinWidth, ScreenRtMaxWidth);
        }

        private static void ApplyScreenRenderTextureSize(int width)
        {
            int height = ScreenRtHeightFor(width);
            _screenRtWidth = width;
            if (_screenRenderTexture != null && _screenRenderTexture.width == width && _screenRenderTexture.height == height)
                return;

            // Point the camera and all six material texture slots at the new RT before the old one
            // is released, so nothing samples a destroyed texture for a frame.
            RenderTexture previous = _screenRenderTexture;
            _screenRenderTexture = CreateScreenRenderTexture(width, height);
            if (_screenCamera != null)
                _screenCamera.targetTexture = _screenRenderTexture;
            ConfigureTabletScreenMaterial();
            if (previous != null)
            {
                previous.Release();
                DestroyObject(previous);
            }

            _hasRenderedScreenTexture = false;
            _nextScreenRenderAt = 0f;
            _screenRtLoggedThisOpen = false;
        }

        private static void LogScreenRenderTextureOnce(Camera camera, float spanX, float spanY, int width)
        {
            if (_screenRtLoggedThisOpen)
                return;

            _screenRtLoggedThisOpen = true;
            Plugin.Log?.LogInfo($"[Field Tablet] screen RT bucket={width}x{ScreenRtHeightFor(width)} measuredSpan={spanX:0}x{spanY:0}px camera={camera.pixelWidth}x{camera.pixelHeight}");
        }

        private static Material EnsureScreenMaterial()
        {
            if (_screenMaterial != null)
                return _screenMaterial;

            EnsureScreenRig();
            _screenMaterial = CloneVanillaScreenMaterial();
            if (_screenMaterial != null)
            {
                _screenMaterialSource = "vanilla map screen clone";
            }
            else
            {
                Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
                _screenMaterial = new Material(shader);
                _screenMaterialSource = "runtime " + (shader != null ? shader.name : "<null>");
            }
            _screenMaterial.name = "Y4NGZ_FieldTablet_ScreenMaterial";
            ConfigureTabletScreenMaterial();
            return _screenMaterial;
        }

        private static Material CloneVanillaScreenMaterial()
        {
            try
            {
                ManualCameraRenderer mapScreen = StartOfRound.Instance != null ? StartOfRound.Instance.mapScreen : null;
                if (mapScreen == null)
                    return null;

                if (mapScreen.onScreenMat != null)
                    return new Material(mapScreen.onScreenMat);

                MeshRenderer mesh = mapScreen.mesh;
                Material[] materials = mesh != null ? mesh.sharedMaterials : null;
                if (materials == null || mapScreen.materialIndex < 0 || mapScreen.materialIndex >= materials.Length)
                    return null;
                Material source = materials[mapScreen.materialIndex];
                return source != null ? new Material(source) : null;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] vanilla screen material clone failed: " + ex.Message);
                return null;
            }
        }

        private static void ConfigureTabletScreenMaterial()
        {
            if (_screenMaterial == null || _screenRenderTexture == null)
                return;

            // In-game verified recipe (LGUShipSystems ShipPowerDisplay): swap the RT into a
            // vanilla screen material and force plain white emission so the RT colors come
            // through untinted at scene-correct brightness.
            try { _screenMaterial.mainTexture = _screenRenderTexture; }
            catch { }

            SetTexture(_screenMaterial, "_UnlitColorMap", _screenRenderTexture);
            SetTexture(_screenMaterial, "_BaseColorMap", _screenRenderTexture);
            SetTexture(_screenMaterial, "_BaseMap", _screenRenderTexture);
            SetTexture(_screenMaterial, "_MainTex", _screenRenderTexture);
            SetTexture(_screenMaterial, "_EmissiveColorMap", _screenRenderTexture);
            SetTexture(_screenMaterial, "_EmissionMap", _screenRenderTexture);
            SetColor(_screenMaterial, "_BaseColor", Color.white);
            SetColor(_screenMaterial, "_UnlitColor", Color.white);
            SetColor(_screenMaterial, "_Color", Color.white);
            SetColor(_screenMaterial, "_EmissiveColor", Color.white * 1.05f);
            SetColor(_screenMaterial, "_EmissiveColorLDR", Color.white);
            _screenMaterial.EnableKeyword("_EMISSION");
            _screenMaterial.EnableKeyword("_EMISSIVE_COLOR_MAP");
        }

        private static void BuildBootUi(Transform parent)
        {
            _bootTitle = CreateText("BootTitle", parent, "Y4NGZ INDUSTRIES", ScreenTitleFontSize, FontStyles.Normal, TextAlignmentOptions.Center, new Vector2(0.5f, 0.58f), new Vector2(1700f, 180f), ScreenGreen);
            _bootTitle.characterSpacing = 8f;
            Image back = CreateImage("BootBarBack", parent, new Vector2(0.30f, 0.405f), new Vector2(0.70f, 0.45f), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.22f));
            Image fill = CreateImage("BootBarFill", parent, new Vector2(0.30f, 0.405f), new Vector2(0.30f, 0.45f), Vector2.zero, Vector2.zero, ScreenGreen);
            _bootFill = fill.rectTransform;
            CreateImage("BootBaseline", parent, new Vector2(0.30f, 0.353f), new Vector2(0.70f, 0.353f + ScreenDividerThickness), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.35f));
            back.raycastTarget = false;
        }

        private static void BuildScanUi(Transform parent)
        {
            _scanStatus = CreateText("ScanStatus", parent, "SCAN READY", ScreenRowFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(ScreenRowBandMinX, ScreenStatusY), ScreenLineSize, ScreenGreen);

            GameObject reticle = new GameObject("ScanReticle");
            reticle.transform.SetParent(parent, false);
            _scanReticleRoot = reticle.AddComponent<RectTransform>();
            _scanReticleRoot.anchorMin = new Vector2(0.5f, 0.55f);
            _scanReticleRoot.anchorMax = new Vector2(0.5f, 0.55f);
            _scanReticleRoot.pivot = new Vector2(0.5f, 0.5f);
            _scanReticleRoot.sizeDelta = new Vector2(220f, 220f);
            _scanPulse = CreateImage("ScanPulse", _scanReticleRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-116f, -116f), new Vector2(116f, 116f), new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.10f));
            BuildSquareReticle(_scanReticleRoot, ScanReticleLines, 16f, 92f, ScreenDim);
            _scanCenterDot = CreateImage("ScanCenterDot", _scanReticleRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-20f, -20f), new Vector2(20f, 20f), ScreenGreen);

            _scanTarget = CreateText("ScanTarget", parent, string.Empty, ScreenRowFontSize, FontStyles.Normal, TextAlignmentOptions.Center, new Vector2(0.5f, 0.30f), ScreenLineSize, ScreenGreen);
            // The splice binding itself lives on the HUD prompt stack now, so the screen only has
            // to say that a target is locked.
            _scanLinkReady = CreateText("ScanLinkReady", parent, "SPLICE READY", ScreenRowFontSize, FontStyles.Normal, TextAlignmentOptions.Center, new Vector2(0.5f, 0.195f), ScreenLineSize, ScreenAmber);
            _scanLinkReady.characterSpacing = 4f;
            _scanLinkGlyph = BuildLinkGlyph(parent, new Vector2(0.5f, 0.095f));
            _scanLinkReady.gameObject.SetActive(false);
            _scanLinkGlyph.SetActive(false);
        }

        private static void BuildRadarUi(Transform parent)
        {
            _radarStatus = CreateText("RadarStatus", parent, "ZOOM 1.0X", ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Right, new Vector2(ScreenRowBandMaxX, 0.845f), new Vector2(810f, 112f), ScreenDim);
            CreateText("RadarZoomHint", parent, "[+/-] ZOOM", ScreenHintFontSize, FontStyles.Normal, TextAlignmentOptions.Center, new Vector2(0.5f, ScreenFooterY), new Vector2(1080f, 112f), ScreenDim);
            GameObject imageGo = new GameObject("InteriorMapTexture");
            imageGo.transform.SetParent(parent, false);
            RectTransform imageRect = imageGo.AddComponent<RectTransform>();
            imageRect.anchorMin = new Vector2(0.045f, 0.125f);
            imageRect.anchorMax = new Vector2(0.955f, 0.795f);
            imageRect.offsetMin = Vector2.zero;
            imageRect.offsetMax = Vector2.zero;
            _radarImage = imageGo.AddComponent<RawImage>();
            _radarImage.raycastTarget = false;

            GameObject markerGo = new GameObject("RadarMarkers");
            markerGo.transform.SetParent(parent, false);
            _radarMarkerLayer = markerGo.AddComponent<RectTransform>();
            _radarMarkerLayer.anchorMin = imageRect.anchorMin;
            _radarMarkerLayer.anchorMax = imageRect.anchorMax;
            _radarMarkerLayer.offsetMin = Vector2.zero;
            _radarMarkerLayer.offsetMax = Vector2.zero;
        }

        private static void BuildDroneUi(Transform parent)
        {
            GameObject feedGo = new GameObject("DroneFeed");
            feedGo.transform.SetParent(parent, false);
            RectTransform feedRect = feedGo.AddComponent<RectTransform>();
            feedRect.anchorMin = new Vector2(0.045f, 0.30f);
            feedRect.anchorMax = new Vector2(0.60f, 0.86f);
            feedRect.offsetMin = Vector2.zero;
            feedRect.offsetMax = Vector2.zero;
            _droneFeedImage = feedGo.AddComponent<RawImage>();
            _droneFeedImage.raycastTarget = false;
            _droneFeedFallback = CreateText("DroneFeedFallback", feedRect, "NO LINK", ScreenRowFontSize, FontStyles.Normal, TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), new Vector2(900f, 132f), ScreenDim);
            _droneMarker = CreateImage("DroneMarker", feedRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-16f, -16f), new Vector2(16f, 16f), ScreenCyan);
            _droneMarker.rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);

            CreateImage("DroneColumnDivider", parent, new Vector2(0.615f, 0.30f), new Vector2(0.615f + ScreenDividerThickness, 0.86f), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.55f));

            CreateText("DroneHpLabel", parent, "HP", ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(0.66f, 0.82f), new Vector2(540f, 112f), ScreenDim);
            BuildSegmentRow(parent, "DroneHpSegments", 0.66f, 0.955f, 0.70f, 0.775f, DroneTelemetrySegments, DroneHpSegments);
            CreateText("DroneCargoLabel", parent, "CARGO", ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(0.66f, 0.64f), new Vector2(540f, 112f), ScreenDim);
            BuildSegmentRow(parent, "DroneCargoSegments", 0.66f, 0.955f, 0.52f, 0.595f, DroneTelemetrySegments, DroneCargoSegments);
            CreateText("DroneStatusLabel", parent, "STATUS", ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(ScreenRowBandMinX, 0.185f), new Vector2(450f, 112f), ScreenDim);
            _droneStatusValue = CreateText("DroneStatusValue", parent, "OK", ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(0.22f, 0.185f), new Vector2(1306f, 112f), ScreenGreen);
        }

        private static void BuildCommandUi(Transform parent)
        {
            _commandStatusLine = CreateText("CommandStatus", parent, string.Empty, ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(ScreenRowBandMinX, ScreenStatusY), ScreenValueLineSize, ScreenDim);

            // Full-width rows: the telemetry column that used to sit beside them cannot hold
            // legible text at this size, so it collapses into one numeric line in the footer band.
            for (int i = 0; i < ScreenVisibleRows; i++)
            {
                float y = ScreenFirstRowY - i * ScreenRowPitch;
                Image selector = CreateImage("CommandSelector" + i, parent, new Vector2(ScreenRowBandMinX, y - ScreenRowHalfBand), new Vector2(ScreenRowBandMaxX, y + ScreenRowHalfBand), Vector2.zero, Vector2.zero, Color.clear);
                TextMeshProUGUI label = CreateText("CommandLabel" + i, parent, "COMMAND", ScreenRowFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(ScreenRowLabelX, y), ScreenRowLabelSize, ScreenGreen);
                TextMeshProUGUI reason = CreateText("CommandReason" + i, parent, string.Empty, ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Right, new Vector2(ScreenRowDetailX, y), ScreenRowDetailSize, ScreenRed);
                selector.gameObject.SetActive(false);
                label.gameObject.SetActive(false);
                reason.gameObject.SetActive(false);
                CommandSelectors.Add(selector);
                CommandLabels.Add(label);
                CommandReasons.Add(reason);
            }

            _commandTelemetryLine = CreateText("CommandTelemetry", parent, string.Empty, ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(ScreenRowBandMinX, ScreenFooterY), new Vector2(1261f, 112f), ScreenDim);
            _commandPageText = CreateText("CommandPage", parent, string.Empty, ScreenHintFontSize, FontStyles.Normal, TextAlignmentOptions.Right, new Vector2(ScreenRowBandMaxX, ScreenFooterY), new Vector2(378f, 112f), ScreenDim);
        }

        private static void BuildSegmentRow(Transform parent, string name, float anchorMinX, float anchorMaxX, float anchorMinY, float anchorMaxY, int count, List<Image> output)
        {
            output.Clear();
            GameObject rowGo = new GameObject(name);
            rowGo.transform.SetParent(parent, false);
            RectTransform row = rowGo.AddComponent<RectTransform>();
            row.anchorMin = new Vector2(anchorMinX, anchorMinY);
            row.anchorMax = new Vector2(anchorMaxX, anchorMaxY);
            row.offsetMin = Vector2.zero;
            row.offsetMax = Vector2.zero;
            for (int i = 0; i < count; i++)
            {
                Image segment = CreateImage("Segment" + i, row,
                    new Vector2(i / (float)count, 0f),
                    new Vector2((i + 1) / (float)count, 1f),
                    new Vector2(8f, 0f), new Vector2(-8f, 0f),
                    ScreenGreen);
                output.Add(segment);
            }
        }

        private static GameObject BuildLinkGlyph(Transform parent, Vector2 anchor)
        {
            GameObject glyphGo = new GameObject("LinkGlyph");
            glyphGo.transform.SetParent(parent, false);
            RectTransform glyph = glyphGo.AddComponent<RectTransform>();
            glyph.anchorMin = anchor;
            glyph.anchorMax = anchor;
            glyph.pivot = new Vector2(0.5f, 0.5f);
            glyph.sizeDelta = new Vector2(140f, 52f);
            // Two chain loops with a connecting bar.
            BuildBoxOutline(glyph, "LoopL", new Vector2(0f, 0f), new Vector2(0.42f, 1f), 10f, ScreenAmber);
            BuildBoxOutline(glyph, "LoopR", new Vector2(0.58f, 0f), new Vector2(1f, 1f), 10f, ScreenAmber);
            CreateImage("LinkBar", glyph, new Vector2(0.34f, 0.4f), new Vector2(0.66f, 0.6f), Vector2.zero, Vector2.zero, ScreenAmber);
            return glyphGo;
        }

        private static void BuildBoxOutline(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, float thickness, Color color)
        {
            GameObject boxGo = new GameObject(name);
            boxGo.transform.SetParent(parent, false);
            RectTransform box = boxGo.AddComponent<RectTransform>();
            box.anchorMin = anchorMin;
            box.anchorMax = anchorMax;
            box.offsetMin = Vector2.zero;
            box.offsetMax = Vector2.zero;
            CreateImage("Top", box, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -thickness), Vector2.zero, color);
            CreateImage("Bottom", box, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, thickness), color);
            CreateImage("Left", box, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(thickness, 0f), color);
            CreateImage("Right", box, new Vector2(1f, 0f), Vector2.one, new Vector2(-thickness, 0f), Vector2.zero, color);
        }

        private static void BuildHackUi(Transform parent)
        {
            _hackStatusText = CreateText("HackStatus", parent, "SPLICING", ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(ScreenRowBandMinX, ScreenStatusY), ScreenValueLineSize, ScreenAmber);

            _hackTargetText = CreateText("HackTarget", parent, "FIELD TARGET", ScreenRowFontSize, FontStyles.Normal, TextAlignmentOptions.Center, new Vector2(0.5f, 0.60f), ScreenLineSize, ScreenGreen);
            _hackTargetText.characterSpacing = 3f;

            Image progressBack = CreateImage("HackBarBack", parent, new Vector2(0.16f, 0.42f), new Vector2(0.84f, 0.47f), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.22f));
            BuildBoxOutline(progressBack.rectTransform, "HackBarFrame", new Vector2(-0.01f, -0.35f), new Vector2(1.01f, 1.35f), 16f, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.55f));
            Image progressFill = CreateImage("HackBarFill", parent, new Vector2(0.16f, 0.42f), new Vector2(0.16f, 0.47f), Vector2.zero, Vector2.zero, ScreenAmber);
            _hackProgressFill = progressFill.rectTransform;
            progressBack.raycastTarget = false;

            _hackPercentText = CreateText("HackPercent", parent, "00%", ScreenReadoutFontSize, FontStyles.Bold, TextAlignmentOptions.Center, new Vector2(0.5f, 0.27f), new Vector2(1000f, 190f), ScreenAmber);
            _hackHintText = CreateText("HackHint", parent, "[" + Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.TabletBack, "BKSP") + "] ABORT", ScreenHintFontSize, FontStyles.Normal, TextAlignmentOptions.Center, new Vector2(0.5f, 0.105f), new Vector2(1170f, 112f), ScreenDim);
        }

        private static void BuildMainframeUi(Transform parent)
        {
            _mainframeStatusText = CreateText("MainframeStatus", parent, "MAINFRAME", ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(ScreenRowBandMinX, ScreenStatusY), ScreenValueLineSize, ScreenAmber);

            for (int i = 0; i < MainframeVisibleRows; i++)
            {
                float y = ScreenFirstRowY - i * ScreenRowPitch;
                Image selector = CreateImage("MainframeSelector" + i, parent, new Vector2(ScreenRowBandMinX, y - ScreenRowHalfBand), new Vector2(ScreenRowBandMaxX, y + ScreenRowHalfBand), Vector2.zero, Vector2.zero, Color.clear);
                TextMeshProUGUI label = CreateText("MainframeLabel" + i, parent, string.Empty, ScreenRowFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(ScreenRowLabelX, y), ScreenRowLabelSize, ScreenGreen);
                TextMeshProUGUI detail = CreateText("MainframeDetail" + i, parent, string.Empty, ScreenValueFontSize, FontStyles.Normal, TextAlignmentOptions.Right, new Vector2(ScreenRowDetailX, y), ScreenRowDetailSize, ScreenDim);
                selector.gameObject.SetActive(false);
                label.gameObject.SetActive(false);
                detail.gameObject.SetActive(false);
                MainframeSelectors.Add(selector);
                MainframeLabels.Add(label);
                MainframeDetails.Add(detail);
            }

            _uplinkProgressRoot = new GameObject("UplinkProgress", typeof(RectTransform));
            _uplinkProgressRoot.transform.SetParent(parent, false);
            RectTransform progressRect = _uplinkProgressRoot.GetComponent<RectTransform>();
            progressRect.anchorMin = Vector2.zero;
            progressRect.anchorMax = Vector2.one;
            progressRect.offsetMin = Vector2.zero;
            progressRect.offsetMax = Vector2.zero;
            Image uplinkBack = CreateImage("UplinkBarBack", progressRect, new Vector2(0.16f, 0.035f), new Vector2(0.72f, 0.075f), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.22f));
            BuildBoxOutline(uplinkBack.rectTransform, "UplinkBarFrame", new Vector2(-0.01f, -0.35f), new Vector2(1.01f, 1.35f), 16f, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.55f));
            Image uplinkFill = CreateImage("UplinkBarFill", progressRect, new Vector2(0.16f, 0.035f), new Vector2(0.16f, 0.075f), Vector2.zero, Vector2.zero, ScreenAmber);
            _uplinkProgressFill = uplinkFill.rectTransform;
            _uplinkProgressRoot.SetActive(false);

            _mainframePageText = CreateText("MainframePage", parent, string.Empty, ScreenHintFontSize, FontStyles.Normal, TextAlignmentOptions.Right, new Vector2(ScreenRowBandMaxX, ScreenFooterY), new Vector2(378f, 112f), ScreenDim);
        }

        private static void BuildTabBar(Transform parent)
        {
            TabLabels.Clear();
            float[] anchors = { 0.045f, 0.28f, 0.48f, 0.76f };
            for (int i = 0; i < TabNames.Length; i++)
            {
                TextMeshProUGUI label = CreateText("Tab_" + TabNames[i], parent, "  " + TabNames[i], ScreenTabFontSize, FontStyles.Normal, TextAlignmentOptions.Left, new Vector2(anchors[i], 0.945f), new Vector2(432f, 132f), ScreenDim);
                TabLabels.Add(label);
            }
            CreateImage("TabBarDivider", parent, new Vector2(0.03f, 0.895f), new Vector2(0.97f, 0.895f + ScreenDividerThickness), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.55f));
        }

        private static void BuildCrtOverlay(RectTransform root)
        {
            // Twenty-nine scanlines at 1.8 canvas units is 0.23 real pixels each: haze, not an
            // effect. Eight lines at 8 units survive the minification and still read as a CRT.
            for (int i = 1; i < 9; i++)
            {
                float y = i / 9f;
                CreateImage("Scanline" + i, root, new Vector2(0f, y), new Vector2(1f, y), new Vector2(0f, -4f), new Vector2(0f, 4f), new Color(0f, 0f, 0f, 0.22f));
            }
            _rollingScanline = CreateImage("RollingScanline", root, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, -8f), new Vector2(0f, 8f), new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.20f));
        }

        /// <summary>
        /// Dev calibration pattern for the screen aperture (workstream A). Draws the render
        /// texture's own outer edge, so "flush" is a thing you can see: the amber band has to meet
        /// the bezel the whole way round with no baked albedo screen graphic showing beside it.
        /// The corner brackets make a clipped edge obvious even where the band blends into the
        /// bezel, and the readout shows the live rect so it can be nudged without alt-tabbing.
        /// </summary>
        private static void BuildCalibrationOverlay(RectTransform root)
        {
            _calibrationRoot = new GameObject("Calibration", typeof(RectTransform));
            _calibrationRoot.transform.SetParent(root, false);
            RectTransform rect = _calibrationRoot.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            const float border = 16f;
            CreateImage("CalEdgeTop", rect, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -border), Vector2.zero, ScreenAmber);
            CreateImage("CalEdgeBottom", rect, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, border), ScreenAmber);
            CreateImage("CalEdgeLeft", rect, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(border, 0f), ScreenAmber);
            CreateImage("CalEdgeRight", rect, new Vector2(1f, 0f), Vector2.one, new Vector2(-border, 0f), Vector2.zero, ScreenAmber);

            BuildCalibrationCorner(rect, "CalCornerBL", new Vector2(0f, 0f), 1f, 1f);
            BuildCalibrationCorner(rect, "CalCornerBR", new Vector2(1f, 0f), -1f, 1f);
            BuildCalibrationCorner(rect, "CalCornerTL", new Vector2(0f, 1f), 1f, -1f);
            BuildCalibrationCorner(rect, "CalCornerTR", new Vector2(1f, 1f), -1f, -1f);

            const float crossArm = 220f;
            const float crossThick = 8f;
            CreateImage("CalCrossH", rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-crossArm, -crossThick), new Vector2(crossArm, crossThick), ScreenCyan);
            CreateImage("CalCrossV", rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-crossThick, -crossArm), new Vector2(crossThick, crossArm), ScreenCyan);

            // Kept clear of the centre crosshair's lower arm, which reaches y 0.33.
            CreateImage("CalReadoutPanel", rect, new Vector2(0.16f, 0.06f), new Vector2(0.84f, 0.31f),
                Vector2.zero, Vector2.zero, new Color(ScreenPanel.r, ScreenPanel.g, ScreenPanel.b, 0.92f));
            CreateText("CalTitle", rect, "APERTURE CAL", ScreenValueFontSize, FontStyles.Bold,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.265f), new Vector2(1200f, 110f), ScreenAmber);
            _calibrationOffsetReadout = CreateText("CalOffset", rect, string.Empty, ScreenValueFontSize, FontStyles.Normal,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.185f), new Vector2(1200f, 110f), ScreenGreen);
            _calibrationSizeReadout = CreateText("CalSize", rect, string.Empty, ScreenValueFontSize, FontStyles.Normal,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.105f), new Vector2(1200f, 110f), ScreenGreen);
            RefreshCalibrationReadout(ResolveScreenLocalOffset(), ResolveScreenLocalSize());

            _calibrationRoot.SetActive(false);
        }

        private static void BuildCalibrationCorner(RectTransform parent, string name, Vector2 corner, float signX, float signY)
        {
            const float arm = 260f;
            const float thick = 28f;
            const float inset = 34f;
            float nearX = inset * signX;
            float farX = (inset + arm) * signX;
            float nearY = inset * signY;
            float farY = (inset + arm) * signY;
            float thickX = (inset + thick) * signX;
            float thickY = (inset + thick) * signY;

            CreateImage(name + "H", parent, corner, corner,
                new Vector2(Mathf.Min(nearX, farX), Mathf.Min(nearY, thickY)),
                new Vector2(Mathf.Max(nearX, farX), Mathf.Max(nearY, thickY)), ScreenGreen);
            CreateImage(name + "V", parent, corner, corner,
                new Vector2(Mathf.Min(nearX, thickX), Mathf.Min(nearY, farY)),
                new Vector2(Mathf.Max(nearX, thickX), Mathf.Max(nearY, farY)), ScreenGreen);
        }

        private static CanvasGroup CreateGroup(string name, Transform parent)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            CanvasGroup group = go.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
            return group;
        }

        private static TextMeshProUGUI CreateText(string name, Transform parent, string text, float size, FontStyles style, TextAlignmentOptions alignment, Vector2 anchor, Vector2 boxSize, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(alignment == TextAlignmentOptions.Right ? 1f : alignment == TextAlignmentOptions.Center ? 0.5f : 0f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = boxSize;
            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            TMP_FontAsset font = ResolveFont();
            if (font != null)
                tmp.font = font;
            return tmp;
        }

        /// <summary>
        /// Text on the player's world-space crosshair canvas, NOT on the tablet screen. That canvas
        /// is a fixed 58-pixel square floating 0.8 m in front of the camera, so it is sized in that
        /// canvas's own units and is deliberately exempt from the tablet screen's typography budget.
        /// </summary>
        private static TextMeshProUGUI CreateWorldReticleText(string name, Transform parent, float size, Vector2 boxSize)
            => CreateText(name, parent, string.Empty, size, FontStyles.Bold, TextAlignmentOptions.Center, new Vector2(0.5f, 0f), boxSize, ScreenAmber);

        private static Image CreateImage(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            Image img = go.AddComponent<Image>();
            img.sprite = GetPixelSprite();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static void BuildSquareReticle(RectTransform root, List<Image> output, float thickness, float length, Color color)
        {
            output.Clear();
            output.Add(CreateReticleLine(root, "TopLeftH", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -thickness), new Vector2(length, 0f), color));
            output.Add(CreateReticleLine(root, "TopLeftV", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -length), new Vector2(thickness, 0f), color));
            output.Add(CreateReticleLine(root, "TopRightH", new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-length, -thickness), new Vector2(0f, 0f), color));
            output.Add(CreateReticleLine(root, "TopRightV", new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-thickness, -length), new Vector2(0f, 0f), color));
            output.Add(CreateReticleLine(root, "BottomLeftH", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(length, thickness), color));
            output.Add(CreateReticleLine(root, "BottomLeftV", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(thickness, length), color));
            output.Add(CreateReticleLine(root, "BottomRightH", new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-length, 0f), new Vector2(0f, thickness), color));
            output.Add(CreateReticleLine(root, "BottomRightV", new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-thickness, 0f), new Vector2(0f, length), color));
        }

        private static Image CreateReticleLine(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            return CreateImage(name, parent, anchorMin, anchorMax, offsetMin, offsetMax, color);
        }

        private static void EnsurePlayerCrosshair(Camera camera)
        {
            if (_playerReticleRoot != null)
                return;
            if (camera == null)
                return;

            _playerReticleRoot = new GameObject("Y4NGZ_FieldTablet_PlayerWorldReticle");
            UnityEngine.Object.DontDestroyOnLoad(_playerReticleRoot);
            RectTransform rt = _playerReticleRoot.AddComponent<RectTransform>();
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(WorldReticleBasePixels, WorldReticleBasePixels);
            Canvas canvas = _playerReticleRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.sortingOrder = 0;
            CanvasGroup group = _playerReticleRoot.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
            SetLayerRecursively(_playerReticleRoot, 0);
            BuildSquareReticle(rt, PlayerReticleLines, 2.2f, 13f, ScreenAmber);
            _worldReticleLabel = CreateWorldReticleText("WorldReticleLabel", rt, WorldReticleLabelFontSize, new Vector2(150f, 18f));
            _worldReticleLabel.rectTransform.anchoredPosition = new Vector2(0f, -18f);
            SetActiveIfChanged(_worldReticleLabel.gameObject, false);
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null)
                return;

            root.layer = layer;
            for (int i = 0; i < root.transform.childCount; i++)
                SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
        }

        private static bool TryRenderRadarTextureThrottled(PlayerControllerB player, Vector3 focus, bool insideFactory, bool inShipRoom, bool updateMarkers)
        {
            float time = Time.unscaledTime;
            bool modeChanged = _mode != _lastRadarRenderedMode;
            if (!modeChanged && time < _nextRadarRenderAt)
                return _radarRendered;

            _nextRadarRenderAt = time + RadarRenderInterval;
            _lastRadarRenderedMode = _mode;
            _radarRendered = RenderRadarTexture(player, focus, insideFactory, inShipRoom);
            if (_radarRendered && updateMarkers)
                UpdateRadarMarkers(player);
            return _radarRendered;
        }

        private static bool RenderRadarTexture(PlayerControllerB player, Vector3 focus, bool insideFactory, bool inShipRoom)
        {
            ManualCameraRenderer mapScreen = StartOfRound.Instance != null ? StartOfRound.Instance.mapScreen : null;
            Camera sourceCamera = mapScreen != null ? mapScreen.mapCamera : null;
            if (player == null || sourceCamera == null)
                return false;

            EnsureRadarTexture();
            EnsureRadarCamera(sourceCamera);
            if (_radarCamera == null || _radarTexture == null)
                return false;

            ConfigureRadarCamera(mapScreen, sourceCamera, focus, insideFactory, inShipRoom);
            _vanillaMapUiState = HideVanillaMapUi(mapScreen);
            GameObject contourMap = ResolveContourMap(mapScreen);
            bool restoreContour = false;
            bool contourWasActive = false;
            Vector3 contourPosition = Vector3.zero;
            if (contourMap != null)
            {
                restoreContour = true;
                contourWasActive = contourMap.activeSelf;
                contourPosition = contourMap.transform.position;
                bool showOutsideMap = !insideFactory;
                contourMap.SetActive(showOutsideMap);
                if (showOutsideMap)
                    contourMap.transform.position = new Vector3(contourPosition.x, focus.y - 1.5f, contourPosition.z);
            }

            try
            {
                _radarCamera.Render();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] radar render skipped: " + ex.Message);
                return false;
            }
            finally
            {
                if (restoreContour && contourMap != null)
                {
                    contourMap.SetActive(contourWasActive);
                    contourMap.transform.position = contourPosition;
                }
                RestoreVanillaMapUi(mapScreen);
            }

            return true;
        }

        private static void EnsureRadarTexture()
        {
            if (_radarTexture != null && _radarTexture.IsCreated())
            {
                if (_radarImage != null && _radarImage.texture != _radarTexture)
                    _radarImage.texture = _radarTexture;
                return;
            }

            _radarTexture = new RenderTexture(RadarTextureSize, RadarTextureSize, 16, RenderTextureFormat.ARGB32)
            {
                name = "Y4NGZ_FieldTablet_RadarTexture",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            _radarTexture.Create();
            if (_radarImage != null)
                _radarImage.texture = _radarTexture;
        }

        private static void EnsureRadarCamera(Camera sourceCamera)
        {
            if (sourceCamera == null)
                return;
            if (_radarCamera == null)
            {
                GameObject cameraGo = new GameObject("Y4NGZ_FieldTablet_RadarCamera") { hideFlags = HideFlags.HideAndDontSave };
                _radarCamera = cameraGo.AddComponent<Camera>();
            }
            if (_radarSourceCamera != sourceCamera)
            {
                _radarCamera.CopyFrom(sourceCamera);
                _radarSourceCamera = sourceCamera;
            }
            _radarCamera.enabled = false;
            _radarCamera.targetTexture = _radarTexture;
            _radarCamera.rect = new Rect(0f, 0f, 1f, 1f);
            _radarCamera.aspect = 1f;
        }

        private static void ConfigureRadarCamera(ManualCameraRenderer mapScreen, Camera sourceCamera, Vector3 focus, bool insideFactory, bool inShipRoom)
        {
            _radarCamera.transform.SetPositionAndRotation(
                new Vector3(focus.x, focus.y + RadarCameraHeight, focus.z),
                sourceCamera.transform.rotation);

            _radarCamera.cullingMask = sourceCamera.cullingMask;
            _radarCamera.clearFlags = sourceCamera.clearFlags;
            _radarCamera.backgroundColor = sourceCamera.backgroundColor;
            _radarCamera.orthographic = sourceCamera.orthographic;
            _radarCamera.orthographicSize = Mathf.Max(1f, sourceCamera.orthographicSize / Mathf.Max(0.1f, _radarZoom));
            _radarCamera.fieldOfView = sourceCamera.fieldOfView;

            if (inShipRoom)
            {
                _radarCamera.nearClipPlane = -0.96f;
                _radarCamera.farClipPlane = 7.52f;
            }
            else if (!insideFactory)
            {
                float near = mapScreen != null ? mapScreen.cameraNearPlane : sourceCamera.nearClipPlane;
                float far = mapScreen != null ? mapScreen.cameraFarPlane : sourceCamera.farClipPlane;
                _radarCamera.nearClipPlane = near - 18f;
                _radarCamera.farClipPlane = far + 18f;
            }
            else
            {
                _radarCamera.nearClipPlane = mapScreen != null ? mapScreen.cameraNearPlane : sourceCamera.nearClipPlane;
                _radarCamera.farClipPlane = mapScreen != null ? mapScreen.cameraFarPlane : sourceCamera.farClipPlane;
            }
        }

        private static Vector3 ResolveRadarFocus(PlayerControllerB player)
        {
            Vector3 focus = player.transform.position;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMask : ~0;
            if (Physics.Raycast(player.transform.position + Vector3.up * 0.1f, Vector3.down, out RaycastHit hit, 5f, mask, QueryTriggerInteraction.Ignore))
                focus = hit.point + Vector3.up * 0.06f;
            return focus;
        }

        private static GameObject ResolveContourMap(ManualCameraRenderer mapScreen)
        {
            if (mapScreen == null)
                return null;
            if (mapScreen.contourMap != null)
                return mapScreen.contourMap;
            try
            {
                GameObject contour = GameObject.FindGameObjectWithTag("TerrainContourMap");
                if (contour != null)
                    mapScreen.contourMap = contour;
                return contour;
            }
            catch
            {
                return null;
            }
        }

        private static VanillaMapUiState HideVanillaMapUi(ManualCameraRenderer mapScreen)
        {
            VanillaMapUiState state = new VanillaMapUiState();
            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.mapScreenPlayerName != null)
                {
                    state.MapScreenPlayerNameEnabled = round.mapScreenPlayerName.enabled;
                    round.mapScreenPlayerName.enabled = false;
                }
                if (round.mapScreenPlayerNameBG != null)
                {
                    state.MapScreenPlayerNameBgEnabled = round.mapScreenPlayerNameBG.enabled;
                    round.mapScreenPlayerNameBG.enabled = false;
                }
            }

            if (mapScreen == null)
                return state;

            if (mapScreen.headMountedCamUI != null)
            {
                state.HeadMountedCamUiEnabled = mapScreen.headMountedCamUI.enabled;
                mapScreen.headMountedCamUI.enabled = false;
            }
            if (mapScreen.localPlayerPlaceholder != null)
            {
                state.LocalPlayerPlaceholderEnabled = mapScreen.localPlayerPlaceholder.enabled;
                mapScreen.localPlayerPlaceholder.enabled = false;
            }
            if (mapScreen.compassRose != null)
            {
                state.CompassRoseEnabled = mapScreen.compassRose.enabled;
                mapScreen.compassRose.enabled = false;
            }
            if (mapScreen.shipArrowUI != null)
            {
                state.ShipArrowUiActive = mapScreen.shipArrowUI.activeSelf;
                mapScreen.shipArrowUI.SetActive(false);
            }
            if (mapScreen.shipIcon != null)
            {
                state.ShipIconActive = mapScreen.shipIcon.activeSelf;
                mapScreen.shipIcon.SetActive(false);
            }
            if (mapScreen.LostSignalUI != null)
            {
                state.LostSignalUiActive = mapScreen.LostSignalUI.activeSelf;
                mapScreen.LostSignalUI.SetActive(false);
            }
            if (mapScreen.lineFromRadarTargetToExit != null)
            {
                state.ExitLineEnabled = mapScreen.lineFromRadarTargetToExit.enabled;
                mapScreen.lineFromRadarTargetToExit.enabled = false;
            }
            return state;
        }

        private static void RestoreVanillaMapUi(ManualCameraRenderer mapScreen)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.mapScreenPlayerName != null)
                    round.mapScreenPlayerName.enabled = _vanillaMapUiState.MapScreenPlayerNameEnabled;
                if (round.mapScreenPlayerNameBG != null)
                    round.mapScreenPlayerNameBG.enabled = _vanillaMapUiState.MapScreenPlayerNameBgEnabled;
            }

            if (mapScreen == null)
                return;
            if (mapScreen.headMountedCamUI != null)
                mapScreen.headMountedCamUI.enabled = _vanillaMapUiState.HeadMountedCamUiEnabled;
            if (mapScreen.localPlayerPlaceholder != null)
                mapScreen.localPlayerPlaceholder.enabled = _vanillaMapUiState.LocalPlayerPlaceholderEnabled;
            if (mapScreen.compassRose != null)
                mapScreen.compassRose.enabled = _vanillaMapUiState.CompassRoseEnabled;
            if (mapScreen.shipArrowUI != null)
                mapScreen.shipArrowUI.SetActive(_vanillaMapUiState.ShipArrowUiActive);
            if (mapScreen.shipIcon != null)
                mapScreen.shipIcon.SetActive(_vanillaMapUiState.ShipIconActive);
            if (mapScreen.LostSignalUI != null)
                mapScreen.LostSignalUI.SetActive(_vanillaMapUiState.LostSignalUiActive);
            if (mapScreen.lineFromRadarTargetToExit != null)
                mapScreen.lineFromRadarTargetToExit.enabled = _vanillaMapUiState.ExitLineEnabled;
        }

        private static void UpdateRadarMarkers(PlayerControllerB player)
        {
            if (_radarMarkerLayer == null || _radarCamera == null || player == null)
                return;

            int used = 0;
            AddRadarMarker(ref used, player.transform.position, ScreenGreen, 16f, player.transform.eulerAngles.y);
            StartOfRound round = StartOfRound.Instance;
            if (round != null && round.allPlayerScripts != null)
            {
                for (int i = 0; i < round.allPlayerScripts.Length; i++)
                {
                    PlayerControllerB teammate = round.allPlayerScripts[i];
                    if (teammate == null || teammate == player || teammate.isPlayerDead || !teammate.isPlayerControlled || teammate.isInsideFactory != player.isInsideFactory)
                        continue;
                    AddRadarMarker(ref used, teammate.transform.position, ScreenAmber, 12f, float.NaN);
                }
            }

            EntranceTeleport[] entrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            bool wantEntranceToBuilding = !player.isInsideFactory;
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null || entrance.isEntranceToBuilding != wantEntranceToBuilding)
                    continue;
                Vector3 pos = entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
                AddRadarMarker(ref used, pos, entrance.entranceId == 0 ? ScreenCyan : ScreenAmber, entrance.entranceId == 0 ? 12f : 9f, float.NaN);
            }

            for (int i = used; i < RadarMarkers.Count; i++)
            {
                if (RadarMarkers[i] != null)
                    SetActiveIfChanged(RadarMarkers[i].gameObject, false);
            }
        }

        private static void AddRadarMarker(ref int used, Vector3 worldPosition, Color color, float size, float headingDegrees)
        {
            if (!TryWorldToRadarPosition(worldPosition + Vector3.up * 0.25f, out Vector2 anchoredPosition))
                return;

            Image marker = GetRadarMarker(used++);
            RectTransform rt = marker.rectTransform;
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = new Vector2(size, size);
            rt.localEulerAngles = float.IsNaN(headingDegrees) ? new Vector3(0f, 0f, 45f) : new Vector3(0f, 0f, -headingDegrees);
            SetColorIfChanged(marker, color);
            SetActiveIfChanged(marker.gameObject, true);
        }

        private static bool TryWorldToRadarPosition(Vector3 worldPosition, out Vector2 anchoredPosition)
        {
            anchoredPosition = Vector2.zero;
            if (_radarCamera == null || _radarMarkerLayer == null)
                return false;

            Vector3 viewport = _radarCamera.WorldToViewportPoint(worldPosition);
            if (viewport.z < 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                return false;

            Rect rect = _radarMarkerLayer.rect;
            anchoredPosition = new Vector2((viewport.x - 0.5f) * rect.width, (viewport.y - 0.5f) * rect.height);
            return true;
        }

        private static Image GetRadarMarker(int index)
        {
            while (RadarMarkers.Count <= index)
            {
                Image img = CreateImage("RadarMarker", _radarMarkerLayer, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-6f, -6f), new Vector2(6f, 6f), ScreenGreen);
                img.gameObject.SetActive(false);
                RadarMarkers.Add(img);
            }
            return RadarMarkers[index];
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

        private static string ResolveItemName(GrabbableObject item)
        {
            if (item == null)
                return "ITEM";
            if (item.itemProperties != null && !string.IsNullOrWhiteSpace(item.itemProperties.itemName))
                return item.itemProperties.itemName.ToUpperInvariant();
            return string.IsNullOrWhiteSpace(item.name) ? "ITEM" : item.name.ToUpperInvariant();
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
                case TabletHackTargetKind.DronePickup: return "DRONE PICKUP";
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

        private static void SetFloat(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
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
