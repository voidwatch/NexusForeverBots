using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NexusForever.Shared.Database;
using NexusForever.WorldServer.Command.Context;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Game.RBAC.Static;

namespace NexusForever.WorldServer.Command.Handler
{
    public partial class GameTableCommandCategory
    {
        private static List<QuestInfo> stressQuests;
        private static readonly Random stressRng = new(27);
        private static readonly Dictionary<string, (int Ok, int Refused)> stressCounts = new();

        /// <summary>
        /// A fixed mix of quests with objectives: repeatable, mentioned (communicator), timed and plain.
        /// </summary>
        private static List<QuestInfo> StressQuests()
        {
            if (stressQuests != null)
                return stressQuests;

            List<QuestInfo> all = GlobalQuestManager.Instance.GetAllQuestInfo()
                .Where(q => q.Objectives.Count > 0 && q.Entry.PushedItemIds.All(i => i == 0u))
                .OrderBy(q => q.Entry.Id)
                .ToList();
            var rng = new Random(27);
            IEnumerable<QuestInfo> Pick(IEnumerable<QuestInfo> source, int n) => source.OrderBy(_ => rng.Next()).Take(n);
            stressQuests = Pick(all.Where(q => q.Entry.QuestRepeatPeriodEnum != 0u), 12)
                .Concat(Pick(all.Where(q => q.IsQuestMentioned && q.Entry.QuestRepeatPeriodEnum == 0u), 12))
                .Concat(Pick(all.Where(q => q.Entry.MaxTimeAllowedMS != 0u), 6))
                .Concat(Pick(all.Where(q => !q.IsQuestMentioned && q.Entry.QuestRepeatPeriodEnum == 0u && q.Entry.MaxTimeAllowedMS == 0u), 12))
                .GroupBy(q => q.Entry.Id).Select(g => g.First())
                .ToList();
            return stressQuests;
        }

        [Command(Permission.Character, "Quest save stress test: each online bot runs random quest transitions on a fixed quest mix (accept, re-accept, abandon, achieve, complete, repeat, mention, objective) and saves; failures land in character-save-failed.", "queststress")]
        public void HandleQuestStress(ICommandContext context,
            [Parameter("Transitions per bot, or 0 to show the counts so far.")]
            uint rounds,
            [Parameter("1: save each bot after its transitions (overlapping saves), 0: leave saving to the 60 s timer.")]
            uint save)
        {
            List<Player> players = Bots.BotManager.Instance.Bots.Select(b => b.Session?.Player).Where(p => p != null && p.Map != null).ToList();
            List<QuestInfo> pool = StressQuests();
            string[] ops = { "accept", "accept", "abandon", "achieve", "complete", "repeat", "mention", "objective" };

            void Count(string op, bool ok)
            {
                stressCounts.TryGetValue(op, out (int Ok, int Refused) c);
                stressCounts[op] = ok ? (c.Ok + 1, c.Refused) : (c.Ok, c.Refused + 1);
            }

            int saves = 0;
            foreach (Player player in players)
            {
                for (uint i = 0; i < rounds; i++)
                {
                    QuestInfo info = pool[stressRng.Next(pool.Count)];
                    ushort id = (ushort)info.Entry.Id;
                    string op = ops[stressRng.Next(ops.Length)];
                    try
                    {
                        QuestState? state = player.QuestManager.GetQuestState(id);
                        switch (op)
                        {
                            case "accept":
                                player.QuestManager.QuestAdd(info);
                                break;
                            case "abandon":
                                player.QuestManager.QuestAbandon(id);
                                break;
                            case "achieve":
                                player.QuestManager.QuestAchieve(id);
                                break;
                            case "complete":
                                if (state == QuestState.Accepted)
                                    player.QuestManager.QuestAchieve(id);
                                player.QuestManager.QuestComplete(id, 0, false, true);
                                break;
                            case "repeat":
                                if (state != QuestState.Completed)
                                    throw new QuestException("not completed");
                                player.QuestManager.SetQuestReset(id, DateTime.UtcNow.AddDays(-1d));
                                player.QuestManager.QuestAdd(info);
                                break;
                            case "mention":
                                player.QuestManager.QuestMention(id);
                                break;
                            case "objective":
                                player.QuestManager.QuestAchieveObjective(id, 0);
                                break;
                        }
                        Count(op, true);
                    }
                    catch (Exception exception)
                    {
                        Count(op, false);
                        if (exception is not QuestException and not ArgumentException)
                            Diagnostics.FailureLedger.Record("quest-stress-error", $"{op}: {exception.GetType().Name}", exception.Message);
                    }
                }

                if (rounds > 0 && save == 1u && !CharacterSave.SaveInFlight(player.CharacterId))
                {
                    player.Save();
                    saves++;
                }
            }

            Send(context, $"{players.Count} bots ({players.Count(p => !p.Session.CanProcessPackets)} not processing packets, {players.Count(p => CharacterSave.SaveInFlight(p.CharacterId))} saving), {saves} saves started; " + string.Join(", ", stressCounts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value.Ok}/{c.Value.Ok + c.Value.Refused}")));
        }

        [Command(Permission.Character, "Make the next save of every online bot rewrite its quests from the game state (the repair a failed quest batch triggers); check with questverify.", "questresync")]
        public void HandleQuestResync(ICommandContext context)
        {
            List<Player> players = Bots.BotManager.Instance.Bots.Select(b => b.Session?.Player).Where(p => p != null).ToList();
            foreach (Player player in players)
            {
                player.QuestManager.RequestResync();
                if (!CharacterSave.SaveInFlight(player.CharacterId))
                    player.Save();
            }
            Send(context, $"resync requested for {players.Count} bots");
        }

        [Command(Permission.Character, "Compare the quests of every online bot with its database rows after a save; differences in /cache/quest-verify.txt and the ledger (character-quest-mismatch).", "questverify")]
        public void HandleQuestVerify(ICommandContext context)
        {
            List<Player> players = Bots.BotManager.Instance.Bots.Select(b => b.Session?.Player).Where(p => p != null && p.Map != null).ToList();
            string path = Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "quest-verify.txt");
            File.WriteAllText(path, $"quest verify started {DateTime.UtcNow:O} for {players.Count} bots{Environment.NewLine}");
            var fileLock = new object();

            foreach (Player player in players)
            {
                ulong characterId = player.CharacterId;
                string name = player.Name;
                player.Save(() =>
                {
                    List<CharacterSave.QuestRow> rows = player.QuestManager.Snapshot();
                    string active = string.Join(",", player.QuestManager.GetActiveQuests()
                        .Where(q => q.State is QuestState.Accepted or QuestState.Achieved).Select(q => q.Id).OrderBy(id => id));
                    Task.Run(async () =>
                    {
                        List<string> differences;
                        try
                        {
                            differences = await CharacterSave.Verify(DatabaseManager.Instance.CharacterDatabase, characterId, rows);
                        }
                        catch (Exception exception)
                        {
                            differences = new List<string> { $"verify failed: {exception.Message}" };
                        }

                        foreach (string difference in differences)
                            Diagnostics.FailureLedger.Record("character-quest-mismatch", difference.Split(" q")[0], $"{name} {difference}");
                        lock (fileLock)
                            File.AppendAllText(path, $"{name}: {rows.Count(r => !r.Pending)} quests compared, {rows.Count(r => r.Pending)} pending, {differences.Count} differ"
                                + (differences.Count > 0 ? " (" + string.Join("; ", differences.Take(5)) + ")" : "") + $" | active {active}" + Environment.NewLine);
                    });
                });
            }

            Send(context, $"verifying {players.Count} bots, results in {path}");
        }

