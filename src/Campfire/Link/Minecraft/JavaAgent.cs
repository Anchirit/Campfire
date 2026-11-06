using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Campfire.Localization;

namespace Campfire.Link.Minecraft
{
    /// <summary>
    /// Attaches the small Java agent that works from inside the host's own Minecraft server: it adds the room
    /// header, footer, welcome message and announcement, and it opens an imported map to LAN when asked
    /// (see <see cref="LanOpener"/>).
    /// </summary>
    internal static class JavaAgent
    {
        private const string AgentResource = "Campfire.CampfireAgent.jar";
        private const string AttachResource = "Campfire.CampfireAttach.jar";
        private const string HookResource = "Campfire.CampfireHook.jar";
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>How long to wait for the hook to confirm it runs.</summary>
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(6);

        /// <summary>The room state the hook reads; see <see cref="AgentState"/>.</summary>
        internal static string StatePath(int processId) => Path.Combine(LinkSettings.DataDirectory, "overlay-" + processId + ".txt");

        /// <summary>Written by the hook once it shows the overlay in the game.</summary>
        internal static string ReadyPath(int processId) => Path.Combine(LinkSettings.DataDirectory, "overlay-" + processId + ".ready");

        /// <summary>
        /// Loads the agent into <paramref name="processId"/> and waits until it shows the overlay. Returns
        /// <c>false</c> when no Java on this computer can attach to the game, the agent cannot be loaded or
        /// the game is not a version it knows. An <see cref="AgentState"/> for the process must already exist.
        /// </summary>
        public static async Task<bool> AttachAsync(int processId, CancellationToken cancellation)
        {
            if (!await LoadAsync(processId, cancellation).ConfigureAwait(false))
                return false;

            try
            {
                // The hook's own confirmation tells that it runs in this game.
                var deadline = DateTime.UtcNow + ReadyTimeout;
                while (!File.Exists(ReadyPath(processId)))
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        LinkLog.Warn("The Java agent was loaded, but the game did not start the overlay; see agent.log.");
                        return false;
                    }
                    await Task.Delay(250, cancellation).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            LinkLog.Info("In-game overlay agent attached to game process " + processId + ".");
            return true;
        }

        /// <summary>
        /// Loads the agent into <paramref name="processId"/>, with the mapping file of its release when that is
        /// obfuscated. Returns whether the agent was loaded. The hook is installed once per game.
        /// </summary>
        public static async Task<bool> LoadAsync(int processId, CancellationToken cancellation)
        {
            if (processId <= 0)
                return false;

            string mappings = null;
            try
            {
                mappings = await GameMappings.PrepareAsync(processId, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                // Without mappings the agent works with official names only.
                LinkLog.Warn("The mapping file could not be prepared: " + ex.Message);
            }

            await Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                string java = await Task.Run(() => FindAttachJava(processId), cancellation).ConfigureAwait(false);
                if (java == null)
                {
                    LinkLog.Warn("No Java on this computer can attach to the running game; the in-game agent is skipped.");
                    return false;
                }

                // Compared by content hash.
                string directory = Path.Combine(LinkSettings.DataDirectory, "agent",
                    "v" + ResourceHash(AgentResource) + "-" + ResourceHash(HookResource) + "-" + ResourceHash(AttachResource));
                Directory.CreateDirectory(directory);
                string agent = Extract(AgentResource, Path.Combine(directory, "CampfireAgent.jar"));
                string attach = Extract(AttachResource, Path.Combine(directory, "CampfireAttach.jar"));
                Extract(HookResource, Path.Combine(directory, "CampfireHook.jar"));

                string options = "state=" + LinkSettings.DataDirectory + (mappings != null ? "|mappings=" + mappings : "");
                var startInfo = new ProcessStartInfo(java)
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                foreach (string argument in new[] { "--add-modules", "jdk.attach", "-cp", attach, "campfire.CampfireAttach",
                    processId.ToString(CultureInfo.InvariantCulture), agent, options }) startInfo.ArgumentList.Add(argument);

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    using var stopBridge = cancellation.Register(() => Platform.ProcessSupervisor.TryKill(process));
                    bool exited = await Task.Run(() => process.WaitForExit(20000), CancellationToken.None).ConfigureAwait(false);
                    if (!exited)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                        }
                        LinkLog.Warn("The Java agent did not finish attaching in time.");
                        return false;
                    }

                    string errorText = await error.ConfigureAwait(false);
                    await output.ConfigureAwait(false);
                    if (process.ExitCode != 0)
                    {
                        LinkLog.Warn("The Java agent could not be attached: " + errorText.Trim());
                        return false;
                    }
                }

