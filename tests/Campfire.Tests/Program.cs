using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Campfire;
using Campfire.Link;
using Campfire.Link.Room;
using Campfire.Link.Minecraft;
using Campfire.Link.Runtime;
using Campfire.Platform;

[assembly: SupportedOSPlatform("linux")]

internal static class Tests
{
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private static async Task Main(string[] args)
    {
        string data = Path.Combine(Path.GetTempPath(), "campfire-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CAMPFIRE_DATA_DIR", data);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;

        var zero = RoomCode.Find("chat u/0000-0000-0000-0000 end");
        Check(zero?.NetworkName == "scaffolding-mc-0000-0000" && zero.NetworkSecret == "0000-0000", "known original invitation derivation");
        Check(RoomCode.Find("U/1000-0000-0000-0000") == null, "invalid checksum rejected");
        for (int i = 0; i < 128; i++)
        {
            var code = RoomCode.Create();
            if (RoomCode.Find(code.Code.ToLowerInvariant())?.Code != code.Code) throw new Exception("Invite roundtrip failed");
        }
        Check(true, "128 random invitations roundtrip through unchanged original parser");

        using (var encoded = new MemoryStream())
        {
            await RoomFraming.WriteRequestAsync(encoded, "c:ping", new byte[] { 1, 2, 3 }, token);
            Check(encoded.ToArray().SequenceEqual(new byte[] { 6, 99, 58, 112, 105, 110, 103, 0, 0, 0, 3, 1, 2, 3 }), "exact original room request bytes");
            encoded.Position = 0;
            var decoded = await RoomFraming.ReadRequestAsync(encoded, token);
            Check(decoded.Kind == "c:ping" && decoded.Body.SequenceEqual(new byte[] { 1, 2, 3 }), "room frame read roundtrip");
        }
        try
        {
            await RoomFraming.ReadResponseAsync(new MemoryStream(new byte[] { 0, 0, 16, 0, 1 }), token);
            throw new Exception("Oversized body accepted");
        }
        catch (InvalidDataException) { Check(true, "room response body limit enforced"); }

        var config = new EasyTierConfig { Hostname = "test", NetworkName = zero.NetworkName, NetworkSecret = zero.NetworkSecret };
        config.Peers.AddRange(LinkSettings.GetNodes());
        config.TcpWhitelist.Add("0"); config.UdpWhitelist.Add("0");
        string toml = config.ToToml();
        Check(PublicNodes.All.Count == 9 && config.Peers.Count == 9, "all nine original public nodes enabled by default");
        Check(toml.Contains("no_tun = true") && toml.Contains("enable_quic_proxy = true") && toml.Contains("p2p_only = false")
            && toml.Contains("data_compress_algo = 2") && toml.Contains("private_mode = true"), "original non-Win7 EasyTier parameters");
        AppSettings.Set("link.nodes", "tcp://127.0.0.1:9");
        Check(LinkSettings.GetNodes().SequenceEqual(PublicNodes.All.Select(n => n.Url)), "custom node settings are not consumed by the Linux CLI");
        config.DirectOnly = true;
        Check(config.ToToml().Contains("p2p_only = true"), "original direct-only flag");
        config.HideAddress = true;
        Check(config.ToToml().Contains("listeners = []") && config.ToToml().Contains("disable_p2p = true")
            && config.ToToml().Contains("p2p_only = false"), "original hidden-address precedence");

        byte[] body = Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();
        byte[] frame = GameProtocol.Frame(body, 64);
        int offset = 0;
        GameProtocol.ReadVarInt(frame, ref offset);
        Check(GameProtocol.Unwrap(frame.Skip(offset).ToArray(), 64, 4096).SequenceEqual(body), "original Minecraft compressed framing");

        var host = new PlayerProfile("Host", "test-host", "Campfire", PlayerKind.Host);
        using (var server = RoomServer.Start(host, 25565))
        using (var client = await RoomClient.TryConnectAsync(server.Port, token))
        {
            Check(client != null, "RoomClient TCP fingerprint handshake with original RoomServer");
            Check(await client.GetServerPortAsync(token) == 25565, "big-endian server port response");
            server.Room = new RoomInfo("Linux test", "original wire format", 2);
            Check((await client.GetRoomInfoAsync(token)).Name == "Linux test", "original room information JSON");
            var guest = new PlayerProfile("Guest", "test-guest", "Campfire", PlayerKind.Local);
            await client.SendHeartbeatAsync(guest, token);
            Check((await client.GetPlayersAsync(token)).Count == 2, "heartbeat admission and player list");
            try
            {
                await client.SendHeartbeatAsync(new PlayerProfile("Other", "other", "Campfire", PlayerKind.Local), token);
                throw new Exception("Full room accepted");
            }
            catch (RoomRejectedException ex) { Check(ex.Full, "original room capacity rejection"); }
            server.Remove(guest.MachineId);
            try { await client.SendHeartbeatAsync(guest, token); throw new Exception("Removed guest accepted"); }
            catch (RoomRejectedException ex) { Check(!ex.Full && !ex.Blocked, "original removal response"); }
        }

        await TestProxyAsync(token);
        await TestMappingsAsync(data);
        if (args.FirstOrDefault() == "--java-pid")
        {
            int pid = int.Parse(args[1]);
            Check(LinuxProcesses.JavaPids().Contains(pid), "Linux Java process discovery");
            Check(LinuxProcesses.Arguments(pid).Contains("--gameDir"), "Linux argv preserves original game arguments");
            Check(!LinuxProcesses.Arguments(pid).Contains("private-fixture-token"), "access token redacted before retaining argv");
            int port = int.Parse(args[2]);
            Check(LocalWorldFinder.FindProcess(port) == pid, "Linux TCP listener ownership");
            Check(LocalWorldFinder.HasUdpSocket(port, pid), "Linux UDP socket ownership");
            var game = Campfire.Worlds.RunningGame.FromProcess(pid);
            Check(game != null && game.GameDirectory.Contains(" "), "game directory with spaces from argv");
            using var opener = new LanOpener(pid, null);
            Check(await JavaAgent.LoadAsync(pid, token), "original JAR loaded through Linux jdk.attach");
            int? result = null;
            for (int i = 0; i < 100 && result == null; i++) { await Task.Delay(100, token); result = opener.ReadResult(); }
            Check(result > 0, "original Hook tick invoked publishServer in the JVM test fixture");
        }
        Console.WriteLine("All tests passed. These fixtures do not certify real Minecraft or Windows interoperability.");
    }

