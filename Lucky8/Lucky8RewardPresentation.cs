using UnityEngine;

namespace Y4NGZUpgrades.Lucky8
{
    internal static class Lucky8RewardPresentation
    {
        internal static uint PackColor(Color color)
        {
            Color32 packed = color;
            return (uint)(packed.r << 24 | packed.g << 16 | packed.b << 8 | packed.a);
        }

        internal static bool TryGetColor(Lucky8RewardDefinition reward, out Color color)
        {
            color = Color.white;
            if (reward?.HasPresentationColor != true) return false;
            uint packed = reward.PresentationColorRgba;
            color = new Color32(
                (byte)(packed >> 24),
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed);
            return true;
        }
    }
}
