using System.Text.Json;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 861: 1.0.0.126 (October 2011) still sold the dodge runes; 1.0.0.132 removed and refunded them (that line's copy of this file). Here
/// nothing is retired and the removal, which this line's Platform never runs, changes nothing.
/// </summary>
public static class DodgeRuneChecks
{
    [Check("server.air126-dodge-runes-kept")]
    public static void DodgeRunesStaySoldAndOwned()
    {
        var catalog = new RuneCatalog();
        TestAssert.False(Enumerable.Range(5001, 374).Concat(Enumerable.Range(8001, 34)).Any(catalog.IsRetired), "no rune is retired");
        TestAssert.Equal(374, catalog.All.Count);
        TestAssert.True(catalog.IsRegular(5019) && catalog.IsRegular(5353), "the Evasion runes are regular runes");

        var root = Path.Combine(Path.GetTempPath(), "league-air126-dodge-kept-" + Guid.NewGuid().ToString("N"));
        try
        {
            long owner;
            using (var accounts = new PlatformAccounts(root, root))
            {
                owner = accounts.RegisterPublic("dodgeowner", "testing-password", "Dodge Owner", maxed: false).Id;
                TestAssert.True(accounts.StoreCatalog.TryGet("rune:5079", out _), "the store sells the Lesser Seal of Evasion");
            }
            var path = Path.Combine(root, "accounts.json");
            var state = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(path), MatchRoster.JsonOptions)!;
            File.WriteAllText(path, JsonSerializer.Serialize(state with
            {
                Accounts = [.. state.Accounts.Select(account => account with
                {
                    Loadout = account.Loadout with { SummonerLevel = 30, YellowRunes = [5079] },
                    RunePages = [.. account.RunePages!.Select(page => page.PageId == 1 ? page with { Slots = new() { [10] = 5079 } } : page)],
                    Economy = account.Economy! with
                    {
                        Level = 30, Ip = 100, OwnedRunes = new() { [5079] = 1 },
                        Purchases = [new(Guid.NewGuid(), "rune:5079", "IP", 65, DateTimeOffset.UnixEpoch, 1)],
                    },
                })],
            }, MatchRoster.JsonOptions));
            var before = File.ReadAllBytes(path);
            using (var accounts = new PlatformAccounts(root, root))
            {
                TestAssert.Equal(0, accounts.RetireRunes(), "nothing to remove on this line");
                TestAssert.Equal(1, PlatformEconomy.For(accounts.Get(owner)).OwnedRunes![5079]);
                TestAssert.Equal(100L, PlatformEconomy.For(accounts.Get(owner)).Ip);
            }
            TestAssert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "the store is untouched");
            TestAssert.Equal(0, Directory.GetFiles(root, "accounts.before-retired-runes-*.json").Length, "and no backup is written");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
