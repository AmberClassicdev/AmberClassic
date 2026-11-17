using System.Text.Json;
using System.Text.Json.Serialization;
using Amberline.Network.Sessions;

namespace Amberline.Platform;

/// <summary>
/// Batch 844: a named Ranked Team (patch 1.0.0.129 "Ranked Teams now available"; 1.0.0.132 roster up to 9 and provisional teams disbanded,
/// wiki V1.0.0.129 and V1.0.0.132). Only a line whose AIR client has the team service offers them (<see cref="AirLine.RankedTeamQueues"/>:
/// 1.0.0.132's AIR 1.52; 1.0.0.126's AIR 1.45 has none), so the 1.0.0.126 line never creates one. <c>Creator</c> and <c>Owner</c> (the
/// captain) are account ids; the roster holds every row the client lists (TeamMemberInfoDTO.status: MEMBER, INVITED, KICKED, DECLINED).
/// A disbanded team stays (invisible) so the "3 ranked teams per week" rule still counts it.
/// </summary>
public sealed record PlatformTeam(string Id, string Name, string Tag, long Creator, long Owner, PlatformTeamMember[] Roster,
    DateTimeOffset Created, DateTimeOffset Modified, DateTimeOffset? LastInvite = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Disbanded = null);

/// <summary>A roster row: the account, its status, when it was invited and when it joined (null until it did).</summary>
public sealed record PlatformTeamMember(long Account, string Status, DateTimeOffset Invited, DateTimeOffset? Joined = null);

/// <summary>One side of a ranked team game: the team as named when it played, and its team rating before and after (null until rated).</summary>
public sealed record PlatformTeamSide(string TeamId, string Name, string Tag, PlatformRating? Rating = null);

/// <summary>A finished game of a team, read from the receipts (<see cref="PlatformResult.Teams"/>).</summary>
public sealed record PlatformTeamGame(PlatformResult Receipt, MatchResult Result, string QueueType, bool Win, PlatformTeamSide Side,
    PlatformTeamSide Opponent, int Kills, int Deaths, int Assists);

public sealed partial class PlatformAccounts
{
    /// <summary>The roster statuses (TeamMemberStatus.as).</summary>
    public const string TeamMember = "MEMBER", TeamInvited = "INVITED", TeamKicked = "KICKED", TeamDeclined = "DECLINED";
    /// <summary>Team.MAX_NUMBER_OF_MEMBERS (1.52 Team.as): members and open invites together (calculateInvitesAvailable); wiki 132.03.</summary>
    public const int TeamRosterLimit = 9;
    /// <summary>Team.MIN_SUMMONER_LEVEL_FOR_RANKED_TEAMS; RT_TEAM_INVITE_LEVEL "This Summoner is under level 30".</summary>
    public const int TeamMinLevel = 30;
    /// <summary>RT_TEAM_INVITE_MAX_TEAMS "A summoner may only be a member of 5 teams"; RT_PLAYER_CANNOT_CREATE_TEAM "you can only have 5 teams total".</summary>
    public const int TeamsPerSummoner = 5;
    /// <summary>RT_PLAYER_CANNOT_CREATE_TEAM "You can only create 3 ranked teams per week".</summary>
    public const int TeamsCreatedPerWeek = 3;
    /// <summary>TeamStatDetail.GAMES_PLAYED_FOR_RANKED_STATUS: a team leaves PROVISIONAL after 5 games in any one queue (Status_seeding_bullet1).</summary>
    public const int RankedTeamGames = 5;
    /// <summary>Team.calculateInvitesAvailable/calculateNextInvite: a team that is not provisional sends one invite per week (Status_ranked_bullet1).</summary>
    public static readonly TimeSpan TeamInviteInterval = TimeSpan.FromDays(7);
    /// <summary>
    /// Reconstruction (wiki 132.04: "Provisional Ranked Teams will now be automatically disbanded after a certain period of time to prevent
    /// name squatting"; the period is not recovered): a team still provisional 30 days after it was created counts as disbanded.
    /// </summary>
    public static readonly TimeSpan ProvisionalTeamLifetime = TimeSpan.FromDays(30);
    /// <summary>Reconstruction (RT_TEAM_INVITE_MAX_PENDING names a limit, not its value): open invites one summoner may hold.</summary>
    public const int PendingTeamInvites = 10;

