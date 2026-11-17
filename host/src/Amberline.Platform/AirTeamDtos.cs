using RtmpSharp.IO;
using static Amberline.Platform.AirWire;

namespace Amberline.Platform;

/// <summary>
/// Batch 844: the Ranked Teams wire classes of AIR 1.52 (the team aliases its main and rt modules register, named in AirProtocolNames; fields from the
/// batch 791 alias catalogue, <c>scratch/patch132/u-air132/aliases-132.csv</c>). They are sent only by the team service, which a line routes
/// only when its client has one (<see cref="AirLine.RankedTeamQueues"/>), and by the end-of-game screen of a team game.
/// </summary>
public static class AirTeamDtos
{
    public const string TeamIdAlias = AirProtocolNames.TeamId;
    public const string TeamInfoAlias = AirProtocolNames.TeamInfo;
    public const string PlayerAlias = AirProtocolNames.TeamPlayer;
    public const string TeamAlias = AirProtocolNames.TeamDTO;
    public const string RosterAlias = AirProtocolNames.RosterDTO;
    public const string MemberAlias = AirProtocolNames.TeamMemberInfoDTO;
    public const string StatSummaryAlias = AirProtocolNames.TeamStatSummary;
    public const string StatDetailAlias = AirProtocolNames.TeamStatDetail;
    public const string MatchHistoryAlias = AirProtocolNames.TeamMatchHistorySummary;
    /// <summary>Reconstruction: the games a TeamDTO lists in matchHistory (the RT history view lists what it gets).</summary>
    public const int MatchHistoryLength = 20;

    /// <summary>TeamId: the client compares <c>fullId</c> as a string (TeamId.Compare).</summary>
    public static AsObject TeamId(string id) => Object(TeamIdAlias, ("fullId", id));

    /// <summary>A Player.playerTeams row: the team and the player's roster status (TeamInfo.memberStatusString).</summary>
    public static AsObject TeamInfo(PlatformTeam team, string? memberStatus) =>
        Object(TeamInfoAlias, ("teamId", TeamId(team.Id)), ("name", team.Name), ("tag", team.Tag), ("memberStatusString", memberStatus));

    /// <summary>EndOfGameStats.myTeamInfo / otherTeamInfo: the team as named when it played (TeamInfo.fromTeam leaves memberStatusString null).</summary>
    public static AsObject TeamInfo(PlatformTeamSide side) =>
        Object(TeamInfoAlias, ("teamId", TeamId(side.TeamId)), ("name", side.Name), ("tag", side.Tag), ("memberStatusString", null));

    /// <summary>
    /// createPlayer (own) and findPlayer (another summoner). playerId is the summoner id: the RT module compares it with
    /// session.summoner.sumId and builds chat JIDs "sum" + playerId. The own Player lists every row, so the client's showInvites raises an
    /// invite or kick notice at login (RankedTeamManagementController); another summoner's lists only the teams they are a MEMBER of
    /// (Reconstruction: the profile shows only those, ProfilePlayerProxy.memberTeams). createdTeams: the listed teams the player created
    /// (Reconstruction: no 1.52 reader uses it beyond ProfilePlayerProxy.createdTeams).
    /// </summary>
    public static AsObject Player(PlatformAccounts accounts, PlatformAccount account, bool own)
    {
        var rows = accounts.TeamsOf(account.Id).Where(row => own || row.Status == PlatformAccounts.TeamMember).ToArray();
        return Object(PlayerAlias, ("playerId", (double)account.SummonerId),
            ("playerTeams", List(rows.Select(row => (object)TeamInfo(row.Team, row.Status)))),
            ("createdTeams", List(rows.Where(row => row.Team.Creator == account.Id).Select(row => (object)TeamInfo(row.Team, row.Status)))));
    }

    /// <summary>
    /// TeamDTO, as the RT module reads it: the roster (RosterDTO.ownerId and every row; playerId the summoner id, playerName the summoner name),
    /// status PROVISIONAL or RANKED (ProfileStatusInfoMediator and ManageTeamView test only PROVISIONAL), the dates the profile and Manage tabs
    /// print, lastJoinDate the last invite ("Last Invite Sent", Team.calculateNextInvite) and one TeamStatDetail per ranked team queue
    /// (ProfileTeamContent sums them; ProfileQueueSummaryView shows the rating once wins + losses reach 5). A row whose login the host deleted
    /// is left out (the delete takes it off the roster).
    /// </summary>
    public static AsObject Team(PlatformAccounts accounts, PlatformTeam team)
    {
        var games = accounts.TeamGames(team.Id);
        var members = team.Roster.Select(row => (Row: row, Account: accounts.Find(row.Account))).Where(member => member.Account is not null)
            .Select(member => (object)Object(MemberAlias, ("playerId", (double)member.Account!.SummonerId), ("playerName", member.Account.Summoner),
                ("status", member.Row.Status), ("inviteDate", member.Row.Invited.UtcDateTime), ("joinDate", member.Row.Joined?.UtcDateTime)));
        var owner = accounts.Find(team.Owner)?.SummonerId ?? PlatformAccounts.SummonerIdFor(team.Owner);
        var details = AirLine.RankedTeamQueues.Select(queue =>
        {
            var played = games.Where(game => game.QueueType == queue.Type).ToArray();
            var ratings = played.Select(game => game.Side.Rating!.After).ToArray();
            return (object)Object(StatDetailAlias, ("teamId", TeamId(team.Id)), ("teamStatType", queue.Type),
                ("wins", played.Count(game => game.Win)), ("losses", played.Count(game => !game.Win)),
                ("rating", ratings.Length == 0 ? PlatformRatings.StartingRating : ratings[0]),
                ("maxRating", ratings.Append(PlatformRatings.StartingRating).Max()));
        });
        return Object(TeamAlias, ("teamId", TeamId(team.Id)), ("name", team.Name), ("tag", team.Tag),
            ("status", accounts.IsProvisional(team) ? "PROVISIONAL" : "RANKED"),
            ("createDate", team.Created.UtcDateTime), ("modifyDate", team.Modified.UtcDateTime),
            ("lastJoinDate", team.LastInvite?.UtcDateTime), ("lastGameDate", games.FirstOrDefault()?.Receipt.CompletedAt.UtcDateTime),
            ("roster", Object(RosterAlias, ("ownerId", (double)owner), ("memberList", List(members)))),
            ("teamStatSummary", Object(StatSummaryAlias, ("teamId", TeamId(team.Id)), ("teamStatDetails", List(details)))),
            ("matchHistory", List(games.Take(MatchHistoryLength).Select(game => (object)Object(MatchHistoryAlias,
                ("gameId", (double)game.Receipt.GameId), ("date", (double)game.Receipt.CompletedAt.ToUnixTimeMilliseconds()),
                ("gameMode", game.Result.Mode), ("mapId", game.Result.MapId), ("invalid", false), ("win", game.Win),
                ("opposingTeamName", game.Opponent.Name), ("kills", game.Kills), ("deaths", game.Deaths), ("assists", game.Assists))))));
    }
}
