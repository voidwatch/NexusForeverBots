using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Network;
using NexusForever.WorldServer.Game.Entity.Network.Model;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Prerequisite;
using NexusForever.WorldServer.Game.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Entity type 5: a gathering node (ore, tree, relic, plant; Creature2.CreationTypeEnum 5 with a TradeskillHarvestingInfoId, 5015 world
    /// database rows). They used to spawn as Simple and could not be gathered.
    /// Who may gather is the client's Unit:CanBeHarvestedBy (client code): HarvestUnit -> Creature2 -> TradeskillHarvestingInfo,
    /// whose prerequisite (type 19, tradeskill tier at least the node's) is evaluated for the player (certain).
    /// A node is gathered with the harvesting tool's spell (Item2 family 28, ItemSpecial.Spell4IdOnActivate, e.g. 42389 "Novice's Laser
    /// Pickaxe - Harvesting - Mining - Tier 1": two Damage effects at the node). One tool cast gathers the node (guess, docs/ASSUMPTIONS.md
    /// "Harvesting"): the loot is the node's researched drop table, the tradeskill xp the node tier's TradeskillTier.CraftXp, and the node comes
    /// back after the respawn time.
    /// </summary>
    [DatabaseEntity(EntityType.HarvestUnit)]
    public class HarvestUnit : NonPlayer
    {
        private static readonly Random random = new();
        private static Dictionary<uint, uint> toolSkills;
        private static readonly object toolGate = new();

        // set by the first gather: a tool spell has two damage effects and the node leaves the map only on the next update
        private bool gathered;

        public HarvestUnit()
            : base(EntityType.HarvestUnit)
        {
        }

        public TradeskillHarvestingInfoEntry HarvestingInfo => GameTableManager.Instance.TradeskillHarvestingInfo
            .GetEntry(GameTableManager.Instance.Creature2.GetEntry(CreatureId)?.TradeskillHarvestingInfoId ?? 0u);

        public TradeskillTierEntry RequiredTier => GameTableManager.Instance.TradeskillTier.GetEntry(HarvestingInfo?.TradeSkillTierId ?? 0u);

        /// <summary>
        /// Harvesting tool spells (Item2 family 28: laser pickaxe, laser chainsaw, relic blaster, ...) to the tradeskill of the tool's item category
        /// (Item2Category.TradeSkillId: 106 Mining, 116 Survivalist, 117 Relic Hunter).
        /// </summary>
        public static IReadOnlyDictionary<uint, uint> ToolSkills
        {
            get
            {
                lock (toolGate)
                {
                    if (toolSkills != null)
                        return toolSkills;

                    var result = new Dictionary<uint, uint>();
                    foreach (Item2Entry item in GameTableManager.Instance.Item.Entries.Where(e => e != null && e.Item2FamilyId == 28u))
                    {
                        uint skill = GameTableManager.Instance.Item2Category.GetEntry(item.Item2CategoryId)?.TradeSkillId ?? 0u;
                        uint spell = GameTableManager.Instance.ItemSpecial.GetEntry(item.ItemSpecialId00)?.Spell4IdOnActivate ?? 0u;
                        if (skill != 0u && spell != 0u)
                            result.TryAdd(spell, skill);
                    }

                    toolSkills = result;
                    return result;
                }
            }
        }

        /// <summary>Gathering skills without a tool item (Item2 family 28): Farmer. Their nodes are gathered by the class auto attack.</summary>
        public static bool IsHandGathered(uint tradeskillId)
        {
            TradeskillEntry skill = GameTableManager.Instance.Tradeskill.GetEntry(tradeskillId);
            return skill != null && (skill.Flags & 2u) != 0u && !ToolSkills.Values.Contains(tradeskillId);
        }

        /// <summary>The class auto attack spells (Class.Spell4IdAttackPrimary, Spell4IdAttackUnarmed) the client casts on interact.</summary>
        public static bool IsAutoAttack(Player player, uint spell4Id)
        {
            ClassEntry entry = GameTableManager.Instance.Class.GetEntry((uint)player.Class);
            return entry != null && spell4Id != 0u && (entry.Spell4IdAttackPrimary.Contains(spell4Id) || entry.Spell4IdAttackUnarmed.Contains(spell4Id));
        }

        /// <summary>The client's CanBeHarvestedBy: the HarvestingInfo prerequisite (tradeskill tier) is met.</summary>
        public bool CanBeHarvestedBy(Player player)
        {
            TradeskillHarvestingInfoEntry info = HarvestingInfo;
            if (info == null)
                return false;
            if (info.PrerequisiteId != 0u)
                return PrerequisiteManager.Instance.Meets(player, info.PrerequisiteId);

            TradeskillTierEntry tier = RequiredTier;
            return tier == null || (player.TradeskillManager.HasLearned(tier.TradeSkillId) && player.TradeskillManager.GetTier(tier.TradeSkillId) >= tier.Tier);
        }

        /// <summary>
        /// A spell's damage effect reached the node. Only a harvesting tool of the node's tradeskill gathers it; anything else does nothing
        /// (nodes take no combat damage). Returns true when the node was gathered.
        /// </summary>
        public bool OnToolHit(UnitEntity caster, uint spell4Id, uint rootSpell4Id = 0u)
        {
            if (gathered || IsDead || Map == null || caster is not Player player)
                return false;

            TradeskillTierEntry tier = RequiredTier;
            if (tier != null && (IsAutoAttack(player, spell4Id) || IsAutoAttack(player, rootSpell4Id)))
            {
                // the client's interact on a node uses the harvesting tool when one is in the tool slot and the node is not a Farmer node;
                // otherwise it casts the class auto attack at the node (certain: the interact function, the class row's Spell4IdAttackPrimary /
                // Spell4IdAttackUnarmed). "The ability to harvest plants is inherent to all players and does not require a trainer or special
                // tools" (wildstaronline-archive.fandom.com/wiki/Hobby); Mining: "Though technically not necessary, as the player's weapon will
                // do just fine, the proper tools will make the process much faster" (/wiki/Mining). One hit gathers here (guess, no speed data).
            }
            else if (!ToolSkills.TryGetValue(spell4Id, out uint toolSkill))
            {
                Diagnostics.FailureLedger.Record("harvest-not-a-tool", $"spell {spell4Id} hit node {CreatureId}", "nodes are gathered with a harvesting tool");
                return false;
            }
            else if (tier != null && toolSkill != tier.TradeSkillId)
            {
                Diagnostics.FailureLedger.Record("harvest-wrong-tool", $"tool of skill {toolSkill} on node of skill {tier.TradeSkillId}", $"creature {CreatureId}");
                return false;
            }
            if (!CanBeHarvestedBy(player))
            {
                Diagnostics.FailureLedger.Record("harvest-refused", $"node {CreatureId} needs skill {tier?.TradeSkillId} tier {tier?.Tier}",
                    $"player tier {(tier == null ? 0u : player.TradeskillManager.GetTier(tier.TradeSkillId))}");
                return false;
            }

            // loot: the node's researched drop table (Jabbithole "gathered from" rows), every row rolled by its chance; the most likely row is
            // always given so a gather never comes back empty (assumption)
            gathered = true;
            var research = Research.CreatureResearch.Get(CreatureId, (int)Math.Max(Level, 1u));
            int given = 0;
            if (research != null && research.Drops.Count > 0)
            {
                foreach (var (itemId, chance) in research.Drops)
                    if (random.NextDouble() * 100d < chance && Give(player, itemId))
                        given++;
                if (given == 0 && Give(player, research.Drops.OrderByDescending(d => d.Chance).First().ItemId))
                    given++;
            }
            Diagnostics.FailureLedger.Record(given > 0 ? "harvest-gathered" : "harvest-no-loot-data", $"node {CreatureId} skill {tier?.TradeSkillId} tier {tier?.Tier}",
                research == null ? "no researched drop table for the node name" : $"{given} items");

            if (tier != null)
                player.TradeskillManager.AddXp(tier.TradeSkillId, tier.CraftXp);
            player.QuestManager.ObjectiveUpdate(Quest.Static.QuestObjectiveType.KillCreature, CreatureId, 1u);
            player.QuestManager.OnQuestItemSource(CreatureId);

            Despawn();
            return true;
        }

        private static Dictionary<uint, uint> materialRemap;

        /// <summary>
        /// The researched node drops are pre-revamp item ids: 12 of them are copies flagged 0x4000 (or not used by any schematic) of a material
        /// with the same name that the schematics use now (e.g. Platinum Chunk 14188 -> 81839, Iron Chunk 12542 -> 81821). A gather gives the
        /// current material (likely: same name, one current candidate each; docs/ASSUMPTIONS.md "Harvesting").
        /// </summary>
        public static uint CurrentMaterial(uint itemId)
        {
            lock (toolGate)
            {
                if (materialRemap == null)
                {
                    var text = GameTableManager.Instance.TextEnglish;
                    string Name(Item2Entry e) => (text.GetEntry(e.LocalizedTextIdName) ?? "").Split('{')[0].Trim();
                    var materials = new HashSet<uint>();
                    foreach (TradeskillSchematic2Entry s in GameTableManager.Instance.TradeskillSchematic2.Entries.Where(e => e != null))
                        foreach (uint m in new[] { s.Item2IdMaterial00, s.Item2IdMaterial01, s.Item2IdMaterial02, s.Item2IdMaterial03, s.Item2IdMaterial04 })
                            if (m != 0u)
                                materials.Add(m);
                    var current = materials.Select(m => GameTableManager.Instance.Item.GetEntry(m)).Where(e => e != null && (e.Flags & 0x4000u) == 0u)
                        .GroupBy(Name).Where(g => g.Key.Length > 0 && g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Id);
                    materialRemap = new Dictionary<uint, uint>();
                    foreach (Item2Entry e in GameTableManager.Instance.Item.Entries.Where(e => e != null && ((e.Flags & 0x4000u) != 0u || !materials.Contains(e.Id))))
                        if (current.TryGetValue(Name(e), out uint to) && to != e.Id)
                            materialRemap[e.Id] = to;
                }
                return materialRemap.TryGetValue(itemId, out uint id) ? id : itemId;
            }
        }

        private static bool Give(Player player, uint itemId)
        {
            uint current = CurrentMaterial(itemId);
            if (current != itemId)
            {
                Diagnostics.FailureLedger.Record("harvest-material-current-id", $"item {itemId} -> {current}");
                itemId = current;
            }
            if (ItemManager.Instance.GetItemInfo(itemId) == null)
            {
                Diagnostics.FailureLedger.Record("loot-unknown-item", $"item {itemId} (harvest)");
                return false;
            }
            if (!player.Inventory.HasFreeSlot() && player.Session is Bots.BotSession)
                player.Inventory.DiscardJunk();
            if (!player.Inventory.HasFreeSlot())
            {
                Diagnostics.FailureLedger.Record("loot-bag-full", $"item {itemId}", "harvest");
                return false;
            }

            player.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 1u, ItemUpdateReason.Loot);
            return true;
        }

        protected override IEntityModel BuildEntityModel()
        {
            return new HarvestUnitEntityModel { CreatureId = CreatureId };
        }
    }
}
