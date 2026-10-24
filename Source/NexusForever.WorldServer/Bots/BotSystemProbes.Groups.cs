using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using NexusForever.Shared.Database;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Social;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>System probes, second batch: group flags, friends by identity, instance portals.</summary>
    public static partial class BotSystems
    {
        private static void WriteMemberFlags(NexusForever.Shared.Network.GamePacketWriter w, ulong groupId, ulong target, GroupMemberInfoFlags flags, GroupMemberInfoFlags changed)
        {
            w.Write(groupId);
            w.Write(WorldServer.RealmId, 14u);
            w.Write(target);
            w.Write((uint)flags);
            w.Write((uint)changed);
        }

        /// <summary>Two-bot group: leader sets the other's main tank (0x0408), the member sets their own role, a member-only change of an admin flag is refused, raid conversion (0x0406), ready check (0x044A).</summary>
        private static void GroupFlagsProbe(Player me)
        {
            Player other = GroupPartner(me);
            if (other == null || GroupManager.Instance.GetGroup(me.CharacterId) != null)
                throw new ProbeSkip();

            PlayerGroup group = GroupManager.Instance.CreateServerGroup(new List<Player> { me, other }, 5);
            try
            {
                Player leader = group.LeaderId == me.CharacterId ? me : other;
                Player member = leader == me ? other : me;
                GroupMember target = group.GetMember(member.CharacterId);

                GroupMemberInfoFlags before = target.Flags;
                GroupOptionsHandler.HandleGroupMemberFlags(leader.Session, BotPackets.Build<ClientGroupMemberFlagsUpdate>(w =>
                    WriteMemberFlags(w, group.Id, member.CharacterId, before | GroupMemberInfoFlags.MainTank, GroupMemberInfoFlags.MainTank)));
                Expect(target.Flags.HasFlag(GroupMemberInfoFlags.MainTank), "leader could not set main tank");

                GroupMemberInfoFlags roles = GroupMemberInfoFlags.Tank | GroupMemberInfoFlags.Healer | GroupMemberInfoFlags.DPS;
                GroupOptionsHandler.HandleGroupMemberFlags(member.Session, BotPackets.Build<ClientGroupMemberFlagsUpdate>(w =>
                    WriteMemberFlags(w, group.Id, member.CharacterId, (target.Flags & ~roles) | GroupMemberInfoFlags.Healer, roles)));
                Expect((target.Flags & roles) == GroupMemberInfoFlags.Healer, $"member role flags {target.Flags & roles} after picking healer");

                GroupOptionsHandler.HandleGroupMemberFlags(member.Session, BotPackets.Build<ClientGroupMemberFlagsUpdate>(w =>
                    WriteMemberFlags(w, group.Id, member.CharacterId, target.Flags | GroupMemberInfoFlags.RaidAssistant, GroupMemberInfoFlags.RaidAssistant)));
                Expect(!target.Flags.HasFlag(GroupMemberInfoFlags.RaidAssistant), "a member made themselves raid assistant");

                GroupOptionsHandler.HandleGroupMemberFlags(member.Session, BotPackets.Build<ClientGroupMemberFlagsUpdate>(w =>
                    WriteMemberFlags(w, group.Id, member.CharacterId, target.Flags | GroupMemberInfoFlags.Ready | GroupMemberInfoFlags.HasSetReady, GroupMemberInfoFlags.Ready | GroupMemberInfoFlags.HasSetReady)));
                Expect(target.Flags.HasFlag(GroupMemberInfoFlags.Ready), "member could not set ready");
                GroupOptionsHandler.HandleGroupReadyCheck(leader.Session, BotPackets.Build<ClientGroupReadyCheck>(w => { w.Write(group.Id); w.WriteStringWide("ready?"); }));
                Expect(!target.Flags.HasFlag(GroupMemberInfoFlags.Ready), "ready state not cleared by the ready check");

                // instance difficulty for the group's next instance (0x0412), veteran then normal; a member may not change it
                ulong groupId = group.Id;
                GroupOptionsHandler.HandleGroupInstanceDifficulty(leader.Session, BotPackets.Build<ClientGroupInstanceDifficulty>(w => { w.Write(groupId); w.Write((byte)1, 2u); }));
                Expect(InstanceSettings.For(DungeonInstancedMap.InstanceIdFor(leader))?.Difficulty == InstanceDifficulty.Veteran, "group difficulty not veteran");
                GroupOptionsHandler.HandleGroupInstanceDifficulty(member.Session, BotPackets.Build<ClientGroupInstanceDifficulty>(w => { w.Write(groupId); w.Write((byte)0, 2u); }));
                Expect(InstanceSettings.For(DungeonInstancedMap.InstanceIdFor(leader))?.Difficulty == InstanceDifficulty.Veteran, "a member changed the group difficulty");
                GroupOptionsHandler.HandleGroupInstanceDifficulty(leader.Session, BotPackets.Build<ClientGroupInstanceDifficulty>(w => { w.Write(groupId); w.Write((byte)0, 2u); }));
                // nobody is in an instance: GotoGroupInstance is refused
                GroupOptionsHandler.HandleGroupGotoInstance(member.Session, BotPackets.Build<ClientGroupGotoInstance>(w => w.Write(groupId)));

                // target markers (0x0432): the leader marks the member, then the mark moves to another marker, then it is cleared
                uint unit = member.Guid;
                GroupOptionsHandler.HandleGroupMarkUnit(leader.Session, BotPackets.Build<ClientGroupMarkUnit>(w => { w.Write(1u); w.Write(unit); }));
                Expect(group.Markers.TryGetValue(1u, out uint marked) && marked == unit, "marker 1 not on the member");
                GroupOptionsHandler.HandleGroupMarkUnit(leader.Session, BotPackets.Build<ClientGroupMarkUnit>(w => { w.Write(2u); w.Write(unit); }));
                Expect(!group.Markers.ContainsKey(1u) && group.Markers.ContainsKey(2u), "a unit carries two markers");
                GroupOptionsHandler.HandleGroupMarkUnit(leader.Session, BotPackets.Build<ClientGroupMarkUnit>(w => { w.Write(2u); w.Write(0u); }));
                Expect(group.Markers.Count == 0, "marker not cleared");

                GroupOptionsHandler.HandleGroupFlags(leader.Session, BotPackets.Build<ClientGroupFlagsUpdate>(w => { w.Write(group.Id); w.Write((uint)(group.Flags | GroupFlags.Raid)); }));
                Expect(group.Flags.HasFlag(GroupFlags.Raid) && group.MaxSize >= 20, $"not a raid after ConvertToRaid (flags {group.Flags}, max {group.MaxSize})");
                GroupOptionsHandler.HandleGroupFlags(member.Session, BotPackets.Build<ClientGroupFlagsUpdate>(w => { w.Write(group.Id); w.Write((uint)GroupFlags.OpenWorld); }));
                Expect(group.Flags.HasFlag(GroupFlags.Raid), "a member changed the group flags");
            }
            finally
            {
                if (GroupManager.Instance.GetGroup(me.CharacterId) is PlayerGroup g)
                    GroupManager.Instance.Leave(me, g.Id, true);
            }
        }

        /// <summary>FriendshipLib by identity: add friend (0x03B3), note (0x03C7), rival, remove (0x03C1); the list survives a reload from the JSON store.</summary>
        private static void FriendsIdentityProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            FriendManager friends = FriendManager.Instance;
            ulong id = other.CharacterId;
            friends.RemoveFriend(me.CharacterId, id);
            friends.RemoveRival(me.CharacterId, id);

            void Add(byte type, string note) => FriendshipListHandler.HandleFriendshipAdd(me.Session, BotPackets.Build<ClientFriendshipAdd>(w =>
            {
                w.Write(WorldServer.RealmId, 14u);
                w.Write(id);
                w.Write(type, 4u);
                w.WriteStringWide(note);
            }));
            void Remove(byte type) => FriendshipListHandler.HandleFriendshipRemove(me.Session, BotPackets.Build<ClientFriendshipRemove>(w =>
            {
                w.Write(WorldServer.RealmId, 14u);
                w.Write(id);
                w.Write(type, 4u);
            }));

            Add(0, "");
            Expect(friends.IsFriend(me.CharacterId, id), "friend by identity not added");
            FriendshipListHandler.HandleFriendshipSetNote(me.Session, BotPackets.Build<ClientFriendshipSetNote>(w =>
            {
                w.Write(WorldServer.RealmId, 14u);
                w.Write(id);
                w.WriteStringWide("met in a probe");
            }));
            Expect(friends.Note(me.CharacterId, id) == "met in a probe", "friend note not set");
            Add(3, "rival");
            Expect(friends.IsRival(me.CharacterId, id), "rival not added");

            var reloaded = new CharacterJson<FriendManager.State>("friends", me.CharacterId).Value;
            Expect(reloaded.Friends.Contains(id) && reloaded.Rivals.Contains(id), "friend list not saved to the store");

            Remove(0);
            Remove(3);
            Expect(!friends.IsFriend(me.CharacterId, id) && !friends.IsRival(me.CharacterId, id), "friend or rival not removed");
        }

        /// <summary>Neighbours through the handlers: invite by name (0x0512), accept (0x0513), roommate permission (0x0518) lets the neighbour decorate, evict (0x0515).</summary>
        private static void NeighbourProbe(Player me)
        {
            Player other = Partner(me) ?? throw new ProbeSkip();
            var neighbours = Game.Housing.NeighbourManager.Instance;
            Game.Housing.Residence residence = me.ResidenceManager.Residence ?? Game.Housing.GlobalResidenceManager.Instance.CreateResidence(me);
            string name = other.Name;
            ulong id = other.CharacterId;
            if (neighbours.IsNeighbour(me.CharacterId, id))
                NeighbourHandler.HandleNeighborEvict(me.Session, BotPackets.Build<ClientHousingNeighborEvict>(w => { w.Write(WorldServer.RealmId, 14u); w.Write(id); w.WriteStringWide(""); }));

            NeighbourHandler.HandleNeighborInvite(me.Session, BotPackets.Build<ClientHousingNeighborInvite>(w => { w.Write((ushort)0, 14u); w.Write(0ul); w.WriteStringWide(name); }));
            Expect(neighbours.HasInvite(id), "no pending neighbour invite after invite by name");
            NeighbourHandler.HandleNeighborResponse(other.Session, BotPackets.Build<ClientHousingNeighborResponse>(w => w.Write(true)));
            Expect(neighbours.IsNeighbour(me.CharacterId, id) && neighbours.IsNeighbour(id, me.CharacterId), "not neighbours after accept");
            Expect(!residence.CanModifyResidence(other), "a plain neighbour may decorate");

            NeighbourHandler.HandleNeighborPermission(me.Session, BotPackets.Build<ClientHousingNeighborPermission>(w =>
            {
                w.Write(WorldServer.RealmId, 14u);
                w.Write(id);
                w.WriteStringWide("");
                w.Write(Game.Housing.NeighbourManager.Roommate);
            }));
            Expect(residence.CanModifyResidence(other), "a roommate may not decorate");

            NeighbourHandler.HandleNeighborEvict(me.Session, BotPackets.Build<ClientHousingNeighborEvict>(w => { w.Write(WorldServer.RealmId, 14u); w.Write(id); w.WriteStringWide(""); }));
            Expect(!neighbours.IsNeighbour(me.CharacterId, id) && !neighbours.IsNeighbour(id, me.CharacterId), "still neighbours after evict");
        }

        private sealed class CommunityTrip
        {
            public ushort World;
            public Vector3 Position;
            public int Stage;
            public int Waits;
        }

        private static readonly ConditionalWeakTable<Player, CommunityTrip> communityTrips = new();

        /// <summary>
        /// Community plots through the upstream handlers: the bot registers its own community (one per player), goes to its residence, places the
        /// residence on community plot 1 (0x052B, property 100), checks it is a child of the community residence, removes it again (0x04EA) and
        /// goes back to where it was.
        /// </summary>
        private static void CommunityProbe(Player me)
        {
            void Back(CommunityTrip t)
            {
                communityTrips.Remove(me);
                me.TeleportTo(t.World, t.Position.X, t.Position.Y, t.Position.Z);
            }

            if (!communityTrips.TryGetValue(me, out CommunityTrip trip))
            {
                if (me.Map == null || me.Map is MapInstance || !me.CanTeleport() || !BotQuests.CanVisitWorld(me.Map.Entry.Id))
                    throw new ProbeSkip();

                var community = me.GuildManager.GetGuild<Game.Guild.Community>(Game.Guild.Static.GuildType.Community);
                if (community == null)
                {
                    string name = ("QAC " + me.Name).Length > 24 ? ("QAC " + me.Name).Substring(0, 24) : "QAC " + me.Name;
                    var check = me.GuildManager.CanRegisterGuild(Game.Guild.Static.GuildType.Community, name, "Leader", "Council", "Member");
                    if (check.Result != Game.Guild.Static.GuildResult.Success)
                    {
                        FailureLedger.Record("system-skip", "community: cannot register", check.Result.ToString());
                        throw new ProbeSkip();
                    }
                    me.GuildManager.RegisterGuild(Game.Guild.Static.GuildType.Community, name, "Leader", "Council", "Member");
                    community = me.GuildManager.GetGuild<Game.Guild.Community>(Game.Guild.Static.GuildType.Community);
                    Expect(community?.Residence != null, "community registered without a residence");
                }

                Game.Housing.Residence own = me.ResidenceManager.Residence ?? Game.Housing.GlobalResidenceManager.Instance.CreateResidence(me);
                if (own.Parent != null)
                    throw new ProbeSkip();
                trip = new CommunityTrip { World = (ushort)me.Map.Entry.Id, Position = me.Position };
                communityTrips.Add(me, trip);
                Game.Housing.ResidenceEntrance entrance = Game.Housing.GlobalResidenceManager.Instance.GetResidenceEntrance(own.PropertyInfoId);
                me.TeleportTo(entrance.Entry, entrance.Position, own.Id);
                RunNext(me, "community");
                throw new ProbeSkip();
            }

            Game.Housing.Residence residence = me.ResidenceManager.Residence;
            var guild = me.GuildManager.GetGuild<Game.Guild.Community>(Game.Guild.Static.GuildType.Community);
            if (residence == null || guild?.Residence == null)
            {
                Back(trip);
                throw new ProbeFailure("residence or community gone during the trip");
            }

            if (me.Map is not ResidenceMapInstance)
            {
                // placed on the community: the removal must be sent from a residence map, the community's own map is one (the bot's own
                // logic or the instance unload can take it away after the placement)
                if (trip.Stage == 1 && trip.Waits == 3 && residence.Parent != null)
                {
                    Game.Housing.ResidenceEntrance communityEntrance = Game.Housing.GlobalResidenceManager.Instance.GetResidenceEntrance(guild.Residence.PropertyInfoId);
                    if (communityEntrance != null)
                        me.TeleportTo(communityEntrance.Entry, communityEntrance.Position, guild.Residence.Id);
                }
                if (++trip.Waits > 10)
                {
                    FailureLedger.Record("system-skip", "community: residence teleport did not happen", $"stage {trip.Stage}");
                    if (residence.Parent != null && trip.Stage == 1)
                        throw new ProbeFailure("residence left on the community plot: no community map reached");
                    communityTrips.Remove(me);
                    throw new ProbeSkip();
                }
                RunNext(me, "community");
                throw new ProbeSkip();
            }
            trip.Waits = 0;

            switch (trip.Stage)
            {
                case 0:
                    HousingHandler.HandleHousingCommunityPlacement(me.Session, BotPackets.Build<ClientHousingCommunityPlacement>(w =>
                    {
                        w.Write((ushort)0, 14u);
                        w.Write(0ul);
                        w.Write(0u);
                    }));
                    Expect(residence.Parent == guild.Residence && guild.Residence.GetChild(me.CharacterId) != null, "residence not on the community after placement");
                    Expect(residence.PropertyInfoId == Game.Housing.Static.PropertyInfoId.CommunityResidence1, $"residence property {residence.PropertyInfoId} after placement on plot 1");
                    trip.Stage = 1;
                    FailureLedger.Record("system-ok", "community-placement");
                    RunNext(me, "community");
                    throw new ProbeSkip();
                case 1:
                    HousingHandler.HandleHousingCommunityRemoval(me.Session, BotPackets.Build<ClientHousingCommunityRemoval>(w =>
                    {
                        w.Write((ushort)0, 14u);
                        w.Write(0ul);
                    }));
                    Expect(residence.Parent == null && guild.Residence.GetChild(me.CharacterId) == null, "residence still on the community after removal");
                    Expect(residence.PropertyInfoId == Game.Housing.Static.PropertyInfoId.Residence, $"residence property {residence.PropertyInfoId} after removal");
                    trip.Stage = 2;
                    RunNext(me, "community");
                    throw new ProbeSkip();
                default:
                    Back(trip);
                    return;
            }
        }

        private static readonly ConditionalWeakTable<Player, MailState> vehicleStates = new();

        /// <summary>SummonVehicle spell (effect type 6): the bot becomes the pilot, then leaves through ClientVehicleDisembark (0x01AF).</summary>
        private static void VehicleProbe(Player me)
        {
            if (vehicleStates.TryGetValue(me, out MailState state))
            {
                if (me.VehicleGuid != 0u || me.LastMountedAt >= state.Sent)
                {
                    vehicleStates.Remove(me);
                    if (me.VehicleGuid != 0u)
                        VehicleHandler.HandleVehicleDisembark(me.Session, BotPackets.Build<ClientVehicleDisembark>(w => { }));
                    Expect(me.VehicleGuid == 0u, "still in the vehicle after Disembark");
                    return;
                }

                if (System.DateTime.UtcNow - state.Sent < System.TimeSpan.FromSeconds(30))
                    throw new ProbeSkip();

                vehicleStates.Remove(me);
                throw new ProbeFailure($"vehicle spell {state.Spell} cast but the bot never became the pilot in 30 s");
            }

            if (me.Map == null || me.IsDead || !me.CanMount())
                throw new ProbeSkip();

            var effects = GameTableManager.Instance.Spell4Effects.Entries
                .Where(e => e != null && e.EffectType == (uint)Game.Spell.Static.SpellEffectType.SummonVehicle && e.TargetFlags == 1u
                    && GameTableManager.Instance.Creature2.GetEntry(e.DataBits00) != null && GameTableManager.Instance.UnitVehicle.GetEntry(e.DataBits01) != null)
                .Where(e => GameTableManager.Instance.Spell4.GetEntry(e.SpellId) is Spell4Entry s && (s.PrerequisiteIdCasterCast == 0u
                    || Game.Prerequisite.PrerequisiteManager.Instance.Meets(me, s.PrerequisiteIdCasterCast, true)))
                .ToList();
            if (effects.Count == 0)
                throw new ProbeSkip();
            var effect = effects[BotManager.Rng.Next(effects.Count)];
            vehicleStates.Add(me, new MailState { Sent = System.DateTime.UtcNow, Spell = effect.SpellId });
            me.CastSpell(effect.SpellId, new Game.Spell.SpellParameters { UserInitiatedSpellCast = false });
            throw new ProbeSkip();
        }

        private static List<(ushort World, uint Creature, Vector3 Position, ushort Destination)> portalSpawns;

        private sealed class PortalTrip
        {
            public ushort World;
            public Vector3 Position;
            public ushort Destination;
            public ushort PortalWorld;
            public Vector3 PortalPosition;
            public int Turns;
        }

        private static readonly ConditionalWeakTable<Player, PortalTrip> portalTrips = new();

        /// <summary>World a portal unit takes the player to: an instance by name, else an open-world transport's target zone (0 = unmapped).</summary>
        private static ushort PortalTarget(Creature2Entry c, ushort world, Vector3 position)
        {
            InstancePortalEntry ip = GameTableManager.Instance.InstancePortal.GetEntry(c.InstancePortalId);
            if (ip == null)
                return 0;
            // same order as InstancePortals.Activate: the activate spell's teleport first
            if (InstancePortals.ActivateTeleport(c) is { } landing)
                return (ushort)landing.WorldId;
            ushort instance = InstancePortals.Destination(c, ip);
            return instance != 0 ? instance : InstancePortals.TravelDestination(c, ip, world, position).World;
        }

        /// <summary>The bot entered an instance through the portal probe and is about to be checked and sent back (Bot.cs leaves it there until then).</summary>
        public static bool OnPortalTrip(Player me) => portalTrips.TryGetValue(me, out _);

        /// <summary>
        /// Instance portal units through ClientActivateUnit: go to a portal whose name resolves to an instance, activate it, check the bot arrives
        /// in that world, then back to where it started.
        /// </summary>
        private static void PortalProbe(Player me)
        {
            if (portalSpawns == null)
            {
                portalSpawns = DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns(false)
                    .Select(s => (s, c: GameTableManager.Instance.Creature2.GetEntry(s.Creature)))
                    .Where(x => x.c != null && x.c.InstancePortalId != 0u && GameTableManager.Instance.InstancePortal.GetEntry(x.c.InstancePortalId) is InstancePortalEntry p && p.InstancePortalTypeEnum < 2u)
                    .Select(x => (x.s.World, x.s.Creature, new Vector3(x.s.X, x.s.Y, x.s.Z), PortalTarget(x.c, x.s.World, new Vector3(x.s.X, x.s.Y, x.s.Z))))
                    .Where(x => x.Item4 != 0 && x.Item4 != x.World)   // same-world transports cannot be told apart by the map id
                    .ToList();
                FailureLedger.Record("portal-probe-spawns", $"{portalSpawns.Count} spawned portals resolve to another world");
            }
            if (portalSpawns.Count == 0)
                throw new ProbeSkip();

            if (!portalTrips.TryGetValue(me, out PortalTrip trip))
            {
                if (me.Map == null || me.Map is MapInstance || !BotQuests.CanVisitWorld(me.Map.Entry.Id))
                    throw new ProbeSkip();
                // portals the bot may use: level range and faction of the portal unit
                var usable = portalSpawns.Where(p => GameTableManager.Instance.Creature2.GetEntry(p.Creature) is Creature2Entry c
                    && GameTableManager.Instance.InstancePortal.GetEntry(c.InstancePortalId) is InstancePortalEntry ip && (ip.MinLevel == 0u || me.Level >= ip.MinLevel)
                    && (c.FactionId == 0u || me.GetDispositionTo((Game.Reputation.Static.Faction)c.FactionId) != Game.Reputation.Static.Disposition.Hostile)).ToList();
                if (usable.Count == 0)
                    throw new ProbeSkip();
                var spawn = usable[BotManager.Rng.Next(usable.Count)];
                if (!BotQuests.CanVisitWorld(spawn.World))
                    throw new ProbeSkip();
                trip = new PortalTrip { World = (ushort)me.Map.Entry.Id, Position = me.Position, Destination = spawn.Destination, PortalWorld = spawn.World, PortalPosition = spawn.Position };
                portalTrips.Add(me, trip);
                me.TeleportTo(spawn.World, spawn.Position.X, spawn.Position.Y, spawn.Position.Z);
                RunNext(me, "portal");
                throw new ProbeSkip();
            }

            trip.Turns++;
            if (me.Map != null && me.Map.Entry.Id == trip.Destination)
            {
                portalTrips.Remove(me);
                me.TeleportTo(trip.World, trip.Position.X, trip.Position.Y, trip.Position.Z);
                FailureLedger.Record("system-ok", "portal-entered");
                return;
            }

            if (trip.Turns > 8 || me.Map == null)
            {
                portalTrips.Remove(me);
                me.TeleportTo(trip.World, trip.Position.X, trip.Position.Y, trip.Position.Z);
                throw new ProbeFailure($"not in world {trip.Destination} after the portal (map {me.Map?.Entry.Id})");
            }

            // the bot's own logic may have moved it away from the portal: back to the portal
            if (me.Map.Entry.Id != trip.PortalWorld || Vector3.Distance(me.Position, trip.PortalPosition) > 40f)
            {
                me.TeleportTo(trip.PortalWorld, trip.PortalPosition.X, trip.PortalPosition.Y, trip.PortalPosition.Z);
                RunNext(me, "portal");
                throw new ProbeSkip();
            }

            if (trip.Turns >= 1)
            {
                me.Map.Search(trip.PortalPosition, 60f, new Game.Map.Search.SearchCheckRange(trip.PortalPosition, 60f, me), out List<GridEntity> around);
                // the portal to the trip's destination (Thayd has several portal units within 60 m of each other) that the bot's level allows
                WorldEntity portal = around.OfType<WorldEntity>().FirstOrDefault(e => GameTableManager.Instance.Creature2.GetEntry(e.CreatureId) is Creature2Entry c
                    && c.InstancePortalId != 0u && GameTableManager.Instance.InstancePortal.GetEntry(c.InstancePortalId) is InstancePortalEntry ip
                    && PortalTarget(c, (ushort)me.Map.Entry.Id, e.Position) == trip.Destination && (ip.MinLevel == 0u || me.Level >= ip.MinLevel));
                if (portal == null)
                {
                    if (trip.Turns >= 6)
                    {
                        portalTrips.Remove(me);
                        me.TeleportTo(trip.World, trip.Position.X, trip.Position.Y, trip.Position.Z);
                        throw new ProbeFailure("no portal unit within 60 m of its spawn");
                    }
                }
                else if (me.GetVisible<WorldEntity>(portal.Guid) != null)
                {
                    uint guid = portal.Guid;
                    EntityHandler.HandleActivateUnit(me.Session, BotPackets.Build<ClientActivateUnit>(w => w.Write(guid)));
                }
            }

            RunNext(me, "portal");
            throw new ProbeSkip();
        }
    }
}
