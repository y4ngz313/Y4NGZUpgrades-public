using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Rendering;

namespace Y4NGZUpgrades.Effects
{
    /// <summary>
    /// Reusable local-only cloak visual for players, ported from the Dreadweaver active-camo
    /// technique (Y4NGZMonsters DreadweaverAI: CaptureVisuals / EnsureCamoShells /
    /// CreateSkinnedCamoShell / ApplyCamoShellProperties / ActiveCamoVisibilityRoutine).
    ///
    /// When Y4NGZMonsters has loaded the Dreadweaver bundle, its authored dissolve material is
    /// cloned for the shell. The generic material is retained only for installations where that
    /// optional provider is unavailable.
    ///
    /// PLAYER-SPECIFIC SAFETY (#362 / S5-S6):
    /// - The real body is hidden with <see cref="Renderer.forceRenderingOff"/>, never
    ///   <c>renderer.enabled</c>. Vanilla LOD code writes <c>enabled</c> on
    ///   thisPlayerModel / thisPlayerModelLOD1 / thisPlayerModelLOD2 every frame, so toggling
    ///   <c>enabled</c> from a network snapshot loses the race and the body pops back.
    /// - First-person arms (thisPlayerModelArms / localArmsTransform / playerModelArmsMetarig)
    ///   and the local visor are NEVER touched, so a cloaked local player keeps their own hands
    ///   and HUD visor.
    /// - Held items (ItemSlots + currentlyHeldObjectServer) and their lights are hidden with the
    ///   body and restored on decloak, death, disconnect or teardown.
    ///
    /// This class is purely cosmetic and entirely client-local: callers decide who is cloaked.
    /// </summary>
    internal static class CamoShellCloak
    {
        internal const string ShellName = "_Y4NGZCamoShell";

        private const float VanishSeconds = 0.65f;
        private const float RevealSeconds = 0.65f;
        // The body pops out partway through the vanish so the shell is already carrying the read.
        private const float VanishBodyHideFraction = 0.45f;
        // Matches the Dreadweaver: the body is readable again well before the shell finishes.
        private const float RevealReadableFraction = 0.72f;
        private const float ShellBaseAlpha = 0.38f;
        private const float HeldItemRefreshInterval = 0.25f;
        private const float BundledMaterialProbeInterval = 1f;
        // Watchdog slack: a routine abandoned by a scene load must still restore (#211 pattern).
        private const float RoutineWatchdogSlack = 1.5f;

        private static readonly Dictionary<PlayerControllerB, PlayerCloak> Cloaks =
            new Dictionary<PlayerControllerB, PlayerCloak>();
        private static readonly List<PlayerControllerB> ScratchKeys = new List<PlayerControllerB>();

        private static readonly CloakOverrideLease<Renderer> HeldRendererLeases = new CloakOverrideLease<Renderer>();
        private static readonly CloakOverrideLease<Light> HeldLightLeases = new CloakOverrideLease<Light>();

        private static void ReleaseHeldRenderer(Renderer renderer)
        {
            bool restore = HeldRendererLeases.Release(renderer, out bool original);
            if (renderer != null) renderer.forceRenderingOff = restore ? original : true;
        }

        private static void ReleaseHeldLight(Light light)
        {
            bool restore = HeldLightLeases.Release(light, out bool original);
            if (light != null) light.enabled = restore && original;
        }

        private static Material _shellTemplate;
        private static bool _usingBundledDissolve;
        private static float _nextBundledMaterialProbe;
        private static int _shellTemplateRevision;
        private static bool _shaderMissingLogged;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        internal static void Cloak(PlayerControllerB player) => SetCloaked(player, true);

        internal static void Decloak(PlayerControllerB player) => SetCloaked(player, false);

        internal static bool IsCloaked(PlayerControllerB player)
        {
            if (ReferenceEquals(player, null))
                return false;

            return Cloaks.TryGetValue(player, out PlayerCloak cloak) && cloak.Cloaked;
        }

