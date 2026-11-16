using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 798 (AIR 132 B, the lobby and champion select of LolClient 1.52.12_01_17_13_40): the GameDTO's bans, the six-ban draft, the 1.52
/// summoner pool, and a guard that every object the platform sends declares only fields the 1.52 classes have. Evidence is the 1.52 export
/// (<c>scratch/patch132/u-air132</c>); file:line references below are into it.
/// </summary>
public static class Air132ChampionSelectChecks
{
    private const string BannedChampionAlias = "com.riotgames.platform.game.BannedChampion";

    /// <summary>
    /// 1.52's GameDTO has no <c>bannedChampionIds</c>; it reads <c>bannedChampions</c>, BannedChampion objects of championId, teamId and
    /// pickTurn (GameDTO.as, BannedChampion.as). Champion select marks each championId banned (ChampionSelectionController.as:1126-1148) and
    /// the spectator view files a ban under its teamId, 100 or 200, and reads the list's length (SpectatorChampionSelectionView.as:588-606).
    /// </summary>
    [Check("server.air132-gamedto-banned-champions")]
    public static void GameDtoSendsBannedChampions() => BannedChampionsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Patch 1.0.0.129 made the draft six bans (wiki 129.57): the draft config's maxAllowableBans, which the tips print as the ban progress
    /// and whose six-ban branch the client has (ChampionSelectionTipsController.as:398, 540-546). The client takes the banning team from the
    /// ACTIVE participant (ChampionSelectionController.findBanTeam, as:1413-1432); the server's order is the captains alternating, team one
    /// first (Reconstruction, AirLine.DraftMaxBans).
    /// </summary>
    [Check("server.air132-draft-six-bans")]
    public static void DraftHasSixAlternatingBans() => SixBansAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The 1.52 summoner pool (SpellGeneratedData.as:32-47): Surge (16) and Promote on Summoner's Rift (20, level 8) are offered, Fortify (5)
    /// and Rally (9) are inactive and offered nowhere, Garrison is level 1; the system states keep the two inactive (Inventory.as:294-305);
    /// a saved Fortify or Rally seats the default pair; no bot holds a removed spell.
    /// </summary>
    [Check("server.air132-summoner-pool")]
    public static void SummonerPoolIsThe152Catalogue() => SummonerPoolAsync().GetAwaiter().GetResult();


