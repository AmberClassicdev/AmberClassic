using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirPartyQueueChecks;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>Batch 586: the ranked queues 4, 6 and 9, their requirements, the Elo rating of each queue, the end-of-game line and the ladder. Batch 805: queues 11, 18 and 19 are retired (not offered); their saved games still load.</summary>
public static class AirRankedChecks
{
    [Check("server.air-ranked-queues-and-requirements")]
    public static void QueuesAndRequirements() => RequirementsAsync().GetAwaiter().GetResult();

    [Check("server.air-ranked-solo-duo-and-teams-match-by-rating")]
    public static void SoloDuoAndTeamsMatchByRating() => MatchingAsync().GetAwaiter().GetResult();

    [Check("server.air-ranked-results-rate-each-queue")]
    public static void ResultsRateEachQueue() => Ratings();

    [Check("server.air-ranked-ladder-lists-ratings")]
    public static void LadderListsRatings() => Ladder();

    [Check("server.air-non-retail-ranked-queues-not-offered")]
    public static void NonRetailRankedQueuesNotOffered() => NotOfferedAsync().GetAwaiter().GetResult();

    [Check("server.air-ranked-solo-3v3-and-dominion-queues")]
    public static void SoloThreeAndDominionQueues() => SavedGames();

    [Check("server.air-ranked-solo-3v3-and-dominion-ratings")]
    public static void SoloThreeAndDominionRatings() => MoreRatings();

