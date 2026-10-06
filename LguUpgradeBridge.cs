using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Reflection-only ownership adapter for LGU's personal upgrades. Effects remain upstream;
    /// tokens and per-save ranks belong here. LGU's saved copies never contain token-owned ranks.
    /// Readiness follows its actual load coroutine, not a timer or plugin presence.
    /// </summary>
    internal static class LguUpgradeBridge
    {
        private const string OwnershipNotice =
            "\n\n[Y4NGZUpgrades] Personal upgrade: buy with tokens in the Employee File skill tree. "
            + "Not available for company credits, refunds or scrap contributions.";
        private const string SuppressionNotice =
            "\n\n[Y4NGZUpgrades] This upgrade is provided natively by Y4NGZUpgrades and is switched off here.";

        private sealed class Binding
        {
            internal Binding(string upgradeId, string lguName, string configProperty)
            {
                UpgradeId = upgradeId;
                LguName = lguName;
                ConfigProperty = configProperty;
            }

            /// <summary>The Y4NGZ catalog id, always <c>lgu_</c> + the normalised LGU name.</summary>
            internal string UpgradeId { get; }

            /// <summary>LGU's <c>UPGRADE_NAME</c>: its dictionary key and terminal node identity.</summary>
            internal string LguName { get; }

            /// <summary>The property on LGU's <c>LategameConfiguration</c> that configures it.</summary>
            internal string ConfigProperty { get; }
        }

        // The allowlist. An upgrade earns a place here when its effect is evaluated against the
        // local player's own body, perception or the equipment that player is holding or wearing,
        // and a crewmate without it gains nothing. Ship hardware, store economy, dropship orders
        // and unattended world machinery are LGU's to sell for credits and are absent on purpose.
        // Charging Booster is one of them (#442): it fits a station to every radar booster in the
        // world and syncs that station's cooldown to the whole crew.
        private static readonly Binding[] Bindings =
        {
            new Binding("lgu_back_muscles", "Back Muscles", "BackMusclesConfiguration"),
            new Binding("lgu_stimpack", "Stimpack", "StimpackConfiguration"),
            new Binding("lgu_protein_powder", "Protein Powder", "ProteinPowderConfiguration"),
            new Binding("lgu_deeper_pockets", "Deeper Pockets", "DeeperPocketsConfiguration"),
            // LGU spells the property "ExplosionReistance"; matching its typo is the contract.
            new Binding("lgu_explosion_resistance", "Explosion Resistance", "ExplosionReistanceConfiguration"),
            new Binding("lgu_bullet_resistance", "Bullet Resistance", "BulletResistanceConfiguration"),
            new Binding("lgu_hollow_point", "Hollow Point", "HollowPointConfiguration"),
            new Binding("lgu_long_barrel", "Long Barrel", "LongBarrelConfiguration"),
            new Binding("lgu_sleight_of_hand", "Sleight of Hand", "SleightOfHandConfiguration"),
            new Binding("lgu_silver_bullets", "Silver Bullets", "SilverBulletsConfiguration"),
            new Binding("lgu_running_shoes", "Running Shoes", "RunningShoesConfiguration"),
            new Binding("lgu_strong_legs", "Strong Legs", "StrongLegsConfiguration"),
            new Binding("lgu_bigger_lungs", "Bigger Lungs", "BiggerLungsConfiguration"),
            new Binding("lgu_carbon_kneejoints", "Carbon Kneejoints", "CarbonKneejointsConfiguration"),
            new Binding("lgu_hiking_boots", "Hiking Boots", "HikingBootsConfiguration"),
            new Binding("lgu_reinforced_boots", "Reinforced Boots", "ReinforcedBootsConfiguration"),
            new Binding("lgu_rubber_boots", "Rubber Boots", "RubberBootsConfiguration"),
            new Binding("lgu_traction_boots", "Traction Boots", "TractionBootsConfiguration"),
            // LGU spells the property "ClimblingGloves"; matching its typo is the contract.
            new Binding("lgu_climbing_gloves", "Climbing Gloves", "ClimblingGlovesConfiguration"),
            new Binding("lgu_oxygen_canisters", "Oxygen Canisters", "OxygenCanistersConfiguration"),
            new Binding("lgu_clay_glasses", "Clay Glasses", "ClayGlassesConfiguration"),
            new Binding("lgu_better_scanner", "Better Scanner", "BetterScannerUpgradeConfiguration"),
            new Binding("lgu_quick_hands", "Quick Hands", "QuickHandsConfiguration"),
            new Binding("lgu_mechanical_arms", "Mechanical Arms", "MechanicalArmsConfiguration"),
            new Binding("lgu_lithium_batteries", "Lithium Batteries", "LithiumBatteriesConfiguration"),
            // LGU's "Night Vision" string is the goggles' active flag, not a purchasable node.
            new Binding("lgu_nv_headset_batteries", "NV Headset Batteries", "NightVisionUpgradeConfiguration"),
            new Binding("lgu_locksmith", "Locksmith", "LocksmithConfiguration"),
            new Binding("lgu_aluminium_coils", "Aluminium Coils", "AluminiumCoilConfiguration"),
            new Binding("lgu_jet_fuel", "Jet Fuel", "JetFuelConfiguration"),
            new Binding("lgu_jetpack_thrusters", "Jetpack Thrusters", "JetpackThrustersConfiguration"),
            new Binding("lgu_beekeeper", "Beekeeper", "BeekeeperConfiguration"),
            new Binding("lgu_medical_nanobots", "Medical Nanobots", "MedicalNanobotsConfiguration"),
            new Binding("lgu_effective_bandaids", "Effective Bandaids", "EffectiveBandaidsConfiguration"),
            new Binding("lgu_walkie_gps", "Walkie GPS", "WalkieGpsConfiguration"),
            new Binding("lgu_sick_beats", "Sick Beats", "SickBeatsUpgradeConfiguration"),
            new Binding("lgu_tzp_buffer", "TZP Buffer", "TZPBufferConfiguration"),
            new Binding("lgu_fedora_suit", "Fedora Suit", "FedoraSuitConfiguration"),
            new Binding("lgu_weed_genetic_manipulation", "Weed Genetic Manipulation", "WeedGeneticManipulationConfiguration")
        };

        private static readonly Dictionary<string, Binding> ByUpgradeId =
            BuildIndex(binding => binding.UpgradeId);
        private static readonly Dictionary<string, Binding> ByLguName =
            BuildIndex(binding => binding.LguName);

        private sealed class OwnedNode
        {
            internal Binding Binding;
            internal object Node;
            internal Component Component;
            internal bool RequiresNetwork;
            internal MethodInfo Load;
            internal MethodInfo Increment;
            internal MethodInfo Unwind;
            internal bool Visible;
            internal bool Shared;
            internal bool Refundable;
            internal string Description;
            internal bool Imported;
        }

        private sealed class LoadObservation
        {
            internal int Generation;
            internal object Store;
        }

        private static readonly ConditionalWeakTable<object, LoadObservation> Loads =
            new ConditionalWeakTable<object, LoadObservation>();
        private static FieldInfo _iteratorStore;

        private static readonly Dictionary<string, OwnedNode> Owned =
            new Dictionary<string, OwnedNode>(StringComparer.Ordinal);
        private static readonly HashSet<string> Enabled = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Unavailable = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Suppressed = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> StaticCopyLogged = new HashSet<string>(StringComparer.Ordinal);
        private static IList _creditNodes;
        private static int _pendingLoads;
        private static readonly List<MethodBase> Hooks = new List<MethodBase>();
        private static Harmony _harmony;
        private static Assembly _assembly;
        private static Type _busType, _storeType, _nodeType, _baseType, _saveType, _progressionType;
        private static PropertyInfo _busInstance, _storeInstance, _saveInfo;
        private static FieldInfo _nodes, _objects, _active, _levels, _playerId, _savedActive, _savedLevels;
        private static PropertyInfo _name, _visible, _shared, _refundable, _description;
        private static PropertyInfo _unlocked, _rank, _maxRank, _unlockPrice;
        private static MethodInfo _saveRpc, _serialize, _selectNode, _currentPrice;
        private static bool _installed, _runtimeReady, _loadingComplete, _applying, _mayImport, _quirkAccurateCopy;
        private static int _generation;
        private static object _loadedStore;
        private static string _saveKey;

        internal static bool IsReady => _installed && _runtimeReady;
        internal static bool IsInstalled => _installed;
        internal static bool IsBridgedUpgrade(string id) =>
            !string.IsNullOrEmpty(id) && ByUpgradeId.ContainsKey(id);

        /// <summary>
        /// Adopted but not sold: a native row supplies this effect in the configured mode (#435).
        /// Fixed at <see cref="Initialize"/>; false whenever the integration is not installed.
        /// </summary>
        internal static bool IsSuppressed(string id) =>
            !string.IsNullOrEmpty(id) && ByUpgradeId.TryGetValue(id, out Binding binding)
            && Suppressed.Contains(binding.LguName);

        internal static bool TryGetSupportedLevels(string id, out int levels)
        {
            levels = 0;
            if (!IsReady || !ByUpgradeId.TryGetValue(id, out Binding binding)
                || !Owned.TryGetValue(binding.LguName, out OwnedNode owned))
                return false;
            if (Suppressed.Contains(binding.LguName)) return false;
            levels = Math.Max(0, (int)_maxRank.GetValue(owned.Node)) + 1;
            return true;
        }

        // The only Late Game Upgrades build whose formula bugs were verified (PLAN.md #442). Its
        // true numbers are shown only on that build; any other version gets LGU's nominal copy.
        private const string VerifiedQuirkVersion = "3.14.1";

        /// <summary>
        /// Numbered per-level copy for a bridged row, generated from the Late Game Upgrades
        /// configuration its effect code reads (#442). False keeps the row's static catalog copy:
        /// the integration is down, or a configuration member is missing, unreadable or not a
        /// finite number. Each such row is logged once. LGU's own terminal description is never
        /// used: it carries credit prices and repeats LGU's advertised-but-wrong numbers.
        /// </summary>
        internal static bool TryDescribe(string id, int levels, out string text)
        {
            text = null;
            if (!_installed || !ByUpgradeId.TryGetValue(id ?? string.Empty, out Binding binding))
                return false;

            string reason;
            try
            {
                object bus = _busInstance.GetValue(null);
                object config = bus == null ? null : Property(_busType, "PluginConfiguration").GetValue(bus);
                // Plugin Awake registers before LGU's bus exists; the lobby-ready pass regenerates
                // the copy, so only a failure once the configuration is live is worth reporting.
                if (config == null)
                    return false;
                object upgrade = Property(config.GetType(), binding.ConfigProperty).GetValue(config);
                if (upgrade != null && Gui.LguLevelCopy.TryDescribe(id, levels, _quirkAccurateCopy,
                        ArmoryBridge.SupportsIncomingFirearmResistance,
                        (string member, out object value) => TryReadConfigValue(upgrade, member, out value), out text))
                    return true;
                reason = upgrade == null
                    ? $"its configuration '{binding.ConfigProperty}' is missing"
                    : "a configuration value is missing or is not a finite number";
            }
            catch (Exception error)
            {
                reason = error.GetType().Name + ": " + error.Message;
            }

            text = null;
            if (StaticCopyLogged.Add(id))
                Plugin.Log?.LogWarning($"[LGU] '{binding.LguName}' keeps its static menu copy; {reason}.");
            return false;
        }

        /// <summary>
        /// Reads <c>member</c> or <c>member[i]</c> off an LGU upgrade configuration and unwraps the
        /// CSync entry's <c>Value</c>. Some list slots are null by design; they read as missing.
        /// </summary>
        private static bool TryReadConfigValue(object upgrade, string member, out object value)
        {
            value = null;
            int open = member.IndexOf('[');
            PropertyInfo property = AccessTools.Property(upgrade.GetType(), open < 0 ? member : member.Substring(0, open));
            object entry = property?.GetValue(upgrade);
            if (open >= 0)
            {
                if (!(entry is IList list)
                    || !int.TryParse(member.Substring(open + 1, member.Length - open - 2), out int index)
                    || index < 0 || index >= list.Count)
                    return false;
                entry = list[index];
            }

            PropertyInfo valueProperty = entry == null ? null : AccessTools.Property(entry.GetType(), "Value");
            value = valueProperty?.GetValue(entry);
            if (value is Enum) value = value.ToString();
            return value != null;
        }

        internal static void Initialize(Harmony harmony)
        {
            // #493: with the integration switched off nothing here touches Late Game Upgrades -
            // no adoption, no imported rows, no hooks, no edits to its credit store - so it keeps
            // its whole store. Token-owned imported ranks stay dormant in the Y4NGZ save.
            if (_installed || harmony == null || !Plugin.LguIntegrationActive
                || !Chainloader.PluginInfos.TryGetValue(
                    OptionalPluginCapabilities.LateGameUpgradesGuid, out var plugin)
                || plugin.Instance == null)
                return;
            _harmony = harmony;
            _assembly = plugin.Instance.GetType().Assembly;
            _quirkAccurateCopy = string.Equals(
                plugin.Metadata?.Version?.ToString(), VerifiedQuirkVersion, StringComparison.Ordinal);
            try
            {
                ResolveContract();
                foreach (Binding binding in Bindings)
                {
                    if (Plugin.ProgressionConfig?.UpgradePrices == null
                        || Plugin.ProgressionConfig.UpgradePrices.ResolveEnabled(UpgradeCatalogTable.Get(binding.UpgradeId)))
                        Enabled.Add(binding.LguName);
                }
                // A native row that supplies the same effect keeps the imported one adopted and
                // unsold (#435): out of LGU's own store, its effect unwound, its ranks dormant.
                // Command Net's switch is its own Enabled key; it has no functional dependency.
                LguUpgradeMode mode = Plugin.LguUpgradeMode;
                bool commandNetEnabled = Plugin.ProgressionConfig?.UpgradePrices == null
                    || Plugin.ProgressionConfig.UpgradePrices.ResolveEnabled(
                        UpgradeCatalogTable.Get(NativeUpgradeFamilies.CommandNetId));
                foreach (Binding binding in Bindings)
                {
                    if (NativeUpgradeFamilies.IsSuppressedImport(mode, binding.UpgradeId, commandNetEnabled))
                        Suppressed.Add(binding.LguName);
                }
                if (Suppressed.Count > 0)
                    Plugin.Log?.LogInfo($"[LGU] Suppressed {Suppressed.Count} row(s) that native upgrades supply ({mode}).");
                InstallHooks();
                _installed = true;
                Y4NGZUpgradeManager.UpgradesChanged += OnUpgradesChanged;
                AdoptNodes();
                Plugin.Log?.LogInfo($"[LGU] Bound {Bindings.Length} personal upgrades; awaiting LGU's loaded player save.");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogError($"[LGU] Integration unavailable; no token purchases enabled: {error}");
                Shutdown();
            }
        }

        private static Type RequireType(string name) => _assembly.GetType(name, true);
        private static PropertyInfo Property(Type type, string name) =>
            AccessTools.Property(type, name) ?? throw new MissingMemberException(type.FullName, name);
        private static FieldInfo Field(Type type, string name) =>
            AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
        private static MethodInfo Method(Type type, string name, params Type[] args) =>
            AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);

        private static void ResolveContract()
        {
            _busType = RequireType("MoreShipUpgrades.Managers.UpgradeBus");
            _storeType = RequireType("MoreShipUpgrades.Managers.LguStore");
            _nodeType = RequireType("MoreShipUpgrades.UI.TerminalNodes.CustomTerminalNode");
            _baseType = RequireType("MoreShipUpgrades.Misc.Upgrades.BaseUpgrade");
            _saveType = RequireType("MoreShipUpgrades.Managers.SaveInfo");
            _progressionType = RequireType("MoreShipUpgrades.Managers.ItemProgressionManager");
            _busInstance = Property(_busType, "Instance");
            _storeInstance = Property(_storeType, "Instance");
            _saveInfo = Property(_storeType, "SaveInfo");
            _nodes = Field(_busType, "terminalNodes");
            _objects = Field(_busType, "UpgradeObjects");
            _active = Field(_busType, "activeUpgrades");
            _levels = Field(_busType, "upgradeLevels");
            _playerId = Field(_storeType, "playerID");
            _savedActive = Field(_saveType, "activeUpgrades");
            _savedLevels = Field(_saveType, "upgradeLevels");
            _name = Property(_nodeType, "OriginalName");
            _visible = Property(_nodeType, "Visible");
            _shared = Property(_nodeType, "SharedUpgrade");
            _refundable = Property(_nodeType, "Refundable");
            _description = Property(_nodeType, "Description");
            _unlocked = Property(_nodeType, "Unlocked");
            _rank = Property(_nodeType, "CurrentUpgrade");
            _maxRank = Property(_nodeType, "MaxUpgrade");
            _unlockPrice = Property(_nodeType, "UnlockPrice");
            _saveRpc = Method(_storeType, "UpdateLGUSaveServerRpc", typeof(ulong), typeof(byte[]), typeof(bool));
            _serialize = Method(Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json", true),
                "SerializeObject", typeof(object));
            _selectNode = Method(_progressionType, "SelectTerminalNode", _nodeType.MakeByRefType(), _nodeType);
            _currentPrice = Method(_nodeType, "GetCurrentPrice");
        }

        private static void InstallHooks()
        {
            // The compiler's tiny IEnumerator factory can be inlined by Mono. Observe the
            // actual state machine so every direct LGU caller reaches the completion hook.
            Type iterator = Method(_storeType, "WaitForUpgradeObject")
                .GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType
                ?? throw new MissingMemberException("LGU load iterator");
            _iteratorStore = Field(iterator, "<>4__this");
            Patch(Method(iterator, "MoveNext"), prefix: nameof(BeforeLoadStep), postfix: nameof(AfterLoadStep));
            Patch(Method(_busType, "Reconstruct"), postfix: nameof(NodesChanged));
            Patch(Method(_storeType, "UpdateUpgradeBus", typeof(bool), typeof(bool)),
                prefix: nameof(BeforeLoad), postfix: nameof(DetachLoadedRanks));
            Patch(Method(_busType, "ResetAllValues", typeof(bool)), prefix: nameof(BeforeReset), postfix: nameof(AfterReset));
            Patch(Method(RequireType("MoreShipUpgrades.Managers.RandomizeUpgradeManager"),
                "RandomizeUpgrades", typeof(int)), postfix: nameof(NodesChanged));
            Patch(Method(_storeType, "HandleUpgrade", _nodeType, typeof(bool), typeof(bool)), prefix: nameof(GuardNode));
            Patch(Method(_storeType, "UpdateUpgrades", _nodeType, typeof(bool), typeof(bool)), prefix: nameof(GuardNode));
            Patch(Method(_storeType, "LockUpgradeServerRpc", typeof(string), typeof(ulong)), prefix: nameof(GuardName));
            // Guard the local rank sink, not ClientRpc wrappers: skipping a host wrapper would
            // suppress legitimate upgrades for other clients that do not run this integration.
            Type shop = RequireType("MoreShipUpgrades.UI.Application.UpgradeStoreApplication");
            Type mode = RequireType("MoreShipUpgrades.UI.TerminalNodes.PurchaseMode");
            Patch(Method(shop, "PurchaseUpgrade", _nodeType, typeof(int), typeof(Action)), prefix: nameof(GuardNode));
            Patch(Method(shop, "PurchaseUpgradeAlternateCurrency", _nodeType, typeof(int), typeof(Action)), prefix: nameof(GuardNode));
            Patch(Method(shop, "RefundUpgrade", _nodeType, mode, typeof(Action)), prefix: nameof(GuardNode));
            Patch(Method(_progressionType, "ContributeTowardsUpgrade", _nodeType, typeof(int)), prefix: nameof(GuardNode));
            Patch(Method(_progressionType, "RankUpUpgrade", _nodeType), prefix: nameof(GuardNode));
            Patch(Method(_progressionType, "AddScrapToUpgrade", _nodeType.MakeByRefType(), typeof(string)), prefix: nameof(GuardNode));
            Patch(Method(_progressionType, "PickRandomUpgrade"), prefix: nameof(PickRandomPrefix));
            Patch(Method(_progressionType, "SelectChancePerScrapUpgrade"), prefix: nameof(SelectChancePrefix));
            Patch(Method(_progressionType, "SelectNearestValueUpgrade", typeof(int)), prefix: nameof(SelectNearestPrefix));
            Patch(AccessTools.Constructor(_saveType, Type.EmptyTypes), postfix: nameof(FilterSavedRanks));
        }

        private static void Patch(MethodBase original, string prefix = null, string postfix = null)
        {
            if (original == null) throw new MissingMethodException("LGU hook target missing");
            _harmony.Patch(original,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(LguUpgradeBridge), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(LguUpgradeBridge), postfix));
            Hooks.Add(original);
        }

        private static Dictionary<string, Binding> BuildIndex(Func<Binding, string> key)
        {
            var result = new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
            foreach (Binding binding in Bindings) result.Add(key(binding), binding);
            return result;
        }

        private static bool IsOwned(object node) => node != null &&
            _name.GetValue(node) is string name && Owned.ContainsKey(name);
        private static bool GuardNode(object[] __args) => !_installed
            || (__args[0] != null && !IsOwned(__args[0]));
        private static bool GuardName(string __0) => !_installed || !Owned.ContainsKey(__0);

        private static void NodesChanged()
        {
            if (!_installed) return;
            try { AdoptNodes(); }
            catch (Exception error) { FailClosed("taking over personal nodes", error); }
        }

        private static void AdoptNodes()
        {
            object bus = _busInstance.GetValue(null);
            if (bus == null || !(_nodes.GetValue(bus) is IList nodes)) return;
            foreach (object node in nodes)
            {
                string name = (string)_name.GetValue(node);
                bool isSuppressed = Suppressed.Contains(name);
                if (!(Enabled.Contains(name) || isSuppressed) || Unavailable.Contains(name)) continue;
                if (!isSuppressed && (int)_unlockPrice.GetValue(node) <= 0) continue;
                if (!Owned.TryGetValue(name, out OwnedNode owned) || !ReferenceEquals(owned.Node, node))
                {
                    if (owned != null) RestoreNode(owned);
                    owned = new OwnedNode
                    {
                        Binding = ByLguName[name], Node = node,
                        Visible = (bool)_visible.GetValue(node), Shared = (bool)_shared.GetValue(node),
                        Refundable = (bool)_refundable.GetValue(node),
                        Description = (string)_description.GetValue(node)
                    };
                    Owned[name] = owned;
                }
                _visible.SetValue(node, false);
                _shared.SetValue(node, false);
                _refundable.SetValue(node, false);
                _description.SetValue(node, owned.Description + (isSuppressed ? SuppressionNotice : OwnershipNotice));
            }
            RebuildCreditNodes(nodes);
        }

        private static void RestoreNode(OwnedNode owned)
        {
            _visible.SetValue(owned.Node, owned.Visible);
            _shared.SetValue(owned.Node, owned.Shared);
            _refundable.SetValue(owned.Node, owned.Refundable);
            _description.SetValue(owned.Node, owned.Description);
        }

        private static void BeforeLoad(bool __0)
        {
            if (!_installed) return;
            try
            {
                WithdrawLiveRanks();
                Invalidate();
                _mayImport = __0; // "load lgu <other player>" may never import somebody else's ranks.
            }
            catch (Exception error) { FailClosed("preparing a save load", error); }
        }

        private static void DetachLoadedRanks()
        {
            if (!_installed) return;
            object bus = _busInstance.GetValue(null);
            // LGU aliases the host's saved dictionaries, including "load lgu <other>".
            // Local token reconciliation must never mutate another player's stored ranks.
            _active.SetValue(bus, new Dictionary<string, bool>(
                (IDictionary<string, bool>)_active.GetValue(bus)));
            _levels.SetValue(bus, new Dictionary<string, int>(
                (IDictionary<string, int>)_levels.GetValue(bus)));
        }

        private static void Invalidate()
        {
            _runtimeReady = false;
            _loadingComplete = false;
            _pendingLoads = 0;
            _generation++;
        }

        private static bool BeforeLoadStep(object __instance, ref bool __result)
        {
            if (!_installed) return true;
            if (!Loads.TryGetValue(__instance, out LoadObservation observation))
            {
                observation = new LoadObservation
                {
                    Generation = _generation,
                    Store = _iteratorStore.GetValue(__instance)
                };
                Loads.Add(__instance, observation);
                _pendingLoads++;
            }
            if (observation.Generation == _generation) return true;
            __result = false;
            return false;
        }

        private static void AfterLoadStep(object __instance, bool __result)
        {
            if (__result || !Loads.TryGetValue(__instance, out LoadObservation observation)) return;
            Loads.Remove(__instance);
            if (!_installed || observation.Generation != _generation) return;
            if (--_pendingLoads > 0) return;
            _loadedStore = observation.Store;
            _loadingComplete = true;
            CompleteLoad();
        }

        private static void CompleteLoad()
        {
            if (!_installed || !_loadingComplete || _applying) return;
            _applying = true;
            try
            {
                if (!Y4NGZUpgradeManager.SwitchToCurrentSave()) return;
                if (!ReferenceEquals(_storeInstance.GetValue(null), _loadedStore)
                    || GameNetworkManager.Instance?.localPlayerController == null) return;
                AdoptNodes();
                foreach (Binding binding in Bindings)
                {
                    if (!Owned.TryGetValue(binding.LguName, out OwnedNode owned)) continue;
                    try { BindComponent(owned); }
                    catch (Exception error)
                    {
                        RestoreNode(owned);
                        Owned.Remove(binding.LguName);
                        Unavailable.Add(binding.LguName);
                        // The row goes back to LGU's credit store, so let a later lobby
                        // re-import whatever it holds by then rather than drive it away.
                        Y4NGZUpgradeManager.ClearLguImport(binding.UpgradeId);
                        Plugin.Log?.LogWarning($"[LGU] '{binding.LguName}' unavailable this lobby; saved ranks retained: {error}");
                    }
                }
                RebuildCreditNodes((IList)_nodes.GetValue(_busInstance.GetValue(null)));
                _saveKey = Y4NGZUpgradeManager.CurrentSaveKey;
                _runtimeReady = true;
                // Host-synchronised nodes, not local Awake-time config, define rank caps.
                Y4NGZUpgradeCatalog.RegisterDefaults();
                foreach (OwnedNode owned in Owned.Values)
                {
                    int existing = _mayImport ? ReadLevel(owned) : 0;
                    Y4NGZUpgradeManager.ImportLguLevel(owned.Binding.UpgradeId, existing);
                }
                // Do not remove the original LGU save until its replacement is durable.
                Y4NGZUpgradeManager.PersistLguImport();
                foreach (OwnedNode owned in Owned.Values) owned.Imported = true;
                ApplyOwnedRanks();
                if (_mayImport) PersistProviderSave();
                Y4NGZUpgradeManager.RaiseUpgradesChanged();
                Plugin.Log?.LogInfo($"[LGU] Ready: {Owned.Count} personal nodes on '{_saveKey}'.");
            }
            catch (Exception error) { FailClosed("loading personal ownership", error); }
            finally { _applying = false; }
        }

        private static void BindComponent(OwnedNode owned)
        {
            object bus = _busInstance.GetValue(null);
            var objects = (IDictionary)_objects.GetValue(bus);
            var host = objects[owned.Binding.LguName] as GameObject;
            var component = host == null ? null : host.GetComponent(_baseType);
            if (component == null)
                throw new InvalidOperationException($"LGU object missing: {owned.Binding.LguName}");
            owned.RequiresNetwork = false;
            foreach (MethodInfo method in component.GetType().GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.IsDefined(typeof(ServerRpcAttribute), true)
                    || method.IsDefined(typeof(ClientRpcAttribute), true))
                    owned.RequiresNetwork = true;
            }
            // Some LGU scalar upgrades register a live CreateNetworkPrefab singleton.
            // Only RPC-bearing managers require a spawned NetworkObject.
            if (owned.RequiresNetwork && (!(component is NetworkBehaviour network) || !network.IsSpawned))
                throw new InvalidOperationException($"LGU RPC object not spawned: {owned.Binding.LguName}");
            owned.Component = component;
            owned.Load = Method(component.GetType(), "Load");
            owned.Unwind = Method(component.GetType(), "Unwind");
            owned.Increment = AccessTools.Method(component.GetType(), "Increment", Type.EmptyTypes);
            if ((int)_maxRank.GetValue(owned.Node) > 0 && owned.Increment == null)
                throw new MissingMethodException(component.GetType().FullName, "Increment");
        }

        private static void OnUpgradesChanged()
        {
            if (!_installed || _applying || !_loadingComplete) return;
            if (!IsReady) { CompleteLoad(); return; }
            _applying = true;
            try
            {
                if (!string.Equals(_saveKey, Y4NGZUpgradeManager.CurrentSaveKey, StringComparison.Ordinal))
                {
                    WithdrawLiveRanks();
                    _runtimeReady = false;
                    _mayImport = false;
                }
                else ApplyOwnedRanks();
            }
            catch (Exception error) { FailClosed("reconciling personal ownership", error); }
            finally { _applying = false; }
            if (_installed && !IsReady) CompleteLoad();
        }

        private static void ApplyOwnedRanks()
        {
            foreach (OwnedNode owned in Owned.Values)
            {
                int target = Y4NGZUpgradeManager.GetLevel(owned.Binding.UpgradeId);
                if (ReadLevel(owned) != target) Drive(owned, target);
            }
        }

        private static int ReadLevel(OwnedNode owned)
        {
            object bus = _busInstance.GetValue(null);
            var active = (IDictionary)_active.GetValue(bus);
            if (!(active[owned.Binding.LguName] is bool enabled) || !enabled) return 0;
            var levels = (IDictionary)_levels.GetValue(bus);
            return 1 + (levels[owned.Binding.LguName] is int level ? Math.Max(0, level) : 0);
        }

        private static void Drive(OwnedNode owned, int target)
        {
            if (owned.Component == null) throw new InvalidOperationException("LGU component was destroyed");
            int current = ReadLevel(owned);
            if (target < current)
            {
                if (owned.Binding.LguName == "Sick Beats") StopSickBeats(owned);
                // Unwind reads the full active rank. Decrementing first strands attribute bonuses.
                owned.Unwind.Invoke(owned.Component, null);
                _unlocked.SetValue(owned.Node, false);
                _rank.SetValue(owned.Node, 0);
                if (owned.Binding.LguName == "Walkie GPS")
                    Method(owned.Component.GetType(), "WalkieDeactivate").Invoke(owned.Component, null);
                // Battery upgrades do not own the underlying night-vision goggles.
                if (owned.Binding.LguName == "NV Headset Batteries"
                    && (bool)Field(owned.Component.GetType(), "nightVisionActive").GetValue(owned.Component))
                    Method(owned.Component.GetType(), "TurnOff", typeof(bool)).Invoke(owned.Component, new object[] { false });
                var player = GameNetworkManager.Instance?.localPlayerController;
                if (player != null && !player.isPlayerDead && player.health < 1) player.health = 1;
                current = 0;
            }
            if (target > current && current == 0)
            {
                ((IDictionary)_levels.GetValue(_busInstance.GetValue(null)))[owned.Binding.LguName] = 0;
                _rank.SetValue(owned.Node, 0);
                _unlocked.SetValue(owned.Node, true);
                owned.Load.Invoke(owned.Component, null);
                current = 1;
            }
            while (current < target)
            {
                _rank.SetValue(owned.Node, current);
                owned.Increment.Invoke(owned.Component, null);
                current++;
            }
            if (ReadLevel(owned) != target)
                throw new InvalidOperationException($"LGU did not apply '{owned.Binding.LguName}' rank {target}");
        }

        internal static bool TryCreatePurchase(string id, out IUpgradePurchaseEffect effect)
        {
            effect = null;
            if (!ByUpgradeId.TryGetValue(id, out Binding binding)) return true;
            if (!IsReady || !string.Equals(_saveKey, Y4NGZUpgradeManager.CurrentSaveKey, StringComparison.Ordinal)
                || !Owned.TryGetValue(binding.LguName, out OwnedNode owned)
                || owned.Component == null
                || (owned.RequiresNetwork && (!(owned.Component is NetworkBehaviour network) || !network.IsSpawned))
                || GameNetworkManager.Instance?.localPlayerController == null)
                return false;
            effect = new PurchaseEffect(owned);
            return true;
        }

        private sealed class PurchaseEffect : IUpgradePurchaseEffect
        {
            private readonly OwnedNode _owned;
            private readonly int _generation, _previous, _health;
            private bool _attempted;
            internal PurchaseEffect(OwnedNode owned)
            {
                _owned = owned;
                _generation = LguUpgradeBridge._generation;
                _previous = ReadLevel(owned);
                _health = GameNetworkManager.Instance.localPlayerController.health;
            }
            public bool TryApply(int level)
            {
                if (!IsReady || _generation != LguUpgradeBridge._generation
                    || level != _previous + 1 || level > (int)_maxRank.GetValue(_owned.Node) + 1)
                    return false;
                _attempted = true;
                try { Drive(_owned, level); return true; }
                catch (Exception error)
                {
                    Plugin.Log?.LogError($"[LGU] Purchase refused without charge: {error}");
                    return false;
                }
            }
            public void Rollback()
            {
                if (!_attempted || _generation != LguUpgradeBridge._generation) return;
                try
                {
                    Drive(_owned, _previous);
                    GameNetworkManager.Instance.localPlayerController.health = _health;
                }
                catch (Exception error) { FailClosed("rolling back a refused purchase", error); }
            }
        }

        private static void FilterSavedRanks(object __instance)
        {
            if (!_installed) return;
            var active = (IDictionary)_savedActive.GetValue(__instance);
            var levels = (IDictionary)_savedLevels.GetValue(__instance);
            foreach (OwnedNode owned in Owned.Values)
            {
                if (!owned.Imported) continue;
                active.Remove(owned.Binding.LguName);
                levels.Remove(owned.Binding.LguName);
            }
        }

        private static void PersistProviderSave()
        {
            object snapshot = Activator.CreateInstance(_saveType);
            _saveInfo.SetValue(_loadedStore, snapshot);
            string json = (string)_serialize.Invoke(null, new[] { snapshot });
            _saveRpc.Invoke(_loadedStore, new[] { _playerId.GetValue(_loadedStore), Encoding.ASCII.GetBytes(json), (object)false });
        }

        private static void BeforeReset(bool __0)
        {
            if (!_installed) return;
            // Disconnect already destroyed the player and is shutting RPCs down.
            // LGU destroys these components immediately after its wipe; do not Unwind them.
            if (!__0)
            {
                try { WithdrawLiveRanks(); }
                catch (Exception error) { FailClosed("withdrawing ranks before reset", error); }
            }
            Invalidate();
            _mayImport = false;
        }

        private static void AfterReset(bool __0)
        {
            if (!_installed) return;
            if (__0)
            {
                foreach (OwnedNode owned in Owned.Values) RestoreNode(owned);
                Owned.Clear();
                Unavailable.Clear();
                _creditNodes = null;
                _loadedStore = null;
                _saveKey = null;
                Y4NGZUpgradeCatalog.RegisterDefaults();
                Y4NGZUpgradeManager.RaiseUpgradesChanged();
            }
            else Y4NGZPersistentRunner.Run(AfterRoundReset(_generation));
        }

        private static IEnumerator AfterRoundReset(int generation)
        {
            // Wait for ResetUpgradeBusClientRpc to finish resetting its save, not an arbitrary delay.
            yield return null;
            if (!_installed || generation != _generation || _pendingLoads > 0) yield break;
            _mayImport = false;
            _loadingComplete = true;
            CompleteLoad();
        }

        private static void WithdrawLiveRanks()
        {
            if (GameNetworkManager.Instance?.localPlayerController == null) return;
            foreach (OwnedNode owned in Owned.Values)
                if (owned.Component != null && ReadLevel(owned) > 0) Drive(owned, 0);
        }

        private static void StopSickBeats(OwnedNode owned)
        {
            FieldInfo active = Field(owned.Component.GetType(), "EffectsActive");
            if (!(bool)active.GetValue(owned.Component)) return;
            active.SetValue(owned.Component, false);
            Method(owned.Component.GetType(), "HandlePlayerEffects", typeof(GameNetcodeStuff.PlayerControllerB))
                .Invoke(null, new object[] { GameNetworkManager.Instance.localPlayerController });
        }

        // Preserve LGU's item-progression choices, but exclude the nodes now sold for tokens.
        private static void RebuildCreditNodes(IList source)
        {
            if (_creditNodes == null || _creditNodes.GetType() != source.GetType())
                _creditNodes = (IList)Activator.CreateInstance(source.GetType());
            else _creditNodes.Clear();
            foreach (object node in source) if (!IsOwned(node)) _creditNodes.Add(node);
        }

        private static IList CreditNodes()
        {
            if (_creditNodes == null) RebuildCreditNodes((IList)_nodes.GetValue(_busInstance.GetValue(null)));
            return _creditNodes;
        }
        private static bool PickRandomPrefix(ref object __result)
        {
            if (!_installed) return true;
            IList nodes = CreditNodes();
            __result = nodes.Count == 0 ? null : nodes[UnityEngine.Random.Range(0, nodes.Count)];
            return false;
        }
        private static bool SelectChancePrefix(ref object __result)
        {
            if (!_installed) return true;
            IList nodes = CreditNodes();
            __result = null;
            if (nodes.Count == 0) return false;
            object mode = Property(_progressionType, "CurrentChancePerScrapMode").GetValue(null);
            if (mode.ToString() == "Random")
            {
                __result = nodes[UnityEngine.Random.Range(0, nodes.Count)];
                for (int tries = 0; tries < nodes.Count * 2
                    && (int)_maxRank.GetValue(__result) <= (int)_rank.GetValue(__result); tries++)
                    __result = nodes[UnityEngine.Random.Range(0, nodes.Count)];
            }
            else
            {
                var args = new object[2];
                foreach (object node in nodes)
                {
                    args[0] = __result; args[1] = node;
                    _selectNode.Invoke(null, args);
                    __result = args[0];
                }
            }
            return false;
        }
        private static bool SelectNearestPrefix(int __0, ref object __result)
        {
            if (!_installed) return true;
            __result = null;
            int nearest = int.MaxValue;
            foreach (object node in CreditNodes())
            {
                int delta = __0 - (int)_currentPrice.Invoke(node, null);
                if (delta > 0 && delta < nearest) { nearest = delta; __result = node; }
            }
            return false;
        }

        private static void FailClosed(string operation, Exception error)
        {
            Plugin.Log?.LogError($"[LGU] Integration stopped while {operation}; token purchases disabled: {error}");
            Shutdown();
            Y4NGZUpgradeCatalog.RegisterDefaults();
            Y4NGZUpgradeManager.RaiseUpgradesChanged();
        }

        internal static void Shutdown()
        {
            Y4NGZUpgradeManager.UpgradesChanged -= OnUpgradesChanged;
            Invalidate();
            foreach (OwnedNode owned in Owned.Values)
            {
                try
                {
                    if (GameNetworkManager.Instance?.localPlayerController != null
                        && owned.Imported && owned.Component != null && ReadLevel(owned) > 0)
                        Drive(owned, 0);
                }
                catch (Exception error) { Plugin.Log?.LogError($"[LGU] Could not restore {owned.Binding.LguName}: {error}"); }
                try { RestoreNode(owned); }
                catch (Exception error) { Plugin.Log?.LogError($"[LGU] Could not restore node metadata: {error}"); }
            }
            _installed = false;
            foreach (MethodBase hook in Hooks)
            {
                try { _harmony.Unpatch(hook, HarmonyPatchType.All, _harmony.Id); }
                catch (Exception error) { Plugin.Log?.LogError($"[LGU] Could not remove hook: {error}"); }
            }
            Hooks.Clear(); Owned.Clear(); Enabled.Clear(); Unavailable.Clear(); Suppressed.Clear();
            _creditNodes = null;
            _loadedStore = null; _saveKey = null; _harmony = null;
        }
    }
}
