using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    internal static class OutlineRuntimeAssets
    {
        private const string BundleName = "y4ngz-outlineeffect.lethalbundle";

        private static readonly Dictionary<string, Shader> ShaderCache = new Dictionary<string, Shader>();
        private static AssetBundle _bundle;
        private static bool _loadAttempted;

        internal static Shader LoadShader(string shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
                return null;

            if (ShaderCache.TryGetValue(shaderName, out Shader cached) && cached != null)
                return cached;

            Shader shader = Resources.Load<Shader>(shaderName);
            if (shader == null)
                shader = LoadShaderFromBundles(shaderName);

            if (shader != null)
                ShaderCache[shaderName] = shader;
            else
                Plugin.Log?.LogWarning($"OutlineEffect shader '{shaderName}' could not be loaded. Missing {BundleName}?");

            return shader;
        }

        private static Shader LoadShaderFromBundles(string shaderName)
        {
            foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
            {
                // LoadAsset on a streamed scene bundle throws (BetterArmory-public #1), and after a
                // lobby cycle LethalLevelLoader keeps moon scene bundles in this list.
                if (loaded == null || loaded.isStreamedSceneAssetBundle)
                    continue;

                Shader shader;
                try
                {
                    shader = loaded.LoadAsset<Shader>(shaderName);
                }
                catch (System.Exception)
                {
                    // Foreign bundle in an unknown state — a probe miss, not an error.
                    continue;
                }

                if (shader != null)
                    return shader;
            }

            EnsureBundleLoaded();
            return _bundle != null ? _bundle.LoadAsset<Shader>(shaderName) : null;
        }

        private static void EnsureBundleLoaded()
        {
            if (_loadAttempted)
                return;

            _loadAttempted = true;
            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string bundlePath = Path.Combine(dllDir ?? "", BundleName);
            if (!File.Exists(bundlePath))
                return;

            _bundle = AssetBundle.LoadFromFile(bundlePath);
            if (_bundle == null)
                Plugin.Log?.LogWarning($"Failed to load outline effect bundle at {bundlePath}");
        }
    }
}
