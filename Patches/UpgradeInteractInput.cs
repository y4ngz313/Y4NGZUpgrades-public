using UnityEngine.InputSystem;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Reads Lethal Company's live Interact action so hold interactions follow keyboard, gamepad,
    /// and user rebinding instead of assuming the authored E key.
    /// </summary>
    internal static class UpgradeInteractInput
    {
        internal static bool IsHeld()
        {
            InputAction action = ResolveAction();
            if (action != null)
                return action.IsPressed();

            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard.eKey.isPressed;
        }

        internal static string DisplayLabel()
        {
            InputAction action = ResolveAction();
            string label = action?.GetBindingDisplayString();
            if (string.IsNullOrWhiteSpace(label))
                return "E";

            return label.Trim().ToUpperInvariant();
        }

        private static InputAction ResolveAction()
        {
            InputActionAsset actions = IngamePlayerSettings.Instance?.playerInput?.actions;
            return actions?.FindAction("Interact", throwIfNotFound: false);
        }
    }
}
