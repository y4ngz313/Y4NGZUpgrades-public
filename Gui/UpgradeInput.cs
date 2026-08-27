using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Y4NGZUpgrades.Gui
{
    /// <summary>
    /// One read/display surface for InputUtils actions and the vanilla action asset. Gameplay code
    /// never needs to know which physical key currently owns a command.
    /// </summary>
    internal static class UpgradeInput
    {
        internal static bool WasPressed(InputAction action)
        {
            return action != null && action.WasPressedThisFrame();
        }

        internal static bool IsPressed(InputAction action)
        {
            return action != null && action.IsPressed();
        }

        internal static string DisplayLabel(InputAction action, string fallback)
        {
            if (action == null)
                return fallback ?? string.Empty;

            try
            {
                int index = FindKeyboardMouseBindingIndex(action);
                string label = index >= 0
                    ? action.GetBindingDisplayString(index)
                    : action.GetBindingDisplayString();
                return string.IsNullOrWhiteSpace(label)
                    ? fallback ?? string.Empty
                    : label.Trim().ToUpperInvariant();
            }
            catch
            {
                return fallback ?? string.Empty;
            }
        }

        internal static string DisplayPair(
            InputAction first,
            string firstFallback,
            InputAction second,
            string secondFallback)
        {
            return DisplayLabel(first, firstFallback) + "/" + DisplayLabel(second, secondFallback);
        }

        internal static string EffectivePath(InputAction action)
        {
            int index = FindKeyboardMouseBindingIndex(action);
            return action != null && index >= 0 ? action.bindings[index].effectivePath ?? string.Empty : string.Empty;
        }

        internal static int FindKeyboardMouseBindingIndex(InputAction action)
        {
            if (action == null)
                return -1;

            for (int index = 0; index < action.bindings.Count; index++)
            {
                string groups = action.bindings[index].groups;
                if (!string.IsNullOrWhiteSpace(groups)
                    && groups.IndexOf("KeyboardAndMouse", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return index;
                }
            }

            return action.bindings.Count > 0 ? 0 : -1;
        }

        internal static InputAction ResolveVanillaAction(string actionName)
        {
            if (string.IsNullOrWhiteSpace(actionName))
                return null;

            InputActionAsset actions = IngamePlayerSettings.Instance?.playerInput?.actions;
            return actions?.FindAction(actionName, throwIfNotFound: false);
        }

        internal static Vector2 ReadVanillaVector2(string actionName)
        {
            InputAction action = ResolveVanillaAction(actionName);
            return action != null ? action.ReadValue<Vector2>() : Vector2.zero;
        }

        internal static string VanillaDisplayLabel(string actionName, string fallback)
        {
            return DisplayLabel(ResolveVanillaAction(actionName), fallback);
        }
    }
}