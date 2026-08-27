using System;
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    /// <summary>
    /// Moon-independent lookup for a loaded vanilla enemy prefab, so effects can scavenge assets off
    /// creatures the current moon never spawns. The quick menu's test level enumerates every
    /// EnemyType in the game, which is why it is tried first; the loaded-object sweep is the last
    /// resort because it walks everything.
    /// </summary>
    internal static class VanillaEnemyPrefabs
    {
        /// <summary>
        /// Returns the first loaded prefab carrying <typeparamref name="T"/> that <paramref name="usable"/>
        /// accepts, or null. Callers throttle their own retries - nothing here is cached.
        /// </summary>
        internal static T Find<T>(Func<T, bool> usable) where T : Component
        {
            if (usable == null)
                return null;

            QuickMenuManager menu = UnityEngine.Object.FindObjectOfType<QuickMenuManager>();
            T fromTestLevel = FindInLevel(menu != null ? menu.testAllEnemiesLevel : null, usable);
            if (fromTestLevel != null)
                return fromTestLevel;

            StartOfRound round = StartOfRound.Instance;
            if (round != null && round.levels != null)
            {
                for (int i = 0; i < round.levels.Length; i++)
                {
                    T fromLevel = FindInLevel(round.levels[i], usable);
                    if (fromLevel != null)
                        return fromLevel;
                }
            }

            T[] loaded = Resources.FindObjectsOfTypeAll<T>();
            T sceneFallback = null;
            for (int i = 0; i < loaded.Length; i++)
            {
                T candidate = loaded[i];
                if (candidate == null || !usable(candidate))
                    continue;

                // A prefab asset lives outside any scene. Prefer it: a live creature standing on the
                // moon may already have toggled the very thing we want to copy.
                if (!candidate.gameObject.scene.IsValid())
                    return candidate;
                if (sceneFallback == null)
                    sceneFallback = candidate;
            }

            return sceneFallback;
        }

        private static T FindInLevel<T>(SelectableLevel level, Func<T, bool> usable) where T : Component
        {
            if (level == null)
                return null;

            T found = FindInSpawnList(level.Enemies, usable);
            if (found != null)
                return found;
            found = FindInSpawnList(level.OutsideEnemies, usable);
            if (found != null)
                return found;
            return FindInSpawnList(level.DaytimeEnemies, usable);
        }

        private static T FindInSpawnList<T>(List<SpawnableEnemyWithRarity> spawns, Func<T, bool> usable)
            where T : Component
        {
            if (spawns == null)
                return null;

            for (int i = 0; i < spawns.Count; i++)
            {
                SpawnableEnemyWithRarity spawn = spawns[i];
                GameObject prefab = spawn != null && spawn.enemyType != null ? spawn.enemyType.enemyPrefab : null;
                if (prefab == null)
                    continue;

                T candidate = prefab.GetComponent<T>();
                if (candidate != null && usable(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
