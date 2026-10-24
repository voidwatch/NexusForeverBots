using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Database.Character;
using NexusForever.Database.Character.Model;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Achievement.Static;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Prerequisite;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Game.Reputation;
using NexusForever.WorldServer.Game.Reputation.Static;
using NexusForever.WorldServer.Game.Static;
using NexusForever.WorldServer.Network.Message.Model;
using NLog;

namespace NexusForever.WorldServer.Game.Entity
{
    public class QuestManager : ISaveCharacter, IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        [Flags]
        private enum GetQuestFlags
        {
            Completed = 0x01,
            Inactive  = 0x02,
            Active    = 0x04,
            All       = Completed | Inactive | Active
        }

        private readonly Player player;

        private readonly Dictionary<ushort, Quest.Quest> completedQuests = new();
        private readonly Dictionary<ushort, Quest.Quest> inactiveQuests = new();
        private readonly Dictionary<ushort, Quest.Quest> activeQuests = new();

        /// <summary>
        /// Create a new <see cref="QuestManager"/> from existing <see cref="CharacterModel"/> database model.
        /// </summary>
        public QuestManager(Player owner, CharacterModel model)
        {
            player = owner;

            foreach (CharacterQuestModel questModel in model.Quest)
            {
                QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(questModel.QuestId);
                if (info == null)
                {
                    log.Error($"Player {player.CharacterId} has an invalid quest {questModel.QuestId}!");
                    continue;
                }

                var quest = new Quest.Quest(player, info, questModel);
                switch (quest.State)
                {
                    case QuestState.Completed:
                        completedQuests.Add(quest.Id, quest);
                        break;
                    case QuestState.Botched:
                    case QuestState.Ignored:
                    case QuestState.Mentioned:
                        inactiveQuests.Add(quest.Id, quest);
                        break;
                    case QuestState.Accepted:
                    case QuestState.Achieved:
                        activeQuests.Add(quest.Id, quest);
                        break;
                    default:
                        // an Abandoned (or Unknown) row is a delete that never reached the database. Left out of the lists, the game did not know
                        // the quest and the next accept inserted it again (duplicate key). Held like an abandon in this session: delete pending.
                        Diagnostics.FailureLedger.Record("quest-row-stale-state", $"state {quest.State} row deleted at the next save", $"character {player.CharacterId} quest {quest.Id}");
                        quest.EnqueueDelete(true);
                        activeQuests.Add(quest.Id, quest);
                        break;
                }
            }
        }

        public void Save(CharacterContext context)
        {
            foreach (ushort questId in activeQuests.Keys.Where(k => inactiveQuests.ContainsKey(k) || completedQuests.ContainsKey(k))
                .Concat(inactiveQuests.Keys.Where(completedQuests.ContainsKey)).Distinct().ToList())
            {
                string where = DescribeQuest(questId);
                Diagnostics.FailureLedger.Record("quest-in-two-lists", $"{where}: {GetTrace(questId)}", $"character {player.CharacterId} quest {questId}");
                CharacterSave.Log($"two lists character {player.CharacterId} quest {questId} [{where}]: {GetTrace(questId)}");
            }

            // active first: when a quest id is held twice, the active one is the newest and the other waits for the next save
            savedThisPass.Clear();
            foreach (Quest.Quest quest in activeQuests.Values.ToList())
            {
                bool delete = quest.PendingDelete;
                if (SaveQuest(context, quest) && delete)
                    activeQuests.Remove(quest.Id);
            }

            foreach (Quest.Quest quest in inactiveQuests.Values.ToList())
            {
                bool delete = quest.PendingDelete;
                if (SaveQuest(context, quest) && delete)
                    inactiveQuests.Remove(quest.Id);
            }

            foreach (Quest.Quest quest in completedQuests.Values)
                SaveQuest(context, quest);
        }

        private volatile bool resyncPending;

        /// <summary>
        /// A quest batch failed: its inserts and deletes never reached the database, so the next save rewrites all quests instead of the changes.
        /// </summary>
        public void RequestResync() => resyncPending = true;

        public bool ResyncPending => resyncPending;

        /// <summary>
        /// The quests as the game holds them (quests queued for delete are left out) for <see cref="CharacterSave.Resync"/>; pending changes are dropped.
        /// </summary>
        public List<CharacterSave.QuestRow> TakeResyncSnapshot()
        {
            resyncPending = false;
            return BuildRows(true);
        }

        /// <summary>
        /// The quest rows the database should hold once the pending changes are saved (for a check against the database).
        /// </summary>
        public List<CharacterSave.QuestRow> Snapshot() => BuildRows(false);

        private List<CharacterSave.QuestRow> BuildRows(bool resync)
        {
            var rows = new List<CharacterSave.QuestRow>();
            var seen = new HashSet<ushort>();
            foreach (Dictionary<ushort, Quest.Quest> list in new[] { activeQuests, inactiveQuests, completedQuests })
            {
                foreach (Quest.Quest quest in list.Values.ToList())
                {
                    if (quest.PendingDelete)
                    {
                        if (resync)
                        {
                            list.Remove(quest.Id);
                            quest.DiscardPendingSave();
                        }
                        else if (seen.Add(quest.Id))
                            rows.Add(new CharacterSave.QuestRow { QuestId = quest.Id, Pending = true, Objectives = new() });
                        continue;
                    }

                    if (resync)
                        quest.DiscardPendingSave();
                    if (!seen.Add(quest.Id))
                        continue;

                    if (resync)
                        Trace(quest.Id, "resync");
                    rows.Add(new CharacterSave.QuestRow
                    {
                        QuestId    = quest.Id,
                        State      = (byte)quest.State,
                        Flags      = (byte)quest.Flags,
                        Timer      = quest.Timer,
                        Reset      = quest.Reset,
                        Objectives = quest.Select(o => (o.Index, o.Progress)).ToList(),
                        Pending    = !resync && quest.PendingSave
                    });
                }
            }

            return rows;
        }

        private readonly HashSet<ushort> savedThisPass = new();

        private bool SaveQuest(CharacterContext context, Quest.Quest quest)
        {
            // a quest held in two lists would put its rows in the batch twice, which EF refuses for the whole batch (quest-in-two-lists is ledgered above)
            if (!savedThisPass.Add(quest.Id))
                return false;

            if (quest.PendingDelete)
                Trace(quest.Id, quest.PendingCreate ? "save:drop" : "save:D");
            else if (quest.PendingCreate)
                Trace(quest.Id, "save:I");
            try
            {
                quest.Save(context);
            }
            catch (InvalidOperationException)
            {
                string tracked = string.Join(",", context.ChangeTracker.Entries<CharacterQuestObjectiveModel>()
                    .Where(e => e.Entity.QuestId == quest.Id).Select(e => $"{e.Entity.Index}:{e.State}"));
                string trackedQuest = string.Join(",", context.ChangeTracker.Entries<CharacterQuestModel>()
                    .Where(e => e.Entity.QuestId == quest.Id).Select(e => e.State.ToString()));
                CharacterSave.Log($"tracking character {player.CharacterId} quest {quest.Id} [{DescribeQuest(quest.Id)}] objectives {string.Join(",", quest.Select(o => o.Index))} tracked quest [{trackedQuest}] objectives [{tracked}]: {GetTrace(quest.Id)}");
                throw;
            }
            return true;
        }

        // recent transitions per quest (bounded), written to the ledger when a character save fails
        private readonly Dictionary<ushort, string> questTrace = new();

        private void Trace(ushort questId, string op)
        {
            questTrace.TryGetValue(questId, out string trace);
            trace = trace == null ? op : trace + ">" + op;
            if (trace.Length > 120)
                trace = trace[^120..];
            questTrace[questId] = trace;
        }

        /// <summary>Transitions of the quest since login ("loaded" when it came from the database and has not changed since).</summary>
        public string GetTrace(ushort questId) => questTrace.TryGetValue(questId, out string trace) ? trace : "loaded";