        [Command(Permission.Character, "Save test: give one online bot an item and one quest objective step and print the values the game holds (compare with the database after a restart). With fault 1 its next character batch also gets a row that cannot be written, and the save starts now.", "savetest")]
        public void HandleSaveTest(ICommandContext context,
            [Parameter("Item id to give (one).")]
            uint itemId,
            [Parameter("1: add a failing row to the next character batch and save now.")]
            uint fault)
        {
            foreach (Player player in Bots.BotManager.Instance.Bots.Select(b => b.Session?.Player).Where(p => p != null && p.Map != null))
            {
                List<QuestObjective> open = player.QuestManager.GetActiveQuests()
                    .Where(q => q.State == QuestState.Accepted)
                    .SelectMany(q => q)
                    .Where(o => o.Progress < o.ObjectiveInfo.Entry.Count)
                    .ToList();
                // the first open objective that takes a step (sequential objectives and some types ignore a plain advance; those get the
                // step through the quest system, objectives that still do not move are skipped)
                QuestObjective objective = null;
                ushort questId = 0;
                uint before = 0u;
                foreach (QuestObjective candidate in open.OrderBy(o => o.Progress + 1u < o.ObjectiveInfo.Entry.Count ? 0 : 1).Take(20))
                {
                    questId = (ushort)candidate.QuestInfo.Entry.Id;
                    before = candidate.Progress;
                    player.QuestManager.AdvanceObjective(questId, candidate.Index, 1u);
                    if (candidate.Progress == before)
                        player.QuestManager.QuestAchieveObjective(questId, candidate.Index);
                    if (candidate.Progress != before)
                    {
                        objective = candidate;
                        break;
                    }
                }
                if (objective == null)
                    continue;

                player.Inventory.ItemCreate(Game.Entity.Static.InventoryLocation.Inventory, itemId, 1u);
                if (fault == 1u)
                {
                    CharacterSave.InjectFault(player.CharacterId);
                    player.Save();
                }

                Send(context, $"savetest character={player.CharacterId} name={player.Name} item={itemId} count={player.Inventory.GetItemCount(itemId)} "
                    + $"quest={questId} index={objective.Index} progress={before}->{objective.Progress} fault={fault}");
                return;
            }

            Send(context, "savetest: no online bot with a quest objective in progress");
        }
    }
}
