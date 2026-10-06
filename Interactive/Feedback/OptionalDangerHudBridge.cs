using System;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZUpgrades.Interactive.Feedback
{
    /// <summary>
    /// Gameplay-owned feed into Y4NGZUI's danger renderers. Upgrades keeps damage-source
    /// classification; every presentation call is name-only and becomes a no-op without Y4NGZUI.
    /// </summary>
    internal static class OptionalDangerHudBridge
    {
        private const string PlayerDangerTypeName = "Y4NGZUI.PlayerDangerHud, Y4NGZUI";
        private const string DirectionalDamageTypeName = "Y4NGZUI.DirectionalDamageHud, Y4NGZUI";

        private static Type _playerDangerType;
        private static Type _directionalDamageType;
        private static MethodInfo _updatePlayerState;
        private static MethodInfo _pulseDamage;
        private static MethodInfo _destroyPlayerDanger;
        private static MethodInfo _setPlayerDangerVisible;
        private static MethodInfo _showDirectionalDamage;
        private static MethodInfo _destroyDirectionalDamage;
        private static MethodInfo _setDirectionalDamageVisible;

        internal static void UpdatePlayerState(PlayerControllerB player)
        {
            Resolve();
            Invoke(_updatePlayerState, player);
        }

        internal static void PulseDamage(int damageNumber)
        {
            Resolve();
            Invoke(_pulseDamage, damageNumber);
        }

        internal static void ShowDirectionalDamage(
            PlayerControllerB player,
            Vector3 sourcePosition,
            int damageNumber)
        {
            Resolve();
            Invoke(_showDirectionalDamage, player, sourcePosition, damageNumber);
        }

        internal static void SetPresentationVisible(bool visible)
        {
            Resolve();
            Invoke(_setPlayerDangerVisible, visible);
            Invoke(_setDirectionalDamageVisible, visible);
        }

        internal static void DestroyInstances()
        {
            Resolve();
            Invoke(_destroyDirectionalDamage);
            Invoke(_destroyPlayerDanger);
        }

        internal static void Shutdown()
        {
            DestroyInstances();
            _playerDangerType = null;
            _directionalDamageType = null;
            _updatePlayerState = null;
            _pulseDamage = null;
            _destroyPlayerDanger = null;
            _setPlayerDangerVisible = null;
            _showDirectionalDamage = null;
            _destroyDirectionalDamage = null;
            _setDirectionalDamageVisible = null;
        }

        private static void Resolve()
        {
            if ((_playerDangerType != null && _directionalDamageType != null)
                || !OptionalPluginCapabilities.Y4NGZUi)
            {
                return;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public;
            _playerDangerType = Type.GetType(PlayerDangerTypeName, throwOnError: false);
            _directionalDamageType = Type.GetType(DirectionalDamageTypeName, throwOnError: false);
            _updatePlayerState = _playerDangerType?.GetMethod("UpdatePlayerState", flags);
            _pulseDamage = _playerDangerType?.GetMethod("PulseDamage", flags);
            _destroyPlayerDanger = _playerDangerType?.GetMethod("DestroyInstance", flags);
            _setPlayerDangerVisible =
                _playerDangerType?.GetMethod("SetPresentationVisible", flags);
            _showDirectionalDamage =
                _directionalDamageType?.GetMethod("ShowFromWorldPosition", flags);
            _destroyDirectionalDamage =
                _directionalDamageType?.GetMethod("DestroyInstance", flags);
            _setDirectionalDamageVisible =
                _directionalDamageType?.GetMethod("SetPresentationVisible", flags);
        }

        private static void Invoke(MethodInfo method, params object[] arguments)
        {
            if (method == null)
                return;
            try { method.Invoke(null, arguments); }
            catch { }
        }
    }
}
