using System;
using System.IO;
using System.Threading;
using NexusForever.WorldServer.Command.Context;
using NLog;

namespace NexusForever.WorldServer.Command
{
    /// <summary>
    /// Runs console commands dropped into a file, for scripts (scripts/gt.sh): each line of commands.txt is "id TAB command".
    /// Output goes to out/id.txt and out/id.txt.done appears when the command has finished.
    /// </summary>
    public static class FileCommandInbox
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();
        private static Timer timer;

        private static string Directory => Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory;

        public static void Start()
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "out"));
            timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private static void Poll()
        {
            try
            {
                string inbox = Path.Combine(Directory, "commands.txt");
                if (!File.Exists(inbox))
                    return;

                // take the file over so the script can keep appending to a fresh one
                string work = inbox + ".work";
                File.Move(inbox, work, true);

                foreach (string line in File.ReadAllLines(work))
                {
                    string[] parts = line.Split('\t', 2);
                    if (parts.Length != 2 || parts[0].Length == 0 || parts[0].IndexOfAny(new[] { '/', '\\', '.' }) >= 0)
                        continue;

                    string output = Path.Combine(Directory, "out", parts[0] + ".txt");
                    var context = new FileCommandContext(output);

                    log.Info($"Script command: {parts[1]}");
                    CommandManager.Instance.HandleCommandDelay(context, parts[1]);
                    CommandManager.Instance.HandleCommandDelay(context, $"gt done {parts[0]}");
                }

                File.Delete(work);
            }
            catch (Exception exception)
            {
                log.Error(exception);
            }
        }
    }
}
