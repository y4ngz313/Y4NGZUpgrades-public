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
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.DoAIInterval))]
        [HarmonyPostfix]
        private static void PostEnemyDoAIInterval(EnemyAI __instance)
        {
            try
            {
                if (!IsServerAuthority() || __instance == null || __instance.isEnemyDead || !CanDroneBeDamaged())
                    return;
                // #500: only enemies with a drone attack are pulled toward it, and a lunging dog keeps
                // the destination its own EnterLunge set (MouthDogAI.cs:516-520).
                DroneAttacker attacker = ResolveDroneAttacker(__instance);
                if (attacker == DroneAttacker.None)
                    return;
                if (attacker == DroneAttacker.MouthDog && ((MouthDogAI)__instance).inLunge)
                    return;
                // F-DRONE-7: bare proximity used to be enough to hijack an enemy, so an enemy walking
                // at the crew was re-targeted between the player and the drone every AI interval.
                // Only an enemy the drone attacked, or one a pointer order named, may be pulled.
                if (!EnemySharesDroneSpace(__instance) || !IsDroneAttackThreat(__instance))
                    return;

                Vector3 dronePosition = GetDroneTargetPosition();
                if (dronePosition == Vector3.zero)
                    return;
                if (PlayerHasPriorityOverDrone(__instance, dronePosition))
                    return;

                // targetPlayer is deliberately left alone: several vanilla AIs read it from their own
                // Update between intervals, and nulling it from outside is what produced NREs inside
                // vanilla code. SetDestinationToPosition alone pulls the enemy for this interval.
                SteerEnemyTowardDrone(__instance);
            }
            catch (Exception ex)
            {
                string enemyName = __instance != null ? __instance.GetType().Name : "<null>";
                Plugin.Log?.LogDebug($"Courier Drone enemy DoAIInterval postfix skipped for {enemyName}: {ex.Message}");
            }
        }

        private static bool PlayerHasPriorityOverDrone(EnemyAI enemy, Vector3 dronePosition)
        {
            if (enemy is MouthDogAI dog)
                return DogHuntsSomethingCloserThanDrone(dog, dronePosition);

            PlayerControllerB target = enemy != null ? enemy.targetPlayer : null;
            if (target == null || target.isPlayerDead || target.transform == null || enemy.transform == null)
                return false;

            float playerDistanceSqr = (target.transform.position - enemy.transform.position).sqrMagnitude;
            float droneDistanceSqr = (dronePosition - enemy.transform.position).sqrMagnitude;
            return playerDistanceSqr < droneDistanceSqr;
        }

        /// <summary>
        /// #500: the eyeless dog's PlayerHasPriorityOverDrone. MouthDogAI never sets targetPlayer; a
        /// hunting dog chases its noisePositionGuess (MouthDogAI.cs:308-316). A state-2 dog whose guess is
        /// away from the drone and nearer to the dog than the drone is hunting something closer. The
        /// guess is local and unsynced, so only the dog's owner can read it; TryEnrageDogTowardDrone
        /// leaves a dog a client hunts with to that client.
        /// </summary>
        private static bool DogHuntsSomethingCloserThanDrone(MouthDogAI dog, Vector3 dronePosition)
        {
            if (!dog.IsOwner || dog.currentBehaviourStateIndex != 2)
                return false;
            // Still the host's own drone enrage: EnrageDogOnLocalClient set lastHeardNoisePosition to the
            // drone spot (MouthDogAI.cs:504), and the dog's search around that old spot after a lunge
            // (355-365) is not a closer target. Any later noise, howl, hit or stun rewrites
            // lastHeardNoisePosition (424, and 504 from 187, 469 and 594), which restores the priority.
            if (DogDroneEnrageTargetByEnemy.TryGetValue(GetEnemyKey(dog), out Vector3 enrageTarget)
                && (dog.lastHeardNoisePosition - enrageTarget).sqrMagnitude < 0.01f)
                return false;

            Vector3 guess = dog.noisePositionGuess;
            Vector3 dogPosition = dog.transform.position;
            return (guess - dronePosition).sqrMagnitude > DOG_DRONE_GUESS_RADIUS * DOG_DRONE_GUESS_RADIUS
                && (guess - dogPosition).sqrMagnitude < (dronePosition - dogPosition).sqrMagnitude;
        }

        // #500: the eyeless dog's attack on the drone is its real lunge. A dog the host enraged toward
        // the drone (TryEnrageDogTowardDrone) lunges from its own state-2 Update (MouthDogAI.cs:308-316);
        // this turns that lunge onto the drone the way vanilla OnCollideWithEnemy turns the dog toward
        // what it hit before EnterLunge (605-608). Host only, and only for a dog the drone attacked or
        // a pointer order named: a client's lunge, or one at a player's noise that merely landed near
        // the drone, keeps its own aim.
        [HarmonyPatch(typeof(MouthDogAI), nameof(MouthDogAI.EnterLunge))]
        [HarmonyPrefix]
        private static void PreMouthDogEnterLunge(MouthDogAI __instance)
        {
            if (!IsServerAuthority() || __instance == null || !CanDroneBeDamaged() || !EnemySharesDroneSpace(__instance))
                return;
            if (!IsDroneAttackThreat(__instance))
                return;

            Vector3 dronePosition = GetDroneTargetPosition();
            if (dronePosition == Vector3.zero)
                return;
            if ((__instance.noisePositionGuess - dronePosition).sqrMagnitude > DOG_DRONE_GUESS_RADIUS * DOG_DRONE_GUESS_RADIUS)
                return;
            // A lunge started next to a live player is vanilla's player-collision lunge; leave it aimed.
            if (IsControlledPlayerNear(__instance.transform.position, DOG_PLAYER_LUNGE_RADIUS))
                return;

            SnapEnemyYawToward(__instance, dronePosition);
        }

        private static bool IsControlledPlayerNear(Vector3 position, float radius)
        {
            StartOfRound round = StartOfRound.Instance;
            PlayerControllerB[] players = round != null ? round.allPlayerScripts : null;
            if (players == null)
                return false;

            float radiusSqr = radius * radius;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                    continue;
                if ((player.transform.position - position).sqrMagnitude <= radiusSqr)
                    return true;
            }

            return false;
        }

        // #500, host only: the lunge EnterLunge just started lands on the drone when it is aimed at it.
        [HarmonyPatch(typeof(MouthDogAI), nameof(MouthDogAI.EnterLunge))]
        [HarmonyPostfix]
        private static void PostMouthDogEnterLunge(MouthDogAI __instance)
        {
            if (!IsServerAuthority() || __instance == null || !CanDroneBeDamaged())
                return;
            if (!EnemySharesDroneSpace(__instance) || IsCombatRetreatActive())
                return;

            Vector3 dronePosition = GetDroneTargetPosition();
            if (dronePosition == Vector3.zero || !CanEnemyEngageDrone(__instance, DroneAttacker.MouthDog, dronePosition))
                return;

            DroneAttackProfile profile = DroneAttackProfiles[(int)DroneAttacker.MouthDog];
            Vector3 toDrone = dronePosition - __instance.transform.position;
            if (toDrone.sqrMagnitude > profile.Reach * profile.Reach)
                return;

            toDrone.y = 0f;
            Vector3 forward = __instance.transform.forward;
            forward.y = 0f;
            if (toDrone.sqrMagnitude > 0.001f && Vector3.Angle(forward, toDrone) > DOG_DRONE_LUNGE_MAX_ANGLE)
                return;
            if (!TryConsumeDroneAttackCooldown(__instance, profile.Cooldown))
                return;

            ApplyDroneDamage(profile.Damage, __instance);
        }

        /// <summary>
        /// #500 null attacker. Drone flame (F-DRONE-1) and grenade hits reach SandSpiderAI.HitEnemy with
        /// a null playerWhoHit, and on the spider's owner vanilla then calls TriggerChaseWithPlayer(null)
        /// (SandSpiderAI.cs:1356), which dereferences it (1269). HitEnemy has already applied the damage
        /// (1349), so declining to chase nobody keeps damage parity and removes the exception.
        /// </summary>
        [HarmonyPatch(typeof(SandSpiderAI), nameof(SandSpiderAI.TriggerChaseWithPlayer))]
        [HarmonyPrefix]
        private static bool PreSandSpiderTriggerChaseWithPlayer(PlayerControllerB playerScript)
        {
            return playerScript != null;
        }

        /// <summary>
        /// #500 grenade damage. Vanilla Landmine.SpawnExplosion hits each enemy within 4.5 m with
        /// HitEnemyOnLocalClient(6) on the peer that owns it (Landmine.cs:433-435). Inside a drone
        /// grenade's explosion the host's configured damage replaces the 6. Hot vanilla method: one int check.
        /// </summary>
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.HitEnemyOnLocalClient))]
        [HarmonyPrefix]
        private static void PreEnemyHitEnemyOnLocalClient(ref int force)
        {
            if (_droneGrenadeExplosionScope > 0)
                force = _droneGrenadeForceOverride;
        }

        private static void ResetDroneHealthForRound()
        {
            _droneHealth = DRONE_MAX_HEALTH;
            _droneDestroyedThisRound = false;
            _nextEnemyScanAt = 0f;
            _lastEnemyScanFrame = -1;
            _nextFlameAt = 0f;
            _nextGrenadeAt = 0f;
            _combatLandedUntil = 0f;
            _nextGuardScrapScanAt = 0f;
            _magnetPickupActive = false;
            _retreatUntil = 0f;
            _lastManualPoseAt = 0f;
            ClearPointerTargetState();
            DroneThreats.Clear();
            DroneAttackThreats.Clear();
            NextDroneHitByEnemy.Clear();
            NextDogEnrageAtByEnemy.Clear();
            DogDroneEnrageTargetByEnemy.Clear();
        }

        private static bool CanDroneBeDamaged()
        {
            return !_droneDestroyedThisRound
                && _droneHealth > 0
                && _droneVisual != null
                && _droneVisual.activeInHierarchy
                && _mode != DroneMode.NotSpawned;
        }

        internal static void NotifyDroneAttackedEnemy(EnemyAI enemy)
        {
            if (!IsServerAuthority() || enemy == null || enemy.isEnemyDead || !CanDroneBeDamaged())
                return;
            if (!EnemySharesDroneSpace(enemy))
                return;

            MarkDroneThreat(enemy, DRONE_ATTACK_THREAT_SECONDS, attackedByDrone: true);
        }

        private static void UpdateEnemyDroneAggroScanOncePerFrame()
        {
            if (_lastEnemyScanFrame == Time.frameCount)
                return;

            _lastEnemyScanFrame = Time.frameCount;
            if (Time.time < _nextEnemyScanAt)
                return;

            _nextEnemyScanAt = Time.time + ENEMY_SCAN_INTERVAL;
            if (!IsServerAuthority() || !CanDroneBeDamaged())
                return;

            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || roundManager.SpawnedEnemies == null)
                return;

            bool timing = _dronePerfTiming;
            long started = timing ? Stopwatch.GetTimestamp() : 0L;
            bool anyLiveThreat = false;
            for (int i = 0; i < roundManager.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = roundManager.SpawnedEnemies[i];
                if (enemy == null)
                    continue;

                UpdateEnemyDroneAggro(enemy);
                UpdateEnemyDroneAttack(enemy);
                if (!anyLiveThreat && !enemy.isEnemyDead && IsDroneThreat(enemy))
                    anyLiveThreat = true;
            }

            if (anyLiveThreat)
                MarkDroneCombatActive();
            if (timing)
            {
                AddDronePerfTicks(PERF_BUCKET_SCAN, started);
                started = Stopwatch.GetTimestamp();
            }

            UpdateDroneCombat(roundManager.SpawnedEnemies);
            if (timing)
                AddDronePerfTicks(PERF_BUCKET_COMBAT, started);
        }

        private static void UpdateEnemyDroneAggro(EnemyAI enemy)
        {
            if (!IsServerAuthority() || enemy == null || enemy.isEnemyDead || !CanDroneBeDamaged())
                return;
            if (!EnemySharesDroneSpace(enemy))
                return;
            // F-DRONE-4: while it is running for the owner ring the drone neither taunts nor trades.
            if (IsCombatRetreatActive())
                return;

            Vector3 dronePosition = GetDroneTargetPosition();
            if (dronePosition == Vector3.zero)
                return;

            float distanceSqr = (enemy.transform.position - dronePosition).sqrMagnitude;
            if (distanceSqr <= DRONE_PROXIMITY_AGGRO_RADIUS * DRONE_PROXIMITY_AGGRO_RADIUS)
                MarkDroneThreat(enemy, DRONE_PROXIMITY_THREAT_SECONDS);
        }

        /// <summary>
        /// #500: the drone takes damage only when a supported vanilla enemy performs its own attack on
        /// it. Host only, once per enemy per 0.25 s scan. Six enemies turn toward the drone and play
        /// their vanilla attack animation and sound on every peer (TechnicianCourier_EnemyAttack); the
        /// eyeless dog is enraged toward the drone and its real lunge lands the damage (MouthDogAI
        /// EnterLunge patches). Unsupported enemies never damage, steer toward or turn toward the drone.
        /// </summary>
        private static void UpdateEnemyDroneAttack(EnemyAI enemy)
        {
            if (!IsServerAuthority() || enemy == null || enemy.isEnemyDead || !CanDroneBeDamaged())
                return;
            if (!EnemySharesDroneSpace(enemy) || IsCombatRetreatActive())
                return;

            DroneAttacker attacker = ResolveDroneAttacker(enemy);
            if (attacker == DroneAttacker.None)
                return;

            Vector3 dronePosition = GetDroneTargetPosition();
            if (dronePosition == Vector3.zero || !CanEnemyEngageDrone(enemy, attacker, dronePosition))
                return;

            float distanceSqr = (enemy.transform.position - dronePosition).sqrMagnitude;
            if (attacker == DroneAttacker.MouthDog)
            {
                TryEnrageDogTowardDrone((MouthDogAI)enemy, dronePosition, distanceSqr);
                return;
            }

            DroneAttackProfile profile = DroneAttackProfiles[(int)attacker];
            float faceRange = profile.Reach * DRONE_ENEMY_FACE_RANGE_MULTIPLIER;
            if (distanceSqr <= faceRange * faceRange)
                RotateEnemyTowardDrone(enemy, dronePosition);
            if (distanceSqr <= profile.Reach * profile.Reach)
                TryEnemyAttackDrone(enemy, attacker);
        }

        private static DroneAttacker ResolveDroneAttacker(EnemyAI enemy)
        {
            switch (enemy)
            {
                case SandSpiderAI _: return DroneAttacker.SandSpider;
                case CrawlerAI _: return DroneAttacker.Crawler;
                case HoarderBugAI _: return DroneAttacker.HoarderBug;
                case BaboonBirdAI _: return DroneAttacker.Baboon;
                case NutcrackerEnemyAI _: return DroneAttacker.Nutcracker;
                case FlowermanAI _: return DroneAttacker.Flowerman;
                case MouthDogAI _: return DroneAttacker.MouthDog;
                default: return DroneAttacker.None;
            }
        }

        /// <summary>
        /// Gates every drone attack shares: the vanilla collision gates (MeetsStandardPlayerCollisionConditions,
        /// EnemyAI.cs:343-358), the enemy's own attack gate, the drone-threat mark and player priority.
        /// </summary>
        private static bool CanEnemyEngageDrone(EnemyAI enemy, DroneAttacker attacker, Vector3 dronePosition)
        {
            if (enemy.isEnemyDead || enemy.enemyHP <= 0 || enemy.stunNormalizedTimer > 0f || !enemy.ventAnimationFinished)
                return false;

            // A vanilla hoarding bug bites only while chasing (HoarderBugAI.cs:950), and a vanilla dog
            // turns on an enemy only when that enemy hits it (MouthDogAI.HitEnemy, 592-595), so for
            // these two bare proximity is not enough: the drone must have attacked it, or a pointer
            // order must have named it.
            bool threat = attacker == DroneAttacker.HoarderBug || attacker == DroneAttacker.MouthDog
                ? IsDroneAttackThreat(enemy)
                : IsDroneThreat(enemy);
            return threat
                && PassesVanillaAttackGate(enemy, attacker)
                && !PlayerHasPriorityOverDrone(enemy, dronePosition);
        }

        /// <summary>The enemy's own condition for starting its vanilla attack.</summary>
        private static bool PassesVanillaAttackGate(EnemyAI enemy, DroneAttacker attacker)
        {
            switch (attacker)
            {
                case DroneAttacker.SandSpider:
                    // SandSpiderAI.cs:1330-1332: not on a wall, not spooling a body.
                    return enemy is SandSpiderAI spider && !spider.onWall && !spider.spoolingPlayerBody;
                case DroneAttacker.Baboon:
                    // BaboonBirdAI.cs:352: not in its stab-kill animation.
                    return enemy is BaboonBirdAI baboon && !baboon.inSpecialAnimation && !baboon.doingKillAnimation;
                case DroneAttacker.Nutcracker:
                    // NutcrackerEnemyAI.cs:1146: no kick while aiming or reloading the shotgun.
                    return enemy is NutcrackerEnemyAI nutcracker && !nutcracker.aimingGun && !nutcracker.reloadingGun;
                case DroneAttacker.Flowerman:
                    // FlowermanAI.cs:641 and 667: not already killing or carrying a body.
                    return enemy is FlowermanAI flowerman && !flowerman.inKillAnimation && !flowerman.carryingPlayerBody;
                default:
                    return true;
            }
        }

        private static void RotateEnemyTowardDrone(EnemyAI enemy, Vector3 dronePosition)
        {
            Vector3 toDrone = dronePosition - enemy.transform.position;
            toDrone.y = 0f;
            if (toDrone.sqrMagnitude <= 0.001f)
                return;

            // Called from the 0.25 s scan, so one step covers a whole scan interval.
            Quaternion desired = Quaternion.LookRotation(toDrone.normalized, Vector3.up);
            enemy.transform.rotation = Quaternion.RotateTowards(
                enemy.transform.rotation,
                desired,
                DRONE_ENEMY_FACE_TURN_DEGREES_PER_SECOND * ENEMY_SCAN_INTERVAL);
        }

        private static void TryEnemyAttackDrone(EnemyAI enemy, DroneAttacker attacker)
        {
            DroneAttackProfile profile = DroneAttackProfiles[(int)attacker];
            if (!TryConsumeDroneAttackCooldown(enemy, profile.Cooldown))
                return;

            Vector3 facePosition = GetDroneCombatPosition();
            PlayEnemyDroneAttackLocal(enemy, attacker, facePosition);
            BroadcastEnemyDroneAttack(enemy, facePosition);
            ApplyDroneDamage(profile.Damage, enemy);
        }

        private static bool TryConsumeDroneAttackCooldown(EnemyAI enemy, float cooldown)
        {
            ulong key = GetEnemyKey(enemy);
            if (NextDroneHitByEnemy.TryGetValue(key, out float nextHitAt) && Time.time < nextHitAt)
                return false;

            NextDroneHitByEnemy[key] = Time.time + cooldown;
            return true;
        }

        /// <summary>
        /// Makes the drone the dog's noise target the way vanilla DetectNoise does
        /// (EnrageDogOnLocalClient, MouthDogAI.cs:472-506; exact position, full chase). Its own state-2
        /// Update then lunges once it is within 4 m (308-316). At most once per
        /// DOG_DRONE_ENRAGE_INTERVAL; never while a client hunts with the dog, and not while the dog is
        /// already hunting the drone's position. A host-owned dog hunting something closer was already
        /// turned away by PlayerHasPriorityOverDrone in CanEnemyEngageDrone.
        /// </summary>
        private static void TryEnrageDogTowardDrone(MouthDogAI dog, Vector3 dronePosition, float distanceSqr)
        {
            if (distanceSqr > DRONE_PROXIMITY_AGGRO_RADIUS * DRONE_PROXIMITY_AGGRO_RADIUS)
                return;
            if (dog.inLunge || dog.inKillAnimation || dog.currentBehaviourStateIndex == 3)
                return;
            // A client whose noise enraged the dog took ownership (MouthDogAI.cs:495-498) and hunts with
            // its own unsynced noisePositionGuess. Enraging here would take the dog back to the host every
            // second and the client's next noise would take it again; let the client keep it.
            if (!dog.IsOwner && dog.currentBehaviourStateIndex >= 2)
                return;
            if (dog.currentBehaviourStateIndex == 2
                && (dog.noisePositionGuess - dronePosition).sqrMagnitude <= DOG_DRONE_GUESS_RADIUS * DOG_DRONE_GUESS_RADIUS)
                return;

            ulong key = GetEnemyKey(dog);
            if (NextDogEnrageAtByEnemy.TryGetValue(key, out float nextEnrageAt) && Time.time < nextEnrageAt)
                return;

            NextDogEnrageAtByEnemy[key] = Time.time + DOG_DRONE_ENRAGE_INTERVAL;
            try
            {
                dog.EnrageDogOnLocalClient(dronePosition, Mathf.Sqrt(distanceSqr), approximatePosition: false, fullyEnrage: true);
                DogDroneEnrageTargetByEnemy[key] = dronePosition;
            }
            catch (Exception ex)
            {
                WarnOncePerEnemyType(DroneAttackWarnedEnemyTypes, dog, "dog enrage", ex);
            }
        }

        private static void UpdateDroneCombat(List<EnemyAI> enemies)
        {
            if (!CanDroneUseCombat() || enemies == null)
                return;

            int tier = ResolveCombatTier();
            if (tier < 1)
                return;

            Vector3 dronePosition = GetDroneCombatPosition();
            Vector3 origin = GetDroneCombatOrigin();
            if (dronePosition == Vector3.zero || origin == Vector3.zero)
                return;

            EnemyAI flameTarget = null;
            float bestFlameSqr = FLAME_TRIGGER_RANGE * FLAME_TRIGGER_RANGE;
            EnemyAI grenadeTarget = null;
            Vector3 grenadeImpact = Vector3.zero;
            float bestGrenadeSqr = GRENADE_MAX_RANGE * GRENADE_MAX_RANGE;
            bool canFlame = Time.time >= _nextFlameAt;
            bool canGrenade = tier >= 2 && Time.time >= _nextGrenadeAt;
            Transform grenadeMuzzle = canGrenade ? ResolveGrenadeMuzzle() : null;
            Vector3 grenadeOrigin = grenadeMuzzle != null ? grenadeMuzzle.position : origin;
            if (TryUsePriorityCombatTarget(tier, origin, dronePosition, grenadeOrigin, canFlame, canGrenade))
                return;

            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (!IsCombatEligibleEnemy(enemy))
                    continue;

                Vector3 center = ResolveEnemyCenter(enemy);
                if (center == Vector3.zero)
                    continue;

                float droneSqr = (center - dronePosition).sqrMagnitude;
                if (canFlame && droneSqr <= bestFlameSqr && HasDroneLineOfSight(origin, center))
                {
                    bestFlameSqr = droneSqr;
                    flameTarget = enemy;
                }

                if (!canGrenade)
                    continue;

                float grenadeSqr = (center - grenadeOrigin).sqrMagnitude;
                if (grenadeSqr < GRENADE_MIN_RANGE * GRENADE_MIN_RANGE || grenadeSqr > bestGrenadeSqr)
                    continue;
                if (!HasDroneLineOfSight(grenadeOrigin, center))
                    continue;
                if (IsPlayerInGrenadeDanger(center))
                    continue;

                bestGrenadeSqr = grenadeSqr;
                grenadeTarget = enemy;
                grenadeImpact = center;
            }

            if (flameTarget != null)
            {
                FireDroneFlame(manualMode: false);
                return;
            }

            if (grenadeTarget != null)
                FireDroneGrenade(grenadeImpact);
        }


        private static bool TryUsePriorityCombatTarget(int tier, Vector3 origin, Vector3 dronePosition, Vector3 grenadeOrigin, bool canFlame, bool canGrenade)
        {
            EnemyAI priorityTarget = ResolvePriorityTarget();
            if (priorityTarget == null)
                return false;

            Vector3 center = ResolveEnemyCenter(priorityTarget);
            if (center == Vector3.zero)
                return true;

            float flameSqr = (center - dronePosition).sqrMagnitude;
            if (canFlame && flameSqr <= FLAME_TRIGGER_RANGE * FLAME_TRIGGER_RANGE && HasDroneLineOfSight(origin, center))
            {
                FireDroneFlame(manualMode: false);
                return true;
            }

            if (!canGrenade || tier < 2)
                return true;

            float grenadeSqr = (center - grenadeOrigin).sqrMagnitude;
            if (grenadeSqr < GRENADE_MIN_RANGE * GRENADE_MIN_RANGE || grenadeSqr > GRENADE_MAX_RANGE * GRENADE_MAX_RANGE)
                return true;
            if (!HasDroneLineOfSight(grenadeOrigin, center))
                return true;
            if (IsPlayerInGrenadeDanger(center))
                return true;

            FireDroneGrenade(center);
            return true;
        }

        private static EnemyAI ResolvePriorityTarget()
        {
            if (_priorityTargetId == 0)
                return null;
            if (Time.time > _priorityTargetExpiresAt)
            {
                ClearPriorityTargetOnly();
                return null;
            }

            EnemyAI enemy = ResolveEnemy(_priorityTargetId);
            if (!IsCombatEligibleEnemy(enemy))
            {
                ClearPriorityTargetOnly();
                return null;
            }

            return enemy;
        }

        private static bool CanDroneUseCombat()
        {
            return IsServerAuthority()
                && CanDroneBeDamaged()
                && _mode != DroneMode.ManualControl
                && Time.time >= _combatLandedUntil
                // F-DRONE-4: a nearly dead autonomous drone disengages. A piloted drone does not -
                // that is the player's call, so CanDroneUseManualCombat is deliberately unaffected.
                && !IsCombatRetreatActive()
                && _droneVisual != null
                && _droneVisual.activeInHierarchy;
        }

        private static bool IsCombatRetreatActive()
        {
            return _retreatUntil > 0f
                && Time.time < _retreatUntil
                && _mode != DroneMode.ManualControl
                && !_manualControlActive;
        }

        /// <summary>
        /// F-DRONE-4: FOLLOW_RING_RADIUS parks the drone 2.3 m from the owner, inside the reach of
        /// most enemies' drone attacks (DroneAttackProfiles), and it used to stay there until it
        /// died. At DRONE_RETREAT_HEALTH it breaks
        /// off: threats are dropped, autonomous combat is suppressed and the drone heads back to the
        /// owner's follow ring for DRONE_RETREAT_SECONDS before it re-engages.
        /// </summary>
        private static void BeginCombatRetreat()
        {
            _retreatUntil = Time.time + DRONE_RETREAT_SECONDS;
            ClearPriorityTargetOnly();
            DroneThreats.Clear();
            DroneAttackThreats.Clear();

            // Parked at the entrance it would simply keep taking hits; send it home to the owner.
            if (_mode != DroneMode.AtEntrance)
                return;

            _mode = DroneMode.IdleAtPlayer;
            _isFollowingPlayer = false;
            _followSettled = false;
            StartIdleFollow(++_routeVersion);
            BroadcastDroneState();
        }

        private static bool CanDroneUseManualCombat()
        {
            return IsServerAuthority()
                && CanDroneBeDamaged()
                && _mode == DroneMode.ManualControl
                && Time.time >= _combatLandedUntil
                && !_magnetPickupActive
                && _droneVisual != null
                && _droneVisual.activeInHierarchy;
        }

        private static int ResolveCombatTier()
        {
            return _ownerCourierTier > 0 ? _ownerCourierTier : CourierDroneUpgrade.GetTier();
        }

        private static bool IsCombatEligibleEnemy(EnemyAI enemy)
        {
            return enemy != null
                && !enemy.isEnemyDead
                && enemy.transform != null
                && EnemySharesDroneSpace(enemy);
        }

        private static void FireDroneFlame(bool manualMode)
        {
            _nextFlameAt = Time.time + Plugin.GetDroneFlameCooldownSeconds();
            Vector3 position = GetDroneCombatPosition();
            PlayDroneFlameLocal(position);
            BroadcastDroneFlame(position);
            StartCoroutineOnAvailableHost(FlameDamageRoutine(_routeVersion, manualMode));
        }

        private static IEnumerator FlameDamageRoutine(int version, bool manualMode)
        {
            // #500: the host's tick count is read once, so a config change cannot skew a running burst.
            int ticks = Plugin.GetDroneFlameTicksPerBurst();
            WaitForSeconds tickWait = ticks > 1 ? new WaitForSeconds(FLAME_DAMAGE_WINDOW_SECONDS / ticks) : null;
            for (int tick = 0; tick < ticks; tick++)
            {
                if (version != _routeVersion || !(manualMode ? CanDroneUseManualCombat() : CanDroneUseCombat()))
                    yield break;

                bool timing = _dronePerfTiming;
                long started = timing ? Stopwatch.GetTimestamp() : 0L;
                ApplyFlameDamageTick();
                if (timing)
                    AddDronePerfTicks(PERF_BUCKET_FLAME, started);
                if (tick < ticks - 1 && tickWait != null)
                    yield return tickWait;
            }
        }

        /// <summary>
        /// Host-only. <c>playerWhoHit</c> is deliberately null (F-DRONE-1): vanilla
        /// HitEnemyOnLocalClient applies HitEnemy locally whenever playerWhoHit is non-null AND
        /// sends HitEnemyServerRpc, whose ClientRpc then runs HitEnemy on every machine EXCEPT the
        /// one whose local player id equals playerWhoHit. Passing the drone's owner therefore dealt
        /// double damage on the host, none at all on the owner's own client, and silently diverged
        /// enemy HP between machines. With null the host sends playerWhoHit -1 and every machine
        /// applies the configured flame damage exactly once. Owner kill credit is handled separately, through the
        /// mod's own ledger in <see cref="CreditLocalFlameAttribution"/>.
        /// </summary>
        private static void ApplyFlameDamageTick()
        {
            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || roundManager.SpawnedEnemies == null)
                return;

            Vector3 origin = GetDroneCombatPosition();
            float radiusSqr = FLAME_DAMAGE_RADIUS * FLAME_DAMAGE_RADIUS;
            int force = Plugin.GetDroneFlameDamagePerTick();
            for (int i = 0; i < roundManager.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = roundManager.SpawnedEnemies[i];
                if (!IsCombatEligibleEnemy(enemy))
                    continue;

                Vector3 center = ResolveEnemyCenter(enemy);
                if ((center - origin).sqrMagnitude > radiusSqr)
                    continue;

                Vector3 direction = (center - origin).sqrMagnitude > 0.001f
                    ? (center - origin).normalized
                    : Vector3.up;
                // Keeps one enemy from aborting the rest of the tick. It guards the local hit call, its
                // Harmony patches and NotifyDroneAttackedEnemy only: with a null attacker HitEnemy itself
                // runs later, in the NGO HitEnemyClientRpc handler (EnemyAI.cs:2363-2373, 2422-2432), so
                // a HitEnemy override's exception never reaches this catch. The spider's NRE is prevented
                // by the TriggerChaseWithPlayer prefix.
                try
                {
                    enemy.HitEnemyOnLocalClient(force, direction, null, false, DRONE_FLAME_HIT_ID);
                    NotifyDroneAttackedEnemy(enemy);
                }
                catch (Exception ex)
                {
                    WarnOncePerEnemyType(FlameHitWarnedEnemyTypes, enemy, "flame hit", ex);
                }
            }
        }

        private static void FireDroneGrenade(Vector3 targetPosition)
        {
            Transform muzzle = ResolveGrenadeMuzzle();
            Vector3 start = muzzle != null ? muzzle.position : GetDroneCombatOrigin();
            Vector3 velocity = CalculateGrenadeVelocity(start, targetPosition);
            if (velocity == Vector3.zero)
                return;

            _nextGrenadeAt = Time.time + GRENADE_COOLDOWN;
            int grenadeId = _nextGrenadeId++;
            if (_nextGrenadeId == int.MaxValue)
                _nextGrenadeId = 1;

            PlayGrenadeLaunchLocal(grenadeId, start, velocity);
            BroadcastGrenadeLaunch(grenadeId, start, velocity);
            StartCoroutineOnAvailableHost(GrenadeDetonationRoutine(grenadeId, start, velocity));
        }

        private static bool HandleManualFireRequest(int playerId, ManualFireKind fireKind, Vector3 targetPosition)
        {
            if (!CanDroneUseManualCombat() || playerId != _ownerPlayerId)
                return false;

            int tier = ResolveCombatTier();
            switch (fireKind)
            {
                case ManualFireKind.Flame:
                    if (tier < 1 || Time.time < _nextFlameAt)
                        return false;

                    FireDroneFlame(manualMode: true);
                    return true;

                case ManualFireKind.Grenade:
                    if (tier < 2 || Time.time < _nextGrenadeAt)
                        return false;

                    Transform muzzle = ResolveGrenadeMuzzle();
                    Vector3 start = muzzle != null ? muzzle.position : GetDroneCombatOrigin();
                    if (start == Vector3.zero)
                        return false;

                    FireDroneGrenade(ClampManualGrenadeTarget(start, targetPosition));
                    return true;
            }

            return false;
        }

        private static IEnumerator GrenadeDetonationRoutine(int grenadeId, Vector3 start, Vector3 velocity)
        {
            float elapsed = 0f;
            Vector3 previous = start;
            Vector3 detonation = start;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;

            while (elapsed < GRENADE_FUSE_SECONDS)
            {
                elapsed += Time.deltaTime;
                Vector3 current = EvaluateGrenadePosition(start, velocity, elapsed);
                Vector3 delta = current - previous;
                float distance = delta.magnitude;
                if (distance > 0.001f && Physics.SphereCast(previous, GRENADE_PROJECTILE_RADIUS, delta.normalized, out RaycastHit hit, distance, mask, QueryTriggerInteraction.Ignore))
                {
                    detonation = hit.point;
                    DetonateDroneGrenade(grenadeId, detonation);
                    yield break;
                }

                previous = current;
                detonation = current;
                yield return null;
            }

            DetonateDroneGrenade(grenadeId, detonation);
        }

        private static void DetonateDroneGrenade(int grenadeId, Vector3 position)
        {
            // Host-only. The blast itself lives in PlayGrenadeExplosionLocal so every peer runs it
            // exactly once, including this machine via the call below (F-DRONE-2).
            // #500: one host read of the grenade damage, applied here and sent to every client.
            int force = Plugin.GetDroneGrenadeDamage();
            PlayGrenadeExplosionLocal(grenadeId, position, force);
            BroadcastGrenadeExplosion(grenadeId, position, force);
        }

        private static Vector3 CalculateGrenadeVelocity(Vector3 start, Vector3 target)
        {
            Vector3 toTarget = target - start;
            Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
            float horizontalDistance = flat.magnitude;
            if (horizontalDistance <= 0.001f)
                return Vector3.zero;

            float flightTime = Mathf.Clamp(horizontalDistance / GRENADE_LOB_SPEED, 0.75f, GRENADE_FUSE_SECONDS * 0.82f);
            Vector3 randomSpread = UnityEngine.Random.insideUnitSphere * 0.45f;
            randomSpread.y = Mathf.Abs(randomSpread.y) * 0.25f;
            Vector3 adjustedTarget = target + randomSpread;
            return (adjustedTarget - start - 0.5f * Physics.gravity * flightTime * flightTime) / flightTime;
        }

        private static Vector3 EvaluateGrenadePosition(Vector3 start, Vector3 velocity, float elapsed)
        {
            return start + velocity * elapsed + 0.5f * Physics.gravity * elapsed * elapsed;
        }

        private static Transform ResolveGrenadeMuzzle()
        {
            return CourierDroneRuntimeAssets.FindGrenadeMuzzle(_droneVisual);
        }

        private static Vector3 GetDroneCombatPosition()
        {
            return _droneVisual != null ? _droneVisual.transform.position : ToFlightPosition(_droneGroundPosition);
        }

        private static Vector3 GetDroneCombatOrigin()
        {
            Vector3 position = GetDroneCombatPosition();
            return position != Vector3.zero ? position + Vector3.up * 0.12f : Vector3.zero;
        }

        private static Vector3 ResolveEnemyCenter(EnemyAI enemy)
        {
            if (enemy == null)
                return Vector3.zero;
            if (enemy.eye != null)
                return enemy.eye.position;

            // #500 perf: the first live skinned/mesh renderer is found once per enemy and reused; its
            // bounds are still read on every call. A missing or destroyed renderer is walked again.
            int id = enemy.GetInstanceID();
            EnemyCenterRenderers.TryGetValue(id, out Renderer renderer);
            if (renderer == null)
            {
                if (enemy.skinnedMeshRenderers != null)
                {
                    for (int i = 0; i < enemy.skinnedMeshRenderers.Length; i++)
                    {
                        if (enemy.skinnedMeshRenderers[i] == null)
                            continue;

                        renderer = enemy.skinnedMeshRenderers[i];
                        break;
                    }
                }

                if (renderer == null && enemy.meshRenderers != null)
                {
                    for (int i = 0; i < enemy.meshRenderers.Length; i++)
                    {
                        if (enemy.meshRenderers[i] == null)
                            continue;

                        renderer = enemy.meshRenderers[i];
                        break;
                    }
                }

                if (renderer != null)
                    EnemyCenterRenderers[id] = renderer;
            }

            return renderer != null ? renderer.bounds.center : enemy.transform.position + Vector3.up * 1.2f;
        }

        private static bool HasDroneLineOfSight(Vector3 origin, Vector3 target)
        {
            if (origin == Vector3.zero || target == Vector3.zero)
                return false;

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            return !Physics.Linecast(origin, target, mask, QueryTriggerInteraction.Ignore);
        }

        private static bool IsPlayerInGrenadeDanger(Vector3 impactPosition)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return false;

            float dangerSqr = (GRENADE_DAMAGE_RANGE + GRENADE_SAFETY_MARGIN) * (GRENADE_DAMAGE_RANGE + GRENADE_SAFETY_MARGIN);
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || player.isPlayerDead || player.transform == null)
                    continue;
                if (player.isInsideFactory != _droneIsInside)
                    continue;
                if ((player.transform.position - impactPosition).sqrMagnitude <= dangerSqr)
                    return true;
            }

            return false;
        }

        private static void PlayDroneFlameLocal(Vector3 position)
        {
            MarkDroneCombatActive();
            if (_droneVisual != null)
                CourierDroneRuntimeAssets.PlayFireAttack(_droneVisual);

            GameObject effect = SpawnCompanionEffect("FireAttackParticles", position, _droneVisual != null ? _droneVisual.transform.rotation : Quaternion.identity, 2.2f);
            if (effect != null && _droneVisual != null)
                effect.transform.SetParent(_droneVisual.transform, worldPositionStays: true);

            CreditLocalFlameAttribution(position);
        }

        /// <summary>
        /// F-DRONE-1 replacement for owner attribution. The damage itself is applied host-side with
        /// playerWhoHit null, so the vanilla EnemyAI.HitEnemy postfix the progression ledger listens
        /// on can no longer name a payee. This runs on EVERY machine (the host locally, clients off
        /// MSG_COURIER_FLAME), and the ledger calls are themselves local-player-only, so exactly one
        /// machine - the drone owner's - records the hit. EnemyAI.KillEnemy then fires on every
        /// machine and the owner's own ledger pays out inside the attribution window.
        /// Fires once per burst rather than once per tick: the window is seconds wide, the burst is
        /// 1.2 s long, and this is the presentation path, not the damage path.
        /// </summary>
        private static void CreditLocalFlameAttribution(Vector3 origin)
        {
            if (origin == Vector3.zero || _ownerPlayerId < 0)
                return;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (local == null || (int)local.playerClientId != _ownerPlayerId)
                return;

            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || roundManager.SpawnedEnemies == null)
                return;

            float radiusSqr = FLAME_DAMAGE_RADIUS * FLAME_DAMAGE_RADIUS;
            for (int i = 0; i < roundManager.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = roundManager.SpawnedEnemies[i];
                if (enemy == null || enemy.isEnemyDead || enemy.transform == null)
                    continue;

                Vector3 center = ResolveEnemyCenter(enemy);
                if (center == Vector3.zero || (center - origin).sqrMagnitude > radiusSqr)
                    continue;

                ProgressionManager.RecordEnemyHit(enemy, local);
                EmployeeStatistics.RecordEnemyHit(enemy, local);
            }
        }

        private static void PlayGrenadeLaunchLocal(int grenadeId, Vector3 start, Vector3 velocity)
        {
            MarkDroneCombatActive();
            Vector3 launchDirection = velocity;
            float launchSqr = Vector3.Dot(launchDirection, launchDirection);
            if (launchSqr > 0.001f)
                launchDirection.Normalize();
            Quaternion rotation = launchSqr > 0.001f ? Quaternion.LookRotation(launchDirection, Vector3.up) : Quaternion.identity;
            SpawnCompanionEffect("GrenadeLaunchDustParticles", start, rotation, 2f);

            GameObject grenade = CourierDroneRuntimeAssets.InstantiateCompanionPrefab("Grenade", start, rotation);
            if (grenade == null)
                grenade = GameObject.CreatePrimitive(PrimitiveType.Sphere);

            grenade.name = "Y4NGZ_CourierDroneGrenade";
            grenade.transform.position = start;
            grenade.transform.rotation = rotation;
            grenade.transform.localScale = Vector3.one * 0.35f;
            Collider collider = grenade.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);

            Rigidbody body = grenade.GetComponent<Rigidbody>();
            if (body == null)
                body = grenade.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.isKinematic = false;
            body.velocity = velocity;
            body.angularVelocity = UnityEngine.Random.onUnitSphere * 9f;

            if (ActiveGrenadeVisuals.TryGetValue(grenadeId, out GameObject old) && old != null)
                UnityEngine.Object.Destroy(old);
            ActiveGrenadeVisuals[grenadeId] = grenade;
            UnityEngine.Object.Destroy(grenade, GRENADE_FUSE_SECONDS + 1f);
        }

        /// <summary>
        /// Runs on every peer (the host directly from <see cref="DetonateDroneGrenade"/>, clients off
        /// MSG_COURIER_GRENADE_EXPLODE).
        ///
        /// F-DRONE-2: Landmine.SpawnExplosion used to run host-only, but vanilla only kills/damages a
        /// PlayerControllerB when that player <c>IsOwner</c> and only shakes
        /// GameNetworkManager.localPlayerController's camera - so the grenade was lethal to the host
        /// and completely harmless and silent to every client, contradicting "the grenade still
        /// detonates if someone walks in". Vanilla landmines avoid this by running SpawnExplosion on
        /// every peer via ExplodeMineClientRpc; this does the same. Enemy damage stays
        /// single-application because vanilla guards that branch with <c>mainScript.IsOwner</c>,
        /// which is the host.
        ///
        /// F-DRONE-16: the bundle's blast particles are optional and InstantiateCompanionPrefab
        /// returns null when the asset is absent, which left an invisible explosion. When that
        /// happens, ask vanilla for its own explosion VFX instead.
        /// </summary>
        private static void PlayGrenadeExplosionLocal(int grenadeId, Vector3 position, int force)
        {
            if (ActiveGrenadeVisuals.TryGetValue(grenadeId, out GameObject grenade) && grenade != null)
                UnityEngine.Object.Destroy(grenade);
            ActiveGrenadeVisuals.Remove(grenadeId);
            bool hasBundleEffect = SpawnCompanionEffect("GrenadeExplosionParticles", position, Quaternion.identity, 4f) != null;

            // #500: the host's grenade damage replaces vanilla's 6 for this explosion only.
            _droneGrenadeForceOverride = force;
            _droneGrenadeExplosionScope++;
            try
            {
                // Friendly-fire gate intentionally runs at fire time only; detonation still resolves if a player moves in.
                Landmine.SpawnExplosion(position, !hasBundleEffect, GRENADE_KILL_RANGE, GRENADE_DAMAGE_RANGE);
            }
            catch (Exception ex)
            {
                if (!_droneGrenadeExplosionWarned)
                {
                    _droneGrenadeExplosionWarned = true;
                    Plugin.Log?.LogWarning($"Courier Drone grenade Landmine.SpawnExplosion threw: {ex.Message}");
                }
            }
            finally
            {
                _droneGrenadeExplosionScope--;
            }
        }

        private static void DestroyActiveGrenadeVisuals()
        {
            foreach (KeyValuePair<int, GameObject> pair in ActiveGrenadeVisuals)
            {
                if (pair.Value != null)
                    UnityEngine.Object.Destroy(pair.Value);
            }

            ActiveGrenadeVisuals.Clear();
        }

        private static GameObject SpawnCompanionEffect(string assetName, Vector3 position, Quaternion rotation, float destroyAfter)
        {
            GameObject effect = CourierDroneRuntimeAssets.InstantiateCompanionPrefab(assetName, position, rotation);
            if (effect == null)
                return null;

            effect.SetActive(true);
            ParticleSystem[] systems = effect.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem system = systems[i];
                if (system == null)
                    continue;

                system.gameObject.SetActive(true);
                system.Play(withChildren: true);
            }

            UnityEngine.Object.Destroy(effect, destroyAfter);
            return effect;
        }

        /// <summary>
        /// <paramref name="attackedByDrone"/> separates the two kinds of threat (F-DRONE-7): an
        /// enemy the drone actually burned/shelled, or one a pointer order named, may be steered off
        /// the crew; an enemy that merely walked within DRONE_PROXIMITY_AGGRO_RADIUS may not.
        /// </summary>
        private static void MarkDroneThreat(EnemyAI enemy, float seconds, bool attackedByDrone = false)
        {
            ulong key = GetEnemyKey(enemy);
            float expiresAt = Time.time + Mathf.Max(0.1f, seconds);
            DroneThreats[key] = expiresAt;
            if (attackedByDrone)
                DroneAttackThreats[key] = expiresAt;
        }

        private static bool IsDroneThreat(EnemyAI enemy)
        {
            return IsThreatEntryLive(DroneThreats, GetEnemyKey(enemy));
        }

        private static bool IsDroneAttackThreat(EnemyAI enemy)
        {
            return IsThreatEntryLive(DroneAttackThreats, GetEnemyKey(enemy));
        }

        private static bool IsThreatEntryLive(Dictionary<ulong, float> threats, ulong key)
        {
            if (!threats.TryGetValue(key, out float expiresAt))
                return false;
            if (Time.time <= expiresAt)
                return true;

            threats.Remove(key);
            return false;
        }

        private static void SteerEnemyTowardDrone(EnemyAI enemy)
        {
            Vector3 dronePosition = GetDroneTargetPosition();
            if (dronePosition == Vector3.zero)
                return;

            try
            {
                // F-DRONE-7: SetDestinationToPosition already drives the agent; the extra direct
                // agent.SetDestination fought whatever the AI set for itself the same frame.
                enemy.SetDestinationToPosition(dronePosition);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Courier Drone enemy aggro steer skipped for {enemy.GetType().Name}: {ex.Message}");
            }
        }

        private static bool EnemySharesDroneSpace(EnemyAI enemy)
        {
            return enemy != null && enemy.isOutside != _droneIsInside;
        }

        private static Vector3 GetDroneTargetPosition()
        {
            return CurrentDroneGroundPositionOr(_droneGroundPosition);
        }

        private static ulong GetEnemyKey(EnemyAI enemy)
        {
            if (enemy == null)
                return 0;
            if (enemy.NetworkObject != null)
                return enemy.NetworkObjectId;

            return unchecked((ulong)enemy.GetInstanceID());
        }

        private static void ApplyDroneDamage(int damage, EnemyAI sourceEnemy)
        {
            if (damage <= 0 || !CanDroneBeDamaged())
                return;
            if (!IsServerAuthority())
                return;

            _droneHealth = Mathf.Max(0, _droneHealth - damage);
            bool bigHit = damage > 3;
            PlayDroneHitLocal(damage, bigHit, _droneHealth);
            BroadcastDroneHit(damage, bigHit, _droneHealth);
            if (_droneHealth <= 0)
            {
                DestroyDroneFromDamage(sourceEnemy);
                return;
            }

            // F-DRONE-4: break off once the drone is nearly dead rather than trading to zero.
            if (_droneHealth <= DRONE_RETREAT_HEALTH && !IsCombatRetreatActive())
                BeginCombatRetreat();

            BroadcastDroneState();
        }

        private static void PlayDroneHitLocal(int damageAmount, bool bigHit, int healthAfter)
        {
            MarkDroneCombatActive();
            if (_droneVisual == null)
                return;

            Vector3 joltDirection = UnityEngine.Random.insideUnitSphere;
            joltDirection.y = 0f;
            if (joltDirection.sqrMagnitude <= 0.001f)
                joltDirection = Vector3.right;
            CourierDroneRuntimeAssets.PlayHitReaction(
                _droneVisual,
                bigHit,
                joltDirection.normalized,
                DRONE_HIT_JOLT_DISTANCE,
                DRONE_HIT_JOLT_SECONDS);
            MaybeDisplayOwnerAttackWarning(healthAfter);
        }

        private static void MaybeDisplayOwnerAttackWarning(int healthAfter)
        {
            if (healthAfter <= 0 || Time.time < _nextDroneAttackWarningAt)
                return;

            _nextDroneAttackWarningAt = Time.time + DRONE_ATTACK_WARNING_INTERVAL;
            int healthPercent = Mathf.CeilToInt(Mathf.Clamp01(healthAfter / (float)DRONE_MAX_HEALTH) * 100f);
            DisplayOwnerTip(_ownerPlayerId, $"Under attack — {healthPercent}%", isWarning: true);
        }

        /// <summary>
        /// #500, every peer: turns the enemy toward the drone, then plays exactly the animator and audio
        /// calls of that enemy's vanilla attack - no player id, no player damage, no behaviour-state or
        /// agent change. The host applies the drone damage separately.
        /// </summary>
        private static void PlayEnemyDroneAttackLocal(EnemyAI enemy, DroneAttacker attacker, Vector3 dronePosition)
        {
            MarkDroneCombatActive();
            // A real Bracken kill on this peer owns the 'killing' bool; neither start nor clear it.
            if (enemy is FlowermanAI busyFlowerman && (busyFlowerman.inKillAnimation || busyFlowerman.carryingPlayerBody))
                return;

            try
            {
                SnapEnemyYawToward(enemy, dronePosition);
                Animator animator = enemy.creatureAnimator;
                switch (attacker)
                {
                    case DroneAttacker.SandSpider:
                    {
                        // Body of SandSpiderAI's hit-player ClientRpc, SandSpiderAI.cs:1405-1408.
                        SandSpiderAI spider = (SandSpiderAI)enemy;
                        animator.SetTrigger("attack");
                        spider.overrideAnimation = 0.8f;
                        spider.creatureSFX.PlayOneShot(spider.attackSFX);
                        WalkieTalkie.TransmitOneShotAudio(spider.creatureSFX, spider.attackSFX);
                        break;
                    }
                    case DroneAttacker.Crawler:
                    {
                        // Body of CrawlerAI's hit-player ClientRpc, CrawlerAI.cs:583-587; the agent
                        // recoil at 588 is gameplay, not presentation, and is skipped.
                        CrawlerAI crawler = (CrawlerAI)enemy;
                        if (!crawler.inSpecialAnimation)
                            animator.SetTrigger("HitPlayer");
                        crawler.creatureVoice.PlayOneShot(crawler.bitePlayerSFX);
                        break;
                    }
                    case DroneAttacker.HoarderBug:
                    {
                        // Body of HoarderBugAI's hit-player ClientRpc, HoarderBugAI.cs:999-1004.
                        HoarderBugAI hoarderBug = (HoarderBugAI)enemy;
                        if (!hoarderBug.isEnemyDead)
                        {
                            animator.SetTrigger("HitPlayer");
                            hoarderBug.creatureSFX.PlayOneShot(hoarderBug.hitPlayerSFX);
                            WalkieTalkie.TransmitOneShotAudio(hoarderBug.creatureSFX, hoarderBug.hitPlayerSFX);
                        }
                        break;
                    }
                    case DroneAttacker.Baboon:
                    {
                        // BaboonBirdAI.OnCollideWithEnemy, BaboonBirdAI.cs:375-379, which vanilla also
                        // runs locally on every peer.
                        AudioClip hitClip = enemy.enemyType.audioClips[5];
                        animator.ResetTrigger("Hit");
                        animator.SetTrigger("Hit");
                        enemy.creatureSFX.PlayOneShot(hitClip);
                        WalkieTalkie.TransmitOneShotAudio(enemy.creatureSFX, hitClip);
                        if (RoundManager.Instance != null)
                            RoundManager.Instance.PlayAudibleNoise(enemy.creatureSFX.transform.position, 8f, 0.7f);
                        break;
                    }
                    case DroneAttacker.Nutcracker:
                    {
                        // The presentation half of NutcrackerEnemyAI.LegKickPlayer, NutcrackerEnemyAI.cs:
                        // 1207-1210; the kill, retarget and state switch around it are skipped.
                        NutcrackerEnemyAI nutcracker = (NutcrackerEnemyAI)enemy;
                        animator.SetTrigger("Kick");
                        nutcracker.creatureSFX.Stop();
                        nutcracker.torsoTurnAudio.volume = 0f;
                        nutcracker.creatureSFX.PlayOneShot(nutcracker.kickSFX);
                        break;
                    }
                    case DroneAttacker.Flowerman:
                    {
                        // The animator and audio half of the Bracken kill: KillPlayerAnimationClientRpc
                        // (FlowermanAI.cs:738) and killAnimation (760-761). 'killing' is held 0.65 s, a
                        // deliberate deviation from 773-775 (no body is carried; see
                        // FLOWERMAN_DRONE_KILL_SECONDS); the player, body, agent and state halves are skipped.
                        FlowermanAI flowerman = (FlowermanAI)enemy;
                        animator.SetBool("killing", true);
                        WalkieTalkie.TransmitOneShotAudio(flowerman.crackNeckAudio, flowerman.crackNeckSFX);
                        flowerman.crackNeckAudio.PlayOneShot(flowerman.crackNeckSFX);
                        StartCoroutineOnAvailableHost(ClearFlowermanDroneKillRoutine(flowerman));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                WarnOncePerEnemyType(DroneAttackWarnedEnemyTypes, enemy, "attack presentation", ex);
            }
        }

        /// <summary>
        /// Turns the enemy's yaw to face <paramref name="targetPosition"/>, keeping its x/z tilt, as
        /// vanilla attacks do as they land (NutcrackerEnemyAI.cs:1201-1203, FlowermanAI.cs:746-749).
        /// </summary>
        private static void SnapEnemyYawToward(EnemyAI enemy, Vector3 targetPosition)
        {
            Transform enemyTransform = enemy.transform;
            Vector3 toTarget = targetPosition - enemyTransform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude <= 0.001f)
                return;

            Vector3 euler = enemyTransform.eulerAngles;
            euler.y = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            enemyTransform.eulerAngles = euler;
        }

        private static IEnumerator ClearFlowermanDroneKillRoutine(FlowermanAI flowerman)
        {
            yield return new WaitForSeconds(FLOWERMAN_DRONE_KILL_SECONDS);
            // The Bracken may have despawned, or a real kill that started meanwhile now owns the bool
            // (FlowermanAI.cs:736-738).
            if (flowerman == null || flowerman.creatureAnimator == null || flowerman.inKillAnimation)
                yield break;

            flowerman.creatureAnimator.SetBool("killing", false);
        }

        /// <summary>One warning per enemy type in <paramref name="warnedTypes"/>, message only: these paths run per hit.</summary>
        private static void WarnOncePerEnemyType(HashSet<string> warnedTypes, EnemyAI enemy, string context, Exception ex)
        {
            string typeName = enemy != null ? enemy.GetType().Name : "<null>";
            if (warnedTypes.Add(typeName))
                Plugin.Log?.LogWarning($"Courier Drone {context} failed for {typeName}: {ex.Message}");
        }

        private static void DestroyDroneFromDamage(EnemyAI sourceEnemy)
        {
            if (_droneDestroyedThisRound)
                return;

            _droneHealth = 0;
            _droneDestroyedThisRound = true;
            Vector3 deathPosition = CurrentDroneGroundPositionOr(_droneGroundPosition);
            if (deathPosition != Vector3.zero)
                _droneGroundPosition = deathPosition;

            DestroyDroneLocal(dropCargo: true, notifyOwner: true);
            BroadcastDroneDeath(_droneGroundPosition, _droneIsInside);
            BroadcastDroneState();
        }

        private static void DestroyDroneLocal(bool dropCargo, bool notifyOwner)
        {
            StopManualControl(resumeDrone: false, notifyOwner: false);
            _routeVersion++;
            CourierDroneRuntimeAssets.StopMagnetSound(_droneVisual);
            // F-DRONE-12: the wreck lingers for DRONE_DEATH_VISUAL_SECONDS so the explode animation
            // can play, and the looping propeller source was only ever stopped by OnDisable, which
            // never fires on the orphaned object - so an exploded drone kept humming for five
            // seconds.
            CourierDroneRuntimeAssets.StopPropellerSound(_droneVisual);
            GameObject deathVisual = _droneVisual;
            Vector3 effectPosition = deathVisual != null
                ? deathVisual.transform.position
                : ToFlightPosition(_droneGroundPosition);
            bool animatedDeath = deathVisual != null && CourierDroneRuntimeAssets.PlayDeath(deathVisual);

            if (dropCargo)
                DropCarriedScrapAtDrone();
            else
                ReleaseCarriedItems();

            PlannedItems.Clear();
            DroneThreats.Clear();
            DroneAttackThreats.Clear();
            NextDroneHitByEnemy.Clear();
            NextDogEnrageAtByEnemy.Clear();
            DogDroneEnrageTargetByEnemy.Clear();
            DestroyActiveGrenadeVisuals();
            _mode = DroneMode.NotSpawned;
            _isFollowingPlayer = false;
            _followSettled = false;
            ClearFollowPathCache();
            _reportedNoEntranceScrap = false;
            _commandCooldownEnd = 0f;
            _combatLandedUntil = 0f;
            _nextGuardScrapScanAt = 0f;
            _flashlightEnabled = false;
            _magnetPickupActive = false;
            _nextDroneAttackWarningAt = 0f;
            _retreatUntil = 0f;
            _lastManualPoseAt = 0f;
            ClearPointerTargetState();

            if (!animatedDeath)
                SpawnDroneDeathEffect(effectPosition);

            if (deathVisual != null)
            {
                UnityEngine.Object.Destroy(deathVisual, animatedDeath ? DRONE_DEATH_VISUAL_SECONDS : 0f);
                if (_droneVisual == deathVisual)
                {
                    _droneVisual = null;
                    ClearDroneRendererCache();
                }
            }

            if (notifyOwner)
                DisplayOwnerTip(_ownerPlayerId, "Drone destroyed. It can be redeployed next round.", isWarning: true);
        }

        private static void SpawnDroneDeathEffect(Vector3 position)
        {
            if (position == Vector3.zero)
                return;

            GameObject flash = new GameObject("Y4NGZ_CourierDroneDeathFlash");
            flash.transform.position = position;
            Light light = flash.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.22f, 0.12f);
            light.range = 6f;
            light.intensity = 9f;
            light.shadows = LightShadows.None;
            UnityEngine.Object.Destroy(flash, 0.45f);
        }
    }
}
