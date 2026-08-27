using System;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Y4NGZUpgrades.Lucky8
{
    internal sealed class Lucky8MachineBehaviour : MonoBehaviour
    {
        private const float ScreenRampSeconds = 0.45f;
        private const float NearZeroEmission = 0.015f;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int BaseColorMapId = Shader.PropertyToID("_BaseColorMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private readonly Renderer[] screens = new Renderer[8];
        private readonly MaterialPropertyBlock[] screenBlocks = new MaterialPropertyBlock[8];
        private readonly Color[] authoredScreenEmission = new Color[8];
        private readonly Renderer[] frames = new Renderer[8];
        private readonly MaterialPropertyBlock[] frameBlocks = new MaterialPropertyBlock[8];
        private readonly Lucky8RewardDefinition[] resolvedRewardSources = new Lucky8RewardDefinition[8];
        private readonly Lucky8ResolvedIcon[] resolvedIcons = new Lucky8ResolvedIcon[8];
        private readonly TextMeshPro[] labels = new TextMeshPro[8];
        private readonly Renderer[] tokenLeds = new Renderer[3];
        private readonly MaterialPropertyBlock[] tokenLedBlocks = new MaterialPropertyBlock[3];
        private NetworkObject networkObject;
        private InteractTrigger trigger;
        private AudioSource audioSource;
        private Light marqueeLight;
        private Light rewardBayLight;
        private int lastHighlight = -2;
        private byte lastSoldMask = byte.MaxValue;
        private int lastSpins = -1;
        private bool lastWasSpinning;
        private bool visualStateInitialized;
        private bool promptInitialized;
        private bool lastPromptSpinning;
        private byte lastPromptSoldMask;
        private int lastPromptSpins;
        private int lastPromptCost;
        private int lastPromptAvailableTokens;
        private float lastPromptHoldDuration;
        private float screenEmissionLevel;
        private float nextRefresh;

        private void Start()
        {
            networkObject = GetComponent<NetworkObject>();
            DiscoverParts();
            ConfigureLights();
            ConfigureInteraction();
            ConfigureAudio();
            Lucky8Manager.RegisterMachine(this);
        }

        private void Update()
        {
            if (networkObject == null || !networkObject.IsSpawned)
                return;
            if (!Lucky8Manager.TryGetState(networkObject.NetworkObjectId, out Lucky8MachineState state))
                return;

            int highlight = ResolveHighlight(state);
            float emissionTarget = IsRewardLightingActive(state) ? 1f : 0f;
            float previousEmission = screenEmissionLevel;
            screenEmissionLevel = Mathf.MoveTowards(screenEmissionLevel, emissionTarget, Time.unscaledDeltaTime / ScreenRampSeconds);
            bool emissionChanged = !Mathf.Approximately(previousEmission, screenEmissionLevel);
            if (emissionChanged || Time.unscaledTime >= nextRefresh || highlight != lastHighlight || state.SoldMask != lastSoldMask || state.SpinsRemaining != lastSpins)
            {
                nextRefresh = Time.unscaledTime + 0.2f;
                ApplyState(state, highlight);
            }
            UpdatePrompt(state);
            UpdateLights(state);
        }

        internal Vector3 PrizeChutePosition
        {
            get
            {
                Transform chute = FindDeep(transform, "PrizeChute");
                return chute != null ? chute.position + transform.forward * 0.55f + Vector3.up * 0.1f : transform.position + transform.forward + Vector3.up * 0.35f;
            }
        }

        internal void PlayTick()
        {
            if (audioSource != null && Lucky8ProceduralAudio.Tick != null)
                audioSource.PlayOneShot(Lucky8ProceduralAudio.Tick, 0.42f);
        }

        internal void PlayWin()
        {
            if (audioSource != null && Lucky8ProceduralAudio.Win != null)
                audioSource.PlayOneShot(Lucky8ProceduralAudio.Win, 0.65f);
        }

        private void DiscoverParts()
        {
            for (int i = 0; i < 8; i++)
            {
                Transform slot = FindDeep(transform, "RewardSlot_" + i);
                screens[i] = slot != null ? slot.GetComponentInChildren<Renderer>(true) : null;
                if (screens[i] != null)
                {
                    screenBlocks[i] = new MaterialPropertyBlock();
                    screens[i].GetPropertyBlock(screenBlocks[i]);
                    authoredScreenEmission[i] = ReadAuthoredEmission(screens[i]);
                    Color startupEmission = authoredScreenEmission[i] * NearZeroEmission;
                    screenBlocks[i].SetColor(EmissiveColorId, startupEmission);
                    screenBlocks[i].SetColor(EmissionColorId, startupEmission);
                    screens[i].SetPropertyBlock(screenBlocks[i]);
                }
                Transform frame = FindDeep(transform, "RewardFrame_" + i);
                frames[i] = frame != null ? frame.GetComponentInChildren<Renderer>(true) : null;
                if (frames[i] != null)
                {
                    frameBlocks[i] = new MaterialPropertyBlock();
                    frames[i].GetPropertyBlock(frameBlocks[i]);
                }
                if (slot != null)
                    labels[i] = CreateLabel(slot, "RewardLabel_" + i, 1.0f);
            }
            for (int i = 0; i < 3; i++)
            {
                Transform led = FindDeep(transform, "TokenLed_" + i);
                tokenLeds[i] = led != null ? led.GetComponentInChildren<Renderer>(true) : null;
                if (tokenLeds[i] != null)
                {
                    tokenLedBlocks[i] = new MaterialPropertyBlock();
                    tokenLeds[i].GetPropertyBlock(tokenLedBlocks[i]);
                }
            }
        }

        private void ConfigureLights()
        {
            string issue = null;
            marqueeLight = CreatePointLight(
                FindDeep(transform, "MarqueeLightAnchor"),
                new Color(1f, 0.38f, 0.30f),
                2.5f,
                85f,
                "marquee",
                out string marqueeIssue);
            if (!string.IsNullOrEmpty(marqueeIssue)) issue = marqueeIssue;

            rewardBayLight = CreatePointLight(
                FindDeep(transform, "RewardBayLightAnchor"),
                new Color(1f, 0.68f, 0.42f),
                2f,
                65f,
                "reward bay",
                out string rewardIssue);
            if (!string.IsNullOrEmpty(rewardIssue))
                issue = string.IsNullOrEmpty(issue) ? rewardIssue : issue + "; " + rewardIssue;

            if (!string.IsNullOrEmpty(issue))
                Plugin.Log?.LogWarning("[LUCKY-8] Light setup degraded: " + issue);
        }

        private static Light CreatePointLight(
            Transform anchor,
            Color color,
            float range,
            float lumens,
            string label,
            out string issue)
        {
            issue = null;
            if (anchor == null)
            {
                issue = label + " anchor is missing";
                return null;
            }

            Light light;
            try
            {
                light = anchor.GetComponent<Light>() ?? anchor.gameObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = color;
                light.range = range;
                light.intensity = 1f;
                light.shadows = LightShadows.None;
                light.bounceIntensity = 0f;
                light.enabled = false;

                HDAdditionalLightData hdLight = anchor.GetComponent<HDAdditionalLightData>()
                    ?? anchor.gameObject.AddComponent<HDAdditionalLightData>();
                if (hdLight == null)
                {
                    issue = label + " HDAdditionalLightData is missing";
                    return light;
                }
                hdLight.SetLightTypeAndShape(HDLightTypeAndShape.Point);
                hdLight.SetRange(range);
                hdLight.SetIntensity(lumens, LightUnit.Lumen);
                hdLight.EnableShadows(false);
                return light;
            }
            catch (Exception exception)
            {
                issue = label + " HDRP light failed (" + exception.GetType().Name + ")";
                return anchor.GetComponent<Light>();
            }
        }

        private void UpdateLights(Lucky8MachineState state)
        {
            if (marqueeLight != null) marqueeLight.enabled = IsMachinePowered(state);
            if (rewardBayLight != null) rewardBayLight.enabled = IsRewardLightingActive(state);
        }

        private static bool IsMachinePowered(Lucky8MachineState state)
        {
            return state != null
                && !state.IsSpinning
                && state.SpinsRemaining > 0
                && state.SoldMask != byte.MaxValue;
        }

        private static bool IsRewardLightingActive(Lucky8MachineState state)
        {
            // An accepted token spend immediately enters the replicated spin state;
            // WinnerIndex then persists for the post-spin winner presentation.
            return state != null && (state.IsSpinning || state.WinnerIndex >= 0);
        }

        private void ConfigureInteraction()
        {
            Transform button = FindDeep(transform, "CenterButton");
            GameObject target = button != null ? button.gameObject : gameObject;
            int layer = LayerMask.NameToLayer("InteractableObject");
            target.layer = layer >= 0 ? layer : 9;
            TrySetTag(target, "InteractTrigger");
            Collider collider = target.GetComponent<Collider>();
            if (collider == null)
            {
                SphereCollider sphere = target.AddComponent<SphereCollider>();
                sphere.radius = 0.18f;
                sphere.isTrigger = true;
            }
            trigger = target.GetComponent<InteractTrigger>() ?? target.AddComponent<InteractTrigger>();
            trigger.interactable = true;
            trigger.oneHandedItemAllowed = true;
            trigger.twoHandedItemAllowed = true;
            trigger.holdInteraction = true;
            trigger.timeToHold = Lucky8Manager.EffectiveHoldDuration;
            trigger.timeToHoldSpeedMultiplier = 1f;
            trigger.interactCooldown = true;
            trigger.cooldownTime = 0.5f;
            trigger.hoverTip = "Insert 3 tokens : [" + Patches.UpgradeInteractInput.DisplayLabel() + "]";
            trigger.disabledHoverTip = "[ LUCKY-8 unavailable ]";
            trigger.disableTriggerMesh = true;
            if (trigger.onInteract == null) trigger.onInteract = new InteractEvent();
            trigger.onInteract.RemoveListener(OnInteract);
            trigger.onInteract.AddListener(OnInteract);
        }

        private void ConfigureAudio()
        {
            Lucky8ProceduralAudio.Ensure();
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
            audioSource.minDistance = 1.5f;
            audioSource.maxDistance = 14f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.clip = Lucky8ProceduralAudio.Hum;
            audioSource.loop = true;
            audioSource.volume = 0.11f;
            audioSource.Play();
        }

        private void OnInteract(GameNetcodeStuff.PlayerControllerB player)
        {
            if (player == null || player != StartOfRound.Instance?.localPlayerController || networkObject == null)
                return;
            Lucky8Manager.TryBeginLocalSpin(networkObject.NetworkObjectId, player);
        }

        private int ResolveHighlight(Lucky8MachineState state)
        {
            if (!state.IsSpinning)
                return state.WinnerIndex;
            double now = NetworkManager.Singleton?.ServerTime.Time ?? Time.timeAsDouble;
            float normalized = Mathf.Clamp01((float)((now - state.SpinStartedAt) / Math.Max(0.1f, state.SpinDuration)));
            int winner = Mathf.Clamp(state.WinnerIndex, 0, 7);
            int totalSteps = 24 + winner;
            float eased = 1f - Mathf.Pow(1f - normalized, 3f);
            return Mathf.Min(totalSteps, Mathf.FloorToInt(totalSteps * eased)) % 8;
        }

        private void ApplyState(Lucky8MachineState state, int highlight)
        {
            bool updateLabels = !visualStateInitialized || state.SoldMask != lastSoldMask;
            if (state.IsSpinning && highlight != lastHighlight)
                PlayTick();
            if (!state.IsSpinning && lastWasSpinning && highlight == state.WinnerIndex)
                PlayWin();

            for (int i = 0; i < 8; i++)
            {
                Lucky8RewardDefinition reward = state.Rewards[i];
                bool sold = state.IsSold(i);
                bool rewardChanged = !ReferenceEquals(resolvedRewardSources[i], reward);
                if (rewardChanged || resolvedIcons[i]?.Texture == null)
                {
                    resolvedRewardSources[i] = reward;
                    resolvedIcons[i] = Lucky8IconResolver.Resolve(reward);
                }

                bool selected = i == highlight;
                Color accent = FrameColor(reward);
                ApplyScreen(
                    screens[i],
                    screenBlocks[i],
                    authoredScreenEmission[i],
                    resolvedIcons[i]?.Texture,
                    accent,
                    selected,
                    sold,
                    screenEmissionLevel);
                ApplyFrame(frames[i], frameBlocks[i], accent, selected, sold);
                if ((updateLabels || rewardChanged) && labels[i] != null)
                {
                    labels[i].text = sold ? "SOLD\nOUT" : ShortLabel(reward?.DisplayName);
                    labels[i].color = sold ? new Color(0.9f, 0.16f, 0.1f) : Color.white;
                }
            }
            for (int i = 0; i < tokenLeds.Length; i++)
                ApplyEmission(tokenLeds[i], tokenLedBlocks[i], i < Mathf.Min(3, Lucky8Manager.EffectiveSpinCost), i < state.SpinsRemaining ? new Color(1f, 0.55f, 0.08f) : new Color(0.15f, 0.05f, 0.03f));

            lastHighlight = highlight;
            lastSoldMask = state.SoldMask;
            lastSpins = state.SpinsRemaining;
            lastWasSpinning = state.IsSpinning;
            visualStateInitialized = true;
        }

        private void UpdatePrompt(Lucky8MachineState state)
        {
            if (trigger == null) return;
            float holdDuration = Lucky8Manager.EffectiveHoldDuration;
            int cost = Lucky8Manager.EffectiveSpinCost;
            int availableTokens = ProgressionApi.AvailableTokens;
            if (promptInitialized
                && lastPromptSpinning == state.IsSpinning
                && lastPromptSoldMask == state.SoldMask
                && lastPromptSpins == state.SpinsRemaining
                && lastPromptCost == cost
                && lastPromptAvailableTokens == availableTokens
                && Mathf.Approximately(lastPromptHoldDuration, holdDuration))
                return;

            promptInitialized = true;
            lastPromptSpinning = state.IsSpinning;
            lastPromptSoldMask = state.SoldMask;
            lastPromptSpins = state.SpinsRemaining;
            lastPromptCost = cost;
            lastPromptAvailableTokens = availableTokens;
            lastPromptHoldDuration = holdDuration;
            trigger.timeToHold = holdDuration;
            trigger.interactable = !state.IsSpinning && state.SpinsRemaining > 0 && state.SoldMask != byte.MaxValue;
            if (state.IsSpinning)
                trigger.disabledHoverTip = "[ SELECTOR SPINNING ]";
            else if (state.SpinsRemaining <= 0 || state.SoldMask == byte.MaxValue)
                trigger.disabledHoverTip = "[ MACHINE DEPLETED ]";
            else if (availableTokens < cost)
                trigger.hoverTip = $"Need {cost} tokens ({availableTokens} available)";
            else
                trigger.hoverTip = $"Insert {cost} tokens - {state.SpinsRemaining} spin(s) left : [{Patches.UpgradeInteractInput.DisplayLabel()}]";
        }

        private static void ApplyScreen(
            Renderer renderer,
            MaterialPropertyBlock block,
            Color authoredEmission,
            Texture icon,
            Color accent,
            bool highlighted,
            bool sold,
            float emissionLevel)
        {
            if (renderer == null || block == null) return;
            Color baseColor = sold
                ? new Color(0.10f, 0.018f, 0.012f)
                : Color.white * (highlighted ? 1.15f : 0.78f);
            float authoredLevel = Mathf.Lerp(NearZeroEmission, 1f, emissionLevel);
            Color emission = authoredEmission * authoredLevel;
            if (sold) emission *= 0.13f;
            if (highlighted) emission += accent * (sold ? 1.55f : 2.2f);
            block.SetColor(BaseColorId, baseColor);
            block.SetColor(ColorId, baseColor);
            block.SetColor(EmissiveColorId, emission);
            block.SetColor(EmissionColorId, emission);
            block.SetTexture(BaseColorMapId, icon);
            block.SetTexture(MainTexId, icon);
            renderer.SetPropertyBlock(block);
        }

        private static void ApplyFrame(
            Renderer renderer,
            MaterialPropertyBlock block,
            Color accent,
            bool highlighted,
            bool sold)
        {
            if (renderer == null || block == null) return;
            Color baseColor = sold ? new Color(0.18f, 0.025f, 0.018f) : accent;
            if (highlighted) baseColor *= sold ? 1.2f : 1.28f;
            Color emission = baseColor * (highlighted ? (sold ? 1.5f : 2.35f) : (sold ? 0.025f : 0.10f));
            block.SetColor(BaseColorId, baseColor);
            block.SetColor(ColorId, baseColor);
            block.SetColor(EmissiveColorId, emission);
            block.SetColor(EmissionColorId, emission);
            renderer.SetPropertyBlock(block);
        }

        private static void ApplyEmission(Renderer renderer, MaterialPropertyBlock block, bool enabled, Color color)
        {
            if (renderer == null || block == null) return;
            Color applied = enabled ? color : new Color(0.04f, 0.025f, 0.015f);
            block.SetColor(BaseColorId, applied);
            block.SetColor(ColorId, applied);
            block.SetColor(EmissiveColorId, applied * (enabled ? 2f : 0.1f));
            block.SetColor(EmissionColorId, applied * (enabled ? 2f : 0.1f));
            renderer.SetPropertyBlock(block);
        }

        private static Color ReadAuthoredEmission(Renderer renderer)
        {
            Material material = renderer != null ? renderer.sharedMaterial : null;
            if (material != null)
            {
                if (material.HasProperty(EmissiveColorId))
                {
                    Color color = material.GetColor(EmissiveColorId);
                    if (color.maxColorComponent > 0f) return color;
                }
                if (material.HasProperty(EmissionColorId))
                {
                    Color color = material.GetColor(EmissionColorId);
                    if (color.maxColorComponent > 0f) return color;
                }
            }
            return new Color(1.0f, 0.74f, 0.45f) * 1.1f;
        }

        private static Color CategoryColor(Lucky8RewardCategory category)
        {
            switch (category)
            {
                case Lucky8RewardCategory.Suit: return new Color(0.12f, 0.7f, 0.8f);
                case Lucky8RewardCategory.Cosmetic: return new Color(0.75f, 0.24f, 0.85f);
                case Lucky8RewardCategory.Ammo: return new Color(0.92f, 0.76f, 0.18f);
                case Lucky8RewardCategory.Emote: return new Color(0.36f, 0.62f, 1f);
                default: return new Color(0.94f, 0.45f, 0.08f);
            }
        }

        private static Color FrameColor(Lucky8RewardDefinition reward)
        {
            Color category = CategoryColor(reward?.Category ?? Lucky8RewardCategory.Weapon);
            if (reward?.Category == Lucky8RewardCategory.Emote
                && Lucky8RewardPresentation.TryGetColor(reward, out Color emoteColor))
            {
                category = emoteColor;
            }
            switch (reward?.Rarity ?? Lucky8Rarity.Common)
            {
                case Lucky8Rarity.Uncommon:
                    return Color.Lerp(category, new Color(0.32f, 0.92f, 0.42f), 0.18f) * 0.92f;
                case Lucky8Rarity.Rare:
                    return Color.Lerp(category, new Color(0.18f, 0.58f, 1f), 0.28f) * 1.08f;
                case Lucky8Rarity.Legendary:
                    return Color.Lerp(category, new Color(1f, 0.82f, 0.24f), 0.40f) * 1.22f;
                default:
                    return category * 0.72f;
            }
        }

        private TextMeshPro CreateLabel(Transform slot, string name, float size)
        {
            var labelObject = new GameObject(name);
            labelObject.transform.SetParent(transform, true);
            labelObject.transform.position = slot.position + transform.forward * 0.035f - transform.up * 0.061f;
            labelObject.transform.rotation = Quaternion.LookRotation(-transform.forward, transform.up);
            labelObject.transform.localScale = Vector3.one * 0.12f;
            TextMeshPro label = labelObject.AddComponent<TextMeshPro>();
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = size;
            label.enableWordWrapping = true;
            label.rectTransform.sizeDelta = new Vector2(2.25f, 0.72f);
            return label;
        }

        private static string ShortLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "?";
            value = value.Trim().ToUpperInvariant();
            return value.Length <= 16 ? value : value.Substring(0, 15) + "…";
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase)) return child;
            return null;
        }

        private static void TrySetTag(GameObject target, string tag)
        {
            try { target.tag = tag; }
            catch { }
        }

        private void OnDestroy()
        {
            if (trigger?.onInteract != null) trigger.onInteract.RemoveListener(OnInteract);
            Lucky8Manager.UnregisterMachine(this);
        }
    }

    internal static class Lucky8ProceduralAudio
    {
        internal static AudioClip Hum { get; private set; }
        internal static AudioClip Tick { get; private set; }
        internal static AudioClip Win { get; private set; }

        internal static void Ensure()
        {
            if (Hum != null) return;
            Hum = Tone("Lucky8_Hum", 2f, 56f, 0.08f, false);
            Tick = Tone("Lucky8_Tick", 0.055f, 920f, 0.5f, true);
            Win = WinTone();
        }

        private static AudioClip Tone(string name, float duration, float frequency, float volume, bool decay)
        {
            const int sampleRate = 22050;
            int count = Mathf.CeilToInt(duration * sampleRate);
            float[] samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = decay ? 1f - i / (float)count : 1f;
                samples[i] = Mathf.Sin(t * frequency * Mathf.PI * 2f) * volume * envelope;
            }
            AudioClip clip = AudioClip.Create(name, count, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static AudioClip WinTone()
        {
            const int sampleRate = 22050;
            const float duration = 0.8f;
            int count = Mathf.CeilToInt(duration * sampleRate);
            float[] samples = new float[count];
            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)sampleRate;
                int note = Mathf.Min(notes.Length - 1, Mathf.FloorToInt(t / (duration / notes.Length)));
                samples[i] = Mathf.Sin(t * notes[note] * Mathf.PI * 2f) * 0.34f * (1f - t / duration);
            }
            AudioClip clip = AudioClip.Create("Lucky8_Win", count, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
