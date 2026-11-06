using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace Campfire.Link.Minecraft
{
    /// <summary>
    /// The account of a Minecraft game running on this computer, read from the game's command line. Every
    /// launcher passes the player name, UUID and account type there.
    /// </summary>
    /// <remarks>
    /// Only these three values are read. The command line also carries the access token, which is never kept,
    /// logged or sent anywhere.
    /// </remarks>
    internal sealed class GameAccount
    {
        private GameAccount(string name, string uuid, bool isPremium, int processId)
        {
            Name = name;
            Uuid = uuid;
            IsPremium = isPremium;
            ProcessId = processId;
        }

        public string Name { get; }

        /// <summary>UUID without dashes, or <c>null</c>.</summary>
        public string Uuid { get; }

        /// <summary>Whether the game was started with a Microsoft (premium) account.</summary>
        public bool IsPremium { get; }

        /// <summary>Process id of the running game.</summary>
        public int ProcessId { get; }

        /// <summary>The account of the running game, or <c>null</c> when no game is running.</summary>
        public static GameAccount Current { get; private set; }

        /// <summary>Looks for a running game and updates <see cref="Current"/>.</summary>
        public static async Task<GameAccount> RefreshAsync()
        {
            var account = LinkSettings.ReadGameAccount ? await Task.Run(() => Detect()).ConfigureAwait(false) : null;
            Current = account;
            return account;
        }

        private static GameAccount Detect()
        {
            foreach (int pid in Platform.LinuxProcesses.JavaPids())
            {
                var arguments = ReadArguments(pid);
                string name = ValueAfter(arguments, "--username");
                if (string.IsNullOrEmpty(name)) continue;
                string uuid = ValueAfter(arguments, "--uuid")?.Replace("-", "").ToLowerInvariant();
                string type = ValueAfter(arguments, "--userType");
                bool premium = (type == "msa" || type == "mojang") && uuid != null && uuid.Length == 32;
                return new GameAccount(name, uuid, premium, pid);
            }
            return null;
        }

        private static string ValueAfter(IReadOnlyList<string> arguments, string option)
        {
            for (int i = 0; i + 1 < arguments.Count; i++)
                if (arguments[i] == option) return arguments[i + 1];
            return null;
        }

        internal static IReadOnlyList<string> ReadArguments(int processId) => Platform.LinuxProcesses.Arguments(processId);
    }
}
