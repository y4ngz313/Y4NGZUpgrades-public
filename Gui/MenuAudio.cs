using System;
using System.Collections;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;

namespace Y4NGZUpgrades.Gui
{
    /// <summary>
    /// Authored sound set for the player menu. Clips live under
    /// <c>Assets/UI/MenuSfx</c> and are loaded once per session, so a menu that
    /// opens before the load finishes falls back to the vanilla-derived UI
    /// clips rather than going silent.
    /// </summary>
    internal static class MenuAudio
    {
        private const string SfxFolder = "MenuSfx";

        private const string ClickFile = "click.wav";
        private const string ExitFile = "exit.wav";

        // Optional. No deny clip has been authored yet, so this file is
        // normally absent and PlayDeny falls through to the vanilla refusal
        // buzz. Drop a deny.wav next to the others and it takes over with no
        // code change.
        private const string DenyFile = "deny.wav";

        private static readonly string[] NavFiles = { "menu01.wav", "menu02.wav" };
        private static readonly string[] PurchaseFiles =
        {
            "upgrade01.wav",
            "upgrade02.wav",
            "upgrade03.wav",
            "upgrade04.wav"
        };

        // The shipped clips are peak-normalized to -1 dBFS, which is hotter than
        // the vanilla UI bank. These bring them back down to sit alongside it.
        // Trimmed a further ~20% on Lawson's verdict after the first in-game
        // pass; the ratios between them are unchanged.
        private const float ClickVolume = 0.30f;
        private const float NavVolume = 0.36f;
        private const float ExitVolume = 0.40f;
        private const float PurchaseVolume = 0.44f;
        private const float DenyVolume = 0.40f;

        private static AudioClip _clickClip;
        private static AudioClip _exitClip;
        private static AudioClip _denyClip;
        private static readonly AudioClip[] _navClips = new AudioClip[NavFiles.Length];
        private static readonly AudioClip[] _purchaseClips = new AudioClip[PurchaseFiles.Length];

        private static bool _loadStarted;
        private static AudioSource _fallbackSource;

        /// <summary>Idempotent. Safe to call before a scene or HUD exists.</summary>
        internal static void Preload()
        {
            if (_loadStarted)
                return;

            MonoBehaviour host = Plugin.Instance;
            if (host == null || !host.isActiveAndEnabled)
                return;

            _loadStarted = true;
            host.StartCoroutine(LoadAll());
        }

        /// <summary>Any clickable surface that is not one of the section buttons.</summary>
        internal static void PlayClick()
        {
            if (!TryPlay(_clickClip, ClickVolume))
                UI.PlayHover();
        }

        /// <summary>
        /// The section buttons — Upgrades, Suits, Cosmetics, Emotes, Employee File.
        /// </summary>
        internal static void PlayNav()
        {
            AudioClip clip = PickRandom(_navClips);
            if (!TryPlay(clip, NavVolume))
                UI.PlayHover();
        }

        /// <summary>Closing the menu, by button, ESC, or P.</summary>
        internal static void PlayExit()
        {
            if (!TryPlay(_exitClip, ExitVolume))
                UI.PlayClose();
        }

        /// <summary>
        /// A refused action: cannot afford, gate locked, already owned, a
        /// price that moved under the click. Every refusal path routes here so
        /// there is exactly one deny sound in the menu.
        /// Falls back to the vanilla refusal buzz
        /// (<c>ShipBuildModeManager.denyPlacementSFX</c>, via
        /// <see cref="UI.PlayDeny"/>) until a deny clip is authored.
        /// </summary>
        internal static void PlayDeny()
        {
            if (TryPlay(_denyClip, DenyVolume))
                return;

            // Vanilla-owned buzz next. If nothing vanilla has resolved yet
            // (menu opened before the ship's components exist), the authored
            // exit clip covers it - a slightly off blip beats a dead button.
            if (UI.PlayDeny())
                return;

            TryPlay(_exitClip, DenyVolume);
        }

