using System.Text.Json;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 865: the store repairs that run at a start or a read (batch 861's removal of the retired runes, batch 860's repair of a saved
/// summoner pair) never stop the start, a read or another account's read, whatever field a store written by hand or by another build lacks,
/// and a refused write leaves the store as it was. Both lines: the dodge runes are retired on 1.0.0.132 only, so 1.0.0.126 expects no change.
/// </summary>
public static class AccountRepairChecks
{
    [Check("server.air-retired-runes-removal-survives-missing-fields-and-a-refused-write")]
    public static void RetiredRunesRemovalSurvivesMissingFields()
    {
        var retired = new RuneCatalog().IsRetired(5079);
        var root = TemporaryDirectory("league-air-retired-runes-missing-fields");
        try
        {
            long bare, holes, good;
            using (var seeded = new PlatformAccounts(root, root))
            {
                bare = seeded.RegisterPublic("repairbare", "testing-password", "repairbare", maxed: false).Id;
                holes = seeded.RegisterPublic("repairholes", "testing-password", "repairholes", maxed: false).Id;
                good = seeded.RegisterPublic("repairgood", "testing-password", "repairgood", maxed: false).Id;
            }
            var store = Path.Combine(root, "accounts.json");
            // No loadout; a null page, a page without slots and a loadout without rune groups.
            Edit(store, account => account.Id == bare ? account with { Loadout = null! }
                : account.Id == holes ? account with
                {
                    RunePages = [null!, account.RunePages![0] with { Slots = null! }],
                    Loadout = account.Loadout with { RedRunes = null, YellowRunes = null, BlueRunes = null, BlackRunes = null },
                }
                : account);
            var before = File.ReadAllBytes(store);
            using (var accounts = new PlatformAccounts(root, root))
            {
                var log = new StringWriter();
                TestAssert.Equal(0, accounts.RetireRunes(log), "without a retired rune nothing changes");
                TestAssert.Equal("", log.ToString(), "and nothing is logged");
            }
            TestAssert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(store)), "the store stays byte for byte");
            TestAssert.Equal(0, Directory.GetFiles(root, "accounts.before-retired-runes-*.json").Length, "and no backup is written");

            // The same holes beside a Lesser Seal of Evasion each: one bought at 65 IP and slotted on a page, one without a receipt.
            Edit(store, account => account.Id == good ? account : account with
            {
                Economy = account.Economy! with
                {
                    Ip = 0, OwnedRunes = new() { [5079] = 1 },
                    Purchases = account.Id == holes ? [new(Guid.NewGuid(), "rune:5079", "IP", 65, DateTimeOffset.UnixEpoch, 1)] : [],
                },
                RunePages = account.Id == holes
                    ? [.. account.RunePages!, new PlatformRunePage(9, "Dodge", false, new() { [10] = 5079 }, DateTime.UnixEpoch)]
                    : account.RunePages,
            });
            var price = PlatformStorePrices.Default.Rune(5079)?.Ip ?? 15;
            using (var accounts = new PlatformAccounts(root, root))
            {
                TestAssert.Equal(retired ? 2 : 0, accounts.RetireRunes(TextWriter.Null), "both accounts change where the rune is retired");
                var paid = accounts.Get(holes);
                TestAssert.Equal(retired ? 65L : 0L, PlatformEconomy.For(paid).Ip, "the receipt's IP comes back");
                TestAssert.Equal(retired ? 0 : 1, PlatformEconomy.For(paid).OwnedRunes?.GetValueOrDefault(5079) ?? 0);
                TestAssert.True(paid.RunePages![0] is null && paid.RunePages[1].Slots is null, "a null page and a page without slots stay");
                TestAssert.Equal(retired ? 0 : 1, paid.RunePages[2].Slots.Count, "the rune leaves the page that held it");
                TestAssert.True(paid.Loadout.RedRunes is null, "a loadout without rune groups keeps none");
                var noLoadout = accounts.Get(bare);
                TestAssert.True(noLoadout.Loadout is null, "an account without a loadout reads and keeps none");
                TestAssert.Equal(retired ? (long)price : 0L, PlatformEconomy.For(noLoadout).Ip, "a copy without a receipt is refunded at the list price");
                TestAssert.True(accounts.Get(good).Loadout is not null, "the account beside them reads");
            }

            // A refused copy or write (the store held open) changes nothing and logs one line; the start goes on.
            Edit(store, account => account.Id == good ? account with { Economy = account.Economy! with { OwnedRunes = new() { [5079] = 1 } } } : account);
            before = File.ReadAllBytes(store);
            using (var accounts = new PlatformAccounts(root, root))
            {
                var log = new StringWriter();
                using (new FileStream(store, FileMode.Open, FileAccess.Read, FileShare.None))
                    TestAssert.Equal(0, accounts.RetireRunes(log), "a refused write changes no account");
                TestAssert.Equal(retired ? 1 : 0, log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, "one line is logged: " + log);
                TestAssert.Equal(1, PlatformEconomy.For(accounts.Get(good)).OwnedRunes![5079], "the rune stays until a later start");
            }
            TestAssert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(store)), "the store is untouched");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-summoner-pair-repair-survives-missing-fields-and-a-refused-write")]
    public static void SummonerPairRepairSurvivesMissingFields()
    {
        var root = TemporaryDirectory("league-air-summoner-pair-missing-fields");
        try
        {
            long bare, blank, refused, zero, good;
            using (var seeded = new PlatformAccounts(root))
            {
                bare = seeded.RegisterPublic("pairbare", "testing-password", "pairbare", maxed: false).Id;
                blank = seeded.RegisterPublic("pairblank", "testing-password", "pairblank", maxed: false).Id;
                refused = seeded.RegisterPublic("pairrefused", "testing-password", "pairrefused", maxed: false).Id;
                zero = seeded.RegisterPublic("pairzero", "testing-password", "pairzero", maxed: false).Id;
                good = seeded.RegisterPublic("pairgood", "testing-password", "pairgood", maxed: false).Id;
            }
            var store = Path.Combine(root, "accounts.json");
            // No loadout; no spell names; Stifle (in no pool); level 0, where not even the default pair is usable.
            Edit(store, account =>
                account.Id == bare ? account with { Loadout = null! }
                : account.Id == blank ? account with { Loadout = account.Loadout with { SummonerSpell1 = null!, SummonerSpell2 = null! } }
                : account.Id == refused ? account with { Loadout = account.Loadout with { SummonerSpell1 = "SummonerStifle", SummonerSpell2 = "SummonerHeal" } }
                : account.Id == zero ? account with { Loadout = account.Loadout with
                    { SummonerLevel = 0, SummonerSpell1 = "SummonerFlash", SummonerSpell2 = "SummonerDot" } }
                : account);
            var heal = ("SummonerHeal", "SummonerHaste");
            using (var accounts = new PlatformAccounts(root))
            {
                TestAssert.True(accounts.Get(bare).Loadout is null && accounts.Find(bare)!.Loadout is null, "an account without a loadout reads");
                TestAssert.Equal(heal, Pair(accounts.Get(blank)), "missing spell names become the level-one default");
                using (new FileStream(store, FileMode.Open, FileAccess.Read, FileShare.None))
                    TestAssert.Equal(heal, Pair(accounts.Get(refused)), "a refused write still reads the repaired pair");
                TestAssert.Equal(("SummonerStifle", "SummonerHeal"), Pair(Read(store, refused)), "the refused repair is not stored");
                TestAssert.Equal(heal, Pair(accounts.Get(refused)), "the next read repairs it again");
                TestAssert.Equal(heal, Pair(Read(store, refused)), "and stores it");
                TestAssert.Equal(heal, Pair(accounts.Get(zero)), "level 0 gets the default once");
                var written = File.GetLastWriteTimeUtc(store);
                accounts.Get(zero);
                TestAssert.Equal(written, File.GetLastWriteTimeUtc(store), "and the store is not written again at the next read");
                TestAssert.True(accounts.Get(good).Loadout is not null, "the account beside them reads");
            }
        }
        finally { DeleteDirectory(root); }
    }

    private static (string, string) Pair(PlatformAccount account) => (account.Loadout.SummonerSpell1, account.Loadout.SummonerSpell2);

    private static PlatformAccount Read(string store, long id) =>
        JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(store), MatchRoster.JsonOptions)!.Accounts.Single(a => a.Id == id);

    private static void Edit(string store, Func<PlatformAccount, PlatformAccount> change)
    {
        var state = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(store), MatchRoster.JsonOptions)!;
        File.WriteAllText(store, JsonSerializer.Serialize(state with { Accounts = [.. state.Accounts.Select(change)] }, MatchRoster.JsonOptions));
    }
}
