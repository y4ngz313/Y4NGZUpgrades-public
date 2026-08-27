using Unity.Netcode;
using UnityEngine;

namespace Y4NGZUpgrades.Lucky8
{
    internal sealed class Lucky8ClaimCapsule : MonoBehaviour
    {
        private NetworkObject networkObject;
        internal Lucky8RewardDefinition Reward { get; private set; }

        private void Start()
        {
            networkObject = GetComponent<NetworkObject>();
            Lucky8Manager.RegisterCapsule(this);
        }

        internal void SetReward(Lucky8RewardDefinition reward)
        {
            Reward = reward;
            if (reward == null) return;
            var block = new MaterialPropertyBlock();
            Color color;
            if (!Lucky8RewardPresentation.TryGetColor(reward, out color))
            {
                color = reward.Category == Lucky8RewardCategory.Suit
                    ? new Color(0.1f, 0.75f, 0.85f)
                    : reward.Category == Lucky8RewardCategory.Emote
                        ? new Color(0.36f, 0.62f, 1f)
                        : new Color(0.78f, 0.25f, 0.9f);
            }
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            block.SetColor("_EmissiveColor", color * 0.8f);
            block.SetColor("_EmissionColor", color * 0.8f);
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.enabled)
                    renderer.SetPropertyBlock(block);
            }
        }

        internal void TryClaimLocal(GrabbableObject item)
        {
            if (item == null || item.playerHeldBy == null || item.playerHeldBy != StartOfRound.Instance?.localPlayerController || Reward == null)
                return;
            if (!Lucky8RewardUnlockApi.TryClaim(Reward, out string reason))
            {
                HUDManager.Instance?.DisplayTip("LUCKY-8", reason == "already-owned" ? "You already own this reward. Give the capsule to another employee." : "The capsule could not be claimed.", true);
                return;
            }

            HUDManager.Instance?.DisplayTip("LUCKY-8 PRIZE", Reward.DisplayName + " unlocked.", false);
            if (networkObject != null)
                Lucky8Manager.RequestConsumeCapsule(networkObject.NetworkObjectId);
        }

        private void OnDestroy()
        {
            Lucky8Manager.UnregisterCapsule(this);
        }
    }
}