        /// <summary>
        /// Where the quest sits in memory: the lists holding it and its state, e.g. "active:Accepted".
        /// </summary>
        public string DescribeQuest(ushort questId)
        {
            var parts = new List<string>();
            if (activeQuests.TryGetValue(questId, out Quest.Quest quest))
                parts.Add($"active:{quest.State}");
            if (inactiveQuests.TryGetValue(questId, out quest))
                parts.Add($"inactive:{quest.State}");
            if (completedQuests.TryGetValue(questId, out quest))
                parts.Add($"completed:{quest.State}");
            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        private double areaTimer;
        private ulong sentPeriodicSeed;
        private DateTime contractBoardOpened = DateTime.MinValue;

        /// <summary>The player opened a Contract Board (interaction event 85).</summary>
        public void ContractBoardOpened(uint unitId)
        {
            contractBoardOpened = DateTime.UtcNow;
            Diagnostics.FailureLedger.Record("contract-board-open", "interaction event 85");
        }

        private ServerQuestContractGoodQualityChanged GoodContracts(DateTime now)
        {
            var message = new ServerQuestContractGoodQualityChanged();
            for (uint type = 1u; type < 3u; type++)
                message.GoodQuest[type] = PeriodicQuests.GoodContract(type, now);
            return message;
        }

        public void Update(double lastTick)
        {
            areaTimer -= lastTick;
            if (areaTimer <= 0d)
            {
                areaTimer = 1d;
                UpdateAreaObjectives();

                // daily reset: a new periodic seed makes the client redraw its rotating dailies and contracts (client handler redraws only when the seed changes)
                ulong seed = PeriodicQuests.Seed(DateTime.UtcNow);
                if (sentPeriodicSeed != 0ul && seed != sentPeriodicSeed)
                {
                    sentPeriodicSeed = seed;
                    player.Session.EnqueueMessageEncrypted(new ServerQuestPeriodicReset { Seed = seed });
                    player.Session.EnqueueMessageEncrypted(GoodContracts(DateTime.UtcNow));
                    Diagnostics.FailureLedger.Record("periodic-reset-sent", "new periodic seed after the daily reset");
                }
            }

            var botchedQuests = new List<Quest.Quest>();
            foreach (Quest.Quest quest in activeQuests.Values)
            {
                quest.Update(lastTick);
                if (quest.State == QuestState.Botched)
                    botchedQuests.Add(quest);
            }

            foreach (Quest.Quest quest in botchedQuests)
            {
                activeQuests.Remove(quest.Id);
                inactiveQuests.Add(quest.Id, quest);
                Trace(quest.Id, "botch");

                log.Trace($"Failed to complete quest {quest.Id} before the timer expired!");
            }
        }

        public void SendInitialPackets()
        {
            DateTime now = DateTime.UtcNow;
            player.Session.EnqueueMessageEncrypted(new ServerQuestInit
            {
                Completed = completedQuests.Values
                    .Select(q => new ServerQuestInit.QuestComplete
                    {
                        QuestId        = q.Id,
                        CompletedToday = now < q.Reset
                    }).ToList(),
                Inactive = inactiveQuests.Values
                    .Select(q => new ServerQuestInit.QuestInactive
                    {
                        QuestId = q.Id,
                        State   = q.State
                    }).ToList(),
                Active = activeQuests.Values
                    .Where(q => !q.PendingDelete)
                    .Select(q => new ServerQuestInit.QuestActive
                    {
                        QuestId        = q.Id,
                        State          = q.State,
                        RandomResultId = q.RandomResultId,
                        Flags          = q.Flags,
                        Timer      = q.Timer ?? 0u,
                        Objectives = q.Select(o => new ServerQuestInit.QuestActive.Objective
                        {
                            Progress = o.Progress,
                            Timer    = 0u
                        }).ToList()
                    }).ToList(),
                DailyRandomSeed = sentPeriodicSeed = PeriodicQuests.Seed(now)
            });
            player.Session.EnqueueMessageEncrypted(GoodContracts(now));
        }

        /// <summary>
        /// Objective progress as the client's prerequisite types 22/23/33-36 read it: a completed quest gives the objective's count
        /// (100 for percent objectives: types 2, 8, 15, 16, 46 with a count above 1 and without flag 0x200), a quest in the log its progress, else 0.
        /// </summary>
        public uint ObjectiveProgressForPrerequisite(ushort questId, uint index)
        {
            if (completedQuests.ContainsKey(questId) && !activeQuests.ContainsKey(questId))
            {
                QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(questId);
                QuestObjectiveEntry entry = info?.Objectives.ElementAtOrDefault((int)index)?.Entry;
                if (entry == null || entry.Count == 0u)
                    return 0u;
                bool percent = entry.Type is 2u or 8u or 15u or 16u or 46u && entry.Count > 1u && (entry.Flags & 0x200u) == 0u;
                return percent ? 100u : entry.Count;
            }

            if (activeQuests.TryGetValue(questId, out Quest.Quest quest))
                return quest.FirstOrDefault(o => o.Index == index)?.Progress ?? 0u;

            return 0u;
        }

        /// <summary>
        /// Whether the objective at the (0 based) index of a quest the player has taken or finished is complete.
        /// </summary>
        public bool IsObjectiveComplete(ushort questId, uint index)
        {
            Quest.Quest quest = GetQuest(questId);
            return quest != null && quest.Any(o => o.Index == index && o.IsComplete());
        }

        /// <summary>
        /// Return <see cref="QuestState"/> for supplied quest.
        /// </summary>
        public QuestState? GetQuestState(ushort questId)
        {
            return GetQuest(questId)?.State;
        }

        /// <summary>
        /// Advance the objective at the supplied position of an active quest, used by the QuestAdvanceObjective spell effect
        /// (quest id, objective index and amount come straight from the effect data). Returns false when the quest or objective is not there.
        /// </summary>
        public bool AdvanceObjective(ushort questId, byte index, uint amount)
        {
            Quest.Quest quest = GetQuest(questId, GetQuestFlags.Active);
            Quest.QuestObjective objective = quest?.FirstOrDefault(o => o.Index == index);
            if (objective == null)
                return false;

            quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, amount);
            return true;
        }

        private Quest.Quest GetQuest(ushort questId, GetQuestFlags flags = GetQuestFlags.All)
        {
            if ((flags & GetQuestFlags.Active) != 0
                && activeQuests.TryGetValue(questId, out Quest.Quest quest))
                return quest;

            if ((flags & GetQuestFlags.Inactive) != 0
                && inactiveQuests.TryGetValue(questId, out quest))
                return quest;

            if ((flags & GetQuestFlags.Completed) != 0
                && completedQuests.TryGetValue(questId, out quest))
                return quest;

            return null;
        }

        /// <summary>
        /// Mention a <see cref="Quest"/> from supplied quest ID, skipping any prerequisites checks.
        /// </summary>
        /// <summary>
        /// Test hook for the bot driver: the reset time of a completed repeatable quest.
        /// </summary>
        public DateTime? GetQuestReset(ushort questId) => GetQuest(questId, GetQuestFlags.Completed)?.Reset;

        public void SetQuestReset(ushort questId, DateTime reset)
        {
            Quest.Quest quest = GetQuest(questId, GetQuestFlags.Completed);
            if (quest != null)
                quest.Reset = reset;
        }

