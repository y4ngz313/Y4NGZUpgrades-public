using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Postfix on PlayerControllerB.DamagePlayer that, for the local player only, identifies
    /// the most likely attacking enemy (recently-recorded attacker, fall back to nearest
    /// living enemy within melee range) and reflects a shovel-class hit back at it. Tier 2
    /// also applies a 1-second stagger via SetEnemyStunned. Spawns a brief orange spark
    /// light at the enemy + plays a salvaged shock/zap clip if one can be found in the
    /// game's loaded audio.
    /// </summary>
    [HarmonyPatch]
    internal static class RetaliationPatch
    {
        // Maximum range (m) at which we'll consider an enemy a candidate attacker via
        // proximity fallback. ~8m comfortably covers melee reach for vanilla enemies
        // (bracken/crawler/dog/etc.) without picking up obviously-uninvolved enemies.
        private const float MELEE_RANGE = 8f;

        // Maximum range we'll trust the recent-attacker tracker for. Slightly larger than
        // MELEE_RANGE because the recorded enemy may have moved a step between recording
        // the attack and DamagePlayer firing.
        private const float TRACKER_VALIDITY_RANGE = 15f;

        // How long after a recorded attack we still trust the tracker. 0.5s lines up with
        // the typical attack-then-damage delay across vanilla enemies.
        private const float TRACKER_VALIDITY_WINDOW = 0.5f;

        // Spark visual lifetimes -- Tier 1 is brief, Tier 2 lingers a touch longer to
        // sell the heavier "stagger" feedback.
        private const float SPARK_DURATION_T1 = 0.15f;
        private const float SPARK_DURATION_T2 = 0.30f;

        // Cached reference to a shock/zap/electric/spark AudioClip resolved on first hit.
        // Resources.FindObjectsOfTypeAll is expensive enough that we only want to do the
        // search once per session.
        private static AudioClip _sparkClip;
        private static bool _sparkClipSearched;

        // Last enemy that was recorded as "about to hit a player" via our broad enemy
        // attack patches. Cleared implicitly by the validity-window check.
        private static EnemyAI _lastAttacker;
        private static float _lastAttackerTime = -1f;

        // -------------------------------------------------------------------
        // ATTACKER TRACKER
        // -------------------------------------------------------------------

        /// <summary>
        /// Records the given enemy as the most recent attacker. Called from broad enemy
        /// attack-method prefixes below. We keep only the latest because most damage
        /// events are dispatched within ~1 frame of the attack hookup.
        /// </summary>
        private static void RecordAttacker(EnemyAI enemy)
        {
            if (enemy == null) return;
            _lastAttacker = enemy;
            _lastAttackerTime = Time.time;
        }

        /// <summary>
        /// Patches the most common enemy "I just damaged a player" entry points. We
        /// deliberately keep this list short and use the proximity fallback for everything
        /// else -- LC has many enemy classes and we don't want to maintain a full table.
        /// </summary>
        [HarmonyPatch(typeof(EnemyAI), "OnCollideWithPlayer")]
        [HarmonyPrefix]
        private static void PreEnemyAIOnCollideWithPlayer(EnemyAI __instance, Collider other)
        {
            // OnCollideWithPlayer fires on ALL enemies that handle player collision damage
            // (crawler, baboon hawk, etc.). We can record without checking what the method
            // is going to do -- the validity window will discard us if no DamagePlayer fires.
            if (other == null) return;
            PlayerControllerB hit = other.GetComponent<PlayerControllerB>();
            if (hit == null) return;
            if (hit != GameNetworkManager.Instance?.localPlayerController) return;
            RecordAttacker(__instance);
        }

        /// <summary>
        /// Returns the most recent attacker if it's still within validity window AND
        /// reasonably close to the player (avoids spurious matches against an enemy that
        /// hit the player and then ran far away before DamagePlayer fired).
        /// </summary>
        private static EnemyAI GetRecentAttacker(PlayerControllerB player)
        {
            if (_lastAttacker == null) return null;
            if (Time.time - _lastAttackerTime > TRACKER_VALIDITY_WINDOW) return null;
            if (_lastAttacker.isEnemyDead) return null;
            if (Vector3.Distance(player.transform.position, _lastAttacker.transform.position) > TRACKER_VALIDITY_RANGE) return null;
            return _lastAttacker;
        }

        /// <summary>
        /// Falls back to the nearest living enemy within melee range. This is the safety
        /// net for any enemy class we didn't manage to patch a tracker into.
        /// </summary>
        private static EnemyAI FindClosestEnemy(PlayerControllerB player)
        {
            EnemyAI closest = null;
            float closestDist = MELEE_RANGE;
            foreach (EnemyAI enemy in Object.FindObjectsOfType<EnemyAI>())
            {
                if (enemy == null || enemy.isEnemyDead) continue;
                float dist = Vector3.Distance(player.transform.position, enemy.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = enemy;
                }
            }
            return closest;
        }

        // -------------------------------------------------------------------
        // DAMAGE REFLECT
        // -------------------------------------------------------------------

        /// <summary>
        /// Postfix on DamagePlayer. Filters out fall damage / non-enemy sources / dead
        /// players, identifies an attacker, then applies tier-appropriate reflect damage,
        /// optional Tier 2 stagger, and the spark visual + audio feedback.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPostfix]
        private static void PostDamagePlayer(PlayerControllerB __instance, int damageNumber, bool fallDamage, CauseOfDeath causeOfDeath)
        {
            // Local player only -- Retaliation is a per-player upgrade and reflecting
            // remote players' hits would double-count damage against the same enemy.
            if (__instance != GameNetworkManager.Instance?.localPlayerController) return;
            if (fallDamage) return;
            if (damageNumber <= 0) return;
            if (__instance.isPlayerDead) return;

            // Filter out clearly non-enemy damage causes. Anything not in this allow-list
            // is treated as "something else hurt me" (gunshot, fire, etc.) and skipped.
            if (!IsEnemyCause(causeOfDeath)) return;

            if (!RetaliationUpgrade.HasMergedRetaliation()) return;

            EnemyAI attacker = GetRecentAttacker(__instance) ?? FindClosestEnemy(__instance);
            if (attacker == null) return;

            // HitEnemyOnLocalClient with force=1 mirrors a basic shovel hit. The third
            // arg (PlayerControllerB) is the attributed attacker so kills are credited
            // properly. playHitSFX=false because we play our own zap clip below.
            attacker.HitEnemyOnLocalClient(1, __instance.transform.forward, __instance, false, -1);

            if (!attacker.isEnemyDead)
                attacker.SetEnemyStunned(true, RetaliationUpgrade.TIER2_STUN_SECONDS, __instance);

            SpawnSparkEffect(attacker, tier2: true, SPARK_DURATION_T2);
        }

        /// <summary>
        /// Whitelist of CauseOfDeath values that we treat as "an enemy did this". Anything
        /// else (Gunshots, Burning, etc.) we leave alone to avoid spurious reflects.
        /// </summary>
        private static bool IsEnemyCause(CauseOfDeath cause)
        {
            switch (cause)
            {
                case CauseOfDeath.Mauling:
                case CauseOfDeath.Bludgeoning:
                case CauseOfDeath.Strangulation:
                case CauseOfDeath.Stabbing:
                case CauseOfDeath.Crushing:
                case CauseOfDeath.Suffocation:
                    return true;
                default:
                    return false;
            }
        }

        // -------------------------------------------------------------------
        // SPARK VISUAL + AUDIO
        // -------------------------------------------------------------------

        /// <summary>
        /// Spawns a brief orange (Tier 1) or warmer-white (Tier 2) point light at the
        /// enemy's position and plays a salvaged shock/zap clip if available. Light is
        /// auto-destroyed after `duration` seconds.
        /// </summary>
        private static void SpawnSparkEffect(EnemyAI enemy, bool tier2, float duration)
        {
            Vector3 pos = enemy.transform.position + Vector3.up * 1f;

            GameObject flashGo = new GameObject("Y4NGZ_RetaliationFlash");
            flashGo.transform.position = pos;
            Light light = flashGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = tier2 ? new Color(1f, 0.9f, 0.6f) : new Color(1f, 0.7f, 0.2f);
            light.intensity = tier2 ? 12f : 8f;
            light.range = tier2 ? 7f : 5f;
            light.shadows = LightShadows.None;
            Object.Destroy(flashGo, duration);

            AudioClip clip = ResolveSparkClip();
            if (clip != null)
            {
                AudioSource.PlayClipAtPoint(clip, pos, tier2 ? 0.8f : 0.6f);
            }
        }

        /// <summary>
        /// Lazy one-time scan through loaded AudioClips for something that sounds like a
        /// spark/zap. We cache the result (including a null) so we never re-scan.
        /// </summary>
        private static AudioClip ResolveSparkClip()
        {
            if (_sparkClipSearched) return _sparkClip;
            _sparkClipSearched = true;

            AudioClip[] clips = Resources.FindObjectsOfTypeAll<AudioClip>();
            foreach (AudioClip c in clips)
            {
                if (c == null) continue;
                string n = c.name.ToLowerInvariant();
                if (n.Contains("shock") || n.Contains("zap") || n.Contains("spark") || n.Contains("electric"))
                {
                    _sparkClip = c;
                    return _sparkClip;
                }
            }
            // Secondary fallback -- something punchy enough to feel like a hit.
            foreach (AudioClip c in clips)
            {
                if (c == null) continue;
                string n = c.name.ToLowerInvariant();
                if (n.Contains("impact") || n.Contains("hit") || n.Contains("zap"))
                {
                    _sparkClip = c;
                    return _sparkClip;
                }
            }
            return null;
        }
    }
}
