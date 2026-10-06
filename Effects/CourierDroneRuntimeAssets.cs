using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace Y4NGZUpgrades.Effects
{
    internal static class CourierDroneRuntimeAssets
    {
        private const string BundleName = "y4ngz-steampunkdrone.lethalbundle";
        private const string AudioAssetFolderName = "CourierDrone";
        private const string PropellerLoopFileName = "PropellerLoop.mp3";
        private const string WakeupFileName = "WakeupRobot.mp3";
        private const string SendToEntranceFileName = "CommandSendToEntrance.mp3";
        private const string RecallFileName = "CommandRecall.mp3";
        private const string MagnetPickupFileName = "MagnetPickupLoop.mp3";
        private const float TargetLargestDimension = 0.78f;
        private const float PropellerVolume = 0.34f;
        private const float CommandVolume = 0.78f;
        private const float WakeupVolume = 0.82f;
        private const float MagnetVolume = 0.54f;
        private const float ImpactVolume = 0.72f;
        private const float FallbackImpactPitch = 0.6f;

        private static readonly List<CourierDroneAudioController> ActiveAudioControllers = new List<CourierDroneAudioController>();
        private static AssetBundle _bundle;
        private static GameObject _prefab;
        private static bool _loadAttempted;
        private static bool _loggedPrefab;
        private static bool _audioLoadStarted;
        private static AudioClip _propellerLoopClip;
        private static AudioClip _wakeupClip;
        private static AudioClip _sendToEntranceClip;
        private static AudioClip _recallClip;
        private static AudioClip _magnetPickupClip;
        private static AudioClip _impactClip;
        private static Material _dissolveMaterial;
        private static bool _dissolveMaterialLoadAttempted;
        private static bool _impactClipLoadAttempted;
        private static AudioMixerGroup _worldMixerGroup;
        private static readonly Dictionary<string, GameObject> CompanionPrefabs = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        internal static GameObject InstantiateDroneVisual(Vector3 position, Quaternion rotation)
        {
            GameObject prefab = LoadDronePrefab();
            if (prefab == null)
                return null;

            GameObject drone = UnityEngine.Object.Instantiate(prefab, position, rotation);
            drone.name = "Y4NGZ_SteampunkDrone";
            drone.SetActive(true);
            ForceLiveBodyRenderable(drone);
            StripColliders(drone);
            NormalizeSize(drone);
            AddMarkerLight(drone);
            EnsureSteampunkController(drone);
            EnsureDroneAudio(drone);
            TryFixMixerGroups(drone);
            LogSpawnDiagnostics(drone);
            return drone;
        }

        /// <summary>
        /// F-DRONE-11: the drone's AudioSources never had an outputAudioMixerGroup, so drone SFX
        /// bypassed the game mixer entirely - they ignored the player's SFX slider and were never
        /// occluded or ducked like vanilla sounds. Called after EnsureDroneAudio so the three runtime
        /// sources already exist on the object DawnLib walks.
        /// </summary>
        private static void TryFixMixerGroups(GameObject drone)
        {
            if (drone == null)
                return;

            try
            {
                DawnLibCompat.FixMixerGroups(drone);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Courier Drone mixer group fixup skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Direct fallback for <see cref="TryFixMixerGroups"/>: borrows the group vanilla already
        /// routes a player's world audio through, so the drone obeys the same volume and routing
        /// even when DawnLib's walk misses a source added at runtime.
        /// </summary>
        private static AudioMixerGroup ResolveWorldMixerGroup()
        {
            if (_worldMixerGroup != null)
                return _worldMixerGroup;

            PlayerControllerB[] players = StartOfRound.Instance != null ? StartOfRound.Instance.allPlayerScripts : null;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                AudioSource source = player != null ? player.movementAudio : null;
                if (source != null && source.outputAudioMixerGroup != null)
                {
                    _worldMixerGroup = source.outputAudioMixerGroup;
                    return _worldMixerGroup;
                }
            }

            return null;
        }

        internal static SteampunkDroneController EnsureSteampunkController(GameObject drone)
        {
            if (drone == null)
                return null;

            SteampunkDroneController controller = drone.GetComponentInChildren<SteampunkDroneController>(includeInactive: true);
            if (controller == null)
            {
                Animator animator = drone.GetComponentInChildren<Animator>(includeInactive: true);
                GameObject host = animator != null ? animator.gameObject : drone;
                controller = host.AddComponent<SteampunkDroneController>();
            }

            controller.Initialize(drone);
            return controller;
        }

        internal static void PlayLiftOff(GameObject drone)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            if (controller != null)
                controller.PlayLiftOff();
        }

        internal static void PlayLand(GameObject drone)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            if (controller != null)
                controller.PlayLand();
        }

        internal static void PlayMaterializeIn(GameObject drone)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            if (controller != null)
                controller.PlayMaterializeIn();
        }

        internal static float PlayDematerializeOut(GameObject drone)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            return controller != null ? controller.PlayDematerializeOut() : 0f;
        }

        internal static void PlayHitReaction(GameObject drone, bool largeHit, Vector3 joltDirection, float joltDistance, float joltSeconds)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            if (controller != null)
            {
                controller.PlayHurt(largeHit);
                controller.PlayHitJolt(joltDirection, joltDistance, joltSeconds);
            }

            CourierDroneAudioController audio = GetAudioController(drone);
            if (audio != null)
                audio.PlayImpact();
        }

        internal static void PlayFireAttack(GameObject drone)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            if (controller != null)
                controller.PlayFireAttack();
        }

        internal static bool PlayDeath(GameObject drone)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            if (controller == null)
                return false;

            controller.PlayDeath();
            return controller.HasAnimator;
        }

        internal static void SetFlashlight(GameObject drone, bool enabled)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            if (controller != null)
                controller.SetFlashlight(enabled);
        }

        internal static Transform FindCarryAnchor(GameObject drone)
        {
            SteampunkDroneController controller = EnsureSteampunkController(drone);
            return controller != null ? controller.CarryAnchor : null;
        }

        internal static Transform FindGrenadeMuzzle(GameObject drone)
        {
            if (drone == null)
                return null;

            return FindNamedTransform(drone.transform, "Y4NGZ_GrenadeMuzzle");
        }

        internal static GameObject InstantiateCompanionPrefab(string assetName, Vector3 position, Quaternion rotation)
        {
            GameObject prefab = LoadCompanionPrefab(assetName);
            return prefab != null ? UnityEngine.Object.Instantiate(prefab, position, rotation) : null;
        }

        internal static Material LoadDissolveMaterial()
        {
            if (_dissolveMaterialLoadAttempted)
                return _dissolveMaterial;

            _dissolveMaterialLoadAttempted = true;
            EnsureBundleLoaded();
            if (_bundle == null)
                return null;

            string[] directNames =
            {
                "M_SteampunkDroneDissolve",
                "assets/y4ngz/steampunkdrone/materials/m_steampunkdronedissolve.mat",
                "m_steampunkdronedissolve",
            };

            for (int i = 0; i < directNames.Length; i++)
            {
                Material material = _bundle.LoadAsset<Material>(directNames[i]);
                if (IsDissolveMaterial(material))
                {
                    _dissolveMaterial = material;
                    return _dissolveMaterial;
                }
            }

            string[] assetNames = _bundle.GetAllAssetNames();
            for (int i = 0; i < assetNames.Length; i++)
            {
                string normalized = assetNames[i].Replace('\\', '/');
                if (normalized.IndexOf("dissolve", StringComparison.OrdinalIgnoreCase) < 0
                    || !normalized.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Material material = _bundle.LoadAsset<Material>(assetNames[i]);
                if (IsDissolveMaterial(material))
                {
                    _dissolveMaterial = material;
                    return _dissolveMaterial;
                }
            }

            return null;
        }

        internal static void EnsureDroneAudio(GameObject drone)
        {
            if (drone == null)
                return;

            CourierDroneAudioController audio = drone.GetComponent<CourierDroneAudioController>();
            if (audio == null)
                audio = drone.AddComponent<CourierDroneAudioController>();
            audio.Initialize();
        }

        internal static void PlayWakeupSound(GameObject drone)
        {
            CourierDroneAudioController audio = GetAudioController(drone);
            if (audio != null)
                audio.PlayWakeup();
        }

        internal static void PlaySendToEntranceSound(GameObject drone)
        {
            CourierDroneAudioController audio = GetAudioController(drone);
            if (audio != null)
                audio.PlaySendToEntrance();
        }

        internal static void PlayRecallSound(GameObject drone)
        {
            CourierDroneAudioController audio = GetAudioController(drone);
            if (audio != null)
                audio.PlayRecall();
        }

        internal static void StartMagnetSound(GameObject drone)
        {
            CourierDroneAudioController audio = GetAudioController(drone);
            if (audio != null)
                audio.StartMagnetLoop();
        }

        internal static void StopMagnetSound(GameObject drone)
        {
            CourierDroneAudioController audio = drone != null ? drone.GetComponent<CourierDroneAudioController>() : null;
            if (audio != null)
                audio.StopMagnetLoop();
        }

        /// <summary>
        /// F-DRONE-12: silences the wreck. Deliberately does NOT go through EnsureDroneAudio - the
        /// drone is being torn down, so adding an audio controller back onto it would be wrong.
        /// </summary>
        internal static void StopPropellerSound(GameObject drone)
        {
            CourierDroneAudioController audio = drone != null ? drone.GetComponent<CourierDroneAudioController>() : null;
            if (audio != null)
                audio.StopPropellerLoop();
        }

        private static CourierDroneAudioController GetAudioController(GameObject drone)
        {
            if (drone == null)
                return null;

            EnsureDroneAudio(drone);
            return drone.GetComponent<CourierDroneAudioController>();
        }

        private static GameObject LoadDronePrefab()
        {
            if (_prefab != null)
                return _prefab;

            EnsureBundleLoaded();
            if (_bundle == null)
                return null;

            _prefab = TryLoadNamedPrefab("Y4NGZ_SteampunkDrone")
                ?? TryLoadNamedPrefab("Y4NGZ_CourierDrone")
                ?? TryLoadNamedPrefab("HoveringDrone")
                ?? TryLoadNamedPrefab("Hovering drone")
                ?? FindFirstRenderablePrefab();

            if (_prefab == null)
                Plugin.Log?.LogWarning($"Courier Drone bundle '{BundleName}' did not contain a renderable prefab.");
            else if (!_loggedPrefab)
            {
                _loggedPrefab = true;
                int rendererCount = _prefab.GetComponentsInChildren<Renderer>(includeInactive: true).Length;
                Plugin.Log?.LogInfo($"Courier Drone visual loaded from '{BundleName}': {_prefab.name} ({rendererCount} renderer(s)).");
            }

            return _prefab;
        }

        private static GameObject TryLoadNamedPrefab(string assetName)
        {
            if (_bundle == null || string.IsNullOrWhiteSpace(assetName))
                return null;

            GameObject prefab = _bundle.LoadAsset<GameObject>(assetName);
            if (HasRenderer(prefab))
                return prefab;

            foreach (string path in _bundle.GetAllAssetNames())
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), assetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                prefab = _bundle.LoadAsset<GameObject>(path);
                if (HasRenderer(prefab))
                    return prefab;
            }

            return null;
        }

        private static GameObject LoadCompanionPrefab(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
                return null;
            if (CompanionPrefabs.TryGetValue(assetName, out GameObject cached))
                return cached;

            EnsureBundleLoaded();
            if (_bundle == null)
                return null;

            GameObject prefab = _bundle.LoadAsset<GameObject>(assetName);
            if (prefab == null)
            {
                foreach (string path in _bundle.GetAllAssetNames())
                {
                    if (!string.Equals(Path.GetFileNameWithoutExtension(path), assetName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    prefab = _bundle.LoadAsset<GameObject>(path);
                    if (prefab != null)
                        break;
                }
            }

            // F-DRONE-16: cache the miss as well. A missing asset used to return null silently AND
            // rescan GetAllAssetNames on every single call, so an absent grenade-explosion prefab
            // read in game as "the grenade does nothing" with no hint in the log. Caching null both
            // stops the rescan and makes the warning fire exactly once per asset name.
            CompanionPrefabs[assetName] = prefab;
            if (prefab == null)
            {
                Plugin.Log?.LogWarning(
                    $"Courier Drone companion prefab '{assetName}' is missing from bundle '{BundleName}'; using the vanilla fallback effect.");
            }

            return prefab;
        }

        private static Transform FindNamedTransform(Transform root, string exactName)
        {
            if (root == null || string.IsNullOrWhiteSpace(exactName))
                return null;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform != null && string.Equals(transform.name, exactName, StringComparison.OrdinalIgnoreCase))
                    return transform;
            }

            return null;
        }

        private static GameObject FindFirstRenderablePrefab()
        {
            if (_bundle == null)
                return null;

            GameObject[] prefabs = _bundle.LoadAllAssets<GameObject>();
            for (int i = 0; i < prefabs.Length; i++)
            {
                if (HasRenderer(prefabs[i]))
                    return prefabs[i];
            }

            return null;
        }

        private static bool HasRenderer(GameObject prefab)
        {
            return prefab != null && prefab.GetComponentInChildren<Renderer>(includeInactive: true) != null;
        }

        private static bool IsDissolveMaterial(Material material)
        {
            return material != null
                && material.shader != null
                && material.shader.name.IndexOf("Dissolve", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void EnsureBundleLoaded()
        {
            if (_loadAttempted)
                return;

            _loadAttempted = true;

            foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
            {
                // LoadAsset on a streamed scene bundle throws (BetterArmory-public #1), and after a
                // lobby cycle LethalLevelLoader keeps moon scene bundles in this list.
                if (loaded == null || loaded.isStreamedSceneAssetBundle)
                    continue;

                GameObject probe;
                try
                {
                    probe = loaded.LoadAsset<GameObject>("Y4NGZ_SteampunkDrone")
                        ?? loaded.LoadAsset<GameObject>("Y4NGZ_CourierDrone");
                }
                catch (System.Exception)
                {
                    // Foreign bundle in an unknown state — a probe miss, not an error.
                    continue;
                }

                if (HasRenderer(probe))
                {
                    _bundle = loaded;
                    return;
                }
            }

            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string bundlePath = Path.Combine(dllDir ?? "", BundleName);
            if (!File.Exists(bundlePath))
            {
                Plugin.Log?.LogWarning($"Courier Drone bundle not found at {bundlePath}. Falling back to placeholder visual.");
                return;
            }

            _bundle = AssetBundle.LoadFromFile(bundlePath);
            if (_bundle == null)
                Plugin.Log?.LogWarning($"Failed to load Courier Drone bundle at {bundlePath}. Falling back to placeholder visual.");
        }

        private static void StripColliders(GameObject drone)
        {
            Collider[] colliders = drone.GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || SteampunkDroneController.IsBrokenPieceTransform(collider.transform))
                    continue;

                UnityEngine.Object.Destroy(collider);
            }
        }

        private static void ForceLiveBodyRenderable(GameObject drone)
        {
            Renderer[] renderers = drone.GetComponentsInChildren<Renderer>(includeInactive: true);
            Shader fallbackShader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsLiveBodyRenderer(renderer))
                    continue;

                ActivatePathToRoot(renderer.transform, drone.transform);
                renderer.gameObject.SetActive(true);
                renderer.enabled = true;

                Material[] materials = renderer.materials;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material material = materials[j];
                    if (material == null)
                        continue;

                    if (fallbackShader != null && (material.shader == null || !material.shader.isSupported))
                    {
                        material.shader = fallbackShader;
                        material.color = new Color(0.55f, 0.95f, 1f, 1f);
                    }
                }
            }

            Animator animator = drone.GetComponentInChildren<Animator>(includeInactive: true);
            if (animator != null && !SteampunkDroneController.IsBrokenPieceTransform(animator.transform))
                ActivatePathToRoot(animator.transform, drone.transform);
        }

        private static void NormalizeSize(GameObject drone)
        {
            Renderer[] renderers = drone.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (!TryCalculateLiveBodyBounds(renderers, out Bounds bounds, out _))
                return;

            float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (largest <= 0.001f || float.IsNaN(largest) || float.IsInfinity(largest))
                return;

            float scale = TargetLargestDimension / largest;
            if (scale <= 0.001f || float.IsNaN(scale) || float.IsInfinity(scale))
                return;

            drone.transform.localScale *= scale;
        }

        private static bool TryCalculateLiveBodyBounds(Renderer[] renderers, out Bounds bounds, out int activeCount)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            activeCount = 0;
            if (renderers == null)
                return false;

            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsLiveBodyRenderer(renderer) || !renderer.gameObject.activeInHierarchy || !renderer.enabled)
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

                activeCount++;
            }

            return hasBounds;
        }

        private static bool IsLiveBodyRenderer(Renderer renderer)
        {
            return renderer != null
                && !SteampunkDroneController.IsBrokenPieceTransform(renderer.transform)
                && !IsFlashlightConeTransform(renderer.transform)
                && !IsEffectRenderer(renderer);
        }

        private static bool IsFlashlightConeTransform(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                string name = current.name ?? string.Empty;
                if (name.IndexOf("Y4NGZ_FlashlightCone", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("FlashlightCone", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("LightCone", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                current = current.parent;
            }

            return false;
        }

        private static bool IsEffectRenderer(Renderer renderer)
        {
            string name = renderer != null ? renderer.name ?? string.Empty : string.Empty;
            return renderer is ParticleSystemRenderer
                || renderer is TrailRenderer
                || renderer is LineRenderer
                || name.IndexOf("Trail", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Dust", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Smoke", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Explosion", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("FireAttack", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ActivatePathToRoot(Transform transform, Transform root)
        {
            Transform current = transform;
            while (current != null)
            {
                current.gameObject.SetActive(true);
                if (current == root)
                    break;
                current = current.parent;
            }
        }

        private static void LogSpawnDiagnostics(GameObject drone)
        {
            Renderer[] renderers = drone.GetComponentsInChildren<Renderer>(includeInactive: true);
            TryCalculateLiveBodyBounds(renderers, out Bounds bounds, out int activeCount);
            Plugin.Log?.LogInfo(
                $"Courier Drone visual spawned rendererCount={renderers.Length} activeCount={activeCount} finalScale={FormatVector(drone.transform.localScale)} boundsSize={FormatVector(bounds.size)}");
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
        }

        private static void AddMarkerLight(GameObject drone)
        {
            Light light = drone.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.45f, 0.95f, 1f);
            light.range = 4.5f;
            light.intensity = 1.35f;
            light.shadows = LightShadows.None;
        }

        private static void RegisterAudioController(CourierDroneAudioController controller)
        {
            if (controller == null || ActiveAudioControllers.Contains(controller))
                return;

            ActiveAudioControllers.Add(controller);
        }

        private static void UnregisterAudioController(CourierDroneAudioController controller)
        {
            ActiveAudioControllers.Remove(controller);
        }

        private static AudioClip ResolveImpactClip(out float pitch)
        {
            AudioClip bundleClip = LoadImpactClipFromBundle();
            if (bundleClip != null)
            {
                pitch = 1f;
                return bundleClip;
            }

            pitch = FallbackImpactPitch;
            return _sendToEntranceClip ?? _recallClip ?? _wakeupClip ?? _magnetPickupClip;
        }

        private static AudioClip LoadImpactClipFromBundle()
        {
            if (_impactClipLoadAttempted)
                return _impactClip;

            _impactClipLoadAttempted = true;
            EnsureBundleLoaded();
            if (_bundle == null)
                return null;

            string[] assetNames = _bundle.GetAllAssetNames();
            for (int i = 0; i < assetNames.Length; i++)
            {
                string normalized = assetNames[i].Replace('\\', '/');
                if (!LooksLikeImpactAudio(normalized))
                    continue;

                AudioClip clip = _bundle.LoadAsset<AudioClip>(assetNames[i]);
                if (clip != null)
                {
                    _impactClip = clip;
                    return _impactClip;
                }
            }

            return null;
        }

        private static bool LooksLikeImpactAudio(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
                return false;

            bool audioFile = assetName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                || assetName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                || assetName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || assetName.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase)
                || assetName.EndsWith(".aif", StringComparison.OrdinalIgnoreCase);
            if (!audioFile)
                return false;

            return assetName.IndexOf("hurt", StringComparison.OrdinalIgnoreCase) >= 0
                || assetName.IndexOf("impact", StringComparison.OrdinalIgnoreCase) >= 0
                || assetName.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void EnsureAudioLoading(MonoBehaviour fallbackHost)
        {
            if (_audioLoadStarted)
                return;

            MonoBehaviour host = Y4NGZPersistentRunner.Host != null
                ? Y4NGZPersistentRunner.Host
                : fallbackHost;
            if (host == null)
                return;

            _audioLoadStarted = true;
            host.StartCoroutine(LoadCourierDroneAudio());
        }

        private static IEnumerator LoadCourierDroneAudio()
        {
            yield return LoadAudioClip(PropellerLoopFileName, "Y4NGZ_CourierDrone_PropellerLoop", clip => _propellerLoopClip = clip);
            NotifyAudioControllers();

            yield return LoadAudioClip(WakeupFileName, "Y4NGZ_CourierDrone_Wakeup", clip => _wakeupClip = clip);
            NotifyAudioControllers();

            yield return LoadAudioClip(SendToEntranceFileName, "Y4NGZ_CourierDrone_SendToEntrance", clip => _sendToEntranceClip = clip);
            NotifyAudioControllers();

            yield return LoadAudioClip(RecallFileName, "Y4NGZ_CourierDrone_Recall", clip => _recallClip = clip);
            NotifyAudioControllers();

            yield return LoadAudioClip(MagnetPickupFileName, "Y4NGZ_CourierDrone_MagnetPickup", clip => _magnetPickupClip = clip);
            NotifyAudioControllers();
        }

        private static IEnumerator LoadAudioClip(string fileName, string clipName, Action<AudioClip> assignClip)
        {
            string path = FindCourierDroneAudioPath(fileName);
            if (string.IsNullOrWhiteSpace(path))
            {
                Plugin.Log?.LogWarning($"Courier Drone audio '{fileName}' was not found under Assets/{AudioAssetFolderName}.");
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log?.LogWarning($"Courier Drone audio '{fileName}' failed to load: {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip != null)
                    clip.name = clipName;
                assignClip?.Invoke(clip);
            }
        }

        private static string FindCourierDroneAudioPath(string fileName)
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(assemblyDir))
                return null;

            string direct = Path.Combine(assemblyDir, "Assets", AudioAssetFolderName, fileName);
            return File.Exists(direct) ? direct : null;
        }

        private static void NotifyAudioControllers()
        {
            for (int i = ActiveAudioControllers.Count - 1; i >= 0; i--)
            {
                CourierDroneAudioController controller = ActiveAudioControllers[i];
                if (controller == null)
                {
                    ActiveAudioControllers.RemoveAt(i);
                    continue;
                }

                controller.RefreshLoadedClips();
            }
        }

        private sealed class CourierDroneAudioController : MonoBehaviour
        {
            private AudioSource _propellerSource;
            private AudioSource _oneShotSource;
            private AudioSource _magnetSource;
            private bool _initialized;
            private bool _pendingWakeup;
            private bool _pendingSendToEntrance;
            private bool _pendingRecall;
            private bool _pendingImpact;
            private bool _magnetRequested;
            private bool _propellerSuppressed;

            internal void Initialize()
            {
                EnsureSources();
                ApplyMixerGroup();
                RegisterAudioController(this);
                EnsureAudioLoading(this);
                RefreshLoadedClips();
            }

            internal void PlayWakeup()
            {
                PlayOrQueue(ref _pendingWakeup, _wakeupClip, WakeupVolume);
            }

            internal void PlaySendToEntrance()
            {
                PlayOrQueue(ref _pendingSendToEntrance, _sendToEntranceClip, CommandVolume);
            }

            internal void PlayRecall()
            {
                PlayOrQueue(ref _pendingRecall, _recallClip, CommandVolume);
            }

            internal void PlayImpact()
            {
                AudioClip clip = ResolveImpactClip(out float pitch);
                PlayOrQueue(ref _pendingImpact, clip, ImpactVolume, pitch);
            }

            internal void StartMagnetLoop()
            {
                EnsureSources();
                _magnetRequested = true;
                EnsureAudioLoading(this);

                if (_magnetPickupClip == null || _magnetSource == null)
                    return;

                _magnetSource.clip = _magnetPickupClip;
                if (!_magnetSource.isPlaying)
                    _magnetSource.Play();
            }

            internal void StopMagnetLoop()
            {
                _magnetRequested = false;
                if (_magnetSource != null)
                    _magnetSource.Stop();
            }

            /// <summary>
            /// F-DRONE-12: the suppression flag matters because RefreshLoadedClips is called back
            /// whenever a clip finishes loading, which would otherwise restart the loop on a wreck
            /// that is waiting out its death animation.
            /// </summary>
            internal void StopPropellerLoop()
            {
                _propellerSuppressed = true;
                if (_propellerSource != null)
                    _propellerSource.Stop();
            }

            internal void RefreshLoadedClips()
            {
                EnsureSources();
                ApplyMixerGroup();

                if (!_propellerSuppressed && _propellerSource != null && _propellerLoopClip != null)
                {
                    _propellerSource.clip = _propellerLoopClip;
                    if (isActiveAndEnabled && !_propellerSource.isPlaying)
                        _propellerSource.Play();
                }

                if (_pendingWakeup && _wakeupClip != null)
                    PlayOrQueue(ref _pendingWakeup, _wakeupClip, WakeupVolume);

                if (_pendingSendToEntrance && _sendToEntranceClip != null)
                    PlayOrQueue(ref _pendingSendToEntrance, _sendToEntranceClip, CommandVolume);

                if (_pendingRecall && _recallClip != null)
                    PlayOrQueue(ref _pendingRecall, _recallClip, CommandVolume);

                if (_pendingImpact)
                {
                    AudioClip impact = ResolveImpactClip(out float pitch);
                    PlayOrQueue(ref _pendingImpact, impact, ImpactVolume, pitch);
                }

                if (_magnetRequested && _magnetPickupClip != null)
                    StartMagnetLoop();
            }

            private void Awake()
            {
                Initialize();
            }

            private void OnEnable()
            {
                Initialize();
            }

            private void OnDisable()
            {
                UnregisterAudioController(this);
                if (_propellerSource != null)
                    _propellerSource.Stop();
                if (_magnetSource != null)
                    _magnetSource.Stop();
            }

            private void OnDestroy()
            {
                UnregisterAudioController(this);
            }

            private void EnsureSources()
            {
                if (_initialized)
                    return;

                _initialized = true;
                _propellerSource = gameObject.AddComponent<AudioSource>();
                ConfigureWorldSource(_propellerSource, PropellerVolume, loop: true, minDistance: 3.5f, maxDistance: 28f);

                _oneShotSource = gameObject.AddComponent<AudioSource>();
                ConfigureWorldSource(_oneShotSource, 1f, loop: false, minDistance: 5f, maxDistance: 34f);

                _magnetSource = gameObject.AddComponent<AudioSource>();
                ConfigureWorldSource(_magnetSource, MagnetVolume, loop: true, minDistance: 3.5f, maxDistance: 22f);
                ApplyMixerGroup();
            }

            /// <summary>
            /// F-DRONE-11: routes the three runtime sources through the game mixer. Re-run from
            /// Initialize/RefreshLoadedClips because StartOfRound - and therefore the group this
            /// borrows - may not exist yet the first time the sources are built.
            /// </summary>
            private void ApplyMixerGroup()
            {
                AudioMixerGroup group = ResolveWorldMixerGroup();
                if (group == null)
                    return;

                if (_propellerSource != null)
                    _propellerSource.outputAudioMixerGroup = group;
                if (_oneShotSource != null)
                    _oneShotSource.outputAudioMixerGroup = group;
                if (_magnetSource != null)
                    _magnetSource.outputAudioMixerGroup = group;
            }

            private static void ConfigureWorldSource(AudioSource source, float volume, bool loop, float minDistance, float maxDistance)
            {
                if (source == null)
                    return;

                source.playOnAwake = false;
                source.loop = loop;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = minDistance;
                source.maxDistance = maxDistance;
                source.volume = volume;
            }

            private void PlayOrQueue(ref bool pending, AudioClip clip, float volume, float pitch = 1f)
            {
                EnsureSources();
                EnsureAudioLoading(this);

                if (clip == null || _oneShotSource == null)
                {
                    pending = true;
                    return;
                }

                pending = false;
                float previousPitch = _oneShotSource.pitch;
                _oneShotSource.pitch = Mathf.Max(0.1f, pitch);
                _oneShotSource.PlayOneShot(clip, volume);
                _oneShotSource.pitch = previousPitch;
            }
        }
    }
}
