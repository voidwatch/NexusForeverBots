namespace NexusForever.WorldServer.Game.Prerequisite.Static
{
    // TODO: name these from PrerequisiteType.tbl error messages
    public enum PrerequisiteType
    {
        None        = 0,
        LocationDistance = 16, // client evaluator: distance from the unit to WorldLocation2 <object> (same world, else false) compared with value (metres), certain
        ZoneServer  = 27,  // "You are out of bounds": value is a WorldZone id; the client stub returns false, so the server decides; same rule as 26 (likely)
        IsPlayer    = 39,  // client code: Equal = the unit is a character (unit type 20), NotEqual = it is not; value unused, certain
        BonusTierPoints = 235, // bonus ability tier points unlocked (row 28559 LessThan 7, "Maximum Ability Tier points reached"), certain
        BonusAmpPower   = 237, // bonus AMP power unlocked (row 28557 LessThan 10, "Maximum AMP power reached"), certain
        SettlerImprovement = 129, // name kept; decoded: object is a spell group id, "has an active effect from a spell of the group" (docs/ASSUMPTIONS.md)
        Unknown117  = 117, // public event <object> is running (client code), certain
        Unknown61   = 61,  // completed missions of PathEpisode <object> without flag 0x40 compared with value (client code), certain
        Unknown232  = 232, // "Plane requirement": client flags (message 0x0172, never sent by this server) & value (client code), certain
        Unknown194  = 194, // "Ground mounts cannot be used in this area": no WorldZone flag 0x10 in the zone chain (client code), certain
        Unknown195  = 195, // "Hoverboard mounts cannot be used in this area": same client function as 194, certain
        Unknown226  = 226, // "Entitlement requirement": the account/character holds Entitlement <object> (client code), certain
        Unknown270  = 270, // "Loyalty requirement": account loyalty points compared with value (client code); this server has none (0)
        Unknown74   = 74,  // object Challenge id: the challenge is unlocked (client challenge record flag; "The specified challenge is locked"); unlocked when it first starts
        Unknown125  = 125, // object Challenge id: the challenge is in its restart cooldown (client IsInCooldown); this server has none
        Unknown107  = 107, // object PathMission id: for a player always "not set" (client: Equal false, NotEqual true); for creatures their object bit
        Unknown155  = 155, // object PathMission id, value = client mission state 0 none, 1 scan not started, 2 available, 3 started, 4 complete
        // likely the instance's veteran mode (0 normal, 1 veteran): its 113 rows gate dungeon/adventure spell effects only ([TS] [SK] [KV] [STL] [UPJ],
        // Malgrave Adventure), in Equal 0 / Equal 1 pairs (rows 14409/14893) whose 1 rows carry ~1.5x the damage coefficients (median of 321 pairs)
        InstanceVeteran = 143,
        Unknown141  = 141, // "You are out of bounds": inside WorldLocation2 <object> radius (client code), certain
        Unknown69   = 69,  // object Challenge id: the challenge is running (client IsActivated flag, certain)
        InCombat = 28, // Equal = in combat, NotEqual = not (certain: failure texts "You must be in combat" row 903 Equal, "Cannot use in combat or in PvP instances" NotEqual)
        ClassResource = 64, // object = class resource index (Stat Resource0-6), value compared with its current amount ("Not enough Psi Points" object 1, "Insufficient Spell Power" object 4)
        ItemEquipped = 13, // value Item2 id, error text "Item equipment requirement not met": the item is equipped (Equal) or not (NotEqual)
        InventoryItem = 246, // value Item2 id (791 of 793 rows), error text "Inventory requirement not met": the item is in the bags (Equal) or not (NotEqual)
        PathMissionActive = 54, // value PathMission id, error text "You do not meet the player path mission requirement": the mission is open for the player and not done
        ChallengeProgress = 160, // object Challenge id, value compared with the active challenge's count ("Ch515 isActive and >4"; Diagnostic Tests 1078 gates its steps on 0..7)
        QuestObjectiveProgress5 = 36, // objective 5 progress of quest <object> compared with value (client evaluator case , objective index 5), certain
        Unknown102  = 102, // "Spell effect requirements": an effect of Spell4Effects row <object> is active (client code), certain
        QuestObjectiveProgress0 = 22, // objective 0 progress of quest <object> compared with value (client evaluator case 22, objective index 0), certain
        QuestObjectiveProgress1 = 23, // objective 1, same function
        QuestObjectiveProgress2 = 33, // objective 2
        QuestObjectiveProgress3 = 34, // objective 3
        QuestObjectiveProgress4 = 35, // objective 4
        Level       = 1,
        Race        = 2,
        Class       = 3,
        Faction     = 4,
        Reputation  = 5,
        Prerequisite = 11, // inferred: objectId is another prerequisite id, value 0 means it must not be met
        Quest       = 6,
        QuestObjectiveDone = 94, // client text "Quest objective requirements not met"; object quest id, value objective index (0-4), Equal/NotEqual only (871 uses). Row 40514 lets the Starfall firework holders of quest 10496 be activated while its objective 0 (activate 5 holders) is NotEqual = not done; row 6019 = quest 4944 accepted and its objective 4 not done. Read like 68: the objective is complete. Likely, docs/ASSUMPTIONS.md
        QuestObjectiveIndex = 68, // client text "Quest objective requirement not met"; object is a quest id, value an objective index (0 based): objective <value> of quest <object> is complete (client code, certain); rows 26625-26628 gate follow-ups on which of the four Lopp the player befriended in quests 7202/7204. Inferred, not proven
        Achievement = 7,
        Path        = 52,
        CCState     = 56,  // value is a CCState (Urgent Withdrawal needs "not 2" = not rooted; drills and probes need 8-10 = target knocked down or vulnerable)
        Tradeskill  = 19,  // object is a tradeskill id, compared with the character's level in it (0 when unknown); used by harvesting spells and quest chains
        PublicEventObjective = 118, // the client error text is "Public event objective requirement not met" (PrerequisiteType.tbl); object is a PublicEventObjective id (rows 12108+ gate quests behind Project Monolith event stages). First guessed as an Eldan augmentation, ids overlap
        Zone        = 26,  // client code: the current zone or one of its parents (WorldZone.parentZoneId) is value, certain
        AuraStacks  = 59,  // inferred: object is a spell (base) id, the comparison is against the number of its stacks on the unit (Engineer pet upgrade levels 1-6 against one object, "at most 1" on a public event carry effect)
        AuraActive  = 15,  // inferred from stealth spells: value is a spell4 id, Equal = the caster has it active
        SpellState  = 116, // inferred: stealth related state checks, value 28455 is used by every stealth ability
        Vital       = 73,
        VitalPercent = 71,  // object is a vital id (VitalModifier ids), value a percentage of its maximum
        QuestStateOf = 43,  // object quest id, value objective index: quest held, objective not complete and active in sequence (client code, certain)
        Currency293  = 293, // inferred: object is a currency id, value an amount (quests 10604/10605: currency 15 <= 5000, looks like a cap)
        Unknown109  = 109, // inferred: object is an achievement id (1537, 1634, 3460, Achievement.PrerequisiteId uses it 15 times), Equal = completed, NotEqual = not completed; 173 rows
        PathMission = 67,  // inferred: value is a PathMission id (rows [67,52] pair mission 156/158/160 with the path of that mission), Equal = completed, NotEqual = not completed; 186 rows
        CharacterTitle = 288, // object is a CharacterTitle id (row 43305: title 400 "Smart Shopper", quest 10510 "Learning to Shop" is offered while the player lacks it), Equal = owns it, NotEqual = does not
        SpellBaseId = 214,
        BaseFaction = 250,
        WorldType   = 240  // object is a World.type (3 PvP, 6 adventure, 7/8 warplot, 9 raid, 11 dungeon, 2/5 housing), Equal = in such a world, NotEqual = not (docs/ASSUMPTIONS.md)
    }
}
