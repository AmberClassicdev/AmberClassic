using Amberline.Core;
using static Amberline.Platform.AirWire;

namespace Amberline.Platform;

/// <summary>
/// The AIR line's champion-select tables for LolClient 1.52 (patch 1.0.0.132, <see cref="AirLine"/>; batch 798): the summoner pool and the
/// GameDTO's bans. Evidence: the JPEXS export of the 1.52 modules (batch 791, <c>scratch/patch132/u-air132</c>); file:line references are
/// into it.
/// </summary>
public static partial class AirGameDtos
{
    /// <summary>
    /// The 1.52 summoner catalogue, row by row as the client's generated <c>SpellGeneratedData.fillSpellData</c> lists it
    /// (AirGeneratedContent.swf, SpellGeneratedData.as:32-47): id, script name, active, gameModes; the minimum levels are
    /// <see cref="AirDtos.SpellMinimumLevel"/>. Patch 1.0.0.129 added Surge (16, SummonerBattleCry) and put Promote on Summoner's Rift (20,
    /// SummonerPromoteSR, CLASSIC and ODIN; 1.45's Dominion-only Promote 18 is gone), and took Fortify (5) and Rally (9) out of the pool:
    /// they stay listed, inactive (wiki V1.0.0.129, research <c>scratch/patch132/a-wiki</c> 129.40, 129.45, 129.48).
    /// </summary>
    private static readonly (int Id, string Name, bool Active, string[] Modes)[] Catalogue =
    [
        (16, "SummonerBattleCry", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (1, "SummonerBoost", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (2, "SummonerClairvoyance", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (14, "SummonerDot", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (3, "SummonerExhaust", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (4, "SummonerFlash", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (5, "SummonerFortify", false, ["CLASSIC", "TUTORIAL"]),
        (6, "SummonerHaste", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (7, "SummonerHeal", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (13, "SummonerMana", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (17, "SummonerOdinGarrison", true, ["ODIN"]),
        (20, "SummonerPromoteSR", true, ["CLASSIC", "ODIN"]),
        (9, "SummonerRally", false, ["CLASSIC", "TUTORIAL"]),
        (10, "SummonerRevive", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (11, "SummonerSmite", true, ["CLASSIC", "TUTORIAL", "ODIN"]),
        (12, "SummonerTeleport", true, ["CLASSIC", "TUTORIAL"]),
    ];

    /// <summary>
    /// The inactive spells, sent as the system states' <c>inactiveSpellIdList</c>: Fortify and Rally. The list is not optional: whenever it
    /// arrives (and 1.52 always builds one, ClientSystemStatesNotification.as:113) the client sets every spell's <c>active</c> to "not in this
    /// list" (Inventory.updateSpellDictionary and updateInactiveSpells_internal, Inventory.as:294-305, 787-806), so an empty list would offer
    /// the two again.
    /// </summary>
    public static readonly int[] InactiveSpellIds = [.. Catalogue.Where(row => !row.Active).Select(row => row.Id)];

    /// <summary>
    /// Every summoner the catalogue lists for CLASSIC or TUTORIAL, inactive ones included, so that a saved Fortify or Rally still resolves
    /// (<see cref="SpellId"/>). Batch 798, Reconstruction: such a seat gets the line's default pair, as for any spell its map does not offer
    /// (PlatformGames.OfferedSeat); 1.52 shows a seat's spells as the GameDTO names them (ChampionSelectionController.findAssignedItems,
    /// as:1694-1729) and has no handling of its own for an inactive one, and retail's server-side answer is not recovered.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> SpellNames = Catalogue.Where(row => row.Modes is not ["ODIN"])
        .ToDictionary(row => row.Id, row => row.Name);
    /// <summary>The Dominion-only summoners (gameModes ODIN alone): Garrison. A Dominion pick of one is for that game only (PlatformGames.SelectSpells).</summary>
    public static readonly IReadOnlyDictionary<int, string> DominionSpellNames = Catalogue.Where(row => row.Modes is ["ODIN"])
        .ToDictionary(row => row.Id, row => row.Name);
    /// <summary>Every summoner spell id the platform knows, on any map.</summary>
    public static readonly IReadOnlyDictionary<int, string> AllSpellNames = SpellNames.Concat(DominionSpellNames)
        .ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>
    /// The summoner spells a game on <paramref name="mapId"/> offers, as 1.52's champion select lists them
    /// (ChampionSelectionController.initializeSpells, as:2218-2247): an active spell (Inventory.getActiveSpells, as:500-511) whose gameModes
    /// holds the map's mode (<see cref="GameMaps.ModeOf"/>: ODIN on the Crystal Scar, TUTORIAL on the Basic Tutorial's map, else CLASSIC); the
    /// summoner level is <see cref="SpellUsable"/>'s. The per-mode inactive lists stay empty (AirDtos.ClientSystemStates).
    /// Reconstruction: the list is keyed by map, so Battle Training (Summoner's Rift in TUTORIAL) is offered the CLASSIC list, Promote
    /// included; its client lists only the TUTORIAL spells and so never sends Promote, and a saved Promote is kept there.
    /// </summary>
    public static IReadOnlyDictionary<int, string> SpellNamesFor(int mapId)
    {
        var mode = GameMaps.ModeOf(mapId);
        return Catalogue.Where(row => row.Active && row.Modes.Contains(mode)).ToDictionary(row => row.Id, row => row.Name);
    }

    /// <summary>
    /// The GameDTO's bans. 1.52 reads <c>bannedChampions</c>, a list of <c>BannedChampion</c> (championId, teamId, pickTurn, all int;
    /// BannedChampion.as), where 1.45 read <c>bannedChampionIds</c> (GameDTO.as). Champion select marks each championId banned
    /// (ChampionSelectionController.updateBannedChampionStates, as:1126-1148) and announces the new ones (printNewConfirmedBans,
    /// as:1659-1672); the spectator view files each ban under the banning team, teamId 100 or 200, and reads the list's length
    /// (SpectatorChampionSelectionView.addAlreadyBannedChampions, as:588-606), so the list is always sent. pickTurn is the ban's turn.
    /// </summary>
    private static (string Name, object? Value) Bans(PlatformGames.Lobby g) => ("bannedChampions", List(g.BannedChampions.Select(ban =>
        (object)Object(AirProtocolNames.BannedChampion, ("championId", ban.Champion), ("teamId", ban.Team == Team.Order ? 100 : 200),
            ("pickTurn", ban.Turn)))));
}
