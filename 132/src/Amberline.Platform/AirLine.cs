namespace Amberline.Platform;

/// <summary>
/// The AIR client line this platform serves: patch 1.0.0.132's LolClient 1.52 (owner, 14 November 2026: master serves 1.52 only; the
/// 1.45 client stays on the 126 line). The 1.52 differences of the shared platform code are kept together: the values here, the wire
/// shapes in <c>AirDtos.Line.cs</c> (the externalized system states and broadcast, the login packet's 1.52 fields, the null default
/// spells, the inactive summoner spells) and <c>AirFault.Line.cs</c> (the root causes); batch 798 adds <c>AirGameDtos.Line.cs</c> (the
/// summoner pool, the GameDTO's bans).
/// </summary>
public static class AirLine
{
    /// <summary>
    /// The client's <c>Version.CURRENT_VERSION</c> (Version.as:6), which <c>loginService.login</c> sends as its client version
    /// (LoginController.as:347). The login admits only it; <c>/health</c>, the landing page and the companion's health probe name it.
    /// </summary>
    public const string ClientVersion = "1.52.12_01_17_13_40";

    /// <summary>
    /// Batch 798: Draft Mode's ban turns. Patch 1.0.0.129 made the draft six bans (was four; wiki V1.0.0.129, research
    /// <c>scratch/patch132/a-wiki</c> 129.57); the server sends it as the draft config's <c>maxAllowableBans</c>, which 1.52 prints as the ban
    /// progress "(pickTurn/maxAllowableBans)" (ChampionSelectionTipsController.findBanProgress, as:540-546) and whose six-ban branch keeps the
    /// "ban again" tip up to the fifth ban (as:398). The 1.52 bundle's tip texts still say "up to 4" and "a total of 2"
    /// (championSelection_newbietip_notBanningTip, potentialBannerTip): the client's text, not the rule.
    /// Reconstruction (the order): the client has none of its own. It takes the banning team from the ACTIVE participant
    /// (ChampionSelectionController.findBanTeam, as:1413-1432) and counts bans by pickTurn (getCurrentBanCount), so the server sets the
    /// order: one ban per turn, the captains alternating with team one first, 1-2-1-2-1-2 (<see cref="PlatformGames.PickMode"/>), each captain
    /// banning three times, which is what the tips' third-ban branch expects.
    /// </summary>
    public const int DraftMaxBans = 6;

    /// <summary>
    /// Batch 799: the rune page cap (<c>PlatformEconomyRules.MaximumRunePages</c>: the pages an account may own, the extra pages the
    /// store sells and the room a bundle needs). Patch 1.0.0.129 raised it from ten to twenty (wiki V1.0.0.129, change 129.56; research
    /// <c>scratch/patch132/a-wiki</c>). The 1.52 runebook counts the pages it is sent (SimpleRuneBookView.getNumPages) and holds no limit of
    /// its own, so the server's cap is the one that applies.
    /// </summary>
    public const int MaximumRunePages = 20;

    /// <summary>
    /// Batch 799: the champions a summoner must have available to join a ranked queue. 1.52's bundle says "must have at least 16
    /// champions available." (matchmaking_join_rankedqueue_reason_champs, the 1.52 bundle's resource script,
    /// shown for RANKED_NUM_CHAMPS by RankedQueueFailuresDialog); 16 is the six bans and ten picks of <see cref="DraftMaxBans"/>.
    /// </summary>
    public const int RankedMinChampions = 16;
}
