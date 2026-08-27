using System;
using System.Collections;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

#pragma warning disable Harmony003

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ShoulderCheckPatch
    {
        private const float IMPACT_LOCKOUT_SECONDS = 0.25f;
        private const int DOOR_MASK = ~0;

        private static float _sprintSeconds;
        private static bool _chargeReady;
        private static float _lastImpactTime;
        private static DoorLock _lastDoor;
        private static float _lastDoorTime;

        private static GameObject _windRoot;
        private static Image[] _windLines;
        private static Sprite _pixelSprite;

        private static bool _doorReflectionResolved;
        private static MethodInfo _openDoorAsEnemy;
        private static FieldInfo _doorOpenedField;
        private static PropertyInfo _doorOpenedProperty;

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            if (!CanMaintainSprintCharge(__instance))
            {
                ResetCharge();
                UpdateVignette(false);
                return;
            }

            _sprintSeconds += Time.deltaTime;
            _chargeReady = _sprintSeconds >= ShoulderCheckUpgrade.GetArmSeconds();
            UpdateVignette(_chargeReady);

            if (_chargeReady)
                TrySlamDoor(__instance);
        }

        [HarmonyPatch(typeof(EnemyAI), "OnCollideWithPlayer")]
        [HarmonyPrefix]
        private static bool PreEnemyCollideWithPlayer(EnemyAI __instance, Collider other)
        {
            if (__instance == null || other == null || !_chargeReady)
                return true;
            if (__instance.isEnemyDead)
                return true;

            PlayerControllerB player = other.GetComponent<PlayerControllerB>() ?? other.GetComponentInParent<PlayerControllerB>();
            if (!IsLocalPlayer(player))
                return true;
            if (Time.time < _lastImpactTime + IMPACT_LOCKOUT_SECONDS)
                return true;

            ApplyEnemyImpact(__instance, player);
            ConsumeCharge();
            return false;
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            ResetCharge();
            HideVignette();
            _lastImpactTime = 0f;
            _lastDoor = null;
            _lastDoorTime = 0f;
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            ResetCharge();
            HideVignette();
        }

        private static bool CanMaintainSprintCharge(PlayerControllerB player)
        {
            if (player == null || player.isPlayerDead || !ShoulderCheckUpgrade.IsUnlocked())
                return false;
            if (!player.isSprinting || player.isCrouching)
                return false;
            if (player.sprintMeter < ShoulderCheckUpgrade.READY_MIN_SPRINT_METER)
                return false;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return false;
            if (player.isTypingChat || player.inTerminalMenu || player.inSpecialInteractAnimation)
                return false;
            return true;
        }

        private static void TrySlamDoor(PlayerControllerB player)
        {
            Transform camera = player.gameplayCamera != null ? player.gameplayCamera.transform : player.transform;
            Vector3 forward = Vector3.ProjectOnPlane(camera.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f)
                forward = player.transform.forward;
            forward.Normalize();

            Ray ray = new Ray(camera.position + Vector3.down * 0.1f, forward);
            RaycastHit hit;
            if (!Physics.SphereCast(
                    ray,
                    ShoulderCheckUpgrade.DOOR_CHECK_RADIUS,
                    out hit,
                    ShoulderCheckUpgrade.DOOR_CHECK_RANGE,
                    DOOR_MASK,
                    QueryTriggerInteraction.Collide))
            {
                return;
            }

            DoorLock door = hit.collider != null ? hit.collider.GetComponentInParent<DoorLock>() : null;
            if (!IsUsableDoor(door))
                return;
            if (door == _lastDoor && Time.time < _lastDoorTime + 0.7f)
                return;

            ResolveDoorReflection();
            if (_openDoorAsEnemy == null)
                return;

            try
            {
                _openDoorAsEnemy.Invoke(door, null);
                _lastDoor = door;
                _lastDoorTime = Time.time;
                HUDManager.Instance?.ShakeCamera(ScreenShakeType.Small);
                ConsumeCharge();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"[Shoulder Check] Door slam skipped: {ex.Message}");
            }
        }

        private static bool IsUsableDoor(DoorLock door)
        {
            return door != null && !door.isLocked && !DoorIsOpen(door);
        }

        private static bool DoorIsOpen(DoorLock door)
        {
            if (door == null)
                return false;

            ResolveDoorReflection();
            try
            {
                if (_doorOpenedField != null && _doorOpenedField.GetValue(door) is bool fieldValue)
                    return fieldValue;
                if (_doorOpenedProperty != null && _doorOpenedProperty.GetValue(door, null) is bool propertyValue)
                    return propertyValue;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"[Shoulder Check] Door state check skipped: {ex.Message}");
            }

            return false;
        }

        private static void ResolveDoorReflection()
        {
            if (_doorReflectionResolved)
                return;

            _doorReflectionResolved = true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _openDoorAsEnemy = typeof(DoorLock).GetMethod("OpenDoorAsEnemyServerRpc", flags);
            _doorOpenedField = typeof(DoorLock).GetField("isDoorOpened", flags)
                ?? typeof(DoorLock).GetField("isDoorOpen", flags);
            _doorOpenedProperty = typeof(DoorLock).GetProperty("isDoorOpened", flags)
                ?? typeof(DoorLock).GetProperty("isDoorOpen", flags);
        }

        private static void ApplyEnemyImpact(EnemyAI enemy, PlayerControllerB player)
        {
            _lastImpactTime = Time.time;

            Vector3 direction = enemy.transform.position - player.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f)
            {
                Transform camera = player.gameplayCamera != null ? player.gameplayCamera.transform : player.transform;
                direction = camera.forward;
                direction.y = 0f;
            }
            direction.Normalize();

            try
            {
                enemy.HitEnemyOnLocalClient(ShoulderCheckUpgrade.GetDamage(), direction);
                enemy.SetEnemyStunned(true, ShoulderCheckUpgrade.IMPACT_STUN_SECONDS, player);
                player.StartCoroutine(ApplyEnemyKnockback(enemy, direction, ShoulderCheckUpgrade.GetKnockbackMeters()));
                HUDManager.Instance?.ShakeCamera(ScreenShakeType.Small);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"[Shoulder Check] Enemy impact skipped: {ex.Message}");
            }
        }

        private static IEnumerator ApplyEnemyKnockback(EnemyAI enemy, Vector3 direction, float meters)
        {
            if (enemy == null || direction.sqrMagnitude < 0.01f || meters <= 0f)
                yield break;

            Vector3 start = enemy.transform.position;
            Vector3 end = start + direction.normalized * meters;
            float duration = 0.24f;
            float elapsed = 0f;

            while (elapsed < duration && enemy != null && !enemy.isEnemyDead)
            {
                float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
                Vector3 next = Vector3.Lerp(start, end, t);
                if (enemy.agent != null && enemy.agent.enabled)
                    enemy.agent.Warp(next);
                else
                    enemy.transform.position = next;

                elapsed += Time.deltaTime;
                yield return null;
            }
        }

        private static void ConsumeCharge()
        {
            ResetCharge();
            UpdateVignette(false);
        }

        private static void ResetCharge()
        {
            _sprintSeconds = 0f;
            _chargeReady = false;
        }

        private static void UpdateVignette(bool ready)
        {
            if (!ready)
            {
                HideVignette();
                return;
            }

            EnsureVignette();
            if (_windRoot == null || _windLines == null)
                return;

            _windRoot.SetActive(true);
            float tierBoost = ShoulderCheckUpgrade.GetTier() >= 3 ? 0.025f : 0f;
            float pulse = Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6.5f)) * 0.025f;
            for (int i = 0; i < _windLines.Length; i++)
            {
                Image line = _windLines[i];
                if (line == null)
                    continue;

                float alpha = 0.055f + tierBoost + pulse + (i % 3) * 0.012f;
                line.color = new Color(0.72f, 0.94f, 1f, Mathf.Clamp(alpha, 0.035f, 0.13f));
            }
        }

        private static void EnsureVignette()
        {
            if (_windRoot != null)
                return;
            if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null)
                return;

            Canvas canvas = HUDManager.Instance.playerScreenTexture.canvas;
            if (canvas == null)
                return;

            _windRoot = new GameObject("Y4NGZ_ShoulderCheckWindEdges");
            _windRoot.transform.SetParent(canvas.transform, worldPositionStays: false);
            RectTransform rect = _windRoot.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            _windRoot.SetActive(false);

            _windLines = new Image[10];
            CreateWindLine(0, "LeftTop", new Vector2(0f, 0.72f), new Vector2(0f, 0.72f), new Vector2(16f, 0f), 72f, 2.5f, -8f);
            CreateWindLine(1, "LeftMidA", new Vector2(0f, 0.58f), new Vector2(0f, 0.58f), new Vector2(10f, 0f), 58f, 2.2f, -4f);
            CreateWindLine(2, "LeftMidB", new Vector2(0f, 0.43f), new Vector2(0f, 0.43f), new Vector2(20f, 0f), 64f, 2.2f, 5f);
            CreateWindLine(3, "LeftLow", new Vector2(0f, 0.29f), new Vector2(0f, 0.29f), new Vector2(12f, 0f), 76f, 2.5f, 9f);
            CreateWindLine(4, "LeftFarLow", new Vector2(0f, 0.18f), new Vector2(0f, 0.18f), new Vector2(24f, 0f), 48f, 2f, 4f);

            CreateWindLine(5, "RightTop", new Vector2(1f, 0.72f), new Vector2(1f, 0.72f), new Vector2(-16f, 0f), 72f, 2.5f, 8f);
            CreateWindLine(6, "RightMidA", new Vector2(1f, 0.58f), new Vector2(1f, 0.58f), new Vector2(-10f, 0f), 58f, 2.2f, 4f);
            CreateWindLine(7, "RightMidB", new Vector2(1f, 0.43f), new Vector2(1f, 0.43f), new Vector2(-20f, 0f), 64f, 2.2f, -5f);
            CreateWindLine(8, "RightLow", new Vector2(1f, 0.29f), new Vector2(1f, 0.29f), new Vector2(-12f, 0f), 76f, 2.5f, -9f);
            CreateWindLine(9, "RightFarLow", new Vector2(1f, 0.18f), new Vector2(1f, 0.18f), new Vector2(-24f, 0f), 48f, 2f, -4f);
        }

        private static void CreateWindLine(
            int index,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 anchoredPosition,
            float width,
            float height,
            float rotation)
        {
            if (_windRoot == null || index < 0 || index >= _windLines.Length)
                return;

            GameObject go = new GameObject(name);
            go.transform.SetParent(_windRoot.transform, worldPositionStays: false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = anchorMin.x < 0.5f ? new Vector2(0f, 0.5f) : new Vector2(1f, 0.5f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = new Vector2(width, height);
            rt.localRotation = Quaternion.Euler(0f, 0f, rotation);

            Image image = go.AddComponent<Image>();
            image.sprite = GetPixelSprite();
            image.raycastTarget = false;
            image.color = new Color(0.72f, 0.94f, 1f, 0.06f);
            _windLines[index] = image;
        }

        private static void HideVignette()
        {
            if (_windRoot != null)
                _windRoot.SetActive(false);
        }

        private static Sprite GetPixelSprite()
        {
            if (_pixelSprite != null)
                return _pixelSprite;

            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false, true);
            _pixelSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
            _pixelSprite.hideFlags = HideFlags.HideAndDontSave;
            return _pixelSprite;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }
    }
}

#pragma warning restore Harmony003
