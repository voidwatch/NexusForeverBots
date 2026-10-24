using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;

namespace NexusForever.WorldServer.Game.Quest
{
    /// <summary>
    /// Halon Ring and Murkmire were never released (scrapped content, docs/ASSUMPTIONS.md "Cut content").
    /// Quests in their zone trees are "cut": audits do not count them as missing spawns.
    /// </summary>
    public static class CutContent
    {
        private const uint ZoneMurkmire = 207u;
        private const uint ZoneHalonRing = 866u;

        public static bool IsCutZone(uint zoneId)
        {
            for (int depth = 0; zoneId != 0u && depth < 8; depth++)
            {
                if (zoneId == ZoneMurkmire || zoneId == ZoneHalonRing)
                    return true;
                zoneId = GameTableManager.Instance.WorldZone.GetEntry(zoneId)?.ParentZoneId ?? 0u;
            }
            return false;
        }

        /// <summary>
        /// A quest is cut when its zone is cut or when the client's turn-in location (Quest2.WorldLocation2IdReceiver) lies in a cut zone:
        /// about 200 quests carry no zone of their own but are handed in on Halon Ring (world 1068) (docs/ASSUMPTIONS.md, confidence likely).
        /// </summary>
        public static bool IsCutQuest(Quest2Entry entry)
        {
            if (entry == null)
                return false;
            if (IsCutZone(entry.WorldZoneId))
                return true;
            // quest categories 44 "Zone - Murkmire", 47 "Zone - Halon Ring 1 - Borellian Cluster", 55 "Zone - Halon Ring 2 - Apex-9"
            // (many of these quests carry zone id 1480, which is not in WorldZone)
            if (entry.QuestCategoryId is 44u or 47u or 55u)
                return true;
            WorldLocation2Entry receiver = GameTableManager.Instance.WorldLocation2.GetEntry(entry.WorldLocation2IdReceiver);
            return receiver != null && IsCutZone(receiver.WorldZoneId);
        }

        /// <summary>
        /// Developer test maps: asset path contains "test" or "sandbox" and the world has no name (all 287 such worlds in build 16042 are unnamed,
        /// e.g. Map\DeathwoodRanchDanaTestMap 825, Map\CombatSandbox 1163). Quests handed in there are test content, not released quests.
        /// </summary>
        public static bool IsTestWorld(uint worldId)
        {
            WorldEntry world = GameTableManager.Instance.World.GetEntry(worldId);
            if (world == null || world.LocalizedTextIdName != 0u || world.AssetPath == null)
                return false;
            string path = world.AssetPath.ToLowerInvariant();
            return path.Contains("test") || path.Contains("sandbox");
        }

        public static bool IsTestQuest(Quest2Entry entry)
        {
            if (entry == null)
                return false;
            WorldLocation2Entry receiver = GameTableManager.Instance.WorldLocation2.GetEntry(entry.WorldLocation2IdReceiver);
            return receiver != null && IsTestWorld(receiver.WorldId);
        }

        private const uint ZoneDreadmoor = 2197u;
        private const uint QuestCategoryDreadmoor = 41u;
        private static readonly uint[] zonesCoralus = { 2186u, 4679u };
        private const uint QuestCategoryCoralus = 43u;

        /// <summary>
        /// Dreadmoor (zone 2197, quest category 41 "Zone - Dreadmoor") was never released: described as an unreleased zone found in the client data
        /// (YouTube "Wildstar - Dreadmoor (unreleased Zone)", wiki summaries), no archived Jabbithole zone or NPC page names it (24,000 NPC pages).
        /// Coralus (zones 2186/4679, category 43): the archived Jabbithole zone page (coralus-89, 2014) lists no NPCs, quests, challenges or path
        /// missions (Thayd: 241 NPCs, 27 quests), exactly like the cut Halon Ring zone pages, and no NPC page is found in Coralus.
        /// Reported separately from the Halon Ring / Murkmire cut list (docs/ASSUMPTIONS.md, confidence likely).
        /// </summary>
        public static bool IsUnreleasedZoneQuest(Quest2Entry entry)
        {
            if (entry == null)
                return false;
            if (entry.QuestCategoryId == QuestCategoryDreadmoor || entry.QuestCategoryId == QuestCategoryCoralus)
                return true;
            for (uint zoneId = entry.WorldZoneId, depth = 0; zoneId != 0u && depth < 8; depth++)
            {
                if (zoneId == ZoneDreadmoor || System.Array.IndexOf(zonesCoralus, zoneId) >= 0)
                    return true;
                zoneId = GameTableManager.Instance.WorldZone.GetEntry(zoneId)?.ParentZoneId ?? 0u;
            }
            return false;
        }

        private const uint ZoneQuestTestIsland = 262u;
        private const uint ZonePublicEventIsland = 1663u;
        private static readonly System.Text.RegularExpressions.Regex devTag = new(@"\[(PH|PROTOTYPE|DNT|TEST)\]|Prototyp|Cheat|QuestTestIsland|Quest Test",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Developer content: the quest lies in Quest Test Island (262) or PublicEventIsland (1663), its title is tagged [DNT]/[TEST]/[DEPRECATED],
        /// or every creature that gives or receives it is
        /// tagged as a placeholder/prototype/test NPC in Creature2.Description ("[PH] ...", "[PROTOTYPE] ...", "Cheat Leveling Quest Giver", "Quest Test").
        /// None of these have archived Jabbithole pages (docs/ASSUMPTIONS.md, confidence likely).
        /// </summary>
        public static bool IsDevQuest(Quest2Entry entry, System.Collections.Generic.IReadOnlyCollection<uint> npcs)
        {
            if (entry == null)
                return false;
            for (uint zoneId = entry.WorldZoneId, depth = 0; zoneId != 0u && depth < 8; depth++)
            {
                if (zoneId == ZoneQuestTestIsland || zoneId == ZonePublicEventIsland)
                    return true;
                zoneId = GameTableManager.Instance.WorldZone.GetEntry(zoneId)?.ParentZoneId ?? 0u;
            }
            // titles tagged by the developers: [DNT] (do not translate), [TEST], [DEPRECATED], [DEPR]
            string title = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(entry.LocalizedTextIdTitle) ?? "";
            if (title.Contains("[DNT]") || title.Contains("[TEST]") || title.Contains("[DEPRECATED]") || title.Contains("[DEPR]"))
                return true;
            if (npcs == null || npcs.Count == 0)
                return false;
            foreach (uint id in npcs)
            {
                string description = GameTableManager.Instance.Creature2.GetEntry(id)?.Description;
                if (description == null || !devTag.IsMatch(description))
                    return false;
            }
            return true;
        }
    }
}