        public void QuestMention(ushort questId)
        {
            QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(questId);
            if (info == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            if (DisableManager.Instance.IsDisabled(DisableType.Quest, questId))
            {
                player.SendSystemMessage($"Unable to add quest {questId} because it is disabled.");
                return;
            }

            if (GetQuest(questId) != null)
                return;

            QuestMention(info);
        }

        /// <summary>
        /// Mention a <see cref="Quest"/> from supplied <see cref="QuestInfo"/>, skipping any prerequisites checks.
        /// </summary>
        public void QuestMention(QuestInfo info)
        {
            Quest.Quest quest = GetQuest((ushort)info.Entry.Id);
            Trace((ushort)info.Entry.Id, quest == null ? "mention:new" : $"mention:{quest.State}{(quest.PendingDelete ? "-pd" : "")}{(quest.PendingCreate ? "-pc" : "")}@{DescribeQuest(quest.Id)}");
            if (quest == null)
                quest = new Quest.Quest(player, info);
            else
                QuestRemove(quest);

            quest.State = QuestState.Mentioned;
            inactiveQuests.Add((ushort)info.Entry.Id, quest);

            log.Trace($"Mentioned new quest {info.Entry.Id}.");
        }

        /// <summary>
        /// Add a <see cref="Quest"/> from supplied id, optionally supplying <see cref="Item"/> which was used to start the quest.
        /// </summary>
        public void QuestAdd(ushort questId, Item item)
        {
            QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(questId);
            if (info == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            if (DisableManager.Instance.IsDisabled(DisableType.Quest, questId))
            {
                player.SendSystemMessage($"Unable to add quest {questId} because it is disabled.");
                return;
            }

            Quest.Quest quest = GetQuest(questId);
            QuestAdd(info, quest, item);
        }

        private void QuestAdd(QuestInfo info, Quest.Quest quest, Item item)
        {
            if (quest?.State is QuestState.Accepted or QuestState.Achieved)
                throw new QuestException($"Player {player.CharacterId} tried to start quest {info.Entry.Id} which is already in progress!");

            // if quest has already been completed make sure it's repeatable and the reset period has elapsed
            if (quest?.State == QuestState.Completed)
            {
                if (info.Entry.QuestRepeatPeriodEnum == 0u)
                    throw new QuestException($"Player {player.CharacterId} tried to start quest {info.Entry.Id} which they have already completed!");

                DateTime? resetTime = GetQuest((ushort)info.Entry.Id, GetQuestFlags.Completed).Reset;
                if (DateTime.UtcNow < resetTime)
                    throw new QuestException($"Player {player.CharacterId} tried to start quest {info.Entry.Id} which hasn't reset yet!");
            }

            if (item != null)
            {
                if (info.Entry.Id != item.Info.Entry.Quest2IdActivation)
                    throw new QuestException($"Player {player.CharacterId} tried to start quest {info.Entry.Id} from invalid item {item.Info.Entry.Id}!");

                // TODO: consume charge
            }
            else
            {
                // make sure the player is in range of a quest giver or they are eligible for a communicator message that starts the quest
                // contracts are taken at the Contract Board (interaction event 85), a UI over the tables: no giver creature exists for them (docs/ASSUMPTIONS.md)
                if (info.IsContract())
                {
                }
                else if (!GlobalQuestManager.Instance.GetQuestGivers((ushort)info.Entry.Id)
                        .Any(c => player.GetVisibleCreature<WorldEntity>(c).Any())
                    && !GlobalQuestManager.Instance.GetQuestCommunicatorMessages((ushort)info.Entry.Id)
                        .Any(m => m.Meets(player)))
                    throw new QuestException($"Player {player.CharacterId} tried to start quest {info.Entry.Id} without quest giver!");
            }

            // server doesn't send an error message for prerequisites since the client does the exact same checks
            // it's assumed that a player could never get here without cheating in some way
            if (!MeetsPrerequisites(info))
                throw new QuestException($"Player {player.CharacterId} tried to start quest {info.Entry.Id} without meeting the prerequisites!");

            if (info.IsContract())
                Diagnostics.FailureLedger.Record("contract-accepted", $"quest {info.Entry.Id}",
                    DateTime.UtcNow - contractBoardOpened < TimeSpan.FromMinutes(10) ? "board open" : "board not opened (bot or late accept)");
            QuestAdd(info);
        }

        /// <summary>
        /// Equipped items changed: offer the first unfinished quest of each equipped item's imbuement chain (<see cref="ItemImbuements"/>).
        /// Called from the player update, never from the inventory move itself.
        /// </summary>
        public void CheckImbuements()
        {
            foreach (Item item in player.Inventory.GetItems(InventoryLocation.Equipped))
            {
                uint imbuement = item?.Info?.Entry?.ItemImbuementId ?? 0u;
                if (imbuement == 0u)
                    continue;

                foreach ((uint questId, _) in ItemImbuements.Chain(imbuement))
                {
                    QuestState? state = GetQuestState((ushort)questId);
                    if (state == QuestState.Completed)
                        continue;
                    if (state is QuestState.Accepted or QuestState.Achieved)
                        break;

                    QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo((ushort)questId);
                    if (info == null || DisableManager.Instance.IsDisabled(DisableType.Quest, (ushort)questId))
                        break;
                    if (!MeetsPrerequisites(info))
                    {
                        Diagnostics.FailureLedger.Record("imbuement-quest-not-met", $"quest {questId}", $"item {item.Info.Entry.Id}");
                        break;
                    }

                    QuestAdd(info);
                    Diagnostics.FailureLedger.Record("imbuement-quest-started", $"quest {questId}", $"item {item.Info.Entry.Id}");
                    break;
                }
            }
        }

        /// <summary>Why the last <see cref="MeetsPrerequisites"/> / <see cref="CanAcceptWithoutGiver"/> call said no (diagnostics).</summary>
        [ThreadStatic] public static string LastRefusal;

        private static bool Refuse(string reason)
        {
            LastRefusal = reason;
            return false;
        }

        private bool MeetsPrerequisites(QuestInfo info)
        {
            if (info.Entry.QuestPlayerFactionEnum == 0u && player.Faction1 != Faction.Exile)
                return Refuse("faction-exile");
            if (info.Entry.QuestPlayerFactionEnum == 1u && player.Faction1 != Faction.Dominion)
                return Refuse("faction-dominion");
            if (info.Entry.PrerequisiteRace != 0u && player.Race != (Race)info.Entry.PrerequisiteRace)
                return Refuse("race");
            if (info.Entry.PrerequisiteClass != 0u && player.Class != (Class)info.Entry.PrerequisiteClass)
                return Refuse("class");
            if (player.Level < info.Entry.PrerequisiteLevel)
                return Refuse("level");

            if (!info.PrerequisiteQuests.IsEmpty)
            {
                bool preReqQuestsCompleted;
                if ((info.Entry.PrerequisiteFlags & 1) != 0u)
                    preReqQuestsCompleted = info.PrerequisiteQuests.Any(q => GetQuestState((ushort)q.Id) == QuestState.Completed);
                else
                    preReqQuestsCompleted = info.PrerequisiteQuests.All(q => GetQuestState((ushort)q.Id) == QuestState.Completed);

                if (!preReqQuestsCompleted)
                    return Refuse("prereq-quests");
            }

            PrerequisiteManager.LastFailure = null;
            if (info.Entry.PrerequisiteId != 0u && !PrerequisiteManager.Instance.Meets(player, info.Entry.PrerequisiteId))
                return Refuse("prereq-id " + (PrerequisiteManager.LastFailure ?? $"row {info.Entry.PrerequisiteId}"));

            // rotating dailies: only the quests drawn for the player today are offered, a group limits how many of its quests are in the log
            if (info.Entry.PeriodicQuestGroupId != 0u)
            {
                if (!PeriodicQuests.IsOffered(player, info))
                    return Refuse("periodic-offered");

                int inLog = activeQuests.Values.Count(q => q.Info.Entry.PeriodicQuestGroupId == info.Entry.PeriodicQuestGroupId);
                if (!PeriodicQuests.GroupHasRoom(info.Entry.PeriodicQuestGroupId, inLog))
                    return Refuse("periodic-room");
            }

            if (!info.IsContract())
            {
                GameFormulaEntry entry = GameTableManager.Instance.GameFormula.GetEntry(655);
                // client also hard codes 40 if entry doesn't exist. This is the limit of quests in the log: upstream compared the COMPLETED quests,
                // which locked every character out of new quests after its 41st completion (bots hit it in the first hour)
                if (activeQuests.Count > (entry?.Dataint0 ?? 40u))
                    return Refuse("log-full");
            }
            else
            {
                // ContractsLib.GetMaxActiveContracts: reward property 37 ActiveContractSlots, data 0, Discrete (3 basic / 4 signature in RewardPropertyPremiumModifier)
                if (ActiveContractCount() >= ActiveContractSlots())
                    return Refuse("contract-slots");
            }

            return true;
        }

        public int ActiveContractCount() => activeQuests.Values.Count(q => q.Info.IsContract() && q.State is QuestState.Accepted or QuestState.Achieved);

        /// <summary>Contract slots the client shows (reward property ActiveContractSlots); 0 when the property is missing, as in the client.</summary>
        public int ActiveContractSlots()
        {
            RewardProperty property = player.Session.EntitlementManager.GetRewardProperty(RewardPropertyType.ActiveContractSlots);
            return property?.Entry.RewardModifierValueTypeEnum == 1u ? (int)(property.GetValue(0u) ?? 0f) : 0;
        }

        /// <summary>
        /// Add a <see cref="Quest"/> from supplied <see cref="QuestInfo"/>, skipping any prerequisites checks.
        /// </summary>
        public void QuestAdd(QuestInfo info)
        {
            // make sure player has room for all pushed items
            if (player.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory)
                < info.Entry.PushedItemIds.Count(i => i != 0u))
            {
                player.SendGenericError(GenericError.ItemInventoryFull);
                return;
            }

            for (int i = 0; i < info.Entry.PushedItemIds.Length; i++)
            {
                uint itemId = info.Entry.PushedItemIds[i];
                if (itemId == 0u)
                    continue;
                // 9 quests push an item that is not in Item2 (3144: 6036, 3224: 6035, 3555: 6638 ...); creating it threw and the quest could not be taken
                if (GameTableManager.Instance.Item.GetEntry(itemId) == null)
                {
                    Diagnostics.FailureLedger.Record("quest-pushed-item-missing", $"item {itemId} not in Item2", $"quest {info.Entry.Id}");
                    continue;
                }
                player.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, info.Entry.PushedItemCounts[i]);
            }

            // virtual items are quest tokens the client shows; the server has no store for them (only 6 quests use one as a VirtualCollect item), so counted
            if (info.Entry.VirtualItemIdPushed00 != 0u)
                Diagnostics.FailureLedger.Record("quest-virtual-item-pushed", $"item {info.Entry.VirtualItemIdPushed00} x{info.Entry.VirtualItemPushedCount00} not stored", $"quest {info.Entry.Id}");

            Quest.Quest quest = GetQuest((ushort)info.Entry.Id);
            Trace((ushort)info.Entry.Id, quest == null ? "add:new" : $"add:{quest.State}{(quest.PendingDelete ? "-pd" : "")}{(quest.PendingCreate ? "-pc" : "")}@{DescribeQuest(quest.Id)}");
            if (quest == null)
                quest = new Quest.Quest(player, info);
            else
                QuestRemove(quest);

            quest.Flags |= QuestFlags.Tracked;
            quest.State = QuestState.Accepted;
            // a stale entry of the same quest (state no longer matching the list it sits in) must not make the add throw
            inactiveQuests.Remove((ushort)info.Entry.Id);
            activeQuests[(ushort)info.Entry.Id] = quest;
            Diagnostics.QuestTruth.OnAccepted(player, (ushort)info.Entry.Id);

            quest.InitialiseTimer();

            // a quest without objectives has nothing left to do once taken (Quest sets Achieved when all objectives are complete, vacuously true here):
            // it is handed in at the receiver
            if (!quest.Any())
                quest.State = QuestState.Achieved;

            log.Trace($"Accepted new quest {info.Entry.Id}.");

            // items already in the bags count for a collect objective
            foreach (QuestObjective objective in quest.ToList())
                if (objective.ObjectiveInfo.Type == QuestObjectiveType.CollectItem)
                {
                    uint owned = player.Inventory.GetItemCount(objective.ObjectiveInfo.Entry.Data);
                    if (owned > 0u)
                        quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, owned);
                }

