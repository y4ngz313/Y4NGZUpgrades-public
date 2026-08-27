using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Hooks PlayerControllerB.KillPlayer for the local player and, if Salvager is
    /// owned at Tier 2+ and the death is inside the facility, spawns:
    ///   * A pulsing red point light at the death position (Salvager T2+).
    ///   * A persistent HUD skull marker pinned to that world position (Salvager T2+).
    ///   * A blue ghost trail rendered between the facility entrance and the death
    ///     position via a LineRenderer pathed through NavMesh (Salvager T3).
    ///
    /// All visuals are local-only - they're spawned on whatever client triggered the
    /// patch, so teammates won't see them unless they also own the upgrade. Trying to
    /// network-sync these would require a NetworkBehaviour prefab and a server RPC,
    /// which is out of scope for this iteration.
    ///
    /// Visuals persist until StartOfRound.OnShipLeave (round end), at which point
    /// BeaconPulse coroutine self-terminates because its host GameObject is destroyed
    /// alongside the level scene.
    /// </summary>
    [HarmonyPatch]
    internal static class LastWillPatch
    {
        private const string MSG_LAST_WILL_BEACON = "Y4NGZ_LastWill_Beacon";

        // We only allow one beacon at a time per local client. Spawning a second would
        // happen if the player somehow died twice without a round reset; we just replace
        // the old one rather than stacking lights.
        private static GameObject _activeBeacon;
        private static bool _handlersRegistered;

        private const float BEACON_PULSE_PERIOD = 1.4f;   // seconds per pulse
        private const float BEACON_INTENSITY_MIN = 2f;
        private const float BEACON_INTENSITY_MAX = 9f;
        private const float BEACON_RANGE = 14f;

        private const float TRAIL_REFRESH_AFTER = 30f;    // re-path once for late-comers
        private const float TRAIL_WIDTH = 0.18f;

        // -----------------------------------------------------------------------
        // 1) DEATH HOOK
        // -----------------------------------------------------------------------

        /// <summary>
        /// Postfix on KillPlayer. We use the post-state so PlayerControllerB.isPlayerDead
        /// is already true and the corpse position has been finalized. We only act for
        /// the local owning player; remote players' deaths fire their own KillPlayer
        /// invocations on each client and we don't want to spam beacons.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPostfix]
        private static void PostKillPlayer(PlayerControllerB __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!__instance.IsOwner) return;
                if (__instance != GameNetworkManager.Instance?.localPlayerController) return;
                if (!SalvagerUpgrade.HasTier(SalvagerUpgrade.TIER_BEACON)) return;

                // Don't drop beacons aboard the ship - pointless, and confuses HUD.
                if (__instance.isInHangarShipRoom) return;
                if (!__instance.isInsideFactory) return;

                Vector3 deathPos = __instance.transform.position;
                int tier = SalvagerUpgrade.GetTier();
                SpawnBeacon(deathPos, tier);
                BroadcastBeacon(deathPos, tier);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"LastWillPatch.PostKillPlayer failed: {e}");
            }
        }

        // -----------------------------------------------------------------------
        // 2) BEACON SPAWN + PULSE
        // -----------------------------------------------------------------------

        private static void SpawnBeacon(Vector3 pos, int tier)
        {
            // Replace any prior beacon (e.g., second death same round).
            if (_activeBeacon != null) Object.Destroy(_activeBeacon);

            GameObject host = new GameObject("Y4NGZ_LastWill_Beacon");
            host.transform.position = pos + Vector3.up * 0.4f;

            Light l = host.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.2f, 0.2f);
            l.intensity = BEACON_INTENSITY_MIN;
            l.range = BEACON_RANGE;
            l.shadows = LightShadows.None;

            BeaconPulse pulser = host.AddComponent<BeaconPulse>();
            pulser.Init(l, BEACON_INTENSITY_MIN, BEACON_INTENSITY_MAX, BEACON_PULSE_PERIOD);

            // HUD skull marker - we keep this lightweight: an UI overlay would require
            // injecting into HUDManager's canvas. Instead we use Unity's built-in label
            // by piggybacking on the existing OnGUI tooltip pattern via a small driver.
            host.AddComponent<HudMarkerDriver>().Init(pos, "X");

            _activeBeacon = host;

            if (tier >= SalvagerUpgrade.TIER_TRAIL)
            {
                Vector3 entrance = ResolveEntrancePosition(pos);
                if (entrance != Vector3.zero)
                    _activeBeacon.AddComponent<TrailDriver>().Init(entrance, pos);
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        /// <summary>
        /// Netcode nulls the CustomMessagingManager on shutdown and builds a fresh one for the next
        /// host/join, so this latch has to drop or the second lobby of a session never receives
        /// death beacons.
        /// </summary>
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            _handlersRegistered = false;
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered) return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;

            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                    MSG_LAST_WILL_BEACON, OnReceiveBeacon);
                _handlersRegistered = true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"LastWillPatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

