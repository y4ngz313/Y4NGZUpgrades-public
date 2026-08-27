using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades
{
    [HarmonyPatch]
    internal static class ProgressionOrbitNameTags
    {
        private const float HeightAbovePlayer = 2.65f;
        private const float RefreshInterval = 0.25f;

        private static readonly Dictionary<ulong, TagView> TagsByClientId =
            new Dictionary<ulong, TagView>();

        private static float _nextRefreshTime;

        private static TMP_FontAsset _fontCache;
        private static Material _fontMaterialCache;

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostLateUpdate(PlayerControllerB __instance)
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (__instance == null || local == null || __instance != local)
                return;

            StartOfRound round = StartOfRound.Instance;
            if (round == null || !IsInOrbit(round))
            {
                ClearAll();
                return;
            }

            ProgressionRankSync.RegisterNetworkHandlers();
            if (Time.unscaledTime >= _nextRefreshTime)
            {
                _nextRefreshTime = Time.unscaledTime + RefreshInterval;
                ProgressionRankSync.PublishLocalRank();
                ProgressionRankSync.RequestAllRanksFromServer();
                RefreshTagSet(round, local);
            }

            UpdateTagTransforms(local);
        }

        /// <summary>
        /// These tags are an orbit-only readout, so the ship being anywhere else has to take them
        /// down. `inShipPhase` alone is the flag the ship raises for orbit, but it is cleared at
        /// different points in the descent depending on how the round starts; pairing it with
        /// `shipHasLanded` means a landed ship can never satisfy the check regardless of ordering.
        /// </summary>
        private static bool IsInOrbit(StartOfRound round)
        {
            return round.inShipPhase && !round.shipHasLanded;
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            ClearAll();
            ProgressionRankSync.RegisterNetworkHandlers();
            ProgressionRankSync.PublishLocalRank();
            ProgressionRankSync.RequestAllRanksFromServer();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void OnEndOfGame()
        {
            ClearAll();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void OnDisconnect()
        {
            ClearAll();
            ProgressionRankSync.Clear();
        }

        private static void RefreshTagSet(StartOfRound round, PlayerControllerB local)
        {
            if (round?.allPlayerScripts == null)
                return;

            HashSet<ulong> seen = new HashSet<ulong>();
            for (int i = 0; i < round.allPlayerScripts.Length; i++)
            {
                PlayerControllerB player = round.allPlayerScripts[i];
                if (!ShouldShowTagFor(player, local))
                    continue;

                ulong clientId = player.actualClientId;

                TagView tag = GetOrCreateTag(player);
                if (tag == null)
                    continue;

                seen.Add(clientId);

                int rankXp = ProgressionRankSync.GetRankXpForPlayer(player);
                string rankName = RankCatalog.GetRank(rankXp).Name;
                string username = string.IsNullOrWhiteSpace(player.playerUsername)
                    ? "Employee"
                    : player.playerUsername;

                tag.SetText(username, rankName);
            }

            List<ulong> stale = null;
            foreach (ulong clientId in TagsByClientId.Keys)
            {
                if (seen.Contains(clientId))
                    continue;

                stale ??= new List<ulong>();
                stale.Add(clientId);
            }

            if (stale == null)
                return;

            for (int i = 0; i < stale.Count; i++)
            {
                if (TagsByClientId.TryGetValue(stale[i], out TagView tag))
                    tag.Destroy();
                TagsByClientId.Remove(stale[i]);
            }
        }

        private static void UpdateTagTransforms(PlayerControllerB local)
        {
            Camera camera = local?.gameplayCamera;
            if (camera == null)
                camera = Camera.main;
            if (camera == null)
                return;

            foreach (TagView tag in TagsByClientId.Values)
                tag.UpdateTransform(camera);
        }

        private static bool ShouldShowTagFor(PlayerControllerB player, PlayerControllerB local)
        {
            if (player == null || local == null || player == local)
                return false;
            if (!player.isPlayerControlled || player.isPlayerDead)
                return false;
            return player.gameObject != null && player.gameObject.activeInHierarchy;
        }

        private static TagView GetOrCreateTag(PlayerControllerB player)
        {
            ulong clientId = player.actualClientId;
            if (TagsByClientId.TryGetValue(clientId, out TagView existing)
                && existing != null
                && existing.IsAlive)
            {
                return existing;
            }

            TagView created = TagView.Create(player);
            if (created != null)
                TagsByClientId[clientId] = created;
            return created;
        }

        private static void ClearAll()
        {
            foreach (TagView tag in TagsByClientId.Values)
                tag?.Destroy();
            TagsByClientId.Clear();
        }

        /// <summary>
        /// A TextMeshPro component added to a bare GameObject inherits no font asset, and TMP
        /// draws that state as a filled block rather than glyphs — a second pane sitting on the
        /// backdrop. Borrowing a live HUD label's font and its shared material is the only way to
        /// be sure the atlas and the SDF material actually match. Returns false until the HUD
        /// exists, and the caller retries on the next refresh instead of building a broken tag.
        /// </summary>
        private static bool TryResolveFont(out TMP_FontAsset font, out Material material)
        {
            if (_fontCache != null)
            {
                font = _fontCache;
                material = _fontMaterialCache;
                return true;
            }

            font = null;
            material = null;

            HUDManager hud = HUDManager.Instance;
            if (hud == null)
                return false;

            TMP_Text source = null;
            if (hud.playerLevelText != null && hud.playerLevelText.font != null)
                source = hud.playerLevelText;
            else if (hud.clockNumber != null && hud.clockNumber.font != null)
                source = hud.clockNumber;
            else
                source = hud.GetComponentInChildren<TMP_Text>(true);

            if (source == null || source.font == null)
                return false;

            _fontCache = source.font;
            _fontMaterialCache = source.fontSharedMaterial;
            font = _fontCache;
            material = _fontMaterialCache;
            return true;
        }

        private sealed class TagView
        {
            private const float PaddingX = 14f;
            private const float PaddingY = 8f;

            private readonly PlayerControllerB _player;
            private readonly GameObject _root;
            private readonly RectTransform _rect;
            private readonly RectTransform _backdropRect;
            private readonly TextMeshProUGUI _text;

            private string _lastUsername;
            private string _lastRank;

            private TagView(
                PlayerControllerB player,
                GameObject root,
                RectTransform rect,
                RectTransform backdropRect,
                TextMeshProUGUI text)
            {
                _player = player;
                _root = root;
                _rect = rect;
                _backdropRect = backdropRect;
                _text = text;
            }

            internal bool IsAlive => _root != null && _text != null && _player != null;

            internal static TagView Create(PlayerControllerB player)
            {
                if (player == null)
                    return null;
                if (!TryResolveFont(out TMP_FontAsset font, out Material fontMaterial))
                    return null;

                GameObject root = new GameObject("Y4NGZ_OrbitNameTag");
                root.transform.SetParent(player.transform, false);
                root.transform.localPosition = new Vector3(0f, HeightAbovePlayer, 0f);
                root.transform.localScale = Vector3.one * 0.0105f;

                Canvas canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.sortingOrder = 80;
                // TMP reads its SDF parameters out of the extra vertex streams; a hand-built canvas
                // carries only TexCoord0 unless they are asked for.
                canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1
                    | AdditionalCanvasShaderChannels.Normal
                    | AdditionalCanvasShaderChannels.Tangent;

                RectTransform rect = root.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(260f, 62f);

                GameObject bgObj = new GameObject("Background");
                bgObj.transform.SetParent(root.transform, false);
                RectTransform bgRect = bgObj.AddComponent<RectTransform>();
                // Centred and sized from the text in SetText rather than stretched to the canvas:
                // a fixed backdrop wider than the name reads as a second empty panel behind it.
                bgRect.anchorMin = new Vector2(0.5f, 0.5f);
                bgRect.anchorMax = new Vector2(0.5f, 0.5f);
                bgRect.pivot = new Vector2(0.5f, 0.5f);
                bgRect.anchoredPosition = Vector2.zero;
                bgRect.sizeDelta = Vector2.zero;

                Image bg = bgObj.AddComponent<Image>();
                bg.sprite = null;
                bg.color = new Color(0.05f, 0.05f, 0.06f, 0.55f);
                bg.raycastTarget = false;

                GameObject textObj = new GameObject("Label");
                textObj.transform.SetParent(root.transform, false);
                RectTransform textRect = textObj.AddComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;

                TextMeshProUGUI text = textObj.AddComponent<TextMeshProUGUI>();
                text.font = font;
                if (fontMaterial != null)
                    text.fontSharedMaterial = fontMaterial;
                text.alignment = TextAlignmentOptions.Center;
                text.enableWordWrapping = false;
                text.overflowMode = TextOverflowModes.Overflow;
                text.fontSize = 17f;
                text.richText = true;
                text.raycastTarget = false;
                text.characterSpacing = 0f;

                return new TagView(player, root, rect, bgRect, text);
            }

            internal void SetText(string username, string rank)
            {
                if (_text == null)
                    return;
                if (string.Equals(_lastUsername, username, StringComparison.Ordinal)
                    && string.Equals(_lastRank, rank, StringComparison.Ordinal))
                {
                    return;
                }

                _lastUsername = username;
                _lastRank = rank;
                _text.text =
                    "<color=#FFE8DE>" + EscapeRichText(username) + "</color>\n" +
                    "<size=72%><color=#FEF656>" + EscapeRichText(rank) + "</color></size>";

                ResizeBackdrop();
            }

            private void ResizeBackdrop()
            {
                if (_backdropRect == null || _text == null)
                    return;

                _text.ForceMeshUpdate();
                Vector2 preferred = _text.GetRenderedValues(false);
                if (preferred.x <= 0f || preferred.y <= 0f)
                    preferred = _text.GetPreferredValues();

                _backdropRect.sizeDelta = new Vector2(
                    preferred.x + (PaddingX * 2f),
                    preferred.y + (PaddingY * 2f));
            }

            internal void UpdateTransform(Camera camera)
            {
                if (_root == null || _player == null || camera == null)
                    return;

                SuppressVanillaBillboard();

                _root.transform.localPosition = new Vector3(0f, HeightAbovePlayer, 0f);
                Vector3 toCamera = _root.transform.position - camera.transform.position;
                if (toCamera.sqrMagnitude > 0.001f)
                    _root.transform.rotation = Quaternion.LookRotation(toCamera.normalized, Vector3.up);

                if (_rect != null)
                {
                    float distance = Vector3.Distance(camera.transform.position, _root.transform.position);
                    float scale = Mathf.Clamp(distance / 16f, 0.75f, 1.25f);
                    _root.transform.localScale = Vector3.one * (0.0105f * scale);
                }
            }

            /// <summary>
            /// The stock username billboard sits at the same head height and carries the same name,
            /// so while this tag is up the two overlap and the stock one fades in and out on its own
            /// timer. Nothing is restored on teardown: the game re-activates its billboard from
            /// ShowNameBillboard whenever it wants it, so dropping the suppression is enough.
            /// </summary>
            private void SuppressVanillaBillboard()
            {
                if (_player.usernameAlpha != null)
                    _player.usernameAlpha.alpha = 0f;

                Canvas usernameCanvas = _player.usernameCanvas;
                if (usernameCanvas != null && usernameCanvas.gameObject.activeSelf)
                    usernameCanvas.gameObject.SetActive(false);
            }

            internal void Destroy()
            {
                if (_root != null)
                    UnityEngine.Object.Destroy(_root);
            }

            private static string EscapeRichText(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return string.Empty;

                return value.Replace("<", "&lt;").Replace(">", "&gt;");
            }
        }
    }
}
