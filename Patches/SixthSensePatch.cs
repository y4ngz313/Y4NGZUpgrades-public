using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class SixthSensePatch
    {
        private const float ObjectiveRescanInterval = 5f;
        private const float StaticRescanInterval = 5f;
        private const float ItemRescanInterval = 1f;
        private const int ObjectiveTypesPerRefreshTick = 3;
        private const float StaticRescanPhaseOffset = ObjectiveRescanInterval * 0.5f;
        private const string CompanyAssemblyName = "Y4NGZCompany";
        private const string ShipSystemsAssemblyName = "Y4NGZShipSystems";
        private const string MonitorTakeoverAssemblyName = "Y4NGZMonitorTakeover";
        private const int MaxRenderableCacheEntries = 1024;

        private struct ObjectiveCandidate
        {
            internal Component Component;
            internal GameObject Root;
            internal Vector3 Position;
            internal int RootKey;
        }

        private struct RenderableTargetCacheEntry
        {
            internal GameObject Root;
            internal Vector3 CenterOffset;
        }

        private static readonly string[] ObjectiveNeedles =
        {
            "mainframe",
            "stash",
            "company",
            "bomb",
            "shipping",
            "container",
            "breaker",
            "blackout",
            "survey"
        };

        private static readonly Dictionary<int, GameObject> ActiveOutlines = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, RenderableTargetCacheEntry> RenderableTargetCache = new Dictionary<int, RenderableTargetCacheEntry>();
        private static readonly List<ObjectiveCandidate> ObjectiveCandidates = new List<ObjectiveCandidate>();
        private static readonly List<ObjectiveCandidate> PendingObjectiveCandidates = new List<ObjectiveCandidate>();
        private static readonly HashSet<int> SeenThisRefresh = new HashSet<int>();
        private static readonly HashSet<int> SeenObjectiveRootsThisRefresh = new HashSet<int>();
        private static EntranceTeleport[] _cachedEntrances = Array.Empty<EntranceTeleport>();
        private static Turret[] _cachedTurrets = Array.Empty<Turret>();
        private static Landmine[] _cachedMines = Array.Empty<Landmine>();
        private static GrabbableObject[] _cachedItems = Array.Empty<GrabbableObject>();
        private static Type[] _objectiveTypes;
        private static bool _objectiveTypesResolved;
        private static bool _sceneCacheInvalidationHooked;
        private static float _nextRefreshTime;
        private static float _nextObjectiveRescanTime;
        private static float _nextStaticRescanTime;
        private static float _nextItemRescanTime;
        private static int _objectiveRebuildNextTypeIndex;
        private static bool _objectiveRebuildInProgress;

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostLateUpdate(PlayerControllerB __instance)
        {
            EnsureSceneCacheInvalidationHooked();

            if (!IsLocalPlayer(__instance))
                return;

            if (!IsActiveRound())
            {
                ClearAll();
                return;
            }

            if (!SixthSenseUpgrade.IsUnlocked() || __instance.isPlayerDead)
            {
                ClearAll();
                return;
            }

            if (Time.time < _nextRefreshTime)
                return;

            _nextRefreshTime = Time.time + SixthSenseUpgrade.REFRESH_INTERVAL;
            RefreshTargets(__instance);
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            EnsureSceneCacheInvalidationHooked();
            ClearRenderableTargetCache();
            ClearAll();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            ClearRenderableTargetCache();
            ClearAll();
        }

        private static void RefreshTargets(PlayerControllerB player)
        {
            SeenThisRefresh.Clear();

            ScanEntrances(player);
            ScanObjectives(player);
            ScanTurrets(player);
            ScanMines(player);

            if (SixthSenseUpgrade.ShowsItems())
                ScanItems(player);

            if (SixthSenseUpgrade.ShowsEnemies())
                ScanEnemies(player);

            ClearStale();
        }

        private static void ScanEntrances(PlayerControllerB player)
        {
            EnsureStaticTargetCacheFresh();

            EntranceTeleport[] entrances = _cachedEntrances;
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null)
                    continue;

                ResolveRenderableTarget(
                    entrance.gameObject,
                    entrance.gameObject,
                    ResolveEntranceCenter(entrance),
                    out GameObject root,
                    out Vector3 center);
                ConsiderTarget(player, root, center, SixthSenseUpgrade.BASE_RANGE, UpgradeOutlineChannel.Utility);
            }
        }

        private static void ScanObjectives(PlayerControllerB player)
        {
            if (!_objectiveRebuildInProgress && Time.time >= _nextObjectiveRescanTime)
                BeginObjectiveCacheRebuild();

            ContinueObjectiveCacheRebuild();

            SeenObjectiveRootsThisRefresh.Clear();
            for (int i = 0; i < ObjectiveCandidates.Count; i++)
            {
                ObjectiveCandidate candidate = ObjectiveCandidates[i];
                if (candidate.Component == null || candidate.Root == null)
                    continue;

                if (SeenObjectiveRootsThisRefresh.Contains(candidate.RootKey))
                    continue;

                SeenObjectiveRootsThisRefresh.Add(candidate.RootKey);
                ConsiderTarget(
                    player,
                    candidate.Root,
                    candidate.Position,
                    SixthSenseUpgrade.BASE_RANGE,
                    UpgradeOutlineChannel.Utility);
            }
        }

        private static void ScanTurrets(PlayerControllerB player)
        {
            EnsureStaticTargetCacheFresh();

            Turret[] turrets = _cachedTurrets;
            for (int i = 0; i < turrets.Length; i++)
            {
                Turret turret = turrets[i];
                if (turret == null || !turret.turretActive)
                    continue;

                ResolveRenderableTarget(
                    turret.gameObject,
                    turret.gameObject,
                    turret.transform.position,
                    out GameObject root,
                    out Vector3 center);
                ConsiderTarget(player, root, center, SixthSenseUpgrade.HAZARD_RANGE, UpgradeOutlineChannel.Danger);
            }
        }

        private static void ScanMines(PlayerControllerB player)
        {
            EnsureStaticTargetCacheFresh();

            Landmine[] mines = _cachedMines;
            for (int i = 0; i < mines.Length; i++)
            {
                Landmine mine = mines[i];
                if (mine == null || mine.hasExploded)
                    continue;

                ResolveRenderableTarget(
                    mine.gameObject,
                    mine.gameObject,
                    mine.transform.position,
                    out GameObject root,
                    out Vector3 center);
                ConsiderTarget(player, root, center, SixthSenseUpgrade.HAZARD_RANGE, UpgradeOutlineChannel.Danger);
            }
        }

        private static void ScanItems(PlayerControllerB player)
        {
            if (Time.time >= _nextItemRescanTime)
                RebuildItemCache();

            GrabbableObject[] items = _cachedItems;
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item == null
                    || item.itemProperties == null
                    || !item.grabbable
                    || item.deactivated
                    || item.isHeld
                    || item.isHeldByEnemy)
                {
                    continue;
                }

                ResolveRenderableTarget(
                    item.gameObject,
                    item.gameObject,
                    item.transform.position,
                    out GameObject root,
                    out Vector3 center);
                ConsiderTarget(player, root, center, SixthSenseUpgrade.ITEM_RANGE, UpgradeOutlineChannel.Utility);
            }
        }

        private static void ScanEnemies(PlayerControllerB player)
        {
            RoundManager roundManager = RoundManager.Instance;
            List<EnemyAI> enemies = roundManager != null ? roundManager.SpawnedEnemies : null;
            if (enemies == null)
                return;

            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (enemy == null || enemy.isEnemyDead)
                    continue;

                ConsiderTarget(
                    player,
                    enemy.gameObject,
                    ResolveEnemyCenter(enemy),
                    SixthSenseUpgrade.ENEMY_RANGE,
                    UpgradeOutlineChannel.Danger);
            }
        }

        private static void ConsiderTarget(
            PlayerControllerB player,
            GameObject root,
            Vector3 position,
            float range,
            UpgradeOutlineChannel channel)
        {
            if (player == null || root == null || range <= 0f)
                return;

            if ((position - player.transform.position).sqrMagnitude > range * range)
                return;

            if (HasDirectLineOfSight(player, root, position))
                return;

            int key = root.GetInstanceID();
            SeenThisRefresh.Add(key);
            if (OutlineEffectBridge.Show(root, channel, SixthSenseUpgrade.OUTLINE_SECONDS, UpgradeOutlineSource.SixthSense))
                ActiveOutlines[key] = root;
        }

        private static bool HasDirectLineOfSight(PlayerControllerB player, GameObject root, Vector3 position)
        {
            if (player == null || root == null || player.gameplayCamera == null)
                return false;

            Vector3 eyePosition = player.gameplayCamera.transform.position;
            if ((position - eyePosition).sqrMagnitude <= 0.0001f)
                return true;

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            RaycastHit hit;
            if (!Physics.Linecast(eyePosition, position, out hit, mask, QueryTriggerInteraction.Ignore))
                return true;

            return IsIgnoredLineOfSightHit(hit.collider, root, player);
        }

        private static bool IsIgnoredLineOfSightHit(Collider collider, GameObject targetRoot, PlayerControllerB player)
        {
            if (collider == null)
                return false;

            Transform hitTransform = collider.transform;
            if (BelongsToTransformRoot(hitTransform, targetRoot != null ? targetRoot.transform : null))
                return true;

            return player != null && BelongsToTransformRoot(hitTransform, player.transform);
        }

        private static bool BelongsToTransformRoot(Transform hitTransform, Transform ownerTransform)
        {
            if (hitTransform == null || ownerTransform == null)
                return false;

            if (hitTransform == ownerTransform || hitTransform.IsChildOf(ownerTransform))
                return true;

            Transform hitRoot = hitTransform.root;
            Transform ownerRoot = ownerTransform.root;
            return hitRoot != null && ownerRoot != null && hitRoot == ownerRoot;
        }

        private static void BeginObjectiveCacheRebuild()
        {
            _nextObjectiveRescanTime = Time.time + ObjectiveRescanInterval;
            _objectiveRebuildInProgress = true;
            _objectiveRebuildNextTypeIndex = 0;
            PendingObjectiveCandidates.Clear();
        }

        private static void ContinueObjectiveCacheRebuild()
        {
            if (!_objectiveRebuildInProgress)
                return;

            Type[] objectiveTypes = ResolveObjectiveTypes();
            if (objectiveTypes == null || objectiveTypes.Length == 0)
            {
                CompleteObjectiveCacheRebuild();
                return;
            }

            int scannedThisTick = 0;
            while (_objectiveRebuildNextTypeIndex < objectiveTypes.Length
                && scannedThisTick < ObjectiveTypesPerRefreshTick)
            {
                ScanObjectiveType(objectiveTypes[_objectiveRebuildNextTypeIndex], PendingObjectiveCandidates);
                _objectiveRebuildNextTypeIndex++;
                scannedThisTick++;
            }

            if (_objectiveRebuildNextTypeIndex >= objectiveTypes.Length)
                CompleteObjectiveCacheRebuild();
        }

        private static void ScanObjectiveType(Type objectiveType, List<ObjectiveCandidate> candidates)
        {
            if (objectiveType == null || candidates == null)
                return;

            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsByType(objectiveType, FindObjectsSortMode.None);
            for (int objectIndex = 0; objectIndex < objects.Length; objectIndex++)
            {
                Component component = objects[objectIndex] as Component;
                if (component == null)
                    continue;

                if (!ResolveObjectiveTarget(component, out GameObject root, out Vector3 center))
                    continue;

                candidates.Add(new ObjectiveCandidate
                {
                    Component = component,
                    Root = root,
                    Position = center,
                    RootKey = root.GetInstanceID()
                });
            }
        }

        private static void CompleteObjectiveCacheRebuild()
        {
            ObjectiveCandidates.Clear();
            ObjectiveCandidates.AddRange(PendingObjectiveCandidates);
            PendingObjectiveCandidates.Clear();
            _objectiveRebuildInProgress = false;
            _objectiveRebuildNextTypeIndex = 0;
        }

        private static void EnsureStaticTargetCacheFresh()
        {
            if (Time.time < _nextStaticRescanTime)
                return;

            if (ObjectiveScanDueThisFrame())
            {
                _nextStaticRescanTime = Time.time + StaticRescanPhaseOffset;
                return;
            }

            RebuildStaticTargetCaches();
        }

        private static bool ObjectiveScanDueThisFrame()
        {
            return _objectiveRebuildInProgress || Time.time >= _nextObjectiveRescanTime;
        }

        private static void RebuildStaticTargetCaches()
        {
            _nextStaticRescanTime = Time.time + StaticRescanInterval;
            _cachedEntrances = UnityEngine.Object.FindObjectsByType<EntranceTeleport>(FindObjectsSortMode.None);
            _cachedTurrets = UnityEngine.Object.FindObjectsByType<Turret>(FindObjectsSortMode.None);
            _cachedMines = UnityEngine.Object.FindObjectsByType<Landmine>(FindObjectsSortMode.None);
        }

        private static void RebuildItemCache()
        {
            _nextItemRescanTime = Time.time + ItemRescanInterval;
            _cachedItems = UnityEngine.Object.FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None);
        }

        private static Type[] ResolveObjectiveTypes()
        {
            if (_objectiveTypesResolved)
                return _objectiveTypes;

            _objectiveTypesResolved = true;

            // Y4NGZCompany#393 split the Company namespace across several assemblies, so
            // "the Y4NGZCompany assembly" is no longer a single object to find. Scan every
            // assembly and keep the filter on the namespace, which the split preserved.
            List<Assembly> companyAssemblies = FindCompanyAssemblies();
            if (companyAssemblies.Count == 0)
            {
                _objectiveTypes = Array.Empty<Type>();
                Plugin.Log?.LogInfo("[Sixth Sense] No Y4NGZCompany assembly loaded; objective scan disabled.");
                return _objectiveTypes;
            }

            List<Type> objectiveTypes = new List<Type>();
            for (int a = 0; a < companyAssemblies.Count; a++)
            {
                Type[] assemblyTypes = GetLoadableTypes(companyAssemblies[a]);
                for (int i = 0; i < assemblyTypes.Length; i++)
                {
                    Type type = assemblyTypes[i];
                    if (LooksLikeY4ngzCompanyObjectiveType(type))
                        objectiveTypes.Add(type);
                }
            }

            _objectiveTypes = objectiveTypes.ToArray();
            Plugin.Log?.LogInfo($"[Sixth Sense] Resolved {_objectiveTypes.Length} Y4NGZCompany objective component type(s) across {companyAssemblies.Count} assembly/assemblies.");
            return _objectiveTypes;
        }

        private static List<Assembly> FindCompanyAssemblies()
        {
            List<Assembly> found = new List<Assembly>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                string name = assembly?.GetName().Name;
                if (name == CompanyAssemblyName
                    || name == ShipSystemsAssemblyName
                    || name == MonitorTakeoverAssemblyName
                    || name == "LethalCCTV"
                    || name == "Y4NGZCore"
                    || name == "Y4NGZBloodFX"
                    || name == "Y4NGZAtmosphere")
                {
                    found.Add(assembly);
                }
            }

            return found;
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types ?? Array.Empty<Type>();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static bool LooksLikeY4ngzCompanyObjectiveType(Type type)
        {
            if (type == null || type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                return false;

            // Namespace, not assembly: the split moved types between DLLs but kept every one of
            // them under Y4NGZCompany.* (Y4NGZCompany#393).
            if (type.Namespace == null || !type.Namespace.StartsWith(CompanyAssemblyName + ".", StringComparison.Ordinal))
                return false;

            string typeName = type.Name;
            if (string.IsNullOrEmpty(typeName))
            {
                return false;
            }

            for (int i = 0; i < ObjectiveNeedles.Length; i++)
            {
                if (typeName.IndexOf(ObjectiveNeedles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ResolveObjectiveTarget(Component component, out GameObject root, out Vector3 center)
        {
            root = null;
            center = Vector3.zero;
            if (component == null)
                return false;

            ResolveRenderableTarget(
                component.gameObject,
                component.gameObject,
                component.transform.position,
                out root,
                out center);

            if (root != null)
                return true;

            NetworkObject networkObject = component.GetComponentInParent<NetworkObject>();
            root = networkObject != null ? networkObject.gameObject : component.gameObject;
            center = root != null ? root.transform.position : component.transform.position;
            return root != null;
        }

        private static void ResolveRenderableTarget(
            GameObject preferredRoot,
            GameObject fallbackRoot,
            Vector3 fallbackCenter,
            out GameObject root,
            out Vector3 center)
        {
            GameObject cacheSource = preferredRoot != null ? preferredRoot : fallbackRoot;
            if (cacheSource == null)
            {
                root = null;
                center = fallbackCenter;
                return;
            }

            int key = cacheSource.GetInstanceID();
            RenderableTargetCacheEntry entry;
            if (RenderableTargetCache.TryGetValue(key, out entry))
            {
                if (entry.Root != null)
                {
                    root = entry.Root;
                    center = root.transform.position + entry.CenterOffset;
                    return;
                }

                RenderableTargetCache.Remove(key);
            }

            if (RenderableTargetCache.Count >= MaxRenderableCacheEntries)
                RenderableTargetCache.Clear();

            root = ResolveRenderableRootUncached(preferredRoot, fallbackRoot);
            if (root == null)
            {
                center = fallbackCenter;
                return;
            }

            center = ResolveRendererCenterUncached(root, fallbackCenter);
            RenderableTargetCache[key] = new RenderableTargetCacheEntry
            {
                Root = root,
                CenterOffset = center - root.transform.position
            };
        }

        private static GameObject ResolveRenderableRootUncached(GameObject preferredRoot, GameObject fallbackRoot)
        {
            if (HasUsableRenderer(preferredRoot))
                return preferredRoot;

            if (HasUsableRenderer(fallbackRoot))
                return fallbackRoot;

            Transform cursor = preferredRoot != null ? preferredRoot.transform.parent : null;
            while (cursor != null)
            {
                if (HasUsableRenderer(cursor.gameObject))
                    return cursor.gameObject;

                NetworkObject networkObject = cursor.GetComponent<NetworkObject>();
                if (networkObject != null)
                    return cursor.gameObject;

                cursor = cursor.parent;
            }

            return preferredRoot != null ? preferredRoot : fallbackRoot;
        }

        private static bool HasUsableRenderer(GameObject root)
        {
            if (root == null)
                return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: false);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;
                if (renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                    continue;

                return true;
            }

            return false;
        }

        private static Vector3 ResolveRendererCenterUncached(GameObject root, Vector3 fallback)
        {
            if (root == null)
                return fallback;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: false);
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;
                if (renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds ? bounds.center : fallback;
        }

        private static void EnsureSceneCacheInvalidationHooked()
        {
            if (_sceneCacheInvalidationHooked)
                return;

            _sceneCacheInvalidationHooked = true;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private static void OnActiveSceneChanged(Scene previousScene, Scene nextScene)
        {
            ClearRenderableTargetCache();
            ClearTargetDiscoveryCaches();
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            ClearRenderableTargetCache();
            ClearTargetDiscoveryCaches();
        }

        private static void ClearRenderableTargetCache()
        {
            RenderableTargetCache.Clear();
        }

        private static void ClearTargetDiscoveryCaches()
        {
            ObjectiveCandidates.Clear();
            PendingObjectiveCandidates.Clear();
            _objectiveRebuildInProgress = false;
            _objectiveRebuildNextTypeIndex = 0;
            _cachedEntrances = Array.Empty<EntranceTeleport>();
            _cachedTurrets = Array.Empty<Turret>();
            _cachedMines = Array.Empty<Landmine>();
            _cachedItems = Array.Empty<GrabbableObject>();
            _nextObjectiveRescanTime = 0f;
            _nextStaticRescanTime = Time.time + StaticRescanPhaseOffset;
            _nextItemRescanTime = 0f;
        }

        private static Vector3 ResolveEntranceCenter(EntranceTeleport entrance)
        {
            if (entrance == null)
                return Vector3.zero;

            return entrance.entrancePoint != null
                ? entrance.entrancePoint.position + Vector3.up * 1.2f
                : entrance.transform.position + Vector3.up * 1.2f;
        }

        private static Vector3 ResolveEnemyCenter(EnemyAI enemy)
        {
            if (enemy == null)
                return Vector3.zero;
            if (enemy.eye != null)
                return enemy.eye.position;

            Renderer renderer = null;
            if (enemy.skinnedMeshRenderers != null)
            {
                for (int i = 0; i < enemy.skinnedMeshRenderers.Length; i++)
                {
                    if (enemy.skinnedMeshRenderers[i] == null)
                        continue;

                    renderer = enemy.skinnedMeshRenderers[i];
                    break;
                }
            }

            if (renderer == null && enemy.meshRenderers != null)
            {
                for (int i = 0; i < enemy.meshRenderers.Length; i++)
                {
                    if (enemy.meshRenderers[i] == null)
                        continue;

                    renderer = enemy.meshRenderers[i];
                    break;
                }
            }

            return renderer != null ? renderer.bounds.center : enemy.transform.position + Vector3.up * 1.2f;
        }

        private static void ClearStale()
        {
            List<int> stale = null;
            foreach (KeyValuePair<int, GameObject> pair in ActiveOutlines)
            {
                if (pair.Value != null && SeenThisRefresh.Contains(pair.Key))
                    continue;

                if (stale == null)
                    stale = new List<int>();
                stale.Add(pair.Key);
            }

            if (stale == null)
                return;

            for (int i = 0; i < stale.Count; i++)
            {
                int key = stale[i];
                GameObject root;
                if (ActiveOutlines.TryGetValue(key, out root) && root != null)
                    OutlineEffectBridge.Clear(root, UpgradeOutlineSource.SixthSense);

                ActiveOutlines.Remove(key);
            }
        }

        private static void ClearAll()
        {
            foreach (KeyValuePair<int, GameObject> pair in ActiveOutlines)
            {
                if (pair.Value != null)
                    OutlineEffectBridge.Clear(pair.Value, UpgradeOutlineSource.SixthSense);
            }

            ActiveOutlines.Clear();
            SeenThisRefresh.Clear();
            SeenObjectiveRootsThisRefresh.Clear();
            ClearTargetDiscoveryCaches();
            _nextRefreshTime = 0f;
        }

        private static bool IsActiveRound()
        {
            StartOfRound round = StartOfRound.Instance;
            return round != null && !round.inShipPhase && round.shipDoorsEnabled;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }
    }
}
