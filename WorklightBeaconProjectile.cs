using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Networking;
using Y4NGZUpgrades.Patches;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades
{
    internal sealed class WorklightBeaconProjectile : MonoBehaviour
    {
        internal static readonly List<WorklightBeaconProjectile> ActiveBeacons = new List<WorklightBeaconProjectile>();
        private static readonly Dictionary<string, WorklightBeaconProjectile> ActiveBeaconsByKey = new Dictionary<string, WorklightBeaconProjectile>();

        private const float MAX_AIRBORNE_TIME = 6f;
        private const float VISUAL_SIZE = 0.24f;
        private const float PICKUP_RADIUS = 0.45f;
        private const string LAND_SOUND_FILE = "soundreality-switch-150130.mp3";

        private static AudioClip _landClip;
        private static bool _landClipLoadStarted;

        private Rigidbody _body;
        private SphereCollider _collider;
        private Light _light;
        private InteractTrigger _pickupTrigger;
        private float _spawnTime;
        private float _destroyAt;
        private int _tier;
        private bool _stuck;
        private string _key;
        private ulong _throwerNetObjId;
        private uint _beaconId;

        internal ulong ThrowerNetObjId => _throwerNetObjId;
        internal uint BeaconId => _beaconId;

        internal static bool TryGetAimedBeacon(PlayerControllerB player, out WorklightBeaconProjectile beacon)
        {
            beacon = null;
            if (player == null || player.gameplayCamera == null)
                return false;

            int interactableLayer = LayerMask.NameToLayer("InteractableObject");
            if (interactableLayer < 0)
                interactableLayer = 9;

            Ray ray = new Ray(player.gameplayCamera.transform.position, player.gameplayCamera.transform.forward);
            if (!Physics.Raycast(ray, out RaycastHit hit, 4f, 1 << interactableLayer, QueryTriggerInteraction.Collide))
                return false;

            beacon = hit.collider != null ? hit.collider.GetComponentInParent<WorklightBeaconProjectile>() : null;
            return beacon != null && beacon._stuck;
        }

        public static GameObject Spawn(Vector3 position, Vector3 direction, float throwForce, ulong throwerNetObjId, uint beaconId, int tier)
        {
            string key = MakeKey(throwerNetObjId, beaconId);
            if (ActiveBeaconsByKey.TryGetValue(key, out WorklightBeaconProjectile existing) && existing != null)
                return existing.gameObject;

            GameObject root = new GameObject("Y4NGZ_WorklightBeacon");
            root.transform.position = position;

            SphereCollider collider = root.AddComponent<SphereCollider>();
            collider.radius = 0.12f;
            collider.isTrigger = false;

            Rigidbody body = root.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.mass = 0.18f;
            body.drag = 0.05f;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            AttachVisual(root.transform);

            WorklightBeaconProjectile beacon = root.AddComponent<WorklightBeaconProjectile>();
            beacon._body = body;
            beacon._tier = Mathf.Clamp(tier, 1, 3);
            beacon._spawnTime = Time.time;
            beacon._destroyAt = Time.time + WorklightBeaconUpgrade.LIFE_SECONDS;
            beacon._key = key;
            beacon._throwerNetObjId = throwerNetObjId;
            beacon._beaconId = beaconId;
            beacon._collider = collider;
            beacon.CreateLight(active: false);
            EnsureLandSoundLoading();

            body.AddForce(direction.normalized * throwForce, ForceMode.VelocityChange);

            ActiveBeacons.Add(beacon);
            ActiveBeaconsByKey[key] = beacon;
            return root;
        }

        public static void DestroyAllActive()
        {
            for (int i = ActiveBeacons.Count - 1; i >= 0; i--)
            {
                WorklightBeaconProjectile beacon = ActiveBeacons[i];
                if (beacon != null)
                    Destroy(beacon.gameObject);
            }

            ActiveBeacons.Clear();
            ActiveBeaconsByKey.Clear();
        }

        public static void DestroySynced(ulong throwerNetObjId, uint beaconId)
        {
            string key = MakeKey(throwerNetObjId, beaconId);
            if (!ActiveBeaconsByKey.TryGetValue(key, out WorklightBeaconProjectile beacon) || beacon == null)
                return;

            Destroy(beacon.gameObject);
        }

        private static string MakeKey(ulong throwerNetObjId, uint beaconId)
        {
            return throwerNetObjId.ToString() + ":" + beaconId.ToString();
        }

        private void Update()
        {
            if (!_stuck && Time.time - _spawnTime >= MAX_AIRBORNE_TIME)
                Stick(transform.position, Vector3.up, null);

            if (Time.time >= _destroyAt)
                Destroy(gameObject);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_stuck)
                return;

            ContactPoint contact = collision != null && collision.contactCount > 0
                ? collision.GetContact(0)
                : default;

            Vector3 point = collision != null && collision.contactCount > 0
                ? contact.point
                : transform.position;
            Vector3 normal = collision != null && collision.contactCount > 0
                ? contact.normal
                : Vector3.up;
            Transform parent = collision != null && collision.transform != null
                ? collision.transform
                : null;

            Stick(point, normal, parent);
        }

        private void Stick(Vector3 point, Vector3 normal, Transform parent)
        {
            _stuck = true;
            Vector3 forward = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
            Vector3 up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            transform.position = point + forward * 0.025f;
            transform.rotation = Quaternion.LookRotation(forward, up);
            if (parent != null && parent.GetComponentInParent<PlayerControllerB>() == null && parent.GetComponentInParent<EnemyAI>() == null)
                transform.SetParent(parent, worldPositionStays: true);

            if (_body != null)
            {
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                _body.isKinematic = true;
                _body.useGravity = false;
            }

            if (_collider != null)
            {
                _collider.enabled = true;
                _collider.isTrigger = true;
                _collider.radius = PICKUP_RADIUS;
            }

            if (_light == null)
                CreateLight(active: true);
            else
                _light.enabled = true;

            PlayLandSound();
            ConfigurePickupTrigger();
        }

        private void PlayLandSound()
        {
            if (_landClip == null)
                return;

            AudioSource source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 1f;
            source.maxDistance = 18f;
            source.volume = 0.8f;
            source.PlayOneShot(_landClip);
        }

        private static void EnsureLandSoundLoading()
        {
            if (_landClip != null || _landClipLoadStarted)
                return;

            if (Y4NGZPersistentRunner.Run(LoadLandSoundClip()) != null)
                _landClipLoadStarted = true;
        }

        private static IEnumerator LoadLandSoundClip()
        {
            string path = FindLandSoundPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                Plugin.Log?.LogWarning("[WorklightBeacon] Landing sound not found under Assets/Worklight.");
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log?.LogWarning($"[WorklightBeacon] Failed to load landing sound '{path}': {request.error}");
                    yield break;
                }

                _landClip = DownloadHandlerAudioClip.GetContent(request);
                if (_landClip != null)
                    _landClip.name = "Y4NGZ_WorklightLand";
            }
        }

        private static string FindLandSoundPath()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                string direct = Path.Combine(assemblyDir, "Assets", "Worklight", LAND_SOUND_FILE);
                if (File.Exists(direct))
                    return direct;
            }

            string pluginFolder = Path.Combine(Paths.PluginPath, "Y4NGZUpgrades", "Assets", "Worklight", LAND_SOUND_FILE);
            if (File.Exists(pluginFolder))
                return pluginFolder;

            string legacyFolder = Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "Assets", "Worklight", LAND_SOUND_FILE);
            return File.Exists(legacyFolder) ? legacyFolder : null;
        }

        private void ConfigurePickupTrigger()
        {
            int interactableLayer = LayerMask.NameToLayer("InteractableObject");
            gameObject.layer = interactableLayer >= 0 ? interactableLayer : 9;

            if (_pickupTrigger == null)
                _pickupTrigger = gameObject.AddComponent<InteractTrigger>();
            if (_pickupTrigger == null)
                return;

            TrySetTag(gameObject, "InteractTrigger");
            _pickupTrigger.interactable = true;
            _pickupTrigger.oneHandedItemAllowed = true;
            _pickupTrigger.twoHandedItemAllowed = true;
            _pickupTrigger.holdInteraction = false;
            _pickupTrigger.interactCooldown = true;
            _pickupTrigger.cooldownTime = 0.2f;
            _pickupTrigger.currentCooldownValue = -0.05f;
            _pickupTrigger.hoverTip = "Pick up worklight : [" + Patches.UpgradeInteractInput.DisplayLabel() + "]";
            _pickupTrigger.disabledHoverTip = "[ Worklight recovered ]";
            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            if (localPlayer != null && localPlayer.grabItemIcon != null)
                _pickupTrigger.hoverIcon = localPlayer.grabItemIcon;
            _pickupTrigger.disableTriggerMesh = true;

            if (_pickupTrigger.onInteract == null)
                _pickupTrigger.onInteract = new InteractEvent();

            _pickupTrigger.onInteract.RemoveListener(OnPickupInteract);
            _pickupTrigger.onInteract.AddListener(OnPickupInteract);
        }

        private static void TrySetTag(GameObject go, string tag)
        {
            if (go == null || string.IsNullOrWhiteSpace(tag))
                return;

            try
            {
                go.tag = tag;
            }
            catch
            {
            }
        }

        private void OnPickupInteract(PlayerControllerB player)
        {
            WorklightBeaconPatch.TryPickupBeacon(this, player);
        }

        private void CreateLight(bool active)
        {
            GameObject lightObject = new GameObject("WorklightBeaconLight");
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.localPosition = new Vector3(0f, 0f, 0.045f);

            _light = lightObject.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(1f, 0.93f, 0.82f, 1f);
            _light.intensity = WorklightBeaconUpgrade.GetIntensity(_tier);
            _light.range = WorklightBeaconUpgrade.GetRange(_tier);
            _light.shadows = LightShadows.Soft;
            _light.enabled = active;
        }

        private static void AttachVisual(Transform parent)
        {
            GameObject basePlate = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            basePlate.name = "WorklightBeaconBase";
            basePlate.transform.SetParent(parent, worldPositionStays: false);
            basePlate.transform.localPosition = Vector3.zero;
            basePlate.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            basePlate.transform.localScale = new Vector3(VISUAL_SIZE, 0.035f, VISUAL_SIZE);
            RemoveCollider(basePlate);
            ApplyMaterial(basePlate, new Color(0.08f, 0.075f, 0.065f, 1f), 0.15f, 0.45f);

            GameObject lens = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lens.name = "WorklightBeaconLens";
            lens.transform.SetParent(parent, worldPositionStays: false);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.055f);
            lens.transform.localScale = new Vector3(0.13f, 0.13f, 0.045f);
            RemoveCollider(lens);
            ApplyMaterial(lens, new Color(1f, 0.9f, 0.72f, 1f), 0f, 0.95f);

            GameObject magnet = GameObject.CreatePrimitive(PrimitiveType.Cube);
            magnet.name = "WorklightBeaconMagnet";
            magnet.transform.SetParent(parent, worldPositionStays: false);
            magnet.transform.localPosition = new Vector3(0f, -0.015f, -0.045f);
            magnet.transform.localScale = new Vector3(0.16f, 0.03f, 0.025f);
            RemoveCollider(magnet);
            ApplyMaterial(magnet, new Color(0.55f, 0.04f, 0.025f, 1f), 0.3f, 0.55f);
        }

        private static void RemoveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);
        }

        private static void ApplyMaterial(GameObject go, Color color, float metallic, float smoothness)
        {
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null)
                return;

            Shader shader = Shader.Find("HDRP/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null)
                return;

            Material material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", color * 1.4f);
            renderer.sharedMaterial = material;
        }

        private void OnDestroy()
        {
            if (_pickupTrigger != null && _pickupTrigger.onInteract != null)
                _pickupTrigger.onInteract.RemoveListener(OnPickupInteract);
            ActiveBeacons.Remove(this);
            if (!string.IsNullOrWhiteSpace(_key))
                ActiveBeaconsByKey.Remove(_key);
        }
    }
}
