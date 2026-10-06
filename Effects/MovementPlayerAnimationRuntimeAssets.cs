using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    internal static class MovementPlayerAnimationRuntimeAssets
    {
        internal const string ActiveBool = "Y4NGZ_Movement_Active";
        internal const string EnterTrigger = "Y4NGZ_Slide_Enter";
        internal const string ExitTrigger = "Y4NGZ_Slide_Exit";
        internal const string Mantle1mTrigger = "Y4NGZ_Mantle1m_Enter";
        internal const string Mantle2mTrigger = "Y4NGZ_Mantle2m_Enter";
        internal const string FullBodyLayerName = "Y4NGZMovementFullBody";
        internal const string FirstPersonArmsLayerName = "Y4NGZMovementFirstPersonArms";
        internal const string LocalBodyLayerName = "Y4NGZMovementLocalBody";

        private const string PrimaryBundleFile = "y4ngz-movement-playeranimations.lethalbundle";
        private const string SecondaryBundleFile = "y4ngz-movement-playeranimations.bundle";
        private const string PrimaryControllerAsset = "Y4NGZ_Movement_PlayerMetarig.controller";
        private const string SecondaryControllerAsset = "Y4NGZ_Movement_PlayerMetarig";
        private const int MovementControllerContractVersion = 3;

        private static readonly RequiredAnimatorParameter[] RequiredParameters =
        {
            new RequiredAnimatorParameter(ActiveBool, AnimatorControllerParameterType.Bool),
            new RequiredAnimatorParameter(EnterTrigger, AnimatorControllerParameterType.Trigger),
            new RequiredAnimatorParameter(ExitTrigger, AnimatorControllerParameterType.Trigger),
            new RequiredAnimatorParameter(Mantle1mTrigger, AnimatorControllerParameterType.Trigger),
            new RequiredAnimatorParameter(Mantle2mTrigger, AnimatorControllerParameterType.Trigger),
        };

        private static readonly string[] RequiredVanillaLayerNames =
        {
            "UpperBodyEmotes",
            "EmotesNoArms",
            "HoldingItemsRightHand",
            "HoldingItemsBothHands",
            "SpecialAnimations",
        };

        private static readonly string[] RequiredMovementLayerNames =
        {
            FullBodyLayerName,
            FirstPersonArmsLayerName,
            LocalBodyLayerName,
        };

        private static readonly string[] RequiredMovementStateNames =
        {
            "Empty",
            "SlideStart",
            "SlideLoop",
            "SlideExit",
            "Mantle1m",
            "Mantle2m",
        };

        private static AssetBundle _controllerBundle;
        private static RuntimeAnimatorController _controller;
        private static bool _loadAttempted;
        private static bool _missingControllerLogged;
        private static bool _controllerDiagnosticsLogged;
        private static bool _controllerContractLogged;
        private static bool _controllerRejected;
        private static bool _controllerRejectedLogged;
        private static string _rejectedControllerName;
        private static string _controllerRejectReason;

        internal static void Prewarm()
        {
            ResolveController();
        }

        /// <summary>
        /// F-ESCAPE-3: <see cref="ResolveController"/> used to latch <c>_loadAttempted</c> on its
        /// first failure and never look again, so one unlucky moment (a load-order race, another
        /// loader holding the bundle, a transient file error) disabled the authored slide and
        /// mantle clips for the rest of the session. Called once per round so the resolver gets a
        /// fresh attempt. The "already told you" log latches stay set: the player-facing signal is
        /// the HUD tip in <c>PanicSlidePatch</c>, and the ability itself now runs regardless.
        /// </summary>
        internal static void AllowControllerLoadRetry()
        {
            if (_controller != null)
                return;

            _loadAttempted = false;
        }

        internal static RuntimeAnimatorController ResolveController()
        {
            if (_controller != null)
                return _controller;
            if (_loadAttempted)
            {
                LogMissingControllerOnce();
                return null;
            }

            _loadAttempted = true;

            RuntimeAnimatorController recovered = FindControllerInKnownLoadedBundle();
            if (TryAcceptLoadedController(recovered, "loaded-bundle-scan"))
            {
                Plugin.Log?.LogInfo("[Panic Slide] Movement player animator controller prewarmed from its already-loaded bundle.");
                return _controller;
            }

            string pluginDir = GetPluginDirectory();
            if (string.IsNullOrEmpty(pluginDir))
            {
                LogMissingControllerOnce();
                return null;
            }

            string[] bundleFiles =
            {
                PrimaryBundleFile,
                SecondaryBundleFile,
            };

            for (int i = 0; i < bundleFiles.Length; i++)
            {
                string path = Path.Combine(pluginDir, bundleFiles[i]);
                if (!File.Exists(path))
                    continue;

                try
                {
                    _controllerBundle = AssetBundle.LoadFromFile(path);
                    if (_controllerBundle == null)
                    {
                        recovered = FindControllerInKnownLoadedBundle();
                        if (TryAcceptLoadedController(recovered, "loaded-bundle-scan-after-null-load"))
                        {
                            Plugin.Log?.LogInfo("[Panic Slide] Movement player animator controller recovered after AssetBundle.LoadFromFile returned null.");
                            return _controller;
                        }

                        Plugin.Log?.LogWarning($"[Panic Slide] Movement player animation bundle failed to load: {path}");
                        continue;
                    }

                    RuntimeAnimatorController loaded = LoadControllerFromBundle(_controllerBundle);
                    if (TryAcceptLoadedController(loaded, Path.GetFileName(path)))
                    {
                        Plugin.Log?.LogInfo($"[Panic Slide] Loaded movement player animator controller '{_controller.name}' from {Path.GetFileName(path)}.");
                        return _controller;
                    }

                    if (loaded == null)
                        Plugin.Log?.LogWarning($"[Panic Slide] Movement player animation bundle contains no expected controller: {path}");
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[Panic Slide] Movement player animation bundle load failed '{path}': {ex.Message}");
                    recovered = FindControllerInKnownLoadedBundle();
                    if (TryAcceptLoadedController(recovered, "loaded-bundle-scan-after-exception"))
                    {
                        Plugin.Log?.LogInfo("[Panic Slide] Movement player animator controller recovered from loaded bundles after load exception.");
                        return _controller;
                    }
                }
            }

            LogMissingControllerOnce();
            return null;
        }

        internal static void LogAnimatorControllerDiagnosticsOnce(Animator animator, RuntimeAnimatorController controller)
        {
            if (_controllerDiagnosticsLogged || animator == null || controller == null)
                return;

            _controllerDiagnosticsLogged = true;
            try
            {
                var layerNames = new List<string>();
                int layerCount = Mathf.Max(0, animator.layerCount);
                for (int i = 0; i < layerCount; i++)
                    layerNames.Add(i + ":" + animator.GetLayerName(i));

                var parameterNames = new List<string>();
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    parameterNames.Add(parameter.name + ":" + parameter.type);
                }

                Plugin.Log?.LogInfo(
                    "[Panic Slide] Movement player animator diagnostics: " +
                    $"controller='{controller.name}', layerCount={layerCount}, " +
                    $"layers=[{string.Join(", ", layerNames.ToArray())}], " +
                    $"parameters=[{string.Join(", ", parameterNames.ToArray())}].");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[Panic Slide] Movement player animator diagnostics failed: {ex.Message}");
            }
        }

        internal static bool ValidateAnimatorControllerContract(Animator animator, RuntimeAnimatorController controller, string context)
        {
            if (animator == null || controller == null)
                return false;

            bool valid = true;
            var missing = new List<string>();
            var parameterTypes = new Dictionary<string, AnimatorControllerParameterType>();
            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    parameterTypes[parameter.name] = parameter.type;
                }

                for (int i = 0; i < RequiredParameters.Length; i++)
                {
                    RequiredAnimatorParameter required = RequiredParameters[i];
                    if (!parameterTypes.TryGetValue(required.Name, out AnimatorControllerParameterType actual))
                    {
                        missing.Add(required.Name + ":missing");
                        valid = false;
                        continue;
                    }

                    if (actual != required.Type)
                    {
                        missing.Add(required.Name + ":expected " + required.Type + " got " + actual);
                        valid = false;
                    }
                }

                RequireLayers(animator, RequiredVanillaLayerNames, missing, ref valid);
                RequireMovementLayersAndStates(animator, missing, ref valid);

                if (!_controllerContractLogged || !valid)
                {
                    _controllerContractLogged = true;
                    string level = valid ? "OK" : "INVALID";
                    string details = missing.Count == 0 ? "all required parameters/layers/states present" : string.Join(", ", missing.ToArray());
                    Plugin.Log?.LogInfo(
                        $"[Panic Slide] Movement player animator contract v{MovementControllerContractVersion} {level}: " +
                        $"controller='{controller.name}', context='{context}', {details}.");
                }
            }
            catch (Exception ex)
            {
                valid = false;
                Plugin.Log?.LogWarning(
                    $"[Panic Slide] Movement player animator contract validation failed in {context}: {ex.Message}");
            }

            return valid;
        }

        private static void RequireMovementLayersAndStates(Animator animator, List<string> missing, ref bool valid)
        {
            for (int i = 0; i < RequiredMovementLayerNames.Length; i++)
            {
                string requiredLayer = RequiredMovementLayerNames[i];
                int layerIndex = FindLayerIndex(animator, requiredLayer);
                if (layerIndex < 0)
                {
                    missing.Add("layer:" + requiredLayer + ":missing");
                    valid = false;
                    continue;
                }

                for (int state = 0; state < RequiredMovementStateNames.Length; state++)
                {
                    string requiredState = RequiredMovementStateNames[state];
                    if (HasState(animator, layerIndex, requiredLayer, requiredState))
                        continue;

                    missing.Add("layer:" + requiredLayer + ":state:" + requiredState + ":missing");
                    valid = false;
                }
            }
        }

        private static int FindLayerIndex(Animator animator, string layerName)
        {
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                if (string.Equals(animator.GetLayerName(layer), layerName, StringComparison.Ordinal))
                    return layer;
            }

            return -1;
        }

        private static bool HasState(Animator animator, int layerIndex, string layerName, string stateName)
        {
            int fullPathHash = Animator.StringToHash(layerName + "." + stateName);
            if (animator.HasState(layerIndex, fullPathHash))
                return true;

            int shortNameHash = Animator.StringToHash(stateName);
            return animator.HasState(layerIndex, shortNameHash);
        }

        private static void RequireLayers(Animator animator, string[] requiredLayers, List<string> missing, ref bool valid)
        {
            for (int i = 0; i < requiredLayers.Length; i++)
            {
                string requiredLayer = requiredLayers[i];
                bool found = false;
                for (int layer = 0; layer < animator.layerCount; layer++)
                {
                    if (string.Equals(animator.GetLayerName(layer), requiredLayer, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    missing.Add("layer:" + requiredLayer + ":missing");
                    valid = false;
                }
            }
        }

        private static bool TryAcceptLoadedController(RuntimeAnimatorController controller, string sourceLabel)
        {
            if (controller == null)
                return false;

            if (!PreflightControllerContract(controller, sourceLabel))
            {
                if (_controllerBundle != null)
                {
                    try { _controllerBundle.Unload(unloadAllLoadedObjects: false); } catch { }
                    _controllerBundle = null;
                }
                return false;
            }

            _controller = controller;
            _controllerRejected = false;
            _rejectedControllerName = null;
            _controllerRejectReason = null;
            return true;
        }

        private static bool PreflightControllerContract(RuntimeAnimatorController controller, string sourceLabel)
        {
            GameObject probe = null;
            try
            {
                probe = new GameObject("Y4NGZ_MovementAnimatorPreflight")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Animator animator = probe.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                LogAnimatorControllerDiagnosticsOnce(animator, controller);
                bool valid = ValidateAnimatorControllerContract(animator, controller, "bundle-preflight:" + sourceLabel);
                if (!valid)
                {
                    RejectLoadedController(
                        controller,
                        "controller contract v" + MovementControllerContractVersion +
                        " invalid during bundle preflight; the controller must clone the vanilla metarig controller and append all movement layers, including first-person arms and local-body states");
                }
                return valid;
            }
            catch (Exception ex)
            {
                RejectLoadedController(controller, "controller preflight exception: " + ex.Message);
                return false;
            }
            finally
            {
                if (probe != null)
                    UnityEngine.Object.Destroy(probe);
            }
        }

        private static void RejectLoadedController(RuntimeAnimatorController controller, string reason)
        {
            _controllerRejected = true;
            _rejectedControllerName = controller != null ? controller.name : "<null>";
            _controllerRejectReason = reason;

            if (_controllerRejectedLogged)
                return;

            _controllerRejectedLogged = true;
            Plugin.Log?.LogWarning(
                "[Panic Slide] Authored movement animator controller rejected before live player apply: " +
                $"controller='{_rejectedControllerName}', reason='{_controllerRejectReason}'.");
        }

        private static RuntimeAnimatorController FindControllerInKnownLoadedBundle()
        {
            try
            {
                foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (loaded == null || !IsMovementControllerBundle(loaded))
                        continue;

                    RuntimeAnimatorController controller = LoadControllerFromBundle(loaded);
                    if (controller != null)
                        return controller;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[Panic Slide] Known movement-bundle lookup failed: {ex.Message}");
            }

            return null;
        }

        private static bool IsMovementControllerBundle(AssetBundle bundle)
        {
            string name = bundle != null ? bundle.name : null;
            return !string.IsNullOrEmpty(name)
                && name.IndexOf("y4ngz-movement-playeranimations", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static RuntimeAnimatorController LoadControllerFromBundle(AssetBundle bundle)
        {
            if (bundle == null)
                return null;

            try
            {
                RuntimeAnimatorController controller = bundle.LoadAsset<RuntimeAnimatorController>(PrimaryControllerAsset);
                if (controller != null)
                    return controller;

                controller = bundle.LoadAsset<RuntimeAnimatorController>(SecondaryControllerAsset);
                if (controller != null)
                    return controller;

                RuntimeAnimatorController[] controllers = bundle.LoadAllAssets<RuntimeAnimatorController>();
                for (int i = 0; i < controllers.Length; i++)
                {
                    RuntimeAnimatorController candidate = controllers[i];
                    if (candidate == null)
                        continue;

                    if (string.Equals(candidate.name, PrimaryControllerAsset, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidate.name, SecondaryControllerAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string GetPluginDirectory()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }

        private static void LogMissingControllerOnce()
        {
            if (_controller != null || _missingControllerLogged)
                return;

            _missingControllerLogged = true;
            if (_controllerRejected)
            {
                Plugin.Log?.LogInfo(
                    "[Panic Slide] Movement player animation controller unavailable because the bundled controller was rejected: " +
                    $"controller='{(_rejectedControllerName ?? "<unknown>")}', reason='{(_controllerRejectReason ?? "unknown")}'.");
                return;
            }

            string pluginDir = GetPluginDirectory() ?? "<unknown>";
            Plugin.Log?.LogInfo(
                "[Panic Slide] Movement player animation controller not loaded. " +
                $"Checked '{PrimaryBundleFile}' and '{SecondaryBundleFile}' beside the plugin in '{pluginDir}'.");
        }

        private struct RequiredAnimatorParameter
        {
            internal readonly string Name;
            internal readonly AnimatorControllerParameterType Type;

            internal RequiredAnimatorParameter(string name, AnimatorControllerParameterType type)
            {
                Name = name;
                Type = type;
            }
        }
    }
}
