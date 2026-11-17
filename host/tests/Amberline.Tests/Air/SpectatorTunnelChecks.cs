using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Network.Spectator;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 853 (spectating S4b): a friend spectates through the player tool's pinned tunnel, and the grid serves a game only to its own
/// spectators. All on free loopback ports, never 8088: the stock client's request (one <c>GET ... HTTP/1.0</c> per connection, read to the
/// close, <see cref="SpectatorGridChecks.GetAsync"/>), the friend's player tool (<see cref="AirRemoteProxy"/>) with its spectator port, the
/// host's pinned HTTPS port (<see cref="AirRemoteTunnel.MapSpectate"/>) and a lobby whose grid has the rule both lines' Program.cs sets
/// (<see cref="AirSpectatorLobbyChecks.Rig"/>). No game client runs: a short-lived cmd stands in for the spectator client's process.
/// </summary>
public static class SpectatorTunnelChecks
{
    [Check("server.spectate-tunnel-serves-only-spectators")]
    public static void TheTunnelCarriesASpectatorsRequestsAndTheGridServesOnlyItsSpectators() => ServesOnlySpectatorsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 854 (restated): the port opens with the first spectator launch and stays until the tool ends (it closed with the client
    /// before), because AIR 1.52 asks endOfGameStats there right after a drop-in spectator's client ends; the classic start opens it for its
    /// whole session (the 2011 launcher's own Maestro starts that client).
    /// </summary>
    [Check("server.spectate-companion-port-follows-the-launch")]
    public static void TheFriendsSpectatorPortOpensWithTheSpectatorLaunchAndStaysForTheSession() => PortFollowsTheLaunchAsync().GetAwaiter().GetResult();

    private static async Task ServesOnlySpectatorsAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-spectate-tunnel-").FullName;
        using var certificate = AirRemoteChecks.CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var games = new List<Process>();
        try
        {
            await using var rig = new AirSpectatorLobbyChecks.Rig(root);
            var owner = rig.SignIn("tunnelowner");
            var player = rig.SignIn("tunnelplayer", throughTunnel: true);
            var friend = rig.SignIn("tunnelwatcher", throughTunnel: true);
            var local = rig.SignIn("localwatcher");
            var id = Convert.ToInt32(rig.Create(owner, "Watched through the tunnel", "", "ALL")["id"]);
            rig.Call(player, "joinGame", (double)id, null);
            rig.Call(friend, "observeGame", (double)id, null);
            rig.Call(local, "observeGame", (double)id, null);
            await rig.LaunchAsync(id, owner, player);
            rig.Games.StartPlayingForTest(id);

            // The recorder's stream: a start-up chunk of about 400 KB that does not compress (many frames on every hop), then a game chunk 30 s on.
            var folder = rig.Manager.SpectatorFolder(rig.Manager.List(PlatformGames.Guest(owner)).Single().Id);
            var start = rig.Clock.Now;
            var recording = new SpectatorRecording(folder, SpectatorChunk.NewChunkKey(), rig.Clock);
            recording.AppendStartup([.. Enumerable.Range(0, 4).Select(_ => new SpectatorPacket(0, 1, [0x57, 0, 0, 0, 0, .. RandomNumberGenerator.GetBytes(99_999)]))]);
            rig.Clock.Now = start.AddSeconds(30);
            recording.AppendGame([SpectatorGridChecks.Packet()], TimeSpan.FromSeconds(30));
            var startup = File.ReadAllBytes(SpectatorRecording.ChunkPath(folder, 1));
            TestAssert.True(startup.Length > 300_000, "an incompressible start-up chunk: " + startup.Length);

            // The host: its pinned HTTPS port with the spectator route, as both lines' Program.cs maps it.
            var access = new AirRemoteAccess(Path.Combine(root, "remote"));
            var remotePort = AirRemoteChecks.FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort, listen => listen.UseHttps(certificate)));
            await using var app = builder.Build();
            AirRemoteTunnel.MapSpectate(app, access, rig.Grid!);
            await app.StartAsync(stop.Token);
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            TestAssert.True(AirRemoteTunnel.IsPublicPath(AirRemoteTunnel.SpectatePath + SpectatorGridChecks.Meta(id)), "the route is on the friends' port");

