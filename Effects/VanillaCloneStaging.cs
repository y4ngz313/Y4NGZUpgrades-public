using System;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    /// <summary>
    /// Shared staging for runtime clones of vanilla VFX subtrees. Clones are built under a
    /// deactivated holder so nothing on them runs Awake before the netcode components are stripped,
    /// which is what keeps a cloned enemy or explosion part from registering with the NetworkManager.
    /// The caller positions, scales and activates what it gets back.
    /// </summary>
    internal static class VanillaCloneStaging
    {
        private static Transform _nursery;

        internal static GameObject CloneInactive(GameObject template, Transform parent, string name)
        {
            if (template == null || parent == null)
                return null;

            Transform nursery = EnsureNursery();
            if (nursery == null)
                return null;

            GameObject clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(template, nursery, false);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning(
                    "[VanillaClone] cloning '" + template.name + "' failed: " + exception.Message);
                return null;
            }

            clone.name = name;
            clone.SetActive(false);
            StripNetworking(clone);

            clone.transform.SetParent(parent, false);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            return clone;
        }

        /// <summary>
        /// Vanilla VFX subtrees are authored as plain particles, lights and audio, but a future patch
        /// or another mod's replacement prefab could park a NetworkBehaviour under one. Removing them
        /// while the clone is still inactive keeps the spawn warnings out of the log.
        /// </summary>
        private static void StripNetworking(GameObject clone)
        {
            NetworkBehaviour[] behaviours = clone.GetComponentsInChildren<NetworkBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null)
                    UnityEngine.Object.DestroyImmediate(behaviours[i]);
            }

            NetworkObject[] netObjects = clone.GetComponentsInChildren<NetworkObject>(true);
            for (int i = 0; i < netObjects.Length; i++)
            {
                if (netObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(netObjects[i]);
            }
        }

        private static Transform EnsureNursery()
        {
            if (_nursery != null)
                return _nursery;

            GameObject holder = new GameObject("Y4NGZ_VanillaCloneNursery");
            holder.SetActive(false);
            // DontSave keeps the staging holder alive across scene loads without a DontDestroyOnLoad
            // call on an already-deactivated object.
            holder.hideFlags = HideFlags.HideAndDontSave;
            _nursery = holder.transform;
            return _nursery;
        }
    }
}
