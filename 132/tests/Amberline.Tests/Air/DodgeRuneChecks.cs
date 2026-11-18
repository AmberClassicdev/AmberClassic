using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 861: 1.0.0.132 removed the dodge runes (wiki V1.0.0.132 "Dodge global stat removal."; wiki Dodge "Players were refunded the spent
/// IP when they were removed in V1.0.0.132."). The 1.0.0.126 line keeps its own copy of this file, which pins that nothing changes there.
/// </summary>
public static class DodgeRuneChecks
{
    // Evasion (rFlatDodgeMod) and Elusion (rFlatDodgeModPerLevel) in every colour and tier: the shipped records that carry a dodge stat.
    private static readonly int[] Dodge =
        [5019, 5020, 5049, 5050, 5079, 5080, 5109, 5110, 5141, 5142, 5171, 5172, 5201, 5202, 5231, 5232, 5263, 5264, 5293, 5294, 5323, 5324, 5353, 5354];

    [Check("server.air132-dodge-runes-retired")]
    public static void DodgeRunesAreNoLongerSoldGrantedOrEquipped()
    {
        var catalog = new RuneCatalog();
        TestAssert.Equal(Dodge, Enumerable.Range(5001, 374).Where(catalog.IsRetired).ToArray(), "exactly the dodge runes are retired");
        TestAssert.Equal(new[] { 8004, 8010, 8018, 8029 }, Enumerable.Range(8001, 34).Where(catalog.IsRetired).ToArray(), "and the event dodge runes");
        TestAssert.Equal(350, catalog.All.Count);
        TestAssert.True(Dodge.All(id => !catalog.IsRegular(id) && catalog.Contains(id) && catalog.Get(id) is not null),
            "a retired record still resolves (the game and the bots read it)");
        // Retail's bots kept their dodge runes until V1.0.0.152 (wiki Dodge); a bot page still reads them.
        TestAssert.Equal(1, RunePage.CreateBot(catalog, [5079]).Yellow.Count);

        var root = TemporaryDirectory("league-air132-dodge-retired");
        try
        {
            using var accounts = new PlatformAccounts(root, root);
            var buyer = accounts.RegisterPublic("dodgebuyer", "testing-password", "Dodge Buyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 10_000, 0, Guid.NewGuid());
            foreach (var id in Dodge)
            {
                TestAssert.False(accounts.StoreCatalog.TryGet("rune:" + id, out _), $"rune {id} is no store product");
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(buyer.Id, "rune:" + id, "IP", Guid.NewGuid()));
            }
            TestAssert.Equal(10_000L, PlatformEconomy.For(accounts.Get(buyer.Id)).Ip, "nothing was charged");

            // Not even an account that owns everything owns or equips one.
            var maxed = accounts.RegisterPublic("dodgemaxed", "testing-password", "Dodge Maxed");
            var ids = ((ArrayCollection)AirDtos.RuneInventory(accounts.Get(maxed.Id))["summonerRunes"])
                .Cast<AsObject>().Select(rune => Convert.ToInt32(rune["runeId"])).ToArray();
            TestAssert.False(ids.Intersect(Dodge).Any(), "the full inventory lists no dodge rune");
            var pages = accounts.GetRunePages(maxed.Id);
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(maxed.Id, maxed.SummonerId,
                AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [10] = 5323 } })));
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air132-dodge-runes-refunded-and-removed-once")]
    public static void TheMigrationRefundsTheSpentIpCleansEveryPageAndRunsOnce()
    {
        var root = TemporaryDirectory("league-air132-dodge-migration");
        try
        {
            var start = new DateTimeOffset(2012, 1, 10, 12, 0, 0, TimeSpan.Zero);
            long owner, maxed, clean, rich;
            using (var accounts = new PlatformAccounts(root, root))
            {
                owner = accounts.RegisterPublic("dodgeowner", "testing-password", "Dodge Owner", maxed: false).Id;
                maxed = accounts.RegisterPublic("dodgefull", "testing-password", "Dodge Full").Id;
                clean = accounts.RegisterPublic("dodgeclean", "testing-password", "Dodge Clean", maxed: false).Id;
                rich = accounts.RegisterPublic("dodgerich", "testing-password", "Dodge Rich", maxed: false).Id;
            }
            static PlatformPurchase Receipt(int rune, int quantity, long price, DateTimeOffset at) =>
                new(Guid.NewGuid(), "rune:" + rune, "IP", price, at, quantity);
            Edit(root, account =>
                // Three Lesser Seals of Evasion (two on a receipt at 65 IP, one from the combiner) and a Greater Quintessence of Evasion.
                account.Id == owner ? Economy(account, 30, 1_000, new() { [5001] = 1, [5079] = 3, [5353] = 1 },
                    [Receipt(5001, 1, 15, start), Receipt(5079, 2, 130, start.AddDays(1)), Receipt(5353, 1, 2050, start.AddDays(2))],
                    Pages(account, new() { [1] = 5001, [10] = 5079, [11] = 5079, [12] = 5079, [28] = 5353 }, new() { [10] = 5079 }),
                    account.Loadout with { RedRunes = [5001], YellowRunes = [5079, 5079, 5079], BlackRunes = [5353] })
                // A full account owns everything and bought nothing: a Lesser Mark of Evasion on its first page, a Glyph on its second.
                : account.Id == maxed ? account with
                {
                    RunePages = Pages(account, new() { [1] = 5019, [2] = 5001 }, new() { [19] = 5049 }),
                    Loadout = account.Loadout with { RedRunes = [5019, 5001] },
                }
                : account.Id == clean ? Economy(account, 30, 500, new() { [5001] = 1 }, [Receipt(5001, 1, 15, start)],
                    Pages(account, new() { [1] = 5001 }, []), account.Loadout with { RedRunes = [5001] })
                : Economy(account, 30, PlatformEconomy.MaxBalance - 10, new() { [5019] = 1 }, [Receipt(5019, 1, 65, start)],
                    Pages(account, new() { [1] = 5019 }, []), account.Loadout with { RedRunes = [5019] }));
            var store = Path.Combine(root, "accounts.json");
            var before = File.ReadAllBytes(store);
            var cleanBefore = Json(Read(root).Single(account => account.Id == clean));
            // The copy without a receipt is refunded at the price list's price (the store's tier price under the neutral list).
            var combinerCopy = PlatformStorePrices.Default.Rune(5079)?.Ip ?? 15;

            using (var accounts = new PlatformAccounts(root, root))
            {
                TestAssert.Equal(3, accounts.RetireRunes(), "the three accounts holding a dodge rune change");
                var after = File.ReadAllBytes(store);
                TestAssert.Equal(0, accounts.RetireRunes(), "a second run finds nothing");
                TestAssert.True(after.AsSpan().SequenceEqual(File.ReadAllBytes(store)), "and leaves the store byte for byte");

                var backups = Directory.GetFiles(root, "accounts.before-retired-runes-*.json");
                TestAssert.Equal(1, backups.Length, "one backup, written before the change");
                TestAssert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(backups[0])), "the backup is the store as it was");

                var paid = PlatformEconomy.For(accounts.Get(owner));
                TestAssert.Equal(1_000L + 130 + combinerCopy + 2050, paid.Ip, "the spent IP comes back");
                TestAssert.Equal(new Dictionary<int, int> { [5001] = 1 }, paid.OwnedRunes!, "the dodge runes are gone");
                TestAssert.Equal(3, paid.Purchases!.Length, "the receipts stay");
                var ownerPages = accounts.GetRunePages(owner);
                TestAssert.Equal(new Dictionary<int, int> { [1] = 5001 }, ownerPages[0].Slots);
                TestAssert.Equal(0, ownerPages[1].Slots.Count);
                var loadout = accounts.Get(owner).Loadout;
                TestAssert.Equal(new[] { 5001 }, loadout.RedRunes!, "the loadout keeps the current page's runes");
                TestAssert.Equal(0, loadout.YellowRunes!.Length + loadout.BlackRunes!.Length);

                var full = accounts.Get(maxed);
                TestAssert.True(full.Economy is null, "a full account bought nothing and gets no IP");
                TestAssert.Equal(new Dictionary<int, int> { [2] = 5001 }, full.RunePages![0].Slots);
                TestAssert.Equal(0, full.RunePages[1].Slots.Count);
                TestAssert.Equal(new[] { 5001 }, full.Loadout.RedRunes!);

                TestAssert.Equal(PlatformEconomy.MaxBalance, PlatformEconomy.For(accounts.Get(rich)).Ip, "the refund stops at the balance cap");
                TestAssert.Equal(cleanBefore, Json(accounts.Get(clean)), "an account without dodge runes is untouched");

                // The pages stay valid: they save and switch as they are, and a dodge rune is refused.
                accounts.SaveRunePages(owner, accounts.Get(owner).SummonerId, ownerPages);
                accounts.SelectRunePage(owner, accounts.Get(owner).SummonerId, ownerPages[1].PageId);
                accounts.SelectRunePage(maxed, full.SummonerId, full.RunePages[1].PageId);
                accounts.SaveRunePages(maxed, full.SummonerId, accounts.GetRunePages(maxed));
                TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(owner, accounts.Get(owner).SummonerId,
                    AirRuneAccountChecks.Book(ownerPages, ownerPages[0] with { Slots = new Dictionary<int, int> { [10] = 5079 } })));
            }
            using (var accounts = new PlatformAccounts(root, root))
                TestAssert.Equal(0, accounts.RetireRunes(), "the next start finds nothing either");
            TestAssert.Equal(1, Directory.GetFiles(root, "accounts.before-retired-runes-*.json").Length, "and writes no new backup");
        }
        finally { DeleteDirectory(root); }
    }

    internal static PlatformAccount Economy(PlatformAccount account, int level, long ip, Dictionary<int, int> owned, PlatformPurchase[] receipts,
        PlatformRunePage[] pages, MatchPlayer loadout) => account with
    {
        Loadout = loadout with { SummonerLevel = level },
        RunePages = pages,
        Economy = account.Economy! with { Level = level, Ip = ip, OwnedRunes = owned, Purchases = receipts },
    };

    // The account's first two pages with these slots (the first stays current), the rest as they are.
    internal static PlatformRunePage[] Pages(PlatformAccount account, Dictionary<int, int> first, Dictionary<int, int> second) =>
        [.. account.RunePages!.Select(page => page.PageId switch { 1 => page with { Slots = first }, 2 => page with { Slots = second }, _ => page })];

    internal static void Edit(string root, Func<PlatformAccount, PlatformAccount> change)
    {
        var path = Path.Combine(root, "accounts.json");
        var state = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(path), MatchRoster.JsonOptions)!;
        File.WriteAllText(path, JsonSerializer.Serialize(state with { Accounts = [.. state.Accounts.Select(change)] }, MatchRoster.JsonOptions));
    }

    private static PlatformAccount[] Read(string root) =>
        JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(Path.Combine(root, "accounts.json")), MatchRoster.JsonOptions)!.Accounts;

    private static string Json(PlatformAccount account) => JsonSerializer.Serialize(account, MatchRoster.JsonOptions);

    private static string TemporaryDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