        /// <summary>
        /// Drives the cross-fade toward <paramref name="cloaked"/>. Idempotent: re-asserting the
        /// state a player is already settled into does nothing, so repeated network snapshots are
        /// free.
        /// </summary>
        internal static void SetCloaked(PlayerControllerB player, bool cloaked)
        {
            if (ReferenceEquals(player, null) || player == null)
                return;

            try
            {
                if (!Cloaks.TryGetValue(player, out PlayerCloak cloak))
                {
                    if (!cloaked)
                        return; // Nothing captured and nothing to hide.

                    cloak = PlayerCloak.Capture(player);
                    if (cloak == null)
                        return;

                    Cloaks[player] = cloak;
                }

                if (cloak.Cloaked == cloaked && cloak.Routine == null && cloak.Settled)
                    return;

                cloak.Cloaked = cloaked;
                cloak.Sequence++;
                StopRoutine(cloak);

                cloak.Settled = false;
                cloak.RoutineDeadline =
                    Time.time + (cloaked ? VanishSeconds : RevealSeconds) + RoutineWatchdogSlack;
                cloak.Routine = Y4NGZPersistentRunner.Run(CrossFadeRoutine(cloak, cloaked, cloak.Sequence));

                if (cloak.Routine == null)
                {
                    // No host available: snap to the end state rather than leaving a half-cloak.
                    cloak.EnsureShells();
                    cloak.SetShellsShown(false);
                    cloak.SetBodyHidden(cloaked);
                    cloak.Settled = true;
                    if (!cloaked)
                        ForgetPlayer(player);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[CamoShellCloak] SetCloaked failed: {e}");
                ForgetPlayer(player);
            }
        }

        /// <summary>
        /// Immediate, unconditional restore for one player: stops any routine, clears every
        /// forceRenderingOff / light override, destroys the shells and drops the entry. Safe to
        /// call for a player that was never cloaked.
        /// </summary>
        internal static void ForgetPlayer(PlayerControllerB player)
        {
            if (ReferenceEquals(player, null))
                return;

            if (!Cloaks.TryGetValue(player, out PlayerCloak cloak))
                return;

            StopRoutine(cloak);
            cloak.RestoreImmediate();
            Cloaks.Remove(player);
        }

        internal static void EnforceHiddenLights()
        {
            foreach (var entry in Cloaks)
                if (entry.Value.BodyHidden) entry.Value.EnforceHiddenLights();
        }

        internal static void CleanupAll()
        {
            ScratchKeys.Clear();
            ScratchKeys.AddRange(Cloaks.Keys);
            for (int i = 0; i < ScratchKeys.Count; i++)
            {
                if (Cloaks.TryGetValue(ScratchKeys[i], out PlayerCloak cloak))
                {
                    StopRoutine(cloak);
                    cloak.RestoreImmediate();
                }
            }

            ScratchKeys.Clear();
            Cloaks.Clear();
        }

        /// <summary>
        /// Per-frame janitor. Prunes destroyed / dead players, keeps the held-item set current for
        /// cloaked players who swap slots mid-cloak, and force-restores any entry whose routine was
        /// abandoned (scene load, host teardown) so a stranded player can never stay invisible.
        /// Callers drive this from a context that outlives the player.
        /// </summary>
        internal static void Tick()
        {
            if (Cloaks.Count == 0)
                return;

            ScratchKeys.Clear();
            ScratchKeys.AddRange(Cloaks.Keys);

            for (int i = 0; i < ScratchKeys.Count; i++)
            {
                PlayerControllerB player = ScratchKeys[i];
                if (!Cloaks.TryGetValue(player, out PlayerCloak cloak))
                    continue;

                if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                {
                    StopRoutine(cloak);
                    cloak.RestoreImmediate();
                    Cloaks.Remove(player);
                    continue;
                }

                if (!cloak.Settled && Time.time > cloak.RoutineDeadline)
                {
                    // The routine never reached its tail. Land on the logical state by hand.
                    Plugin.Log?.LogWarning(
                        $"[CamoShellCloak] cross-fade for player {player.playerClientId} was abandoned; forcing restore.");
                    StopRoutine(cloak);
                    cloak.EnsureShells();
                    cloak.SetShellsShown(false);
                    cloak.SetBodyHidden(cloak.Cloaked);
                    cloak.Settled = true;
                    if (!cloak.Cloaked)
                    {
                        cloak.RestoreImmediate();
                        Cloaks.Remove(player);
                    }

                    continue;
                }

                // A late-loading optional Dreadweaver bundle replaces the generic fallback on
                // shells that have already finished their fade too. This keeps the visual
                // upgrade independent of provider load order.
                cloak.RefreshShellTemplateIfNeeded();

                if (cloak.BodyHidden && Time.time >= cloak.NextHeldRefresh)
                {
                    cloak.NextHeldRefresh = Time.time + HeldItemRefreshInterval;
                    cloak.RefreshHeldItems(hidden: true);
                }
            }

            ScratchKeys.Clear();
        }

        // ------------------------------------------------------------------
        // Cross-fade
        // ------------------------------------------------------------------

        private static IEnumerator CrossFadeRoutine(PlayerCloak cloak, bool cloaked, int sequence)
        {
            cloak.EnsureShells();

            float duration = Mathf.Max(0.12f, cloaked ? VanishSeconds : RevealSeconds);
            float elapsed = 0f;

            cloak.SetBodyHidden(!cloaked);
            cloak.SetShellsShown(true);
            cloak.ApplyShellProperties(cloaked ? 0f : 1f, cloaked ? 0.65f : 1.15f);

            while (elapsed < duration)
            {
                if (sequence != cloak.Sequence)
                    yield break; // A newer transition owns this player now.

                float t = Mathf.Clamp01(elapsed / duration);
                if (cloaked)
                {
                    cloak.ApplyShellProperties(t, Mathf.Lerp(0.65f, 1.35f, t));
                    if (t >= VanishBodyHideFraction)
                        cloak.SetBodyHidden(true);
                }
                else
                {
                    cloak.ApplyShellProperties(1f - t, Mathf.Lerp(1.15f, 0.25f, t));
                    if (t >= RevealReadableFraction)
                        cloak.SetBodyHidden(false);
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (sequence != cloak.Sequence)
                yield break;

            cloak.SetShellsShown(false);
            cloak.SetBodyHidden(cloaked);
            cloak.Settled = true;
            cloak.Routine = null;

            if (!cloaked)
            {
                // Fully visible again: drop the shells so nothing lingers on the player rig.
                cloak.RestoreImmediate();
                Cloaks.Remove(cloak.Player);
            }
        }

        private static void StopRoutine(PlayerCloak cloak)
        {
            if (cloak.Routine == null)
                return;

            Y4NGZPersistentRunner.Stop(cloak.Routine);
            cloak.Routine = null;
        }

        // ------------------------------------------------------------------
        // Renderer discovery
        // ------------------------------------------------------------------

        /// <summary>
        /// The single source of truth for "is this renderer part of the third-person body we are
        /// allowed to hide". Excludes, in order: our own shells, the first-person arms rig, the
        /// local visor, scan/map helper nodes, the username billboard, and any renderer that is
        /// not a mesh (particles, trails, lines - hiding those would kill unrelated effects).
        /// </summary>
        private static bool IsCloakableBodyRenderer(PlayerControllerB player, Renderer renderer)
        {
            if (renderer == null)
                return false;
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer))
                return false;

            // Item renderers must be managed by the held set, never captured as body renderers:
            // otherwise a dropped item stays hidden until the body itself is restored.
            if (renderer.GetComponentInParent<GrabbableObject>() != null) return false;

            // First-person arms: never hidden, or the owner loses their own hands.
            if (renderer == player.thisPlayerModelArms)
                return false;

            Transform current = renderer.transform;
            while (current != null)
            {
                if (current == player.localArmsTransform)
                    return false;
                if (current == player.playerModelArmsMetarig)
                    return false;
                if (current == player.localVisor)
                    return false;
                if (current == player.localVisorTargetPoint)
                    return false;
                if (!ReferenceEquals(player.usernameBillboard, null) && current == player.usernameBillboard)
                    return false;

                string name = current.name ?? string.Empty;
                if (name.IndexOf(ShellName, StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("ScanNode", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("MapDot", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }

                current = current.parent;
            }

            return true;
        }

        private static bool IsCloakableItemRenderer(Renderer renderer)
        {
            if (renderer == null)
                return false;
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer))
                return false;

            Transform current = renderer.transform;
            while (current != null)
            {
                string name = current.name ?? string.Empty;
                if (name.IndexOf(ShellName, StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("ScanNode", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("MapDot", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }

                current = current.parent;
            }

            return true;
        }

        // ------------------------------------------------------------------
        // Shell material
        // ------------------------------------------------------------------

        private static Material GetShellTemplate()
        {
            // Dreadweaver can finish loading after a Shadow Step shell has already fallen back.
            // Keep probing at a low cadence until its authored material is available.
            if (!_usingBundledDissolve && Time.unscaledTime >= _nextBundledMaterialProbe)
            {
                _nextBundledMaterialProbe = Time.unscaledTime + BundledMaterialProbeInterval;
                Material bundled = FindLoadedDreadweaverDissolveMaterial();
                if (bundled != null && IsUsableDissolveShader(bundled.shader))
                {
                    Material old = _shellTemplate;
                    _shellTemplate = new Material(bundled) { name = "M_Y4NGZCamoShellDreadweaver" };
                    ConfigureShellMaterial(_shellTemplate);
                    _usingBundledDissolve = true;
                    _shellTemplateRevision++;
                    if (old != null) UnityEngine.Object.Destroy(old);
                    Plugin.Log?.LogInfo("[CamoShellCloak] upgraded to the loaded Dreadweaver dissolve material.");
                }
            }

            if (_shellTemplate != null)
                return _shellTemplate;

            Shader shader = Shader.Find("HDRP/Lit")
                ?? Shader.Find("HDRP/Unlit")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Unlit/Transparent")
                ?? Shader.Find("Sprites/Default");

            if (shader == null)
            {
                if (!_shaderMissingLogged)
                {
                    _shaderMissingLogged = true;
                    Plugin.Log?.LogWarning(
                        "[CamoShellCloak] no usable transparent shader found; cloak will hide the body without a shell fade.");
                }

                return null;
            }

            _shellTemplate = new Material(shader) { name = "M_Y4NGZCamoShellRuntime" };
            ConfigureShellMaterial(_shellTemplate);
            _shellTemplateRevision++;
            return _shellTemplate;
        }

        /// <summary>
        /// The monster plugin is optional, so this intentionally depends only on Unity's loaded
        /// bundle table. It accepts material names and asset paths that Dreadweaver itself owns,
        /// never a similarly named material from an unrelated plugin.
        /// </summary>
        private static Material FindLoadedDreadweaverDissolveMaterial()
        {
            string[] directNames =
            {
                "M_DreadweaverDissolveCamo",
                "assets/dreadweaver/materials/m_dreadweaverdissolvecamo.mat",
                "Shader Graphs_Dissolve_Dissolve_Metallic",
                "assets/shadergraph_dissolve/hdrp/materials/shader graphs_dissolve_dissolve_metallic.mat"
            };

            foreach (AssetBundle bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle == null || bundle.isStreamedSceneAssetBundle)
                    continue;

                for (int i = 0; i < directNames.Length; i++)
                {
                    Material direct = TryLoadMaterial(bundle, directNames[i]);
                    if (direct != null)
                        return direct;
                }

                string[] assetNames;
                try { assetNames = bundle.GetAllAssetNames(); }
                catch { continue; }
                for (int i = 0; i < assetNames.Length; i++)
                {
                    string assetName = assetNames[i].Replace('\\', '/');
                    if (assetName.IndexOf("dreadweaver", StringComparison.OrdinalIgnoreCase) < 0
                        || assetName.IndexOf("dissolve", StringComparison.OrdinalIgnoreCase) < 0
                        || !assetName.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Material discovered = TryLoadMaterial(bundle, assetNames[i]);
                    if (discovered != null)
                        return discovered;
                }
            }

            return null;
        }

        private static Material TryLoadMaterial(AssetBundle bundle, string assetName)
        {
            try { return bundle.LoadAsset<Material>(assetName); }
            catch { return null; }
        }

        private static bool IsUsableDissolveShader(Shader shader)
        {
            if (shader == null || !shader.isSupported)
                return false;

            string name = shader.name ?? string.Empty;
            return name.IndexOf("InternalError", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Hidden/InternalErrorShader", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void ConfigureShellMaterial(Material material)
        {
            if (material == null)
                return;

            Color baseColor = new Color(0.08f, 0.48f, 0.55f, ShellBaseAlpha);
            Color edgeColor = new Color(0.12f, 0.95f, 1f, 1f);

            SetColorIfPresent(material, "_BaseColor", baseColor);
            SetColorIfPresent(material, "_Color", baseColor);
            SetColorIfPresent(material, "_UnlitColor", baseColor);
            SetColorIfPresent(material, "_TintColor", baseColor);
            SetColorIfPresent(material, "_EmissionColor", edgeColor * 2.5f);
            SetColorIfPresent(material, "_EmissiveColor", edgeColor * 2.5f);

            // HDRP/Lit only becomes transparent when _SurfaceType is flipped and the blend
            // keywords are enabled by hand; a plain alpha write on an opaque material is ignored.
            SetFloatIfPresent(material, "_SurfaceType", 1f);
            SetFloatIfPresent(material, "_BlendMode", 0f);
            SetFloatIfPresent(material, "_ZWrite", 0f);
            SetFloatIfPresent(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
            SetFloatIfPresent(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, "_AlphaSrcBlend", (float)BlendMode.One);
            SetFloatIfPresent(material, "_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, "_Mode", 3f);
            SetFloatIfPresent(material, "_CullMode", 0f);
            SetFloatIfPresent(material, "_Cull", 0f);
            SetFloatIfPresent(material, "_DoubleSidedEnable", 1f);
            SetFloatIfPresent(material, "_Dissolve", 0f);
            SetFloatIfPresent(material, "_DissolveAmount", 0f);
            SetFloatIfPresent(material, "_EdgeWidth", 0.08f);
            SetFloatIfPresent(material, "_EdgeColorIntensity", 4.25f);
            SetFloatIfPresent(material, "_NoiseScale", 26f);
            SetFloatIfPresent(material, "_Alpha", ShellBaseAlpha);

            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = 3000;
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.EnableKeyword("_EMISSION");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHATESTENABLE_ON");
        }

        private static void ApplySourceTint(Material material, Material source)
        {
            if (material == null || source == null)
                return;

            Texture baseTexture = GetTexture(source, "_BaseColorMap")
                ?? GetTexture(source, "_BaseMap")
                ?? GetTexture(source, "_MainTex")
                ?? source.mainTexture;
            SetTextureIfPresent(material, "_BaseColorMap", baseTexture);
            SetTextureIfPresent(material, "_BaseMap", baseTexture);
            SetTextureIfPresent(material, "_MainTex", baseTexture);

            Color sourceColor = GetColor(source, "_BaseColor") ?? GetColor(source, "_Color") ?? Color.white;
            Color darkCamo = new Color(0.08f, 0.52f, 0.6f, 1f);
            Color tealBias = new Color(0.06f, 0.85f, 0.95f, 1f);
            Color tint = Color.Lerp(darkCamo, sourceColor, 0.18f);
            tint = Color.Lerp(tint, tealBias, 0.22f);
            tint.a = ShellBaseAlpha;
            SetColorIfPresent(material, "_BaseColor", tint);
            SetColorIfPresent(material, "_Color", tint);
        }

        private static void SetFloatIfPresent(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void SetColorIfPresent(Material material, string property, Color value)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, value);
        }

        private static void SetTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static Texture GetTexture(Material material, string property)
        {
            return material != null && material.HasProperty(property) ? material.GetTexture(property) : null;
        }

        private static Color? GetColor(Material material, string property)
        {
            return material != null && material.HasProperty(property)
                ? material.GetColor(property)
                : (Color?)null;
        }

        // ------------------------------------------------------------------
        // Per-player state
        // ------------------------------------------------------------------

        private sealed class Shell
        {
            internal Renderer Renderer;
            internal Material[] Materials;
        }

        private sealed class PlayerCloak
        {
            internal PlayerControllerB Player;
            internal Renderer[] Body;
            internal bool[] BodyForceOffDefaults;
            internal readonly List<Shell> Shells = new List<Shell>();
            internal bool ShellsCreated;
            internal bool Cloaked;
            internal bool BodyHidden;
            internal bool Settled = true;
            internal int Sequence;
            internal Coroutine Routine;
            internal float RoutineDeadline;
            internal float NextHeldRefresh;
            internal int ShellTemplateRevision = -1;

            private readonly Dictionary<Renderer, bool> _heldRenderers = new Dictionary<Renderer, bool>();
            private readonly Dictionary<Light, bool> _heldLights = new Dictionary<Light, bool>();
            private readonly List<Renderer> _scratchRenderers = new List<Renderer>();
            private readonly List<Light> _scratchLights = new List<Light>();
            private readonly List<Renderer> _releasedRenderers = new List<Renderer>();
            private readonly List<Light> _releasedLights = new List<Light>();

            internal static PlayerCloak Capture(PlayerControllerB player)
            {
                Renderer[] all = player.GetComponentsInChildren<Renderer>(true);
                var body = new List<Renderer>(all.Length);
                for (int i = 0; i < all.Length; i++)
                {
                    if (IsCloakableBodyRenderer(player, all[i]))
                        body.Add(all[i]);
                }

                // The LOD meshes may hang off a separate root on some rigs; make sure they are in.
                AddIfMissing(body, player.thisPlayerModel);
                AddIfMissing(body, player.thisPlayerModelLOD1);
                AddIfMissing(body, player.thisPlayerModelLOD2);

                if (body.Count == 0)
                {
                    Plugin.Log?.LogWarning(
                        $"[CamoShellCloak] no cloakable renderers found for player {player.playerClientId}.");
                    return null;
                }

                var cloak = new PlayerCloak
                {
                    Player = player,
                    Body = body.ToArray(),
                };

                cloak.BodyForceOffDefaults = new bool[cloak.Body.Length];
                for (int i = 0; i < cloak.Body.Length; i++)
                    cloak.BodyForceOffDefaults[i] = cloak.Body[i].forceRenderingOff;

                return cloak;
            }

            private static void AddIfMissing(List<Renderer> body, Renderer renderer)
            {
                if (renderer == null || body.Contains(renderer))
                    return;

                body.Add(renderer);
            }

            internal void EnsureShells()
            {
                Material template = GetShellTemplate();
                if (ShellsCreated)
                {
                    RefreshShellMaterialsIfNeeded(template);
                    return;
                }

                ShellsCreated = true;

                if (template == null)
                    return;

                for (int i = 0; i < Body.Length; i++)
                {
                    Renderer source = Body[i];
                    if (source == null)
                        continue;

                    if (source is SkinnedMeshRenderer skinned)
                        CreateSkinnedShell(skinned, template);
                    else if (source is MeshRenderer meshRenderer)
                        CreateMeshShell(meshRenderer, template);
                }

                ShellTemplateRevision = _shellTemplateRevision;
            }

            internal void RefreshShellTemplateIfNeeded()
            {
                RefreshShellMaterialsIfNeeded(GetShellTemplate());
            }

            private void RefreshShellMaterialsIfNeeded(Material template)
            {
                if (template == null || ShellTemplateRevision == _shellTemplateRevision)
                    return;

                for (int i = 0; i < Shells.Count; i++)
                {
                    Shell shell = Shells[i];
                    if (shell?.Renderer == null)
                        continue;

                    Renderer source = shell.Renderer.transform.parent != null
                        ? shell.Renderer.transform.parent.GetComponent<Renderer>()
                        : null;
                    Material[] replacement = BuildShellMaterials(source?.sharedMaterials, template);
                    Material[] previous = shell.Materials;
                    shell.Renderer.sharedMaterials = replacement;
                    shell.Materials = replacement;
                    DestroyShellMaterials(previous);
                }

                ShellTemplateRevision = _shellTemplateRevision;
                Plugin.Log?.LogInfo("[CamoShellCloak] refreshed active shell materials from the Dreadweaver template.");
            }

            private void CreateSkinnedShell(SkinnedMeshRenderer source, Material template)
            {
                if (source.sharedMesh == null)
                    return;

                GameObject shellObject = NewShellObject(source);
                SkinnedMeshRenderer shell = shellObject.AddComponent<SkinnedMeshRenderer>();
                shell.sharedMesh = source.sharedMesh;
                shell.bones = source.bones;
                shell.rootBone = source.rootBone;
                shell.localBounds = source.localBounds;
                shell.updateWhenOffscreen = true;
                shell.shadowCastingMode = ShadowCastingMode.Off;
                shell.receiveShadows = false;
                shell.sharedMaterials = BuildShellMaterials(source.sharedMaterials, template);
                shell.enabled = false;

                Shells.Add(new Shell { Renderer = shell, Materials = shell.sharedMaterials });
            }

            private void CreateMeshShell(MeshRenderer source, Material template)
            {
                MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
                if (sourceFilter == null || sourceFilter.sharedMesh == null)
                    return;

                GameObject shellObject = NewShellObject(source);
                MeshFilter shellFilter = shellObject.AddComponent<MeshFilter>();
                shellFilter.sharedMesh = sourceFilter.sharedMesh;

                MeshRenderer shell = shellObject.AddComponent<MeshRenderer>();
                shell.shadowCastingMode = ShadowCastingMode.Off;
                shell.receiveShadows = false;
                shell.sharedMaterials = BuildShellMaterials(source.sharedMaterials, template);
                shell.enabled = false;

                Shells.Add(new Shell { Renderer = shell, Materials = shell.sharedMaterials });
            }

            private static GameObject NewShellObject(Renderer source)
            {
                GameObject shellObject = new GameObject(ShellName + "_" + source.name);
                shellObject.hideFlags = HideFlags.DontSave;
                shellObject.layer = source.gameObject.layer;
                shellObject.transform.SetParent(source.transform, false);
                shellObject.transform.localPosition = Vector3.zero;
                shellObject.transform.localRotation = Quaternion.identity;
                shellObject.transform.localScale = Vector3.one;
                return shellObject;
            }

            private static Material[] BuildShellMaterials(Material[] sourceMaterials, Material template)
            {
                int count = Mathf.Max(1, sourceMaterials != null ? sourceMaterials.Length : 0);
                var materials = new Material[count];
                for (int i = 0; i < count; i++)
                {
                    Material source = sourceMaterials != null && i < sourceMaterials.Length ? sourceMaterials[i] : null;
                    Material material = new Material(template) { name = "M_Y4NGZCamoShellInstance" };
                    ConfigureShellMaterial(material);
                    ApplySourceTint(material, source);
                    materials[i] = material;
                }

                return materials;
            }

            internal void SetShellsShown(bool shown)
            {
                for (int i = 0; i < Shells.Count; i++)
                {
                    Renderer renderer = Shells[i]?.Renderer;
                    if (renderer != null)
                        renderer.enabled = shown;
                }
            }

            internal void ApplyShellProperties(float fade, float intensity)
            {
                RefreshShellMaterialsIfNeeded(GetShellTemplate());
                float clampedFade = Mathf.Clamp01(fade);
                float clampedIntensity = Mathf.Clamp01(intensity);
                Color edge = Color.Lerp(
                    new Color(0.35f, 0.95f, 1f, 1f),
                    new Color(0.75f, 1f, 1f, 1f),
                    Mathf.Clamp01(intensity - 0.4f));
                Color emissive = edge * Mathf.Lerp(1.4f, 3.5f, clampedIntensity);
                float alpha = ShellBaseAlpha * clampedFade;

                for (int i = 0; i < Shells.Count; i++)
                {
                    Material[] materials = Shells[i]?.Materials;
                    if (materials == null)
                        continue;

                    for (int m = 0; m < materials.Length; m++)
                    {
                        Material material = materials[m];
                        if (material == null)
                            continue;

                        SetMaterialAlpha(material, "_BaseColor", alpha);
                        SetMaterialAlpha(material, "_Color", alpha);
                        SetMaterialAlpha(material, "_UnlitColor", alpha);
                        SetFloatIfPresent(material, "_Alpha", alpha);
                        SetFloatIfPresent(material, "_Dissolve", clampedFade);
                        SetFloatIfPresent(material, "_DissolveAmount", clampedFade);
                        SetFloatIfPresent(material, "_Cutoff", Mathf.Clamp01(clampedFade * 0.85f));
                        SetFloatIfPresent(material, "_AlphaCutoff", clampedFade);
                        SetColorIfPresent(material, "_EmissionColor", emissive * clampedFade);
                        SetColorIfPresent(material, "_EmissiveColor", emissive * clampedFade);
                        SetFloatIfPresent(material, "_EmissiveIntensity", Mathf.Lerp(1.5f, 5f, clampedIntensity));
                        SetFloatIfPresent(material, "_EmissionIntensity", Mathf.Lerp(1.5f, 5f, clampedIntensity));
                    }
                }
            }

            private static void SetMaterialAlpha(Material material, string property, float alpha)
            {
                if (material == null || !material.HasProperty(property))
                    return;

                Color color = material.GetColor(property);
                color.a = alpha;
                material.SetColor(property, color);
            }

            /// <summary>
            /// Hides / shows the real body via <see cref="Renderer.forceRenderingOff"/> so vanilla's
            /// per-frame LOD writes to <c>renderer.enabled</c> keep working untouched.
            /// </summary>
            internal void SetBodyHidden(bool hidden)
            {
                if (BodyHidden == hidden && Body != null)
                {
                    // Still refresh held items: the player may have swapped slots mid-cloak.
                    if (hidden)
                        RefreshHeldItems(hidden: true);
                    return;
                }

                BodyHidden = hidden;
                for (int i = 0; i < Body.Length; i++)
                {
                    Renderer renderer = Body[i];
                    if (renderer == null)
                        continue;

                    renderer.forceRenderingOff = hidden || BodyForceOffDefaults[i];
                }

                RefreshHeldItems(hidden);
                NextHeldRefresh = Time.time + HeldItemRefreshInterval;
            }

            /// <summary>
            /// Re-scans the player's held / pocketed items. When hiding, newly held renderers and
            /// lights are captured and suppressed; when showing, every captured override is undone
            /// and the table is cleared.
            /// </summary>
            internal void RefreshHeldItems(bool hidden)
            {
                if (!hidden)
                {
                    RestoreHeldItems();
                    return;
                }

                if (Player == null)
                    return;

                _scratchRenderers.Clear();
                _scratchLights.Clear();
                // Preserve the owner's first-person item/light. Other clients hide these.
                if (Player != GameNetworkManager.Instance?.localPlayerController)
                    CollectHeldVisuals(_scratchRenderers, _scratchLights);

                _releasedRenderers.Clear();
                foreach (var entry in _heldRenderers)
                    if (!_scratchRenderers.Contains(entry.Key))
                    {
                        ReleaseHeldRenderer(entry.Key);
                        _releasedRenderers.Add(entry.Key);
                    }
                foreach (var renderer in _releasedRenderers) _heldRenderers.Remove(renderer);
                _releasedLights.Clear();
                foreach (var entry in _heldLights)
                    if (!_scratchLights.Contains(entry.Key))
                    {
                        ReleaseHeldLight(entry.Key);
                        _releasedLights.Add(entry.Key);
                    }
                foreach (var light in _releasedLights) _heldLights.Remove(light);

                for (int i = 0; i < _scratchRenderers.Count; i++)
                {
                    Renderer renderer = _scratchRenderers[i];
                    if (renderer == null)
                        continue;

                    if (!_heldRenderers.ContainsKey(renderer))
                    {
                        _heldRenderers[renderer] = renderer.forceRenderingOff;
                        HeldRendererLeases.Acquire(renderer, renderer.forceRenderingOff);
                    }

                    renderer.forceRenderingOff = true;
                }

                for (int i = 0; i < _scratchLights.Count; i++)
                {
                    Light light = _scratchLights[i];
                    if (light == null)
                        continue;

                    if (!_heldLights.ContainsKey(light))
                    {
                        _heldLights[light] = light.enabled;
                        HeldLightLeases.Acquire(light, light.enabled);
                    }

                    light.enabled = false;
                }

                _scratchRenderers.Clear();
                _scratchLights.Clear();
            }

            private void CollectHeldVisuals(List<Renderer> renderers, List<Light> lights)
            {
                AddItemVisuals(Player.currentlyHeldObjectServer, renderers, lights);
                if (Player.helmetLight != null) lights.Add(Player.helmetLight);

                GrabbableObject[] slots = Player.ItemSlots;
                if (slots == null)
                    return;

                for (int i = 0; i < slots.Length; i++)
                    AddItemVisuals(slots[i], renderers, lights);
            }

            private void AddItemVisuals(GrabbableObject item, List<Renderer> renderers, List<Light> lights)
            {
                if (item == null)
                    return;

                // Only suppress items actually parented to this player; a dropped item keeps its
                // own visuals and must never be hidden by a cloak.
                if (item.playerHeldBy != Player)
                    return;

                Renderer[] itemRenderers = item.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < itemRenderers.Length; i++)
                {
                    if (IsCloakableItemRenderer(itemRenderers[i]) && !renderers.Contains(itemRenderers[i]))
                        renderers.Add(itemRenderers[i]);
                }

                Light[] itemLights = item.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < itemLights.Length; i++)
                {
                    if (itemLights[i] != null && !lights.Contains(itemLights[i]))
                        lights.Add(itemLights[i]);
                }
            }

            internal void EnforceHiddenLights()
            {
                foreach (var entry in _heldLights)
                    if (entry.Key != null) entry.Key.enabled = false;
            }

            private void RestoreHeldItems()
            {
                foreach (KeyValuePair<Renderer, bool> entry in _heldRenderers)
                {
                    ReleaseHeldRenderer(entry.Key);
                }

                foreach (KeyValuePair<Light, bool> entry in _heldLights)
                {
                    ReleaseHeldLight(entry.Key);
                }

                _heldRenderers.Clear();
                _heldLights.Clear();
            }

            /// <summary>Undoes every override this cloak owns and destroys its shells.</summary>
            internal void RestoreImmediate()
            {
                if (Body != null)
                {
                    for (int i = 0; i < Body.Length; i++)
                    {
                        Renderer renderer = Body[i];
                        if (renderer != null)
                            renderer.forceRenderingOff = BodyForceOffDefaults[i];
                    }
                }

                BodyHidden = false;
                RestoreHeldItems();

                for (int i = 0; i < Shells.Count; i++)
                {
                    Shell shell = Shells[i];
                    if (shell?.Renderer == null)
                        continue;

                    DestroyShellMaterials(shell.Materials);

                    UnityEngine.Object.Destroy(shell.Renderer.gameObject);
                }

                Shells.Clear();
                ShellsCreated = false;
                ShellTemplateRevision = -1;
                Cloaked = false;
                Settled = true;
            }

            private static void DestroyShellMaterials(Material[] materials)
            {
                if (materials == null)
                    return;

                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] != null)
                        UnityEngine.Object.Destroy(materials[i]);
            }
        }
    }
}
