using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZInteractions.InteractionAnimationApi;

namespace Y4NGZUpgrades.Lucky8
{
    internal static class Lucky8ButtonPressAnimation
    {
        private const string PackId = "y4ngz.upgrades.lucky8";
        private const string InteractionId = "y4ngz.lucky8.button_press";
        private const string ManifestFile = "y4ngz-lucky8-button-press.manifest.json";
        private static bool registered;
        private static bool playing;

        internal static void Initialize()
        {
            try
            {
                string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string path = Path.Combine(root, ManifestFile);
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path);
                var pack = new InteractionAnimationPackDefinition
                {
                    PackId = PackId,
                    Version = Plugin.Version,
                    AssetRootPath = root,
                    Interactions = new[]
                    {
                        new InteractionAnimationDefinition
                        {
                            InteractionId = InteractionId,
                            PresentationKind = InteractionAnimationPresentationKind.BodyWorld,
                            ManifestJson = json,
                        },
                    },
                };
                registered = LCInteractionAnimationAPI.TryRegisterInteractionPack(pack, out string reason);
                if (!registered)
                    Plugin.Log?.LogWarning("[LUCKY-8] Button animation pack registration skipped: " + reason);
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning("[LUCKY-8] Button animation initialization failed: " + e.Message);
            }
        }

        internal static void Play(PlayerControllerB player)
        {
            if (player == null || playing) return;
            Y4NGZPersistentRunner.Run(PlayRoutine(player));
        }

        private static IEnumerator PlayRoutine(PlayerControllerB player)
        {
            playing = true;
            bool restoreMove = player.disableMoveInput;
            bool restoreLook = player.disableLookInput;
            bool restoreInteract = player.disableInteract;
            List<InputAction> disabledActions = new List<InputAction>();
            InteractionAnimationHandle handle = InteractionAnimationHandle.Empty;
            try
            {
                disabledActions = DisableItemActions();
                player.disableMoveInput = true;
                player.disableLookInput = true;
                player.disableInteract = true;

                if (registered)
                {
                    var request = new InteractionAnimationRequest
                    {
                        Player = player,
                        PackId = PackId,
                        InteractionId = InteractionId,
                    };
                    if (!LCInteractionAnimationAPI.TryStartInteraction(request, out handle, out string reason))
                        Plugin.Log?.LogDebug("[LUCKY-8] Button animation unavailable for this press: " + reason);
                }

                float until = Time.unscaledTime + Lucky8Manager.EffectivePressDuration;
                while (Time.unscaledTime < until && player != null && !player.isPlayerDead)
                    yield return null;
            }
            finally
            {
                if (handle.IsValid && LCInteractionAnimationAPI.IsInteractionActive(handle))
                    LCInteractionAnimationAPI.TryStopInteraction(handle, InteractionAnimationStopReason.NaturalEnd);
                if (player != null)
                {
                    player.disableMoveInput = restoreMove;
                    player.disableLookInput = restoreLook;
                    player.disableInteract = restoreInteract;
                }
                for (int i = 0; i < disabledActions.Count; i++)
                    disabledActions[i]?.Enable();
                playing = false;
            }
        }

        private static List<InputAction> DisableItemActions()
        {
            var disabled = new List<InputAction>(6);
            InputActionAsset actions = IngamePlayerSettings.Instance?.playerInput?.actions;
            if (actions == null) return disabled;
            string[] names =
            {
                "ActivateItem",
                "ItemSecondaryUse",
                "ItemTertiaryUse",
                "SwitchItem",
                "UseUtilitySlot",
                "DiscardHeldObject",
            };
            for (int i = 0; i < names.Length; i++)
            {
                InputAction action = actions.FindAction(names[i], false);
                if (action == null || !action.enabled) continue;
                action.Disable();
                disabled.Add(action);
            }
            return disabled;
        }
    }
}