    private static async Task TestMappingsAsync(string data)
    {
        string root = Path.Combine(data, "game with spaces");
        string folder = Path.Combine(root, "versions", "1.20.1");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "1.20.1.json"), """
            {"downloads":{"client_mappings":{"url":"https://piston-data.mojang.com/fixture","sha1":"fixture-sha"}}}
            """);
        var source = GameMappings.FindSource(new[] { "java", "-cp", "/some/first.jar:" + Path.Combine(folder, "1.20.1.jar") });
        Check(source?.Sha1 == "fixture-sha", "Linux colon classpath and spaces locate mapping JSON");
        source = GameMappings.FindSource(new[] { "java", "--version", "1.20.1", "-DlegacyClassPath=" + Path.Combine(root, "libraries", "fixture.jar") });
        Check(source?.Sha1 == "fixture-sha", "Linux Forge-style libraries path locates mapping JSON");
    }

    private static async Task TestProxyAsync(CancellationToken token)
    {
        var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        try
        {
            using var gate = GameProxy.Start(new IPEndPoint(IPAddress.Loopback, 0), ((IPEndPoint)target.LocalEndpoint).Port);
            var serve = Task.Run(async () =>
            {
                using var server = await target.AcceptTcpClientAsync(token);
                byte[] incoming = new byte[1];
                await server.GetStream().ReadExactlyAsync(incoming, token);
                Check(incoming[0] == 0xFE, "GameProxy forwards legacy Minecraft ping");
                await server.GetStream().WriteAsync(new byte[] { 0xFF }, token);
            }, token);
            Check(await MinecraftProbe.IsServerAsync(gate.Port, TimeSpan.FromSeconds(3)), "Minecraft probe works through original host gate");
            await serve;
        }
        finally { target.Stop(); }

        target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        try
        {
            using var gate = GameProxy.Start(new IPEndPoint(IPAddress.Loopback, 0), ((IPEndPoint)target.LocalEndpoint).Port);
            byte[] encrypted = new byte[] { 255, 128, 0, 129, 200, 17, 91 };
            var serve = Task.Run(async () =>
            {
                using var server = await target.AcceptTcpClientAsync(token);
                var stream = server.GetStream();
                await ReadFrameAsync(stream, token); await ReadFrameAsync(stream, token);
                await stream.WriteAsync(GameProtocol.Frame(new byte[] { 1 }, -1), token);
                await stream.WriteAsync(encrypted, token);
            }, token);
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, gate.Port, token);
            using var handshake = new MemoryStream();
            GameProtocol.WriteVarInt(handshake, 0); GameProtocol.WriteVarInt(handshake, 763);
            WriteString(handshake, "localhost"); handshake.Write(new byte[] { 0x63, 0xDD }); GameProtocol.WriteVarInt(handshake, 2);
            await client.GetStream().WriteAsync(GameProtocol.Frame(handshake.ToArray(), -1), token);
            using var login = new MemoryStream(); GameProtocol.WriteVarInt(login, 0); WriteString(login, "Guest");
            await client.GetStream().WriteAsync(GameProtocol.Frame(login.ToArray(), -1), token);
            byte[] result = new byte[2 + encrypted.Length];
            await client.GetStream().ReadExactlyAsync(result, token);
            Check(result.SequenceEqual(new byte[] { 1, 1 }.Concat(encrypted)), "encrypted Minecraft bytes pass through without parsing or authentication changes");
            await serve;
        }
        finally { target.Stop(); }
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value); GameProtocol.WriteVarInt(stream, bytes.Length); stream.Write(bytes);
    }
    private static async Task ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] one = new byte[1]; int size = 0, shift = 0;
        do { await stream.ReadExactlyAsync(one, token); size |= (one[0] & 127) << shift; shift += 7; } while ((one[0] & 128) != 0);
        await stream.ReadExactlyAsync(new byte[size], token);
    }
}
