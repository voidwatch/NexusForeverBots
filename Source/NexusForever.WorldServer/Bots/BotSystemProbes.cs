using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Housing;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Travel;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>System probes: every feature through the real client message handler (docs/SYSTEMS.md).</summary>
    public static partial class BotSystems
    {
        /// <summary>Vendor window open: damage a weapon or armor piece, repair it (0x014C with its guid), then repair all (guid 0).</summary>
        private static void RepairCheck(Player me)
        {
            // armor and weapons have 1 copper (or no) table value, so the client formula gives a repair cost of 0 (docs/ASSUMPTIONS.md "Remaining partial systems")
            Item2Entry entry = GameTableManager.Instance.Item.Entries.Where(e => e != null && (e.Item2FamilyId == 1u || e.Item2FamilyId == 2u) && e.PowerLevel > 0u)
                .OrderByDescending(e => e.CurrencyTypeIdSellToVendor[0] == (uint)CurrencyType.Credits ? e.CurrencyAmountSellToVendor[0] : 0u).FirstOrDefault();
            if (entry == null)
            {
                FailureLedger.Record("system-skip", "vendor-repair: no armor or weapon with a credit value");
                return;
            }

            EnsureRoom(me, 1u);
            me.Inventory.ItemCreate(InventoryLocation.Inventory, entry.Id, 1u);
            Item item = me.Inventory.GetItems(InventoryLocation.Inventory).LastOrDefault(i => i != null && i.Info.Id == entry.Id);
            Expect(item != null, $"repair item {entry.Id} not created");

            ItemDurability.Set(me, item, 0.5f);
            ulong cost = ItemDurability.RepairCost(item);
            Expect(ItemDurability.CurrentPoints(item) < ItemDurability.MaxPoints(item), $"item {entry.Id} not damaged at half durability (max {ItemDurability.MaxPoints(item)})");
            ulong before = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            ulong guid = item.Guid;
            VendorHandler.HandleVendorRepair(me.Session, BotPackets.Build<ClientVendorRepair>(w => { w.Write(guid); w.Write(0ul); w.Write(0ul); }));
            Expect(item.Durability >= 1f, $"durability {item.Durability} after repair");
            Expect((me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) == before - cost, $"repair charged {before - (me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul)}, expected {cost}");

            ItemDurability.Set(me, item, 0f);
            VendorHandler.HandleVendorRepair(me.Session, BotPackets.Build<ClientVendorRepair>(w => { w.Write(0ul); w.Write(0ul); w.Write(0ul); }));
            Expect(!ItemDurability.Damaged(me).Any(), "damaged items left after repair all");
            me.Inventory.ItemRemove(item);
            FailureLedger.Record("system-ok", "vendor-repair");
        }

        /// <summary>Taxi purchase through 0x00FF: the bot stands at a node and buys the hop path to an unlocked node two hops away (one hop when none).</summary>
        private static void TaxiPathProbe(Player me)
        {
            uint? source = me.TaxiManager.CurrentNode();
            if (source == null)
            {
                var starts = GameTableManager.Instance.TaxiRoute.Entries.Where(r => r != null && me.TaxiManager.FactionAllowed(r.TaxiNodeIdSource)).ToList();
                var start = starts[BotManager.Rng.Next(starts.Count)];
                if (!TaxiManager.TryGetLocation(start.TaxiNodeIdSource, out ushort world, out Vector3 position) || !BotQuests.CanVisitWorld(world))
                    throw new ProbeSkip();
                me.TeleportTo(world, position.X, position.Y, position.Z);
                RunNext(me, "taxipath");
                throw new ProbeSkip();
            }

            List<uint> path = null;
            foreach (TaxiRouteEntry first in GameTableManager.Instance.TaxiRoute.Entries.Where(r => r != null && r.TaxiNodeIdSource == source.Value))
            {
                TaxiRouteEntry second = GameTableManager.Instance.TaxiRoute.Entries.FirstOrDefault(r => r != null && r.TaxiNodeIdSource == first.TaxiNodeIdDestination
                    && r.TaxiNodeIdDestination != source.Value && me.TaxiManager.FactionAllowed(r.TaxiNodeIdDestination) && me.TaxiManager.FactionAllowed(first.TaxiNodeIdDestination)
                    && TaxiManager.TryGetLocation(r.TaxiNodeIdDestination, out _, out _));
                if (second != null && GameTableManager.Instance.TaxiRoute.Entries.All(r => r == null || r.TaxiNodeIdSource != source.Value || r.TaxiNodeIdDestination != second.TaxiNodeIdDestination))
                {
                    path = new List<uint> { first.TaxiNodeIdDestination, second.TaxiNodeIdDestination };
                    break;
                }
                if (path == null && me.TaxiManager.FactionAllowed(first.TaxiNodeIdDestination) && TaxiManager.TryGetLocation(first.TaxiNodeIdDestination, out _, out _))
                    path = new List<uint> { first.TaxiNodeIdDestination };
            }
            if (path == null)
                throw new ProbeSkip();

            ulong price = 0ul;
            uint previous = source.Value;
            foreach (uint node in path)
            {
                price += GameTableManager.Instance.TaxiRoute.Entries.First(r => r != null && r.TaxiNodeIdSource == previous && r.TaxiNodeIdDestination == node).Price;
                previous = node;
            }

            me.TaxiManager.Unlock(path[^1]);
            me.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, price);
            ulong before = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            var nodes = new List<uint>(path);
            MatchingHandler.HandleTaxiPurchase(me.Session, BotPackets.Build<ClientTaxiPurchase>(w =>
            {
                w.Write((uint)nodes.Count);
                foreach (uint node in nodes)
                    w.Write(node);
            }));
            Expect((me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) == before - price, $"taxi path of {path.Count} hops: charged {before - (me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul)}, expected {price}");
            FailureLedger.Record("system-ok", $"taxipath-{path.Count}-hop");
        }

        private static void WriteQueue(NexusForever.Shared.Network.GamePacketWriter w, byte matchType, IReadOnlyList<uint> maps, uint roles)
        {
            w.Write(matchType, 5u);
            w.Write((uint)maps.Count);
            foreach (uint map in maps)
                w.Write(map);
            w.Write((ushort)0, 14u);
            w.Write(0u);
            w.Write(roles);
            w.Write(0u);
        }

        private static List<uint> MatchingMaps(ushort world)
        {
            return GameTableManager.Instance.MatchingGameMap.Entries.Where(m => m != null && m.WorldId == world).Select(m => m.Id).ToList();
        }

        /// <summary>Group finder through MatchMakingLib.Queue (0x05EF): queue with the role bit field; a match the queue forms is disbanded.</summary>
        private static void MatchQueueProbe(Player me)
        {
            GroupFinder finder = GroupFinder.Instance;
            if (me.Map == null || finder.IsQueued(me) || GroupManager.Instance.GetGroup(me.CharacterId) != null)
                throw new ProbeSkip();

            List<ushort> available = finder.AvailableDungeons(me);
            if (available.Count == 0)
                throw new ProbeSkip();
            List<uint> maps = MatchingMaps(available[0]);
            Expect(maps.Count > 0, $"no MatchingGameMap row for world {available[0]}");

            GroupFinder.Role role = GroupFinder.CanFill(me.Class, GroupFinder.Role.Tank) && BotManager.Rng.NextDouble() < 0.4 ? GroupFinder.Role.Tank
                : GroupFinder.CanFill(me.Class, GroupFinder.Role.Healer) && BotManager.Rng.NextDouble() < 0.5 ? GroupFinder.Role.Healer : GroupFinder.Role.Damage;
            PlayerGroup last = finder.LastMatch;
            MatchingHandler.HandleMatchingQueue(me.Session, BotPackets.Build<ClientMatchingQueue>(w => WriteQueue(w, MatchingHandler.MatchTypeDungeon, maps, 1u << (int)role)));

            PlayerGroup group = finder.LastMatch;
            if (group != last && group != null)
            {
                Expect(group.Members.Count == 5, $"queue match of {group.Members.Count}");
                Player leader = Game.CharacterCache.CharacterManager.Instance.GetPlayer(group.LeaderId);
                if (leader != null)
                    GroupManager.Instance.Leave(leader, group.Id, true);
                FailureLedger.Record("system-ok", "matchqueue-match");
                return;
            }
            Expect(finder.IsQueued(me), $"not queued after MatchMakingLib.Queue as {role}");

            // the wrong match type is refused
            finder.Dequeue(me);
            MatchingHandler.HandleMatchingQueue(me.Session, BotPackets.Build<ClientMatchingQueue>(w => WriteQueue(w, 0, maps, 1u << (int)role)));
            Expect(!finder.IsQueued(me), "queued for a battleground (match type 0)");
        }

        /// <summary>Another online bot of the same faction that is not grouped or queued, null when there is none.</summary>
        private static Player GroupPartner(Player me)
        {
            return NexusForever.Shared.Network.NetworkManager<Network.WorldSession>.Instance
                .GetSessions(s => s.Player != null && s.Player != me && s.Player.Session is BotSession && s.Player.Map != null && s.Player.Faction1 == me.Faction1
                    && GroupManager.Instance.GetGroup(s.Player.CharacterId) == null && !GroupFinder.Instance.IsQueued(s.Player))
                .Select(s => s.Player).FirstOrDefault();
        }

        /// <summary>QueueAsGroup (0x05F3) for a two-bot group, both confirm a role (0x05B2); then a second check that one member declines.</summary>
        private static void RoleCheckProbe(Player me)
        {
            Player other = GroupPartner(me);
            if (other == null || GroupManager.Instance.GetGroup(me.CharacterId) != null || GroupFinder.Instance.IsQueued(me))
                throw new ProbeSkip();
            List<ushort> available = GroupFinder.Instance.AvailableDungeons(me);
            if (available.Count == 0)
                throw new ProbeSkip();
            List<uint> maps = MatchingMaps(available[0]);

            PlayerGroup group = GroupManager.Instance.CreateServerGroup(new List<Player> { me, other }, 5);
            try
            {
                Expect(group.Members.Count == 2, $"server group of {group.Members.Count}");
                MatchingHandler.HandleMatchingQueueParty(me.Session, BotPackets.Build<ClientMatchingQueueParty>(w => WriteQueue(w, MatchingHandler.MatchTypeDungeon, maps, 1u << 2)));
                Expect(MatchingHandler.RoleCheckActive(group), "no role check after QueueAsGroup");
                foreach (Player member in new[] { me, other })
                    MatchingHandler.HandleMatchingRoleCheckResponse(member.Session, BotPackets.Build<ClientMatchingRoleCheckResponse>(w =>
                    {
                        w.Write(MatchingHandler.MatchTypeDungeon, 5u);
                        w.Write(1u << 2);
                        w.Write(true);
                    }));
                Expect(!MatchingHandler.RoleCheckActive(group), "role check still open after every member confirmed");

                // the two-damage premade waits in the group queue and is filled from solo players: a tank, a healer, one more damage
                Expect(GroupFinder.Instance.IsGroupQueued(group) || group.Members.Count == 5, "premade neither queued nor filled after its role check");
                if (group.Members.Count < 5)
                {
                    var solos = NexusForever.Shared.Network.NetworkManager<Network.WorldSession>.Instance
                        .GetSessions(s => s.Player != null && s.Player != me && s.Player != other && s.Player.Session is BotSession && s.Player.Map != null
                            && s.Player.Faction1 == me.Faction1 && GroupManager.Instance.GetGroup(s.Player.CharacterId) == null && !GroupFinder.Instance.IsQueued(s.Player))
                        .Select(s => s.Player).ToList();
                    Player tank = solos.FirstOrDefault(p => GroupFinder.CanFill(p.Class, GroupFinder.Role.Tank));
                    Player healer = solos.FirstOrDefault(p => p != tank && GroupFinder.CanFill(p.Class, GroupFinder.Role.Healer));
                    Player damage = solos.FirstOrDefault(p => p != tank && p != healer);
                    if (tank != null && healer != null && damage != null)
                    {
                        GroupFinder.Instance.Enqueue(tank, GroupFinder.Role.Tank, available[0]);
                        GroupFinder.Instance.Enqueue(healer, GroupFinder.Role.Healer, available[0]);
                        GroupFinder.Instance.Enqueue(damage, GroupFinder.Role.Damage, available[0]);
                        GroupFinder.Instance.TryMatch(available[0]);
                        foreach (Player solo in new[] { tank, healer, damage })
                            GroupFinder.Instance.Dequeue(solo);
                        Expect(group.Members.Count == 5, $"premade of 2 not filled from 3 queued solo players ({group.Members.Count} members)");
                        FailureLedger.Record("system-ok", "premade-filled");
                    }
                }

                MatchingHandler.HandleMatchingQueueParty(me.Session, BotPackets.Build<ClientMatchingQueueParty>(w => WriteQueue(w, MatchingHandler.MatchTypeDungeon, maps, 1u << 2)));
                MatchingHandler.HandleMatchingRoleCheckResponse(other.Session, BotPackets.Build<ClientMatchingRoleCheckResponse>(w =>
                {
                    w.Write(MatchingHandler.MatchTypeDungeon, 5u);
                    w.Write(0u);
                    w.Write(false);
                }));
                Expect(!MatchingHandler.RoleCheckActive(group), "role check still open after a member declined");
            }
            finally
            {
                if (GroupManager.Instance.GetGroup(me.CharacterId) is PlayerGroup g)
                    GroupManager.Instance.Leave(me, g.Id, true);
            }
        }

        private sealed class HousingTrip
        {
            public ushort World;
            public Vector3 Position;
            public int Stage;
        }

        private static readonly ConditionalWeakTable<Player, HousingTrip> housingTrips = new();

        /// <summary>
        /// Housing through the handlers on the residence map. Turn 1: go to the own residence. Turn 2: plug place, rotate, repair, remove (0x0510),
        /// privacy (0x00C9), visit another bot's public residence by name (0x0531). Turn 3: return home (0x052E handler), then back to the world.
        /// </summary>
        private static void HousingVisitProbe(Player me)
        {
            if (!housingTrips.TryGetValue(me, out HousingTrip trip))
            {
                if (me.Map == null || me.Map is ResidenceMapInstance || !me.CanTeleport() || !BotQuests.CanVisitWorld(me.Map.Entry.Id))
                    throw new ProbeSkip();
                Residence own = me.ResidenceManager.Residence ?? GlobalResidenceManager.Instance.CreateResidence(me);
                trip = new HousingTrip { World = (ushort)me.Map.Entry.Id, Position = me.Position };
                housingTrips.Add(me, trip);
                ResidenceEntrance entrance = GlobalResidenceManager.Instance.GetResidenceEntrance(own.PropertyInfoId);
                me.TeleportTo(entrance.Entry, entrance.Position, own.Parent?.Id ?? own.Id);
                RunNext(me, "housingvisit");
                throw new ProbeSkip();
            }

            try
            {
                if (me.Map is not ResidenceMapInstance)
                {
                    if (++trip.Stage > 6)
                    {
                        // the bot's own logic (combat, quest travel) can keep it from teleporting; recorded, not a housing failure
                        FailureLedger.Record("system-skip", "housingvisit: residence teleport did not happen", $"map {me.Map?.Entry.Id}");
                        housingTrips.Remove(me);
                        throw new ProbeSkip();
                    }
                    RunNext(me, "housingvisit");
                    throw new ProbeSkip();
                }

                Residence residence = me.ResidenceManager.Residence;
                if (trip.Stage < 100 && residence?.Map == me.Map)
                {
                    PlugChecks(me, residence);

                    var level = residence.PrivacyLevel;
                    HousingHandler.HandleHousingSetPrivacyLevel(me.Session, BotPackets.Build<ClientHousingSetPrivacyLevel>(w => w.Write(Game.Housing.Static.ResidencePrivacyLevel.Public, 32u)));
                    Expect(residence.PrivacyLevel == Game.Housing.Static.ResidencePrivacyLevel.Public, "privacy not public through 0x00C9");

                    // visit another online bot's public residence by owner name
                    Player host = NexusForever.Shared.Network.NetworkManager<Network.WorldSession>.Instance
                        .GetSessions(s => s.Player != null && s.Player != me && s.Player.Session is BotSession && s.Player.ResidenceManager.Residence?.PrivacyLevel == Game.Housing.Static.ResidencePrivacyLevel.Public)
                        .Select(s => s.Player).FirstOrDefault();
                    if (host != null)
                    {
                        string name = host.Name;
                        trip.Stage = 100;
                        HousingHandler.HandleHousingVisit(me.Session, BotPackets.Build<ClientHousingVisit>(w =>
                        {
                            w.Write(0ul);
                            w.Write((ushort)0, 14u);
                            w.Write(0ul);
                            w.WriteStringWide(name);
                            w.Write((ushort)0, 14u);
                            w.Write(0ul);
                            w.WriteStringWide("");
                        }));
                        RunNext(me, "housingvisit");
                        FailureLedger.Record("system-ok", "housing-visit-sent");
                        throw new ProbeSkip();
                    }
                    FailureLedger.Record("system-skip", "housingvisit: no other public bot residence yet");
                }
                else if (trip.Stage >= 100 && residence?.Map != me.Map)
                {
                    FailureLedger.Record("system-ok", "housing-visit-arrived");
                    HousingHandler.HandleHousingReturn(me.Session, BotPackets.Build<ClientHousingReturn>(w => { }));
                    FailureLedger.Record("system-ok", "housing-return");
                }
                else if (trip.Stage >= 100 && trip.Stage++ < 106)
                {
                    RunNext(me, "housingvisit");
                    throw new ProbeSkip();
                }

                housingTrips.Remove(me);
                me.TeleportTo(trip.World, trip.Position.X, trip.Position.Y, trip.Position.Z);
            }
            catch (ProbeFailure)
            {
                housingTrips.Remove(me);
                me.TeleportTo(trip.World, trip.Position.X, trip.Position.Y, trip.Position.Z);
                throw;
            }
        }

        private static void PlugChecks(Player me, Residence residence)
        {
            Plot plot = null;
            HousingPlugItemEntry plug = null;
            foreach (Plot candidate in residence.GetPlots().Where(p => p.PlotInfoEntry != null && p.Index > 0))
            {
                plug = GameTableManager.Instance.HousingPlugItem.Entries.FirstOrDefault(e => e != null && e.Id != 0u && e.HousingPlotTypeId == candidate.PlotInfoEntry.PlotType
                    && e.PrerequisiteId00 == 0u && e.PrerequisiteId01 == 0u && e.Id != candidate.PlugItemEntry?.Id);
                if (plug != null)
                {
                    plot = candidate;
                    break;
                }
            }
            if (plot == null)
            {
                FailureLedger.Record("system-skip", "housing-plug: no plot with a fitting plug");
                return;
            }

            uint previous = plot.PlugItemEntry?.Id ?? 0u;
            ulong owner = me.CharacterId;
            void Send(byte op, uint item, uint facing) => HousingHandler.HandleHousingPlugUpdate(me.Session, BotPackets.Build<ClientHousingPlugUpdate>(w =>
            {
                w.Write(WorldServer.RealmId, 14u);
                w.Write(owner);
                w.Write((uint)plot.Index);
                w.Write(item);
                w.Write(facing);
                w.Write(0u);
                w.Write(op, 3u);
                w.WriteBytes(new byte[100]);
            }));

            Send(ClientHousingPlugUpdate.Place, plug.Id, 4u);
            Expect(plot.PlugItemEntry?.Id == plug.Id && plot.PlugEntity != null, $"plug {plug.Id} not placed on plot {plot.Index}");
            Send(ClientHousingPlugUpdate.Place, plug.Id, 1u);
            Expect(plot.PlugFacing == Game.Housing.Static.HousingPlugFacing.South, $"plug facing {plot.PlugFacing} after rotate");
            plot.BuildState = 0;
            Send(ClientHousingPlugUpdate.Repair, 0u, 0u);
            Expect(plot.BuildState == 4, "plug not repaired");
            Send(ClientHousingPlugUpdate.Remove, 0u, 0u);
            Expect(plot.PlugItemEntry == null && plot.PlugEntity == null, "plug not removed");
            if (previous != 0u)
                Send(ClientHousingPlugUpdate.Place, previous, 4u);
            FailureLedger.Record("system-ok", "housing-plug");
        }

        /// <summary>
        /// Dye unlock item through ClientItemGenericUnlock (dye known afterwards, item used), then a costume save (0x0255) that dyes an unlocked
        /// chest piece: the dye cost is charged.
        /// </summary>
        private static void DyeProbe(Player me)
        {
            EnsureRoom(me, 2u);
            GenericUnlockManager unlocks = me.Session.GenericUnlockManager;
            var dyeItems = GameTableManager.Instance.Item.Entries.Where(e => e != null && e.GenericUnlockSetId != 0u).ToList();
            Item2Entry dyeItem = dyeItems.Where(e => SetEntries(e.GenericUnlockSetId).Any(u => !unlocks.IsUnlocked((GenericUnlockType)u.GenericUnlockTypeEnum, u.UnlockObject)))
                .OrderBy(_ => BotManager.Rng.Next()).FirstOrDefault();
            if (dyeItem != null)
            {
                me.Inventory.ItemCreate(InventoryLocation.Inventory, dyeItem.Id, 1u);
                Item item = me.Inventory.GetItems(InventoryLocation.Inventory).LastOrDefault(i => i != null && i.Info.Id == dyeItem.Id);
                Expect(item != null, $"dye item {dyeItem.Id} not created");
                uint count = me.Inventory.GetItemCount(dyeItem.Id);
                var location = item.Location;
                uint bagIndex = item.BagIndex;
                ItemHandler.HandleItemGenericUnlock(me.Session, BotPackets.Build<ClientItemGenericUnlock>(w => { w.Write(location, 9u); w.Write(bagIndex); }));
                Expect(SetEntries(dyeItem.GenericUnlockSetId).All(u => unlocks.IsUnlocked((GenericUnlockType)u.GenericUnlockTypeEnum, u.UnlockObject)), $"dye set {dyeItem.GenericUnlockSetId} not unlocked by item {dyeItem.Id}");
                Expect(me.Inventory.GetItemCount(dyeItem.Id) < count, "dye item not used");
                FailureLedger.Record("system-ok", "dye-unlock");
            }

            // costume save with a dye on channel 0 of an unlocked chest piece
            uint[] known = GameTableManager.Instance.DyeColorRamp.Entries.Where(r => r != null && r.Id != 0u && unlocks.IsDyeUnlocked(r.Id)).Select(r => r.Id).ToArray();
            if (known.Length == 0)
                throw new ProbeSkip();
            Item2Entry chest = GameTableManager.Instance.Item.Entries.FirstOrDefault(e => e != null && e.Item2FamilyId == 1u
                && GameTableManager.Instance.Item2Type.GetEntry(e.Item2TypeId)?.ItemSlotId == (uint)ItemSlot.ArmorChest
                && (GameTableManager.Instance.ItemDisplay.GetEntry(Item.GetDisplayId(e))?.DyeChannelFlags & 1u) != 0u);
            Expect(chest != null, "no dyeable chest item in the tables");

            me.Inventory.ItemCreate(InventoryLocation.Inventory, chest.Id, 1u);
            Item piece = me.Inventory.GetItems(InventoryLocation.Inventory).LastOrDefault(i => i != null && i.Info.Id == chest.Id);
            Expect(piece != null, $"chest {chest.Id} not created");
            var pieceLocation = piece.Location;
            uint pieceIndex = piece.BagIndex;
            CostumeHandler.HandleCostumeItemUnlock(me.Session, BotPackets.Build<ClientCostumeItemUnlock>(w => { w.Write(pieceLocation, 9u); w.Write(pieceIndex); }));
            me.Inventory.ItemRemove(piece);

            uint dye = known[BotManager.Rng.Next(known.Length)];
            DyeColorRampEntry ramp = GameTableManager.Instance.DyeColorRamp.GetEntry(dye);
            Game.Entity.Costume current = me.CostumeManager.GetCostume(0);
            CostumeItem currentChest = current?.GetItem(CostumeItemSlot.Chest);
            bool unchanged = currentChest != null && currentChest.ItemId == chest.Id && (currentChest.DyeData & 0x3FF) == (ramp.RampIndex & 0x3FF);
            ulong expected = unchanged ? 0ul : CostumeManager.DyeCost(me.Level, chest, 0, ramp);
            me.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, expected);
            ulong before = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;
            CostumeHandler.HandleCostumeSave(me.Session, BotPackets.Build<ClientCostumeSave>(w =>
            {
                w.Write(0);
                w.Write((byte)0, 2u);
                w.Write(0ul);
                for (int slot = 0; slot < Game.Entity.Costume.MaxCostumeItems; slot++)
                {
                    w.Write(slot == 0 ? chest.Id : 0u, 18u);
                    for (int channel = 0; channel < 3; channel++)
                        w.Write(slot == 0 && channel == 0 ? dye : 0u);
                }
                w.Write(1u);
                w.Write(false);
            }));
            CostumeItem saved = me.CostumeManager.GetCostume(0)?.GetItem(CostumeItemSlot.Chest);
            Expect(saved != null && saved.ItemId == chest.Id, "costume chest not saved");
            Expect((me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) == before - expected, $"dye charged {before - (me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul)}, expected {expected}");
            FailureLedger.Record("system-ok", expected > 0ul ? "dye-cost-charged" : "dye-cost-unchanged");
        }

        private static IEnumerable<GenericUnlockEntryEntry> SetEntries(uint setId)
        {
            GenericUnlockSetEntry set = GameTableManager.Instance.GenericUnlockSet.GetEntry(setId);
            if (set == null)
                return Enumerable.Empty<GenericUnlockEntryEntry>();
            return new[] { set.GenericUnlockEntryId00, set.GenericUnlockEntryId01, set.GenericUnlockEntryId02, set.GenericUnlockEntryId03, set.GenericUnlockEntryId04, set.GenericUnlockEntryId05 }
                .Select(id => GameTableManager.Instance.GenericUnlockEntry.GetEntry(id)).Where(e => e != null).ToList();
        }
    }
}
