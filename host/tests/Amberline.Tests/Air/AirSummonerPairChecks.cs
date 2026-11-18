using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 860 (owner report, 17 November 2026: a player had two blank summoner spells and then could not enter the game): a seat never holds,
/// and a game never launches with, a pair the map, the summoner's level or the game server refuses, and a saved default that no map seats is
/// repaired. Both lines.
/// </summary>
public static class AirSummonerPairChecks
{
    [Check("server.air-summoner-pair-unknown-saved-spell-seats-the-default")]
    public static void UnknownSavedSpellSeatsTheDefault() => UnknownAsync().GetAwaiter().GetResult();

    [Check("server.air-summoner-pair-repeated-spell-launches-with-the-default")]
    public static void RepeatedSpellLaunchesWithTheDefault() => RepeatedAsync().GetAwaiter().GetResult();

    [Check("server.air-summoner-pair-saved-pair-no-map-seats-is-repaired")]
    public static void SavedPairNoMapSeatsIsRepaired()
    {
        var root = TemporaryDirectory("league-air-summoner-pair-saved");
        try
        {
            long unknown, twice, locked, teleport;
            using (var seeded = new PlatformAccounts(root))
            {
                unknown = seeded.RegisterPublic("pairsavedunknown", "testing-password", "pairsavedunknown").Id;
                twice = seeded.RegisterPublic("pairsavedtwice", "testing-password", "pairsavedtwice").Id;
                locked = seeded.RegisterPublic("pairsavedlocked", "testing-password", "pairsavedlocked", maxed: false).Id;
                teleport = seeded.RegisterPublic("pairsavedteleport", "testing-password", "pairsavedteleport", maxed: false).Id;
            }
            // What a store from another build or a hand edit can hold: Stifle (never in either line's pool), Heal twice, Flash and Ignite
            // at level one (they unlock at 12 and 7), and Teleport with Heal at level one (Summoner's Rift seats it, the Crystal Scar does not).
            var saved = new Dictionary<long, (string, string)>
            {
                [unknown] = ("SummonerStifle", "SummonerHeal"), [twice] = ("SummonerHeal", "SummonerHeal"),
                [locked] = ("SummonerFlash", "SummonerDot"), [teleport] = ("SummonerTeleport", "SummonerHeal"),
            };
            var path = Path.Combine(root, "accounts.json");
            var state = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(path), MatchRoster.JsonOptions)!;
            File.WriteAllText(path, JsonSerializer.Serialize(state with
            {
                Accounts = state.Accounts.Select(a => a with { Loadout = a.Loadout with
                { SummonerSpell1 = saved[a.Id].Item1, SummonerSpell2 = saved[a.Id].Item2 } }).ToArray(),
            }, MatchRoster.JsonOptions));

            using (var store = new PlatformAccounts(root))
            {
                TestAssert.Equal(("SummonerFlash", "SummonerDot"), Pair(store.Get(unknown)), "an unknown saved spell becomes the level-30 default");
                TestAssert.Equal(("SummonerFlash", "SummonerDot"), Pair(store.Get(twice)), "a repeated saved spell becomes the default");
                TestAssert.Equal(("SummonerHeal", "SummonerHaste"), Pair(store.Get(locked)), "a level-one summoner's locked pair becomes Heal and Ghost");
                TestAssert.Equal(("SummonerTeleport", "SummonerHeal"), Pair(store.Get(teleport)), "a pair one map seats is kept");
                // The old pair failed every later save of the loadout (ValidateSpellUnlocks); the repaired one does not.
                var account = store.Get(locked);
                store.SelectRunePage(locked, account.SummonerId, PlatformAccounts.RunePagesFor(account).Single(page => page.IsCurrent).PageId);
            }
            var kept = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(path), MatchRoster.JsonOptions)!;
            TestAssert.Equal(("SummonerHeal", "SummonerHaste"), Pair(kept.Accounts.Single(a => a.Id == locked)), "the store keeps the repair");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-summoner-pair-offered-spells-load-on-the-game-server")]
    public static void OfferedSpellsLoadOnTheGameServer()
    {
        var shipped = GameData.Default.SpellInibinNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var map in new[] { GameMaps.Normal, GameMaps.Tutorial, GameMaps.TwistedTreeline, GameMaps.CrystalScar })
        {
            foreach (var name in AirGameDtos.SpellNamesFor(map).Values)
            {
                TestAssert.Equal(name, SummonerLoadout.TryCanonicalFromDisplay(name), $"map {map}: the game server's roster reads {name} as itself");
                TestAssert.True(shipped.Contains(name), $"map {map}: {name}.inibin ships under the game data's Spells");
            }
            foreach (var level in new[] { 1, 30 })
            {
                var (first, second) = AirGameDtos.DefaultSpells(map, level);
                TestAssert.True(AirGameDtos.PairUsable(first, second, map, level), $"map {map}: the level-{level} default pair is seatable");
            }
        }
    }

    private static async Task UnknownAsync()
    {
        var root = TemporaryDirectory("league-air-summoner-pair-unknown");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            var host = rig.SignIn("pairunknownhost");
            var account = rig.Accounts.Get(host.Id);
            rig.Games.Create(account with { Loadout = account.Loadout with { SummonerSpell1 = "SummonerStifle" } }, "Pair unknown", "",
                GameMaps.Normal, 10, AirGameDtos.BlindPickConfigId, GameMaps.ClassicMode, "NONE");
            TestAssert.Equal(AirGameDtos.DefaultSpells(GameMaps.Normal, 30), Seat(rig, host.Id), "an unknown saved spell seats the default pair");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task RepeatedAsync()
    {
        var root = TemporaryDirectory("league-air-summoner-pair-repeated");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            var host = rig.SignIn("pairrepeatedhost");
            var account = rig.Accounts.Get(host.Id);
            var created = (AsObject)rig.Games.Create(account with { Loadout = account.Loadout with { SummonerSpell2 = "SummonerFlash" } },
                "Pair repeated", "", GameMaps.Normal, 10, AirGameDtos.BlindPickConfigId, GameMaps.ClassicMode, "NONE");
            TestAssert.Equal(AirGameDtos.DefaultSpells(GameMaps.Normal, 30), Seat(rig, host.Id), "Flash twice seats the default pair");
            var id = Convert.ToInt32(created["id"]);
            rig.Games.Start(host.Id, id, Convert.ToInt64(created["optimisticLock"]));
            // The client's own route: selectSpells refuses the same spell twice and an id outside the catalogue (15: no spell in either
            // line) and keeps the previous pick.
            var before = (Seat(rig, host.Id), Pair(rig.Accounts.Get(host.Id)));
            TestAssert.Throws<ArgumentException>(() => rig.Games.SelectSpells(host.Id, 4, 4), "a pick of Flash twice is refused");
            TestAssert.Throws<ArgumentException>(() => rig.Games.SelectSpells(host.Id, 15, 4), "a pick of an unknown id is refused");
            TestAssert.Equal(before, (Seat(rig, host.Id), Pair(rig.Accounts.Get(host.Id))), "a refused pick changes neither the seat nor the save");
            rig.Games.Pick(host.Id, 1);
            rig.Games.LockIn(host.Id);
            rig.Clock.Now = rig.Clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            rig.Games.Tick();
            TestAssert.Equal("START_REQUESTED", ((AsObject)rig.Games.Get(host.Id, id))["gameState"], "the game launches");
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(("SummonerFlash", "SummonerDot"), (roster[0].SummonerSpell1, roster[0].SummonerSpell2), "the roster holds two different spells");
        }
        finally { DeleteDirectory(root); }
    }

    private static (string, string) Pair(PlatformAccount account) => (account.Loadout.SummonerSpell1, account.Loadout.SummonerSpell2);

    /// <summary>The spells the game DTO sends for one account: accountSpellsMap is flattened, [accountId, spell1, spell2] per member.</summary>
    private static (int, int) Seat(AirPartyQueueChecks.Rig rig, long account)
    {
        var flat = ((ArrayCollection)((AsObject)rig.Games.Current(account)!)["accountSpellsMap"]).Cast<object>().Select(v => (long)(double)v).ToArray();
        for (var index = 0; index + 2 < flat.Length; index += 3)
            if (flat[index] == account) return ((int)flat[index + 1], (int)flat[index + 2]);
        throw new InvalidOperationException($"the game DTO sends no spells for account {account}");
    }
}
