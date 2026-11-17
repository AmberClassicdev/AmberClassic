using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Network.Spectator;
using Amberline.Platform;
using Amberline.Server;
using Amberline.Tests.Network;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 848 (spectating S3): the custom-game lobby's spectator slots and the play start, the same on both AIR lines (1.45 and 1.52 send the
/// same gameService calls, GameServiceImpl.as). Every call goes through the RTMP router the stock client reaches (PlatformServices.Route) and
/// its answer crosses the AMF writer; the line checks (<c>server.air-spectate-line-fields</c>) pin each line's own fields and, on 1.0.0.132,
/// audit everything this scenario sends against the 1.52 classes.
/// </summary>
public static class AirSpectatorLobbyChecks
{
    [Check("server.air-spectate-lobby-calls")]
    public static void LobbyCalls() => LobbyScenarioAsync().GetAwaiter().GetResult();

    [Check("server.air-spectate-play-start")]
    public static void PlayStart() => PlayStartAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The lobby calls, their answers (a GameDTO, a Boolean or nothing) and refusals (the client's PG codes), the four slots, the owner's ban,
    /// a spectator's leave, who counts as in a game, the chat room, the last player's leave; returns every answer and push for the 1.52 audit.
    /// </summary>
    internal static async Task<List<(string What, object? Value)>> LobbyScenarioAsync()
    {
        var root = TemporaryDirectory("league-air-spectate-lobby");
        try
        {
            await using var rig = new Rig(root);
            var owner = rig.SignIn("lobbyowner");
            var player = rig.SignIn("lobbyplayer");
            var closer = rig.SignIn("lobbycloser");
            var outsider = rig.SignIn("lobbyoutsider");
            var watchers = Enumerable.Range(0, 5).Select(index => rig.SignIn("lobbywatch" + index)).ToArray();

            // The create box's ALL allows spectators with the three-minute delay; creating binds no port.
            var created = rig.Create(owner, "Spectated", "pw", "ALL");
            var id = Id(created);
            TestAssert.Equal(true, created["spectatorsAllowed"]);
            TestAssert.Equal(180, created["spectatorDelay"], "AIR waits SpectatorGrid.Delay, in seconds");
            TestAssert.Equal(0, Observers(created).Length);
            TestAssert.Equal(0, rig.Grid!.ListeningPort, "creating binds no spectator port");
            TestAssert.Equal("ALL", rig.Listed(owner, id)["allowSpectators"], "the browser's Spectate button needs ALL (canSpectate)");
            TestAssert.Equal(0, rig.Listed(owner, id)["spectatorCount"]);

            // NONE: no spectators there.
            var closed = Id(rig.Create(closer, "Closed", "", "NONE"));
            TestAssert.Equal(false, ((AsObject)rig.Call(closer, "getGame", (double)closed)!)["spectatorsAllowed"]);
            TestAssert.Equal("NONE", rig.Listed(closer, closed)["allowSpectators"]);
            TestAssert.Equal("PG-0036", rig.Refused(watchers[0], "observeGame", (double)closed, null), "spectating is disabled for this game");
            rig.Call(outsider, "joinGame", (double)closed, null);
            TestAssert.Equal("PG-0036", rig.Refused(outsider, "switchPlayerToObserver", (double)closed));
            rig.Call(outsider, "quitGame");
            TestAssert.Equal("PG-0036", rig.Refused(owner, "createPracticeGame", "Odd", "CLASSIC", AirDtos.Maps()[0], 4, 1, null, "LOBBYONLY"),
                "the create box sends ALL or NONE only");

            // observeGame: the password (PG-0010), then the GameDTO with the spectator as a GameObserver; the slot binds the grid.
            rig.Call(player, "joinGame", (double)id, "pw");
            TestAssert.Equal("PG-0010", rig.Refused(watchers[0], "observeGame", (double)id, "nope"));
            var watched = (AsObject)rig.Call(watchers[0], "observeGame", (double)id, "pw")!;
            var observer = Observers(watched).Single();
            TestAssert.Equal("com.riotgames.platform.game.GameObserver", observer.TypeName);
            TestAssert.Equal((double)watchers[0].Id, observer["accountId"]);
            TestAssert.Equal((double)watchers[0].SummonerId, observer["summonerId"]);
            TestAssert.Equal(watchers[0].Summoner, observer["summonerName"], "the client finds itself by name (getTeamNameForSummoner)");
            TestAssert.Equal(AirGameDtos.InternalName(watchers[0]), observer["summonerInternalName"]);
            TestAssert.Equal(watchers[0].ProfileIconId, observer["profileIconId"]);
            TestAssert.True(Accounts(watched).All(account => account != watchers[0].Id), "a spectator is no player");
            TestAssert.True(rig.Grid.ListeningPort != 0, "the first spectator binds the grid");
            TestAssert.Equal(1, Observers((AsObject)rig.Call(watchers[0], "observeGame", (double)id, "pw")!).Length, "observing again changes nothing");
            TestAssert.True(rig.PushedTo(owner).Any(body => Observers(body).Length == 1) && rig.PushedTo(player).Any(body => Observers(body).Length == 1),
                "the players see the spectator");

            // Four slots: a fifth is refused (game full), a player's switch answers false, and the browser shows the count.
            foreach (var watcher in watchers[1..4]) rig.Call(watcher, "observeGame", (double)id, "pw");
            TestAssert.Equal("PG-0009", rig.Refused(watchers[4], "observeGame", (double)id, "pw"));
            TestAssert.Equal(4, rig.Listed(owner, id)["spectatorCount"]);
            TestAssert.Equal(false, rig.Call(player, "switchPlayerToObserver", (double)id), "the four slots are taken");
            TestAssert.True(rig.PushedTo(watchers[0]).Any(body => Observers(body).Length == 4), "the spectators follow the lobby");

            // A spectator is in a game: no other game, no queue, no party.
            TestAssert.Equal("PG-0012", rig.Refused(watchers[0], "createPracticeGame", "Elsewhere", "CLASSIC", AirDtos.Maps()[0], 4, 1, null, "ALL"));
            TestAssert.Equal("PG-0008", rig.Refused(watchers[0], "joinGame", (double)closed, null));
            TestAssert.Equal("PG-0008", rig.Refused(watchers[0], "observeGame", (double)closed, null));
            var queued = (AsObject)rig.Rpc.Route(watchers[0].Username, "matchmakerService", "attachToQueue",
                [AirWire.Object("com.riotgames.platform.matchmaking.MatchMakerParams", ("queueIds", AirWire.List([(double)PlatformGames.NormalQueueId])),
                    ("team", null), ("invitationId", null), ("botDifficulty", null))])!;
            rig.Sent.Add(("matchmakerService.attachToQueue", queued));
            TestAssert.Equal("ALREADY_IN_GAME", ((AsObject)((ArrayCollection)queued["playerJoinFailures"])[0])["reasonFailed"]);
            TestAssert.Throws<AirFault>(() => rig.Rpc.Route(watchers[0].Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["invite-1"]));

            // The team-select room only: spectators never ask for a champion-select room, and the team rooms are the players'.
            TestAssert.True(rig.Games.MayEnterRoom(watchers[0].Id, PlatformGames.ChatRoomName("ap", "Spectated")));
            TestAssert.False(rig.Games.MayEnterRoom(watchers[0].Id, PlatformGames.ChatRoomName("c1", "Spectated_c1")));

            // The owner bans a spectator (PLAYER_BANNED_FROM_GAME), and the ban holds; a player may not.
            TestAssert.Throws<ArgumentException>(() => rig.Rpc.Route(player.Username, "gameService", "banObserverFromGame", [(double)id, (double)watchers[3].Id]));
            TestAssert.True(rig.Call(owner, "banObserverFromGame", (double)id, (double)watchers[3].Id) is null);
            TestAssert.Equal("PLAYER_BANNED_FROM_GAME", rig.PushedTo(watchers[3]).Last()["type"]);
            TestAssert.Equal("PG-0021", rig.Refused(watchers[3], "observeGame", (double)id, "pw"));

            // Leaving: quitGame and declineObserverReconnect free the slot and the spectator.
            rig.Call(watchers[2], "quitGame");
            rig.Call(watchers[1], "declineObserverReconnect");
            TestAssert.True(rig.Games.Current(watchers[2].Id) is null && rig.Games.Current(watchers[1].Id) is null);
            TestAssert.Equal(1, Observers((AsObject)rig.Call(owner, "getGame", (double)id)!).Length);

            // Switches: a spectator takes a player slot (Boolean true), not on a full team (false); the owner who becomes a spectator hands
            // the lobby to a player; the last player stays one.
            TestAssert.Equal(true, rig.Call(watchers[0], "switchObserverToPlayer", (double)id, 200d));
            var switched = (AsObject)rig.Call(owner, "getGame", (double)id)!;
            TestAssert.True(Accounts(switched, "teamTwo").Contains(watchers[0].Id) && Observers(switched).Length == 0);
            rig.Call(watchers[1], "observeGame", (double)id, "pw");
            TestAssert.Equal(false, rig.Call(watchers[1], "switchObserverToPlayer", (double)id, 200d), "purple is full (two of four slots)");
            TestAssert.Equal(true, rig.Call(owner, "switchPlayerToObserver", (double)id));
            var handed = (AsObject)rig.Call(owner, "getGame", (double)id)!;
            TestAssert.True(Observers(handed).Any(o => (double)o["accountId"] == owner.Id), "the owner spectates");
            TestAssert.Equal((double)player.Id, ((AsObject)handed["ownerSummary"])["accountId"], "the first player owns the lobby");
            TestAssert.Equal(true, rig.Call(owner, "switchObserverToPlayer", (double)id, 100d));
            var lonely = Id(rig.Create(outsider, "Lonely", "", "ALL"));
            TestAssert.Equal(false, rig.Call(outsider, "switchPlayerToObserver", (double)lonely), "the last player stays");
            rig.Call(outsider, "quitGame");

            // Champion selection: no new spectator (PG-0020), no switch (PG-0018); the spectators follow it.
            rig.Games.Start(player.Id, id, (long)(double)((AsObject)rig.Call(player, "getGame", (double)id)!)["optimisticLock"]);
            TestAssert.Equal("PG-0020", rig.Refused(watchers[4], "observeGame", (double)id, "pw"));
            TestAssert.Equal("PG-0018", rig.Refused(player, "switchPlayerToObserver", (double)id));
            TestAssert.Equal("CHAMP_SELECT", rig.PushedTo(watchers[1]).Last()["gameState"]);

            // The last player leaves team selection: the spectators get the vacated lobby (LAST_PLAYER_LEFT), which still names its owner;
            // the leaver is not pushed.
            var vacated = Id(rig.Create(outsider, "Vacated", "", "ALL"));
            rig.Call(watchers[4], "observeGame", (double)vacated, null);
            var before = rig.PushedTo(outsider).Length;
            rig.Call(outsider, "quitGame");
            var closing = rig.PushedTo(watchers[4]).Last();
            TestAssert.Equal("TERMINATED", closing["gameState"]);
            TestAssert.Equal("LAST_PLAYER_LEFT", closing["terminatedCondition"], "practiceGame_gameLobbyVacated");
            TestAssert.True(closing["ownerSummary"] is AsObject, "MasterGameController reads ownerSummary.accountId on every team-select DTO");
            TestAssert.Equal(before, rig.PushedTo(outsider).Length);
            TestAssert.True(rig.Games.Current(watchers[4].Id) is null);

            // A Platform without a grid creates player-only rooms, as before batch 848.
            await using var plain = new Rig(Path.Combine(root, "plain"), grid: false);
            var host = plain.SignIn("plainowner");
            TestAssert.Equal(false, plain.Create(host, "Plain", "", "ALL")["spectatorsAllowed"]);

            // The spectator port is taken (the other line's Platform, another program): the slot is refused, PG-0036, and nobody spectates.
            var held = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            held.Start();
            try
            {
                await using var busy = new Rig(Path.Combine(root, "busy"), gridPort: ((IPEndPoint)held.LocalEndpoint).Port);
                var busyOwner = busy.SignIn("busyowner");
                var busyPlayer = busy.SignIn("busyplayer");
                var busyWatcher = busy.SignIn("busywatch");
                var taken = Id(busy.Create(busyOwner, "Busy", "", "ALL"));
                busy.Call(busyPlayer, "joinGame", (double)taken, null);
                TestAssert.Equal("PG-0036", busy.Refused(busyWatcher, "observeGame", (double)taken, null));
                TestAssert.Equal("PG-0036", busy.Refused(busyPlayer, "switchPlayerToObserver", (double)taken));
                var unchanged = (AsObject)busy.Call(busyOwner, "getGame", (double)taken)!;
                TestAssert.True(Observers(unchanged).Length == 0 && Accounts(unchanged).Length == 2, "no spectator, both players stay");
                TestAssert.True(busy.Games.Current(busyWatcher.Id) is null);
                rig.Sent.AddRange(busy.Sent);
            }
            finally { held.Stop(); }

            return [.. rig.Sent, .. rig.Pushed.Select(push => ("push", (object?)push.Body))];
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>
    /// The play start: a game with a spectator starts its server with the stream switch and the chunk key (and a roster of players only),
    /// registers the stream with the grid under AIR's game id, and once it plays sends the spectator its start, whose key the client unwraps
    /// to that chunk key. A game without a spectator records nothing.
    /// </summary>
    private static async Task PlayStartAsync()
    {
        var root = TemporaryDirectory("league-air-spectate-start");
        try
        {
            await using var rig = new Rig(root);
            var owner = rig.SignIn("startowner");
            var friend = rig.SignIn("startfriend");
            var watcher = rig.SignIn("startwatcher");
            var id = Id(rig.Create(owner, "Watched start", "", "ALL"));
            rig.Call(friend, "joinGame", (double)id, null);
            rig.Call(watcher, "observeGame", (double)id, null);
            var start = await rig.LaunchAsync(id, owner, friend);

            var server = rig.Manager.List(PlatformGames.Guest(owner)).Single().Id;
            var folder = rig.Manager.SpectatorFolder(server);
            TestAssert.Equal(Path.Combine(root, "games", server.ToString("N"), SpectatorRecording.FolderName), folder, "<game dir>/spectator");
            var arguments = start.ArgumentList.ToArray();
            var at = Array.IndexOf(arguments, SpectatorRecording.StreamSwitch);
            TestAssert.True(at > 0 && arguments[at + 1] == folder, "--spectator-stream <game dir>/spectator: " + string.Join(' ', arguments));
            var chunkKey = start.Environment[SpectatorRecording.KeyVariable]!;
            TestAssert.True(chunkKey.Length == 16 && chunkKey.All(char.IsAsciiLetterOrDigit), "a 16-character chunk key in AMBERLINE_SPECTATOR_KEY");
            TestAssert.False(arguments.Any(argument => argument.Contains(chunkKey, StringComparison.Ordinal)), "the key is not on the command line");
            var roster = MatchRoster.Read(Path.Combine(root, "games", server.ToString("N"), "roster.json"));
            TestAssert.Equal(new[] { owner.Summoner, friend.Summoner }, roster.Select(seat => seat.Name).ToArray(), "the roster holds the players only");

            // The grid serves the game under AIR's id (the id in the spectator's launch).
            using (var meta = JsonDocument.Parse(await SpectatorGridChecks.TextAsync(rig.Grid!.ListeningPort, SpectatorGridChecks.Meta(id))))
                TestAssert.Equal(id, meta.RootElement.GetProperty("gameKey").GetProperty("gameId").GetInt32());

            // Playing: the spectator, and only the spectator, gets a spectator start.
            rig.Games.StartPlayingForTest(id);
            var starts = rig.Pushed.Where(push => push.Body.TypeName.EndsWith("PlayerCredentialsDto", StringComparison.Ordinal)).ToArray();
            TestAssert.Equal(new[] { watcher.Id }, starts.Select(push => push.Account).ToArray());
            var credentials = (AsObject)Rig.RoundTrip(starts[0].Body);
            TestAssert.Equal(true, credentials["observer"]);
            TestAssert.Equal((double)id, credentials["gameId"]);
            TestAssert.Equal("127.0.0.1", credentials["observerServerIp"]);
            TestAssert.Equal(SpectatorGrid.Port, credentials["observerServerPort"], "the client's fixed 8088");
            TestAssert.Equal("", credentials["serverIp"], "no game server: a spectator downloads from the grid");
            var keys = new[] { "encryptionKey", "observerEncryptionKey" }
                .Where(field => credentials.ContainsKey(field) && credentials[field] is string { Length: > 0 }).Select(field => (string)credentials[field]).ToArray();
            TestAssert.Equal(1, keys.Length, "one key field, the line's");
            TestAssert.Equal(32, keys[0].Length);
            TestAssert.Equal(Encoding.ASCII.GetBytes(chunkKey), SpectatorChunkChecks.Client.ChunkKey(keys[0], id), "the client unwraps the chunk key");
            // From the launch on the platform holds a spectator in nothing: an AIR restarted mid-game (no reconnect before S5) may play.
            TestAssert.True(rig.Games.Current(watcher.Id) is null, "a launched game's spectator is free");
            TestAssert.Equal("TEAM_SELECT", rig.Create(watcher, "After the launch", "", "NONE")["gameState"]);

            // No spectator. 1.0.0.126: no switch, no key, nothing for the grid. Batch 854 (restated): on a line with the friends list's
            // drop-in (1.0.0.132, AirLine.SpectatorDropIn) a game that allows spectators records from the launch anyway, so a friend can
            // drop in later (design 845 C2, S3's note); still nobody gets a spectator start.
            var host = rig.SignIn("plainowner");
            var guest = rig.SignIn("plainfriend");
            var plain = Id(rig.Create(host, "Unwatched start", "", "ALL"));
            rig.Call(guest, "joinGame", (double)plain, null);
            var unwatched = await rig.LaunchAsync(plain, host, guest);
            TestAssert.Equal(AirLine.SpectatorDropIn, unwatched.ArgumentList.Contains(SpectatorRecording.StreamSwitch), "records without a spectator only with the drop-in");
            TestAssert.Equal(AirLine.SpectatorDropIn, unwatched.Environment.ContainsKey(SpectatorRecording.KeyVariable));
            TestAssert.Equal(AirLine.SpectatorDropIn ? "HTTP/1.0 403" : "HTTP/1.0 404",
                (await SpectatorGridChecks.GetAsync(rig.Grid.ListeningPort, SpectatorGridChecks.Meta(plain))).Head[..12], "registered with the grid only with the drop-in; nobody spectates it yet");
            var pushedBefore = rig.Pushed.Count(push => push.Body.TypeName.EndsWith("PlayerCredentialsDto", StringComparison.Ordinal));
            rig.Games.StartPlayingForTest(plain);
            TestAssert.Equal(pushedBefore, rig.Pushed.Count(push => push.Body.TypeName.EndsWith("PlayerCredentialsDto", StringComparison.Ordinal)));
        }
        finally { DeleteDirectory(root); }
    }

    private static int Id(object game) => Convert.ToInt32(((AsObject)game)["id"]);

    internal static AsObject[] Observers(AsObject game) => ((ArrayCollection)game["observers"]).Cast<AsObject>().ToArray();

    private static long[] Accounts(AsObject game, params string[] teams) => (teams.Length == 0 ? ["teamOne", "teamTwo"] : teams)
        .SelectMany(team => ((ArrayCollection)game[team]).Cast<AsObject>()).Select(participant => (long)(double)participant["accountId"]).ToArray();

    /// <summary>A Platform with a spectator grid on a free loopback port (never 8088) and a game manager that starts no server.</summary>
    internal sealed class Rig : IAsyncDisposable
    {
        public readonly ManualClock Clock = new();
        public readonly PlatformAccounts Accounts;
        public readonly GameManager Manager;
        public readonly SpectatorGrid? Grid;
        public readonly PlatformGames Games;
        public readonly PlatformSessions Sessions;
        public readonly PlatformServices Rpc;
        public readonly List<(long Account, AsObject Body)> Pushed = [];
        public readonly List<(string What, object? Value)> Sent = [];
        private readonly List<ProcessStartInfo> _starts = [];

        public Rig(string root, bool grid = true, int gridPort = 0)
        {
            Accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            Manager = NonLaunchingManager(root);
            Manager.Starting = start => { lock (_starts) _starts.Add(start); };
            Grid = grid ? new SpectatorGrid(new IPEndPoint(IPAddress.Loopback, gridPort), Clock, TextWriter.Null) : null;
            Games = new PlatformGames(Accounts, Manager, "127.0.0.1", Clock, spectatorGrid: Grid);
            Games.Push = (account, body) => Pushed.Add((account, (AsObject)body));
            Sessions = new PlatformSessions(Accounts);
            Rpc = new PlatformServices(Accounts, Sessions, Games);
            // Batch 853: the access rule as both lines' Program.cs sets it. Batch 854: and the end of game as 1.0.0.132's does (on 1.0.0.126
            // it answers nothing: AirLine.SpectatorDropIn).
            if (Grid is not null)
            {
                Grid.MayWatch = (gameId, account) => Games.MaySpectate(gameId, account, Sessions.IsLocalAccount);
                Grid.EndOfGameStats = (gameId, account) => Games.SpectatorEndOfGame(gameId, account, Sessions.IsLocalAccount);
            }
        }

        /// <summary>
        /// Batch 854: the game <paramref name="owner"/> launched ends, Order winning: its server's result file (match-result.json under the
        /// game's logs, as the server writes it at victory, one row per seat of its frozen roster), which the next
        /// <see cref="PlatformGames.Tick"/> reads.
        /// </summary>
        public void Finish(PlatformAccount owner)
        {
            var host = HostOf(owner);
            var game = Path.GetDirectoryName(Manager.SpectatorFolder(host))!;
            var players = MatchRoster.Read(Path.Combine(game, "roster.json")).Select(seat => AirQueueChecks.Player(Accounts.FindName(seat.Name)!) with
            {
                Name = seat.Name, Champion = seat.Champion, Team = seat.Team.ToString(), SkinId = seat.SkinId,
                SummonerSpells = [seat.SummonerSpell1, seat.SummonerSpell2],
            }).ToArray();
            var folder = Path.Combine(game, "logs", "session");
            Directory.CreateDirectory(folder);
            var result = new MatchResult(8, "Version", 1, "CLASSIC", "Order", 600, players, false);
            File.WriteAllText(Path.Combine(folder, "match-result.json"), JsonSerializer.Serialize(new { session_id = host.ToString(), result }));
            Games.Tick();
            TestAssert.True(Games.Current(owner.Id) is not null, "the result is taken (an invalid one aborts the game)");
        }

        /// <summary>Batch 854: the server game of the match <paramref name="player"/> plays in (the manager lists every game).</summary>
        public Guid HostOf(PlatformAccount player) => Manager.List(PlatformGames.Guest(player)).Select(game => game.Id).Single(id =>
            MatchRoster.Read(Path.Combine(Path.GetDirectoryName(Manager.SpectatorFolder(id))!, "roster.json")).Any(seat => seat.Name == player.Summoner));

        /// <summary>A login on this PC (its connection is the user name); batch 853: <paramref name="throughTunnel"/> marks a friend's.</summary>
        public PlatformAccount SignIn(string name, bool throughTunnel = false)
        {
            var account = Accounts.RegisterPublic(name, "testing-password", name);
            var session = Sessions.Login(account.Username, account.Username, Sessions.IssueTicket(account), AirQueueChecks.Version);
            TestAssert.True(Sessions.AuthenticateChannel(account.Username, account.Username, session.Token));
            Sessions.ObserveOrigin(account.Username, throughTunnel);
            return account;
        }

        /// <summary>A gameService call as the stock client sends it; the answer crosses the AMF writer and is kept for the audit.</summary>
        public object? Call(PlatformAccount who, string operation, params object?[] args)
        {
            var value = Rpc.Route(who.Username, "gameService", operation, args);
            Sent.Add(("gameService." + operation, value));
            return value is null or bool ? value : RoundTrip(value);
        }

        /// <summary>The call's refusal: the root cause's errorCode, the PG text the client shows.</summary>
        public string Refused(PlatformAccount who, string operation, params object?[] args)
        {
            try { Rpc.Route(who.Username, "gameService", operation, args); }
            catch (AirFault fault)
            {
                Sent.Add(($"gameService.{operation} (refused)", fault.RootCause()));
                return fault.ErrorCode;
            }
            throw new InvalidOperationException($"gameService.{operation} was expected to be refused");
        }

        /// <summary>createPracticeGame in the stock argument order: name, mode, map, slots, pick type, password, spectators.</summary>
        public AsObject Create(PlatformAccount who, string name, string password, string spectators) =>
            (AsObject)Call(who, "createPracticeGame", name, "CLASSIC", AirDtos.Maps()[0], 4, 1, password.Length == 0 ? null : password, spectators)!;

        public AsObject Listed(PlatformAccount who, int id) =>
            ((ArrayCollection)Call(who, "listAllPracticeGames")!).Cast<AsObject>().Single(game => Convert.ToInt32(game["id"]) == id);

        public AsObject[] PushedTo(PlatformAccount who) => Pushed.Where(push => push.Account == who.Id).Select(push => push.Body).ToArray();

        /// <summary>Blind pick to the launch; returns the server start the manager made (its placeholder never runs).</summary>
        public async Task<ProcessStartInfo> LaunchAsync(int id, PlatformAccount owner, PlatformAccount other)
        {
            Games.Start(owner.Id, id, (long)(double)((AsObject)Games.Get(owner.Id, id))["optimisticLock"]);
            var champions = AirGameDtos.ChampionNames.Keys.Order().Take(2).ToArray();
            Games.Pick(owner.Id, champions[0]);
            Games.Pick(other.Id, champions[1]);
            Games.LockIn(owner.Id);
            Games.LockIn(other.Id);
            int count;
            lock (_starts) count = _starts.Count;
            Clock.Now = Clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            Games.Tick();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                lock (_starts) if (_starts.Count > count) return _starts[count];
                await Task.Delay(20, deadline.Token);
            }
        }

        public static object RoundTrip(object value)
        {
            using var buffer = new MemoryStream();
            using (var writer = new AmfWriter(buffer, new SerializationContext())) writer.WriteAmf3Item(value);
            using var reader = new AmfReader(new MemoryStream(buffer.ToArray()), new SerializationContext());
            return reader.ReadAmf3Item();
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            if (Grid is not null) await Grid.DisposeAsync();
            Accounts.Dispose();
        }
    }
}
