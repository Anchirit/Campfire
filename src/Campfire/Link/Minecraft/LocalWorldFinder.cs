using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Campfire.Link.Minecraft
{
    /// <summary>
    /// Waits for "Open to LAN" on this computer.
    /// </summary>
    /// <remarks>
    /// It reads the system table of listening TCP ports, picks those owned by a Java process and pings them
    /// locally. A dedicated server running on this computer is found the same way.
    /// </remarks>
    internal static class LocalWorldFinder
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

        /// <summary>How long a port that did not answer a ping is skipped.</summary>
        private static readonly TimeSpan RetryRejected = TimeSpan.FromSeconds(20);

        /// <summary>Returns the game port of a Minecraft world or server running on this computer.</summary>
        /// <param name="accept">Decides about each world found by its port, or <c>null</c> to take the first one.</param>
        public static async Task<int> WaitForLocalWorldAsync(CancellationToken cancellation, Func<int, bool> accept = null)
        {
            var rejected = new Dictionary<int, DateTime>();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (int port in GetJavaListeningPorts())
                {
                    if (rejected.TryGetValue(port, out var since) && DateTime.UtcNow - since < RetryRejected)
                        continue;
                    if (await MinecraftProbe.IsServerAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(false)
                        && (accept == null || accept(port)))
                        return port;
                    rejected[port] = DateTime.UtcNow;
                }
                await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
            }
        }

        /// <summary>The process listening on TCP <paramref name="port"/>, or 0 when none does.</summary>
        public static int FindProcess(int port)
        {
            return Platform.LinuxProcesses.Sockets(false).FirstOrDefault(s => s.Port == port).Pid;
        }

        public static bool HasUdpSocket(int port, int pid)
        {
            return pid > 0 && Platform.LinuxProcesses.Sockets(true, pid).Any(s => s.Port == port);
        }

        private static IEnumerable<int> GetJavaListeningPorts()
        {
            return Platform.LinuxProcesses.Sockets(false).Select(s => s.Port).Distinct().OrderBy(p => p);
        }
    }
}
