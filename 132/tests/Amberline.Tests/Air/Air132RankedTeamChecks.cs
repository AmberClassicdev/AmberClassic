using System.Text.Json;
using Amberline.Host;
using Amberline.Platform;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 844: Ranked Teams on the 132 line (AIR 1.52's summonerTeamService, TeamServiceImpl.as; the RT module's rules, Team.as and
/// rankedTeams_en_US.properties). Every call goes through the RTMP router the stock client reaches (PlatformServices.Route), its answer
/// crosses the AMF writer, and every object it sends declares only fields its 1.52 class declares (air152-declared-fields.json).
/// </summary>
public static class Air132RankedTeamChecks
{


    [Check("server.air132-ranked-teams-persist")]
    public static void Persist() => PersistAsync().GetAwaiter().GetResult();



    /// <summary>Records a finished game of the handoff's roster as the match server reports it (each player two kills).</summary>
    private static bool Record(PlatformAccounts accounts, PlatformPendingMatch handoff, string winner)
    {
        var players = handoff.Players.Select(seat => Player(accounts.FindSummoner((long)seat.PlayerId!)!) with
        {
            Name = seat.Name, Team = seat.Team.ToString(), Champion = seat.Champion, SkinId = seat.SkinId, Kills = 2,
            SummonerSpells = [seat.SummonerSpell1, seat.SummonerSpell2],
        }).ToArray();
        var result = new Amberline.Network.Sessions.MatchResult(8, "Version 1.0.0.132", 1, "CLASSIC", winner, 1800, players, false);
        return accounts.RecordResult(new(handoff.GameId, handoff.HostGameId, handoff.Accounts,
            JsonSerializer.SerializeToElement(result, Amberline.Server.MatchRoster.JsonOptions), DateTimeOffset.UtcNow, handoff.QueueId));
    }

    /// <summary>Plays a queued Draft Mode game to its launch: the ban turns run out, each pick turn picks and locks in, the trade phase ends.</summary>
    private static void PlayDraft(TeamRig rig, PlatformAccount[] players)
    {
        var champions = new Queue<int>(AirGameDtos.ChampionNames.Keys.Order());
        var picked = new HashSet<long>();
        for (var step = 0; step < 100; step++)
        {
            var game = (AsObject)rig.Games.Current(players[0].Id)!;
            switch ((string)game["gameState"])
            {
                case "PRE_CHAMP_SELECT":
                    rig.Clock.Now = rig.Clock.Now.AddSeconds(PlatformGames.DraftBanServerSeconds);
                    rig.Games.Tick();
                    break;
                case "CHAMP_SELECT":
                    var moved = false;
                    foreach (var player in players.Where(player => !picked.Contains(player.Id)))
                        try
                        {
                            rig.Games.Pick(player.Id, champions.Peek());
                            rig.Games.LockIn(player.Id);
                            champions.Dequeue();
                            picked.Add(player.Id);
                            moved = true;
                        }
                        catch (ArgumentException) { }
                    TestAssert.True(moved, "each pick turn moves on");
                    break;
                case "POST_CHAMP_SELECT":
                    rig.Clock.Now = rig.Clock.Now.AddSeconds(PlatformGames.DraftPostPickServerSeconds);
                    rig.Games.Tick();
                    break;
                case "START_REQUESTED":
                    return;
                default:
                    throw new InvalidOperationException($"unexpected state {game["gameState"]}");
            }
        }
        throw new InvalidOperationException("the draft did not launch");
    }

    /// <summary>The first refusal's message of an attach, or "joined".</summary>
    private static string Message(AsObject notification) =>
        notification["playerJoinFailures"] is ArrayCollection { Count: > 0 } failures ? (string)((AsObject)failures[0])["message"] : "joined";


    /// <summary>
    /// Teams are in accounts.json: a store without teams writes no Teams key (an older build still reads it), a reopened store has the team
    /// and its roster, a team still provisional thirty days after its creation is disbanded (wiki 132.04, Reconstruction: the period), and a
    /// login the host deletes leaves its teams (NOT RETAIL, batch 748's delete): the first member to have joined becomes captain, a team left
    /// without members is disbanded.
    /// </summary>
    private static async Task PersistAsync()
    {
        var root = TemporaryDirectory("league-air132-teams-persist");
        try
        {
            string id;
            PlatformAccount a, b, c;
            var store = Path.Combine(root, "accounts", "accounts.json");
            await using (var rig = new TeamRig(root))
            {
                (a, b, c) = (rig.SignIn("teamkeepa"), rig.SignIn("teamkeepb"), rig.SignIn("teamkeepc"));
                TestAssert.False(File.ReadAllText(store).Contains("\"Teams\"", StringComparison.Ordinal), "no team, no Teams key");
                id = Id(rig.Call(a, "createTeam", "Keepers", "KEEP"));
                rig.Call(a, "invitePlayer", (double)b.SummonerId, TeamId(id));
                rig.Call(b, "joinTeam", TeamId(id));
                rig.Call(a, "invitePlayer", (double)c.SummonerId, TeamId(id));
            }
            TestAssert.True(File.ReadAllText(store).Contains("\"Teams\"", StringComparison.Ordinal));
            await using (var rig = new TeamRig(root))
            {
                var team = rig.Accounts.FindTeam(id)!;
                TestAssert.Equal("Keepers", team.Name);
                TestAssert.Equal("MEMBER,MEMBER,INVITED", string.Join(",", team.Roster.Select(row => row.Status)), "the roster survives a restart");
                TestAssert.Equal(a.Id, team.Owner);

                // The captain's login is deleted: B, the first to have joined after A, captains; then B too, and C's open invite does not keep
                // the team alive.
                TestAssert.Equal(DeleteOutcome.Deleted, rig.Accounts.Delete(a.Id));
                TestAssert.Equal(b.Id, rig.Accounts.FindTeam(id)!.Owner);
                TestAssert.Equal(DeleteOutcome.Deleted, rig.Accounts.Delete(b.Id));
                TestAssert.True(rig.Accounts.FindTeam(id) is null, "a team without members is disbanded");

                // A provisional team is disbanded thirty days after its creation.
                var d = rig.SignIn("teamkeepd");
                var squat = Id(rig.Call(d, "createTeam", "Name Squatters", "SQUAT"));
                rig.Clock.Now = rig.Clock.Now.AddDays(29);
                TestAssert.True(rig.Call(d, "findTeamById", TeamId(squat)) is not null, "29 days: still there");
                rig.Clock.Now = rig.Clock.Now.AddDays(1);
                TestAssert.True(rig.Call(d, "findTeamById", TeamId(squat)) is null, "30 days provisional: disbanded");
                TestAssert.Equal(0, ((ArrayCollection)((AsObject)rig.Call(d, "createPlayer")!)["playerTeams"]).Count);
                TestAssert.Equal(false, rig.Call(d, "doesTagExist", "SQUAT"));
            }

            // A store an earlier build wrote (no Teams key) loads; the team service answers from it.
            var earlier = Path.Combine(root, "earlier");
            Directory.CreateDirectory(Path.Combine(earlier, "accounts"));
            await using (var rig = new TeamRig(earlier)) rig.SignIn("teamearlier");
            TestAssert.False(File.ReadAllText(Path.Combine(earlier, "accounts", "accounts.json")).Contains("\"Teams\"", StringComparison.Ordinal));
            await using (var rig = new TeamRig(earlier))
            {
                var player = rig.Accounts.FindName("teamearlier")!;
                TestAssert.Equal(0, rig.Accounts.TeamsOf(player.Id).Length);
            }
        }
        finally { DeleteDirectory(root); }
    }

    internal static AsObject TeamId(string id) => AirWire.Object(AirTeamDtos.TeamIdAlias, ("fullId", id));

    internal static string Id(object? teamOrInfo) => (string)((AsObject)((AsObject)teamOrInfo!)["teamId"])["fullId"];

    private static AsObject Member(object? team, PlatformAccount who) => ((ArrayCollection)((AsObject)((AsObject)team!)["roster"])["memberList"])
        .Cast<AsObject>().Single(row => Equals(row["playerId"], (double)who.SummonerId));

    private static string Row(object? team, PlatformAccount who) => ((ArrayCollection)((AsObject)((AsObject)team!)["roster"])["memberList"])
        .Cast<AsObject>().SingleOrDefault(row => Equals(row["playerId"], (double)who.SummonerId))?["status"] as string ?? "none";

    /// <summary>A platform whose accounts, games and sessions share one manual clock; every team answer is kept for the declared-fields audit.</summary>
    internal sealed class TeamRig : IAsyncDisposable
    {
        public readonly ManualClock Clock = new();
        public readonly PlatformAccounts Accounts;
        public readonly GameManager Manager;
        public readonly PlatformGames Games;
        public readonly PlatformSessions Sessions;
        public readonly PlatformServices Rpc;
        public readonly List<(long Account, AsObject Body)> Pushed = [];
        public readonly List<(string What, object? Value)> Sent = [];

        public TeamRig(string root)
        {
            Accounts = new PlatformAccounts(Path.Combine(root, "accounts"), timeProvider: Clock);
            Manager = NonLaunchingManager(root);
            Games = new PlatformGames(Accounts, Manager, "127.0.0.1", Clock);
            Games.Push = (account, body) => Pushed.Add((account, (AsObject)body));
            Sessions = new PlatformSessions(Accounts);
            Rpc = new PlatformServices(Accounts, Sessions, Games);
        }

        public PlatformAccount SignIn(string name, bool maxed = true)
        {
            var account = Accounts.FindName(name) ?? Accounts.RegisterPublic(name, "testing-password", name, maxed);
            var session = Sessions.Login(account.Username, account.Username, Sessions.IssueTicket(account), AirLine.ClientVersion);
            TestAssert.True(Sessions.AuthenticateChannel(account.Username, account.Username, session.Token));
            return account;
        }

        /// <summary>A summonerTeamService call as the stock client sends it; the answer crosses the AMF writer and is kept for the audit.</summary>
        public object? Call(PlatformAccount who, string operation, params object?[] args)
        {
            var value = Rpc.Route(who.Username, "summonerTeamService", operation, args);
            Sent.Add(($"summonerTeamService.{operation}", value));
            return value is null ? null : value is bool ? value : Air132LoginChecks.Decode(value);
        }

        /// <summary>The call's refusal code (the root cause's errorCode the RT module shows as RT_&lt;code&gt;).</summary>
        public string Refused(PlatformAccount who, string operation, params object?[] args)
        {
            try { Rpc.Route(who.Username, "summonerTeamService", operation, args); }
            catch (AirFault fault)
            {
                var cause = fault.RootCause();
                Sent.Add(($"summonerTeamService.{operation} (refused)", cause));
                TestAssert.Equal(AirFault.GameException, cause.TypeName, "a team refusal reaches the call's handler (TeamServiceImpl.getErrorMap)");
                return (string)cause["errorCode"];
            }
            throw new InvalidOperationException($"summonerTeamService.{operation} was expected to be refused");
        }

        /// <summary>A team: the first player creates it, invites the rest, and each accepts; returns its id.</summary>
        public string MakeTeam(PlatformAccount[] players, string name, string tag)
        {
            var id = Id(Call(players[0], "createTeam", name, tag));
            foreach (var member in players.Skip(1))
            {
                Call(players[0], "invitePlayer", (double)member.SummonerId, TeamId(id));
                Call(member, "joinTeam", TeamId(id));
            }
            return id;
        }

        /// <summary>
        /// The stock team party: each member accepts the invitation (acceptInviteForMatchmakingGame), then the leader's attachTeamToQueue sends
        /// the team's summoner ids, the invitation and the ranked team (MasterGameViewController.joinQueue_attach); a joined party's reply is sent.
        /// </summary>
        public AsObject AttachTeam(PlatformAccount leader, PlatformAccount[] members, int queue, string invitation, string? teamId)
        {
            foreach (var member in members) Rpc.Route(member.Username, "matchmakerService", "acceptInviteForMatchmakingGame", [invitation]);
            var answer = (AsObject)Rpc.Route(leader.Username, "matchmakerService", "attachTeamToQueue",
                [AirWire.Object("com.riotgames.platform.matchmaking.MatchMakerParams", ("queueIds", AirWire.List([(double)queue])),
                    ("team", AirWire.List(members.Prepend(leader).Select(player => (object)(double)player.SummonerId))),
                    ("invitationId", invitation), ("botDifficulty", null), ("teamId", teamId is null ? null : TeamId(teamId)))])!;
            Sent.Add(("matchmakerService.attachTeamToQueue", answer));
            if (answer["playerJoinFailures"] is null) Rpc.ResponseSent(leader.Username, "matchmakerService", "attachTeamToQueue");
            return answer;
        }


        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            Accounts.Dispose();
        }
    }
}
