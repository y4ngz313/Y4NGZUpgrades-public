using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Rendering;

namespace Y4NGZUpgrades.Effects
{
    // Owner-local, bounded history of positions actually visited. Never pathfinds into unseen
    // rooms, connects teleports, or adds a light to the world. Normal depth testing hides walls.
    internal static class LoneWolfTrail
    {
        private struct Step { internal Vector3 Position; internal float Time; internal bool Inside; }
        private static readonly List<Step> Steps = new List<Step>(121);
        private static readonly List<LineRenderer> Lines = new List<LineRenderer>(120);
        private static Material _material;
        private static float _nextSample;

        internal static void Tick(PlayerControllerB player, bool record, bool show)
        {
            if (!record) { Clear(); return; }
            if (Time.time < _nextSample) return;
            _nextSample = Time.time + 0.5f;
            while (Steps.Count > 0 && Time.time - Steps[0].Time > 60f) Steps.RemoveAt(0);
            Vector3 position = player.transform.position + Vector3.up * 0.08f;
            if (Steps.Count == 0 || Vector3.Distance(Steps[Steps.Count - 1].Position, position) >= 0.8f)
                Steps.Add(new Step { Position = position, Time = Time.time, Inside = player.isInsideFactory });
            while (Steps.Count > 120) Steps.RemoveAt(0);
            if (show && _material == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null) _material = new Material(shader) { name = "LoneWolfReturnTrail" };
            }
            int visible = 0;
            if (show && _material != null)
                for (int i = 1; i < Steps.Count; i++)
                {
                    Step a = Steps[i - 1], b = Steps[i];
                    if (a.Inside != b.Inside || a.Inside != player.isInsideFactory
                        || Vector3.Distance(a.Position, b.Position) > 6f
                        || Vector3.Distance(b.Position, position) > 25f) continue;
                    if (visible >= Lines.Count || Lines[visible] == null)
                    {
                        var line = new GameObject("LoneWolfReturnTrail").AddComponent<LineRenderer>();
                        line.sharedMaterial = _material;
                        line.positionCount = 2;
                        line.useWorldSpace = true;
                        line.widthMultiplier = 0.035f;
                        line.shadowCastingMode = ShadowCastingMode.Off;
                        line.receiveShadows = false;
                        if (visible >= Lines.Count) Lines.Add(line);
                        else Lines[visible] = line;
                    }
                    var segment = Lines[visible++];
                    segment.gameObject.SetActive(true);
                    segment.SetPosition(0, a.Position);
                    segment.SetPosition(1, b.Position);
                    float alpha = Mathf.Lerp(0.2f, 0.65f, Mathf.Clamp01(1f - (Time.time - b.Time) / 60f));
                    segment.startColor = segment.endColor = new Color(0.2f, 0.85f, 0.9f, alpha);
                }
            for (int i = visible; i < Lines.Count; i++) if (Lines[i] != null) Lines[i].gameObject.SetActive(false);
        }

        internal static void Clear()
        {
            Steps.Clear();
            _nextSample = 0f;
            foreach (var line in Lines) if (line != null) line.gameObject.SetActive(false);
        }

        internal static void Destroy()
        {
            Clear();
            foreach (var line in Lines) if (line != null) Object.Destroy(line.gameObject);
            Lines.Clear();
            if (_material != null) Object.Destroy(_material);
            _material = null;
        }
    }
}
