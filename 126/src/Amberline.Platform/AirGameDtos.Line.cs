using static Amberline.Platform.AirWire;

namespace Amberline.Platform;

/// <summary>
/// The AIR line's champion-select tables: patch 1.0.0.126's LolClient 1.45 (batch 798 moved them here unchanged from the shared
/// <c>AirGameDtos.cs</c>, where 1.0.0.132's line keeps its own copy).
/// </summary>
public static partial class AirGameDtos
{
    public static readonly IReadOnlyDictionary<int, string> SpellNames = new Dictionary<int, string>
    { [1] = "SummonerBoost", [2] = "SummonerClairvoyance", [3] = "SummonerExhaust", [4] = "SummonerFlash", [5] = "SummonerFortify", [6] = "SummonerHaste", [7] = "SummonerHeal", [9] = "SummonerRally", [10] = "SummonerRevive", [11] = "SummonerSmite", [12] = "SummonerTeleport", [13] = "SummonerMana", [14] = "SummonerDot" };
    /// <summary>
    /// Batch 455: the Dominion-only summoners (AIR SpellGeneratedData ids 17 and 18). The plan is in docs/research/2026-10-28-dominion-plan.md, section 5.4.
    /// Their inibins ship under DATA/Spells, and the AIR spell DTO lists them with gameModes ODIN only (AirDtos.Spell). NOT RETAIL: the stock
    /// summoner list of Summoner's Rift never offers them. Batch 500: Promote (18) joins Garrison (17), as SpellGeneratedData.as lists it.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> DominionSpellNames = new Dictionary<int, string>
    { [17] = "SummonerOdinGarrison", [18] = "SummonerOdinPromote" };
    /// <summary>Batch 500: every summoner spell id the platform knows, on any map (the classic list and the Dominion pair).</summary>
    public static readonly IReadOnlyDictionary<int, string> AllSpellNames = SpellNames.Concat(DominionSpellNames)
        .ToDictionary(pair => pair.Key, pair => pair.Value);
    /// <summary>
    /// Batch 500: the summoner spell ids the ODIN game mode offers, as SpellGeneratedData.as lists them (gameModes contains "ODIN", checked
    /// 31 October 2026): every classic spell but Fortify (5), Rally (9) and Teleport (12), plus Garrison (17) and Promote (18).
    /// </summary>
    public static readonly int[] OdinSpellIds = [1, 2, 3, 4, 6, 7, 10, 11, 13, 14, 17, 18];
    /// <summary>The summoner spells a game on <paramref name="mapId"/> offers: map 8 (ODIN) its own list, every other map the classic one.</summary>
    public static IReadOnlyDictionary<int, string> SpellNamesFor(int mapId) => mapId == Amberline.Core.GameMaps.CrystalScar
        ? AllSpellNames.Where(pair => OdinSpellIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)
        : SpellNames;

    /// <summary>The GameDTO's bans: 1.45's <c>bannedChampionIds</c>, the champion ids in ban order.</summary>
    private static (string Name, object? Value) Bans(PlatformGames.Lobby g) =>
        ("bannedChampionIds", List(g.BannedChampions.Select(ban => (object)ban.Champion)));

    /// <summary>
    /// Batch 848: a spectator's key goes in 1.45's <c>encryptionKey</c>, the field its SpectatorController.startSpectating hands Maestro
    /// (SpectatorController.as:131-151); 1.45's PlayerCredentialsDTO has no observerEncryptionKey.
    /// </summary>
    private static void ObserverKey(RtmpSharp.IO.AsObject credentials, string key) => credentials["encryptionKey"] = key;
}
