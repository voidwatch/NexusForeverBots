using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Quest.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Quest
{
    public sealed class GlobalQuestManager : Singleton<GlobalQuestManager>, IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// <see cref="DateTime"/> representing the next daily reset.
        /// </summary>
        public DateTime NextDailyReset => Groups.InstanceLockouts.NextDailyReset(Groups.InstanceLockouts.Now());

        /// <summary>
        /// <see cref="DateTime"/> representing the next weekly reset.
        /// </summary>
        public DateTime NextWeeklyReset => Groups.InstanceLockouts.NextWeeklyReset(Groups.InstanceLockouts.Now());

        private ImmutableDictionary<ushort, QuestInfo> questInfoStore;
        private ImmutableDictionary<ushort, ImmutableList<uint>> questGiverStore;
        private ImmutableDictionary<ushort, ImmutableList<uint>> questReceiverStore;

        private ImmutableDictionary<uint, CommunicatorMessage> communicatorStore;
        private ImmutableDictionary<ushort, ImmutableList<CommunicatorMessage>> communicatorQuestStore;
        private ImmutableDictionary<(ushort /*questId*/, QuestState), ImmutableList<CommunicatorMessage>> communicatorQuestStateTriggerStore;

        private GlobalQuestManager()
        {
        }

        public void Initialise()
        {
            Stopwatch sw = Stopwatch.StartNew();

            InitialiseQuestInfo();
            InitialiseQuestRelations();

            InitialiseCommunicatorEntries();
            InitialiseCommunicatorQuests();
            InitialiseCommunicatorQuestStateTriggers();

            log.Info($"Cached {questInfoStore.Count} quests in {sw.ElapsedMilliseconds}ms.");
        }

        private void InitialiseQuestInfo()
        {
            var builder = ImmutableDictionary.CreateBuilder<ushort, QuestInfo>();
            foreach (Quest2Entry entry in GameTableManager.Instance.Quest2.Entries)
                builder.Add((ushort)entry.Id, new QuestInfo(entry));

            questInfoStore = builder.ToImmutable();
        }

        private void InitialiseQuestRelations()
        {
            var questGivers = new Dictionary<ushort, List<uint>>();
            var questReceivers = new Dictionary<ushort, List<uint>>();

            foreach (Creature2Entry entry in GameTableManager.Instance.Creature2.Entries)
            {
                // ReSharper disable once PossibleInvalidCastExceptionInForeachLoop
                foreach (ushort questId in entry.QuestIdGiven.Where(q => q != 0u))
                {
                    if (!questGivers.ContainsKey(questId))
                        questGivers.Add(questId, new List<uint>());

                    questGivers[questId].Add(entry.Id);
                }

                // ReSharper disable once PossibleInvalidCastExceptionInForeachLoop
                foreach (ushort questId in entry.QuestIdReceive.Where(q => q != 0u))
                {
                    if (!questReceivers.ContainsKey(questId))
                        questReceivers.Add(questId, new List<uint>());

                    questReceivers[questId].Add(entry.Id);
                }
            }

            LoadResearchLinks(questGivers, questReceivers);

            questGiverStore = questGivers.ToImmutableDictionary(k => k.Key, v => v.Value.ToImmutableList());
            questReceiverStore = questReceivers.ToImmutableDictionary(k => k.Key, v => v.Value.ToImmutableList());
        }

        /// <summary>
        /// Giver/receiver links shown on archived Jabbithole quest pages that Creature2.QuestIdGiven/QuestIdReceive lack (tradeskill work orders from
        /// the Work Order Boards, a few story NPCs). File: data/research/quest-links.tsv from scripts/research/jh_quest_links.py (docs/ASSUMPTIONS.md).
        /// </summary>
        private static void LoadResearchLinks(Dictionary<ushort, List<uint>> givers, Dictionary<ushort, List<uint>> receivers)
        {
            string path = System.IO.Path.Combine(Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research", "quest-links.tsv");
            if (!System.IO.File.Exists(path))
                return;

            int added = 0;
            try
            {
                foreach (string line in System.IO.File.ReadLines(path))
                {
                    string[] f = line.Split('\t');
                    if (line.StartsWith('#') || f.Length < 3 || !ushort.TryParse(f[0], out ushort questId) || !uint.TryParse(f[2], out uint creature))
                        continue;
                    if (GameTableManager.Instance.Creature2.GetEntry(creature) == null || GameTableManager.Instance.Quest2.GetEntry(questId) == null)
                        continue;

                    var store = f[1] == "giver" ? givers : f[1] == "receiver" ? receivers : null;
                    if (store == null)
                        continue;
                    if (!store.TryGetValue(questId, out List<uint> list))
                        store[questId] = list = new List<uint>();
                    if (!list.Contains(creature))
                    {
                        list.Add(creature);
                        added++;
                    }
                }
            }
            catch (Exception e)
            {
                log.Error(e, $"Failed to read {path}.");
            }

            log.Info($"Added {added} researched quest giver/receiver link(s) from {path}.");
        }

        private void InitialiseCommunicatorEntries()
        {
            var builder = ImmutableDictionary.CreateBuilder<uint, CommunicatorMessage>();
            foreach (CommunicatorMessagesEntry entry in GameTableManager.Instance.CommunicatorMessages.Entries)
            {
                var communicator = new CommunicatorMessage(entry);
                builder.Add(communicator.Id, communicator);
            }

            communicatorStore = builder.ToImmutable();
        }

        private void InitialiseCommunicatorQuests()
        {
            var builder = new Dictionary<ushort, List<CommunicatorMessage>>();
            foreach (CommunicatorMessagesEntry entry in GameTableManager.Instance.CommunicatorMessages.Entries
                .Where(e => e.QuestIdDelivered != 0u))
            {
                CommunicatorMessage communicator = communicatorStore[entry.Id];
                if (!builder.ContainsKey(communicator.QuestId))
                    builder.Add(communicator.QuestId, new List<CommunicatorMessage>());

                builder[communicator.QuestId].Add(communicator);
            }

            communicatorQuestStore = builder.ToImmutableDictionary(e => e.Key, e => e.Value.ToImmutableList());
        }

        private void InitialiseCommunicatorQuestStateTriggers()
        {
            var builder = new Dictionary<(ushort, QuestState), List<CommunicatorMessage>>();
            foreach (CommunicatorMessagesEntry entry in GameTableManager.Instance.CommunicatorMessages.Entries
                .Where(e => e.QuestIdDelivered != 0u))
            {
                foreach ((ushort QuestId, QuestState QuestState) p in
                    entry.Quests.Zip(entry.States, (a, b) => ((ushort)a, (QuestState)b)))
                {
                    if (p.QuestId == 0)
                        continue;

                    if (p.QuestId == entry.QuestIdDelivered)
                        continue;

                    if (!builder.ContainsKey(p))
                        builder.Add(p, new List<CommunicatorMessage>());

                    CommunicatorMessage communicator = communicatorStore[entry.Id];
                    builder[p].Add(communicator);
                }
            }

            communicatorQuestStateTriggerStore = builder.ToImmutableDictionary(e => e.Key, e => e.Value.ToImmutableList());
        }

        public void Update(double lastTick)
        {
            // reset times are computed from the clock, nothing to roll over
        }

        /// <summary>
        /// Return <see cref="QuestInfo"/> for supplied quest.
        /// </summary>
        public QuestInfo GetQuestInfo(ushort questId)
        {
            return questInfoStore.TryGetValue(questId, out QuestInfo questInfo) ? questInfo : null;
        }

        /// <summary>
        /// Return every known quest.
        /// </summary>
        public IEnumerable<QuestInfo> GetAllQuestInfo()
        {
            return questInfoStore.Values;
        }

        /// <summary>
        /// Return a collection of creatures that start the supplied quest.
        /// </summary>
        public IEnumerable<uint> GetQuestGivers(ushort questId)
        {
            return questGiverStore.TryGetValue(questId, out ImmutableList<uint> creatureIds) ? creatureIds : Enumerable.Empty<uint>();
        }

        /// <summary>
        /// Return a collection of creatures that finish the supplied quest.
        /// </summary>
        public IEnumerable<uint> GetQuestReceivers(ushort questId)
        {
            return questReceiverStore.TryGetValue(questId, out ImmutableList<uint> creatureIds) ? creatureIds : Enumerable.Empty<uint>();
        }

        /// <summary>
        /// Return a collection of <see cref="CommunicatorMessage"/>'s that start the supplied quest.
        /// </summary>
        public IEnumerable<CommunicatorMessage> GetQuestCommunicatorMessages(ushort questId)
        {
            return communicatorQuestStore.TryGetValue(questId, out ImmutableList<CommunicatorMessage> creatureIds)
                ? creatureIds : Enumerable.Empty<CommunicatorMessage>();
        }

        /// <summary>
        /// Return a collection of <see cref="CommunicatorMessage"/>'s that are triggered when a quest hits a certain state.
        /// </summary>
        public IEnumerable<CommunicatorMessage> GetQuestCommunicatorQuestStateTriggers(ushort questId, QuestState state)
        {
            return communicatorQuestStateTriggerStore.TryGetValue((questId, state), out ImmutableList<CommunicatorMessage> triggers)
                ? triggers : Enumerable.Empty<CommunicatorMessage>();
        }
    }
}
