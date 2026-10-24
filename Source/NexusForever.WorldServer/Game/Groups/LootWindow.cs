using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.Network;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Network;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Game.Groups
{
    /// <summary>
    /// Group loot that waits for a decision: need/greed rolls and master loot. The item is announced to the group with ServerLootNotify on the corpse
    /// (owner unit = the killed creature, one loot id per item, RequiresRoll / OnlyMasterLootable, RollTime in ms as NeedVsGreed.lua reads nTimeLeft).
    /// ClientLootRollAction and ClientLootAssignMaster (layouts from the client, docs/PROTOCOL.md) record the decision; the world loop resolves it:
    /// need beats greed, highest roll 1-100 wins (ServerLootRoll per roll, ServerLootWinner, ServerLootGrant), all passed drops the item,
    /// a missing roll at the deadline counts as pass, master loot not assigned by the deadline goes to the leader (ASSUMPTIONS "Loot window").
    /// Requests only record (any thread); grants happen in <see cref="Update"/> on the world thread after the maps.
    /// </summary>
    public static class LootWindow
    {
        public const uint RollTimeMs = 60000u;

        private sealed class Pending
        {
            public uint LootId;
            public uint OwnerUnitId;
            public ItemInfo Item;
            public LootRule Rule;
            public ulong LeaderId;
            public List<ulong> Members;
            public readonly Dictionary<ulong, byte> Rolls = new();
            public ulong MasterAssignee;
            public DateTime Deadline;
        }

        private static readonly object gate = new();
        private static readonly Dictionary<uint, Pending> pending = new();
        private static readonly Random random = new();
        private static uint nextLootId = 0x40000000u;
        private static readonly Dictionary<uint, ulong> granted = new();

        /// <summary>Character that received the item of a resolved window (0 = none or unknown); the last 1000 windows are kept.</summary>
        public static ulong WinnerOf(uint lootId)
        {
            lock (gate)
                return granted.TryGetValue(lootId, out ulong winner) ? winner : 0ul;
        }

        /// <summary>Opens a window for the item when the group's rule needs a decision. False: the caller gives the item the normal way.</summary>
        public static bool TryOpen(Player killer, WorldEntity owner, ItemInfo item)
        {
            if (owner == null || item == null)
                return false;

            PlayerGroup group = GroupManager.Instance.GetGroup(killer.CharacterId);
            if (group == null || group.Members.Count < 2)
                return false;

            LootRule rule = item.Entry.ItemQualityId < (uint)group.ThresholdQuality ? group.NormalRule : group.ThresholdRule;
            if (rule != LootRule.NeedBeforeGreed && rule != LootRule.Master)
                return false;

            List<Player> members = group.Members
                .Select(m => NetworkManager<WorldSession>.Instance.GetSession(s => s.Player != null && s.Player.CharacterId == m.CharacterId)?.Player)
                .Where(p => p != null && p.Map == killer.Map)
                .ToList();
            if (members.Count < 2)
                return false;

            var entry = new Pending
            {
                OwnerUnitId = owner.Guid,
                Item        = item,
                Rule        = rule,
                LeaderId    = group.LeaderId,
                Members     = members.Select(p => p.CharacterId).ToList(),
                Deadline    = DateTime.UtcNow.AddMilliseconds(RollTimeMs)
            };
            lock (gate)
            {
                entry.LootId = nextLootId++;
                pending.Add(entry.LootId, entry);
            }

            foreach (Player member in members)
            {
                member.Session.EnqueueMessageEncrypted(new ServerLootNotify
                {
                    OwnerUnitId = owner.Guid,
                    Items       = new List<LootItemModel> { Model(entry, member.CharacterId == group.LeaderId) }
                });
            }

            FailureLedger.Record("loot-window-opened", rule == LootRule.Master ? "master" : "need/greed", $"{members.Count} members");
            return true;
        }

        private static LootItemModel Model(Pending entry, bool isLeader)
        {
            var model = new LootItemModel
            {
                LootUnitId         = entry.LootId,
                Type               = 0u, // StaticItem (LootItemType, NexusForever game_rework)
                ItemId             = entry.Item.Id,
                Amount             = 1u,
                CanLoot            = entry.Rule == LootRule.Master ? isLeader : true,
                RequiresRoll       = entry.Rule == LootRule.NeedBeforeGreed,
                OnlyMasterLootable = entry.Rule == LootRule.Master,
                RollTime           = entry.Rule == LootRule.NeedBeforeGreed ? RollTimeMs : 0u,
                ItemQuality2Id     = entry.Item.Entry.ItemQualityId
            };
            if (entry.Rule == LootRule.Master && isLeader)
                model.MasterList = entry.Members.Select(Identity).ToList();
            return model;
        }

        private static Identity Identity(ulong characterId) => new Identity { RealmId = WorldServer.RealmId, Id = characterId };

        /// <summary>Need allowed: the item has no class requirement or the player's class meets it (GameLib.IsNeedRollAllowed, same rule as before).</summary>
        public static bool NeedAllowed(Player player, ItemInfo item) => item.Entry.ClassRequired == 0u || item.Entry.ClassRequired == (uint)player.Class;

        /// <summary>ClientLootRollAction: 0 need, 1 greed, 2 pass.</summary>
        public static void Roll(Player player, uint ownerUnitId, uint lootId, byte action)
        {
            lock (gate)
            {
                if (!pending.TryGetValue(lootId, out Pending entry) || entry.OwnerUnitId != ownerUnitId || entry.Rule != LootRule.NeedBeforeGreed)
                {
                    FailureLedger.Record("loot-roll-unknown", "no pending roll for this loot id");
                    return;
                }

                if (!entry.Members.Contains(player.CharacterId) || entry.Rolls.ContainsKey(player.CharacterId) || action > 2)
                {
                    FailureLedger.Record("loot-roll-refused", "not a member, already rolled or bad action");
                    return;
                }

                if (action == 0 && !NeedAllowed(player, entry.Item))
                {
                    FailureLedger.Record("loot-roll-need-refused", "class cannot use the item, counted as greed");
                    action = 1;
                }

                entry.Rolls[player.CharacterId] = action;
                FailureLedger.Record("loot-roll-action", action switch { 0 => "need", 1 => "greed", _ => "pass" });
            }
        }

        /// <summary>ClientLootAssignMaster: only the leader, only to a member that was in the window.</summary>
        public static void AssignMaster(Player player, uint ownerUnitId, uint lootId, ulong assignee)
        {
            lock (gate)
            {
                if (!pending.TryGetValue(lootId, out Pending entry) || entry.OwnerUnitId != ownerUnitId || entry.Rule != LootRule.Master)
                {
                    FailureLedger.Record("loot-master-unknown", "no pending master loot for this loot id");
                    return;
                }

                if (player.CharacterId != entry.LeaderId || !entry.Members.Contains(assignee))
                {
                    FailureLedger.Record("loot-master-refused", player.CharacterId != entry.LeaderId ? "not the leader" : "assignee not in the window");
                    return;
                }

                entry.MasterAssignee = assignee;
                FailureLedger.Record("loot-master-assign", "assigned");
            }
        }

        /// <summary>Decisions a bot still has to make: (owner, loot id, item, is master decision).</summary>
        public static List<(uint Owner, uint LootId, ItemInfo Item, bool Master, List<ulong> Members)> PendingFor(Player player)
        {
            lock (gate)
                return pending.Values
                    .Where(p => p.Rule == LootRule.NeedBeforeGreed ? p.Members.Contains(player.CharacterId) && !p.Rolls.ContainsKey(player.CharacterId)
                        : p.LeaderId == player.CharacterId && p.MasterAssignee == 0ul)
                    .Select(p => (p.OwnerUnitId, p.LootId, p.Item, p.Rule == LootRule.Master, p.Members.ToList()))
                    .ToList();
        }

        public static bool IsPending(uint lootId)
        {
            lock (gate)
                return pending.ContainsKey(lootId);
        }

        /// <summary>World thread, after the maps: resolve finished or expired windows and hand out the items.</summary>
        public static void Update()
        {
            List<Pending> ready;
            DateTime now = DateTime.UtcNow;
            lock (gate)
            {
                if (pending.Count == 0)
                    return;

                ready = pending.Values.Where(p => now >= p.Deadline
                    || (p.Rule == LootRule.NeedBeforeGreed && p.Members.All(m => p.Rolls.ContainsKey(m)))
                    || (p.Rule == LootRule.Master && p.MasterAssignee != 0ul)).ToList();
                foreach (Pending p in ready)
                    pending.Remove(p.LootId);
            }

            foreach (Pending entry in ready)
            {
                try
                {
                    Resolve(entry, now >= entry.Deadline);
                }
                catch (Exception ex)
                {
                    FailureLedger.Record("loot-window-error", ex.GetType().Name, ex.Message);
                }
            }
        }

        private static Player Online(ulong characterId) =>
            NetworkManager<WorldSession>.Instance.GetSession(s => s.Player != null && s.Player.CharacterId == characterId)?.Player;

        private static void Resolve(Pending entry, bool expired)
        {
            List<Player> members = entry.Members.Select(Online).Where(p => p != null).ToList();
            ulong winnerId = 0ul;
            var rolls = new List<ServerLootWinner.Roll>();

            if (entry.Rule == LootRule.NeedBeforeGreed)
            {
                foreach (ulong member in entry.Members.Where(m => !entry.Rolls.ContainsKey(m)))
                    entry.Rolls[member] = 2;
                if (expired)
                    FailureLedger.Record("loot-roll-timeout", "missing rolls counted as pass");

                foreach (var (characterId, action) in entry.Rolls)
                    foreach (Player member in members)
                        member.Session.EnqueueMessageEncrypted(new ServerLootRoll { LootUnitId = entry.LootId, Roller = Identity(characterId), ItemId = entry.Item.Id, Action = action });

                var rolled = entry.Rolls.Where(r => r.Value != 2).Select(r => (Id: r.Key, Need: r.Value == 0, Value: (uint)random.Next(1, 101))).ToList();
                rolls = rolled.Select(r => new ServerLootWinner.Roll { Roller = Identity(r.Id), Value = r.Value }).ToList();
                if (rolled.Count == 0)
                {
                    FailureLedger.Record("loot-roll-all-passed", $"item {entry.Item.Id} dropped");
                    return;
                }

                var best = rolled.OrderByDescending(r => r.Need).ThenByDescending(r => r.Value).First();
                winnerId = best.Id;
                foreach (Player member in members)
                    member.Session.EnqueueMessageEncrypted(new ServerLootWinner
                    {
                        LootUnitId = entry.LootId,
                        Winner     = new ServerLootWinner.Roll { Roller = Identity(best.Id), Value = best.Value },
                        ItemId     = entry.Item.Id,
                        Rolls      = rolls
                    });
                FailureLedger.Record("group-loot-rolled", $"{rolled.Count} rolls", best.Need ? "need won" : "greed won");
            }
            else
            {
                winnerId = entry.MasterAssignee != 0ul ? entry.MasterAssignee : entry.LeaderId;
                FailureLedger.Record(entry.MasterAssignee != 0ul ? "loot-master-granted" : "loot-master-timeout", entry.MasterAssignee != 0ul ? "assigned by the leader" : "unassigned, given to the leader");
            }

            Player winner = Online(winnerId);
            if (winner == null || !winner.Inventory.HasFreeSlot())
            {
                FailureLedger.Record("loot-window-no-room", winner == null ? "winner offline" : "winner bag full");
                return;
            }

            winner.Inventory.ItemCreate(InventoryLocation.Inventory, entry.Item, 1u, ItemUpdateReason.Loot);
            lock (gate)
            {
                if (granted.Count >= 1000)
                    granted.Remove(granted.Keys.Min());
                granted[entry.LootId] = winner.CharacterId;
            }
            LootItemModel item = Model(entry, false);
            foreach (Player member in members)
                member.Session.EnqueueMessageEncrypted(new ServerLootGrant { OwnerUnitId = entry.OwnerUnitId, LooterUnitId = winner.Guid, Item = item });
            FailureLedger.Record("loot", "group window item granted");
        }
    }
}
