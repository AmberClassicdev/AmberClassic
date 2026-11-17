using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amberline.AirCompanion;
using Amberline.Network.Spectator;
using Amberline.Platform;
using Amberline.Tests.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using RtmpSharp.IO;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 854 (spectating S5): a spectator's reconnect (both lines), 1.0.0.132's drop-in from the friends list, and the grid's end of game
/// for 1.52 (through the friend's player tool too). Everything through the RTMP router (PlatformServices.Route) and the AMF writer, the grid
/// on free loopback ports (never 8088), the rig's clock; no game client or server runs (a short-lived cmd stands in for the client's process).
/// What differs per line follows <see cref="AirLine.SpectatorDropIn"/>; the line checks (<c>server.air-spectate-s5-line-fields</c>) audit the
/// answers against each AIR's classes.
/// </summary>
public static class SpectatorS5Checks
{
    [Check("server.air-spectate-reconnect")]
    public static void ASpectatorReconnectsWhileTheDelayedStreamPlays() => ReconnectScenarioAsync().GetAwaiter().GetResult();

    [Check("server.air-spectate-drop-in")]
    public static void AFriendDropsIntoARunningCustomGame() => DropInAsync().GetAwaiter().GetResult();

    [Check("server.spectate-end-of-game-stats")]
    public static void TheGridServesTheSpectatorsEndOfGameOnceTheStreamEnds() => EndOfGameScenarioAsync().GetAwaiter().GetResult();

