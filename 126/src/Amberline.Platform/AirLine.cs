namespace Amberline.Platform;

/// <summary>
/// The AIR client line this platform serves: patch 1.0.0.126's LolClient 1.45. Batch 798: only the values the shared platform code
/// (<c>host/</c>) reads per line are here; 1.0.0.132's line keeps its own copy.
/// </summary>
public static class AirLine
{
    // The bundle's draft tips: the captains "ban up to 4 champions", each "a total of 2" (championSelection_newbietip_notBanningTip,
    // potentialBannerTip); the ban progress the client prints is "(pickTurn/maxAllowableBans)" (findBanProgress).
    public const int DraftMaxBans = 4;

    // Owner decision, 25 October 2026: 1.0.0.126 allows ten rune pages (twenty came only in 1.0.0.129). A stored value still loads
    // (2 to 20), and accounts above a lowered cap keep their pages.
    public const int MaximumRunePages = 10;

    /// <summary>The bundle's matchmaking_join_rankedqueue_reason_champs: 14 in 126 (the four 2011 draft bans and ten picks; 16 came with six bans).</summary>
    public const int RankedMinChampions = 14;

    /// <summary>
    /// Batch 844: no Ranked Teams queue on this line. AIR 1.45's TeamService is FakeTeamService (ClientModuleServices.as:88, a local stub with
    /// no remote call) and mod_rt.dat does not ship, so nothing offers a team queue and the platform routes no team service. 1.0.0.132's copy
    /// lists queues 42 and 41.
    /// </summary>
    public static PlatformGames.QueueRule[] RankedTeamQueues => [];

    /// <summary>
    /// Batch 854: no drop-in spectating on this line. AIR 1.45 never calls retrieveInProgressSpectatorGameInfo (SpectatorController.spectateSummoner
    /// exists, nothing calls it; design 845, B1) and has no spectator end of game, so a game records only with a spectator in its slots.
    /// 1.0.0.132's copy is true.
    /// </summary>
    public static bool SpectatorDropIn => false;
}
