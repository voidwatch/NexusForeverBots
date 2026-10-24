using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using NexusForever.Shared.Database;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Tradeskill;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Bots that use tradeskills the way a player does, through the real client message handlers:
    /// - learn a skill with ClientTradeskillLearn (TradeskillHandler),
    /// - raise a crafting skill to a tier by crafting schematics of the current tier at a crafting station (ClientCraftingCraftItemSimple, or
    ///   ClientCraftingCraftItem + ClientCraftingSimpleCraft for coordinate professions); the xp is the table's TradeskillTier.CraftXp per craft,
    /// - gather a harvest node with the harvesting tool's item use (ClientItemUse -> tool spell -> HarvestUnit).
    /// Materials and tools have no vendor or loot source in the data for most items (research/tradeskill-materials.tsv): what the bot lacks is
    /// granted and counted (tradeskill-qa-material-grant, tradeskill-qa-tool-grant). The crafted training items are discarded.
    /// </summary>
    public static class BotTradeskills
    {
        private const float StationRange = 15f;
        private const int CraftsPerThink = 2;

        public sealed class Goal
        {
            public uint Skill;
            public uint Tier;
            public uint Quest;
            public DateTime Since = DateTime.UtcNow;
            public int Crafts;
            // material -> when gathering it is given up (then the QA grant fills in)
            public Dictionary<uint, DateTime> GatherUntil = new();
            // materials whose nodes need a higher gathering tier: the bot gathers lower nodes of that skill first
            public HashSet<uint> Leveling = new();
            public Dictionary<uint, uint> InBag = new();
        }

        /// <summary>Time a bot spends raising a gathering skill to the tier of a material's nodes before the QA grant fills in.</summary>
        private static readonly TimeSpan LevelingBudget = TimeSpan.FromMinutes(8);

        /// <summary>Time a bot spends gathering one material for its craft goal before the QA grant fills the rest.</summary>
        private static readonly TimeSpan GatherBudget = TimeSpan.FromMinutes(3);
        private static volatile Dictionary<uint, HashSet<uint>> materialNodes;

        private static readonly ConcurrentDictionary<ulong, Goal> goals = new();
        private static volatile Dictionary<ushort, List<(uint Creature, uint Skill, Vector3 Position)>> stations;
        private static volatile Dictionary<ushort, List<(uint Creature, Vector3 Position)>> nodes;
        private static int stationsLoading;

        public static bool HasGoal(Player me) => goals.ContainsKey(me.CharacterId);

        /// <summary>QA harness: credits a bot lacks for a swapped-out skill's RelearnCost (counted).</summary>
        public static void PayRelearn(Player me, uint skill)
        {
            ulong relearn = me.TradeskillManager.RelearnCost(skill);
            ulong credits = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            if (relearn > credits)
            {
                me.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, relearn - credits);
                Diagnostics.FailureLedger.Record("tradeskill-qa-relearn-credit-grant", $"skill {skill}", $"{relearn - credits} credits of {relearn}");
                Diagnostics.QuestTruth.Grant(me, "relearn-credit", skill);
            }
        }

        /// <summary>
        /// Learns a tradeskill through the trainer message (ClientTradeskillLearn: skill, skill to swap out). With two active trainer skills the
        /// bot swaps one out like the trainer window's "swap with" button: never the skill of its current craft goal, otherwise the one with the
        /// lowest tier (cheapest to relearn); a swapped-out skill keeps its xp and costs its tier's RelearnCost to come back.
        /// </summary>
        public static bool Learn(Player me, uint skill)
        {
            TradeskillManager manager = me.TradeskillManager;
            if (manager.HasLearned(skill))
                return true;

            uint drop = 0u;
            if (TradeskillManager.CountsAgainstLimit(skill) && manager.LimitedSkillCount >= TradeskillManager.MaxTrainerSkills)
            {
                uint keep = goals.TryGetValue(me.CharacterId, out Goal goal) ? goal.Skill : 0u;
                drop = manager.LearnedSkills.Where(id => id != keep && TradeskillManager.CountsAgainstLimit(id))
                    .OrderBy(id => manager.GetTier(id)).ThenBy(id => id).FirstOrDefault();
                if (drop == 0u)
                {
                    Diagnostics.FailureLedger.Record("bot-tradeskill-no-swap", $"skill {skill}", $"both active skills are needed (goal {keep})");
                    return false;
                }
            }

            // a swapped-out skill costs its tier's RelearnCost: the QA harness pays what the bot lacks (counted), else bots that swap between a
            // gathering and a crafting skill for their materials stop at the trainer for good (1085 refusals per gate before)
            PayRelearn(me, skill);

            TradeskillHandler.HandleLearn(me.Session, BotPackets.Build<ClientTradeskillLearn>(w =>
            {
                w.Write(skill);
                w.Write(drop);
            }));
            bool ok = manager.HasLearned(skill);
            if (ok && drop != 0u)
                Diagnostics.FailureLedger.Record("bot-tradeskill-swapped", $"skill {drop} -> {skill}");
            return ok;
        }

        /// <summary>
        /// A quest needs a tier of a crafting skill the bot has learned but not reached: work towards it by crafting (replaces the QA tier grant).
        /// </summary>
        public static void SetGoal(Player me, uint skill, uint tier, uint quest)
        {
            if (goals.TryGetValue(me.CharacterId, out Goal current) && current.Skill == skill && current.Tier >= tier)
                return;

            goals[me.CharacterId] = new Goal { Skill = skill, Tier = tier, Quest = quest };
            Diagnostics.FailureLedger.Record("tradeskill-bot-craft-goal", $"skill {skill} tier {tier}", $"quest {quest}");
        }

        /// <summary>One think of a bot with a craft goal; true while the bot is busy with it.</summary>
        public static bool Think(Player me)
        {
            if (!goals.TryGetValue(me.CharacterId, out Goal goal))
                return false;

            TradeskillManager manager = me.TradeskillManager;
            PickTalents(me, goal.Skill);
            if (!manager.HasLearned(goal.Skill) || manager.GetTier(goal.Skill) >= goal.Tier)
            {
                goals.TryRemove(me.CharacterId, out _);
                if (manager.HasLearned(goal.Skill))
                    Diagnostics.FailureLedger.Record("tradeskill-tier-reached-by-crafting", $"skill {goal.Skill} tier {goal.Tier}",
                        $"{goal.Crafts} crafts in {(DateTime.UtcNow - goal.Since).TotalSeconds:0} s for quest {goal.Quest}");
                return false;
            }

            // a long goal is given up after 20 minutes so the bot does not stand at a station forever (counted)
            if (DateTime.UtcNow - goal.Since > TimeSpan.FromMinutes(20))
            {
                goals.TryRemove(me.CharacterId, out _);
                Diagnostics.FailureLedger.Record("tradeskill-craft-goal-timeout", $"skill {goal.Skill} tier {goal.Tier}", $"xp {manager.GetXp(goal.Skill)} after {goal.Crafts} crafts");
                return false;
            }

            // materials first: gather them from harvest nodes where the research drop data names a node (real path), else the QA grant
            TradeskillSchematic2Entry next = TrainingSchematic(manager, goal.Skill);
            if (next != null && Gather(me, goal, next))
                return true;

            WorldEntity station = NearStation(me, goal.Skill);
            if (station == null)
            {
                if (!GoToStation(me, goal.Skill))
                {
                    goals.TryRemove(me.CharacterId, out _);
                    Diagnostics.FailureLedger.Record("tradeskill-no-station", $"skill {goal.Skill}", "no crafting station spawn in an open world");
                }
                return true;
            }

            for (int i = 0; i < CraftsPerThink; i++)
            {
                TradeskillSchematic2Entry schematic = TrainingSchematic(manager, goal.Skill);
                if (schematic == null)
                {
                    goals.TryRemove(me.CharacterId, out _);
                    Diagnostics.FailureLedger.Record("tradeskill-no-training-schematic", $"skill {goal.Skill} tier {manager.GetTier(goal.Skill)}");
                    return false;
                }

                // a second craft in the same think only with materials in the bags; the next think gathers again
                if (i > 0 && !HasMaterials(me, schematic))
                    break;
                EnsureMaterials(me, schematic);
                uint xp = manager.GetXp(goal.Skill);
                CraftAtStation(me, schematic, station.Guid);
                goal.Crafts++;
                if (manager.GetXp(goal.Skill) == xp)
                {
                    Diagnostics.FailureLedger.Record("tradeskill-bot-craft-no-xp", $"schematic {schematic.Id} skill {goal.Skill}", "craft refused or gave no xp");
                    break;
                }

                // the training output is not needed, keep the bag free
                if (schematic.Item2IdOutput != 0u && me.Inventory.GetItemCount(schematic.Item2IdOutput) > 0u)
                    me.Inventory.ItemDelete(schematic.Item2IdOutput, me.Inventory.GetItemCount(schematic.Item2IdOutput));
                if (schematic.Item2IdOutputFail != 0u && me.Inventory.GetItemCount(schematic.Item2IdOutputFail) > 0u)
                    me.Inventory.ItemDelete(schematic.Item2IdOutputFail, me.Inventory.GetItemCount(schematic.Item2IdOutputFail));
            }

            return true;
        }

        /// <summary>Node creature ids (spawned in an open world) whose researched drops give the material (after the current-id mapping).</summary>
        private static HashSet<uint> NodesFor(uint material)
        {
            var index = nodes;
            if (index == null)
            {
                LoadStations();
                return null;
            }

            if (materialNodes == null)
            {
                var result = new Dictionary<uint, HashSet<uint>>();
                foreach (uint creature in index.Values.SelectMany(l => l.Select(n => n.Creature)).Distinct())
                {
                    var research = Game.Research.CreatureResearch.Get(creature, 1);
                    if (research == null)
                        continue;
                    foreach (var (itemId, _) in research.Drops)
                    {
                        uint current = HarvestUnit.CurrentMaterial(itemId);
                        if (!result.TryGetValue(current, out HashSet<uint> set))
                            result.Add(current, set = new HashSet<uint>());
                        set.Add(creature);
                    }
                }
                materialNodes = result;
                Diagnostics.FailureLedger.Record("tradeskill-material-nodes-indexed", $"{result.Count} items from {result.Values.SelectMany(s => s).Distinct().Count()} node kinds");
            }
            return materialNodes.TryGetValue(material, out HashSet<uint> nodeSet) ? nodeSet : null;
        }

        /// <summary>
        /// Gathers a material the next craft of the goal still lacks: harvest a node of a kind that drops it when one is near, else travel to a
        /// spawn of such a node the bot may gather (its gathering skill learned or swapped in, the node's tier reached). True while busy.
        /// A material without a node source, out of the bot's gathering tier, or not complete after <see cref="GatherBudget"/> is left to the
        /// QA grant (EnsureMaterials, counted).
        /// </summary>
        private static bool Gather(Player me, Goal goal, TradeskillSchematic2Entry schematic)
        {
            uint[] materials = { schematic.Item2IdMaterial00, schematic.Item2IdMaterial01, schematic.Item2IdMaterial02, schematic.Item2IdMaterial03, schematic.Item2IdMaterial04 };
            uint[] costs     = { schematic.MaterialCost00, schematic.MaterialCost01, schematic.MaterialCost02, schematic.MaterialCost03, schematic.MaterialCost04 };
            for (int i = 0; i < materials.Length; i++)
            {
                uint material = materials[i];
                if (material == 0u || me.Inventory.GetItemCount(material) >= Math.Max(costs[i], 1u))
                    continue;

                // past the budget the QA grant fills in (and raises the count), so only counts inside the budget are gathered ones
                if (goal.GatherUntil.TryGetValue(material, out DateTime until) && DateTime.UtcNow > until)
                    continue;

                // the tool cast lands after the item use: count what arrived since the last think
                uint inBag = me.Inventory.GetItemCount(material);
                if (goal.InBag.TryGetValue(material, out uint before) && inBag > before)
                    Diagnostics.FailureLedger.Record("tradeskill-material-gathered", $"item {material} skill {schematic.TradeSkillId}", $"{inBag - before} from nodes");
                goal.InBag[material] = inBag;
                HashSet<uint> kinds = NodesFor(material);
                if (kinds == null || kinds.Count == 0)
                    continue;
                if (!goal.GatherUntil.ContainsKey(material))
                    goal.GatherUntil[material] = DateTime.UtcNow + GatherBudget;

                // out of the bot's gathering tier: work the gathering skill up on the nodes it may gather (as a player would), within a longer budget
                if (!kinds.Any(k => CanGatherKind(me, k)) && LevelingKinds(me, kinds) is { Count: > 0 } leveling)
                {
                    if (!goal.Leveling.Contains(material))
                    {
                        goal.Leveling.Add(material);
                        goal.GatherUntil[material] = DateTime.UtcNow + LevelingBudget;
                        Diagnostics.FailureLedger.Record("tradeskill-gather-leveling", $"item {material} skill {schematic.TradeSkillId}", $"{leveling.Count} node kinds of the gathering skill below the material's tier");
                    }
                    kinds = leveling;
                }

                // a node of the kind next to the bot
                if (me.Map != null)
                {
                    HashSet<uint> wanted = kinds;
                    me.Map.Search(me.Position, 40f, new SearchCheckRange(me.Position, 40f, me), out List<GridEntity> near);
                    HarvestUnit node = near.OfType<HarvestUnit>().Where(n => !n.IsDead && wanted.Contains(n.CreatureId))
                        .OrderBy(n => Vector3.DistanceSquared(n.Position, me.Position)).FirstOrDefault();
                    if (node != null)
                    {
                        if (Harvest(me, node))
                            return true;
                    }
                }

                // travel to a node of the kind the bot can gather (same world first)
                var spawns = nodes;
                ushort here = (ushort)(me.Map?.Entry.Id ?? 0u);
                var candidates = spawns.SelectMany(kv => kv.Value.Where(n => kinds.Contains(n.Creature)).Select(n => (World: kv.Key, n.Creature, n.Position)))
                    .Where(n => CanGatherKind(me, n.Creature)).ToList();
                if (candidates.Count == 0)
                {
                    goal.GatherUntil[material] = DateTime.MinValue;
                    Diagnostics.FailureLedger.Record("tradeskill-gather-out-of-reach", $"item {material} skill {schematic.TradeSkillId}", "no node of a kind the bot may gather (gathering tier)");
                    continue;
                }
                var target = candidates.Where(c => c.World == here).DefaultIfEmpty(candidates[BotManager.Rng.Next(candidates.Count)])
                    .OrderBy(c => c.World == here ? Vector3.DistanceSquared(c.Position, me.Position) : 0f).First();
                if (target.World == here && me.Map != null)
                    me.TeleportTo(me.Map.Entry, target.Position + new Vector3(1.5f, 0f, 1.5f), (me.Map as MapInstance)?.InstanceId);
                else
                    me.TeleportTo(target.World, target.Position.X + 1.5f, target.Position.Y, target.Position.Z + 1.5f);
                return true;
            }
            return false;
        }

        private static TradeskillTierEntry NodeTier(uint creature)
        {
            Creature2Entry entry = GameTableManager.Instance.Creature2.GetEntry(creature);
            TradeskillHarvestingInfoEntry info = GameTableManager.Instance.TradeskillHarvestingInfo.GetEntry(entry?.TradeskillHarvestingInfoId ?? 0u);
            return GameTableManager.Instance.TradeskillTier.GetEntry(info?.TradeSkillTierId ?? 0u);
        }

        /// <summary>Spawned node kinds of the same gathering skill as the material's nodes that the bot may gather now, highest tier first.</summary>
        private static HashSet<uint> LevelingKinds(Player me, HashSet<uint> kinds)
        {
            var skills = kinds.Select(NodeTier).Where(t => t != null).Select(t => t.TradeSkillId).ToHashSet();
            var all = nodes?.Values.SelectMany(l => l.Select(n => n.Creature)).Distinct().Where(c => NodeTier(c) is { } t && skills.Contains(t.TradeSkillId) && CanGatherKind(me, c)).ToList();
            if (all == null || all.Count == 0)
                return new HashSet<uint>();
            uint top = all.Max(c => NodeTier(c).Tier);
            return all.Where(c => NodeTier(c).Tier == top).ToHashSet();
        }

        /// <summary>The bot has, or can swap in, the node's gathering skill at the node's tier (HarvestingInfo prerequisite).</summary>
        private static bool CanGatherKind(Player me, uint creature)
        {
            Creature2Entry entry = GameTableManager.Instance.Creature2.GetEntry(creature);
            TradeskillHarvestingInfoEntry info = GameTableManager.Instance.TradeskillHarvestingInfo.GetEntry(entry?.TradeskillHarvestingInfoId ?? 0u);
            TradeskillTierEntry tier = GameTableManager.Instance.TradeskillTier.GetEntry(info?.TradeSkillTierId ?? 0u);
            if (tier == null)
                return false;
            TradeskillManager manager = me.TradeskillManager;
            uint have = manager.HasLearned(tier.TradeSkillId) ? manager.GetTier(tier.TradeSkillId) : 1u;
            return have >= tier.Tier;
        }

        /// <summary>Sends the craft messages the client sends for the schematic's profession.</summary>
        public static void CraftAtStation(Player me, TradeskillSchematic2Entry schematic, uint stationGuid, bool catalyst = false)
        {
            TradeskillEntry skill = GameTableManager.Instance.Tradeskill.GetEntry(schematic.TradeSkillId);
            if (skill != null && (skill.Flags & 8u) != 0u)
            {
                // every third coordinate craft (or when asked) the bot uses a global catalyst of the schematic's skill and tier
                uint catalystItem = catalyst || BotManager.Rng.Next(3) == 0 ? CatalystItem(me, schematic) : 0u;
                TradeskillHandler.HandleCraftStart(me.Session, BotPackets.Build<ClientCraftingCraftItem>(w =>
                {
                    w.Write(0u);
                    w.Write(stationGuid);
                    w.Write(schematic.Id);
                    w.Write(1u);
                    w.Write(catalystItem, 18u);
                }));
                TradeskillHandler.HandleCraftComplete(me.Session, BotPackets.Build<ClientCraftingSimpleCraft>(w =>
                {
                    w.Write(0u);
                    w.Write(stationGuid);
                    w.Write(schematic.Id);
                }));
                return;
            }

            TradeskillHandler.HandleCraftSimple(me.Session, BotPackets.Build<ClientCraftingCraftItemSimple>(w =>
            {
                w.Write(0u);
                w.Write(stationGuid);
                w.Write(schematic.Id);
                w.Write(1u);
            }));

            // circuit skills: 0x0852 started the craft, ClientCraftingComplexCraft finishes it with a power core (none for universal schematics)
            if (Game.Tradeskill.TradeskillManager.IsCircuit(schematic) && me.TradeskillManager.Current?.Circuit == true)
            {
                uint core = Game.Tradeskill.TradeskillManager.IsUniversal(schematic) ? 0u : PowerCoreItem(me, schematic);
                TradeskillHandler.HandleCraftCompleteComplex(me.Session, BotPackets.Build<ClientCraftingComplexCraft>(w =>
                {
                    w.Write(0u);
                    w.Write(stationGuid);
                    w.Write(schematic.Id);
                    w.Write(0ul);
                    w.Write(core, 18u);
                    w.Write(0u);
                    w.Write(0u, 3u);
                }));
                if (me.TradeskillManager.Current != null)
                {
                    Diagnostics.FailureLedger.Record("bot-circuit-craft-unfinished", $"schematic {schematic.Id}", $"core {core}");
                    TradeskillHandler.HandleAbandon(me.Session, BotPackets.Build<ClientCraftingAbandon>(w => w.Write(0u, 8u)));
                }
            }
        }

        private static volatile Dictionary<uint, uint[]> catalystItems;

        /// <summary>Item ids per TradeskillCatalyst row (Item2.TradeskillCatalystId).</summary>
        private static Dictionary<uint, uint[]> CatalystItems()
        {
            var map = catalystItems;
            if (map != null)
                return map;
            map = GameTableManager.Instance.Item.Entries.Where(e => e != null && e.TradeskillCatalystId != 0u)
                .GroupBy(e => e.TradeskillCatalystId).ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToArray());
            catalystItems = map;
            return map;
        }

        /// <summary>
        /// A catalyst item for a craft start: a global catalyst (effects 11-16) of the schematic's skill and tier from the bags, else a counted
        /// QA grant (tradeskill-qa-catalyst-grant: catalysts drop from nodes and creatures in the game, the research data has no source).
        /// 0 when the tables have none for that skill and tier.
        /// </summary>
        public static uint CatalystItem(Player me, TradeskillSchematic2Entry schematic, bool global = true)
        {
            var rows = GameTableManager.Instance.TradeskillCatalyst.Entries.Where(r => r != null && r.TradeSkillId == schematic.TradeSkillId && r.Tier == schematic.Tier
                && (global ? Game.Tradeskill.TradeskillCatalysts.IsGlobal(r) : Game.Tradeskill.TradeskillCatalysts.ChangesAdditive(r))).Select(r => r.Id).ToList();
            var items = rows.SelectMany(r => CatalystItems().TryGetValue(r, out uint[] ids) ? ids : Array.Empty<uint>()).ToList();
            if (items.Count == 0)
                return 0u;
            uint owned = items.FirstOrDefault(i => me.Inventory.GetItemCount(i) > 0u);
            if (owned != 0u)
                return owned;
            uint pick = items[BotManager.Rng.Next(items.Count)];
            if (!me.Inventory.HasFreeSlot())
                me.Inventory.DiscardJunk();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, pick, 1u);
            Diagnostics.FailureLedger.Record("tradeskill-qa-catalyst-grant", $"skill {schematic.TradeSkillId} tier {schematic.Tier}", $"item {pick}");
            Diagnostics.QuestTruth.Grant(me, "catalyst-grant", schematic.TradeSkillId);
            return me.Inventory.GetItemCount(pick) > 0u ? pick : 0u;
        }

        /// <summary>
        /// A power core for a circuit craft: the fitting core (TradeskillManager.FitsPowerCore) with the highest power level from the bags, else a
        /// counted QA grant of the core whose power level equals the output's (tradeskill-qa-powercore-grant: no vendor or drop source in the data
        /// names the current core items).
        /// </summary>
        public static uint PowerCoreItem(Player me, TradeskillSchematic2Entry schematic)
        {
            Item2Entry output = GameTableManager.Instance.Item.GetEntry(schematic.Item2IdOutput);
            Item owned = me.Inventory.GetItems(InventoryLocation.Inventory)
                .Where(i => i?.Info?.Entry != null && Game.Tradeskill.TradeskillManager.FitsPowerCore(i.Info.Entry, output))
                .OrderByDescending(i => i.Info.Entry.PowerLevel).FirstOrDefault();
            if (owned != null)
                return owned.Info.Entry.Id;
            Item2Entry core = GameTableManager.Instance.Item.Entries.Where(e => e != null && Game.Tradeskill.TradeskillManager.FitsPowerCore(e, output))
                .OrderByDescending(e => e.PowerLevel).ThenBy(e => e.Id).FirstOrDefault();
            if (core == null)
            {
                Diagnostics.FailureLedger.Record("tradeskill-no-powercore", $"schematic {schematic.Id}", $"output {output?.Id} level {output?.PowerLevel}");
                return 0u;
            }
            if (!me.Inventory.HasFreeSlot())
                me.Inventory.DiscardJunk();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, core.Id, 1u);
            Diagnostics.FailureLedger.Record("tradeskill-qa-powercore-grant", $"skill {schematic.TradeSkillId}", $"core {core.Id} level {core.PowerLevel}");
            Diagnostics.QuestTruth.Grant(me, "powercore-grant", schematic.TradeSkillId);
            return core.Id;
        }

        /// <summary>
        /// Picks talents of a skill through ClientTradeskillPickTalent (skill, tier index, bonus): every open tier (PointsToUnlock reached) without a
        /// pick gets its first bonus with an effect the crafting code applies (else its first bonus).
        /// </summary>
        public static bool PickTalents(Player me, uint skill)
        {
            var manager = me.TradeskillManager;
            if (!manager.HasLearned(skill) || manager.TalentPoints(skill) == 0u)
                return false;
            var applied = new HashSet<uint> { 5u, 6u, 10u, 11u, 14u, 15u, 16u, 17u, 20u, 26u, 27u, 65u };
            List<TradeskillTalentTierEntry> rows = TradeskillManager.TalentTiers(skill);
            bool any = false;
            for (int index = 0; index < rows.Count; index++)
            {
                TradeskillTalentTierEntry row = rows[index];
                uint[] bonuses = { row.TradeSkillBonusId00, row.TradeSkillBonusId01, row.TradeSkillBonusId02, row.TradeSkillBonusId03, row.TradeSkillBonusId04 };
                // tiers share bonus ids (Outfitter 332 sits in tiers 2, 5 and 8): the slot decides, not the id
                if (manager.PickedTalentAt(skill, index) != 0u || manager.TalentPoints(skill) < row.PointsToUnlock)
                    continue;
                uint bonus = bonuses.Where(b => b != 0u).OrderBy(b => GameTableManager.Instance.TradeskillBonus.GetEntry(b) is { } e
                    && (applied.Contains(e.TradeskillBonusEnum00) || applied.Contains(e.TradeskillBonusEnum01) || applied.Contains(e.TradeskillBonusEnum02)) ? 0 : 1).FirstOrDefault();
                if (bonus == 0u)
                    continue;
                uint tierIndex = (uint)index;
                TradeskillHandler.HandlePickTalent(me.Session, BotPackets.Build<ClientTradeskillPickTalent>(w =>
                {
                    w.Write(skill);
                    w.Write(tierIndex);
                    w.Write(bonus);
                }));
                bool ok = manager.PickedTalentAt(skill, index) == bonus;
                Diagnostics.FailureLedger.Record(ok ? "bot-tradeskill-talent-picked" : "bot-tradeskill-talent-refused", $"skill {skill}", $"tier {index} bonus {bonus}");
                if (!ok)
                    break;
                any = true;
            }
            return any;
        }

        private static readonly ConcurrentDictionary<uint, uint[]> variantAdditives = new();

        /// <summary>
        /// Additive items that move a coordinate craft from the grid origin into the variant's circle (TradeskillAdditive vectors, at most
        /// Tradeskill.MaxAdditives, additive tier at most the variant's), fewest first; null when no combination reaches it.
        /// </summary>
        public static uint[] AdditivesFor(TradeskillSchematic2Entry variant)
        {
            return variantAdditives.GetOrAdd(variant.Id, _ =>
            {
                TradeskillEntry skill = GameTableManager.Instance.Tradeskill.GetEntry(variant.TradeSkillId);
                int max = (int)Math.Max(skill?.MaxAdditives ?? 3u, 1u);
                var candidates = new List<(uint Item, float X, float Y)>();
                foreach (TradeskillAdditiveEntry additive in GameTableManager.Instance.TradeskillAdditive.Entries.Where(a => a != null && a.TradeSkillId == variant.TradeSkillId && a.Tier <= ((skill?.Flags & 1u) is > 0u ? 1u : Math.Max(variant.Tier, 1u))))
                {
                    Item2Entry item = GameTableManager.Instance.Item.Entries.FirstOrDefault(i => i != null && i.TradeskillAdditiveId == additive.Id);
                    if (item != null)
                        candidates.Add((item.Id, additive.VectorX, additive.VectorY));
                }

                bool Inside(float x, float y) => MathF.Sqrt((x - variant.VectorX) * (x - variant.VectorX) + (y - variant.VectorY) * (y - variant.VectorY)) <= variant.Radius;
                if (Inside(0f, 0f))
                    return Array.Empty<uint>();
                for (int a = 0; a < candidates.Count; a++)
                {
                    if (Inside(candidates[a].X, candidates[a].Y))
                        return new[] { candidates[a].Item };
                    if (max < 2)
                        continue;
                    for (int b = a; b < candidates.Count; b++)
                    {
                        float x2 = candidates[a].X + candidates[b].X, y2 = candidates[a].Y + candidates[b].Y;
                        if (Inside(x2, y2))
                            return new[] { candidates[a].Item, candidates[b].Item };
                        if (max < 3)
                            continue;
                        for (int c = b; c < candidates.Count; c++)
                            if (Inside(x2 + candidates[c].X, y2 + candidates[c].Y))
                                return new[] { candidates[a].Item, candidates[b].Item, candidates[c].Item };
                    }
                }
                return null;
            });
        }

        /// <summary>
        /// Coordinate craft of a variant (a child schematic with a grid circle): start the parent, add the additives that move into the circle,
        /// complete (ClientCraftingCraftItem, ClientCraftingAdditive, ClientCraftingSimpleCraft). False when no additive combination reaches it.
        /// </summary>
        public static bool CraftVariant(Player me, TradeskillSchematic2Entry variant, uint stationGuid)
        {
            TradeskillSchematic2Entry parent = GameTableManager.Instance.TradeskillSchematic2.GetEntry(variant.TradeskillSchematic2IdParent);
            uint[] additives = parent == null ? null : AdditivesFor(variant);
            if (additives == null)
            {
                Diagnostics.FailureLedger.Record("tradeskill-variant-unreachable", $"schematic {variant.Id} skill {variant.TradeSkillId}", "no combination of at most MaxAdditives table additives lands in its circle");
                return false;
            }

            // additives are bought in the crafting window's additive store, not taken from the bags (client crafting window)
            EnsureMaterials(me, parent);
            // a catalyst that attracts the point to the nearest craft target makes the variant reliable despite the additive spread
            uint catalystItem = AttractCatalyst(me, parent);

            TradeskillHandler.HandleCraftStart(me.Session, BotPackets.Build<ClientCraftingCraftItem>(w =>
            {
                w.Write(0u);
                w.Write(stationGuid);
                w.Write(parent.Id);
                w.Write(1u);
                w.Write(catalystItem, 18u);
            }));
            // additives land with a random spread: like a player watching the marker, the bot adds the planned first additive, then the
            // one whose vector brings the current point closest to the variant's centre, until the point is inside or the slots are used
            var manager = me.TradeskillManager;
            int slots = Math.Max(manager.MaxAdditives(parent), additives.Length);
            var options = GameTableManager.Instance.TradeskillAdditive.Entries.Where(a => a != null && a.TradeSkillId == parent.TradeSkillId && a.Tier <= manager.MaxAdditiveTier(parent))
                .Select(a => (Item: AdditiveItem(a.Id), a.VectorX, a.VectorY)).Where(a => a.Item != 0u).ToList();
            for (int k = 0; k < slots && manager.Current != null; k++)
            {
                float dx = manager.Current.X - variant.VectorX, dy = manager.Current.Y - variant.VectorY;
                if (k > 0 && MathF.Sqrt(dx * dx + dy * dy) <= variant.Radius * 0.8f)
                    break;
                uint additive = k < additives.Length && k == 0 ? additives[0]
                    : options.OrderBy(o => (manager.Current.X + o.VectorX - variant.VectorX) * (manager.Current.X + o.VectorX - variant.VectorX)
                        + (manager.Current.Y + o.VectorY - variant.VectorY) * (manager.Current.Y + o.VectorY - variant.VectorY)).Select(o => o.Item).FirstOrDefault();
                if (additive == 0u)
                    break;
                // a closer point is not reachable from here: stop adding
                if (k > 0 && options.FirstOrDefault(o => o.Item == additive) is var best
                    && (manager.Current.X + best.VectorX - variant.VectorX) * (manager.Current.X + best.VectorX - variant.VectorX)
                     + (manager.Current.Y + best.VectorY - variant.VectorY) * (manager.Current.Y + best.VectorY - variant.VectorY) >= dx * dx + dy * dy)
                    break;
                TradeskillHandler.HandleAdditive(me.Session, BotPackets.Build<ClientCraftingAdditive>(w =>
                {
                    w.Write(stationGuid);
                    w.Write(additive, 18u);
                    w.Write(0u, 18u);
                }));
            }
            TradeskillHandler.HandleCraftComplete(me.Session, BotPackets.Build<ClientCraftingSimpleCraft>(w =>
            {
                w.Write(0u);
                w.Write(stationGuid);
                w.Write(parent.Id);
            }));
            return true;
        }

        private static volatile Dictionary<uint, uint> additiveItems;

        /// <summary>The item of a TradeskillAdditive row (Item2.TradeskillAdditiveId), 0 when none.</summary>
        private static uint AdditiveItem(uint additiveId)
        {
            var map = additiveItems;
            if (map == null)
                additiveItems = map = GameTableManager.Instance.Item.Entries.Where(e => e != null && e.TradeskillAdditiveId != 0u)
                    .GroupBy(e => e.TradeskillAdditiveId).ToDictionary(g => g.Key, g => g.Min(e => e.Id));
            return map.TryGetValue(additiveId, out uint id) ? id : 0u;
        }

        /// <summary>An "attract to the nearest crafting target" catalyst (effect 15) of the schematic's skill and tier from the bags, else 0.</summary>
        private static uint AttractCatalyst(Player me, TradeskillSchematic2Entry schematic)
        {
            var rows = GameTableManager.Instance.TradeskillCatalyst.Entries.Where(r => r != null && r.TradeSkillId == schematic.TradeSkillId && r.Tier == schematic.Tier
                && Game.Tradeskill.TradeskillCatalysts.Effects(r).Any(e => e.Kind == Game.Tradeskill.TradeskillCatalysts.AttractCraftTarget)).Select(r => r.Id);
            return rows.SelectMany(r => CatalystItems().TryGetValue(r, out uint[] ids) ? ids : Array.Empty<uint>()).FirstOrDefault(i => me.Inventory.GetItemCount(i) > 0u);
        }

        private static readonly ConcurrentDictionary<(uint Skill, uint Tier), TradeskillSchematic2Entry[]> training = new();

        /// <summary>
        /// Schematics of the skill's current tier that a player knows without discovering them (no coordinate variants), with materials, cheapest first.
        /// </summary>
        private static TradeskillSchematic2Entry TrainingSchematic(TradeskillManager manager, uint skill)
        {
            uint tier = manager.GetTier(skill);
            TradeskillSchematic2Entry[] list = training.GetOrAdd((skill, tier), key => GameTableManager.Instance.TradeskillSchematic2.Entries
                .Where(e => e != null && e.TradeSkillId == key.Skill && e.Tier == key.Tier && e.DiscoverableQuadrant == 0u && e.TradeskillSchematic2IdParent == 0u
                    && e.Item2IdOutput != 0u && e.Item2IdMaterial00 != 0u)
                .OrderBy(e => e.MaterialCost00 + e.MaterialCost01 + e.MaterialCost02 + e.MaterialCost03 + e.MaterialCost04)
                .ThenBy(e => e.Id)
                .ToArray());
            // schematics whose materials come from harvest nodes first, so the bot gathers instead of taking QA grants; then the cheapest
            TradeskillSchematic2Entry known = list.Where(manager.KnowsSchematic)
                .OrderByDescending(e => new[] { e.Item2IdMaterial00, e.Item2IdMaterial01, e.Item2IdMaterial02, e.Item2IdMaterial03, e.Item2IdMaterial04 }
                    .Count(m => m != 0u && NodesFor(m) is { Count: > 0 }))
                .FirstOrDefault();
            if (known != null || list.Length == 0)
                return known;

            // no schematic of the tier is known (Architect tiers 2-5 have no flag 1 schematic: achievements and items teach them): QA grant, counted
            manager.LearnSchematic(list[0].Id);
            Diagnostics.FailureLedger.Record("tradeskill-qa-schematic-grant", $"schematic {list[0].Id} skill {skill}", $"training at tier {tier}");
            Diagnostics.QuestTruth.Grant(manager.Owner, "schematic-grant", skill);
            return list[0];
        }

        private static bool HasMaterials(Player me, TradeskillSchematic2Entry schematic)
        {
            uint[] materials = { schematic.Item2IdMaterial00, schematic.Item2IdMaterial01, schematic.Item2IdMaterial02, schematic.Item2IdMaterial03, schematic.Item2IdMaterial04 };
            uint[] costs     = { schematic.MaterialCost00, schematic.MaterialCost01, schematic.MaterialCost02, schematic.MaterialCost03, schematic.MaterialCost04 };
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] != 0u && me.Inventory.GetItemCount(materials[i]) < Math.Max(costs[i], 1u))
                    return false;
            return true;
        }

        public static void EnsureMaterials(Player me, TradeskillSchematic2Entry schematic, uint times = 1u)
        {
            uint[] materials = { schematic.Item2IdMaterial00, schematic.Item2IdMaterial01, schematic.Item2IdMaterial02, schematic.Item2IdMaterial03, schematic.Item2IdMaterial04 };
            uint[] costs     = { schematic.MaterialCost00, schematic.MaterialCost01, schematic.MaterialCost02, schematic.MaterialCost03, schematic.MaterialCost04 };
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == 0u)
                    continue;

                uint need = Math.Max(costs[i], 1u) * times;
                uint have = me.Inventory.GetItemCount(materials[i]);
                if (have >= need)
                    continue;

                if (!me.Inventory.HasFreeSlot())
                    me.Inventory.DiscardJunk();
                me.Inventory.ItemCreate(InventoryLocation.Inventory, materials[i], need - have);
                me.TradeskillManager.MarkQaMaterials(schematic.TradeSkillId);
                Diagnostics.FailureLedger.Record("tradeskill-qa-material-grant", $"skill {schematic.TradeSkillId}", $"item {materials[i]}");
                Diagnostics.QuestTruth.Grant(me, "material-grant", schematic.TradeSkillId);
            }
        }

        /// <summary>A crafting station for the skill near the bot (Creature2.TradeSkillIdStation = skill or every skill).</summary>
        public static WorldEntity NearStation(Player me, uint skill)
        {
            if (me.Map == null)
                return null;

            me.Map.Search(me.Position, StationRange, new SearchCheckRange(me.Position, StationRange, me), out List<GridEntity> near);
            return near.OfType<WorldEntity>()
                .Where(e => e is not Player && GameTableManager.Instance.Creature2.GetEntry(e.CreatureId) is { } c && (c.TradeSkillIdStation == skill || c.TradeSkillIdStation == uint.MaxValue))
                .OrderBy(e => Vector3.DistanceSquared(e.Position, me.Position))
                .FirstOrDefault();
        }

        /// <summary>Teleports the bot next to a crafting station spawn for the skill, on its own map when there is one. False when none exists.</summary>
        public static bool GoToStation(Player me, uint skill)
        {
            Dictionary<ushort, List<(uint Creature, uint Skill, Vector3 Position)>> index = stations;
            if (index == null)
            {
                LoadStations();
                return true;
            }

            ushort here = (ushort)(me.Map?.Entry.Id ?? 0u);
            var options = index.Where(kv => kv.Value.Any(s => s.Skill == skill || s.Skill == uint.MaxValue))
                .OrderBy(kv => kv.Key == here ? 0 : 1).ToList();
            if (options.Count == 0)
                return false;

            (ushort world, var list) = (options[0].Key, options[0].Value);
            var target = list.Where(s => s.Skill == skill || s.Skill == uint.MaxValue).OrderBy(s => Vector3.DistanceSquared(s.Position, me.Position)).First();
            Diagnostics.FailureLedger.Record("tradeskill-bot-to-station", $"skill {skill}", $"world {world} creature {target.Creature}");
            if (world == here && me.Map != null)
                me.TeleportTo(me.Map.Entry, target.Position + new Vector3(1.5f, 0f, 1.5f), (me.Map as MapInstance)?.InstanceId);
            else
                me.TeleportTo(world, target.Position.X + 1.5f, target.Position.Y, target.Position.Z + 1.5f);
            return true;
        }

        private static void LoadStations()
        {
            if (Interlocked.Exchange(ref stationsLoading, 1) == 1)
                return;

            Task.Run(() =>
            {
                try
                {
                    var index = new Dictionary<ushort, List<(uint, uint, Vector3)>>();
                    var nodeIndex = new Dictionary<ushort, List<(uint, Vector3)>>();
                    foreach (var spawn in DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns())
                    {
                        Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(spawn.Creature);
                        if (creature == null || (creature.TradeSkillIdStation == 0u && creature.TradeskillHarvestingInfoId == 0u) || BotQuests.IsInstancedWorld(spawn.World))
                            continue;
                        WorldEntry world = GameTableManager.Instance.World.GetEntry(spawn.World);
                        if (world == null || !BaseMapManager.Instance.HasBaseMap(world.AssetPath)
                            || (Game.Map.Static.MapType)world.Type is Game.Map.Static.MapType.Residence or Game.Map.Static.MapType.Community)
                            continue;

                        if (creature.TradeskillHarvestingInfoId != 0u)
                        {
                            if (!nodeIndex.TryGetValue(spawn.World, out var nodeList))
                                nodeIndex.Add(spawn.World, nodeList = new List<(uint, Vector3)>());
                            nodeList.Add((spawn.Creature, new Vector3(spawn.X, spawn.Y, spawn.Z)));
                            continue;
                        }

                        if (!index.TryGetValue(spawn.World, out var list))
                            index.Add(spawn.World, list = new List<(uint, uint, Vector3)>());
                        list.Add((spawn.Creature, creature.TradeSkillIdStation, new Vector3(spawn.X, spawn.Y, spawn.Z)));
                    }
                    nodes = nodeIndex;
                    stations = index;
                    Diagnostics.FailureLedger.Record("tradeskill-stations-indexed", $"{index.Values.Sum(l => l.Count)} station spawns in {index.Count} worlds, {nodeIndex.Values.Sum(l => l.Count)} harvest node spawns in {nodeIndex.Count} worlds");
                }
                catch (Exception ex)
                {
                    Diagnostics.FailureLedger.Record("tradeskill-stations-index-failed", ex.GetType().Name, ex.Message);
                    nodes = new Dictionary<ushort, List<(uint, Vector3)>>();
                    stations = new Dictionary<ushort, List<(uint, uint, Vector3)>>();
                }
            });
        }

        /// <summary>Teleports the bot to a random harvest node spawn, on its own map when it has some. False when the index is not ready or empty.</summary>
        public static bool GoToNode(Player me, Func<uint, bool> kind = null)
        {
            var index = nodes;
            if (index == null)
            {
                LoadStations();
                return false;
            }

            if (kind != null)
                index = index.ToDictionary(kv => kv.Key, kv => kv.Value.Where(n => kind(n.Creature)).ToList());
            ushort here = (ushort)(me.Map?.Entry.Id ?? 0u);
            List<(uint Creature, Vector3 Position)> list = index.TryGetValue(here, out var own) && own.Count > 0 ? own : index.Values.Where(l => l.Count > 0).OrderByDescending(l => l.Count).FirstOrDefault();
            if (list == null || list.Count == 0)
                return false;

            ushort world = index.TryGetValue(here, out var mine) && mine == list ? here : index.First(kv => kv.Value == list).Key;
            var target = list[BotManager.Rng.Next(list.Count)];
            if (world == here && me.Map != null)
                me.TeleportTo(me.Map.Entry, target.Position + new Vector3(1.5f, 0f, 1.5f), (me.Map as MapInstance)?.InstanceId);
            else
                me.TeleportTo(world, target.Position.X + 1.5f, target.Position.Y, target.Position.Z + 1.5f);
            return true;
        }

        /// <summary>
        /// Gather a harvest node: learn the node's gathering skill if needed, hold a tool of the skill, use the tool on the node (ClientItemUse).
        /// Returns false when the bot cannot (tier too low); the reason is in the ledger.
        /// </summary>
        public static bool Harvest(Player me, HarvestUnit node)
        {
            TradeskillTierEntry tier = node.RequiredTier;
            if (tier == null)
            {
                Diagnostics.FailureLedger.Record("harvest-node-no-tier", $"creature {node.CreatureId}");
                return false;
            }

            bool byHand = HarvestUnit.IsHandGathered(tier.TradeSkillId);
            if (!(byHand ? me.TradeskillManager.HasLearned(tier.TradeSkillId) : Learn(me, tier.TradeSkillId)))
            {
                Diagnostics.FailureLedger.Record("harvest-bot-cannot-learn", $"skill {tier.TradeSkillId}");
                return false;
            }
            if (!node.CanBeHarvestedBy(me))
            {
                Diagnostics.FailureLedger.Record("harvest-bot-tier-too-low", $"node {node.CreatureId} skill {tier.TradeSkillId} tier {tier.Tier}", $"bot tier {me.TradeskillManager.GetTier(tier.TradeSkillId)}");
                return false;
            }

            if (byHand)
            {
                // Farmer: the client's interact casts the class auto attack at the node (no tool)
                ClassEntry entry = GameTableManager.Instance.Class.GetEntry((uint)me.Class);
                uint attack = entry?.Spell4IdAttackPrimary.FirstOrDefault(id => id != 0u) ?? 0u;
                if (attack == 0u)
                    attack = entry?.Spell4IdAttackUnarmed.FirstOrDefault(id => id != 0u) ?? 0u;
                if (attack == 0u)
                {
                    Diagnostics.FailureLedger.Record("harvest-bot-no-auto-attack", $"class {(uint)me.Class}");
                    return false;
                }
                me.MovementManager.StopSpline();
                me.CastSpell(attack, new Game.Spell.SpellParameters { UserInitiatedSpellCast = true, PrimaryTargetId = node.Guid });
                Diagnostics.FailureLedger.Record("bot-farm-gather-cast", $"node {node.CreatureId}", $"spell {attack}");
                return true;
            }

            Item tool = Tool(me, tier.TradeSkillId);
            if (tool == null)
                return false;

            ItemHandler.HandleItemUse(me.Session, BotPackets.Build<ClientItemUse>(w =>
            {
                w.Write(0u);
                w.Write((uint)tool.Location, 9u);
                w.Write(tool.BagIndex);
                w.Write(node.Guid);
                w.Write((uint)tool.Location, 9u);
                w.Write(tool.BagIndex);
                w.Write(node.Position.X);
                w.Write(node.Position.Y);
                w.Write(node.Position.Z);
            }));
            return true;
        }

        private static Item FindTool(Player me, uint skill)
        {
            var toolItems = HarvestUnit.ToolSkills;
            return me.Inventory.GetItems(InventoryLocation.Inventory)
                .Concat(me.Inventory.GetItems(InventoryLocation.Equipped))
                .FirstOrDefault(i => i?.Info?.Entry != null && i.Info.Entry.Item2FamilyId == 28u
                    && GameTableManager.Instance.ItemSpecial.GetEntry(i.Info.Entry.ItemSpecialId00) is { } special
                    && toolItems.TryGetValue(special.Spell4IdOnActivate, out uint s) && s == skill);
        }

        private static Item Tool(Player me, uint skill)
        {
            Item tool = FindTool(me, skill);
            if (tool != null)
                return tool;

            // the lowest tool of the skill (the novice one): no vendor stock or drop source for tools in the data
            Item2Entry novice = GameTableManager.Instance.Item.Entries
                .Where(e => e != null && e.Item2FamilyId == 28u && GameTableManager.Instance.Item2Category.GetEntry(e.Item2CategoryId)?.TradeSkillId == skill
                    && GameTableManager.Instance.ItemSpecial.GetEntry(e.ItemSpecialId00)?.Spell4IdOnActivate is > 0u)
                .OrderBy(e => e.PowerLevel).ThenBy(e => e.Id).FirstOrDefault();
            if (novice == null)
            {
                Diagnostics.FailureLedger.Record("harvest-no-tool-item", $"skill {skill}");
                return null;
            }

            if (!me.Inventory.HasFreeSlot())
                me.Inventory.DiscardJunk();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, novice.Id, 1u);
            Diagnostics.FailureLedger.Record("tradeskill-qa-tool-grant", $"skill {skill}", $"item {novice.Id}");
            Diagnostics.QuestTruth.Grant(me, "tool-grant", skill);
            tool = FindTool(me, skill);
            if (tool == null)
                Diagnostics.FailureLedger.Record("harvest-tool-not-in-bag", $"skill {skill}", $"item {novice.Id} granted but not found (bag full?)");
            return tool;
        }
    }
}