        /// <summary>
        /// Buying tier <paramref name="tier"/> of an upgrade: tier 3 plays
        /// upgrade03. Tiers outside the authored set wrap into it.
        /// </summary>
        internal static void PlayPurchase(int tier)
        {
            int index = _purchaseClips.Length > 0
                ? (Mathf.Max(1, tier) - 1) % _purchaseClips.Length
                : 0;

            AudioClip clip = index >= 0 && index < _purchaseClips.Length
                ? _purchaseClips[index]
                : null;

            if (!TryPlay(clip, PurchaseVolume))
                UI.PlayPurchase();
        }

        /// <summary>Buying an emote, suit, or cosmetic — no tier, so any clip.</summary>
        internal static void PlayPurchaseRandom()
        {
            AudioClip clip = PickRandom(_purchaseClips);
            if (!TryPlay(clip, PurchaseVolume))
                UI.PlayPurchase();
        }

        private static AudioClip PickRandom(AudioClip[] pool)
        {
            if (pool == null || pool.Length == 0)
                return null;

            int start = UnityEngine.Random.Range(0, pool.Length);
            for (int offset = 0; offset < pool.Length; offset++)
            {
                AudioClip candidate = pool[(start + offset) % pool.Length];
                if (candidate != null)
                    return candidate;
            }

            return null;
        }

        private static bool TryPlay(AudioClip clip, float volume)
        {
            if (clip == null)
                return false;

            float scaled = volume * Mathf.Clamp01(
                Plugin.MenuSoundVolume != null ? Plugin.MenuSoundVolume.Value : 1f);
            if (scaled <= 0.001f)
                return true;

            HUDManager hud = HUDManager.Instance;
            if (hud != null && hud.UIAudio != null)
            {
                hud.UIAudio.PlayOneShot(clip, scaled);
                return true;
            }

            AudioSource source = EnsureFallbackSource();
            if (source == null)
                return false;

            source.PlayOneShot(clip, scaled);
            return true;
        }

        // The menu can be opened in orbit before the in-game HUD audio source is
        // live. One persistent 2D source covers that window.
        private static AudioSource EnsureFallbackSource()
        {
            if (_fallbackSource != null)
                return _fallbackSource;

            var host = new GameObject("Y4NGZ_MenuAudio");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _fallbackSource = host.AddComponent<AudioSource>();
            _fallbackSource.playOnAwake = false;
            _fallbackSource.loop = false;
            _fallbackSource.spatialBlend = 0f;
            return _fallbackSource;
        }

        private static IEnumerator LoadAll()
        {
            yield return LoadClip(ClickFile, clip => _clickClip = clip);
            yield return LoadClip(ExitFile, clip => _exitClip = clip);
            yield return LoadClip(DenyFile, clip => _denyClip = clip, required: false);

            for (int i = 0; i < NavFiles.Length; i++)
            {
                int index = i;
                yield return LoadClip(NavFiles[index], clip => _navClips[index] = clip);
            }

            for (int i = 0; i < PurchaseFiles.Length; i++)
            {
                int index = i;
                yield return LoadClip(PurchaseFiles[index], clip => _purchaseClips[index] = clip);
            }
        }

        private static IEnumerator LoadClip(string fileName, Action<AudioClip> assign, bool required = true)
        {
            string path = ResolvePath(fileName);
            if (string.IsNullOrEmpty(path))
            {
                if (required)
                    Plugin.CustomLogger?.LogWarning(
                        $"[PMenu] Menu sound missing: Assets/UI/{SfxFolder}/{fileName}");
                yield break;
            }

            using (UnityWebRequest request =
                UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.CustomLogger?.LogWarning(
                        $"[PMenu] Failed loading menu sound '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                    yield break;

                clip.name = "Y4NGZ_Menu_" + Path.GetFileNameWithoutExtension(fileName);
                assign(clip);
            }
        }

        private static string ResolvePath(string fileName)
        {
            string assemblyDir = Path.GetDirectoryName(typeof(MenuAudio).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "Assets", "UI", SfxFolder, fileName),
                Path.Combine(assemblyDir, "InteractiveAssets", "UI", SfxFolder, fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "Assets", "UI", SfxFolder, fileName),
                Path.Combine(Paths.PluginPath, "Y4NGZUpgrades", "Assets", "UI", SfxFolder, fileName)
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