#pragma warning disable Harmony003 // Custom message serializers mutate FastBufferReader/FastBufferWriter by design.
        private const int BEACON_PAYLOAD_BYTES = sizeof(float) * 3 + sizeof(int);

        /// <summary>
        /// Netcode has no client-to-all primitive - SendNamedMessageToAll reads ConnectedClientsIds,
        /// whose getter throws off-host. The host broadcasts; a client sends to the host, which
        /// relays. This is called from a KillPlayer postfix, so nothing may escape.
        /// </summary>
        private static void BroadcastBeacon(Vector3 pos, int tier)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            FastBufferWriter writer = new FastBufferWriter(BEACON_PAYLOAD_BYTES, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(pos.x);
                writer.WriteValueSafe(pos.y);
                writer.WriteValueSafe(pos.z);
                writer.WriteValueSafe(tier);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_LAST_WILL_BEACON, writer);
                else
                    messaging.SendNamedMessage(MSG_LAST_WILL_BEACON, NetworkManager.ServerClientId, writer);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"LastWillPatch: beacon send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>Host-only re-broadcast of a client's death beacon to every other client.</summary>
        private static void RelayBeacon(ulong excludeClientId, Vector3 pos, int tier)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(BEACON_PAYLOAD_BYTES, Allocator.Temp);
                try
                {
                    writer.WriteValueSafe(pos.x);
                    writer.WriteValueSafe(pos.y);
                    writer.WriteValueSafe(pos.z);
                    writer.WriteValueSafe(tier);
                    messaging.SendNamedMessage(MSG_LAST_WILL_BEACON, clientId, writer);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogWarning($"LastWillPatch: beacon relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        private static void OnReceiveBeacon(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                NetworkManager network = NetworkManager.Singleton;
                ulong localClientId = network != null ? network.LocalClientId : ulong.MaxValue;
                if (senderClientId == localClientId) return;

                float x, y, z;
                int tier;
                reader.ReadValueSafe(out x);
                reader.ReadValueSafe(out y);
                reader.ReadValueSafe(out z);
                reader.ReadValueSafe(out tier);

                // The payload carries no player identity - only a corpse position - so there is
                // nothing to re-derive from the sender; the host just fans it out.
                if (network != null && network.IsServer && senderClientId != network.LocalClientId)
                    RelayBeacon(senderClientId, new Vector3(x, y, z), tier);

                SpawnBeacon(new Vector3(x, y, z), tier);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"LastWillPatch: malformed beacon sync: {ex.Message}");
            }
        }
