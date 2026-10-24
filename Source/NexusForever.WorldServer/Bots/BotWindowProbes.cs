using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Game.Mail.Static;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Probes for the Contract Board, mail take-all and the group loot window, each through the real client message handlers
    /// (layouts recovered from the retail client, docs/PROTOCOL.md).
    /// </summary>
    public static partial class BotSystems
    {
        // board open (event 85), accept an offered contract at location 300, refuse a contract the board does not show, achieve, hand in through ClientQuestComplete
        private static void ContractProbe(Player me)
        {
            // contracts are level 50 content (Quest2 level 50); a system bot is raised to 50 once for the probe
            if (me.Level < 50u)
            {
                me.XpManager.SetLevel(50);
                FailureLedger.Record("contract-probe-level", "system bot raised to level 50 for the Contract Board");
                throw new ProbeSkip();
            }

            int slots = me.QuestManager.ActiveContractSlots();
            Expect(slots >= 3, $"contract slots {slots} (RewardPropertyPremiumModifier gives 3 or 4)");

            EntityHandler.HandleClientEntityInteraction(me.Session, BotPackets.Build<ClientEntityInteract>(w =>
            {
                w.Write(0u);
                w.Write(85u, 7u);
            }));

            DateTime now = DateTime.UtcNow;
            var board = PeriodicQuests.BoardOffer(1u, now);
            Expect(board.Count > 0, "the PvE board offers nothing");

            ClientQuestAccept Accept(uint quest) => BotPackets.Build<ClientQuestAccept>(w =>
            {
                w.Write(300u, 9u);
                w.Write(uint.MaxValue);
                w.Write((ushort)quest, 15u);
            });

            // a contract of the board's groups that is not on today's board must be refused
            var hidden = GameTableManager.Instance.Quest2.Entries
                .Where(q => q.PeriodicQuestGroupId != 0u && GameTableManager.Instance.PeriodicQuestGroup.GetEntry(q.PeriodicQuestGroupId)?.ContractTypeEnum == 1u
                    && PeriodicQuests.IsPooled(q.Id) && !board.Contains(q.Id) && me.QuestManager.GetQuestState((ushort)q.Id) is null or QuestState.Abandoned)
                .Select(q => q.Id).FirstOrDefault();
            if (hidden != 0u)
            {
                try
                {
                    QuestHandler.HandleQuestAccept(me.Session, Accept(hidden));
                }
                catch (Exception)
                {
                }
                Expect(me.QuestManager.GetQuestState((ushort)hidden) is not (QuestState.Accepted or QuestState.Achieved), $"contract {hidden} accepted although not on the board");
            }

            int active = me.QuestManager.ActiveContractCount();
            uint pick = board.FirstOrDefault(q =>
            {
                var info = GlobalQuestManager.Instance.GetQuestInfo((ushort)q);
                return info != null && me.QuestManager.CanAcceptWithoutGiver(info);
            });
            if (pick == 0u)
            {
                FailureLedger.Record("contract-probe-none", "no board contract acceptable for this bot", Game.Entity.QuestManager.LastRefusal ?? "");
                throw new ProbeSkip();
            }

            QuestHandler.HandleQuestAccept(me.Session, Accept(pick));
            Expect(me.QuestManager.GetQuestState((ushort)pick) == QuestState.Accepted, $"board contract {pick} not accepted ({Game.Entity.QuestManager.LastRefusal})");
            Expect(me.QuestManager.ActiveContractCount() == active + 1, "active contract count did not grow");

            me.QuestManager.QuestAchieve((ushort)pick);
            QuestHandler.HandleQuestComplete(me.Session, BotPackets.Build<ClientQuestComplete>(w =>
            {
                w.Write((ushort)pick, 15u);
                w.Write((ushort)0, 15u);
                w.Write(false);
            }));
            Expect(me.QuestManager.GetQuestState((ushort)pick) == QuestState.Completed, $"contract {pick} not handed in");
            FailureLedger.Record("contract-probe-ok", $"quest {pick}", $"{board.Count} on the board, {slots} slots");
        }

        private sealed class TakeAllState
        {
            public HashSet<ulong> Known;
            public DateTime Since = DateTime.UtcNow;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, TakeAllState> takeAll = new();

        // two system mails with items (1 and 2 attachments), a mailbox next to the bot, then ClientMailTakeAllFromSelection: every attachment lands in the bag
        private static void MailTakeAllProbe(Player me)
        {
            if (me.Map == null)
                throw new ProbeSkip();

            if (!takeAll.TryGetValue(me, out TakeAllState st))
            {
                EnsureRoom(me, 4u);
                st = new TakeAllState { Known = me.MailManager.MailIds.ToHashSet() };
                takeAll.Add(me, st);
                SpawnMailbox(me);
                uint creature = GameTableManager.Instance.Creature2.Entries.First(c => c.Id != 0u).Id;
                uint text = GameTableManager.Instance.LocalizedText.Entries.First(t => t.Id != 0u).Id;
                uint item = TestItemId();
                me.MailManager.SendMail(creature, DeliveryTime.Instant, text, text, new[] { item });
                me.MailManager.SendMail(creature, DeliveryTime.Instant, text, text, new[] { item, item });
                throw new ProbeSkip();
            }

            List<ulong> ids = me.MailManager.MailIds.Where(id => !st.Known.Contains(id) && me.MailManager.AttachmentCount(id) > 0).Take(2).ToList();
            Game.Entity.Mailbox box = FindMailbox(me);
            if (ids.Count < 2 || box == null)
            {
                if (DateTime.UtcNow - st.Since > TimeSpan.FromSeconds(240))
                {
                    // a setup miss (the bot was moved away from the spawned mailbox by another probe) says nothing about take-all itself
                    takeAll.Remove(me);
                    FailureLedger.Record("mailtakeall-setup-miss", $"{ids.Count}/2 new mails with items, mailbox {(box != null)}");
                    throw new ProbeSkip();
                }
                if (ids.Count == 2 && box == null)
                    SpawnMailbox(me);
                RunNext(me, "mailtakeall");
                throw new ProbeSkip();
            }

            takeAll.Remove(me);
            EnsureRoom(me, 4u);
            int expected = ids.Sum(id => me.MailManager.AttachmentCount(id));
            uint itemId = TestItemId();
            uint before = me.Inventory.GetItemCount(itemId);
            using var capture = new Capture(me);
            MailHandler.HandleMailTakeAllFromSelection(me.Session, BotPackets.Build<ClientMailTakeAllFromSelection>(w =>
            {
                w.Write((uint)ids.Count);
                foreach (ulong id in ids)
                    w.Write(id);
                w.Write(box.Guid);
            }));

            var answers = capture.Of<ServerMailTakeAttachment>();
            Expect(answers.Count == expected && answers.All(a => a.Result == Game.Static.GenericError.Ok), $"take-all answers {answers.Count}/{expected}: {string.Join(",", answers.Select(a => a.Result))}");
            Expect(ids.All(id => me.MailManager.AttachmentCount(id) == 0), "attachments left on the mails");
            Expect(me.Inventory.GetItemCount(itemId) >= before + (uint)expected, $"items {me.Inventory.GetItemCount(itemId)} after take-all, {before} before");
            foreach (ulong id in ids)
                me.MailManager.MailDelete(id);
        }

        private sealed class LootWindowState
        {
            public Player Other;
            public uint LootId;
            public uint ItemId;
            public uint Total;
            public bool Master;
            public DateTime Since = DateTime.UtcNow;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, LootWindowState> lootWindows = new();

        private static uint GroupCount(Player a, Player b, uint item) => a.Inventory.GetItemCount(item) + b.Inventory.GetItemCount(item);

        // a two bot group: a need/greed window (notify to both, need and greed through ClientLootRollAction), resolved by the world loop;
        // then a master loot window the leader assigns to the other bot through ClientLootAssignMaster
        private static void LootWindowProbe(Player me)
        {
            var groups = GroupManager.Instance;
            if (lootWindows.TryGetValue(me, out LootWindowState st))
            {
                if (LootWindow.IsPending(st.LootId))
                {
                    if (DateTime.UtcNow - st.Since < TimeSpan.FromSeconds(LootWindow.RollTimeMs / 1000 + 30))
                        throw new ProbeSkip();
                    lootWindows.Remove(me);
                    groups.Leave(me, groups.GetGroup(me.CharacterId)?.Id ?? 0ul, true);
                    throw new ProbeFailure("loot window never resolved");
                }

                ulong winner = LootWindow.WinnerOf(st.LootId);
                if (st.Master)
                {
                    lootWindows.Remove(me);
                    Expect(winner == st.Other.CharacterId, $"master loot went to {winner}, not the assignee");
                    groups.Leave(me, groups.GetGroup(me.CharacterId)?.Id ?? 0ul, true);
                    return;
                }

                Expect(winner == me.CharacterId, $"need/greed window winner {winner}: the need roll did not win against greed");
                PlayerGroup g = groups.GetGroup(me.CharacterId);
                if (g == null)
                {
                    lootWindows.Remove(me);
                    throw new ProbeFailure("group gone before the master window");
                }

                g.NormalRule = LootRule.Master;
                g.ThresholdRule = LootRule.Master;
                var info = ItemManager.Instance.GetItemInfo(st.ItemId);
                st.Total = GroupCount(me, st.Other, st.ItemId);
                using (var capture = new Capture(me))
                {
                    if (st.Other.Map != me.Map || !LootWindow.TryOpen(me, me, info))
                    {
                        lootWindows.Remove(me);
                        groups.Leave(me, g.Id, true);
                        FailureLedger.Record("lootwindow-probe-partner-left", "partner left the map before the master window");
                        throw new ProbeSkip();
                    }
                    var notify = capture.Of<ServerLootNotify>().LastOrDefault();
                    Expect(notify != null && notify.Items[0].OnlyMasterLootable && notify.Items[0].MasterList.Count == 2, "leader got no master loot notify with both members");
                    st.LootId = notify.Items[0].LootUnitId;
                }
                st.Master = true;
                st.Since = DateTime.UtcNow;
                var assign = BotPackets.Build<ClientLootAssignMaster>(w =>
                {
                    w.Write(me.Guid);
                    w.Write(st.LootId);
                    w.Write(WorldServer.RealmId, 14u);
                    w.Write(st.Other.CharacterId);
                });
                InstanceHandler.HandleLootAssignMaster(st.Other.Session, assign); // not the leader: refused
                InstanceHandler.HandleLootAssignMaster(me.Session, assign);
                throw new ProbeSkip();
            }

            Player other = NearPartner(me) ?? throw new ProbeSkip();
            if (groups.GetGroup(me.CharacterId) != null || groups.GetGroup(other.CharacterId) != null || lootWindows.TryGetValue(other, out _))
                throw new ProbeSkip();

            EnsureRoom(me, 2u);
            EnsureRoom(other, 2u);
            var entry = GameTableManager.Instance.Item.Entries.FirstOrDefault(i => i.ItemQualityId == 3u && i.ClassRequired == 0u && i.Item2FamilyId == 1u);
            var item = entry == null ? null : ItemManager.Instance.GetItemInfo(entry.Id);
            if (item == null)
                throw new ProbeSkip();

            Expect(groups.Invite(me, other.Name) == GroupInviteResult.Sent, "invite not sent");
            PlayerGroup group = groups.GetGroup(me.CharacterId);
            Expect(group != null, "no group after the invite");
            groups.InviteResponse(other, group.Id, true);
            Expect(groups.GetGroup(other.CharacterId)?.Id == group.Id, "partner not in the group");
            group.NormalRule = LootRule.NeedBeforeGreed;
            group.ThresholdRule = LootRule.NeedBeforeGreed;

            var state = new LootWindowState { Other = other, ItemId = item.Id, Total = GroupCount(me, other, item.Id) };
            using (var mine = new Capture(me))
            using (var theirs = new Capture(other))
            {
                Expect(LootWindow.TryOpen(me, me, item), "need/greed window not opened");
                var notify = mine.Of<ServerLootNotify>().LastOrDefault();
                Expect(notify != null && notify.Items.Count == 1 && notify.Items[0].RequiresRoll && notify.Items[0].RollTime == LootWindow.RollTimeMs, "no roll notify for the looter");
                Expect(theirs.Of<ServerLootNotify>().Any(n => n.Items[0].LootUnitId == notify.Items[0].LootUnitId), "partner got no roll notify");
                state.LootId = notify.Items[0].LootUnitId;
            }

            ClientLootRollAction Roll(byte action) => BotPackets.Build<ClientLootRollAction>(w =>
            {
                w.Write(me.Guid);
                w.Write(state.LootId);
                w.Write(action, 2u);
            });
            InstanceHandler.HandleLootRollAction(me.Session, Roll(0));
            InstanceHandler.HandleLootRollAction(other.Session, Roll(1));
            lootWindows.Add(me, state);
            throw new ProbeSkip();
        }

        /// <summary>
        /// Bots in a group answer open loot windows through the client messages: need when their class can use the item, else greed;
        /// a leader assigns master loot to the first member that could need it (else to itself).
        /// </summary>
        public static void AnswerLoot(Player me)
        {
            foreach (var (owner, lootId, item, master, members) in LootWindow.PendingFor(me))
            {
                if (lootWindows.TryGetValue(me, out LootWindowState probe) && probe.LootId == lootId)
                    continue;

                if (master)
                {
                    Player to = members.Select(m => NexusForever.Shared.Network.NetworkManager<Network.WorldSession>.Instance
                            .GetSession(s => s.Player != null && s.Player.CharacterId == m)?.Player)
                        .FirstOrDefault(p => p != null && LootWindow.NeedAllowed(p, item) && p.Inventory.HasFreeSlot()) ?? me;
                    InstanceHandler.HandleLootAssignMaster(me.Session, BotPackets.Build<ClientLootAssignMaster>(w =>
                    {
                        w.Write(owner);
                        w.Write(lootId);
                        w.Write(WorldServer.RealmId, 14u);
                        w.Write(to.CharacterId);
                    }));
                }
                else
                {
                    byte action = (byte)(!me.Inventory.HasFreeSlot() ? 2 : LootWindow.NeedAllowed(me, item) ? 0 : 1);
                    InstanceHandler.HandleLootRollAction(me.Session, BotPackets.Build<ClientLootRollAction>(w =>
                    {
                        w.Write(owner);
                        w.Write(lootId);
                        w.Write(action, 2u);
                    }));
                }
            }
        }
    }
}
