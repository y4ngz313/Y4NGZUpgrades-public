using System.Collections.Generic;
using cakeslice;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    internal enum UpgradeOutlineChannel
    {
        Danger = 0,
        Friendly = 1,
        Utility = 2
    }

    internal enum UpgradeOutlineSource
    {
        Ping = 0,
        // WP12 dead-code sweep: SquadSight = 1 is retired. SquadSightPatch outlines through
        // BuddySystem; no caller ever passed SquadSight. The remaining values keep their
        // numbers so an in-flight request dictionary key never shifts meaning.
        Generic = 2,
        SixthSense = 3,
        BuddySystem = 4
    }

    internal static class OutlineEffectBridge
    {
        private static readonly Color DangerColor = new Color(1f, 0.18f, 0.12f, 1f);
        private static readonly Color FriendlyColor = new Color(0.2f, 1f, 0.45f, 1f);
        private static readonly Color UtilityColor = new Color(0.25f, 0.85f, 1f, 1f);

        private static Camera _camera;
        private static OutlineEffect _effect;
        private static float _nextMissingShaderLogAt;

        internal static bool Show(GameObject targetRoot, UpgradeOutlineChannel channel, float duration)
        {
            return Show(targetRoot, channel, duration, UpgradeOutlineSource.Generic);
        }

        internal static bool Show(
            GameObject targetRoot,
            UpgradeOutlineChannel channel,
            float duration,
            UpgradeOutlineSource source)
        {
            if (targetRoot == null || duration <= 0f)
                return false;

            if (!EnsureEffect())
                return false;

            Y4NGZOutlineTarget driver = targetRoot.GetComponent<Y4NGZOutlineTarget>();
            if (driver == null)
                driver = targetRoot.AddComponent<Y4NGZOutlineTarget>();

            return driver.Show(source, (int)channel, duration);
        }

        internal static void Clear(GameObject targetRoot)
        {
            Clear(targetRoot, UpgradeOutlineSource.Generic);
        }

        internal static void Clear(GameObject targetRoot, UpgradeOutlineSource source)
        {
            if (targetRoot == null)
                return;

            Y4NGZOutlineTarget driver = targetRoot.GetComponent<Y4NGZOutlineTarget>();
            if (driver != null)
                driver.Clear(source);
        }

        private static bool EnsureEffect()
        {
            Camera camera = ResolveCamera();
            if (camera == null)
                return false;

            Shader outline = OutlineRuntimeAssets.LoadShader("OutlineShader");
            Shader buffer = OutlineRuntimeAssets.LoadShader("OutlineBufferShader");
            Shader overlay = OutlineRuntimeAssets.LoadShader("Y4NGZOutlineOverlayShader");
            if (outline == null || buffer == null || overlay == null)
            {
                if (Time.unscaledTime >= _nextMissingShaderLogAt)
                {
                    _nextMissingShaderLogAt = Time.unscaledTime + 10f;
                    Plugin.Log?.LogWarning("OutlineEffectBridge: outline shaders are unavailable; Ping/Squad Sight outlines may be invisible.");
                }
            }

            if (_effect != null && _camera == camera)
                return true;

            _camera = camera;
            _effect = camera.GetComponent<OutlineEffect>();
            if (_effect == null)
                _effect = camera.gameObject.AddComponent<OutlineEffect>();

            ConfigureEffect(_effect, camera);
            return true;
        }

        private static Camera ResolveCamera()
        {
            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;

            if (player != null && player.gameplayCamera != null)
                return player.gameplayCamera;

            return Camera.main;
        }

        private static void ConfigureEffect(OutlineEffect effect, Camera camera)
        {
            if (effect == null)
                return;

            effect.sourceCamera = camera;
            effect.lineThickness = 0.85f;
            effect.lineIntensity = 1.35f;
            effect.fillAmount = 0f;
            effect.lineColor0 = DangerColor;
            effect.lineColor1 = FriendlyColor;
            effect.lineColor2 = UtilityColor;
            effect.additiveRendering = true;
            effect.backfaceCulling = false;
            effect.useFillColor = false;
            effect.cornerOutlines = true;
            effect.addLinesBetweenColors = false;
            effect.autoEnableOutlines = false;
            effect.UpdateMaterialsPublicProperties();
        }

        private sealed class Y4NGZOutlineTarget : MonoBehaviour
        {
            private const float RendererRefreshIntervalSeconds = 0.5f;
            private const int MapRadarLayer = 14;
            private const int HelperGeometryFallbackLayer = 22;
            private static readonly LOD[] EmptyLods = new LOD[0];
            private static readonly string[] HelperPathNeedles =
            {
                "scannode",
                "scan node",
                "mapdot",
                "map dot",
                "radar",
                "minimap",
                "shadow"
            };
            private static readonly string[] HelperMeshNeedles =
            {
                "scannode",
                "mapdot",
                "radar",
                "minimap"
            };

            private readonly List<OutlineRecord> _records = new List<OutlineRecord>();
            private readonly List<Renderer> _rendererScanBuffer = new List<Renderer>();
            private readonly List<SkinnedMeshRenderer> _skinnedScanBuffer = new List<SkinnedMeshRenderer>();
            private readonly List<LODGroup> _lodGroupScanBuffer = new List<LODGroup>();
            private readonly List<Component> _componentScanBuffer = new List<Component>();
            private readonly List<Material> _materialScanBuffer = new List<Material>();
            private readonly List<UpgradeOutlineSource> _expiredSources = new List<UpgradeOutlineSource>();
            private readonly Dictionary<UpgradeOutlineSource, OutlineRequest> _requests =
                new Dictionary<UpgradeOutlineSource, OutlineRequest>();
            private readonly Dictionary<LODGroup, LOD[]> _lodCache = new Dictionary<LODGroup, LOD[]>();
            private readonly HashSet<Renderer> _trackedRenderers = new HashSet<Renderer>();
            private readonly HashSet<Renderer> _lodManagedRenderers = new HashSet<Renderer>();
            private readonly HashSet<Renderer> _activeLodRenderers = new HashSet<Renderer>();
            private readonly HashSet<Renderer> _scratchLodManagedRenderers = new HashSet<Renderer>();
            private readonly HashSet<Renderer> _scratchActiveLodRenderers = new HashSet<Renderer>();
            private int _channel;
            private int _lastAppliedChannel = int.MinValue;
            private int _lastActiveCount;
            private float _nextRendererRefreshAt;
            private bool _targetReferencesResolved;
            private bool _outlineStateDirty = true;
            private int _gameplayCullingMask;
            private bool _gameplayCullingMaskResolved;
            private bool _playerBodyFilterActive;
            private Renderer _playerPrimaryBodyRenderer;
            private Transform _playerPrimaryBodyParent;
            private PlayerControllerB _targetPlayer;
            private EnemyAI _targetEnemy;
            private GrabbableObject _targetItem;

            private void Awake()
            {
                ResolveTargetReferences();
            }

            internal bool Show(UpgradeOutlineSource source, int channel, float duration)
            {
                // F-GHOST-5: Sixth Sense re-Shows every in-range occluded target every 0.25s, and an
                // unconditional forceFullScan here made each of those refreshes run
                // RefreshGameplayCullingMask + RefreshActiveLodRendererSets (a GetComponentsInChildren
                // <LODGroup> plus per-LOD screen-height maths) + RefreshPlayerBodyRendererFilter + a
                // GetComponentsInChildren<Renderer> walk, and reset _nextRendererRefreshAt so the
                // 0.5s internal throttle was permanently defeated. The hierarchy only needs a fresh
                // scan when this source is new or its channel moved; a plain extension of ExpiresAt
                // can ride the throttled RefreshRendererCacheIfDue path like every Update tick does.
                OutlineRequest existing;
                bool newSource = !_requests.TryGetValue(source, out existing);
                bool channelChanged = newSource || existing.Channel != channel;

                _requests[source] = new OutlineRequest
                {
                    Channel = channel,
                    ExpiresAt = Time.time + duration
                };

                if (channelChanged)
                {
                    _outlineStateDirty = true;
                    EnsureRenderers(forceFullScan: true);
                }
                else
                {
                    RefreshRendererCacheIfDue();
                }

                ResolveActiveChannel();
                return ApplyChannel() > 0;
            }

            internal void Clear(UpgradeOutlineSource source)
            {
                if (source == UpgradeOutlineSource.Generic)
                {
                    Destroy(this);
                    return;
                }

                bool removed = _requests.Remove(source);
                if (_requests.Count <= 0)
                {
                    Destroy(this);
                    return;
                }

                if (removed)
                    _outlineStateDirty = true;
                ResolveActiveChannel();
                ApplyChannel();
            }

            private void Update()
            {
                bool removed = false;
                _expiredSources.Clear();
                foreach (KeyValuePair<UpgradeOutlineSource, OutlineRequest> pair in _requests)
                {
                    if (Time.time < pair.Value.ExpiresAt)
                        continue;

                    _expiredSources.Add(pair.Key);
                }

                if (_expiredSources.Count > 0)
                {
                    for (int i = 0; i < _expiredSources.Count; i++)
                        _requests.Remove(_expiredSources[i]);
                    _expiredSources.Clear();
                    _outlineStateDirty = true;
                    removed = true;
                }

                if (_requests.Count <= 0)
                {
                    Destroy(this);
                    return;
                }

                RefreshRendererCacheIfDue();
                if (removed)
                    ResolveActiveChannel();
                ApplyChannel();
            }

            private void ResolveTargetReferences()
            {
                if (_targetReferencesResolved)
                    return;

                _targetReferencesResolved = true;
                _targetPlayer = GetComponent<PlayerControllerB>();
                _targetEnemy = GetComponent<EnemyAI>();
                _targetItem = GetComponent<GrabbableObject>();
            }

            private void RefreshRendererCacheIfDue()
            {
                if (Time.unscaledTime < _nextRendererRefreshAt)
                    return;

                EnsureRenderers(forceFullScan: true);
            }

            private void EnsureRenderers(bool forceFullScan)
            {
                ResolveTargetReferences();
                if (!forceFullScan)
                    return;

                _nextRendererRefreshAt = Time.unscaledTime + RendererRefreshIntervalSeconds;

                // Resolve the gameplay culling mask once per scan: CommandBuffer.DrawRenderer ignores
                // camera culling masks, so map/radar helper geometry would otherwise be outlined even
                // though the gameplay camera never renders it.
                RefreshGameplayCullingMask();

                bool changed = RefreshActiveLodRendererSets();
                RefreshPlayerBodyRendererFilter();

                _rendererScanBuffer.Clear();
                GetComponentsInChildren(false, _rendererScanBuffer);
                for (int i = 0; i < _rendererScanBuffer.Count; i++)
                {
                    if (TryTrackRenderer(_rendererScanBuffer[i]))
                        changed = true;
                }
                _rendererScanBuffer.Clear();

                if (changed)
                    _outlineStateDirty = true;
            }

            private void RefreshGameplayCullingMask()
            {
                Camera camera = StartOfRound.Instance != null ? StartOfRound.Instance.activeCamera : null;
                if (camera == null)
                {
                    PlayerControllerB local = GameNetworkManager.Instance != null
                        ? GameNetworkManager.Instance.localPlayerController
                        : null;
                    if (local != null)
                        camera = local.gameplayCamera;
                }

                if (camera == null)
                    camera = ResolveCamera();

                if (camera == null)
                {
                    _gameplayCullingMaskResolved = false;
                    return;
                }

                _gameplayCullingMask = camera.cullingMask;
                _gameplayCullingMaskResolved = true;
            }

            private bool IsGameplayVisibleLayer(int layer)
            {
                if (layer < 0 || layer > 31)
                    return false;

                if (_gameplayCullingMaskResolved)
                    return (_gameplayCullingMask & (1 << layer)) != 0;

                // No camera resolvable: fall back to rejecting the known helper-geometry layers.
                return layer != MapRadarLayer && layer != HelperGeometryFallbackLayer;
            }

            // The LOD filter fails open when a target carries no LODGroup (vanilla players do not).
            // Pin players to a single body renderer so LOD1/LOD2/arms duplicates cannot stack three
            // outlines on one silhouette, while cosmetics parented to bones elsewhere still outline.
            private void RefreshPlayerBodyRendererFilter()
            {
                _playerBodyFilterActive = false;
                _playerPrimaryBodyRenderer = null;
                _playerPrimaryBodyParent = null;

                if (_targetPlayer == null || _lodManagedRenderers.Count > 0)
                    return;

                _skinnedScanBuffer.Clear();
                GetComponentsInChildren(false, _skinnedScanBuffer);

                int candidates = 0;
                Renderer firstEnabled = null;
                Renderer namedLod0 = null;
                for (int i = 0; i < _skinnedScanBuffer.Count; i++)
                {
                    SkinnedMeshRenderer skinned = _skinnedScanBuffer[i];
                    if (!IsPlayerBodyCandidate(skinned))
                        continue;

                    candidates++;
                    if (firstEnabled == null)
                        firstEnabled = skinned;
                    if (namedLod0 == null
                        && skinned.name.IndexOf("lod0", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        namedLod0 = skinned;
                    }
                }

                _skinnedScanBuffer.Clear();
                if (candidates <= 1)
                    return;

                Renderer primary = _targetPlayer.thisPlayerModel;
                if (primary == null || !IsPlayerBodyCandidate(primary))
                    primary = namedLod0 ?? firstEnabled;

                if (primary == null)
                    return;

                _playerPrimaryBodyRenderer = primary;
                _playerPrimaryBodyParent = primary.transform.parent;
                _playerBodyFilterActive = true;
            }

            private bool IsPlayerBodyCandidate(Renderer renderer)
            {
                if (renderer == null || !renderer.enabled)
                    return false;

                if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                    return false;

                if (!IsGameplayVisibleLayer(renderer.gameObject.layer))
                    return false;

                if (renderer.GetComponentInParent<PlayerControllerB>() != _targetPlayer)
                    return false;

                return renderer.GetComponentInParent<GrabbableObject>() == null
                       && !IsHelperRenderer(renderer);
            }

            private bool IsAllowedPlayerBodyRenderer(Renderer renderer)
            {
                if (!_playerBodyFilterActive || _playerPrimaryBodyRenderer == null)
                    return true;

                if (renderer == _playerPrimaryBodyRenderer)
                    return true;

                if (!(renderer is SkinnedMeshRenderer))
                    return true;

                // First-person arms live off the body root but must never contribute an outline.
                if (_targetPlayer != null && renderer == _targetPlayer.thisPlayerModelArms)
                    return false;

                // Only reject sibling body LODs; cosmetics attached to bones keep their outline.
                return renderer.transform.parent != _playerPrimaryBodyParent;
            }

            private bool RefreshActiveLodRendererSets()
            {
                _scratchLodManagedRenderers.Clear();
                _scratchActiveLodRenderers.Clear();

                Camera camera = ResolveCamera();

                _lodGroupScanBuffer.Clear();
                GetComponentsInChildren(false, _lodGroupScanBuffer);
                for (int i = 0; i < _lodGroupScanBuffer.Count; i++)
                {
                    LODGroup group = _lodGroupScanBuffer[i];
                    if (group == null || !group.enabled)
                        continue;

                    LOD[] lods = GetCachedLods(group);
                    if (lods == null || lods.Length == 0)
                        continue;

                    int primaryLod = -1;
                    for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
                    {
                        Renderer[] lodRenderers = lods[lodIndex].renderers;
                        if (lodRenderers == null)
                            continue;

                        bool hasRenderer = false;
                        for (int rendererIndex = 0; rendererIndex < lodRenderers.Length; rendererIndex++)
                        {
                            Renderer lodRenderer = lodRenderers[rendererIndex];
                            if (lodRenderer == null)
                                continue;

                            _scratchLodManagedRenderers.Add(lodRenderer);
                            hasRenderer = true;
                        }

                        if (primaryLod < 0 && hasRenderer)
                            primaryLod = lodIndex;
                    }

                    int activeLod = ResolveActiveLodIndex(group, lods, camera, primaryLod);
                    if (activeLod < 0 || activeLod >= lods.Length)
                        continue;

                    Renderer[] activeRenderers = lods[activeLod].renderers;
                    if (activeRenderers == null)
                        continue;

                    for (int rendererIndex = 0; rendererIndex < activeRenderers.Length; rendererIndex++)
                    {
                        Renderer activeRenderer = activeRenderers[rendererIndex];
                        if (activeRenderer != null)
                            _scratchActiveLodRenderers.Add(activeRenderer);
                    }
                }

                bool changed = !_lodManagedRenderers.SetEquals(_scratchLodManagedRenderers)
                    || !_activeLodRenderers.SetEquals(_scratchActiveLodRenderers);

                if (changed)
                {
                    ReplaceRendererSet(_lodManagedRenderers, _scratchLodManagedRenderers);
                    ReplaceRendererSet(_activeLodRenderers, _scratchActiveLodRenderers);
                }

                _lodGroupScanBuffer.Clear();
                _scratchLodManagedRenderers.Clear();
                _scratchActiveLodRenderers.Clear();
                return changed;
            }

            private LOD[] GetCachedLods(LODGroup group)
            {
                if (group == null)
                    return EmptyLods;

                if (_lodCache.TryGetValue(group, out LOD[] lods) && lods != null)
                    return lods;

                lods = group.GetLODs();
                if (lods == null)
                    lods = EmptyLods;

                _lodCache[group] = lods;
                return lods;
            }

            private static void ReplaceRendererSet(HashSet<Renderer> target, HashSet<Renderer> source)
            {
                target.Clear();
                foreach (Renderer renderer in source)
                {
                    if (renderer != null)
                        target.Add(renderer);
                }
            }

            private static int ResolveActiveLodIndex(LODGroup group, LOD[] lods, Camera camera, int fallbackLod)
            {
                if (group == null || lods == null || lods.Length == 0)
                    return -1;

                if (camera == null)
                    return fallbackLod;

                float relativeHeight = CalculateRelativeScreenHeight(group, camera);
                int best = lods.Length - 1;
                for (int i = 0; i < lods.Length; i++)
                {
                    if (relativeHeight >= lods[i].screenRelativeTransitionHeight)
                    {
                        best = i;
                        break;
                    }
                }

                return best;
            }

            private static float CalculateRelativeScreenHeight(LODGroup group, Camera camera)
            {
                if (group == null || camera == null)
                    return 1f;

                Transform groupTransform = group.transform;
                Vector3 referencePoint = groupTransform.TransformPoint(group.localReferencePoint);
                float distance = Vector3.Distance(camera.transform.position, referencePoint);
                if (distance <= 0.01f)
                    return 1f;

                float worldSize = Mathf.Max(0.01f, group.size * MaxAbsScale(groupTransform.lossyScale));
                if (camera.orthographic)
                    return worldSize / Mathf.Max(0.01f, camera.orthographicSize * 2f);

                float relativeHeight = worldSize / (2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
                return relativeHeight * Mathf.Max(0.01f, QualitySettings.lodBias);
            }

            private static float MaxAbsScale(Vector3 scale)
            {
                return Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            }

            private bool TryTrackRenderer(Renderer renderer)
            {
                if (renderer == null || !renderer.enabled)
                    return false;

                if (renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                    return false;

                // Shadow-only proxies draw nothing in the colour pass but the outline command buffer
                // would still stamp their silhouette.
                if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                    return false;

                if (!IsGameplayVisibleLayer(renderer.gameObject.layer))
                    return false;

                if (_trackedRenderers.Contains(renderer))
                    return false;

                if (!RendererBelongsToTarget(renderer))
                    return false;

                cakeslice.Outline outline = renderer.GetComponent<cakeslice.Outline>();
                bool created = outline == null;
                bool previousEnabled = false;
                int previousColor = 0;
                bool previousErase = false;

                if (outline == null)
                {
                    outline = renderer.gameObject.AddComponent<cakeslice.Outline>();
                }
                else
                {
                    previousEnabled = outline.enabled;
                    previousColor = outline.color;
                    previousErase = outline.eraseRenderer;
                }

                _records.Add(new OutlineRecord
                {
                    Renderer = renderer,
                    Outline = outline,
                    Created = created,
                    PreviousEnabled = previousEnabled,
                    PreviousColor = previousColor,
                    PreviousEraseRenderer = previousErase
                });
                _trackedRenderers.Add(renderer);
                return true;
            }

            private bool RendererBelongsToTarget(Renderer renderer)
            {
                if (renderer == null)
                    return false;

                if (_targetPlayer != null)
                {
                    PlayerControllerB owner = renderer.GetComponentInParent<PlayerControllerB>();
                    if (owner != _targetPlayer)
                        return false;

                    if (renderer.GetComponentInParent<EnemyAI>() != null)
                        return false;

                    if (!IsActiveLodRenderer(renderer) || IsHelperRenderer(renderer))
                        return false;

                    if (!IsAllowedPlayerBodyRenderer(renderer))
                        return false;

                    GrabbableObject heldItem = renderer.GetComponentInParent<GrabbableObject>();
                    return heldItem == null;
                }

                if (_targetEnemy != null)
                {
                    EnemyAI owner = renderer.GetComponentInParent<EnemyAI>();
                    if (owner != _targetEnemy)
                        return false;

                    if (renderer.GetComponentInParent<PlayerControllerB>() != null)
                        return false;

                    if (!IsActiveLodRenderer(renderer) || IsHelperRenderer(renderer))
                        return false;

                    GrabbableObject carriedItem = renderer.GetComponentInParent<GrabbableObject>();
                    return carriedItem == null;
                }

                if (_targetItem != null)
                {
                    GrabbableObject owner = renderer.GetComponentInParent<GrabbableObject>();
                    return owner == _targetItem && IsActiveLodRenderer(renderer);
                }

                return renderer.transform.IsChildOf(transform) && IsActiveLodRenderer(renderer);
            }

            private bool IsActiveLodRenderer(Renderer renderer)
            {
                if (renderer == null || _lodManagedRenderers.Count <= 0)
                    return true;

                return !_lodManagedRenderers.Contains(renderer) || _activeLodRenderers.Contains(renderer);
            }

            private bool IsHelperRenderer(Renderer renderer)
            {
                if (renderer == null)
                    return true;

                if (TransformOrParentsNameContainsAny(renderer.transform, HelperPathNeedles))
                    return true;

                if (HasParentComponentNamed(renderer.transform, "ScanNodeProperties"))
                    return true;

                if (ContainsAny(GetMeshName(renderer), HelperMeshNeedles))
                    return true;

                return MaterialNamesContainAny(renderer, HelperPathNeedles);
            }

            private static bool TransformOrParentsNameContainsAny(Transform current, string[] needles)
            {
                if (current == null)
                    return false;

                int guard = 0;
                while (current != null && guard++ < 48)
                {
                    if (ContainsAny(current.name, needles))
                        return true;

                    current = current.parent;
                }

                return false;
            }

            private bool HasParentComponentNamed(Transform transform, string componentTypeName)
            {
                Transform current = transform;
                int guard = 0;
                while (current != null && guard++ < 48)
                {
                    _componentScanBuffer.Clear();
                    current.GetComponents(_componentScanBuffer);
                    for (int i = 0; i < _componentScanBuffer.Count; i++)
                    {
                        Component component = _componentScanBuffer[i];
                        if (component != null && component.GetType().Name == componentTypeName)
                        {
                            _componentScanBuffer.Clear();
                            return true;
                        }
                    }

                    current = current.parent;
                }

                _componentScanBuffer.Clear();
                return false;
            }

            private static string GetMeshName(Renderer renderer)
            {
                if (renderer == null)
                    return string.Empty;

                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null && skinned.sharedMesh != null)
                    return skinned.sharedMesh.name;

                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                return meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.name : string.Empty;
            }

            private bool MaterialNamesContainAny(Renderer renderer, string[] needles)
            {
                if (renderer == null)
                    return false;

                _materialScanBuffer.Clear();
                renderer.GetSharedMaterials(_materialScanBuffer);
                for (int i = 0; i < _materialScanBuffer.Count; i++)
                {
                    Material material = _materialScanBuffer[i];
                    if (material != null && ContainsAny(material.name, needles))
                    {
                        _materialScanBuffer.Clear();
                        return true;
                    }
                }

                _materialScanBuffer.Clear();
                return false;
            }

            private static bool ContainsAny(string value, string[] needles)
            {
                if (string.IsNullOrEmpty(value))
                    return false;

                for (int i = 0; i < needles.Length; i++)
                {
                    string needle = needles[i];
                    if (!string.IsNullOrEmpty(needle)
                        && value.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool ResolveActiveChannel()
            {
                int bestChannel = (int)UpgradeOutlineChannel.Utility;
                float bestPriority = -1f;
                foreach (KeyValuePair<UpgradeOutlineSource, OutlineRequest> pair in _requests)
                {
                    float priority = ChannelPriority(pair.Value.Channel);
                    if (priority < bestPriority)
                        continue;

                    bestPriority = priority;
                    bestChannel = pair.Value.Channel;
                }

                if (_channel == bestChannel)
                    return false;

                _channel = bestChannel;
                _outlineStateDirty = true;
                return true;
            }

            private static float ChannelPriority(int channel)
            {
                if (channel == (int)UpgradeOutlineChannel.Danger)
                    return 3f;
                if (channel == (int)UpgradeOutlineChannel.Friendly)
                    return 2f;
                return 1f;
            }

            private int ApplyChannel()
            {
                if (!_outlineStateDirty && _lastAppliedChannel == _channel)
                    return _lastActiveCount;

                int activeCount = 0;
                for (int i = 0; i < _records.Count; i++)
                {
                    cakeslice.Outline outline = _records[i].Outline;
                    if (outline == null)
                        continue;

                    bool active = RendererBelongsToTarget(_records[i].Renderer);
                    if (outline.color != _channel)
                        outline.color = _channel;
                    if (outline.eraseRenderer)
                        outline.eraseRenderer = false;
                    if (outline.enabled != active)
                        outline.enabled = active;
                    if (active)
                        activeCount++;
                }

                _lastAppliedChannel = _channel;
                _lastActiveCount = activeCount;
                _outlineStateDirty = false;
                return activeCount;
            }

            private void OnDestroy()
            {
                for (int i = 0; i < _records.Count; i++)
                {
                    OutlineRecord record = _records[i];
                    if (record.Outline == null)
                        continue;

                    if (record.Created)
                    {
                        Destroy(record.Outline);
                    }
                    else
                    {
                        record.Outline.color = record.PreviousColor;
                        record.Outline.eraseRenderer = record.PreviousEraseRenderer;
                        record.Outline.enabled = record.PreviousEnabled;
                    }
                }

                _records.Clear();
                _rendererScanBuffer.Clear();
                _skinnedScanBuffer.Clear();
                _lodGroupScanBuffer.Clear();
                _componentScanBuffer.Clear();
                _materialScanBuffer.Clear();
                _expiredSources.Clear();
                _lodCache.Clear();
                _trackedRenderers.Clear();
                _lodManagedRenderers.Clear();
                _activeLodRenderers.Clear();
                _scratchLodManagedRenderers.Clear();
                _scratchActiveLodRenderers.Clear();
            }
        }

        private struct OutlineRequest
        {
            internal int Channel;
            internal float ExpiresAt;
        }

        private struct OutlineRecord
        {
            internal Renderer Renderer;
            internal cakeslice.Outline Outline;
            internal bool Created;
            internal bool PreviousEnabled;
            internal int PreviousColor;
            internal bool PreviousEraseRenderer;
        }
    }
}