    /// <summary>
    /// retrieveInProgressGameInfo and the login packet's reconnectInfo for a spectator: nothing before the game plays; then its start again,
    /// with the GameDTO in progress and listing it (AIR opens the spectator's views from it), and no reconnectDelay (1.45's class has none);
    /// after the result too, until the delayed stream's end is published; never after declineObserverReconnect (and the grid then serves it no
    /// more), never for a game that failed. Returns every answer for the line audit.
    /// </summary>
    internal static async Task<List<(string What, object? Value)>> ReconnectScenarioAsync()
    {
        var root = TemporaryDirectory("league-spectate-reconnect");
        try
        {
            await using var rig = new AirSpectatorLobbyChecks.Rig(root);
            var owner = rig.SignIn("recowner");
            var player = rig.SignIn("recplayer");
            var stays = rig.SignIn("recstays");
            var leaves = rig.SignIn("recleaves");
            var id = Id(rig.Create(owner, "Reconnected", "", "ALL"));
            rig.Call(player, "joinGame", (double)id, null);
            rig.Call(stays, "observeGame", (double)id, null);
            rig.Call(leaves, "observeGame", (double)id, null);
            TestAssert.True(rig.Call(stays, "retrieveInProgressGameInfo") is null, "no reconnect in team selection");
            var start = await rig.LaunchAsync(id, owner, player);
            var chunkKey = start.Environment[SpectatorRecording.KeyVariable]!;
            TestAssert.True(rig.Call(stays, "retrieveInProgressGameInfo") is null, "nor before the game plays (no spectator has its start yet)");
            rig.Games.StartPlayingForTest(id);
            var begin = rig.Clock.Now;
            var recording = new SpectatorRecording(Folder(rig, owner), chunkKey, rig.Clock);
            recording.AppendStartup([SpectatorGridChecks.Packet()]);

            // The spectator's start again, at once (both AIRs pass delay 0 on this path).
            var info = (AsObject)rig.Call(stays, "retrieveInProgressGameInfo")!;
            TestAssert.Equal("com.riotgames.platform.game.GameReconnectInfoDTO", info.TypeName);
            TestAssert.False(info.ContainsKey("reconnectDelay"), "no reconnectDelay: AIR 1.45's GameReconnectionInfo has no such field");
            var game = (AsObject)info["game"];
            TestAssert.Equal((double)id, game["id"]);
            TestAssert.Equal("IN_PROGRESS", game["gameState"], "in progress for the spectator, the delay behind");
            TestAssert.True(AirSpectatorLobbyChecks.Observers(game).Any(o => (double)o["accountId"] == stays.Id),
                "it lists the spectator: AIR finds itself there (getTeamNameForSummoner) and opens the spectator's disconnected view");
            Start((AsObject)info["playerCredentials"], id, chunkKey);
            var reconnectInfo = (AsObject)AirSpectatorLobbyChecks.Rig.RoundTrip(LoginReconnectInfo(rig, stays)!);
            TestAssert.Equal((double)id, ((AsObject)reconnectInfo["game"])["id"], "the login packet's reconnectInfo (ApplicationController.handleLoginDataPacket)");
            rig.Sent.Add(("login reconnectInfo", reconnectInfo));
            TestAssert.True(rig.Call(player, "retrieveInProgressGameInfo") is null, "a player's own rule is unchanged (no credentials yet in this rig)");

            // Quit Spectating: never offered again, and the grid serves that account no more.
            rig.Call(leaves, "declineObserverReconnect");
            TestAssert.True(rig.Call(leaves, "retrieveInProgressGameInfo") is null, "a declined reconnect is not offered again");
            TestAssert.False(rig.Games.MaySpectate(id, leaves.Id, _ => false), "nor is its stream served to it");
            TestAssert.True(rig.Games.Current(stays.Id) is null, "a launched game's spectator stays free (S3)");

            // The result: the delayed stream plays on, and so does the reconnect, up to the publication of the stream's end.
            rig.Finish(owner);
            TestAssert.True(rig.Call(stays, "retrieveInProgressGameInfo") is AsObject, "after the result, while the delayed stream plays");
            rig.Clock.Now = begin.AddSeconds(30);
            recording.AppendGame([SpectatorGridChecks.Packet()], TimeSpan.FromSeconds(30));
            recording.End();
            rig.Clock.Now = begin.AddSeconds(30) + SpectatorGrid.Delay - TimeSpan.FromMilliseconds(1);
            TestAssert.True(rig.Call(stays, "retrieveInProgressGameInfo") is AsObject, "until the stream's end is published");
            rig.Clock.Now += TimeSpan.FromMilliseconds(1);
            TestAssert.True(rig.Call(stays, "retrieveInProgressGameInfo") is null, "then the game is over for the spectator too");
            TestAssert.True(LoginReconnectInfo(rig, stays) is null, "and the login packet offers none");

            // A game whose server failed offers none.
            var owner2 = rig.SignIn("recowner2");
            var player2 = rig.SignIn("recplayer2");
            var watcher2 = rig.SignIn("recwatcher2");
            var failed = Id(rig.Create(owner2, "Failed", "", "ALL"));
            rig.Call(player2, "joinGame", (double)failed, null);
            rig.Call(watcher2, "observeGame", (double)failed, null);
            await rig.LaunchAsync(failed, owner2, player2);
            rig.Games.StartPlayingForTest(failed);
            TestAssert.True(rig.Call(watcher2, "retrieveInProgressGameInfo") is AsObject, "offered while it runs");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (rig.Manager.List(PlatformGames.Guest(owner2)).Single(game => game.Id == rig.HostOf(owner2)).State != "Failed") await Task.Delay(50, deadline.Token);
            rig.Games.Tick();
            TestAssert.Equal("FAILED_TO_START", ((AsObject)rig.Call(owner2, "getGame", (double)failed)!)["gameState"]);
            TestAssert.True(rig.Call(watcher2, "retrieveInProgressGameInfo") is null, "a failed game offers no reconnect");
            return rig.Sent;
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>
    /// 1.0.0.132: retrieveInProgressSpectatorGameInfo drops a friend into a running custom game that allows spectators: the GameDTO with it
    /// among the observers, its start (whose key the client unwraps to the game's chunk key) and reconnectDelay, what is left of the delay;
    /// the same answer again without a second slot; refused (PG-0036, which AIR shows as SPECTATING_DISABLED) before the game plays, for an
    /// unknown name, a banned account, the fifth spectator, a game with a password, a game without spectators, and once the result is in; a
    /// player of the game is "already in a game". 1.0.0.126: not routed, and a game without a spectator records nothing.
    /// </summary>
    private static async Task DropInAsync()
    {
        var root = TemporaryDirectory("league-spectate-drop-in");
        try
        {
            await using var rig = new AirSpectatorLobbyChecks.Rig(root);
            var owner = rig.SignIn("dropowner");
            var player = rig.SignIn("dropplayer");
            var kicked = rig.SignIn("dropkicked");
            var droppers = Enumerable.Range(0, 5).Select(index => rig.SignIn("dropper" + index)).ToArray();
            var id = Id(rig.Create(owner, "Dropped into", "", "ALL"));
            rig.Call(kicked, "joinGame", (double)id, null);
            rig.Call(owner, "banUserFromGame", (double)id, (double)kicked.Id);
            rig.Call(player, "joinGame", (double)id, null);
            var start = await rig.LaunchAsync(id, owner, player);
            if (!AirLine.SpectatorDropIn)
            {
                TestAssert.Throws<ArgumentException>(() => rig.Rpc.Route(droppers[0].Username, "gameService", "retrieveInProgressSpectatorGameInfo", [owner.Summoner]),
                    "AIR 1.45 has no drop-in: the call is not routed");
                TestAssert.False(start.ArgumentList.Contains(SpectatorRecording.StreamSwitch), "a game without a spectator records nothing");
                return;
            }
            var chunkKey = start.Environment[SpectatorRecording.KeyVariable]!;
            TestAssert.Equal("PG-0036", rig.Refused(droppers[0], "retrieveInProgressSpectatorGameInfo", owner.Summoner), "not before the game plays");
            rig.Games.StartPlayingForTest(id);
            var playing = rig.Clock.Now;
            TestAssert.Equal("PG-0036", rig.Refused(droppers[0], "retrieveInProgressSpectatorGameInfo", "Nobody By That Name"));
            TestAssert.Equal("PG-0008", rig.Refused(player, "retrieveInProgressSpectatorGameInfo", owner.Summoner),
                "a player of the game is in a game: it never gets both teams' sight");
            TestAssert.Equal("PG-0036", rig.Refused(kicked, "retrieveInProgressSpectatorGameInfo", owner.Summoner), "banned from the lobby, banned from its stream");

            // A minute in: the client waits the two minutes left, so the first game chunk is out when it asks.
            rig.Clock.Now = playing.AddSeconds(60);
            var info = (AsObject)rig.Call(droppers[0], "retrieveInProgressSpectatorGameInfo", owner.Summoner)!;
            TestAssert.Equal("com.riotgames.platform.game.GameReconnectInfoDTO", info.TypeName);
            TestAssert.Equal(120, info["reconnectDelay"], "what is left of the delay, in seconds");
            var game = (AsObject)info["game"];
            TestAssert.Equal("IN_PROGRESS", game["gameState"]);
            TestAssert.True(AirSpectatorLobbyChecks.Observers(game).Any(o => (double)o["accountId"] == droppers[0].Id), "among the observers: AIR spectates");
            Start((AsObject)info["playerCredentials"], id, chunkKey);
            TestAssert.True(rig.Games.MaySpectate(id, droppers[0].Id, _ => false), "the grid serves the friend's tunnel now");
            TestAssert.True(rig.Games.Current(droppers[0].Id) is null, "and the friend stays free, as a lobby spectator after the launch");
            var again = (AsObject)rig.Call(droppers[0], "retrieveInProgressSpectatorGameInfo", player.Summoner)!;
            TestAssert.Equal(1, AirSpectatorLobbyChecks.Observers((AsObject)again["game"]).Count(o => (double)o["accountId"] == droppers[0].Id),
                "asking again (by the other player's name, the same game) takes no second slot");

            // Four slots.
            foreach (var dropper in droppers[1..4]) rig.Call(dropper, "retrieveInProgressSpectatorGameInfo", owner.Summoner);
            TestAssert.Equal("PG-0036", rig.Refused(droppers[4], "retrieveInProgressSpectatorGameInfo", owner.Summoner), "the fifth spectator");
            // Once the delay has passed, the client starts at once (and plays the stream from its start: no keyframes yet).
            rig.Clock.Now = playing + SpectatorGrid.Delay + TimeSpan.FromSeconds(1);
            TestAssert.Equal(0, ((AsObject)rig.Call(droppers[1], "retrieveInProgressSpectatorGameInfo", owner.Summoner)!)["reconnectDelay"]);

            // The result is in: no more drop-ins.
            rig.Finish(owner);
            TestAssert.Equal("PG-0036", rig.Refused(droppers[0], "retrieveInProgressSpectatorGameInfo", owner.Summoner), "a finished game, even for its spectator");

            // A game with a password (the friends list asks for none), and a game without spectators.
            var lockedOwner = rig.SignIn("lockedowner");
            var lockedPlayer = rig.SignIn("lockedplayer");
            var locked = Id(rig.Create(lockedOwner, "Locked", "secret", "ALL"));
            rig.Call(lockedPlayer, "joinGame", (double)locked, "secret");
            await rig.LaunchAsync(locked, lockedOwner, lockedPlayer);
            rig.Games.StartPlayingForTest(locked);
            TestAssert.Equal("PG-0036", rig.Refused(droppers[4], "retrieveInProgressSpectatorGameInfo", lockedOwner.Summoner), "a game with a password");
            await using var other = new AirSpectatorLobbyChecks.Rig(Path.Combine(root, "closed"));
            var closedOwner = other.SignIn("closedowner");
            var closedPlayer = other.SignIn("closedplayer");
            var closedDropper = other.SignIn("closeddropper");
            var closed = Id(other.Create(closedOwner, "Closed", "", "NONE"));
            other.Call(closedPlayer, "joinGame", (double)closed, null);
            TestAssert.False((await other.LaunchAsync(closed, closedOwner, closedPlayer)).ArgumentList.Contains(SpectatorRecording.StreamSwitch), "NONE records nothing");
            other.Games.StartPlayingForTest(closed);
            TestAssert.Equal("PG-0036", other.Refused(closedDropper, "retrieveInProgressSpectatorGameInfo", closedOwner.Summoner), "a game without spectators");
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>
    /// AIR 1.52 asks <c>endOfGameStats/NA1/&lt;gameId&gt;/&lt;handshakeToken&gt;</c> at its start's observerServerIp:observerServerPort after a
    /// drop-in spectator's client has ended. 1.0.0.132: the friend's tool keeps its port after the client's exit and carries the request;
    /// 404 until the stream's end is published (the delay holds the result back), then the base64 AMF3 SpectatorEndOfGameStats for that
    /// spectator: the observer classes, Order first, no reward; the host's own spectator on this PC gets its own; a player of the game is
    /// refused. 1.0.0.126: no end of game on the grid (404). Returns every answer for the line audit.
    /// </summary>
    internal static async Task<List<(string What, object? Value)>> EndOfGameScenarioAsync()
    {
        var root = TemporaryDirectory("league-spectate-end-of-game");
        using var certificate = AirRemoteChecks.CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var clients = new List<Process>();
        try
        {
            await using var rig = new AirSpectatorLobbyChecks.Rig(root);
            var owner = rig.SignIn("eogowner");
            var player = rig.SignIn("eogplayer", throughTunnel: true);
            var local = rig.SignIn("eoglocal");
            var friend = rig.SignIn("eogfriend", throughTunnel: true);
            var id = Id(rig.Create(owner, "Ended", "", "ALL"));
            rig.Call(player, "joinGame", (double)id, null);
            rig.Call(local, "observeGame", (double)id, null);
            var start = await rig.LaunchAsync(id, owner, player);
            rig.Games.StartPlayingForTest(id);
            var begin = rig.Clock.Now;
            var recording = new SpectatorRecording(Folder(rig, owner), start.Environment[SpectatorRecording.KeyVariable]!, rig.Clock);
            recording.AppendStartup([SpectatorGridChecks.Packet()]);
            var path = $"/observer-mode/rest/consumer/endOfGameStats/NA1/{id}/token";
            var gridPort = rig.Grid!.ListeningPort;

            // The host's pinned port and the friend's player tool, as in the tunnel check.
            var access = new AirRemoteAccess(Path.Combine(root, "remote"));
            var remotePort = AirRemoteChecks.FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort, listen => listen.UseHttps(certificate)));
            await using var app = builder.Build();
            AirRemoteTunnel.MapSpectate(app, access, rig.Grid);
            await app.StartAsync(stop.Token);
            await using var friendTool = await AirRemoteProxy.StartWithPortsAsync(new AirRemoteInvitation("127.0.0.1", remotePort, access.Issue(friend.Id).Token,
                certificate.GetCertHashString(HashAlgorithmName.SHA256)), AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), stop.Token);
            var friendPort = AirRemoteChecks.FreePort();

            if (AirLine.SpectatorDropIn)
            {
                var info = (AsObject)rig.Call(friend, "retrieveInProgressSpectatorGameInfo", owner.Summoner)!;
                var credentials = (AsObject)info["playerCredentials"];
                // The URL AIR builds from that start (SpectatorController.retrieveEndOfGameStats): nothing secret, and a path the tool carries.
                TestAssert.Equal(path, $"/observer-mode/rest/consumer/endOfGameStats/NA1/{(long)(double)credentials["gameId"]}/{credentials["handshakeToken"]}");
                TestAssert.Equal(SpectatorGrid.Port, credentials["observerServerPort"]);
                var client = friendTool.Launch(SpectatorTunnelChecks.SpectatorLaunch(id), _ => SpectatorTunnelChecks.StartSyntheticClient(), friendPort);
                clients.Add(client);
                SpectatorTunnelChecks.Stop(client);
                TestAssert.True((await SpectatorGridChecks.GetAsync(friendPort, path)).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal),
                    "the client has ended, its port is still there, and the result waits for the stream's end");
            }
            TestAssert.True((await SpectatorGridChecks.GetAsync(gridPort, path)).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "not before the end, on this PC");

            // The game ends; its stream's end is published the delay later.
            rig.Finish(owner);
            rig.Clock.Now = begin.AddSeconds(30);
            recording.AppendGame([SpectatorGridChecks.Packet()], TimeSpan.FromSeconds(30));
            recording.End();
            rig.Clock.Now = begin.AddSeconds(30) + SpectatorGrid.Delay - TimeSpan.FromMilliseconds(1);
            TestAssert.True((await SpectatorGridChecks.GetAsync(gridPort, path)).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "the delay holds the result back");
            rig.Clock.Now += TimeSpan.FromMilliseconds(1);
            var sent = new List<(string What, object? Value)>(rig.Sent);
            var (localHead, localBody) = await SpectatorGridChecks.GetAsync(gridPort, path);
            if (!AirLine.SpectatorDropIn)
            {
                TestAssert.True(localHead.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "AIR 1.45 has no spectator end of game: the grid serves none");
                return sent;
            }
            TestAssert.True(localHead.StartsWith("HTTP/1.0 200 OK\r\n", StringComparison.Ordinal) && localHead.Contains("\r\nContent-Type: text/plain\r\n", StringComparison.Ordinal), localHead);
            var mine = Stats(localBody, local, owner, player);
            sent.Add(("endOfGameStats (this PC)", mine));
            var (head, body) = await SpectatorGridChecks.GetAsync(friendPort, path);
            TestAssert.True(head.StartsWith("HTTP/1.0 200 OK\r\n", StringComparison.Ordinal), "through the friend's tool, after its client ended: " + head);
            sent.Add(("endOfGameStats (friend)", Stats(body, friend, owner, player)));
            TestAssert.Equal(403, rig.Grid.Answer(path, player.Id).Status, "a player of the game, through the tunnel, gets none");
            await app.StopAsync();
            return sent;
        }
        finally
        {
            foreach (var client in clients) SpectatorTunnelChecks.Stop(client);
            DeleteDirectory(root);
        }
    }

