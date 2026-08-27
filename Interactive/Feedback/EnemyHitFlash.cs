using System.Collections;
using UnityEngine;

namespace Y4NGZUpgrades.Interactive.Feedback
{
    /// <summary>
    /// Whole-body hit feedback on a creature: a brief tint, a point light, and a click. Fired from
    /// <c>CombatFeedbackPatch</c>'s postfix on <c>EnemyAI.HitEnemy</c>, so it covers every damage
    /// source, not just this mod's weapons.
    ///
    /// <b>The impact particles are gone, 2026-08-03 (#125).</b> `SpawnImpactParticles` burst 5-10
    /// billboards graded orange to dark red at <see cref="ResolveCenter"/> — and never assigned a
    /// texture to their material, so every particle rendered as a solid coloured square. Lawson read
    /// it in game as "old pixel like blood", and it sat on top of the position-accurate creature
    /// impact that #119/#121 had just landed, because it spawned at the body centre while the real
    /// burst spawns at the contact point.
    ///
    /// It is deleted rather than fixed because the thing it was standing in for now exists:
    /// <c>WeaponImpactEffects.PlayCreatureImpact</c> (BetterArmory) draws at the exact
    /// hit point, from a prefab chosen by what the creature is made of. **Do not add a particle
    /// effect back here.** Anything material-aware belongs in `ImpactMaterialCatalog` and its
    /// consumers; this class is for feedback about the creature as a whole, which is why the tint and
    /// the light are still here and still spawn at the body centre.
    ///
    /// The `Combat Feedback / Enemy Impact VFX Enabled` config entry went with it. An existing .cfg
    /// keeps the orphaned line harmlessly until BepInEx rewrites the file.
    /// </summary>
    internal sealed class EnemyHitFlash : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");

        private static AudioClip _impactClip;

        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;
        private Coroutine _flashRoutine;

        public static void Pulse(EnemyAI enemy)
        {
            if (enemy == null) return;
            if (Plugin.CombatFeedbackEnabled?.Value == false) return;

            if (Plugin.CombatFeedbackEnemyFlashEnabled?.Value == true)
            {
                EnemyHitFlash flash = enemy.gameObject.GetComponent<EnemyHitFlash>();
                if (flash == null) flash = enemy.gameObject.AddComponent<EnemyHitFlash>();
                flash.StartPulse(enemy);
            }

            if (Plugin.CombatFeedbackEnemyImpactLightEnabled?.Value == true)
            {
                SpawnImpactLight(enemy);
            }

            if (Plugin.CombatFeedbackEnemyImpactSoundEnabled?.Value != false)
            {
                PlayImpactSound(enemy);
            }
        }

        private void StartPulse(EnemyAI enemy)
        {
            if (_renderers == null || _renderers.Length == 0)
            {
                _renderers = CollectRenderers(enemy);
            }

            if (_renderers == null || _renderers.Length == 0)
                return;

            if (_block == null) _block = new MaterialPropertyBlock();
            if (_flashRoutine != null) StopCoroutine(_flashRoutine);
            _flashRoutine = StartCoroutine(FlashRoutine());
        }

        private static Renderer[] CollectRenderers(EnemyAI enemy)
        {
            if (enemy == null) return System.Array.Empty<Renderer>();

            int skinnedCount = enemy.skinnedMeshRenderers != null ? enemy.skinnedMeshRenderers.Length : 0;
            int meshCount = enemy.meshRenderers != null ? enemy.meshRenderers.Length : 0;

            if (skinnedCount + meshCount <= 0)
            {
                return enemy.GetComponentsInChildren<Renderer>(includeInactive: false);
            }

            Renderer[] renderers = new Renderer[skinnedCount + meshCount];
            int index = 0;
            for (int i = 0; i < skinnedCount; i++)
                renderers[index++] = enemy.skinnedMeshRenderers[i];
            for (int i = 0; i < meshCount; i++)
                renderers[index++] = enemy.meshRenderers[i];
            return renderers;
        }

