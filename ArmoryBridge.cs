using System;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Every read from Y4NGZUpgrades into Better Armory (#267), and the mirror image of Better
    /// Armory's own <c>UpgradesBridge</c>.
    ///
    /// Neither assembly references the other. Better Armory's prize, ammo reserve, presentation
    /// and optional weapon-hosting capabilities are bound here by full type name, once, and
    /// cached. Capability results are read live so local settings remain authoritative.
    /// A required member that cannot be resolved logs one
    /// warning for the session and then permanently returns the value that means "Better Armory is
    /// not here": empty pools, no prefab, a zero balance, nothing to stop. Every one of those is a
    /// legitimate state on an Upgrades-only install, which is why no caller needs a second code path.
    ///
    /// Presence is answered by the Chainloader rather than by whether a type resolved, so a
    /// signature drift inside an installed Better Armory reads as a failure to warn about rather
    /// than as an absent plugin.
    /// </summary>
    internal static class ArmoryBridge
    {
        private const string ArmoryPluginGuid = "com.y4ngz.betterarmory";
        private const string PrizeApiTypeName = "Y4NGZUpgrades.Weapons.ArmoryPrizeApi";
        private const string AmmoApiTypeName = "Y4NGZUpgrades.Weapons.ArmoryAmmoReserveApi";
        private const string PresentationApiTypeName = "Y4NGZUpgrades.Weapons.ArmoryPresentationApi";
        private const string HostingApiTypeName = "Y4NGZUpgrades.Weapons.WeaponHostingApi";

        private static readonly string[] NoIds = new string[0];
        private static readonly Type[] OneString = { typeof(string) };

        private static bool _presenceResolved;
        private static bool _present;

        /// <summary>Whether Better Armory is loaded at all. Cached; the plugin list cannot change
        /// after the Chainloader has run.</summary>
        internal static bool IsArmoryPresent
        {
            get
            {
                if (_presenceResolved)
                    return _present;
                _presenceResolved = true;

                try
                {
                    _present = BepInEx.Bootstrap.Chainloader.PluginInfos != null
                        && BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ArmoryPluginGuid);
                }
                catch
                {
                    _present = false;
                }

                return _present;
            }
        }

        // -- LUCKY-8 prizes ---------------------------------------------------------------------

        private static readonly Lazy<MethodInfo> WeaponPrizeIds =
            Method(PrizeApiTypeName, "GetWeaponPrizeIds", Type.EmptyTypes);
        private static readonly Lazy<MethodInfo> WeaponDisplayName =
            Method(PrizeApiTypeName, "GetWeaponDisplayName", OneString);
        private static readonly Lazy<MethodInfo> WeaponPrizePrefab =
            Method(PrizeApiTypeName, "GetWeaponPrizePrefab", OneString);
        private static readonly Lazy<MethodInfo> WeaponItem =
            Method(PrizeApiTypeName, "GetWeaponItem", OneString);
        private static readonly Lazy<MethodInfo> WeaponFingerprint =
            Method(PrizeApiTypeName, "GetWeaponFingerprintTokens", Type.EmptyTypes);
        private static readonly Lazy<MethodInfo> WeaponPrizeInit =
            Method(PrizeApiTypeName, "InitializeWeaponPrize", new[] { typeof(GameObject), typeof(string) });

        private static readonly Lazy<MethodInfo> AmmoPrizeIds =
            Method(PrizeApiTypeName, "GetAmmoPrizeIds", Type.EmptyTypes);
        private static readonly Lazy<MethodInfo> AmmoDisplayName =
            Method(PrizeApiTypeName, "GetAmmoDisplayName", OneString);
        private static readonly Lazy<MethodInfo> AmmoPrizePrefab =
            Method(PrizeApiTypeName, "GetAmmoPrizePrefab", OneString);
        private static readonly Lazy<MethodInfo> AmmoItem =
            Method(PrizeApiTypeName, "GetAmmoItem", OneString);

        internal static string[] GetWeaponPrizeIds() => Strings(WeaponPrizeIds);

        internal static string GetWeaponDisplayName(string weaponId) =>
            Invoke(WeaponDisplayName, weaponId) as string ?? weaponId;

        internal static GameObject GetWeaponPrizePrefab(string weaponId) =>
            Invoke(WeaponPrizePrefab, weaponId) as GameObject;

        internal static Item GetWeaponItem(string weaponId) =>
            Invoke(WeaponItem, weaponId) as Item;

        internal static string[] GetWeaponFingerprintTokens() => Strings(WeaponFingerprint);

        internal static void InitializeWeaponPrize(GameObject instance, string weaponId)
        {
            MethodInfo method = Resolve(WeaponPrizeInit);
            if (method == null || instance == null)
                return;

            try { method.Invoke(null, new object[] { instance, weaponId }); }
            catch (Exception ex) { Warn("InitializeWeaponPrize", ex); }
        }

        internal static string[] GetAmmoPrizeIds() => Strings(AmmoPrizeIds);

        internal static string GetAmmoDisplayName(string pickupId) =>
            Invoke(AmmoDisplayName, pickupId) as string ?? pickupId;

        internal static GameObject GetAmmoPrizePrefab(string pickupId) =>
            Invoke(AmmoPrizePrefab, pickupId) as GameObject;

        internal static Item GetAmmoItem(string pickupId) =>
            Invoke(AmmoItem, pickupId) as Item;

        // -- Ammo reserve (Employee File AMMUNITION panel) ---------------------------------------

        private static readonly Lazy<MethodInfo> AmmoFamilyIds =
            Method(AmmoApiTypeName, "GetAmmoFamilyIds", Type.EmptyTypes);
        private static readonly Lazy<MethodInfo> AmmoFamilyLabel =
            Method(AmmoApiTypeName, "GetAmmoFamilyLabel", OneString);
        private static readonly Lazy<MethodInfo> AmmoFamilyReserve =
            Method(AmmoApiTypeName, "GetAmmoFamilyReserve", OneString);

        internal static string[] GetAmmoFamilyIds() => Strings(AmmoFamilyIds);

        internal static string GetAmmoFamilyLabel(string familyId) =>
            Invoke(AmmoFamilyLabel, familyId) as string ?? familyId;

        internal static int GetAmmoFamilyReserve(string familyId)
        {
            object value = Invoke(AmmoFamilyReserve, familyId);
            return value is int amount ? amount : 0;
        }

        // -- Held-item stow ----------------------------------------------------------------------

        private static readonly Lazy<MethodInfo> StopPresentation =
            Method(PresentationApiTypeName, "StopPresentationForHeldItemStow", new[] { typeof(PlayerControllerB) });

        internal static void StopPresentationForHeldItemStow(PlayerControllerB player)
        {
            MethodInfo method = Resolve(StopPresentation);
            if (method == null || player == null)
                return;

            try { method.Invoke(null, new object[] { player }); }
            catch (Exception ex) { Warn("StopPresentationForHeldItemStow", ex); }
        }

        // -- Incoming firearm resistance (upgrade descriptions) ---------------------------------

        private static readonly Lazy<Func<bool>> IncomingFirearmResistance = new Lazy<Func<bool>>(() =>
        {
            Type type = FindType(HostingApiTypeName);
            MethodInfo method = type?.GetMethod(
                "IsIncomingFirearmResistanceAvailable",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            // Older Better Armory versions legitimately lack this optional capability.
            return method?.ReturnType == typeof(bool)
                ? (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), method)
                : null;
        });

        internal static bool SupportsIncomingFirearmResistance
        {
            get
            {
                try { return IncomingFirearmResistance.Value?.Invoke() ?? false; }
                catch (Exception ex)
                {
                    Warn("IsIncomingFirearmResistanceAvailable", ex);
                    return false;
                }
            }
        }

        // -- Plumbing ----------------------------------------------------------------------------

        private static Lazy<MethodInfo> Method(string typeName, string methodName, Type[] parameters)
        {
            return new Lazy<MethodInfo>(() =>
            {
                Type type = FindType(typeName);
                if (type == null)
                    return null;

                MethodInfo method = type.GetMethod(
                    methodName, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
                if (method == null)
                {
                    Plugin.Log?.LogWarning(
                        $"[ArmoryBridge] {typeName}.{methodName} was not found. That bridge is " +
                        "disabled for this session.");
                }

                return method;
            });
        }

        /// <summary>Resolves once and swallows a resolution failure, so a call site never has to
        /// guard the lookup itself.</summary>
        private static MethodInfo Resolve(Lazy<MethodInfo> lazy)
        {
            try { return lazy.Value; }
            catch { return null; }
        }

        private static object Invoke(Lazy<MethodInfo> lazy, string argument)
        {
            MethodInfo method = Resolve(lazy);
            if (method == null)
                return null;

            try { return method.Invoke(null, new object[] { argument }); }
            catch (Exception ex)
            {
                Warn(method.Name, ex);
                return null;
            }
        }

        private static string[] Strings(Lazy<MethodInfo> lazy)
        {
            MethodInfo method = Resolve(lazy);
            if (method == null)
                return NoIds;

            try { return method.Invoke(null, null) as string[] ?? NoIds; }
            catch (Exception ex)
            {
                Warn(method.Name, ex);
                return NoIds;
            }
        }

        /// <summary>One line per failing member for the whole session. These sit on prize,
        /// menu-render and stow paths, all of which repeat.</summary>
        private static readonly System.Collections.Generic.HashSet<string> Warned =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static void Warn(string member, Exception ex)
        {
            if (!Warned.Add(member))
                return;
            Plugin.Log?.LogWarning($"[ArmoryBridge] {member} failed safely: {ex.Message}");
        }

        private static Type FindType(string fullName)
        {
            if (!IsArmoryPresent)
                return null;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                        return type;
                }
                catch
                {
                }
            }

            if (IsArmoryPresent)
            {
                Plugin.Log?.LogWarning(
                    $"[ArmoryBridge] Better Armory is installed but '{fullName}' was not found. " +
                    "That bridge is disabled for this session.");
            }

            return null;
        }
    }
}