    /// <summary>The grid's answer as AIR 1.52 reads it (Base64Decoder, then ByteArray.readObject), checked for <paramref name="viewer"/>.</summary>
    private static AsObject Stats(byte[] body, PlatformAccount viewer, PlatformAccount order, PlatformAccount chaos)
    {
        using var reader = new AmfReader(new MemoryStream(Convert.FromBase64String(Encoding.ASCII.GetString(body))), new SerializationContext());
        var stats = (AsObject)reader.ReadAmf3Item();
        TestAssert.False(reader.DataAvailable, "one AMF3 value");
        TestAssert.Equal("com.riotgames.platform.observer.domain.EndOfGameStats", stats.TypeName, "readObject() as SpectatorEndOfGameStats");
        TestAssert.Equal((double)viewer.SummonerId, stats["userId"]);
        TestAssert.Equal(0d, stats["ipEarned"], "a spectator earns nothing");
        TestAssert.Equal(0d, stats["experienceEarned"]);
        var team = ((System.Collections.IEnumerable)stats["teamPlayerParticipantStats"]).Cast<AsObject>().ToArray();
        var other = ((System.Collections.IEnumerable)stats["otherTeamPlayerParticipantStats"]).Cast<AsObject>().ToArray();
        TestAssert.Equal(new[] { (double)order.SummonerId }, team.Select(row => (double)row["userId"]).ToArray(), "Order first: no row is the viewer's");
        TestAssert.Equal(new[] { (double)chaos.SummonerId }, other.Select(row => (double)row["userId"]).ToArray());
        TestAssert.Equal(100d, team[0]["teamId"], "the blue team first (EndOfGameStatsController keeps it there)");
        foreach (var row in team.Concat(other))
        {
            TestAssert.Equal("com.riotgames.platform.observer.domain.PlayerParticipantStatsSummary", row.TypeName);
            TestAssert.True(((System.Collections.IEnumerable)row["statistics"]).Cast<AsObject>()
                .All(stat => stat.TypeName == "com.riotgames.platform.observer.domain.RawStatDTO"), "the observer stat class");
        }
        TestAssert.True(((System.Collections.IEnumerable)team[0]["statistics"]).Cast<AsObject>().Any(stat => (string)stat["statTypeName"] == "WIN"), "Order won");
        return stats;
    }

