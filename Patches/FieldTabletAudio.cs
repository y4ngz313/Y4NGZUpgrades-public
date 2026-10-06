using System;
using System.Collections;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Authored, diegetic feedback for the Field Operations tablet. The persistent host follows
    /// the visible screen overlay while the tablet is deployed, so cues pan and attenuate from the
    /// prop without being cut off when the overlay is destroyed during the stow animation.
    /// </summary>
    internal static class FieldTabletAudio
    {
        private const string AssetFolderName = "FieldTablet";
        private const string SoundFolderName = "Sounds";

        private static readonly string[] EquipFiles = { "equip01.ogg", "equip02.ogg" };
        private static readonly string[] MovementFiles = { "movement01.ogg", "movement02.ogg" };
        private static readonly string[] SelectFiles = { "select01.ogg", "select02.ogg" };
        private static readonly string[] DroneCommandFiles = { "drone-command01.ogg", "drone-command02.ogg" };
        private const string BackFile = "back.ogg";
        private const string DroneSummonFile = "drone-summon.ogg";
        private const string HackFile = "hack.ogg";

        // The shipped files are peak-normalized to approximately -6 dBFS. These caps leave the
        // frequent input ticks below ordinary item/gameplay audio, while one-off cues stay legible.
        private const float MovementVolume = 0.18f;
        private const float BackVolume = 0.20f;
        private const float SelectVolume = 0.22f;
        private const float EquipVolume = 0.24f;
        private const float DroneSummonVolume = 0.28f;
        private const float DroneCommandVolume = 0.24f;
        private const float HackVolume = 0.24f;

        private const float SpatialBlend = 0.82f;
        private const float MinDistance = 0.20f;
        private const float MaxDistance = 3.20f;

        private static readonly AudioClip[] EquipClips = new AudioClip[EquipFiles.Length];
        private static readonly AudioClip[] MovementClips = new AudioClip[MovementFiles.Length];
        private static readonly AudioClip[] SelectClips = new AudioClip[SelectFiles.Length];
        private static readonly AudioClip[] DroneCommandClips = new AudioClip[DroneCommandFiles.Length];

        private static AudioClip _backClip;
        private static AudioClip _droneSummonClip;
        private static AudioClip _hackClip;

        private static GameObject _host;
        private static AudioSource _inputSource;
        private static AudioSource _actionSource;
        private static Transform _anchor;
        // The mixer source the sources were last routed through; with _anchor it lets the
        // per-frame BindTo return early once nothing about the binding has changed.
        private static AudioSource _boundMixerSource;
        private static bool _loadStarted;
        private static bool _equipQueued;
        private static int _lastEquipIndex = -1;
        private static int _lastMovementIndex = -1;
        private static int _lastSelectIndex = -1;
        private static int _lastDroneCommandIndex = -1;

        /// <summary>Idempotent and safe before a round or local player exists.</summary>
        internal static void Preload()
        {
            if (_loadStarted)
                return;

            MonoBehaviour host = Y4NGZPersistentRunner.Host;
            if (host == null || !host.isActiveAndEnabled)
                return;

            _loadStarted = true;
            host.StartCoroutine(LoadAll());
        }

        /// <summary>
        /// Follows the actual screen surface and borrows the player's item mixer group. Called by
        /// the screen runtime every frame; once the anchor, mixer source and audio sources are
        /// unchanged it only retries a queued equip cue. The LateUpdate postfix moves the host
        /// with the hand-animated tablet through <see cref="FollowBoundAnchor"/>.
        /// </summary>
        internal static void BindTo(Transform anchor, AudioSource mixerSource)
        {
            if (anchor == null)
                return;

            if (ReferenceEquals(anchor, _anchor)
                && ReferenceEquals(mixerSource, _boundMixerSource)
                && _host != null
                && _inputSource != null
                && _actionSource != null)
            {
                if (_equipQueued)
                    TryPlayQueuedEquip();
                return;
            }

            _anchor = anchor;
            _boundMixerSource = mixerSource;
            EnsureSources();
            FollowAnchor();

            if (mixerSource != null)
            {
                _inputSource.outputAudioMixerGroup = mixerSource.outputAudioMixerGroup;
                _actionSource.outputAudioMixerGroup = mixerSource.outputAudioMixerGroup;
            }

            TryPlayQueuedEquip();
        }

        /// <summary>Queues the pull-out cue until both the authored prop and clip are ready.</summary>
        internal static void QueueEquip()
        {
            _equipQueued = true;
            TryPlayQueuedEquip();
        }

        /// <summary>Plays the put-away cue immediately; the persistent host preserves its tail.</summary>
        internal static void PlayEquip()
        {
            PlayVariant(_actionSource, EquipClips, ref _lastEquipIndex, EquipVolume);
        }

        internal static void PlayMovement()
        {
            PlayVariant(_inputSource, MovementClips, ref _lastMovementIndex, MovementVolume);
        }

        internal static void PlayBack()
        {
            PlayClip(_inputSource, _backClip, BackVolume);
        }

        internal static void PlaySelect()
        {
            PlayVariant(_inputSource, SelectClips, ref _lastSelectIndex, SelectVolume);
        }

        internal static void PlayDroneSummon()
        {
            PlayClip(_actionSource, _droneSummonClip, DroneSummonVolume);
        }

        internal static void PlayDroneCommand()
        {
            PlayVariant(_actionSource, DroneCommandClips, ref _lastDroneCommandIndex, DroneCommandVolume);
        }

        internal static void PlayHack()
        {
            PlayClip(_actionSource, _hackClip, HackVolume);
        }

        internal static void Unbind()
        {
            _anchor = null;
            _boundMixerSource = null;
            _equipQueued = false;
        }

        internal static void FollowBoundAnchor()
        {
            FollowAnchor();
        }

        internal static void Shutdown()
        {
            _anchor = null;
            _boundMixerSource = null;
            _equipQueued = false;
            StopSource(_inputSource);
            StopSource(_actionSource);
            if (_host != null)
                UnityEngine.Object.Destroy(_host);
            _host = null;
            _inputSource = null;
            _actionSource = null;

            DestroyClips(EquipClips);
            DestroyClips(MovementClips);
            DestroyClips(SelectClips);
            DestroyClips(DroneCommandClips);
            DestroyClip(ref _backClip);
            DestroyClip(ref _droneSummonClip);
            DestroyClip(ref _hackClip);
            _loadStarted = false;
            _lastEquipIndex = -1;
            _lastMovementIndex = -1;
            _lastSelectIndex = -1;
            _lastDroneCommandIndex = -1;
        }

        private static void EnsureSources()
        {
            if (_host == null)
            {
                _host = new GameObject("Y4NGZ_FieldTabletAudio")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                UnityEngine.Object.DontDestroyOnLoad(_host);
            }

            if (_inputSource == null)
                _inputSource = CreateSource("Input");
            if (_actionSource == null)
                _actionSource = CreateSource("Action");
        }

        private static AudioSource CreateSource(string suffix)
        {
            GameObject child = new GameObject("Y4NGZ_FieldTabletAudio_" + suffix)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            child.transform.SetParent(_host.transform, false);
            AudioSource source = child.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = SpatialBlend;
            source.dopplerLevel = 0f;
            source.spread = 30f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = MinDistance;
            source.maxDistance = MaxDistance;
            source.priority = 160;
            return source;
        }

        private static void FollowAnchor()
        {
            if (_host == null || _anchor == null)
                return;

            _host.transform.SetPositionAndRotation(_anchor.position, _anchor.rotation);
        }

        private static void TryPlayQueuedEquip()
        {
            if (!_equipQueued || _anchor == null)
                return;
            if (!PlayVariant(_actionSource, EquipClips, ref _lastEquipIndex, EquipVolume))
                return;
            _equipQueued = false;
        }

        private static bool PlayVariant(AudioSource source, AudioClip[] clips, ref int lastIndex, float volume)
        {
            if (source == null || clips == null || clips.Length == 0)
                return false;

            int start = UnityEngine.Random.Range(0, clips.Length);
            for (int offset = 0; offset < clips.Length; offset++)
            {
                int index = (start + offset) % clips.Length;
                if (clips[index] == null || (index == lastIndex && HasOtherLiveClip(clips, lastIndex)))
                    continue;

                lastIndex = index;
                return PlayClip(source, clips[index], volume);
            }

            if (lastIndex >= 0 && lastIndex < clips.Length)
                return PlayClip(source, clips[lastIndex], volume);
            return false;
        }

        private static bool HasOtherLiveClip(AudioClip[] clips, int excludedIndex)
        {
            for (int i = 0; i < clips.Length; i++)
            {
                if (i != excludedIndex && clips[i] != null)
                    return true;
            }
            return false;
        }

        private static bool PlayClip(AudioSource source, AudioClip clip, float volume)
        {
            if (source == null || clip == null)
                return false;

            FollowAnchor();
            source.Stop();
            source.clip = clip;
            source.volume = Mathf.Clamp01(volume);
            source.Play();
            return true;
        }

        private static IEnumerator LoadAll()
        {
            for (int i = 0; i < EquipFiles.Length; i++)
            {
                int index = i;
                yield return LoadClip(EquipFiles[index], clip => EquipClips[index] = clip);
            }
            for (int i = 0; i < MovementFiles.Length; i++)
            {
                int index = i;
                yield return LoadClip(MovementFiles[index], clip => MovementClips[index] = clip);
            }
            for (int i = 0; i < SelectFiles.Length; i++)
            {
                int index = i;
                yield return LoadClip(SelectFiles[index], clip => SelectClips[index] = clip);
            }

            yield return LoadClip(BackFile, clip => _backClip = clip);
            yield return LoadClip(DroneSummonFile, clip => _droneSummonClip = clip);
            for (int i = 0; i < DroneCommandFiles.Length; i++)
            {
                int index = i;
                yield return LoadClip(DroneCommandFiles[index], clip => DroneCommandClips[index] = clip);
            }
            yield return LoadClip(HackFile, clip => _hackClip = clip);

            TryPlayQueuedEquip();
        }

        private static IEnumerator LoadClip(string fileName, Action<AudioClip> assign)
        {
            string path = ResolvePath(fileName);
            if (string.IsNullOrEmpty(path))
            {
                Plugin.Log?.LogWarning($"[Field Tablet] sound missing: Assets/{AssetFolderName}/{SoundFolderName}/{fileName}");
                yield break;
            }

            using (UnityWebRequest request =
                UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log?.LogWarning($"[Field Tablet] failed loading sound '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                    yield break;

                clip.name = "Y4NGZ_FieldTablet_" + Path.GetFileNameWithoutExtension(fileName);
                assign?.Invoke(clip);
            }
        }

        private static string ResolvePath(string fileName)
        {
            string assemblyDir = Path.GetDirectoryName(typeof(FieldTabletAudio).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "Assets", AssetFolderName, SoundFolderName, fileName),
                Path.Combine(assemblyDir, "InteractiveAssets", AssetFolderName, SoundFolderName, fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "Assets", AssetFolderName, SoundFolderName, fileName),
                Path.Combine(Paths.PluginPath, "Y4NGZUpgrades", "Assets", AssetFolderName, SoundFolderName, fileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                    return candidates[i];
            }

            return null;
        }

        private static void StopSource(AudioSource source)
        {
            if (source == null)
                return;
            source.Stop();
            source.clip = null;
        }

        private static void DestroyClips(AudioClip[] clips)
        {
            for (int i = 0; i < clips.Length; i++)
                DestroyClip(ref clips[i]);
        }

        private static void DestroyClip(ref AudioClip clip)
        {
            if (clip != null)
                UnityEngine.Object.Destroy(clip);
            clip = null;
        }
    }
}