                LinkLog.Info("Java agent loaded into game process " + processId + (mappings != null ? " with mappings." : "."));
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception
                                       || ex is NotSupportedException || ex is ArgumentException)
            {
                LinkLog.Warn("The Java agent could not be attached: " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                LinkLog.Warn("The Java agent could not be attached: " + ex.Message);
                return false;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static readonly Dictionary<string, bool> AttachSupport = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A Java that can attach to the game: its own since Java 9, otherwise another one on this computer.
        /// </summary>
        private static string FindAttachJava(int processId)
        {
            return AttachCandidates(FindJava(processId)).FirstOrDefault(CanAttach);
        }

        private static IEnumerable<string> AttachCandidates(string game)
        {
            if (game != null) yield return game;
            string home = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "bin", "java");
            foreach (string root in new[] { "/usr/lib/jvm", "/opt/java", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft", "runtime") })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string java in FindJavaFiles(root)) yield return java;
            }
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(directory)) yield return Path.Combine(directory, "java");
        }

        private static IEnumerable<string> FindJavaFiles(string root)
        {
            try
            {
                return Directory.EnumerateFiles(root, "java", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "bin").ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }

        /// <summary>Whether <paramref name="java"/> has the attach module.</summary>
        private static bool CanAttach(string java)
        {
            lock (AttachSupport)
            {
                if (AttachSupport.TryGetValue(java, out bool known))
                    return known;
            }

            bool supported = false;
            try
            {
                if (File.Exists(java))
                {
                    var startInfo = new ProcessStartInfo(java, "--list-modules")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (var process = Process.Start(startInfo))
                    {
                        Task<string> error = process.StandardError.ReadToEndAsync();
                        var output = process.StandardOutput.ReadToEndAsync();
                        if (!process.WaitForExit(10000)) Platform.ProcessSupervisor.TryKill(process);
                        process.WaitForExit();
                        supported = output.GetAwaiter().GetResult().IndexOf("jdk.attach@", StringComparison.Ordinal) >= 0;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException
                                       || ex is System.ComponentModel.Win32Exception || ex is AggregateException)
            {
            }

            lock (AttachSupport)
                AttachSupport[java] = supported;
            return supported;
        }

        private static string FindJava(int processId) => Platform.LinuxProcesses.ImagePath(processId);

        private static string ResourceHash(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new InvalidOperationException("Missing embedded resource " + name + ".");
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
                }
            }
        }

        private static string Extract(string name, string path)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new InvalidOperationException("Missing embedded resource " + name + ".");

                if (File.Exists(path) && new FileInfo(path).Length == stream.Length)
                    return path;

                string temporary = path + ".tmp";
                using (var file = File.Create(temporary))
                    stream.CopyTo(file);
                File.Copy(temporary, path, true);
                File.Delete(temporary);
                return path;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    /// <summary>
    /// Tells the hook in the game that the room is open, and in which language to speak. The file is rewritten
    /// every few seconds and deleted when the room ends; the hook stops and clears the player list as soon as
    /// it is gone or no longer fresh.
    /// </summary>
    internal sealed class AgentState : IDisposable
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private readonly object _sync = new object();
        private readonly string _path;
        private readonly string _readyPath;
        private readonly Timer _timer;
        private bool _disposed;
        private volatile string _notice;

        /// <summary>The room announcement shown in the game; one line, or empty for none.</summary>
        public string Notice
        {
            get => _notice;
            set
            {
                _notice = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
                Write();
            }
        }

        public AgentState(int processId)
        {
            _path = JavaAgent.StatePath(processId);
            _readyPath = JavaAgent.ReadyPath(processId);
            // Clears the confirmation of an earlier room.
            TryDelete(_readyPath);
            Write();
            _timer = new Timer(_ => Write(), null, Interval, Interval);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            _timer.Dispose();
            TryDelete(_path);
            TryDelete(_readyPath);
        }

        private void Write()
        {
            var lines = new[]
            {
                "time=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                "tabTitle=" + Text.Get("Link.Game.TabTitle"),
                "welcome=" + Text.Get("Link.Game.Welcome"),
                "host=" + Text.Get("Link.Game.Host"),
                "online=" + Text.Get("Link.Game.Online"),
                "players=" + Text.Get("Link.Game.PlayersValue"),
                "latency=" + Text.Get("Link.Game.Latency"),
                "latencyValue=" + Text.Get("Link.Game.LatencyValue"),
                "measuring=" + Text.Get("Link.Game.LatencyMeasuring"),
                "noticeTitle=" + Text.Get("Link.Game.Notice"),
                "notice=" + (_notice ?? ""),
            };

            lock (_sync)
            {
                if (_disposed)
                    return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    // Written aside and swapped in.
                    string temporary = _path + ".tmp";
                    File.WriteAllText(temporary, string.Join("\n", lines) + "\n", Utf8);
                    if (File.Exists(_path))
                        File.Replace(temporary, _path, null);
                    else
                        File.Move(temporary, _path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }
}
