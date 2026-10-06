using System.Diagnostics;
using System.Globalization;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Per-frame cost of the Field Tablet, split the way the external profiler cannot: it books
    /// the whole tablet postfix inside <c>PlayerControllerB.Update</c>. Accumulates
    /// <see cref="Stopwatch.GetTimestamp"/> deltas with no allocation per frame and writes one
    /// Debug line every ten seconds of real time, built only at log time.
    /// </summary>
    internal static class FieldTabletPerfMeter
    {
        internal enum Section
        {
            RtMeasure,
            Scan,
            Input,
            Reticle,
            // Inclusive of Texture and Radar, which run inside it; the log subtracts them.
            Views,
            Texture,
            Radar,
            Count
        }

        private const int WindowSeconds = 10;
        private static readonly long WindowTicks = Stopwatch.Frequency * WindowSeconds;
        private static readonly long[] SectionTicks = new long[(int)Section.Count];

        private static long _windowStartedAt;
        private static long _pendingPatchTicks;
        private static int _stowedFrames;
        private static long _stowedPatchTicks;
        private static int _heldFrames;
        private static long _heldPatchTicks;
        private static long _screenTicks;
        private static int _textureRenders;
        private static int _radarRenders;

        internal static long Timestamp() => Stopwatch.GetTimestamp();

        /// <summary>Books the time since <paramref name="startedAt"/> to a section and returns now.</summary>
        internal static long Lap(Section section, long startedAt)
        {
            long now = Stopwatch.GetTimestamp();
            SectionTicks[(int)section] += now - startedAt;
            return now;
        }

        internal static void AddScreenFrame(long startedAt)
        {
            _screenTicks += Stopwatch.GetTimestamp() - startedAt;
        }

        internal static void AddTextureRender(long startedAt)
        {
            Lap(Section.Texture, startedAt);
            _textureRenders++;
        }

        internal static void AddRadarRender(long startedAt)
        {
            Lap(Section.Radar, startedAt);
            _radarRenders++;
        }

        /// <summary>The Update postfix's share; the LateUpdate postfix closes the frame.</summary>
        internal static void AddPatchUpdate(long startedAt)
        {
            _pendingPatchTicks += Stopwatch.GetTimestamp() - startedAt;
        }

        /// <summary>
        /// Closes the local player's frame from the LateUpdate postfix. Only the local player's
        /// postfixes call in, so frames without a local player are never counted.
        /// </summary>
        internal static void EndPatchFrame(long lateUpdateStartedAt, bool held)
        {
            long now = Stopwatch.GetTimestamp();
            long frameTicks = _pendingPatchTicks + (now - lateUpdateStartedAt);
            _pendingPatchTicks = 0;
            if (held)
            {
                _heldFrames++;
                _heldPatchTicks += frameTicks;
            }
            else
            {
                _stowedFrames++;
                _stowedPatchTicks += frameTicks;
            }

            if (_windowStartedAt == 0)
            {
                _windowStartedAt = now;
                return;
            }

            long elapsed = now - _windowStartedAt;
            if (elapsed < WindowTicks)
                return;

            if (_stowedFrames + _heldFrames > 0)
                Plugin.Log?.LogDebug(BuildWindowLine(elapsed));
            Reset(now);
        }

        private static string BuildWindowLine(long elapsedTicks)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string stowed = string.Format(c, "stowed {0}f {1:0.000} ms/f", _stowedFrames, PerFrame(_stowedPatchTicks, _stowedFrames));
            string held = _heldFrames == 0
                ? "held 0f"
                : string.Format(c,
                    "held {0}f patch {1:0.000} ms/f screen {2:0.000} ms/f (rtMeasure {3:0.00}, scan {4:0.00}, input {5:0.00}, reticle {6:0.00}, views {7:0.00}, texture {8:0.00} ms/f over {9} renders, radar {10:0.00} ms/f over {11} renders)",
                    _heldFrames,
                    PerFrame(_heldPatchTicks, _heldFrames),
                    PerFrame(_screenTicks, _heldFrames),
                    PerFrame(SectionTicks[(int)Section.RtMeasure], _heldFrames),
                    PerFrame(SectionTicks[(int)Section.Scan], _heldFrames),
                    PerFrame(SectionTicks[(int)Section.Input], _heldFrames),
                    PerFrame(SectionTicks[(int)Section.Reticle], _heldFrames),
                    PerFrame(ExclusiveViewTicks(), _heldFrames),
                    PerFrame(SectionTicks[(int)Section.Texture], _heldFrames),
                    _textureRenders,
                    PerFrame(SectionTicks[(int)Section.Radar], _heldFrames),
                    _radarRenders);
            return string.Format(c, "[Field Tablet] perf {0:0}s: {1} | {2}",
                elapsedTicks / (double)Stopwatch.Frequency, stowed, held);
        }

        private static long ExclusiveViewTicks()
        {
            long ticks = SectionTicks[(int)Section.Views] - SectionTicks[(int)Section.Texture] - SectionTicks[(int)Section.Radar];
            return ticks > 0 ? ticks : 0;
        }

        // Same conversion as BetterArmory's WeaponVfxRuntimeAssets.ElapsedMs.
        private static double PerFrame(long ticks, int frames)
            => frames > 0 ? ticks * 1000d / Stopwatch.Frequency / frames : 0d;

        private static void Reset(long now)
        {
            _windowStartedAt = now;
            _stowedFrames = 0;
            _stowedPatchTicks = 0;
            _heldFrames = 0;
            _heldPatchTicks = 0;
            _screenTicks = 0;
            _textureRenders = 0;
            _radarRenders = 0;
            for (int i = 0; i < SectionTicks.Length; i++)
                SectionTicks[i] = 0;
        }
    }
}
