namespace NexusForever.WorldServer.Game.Prerequisite.Static
{
    public enum PrerequisiteComparison
    {
        Equal    = 1,
        NotEqual = 2,
        // 3-6 decoded from Prerequisite failure texts (certain, docs/ASSUMPTIONS.md): "You must be at least level 17 to enter Stormtalon's Lair"
        // and "Requires an Arena Rating of 1400 or higher" use 4; "Maximum AMP power reached" (t237 c5 v10) and "Maximum number of Bots already
        // summoned" (t59 c5 v2, Engineer limit 2) use 5; "You're out of Vitalus Treatments" uses 3 with value 0; quest level windows pair 4 and 6
        // (quest 7980, con level 15: c4 14 and c6 15, empty with the old 4 = GreaterThan / 6 = LessThan); heat gauge bands c4 75 & c5 90.
        GreaterThan = 3,
        GreaterThanOrEqual = 4,
        LessThan = 5,
        LessThanOrEqual = 6
    }
}
