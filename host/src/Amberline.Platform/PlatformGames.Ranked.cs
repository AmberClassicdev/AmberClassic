namespace Amberline.Platform;

/// <summary>
/// Batch 586: the ranked queues of 1.0.0.126 (owner decision, 1 November 2026). What the stock client states:
/// <list type="bullet">
/// <item>The play menu (ChooseGameFlowViewLayoutDefinition.as) has a Ranked draft leaf on Summoner's Rift 5v5 (soloid 4, partyid 6),
/// Twisted Treeline 3v3 (soloid 11, partyid 9) and The Crystal Scar 5v5 (18, 19). QueueType names them RANKED_SOLO_5x5,
/// RANKED_PREMADE_5x5, RANKED_PREMADE_3x3 and, for the retired queues 11 and 18/19, RANKED_SOLO_3x3 and ODIN_RANKED_*;
/// PlayerStatSummaryType.convert gives each its own summary, so each queue has its own rating. The profile's ranked view
/// (ProfileStatsSummaryPresentationModel, ProfileStatsRankingView) shows three of them, RANKED_PREMADE_3x3, RANKED_SOLO_5x5 and
/// RANKED_PREMADE_5x5, the three Season One launched with ("3v3 Premade, 5v5 Premade, or 5v5 Solo", wiki Season_One). Batch 586 ran
/// 4, 6 and 9. Batch 590 ran 11, 18 and 19 as NOT RETAIL; batch 805 (owner decision, 15 November 2026) removed them again: retail 126
/// never ran them (no ranked solo 3v3 row; Dominion launched in September 2011 without ranked), so their menu leaves stay disabled, as in
/// retail. Their saved games still count (<see cref="RetiredRankedQueues"/>).</item>
/// <item>There are no named ranked teams in 126: the client's TeamService is FakeTeamService (ClientModuleServices.as:88, a local stub
/// with only createNewTeam, answered after 200 ms with no remote call), the RANKED_TEAMS module mod_rt.dat does not ship and
/// RiotApplication.loadModuleRT defaults to false. The "team" queues are the arranged-team premade queues: the bundle calls their games
/// "Ranked Team Game" (practiceGame_gameMode_RANKED_GAME_PREMADE) and the buddy tooltip "Ranked 5x5 Team:" / "Ranked 3x3 Team:". Each
/// player has a premade rating; a party queues with its members' ratings (batch 579's party flow).</item>
/// <item>Party sizes: CreateNormalArrangedGame.as:1201 enables Start Game for a ranked party of minimumParticipantListSize players, or of
/// two when maximumParticipantListSize is 5, with the tooltips "You must have either 2 or {0} players" (5v5) and "You must have {0}
/// players" (3v3); MasterGameViewController.joinQueue_attach moves a party smaller than the premade minimum to the RANKED_SOLO_5x5 queue
/// (GameQueueManager.joinEquivalentPremadeSoloQueue). So 4 takes one or two (solo/duo), 6 exactly five, 9 exactly three.</item>
/// <item>Eligibility: the menu enables a ranked leaf from the queue's minLevel ("You must be at least level {0} before you can join ranked
/// games"); the server answers FailedJoinPlayer RANKED_MIN_LEVEL / RANKED_NUM_CHAMPS / RANKED_MAX_LEVEL, which RankedQueueFailuresDialog
/// shows per summoner: "Minimum level", "must have at least 14 champions available." (126's bundle; 132's says 16, see <see cref="AirLine.RankedMinChampions"/>).</item>
/// </list>
/// </summary>
public sealed partial class PlatformGames
{
    public const int RankedSoloQueueId = 4;
    public const int RankedTeamQueueId = 6;
    public const int RankedTreelineTeamQueueId = 9;
    // Retired by batch 805 (RetiredRankedQueues): nothing offers or matches them. The ids stay for the saved games that name them.
    public const int RankedTreelineSoloQueueId = 11;
    public const int DominionRankedSoloQueueId = 18;
    public const int DominionRankedTeamQueueId = 19;
    /// <summary>Season One: "summoners of Level 30 will be able to indulge in the ranked game system" (wiki Season_One); the queue's minLevel.</summary>
    public const int RankedMinLevel = 30;
    /// <summary>The line's champion requirement (<see cref="AirLine.RankedMinChampions"/>): 14 in 126, 16 in 132.</summary>
    public static int RankedMinChampions => AirLine.RankedMinChampions;

    // Every ranked queue picks in Draft Mode (the menu's Ranked leaves are data="true;draft_mode"). Reconstruction: the names; the game name
    // stems are the bundle's practiceGame_gameMode_RANKED_GAME_SOLO "Ranked Solo Game" and _PREMADE "Ranked Team Game".
    private static QueueRule[] RankedQueues =>
    [
        new(RankedSoloQueueId, "RANKED_SOLO_5x5", "RANKED_GAME_SOLO", PlatformStoreCalendar.NormalRiftMapId, 5, "Ranked Solo/Duo 5v5",
            "Ranked Solo Game", AirGameDtos.DraftConfigId, Ranked: true, MaxParty: 2),
        new(RankedTeamQueueId, "RANKED_PREMADE_5x5", "RANKED_GAME_PREMADE", PlatformStoreCalendar.NormalRiftMapId, 5, "Ranked Team 5v5",
            "Ranked Team Game", AirGameDtos.DraftConfigId, Ranked: true, MinParty: 5),
        new(RankedTreelineTeamQueueId, "RANKED_PREMADE_3x3", "RANKED_GAME_PREMADE", PlatformStoreCalendar.TwistedTreelineMapId, 3,
            "Ranked Team 3v3", "Ranked Team Game", AirGameDtos.DraftConfigId, Ranked: true, MinParty: 3),
    ];