        private IEnumerator FlashRoutine()
        {
            const float duration = 0.18f;
            Color flashColor = new Color(1f, 0.62f, 0.22f, 1f);
            float startedAt = Time.time;

            while (Time.time - startedAt < duration)
            {
                float t = Mathf.Clamp01((Time.time - startedAt) / duration);
                float strength = 1f - (t * t);
                ApplyTint(flashColor, strength);
                yield return null;
            }

            ClearTint();
            _flashRoutine = null;
        }

        private void ApplyTint(Color color, float strength)
        {
            if (_block == null) _block = new MaterialPropertyBlock();

            Color baseTint = Color.Lerp(Color.white, color, Mathf.Clamp01(strength * 0.65f));
            Color emissiveTint = color * Mathf.Clamp01(strength * 1.4f);

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null || !renderer.enabled) continue;

                _block.Clear();
                _block.SetColor(BaseColorId, baseTint);
                _block.SetColor(ColorId, baseTint);
                _block.SetColor(EmissiveColorId, emissiveTint);
                renderer.SetPropertyBlock(_block);
            }
        }

        private void ClearTint()
        {
            if (_renderers == null) return;

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null) continue;
                renderer.SetPropertyBlock(null);
            }
        }

        private static void SpawnImpactLight(EnemyAI enemy)
        {
            Vector3 position = ResolveCenter(enemy);

            GameObject go = new GameObject("Y4NGZ_EnemyHitFlash_Light");
            go.transform.position = position;

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.58f, 0.22f);
            light.intensity = 8f;
            light.range = 5f;
            light.shadows = LightShadows.None;
            light.bounceIntensity = 0f;

            MonoBehaviour host = HUDManager.Instance != null
                ? (MonoBehaviour)HUDManager.Instance
                : enemy;
            host.StartCoroutine(FadeLight(go, light));
        }

        private static void PlayImpactSound(EnemyAI enemy)
        {
            AudioClip clip = GetImpactClip();
            if (clip == null) return;
            AudioSource.PlayClipAtPoint(clip, ResolveCenter(enemy), 0.16f);
        }

        private static IEnumerator FadeLight(GameObject go, Light light)
        {
            const float duration = 0.12f;
            float startIntensity = light != null ? light.intensity : 0f;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (light == null) yield break;
                float t = Mathf.Clamp01(elapsed / duration);
                light.intensity = startIntensity * (1f - t);
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (go != null) Destroy(go);
        }

        private static Vector3 ResolveCenter(EnemyAI enemy)
        {
            if (enemy == null) return Vector3.zero;

            if (enemy.eye != null)
                return enemy.eye.position;

            Renderer renderer = null;
            if (enemy.skinnedMeshRenderers != null)
            {
                for (int i = 0; i < enemy.skinnedMeshRenderers.Length; i++)
                {
                    if (enemy.skinnedMeshRenderers[i] != null)
                    {
                        renderer = enemy.skinnedMeshRenderers[i];
                        break;
                    }
                }
            }

            if (renderer == null && enemy.meshRenderers != null)
            {
                for (int i = 0; i < enemy.meshRenderers.Length; i++)
                {
                    if (enemy.meshRenderers[i] != null)
                    {
                        renderer = enemy.meshRenderers[i];
                        break;
                    }
                }
            }

            if (renderer != null)
                return renderer.bounds.center;

            return enemy.transform.position + Vector3.up;
        }

        private static AudioClip GetImpactClip()
        {
            if (_impactClip != null) return _impactClip;

            const int sampleRate = 44100;
            const float duration = 0.045f;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            float[] data = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float envelope = Mathf.Exp(-t * 70f);
                float click = Mathf.Sin(2f * Mathf.PI * 620f * t) * 0.55f;
                float grit = Mathf.Sin(2f * Mathf.PI * 1510f * t) * 0.25f;
                data[i] = (click + grit) * envelope * 0.38f;
            }

            _impactClip = AudioClip.Create("Y4NGZ_EnemyImpactTick", sampleCount, 1, sampleRate, false);
            _impactClip.SetData(data, 0);
            _impactClip.hideFlags = HideFlags.HideAndDontSave;
            return _impactClip;
        }

        private void OnDestroy()
        {
            ClearTint();
        }
    }
}
