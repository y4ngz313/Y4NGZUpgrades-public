using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    internal sealed class SteampunkDroneController : MonoBehaviour
    {
        private const string CarryAnchorName = "Y4NGZ_CarryAnchor";
        private const string FlashlightConeName = "Y4NGZ_FlashlightCone";
        private const string FlashlightSpotName = "Y4NGZ_FlashlightSpot";
        private const string BrokenDroneName = "BrokenDrone";
        private const string BrokenBodyName = "BrokenBody";
        private const float ExplosionForce = 26f;
        private const float ExplosionRadius = 3.2f;
        private const float ExplosionUpwardsModifier = 0.35f;
        private const float MaterializeDurationSeconds = 0.9f;
        private const float DematerializeDurationSeconds = 0.7f;
        private const float FlashlightSpotRange = 22f;
        private const float FlashlightSpotAngle = 50f;
        private const float FlashlightInnerSpotAngle = 35f;
        private const float FlashlightSpotIntensity = 12f;
        private const float FlashlightConeAlphaMultiplier = 0.35f;

        private static readonly int LiftOffTrigger = Animator.StringToHash("LiftOff");
        private static readonly int LandTrigger = Animator.StringToHash("Land");
        private static readonly int FireAttackTrigger = Animator.StringToHash("FireAttack");
        private static readonly int GetHurtSmallTrigger = Animator.StringToHash("GetHurt_01");
        private static readonly int GetHurtLargeTrigger = Animator.StringToHash("GetHurt_02");
        private static readonly int ExplodeTrigger = Animator.StringToHash("Explode");
        private static readonly int TurnOnFlashlightTrigger = Animator.StringToHash("TurnOnFlashlight");
        private static readonly int TurnOffFlashlightTrigger = Animator.StringToHash("TurnOffFlashlight");
        private static readonly Color FlashlightWarmWhite = new Color(1.0f, 0.956f, 0.84f, 1f);
        private static readonly string[] DissolvePropertyNames =
        {
            "_Dissolve",
            "_DissolveAmount",
            "_Cutoff",
            "_AlphaCutoff",
        };
        private static bool _loggedMissingDissolveMaterial;

        private GameObject _rootObject;
        private Animator _animator;
        private Transform _brokenPiecesRoot;
        private GameObject _flashlightCone;
        private Light[] _flashlightLights;
        private Renderer[] _flashlightRenderers;
        private Rigidbody[] _brokenPieceBodies;
        private Renderer[] _liveBodyRenderers;
        private Transform _joltTarget;
        private Coroutine _dissolveRoutine;
        private Coroutine _joltRoutine;
        private Vector3 _joltBaseLocalPosition;
        private bool _initialized;
        private bool _flashlightConeToned;
        // F-DRONE-8: the real materials are captured ONCE and kept here for the object's lifetime,
        // and the runtime dissolve copies currently on the renderers are tracked so they can always
        // be put back and destroyed.
        private Material[][] _originalMaterials;
        private Material[][] _activeDissolveMaterials;

        internal bool HasAnimator => _animator != null && _animator.runtimeAnimatorController != null;
        internal Transform CarryAnchor { get; private set; }

        internal void Initialize(GameObject rootObject)
        {
            if (rootObject == null)
                rootObject = gameObject;

            if (_initialized && _rootObject == rootObject)
            {
                ConfigureAnimator();
                return;
            }

            _initialized = true;
            _rootObject = rootObject;
            _animator = rootObject.GetComponentInChildren<Animator>(includeInactive: true);
            ConfigureAnimator();
            _joltTarget = ResolveJoltTarget();
            _flashlightConeToned = false;

            _brokenPiecesRoot = FindNamedTransform(rootObject.transform, BrokenDroneName)
                ?? FindNamedTransform(rootObject.transform, BrokenBodyName)
                ?? FindTransformContaining(rootObject.transform, BrokenDroneName)
                ?? FindTransformContaining(rootObject.transform, BrokenBodyName);
            CarryAnchor = FindNamedTransform(rootObject.transform, CarryAnchorName);
            Transform flashlightCone = FindNamedTransform(rootObject.transform, FlashlightConeName)
                ?? FindTransformContaining(rootObject.transform, "FlashlightCone")
                ?? FindTransformContaining(rootObject.transform, "Flashlight");
            _flashlightCone = flashlightCone != null ? flashlightCone.gameObject : null;

            CacheFlashlight();
            CacheBrokenPieces();
            _liveBodyRenderers = null;
            SetBrokenPiecesActive(false);
            SetFlashlightActive(false);
        }

        internal void PlayLiftOff()
        {
            EnsureInitialized();
            SetBrokenPiecesActive(false);
            SetTrigger(LiftOffTrigger);
        }

        internal void PlayLand()
        {
            EnsureInitialized();
            SetFlashlightActive(false);
            SetTrigger(LandTrigger);
        }

        internal void PlayFireAttack()
        {
            EnsureInitialized();
            SetTrigger(FireAttackTrigger);
        }

        internal void PlayHurt(bool largeHit)
        {
            EnsureInitialized();
            SetTrigger(largeHit ? GetHurtLargeTrigger : GetHurtSmallTrigger);
        }

        internal void PlayHitJolt(Vector3 awayDirection, float distance, float duration)
        {
            EnsureInitialized();
            if (_joltTarget == null || distance <= 0f || duration <= 0f)
                return;

            Vector3 direction = awayDirection.sqrMagnitude > 0.001f ? awayDirection.normalized : Vector3.right;
            Vector3 localDirection = _joltTarget.parent != null
                ? _joltTarget.parent.InverseTransformDirection(direction)
                : direction;
            localDirection.y = 0f;
            if (localDirection.sqrMagnitude <= 0.001f)
                localDirection = Vector3.right;
            localDirection.Normalize();

            if (_joltRoutine != null)
            {
                StopCoroutine(_joltRoutine);
                if (_joltTarget != null)
                    _joltTarget.localPosition = _joltBaseLocalPosition;
            }

            _joltBaseLocalPosition = _joltTarget.localPosition;
            _joltRoutine = StartCoroutine(PlayHitJoltRoutine(_joltTarget, _joltBaseLocalPosition, localDirection * distance, duration));
        }

        internal void PlayDeath()
        {
            EnsureInitialized();
            SetFlashlightActive(false);
            SetTrigger(ExplodeTrigger);
        }

        internal void PlayFlashlightOn()
        {
            EnsureInitialized();
            SetFlashlightActive(true);
            SetTrigger(TurnOnFlashlightTrigger);
        }

        internal void PlayFlashlightOff()
        {
            EnsureInitialized();
            SetFlashlightActive(false);
            SetTrigger(TurnOffFlashlightTrigger);
        }

        internal void SetFlashlight(bool enabled)
        {
            EnsureInitialized();
            if (enabled)
                PlayFlashlightOn();
            else
                PlayFlashlightOff();
        }

        internal void PlayMaterializeIn(float duration = MaterializeDurationSeconds)
        {
            EnsureInitialized();
            StartDissolve(1f, 0f, Mathf.Max(0.01f, duration), restoreOriginalMaterials: true);
        }

        internal float PlayDematerializeOut(float duration = DematerializeDurationSeconds)
        {
            EnsureInitialized();
            return StartDissolve(0f, 1f, Mathf.Max(0.01f, duration), restoreOriginalMaterials: false);
        }

        private IEnumerator PlayHitJoltRoutine(Transform target, Vector3 baseLocalPosition, Vector3 localOffset, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (target == null)
                    yield break;

                float normalized = Mathf.Clamp01(elapsed / duration);
                float decay = 1f - Mathf.SmoothStep(0f, 1f, normalized);
                target.localPosition = baseLocalPosition + localOffset * decay;
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (target != null)
                target.localPosition = baseLocalPosition;
            _joltRoutine = null;
        }

        /// <summary>
        /// F-DRONE-8: three bugs lived here. The originals were re-captured on every call from
        /// <c>renderer.materials</c>, which instantiates a fresh copy of every shared material; a
        /// dematerialize left its <c>new Material(template)</c> instances on the renderers and threw
        /// the captured originals away; and a pre-empting call (PlayMaterializeIn right after the
        /// dematerialize in StuckRescueRelocateRoutine) then captured those dissolve copies AS the
        /// originals and restored them at the end - so a stuck-rescued drone permanently wore flat
        /// base-colour-only materials and every rescue cycle leaked another layer.
        ///
        /// Now the originals are captured once from <c>sharedMaterials</c> and kept for the object's
        /// lifetime, and whichever direction ran last, the dissolve set is restored and destroyed
        /// before a new one is built (and on OnDestroy). A finished dematerialize deliberately keeps
        /// its set applied at amount 1 - restoring there would pop the drone back into view during
        /// the gap before it is destroyed or re-materialized.
        /// </summary>
        private float StartDissolve(float from, float to, float duration, bool restoreOriginalMaterials)
        {
            Material template = CourierDroneRuntimeAssets.LoadDissolveMaterial();
            if (template == null)
            {
                LogMissingDissolveMaterialOnce();
                return 0f;
            }

            Renderer[] renderers = GetLiveBodyRenderers();
            if (renderers.Length == 0)
                return 0f;

            if (_dissolveRoutine != null)
            {
                StopCoroutine(_dissolveRoutine);
                _dissolveRoutine = null;
            }

            if (_originalMaterials == null)
                _originalMaterials = CaptureOriginalMaterials(renderers);
            else
                ReleaseActiveDissolveMaterials(renderers);

            Material[][] dissolveMaterials = CreateDissolveMaterialArrays(template, _originalMaterials);
            _activeDissolveMaterials = dissolveMaterials;
            ApplyDissolveMaterials(renderers, dissolveMaterials, from);
            _dissolveRoutine = StartCoroutine(PlayDissolveRoutine(renderers, dissolveMaterials, from, to, duration, restoreOriginalMaterials));
            return duration;
        }

        private IEnumerator PlayDissolveRoutine(
            Renderer[] renderers,
            Material[][] dissolveMaterials,
            float from,
            float to,
            float duration,
            bool restoreOriginalMaterials)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float normalized = duration > 0.001f ? Mathf.Clamp01(elapsed / duration) : 1f;
                float amount = Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, normalized));
                ApplyDissolveAmount(dissolveMaterials, amount);
                elapsed += Time.deltaTime;
                yield return null;
            }

            ApplyDissolveAmount(dissolveMaterials, to);
            _dissolveRoutine = null;
            if (restoreOriginalMaterials)
                ReleaseActiveDissolveMaterials(renderers);
        }

        /// <summary>
        /// Puts the captured originals back on the renderers and destroys the runtime dissolve copies
        /// that were on them. Safe to call when no dissolve set is active.
        /// </summary>
        private void ReleaseActiveDissolveMaterials(Renderer[] renderers)
        {
            if (_originalMaterials != null && renderers != null)
                RestoreOriginalMaterials(renderers, _originalMaterials);

            DestroyDissolveMaterials(_activeDissolveMaterials);
            _activeDissolveMaterials = null;
        }

        private void OnDestroy()
        {
            // The drone object can be destroyed mid-dissolve (dismiss, death, round reset); without
            // this the runtime dissolve copies are orphaned (F-DRONE-8).
            DestroyDissolveMaterials(_activeDissolveMaterials);
            _activeDissolveMaterials = null;
        }

        private static void ApplyDissolveMaterials(Renderer[] renderers, Material[][] dissolveMaterialSets, float amount)
        {
            ApplyDissolveAmount(dissolveMaterialSets, amount);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                Material[] dissolveMaterials = dissolveMaterialSets[i];
                if (renderer == null || dissolveMaterials == null)
                    continue;

                renderer.sharedMaterials = dissolveMaterials;
            }
        }

        /// <summary>
        /// sharedMaterials, not materials: the getter on <c>materials</c> instantiates a copy of
        /// every material it returns, so capturing through it leaked a full set per call (F-DRONE-8).
        /// </summary>
        private static Material[][] CaptureOriginalMaterials(Renderer[] renderers)
        {
            Material[][] originalMaterials = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                originalMaterials[i] = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
            }

            return originalMaterials;
        }

        private static Material[][] CreateDissolveMaterialArrays(Material template, Material[][] originalMaterials)
        {
            Material[][] dissolveMaterialSets = new Material[originalMaterials.Length][];
            for (int i = 0; i < originalMaterials.Length; i++)
            {
                Material[] originals = originalMaterials[i];
                int count = Mathf.Max(1, originals != null ? originals.Length : 0);
                Material[] dissolveMaterials = new Material[count];
                for (int m = 0; m < count; m++)
                {
                    Material source = originals != null && m < originals.Length ? originals[m] : null;
                    Material material = new Material(template)
                    {
                        name = "M_SteampunkDroneDissolve_Runtime"
                    };
                    if (source != null && source.mainTexture != null)
                    {
                        SetMaterialTextureIfPresent(material, "_BaseColorMap", source.mainTexture);
                        SetMaterialTextureIfPresent(material, "_MainTex", source.mainTexture);
                        SetMaterialTextureIfPresent(material, "_BaseMap", source.mainTexture);
                    }

                    dissolveMaterials[m] = material;
                }

                dissolveMaterialSets[i] = dissolveMaterials;
            }

            return dissolveMaterialSets;
        }

        private static void ApplyDissolveAmount(Material[][] dissolveMaterialSets, float amount)
        {
            if (dissolveMaterialSets == null)
                return;

            for (int i = 0; i < dissolveMaterialSets.Length; i++)
            {
                Material[] materials = dissolveMaterialSets[i];
                if (materials == null)
                    continue;

                for (int m = 0; m < materials.Length; m++)
                    SetDissolveFloat(materials[m], Mathf.Clamp01(amount));
            }
        }

        private static void RestoreOriginalMaterials(Renderer[] renderers, Material[][] originalMaterials)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null && i < originalMaterials.Length && originalMaterials[i] != null)
                    renderer.sharedMaterials = originalMaterials[i];
            }
        }

        private static void DestroyDissolveMaterials(Material[][] dissolveMaterialSets)
        {
            if (dissolveMaterialSets == null)
                return;

            for (int i = 0; i < dissolveMaterialSets.Length; i++)
            {
                Material[] materials = dissolveMaterialSets[i];
                if (materials == null)
                    continue;

                for (int m = 0; m < materials.Length; m++)
                {
                    if (materials[m] != null)
                        Destroy(materials[m]);
                }
            }
        }

        private Renderer[] GetLiveBodyRenderers()
        {
            if (_liveBodyRenderers != null)
                return _liveBodyRenderers;

            Renderer[] allRenderers = _rootObject != null
                ? _rootObject.GetComponentsInChildren<Renderer>(includeInactive: true)
                : GetComponentsInChildren<Renderer>(includeInactive: true);
            List<Renderer> liveRenderers = new List<Renderer>(allRenderers.Length);
            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer renderer = allRenderers[i];
                if (IsLiveBodyRenderer(renderer))
                    liveRenderers.Add(renderer);
            }

            _liveBodyRenderers = liveRenderers.ToArray();
            return _liveBodyRenderers;
        }

        private static bool IsLiveBodyRenderer(Renderer renderer)
        {
            return renderer != null
                && !IsBrokenPieceTransform(renderer.transform)
                && !IsFlashlightTransform(renderer.transform)
                && !IsEffectRenderer(renderer);
        }

        private static void LogMissingDissolveMaterialOnce()
        {
            if (_loggedMissingDissolveMaterial)
                return;

            _loggedMissingDissolveMaterial = true;
            Plugin.Log?.LogInfo("Steampunk drone dissolve material missing from bundle; materialize/dematerialize effects disabled.");
        }

        private static bool SetDissolveFloat(Material material, float value)
        {
            for (int i = 0; i < DissolvePropertyNames.Length; i++)
            {
                if (SetMaterialFloatIfPresent(material, DissolvePropertyNames[i], value))
                    return true;
            }

            return false;
        }

        private static bool SetMaterialFloatIfPresent(Material material, string propertyName, float value)
        {
            if (material == null || !material.HasProperty(propertyName))
                return false;

            material.SetFloat(propertyName, value);
            return true;
        }

        private static void SetMaterialTextureIfPresent(Material material, string propertyName, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(propertyName))
                material.SetTexture(propertyName, texture);
        }

        public void Explode()
        {
            EnsureInitialized();
            SetBrokenPiecesActive(true);

            Vector3 center = _rootObject != null ? _rootObject.transform.position : transform.position;
            if (_brokenPiecesRoot != null)
                center = _brokenPiecesRoot.position;

            if (_brokenPieceBodies == null)
                return;

            for (int i = 0; i < _brokenPieceBodies.Length; i++)
            {
                Rigidbody body = _brokenPieceBodies[i];
                if (body == null)
                    continue;

                body.isKinematic = false;
                body.useGravity = true;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.AddExplosionForce(ExplosionForce, center, ExplosionRadius, ExplosionUpwardsModifier, ForceMode.Impulse);
            }
        }

        internal static bool IsBrokenPieceTransform(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                string name = current.name;
                if (ContainsOrdinalIgnoreCase(name, BrokenDroneName)
                    || ContainsOrdinalIgnoreCase(name, BrokenBodyName)
                    || ContainsOrdinalIgnoreCase(name, "BrokenPiece")
                    || ContainsOrdinalIgnoreCase(name, "BrokenCasing")
                    || ContainsOrdinalIgnoreCase(name, "Debris"))
                {
                    return true;
                }
            }

            return false;
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
                Initialize(_rootObject != null ? _rootObject : gameObject);
        }

        private void ConfigureAnimator()
        {
            if (_animator == null)
                return;

            _animator.enabled = true;
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        private Transform ResolveJoltTarget()
        {
            Transform root = _rootObject != null ? _rootObject.transform : transform;
            Transform target = _animator != null ? _animator.transform : null;
            if (target == null && root != null && root.childCount > 0)
                target = root.GetChild(0);
            if (target == root)
                return null;

            return target;
        }

        private void SetTrigger(int triggerHash)
        {
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return;

            _animator.SetTrigger(triggerHash);
        }

        private void CacheFlashlight()
        {
            if (_flashlightCone != null)
            {
                EnsureRuntimeFlashlightSpot(_flashlightCone.transform);
                _flashlightLights = _flashlightCone.GetComponentsInChildren<Light>(includeInactive: true);
                _flashlightRenderers = _flashlightCone.GetComponentsInChildren<Renderer>(includeInactive: true);
                ToneFlashlightConeRenderers();
                return;
            }

            Light[] lights = _rootObject != null
                ? _rootObject.GetComponentsInChildren<Light>(includeInactive: true)
                : GetComponentsInChildren<Light>(includeInactive: true);
            int count = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null && IsFlashlightTransform(lights[i].transform))
                    count++;
            }

            _flashlightLights = new Light[count];
            int writeIndex = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null && IsFlashlightTransform(lights[i].transform))
                    _flashlightLights[writeIndex++] = lights[i];
            }
            _flashlightRenderers = new Renderer[0];
        }

        private Light EnsureRuntimeFlashlightSpot(Transform cone)
        {
            if (cone == null)
                return null;

            Transform spotTransform = FindNamedTransform(cone, FlashlightSpotName);
            if (spotTransform == null)
            {
                GameObject spot = new GameObject(FlashlightSpotName);
                spot.transform.SetParent(cone, worldPositionStays: false);
                spot.transform.localPosition = Vector3.zero;
                spot.transform.localRotation = Quaternion.identity;
                spotTransform = spot.transform;
            }

            Light light = spotTransform.GetComponent<Light>();
            if (light == null)
                light = spotTransform.gameObject.AddComponent<Light>();

            light.type = LightType.Spot;
            light.range = FlashlightSpotRange;
            light.spotAngle = FlashlightSpotAngle;
            light.innerSpotAngle = FlashlightInnerSpotAngle;
            light.color = FlashlightWarmWhite;
            light.intensity = FlashlightSpotIntensity;
            light.shadows = LightShadows.None;
            light.enabled = false;
            return light;
        }

        private void ToneFlashlightConeRenderers()
        {
            if (_flashlightConeToned || _flashlightRenderers == null)
                return;

            _flashlightConeToned = true;
            for (int i = 0; i < _flashlightRenderers.Length; i++)
            {
                Renderer renderer = _flashlightRenderers[i];
                if (renderer == null)
                    continue;

                Material[] materials = renderer.materials;
                for (int m = 0; m < materials.Length; m++)
                    ToneFlashlightConeMaterial(materials[m]);
            }
        }

        private static void ToneFlashlightConeMaterial(Material material)
        {
            if (material == null)
                return;

            if (ToneMaterialColorIfPresent(material, "_UnlitColor"))
                return;
            if (ToneMaterialColorIfPresent(material, "_BaseColor"))
                return;
            ToneMaterialColorIfPresent(material, "_Color");
        }

        private static bool ToneMaterialColorIfPresent(Material material, string propertyName)
        {
            if (material == null || !material.HasProperty(propertyName))
                return false;

            Color current = material.GetColor(propertyName);
            Color toned = Color.Lerp(current, FlashlightWarmWhite, 0.65f);
            toned.a = current.a * FlashlightConeAlphaMultiplier;
            material.SetColor(propertyName, toned);
            return true;
        }

        private void CacheBrokenPieces()
        {
            Transform bodyRoot = _brokenPiecesRoot != null ? _brokenPiecesRoot : (_rootObject != null ? _rootObject.transform : transform);
            Rigidbody[] bodies = bodyRoot.GetComponentsInChildren<Rigidbody>(includeInactive: true);
            if (_brokenPiecesRoot != null)
            {
                _brokenPieceBodies = bodies;
                return;
            }

            int count = 0;
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i] != null && IsBrokenPieceTransform(bodies[i].transform))
                    count++;
            }

            if (count == 0)
            {
                _brokenPieceBodies = bodies;
                return;
            }

            _brokenPieceBodies = new Rigidbody[count];
            int writeIndex = 0;
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i] != null && IsBrokenPieceTransform(bodies[i].transform))
                    _brokenPieceBodies[writeIndex++] = bodies[i];
            }
        }

        private void SetBrokenPiecesActive(bool active)
        {
            if (_brokenPiecesRoot == null)
                return;

            _brokenPiecesRoot.gameObject.SetActive(active);
        }

        private void SetFlashlightActive(bool active)
        {
            if (_flashlightCone != null)
                _flashlightCone.SetActive(active);

            if (_flashlightLights != null)
            {
                for (int i = 0; i < _flashlightLights.Length; i++)
                {
                    Light light = _flashlightLights[i];
                    if (light == null)
                        continue;

                    light.enabled = active;
                    light.gameObject.SetActive(active);
                }
            }

            if (_flashlightRenderers == null)
                return;

            for (int i = 0; i < _flashlightRenderers.Length; i++)
            {
                Renderer renderer = _flashlightRenderers[i];
                if (renderer != null)
                    renderer.enabled = active;
            }
        }

        private static Transform FindNamedTransform(Transform root, string exactName)
        {
            if (root == null || string.IsNullOrEmpty(exactName))
                return null;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate != null && string.Equals(candidate.name, exactName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        private static bool IsFlashlightTransform(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (ContainsOrdinalIgnoreCase(current.name, "Flashlight"))
                    return true;
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

        private static Transform FindTransformContaining(Transform root, string namePart)
        {
            if (root == null || string.IsNullOrEmpty(namePart))
                return null;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate != null && ContainsOrdinalIgnoreCase(candidate.name, namePart))
                    return candidate;
            }

            return null;
        }

        private static bool ContainsOrdinalIgnoreCase(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(fragment)
                && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