    // NOT RETAIL (batch 590), retired by batch 805 (owner decision, 15 November 2026: remove the non-retail ranked queues 11, 18 and 19, the
    // closest solution to retail). Retail 126 never offered them, so nothing offers, accepts or matches these rules any more (Queues,
    // OfferedQueueOf). Saved games still name them, so the readers resolve them through QueueOf and SavedQueues (history, records, ratings,
    // recent games, the saved-handoff and result checks): an account with such games still loads and lists them. Batch 590's reasons:
    // joinEquivalentPremadeSoloQueue moves only a 5x5 classic party, so a TT or Dominion party never reached 11 or 18; Dominion is map 8 in
    // mode ODIN, with map 8's summoner spells and score block; 19's sizes and the names are Reconstruction.
    private static readonly QueueRule[] RetiredRankedQueues =
    [
        new(RankedTreelineSoloQueueId, "RANKED_SOLO_3x3", "RANKED_GAME_SOLO", PlatformStoreCalendar.TwistedTreelineMapId, 3,
            "Ranked Solo 3v3", "Ranked Solo Game", AirGameDtos.DraftConfigId, Ranked: true, MaxParty: 1),
        new(DominionRankedSoloQueueId, "ODIN_RANKED_SOLO", "RANKED_GAME_SOLO", PlatformStoreCalendar.CrystalScarMapId, 5,
            "Dominion Ranked Solo 5v5", "Ranked Solo Game", AirGameDtos.DraftConfigId, Ranked: true, MaxParty: 1),
        new(DominionRankedTeamQueueId, "ODIN_RANKED_PREMADE", "RANKED_GAME_PREMADE", PlatformStoreCalendar.CrystalScarMapId, 5,
            "Dominion Ranked Team 5v5", "Ranked Team Game", AirGameDtos.DraftConfigId, Ranked: true, MinParty: 5),
    ];

    /// <summary>
    /// The hook for LeaverBuster (batch 587 owns leaver tracking and the low-priority delay): whether a queue entry of this account, which
    /// joined at this time, may be matched now. Every queue's matcher asks it for every entry; a party waits for its slowest member.
    /// </summary>
    public Func<long, DateTimeOffset, bool> MayMatch { get; set; } = (_, _) => true;

    /// <summary>The failures RankedQueueFailuresDialog lists, one per summoner and reason. Reconstruction: "available" champions are the owned
    /// ones; whether retail counted the free rotation is not in the client (Champion.isAvailable counts it for picking).</summary>
    private (PlatformAccount Player, string Reason)[] RankedFailures(IEnumerable<PlatformAccount> players) =>
        players.SelectMany(player => new[]
        {
            player.Loadout.SummonerLevel < RankedMinLevel ? "RANKED_MIN_LEVEL" : null,
            OwnedChampions(player) < RankedMinChampions ? "RANKED_NUM_CHAMPS" : null,
        }.OfType<string>().Select(reason => (player, reason))).ToArray();

    private static int OwnedChampions(PlatformAccount player) => player.Economy is null || player.Economy.AllUnlocked
        ? AirGameDtos.ChampionNames.Count : player.Economy.OwnedChampions?.Distinct().Count(AirGameDtos.ChampionNames.ContainsKey) ?? 0;

    /// <summary>
    /// Reconstruction (retail's matchmaker is not in the shipped files, which only show an Elo rating per ranked queue): each unit in
    /// arrival order may anchor a game. Its candidates are the units whose rating (a party's mean) is within <see cref="RatingWindow"/> of
    /// the anchor's, closest first, and each joins the side it fits with the lower rating total, so the two teams' means stay close. The
    /// first anchor whose two sides fill starts the game. The window widens while the anchor waits, so a small group still plays.
    /// ponytail: every anchor is tried every tick, O(units²) on at most the queue's 100 entries.
    /// </summary>
    private List<QueueEntry>[]? RankedTeams(QueueRule rule, QueueEntry[][] units)
    {
        var rated = units.Select(unit => (Unit: unit, Rating: unit.Average(entry => (double)accounts.Rating(entry.AccountId, rule.Type)))).ToArray();
        var now = _clock.GetUtcNow();
        foreach (var anchor in rated)
        {
            var window = RatingWindow(now - anchor.Unit[0].Joined);
            var teams = new[] { new List<QueueEntry>(), new List<QueueEntry>() };
            var totals = new double[2];
            foreach (var (unit, rating) in rated.Where(unit => Math.Abs(unit.Rating - anchor.Rating) <= window)
                         .OrderBy(unit => unit.Unit == anchor.Unit ? -1 : Math.Abs(unit.Rating - anchor.Rating)))
            {
                var side = teams[0].Count + unit.Length > rule.TeamSize ? 1
                    : teams[1].Count + unit.Length > rule.TeamSize || totals[0] <= totals[1] ? 0 : 1;
                if (teams[side].Count + unit.Length > rule.TeamSize) continue;
                teams[side].AddRange(unit);
                totals[side] += rating * unit.Length;
            }
            if (teams.All(side => side.Count == rule.TeamSize)) return teams;
        }
        return null;
    }

    /// <summary>Reconstruction: 150 rating points, plus 10 for every second the anchor has waited.</summary>
    internal static double RatingWindow(TimeSpan waited) => 150 + 10 * Math.Max(0, waited.TotalSeconds);
}
