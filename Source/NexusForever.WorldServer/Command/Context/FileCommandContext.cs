using System.Collections.Immutable;
using System.IO;
using NexusForever.Shared.GameTable.Static;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.RBAC;
using NexusForever.WorldServer.Game.RBAC.Static;

namespace NexusForever.WorldServer.Command.Context
{
    /// <summary>
    /// Console permissions, output written to a file so a script can run a command and read the answer without a game client.
    /// </summary>
    public class FileCommandContext : ICommandContext
    {
        private readonly string outputPath;

        public WorldEntity Invoker { get; }
        public WorldEntity Target { get; }

        public Language Language { get; } = Language.English;
        public ImmutableHashSet<Permission> Permissions { get; }

        public FileCommandContext(string outputPath)
        {
            this.outputPath = outputPath;

            RBACRole role = RBACManager.Instance.GetRole(Role.Console);
            if (role == null)
                throw new InvalidDataException("Console role doesn't exist!");

            Permissions = role.Permissions.Keys.ToImmutableHashSet();
        }

        public void SendMessage(string message)
        {
            Append(message);
        }

        public void SendError(string message)
        {
            Append($"ERROR: {message}");
        }

        public T GetTargetOrInvoker<T>() where T : WorldEntity
        {
            return null;
        }

        /// <summary>
        /// Tell the waiting script that every message of the command has been written.
        /// </summary>
        public void Complete()
        {
            File.WriteAllText(outputPath + ".done", string.Empty);
        }

        private void Append(string message)
        {
            File.AppendAllText(outputPath, message + "\n");
        }
    }
}