    /// <summary>A refusal the stock RT module shows as its <c>RT_&lt;errorCode&gt;</c> text: TeamServiceImpl.getErrorMap sends GameException and
    /// PlatformException to the call's handler, and RTShowServiceError reads ServerError.errorCode.</summary>
    private static AirFault TeamRefused(string code, string message) => new(AirFault.GameException, code, message);

    /// <summary>The live teams the account has a roster row on, with the row's status (Player.playerTeams).</summary>
    public (PlatformTeam Team, string Status)[] TeamsOf(long accountId)
    {
        lock (_gate)
            return [.. LiveTeams().SelectMany(team => team.Roster.Where(row => row.Account == accountId).Select(row => (team, row.Status)))];
    }

    /// <summary>Every live team (the ladder's team tables).</summary>
    public PlatformTeam[] ListTeams()
    {
        lock (_gate) return [.. LiveTeams()];
    }

    public PlatformTeam? FindTeam(string id)
    {
        lock (_gate) return LiveTeams().FirstOrDefault(team => team.Id == id);
    }

    public PlatformTeam? FindTeamByName(string name)
    {
        lock (_gate) return LiveTeams().FirstOrDefault(team => team.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public PlatformTeam? FindTeamByTag(string tag)
    {
        lock (_gate) return LiveTeams().FirstOrDefault(team => team.Tag.Equals(tag.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a team is provisional (TeamStatus.PROVISIONAL): it has not played <see cref="RankedTeamGames"/> games in any one queue.</summary>
    public bool IsProvisional(PlatformTeam team)
    {
        lock (_gate) return Provisional(team);
    }

    /// <summary>The team's finished games, newest first.</summary>
    public PlatformTeamGame[] TeamGames(string teamId)
    {
        lock (_gate) return GamesOf(teamId).Reverse().ToArray();
    }

    /// <summary>
    /// summonerTeamService.createTeam. Reconstruction (the client checks nothing itself): the name is 3-24 letters, digits and spaces and the
    /// tag 3-6 letters and digits (createTeamInstructions1-2, RT_INVALID_TEAM_*_CHARACTERS "may not contain special characters"), both unique
    /// among live teams ignoring case; no word filter ships, so none applies. The creator must be level 30, a member of fewer than five teams
    /// and have created fewer than three teams in the last seven days (RT_PLAYER_CANNOT_CREATE_TEAM). The team starts PROVISIONAL with its
    /// creator as captain.
    /// </summary>
    public PlatformTeam CreateTeam(long accountId, string name, string tag)
    {
        name = name.Trim();
        tag = tag.Trim();
        if (name.Length is < 3 or > 24) throw TeamRefused("INVALID_TEAM_NAME_LENGTH", "Team name must be 3-24 characters long.");
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == ' '))
            throw TeamRefused("INVALID_TEAM_NAME_CHARACTERS", "Team name may not contain special characters.");
        if (tag.Length is < 3 or > 6) throw TeamRefused("INVALID_TEAM_TAG_LENGTH", "Team tag must be 3-6 characters long.");
        if (!tag.All(char.IsAsciiLetterOrDigit)) throw TeamRefused("INVALID_TEAM_TAG_CHARACTERS", "Team tag may not contain special characters.");
        lock (_gate)
        {
            var account = RequireAccount(accountId);
            var now = _time.GetUtcNow();
            if (account.Loadout.SummonerLevel < TeamMinLevel || MemberTeams(accountId) >= TeamsPerSummoner ||
                (_state.Teams ?? []).Count(team => team.Creator == accountId && now - team.Created < TeamInviteInterval) >= TeamsCreatedPerWeek)
                throw TeamRefused("PLAYER_CANNOT_CREATE_TEAM", "You may not create any more teams at this time.");
            var live = LiveTeams().ToArray();
            if (live.Any(team => team.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw TeamRefused("EXISTING_TEAM_NAME", "Team name is already in use.");
            if (live.Any(team => team.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                throw TeamRefused("EXISTING_TEAM_TAG", "Team tag is already in use.");
            // Reconstruction: Riot's later public team ids had the form TEAM-<guid>; the client only compares fullId as a string.
            var created = new PlatformTeam("TEAM-" + Guid.NewGuid().ToString("D"), name, tag, accountId, accountId,
                [new(accountId, TeamMember, now, now)], now, now);
            Save(_state with { Teams = [.. _state.Teams ?? [], created] });
            return created;
        }
    }

    /// <summary>
    /// summonerTeamService.invitePlayer (the captain's Manage tab, InviteBuddyRenderer). The invitee must be level 30, neither a member nor
    /// invited, a member of fewer than five teams and holding fewer than <see cref="PendingTeamInvites"/> open invites. Members and open invites
    /// stay within nine. A team that is not provisional invites once per seven days from its last invite (lastJoinDate, which the Manage tab
    /// labels "Last Invite Sent", Team.calculateInvitesAvailable); a provisional one without that limit. A declined or kicked row is replaced.
    /// </summary>
    public PlatformTeam InviteToTeam(long captainId, string teamId, long inviteeSummonerId) => ChangeTeam(teamId, captainId, captainOnly: true,
        (team, now) =>
        {
            var invitee = _state.Accounts.FirstOrDefault(account => account.SummonerId == inviteeSummonerId)
                ?? throw TeamRefused("PLAYER_NOT_FOUND", "Player could not be found.");
            switch (team.Roster.FirstOrDefault(row => row.Account == invitee.Id)?.Status)
            {
                case TeamMember: throw TeamRefused("PLAYER_ALREADY_MEMBER", "Summoner is already a member of the team.");
                case TeamInvited: throw TeamRefused("PLAYER_ALREADY_INVITED", "Summoner is already invited to the team.");
            }
            if (invitee.Loadout.SummonerLevel < TeamMinLevel) throw TeamRefused("TEAM_INVITE_LEVEL", "This Summoner is under level 30.");
            if (MemberTeams(invitee.Id) >= TeamsPerSummoner)
                throw TeamRefused("TEAM_INVITE_MAX_TEAMS", "A summoner may only be a member of 5 teams.");
            if (LiveTeams().Count(other => other.Roster.Any(row => row.Account == invitee.Id && row.Status == TeamInvited)) >= PendingTeamInvites)
                throw TeamRefused("TEAM_INVITE_MAX_PENDING", "This Summoner has reached the maximum number of pending invites.");
            if (team.Roster.Count(row => row.Status is TeamMember or TeamInvited) >= TeamRosterLimit)
                throw TeamRefused("TEAM_INVITE_LIMIT", "Cannot invite. Invite limit reached.");
            if (!Provisional(team) && team.LastInvite is { } last && now - last < TeamInviteInterval)
                throw TeamRefused("TEAM_JOIN_DATE_LIMIT", "Cannot invite at this time.");
            return team with
            {
                Roster = [.. team.Roster.Where(row => row.Account != invitee.Id), new(invitee.Id, TeamInvited, now)],
                LastInvite = now,
            };
        });

    /// <summary>
    /// summonerTeamService.kickPlayer: the captain removes a member (KICKED: the player sees the kick at the next login, or at once from the
    /// client's RANKED_TEAM_UPDATE message, and acknowledges it with leaveTeam) or revokes an invite (the row goes; ModalTeamMediator's
    /// "Revoke" sends kickPlayer for an INVITED or DECLINED row). The captain cannot be kicked (RT_KICK_TEAM_OWNER).
    /// </summary>
    public PlatformTeam KickFromTeam(long captainId, string teamId, long targetSummonerId) => ChangeTeam(teamId, captainId, captainOnly: true,
        (team, _) =>
        {
            var target = _state.Accounts.FirstOrDefault(account => account.SummonerId == targetSummonerId)
                ?? throw TeamRefused("PLAYER_NOT_FOUND", "Player could not be found.");
            if (target.Id == team.Owner) throw TeamRefused("KICK_TEAM_OWNER", "Cannot remove Summoner from team.");
            var row = team.Roster.FirstOrDefault(row => row.Account == target.Id)
                ?? throw TeamRefused("NOT_TEAM_MEMBER", "This summoner is not on the team.");
            return team with
            {
                Roster = row.Status == TeamMember
                    ? [.. team.Roster.Select(other => other == row ? row with { Status = TeamKicked } : other)]
                    : [.. team.Roster.Where(other => other != row)],
            };
        });

    /// <summary>summonerTeamService.joinTeam: an invited summoner accepts (ProfileTeamMediator). Refused when the team holds nine members
    /// (RT_TEAM_FULL) or the summoner is already in five teams.</summary>
    public PlatformTeam JoinTeam(long accountId, string teamId) => ChangeTeam(teamId, accountId, captainOnly: false, (team, now) =>
    {
        var row = team.Roster.FirstOrDefault(row => row.Account == accountId);
        if (row?.Status != TeamInvited) throw TeamRefused("PLAYER_NOT_INVITED", "Team invite is no longer valid.");
        if (team.Roster.Count(other => other.Status == TeamMember) >= TeamRosterLimit) throw TeamRefused("TEAM_FULL", "Cannot join. Team is full.");
        if (MemberTeams(accountId) >= TeamsPerSummoner)
            throw TeamRefused("TEAM_INVITE_MAX_TEAMS", "A summoner may only be a member of 5 teams.");
        return team with { Roster = [.. team.Roster.Select(other => other == row ? row with { Status = TeamMember, Joined = now } : other)] };
    });

    /// <summary>summonerTeamService.declineInvite: the invite becomes DECLINED, which the captain's Manage tab lists as declined.</summary>
    public PlatformTeam DeclineTeam(long accountId, string teamId) => ChangeTeam(teamId, accountId, captainOnly: false, (team, _) =>
    {
        var row = team.Roster.FirstOrDefault(row => row.Account == accountId);
        if (row?.Status != TeamInvited) throw TeamRefused("PLAYER_NOT_INVITED", "Team invite is no longer valid.");
        return team with { Roster = [.. team.Roster.Select(other => other == row ? row with { Status = TeamDeclined } : other)] };
    });

    /// <summary>
    /// summonerTeamService.leaveTeam(teamId) and the captain's leaveTeam(newOwnerId, teamId) (TeamServiceImpl.ownerLeaveTeam). The stock client
    /// sends the one-argument form for three things: a member leaving (RTLeaveTeamCommand), the docked invite's Decline
    /// (RankedTeamManagementController.onInviteClick, so an INVITED row becomes DECLINED, as declineInvite does) and closing the docked kick
    /// notice (onKickClick, which clears the KICKED row). The captain leaves only by naming a member as the new captain
    /// (RT_LEAVE_TEAM_WITHOUT_SUCCESSOR); a captain alone disbands instead (the client sends disbandTeam).
    /// </summary>
    public PlatformTeam LeaveTeam(long accountId, string teamId, long? newOwnerSummonerId = null) =>
        ChangeTeam(teamId, accountId, captainOnly: false, (team, _) =>
        {
            var row = team.Roster.FirstOrDefault(row => row.Account == accountId)
                ?? throw TeamRefused("NOT_TEAM_MEMBER", "This action can only be performed by a team member.");
            var without = team.Roster.Where(other => other != row).ToArray();
            if (row.Status == TeamInvited)
                return team with { Roster = [.. team.Roster.Select(other => other == row ? row with { Status = TeamDeclined } : other)] };
            if (row.Status != TeamMember) return team with { Roster = without };
            if (newOwnerSummonerId is not { } successor)
                return team.Owner == accountId
                    ? throw TeamRefused("LEAVE_TEAM_WITHOUT_SUCCESSOR", "Cannot leave team.")
                    : team with { Roster = without };
            if (team.Owner != accountId) throw TeamRefused("NOT_TEAM_OWNER", "This action can only be performed by the team captain.");
            var next = without.FirstOrDefault(other => other.Status == TeamMember &&
                _state.Accounts.Any(account => account.Id == other.Account && account.SummonerId == successor))
                ?? throw TeamRefused("NOT_TEAM_MEMBER", "The new captain must be a member of the team.");
            return team with { Owner = next.Account, Roster = without };
        });

    /// <summary>summonerTeamService.changeOwner: the captain promotes a member (RTPromoteCaptainCommand).</summary>
    public PlatformTeam ChangeTeamOwner(long captainId, string teamId, long newOwnerSummonerId) => ChangeTeam(teamId, captainId, captainOnly: true,
        (team, _) =>
        {
            var target = _state.Accounts.FirstOrDefault(account => account.SummonerId == newOwnerSummonerId)
                ?? throw TeamRefused("PLAYER_NOT_FOUND", "Player could not be found.");
            if (target.Id == team.Owner) throw TeamRefused("PLAYER_ALREADY_OWNER", "Summoner is already the team captain.");
            if (team.Roster.FirstOrDefault(row => row.Account == target.Id)?.Status != TeamMember)
                throw TeamRefused("NOT_TEAM_MEMBER", "The new captain must be a member of the team.");
            return team with { Owner = target.Id };
        });

    /// <summary>summonerTeamService.disbandTeam: only once the captain is the last member and no invite is open (RT_TEAM_CAN_NOT_DISBAND).</summary>
    public PlatformTeam DisbandTeam(long captainId, string teamId) => ChangeTeam(teamId, captainId, captainOnly: true, (team, now) =>
        team.Roster.Any(row => row.Account != team.Owner && row.Status is TeamMember or TeamInvited)
            ? throw TeamRefused("TEAM_CAN_NOT_DISBAND", "You cannot disband this team until all members and all pending invitations are removed.")
            : team with { Disbanded = now });

    /// <summary>
    /// Batch 844, the ranked team queues (41, 42): the party must be <paramref name="size"/> members of one live team, the leader among them.
    /// Null when it is; otherwise the refusal's text.
    /// </summary>
    public string? TeamPartyRefusal(string teamId, IReadOnlyCollection<long> party, int size)
    {
        lock (_gate)
        {
            if (LiveTeams().FirstOrDefault(team => team.Id == teamId) is not { } found) return "The team could not be found.";
            if (party.Count != size) return "This team cannot join this queue.";
            return party.All(account => found.Roster.Any(row => row.Account == account && row.Status == TeamMember))
                ? null : "Every player must be a member of the team.";
        }
    }

    /// <summary>The team's rating in a ranked team queue type now (<see cref="PlatformRatings.TeamCurrent"/>).</summary>
    public int TeamRating(string teamId, string queueType)
    {
        lock (_gate) return PlatformRatings.TeamCurrent(teamId, _state.Results, queueType);
    }

    /// <summary>One change under the lock: the live team, the caller's right to change it, the change, its Modified time and the save.</summary>
    private PlatformTeam ChangeTeam(string teamId, long accountId, bool captainOnly, Func<PlatformTeam, DateTimeOffset, PlatformTeam> change)
    {
        lock (_gate)
        {
            var team = LiveTeams().FirstOrDefault(team => team.Id == teamId) ?? throw TeamRefused("TEAM_NOT_FOUND", "The team could not be found.");
            if (captainOnly && team.Owner != accountId)
                throw TeamRefused("NOT_TEAM_OWNER", "This action can only be performed by the team captain.");
            var now = _time.GetUtcNow();
            var changed = change(team, now) with { Modified = now };
            Save(_state with { Teams = [.. _state.Teams!.Select(other => other.Id == teamId ? changed : other)] });
            return changed;
        }
    }

    /// <summary>
    /// NOT RETAIL (batch 748's host-side delete, which retail never had): a deleted login leaves every team. A captain's team passes to the
    /// member who joined first; a team left without a member is disbanded. Called under the lock by <see cref="Delete"/>.
    /// </summary>
    private PlatformTeam[]? TeamsWithout(long accountId, DateTimeOffset now) => _state.Teams?.Select(team =>
    {
        if (team.Disbanded is not null || team.Roster.All(row => row.Account != accountId)) return team;
        var roster = team.Roster.Where(row => row.Account != accountId).ToArray();
        var heir = roster.Where(row => row.Status == TeamMember).OrderBy(row => row.Joined).FirstOrDefault();
        return team with
        {
            Roster = roster,
            Owner = team.Owner != accountId ? team.Owner : heir?.Account ?? team.Owner,
            Modified = now,
            Disbanded = heir is null ? now : null,
        };
    }).ToArray();

    // Under the lock.
    private IEnumerable<PlatformTeam> LiveTeams()
    {
        var now = _time.GetUtcNow();
        return (_state.Teams ?? []).Where(team => team.Disbanded is null && !(now - team.Created >= ProvisionalTeamLifetime && Provisional(team)));
    }

    private int MemberTeams(long accountId) => LiveTeams().Count(team => team.Roster.Any(row => row.Account == accountId && row.Status == TeamMember));

    private bool Provisional(PlatformTeam team) =>
        !GamesOf(team.Id).GroupBy(game => game.QueueType).Any(queue => queue.Count() >= RankedTeamGames);

    // Oldest first.
    private IEnumerable<PlatformTeamGame> GamesOf(string teamId)
    {
        foreach (var receipt in _state.Results)
        {
            if (receipt.Teams is not { } sides || PlatformGames.QueueOf(receipt.QueueId) is not { } queue) continue;
            var mine = sides.FirstOrDefault(side => side.Value.TeamId == teamId);
            if (mine.Value is null) continue;
            var other = sides.First(side => side.Key != mine.Key).Value;
            var result = receipt.Result.Deserialize<MatchResult>(Json) ?? throw new InvalidDataException($"Result receipt {receipt.GameId} is empty.");
            var players = result.Players.Where(player => player.Team == mine.Key).ToArray();
            yield return new(receipt, result, queue.Type, result.Winner == mine.Key, mine.Value, other,
                players.Sum(player => player.Kills), players.Sum(player => player.Deaths), players.Sum(player => player.Assists));
        }
    }
}
