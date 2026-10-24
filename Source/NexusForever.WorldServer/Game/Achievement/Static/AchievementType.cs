namespace NexusForever.WorldServer.Game.Achievement.Static
{
    // TODO: name more
    public enum AchievementType
    {
        KillCreatureEntry = 1,
        KillCreatureGroup = 2,
        QuestComplete     = 3,
        // tradeskill achievements (decoded from the 761 TradeskillAchievementReward rows and their titles/descriptions, certain)
        TradeskillCraftItem       = 35, // ObjectId = crafted item, Value = how many ("by crafting 3 sets of $m(item=14838)")
        TradeskillTierReached     = 37, // ObjectId = TradeskillTier row ("You reached Apprentice Architect")
        TradeskillCraftChecklist  = 40, // checklist ObjectId = crafted item ("crafting a batch of each")
        TradeskillLearnSchematics = 44, // checklist ObjectId = schematic ("You learned all of the following recipes")
        MapComplete       = 53,
        TradeskillCraftAny        = 76, // checklist ObjectId = crafted item, Value = count ("crafting 5 of X, Y or Z")
        ItemConsume       = 80
    }
}
