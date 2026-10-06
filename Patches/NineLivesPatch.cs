using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class NineLivesPatch
    {
        internal static readonly NineLivesState State = new NineLivesState();
        private enum FeedbackCue { Ready, Broken, Saved }
        private static readonly AudioClip[] FeedbackClips = new AudioClip[3];

        /// <summary>
        /// False while <see cref="PreserveHealthCap"/> could not find the v81 clamp it rewrites, so
        /// the damage path still clamps health to the vanilla 100. Consumers of Resilience's raised
        /// cap have to clamp defensively in that case (F-INFRA-1 / F-ENF-2).
        /// </summary>
        internal static bool HealthCapTranspilerApplied;

        /// <summary>
        /// True while the damage clamp <see cref="PreserveHealthCap"/> rewrote also carries
        /// Lategame Upgrades' Stimpack ceiling, i.e. the live ceiling is
        /// <c>Stimpack.CheckForAdditionalHealth(GetMaxHealth())</c> rather than
        /// <c>GetMaxHealth()</c>. Read straight off the instruction stream, because plugin presence
        /// alone cannot say whether LGU's own transpiler found its literal.
        /// </summary>
        internal static bool LguHealthCapComposed;

        private static bool _initialized;
        private static bool IsLocal(PlayerControllerB player) => player != null && player.IsOwner
            && player == GameNetworkManager.Instance?.localPlayerController;

        private static void Initialize()
        {
            if (_initialized) return;
            State.Reset(Time.time);
            _initialized = true;
        }

        // Run after Foreman and terrain reductions. Absorption changes the argument to zero
        // before skipping vanilla, so other postfixes see an absorbed hit.
        // Vanilla DamagePlayer(0) is NOT harmless: it clamps health to 100 and plays hurt effects.
        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static bool BeforeDamage(PlayerControllerB __instance, ref int damageNumber,
            CauseOfDeath causeOfDeath)
        {
            if (!IsLocal(__instance) || __instance.isPlayerDead || !__instance.AllowPlayerDeath()) return true;
            damageNumber = SurvivalAbilityRules.ReduceIsolatedDamage(damageNumber,
                LoneWolfUpgrade.ActiveLevel, LoneWolfUpgrade.LoneWolfActive);
            if (!NineLivesUpgrade.IsUnlocked() || damageNumber <= 0) return true;
            Initialize();
            int before = State.Shield;
            damageNumber = State.Absorb(damageNumber, Time.time);
            if (before > 0 && State.Shield == 0) PlayFeedback(FeedbackCue.Broken);
            if (damageNumber <= 0) return false;

            // Handle actual fatal damage before vanilla's undocumented survive-at-5-HP rule.
            // Numeric fall damage is a hit; bottomless pits and other direct environmental kills
            // are handled separately below and cannot be escaped with the save.
            if (damageNumber >= __instance.health && TrySave(__instance,
                causeOfDeath != CauseOfDeath.Abandoned && causeOfDeath != CauseOfDeath.Drowning))
            {
                damageNumber = 0;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool BeforeKill(PlayerControllerB __instance, CauseOfDeath causeOfDeath, int deathAnimation)
        {
            if (!IsLocal(__instance) || __instance.isPlayerDead || !__instance.AllowPlayerDeath()
                || !NineLivesUpgrade.IsUnlocked()) return true;
            // Existing lightning immunity wins without spending the once-per-round save.
            if (StormyWeatherPatch.IsLightningStrike && LightFeetUpgrade.HasTier(LightFeetUpgrade.TIER_GROUNDED)) return true;
            // An LGU explosion mitigation is free and runs from its own KillPlayer prefix behind
            // ours, so spending the round's single save here would waste it. The reduced damage it
            // deals still arrives at BeforeDamage, where the save is available if it is fatal.
            if (causeOfDeath == CauseOfDeath.Blast && LguEffectCompatibility.WillMitigateExplosionDeath())
                return true;
            bool eligible = causeOfDeath != CauseOfDeath.Gravity && causeOfDeath != CauseOfDeath.Drowning
                && causeOfDeath != CauseOfDeath.Abandoned && causeOfDeath != CauseOfDeath.Inertia
                && DamageCauseFilter.IsEnemyOrTrapCause(causeOfDeath, deathAnimation);
            return !TrySave(__instance, eligible);
        }

        private static bool TrySave(PlayerControllerB player, bool eligible)
        {
            Initialize();
            bool wasProtected = State.IsProtected(Time.time);
            if (!State.PreventDeath(Time.time, NineLivesUpgrade.HasInsurance(), eligible)) return false;
            if (wasProtected) return true; // Repeated execution attempts never extend the window.
            player.health = 1;
            // The caller may still dereference its grabbed player after KillPlayer returns.
            // Finish that call before cancelling its coroutine and releasing the grab.
            if (player.inAnimationWithEnemy != null)
                Y4NGZPersistentRunner.Run(ReleaseSavedPlayer(player, player.inAnimationWithEnemy));
            player.MakeCriticallyInjured(true);
            HUDManager.Instance?.UpdateHealthUI(1, hurtPlayer: false);
            // Vanilla owns health synchronization; the shield itself stays owner-local.
            if (player.IsServer) player.DamagePlayerClientRpc(0, 1);
            else player.DamagePlayerServerRpc(0, 1);
            PlayFeedback(FeedbackCue.Saved);
            return true;
        }

        private static IEnumerator ReleaseSavedPlayer(PlayerControllerB player, EnemyAI enemy)
        {
            yield return null;
            if (player == null || player.isPlayerDead || enemy == null) yield break;
            if (enemy.inSpecialAnimationWithPlayer != null && enemy.inSpecialAnimationWithPlayer != player) yield break;
            // If the player has entered another interaction, do not tear that one down.
            if (player.inAnimationWithEnemy != null && player.inAnimationWithEnemy != enemy) yield break;
            try
            {
                if (enemy is FlowermanAI bracken) bracken.FinishKillAnimation(carryingBody: false);
                else if (enemy is ForestGiantAI giant) giant.StopKillAnimation();
                else enemy.CancelSpecialAnimationWithPlayer();
                if (player.inAnimationWithEnemy == enemy)
                {
                    player.inAnimationWithEnemy = null;
                    player.inSpecialInteractAnimation = false;
                    player.snapToServerPosition = false;
                }
            }
            catch (Exception error) { Plugin.Log?.LogWarning("Nine Lives grab cancellation: " + error.Message); }
        }

        // Preserve Resilience health during partial shield spillover. Replace only the exact
        // integer Clamp(health - damage, 0, 100) ceiling in the installed v81 method.
        //
        // Ordering is load-bearing when Lategame Upgrades is installed. Its own DamagePlayer
        // transpiler hunts the same `ldc.i4.s 100` and inserts a call to
        // Stimpack.CheckForAdditionalHealth directly after it. Whichever of the two runs first
        // destroys the other's pattern: if we rewrite the literal away, LGU never finds its
        // ceiling AND its search loop - which only stops once it has - goes on to inject its
        // Sick Beats defence call after every remaining `ldarg.1` in the method; if LGU runs
        // first, the Clamp call is no longer adjacent to the literal and we degrade to the
        // vanilla cap, silently deleting Resilience's raised ceiling. Running last and accepting
        // an interposed int->int ceiling call composes both: the clamp becomes
        // Clamp(health - damage, 0, CheckForAdditionalHealth(GetMaxHealth())).
        //
        // [HarmonyAfter] takes a patch OWNER id, and LGU's is its plugin GUID - it patches with
        // `PatchManager.harmony = new(Metadata.GUID)` - so the capability constant is the right
        // value here as well as for the presence check.
        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter(OptionalPluginCapabilities.LateGameUpgradesGuid)]
        private static IEnumerable<CodeInstruction> PreserveHealthCap(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var clamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp), new[] { typeof(int), typeof(int), typeof(int) });
            // Count first, rewrite second: a transpiler that throws becomes a HarmonyException out
            // of the class processor, and before F-INFRA-1 that aborted every remaining patch class
            // and the rest of Plugin.Awake. One unmatchable IL shape must cost one inactive hook,
            // exactly the way ExtraSlotPatch.Transpiler degrades.
            int matches = 0;
            int target = -1;
            bool composed = false;
            for (int i = 1; i + 1 < code.Count; i++)
            {
                if (!code[i - 1].LoadsConstant(0) || !code[i].LoadsConstant(100)) continue;
                int ceiling = i + 1;
                bool lguCeiling = false;
                while (ceiling < code.Count && ceiling - i <= MaxInterposedCeilingCalls
                    && RaisesIntegerCeiling(code[ceiling]))
                {
                    if (code[ceiling].operand is MethodInfo method
                        && method.DeclaringType?.FullName == LguEffectCompatibility.StimpackTypeName
                        && method.Name == "CheckForAdditionalHealth")
                        lguCeiling = true;
                    ceiling++;
                }
                if (ceiling >= code.Count || !code[ceiling].Calls(clamp)) continue;
                matches++;
                target = i;
                composed = lguCeiling;
            }
            if (matches != 1)
            {
                HealthCapTranspilerApplied = false;
                LguHealthCapComposed = false;
                Plugin.Log?.LogError(
                    "Nine Lives: expected one v81 damage health clamp, found " + matches
                    + ". DamagePlayer is left unpatched, so Resilience's raised health cap stays at "
                    + "the vanilla 100; every other Y4NGZUpgrades patch is unaffected.");
                return code;
            }

            code[target].opcode = OpCodes.Call;
            code[target].operand = AccessTools.Method(typeof(MergedUpgradePatches), nameof(MergedUpgradePatches.GetMaxHealth));
            HealthCapTranspilerApplied = true;
            LguHealthCapComposed = composed;
            return code;
        }

        /// <summary>
        /// A bound on how many foreign ceiling calls may sit between the pushed literal and the
        /// Clamp before the shape stops being recognisable as vanilla's.
        /// </summary>
        private const int MaxInterposedCeilingCalls = 4;

        /// <summary>
        /// An <c>int Foo(int)</c> call, which is the only shape another mod can legally splice
        /// between the clamp's pushed ceiling and the Clamp itself without unbalancing the stack.
        /// </summary>
        private static bool RaisesIntegerCeiling(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Call) return false;
            if (!(instruction.operand is MethodInfo method)) return false;
            if (method.ReturnType != typeof(int)) return false;
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Tick(PlayerControllerB __instance)
        {
            if (!IsLocal(__instance)) return;
            Initialize();
            bool owned = NineLivesUpgrade.IsUnlocked();
            bool alive = !__instance.isPlayerDead && __instance.isPlayerControlled;
            if (!owned || !alive)
            {
                State.ClearLife(Time.time); // Revival does not replenish the round's spent save.
                return;
            }
            int capacity = MergedUpgradePatches.GetEffectiveMaxHealth();
            if (State.Tick(Time.time, capacity)) PlayFeedback(FeedbackCue.Ready);
        }

        internal static void OnRoundStarted()
        {
            State.Reset(Time.time);
            _initialized = true;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPostfix]
        private static void AfterKill(PlayerControllerB __instance)
        {
            if (!IsLocal(__instance) || !__instance.isPlayerDead) return;
            State.ClearLife(Time.time);
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void EndRound() { State.ClearLife(Time.time); }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void Disconnect()
        {
            State.Reset(Time.time);
            _initialized = false;
        }

        private static void PlayFeedback(FeedbackCue cue)
        {
            if (HUDManager.Instance?.UIAudio == null)
                return;

            HUDManager.Instance.UIAudio.PlayOneShot(
                GetFeedbackClip(cue), cue == FeedbackCue.Saved ? 0.65f : 0.35f);
        }

        private static AudioClip GetFeedbackClip(FeedbackCue cue)
        {
            int index = (int)cue;
            if (FeedbackClips[index] != null)
                return FeedbackClips[index];

            const int rate = 22050;
            float seconds = cue == FeedbackCue.Saved ? 0.65f : 0.35f;
            float[] data = new float[(int)(rate * seconds)];
            var noise = new System.Random(413 + index);
            for (int i = 0; i < data.Length; i++)
            {
                float t = (float)i / rate;
                float envelope = Mathf.Sin(Mathf.PI * t / seconds);
                float sample;
                if (cue == FeedbackCue.Broken)
                    sample = (float)(noise.NextDouble() * 2 - 1) * Mathf.Exp(-t * 18f);
                else if (cue == FeedbackCue.Saved)
                {
                    float beat = Mathf.Exp(-t * 25f)
                        + (t >= 0.22f ? 0.7f * Mathf.Exp(-(t - 0.22f) * 25f) : 0f);
                    sample = Mathf.Sin(2f * Mathf.PI * 70f * t) * beat;
                }
                else
                    sample = Mathf.Sin(2f * Mathf.PI * (380f * t + 420f * t * t)) * envelope;
                data[i] = Mathf.Clamp(sample * 0.6f, -1f, 1f);
            }

            AudioClip clip = AudioClip.Create("NineLives_" + cue, data.Length, 1, rate, false);
            clip.SetData(data, 0);
            FeedbackClips[index] = clip;
            return clip;
        }
    }
}