    private static async Task BannedChampionsAsync()
    {
        var root = TemporaryDirectory("league-air132-bans");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            var blindOwner = rig.SignIn("air132blindowner");
            var blind = (AsObject)rig.Games.Create(rig.Accounts.Get(blindOwner.Id), "Blind 132", "", GameMaps.Normal, 2,
                AirGameDtos.BlindPickConfigId, GameMaps.ClassicMode, "NONE");
            TestAssert.False(blind.ContainsKey("bannedChampionIds"), "1.52's GameDTO declares no bannedChampionIds");
            TestAssert.True(blind["bannedChampions"] is ArrayCollection { Count: 0 }, "a game without bans sends an empty list, never null");

            var a = rig.SignIn("air132bana");
            var b = rig.SignIn("air132banb");
            var id = DraftLobby(rig, a, b);
            // Team one's captain lets the first turn run out; team two's bans in turn 2, team one's in turn 3.
            rig.Clock.Now = rig.Clock.Now.AddSeconds(PlatformGames.DraftBanServerSeconds);
            rig.Games.Tick();
            rig.Games.Ban(b.Id, 22);
            rig.Games.Ban(a.Id, 1);
            var game = (AsObject)rig.Games.Get(a.Id, id);
            TestAssert.False(game.ContainsKey("bannedChampionIds"));
            var bans = ((ArrayCollection)game["bannedChampions"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(2, bans.Length);
            foreach (var ban in bans)
            {
                TestAssert.Equal(BannedChampionAlias, ban.TypeName);
                TestAssert.Equal("championId,pickTurn,teamId", string.Join(",", ban.Keys.Order(StringComparer.Ordinal)), "BannedChampion's fields");
                TestAssert.True(ban.Values.All(value => value is int), "the three fields are AS3 ints");
            }
            TestAssert.Equal("22:200:2,1:100:3", string.Join(",", bans.Select(ban => $"{ban["championId"]}:{ban["teamId"]}:{ban["pickTurn"]}")),
                "each ban names its champion, the banning team (100 team one, 200 team two) and its turn");
            // The objects cross the AMF writer typed.
            var decoded = (AsObject)Air132LoginChecks.Decode(game);
            TestAssert.True(((ArrayCollection)decoded["bannedChampions"]).Cast<AsObject>().All(ban => ban.TypeName == BannedChampionAlias));
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task SixBansAsync()
    {
        var draft = AirDtos.GameTypes().Cast<AsObject>().Single(config => Equals(config["id"], 2d));
        TestAssert.Equal(6, draft["maxAllowableBans"], "the draft config's six bans");
        var root = TemporaryDirectory("league-air132-six-bans");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            var a = rig.SignIn("air132sixa");
            var b = rig.SignIn("air132sixb");
            var id = DraftLobby(rig, a, b);
            int[] champions = [1, 22, 61, 64, 81, 76];
            for (var turn = 1; turn <= champions.Length; turn++)
            {
                var game = (AsObject)rig.Games.Get(a.Id, id);
                TestAssert.Equal("PRE_CHAMP_SELECT", game["gameState"], $"ban turn {turn} is in the ban phase");
                TestAssert.Equal(turn, game["pickTurn"]);
                var (captain, other) = turn % 2 == 1 ? (a, b) : (b, a);
                TestAssert.Equal(1, PickMode(game, captain), $"turn {turn}: {captain.Summoner} bans");
                TestAssert.Equal(0, PickMode(game, other), $"turn {turn}: {other.Summoner} waits");
                TestAssert.Throws<AirFault>(() => rig.Games.Ban(other.Id, champions[turn - 1]), "a ban out of turn is refused");
                rig.Games.Ban(captain.Id, champions[turn - 1]);
            }
            var picks = (AsObject)rig.Games.Get(a.Id, id);
            TestAssert.Equal("CHAMP_SELECT", picks["gameState"], "the picks begin after the sixth ban");
            TestAssert.Equal(1, picks["pickTurn"]);
            var bans = ((ArrayCollection)picks["bannedChampions"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(string.Join(",", champions), string.Join(",", bans.Select(ban => ban["championId"])));
            TestAssert.Equal("100,200,100,200,100,200", string.Join(",", bans.Select(ban => ban["teamId"])), "1-2-1-2-1-2");
            TestAssert.Equal("1,2,3,4,5,6", string.Join(",", bans.Select(ban => ban["pickTurn"])));
            TestAssert.Equal("PG-0031", Expect(() => rig.Games.Pick(a.Id, 76)).ErrorCode, "the sixth ban holds in the picks");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task SummonerPoolAsync()
    {
        static string Ids(IReadOnlyDictionary<int, string> spells) => string.Join(",", spells.Keys.Order());
        // The catalogue by mode: active spells whose gameModes hold the map's mode.
        foreach (var map in new[] { GameMaps.Normal, GameMaps.Harrowing, GameMaps.TwistedTreeline })
            TestAssert.Equal("1,2,3,4,6,7,10,11,12,13,14,16,20", Ids(AirGameDtos.SpellNamesFor(map)), $"map {map} (CLASSIC)");
        TestAssert.Equal("1,2,3,4,6,7,10,11,13,14,16,17,20", Ids(AirGameDtos.SpellNamesFor(GameMaps.CrystalScar)), "the Crystal Scar (ODIN)");
        TestAssert.Equal("1,2,3,4,6,7,10,11,12,13,14,16", Ids(AirGameDtos.SpellNamesFor(GameMaps.Tutorial)), "the Basic Tutorial (TUTORIAL): no Promote");
        foreach (var (name, id, level) in new[] { ("SummonerBattleCry", 16, 1), ("SummonerPromoteSR", 20, 8), ("SummonerOdinGarrison", 17, 1),
                     ("SummonerFortify", 5, 4), ("SummonerRally", 9, 8), ("SummonerFlash", 4, 12), ("SummonerTeleport", 12, 1) })
        {
            TestAssert.Equal(id, AirGameDtos.SpellId(name), $"{name}'s 1.52 id");
            TestAssert.Equal(level, AirDtos.SpellMinimumLevel(name), $"{name}'s 1.52 minimum level");
        }
        TestAssert.False(AirGameDtos.AllSpellNames.ContainsKey(18), "1.45's Dominion Promote (18) is no 1.52 spell");
        TestAssert.Throws<ArgumentException>(() => AirDtos.SpellMinimumLevel("SummonerOdinPromote"));

        // The system states keep Fortify and Rally inactive; the per-mode lists stay empty.
        var login = Air132LoginChecks.Decode(AirDtos.LoginData(Air132LoginChecks.Account()));
        using (var states = JsonDocument.Parse(Air132LoginChecks.JsonText(((AsObject)login)["clientSystemStates"])))
        {
            static string List(JsonElement value) => string.Join(",", value.EnumerateArray().Select(item => item.GetInt32()));
            TestAssert.Equal("5,9", List(states.RootElement.GetProperty("inactiveSpellIdList")), "an empty list would offer them again");
            foreach (var key in new[] { "inactiveClassicSpellIdList", "inactiveOdinSpellIdList", "inactiveTutorialSpellIdList" })
                TestAssert.Equal("", List(states.RootElement.GetProperty(key)), key);
        }

        var root = TemporaryDirectory("league-air132-summoner-pool");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            // A level-one summoner: Surge is offered at level one, Promote needs eight, Fortify and Rally are offered at no level.
            var host = rig.SignIn("air132poolhost");
            var economy = rig.SignIn("air132pooleconomy", maxed: false);
            Start(rig, host, economy, GameMaps.Normal);
            rig.Games.SelectSpells(economy.Id, 16, 7);
            TestAssert.Equal((16, 7), Seat(rig, economy.Id), "a level-one summoner picks Surge");
            TestAssert.Throws<ArgumentException>(() => rig.Games.SelectSpells(economy.Id, 20, 7), "Promote unlocks at level eight");
            foreach (var removed in new[] { 5, 9 })
            {
                TestAssert.Throws<ArgumentException>(() => rig.Games.SelectSpells(host.Id, removed, 7), $"spell {removed} is out of the pool");
                TestAssert.Throws<ArgumentException>(() => rig.Games.SelectSpells(economy.Id, removed, 7), $"spell {removed} is out of the pool");
            }
            // A full summoner picks Promote on Summoner's Rift; the classic pick is saved.
            rig.Games.SelectSpells(host.Id, 20, 4);
            TestAssert.Equal((20, 4), Seat(rig, host.Id));
            TestAssert.Equal("SummonerPromoteSR", rig.Accounts.Get(host.Id).Loadout.SummonerSpell1, "a Summoner's Rift pick of Promote is kept");

            // A summoner who saved Fortify and Rally before 1.0.0.132 is seated with the default pair (Reconstruction).
            var veteran = rig.SignIn("air132poolveteran");
            rig.Accounts.SaveLoadout(veteran.Id, rig.Accounts.Get(veteran.Id).Loadout with
            { SummonerSpell1 = "SummonerFortify", SummonerSpell2 = "SummonerRally" });
            var other = rig.SignIn("air132poolother");
            Start(rig, other, veteran, GameMaps.Normal);
            TestAssert.Equal((AirGameDtos.SpellId(SummonerLoadout.DefaultFirst), AirGameDtos.SpellId(SummonerLoadout.DefaultSecond)),
                Seat(rig, veteran.Id), "a saved Fortify and Rally give the seat Flash and Ignite");

            // The Crystal Scar: Garrison and Promote at level 30; Garrison is Dominion-only, so the pick is not saved.
            var odinHost = rig.SignIn("air132poolodinhost");
            var odin = rig.SignIn("air132poolodin");
            Start(rig, odinHost, odin, GameMaps.CrystalScar);
            rig.Games.SelectSpells(odin.Id, 17, 20);
            TestAssert.Equal((17, 20), Seat(rig, odin.Id));
            TestAssert.Equal(SummonerLoadout.DefaultFirst, rig.Accounts.Get(odin.Id).Loadout.SummonerSpell1, "a Dominion pick is for that game only");

            // No bot holds a removed spell: the shipped bot pairs and the default pair are all offered on Summoner's Rift.
            var offered = AirGameDtos.SpellNamesFor(GameMaps.Normal).Values.ToHashSet();
            foreach (var champion in AirGameDtos.ChampionNames.Keys.Where(PlatformBots.IsBotEnabled))
            {
                var seat = PlatformBots.Seat(champion, Team.Chaos, 0);
                TestAssert.True(offered.Contains(seat.SummonerSpell1) && offered.Contains(seat.SummonerSpell2),
                    $"{seat.Champion}'s bot spells ({seat.SummonerSpell1}, {seat.SummonerSpell2}) are in the 1.52 pool");
            }
        }
        finally { DeleteDirectory(root); }
    }


    /// <summary>A solo blind-pick game launched on the built match server, as server.air-solo-handoff-reconnect-and-retry runs it.</summary>
    private static async Task GameStartAsync(string root, List<(string What, object? Value)> sent)
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();
        using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
        var account = accounts.RegisterPublic("air132fieldstart", "testing-password", "Field Start");
        var sessions = new PlatformSessions(accounts);
        var session = sessions.Login("start", account.Username, sessions.IssueTicket(account), AirLine.ClientVersion);
        TestAssert.True(sessions.AuthenticateChannel("start", account.Username, session.Token));
        var clock = new ManualClock();
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Amberline.Server", "release", "Amberline.Server.exe"));
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, executable,
            FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
        var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
        games.Push = (_, body) => sent.Add(("push", body));
        var rpc = new PlatformServices(accounts, sessions, games);
        var lobby = (AsObject)rpc.Route("start", "gameService", "createPracticeGame",
            ["Field start", GameMaps.ClassicMode, AirDtos.Maps()[0], 2, AirGameDtos.BlindPickConfigId, "", "NONE"])!;
        sent.Add(("gameService.createPracticeGame", lobby));
        sent.Add(("gameService.startChampionSelection", rpc.Route("start", "gameService", "startChampionSelection",
            [lobby["id"], lobby["optimisticLock"]])));
        sent.Add(("gameService.selectChampion", rpc.Route("start", "gameService", "selectChampion", [1])));
        _ = rpc.Route("start", "gameService", "championSelectCompleted", []);
        clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
        games.Tick();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (games.Reconnect(account.Id) is null)
        {
            await Task.Delay(100, deadline.Token);
            games.Tick();
        }
        sent.Add(("gameService.retrieveInProgressGameInfo", rpc.Route("start", "gameService", "retrieveInProgressGameInfo", [])));
        var guest = PlatformGames.Guest(account);
        foreach (var match in manager.List(guest).Where(g => g.State is not ("Stopped" or "Failed")))
            await manager.StopAsync(guest, match.Id);
        games.Tick();
    }

    /// <summary>
    /// The fixture: by alias, the 1.52 classes' writable fields (a var or a setter) and getter-only fields, and the externalized JSON
    /// classes' assignProps keys.
    /// </summary>
    internal sealed record Declared(Dictionary<string, HashSet<string>> Classes, Dictionary<string, HashSet<string>> ReadOnly,
        Dictionary<string, HashSet<string>> Json)
    {

    }

    /// <summary>
    /// Walks one sent value. A typed AMF object may name a field its class declares, writable or getter-only: the AMF decoder meets a
    /// getter-only one on the 126 line already (GameQueueConfig.name, getter-only in 1.45 and 1.52, is in every queue list the 126 line's
    /// AirDtos.QueueCatalog sends). Inside an externalized JSON body an object is copied by AS3 assignment (copyProps,
    /// ClientSystemStatesNotification.as:106, 121; BroadcastNotification.as:43-59), which cannot write a getter-only field, so there only
    /// the writable ones pass (batch 794: BroadcastMessage.isOnLoginDataPacket).
    /// </summary>
    internal static void Audit(object? value, string path, Declared declared, List<string> bad, HashSet<string> seen, bool copied = false)
    {
        switch (value)
        {
            case AirJsonObject json:
                seen.Add(json.TypeName);
                if (!declared.Json.TryGetValue(json.TypeName, out var keys))
                    bad.Add($"{path}: {json.TypeName} is no externalized JSON class of 1.52");
                foreach (var (key, field) in json)
                {
                    if (keys is not null && !keys.Contains(key)) bad.Add($"{path}: {json.TypeName}'s assignProps reads no '{key}'");
                    Audit(field, path + "." + key, declared, bad, seen, copied: true);
                }
                break;
            case AsObject typed when typed.IsTyped:
                seen.Add(typed.TypeName);
                if (!declared.Classes.TryGetValue(typed.TypeName, out var fields))
                    bad.Add($"{path}: alias {typed.TypeName} is not registered by 1.52");
                var getterOnly = declared.ReadOnly.GetValueOrDefault(typed.TypeName) ?? [];
                foreach (var (key, field) in typed)
                {
                    if (fields is not null && !fields.Contains(key) && (copied || !getterOnly.Contains(key)))
                        bad.Add($"{path}: {typed.TypeName} declares no {(getterOnly.Contains(key) ? "writable " : "")}'{key}'");
                    Audit(field, path + "." + key, declared, bad, seen, copied);
                }
                break;
            case AsObject anonymous:
                foreach (var (key, field) in anonymous) Audit(field, path + "." + key, declared, bad, seen, copied);
                break;
            case System.Collections.IEnumerable items and not string and not byte[]:
                var index = 0;
                foreach (var item in items) Audit(item, $"{path}[{index++}]", declared, bad, seen, copied);
                break;
        }
    }

    /// <summary>A two-human Draft Mode lobby (A captains team one, B team two) in its ban phase; returns the game id.</summary>
    private static int DraftLobby(AirPartyQueueChecks.Rig rig, PlatformAccount a, PlatformAccount b)
    {
        var created = (AsObject)rig.Games.Create(rig.Accounts.Get(a.Id), "Draft 132 " + a.Summoner, "", GameMaps.Normal, 2,
            AirGameDtos.DraftConfigId, GameMaps.ClassicMode, "NONE");
        var id = Convert.ToInt32(created["id"]);
        rig.Games.Join(rig.Accounts.Get(b.Id), id, "");
        rig.Games.Start(a.Id, id, Convert.ToInt64(((AsObject)rig.Games.Get(a.Id, id))["optimisticLock"]));
        TestAssert.Equal("PRE_CHAMP_SELECT", ((AsObject)rig.Games.Get(a.Id, id))["gameState"]);
        return id;
    }

    /// <summary>A blind-pick custom game on the map, started: the host and the seat are in champion select.</summary>
    private static void Start(AirPartyQueueChecks.Rig rig, PlatformAccount host, PlatformAccount seat, int mapId)
    {
        var created = (AsObject)rig.Games.Create(rig.Accounts.Get(host.Id), $"Pool {mapId} {seat.Summoner}", "", mapId, mapId == GameMaps.CrystalScar ? 10 : 2,
            AirGameDtos.BlindPickConfigId, GameMaps.ModeOf(mapId), "NONE");
        var id = Convert.ToInt32(created["id"]);
        rig.Games.Join(rig.Accounts.Get(seat.Id), id, "");
        rig.Games.Start(host.Id, id, Convert.ToInt64(((AsObject)rig.Games.Get(host.Id, id))["optimisticLock"]));
    }

    /// <summary>The spells the GameDTO sends for one account (accountSpellsMap: [accountId, spell1, spell2] per member).</summary>
    private static (int First, int Second) Seat(AirPartyQueueChecks.Rig rig, long account)
    {
        var flat = ((ArrayCollection)((AsObject)rig.Games.Current(account)!)["accountSpellsMap"]).Cast<object>().Select(value => Convert.ToInt64(value)).ToArray();
        for (var index = 0; index + 2 < flat.Length; index += 3)
            if (flat[index] == account) return ((int)flat[index + 1], (int)flat[index + 2]);
        throw new InvalidOperationException($"the game DTO sends no spells for account {account}");
    }

    private static int PickMode(AsObject game, PlatformAccount account) =>
        (int)((ArrayCollection)game["teamOne"]).Cast<AsObject>().Concat(((ArrayCollection)game["teamTwo"]).Cast<AsObject>())
            .Single(p => p.TypeName == AirProtocolNames.PlayerParticipant && Equals(p["accountId"], (double)account.Id))["pickMode"];

    private static AirFault Expect(Action action)
    {
        try { action(); }
        catch (AirFault fault) { return fault; }
        throw new InvalidOperationException("The call was expected to be refused with a typed platform exception.");
    }
}