            // The friend who spectates: its player tool opens its spectator port with the launch, and its client's requests come back.
            await using var friendTool = await AirRemoteProxy.StartWithPortsAsync(new AirRemoteInvitation("127.0.0.1", remotePort, access.Issue(friend.Id).Token, pin),
                AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), stop.Token);
            var friendPort = AirRemoteChecks.FreePort();
            games.Add(friendTool.Launch(SpectatorLaunch(id), _ => StartSyntheticClient(), friendPort));
            for (var i = 0; i < 3; i++)
            {
                var (head, body) = await SpectatorGridChecks.GetAsync(friendPort, SpectatorGridChecks.Chunk(id, 1));
                TestAssert.True(head.StartsWith("HTTP/1.0 200 OK\r\n", StringComparison.Ordinal) && head.Contains("\r\nContent-Type: application/octet-stream\r\n", StringComparison.Ordinal), head);
                TestAssert.True(Same(startup, body), $"the start-up chunk byte for byte through the tunnel ({body.Length} of {startup.Length} bytes)");
            }
            using (var meta = JsonDocument.Parse(await SpectatorGridChecks.TextAsync(friendPort, SpectatorGridChecks.Meta(id))))
                TestAssert.Equal(id, meta.RootElement.GetProperty("gameKey").GetProperty("gameId").GetInt32(), "getGameMetaData through the tunnel");

            // The delay holds through the tunnel too: the game chunk closed at +30 s comes at +210 s, not a millisecond earlier.
            rig.Clock.Now = start.AddSeconds(210) - TimeSpan.FromMilliseconds(1);
            using (var info = JsonDocument.Parse(await SpectatorGridChecks.TextAsync(friendPort, SpectatorGridChecks.ChunkInfo(id))))
                TestAssert.Equal(1, info.RootElement.GetProperty("chunkId").GetInt32(), "held back the delay");
            TestAssert.True((await SpectatorGridChecks.GetAsync(friendPort, SpectatorGridChecks.Chunk(id, 2))).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "not early");
            rig.Clock.Now = start.AddSeconds(210);
            using (var info = JsonDocument.Parse(await SpectatorGridChecks.TextAsync(friendPort, SpectatorGridChecks.ChunkInfo(id))))
                TestAssert.Equal(2, info.RootElement.GetProperty("chunkId").GetInt32(), "published");
            TestAssert.Equal(Convert.ToHexString(File.ReadAllBytes(SpectatorRecording.ChunkPath(folder, 2))),
                Convert.ToHexString((await SpectatorGridChecks.GetAsync(friendPort, SpectatorGridChecks.Chunk(id, 2))).Body), "the delayed chunk through the tunnel");

            // The tool carries only a consumer path: anything else is answered on the friend's PC and never reaches the host.
            foreach (var path in new[] { "/manage", AirRemoteTunnel.SpectatePath, "/observer-mode/rest/consumer/../../../manage", "/observer-mode/rest/consumer/getGameMetaData/NA1/1%2F/token",
                         "/observer-mode/rest/consumer/getGameMetaData/NA1/1/token?x=1" })
                TestAssert.True((await SpectatorGridChecks.GetAsync(friendPort, path)).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), path);

            // A player of that game, signed in through the tunnel, is refused every request (403), and the refusals are no failed sign-ins:
            // more of them than the throttle's ten leave the address and the spectator served.
            await using var playerTool = await AirRemoteProxy.StartWithPortsAsync(new AirRemoteInvitation("127.0.0.1", remotePort, access.Issue(player.Id).Token, pin),
                AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), stop.Token);
            var playerPort = AirRemoteChecks.FreePort();
            games.Add(playerTool.Launch(SpectatorLaunch(id), _ => StartSyntheticClient(), playerPort));
            for (var i = 0; i < 12; i++)
                foreach (var path in new[] { SpectatorGridChecks.Meta(id), SpectatorGridChecks.ChunkInfo(id), SpectatorGridChecks.Chunk(id, 1) })
                {
                    var (head, body) = await SpectatorGridChecks.GetAsync(playerPort, path);
                    TestAssert.True(head.StartsWith("HTTP/1.0 403 Forbidden\r\n", StringComparison.Ordinal) && body.Length == 0, path + ": " + head);
                }
            TestAssert.False(access.Throttled(IPAddress.Loopback), "a refused spectator request is no failed sign-in");
            TestAssert.True((await SpectatorGridChecks.GetAsync(friendPort, SpectatorGridChecks.Meta(id))).Head.StartsWith("HTTP/1.0 200 ", StringComparison.Ordinal), "the spectator still");

            // The host's own spectator asks the grid on this PC directly, served while it is signed in here; with no spectator signed in on
            // this PC the grid refuses the PC, though the game's owner (a player) is signed in here.
            var gridPort = rig.Grid!.ListeningPort;
            TestAssert.True(Same(startup, (await SpectatorGridChecks.GetAsync(gridPort, SpectatorGridChecks.Chunk(id, 1))).Body), "the host's spectator on this PC");
            rig.Sessions.Logout(local.Username);
            TestAssert.True((await SpectatorGridChecks.GetAsync(gridPort, SpectatorGridChecks.Meta(id))).Head.StartsWith("HTTP/1.0 403 Forbidden\r\n", StringComparison.Ordinal),
                "no spectator signed in on this PC: a local process gets nothing");

            // Without a sign-in no stream either (the tunnels' own gate: 401, counted as a failed attempt).
            await using var strangerTool = await AirRemoteProxy.StartWithPortsAsync(new AirRemoteInvitation("127.0.0.1", remotePort, "", pin),
                AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), stop.Token);
            var strangerPort = AirRemoteChecks.FreePort();
            games.Add(strangerTool.Launch(SpectatorLaunch(id), _ => StartSyntheticClient(), strangerPort));
            TestAssert.True((await SpectatorGridChecks.GetAsync(strangerPort, SpectatorGridChecks.Meta(id))).Head.StartsWith("HTTP/1.0 401 ", StringComparison.Ordinal), "no bearer");
            await app.StopAsync();
        }
        finally
        {
            foreach (var game in games) Stop(game);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task PortFollowsTheLaunchAsync()
    {
        // The friend's tool alone: nothing here reaches a host.
        static Task<AirRemoteProxyHandle> ToolAsync() => AirRemoteProxy.StartWithPortsAsync(
            new AirRemoteInvitation("127.0.0.1", AirRemoteChecks.FreePort(), new string('a', 64), new string('b', 64)),
            AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort());
        var port = AirRemoteChecks.FreePort();
        var games = new List<Process>();
        try
        {
            await using (var tool = await ToolAsync())
            {
                // A player's launch opens nothing.
                var playing = tool.Launch(MaestroCreateRequest.Parse("127.0.0.1 5119 abcdefghijklmnop 1"), _ => StartSyntheticClient(), port);
                games.Add(playing);
                TestAssert.False(await AcceptingAsync(port), "a player's launch opens no spectator port");
                Stop(playing);

                // The spectator launch opens it; the tool answers a non-consumer path itself.
                var first = tool.Launch(SpectatorLaunch(7), _ => StartSyntheticClient(), port);
                games.Add(first);
                TestAssert.True(await AcceptingAsync(port), "open while the spectator client runs");
                TestAssert.True((await SpectatorGridChecks.GetAsync(port, "/manage")).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "answered on this PC");
                // The client ends: the port stays for AIR 1.52's end-of-game request, which comes right after.
                Stop(first);
                await Task.Delay(500);
                TestAssert.True(await AcceptingAsync(port), "still open after the spectator client has exited");
                TestAssert.True((await SpectatorGridChecks.GetAsync(port, "/manage")).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "and still answers");

                // AIR's reconnect relaunches at once, and a failed start, find it open; they keep it.
                var second = tool.Launch(SpectatorLaunch(7), _ => StartSyntheticClient(), port);
                games.Add(second);
                TestAssert.True(await AcceptingAsync(port), "a relaunch keeps it");
                TestAssert.Throws<IOException>(() => tool.Launch(SpectatorLaunch(7), _ => throw new IOException("no client"), port), "the start's failure");
                TestAssert.True(await AcceptingAsync(port), "a failed start leaves this tool's port open");
                Stop(second);
            }
            await ClosedAsync(port, "closed when the tool ends");

            // Another program on the port: the launch is refused with a plain message and starts no client.
            using (var held = new TcpListener(IPAddress.Loopback, port))
            {
                held.Start();
                await using var tool = await ToolAsync();
                var starts = 0;
                IOException? refusal = null;
                try { games.Add(tool.Launch(SpectatorLaunch(7), _ => { starts++; return StartSyntheticClient(); }, port)); }
                catch (IOException error) { refusal = error; }
                TestAssert.Equal(0, starts, "no client starts");
                TestAssert.True(refusal?.Message.StartsWith($"Spectating needs port {port} on this PC, and another program is using it", StringComparison.Ordinal) == true,
                    refusal?.Message ?? "no refusal");
                TestAssert.True(refusal!.Message.EndsWith("Close that program, then spectate again.", StringComparison.Ordinal), refusal.Message);

                // The classic start (batch 853 concern 2): the session's listen on a held port is one plain line and no failure.
                var told = new List<string>();
                TestAssert.False(tool.ListenForSpectators(told.Add, port), "a clash: the classic session goes on without spectating");
                TestAssert.True(told.Count == 1 && told[0].StartsWith($"Spectating needs port {port} on this PC", StringComparison.Ordinal) &&
                    told[0].EndsWith("start again to spectate.", StringComparison.Ordinal), string.Join(" | ", told));
                // A player's launch still starts its client.
                var player = tool.Launch(MaestroCreateRequest.Parse("127.0.0.1 5119 abcdefghijklmnop 1"), _ => { starts++; return StartSyntheticClient(); }, port);
                games.Add(player);
                TestAssert.Equal(1, starts, "the player path is untouched by the clash");
            }

            // The classic start on a free port: the port listens for the session (no launch of ours ever comes) and closes with the tool.
            await using (var tool = await ToolAsync())
            {
                var told = new List<string>();
                TestAssert.True(tool.ListenForSpectators(told.Add, port), "the classic session listens");
                TestAssert.Equal(0, told.Count);
                TestAssert.True(await AcceptingAsync(port), "open for the classic session");
                TestAssert.True((await SpectatorGridChecks.GetAsync(port, "/manage")).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "the same port's rules");
            }
            await ClosedAsync(port, "closed when the classic session's tool ends");
        }
        finally
        {
            foreach (var game in games) Stop(game);
        }
    }

    /// <summary>AIR's spectator launch (batch 849's five-field form) for <paramref name="gameId"/>; the key is any 32 base64 characters here.</summary>
    internal static MaestroCreateRequest SpectatorLaunch(long gameId) => MaestroCreateRequest.Parse($"spectator 127.0.0.1 {new string('A', 32)} {gameId} NA1");

    private static bool Same(byte[] expected, byte[] actual) => expected.AsSpan().SequenceEqual(actual);

    /// <summary>Stands in for the spectator client's process (AirMaestroLifecycleChecks' synthetic native process).</summary>
    internal static Process StartSyntheticClient()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -n 60 127.0.0.1 >nul");
        return Process.Start(start) ?? throw new InvalidOperationException("synthetic client did not start");
    }

    internal static void Stop(Process game)
    {
        try
        {
            if (!game.HasExited) game.Kill(entireProcessTree: true);
            game.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task<bool> AcceptingAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception error) when (error is SocketException or TimeoutException) { return false; }
    }

    private static async Task ClosedAsync(int port, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && await AcceptingAsync(port)) await Task.Delay(50);
        TestAssert.False(await AcceptingAsync(port), description);
    }
}
