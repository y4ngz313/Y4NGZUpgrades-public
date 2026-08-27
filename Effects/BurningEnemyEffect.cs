using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    internal sealed class BurningEnemyEffect : MonoBehaviour
    {
        private const int BurnHitId = -9401;
        private const float MinimumVisibleLightIntensity = 4.5f;
        private const float FadeOutSeconds = 0.6f;
        private const float DefaultRigReferenceHeight = 6f;
        private const float MinRigScale = 0.08f;
        private const float MaxRigScale = 2f;

        private static readonly int BaseColorProperty = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static readonly int EmissiveColorProperty = Shader.PropertyToID("_EmissiveColor");
        private static readonly int EmissionColorProperty = Shader.PropertyToID("_EmissionColor");

        private static Material _particleMaterial;
        private static Material _softParticleMaterial;
        private static bool _particleMaterialSearched;

        private EnemyAI _enemy;
        private ParticleSystem _flames;
        private ParticleSystem _smoke;
        private Light _light;
        private float _endTime;
        private float _nextDamageTick;
        private float _tickInterval;
        private int _damagePerTick;
        private int _sourcePlayerId = -1;
        private bool _stopping;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _burnBlock;
        private GameObject _rig;
        private RigLight[] _rigLights;
        private RigFollower[] _rigFollowers;
        private AudioSource _rigAudio;
        private bool _visualsBuilt;
        private float _fadeEndTime;

        internal bool IsStopping => _stopping;

        public void Refresh(EnemyAI enemy, int sourcePlayerId, float duration, int damagePerTick, float tickInterval)
        {
            _enemy = enemy;
            _stopping = false;
            _sourcePlayerId = sourcePlayerId;
            _damagePerTick = Mathf.Max(0, damagePerTick);
            _tickInterval = Mathf.Max(0.1f, tickInterval);
            _endTime = Mathf.Max(_endTime, Time.time + Mathf.Max(0.1f, duration));

            if (_nextDamageTick <= 0f)
                _nextDamageTick = Time.time + Mathf.Min(_tickInterval, 0.35f);

            EnsureVisuals();
            CacheRenderers();
            ApplyBurnTint();

            if (_flames != null && !_flames.isPlaying)
                _flames.Play(withChildren: true);
            if (_smoke != null && !_smoke.isPlaying)
                _smoke.Play(withChildren: true);
        }

        private void EnsureVisuals()
        {
            if (_visualsBuilt) return;
            _visualsBuilt = true;

            CacheRenderers();

            Vector3 center = ResolveCenter(_enemy);
            transform.position = center;
            transform.SetParent(_enemy.transform, worldPositionStays: true);

            // The vanilla Forest Giant rig is the intended look. The procedural embers below stay as
            // the fallback for sessions where that prefab never becomes reachable; the choice is made
            // once per effect because a burn only lasts a few seconds anyway.
            if (TryBuildVanillaRig())
                return;

            BuildProceduralVisuals();
        }

        private void BuildProceduralVisuals()
        {
            if (_flames == null)
            {
                GameObject flameGo = new GameObject("Y4NGZ_BurningEnemy_Embers");
                flameGo.transform.SetParent(transform, worldPositionStays: false);
                flameGo.transform.localPosition = Vector3.zero;
                flameGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                _flames = flameGo.AddComponent<ParticleSystem>();
                ConfigureEmbers(_flames);
            }

            if (_smoke == null)
            {
                GameObject smokeGo = new GameObject("Y4NGZ_BurningEnemy_Smoke");
                smokeGo.transform.SetParent(transform, worldPositionStays: false);
                smokeGo.transform.localPosition = Vector3.up * 0.12f;
                _smoke = smokeGo.AddComponent<ParticleSystem>();
                ConfigureSmoke(_smoke);
            }

            if (_light == null)
            {
                GameObject lightGo = new GameObject("Y4NGZ_BurningEnemy_Light");
                lightGo.transform.SetParent(transform, worldPositionStays: false);
                lightGo.transform.localPosition = Vector3.zero;
                _light = lightGo.AddComponent<Light>();
                _light.type = LightType.Point;
                _light.color = new Color(1f, 0.42f, 0.12f);
                _light.range = 8.5f;
                _light.intensity = ResolveVisibleLightIntensity();
                _light.shadows = LightShadows.None;
                _light.bounceIntensity = 0.28f;
                BurningFireLightBudget.Register(_light);
            }
        }

        /// <summary>
        /// Dresses the enemy in a scaled clone of the giant's rig. Returns false whenever the rig is
        /// switched off or unreachable so the caller builds the procedural embers instead.
        /// </summary>
        private bool TryBuildVanillaRig()
        {
            if (Plugin.BurningRigEnabled != null && !Plugin.BurningRigEnabled.Value)
                return false;

            GameObject rig = VanillaBurnRigAssets.CloneRig(transform);
            if (rig == null)
                return false;

            _rig = rig;

            Vector3 center = transform.position;
            float bodyHeight = 0f;
            if (TryResolveBodyBounds(out Bounds body))
            {
                center = body.center;
                bodyHeight = body.size.y;
            }
            else if (_enemy != null && _enemy.eye != null)
            {
                // No usable renderer: the eye sits near the top of the creature, so its height above
                // the root is a workable stand-in. Without this the fit falls back to giant scale.
                bodyHeight = Mathf.Max(0f, _enemy.eye.position.y - _enemy.transform.position.y) * 1.15f;
            }

            float scale = ResolveRigScale(bodyHeight);
            rig.transform.position = center;
            // bodyHeight is a world measurement, so the fit ratio is a world scale. Divide out the
            // enemy's own scale before assigning it locally or a scaled-up creature double-counts.
            float inherited = Mathf.Abs(transform.lossyScale.y) > 0.0001f ? transform.lossyScale.y : 1f;
            rig.transform.localScale = Vector3.one * (scale / inherited);

            // Particle systems default to Local scaling, which ignores the root we just shrank.
            // Hierarchy makes that one localScale drive size, speed and shape together - the rig is
            // authored at giant proportions and has to read on a Hoarding Bug too.
            ParticleSystem[] particles = rig.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i] == null) continue;
                ParticleSystem.MainModule main = particles[i].main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }

            CacheRigLights(rig, scale);
            AttachRigToBones(rig);
            rig.SetActive(true);
            EnsureRigAudio(scale);
            LogRigDebug(scale, bodyHeight, particles.Length);
            return true;
        }

        /// <summary>
        /// Spreads the rig's flame containers over the target skeleton. The vanilla rig is limb
        /// partitioned for a giant, so on another creature the containers are re-aimed at sampled
        /// bones and driven in LateUpdate; reparenting them instead would inherit arbitrary bone
        /// scale and undo the particle sizing computed above.
        /// </summary>
        private void AttachRigToBones(GameObject rig)
        {
            List<Transform> containers = CollectFlameContainers(rig);
            if (containers.Count == 0) return;

            List<Transform> bones = CollectBones();
            if (bones.Count < 2) return;

            _rigFollowers = new RigFollower[Mathf.Min(containers.Count, bones.Count)];
            for (int i = 0; i < _rigFollowers.Length; i++)
            {
                // Bone arrays run root-to-extremity, so even sampling spreads the flames over the
                // whole body instead of clustering them all at the hips.
                int boneIndex = Mathf.Clamp(
                    Mathf.RoundToInt((i + 0.5f) * bones.Count / _rigFollowers.Length),
                    0,
                    bones.Count - 1);
                _rigFollowers[i].Container = containers[i];
                _rigFollowers[i].Bone = bones[boneIndex];
            }
        }

        private static List<Transform> CollectFlameContainers(GameObject rig)
        {
            List<Transform> containers = new List<Transform>();
            Transform root = rig.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child != null && child.GetComponentInChildren<ParticleSystem>(true) != null)
                    containers.Add(child);
            }

            return containers;
        }

        /// <summary>
        /// Skinning bones off the cached renderer set. Bundy reaches this path too - its renderers
        /// are found by <see cref="CacheRenderers"/> rather than the empty EnemyAI arrays.
        /// </summary>
        private List<Transform> CollectBones()
        {
            List<Transform> bones = new List<Transform>();
            if (_renderers == null) return bones;

            for (int i = 0; i < _renderers.Length; i++)
            {
                SkinnedMeshRenderer skinned = _renderers[i] as SkinnedMeshRenderer;
                Transform[] skinnedBones = skinned != null ? skinned.bones : null;
                if (skinnedBones == null) continue;

                for (int b = 0; b < skinnedBones.Length; b++)
                {
                    Transform bone = skinnedBones[b];
                    if (bone != null && !bones.Contains(bone))
                        bones.Add(bone);
                }
            }

            return bones;
        }

        /// <summary>Runs after the animator writes the pose, so the flames sit on the posed limbs.</summary>
        private void LateUpdate()
        {
            if (_rigFollowers == null) return;

            for (int i = 0; i < _rigFollowers.Length; i++)
            {
                Transform container = _rigFollowers[i].Container;
                Transform bone = _rigFollowers[i].Bone;
                if (container == null || bone == null) continue;
                container.position = bone.position;
            }
        }

        private void CacheRigLights(GameObject rig, float scale)
        {
            Light[] lights = rig.GetComponentsInChildren<Light>(true);
            _rigLights = new RigLight[lights.Length];
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null) continue;

                // Shadow-casting fire lights are what makes several burning enemies hurt at once.
                light.shadows = LightShadows.None;
                light.range = Mathf.Max(2f, light.range * scale);
                _rigLights[i].Light = light;
                _rigLights[i].BaseRange = light.range;
                _rigLights[i].BaseIntensity = light.intensity;
                BurningFireLightBudget.Register(light);
            }
        }

        private void EnsureRigAudio(float scale)
        {
            if (Plugin.BurningRigAudioEnabled != null && !Plugin.BurningRigAudioEnabled.Value)
                return;

            AudioClip clip = VanillaBurnRigAssets.ResolveBurningClip();
            if (clip == null || _rig == null)
                return;

            // The giant carries its burning loop on a separate source on its own root, not inside the
            // particle container, so the clone needs one built here.
            _rigAudio = _rig.AddComponent<AudioSource>();
            _rigAudio.clip = clip;
            _rigAudio.loop = true;
            _rigAudio.playOnAwake = false;
            _rigAudio.spatialBlend = 1f;
            _rigAudio.rolloffMode = AudioRolloffMode.Linear;
            _rigAudio.minDistance = 1f;
            _rigAudio.maxDistance = Mathf.Lerp(8f, 22f, Mathf.Clamp01(scale));
            _rigAudio.volume = 0f;
            _rigAudio.Play();
        }

        private static float ResolveRigScale(float bodyHeight)
        {
            float reference = Plugin.BurningRigReferenceHeight != null
                ? Mathf.Max(0.5f, Plugin.BurningRigReferenceHeight.Value)
                : DefaultRigReferenceHeight;
            float multiplier = Plugin.BurningRigScaleMultiplier != null
                ? Mathf.Max(0.01f, Plugin.BurningRigScaleMultiplier.Value)
                : 1f;
            float fit = bodyHeight > 0.05f ? bodyHeight / reference : 1f;
            return Mathf.Clamp(fit * multiplier, MinRigScale, MaxRigScale);
        }

        /// <summary>
        /// Body extents from the cached renderer set rather than <c>EnemyAI.skinnedMeshRenderers</c>,
        /// which Bundy leaves empty - its bootstrap only assigns <c>eye</c>.
        /// </summary>
        private bool TryResolveBodyBounds(out Bounds bounds)
        {
            bounds = default;
            if (_renderers == null || _renderers.Length == 0)
                return false;

            bool found = false;
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null || !renderer.enabled) continue;

                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                    continue;
                }

                bounds.Encapsulate(renderer.bounds);
            }

            return found;
        }

        private void LogRigDebug(float scale, float bodyHeight, int particleCount)
        {
            if (Plugin.BurningDebugLogging == null || !Plugin.BurningDebugLogging.Value) return;
            Plugin.Log?.LogInfo(
                $"[BurnDebug] Forest Giant rig attached to '{(_enemy != null ? _enemy.gameObject.name : "<null>")}' " +
                $"bodyHeight={bodyHeight:0.00} scale={scale:0.000} particles={particleCount} " +
                $"lights={(_rigLights != null ? _rigLights.Length : 0)}.");
        }

        private static void ConfigureEmbers(ParticleSystem ps)
        {
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.8f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.32f, 0.72f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.16f, 0.72f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.28f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.66f, 0.16f, 0.92f),
                new Color(1f, 0.18f, 0.03f, 0.55f));
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.gravityModifier = -0.05f;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 58f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.48f;
            shape.position = Vector3.zero;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.72f, 0.18f), 0f),
                    new GradientColorKey(new Color(1f, 0.34f, 0.05f), 0.42f),
                    new GradientColorKey(new Color(0.18f, 0.04f, 0.01f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0.88f, 0f),
                    new GradientAlphaKey(0.62f, 0.45f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = gradient;

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve size = new AnimationCurve();
            size.AddKey(0f, 0.35f);
            size.AddKey(0.22f, 1.0f);
            size.AddKey(1f, 0.08f);
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, size);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.minParticleSize = 0.02f;
            renderer.maxParticleSize = 0.85f;
            renderer.sortingFudge = 4f;
            Material material = ResolveParticleMaterial();
            if (material != null)
                renderer.sharedMaterial = material;
        }

        private static void ConfigureSmoke(ParticleSystem ps)
        {
            ParticleSystem.MainModule main = ps.main;
            main.duration = 1.2f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.45f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.04f, 0.28f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.22f, 0.62f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.16f, 0.13f, 0.11f, 0.18f),
                new Color(0.42f, 0.34f, 0.26f, 0.28f));
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.gravityModifier = -0.02f;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 11f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.42f;
            shape.position = Vector3.zero;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.36f, 0.28f, 0.22f), 0f),
                    new GradientColorKey(new Color(0.18f, 0.15f, 0.13f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.22f, 0.28f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = gradient;

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve size = new AnimationCurve();
            size.AddKey(0f, 0.25f);
            size.AddKey(0.45f, 1.0f);
            size.AddKey(1f, 1.35f);
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, size);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.minParticleSize = 0.04f;
            renderer.maxParticleSize = 1.1f;
            renderer.sortingFudge = 1f;
            Material material = ResolveSoftParticleMaterial();
            if (material != null)
                renderer.sharedMaterial = material;
        }

        private void Update()
        {
            BurningFireLightBudget.Tick();

            if (_stopping)
            {
                AdvanceFadeOut();
                return;
            }

            if (_enemy == null || _enemy.isEnemyDead || Time.time >= _endTime)
            {
                StopAndDestroy();
                return;
            }

            if (_light != null)
            {
                float flicker = 0.75f + Mathf.PerlinNoise(Time.time * 15f, GetInstanceID() * 0.01f) * 0.5f;
                _light.intensity = ResolveVisibleLightIntensity() * flicker;
            }

            // Same ramp rate the giant uses on its own burning loop.
            if (_rigAudio != null && _rigAudio.volume < 1f)
                _rigAudio.volume = Mathf.Min(1f, _rigAudio.volume + Time.deltaTime * 0.5f);

            ApplyBurnTint();

            if (IsDamageAuthority() && _damagePerTick > 0 && Time.time >= _nextDamageTick)
            {
                _nextDamageTick = Time.time + _tickInterval;
                ApplyDamageTick();
            }
        }

        private void ApplyDamageTick()
        {
            if (_enemy == null || _enemy.isEnemyDead) return;

            PlayerControllerB sourcePlayer = ResolveSourcePlayer();
            _enemy.HitEnemy(_damagePerTick, sourcePlayer, false, BurnHitId);

            int skipLocalPlayerId = -1;
            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            if (localPlayer != null)
                skipLocalPlayerId = (int)localPlayer.playerClientId;

            if (skipLocalPlayerId >= 0)
                _enemy.HitEnemyClientRpc(_damagePerTick, skipLocalPlayerId, false, BurnHitId);
        }

        private bool IsDamageAuthority()
        {
            return NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        }

        private PlayerControllerB ResolveSourcePlayer()
        {
            if (_sourcePlayerId < 0 || StartOfRound.Instance == null || StartOfRound.Instance.allPlayerScripts == null)
                return null;
            if (_sourcePlayerId >= StartOfRound.Instance.allPlayerScripts.Length)
                return null;
            return StartOfRound.Instance.allPlayerScripts[_sourcePlayerId];
        }

        private void StopAndDestroy()
        {
            if (_stopping) return;
            _stopping = true;
            _fadeEndTime = Time.time + FadeOutSeconds;

            if (_flames != null)
                _flames.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
            if (_smoke != null)
                _smoke.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);

            if (_light != null)
            {
                BurningFireLightBudget.Unregister(_light);
                Destroy(_light.gameObject);
                _light = null;
            }

            StopRig();
            ClearBurnTint();
            Destroy(gameObject, FadeOutSeconds);
        }

        /// <summary>Emission ends now; the live particles, light and loop ride out the same tail.</summary>
        private void StopRig()
        {
            if (_rig != null)
            {
                ParticleSystem[] particles = _rig.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < particles.Length; i++)
                {
                    if (particles[i] != null)
                        particles[i].Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            ReleaseRigLights();
        }

        /// <summary>Hands the rig's lights back so the budget stops re-enabling them mid-fade.</summary>
        private void ReleaseRigLights()
        {
            if (_rigLights == null) return;
            for (int i = 0; i < _rigLights.Length; i++)
                BurningFireLightBudget.Unregister(_rigLights[i].Light);
        }

        private void AdvanceFadeOut()
        {
            float remaining = Mathf.Clamp01((_fadeEndTime - Time.time) / FadeOutSeconds);

            if (_rigLights != null)
            {
                for (int i = 0; i < _rigLights.Length; i++)
                {
                    Light light = _rigLights[i].Light;
                    if (light == null) continue;
                    light.range = _rigLights[i].BaseRange * remaining;
                    light.intensity = _rigLights[i].BaseIntensity * remaining;
                    if (remaining <= 0f)
                        light.enabled = false;
                }
            }

            if (_rigAudio != null)
                _rigAudio.volume = Mathf.Max(0f, _rigAudio.volume - Time.deltaTime / FadeOutSeconds);
        }

        private void CacheRenderers()
        {
            if (_enemy == null || _renderers != null) return;

            Renderer[] allRenderers = _enemy.GetComponentsInChildren<Renderer>(includeInactive: false);
            List<Renderer> enemyRenderers = new List<Renderer>(allRenderers.Length);
            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer renderer = allRenderers[i];
                if (renderer == null) continue;
                if (renderer.transform.IsChildOf(transform)) continue;
                enemyRenderers.Add(renderer);
            }

            _renderers = enemyRenderers.ToArray();
            if (_burnBlock == null)
                _burnBlock = new MaterialPropertyBlock();
        }

        private void ApplyBurnTint()
        {
            CacheRenderers();
            if (_renderers == null || _burnBlock == null) return;

            float flicker = 0.55f + Mathf.PerlinNoise(Time.time * 18f, GetInstanceID() * 0.017f) * 0.45f;
            Color baseTint = Color.Lerp(
                new Color(0.92f, 0.34f, 0.12f, 1f),
                new Color(1f, 0.58f, 0.18f, 1f),
                flicker);
            Color emissionTint = new Color(1f, 0.32f + flicker * 0.24f, 0.06f, 1f) * (0.9f + flicker * 0.7f);

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null) continue;

                renderer.GetPropertyBlock(_burnBlock);
                _burnBlock.SetColor(BaseColorProperty, baseTint);
                _burnBlock.SetColor(ColorProperty, baseTint);
                _burnBlock.SetColor(EmissiveColorProperty, emissionTint);
                _burnBlock.SetColor(EmissionColorProperty, emissionTint);
                renderer.SetPropertyBlock(_burnBlock);
            }
        }

        private void ClearBurnTint()
        {
            if (_renderers == null) return;

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null) continue;
                renderer.SetPropertyBlock(null);
            }
        }

        private static float ResolveVisibleLightIntensity()
        {
            float configured = Plugin.BurningLightIntensity?.Value ?? 2.2f;
            return Mathf.Max(MinimumVisibleLightIntensity, configured);
        }

        private static Vector3 ResolveCenter(EnemyAI enemy)
        {
            if (enemy == null) return Vector3.zero;
            if (enemy.eye != null) return enemy.eye.position;

            Renderer renderer = null;
            if (enemy.skinnedMeshRenderers != null)
            {
                for (int i = 0; i < enemy.skinnedMeshRenderers.Length; i++)
                {
                    if (enemy.skinnedMeshRenderers[i] == null) continue;
                    renderer = enemy.skinnedMeshRenderers[i];
                    break;
                }
            }

            if (renderer == null && enemy.meshRenderers != null)
            {
                for (int i = 0; i < enemy.meshRenderers.Length; i++)
                {
                    if (enemy.meshRenderers[i] == null) continue;
                    renderer = enemy.meshRenderers[i];
                    break;
                }
            }

            return renderer != null
                ? renderer.bounds.center
                : enemy.transform.position + Vector3.up;
        }

        private static Material ResolveParticleMaterial()
        {
            Material heldFlamethrowerMaterial = ResolveHeldFlamethrowerParticleMaterial();
            if (heldFlamethrowerMaterial != null)
            {
                _particleMaterial = heldFlamethrowerMaterial;
                return _particleMaterial;
            }

            if (_particleMaterialSearched) return _particleMaterial;
            _particleMaterialSearched = true;

            ParticleSystemRenderer[] renderers = Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                ParticleSystemRenderer renderer = renderers[i];
                if (renderer == null || renderer.sharedMaterial == null || renderer.gameObject == null) continue;

                string name = renderer.gameObject.name.ToLowerInvariant();
                if (name.Contains("fire") || name.Contains("flame") || name.Contains("burn"))
                {
                    _particleMaterial = renderer.sharedMaterial;
                    return _particleMaterial;
                }
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                ParticleSystemRenderer renderer = renderers[i];
                if (renderer == null || renderer.sharedMaterial == null || renderer.gameObject == null) continue;

                string name = renderer.gameObject.name.ToLowerInvariant();
                if (name.Contains("spark") || name.Contains("smoke") || name.Contains("explosion"))
                {
                    _particleMaterial = renderer.sharedMaterial;
                    return _particleMaterial;
                }
            }

            _particleMaterial = CreateFallbackParticleMaterial();
            return _particleMaterial;
        }

        private static Material ResolveHeldFlamethrowerParticleMaterial()
        {
            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            GrabbableObject held = localPlayer != null ? localPlayer.currentlyHeldObjectServer : null;
            if (!HeldObjectLooksLikeFlamethrower(held)) return null;

            ParticleSystemRenderer[] renderers = held.GetComponentsInChildren<ParticleSystemRenderer>(includeInactive: true);
            Material firstUsableMaterial = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                ParticleSystemRenderer renderer = renderers[i];
                if (renderer == null || renderer.sharedMaterial == null) continue;

                if (firstUsableMaterial == null)
                    firstUsableMaterial = renderer.sharedMaterial;

                string rendererName = renderer.gameObject != null ? renderer.gameObject.name : string.Empty;
                string materialName = renderer.sharedMaterial.name;
                string haystack = (rendererName + " " + materialName).ToLowerInvariant();
                if (haystack.Contains("flame") || haystack.Contains("fire") || haystack.Contains("thrower"))
                    return renderer.sharedMaterial;
            }

            return firstUsableMaterial;
        }

        private static bool HeldObjectLooksLikeFlamethrower(GrabbableObject held)
        {
            if (held == null) return false;
            if (NameSuggestsFlamethrower(held.GetType().FullName)) return true;
            if (NameSuggestsFlamethrower(held.name)) return true;

            Item item = held.itemProperties;
            if (item == null) return false;

            return NameSuggestsFlamethrower(item.itemName)
                   || NameSuggestsFlamethrower(item.name);
        }

        private static bool NameSuggestsFlamethrower(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string lower = value.ToLowerInvariant();
            return lower.Contains("flamethrower")
                   || lower.Contains("flame thrower")
                   || lower.Contains("flame_thrower")
                   || lower.Contains("flame-thrower");
        }

        private static Material ResolveSoftParticleMaterial()
        {
            if (_softParticleMaterial != null) return _softParticleMaterial;
            _softParticleMaterial = CreateFallbackParticleMaterial("Y4NGZ_BurningEnemy_SoftParticleMaterial");
            return _softParticleMaterial;
        }

        private static Material CreateFallbackParticleMaterial()
        {
            return CreateFallbackParticleMaterial("Y4NGZ_BurningEnemy_FallbackParticleMaterial");
        }

        private static Material CreateFallbackParticleMaterial(string materialName)
        {
            Shader shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null)
                shader = Shader.Find("Legacy Shaders/Particles/Additive");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
                return null;

            Material material = new Material(shader)
            {
                name = materialName,
                hideFlags = HideFlags.HideAndDontSave
            };
            Texture2D texture = CreateSoftParticleTexture();
            if (texture != null)
            {
                if (material.HasProperty("_MainTex"))
                    material.SetTexture("_MainTex", texture);
                if (material.HasProperty("_BaseMap"))
                    material.SetTexture("_BaseMap", texture);
            }
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.white);
            if (material.HasProperty("_TintColor"))
                material.SetColor("_TintColor", Color.white);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", Color.white);
            material.renderQueue = 3000;
            return material;
        }

        private static Texture2D CreateSoftParticleTexture()
        {
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "Y4NGZ_BurningEnemy_SoftParticleTexture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float dist = Mathf.Sqrt(u * u + v * v);
                    float alpha = Mathf.Clamp01(1f - dist);
                    alpha = alpha * alpha * (3f - 2f * alpha);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }

        private void OnDestroy()
        {
            if (_flames != null)
                _flames.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
            if (_smoke != null)
                _smoke.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);

            // Covers the round-end/despawn path, where the enemy takes this object down without ever
            // running StopAndDestroy.
            if (_light != null)
                BurningFireLightBudget.Unregister(_light);
            ReleaseRigLights();

            ClearBurnTint();
        }

        private struct RigLight
        {
            internal Light Light;
            internal float BaseRange;
            internal float BaseIntensity;
        }

        private struct RigFollower
        {
            internal Transform Container;
            internal Transform Bone;
        }
    }
}