    /// <summary>
    /// Batch 805 (owner, 15 November 2026: remove the non-retail ranked queues 11, 18 and 19, the closest solution to retail). Retail 126
    /// never offered them, so the stock menu's Twisted Treeline solo and Crystal Scar ranked leaves stay disabled: the host lists neither
    /// queue, enables neither and refuses every attach to them with "This queue is not enabled.". A friend's duo on Dominion ranked got
    /// the client's "queue disabled" box before any server call; queue 4 still takes that duo.
    /// </summary>
    private static async Task NotOfferedAsync()
    {
        var root = TemporaryDirectory("league-air-ranked-not-offered");
        try
        {
            await using var host = new Rig(root);
            var viewer = host.SignIn("notofferedview");
            // Batch 844: a line's Ranked Teams queues (AirLine.RankedTeamQueues: 42 and 41 on 1.0.0.132, none on 1.0.0.126) come on top; the
            // 132 check server.air132-ranked-teams-queue-team-vs-team pins them.
            var teamQueues = AirLine.RankedTeamQueues.Select(queue => (double)queue.Id).ToArray();
            var queues = ((ArrayCollection)host.Rpc.Route(viewer.Username, "matchmakerService", "getAvailableQueues", [])!)
                .Cast<AsObject>().ToDictionary(queue => (double)queue["id"]);
            TestAssert.Equal("2,4,6,7,8,9,14,16,17", string.Join(",", queues.Keys.Where(id => !teamQueues.Contains(id)).Order()),
                "the catalog has no queue 11, 18 or 19");
            TestAssert.Equal(teamQueues.Length, queues.Keys.Count(teamQueues.Contains), "the line's Ranked Teams queues are listed");
            TestAssert.Equal("4,6,9", string.Join(",", queues.Values.Where(queue => Equals(queue["ranked"], true))
                .Select(queue => (double)queue["id"]).Where(id => !teamQueues.Contains(id)).Order()), "the ranked queues are 4, 6 and 9 (their sizes: RequirementsAsync)");
            var login = (AsObject)host.Rpc.Route(viewer.Username, "clientFacadeService", "getLoginDataPacketForUser", [])!;
            var enabled = ((ArrayCollection)((AsObject)login["clientSystemStates"])["enabledQueueIdsList"]).Cast<object>().ToArray();
            TestAssert.Equal(new object[] { 2d, 7d, 8d, 14d, 16d, 17d, 4d, 6d, 9d }.Concat(teamQueues.Cast<object>()).ToArray(), enabled,
                "the enabled queues are the nine the menu offers, then the line's Ranked Teams queues");
            // Batch 844: the team service answers only on a line with Ranked Teams queues; 1.0.0.126's AIR 1.45 has none (FakeTeamService), and
            // there the call stays "not implemented".
            if (teamQueues.Length == 0)
                TestAssert.Throws<ArgumentException>(() => host.Rpc.Route(viewer.Username, "summonerTeamService", "createPlayer", []),
                    "no team service without Ranked Teams queues");
            else TestAssert.True(host.Rpc.Route(viewer.Username, "summonerTeamService", "createPlayer", []) is AsObject, "the team service answers");

            // Refusals: a solo player on 11 or 19, and a duo on 11, 18 or 19 (the client moves a duo only to queue 4), get the refusal.
            var mate = host.SignIn("notofferedmate");
            _ = host.Rpc.Route(mate.Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["800"]);
            TestAssert.Equal("This queue is not enabled.", Refusal(host.Attach(viewer, "attachToQueue", 11, null, null)), "a solo player, queue 11");
            TestAssert.Equal("This queue is not enabled.", Refusal(host.Attach(viewer, "attachToQueue", 19, null, null)), "a solo player, queue 19");
            foreach (var queue in new[] { 11, 18, 19 })
                TestAssert.Equal("This queue is not enabled.",
                    Refusal(host.Attach(viewer, "attachTeamToQueue", queue, [viewer.SummonerId, mate.SummonerId], "800")), $"a duo, queue {queue}");
            foreach (var queue in new double[] { 11, 18, 19 })
                TestAssert.Throws<ArgumentException>(() => host.Rpc.Route(viewer.Username, "matchmakerService", "getQueueInfo", [queue]),
                    $"getQueueInfo refuses queue {queue}");

            // The duo queues where the client sends it: queue 4 takes it.
            host.Party(viewer, [mate], 4, "801");
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>
    /// Batch 805: the saved games of the retired queues still load. A player's games of 11, 18 and 19 (recorded by batch 590) are read back
    /// from disk by a new PlatformAccounts and listed in recent games as ranked games of their own queue type: the readers resolve a queue
    /// the host no longer offers (<see cref="PlatformGames.QueueOf"/>).
    /// </summary>
    private static void SavedGames()
    {
        var root = TemporaryDirectory("league-air-ranked-saved-games");
        try
        {
            PlatformAccount a, b;
            using (var first = new PlatformAccounts(root))
            {
                (a, b) = (Register(first, "savedgamesa"), Register(first, "savedgamesb"));
                Ranked(first, PlatformGames.RankedTreelineSoloQueueId, 1, [a], [b]);
                Ranked(first, PlatformGames.DominionRankedSoloQueueId, 2, [a], [b]);
                Ranked(first, PlatformGames.DominionRankedTeamQueueId, 3, [b], [a]);
            }
            using var reopened = new PlatformAccounts(root);
            var receipts = reopened.ResultsFor(a.Id).ToArray();
            TestAssert.Equal(3, receipts.Length, "the three saved games load");
            var recent = ((ArrayCollection)AirGameDtos.RecentGames(reopened.Get(a.Id), receipts)["gameStatistics"]).Cast<AsObject>().ToArray();
            foreach (var type in new[] { "RANKED_SOLO_3x3", "ODIN_RANKED_SOLO", "ODIN_RANKED_PREMADE" })
                TestAssert.True(recent.Any(row => Equals(row["queueType"], type) && Equals(row["ranked"], true)), $"{type} is listed as a ranked game");
        }
        finally { DeleteDirectory(root); }
    }

    private static void MoreRatings()
    {
        var root = TemporaryDirectory("league-air-ranked-more-ratings");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var (a, b) = (Register(accounts, "moratea"), Register(accounts, "morateb"));
            // A ranked team 3v3 rating first; ranked solo 3v3 is a summary the client counts as ranked (RankedSolo3x3), so it starts at
            // getInitialRankedRating, and Dominion's (OdinRankedSolo, odinRankedPremade) are not ("Ranked" must lead the name): 1200.
            Ranked(accounts, PlatformGames.RankedTreelineTeamQueueId, 1, [a], [b]);
            TestAssert.Equal(1232, accounts.Rating(a.Id, "RANKED_PREMADE_3x3"));
            Ranked(accounts, PlatformGames.RankedTreelineSoloQueueId, 2, [a], [b]);
            Ranked(accounts, PlatformGames.DominionRankedSoloQueueId, 3, [b], [a]);
            Ranked(accounts, PlatformGames.DominionRankedTeamQueueId, 4, [a], [b]);
            var receipts = accounts.ResultsFor(a.Id);
            TestAssert.Equal(1211, receipts.Single(receipt => receipt.GameId == 2).Ratings![a.Id].Before, "round((1232 + 2400) / 3)");
            TestAssert.Equal(new PlatformRating(1200, 1168), receipts.Single(receipt => receipt.GameId == 3).Ratings![a.Id]);
            TestAssert.Equal(1200, receipts.Single(receipt => receipt.GameId == 4).Ratings![a.Id].Before);

            var summaries = ((ArrayCollection)AirProfileDtos.PlayerStatSummaries(accounts.Get(a.Id), receipts)["playerStatSummarySet"])
                .Cast<AsObject>().ToDictionary(row => (string)row["playerStatSummaryType"]);
            TestAssert.Equal(receipts.Single(receipt => receipt.GameId == 2).Ratings![a.Id].After, summaries["RankedSolo3x3"]["rating"]);
            TestAssert.Equal(1168, summaries["OdinRankedSolo"]["rating"]);
            TestAssert.Equal(1, summaries["OdinRankedSolo"]["losses"]);
            TestAssert.Equal(1232, summaries["odinRankedPremade"]["rating"], "the client's lower-case spelling");
            TestAssert.Equal(0, summaries["OdinUnranked"]["wins"] is int odinWins ? odinWins : -1, "Dominion ranked is not OdinUnranked");
            TestAssert.Equal((1, 0, 0), AirProfileDtos.Record(accounts.Get(a.Id), receipts, "ODIN_RANKED_PREMADE"),
                "the end-of-game counters print the Dominion ranked record");
            TestAssert.Equal((0, 1, 0), AirProfileDtos.Record(accounts.Get(a.Id), receipts, "ODIN_RANKED_SOLO"));

            // No profile row shows them, and since batch 805 the ladder does not list them either (it has the three offered ranked queues).
            var html = PlatformHome.RenderLadder(accounts);
            foreach (var title in new[] { "Ranked 3x3 Solo", "Dominion Ranked 5x5 Solo", "Dominion Ranked 5x5 Team" })
                TestAssert.False(html.Contains($"<h2>{title}</h2>", StringComparison.Ordinal), title);
            TestAssert.True(html.Contains("<h2>Ranked 3x3 Team</h2>", StringComparison.Ordinal));
            TestAssert.True(html.Contains("<td>1</td><td>moratea</td><td>placement 1/10</td><td>1</td><td>0</td>", StringComparison.Ordinal));

            // A saved retired rating seeds a queue the player has not played: c's only ranked game is queue 11 (won, 1232), so queue 4
            // starts at round((1232 + 2400) / 3) = 1211. Seeded from the offered queues alone, it would start at 1200.
            var (c, d) = (Register(accounts, "moratec"), Register(accounts, "morated"));
            Ranked(accounts, PlatformGames.RankedTreelineSoloQueueId, 5, [c], [d]);
            TestAssert.Equal(1211, accounts.Rating(c.Id, "RANKED_SOLO_5x5"), "queue 4 starts from the saved queue 11 rating");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task RequirementsAsync()
    {
        var root = TemporaryDirectory("league-air-ranked-queues");
        try
        {
            await using var host = new Rig(root);
            var leader = host.SignIn("rankedlead");
            var queues = ((ArrayCollection)host.Rpc.Route(leader.Username, "matchmakerService", "getAvailableQueues", [])!)
                .Cast<AsObject>().ToDictionary(queue => (double)queue["id"]);
            // The menu's Ranked leaves: SR 5v5 solo 4 / party 6, TT 3v3 party 9 (ChooseGameFlowViewLayoutDefinition); the sizes the arranged
            // lobby's Start Game button reads (CreateNormalArrangedGame.as:1201) and the level the menu enables a ranked leaf from.
            foreach (var (id, type, map, size, min, max) in new[]
                     { (4d, "RANKED_SOLO_5x5", 1d, 5d, 1, 2), (6d, "RANKED_PREMADE_5x5", 1d, 5d, 5, 5), (9d, "RANKED_PREMADE_3x3", 4d, 3d, 3, 3) })
            {
                var queue = queues[id];
                TestAssert.Equal(true, queue["ranked"], $"queue {id} is ranked");
                TestAssert.Equal(type, queue["type"]);
                TestAssert.Equal(30, queue["minLevel"], "ranked games need level 30");
                TestAssert.Equal(min, queue["minimumParticipantListSize"], $"queue {id} minimum party");
                TestAssert.Equal(max, queue["maximumParticipantListSize"], $"queue {id} maximum party");
                TestAssert.Equal(size, queue["numPlayersPerTeam"]);
                TestAssert.Equal(AirGameDtos.DraftConfigId, queue["gameTypeConfigId"], "the Ranked leaves are draft_mode");
                TestAssert.Equal(new object[] { map }, ((ArrayCollection)queue["supportedMapIds"]).Cast<object>().ToArray());
            }
            TestAssert.Equal(false, queues[2d]["ranked"]);
            var login = (AsObject)host.Rpc.Route(leader.Username, "clientFacadeService", "getLoginDataPacketForUser", [])!;
            var enabled = ((ArrayCollection)((AsObject)login["clientSystemStates"])["enabledQueueIdsList"]).Cast<object>().ToArray();
            TestAssert.True(new object[] { 4d, 6d, 9d }.All(enabled.Contains), "the ranked queues are enabled");

            // A fresh account fails both requirements; RankedQueueFailuresDialog lists the reasons by summoner name.
            var fresh = host.SignIn("rankedfresh", maxed: false);
            var refused = (ArrayCollection)host.Attach(fresh, "attachToQueue", 4, null, null)["playerJoinFailures"];
            var reasons = refused.Cast<AsObject>().Select(row => (string)row["reasonFailed"]).ToArray();
            TestAssert.Equal(new[] { "RANKED_MIN_LEVEL", "RANKED_NUM_CHAMPS" }, reasons);
            TestAssert.True(refused.Cast<AsObject>().All(row => row.TypeName == "com.riotgames.platform.game.FailedJoinPlayer" &&
                Equals(((AsObject)row["summoner"])["name"], fresh.Summoner)));
            TestAssert.Equal(0, ((AsObject)host.Games.QueueInfo(4))["queueLength"]);

            // A duo with an unqualified member: only the member is named ("Because {0} did not meet the required game play criteria").
            _ = host.Rpc.Route(fresh.Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["600"]);
            var duo = (ArrayCollection)host.Attach(leader, "attachTeamToQueue", 4, [leader.SummonerId, fresh.SummonerId], "600")["playerJoinFailures"];
            TestAssert.Equal(2, duo.Count);
            TestAssert.True(duo.Cast<AsObject>().All(row => Equals(((AsObject)row["summoner"])["name"], fresh.Summoner)));

            // Party sizes: a solo player or a duo never enters the premade queue 6 (the client moves a duo to 4), three never queue 4.
            var others = Enumerable.Range(0, 4).Select(index => host.SignIn("rankedmate" + index)).ToArray();
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(leader, "attachToQueue", 6, null, null)));
            _ = host.Rpc.Route(others[0].Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["601"]);
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(leader, "attachTeamToQueue", 6, [leader.SummonerId, others[0].SummonerId], "601")));
            _ = host.Rpc.Route(others[1].Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["601"]);
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(leader, "attachTeamToQueue", 4,
                [leader.SummonerId, others[0].SummonerId, others[1].SummonerId], "601")));
            TestAssert.True(host.Attach(leader, "attachTeamToQueue", 9,
                [leader.SummonerId, others[0].SummonerId, others[1].SummonerId], "601")["playerJoinFailures"] is null, "three queue 9");
            TestAssert.Equal(3, ((AsObject)host.Games.QueueInfo(9))["queueLength"]);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task MatchingAsync()
    {
        var root = TemporaryDirectory("league-air-ranked-match");
        try
        {
            await using var host = new Rig(root);
            // A star who beat seven newcomers rates more than the window (150) above the 1200 a new solo/duo player starts at.
            var star = host.SignIn("rankedstar");
            for (var game = 1; game <= 7; game++) Ranked(host.Accounts, 4, game, [star], [host.SignIn("rankedvictim" + game)]);
            var starRating = host.Accounts.Rating(star.Id, "RANKED_SOLO_5x5");
            TestAssert.True(starRating > 1350, $"the star rates {starRating}");
            host.Queue(star, 4);

            // Ten newcomers, a duo among them: they match each other at once; the star keeps waiting.
            var fresh = Enumerable.Range(0, 10).Select(index => host.SignIn("rankednew" + index)).ToArray();
            host.Party(fresh[0], [fresh[1]], 4, "700");
            foreach (var player in fresh.Skip(2)) host.Queue(player, 4);
            host.Games.Tick();
            var game10 = (AsObject)host.Games.Current(fresh[0].Id)!;
            TestAssert.Equal("PRE_CHAMP_SELECT", game10["gameState"], "ranked picks in Draft Mode, bans first");
            TestAssert.Equal("RANKED_GAME_SOLO", game10["gameType"]);
            TestAssert.Equal("RANKED_SOLO_5x5", game10["queueTypeName"]);
            TestAssert.True(((string)game10["name"]).StartsWith("Ranked Solo Game ", StringComparison.Ordinal));
            var one = Accounts(game10, "teamOne");
            TestAssert.True(one.Contains(fresh[0].Id) == one.Contains(fresh[1].Id), "the duo plays on one team");
            TestAssert.True(host.Games.Current(star.Id) is null, "the star is outside the window");
            TestAssert.Equal(1, ((AsObject)host.Games.QueueInfo(4))["queueLength"]);

            // Nine more newcomers: not a game with the star until the window has widened past his rating.
            var late = Enumerable.Range(0, 9).Select(index => host.SignIn("rankedlate" + index)).ToArray();
            foreach (var player in late) host.Queue(player, 4);
            host.Games.Tick();
            TestAssert.True(host.Games.Current(star.Id) is null && host.Games.Current(late[0].Id) is null, "nine and a distant star wait");
            host.Clock.Now = host.Clock.Now.AddSeconds(Math.Ceiling((starRating - 1200 - 150) / 10.0) + 1);
            host.Games.Tick();
            var widened = (AsObject)host.Games.Current(star.Id)!;
            TestAssert.True(Equals(widened["id"], ((AsObject)host.Games.Current(late[0].Id)!)["id"]), "the widened window takes the star");

            // Ranked teams: two parties of five meet in queue 6, each party one team; two parties of three in queue 9 on map 4.
            var teams = Enumerable.Range(0, 10).Select(index => host.SignIn("rankedteam" + index)).ToArray();
            host.Party(teams[0], teams[1..5], 6, "710");
            host.Party(teams[5], teams[6..10], 6, "711");
            var three = Enumerable.Range(0, 6).Select(index => host.SignIn("rankedthree" + index)).ToArray();
            host.Party(three[0], three[1..3], 9, "720");
            host.Party(three[3], three[4..6], 9, "721");
            host.Games.Tick();
            var team5 = (AsObject)host.Games.Current(teams[0].Id)!;
            TestAssert.Equal("RANKED_GAME_PREMADE", team5["gameType"]);
            TestAssert.Equal("RANKED_PREMADE_5x5", team5["queueTypeName"]);
            TestAssert.Equal(teams[..5].Select(player => player.Id).ToArray(), Accounts(team5, "teamOne"));
            TestAssert.Equal(teams[5..].Select(player => player.Id).ToArray(), Accounts(team5, "teamTwo"));
            var team3 = (AsObject)host.Games.Current(three[0].Id)!;
            TestAssert.Equal(4, team3["mapId"]);
            TestAssert.Equal("RANKED_PREMADE_3x3", team3["queueTypeName"]);
            TestAssert.Equal(three[..3].Select(player => player.Id).ToArray(), Accounts(team3, "teamOne"));
        }
        finally { DeleteDirectory(root); }
    }

    private static void Ratings()
    {
        var root = TemporaryDirectory("league-air-ranked-ratings");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var (a, b, c, d) = (Register(accounts, "ratea"), Register(accounts, "rateb"), Register(accounts, "ratec"), Register(accounts, "rated"));

            // Solo/duo: everyone starts at the client's 1200; equal teams, a placement game (K 64): plus or minus 32.
            Ranked(accounts, 4, 1, [a, b], [c, d]);
            var first = accounts.ResultsFor(a.Id).Single();
            TestAssert.Equal(new PlatformRating(1200, 1232), first.Ratings![a.Id]);
            TestAssert.Equal(new PlatformRating(1200, 1168), first.Ratings[d.Id]);

            // The ranked team queue 6 starts each player at the client's getInitialRankedRating, round((best + 2400) / 3); a leaver on the
            // winning team scores a loss.
            Ranked(accounts, 6, 2, [a, b], [c, d], leaver: b);
            var premade = accounts.ResultsFor(a.Id).Single(receipt => receipt.QueueId == 6).Ratings!;
            TestAssert.Equal(1211, premade[a.Id].Before);
            TestAssert.Equal(1189, premade[c.Id].Before);
            var expected = 1 / (1 + Math.Pow(10, (1189 - 1211) / 400.0));
            TestAssert.Equal(1211 + (int)Math.Round(64 * (1 - expected)), premade[a.Id].After);
            TestAssert.Equal(1211 + (int)Math.Round(64 * (0 - expected)), premade[b.Id].After, "the leaver loses");
            TestAssert.Equal(1189 + (int)Math.Round(64 * (0 - (1 - expected))), premade[c.Id].After);

            // The profile: each ranked queue its own summary, rating and record; Unranked untouched.
            var summaries = ((ArrayCollection)AirProfileDtos.PlayerStatSummaries(accounts.Get(a.Id), accounts.ResultsFor(a.Id))
                ["playerStatSummarySet"]).Cast<AsObject>().ToDictionary(row => (string)row["playerStatSummaryType"]);
            TestAssert.Equal(1232, summaries["RankedSolo5x5"]["rating"]);
            TestAssert.Equal(1232, summaries["RankedSolo5x5"]["maxRating"]);
            TestAssert.Equal(1, summaries["RankedSolo5x5"]["wins"]);
            TestAssert.Equal(premade[a.Id].After, summaries["RankedPremade5x5"]["rating"]);
            TestAssert.Equal(0, summaries["RankedPremade3x3"]["rating"], "an unplayed queue stays unrated (the profile shows nothing)");
            TestAssert.Equal(0, summaries["Unranked"]["wins"], "ranked games are not Unranked");
            TestAssert.Equal((1, 0, 0), AirProfileDtos.Record(accounts.Get(b.Id), accounts.ResultsFor(b.Id), "RANKED_SOLO_5x5"));
            TestAssert.Equal((0, 0, 1), AirProfileDtos.Record(accounts.Get(b.Id), accounts.ResultsFor(b.Id), "RANKED_PREMADE_5x5"));

            // The end-of-game screen: ranked, the new rating and its change; recent games read "(ranked game)".
            var game = new PlatformGames.Lobby(1, "Ranked Solo Game 1", "", a.Id, 10)
            {
                QueueId = PlatformGames.RankedSoloQueueId,
                Result = first.Result.Deserialize<MatchResult>(MatchRoster.JsonOptions),
            };
            game.Members.Add(new PlatformGames.Member(accounts.Get(a.Id), Team.Order));
            var results = AirGameDtos.Results(game, game.Members[0], first.Rewards?.GetValueOrDefault(a.Id), accounts.EconomyRules,
                account => AirProfileDtos.Record(account, accounts.ResultsFor(account.Id), "RANKED_SOLO_5x5"), DateTimeOffset.UtcNow,
                first.Ratings[a.Id]);
            TestAssert.Equal(true, results["ranked"]);
            TestAssert.Equal("RANKED_SOLO_5x5", results["queueType"]);
            TestAssert.Equal(1232, results["elo"]);
            TestAssert.Equal(32, results["eloChange"]);
            var mine = (AsObject)((ArrayCollection)results["teamPlayerParticipantStats"]).Cast<AsObject>()
                .Single(row => Equals(row["userId"], (double)a.SummonerId));
            TestAssert.Equal(1d, mine["wins"], "the record counters are the ranked queue's");
            var recent = ((ArrayCollection)AirGameDtos.RecentGames(accounts.Get(a.Id), accounts.ResultsFor(a.Id))["gameStatistics"]).Cast<AsObject>();
            TestAssert.True(recent.All(row => Equals(row["ranked"], true)));

            // After the client's ten placement games the K falls to 32: plus 16 between equals.
            var placed = Enumerable.Range(0, 10).Select(index => new PlatformResult(100 + index, Guid.NewGuid(), [a.Id, c.Id],
                default, QueueId: PlatformGames.RankedTreelineTeamQueueId,
                Ratings: new() { [a.Id] = new(1300, 1300), [c.Id] = new(1300, 1300) })).ToArray();
            var even = new MatchResult(8, "Version 1.0.0.126", 4, "CLASSIC", "Order", 1800, [Player(a), Player(c) with { Team = "Chaos" }], false);
            var change = PlatformRatings.Rate(even, new Dictionary<ulong, long> { [(ulong)a.SummonerId] = a.Id, [(ulong)c.SummonerId] = c.Id },
                placed, "RANKED_PREMADE_3x3");
            TestAssert.Equal(new PlatformRating(1300, 1316), change[a.Id]);
        }
        finally { DeleteDirectory(root); }
    }

    private static void Ladder()
    {
        var root = TemporaryDirectory("league-air-ranked-ladder");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var (a, b, c, d) = (Register(accounts, "laddera"), Register(accounts, "ladderb"), Register(accounts, "ladderc"), Register(accounts, "ladderd"));
            for (var game = 1; game <= 10; game++) Ranked(accounts, 4, game, [a], [b]);
            Ranked(accounts, 6, 11, [c], [d]);
            var html = PlatformHome.RenderLadder(accounts);
            var rating = accounts.Rating(a.Id, "RANKED_SOLO_5x5");
            TestAssert.True(html.Contains("<h2>Ranked 5x5 Solo</h2>", StringComparison.Ordinal));
            TestAssert.True(html.Contains($"<td>1</td><td>laddera</td><td>{rating}</td><td>10</td><td>0</td>", StringComparison.Ordinal),
                "ten games show the rating");
            TestAssert.True(html.Contains("<td>1</td><td>ladderc</td><td>placement 1/10</td><td>1</td><td>0</td>", StringComparison.Ordinal),
                "a rating stays hidden until ten games, as the stock profile hides it");
            TestAssert.True(html.Contains("<h2>Ranked 3x3 Team</h2>", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(root); }
    }

    private static PlatformAccount Register(PlatformAccounts accounts, string name) => accounts.RegisterPublic(name, "testing-password", name);

    /// <summary>The refusal an attach came back with (its failure's message), or "joined" when the attach queued.</summary>
    private static string Refusal(AsObject notification) =>
        notification["playerJoinFailures"] is ArrayCollection { Count: > 0 } failures ? (string)((AsObject)failures[0])["message"] : "joined";

    /// <summary>Records a finished ranked game the way the platform's Tick does.</summary>
    private static void Ranked(PlatformAccounts accounts, int queue, int gameId, PlatformAccount[] order, PlatformAccount[] chaos,
        string winner = "Order", PlatformAccount? leaver = null)
    {
        var players = order.Select(account => Player(account)).Concat(chaos.Select(account => Player(account) with { Team = "Chaos" }))
            .Select(player => leaver is not null && player.PlayerId == (ulong)leaver.SummonerId ? player with { Leaver = true } : player).ToArray();
        var map = PlatformGames.QueueOf(queue)!.MapId;
        var match = new MatchResult(8, "Version 1.0.0.126", map, GameMaps.ModeOf(map), winner, 1800, players, false);
        TestAssert.True(accounts.RecordResult(new(gameId, Guid.NewGuid(), order.Concat(chaos).Select(account => account.Id).ToArray(),
            JsonSerializer.SerializeToElement(match, MatchRoster.JsonOptions), DateTimeOffset.UtcNow, queue)));
    }
}
