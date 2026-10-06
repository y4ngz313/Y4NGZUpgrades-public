using System;
using System.Reflection;
using HarmonyLib;
using Y4NGZUpgrades.Patches;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Reflection seam onto the Lategame Upgrades (LGU) effect helpers whose results this mod's own
    /// player effects have to compose with. Nothing here registers, prices, displays or owns an LGU
    /// upgrade - <see cref="LguUpgradeBridge"/> does all of that. This type answers exactly one kind
    /// of question: "what value does the game actually use for X right now, given that LGU may have
    /// rewritten the instruction that reads it".
    ///
    /// Why reflection and not a namespace match: LGU applies almost every player effect as a
    /// transpiler that wraps a vanilla READ (<c>ldfld movementSpeed</c> becomes
    /// <c>ldfld movementSpeed; call RunningShoes.GetAdditionalMovementSpeed</c>). Those rewrites are
    /// invisible from the outside - no field changes, no patch of ours is consulted - so the only
    /// way to agree with the game about a quantity is to call the same helper LGU injected. The two
    /// exceptions, where LGU writes a field absolutely, are Sick Beats (<c>movementSpeed</c>) and
    /// Back Muscles (<c>carryWeight</c>), and those are handled by composing values rather than by
    /// calling anything.
    ///
    /// Degradation is deliberate and per-member. When a helper cannot be bound or throws, each
    /// accessor falls back to the value that cannot destroy an effect that is still live in the
    /// game: the health ceiling reports failure so the caller stops clamping at all, and the health
    /// regeneration floor stands the local tick down rather than risk healing twice.
    /// </summary>
    internal static class LguEffectCompatibility
    {
        internal const string StimpackTypeName =
            "MoreShipUpgrades.UpgradeComponents.TierUpgrades.AttributeUpgrades.Stimpack";
        private const string MedicalNanobotsTypeName =
            "MoreShipUpgrades.UpgradeComponents.TierUpgrades.Player.MedicalNanobots";
        private const string BiggerLungsTypeName =
            "MoreShipUpgrades.UpgradeComponents.TierUpgrades.BiggerLungs";
        private const string BackMusclesTypeName =
            "MoreShipUpgrades.UpgradeComponents.TierUpgrades.AttributeUpgrades.BackMuscles";
        private const string BaseUpgradeTypeName =
            "MoreShipUpgrades.Misc.Upgrades.BaseUpgrade";
        private const string PlayerPatcherTypeName =
            "MoreShipUpgrades.Patches.PlayerController.PlayerControllerBPatcher";
        private const string UpgradeBusTypeName = "MoreShipUpgrades.Managers.UpgradeBus";
        private const string LguStoreTypeName = "MoreShipUpgrades.Managers.LguStore";

        /// <summary>Upgrade key LGU's own <c>PreventInstantKill</c> prefix gates on.</summary>
        private const string ExplosionResistanceUpgradeName = "Explosion Resistance";

        private static bool? _present;
        private static bool _resolved;
        private static bool _runtimeProbeBound;
        private static Assembly _providerAssembly;
        private static Func<object> _upgradeBusInstance;
        private static Func<object> _storeInstance;

        private static Func<int, int> _stimpackCeiling;
        private static Func<int, int> _healthRegenFloor;
        private static Func<float, float> _effectiveSprintTime;
        private static Func<float, float> _reducedItemWeight;
        private static Func<string, bool> _upgradeActive;
        private static Func<string, int> _upgradeLevel;
        private static FieldInfo _stimpackInstance;
        private static FieldInfo _playerHealthLevels;
        private static FieldInfo _explosionMitigationLatch;

        /// <summary>
        /// True when Lategame Upgrades is loaded at all. Plugin presence, not type discovery, for
        /// the same reason <see cref="OptionalPluginCapabilities"/> uses it: a stale type of the
        /// same name in another assembly must never switch a composition rule on.
        /// </summary>
        internal static bool Present
        {
            get
            {
                if (!_present.HasValue)
                    _present = OptionalPluginCapabilities.IsLoaded(
                        OptionalPluginCapabilities.LateGameUpgradesGuid);

                return _present.Value;
            }
        }

        /// <summary>
        /// Whether LGU's live effect state may be read right now (#493). Integrated into the
        /// player menu, the bridge's readiness decides, as it always has. Not integrated, the
        /// bridge never starts and LGU sells every upgrade for credits, so LGU's own runtime decides:
        /// its UpgradeBus and its lobby store are both alive (<see cref="IsRuntimeLive"/>). The rule
        /// is <see cref="LguEffectComposition.CanReadLiveEffects"/>.
        /// </summary>
        private static bool CanReadLive()
        {
            bool present = Present;
            bool integrated = Plugin.LguIntegrationActive;
            return LguEffectComposition.CanReadLiveEffects(
                present,
                integrated,
                integrated && LguUpgradeBridge.IsReady,
                present && !integrated && IsRuntimeLive());
        }

        /// <summary>
        /// LGU's UpgradeBus and LguStore singletons both exist as live Unity objects right now. Their
        /// creation time is never assumed - the bus comes from LGU's own startup and the store
        /// spawns with each lobby - so either one missing or destroyed simply reads as not live.
        /// Read through delegates bound once, so the per-frame probe is two calls and two Unity
        /// null checks.
        /// </summary>
        private static bool IsRuntimeLive()
        {
            if (!_runtimeProbeBound)
            {
                Assembly provider = ProviderAssembly();
                if (provider == null)
                    return false;
                _runtimeProbeBound = true;
                _upgradeBusInstance = BindInstanceGetter(provider, UpgradeBusTypeName);
                _storeInstance = BindInstanceGetter(provider, LguStoreTypeName);
            }

            return IsAlive(_upgradeBusInstance) && IsAlive(_storeInstance);
        }

        private static Func<object> BindInstanceGetter(Assembly provider, string typeName)
        {
            try
            {
                Type owner = provider.GetType(typeName, false);
                MethodInfo getter = owner == null ? null : AccessTools.PropertyGetter(owner, "Instance");
                if (getter != null && getter.IsStatic
                    && Delegate.CreateDelegate(typeof(Func<object>), getter, throwOnBindFailure: false)
                        is Func<object> bound)
                    return bound;

                Plugin.Log?.LogWarning(
                    "LGU compatibility: " + typeName + ".Instance is missing. Composition with Late Game "
                    + "Upgrades stands down while it is not integrated into the player menu.");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning(
                    "LGU compatibility: could not bind " + typeName + ".Instance: " + error.Message);
            }

            return null;
        }

        private static bool IsAlive(Func<object> instance)
        {
            if (instance == null)
                return false;
            try
            {
                return instance() is UnityEngine.Object live && live != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Assembly ProviderAssembly()
        {
            if (_providerAssembly != null)
                return _providerAssembly;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(
                    OptionalPluginCapabilities.LateGameUpgradesGuid, out var plugin)
                || plugin.Instance == null)
                return null;

            _providerAssembly = plugin.Instance.GetType().Assembly;
            return _providerAssembly;
        }

        /// <summary>
        /// Binds every helper once, the first time <see cref="CanReadLive"/> says LGU's state may be
        /// read. Every caller checks that first; before it a failed bind is not cached.
        /// </summary>
        private static void EnsureResolved()
        {
            if (_resolved || !Present || ProviderAssembly() == null)
                return;

            _resolved = true;

            _stimpackCeiling = Bind<Func<int, int>>(StimpackTypeName, "CheckForAdditionalHealth");
            _healthRegenFloor = Bind<Func<int, int>>(MedicalNanobotsTypeName, "GetIncreasedHealthRegeneration");
            _effectiveSprintTime = Bind<Func<float, float>>(BiggerLungsTypeName, "GetAdditionalStaminaTime");
            _reducedItemWeight = Bind<Func<float, float>>(BackMusclesTypeName, "DecreasePossibleWeight");
            _upgradeActive = Bind<Func<string, bool>>(BaseUpgradeTypeName, "GetActiveUpgrade");
            _upgradeLevel = Bind<Func<string, int>>(BaseUpgradeTypeName, "GetUpgradeLevel");
            Type stimpack = _providerAssembly.GetType(StimpackTypeName, false);
            _stimpackInstance = stimpack == null ? null : AccessTools.DeclaredField(stimpack, "Instance");
            _playerHealthLevels = stimpack == null ? null : AccessTools.DeclaredField(stimpack, "playerHealthLevels");

            Type playerPatcher = _providerAssembly.GetType(PlayerPatcherTypeName, false);
            _explosionMitigationLatch = playerPatcher == null
                ? null
                : AccessTools.DeclaredField(playerPatcher, "alreadyMitigated");
        }

        private static TDelegate Bind<TDelegate>(string typeName, string methodName)
            where TDelegate : class
        {
            try
            {
                Type owner = _providerAssembly.GetType(typeName, false);
                MethodInfo method = owner == null
                    ? null
                    : AccessTools.DeclaredMethod(owner, methodName);
                if (method == null || !method.IsStatic)
                {
                    Plugin.Log?.LogWarning(
                        "LGU compatibility: " + typeName + "." + methodName + " is missing. The "
                        + "matching composition rule stands down for this session.");
                    return null;
                }

                return Delegate.CreateDelegate(typeof(TDelegate), method, throwOnBindFailure: false)
                    as TDelegate;
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning(
                    "LGU compatibility: could not bind " + typeName + "." + methodName + ": "
                    + error.Message);
                return null;
            }
        }

        /// <summary>
        /// Health ceiling the game's own damage path enforces, given this mod's
        /// <paramref name="nativeMax"/>.
        ///
        /// LGU transpiles <c>PlayerControllerB.DamagePlayer</c> so vanilla's literal 100 becomes
        /// <c>Stimpack.CheckForAdditionalHealth(100)</c>, and it also adds flat health to the player
        /// on purchase and restores it through its own <c>ReviveDeadPlayers</c> transpiler. Our
        /// per-frame clamp therefore has to know about that ceiling or it deletes the Stimpack
        /// health within one frame of it being granted.
        ///
        /// Whether the two rewrites actually composed is not guessed from plugin presence:
        /// <see cref="NineLivesPatch.PreserveHealthCap"/> reads it straight off the instruction
        /// stream it rewrites.
        /// </summary>
        /// <returns>
        /// False when the ceiling cannot be established. The caller must then not clamp at all:
        /// the live ceiling is higher than anything we can prove, and clamping to our own value
        /// would destroy health the game considers legitimate.
        /// </returns>
        internal static bool TryComposeMaxHealth(int nativeMax, out int composed)
        {
            composed = nativeMax;
            if (!NineLivesPatch.LguHealthCapComposed)
                return true;
            if (!CanReadLive())
                return false;

            EnsureResolved();
            Func<int, int> ceiling = _stimpackCeiling;
            if (ceiling == null)
                return false;

            int value;
            try
            {
                if (!IsLocalHealthRankSynchronized())
                    return false;
                value = ceiling(nativeMax);
            }
            catch (Exception error)
            {
                _stimpackCeiling = null;
                Plugin.Log?.LogWarning(
                    "LGU compatibility: the Stimpack health ceiling threw, so the Resilience health "
                    + "clamp stands down for this session: " + error.Message);
                return false;
            }

            composed = value < nativeMax ? nativeMax : value;
            return true;
        }

        private static bool IsLocalHealthRankSynchronized()
        {
            var player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || _upgradeActive == null || _upgradeLevel == null
                || _stimpackInstance == null || _playerHealthLevels == null)
                return false;

            object instance = _stimpackInstance.GetValue(null);
            if (instance == null || !(_playerHealthLevels.GetValue(instance)
                is System.Collections.Generic.Dictionary<ulong, int> levels))
                return false;

            bool published = levels.TryGetValue(player.playerSteamId, out int publishedLevel);
            return LguEffectComposition.IsHealthRankSynchronized(
                _upgradeActive("Stimpack"), _upgradeLevel("Stimpack"), published, publishedLevel);
        }

        /// <summary>
        /// Health at or above which vanilla's own 1 HP/s regeneration has stopped, so Resilience's
        /// bonus regeneration may take over without healing twice in the same band.
        ///
        /// LGU's Medical Nanobots replaces vanilla's literal cap in <c>LateUpdate</c> with
        /// <c>GetIncreasedHealthRegeneration(20)</c>, raising it. When the helper is unavailable the
        /// floor is reported as unreachable, which stands our tick down entirely - losing a small
        /// regeneration benefit is recoverable, healing at double the advertised rate is not.
        /// </summary>
        internal static int ComposeHealthRegenFloor(int vanillaFloor)
        {
            bool readable = CanReadLive();
            int? lguFloor = null;
            if (readable)
            {
                EnsureResolved();
                Func<int, int> floor = _healthRegenFloor;
                if (floor != null)
                {
                    try
                    {
                        lguFloor = floor(vanillaFloor);
                    }
                    catch (Exception error)
                    {
                        _healthRegenFloor = null;
                        Plugin.Log?.LogWarning(
                            "LGU compatibility: the Medical Nanobots regeneration cap threw, so Resilience's "
                            + "bonus regeneration stands down for this session: " + error.Message);
                    }
                }
            }

            return LguEffectComposition.HealthRegenFloor(vanillaFloor, Present, readable, lguFloor);
        }

        /// <summary>
        /// Sprint time as the game's stamina denominator evaluates it. LGU's Bigger Lungs rewrites
        /// every <c>sprintTime</c> read inside <c>PlayerControllerB.LateUpdate</c>, so the field
        /// value alone understates the denominator and overstates our regeneration compensation.
        /// </summary>
        internal static float ComposeEffectiveSprintTime(float sprintTime)
        {
            if (!CanReadLive())
                return sprintTime;

            EnsureResolved();
            Func<float, float> effective = _effectiveSprintTime;
            if (effective == null)
                return sprintTime;

            try
            {
                float value = effective(sprintTime);
                return value < sprintTime ? sprintTime : value;
            }
            catch (Exception error)
            {
                _effectiveSprintTime = null;
                Plugin.Log?.LogWarning(
                    "LGU compatibility: the Bigger Lungs stamina time helper threw: " + error.Message);
                return sprintTime;
            }
        }

        /// <summary>
        /// One item's carry-weight contribution after LGU's Back Muscles has had its say. Its
        /// reduce-weight mode both rewrites vanilla's incremental weight arithmetic and writes
        /// <c>carryWeight</c> absolutely on purchase, and Transporter recomputes the whole figure
        /// from scratch every frame - so Transporter has to apply the same per-item reduction or its
        /// recomputation silently deletes the Back Muscles benefit. In the other two modes the
        /// helper is a pass-through and this composes to the unchanged value.
        /// </summary>
        internal static float ComposeItemWeightPenalty(float penalty)
        {
            if (!CanReadLive())
                return penalty;

            EnsureResolved();
            Func<float, float> reduced = _reducedItemWeight;
            if (reduced == null)
                return penalty;

            try
            {
                return LguEffectComposition.ComposeWeightPenalty(penalty, reduced(penalty));
            }
            catch (Exception error)
            {
                _reducedItemWeight = null;
                Plugin.Log?.LogWarning(
                    "LGU compatibility: the Back Muscles weight helper threw: " + error.Message);
                return penalty;
            }
        }

        /// <summary>
        /// True when LGU is about to convert this explosion death into survivable damage instead.
        ///
        /// Its <c>KillPlayer</c> prefix turns a <see cref="CauseOfDeath.Blast"/> kill into
        /// <c>DamagePlayer(GetExplosionDamageResistance(100))</c> and skips vanilla, but only while
        /// its own re-entrancy latch is clear; on the second pass it lets the death through. Nine
        /// Lives runs first (<c>Priority.First</c>) and would otherwise spend its once-per-round
        /// save on a death that costs the player nothing, exactly the way the existing lightning
        /// immunity is allowed to win first. The reduced damage still reaches Nine Lives through
        /// <c>DamagePlayer</c>, so deferring never removes the save - it only stops it being wasted.
        /// </summary>
        internal static bool WillMitigateExplosionDeath()
        {
            if (!CanReadLive())
                return false;

            EnsureResolved();
            Func<string, bool> active = _upgradeActive;
            FieldInfo latch = _explosionMitigationLatch;
            if (active == null || latch == null)
                return false;

            try
            {
                if (!active(ExplosionResistanceUpgradeName))
                    return false;

                return latch.GetValue(null) is bool mitigated && !mitigated;
            }
            catch (Exception error)
            {
                _upgradeActive = null;
                _explosionMitigationLatch = null;
                Plugin.Log?.LogWarning(
                    "LGU compatibility: could not read the explosion mitigation state: "
                    + error.Message);
                return false;
            }
        }
    }
}
