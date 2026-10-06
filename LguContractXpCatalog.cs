using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    internal static class LguContractXpCatalog
    {
        internal readonly struct Award
        {
            internal readonly string Kind, Section, Label;
            internal readonly int BaseXp;
            internal Award(string kind, string section, string label, int baseXp)
            { Kind = kind; Section = section; Label = label; BaseXp = baseXp; }
        }
        internal static readonly Award[] Awards =
        {
            new Award("LguLaptopLogin", "Data Retrieval Laptop Login", "Laptop login opened", 8),
            new Award("LguDataRecovered", "Data Retrieval Recovered", "Survey data recovered", 20),
            new Award("LguHoardingBugExterminated", "Extermination Bug Culled", "Hoarding bug culled", 8),
            new Award("LguNestDestroyed", "Extermination Nest Destroyed", "Bug nest destroyed", 25),
            new Award("LguScavengerTreated", "Extraction Scavenger Treated", "Scavenger treated", 10),
            new Award("LguScavengerRecovered", "Extraction Scavenger Recovered", "Scavenger carried aboard", 25),
            new Award("LguRitualItemPlaced", "Exorcism Ritual Item", "Ritual item placed", 8),
            new Award("LguDemonIdentified", "Exorcism Demon Identified", "Demon identified", 15),
            // #457: 8, not 4 - a 4 XP act rounds away to nothing in the report.
            new Award("LguWireCut", "LGU Defusal Wire", "Device wire cut", 8),
            new Award("LguDeviceDefused", "LGU Defusal Complete", "Device defused", 10)
        };
        internal static bool TryGet(string kind, out Award award)
        {
            foreach (Award candidate in Awards)
                if (string.Equals(kind, candidate.Kind, StringComparison.Ordinal))
                { award = candidate; return true; }
            award = default;
            return false;
        }
    }
}
