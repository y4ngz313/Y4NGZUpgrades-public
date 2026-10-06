namespace Y4NGZUpgrades.Gui;

/// <summary>Where imported Late Game Upgrades rows live in the player menu (#435).</summary>
internal enum LguUpgradeLayout
{
    /// <summary>In their authored class trees, gated and connected like any other row.</summary>
    ClassTrees,

    /// <summary>In one flat Augments catalog with no gates and no branch lines.</summary>
    SeparateCatalog
}

/// <summary>The upgrade screen a menu route lands on.</summary>
internal enum UpgradeCatalogView
{
    /// <summary>The native class trees.</summary>
    ClassTrees,

    /// <summary>The flat Augments catalog of imported rows.</summary>
    Augments,

    /// <summary>Neither catalog sells anything: the Employee File hub.</summary>
    EmployeeFile
}

/// <summary>
/// Where imported Late Game Upgrades rows are registered, and which upgrade screen a menu route
/// may open (#435). The catalog pass, the rail and every re-render read these rules, so a route
/// can never open a screen the rail does not offer. Unity-free so the deterministic checks
/// exercise the shipped rules rather than a copy.
/// </summary>
internal static class LguCatalogLayout
{
    /// <summary>
    /// The layout a fresh profile binds (#493), and the one the one-time default migration moves
    /// a profile still holding the old ClassTrees default to.
    /// </summary>
    internal const LguUpgradeLayout DefaultLayout = LguUpgradeLayout.SeparateCatalog;

    /// <summary>
    /// Whether a catalog pass registers imported rows in the flat Augments catalog. While the
    /// resolved policy hides the native catalog (LGU-only mode with Late Game Upgrades integrated)
    /// it always does, whatever the configured layout: the class trees would hold nothing native.
    /// Otherwise the configured layout decides, and an unrecognised value keeps the class trees.
    /// </summary>
    internal static bool UsesFlatCatalog(LguUpgradeLayout configured, bool nativeCatalogHidden)
    {
        return nativeCatalogHidden || configured == LguUpgradeLayout.SeparateCatalog;
    }

    /// <summary>The class trees are offered unless the resolved policy hides the native catalog.</summary>
    internal static bool ClassTreesAvailable(bool nativeCatalogHidden)
    {
        return !nativeCatalogHidden;
    }

    /// <summary>
    /// Augments is offered only while the registered catalog holds flat rows. A provider that is
    /// absent, not integrated or still loading, every imported row switched off, or the
    /// class-tree layout all leave it empty.
    /// </summary>
    internal static bool AugmentsAvailable(int flatRows)
    {
        return flatRows > 0;
    }

    /// <summary>
    /// The screen a route to <paramref name="requested"/> lands on. An unavailable catalog falls
    /// to the other one, and to the Employee File when neither sells anything. The answer is
    /// always a screen that resolves to itself, so a fallback can never bounce back.
    /// </summary>
    internal static UpgradeCatalogView Resolve(
        UpgradeCatalogView requested,
        bool nativeCatalogHidden,
        int flatRows)
    {
        bool classTrees = ClassTreesAvailable(nativeCatalogHidden);
        bool augments = AugmentsAvailable(flatRows);
        switch (requested)
        {
            case UpgradeCatalogView.ClassTrees:
                if (classTrees)
                    return UpgradeCatalogView.ClassTrees;
                return augments ? UpgradeCatalogView.Augments : UpgradeCatalogView.EmployeeFile;
            case UpgradeCatalogView.Augments:
                if (augments)
                    return UpgradeCatalogView.Augments;
                return classTrees ? UpgradeCatalogView.ClassTrees : UpgradeCatalogView.EmployeeFile;
            default:
                return UpgradeCatalogView.EmployeeFile;
        }
    }
}
