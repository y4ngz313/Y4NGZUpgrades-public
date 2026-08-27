using System;
using System.Collections;
using System.IO;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Y4NGZUpgrades
{
    internal static class ProgressionXpBarUi
    {
        private const string MarksCounterName = "Y4NGZ_TokensGrantedCounter";
        private const string MarksStripName = "Y4NGZ_TokensGrantedExtension";
        private const string LegacyMarksStripName = "Y4NGZ_MarksGrantedStrip";
        private const string MarksSoundFileName = "juniorsoundays-ui-sound-67-527859.mp3";
        private const float MarksStripHeight = 52f;
        private const float MarksStripVerticalGap = 10f;
        private const float PresentationVisibilityTimeoutSeconds = 2f;
        private const float CoordinatedPresentationReadyLifetimeSeconds = 1.5f;
        private const float DeferPresentationFailsafeSeconds = 45f;
        // Matches Y4NGZCompany's XpPopupRankOrMarksBeatSeconds so the report's hold
        // window budgets the same time we actually spend on rank-up beats.
        private const float RankUpBeatSeconds = 0.4f;
        private const string OwnedLevellingAudioName = "Y4NGZ_ProgressionLevellingAudio";
        private const string OwnedUiAudioName = "Y4NGZ_ProgressionUiAudio";

        private static AudioSource _ownedLevellingAudio;
        private static AudioSource _ownedUiAudio;
        private static readonly Vector3[] TokenStripWorldCorners = new Vector3[4];

        private static MonoBehaviour _coroutineHost;
        private static AudioClip _marksGrantedClip;
        private static bool _marksGrantedClipLoadStarted;
        private static MonoBehaviour _animationHost;
        private static Coroutine _animationCoroutine;
        private static HUDManager _animationHud;
        private static RoundXpBreakdown _animationLog;
        private static RoundXpBreakdown _completedLog;
        private static int _animationGeneration;
        private static HUDManager _coordinatedPresentationHud;
        private static float _coordinatedPresentationReadyUntilRealtime;
        private static readonly Vector3[] PresentationWorldCorners = new Vector3[4];

        private enum RankChangeDirection
        {
            Up,
            None
        }

        // While this returns true the XP/token presentation must not start: no
        // meter movement and, critically, no audio. Resolved against
        // LGUContractHUD's performance report gate so a ShowRoundXp call that
        // slips in while the report is on screen parks instead of playing its
        // audio underneath the report and then replaying silently.
        internal static Func<HUDManager, bool> ShouldDeferPresentation;

        internal static void Initialize(MonoBehaviour host)
        {
            _coroutineHost = host;
            EnsureMarksSoundLoading();
            ShouldDeferPresentation = null;
            TryResolveCompanyReportHold();
        }

        private static void TryResolveCompanyReportHold()
        {
            if (ShouldDeferPresentation != null)
                return;
            if (!OptionalPluginCapabilities.Contracted)
            {
                Plugin.Log?.LogInfo(
                    "[Progression] Contracted not present; XP/token gains use the standalone vanilla level-box presentation.");
                return;
            }

            try
            {
                Type reportType = Type.GetType(
                    "Y4NGZCompany.Contracts._Shared.ContractPerformanceReportUi, Y4NGZCompany",
                    throwOnError: false);
                System.Reflection.MethodInfo method = reportType?.GetMethod(
                    "ShouldHoldXpPresentation",
                    System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(HUDManager) },
                    null);
                if (method == null)
                    return;

                ShouldDeferPresentation = hud =>
                {
                    try
                    {
                        return method.Invoke(null, new object[] { hud }) is bool hold && hold;
                    }
                    catch
                    {
                        return false;
                    }
                };
                Plugin.Log?.LogInfo("[Progression] XP/token presentation gated behind the Company performance report.");
            }
            catch
            {
            }
        }

        internal static bool IsPresentationActive => _animationCoroutine != null;

        /// <summary>
        /// Duplicate-presentation suppression is keyed on the round number, not on object
        /// identity. Identity alone made a single missed round permanently swallow every
        /// later one: if ResetForRound never ran (it hung off the host-only StartGame hook,
        /// #214) FinalizeRound kept returning the same cached instance and this check
        /// matched forever. A sequence number cannot alias across rounds.
        /// </summary>
        private static bool IsSameRound(RoundXpBreakdown a, RoundXpBreakdown b)
        {
            if (a == null || b == null)
                return false;
            if (ReferenceEquals(a, b))
                return true;
            return a.RoundSequence > 0 && a.RoundSequence == b.RoundSequence;
        }

        internal static void ResetForRound()
        {
            CancelActiveAnimation("round reset");
            _completedLog = null;
            ClearCoordinatedPresentationReady();
        }

        internal static void MarkPresentationWindowReady(HUDManager hud)
        {
            if (hud == null)
                return;

            _coordinatedPresentationHud = hud;
            _coordinatedPresentationReadyUntilRealtime =
                Time.realtimeSinceStartup + CoordinatedPresentationReadyLifetimeSeconds;
        }

        // The vanilla level box carries whatever fill/text it last had (scene-authored
        // defaults on a fresh session, or last round's final values). The Company report
        // fades the box in seconds before our value replay runs, so without this the
        // player watches stale vanilla-looking values instead of their real progression.
        // Stamp the true starting state as soon as the presentation is requested.
        internal static void PrimeStartingValues(HUDManager hud, RoundXpBreakdown log)
        {
            if (hud == null || _animationCoroutine != null)
                return;

            if (log != null && IsSameRound(log, _completedLog))
                return;

            int xp = log != null ? Mathf.Max(0, log.OldXp) : ProgressionManager.CurrentRankXp;
            SetHudRankImmediate(hud, xp, RankChangeDirection.None);
            HideMarksCounter(hud);
        }

        internal static void ShowRoundXp(HUDManager hud, RoundXpBreakdown log)
        {
            if (hud == null)
                return;

            if (log == null)
            {
                CancelActiveAnimation("missing round breakdown");
                HideMarksCounter(hud);
                SetHudRankImmediate(hud, ProgressionManager.CurrentRankXp, RankChangeDirection.None);
                return;
            }

            if (IsSameRound(log, _animationLog) && _animationCoroutine != null)
            {
                Plugin.Log?.LogInfo("[Progression] Coalesced duplicate in-flight XP/token presentation request.");
                return;
            }

            if (IsSameRound(log, _completedLog))
            {
                SetHudRankImmediate(
                    hud,
                    Mathf.Max(0, log.NewXp),
                    log.RanksGained > 0 ? RankChangeDirection.Up : RankChangeDirection.None);
                Plugin.Log?.LogInfo("[Progression] Ignored duplicate completed XP/token presentation request.");
                return;
            }

            CancelActiveAnimation("superseded presentation request");
            MonoBehaviour host = _coroutineHost != null
                ? _coroutineHost
                : Y4NGZPersistentRunner.Host != null
                    ? Y4NGZPersistentRunner.Host
                    : hud;
            if (host == null)
                return;

            _animationHost = host;
            _animationHud = hud;
            _animationLog = log;
            int generation = ++_animationGeneration;
            bool presentationWindowReady = ConsumeCoordinatedPresentationReady(hud);
            _animationCoroutine = host.StartCoroutine(
                AnimateXp(hud, log, generation, presentationWindowReady));
            Plugin.Log?.LogInfo(
                $"[Progression] XP/token presentation started: old={log.OldXp}, new={log.NewXp}, " +
                $"awarded={log.AwardedTotal}, ranks={log.RanksGained}, tokens={log.CurrencyGranted}, " +
                $"coordinatedReady={presentationWindowReady}.");
        }

        private static IEnumerator AnimateXp(
            HUDManager hud,
            RoundXpBreakdown log,
            int generation,
            bool presentationWindowReady)
        {
            int oldXp = Mathf.Max(0, log.OldXp);
            int newXp = Mathf.Max(0, log.NewXp);
            int gain = Mathf.Max(0, log.AwardedTotal);
            int changingRank = RankCatalog.GetRankIndex(oldXp);
            float changingXp = oldXp;
            RankChangeDirection direction = log.RanksGained > 0 ? RankChangeDirection.Up : RankChangeDirection.None;
            int totalMarksEarned = Mathf.Max(0, log.CurrencyGranted);
            int marksShown = 0;
            TextMeshProUGUI marksCounter = totalMarksEarned > 0
                ? EnsureMarksCounter(hud)
                : FindMarksCounter(hud);
            SetMarksCounter(marksCounter, 0, visible: false);

            // Park the whole presentation (audio included) while the Company
            // performance report still owns the end-of-round screen. This is
            // the backstop for calls that reach us before the report finished:
            // without it the meter audio plays underneath the report and the
            // later replay lands in the completed-log short-circuit, showing a
            // full meter with no animation or sound.
            if (!presentationWindowReady && ShouldDeferPresentation != null)
            {
                float deferStart = Time.realtimeSinceStartup;
                bool parked = false;
                while (Time.realtimeSinceStartup - deferStart < DeferPresentationFailsafeSeconds)
                {
                    bool hold;
                    try
                    {
                        hold = ShouldDeferPresentation(hud);
                    }
                    catch
                    {
                        hold = false;
                    }

                    if (!hold)
                        break;

                    if (!parked)
                    {
                        parked = true;
                        Plugin.Log?.LogInfo("[Progression] XP/token presentation parked behind the performance report.");
                    }

                    if (!IsCurrentPresentation(hud, log, generation))
                        yield break;

                    yield return null;
                }

                if (parked)
                {
                    Plugin.Log?.LogInfo(
                        "[Progression] XP/token presentation released after " +
                        $"{Time.realtimeSinceStartup - deferStart:0.00}s behind the performance report.");
                }
            }

            if (!presentationWindowReady)
            {
                yield return WaitForPresentationVisible(hud, log, generation);
                if (!IsCurrentPresentation(hud, log, generation))
                    yield break;

                if (!IsPresentationVisible(hud))
                {
                    SetHudRankImmediate(hud, newXp, direction);
                    HideMarksCounter(hud);
                    Plugin.Log?.LogWarning(
                        $"[Progression] XP/token presentation never became visible within " +
                        $"{PresentationVisibilityTimeoutSeconds:0.0}s; applied final values without playing audio.");
                    CompleteActiveAnimation(hud, log, generation);
                    yield break;
                }
            }
            else
            {
                // Let the coordinator animator sample commit before audio/value playback.
                yield return null;
                if (!IsCurrentPresentation(hud, log, generation))
                    yield break;
            }

            PlayLevellingFillAudio(hud, gain);

            float start = Time.realtimeSinceStartup;
            float duration = Mathf.Clamp(gain / 120f, 1.1f, 2.5f);
            while (Time.realtimeSinceStartup - start < duration)
            {
                float t = Mathf.Clamp01((Time.realtimeSinceStartup - start) / duration);
                changingXp = Mathf.Lerp(oldXp, newXp, Smooth(t));
                int nextRank = RankCatalog.GetRankIndex(Mathf.RoundToInt(changingXp));
                if (nextRank > changingRank)
                {
                    for (int rank = changingRank + 1; rank <= nextRank; rank++)
                        OnAnimatedRankUp(hud, log, rank, totalMarksEarned, ref marksShown, marksCounter);

                    changingRank = nextRank;
                }
                else if (nextRank != changingRank)
                {
                    changingRank = nextRank;
                }

                SetHudRankValues(hud, Mathf.RoundToInt(changingXp), changingRank, direction);
                yield return null;
            }

            // The loop exits on elapsed time and never lands a final t = 1 sample, so
            // a rank boundary sitting at (or just under) newXp is silently skipped:
            // no shake, no level SFX, no token reveal, no token sound - the meter just
            // snaps to the new rank below. Settle the remaining rank-ups explicitly.
            int finalRank = RankCatalog.GetRankIndex(newXp);
            while (changingRank < finalRank)
            {
                changingRank++;
                OnAnimatedRankUp(hud, log, changingRank, totalMarksEarned, ref marksShown, marksCounter);
                SetHudRankValues(hud, newXp, changingRank, direction);

                float beatStart = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - beatStart < RankUpBeatSeconds)
                {
                    if (!IsCurrentPresentation(hud, log, generation))
                        yield break;
                    yield return null;
                }
            }

            StopLevellingAudio(hud);

            SetHudRankImmediate(hud, newXp, direction);
            if (totalMarksEarned > 0)
                SetMarksCounter(marksCounter, totalMarksEarned, visible: true);

            Plugin.Log?.LogInfo(
                $"[Progression] XP/token presentation settled: rank={changingRank}/{finalRank}, " +
                $"tokensEarned={totalMarksEarned}, tokensShown={marksShown}, " +
                $"counterPresent={marksCounter != null}, " +
                $"stripActive={(marksCounter != null && marksCounter.transform.parent != null && marksCounter.transform.parent.gameObject.activeInHierarchy)}.");

            CompleteActiveAnimation(hud, log, generation);
        }

        private static IEnumerator WaitForPresentationVisible(
            HUDManager hud,
            RoundXpBreakdown log,
            int generation)
        {
            float start = Time.realtimeSinceStartup;
            while (IsCurrentPresentation(hud, log, generation)
                   && !IsPresentationVisible(hud)
                   && Time.realtimeSinceStartup - start < PresentationVisibilityTimeoutSeconds)
            {
                yield return null;
            }
        }

        private static bool ConsumeCoordinatedPresentationReady(HUDManager hud)
        {
            if (_coordinatedPresentationHud == null
                || Time.realtimeSinceStartup > _coordinatedPresentationReadyUntilRealtime)
            {
                ClearCoordinatedPresentationReady();
                return false;
            }

            if (_coordinatedPresentationHud != hud)
                return false;

            ClearCoordinatedPresentationReady();
            return true;
        }

        private static void ClearCoordinatedPresentationReady()
        {
            _coordinatedPresentationHud = null;
            _coordinatedPresentationReadyUntilRealtime = 0f;
        }

        internal static bool IsPresentationVisible(HUDManager hud)
        {
            RectTransform levelUpBox = FindVanillaLevelUpBox(hud);
            if (levelUpBox == null || levelUpBox.gameObject == null || !levelUpBox.gameObject.activeInHierarchy)
                return false;

            Canvas canvas = levelUpBox.GetComponentInParent<Canvas>();
            if (canvas == null || !canvas.isActiveAndEnabled)
                return false;

            if (!IsCanvasGroupPathVisible(levelUpBox))
                return false;

            Graphic[] graphics = levelUpBox.GetComponentsInChildren<Graphic>(false);
            bool hasVisibleGraphic = false;
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic != null
                    && graphic.enabled
                    && graphic.gameObject.activeInHierarchy
                    && graphic.color.a > 0.03f)
                {
                    hasVisibleGraphic = true;
                    break;
                }
            }

            if (!hasVisibleGraphic)
                return false;

            return IsRectOnScreen(levelUpBox, canvas);
        }

        private static bool IsCanvasGroupPathVisible(Transform transform)
        {
            float effectiveAlpha = 1f;
            for (Transform current = transform; current != null; current = current.parent)
            {
                CanvasGroup[] groups = current.GetComponents<CanvasGroup>();
                bool ignoreParents = false;
                for (int i = 0; i < groups.Length; i++)
                {
                    CanvasGroup group = groups[i];
                    if (group == null || !group.isActiveAndEnabled)
                        continue;

                    effectiveAlpha *= group.alpha;
                    if (effectiveAlpha <= 0.03f)
                        return false;
                    ignoreParents |= group.ignoreParentGroups;
                }

                if (ignoreParents)
                    break;
            }

            return true;
        }

        private static bool IsRectOnScreen(RectTransform rect, Canvas canvas)
        {
            if (Screen.width <= 0 || Screen.height <= 0)
                return true;

            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas.worldCamera;
            rect.GetWorldCorners(PresentationWorldCorners);

            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < PresentationWorldCorners.Length; i++)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, PresentationWorldCorners[i]);
                minX = Mathf.Min(minX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxX = Mathf.Max(maxX, point.x);
                maxY = Mathf.Max(maxY, point.y);
            }

            Rect screenRect = canvas.pixelRect;
            if (screenRect.width <= 0f || screenRect.height <= 0f)
                screenRect = new Rect(0f, 0f, Screen.width, Screen.height);

            return maxX >= screenRect.xMin
                && minX <= screenRect.xMax
                && maxY >= screenRect.yMin
                && minY <= screenRect.yMax;
        }

        private static bool IsCurrentPresentation(
            HUDManager hud,
            RoundXpBreakdown log,
            int generation)
        {
            return generation == _animationGeneration
                && _animationHud == hud
                && ReferenceEquals(_animationLog, log);
        }

        private static void CompleteActiveAnimation(
            HUDManager hud,
            RoundXpBreakdown log,
            int generation)
        {
            if (!IsCurrentPresentation(hud, log, generation))
                return;

            StopLevellingAudio(hud);
            _completedLog = log;
            _animationCoroutine = null;
            _animationHost = null;
            _animationHud = null;
            _animationLog = null;
            Plugin.Log?.LogInfo("[Progression] XP/token presentation value animation completed.");
        }

        private static void CancelActiveAnimation(string reason)
        {
            if (_animationCoroutine == null)
                return;

            Coroutine coroutine = _animationCoroutine;
            MonoBehaviour host = _animationHost;
            HUDManager hud = _animationHud;
            _animationGeneration++;
            _animationCoroutine = null;
            _animationHost = null;
            _animationHud = null;
            _animationLog = null;

            if (host != null && coroutine != null)
                host.StopCoroutine(coroutine);
            StopLevellingAudio(hud);
            if (hud != null)
                HideMarksCounter(hud);
            Plugin.Log?.LogInfo($"[Progression] Cancelled XP/token presentation ({reason ?? "unspecified"}).");
        }

        private static void StopLevellingAudio(HUDManager hud)
        {
            if (hud != null && hud.LevellingAudio != null && hud.LevellingAudio.isPlaying)
                hud.LevellingAudio.Stop();
            if (_ownedLevellingAudio != null && _ownedLevellingAudio.isPlaying)
                _ownedLevellingAudio.Stop();
        }

        // The report freezes the endgame animator for the whole XP window, and the
        // vanilla level-up hierarchy is force-toggled around it. AudioSource.Play is a
        // silent no-op on a disabled source or an inactive GameObject, which is how the
        // meter ends up animating with no sound at all - so fall back to a source we
        // own and keep active whenever the vanilla one is not usable this frame.
        private static void PlayLevellingFillAudio(HUDManager hud, int gain)
        {
            if (hud == null || gain <= 0 || hud.increaseXPSFX == null)
            {
                Plugin.Log?.LogInfo(
                    "[Progression] XP fill audio skipped: " +
                    $"hud={hud != null}, gain={gain}, clip={(hud != null && hud.increaseXPSFX != null)}.");
                return;
            }

            if (IsUsableAudioSource(hud.LevellingAudio))
            {
                hud.LevellingAudio.clip = hud.increaseXPSFX;
                hud.LevellingAudio.Play();
                Plugin.Log?.LogInfo(
                    $"[Progression] XP fill audio playing on vanilla LevellingAudio (isPlaying={hud.LevellingAudio.isPlaying}, " +
                    $"volume={hud.LevellingAudio.volume:0.00}, mute={hud.LevellingAudio.mute}).");
                return;
            }

            AudioSource owned = EnsureOwnedAudioSource(
                ref _ownedLevellingAudio,
                OwnedLevellingAudioName,
                hud.LevellingAudio ?? hud.UIAudio);
            if (owned == null)
                return;

            owned.loop = true;
            owned.clip = hud.increaseXPSFX;
            owned.Play();
            Plugin.Log?.LogInfo("[Progression] Vanilla LevellingAudio unusable; played the XP fill through the Y4NGZ source.");
        }

        private static void PlayLevelUpOneShot(HUDManager hud)
        {
            if (hud == null || hud.levelIncreaseSFX == null)
                return;

            if (IsUsableAudioSource(hud.UIAudio))
            {
                hud.UIAudio.PlayOneShot(hud.levelIncreaseSFX);
                return;
            }

            AudioSource owned = EnsureOwnedAudioSource(
                ref _ownedUiAudio,
                OwnedUiAudioName,
                hud.UIAudio ?? hud.LevellingAudio);
            owned?.PlayOneShot(hud.levelIncreaseSFX);
        }

        private static bool IsUsableAudioSource(AudioSource source)
        {
            return source != null
                && source.gameObject != null
                && source.gameObject.activeInHierarchy
                && source.isActiveAndEnabled;
        }

        private static AudioSource EnsureOwnedAudioSource(
            ref AudioSource cached,
            string objectName,
            AudioSource template)
        {
            if (cached != null && cached.gameObject != null)
                return cached;

            GameObject obj = new GameObject(objectName);
            UnityEngine.Object.DontDestroyOnLoad(obj);

            AudioSource source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.volume = template != null ? template.volume : 1f;
            if (template != null)
                source.outputAudioMixerGroup = template.outputAudioMixerGroup;

            cached = source;
            return source;
        }

        private static float Smooth(float t)
        {
            return t * t * (3f - 2f * t);
        }

        private static void SetHudRankImmediate(HUDManager hud, int xp, RankChangeDirection direction)
        {
            SetHudRankValues(hud, xp, RankCatalog.GetRankIndex(xp), direction);
        }

        private static void SetHudRankValues(HUDManager hud, int xp, int rankIndex, RankChangeDirection direction)
        {
            RankDefinition rank = RankCatalog.GetRankByIndex(rankIndex);
            float progress = rankIndex >= RankCatalog.Count - 1
                ? 1f
                : Mathf.Clamp01((xp - rank.StartXp) / (float)Mathf.Max(1, rank.EndXp - rank.StartXp));

            Image meter = hud.playerLevelMeter;
            if (meter != null)
                meter.fillAmount = progress;

            TextMeshProUGUI rankText = hud.playerLevelText;
            if (rankText != null)
            {
                string color = direction == RankChangeDirection.Up ? "#ffff00" : "#6969ff";
                rankText.text = $"<color={color}>{rank.Name}</color>";
            }

            TextMeshProUGUI counter = hud.playerLevelXPCounter;
            if (counter != null)
                counter.text = $"<color=#ffff00>{xp} XP</color>";
        }

        private static void OnAnimatedRankUp(
            HUDManager hud,
            RoundXpBreakdown log,
            int rank,
            int totalMarksEarned,
            ref int marksShown,
            TextMeshProUGUI marksCounter)
        {
            int ranksEarnedSoFar = Mathf.Clamp(rank - log.OldRankIndex, 1, Mathf.Max(1, log.RanksGained));
            if (totalMarksEarned > 0 && log.RanksGained > 0)
            {
                int targetMarks = Mathf.Clamp(
                    Mathf.CeilToInt(totalMarksEarned * (ranksEarnedSoFar / (float)log.RanksGained)),
                    0,
                    totalMarksEarned);
                if (targetMarks > marksShown)
                {
                    marksShown = targetMarks;
                    SetMarksCounter(marksCounter, marksShown, visible: true);
                    PlayMarksGrantedSound(hud, ranksEarnedSoFar);
                }
            }

            PlayLevelUpOneShot(hud);
            if (hud.playerLevelBoxAnimator != null && hud.playerLevelBoxAnimator.isActiveAndEnabled)
                hud.playerLevelBoxAnimator.SetTrigger("Shake");
        }

        private static TextMeshProUGUI EnsureMarksCounter(HUDManager hud)
        {
            RectTransform levelUpBox = FindVanillaLevelUpBox(hud);
            if (levelUpBox == null)
                return null;

            RemoveMisplacedTokenStrips(levelUpBox);

            TextMeshProUGUI existing = FindMarksCounter(levelUpBox);
            if (existing != null)
            {
                UpdateMarksStripLayout(existing.transform.parent, levelUpBox);
                return existing;
            }

            GameObject strip = EnsureMarksStrip(levelUpBox);
            TextMeshProUGUI source = FindEmployeeRankTitleText(FindTextSearchRoot(hud, levelUpBox), hud);

            GameObject textObj = new GameObject(MarksCounterName);
            textObj.transform.SetParent(strip.transform, false);
            textObj.transform.SetAsLastSibling();

            RectTransform rect = textObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.03f, 0f);
            rect.anchorMax = new Vector2(0.97f, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            TextMeshProUGUI text = textObj.AddComponent<TextMeshProUGUI>();
            if (source != null)
            {
                text.font = source.font;
                text.fontSharedMaterial = source.fontSharedMaterial;
                float xpCounterSize = hud.playerLevelXPCounter != null ? hud.playerLevelXPCounter.fontSize * 0.58f : 0f;
                text.fontSize = Mathf.Clamp(Mathf.Max(source.fontSize * 0.58f, xpCounterSize), 16f, 34f);
            }
            else
            {
                text.fontSize = 24f;
            }

            text.fontStyle = source != null ? source.fontStyle : FontStyles.Normal;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            text.richText = true;
            text.color = new Color(1f, 0.86f, 0.24f, 1f);
            text.characterSpacing = 0f;
            strip.SetActive(false);
            return text;
        }

        private static GameObject EnsureMarksStrip(RectTransform levelUpBox)
        {
            Transform existing = levelUpBox.Find(MarksStripName);
            if (existing != null)
            {
                UpdateMarksStripLayout(existing, levelUpBox);
                return existing.gameObject;
            }

            GameObject strip = new GameObject(MarksStripName);
            strip.transform.SetParent(levelUpBox, false);
            strip.transform.SetAsLastSibling();

            RectTransform rect = strip.AddComponent<RectTransform>();
            ApplyTokenStripLayout(rect, levelUpBox);
            TokensGrantedLifetime lifetime = strip.AddComponent<TokensGrantedLifetime>();
            lifetime.Reference = levelUpBox;

            Image background = strip.AddComponent<Image>();
            background.color = new Color(0.18f, 0f, 0f, 0.72f);
            background.raycastTarget = false;

            Color border = new Color(1f, 0f, 0f, 0.92f);
            AddHorizontalFrameLine(strip.transform, "Top", 1f, -0.7f, border);
            AddHorizontalFrameLine(strip.transform, "Bottom", 0f, 0.7f, border);
            AddVerticalFrameLine(strip.transform, "Left", 0f, 0.7f, border);
            AddVerticalFrameLine(strip.transform, "Right", 1f, -0.7f, border);

            strip.SetActive(false);
            return strip;
        }

        private static void UpdateMarksStripLayout(Transform strip, RectTransform levelUpBox)
        {
            if (strip == null)
                return;

            if (strip is RectTransform rect)
                ApplyTokenStripLayout(rect, levelUpBox);

            TokensGrantedLifetime lifetime = strip.GetComponent<TokensGrantedLifetime>();
            if (lifetime == null)
                lifetime = strip.gameObject.AddComponent<TokensGrantedLifetime>();
            lifetime.Reference = levelUpBox;
        }

        private static void ApplyTokenStripLayout(RectTransform rect, RectTransform levelUpBox)
        {
            if (rect == null)
                return;

            if (HasCanvasRoomBelow(levelUpBox))
            {
                rect.anchorMin = new Vector2(0.035f, 0f);
                rect.anchorMax = new Vector2(0.965f, 0f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.offsetMin = new Vector2(0f, -(MarksStripVerticalGap + MarksStripHeight));
                rect.offsetMax = new Vector2(0f, -MarksStripVerticalGap);
            }
            else
            {
                // The level box sits too low for a strip hanging underneath it; stack
                // above instead so the counter cannot land off the bottom of the canvas.
                rect.anchorMin = new Vector2(0.035f, 1f);
                rect.anchorMax = new Vector2(0.965f, 1f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.offsetMin = new Vector2(0f, MarksStripVerticalGap);
                rect.offsetMax = new Vector2(0f, MarksStripVerticalGap + MarksStripHeight);
            }

            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
        }

        private static bool HasCanvasRoomBelow(RectTransform levelUpBox)
        {
            if (levelUpBox == null)
                return true;

            Canvas canvas = levelUpBox.GetComponentInParent<Canvas>();
            RectTransform canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            if (canvasRect == null)
                return true;

            levelUpBox.GetWorldCorners(TokenStripWorldCorners);
            float boxBottomLocal = canvasRect.InverseTransformPoint(TokenStripWorldCorners[0]).y;
            float required = MarksStripVerticalGap + MarksStripHeight + 4f;
            return boxBottomLocal - required >= canvasRect.rect.yMin;
        }

        private static void AddHorizontalFrameLine(Transform parent, string name, float yAnchor, float yOffset, Color color)
        {
            GameObject line = new GameObject("Frame" + name);
            line.transform.SetParent(parent, false);

            RectTransform rect = line.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, yAnchor);
            rect.anchorMax = new Vector2(1f, yAnchor);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, yOffset);
            rect.sizeDelta = new Vector2(0f, 1.4f);

            Image image = line.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static void AddVerticalFrameLine(Transform parent, string name, float xAnchor, float xOffset, Color color)
        {
            GameObject line = new GameObject("Frame" + name);
            line.transform.SetParent(parent, false);

            RectTransform rect = line.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(xAnchor, 0f);
            rect.anchorMax = new Vector2(xAnchor, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(xOffset, 0f);
            rect.sizeDelta = new Vector2(1.4f, 0f);

            Image image = line.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static RectTransform FindVanillaLevelUpBox(HUDManager hud)
        {
            GameObject levelUpBox = GameObject.Find("/Systems/UI/Canvas/EndgameStats/LevelUp/LevelUpBox");
            if (levelUpBox != null && levelUpBox.TryGetComponent(out RectTransform directRect))
                return directRect;

            RectTransform fieldRoot = FindCommonRankPanelRoot(hud);
            if (fieldRoot != null)
                return fieldRoot;

            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                GameObject obj = all[i];
                if (obj == null || obj.name != "LevelUpBox")
                    continue;

                if (obj.TryGetComponent(out RectTransform rect))
                    return rect;
            }

            return null;
        }

        private static RectTransform FindCommonRankPanelRoot(HUDManager hud)
        {
            if (hud == null || hud.playerLevelMeter == null)
                return null;

            if (hud.playerLevelMeter != null)
            {
                RectTransform meter = hud.playerLevelMeter.rectTransform;
                for (Transform current = meter; current != null; current = current.parent)
                {
                    if (!(current is RectTransform rect))
                        continue;

                    if (ContainsTransform(rect, hud.playerLevelMeter)
                        && ContainsTransform(rect, hud.playerLevelText)
                        && ContainsTransform(rect, hud.playerLevelXPCounter))
                    {
                        return rect;
                    }
                }
            }

            return null;
        }

        private static RectTransform FindTextSearchRoot(HUDManager hud, RectTransform fallback)
        {
            if (hud != null && hud.playerLevelText != null)
            {
                for (Transform current = hud.playerLevelText.transform; current != null; current = current.parent)
                {
                    if (current is RectTransform rect
                        && ContainsTransform(rect, hud.playerLevelMeter)
                        && ContainsTransform(rect, hud.playerLevelText)
                        && ContainsTransform(rect, hud.playerLevelXPCounter))
                    {
                        return rect;
                    }
                }
            }

            if (hud != null && hud.transform is RectTransform hudRect)
                return hudRect;

            return fallback;
        }

        private static bool ContainsTransform(Transform root, Component child)
        {
            if (child == null || root == null)
                return true;

            Transform childTransform = child.transform;
            return childTransform == root || childTransform.IsChildOf(root);
        }

        private static TextMeshProUGUI FindMarksCounter(HUDManager hud)
        {
            RectTransform levelUpBox = FindVanillaLevelUpBox(hud);
            return levelUpBox != null ? FindMarksCounter(levelUpBox) : null;
        }

        private static TextMeshProUGUI FindMarksCounter(RectTransform root)
        {
            TextMeshProUGUI[] texts = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TextMeshProUGUI text = texts[i];
                if (text != null && text.gameObject.name == MarksCounterName)
                    return text;
            }

            return null;
        }

        private static TextMeshProUGUI FindEmployeeRankTitleText(RectTransform root, HUDManager hud)
        {
            TextMeshProUGUI[] texts = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TextMeshProUGUI text = texts[i];
                if (text == null || text.gameObject.name == MarksCounterName)
                    continue;

                string value = text.text ?? string.Empty;
                string upper = value.ToUpperInvariant();
                if (upper.Contains("EMPLOYEE") && upper.Contains("RANK"))
                    return text;
            }

            for (int i = 0; i < texts.Length; i++)
            {
                TextMeshProUGUI text = texts[i];
                if (text == null
                    || text == hud.playerLevelText
                    || text == hud.playerLevelXPCounter
                    || text.gameObject.name == MarksCounterName)
                {
                    continue;
                }

                if (text.gameObject.name.IndexOf("rank", StringComparison.OrdinalIgnoreCase) >= 0)
                    return text;
            }

            return hud.playerLevelText != null
                ? hud.playerLevelText
                : hud.playerLevelXPCounter;
        }

        private static void HideMarksCounter(HUDManager hud)
        {
            SetMarksCounter(FindMarksCounter(hud), 0, visible: false);
            SetAllTokenStripsActive(hud, false);
        }

        private static void SetMarksCounter(TextMeshProUGUI counter, int marks, bool visible)
        {
            if (counter == null)
                return;

            bool show = visible && marks > 0;
            GameObject displayRoot = counter.transform.parent != null
                && counter.transform.parent.gameObject.name == MarksStripName
                    ? counter.transform.parent.gameObject
                    : counter.gameObject;
            displayRoot.SetActive(show);
            TokensGrantedLifetime lifetime = displayRoot.GetComponent<TokensGrantedLifetime>();
            if (lifetime != null && show)
                lifetime.Extend(9f);

            if (marks <= 0)
                return;

            counter.text = marks == 1
                ? "<color=#ffff00>1 TOKEN GRANTED</color>"
                : $"<color=#ffff00>{marks} TOKENS GRANTED</color>";
        }

        private static void RemoveMisplacedTokenStrips(Transform expectedParent)
        {
            if (expectedParent == null)
                return;

            RectTransform[] transforms = Resources.FindObjectsOfTypeAll<RectTransform>();
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (!IsTokenStripName(transform.name))
                    continue;

                if (transform.parent == expectedParent)
                    continue;

                transform.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(transform.gameObject);
            }
        }

        private static void SetAllTokenStripsActive(HUDManager hud, bool active)
        {
            RectTransform[] transforms = Resources.FindObjectsOfTypeAll<RectTransform>();
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (IsTokenStripName(transform.name))
                    transform.gameObject.SetActive(active);
            }
        }

        private static bool IsTokenStripName(string name)
        {
            return string.Equals(name, MarksStripName, StringComparison.Ordinal)
                || string.Equals(name, LegacyMarksStripName, StringComparison.Ordinal)
                || string.Equals(name, "Y4NGZ_MarksGrantedExtension", StringComparison.Ordinal);
        }

        private static void EnsureMarksSoundLoading()
        {
            if (_marksGrantedClip != null || _marksGrantedClipLoadStarted)
                return;

            MonoBehaviour host = _coroutineHost != null
                ? _coroutineHost
                : Y4NGZPersistentRunner.Host;
            if (host == null)
                return;

            _marksGrantedClipLoadStarted = true;
            host.StartCoroutine(LoadMarksSoundClip());
        }

        private static IEnumerator LoadMarksSoundClip()
        {
            string path = FindMarksSoundPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                Plugin.Log?.LogWarning("[Progression] Tokens granted sound not found under Assets/UI.");
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log?.LogWarning($"[Progression] Failed to load tokens granted sound '{path}': {request.error}");
                    yield break;
                }

                _marksGrantedClip = DownloadHandlerAudioClip.GetContent(request);
                if (_marksGrantedClip != null)
                    _marksGrantedClip.name = "Y4NGZ_TokensGranted";
            }
        }

        private static string FindMarksSoundPath()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                string direct = Path.Combine(assemblyDir, "Assets", "UI", MarksSoundFileName);
                if (File.Exists(direct))
                    return direct;
            }

            string pluginFolder = Path.Combine(Paths.PluginPath, "Y4NGZUpgrades", "Assets", "UI", MarksSoundFileName);
            return File.Exists(pluginFolder) ? pluginFolder : null;
        }

        private static void PlayMarksGrantedSound(HUDManager hud, int rankUpNumber)
        {
            EnsureMarksSoundLoading();
            if (_marksGrantedClip == null)
                return;

            GameObject obj = new GameObject("Y4NGZ_TokensGrantedSfx");
            UnityEngine.Object.DontDestroyOnLoad(obj);

            AudioSource source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.volume = 0.85f;
            source.pitch = Mathf.Clamp(1f + (Mathf.Max(1, rankUpNumber) - 1) * 0.075f, 1f, 1.55f);
            source.clip = _marksGrantedClip;
            if (hud != null && hud.UIAudio != null)
                source.outputAudioMixerGroup = hud.UIAudio.outputAudioMixerGroup;

            source.Play();
            float lifetime = Mathf.Max(0.1f, _marksGrantedClip.length / Mathf.Max(0.1f, source.pitch)) + 0.25f;
            UnityEngine.Object.Destroy(obj, lifetime);
        }

        private sealed class TokensGrantedLifetime : MonoBehaviour
        {
            internal RectTransform Reference;
            private float _hideAt = -1f;

            private void LateUpdate()
            {
                if (Reference != null && !Reference.gameObject.activeInHierarchy)
                {
                    gameObject.SetActive(false);
                    return;
                }

                if (_hideAt > 0f && Time.realtimeSinceStartup >= _hideAt)
                    gameObject.SetActive(false);
            }

            internal void Extend(float seconds)
            {
                _hideAt = Time.realtimeSinceStartup + Mathf.Max(0.25f, seconds);
            }
        }
    }
}