    /// <summary>A spectator's start: observer, this game, the grid on loopback 8088, the literal token, and the line's key field, which the
    /// client unwraps to the game's chunk key.</summary>
    private static void Start(AsObject credentials, int id, string chunkKey)
    {
        TestAssert.Equal(true, credentials["observer"]);
        TestAssert.Equal((double)id, credentials["gameId"]);
        TestAssert.Equal("127.0.0.1", credentials["observerServerIp"]);
        TestAssert.Equal(SpectatorGrid.Port, credentials["observerServerPort"]);
        TestAssert.Equal("token", credentials["handshakeToken"], "the literal, never a secret: AIR 1.52 puts it in a URL");
        var keys = new[] { "encryptionKey", "observerEncryptionKey" }
            .Where(field => credentials.ContainsKey(field) && credentials[field] is string { Length: > 0 }).Select(field => (string)credentials[field]).ToArray();
        TestAssert.Equal(1, keys.Length, "one key field, the line's");
        TestAssert.Equal(Encoding.ASCII.GetBytes(chunkKey), SpectatorChunkChecks.Client.ChunkKey(keys[0], id), "the client unwraps the game's chunk key");
    }

    /// <summary>The login packet's reconnectInfo for <paramref name="who"/> (1.52's packet holds externalized JSON the rig's AMF round trip
    /// does not write, so only this field crosses it).</summary>
    private static object? LoginReconnectInfo(AirSpectatorLobbyChecks.Rig rig, PlatformAccount who) =>
        ((AsObject)rig.Rpc.Route(who.Username, "clientFacadeService", "getLoginDataPacketForUser", [])!)["reconnectInfo"];

    private static string Folder(AirSpectatorLobbyChecks.Rig rig, PlatformAccount owner) => rig.Manager.SpectatorFolder(rig.HostOf(owner));

    private static int Id(object game) => Convert.ToInt32(((AsObject)game)["id"]);
}
