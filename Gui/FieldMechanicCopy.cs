using System.Collections.Generic;

namespace Y4NGZUpgrades.Gui;

/// <summary>
/// Which Field Mechanic level lines the inspector draws. Level 1 is two effects from two plugins:
/// the fuel-pump stall reduction needs Ship Systems and the camera shutdown needs LethalCCTV, and
/// without either the level is not sold at all (#441). The unique-only variant drops the door
/// level (#435). Unity-free so the checks select exactly the copy the menu shows.
/// </summary>
internal static class FieldMechanicCopy
{
    internal const string Summary =
        "On-site sabotage of facility hardware. Every hold interaction takes 1.2 seconds.";
    internal const string Pumps = "Fuel pumps are half as likely to stall.";
    internal const string Cameras = "Company CCTV cameras can be disabled for the rest of the round.";
    internal const string Doors = "Standard locked doors can be hacked open, no key required.";
    internal const string Turrets =
        "Turrets can be disabled for 90 seconds from the Field Operations tablet or a nearby hold, "
        + "with a 20 second cooldown.";

    /// <summary>
    /// One effect line per level the row sells, in order, or null when the authored description
    /// already reads right because both providers are installed.
    /// </summary>
    internal static string[] SelectLevels(bool pumps, bool cameras, bool uniqueOnly)
    {
        if (pumps && cameras)
            return null;

        var levels = new List<string>(3);
        if (pumps)
            levels.Add(Pumps);
        else if (cameras)
            levels.Add(Cameras);

        if (!uniqueOnly)
            levels.Add(Doors);

        levels.Add(Turrets);
        return levels.ToArray();
    }
}