            // a "complete quest X" objective is met at once when X was finished before this quest was taken
            foreach (QuestObjective objective in quest.ToList())
                if (objective.ObjectiveInfo.Type == QuestObjectiveType.CompleteQuest && GetQuestState((ushort)objective.ObjectiveInfo.Entry.Data) == QuestState.Completed)
                    quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, 1u);
        }

        private void QuestRemove(Quest.Quest quest)
        {
            // remove existing quest from its current home before
            switch (quest.State)
            {
                case QuestState.Abandoned:
                    activeQuests.Remove(quest.Id);
                    break;
                case QuestState.Completed:
                    completedQuests.Remove(quest.Id);
                    break;
                case QuestState.Botched:
                case QuestState.Ignored:
                case QuestState.Mentioned:
                    inactiveQuests.Remove(quest.Id);
                    activeQuests.Remove(quest.Id); // older abandons left mentioned quests in both lists
                    break;
            }

            if (quest.PendingDelete)
                quest.EnqueueDelete(false);

            // reset previous objective progress
            foreach (QuestObjective objective in quest)
                objective.Progress = 0u;
        }

        private static Dictionary<ushort, ushort> questFlagAchievements;

        /// <summary>
        /// Type 12 achievements titled "Q&lt;quest id&gt; Complete" (4719 "Q6715 Complete", 4727 "Q7008 Complete", value 1, no other criteria) are flags
        /// the game set when that quest was completed; prerequisite type 7 rows gate the follow-up chains on them (quest 7009 needs 4727). Likely.
        /// </summary>
        private void GrantQuestFlagAchievement(ushort questId)
        {
            if (questFlagAchievements == null)
            {
                var index = new Dictionary<ushort, ushort>();
                var text = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
                foreach (AchievementEntry entry in GameTableManager.Instance.Achievement.Entries)
                {
                    if (entry.AchievementTypeId != 12u)
                        continue;
                    var match = System.Text.RegularExpressions.Regex.Match(text?.GetEntry(entry.LocalizedTextIdTitle) ?? "", @"^Q(\d+) Complete$");
                    if (match.Success && ushort.TryParse(match.Groups[1].Value, out ushort quest))
                        index[quest] = (ushort)entry.Id;
                }
                questFlagAchievements = index;
            }

            if (questFlagAchievements.TryGetValue(questId, out ushort achievementId) && !player.AchievementManager.HasCompletedAchievement(achievementId))
            {
                try
                {
                    player.AchievementManager.GrantAchievement(achievementId);
                    Diagnostics.FailureLedger.Record("quest-flag-achievement", $"achievement {achievementId}", $"quest {questId}");
                }
                catch (Exception exception)
                {
                    Diagnostics.FailureLedger.Record("quest-flag-achievement-failed", $"achievement {achievementId}", exception.Message);
                }
            }
        }

        /// <summary>
        /// Retry an inactive <see cref="Quest"/> that was previously failed.
        /// </summary>
        public void QuestRetry(ushort questId)
        {
            QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(questId);
            if (info == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            Quest.Quest quest = GetQuest(questId, GetQuestFlags.Inactive);
            if (quest == null)
                throw new QuestException($"Player {player.CharacterId} tried to restart quest {questId} which they don't have!");

            if (quest.State != QuestState.Botched)
                throw new QuestException($"Player {player.CharacterId} tried to restart quest {questId} which hasn't been failed!");

            QuestAdd(info, quest, null);
        }

        /// <summary>
        /// Abandon an active <see cref="Quest"/>.
        /// </summary>
        public void QuestAbandon(ushort questId)
        {
            if (GlobalQuestManager.Instance.GetQuestInfo(questId) == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            Quest.Quest quest = GetQuest(questId, GetQuestFlags.Active | GetQuestFlags.Inactive);
            if (quest == null || quest.PendingDelete)
                throw new QuestException($"Player {player.CharacterId} tried to abandon quest {questId} which they don't have!");

            if (!quest.CanAbandon())
                throw new QuestException($"Player {player.CharacterId} tried to abandon quest {questId} which can't be abandoned!");

            // don't delete quests that have been mentioned, they may not be able to be re-collected. A mentioned quest leaves the active list:
            // it used to be queued for delete but stay in activeQuests while also going to inactiveQuests as Mentioned, so it kept a log slot
            // and taking it again failed with a duplicate key (bot quest 10518/5056, "An item with the same key has already been added")
            bool mentioned = quest.Info.IsQuestMentioned;
            Trace(questId, $"abandon:{quest.State}{(mentioned ? "-m" : "")}{(quest.PendingCreate ? "-pc" : "")}");
            if (!mentioned && !quest.PendingCreate && quest.CanDelete())
                quest.EnqueueDelete(true);
            else
            {
                switch (quest.State)
                {
                    case QuestState.Accepted:
                    case QuestState.Achieved:
                        activeQuests.Remove(questId);
                        break;
                    case QuestState.Botched:
                        inactiveQuests.Remove(quest.Id);
                        break;
                }
            }

            foreach (QuestObjective objective in quest)
                objective.Progress = 0u;

            if (mentioned)
            {
                quest.State = QuestState.Mentioned;
                inactiveQuests[quest.Id] = quest;
            }
            else
                quest.State = QuestState.Abandoned;

            Diagnostics.QuestTruth.OnAbandoned(player, questId, null);
            log.Trace($"Abandoned quest {questId}.");
        }

        /// <summary>
        /// Complete all <see cref="QuestObjective"/>'s for supplied active <see cref="Quest"/>.
        /// </summary>
        public void QuestAchieve(ushort questId)
        {
            if (GlobalQuestManager.Instance.GetQuestInfo(questId) == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            Quest.Quest quest = GetQuest(questId);
            if (quest == null || quest.PendingDelete)
                throw new QuestException($"Player {player.CharacterId} tried to achieve quest {questId} which they don't have!");

            if (quest.State != QuestState.Accepted)
                throw new QuestException($"Player {player.CharacterId} tried to achieve quest {questId} with invalid state!");

            // the live objectives: a type 31 objective is the pool row picked for the character
            foreach (QuestObjectiveInfo info in quest.Select(o => o.ObjectiveInfo).ToList())
                quest.ObjectiveUpdate(info.Type, info.Entry.Data, info.Entry.Count);
        }

        /// <summary>
        /// Complete single <see cref="QuestObjective"/> for supplied active <see cref="Quest"/>.
        /// </summary>
        public void QuestAchieveObjective(ushort questId, byte index)
        {
            if (GlobalQuestManager.Instance.GetQuestInfo(questId) == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            Quest.Quest quest = GetQuest(questId);
            if (quest == null || quest.PendingDelete)
                throw new QuestException();

            if (quest.State != QuestState.Accepted)
                throw new QuestException();

            QuestObjective objective = quest.SingleOrDefault(o => o.Index == index);
            if (objective == null)
                throw new QuestException();

            quest.ObjectiveUpdate(objective.ObjectiveInfo.Type, objective.ObjectiveInfo.Entry.Data, objective.ObjectiveInfo.Entry.Count);
        }

        /// <summary>
        /// Complete an achieved <see cref="Quest"/> supplying an optional reward and whether the quest was completed from the communicator.
        /// </summary>
        public void QuestComplete(ushort questId, ushort reward, bool communicator, bool skipReceiverCheck = false)
        {
            QuestInfo questInfo = GlobalQuestManager.Instance.GetQuestInfo(questId);
            if (questInfo == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            if (DisableManager.Instance.IsDisabled(DisableType.Quest, questId))
            {
                player.SendSystemMessage($"Unable to complete quest {questId} because it is disabled.");
                return;
            }

            Quest.Quest quest = GetQuest(questId, GetQuestFlags.Active);
            if (quest == null)
            {
                if (!questInfo.IsAutoComplete())
                    throw new QuestException($"Player {player.CharacterId} tried to complete quest {questId} which they don't have!");

                QuestAdd(questId, null);
                quest = GetQuest(questId);
                quest.State = QuestState.Achieved;
            }

            if (quest.State != QuestState.Achieved)
                throw new QuestException($"Player {player.CharacterId} tried to complete quest {questId} which wasn't complete!");

            if (communicator)
            {
                // TODO: check if this is complete, client seems to also refer to contact info
                // for more see QuestTracker:HelperShowQuestCallbackBtn in LUA which contains the logic to show the complete button in the quest tracker
                if (!quest.Info.IsCommunicatorReceived())
                    throw new QuestException($"Player {player.CharacterId} tried to complete quest {questId} without communicator message!");
            }
            else if (!skipReceiverCheck && !ItemImbuements.IsImbuementQuest(questId) && !(GlobalQuestManager.Instance.GetQuestInfo(questId)?.IsContract() ?? false))
            {
                if (!GlobalQuestManager.Instance.GetQuestReceivers(questId).Any(c => player.GetVisibleCreature<WorldEntity>(c).Any()))
                    throw new QuestException($"Player {player.CharacterId} tried to complete quest {questId} without any quest receiver!");
            }

            // reclaim any quest specific items
            for (int i = 0; i < quest.Info.Entry.PushedItemIds.Length; i++)
            {
                uint itemId = quest.Info.Entry.PushedItemIds[i];
                if (itemId != 0u)
                    player.Inventory.ItemDelete(itemId, quest.Info.Entry.PushedItemCounts[i]);
            }

            // clean bots: which turn-in a player would have had (bots skip the receiver check). The same rules as above: a receiver in view,
            // the communicator, the imbuement/contract exemption; auto-complete quests need none. "no-receiver": a player could not turn it in.
            string turnIn = null;
            if (Diagnostics.QuestTruth.IsClean(player))
            {
                List<uint> receivers = GlobalQuestManager.Instance.GetQuestReceivers(questId).ToList();
                turnIn = receivers.Any(c => player.GetVisibleCreature<WorldEntity>(c).Any()) ? "receiver-near"
                    : quest.Info.IsCommunicatorReceived() ? "communicator"
                    : ItemImbuements.IsImbuementQuest(questId) || quest.Info.IsContract() ? "exempt"
                    : quest.Info.IsAutoComplete() ? "auto"
                    : receivers.Count > 0 ? "receiver-not-near" : "no-receiver";
            }

            RewardQuest(quest.Info, reward);
            quest.State = QuestState.Completed;
            if (ItemImbuements.RewardFor(questId) is { } imbuementReward)
            {
                // types 0 (stat), 1 (item special), 5 (item level) are applied by the gear sum while the item is equipped, 4 (quality) and
                // 6 (rune slots) by the rune slots of the item (ItemImbuements.IsApplied)
                if (ItemImbuements.IsApplied(imbuementReward))
                {
                    player.MarkGearDirty();
                    Diagnostics.FailureLedger.Record("imbuement-reward-applied", $"type {imbuementReward.ItemImbuementRewardTypeEnum}", $"quest {questId}");
                }
                else
                    Diagnostics.FailureLedger.Record("imbuement-reward-not-applied", $"type {imbuementReward.ItemImbuementRewardTypeEnum}",
                        $"quest {questId} reward {imbuementReward.Id} object {imbuementReward.RewardObjectId} value {imbuementReward.RewardValue}/{imbuementReward.RewardValueFloat}");
            }
            player.PathMissionManager.OnQuestCompleted(questId);

            // mark repeatable quests for reset
            switch ((QuestRepeatPeriod)quest.Info.Entry.QuestRepeatPeriodEnum)
            {
                case QuestRepeatPeriod.Daily:
                    quest.Reset = GlobalQuestManager.Instance.NextDailyReset;
                    break;
                case QuestRepeatPeriod.Weekly:
                    quest.Reset = GlobalQuestManager.Instance.NextWeeklyReset;
                    break;
            }

            activeQuests.Remove(questId);
            Trace(questId, $"complete@{DescribeQuest(questId)}");
            completedQuests.Add(questId, quest);
            if (turnIn != null)
                Diagnostics.QuestTruth.OnCompleted(player, quest, turnIn);

            player.AchievementManager.CheckAchievements(player, AchievementType.QuestComplete, questId);
            GrantQuestFlagAchievement(questId);
            ObjectiveUpdate(QuestObjectiveType.CompleteQuest, questId, 1u);
        }

        private void RewardQuest(QuestInfo info, ushort reward)
        {
            if (reward != 0)
            {
                if (!info.Rewards.TryGetValue(reward, out Quest2RewardEntry entry))
                    throw new QuestException($"Player {player.CharacterId} tried to complete quest {info.Entry.Id} with invalid reward!");

                // TODO: make sure reward is valid for player, some rewards are conditional

                RewardQuest(entry);
            }

            // everything that is not an item is given to everybody, a lone item too. Several items are a choice, the client sends the one picked.
            // (inferred from the table: items come in rows of 1 to 15 per quest, other types are one row each)
            int itemRows = info.Rewards.Values.Count(r => (QuestRewardType)r.Quest2RewardTypeId == QuestRewardType.Item);
            foreach (Quest2RewardEntry fixedReward in info.Rewards.Values)
            {
                if (fixedReward.Id == reward)
                    continue;

                if ((QuestRewardType)fixedReward.Quest2RewardTypeId == QuestRewardType.Item && itemRows > 1)
                    continue;

                RewardQuest(fixedReward);
            }

            if (reward == 0 && itemRows > 1)
                Diagnostics.FailureLedger.Record("quest-reward", "quest with a reward choice completed without picking one", $"quest {info.Entry.Id}");

            uint experience = info.GetRewardExperience();
            if (experience != 0u)
                player.GrantXp(experience, ExpReason.Quest);

            uint money = info.GetRewardMoney();
            if (money != 0u)
                player.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, money);

            RewardQuestReputation(info);
        }

        /// <summary>
        /// Reputation from the quest row: up to two factions, each the override amount when set, else the base reputation of the quest level times the difficulty multiplier.
        /// </summary>
        private void RewardQuestReputation(QuestInfo info)
        {
            (uint factionId, float overrideAmount)[] slots =
            {
                (info.Entry.Faction2IdRewardReputation00, info.Entry.RewardReputationOverride00),
                (info.Entry.Faction2IdRewardReputation01, info.Entry.RewardReputationOverride01)
            };

            foreach ((uint factionId, float overrideAmount) in slots)
            {
                if (factionId == 0u)
                    continue;

                float amount = overrideAmount != 0f ? overrideAmount : info.GetRewardReputation();
                if (amount == 0f)
                    continue;

                if (FactionManager.Instance.GetFaction((Faction)factionId) == null)
                {
                    Diagnostics.FailureLedger.Record("quest-reward-unhandled", $"reputation faction {factionId} not in the tables", $"quest {info.Entry.Id}");
                    continue;
                }

                player.ReputationManager.UpdateReputation((Faction)factionId, amount);
                Diagnostics.FailureLedger.Record("quest-reward", "reputation granted");
            }
        }

        private void RewardQuest(Quest2RewardEntry entry)
        {
            switch ((QuestRewardType)entry.Quest2RewardTypeId)
            {
                case QuestRewardType.Item:
                    player.Inventory.ItemCreate(InventoryLocation.Inventory, entry.ObjectId, entry.ObjectAmount);
                    break;
                case QuestRewardType.Money:
                    player.CurrencyManager.CurrencyAddAmount((CurrencyType)entry.ObjectId, entry.ObjectAmount);
                    break;
                case QuestRewardType.Reputation:
                    if (FactionManager.Instance.GetFaction((Faction)entry.ObjectId) != null)
                    {
                        player.ReputationManager.UpdateReputation((Faction)entry.ObjectId, entry.ObjectAmount);
                        Diagnostics.FailureLedger.Record("quest-reward", "reputation reward row granted");
                    }
                    else
                        Diagnostics.FailureLedger.Record("quest-reward-unhandled", $"reputation faction {entry.ObjectId} not in the tables", $"reward {entry.Id}");
                    break;
                case QuestRewardType.TradeSkillXp:
                {
                    // ObjectId is a TradeskillTier row (all 34 ids of the 378 rows are tier rows, several are not Tradeskill ids): the xp goes to
                    // that tier's tradeskill when the player has learned it (likely, docs/ASSUMPTIONS.md "Tradeskills")
                    TradeskillTierEntry tier = GameTableManager.Instance.TradeskillTier.GetEntry(entry.ObjectId);
                    if (tier != null && player.TradeskillManager.HasLearned(tier.TradeSkillId))
                    {
                        player.TradeskillManager.AddXp(tier.TradeSkillId, entry.ObjectAmount);
                        Diagnostics.FailureLedger.Record("quest-reward", "tradeskill xp granted");
                    }
                    else
                        Diagnostics.FailureLedger.Record("quest-reward-tradeskill-xp-skipped", tier == null ? $"tier row {entry.ObjectId} missing" : $"skill {tier.TradeSkillId} not learned", $"quest {entry.Quest2Id}");
                    break;
                }
                case QuestRewardType.TradeSkill:
                    // ObjectId is a Tradeskill id (one row: quest 6380 teaches 21 Tailor); a full set of trainer skills is not replaced
                    if (player.TradeskillManager.HasLearned(entry.ObjectId) || player.TradeskillManager.Learn(entry.ObjectId))
                        Diagnostics.FailureLedger.Record("quest-reward", "tradeskill learned");
                    else
                        Diagnostics.FailureLedger.Record("quest-reward-tradeskill-refused", $"skill {entry.ObjectId}", $"quest {entry.Quest2Id}: {player.TradeskillManager.LastRefusal}");
                    break;
                default:
                {
                    Diagnostics.FailureLedger.Record("quest-reward-unhandled", $"reward type {(QuestRewardType)entry.Quest2RewardTypeId} ({entry.Quest2RewardTypeId})", $"reward {entry.Id} quest {entry.Quest2Id}");
                    break;
                }
            }

            log.Trace($"Recieved quest reward, type: {(QuestRewardType)entry.Quest2RewardTypeId}, objectId: {entry.ObjectId}, amount: {entry.ObjectAmount}.");
        }

        /// <summary>
        /// Ignore or acknowledge an inactive <see cref="Quest"/>.
        /// </summary>
        public void QuestIgnore(ushort questId, bool ignored)
        {
            if (GlobalQuestManager.Instance.GetQuestInfo(questId) == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            // TODO:
        }

        /// <summary>
        /// Track or hide an active <see cref="Quest"/>.
        /// </summary>
        public void QuestTrack(ushort questId, bool tracked)
        {
            if (GlobalQuestManager.Instance.GetQuestInfo(questId) == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            Quest.Quest quest = GetQuest(questId, GetQuestFlags.Active);
            if (quest == null)
                throw new QuestException($"Player {player.CharacterId} tried to track quest {questId} which they don't have!");

            if (quest.State != QuestState.Accepted && quest.State != QuestState.Achieved)
                throw new QuestException($"Player {player.CharacterId} tried to track quest {questId} with invalid state!");

            if (tracked)
                quest.Flags |= QuestFlags.Tracked;
            else
                quest.Flags &= ~QuestFlags.Tracked;

            log.Trace($"Updated tracked state of quest {questId} to {tracked}.");
        }

        /// <summary>
        /// Share supplied <see cref="Quest"/> with another <see cref="Player"/>.
        /// </summary>
        public void QuestShare(ushort questId)
        {
            QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(questId);
            if (info == null)
                throw new ArgumentException($"Invalid quest {questId}!");

            Quest.Quest quest = GetQuest(questId);
            if (quest == null)
                throw new QuestException($"Player {player.CharacterId} tried to share quest {questId} which they don't have!");

            if (!quest.CanShare())
                throw new QuestException($"Player {player.CharacterId} tried to share quest {questId} which can't be shared!");

            Player recipient = player.GetVisible<Player>(player.TargetGuid);
            if (recipient == null)
                throw new QuestException($"Player {player.CharacterId} tried to share quest {questId} to an invalid player!");

            // TODO

            log.Trace($"Shared quest {questId} with player {recipient.Name}.");
        }

        /// <summary>
        /// Accept or deny a shared <see cref="Quest"/> from another <see cref="Player"/>.
        /// </summary>
        public void QuestShareResult(ushort questId, bool result)
        {
            // TODO
        }

        /// <summary>
        /// Currently active quests (snapshot).
        /// </summary>
        public List<Quest.Quest> GetActiveQuests()
        {
            // an abandoned quest waits in activeQuests until the next save deletes it; it is no longer active (it held a bot log slot)
            return activeQuests.Values.Where(q => !q.PendingDelete).ToList();
        }

        /// <summary>
        /// The player holds an objective that this creature's death advances and that can be updated now (accepted quest, objective open,
        /// sequence reached): kill, kill target group, a virtual or quest item the creature gives, a named kill.
        /// Used to let the player damage a quest kill target that is not hostile to it (see SpellEffectHandler damage, docs/ASSUMPTIONS.md).
        /// </summary>
        public bool NeedsKill(uint creatureId) => QuestNeedingKill(creatureId) != 0;

        /// <summary>The first accepted quest whose open objective the creature's death advances, 0 when none.</summary>
        public ushort QuestNeedingKill(uint creatureId)
        {
            foreach (Quest.Quest quest in activeQuests.Values)
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest)
                {
                    if (objective.IsComplete() || !quest.CanUpdateObjective(objective))
                        continue;

                    QuestObjectiveEntry entry = objective.ObjectiveInfo.Entry;
                    bool match = objective.ObjectiveInfo.Type switch
                    {
                        QuestObjectiveType.KillCreature => entry.Data == creatureId,
                        QuestObjectiveType.KillTargetGroup or QuestObjectiveType.KillTargetGroups => TargetGroupHelper.Contains(entry.Data, creatureId),
                        // objectives the kill advances in OnCreatureKilled / the loot roll: a virtual item or a quest item the creature gives, a named kill
                        QuestObjectiveType.VirtualCollect => (entry.TargetGroupIdRewardPane != 0u && TargetGroupHelper.Contains(entry.TargetGroupIdRewardPane, creatureId))
                            || Array.IndexOf(Bots.QuestCoverage.TextCreatures(entry), creatureId) >= 0,
                        QuestObjectiveType.CollectItem => Array.IndexOf(Bots.QuestCoverage.CollectSources(entry), creatureId) >= 0,
                        QuestObjectiveType.Unknown15 => Array.IndexOf(Bots.QuestCoverage.ScriptedTargets(entry).Item1, creatureId) >= 0,
                        _ => false
                    };
                    if (match)
                        return quest.Id;
                }
            }

            return 0;
        }

        /// <summary>
        /// Credit a creature kill to every active quest with a matching kill objective, directly or through a target group.
        /// </summary>
        /// <summary>
        /// Quest loot: a CollectItem objective whose text names this creature as the source of its item gets one item when the player kills,
        /// activates or gathers the creature (the item is not in the creature's researched drop table, e.g. the tutorial Iron Chunk from Iron Nodes).
        /// The item goes through the inventory, which credits the objective like any looted item.
        /// </summary>
        public void OnQuestItemSource(uint creatureId)
        {
            foreach (Quest.Quest quest in activeQuests.Values.ToList())
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest.ToList())
                {
                    QuestObjectiveEntry entry = objective.ObjectiveInfo.Entry;
                    if (objective.IsComplete() || objective.ObjectiveInfo.Type != QuestObjectiveType.CollectItem || !quest.CanUpdateObjective(objective)
                        || Array.IndexOf(Bots.QuestCoverage.TextItemSources(entry), creatureId) < 0)
                        continue;

                    if (ItemManager.Instance.GetItemInfo(entry.Data) == null)
                    {
                        Diagnostics.FailureLedger.Record("quest-loot-unknown-item", $"item {entry.Data}", $"objective {entry.Id}");
                        continue;
                    }

                    player.Inventory.ItemCreate(InventoryLocation.Inventory, entry.Data, 1u, ItemUpdateReason.Loot);
                    Diagnostics.FailureLedger.Record("quest-loot", $"item {entry.Data} from creature {creatureId} (named in the objective text)", $"quest {quest.Id} objective {entry.Id}");
                }
            }
        }

        /// <summary>
        /// Equipping an item credits the activate objectives whose text is "Equip the $(item=N)" for that item (15 objectives with no creature,
        /// e.g. 9031 "Equip the Novice's Laser Pickaxe"; QuestCoverage.EquipItem).
        /// </summary>
        public void OnItemEquipped(uint itemId)
        {
            foreach (Quest.Quest quest in activeQuests.Values.ToList())
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest.ToList())
                    if (!objective.IsComplete() && Bots.QuestCoverage.EquipItem(objective.ObjectiveInfo.Entry) == itemId)
                        quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, 1u);
            }
        }

        public void OnCreatureKilled(uint creatureId, uint guid = 0u)
        {
            player.ChallengeManager.OnCreatureKilled(creatureId, guid);
            OnQuestItemSource(creatureId);

            foreach (Quest.Quest quest in activeQuests.Values.ToList())
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                bool questMatched = false;
                foreach (QuestObjective objective in quest.ToList())
                {
                    QuestObjectiveEntry entry = objective.ObjectiveInfo.Entry;
                    bool match = objective.ObjectiveInfo.Type switch
                    {
                        QuestObjectiveType.KillCreature => entry.Data == creatureId,
                        QuestObjectiveType.ActivateEntity or QuestObjectiveType.ActivateEntity2 => Array.IndexOf(Bots.QuestCoverage.TextCreatures(entry), creatureId) >= 0,
                        QuestObjectiveType.KillTargetGroup or QuestObjectiveType.KillTargetGroups => TargetGroupHelper.Contains(entry.Data, creatureId),
                        QuestObjectiveType.VirtualCollect => DropsVirtualItem(entry, creatureId),
                        QuestObjectiveType.Unknown15 => CreditKillAll(objective, creatureId),
                        QuestObjectiveType.Unknown10 => IsHostileCreature(creatureId) && CreditScripted(objective, creatureId, guid),
                        _ => false
                    };

                    if (match)
                    {
                        questMatched = true;
                        quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, 1u);
                    }
                }

                // a kill that fits none of the quest's kill objectives, with the objectives listed so the target group can be inspected
                if (!questMatched && Combat.CombatConfig.Enabled)
                {
                    foreach (QuestObjective objective in quest.ToList().Where(o => IsKillLike(o.ObjectiveInfo.Type) && !o.IsComplete()))
                    {
                        QuestObjectiveEntry entry = objective.ObjectiveInfo.Entry;
                        // collateral kills (the creature is not one of this quest's targets) are normal, not a ledger failure; debug dump only

                        // one dump per group: its structure and the killed creature, to see why the creature is not in it
                        string description = TargetGroupHelper.Describe(entry.Data);
                        if (description != null)
                            log.Debug($"Quest target group structure: {description}; killed {TargetGroupHelper.DescribeCreature(creatureId)}");
                    }
                }
            }
        }

        /// <summary>
        /// Credit the activation of a creature to every active quest with a matching activate objective, directly or through a target group.
        /// </summary>
        public void OnCreatureActivated(uint creatureId, uint guid = 0u)
        {
            player.ChallengeManager.OnCreatureActivated(creatureId, guid);
            OnQuestItemSource(creatureId);
            foreach (Quest.Quest quest in activeQuests.Values.ToList())
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest.ToList())
                {
                    QuestObjectiveEntry entry = objective.ObjectiveInfo.Entry;
                    bool match = objective.ObjectiveInfo.Type switch
                    {
                        QuestObjectiveType.ActivateEntity or QuestObjectiveType.ActivateEntity2 or QuestObjectiveType.SucceedCSI => entry.Data == creatureId
                            || (entry.Data == 0u && Array.IndexOf(Bots.QuestCoverage.TextCreatures(entry), creatureId) >= 0),
                        QuestObjectiveType.ActivateTargetGroup or QuestObjectiveType.ActivateTargetGroupChecklist => TargetGroupHelper.Contains(entry.Data, creatureId),
                        QuestObjectiveType.VirtualCollect => DropsVirtualItem(entry, creatureId),
                        QuestObjectiveType.Unknown10 => CreditScripted(objective, creatureId, guid),
                        _ => false
                    };

                    if (match)
                        quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, 1u);
                }
            }
        }

        /// <summary>
        /// Credit talking to a creature to every active quest with a matching talk objective, directly or through a target group.
        /// </summary>
        public void OnCreatureTalked(uint creatureId)
        {
            foreach (Quest.Quest quest in activeQuests.Values.ToList())
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest.ToList())
                {
                    QuestObjectiveEntry entry = objective.ObjectiveInfo.Entry;
                    bool match = objective.ObjectiveInfo.Type switch
                    {
                        QuestObjectiveType.TalkTo => entry.Data == creatureId,
                        QuestObjectiveType.TalkToTargetGroup => TargetGroupHelper.Contains(entry.Data, creatureId),
                        _ => false
                    };

                    if (match)
                        quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, 1u);
                }
            }
        }

        /// <summary>
        /// Credit a spell the player cast to the SpellSuccess objectives that name it (the spell itself or another tier of it). When the spell
        /// is limited to a target group, the primary target has to be a creature of that group.
        /// </summary>
        public void OnSpellSuccess(Spell4Entry spell, uint targetCreatureId, uint targetGuid = 0u)
        {
            foreach (Quest.Quest quest in activeQuests.Values.ToList())
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest.ToList())
                {
                    if (objective.IsComplete())
                        continue;

                    // type 10: a quest item or vehicle spell (not a class ability) used on one of the objective's creatures
                    if (objective.ObjectiveInfo.Type == QuestObjectiveType.Unknown10)
                    {
                        if (targetCreatureId != 0u && player.SpellManager.GetSpell(spell.Spell4BaseIdBaseSpell) == null
                            && CreditScripted(objective, targetCreatureId, targetGuid))
                            quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, 1u);
                        continue;
                    }

                    if (!IsSpellSuccess(objective.ObjectiveInfo.Type))
                        continue;

                    Spell4Entry wanted = GameTableManager.Instance.Spell4.GetEntry(objective.ObjectiveInfo.Entry.Data);
                    if (wanted == null || (wanted.Id != spell.Id && wanted.Spell4BaseIdBaseSpell != spell.Spell4BaseIdBaseSpell))
                        continue;

                    Spell4BaseEntry baseEntry = GameTableManager.Instance.Spell4Base.GetEntry(spell.Spell4BaseIdBaseSpell);
                    if (baseEntry != null && baseEntry.TargetGroupIdCastGroup != 0u && !TargetGroupHelper.Contains(baseEntry.TargetGroupIdCastGroup, targetCreatureId))
                    {
                        Diagnostics.FailureLedger.Record("spell-success-wrong-target", $"spell {spell.Id} cast at creature {targetCreatureId}, group {baseEntry.TargetGroupIdCastGroup}", $"quest {quest.Id}");
                        continue;
                    }

                    quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, 1u);
                }
            }
        }

        // units (by guid) and creature ids already counted for a type 10/15 objective, so one creature is counted once (kill after a scan, AoE hits)
        private readonly Dictionary<uint, HashSet<uint>> scriptedCredited = new();

        /// <summary>
        /// Type 10 objective ("free, scan, overload, secure N of X"): one credit per distinct unit of the objective's creatures.
        /// </summary>
        private bool CreditScripted(QuestObjective objective, uint creatureId, uint guid)
        {
            if (Array.IndexOf(Bots.QuestCoverage.ScriptedTargets(objective.ObjectiveInfo.Entry).Targets, creatureId) < 0)
                return false;
            if (guid == 0u)
                return true;

            if (!scriptedCredited.TryGetValue(objective.ObjectiveInfo.Id, out HashSet<uint> units))
                scriptedCredited.Add(objective.ObjectiveInfo.Id, units = new HashSet<uint>());
            return units.Add(guid);
        }

        /// <summary>
        /// Type 15 objective ("kill X and Y"): a kill of one of its creatures counts. When the count is 1 and the text names several creatures,
        /// the objective completes once each named creature is dead (count 1 for "Kill X and Y", docs/ASSUMPTIONS.md).
        /// </summary>
        private bool CreditKillAll(QuestObjective objective, uint creatureId)
        {
            (uint[] targets, uint[] named) = Bots.QuestCoverage.ScriptedTargets(objective.ObjectiveInfo.Entry);
            if (Array.IndexOf(targets, creatureId) < 0)
                return false;
            if (objective.ObjectiveInfo.Entry.Count > 1u || named.Length < 2)
                return true;

            if (!scriptedCredited.TryGetValue(objective.ObjectiveInfo.Id, out HashSet<uint> killed))
                scriptedCredited.Add(objective.ObjectiveInfo.Id, killed = new HashSet<uint>());
            killed.Add(creatureId);
            return named.All(killed.Contains);
        }

        private bool IsHostileCreature(uint creatureId)
        {
            Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(creatureId);
            return creature != null && player.GetDispositionTo((Faction)creature.FactionId) == Disposition.Hostile;
        }

        public static bool IsSpellSuccess(QuestObjectiveType type)
        {
            return type is QuestObjectiveType.SpellSuccess or QuestObjectiveType.SpellSuccess2 or QuestObjectiveType.SpellSuccess3 or QuestObjectiveType.SpellSuccess4;
        }

        private const float AreaCreatureRange = 25f;

        /// <summary>
        /// EnterArea objectives: data is a target group of creatures ("find" objectives), the objective's indicator locations mark the place.
        /// Met inside the indicator location's radius (at least 8 m); objectives without an indicator are met within 25 m of a creature of the group.
        /// </summary>
        public void CheckAreas() => UpdateAreaObjectives();

        private void UpdateAreaObjectives()
        {
            // bots never send the entered-world message that clears the loading flag
            if (player.Map == null || (player.IsLoading && player.Session is not Bots.BotSession))
            {
                if (activeQuests.Values.Any(q => q.Any(o => o.ObjectiveInfo.Type == QuestObjectiveType.EnterArea && !o.IsComplete())))
                    Diagnostics.FailureLedger.Record("area-check", player.Map == null ? "skipped, player has no map" : "skipped, player is loading");
                return;
            }

            foreach (Quest.Quest quest in activeQuests.Values.ToList())
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest.ToList())
                {
                    if (objective.ObjectiveInfo.Type != QuestObjectiveType.EnterArea || objective.IsComplete())
                        continue;

                    bool inArea = IsInArea(objective.ObjectiveInfo.Entry);
                    if (inArea)
                    {
                        Diagnostics.FailureLedger.Record("area-check", "in area", $"objective {objective.ObjectiveInfo.Id} world {player.Map.Entry.Id}");
                        Diagnostics.FailureLedger.Record("objective-inferred", "area entered (indicator location or creature of the group in reach)", $"quest {quest.Id} objective {objective.ObjectiveInfo.Id}");
                        quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, 1u);
                    }
                }
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint, uint), byte> areaMissLogged = new();

        private bool IsInArea(QuestObjectiveEntry entry)
        {
            uint[] indicators = { entry.WorldLocationsIdIndicator00, entry.WorldLocationsIdIndicator01, entry.WorldLocationsIdIndicator02, entry.WorldLocationsIdIndicator03 };
            bool hasIndicator = false;
            foreach (uint indicatorId in indicators.Where(i => i != 0u))
            {
                WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(indicatorId);
                if (location == null)
                    continue;

                hasIndicator = true;
                if (location.WorldId != player.Map.Entry.Id)
                    continue;

                float dx = player.Position.X - location.Position0;
                float dz = player.Position.Z - location.Position2;
                float range = System.Math.Max(location.Radius, 8f);
                if (dx * dx + dz * dz <= range * range
                    && (location.MaxVerticalDistance <= 0f || System.Math.Abs(player.Position.Y - location.Position1) <= System.Math.Max(location.MaxVerticalDistance, 10f)))
                    return true;
            }

            if (hasIndicator || entry.Data == 0u)
                return false;

            player.Map.Search(player.Position, AreaCreatureRange, new SearchCheckRange(player.Position, AreaCreatureRange, player), out List<GridEntity> around);
            bool found = around.OfType<WorldEntity>().Any(c => c is not Player && TargetGroupHelper.Contains(entry.Data, c.CreatureId));
            if (!found && areaMissLogged.TryAdd((entry.Id, player.Map.Entry.Id), 0))
            {
                // diagnostic (once per objective and world, the bots poll this every few seconds): how far is the nearest creature of the group on this map, if any
                player.Map.Search(player.Position, 400f, new SearchCheckRange(player.Position, 400f, player), out List<GridEntity> wide);
                WorldEntity nearest = wide.OfType<WorldEntity>().Where(c => c is not Player && TargetGroupHelper.Contains(entry.Data, c.CreatureId))
                    .OrderBy(c => System.Numerics.Vector3.Distance(player.Position, c.Position)).FirstOrDefault();
                Diagnostics.FailureLedger.Record("area-miss-detail", nearest == null ? "no creature of the group within 400 m" : $"nearest group creature {System.Numerics.Vector3.Distance(player.Position, nearest.Position):F0} m away",
                    $"objective {entry.Id} group {entry.Data}");
            }

            return found;
        }

        /// <summary>
        /// Virtual items (quest collectibles) have no loot data in the client tables. The reward pane group of the objective lists the
        /// creatures they come from, so each kill or use of one of them gives one item (drop chance unknown, inferred from the pane).
        /// </summary>
        private static bool DropsVirtualItem(QuestObjectiveEntry entry, uint creatureId)
        {
            bool inPane = entry.TargetGroupIdRewardPane != 0u && TargetGroupHelper.Contains(entry.TargetGroupIdRewardPane, creatureId);
            if (!inPane && !Array.Exists(Bots.QuestCoverage.TextCreatures(entry), c => c == creatureId))
                return false;

            Diagnostics.FailureLedger.Record("objective-inferred", "virtual item given for a creature in the reward pane group", $"objective {entry.Id} creature {creatureId}");
            return true;
        }

        /// <summary>
        /// Objective types that are about killing, including the ones we cannot credit yet (logged so they can be identified from real play).
        /// </summary>
        private static bool IsKillLike(QuestObjectiveType type)
        {
            return type.ToString().StartsWith("Kill")
                || type == QuestObjectiveType.Unknown10
                || type == QuestObjectiveType.Unknown15
                || type == QuestObjectiveType.CompleteEvent;
        }

        /// <summary>
        /// Returns true if a bot may take the quest: never started before and all prerequisites are met.
        /// The quest giver and communicator checks of a normal accept are skipped.
        /// </summary>
        public bool CanAcceptWithoutGiver(QuestInfo info)
        {
            Quest.Quest existing = GetQuest((ushort)info.Entry.Id);
            LastRefusal = null;
            // same rule as QuestAdd (the accept path): only a quest in progress or completed and not reset blocks it. Mentioned, botched, ignored
            // and abandoned quests sit in the quest list too and can be taken; refusing every listed quest kept 141 open quests away from the bots
            // that had them mentioned (communicator, abandon of a mentioned quest: 3772 state-5 rows in character_quest, forced sweep)
            if (existing?.State is QuestState.Accepted or QuestState.Achieved)
                return Refuse("already-in-log");
            if (existing?.State == QuestState.Completed)
            {
                // a completed daily/weekly quest comes back once its reset time has passed
                bool repeatable = info.Entry.QuestRepeatPeriodEnum != 0u && existing.Reset.HasValue && DateTime.UtcNow >= existing.Reset.Value;
                if (!repeatable)
                    return Refuse("already-done");
            }

            if (DisableManager.Instance.IsDisabled(DisableType.Quest, (ushort)info.Entry.Id))
                return Refuse("disabled");

            return MeetsPrerequisites(info);
        }

        /// <summary>
        /// Update any active <see cref="Quest"/> <see cref="QuestObjective"/>'s with supplied <see cref="QuestObjectiveType"/> and data with progress.
        /// </summary>
        public void ObjectiveUpdate(QuestObjectiveType type, uint data, uint progress)
        {
            foreach (Quest.Quest quest in activeQuests.Values)
                quest.ObjectiveUpdate(type, data, progress);
        }

        /// <summary>
        /// Update any active <see cref="Quest"/> <see cref="QuestObjective"/>'s with supplied ID with progress.
        /// </summary>
        public void ObjectiveUpdate(uint id, uint progress)
        {
            foreach (Quest.Quest quest in activeQuests.Values)
                quest.ObjectiveUpdate(id, progress);
        }
    }
}
