using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Guild.Static;
using NexusForever.WorldServer.Game.Mail.Static;
using NexusForever.WorldServer.Game.Reputation.Static;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Exercises non-combat systems end to end for bots with the Systems role (NF_BOTS_SYSTEMS=1, NF_BOTS_SYSTEM_BOTS=n).
    /// Every probe checks the state the system should have afterwards. Pass counts go to the ledger as system-ok, failures as system-fail,
    /// so one soak shows which systems work and how often they were exercised.
    /// </summary>
    public static partial class BotSystems
    {
        private static int cursor;
        private static volatile List<(ushort World, System.Numerics.Vector3 Position)> vendorSpawns;
        private static int vendorSpawnsLoading;

        /// <summary>Vendor spawn points in worlds a bot may visit (the quest index only holds quest target creatures).</summary>
        private static void LoadVendorSpawns()
        {
            if (System.Threading.Interlocked.Exchange(ref vendorSpawnsLoading, 1) == 1)
                return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var ids = new HashSet<uint>(NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetVendorCreatureIds());
                    foreach (var creature in GameTableManager.Instance.Creature2.Entries)
                        if (Game.Entity.VendorFallback.IsVendor(creature))
                            ids.Add(creature.Id);

                    var list = new List<(ushort, System.Numerics.Vector3)>();
                    foreach (var spawn in NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns())
                    {
                        if (!ids.Contains(spawn.Creature) || !BotQuests.CanVisitWorld(spawn.World) || BotQuests.IsInstancedWorld(spawn.World))
                            continue;

                        var world = GameTableManager.Instance.World.GetEntry(spawn.World);
                        if (world == null || !Game.Map.BaseMapManager.Instance.HasBaseMap(world.AssetPath))
                            continue;

                        list.Add((spawn.World, new System.Numerics.Vector3(spawn.X, spawn.Y, spawn.Z)));
                    }

                    vendorSpawns = list;
                    FailureLedger.Record("system-info", "vendor spawns indexed", $"{list.Count} spawns of {ids.Count} vendor creatures");
                }
                catch (Exception ex)
                {
                    FailureLedger.Record("system-fail", "vendor-index", ex.Message);
                    vendorSpawns = new List<(ushort, System.Numerics.Vector3)>();
                }
            });
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, string> pendingProbes = new();

        /// <summary>The bot was moved for a probe; its next probe turn runs that probe first.</summary>
        private static void RunNext(Player me, string name)
        {
            pendingProbes.Remove(me);
            pendingProbes.Add(me, name);
        }

        private static readonly object guildLock = new();

        private sealed class Probe
        {
            public string Name;
            public Action<Player> Run;
        }

        private static readonly Probe[] probes =
        {
            new Probe { Name = "currency",   Run = CurrencyProbe },
            new Probe { Name = "inventory",  Run = InventoryProbe },
            new Probe { Name = "mail",       Run = MailProbe },
            new Probe { Name = "title",      Run = TitleProbe },
            new Probe { Name = "path",       Run = PathProbe },
            new Probe { Name = "reputation", Run = ReputationProbe },
            new Probe { Name = "housing",    Run = HousingProbe },
            new Probe { Name = "guild",      Run = GuildProbe },
            new Probe { Name = "guildpair",  Run = GuildPairProbe },
            new Probe { Name = "mountride",  Run = MountRideProbe },
            new Probe { Name = "rez",        Run = RezProbe },
            new Probe { Name = "rezoptions", Run = RezOptionsProbe },
            new Probe { Name = "groupfinder", Run = GroupFinderProbe },
            new Probe { Name = "group",      Run = GroupProbe },
            new Probe { Name = "lootrules",  Run = LootRulesProbe },
            new Probe { Name = "chatchannel", Run = ChatChannelProbe },
            new Probe { Name = "mailp2p",    Run = MailP2PProbe },
            new Probe { Name = "pathmission", Run = PathMissionProbe },
            new Probe { Name = "questdaily", Run = QuestDailyProbe },
            new Probe { Name = "periodic",   Run = PeriodicProbe },
            new Probe { Name = "dailylogin", Run = DailyLoginProbe },
            new Probe { Name = "conversion", Run = ConversionProbe },
            new Probe { Name = "tradeskill", Run = TradeskillProbe },
            new Probe { Name = "tradeskill-handlers", Run = TradeskillHandlerProbe },
            new Probe { Name = "harvest",    Run = HarvestProbe },
            new Probe { Name = "harvest-check", Run = HarvestCheckProbe },
            new Probe { Name = "farm",       Run = FarmProbe },
            new Probe { Name = "farm-check", Run = FarmCheckProbe },
            new Probe { Name = "salvage",    Run = SalvageProbe },
            new Probe { Name = "craft-catalyst", Run = CatalystCraftProbe },
            new Probe { Name = "tradeskill-talent", Run = TalentProbe },
            new Probe { Name = "vendor",     Run = VendorProbe },
            new Probe { Name = "challenge",  Run = ChallengeProbe },
            new Probe { Name = "reset",      Run = ResetProbe },
            new Probe { Name = "taxi",       Run = TaxiProbe },
            new Probe { Name = "auction",    Run = AuctionProbe },
            new Probe { Name = "commodity",  Run = CommodityProbe },
            new Probe { Name = "trade",      Run = TradeProbe },
            new Probe { Name = "friends",    Run = FriendsProbe },
            new Probe { Name = "whisper",    Run = WhisperProbe },
            new Probe { Name = "bank",       Run = BankProbe },
            new Probe { Name = "costume",    Run = CostumeProbe },
            new Probe { Name = "achievement", Run = AchievementProbe },
            new Probe { Name = "amp",        Run = AmpProbe },
            new Probe { Name = "rune",       Run = RuneProbe },
            new Probe { Name = "challengechoice", Run = ChallengeChoiceProbe },
            new Probe { Name = "groupjoin",  Run = GroupJoinProbe },
            new Probe { Name = "matchleave", Run = MatchLeaveProbe },
            new Probe { Name = "mount",      Run = MountProbe },
            new Probe { Name = "worldboss",  Run = WorldBossProbe },
            new Probe { Name = "imbuement",  Run = ImbuementProbe },
            new Probe { Name = "p2ptrade",   Run = P2PTradeProbe },
            new Probe { Name = "auctionmsg", Run = AuctionMessageProbe },
            new Probe { Name = "commoditymsg", Run = CommodityMessageProbe },
            new Probe { Name = "instancesettings", Run = InstanceSettingsProbe },
            new Probe { Name = "conversionmsg", Run = ConversionMessageProbe },
            new Probe { Name = "dailyloginmsg", Run = DailyLoginMessageProbe },
            new Probe { Name = "lootwindow", Run = LootWindowProbe },
            new Probe { Name = "contract",   Run = ContractProbe },
            new Probe { Name = "mailtakeall", Run = MailTakeAllProbe },
            new Probe { Name = "taxipath",   Run = TaxiPathProbe },
            new Probe { Name = "matchqueue", Run = MatchQueueProbe },
            new Probe { Name = "rolecheck",  Run = RoleCheckProbe },
            new Probe { Name = "housingvisit", Run = HousingVisitProbe },
            new Probe { Name = "dye",        Run = DyeProbe },
            new Probe { Name = "groupflags", Run = GroupFlagsProbe },
            new Probe { Name = "friendsid",  Run = FriendsIdentityProbe },
            new Probe { Name = "portal",     Run = PortalProbe },
            new Probe { Name = "neighbours", Run = NeighbourProbe },
            new Probe { Name = "vehicle",    Run = VehicleProbe },
            new Probe { Name = "community",  Run = CommunityProbe }
        };

        /// <summary>Runs the next probe for this bot.</summary>
        public static void Think(Player me)
        {
            Probe probe = probes[(int)((uint)System.Threading.Interlocked.Increment(ref cursor) % probes.Length)];
            if (pendingProbes.TryGetValue(me, out string pending))
            {
                pendingProbes.Remove(me);
                probe = probes.First(p => p.Name == pending);
            }
            try
            {
                probe.Run(me);
                FailureLedger.Record("system-ok", probe.Name);
            }
            catch (ProbeSkip)
            {
            }
            catch (ProbeFailure ex)
            {
                FailureLedger.Record("system-fail", probe.Name, ex.Message);
            }
            catch (Exception ex)
            {
                FailureLedger.Record("system-fail", probe.Name, Describe(ex));
            }
        }

        private static string Describe(Exception ex)
        {
            string[] frames = (ex.StackTrace ?? "").Split('\n');
            string where = string.Join(" <- ", frames.Take(3).Select(l => l.Trim().Split(" in ")[0]));
            return ex.GetType().Name + ": " + ex.Message + " at " + where;
        }

        private sealed class ProbeSkip : Exception
        {
        }

        private sealed class ProbeFailure : Exception
        {
            public ProbeFailure(string message) : base(message) { }
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                throw new ProbeFailure(message);
        }

        private static void CurrencyProbe(Player me)
        {
            ulong before = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            me.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 1000ul);
            Expect((me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) == before + 1000ul, "credits not added");
            Expect(me.CurrencyManager.CanAfford(CurrencyType.Credits, 1000ul), "CanAfford false after add");
            me.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, 1000ul);
            Expect((me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) == before, "credits not subtracted");
        }

        private static uint TestItemId()
        {
            return GameTableManager.Instance.Item.Entries.First(e => e.Id != 0u && e.MaxStackCount > 1u).Id;
        }

        /// <summary>Bots have no vendor trips, so full bags are cleared the same way the quest code does it.</summary>
        private static void EnsureRoom(Player me, uint slots)
        {
            for (int i = 0; i < 8 && me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) < slots; i++)
                if (!me.Inventory.DiscardJunk())
                    break;

            if (me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) < slots)
                throw new ProbeSkip();
        }

        private static void InventoryProbe(Player me)
        {
            EnsureRoom(me, 2u);
            uint itemId = TestItemId();
            uint before = me.Inventory.GetItemCount(itemId);
            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 3u);
            Expect(me.Inventory.GetItemCount(itemId) == before + 3u, $"item {itemId} count after create {me.Inventory.GetItemCount(itemId)} not {before + 3u}, slots free {me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory)}");
            me.Inventory.ItemDelete(itemId, 3u);

            Expect(me.Inventory.GetItemCount(itemId) < before + 3u, "item count after delete");
        }

        private sealed class MailState
        {
            public int Before;
            public DateTime Sent;
            public uint Spell;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, MailState> mailStates = new();

        // instant mail is flushed when the character saves (about every 60 s), so the probe sends in one turn and checks in a later one
        private static void MailProbe(Player me)
        {
            if (mailStates.TryGetValue(me, out MailState state))
            {
                if (me.MailManager.MailCount > state.Before)
                {
                    mailStates.Remove(me);
                    ulong id = me.MailManager.MailIds.Last();
                    me.MailManager.MailMarkAsRead(id);
                    me.MailManager.MailDelete(id);
                    return;
                }

                if (DateTime.UtcNow - state.Sent < TimeSpan.FromSeconds(180))
                    throw new ProbeSkip();

                mailStates.Remove(me);
                throw new ProbeFailure("mail not delivered 180 s after sending");
            }

            uint creature = GameTableManager.Instance.Creature2.Entries.First(c => c.Id != 0u).Id;
            uint text = GameTableManager.Instance.LocalizedText.Entries.First(t => t.Id != 0u).Id;
            mailStates.Add(me, new MailState { Before = me.MailManager.MailCount, Sent = DateTime.UtcNow });
            me.MailManager.SendMail(creature, DeliveryTime.Instant, text, text, new uint[0]);
            throw new ProbeSkip();
        }

        private static void TitleProbe(Player me)
        {
            ushort titleId = (ushort)GameTableManager.Instance.CharacterTitle.Entries.First(t => t.Id != 0u).Id;
            me.TitleManager.AddTitle(titleId, true);
            me.TitleManager.ActiveTitleId = titleId;
            Expect(me.TitleManager.ActiveTitleId == titleId, "active title not set");
            me.TitleManager.RevokeTitle(titleId, true);
            Expect(me.TitleManager.ActiveTitleId != titleId, "active title survived revoke");
        }

        private static void PathProbe(Player me)
        {
            foreach (Path path in Enum.GetValues(typeof(Path)))
            {
                if (!me.PathManager.IsPathUnlocked(path))
                    me.PathManager.UnlockPath(path);
                Expect(me.PathManager.IsPathUnlocked(path), $"path {path} not unlocked");
            }

            me.PathManager.AddXp(10u);
        }

        private static void ReputationProbe(Player me)
        {
            Faction faction = Faction.Exile;
            float before = me.ReputationManager.GetReputation(faction)?.Amount ?? 0f;
            me.ReputationManager.UpdateReputation(faction, 10f);
            float after = me.ReputationManager.GetReputation(faction)?.Amount ?? 0f;
            Expect(after > before, "reputation did not rise");
        }

        private static void HousingProbe(Player me)
        {
            var entry = GameTableManager.Instance.HousingDecorInfo.Entries.Where(r => r != null).First(e => e.Id != 0u);
            if (me.ResidenceManager.Residence != null && me.ResidenceManager.Residence.GetDecor().Count() >= 200)
                throw new ProbeSkip();

            me.ResidenceManager.DecorCreate(entry, 1u);
            var residence = me.ResidenceManager.Residence;
            Expect(residence != null, "no residence after creating decor");

            // place the new decor, put it back in the crate, change privacy and back
            var decor = residence.GetDecor().OrderByDescending(d => d.DecorId).First();
            decor.Move(Game.Housing.Static.DecorType.Unknown2, new System.Numerics.Vector3(10f, 0f, 10f), System.Numerics.Quaternion.Identity, 1f);
            Expect(residence.GetPlacedDecor().Any(d => d.DecorId == decor.DecorId), "placed decor not listed as placed");
            decor.Crate();
            Expect(!residence.GetPlacedDecor().Any(d => d.DecorId == decor.DecorId), "crated decor still listed as placed");

            var privacy = residence.PrivacyLevel;
            me.ResidenceManager.SetResidencePrivacy(Game.Housing.Static.ResidencePrivacyLevel.Private);
            Expect(residence.PrivacyLevel == Game.Housing.Static.ResidencePrivacyLevel.Private, "privacy not set to private");
            me.ResidenceManager.SetResidencePrivacy(privacy);
            Expect(residence.PrivacyLevel == privacy, "privacy not restored");
            Expect(residence.GetPlots().Any(), "residence has no plots");
        }

        private static void TradeskillProbe(Player me)
        {
            string step = "start";
            try
            {
            var manager = me.TradeskillManager;
            step = "skills";
            var skills = GameTableManager.Instance.Tradeskill.Entries.Where(r => r != null).Where(t => t.Id != 0u).ToList();
            var skill = skills[(int)(me.CharacterId % (ulong)skills.Count)];
            step = "learn";
            if (!manager.HasLearned(skill.Id))
                Expect(manager.Learn(skill.Id) || (Game.Tradeskill.TradeskillManager.CountsAgainstLimit(skill) && manager.LimitedSkillCount >= Game.Tradeskill.TradeskillManager.MaxTrainerSkills), $"could not learn {skill.Id}: {manager.LastRefusal}");

            if (!manager.HasLearned(skill.Id))
                return;

            step = "schematic";
            var schematic = GameTableManager.Instance.TradeskillSchematic2.Entries.Where(r => r != null)
                .Where(e => e.TradeSkillId == skill.Id && e.Tier <= 1u && e.DiscoverableQuadrant == 0u && e.Item2IdOutput != 0u)
                .OrderBy(e => e.Id).FirstOrDefault();
            if (schematic == null)
                return;

            step = "grant";
            EnsureRoom(me, 8u);
            uint[] materials = { schematic.Item2IdMaterial00, schematic.Item2IdMaterial01, schematic.Item2IdMaterial02, schematic.Item2IdMaterial03, schematic.Item2IdMaterial04 };
            uint[] costs     = { schematic.MaterialCost00, schematic.MaterialCost01, schematic.MaterialCost02, schematic.MaterialCost03, schematic.MaterialCost04 };
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] != 0u)
                    me.Inventory.ItemCreate(InventoryLocation.Inventory, materials[i], Math.Max(costs[i], 1u));

            uint xp = manager.GetXp(skill.Id);
            step = "craft";
            var result = manager.Craft(schematic.Id);
            Expect(result == Game.Tradeskill.CraftResult.Success || result == Game.Tradeskill.CraftResult.Critical,
                $"schematic {schematic.Id} skill {skill.Id}: {result}");
            Expect(manager.GetXp(skill.Id) >= xp, "xp went down");

            // coordinate crafting: a click far outside the schematic's radius must fail
            if ((skill.Flags & 8u) != 0u && schematic.Radius > 0f)
            {
                step = "coordinate miss";
                EnsureRoom(me, 8u);
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] != 0u)
                        me.Inventory.ItemCreate(InventoryLocation.Inventory, materials[i], Math.Max(costs[i], 1u));

                var miss = manager.Craft(schematic.Id, schematic.VectorX + schematic.Radius * 4f + 1f, schematic.VectorY);
                Expect(miss == Game.Tradeskill.CraftResult.Failed, $"coordinate craft far outside the radius gave {miss}");
                FailureLedger.Record("system-ok", "tradeskill-coordinate-miss");
            }
            }
            catch (NullReferenceException ex)
            {
                throw new ProbeFailure("NRE " + ex.StackTrace.Split('\n')[0] + " step " + step);
            }
        }

        /// <summary>
        /// Tradeskills through the client message handlers: learn (0x0857), craft a tier schematic at a station (0x0852, or 0x0851 + 0x0850
        /// for coordinate professions), pick and reset a talent (0x084E, 0x0858) when the skill has a point.
        /// </summary>
        private static void TradeskillHandlerProbe(Player me)
        {
            var manager = me.TradeskillManager;
            // the learned crafting (non-gathering) skill with the lowest tier, so tier progression gets exercised too
            uint skill = manager.LearnedSkills.Where(id => GameTableManager.Instance.Tradeskill.GetEntry(id) is { } t && (t.Flags & 2u) == 0u
                    && GameTableManager.Instance.TradeskillSchematic2.Entries.Any(e => e != null && e.TradeSkillId == id && e.Item2IdMaterial00 != 0u))
                .OrderBy(id => manager.GetTier(id) >= GameTableManager.Instance.TradeskillTier.Entries.Where(t => t != null && t.TradeSkillId == id).Select(t => t.Tier).DefaultIfEmpty(1u).Max() ? 1 : 0)
                .ThenBy(id => manager.GetTier(id)).FirstOrDefault();
            if (skill == 0u)
            {
                var crafting = GameTableManager.Instance.Tradeskill.Entries.Where(t => t != null && Game.Tradeskill.TradeskillManager.IsCraftingSkill(t)).ToList();
                skill = crafting[(int)(me.CharacterId % (ulong)crafting.Count)].Id;
                Expect(BotTradeskills.Learn(me, skill), $"learn {skill} through the handler refused");
                FailureLedger.Record("system-ok", "tradeskill-learn-handler");
            }

            var station = BotTradeskills.NearStation(me, skill);
            if (station == null)
            {
                if (!BotTradeskills.GoToStation(me, skill))
                    throw new ProbeSkip();
                RunNext(me, "tradeskill-handlers");
                return;
            }

            var schematic = GameTableManager.Instance.TradeskillSchematic2.Entries
                .Where(e => e != null && e.TradeSkillId == skill && e.Tier <= manager.GetTier(skill) && e.DiscoverableQuadrant == 0u
                    && e.TradeskillSchematic2IdParent == 0u && e.Item2IdOutput != 0u && e.Item2IdMaterial00 != 0u && manager.KnowsSchematic(e))
                .OrderByDescending(e => e.Tier).ThenBy(e => e.Id).FirstOrDefault();
            if (schematic == null)
                throw new ProbeSkip();

            EnsureRoom(me, 8u);
            BotTradeskills.EnsureMaterials(me, schematic);
            uint xp = manager.GetXp(skill);
            uint had = me.Inventory.GetItemCount(schematic.Item2IdOutput) + (schematic.Item2IdOutputFail == 0u ? 0u : me.Inventory.GetItemCount(schematic.Item2IdOutputFail));
            BotTradeskills.CraftAtStation(me, schematic, station.Guid);
            uint have = me.Inventory.GetItemCount(schematic.Item2IdOutput) + (schematic.Item2IdOutputFail == 0u ? 0u : me.Inventory.GetItemCount(schematic.Item2IdOutputFail));
            Expect(have > had || manager.GetXp(skill) > xp, $"station craft of schematic {schematic.Id} (skill {skill}) through the handler made nothing");
            FailureLedger.Record("system-ok", "tradeskill-craft-handler");

            // tier progression by crafting: now and then the probe bot works its skill up one tier (BotTradeskills goal, checked in the ledger
            // as tradeskill-tier-reached-by-crafting)
            uint maxTier = GameTableManager.Instance.TradeskillTier.Entries.Where(t => t != null && t.TradeSkillId == skill).Select(t => t.Tier).DefaultIfEmpty(1u).Max();
            if (manager.GetTier(skill) < maxTier && !BotTradeskills.HasGoal(me) && BotManager.Rng.Next(3) == 0)
                BotTradeskills.SetGoal(me, skill, manager.GetTier(skill) + 1u, 0u);
            else if (manager.GetTier(skill) >= maxTier && !BotTradeskills.HasGoal(me) && BotManager.Rng.Next(4) == 0)
            {
                // everything maxed: swap a crafting skill at the trainer (0x0857 with a skill to drop) and work the new one up to tier 2
                var craftingSkills = GameTableManager.Instance.Tradeskill.Entries.Where(t => t != null && Game.Tradeskill.TradeskillManager.IsCraftingSkill(t)).Select(t => t.Id).ToList();
                uint drop = manager.LearnedSkills.FirstOrDefault(craftingSkills.Contains);
                uint take = craftingSkills.Where(id => !manager.HasLearned(id)).OrderBy(_ => BotManager.Rng.Next()).FirstOrDefault();
                if (drop != 0u && take != 0u)
                {
                    BotTradeskills.PayRelearn(me, take);
                    TradeskillHandler.HandleLearn(me.Session, BotPackets.Build<ClientTradeskillLearn>(w =>
                    {
                        w.Write(take);
                        w.Write(drop);
                    }));
                    Expect(manager.HasLearned(take) && !manager.HasLearned(drop), $"swap {drop} -> {take} through the trainer message failed");
                    FailureLedger.Record("system-ok", "tradeskill-swap-handler");
                    BotTradeskills.SetGoal(me, take, 2u, 0u);
                }
            }

            // talent picks and resets: the tradeskill-talent probe
        }

        /// <summary>Gather a harvest node with the tool through ClientItemUse: loot or xp must arrive and the node must leave.</summary>
        private static void HarvestProbe(Player me)
        {
            if (me.Map == null)
                throw new ProbeSkip();

            me.Map.Search(me.Position, 60f, new Game.Map.Search.SearchCheckRange(me.Position, 60f, me), out List<GridEntity> around);
            // nodes of skills that have a harvesting tool (Farmer has none in the tables: how farmers gather is not known)
            HarvestUnit node = around.OfType<HarvestUnit>().Where(n => !n.IsDead && n.RequiredTier is { Tier: 1u } tier && HarvestUnit.ToolSkills.Values.Contains(tier.TradeSkillId))
                .OrderBy(n => System.Numerics.Vector3.Distance(n.Position, me.Position)).FirstOrDefault();
            if (node == null)
            {
                if (!BotTradeskills.GoToNode(me))
                    throw new ProbeSkip();
                RunNext(me, "harvest");
                return;
            }

            EnsureRoom(me, 4u);
            uint skill = node.RequiredTier.TradeSkillId;
            uint xp = me.TradeskillManager.GetXp(skill);
            if (System.Numerics.Vector3.Distance(node.Position, me.Position) > 6f)
                me.TeleportTo(me.Map.Entry, node.Position + new System.Numerics.Vector3(1.5f, 0f, 1.5f), (me.Map as Game.Map.MapInstance)?.InstanceId);
            Expect(BotTradeskills.Harvest(me, node), $"harvest of node {node.CreatureId} (skill {skill}) refused");
            // the tool spell resolves on the next spell update
            RunNext(me, "harvest-check");
            harvestChecks.Remove(me);
            harvestChecks.Add(me, new HarvestCheck { Skill = skill, Xp = xp, Node = node });
        }

        /// <summary>
        /// Farmer: gather a farming node with the class auto attack (the client's interact path for skill 20 nodes). The node must be gathered
        /// (it leaves the map) on the next spell update.
        /// </summary>
        private static void FarmProbe(Player me)
        {
            if (me.Map == null)
                throw new ProbeSkip();

            static bool IsFarm(uint creature) => GameTableManager.Instance.Creature2.GetEntry(creature) is { } c
                && GameTableManager.Instance.TradeskillHarvestingInfo.GetEntry(c.TradeskillHarvestingInfoId) is { } info
                && GameTableManager.Instance.TradeskillTier.GetEntry(info.TradeSkillTierId) is { } tier && HarvestUnit.IsHandGathered(tier.TradeSkillId);
            me.Map.Search(me.Position, 60f, new Game.Map.Search.SearchCheckRange(me.Position, 60f, me), out List<GridEntity> around);
            HarvestUnit node = around.OfType<HarvestUnit>().Where(n => !n.IsDead && IsFarm(n.CreatureId))
                .OrderBy(n => System.Numerics.Vector3.Distance(n.Position, me.Position)).FirstOrDefault();
            if (node == null)
            {
                if (!BotTradeskills.GoToNode(me, IsFarm))
                    throw new ProbeSkip();
                RunNext(me, "farm");
                return;
            }

            EnsureRoom(me, 4u);
            if (System.Numerics.Vector3.Distance(node.Position, me.Position) > 4f)
                me.TeleportTo(me.Map.Entry, node.Position + new System.Numerics.Vector3(1.5f, 0f, 1.5f), (me.Map as Game.Map.MapInstance)?.InstanceId);
            Expect(BotTradeskills.Harvest(me, node), $"farming node {node.CreatureId} refused");
            RunNext(me, "farm-check");
            harvestChecks.Remove(me);
            harvestChecks.Add(me, new HarvestCheck { Skill = node.RequiredTier.TradeSkillId, Node = node });
        }

        private static void FarmCheckProbe(Player me)
        {
            if (!harvestChecks.TryGetValue(me, out HarvestCheck check))
                throw new ProbeSkip();
            harvestChecks.Remove(me);
            Expect(check.Node.Map == null || check.Node.IsDead, $"farming node {check.Node.CreatureId} not gathered by the auto attack (spell {Game.Spell.Spell.LastFailure?.ToString() ?? "no failure"})");
            FailureLedger.Record("system-ok", "farm-auto-attack");
        }

        /// <summary>
        /// Salvage through ClientItemSalvage 0x015E: a salvageable bag item (else a counted grant of an item with researched salvage rows) must
        /// leave the bag and its outputs arrive.
        /// </summary>
        private static void SalvageProbe(Player me)
        {
            uint minLevel = GameTableManager.Instance.GameFormula.GetEntry(1034u)?.Dataint0 ?? 0u;
            if (me.Level < minLevel)
                throw new ProbeSkip();
            EnsureRoom(me, 6u);

            Game.Entity.Item item = me.Inventory.GetItems(InventoryLocation.Inventory).FirstOrDefault(i => i?.Info?.Entry != null
                && Game.Tradeskill.Salvage.ResearchedItems.Contains(i.Info.Entry.Id));
            if (item == null)
            {
                var known = Game.Tradeskill.Salvage.ResearchedItems.Where(id => Game.Tradeskill.Salvage.CanSalvage(me, GameTableManager.Instance.Item.GetEntry(id)) == null
                    && !Game.Tradeskill.Salvage.NeedsKey(GameTableManager.Instance.Item.GetEntry(id))).ToList();
                if (known.Count == 0)
                    throw new ProbeSkip();
                uint grant = known[BotManager.Rng.Next(known.Count)];
                me.Inventory.ItemCreate(InventoryLocation.Inventory, grant, 1u);
                FailureLedger.Record("salvage-qa-item-grant", $"item {grant}");
                item = me.Inventory.GetItems(InventoryLocation.Inventory).FirstOrDefault(i => i?.Info?.Entry?.Id == grant);
                if (item == null)
                    throw new ProbeSkip();
            }

            uint id = item.Info.Entry.Id;
            uint before = me.Inventory.GetItemCount(id);
            uint freeBefore = me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory);
            ItemHandler.HandleItemSalvage(me.Session, BotPackets.Build<ClientItemSalvage>(w =>
            {
                w.Write((uint)item.Location, 9u);
                w.Write(item.BagIndex);
                w.Write(item.Guid);
            }));
            Expect(me.Inventory.GetItemCount(id) < before, $"salvage of item {id} through 0x015E left it in the bag");
            FailureLedger.Record("system-ok", "salvage-handler");
            FailureLedger.Record("bot-salvage", $"item {id}", $"free slots {freeBefore} -> {me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory)}");
        }

        /// <summary>A coordinate craft started with a global catalyst (ClientCraftingCraftItem catalyst field): the catalyst must be used up.</summary>
        private static void CatalystCraftProbe(Player me)
        {
            var manager = me.TradeskillManager;
            var catalystKeys = GameTableManager.Instance.TradeskillCatalyst.Entries.Where(r => r != null && r.TradeSkillId != 0u && Game.Tradeskill.TradeskillCatalysts.IsGlobal(r))
                .Select(r => (r.TradeSkillId, r.Tier)).ToHashSet();
            TradeskillSchematic2Entry schematic = GameTableManager.Instance.TradeskillSchematic2.Entries
                .Where(e => e != null && manager.HasLearned(e.TradeSkillId) && catalystKeys.Contains((e.TradeSkillId, e.Tier)) && e.TradeskillSchematic2IdParent == 0u
                    && e.Item2IdMaterial00 != 0u && manager.KnowsSchematic(e) && e.Tier <= manager.GetTier(e.TradeSkillId)
                    && (GameTableManager.Instance.Tradeskill.GetEntry(e.TradeSkillId)?.Flags & 8u) is > 0u)
                .OrderBy(_ => BotManager.Rng.Next()).FirstOrDefault();
            if (schematic == null)
            {
                // Cooking (hobby, coordinate, catalysts at tier 1) is open to every character
                if (!manager.HasLearned(2u) && BotTradeskills.Learn(me, 2u))
                    RunNext(me, "craft-catalyst");
                throw new ProbeSkip();
            }

            var station = BotTradeskills.NearStation(me, schematic.TradeSkillId);
            if (station == null)
            {
                if (!BotTradeskills.GoToStation(me, schematic.TradeSkillId))
                    throw new ProbeSkip();
                RunNext(me, "craft-catalyst");
                return;
            }

            EnsureRoom(me, 6u);
            BotTradeskills.EnsureMaterials(me, schematic);
            uint catalyst = BotTradeskills.CatalystItem(me, schematic);
            if (catalyst == 0u)
                throw new ProbeSkip();
            uint had = me.Inventory.GetItemCount(catalyst);
            // the base output or a variant's (an attracting catalyst moves the point onto a variant); Cooking gives no xp (CraftXp 0)
            var outputs = GameTableManager.Instance.TradeskillSchematic2.Entries.Where(e => e != null && (e.Id == schematic.Id || e.TradeskillSchematic2IdParent == schematic.Id))
                .SelectMany(e => new[] { e.Item2IdOutput, e.Item2IdOutputFail }).Where(i => i != 0u).Distinct().ToList();
            uint made = (uint)outputs.Sum(i => me.Inventory.GetItemCount(i));
            BotTradeskills.CraftAtStation(me, schematic, station.Guid, true);
            Expect(me.Inventory.GetItemCount(catalyst) < had, $"catalyst {catalyst} not used by the craft of schematic {schematic.Id}");
            Expect(outputs.Sum(i => me.Inventory.GetItemCount(i)) > made, $"catalyst craft of schematic {schematic.Id} made nothing");
            FailureLedger.Record("system-ok", "craft-catalyst-handler");
        }

        /// <summary>
        /// Talent pick through ClientTradeskillPickTalent and a craft with the talent active (bonuses in the ledger as tradeskill-bonus-applied).
        /// A bot without points gets one counted QA point (the real source is a tradeskill achievement).
        /// </summary>
        private static void TalentProbe(Player me)
        {
            var manager = me.TradeskillManager;
            uint skill = manager.LearnedSkills.Where(id => GameTableManager.Instance.TradeskillTalentTier.Entries.Any(r => r != null && r.TradeSkillId == id))
                .OrderBy(_ => BotManager.Rng.Next()).FirstOrDefault();
            if (skill == 0u)
                throw new ProbeSkip();
            var station = BotTradeskills.NearStation(me, skill);
            if (station == null)
            {
                if (!BotTradeskills.GoToStation(me, skill))
                    throw new ProbeSkip();
                RunNext(me, "tradeskill-talent");
                return;
            }

            // the next closed tier opens with one more point (counted QA grant; the real source is a tradeskill achievement)
            var tiers = Game.Tradeskill.TradeskillManager.TalentTiers(skill);
            int picked = manager.PickedTalents(skill).Count();
            if (picked < tiers.Count && tiers.Skip(picked).First().PointsToUnlock > manager.TalentPoints(skill))
                while (manager.TalentPoints(skill) < tiers.Skip(picked).First().PointsToUnlock)
                    manager.GrantQaTalentPoint(skill);
            BotTradeskills.PickTalents(me, skill);
            Expect(manager.PickedTalents(skill).Count() > picked || picked >= tiers.Count, $"talent pick of skill {skill} through 0x084E refused");
            if (manager.PickedTalents(skill).Count() > picked)
                FailureLedger.Record("system-ok", "tradeskill-talent-pick");

            var schematic = GameTableManager.Instance.TradeskillSchematic2.Entries
                .Where(e => e != null && e.TradeSkillId == skill && e.Tier <= manager.GetTier(skill) && e.DiscoverableQuadrant == 0u && e.TradeskillSchematic2IdParent == 0u
                    && e.Item2IdOutput != 0u && e.Item2IdMaterial00 != 0u && manager.KnowsSchematic(e))
                .OrderByDescending(e => e.Tier).ThenBy(e => e.Id).FirstOrDefault();
            if (schematic == null)
                return;
            EnsureRoom(me, 6u);
            BotTradeskills.EnsureMaterials(me, schematic);
            BotTradeskills.CraftAtStation(me, schematic, station.Guid);

            // keep at most 3 picks per skill on probe bots: reset now and then (0x0858, the respec cost is paid by the harness)
            if (manager.PickedTalents(skill).Count() >= 3)
            {
                uint cost = GameTableManager.Instance.TradeskillTalentTier.Entries.Where(r => r != null && r.TradeSkillId == skill).Select(r => r.RespecCost).DefaultIfEmpty(0u).Max();
                me.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, Math.Max(cost, 1u));
                TradeskillHandler.HandleResetTalents(me.Session, BotPackets.Build<ClientTradeskillResetTalents>(w => w.Write(skill)));
                Expect(!manager.PickedTalents(skill).Any(), $"talent reset of skill {skill} kept picks");
                FailureLedger.Record("system-ok", "tradeskill-talent-reset");
            }
        }

        private sealed class HarvestCheck
        {
            public uint Skill;
            public uint Xp;
            public HarvestUnit Node;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, HarvestCheck> harvestChecks = new();

        private static void HarvestCheckProbe(Player me)
        {
            if (!harvestChecks.TryGetValue(me, out HarvestCheck check))
                throw new ProbeSkip();
            harvestChecks.Remove(me);
            Expect(me.TradeskillManager.GetXp(check.Skill) > check.Xp, $"harvest of node {check.Node.CreatureId} gave no {check.Skill} xp (spell {Game.Spell.Spell.LastFailure?.ToString() ?? "no failure"})");
            FailureLedger.Record("system-ok", "harvest-item-use");
        }

        /// <summary>Buy the first item of a vendor in range through the real handlers, check credits and bag, then sell it back.</summary>
        private static void VendorProbe(Player me)
        {
            if (me.Map == null)
                throw new ProbeSkip();

            me.Map.Search(me.Position, 1000f, new Game.Map.Search.SearchCheckRange(me.Position, 1000f, me), out List<GridEntity> around);
            NonPlayer vendor = around.OfType<NonPlayer>().FirstOrDefault(n => n.VendorInfo != null && n.VendorInfo.Items.Any());
            if (vendor == null)
            {
                // go to a vendor spawn, the next probe turn of this bot runs the vendor probe first
                // go to a vendor spawn, the next probe turn of this bot runs the vendor probe first
                var spawns = vendorSpawns;
                if (spawns == null)
                {
                    LoadVendorSpawns();
                    throw new ProbeSkip();
                }

                if (spawns.Count > 0)
                {
                    var (world, position) = spawns[BotManager.Rng.Next(spawns.Count)];
                    me.TeleportTo(world, position.X, position.Y, position.Z);
                    RunNext(me, "vendor");
                }
                else
                    FailureLedger.Record("system-skip", "vendor: no usable vendor spawn");

                throw new ProbeSkip();
            }

            EnsureRoom(me, 4u);
            var session = me.Session;
            Network.Message.Handler.VendorHandler.HandleClientVendor(session, BotPackets.Build<NexusForever.WorldServer.Network.Message.Model.ClientEntityInteract>(w =>
            {
                w.Write(vendor.Guid);
                w.Write((byte)5, 7);
            }));
            Expect(me.SelectedVendorInfo == vendor.VendorInfo, "vendor not selected after interact");

            var offer = vendor.VendorInfo.Items.First();
            var itemEntry = GameTableManager.Instance.Item.GetEntry(offer.ItemId);
            me.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 1000000ul);
            ulong credits = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            uint count = me.Inventory.GetItemCount(offer.ItemId);

            Network.Message.Handler.VendorHandler.HandleVendorPurchase(session, BotPackets.Build<NexusForever.WorldServer.Network.Message.Model.ClientVendorPurchase>(w =>
            {
                w.Write(offer.Index);
                w.Write(1u);
            }));
            uint bought = me.Inventory.GetItemCount(offer.ItemId);
            Expect(bought > count, $"vendor item {offer.ItemId} not in bag after purchase");
            bool free = itemEntry.CurrencyAmount.All(a => a == 0u);
            Expect(free || (me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) < credits || itemEntry.CurrencyTypeId[0] != (uint)CurrencyType.Credits, "credits not charged");

            foreach (Game.Entity.Item item in me.Inventory.First(b => b.Location == InventoryLocation.Inventory).Where(i => i.Info.Id == offer.ItemId).Take(1).ToList())
            {
                uint index = item.BagIndex;

                Network.Message.Handler.VendorHandler.HandleVendorSell(session, BotPackets.Build<NexusForever.WorldServer.Network.Message.Model.ClientVendorSell>(w =>
                {
                    w.Write(InventoryLocation.Inventory, 9u);
                    w.Write(index);
                    w.Write(1u);
                }));

                // buyback: a sold item is offered again and buying it back returns it to the bags
                uint afterSell = me.Inventory.GetItemCount(offer.ItemId);
                var sold = Game.Entity.BuybackManager.Instance.GetItems(me).LastOrDefault(b => b.Item.Info.Id == offer.ItemId);
                if (afterSell < bought)
                {
                    Expect(sold != null, "sold item missing from the buyback list");
                    uint uniqueId = sold.UniqueId;
                    Network.Message.Handler.VendorHandler.HandleBuybackItemFromVendor(session, BotPackets.Build<NexusForever.WorldServer.Network.Message.Model.ClientBuybackItemFromVendor>(w => w.Write(uniqueId)));
                    Expect(me.Inventory.GetItemCount(offer.ItemId) > afterSell, "buyback did not return the item");
                    FailureLedger.Record("system-ok", "vendor-buyback");
                }
                break;
            }

            // sell junk (0x0167): a junk item (Item2Category 94) in the bag is sold with the vendor still open
            var junkEntry = GameTableManager.Instance.Item.Entries.FirstOrDefault(e => e != null && e.Item2CategoryId == Network.Message.Handler.VendorHandler.JunkCategory
                && e.CurrencyTypeIdSellToVendor[0] == (uint)CurrencyType.Credits && e.CurrencyAmountSellToVendor[0] > 0u);
            if (junkEntry != null)
            {
                me.Inventory.ItemCreate(InventoryLocation.Inventory, junkEntry.Id, 1u);
                Expect(me.Inventory.GetItemCount(junkEntry.Id) > 0u, $"junk item {junkEntry.Id} not created");
                ulong beforeJunk = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
                Network.Message.Handler.VendorHandler.HandleSellJunkToVendor(session, BotPackets.Build<NexusForever.WorldServer.Network.Message.Model.ClientSellJunkToVendor>(w => { }));
                Expect(me.Inventory.GetItemCount(junkEntry.Id) == 0u, $"junk item {junkEntry.Id} still in the bag after sell junk");
                Expect((me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) > beforeJunk, "sell junk paid nothing");
                FailureLedger.Record("system-ok", "vendor-sell-junk");
            }

            RepairCheck(me);
            // take back the probe's grant, less what the vendor steps spent of it
            me.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, System.Math.Min(1000000ul, me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul));
        }

        /// <summary>Starts a combat challenge and feeds it kills of its target group through the quest manager's kill hook (the same hook real kills use).</summary>
        private static void ChallengeProbe(Player me)
        {
            bool activateKind = BotManager.Rng.Next(3) == 0;
            var entry = GameTableManager.Instance.Challenge.Entries.Where(r => r != null)
                .Where(c => c.Id != 0u && c.ChallengeTypeEnum == (activateKind ? 5u : 0u) && c.Target != 0u)
                .OrderBy(c => c.Id)
                .Skip((int)(me.CharacterId % 20ul))
                .FirstOrDefault(c => Game.Quest.TargetGroupHelper.ResolveSpawned(c.Target).Count > 0);
            if (entry == null)
                throw new ProbeSkip();

            var manager = me.ChallengeManager;
            if (!manager.Start(entry.Id))
                throw new ProbeSkip();

            uint creature = Game.Quest.TargetGroupHelper.ResolveSpawned(entry.Target).First();
            for (int i = 0; i < 40 && manager.ActiveChallenges.Any(a => a.Entry.Id == entry.Id); i++)
                if (activateKind)
                    me.QuestManager.OnCreatureActivated(creature);
                else
                    me.QuestManager.OnCreatureKilled(creature);

            Expect(manager.BestMedal(entry.Id) > 0, $"challenge {entry.Id} gave no medal after 40 kills");
        }

        /// <summary>ActivateChallenge / AbandonChallenge through 0x00C5 (choice 1 and 2).</summary>
        private static void ChallengeChoiceProbe(Player me)
        {
            var manager = me.ChallengeManager;
            var entry = GameTableManager.Instance.Challenge.Entries
                .Where(c => c != null && c.Id != 0u && c.Id < (1u << 14) && (c.ChallengeTypeEnum == 0u || c.ChallengeTypeEnum == 5u) && c.Target != 0u
                    && !manager.ActiveChallenges.Any(a => a.Entry.Id == c.Id))
                .Skip((int)(me.CharacterId % 50ul))
                .FirstOrDefault();
            if (entry == null)
                throw new ProbeSkip();

            ClientChallengeChoice Choice(byte choice) => BotPackets.Build<ClientChallengeChoice>(w =>
            {
                w.Write(entry.Id, 14u);
                w.Write(choice, 5u);
                w.Write(0u);
            });
            Network.Message.Handler.LuaApiHandler.HandleChallengeChoice(me.Session, Choice(ClientChallengeChoice.Activate));
            if (!manager.ActiveChallenges.Any(a => a.Entry.Id == entry.Id))
                throw new ProbeSkip(); // no tiers or target in the table row (ledger challenge-no-tiers-or-target)
            Network.Message.Handler.LuaApiHandler.HandleChallengeChoice(me.Session, Choice(ClientChallengeChoice.Abandon));
            Expect(!manager.ActiveChallenges.Any(a => a.Entry.Id == entry.Id), $"challenge {entry.Id} still active after abandon");
        }

        /// <summary>GroupLib.Join (0x0422) to an ungrouped bot's one-member group, the leader accepts with 0x0453, both leave again.</summary>
        private static void GroupJoinProbe(Player me)
        {
            var groups = Game.Groups.GroupManager.Instance;
            if (groups.GetGroup(me.CharacterId) != null || Game.Groups.GroupFinder.Instance.IsQueued(me))
                throw new ProbeSkip();
            // another system prober as the leader (quest, dungeon and event bots are not disturbed)
            Player leader = BotManager.Instance.Bots.Where(b => b.RunsSystemProbes).Select(b => b.Session?.Player)
                .FirstOrDefault(p => p != null && p != me && p.Map != null && !p.IsDead && p.Faction1 == me.Faction1 && groups.GetGroup(p.CharacterId) == null
                    && !Game.Groups.GroupFinder.Instance.IsQueued(p));
            if (leader == null)
                throw new ProbeSkip();

            var group = groups.CreateServerGroup(new[] { leader }, Game.Groups.GroupManager.MaxGroupSize);
            try
            {
                Network.Message.Handler.LuaApiHandler.HandleGroupRequestJoin(me.Session, BotPackets.Build<ClientGroupRequestJoin>(w =>
                {
                    w.WriteStringWide(leader.Name);
                    w.WriteStringWide("");
                }));
                Network.Message.Handler.LuaApiHandler.HandleGroupRequestJoinResponse(leader.Session, BotPackets.Build<ClientGroupRequestJoinResponse>(w =>
                {
                    w.Write(group.Id);
                    w.Write(true);
                    w.WriteStringWide(me.Name);
                    w.Write(false);
                }));
                Expect(groups.GetGroup(me.CharacterId)?.Id == group.Id, "join request accepted by the leader but the requester is not in the group");
            }
            finally
            {
                if (groups.GetGroup(me.CharacterId) != null)
                    groups.Leave(me, group.Id, false);
                if (groups.GetGroup(leader.CharacterId) != null)
                    groups.Leave(leader, group.Id, true);
            }
        }

        /// <summary>LeaveQueue (0x05B5) takes the bot out of the group finder queue.</summary>
        private static void MatchLeaveProbe(Player me)
        {
            var finder = Game.Groups.GroupFinder.Instance;
            if (Game.Groups.GroupManager.Instance.GetGroup(me.CharacterId) != null)
                throw new ProbeSkip();
            if (!finder.IsQueued(me))
            {
                var available = finder.AvailableDungeons(me);
                if (available.Count == 0 || !finder.Enqueue(me, Game.Groups.GroupFinder.Role.Damage, available[0]))
                    throw new ProbeSkip();
            }
            Network.Message.Handler.LuaApiHandler.HandleMatchingQueueLeave(me.Session, BotPackets.Build<ClientMatchingQueueLeave>(w => w.Write(0u, 5u)));
            Expect(!finder.IsQueued(me), "still queued after LeaveQueue");
        }

        /// <summary>Reset times: the weekly reset is a Wednesday 10:00 UTC after now, the daily one is 10:00 UTC within 24 h.</summary>
        private static int resetProbeBoss = 1000;

        private static void ResetProbe(Player me)
        {
            var now = DateTime.UtcNow;
            uint boss = (uint)System.Threading.Interlocked.Increment(ref resetProbeBoss);
            var daily = Game.Groups.InstanceLockouts.NextDailyReset(now);
            var weekly = Game.Groups.InstanceLockouts.NextWeeklyReset(now);
            Expect(daily > now && daily - now <= TimeSpan.FromHours(24d) && daily.Hour == 10, $"daily reset {daily:u}");
            Expect(weekly > now && weekly - now <= TimeSpan.FromDays(7d) && weekly.DayOfWeek == DayOfWeek.Wednesday && weekly.Hour == 10, $"weekly reset {weekly:u}");
            Expect(Game.Groups.InstanceLockouts.TryLockRaidBoss(me.CharacterId, 65535, boss), "first raid boss lock denied");
            Expect(!Game.Groups.InstanceLockouts.TryLockRaidBoss(me.CharacterId, 65535, boss), "second raid boss lock allowed");
        }

        /// <summary>Two turns: move the bot to a taxi node, then buy the route to the destination (credits charged, destination unlocked).</summary>
        private static void TaxiProbe(Player me)
        {
            var routes = GameTableManager.Instance.TaxiRoute.Entries.Where(r => r != null && r.Price > 0u && me.Map != null
                && me.TaxiManager.FactionAllowed(r.TaxiNodeIdSource) && me.TaxiManager.FactionAllowed(r.TaxiNodeIdDestination)).ToList();
            var route = routes[BotManager.Rng.Next(routes.Count)];

            // a route into a node of the other faction is refused
            var foreign = GameTableManager.Instance.TaxiRoute.Entries.FirstOrDefault(r => r != null && !me.TaxiManager.FactionAllowed(r.TaxiNodeIdDestination));
            if (foreign != null)
            {
                var refused = me.TaxiManager.Travel(foreign.TaxiNodeIdDestination);
                Expect(refused != Game.Travel.TaxiResult.Ok, "taxi to a node of the other faction was accepted");
            }

            if (!Game.Travel.TaxiManager.TryGetLocation(route.TaxiNodeIdSource, out ushort world, out System.Numerics.Vector3 position)
                || !Game.Travel.TaxiManager.TryGetLocation(route.TaxiNodeIdDestination, out _, out _))
                throw new ProbeSkip();

            if (me.TaxiManager.CurrentNode() != route.TaxiNodeIdSource)
            {
                if (!BotQuests.CanVisitWorld(world))
                    throw new ProbeSkip();

                me.TeleportTo(world, position.X, position.Y, position.Z);
                RunNext(me, "taxi");
                throw new ProbeSkip();
            }

            me.TaxiManager.Unlock(route.TaxiNodeIdDestination);
            me.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, route.Price);
            ulong before = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            var result = me.TaxiManager.Travel(route.TaxiNodeIdDestination);
            Expect(result == Game.Travel.TaxiResult.Ok, $"route {route.Id}: {result}");
            Expect((me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) == before - route.Price, "taxi price not charged");
        }

        /// <summary>Another online bot to trade with, null when there is none.</summary>
        private static Player Partner(Player me)
        {
            return NexusForever.Shared.Network.NetworkManager<Network.WorldSession>.Instance
                .GetSessions(s => s.Player != null && s.Player != me && s.Player.Session is BotSession && s.Player.Map != null && s.Player.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) > 2u)
                .Select(s => s.Player).FirstOrDefault();
        }

        private static void AuctionProbe(Player me)
        {
            Player buyer = Partner(me) ?? throw new ProbeSkip();
            EnsureRoom(me, 3u);
            EnsureRoom(buyer, 3u);
            var market = Game.Market.MarketManager.Instance;
            uint itemId = TestItemId();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 2u);
            buyer.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 5000ul);
            ulong sellerCredits = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            ulong buyerCredits = buyer.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            uint buyerItems = buyer.Inventory.GetItemCount(itemId);

            var listing = market.CreateAuction(me, itemId, 2u, 100ul, 1000ul, TimeSpan.FromHours(1d));
            Expect(listing != null, "listing refused");
            Expect(market.Bid(buyer, listing.Id, 200ul), "bid refused");
            Expect(!market.Bid(me, listing.Id, 300ul), "seller could bid on own auction");
            Expect(buyer.CurrencyManager.GetCurrency(CurrencyType.Credits) == buyerCredits - 200ul, "bid not escrowed");
            market.Update(DateTime.UtcNow + TimeSpan.FromHours(2d));
            Expect(buyer.Inventory.GetItemCount(itemId) >= buyerItems + 2u, "winner did not get the item");
            Expect(me.CurrencyManager.GetCurrency(CurrencyType.Credits) == sellerCredits + (ulong)(200 * (1d - Game.Market.MarketManager.AuctionCut)), "seller payout wrong");
            me.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, (ulong)(200 * (1d - Game.Market.MarketManager.AuctionCut)));
        }

        private static void CommodityProbe(Player me)
        {
            Player buyer = Partner(me) ?? throw new ProbeSkip();
            EnsureRoom(me, 3u);
            EnsureRoom(buyer, 3u);
            var market = Game.Market.MarketManager.Instance;
            uint itemId = TestItemId();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 5u);
            buyer.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 5000ul);
            uint buyerItems = buyer.Inventory.GetItemCount(itemId);

            Expect(market.PlaceOrder(me, true, itemId, 5u, 10ul) == 0u, "sell order matched an empty book");
            Expect(market.PlaceOrder(buyer, false, itemId, 3u, 20ul) == 3u, "buy order did not cross the sell order");
            Expect(buyer.Inventory.GetItemCount(itemId) >= buyerItems + 3u, "buyer did not get the commodities");
            var rest = market.Orders.FirstOrDefault(o => o.Owner == me.CharacterId && o.ItemId == itemId && o.Sell);
            Expect(rest != null && rest.Count == 2u, "remaining sell quantity wrong");
            Expect(market.CancelOrder(me, rest.Id), "cancel failed");
        }

        private static void TradeProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            EnsureRoom(me, 3u);
            EnsureRoom(other, 3u);
            uint itemId = TestItemId();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 1u);
            other.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 500ul);
            uint before = other.Inventory.GetItemCount(itemId);
            Expect(Game.Market.MarketManager.Instance.Trade(me, itemId, 1u, 0ul, other, 0u, 0u, 500ul), "trade refused");
            Expect(other.Inventory.GetItemCount(itemId) == before + 1u, "item not traded");
            Expect(!Game.Market.MarketManager.Instance.Trade(me, itemId, 99999u, 0ul, other, 0u, 0u, 0ul), "trade with missing items accepted");
        }

        private static void FriendsProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            var friends = Game.Social.FriendManager.Instance;
            // FriendshipLib.AddByName / RemoveByName through the handlers (0x03B4 / 0x03C2)
            ClientFriendshipAddByName Add(byte type) => BotPackets.Build<ClientFriendshipAddByName>(w =>
            {
                w.WriteStringWide(other.Name);
                w.WriteStringWide("");
                w.Write(type, 4u);
                w.WriteStringWide("");
            });
            LuaApiHandler.HandleFriendshipAddByName(me.Session, Add(ClientFriendshipAddByName.Friend));
            Expect(friends.IsFriend(me.CharacterId, other.CharacterId), "friend list lacks the friend after AddByName");
            LuaApiHandler.HandleFriendshipRemoveByName(me.Session, BotPackets.Build<ClientFriendshipRemoveByName>(w =>
            {
                w.WriteStringWide(other.Name);
                w.WriteStringWide("");
                w.Write(ClientFriendshipAddByName.Friend, 4u);
            }));
            Expect(!friends.IsFriend(me.CharacterId, other.CharacterId), "friend still listed after RemoveByName");
            LuaApiHandler.HandleFriendshipAddByName(me.Session, Add(ClientFriendshipAddByName.Friend));
            LuaApiHandler.HandleFriendshipAddByName(me.Session, Add(ClientFriendshipAddByName.Ignore));
            Expect(!friends.IsFriend(me.CharacterId, other.CharacterId) && friends.IsIgnoring(me.CharacterId, other.CharacterId), "ignore did not replace the friend entry");
            Expect(friends.Unignore(me.CharacterId, other.CharacterId), "unignore failed");
            Expect(!friends.Ignore(me.CharacterId, me.CharacterId), "ignoring yourself accepted");

            // the owner's client gets the list changes (ServerFriendship*, layouts from the client readers)
            if (me.Session is not BotSession session)
                return;
            var sent = new List<NexusForever.Shared.Network.Message.IWritable>();
            void capture(NexusForever.Shared.Network.Message.IWritable m) { lock (sent) sent.Add(m); }
            session.ServerMessage += capture;
            try
            {
                LuaApiHandler.HandleFriendshipAddByName(me.Session, Add(ClientFriendshipAddByName.Rival));
                LuaApiHandler.HandleFriendshipAddByName(me.Session, Add(ClientFriendshipAddByName.Rival));
                FriendshipListHandler.HandleFriendshipSetNote(me.Session, BotPackets.Build<ClientFriendshipSetNote>(w =>
                {
                    w.Write(NexusForever.WorldServer.WorldServer.RealmId, 14u);
                    w.Write(other.CharacterId);
                    w.WriteStringWide("probe");
                }));
                LuaApiHandler.HandleFriendshipRemoveByName(me.Session, BotPackets.Build<ClientFriendshipRemoveByName>(w =>
                {
                    w.WriteStringWide(other.Name);
                    w.WriteStringWide("");
                    w.Write(ClientFriendshipAddByName.Rival, 4u);
                }));
            }
            finally
            {
                session.ServerMessage -= capture;
            }
            ulong id = Game.Social.FriendManager.FriendshipId(ClientFriendshipAddByName.Rival, other.CharacterId);
            lock (sent)
            {
                Expect(sent.OfType<ServerFriendshipAdd>().Any(a => a.Entry.FriendshipId == id && a.Entry.Name == other.Name), "no FriendshipAdd sent");
                Expect(sent.OfType<ServerFriendshipResult>().Any(r => r.Result == ServerFriendshipResult.PlayerAlreadyRival), "no PlayerAlreadyRival result for a duplicate");
                Expect(sent.OfType<ServerFriendshipSetNote>().Any(n => n.FriendshipId == id && n.Note == "probe"), "no note update sent");
                Expect(sent.OfType<ServerFriendshipRemove>().Any(r => r.FriendshipId == id), "no FriendshipRemove sent");
            }
            FailureLedger.Record("system-ok", "friendship-messages");
        }

        /// <summary>Whisper through the real handler: delivered normally, dropped while the recipient ignores the sender.</summary>
        private static void WhisperProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            if (other.Faction1 != me.Faction1)
                throw new ProbeSkip();

            var friends = Game.Social.FriendManager.Instance;
            int blocked() => FailureLedger.Snapshot().Where(e => e.Category == "whisper-blocked-ignored").Sum(e => e.Count);
            void whisper() => Network.Message.Handler.SocialHandler.HandleWhisper(me.Session, BotPackets.Build<NexusForever.WorldServer.Network.Message.Model.ClientChatWhisper>(w =>
            {
                w.WriteStringWide(other.Name);
                w.WriteStringWide("");
                w.WriteStringWide("probe");
                w.Write(0, 5u);
                w.Write(false);
            }));

            int before = blocked();
            friends.Ignore(other.CharacterId, me.CharacterId);
            whisper();
            Expect(blocked() == before + 1, "whisper to a player who ignores the sender was not blocked");
            friends.Unignore(other.CharacterId, me.CharacterId);
            whisper();
            Expect(blocked() == before + 1, "whisper blocked after unignore");
        }

        private static Network.Message.Model.ClientGuildOperation GuildOp(ulong guildId, Game.Guild.Static.GuildOperation operation, string who)
        {
            return BotPackets.Build<Network.Message.Model.ClientGuildOperation>(w =>
            {
                w.Write((ushort)0, 14u);
                w.Write(guildId);
                w.Write(0u);
                w.Write(0ul);
                w.WriteStringWide(who);
                w.Write(operation, 6u);
            });
        }

        // two bots through the real handlers: leader registers a circle, invites the partner, the partner accepts, the leader promotes and demotes, the partner quits
        private static void GuildPairProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            lock (guildLock)
            {
                if (other.GuildManager.Any(g => g.GetMember(me.CharacterId) != null))
                    throw new ProbeSkip();

                var circle = me.GuildManager.GetGuild<Game.Guild.Circle>(GuildType.Circle);
                if (circle == null)
                {
                    string name = ("QP " + me.Name);
                    name = name.Length > 24 ? name.Substring(0, 24) : name;
                    var result = me.GuildManager.CanRegisterGuild(GuildType.Circle, name, "Leader", "Council", "Member");
                    if (result.Result != Game.Guild.Static.GuildResult.Success)
                        throw new ProbeFailure($"circle register refused: {result.Result}");

                    me.GuildManager.RegisterGuild(GuildType.Circle, name, "Leader", "Council", "Member");
                    circle = me.GuildManager.GetGuild<Game.Guild.Circle>(GuildType.Circle);
                }

                Expect(circle != null, "circle missing after register");
                ulong id = circle.Id;
                if (circle.GetMember(other.CharacterId) != null)
                    throw new ProbeSkip();

                Game.Guild.GlobalGuildManager.Instance.HandleGuildOperation(me, GuildOp(id, Game.Guild.Static.GuildOperation.MemberInvite, other.Name));
                Expect(other.GuildManager.CanAcceptInviteToGuild().Result == Game.Guild.Static.GuildResult.Success, "invite not pending for the partner");
                Network.Message.Handler.GuildHandler.HandleInviteResponse(other.Session, BotPackets.Build<Network.Message.Model.ClientGuildInviteResponse>(w => w.Write(true)));
                var member = circle.GetMember(other.CharacterId);
                Expect(member != null, "partner not a member after accepting the invite");

                byte before = member.Rank.Index;
                Game.Guild.GlobalGuildManager.Instance.HandleGuildOperation(me, GuildOp(id, Game.Guild.Static.GuildOperation.MemberPromote, other.Name));
                Expect(member.Rank.Index < before, $"promote did not raise the rank (index {before} -> {member.Rank.Index})");
                Game.Guild.GlobalGuildManager.Instance.HandleGuildOperation(me, GuildOp(id, Game.Guild.Static.GuildOperation.MemberDemote, other.Name));
                Expect(member.Rank.Index == before, "demote did not restore the rank");

                Game.Guild.GlobalGuildManager.Instance.HandleGuildOperation(other, GuildOp(id, Game.Guild.Static.GuildOperation.MemberQuit, ""));
                Expect(circle.GetMember(other.CharacterId) == null, "partner still a member after quitting");
            }
        }

        private static void GuildProbe(Player me)
        {
            lock (guildLock)
            {
                string name = "QA " + me.Name;
                if (name.Length > 24)
                    name = name.Substring(0, 24);

                var existing = Game.Guild.GlobalGuildManager.Instance.GetGuild(name);
                Expect(true, "");
                if (existing == null)
                {
                    existing = Game.Guild.GlobalGuildManager.Instance.RegisterGuild(GuildType.Circle, name, "Leader", "Council", "Member");
                    Expect(existing != null, "circle not created");
                }
            }
        }
            private static void BankProbe(Player me)
        {
            EnsureRoom(me, 2u);
            uint itemId = TestItemId();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 1u);
            Game.Entity.Item item = me.Inventory.GetItems(InventoryLocation.Inventory).FirstOrDefault(i => i.Id == itemId);
            Expect(item != null, "created item not found in bags");

            uint? slot = me.Inventory.GetFirstFreeBagIndex(InventoryLocation.PlayerBank);
            if (slot == null)
            {
                me.Inventory.ItemDelete(itemId, 1u);
                throw new ProbeFailure("bank has no free slot for a bot (capacity not granted)");
            }

            me.Inventory.ItemMove(item, InventoryLocation.PlayerBank, slot.Value);
            Expect(item.Location == InventoryLocation.PlayerBank, $"item still in {item.Location} after bank move");

            uint? back = me.Inventory.GetFirstFreeBagIndex(InventoryLocation.Inventory);
            Expect(back != null, "no bag slot to move the item back");
            me.Inventory.ItemMove(item, InventoryLocation.Inventory, back.Value);
            Expect(item.Location == InventoryLocation.Inventory, "item did not return from the bank");
            me.Inventory.ItemDelete(itemId, 1u);
        }

        private static void CostumeProbe(Player me)
        {
            try
            {
                me.CostumeManager.SetCostume(0);
            }
            catch (InvalidOperationException)
            {
                throw new ProbeSkip(); // swap cooldown
            }

            Expect(me.CostumeIndex == 0, $"costume index {me.CostumeIndex} after set 0");
        }

        private static void AchievementProbe(Player me)
        {
            var manager = me.AchievementManager;
            // achievements that carry a title come first, so title grants through achievements get exercised
            foreach (var entry in GameTableManager.Instance.Achievement.Entries.OrderByDescending(e => e != null && e.CharacterTitleId != 0u))
            {
                if (entry == null || entry.Id == 0u || entry.Id > ushort.MaxValue)
                    continue;
                // guild achievements belong to the guild's manager, a character cannot hold them
                if (Game.Achievement.GlobalAchievementManager.Instance.GetAchievement((ushort)entry.Id) is not { IsPlayerAchievement: true }
                    || manager.HasCompletedAchievement((ushort)entry.Id))
                    continue;

                uint points = manager.AchievementPoints;
                manager.GrantAchievement((ushort)entry.Id);
                Expect(manager.HasCompletedAchievement((ushort)entry.Id), $"achievement {entry.Id} not complete after grant");
                Expect(manager.AchievementPoints >= points, "achievement points fell");
                if (entry.CharacterTitleId != 0u)
                {
                    Expect(me.TitleManager.Any(t => t.Entry.Id == entry.CharacterTitleId), $"title {entry.CharacterTitleId} not granted by achievement {entry.Id}");
                    FailureLedger.Record("system-ok", "achievement-title");
                }
                return;
            }

            throw new ProbeSkip();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, AmpProbeState> ampStates = new();

        private sealed class AmpProbeState
        {
            public ushort AmpId;
            public uint Spell4Id;
            public byte Points;
            public int Step;
        }

        // through the real handlers: an AMP of another class is refused, a valid one is added and its passive applied on the next update,
        // the respec refunds the power and removes the passive
        private static void AmpProbe(Player me)
        {
            byte index = me.SpellManager.ActiveActionSet;
            var set = me.SpellManager.GetActionSet(index);
            Expect(set != null, "no active action set");

            if (ampStates.TryGetValue(me, out AmpProbeState state))
            {
                if (state.Step == 1)
                {
                    Expect(me.SpellManager.IsAmpSpellApplied(state.Spell4Id), $"AMP {state.AmpId} passive {state.Spell4Id} not applied");
                    bool lasting = GameTableManager.Instance.Spell4Effects.Entries.Any(e => e != null && e.SpellId == state.Spell4Id && e.TargetFlags == 1u
                        && (e.EffectType == (uint)Game.Spell.Static.SpellEffectType.Proc || e.EffectType == (uint)Game.Spell.Static.SpellEffectType.UnitPropertyModifier));
                    if (lasting)
                        Expect(me.HasEffectsOf(state.Spell4Id), $"AMP {state.AmpId} passive {state.Spell4Id} has no lasting effect on the player");
                    if (set.GetAmp(state.AmpId) == null)
                        throw new ProbeSkip();
                    // BotAmps may have spent points in between: the respec must give back at least the AMP's cost
                    byte before = set.AmpPoints;
                    uint cost = GameTableManager.Instance.EldanAugmentation.GetEntry(state.AmpId)?.PowerCost ?? 0u;
                    SpellHandler.HandleRequestAmpReset(me.Session, BotAmps.ResetMessage(index, Network.Message.Static.AmpRespecType.Single, state.AmpId));
                    Expect(set.GetAmp(state.AmpId) == null && set.AmpPoints >= before + cost, $"AMP not refunded on respec ({before} -> {set.AmpPoints}, cost {cost})");
                    state.Step = 2;
                    RunNext(me, "amp");
                    return;
                }

                ampStates.Remove(me);
                // the bot's own AMP spending (BotAmps) may have picked the AMP again in between
                if (set.GetAmp(state.AmpId) != null)
                    throw new ProbeSkip();
                Expect(!me.SpellManager.IsAmpSpellApplied(state.Spell4Id) && !me.HasEffectsOf(state.Spell4Id), $"AMP {state.AmpId} passive still on the player after respec");
                FailureLedger.Record("system-ok", "amp-passive");
                return;
            }

            if (me.Level < Game.Spell.ActionSet.AmpUnlockLevel || me.IsDead)
                throw new ProbeSkip();

            var other = GameTableManager.Instance.EldanAugmentation.Entries
                .FirstOrDefault(e => e != null && e.Id != 0u && e.Id <= ushort.MaxValue && e.ClassId != (uint)me.Class && e.ClassId != 0u && e.CategoryTier == 0u);
            if (other != null)
            {
                SpellHandler.HandleRequestActionSetChanges(me.Session, BotAmps.AddAmpMessage(index, (ushort)other.Id));
                Expect(set.GetAmp((ushort)other.Id) == null, $"AMP {other.Id} of class {other.ClassId} accepted for class {me.Class}");
            }

            var entry = GameTableManager.Instance.EldanAugmentation.Entries
                .FirstOrDefault(e => e != null && e.Id != 0u && e.Id <= ushort.MaxValue && e.Spell4IdAugment != 0u
                    && GameTableManager.Instance.Spell4.GetEntry(e.Spell4IdAugment) != null && set.CanAddAmp(e) == null);
            if (entry == null)
            {
                // the bot has spent its power (BotAmps): a full respec through the message refunds everything
                if (!set.Amps.Any())
                    throw new ProbeSkip();
                SpellHandler.HandleRequestAmpReset(me.Session, BotAmps.ResetMessage(index, Network.Message.Static.AmpRespecType.Full, 0u));
                Expect(!set.Amps.Any() && set.AmpPoints == set.AmpPowerTotal, "full AMP respec did not refund every point");
                RunNext(me, "amp");
                return;
            }

            byte points = set.AmpPoints;
            SpellHandler.HandleRequestActionSetChanges(me.Session, BotAmps.AddAmpMessage(index, (ushort)entry.Id));
            Expect(set.GetAmp((ushort)entry.Id) != null, "amp not present after the add message");
            Expect(set.AmpPoints == points - entry.PowerCost, "amp points not spent");
            ampStates.Add(me, new AmpProbeState { AmpId = (ushort)entry.Id, Spell4Id = entry.Spell4IdAugment, Points = points, Step = 1 });
            RunNext(me, "amp");
        }

        // rune slots through the client messages: a basic rune of the slot's element is created in the bags, installed into an empty slot of an
        // equipped item (consumed, listed as installed), an occupied slot refuses a second rune, the slot is cleared, rerolled and one slot added
        private static void RuneProbe(Player me)
        {
            if (me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) < 2u)
                throw new ProbeSkip();

            Game.Entity.Item gear = me.Inventory.GetItems(InventoryLocation.Equipped).FirstOrDefault(i => me.RuneManager.Get(i)?.Runes.Contains(0u) == true);
            if (gear == null)
                throw new ProbeSkip();

            var slots = me.RuneManager.Get(gear);
            int slot = slots.Runes.IndexOf(0u);
            uint element = slots.Types[slot];
            var text = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
            var rune = GameTableManager.Instance.Item.Entries
                .Where(e => e != null && e.Id != 0u && Game.Entity.RuneManager.RuneElement(e) == element && e.RequiredLevel <= me.Level
                    && (text?.GetEntry(GameTableManager.Instance.Item2Type.GetEntry(e.Item2TypeId)?.LocalizedTextId ?? 0u) ?? "").EndsWith("Basic"))
                .OrderByDescending(e => e.RequiredLevel).FirstOrDefault();
            Expect(rune != null, $"no basic rune of element {element} for level {me.Level}");

            me.Inventory.ItemCreate(InventoryLocation.Inventory, rune.Id, 2u, ItemUpdateReason.Script);
            uint before = me.Inventory.GetItemCount(rune.Id);
            Expect(before >= 1u, "rune not created in the bags");
            ClientCraftingRuneInstall install(uint id) => BotPackets.Build<ClientCraftingRuneInstall>(w =>
            {
                w.Write(gear.Guid);
                w.Write((uint)slots.Runes.Count);
                for (int i = 0; i < slots.Runes.Count; i++)
                    w.Write(i == slot ? id : 0u);
            });

            RuneHandler.HandleRuneInstall(me.Session, install(rune.Id));
            Expect(slots.Runes[slot] == rune.Id, $"rune {rune.Id} (element {element}) not installed in slot {slot}");
            Expect(me.Inventory.GetItemCount(rune.Id) == before - 1u, "rune not taken from the bags");
            Expect(me.RuneManager.Installed(gear).Any(e => e.Id == rune.Id), "installed rune not listed for the stats");

            RuneHandler.HandleRuneInstall(me.Session, install(rune.Id));
            Expect(me.Inventory.GetItemCount(rune.Id) == before - 1u, "occupied slot took a second rune");

            RuneHandler.HandleRuneSlotClear(me.Session, BotPackets.Build<ClientCraftingRuneSlotClear>(w => { w.Write(gear.Guid); w.Write((uint)slot); w.Write(false); w.Write(false); }));
            Expect(slots.Runes[slot] == 0u, "rune slot not cleared");

            RuneHandler.HandleRuneSlotReroll(me.Session, BotPackets.Build<ClientCraftingRuneSlotReroll>(w => { w.Write(gear.Guid); w.Write((uint)slot); w.Write(Game.Entity.RuneManager.Fusion, 5u); }));
            Expect(slots.Types[slot] == Game.Entity.RuneManager.Fusion, "reroll did not set the element");

            int count = slots.Types.Count;
            RuneHandler.HandleRuneSlotAdd(me.Session, BotPackets.Build<ClientCraftingRuneSlotAdd>(w => { w.Write(gear.Guid); w.Write(true); w.Write(Game.Entity.RuneManager.Air, 5u); }));
            Expect(slots.Types.Count == Math.Min(count + 1, (int)me.RuneManager.MaxSlots(gear)), "add slot did not respect the quality's maximum");
            me.Inventory.ItemDelete(rune.Id, me.Inventory.GetItemCount(rune.Id));
        }

        // riding itself would pull bots out of their quest routes, so this builds the Mount entity from the table data only
        private static void MountProbe(Player me)
        {
            var effect = GameTableManager.Instance.Spell4Effects.Entries
                .FirstOrDefault(e => e != null && e.EffectType == (uint)Game.Spell.Static.SpellEffectType.SummonMount);
            Expect(effect != null, "no SummonMount effect in Spell4Effects");
            var mount = new Game.Entity.Mount(me, effect.SpellId, effect.DataBits00, effect.DataBits01, effect.DataBits04);
            Expect(mount.CreatureEntry != null && mount.VehicleEntry != null, $"mount creature {effect.DataBits00} / vehicle {effect.DataBits01} missing in tables");
            // teleporting or logging out bots cannot mount right now
            if (!me.CanMount())
                throw new ProbeSkip();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, MailState> rideStates = new();

        // casts a real SummonMount spell on the bot, a later probe turn checks the bot is a passenger of the mount entity and dismounts it
        private static void MountRideProbe(Player me)
        {
            if (rideStates.TryGetValue(me, out MailState state))
            {
                // a bot's own spell casts dismount it again, so the stamp of the passenger add counts as the proof
                if (me.VehicleGuid != 0u || me.LastMountedAt >= state.Sent)
                {
                    me.Dismount();
                    rideStates.Remove(me);
                    Expect(me.VehicleGuid == 0u, "still on the mount after Dismount");
                    return;
                }

                if (DateTime.UtcNow - state.Sent < TimeSpan.FromSeconds(30))
                    throw new ProbeSkip();

                rideStates.Remove(me);
                throw new ProbeFailure($"mount spell {state.Spell} ({GameTableManager.Instance.Spell4.GetEntry(state.Spell)?.Description}) cast but the bot never became a passenger in 30 s");
            }

            if (me.Map == null || me.IsDead || !me.CanMount())
                throw new ProbeSkip();

            var effects = GameTableManager.Instance.Spell4Effects.Entries
                .Where(e => e != null && e.EffectType == (uint)Game.Spell.Static.SpellEffectType.SummonMount
                    && GameTableManager.Instance.Creature2.GetEntry(e.DataBits00) != null && GameTableManager.Instance.UnitVehicle.GetEntry(e.DataBits01) != null)
                .Where(e =>
                {
                    var spell4 = GameTableManager.Instance.Spell4.GetEntry(e.SpellId);
                    string description = spell4?.Description ?? "";
                    // mounts whose caster prerequisite (riding skill, faction, level) the bot fails would only fail the cast
                    if (spell4 != null && spell4.PrerequisiteIdCasterCast != 0u
                        && !Game.Prerequisite.PrerequisiteManager.Instance.Meets(me, spell4.PrerequisiteIdCasterCast, true))
                        return false;
                    return description.Length > 0 && !description.Contains("Delete", StringComparison.OrdinalIgnoreCase) && !description.Contains("DEPRECATED", StringComparison.OrdinalIgnoreCase)
                        && !description.Contains("test", StringComparison.OrdinalIgnoreCase)
                        // boarding spells put the bot into somebody else's vehicle, there is no passenger stamp for a lone bot
                        && !description.Contains("Board", StringComparison.OrdinalIgnoreCase);
                }).ToList();
            if (effects.Count == 0)
                throw new ProbeSkip();
            var effect = effects[BotManager.Rng.Next(effects.Count)];
            rideStates.Add(me, new MailState { Sent = DateTime.UtcNow, Spell = effect.SpellId });
            me.CastSpell(effect.SpellId, new Game.Spell.SpellParameters { UserInitiatedSpellCast = false });
            throw new ProbeSkip();
        }

        // kills the bot (past its immortality), asks for a resurrection through the real handler: the first is granted with the rule's health,
        // the next within the Wake Here cooldown must be refused (the death timer takes over, simulated by Resurrect())
        private static void RezProbe(Player me)
        {
            if (me.Map == null || me.IsDead || me.MaxHealth == 0u || !Game.Combat.CombatConfig.Enabled)
                throw new ProbeSkip();

            bool cooldown = DateTime.UtcNow < me.WakeHereReadyAt;
            me.ProbeDeathCause = "resurrection probe";
            me.Kill(null);
            Expect(me.IsDead, "bot not dead after Kill");

            Network.Message.Handler.ResurrectionHandler.HandleResurrectRequest(me.Session, BotPackets.Build<Network.Message.Model.ClientResurrectRequest>(w => { }));
            if (cooldown)
            {
                Expect(me.IsDead, "wake here granted during its cooldown");
                me.Resurrect();
            }
            else
            {
                Expect(!me.IsDead, "wake here refused without a cooldown");
                Expect(DateTime.UtcNow < me.WakeHereReadyAt, "wake here cooldown not started");
            }

            Expect(!me.IsDead && me.Health >= (uint)(me.MaxHealth * Game.Combat.CombatConfig.ResurrectHealth) - 1u, $"health after rez {me.Health}/{me.MaxHealth}");
        }

        private static void RezAccept(Player me, Game.Combat.ResurrectionType type)
        {
            Network.Message.Handler.ResurrectionHandler.HandleResurrectAccept(me.Session, BotPackets.Build<Network.Message.Model.ClientResurrectAccept>(w =>
            {
                w.Write(me.Guid);
                w.Write((uint)type, 7u);
                w.Write(0u);
            }));
        }

        // death screen options through ClientResurrectAccept: a visible living player casts "Second Life" (spell 1850, Resurrect effect 20.0)
        // on the dead bot, the offer must show and be taken with health 20 %; then a second death is released to the holocrypt (nearest bind
        // point) when the world has one, else the caster option without an offer must be refused
        private static void RezOptionsProbe(Player me)
        {
            if (me.Map == null || me.IsDead || me.MaxHealth == 0u || !Game.Combat.CombatConfig.Enabled || me.Map is Game.Map.DungeonMapInstance)
                throw new ProbeSkip();

            me.Map.Search(me.Position, 25f, new Game.Map.Search.SearchCheckRangePlayerOnly(me.Position, 25f, me), out List<GridEntity> near);
            Player caster = near.OfType<Player>().FirstOrDefault(p => p != me && !p.IsDead && p.Faction1 == me.Faction1 && p.GetVisible<Game.Entity.WorldEntity>(me.Guid) != null);

            me.ProbeDeathCause = "resurrection probe";
            me.Kill(null);
            Expect(me.IsDead, "bot not dead after Kill");
            Expect((me.ResurrectionOptions & Game.Combat.ResurrectionType.WakeHere) != 0, "Wake Here not offered");

            if (caster != null)
            {
                caster.CastSpell(1850u, new Game.Spell.SpellParameters { UserInitiatedSpellCast = false, PrimaryTargetId = me.Guid });
                if (me.HasCasterRezOffer)
                {
                    Expect((me.ResurrectionOptions & Game.Combat.ResurrectionType.SpellCasterLocation) != 0, "caster option not in the death screen flags");
                    RezAccept(me, Game.Combat.ResurrectionType.SpellCasterLocation);
                    Expect(!me.IsDead, "caster resurrection not taken");
                    Expect(System.Math.Abs(me.Health - me.MaxHealth * 0.2f) <= me.MaxHealth * 0.02f + 1f, $"health after Second Life {me.Health}/{me.MaxHealth}, expected 20 %");
                    FailureLedger.Record("system-ok", "rez-caster");
                    me.ProbeDeathCause = "resurrection probe";
                    me.Kill(null);
                }
            }

            RezAccept(me, Game.Combat.ResurrectionType.SpellCasterLocation);
            Expect(me.IsDead, "caster resurrection taken without an offer");
            RezAccept(me, Game.Combat.ResurrectionType.ExitInstance);
            Expect(me.IsDead, "exit instance taken in the open world");
            if ((me.ResurrectionOptions & Game.Combat.ResurrectionType.Holocrypt) != 0)
            {
                RezAccept(me, Game.Combat.ResurrectionType.Holocrypt);
                Expect(!me.IsDead, "holocrypt release refused although offered");
                FailureLedger.Record("system-ok", "rez-holocrypt");
            }
            else
            {
                RezAccept(me, Game.Combat.ResurrectionType.Holocrypt);
                Expect(me.IsDead, "holocrypt taken in a world without a bind point");
            }
            me.Resurrect();
        }

        // takes and finishes a daily quest: the reset time must be the next daily reset, the quest must not be offered again until it passes
        private static void QuestDailyProbe(Player me)
        {
            if (me.Map == null || me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) < 4u)
                throw new ProbeSkip();

            var daily = Game.Quest.GlobalQuestManager.Instance.GetAllQuestInfo()
                .Where(q => q.Entry.QuestRepeatPeriodEnum == 1u && q.Entry.PushedItemIds.All(i => i == 0u) && me.QuestManager.CanAcceptWithoutGiver(q))
                .Take(40).ToList();
            if (daily.Count == 0)
                throw new ProbeSkip();

            var info = daily[BotManager.Rng.Next(daily.Count)];
            ushort id = (ushort)info.Entry.Id;
            me.QuestManager.QuestAdd(info);
            // a daily without objectives is achieved as soon as it is taken
            if (me.QuestManager.GetQuestState(id) == Game.Quest.Static.QuestState.Accepted)
                me.QuestManager.QuestAchieve(id);
            if (me.QuestManager.GetQuestState(id) != Game.Quest.Static.QuestState.Achieved)
            {
                me.QuestManager.QuestAbandon(id);
                throw new ProbeSkip();
            }

            me.QuestManager.QuestComplete(id, 0, false, true);
            Expect(me.QuestManager.GetQuestState(id) == Game.Quest.Static.QuestState.Completed, "daily quest not completed");

            DateTime? reset = me.QuestManager.GetQuestReset(id);
            Expect(reset.HasValue && reset.Value == Game.Quest.GlobalQuestManager.Instance.NextDailyReset, "daily reset time is not the next daily reset");
            Expect(reset.Value > DateTime.UtcNow && reset.Value <= DateTime.UtcNow.AddDays(1d), "daily reset not within 24 h");
            Expect(!me.QuestManager.CanAcceptWithoutGiver(info), "daily offered again before its reset");

            me.QuestManager.SetQuestReset(id, DateTime.UtcNow.AddMinutes(-1d));
            Expect(me.QuestManager.CanAcceptWithoutGiver(info), "daily not offered after its reset time passed");
        }

        // drives one open kill / use / spell mission of the bot's path in its zone through the manager hooks (the same entry points real kills and uses call);
        // with nothing open the bot switches to another path so every path gets exercised
        private static void PathMissionProbe(Player me)
        {
            if (me.Map == null || me.Zone == null)
                throw new ProbeSkip();

            var open = me.PathMissionManager.GetAvailable()
                .Where(d => d.Kind is Game.Entity.PathMissionManager.MissionKind.Kill or Game.Entity.PathMissionManager.MissionKind.Activate or Game.Entity.PathMissionManager.MissionKind.Spell
                    && !me.PathMissionManager.IsCompleted(d.Entry.Id)).ToList();
            if (open.Count == 0)
            {
                var next = (Path)((int)(me.Path + 1) % 4);
                if (me.PathManager.IsPathUnlocked(next) && next != me.Path)
                    me.PathManager.ActivatePath(next);
                throw new ProbeSkip();
            }

            var mission = open[BotManager.Rng.Next(open.Count)];
            var sent = new List<NexusForever.Shared.Network.Message.IWritable>();
            void capture(NexusForever.Shared.Network.Message.IWritable m) { lock (sent) sent.Add(m); }
            var session = me.Session as BotSession;
            if (session != null)
                session.ServerMessage += capture;
            try
            {
                me.PathMissionManager.DriveMission(mission);
            }
            finally
            {
                if (session != null)
                    session.ServerMessage -= capture;
            }
            Expect(me.PathMissionManager.IsCompleted(mission.Entry.Id), $"mission {mission.Entry.Id} ({mission.Kind}) not complete after its creatures were fed");
            if (session != null)
                lock (sent)
                {
                    // the client is told: ServerPathMissionUpdate with Completed for the mission (0x06BB)
                    Expect(sent.OfType<ServerPathMissionUpdate>().Any(u => u.MissionId == mission.Entry.Id && u.Completed), $"no completed ServerPathMissionUpdate for mission {mission.Entry.Id}");
                    FailureLedger.Record("system-ok", "path-mission-packets");
                }
        }

        private sealed class MailP2PState
        {
            public int Phase;
            public Player Partner;
            public uint MyBox, TheirBox, Before;
            public ulong ItemGuid;
            public DateTime Since = DateTime.UtcNow;
            public DateTime LastSave = DateTime.MinValue;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, MailP2PState> mailP2P = new();

        private static uint SpawnMailbox(Player at)
        {
            var creature = GameTableManager.Instance.Creature2.Entries.FirstOrDefault(c => c != null && c.Id != 0u && (c.Description ?? "").Contains("mailbox", StringComparison.OrdinalIgnoreCase));
            var box = new Game.Entity.Mailbox();
            box.Initialise(new NexusForever.Database.World.Model.EntityModel { Creature = creature?.Id ?? 0u, Type = (byte)Game.Entity.Static.EntityType.MailBox });
            at.Map.EnqueueAdd(box, new Game.Map.MapPosition { Position = at.Position });
            return 0u;
        }

        // player to player mail through the real handler path in phases (each probe turn does one): mailboxes next to both bots, then a cash on delivery mail with an item,
        // the partner pays and takes the item, the attachment is refused before the cash is paid
        private static void MailP2PProbe(Player me)
        {
            if (!mailP2P.TryGetValue(me, out MailP2PState st))
            {
                Player other = Partner(me) ?? throw new ProbeSkip();
                if (me.Map == null || other.Map == null)
                    throw new ProbeSkip();

                st = new MailP2PState { Partner = other };
                mailP2P.Add(me, st);
                SpawnMailbox(me);
                SpawnMailbox(other);
                throw new ProbeSkip();
            }

            Player partner = st.Partner;
            if (DateTime.UtcNow - st.Since > TimeSpan.FromSeconds(300) || partner.Map == null || me.Map == null)
            {
                mailP2P.Remove(me);
                if (st.Phase >= 1)
                    throw new ProbeFailure($"mail p2p stuck in phase {st.Phase} (outgoing {me.MailManager.OutgoingCount}, partner mail {partner.MailManager.MailCount} before {st.Before}, partner session {(partner.Session != null)}, canProcess {me.Session.CanProcessPackets})");
                throw new ProbeSkip();
            }

            if (st.Phase == 0)
            {
                // wait until the mailbox is visible to both
                Game.Entity.Mailbox boxMe = FindMailbox(me), boxThem = FindMailbox(partner);
                if (boxMe == null || boxThem == null)
                    throw new ProbeSkip();

                EnsureRoom(me, 2u);
                EnsureRoom(partner, 2u);
                uint itemId = TestItemId();
                me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 1u);
                var item = me.Inventory.GetItems(InventoryLocation.Inventory).First(i => i.Id == itemId);
                st.MyBox = boxMe.Guid;
                st.TheirBox = boxThem.Guid;
                st.ItemGuid = item.Guid;
                st.Before = (uint)partner.MailManager.MailCount;
                me.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 1000ul);
                var send = BotPackets.Build<Network.Message.Model.ClientMailSend>(w =>
                {
                    w.WriteStringWide(partner.Name);
                    w.WriteStringWide("");
                    w.WriteStringWide("probe");
                    w.WriteStringWide("probe cod");
                    w.Write(0ul);
                    w.Write(10ul);
                    w.Write(Game.Mail.Static.DeliveryTime.Instant, 2u);
                    w.Write(boxMe.Guid);
                    w.Write(item.Guid);
                    for (int i = 1; i < 10; i++)
                        w.Write(0ul);
                });
                me.MailManager.SendMail(send);
                // outgoing mail is flushed by the sender's character save: force one instead of waiting up to 60 s (the result event runs after the name lookup, so save later too)
                st.Phase = 1;
                st.Since = DateTime.UtcNow;
                throw new ProbeSkip();
            }

            if (st.Phase == 1)
            {
                if ((uint)partner.MailManager.MailCount <= st.Before)
                {
                    if (DateTime.UtcNow - st.LastSave > TimeSpan.FromSeconds(20) && me.Session.CanProcessPackets)
                    {
                        st.LastSave = DateTime.UtcNow;
                        me.Save();
                    }
                    throw new ProbeSkip();
                }

                ulong id = partner.MailManager.MailIds.Last();
                partner.MailManager.MailMarkAsRead(id);
                uint itemId = TestItemId();
                uint have = partner.Inventory.GetItemCount(itemId);

                // the partner walks around, a refusal for distance says nothing about the cash on delivery rule: try again later
                // (79 of 84 runs in the 2026-10-07 soak timed out here): use a mailbox near the partner, or put one there
                if (!partner.MailManager.IsTargetMailBoxInRange(st.TheirBox))
                {
                    Game.Entity.Mailbox near = FindMailbox(partner);
                    if (near != null && partner.MailManager.IsTargetMailBoxInRange(near.Guid))
                        st.TheirBox = near.Guid;
                    else
                    {
                        if (DateTime.UtcNow - st.LastSave > TimeSpan.FromSeconds(20))
                        {
                            st.LastSave = DateTime.UtcNow;
                            SpawnMailbox(partner);
                        }
                        throw new ProbeSkip();
                    }
                }

                // a full bag refuses the attachment for a reason that has nothing to do with the cash on delivery rule
                if (partner.Inventory.IsInventoryFull(Game.Entity.Static.InventoryLocation.Inventory))
                    throw new ProbeSkip();

                int refused() => FailureLedger.Snapshot().Where(e => e.Category == "mail-cod-unpaid-take").Sum(e => e.Count);
                int before = refused();
                partner.MailManager.MailTakeAttachment(id, 0u, st.TheirBox);
                Expect(partner.Inventory.GetItemCount(itemId) == have, "attachment taken before the cash on delivery was paid");
                Expect(refused() > before, "unpaid cash on delivery was not recorded as refused");

                partner.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 100ul);
                partner.MailManager.MailPayCod(id);
                st.Phase = 2;
                st.Before = have;
                st.Since = DateTime.UtcNow;
                throw new ProbeSkip();
            }

            if (st.Phase == 2)
            {
                // the partner walks away from the mailbox or fills its bag between turns: retry the take until it works, fail only after 2 minutes
                ulong id = partner.MailManager.MailIds.Last();
                uint itemId = TestItemId();
                if (partner.Inventory.GetItemCount(itemId) < st.Before + 1u)
                {
                    if (!partner.MailManager.IsTargetMailBoxInRange(st.TheirBox) && FindMailbox(partner) is Game.Entity.Mailbox near)
                        st.TheirBox = near.Guid;
                    else if (!partner.MailManager.IsTargetMailBoxInRange(st.TheirBox) && DateTime.UtcNow - st.LastSave > TimeSpan.FromSeconds(20))
                    {
                        st.LastSave = DateTime.UtcNow;
                        SpawnMailbox(partner);
                    }
                    if (partner.MailManager.IsTargetMailBoxInRange(st.TheirBox) && !partner.Inventory.IsInventoryFull(Game.Entity.Static.InventoryLocation.Inventory))
                        partner.MailManager.MailTakeAttachment(id, 0u, st.TheirBox);
                    if (partner.Inventory.GetItemCount(itemId) < st.Before + 1u)
                    {
                        if (DateTime.UtcNow - st.Since < TimeSpan.FromMinutes(2))
                            throw new ProbeSkip();
                        mailP2P.Remove(me);
                        throw new ProbeFailure("attachment not received after paying the cash on delivery");
                    }
                }
                partner.MailManager.MailDelete(id);
                partner.Inventory.ItemDelete(itemId, 1u);
                mailP2P.Remove(me);
                return;
            }
        }

        private static Game.Entity.Mailbox FindMailbox(Player p)
        {
            p.Map.Search(p.Position, 20f, new Game.Map.Search.SearchCheckRange(p.Position, 20f, p), out List<GridEntity> around);
            return around.OfType<Game.Entity.Mailbox>().FirstOrDefault();
        }

        // every bot queues for the same dungeon with a role its class can fill; whoever completes a 1 tank / 1 healer / 3 damage set forms the group (checked, then disbanded)
        private static void GroupFinderProbe(Player me)
        {
            if (me.Map == null)
                throw new ProbeSkip();

            var finder = Game.Groups.GroupFinder.Instance;
            if (finder.IsQueued(me) || Game.Groups.GroupManager.Instance.GetGroup(me.CharacterId) != null)
                throw new ProbeSkip();

            var available = finder.AvailableDungeons(me);
            if (available.Count == 0 && me.Level < 50u)
            {
                // dungeons start at level 10+: a low system bot is raised to 50 once (as the contract probe does), so the finder has dungeons to match
                me.XpManager.SetLevel(50);
                FailureLedger.Record("groupfinder-probe-level", "system bot raised to level 50 for the group finder");
                throw new ProbeSkip();
            }
            Expect(available.Count > 0, $"no dungeon is available to a level {me.Level} bot in the matching tables");
            ushort world = available[0];
            var role = Game.Groups.GroupFinder.Role.Damage;
            double roll = BotManager.Rng.NextDouble();
            if (Game.Groups.GroupFinder.CanFill(me.Class, Game.Groups.GroupFinder.Role.Tank) && roll < 0.4)
                role = Game.Groups.GroupFinder.Role.Tank;
            else if (Game.Groups.GroupFinder.CanFill(me.Class, Game.Groups.GroupFinder.Role.Healer) && roll < 0.6)
                role = Game.Groups.GroupFinder.Role.Healer;

            Expect(finder.Enqueue(me, role, world), "queue refused a bot that is ungrouped and can fill the role");
            var group = finder.TryMatch(world);
            if (group == null)
                throw new ProbeSkip();

            Expect(group.Members.Count == 5, $"group of {group.Members.Count}");
            var members = group.Members.Select(m => Game.CharacterCache.CharacterManager.Instance.GetPlayer(m.CharacterId)).ToList();
            Expect(members.All(m => m != null), "a group member is not online");
            Expect(members.Count(m => Game.Groups.GroupFinder.CanFill(m.Class, Game.Groups.GroupFinder.Role.Tank)) >= 1, "no tank-capable member");
            Expect(members.Count(m => Game.Groups.GroupFinder.CanFill(m.Class, Game.Groups.GroupFinder.Role.Healer)) >= 1, "no healer-capable member");
            Expect(members.All(m => m.Faction1 == members[0].Faction1), "mixed factions in a finder group");

            var leader = Game.CharacterCache.CharacterManager.Instance.GetPlayer(group.LeaderId);
            Game.Groups.GroupManager.Instance.Leave(leader ?? members[0], group.Id, true);
            Expect(Game.Groups.GroupManager.Instance.GetGroup(members[0].CharacterId) == null, "group not disbanded after the check");
        }

        // two bots through the real group manager: invite, accept, promote the invitee, kick, leave
        private static void GroupProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            var groups = Game.Groups.GroupManager.Instance;
            if (groups.GetGroup(me.CharacterId) != null || groups.GetGroup(other.CharacterId) != null || me.Faction1 != other.Faction1)
                throw new ProbeSkip();

            var result = groups.Invite(me, other.Name);
            Expect(result == Game.Groups.Static.GroupInviteResult.Sent, $"invite result {result}");
            var group = groups.GetGroup(me.CharacterId);
            Expect(group != null, "inviter has no group after inviting");
            groups.InviteResponse(other, group.Id, true);
            Expect(groups.GetGroup(other.CharacterId)?.Id == group.Id, "invitee not in the group after accepting");
            groups.Promote(me, group.Id, new Network.Message.Model.Shared.Identity { RealmId = WorldServer.RealmId, Id = other.CharacterId });
            Expect(groups.GetGroup(me.CharacterId)?.LeaderId == other.CharacterId, "promote did not change the leader");
            groups.Kick(other, group.Id, new Network.Message.Model.Shared.Identity { RealmId = WorldServer.RealmId, Id = me.CharacterId });
            Expect(groups.GetGroup(me.CharacterId) == null, "kicked member still grouped");
            groups.Leave(other, group.Id, true);
        }

        // group loot rules through the client message and its handler: the leader sets Master/RoundRobin/Excellent/RoundRobin,
        // a non-leader is refused, and the group loot choice follows the new rule
        private static void LootRulesProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            var groups = Game.Groups.GroupManager.Instance;
            if (groups.GetGroup(me.CharacterId) != null || groups.GetGroup(other.CharacterId) != null || me.Faction1 != other.Faction1)
                throw new ProbeSkip();

            Expect(groups.Invite(me, other.Name) == Game.Groups.Static.GroupInviteResult.Sent, "invite not sent");
            var group = groups.GetGroup(me.CharacterId);
            Expect(group != null, "inviter has no group after inviting");
            try
            {
                groups.InviteResponse(other, group.Id, true);
                Expect(groups.GetGroup(other.CharacterId)?.Id == group.Id, "invitee not in the group after accepting");

                Network.Message.Model.ClientGroupLootRulesChange change(Game.Groups.Static.LootRule normal, Game.Groups.Static.LootRule threshold,
                    Game.Groups.Static.LootThreshold quality, Game.Groups.Static.HarvestLootRule harvest)
                    => BotPackets.Build<Network.Message.Model.ClientGroupLootRulesChange>(w =>
                    {
                        w.Write(group.Id);
                        w.Write(normal, 3u);
                        w.Write(threshold, 3u);
                        w.Write(quality, 4u);
                        w.Write(harvest, 2u);
                    });

                Network.Message.Handler.GroupHandler.HandleGroupLootRulesChange(other.Session, change(Game.Groups.Static.LootRule.FreeForAll,
                    Game.Groups.Static.LootRule.FreeForAll, Game.Groups.Static.LootThreshold.Artifact, Game.Groups.Static.HarvestLootRule.RoundRobin));
                Expect(group.NormalRule == Game.Groups.Static.LootRule.NeedBeforeGreed, "non-leader changed the loot rules");

                Network.Message.Handler.GroupHandler.HandleGroupLootRulesChange(me.Session, change(Game.Groups.Static.LootRule.Master,
                    Game.Groups.Static.LootRule.RoundRobin, Game.Groups.Static.LootThreshold.Excellent, Game.Groups.Static.HarvestLootRule.RoundRobin));
                Expect(group.NormalRule == Game.Groups.Static.LootRule.Master && group.ThresholdRule == Game.Groups.Static.LootRule.RoundRobin
                    && group.ThresholdQuality == Game.Groups.Static.LootThreshold.Excellent && group.HarvestRule == Game.Groups.Static.HarvestLootRule.RoundRobin,
                    $"leader change not applied: {group.NormalRule}/{group.ThresholdRule}/{group.ThresholdQuality}/{group.HarvestRule}");

                // below the threshold the normal rule (Master) gives the item to the leader
                var item = GameTableManager.Instance.Item.Entries.FirstOrDefault(i => i.ItemQualityId == 2u && i.ClassRequired == 0u);
                var info = item == null ? null : Game.ItemManager.Instance.GetItemInfo(item.Id);
                if (info != null && me.Map == other.Map && me.Inventory.HasFreeSlot() && other.Inventory.HasFreeSlot())
                    Expect(Game.Groups.GroupLoot.Choose(other, info) == me, "master loot below the threshold did not go to the leader");
            }
            finally
            {
                if (groups.GetGroupById(group.Id) != null)
                    groups.Leave(groups.GetGroupById(group.Id).LeaderId == me.CharacterId ? me : other, group.Id, true);
            }
        }

        // custom chat channel through the client messages: join (creates), partner joins, list members, post, both leave
        private static void ChatChannelProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            const string name = "Nfbotprobe";
            var chat = Game.Social.GlobalChatManager.Instance;

            void join(Player p) => Network.Message.Handler.SocialHandler.HandleChatJoin(p.Session, BotPackets.Build<Network.Message.Model.ClientChatJoin>(w =>
            {
                w.Write(Game.Social.Static.ChatChannelType.Custom, 14u);
                w.WriteStringWide(name);
                w.WriteStringWide("");
                w.Write(0u);
            }));
            void channelMessage<T>(Player p, ulong id, Action<T> handle) where T : NexusForever.Shared.Network.Message.IReadable, new()
                => handle(BotPackets.Build<T>(w =>
                {
                    w.Write(Game.Social.Static.ChatChannelType.Custom, 14u);
                    w.Write(id);
                }));

            join(me);
            var channel = chat.GetChatChannel(Game.Social.Static.ChatChannelType.Custom, name);
            Expect(channel != null && channel.IsMember(me.CharacterId), "join did not create the channel or add the member");
            join(other);
            Expect(channel.IsMember(other.CharacterId), "second member not added");

            channelMessage<Network.Message.Model.ClientChatList>(me, channel.Id, m => Network.Message.Handler.SocialHandler.HandleChatList(me.Session, m));
            Network.Message.Handler.SocialHandler.HandleChat(me.Session, BotPackets.Build<Network.Message.Model.ClientChat>(w =>
            {
                w.Write(Game.Social.Static.ChatChannelType.Custom, 14u);
                w.Write(channel.Id);
                w.WriteStringWide("probe");
                w.Write(0, 5u);
                w.Write((ushort)0);
            }));

            channelMessage<Network.Message.Model.ClientChatLeave>(other, channel.Id, m => Network.Message.Handler.SocialHandler.HandleChatLeave(other.Session, m));
            channelMessage<Network.Message.Model.ClientChatLeave>(me, channel.Id, m => Network.Message.Handler.SocialHandler.HandleChatLeave(me.Session, m));
            Expect(!channel.IsMember(me.CharacterId) && !channel.IsMember(other.CharacterId), "leave did not remove the members");
        }

        // item imbuements: equipping an item with an imbuement offers the first unfinished quest of its chain, the quest completes without a
        // receiver (Game/Quest/ItemImbuements). The objectives are achieved directly here: this probe tests the start/finish path, not the objectives.
        private static void ImbuementProbe(Player me)
        {
            var candidates = Game.Quest.ItemImbuements.ImbuedItems().Where(e => e.RequiredLevel <= me.Level
                    && (e.ClassRequired == 0u || e.ClassRequired == (uint)me.Class))
                .Select(e => (Entry: e, Quest: Game.Quest.ItemImbuements.Chain(e.ItemImbuementId)
                    .Select(c => c.Quest).FirstOrDefault(q => me.QuestManager.GetQuestState((ushort)q) != Game.Quest.Static.QuestState.Completed)))
                .Where(c => c.Quest != 0u && me.QuestManager.GetQuestState((ushort)c.Quest) == null)
                .Where(c => Game.Quest.GlobalQuestManager.Instance.GetQuestInfo((ushort)c.Quest) is { } q && q.Entry.PrerequisiteLevel <= me.Level
                    && (q.Entry.QuestPlayerFactionEnum == 2u || q.Entry.QuestPlayerFactionEnum == (me.Faction1 == Faction.Exile ? 0u : 1u)))
                .Take(200).ToList();
            if (candidates.Count == 0)
                throw new ProbeSkip();

            var pick = candidates[BotManager.Rng.Next(candidates.Count)];
            EnsureRoom(me, 1u);
            me.Inventory.ItemCreate(InventoryLocation.Inventory, pick.Entry.Id, 1u);
            Game.Entity.Item item = me.Inventory.GetItems(InventoryLocation.Inventory).LastOrDefault(i => i?.Info?.Entry?.Id == pick.Entry.Id);
            Expect(item != null, $"item {pick.Entry.Id} not created");

            uint flags = item.Info.SlotEntry?.EquippedSlotFlags ?? 0u;
            int slot = -1;
            for (int b = 0; b < 32 && slot < 0; b++)
                if ((flags & (1u << b)) != 0u)
                    slot = b;
            if (slot < 0 || me.Inventory.CanMoveItem(item, InventoryLocation.Equipped, (uint)slot) != null)
            {
                FailureLedger.Record("imbuement-probe-skip", "cannot equip", $"item {pick.Entry.Id}");
                me.Inventory.ItemDelete(pick.Entry.Id, 1u);
                throw new ProbeSkip();
            }

            me.Inventory.ItemMove(item, InventoryLocation.Equipped, (uint)slot);
            me.QuestManager.CheckImbuements();
            var state = me.QuestManager.GetQuestState((ushort)pick.Quest);
            Expect(state == Game.Quest.Static.QuestState.Accepted, $"quest {pick.Quest} of item {pick.Entry.Id} not started on equip (state {state})");

            // the item's own step (one quest can be a step of several imbuements with different rewards)
            uint rewardId = Game.Quest.ItemImbuements.Chain(pick.Entry.ItemImbuementId).FirstOrDefault(c => c.Quest == pick.Quest).Reward;
            var reward = rewardId != 0u ? GameTableManager.Instance.ItemImbuementReward.GetEntry(rewardId) : null;
            me.ApplyLevelStats(false);
            float before = reward?.ItemImbuementRewardTypeEnum == Game.Quest.ItemImbuements.RewardStat && reward.RewardObjectId < 256u
                ? me.GearStats[reward.RewardObjectId] : 0f;
            me.QuestManager.QuestAchieve((ushort)pick.Quest);
            Expect(me.QuestManager.GetQuestState((ushort)pick.Quest) == Game.Quest.Static.QuestState.Achieved, $"quest {pick.Quest} not achieved");
            me.QuestManager.QuestComplete((ushort)pick.Quest, 0, false);
            Expect(me.QuestManager.GetQuestState((ushort)pick.Quest) == Game.Quest.Static.QuestState.Completed, $"quest {pick.Quest} not completed without a receiver");
            if (reward?.ItemImbuementRewardTypeEnum == Game.Quest.ItemImbuements.RewardStat && reward.RewardObjectId < 256u)
            {
                // the imbuement stat follows the archived tooltips: float x the item's unrounded secondary rating sum
                me.ApplyLevelStats(false);
                float expected = reward.RewardValueFloat * Game.Combat.ItemStats.SecondaryRaw(pick.Entry);
                float gained = me.GearStats[reward.RewardObjectId] - before;
                Expect(MathF.Abs(gained - expected) < 0.01f, $"imbuement stat {reward.RewardObjectId} gained {gained}, expected {expected}");
                FailureLedger.Record("system-ok", "imbuement-stat", $"property {reward.RewardObjectId} +{expected:0.##}");
            }
            FailureLedger.Record("imbuement-probe-ok", $"quest {pick.Quest}", $"item {pick.Entry.Id}");
        }

        // rotating dailies: the draw is stable within a day, differs between days for some characters, respects the per group counts, and the quest log only takes offered quests
        private static void PeriodicProbe(Player me)
        {
            var today = Game.Quest.PeriodicQuests.Offered(me.CharacterId, DateTime.UtcNow);
            Expect(today.Count > 0, "no periodic quest offered today");
            Expect(today.SetEquals(Game.Quest.PeriodicQuests.Offered(me.CharacterId, DateTime.UtcNow)), "the offer changed within a day");

            var byGroup = GameTableManager.Instance.Quest2.Entries.Where(q => q.PeriodicQuestGroupId != 0u && today.Contains(q.Id)).GroupBy(q => q.PeriodicQuestGroupId);
            foreach (var group in byGroup)
            {
                var row = GameTableManager.Instance.PeriodicQuestGroup.GetEntry(group.Key);
                Expect(row != null && group.Count() <= Math.Max(1u, Math.Min(row.PeriodicQuestsOffered, 100000u)), $"group {group.Key} offers {group.Count()} quests, table says {row?.PeriodicQuestsOffered}");
            }

            var tomorrow = Game.Quest.PeriodicQuests.Offered(me.CharacterId, DateTime.UtcNow.AddDays(1d));
            FailureLedger.Record("periodic-rotation", tomorrow.SetEquals(today) ? "same offer tomorrow" : "different offer tomorrow");

            var hidden = GameTableManager.Instance.Quest2.Entries.FirstOrDefault(q => q.PeriodicQuestGroupId != 0u && Game.Quest.PeriodicQuests.IsPooled(q.Id)
                && GameTableManager.Instance.PeriodicQuestGroup.GetEntry(q.PeriodicQuestGroupId)?.ContractTypeEnum == 0u && !today.Contains(q.Id));
            if (hidden != null)
                Expect(!me.QuestManager.CanAcceptWithoutGiver(Game.Quest.GlobalQuestManager.Instance.GetQuestInfo((ushort)hidden.Id)), $"quest {hidden.Id} is not offered today but can be accepted");
        }

        // claims today's login reward, then walks the clock: the next day continues the streak, two days later restarts it
        private static void DailyLoginProbe(Player me)
        {
            var login = me.DailyLoginManager;
            DateTime now = DateTime.UtcNow;
            if (login.CanClaim(now))
            {
                EnsureRoom(me, 2u);
                uint day = login.Claim(now);
                Expect(day > 0u, "claim gave no login day although today was open");
                Expect(!login.CanClaim(now), "claimed twice in one day");
                return;
            }

            int streak = login.Streak;
            EnsureRoom(me, 2u);
            uint next = login.Claim(now.AddDays(1d));
            Expect(next == (uint)(streak % 365) + 1u, $"streak did not continue: day {next} after a streak of {streak}");
            Expect(login.Streak == streak + 1, "streak counter after a claim on the next day");
            uint restart = login.Claim(now.AddDays(4d));
            Expect(restart == 1u && login.Streak == 1, $"streak did not restart after a gap: day {restart}");
        }

        // trades materials for materials or currency through ResourceConversion: missing sources are refused, the right amounts move
        private static void ConversionProbe(Player me)
        {
            var rows = GameTableManager.Instance.ResourceConversion.Entries
                .Where(r => r != null && r.Id != 0u && r.SurchargeId == 0u && r.TargetCount <= 100000u && GameTableManager.Instance.Item.GetEntry(r.SourceId) != null
                    && (r.ResourceConversionTypeEnum == 1u ? Enum.IsDefined(typeof(CurrencyType), (int)r.TargetId) : GameTableManager.Instance.Item.GetEntry(r.TargetId) != null)).ToList();
            Expect(rows.Count > 0, "no usable resource conversion row");
            var row = rows[BotManager.Rng.Next(rows.Count)];
            var sourceInfo = GameTableManager.Instance.Item.GetEntry(row.SourceId);
            uint stack = Math.Max(1u, sourceInfo.MaxStackCount);
            // non-stackable or small stacks need one slot per piece, skip rows the bags cannot hold
            EnsureRoom(me, (Math.Max(1u, row.SourceCount) + stack - 1u) / stack + 2u);

            while (me.Inventory.GetItemCount(row.SourceId) > 0u)
                me.Inventory.ItemDelete(row.SourceId, me.Inventory.GetItemCount(row.SourceId));

            Expect(Game.Rewards.ResourceConversion.Convert(me, row.Id) == Game.Rewards.ConversionResult.MissingSource, "conversion without the source items was not refused");

            me.Inventory.ItemCreate(InventoryLocation.Inventory, row.SourceId, Math.Max(1u, row.SourceCount));
            bool toCurrency = row.ResourceConversionTypeEnum == 1u;
            ulong currency = toCurrency ? me.CurrencyManager.GetCurrency((CurrencyType)row.TargetId) ?? 0ul : 0ul;
            uint targetItems = toCurrency ? 0u : me.Inventory.GetItemCount(row.TargetId);
            var result = Game.Rewards.ResourceConversion.Convert(me, row.Id);
            Expect(result == Game.Rewards.ConversionResult.Done, $"conversion {row.Id}: {result}");
            if (toCurrency)
                Expect((me.CurrencyManager.GetCurrency((CurrencyType)row.TargetId) ?? 0ul) == currency + row.TargetCount, "currency not credited");
            else
                Expect(me.Inventory.GetItemCount(row.TargetId) == targetItems + Math.Max(1u, row.TargetCount), $"target items not received (row {row.Id}, source {row.SourceId}x{row.SourceCount}, target {row.TargetId}x{row.TargetCount}, had {targetItems}, now {me.Inventory.GetItemCount(row.TargetId)})");

            Expect(me.Inventory.GetItemCount(row.SourceId) == 0u, "source items not consumed");
        }

        private static int worldBossCursor;

        // spawns a world boss next to the bot for a few seconds: the creature must exist, resolve to its encounter definition and enter the map
        private static void WorldBossProbe(Player me)
        {
            var defs = Game.Encounters.EncounterManager.Instance.Definitions
                .Where(d => d.World == 0u && (d.Source ?? "").StartsWith("world boss")).ToList();
            Expect(defs.Count > 0, "no world boss definitions loaded");
            var def = defs[(int)((uint)System.Threading.Interlocked.Increment(ref worldBossCursor) % defs.Count)];

            string wanted = Game.Research.CreatureResearch.Slug(def.Boss);
            var text = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
            var creature = GameTableManager.Instance.Creature2.Entries
                .FirstOrDefault(c => c != null && c.Id != 0u && Game.Research.CreatureResearch.Slug(text?.GetEntry(c.LocalizedTextIdName)) == wanted);
            Expect(creature != null, $"world boss {def.Boss} has no Creature2 entry");

            var unit = Game.Entity.CreatureSpawner.Spawn(me.Map, creature.Id, me.Position, 15d);
            Expect(unit != null, $"world boss {def.Boss} could not be spawned next to the bot");
            Expect(Game.Encounters.EncounterManager.Instance.Get(unit)?.Phases.Count > 0, $"world boss {def.Boss} resolves to no encounter phases");

            // lifecycle from the PublicEventObjective rows: 7 world boss events with a 3600 s reset and a 120 s countdown, boss respawn = both
            var events = Game.Events.PublicEventEngine.Instance.WorldBossEvents();
            Expect(events.Count == 12, $"{events.Count} world boss events found, expected 12");
            Expect(events.All(e => e.Reset == 3600d && (e.Countdown == 120d || e.Countdown == 0d && e.Event == 586u)), "world boss reset/countdown not 3600/120 s (Mechathorn 586 has no countdown)");
            Expect(Game.Events.PublicEventEngine.Instance.WorldBossRespawnSeconds(24740u) == 3720d, "Metal Maw (24740) does not respawn after reset + countdown");
            Expect(Game.Events.PublicEventEngine.Instance.WorldBossRespawnSeconds(1u) == null, "an ordinary creature got a world boss respawn");
        }
    }
}