#pragma warning restore Harmony003

        // -----------------------------------------------------------------------
        // 3) ENTRANCE RESOLUTION (TIER 2 TRAIL ANCHOR)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Best-effort guess at the facility entrance position. RoundManager exposes
        /// a list of EntranceTeleports; we want the inside-the-facility side. If
        /// none can be resolved we return Vector3.zero and the caller skips the trail.
        /// </summary>
        private static Vector3 ResolveEntrancePosition(Vector3 deathPos)
        {
            EntranceTeleport[] teleports = Object.FindObjectsOfType<EntranceTeleport>();
            if (teleports == null || teleports.Length == 0) return Vector3.zero;

            // EntranceTeleport.isEntranceToBuilding == false means we're on the
            // inside-the-facility side, which is what we want as the trail start.
            EntranceTeleport best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < teleports.Length; i++)
            {
                EntranceTeleport t = teleports[i];
                if (t == null) continue;
                if (t.isEntranceToBuilding) continue;
                float d = Vector3.Distance(t.entrancePoint != null ? t.entrancePoint.position : t.transform.position, deathPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = t;
                }
            }

            if (best == null) return Vector3.zero;
            return best.entrancePoint != null ? best.entrancePoint.position : best.transform.position;
        }

        // -----------------------------------------------------------------------
        // 4) BEACON PULSE BEHAVIOUR (sin-wave intensity)
        // -----------------------------------------------------------------------

        /// <summary>
        /// MonoBehaviour driver that breathes the beacon's intensity in a sin wave.
        /// Self-cleans by virtue of being attached to the beacon GameObject.
        /// </summary>
        private class BeaconPulse : MonoBehaviour
        {
            private Light _light;
            private float _min;
            private float _max;
            private float _period;
            private float _startTime;

            public void Init(Light light, float min, float max, float period)
            {
                _light = light;
                _min = min;
                _max = max;
                _period = Mathf.Max(0.05f, period);
                _startTime = Time.time;
            }

            private void Update()
            {
                if (_light == null) return;
                float t = (Time.time - _startTime) / _period;
                float s = 0.5f + 0.5f * Mathf.Sin(t * 2f * Mathf.PI);
                _light.intensity = Mathf.Lerp(_min, _max, s);
            }
        }

        // -----------------------------------------------------------------------
        // 5) HUD MARKER (OnGUI overlay)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders a small text label at the world-projected screen position of
        /// the death spot, every frame. Cheap, no Canvas wiring required.
        /// </summary>
        private class HudMarkerDriver : MonoBehaviour
        {
            private Vector3 _worldPos;
            private string _glyph;
            private GUIStyle _style;

            public void Init(Vector3 worldPos, string glyph)
            {
                _worldPos = worldPos;
                _glyph = glyph;
            }

            private void OnGUI()
            {
                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label);
                    _style.fontSize = 22;
                    _style.fontStyle = FontStyle.Bold;
                    _style.alignment = TextAnchor.MiddleCenter;
                    _style.normal.textColor = new Color(1f, 0.25f, 0.25f, 0.9f);
                }

                PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
                Camera cam = localPlayer != null && localPlayer.gameplayCamera != null
                    ? localPlayer.gameplayCamera
                    : Camera.main;
                if (cam == null) return;
                Vector3 sp = cam.WorldToScreenPoint(_worldPos);
                if (sp.z <= 0f) return; // behind camera
                float x = sp.x - 16f;
                float y = (Screen.height - sp.y) - 16f;
                GUI.Label(new Rect(x, y, 32f, 32f), _glyph, _style);
            }
        }

        // -----------------------------------------------------------------------
        // 6) TIER 2 TRAIL DRIVER (NavMesh-pathed LineRenderer)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Computes a NavMesh path between the entrance and the death position
        /// and renders it as a blue LineRenderer. Re-paths once after a delay to
        /// give late-arriving teammates the corrected trail in case the initial
        /// NavMesh sample was sparse during scene load.
        /// </summary>
        private class TrailDriver : MonoBehaviour
        {
            private Vector3 _start;
            private Vector3 _end;
            private LineRenderer _line;
            private float _nextRepathAt;
            private bool _repathed;

            public void Init(Vector3 start, Vector3 end)
            {
                _start = start;
                _end = end;
                BuildLine();
                Repath();
                _nextRepathAt = Time.time + TRAIL_REFRESH_AFTER;
            }

            private void BuildLine()
            {
                GameObject lineHost = new GameObject("Y4NGZ_LastWill_Trail");
                lineHost.transform.SetParent(transform, worldPositionStays: false);
                _line = lineHost.AddComponent<LineRenderer>();
                _line.startWidth = TRAIL_WIDTH;
                _line.endWidth = TRAIL_WIDTH;
                _line.useWorldSpace = true;
                _line.material = new Material(Shader.Find("Sprites/Default"));
                _line.startColor = new Color(0.3f, 0.5f, 1f, 0.9f);
                _line.endColor = new Color(0.3f, 0.5f, 1f, 0.4f);
                _line.numCornerVertices = 2;
            }

            private void Update()
            {
                if (_repathed) return;
                if (Time.time < _nextRepathAt) return;
                Repath();
                _repathed = true;
            }

            private void Repath()
            {
                if (_line == null) return;

                NavMeshPath path = new NavMeshPath();
                NavMeshHit startHit, endHit;
                if (!NavMesh.SamplePosition(_start, out startHit, 5f, NavMesh.AllAreas)) { Fallback(); return; }
                if (!NavMesh.SamplePosition(_end,   out endHit,   5f, NavMesh.AllAreas)) { Fallback(); return; }
                if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path) ||
                    path.status != NavMeshPathStatus.PathComplete)
                {
                    Fallback();
                    return;
                }

                Vector3[] corners = path.corners;
                _line.positionCount = corners.Length;
                for (int i = 0; i < corners.Length; i++)
                    _line.SetPosition(i, corners[i] + Vector3.up * 0.1f);
            }

            /// <summary>Straight-line fallback when NavMesh is unavailable (e.g., on the surface).</summary>
            private void Fallback()
            {
                _line.positionCount = 2;
                _line.SetPosition(0, _start + Vector3.up * 0.1f);
                _line.SetPosition(1, _end + Vector3.up * 0.1f);
            }
        }
    }
}
