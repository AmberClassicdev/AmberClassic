using Amberline.Data;

namespace Amberline.Platform;

public sealed partial class PlatformAccounts
{
    /// <summary>
    /// Batch 861: retail's removal of the dodge runes in 1.0.0.132 (<see cref="RuneCatalog.IsRetired"/>; the 1.0.0.132 Platform runs it at
    /// every start, the 1.0.0.126 one never does and retires nothing). Riot, 24 November 2011 (Morello, quoted by SurrenderAt20): "provide a
    /// full refund for all summoners that currently own Dodge runes" and "removing these runes from your runebooks"; wiki Dodge: "Players
    /// were refunded the spent IP when they were removed in V1.0.0.132." So every account loses its retired runes (owned copies, every
    /// page's slots, the loadout's runes) and gets back the IP it spent on the copies it owned: each copy at the price its IP receipt
    /// paid, the newest receipts first. Reconstruction: a copy no receipt covers (a combiner award) is refunded at the price list's IP price
    /// (the store's tier price under the neutral list), as retail refunded every owned dodge rune; the sources name no other rule. The balance stops at <see cref="PlatformEconomy.MaxBalance"/>.
    /// Receipts stay as they were. An account that owns everything (or a legacy one) bought nothing, so only its pages are cleaned.
    /// It changes the store only when an account holds a retired rune, so a second run changes nothing; before writing, it copies
    /// accounts.json beside itself (a new file each time, never over an earlier copy). Batch 865: any failure (missing rune records, a
    /// refused copy or write, anything else) logs one line and leaves the store as it was; the start goes on and the next start tries again.
    /// Fields a store may lack (no loadout, a null page or page slots) stay as they are. Returns the accounts it changed.
    /// </summary>
    public int RetireRunes(TextWriter? log = null)
    {
        List<(PlatformAccount Account, int Copies, long Ip)> changes;
        try
        {
            var catalog = RuneCatalogForCombining.Value;
            lock (_gate)
            {
                changes = [];
                var accounts = _state.Accounts.Select(account =>
                {
                    var retired = Retire(account, catalog, out var copies, out var ip);
                    if (!ReferenceEquals(retired, account)) changes.Add((account, copies, ip));
                    return retired;
                }).ToArray();
                if (changes.Count == 0) return 0;
                File.Copy(_path, Path.Combine(Path.GetDirectoryName(_path)!,
                    $"accounts.before-retired-runes-{_time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"), overwrite: false);
                Save(_state with { Accounts = accounts });
            }
        }
        catch (Exception ex)
        {
            log?.WriteLine($"Amberline could not remove the retired runes from the accounts; it tries again at the next start: {ex.Message}");
            return 0;
        }
        foreach (var (account, copies, ip) in changes)
            log?.WriteLine($"Retired runes removed from {account.Summoner}: {copies} owned copies, {ip} IP refunded.");
        return changes.Count;
    }

    /// <summary>The account without its retired runes and with their refund; the same instance when it holds none.</summary>
    private static PlatformAccount Retire(PlatformAccount account, RuneCatalog catalog, out int copies, out long ip)
    {
        copies = 0;
        ip = 0;
        var owned = account.Economy?.OwnedRunes?.Where(pair => catalog.IsRetired(pair.Key)).ToArray() ?? [];
        // Batch 865: a store written by hand or by another build may lack the loadout, a page or a page's slots; those stay as they are.
        var loadout = account.Loadout;
        int[]?[] groups = [loadout?.RedRunes, loadout?.YellowRunes, loadout?.BlueRunes, loadout?.BlackRunes];
        if (owned.Length == 0 && !groups.Any(group => group?.Any(catalog.IsRetired) == true) &&
            account.RunePages?.Any(page => page?.Slots?.Values.Any(catalog.IsRetired) == true) != true)
            return account;

        int[]? Keep(int[]? ids) => ids?.Where(id => !catalog.IsRetired(id)).ToArray();
        var cleaned = account with
        {
            Loadout = loadout is null ? null! : loadout with
            {
                RedRunes = Keep(loadout.RedRunes), YellowRunes = Keep(loadout.YellowRunes),
                BlueRunes = Keep(loadout.BlueRunes), BlackRunes = Keep(loadout.BlackRunes),
            },
            RunePages = account.RunePages?.Select(page => page?.Slots is null ? page! : page with
            {
                Slots = page.Slots.Where(pair => !catalog.IsRetired(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value),
            }).ToArray(),
        };
        if (owned.Length == 0) return cleaned;

        var economy = account.Economy!;
        foreach (var (id, count) in owned)
        {
            copies += count;
            var left = count;
            // Batch 865: a receipt without a positive quantity (the store's load refuses one) refunds nothing itself; its copies fall to the price list.
            foreach (var receipt in (economy.Purchases ?? [])
                         .Where(purchase => purchase.Quantity > 0 && purchase.Currency == "IP" &&
                             purchase.ItemId.Equals($"rune:{id}", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(purchase => purchase.PurchasedAt))
            {
                var take = Math.Min(left, receipt.Quantity);
                ip += receipt.Price / receipt.Quantity * take;
                left -= take;
                if (left == 0) break;
            }
            ip += (long)left * (PlatformStorePrices.Default.Rune(id)?.Ip ?? PlatformStoreCatalog.TierIp(catalog.Get(id)!.Tier));
        }
        return cleaned with
        {
            Economy = economy with
            {
                Ip = Math.Min(PlatformEconomy.MaxBalance, economy.Ip + ip),
                OwnedRunes = economy.OwnedRunes!.Where(pair => !catalog.IsRetired(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            },
        };
    }
}
