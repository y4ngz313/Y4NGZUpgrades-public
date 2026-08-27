using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Lucky8
{
    internal static class Lucky8RuntimeAssets
    {
        private const string BundleFileName = "y4ngz-lucky8.bundle";
        private sealed class MaterialIntent
        {
            internal readonly Color BaseColor;
            internal readonly float Metallic;
            internal readonly float Smoothness;
            internal readonly Color EmissiveColor;
            internal readonly bool UsesBaseMap;
            internal readonly bool UsesNormalMap;
            internal readonly bool UsesMaskMap;
            internal readonly bool UsesEmissiveMap;

            internal MaterialIntent(
                Color baseColor,
                float metallic,
                float smoothness,
                Color emissiveColor = default,
                bool usesBaseMap = false,
                bool usesNormalMap = false,
                bool usesMaskMap = false,
                bool usesEmissiveMap = false)
            {
                BaseColor = baseColor;
                Metallic = metallic;
                Smoothness = smoothness;
                EmissiveColor = emissiveColor;
                UsesBaseMap = usesBaseMap;
                UsesNormalMap = usesNormalMap;
                UsesMaskMap = usesMaskMap;
                UsesEmissiveMap = usesEmissiveMap;
            }
        }

        private static readonly MaterialIntent DefaultMaterialIntent =
            new MaterialIntent(new Color(0.05f, 0.055f, 0.06f), 0.78f, 0.58f);

        private static readonly Dictionary<string, MaterialIntent> MaterialIntents =
            new Dictionary<string, MaterialIntent>(StringComparer.OrdinalIgnoreCase)
            {
                ["M_Lucky8_CreamPaint"] = new MaterialIntent(new Color(0.72f, 0.67f, 0.56f), 0.25f, 0.45f, usesBaseMap: true, usesNormalMap: true, usesMaskMap: true),
                ["M_Lucky8_BluePaint"] = new MaterialIntent(new Color(0.05f, 0.21f, 0.33f), 0.30f, 0.48f, usesBaseMap: true, usesNormalMap: true, usesMaskMap: true),
                ["M_Lucky8_DarkSteel"] = new MaterialIntent(new Color(0.05f, 0.055f, 0.06f), 0.78f, 0.58f, usesBaseMap: true, usesMaskMap: true),
                ["M_Lucky8_SteelEdge"] = new MaterialIntent(new Color(0.40f, 0.41f, 0.43f), 0.86f, 0.62f, usesBaseMap: true, usesMaskMap: true),
                ["M_Lucky8_Rubber"] = new MaterialIntent(new Color(0.03f, 0.032f, 0.032f), 0.02f, 0.18f, usesBaseMap: true, usesNormalMap: true),
                ["M_Lucky8_IconFrame"] = new MaterialIntent(new Color(0.095f, 0.10f, 0.105f), 0.68f, 0.52f),
                ["M_Lucky8_IconScreen"] = new MaterialIntent(new Color(0.010f, 0.012f, 0.010f), 0.05f, 0.85f, new Color(1.0f, 0.74f, 0.45f) * 1.1f),
                ["M_Lucky8_ButtonPlastic"] = new MaterialIntent(new Color(0.20f, 0.022f, 0.015f), 0.05f, 0.68f, new Color(0.42f, 0.02f, 0.008f) * 0.18f),
                ["M_Lucky8_Indicator"] = new MaterialIntent(new Color(0.08f, 0.34f, 0.13f), 0.05f, 0.76f, new Color(0.05f, 0.65f, 0.12f) * 1.8f),
                ["M_Lucky8_BrandingTexture"] = new MaterialIntent(Color.white, 0.05f, 0.55f, Color.white * 2.2f, usesBaseMap: true, usesEmissiveMap: true),
                ["M_Lucky8_InstructionDecal"] = new MaterialIntent(Color.white, 0.10f, 0.40f, usesBaseMap: true),
                ["M_Lucky8_CapsuleGlass"] = new MaterialIntent(new Color(0.08f, 0.26f, 0.24f), 0.15f, 0.85f, new Color(0.02f, 0.45f, 0.32f) * 0.5f),
                ["M_Lucky8_CapsuleLabelDecal"] = new MaterialIntent(Color.white, 0.05f, 0.50f, usesBaseMap: true),
            };

        private static AssetBundle bundle;
        private static GameObject holder;
        private static readonly Dictionary<string, Material> runtimeMaterials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        private static bool machineRegistered;
        private static bool capsuleRegistered;

        internal static GameObject MachinePrefab { get; private set; }
        internal static GameObject CapsulePrefab { get; private set; }
        internal static Item CapsuleItem { get; private set; }

        internal static void Initialize()
        {
            LoadBundle();
            TryRegisterMachinePrefab();
        }

        internal static void Shutdown()
        {
            if (holder != null) UnityEngine.Object.Destroy(holder);
            bundle?.Unload(false);
            bundle = null;
            holder = null;
            foreach (Material material in runtimeMaterials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            runtimeMaterials.Clear();
            MachinePrefab = null;
            CapsulePrefab = null;
            CapsuleItem = null;
            machineRegistered = false;
            capsuleRegistered = false;
        }

        internal static bool TryRegisterMachinePrefab()
        {
            if (machineRegistered) return MachinePrefab != null;
            EnsureHolder();
            GameObject root = new GameObject("Y4NGZ_Lucky8_Machine_NetworkPrefab");
            root.transform.SetParent(holder.transform, false);
            root.SetActive(true);
            NetworkObject networkObject = root.AddComponent<NetworkObject>();
            AssignStableNetworkHash(networkObject, "lucky8_machine");
            BoxCollider collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 1.21f, 0f);
            collider.size = new Vector3(1.35f, 2.42f, 0.70f);
            root.AddComponent<Lucky8MachineBehaviour>();

            GameObject authored = bundle?.LoadAsset<GameObject>("Lucky8MachineVisual");
            if (authored != null)
            {
                GameObject visual = UnityEngine.Object.Instantiate(authored, root.transform);
                visual.name = "Lucky8MachineVisual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                RemapAuthoredMaterials(visual);
            }
            else
            {
                BuildFallbackMachine(root.transform);
            }

            DawnLibCompat.RegisterNetworkPrefab(root);
            MachinePrefab = root;
            machineRegistered = true;
            Plugin.Log?.LogInfo("[LUCKY-8] Registered machine network prefab" + (authored != null ? " from authored bundle." : " with procedural fallback visuals."));
            return true;
        }

        internal static bool TryRegisterCapsulePrefab()
        {
            if (capsuleRegistered) return CapsulePrefab != null;
            Item baseItem = FindBasePhysicsProp();
            if (baseItem?.spawnPrefab == null)
                return false;

            EnsureHolder();
            GameObject clone = UnityEngine.Object.Instantiate(baseItem.spawnPrefab, holder.transform);
            clone.name = "Y4NGZ_Lucky8_ClaimCapsule_NetworkPrefab";
            clone.SetActive(true);
            AssignStableNetworkHash(clone.GetComponent<NetworkObject>(), "lucky8_claim_capsule");

            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;

            GameObject authored = bundle?.LoadAsset<GameObject>("Lucky8ClaimCapsuleVisual");
            if (authored != null)
            {
                GameObject visual = UnityEngine.Object.Instantiate(authored, clone.transform);
                visual.name = "Lucky8ClaimCapsuleVisual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                RemapAuthoredMaterials(visual);
                foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = true;
            }
            else
            {
                BuildFallbackCapsule(clone.transform);
            }

            Item item = UnityEngine.Object.Instantiate(baseItem);
            item.name = "Y4NGZ_Lucky8_ClaimCapsule_Item";
            item.itemName = "LUCKY-8 Claim Capsule";
            item.isScrap = false;
            item.creditsWorth = 0;
            item.minValue = 0;
            item.maxValue = 0;
            item.saveItemVariable = false;
            item.holdButtonUse = false;
            item.syncUseFunction = false;
            item.syncInteractLRFunction = false;
            item.twoHanded = false;
            item.twoHandedAnimation = false;
            item.spawnPrefab = clone;
            string activate = Gui.UpgradeInput.VanillaDisplayLabel("ActivateItem", "LMB");
            string drop = Gui.UpgradeInput.VanillaDisplayLabel("DiscardHeldObject", "G");
            item.toolTips = new[] { $"Claim reward : [{activate}]", $"Drop : [{drop}]" };
            item.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(item);
            foreach (GrabbableObject grabbable in clone.GetComponents<GrabbableObject>())
                grabbable.itemProperties = item;

            clone.AddComponent<Lucky8ClaimCapsule>();
            DawnLibCompat.FixMixerGroups(clone);
            DawnLibCompat.RegisterNetworkPrefab(clone);
            DawnLibCompat.RegisterItem("lucky8_claim_capsule", item);
            CapsulePrefab = clone;
            CapsuleItem = item;
            capsuleRegistered = true;
            Plugin.Log?.LogInfo("[LUCKY-8] Registered transferable claim capsule.");
            return true;
        }

        private static void LoadBundle()
        {
            try
            {
                string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string path = Path.Combine(directory, BundleFileName);
                if (File.Exists(path)) bundle = AssetBundle.LoadFromFile(path);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Authored bundle load failed; using fallback: " + e.Message);
            }
        }

        private static void EnsureHolder()
        {
            if (holder != null) return;
            holder = new GameObject("Y4NGZ_Lucky8_InactivePrefabHolder");
            holder.hideFlags = HideFlags.HideAndDontSave;
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);
        }

        private static Item FindBasePhysicsProp()
        {
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null) return null;
            foreach (Item item in items)
            {
                GrabbableObject grabbable = item?.spawnPrefab?.GetComponent<GrabbableObject>();
                if (grabbable != null && grabbable.GetType() == typeof(PhysicsProp))
                    return item;
            }
            return null;
        }

        private static void AssignStableNetworkHash(NetworkObject networkObject, string id)
        {
            if (networkObject == null) return;
            using MD5 md5 = MD5.Create();
            uint hash = BitConverter.ToUInt32(md5.ComputeHash(Encoding.UTF8.GetBytes(Plugin.Guid + ".Lucky8." + id)), 0);
            FieldInfo field = typeof(NetworkObject).GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? typeof(NetworkObject).GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(networkObject, hash);
        }

        private static void BuildFallbackMachine(Transform root)
        {
            CreatePrimitive(root, "CabinetLowerBlue", PrimitiveType.Cube, new Vector3(0f, 0.65f, 0f), new Vector3(1.32f, 1.30f, 0.68f), new Color(0.05f, 0.21f, 0.33f), 0.30f, 0.48f, materialName: "M_Lucky8_BluePaint");
            CreatePrimitive(root, "CabinetUpperCream", PrimitiveType.Cube, new Vector3(0f, 1.82f, 0f), new Vector3(1.32f, 1.10f, 0.68f), new Color(0.72f, 0.67f, 0.56f), 0.25f, 0.45f, materialName: "M_Lucky8_CreamPaint");
            Vector2[] positions =
            {
                new Vector2(-0.39f, 1.92f), new Vector2(0f, 1.92f), new Vector2(0.39f, 1.92f),
                new Vector2(0.39f, 1.58f), new Vector2(0.39f, 1.24f), new Vector2(0f, 1.24f),
                new Vector2(-0.39f, 1.24f), new Vector2(-0.39f, 1.58f),
            };
            for (int i = 0; i < positions.Length; i++)
            {
                CreatePrimitive(root, "RewardFrame_" + i, PrimitiveType.Cube, new Vector3(positions[i].x, positions[i].y, 0.350f), new Vector3(0.318f, 0.238f, 0.014f), new Color(0.095f, 0.10f, 0.105f), 0.68f, 0.52f, materialName: "M_Lucky8_IconFrame");
                CreatePrimitive(root, "RewardSlot_" + i, PrimitiveType.Cube, new Vector3(positions[i].x, positions[i].y, 0.363f), new Vector3(0.286f, 0.206f, 0.012f), new Color(0.010f, 0.012f, 0.010f), 0.05f, 0.85f, materialName: "M_Lucky8_IconScreen");
            }
            CreatePrimitive(root, "CenterButton", PrimitiveType.Cube, new Vector3(0f, 1.58f, 0.37f), new Vector3(0.185f, 0.185f, 0.016f), new Color(0.20f, 0.022f, 0.015f), 0.05f, 0.68f, materialName: "M_Lucky8_ButtonPlastic");
            CreatePrimitive(root, "PrizeChute", PrimitiveType.Cube, new Vector3(0f, 0.48f, 0.36f), new Vector3(0.69f, 0.18f, 0.02f), new Color(0.03f, 0.032f, 0.032f), 0.02f, 0.18f, materialName: "M_Lucky8_Rubber");
            for (int i = 0; i < 3; i++)
                CreatePrimitive(root, "TokenLed_" + i, PrimitiveType.Cube, new Vector3(-0.09f + i * 0.09f, 1.005f, 0.36f), new Vector3(0.042f, 0.026f, 0.012f), new Color(0.08f, 0.34f, 0.13f), 0.05f, 0.76f, materialName: "M_Lucky8_Indicator");

            CreatePrimitive(root, "Lucky8_BrandingDecal", PrimitiveType.Cube, new Vector3(0f, 2.18f, 0.355f), new Vector3(0.92f, 0.20f, 0.012f), Color.white, 0.05f, 0.55f, materialName: "M_Lucky8_BrandingTexture");
            CreatePrimitive(root, "TokenPanelDecal", PrimitiveType.Cube, new Vector3(-0.10f, 0.92f, 0.363f), new Vector3(0.36f, 0.14f, 0.008f), Color.white, 0.10f, 0.40f, materialName: "M_Lucky8_InstructionDecal");
            CreatePrimitive(root, "StatusAccent", PrimitiveType.Cube, new Vector3(0f, 2.005f, 0.356f), new Vector3(0.88f, 0.016f, 0.010f), new Color(0.08f, 0.34f, 0.13f), 0.05f, 0.76f, materialName: "M_Lucky8_Indicator");
            CreateAnchor(root, "MarqueeLightAnchor", new Vector3(0f, 2.175f, 0.40f));
            CreateAnchor(root, "RewardBayLightAnchor", new Vector3(0f, 1.565f, 0.43f));
        }

        private static void RemapAuthoredMaterials(GameObject root)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material sourceMaterial = materials[i];
                    string sourceName = sourceMaterial != null ? sourceMaterial.name.Replace(" (Instance)", string.Empty) : renderer.name;
                    materials[i] = GetRuntimeMaterial(sourceName, sourceMaterial);
                }
                renderer.sharedMaterials = materials;
            }
        }

        private static Material GetRuntimeMaterial(string sourceName, Material sourceMaterial)
        {
            if (runtimeMaterials.TryGetValue(sourceName, out Material cached) && cached != null)
                return cached;

            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            if (shader == null)
                throw new InvalidOperationException("LUCKY-8 could not locate a runtime lit shader.");

            if (!MaterialIntents.TryGetValue(sourceName, out MaterialIntent intent))
                intent = DefaultMaterialIntent;

            var material = new Material(shader)
            {
                name = "Lucky8_Runtime_" + sourceName,
                hideFlags = HideFlags.HideAndDontSave,
            };

            bool copiedBaseMap = false;
            if (intent.UsesBaseMap)
            {
                copiedBaseMap = CopyTexture(sourceMaterial, material, "_BaseColorMap", "_BaseColorMap", "_MainTex");
                copiedBaseMap = CopyTexture(sourceMaterial, material, "_MainTex", "_BaseColorMap", "_MainTex") || copiedBaseMap;
            }
            Color baseColor = copiedBaseMap ? Color.white : intent.BaseColor;
            SetColor(material, "_BaseColor", baseColor);
            SetColor(material, "_Color", baseColor);
            SetFloat(material, "_Metallic", intent.Metallic);
            SetFloat(material, "_Smoothness", intent.Smoothness);
            SetColor(material, "_EmissiveColor", intent.EmissiveColor);
            SetColor(material, "_EmissionColor", intent.EmissiveColor);

            if (intent.UsesNormalMap)
            {
                bool copiedNormal = CopyTexture(sourceMaterial, material, "_NormalMap", "_NormalMap", "_BumpMap");
                copiedNormal = CopyTexture(sourceMaterial, material, "_BumpMap", "_NormalMap", "_BumpMap") || copiedNormal;
                if (copiedNormal)
                {
                    SetFloat(material, "_NormalScale", 0.8f);
                    SetFloat(material, "_BumpScale", 0.8f);
                    material.EnableKeyword("_NORMALMAP");
                    material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
                }
            }
            if (intent.UsesMaskMap)
            {
                bool copiedMask = CopyTexture(sourceMaterial, material, "_MaskMap", "_MaskMap", "_MetallicGlossMap");
                copiedMask = CopyTexture(sourceMaterial, material, "_MetallicGlossMap", "_MaskMap", "_MetallicGlossMap") || copiedMask;
                if (copiedMask)
                {
                    material.EnableKeyword("_MASKMAP");
                    material.EnableKeyword("_METALLICGLOSSMAP");
                }
            }
            if (intent.UsesEmissiveMap)
            {
                bool copiedEmission = CopyTexture(sourceMaterial, material, "_EmissiveColorMap", "_EmissiveColorMap", "_EmissionMap");
                copiedEmission = CopyTexture(sourceMaterial, material, "_EmissionMap", "_EmissiveColorMap", "_EmissionMap") || copiedEmission;
                if (copiedEmission) material.EnableKeyword("_EMISSIVE_COLOR_MAP");
            }
            if (intent.EmissiveColor.maxColorComponent > 0f || intent.UsesEmissiveMap)
            {
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            runtimeMaterials[sourceName] = material;
            return material;
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static bool CopyTexture(Material source, Material target, string targetProperty, params string[] sourceProperties)
        {
            if (source == null || !target.HasProperty(targetProperty)) return false;
            foreach (string sourceProperty in sourceProperties)
            {
                if (!source.HasProperty(sourceProperty)) continue;
                Texture texture = source.GetTexture(sourceProperty);
                if (texture == null) continue;
                target.SetTexture(targetProperty, texture);
                target.SetTextureScale(targetProperty, source.GetTextureScale(sourceProperty));
                target.SetTextureOffset(targetProperty, source.GetTextureOffset(sourceProperty));
                return true;
            }
            return false;
        }

        private static void BuildFallbackCapsule(Transform root)
        {
            CreatePrimitive(root, "CapsuleVisual", PrimitiveType.Capsule, Vector3.zero, new Vector3(0.32f, 0.42f, 0.32f), new Color(0.08f, 0.26f, 0.24f), 0.15f, 0.85f, materialName: "M_Lucky8_CapsuleGlass");
            CreatePrimitive(root, "Capsule8Decal", PrimitiveType.Cylinder, new Vector3(0f, 0.04f, 0.165f), new Vector3(0.052f, 0.006f, 0.052f), Color.white, 0.05f, 0.50f, new Vector3(90f, 0f, 0f), "M_Lucky8_CapsuleLabelDecal");
        }

        private static void CreateAnchor(Transform parent, string name, Vector3 position)
        {
            var anchor = new GameObject(name);
            anchor.transform.SetParent(parent, false);
            anchor.transform.localPosition = position;
        }

        private static GameObject CreatePrimitive(
            Transform parent,
            string name,
            PrimitiveType type,
            Vector3 position,
            Vector3 scale,
            Color color,
            float metallic,
            float smoothness,
            Vector3? euler = null,
            string materialName = null)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localEulerAngles = euler ?? Vector3.zero;
            go.transform.localScale = scale;
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            Renderer renderer = go.GetComponent<Renderer>();
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            if (renderer != null && shader != null)
            {
                Material material;
                if (!string.IsNullOrEmpty(materialName))
                {
                    material = GetRuntimeMaterial(materialName, null);
                }
                else
                {
                    material = new Material(shader) { name = "Lucky8_" + name };
                    SetColor(material, "_BaseColor", color);
                    SetColor(material, "_Color", color);
                    SetFloat(material, "_Metallic", metallic);
                    SetFloat(material, "_Smoothness", smoothness);
                }
                renderer.sharedMaterial = material;
            }
            return go;
        }
    }
}
