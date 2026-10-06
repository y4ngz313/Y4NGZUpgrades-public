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
            _screenLayoutDirty = true;
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
                // F-TABLET-13: non-null but !IsCreated() means Unity released the RT under us
                // (device or resolution loss, alt-tab on some drivers). Overwriting the reference
                // leaked the old object for the session, so release it first - the same order
                // ApplyScreenRenderTextureSize uses.
                if (_screenRenderTexture != null)
                {
                    _screenRenderTexture.Release();
                    DestroyObject(_screenRenderTexture);
                    _screenRenderTexture = null;
                }
                // Opens at the last bucket this session measured, so the common case never
                // rebuilds; the first open of a session starts at the fixed fallback.
                _screenRenderTexture = CreateScreenRenderTexture(_screenRtWidth, ScreenRtHeightFor(_screenRtWidth));
                _screenLayoutDirty = true;
                _screenRendersSinceRebind = 0;
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
            _screenCamera.orthographicSize = FieldTabletLayout.ScreenHeight * 0.5f;
            _screenCamera.aspect = FieldTabletLayout.ScreenWidth / (float)FieldTabletLayout.ScreenHeight;
            _screenCamera.nearClipPlane = 0.1f;
            _screenCamera.farClipPlane = 30f;

            _screenCanvasRoot = new GameObject("Y4NGZ_FieldTablet_ScreenCanvas", typeof(RectTransform));
            _screenCanvasRoot.transform.SetParent(_screenRigRoot.transform, false);
            Canvas canvas = _screenCanvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = _screenCamera;
            canvas.sortingOrder = 0;

            RectTransform root = _screenCanvasRoot.GetComponent<RectTransform>();
            root.sizeDelta = new Vector2(FieldTabletLayout.ScreenWidth, FieldTabletLayout.ScreenHeight);

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
            _screenLayoutDirty = true;
            _screenRendersSinceRebind = 0;
        }

        private static int ScreenRtHeightFor(int width)
            => Mathf.Max(1, Mathf.RoundToInt(width * (FieldTabletLayout.ScreenHeight / (float)FieldTabletLayout.ScreenWidth)));

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

            // #500: while the Get animation still has the quad at the camera, a corner that has just
            // crossed the near plane projects to tens of thousands of pixels and pinned the bucket
            // at the 1024 cap. Such a sample keeps the current bucket, does not count toward the
            // shrink hysteresis and is not the bucket line this open logs.
            if (!IsPlausibleScreenSpan(camera, spanX, spanY))
            {
                if (!_screenRtRejectLoggedThisOpen)
                {
                    _screenRtRejectLoggedThisOpen = true;
                    Plugin.Log?.LogDebug($"[Field Tablet] screen RT rejected measuredSpan={spanX:0}x{spanY:0}px camera={camera.pixelWidth}x{camera.pixelHeight}");
                }
                return;
            }

            // The canvas is 1.4408:1 (F-TABLET-18: not 16:10), so whichever axis is more demanding
            // decides the bucket - the ScreenWidth/ScreenHeight ratio below is that number.
            float needed = Mathf.Max(spanX, spanY * (FieldTabletLayout.ScreenWidth / (float)FieldTabletLayout.ScreenHeight));
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

        private static bool IsPlausibleScreenSpan(Camera camera, float spanX, float spanY)
        {
            if (float.IsNaN(spanX) || float.IsInfinity(spanX) || float.IsNaN(spanY) || float.IsInfinity(spanY))
                return false;
            return spanX <= camera.pixelWidth * ScreenRtMaxSpanViewportRatio
                && spanY <= camera.pixelHeight * ScreenRtMaxSpanViewportRatio;
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
            _screenLayoutDirty = true;
            _screenRendersSinceRebind = 0;
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
    }
}
