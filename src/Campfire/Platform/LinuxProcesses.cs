using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Campfire.Platform
{
    /// <summary>Linux replacements for Windows process command lines and IP Helper owner tables.</summary>
    internal static class LinuxProcesses
    {
        public static int? SelectedGamePid { get; set; }

        public static IEnumerable<int> JavaPids()
        {
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int pid)
                    || (SelectedGamePid.HasValue && SelectedGamePid.Value != pid))
                    continue;
                string path = ImagePath(pid);
                if (Path.GetFileName(path ?? "").StartsWith("java", StringComparison.Ordinal))
                    yield return pid;
            }
        }

        public static string ImagePath(int pid)
        {
            try { return new FileInfo($"/proc/{pid}/exe").LinkTarget; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        public static IReadOnlyList<string> Arguments(int pid)
        {
            try
            {
                var args = Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{pid}/cmdline"))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                // The account token is irrelevant to every operation in this client.
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--accessToken" && i + 1 < args.Length) args[i + 1] = "<redacted>";
                    else if (args[i].StartsWith("--accessToken=", StringComparison.Ordinal)) args[i] = "--accessToken=<redacted>";
                }
                return args;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        public static IReadOnlyList<(int Port, int Pid)> Sockets(bool udp, int? onlyPid = null)
        {
            var byInode = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string name in udp ? new[] { "udp", "udp6" } : new[] { "tcp", "tcp6" })
            {
                try
                {
                    foreach (string line in File.ReadLines("/proc/net/" + name).Skip(1))
                    {
                        var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length < 10 || (!udp && fields[3] != "0A")) continue;
                        int colon = fields[1].LastIndexOf(':');
                        if (colon >= 0 && int.TryParse(fields[1].Substring(colon + 1), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out int port)) byInode[fields[9]] = port;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            var sockets = new HashSet<(int, int)>();
            foreach (int pid in onlyPid.HasValue ? new[] { onlyPid.Value } : JavaPids())
            {
                try
                {
                    foreach (string fd in Directory.EnumerateFiles($"/proc/{pid}/fd"))
                    {
                        string link;
                        try { link = new FileInfo(fd).LinkTarget; }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                        if (link != null && link.StartsWith("socket:[", StringComparison.Ordinal) && link.EndsWith("]", StringComparison.Ordinal)
                            && byInode.TryGetValue(link.Substring(8, link.Length - 9), out int port)) sockets.Add((port, pid));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return sockets.OrderBy(s => s.Item1).ToList();
        }
    }
}
