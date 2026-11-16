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
}
