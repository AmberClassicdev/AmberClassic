using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Champions;
using Amberline.Tools.Audits;
using Amberline.Tools.Diagnostics;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Spells;

/// <summary>
/// <c>ChampionCatalog</c> is the one champion list. These checks keep it honest against the shipped files,
/// the registered champion modules and everything a champion needs to be selectable in AIR.
/// </summary>
public static class ChampionCatalogChecks
{
    [Check("spells-champion-catalog-matches-shipped-data")]
    public static void CatalogMatchesTheShippedClientAndAirData()
    {
        var all = ChampionCatalog.All;
        // Batch 795: the rows are the AIR 1.52 client's (1.0.0.132), 91 champions; 126's AIR 1.45 shipped 84.
        TestAssert.Equal(91, all.Count, "the AIR 1.52 client of 1.0.0.132 ships 91 champions");
        TestAssert.Equal(91, all.Select(champion => champion.Id).Distinct().Count());
        TestAssert.Equal(91, all.Select(champion => champion.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        TestAssert.Equal(all.Select(champion => champion.Id).Order().ToArray(), all.Select(champion => champion.Id).ToArray(),
            "the catalog is ordered by id");

        var data = GameData.Default;
        foreach (var champion in all)
        {
            // The name is the DATA/Characters folder, so every champion resolves to shipped data.
            TestAssert.True(File.Exists(data.Paths.Data($"Characters/{champion.Name}/{champion.Name}.inibin")),
                $"{champion.Name}: shipped character inibin");
            TestAssert.True(champion.SkinCount >= 1, $"{champion.Name}: skin count");
            TestAssert.Equal(5, champion.AbilityIcons.Count, $"{champion.Name}: passive + QWER icons");
            TestAssert.True(champion.AbilityIcons.All(icon => icon.Length > 0 && !icon.EndsWith(".dds", StringComparison.Ordinal)),
                $"{champion.Name}: icon names carry no extension");
            // Retail bots are exactly the champions the client names itself (fontconfig game_bot_<Name>).
            TestAssert.Equal(data.ClientNamesBot(champion.Name), champion.RetailBot, $"{champion.Name}: retail bot flag");
            TestAssert.Equal(champion.Role is not null, champion.Playable, $"{champion.Name}: playable <=> role");
            TestAssert.Equal(champion.Playable, champion.DefaultIp is not null, $"{champion.Name}: playable <=> price");
        }
        TestAssert.Equal(18, all.Count(champion => champion.RetailBot), "the 18 game_bot_ champions");
        TestAssert.True(ChampionCatalog.ByName("leesin")?.Name == "LeeSin" && ChampionCatalog.ById(53)?.Name == "Blitzcrank" &&
            ChampionCatalog.ByName("Teemo")?.Id == 17 && ChampionCatalog.ByName("Nobody") is null && ChampionCatalog.ById(999) is null,
            "lookups by name (any case) and id");

        // Independent pins: the ten playable ids and skin counts as the AIR client lists them. Batch 795 restated the skin counts to the AIR
        // 1.52 client (its ChampionGeneratedData.as in the batch 791 JPEXS export): Annie 6 -> 7, LeeSin 2 -> 3, Nidalee 4 -> 5, Shaco 4 -> 5,
        // Ezreal 4 -> 5, Janna 2 -> 3, Blitzcrank 4 -> 5, TwistedFate 5 -> 6 (the Harrowing 2011, Snowdown 2011 and Lunar Revel 2012 skins).
        var pinned = new (string Name, int Id, int Skins)[]
        {
            ("Annie", 1, 7), ("Orianna", 61, 2), ("LeeSin", 64, 3), ("Nidalee", 76, 5), ("Yorick", 83, 2),
            ("Shaco", 35, 5), ("Ezreal", 81, 5), ("Vayne", 67, 3), ("Janna", 40, 3), ("Blitzcrank", 53, 5),
            // Appended by the BuildingBlocks engine M2 (16 October 2026): the first script-driven champion.
            ("Soraka", 16, 2),
            // Appended by the BuildingBlocks engine M3 (16 October 2026).
            ("Taric", 44, 3), ("Kayle", 10, 5), ("Ryze", 13, 6), ("Sivir", 15, 5),
            // Appended by the new-champion phase (19 October 2026, wave 1): the first champion that was never hand-written.
            ("Kassadin", 38, 4),
            // Appended by phase 3, wave 1 (19 October 2026): new champions from their shipped scripts.
            ("Udyr", 77, 2),
            // Appended by phase 3, wave 2 (19 October 2026): Morgana (W33c), then Xin Zhao; the merge of the two had left Morgana out of the pins.
            ("Morgana", 25, 3),
            // Appended by phase 3, wave 2 (19 October 2026): Xin Zhao.
            ("XinZhao", 5, 4),
            // Appended by phase 3, wave 3 (19 October 2026): Amumu (W23b).
            ("Amumu", 32, 6),
            // Wave 3 (19 October 2026): a retail bot, the first of the combat-hook champions.
            ("Alistar", 12, 5),
            // Wave 2 (19 October 2026): Brand (W19f), appended after Alistar as the roster is.
            ("Brand", 63, 3),
            // Appended by phase 3, wave 5 (19 October 2026): Twisted Fate (W24b).
            ("TwistedFate", 4, 6),
        };
        // The pins are the roster as it stood: the development order never changes, a new champion is appended after them. The pinned rows stay
        // exact; whoever is appended later is held to the shipped data below (installed skins = the catalog's skin count + the default).
        TestAssert.Equal(pinned.Select(p => p.Name).ToArray(), ChampionCatalog.PlayableNames.Take(pinned.Length).ToArray(),
            "playable champions keep the development roster order");
        // Batch 795: the AIR 1.52 rows list the skins the 1.0.0.132 game data added (batch 785 counted one more installed skin than the 1.45
        // rows for 29 champions), so every champion's installed skins are again the default plus its AIR catalog.
        foreach (var appended in ChampionCatalog.Playable.Skip(pinned.Length))
            TestAssert.Equal(appended.SkinCount + 1, data.Skins(appended.Name).Count, appended.Name + " installed skins (default + AIR catalog)");
        foreach (var (name, id, skins) in pinned)
        {
            var champion = ChampionCatalog.ByName(name)!;
            TestAssert.Equal(id, champion.Id, name + " id");
            TestAssert.Equal(skins, champion.SkinCount, name + " skin count");
            TestAssert.Equal(skins + 1, data.Skins(name).Count, name + " installed skins (default + AIR catalog)");
        }
    }

    /// <summary>
    /// Batch 594: the rows are read at runtime from the player's AIR client (AirGeneratedContent.swf) and fontconfig, so no client value
    /// is in the source. This pins what the reader loads by count and hash only: a reader or file change shows here. The hash is of the
    /// rows the generated literal table held until batch 594 (compared field by field when the reader replaced it).
    /// </summary>
    [Check("spells-champion-catalog-runtime-load-hash")]
    public static void TheRuntimeLoadedCatalogMatchesItsPinnedHash()
    {
        var all = ChampionCatalog.All;
        // Batch 795: the AIR 1.52 client's rows (126's AIR 1.45: 84 champions, 312 skins, hash 6d7ce3ce...e5fe). The new hash was computed
        // twice: by this reader and, independently, from the JPEXS export of the 1.52 ChampionGeneratedData.as with the 1.0.0.132 fontconfig.
        TestAssert.Equal(91, all.Count, "champions read from AirGeneratedContent.swf");
        TestAssert.Equal(355, all.Sum(champion => champion.SkinCount), "skins read from AirGeneratedContent.swf");
        var canonical = string.Join("\n", all.Select(champion => string.Join("|", champion.Id, champion.Name, champion.SkinCount,
            string.Join(",", champion.SearchTags), string.Join(",", champion.AbilityIcons), champion.RetailBot ? 1 : 0)));
        TestAssert.Equal(ExpectedCatalogHash,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))),
            "SHA-256 of the loaded rows (id|name|skins|tags|icons|bot, by id)");
    }

    private const string ExpectedCatalogHash = "5bbaddc139beb2fe4fa592e277f16ebb319cf7e1de94a2f5676551d159308a2d";

    /// <summary>
    /// Batch 795: master is the 1.0.0.132 line, so its champion rows are the AIR 1.52 client's (found as the 1.0.0.132 install's AIR copy or
    /// the read-only reference <c>132_AIR</c>; the 126 install's 1.45 copy is never read). Pinned by the reference's own file and by the seven
    /// champions and the skins 1.52 added: before, the rows were the 1.45 client's (84 champions, the seven missing, Annie with 6 skins).
    /// </summary>
    [Check("spells-champion-catalog-132-air-rows")]
    public static void TheCatalogReadsTheAir152Rows()
    {
        var swf = AirChampionData.ResolveSwf();
        TestAssert.False(swf.Contains("AIR126-PREVIEW", StringComparison.OrdinalIgnoreCase), "the 126 install's AIR copy was read: " + swf);
        // assets\swfs\AirGeneratedContent.swf of D:\132_AIR (batch 791 report, section 1); the 1.0.0.132 install's copy is byte-identical.
        TestAssert.Equal("d6ec0375bcaabb3eeb07f06af35ed3f8ce94c4b113af3b77f014f8a2fe54bcd3",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(swf))), "the AIR 1.52 AirGeneratedContent.swf");
        TestAssert.Equal(91, ChampionCatalog.All.Count, "AIR 1.52 lists 91 champions (1.45: 84)");
        TestAssert.Equal(355, ChampionCatalog.All.Sum(champion => champion.SkinCount), "AIR 1.52 lists 355 skin rows (1.45: 312)");
        // The seven champions of 1.0.0.132, each with its classic skin and one more, are AIR rows and playable.
        foreach (var (name, id) in new[] { ("Shyvana", 102), ("Ahri", 103), ("Graves", 104), ("Fizz", 105), ("Volibear", 106), ("Viktor", 112), ("Sejuani", 113) })
        {
            var champion = ChampionCatalog.ById(id);
            TestAssert.True(champion is { Playable: true, SkinCount: 2 } && champion.Name == name, $"{name} ({id}): an AIR 1.52 row, playable, 2 skins");
        }
        // Skins 1.52 added to existing champions (the 132 data's MeshSkin sections; tools.audit.data names all 29).
        foreach (var (name, skins) in new[] { ("Annie", 7), ("TwistedFate", 6), ("Heimerdinger", 4), ("Lux", 4) })
            TestAssert.Equal(skins, ChampionCatalog.ByName(name)!.SkinCount, name + " skins in AIR 1.52");
    }

    [Check("spells-champion-catalog-playable-equals-modules")]
    public static void PlayableSetEqualsRegisteredModulesAndEverySelectabilityInput()
    {
        var playable = ChampionCatalog.Playable;
        var names = playable.Select(champion => champion.Name).Order(StringComparer.Ordinal).ToArray();
        // Batch 769 (1.0.0.132) served seven champions the AIR 1.45 rows did not list; batch 795 reads the AIR 1.52 rows, which list every
        // served champion, so the AIR-listed playable champions are the served roster again.
        var served = ChampionCatalog.PlayableNames.Order(StringComparer.Ordinal).ToArray();
        TestAssert.Equal(served, names, "the AIR-listed playable champions are the served ones");
        TestAssert.Equal(Array.Empty<string>(), served.Where(name => ChampionCatalog.ByName(name) is null).ToArray(),
            "every served champion has an AIR 1.52 row");

        // Supported = registered module: the explicit dispatcher list in ProductionMatchComposition is the other half.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, "Annie", "Shaco");
        var dispatcher = (ChampionSpellDispatcher)runtime.Dependencies.ChampionDispatcher;
        TestAssert.Equal(served, dispatcher.RegisteredChampionNames.Order(StringComparer.Ordinal).ToArray(),
            "the served champions are exactly the registered modules");
        foreach (var champion in ChampionCatalog.All)
            TestAssert.Equal(champion.Playable, dispatcher.FindModule(champion.Name) is not null,
                $"{champion.Name}: playable <=> registered module");

        // Every list the server derives from the catalog agrees with it (the game server's with the served roster, AIR's with its rows).
        void Same(IEnumerable<string> actual, string what) =>
            TestAssert.Equal(served, actual.Order(StringComparer.Ordinal).ToArray(), what);
        Same(ServerOptions.SupportedChampions, "ServerOptions.SupportedChampions");
        TestAssert.Equal(names, AirGameDtos.ChampionNames.Values.Order(StringComparer.Ordinal).ToArray(), "AirGameDtos.ChampionNames");
        Same(BotController.SupportedChampions, "BotController.SupportedChampions");
        Same(DeathRecapOrigin.ChampionNames, "DeathRecapOrigin.ChampionNames");
        // The default headless match seats ten champions (five per side): the first ten playable ones, the hand-written ten.
        TestAssert.Equal(10, HeadlessMatch.DefaultRoster.Count, "HeadlessMatch.DefaultRoster seats ten champions");
        TestAssert.Equal(ChampionCatalog.PlayableNames.Take(10).ToArray(), HeadlessMatch.DefaultRoster.ToArray(),
            "HeadlessMatch.DefaultRoster is the first ten playable champions");
        Same(ChampionRosterAudit.Inventory().Champions.Select(row => row.Name), "ChampionRosterAudit");

        // Batch 65 (owner decision): AIR lists every catalog champion (91 in AIR 1.52); an unplayable one is locked (owned and bot flags false)
        // and a playable one is owned and bot-enabled. The lists below (economy id, store, skins, bots) stay playable-only.
        var air = ((ArrayCollection)AirDtos.Champions()).Cast<AsObject>().ToArray();
        TestAssert.Equal(ChampionCatalog.All.Select(champion => (double)champion.Id).Order().ToArray(),
            air.Select(row => Convert.ToDouble(row["championId"])).Order().ToArray(), "AIR champion list");
        foreach (var row in air)
        {
            var champion = ChampionCatalog.All.Single(candidate => candidate.Id == (int)Convert.ToDouble(row["championId"]));
            TestAssert.Equal(champion.Playable, Convert.ToBoolean(row["owned"]), $"{champion.Name}: AIR grid owned <=> playable");
            TestAssert.Equal(champion.Playable, Convert.ToBoolean(row["botEnabled"]), $"{champion.Name}: AIR grid bot flag <=> playable");
        }
        var artRoot = Path.Combine(Path.GetTempPath(), "league-catalog-art-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Store art exists for every champion id, as in the staged AIR client; only the catalog may decide who is offered.
            var championArt = Path.Combine(artRoot, "assets", "storeImages", "content", "champions");
            Directory.CreateDirectory(championArt);
            foreach (var champion in ChampionCatalog.All)
                File.WriteAllBytes(Path.Combine(championArt, $"{champion.Id}_Web_0.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
            var store = new PlatformStoreCatalog(new PlatformEconomyRules(), artRoot);
            foreach (var champion in ChampionCatalog.All)
            {
                var offered = store.TryGetStockChampion(champion.Id, out var item);
                // Owner decision of 18 October 2026: the store lists all 84 champions at their retail price; the ones the server cannot play are
                // Locked and PlatformAccounts.Purchase refuses them (the same rule as the AIR grid above).
                TestAssert.True(offered, $"{champion.Name}: the store lists every champion");
                TestAssert.Equal(!champion.Playable, item.Locked, $"{champion.Name}: the store locks the champions the server cannot play");
                TestAssert.Equal(champion.Playable, PlatformBots.IsBotEnabled(champion.Id), $"{champion.Name}: lobby bot");
                TestAssert.Equal(champion.Playable ? champion.Id : 0, PlatformEconomy.ChampionId(champion.Name),
                    $"{champion.Name}: economy id");
                if (!champion.Playable) continue;
                TestAssert.Equal($"content/champions/{champion.Id}_Web_0.jpg", item.ImagePath, $"{champion.Name}: store art");
                // Batch 597: the 2011 price list is operator content; the neutral list prices every champion alike.
                if (PlatformContent.Has("store/prices.json")) TestAssert.Equal(champion.DefaultIp, item.IpPrice, $"{champion.Name}: store price");
                TestAssert.Equal(champion.Name, AirGameDtos.ChampionNames[champion.Id], $"{champion.Name}: AIR id");
                var skins = AirGameDtos.SkinIds(champion.Id, skinsOff: false).ToArray();
                TestAssert.True(skins.Length >= 1 && skins.All(skin => skin > champion.Id * 1000 && skin <= champion.Id * 1000 + champion.SkinCount),
                    $"{champion.Name}: AIR skins");
                TestAssert.True(store.ChampionContent(champion.Id) is { Abilities.Count: 5 }, $"{champion.Name}: store content");
                TestAssert.True(PlatformStoreCatalog.ChampionSearchTags(champion.Id).SequenceEqual(champion.SearchTags),
                    $"{champion.Name}: search tags");
            }
        }
        finally { if (Directory.Exists(artRoot)) Directory.Delete(artRoot, recursive: true); }

        // The AIR client the rows come from (the 1.0.0.132 install's copy or the AIR 1.52 reference; batch 795, before: the 126 install's)
        // carries the store art of every playable champion.
        var staged = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(AirChampionData.ResolveSwf())!, "..", "storeImages", "content", "champions"));
        if (Directory.Exists(staged))
            foreach (var champion in playable)
                TestAssert.True(File.Exists(Path.Combine(staged, $"{champion.Id}_Web_0.jpg")),
                    $"{champion.Name}: staged AIR store art");
    }
}
