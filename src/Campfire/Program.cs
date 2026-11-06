using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Campfire.Link;
using Campfire.Link.Lobby;
using Campfire.Link.Minecraft;
using Campfire.Link.Runtime;
using Campfire.Localization;
using Campfire.Platform;

namespace Campfire
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.FirstOrDefault() == ProcessSupervisor.Command)
                    return ProcessSupervisor.RunAsync(args).GetAwaiter().GetResult();
                var options = Options.Parse(args);
                if (options.Command is "help" or "--help" or "-h") { Console.WriteLine(Help); return 0; }
                if (options.Command is "version" or "--version") { Console.WriteLine("Campfire Linux CLI 1.1.31 / EasyTier 2.6.4"); return 0; }
                if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new PlatformNotSupportedException("This build targets Linux x86_64.");
                string directory = options.Get("data-dir");
                if (directory != null) Environment.SetEnvironmentVariable("CAMPFIRE_DATA_DIR", Path.GetFullPath(directory));
                Text.Language = AppSettings.ParseLanguage(options.Get("lang")) ?? AppSettings.LoadLanguage() ?? Text.DetectSystemLanguage();
                if (AppSettings.DataDirectory.Contains('|'))
                    throw new ArgumentException("The data directory cannot contain '|': the original Java agent uses it as an option separator.");
                using var cancel = new CancellationTokenSource();
                ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; cancel.Cancel(); };
                Console.CancelKeyPress += interrupt;
                using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cancel.Cancel(); });
                try
                {
                    int result = 0;
                    using var context = new SerialContext();
                    context.Run(async () => result = await ExecuteAsync(options, context, cancel.Token));
                    return result;
                }
                finally { Console.CancelKeyPress -= interrupt; }
            }
            catch (OperationCanceledException) { return 130; }
            catch (Exception ex)
            {
                Console.Error.WriteLine("campfire: " + ex.Message);
                return 1;
            }
        }

        private static async Task<int> ExecuteAsync(Options options, SerialContext context, CancellationToken token)
        {
            switch (options.Command)
            {
                case "nodes":
                    if (options.Json) Console.WriteLine(JsonSerializer.Serialize(PublicNodes.All.Select(n => new { n.Id, n.Url, n.Provider, n.IsLimited })));
                    else foreach (var node in PublicNodes.All) Console.WriteLine($"{node.Id}\t{node.Url}\t{node.Provider}");
                    return 0;
                case "inspect-code":
                    var code = RoomCode.Find(options.Code) ?? throw new ArgumentException("Invalid original Campfire invitation code.");
                    Console.WriteLine(code.Code);
                    Console.WriteLine("Network: " + code.NetworkName);
                    Console.WriteLine("Nodes are local original settings; they are not encoded in the invitation.");
                    return 0;
                case "install":
                    using (AcquireLock())
                    {
                        if (options.Get("archive") is string archive) EasyTierPackage.InstallArchive(Path.GetFullPath(archive));
                        else await EasyTierPackage.InstallAsync(Progress(options.Json), token);
                        if (!await EasyTierPackage.CanRunAsync()) throw new IOException("EasyTier could not execute on this system.");
                        Emit(options.Json, new { state = "installed", version = EasyTierPackage.Version, directory = EasyTierPackage.InstallDirectory },
                            "EasyTier 2.6.4 verified: " + EasyTierPackage.InstallDirectory);
                    }
                    return 0;
                case "doctor":
                    if (options.Json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { data_directory = AppSettings.DataDirectory,
                            easytier_version = EasyTierPackage.Version, installed = EasyTierPackage.IsInstalled(),
                            nodes = PublicNodes.All.Where(LinkSettings.IsNodeEnabled).Select(n => n.Id),
                            games = Worlds.RunningGame.Find().Select(g => new { g.ProcessId, g.GameDirectory }) }));
                        return 0;
                    }
                    Console.WriteLine("Data: " + AppSettings.DataDirectory);
                    Console.WriteLine("EasyTier: " + (EasyTierPackage.IsInstalled() ? "2.6.4 verified" : "not installed (run campfire install)"));
                    Console.WriteLine("Original nodes: " + string.Join(", ", PublicNodes.All.Where(LinkSettings.IsNodeEnabled).Select(n => n.Id)));
                    foreach (var game in Worlds.RunningGame.Find()) Console.WriteLine($"Game PID {game.ProcessId}: {game.GameDirectory}");
                    Console.WriteLine("Windows interoperability requires a real Windows peer; local checks do not certify it.");
                    return 0;
            }

            using var roomLock = AcquireLock();
            Configure(options);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(LinkSettings.PlayerName))
                throw new ArgumentException("Specify --name NAME before creating or joining a room (saved for later runs).");
            if (!await LobbyClient.IsNameAllowedAsync(LinkSettings.PlayerName, token))
                throw new ArgumentException("The original service rejected this player name.");
            var account = await GameAccount.RefreshAsync();
            if (account != null && !Regex.IsMatch(account.Name, @"^[A-Za-z0-9_]{3,16}$") && !options.Has("allow-invalid-game-name"))
                throw new ArgumentException("Minecraft may reject the running game's name. Use --allow-invalid-game-name to continue as in the original confirmation dialog.");

            var session = new LinkSession(context);
            string previous = null;
            session.Changed += (_, _) =>
            {
                var snapshot = new
                {
                    state = session.Stage.ToString().ToLowerInvariant(), role = session.Role.ToString().ToLowerInvariant(),
                    code = session.Stage == LinkStage.Ready && session.Role == LinkRole.Host ? session.Code?.Code : null,
                    game_port = session.MinecraftPort, local_port = session.LocalPort,
                    address = session.LocalPort > 0 ? "127.0.0.1:" + session.LocalPort : null,
                    nodes = session.ConnectedNodes, players = session.Players.Select(p => new { p.Name, p.GameName, p.Kind, p.LatencyMs }).ToArray(),
                    auto_lan = session.AutoLan.ToString(), error = session.Error?.Message,
                };
                string encoded = JsonSerializer.Serialize(snapshot);
                if (encoded == previous) return;
                previous = encoded;
                if (options.Json) Console.WriteLine(encoded);
                else
                {
                    string detail = snapshot.code != null ? "  Invitation: " + snapshot.code
                        : snapshot.address != null ? "  Minecraft: " + snapshot.address : "";
                    Console.WriteLine($"[{snapshot.state}] {snapshot.role}{detail}  players={session.Players.Count} nodes={session.ConnectedNodes} LAN={snapshot.auto_lan}");
                    if (snapshot.error != null) Console.Error.WriteLine(snapshot.error);
                }
            };
            using var stop = token.Register(() => context.Post(_ => session.Stop(), null));
            try
            {
                token.ThrowIfCancellationRequested();
                if (options.Command == "host")
                    await session.StartHost(options.Int("port", 1, 65535),
                        options.Get("reuse-code") is string reuse ? RoomCode.Find(reuse) ?? throw new ArgumentException("Invalid reuse code.") : null);
                else
                    await session.StartJoin(RoomCode.Find(options.Code) ?? throw new ArgumentException("Invalid original Campfire invitation code."));
                return session.Stage == LinkStage.Failed ? 1 : token.IsCancellationRequested ? 130 : 0;
            }
            finally { session.Shutdown(); }
        }

        private static FileStream AcquireLock()
        {
            try { return new FileStream(Path.Combine(AppSettings.DataDirectory, "cli.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new IOException("Another Campfire CLI is using this data directory.", ex); }
        }

        private static void Configure(Options o)
        {
            if (o.Get("name") is string name)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 16 || name.Any(char.IsControl))
                    throw new ArgumentException("Player name must contain 1–16 characters without control characters.");
                LinkSettings.PlayerName = name;
            }
            if (o.Get("room-name") is string room) LinkSettings.LobbyTitle = room;
            if (o.Get("notice") is string notice) LinkSettings.RoomNotice = notice;
            if (o.Int("max-players", 0, 100) is int limit)
            {
                if (limit == 1) throw new ArgumentException("Use --max-players 0 for unlimited, or 2–100.");
                LinkSettings.MaxPlayers = limit;
            }
            if (o.Int("pid", 1, int.MaxValue) is int pid) LinuxProcesses.SelectedGamePid = pid;
            if (o.Int("local-port", 0, 65535) is int local) LinkSettings.LocalPort = local;
            if (o.Int("search-timeout", 30, 120) is int timeout)
            {
                if (!LinkSettings.SearchTimeoutChoices.Contains(timeout)) throw new ArgumentException("Search timeout must be 30, 60 or 120 seconds.");
                LinkSettings.SearchTimeoutSeconds = timeout;
            }
            if (o.Has("no-auto-lan")) LinkSettings.AutoOpenLan = false;
            if (o.Has("auto-lan")) LinkSettings.AutoOpenLan = true;
            if (o.Has("no-lan-share")) LinkSettings.ShareWithLan = false;
            if (o.Has("lan-share")) LinkSettings.ShareWithLan = true;
            if (o.Has("direct-only")) LinkSettings.AllowRelay = false;
            if (o.Has("allow-relay")) LinkSettings.AllowRelay = true;
            if (o.Has("hide-address")) LinkSettings.HideAddress = true;
            if (o.Has("show-address")) LinkSettings.HideAddress = false;
            if (o.Has("no-backup")) LinkSettings.BackupWorld = false;
            if (o.Has("backup")) LinkSettings.BackupWorld = true;
            if (o.Has("voice")) LinkSettings.Voice = true;
            if (o.Has("no-voice")) LinkSettings.Voice = false;
            // Never consume custom URLs from migrated settings; nodes come only from PublicNodes.All.
            if (LinkSettings.GetNodes().Count == 0) throw new ArgumentException("No original public nodes are enabled in settings.ini.");
        }

        private static IProgress<DownloadProgress> Progress(bool json) => new Progress<DownloadProgress>(p =>
        {
            if (!json) Console.Error.WriteLine($"Downloading EasyTier 2.6.4: {p.Fraction:P0}");
        });

        private static void Emit(bool json, object value, string message) => Console.WriteLine(json ? JsonSerializer.Serialize(value) : message);

        private sealed class Options
        {
            private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
            { "json", "no-auto-lan", "auto-lan", "no-lan-share", "lan-share", "direct-only", "allow-relay", "hide-address", "show-address", "no-backup", "backup", "voice", "no-voice", "allow-invalid-game-name" };
            private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
            { "data-dir", "lang", "archive", "name", "port", "reuse-code", "local-port", "search-timeout", "room-name", "notice", "max-players", "pid" };
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
            public string Command { get; private set; }
            public string Code { get; private set; }
            public bool Json => Has("json");
            public bool Has(string key) => _values.ContainsKey(key);
            public string Get(string key) => _values.TryGetValue(key, out string value) ? value : null;
            public int? Int(string key, int min, int max)
            {
                string value = Get(key);
                if (value == null) return null;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result < min || result > max)
                    throw new ArgumentException($"--{key} must be an integer from {min} to {max}.");
                return result;
            }
            public static Options Parse(string[] args)
            {
                if (args.Contains("--help")) return new Options { Command = "help" };
                var result = new Options { Command = args.FirstOrDefault() ?? "help" };
                if (!new[] { "host", "join", "install", "nodes", "doctor", "inspect-code", "help", "--help", "-h", "version", "--version" }.Contains(result.Command))
                    throw new ArgumentException("Unknown command. Run campfire --help.");
                for (int i = 1; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (!arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        if (result.Command is not ("join" or "inspect-code") || result.Code != null) throw new ArgumentException("Unexpected argument: " + arg);
                        result.Code = arg; continue;
                    }
                    string key = arg.Substring(2);
                    if (Flags.Contains(key)) result._values.Add(key, "true");
                    else if (Values.Contains(key) && i + 1 < args.Length) result._values.Add(key, args[++i]);
                    else throw new ArgumentException("Unknown option or missing value: " + arg);
                }
                if (result.Command is "join" or "inspect-code" && result.Code == null) throw new ArgumentException("An original Campfire invitation code is required.");
                if (result.Get("lang") is string lang && lang is not ("en" or "zh")) throw new ArgumentException("--lang must be en or zh.");
                foreach (var pair in new[] { ("auto-lan", "no-auto-lan"), ("lan-share", "no-lan-share"), ("direct-only", "allow-relay"), ("hide-address", "show-address"), ("backup", "no-backup"), ("voice", "no-voice") })
                    if (result.Has(pair.Item1) && result.Has(pair.Item2)) throw new ArgumentException("Conflicting options: --" + pair.Item1 + " / --" + pair.Item2);
                var allowed = new HashSet<string>(new[] { "data-dir", "lang", "json" }, StringComparer.Ordinal);
                if (result.Command is "host" or "join")
                    allowed.UnionWith(new[] { "name", "pid", "direct-only", "allow-relay", "hide-address", "show-address", "allow-invalid-game-name" });
                if (result.Command == "host")
                    allowed.UnionWith(new[] { "port", "reuse-code", "room-name", "notice", "max-players", "auto-lan", "no-auto-lan", "backup", "no-backup", "voice", "no-voice" });
                if (result.Command == "join")
                    allowed.UnionWith(new[] { "local-port", "search-timeout", "lan-share", "no-lan-share" });
                if (result.Command == "install") allowed.Add("archive");
                foreach (string key in result._values.Keys)
                    if (!allowed.Contains(key)) throw new ArgumentException($"--{key} does not apply to {result.Command}.");
                return result;
            }
        }

        private const string Help = """
        Campfire Linux CLI — original Campfire rooms, EasyTier 2.6.4, original public nodes
        GPL-3.0; modified Linux CLI based on Xocio/Campfire 1.1.31. See LICENSE.

        campfire install [--archive original-linux-2.6.4.zip]
        campfire host --name NAME [--port LAN_PORT] [--reuse-code U/....]
        campfire join 'U/XXXX-XXXX-XXXX-XXXX' --name NAME
        campfire nodes | doctor | inspect-code 'U/....' | version

        Common: --data-dir PATH --lang en|zh --json
        Both:   --pid PID --allow-invalid-game-name
                --allow-relay / --direct-only --show-address / --hide-address
        Host:   --room-name TEXT --notice TEXT --max-players 0|2..100
                --auto-lan / --no-auto-lan --backup / --no-backup --voice / --no-voice
        Join:   --local-port 0..65535 --search-timeout 30|60|120
                --lan-share / --no-lan-share

        Run your Minecraft Java game separately. Without --port, the host detects its
        LAN listener and can attach the original Java agent to open the world to LAN.
        Join through Minecraft's LAN list for the original LAN authentication behavior.
        Keep this process running; Ctrl+C or SIGTERM leaves/closes the room.
        Preferences are saved using the original keys. Defaults: all original public
        nodes, relay allowed, address visible, LAN sharing/auto-LAN/backup on, voice off.
        No custom nodes or server endpoints are accepted. No GUI or telemetry.
        """;
    }
}
