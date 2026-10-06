using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Upgrades;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Y4NGZUpgrades.Patches
{
    internal static partial class CourierDronePatch
    {
        /// <summary>
        /// #500 perf, first PostPlayerUpdate call of each frame. Rolls the 10 s window and decides
        /// whether this frame is timed: Debug logging on and a drone in the world. The frame counts
        /// as combat while <see cref="_droneCombatActiveUntil"/> is ahead, otherwise as idle.
        /// </summary>
        private static void BeginDronePerfFrame()
        {
            _dronePerfLastFrame = Time.frameCount;
            float now = Time.realtimeSinceStartup;
            if (now >= _dronePerfWindowEndsAt)
                RollDronePerfWindow(now);

            _dronePerfTiming = false;
            if (!_dronePerfEnabled || _droneVisual == null || _mode == DroneMode.NotSpawned)
                return;

            int state = Time.time < _droneCombatActiveUntil ? PERF_STATE_COMBAT : PERF_STATE_IDLE;
            _dronePerfStateOffset = state * PERF_BUCKET_COUNT;
            DronePerfFrames[state]++;
            _dronePerfTiming = true;
        }

        private static void AddDronePerfTicks(int bucket, long startedTimestamp)
        {
            DronePerfTicks[_dronePerfStateOffset + bucket] += Stopwatch.GetTimestamp() - startedTimestamp;
        }

        private static void MarkDroneCombatActive()
        {
            _droneCombatActiveUntil = Time.time + DRONE_COMBAT_ACTIVE_SECONDS;
        }

        private static void RollDronePerfWindow(float now)
        {
            if (_dronePerfEnabled && (DronePerfFrames[PERF_STATE_IDLE] > 0 || DronePerfFrames[PERF_STATE_COMBAT] > 0))
                Plugin.Log?.LogDebug(BuildDronePerfLine(now - _dronePerfWindowStartedAt));

            Array.Clear(DronePerfTicks, 0, DronePerfTicks.Length);
            Array.Clear(DronePerfFrames, 0, DronePerfFrames.Length);
            _dronePerfWindowStartedAt = now;
            _dronePerfWindowEndsAt = now + DRONE_PERF_WINDOW_SECONDS;
            _dronePerfEnabled = IsBepInExDiskDebugLogEnabled();
        }

        /// <summary>True when a BepInEx disk log listener shows Debug (BepInEx.cfg [Logging.Disk] LogLevels).</summary>
        private static bool IsBepInExDiskDebugLogEnabled()
        {
            try
            {
                foreach (BepInEx.Logging.ILogListener listener in BepInEx.Logging.Logger.Listeners)
                {
                    if (listener is BepInEx.Logging.DiskLogListener disk
                        && (disk.DisplayedLogLevel & BepInEx.Logging.LogLevel.Debug) != 0)
                        return true;
                }
            }
            catch (Exception)
            {
                // The listener set changed mid-enumeration; the next window asks again.
            }

            return false;
        }

        private static string BuildDronePerfLine(float windowSeconds)
        {
            var line = new System.Text.StringBuilder(192);
            line.Append("[CourierDronePerf] window ")
                .Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1}", windowSeconds))
                .Append('s');
            AppendDronePerfState(line, "idle", PERF_STATE_IDLE);
            AppendDronePerfState(line, "combat", PERF_STATE_COMBAT);
            return line.ToString();
        }

        private static void AppendDronePerfState(System.Text.StringBuilder line, string label, int state)
        {
            int frames = DronePerfFrames[state];
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            line.Append(" | ").Append(label).Append(' ').Append(frames).Append('f');
            for (int bucket = 0; bucket < PERF_BUCKET_COUNT; bucket++)
            {
                double msPerFrame = frames > 0
                    ? DronePerfTicks[state * PERF_BUCKET_COUNT + bucket] * msPerTick / frames
                    : 0.0;
                line.Append(' ')
                    .Append(DronePerfBucketNames[bucket])
                    .Append(' ')
                    .Append(msPerFrame.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }

            line.Append(" ms/f");
        }
    }
}
