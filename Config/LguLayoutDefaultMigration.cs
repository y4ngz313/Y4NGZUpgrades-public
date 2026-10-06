using Y4NGZUpgrades.Gui;

namespace Y4NGZUpgrades.Config
{
    /// <summary>
    /// One-time move of <c>[LGU] Appearance - LGU Upgrade Layout</c> from its old ClassTrees
    /// default to SeparateCatalog (#493). The key is persisted, so a new code default reaches no
    /// existing profile on its own. ClassTrees is the only default the key ever shipped with, and
    /// a stored ClassTrees cannot be told apart from an untouched default, so an unstamped profile
    /// holding it takes the new default once. Every unstamped profile is stamped by that pass,
    /// whatever it held, so a player who picks ClassTrees afterwards keeps it.
    ///
    /// Unity-free and free of BepInEx types so the deterministic checks exercise the decision
    /// directly; <see cref="Y4NGZConfigFiles"/> owns the ConfigEntry, the save and the stamp.
    /// </summary>
    internal static class LguLayoutDefaultMigration
    {
        /// <summary>The default every release before #493 bound.</summary>
        internal const LguUpgradeLayout OldDefault = LguUpgradeLayout.ClassTrees;

        /// <summary>One profile's result of the pass.</summary>
        internal readonly struct Outcome
        {
            internal Outcome(LguUpgradeLayout layout, bool changed, bool stamps)
            {
                Layout = layout;
                Changed = changed;
                Stamps = stamps;
            }

            /// <summary>The layout the profile holds after the pass.</summary>
            internal LguUpgradeLayout Layout { get; }

            /// <summary>True when the caller has to write <see cref="Layout"/> back.</summary>
            internal bool Changed { get; }

            /// <summary>True when the caller stamps the profile once its save holds the result.</summary>
            internal bool Stamps { get; }
        }

        /// <summary>
        /// The whole decision for one profile: <paramref name="stamped"/> is whether its marker
        /// exists, <paramref name="stored"/> the value bound for the key (the default when the key
        /// was absent). A stamped profile is never judged again.
        /// </summary>
        internal static Outcome Decide(bool stamped, LguUpgradeLayout stored)
        {
            if (stamped)
                return new Outcome(stored, false, false);

            return stored == OldDefault
                ? new Outcome(LguCatalogLayout.DefaultLayout, true, true)
                : new Outcome(stored, false, true);
        }
    }
}
