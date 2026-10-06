using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZUpgrades.Patches;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades
{
    internal sealed class WorklightBeaconProjectile : MonoBehaviour
    {
        internal static readonly List<WorklightBeaconProjectile> ActiveBeacons = new List<WorklightBeaconProjectile>();
        private static readonly Dictionary<string, WorklightBeaconProjectile> ActiveBeaconsByKey = new Dictionary<string, WorklightBeaconProjectile>();

        // F-FOREMAN-B-2: a stick notice can outrun the throw notice it belongs to (the remote
        // spawn waits out the release delay first), so an early snap is parked here and applied
        // the moment that beacon spawns.
        private static readonly Dictionary<string, PendingStick> PendingSticks = new Dictionary<string, PendingStick>();
        private const float PENDING_STICK_TTL = 15f;

        private const float MAX_AIRBORNE_TIME = 6f;
        private const float VISUAL_SIZE = 0.24f;
        private const float PICKUP_RADIUS = 0.45f;
        private const string LAND_SOUND_FILE = "soundreality-switch-150130.mp3";

        private static AudioClip _landClip;
        private static bool _landClipLoadStarted;

        // F-FOREMAN-B-8: one shared material per part instead of three new Materials (and three
        // Shader.Find calls) per beacon, none of which were ever destroyed.
        private static readonly Material[] SharedPartMaterials = new Material[3];
        private const int MATERIAL_BASE = 0;
        private const int MATERIAL_LENS = 1;
        private const int MATERIAL_MAGNET = 2;

        private struct PendingStick
        {
            public Vector3 Point;
            public Vector3 Normal;
            public ulong ParentNetObjId;
            public float ReceivedAt;
        }

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
            // F-FOREMAN-B-10: the burn clock starts when the beacon lands (see Stick), not here -
            // up to 6 s of the advertised 180 used to be spent dark in flight.
            beacon._key = key;
            beacon._throwerNetObjId = throwerNetObjId;
            beacon._beaconId = beaconId;
            beacon._collider = collider;
            beacon.CreateLight(active: false);
            EnsureLandSoundLoading();

            // F-FOREMAN-B-5: the release point sits inside the thrower's own capsule in first
            // person, so without this the beacon drops at their feet.
            IgnoreThrowerCollisions(collider, throwerNetObjId);

            body.AddForce(direction.normalized * throwForce, ForceMode.VelocityChange);

            ActiveBeacons.Add(beacon);
            ActiveBeaconsByKey[key] = beacon;

            if (PendingSticks.TryGetValue(key, out PendingStick pending))
            {
                PendingSticks.Remove(key);
                beacon.SnapToAuthoritativePose(pending.Point, pending.Normal, pending.ParentNetObjId);
            }

            return root;
        }

        private static void IgnoreThrowerCollisions(Collider beaconCollider, ulong throwerNetObjId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (beaconCollider == null || players == null)
                return;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || player.NetworkObjectId != throwerNetObjId)
                    continue;

                if (player.thisController != null)
                    Physics.IgnoreCollision(beaconCollider, player.thisController, true);
                Collider[] colliders = player.GetComponentsInChildren<Collider>(true);
                for (int c = 0; c < colliders.Length; c++)
                {
                    if (colliders[c] != null)
                        Physics.IgnoreCollision(beaconCollider, colliders[c], true);
                }
                return;
            }
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
            PendingSticks.Clear();
        }

        /// <summary>
        /// Returns true only when a live beacon was actually removed, which is what lets the
        /// thrower's client tell a real teammate pickup from a redundant relay (F-FOREMAN-B-9).
        /// </summary>
        public static bool DestroySynced(ulong throwerNetObjId, uint beaconId)
        {
            string key = MakeKey(throwerNetObjId, beaconId);
            PendingSticks.Remove(key);
            if (!ActiveBeaconsByKey.TryGetValue(key, out WorklightBeaconProjectile beacon) || beacon == null)
                return false;

            Destroy(beacon.gameObject);
            return true;
        }

        // F-FOREMAN-B-20: the host caps how many beacons one thrower may have live at once.
        internal static int CountActiveFor(ulong throwerNetObjId)
        {
            int count = 0;
            for (int i = 0; i < ActiveBeacons.Count; i++)
            {
                WorklightBeaconProjectile beacon = ActiveBeacons[i];
                if (beacon != null && beacon._throwerNetObjId == throwerNetObjId)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// F-FOREMAN-B-2: the thrower's simulation is authoritative for where a beacon ends up.
        /// Every other machine snaps to the pose it reports instead of trusting its own physics.
        /// </summary>
        internal static void ApplyStickFromNetwork(ulong throwerNetObjId, uint beaconId, Vector3 point, Vector3 normal, ulong parentNetObjId)
        {
            string key = MakeKey(throwerNetObjId, beaconId);
            if (ActiveBeaconsByKey.TryGetValue(key, out WorklightBeaconProjectile beacon) && beacon != null)
            {
                beacon.SnapToAuthoritativePose(point, normal, parentNetObjId);
                return;
            }

            PrunePendingSticks();
            PendingSticks[key] = new PendingStick
            {
                Point = point,
                Normal = normal,
                ParentNetObjId = parentNetObjId,
                ReceivedAt = Time.time
            };
        }

        private static void PrunePendingSticks()
        {
            if (PendingSticks.Count == 0)
                return;

            float cutoff = Time.time - PENDING_STICK_TTL;
            List<string> stale = null;
            foreach (KeyValuePair<string, PendingStick> pair in PendingSticks)
            {
                if (pair.Value.ReceivedAt > cutoff)
                    continue;
                if (stale == null)
                    stale = new List<string>();
                stale.Add(pair.Key);
            }

            if (stale == null)
                return;
            for (int i = 0; i < stale.Count; i++)
                PendingSticks.Remove(stale[i]);
        }

        private static Transform ResolveParentByNetworkObjectId(ulong parentNetObjId)
        {
            if (parentNetObjId == 0UL)
                return null;

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.SpawnManager == null)
                return null;

            return network.SpawnManager.SpawnedObjects.TryGetValue(parentNetObjId, out NetworkObject netObject) && netObject != null
                ? netObject.transform
                : null;
        }

        private static ulong ResolveParentNetworkObjectId(Transform parent)
        {
            if (parent == null)
                return 0UL;

            NetworkObject netObject = parent.GetComponentInParent<NetworkObject>();
            return netObject != null ? netObject.NetworkObjectId : 0UL;
        }

        private static string MakeKey(ulong throwerNetObjId, uint beaconId)
        {
            return throwerNetObjId.ToString() + ":" + beaconId.ToString();
        }

        private void Update()
        {
            if (!_stuck && Time.time - _spawnTime >= MAX_AIRBORNE_TIME)
                StickFromLocalSimulation(transform.position, Vector3.up, null);

            // _destroyAt is only armed by Stick (F-FOREMAN-B-10), so an airborne beacon has no
            // expiry yet; the airborne backstop above guarantees one within MAX_AIRBORNE_TIME.
            if (_stuck && Time.time >= _destroyAt)
                Destroy(gameObject);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_stuck)
                return;

            // F-FOREMAN-B-5: bounce off actors instead of freezing in mid-air where they stood.
            // Sticking only ever refused to *parent* to a player or an enemy, which left the
            // beacon hanging exactly where the teammate was clipped.
            Transform hit = collision != null ? collision.transform : null;
            if (hit != null
                && (hit.GetComponentInParent<PlayerControllerB>() != null
                    || hit.GetComponentInParent<EnemyAI>() != null))
            {
                return;
            }

            ContactPoint contact = collision != null && collision.contactCount > 0
                ? collision.GetContact(0)
                : default;

            Vector3 point = collision != null && collision.contactCount > 0
                ? contact.point
                : transform.position;
            Vector3 normal = collision != null && collision.contactCount > 0
                ? contact.normal
                : Vector3.up;

            StickFromLocalSimulation(point, normal, hit);
        }

        /// <summary>
        /// The local physics result. On the thrower's machine this is the authoritative pose and
        /// is fanned out so every other client snaps to it (F-FOREMAN-B-2).
        /// </summary>
        private void StickFromLocalSimulation(Vector3 point, Vector3 normal, Transform parent)
        {
            Stick(point, normal, parent);

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local != null && local.NetworkObjectId == _throwerNetObjId)
                WorklightBeaconPatch.BroadcastStick(_throwerNetObjId, _beaconId, point, normal, ResolveParentNetworkObjectId(parent));
        }

        private void SnapToAuthoritativePose(Vector3 point, Vector3 normal, ulong parentNetObjId)
        {
            Transform parent = ResolveParentByNetworkObjectId(parentNetObjId);
            if (_stuck)
                transform.SetParent(null, worldPositionStays: true);

            Stick(point, normal, parent);
        }

        private void Stick(Vector3 point, Vector3 normal, Transform parent)
        {
            bool wasStuck = _stuck;
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

            // F-FOREMAN-B-10: the catalog promises 180 s of light, so the clock starts here.
            if (!wasStuck)
                _destroyAt = Time.time + WorklightBeaconUpgrade.LIFE_SECONDS;

            if (_light == null)
                CreateLight(active: true);
            else
                _light.enabled = true;

            if (!wasStuck)
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

        /// <summary>
        /// F-FOREMAN-B-16: InteractTrigger is a NetworkBehaviour and the beacon root has no
        /// NetworkObject, so NGO's NetworkObject getter finds none (and, once Stick parents the
        /// beacon under a door or the cruiser, adopts that object's instead). The component is
        /// kept anyway because it is the only thing that makes PlayerControllerB's hover raycast
        /// draw the "Pick up worklight" tip; the pickup itself never goes through the trigger's
        /// RPC surface - WorklightBeaconPatch.PreInteractPerformed intercepts the interact key
        /// before InteractTrigger ever runs, and onInteract is a plain local UnityEvent. Nothing
        /// here reaches an InteractTrigger RPC path, so the mis-association is inert; the live
        /// check that matters is whether NGO logs warning spam (see the audit risk notes).
        /// </summary>
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

            float range = WorklightBeaconUpgrade.GetRange(_tier);
            float intensity = WorklightBeaconUpgrade.GetIntensity(_tier);

            _light = lightObject.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(1f, 0.93f, 0.82f, 1f);
            _light.intensity = intensity;
            _light.range = range;
            // F-FOREMAN-B-7: no shadows. Level 3 can have nine of these live at once and each
            // shadow-casting point light is six cube faces.
            _light.shadows = LightShadows.None;
            _light.bounceIntensity = 0f;
            _light.enabled = active;

            // F-FOREMAN-B-7: HDRP owns the authoring value; going through HDAdditionalLightData
            // is what makes the catalog's 10/13/17 m radius mean what it says (same pattern as
            // Lucky8MachineBehaviour.CreatePointLight).
            try
            {
                HDAdditionalLightData hdLight = lightObject.GetComponent<HDAdditionalLightData>()
                    ?? lightObject.AddComponent<HDAdditionalLightData>();
                if (hdLight != null)
                {
                    hdLight.SetLightTypeAndShape(HDLightTypeAndShape.Point);
                    hdLight.SetRange(range);
                    hdLight.SetIntensity(WorklightBeaconUpgrade.GetLumens(_tier), LightUnit.Lumen);
                    hdLight.EnableShadows(false);
                }
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning($"[WorklightBeacon] HDRP light authoring failed: {exception.Message}");
            }
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
            ApplyMaterial(basePlate, MATERIAL_BASE, new Color(0.08f, 0.075f, 0.065f, 1f), 0.15f, 0.45f);

            GameObject lens = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lens.name = "WorklightBeaconLens";
            lens.transform.SetParent(parent, worldPositionStays: false);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.055f);
            lens.transform.localScale = new Vector3(0.13f, 0.13f, 0.045f);
            RemoveCollider(lens);
            ApplyMaterial(lens, MATERIAL_LENS, new Color(1f, 0.9f, 0.72f, 1f), 0f, 0.95f);

            GameObject magnet = GameObject.CreatePrimitive(PrimitiveType.Cube);
            magnet.name = "WorklightBeaconMagnet";
            magnet.transform.SetParent(parent, worldPositionStays: false);
            magnet.transform.localPosition = new Vector3(0f, -0.015f, -0.045f);
            magnet.transform.localScale = new Vector3(0.16f, 0.03f, 0.025f);
            RemoveCollider(magnet);
            ApplyMaterial(magnet, MATERIAL_MAGNET, new Color(0.55f, 0.04f, 0.025f, 1f), 0.3f, 0.55f);
        }

        private static void RemoveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);
        }

        private static void ApplyMaterial(GameObject go, int materialIndex, Color color, float metallic, float smoothness)
        {
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null)
                return;

            Material material = GetSharedMaterial(materialIndex, color, metallic, smoothness);
            if (material != null)
                renderer.sharedMaterial = material;
        }

        // F-FOREMAN-B-8: built once per part and kept with HideAndDontSave. Runtime Materials are
        // not garbage collected, so the old per-beacon Material (and Shader.Find) left 27 orphans
        // a round at level 3.
        private static Material GetSharedMaterial(int materialIndex, Color color, float metallic, float smoothness)
        {
            if (materialIndex < 0 || materialIndex >= SharedPartMaterials.Length)
                return null;
            if (SharedPartMaterials[materialIndex] != null)
                return SharedPartMaterials[materialIndex];

            Shader shader = Shader.Find("HDRP/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null)
                return null;

            Material material = new Material(shader)
            {
                name = "Y4NGZ_WorklightBeaconPart" + materialIndex.ToString(),
                hideFlags = HideFlags.HideAndDontSave
            };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", color * 1.4f);
            SharedPartMaterials[materialIndex] = material;
            return material;
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
