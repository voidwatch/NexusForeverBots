namespace NexusForever.WorldServer.Game.Quest.Static
{
    public enum TargetGroupType
    {
        CreatureIdGroup     = 1, // This is the majority of entries in TargetGroups TBL
        CreatureIdGroup2    = 2, // Unsure how this is used, possibly an exclusion list
        FactionIdGroup      = 3,
        NotFactionIdGroup   = 4, // This is used to say "Don't target this faction"
        PlayerRaceIdGroup   = 5,
        Unknown7            = 7,
        CreatureIdGroup9    = 9, // creature ids like type 1 (group 4869 lists the Halon Ring quest objects of quest 7840/7850)
        OtherTargetGroup    = 10, // This is used to target other TargetGroup(s) data
        Unknown11           = 11,
        CreatureRaceIdGroup = 12, // This targets the Creature2Entry.RaceId
        Unknown13           = 13
    }
}
