using System.Linq;
using NexusForever.WorldServer.Bots;
using NexusForever.WorldServer.Command.Context;
using NexusForever.WorldServer.Command.Static;
using NexusForever.WorldServer.Game.RBAC.Static;

namespace NexusForever.WorldServer.Command.Handler
{
    [Command(Permission.Character, "A collection of commands to manage player bots.", "bot")]
    public class BotCommandCategory : CommandCategory
    {
        [Command(Permission.Character, "Log in a bot that follows and assists a player.", "add")]
        public void HandleBotAdd(ICommandContext context,
            [Parameter("Name of the online player the bot follows (case sensitive).")]
            string owner,
            [Parameter("Character name for the bot, created if it does not exist.")]
            string name,
            [Parameter("Class of the bot, for example Warrior, Medic, Esper.")]
            string className)
        {
            string error = BotManager.Instance.Add(owner, name, className);
            context.SendMessage(error ?? $"Bot {name} is logging in for {owner}. Use 'bot list' to see its state.");
        }

        [Command(Permission.Character, "Log out a bot, or every bot and the ambient population with the name 'all'.", "remove")]
        public void HandleBotRemove(ICommandContext context,
            [Parameter("Name of the bot, or 'all'.")]
            string name)
        {
            int count = BotManager.Instance.Remove(name);
            context.SendMessage(count == 0 ? $"No bot named {name}." : $"Removed {count} bot(s).");
        }

        [Command(Permission.Character, "Set the size of the ambient bot population, 0 removes them.", "target")]
        public void HandleBotTarget(ICommandContext context,
            [Parameter("Number of ambient bots to keep logged in.")]
            uint count)
        {
            BotManager.Instance.SetTarget((int)count);
            context.SendMessage($"Ambient bot target set to {count}. They log in a few per second.");
        }

        [Command(Permission.Character, "Change what companion bots do: follow, stay or passive. Target is a bot name, an owner name or 'all'.", "mode")]
        public void HandleBotMode(ICommandContext context,
            [Parameter("follow (default: trail and assist), stay (hold position, still fight nearby) or passive (follow, never attack or heal).")]
            string mode,
            [Parameter("Bot name, owner name or 'all'.")]
            string who)
        {
            if (!System.Enum.TryParse(mode, true, out BotMode parsed))
            {
                context.SendError("Mode must be follow, stay or passive.");
                return;
            }

            int count = 0;
            foreach (Bot bot in Companions(who))
            {
                bot.Mode = parsed;
                count++;
            }

            context.SendMessage(count == 0 ? $"No companion bot matches {who}." : $"{count} companion(s) set to {parsed}.");
        }

        [Command(Permission.Character, "Teleport companion bots to their owner. Target is a bot name, an owner name or 'all'.", "summon")]
        public void HandleBotSummon(ICommandContext context,
            [Parameter("Bot name, owner name or 'all'.")]
            string who)
        {
            int count = 0;
            foreach (Bot bot in Companions(who))
            {
                bot.Summon();
                count++;
            }

            context.SendMessage(count == 0 ? $"No companion bot matches {who}." : $"Summoning {count} companion(s).");
        }

        private static System.Collections.Generic.IEnumerable<Bot> Companions(string who)
        {
            return BotManager.Instance.Bots
                .Where(b => !b.IsAmbient)
                .Where(b => who.Equals("all", System.StringComparison.OrdinalIgnoreCase)
                    || b.Name.Equals(who, System.StringComparison.OrdinalIgnoreCase)
                    || b.OwnerName.Equals(who, System.StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        [Command(Permission.Character, "Test: an online quest bot drops its quests, takes the quest (prerequisites skipped) and travels to its targets; watch it with scripts/bot-feed.sh.", "quest")]
        public void HandleBotQuest(ICommandContext context,
            [Parameter("Quest2 id.")]
            ushort questId,
            [Parameter("Bot name, or 'any' for a quest bot of the quest's faction.")]
            string name)
        {
            context.SendMessage(BotQuests.ForceQuest(questId, name));
        }

        [Command(Permission.Character, "Forced quest sweep: start [minutes per quest], status or stop. Free quest bots take every open doable quest through the forced path; results in /cache/sweep-results.tsv (scripts/force-sweep.sh).", "sweep")]
        public void HandleBotSweep(ICommandContext context,
            [Parameter("start, status or stop.")]
            string action,
            [Parameter("Minutes per quest (start only).", ParameterFlags.Optional)]
            string minutes)
        {
            switch (action.ToLowerInvariant())
            {
                case "start":
                    context.SendMessage(ForceSweep.Start(int.TryParse(minutes, out int m) && m > 0 ? m : 8));
                    break;
                case "stop":
                    context.SendMessage(ForceSweep.Stop());
                    break;
                default:
                    context.SendMessage(ForceSweep.Status());
                    break;
            }
        }

        [Command(Permission.Character, "Show bot counts and the state of the companion bots.", "list")]
        public void HandleBotList(ICommandContext context)
        {
            var bots = BotManager.Instance.Bots.ToList();
            if (bots.Count == 0)
            {
                context.SendMessage($"No bots are running (ambient target {BotManager.Instance.TargetAmbient}).");
                return;
            }

            context.SendMessage($"Ambient target {BotManager.Instance.TargetAmbient}, running {bots.Count}: "
                + string.Join(", ", bots.GroupBy(b => b.State).Select(g => $"{g.Count()} {g.Key}")));

            foreach (Bot bot in bots.Where(b => !b.IsAmbient))
                context.SendMessage($"{bot.Name}: {bot.Class}, owner {bot.OwnerName}, {bot.State}, {bot.Mode}{(bot.Error != null ? " (" + bot.Error + ")" : "")}");
        }
    }
}
